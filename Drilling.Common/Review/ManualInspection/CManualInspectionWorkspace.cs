using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Drilling.Common.Review.ManualInspection;

/// <summary>
/// Pure, equipment-independent state machine for the new Manual Inspection tab.
/// The host owns interlocks, actual move/measure calls and atomic file persistence. This class never
/// sends a motion command or writes an offset file. Design offsets are µm throughout this boundary.
/// The owning controller calls this synchronous engine on its UI owner thread; it must marshal worker
/// callbacks to that thread. The pending-write reservation is a state guard, not a thread synchronizer.
/// </summary>
public sealed class CManualInspectionWorkspace
{
    private readonly Dictionary<string, ST_MANUAL_POINT> _points = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ST_MANUAL_OFFSET> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _appliedMeasurements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _consumedMeasurementHistory = new(StringComparer.Ordinal);
    private readonly HashSet<string> _appliedCandidates = new(StringComparer.Ordinal);
    private readonly List<ST_MANUAL_REVISION> _revisions = [];
    private readonly List<ST_MANUAL_AUDIT> _audit = [];
    private readonly List<ST_MANUAL_HISTORY> _history = [];
    private readonly List<ST_MANUAL_CANDIDATE> _candidateBackups = [];
    private ManualInspectionFieldEvaluator? _fieldEvaluator;
    private CopyUndo? _copyUndo;
    private ST_MANUAL_CANDIDATE? _offsetEditPreviousCandidate;
    private string? _pendingWriteToken;
    private string? _pendingWriteFingerprint;
    private ST_MANUAL_APPLY_SNAPSHOT? _pendingApply;
    private ST_MANUAL_RESTORE_SNAPSHOT? _pendingRestore;
    private IReadOnlyList<ST_MANUAL_CANDIDATE_ROW>? _referenceRows;
    private Dictionary<string, ST_MANUAL_CANDIDATE_ROW> _referenceRowsByKey = new(StringComparer.Ordinal);
    private long _generation;

    public ST_MANUAL_CONTEXT? Context { get; private set; }
    public ST_MANUAL_LIMITS? Limits { get; private set; }
    public IReadOnlyList<ST_MANUAL_POINT> Points => Array.AsReadOnly(_points.Values.ToArray());
    public IReadOnlyDictionary<string, ST_MANUAL_OFFSET> ActiveOffsets => Freeze(_active);
    public IReadOnlyList<ST_MANUAL_REVISION> Revisions => _revisions.AsReadOnly();
    public IReadOnlyList<ST_MANUAL_AUDIT> Audit => _audit.AsReadOnly();
    public IReadOnlyList<ST_MANUAL_HISTORY> History => _history.AsReadOnly();
    public IReadOnlyList<ST_MANUAL_CANDIDATE> CandidateBackups => _candidateBackups.AsReadOnly();
    public ST_MANUAL_CANDIDATE? Candidate { get; private set; }
    public bool HasPendingOffsetEdit => Candidate is { IsOffsetReplacement: true, State: not EN_MANUAL_CANDIDATE_STATE.Applied };
    public ST_MANUAL_OPERATION? Operation { get; private set; }
    public bool HasIncompleteMeasurements => Operation is { Kind: not EN_MANUAL_OPERATION_KIND.Move, RemainingKeys.Count: > 0 };
    public bool IsBusy => _pendingWriteToken is not null || Operation?.State == EN_MANUAL_OPERATION_STATE.Running;
    public bool CanUndoReferenceCopy => _copyUndo is not null && !IsBusy && !HasIncompleteMeasurements && _copyUndo.AfterFingerprint == Fingerprint();

    /// <summary>
    /// Load a real Recipe/Run. Source lineage is not invented for imported results. A reload of the same
    /// Run retains the direct-measurement application ledger, preventing an easy same-result reset bypass.
    /// </summary>
    public void Load(
        ST_MANUAL_CONTEXT context,
        IEnumerable<ST_MANUAL_POINT> points,
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> baseValuesByPointKey,
        ST_MANUAL_LIMITS limits,
        ManualInspectionFieldEvaluator? fieldEvaluator)
    {
        GuardIdle();
        ValidateContext(context); ValidateLimits(limits);
        var sameRun = Context?.RecipeId == context.RecipeId && Context.RunId == context.RunId;
        if (sameRun && HasPendingOffsetEdit) throw new InvalidOperationException("통합 Offset 편집을 저장하거나 취소한 후 작업공간을 다시 불러오세요.");
        if (sameRun && HasIncompleteMeasurements)
            throw new InvalidOperationException("같은 Run의 미완료 측정을 재로드로 지울 수 없습니다. 잔여 측정을 완료하거나 새 Run을 여세요.");
        var data = points.ToArray();
        if (data.Length == 0) throw new InvalidOperationException("작업공간에 표시할 위치가 없습니다.");
        if (data.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != data.Length)
            throw new InvalidOperationException("측정 위치 Key가 중복됐습니다.");
        var values = new Dictionary<string, ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        var offsetGroups = new Dictionary<string, ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        foreach (var point in data)
        {
            ValidatePoint(point);
            if (point.AdoptedFileMeasurement is { } adopted)
            {
                ValidateMeasurement(adopted,context);
                if (point.Source is not ("IMPORTED" or "MANUAL"))
                    throw new InvalidOperationException("파일 입력 근거를 직접 카메라 원측정으로 바꿀 수 없습니다.");
            }
            var value = baseValuesByPointKey.TryGetValue(point.Key, out var supplied) ? supplied : ST_MANUAL_OFFSET.Zero;
            if (!value.IsFinite) throw new InvalidOperationException($"{point.Key}: 현재 보정값이 유효한 숫자가 아닙니다.");
            if (offsetGroups.TryGetValue(point.OffsetKey, out var previous) && !Equal(previous, value))
                throw new InvalidOperationException($"{point.OffsetKey}: 같은 Cell/Shot의 Beam별 현재 보정값이 서로 다릅니다.");
            offsetGroups[point.OffsetKey] = value; values.Add(point.Key, value);
        }
        _points.Clear(); foreach (var point in data) _points.Add(point.Key, point);
        _active.Clear(); foreach (var pair in values) _active.Add(pair.Key, pair.Value);
        if (!sameRun) { _appliedMeasurements.Clear(); _consumedMeasurementHistory.Clear(); _appliedCandidates.Clear(); }
        Context = context; Limits = limits; _fieldEvaluator = fieldEvaluator;
        Candidate = null; Operation = null; _copyUndo = null; _offsetEditPreviousCandidate = null;
        _referenceRows = null; _referenceRowsByKey.Clear();
        _history.Clear(); _candidateBackups.Clear();
        _revisions.Clear();
        _revisions.Add(new(context.RevisionId, "", "INITIAL", "현재 실제 Review Offset 기준", DateTimeOffset.UtcNow, Freeze(_active)));
        _generation++; Log("LOAD", $"{context.RecipeId} / {context.RunId} / {data.Length}점");
    }

    public ST_MANUAL_POINT GetPoint(string key) => _points.TryGetValue(key, out var point)
        ? point : throw new InvalidOperationException($"작업공간에 없는 위치입니다: {key}");

    /// <summary>After the host rechecks real geometry, old uncertain observations stay non-native but fresh measurement is allowed.</summary>
    public void ConfirmEditableMeasurementContext(ST_MANUAL_CONTEXT context)
    {
        GuardIdle(); RequireLoaded(); ValidateContext(context);
        if (context.RecipeId!=Context!.RecipeId || context.RunId!=Context.RunId || context.RevisionId!=Context.RevisionId
            || context.CoordinateSignature!=Context.CoordinateSignature || !context.LineageValid)
            throw new InvalidOperationException("현재 모델·회차·좌표·활성 보정 기준을 다시 연결해야 합니다.");
        if (Context.LineageValid) return;
        foreach (var before in _points.Values.ToArray())
        {
            var after=before with { Source="IMPORTED",ProxySource=null,AdoptedFileMeasurement=null };
            _points[before.Key]=after;
            AddHistory(before,after,"EDITABLE_RECONNECT","현재 기하 재확인 · 옛 측정은 관측으로 보존하고 새 측정 허용");
        }
        Context=context;Candidate=null;_offsetEditPreviousCandidate=null;_generation++;_copyUndo=null;
        Log("EDITABLE_RECONNECT","이전 출처 미확인 자료를 새 raw로 승격하지 않고 현재 모델에서 새 측정할 수 있습니다.");
    }

    /// <summary>
    /// Refresh display observations after reconciling the actual Recipe/Run/offset revision. Imported
    /// CSV values remain IMPORTED, never native correction anchors. A same-value save notification
    /// from our own measurement cannot downgrade its direct raw. Older observations cannot erase a
    /// newer native measurement or representative copy; all replaced values remain in point history.
    /// </summary>
    public int MergeImportedObservations(IEnumerable<ST_MANUAL_POINT> observations,
        IReadOnlyDictionary<string, DateTimeOffset> observedAtByKey)
    {
        GuardIdle(); RequireLoaded();
        if (HasIncompleteMeasurements || HasPendingOffsetEdit) return 0;
        var replacements = new List<(ST_MANUAL_POINT Before, ST_MANUAL_POINT After)>();
        foreach (var incoming in observations)
        {
            if (incoming.Source != "IMPORTED" || !observedAtByKey.TryGetValue(incoming.Key, out var observedAt)) continue;
            var previous = GetPoint(incoming.Key); ValidatePoint(incoming);
            if (previous.CellNo != incoming.CellNo || previous.HeadNo != incoming.HeadNo || previous.ShotNo != incoming.ShotNo
                || previous.BeamNo != incoming.BeamNo || previous.Row != incoming.Row || previous.Column != incoming.Column
                || previous.XMm != incoming.XMm || previous.YMm != incoming.YMm || previous.Masked != incoming.Masked)
                throw new InvalidOperationException("외부 관측의 실제 위치 기준이 다릅니다.");
            if (previous.Masked || incoming.Judge == "UNMEASURED") continue;
            if (incoming.OriginalMeasurement is { } raw) ValidateMeasurement(raw, Context!);
            if (incoming.AdoptedFileMeasurement is { } adopted) ValidateMeasurement(adopted,Context!);
            var existingAt = previous.ProxySource?.CopiedAt
                ?? (previous.Source == "IMPORTED" ? previous.ExternalObservedAt : null)
                ?? previous.OriginalMeasurement?.MeasuredAt ?? DateTimeOffset.MinValue;
            if (observedAt < existingAt) continue;
            if (previous.Judge == incoming.Judge && previous.ErrorXUm == incoming.ErrorXUm && previous.ErrorYUm == incoming.ErrorYUm
                && (previous.Source == "MEASURED" || previous.Source == "IMPORTED" && previous.OriginalMeasurement == incoming.OriginalMeasurement
                    && previous.ExternalObservedAt == observedAt)) continue;
            replacements.Add((previous, incoming with { MeasurementVersion = previous.MeasurementVersion + 1, ExternalObservedAt = observedAt }));
        }
        foreach (var (before, after) in replacements)
        {
            _points[after.Key] = after;
            AddHistory(before, after, after.AdoptedFileMeasurement is null ? "EXTERNAL_OBSERVATION" : "FILE_REFRESH",
                after.AdoptedFileMeasurement is null ? "같은 기준의 최신 외부 측정 표시; 직접 보정 앵커로 승인하지 않음" : "명시적으로 연 파일의 측정값 갱신; 카메라 원측정과 출처 분리");
        }
        if (replacements.Count > 0) { Changed(); Log("EXTERNAL_OBSERVATION", $"외부 측정 {replacements.Count}점 표시 갱신"); }
        return replacements.Count;
    }

