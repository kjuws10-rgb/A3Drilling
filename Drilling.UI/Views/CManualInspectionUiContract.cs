namespace Drilling.UI.Views;

using System.Globalization;

/// <summary>
/// The native workspace reads an immutable projection and delegates every operation to
/// its controller. Painting, selection and panning never call a motion/vision driver.
/// </summary>
public interface IManualInspectionController
{
    CManualInspectionUiSnapshot Snapshot { get; }
    event EventHandler? Changed;
    Task ExecuteAsync(EN_MANUAL_INSPECTION_UI_ACTION action, CManualInspectionUiRequest request,
        CancellationToken cancellationToken = default);
}

public enum EN_MANUAL_INSPECTION_UI_ACTION
{
    LoadCurrentRecipe, LoadResultFile, Refresh, NewSimulation, ImportSession, ExportSession,
    ExportCsv, Move, MoveMeasure, MoveMeasureCorrect, Measure, RetryRemaining, Stop,
    Calculate, Validate, Apply, Rollback, CopyReference, UndoCopy, UpdateLimits, SetAllowManual, UpdateLevels, ShowPoint,
    ReturnToPreviousWorkspace
}

/// <summary>Recipe preview's global DESIGN-mm polygon, before scanner/stage transforms.</summary>
public sealed record CManualInspectionUiLayoutVertex(double Xmm, double Ymm);
public sealed record CManualInspectionUiCellLayout
{
    public int CellNo { get; init; }
    public IReadOnlyList<CManualInspectionUiLayoutVertex> BoundaryMm { get; init; } = [];
    public bool Enabled { get; init; } = true;
}

/// <summary>All offsets/errors in this UI contract are micrometres; positions are millimetres.</summary>
public sealed record CManualInspectionUiPoint
{
    public string Key { get; init; } = "";
    public int CellNo { get; init; }
    public int HeadNo { get; init; }
    public int ShotNo { get; init; }
    public int BeamNo { get; init; }
    public int Row { get; init; }
    public int Column { get; init; }
    public bool IsMasked { get; init; }
    public double DesignXmm { get; init; }
    public double DesignYmm { get; init; }
    public string WorkingJudge { get; init; } = "UNMEASURED";
    public string OriginalJudge { get; init; } = "UNMEASURED";
    public double? WorkingXUm { get; init; }
    public double? WorkingYUm { get; init; }
    public double? OriginalXUm { get; init; }
    public double? OriginalYUm { get; init; }
    public double BaseXUm { get; init; }
    public double BaseYUm { get; init; }
    /// <summary>An actual active offset row exists, including an explicitly saved zero row.</summary>
    public bool HasAppliedOffset { get; init; }
    public int? AppliedLevel { get; init; }
    public double? AppliedMetricUm { get; init; }
    public string DisplayedMeasurementOrigin { get; init; } = "";
    public string MeasurementSourcePath { get; init; } = "";
    public double? CandidateXUm { get; init; }
    public double? CandidateYUm { get; init; }
    /// <summary>Engine-resolved reference F; consumed raw must resolve to B, not B + the same raw again.</summary>
    public double? ReferenceFinalXUm { get; init; }
    public double? ReferenceFinalYUm { get; init; }
    public string Source { get; init; } = "KEPT";
    public bool IsProxy { get; init; }
    public string ReferenceKey { get; init; } = "";
    public string MeasurementToken { get; init; } = "";
    public string Details { get; init; } = "";
    public bool CanUseImportedForCorrection { get; init; }
    /// <summary>
    /// Display coverage is independent of where a point was measured. An active correction row is
    /// measurement-derived history, including interpolated/copied values and explicitly saved zero.
    /// These properties never change native raw, judge, correction eligibility or consumed tokens.
    /// </summary>
    public bool HasWorkingMeasurement => WorkingJudge is "OK" or "NG" && Finite(WorkingXUm) && Finite(WorkingYUm);
    public bool HasMeasurementHistory => !IsMasked && (HasWorkingMeasurement || HasAppliedOffset);
    public bool UsesAppliedMeasurement => !IsMasked && HasAppliedOffset && !HasWorkingMeasurement;
    public double? MeasurementXUm => UsesAppliedMeasurement ? BaseXUm : HasWorkingMeasurement ? WorkingXUm : null;
    public double? MeasurementYUm => UsesAppliedMeasurement ? BaseYUm : HasWorkingMeasurement ? WorkingYUm : null;
    public string MeasurementErrorText => Pair(MeasurementXUm, MeasurementYUm);
    public string MeasurementStatusText => IsMasked ? "Masking" : UsesAppliedMeasurement
        ? "측정됨 · 보정기반" + (WorkingJudge == "ERROR" ? " · 최근 수신 오류" : "")
        : WorkingJudge switch { "OK" => "OK · 측정됨", "NG" => "NG · 측정됨", "ERROR" => "수신 오류", "UNMEASURED" => "미측정", _ => WorkingJudge };
    public string MeasurementBasisText => UsesAppliedMeasurement ? "보정기반 편차 · 기존 적용 Review Offset"
        : HasWorkingMeasurement ? IsProxy ? "대표값 복사 편차" : "측정 결과 편차" : "유효 편차 없음";
    public string MeasurementSourceCode => UsesAppliedMeasurement ? "CORRECTION_BASED" : IsProxy ? "MANUAL" : Source;
    public bool CanBeReference => !IsMasked && !IsProxy && (Source == "MEASURED" || CanUseImportedForCorrection)
        && WorkingJudge is "OK" or "NG" && Finite(WorkingXUm) && Finite(WorkingYUm);
    public string Identity => $"C{CellNo} / H{HeadNo:00} / S{ShotNo} / B{BeamNo}";
    public string WorkingErrorText => Pair(WorkingXUm, WorkingYUm);
    public string OriginalErrorText => Pair(OriginalXUm, OriginalYUm);
    public string BaseOffsetText => Pair(BaseXUm, BaseYUm);
    public string CandidateOffsetText => CandidateXUm.HasValue && CandidateYUm.HasValue
        ? Pair(CandidateXUm, CandidateYUm) : "—";
    public string SourceText => UsesAppliedMeasurement ? "보정기반 측정 · 적용 이력"
        : IsProxy ? "◇ 대표값 복사 · MANUAL" : Source == "MEASURED" ? "직접 측정" : Source == "IMPORTED" ? "불러온 측정 · IMPORTED" : Source;
    public string ReferenceText => string.IsNullOrEmpty(ReferenceKey) ? "—" : ReferenceKey;
    public static bool Finite(double? value) => value.HasValue && double.IsFinite(value.Value);
    public static string Number(double? value, bool sign = false) => Finite(value)
        ? value!.Value.ToString(sign ? "+0.000;-0.000;0.000" : "0.000", CultureInfo.InvariantCulture) : "—";
    public static string Pair(double? x, double? y) => $"{Number(x, true)} / {Number(y, true)}";
}

