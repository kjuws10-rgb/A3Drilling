using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Drilling.Common.Review.ManualInspection;

/// <summary>
/// Portable absolute Review-offset map. Previous version files are audit history, not increments to
/// sum again. Protected settings and final scanner coordinates are reference snapshots only; opening
/// this document never writes those settings and never executes the exported scanner commands.
/// All types in this file are pure data and do not reference equipment, UI or vendor assemblies.
/// </summary>
public sealed record ST_MANUAL_OFFSET_BUNDLE
{
    public string SchemaVersion { get; init; } = "";
    public string ProgramVersion { get; init; } = "";
    public string RecipeId { get; init; } = "";
    public string CoordinateSignature { get; init; } = "";
    public string ActiveOffsetFileName { get; init; } = "";
    public string ActiveOffsetContentSha256 { get; init; } = "";
    public DateTimeOffset ExportedAt { get; init; }
    public bool IsDraft { get; init; }
    public bool InitializeAllReviewRows { get; init; }
    public string ReviewCoordinateSpace { get; init; } = "";
    public string ReviewUnit { get; init; } = "";
    public string FinalCoordinateSpace { get; init; } = "";
    public string FinalCoordinateUnit { get; init; } = "";
    public string ProtectedSnapshotPolicy { get; init; } = "";
    public IReadOnlyList<ST_MANUAL_OFFSET_BUNDLE_POINT> Points { get; init; } = [];
    public IReadOnlyList<ST_MANUAL_OFFSET_BUNDLE_ROW> ReviewRows { get; init; } = [];
    public IReadOnlyList<ST_MANUAL_OFFSET_BUNDLE_HISTORY_ROW> RetainedHistoricalRows { get; init; } = [];
    public IReadOnlyDictionary<string,string> ProtectedParameters { get; init; } = new Dictionary<string,string>();
    public IReadOnlyList<ST_MANUAL_OFFSET_BUNDLE_SHOT> FinalCoordinates { get; init; } = [];
}

public sealed record ST_MANUAL_OFFSET_BUNDLE_POINT(string Key, int CellNo, int HeadNo, int ShotNo,
    int BeamNo, double XMm, double YMm, bool Masked, string ShotId);

/// <summary>One absolute, unscaled DESIGN-mm final Review value for a Cell/Shot shared by its Beams.</summary>
public sealed record ST_MANUAL_OFFSET_BUNDLE_ROW(int CellNo, string ShotId, double XMm, double YMm)
{
    /// <summary>True only for an explicit measurement-offset reset; a measured zero remains false.</summary>
    public bool IsMeasurementOffsetInitialized { get; init; }
    public DateTimeOffset? MeasurementInitializedAt { get; init; }
}

/// <summary>Rows outside the current Recipe/point geometry are retained only as audit, never imported.</summary>
public sealed record ST_MANUAL_OFFSET_BUNDLE_HISTORY_ROW(int CellNo, string ShotId, double XMm,
    double YMm, bool IsDesignCoordinate)
{
    public bool IsMeasurementOffsetInitialized { get; init; }
    public DateTimeOffset? MeasurementInitializedAt { get; init; }
}

/// <summary>
/// Exact current pure-planner output. The aggregate offset and GX/GY are SCANNER mm, with MOF stage
/// following included in GX/GY. EncoderWaitPosition is encoder counts, not mm. Default/First raw
/// values below retain their original Recipe basis and must not be naively added to SCANNER values.
/// </summary>
public sealed record ST_MANUAL_OFFSET_BUNDLE_SHOT(int CellNo, int HeadNo, int ShotNo,
    double DesignXmm, double DesignYmm, double ScannerGxMm, double ScannerGyMm,
    double StageXmm, double StageYmm, double RecipeScannerOffsetXmm, double RecipeScannerOffsetYmm,
    double DefaultRecipeOffsetXmm, double DefaultRecipeOffsetYmm,
    double FirstBeamRecipeOffsetXmm, double FirstBeamRecipeOffsetYmm,
    double DistortionDesignOffsetXmm, double DistortionDesignOffsetYmm,
    double DistortionScannerOffsetXmm, double DistortionScannerOffsetYmm,
    double ReviewScannerOffsetXmm, double ReviewScannerOffsetYmm,
    double ShiftDesignOffsetXmm, double ShiftDesignOffsetYmm,
    double ShiftScannerOffsetXmm, double ShiftScannerOffsetYmm,
    double CombinedScannerOffsetXmm, double CombinedScannerOffsetYmm, double EncoderWaitPosition);

