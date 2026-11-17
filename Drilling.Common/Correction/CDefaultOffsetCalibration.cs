using System.Globalization;
using Drilling.Common.Managers;
using Drilling.Common.Recipe;
using Drilling.Common.Review;
using Drilling.Common.Automation;

namespace Drilling.Common.Correction;

// CENTER setup uses the existing design-mm Default convention. A Vision Target-Hole
// correction is flipped once into that convention; scanner/head transforms are applied
// only when producing scanner commands. No common-Y firing adjustment is introduced.
public static class CDefaultOffsetCalibration
{
    public static IReadOnlyDictionary<string, string> Parameters(ST_RECIPE_DEFINITION recipe, IEnumerable<ST_SYSTEM_PARAMETER> settings)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in settings) values[p.Key] = p.Value;
        foreach (var p in recipe.Parameters) values[p.Key] = p.Value;
        return values;
    }

    public static double Number(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            throw new InvalidOperationException($"Default 측정 설정을 확인해 주세요: {key}");
        return number;
    }

    public static bool DoeExists(IReadOnlyDictionary<string, string> values, int head)
    {
        if (head is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(head));
        var key = $"H{head:00}_DOE_EXIST";
        if (!values.TryGetValue(key, out var text))
            throw new InvalidOperationException($"Default 측정 설정을 확인해 주세요: {key}");
        return text.Trim().ToUpperInvariant() switch
        {
            "ON" or "1" or "TRUE" => true,
            "OFF" or "0" or "FALSE" => false,
            _ => throw new InvalidOperationException($"Default 측정 설정은 ON 또는 OFF여야 합니다: {key}")
        };
    }

    // DOE_EXIST changes only Default Offset inspection. Production and every
    // other Review mode continue to use the operator-selected beam number.
    public static int DefaultInspectionGridSize(IReadOnlyDictionary<string, string> values, int head, ST_DEFAULT_OFFSET_OPTIONS options)
    {
        ValidateOptions(options);
        return DoeExists(values, head) ? options.DoeBeamGridSize : 1;
    }

    public static int DefaultInspectionPointCount(IReadOnlyDictionary<string, string> values, int head, ST_DEFAULT_OFFSET_OPTIONS options)
    {
        var gridSize = DefaultInspectionGridSize(values, head, options);
        return gridSize * gridSize;
    }

    public static string DefaultInspectionDescription(IReadOnlyDictionary<string, string> values, int head, ST_DEFAULT_OFFSET_OPTIONS options)
    {
        if (DoeExists(values, head)) return options.InspectionDescription;
        return options.MachiningMode == EN_DEFAULT_MACHINING_MODE.Cross
            ? "DOE OFF · 중앙 십자 1점 측정"
            : "DOE OFF · 중앙 1점 측정";
    }

    // AK1 starts from the taught Recipe position. AK3 defaults to its design
    // position, but the measurement session may replace it with the actual
    // Review-X encoder position. The glass center is always their midpoint.
    public static ST_DEFAULT_OFFSET_OPTIONS Conditions(IReadOnlyDictionary<string, string> values, double? testStageY = null)
    {
        var alignKey1ReviewX = Number(values, "REVIEW_CAMERA_ALIGN_KEY_POS_X");
        var alignKey3ReviewX = DesignAlignKey3ReviewX(values);
        var stageY = testStageY ?? Number(values, "STAGE_START_POS_Y");
        var tolerance = Math.Min(Number(values, "REVIEW_TOLERANCE_X"), Number(values, "REVIEW_TOLERANCE_Y"));
        if (!double.IsFinite(stageY) || !double.IsFinite(tolerance) || tolerance <= 0)
            throw new InvalidOperationException("Recipe의 Review 허용오차와 Stage Y 위치를 확인해 주세요.");
        return new(alignKey1ReviewX, alignKey3ReviewX, stageY, tolerance)
        {
            DoeBeamGridSize = BeamGridSize(values)
        };
    }

    public static double DesignAlignKey3ReviewX(IReadOnlyDictionary<string, string> values)
    {
        var alignKey1ReviewX = Number(values, "REVIEW_CAMERA_ALIGN_KEY_POS_X");
        var glassWidth = Number(values, "GLASS_SIZE_X");
        var marginX = Number(values, "AK_MARGIN_X");
        var alignKeySpan = glassWidth - 2 * marginX;
        var alignKey3ReviewX = alignKey1ReviewX + alignKeySpan;
        if (!double.IsFinite(alignKey3ReviewX) || alignKeySpan <= 0)
            throw new InvalidOperationException("Glass Size X와 AK Margin X로 AK3 설계 Review X를 계산할 수 없습니다.");
        return alignKey3ReviewX;
    }

    public static ST_DEFAULT_HEAD_GEOMETRY HeadGeometry(IReadOnlyDictionary<string, string> values, int head)
    {
        if (head is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(head));
        var prefix = $"H{head:00}_";
        var x = Number(values, prefix + "SCAN_FIELD_WIDTH_X");
        var y = Number(values, prefix + "SCAN_FIELD_HEIGHT_Y");
        var angle = Number(values, prefix + "DOE_ROTATION_ANGLE");
        if (x <= 0 || y <= 0) throw new InvalidOperationException($"HEAD {head:00}의 Setting Scan Field X/Y는 0보다 커야 합니다.");
        // Setting stores full dimensions, not the distance from the center.
        return new(x / 2, y / 2, angle);
    }

    public static ST_DEFAULT_PREVIEW_POINT PredictCenter(IReadOnlyDictionary<string, string> values, int head, double stageY)
    {
        if (head is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(head));
        if (!double.IsFinite(stageY)) throw new ArgumentOutOfRangeException(nameof(stageY));
        var start = Number(values, "STAGE_START_POS_Y");
        var direction = CScannerAxisTransform.ReadStageDirection(values);
        var gap = values.ContainsKey($"H{head:00}_AK_POSITION_Y") ? Number(values, $"H{head:00}_AK_POSITION_Y")
            : Number(values, "REVIEW_TO_HEAD1_GAP_Y") + (head % 2 == 0 ? Number(values, "HeadGapY") : 0);
        // Same AK/camera and Head reference as CENTER measurement. Stage Start
        // defines the reference pose; its delta is projected into glass design mm.
        // Head flips change scanner commands, not the design-coordinate preview.
        var yAtStart = (start - direction * gap - Number(values, "REVIEW_CAMERA_ALIGN_KEY_POS_Y")) / direction + Number(values, "AK_MARGIN_Y");
        var x = Number(values, "AK_MARGIN_X") + Number(values, "H01_AK_POSITION_X") + (head - 1) * Number(values, "HeadGapX") + Number(values, $"H{head:00}_DEFAULT_OFFSET_X");
        var y = yAtStart + (stageY - start) / direction + Number(values, $"H{head:00}_DEFAULT_OFFSET_Y");
        var width = Number(values, "GLASS_SIZE_X"); var height = Number(values, "GLASS_SIZE_Y");
        if (!double.IsFinite(x) || !double.IsFinite(y) || width <= 0 || height <= 0) throw new InvalidOperationException("예상 가공 위치의 Recipe·Setting 기준값을 확인해 주세요.");
        return new(head, stageY, x, y, x >= 0 && x <= width && y >= 0 && y <= height);
    }

    // x/y are actual absolute GX/GY commands for the CENTER test, after Head
    // conversion. DOE beam spacing is an optical separation, not an axis move.
    public static bool FitsField(IReadOnlyDictionary<string, string> values, int head, double x, double y)
    {
        var field = HeadGeometry(values, head);
        return double.IsFinite(x) && double.IsFinite(y) && Math.Abs(x) <= field.FieldHalfX && Math.Abs(y) <= field.FieldHalfY;
    }

    public static void ValidateOptions(ST_DEFAULT_OFFSET_OPTIONS options)
    {
        if (new[] { options.AlignKey1ReviewX, options.AlignKey3ReviewX, options.BoardCenterX, options.TestStageY, options.Tolerance }.Any(v => !double.IsFinite(v)) || options.Tolerance <= 0)
            throw new InvalidOperationException("AK1·AK3 Review X, 가공할 Stage Y 위치와 Recipe Review 허용오차를 확인해 주세요.");
        if (Math.Abs(options.AlignKey3ReviewX - options.AlignKey1ReviewX) <= double.Epsilon)
            throw new InvalidOperationException("AK1과 AK3 Review X 위치는 서로 달라야 합니다.");
        if (!Enum.IsDefined(options.MachiningMode))
            throw new InvalidOperationException("Default 가공 방식을 확인해 주세요.");
        if (options.DoeBeamGridSize is not (1 or 4))
            throw new InvalidOperationException("Default 측정의 DOE Beam Count는 1 또는 4여야 합니다.");
        if (options.MachiningMode == EN_DEFAULT_MACHINING_MODE.Cross && (!double.IsFinite(options.CrossSize) || options.CrossSize <= 0))
            throw new InvalidOperationException("십자 전체 길이는 0보다 큰 유한한 mm 값이어야 합니다.");
    }

    // Four cross endpoints are stored in design mm and transformed with the Head
    // at script generation. The scanner traces one center cross; the DOE optically
    // duplicates that trace at every beam slot. CrossSize is the full stroke length.
    public static IReadOnlyList<(double X, double Y)> CenterScannerTargets(IReadOnlyDictionary<string, string> values, int head, ST_DEFAULT_OFFSET_OPTIONS options)
    {
        ValidateOptions(options);
        var x = Number(values, $"H{head:00}_DEFAULT_OFFSET_X");
        var y = Number(values, $"H{head:00}_DEFAULT_OFFSET_Y");
        var transform = CScannerAxisTransform.Read(values, head);
        var half = options.CrossSize / 2;
        (double X, double Y)[] design = options.MachiningMode == EN_DEFAULT_MACHINING_MODE.Cross
            ? [(x - half, y), (x + half, y), (x, y - half), (x, y + half)] : [(x, y)];
        var targets = design.Select(p => transform.Apply(p.X, -p.Y)).ToArray();
        if (targets.Any(p => !FitsField(values, head, p.X, p.Y)))
            throw new InvalidOperationException($"HEAD {head:00} CENTER {options.MachiningMode} 스캐너 목표가 설정된 이동 범위를 초과합니다.");
        return targets;
    }

    public static int BeamGridSize(IReadOnlyDictionary<string, string> values)
    {
        var value = Number(values, "SPLITED_BEAM_COUNT");
        if (value != Math.Truncate(value) || value is not (1 or 4))
            throw new InvalidOperationException("Default 측정의 DOE Beam Count는 1 또는 4여야 합니다.");
        return (int)value;
    }

    public static double Pitch(IReadOnlyDictionary<string, string> values, int model)
    {
        var gridSize = BeamGridSize(values);
        if (gridSize == 1) return 0;
        var pattern = CRecipeModelPixelCountReader.ReadPatternForModel(values, model);
        var pitch = Number(values, "PIXEL_SIZE") * pattern.ProcessPitch / gridSize;
        if (!double.IsFinite(pitch) || pitch <= 0) throw new InvalidOperationException("DOE 빔 간격은 0보다 커야 합니다.");
        return pitch;
    }

    public static (double X, double Y) Slot(int beam, int gridSize, double pitch, double angle)
    {
        if (gridSize is not (1 or 4) || beam < 1 || beam > gridSize * gridSize || !double.IsFinite(pitch) ||
            (gridSize == 4 && pitch <= 0) || (gridSize == 1 && pitch != 0) || !double.IsFinite(angle))
            throw new ArgumentException("DOE 빔 슬롯 설정 오류");
        if (gridSize == 1) return (0, 0);
        var center = (gridSize - 1) / 2.0;
        var x = ((beam - 1) % gridSize - center) * pitch;
        var y = ((beam - 1) / gridSize - center) * pitch;
        var radians = angle * Math.PI / 180;
        return (x * Math.Cos(radians) - y * Math.Sin(radians), x * Math.Sin(radians) + y * Math.Cos(radians));
    }

    public static ST_DEFAULT_OFFSET_FIT Fit(IReadOnlyList<ST_DEFAULT_OFFSET_MARK> marks, int gridSize, double pitch, double angle, double residualTolerance)
    {
        if (!double.IsFinite(residualTolerance) || residualTolerance <= 0)
            throw new ArgumentException("중심 위치·Beam 간격 허용오차를 확인해 주세요.");
        if (gridSize is not (1 or 4)) throw new ArgumentException("DOE Beam Count는 1 또는 4여야 합니다.");
        if (!double.IsFinite(pitch) || (gridSize == 4 && pitch <= 0) || (gridSize == 1 && pitch != 0) || !double.IsFinite(angle))
            throw new ArgumentException("DOE 빔 간격 또는 회전 각도를 확인해 주세요.");
        var beamCount = gridSize * gridSize;
        if (marks.Count != beamCount || !marks.Select(m => m.Beam).Order().SequenceEqual(Enumerable.Range(1, beamCount)))
            throw new InvalidOperationException($"서로 다른 {beamCount}개 DOE 빔이 모두 측정되어야 합니다.");
        if (marks.Any(m => m.Judge != "OK" || new[] { m.X, m.Y, m.RawX, m.RawY, m.CorrectionX, m.CorrectionY, m.EncoderX, m.EncoderY }.Any(v => !double.IsFinite(v))))
            throw new InvalidOperationException("NG 또는 유효하지 않은 빔 측정값이 있습니다. 해당 Head를 다시 측정해 주세요.");
        if (gridSize == 1)
            return new(marks[0].X, marks[0].Y, 0, 0, 0);
        // The complete symmetric 4x4 grid has orthogonal design columns: the affine
        // least-squares solution is well-conditioned and can be evaluated directly.
        var cx = marks.Average(m => m.X); var cy = marks.Average(m => m.Y);
        var center = (gridSize - 1) / 2.0;
        var square = Enumerable.Range(1, beamCount).Sum(beam => Math.Pow(((beam - 1) % gridSize - center) * pitch, 2));
        if (!double.IsFinite(square) || square <= 0) throw new InvalidOperationException("DOE 격자 크기를 확인해 주세요.");
        double Coefficient(bool x, bool column) => marks.Sum(m => ((column ? (m.Beam - 1) % gridSize : (m.Beam - 1) / gridSize) - center) * pitch * (x ? m.X - cx : m.Y - cy)) / square;
        var a = Coefficient(true, true); var b = Coefficient(true, false);
        var c = Coefficient(false, true); var d = Coefficient(false, false);
        var px = pitch * Math.Sqrt(a * a + c * c); var py = pitch * Math.Sqrt(b * b + d * d);
        var radians = angle * Math.PI / 180;
        var expected = new[] { Math.Cos(radians), -Math.Sin(radians), Math.Sin(radians), Math.Cos(radians) };
        // Compare the centered fitted grid with the configured DOE grid in mm.
        // Reuse Recipe Review tolerance instead of a separate arbitrary ratio.
        var gridError = Enumerable.Range(1, beamCount).Max(beam =>
        {
            var u = ((beam - 1) % gridSize - center) * pitch; var v = ((beam - 1) / gridSize - center) * pitch;
            return Math.Sqrt(Math.Pow((a - expected[0]) * u + (b - expected[1]) * v, 2) + Math.Pow((c - expected[2]) * u + (d - expected[3]) * v, 2));
        });
        if (!double.IsFinite(px) || !double.IsFinite(py) || !double.IsFinite(gridError) || gridError > residualTolerance)
            throw new InvalidOperationException("실측 Pitch/방향이 예상 DOE 배열과 다릅니다. 슬롯·DOE 각도·Vision Flip을 확인해 주세요.");
        var residual = marks.Max(m =>
        {
            var u = ((m.Beam - 1) % gridSize - center) * pitch; var v = ((m.Beam - 1) / gridSize - center) * pitch;
            return Math.Sqrt(Math.Pow(m.X - cx - a * u - b * v, 2) + Math.Pow(m.Y - cy - c * u - d * v, 2));
        });
        if (!double.IsFinite(residual) || residual > residualTolerance) throw new InvalidOperationException($"{beamCount}빔 위치 오차 {residual:0.0000} mm가 허용값을 초과했습니다.");
        return new(cx, cy, residual, px, py);
    }

    // The global reference never comes from a previously measured Head. H4/H5
    // straddle the glass center; the configured pitch locates every other Head.
    // Stage Y targets preserve the machine's odd/even Head row separation.
    public static ST_DEFAULT_OFFSET_HEAD_RESULT WithHeadReference(IReadOnlyDictionary<string, string> values, ST_DEFAULT_OFFSET_OPTIONS options, ST_DEFAULT_OFFSET_HEAD_RESULT result)
    {
        ValidateOptions(options);
        var hasCompleteDoeMeasurement = result.Marks.Count is 1 or 16 &&
            result.Marks.Select(m => m.Beam).Order().SequenceEqual(Enumerable.Range(1, result.Marks.Count));
        if (result.Head is < 1 or > 8 || !double.IsFinite(result.Q) || !hasCompleteDoeMeasurement ||
            result.Marks.Any(m => !double.IsFinite(m.EncoderX) || !double.IsFinite(m.EncoderY) ||
                !double.IsFinite(m.CorrectionX) || !double.IsFinite(m.CorrectionY)))
            throw new InvalidOperationException("Head 중심 계산에 중앙 1점 또는 완전한 DOE 16점 측정 좌표가 필요합니다.");
        var targetX = options.BoardCenterX + (result.Head - 4.5) * Number(values, "HeadGapX");
        var direction = CScannerAxisTransform.ReadStageDirection(values);
        var gapY = values.ContainsKey($"H{result.Head:00}_AK_POSITION_Y") ? Number(values, $"H{result.Head:00}_AK_POSITION_Y")
            : Number(values, "REVIEW_TO_HEAD1_GAP_Y") + (result.Head % 2 == 0 ? Number(values, "HeadGapY") : 0);
        var targetY = result.Q - direction * gapY;
        // Vision Flip has already put Target-Hole in design mm. Each encoder
        // position plus the received error locates the real mark. A complete,
        // symmetric DOE grid's mean is its virtual center, independent of scan
        // order and including the actual camera arrival position.
        var actualX = result.Marks.Average(m => m.EncoderX - m.CorrectionX);
        var actualY = result.Marks.Average(m => m.EncoderY - direction * m.CorrectionY);
        if (new[] { targetX, targetY, actualX, actualY }.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("글래스 기준 목표 중심 또는 실측 중심 좌표를 확인해 주세요.");
        return result with { TargetReviewX = targetX, TargetStageY = targetY,
            CenterReviewX = actualX, CenterStageY = actualY,
            CenterErrorX = actualX - targetX, CenterErrorY = (actualY - targetY) / direction };
    }

    public static int[] ActiveHeads(IReadOnlyDictionary<string, string> values) =>
        Enumerable.Range(1, 8).Where(h => CScannerAxisConfiguration.IsHeadEnabled(values, h)).ToArray();

    public static IReadOnlyDictionary<string, string> Recommend(IReadOnlyDictionary<string, string> values, ST_DEFAULT_OFFSET_OPTIONS options, IReadOnlyCollection<ST_DEFAULT_OFFSET_HEAD_RESULT> results, IReadOnlyCollection<int>? targetHeads = null)
    {
        ValidateOptions(options);
        var active = ActiveHeads(values).ToHashSet();
        var targets = (targetHeads ?? active).Order().ToArray();
        if (targets.Length == 0 || targets.Distinct().Count() != targets.Length || targets.Any(h => !active.Contains(h)))
            throw new InvalidOperationException("USE인 가공 대상 Head를 확인해 주세요.");
        if (results.Count != targets.Length || !results.Select(r => r.Head).Order().SequenceEqual(targets))
            throw new InvalidOperationException($"{targets.Length}개 Head(USE)의 유효한 측정을 모두 완료해 주세요.");
        var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            var center = WithHeadReference(values, options, r);
            // Add the needed correction to the currently used Head Default.
            // Head axis flips are applied later by the scanner/script planner.
            foreach (var (axis, error) in new[] { ("X", center.CenterErrorX), ("Y", center.CenterErrorY) })
            {
                var key = $"H{r.Head:00}_DEFAULT_OFFSET_{axis}";
                var next = Math.Round(Number(values, key) - error, 5, MidpointRounding.AwayFromZero);
                if (!double.IsFinite(next) || Math.Abs(next) > 100) throw new InvalidOperationException($"HEAD {r.Head:00} Default {axis}가 레시피 허용범위 ±100 mm를 초과합니다.");
                changes[key] = next.ToString("0.00000", CultureInfo.InvariantCulture);
            }
        }
        return changes;
    }
}

