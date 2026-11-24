using System.IO;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

internal static partial class Program
{
    private static async Task RunSynchronizationChecks()
    {
        await Check("common_level_inclusive_boundaries_and_sign", async () =>
        {
            double[] bounds = [5, 10, 20];
            foreach (var (x, expected) in new[] { (0d, 0), (5d, 0), (-5d, 0), (5.0001, 1), (10d, 1), (10.0001, 2), (20d, 2), (20.0001, 3) })
                True(CManualInspectionLevels.Evaluate(x, 0, "MAX_AXIS", bounds) == expected);
            True(CManualInspectionLevels.Evaluate(3, 4, "RADIAL", bounds) == 0);
            Near(CManualInspectionLevels.Metric(3, 4, "RADIAL")!.Value, 5);
            Near(CManualInspectionLevels.Metric(3, -4, "MAX_AXIS")!.Value, 4);
            True(CManualInspectionLevels.Evaluate(null, 0, "MAX_AXIS", bounds) is null);
            True(CManualInspectionLevels.Evaluate(double.NaN, 0, "MAX_AXIS", bounds) is null);
            True(CManualInspectionLevels.Evaluate(0, double.PositiveInfinity, "RADIAL", bounds) is null);
            await Task.CompletedTask;
        });
        await Check("active_offsets_color_unmeasured_points_without_inventing_results", async () =>
        {
            var (f, c) = Fixture();
            f.Source = f.Source with
            {
                Points = f.Source.Points.Select(p => p with { Judge = "UNMEASURED", Source = "KEPT", OriginalMeasurement = null, ErrorXUm = null, ErrorYUm = null }).ToArray(),
                Values = new Dictionary<string, ST_MANUAL_OFFSET> { ["P1"] = new(3, 4), ["P2"] = new(6, 8), ["P3"] = new(15, 0) },
                AppliedOffsetKeys = new HashSet<string>(["P1", "P2", "P3"], StringComparer.Ordinal)
            };
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            True(c.Snapshot.Points.Select(p => p.AppliedLevel).SequenceEqual(new int?[] { 0, 1, 2 }));
            True(c.Snapshot.Points.All(p => p.HasAppliedOffset && p.WorkingJudge == "UNMEASURED" && p.WorkingXUm is null && p.OriginalXUm is null));
            True(f.Saves == 0 && f.Executed.Count == 0);
        });
        await Check("missing_active_row_is_not_a_zero_level_measurement", async () =>
        {
            var (f, c) = await Loaded();
            True(c.Snapshot.Points.All(p => !p.HasAppliedOffset && p.AppliedLevel is null));
            True(f.Saves == 0);
        });
        await Check("external_new_measurement_sync_is_imported_and_has_provenance", async () =>
        {
            var (f, c) = await Loaded();
            SetIncoming(f, "external-1", 7, DateTimeOffset.UtcNow.AddMinutes(2));
            await c.SynchronizeExternalAsync();
            var p = c.Snapshot.Points.Single(p => p.Key == "P1");
            Near(p.WorkingXUm!.Value, 7);
            True(p.Source == "IMPORTED" && p.CanBeReference && !p.CanCopyAsReference && p.MeasurementSourcePath.EndsWith("external-1.csv", StringComparison.Ordinal));
            True(p.DisplayedMeasurementOrigin.Contains("FINE", StringComparison.OrdinalIgnoreCase));
            True(!c.Snapshot.SyncPending && f.Saves == 0 && f.Executed.Count == 0);
        });
        await Check("notification_during_load_is_replayed_after_first_snapshot", async () =>
        {
            var (f, c) = await Loaded();
            SetIncoming(f, "first-captured", 7, DateTimeOffset.UtcNow.AddMinutes(2));
            var loads = f.Loads;
            f.LoadHook = async (_, _) =>
            {
                f.LoadHook = null;
                SetIncoming(f, "mid-load-next", 9, DateTimeOffset.UtcNow.AddMinutes(3));
                await c.SynchronizeExternalAsync();
            };
            await c.SynchronizeExternalAsync();
            await Until(() => !c.IsBusy && c.Snapshot.Points.Single(p => p.Key == "P1").WorkingXUm == 9);
            True(f.Loads == loads + 2 && !c.Snapshot.SyncPending && f.Saves == 0 && f.Executed.Count == 0);
            True(c.Snapshot.Points.Single(p => p.Key == "P1").MeasurementSourcePath.EndsWith("mid-load-next.csv", StringComparison.Ordinal));
        });
        await Check("explicit_file_view_is_not_replaced_by_live_sync", async () =>
        {
            var (f, c) = await Loaded();
            f.Source = f.Source with { SourcePath = "selected-archive.csv", ReadOnly = true };
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile, null, r => r with { ResultPath = "selected-archive.csv" });
            var loads = f.Loads;
            SetIncoming(f, "new-live-while-archive", 8, DateTimeOffset.UtcNow.AddMinutes(2));
            await c.SynchronizeExternalAsync();
            True(f.Loads == loads && c.Snapshot.SourcePath == "selected-archive.csv" && !c.Snapshot.IsReadOnly && f.Executed.Count == 0);
        });
        await Check("cancelled_sync_retries_current_context_without_permanent_readonly_mode", async () =>
        {
            var (f, c) = await Loaded();
            using var cancel = new CancellationTokenSource();
            SetIncoming(f, "cancelled-rebuild", 9, DateTimeOffset.UtcNow.AddMinutes(2));
            f.LoadHook = (_, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
            await Throws(() => c.SynchronizeExternalAsync(cancel.Token));
            True(!c.Snapshot.IsReadOnly && c.Snapshot.SyncPending && !c.IsBusy);
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Move, ["P1"]);
            f.LoadHook = null;
            await c.SynchronizeExternalAsync();
            True(!c.Snapshot.IsReadOnly && !c.Snapshot.SyncPending && f.Executed.Count == 1);
        });
        await Check("older_external_measurement_does_not_replace_native_raw", async () =>
        {
            var (f, c) = await Loaded();
            var before = c.Snapshot.Points.Single(p => p.Key == "P1");
            SetIncoming(f, "external-old", 9, DateTimeOffset.UtcNow.AddYears(-1));
            await c.SynchronizeExternalAsync();
            var after = c.Snapshot.Points.Single(p => p.Key == "P1");
            True(after.WorkingXUm == before.WorkingXUm && after.MeasurementToken == before.MeasurementToken && after.Source == before.Source);
        });
        await Check("same_revision_external_measurement_does_not_authorize_old_candidate", async () =>
        {
            var (f, c) = await Loaded(); await Prepared(c);
            var before = c.Snapshot.CandidateRows.Single().FinalXUm;
            SetIncoming(f, "external-candidate", 8, DateTimeOffset.UtcNow.AddMinutes(2));
            var loads = f.Loads;
            await c.SynchronizeExternalAsync();
            True(c.Snapshot.SyncPending && c.Snapshot.CandidateRows.Count == 1 && f.Loads == loads);
            Near(c.Snapshot.CandidateRows[0].FinalXUm, before);
            True(f.Saves == 0 && f.Executed.Count == 0);
        });
        await Check("external_revision_sync_colors_every_point_with_fresh_actual_values", async () =>
        {
            var (f, c) = await Loaded();
            f.Source = f.Source with
            {
                Context = f.Source.Context with { RevisionId = "EXTERNAL-FINE-REV" },
                Values = new Dictionary<string, ST_MANUAL_OFFSET> { ["P1"] = new(4, -4), ["P2"] = new(6, -6), ["P3"] = new(25, 0) },
                AppliedOffsetKeys = new HashSet<string>(["P1", "P2", "P3"], StringComparer.Ordinal),
                ObservationFingerprint = "external-offset-revision"
            };
            await c.SynchronizeExternalAsync();
            True(c.Snapshot.ActiveRevisionId == "EXTERNAL-FINE-REV");
            True(c.Snapshot.Points.Select(p => p.AppliedLevel).SequenceEqual(new int?[] { 0, 1, 3 }));
            True(!c.Snapshot.CanApply && f.Saves == 0 && f.Executed.Count == 0);
        });
        await Check("external_sync_keeps_incomplete_and_does_not_reload_backend", async () =>
        {
            var (f, c) = await Loaded(); f.FailKey = "P2";
            await Throws(() => Do(c, EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure, ["P1", "P2"]));
            var loads = f.Loads;
            SetIncoming(f, "external-pending", 8, DateTimeOffset.UtcNow.AddMinutes(2));
            await c.SynchronizeExternalAsync();
            True(c.Snapshot.SyncPending && c.Snapshot.HasIncomplete && c.Snapshot.CompletedCount == 1 && c.Snapshot.RemainingCount == 1 && f.Loads == loads);
            True(!c.Snapshot.CanApply && f.Saves == 0);
        });
        await Check("external_sync_during_busy_does_not_reenter_load_or_clear_remaining", async () =>
        {
            var (f, c) = await Loaded(); f.Hold = true;
            var running = Do(c, EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure, ["P1", "P2"]);
            await f.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var loads = f.Loads;
            SetIncoming(f, "external-busy", 9, DateTimeOffset.UtcNow.AddMinutes(2));
            await c.SynchronizeExternalAsync();
            True(c.IsBusy && c.Snapshot.SyncPending && f.Loads == loads);
            c.RequestStop(); await f.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            f.Release.TrySetResult(); await running;
            True(!c.IsBusy && c.Snapshot.HasIncomplete && f.Saves == 0);
        });
        await Check("external_revision_with_candidate_preserves_work_and_blocks_stale_write", async () =>
        {
            var (f, c) = await Loaded(); await Prepared(c);
            f.Stale = true;
            f.Source = f.Source with { Context = f.Source.Context with { RevisionId = "FOREIGN-REV" }, ObservationFingerprint = "foreign-revision-during-candidate" };
            await c.SynchronizeExternalAsync();
            True(c.Snapshot.SyncPending && c.Snapshot.CandidateRows.Count == 1);
            await Throws(() => Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Apply));
            True(f.Saves == 0 && c.Snapshot.ActiveRevisionId == "FOREIGN-REV" && c.Snapshot.CandidateRows.Count==0);
        });
        await Check("applied_candidate_does_not_permanently_block_external_refresh", async () =>
        {
            var (f, c) = await Loaded(); await Prepared(c); await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Apply);
            SetIncoming(f, "after-apply", 9, DateTimeOffset.UtcNow.AddMinutes(2));
            await c.SynchronizeExternalAsync();
            True(!c.Snapshot.SyncPending && c.Snapshot.ActiveRevisionId == "REV-1" && c.Snapshot.Points.Single(p => p.Key == "P1").Source == "IMPORTED");
            True(f.Saves == 1);
        });
        await Check("level_change_recolors_offsets_and_preserves_original_measurement", async () =>
        {
            var (f, c) = Fixture();
            f.Source = f.Source with { Values = new Dictionary<string, ST_MANUAL_OFFSET> { ["P1"] = new(3, 4), ["P2"] = new(6, 8), ["P3"] = new(0, 0) }, AppliedOffsetKeys = new HashSet<string>(["P1", "P2", "P3"]) };
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            var raw = c.Snapshot.Points.Select(p => p.MeasurementToken).ToArray();
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels, null, r => r with { LevelMetric = "RADIAL", LevelBoundsUm = [4, 9, 20], LevelNames = ["A", "B", "C", "D"] });
            True(c.Snapshot.Points.Select(p => p.AppliedLevel).SequenceEqual(new int?[] { 1, 2, 0 }));
            True(c.Snapshot.Points.Select(p => p.MeasurementToken).SequenceEqual(raw) && f.Saves == 0);
        });
        await Check("representative_copy_colors_target_cells_only_after_actual_apply", async () =>
        {
            var (f, c) = Fixture();
            f.Source = f.Source with
            {
                Points = f.Source.Points.Select(p => p.Key == "P1"
                    ? p with { ErrorXUm = 12, OriginalMeasurement = p.OriginalMeasurement! with { ErrorXUm = 12 } }
                    : p with { CellNo = p.CellNo + p.ShotNo, HeadNo = p.ShotNo == 2 ? 8 : 2 }).ToArray()
            };
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.CopyReference, ["P2", "P3"], r => r with { ReferenceKey = "P1", Reason = "Cross Cell representative" });
            True(c.Snapshot.Points.Where(p => p.Key != "P1").All(p => p.WorkingXUm == 1 && p.ReferenceFinalXUm == 12));
            True(f.Saves==0);foreach(var cell in new[]{3,4})await Do(c,EN_MANUAL_INSPECTION_UI_ACTION.BulkApply,[],r=>r with{CurrentCellNo=cell});
            True(c.Snapshot.Points.Where(p => p.Key != "P1").All(p => p.BaseXUm == 12 && p.AppliedLevel == 2 && p.HasAppliedOffset && p.OriginalXUm == 1));
            True(!c.Snapshot.Points.Single(p => p.Key == "P1").HasAppliedOffset && f.Saves == 2);
        });
        await Check("import_merge_preserves_native_when_own_saved_values_are_identical", async () =>
        {
            var (engine, points) = EngineFixture();
            var old = points[0]; var incoming = Import(old, 3, DateTimeOffset.UtcNow.AddMinutes(1));
            True(engine.MergeImportedObservations([incoming], new Dictionary<string, DateTimeOffset> { [old.Key] = incoming.OriginalMeasurement!.MeasuredAt }) == 0);
            True(engine.GetPoint(old.Key).Source == "MEASURED" && engine.GetPoint(old.Key).OriginalMeasurement!.RawToken == old.OriginalMeasurement!.RawToken);
            await Task.CompletedTask;
        });
        await Check("import_merge_newer_value_invalidates_candidate_and_keeps_ledger", async () =>
        {
            var (engine, points) = EngineFixture();
            engine.SeedConsumedMeasurements([new(points[0].Key, "HISTORICAL-RAW")]);
            engine.BuildDirectCandidate([points[1].Key]); True(engine.ValidateCandidate(false).Ok);
            var incoming = Import(points[0], 9, DateTimeOffset.UtcNow.AddMinutes(1));
            True(engine.MergeImportedObservations([incoming], new Dictionary<string, DateTimeOffset> { [incoming.Key] = incoming.OriginalMeasurement!.MeasuredAt }) == 1);
            True(engine.WasConsumed(points[0].Key, "HISTORICAL-RAW") && engine.GetPoint(points[0].Key).Source == "IMPORTED");
            True(engine.Candidate is { State: EN_MANUAL_CANDIDATE_STATE.Draft, Validation: null }
                && engine.History.Any(h => h.Key == points[0].Key && h.Kind == "EXTERNAL_OBSERVATION"));
            await Task.CompletedTask;
        });
        await Check("import_merge_rejects_wrong_geometry_or_run_before_mutation", async () =>
        {
            var (engine, points) = EngineFixture();
            var incoming = Import(points[0], 9, DateTimeOffset.UtcNow.AddMinutes(1));
            var times = new Dictionary<string, DateTimeOffset> { [incoming.Key] = incoming.OriginalMeasurement!.MeasuredAt };
            await Throws(() => Task.Run(() => engine.MergeImportedObservations([incoming with { XMm = incoming.XMm + 1 }], times)));
            await Throws(() => Task.Run(() => engine.MergeImportedObservations([incoming with { OriginalMeasurement = incoming.OriginalMeasurement! with { RunId = "WRONG-RUN" } }], times)));
            True(engine.GetPoint(points[0].Key) == points[0]);
        });
        await Check("import_merge_edited_value_does_not_invent_original_or_direct_anchor", async () =>
        {
            var (engine, points) = EngineFixture();
            var incoming = Import(points[0], 9, DateTimeOffset.UtcNow.AddMinutes(1)) with { OriginalMeasurement = null };
            True(engine.MergeImportedObservations([incoming], new Dictionary<string, DateTimeOffset> { [incoming.Key] = DateTimeOffset.UtcNow.AddMinutes(1) }) == 1);
            var result = engine.GetPoint(points[0].Key);
            True(result.OriginalMeasurement is null && !result.IsDirectMeasurement && result.ErrorXUm == 9);
            await Throws(() => Task.Run(() => engine.PreviewReferenceCopy(result.Key, [points[1].Key])));
        });
        await Check("edited_observation_timestamp_blocks_older_values_after_session_restore", async () =>
        {
            var (engine, points) = EngineFixture();
            var now = DateTimeOffset.UtcNow.AddMinutes(1);
            var edited = Import(points[0], 12, now) with { OriginalMeasurement = null };
            True(engine.MergeImportedObservations([edited], new Dictionary<string, DateTimeOffset> { [edited.Key] = now }) == 1);
            True(engine.GetPoint(edited.Key).ExternalObservedAt == now && engine.GetPoint(edited.Key).OriginalMeasurement is null);
            var session = engine.CaptureSession();
            var restored = new CManualInspectionWorkspace();
            restored.RestoreSession(session, session.Context, points, session.ActiveValuesByPointKey, (_, _, _) => new(true, "synthetic field"));
            var older = edited with { ErrorXUm = 9 };
            True(restored.MergeImportedObservations([older], new Dictionary<string, DateTimeOffset> { [older.Key] = now.AddSeconds(-1) }) == 0);
            True(restored.GetPoint(edited.Key).ErrorXUm == 12 && restored.GetPoint(edited.Key).OriginalMeasurement is null
                && restored.GetPoint(edited.Key).ExternalObservedAt == now);
            await Task.CompletedTask;
        });
        await Check("import_merge_does_not_erase_incomplete_operation", async () =>
        {
            var (engine, points) = EngineFixture();
            var op = engine.BeginOperation(engine.PreviewOperation(EN_MANUAL_OPERATION_KIND.MoveMeasure, [points[0].Key]));
            engine.CompleteOperation(op.Id, EN_MANUAL_OPERATION_STATE.Stopped, "synthetic stop");
            var incoming = Import(points[0], 9, DateTimeOffset.UtcNow.AddMinutes(1));
            True(engine.MergeImportedObservations([incoming], new Dictionary<string, DateTimeOffset> { [incoming.Key] = incoming.OriginalMeasurement!.MeasuredAt }) == 0);
            True(engine.HasIncompleteMeasurements && engine.Operation!.RemainingKeys.SequenceEqual([points[0].Key]) && engine.GetPoint(points[0].Key) == points[0]);
            await Task.CompletedTask;
        });
        await Check("file_monitor_coalesces_csv_updates_and_ignores_unrelated_files", async () =>
        {
            var directory = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var count = 0;
            using var monitor = new CManualInspectionUpdateMonitor([directory], () => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(100));
            File.WriteAllText(Path.Combine(directory, "unrelated.txt"), "no CSV"); await Task.Delay(250); True(count == 0);
            var csv = Path.Combine(directory, "result.csv");
            for (var i = 0; i < 20; i++) File.WriteAllText(csv, "synthetic " + i);
            await Until(() => Volatile.Read(ref count) > 0);
            await Task.Delay(250); True(count < 20);
            var before = Volatile.Read(ref count); File.Move(csv, Path.Combine(directory, "renamed.csv"));
            await Until(() => Volatile.Read(ref count) > before);
            before = Volatile.Read(ref count); File.Delete(Path.Combine(directory, "renamed.csv"));
            await Until(() => Volatile.Read(ref count) > before);
        });
        await Check("file_monitor_handles_initially_missing_root_and_disposal", async () =>
        {
            var ancestor = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(ancestor);
            var directory = Path.Combine(ancestor, "Review"); var count = 0;
            var monitor = new CManualInspectionUpdateMonitor([directory], () => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(100));
            Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "new.csv"), "synthetic");
            await Until(() => Volatile.Read(ref count) > 0);
            monitor.Dispose(); var before = Volatile.Read(ref count); File.WriteAllText(Path.Combine(directory, "after-dispose.csv"), "synthetic");
            await Task.Delay(250); True(count == before);
        });
        await Check("file_monitor_observes_whole_result_directory_moves", async () =>
        {
            var ancestor = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(ancestor);
            var incoming = Path.Combine(ancestor, "incoming"); Directory.CreateDirectory(incoming);
            File.WriteAllText(Path.Combine(incoming, "result.csv"), "synthetic");
            var review = Path.Combine(ancestor, "Review"); Directory.CreateDirectory(review);
            var count = 0;
            using var monitor = new CManualInspectionUpdateMonitor([review], () => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(100));
            var target = Path.Combine(review, "ImportedBatch"); Directory.Move(incoming, target);
            await Until(() => Volatile.Read(ref count) > 0);
            var before = Volatile.Read(ref count); Directory.Move(target, Path.Combine(ancestor, "archived"));
            await Until(() => Volatile.Read(ref count) > before);
        });