public sealed record CManualInspectionUiCandidateRow
{
    public string Key { get; init; } = "";
    public string Source { get; init; } = "";
    public double BaseXUm { get; init; }
    public double BaseYUm { get; init; }
    public double DeltaXUm { get; init; }
    public double DeltaYUm { get; init; }
    public double FinalXUm { get; init; }
    public double FinalYUm { get; init; }
    public string BaseText => CManualInspectionUiPoint.Pair(BaseXUm, BaseYUm);
    public string DeltaText => CManualInspectionUiPoint.Pair(DeltaXUm, DeltaYUm);
    public string FinalText => CManualInspectionUiPoint.Pair(FinalXUm, FinalYUm);
}

public sealed record CManualInspectionUiRevision
{
    public string Id { get; init; } = "";
    public string CreatedAt { get; init; } = "";
    public string Reason { get; init; } = "";
    public string DisplayText => $"{CreatedAt} · {Id} · {Reason}";
}

public sealed record CManualInspectionUiResultFile(string Path, string DisplayText, string GlassId,
    DateTimeOffset SavedAt, string Origin);

/// <summary>One inclusive Level scale for every Cell/Head and for measured/applied views. No device calls.</summary>
public static class CManualInspectionLevels
{
    public static double? Metric(double? x, double? y, string metric)
    {
        if (x is not double a || y is not double b || !double.IsFinite(a) || !double.IsFinite(b)) return null;
        a = Math.Abs(a); b = Math.Abs(b);
        if (metric == "MAX_AXIS") return Math.Max(a, b);
        if (metric != "RADIAL") return null;
        var high = Math.Max(a, b); var low = Math.Min(a, b);
        var value = high == 0 ? 0 : high * Math.Sqrt(1 + (low / high) * (low / high));
        return double.IsFinite(value) ? value : null;
    }

