using System.Globalization;
using System.Text.Json;
using Drilling.Common.Recipe;
using Drilling.Common.Review;
using Drilling.Common.Review.ManualInspection;
using Drilling.File.ReviewOffset;

internal static partial class Program
{
    private static readonly List<object> Results = [];
    private static int Failed;
    private static object? OptionalSourcePlan;
    private static readonly string WorkRoot = Path.Combine(Path.GetTempPath(), "A3ManualInspectionWriterTests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset FixedTime = new(2026, 11, 20, 12, 0, 0, TimeSpan.FromHours(9));

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--probe-store-load")
        {
            File.WriteAllText(args[2] + ".started", "ready");
            await new CReviewOffsetFile(args[1]).LoadLatest("QA");
            File.WriteAllText(args[2], "loaded");
            return 0;
        }
        await Check("vision_headflip_direction_matrix_128", () =>
        {
            var cases = 0;
            for (var v = 0; v < 8; v++) for (var h = 0; h < 8; h++) foreach (var direction in new[] { -1, 1 })
            {
                var p = Parameters(h, direction); var expected = Flip(.0013, -.0027, v);
                var actual = CManualInspectionCoordinateBridge.FromVision(.0013, -.0027, (EN_VISION_FLIP_TYPE)v, p, 1);
                Near(actual.XUm, expected.X * 1000); Near(actual.YUm, expected.Y / direction * 1000); cases++;
            }
            True(cases == 128);
        });
        await Check("stage_result_does_not_apply_vision_flip_twice", () =>
        {
            var p = Parameters(7, -1); var stage = Flip(.0013, -.0027, 5);
            var raw = CManualInspectionCoordinateBridge.FromVision(.0013, -.0027, (EN_VISION_FLIP_TYPE)5, p, 1);
            var csv = CManualInspectionCoordinateBridge.FromStage(stage.X, stage.Y, p, 1);
            Near(raw.XUm, csv.XUm); Near(raw.YUm, csv.YUm);
            var doubleFlip = CManualInspectionCoordinateBridge.FromVision(stage.X, stage.Y, (EN_VISION_FLIP_TYPE)5, p, 1);
            True(Math.Abs(doubleFlip.XUm - raw.XUm) > .1 || Math.Abs(doubleFlip.YUm - raw.YUm) > .1);
        });
        await Check("legacy_scanner_offsets_decode_with_inverse_head_transform", () =>
        {
            for (var h = 0; h < 8; h++) foreach (var direction in new[] { -1, 1 })
            {
                var p = Parameters(h, direction); var row = new ST_REVIEW_OFFSET_ROW(1, "A1", .0013, -.0027);
                var x = row.ReviewOffsetX; var y = row.ReviewOffsetY;
                if ((h & 1) != 0) x = -x; if ((h & 2) != 0) y = -y; if ((h & 4) != 0) (x, y) = (y, x);
                var actual = CManualInspectionCoordinateBridge.FromFile(row, p, 1); Near(actual.XUm, x * 1000); Near(actual.YUm, y / direction * 1000);
            }
        });
        await Check("design_file_offsets_have_no_double_axis_conversion", () =>
        {
            var actual = CManualInspectionCoordinateBridge.FromFile(new(1, "A1", .0013, -.0027) { IsDesignCoordinate = true }, Parameters(7, -1), 1);
            Near(actual.XUm, 1.3); Near(actual.YUm, -2.7);
        });
        await Check("to_file_converts_um_to_design_mm_and_matrix_identity", () =>
        {
            var p = new ST_MANUAL_POINT("QA", 2, 1, 28, 11, 0, 0); var row = CManualInspectionCoordinateBridge.ToFile(p, 27, new(1.234567, -2.345678));
            True(row.CellId == 2 && row.ShotId == "A2" && row.IsDesignCoordinate); Near(row.ReviewOffsetX, .001234567); Near(row.ReviewOffsetY, -.002345678);
        });
        await Check("nonfinite_raw_and_file_output_rejected", () =>
        {
            Throws(() => CManualInspectionCoordinateBridge.FromStage(double.NaN, 0, Parameters(), 1));
            Throws(() => CManualInspectionCoordinateBridge.FromVision(0, double.PositiveInfinity, 0, Parameters(), 1));
            Throws(() => CManualInspectionCoordinateBridge.ToFile(new("QA", 1, 1, 1, 11, 0, 0), 1, new(double.NaN, 0)));
        });
        await Check("mof_following_y_removed_and_review_scale_applied_once_48", () =>
        {
            var cases = 0;
            for (var h = 0; h < 8; h++) foreach (var direction in new[] { -1, 1 }) foreach (var scale in new[] { 0d, .5, 1.5 })
            {
                var p = Parameters(h, direction); p["REVIEW_OFFSET_SCALE"] = (scale * 100).ToString("R", CultureInfo.InvariantCulture);
                var follow = Flip(1.25, -1200, h); var shift = Flip(.4, 0, h); var oldReview = Flip(.003 * scale, -.002 * direction * scale, h);
                var oldFinalX = .11 + oldReview.X; var oldFinalY = -.12 + oldReview.Y;
                var shot = Shot(follow.X + shift.X + oldFinalX, follow.Y + shift.Y + oldFinalY, oldFinalX, oldFinalY, oldReview.X, oldReview.Y);
                var actual = CManualInspectionCoordinateBridge.EvaluateField(shot, new(3, -2), new(13, -22), p, new(100, 1000, 5));
                var stationary = Flip(1.65, 0, h); var delta = Flip(.010 * scale, -.020 * direction * scale, h);
                True(actual.Ok); Near(actual.ScannerXMm!.Value, stationary.X + oldFinalX + delta.X); Near(actual.ScannerYMm!.Value, stationary.Y + oldFinalY + delta.Y); cases++;
            }
            True(cases == 48);
        });
        await Check("field_includes_current_static_offsets_and_new_delta", () =>
        {
            var p = Parameters(); var shot = Shot(1 + .02, -1300 + .03, .02, .03, .002, -.003);
            var result = CManualInspectionCoordinateBridge.EvaluateField(shot, new(2, 3), new(12, 23), p, new(100, 1000, 5));
            True(result.Ok); Near(result.ScannerXMm!.Value, 1.03); Near(result.ScannerYMm!.Value, .01);
        });
        await Check("field_rejects_missing_actual_head_width", () =>
        {
            var p = Parameters(); p.Remove("H01_SCAN_FIELD_WIDTH_X"); True(!CManualInspectionCoordinateBridge.EvaluateField(Shot(0, -1000, 0, 0), new(0, 0), new(1, 1), p, new(100, 1000, 5)).Ok);
        });
        await Check("field_rejects_stationary_offset_and_negative_scale", () =>
        {
            var p = Parameters(); True(!CManualInspectionCoordinateBridge.EvaluateField(Shot(3, -1000, 0, 0), new(0, 0), new(0, 0), p, new(100, 1000, 5)).Ok);
            p["REVIEW_OFFSET_SCALE"] = "-1"; True(!CManualInspectionCoordinateBridge.EvaluateField(Shot(0, -1000, 0, 0), new(0, 0), new(0, 0), p, new(100, 1000, 5)).Ok);
        });
        await Check("field_uses_smaller_user_or_actual_width", () =>
        {
            var p = Parameters(); p["H01_SCAN_FIELD_WIDTH_X"] = "2"; True(!CManualInspectionCoordinateBridge.EvaluateField(Shot(1.01, -1000, 0, 0), new(0, 0), new(0, 0), p, new(100, 1000, 5)).Ok);
        });
        var optionalParameters = Environment.GetEnvironmentVariable("A3_MANUAL_QA_PARAMETERS");
        if (!string.IsNullOrWhiteSpace(optionalParameters)) await Check("optional_source_plan_stationary_field_matches_independent_encoder_model", () =>
        {
            var p = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(optionalParameters))!;
            var plan = CShotCoordinatePlanBuilder.Build(p); var outputs = new List<(ST_HEAD_SHOT_POINT Point, ST_MANUAL_FIELD_RESULT Field)>();
            var masking = CRecipeShotMaskingPlan.Build(p);
            foreach (var shot in plan.Shots.Where(s => s.HeadNo > 0))
            {
                var prefix = $"CELL{shot.CellNo}_{CReviewShotNameFormatter.ToMatrixName(shot.CellShotNo, shot.GroupCountX)}_REVIEW_OFFSET_";
                var x = p.TryGetValue(prefix + "X", out var sx) ? double.Parse(sx, CultureInfo.InvariantCulture) : 0;
                var y = p.TryGetValue(prefix + "Y", out var sy) ? double.Parse(sy, CultureInfo.InvariantCulture) : 0;
                var design = CReviewOffsetData.ToDesign(p, shot.HeadNo, x, y, CReviewOffsetData.IsDesignCoordinate(p, prefix));
                var value = new ST_MANUAL_OFFSET(design.X * 1000, design.Y * 1000);
                var width = double.Parse(p[$"H{shot.HeadNo:00}_SCAN_FIELD_WIDTH_X"], CultureInfo.InvariantCulture);
                var actual = CManualInspectionCoordinateBridge.EvaluateField(shot, value, value, p, new(100, 1000, width));
                var flags = (IsOn(p, $"H{shot.HeadNo:00}_SCANNER_X_FLIP") ? 1 : 0)
                    | (IsOn(p, $"H{shot.HeadNo:00}_SCANNER_Y_FLIP") ? 2 : 0) | (IsOn(p, $"H{shot.HeadNo:00}_SCANNER_XY_FLIP") ? 4 : 0);
                var headScale = p.TryGetValue($"H{shot.HeadNo:00}_ENCODER_SCALE", out var hs)
                    ? double.Parse(hs, CultureInfo.InvariantCulture) : plan.EncoderScale;
                var following = Flip(0, -shot.EncoderWaitPosition / Math.Abs(headScale), flags);
                var expectedX = shot.ScannerGx - following.X; var expectedY = shot.ScannerGy - following.Y;
                Near(actual.ScannerXMm!.Value, expectedX); Near(actual.ScannerYMm!.Value, expectedY);
                True(actual.Ok == (Math.Abs(expectedX) <= width / 2 && Math.Abs(expectedY) <= width / 2));
                outputs.Add((shot, actual));
            }
            var executable = outputs.Where(o => !masking.EvaluateShot(o.Point).ShouldSkipLaser).ToArray();
            OptionalSourcePlan = new { shots = plan.Shots.Count, evaluated = outputs.Count, cells = plan.CellCount,
                outside = outputs.Count(o => !o.Field.Ok), maxStationaryAbsXmm = outputs.Max(o => Math.Abs(o.Field.ScannerXMm ?? 0)),
                maxStationaryAbsYmm = outputs.Max(o => Math.Abs(o.Field.ScannerYMm ?? 0)),
                executable = executable.Length, excludedMaskingOrChess = outputs.Count - executable.Length,
                executableOutside = executable.Count(o => !o.Field.Ok),
                executablePredicate = "Production Build excludes disabled Head; CreateAllPlan/BuildReviewExcludedShots excludes MaskingPlan.EvaluateShot.ShouldSkipLaser. Manager not instantiated.",
                failedKeys = outputs.Where(o => !o.Field.Ok).Take(12).Select(o => o.Point.ShotKey).ToArray() };
            True(outputs.Count > 0);
        });

