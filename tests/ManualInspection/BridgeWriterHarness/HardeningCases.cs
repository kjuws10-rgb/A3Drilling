using System.Diagnostics;
using System.Security.Cryptography;
using Drilling.Common.Review;
using Drilling.File.ReviewOffset;

internal static partial class Program
{
    private static async Task RunHardeningCases()
    {
        await CheckAsync("load_and_all_save_paths_return_actual_file_sha_and_precision", async () =>
        {
            var f = Fixture(); var a = await f.Store.SaveNew("QA", Rows(1.234567, -2.345678));
            var b = await f.Store.LoadLatest("QA");
            True(a.ContentSha256 == Hash(a.FilePath) && b.ContentSha256 == a.ContentSha256);
            Near(a.Rows[0].ReviewOffsetX, b.Rows[0].ReviewOffsetX, 1e-12);
            var c = await f.Store.SaveNewFromSource("QA", Rows(2.456789, -.123456), Source(f.Root, "precision"), false);
            var d = await f.Store.LoadLatest("QA");
            True(c.ContentSha256 == Hash(c.FilePath)); Near(c.Rows[0].ReviewOffsetX, d.Rows[0].ReviewOffsetX, 1e-12);
        });
        await CheckAsync("cas_same_path_replacement_rejects_stale_content_hash", async () =>
        {
            var f = Fixture(); var a = await f.Store.SaveNew("QA", Rows(1, 1)); var original = Hash(a.FilePath);
            var text = File.ReadAllText(a.FilePath); var time = File.GetLastWriteTimeUtc(a.FilePath);
            File.WriteAllText(a.FilePath, text.Replace("0.001000", "0.009000"), new System.Text.UTF8Encoding(true)); File.SetLastWriteTimeUtc(a.FilePath, time);
            await ThrowsAsync(() => f.Store.SaveNewIfUnchanged("QA", a.FilePath, Rows(2, 2), Source(f.Root, "same-path"), expectedLatestContentSha256: original));
            True(ActiveCount(a) == 1); Near((await f.Store.LoadLatest("QA")).Rows[0].ReviewOffsetX, .009);
        });
        await CheckAsync("shared_store_instances_allow_only_one_compare_and_save", async () =>
        {
            var f = Fixture(); var a = await f.Store.SaveNew("QA", Rows(0, 0));
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var slow = new CReviewOffsetFile(Path.Combine(f.Root, "Config"), () => { entered.Set(); True(release.Wait(TimeSpan.FromSeconds(5))); return FixedTime; });
            var peer = new CReviewOffsetFile(Path.Combine(f.Root, "Config"), () => FixedTime);
            async Task<bool> Write(CReviewOffsetFile store, string source, int value) { try { await store.SaveNewIfUnchanged("QA", a.FilePath, Rows(value, value), source); return true; } catch (InvalidOperationException) { return false; } }
            var first = Task.Run(() => Write(slow, Source(f.Root, "shared-slow"), 1));
            True(entered.Wait(TimeSpan.FromSeconds(3)));
            var second = Task.Run(() => Write(peer, Source(f.Root, "shared-peer"), 2));
            await Task.Delay(100); release.Set();
            var result = await Task.WhenAll(first, second); True(result.Count(p => p) == 1 && ActiveCount(a) == 2);
        });
        await CheckAsync("warm_history_cache_observes_other_store_commit", async () =>
        {
            var f = Fixture(); var source = Source(f.Root, "warm"); True(await f.Store.FindSourceApplication("QA", source) is null);
            var peer = new CReviewOffsetFile(Path.Combine(f.Root, "Config"), () => FixedTime);
            var saved = await peer.SaveNewFromSource("QA", Rows(1, 1), source, false);
            True(await f.Store.FindSourceApplication("QA", source) is not null);
            await ThrowsAsync(() => f.Store.SaveNewFromSource("QA", Rows(2, 2), source, false)); True(ActiveCount(saved) == 1);
        });
        await CheckAsync("history_same_length_and_time_replacement_invalidates_digest_cache", async () =>
        {
            var f = Fixture(); var a = Source(f.Root, "sameA"); var b = Source(f.Root, "sameB");
            await f.Store.SaveNewFromSource("QA", Rows(1, 1), a, false);
            var history = (await f.Store.FindSourceApplication("QA", a))!.HistoryFilePath;
            True(await f.Store.FindSourceApplication("QA", b) is null);
            var time = File.GetLastWriteTimeUtc(history); var length = new FileInfo(history).Length;
            File.WriteAllText(history, File.ReadAllText(history).Replace("sameA.csv", "sameB.csv"), new System.Text.UTF8Encoding(true)); File.SetLastWriteTimeUtc(history, time);
            True(new FileInfo(history).Length == length);
            True(await f.Store.FindSourceApplication("QA", b) is not null && await f.Store.FindSourceApplication("QA", a) is null);
        });
        await CheckAsync("backward_clock_save_is_the_new_active_revision", async () =>
        {
            var f = Fixture(); var future = new CReviewOffsetFile(Path.Combine(f.Root, "Config"), () => FixedTime.AddDays(2));
            var old = await future.SaveNew("QA", Rows(1, 1));
            var saved = await f.Store.SaveNew("QA", Rows(2, 2)); var actual = await f.Store.LoadLatest("QA");
            True(actual.FilePath == saved.FilePath && saved.SavedAt > old.SavedAt); Near(actual.Rows[0].ReviewOffsetX, .002);
        });
        await CheckAsync("submillisecond_clock_collision_advances_version", async () =>
        {
            var f = Fixture(); var first = await f.Store.SaveNew("QA", Rows(1, 1));
            var peer = new CReviewOffsetFile(Path.Combine(f.Root, "Config"), () => first.SavedAt.AddTicks(1));
            var second = await peer.SaveNew("QA", Rows(2, 2)); True((await f.Store.LoadLatest("QA")).FilePath == second.FilePath);
        });
        await CheckAsync("midnight_version_shift_keeps_history_and_rollback_evidence", async () =>
        {
            var f = Fixture(); var at = new DateTimeOffset(2026, 11, 20, 23, 59, 59, 999, DateTimeOffset.Now.Offset);
            var store = new CReviewOffsetFile(Path.Combine(f.Root, "Config"), () => at);
            var first = await store.SaveNew("QA", Rows(1, 1)); var source = Source(f.Root, "midnight");
            var second = await store.SaveNewFromSource("QA", Rows(2, 2), source, false);
            True(second.SavedAt.Date > first.SavedAt.Date);
            var app = (await store.FindSourceApplication("QA", source))!; True(app.AppliedAt == second.SavedAt);
            await store.RollbackLatest("QA", second.FilePath);
            True(await store.FindSourceApplication("QA", source) is null);
            True(await store.FindCommittedSourceApplication("QA", source) is not null);
            var fresh = new CReviewOffsetFile(Path.Combine(f.Root, "Config"));
            True(await fresh.FindCommittedSourceApplication("QA", source) is not null);
        });
        await CheckAsync("legacy_rollback_preserves_immutable_source_receipt_for_fresh_store", async () =>
        {
            var f = Fixture(); var source = Source(f.Root, "commit-proof");
            var saved = await f.Store.SaveNewFromSource("QA", Rows(1, 1), source, false);
            await f.Store.RollbackLatest("QA", saved.FilePath);
            var fresh = new CReviewOffsetFile(Path.Combine(f.Root, "Config"));
            True(await fresh.FindSourceApplication("QA", source) is null);
            var proof = await fresh.FindCommittedSourceApplication("QA", source);
            True(proof is not null && proof.OffsetFile == saved.FileName && File.Exists(proof.HistoryFilePath));
        });
        await CheckAsync("invalid_latest_name_cannot_report_a_nonactive_save_as_success", async () =>
        {
            var f = Fixture(); var first = await f.Store.SaveNew("QA", Rows(1, 1));
            File.Copy(first.FilePath, Path.Combine(Path.GetDirectoryName(first.FilePath)!, "Z_reviewoffset.csv"));
            await ThrowsAsync(() => f.Store.SaveNew("QA", Rows(2, 2))); True(ActiveCount(first) == 2);
        });
        await CheckAsync("history_publish_failure_preserves_previous_bytes_and_active_offset", async () =>
        {
            var f = Fixture(); var firstSource = Source(f.Root, "history-first");
            var first = await f.Store.SaveNewFromSource("QA", Rows(1, 1), firstSource, false);
            var history = (await f.Store.FindSourceApplication("QA", firstSource))!.HistoryFilePath;
            var originalBytes = File.ReadAllBytes(history);
            using (var reader = new FileStream(history, FileMode.Open, FileAccess.Read, FileShare.Read))
                await ThrowsAsync(() => f.Store.SaveNewFromSource("QA", Rows(2, 2), Source(f.Root, "history-next"), false));
            True(File.ReadAllBytes(history).SequenceEqual(originalBytes));
            True((await f.Store.LoadLatest("QA")).FilePath == first.FilePath && ActiveCount(first) == 1);
            True(!Directory.EnumerateFiles(Path.GetDirectoryName(first.FilePath)!, "*.tmp", SearchOption.AllDirectories).Any());
        });
        await CheckAsync("cooperating_process_lock_wait_is_cancellable", async () =>
        {
            var f = Fixture(); var path = Path.Combine(f.Root, ".reviewoffset.lock.tmp");
            using (var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                using var c = new CancellationTokenSource(120);
                var cancelled = false; try { await f.Store.LoadLatest("QA", c.Token); } catch (OperationCanceledException) { cancelled = true; }
                True(cancelled);
            }
            await f.Store.LoadLatest("QA"); True(!File.Exists(path));
        });
        await CheckAsync("another_process_waits_for_the_same_store_file_lease", async () =>
        {
            var f = Fixture(); var path = Path.Combine(f.Root, ".reviewoffset.lock.tmp"); var output = Path.Combine(f.Root, "probe.loaded");
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            info.ArgumentList.Add("--probe-store-load"); info.ArgumentList.Add(Path.Combine(f.Root, "Config")); info.ArgumentList.Add(output);
            using var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            using var peer = Process.Start(info)!;
            for (var i = 0; i < 200 && !File.Exists(output + ".started"); i++) await Task.Delay(25);
            True(File.Exists(output + ".started")); await Task.Delay(150); True(!File.Exists(output));
            held.Dispose(); await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); True(peer.ExitCode == 0 && File.Exists(output));
        });
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