    public static int? Evaluate(double? x, double? y, string metric, IReadOnlyList<double> bounds)
    {
        var value = Metric(x, y, metric);
        if (value is null || bounds.Count != 3 || bounds.Any(n => !double.IsFinite(n) || n <= 0)
            || bounds[0] >= bounds[1] || bounds[1] >= bounds[2]) return null;
        for (var i = 0; i < bounds.Count; i++) if (value.Value <= bounds[i]) return i;
        return 3;
    }
}

/// <summary>
/// Version changes whenever the operation/candidate/source/active revision changes.
/// A request captured by a dialog or context menu must be rejected if its version is stale.
/// </summary>
public sealed record CManualInspectionUiSnapshot
{
    public long Version { get; init; }
    public string RecipeId { get; init; } = "—";
    public string RunId { get; init; } = "—";
    public string ActiveRevisionId { get; init; } = "—";
    public string SourcePath { get; init; } = "";
    public bool IsSimulation { get; init; }
    public bool IsBusy { get; init; }
    public bool IsReadOnly { get; init; }
    public bool CanReturnToPreviousWorkspace { get; init; }
    public IReadOnlyList<CManualInspectionUiCellLayout> CellLayouts { get; init; } = [];
    public bool CellLayoutYDown { get; init; } = true;
    public double GlassWidthMm { get; init; }
    public double GlassHeightMm { get; init; }
    public bool HasIncomplete { get; init; }
    public int RemainingCount { get; init; }
    public int CompletedCount { get; init; }
    public int TotalCount { get; init; }
    /// <summary>Last target with verified arrival, not a live motor feedback poll. NaN means unknown.</summary>
    public double StageXmm { get; init; } = double.NaN;
    public double StageYmm { get; init; } = double.NaN;
    public string StatusText { get; init; } = "현재 Recipe 또는 측정 결과를 불러오세요.";
    public string Message { get; init; } = "";
    public string PersistenceText { get; init; } = "작업 상태 · 로컬 파일 보관";
    public bool SyncPending { get; init; }
    public string SynchronizationText { get; init; } = "같은 Recipe·기판의 최근 측정 결과와 활성 보정을 자동으로 확인합니다.";
    public int ImportedResultCount { get; init; }
    public IReadOnlyList<CManualInspectionUiResultFile> RecentResults { get; init; } = [];
    public IReadOnlyList<CManualInspectionUiPoint> Points { get; init; } = [];
    public IReadOnlyList<CManualInspectionUiCandidateRow> CandidateRows { get; init; } = [];
    public IReadOnlyList<CManualInspectionUiRevision> Revisions { get; init; } = [];
    public string CandidateStatus { get; init; } = "후보 없음";
    public bool ValidationPassed { get; init; }
    public bool CanApply { get; init; }
    public bool CanRollback { get; init; }
    public bool CanUndoCopy { get; init; }
    public bool AllowManual { get; init; }
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
    public IReadOnlyList<string> ValidationWarnings { get; init; } = [];
    public double MaxCandidateDeltaUm { get; init; }
    public double MaxCandidateTotalUm { get; init; }
    public double ToleranceXUm { get; init; } = 5;
    public double ToleranceYUm { get; init; } = 5;
    public double DeltaLimitUm { get; init; } = 100;
    public double TotalLimitUm { get; init; } = 1000;
    public double ScannerFieldMm { get; init; } = 10;
    public string LevelMetric { get; init; } = "MAX_AXIS";
    public IReadOnlyList<double> LevelBoundsUm { get; init; } = [5, 10, 20];
    public IReadOnlyList<string> LevelNames { get; init; } = ["정상", "관찰", "주의", "점검"];
}

public sealed record CManualInspectionUiRequest
{
    public long ExpectedVersion { get; init; }
    public IReadOnlyList<string> Keys { get; init; } = [];
    public string? FocusKey { get; init; }
    public string? ReferenceKey { get; init; }
    public string? ResultPath { get; init; }
    public string MeasurementMode { get; init; } = "P2P";
    public string Scenario { get; init; } = "recover";
    public string Fault { get; init; } = "NONE";
    public double Speed { get; init; } = 1;
    public bool AllowManual { get; init; }
    public string Reason { get; init; } = "";
    public double? DeltaLimitUm { get; init; }
    public double? TotalLimitUm { get; init; }
    public double? ScannerFieldMm { get; init; }
    public string? LevelMetric { get; init; }
    public IReadOnlyList<double>? LevelBoundsUm { get; init; }
    public IReadOnlyList<string>? LevelNames { get; init; }
}