        await CheckAsync("cas_initial_empty_revision_writes_exact_design_values", async () =>
        {
            var f = Fixture(); var source = Source(f.Root, "one"); var saved = await f.Store.SaveNewIfUnchanged("QA", "", Rows(1.25, -2.5), source);
            var actual = await f.Store.LoadLatest("QA"); True(actual.FilePath == saved.FilePath && actual.Rows.Single().IsDesignCoordinate); Near(actual.Rows[0].ReviewOffsetX, .00125);
            True(await f.Store.FindSourceApplication("QA", source) is not null);
        });
        await CheckAsync("cas_stale_expected_path_never_overwrites_new_revision", async () =>
        {
            var f = Fixture(); var first = await f.Store.SaveNew("QA", Rows(0, 0)); var newer = await f.Store.SaveNew("QA", Rows(2, 2));
            await ThrowsAsync(() => f.Store.SaveNewIfUnchanged("QA", first.FilePath, Rows(3, 3), Source(f.Root, "stale")));
            var actual = await f.Store.LoadLatest("QA"); True(actual.FilePath == newer.FilePath && ActiveCount(newer) == 2); Near(actual.Rows[0].ReviewOffsetX, .002);
        });
        await CheckAsync("cas_empty_expected_rejected_if_active_exists", async () => { var f = Fixture(); var first = await f.Store.SaveNew("QA", Rows(0, 0)); await ThrowsAsync(() => f.Store.SaveNewIfUnchanged("QA", "", Rows(1, 1), Source(f.Root, "empty"))); True(ActiveCount(first) == 1); });
        await CheckAsync("cas_same_source_duplicate_blocked_across_days", async () =>
        {
            var f = Fixture(); var source = Source(f.Root, "same"); var saved = await f.Store.SaveNewIfUnchanged("QA", "", Rows(1, 1), source);
            var nextDay = new CReviewOffsetFile(Path.Combine(f.Root, "Config"), () => FixedTime.AddDays(1));
            await ThrowsAsync(() => nextDay.SaveNewIfUnchanged("QA", saved.FilePath, Rows(2, 2), source)); True(ActiveCount(saved) == 1);
        });
        await CheckAsync("cas_missing_source_does_not_create_revision", async () => { var f = Fixture(); await ThrowsAsync(() => f.Store.SaveNewIfUnchanged("QA", "", Rows(1, 1), Path.Combine(f.Root, "missing.csv"))); True(!Directory.Exists(f.Store.RootPath)); });
        await CheckAsync("cas_cancelled_token_has_no_files", async () => { var f = Fixture(); var source = Source(f.Root, "cancel"); using var c = new CancellationTokenSource(); c.Cancel(); await ThrowsAsync(() => f.Store.SaveNewIfUnchanged("QA", "", Rows(1, 1), source, c.Token)); True(!Directory.Exists(f.Store.RootPath)); });
        await CheckAsync("cas_invalid_rows_never_remove_previous_revision", async () =>
        {
            var f = Fixture(); var first = await f.Store.SaveNew("QA", Rows(0, 0));
            await ThrowsAsync(() => f.Store.SaveNewIfUnchanged("QA", first.FilePath, new[] { new ST_REVIEW_OFFSET_ROW(1, "A1", double.NaN, 0) }, Source(f.Root, "invalid")));
            True((await f.Store.LoadLatest("QA")).FilePath == first.FilePath && ActiveCount(first) == 1);
        });
        await CheckAsync("cas_history_failure_removes_only_new_offset", async () =>
        {
            var f = Fixture(); var first = await f.Store.SaveNew("QA", Rows(0, 0)); var directory = Path.GetDirectoryName(first.FilePath)!;
            File.WriteAllText(Path.Combine(directory, "_apply"), "synthetic blocked directory");
            await ThrowsAsync(() => f.Store.SaveNewIfUnchanged("QA", first.FilePath, Rows(1, 1), Source(f.Root, "history-error")));
            True((await f.Store.LoadLatest("QA")).FilePath == first.FilePath && ActiveCount(first) == 1);
            True(!Directory.EnumerateFiles(directory, "*.tmp", SearchOption.AllDirectories).Any());
        });
        await CheckAsync("cas_same_instance_concurrent_compare_allows_one_writer", async () =>
        {
            var f = Fixture(); var first = await f.Store.SaveNew("QA", Rows(0, 0)); var s1 = Source(f.Root, "parallel1"); var s2 = Source(f.Root, "parallel2");
            var outcomes = await Task.WhenAll(new[] { s1, s2 }.Select((source, i) => Task.Run(async () => { try { await f.Store.SaveNewIfUnchanged("QA", first.FilePath, Rows(i + 1, i + 1), source); return true; } catch (InvalidOperationException) { return false; } })));
            True(outcomes.Count(x => x) == 1 && ActiveCount(first) == 2);
        });
        await CheckAsync("cas_returned_values_equal_persisted_fractional_um", async () =>
        {
            var f = Fixture(); var saved = await f.Store.SaveNewIfUnchanged("QA", "", Rows(1.234567, -2.345678), Source(f.Root, "fractional")); var actual = await f.Store.LoadLatest("QA");
            Near(actual.Rows[0].ReviewOffsetX * 1000, 1.235); Near(actual.Rows[0].ReviewOffsetY * 1000, -2.346);
            Near(saved.Rows[0].ReviewOffsetX, actual.Rows[0].ReviewOffsetX, 1e-12); Near(saved.Rows[0].ReviewOffsetY, actual.Rows[0].ReviewOffsetY, 1e-12);
        });
        await CheckAsync("legacy_save_still_creates_new_files_without_source", async () =>
        {
            var f = Fixture(); var a = await f.Store.SaveNew("QA", Rows(1, 1)); var b = await f.Store.SaveNew("QA", Rows(2, 2)); True(a.FilePath != b.FilePath && ActiveCount(b) == 2);
            var rolled = await f.Store.RollbackLatest("QA", b.FilePath); True(rolled.Current.FilePath == a.FilePath && File.Exists(rolled.ArchivedFilePath));
        });