    public void SetLimits(ST_MANUAL_LIMITS limits)
    {
        GuardIdle(); RequireLoaded(); ValidateLimits(limits); Limits = limits; Changed();
        Log("LIMITS", "보정 한계가 바뀌어 후보 검증을 무효화했습니다.");
    }

    public void UpdateContext(ST_MANUAL_CONTEXT context)
    {
        GuardIdle(); RequireLoaded(); ValidateContext(context);
        if (context.RecipeId != Context!.RecipeId || context.RunId != Context.RunId || context.RevisionId != Context.RevisionId)
            throw new InvalidOperationException("Recipe/Run/활성 보정 변경은 실제 기준값과 함께 작업공간을 다시 로드해야 합니다.");
        if (HasIncompleteMeasurements && context.CoordinateSignature != Context.CoordinateSignature)
            throw new InvalidOperationException("미완료 측정 중 좌표 기준을 바꿀 수 없습니다. 잔여 측정을 마치거나 새 Run으로 로드하세요.");
        Context = context; Changed(); Log("CONTEXT", "좌표/출처 기준 변경으로 후보 검증을 무효화했습니다.");
    }

    /// <summary>Called after the real measurement API returns. A proxy never overwrites original raw data.</summary>
    public void RecordMeasurement(string key, ST_MANUAL_MEASUREMENT measurement, string? operationId = null)
    {
        RequireLoaded(); GuardNoWrite(); GuardNoOffsetEdit();
        var point = GetPoint(key);
        if (point.Masked) throw new InvalidOperationException("Masking 위치는 측정할 수 없습니다.");
        if (Operation?.State == EN_MANUAL_OPERATION_STATE.Running)
        {
            if (operationId != Operation.Id || !Operation.Keys.Contains(key, StringComparer.Ordinal))
                throw new InvalidOperationException("현재 작업에 속하지 않은 측정 결과입니다.");
        }
        else if (operationId is not null) throw new InvalidOperationException("이미 종료된 작업의 늦은 측정 응답입니다.");
        ValidateMeasurement(measurement, Context!);
        _points[key] = point with
        {
            Judge = measurement.Judge, Source = "MEASURED", ErrorXUm = measurement.ErrorXUm,
            ErrorYUm = measurement.ErrorYUm, OriginalMeasurement = measurement, AdoptedFileMeasurement = null, ProxySource = null,
            MeasurementVersion = point.MeasurementVersion + 1, ExternalObservedAt = null
        };
        AddHistory(point, _points[key], "MEASURE", "직접 재측정; 이전 원측정/작업상태 보존");
        Changed(); Log("MEASURE", $"{key}: {measurement.Judge}; 원측정 기록 갱신");
    }