public enum EN_DEFAULT_MACHINING_MODE { Shot, Cross }
public sealed record ST_DEFAULT_OFFSET_OPTIONS(double AlignKey1ReviewX, double AlignKey3ReviewX, double TestStageY, double Tolerance)
{
    public double BoardCenterX => (AlignKey1ReviewX + AlignKey3ReviewX) / 2;
    public EN_DEFAULT_MACHINING_MODE MachiningMode { get; init; } = EN_DEFAULT_MACHINING_MODE.Shot;
    public double CrossSize { get; init; } = 0.1;
    public int DoeBeamGridSize { get; init; } = 4;
    // Nominal point count for a Head with DOE. DOE_EXIST=OFF is overridden to
    // one center point by DefaultInspectionGridSize.
    public int InspectionPointCount => DoeBeamGridSize * DoeBeamGridSize;
    // The inspection command is part of the machining contract, not an independent
    // operator option. A DOE Head inspects every optical slot; a no-DOE Head
    // inspects its center once. Cross uses the registered setting mark while Shot
    // uses the normal single-mark command.
    public EN_ONE_SHOT_METHOD VisionMethod => MachiningMode == EN_DEFAULT_MACHINING_MODE.Cross
        ? EN_ONE_SHOT_METHOD.MarkFindSettingSingle
        : EN_ONE_SHOT_METHOD.MarkFindSingle;
    public string InspectionDescription => MachiningMode == EN_DEFAULT_MACHINING_MODE.Cross
        ? $"DOE ON {InspectionPointCount}개 십자 / DOE OFF 중앙 십자 1점 측정"
        : $"DOE ON {InspectionPointCount}빔 / DOE OFF 중앙 1점 측정";
    public string MachiningDescription => MachiningMode == EN_DEFAULT_MACHINING_MODE.Cross
        ? $"십자 가공 · DOE ON {DoeBeamGridSize}×{DoeBeamGridSize} / DOE OFF 중앙 1개 형상 · 전체 길이 {CrossSize.ToString("0.#####", CultureInfo.InvariantCulture)} mm · Head Jump Speed 사용"
        : $"샷 가공 · DOE ON {DoeBeamGridSize}×{DoeBeamGridSize} / DOE OFF 중앙 1점 · Head Shot Count 사용";
    public string VisionDescription => VisionMethod == EN_ONE_SHOT_METHOD.MarkFindSettingSingle ? "Mark Find Setting Single" : "Mark Find Single";
}
public sealed record ST_DEFAULT_HEAD_GEOMETRY(double FieldHalfX, double FieldHalfY, double DoeAngle);
public sealed record ST_DEFAULT_PREVIEW_POINT(int Head, double StageY, double X, double Y, bool IsOnGlass);
public sealed record ST_DEFAULT_OFFSET_MARK(int Beam, double X, double Y, double RawX, double RawY, double CorrectionX, double CorrectionY, double EncoderX, double EncoderY, DateTimeOffset CapturedAt, string Judge);
public sealed record ST_DEFAULT_OFFSET_FIT(double X, double Y, double Residual, double PitchX, double PitchY);
public sealed record ST_DEFAULT_OFFSET_HEAD_RESULT(int Head, Guid TestId, double Q, double R, bool AutomationSimulation, bool StageSimulation, bool VisionSimulation, IReadOnlyList<ST_DEFAULT_OFFSET_MARK> Marks, ST_DEFAULT_OFFSET_FIT Fit)
{
    public bool DoeExists { get; init; } = true;
    public int Model { get; init; } = 1;
    public int ReferenceCell { get; init; } = 1;
    public ST_DEFAULT_HEAD_GEOMETRY? Geometry { get; init; }
    public double TargetReviewX { get; init; } = double.NaN;
    public double TargetStageY { get; init; } = double.NaN;
    public double CenterReviewX { get; init; } = double.NaN;
    public double CenterStageY { get; init; } = double.NaN;
    public double CenterErrorX { get; init; } = double.NaN;
    public double CenterErrorY { get; init; } = double.NaN;
}
public sealed record ST_DEFAULT_OFFSET_LOG(Guid SessionId, string Recipe, string Action, DateTimeOffset Time, ST_DEFAULT_OFFSET_OPTIONS Options, IReadOnlyCollection<ST_DEFAULT_OFFSET_HEAD_RESULT> Results, IReadOnlyDictionary<string, string> Before, IReadOnlyDictionary<string, string> After);
