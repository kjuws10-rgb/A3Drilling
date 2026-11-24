using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Drilling.Common.Managers;
using Drilling.Common.Review;
using Drilling.UI.Menu.Menus;

internal static partial class Program
{
    private static async Task RunMonitorDisplayChecks()
    {
        await Check("monitor_saved_review_change_refreshes_once_then_keeps_same_version", async () =>
        {
            var f = new Fixture(); var store = new DisplayOffsets(); var builds = 0;
            var monitor = DisplayMonitor(f, store, () => { builds++; return Task.CompletedTask; });
            await monitor.RefreshLiveData(); True(builds == 1);
            SetDisplay(monitor, "_coordinateReviewOffsetVersion", DisplayVersion("QA", store.Saved));
            await monitor.RefreshLiveData(); True(builds == 1);
            store.Saved = store.Saved with { ContentSha256 = "changed-same-path-same-time" };
            await monitor.RefreshLiveData(); True(builds == 2);
            SetDisplay(monitor, "_coordinateReviewOffsetVersion", DisplayVersion("QA", store.Saved));
            await monitor.RefreshLiveData(); True(builds == 2);
        });
        await Check("monitor_empty_selection_fallback_model_refreshes_and_recipe_versions_are_distinct", async () =>
        {
            var f = new Fixture { Selected = "" }; var store = new DisplayOffsets(); var builds = 0;
            var monitor = DisplayMonitor(f, store, () => { builds++; return Task.CompletedTask; });
            await monitor.RefreshLiveData(); True(builds == 1);
            True(DisplayVersion("QA", store.Saved) != DisplayVersion("OTHER", store.Saved with { RecipeId = "OTHER" }));
        });
        await Check("monitor_inflight_model_switch_and_tab_switch_do_not_publish_stale_offsets", async () =>
        {
            var f = new Fixture(); var store = new DisplayOffsets(); var builds = 0;
            var monitor = DisplayMonitor(f, store, () => { builds++; return Task.CompletedTask; });
            store.OnRead = () => { f.Selected = "OTHER"; return Task.CompletedTask; };
            await monitor.RefreshLiveData(); True(builds == 0);
            f.Selected = "QA";
            store.OnRead = () => { SetDisplay(monitor, "_selectedTabAccessor", (Func<string>)(() => "PRODUCT")); return Task.CompletedTask; };
            await monitor.RefreshLiveData(); True(builds == 0);
        });
        await Check("monitor_concurrent_refresh_is_coalesced_without_device_calls", async () =>
        {
            var f = new Fixture(); var store = new DisplayOffsets(); var builds = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            store.OnRead = async () => { entered.SetResult(); await release.Task; };
            var monitor = DisplayMonitor(f, store, () => { builds++; return Task.CompletedTask; });
            var first = monitor.RefreshLiveData(); await entered.Task;
            await monitor.RefreshLiveData(); True(store.Reads == 1 && builds == 0);
            release.SetResult(); await first; True(builds == 1);
        });
        await Check("monitor_read_failure_retains_display_reports_once_and_recovers", async () =>
        {
            var f = new Fixture(); var store = new DisplayOffsets(); var builds = 0; var messages = new List<string>();
            var monitor = DisplayMonitor(f, store, () => { builds++; return Task.CompletedTask; });
            SetDisplay(monitor, "_setStatusMessage", (Action<string>)messages.Add);
            var prior = "last-good"; SetDisplay(monitor, "_coordinateReviewOffsetVersion", prior);
            store.OnRead = () => throw new IOException("synthetic file read error");
            await monitor.RefreshLiveData(); await monitor.RefreshLiveData();
            True(builds == 0 && messages.Count == 1 && (string)GetDisplay(monitor, "_coordinateReviewOffsetVersion") == prior);
            store.OnRead = null; await monitor.RefreshLiveData(); True(builds == 1);
        });
        await Check("monitor_cancelled_read_never_rebuilds_and_releases_refresh_guard", async () =>
        {
            var f = new Fixture(); var store = new DisplayOffsets(); var builds = 0;
            using var cancel = new CancellationTokenSource();
            var monitor = DisplayMonitor(f, store, () => { builds++; return Task.CompletedTask; });
            store.OnRead = () => { cancel.Cancel(); return Task.CompletedTask; };
            bool cancelled=false;try { await monitor.RefreshLiveData(cancel.Token); } catch(OperationCanceledException) { cancelled=true; } True(cancelled && builds == 0);
            store.OnRead = null; await monitor.RefreshLiveData(); True(builds == 1);
        });
        await Check("monitor_unchanged_success_clears_error_episode_so_repeated_failure_is_reported", async () =>
        {
            var f=new Fixture();var store=new DisplayOffsets();var messages=new List<string>();var builds=0;
            var monitor=DisplayMonitor(f,store,()=>{builds++;return Task.CompletedTask;});
            SetDisplay(monitor,"_setStatusMessage",(Action<string>)messages.Add);
            SetDisplay(monitor,"_coordinateReviewOffsetVersion",DisplayVersion("QA",store.Saved));
            store.OnRead=()=>throw new IOException("temporary read");await monitor.RefreshLiveData();True(messages.Count==1);
            store.OnRead=null;await monitor.RefreshLiveData();True(builds==0&&(string)GetDisplay(monitor,"_coordinateReviewOffsetError")=="");
            store.OnRead=()=>throw new IOException("temporary read");await monitor.RefreshLiveData();True(messages.Count==2&&builds==0);
        });
    }

