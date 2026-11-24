using System.IO;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunHardeningChecks()
    {
        await Check("hardening_stale_confirmation_after_external_refresh_never_moves", async () =>
        {
            var (f, c) = await Loaded();
            var confirmed = new CManualInspectionUiRequest { ExpectedVersion = c.Snapshot.Version, Keys = ["P1"] };
            SetIncoming(f, "confirmed-before-refresh", 8, DateTimeOffset.UtcNow.AddMinutes(1));
            await c.SynchronizeExternalAsync();
            await Throws(() => c.ExecuteAsync(EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect, confirmed));
            True(f.Executed.Count == 0 && f.Saves == 0 && !c.IsBusy);
        });
        await Check("hardening_new_product_run_does_not_restore_previous_measurements_or_copy", async () =>
        {
            var (f, c) = await Loaded();
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.CopyReference, ["P2"], r => r with { ReferenceKey = "P1", Reason = "Old product only" });
            f.Source = WithRun(f.Source, "RUN-2");
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Refresh);
            True(c.Snapshot.RunId == "RUN-2" && c.Snapshot.CandidateRows.Count == 0 && !c.Snapshot.HasIncomplete);
            True(c.Snapshot.Points.All(p => !p.IsProxy && p.ReferenceKey.Length == 0 && p.MeasurementToken.StartsWith("RUN-2:", StringComparison.Ordinal)));
            True(!c.Snapshot.IsReadOnly && f.Executed.Count == 0 && f.Saves == 1);
        });
        await Check("hardening_new_run_can_replace_old_interrupted_run_without_resuming_motion", async () =>
        {
            var (f, c) = await Loaded(); f.FailKey = "P2";
            await Throws(() => Do(c, EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure, ["P1", "P2"]));
            f.Source = WithRun(f.Source, "RUN-2"); var operations = f.Executed.Count;
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Refresh);
            True(c.Snapshot.RunId == "RUN-2" && !c.Snapshot.HasIncomplete && c.Snapshot.CompletedCount == 0);
            True(f.Executed.Count == operations && f.Saves == 0);
        });
        await Check("hardening_same_run_revision_change_cannot_silently_clear_interrupted_work", async () =>
        {
            var (f, c) = await Loaded(); f.FailKey = "P2";
            await Throws(() => Do(c, EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure, ["P1", "P2"]));
            f.Source = f.Source with { Context = f.Source.Context with { RevisionId = "EXTERNAL-REV" } };
            await Throws(() => Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Refresh));
            True(!c.Snapshot.IsReadOnly && c.Snapshot.HasIncomplete && c.Snapshot.RemainingCount == 1 && f.Saves == 0);
            await Throws(() => Do(c, EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining));
        });
        await Check("hardening_old_run_session_import_is_rejected_before_native_operation", async () =>
        {
            var (f, c) = await Loaded(); var session = Path.Combine(f.StateDirectory, "run-one.json");
            c.ChooseFile = (_, _) => session; await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.ExportSession);
            f.Source = WithRun(f.Source, "RUN-2");
            await Throws(() => Do(c, EN_MANUAL_INSPECTION_UI_ACTION.ImportSession));
            True(!c.Snapshot.IsReadOnly && c.Snapshot.RunId == "RUN-1" && f.Executed.Count == 0 && f.Saves == 0);
            try { await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Move, ["P1"]); } catch(InvalidOperationException) { }
            if(f.Executed.Count==0)await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]);
            True(f.Executed.SequenceEqual(["P1"])&&c.Snapshot.Points.Count==f.Source.Points.Count&&!c.Snapshot.CanApply);
        });
        await Check("hardening_import_native_session_exits_explicit_archive_view_and_resumes_live_sync", async () =>
        {
            var (f, c) = await Loaded(); var native = f.Source;
            var session = Path.Combine(f.StateDirectory, "native.json"); c.ChooseFile = (_, _) => session;
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.ExportSession);
            f.Source = native with { ReadOnly = true, SourcePath = "archive.csv" };
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile, null, r => r with { ResultPath = "archive.csv" });
            f.Source = native;
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.ImportSession);
            SetIncoming(f, "live-after-native-import", 11, DateTimeOffset.UtcNow.AddMinutes(2));
            var loads = f.Loads; await c.SynchronizeExternalAsync();
            True(f.Loads == loads + 1 && !c.Snapshot.IsReadOnly && c.Snapshot.SourcePath.Length == 0);
            True(c.Snapshot.Points.Single(p => p.Key == "P1").MeasurementSourcePath == "live-after-native-import.csv");
            True(f.Executed.Count == 0 && f.Saves == 0);
        });
        await Check("hardening_throwing_changed_subscriber_does_not_leak_command_or_hide_later_subscribers", async () =>
        {
            var (f, c) = await Loaded(); var delivered = 0;
            EventHandler bad = (_, _) => throw new InvalidOperationException("Synthetic view render fault");
            EventHandler good = (_, _) => delivered++;
            c.Changed += bad; c.Changed += good;
            try { await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels, null, r => r with { LevelMetric = "RADIAL", LevelBoundsUm = [5, 10, 20], LevelNames = ["A", "B", "C", "D"] }); }
            finally { c.Changed -= bad; c.Changed -= good; }
            True(delivered > 0 && !c.IsBusy && c.Snapshot.LevelMetric == "RADIAL");
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Move, ["P1"]);
            True(f.Executed.SequenceEqual(["P1"]) && f.Saves == 0);
        });
        await Check("hardening_throwing_report_observer_does_not_leak_command_gate", async () =>
        {
            var f = new FakeBackend(Path.Combine(Root, Guid.NewGuid().ToString("N")), false, false);
            var c = new CManualInspectionController(f, _ => throw new IOException("Synthetic log reporter fault"));
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Move, ["P1"]);
            True(!c.IsBusy && f.Executed.SequenceEqual(["P1"]) && f.Saves == 0);
        });
        await Check("hardening_throwing_view_during_sync_keeps_external_observation_and_gate_usable", async () =>
        {
            var (f, c) = await Loaded(); SetIncoming(f, "view-fault-sync", 9, DateTimeOffset.UtcNow.AddMinutes(2));
            EventHandler bad = (_, _) => throw new InvalidOperationException("Synthetic sync paint failure"); c.Changed += bad;
            try { await c.SynchronizeExternalAsync(); } finally { c.Changed -= bad; }
            True(!c.IsBusy && c.Snapshot.Points.Single(p => p.Key == "P1").WorkingXUm == 9);
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Move, ["P1"]);
            True(f.Executed.Count == 1 && f.Saves == 0);
        });
        await Check("hardening_stop_cleanup_survives_throwing_view_observer", async () =>
        {
            var (f, c) = await Loaded(); f.Hold = true;
            var running = Do(c, EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure, ["P1", "P2"]);
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            EventHandler bad = (_, _) => throw new InvalidOperationException("Synthetic STOP paint failure"); c.Changed += bad;
            try
            {
                var stop = c.StopAsync(); await f.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
                True(!stop.IsCompleted && c.IsBusy && f.StopCalls == 1);
                f.Release.TrySetResult(); await running; await stop.WaitAsync(TimeSpan.FromSeconds(3));
                True(!c.IsBusy && c.Snapshot.HasIncomplete && c.Snapshot.RemainingCount == 2);
            }
            finally { c.Changed -= bad; f.Release.TrySetResult(); }
            f.Hold = false; await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining);
            True(!c.Snapshot.HasIncomplete && f.Saves == 0);
        });
        await Check("hardening_external_notification_during_apply_is_deferred_until_committed_revision", async () =>
        {
            var (f, c) = await Loaded(); await Prepared(c); var loads = f.Loads;
            f.SaveHook = async _ => { await c.SynchronizeExternalAsync(); True(c.IsBusy && c.Snapshot.SyncPending && f.Loads == loads); };
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            True(f.Saves == 1 && f.Loads == loads + 1 && c.Snapshot.ActiveRevisionId == "REV-1" && !c.IsBusy);
            True(!c.Snapshot.SyncPending && !c.Snapshot.CanApply);
        });
        await Check("hardening_backend_stop_failure_does_not_skip_cleanup_wait_or_leak_gate", async () =>
        {
            var (f, c) = await Loaded(); f.Hold = true; f.StopHook = () => throw new IOException("Synthetic stop command failure");
            var running = Do(c, EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure, ["P1", "P2"]);
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stop = c.StopAsync(); await f.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try { True(!stop.IsCompleted && c.IsBusy && f.StopCalls == 1); }
            finally { f.Release.TrySetResult(); }
            await running; await stop.WaitAsync(TimeSpan.FromSeconds(3));
            True(!c.IsBusy && c.Snapshot.HasIncomplete && f.Saves == 0 && c.Snapshot.Message.Contains("Synthetic stop command failure", StringComparison.Ordinal));
            f.Hold = false; f.StopHook = null; await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining);
            True(!c.Snapshot.HasIncomplete);
        });
        await Check("hardening_throwing_cancellation_callback_still_requests_backend_stop_and_waits_cleanup", async () =>
        {
            var (f, c) = await Loaded(); f.Hold = true; f.CancellationCallbackThrows = true;
            var running = Do(c, EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure, ["P1", "P2"]);
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stop = c.StopAsync(); await f.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try { True(!stop.IsCompleted && c.IsBusy && f.StopCalls == 1); }
            finally { f.Release.TrySetResult(); }
            await running; await stop.WaitAsync(TimeSpan.FromSeconds(3));
            True(!c.IsBusy && c.Snapshot.HasIncomplete && f.Saves == 0 && c.Snapshot.Message.Contains("Synthetic cancellation callback failure", StringComparison.Ordinal));
            f.Hold = false; f.CancellationCallbackThrows = false; await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining);
            True(!c.Snapshot.HasIncomplete);
        });
        await Check("hardening_cancelled_apply_before_persist_does_not_commit_or_allow_duplicate_write", async () =>
        {
            var (f, c) = await Loaded(); await Prepared(c); using var cancel = new CancellationTokenSource();
            f.SaveHook = token => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
            await c.ExecuteAsync(EN_MANUAL_INSPECTION_UI_ACTION.Apply, new() { ExpectedVersion = c.Snapshot.Version }, cancel.Token);
            True(f.Saves == 0 && c.Snapshot.ActiveRevisionId == "INITIAL" && !c.IsBusy && !c.Snapshot.CanApply);
            await Throws(() => Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Apply));
            f.SaveHook = null; await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Validate);
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Apply); True(f.Saves == 1);
            await Throws(() => Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Apply)); True(f.Saves == 1);
        });
        await Check("hardening_geometry_shrink_requires_current_target_reconfirmation_instead_of_erasing_saved_point", async () =>
        {
            var (f, c) = await Loaded();
            f.Source = f.Source with { Points = f.Source.Points.Take(2).ToArray(), Values = f.Source.Values.Where(p => p.Key != "P3").ToDictionary(p => p.Key, p => p.Value), ObservationFingerprint = "geometry-shrunk" };
            await c.SynchronizeExternalAsync();
            True(!c.Snapshot.IsReadOnly && !c.Snapshot.CanApply && f.Executed.Count == 0 && f.Saves == 0);
            try { await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Move, ["P1"]); } catch(InvalidOperationException) { }
            if(f.Executed.Count==0)await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.Move,["P1"]);
            True(f.Executed.SequenceEqual(["P1"])&&c.Snapshot.Points.Count==f.Source.Points.Count&&!c.Snapshot.CanApply);
        });
        await Check("hardening_beam_growth_preserves_native_raw_and_shared_offset_revision", async () =>
        {
            var (f, c) = await Loaded(); await Prepared(c); await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            var prior = c.Snapshot.Points.Single(p => p.Key == "P1");
            var sibling = f.Source.Points[0] with { Key = "P1/B1", BeamNo = 1, Judge = "UNMEASURED", Source = "KEPT", OriginalMeasurement = null, ErrorXUm = null, ErrorYUm = null };
            f.Source = f.Source with { Points = f.Source.Points.Append(sibling).ToArray(), Values = f.Source.Values.Append(new(sibling.Key, f.Source.Values["P1"])).ToDictionary(p => p.Key, p => p.Value), ObservationFingerprint = "newly-visible-real-beam" };
            await c.SynchronizeExternalAsync();
            var after = c.Snapshot.Points.Single(p => p.Key == "P1");
            True(!c.Snapshot.IsReadOnly && c.Snapshot.Points.Count == 4 && after.MeasurementToken == prior.MeasurementToken && after.WorkingXUm == prior.WorkingXUm);
            True(c.Snapshot.Points.Single(p => p.Key == sibling.Key).BaseXUm == prior.BaseXUm && f.Saves == 1);
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Rollback);
            True(c.Snapshot.Points.Where(p => p.ShotNo == 1).All(p => p.BaseXUm == 0) && f.Saves == 2);
        });
        await Check("hardening_real_file_watcher_enable_disable_reenable_never_executes_device", async () =>
        {
            var (f, c) = await Loaded(); var directory = Path.Combine(f.StateDirectory, "Watched"); Directory.CreateDirectory(directory);
            f.WatchRoots = [directory]; c.EnableAutomaticUpdates(); c.EnableAutomaticUpdates();
            try
            {
                SetIncoming(f, "watch-enabled", 8, DateTimeOffset.UtcNow.AddMinutes(2));
                File.WriteAllText(Path.Combine(directory, "first.csv"), "synthetic");
                await Until(() => c.Snapshot.Points.Single(p => p.Key == "P1").WorkingXUm == 8);
                c.DisableAutomaticUpdates(); await Task.Delay(150); var loads = f.Loads;
                SetIncoming(f, "watch-disabled", 9, DateTimeOffset.UtcNow.AddMinutes(3));
                File.WriteAllText(Path.Combine(directory, "second.csv"), "synthetic"); await Task.Delay(850);
                True(f.Loads == loads && c.Snapshot.Points.Single(p => p.Key == "P1").WorkingXUm == 8);
                c.EnableAutomaticUpdates(); File.WriteAllText(Path.Combine(directory, "third.csv"), "synthetic");
                await Until(() => c.Snapshot.Points.Single(p => p.Key == "P1").WorkingXUm == 9);
                True(f.Executed.Count == 0 && f.Saves == 0);
            }
            finally { c.DisableAutomaticUpdates(); }
        });
        await Check("hardening_applied_copy_audits_captured_reference_raw_after_reference_remeasurement", async () =>
        {
            var (f, c) = await Loaded();
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.CopyReference, ["P2"], r => r with { ReferenceKey = "P1", Reason = "Freeze the captured reference" });
            var session = Path.Combine(f.StateDirectory, "captured-copy.json"); c.ChooseFile = (_, _) => session;
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.ExportSession);
            using var copy = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(session));
            var oldReferenceRaw = copy.RootElement.GetProperty("Session").GetProperty("Points").EnumerateArray().Single(p => p.GetProperty("Key").GetString() == "P2")
                .GetProperty("ProxySource").GetProperty("ReferenceMeasurementToken").GetString();
            True(!string.IsNullOrEmpty(oldReferenceRaw));
            f.NewX = 8; await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure, ["P1"]);
            using var appliedCopy = System.Text.Json.JsonDocument.Parse(f.SourceIds.Single());
            var manualRow = appliedCopy.RootElement.GetProperty("Tokens").EnumerateArray().Single();
            True(manualRow.GetProperty("Source").GetString() == "MANUAL" && manualRow.GetProperty("ReferenceKey").GetString() == "P1");
            True(manualRow.GetProperty("ReferenceRaw").GetString() == oldReferenceRaw && c.Snapshot.Points.Single(p => p.Key == "P2").BaseXUm == 3);
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Calculate, ["P1"]);
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Validate);
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            using var direct = System.Text.Json.JsonDocument.Parse(f.SourceIds.Last());
            var directRow = direct.RootElement.GetProperty("Tokens").EnumerateArray().Single();
            True(directRow.GetProperty("Source").GetString() == "MEASURED" && directRow.GetProperty("ReferenceRaw").GetString() == directRow.GetProperty("MeasurementToken").GetString());
            True(directRow.GetProperty("ReferenceRaw").GetString() != oldReferenceRaw && f.Saves == 2);
        });
    }

    private static CManualInspectionSource WithRun(CManualInspectionSource source, string run)
    {
        var context = source.Context with { RunId = run };
        return source with { Context = context, ObservationFingerprint = run, ImportedResults = new Dictionary<string, CManualInspectionImportedResult>(),
            Points = source.Points.Select(p => p with { OriginalMeasurement = p.OriginalMeasurement! with { RunId = run, RawToken = run + ":" + p.Key } }).ToArray() };
    }
}
