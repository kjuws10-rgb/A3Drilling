using System.Text.Json;
using Drilling.Common.Review;

internal static class Program
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-20T10:00:00+09:00");
    private static readonly ST_MANUAL_RESULT_FRAME Frame = new("R", "GLASS-1", Start, 1, 2, false);
    private static readonly List<object> Results = [];
    private static int Failed;
    private static ST_REVIEW_PLAN_POINT Target(int shot = 1, int cell = 1, int beam = 11)
        => new(shot, $"CELL{cell}_SHOT{shot}", 1, cell, shot, 20, 20, true, shot, 2, 1, 2, 0, 0, EN_REVIEW_POINT_STATE.Ready, "WAIT") { BeamNo = beam };
    private static ST_REVIEW_RESULT_FILE_ROW Row(int shot = 1, int cell = 1, int beam = 11, string source = "MEASURED", string judge = "OK", int minute = 1)
        => new(Start.AddMinutes(minute), "R", $"CELL{cell}_SHOT{shot}", CReviewShotNameFormatter.ToMatrixName(shot, 20), 1, cell, shot / 1000.0, -.002, judge)
        { BeamNo = beam, DesignX = shot, DesignY = 2, ResultSource = source };
    private static ST_REVIEW_RESULT_FILE_DATA File(string path, params ST_REVIEW_RESULT_FILE_ROW[] rows)
        => new(path, Path.GetFileName(path), "R", rows.Max(r => r.SavedAt), rows)
        { GlassId = "GLASS-1", ShiftOffsetX = 1, ShiftOffsetY = 2, IsStageSimulation = false, IsVisionSimulation = false, BeamNo = 11 };
    private static ST_MANUAL_RESULT_PROJECTION Merge(params ST_REVIEW_RESULT_FILE_DATA[] files)
        => CManualInspectionResultProjection.Merge(Frame, files.Select(f => new ST_MANUAL_EXTERNAL_RESULT(f, "TEST")), [Target(), Target(2), Target(1, 2), Target(1, 1, 1)]);
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }
    private static void Check(string name, Action action)
    {
        try { action(); Results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Failed++; Results.Add(new { name, passed = false, error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static int Main(string[] args)
    {
        Check("same_current_frame_accepted", () => True(Merge(File("rough.csv", Row())).Rows.Count == 1));
        Check("different_menus_merge_distinct_coordinates", () => True(Merge(File("rough.csv", Row()), File("fine.csv", Row(2)), File("auto.csv", Row(cell: 2))).Rows.Count == 3));
        Check("latest_actual_observation_wins", () => True(Merge(File("old.csv", Row()), File("new.csv", Row(minute: 3) with { ErrorX = .004 })).Rows.Values.Single().Row.ErrorX == .004));
        Check("latest_error_is_visible_not_old_ok", () => True(Merge(File("old.csv", Row()), File("failed.csv", Row(source: "ERROR", judge: "ERROR", minute: 3))).Rows.Values.Single().Row.Judge == "ERROR"));
        Check("unmeasured_does_not_erase_actual_measurement", () => True(Merge(File("old.csv", Row()), File("new.csv", Row(source: "UNMEASURED", judge: "UNMEASURED", minute: 3))).Rows.Values.Single().FilePath == "old.csv"));
        Check("retained_new_save_time_cannot_overwrite_actual", () => True(Merge(File("old.csv", Row()), File("retained.csv", Row(source: "RETAINED", minute: 3))).Rows.Values.Single().FilePath == "old.csv"));
        Check("retained_has_no_current_run_fallback", () => True(Merge(File("retained.csv", Row(source: "RETAINED"))).Rows.Count == 0));
        Check("manual_edit_visible_without_measurement_promotion", () => True(Merge(File("edited.csv", Row(source: "EDITED"))).Rows.Values.Single().Row.ResultSource == "EDITED"));
        Check("manual_copy_result_source_preserved", () => True(Merge(File("manual.csv", Row(source: "MANUAL"))).Rows.Values.Single().Row.ResultSource == "MANUAL"));
        Check("unknown_source_is_not_auto_linked", () => True(Merge(File("unknown.csv", Row(source: ""))).Rows.Count == 0));
        Check("different_recipe_excluded", () => True(Merge(File("f.csv", Row()) with { RecipeId = "OTHER" }).RejectedFileCount == 1));
        Check("different_glass_excluded", () => True(Merge(File("f.csv", Row()) with { GlassId = "GLASS-2" }).Rows.Count == 0));
        Check("unlinked_current_glass_disables_auto_merge", () => True(CManualInspectionResultProjection.Merge(Frame with { GlassId = "UNLINKED" }, [new(File("f.csv", Row()), "TEST")], [Target()]).Rows.Count == 0));
        Check("unknown_run_start_disables_auto_merge", () => True(CManualInspectionResultProjection.Merge(Frame with { EarliestSavedAt = null }, [new(File("f.csv", Row()), "TEST")], [Target()]).Rows.Count == 0));
        Check("previous_machining_run_excluded", () => True(Merge(File("old-run.csv", Row(minute: -1))).Rows.Count == 0));
        Check("previous_row_cannot_hide_in_newer_file", () => True(Merge(File("mixed.csv", Row(minute: -1), Row(2, minute: 3))).Rows.Count == 1));
        Check("row_recipe_mismatch_excluded", () => True(Merge(File("f.csv", Row() with { RecipeId = "OTHER" })).RejectedRowCount == 1));
        Check("shift_x_mismatch_excluded", () => True(Merge(File("f.csv", Row()) with { ShiftOffsetX = 1.001 }).Rows.Count == 0));
        Check("shift_y_mismatch_excluded", () => True(Merge(File("f.csv", Row()) with { ShiftOffsetY = -2 }).Rows.Count == 0));
        Check("nonfinite_shift_excluded", () => True(Merge(File("f.csv", Row()) with { ShiftOffsetX = double.NaN }).Rows.Count == 0));
        Check("simulation_real_mismatch_excluded", () => True(Merge(File("f.csv", Row()) with { IsStageSimulation = true }).Rows.Count == 0));
        Check("vision_mode_mismatch_excluded", () => True(Merge(File("f.csv", Row()) with { IsVisionSimulation = true }).Rows.Count == 0));
        Check("legacy_unknown_mode_is_explicit_view_only", () => True(Merge(File("f.csv", Row()) with { IsVisionSimulation = null }).Rows.Count == 0));
        Check("matching_explicit_simulation_mode_is_viewable", () => True(CManualInspectionResultProjection.Merge(Frame with { IsSimulation = true },
            [new(File("sim.csv", Row()) with { IsStageSimulation = true, IsVisionSimulation = true }, "SIM")], [Target()]).Rows.Count == 1));
        Check("missing_geometry_is_explicit_view_only", () => True(Merge(File("f.csv", Row() with { DesignX = null })).Rows.Count == 0));
        Check("changed_head_geometry_excluded", () => True(Merge(File("f.csv", Row() with { HeadNo = 2 })).Rows.Count == 0));
        Check("changed_design_geometry_excluded", () => True(Merge(File("f.csv", Row() with { DesignY = 2.001 })).Rows.Count == 0));
        Check("geometry_tolerance_boundary_allowed", () => True(Merge(File("f.csv", Row() with { DesignX = 1.0000009 })).Rows.Count == 1));
        Check("nonfinite_measurement_excluded", () => True(Merge(File("f.csv", Row() with { ErrorX = double.PositiveInfinity })).Rows.Count == 0));
        Check("error_has_no_invented_numeric_requirement", () => True(Merge(File("f.csv", Row(source: "ERROR", judge: "ERROR") with { ErrorX = double.NaN })).Rows.Count == 1));
        Check("per_beam_identity_is_distinct", () => True(Merge(File("f.csv", Row(), Row(beam: 1))).Rows.Count == 2));
        Check("missing_row_beam_uses_file_beam", () => True(Merge(File("f.csv", Row(beam: 0))).Rows.Keys.Single().EndsWith("B11")));
        Check("duplicate_rows_reject_whole_file", () => True(Merge(File("f.csv", Row(), Row())).RejectedFileCount == 1));
        Check("tie_break_is_deterministic", () => True(Merge(File("z.csv", Row()), File("a.csv", Row())).Rows.Values.Single().FilePath == "a.csv"));
        Check("nonexistent_coordinate_not_invented", () => True(Merge(File("f.csv", Row(19))).Rows.Count == 0));
        Check("future_row_cannot_override_file_timestamp", () => True(Merge(File("f.csv", Row()) with { SavedAt = Start }).Rows.Count == 0));
        Check("vendor_assemblies_not_loaded", () => True(!AppDomain.CurrentDomain.GetAssemblies().Any(a => new[] { "Aerotech", "Automation1", "ACS.SPii", "Newport.XPS", "CmdLib", "DeviceIOLib" }.Any(n => (a.GetName().Name ?? "").Contains(n, StringComparison.OrdinalIgnoreCase)))));
        var output = args.Length == 2 && args[0] == "--report" ? args[1] : "external-results.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        System.IO.File.WriteAllText(output, JsonSerializer.Serialize(new { schemaVersion = "a3-manual-external-results/1", passed = Failed == 0, count = Results.Count, failed = Failed, tests = Results,
            scope = "Actual Common pure projection and DTOs. Synthetic saved results/recipe geometry; no equipment App/Root/Manager, SDK, PLC, Vision or motion execution." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"External result projection: {Results.Count - Failed}/{Results.Count} passed. {output}");
        return Failed == 0 ? 0 : 1;
    }
}