#if ACTUAL_ASSEMBLIES
        await Check("actual_general_result_writer_preserves_known_simulation_mode", async () =>
        {
            var root = Path.Combine(Root, Guid.NewGuid().ToString("N"), "Config"); Directory.CreateDirectory(root);
            var files = new Drilling.File.ReviewResult.CReviewResultFile(root);
            foreach (var simulation in new[] { false, true })
            {
                var point = new Drilling.Common.Review.ST_REVIEW_PLAN_POINT(1, "CELL1_A1", 1, 1, 1, 1, 1, true, 1, 2, 3, 4, .001, -.002, Drilling.Common.Review.EN_REVIEW_POINT_STATE.Ok, "OK") { BeamNo = 11 };
                var plan = new Drilling.Common.Review.ST_REVIEW_PLAN("QA", 1, .005, .005, default, DateTimeOffset.UtcNow, [point]);
                await files.Save(new(plan, [point], DateTimeOffset.Now, "GLASS-QA") { IsStageSimulation = simulation, IsVisionSimulation = simulation });
                var result = await files.Load(files.LastSavedPath!);
                True(result.IsStageSimulation == simulation && result.IsVisionSimulation == simulation && result.Rows.Single().ResultSource == "MEASURED");
                var headers = File.ReadLines(files.LastSavedPath!).First();
                True(headers.Contains("STAGE_SIMULATION") && headers.Contains("VISION_SIMULATION"));
            }
        });
        await Check("actual_legacy_or_mixed_mode_is_unknown_not_assumed_real", async () =>
        {
            var root = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var files = new Drilling.File.ReviewResult.CReviewResultFile(Path.Combine(root, "Config"));
            string[] headers = ["SAVED_AT", "RECIPE_ID", "GLASS_ID", "SHOT_KEY", "SHOT_NAME", "HEAD_NO", "CELL_NO", "JUDGE", "STAGE_SIMULATION", "VISION_SIMULATION"];
            IReadOnlyDictionary<string, string> Row(string stage, string vision, string shot) => new Dictionary<string, string>
            { ["SAVED_AT"] = DateTimeOffset.UtcNow.ToString("O"), ["RECIPE_ID"] = "QA", ["GLASS_ID"] = "G", ["SHOT_KEY"] = "CELL1_" + shot, ["SHOT_NAME"] = shot, ["HEAD_NO"] = "1", ["CELL_NO"] = "1", ["JUDGE"] = "OK", ["STAGE_SIMULATION"] = stage, ["VISION_SIMULATION"] = vision };
            var legacy = Path.Combine(root, "legacy.csv");
            WriteFixture(legacy, [Row("", "", "A1")]);
            var unknown = await files.Load(legacy); True(unknown.IsStageSimulation is null && unknown.IsVisionSimulation is null);
            var mixed = Path.Combine(root, "mixed.csv");
            WriteFixture(mixed, [Row("OFF", "OFF", "A1"), Row("ON", "OFF", "A2")]);
            var result = await files.Load(mixed); True(result.IsStageSimulation is null && result.IsVisionSimulation == false);
            void WriteFixture(string path, IReadOnlyList<IReadOnlyDictionary<string,string>> rows)
                => File.WriteAllLines(path, new[] { string.Join(",",headers) }.Concat(rows.Select(row => string.Join(",",headers.Select(name => row[name])))));
        });