public static class CManualInspectionOffsetBundle
{
    public const string Schema = "a3-manual-inspection-offsets/2";
    public const string LegacySchema = "a3-manual-inspection-offsets/1";
    public const string ProgramVersion = "11.24.8";
    public const string ReferenceOnly = "REFERENCE_ONLY_NEVER_APPLY_PROTECTED_SETTINGS";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly HashSet<string> ProtectedGlobalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "HeadGapX", "HeadGapY", "REVIEW_TO_HEAD1_GAP_Y", "SCAN_ENCODER_SCALE", "STAGE_SCAN_DIRECTION_Y",
        "ESC_EDGE_TO_GLASS_EDGE", "ESC_EDGE_TO_GLASS_EDGE_X", "GLASS_CENTER_TO_SCANNER_CENTER", "GLASS_CENTER_TO_SCANNER_CENTER_X",
        "REVIEW_CAMERA_ALIGN_KEY_POS_X", "REVIEW_CAMERA_ALIGN_KEY_POS_Y",
        "ALIGN_KEY_MARGIN_X", "ALIGN_KEY_MARGIN_Y", "AK_MARGIN_X", "AK_MARGIN_Y",
        "ALIGN_OFFSET_SCALE", "REVIEW_OFFSET_SCALE"
    };
    public static bool IsProtectedParameter(string key)
    {
        if(key.Contains("PASSWORD",StringComparison.OrdinalIgnoreCase) || key.Contains("SECRET",StringComparison.OrdinalIgnoreCase)
            || key.Contains("TOKEN",StringComparison.OrdinalIgnoreCase)) return false;
        return ProtectedGlobalKeys.Contains(key)
            || Regex.IsMatch(key, @"^H0[1-8]_(DEFAULT_OFFSET_[XY]|FIRST_BEAM_OFFSET_[XY]|AK_POSITION_[XY]|ENCODER_SCALE|SCANNER_(XY_FLIP|X_FLIP|Y_FLIP))$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)
            || Regex.IsMatch(key, @"^CELL[1-9][0-9]*_.+_(ALIGN|RECIPE)_OFFSET_[XY]$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    }

    public static string Serialize(ST_MANUAL_OFFSET_BUNDLE value)
    {
        Validate(value);
        return JsonSerializer.Serialize(value with { SchemaVersion = Schema },JsonOptions)+"\n";
    }

    public static ST_MANUAL_OFFSET_BUNDLE Deserialize(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        CheckUniqueProperties(parsed.RootElement);
        var value = parsed.RootElement.Deserialize<ST_MANUAL_OFFSET_BUNDLE>(JsonOptions)
            ?? throw new InvalidDataException("통합 Offset 파일이 비어 있습니다.");
        Validate(value);
        return value;
    }

    public static void Validate(ST_MANUAL_OFFSET_BUNDLE value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if(value.SchemaVersion is not (Schema or LegacySchema) || string.IsNullOrWhiteSpace(value.RecipeId)
            || string.IsNullOrWhiteSpace(value.CoordinateSignature) || string.IsNullOrWhiteSpace(value.ProgramVersion)
            || value.ExportedAt==default || value.ReviewCoordinateSpace!="DESIGN" || value.ReviewUnit!="mm"
            || value.FinalCoordinateSpace!="SCANNER" || value.FinalCoordinateUnit!="mm"
            || value.ProtectedSnapshotPolicy!=ReferenceOnly)
            throw new InvalidDataException("통합 Offset 형식·Recipe·좌표계·단위·보호 설정 정책을 확인하세요.");
        if(value.Points is null || value.ReviewRows is null || value.RetainedHistoricalRows is null
            || value.ProtectedParameters is null || value.FinalCoordinates is null)
            throw new InvalidDataException("통합 Offset의 필수 목록이 없습니다.");
        var pointKeys = new HashSet<string>(StringComparer.Ordinal);
        var rowKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in value.ReviewRows)
            if(row is null || row.CellNo<=0 || !ValidShot(row.ShotId) || !Finite(row.XMm,row.YMm)
                || row.IsMeasurementOffsetInitialized && (row.XMm!=0 || row.YMm!=0 || value.SchemaVersion==LegacySchema)
                || !row.IsMeasurementOffsetInitialized && row.MeasurementInitializedAt.HasValue
                || !rowKeys.Add(RowKey(row.CellNo,row.ShotId)))
                throw new InvalidDataException("통합 Review Offset에 잘못된 값 또는 중복 Cell/Shot이 있습니다.");
        var usedRows = new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in value.Points)
        {
            if(p is null || string.IsNullOrWhiteSpace(p.Key) || !pointKeys.Add(p.Key) || p.CellNo<=0 || p.HeadNo is <0 or >8
                || p.ShotNo<=0 || p.BeamNo<=0 || !Finite(p.XMm,p.YMm) || !ValidShot(p.ShotId)
                || !rowKeys.Contains(RowKey(p.CellNo,p.ShotId)))
                throw new InvalidDataException("통합 Offset의 위치 또는 전체 Review 맵이 올바르지 않습니다.");
            usedRows.Add(RowKey(p.CellNo,p.ShotId));
        }
        if(!usedRows.SetEquals(rowKeys)) throw new InvalidDataException("현재 위치에 없는 Review 행은 이력 영역으로 분리해야 합니다.");
        var historicalKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in value.RetainedHistoricalRows)
            if(row is null || row.CellNo<=0 || !ValidShot(row.ShotId) || !Finite(row.XMm,row.YMm)
                || row.IsMeasurementOffsetInitialized && (row.XMm!=0 || row.YMm!=0 || value.SchemaVersion==LegacySchema)
                || !row.IsMeasurementOffsetInitialized && row.MeasurementInitializedAt.HasValue
                || rowKeys.Contains(RowKey(row.CellNo,row.ShotId)) || !historicalKeys.Add(RowKey(row.CellNo,row.ShotId)))
                throw new InvalidDataException("참고용 이전 Offset 행이 유효하지 않거나 현재 맵과 중복됩니다.");
        foreach(var pair in value.ProtectedParameters)
            if(!IsProtectedParameter(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                throw new InvalidDataException("통합 Offset 보호 설정에 허용되지 않은 항목이 있습니다.");
        var coordinateKeys = new HashSet<(int,int)>();
        foreach(var shot in value.FinalCoordinates)
        {
            if(shot is null || shot.CellNo<=0 || shot.HeadNo is <1 or >8 || shot.ShotNo<=0 || !coordinateKeys.Add((shot.CellNo,shot.ShotNo))
                || !Finite(shot.DesignXmm,shot.DesignYmm,shot.ScannerGxMm,shot.ScannerGyMm,shot.StageXmm,shot.StageYmm,
                    shot.RecipeScannerOffsetXmm,shot.RecipeScannerOffsetYmm,shot.DefaultRecipeOffsetXmm,shot.DefaultRecipeOffsetYmm,
                    shot.FirstBeamRecipeOffsetXmm,shot.FirstBeamRecipeOffsetYmm,shot.DistortionDesignOffsetXmm,shot.DistortionDesignOffsetYmm,
                    shot.DistortionScannerOffsetXmm,shot.DistortionScannerOffsetYmm,shot.ReviewScannerOffsetXmm,shot.ReviewScannerOffsetYmm,
                    shot.ShiftDesignOffsetXmm,shot.ShiftDesignOffsetYmm,shot.ShiftScannerOffsetXmm,shot.ShiftScannerOffsetYmm,
                    shot.CombinedScannerOffsetXmm,shot.CombinedScannerOffsetYmm,shot.EncoderWaitPosition))
                throw new InvalidDataException("참고용 최종 좌표에 잘못된 수치 또는 중복 Shot이 있습니다.");
        }
    }

    public static string RowKey(int cell,string shot) => cell.ToString(CultureInfo.InvariantCulture)+":"+shot.ToUpperInvariant();
    private static bool ValidShot(string? value) => !string.IsNullOrWhiteSpace(value)
        && value==value.Trim().ToUpperInvariant() && value.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_');
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private static void CheckUniqueProperties(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var p in value.EnumerateObject())
            {
                if(!names.Add(p.Name)) throw new InvalidDataException("통합 Offset JSON에 중복 항목이 있습니다.");
                CheckUniqueProperties(p.Value);
            }
        }
        else if(value.ValueKind==JsonValueKind.Array) foreach(var p in value.EnumerateArray())CheckUniqueProperties(p);
    }
}
