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
    private readonly List<ST_MANUAL_REVISION> _offsetTimeline = [];
    // Evidence comes only from this process's real load/commit boundary, never session JSON.
    private readonly Dictionary<string,ST_MANUAL_REVISION> _verifiedStoredRevisions=new(StringComparer.Ordinal);
    private int _offsetHistoryCursor;
    public const int MaxOffsetUndoSteps = 10;
    private readonly List<ST_MANUAL_AUDIT> _audit = [];
    private readonly List<ST_MANUAL_HISTORY> _history = [];
    private readonly List<ST_MANUAL_CANDIDATE> _candidateBackups = [];
    private readonly Dictionary<string,ST_MANUAL_CALIBRATION_BASIS> _calibrationBases=new(StringComparer.Ordinal);
    private ManualInspectionFieldEvaluator? _fieldEvaluator;
    private CopyUndo? _copyUndo;
    private ST_MANUAL_CANDIDATE? _offsetEditPreviousCandidate;
    private string? _pendingWriteToken;
    private string? _pendingWriteFingerprint;
    private ST_MANUAL_APPLY_SNAPSHOT? _pendingApply;
    private ST_MANUAL_RESTORE_SNAPSHOT? _pendingRestore;
    private long _generation;

    public ST_MANUAL_CONTEXT? Context { get; private set; }
    public ST_MANUAL_LIMITS? Limits { get; private set; }
    public IReadOnlyList<ST_MANUAL_POINT> Points => Array.AsReadOnly(_points.Values.ToArray());
    public IReadOnlyDictionary<string, ST_MANUAL_OFFSET> ActiveOffsets => Freeze(_active);
    public IReadOnlyList<ST_MANUAL_REVISION> Revisions => _revisions.AsReadOnly();
    public int UndoCount => Math.Max(0, _offsetHistoryCursor);
    public int RedoCount => Math.Max(0, _offsetTimeline.Count - 1 - _offsetHistoryCursor);
    public bool CanUndoOffsets => Context?.LineageValid == true && !IsBusy && !HasIncompleteMeasurements && !HasPendingOffsetEdit && UndoCount > 0;
    public bool CanRedoOffsets => Context?.LineageValid == true && !IsBusy && !HasIncompleteMeasurements && !HasPendingOffsetEdit && RedoCount > 0;
    /// <summary>Saved model state; a new unsaved measurement can clear only a displayed point's flag.</summary>
    public IReadOnlyList<string> ActiveInitializedOffsetKeys => _revisions.LastOrDefault(revision => revision.Id == Context?.RevisionId)?.InitializedOffsetKeys ?? [];
    public IReadOnlyList<ST_MANUAL_AUDIT> Audit => _audit.AsReadOnly();
    public IReadOnlyList<ST_MANUAL_HISTORY> History => _history.AsReadOnly();
    public IReadOnlyList<ST_MANUAL_CANDIDATE> CandidateBackups => _candidateBackups.AsReadOnly();
    public ST_MANUAL_CANDIDATE? Candidate { get; private set; }
    /// <summary>Host-owned current-run evidence, never restored from session JSON.</summary>
    public IReadOnlyDictionary<string,ST_MANUAL_CALIBRATION_BASIS> CalibrationBases
        => new ReadOnlyDictionary<string,ST_MANUAL_CALIBRATION_BASIS>(new Dictionary<string,ST_MANUAL_CALIBRATION_BASIS>(_calibrationBases,StringComparer.Ordinal));
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
        ManualInspectionFieldEvaluator? fieldEvaluator,
        IReadOnlySet<string>? activeInitializedOffsetKeys = null,
        IReadOnlyDictionary<string,ST_MANUAL_CALIBRATION_BASIS>? calibrationBases = null)
    {
        GuardIdle();
        ValidateContext(context); ValidateLimits(limits);
        var sameRun = Context?.RecipeId == context.RecipeId && Context.RunId == context.RunId;
        var sameStoredFrame = sameRun && Context!.CoordinateSignature == context.CoordinateSignature
            && Context.RevisionId == context.RevisionId;
        if (sameRun && HasPendingOffsetEdit) throw new InvalidOperationException("통합 Offset 편집을 저장하거나 취소한 후 작업공간을 다시 불러오세요.");
        if (sameRun && HasIncompleteMeasurements)
            throw new InvalidOperationException("같은 Run의 미완료 측정을 재로드로 지울 수 없습니다. 잔여 측정을 완료하거나 새 Run을 여세요.");
        var data = points.ToArray();
        if (data.Length == 0) throw new InvalidOperationException("작업공간에 표시할 위치가 없습니다.");
        if (data.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != data.Length)
            throw new InvalidOperationException("측정 위치 Key가 중복됐습니다.");
        var dataKeys=data.Select(p=>p.Key).ToHashSet(StringComparer.Ordinal);
        var values = new Dictionary<string, ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        var offsetGroups = new Dictionary<string, ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        foreach (var point in data)
        {
            ValidatePoint(point);
            if(point.StateCopySource is { } state && (state.RecipeId!=context.RecipeId || state.RunId!=context.RunId || state.CoordinateSignature!=context.CoordinateSignature
                || state.ReferenceKey==point.Key || !dataKeys.Contains(state.ReferenceKey)))
                throw new InvalidOperationException($"{point.Key}: 상태 복사의 모델·좌표 기준이 다릅니다.");
            if (point.AdoptedFileMeasurement is { } adopted)
            {
                ValidateMeasurement(adopted,context);
                if (point.Source is not ("IMPORTED" or "MANUAL" or "OPERATOR"))
                    throw new InvalidOperationException("파일 입력 근거를 직접 카메라 원측정으로 바꿀 수 없습니다.");
            }
            var value = baseValuesByPointKey.TryGetValue(point.Key, out var supplied) ? supplied : ST_MANUAL_OFFSET.Zero;
            if (!value.IsFinite) throw new InvalidOperationException($"{point.Key}: 현재 보정값이 유효한 숫자가 아닙니다.");
            if (point.IsMeasurementOffsetInitialized && value != ST_MANUAL_OFFSET.Zero)
                throw new InvalidOperationException($"{point.Key}: 초기화 표시는 0 보정값에만 사용할 수 있습니다.");
            if (offsetGroups.TryGetValue(point.OffsetKey, out var previous) && !Equal(previous, value))
                throw new InvalidOperationException($"{point.OffsetKey}: 같은 Cell/Shot의 Beam별 현재 보정값이 서로 다릅니다.");
            offsetGroups[point.OffsetKey] = value; values.Add(point.Key, value);
        }
        var initializedKeys=activeInitializedOffsetKeys?.ToArray()
            ?? data.Where(point=>point.IsMeasurementOffsetInitialized).Select(point=>point.OffsetKey).Distinct(StringComparer.Ordinal).ToArray();
        ValidateInitializedKeys(initializedKeys,data.ToDictionary(point=>point.Key,StringComparer.Ordinal),values,"현재 파일");
        _points.Clear(); foreach (var point in data) _points.Add(point.Key, point);
        _active.Clear(); foreach (var pair in values) _active.Add(pair.Key, pair.Value);
        if (!sameRun) { _appliedMeasurements.Clear(); _consumedMeasurementHistory.Clear(); _appliedCandidates.Clear(); }
        Context = context; Limits = limits; _fieldEvaluator = fieldEvaluator;
        SetCalibrationAuthority(calibrationBases);
        Candidate = null; Operation = null; _copyUndo = null; _offsetEditPreviousCandidate = null;
        _history.Clear(); _candidateBackups.Clear();
        _revisions.Clear();
        _revisions.Add(new(context.RevisionId, "", "INITIAL", "현재 실제 Review Offset 기준", DateTimeOffset.UtcNow, Freeze(_active))
        { InitializedOffsetKeys = ReadOnly(initializedKeys) });
        _offsetTimeline.Clear(); _offsetTimeline.Add(_revisions[^1]); _offsetHistoryCursor = 0;
        // A same-frame host refresh re-reads the real active file before restoring its in-memory
        // session. Retain process-local history evidence only when that actual current revision,
        // every value and every initialized key still exactly match what this process verified.
        // A different model/frame/revision or changed file contents starts a new trust boundary.
        var preserveVerified = sameStoredFrame
            && _verifiedStoredRevisions.TryGetValue(context.RevisionId, out var currentProof)
            && VerifiedRevisionMatches(_revisions[^1], currentProof);
        if (!preserveVerified) _verifiedStoredRevisions.Clear();
        _verifiedStoredRevisions[context.RevisionId]=_revisions[^1];
        _generation++; Log("LOAD", $"{context.RecipeId} / {context.RunId} / {data.Length}점");
    }

    public ST_MANUAL_POINT GetPoint(string key) => _points.TryGetValue(key, out var point)
        ? point : throw new InvalidOperationException($"작업공간에 없는 위치입니다: {key}");

    /// <summary>After the host rechecks real geometry, old uncertain observations stay non-native but fresh measurement is allowed.</summary>
    public void ConfirmEditableMeasurementContext(ST_MANUAL_CONTEXT context, IReadOnlySet<string>? activeInitializedOffsetKeys = null,
        IReadOnlyDictionary<string,ST_MANUAL_CALIBRATION_BASIS>? calibrationBases = null)
    {
        GuardIdle(); RequireLoaded(); ValidateContext(context);
        if (context.RecipeId!=Context!.RecipeId || context.RunId!=Context.RunId || context.RevisionId!=Context.RevisionId
            || context.CoordinateSignature!=Context.CoordinateSignature || !context.LineageValid)
            throw new InvalidOperationException("현재 모델·회차·좌표·활성 보정 기준을 다시 연결해야 합니다.");
        if(activeInitializedOffsetKeys is not null)
        {
            ValidateInitializedKeys(activeInitializedOffsetKeys.ToArray(),_points,_active,"현재 파일");
            if(!activeInitializedOffsetKeys.SetEquals(ActiveInitializedOffsetKeys))
                throw new InvalidOperationException("현재 파일의 초기화 상태가 작업 기준과 다릅니다. 현재 모델을 다시 불러오세요.");
        }
        SetCalibrationAuthority(calibrationBases);
        if (Context.LineageValid) return;
        foreach (var before in _points.Values.ToArray())
        {
            var after=before with { Source=before.IsMeasurementStateCleared?"MANUAL_STATE":"IMPORTED",ProxySource=null,AdoptedFileMeasurement=null,OperatorResult=null };
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
            var existingAt = WorkingStateAt(previous);
            var clearedAt = previous.StateCopySource?.CopiedAt ?? DateTimeOffset.MinValue;
            if (previous.MeasurementStateChangedAt is { } changedAt && changedAt>clearedAt) clearedAt=changedAt;
            if (observedAt < existingAt) continue;
            if (clearedAt!=DateTimeOffset.MinValue && observedAt<=clearedAt) continue;
            if (previous.Judge == incoming.Judge && previous.ErrorXUm == incoming.ErrorXUm && previous.ErrorYUm == incoming.ErrorYUm
                && (previous.Source == "MEASURED" || previous.Source == "IMPORTED" && previous.OriginalMeasurement == incoming.OriginalMeasurement
                    && previous.ExternalObservedAt == observedAt)) continue;
            replacements.Add((previous, incoming with { MeasurementVersion = previous.MeasurementVersion + 1, ExternalObservedAt = observedAt,
                IsMeasurementStateCleared=false,StateCopySource=null,MeasurementStateChangedAt=previous.MeasurementStateChangedAt }));
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
        GuardIdle(); RequireLoaded(); ValidateLimits(limits);
        var currentOffsetEdit = HasPendingOffsetEdit && Candidate!.SourceFingerprint == SourceFingerprint();
        Limits = limits; Changed();
        // Absolute Init/file drafts have no measurement to recalculate. A previously current draft
        // keeps its exact values but must pass every new limit again; stale drafts stay stale.
        if (currentOffsetEdit) Candidate = Candidate! with { SourceFingerprint = SourceFingerprint() };
        Log("LIMITS", "보정 한계가 바뀌어 후보 검증을 무효화했습니다.");
    }

    public void UpdateContext(ST_MANUAL_CONTEXT context)
    {
        GuardIdle(); RequireLoaded(); ValidateContext(context);
        if (context.RecipeId != Context!.RecipeId || context.RunId != Context.RunId || context.RevisionId != Context.RevisionId)
            throw new InvalidOperationException("Recipe/Run/활성 보정 변경은 실제 기준값과 함께 작업공간을 다시 로드해야 합니다.");
        if (HasIncompleteMeasurements && context.CoordinateSignature != Context.CoordinateSignature)
            throw new InvalidOperationException("미완료 측정 중 좌표 기준을 바꿀 수 없습니다. 잔여 측정을 마치거나 새 Run으로 로드하세요.");
        if(context.CoordinateSignature!=Context.CoordinateSignature)
        {
            // Correction states belong to their coordinate frame; retaining audit is not permission
            // to traverse pre-change states under the newly selected scanner/recipe conversion.
            _offsetTimeline.Clear();_offsetTimeline.Add(_revisions[^1]);_offsetHistoryCursor=0;
            _verifiedStoredRevisions.Clear();_verifiedStoredRevisions[context.RevisionId]=_revisions[^1];
        }
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
        // Only a new host-delivered response can acquire today's independently verified
        // processing basis. Legacy/imported/session packets are never silently backfilled.
        if(measurement.CalibrationBasis is null&&string.IsNullOrWhiteSpace(measurement.CalibrationBasisUnavailableReason)&&_calibrationBases.TryGetValue(point.OffsetKey,out var nativeBasis)
            &&MeasurementModeMatches(measurement,nativeBasis)
            &&BasisMatches(nativeBasis,point,measurement.MeasuredAt))measurement=measurement with{CalibrationBasis=nativeBasis};
        _points[key] = point with
        {
            Judge = measurement.Judge, Source = "MEASURED", ErrorXUm = measurement.ErrorXUm,
            ErrorYUm = measurement.ErrorYUm, OriginalMeasurement = measurement, AdoptedFileMeasurement = null, ProxySource = null,OperatorResult=null,
            MeasurementVersion = point.MeasurementVersion + 1, ExternalObservedAt = null, IsMeasurementOffsetInitialized=false,
            IsMeasurementStateCleared=false,StateCopySource=null,MeasurementStateChangedAt=NextStateEpoch([point],measurement.MeasuredAt)
        };
        AddHistory(point, _points[key], "MEASURE", "직접 재측정; 이전 원측정/작업상태 보존");
        Changed(); Log("MEASURE", $"{key}: {measurement.Judge}; 원측정 기록 갱신");
    }

    /// <summary>Confirm a working result manually without changing raw, current B or any file.</summary>
    public void RecordOperatorResults(IEnumerable<string> keys,double? errorXUm,double? errorYUm,string reason)
    {
        GuardCorrection();GuardNoOffsetEdit();
        if(string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("수동 측정 완료 사유를 입력하세요.");
        if(errorXUm.HasValue!=errorYUm.HasValue || errorXUm is double x && !double.IsFinite(x) || errorYUm is double y && !double.IsFinite(y))
            throw new InvalidOperationException("수동 편차 X/Y를 모두 유효한 숫자로 입력하세요.");
        var selected=Select(keys,false).Select(GetPoint).ToArray();
        if(selected.Length==0 || selected.Any(p=>p.Masked)) throw new InvalidOperationException("수동 완료할 유효한 위치를 선택하세요. Masking 위치는 변경할 수 없습니다.");
        // Prepare every result before changing any point. Missing values on one target must not
        // partly acknowledge the others or release an interrupted measurement operation.
        var prepared=new Dictionary<string,ST_MANUAL_OPERATOR_RESULT>(StringComparer.Ordinal);
        foreach(var point in selected)
        {
            double? dx=errorXUm,dy=errorYUm;ST_MANUAL_OFFSET final;
            if(errorXUm.HasValue)
            {
                var repeated=point.OperatorResult is { } prior && ValidOperatorResult(point,prior,Context!)
                    && prior.ErrorXUm==errorXUm && prior.ErrorYUm==errorYUm;
                final=repeated?point.OperatorResult!.FinalOffset:Add(_active[point.Key],new(errorXUm.Value,errorYUm!.Value));
            }
            else
            {
                try
                {
                    var existing=ResolveReferenceValue(point.Key);
                    // A displayed unadopted file is not a native anchor. This explicit operator
                    // action accepts its shown numbers against the current B without changing raw.
                    if(point.Source=="IMPORTED" && !point.IsDirectMeasurement && point.HasFiniteError)
                    {dx=point.ErrorXUm;dy=point.ErrorYUm;final=Add(_active[point.Key],new(dx!.Value,dy!.Value));}
                    else {final=existing.Final;dx=existing.ErrorXUm;dy=existing.ErrorYUm;}
                }
                catch(InvalidOperationException)
                {
                    if(!point.IsMeasurementOffsetInitialized && !point.IsMeasurementStateCleared && point.HasFiniteError)
                    {dx=point.ErrorXUm;dy=point.ErrorYUm;final=Add(_active[point.Key],new(dx!.Value,dy!.Value));}
                    else throw new InvalidOperationException($"{point.Key}: 승인할 숫자 근거가 없습니다. 편차 X/Y를 직접 입력하세요.");
                }
            }
            if(!final.IsFinite) throw new InvalidOperationException($"{point.Key}: 수동 최종 보정값이 유효한 숫자가 아닙니다.");
            var at=NextStateEpoch([point]);
            var result=new ST_MANUAL_OPERATOR_RESULT(NewId("OPERATOR"),"OK",dx,dy,_active[point.Key],final,reason.Trim(),
                Context!.RecipeId,Context.RunId,Context.CoordinateSignature,Context.RevisionId,at)
                {SubjectPointKey=point.Key,OriginalSource=HistoricalObservationSource(point),ObservedSource=point.Source,
                    ObservedCoordinateSignature=point.OperatorResult?.CoordinateSignature ?? point.ProxySource?.CoordinateSignature
                        ?? point.CorrectionMeasurement?.CoordinateSignature ?? point.OriginalMeasurement?.CoordinateSignature ?? Context.CoordinateSignature,
                    ObservedValueProof=point.OperatorResult?.ValueProof ?? point.ProxySource?.ReferenceValueProof
                        ?? point.CorrectionMeasurement?.RawToken ?? point.OriginalMeasurement?.RawToken ?? ""};
            prepared.Add(point.Key,result with{ValueProof=OperatorValueProof(result)});
        }
        foreach(var point in selected)
        {
            var result=prepared[point.Key];var after=point with{Judge="OK",Source="OPERATOR",ErrorXUm=result.ErrorXUm,ErrorYUm=result.ErrorYUm,
                OperatorResult=result,ProxySource=null,IsMeasurementOffsetInitialized=false,IsMeasurementStateCleared=false,StateCopySource=null,
                ExternalObservedAt=null,MeasurementStateChangedAt=result.AcceptedAt};
            _points[point.Key]=after;AddHistory(point,after,"OPERATOR_COMPLETE",reason.Trim());
        }
        Changed();Log("OPERATOR_COMPLETE",$"{selected.Length}점 수동 OK 완료 · 원측정/활성 B/파일 보존 · 보정 저장은 별도 검증");
    }

    /// <summary>Explicit Manual acknowledgement captures the input XY itself as the replacement F.</summary>
    public void RecordMeasurementOperatorResults(IEnumerable<string> keys,double? x,double? y,string reason)
    {
        GuardCorrection();GuardNoOffsetEdit();var selected=Select(keys,false);
        if(string.IsNullOrWhiteSpace(reason)||selected.Length==0||selected.Select(GetPoint).Any(p=>p.Masked))
            throw new InvalidOperationException("수동 확정할 위치와 사유를 확인하세요. Masking은 변경할 수 없습니다.");
        if(x.HasValue!=y.HasValue||x is double dx&&!double.IsFinite(dx)||y is double dy&&!double.IsFinite(dy))
            throw new InvalidOperationException("최종 Review 보정값 X/Y를 모두 유효한 숫자로 입력하세요.");
        var prepared=new Dictionary<string,ST_MANUAL_OPERATOR_RESULT>(StringComparer.Ordinal);
        foreach(var key in selected)
        {
            var point=GetPoint(key);var existing=x.HasValue?null:ResolveMeasurementReferenceValue(key);
            var final=x.HasValue?new ST_MANUAL_OFFSET(x.Value,y!.Value):existing!.Final;
            var kind=x.HasValue?"ABSOLUTE_INPUT":existing!.CalibrationBasis is not null&&existing.Kind=="CALIBRATED_MEASUREMENT"?"RESIDUAL_APPROVAL":"ABSOLUTE_INPUT";
            var value=new ST_MANUAL_OPERATOR_RESULT(NewId("OPERATOR"),"OK",point.ErrorXUm,point.ErrorYUm,_active[key],final,reason.Trim(),
                Context!.RecipeId,Context.RunId,Context.CoordinateSignature,Context.RevisionId,NextStateEpoch([point]))
                {SubjectPointKey=key,ValueKind=kind,CalibrationBasis=kind=="RESIDUAL_APPROVAL"?existing!.CalibrationBasis:null,
                    OriginalSource=HistoricalObservationSource(point),ObservedSource=point.Source,
                    ObservedCoordinateSignature=point.CorrectionMeasurement?.CoordinateSignature??point.OriginalMeasurement?.CoordinateSignature??Context.CoordinateSignature,
                    ObservedValueProof=point.OperatorResult?.ValueProof??point.ProxySource?.ReferenceValueProof??point.CorrectionMeasurement?.RawToken??""};
            prepared[key]=value with{ValueProof=OperatorValueProof(value)};
        }
        foreach(var key in selected)
        {
            var point=GetPoint(key);var value=prepared[key];
            _points[key]=point with{OperatorResult=value,ProxySource=null,Source="OPERATOR",Judge="OK",IsMeasurementStateCleared=false,StateCopySource=null,
                IsMeasurementOffsetInitialized=false,MeasurementStateChangedAt=value.AcceptedAt};
            AddHistory(point,_points[key],"OPERATOR_ABSOLUTE_REVIEW",reason.Trim());
        }
        Changed();Log("OPERATOR_MEASUREMENT_VALUE","최종 Review 값 명시 입력 또는 가공 기준 추천 승인 · 원측정/B 불변");
    }

    public ST_MANUAL_BULK_PREVIEW PreviewBulkCorrection(IEnumerable<string>? selectedKeys=null)
    {
        RequireLoaded();
        var population=selectedKeys is null?_points.Values.ToArray():Select(selectedKeys,false).Select(GetPoint).ToArray();
        return PreviewBulkCorrectionCore(population,ActiveInitializedOffsetKeys.ToHashSet(StringComparer.Ordinal));
    }

    public ST_MANUAL_BULK_PREVIEW PreviewMeasurementReplacement(IEnumerable<string>? keys=null)
    {
        RequireLoaded();var population=keys is null?_points.Values.ToArray():Select(keys,false).Select(GetPoint).ToArray();
        return PreviewBulkCorrectionCore(population,ActiveInitializedOffsetKeys.ToHashSet(StringComparer.Ordinal),true);
    }

    public IReadOnlyDictionary<int,ST_MANUAL_BULK_PREVIEW> PreviewMeasurementReplacementByCell()
    {
        RequireLoaded();var initialized=ActiveInitializedOffsetKeys.ToHashSet(StringComparer.Ordinal);
        return _points.Values.GroupBy(p=>p.CellNo).ToDictionary(g=>g.Key,g=>PreviewBulkCorrectionCore(g.ToArray(),initialized,true));
    }

    /// <summary>All cell summaries in one population pass, sharing the immutable initialization lookup.</summary>
    public IReadOnlyDictionary<int,ST_MANUAL_BULK_PREVIEW> PreviewBulkCorrectionByCell()
    {
        RequireLoaded();
        var initialized=ActiveInitializedOffsetKeys.ToHashSet(StringComparer.Ordinal);
        return _points.Values.GroupBy(p=>p.CellNo).ToDictionary(group=>group.Key,
            group=>PreviewBulkCorrectionCore(group.ToArray(),initialized));
    }

    private ST_MANUAL_BULK_PREVIEW PreviewBulkCorrectionCore(IReadOnlyList<ST_MANUAL_POINT> population,IReadOnlySet<string> initialized,bool measurementReplacement=false)
    {
        var keys=new List<string>();
        var masked=0;var ng=0;var missing=0;var consumed=0;var unchanged=0;var invalid=0;var manual=0;var numeric=0;
        foreach(var point in population)
        {
            if(point.Masked) { masked++;continue; }
            if(point.Judge=="NG") { ng++;continue; }
            if(point.IsMeasurementOffsetInitialized || point.IsMeasurementStateCleared || point.Judge!="OK")
            { missing++;continue; }
            ST_MANUAL_OFFSET final;
            ST_MANUAL_REFERENCE_VALUE? recommendation=null;
            var isManual=point.ProxySource is not null || point.OperatorResult is not null;
            if(isManual)
            {
                try { recommendation=measurementReplacement?ResolveMeasurementReferenceValue(point.Key):ResolveReferenceValue(point.Key);final=recommendation.Final; }
                catch(InvalidOperationException) { invalid++;continue; }
                if(measurementReplacement&&WasLegacyManualValueApplied(point,final)){consumed++;continue;}
                if(point.HasAppliedOffsetEvidence && !initialized.Contains(point.OffsetKey) && Equal(QuantizeForOffsetFile(final),QuantizeForOffsetFile(_active[point.Key]))) { unchanged++;continue; }
            }
            else
            {
                if(!point.IsDirectMeasurement || !point.HasFiniteError) { missing++;continue; }
                try { RequireMeasurementLineage(point); }
                catch(InvalidOperationException) { invalid++;continue; }
                if(WasConsumed(point.Key,MeasurementToken(point))) { consumed++;continue; }
                var error=new ST_MANUAL_OFFSET(point.ErrorXUm!.Value,point.ErrorYUm!.Value);
                if(measurementReplacement)
                    try{recommendation=ResolveMeasurementReferenceValue(point.Key);final=recommendation.Final;}
                    catch(InvalidOperationException){invalid++;continue;}
                else final=Add(_active[point.Key],error);
            }
            if(measurementReplacement&&point.HasAppliedOffsetEvidence&&!initialized.Contains(point.OffsetKey)
                &&Equal(QuantizeForOffsetFile(final),QuantizeForOffsetFile(_active[point.Key]))){unchanged++;continue;}
            var stored=final.IsFinite?QuantizeForOffsetFile(final):final;
            var increment=measurementReplacement?RecommendedIncrement(point,recommendation!):Subtract(final,_active[point.Key]);
            var storedIncrement=measurementReplacement&&recommendation!.Kind=="CALIBRATED_MEASUREMENT"?Subtract(stored,recommendation.CalibrationBasis!.BaseOffsetUm)
                :measurementReplacement?ST_MANUAL_OFFSET.Zero:Subtract(stored,_active[point.Key]);
            if(!stored.IsFinite || Magnitude(increment)>Limits!.MaxDeltaUm || Magnitude(final)>Limits.MaxTotalUm
                || Magnitude(storedIncrement)>Limits.MaxDeltaUm || Magnitude(stored)>Limits.MaxTotalUm) {numeric++;continue;}
            keys.Add(point.Key);if(isManual)manual++;
        }
        return new(ReadOnly(keys.ToArray()),population.Count,masked,ng,missing,consumed,unchanged,invalid,manual){ExcludedNumeric=numeric};
    }

    public ST_MANUAL_CANDIDATE BuildBulkCandidate() => BuildBulkCandidateCore(null);

    /// <summary>Builds only the explicitly supplied population; other cells and hidden selections are unchanged.</summary>
    public ST_MANUAL_CANDIDATE BuildBulkCandidate(IEnumerable<string> keys)
        => BuildBulkCandidateCore(keys ?? throw new ArgumentNullException(nameof(keys)));

    private ST_MANUAL_CANDIDATE BuildBulkCandidateCore(IEnumerable<string>? keys)
    {
        GuardCorrection();GuardNoOffsetEdit();
        var preview=PreviewBulkCorrection(keys);
        if(preview.Keys.Count==0) throw new InvalidOperationException("일괄 보정할 새 OK 측정/대표 결과가 없습니다. NG·미측정·초기화·이미 적용된 결과는 제외합니다.");
        return BuildDirectCandidate(preview.Keys);
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
            if(point.OperatorResult is { } result)
            {
                RequireOperatorResult(point);
                rows.Add(new(key,point.OffsetKey,point.CellNo,point.HeadNo,point.ShotNo,point.BeamNo,
                    before,Subtract(result.FinalOffset,before),result.FinalOffset,"MANUAL",key,result.Reason,""));
            }
            else if (point.ProxySource is { } proxy)
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

    /// <summary>Manual-only replacement. Delta remains F-B for audit; F is the captured measurement XY.</summary>
    public ST_MANUAL_CANDIDATE BuildMeasurementReplacementCandidate(IEnumerable<string> keys)
    {
        GuardCorrection();GuardNoOffsetEdit();var selected=Select(keys,true);
        if(selected.Length==0)throw new InvalidOperationException("보정할 측정 위치를 선택하세요.");
        var rows=new List<ST_MANUAL_CANDIDATE_ROW>();
        foreach(var key in selected)
        {
            var point=GetPoint(key);var value=ResolveMeasurementReferenceValue(key);var before=_active[key];
            var manual=point.ProxySource is not null||point.OperatorResult is not null;
            rows.Add(new(key,point.OffsetKey,point.CellNo,point.HeadNo,point.ShotNo,point.BeamNo,before,Subtract(value.Final,before),value.Final,
                manual?"MANUAL":"MEASURED",manual?point.OperatorResult is not null?key:point.ProxySource!.ReferenceKey:key,
                manual?point.OperatorResult?.Reason??point.ProxySource!.Reason:"현재 측정편차로 Review Offset 교체",manual?"":MeasurementToken(point)));
        }
        if(Candidate is {State:not EN_MANUAL_CANDIDATE_STATE.Applied})_candidateBackups.Add(Candidate);
        Candidate=NewCandidate(rows) with{IsMeasurementReplacement=true,CalibrationPolicyVersion=9};_generation++;_copyUndo=null;
        Log("MEASUREMENT_REPLACEMENT","실측은 실제 가공 기준의 추천 F, 명시 절대값은 그대로 대체");return Candidate;
    }

    /// <summary>Prepare a whole-model absolute Review map; no active value, raw packet or file is changed.</summary>
    public ST_MANUAL_CANDIDATE BuildOffsetReplacementCandidate(
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values, string reason, bool initializeAllReviewRows,
        IReadOnlySet<string>? initializedOffsetKeys = null)
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
        var initialized=initializeAllReviewRows?byShot.Keys.ToArray():initializedOffsetKeys?.ToArray()??[];
        if(initialized.Any(key=>!byShot.TryGetValue(key,out var value)||value!=ST_MANUAL_OFFSET.Zero))
            throw new InvalidOperationException("초기화 표시는 현재 모델의 0 보정에만 사용할 수 있습니다.");
        _offsetEditPreviousCandidate = Candidate;
        if (Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied }) _candidateBackups.Add(Candidate);
        Candidate = NewCandidate(rows) with { IsOffsetReplacement=true, InitializeAllReviewRows=initializeAllReviewRows,
            InitializationOffsetKeys=initializeAllReviewRows?byShot.Keys.ToArray():[],InitializedOffsetKeys=initialized };
        _generation++; _copyUndo=null;
        Log("OFFSET_EDIT", "모델 전체 측정 보정의 저장 전 작업값입니다. 보호 설정과 활성 파일은 유지합니다.");
        return Candidate;
    }

    /// <summary>Stage only one Cell from a complete imported map or a Cell Init; all other states are retained.</summary>
    public ST_MANUAL_CANDIDATE BuildScopedOffsetReplacementCandidate(int cellNo,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> imported,
        IReadOnlySet<string> importedInitialized,string reason,bool initialize=false)
    {
        GuardCorrection();GuardNoOffsetEdit();
        var inside=_points.Values.Where(p=>p.CellNo==cellNo).Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal);
        if(cellNo<=0||inside.Count==0)throw new InvalidOperationException("작업할 현재 Cell 하나를 먼저 선택하세요.");
        if(imported.Count!=_points.Count||_points.Keys.Any(key=>!imported.ContainsKey(key)))throw new InvalidOperationException("가져온 Offset의 전체 위치를 확인할 수 없습니다.");
        var values=_points.Values.ToDictionary(p=>p.Key,p=>p.CellNo==cellNo?initialize?ST_MANUAL_OFFSET.Zero:imported[p.Key]:_active[p.Key],StringComparer.Ordinal);
        var initialized=ActiveInitializedOffsetKeys.Where(key=>!inside.Contains(key)).ToHashSet(StringComparer.Ordinal);
        initialized.UnionWith(initialize?inside:importedInitialized.Where(inside.Contains));
        var candidate=BuildOffsetReplacementCandidate(values,reason,false,initialized);
        Candidate=candidate with{ReplacementCellNo=cellNo,InitializationOffsetKeys=initialize?inside.ToArray():[],InitializationCellNos=initialize?[cellNo]:[],
            Rows=ReadOnly(candidate.Rows.Select(r=>r with{Source=r.CellNo!=cellNo?"OFFSET_KEEP":initialize?"OFFSET_INIT":"OFFSET_FILE"}).ToArray())};
        return Candidate;
    }

    public ST_MANUAL_CANDIDATE InitializeMeasurementOffsets(IEnumerable<string>? keys=null,IReadOnlyList<int>? cellNos=null)
    {
        if(keys is null) return BuildOffsetReplacementCandidate(
            _points.Keys.ToDictionary(key=>key,_=>ST_MANUAL_OFFSET.Zero,StringComparer.Ordinal),
            "Offset Init · 측정 보정 전체 초기화 · 보호 설정 유지",true);
        GuardCorrection();GuardNoOffsetEdit();
        var selected=Select(keys,excludeMasked:false);
        if(selected.Length==0) throw new InvalidOperationException("초기화할 Cell 또는 Head를 선택하세요.");
        var affected=selected.Select(key=>GetPoint(key).OffsetKey).ToHashSet(StringComparer.Ordinal);
        var explicitCells=cellNos?.Distinct().ToArray()??[];
        if(explicitCells.Any(cell=>cell<=0 || !_points.Values.Any(p=>p.CellNo==cell)
            || _points.Values.Any(p=>p.CellNo==cell && !affected.Contains(p.OffsetKey))))
            throw new InvalidOperationException("Cell 초기화는 해당 Cell의 모든 Shot을 포함해야 합니다.");
        var initialized=ActiveInitializedOffsetKeys.ToHashSet(StringComparer.Ordinal);
        initialized.UnionWith(affected);
        var values=_points.Values.ToDictionary(p=>p.Key,p=>affected.Contains(p.OffsetKey)?ST_MANUAL_OFFSET.Zero:_active[p.Key],StringComparer.Ordinal);
        var candidate=BuildOffsetReplacementCandidate(values,"선택 Cell/Head 측정 보정 초기화 · 보호 설정 유지",false,initialized);
        Candidate=candidate with { InitializationOffsetKeys=affected.ToArray(),InitializationCellNos=explicitCells,
            Rows=ReadOnly(candidate.Rows.Select(row=>row with { Source=affected.Contains(row.OffsetKey)?"OFFSET_INIT":"OFFSET_KEEP" }).ToArray()) };
        return Candidate;
    }

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
        => ResolveReferenceValue(referenceKey).Final;

    /// <summary>A reference value is a captured DESIGN-µm value, not an approval or invented raw.</summary>
    public ST_MANUAL_REFERENCE_VALUE ResolveReferenceValue(string referenceKey)
    {
        RequireLoaded(); var reference = GetPoint(referenceKey);
        if (reference.IsMeasurementOffsetInitialized || reference.IsMeasurementStateCleared)
            throw new InvalidOperationException("대표점으로 지정할 수 있지만 초기화·미측정 상태에는 복사할 값이 없습니다.");
        if(reference.OperatorResult is { } result)
        {
            RequireOperatorResult(reference);
            return new(referenceKey,result.Judge,result.ErrorXUm,result.ErrorYUm,result.FinalOffset,
                result.OperatorId,"OPERATOR","OPERATOR",result.BasisRevisionId);
        }
        if (reference.ProxySource is { } proxy)
        {
            ReferenceAncestors(referenceKey, _points);
            if (proxy.ReferenceKey == referenceKey || !_points.ContainsKey(proxy.ReferenceKey)
                || proxy.RunId != Context!.RunId || proxy.CoordinateSignature != Context.CoordinateSignature
                || proxy.ReferenceJudge != reference.Judge || proxy.ValueKind!="ABSOLUTE_REVIEW"&&(proxy.ErrorXUm != reference.ErrorXUm || proxy.ErrorYUm != reference.ErrorYUm)
                || proxy.ValueKind is not null and not "ABSOLUTE_REVIEW"
                ||proxy.ValueKind=="ABSOLUTE_REVIEW"&&proxy.SubjectPointKey!=referenceKey
                || !KnownReferenceKind(proxy.ReferenceValueKind) || !ValidProxyValueProof(proxy)
                || !proxy.FinalOffset.IsFinite || string.IsNullOrWhiteSpace(proxy.ReferenceMeasurementToken))
                throw new InvalidOperationException("대표 복사값의 출처 또는 좌표 기준을 확인할 수 없습니다.");
            if (string.IsNullOrEmpty(proxy.ReferenceValueKind))
            {
                var stored = reference.HasAppliedOffsetEvidence && !ActiveInitializedOffsetKeys.Contains(reference.OffsetKey)
                    && Equal(_active[referenceKey],proxy.FinalOffset);
                if (!stored)
                {
                    if (GetPoint(proxy.ReferenceKey).ProxySource is not null)
                        throw new InvalidOperationException("이전 복사값의 원본 근거를 다시 확인하세요.");
                    var root = ResolveReferenceValue(proxy.ReferenceKey);
                    if (root.Token!=proxy.ReferenceMeasurementToken || !Equal(root.Final,proxy.FinalOffset)
                        || root.Judge!=proxy.ReferenceJudge || root.ErrorXUm!=proxy.ErrorXUm || root.ErrorYUm!=proxy.ErrorYUm)
                        throw new InvalidOperationException("이전 복사값의 근거를 확인할 수 없습니다. 원본을 다시 지정하거나 새로 측정하세요.");
                }
            }
            return new(proxy.ReferenceKey, reference.Judge, reference.ErrorXUm, reference.ErrorYUm,
                proxy.FinalOffset, proxy.ReferenceMeasurementToken,
                string.IsNullOrWhiteSpace(proxy.ReferenceValueKind) ? "MEASURED" : proxy.ReferenceValueKind,
                string.IsNullOrWhiteSpace(proxy.ReferenceValueSource) ? "MANUAL" : proxy.ReferenceValueSource,
                string.IsNullOrWhiteSpace(proxy.BasisRevisionId) ? Context.RevisionId : proxy.BasisRevisionId);
        }
        if (reference.HeadNo is >= 1 and <= 8 && reference.IsDirectMeasurement && reference.HasFiniteError)
        {
            RequireMeasurementLineage(reference);
            var raw = reference.CorrectionMeasurement ?? reference.OriginalMeasurement;
            var rawToken = raw is null ? "" : MeasurementToken(referenceKey, raw);
            var consumed = rawToken.Length > 0 && WasConsumed(referenceKey, rawToken);
            var error = new ST_MANUAL_OFFSET(reference.ErrorXUm!.Value, reference.ErrorYUm!.Value);
            var finalValue = consumed ? _active[referenceKey] : Add(_active[referenceKey], error);
            if (!finalValue.IsFinite) throw new InvalidOperationException("대표 최종 보정값이 유효하지 않습니다.");
            var kind = consumed ? "APPLIED_MEASUREMENT" : "MEASURED";
            var token = rawToken.Length > 0 ? rawToken : Digest(new { referenceKey, Kind=kind, Context!.RunId,
                Context.CoordinateSignature, Context.RevisionId, reference.Source, reference.Judge, error, Final=finalValue });
            return new(referenceKey, reference.Judge, reference.ErrorXUm, reference.ErrorYUm, finalValue,
                token, kind, reference.Source, Context!.RevisionId);
        }
        if (!reference.HasAppliedOffsetEvidence || ActiveInitializedOffsetKeys.Contains(reference.OffsetKey))
            throw new InvalidOperationException("대표점으로 지정할 수 있지만 복사할 측정값이나 저장 보정값이 없습니다.");
        var final = _active[referenceKey];
        if (!final.IsFinite) throw new InvalidOperationException("대표 최종 보정값이 유효하지 않습니다.");
        // A real saved non-Init Review row is a known correction-based result even if no raw
        // packet survives. Only its reference status is known; never invent residual 0 or raw OK.
        var storedJudge=reference.HeadNo is >=1 and <=8 ? reference.Judge=="UNMEASURED"?"OK":reference.Judge : "UNMEASURED";
        return new(referenceKey, storedJudge, null, null, final,
            Digest(new { referenceKey, Kind="APPLIED_OFFSET", Context!.RunId, Context.CoordinateSignature,
                Context.RevisionId, Final=final }), "APPLIED_OFFSET", reference.Source, Context.RevisionId);
    }

    /// <summary>Recommend from the actual processing basis, or return an explicitly captured absolute Review value.</summary>
    public ST_MANUAL_REFERENCE_VALUE ResolveMeasurementReferenceValue(string referenceKey)
    {
        RequireLoaded();var point=GetPoint(referenceKey);
        if(point.IsMeasurementOffsetInitialized||point.IsMeasurementStateCleared)throw new InvalidOperationException("값 없음 상태입니다. 새로 측정하거나 최종 보정값을 명시적으로 입력하세요.");
        if(point.OperatorResult is { } op)
        {
            RequireOperatorResult(point);
            if(op.ValueKind=="RESIDUAL_APPROVAL")
            {
                var basis=RequireCalibrationBasis(point,op.CalibrationBasis,point.CorrectionMeasurement?.MeasuredAt??point.OriginalMeasurement?.MeasuredAt??op.AcceptedAt);
                if(op.ErrorXUm is not double x||op.ErrorYUm is not double y||!Equal(Recommend(basis,new(x,y)),op.FinalOffset))
                    throw new InvalidOperationException("승인한 실측 잔차와 가공 기준의 추천값이 일치하지 않습니다.");
                return new(referenceKey,op.Judge,op.ErrorXUm,op.ErrorYUm,op.FinalOffset,op.OperatorId,"CALIBRATED_MEASUREMENT","OPERATOR",op.BasisRevisionId){CalibrationBasis=basis};
            }
            if(op.ValueKind!="ABSOLUTE_INPUT")throw new InvalidOperationException("이전 방식의 수동값입니다. 새로 측정하거나 최종 Review 값을 명시적으로 입력하세요.");
            return new(referenceKey,op.Judge,op.ErrorXUm,op.ErrorYUm,op.FinalOffset,op.OperatorId,"ABSOLUTE_INPUT","OPERATOR",op.BasisRevisionId);
        }
        if(point.ProxySource is { } proxy)
        {
            var value=ResolveReferenceValue(referenceKey);
            if(proxy.ValueKind!="ABSOLUTE_REVIEW")throw new InvalidOperationException("이전 복사값의 의미를 확인할 수 없습니다. 추천 보정값을 다시 복사하거나 최종값을 입력하세요.");
            return value with{Kind="ABSOLUTE_PROXY",Final=proxy.FinalOffset};
        }
        if(!point.IsDirectMeasurement||!point.HasFiniteError||point.HeadNo is <1 or >8)
            throw new InvalidOperationException("현재 실측 잔차가 없습니다. 저장 B만으로 측정을 만들지 않습니다.");
        RequireMeasurementLineage(point);var raw=point.CorrectionMeasurement!;
        if(!string.IsNullOrWhiteSpace(raw.CalibrationBasisUnavailableReason))throw new InvalidOperationException(raw.CalibrationBasisUnavailableReason);
        var verified=RequireCalibrationBasis(point,raw.CalibrationBasis,raw.MeasuredAt);
        var final=Recommend(verified,new(point.ErrorXUm!.Value,point.ErrorYUm!.Value));
        return new(referenceKey,point.Judge,point.ErrorXUm,point.ErrorYUm,final,MeasurementToken(point),"CALIBRATED_MEASUREMENT",point.Source,Context!.RevisionId){CalibrationBasis=verified};
    }

    private void SetCalibrationAuthority(IReadOnlyDictionary<string,ST_MANUAL_CALIBRATION_BASIS>? values)
    {
        var changed=values is null?_calibrationBases.Count>0:_calibrationBases.Count!=values.Count
            ||_calibrationBases.Any(p=>!values.TryGetValue(p.Key,out var current)||current!=p.Value);
        _calibrationBases.Clear();
        if(values is not null)foreach(var pair in values)if(pair.Value is not null)_calibrationBases[pair.Key]=pair.Value;
        if(changed){_generation++;InvalidateCandidate();}
    }
    private bool BasisMatches(ST_MANUAL_CALIBRATION_BASIS basis,ST_MANUAL_POINT point,DateTimeOffset measuredAt)
        =>_calibrationBases.TryGetValue(point.OffsetKey,out var actual)&&actual==basis
            &&basis.RecipeId==Context!.RecipeId&&basis.RunId==Context.RunId&&basis.CoordinateSignature==Context.CoordinateSignature
            &&basis.CellNo==point.CellNo&&basis.ShotNo==point.ShotNo&&basis.HeadNo==point.HeadNo
            &&!string.IsNullOrWhiteSpace(basis.BasisId)&&!string.IsNullOrWhiteSpace(basis.ProcessId)&&!string.IsNullOrWhiteSpace(basis.ReviewCsvSha256)
            &&basis.CompletedAt!=default&&basis.CompletedAt<=measuredAt&&basis.BaseOffsetUm.IsFinite&&double.IsFinite(basis.ReviewOffsetScale);
    private ST_MANUAL_CALIBRATION_BASIS RequireCalibrationBasis(ST_MANUAL_POINT point,ST_MANUAL_CALIBRATION_BASIS? basis,DateTimeOffset measuredAt)
    {
        if(basis is null||!BasisMatches(basis,point,measuredAt))throw new InvalidOperationException("가공 당시 보정 기준을 확인할 수 없습니다. 측정은 가능하며, 자동 보정은 새 가공 기준이 필요합니다.");
        var raw=point.CorrectionMeasurement??point.OriginalMeasurement;
        if(raw is null||!MeasurementModeMatches(raw,basis)||!string.IsNullOrWhiteSpace(raw.CalibrationBasisUnavailableReason))
            throw new InvalidOperationException("가공·이동·Vision의 실제/SIM 기준을 확인할 수 없습니다. 측정은 가능하며 최종 Review 값은 명시 입력할 수 있습니다.");
        if(basis.ReviewOffsetScale<=0)throw new InvalidOperationException("가공 시 Review Scale이 0입니다. 자동 보정은 불가하며 최종값을 직접 입력할 수 있습니다.");
        return basis;
    }
    private static bool MeasurementModeMatches(ST_MANUAL_MEASUREMENT measurement,ST_MANUAL_CALIBRATION_BASIS basis)
        =>measurement.IsStageSimulation is bool stage&&measurement.IsVisionSimulation is bool vision&&stage==basis.IsSimulation&&vision==basis.IsSimulation;
    private static ST_MANUAL_OFFSET Recommend(ST_MANUAL_CALIBRATION_BASIS basis,ST_MANUAL_OFFSET residual)
    {
        var final=Add(basis.BaseOffsetUm,new(residual.XUm/basis.ReviewOffsetScale,residual.YUm/basis.ReviewOffsetScale));
        if(!final.IsFinite)throw new InvalidOperationException("가공 기준과 잔차로 계산한 추천 보정값이 유한하지 않습니다.");return final;
    }
    private ST_MANUAL_OFFSET RecommendedIncrement(ST_MANUAL_POINT point,ST_MANUAL_REFERENCE_VALUE value)
        =>value.Kind=="CALIBRATED_MEASUREMENT"&&value.CalibrationBasis is { } basis?Subtract(value.Final,basis.BaseOffsetUm):ST_MANUAL_OFFSET.Zero;

    public ST_MANUAL_COPY_PREVIEW PreviewReferenceCopy(string referenceKey, IEnumerable<string> selectedKeys,bool allowIncomplete=false)
        => PreviewReferenceCopyCore(referenceKey,selectedKeys,allowIncomplete,false);

    public ST_MANUAL_COPY_PREVIEW PreviewMeasurementReferenceCopy(string referenceKey,IEnumerable<string> selectedKeys,bool allowIncomplete=false)
        => PreviewReferenceCopyCore(referenceKey,selectedKeys,allowIncomplete,true);

    private ST_MANUAL_COPY_PREVIEW PreviewReferenceCopyCore(string referenceKey,IEnumerable<string> selectedKeys,bool allowIncomplete,bool measurementReplacement)
    {
        GuardCorrection(allowIncomplete); GuardNoOffsetEdit(); var reference = GetPoint(referenceKey);
        var value = measurementReplacement?ResolveMeasurementReferenceValue(referenceKey):ResolveReferenceValue(referenceKey); var final = value.Final;
        if (Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied }) GuardCandidateBase(Candidate);
        var selection = Select(selectedKeys, excludeMasked: false);
        var requested = selection.Where(key => key != referenceKey).ToArray();
        if (requested.Length == 0) throw new InvalidOperationException("대표점 이외의 복사 대상을 선택하세요.");
        var excluded = requested.Where(key => GetPoint(key).Masked).ToArray();
        var targets = requested.Where(key => !GetPoint(key).Masked).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("선택 대상이 모두 Masking 영역입니다.");
        if (targets.Contains(value.OriginReferenceKey, StringComparer.Ordinal))
            throw new InvalidOperationException("대표값의 원본 점으로 다시 복사할 수 없습니다. 순환 복사를 제외하세요.");
        var ancestors = ReferenceAncestors(referenceKey, _points);
        if (targets.Any(ancestors.Contains))
            throw new InvalidOperationException("대표값의 이전 원본으로 돌아가는 순환 복사는 할 수 없습니다.");
        var rows = targets.Select(key => new ST_MANUAL_COPY_TARGET(key, GetPoint(key).Judge, _active[key], Subtract(final, _active[key]))).ToArray();
        return new(referenceKey, ReadOnly(selection), ReadOnly(rows), ReadOnly(excluded), value.Judge,
            value.ErrorXUm, value.ErrorYUm, final, CopyFingerprint(referenceKey, selection))
        { OriginReferenceKey=value.OriginReferenceKey,ReferenceValueToken=value.Token,ReferenceValueKind=value.Kind,
            ReferenceValueSource=value.Source,BasisRevisionId=value.BasisRevisionId,IsMeasurementReplacement=measurementReplacement,CalibrationBasis=value.CalibrationBasis };
    }

    public ST_MANUAL_CANDIDATE ConfirmReferenceCopy(ST_MANUAL_COPY_PREVIEW preview, IEnumerable<string> selectedKeys, string reason)
    {
        GuardCorrection();
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("대표 복사 사유를 입력하세요.");
        var current = PreviewReferenceCopyCore(preview.ReferenceKey,selectedKeys,false,preview.IsMeasurementReplacement);
        if (preview.Fingerprint != current.Fingerprint || Digest(preview) != Digest(current))
            throw new InvalidOperationException("검토 뒤 측정·선택·후보·보정 기준이 바뀌었습니다. 다시 미리보기 하세요.");
        var beforeCandidate = Candidate;
        var beforePoints = current.Targets.ToDictionary(row => row.Key, row => GetPoint(row.Key), StringComparer.Ordinal);
        var rows = Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied }
            ? Candidate.Rows.ToDictionary(row => row.Key, StringComparer.Ordinal) : new Dictionary<string, ST_MANUAL_CANDIDATE_ROW>(StringComparer.Ordinal);
        var at = NextStateEpoch(beforePoints.Values.Append(GetPoint(current.ReferenceKey)));
        foreach (var target in current.Targets)
        {
            var point = GetPoint(target.Key);
            // An explicitly unmeasured/error state is a truthful original state, not an invented measurement.
            // A missing successful raw measurement remains missing and is never fabricated from copied values.
            var original = point.OriginalMeasurement;
            var captured = new ST_MANUAL_PROXY(current.OriginReferenceKey, current.Judge, current.ErrorXUm, current.ErrorYUm, current.Final, reason.Trim(), Context!.RunId, at)
            { CoordinateSignature = Context.CoordinateSignature, ReferenceMeasurementToken = current.ReferenceValueToken,
                CopiedFromKey=current.ReferenceKey,ReferenceValueKind=current.ReferenceValueKind,
                ReferenceValueSource=current.ReferenceValueSource,BasisRevisionId=current.BasisRevisionId,
                ValueKind=current.IsMeasurementReplacement?"ABSOLUTE_REVIEW":null,CalibrationBasis=current.IsMeasurementReplacement?current.CalibrationBasis:null,
                SubjectPointKey=current.IsMeasurementReplacement?target.Key:null,
                OriginalSource = point.ProxySource?.OriginalSource ?? point.OperatorResult?.OriginalSource ?? point.StateCopySource?.OriginalSource ?? point.Source };
            captured = captured with { ReferenceValueProof=ProxyValueProof(captured) };
            _points[target.Key] = point with
            {
                Judge = current.Judge, ErrorXUm = current.IsMeasurementReplacement?point.ErrorXUm:current.ErrorXUm,
                ErrorYUm = current.IsMeasurementReplacement?point.ErrorYUm:current.ErrorYUm, Source = "MANUAL",
                OriginalMeasurement = original,
                ProxySource = captured,OperatorResult=null,
                IsMeasurementOffsetInitialized=false,IsMeasurementStateCleared=false,StateCopySource=null,MeasurementStateChangedAt=at
            };
            AddHistory(point, _points[target.Key], "REFERENCE_COPY", reason.Trim());
            rows[target.Key] = new(target.Key, point.OffsetKey, point.CellNo, point.HeadNo, point.ShotNo, point.BeamNo,
                target.Base, target.Delta, current.Final, "MANUAL", current.OriginReferenceKey, reason.Trim(), "");
        }
        _generation++; Candidate = NewCandidate(rows.Values) with{IsMeasurementReplacement=preview.IsMeasurementReplacement,
            CalibrationPolicyVersion=preview.IsMeasurementReplacement?9:0}; _copyUndo = new(beforeCandidate, beforePoints, Fingerprint());
        Log("REFERENCE_COPY", $"{current.ReferenceKey} → {current.Targets.Count}점; 작업판정·편차·최종 F 복사, 원측정 보존");
        return Candidate;
    }

    /// <summary>Only stage copied measurement values; the common Cell Bulk command owns the later CSV write.</summary>
    public void ConfirmMeasurementReferenceCopy(ST_MANUAL_COPY_PREVIEW preview,IEnumerable<string> selectedKeys,string reason)
    {
        if(!preview.IsMeasurementReplacement)throw new InvalidOperationException("측정편차 복사 미리보기를 다시 확인하세요.");
        ConfirmReferenceCopy(preview,selectedKeys,reason);var undo=_copyUndo!;
        Candidate=null;_copyUndo=undo with{AfterFingerprint=Fingerprint()};
        Log("MEASUREMENT_COPY_STAGED","측정편차/판정만 작업화면에 복사 · 현재 Cell 일괄 보정 전 CSV 변경 없음");
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

    public bool CanCopyReferenceState(string referenceKey)
    {
        RequireLoaded();var point=GetPoint(referenceKey);
        if(point.IsMeasurementOffsetInitialized || point.IsMeasurementStateCleared) return true;
        return point.Judge is "UNMEASURED" or "ERROR" && !point.HasFiniteError
            && point.ProxySource is null && !point.HasAppliedOffsetEvidence;
    }

    public bool CanCopyMeasurementReferenceState(string key)
    {
        var point=GetPoint(key);
        // An absolute work value is numeric even when that target has never produced
        // its own camera residual. Keep missing-state copy separate from value copy.
        if(!point.IsMeasurementOffsetInitialized&&!point.IsMeasurementStateCleared
            &&(point.OperatorResult?.ValueKind=="ABSOLUTE_INPUT"||point.ProxySource?.ValueKind=="ABSOLUTE_REVIEW"))return false;
        if(CanCopyReferenceState(key))return true;
        return !point.HasFiniteError&&(point.HasAppliedOffsetEvidence||point.OperatorResult is not null||point.ProxySource is not null);
    }

    public ST_MANUAL_STATE_COPY_PREVIEW PreviewReferenceStateCopy(string referenceKey,IEnumerable<string> selectedKeys,bool allowIncomplete=false)
        => PreviewReferenceStateCopyCore(referenceKey,selectedKeys,allowIncomplete,false);

    public ST_MANUAL_STATE_COPY_PREVIEW PreviewMeasurementReferenceStateCopy(string referenceKey,IEnumerable<string> selectedKeys,bool allowIncomplete=false)
        => PreviewReferenceStateCopyCore(referenceKey,selectedKeys,allowIncomplete,true);

    private ST_MANUAL_STATE_COPY_PREVIEW PreviewReferenceStateCopyCore(string referenceKey,IEnumerable<string> selectedKeys,bool allowIncomplete,bool measurementReplacement)
    {
        GuardCorrection(allowIncomplete);GuardNoOffsetEdit();var reference=GetPoint(referenceKey);
        if(!(measurementReplacement?CanCopyMeasurementReferenceState(referenceKey):CanCopyReferenceState(referenceKey))) throw new InvalidOperationException("이 대표점은 빈 상태 복사 대상이 아닙니다. 현재 값의 근거를 확인하세요.");
        var selection=Select(selectedKeys,excludeMasked:false);var requested=selection.Where(key=>key!=referenceKey).ToArray();
        var excluded=requested.Where(key=>GetPoint(key).Masked).ToArray();var targets=requested.Where(key=>!GetPoint(key).Masked).ToArray();
        if(targets.Length==0) throw new InvalidOperationException("대표점 이외의 유효한 상태 복사 대상을 선택하세요.");
        var judge=reference.IsMeasurementStateCleared?reference.Judge:reference.IsMeasurementOffsetInitialized?"UNMEASURED":reference.Judge;
        if(measurementReplacement&&judge=="OK"&&!reference.HasFiniteError)judge="UNMEASURED";
        if(judge=="OK") throw new InvalidOperationException("숫자 근거가 없는 상태를 OK로 복사할 수 없습니다.");
        return new(referenceKey,ReadOnly(selection),ReadOnly(targets),ReadOnly(excluded),judge,CopyFingerprint(referenceKey,selection)){IsMeasurementReplacement=measurementReplacement};
    }

    public void ConfirmReferenceStateCopy(ST_MANUAL_STATE_COPY_PREVIEW preview,IEnumerable<string> selectedKeys,string reason)
    {
        GuardCorrection();
        if(string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("대표 상태 복사 사유를 입력하세요.");
        var current=PreviewReferenceStateCopyCore(preview.ReferenceKey,selectedKeys,false,preview.IsMeasurementReplacement);
        if(preview.Fingerprint!=current.Fingerprint || Digest(preview)!=Digest(current))
            throw new InvalidOperationException("검토 뒤 대표 상태·선택·모델 기준이 바뀌었습니다. 다시 확인하세요.");
        var beforeCandidate=Candidate;var before=current.Targets.ToDictionary(key=>key,GetPoint,StringComparer.Ordinal);
        var at=NextStateEpoch(before.Values.Append(GetPoint(current.ReferenceKey)));
        foreach(var key in current.Targets)
        {
            var point=GetPoint(key);var after=point with { Judge=current.Judge,ErrorXUm=null,ErrorYUm=null,Source="MANUAL_STATE",
                ProxySource=null,OperatorResult=null,AdoptedFileMeasurement=null,ExternalObservedAt=null,IsMeasurementStateCleared=true,
                MeasurementStateChangedAt=at,StateCopySource=new(current.ReferenceKey,current.Judge,reason.Trim(),Context!.RunId,Context.CoordinateSignature,at)
                    { RecipeId=Context.RecipeId,OriginalSource=point.StateCopySource?.OriginalSource ?? point.ProxySource?.OriginalSource ?? point.OperatorResult?.OriginalSource ?? point.Source } };
            _points[key]=after;AddHistory(point,after,"REFERENCE_STATE_COPY",reason.Trim());
        }
        var affected=current.Targets.ToHashSet(StringComparer.Ordinal);
        var remaining=Candidate is {State:not EN_MANUAL_CANDIDATE_STATE.Applied}
            ?Candidate.Rows.Where(row=>!affected.Contains(row.Key)).ToArray():[];
        _generation++;Candidate=remaining.Length>0?NewCandidate(remaining):null;
        _copyUndo=new(beforeCandidate,before,Fingerprint());
        Log("REFERENCE_STATE_COPY",$"{current.ReferenceKey} → {current.Targets.Count}점 · {current.Judge} 빈 상태만 복사 · 실제 B/원측정/파일 유지");
    }

    public bool ReleaseIncompleteOperation(string reason)
    {
        GuardIdle();RequireLoaded();GuardNoOffsetEdit();
        if(!HasIncompleteMeasurements) return false;
        if(!Context!.LineageValid || string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("현재 모델 기준과 미완료 작업 전환 사유를 확인하세요.");
        RetireIncompleteOperation("OP_RECOVERED_IDLE",reason);Operation=null;Changed();return true;
    }

    private void RetireIncompleteOperation(string auditKind,string reason)
    {
        var previous=Operation!;var at=NextStateEpoch(previous.RemainingKeys.Select(GetPoint));
        Log(auditKind,JsonSerializer.Serialize(previous));Log(auditKind+"_REASON",reason);
        foreach(var key in previous.RemainingKeys)
        {
            var point=GetPoint(key);if(point.MeasurementVersion>previous.MeasurementVersionsBefore[key]) continue;
            // A failed camera attempt is not a new result. Preserve a captured absence instead
            // of exposing an older stored B as a freshly eligible representative again.
            var pending=point.IsMeasurementStateCleared || point.OperatorResult is not null ? point with {MeasurementStateChangedAt=at} : point with {
                Judge="UNMEASURED",Source=HistoricalObservationSource(point),ErrorXUm=null,ErrorYUm=null,
                ProxySource=null,AdoptedFileMeasurement=null,ExternalObservedAt=null,IsMeasurementStateCleared=false,StateCopySource=null,
                MeasurementStateChangedAt=at };
            _points[key]=pending;AddHistory(point,pending,auditKind,reason+" · 이전 잔차 자격 해제, 원측정·실제 B 보존");
        }
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
        if(candidate.IsMeasurementReplacement&&candidate.CalibrationPolicyVersion!=9)
            errors.Add("이전 보정 정책의 후보입니다. 가공 기준을 확인한 뒤 현재 Cell 일괄 보정을 다시 요청하세요.");
        if(candidate.IsMeasurementReplacement&&candidate.IsOffsetReplacement
            ||candidate.CalibrationPolicyVersion is not 0 and not 9
            ||!candidate.IsMeasurementReplacement&&candidate.CalibrationPolicyVersion!=0
            ||candidate.ReplacementCellNo is int scope&&(scope<=0||!candidate.IsOffsetReplacement||!_points.Values.Any(p=>p.CellNo==scope)))
            errors.Add("측정 교체 정책 또는 현재 Cell 범위가 잘못됐습니다.");
        var finals = new Dictionary<string, ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (candidate.IsOffsetReplacement && (candidate.Rows.Count!=_points.Count || !candidate.Rows.Select(r=>r.Key).ToHashSet(StringComparer.Ordinal).SetEquals(_points.Keys)))
            errors.Add("통합 Offset 교체 후보는 모델 전체 위치를 포함해야 합니다.");
        var initialized=candidate.InitializedOffsetKeys.ToHashSet(StringComparer.Ordinal);
        var activeInitialized=ActiveInitializedOffsetKeys.ToHashSet(StringComparer.Ordinal);
        var initialization=candidate.InitializeAllReviewRows?_points.Values.Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal)
            :candidate.InitializationOffsetKeys.ToHashSet(StringComparer.Ordinal);
        var knownOffsetKeys=_points.Values.Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal);
        if(initialization.Any(key=>!knownOffsetKeys.Contains(key)) || initialized.Any(key=>!knownOffsetKeys.Contains(key)))
            errors.Add("초기화 범위에 현재 모델에 없는 Shot이 있습니다.");
        if(candidate.InitializationCellNos.Any(cell=>!_points.Values.Any(p=>p.CellNo==cell)
            || _points.Values.Any(p=>p.CellNo==cell && !initialization.Contains(p.OffsetKey))))
            errors.Add("Cell 초기화 범위와 실제 후보가 일치하지 않습니다.");
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
                var init=initialization.Contains(row.OffsetKey);
                var outside=candidate.ReplacementCellNo is int cell&&row.CellNo!=cell;
                var expected=outside?"OFFSET_KEEP":candidate.IsMeasurementOffsetInitialization?init?"OFFSET_INIT":"OFFSET_KEEP":"OFFSET_FILE";
                if (row.Source!=expected || string.IsNullOrWhiteSpace(row.Reason) || row.MeasurementToken.Length>0
                    || init && row.Final!=ST_MANUAL_OFFSET.Zero || candidate.IsMeasurementOffsetInitialization && !init && row.Final!=row.Base
                    || candidate.IsMeasurementOffsetInitialization && !init && initialized.Contains(row.OffsetKey)!=activeInitialized.Contains(row.OffsetKey)
                    || outside&&(row.Final!=row.Base||row.Delta!=ST_MANUAL_OFFSET.Zero||initialized.Contains(row.OffsetKey)!=activeInitialized.Contains(row.OffsetKey))
                    || initialized.Contains(row.OffsetKey) && row.Final!=ST_MANUAL_OFFSET.Zero)
                    errors.Add($"{row.Key}: 통합 Offset 교체/초기화 출처가 일치하지 않습니다.");
            }
            else if (row.Source == "MANUAL")
            {
                if (!allowManual || string.IsNullOrWhiteSpace(row.Reason)) errors.Add($"{row.Key}: 수동 후보 허용과 사유 확인이 필요합니다.");
                if(point.OperatorResult is { } manual)
                {
                    try {RequireOperatorResult(point);}catch(InvalidOperationException error){errors.Add(error.Message);}
                    ST_MANUAL_OFFSET expected=manual.FinalOffset;
                    if(candidate.IsMeasurementReplacement)try{expected=ResolveMeasurementReferenceValue(point.Key).Final;}catch(InvalidOperationException error){errors.Add(error.Message);}
                    if(row.ReferenceKey!=point.Key || !Equal(expected,row.Final) || row.Reason!=manual.Reason)
                        errors.Add($"{row.Key}: 수동 완료한 값과 후보가 일치하지 않습니다.");
                }
                else if (point.ProxySource is null || point.ProxySource.ReferenceKey != row.ReferenceKey
                    || candidate.IsMeasurementReplacement&&!Equal(point.ProxySource.FinalOffset,row.Final)
                    || !candidate.IsMeasurementReplacement&&!Equal(point.ProxySource.FinalOffset,row.Final))
                    errors.Add($"{row.Key}: 대표 복사 출처와 후보가 일치하지 않습니다.");
                if (point.ProxySource is { } proxy && (proxy.RunId != Context!.RunId || proxy.CoordinateSignature != Context.CoordinateSignature || string.IsNullOrWhiteSpace(proxy.ReferenceMeasurementToken)))
                    errors.Add($"{row.Key}: 대표 복사 후 좌표 기준/Run이 바뀌었거나 원측정 계보가 없습니다. 새 기준으로 복사하세요.");
                if (point.ProxySource is { } proof && (!KnownReferenceKind(proof.ReferenceValueKind) || !ValidProxyValueProof(proof)))
                    errors.Add($"{row.Key}: 대표 복사값의 저장된 근거가 변경되었습니다. 다시 복사하세요.");
                if(point.ProxySource is {ValueKind:"ABSOLUTE_REVIEW"} bound&&bound.SubjectPointKey!=point.Key)
                    errors.Add($"{row.Key}: 대표 복사값의 대상 위치 근거가 다릅니다.");
                if(candidate.IsMeasurementReplacement&&WasLegacyManualValueApplied(point,row.Final))
                    errors.Add($"{row.Key}: 이전 방식에서 이미 적용한 수동/복사 결과입니다. 새로 복사하거나 편차를 명시적으로 확정하세요.");
            }
            else if (row.Source == "MEASURED")
            {
                if (!point.IsDirectMeasurement || !point.HasFiniteError || row.MeasurementToken != MeasurementToken(point)) errors.Add($"{row.Key}: 직접 원측정이 바뀌었습니다.");
                if(candidate.IsMeasurementReplacement)
                    try{if(!Equal(ResolveMeasurementReferenceValue(point.Key).Final,row.Final))errors.Add($"{row.Key}: 실제 가공 기준의 추천 F와 후보가 다릅니다.");}
                    catch(InvalidOperationException error){errors.Add(error.Message);}
                else if (point.HasFiniteError && !Equal(row.Delta, new(point.ErrorXUm!.Value, point.ErrorYUm!.Value)))
                    errors.Add($"{row.Key}: 직접 측정 후보의 증감이 현재 원측정 편차와 다릅니다.");
                try { RequireMeasurementLineage(point); } catch (InvalidOperationException error) { errors.Add(error.Message); }
                if (WasConsumed(point.Key, row.MeasurementToken) && (candidate.IsMeasurementReplacement||Magnitude(row.Delta) > 1e-9))
                    errors.Add($"{row.Key}: 같은 기판/Run의 동일 원측정은 재누적할 수 없습니다. 새 가공 결과/Run을 사용하세요.");
            }
            else errors.Add($"{row.Key}: 지원하지 않는 후보 출처입니다.");
            // A scoped Init must retain legacy B outside its scope exactly. Retaining that
            // value is not a new >limit correction; ordinary candidates/file replacement still
            // use every existing delta/total/field guard.
            var retained=candidate.IsOffsetReplacement&&(candidate.IsMeasurementOffsetInitialization&&!initialization.Contains(row.OffsetKey)
                    ||candidate.ReplacementCellNo is int retainedCell&&row.CellNo!=retainedCell);
            var keptByInit=retained && row.Source=="OFFSET_KEEP"
                && row.Final==row.Base && row.Delta==ST_MANUAL_OFFSET.Zero
                && initialized.Contains(row.OffsetKey)==activeInitialized.Contains(row.OffsetKey);
            if(!keptByInit)
            {
                var limitedInput=row.Delta;
                if(candidate.IsMeasurementReplacement)
                {
                    limitedInput=ST_MANUAL_OFFSET.Zero;
                    try
                    {
                        var value=ResolveMeasurementReferenceValue(point.Key);limitedInput=RecommendedIncrement(point,value);
                        if(value.Kind=="CALIBRATED_MEASUREMENT"&&Magnitude(Subtract(QuantizeForOffsetFile(row.Final),value.CalibrationBasis!.BaseOffsetUm))>Limits!.MaxDeltaUm)
                            errors.Add($"{row.Key}: 반올림 후 추천 증분이 한계를 초과합니다.");
                    }
                    catch(InvalidOperationException error){errors.Add(error.Message);}
                }
                maxDelta = Math.Max(maxDelta, Magnitude(limitedInput)); maxTotal = Math.Max(maxTotal, Magnitude(row.Final));
                if (!(candidate.IsOffsetReplacement && (initialization.Contains(row.OffsetKey)||candidate.ReplacementCellNo.HasValue))
                    && Magnitude(limitedInput) > Limits!.MaxDeltaUm) errors.Add($"{row.Key}: {(candidate.IsMeasurementReplacement?"입력 측정편차":"이번 증감")}가 {Limits.MaxDeltaUm:G} µm 제한을 초과했습니다.");
                if (Magnitude(row.Final) > Limits!.MaxTotalUm) errors.Add($"{row.Key}: 누적 총량이 {Limits.MaxTotalUm:G} µm 제한을 초과했습니다.");
            }
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
                if(candidate.IsOffsetReplacement && candidate.IsMeasurementOffsetInitialization && !initialization.Contains(point.OffsetKey)) continue;
                if(candidate.ReplacementCellNo is int cell&&point.CellNo!=cell
                    ||candidate.IsMeasurementReplacement&&!finals.ContainsKey(point.OffsetKey))continue;
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
        var initializing=candidate.IsOffsetReplacement && candidate.IsMeasurementOffsetInitialization;
        var scoped=initializing||candidate.ReplacementCellNo.HasValue||candidate.IsMeasurementReplacement;
        ValidateStoredValues(values, candidate.Rows.Where(row=>!scoped || row.Source!="OFFSET_KEEP").Select(row => row.OffsetKey).ToHashSet(StringComparer.Ordinal),
            checkDelta: !initializing&&!candidate.IsMeasurementReplacement&&!candidate.ReplacementCellNo.HasValue,checkAllFields:!scoped,
            measurementRows:candidate.IsMeasurementReplacement?candidate.Rows:null);
        var offsets = _points.Values.GroupBy(point => point.OffsetKey).ToDictionary(group => group.Key, group => values[group.First().Key], StringComparer.Ordinal);
        var consumed = candidate.Rows.Where(row => row.Source == "MEASURED" && row.MeasurementToken.Length > 0)
            .ToDictionary(row => row.Key, row => row.MeasurementToken, StringComparer.Ordinal);
        var changedShots = candidate.Rows.Where(row=>row.Source!="OFFSET_KEEP").Select(row => row.OffsetKey).ToHashSet(StringComparer.Ordinal);
        var initializedAfter = candidate.IsOffsetReplacement
            ? candidate.InitializeAllReviewRows ? offsets.Keys.ToHashSet(StringComparer.Ordinal) : candidate.InitializedOffsetKeys.ToHashSet(StringComparer.Ordinal)
            : ActiveInitializedOffsetKeys.Where(key => !changedShots.Contains(key)).ToHashSet(StringComparer.Ordinal);
        foreach (var point in _points.Values.Where(point => changedShots.Contains(point.OffsetKey)))
        {
            // Freeze the companion raw identities before actual I/O. The digest covers this dictionary,
            // so the host can persist exactly these opaque tokens without recreating conversion rules.
            var raw = point.CorrectionMeasurement ?? point.OriginalMeasurement;
            if (raw is not null && raw.Judge is "OK" or "NG" && raw.ErrorXUm is double x && raw.ErrorYUm is double y
                && double.IsFinite(x) && double.IsFinite(y) && !string.IsNullOrWhiteSpace(raw.RawToken)
                && raw.RecipeId == Context!.RecipeId && raw.RunId == Context.RunId && raw.CoordinateSignature == Context.CoordinateSignature)
                consumed[point.Key] = MeasurementToken(point.Key,raw);
        }
        var snapshot = new ST_MANUAL_APPLY_SNAPSHOT(NewId("WRITE"), candidate.Id, Context!.RecipeId, Context.RunId, Context.RevisionId,
            Freeze(values), Freeze(offsets), ReadOnly(candidate.Rows.ToArray()))
        { ConsumedMeasurements = Freeze(consumed), IsOffsetReplacement=candidate.IsOffsetReplacement, InitializeAllReviewRows=candidate.InitializeAllReviewRows,
            IsMeasurementReplacement=candidate.IsMeasurementReplacement,ReplacementCellNo=candidate.ReplacementCellNo,
            CalibrationPolicyVersion=candidate.CalibrationPolicyVersion,
            InitializationOffsetKeys=ReadOnly(candidate.InitializationOffsetKeys.ToArray()),InitializedOffsetKeys=ReadOnly(initializedAfter.ToArray()),
            InitializationCellNos=ReadOnly(candidate.InitializationCellNos.ToArray()) };
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
            snapshot.IsOffsetReplacement && snapshot.IsMeasurementOffsetInitialization,measurementReplacement:snapshot.IsMeasurementReplacement,
            scopedReplacement:snapshot.ReplacementCellNo.HasValue);
        ValidateInitializedKeys(snapshot.InitializedOffsetKeys, _points, actual, "저장 스냅샷");
        _active.Clear(); foreach (var pair in actual) _active.Add(pair.Key, pair.Value);
        // Driving and companion tokens were reserved together. Commit never rebuilds the opaque set
        // after file I/O, and a failed write never updates this ledger.
        foreach (var pair in snapshot.ConsumedMeasurements) MarkConsumed(pair.Key, pair.Value);
        if (snapshot.IsOffsetReplacement)
        {
            var affected=snapshot.IsMeasurementOffsetInitialization
                ?snapshot.InitializeAllReviewRows?_points.Values.Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal):snapshot.InitializationOffsetKeys.ToHashSet(StringComparer.Ordinal)
                :_points.Values.Where(p=>snapshot.ReplacementCellNo is not int cell||p.CellNo==cell).Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal);
            var initialized=snapshot.InitializeAllReviewRows?_points.Values.Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal):snapshot.InitializedOffsetKeys.ToHashSet(StringComparer.Ordinal);
            foreach (var before in _points.Values.ToArray())
            {
                if(!affected.Contains(before.OffsetKey)) continue;
                var after=before with { Judge="UNMEASURED",Source=HistoricalObservationSource(before),ErrorXUm=null,ErrorYUm=null,ProxySource=null,OperatorResult=null,AdoptedFileMeasurement=null,ExternalObservedAt=null,
                    IsMeasurementOffsetInitialized=initialized.Contains(before.OffsetKey),HasAppliedOffsetEvidence=!initialized.Contains(before.OffsetKey),
                    IsMeasurementStateCleared=false,StateCopySource=null,MeasurementStateChangedAt=NextStateEpoch([before]) };
                _points[before.Key]=after;
                AddHistory(before,after,"OFFSET_REPLACED","통합 Offset 기준 변경 · 옛 잔차/대표값 재사용 해제 · 실제 원측정은 이력 보존");
            }
            _offsetEditPreviousCandidate=null;
        }
        else
        {
            // Review storage is Cell/Shot scoped. A real measurement/copy of one Beam restores
            // the shared correction state of every Beam at that Shot, including a genuine zero.
            var measuredShots=snapshot.Rows.Select(row=>row.OffsetKey).ToHashSet(StringComparer.Ordinal);
            foreach(var point in _points.Values.Where(p=>measuredShots.Contains(p.OffsetKey)).ToArray())
                _points[point.Key]=point with { IsMeasurementOffsetInitialized=false,HasAppliedOffsetEvidence=true,
                    // A shared Shot B is real, but a sibling's saved measurement is not a new
                    // response for this point. Its explicitly copied absence remains captured.
                    MeasurementStateChangedAt=NextStateEpoch([point]) };
        }
        _appliedCandidates.Add(candidate.Id); Context = Context with { RevisionId = newRevisionId };
        Candidate = candidate with
        {
            State = EN_MANUAL_CANDIDATE_STATE.Applied,
            Rows = ReadOnly(candidate.Rows.Select(row => row with { Final = actual[row.Key], Delta = Subtract(actual[row.Key], row.Base) }).ToArray())
        };
        _revisions.Add(new(newRevisionId, oldRevision, "MANUAL_INSPECTION", reason, DateTimeOffset.UtcNow, Freeze(_active))
        { InitializedOffsetKeys = ReadOnly(snapshot.InitializedOffsetKeys.ToArray()) });
        _verifiedStoredRevisions[newRevisionId]=_revisions[^1];
        AppendOffsetTimeline(_revisions[^1]);
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
        => CreateOffsetHistorySnapshot(redo:false);

    public ST_MANUAL_RESTORE_SNAPSHOT CreateRestoreNextSnapshot()
        => CreateOffsetHistorySnapshot(redo:true);

    private ST_MANUAL_RESTORE_SNAPSHOT CreateOffsetHistorySnapshot(bool redo)
    {
        GuardCorrection(); GuardNoOffsetEdit();
        var index=_offsetHistoryCursor+(redo?1:-1);
        if(index<0 || index>=_offsetTimeline.Count)
            throw new InvalidOperationException(redo?"다시 적용할 보정 기록이 없습니다.":"되돌릴 보정 기록이 없습니다. 최대 10회까지 가능합니다.");
        var target=_offsetTimeline[index];
        var quantized = target.ValuesByPointKey.ToDictionary(pair => pair.Key, pair => QuantizeForOffsetFile(pair.Value), StringComparer.Ordinal);
        ValidateStoredValues(quantized, _points.Values.Select(point => point.OffsetKey).ToHashSet(StringComparer.Ordinal), checkDelta: false,
            enforceTotal:!IsVerifiedStoredRevision(target));
        var initialized=target.InitializedOffsetKeys.ToHashSet(StringComparer.Ordinal);
        var currentInitialized=ActiveInitializedOffsetKeys.ToHashSet(StringComparer.Ordinal);
        var changedShots=_points.Values.Where(p=>!Equal(_active[p.Key],quantized[p.Key])
            || initialized.Contains(p.OffsetKey)!=currentInitialized.Contains(p.OffsetKey)).Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal);
        var consumed=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var point in _points.Values.Where(p=>changedShots.Contains(p.OffsetKey)))
        {
            var raw=point.CorrectionMeasurement??point.OriginalMeasurement;
            if(raw is not null && raw.Judge is "OK" or "NG" && raw.ErrorXUm is double x && raw.ErrorYUm is double y
                && double.IsFinite(x) && double.IsFinite(y) && !string.IsNullOrWhiteSpace(raw.RawToken)
                && raw.RecipeId==Context!.RecipeId && raw.RunId==Context.RunId && raw.CoordinateSignature==Context.CoordinateSignature)
                consumed[point.Key]=MeasurementToken(point.Key,raw);
        }
        var snapshot = new ST_MANUAL_RESTORE_SNAPSHOT(NewId("RESTORE"), Context!.RecipeId, Context.RunId, Context.RevisionId,
            target.Id, Freeze(quantized))
        { InitializedOffsetKeys = ReadOnly(target.InitializedOffsetKeys.ToArray()),TargetHistoryIndex=index,IsRedo=redo,ConsumedMeasurements=Freeze(consumed),
            InitializationOffsetKeys=ReadOnly(changedShots.Where(initialized.Contains).ToArray()) };
        _pendingWriteToken = snapshot.Token; _pendingRestore = snapshot; _pendingWriteFingerprint = Digest(snapshot);
        return snapshot;
    }

    public void CommitRestored(ST_MANUAL_RESTORE_SNAPSHOT snapshot, string newRevisionId, string reason,
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET>? actualValuesByPointKey = null)
    {
        RequireWrite(snapshot.Token, Digest(snapshot), applying: false); RequireNewRevision(newRevisionId);
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("원복 사유가 필요합니다.");
        var oldRevision = Context!.RevisionId;
        var restoreTarget=_offsetTimeline[snapshot.TargetHistoryIndex];
        var actual = VerifyWrittenValues(actualValuesByPointKey ?? snapshot.ValuesByPointKey, snapshot.ValuesByPointKey, null,
            restoringVerifiedRevision:IsVerifiedStoredRevision(restoreTarget));
        ValidateInitializedKeys(snapshot.InitializedOffsetKeys, _points, actual, "원복 스냅샷");
        var initialized = snapshot.InitializedOffsetKeys.ToHashSet(StringComparer.Ordinal);
        var currentInitialized=ActiveInitializedOffsetKeys.ToHashSet(StringComparer.Ordinal);
        var changedShots=_points.Values.Where(p=>!Equal(_active[p.Key],actual[p.Key])
            || initialized.Contains(p.OffsetKey)!=currentInitialized.Contains(p.OffsetKey)).Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal);
        // Restore the saved state rather than guessing from the numeric value. A real measured
        // zero remains measured, whereas a restored Init zero must return to the gray state.
        foreach (var before in _points.Values.ToArray())
        {
            var after = (changedShots.Contains(before.OffsetKey)
                ? before with { IsMeasurementOffsetInitialized = initialized.Contains(before.OffsetKey), Judge = "UNMEASURED", Source = HistoricalObservationSource(before), ErrorXUm = null,
                    ErrorYUm = null, ProxySource = null, OperatorResult=null,AdoptedFileMeasurement = null, ExternalObservedAt = null,
                    IsMeasurementStateCleared=false,StateCopySource=null,MeasurementStateChangedAt=NextStateEpoch([before]) }
                : before) with { HasAppliedOffsetEvidence=!initialized.Contains(before.OffsetKey) };
            if (before != after) { _points[before.Key] = after; AddHistory(before, after, "OFFSET_RESTORED", "저장된 초기화 상태 복원 · 원측정 이력 보존"); }
        }
        _active.Clear(); foreach (var pair in actual) _active.Add(pair.Key, pair.Value);
        foreach(var pair in snapshot.ConsumedMeasurements) MarkConsumed(pair.Key,pair.Value);
        Context = Context with { RevisionId = newRevisionId };
        _revisions.Add(new(newRevisionId, oldRevision, snapshot.IsRedo?"REDO":"UNDO", reason.Trim(), DateTimeOffset.UtcNow, Freeze(_active))
        { InitializedOffsetKeys = ReadOnly(snapshot.InitializedOffsetKeys.ToArray()) });
        _verifiedStoredRevisions[newRevisionId]=_revisions[^1];
        _offsetHistoryCursor=snapshot.TargetHistoryIndex;
        Candidate = null; ClearWrite(); _generation++; _copyUndo = null;
        // The same-raw application ledger is intentionally retained. Rollback is not permission to reapply old holes.
        Log("RESTORE_COMMITTED", $"{snapshot.RestoreTargetRevisionId} 값을 새 Revision {newRevisionId}로 복원했습니다.");
    }

    public void AbortRestore(ST_MANUAL_RESTORE_SNAPSHOT snapshot)
    {
        RequireWrite(snapshot.Token, Digest(snapshot), applying: false); ClearWrite(); Log("RESTORE_ABORT", "원복 파일 저장 실패/취소; 활성값 유지");
    }

    private void AppendOffsetTimeline(ST_MANUAL_REVISION revision)
    {
        if(_offsetHistoryCursor+1<_offsetTimeline.Count)
            _offsetTimeline.RemoveRange(_offsetHistoryCursor+1,_offsetTimeline.Count-_offsetHistoryCursor-1);
        _offsetTimeline.Add(revision);
        if(_offsetTimeline.Count>MaxOffsetUndoSteps+1)
            _offsetTimeline.RemoveRange(0,_offsetTimeline.Count-(MaxOffsetUndoSteps+1));
        _offsetHistoryCursor=_offsetTimeline.Count-1;
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
            if (isRetry && (kind != EN_MANUAL_OPERATION_KIND.MoveMeasure || !valid.SequenceEqual(Operation!.RemainingKeys)))
                throw new InvalidOperationException("미완료 측정의 남은 점만 측정 재실행할 수 있습니다. 자동 보정은 재개하지 않습니다.");
            if (!isRetry && kind == EN_MANUAL_OPERATION_KIND.MeasureAndCorrect)
                throw new InvalidOperationException("미완료 검사 후 자동 보정은 재개하지 않습니다. 새 수동 측정을 실행하세요.");
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
        if (HasIncompleteMeasurements && !preview.IsRetry)
        {
            RetireIncompleteOperation("OP_SUPERSEDED","새 수동 작업으로 전환");
        }
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
            OffsetTimelineRevisionIds=ReadOnly(_offsetTimeline.Select(revision=>revision.Id).ToArray()),OffsetHistoryCursor=_offsetHistoryCursor,
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
    /// A saved RUNNING operation becomes STOPPED; explicit retry or a validated new workflow may supersede it.
    /// </summary>
    public void RestoreSessionJson(
        string json, ST_MANUAL_CONTEXT expectedContext, IEnumerable<ST_MANUAL_POINT> expectedPoints,
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> actualBaseValuesByPointKey,
        ManualInspectionFieldEvaluator? fieldEvaluator,
        IReadOnlySet<string>? expectedActiveInitializedOffsetKeys = null,
        IReadOnlyDictionary<string,ST_MANUAL_CALIBRATION_BASIS>? calibrationBases = null)
    {
        var session = JsonSerializer.Deserialize<ST_MANUAL_SESSION>(json)
            ?? throw new InvalidOperationException("빈 작업공간 세션입니다.");
        RestoreSession(session, expectedContext, expectedPoints, actualBaseValuesByPointKey, fieldEvaluator,expectedActiveInitializedOffsetKeys,calibrationBases);
    }

    public void RestoreSession(
        ST_MANUAL_SESSION session, ST_MANUAL_CONTEXT expectedContext, IEnumerable<ST_MANUAL_POINT> expectedPoints,
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> actualBaseValuesByPointKey,
        ManualInspectionFieldEvaluator? fieldEvaluator,
        IReadOnlySet<string>? expectedActiveInitializedOffsetKeys = null,
        IReadOnlyDictionary<string,ST_MANUAL_CALIBRATION_BASIS>? calibrationBases = null)
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
                // Copy provenance belongs to the representative; the target may never have
                // produced its own successful raw (including an edited/imported legacy result).
                if (point.Source != "MANUAL"
                    || proxy.ReferenceKey == point.Key || !geometry.ContainsKey(proxy.ReferenceKey) || proxy.RunId != expectedContext.RunId
                    || proxy.CoordinateSignature != expectedContext.CoordinateSignature || string.IsNullOrWhiteSpace(proxy.ReferenceMeasurementToken)
                    || proxy.ReferenceJudge != point.Judge || !proxy.FinalOffset.IsFinite || string.IsNullOrWhiteSpace(proxy.Reason)
                    || proxy.ValueKind!="ABSOLUTE_REVIEW"&&(proxy.ErrorXUm != point.ErrorXUm || proxy.ErrorYUm != point.ErrorYUm)
                    || proxy.ValueKind is not null and not "ABSOLUTE_REVIEW"
                    ||proxy.ValueKind=="ABSOLUTE_REVIEW"&&proxy.SubjectPointKey!=point.Key
                    || !KnownReferenceKind(proxy.ReferenceValueKind) || !ValidProxyValueProof(proxy)
                    || proxy.CopiedFromKey is null || proxy.ReferenceValueSource is null || proxy.BasisRevisionId is null
                    || proxy.CopiedFromKey.Length > 0 && (proxy.CopiedFromKey == point.Key || !geometry.ContainsKey(proxy.CopiedFromKey)))
                    lineage = false;
            }
            // A labelled IMPORTED point is retained for viewing/reinspection and is not an eligible
            // direct anchor. Its historical frame must not invalidate unrelated new native measurements.
            if (point.StateCopySource is { } state && (state.RecipeId!=expectedContext.RecipeId || state.RunId!=expectedContext.RunId || state.CoordinateSignature!=expectedContext.CoordinateSignature
                || state.ReferenceKey==point.Key || !geometry.ContainsKey(state.ReferenceKey))) lineage=false;
            if(point.OperatorResult is { } operatorResult && !ValidOperatorResult(point,operatorResult,expectedContext)) lineage=false;
            if (point.OriginalMeasurement is { } measurement && point.Source != "IMPORTED" && point.ProxySource?.OriginalSource != "IMPORTED" && point.StateCopySource?.OriginalSource!="IMPORTED" && point.OperatorResult?.OriginalSource!="IMPORTED")
            {
                try { ValidateMeasurement(measurement, expectedContext); } catch (InvalidOperationException) { lineage = false; }
            }
            if (point.AdoptedFileMeasurement is { } adopted)
                try { ValidateMeasurement(adopted,expectedContext); } catch (InvalidOperationException) { lineage=false; }
        }
        var storedPoints = session.Points.ToDictionary(point=>point.Key,StringComparer.Ordinal);
        foreach (var point in session.Points.Where(point=>point.ProxySource is not null))
            try { ReferenceAncestors(point.Key, storedPoints); } catch (InvalidOperationException) { lineage=false; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var previousRevisionId = "";
        foreach (var revision in session.Revisions)
        {
            if (string.IsNullOrWhiteSpace(revision.Id) || !ids.Add(revision.Id) || revision.ValuesByPointKey is null
                || revision.ValuesByPointKey.Count != geometry.Count || revision.ValuesByPointKey.Any(pair => !geometry.ContainsKey(pair.Key) || !pair.Value.IsFinite))
                throw new InvalidOperationException("세션의 보정 Revision 기록이 잘못됐습니다.");
            ValidateInitializedKeys(revision.InitializedOffsetKeys, geometry, revision.ValuesByPointKey, "세션 Revision");
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
        // Older sessions know numeric zero but not its reset provenance. Never let one remove
        // a reset proven by the current native source. A new unsaved measurement may legitimately
        // clear a point's display flag while its saved Revision still carries this Shot key.
        var nativeInitialized=geometry.Values.Where(p=>p.IsMeasurementOffsetInitialized).Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal);
        if(!nativeInitialized.IsSubsetOf(active.InitializedOffsetKeys))
            throw new InvalidOperationException("이전 작업에 현재 파일의 초기화 상태가 없습니다. 현재 모델 기준으로 다시 연결하세요.");
        if(expectedActiveInitializedOffsetKeys is not null)
        {
            ValidateInitializedKeys(expectedActiveInitializedOffsetKeys.ToArray(),geometry,actualBaseValuesByPointKey,"현재 파일");
            if(!expectedActiveInitializedOffsetKeys.SetEquals(active.InitializedOffsetKeys))
                throw new InvalidOperationException("세션의 초기화 상태가 현재 활성 파일과 다릅니다. 현재 모델 기준으로 다시 연결하세요.");
        }
        if (session.Candidate is { } savedCandidate) ValidateSessionCandidate(savedCandidate, geometry, expectedContext, ids);
        var temporary = new CManualInspectionWorkspace();
        temporary.Load(expectedContext with { LineageValid = lineage }, session.Points.Select(point=>point with
            { HasAppliedOffsetEvidence=geometry[point.Key].HasAppliedOffsetEvidence }), actualBaseValuesByPointKey, session.Limits, fieldEvaluator,
            active.InitializedOffsetKeys.ToHashSet(StringComparer.Ordinal),calibrationBases);
        if (session.Operation is { } operation)
        {
            if (operation.Keys is null || operation.CompletedKeys is null || operation.RemainingKeys is null || operation.MeasurementVersionsBefore is null
                || !Enum.IsDefined(operation.Kind) || !Enum.IsDefined(operation.State))
                throw new InvalidOperationException("세션 작업 상태 또는 필수 범위가 잘못됐습니다.");
            var keySet = new HashSet<string>(operation.Keys, StringComparer.Ordinal);
            var doneSet = new HashSet<string>(operation.CompletedKeys, StringComparer.Ordinal);
            if (operation.RecipeId != expectedContext.RecipeId || operation.RunId != expectedContext.RunId
                || !ids.Contains(operation.BaseRevisionId)
                || operation.MeasurementVersionsBefore.Count != keySet.Count
                || operation.Keys.Any(key=>!operation.MeasurementVersionsBefore.TryGetValue(key,out var version)||version<0)
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
            Validation = null, Rows = ReadOnly(session.Candidate.Rows.ToArray()),
            InitializationOffsetKeys = ReadOnly(session.Candidate.InitializationOffsetKeys.ToArray()),
            InitializedOffsetKeys = ReadOnly(session.Candidate.InitializedOffsetKeys.ToArray()),
            InitializationCellNos = ReadOnly(session.Candidate.InitializationCellNos.ToArray())
        };
        if (session.OffsetEditPreviousCandidate is { } previousEdit)
        {
            ValidateSessionCandidate(previousEdit, geometry, expectedContext, ids);
            if (!temporary.HasPendingOffsetEdit || previousEdit.RecipeId!=expectedContext.RecipeId || previousEdit.RunId!=expectedContext.RunId
                || previousEdit.State!=EN_MANUAL_CANDIDATE_STATE.Applied && previousEdit.BaseRevisionId!=expectedContext.RevisionId || previousEdit.Rows is null
                || previousEdit.Rows.Any(row=>row is null || !geometry.ContainsKey(row.Key)))
                throw new InvalidOperationException("통합 Offset 편집의 이전 후보 기준이 세션과 다릅니다.");
            temporary._offsetEditPreviousCandidate=previousEdit with { Rows=ReadOnly(previousEdit.Rows.ToArray()),Validation=null,
                InitializationOffsetKeys=ReadOnly(previousEdit.InitializationOffsetKeys.ToArray()),
                InitializedOffsetKeys=ReadOnly(previousEdit.InitializedOffsetKeys.ToArray()),InitializationCellNos=ReadOnly(previousEdit.InitializationCellNos.ToArray()),
                State=previousEdit.State==EN_MANUAL_CANDIDATE_STATE.Applied?EN_MANUAL_CANDIDATE_STATE.Applied:EN_MANUAL_CANDIDATE_STATE.Draft };
        }
        temporary._revisions.Clear();
        temporary._revisions.AddRange(session.Revisions.Select(revision => revision with { ValuesByPointKey = Freeze(revision.ValuesByPointKey),
            InitializedOffsetKeys = ReadOnly(revision.InitializedOffsetKeys.ToArray()) }));
        temporary.RestoreOffsetTimeline(session);
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
            ValidateSessionCandidate(backup, geometry, expectedContext, ids, archived: true);
            temporary._candidateBackups.Add(backup with { Rows = ReadOnly(backup.Rows.ToArray()),
                InitializationOffsetKeys=ReadOnly(backup.InitializationOffsetKeys.ToArray()),InitializedOffsetKeys=ReadOnly(backup.InitializedOffsetKeys.ToArray()),
                InitializationCellNos=ReadOnly(backup.InitializationCellNos.ToArray()) });
        }
        if (Context?.RecipeId == expectedContext.RecipeId && Context.RunId == expectedContext.RunId)
        {
            // NativeLoad may already have seeded committed durable receipts. An older session is not
            // allowed to erase those facts, even when it has the same actual active offset revision.
            temporary.SeedConsumedMeasurements(_consumedMeasurementHistory.SelectMany(pair => pair.Value.Select(token => new KeyValuePair<string, string>(pair.Key, token))));
            foreach (var id in _appliedCandidates) temporary._appliedCandidates.Add(id);
        }
        var verified=Context?.RecipeId==expectedContext.RecipeId && Context.RunId==expectedContext.RunId && Context.CoordinateSignature==expectedContext.CoordinateSignature
            ?_verifiedStoredRevisions.Values.Where(proof=>temporary._revisions.Any(revision=>VerifiedRevisionMatches(revision,proof))).ToArray():[];
        _points.Clear(); foreach (var pair in temporary._points) _points.Add(pair.Key, pair.Value);
        _active.Clear(); foreach (var pair in temporary._active) _active.Add(pair.Key, pair.Value);
        _calibrationBases.Clear();foreach(var pair in temporary._calibrationBases)_calibrationBases.Add(pair.Key,pair.Value);
        _appliedMeasurements.Clear(); foreach (var pair in temporary._appliedMeasurements) _appliedMeasurements.Add(pair.Key, pair.Value);
        _consumedMeasurementHistory.Clear();
        foreach (var pair in temporary._consumedMeasurementHistory) _consumedMeasurementHistory.Add(pair.Key, new(pair.Value, StringComparer.Ordinal));
        _appliedCandidates.Clear(); foreach (var id in temporary._appliedCandidates) _appliedCandidates.Add(id);
        _revisions.Clear(); _revisions.AddRange(temporary._revisions);
        _offsetTimeline.Clear(); _offsetTimeline.AddRange(temporary._offsetTimeline); _offsetHistoryCursor=temporary._offsetHistoryCursor;
        _verifiedStoredRevisions.Clear();foreach(var proof in verified)_verifiedStoredRevisions[proof.Id]=proof;
        var activeProof=temporary._revisions.Last(revision=>revision.Id==expectedContext.RevisionId);
        _verifiedStoredRevisions[expectedContext.RevisionId]=activeProof with{ValuesByPointKey=Freeze(_active)};
        _audit.Clear(); _audit.AddRange(temporary._audit);
        _history.Clear(); _history.AddRange(temporary._history);
        _candidateBackups.Clear(); _candidateBackups.AddRange(temporary._candidateBackups);
        Context = temporary.Context; Limits = temporary.Limits; Candidate = temporary.Candidate; Operation = temporary.Operation;
        _offsetEditPreviousCandidate=temporary._offsetEditPreviousCandidate;
        _fieldEvaluator = fieldEvaluator; _copyUndo = null; ClearWrite(); _generation++;
        Log("SESSION_RESTORE", lineage ? "세션 복구: 후보 재검증 필요, 자동 실행 없음" : "세션 원측정/대표 출처 불완전: 조회 전용");
    }

    private void RestoreOffsetTimeline(ST_MANUAL_SESSION session)
    {
        var ids=session.OffsetTimelineRevisionIds;
        if(ids is null) throw new InvalidOperationException("세션 보정 되돌리기 이력 목록이 없습니다.");
        if(ids.Count==0 && session.OffsetHistoryCursor==-1)
        {
            // Pre-timeline sessions retain their original audit but cannot invent redo direction.
            _offsetTimeline.Clear();_offsetTimeline.Add(_revisions[^1]);_offsetHistoryCursor=0;return;
        }
        if(ids.Count==0 || ids.Count>MaxOffsetUndoSteps+1 || session.OffsetHistoryCursor<0 || session.OffsetHistoryCursor>=ids.Count
            || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count()!=ids.Count)
            throw new InvalidOperationException("세션 보정 되돌리기 이력 범위가 잘못됐습니다.");
        var revisionIndices=_revisions.Select((r,i)=>(r.Id,Index:i)).ToDictionary(pair=>pair.Id,pair=>pair.Index,StringComparer.Ordinal);
        var order=-1;var timeline=new List<ST_MANUAL_REVISION>();
        foreach(var id in ids)
        {
            if(!revisionIndices.TryGetValue(id,out var index) || index<=order)
                throw new InvalidOperationException("세션 보정 되돌리기 이력의 Revision 또는 순서가 잘못됐습니다.");
            order=index;timeline.Add(_revisions[index]);
        }
        var selected=timeline[session.OffsetHistoryCursor];
        var active=_revisions[^1];
        // A legacy scanner-coordinate baseline can convert to sub-file precision in DESIGN µm.
        // Undo writes its canonical six-decimal-mm representation, which is the same logical state.
        if(selected.ValuesByPointKey.Any(pair=>!Equal(pair.Value,_active[pair.Key])
                && !Equal(QuantizeForOffsetFile(pair.Value),_active[pair.Key]))
            || !selected.InitializedOffsetKeys.ToHashSet(StringComparer.Ordinal).SetEquals(active.InitializedOffsetKeys))
            throw new InvalidOperationException("세션 보정 되돌리기 위치가 현재 활성값과 다릅니다.");
        _offsetTimeline.Clear();_offsetTimeline.AddRange(timeline);_offsetHistoryCursor=session.OffsetHistoryCursor;
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

    private static bool VerifiedRevisionMatches(ST_MANUAL_REVISION revision,ST_MANUAL_REVISION proof)
        => revision.Id==proof.Id && revision.ValuesByPointKey.Count==proof.ValuesByPointKey.Count
            && revision.ValuesByPointKey.All(pair=>proof.ValuesByPointKey.TryGetValue(pair.Key,out var value)&&value==pair.Value)
            && revision.InitializedOffsetKeys.ToHashSet(StringComparer.Ordinal).SetEquals(proof.InitializedOffsetKeys);
    private bool IsVerifiedStoredRevision(ST_MANUAL_REVISION revision)
        => _verifiedStoredRevisions.TryGetValue(revision.Id,out var proof) && VerifiedRevisionMatches(revision,proof);

    private void ValidateStoredValues(IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values, IReadOnlySet<string> changedOffsets, bool checkDelta,bool checkAllFields=true,bool enforceTotal=true,
        IReadOnlyList<ST_MANUAL_CANDIDATE_ROW>? measurementRows=null)
    {
        if (_fieldEvaluator is null) throw new InvalidOperationException("실제 Scanner Field 검증기가 없어 보정 파일을 저장할 수 없습니다.");
        if(measurementRows is not null)
            foreach(var row in measurementRows)
            {
                var value=ResolveMeasurementReferenceValue(row.Key);
                if(value.Kind=="CALIBRATED_MEASUREMENT"&&Magnitude(Subtract(values[row.Key],value.CalibrationBasis!.BaseOffsetUm))>Limits!.MaxDeltaUm)
                    throw new InvalidOperationException($"{row.Key}: 실제 저장 후 추천 증분 한계를 넘습니다.");
            }
        foreach (var point in _points.Values)
        {
            var value = values[point.Key];
            if (!value.IsFinite) throw new InvalidOperationException($"{point.Key}: 양자화한 저장값이 유효하지 않습니다.");
            if(!checkAllFields && !changedOffsets.Contains(point.OffsetKey)
                && !Equal(value,_active[point.Key]) && !Equal(value,QuantizeForOffsetFile(_active[point.Key])))
                throw new InvalidOperationException($"{point.Key}: 초기화 범위 밖의 기존 보정값을 변경할 수 없습니다.");
            if (changedOffsets.Contains(point.OffsetKey))
            {
                if (enforceTotal && Magnitude(value) > Limits!.MaxTotalUm) throw new InvalidOperationException($"{point.Key}: 실제 저장 반올림 후 총량 한계를 넘습니다.");
                if (checkDelta && Magnitude(Subtract(value, _active[point.Key])) > Limits!.MaxDeltaUm)
                    throw new InvalidOperationException($"{point.Key}: 실제 저장 반올림 후 증감 한계를 넘습니다.");
            }
            if (!point.Masked && (checkAllFields || changedOffsets.Contains(point.OffsetKey)))
            {
                var field = _fieldEvaluator(point, value, Limits!);
                if (!field.Ok) throw new InvalidOperationException($"{point.Key}: 실제 저장 반올림 후 Field 검사: {field.Message}");
            }
        }
    }

    private IReadOnlyDictionary<string, ST_MANUAL_OFFSET> VerifyWrittenValues(
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> actual,
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> snapshotValues,
        IReadOnlyList<ST_MANUAL_CANDIDATE_ROW>? requestedRows, bool initialization = false,bool restoringVerifiedRevision=false,
        bool measurementReplacement=false,bool scopedReplacement=false)
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
        var checkedOffsets=(initialization||scopedReplacement) && requestedRows is not null
            ?requestedRows.Where(row=>row.Source!="OFFSET_KEEP").Select(row=>row.OffsetKey).ToHashSet(StringComparer.Ordinal)
            :requestedByOffset?.Keys.ToHashSet(StringComparer.Ordinal) ?? byOffset.Keys.ToHashSet(StringComparer.Ordinal);
        ValidateStoredValues(actual, checkedOffsets, checkDelta: requestedRows is not null && !initialization&&!measurementReplacement&&!scopedReplacement,
            checkAllFields:!initialization&&!scopedReplacement&&!measurementReplacement,enforceTotal:!restoringVerifiedRevision,
            measurementRows:measurementReplacement?requestedRows:null);
        return Freeze(actual);
    }
    private void InvalidateCandidate()
    { if (Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied }) Candidate = Candidate with { State = EN_MANUAL_CANDIDATE_STATE.Draft, Validation = null }; }
    private void GuardCorrection(bool allowIncomplete=false)
    {
        GuardIdle(); RequireLoaded();
        if (HasIncompleteMeasurements && !allowIncomplete) throw new InvalidOperationException("미완료 측정의 남은 점을 먼저 완료하거나 새 Run을 여세요.");
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
        ? MeasurementToken(point.Key,measurement) : "";
    private static bool KnownReferenceKind(string? kind) => kind is "" or "MEASURED" or "APPLIED_MEASUREMENT" or "APPLIED_OFFSET" or "OPERATOR"
        or "CALIBRATED_MEASUREMENT" or "ABSOLUTE_INPUT" or "ABSOLUTE_PROXY";

    private bool WasLegacyManualValueApplied(ST_MANUAL_POINT point,ST_MANUAL_OFFSET measurementValue)
    {
        var final=point.OperatorResult?.FinalOffset??point.ProxySource?.FinalOffset;
        var capturedAt=point.OperatorResult?.AcceptedAt??point.ProxySource?.CopiedAt;
        if(final is null||capturedAt is null||Equal(final.Value,measurementValue)||!point.HasAppliedOffsetEvidence
            ||!Equal(QuantizeForOffsetFile(final.Value),QuantizeForOffsetFile(_active[point.Key])))return false;
        foreach(var revision in _revisions.Where(r=>r.Kind=="MANUAL_INSPECTION"&&r.CreatedAt>=capturedAt.Value
            &&!r.InitializedOffsetKeys.Contains(point.OffsetKey)&&r.ValuesByPointKey.TryGetValue(point.Key,out var saved)
            &&Equal(QuantizeForOffsetFile(saved),QuantizeForOffsetFile(final.Value))))
        {
            var before=_revisions.FirstOrDefault(r=>r.Id==revision.ParentId);
            // A captured value matching today's B alone is insufficient. Require a real
            // recorded revision transition after the capture (or its applied candidate).
            if(before is not null&&(before.InitializedOffsetKeys.Contains(point.OffsetKey)
                ||!Equal(QuantizeForOffsetFile(before.ValuesByPointKey[point.Key]),QuantizeForOffsetFile(final.Value))))return true;
            if(Candidate is {State:EN_MANUAL_CANDIDATE_STATE.Applied} applied&&_appliedCandidates.Contains(applied.Id)
                &&applied.BaseRevisionId==revision.ParentId&&applied.Rows.Any(r=>r.Key==point.Key&&r.Source=="MANUAL"
                    &&Equal(QuantizeForOffsetFile(r.Final),QuantizeForOffsetFile(final.Value))))return true;
        }
        return false;
    }
    private static string HistoricalObservationSource(ST_MANUAL_POINT point) => point.Source=="IMPORTED"
        || point.ProxySource?.OriginalSource=="IMPORTED" || point.StateCopySource?.OriginalSource=="IMPORTED" || point.OperatorResult?.OriginalSource=="IMPORTED"?"IMPORTED":"KEPT";
    private static DateTimeOffset WorkingStateAt(ST_MANUAL_POINT point)
    {
        var at=point.OriginalMeasurement?.MeasuredAt ?? DateTimeOffset.MinValue;
        if(point.ExternalObservedAt is { } observed && observed>at) at=observed;
        if(point.ProxySource?.CopiedAt is { } proxy && proxy>at) at=proxy;
        if(point.StateCopySource?.CopiedAt is { } copied && copied>at) at=copied;
        if(point.OperatorResult?.AcceptedAt is { } accepted && accepted>at) at=accepted;
        if(point.MeasurementStateChangedAt is { } changed && changed>at) at=changed;
        return at;
    }
    private static DateTimeOffset NextStateEpoch(IEnumerable<ST_MANUAL_POINT> points,DateTimeOffset? minimum=null)
    {
        var at=DateTimeOffset.UtcNow;
        if(minimum is { } lower && lower>at) at=lower;
        foreach(var point in points)
        {
            var previous=WorkingStateAt(point);
            if(previous>=at) at=previous==DateTimeOffset.MaxValue?previous:previous.AddTicks(1);
        }
        return at;
    }
    private static string ProxyValueProof(ST_MANUAL_PROXY proxy) => proxy.ValueKind is null?Digest(new { proxy.ReferenceKey,proxy.ReferenceJudge,
        proxy.ErrorXUm,proxy.ErrorYUm,proxy.FinalOffset,proxy.ReferenceMeasurementToken,proxy.ReferenceValueKind,
        proxy.ReferenceValueSource,proxy.BasisRevisionId,proxy.RunId,proxy.CoordinateSignature,proxy.CopiedFromKey,proxy.CopiedAt,
        proxy.OriginalSource,proxy.Reason }):Digest(new { proxy.ReferenceKey,proxy.ReferenceJudge,
        proxy.ErrorXUm,proxy.ErrorYUm,proxy.FinalOffset,proxy.ReferenceMeasurementToken,proxy.ReferenceValueKind,
        proxy.ReferenceValueSource,proxy.BasisRevisionId,proxy.RunId,proxy.CoordinateSignature,proxy.CopiedFromKey,proxy.CopiedAt,
        proxy.OriginalSource,proxy.Reason,proxy.ValueKind,proxy.CalibrationBasis,proxy.SubjectPointKey });
    private static bool ValidProxyValueProof(ST_MANUAL_PROXY proxy) =>
        string.IsNullOrEmpty(proxy.ReferenceValueKind) && string.IsNullOrEmpty(proxy.ReferenceValueProof)
        || !string.IsNullOrWhiteSpace(proxy.ReferenceValueProof) && proxy.ReferenceValueProof==ProxyValueProof(proxy);
    private static string OperatorValueProof(ST_MANUAL_OPERATOR_RESULT value)=>Digest(value with{ValueProof=""});
    private static bool ValidOperatorResult(ST_MANUAL_POINT point,ST_MANUAL_OPERATOR_RESULT result,ST_MANUAL_CONTEXT context)
        => point.Source=="OPERATOR" && point.ProxySource is null && !point.IsMeasurementStateCleared && !point.IsMeasurementOffsetInitialized
            && result.SubjectPointKey==point.Key
            && result.Judge=="OK" && point.Judge==result.Judge && point.ErrorXUm==result.ErrorXUm && point.ErrorYUm==result.ErrorYUm
            && result.ErrorXUm.HasValue==result.ErrorYUm.HasValue
            && (!result.ErrorXUm.HasValue || double.IsFinite(result.ErrorXUm.Value)&&double.IsFinite(result.ErrorYUm!.Value))
            && result.BaseOffset.IsFinite && result.FinalOffset.IsFinite && !string.IsNullOrWhiteSpace(result.OperatorId)
            && !string.IsNullOrWhiteSpace(result.Reason) && !string.IsNullOrWhiteSpace(result.BasisRevisionId)
            && result.RecipeId==context.RecipeId && result.RunId==context.RunId && result.CoordinateSignature==context.CoordinateSignature
            && result.ValueKind is null or "ABSOLUTE_INPUT" or "RESIDUAL_APPROVAL"
            && !string.IsNullOrWhiteSpace(result.ValueProof) && result.ValueProof==OperatorValueProof(result);
    private void RequireOperatorResult(ST_MANUAL_POINT point)
    {
        if(point.OperatorResult is not { } result || !ValidOperatorResult(point,result,Context!))
            throw new InvalidOperationException($"{point.Key}: 수동 완료 값의 출처·좌표·판정·기록이 일치하지 않습니다.");
    }
    private static HashSet<string> ReferenceAncestors(string key, IReadOnlyDictionary<string,ST_MANUAL_POINT> points)
    {
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        var states = new Dictionary<string,byte>(StringComparer.Ordinal);
        var pending = new Stack<(string Key,bool Exit)>(); pending.Push((key,false));
        while (pending.TryPop(out var item))
        {
            if (item.Exit) { states[item.Key]=2;continue; }
            if (string.IsNullOrWhiteSpace(item.Key) || !points.TryGetValue(item.Key,out var point))
                throw new InvalidOperationException("대표 복사의 원본 위치가 없습니다.");
            var state=states.GetValueOrDefault(item.Key);
            if(state==1) throw new InvalidOperationException("대표 복사 출처에 순환 관계가 있습니다.");
            if(state==2) continue;
            states[item.Key]=1;ancestors.Add(item.Key);pending.Push((item.Key,true));
            if(point.ProxySource is not { } proxy) continue;
            pending.Push((proxy.ReferenceKey,false));
        }
        return ancestors;
    }
    private string MeasurementToken(string key,ST_MANUAL_MEASUREMENT measurement)
        => Digest(new { Context!.RunId, Key=key, measurement.RawToken, measurement.CoordinateSignature });
    private string SourceFingerprint() => Digest(new
    {
        Context, Limits,
        Points = _points.Values.OrderBy(point => point.Key, StringComparer.Ordinal).ToArray(),
        Active = _active.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()
    });
    private string Fingerprint() => Digest(new
    {
        Generation = _generation, Source = SourceFingerprint(),
        Candidate = Candidate is null ? null : new { Candidate.Id, Candidate.SourceFingerprint, Candidate.BaseRevisionId, Candidate.Rows,
            Candidate.IsOffsetReplacement,Candidate.IsMeasurementReplacement,Candidate.ReplacementCellNo,Candidate.CalibrationPolicyVersion,
            Candidate.InitializeAllReviewRows,Candidate.InitializationOffsetKeys,Candidate.InitializedOffsetKeys,Candidate.InitializationCellNos }
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
    private static void ValidateInitializedKeys(IReadOnlyList<string>? keys, IReadOnlyDictionary<string, ST_MANUAL_POINT> geometry,
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values, string subject)
    {
        if (keys is null || keys.Any(string.IsNullOrWhiteSpace) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
            throw new InvalidOperationException($"{subject}: 초기화 상태 배열이 없거나 중복됐습니다.");
        var known = geometry.Values.GroupBy(point => point.OffsetKey).ToDictionary(group => group.Key, StringComparer.Ordinal);
        foreach (var key in keys)
            if (!known.TryGetValue(key, out var points) || points.Any(point => !values.TryGetValue(point.Key, out var value) || value != ST_MANUAL_OFFSET.Zero))
                throw new InvalidOperationException($"{subject}: 초기화 상태는 현재 모델의 0 보정 Shot에만 사용할 수 있습니다.");
    }
    private static void ValidateSessionCandidate(ST_MANUAL_CANDIDATE candidate, IReadOnlyDictionary<string, ST_MANUAL_POINT> geometry,
        ST_MANUAL_CONTEXT context, IReadOnlySet<string> revisionIds, bool archived = false)
    {
        // JSON can explicitly supply null despite the non-nullable/default declarations. Validate
        // before cloning metadata so a malformed local session cannot enter a partly restored state.
        if (candidate.Rows is null || candidate.InitializationOffsetKeys is null || candidate.InitializedOffsetKeys is null || candidate.InitializationCellNos is null
            || !Enum.IsDefined(candidate.State) || string.IsNullOrWhiteSpace(candidate.Id) || string.IsNullOrWhiteSpace(candidate.SourceFingerprint)
            || candidate.RecipeId != context.RecipeId || candidate.RunId != context.RunId || !revisionIds.Contains(candidate.BaseRevisionId)
            || !archived && candidate.State != EN_MANUAL_CANDIDATE_STATE.Applied && candidate.BaseRevisionId != context.RevisionId)
            throw new InvalidOperationException("세션 후보의 모델·회차·Revision 또는 초기화 상태가 잘못됐습니다.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, ST_MANUAL_OFFSET>(StringComparer.Ordinal);
        foreach (var row in candidate.Rows)
        {
            if (row is null || row.Key is null || !seen.Add(row.Key) || !geometry.TryGetValue(row.Key, out var point)
                || row.OffsetKey != point.OffsetKey || row.CellNo != point.CellNo || row.HeadNo != point.HeadNo || row.ShotNo != point.ShotNo || row.BeamNo != point.BeamNo
                || !row.Base.IsFinite || !row.Delta.IsFinite || !row.Final.IsFinite || row.Source is null || row.ReferenceKey is null || row.Reason is null || row.MeasurementToken is null)
                throw new InvalidOperationException("세션 후보 행의 위치·식별자·보정값이 현재 모델과 다릅니다.");
            values.Add(row.Key, row.Final);
        }
        var known = geometry.Values.Select(point => point.OffsetKey).ToHashSet(StringComparer.Ordinal);
        var initialization = candidate.InitializeAllReviewRows ? known : candidate.InitializationOffsetKeys.ToHashSet(StringComparer.Ordinal);
        if (candidate.InitializationOffsetKeys.Any(string.IsNullOrWhiteSpace)
            || candidate.InitializationOffsetKeys.Count != candidate.InitializationOffsetKeys.Distinct(StringComparer.Ordinal).Count()
            || candidate.InitializationOffsetKeys.Any(key => !known.Contains(key))
            || candidate.InitializeAllReviewRows && candidate.InitializationOffsetKeys.Count > 0 && !initialization.SetEquals(candidate.InitializationOffsetKeys)
            || candidate.InitializationCellNos.Count != candidate.InitializationCellNos.Distinct().Count()
            || candidate.InitializationCellNos.Any(cell => cell <= 0 || !geometry.Values.Any(point => point.CellNo == cell)
                || geometry.Values.Any(point => point.CellNo == cell && !initialization.Contains(point.OffsetKey)))
            || !candidate.IsOffsetReplacement && (candidate.InitializeAllReviewRows || candidate.InitializationOffsetKeys.Count > 0
                || candidate.InitializedOffsetKeys.Count > 0 || candidate.InitializationCellNos.Count > 0)
            || candidate.IsOffsetReplacement && !seen.SetEquals(geometry.Keys))
            throw new InvalidOperationException("세션 후보의 초기화 범위가 현재 모델과 다릅니다.");
        if(candidate.IsMeasurementReplacement&&candidate.IsOffsetReplacement
            ||candidate.ReplacementCellNo is int cell&&(cell<=0||!candidate.IsOffsetReplacement||!geometry.Values.Any(p=>p.CellNo==cell)))
            throw new InvalidOperationException("세션 후보의 측정 교체 정책 또는 Cell 범위가 잘못됐습니다.");
        ValidateInitializedKeys(candidate.InitializedOffsetKeys, geometry, values, "세션 후보");
        if (initialization.Any(key => geometry.Values.Where(point => point.OffsetKey == key).Any(point => !values.TryGetValue(point.Key, out var value) || value != ST_MANUAL_OFFSET.Zero)))
            throw new InvalidOperationException("세션 초기화 후보는 해당 Shot을 모두 0으로 설정해야 합니다.");
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
        if(point.IsMeasurementStateCleared)
        {
            if(point.Judge is not ("UNMEASURED" or "ERROR" or "NG") || point.ErrorXUm is not null || point.ErrorYUm is not null
                || point.ProxySource is not null || point.AdoptedFileMeasurement is not null || point.Source!="MANUAL_STATE"
                || point.StateCopySource is not { } state || state.Judge!=point.Judge || string.IsNullOrWhiteSpace(state.ReferenceKey)
                || string.IsNullOrWhiteSpace(state.Reason)) throw new InvalidOperationException($"{point.Key}: 빈 상태 복사 근거가 일치하지 않습니다.");
        }
        else if(point.StateCopySource is not null) throw new InvalidOperationException($"{point.Key}: 상태 복사 표시와 근거가 다릅니다.");
        var capturedFinal=point.OperatorResult is { } op && op.FinalOffset.IsFinite && op.ValueProof==OperatorValueProof(op)
            || point.ProxySource is { } proxy && proxy.FinalOffset.IsFinite && KnownReferenceKind(proxy.ReferenceValueKind) && ValidProxyValueProof(proxy);
        if (point.Judge is "OK" or "NG" && !point.HasFiniteError && !point.IsMeasurementStateCleared && !capturedFinal) throw new InvalidOperationException($"{point.Key}: OK/NG의 편차가 유효하지 않습니다.");
        if(point.Source=="OPERATOR" && point.OperatorResult is null || point.OperatorResult is not null && point.Source!="OPERATOR")
            throw new InvalidOperationException($"{point.Key}: 수동 완료 출처와 기록이 다릅니다.");
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
            new(before.Judge, before.ErrorXUm, before.ErrorYUm, before.Source, before.ProxySource){OperatorResult=before.OperatorResult},
            new(after.Judge, after.ErrorXUm, after.ErrorYUm, after.Source, after.ProxySource){OperatorResult=after.OperatorResult},
            before.OriginalMeasurement, after.OriginalMeasurement, reason));
    private sealed record CopyUndo(ST_MANUAL_CANDIDATE? Candidate, IReadOnlyDictionary<string, ST_MANUAL_POINT> Points, string AfterFingerprint);
}
