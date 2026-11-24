using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunStartupFinalOffsetsCases()
    {
        await Check("startup_preload_reads_resolved_recipe_final_without_guard_motion_or_save", async () =>
        {
            var (f, _) = Fixture();
            // The shell has resolved the configured fallback before calling EnsureLoaded.
            f.Source = f.Source with
            {
                Context = f.Source.Context with { RecipeId = "RESOLVED-RECIPE", RunId = "PREPARED-RUN" },
                Points = f.Source.Points.Select(p => p with
                {
                    OriginalMeasurement = p.OriginalMeasurement is { } m
                        ? m with { RecipeId = "RESOLVED-RECIPE", RunId = "PREPARED-RUN" } : null
                }).ToArray()
            };
            var b = new FinalBackend(f) { Saved = Saved(f.Source, "LATEST-SAVED", 47) };
            var c = new CManualInspectionController(b);
            True(f.Loads == 0 && !c.Snapshot.HasFinalOffsetSnapshot);
            await c.EnsureLoaded();
            True(c.Snapshot.HasFinalOffsetSnapshot && c.Snapshot.FinalOffsetFileName == "LATEST-SAVED.csv"
                && c.Snapshot.FinalOffsetRevisionId == "LATEST-SAVED");
            Near(c.Snapshot.FinalOffsetPoints["P1"].XUm, 47);
            True(f.Loads == 1 && f.Guards == 0 && f.Saves == 0 && f.Executed.Count == 0 && !c.IsBusy);
        });
        await Check("startup_saved_read_failure_is_visible_and_retry_recovers_without_device_action", async () =>
        {
            var (f, _) = Fixture();
            var b = new FinalBackend(f) { Saved = Saved(f.Source, "RECOVERED", 12), FailRead = true };
            var c = new CManualInspectionController(b);
            await c.EnsureLoaded();
            True(!c.IsBusy && !c.Snapshot.HasFinalOffsetSnapshot && c.Snapshot.FinalOffsetStatusText.Contains("실패"));
            b.FailRead = false;
            await c.EnsureLoaded();
            True(c.Snapshot.HasFinalOffsetSnapshot && c.Snapshot.FinalOffsetFileName == "RECOVERED.csv"
                && f.Executed.Count == 0 && f.Saves == 0 && f.Guards == 0);
        });
        await Check("cancelled_startup_preload_releases_command_and_can_retry", async () =>
        {
            var (f, _) = Fixture();
            var b = new FinalBackend(f) { Saved = Saved(f.Source, "AFTER-CANCEL", 8) };
            var c = new CManualInspectionController(b);
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Throws(() => c.EnsureLoaded(cancel.Token));
            True(!c.IsBusy && f.Executed.Count == 0 && f.Saves == 0);
            await c.EnsureLoaded();
            True(c.Snapshot.HasFinalOffsetSnapshot && f.Guards == 0 && f.Saves == 0);
        });
        await Check("startup_loaded_manual_apply_reloads_new_saved_revision_without_double_application", async () =>
        {
            var (f, _) = Fixture();
            var b = new FinalBackend(f) { UseCurrent = true };
            var c = new CManualInspectionController(b);
            await c.EnsureLoaded();
            await Prepared(c);
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            True(f.Saves == 1 && c.Snapshot.FinalOffsetRevisionId == "REV-1"
                && c.Snapshot.FinalOffsetFileName == "REV-1.csv");
            Near(c.Snapshot.FinalOffsetPoints["P1"].XUm, 3);
            await c.EnsureLoaded();
            True(f.Saves == 1 && f.Executed.Count == 0 && c.Snapshot.FinalOffsetRevisionId == "REV-1");
            Near(c.Snapshot.FinalOffsetPoints["P1"].XUm, 3);
        });
    }
}
