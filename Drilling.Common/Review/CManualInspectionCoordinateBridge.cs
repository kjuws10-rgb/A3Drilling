using System.Globalization;
using Drilling.Common.Recipe;
using Drilling.Common.Review.ManualInspection;
using Drilling.Common.Correction;

namespace Drilling.Common.Review;

/// <summary>
/// Explicit boundary for the new workspace. Vision reports mm, the working model uses DESIGN µm,
/// and existing Review Offset files store mm with an explicit coordinate-space column.
/// No device or file is opened by these functions.
/// </summary>
public static class CManualInspectionCoordinateBridge
{
    public static ST_MANUAL_OFFSET FromVision(double rawXmm, double rawYmm, EN_VISION_FLIP_TYPE visionFlip,
        IReadOnlyDictionary<string, string> parameters, int headNo)
    {
        var stage = CReviewManager.VisionErrorToStageOffset(rawXmm, rawYmm, visionFlip);
        return FromStage(stage.X, stage.Y, parameters, headNo);
    }

    /// <summary>
    /// Existing Result CSV's VISION_FLIP_CONVERT fields have already passed Vision Flip.
    /// Applying Vision Flip a second time would reverse some heads. Resolve the Scanner/Design
    /// direction through the same production converters used when machining consumes its offsets.
    /// </summary>
    public static ST_MANUAL_OFFSET FromStage(double xMm, double yMm,
        IReadOnlyDictionary<string, string> parameters, int headNo)
    {
        if (!double.IsFinite(xMm) || !double.IsFinite(yMm))
            throw new InvalidOperationException("측정 편차가 유효한 숫자가 아닙니다.");
        var scanner = CScannerAxisTransform.Read(parameters, headNo).Apply(xMm, yMm);
        var design = CScannerAxisTransform.ScannerOffsetToDesign(parameters, headNo, scanner.X, scanner.Y);
        return new(design.X * 1000.0, design.Y * 1000.0);
    }

    public static ST_MANUAL_OFFSET FromFile(ST_REVIEW_OFFSET_ROW row,
        IReadOnlyDictionary<string, string> parameters, int headNo)
    {
        var design = CReviewOffsetData.ToDesign(parameters, headNo,
            row.ReviewOffsetX, row.ReviewOffsetY, row.IsDesignCoordinate);
        return new(design.X * 1000.0, design.Y * 1000.0);
    }

    public static ST_REVIEW_OFFSET_ROW ToFile(ST_MANUAL_POINT point, int columns, ST_MANUAL_OFFSET value)
    {
        if (!value.IsFinite) throw new InvalidOperationException("저장할 보정값이 유효하지 않습니다.");
        return new(point.CellNo, CReviewShotNameFormatter.ToMatrixName(point.ShotNo, columns),
            value.XUm / 1000.0, value.YUm / 1000.0) { IsDesignCoordinate = true };
    }

    /// <summary>
    /// ScannerGy in a MOF plan contains the stage-following trajectory, not a stationary field error.
    /// Remove that ideal moving-stage component before testing the stationary command contribution.
    /// Keep Recipe/Default/First/Align/Shift and apply only (F-B)*REVIEW_OFFSET_SCALE once.
    /// This is a point/recipe-field check; the controller's physical software limits remain authoritative.
    /// </summary>
    public static ST_MANUAL_FIELD_RESULT EvaluateField(ST_HEAD_SHOT_POINT shot, ST_MANUAL_OFFSET baseValue,
        ST_MANUAL_OFFSET finalValue, IReadOnlyDictionary<string, string> parameters, ST_MANUAL_LIMITS limits)
    {
        if (!baseValue.IsFinite || !finalValue.IsFinite || !double.IsFinite(limits.FieldWidthMm) || limits.FieldWidthMm <= 0)
            return new(false, "Field 검사 입력이 유효하지 않습니다.");
        var xKey = $"H{shot.HeadNo:00}_SCAN_FIELD_WIDTH_X";
        // Some definitions use SCANNER_FIELD_WIDTH_X; do not silently replace a missing field by a demo value.
        var actualWidth = Number(parameters, xKey, $"H{shot.HeadNo:00}_HEAD_FIELD_WIDTH_X");
        if (!(actualWidth > 0)) return new(false, $"Head {shot.HeadNo}의 Scan Field X 설정을 확인해 주세요.");
        var basis = CScannerAxisTransform.ScannerOffsetToDesign(parameters, shot.HeadNo,
            shot.ScannerGx - shot.FinalScannerOffsetX, shot.ScannerGy - shot.FinalScannerOffsetY);
        var stationary = CScannerAxisTransform.DesignOffsetToScanner(parameters, shot.HeadNo, basis.X, 0);
        var scale = parameters.TryGetValue("REVIEW_OFFSET_SCALE", out var text)
            ? double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) / 100.0 : 1.0;
        if (!double.IsFinite(scale) || scale < 0) return new(false, "REVIEW_OFFSET_SCALE이 유효하지 않습니다.");
        // The existing plan reader rounds Review Offset to 0.001 mm. File precision is finer,
        // so evaluate the actual consumed F, not the unrounded difference between file B and F.
        var next = CScannerAxisTransform.DesignOffsetToScanner(parameters, shot.HeadNo,
            CAlignShotOffsetData.RoundValue(finalValue.XUm / 1000.0),
            CAlignShotOffsetData.RoundValue(finalValue.YUm / 1000.0));
        var gx = stationary.X + shot.FinalScannerOffsetX - shot.ReviewOffsetX + next.X * scale;
        var gy = stationary.Y + shot.FinalScannerOffsetY - shot.ReviewOffsetY + next.Y * scale;
        var half = Math.Min(actualWidth, limits.FieldWidthMm) / 2.0;
        var ok = double.IsFinite(gx) && double.IsFinite(gy) && Math.Abs(gx) <= half && Math.Abs(gy) <= half;
        return new(ok, ok ? "" : $"H{shot.HeadNo:00} Scanner {gx:0.######}/{gy:0.######} mm, 검증 범위 ±{half:0.###} mm")
        { ScannerXMm = gx, ScannerYMm = gy };
    }

    private static double Number(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
            if (values.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) return value;
        return double.NaN;
    }
}
