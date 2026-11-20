using Drilling.Common.Review;

internal static partial class Program
{
    private static void RunHardeningChecks()
    {
        Check("hardening_run_cutoff_exact_time_is_inclusive", () => True(Merge(File("at-cutoff.csv", Row() with { SavedAt = Start })).Rows.Count == 1));
        Check("hardening_one_tick_before_cutoff_cannot_enter_through_later_file", () =>
            True(Merge(File("later-file.csv", Row() with { SavedAt = Start.AddTicks(-1) }, Row(2, minute: 5))).Rows.Keys.Single().Contains("B11")
                && Merge(File("later-file.csv", Row() with { SavedAt = Start.AddTicks(-1) }, Row(2, minute: 5))).RejectedRowCount == 1));
        Check("hardening_row_timestamp_decides_across_newer_file_save", () =>
        {
            var olderRowLaterFile = File("newer-file.csv", Row() with { ErrorX = .100 }, Row(2, minute: 8));
            var latestRowEarlierFile = File("latest-observation.csv", Row(minute: 6) with { ErrorX = .006 });
            var merged = Merge(olderRowLaterFile, latestRowEarlierFile);
            True(merged.Rows[CManualInspectionResultProjection.RowKey(1, CReviewShotNameFormatter.ToMatrixName(1, 20), 11)].Row.ErrorX == .006);
        });
        Check("hardening_file_order_permutations_produce_identical_current_observation", () =>
        {
            var files = new[] { File("rough.csv", Row()), File("fine.csv", Row(minute: 2)), File("auto.csv", Row(2, minute: 3)) };
            var expected = Merge(files).Rows.OrderBy(p => p.Key).Select(p => (p.Key, p.Value.Row, p.Value.FilePath)).ToArray();
            foreach (var permutation in new[] { files.Reverse().ToArray(), new[] { files[1], files[2], files[0] }, new[] { files[2], files[0], files[1] } })
                True(Merge(permutation).Rows.OrderBy(p => p.Key).Select(p => (p.Key, p.Value.Row, p.Value.FilePath)).SequenceEqual(expected));
        });
        Check("hardening_duplicate_beam_fallback_rejects_file_atomically", () =>
        {
            var result = Merge(File("duplicate-fallback.csv", Row(beam: 0), Row(beam: 11), Row(2)));
            True(result.RejectedFileCount == 1 && result.Rows.Count == 0);
        });
        Check("hardening_previous_product_rows_never_join_current_cell_even_at_newest_time", () =>
        {
            var result = Merge(File("previous-product.csv", Row(minute: 10)) with { GlassId = "GLASS-2" }, File("current-product.csv", Row()));
            True(result.RejectedFileCount == 1 && result.Rows.Values.Single().FilePath == "current-product.csv");
        });
        Check("hardening_row_from_other_recipe_cannot_shadow_earlier_valid_row", () =>
        {
            var result = Merge(File("bad-recipe.csv", Row(minute: 10) with { RecipeId = "OTHER" }), File("valid.csv", Row()));
            True(result.Rows.Values.Single().FilePath == "valid.csv" && result.RejectedRowCount == 1);
        });
        Check("hardening_recipe_and_glass_case_are_identity_insensitive", () =>
        {
            var result = Merge(File("same-case.csv", Row() with { RecipeId = "r" }) with { RecipeId = "r", GlassId = "glass-1" });
            True(result.Rows.Count == 1);
        });
        Check("hardening_invalid_current_shift_disables_entire_automatic_projection", () =>
        {
            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                True(CManualInspectionResultProjection.Merge(Frame with { ShiftYmm = invalid }, [new(File("f.csv", Row()), "FINE")], [Target()]).Rows.Count == 0);
        });
        Check("hardening_unknown_current_product_names_disable_automatic_projection", () =>
        {
            foreach (var unknown in new[] { "", " ", "UNKNOWN", " none ", "-", "Unlinked" })
                True(CManualInspectionResultProjection.Merge(Frame with { GlassId = unknown }, [new(File("f.csv", Row()), "FINE")], [Target()]).Rows.Count == 0);
        });
        Check("hardening_same_coordinates_do_not_cross_cell_or_beam_identity", () =>
        {
            var onlyOneTarget = new[] { Target() };
            foreach (var mismatch in new[] { Row(cell: 2), Row(beam: 1) })
                True(CManualInspectionResultProjection.Merge(Frame, [new(File("foreign-slot.csv", mismatch), "AUTO")], onlyOneTarget).Rows.Count == 0);
        });
        Check("hardening_geometry_just_outside_tolerance_is_excluded", () =>
            True(Merge(File("geometry.csv", Row() with { DesignX = 1.0000011 })).Rows.Count == 0));
        Check("hardening_newest_invalid_numeric_cannot_erase_old_valid_observation", () =>
        {
            var result = Merge(File("old-valid.csv", Row()), File("new-invalid.csv", Row(minute: 4) with { ErrorY = double.NaN }));
            True(result.Rows.Values.Single().FilePath == "old-valid.csv" && result.RejectedRowCount == 1);
        });
        Check("hardening_error_remains_error_when_numeric_payload_is_zero", () =>
        {
            var result = Merge(File("prior-ok.csv", Row()), File("failed.csv", Row(source: "ERROR", judge: "ERROR", minute: 5) with { ErrorX = 0, ErrorY = 0 }));
            True(result.Rows.Values.Single().Row.Judge == "ERROR" && result.Rows.Values.Single().Row.ResultSource == "ERROR");
        });
        Check("hardening_per_row_origin_and_file_provenance_survive_multimenu_merge", () =>
        {
            var merged = CManualInspectionResultProjection.Merge(Frame,
                [new(File("rough.csv", Row()), "ROUGH"), new(File("fine.csv", Row(2)), "FINE"), new(File("auto.csv", Row(cell: 2)), "AUTO")],
                [Target(), Target(2), Target(1, 2)]);
            True(merged.Rows.Values.Select(p => (p.FilePath, p.Origin)).ToHashSet().SetEquals(new[] { ("rough.csv", "ROUGH"), ("fine.csv", "FINE"), ("auto.csv", "AUTO") }));
        });
        Check("hardening_ambiguous_current_target_identity_fails_closed", () =>
        {
            var rejected = false;
            try { CManualInspectionResultProjection.Merge(Frame, [new(File("f.csv", Row()), "FINE")], [Target(), Target()]); }
            catch (ArgumentException) { rejected = true; }
            True(rejected);
        });
    }
}
