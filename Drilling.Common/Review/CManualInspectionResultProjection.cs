using System.Collections.ObjectModel;

namespace Drilling.Common.Review;

/// <summary>
/// External result files have no native Manual Inspection Run id. Automatic observation is therefore
/// limited to a known current product, its creation/latest machining time, identical Shift and mode,
/// and row geometry that matches the current recipe. The result remains IMPORTED, never a live anchor.
/// </summary>
public sealed record ST_MANUAL_RESULT_FRAME(string RecipeId, string GlassId, DateTimeOffset? EarliestSavedAt,
    double ShiftXmm, double ShiftYmm, bool IsSimulation)
{
    public bool IsLinked => !string.IsNullOrWhiteSpace(RecipeId) && IsKnownGlass(GlassId)
        && EarliestSavedAt is not null && double.IsFinite(ShiftXmm) && double.IsFinite(ShiftYmm);

    public static bool IsKnownGlass(string glass) => !string.IsNullOrWhiteSpace(glass)
        && glass.Trim().ToUpperInvariant() is not ("UNLINKED" or "UNKNOWN" or "NONE" or "-");
}

public sealed record ST_MANUAL_EXTERNAL_RESULT(ST_REVIEW_RESULT_FILE_DATA File, string Origin);
public sealed record ST_MANUAL_IMPORTED_OBSERVATION(ST_REVIEW_RESULT_FILE_ROW Row,
    string FilePath, string Origin);
public sealed record ST_MANUAL_RESULT_PROJECTION(
    IReadOnlyDictionary<string, ST_MANUAL_IMPORTED_OBSERVATION> Rows,
    int RejectedFileCount, int RejectedRowCount, int IgnoredUnmeasuredCount);

/// <summary>Pure read projection. It never changes a result file, offset, native measurement or judge.</summary>
public static class CManualInspectionResultProjection
{
    public static string RowKey(int cell, string matrixName, int beam)
        => $"{cell}:{Normalize(matrixName)}:B{beam}";

    public static bool IsSameFrame(ST_MANUAL_RESULT_FRAME frame, ST_REVIEW_RESULT_FILE_DATA file)
        => frame.IsLinked
        && file.RecipeId.Equals(frame.RecipeId, StringComparison.OrdinalIgnoreCase)
        && file.GlassId.Equals(frame.GlassId, StringComparison.OrdinalIgnoreCase)
        && file.SavedAt >= frame.EarliestSavedAt
        && file.IsStageSimulation == frame.IsSimulation && file.IsVisionSimulation == frame.IsSimulation
        && double.IsFinite(file.ShiftOffsetX) && double.IsFinite(file.ShiftOffsetY)
        && Math.Abs(file.ShiftOffsetX - frame.ShiftXmm) <= .000000001
        && Math.Abs(file.ShiftOffsetY - frame.ShiftYmm) <= .000000001;

    public static ST_MANUAL_RESULT_PROJECTION Merge(ST_MANUAL_RESULT_FRAME frame,
        IEnumerable<ST_MANUAL_EXTERNAL_RESULT> files, IEnumerable<ST_REVIEW_PLAN_POINT> actualTargets)
    {
        var targets = actualTargets.ToDictionary(p => RowKey(p.CellNo,
            CReviewShotNameFormatter.ToMatrixName(p.ShotNo, p.PixelCountX), p.BeamNo), StringComparer.OrdinalIgnoreCase);
        var direct = new Dictionary<string, ST_MANUAL_IMPORTED_OBSERVATION>(StringComparer.OrdinalIgnoreCase);
        var rejectedFiles = 0; var rejectedRows = 0; var unmeasured = 0;
        foreach (var item in files.OrderByDescending(f => f.File.SavedAt)
                     .ThenBy(f => f.File.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var file = item.File;
            if (!IsSameFrame(frame, file)) { rejectedFiles++; continue; }
            var duplicate = file.Rows.GroupBy(r => RowKey(r.CellNo, r.ShotName, r.BeamNo > 0 ? r.BeamNo : file.BeamNo),
                StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
            if (duplicate) { rejectedFiles++; continue; }
            foreach (var row in file.Rows.OrderByDescending(r => r.SavedAt))
            {
                if (row.Judge is "UNMEASURED" or "UNKNOWN" or "WAIT" or "미검사" || row.ResultSource == "UNMEASURED")
                { unmeasured++; continue; }
                // RETAINED is saved with the new file's timestamp, not its original capture time.
                // It cannot prove this machining run and therefore is explicit-file-view only.
                if (row.ResultSource == "RETAINED") { unmeasured++; continue; }
                var key = RowKey(row.CellNo, row.ShotName, row.BeamNo > 0 ? row.BeamNo : file.BeamNo);
                // Missing design coordinates cannot prove that a legacy result belongs to this recipe
                // geometry. Those files remain available through explicit read-only file selection.
                if (!row.RecipeId.Equals(frame.RecipeId, StringComparison.OrdinalIgnoreCase)
                    || row.SavedAt < frame.EarliestSavedAt || row.SavedAt > file.SavedAt
                    || !targets.TryGetValue(key, out var target) || row.HeadNo != target.HeadNo
                    || row.DesignX is not double x || row.DesignY is not double y
                    || !double.IsFinite(x) || !double.IsFinite(y)
                    || Math.Abs(x - target.DesignX) > .000001 || Math.Abs(y - target.DesignY) > .000001
                    || row.Judge is not ("OK" or "NG" or "ERROR")
                    || row.ResultSource is not ("MEASURED" or "EDITED" or "MANUAL" or "ERROR")
                    || row.Judge is "OK" or "NG" && (!double.IsFinite(row.ErrorX) || !double.IsFinite(row.ErrorY)))
                { rejectedRows++; continue; }
                var observation = new ST_MANUAL_IMPORTED_OBSERVATION(row, file.FilePath, item.Origin);
                if (!direct.TryGetValue(key, out var previous) || previous.Row.SavedAt < row.SavedAt)
                    direct[key] = observation;
            }
        }
        return new(new ReadOnlyDictionary<string, ST_MANUAL_IMPORTED_OBSERVATION>(direct), rejectedFiles, rejectedRows, unmeasured);
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