#endif
    }

    private static void SetIncoming(FakeBackend f, string identity, double x, DateTimeOffset observedAt)
    {
        var point = f.Source.Points.Single(p => p.Key == "P1");
        var raw = new ST_MANUAL_MEASUREMENT("OK", x, -1, "IMPORTED-" + identity, f.Source.Context.RecipeId, f.Source.Context.RunId, f.Source.Context.CoordinateSignature)
        { MeasuredAt = observedAt, RawData = "Imported synthetic " + identity };
        f.Source = f.Source with
        {
            Points = f.Source.Points.Select(p => p.Key == point.Key ? p with { Source = "IMPORTED", Judge = "OK", ErrorXUm = x, ErrorYUm = -1, OriginalMeasurement = raw, MeasurementVersion = 0 } : p).ToArray(),
            ImportedResults = new Dictionary<string, CManualInspectionImportedResult> { [point.Key] = new(identity + ".csv", observedAt, "FINE · external synthetic") },
            ObservationFingerprint = identity
        };
    }

    private static (CManualInspectionWorkspace Engine, ST_MANUAL_POINT[] Points) EngineFixture()
    {
        var f = new FakeBackend(Path.Combine(Root, Guid.NewGuid().ToString("N")), false, false);
        var engine = new CManualInspectionWorkspace();
        engine.Load(f.Source.Context, f.Source.Points, f.Source.Values, f.Source.Limits, f.Source.FieldEvaluator);
        return (engine, f.Source.Points.ToArray());
    }

    private static ST_MANUAL_POINT Import(ST_MANUAL_POINT previous, double x, DateTimeOffset observedAt)
        => previous with { Source = "IMPORTED", ErrorXUm = x, OriginalMeasurement = previous.OriginalMeasurement! with { ErrorXUm = x, MeasuredAt = observedAt, RawToken = "EXTERNAL-RAW" } };

    private static async Task Until(Func<bool> condition)
    {
        var end = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition()) { if (DateTimeOffset.UtcNow > end) throw new TimeoutException("Synthetic file change was not observed."); await Task.Delay(20); }
    }
}
