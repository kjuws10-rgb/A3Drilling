using System.Collections.ObjectModel;

namespace Drilling.Common.Review.ManualInspection;

/// <summary>Design-coordinate correction in micrometres. Existing Review Offset CSV uses millimetres.</summary>
public readonly record struct ST_MANUAL_OFFSET(double XUm, double YUm)
{
    public static ST_MANUAL_OFFSET Zero => new(0, 0);
    public bool IsFinite => double.IsFinite(XUm) && double.IsFinite(YUm);
}

/// <summary>The host includes all coordinate-affecting recipe/setting inputs in CoordinateSignature.</summary>
public sealed record ST_MANUAL_CONTEXT(string RecipeId, string RunId, string RevisionId, string CoordinateSignature)
{
    public bool LineageValid { get; init; } = true;
    public string SourceResultPath { get; init; } = "";
}

/// <summary>Actual limits must be supplied by the host. FieldWidthMm is not an equipment model.</summary>
public sealed record ST_MANUAL_LIMITS(double MaxDeltaUm, double MaxTotalUm, double FieldWidthMm);

/// <summary>
/// Immutable last direct measurement. ErrorX/YUm are design-coordinate correction values supplied by
/// the production converter; ScannerErrorX/YUm retain the actual measurement for the original-data UI.
/// RawToken must be stable for the same raw packet, not an attempt number or timestamp.
/// </summary>
public sealed record ST_MANUAL_MEASUREMENT(
    string Judge, double? ErrorXUm, double? ErrorYUm,
    string RawToken, string RecipeId, string RunId, string CoordinateSignature)
{
    public double? ScannerErrorXUm { get; init; }
    public double? ScannerErrorYUm { get; init; }
    public string RawData { get; init; } = "";
    public DateTimeOffset MeasuredAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ST_MANUAL_PROXY(
    string ReferenceKey, string ReferenceJudge, double ErrorXUm, double ErrorYUm,
    ST_MANUAL_OFFSET FinalOffset, string Reason, string RunId, DateTimeOffset CopiedAt)
{
    public string CoordinateSignature { get; init; } = "";
    public string ReferenceMeasurementToken { get; init; } = "";
    public string OriginalSource { get; init; } = "";
}

/// <summary>
/// A displayed location is beam-specific; OffsetKey deliberately is not. The production CSV stores one
/// correction per Cell/Shot. Applying a selected Beam therefore updates the displayed base of its siblings.
/// </summary>
public sealed record ST_MANUAL_POINT(
    string Key, int CellNo, int HeadNo, int ShotNo, int BeamNo, double XMm, double YMm)
{
    public int Row { get; init; }
    public int Column { get; init; }
    public bool Masked { get; init; }
    public string Judge { get; init; } = "UNMEASURED";
    public string Source { get; init; } = "KEPT";
    public double? ErrorXUm { get; init; }
    public double? ErrorYUm { get; init; }
    public ST_MANUAL_MEASUREMENT? OriginalMeasurement { get; init; }
    public ST_MANUAL_PROXY? ProxySource { get; init; }
    public long MeasurementVersion { get; init; }
    /// <summary>External file observation time, including EDITED values whose original raw is unknown.</summary>
    public DateTimeOffset? ExternalObservedAt { get; init; }
    public string OffsetKey => $"C{CellNo}/S{ShotNo}";
    public bool HasFiniteError => ErrorXUm is double x && ErrorYUm is double y && double.IsFinite(x) && double.IsFinite(y);
    public bool IsDirectMeasurement => ProxySource is null && Source == "MEASURED";
}

public enum EN_MANUAL_CANDIDATE_STATE { Draft, Validated, Invalid, Applying, Applied }
public enum EN_MANUAL_OPERATION_KIND { Move, Measure, MoveMeasure, Flying, MeasureAndCorrect }
public enum EN_MANUAL_OPERATION_STATE { Running, Completed, Stopped, Error, CorrectionBlocked }

public sealed record ST_MANUAL_CANDIDATE_ROW(
    string Key, string OffsetKey, int CellNo, int HeadNo, int ShotNo, int BeamNo,
    ST_MANUAL_OFFSET Base, ST_MANUAL_OFFSET Delta, ST_MANUAL_OFFSET Final,
    string Source, string ReferenceKey, string Reason, string MeasurementToken);

public sealed record ST_MANUAL_VALIDATION(
    bool Ok, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
    double MaxDeltaUm, double MaxTotalUm, bool AllowManual, string Fingerprint);

public sealed record ST_MANUAL_CANDIDATE(
    string Id, string RecipeId, string RunId, string BaseRevisionId, string SourceFingerprint,
    IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> Rows, EN_MANUAL_CANDIDATE_STATE State,
    ST_MANUAL_VALIDATION? Validation);

public sealed record ST_MANUAL_COPY_TARGET(
    string Key, string PreviousJudge, ST_MANUAL_OFFSET Base, ST_MANUAL_OFFSET Delta);

public sealed record ST_MANUAL_COPY_PREVIEW(
    string ReferenceKey, IReadOnlyList<string> SelectionKeys, IReadOnlyList<ST_MANUAL_COPY_TARGET> Targets,
    IReadOnlyList<string> ExcludedMaskedKeys, string Judge, double ErrorXUm, double ErrorYUm,
    ST_MANUAL_OFFSET Final, string Fingerprint);

/// <summary>All values are immutable copies; the host writes its real offset file before CommitApplied.</summary>
public sealed record ST_MANUAL_APPLY_SNAPSHOT(
    string Token, string CandidateId, string RecipeId, string RunId, string BaseRevisionId,
    IReadOnlyDictionary<string, ST_MANUAL_OFFSET> ValuesByPointKey,
    IReadOnlyDictionary<string, ST_MANUAL_OFFSET> ValuesByOffsetKey,
    IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> Rows)
{
    /// <summary>
    /// Opaque native raw tokens whose Shot correction basis is consumed by this write, including Beam
    /// siblings. Durable storage records all entries, but only MEASURED driving rows are duplicate-gated.
    /// A previously consumed companion token must not block a new driving measurement of its sibling.
    /// </summary>
    public IReadOnlyDictionary<string, string> ConsumedMeasurements { get; init; }
        = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
}

public sealed record ST_MANUAL_RESTORE_SNAPSHOT(
    string Token, string RecipeId, string RunId, string BaseRevisionId, string RestoreTargetRevisionId,
    IReadOnlyDictionary<string, ST_MANUAL_OFFSET> ValuesByPointKey);

public sealed record ST_MANUAL_REVISION(
    string Id, string ParentId, string Kind, string Reason, DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, ST_MANUAL_OFFSET> ValuesByPointKey);

public sealed record ST_MANUAL_OPERATION_PREVIEW(
    EN_MANUAL_OPERATION_KIND Kind, IReadOnlyList<string> Keys, IReadOnlyList<string> ExcludedMaskedKeys,
    bool IsRetry, string Fingerprint);

public sealed record ST_MANUAL_OPERATION(
    string Id, EN_MANUAL_OPERATION_KIND Kind, EN_MANUAL_OPERATION_STATE State,
    IReadOnlyList<string> Keys, IReadOnlyList<string> CompletedKeys, IReadOnlyList<string> RemainingKeys,
    string RecipeId, string RunId, string BaseRevisionId, string CoordinateSignature,
    IReadOnlyDictionary<string, long> MeasurementVersionsBefore, bool IsRetry, string Error);

public sealed record ST_MANUAL_OPERATION_COMPLETION(
    ST_MANUAL_OPERATION Operation, bool AutoCorrectionAllowed, IReadOnlyList<string> Errors);

/// <summary>Production host must evaluate actual Head axis conversion and current scanner limits.</summary>
public sealed record ST_MANUAL_FIELD_RESULT(bool Ok, string Message)
{
    public double? ScannerXMm { get; init; }
    public double? ScannerYMm { get; init; }
}
public delegate ST_MANUAL_FIELD_RESULT ManualInspectionFieldEvaluator(
    ST_MANUAL_POINT point, ST_MANUAL_OFFSET finalDesignOffsetUm, ST_MANUAL_LIMITS limits);

public sealed record ST_MANUAL_SCATTER(
    int Total, int Count, int ProxyCount, int ExcludedMasked, int ExcludedUnmeasured,
    int ExcludedError, int ExcludedInvalid, double? MeanXUm, double? MeanYUm,
    double? SigmaXUm, double? SigmaYUm, double? MaxVectorUm,
    IReadOnlyList<string> ValidKeys);

public sealed record ST_MANUAL_AUDIT(DateTimeOffset At, string Kind, string Message);
public sealed record ST_MANUAL_WORKING_STATE(string Judge, double? ErrorXUm, double? ErrorYUm, string Source, ST_MANUAL_PROXY? ProxySource);
public sealed record ST_MANUAL_HISTORY(
    DateTimeOffset At, string Key, string Kind, ST_MANUAL_WORKING_STATE Before, ST_MANUAL_WORKING_STATE After,
    ST_MANUAL_MEASUREMENT? OriginalBefore, ST_MANUAL_MEASUREMENT? OriginalAfter, string Reason);
public sealed record ST_MANUAL_CONSUMED_MEASUREMENT(string Key, string Token);

/// <summary>Local session data only; reopening does not authorize equipment execution or automatic correction.</summary>
public sealed record ST_MANUAL_SESSION(
    int SchemaVersion, ST_MANUAL_CONTEXT Context, ST_MANUAL_LIMITS Limits,
    IReadOnlyList<ST_MANUAL_POINT> Points,
    IReadOnlyDictionary<string, ST_MANUAL_OFFSET> ActiveValuesByPointKey,
    IReadOnlyList<ST_MANUAL_REVISION> Revisions,
    IReadOnlyDictionary<string, string> AppliedMeasurements,
    IReadOnlyList<string> AppliedCandidates,
    ST_MANUAL_CANDIDATE? Candidate,
    ST_MANUAL_OPERATION? Operation,
    IReadOnlyList<ST_MANUAL_AUDIT> Audit)
{
    public IReadOnlyList<ST_MANUAL_HISTORY> History { get; init; } = Array.Empty<ST_MANUAL_HISTORY>();
    public IReadOnlyList<ST_MANUAL_CANDIDATE> CandidateBackups { get; init; } = Array.Empty<ST_MANUAL_CANDIDATE>();
    public IReadOnlyList<ST_MANUAL_CONSUMED_MEASUREMENT> ConsumedMeasurementHistory { get; init; } = Array.Empty<ST_MANUAL_CONSUMED_MEASUREMENT>();
}
