using System.Globalization;
using System.IO;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;
using Microsoft.VisualBasic.FileIO;

internal static partial class Program
{
    private static async Task RunCoverageChecks()
    {
        await Check("existing_corrections_without_residual_are_not_displayed_as_current_measurements", async () =>
        {
            var (f, c) = CorrectionFixture();
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            True(c.Snapshot.Points.All(p => !p.HasMeasurementHistory && !p.UsesAppliedMeasurement&&p.MeasurementXUm is null));
            True(c.Snapshot.Points.All(p => p.WorkingJudge == "UNMEASURED" && p.WorkingXUm is null
                && p.OriginalXUm is null && p.MeasurementToken == "" && p.CanBeReference&&!p.CanCopyAsReference));
            Near(c.Snapshot.Points[0].BaseXUm,7);
            True(c.Snapshot.Points[1].MeasurementXUm is null && c.Snapshot.Points[1].AppliedLevel == 0
                && f.Saves == 0 && f.Executed.Count == 0);
        });
        await Check("displayed_history_cannot_reapply_its_stored_offset_as_new_raw", async () =>
        {
            var (f, c) = CorrectionFixture(); await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            await Throws(() => Do(c, EN_MANUAL_INSPECTION_UI_ACTION.Calculate, ["P1"]));
            True(f.Saves == 0 && c.Snapshot.Points[0].BaseXUm == 7 && !c.Snapshot.CanApply);
        });
        await Check("fresh_direct_measurement_overrides_display_fallback_only", async () =>
        {
            var (f, c) = CorrectionFixture(); await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure, ["P1"]);
            var p = c.Snapshot.Points.Single(p => p.Key == "P1");
            True(p.HasWorkingMeasurement && !p.UsesAppliedMeasurement && p.CanBeReference && p.Source == "MEASURED");
            Near(p.MeasurementXUm!.Value, f.NewX); Near(p.BaseXUm, 7);
            True(p.OriginalXUm == f.NewX && p.MeasurementToken.Length > 0 && f.Saves == 0);
        });
        await Check("coverage_export_keeps_original_raw_columns_and_adds_display_basis", async () =>
        {
            var (f, c) = CorrectionFixture(); await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            var path = Path.Combine(f.StateDirectory, "coverage-export.csv"); c.ChooseFile = (_, _) => path;
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv);
            using var parser = new TextFieldParser(path) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
            parser.SetDelimiters(","); var header = parser.ReadFields()!; var values = parser.ReadFields()!;
            var row = header.Select((name, index) => (name, value: values[index])).ToDictionary(p => p.name, p => p.value);
            True(row.ContainsKey("MEASUREMENT_HISTORY") && row.ContainsKey("MEASUREMENT_BASIS")
                && row.ContainsKey("DISPLAY_DX_DESIGN_UM") && row.ContainsKey("DISPLAY_DY_DESIGN_UM") && row.ContainsKey("MEASUREMENT_LEVEL"));
            True(row["WORK_JUDGE"] == "UNMEASURED" && row["WORK_DX_DESIGN_UM"] == ""
                && row["ORIGINAL_DX_DESIGN_UM"] == "" && row["ORIGINAL_RAW"] == "");
            True(row["DISPLAY_DX_DESIGN_UM"]=="");
            Near(double.Parse(row["BASE_X_DESIGN_UM"], CultureInfo.InvariantCulture), 7);
            True(row["MEASUREMENT_BASIS"].Contains("유효 편차 없음") && row["MEASUREMENT_LEVEL"]=="" && f.Saves == 0);
        });
        await Check("point_history_explains_correction_coverage_and_keeps_null_original", async () =>
        {
            var (f, c) = CorrectionFixture(); await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            string detail = ""; c.ShowDetails = (_, text) => detail = text;
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint, ["P1"]);
            True(detail.Contains("Level 없음") && detail.Contains("유효 편차 없음"));
            True(c.Snapshot.Points[0].OriginalXUm is null && c.Snapshot.Points[0].WorkingXUm is null && f.Saves == 0 && f.Executed.Count == 0);
        });
        await Check("masked_stored_offset_is_excluded_from_measured_coverage", async () =>
        {
            var (f, c) = CorrectionFixture();
            f.Source = f.Source with { Points = f.Source.Points.Select((p, i) => i == 0 ? p with { Masked = true } : p).ToArray() };
            await Do(c, EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe);
            var p = c.Snapshot.Points[0];
            True(p.HasAppliedOffset && !p.HasMeasurementHistory && !p.UsesAppliedMeasurement && p.MeasurementXUm is null && p.AppliedLevel is null);
        });
    }

    private static (FakeBackend, CManualInspectionController) CorrectionFixture()
    {
        var (f, c) = Fixture();
        f.Source = f.Source with
        {
            Points = f.Source.Points.Select(p => p with { Judge = "UNMEASURED", Source = "KEPT", ErrorXUm = null,
                ErrorYUm = null, OriginalMeasurement = null, MeasurementVersion = 0 }).ToArray(),
            Values = new Dictionary<string, ST_MANUAL_OFFSET> { ["P1"] = new(7, -2), ["P2"] = new(0, 0), ["P3"] = new(12, 0) },
            AppliedOffsetKeys = new HashSet<string>(["P1", "P2", "P3"], StringComparer.Ordinal)
        };
        return (f, c);
    }
}