        await RunHardeningCases();
        var output = args.Length == 2 && args[0] == "--report" ? args[1] : Path.Combine(AppContext.BaseDirectory, "bridge-writer-results.json");
        var result = new { schemaVersion = "a3-manual-inspection-bridge-writer-tests/1", passed = Failed == 0, count = Results.Count, failed = Failed, tests = Results, optionalSourcePlan = OptionalSourcePlan,
            scope = "New production coordinate bridge, original pure planner/axis transform, production offset writer and CSV parser. Source-derived fixture contracts/static Vision conversion only. Synthetic temporary CSVs; no device, WPF, SDK or scripts." };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Console.WriteLine($"Bridge/writer tests: {Results.Count - Failed}/{Results.Count} passed. Report: {output}"); return Failed == 0 ? 0 : 1;
    }

    private static Dictionary<string, string> Parameters(int flags = 0, int direction = -1) => new(StringComparer.OrdinalIgnoreCase)
    { ["H01_SCANNER_X_FLIP"] = (flags & 1) != 0 ? "ON" : "OFF", ["H01_SCANNER_Y_FLIP"] = (flags & 2) != 0 ? "ON" : "OFF", ["H01_SCANNER_XY_FLIP"] = (flags & 4) != 0 ? "ON" : "OFF", ["STAGE_SCAN_DIRECTION_Y"] = direction.ToString(CultureInfo.InvariantCulture), ["H01_SCAN_FIELD_WIDTH_X"] = "110", ["REVIEW_OFFSET_SCALE"] = "100" };
    private static (double X, double Y) Flip(double x, double y, int flags) { if ((flags & 4) != 0) (x, y) = (y, x); if ((flags & 1) != 0) x = -x; if ((flags & 2) != 0) y = -y; return (x, y); }
    private static bool IsOn(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var text) && text.Trim().ToUpperInvariant() is "ON" or "TRUE" or "1" or "YES";
    private static ST_HEAD_SHOT_POINT Shot(double gx, double gy, double ox, double oy, double reviewX = 0, double reviewY = 0) => new(1, "CELL1_A1", 1, 1, 1, 0, 0, 1, 1, 0, 0, gx, gy, 0, 0, 0, 0, 0, 0, 0, 0, reviewX, reviewY, 0, 0, 0, 0, ox, oy, 0);
    private static ST_REVIEW_OFFSET_ROW[] Rows(double xUm, double yUm) => [new(1, "A1", xUm / 1000, yUm / 1000) { IsDesignCoordinate = true }];
    private static (string Root, CReviewOffsetFile Store) Fixture() { var root = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return (root, new(Path.Combine(root, "Config"), () => FixedTime)); }
    private static string Source(string root, string name) { var file = Path.Combine(root, "Data", name + ".csv"); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "Synthetic QA source,not equipment data\n1,2\n"); return file; }
    private static int ActiveCount(ST_REVIEW_OFFSET_FILE_DATA file) => Directory.EnumerateFiles(Path.GetDirectoryName(file.FilePath)!, "*_reviewoffset.csv", SearchOption.TopDirectoryOnly).Count();
    private static void Near(double actual, double expected, double tolerance = 1e-8) { if (!double.IsFinite(actual) || Math.Abs(actual - expected) > tolerance) throw new InvalidOperationException($"Expected {expected:R}, actual {actual:R}"); }
    private static void True(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
    private static void Throws(Action action) { var failed = false; try { action(); } catch { failed = true; } if (!failed) throw new InvalidOperationException("Expected rejection"); }
    private static async Task ThrowsAsync(Func<Task> action) { var failed = false; try { await action(); } catch { failed = true; } if (!failed) throw new InvalidOperationException("Expected rejection"); }
    private static Task Check(string name, Action body) => CheckAsync(name, () => { body(); return Task.CompletedTask; });
    private static async Task CheckAsync(string name, Func<Task> body) { try { await body(); Results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); } catch (Exception e) { Failed++; Results.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); } }
}