    public ST_MANUAL_CANDIDATE BuildDirectCandidate(IEnumerable<string> keys)
    {
        GuardCorrection(); GuardNoOffsetEdit();
        var selected = Select(keys, excludeMasked: true);
        if (selected.Length == 0) throw new InvalidOperationException("보정할 측정 위치를 선택하세요.");
        var rows = new List<ST_MANUAL_CANDIDATE_ROW>();
        foreach (var key in selected)
        {
            var point = GetPoint(key); var before = _active[key];
            if (point.ProxySource is { } proxy)
                rows.Add(new(key, point.OffsetKey, point.CellNo, point.HeadNo, point.ShotNo, point.BeamNo,
                    before, Subtract(proxy.FinalOffset, before), proxy.FinalOffset, "MANUAL", proxy.ReferenceKey, proxy.Reason, ""));
            else if (point.IsDirectMeasurement && point.HasFiniteError)
            {
                var delta = new ST_MANUAL_OFFSET(point.ErrorXUm!.Value, point.ErrorYUm!.Value);
                var final = Add(before, delta);
                if (!final.IsFinite) throw new InvalidOperationException($"{key}: B + 측정 편차의 수치가 표현 범위를 초과했습니다.");
                rows.Add(new(key, point.OffsetKey, point.CellNo, point.HeadNo, point.ShotNo, point.BeamNo,
                    before, delta, final, "MEASURED", key, point.AdoptedFileMeasurement is not null ? "명시적으로 불러온 파일의 측정값" : "", MeasurementToken(point)));
            }
            else throw new InvalidOperationException($"{key}: 직접 측정 또는 유효한 대표 복사가 필요합니다.");
        }
        if (Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied }) _candidateBackups.Add(Candidate);
        Candidate = NewCandidate(rows); _generation++; _copyUndo = null;
        Log("CANDIDATE", $"선택 {rows.Count}점의 B + Δ = F 후보를 계산했습니다.");
        return Candidate;
    }

    /// <summary>Prepare a whole-model absolute Review map; no active value, raw packet or file is changed.</summary>
    public ST_MANUAL_CANDIDATE BuildOffsetReplacementCandidate(
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values, string reason, bool initializeAllReviewRows)
    {
        GuardCorrection(); GuardNoOffsetEdit();
        if (string.IsNullOrWhiteSpace(reason) || values.Count != _points.Count || values.Keys.Any(key => !_points.ContainsKey(key)))
            throw new InvalidOperationException("통합 Offset은 현재 모델의 모든 위치와 사유가 필요합니다.");
        var byShot = new Dictionary<string, ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        var rows = new List<ST_MANUAL_CANDIDATE_ROW>();
        foreach (var point in _points.Values)
        {
            var final = values[point.Key]; var before = _active[point.Key];
            if (!final.IsFinite || initializeAllReviewRows && final != ST_MANUAL_OFFSET.Zero)
                throw new InvalidOperationException("초기화는 측정 기반 Review Offset 전체를 0으로만 설정합니다.");
            if (byShot.TryGetValue(point.OffsetKey, out var other) && !Equal(other, final))
                throw new InvalidOperationException("같은 Cell/Shot의 Beam은 같은 통합 Offset을 사용해야 합니다.");
            byShot[point.OffsetKey] = final;
            rows.Add(new(point.Key,point.OffsetKey,point.CellNo,point.HeadNo,point.ShotNo,point.BeamNo,
                before,Subtract(final,before),final,initializeAllReviewRows?"OFFSET_INIT":"OFFSET_FILE","",reason.Trim(),""));
        }
        _offsetEditPreviousCandidate = Candidate;
        if (Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied }) _candidateBackups.Add(Candidate);
        Candidate = NewCandidate(rows) with { IsOffsetReplacement=true, InitializeAllReviewRows=initializeAllReviewRows };
        _generation++; _copyUndo=null;
        Log("OFFSET_EDIT", "모델 전체 측정 보정의 저장 전 작업값입니다. 보호 설정과 활성 파일은 유지합니다.");
        return Candidate;
    }

    public ST_MANUAL_CANDIDATE InitializeMeasurementOffsets() => BuildOffsetReplacementCandidate(
        _points.Keys.ToDictionary(key=>key,_=>ST_MANUAL_OFFSET.Zero,StringComparer.Ordinal),
        "Offset Init · Simple/Rough/Fine/One/All/Line 측정 보정 전체 초기화 · 보호 설정 유지",true);

    public void CancelOffsetReplacement()
    {
        GuardCorrection();
        if (!HasPendingOffsetEdit) throw new InvalidOperationException("취소할 통합 Offset 편집이 없습니다.");
        Candidate=_offsetEditPreviousCandidate; _offsetEditPreviousCandidate=null;
        if (Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied }) Candidate=Candidate with { State=EN_MANUAL_CANDIDATE_STATE.Draft,Validation=null };
        _generation++; _copyUndo=null;
        Log("OFFSET_EDIT_CANCEL", "저장 전 통합 Offset 편집 취소 · 활성값과 원측정 유지");
    }

    /// <summary>
    /// The UI and copy operation share one authoritative representative F. A raw measurement already
    /// consumed by a saved correction must not be added to its new B a second time. Untouched reference
    /// Shots remain usable for several independent target groups. Working/original measurement errors
    /// are never replaced with this resolved final correction.
    /// </summary>
    public ST_MANUAL_OFFSET ResolveReferenceFinal(string referenceKey)
    {
        RequireLoaded(); var reference = GetPoint(referenceKey);
        if (!reference.IsDirectMeasurement || reference.Masked || reference.Judge is not ("OK" or "NG") || !reference.HasFiniteError)
            throw new InvalidOperationException("직접 측정한 OK 또는 NG 점만 대표값으로 사용할 수 있습니다. 복사본은 대표점이 아닙니다.");
        RequireMeasurementLineage(reference);
        var token = MeasurementToken(reference);
        if (WasConsumed(referenceKey, token))
            return _active[referenceKey];

        ST_MANUAL_CANDIDATE_ROW? row = null;
        if (Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied })
        {
            GuardCandidateBase(Candidate);
            if (!ReferenceEquals(_referenceRows, Candidate.Rows))
            {
                _referenceRowsByKey = Candidate.Rows.ToDictionary(item => item.Key, StringComparer.Ordinal);
                _referenceRows = Candidate.Rows;
            }
            _referenceRowsByKey.TryGetValue(referenceKey, out row);
        }
        var error = new ST_MANUAL_OFFSET(reference.ErrorXUm!.Value, reference.ErrorYUm!.Value);
        if (row is not null && (row.Source != "MEASURED" || row.MeasurementToken != token
            || row.OffsetKey != reference.OffsetKey || !Equal(row.Base, _active[referenceKey])
            || !Equal(row.Delta, error) || !Equal(Add(row.Base, row.Delta), row.Final)))
            throw new InvalidOperationException("대표점 후보가 현재 직접 원측정/기준과 다릅니다. 후보를 다시 계산하세요.");
        var final = row?.Final ?? Add(_active[referenceKey], error);
        if (!final.IsFinite) throw new InvalidOperationException("대표 최종 보정값이 유효하지 않습니다.");
        return final;
    }

    public ST_MANUAL_COPY_PREVIEW PreviewReferenceCopy(string referenceKey, IEnumerable<string> selectedKeys)
    {
        GuardCorrection(); GuardNoOffsetEdit(); var reference = GetPoint(referenceKey);
        var final = ResolveReferenceFinal(referenceKey);
        if (Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied }) GuardCandidateBase(Candidate);
        var selection = Select(selectedKeys, excludeMasked: false);
        var requested = selection.Where(key => key != referenceKey).ToArray();
        if (requested.Length == 0) throw new InvalidOperationException("대표점 이외의 복사 대상을 선택하세요.");
        var excluded = requested.Where(key => GetPoint(key).Masked).ToArray();
        var targets = requested.Where(key => !GetPoint(key).Masked).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("선택 대상이 모두 Masking 영역입니다.");
        var rows = targets.Select(key => new ST_MANUAL_COPY_TARGET(key, GetPoint(key).Judge, _active[key], Subtract(final, _active[key]))).ToArray();
        return new(referenceKey, ReadOnly(selection), ReadOnly(rows), ReadOnly(excluded), reference.Judge,
            reference.ErrorXUm!.Value, reference.ErrorYUm!.Value, final, CopyFingerprint(referenceKey, selection));
    }

    public ST_MANUAL_CANDIDATE ConfirmReferenceCopy(ST_MANUAL_COPY_PREVIEW preview, IEnumerable<string> selectedKeys, string reason)
    {
        GuardCorrection();
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("대표 복사 사유를 입력하세요.");
        var current = PreviewReferenceCopy(preview.ReferenceKey, selectedKeys);
        if (preview.Fingerprint != current.Fingerprint || Digest(preview) != Digest(current))
            throw new InvalidOperationException("검토 뒤 측정·선택·후보·보정 기준이 바뀌었습니다. 다시 미리보기 하세요.");
        var beforeCandidate = Candidate;
        var beforePoints = current.Targets.ToDictionary(row => row.Key, row => GetPoint(row.Key), StringComparer.Ordinal);
        var rows = Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied }
            ? Candidate.Rows.ToDictionary(row => row.Key, StringComparer.Ordinal) : new Dictionary<string, ST_MANUAL_CANDIDATE_ROW>(StringComparer.Ordinal);
        var at = DateTimeOffset.UtcNow;
        foreach (var target in current.Targets)
        {
            var point = GetPoint(target.Key);
            // An explicitly unmeasured/error state is a truthful original state, not an invented measurement.
            // A missing successful raw measurement remains missing and is never fabricated from copied values.
            var original = point.OriginalMeasurement;
            if (original is null && point.Judge is "ERROR" or "UNMEASURED")
                original = new(point.Judge, point.ErrorXUm, point.ErrorYUm, "", Context!.RecipeId, Context.RunId, Context.CoordinateSignature)
                { MeasuredAt = DateTimeOffset.MinValue };
            _points[target.Key] = point with
            {
                Judge = current.Judge, ErrorXUm = current.ErrorXUm, ErrorYUm = current.ErrorYUm, Source = "MANUAL",
                OriginalMeasurement = original,
                ProxySource = new(current.ReferenceKey, current.Judge, current.ErrorXUm, current.ErrorYUm, current.Final, reason.Trim(), Context!.RunId, at)
                { CoordinateSignature = Context.CoordinateSignature, ReferenceMeasurementToken = MeasurementToken(GetPoint(current.ReferenceKey)), OriginalSource = point.ProxySource?.OriginalSource ?? point.Source }
            };
            AddHistory(point, _points[target.Key], "REFERENCE_COPY", reason.Trim());
            rows[target.Key] = new(target.Key, point.OffsetKey, point.CellNo, point.HeadNo, point.ShotNo, point.BeamNo,
                target.Base, target.Delta, current.Final, "MANUAL", current.ReferenceKey, reason.Trim(), "");
        }
        _generation++; Candidate = NewCandidate(rows.Values); _copyUndo = new(beforeCandidate, beforePoints, Fingerprint());
        Log("REFERENCE_COPY", $"{current.ReferenceKey} → {current.Targets.Count}점; 작업판정·편차·최종 F 복사, 원측정 보존");
        return Candidate;
    }

    public void UndoReferenceCopy()
    {
        GuardCorrection(); var undo = _copyUndo ?? throw new InvalidOperationException("되돌릴 대표 복사가 없습니다.");
        if (undo.AfterFingerprint != Fingerprint())
            throw new InvalidOperationException("복사 뒤 측정·설정·후보·활성 보정이 바뀌어 간편 되돌리기를 차단했습니다.");
        Candidate = undo.Candidate;
        foreach (var pair in undo.Points)
        {
            AddHistory(GetPoint(pair.Key), pair.Value, "REFERENCE_COPY_UNDO", "직전 대표 복사 취소");
            _points[pair.Key] = pair.Value;
        }
        _copyUndo = null; _generation++;
        Log("REFERENCE_COPY_UNDO", "직전 대표 복사의 작업상태와 후보를 복원했습니다.");
    }

    public ST_MANUAL_VALIDATION ValidateCandidate(bool allowManual)
    {
        GuardCorrection();
        var candidate = Candidate ?? throw new InvalidOperationException("먼저 선택점 보정 후보를 계산하세요.");
        var errors = new List<string>(); var warnings = new List<string>();
        if (candidate.State is EN_MANUAL_CANDIDATE_STATE.Applied or EN_MANUAL_CANDIDATE_STATE.Applying || _appliedCandidates.Contains(candidate.Id))
            errors.Add("이미 적용했거나 저장 중인 후보입니다.");
        try { GuardCandidateBase(candidate); } catch (InvalidOperationException error) { errors.Add(error.Message); }
        if (candidate.SourceFingerprint != SourceFingerprint()) errors.Add("계산 뒤 측정·상태·설정·활성 보정이 바뀌었습니다. 다시 계산하세요.");
        if (_fieldEvaluator is null) errors.Add("실제 Head 좌표변환/Scanner Field 검증기가 연결되지 않아 적용할 수 없습니다.");
        var maxDelta = 0d; var maxTotal = 0d;
        var finals = new Dictionary<string, ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (candidate.IsOffsetReplacement && (candidate.Rows.Count!=_points.Count || !candidate.Rows.Select(r=>r.Key).ToHashSet(StringComparer.Ordinal).SetEquals(_points.Keys)))
            errors.Add("통합 Offset 교체 후보는 모델 전체 위치를 포함해야 합니다.");
        foreach (var row in candidate.Rows)
        {
            if (!seen.Add(row.Key)) { errors.Add($"{row.Key}: 후보 Key가 중복됐습니다."); continue; }
            if (!_points.TryGetValue(row.Key, out var point) || point.Masked && !candidate.IsOffsetReplacement) { errors.Add($"{row.Key}: 없거나 Masking된 위치입니다."); continue; }
            if (row.OffsetKey != point.OffsetKey) errors.Add($"{row.Key}: Cell/Shot 저장 식별자가 다릅니다.");
            if (!row.Base.IsFinite || !row.Delta.IsFinite || !row.Final.IsFinite) { errors.Add($"{row.Key}: 보정값이 유한하지 않습니다."); continue; }
            if (!Equal(row.Base, _active[row.Key]) || !Equal(Add(row.Base, row.Delta), row.Final)) errors.Add($"{row.Key}: 기준 B 또는 B + Δ = F가 일치하지 않습니다.");
            if (!candidate.IsOffsetReplacement && point.Judge != "OK") errors.Add($"{row.Key}: 작업판정 {point.Judge}는 보정을 적용할 수 없습니다.");
            if (candidate.IsOffsetReplacement)
            {
                if (row.Source!=(candidate.InitializeAllReviewRows?"OFFSET_INIT":"OFFSET_FILE") || string.IsNullOrWhiteSpace(row.Reason)
                    || row.MeasurementToken.Length>0 || candidate.InitializeAllReviewRows && row.Final!=ST_MANUAL_OFFSET.Zero)
                    errors.Add($"{row.Key}: 통합 Offset 교체/초기화 출처가 일치하지 않습니다.");
            }
            else if (row.Source == "MANUAL")
            {
                if (!allowManual || string.IsNullOrWhiteSpace(row.Reason)) errors.Add($"{row.Key}: 수동 후보 허용과 사유 확인이 필요합니다.");
                if (point.ProxySource is null || point.ProxySource.ReferenceKey != row.ReferenceKey || !Equal(point.ProxySource.FinalOffset, row.Final))
                    errors.Add($"{row.Key}: 대표 복사 출처와 후보가 일치하지 않습니다.");
                if (point.ProxySource is { } proxy && (proxy.RunId != Context!.RunId || proxy.CoordinateSignature != Context.CoordinateSignature || string.IsNullOrWhiteSpace(proxy.ReferenceMeasurementToken)))
                    errors.Add($"{row.Key}: 대표 복사 후 좌표 기준/Run이 바뀌었거나 원측정 계보가 없습니다. 새 기준으로 복사하세요.");
            }
            else if (row.Source == "MEASURED")
            {
                if (!point.IsDirectMeasurement || !point.HasFiniteError || row.MeasurementToken != MeasurementToken(point)) errors.Add($"{row.Key}: 직접 원측정이 바뀌었습니다.");
                if (point.HasFiniteError && !Equal(row.Delta, new(point.ErrorXUm!.Value, point.ErrorYUm!.Value)))
                    errors.Add($"{row.Key}: 직접 측정 후보의 증감이 현재 원측정 편차와 다릅니다.");
                try { RequireMeasurementLineage(point); } catch (InvalidOperationException error) { errors.Add(error.Message); }
                if (WasConsumed(point.Key, row.MeasurementToken) && Magnitude(row.Delta) > 1e-9)
                    errors.Add($"{row.Key}: 같은 기판/Run의 동일 원측정은 재누적할 수 없습니다. 새 가공 결과/Run을 사용하세요.");
            }
            else errors.Add($"{row.Key}: 지원하지 않는 후보 출처입니다.");
            maxDelta = Math.Max(maxDelta, Magnitude(row.Delta)); maxTotal = Math.Max(maxTotal, Magnitude(row.Final));
            if (!(candidate.IsOffsetReplacement && candidate.InitializeAllReviewRows) && Magnitude(row.Delta) > Limits!.MaxDeltaUm) errors.Add($"{row.Key}: 이번 증감이 {Limits.MaxDeltaUm:G} µm 제한을 초과했습니다.");
            if (Magnitude(row.Final) > Limits!.MaxTotalUm) errors.Add($"{row.Key}: 누적 총량이 {Limits.MaxTotalUm:G} µm 제한을 초과했습니다.");
            if (finals.TryGetValue(row.OffsetKey, out var other) && !Equal(other, row.Final)) errors.Add($"{row.OffsetKey}: 기존 CSV는 Cell/Shot별 한 값입니다. Beam별 서로 다른 F를 저장할 수 없습니다.");
            else finals[row.OffsetKey] = row.Final;
        }
        if (candidate.Rows.Count == 0) errors.Add("적용할 보정 후보가 없습니다.");
        // A saved Shot correction affects every displayed Beam with the same OffsetKey. Check all of them,
        // including unselected siblings and unchanged points outside the selected region.
        if (_fieldEvaluator is not null)
        {
            foreach (var point in _points.Values.Where(point => !point.Masked))
            {
                var final = finals.TryGetValue(point.OffsetKey, out var changed) ? changed : _active[point.Key];
                try
                {
                    var result = _fieldEvaluator(point, final, Limits!);
                    if (!result.Ok) errors.Add($"{point.Key}: {result.Message}");
                }
                catch (Exception error) { errors.Add($"{point.Key}: 실제 Field 검사 실패: {error.Message}"); }
            }
        }
        var validation = new ST_MANUAL_VALIDATION(errors.Count == 0, ReadOnly(errors.Distinct().ToArray()), ReadOnly(warnings.ToArray()),
            maxDelta, maxTotal, allowManual, Fingerprint());
        Candidate = candidate with { State = validation.Ok ? EN_MANUAL_CANDIDATE_STATE.Validated : EN_MANUAL_CANDIDATE_STATE.Invalid, Validation = validation };
        _copyUndo = null; Log("VALIDATE", validation.Ok ? "후보 검증 통과" : $"후보 검증 차단 {validation.Errors.Count}건");
        return validation;
    }

    /// <summary>Reserve a validated snapshot. No state mutator is allowed until CommitApplied or AbortApply.</summary>
    public ST_MANUAL_APPLY_SNAPSHOT CreateApplySnapshot(bool allowManual)
    {
        GuardCorrection();
        if (Candidate is not { State: EN_MANUAL_CANDIDATE_STATE.Validated, Validation.Ok: true })
            throw new InvalidOperationException("먼저 현재 후보 검증을 통과한 뒤 적용·저장을 요청하세요.");
        var previousFingerprint = Candidate.Validation.Fingerprint;
        var validation = ValidateCandidate(allowManual);
        if (!validation.Ok) throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));
        if (previousFingerprint != validation.Fingerprint)
            throw new InvalidOperationException("검증 이후 작업 기준이 바뀌었습니다. 다시 검증하세요.");
        var candidate = Candidate!;
        var finals = candidate.Rows.GroupBy(row => row.OffsetKey).ToDictionary(group => group.Key, group => group.First().Final, StringComparer.Ordinal);
        var values = _points.Values.ToDictionary(point => point.Key,
            point => QuantizeForOffsetFile(finals.TryGetValue(point.OffsetKey, out var final) ? final : _active[point.Key]), StringComparer.Ordinal);
        ValidateStoredValues(values, candidate.Rows.Select(row => row.OffsetKey).ToHashSet(StringComparer.Ordinal), checkDelta: !(candidate.IsOffsetReplacement && candidate.InitializeAllReviewRows));
        var offsets = _points.Values.GroupBy(point => point.OffsetKey).ToDictionary(group => group.Key, group => values[group.First().Key], StringComparer.Ordinal);
        var consumed = candidate.Rows.Where(row => row.Source == "MEASURED" && row.MeasurementToken.Length > 0)
            .ToDictionary(row => row.Key, row => row.MeasurementToken, StringComparer.Ordinal);
        var changedShots = candidate.Rows.Select(row => row.OffsetKey).ToHashSet(StringComparer.Ordinal);
        foreach (var point in _points.Values.Where(point => changedShots.Contains(point.OffsetKey)))
        {
            // Freeze the companion raw identities before actual I/O. The digest covers this dictionary,
            // so the host can persist exactly these opaque tokens without recreating conversion rules.
            var raw = point.CorrectionMeasurement ?? point.OriginalMeasurement;
            if (raw is not null && raw.Judge is "OK" or "NG" && raw.ErrorXUm is double x && raw.ErrorYUm is double y
                && double.IsFinite(x) && double.IsFinite(y) && !string.IsNullOrWhiteSpace(raw.RawToken)
                && raw.RecipeId == Context!.RecipeId && raw.RunId == Context.RunId && raw.CoordinateSignature == Context.CoordinateSignature)
                consumed[point.Key] = MeasurementToken(point);
        }
        var snapshot = new ST_MANUAL_APPLY_SNAPSHOT(NewId("WRITE"), candidate.Id, Context!.RecipeId, Context.RunId, Context.RevisionId,
            Freeze(values), Freeze(offsets), ReadOnly(candidate.Rows.ToArray()))
        { ConsumedMeasurements = Freeze(consumed), IsOffsetReplacement=candidate.IsOffsetReplacement, InitializeAllReviewRows=candidate.InitializeAllReviewRows };
        _pendingWriteToken = snapshot.Token; _pendingApply = snapshot; _pendingWriteFingerprint = Digest(snapshot);
        Candidate = candidate with { State = EN_MANUAL_CANDIDATE_STATE.Applying };
        Log("APPLY_RESERVED", "오프셋 파일 저장 대기; 실제 저장 성공 전에는 활성값을 바꾸지 않습니다.");
        return snapshot;
    }

    public void CommitApplied(ST_MANUAL_APPLY_SNAPSHOT snapshot, string newRevisionId, string reason,
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET>? actualValuesByPointKey = null)
    {
        RequireWrite(snapshot.Token, Digest(snapshot), applying: true); RequireNewRevision(newRevisionId);
        var candidate = Candidate!; var oldRevision = Context!.RevisionId;
        var actual = VerifyWrittenValues(actualValuesByPointKey ?? snapshot.ValuesByPointKey, snapshot.ValuesByPointKey, snapshot.Rows,
            snapshot.IsOffsetReplacement && snapshot.InitializeAllReviewRows);
        _active.Clear(); foreach (var pair in actual) _active.Add(pair.Key, pair.Value);
        // Driving and companion tokens were reserved together. Commit never rebuilds the opaque set
        // after file I/O, and a failed write never updates this ledger.
        foreach (var pair in snapshot.ConsumedMeasurements) MarkConsumed(pair.Key, pair.Value);
        if (snapshot.IsOffsetReplacement)
        {
            foreach (var before in _points.Values.ToArray())
            {
                var after=before with { Judge="UNMEASURED",Source="KEPT",ErrorXUm=null,ErrorYUm=null,ProxySource=null,AdoptedFileMeasurement=null,ExternalObservedAt=null };
                _points[before.Key]=after;
                AddHistory(before,after,"OFFSET_REPLACED","통합 Offset 기준 변경 · 옛 잔차/대표값 재사용 해제 · 실제 원측정은 이력 보존");
            }
            _offsetEditPreviousCandidate=null;
        }
        _appliedCandidates.Add(candidate.Id); Context = Context with { RevisionId = newRevisionId };
        Candidate = candidate with
        {
            State = EN_MANUAL_CANDIDATE_STATE.Applied,
            Rows = ReadOnly(candidate.Rows.Select(row => row with { Final = actual[row.Key], Delta = Subtract(actual[row.Key], row.Base) }).ToArray())
        };
        _revisions.Add(new(newRevisionId, oldRevision, "MANUAL_INSPECTION", reason, DateTimeOffset.UtcNow, Freeze(_active)));
        ClearWrite(); _generation++; _copyUndo = null;
        Log("APPLY_COMMITTED", $"실제 저장 성공: {oldRevision} → {newRevisionId}");
    }

    public void AbortApply(ST_MANUAL_APPLY_SNAPSHOT snapshot)
    {
        RequireWrite(snapshot.Token, Digest(snapshot), applying: true);
        Candidate = Candidate! with { State = EN_MANUAL_CANDIDATE_STATE.Draft, Validation = null };
        ClearWrite(); _copyUndo = null; Log("APPLY_ABORT", "파일 저장 실패/취소; 활성 보정은 유지했습니다.");
    }

    public ST_MANUAL_RESTORE_SNAPSHOT CreateRestorePreviousSnapshot()
    {
        GuardIdle(); RequireLoaded(); GuardNoOffsetEdit();
        if (!Context!.LineageValid) throw new InvalidOperationException("현재 모델 기준을 다시 연결한 후 활성 보정을 원복하세요.");
        var current = _revisions.LastOrDefault(revision => revision.Id == Context!.RevisionId);
        var previous = current is null ? null : _revisions.LastOrDefault(revision => revision.Id == current.ParentId);
        if (previous is null) throw new InvalidOperationException("원복할 직전 활성 보정 기록이 없습니다.");
        var quantized = previous.ValuesByPointKey.ToDictionary(pair => pair.Key, pair => QuantizeForOffsetFile(pair.Value), StringComparer.Ordinal);
        ValidateStoredValues(quantized, _points.Values.Select(point => point.OffsetKey).ToHashSet(StringComparer.Ordinal), checkDelta: false);
        var snapshot = new ST_MANUAL_RESTORE_SNAPSHOT(NewId("RESTORE"), Context!.RecipeId, Context.RunId, Context.RevisionId,
            previous.Id, Freeze(quantized));
        _pendingWriteToken = snapshot.Token; _pendingRestore = snapshot; _pendingWriteFingerprint = Digest(snapshot);
        return snapshot;
    }

    public void CommitRestored(ST_MANUAL_RESTORE_SNAPSHOT snapshot, string newRevisionId, string reason,
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET>? actualValuesByPointKey = null)
    {
        RequireWrite(snapshot.Token, Digest(snapshot), applying: false); RequireNewRevision(newRevisionId);
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("원복 사유가 필요합니다.");
        var oldRevision = Context!.RevisionId;
        var actual = VerifyWrittenValues(actualValuesByPointKey ?? snapshot.ValuesByPointKey, snapshot.ValuesByPointKey, null);
        _active.Clear(); foreach (var pair in actual) _active.Add(pair.Key, pair.Value);
        Context = Context with { RevisionId = newRevisionId };
        _revisions.Add(new(newRevisionId, oldRevision, "RESTORE", reason.Trim(), DateTimeOffset.UtcNow, Freeze(_active)));
        Candidate = null; ClearWrite(); _generation++; _copyUndo = null;
        // The same-raw application ledger is intentionally retained. Rollback is not permission to reapply old holes.
        Log("RESTORE_COMMITTED", $"{snapshot.RestoreTargetRevisionId} 값을 새 Revision {newRevisionId}로 복원했습니다.");
    }

    public void AbortRestore(ST_MANUAL_RESTORE_SNAPSHOT snapshot)
    {
        RequireWrite(snapshot.Token, Digest(snapshot), applying: false); ClearWrite(); Log("RESTORE_ABORT", "원복 파일 저장 실패/취소; 활성값 유지");
    }

    public ST_MANUAL_OPERATION_PREVIEW PreviewOperation(EN_MANUAL_OPERATION_KIND kind, IEnumerable<string> keys, bool isRetry = false)
    {
        GuardIdle(); RequireLoaded(); GuardNoOffsetEdit();
        if (!Enum.IsDefined(kind)) throw new InvalidOperationException("지원하지 않는 작업 방식입니다.");
        var selected = Select(keys, excludeMasked: false);
        var valid = selected.Where(key => !GetPoint(key).Masked).ToArray();
        var excluded = selected.Where(key => GetPoint(key).Masked).ToArray();
        if (valid.Length == 0) throw new InvalidOperationException("실행할 위치가 없습니다. Masking은 제외됩니다.");
        if (HasIncompleteMeasurements)
        {
            if (!isRetry || kind != EN_MANUAL_OPERATION_KIND.MoveMeasure || !valid.SequenceEqual(Operation!.RemainingKeys))
                throw new InvalidOperationException("미완료 측정의 남은 점만 측정 재실행할 수 있습니다. 자동 보정은 재개하지 않습니다.");
        }
        else if (isRetry) throw new InvalidOperationException("재실행할 남은 측정이 없습니다.");
        return new(kind, ReadOnly(valid), ReadOnly(excluded), isRetry, OperationFingerprint(kind, valid, isRetry));
    }

    public ST_MANUAL_OPERATION BeginOperation(ST_MANUAL_OPERATION_PREVIEW preview)
    {
        GuardIdle(); RequireLoaded();
        var verified = PreviewOperation(preview.Kind, preview.Keys.Concat(preview.ExcludedMaskedKeys), preview.IsRetry);
        if (verified.Fingerprint != preview.Fingerprint || !verified.Keys.SequenceEqual(preview.Keys))
            throw new InvalidOperationException("실행 미리보기 뒤 대상·설정·보정 기준이 바뀌었습니다.");
        var versions = preview.Keys.ToDictionary(key => key, key => GetPoint(key).MeasurementVersion, StringComparer.Ordinal);
        Operation = new(NewId("OP"), preview.Kind, EN_MANUAL_OPERATION_STATE.Running, ReadOnly(preview.Keys.ToArray()),
            ReadOnly(Array.Empty<string>()), ReadOnly(preview.Keys.ToArray()), Context!.RecipeId, Context.RunId,
            Context.RevisionId, Context.CoordinateSignature, Freeze(versions), preview.IsRetry, "");
        _generation++; InvalidateCandidate(); _copyUndo = null;
        Log("OP_START", $"{preview.Kind}: {preview.Keys.Count}점" + (preview.IsRetry ? "; 잔여 측정만" : ""));
        return Operation;
    }

    public void MarkOperationPointCompleted(string operationId, string key)
    {
        RequireOperation(operationId); var operation = Operation!;
        if (!operation.Keys.Contains(key, StringComparer.Ordinal)) throw new InvalidOperationException("현재 작업에 없는 위치입니다.");
        if (operation.CompletedKeys.Contains(key, StringComparer.Ordinal)) throw new InvalidOperationException("이미 완료 처리한 위치입니다.");
        if (operation.Kind != EN_MANUAL_OPERATION_KIND.Move && GetPoint(key).MeasurementVersion <= operation.MeasurementVersionsBefore[key])
            throw new InvalidOperationException("이번 작업의 새 측정 응답이 없는 위치를 완료 처리할 수 없습니다.");
        if (operation.Kind != EN_MANUAL_OPERATION_KIND.Move)
        {
            var point = GetPoint(key);
            if (!point.IsDirectMeasurement || point.Judge is not ("OK" or "NG") || !point.HasFiniteError)
                throw new InvalidOperationException("수신 오류/미측정은 완료가 아닙니다. 해당 위치를 남겨 측정 재실행하세요.");
            RequireMeasurementLineage(point);
        }
        var completed = operation.CompletedKeys.Append(key).ToArray();
        Operation = operation with { CompletedKeys = ReadOnly(completed), RemainingKeys = ReadOnly(operation.Keys.Except(completed, StringComparer.Ordinal).ToArray()) };
    }

    public ST_MANUAL_OPERATION_COMPLETION CompleteOperation(string operationId, EN_MANUAL_OPERATION_STATE state = EN_MANUAL_OPERATION_STATE.Completed, string error = "")
    {
        RequireOperation(operationId);
        if (state == EN_MANUAL_OPERATION_STATE.Running) throw new InvalidOperationException("종료 상태를 지정하세요.");
        var operation = Operation!; var errors = new List<string>();
        if (state == EN_MANUAL_OPERATION_STATE.Completed && operation.RemainingKeys.Count > 0)
        { state = EN_MANUAL_OPERATION_STATE.Error; errors.Add("일부 위치가 끝나지 않아 전체 완료로 처리하지 않았습니다."); }
        var auto = state == EN_MANUAL_OPERATION_STATE.Completed && operation.Kind == EN_MANUAL_OPERATION_KIND.MeasureAndCorrect && !operation.IsRetry;
        if (auto)
        {
            if (operation.RecipeId != Context!.RecipeId || operation.RunId != Context.RunId || operation.BaseRevisionId != Context.RevisionId || operation.CoordinateSignature != Context.CoordinateSignature)
                errors.Add("작업 시작 후 Recipe/Run/Revision/설정 기준이 바뀌었습니다.");
            foreach (var key in operation.Keys)
            {
                var point = GetPoint(key);
                if (!point.IsDirectMeasurement || point.Judge != "OK" || !point.HasFiniteError || point.MeasurementVersion <= operation.MeasurementVersionsBefore[key])
                    errors.Add($"{key}: 이번 작업의 새 직접 측정 OK가 아닙니다.");
                try { RequireMeasurementLineage(point); } catch (InvalidOperationException invalid) { errors.Add(invalid.Message); }
            }
            if (errors.Count > 0) { auto = false; state = EN_MANUAL_OPERATION_STATE.CorrectionBlocked; }
        }
        if (!string.IsNullOrWhiteSpace(error)) errors.Add(error);
        Operation = operation with { State = state, Error = string.Join(" / ", errors) };
        _generation++; InvalidateCandidate(); _copyUndo = null;
        Log("OP_END", $"{state}: {operation.CompletedKeys.Count}/{operation.Keys.Count}; 자동보정 허용={auto}");
        return new(Operation, auto, ReadOnly(errors.ToArray()));
    }

    /// <summary>Statistics are independent of map filters. The host chooses Cell/Head/selected Cells before this call.</summary>
    public static ST_MANUAL_SCATTER ComputeScatter(IEnumerable<ST_MANUAL_POINT> source, bool useOriginal = false)
    {
        var points = source.ToArray(); var valid = new List<(string Key, double X, double Y)>();
        var masked = 0; var missing = 0; var errors = 0; var invalid = 0; var proxy = 0;
        foreach (var point in points)
        {
            if (point.Masked) { masked++; continue; }
            var measurement = useOriginal ? point.OriginalMeasurement : null;
            var missingOriginalJudge = point.ProxySource is null && point.Judge is "ERROR" or "UNMEASURED" ? point.Judge : "UNKNOWN";
            var judge = useOriginal ? measurement?.Judge ?? missingOriginalJudge : point.Judge;
            var x = useOriginal ? measurement?.ErrorXUm : point.ErrorXUm;
            var y = useOriginal ? measurement?.ErrorYUm : point.ErrorYUm;
            if (judge == "UNMEASURED") { missing++; continue; }
            if (judge == "ERROR") { errors++; continue; }
            if (judge is not ("OK" or "NG") || x is not double ex || y is not double ey || !double.IsFinite(ex) || !double.IsFinite(ey))
            { invalid++; continue; }
            if (!useOriginal && point.ProxySource is not null) proxy++;
            valid.Add((point.Key, ex, ey));
        }
        double? meanX = valid.Count == 0 ? null : valid.Average(value => value.X);
        double? meanY = valid.Count == 0 ? null : valid.Average(value => value.Y);
        double? sigmaX = valid.Count == 0 ? null : Math.Sqrt(valid.Average(value => Math.Pow(value.X - meanX!.Value, 2)));
        double? sigmaY = valid.Count == 0 ? null : Math.Sqrt(valid.Average(value => Math.Pow(value.Y - meanY!.Value, 2)));
        double? max = valid.Count == 0 ? null : valid.Max(value => Math.Sqrt(value.X * value.X + value.Y * value.Y));
        return new(points.Length, valid.Count, proxy, masked, missing, errors, invalid, meanX, meanY, sigmaX, sigmaY, max, ReadOnly(valid.Select(value => value.Key).ToArray()));
    }

    public ST_MANUAL_SESSION CaptureSession()
    {
        RequireLoaded();
        return new(1, Context!, Limits!, Points, Freeze(_active), ReadOnly(_revisions.ToArray()),
            Freeze(_appliedMeasurements.Where(pair => _points.ContainsKey(pair.Key))), ReadOnly(_appliedCandidates.OrderBy(id => id, StringComparer.Ordinal).ToArray()),
            Candidate, Operation, ReadOnly(_audit.ToArray()))
        {
            History = ReadOnly(_history.ToArray()), CandidateBackups = ReadOnly(_candidateBackups.ToArray()), OffsetEditPreviousCandidate=_offsetEditPreviousCandidate,
            ConsumedMeasurementHistory = ReadOnly(_consumedMeasurementHistory
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(pair => pair.Value.OrderBy(token => token, StringComparer.Ordinal).Select(token => new ST_MANUAL_CONSUMED_MEASUREMENT(pair.Key, token)))
                .ToArray())
        };
    }

    /// <summary>Read-only opaque lookup; all consumed tokens are retained, not only the last token of a point.</summary>
    public bool WasConsumed(string key, string token) => _consumedMeasurementHistory.TryGetValue(key, out var tokens) && tokens.Contains(token)
        || _appliedMeasurements.TryGetValue(key, out var latest) && latest == token;

    /// <summary>
    /// Seed committed durable receipts for the current Recipe/Run. The host filters receipt scope before
    /// calling this method and passes opaque keys/tokens without rebuilding the measurement hash.
    /// Unknown display keys may be historical Beam siblings; they cannot create a point or authorize motion.
    /// </summary>
    public void SeedConsumedMeasurements(IEnumerable<KeyValuePair<string, string>> measurements)
    {
        GuardIdle(); RequireLoaded();
        var input = measurements.ToArray();
        if (input.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            throw new InvalidOperationException("저장된 측정 소비 이력의 Key/Token이 비어 있습니다.");
        var added = false;
        foreach (var pair in input)
        {
            if (!_consumedMeasurementHistory.TryGetValue(pair.Key, out var tokens)) _consumedMeasurementHistory.Add(pair.Key, tokens = new(StringComparer.Ordinal));
            added |= tokens.Add(pair.Value);
        }
        if (added) { Changed(); Log("CONSUMED_SEED", "이미 저장한 원측정 전체 이력을 복구했습니다. 기존 후보는 다시 검증해야 합니다."); }
    }

    public string ExportSessionJson(bool writeIndented = true) => JsonSerializer.Serialize(CaptureSession(), new JsonSerializerOptions { WriteIndented = writeIndented });

    /// <summary>
    /// Reconcile a local session against a freshly obtained production Recipe plan and actual offset
    /// revision. Do not pass session-supplied geometry/base values back as the expected arguments.
    /// A saved RUNNING operation becomes STOPPED and can only retry remaining measurements.
    /// </summary>
    public void RestoreSessionJson(
        string json, ST_MANUAL_CONTEXT expectedContext, IEnumerable<ST_MANUAL_POINT> expectedPoints,
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> actualBaseValuesByPointKey,
        ManualInspectionFieldEvaluator? fieldEvaluator)
    {
        var session = JsonSerializer.Deserialize<ST_MANUAL_SESSION>(json)
            ?? throw new InvalidOperationException("빈 작업공간 세션입니다.");
        RestoreSession(session, expectedContext, expectedPoints, actualBaseValuesByPointKey, fieldEvaluator);
    }

    public void RestoreSession(
        ST_MANUAL_SESSION session, ST_MANUAL_CONTEXT expectedContext, IEnumerable<ST_MANUAL_POINT> expectedPoints,
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> actualBaseValuesByPointKey,
        ManualInspectionFieldEvaluator? fieldEvaluator)
    {
        GuardIdle(); ValidateContext(expectedContext);
        if (Context?.RecipeId == expectedContext.RecipeId && Context.RunId == expectedContext.RunId && HasIncompleteMeasurements)
            throw new InvalidOperationException("같은 Run의 미완료 측정을 이전 세션으로 지울 수 없습니다. 잔여 측정을 마치거나 새 Run을 여세요.");
        if (session.SchemaVersion != 1 || session.Context is null || session.Limits is null || session.Points is null
            || session.ActiveValuesByPointKey is null || session.Revisions is null || session.AppliedMeasurements is null
            || session.AppliedCandidates is null || session.Audit is null)
            throw new InvalidOperationException("지원하지 않거나 필수 항목이 없는 세션입니다.");
        if (session.Points.Any(point => point is null) || session.Revisions.Any(revision => revision is null)
            || session.Candidate is not null && (session.Candidate.Rows is null || session.Candidate.Rows.Any(row => row is null)))
            throw new InvalidOperationException("세션 위치/Revision/후보 행에 누락 항목이 있습니다.");
        if (session.Context.RecipeId != expectedContext.RecipeId || session.Context.RunId != expectedContext.RunId
            || session.Context.RevisionId != expectedContext.RevisionId || session.Context.CoordinateSignature != expectedContext.CoordinateSignature)
            throw new InvalidOperationException("세션과 실제 Recipe/Run/Revision/좌표 설정이 다릅니다. 실제 기준으로 다시 로드하세요.");
        var geometry = expectedPoints.ToDictionary(point => point.Key, StringComparer.Ordinal);
        if (geometry.Count != session.Points.Count || session.Points.Select(point => point.Key).Distinct(StringComparer.Ordinal).Count() != geometry.Count)
            throw new InvalidOperationException("세션과 실제 Recipe의 위치 집합이 다릅니다.");
        var lineage = expectedContext.LineageValid && session.Context.LineageValid;
        foreach (var point in session.Points)
        {
            if (!geometry.TryGetValue(point.Key, out var actual) || point.CellNo != actual.CellNo || point.HeadNo != actual.HeadNo
                || point.ShotNo != actual.ShotNo || point.BeamNo != actual.BeamNo || point.Row != actual.Row || point.Column != actual.Column
                || point.Masked != actual.Masked || Math.Abs(point.XMm - actual.XMm) > 1e-9 || Math.Abs(point.YMm - actual.YMm) > 1e-9)
                throw new InvalidOperationException($"{point.Key}: 세션 좌표/Masking이 실제 Recipe와 다릅니다.");
            if (!actualBaseValuesByPointKey.TryGetValue(point.Key, out var actualBase)
                || !session.ActiveValuesByPointKey.TryGetValue(point.Key, out var storedBase) || !Equal(actualBase, storedBase))
                throw new InvalidOperationException($"{point.Key}: 세션의 현재 B가 실제 활성 보정 파일과 다릅니다.");
            if (point.ProxySource is { } proxy)
            {
                if ((point.OriginalMeasurement is null && point.AdoptedFileMeasurement is null) || point.Source != "MANUAL" || point.Judge is not ("OK" or "NG")
                    || proxy.ReferenceKey == point.Key || !geometry.ContainsKey(proxy.ReferenceKey) || proxy.RunId != expectedContext.RunId
                    || proxy.CoordinateSignature != expectedContext.CoordinateSignature || string.IsNullOrWhiteSpace(proxy.ReferenceMeasurementToken)
                    || proxy.ReferenceJudge != point.Judge || !proxy.FinalOffset.IsFinite || string.IsNullOrWhiteSpace(proxy.Reason)
                    || proxy.ErrorXUm != point.ErrorXUm || proxy.ErrorYUm != point.ErrorYUm)
                    lineage = false;
            }
            // A labelled IMPORTED point is retained for viewing/reinspection and is not an eligible
            // direct anchor. Its historical frame must not invalidate unrelated new native measurements.
            if (point.OriginalMeasurement is { } measurement && point.Source != "IMPORTED" && point.ProxySource?.OriginalSource != "IMPORTED")
            {
                try { ValidateMeasurement(measurement, expectedContext); } catch (InvalidOperationException) { lineage = false; }
            }
            if (point.AdoptedFileMeasurement is { } adopted)
                try { ValidateMeasurement(adopted,expectedContext); } catch (InvalidOperationException) { lineage=false; }
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var previousRevisionId = "";
        foreach (var revision in session.Revisions)
        {
            if (string.IsNullOrWhiteSpace(revision.Id) || !ids.Add(revision.Id) || revision.ValuesByPointKey is null
                || revision.ValuesByPointKey.Count != geometry.Count || revision.ValuesByPointKey.Any(pair => !geometry.ContainsKey(pair.Key) || !pair.Value.IsFinite))
                throw new InvalidOperationException("세션의 보정 Revision 기록이 잘못됐습니다.");
            if (revision.ParentId != previousRevisionId)
                throw new InvalidOperationException("세션 Revision 부모가 없거나 순서/계보가 잘못됐습니다.");
            previousRevisionId = revision.Id;
            foreach (var group in geometry.Values.GroupBy(point => point.OffsetKey))
            {
                var first = revision.ValuesByPointKey[group.First().Key];
                if (group.Any(point => !Equal(first, revision.ValuesByPointKey[point.Key])))
                    throw new InvalidOperationException("세션 Revision의 같은 Cell/Shot 보정값이 서로 다릅니다.");
            }
        }
        var active = session.Revisions.LastOrDefault(revision => revision.Id == expectedContext.RevisionId);
        if (active is null || previousRevisionId != expectedContext.RevisionId || active.ValuesByPointKey.Any(pair => !Equal(pair.Value, actualBaseValuesByPointKey[pair.Key])))
            throw new InvalidOperationException("세션 활성 Revision 이력이 실제 파일과 다릅니다.");
        var temporary = new CManualInspectionWorkspace();
        temporary.Load(expectedContext with { LineageValid = lineage }, session.Points, actualBaseValuesByPointKey, session.Limits, fieldEvaluator);
        if (session.Operation is { } operation)
        {
            if (operation.Keys is null || operation.CompletedKeys is null || operation.RemainingKeys is null || operation.MeasurementVersionsBefore is null
                || !Enum.IsDefined(operation.Kind) || !Enum.IsDefined(operation.State))
                throw new InvalidOperationException("세션 작업 상태 또는 필수 범위가 잘못됐습니다.");
            var keySet = new HashSet<string>(operation.Keys, StringComparer.Ordinal);
            var doneSet = new HashSet<string>(operation.CompletedKeys, StringComparer.Ordinal);
            if (operation.RecipeId != expectedContext.RecipeId || operation.RunId != expectedContext.RunId
                || !ids.Contains(operation.BaseRevisionId)
                || operation.State == EN_MANUAL_OPERATION_STATE.Running && operation.BaseRevisionId != expectedContext.RevisionId
                || operation.RemainingKeys.Count > 0 && operation.CoordinateSignature != expectedContext.CoordinateSignature
                || operation.Keys.Any(key => !geometry.ContainsKey(key) || geometry[key].Masked) || keySet.Count != operation.Keys.Count
                || !doneSet.IsSubsetOf(keySet) || doneSet.Count != operation.CompletedKeys.Count
                || !operation.RemainingKeys.SequenceEqual(operation.Keys.Where(key => !doneSet.Contains(key))))
                throw new InvalidOperationException("세션의 미완료 측정 범위가 잘못됐습니다.");
            temporary.Operation = operation with
            {
                State = operation.State == EN_MANUAL_OPERATION_STATE.Running ? EN_MANUAL_OPERATION_STATE.Stopped : operation.State,
                Error = operation.State == EN_MANUAL_OPERATION_STATE.Running ? "세션 복구: 자동 실행/자동 보정을 재개하지 않습니다." : operation.Error,
                Keys = ReadOnly(operation.Keys.ToArray()), CompletedKeys = ReadOnly(operation.CompletedKeys.ToArray()),
                RemainingKeys = ReadOnly(operation.RemainingKeys.ToArray()), MeasurementVersionsBefore = Freeze(operation.MeasurementVersionsBefore)
            };
        }
        // Restore valid source data, but never resurrect a previous validation approval or pending write.
        temporary.Candidate = session.Candidate is null ? null : session.Candidate with
        {
            State = session.Candidate.State == EN_MANUAL_CANDIDATE_STATE.Applied ? EN_MANUAL_CANDIDATE_STATE.Applied : EN_MANUAL_CANDIDATE_STATE.Draft,
            Validation = null, Rows = ReadOnly(session.Candidate.Rows.ToArray())
        };
        if (session.OffsetEditPreviousCandidate is { } previousEdit)
        {
            if (!temporary.HasPendingOffsetEdit || previousEdit.RecipeId!=expectedContext.RecipeId || previousEdit.RunId!=expectedContext.RunId
                || previousEdit.State!=EN_MANUAL_CANDIDATE_STATE.Applied && previousEdit.BaseRevisionId!=expectedContext.RevisionId || previousEdit.Rows is null
                || previousEdit.Rows.Any(row=>row is null || !geometry.ContainsKey(row.Key)))
                throw new InvalidOperationException("통합 Offset 편집의 이전 후보 기준이 세션과 다릅니다.");
            temporary._offsetEditPreviousCandidate=previousEdit with { Rows=ReadOnly(previousEdit.Rows.ToArray()),Validation=null,
                State=previousEdit.State==EN_MANUAL_CANDIDATE_STATE.Applied?EN_MANUAL_CANDIDATE_STATE.Applied:EN_MANUAL_CANDIDATE_STATE.Draft };
        }
        temporary._revisions.Clear();
        temporary._revisions.AddRange(session.Revisions.Select(revision => revision with { ValuesByPointKey = Freeze(revision.ValuesByPointKey) }));
        foreach (var pair in session.AppliedMeasurements)
        {
            if (!geometry.ContainsKey(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) throw new InvalidOperationException("세션의 측정 적용 이력이 잘못됐습니다.");
            temporary.MarkConsumed(pair.Key, pair.Value);
        }
        if (session.ConsumedMeasurementHistory is null || session.ConsumedMeasurementHistory.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Token)))
            throw new InvalidOperationException("세션의 전체 측정 소비 이력이 잘못됐습니다.");
        temporary.SeedConsumedMeasurements(session.ConsumedMeasurementHistory.Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Token)));
        foreach (var id in session.AppliedCandidates) temporary._appliedCandidates.Add(id);
        temporary._audit.Clear(); temporary._audit.AddRange(session.Audit);
        if (session.History is null || session.CandidateBackups is null) throw new InvalidOperationException("세션 이력 배열이 잘못됐습니다.");
        foreach (var history in session.History)
        {
            if (history is null || !geometry.ContainsKey(history.Key)) throw new InvalidOperationException("세션 이력의 위치가 현재 Recipe에 없습니다.");
            temporary._history.Add(history);
        }
        foreach (var backup in session.CandidateBackups)
        {
            if (backup is null || backup.Rows is null) throw new InvalidOperationException("세션 후보 보관본이 잘못됐습니다.");
            temporary._candidateBackups.Add(backup with { Rows = ReadOnly(backup.Rows.ToArray()) });
        }
        if (Context?.RecipeId == expectedContext.RecipeId && Context.RunId == expectedContext.RunId)
        {
            // NativeLoad may already have seeded committed durable receipts. An older session is not
            // allowed to erase those facts, even when it has the same actual active offset revision.
            temporary.SeedConsumedMeasurements(_consumedMeasurementHistory.SelectMany(pair => pair.Value.Select(token => new KeyValuePair<string, string>(pair.Key, token))));
            foreach (var id in _appliedCandidates) temporary._appliedCandidates.Add(id);
        }
        _points.Clear(); foreach (var pair in temporary._points) _points.Add(pair.Key, pair.Value);
        _active.Clear(); foreach (var pair in temporary._active) _active.Add(pair.Key, pair.Value);
        _appliedMeasurements.Clear(); foreach (var pair in temporary._appliedMeasurements) _appliedMeasurements.Add(pair.Key, pair.Value);
        _consumedMeasurementHistory.Clear();
        foreach (var pair in temporary._consumedMeasurementHistory) _consumedMeasurementHistory.Add(pair.Key, new(pair.Value, StringComparer.Ordinal));
        _appliedCandidates.Clear(); foreach (var id in temporary._appliedCandidates) _appliedCandidates.Add(id);
        _revisions.Clear(); _revisions.AddRange(temporary._revisions);
        _audit.Clear(); _audit.AddRange(temporary._audit);
        _history.Clear(); _history.AddRange(temporary._history);
        _candidateBackups.Clear(); _candidateBackups.AddRange(temporary._candidateBackups);
        Context = temporary.Context; Limits = temporary.Limits; Candidate = temporary.Candidate; Operation = temporary.Operation;
        _offsetEditPreviousCandidate=temporary._offsetEditPreviousCandidate;
        _fieldEvaluator = fieldEvaluator; _copyUndo = null; ClearWrite(); _generation++;
        Log("SESSION_RESTORE", lineage ? "세션 복구: 후보 재검증 필요, 자동 실행 없음" : "세션 원측정/대표 출처 불완전: 조회 전용");
    }

    private ST_MANUAL_CANDIDATE NewCandidate(IEnumerable<ST_MANUAL_CANDIDATE_ROW> rows) => new(NewId("CAND"), Context!.RecipeId,
        Context.RunId, Context.RevisionId, SourceFingerprint(), ReadOnly(rows.OrderBy(row => row.Key, StringComparer.Ordinal).ToArray()), EN_MANUAL_CANDIDATE_STATE.Draft, null);
    private void Changed() { _generation++; InvalidateCandidate(); _copyUndo = null; }

    /// <summary>
    /// Match CReviewOffsetFile's mm.ToString("0.000000", InvariantCulture) byte-level numeric format.
    /// This is 0.001 µm file precision and is not a claim about the later planner's 1 µm rounding or motion resolution.
    /// </summary>
    public static ST_MANUAL_OFFSET QuantizeForOffsetFile(ST_MANUAL_OFFSET value)
    {
        if (!value.IsFinite) throw new InvalidOperationException("저장할 보정값이 유한하지 않습니다.");
        static double Axis(double um) => double.Parse((um / 1000d).ToString("0.000000", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) * 1000d;
        return new(Axis(value.XUm), Axis(value.YUm));
    }

    private void ValidateStoredValues(IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values, IReadOnlySet<string> changedOffsets, bool checkDelta)
    {
        if (_fieldEvaluator is null) throw new InvalidOperationException("실제 Scanner Field 검증기가 없어 보정 파일을 저장할 수 없습니다.");
        foreach (var point in _points.Values)
        {
            var value = values[point.Key];
            if (!value.IsFinite) throw new InvalidOperationException($"{point.Key}: 양자화한 저장값이 유효하지 않습니다.");
            if (changedOffsets.Contains(point.OffsetKey))
            {
                if (Magnitude(value) > Limits!.MaxTotalUm) throw new InvalidOperationException($"{point.Key}: 실제 저장 반올림 후 총량 한계를 넘습니다.");
                if (checkDelta && Magnitude(Subtract(value, _active[point.Key])) > Limits!.MaxDeltaUm)
                    throw new InvalidOperationException($"{point.Key}: 실제 저장 반올림 후 증감 한계를 넘습니다.");
            }
            if (!point.Masked)
            {
                var field = _fieldEvaluator(point, value, Limits!);
                if (!field.Ok) throw new InvalidOperationException($"{point.Key}: 실제 저장 반올림 후 Field 검사: {field.Message}");
            }
        }
    }

    private IReadOnlyDictionary<string, ST_MANUAL_OFFSET> VerifyWrittenValues(
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> actual,
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> snapshotValues,
        IReadOnlyList<ST_MANUAL_CANDIDATE_ROW>? requestedRows, bool initialization = false)
    {
        if (actual.Count != _points.Count || actual.Keys.Any(key => !_points.ContainsKey(key)))
            throw new InvalidOperationException("실제 저장된 Offset의 위치 집합이 예약 스냅샷과 다릅니다.");
        var requestedByOffset = requestedRows?.GroupBy(row => row.OffsetKey).ToDictionary(group => group.Key, group => group.First().Final, StringComparer.Ordinal);
        var byOffset = new Dictionary<string, ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        foreach (var point in _points.Values)
        {
            var value = actual[point.Key];
            var requested = requestedByOffset is not null && requestedByOffset.TryGetValue(point.OffsetKey, out var original) ? original : snapshotValues[point.Key];
            if (!value.IsFinite || Magnitude(Subtract(value, requested)) > 0.000500001)
                throw new InvalidOperationException($"{point.Key}: 실제 저장값이 요청값의 0.0005 µm 반올림 범위를 벗어났습니다.");
            if (byOffset.TryGetValue(point.OffsetKey, out var other) && !Equal(value, other))
                throw new InvalidOperationException($"{point.OffsetKey}: 실제 저장된 같은 Shot의 Beam별 값이 다릅니다.");
            byOffset[point.OffsetKey] = value;
        }
        ValidateStoredValues(actual, requestedByOffset?.Keys.ToHashSet(StringComparer.Ordinal) ?? byOffset.Keys.ToHashSet(StringComparer.Ordinal), checkDelta: requestedRows is not null && !initialization);
        return Freeze(actual);
    }
    private void InvalidateCandidate()
    { if (Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied }) Candidate = Candidate with { State = EN_MANUAL_CANDIDATE_STATE.Draft, Validation = null }; }
    private void GuardCorrection()
    {
        GuardIdle(); RequireLoaded();
        if (HasIncompleteMeasurements) throw new InvalidOperationException("미완료 측정의 남은 점을 먼저 완료하거나 새 Run을 여세요.");
        if (!Context!.LineageValid) throw new InvalidOperationException("현재 모델 기준을 다시 연결하고 새 측정 또는 명시한 통합 Offset으로 보정하세요.");
    }
    private void GuardIdle() { if (IsBusy) throw new InvalidOperationException("동작 또는 보정 파일 저장 중입니다. 완료/중지 후 조작하세요."); }
    private void GuardNoWrite() { if (_pendingWriteToken is not null) throw new InvalidOperationException("보정 파일 저장 중에는 측정 상태를 바꿀 수 없습니다."); }
    private void GuardNoOffsetEdit() { if (HasPendingOffsetEdit) throw new InvalidOperationException("통합 Offset 편집을 먼저 모델에 저장하거나 취소한 후 이동·측정·복사하세요."); }
    private void RequireLoaded() { if (Context is null || Limits is null) throw new InvalidOperationException("먼저 실제 Recipe/Run 작업공간을 로드하세요."); }
    private void RequireOperation(string id)
    { if (Operation?.Id != id || Operation.State != EN_MANUAL_OPERATION_STATE.Running) throw new InvalidOperationException("현재 실행 중인 작업이 아니거나 늦은 완료 응답입니다."); }
    private void RequireWrite(string token, string fingerprint, bool applying)
    {
        if (_pendingWriteToken != token || _pendingWriteFingerprint != fingerprint || (applying ? _pendingApply is null : _pendingRestore is null))
            throw new InvalidOperationException("예약한 실제 저장 스냅샷과 다르거나 이미 종료된 저장입니다.");
    }
    private void RequireNewRevision(string id)
    { if (string.IsNullOrWhiteSpace(id) || _revisions.Any(revision => revision.Id == id)) throw new InvalidOperationException("실제 저장 성공의 새 고유 Revision이 필요합니다."); }
    private void ClearWrite() { _pendingWriteToken = null; _pendingWriteFingerprint = null; _pendingApply = null; _pendingRestore = null; }
    private void GuardCandidateBase(ST_MANUAL_CANDIDATE candidate)
    {
        if (candidate.RecipeId != Context!.RecipeId || candidate.RunId != Context.RunId || candidate.BaseRevisionId != Context.RevisionId)
            throw new InvalidOperationException("후보의 Recipe/Run/활성 보정 기준이 바뀌었습니다.");
    }
    private void RequireMeasurementLineage(ST_MANUAL_POINT point)
    {
        if (point.CorrectionMeasurement is not { } measurement) throw new InvalidOperationException($"{point.Key}: 직접 측정 또는 확인한 파일 측정값이 없습니다.");
        ValidateMeasurement(measurement, Context!);
        if (point.IsDirectMeasurement && (point.Judge != measurement.Judge
            || point.ErrorXUm != measurement.ErrorXUm || point.ErrorYUm != measurement.ErrorYUm))
            throw new InvalidOperationException($"{point.Key}: 직접 측정의 작업판정/편차가 보존한 원측정과 다릅니다.");
        if (measurement.Judge is not ("OK" or "NG")) throw new InvalidOperationException($"{point.Key}: 원측정 상태가 OK/NG가 아닙니다.");
    }
    private string MeasurementToken(ST_MANUAL_POINT point) => point.CorrectionMeasurement is { } measurement
        // RawData preserves the original response, which can contain a fresh request UID or timestamp.
        // The host's stable semantic RawToken deliberately excludes those transport-only changes.
        ? Digest(new { Context!.RunId, point.Key, measurement.RawToken, measurement.CoordinateSignature }) : "";
    private string SourceFingerprint() => Digest(new
    {
        Context, Limits,
        Points = _points.Values.OrderBy(point => point.Key, StringComparer.Ordinal).ToArray(),
        Active = _active.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()
    });
    private string Fingerprint() => Digest(new
    {
        Generation = _generation, Source = SourceFingerprint(),
        Candidate = Candidate is null ? null : new { Candidate.Id, Candidate.SourceFingerprint, Candidate.BaseRevisionId, Candidate.Rows }
    });
    private string CopyFingerprint(string referenceKey, IEnumerable<string> selection) => Digest(new
    { State = Fingerprint(), ReferenceKey = referenceKey, Selection = selection.OrderBy(key => key, StringComparer.Ordinal).ToArray() });
    private string OperationFingerprint(EN_MANUAL_OPERATION_KIND kind, IEnumerable<string> keys, bool retry) => Digest(new
    { State = Fingerprint(), Kind = kind, Keys = keys.ToArray(), IsRetry = retry, Incomplete = Operation });
    private string[] Select(IEnumerable<string> keys, bool excludeMasked)
    {
        var selected = keys.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var key in selected) _ = GetPoint(key);
        return excludeMasked ? selected.Where(key => !GetPoint(key).Masked).ToArray() : selected;
    }
    private static void ValidateContext(ST_MANUAL_CONTEXT context)
    {
        if (string.IsNullOrWhiteSpace(context.RecipeId) || string.IsNullOrWhiteSpace(context.RunId)
            || string.IsNullOrWhiteSpace(context.RevisionId) || string.IsNullOrWhiteSpace(context.CoordinateSignature))
            throw new InvalidOperationException("Recipe/Run/Revision/좌표 기준을 모두 명시해야 합니다.");
    }
    private static void ValidateLimits(ST_MANUAL_LIMITS limits)
    {
        if (!new[] { limits.MaxDeltaUm, limits.MaxTotalUm, limits.FieldWidthMm }.All(value => double.IsFinite(value) && value > 0))
            throw new InvalidOperationException("증감/누적/Field 한계는 0보다 큰 유한한 값이어야 합니다.");
    }
    private static void ValidatePoint(ST_MANUAL_POINT point)
    {
        var headValid = point.HeadNo is >= 1 and <= 8 || point.Masked && point.HeadNo == 0;
        if (string.IsNullOrWhiteSpace(point.Key) || point.CellNo < 1 || !headValid || point.ShotNo < 1 || point.BeamNo is < 1 or > 16
            || point.Row < 0 || point.Column < 0 || !double.IsFinite(point.XMm) || !double.IsFinite(point.YMm))
            throw new InvalidOperationException("측정 위치 식별자/단위 좌표가 잘못됐습니다.");
        if (!KnownJudge(point.Judge)) throw new InvalidOperationException($"{point.Key}: 알 수 없는 판정을 OK로 바꿀 수 없습니다.");
        if (point.ErrorXUm is double x && !double.IsFinite(x) || point.ErrorYUm is double y && !double.IsFinite(y))
            throw new InvalidOperationException($"{point.Key}: 편차의 NaN/무한대는 측정값으로 저장할 수 없습니다.");
        if (point.Judge is "OK" or "NG" && !point.HasFiniteError) throw new InvalidOperationException($"{point.Key}: OK/NG의 편차가 유효하지 않습니다.");
    }
    private static void ValidateMeasurement(ST_MANUAL_MEASUREMENT measurement, ST_MANUAL_CONTEXT context)
    {
        if (!KnownJudge(measurement.Judge)) throw new InvalidOperationException("알 수 없는 측정 판정입니다.");
        if (new[] { measurement.ErrorXUm, measurement.ErrorYUm, measurement.ScannerErrorXUm, measurement.ScannerErrorYUm }
            .Any(value => value.HasValue && !double.IsFinite(value.Value)))
            throw new InvalidOperationException("측정 편차의 NaN/무한대는 유효한 수치로 저장할 수 없습니다.");
        if (measurement.RecipeId != context.RecipeId || measurement.RunId != context.RunId || measurement.CoordinateSignature != context.CoordinateSignature)
            throw new InvalidOperationException("직접 원측정의 Recipe/Run/좌표 설정 출처가 현재와 다릅니다.");
        if (measurement.Judge is "OK" or "NG")
        {
            if (measurement.ErrorXUm is not double x || measurement.ErrorYUm is not double y || !double.IsFinite(x) || !double.IsFinite(y)
                || string.IsNullOrWhiteSpace(measurement.RawToken)) throw new InvalidOperationException("직접 OK/NG 측정은 유효한 편차와 안정적인 원시 패킷 토큰이 필요합니다.");
        }
    }
    private static bool KnownJudge(string judge) => judge is "OK" or "NG" or "ERROR" or "UNMEASURED";
    private static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");
    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static ST_MANUAL_OFFSET Add(ST_MANUAL_OFFSET a, ST_MANUAL_OFFSET b) => new(a.XUm + b.XUm, a.YUm + b.YUm);
    private static ST_MANUAL_OFFSET Subtract(ST_MANUAL_OFFSET a, ST_MANUAL_OFFSET b) => new(a.XUm - b.XUm, a.YUm - b.YUm);
    private static double Magnitude(ST_MANUAL_OFFSET value) => Math.Max(Math.Abs(value.XUm), Math.Abs(value.YUm));
    private static bool Equal(ST_MANUAL_OFFSET a, ST_MANUAL_OFFSET b) => Math.Abs(a.XUm - b.XUm) <= 1e-7 && Math.Abs(a.YUm - b.YUm) <= 1e-7;
    private static IReadOnlyList<T> ReadOnly<T>(T[] values) => Array.AsReadOnly(values);
    private static IReadOnlyDictionary<string, T> Freeze<T>(IEnumerable<KeyValuePair<string, T>> values)
        => new ReadOnlyDictionary<string, T>(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    private void Log(string kind, string message) => _audit.Add(new(DateTimeOffset.UtcNow, kind, message));
    private void MarkConsumed(string key, string token)
    {
        _appliedMeasurements[key] = token;
        if (!_consumedMeasurementHistory.TryGetValue(key, out var tokens)) _consumedMeasurementHistory.Add(key, tokens = new(StringComparer.Ordinal));
        tokens.Add(token);
    }
    private void AddHistory(ST_MANUAL_POINT before, ST_MANUAL_POINT after, string kind, string reason)
        => _history.Add(new(DateTimeOffset.UtcNow, before.Key, kind,
            new(before.Judge, before.ErrorXUm, before.ErrorYUm, before.Source, before.ProxySource),
            new(after.Judge, after.ErrorXUm, after.ErrorYUm, after.Source, after.ProxySource),
            before.OriginalMeasurement, after.OriginalMeasurement, reason));
    private sealed record CopyUndo(ST_MANUAL_CANDIDATE? Candidate, IReadOnlyDictionary<string, ST_MANUAL_POINT> Points, string AfterFingerprint);
}