    private static CMenuMonitor DisplayMonitor(Fixture f, DisplayOffsets store, Func<Task> rebuild)
    {
        // Only the real display refresh method is exercised. Never construct hardware managers,
        // polling services, the application Root, or equipment view model constructors.
        var monitor = (CMenuMonitor)RuntimeHelpers.GetUninitializedObject(typeof(CMenuMonitor));
        SetDisplay(monitor, "_recipeManager", new CRecipeManager(new RecipesFile(f)));
        SetDisplay(monitor, "_reviewOffsetFile", store);
        SetDisplay(monitor, "_selectedRecipeIdProvider", (Func<string>)(() => f.Selected));
        SetDisplay(monitor, "_selectedTabAccessor", (Func<string>)(() => "COORDINATE VIEWER"));
        SetDisplay(monitor, "<SelectedTab>k__BackingField", "COORDINATE VIEWER");
        SetDisplay(monitor, "_setStatusMessage", (Action<string>)(_ => { }));
        SetDisplay(monitor, "_refreshCurrentScreen", rebuild);
        SetDisplay(monitor, "_coordinateReviewOffsetVersion", "");
        SetDisplay(monitor, "_coordinateReviewOffsetError", "");
        return monitor;
    }
    private static void SetDisplay(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object GetDisplay(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static string DisplayVersion(string recipe, ST_REVIEW_OFFSET_FILE_DATA file) =>
        (string)typeof(CMenuMonitor).GetMethod("CoordinateReviewOffsetVersion", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [recipe, file])!;

    private sealed class DisplayOffsets : CReviewOffsetFileBase
    {
        public ST_REVIEW_OFFSET_FILE_DATA Saved = new("QA", "synthetic/offset.csv", "offset.csv", DateTimeOffset.UnixEpoch, []) { ContentSha256 = "v1" };
        public Func<Task>? OnRead;
        public int Reads;
        public override string RootPath => "synthetic";
        public override async Task<ST_REVIEW_OFFSET_FILE_DATA> LoadLatest(string id, CancellationToken token = default)
        { Reads++; if (OnRead is not null) await OnRead(); token.ThrowIfCancellationRequested(); return Saved with { RecipeId = id }; }
        public override Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNew(string id, IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows, CancellationToken token = default) => throw new NotSupportedException();
        public override Task<ST_REVIEW_OFFSET_SOURCE_APPLICATION?> FindSourceApplication(string id, string path, CancellationToken token = default) => throw new NotSupportedException();
        public override Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNewFromSource(string id, IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows, string path, bool duplicate, CancellationToken token = default) => throw new NotSupportedException();
        public override Task<ST_REVIEW_OFFSET_ROLLBACK_RESULT> RollbackLatest(string id, string path, CancellationToken token = default) => throw new NotSupportedException();
    }
}
