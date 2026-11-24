using System.Globalization;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Views;
using Microsoft.Win32;

namespace Drilling.UI.Menu.Menus;

/// <summary>
/// Owns only the new Manual Inspection workspace. All engine mutations run on the WPF dispatcher;
/// device callbacks are marshalled before publishing a snapshot. STOP cancels immediately, but the
/// busy/navigation guard remains held until the owning move/IOF cleanup has actually returned.
/// </summary>
public sealed class CManualInspectionController : IManualInspectionController
{
    private readonly IManualInspectionBackend _production;
    private IManualInspectionBackend _backend;
    private CManualInspectionWorkspace _engine = new();
    private WorkspaceBookmark? _previousWorkspace;
    private bool _loadFailureRecovered;
    private string? _offsetDraftRecoveryBlockedPath;
    private readonly SemaphoreSlim _command = new(1, 1);
    private readonly Action<string> _report;
    private readonly TimeProvider _presentationTime;
    private long _lastProgressTimestamp;
    private bool _hasProgressTimestamp, _pendingPointPublication;
    private CManualInspectionSource? _source;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _idle;
    private bool _busy, _allowManual, _waitForControlStop;
    private long _version;
    // Presentation receipt only: never restored from an old file/session and never an operation token.
    private long _measurementSequence;
    private string? _lastMeasuredPointKey;
    private string _lastMeasurementText="";
    private bool _isMeasurementInProgress;
    private string? _activeMeasurementPointKey;
    private IReadOnlyList<string> _activeMeasurementPointKeys = [];
    private string _status = "현재 Recipe 또는 측정 결과를 불러오세요.", _message = "", _persistence = "아직 저장된 작업 없음";
    private double _stageX = double.NaN, _stageY = double.NaN;
    private string _levelMetric = "MAX_AXIS";
    private double[] _levelBounds = [5, 10, 20];
    private string[] _levelNames = ["정상", "관찰", "주의", "점검"];
    private DateTimeOffset _lastCheckpoint;
    private string _simulationScenario = "normal";
    private string _stopRequestError = "";
    private bool _syncPending, _syncQueued, _explicitResultView;
    private string _synchronizationText = "같은 Recipe·기판의 최근 측정과 활성 보정을 자동 확인합니다.";
    private CManualInspectionUpdateMonitor? _updateMonitor;
    private CManualInspectionSavedOffsetSnapshot? _finalOffsets;
    private IReadOnlyDictionary<string,CManualInspectionUiOffset> _finalUiPoints=new Dictionary<string,CManualInspectionUiOffset>(StringComparer.Ordinal);
    private IReadOnlySet<string> _finalInitializedPointKeys=FrozenSet<string>.Empty;
    private readonly SemaphoreSlim _finalOffsetReadGate=new(1,1);
    private bool _finalOffsetReadQueued,_finalOffsetFollowupScheduled;
    private string _finalOffsetStatus="최종 저장 보정 확인 전";
    private ST_MANUAL_CONTEXT? _lastSavedContext;
    private string _lastSaveVerificationCode="NONE",_lastSaveVerificationText="";
    private Dictionary<string, CManualInspectionImportedResult> _displayedImports = new(StringComparer.Ordinal);
    public CManualInspectionUiSnapshot Snapshot { get; private set; } = new();
    public event EventHandler? Changed;
    public bool IsBusy => _busy || _engine.IsBusy;
    public Func<bool, string?, string?> ChooseFile { get; set; } = DefaultChooseFile;
    public Action<string, string> ShowDetails { get; set; } = (title, text) =>
        Popup.CRecipeConfirmDialog.ShowWarning(title, text);

    public CManualInspectionController(IManualInspectionBackend backend, Action<string>? report = null,
        TimeProvider? presentationTime = null)
    { _production = _backend = backend; _report = report ?? (_ => { }); _presentationTime = presentationTime ?? TimeProvider.System; Publish(); }

    public async Task EnsureLoaded(CancellationToken token = default)
    {
        if (_source is not null) { await SynchronizeExternalAsync(token); return; }
        if (IsBusy) return;
        await ExecuteAsync(EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe,
            new() { ExpectedVersion = Snapshot.Version }, token);
    }

    /// <summary>Application-lifetime notifications; they never start a device operation.</summary>
    public void EnableAutomaticUpdates()
    {
        if (_updateMonitor is not null || _production.WatchRoots.Count == 0) return;
        try { _updateMonitor = new CManualInspectionUpdateMonitor(_production.WatchRoots, () =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted) return;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
            {
                try { await SynchronizeExternalAsync(); }
                catch (Exception e) { _synchronizationText = "자동 갱신 대기 · " + e.Message; Publish(reusePoints: true); }
            }));
        }); }
        catch (Exception e) { _synchronizationText = "자동 갱신 연결 확인 필요 · 새로고침 사용 가능 · " + e.Message; Publish(reusePoints: true); }
    }

    public void DisableAutomaticUpdates() { _updateMonitor?.Dispose(); _updateMonitor = null; }

    /// <summary>Legacy Review completion notification. Reads saved results/offsets; never starts motion or applies a candidate.</summary>
    public Task NotifyExternalReviewChangedAsync(CancellationToken cancellationToken=default)
        => SynchronizeExternalAsync(cancellationToken);

    private static bool SameSavedFrame(ST_MANUAL_CONTEXT context,CManualInspectionSavedOffsetSnapshot saved)
        => context.RecipeId==saved.RecipeId && context.RunId==saved.RunId && context.CoordinateSignature==saved.CoordinateSignature;

    private void AcceptFinalOffsets(CManualInspectionSavedOffsetSnapshot? saved)
    {
        if(saved is null) { _finalOffsets=null;_finalOffsetStatus="이 연결은 별도 최종 저장 보정 조회를 제공하지 않습니다.";return; }
        if(_source is null || !SameSavedFrame(_source.Context,saved) || saved.ValuesByPointKey.Any(pair=>!pair.Value.IsFinite)
            || _source.Points.Any(point=>!saved.ValuesByPointKey.ContainsKey(point.Key)))
            throw new InvalidDataException("최종 저장 보정의 모델·회차·좌표·전체 위치가 현재 작업과 다릅니다.");
        _finalOffsets=saved with
        {
            ValuesByPointKey=new ReadOnlyDictionary<string,ST_MANUAL_OFFSET>(saved.ValuesByPointKey.ToDictionary(pair=>pair.Key,pair=>pair.Value,StringComparer.Ordinal)),
            InitializedOffsetKeys=saved.InitializedOffsetKeys.ToFrozenSet(StringComparer.Ordinal),AppliedPointKeys=saved.AppliedPointKeys.ToFrozenSet(StringComparer.Ordinal)
        };
        _finalUiPoints=new ReadOnlyDictionary<string,CManualInspectionUiOffset>(saved.ValuesByPointKey.ToDictionary(pair=>pair.Key,
            pair=>new CManualInspectionUiOffset(pair.Value.XUm,pair.Value.YUm),StringComparer.Ordinal));
        var pointOffsetKeys=_source.Points.ToDictionary(point=>point.Key,point=>point.OffsetKey,StringComparer.Ordinal);
        _finalInitializedPointKeys=saved.ValuesByPointKey.Keys.Where(key=>saved.InitializedOffsetKeys.Contains(pointOffsetKeys.GetValueOrDefault(key)
            ?? string.Join("/",key.Split('/').Where(part=>part.StartsWith('C') || part.StartsWith('S'))))).ToFrozenSet(StringComparer.Ordinal);
        _finalOffsetStatus=string.IsNullOrWhiteSpace(saved.FilePath)?"저장된 측정 보정 파일 없음 · 미적용 위치는 값 없음"
            : $"최종 저장 {saved.SavedAt?.ToLocalTime():MM-dd HH:mm:ss} · {saved.RowCount:N0}행 · Init {saved.InitializedRowCount:N0}행 · 미저장 작업 제외";
    }

    private async Task RefreshFinalOffsets(CancellationToken token,bool waitForFresh=false)
    {
        if(_source is null) return;
        if(!await _finalOffsetReadGate.WaitAsync(waitForFresh?Timeout.Infinite:0,token))
        { _finalOffsetReadQueued=true;return; }
        var publishCancelledRead=false;
        try
        {
            // At most the original read and one coalesced latest read own the gate. Further
            // notifications yield to the dispatcher, so continuous writes cannot spin this call.
            for(var attempt=0;attempt<2;attempt++)
            {
                _finalOffsetReadQueued=false;
                var source=_source;var backend=_backend;
                bool CurrentFrame()=>_source is not null && backend==_backend && source.Context.RecipeId==_source.Context.RecipeId
                    && source.Context.RunId==_source.Context.RunId && source.Context.CoordinateSignature==_source.Context.CoordinateSignature;
                try
                {
                    var saved=await backend.ReadSavedOffsetSnapshotAsync(source,token);
                    if(!CurrentFrame()) { _finalOffsetReadQueued=false;return; }
                    AcceptFinalOffsets(saved);
                }
                catch(OperationCanceledException)
                {
                    _finalOffsetReadQueued=false;
                    if(CurrentFrame()) { _finalOffsets=null;_finalOffsetStatus="최종 저장 보정 확인 중지 · 다시 확인하세요.";publishCancelledRead=true; }
                    throw;
                }
                catch(Exception error)
                {
                    if(!CurrentFrame()) { _finalOffsetReadQueued=false;return; }
                    _finalOffsets=null;_finalOffsetStatus="최종 저장 보정 확인 실패 · "+error.Message;
                }
                if(!_finalOffsetReadQueued) break;
            }
        }
        finally
        {
            try { VerifyLastSavedOffsets(); }
            finally
            {
                _finalOffsetReadGate.Release();
                if(publishCancelledRead) Publish(reusePoints:true);
                else ScheduleFinalOffsetFollowup();
            }
        }
    }

    private bool HasCurrentSaveConfirmation => _source is not null && _lastSavedContext is { } saved
        && saved.RecipeId==_source.Context.RecipeId && saved.RunId==_source.Context.RunId
        && saved.CoordinateSignature==_source.Context.CoordinateSignature;
    private void BeginSaveConfirmation()
    {
        _lastSavedContext=_engine.Context;
        _lastSaveVerificationCode="PENDING";_lastSaveVerificationText="CSV 저장됨 · 재확인 중";
    }
    private void VerifyLastSavedOffsets()
    {
        if(!HasCurrentSaveConfirmation) return;
        if(_finalOffsets is not { } final)
        {
            _lastSaveVerificationCode=_finalOffsetStatus.StartsWith("이 연결은",StringComparison.Ordinal)?"UNSUPPORTED":"READ_FAILED";
            _lastSaveVerificationText=_lastSaveVerificationCode=="UNSUPPORTED"?"CSV 저장됨 · 조회 미지원":"CSV 저장됨 · 재확인 실패";
            return;
        }
        if(final.RevisionId!=_lastSavedContext!.RevisionId)
        { _lastSaveVerificationCode="SUPERSEDED";_lastSaveVerificationText="CSV 저장됨 · 최신값 변경";return; }
        var values=_engine.ActiveOffsets;
        var offsets=_engine.Points.Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal);
        var initialized=_engine.ActiveInitializedOffsetKeys.ToHashSet(StringComparer.Ordinal);
        var same=_engine.Context?.RevisionId==_lastSavedContext.RevisionId
            && values.All(p=>final.ValuesByPointKey.TryGetValue(p.Key,out var actual)
                && Math.Abs(p.Value.XUm-actual.XUm)<=1e-7 && Math.Abs(p.Value.YUm-actual.YUm)<=1e-7)
            && initialized.SetEquals(final.InitializedOffsetKeys.Where(offsets.Contains));
        _lastSaveVerificationCode=same?"VERIFIED":"MISMATCH";
        _lastSaveVerificationText=same?"저장·CSV 확인 완료":"CSV 저장됨 · 내용 불일치";
    }
    private string SaveConfirmationSuffix => HasCurrentSaveConfirmation ? " · "+_lastSaveVerificationText : "";

    private void ScheduleFinalOffsetFollowup()
    {
        if(!_finalOffsetReadQueued || _finalOffsetFollowupScheduled) return;
        var dispatcher=Application.Current?.Dispatcher;
        if(dispatcher is null || dispatcher.HasShutdownStarted) { _finalOffsetReadQueued=false;return; }
        _finalOffsetFollowupScheduled=true;
        dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(async()=>
        {
            _finalOffsetFollowupScheduled=false;
            if(!_finalOffsetReadQueued) return;
            try { await RefreshFinalOffsets(CancellationToken.None);Publish(reusePoints:true); }
            catch(OperationCanceledException) { _finalOffsetReadQueued=false; }
        }));
    }

    /// <summary>
    /// Read-only refresh requested by another menu/file save or by tab activation. Do not reenter the
    /// mutable production adapter during motion/write, or replace pending work without confirmation.
    /// </summary>
    public async Task SynchronizeExternalAsync(CancellationToken cancellationToken = default)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        { await dispatcher.InvokeAsync(() => SynchronizeExternalAsync(cancellationToken)).Task.Unwrap(); return; }
        if (_source is null || _backend != _production) return;
        // The saved map is a separate read-only projection, so drafts, incomplete runs and an
        // explicitly opened result keep their raw/candidate state while still seeing the latest file.
        await RefreshFinalOffsets(cancellationToken);
        if (_explicitResultView)
        { _synchronizationText = "선택한 파일 작업 유지 · 자동으로 다른 자료로 전환하지 않습니다. 원래 작업은 파일 메뉴에서 돌아가세요."; Publish(reusePoints: true); return; }
        _syncQueued = true;
        if (IsBusy || _engine.HasIncompleteMeasurements || _engine.Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied })
        {
            _syncPending = true;
            _synchronizationText = IsBusy ? "새 결과 확인 대기 · 현재 동작 정리 후 갱신합니다."
                : _engine.HasIncompleteMeasurements ? "새 결과 확인 대기 · 미완료 측정은 보존했습니다. 잔여 측정을 먼저 완료하세요."
                : "새 결과 확인 대기 · 미적용 후보를 보존했습니다. 결과 새로고침에서 확인 후 갱신하세요.";
            Publish(reusePoints: true); return;
        }
        if (!await _command.WaitAsync(0, cancellationToken)) { _syncPending = true; Publish(reusePoints: true); return; }
        _busy = true; _syncQueued = false; _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopRequestError = "";
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await LoadSource(null, _cancellation.Token, automatic: true);
            _syncPending = false;
            _synchronizationText = "최근 측정·활성 보정 동기화 " + DateTimeOffset.Now.ToString("HH:mm:ss")
                + " · 측정 없는 위치도 실제 적용값으로 Level 표시";
        }
        catch (OperationCanceledException)
        {
            if (_source is not null) _source = _source with { ReadOnly = true };
            _syncPending = true; _synchronizationText = "동기화 중지 · 다음 동작 때 현재 모델 기준을 다시 연결합니다.";
            throw;
        }
        catch (Exception e)
        {
            if (_source is not null) _source = _source with { ReadOnly = true };
            _syncPending = true; _synchronizationText = "동기화 확인 필요 · " + e.Message;
        }
        finally
        {
            _busy = false; _cancellation?.Dispose(); _cancellation = null;
            try { SaveLocalSession(); } catch (Exception e) { _persistence = "자동 보관 실패 · " + e.Message; }
            try { Publish(); }
            finally { _idle?.TrySetResult(); _command.Release(); }
            ScheduleQueuedSynchronization();
        }
    }

    private void ScheduleQueuedSynchronization()
    {
        if (!_syncQueued || _engine.HasIncompleteMeasurements
            || _engine.Candidate is { State: not EN_MANUAL_CANDIDATE_STATE.Applied }) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            try { await SynchronizeExternalAsync(); }
            catch (OperationCanceledException) { }
        }));
    }

    public async Task ExecuteAsync(EN_MANUAL_INSPECTION_UI_ACTION action, CManualInspectionUiRequest request,
        CancellationToken cancellationToken = default)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        { await dispatcher.InvokeAsync(() => ExecuteAsync(action, request, cancellationToken)).Task.Unwrap(); return; }
        if (action == EN_MANUAL_INSPECTION_UI_ACTION.Stop) { RequestStop(); return; }
        if (action == EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect)
            throw new InvalidOperationException("이동 후 측정 및 보정은 제공하지 않습니다. 이동 후 측정한 뒤 일괄 보정을 사용하세요.");
        if (request.ExpectedVersion != Snapshot.Version) throw new InvalidOperationException("선택/확인 뒤 작업 기준이 바뀌었습니다. 현재 화면에서 다시 요청하세요.");
        if (!await _command.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("다른 작업이 진행 중입니다.");
        _busy = true; _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopRequestError = "";
        // A modal information/export dialog owns the UI command gate, but no device or offset write.
        // Heavy-alarm shutdown must wait for actual control/write cleanup, not a user closing a dialog.
        _waitForControlStop = action is EN_MANUAL_INSPECTION_UI_ACTION.Move
            or EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure or EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect
            or EN_MANUAL_INSPECTION_UI_ACTION.Measure or EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining
            or EN_MANUAL_INSPECTION_UI_ACTION.Apply or EN_MANUAL_INSPECTION_UI_ACTION.Rollback
            or EN_MANUAL_INSPECTION_UI_ACTION.BulkApply or EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets
            or EN_MANUAL_INSPECTION_UI_ACTION.CopyReference
            or EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets or EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cancellation.Token;
        _loadFailureRecovered=false;
        try
        {
            if (_engine.HasPendingOffsetEdit && action is not (EN_MANUAL_INSPECTION_UI_ACTION.Validate or EN_MANUAL_INSPECTION_UI_ACTION.Apply
                or EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit or EN_MANUAL_INSPECTION_UI_ACTION.UpdateLimits
                or EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels or EN_MANUAL_INSPECTION_UI_ACTION.SetAllowManual
                or EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv or EN_MANUAL_INSPECTION_UI_ACTION.ExportOffsetBundle
                or EN_MANUAL_INSPECTION_UI_ACTION.ExportSession or EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint
                or EN_MANUAL_INSPECTION_UI_ACTION.ExportFinalOffsets or EN_MANUAL_INSPECTION_UI_ACTION.ExportCurrentOffsetCsv))
            {
                _loadFailureRecovered=true; // No source load was attempted; retain a writable draft.
                throw new InvalidOperationException("통합 Offset은 아직 저장 전입니다. 모델 Offset 저장 또는 편집 취소 후 이동·측정·파일 전환하세요.");
            }
            if (action is not (EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint or EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv or EN_MANUAL_INSPECTION_UI_ACTION.ExportSession))
                Publish();
            switch (action)
            {
                case EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe:
                    if (_previousWorkspace is not null) await ReturnToPreviousWorkspace(token);
                    else await LoadSource(null, token);
                    break;
                case EN_MANUAL_INSPECTION_UI_ACTION.Refresh:
                    await LoadSource(_explicitResultView ? _source?.SourcePath : null, token, preserveFileWork:_explicitResultView); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace:
                    await ReturnToPreviousWorkspace(token); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile:
                    var result = request.ResultPath ?? ChooseFile(false, "csv");
                    if (result is not null) await LoadSource(result, token); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.NewSimulation:
                    if (!_production.IsSimulation) throw new InvalidOperationException("실제 설비 모드에서는 SIM 예제를 열 수 없습니다.");
                    if (_engine.HasIncompleteMeasurements && _backend == _production)
                        throw new InvalidOperationException("실제 작업의 잔여 측정을 예제로 덮을 수 없습니다.");
                    _simulationScenario = request.Scenario;
                    _backend = new CManualInspectionReplayBackend(_simulationScenario, _production.StateDirectory);
                    await LoadSource(null, token, keepBackend: true); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.ImportSession: await ImportSession(token); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.ExportSession: await ExportSession(token); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv: await ExportCsv(token); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets:
                    await Current(token);
                    InitializeOffsets(request);
                    var initialCheck=_engine.ValidateCandidate(false);
                    if(!initialCheck.Ok)
                    {
                        _engine.CancelOffsetReplacement();
                        throw new InvalidOperationException("Offset Init을 적용하지 않았습니다. 기존 작업으로 돌아갔으며 이동·측정할 수 있습니다.\n"+string.Join("\n",initialCheck.Errors));
                    }
                    _status=initialCheck.Ok ? "Offset Init · 화면의 측정 기반 통합 Offset은 0입니다. 모델 Offset 저장 전까지 실제 활성값·기존 파일은 유지됩니다."
                        : "Offset Init 작업값 0 · 저장 검증 확인 필요: "+string.Join("\n",initialCheck.Errors);
                    break;
                case EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit:
                    _engine.CancelOffsetReplacement();
                    _status="통합 Offset 편집을 취소했습니다. 실제 활성 보정과 원측정은 그대로입니다.";break;
                case EN_MANUAL_INSPECTION_UI_ACTION.ExportOffsetBundle:
                    await ExportOffsetBundle(request.ResultPath,token);break;
                case EN_MANUAL_INSPECTION_UI_ACTION.ExportFinalOffsets:
                    await ExportFinalOffsets(request.ResultPath,token);break;
                case EN_MANUAL_INSPECTION_UI_ACTION.ExportCurrentOffsetCsv:
                    await ExportCurrentOffsetCsv(request.ResultPath,token);break;
                case EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle:
                    await Current(token);
                    var importPath=request.ResultPath??ChooseFile(false,"offset");
                    if (importPath is not null)
                    {
                        var absolute=await _backend.ImportOffsetBundleAsync(_source!,importPath,token);
                        _engine.BuildOffsetReplacementCandidate(absolute,"통합 Offset 파일 · 절대 누적값 교체 · "+Path.GetFileName(importPath),false,
                            _backend.ImportedInitializedOffsetKeys);
                        var importedCheck=_engine.ValidateCandidate(false);
                        _status=importedCheck.Ok ? "통합 Offset 파일을 저장 전 작업값으로 열었습니다. 기존값에 다시 더하지 않습니다. 모델 Offset 저장으로 반영하세요."
                            : "통합 Offset 파일 작업값 · 저장 검증 확인 필요: "+string.Join("\n",importedCheck.Errors);
                        if(!string.IsNullOrWhiteSpace(_backend.ImportedOffsetDescription)) _status+=" · "+_backend.ImportedOffsetDescription;
                    }
                    break;
                case EN_MANUAL_INSPECTION_UI_ACTION.Calculate:
                    await Current(token); _engine.BuildDirectCandidate(request.Keys); _status = "선택점의 보정 후보를 계산했습니다. 검증 후 적용하세요."; break;
                case EN_MANUAL_INSPECTION_UI_ACTION.Validate:
                    await Current(token); _allowManual = request.AllowManual;
                    var check = _engine.ValidateCandidate(_allowManual);
                    _status = check.Ok ? "현재 후보 검증 통과 · 아직 활성 보정에 저장하지 않았습니다." : string.Join("\n", check.Errors); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.Apply:
                    _allowManual = request.AllowManual;
                    if (_engine.HasPendingOffsetEdit)
                    {
                        await Current(token);
                        var editedCheck = _engine.ValidateCandidate(_allowManual);
                        if (!editedCheck.Ok) throw new InvalidOperationException("Offset을 저장하지 않았습니다.\n" + string.Join("\n", editedCheck.Errors));
                        await Apply(token, sourceVerified: true);
                    }
                    else await Apply(token);
                    break;
                case EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets:
                    await ApplySelectedOffsets(request.Keys, token); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.BulkApply: await BulkApply(request.CurrentCellNo,token); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.Rollback:
                case EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets: await Restore(token,false); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.RedoOffsets: await Restore(token,true); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.CopyReference:
                    await CopyReference(request,token);break;
                case EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement:
                    await ForceMeasurement(request,token);break;
                case EN_MANUAL_INSPECTION_UI_ACTION.UndoCopy:
                    await Current(token); _engine.UndoReferenceCopy(); _status = "직전 대표 복사만 취소했습니다."; break;
                case EN_MANUAL_INSPECTION_UI_ACTION.UpdateLimits:
                    await Current(token);
                    _engine.SetLimits(new(request.DeltaLimitUm ?? throw new InvalidOperationException("Δ 한계를 입력하세요."),
                        request.TotalLimitUm ?? throw new InvalidOperationException("총량 한계를 입력하세요."),
                        request.ScannerFieldMm ?? throw new InvalidOperationException("Field를 입력하세요.")));
                    _status = "검증 한계를 저장했습니다. 후보를 다시 검증하세요."; break;
                case EN_MANUAL_INSPECTION_UI_ACTION.SetAllowManual:
                    _allowManual = request.AllowManual;
                    if (_engine.Candidate is not null) _engine.ValidateCandidate(_allowManual);
                    _status = _allowManual ? "사유가 기록된 MANUAL 후보도 검증합니다." : "MANUAL 후보의 활성 적용을 허용하지 않습니다."; break;
                case EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels: UpdateLevels(request); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint: ShowPoint(request.FocusKey ?? request.Keys.FirstOrDefault()); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.Move:
                case EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure:
                case EN_MANUAL_INSPECTION_UI_ACTION.Measure:
                case EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining:
                    await Run(action, request, token); break;
                default: throw new InvalidOperationException("지원하지 않는 Manual Inspection 동작입니다.");
            }
            _message = _status;
        }
        catch (OperationCanceledException)
        { _status = "중지했습니다. 미완료 위치는 잔여 재실행으로 측정하세요. 자동 보정은 재개하지 않습니다."
                + (_stopRequestError.Length > 0 ? "\nSTOP 요청 확인 필요 · " + _stopRequestError : ""); _message = _status; }
        catch (Exception e)
        {
            if (action is EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe or EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile
                or EN_MANUAL_INSPECTION_UI_ACTION.Refresh or EN_MANUAL_INSPECTION_UI_ACTION.ImportSession)
                if (_source is not null && !_loadFailureRecovered) _source = _source with { ReadOnly = true };
            _status = e.Message; _message = e.Message; throw;
        }
        finally
        {
            _busy = false; _waitForControlStop = false; _cancellation?.Dispose(); _cancellation = null;
            try { SaveLocalSession(); } catch (Exception e) { _persistence = "자동 보관 실패 · " + e.Message; }
            try
            {
                Publish();
                try { _report(_status); }
                catch (Exception error) { TraceObserverFailure("status reporter", error); }
            }
            finally { _idle?.TrySetResult(); _command.Release(); }
            if (_syncQueued && !_engine.HasIncompleteMeasurements
                && _engine.Candidate is not { State: not EN_MANUAL_CANDIDATE_STATE.Applied })
                await SynchronizeExternalAsync();
        }
    }

    private async Task LoadSource(string? path, CancellationToken token, bool keepBackend = false, bool automatic = false, bool preserveFileWork = false)
    {
        _loadFailureRecovered = false;
        if (path is not null) { await OpenResultWorkspace(path, token,preserveFileWork); await RefreshFinalOffsets(token,true); return; }
        var previous = _source is not null && !IsOffsetDraftRecoveryBlocked(SessionPath()) ? _engine.CaptureSession() : null;
        if (previous is not null) SaveLocalSession();
        if (!keepBackend) _backend = _production;
        _status = "실제 Recipe·Setting·보정·측정 위치를 확인하는 중…"; Publish();
        var next = await _backend.LoadAsync(path, token);
        if (automatic && _source is not null && !_source.ReadOnly
            && next.ObservationFingerprint.Length > 0 && next.ObservationFingerprint == _source.ObservationFingerprint
            && SameFrame(_source.Context, next.Context))
        { _source = next; ReconcileDisplayedImports(next); return; }
        var sameFrame = previous is not null && SameFrame(previous.Context, next.Context);
        if(!automatic || !sameFrame) ClearMeasurementReceipt();
        _engine.Load(next.Context, next.Points, next.Values, next.Limits, next.FieldEvaluator,next.StoredInitializedOffsetKeys);
        _source = next; _allowManual = false; _explicitResultView = path is not null;
        if(_finalOffsets is not null && !SameSavedFrame(next.Context,_finalOffsets)) _finalOffsets=null;
        _stageX = _stageY = double.NaN;
        var saved = SessionPath();
        if (path is null && (sameFrame || System.IO.File.Exists(saved)))
        {
            var backingUpOffsetDraft = false;
            var deferredOffsetDraft = false;
            var deferredIncomplete = false;
            try
            {
                if (sameFrame)
                    _engine.RestoreSession(ExpandSession(previous!, next), next.Context, next.Points, next.Values, next.FieldEvaluator,next.StoredInitializedOffsetKeys);
                else
                {
                    var bytes = await System.IO.File.ReadAllBytesAsync(saved, token);
                    using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    var bundle = JsonSerializer.Deserialize<SessionBundle>(await reader.ReadToEndAsync(token))
                        ?? throw new InvalidDataException("빈 보관 자료");
                    var verified=new CManualInspectionWorkspace();
                    // Validate the complete original operation/candidate before deciding whether
                    // automatic startup recovery should defer any unfinished work.
                    verified.RestoreSession(ExpandSession(bundle.Session,next),next.Context,next.Points,next.Values,next.FieldEvaluator,next.StoredInitializedOffsetKeys);
                    deferredOffsetDraft=_backend is not CManualInspectionReplayBackend && verified.HasPendingOffsetEdit;
                    deferredIncomplete=_backend is not CManualInspectionReplayBackend && verified.HasIncompleteMeasurements;
                    if(deferredOffsetDraft || deferredIncomplete)
                    {
                        backingUpOffsetDraft = true;
                        ArchiveOffsetDraft(bytes,deferredIncomplete?deferredOffsetDraft?"startup-workspace":"incomplete-operation":"offset-draft");
                        backingUpOffsetDraft = false;
                        if (IsOffsetDraftRecoveryBlocked(saved)) _offsetDraftRecoveryBlockedPath = null;
                        if(deferredOffsetDraft)
                        {
                            var restored=verified.CaptureSession();restored=restored with { Candidate=restored.OffsetEditPreviousCandidate,OffsetEditPreviousCandidate=null };
                            verified=new CManualInspectionWorkspace();
                            verified.RestoreSession(restored,next.Context,next.Points,next.Values,next.FieldEvaluator,next.StoredInitializedOffsetKeys);
                        }
                        if(deferredIncomplete) verified.ReleaseIncompleteOperation("최초 자동 로딩 · 미완료 원문 복구 보존 후 저장 모델의 idle 작업으로 시작");
                    }
                    _engine=verified;
                    RestoreOptions(bundle);
                    if (IsOffsetDraftRecoveryBlocked(saved)) _offsetDraftRecoveryBlockedPath = null;
                }
                _engine.MergeImportedObservations(next.Points, next.ImportedResults.ToDictionary(p => p.Key, p => p.Value.SavedAt));
                _status = deferredIncomplete
                    ? "이전 미완료 작업 원문은 Recovery에 보존했습니다. 현재 저장된 모델에서 새 수동 작업을 시작할 수 있습니다."
                    : deferredOffsetDraft
                    ? "이전 미저장 Offset 편집 원문은 Recovery에 보존했습니다. 현재 저장된 모델 기준으로 열어 새 측정할 수 있습니다."
                    : "최근 외부 측정과 같은 기준의 작업 상태를 함께 표시했습니다. 자동 동작은 시작하지 않습니다.";
            }
            catch (OperationCanceledException)
            {
                _source = next with { ReadOnly = true };
                if (!sameFrame) _offsetDraftRecoveryBlockedPath = Path.GetFullPath(saved);
                throw;
            }
            catch (Exception e)
            {
                // Keep the conflicting saved file and do not quietly clear an interrupted same-run session.
                _source = next with { ReadOnly = true };
                if (!sameFrame) _offsetDraftRecoveryBlockedPath = Path.GetFullPath(saved);
                _status = "보관 자료는 유지합니다. 다음 동작 때 현재 모델 기준을 연결해 새 측정할 수 있습니다.\n" + e.Message;
                if (backingUpOffsetDraft)
                {
                    _offsetDraftRecoveryBlockedPath = Path.GetFullPath(saved);
                    throw new IOException("이전 보관 작업의 복구 백업에 실패하여 모델 열기를 중단했습니다. 원본 작업 파일을 보존했으며 저장·측정은 실행하지 않았습니다.\n" + e.Message, e);
                }
            }
        }
        else _status = next.Description + " · 현재 모델에서 편집·새 측정 가능";
        _engine.SeedConsumedMeasurements(_backend.ConsumedMeasurements);
        if (!sameFrame) _displayedImports.Clear();
        ReconcileDisplayedImports(next);
        _syncPending = _engine.HasIncompleteMeasurements && next.ImportedResults.Count > 0;
        if (automatic) _syncQueued |= _syncPending;
        else _syncQueued = _syncPending;
        _synchronizationText = path is null ? "최근 측정·현재 활성 보정 표시 · " + next.Description
            : "선택 결과 조회 · " + next.SourcePath;
        if (_syncPending) _synchronizationText = "외부 결과 확인 대기 · 미완료 측정을 보존했습니다. 잔여 측정 후 갱신합니다.";
        await RefreshFinalOffsets(token,true);
    }

    private sealed record WorkspaceBookmark(CManualInspectionWorkspace Engine, CManualInspectionSource Source,
        IManualInspectionBackend Backend, SessionBundle Options,
        Dictionary<string,CManualInspectionImportedResult> DisplayedImports, string Status, string Message);

    private WorkspaceBookmark CaptureBookmark() => new(_engine,_source!,_backend,Bundle(),
        new(_displayedImports,StringComparer.Ordinal),_status,_message);

    private async Task OpenResultWorkspace(string path,CancellationToken token,bool preserveFileWork=false)
    {
        if (_engine.HasIncompleteMeasurements)
        {
            _loadFailureRecovered=true;
            throw new InvalidOperationException("중단·실패한 묶음 측정의 잔여 위치를 완료한 후 파일을 전환하세요. 파일 전환으로 미완료 범위를 지우지 않습니다.");
        }
        var before = _source is null ? null : CaptureBookmark();
        if (before is not null) SaveLocalSession();
        try
        {
            var next = await _production.LoadAsync(path,token);
            if (preserveFileWork && _explicitResultView && _source is not null && SameFrame(_source.Context,next.Context)
                && _engine.Points.Select(p=>p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(next.Points.Select(p=>p.Key)))
            {
                _engine.MergeImportedObservations(next.Points,next.ImportedResults.ToDictionary(p=>p.Key,p=>p.Value.SavedAt));
                _source=next;ReconcileDisplayedImports(next);_syncPending=_syncQueued=false;
                _status="같은 파일을 다시 읽었습니다. 현재 측정·복사·후보·설정과 파일 이전 복귀 지점은 유지합니다.";
                return;
            }
            var separate = new CManualInspectionWorkspace();
            separate.Load(next.Context,next.Points,next.Values,next.Limits,next.FieldEvaluator,next.StoredInitializedOffsetKeys);
            separate.SeedConsumedMeasurements(_production.ConsumedMeasurements);
            if(before is not null && before.Engine.Context is { LineageValid:true } existingContext
                && SameFrame(existingContext,next.Context))
            {
                // Opening measurements changes their display/source, not the saved correction
                // timeline. Carry the verified model history while dropping draft approvals.
                try
                {
                    var history=ExpandSession(before.Engine.CaptureSession(),next) with { Points=next.Points,
                        Limits=next.Limits,Candidate=null,Operation=null,OffsetEditPreviousCandidate=null };
                    separate.RestoreSession(history,next.Context,next.Points,next.Values,next.FieldEvaluator,next.StoredInitializedOffsetKeys);
                    separate.SeedConsumedMeasurements(_production.ConsumedMeasurements);
                }
                catch(InvalidOperationException error)
                { _persistence="파일의 현재 좌표 기준으로 시작 · 이전 적용 이력은 원래 작업에 보존 · "+error.Message; }
            }
            // Publish only after the entire file/geometry/context has passed. Repeated files keep
            // the first native bookmark; they never replace its engine or checkpoint with CSV state.
            if (!_explicitResultView && before is not null) _previousWorkspace ??= before;
            _backend = _production; _engine = separate; _source = next; _explicitResultView = true;
            ClearMeasurementReceipt();
            _allowManual = false; _stageX = _stageY = double.NaN;
            _displayedImports.Clear();ReconcileDisplayedImports(next);
            _syncPending = _syncQueued = false;
            _synchronizationText = "불러온 파일 작업 · 현재 Recipe 기준 이동·측정·검증 후 보정 · " + next.SourcePath;
            _status = next.Description;
        }
        catch
        {
            if (before is not null)
            {
                // LoadAsync prepares mutable adapter plans. Restore them as well as retaining the
                // old engine, so a rejected CSV cannot leave old controls wired to partial new plans.
                try
                {
                    var reloaded = await before.Backend.LoadAsync(
                        string.IsNullOrWhiteSpace(before.Source.SourcePath)?null:before.Source.SourcePath,
                        CancellationToken.None,before.Engine.Points.Select(p=>p.Key).ToArray());
                    _loadFailureRecovered = SameFrame(before.Source.Context,reloaded.Context);
                    _backend=before.Backend;_engine=before.Engine;
                    _source = _loadFailureRecovered ? reloaded : before.Source with { ReadOnly=true };
                }
                catch { _source=before.Source with { ReadOnly=true }; }
            }
            throw;
        }
    }

    private async Task ReturnToPreviousWorkspace(CancellationToken token)
    {
        var bookmark = _previousWorkspace ?? throw new InvalidOperationException("돌아갈 파일 이전 작업이 없습니다.");
        if (_engine.HasIncompleteMeasurements)
        {
            _loadFailureRecovered=true;
            throw new InvalidOperationException("현재 묶음 측정의 잔여 위치를 완료한 후 돌아가세요. 완료한 측정 결과는 자동 보관합니다.");
        }
        SaveLocalSession();
        var keys = bookmark.Engine.Points.Select(p=>p.Key).ToArray();
        CManualInspectionSource next;
        try
        {
            next = await bookmark.Backend.LoadAsync(null,token);
            if (SameFrame(bookmark.Source.Context,next.Context) && keys.Except(next.Points.Select(p=>p.Key),StringComparer.Ordinal).Any())
                next=await bookmark.Backend.LoadAsync(null,token,keys);
        }
        catch
        {
            // The adapter can have prepared part of a different plan even when a read fails.
            // Reconnect the still-visible file before allowing more operations on that display.
            try
            {
                if (_source is not null)
                {
                    var active = await _backend.LoadAsync(_explicitResultView?_source.SourcePath:null,
                        CancellationToken.None,_engine.Points.Select(p=>p.Key).ToArray());
                    _loadFailureRecovered=SameFrame(_source.Context,active.Context);
                    _source = _loadFailureRecovered ? active : _source with { ReadOnly=true };
                }
            }
            catch { if (_source is not null) _source=_source with { ReadOnly=true }; }
            throw;
        }
        var exact = SameFrame(bookmark.Source.Context,next.Context);
        var sameCoordinates = bookmark.Source.Context.RecipeId==next.Context.RecipeId
            && bookmark.Source.Context.RunId==next.Context.RunId
            && bookmark.Source.Context.CoordinateSignature==next.Context.CoordinateSignature;
        CManualInspectionWorkspace restored;
        if (exact) restored=bookmark.Engine;
        else
        {
            restored=new();
            var display = next.Points;
            if (sameCoordinates)
            {
                var old=bookmark.Engine.Points.ToDictionary(p=>p.Key,StringComparer.Ordinal);
                display=next.Points.Select(p=>old.TryGetValue(p.Key,out var value)
                    && value.CellNo==p.CellNo && value.HeadNo==p.HeadNo && value.ShotNo==p.ShotNo && value.BeamNo==p.BeamNo
                    && value.XMm==p.XMm && value.YMm==p.YMm && value.Masked==p.Masked
                    ? value with { Source=!p.IsMeasurementOffsetInitialized && value.IsMeasurementStateCleared?"MANUAL_STATE":"IMPORTED",ProxySource=null,
                        OperatorResult=null,ArchivedOperatorResult=value.OperatorResult??value.ArchivedOperatorResult,AdoptedFileMeasurement=null,
                        IsMeasurementOffsetInitialized=p.IsMeasurementOffsetInitialized,
                        IsMeasurementStateCleared=!p.IsMeasurementOffsetInitialized && value.IsMeasurementStateCleared,
                        StateCopySource=p.IsMeasurementOffsetInitialized?null:value.StateCopySource,
                        HasAppliedOffsetEvidence=p.HasAppliedOffsetEvidence,
                        Judge=p.IsMeasurementOffsetInitialized?"UNMEASURED":value.Judge,
                        ErrorXUm=p.IsMeasurementOffsetInitialized?null:value.ErrorXUm,
                        ErrorYUm=p.IsMeasurementOffsetInitialized?null:value.ErrorYUm } : p).ToArray();
            }
            // Changed B invalidates the old candidate AND old measurement eligibility. Historical
            // values remain visible, but cannot be silently added to the newly corrected baseline.
            restored.Load(next.Context,display,next.Values,sameCoordinates?bookmark.Options.Session.Limits:next.Limits,next.FieldEvaluator,next.StoredInitializedOffsetKeys);
            if(sameCoordinates && _engine.Context is { } fileContext && SameFrame(fileContext,next.Context))
            {
                // Returning the presentation bookmark must not discard corrections just saved
                // in the file workspace. Transfer only a verified same-frame history/ledger;
                // old visible raw stays historical and carries no correction eligibility.
                try
                {
                    var history=ExpandSession(_engine.CaptureSession(),next) with { Points=display,
                        Limits=bookmark.Options.Session.Limits,Candidate=null,Operation=null,OffsetEditPreviousCandidate=null };
                    restored.RestoreSession(history,next.Context,next.Points,next.Values,next.FieldEvaluator,next.StoredInitializedOffsetKeys);
                }
                catch(InvalidOperationException error)
                { _persistence="현재 모델 기준으로 복귀 · 이전 적용 이력은 원래 작업 파일에 보존 · "+error.Message; }
            }
        }
        restored.SeedConsumedMeasurements(bookmark.Backend.ConsumedMeasurements);
        _backend=bookmark.Backend;_source=next;_engine=restored;_explicitResultView=false;
        ClearMeasurementReceipt();
        RestoreOptions(bookmark.Options);
        _displayedImports=new(bookmark.DisplayedImports,StringComparer.Ordinal);ReconcileDisplayedImports(next);
        if (!exact && sameCoordinates)
            foreach (var point in restored.Points.Where(p=>p.Source=="IMPORTED" && p.HasFiniteError))
                _displayedImports[point.Key]=new(bookmark.Source.SourcePath,
                    point.ExternalObservedAt??point.OriginalMeasurement?.MeasuredAt??DateTimeOffset.MinValue,
                    "이전 작업의 측정·복사값 · 활성 보정 변경으로 재측정 필요");
        _stageX=_stageY=double.NaN; // Returning the screen never moves the actual stage back.
        _syncPending=_syncQueued=false;_previousWorkspace=null;
        _synchronizationText="파일 불러오기 전 작업으로 돌아왔습니다. 실제 장비 위치·활성 보정은 되돌리지 않습니다.";
        _status=exact ? "파일 이전의 선택 Recipe 작업·측정·후보·설정을 복구했습니다. 장비 이동은 하지 않았습니다."
            : sameCoordinates ? "원래 Recipe 화면으로 복귀 · 현재 활성 보정값을 반영했습니다. 이전 측정은 이력으로 보존하고 후보는 다시 측정·계산하세요."
            : "현재 Recipe·가공 회차로 복귀했습니다. 달라진 기준의 옛 측정·후보는 자동 재사용하지 않으며 이전 자동 보관 파일은 보존합니다.";
        await RefreshFinalOffsets(token,true);
    }

    private static bool SameFrame(ST_MANUAL_CONTEXT left, ST_MANUAL_CONTEXT right) => left.RecipeId == right.RecipeId
        && left.RunId == right.RunId && left.RevisionId == right.RevisionId && left.CoordinateSignature == right.CoordinateSignature;

    private void ReconcileDisplayedImports(CManualInspectionSource next)
    {
        var incoming = next.Points.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var reconciled = new Dictionary<string, CManualInspectionImportedResult>(StringComparer.Ordinal);
        foreach (var point in _engine.Points.Where(p => p.Source == "IMPORTED" && p.ProxySource is null))
        {
            if (incoming.TryGetValue(point.Key, out var fresh) && next.ImportedResults.TryGetValue(point.Key, out var metadata)
                && point.Judge == fresh.Judge && point.ErrorXUm == fresh.ErrorXUm && point.ErrorYUm == fresh.ErrorYUm
                && point.OriginalMeasurement == fresh.OriginalMeasurement
                && (point.ExternalObservedAt ?? point.OriginalMeasurement?.MeasuredAt) == metadata.SavedAt) reconciled[point.Key] = metadata;
            else if (_displayedImports.TryGetValue(point.Key, out var kept)) reconciled[point.Key] = kept;
        }
        _displayedImports = reconciled;
    }

    /// <summary>Add only newly visible, real Beam slots to a saved display; all old geometry is still verified by the engine.</summary>
    private static ST_MANUAL_SESSION ExpandSession(ST_MANUAL_SESSION session, CManualInspectionSource next)
    {
        var existing = session.Points.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var added = next.Points.Where(p => !existing.ContainsKey(p.Key)).ToArray();
        if (added.Length == 0) return session;
        IReadOnlyDictionary<string, ST_MANUAL_OFFSET> Extend(IReadOnlyDictionary<string, ST_MANUAL_OFFSET> values)
        {
            var result = values.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var byOffset = session.Points.GroupBy(p => p.OffsetKey).ToDictionary(g => g.Key, g => values[g.First().Key], StringComparer.Ordinal);
            foreach (var point in added) result[point.Key] = byOffset.GetValueOrDefault(point.OffsetKey, next.Values[point.Key]);
            return result;
        }
        return session with
        {
            Points = session.Points.Concat(added).ToArray(), ActiveValuesByPointKey = Extend(session.ActiveValuesByPointKey),
            Revisions = session.Revisions.Select(r => r with { ValuesByPointKey = Extend(r.ValuesByPointKey) }).ToArray()
        };
    }

    private async Task Current(CancellationToken token)
    {
        if (_source is null) throw new InvalidOperationException("먼저 현재 Recipe를 불러오세요.");
        _backend.GuardOperation();
        if (_source.ReadOnly || _engine.Context?.LineageValid==false) await ReconnectEditableSource(token);
        try { await _backend.EnsureCurrentAsync(_source!, token); }
        catch (InvalidOperationException) when (!_engine.HasPendingOffsetEdit)
        {
            await ReconnectEditableSource(token);
            await _backend.EnsureCurrentAsync(_source!,token);
        }
    }

    private async Task ReconnectEditableSource(CancellationToken token)
    {
        var before=_source!; var old=_engine.CaptureSession();
        var path=_explicitResultView?before.SourcePath:null;
        var nextExplicitResultView=_explicitResultView;
        CManualInspectionSource next;
        try { next=await _backend.LoadAsync(path,token); }
        catch (Exception error) when (!string.IsNullOrWhiteSpace(path)
            && error is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            // An old file can disappear or refer to the previously selected model. Only this
            // operation-time reconnect falls back; explicit user imports still fail strictly.
            next=await _backend.LoadAsync(null,token);
            if (next.Context.RecipeId==before.Context.RecipeId) next=next with { SourcePath=before.SourcePath };
            else nextExplicitResultView=false;
        }
        var same=SameFrame(before.Context,next.Context);
        if (same && old.Points.Select(p=>p.Key).Except(next.Points.Select(p=>p.Key),StringComparer.Ordinal).Any())
            next=await _backend.LoadAsync(string.IsNullOrWhiteSpace(next.Context.SourceResultPath)?null:path,token,old.Points.Select(p=>p.Key).ToArray());
        var geometry=next.Points.ToDictionary(p=>p.Key,StringComparer.Ordinal);
        same=SameFrame(before.Context,next.Context)
            && old.Points.Count==next.Points.Count
            && old.Points.All(p=>geometry.TryGetValue(p.Key,out var n) && n.CellNo==p.CellNo && n.HeadNo==p.HeadNo && n.ShotNo==p.ShotNo
                && n.BeamNo==p.BeamNo && n.Masked==p.Masked && n.XMm==p.XMm && n.YMm==p.YMm);
        var context=next.Context with { LineageValid=true };
        var nextCheckpoint=SessionPathFor(context,nextExplicitResultView?next.SourcePath:null);
        ArchiveCheckpoint(nextCheckpoint);
        var connected=_engine;
        if (same)
        {
            _engine.ConfirmEditableMeasurementContext(context,next.StoredInitializedOffsetKeys);
        }
        else
        {
            // Preserve old in-progress evidence before exposing the new real coordinate frame.
            var recovery=Path.Combine(_backend.StateDirectory,"Recovery",CManualInspectionBackend.Digest(JsonSerializer.Serialize(old))+".json");
            if (!System.IO.File.Exists(recovery)) CManualInspectionBackend.AtomicText(recovery,JsonSerializer.Serialize(Bundle(),JsonOptions));
            var trusted=next.Context.LineageValid && !next.ReadOnly;
            var points=trusted?next.Points:next.Points.Select(p=>p with { Source="IMPORTED",ProxySource=null,AdoptedFileMeasurement=null }).ToArray();
            var replacement=new CManualInspectionWorkspace();
            replacement.Load(context,points,next.Values,next.Limits,next.FieldEvaluator,next.StoredInitializedOffsetKeys);
            if (System.IO.File.Exists(nextCheckpoint))
            {
                try
                {
                    var saved=JsonSerializer.Deserialize<SessionBundle>(await System.IO.File.ReadAllTextAsync(nextCheckpoint,token));
                    if (saved is null || saved.SchemaVersion!=1 || saved.Simulation!=(_backend is CManualInspectionReplayBackend))
                        throw new InvalidDataException("이전 작업 형식 또는 실행 모드가 다릅니다.");
                    replacement.RestoreSession(ExpandSession(saved.Session,next),context,points,next.Values,next.FieldEvaluator,next.StoredInitializedOffsetKeys);
                    replacement.ConfirmEditableMeasurementContext(context,next.StoredInitializedOffsetKeys);
                    RestoreOptions(saved);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is JsonException or InvalidOperationException or InvalidDataException or IOException or ArgumentException)
                { _persistence="이전 작업 원문은 Recovery에 보존 · 현재 기준으로 새 측정 가능 · "+error.Message; }
            }
            replacement.SeedConsumedMeasurements(old.ConsumedMeasurementHistory.Select(v=>new KeyValuePair<string,string>(v.Key,v.Token)));
            replacement.SeedConsumedMeasurements(old.AppliedMeasurements);
            connected=replacement;
        }
        connected.SeedConsumedMeasurements(_backend.ConsumedMeasurements);
        _engine=connected;_source=next with { Context=context,ReadOnly=false };
        if(!same) ClearMeasurementReceipt();
        _explicitResultView=nextExplicitResultView;
        ReconcileDisplayedImports(_source!);_stageX=_stageY=double.NaN;
        _synchronizationText="현재 모델 기준 재연결 · 이전 출처는 보존하며 새 측정 가능합니다.";
        _loadFailureRecovered=true;
        if(!same){_finalOffsets=null;_finalOffsetStatus="새 작업 기준의 최종 저장 보정 확인 중";}
        await RefreshFinalOffsets(token,true);
        if (!same) throw new InvalidOperationException("모델·가공 회차·좌표 또는 활성 Offset이 바뀌어 새 기준으로 연결했습니다. 대상을 다시 확인하고 측정하세요.");
    }

    private async Task Run(EN_MANUAL_INSPECTION_UI_ACTION action, CManualInspectionUiRequest request, CancellationToken token)
    {
        await Current(token);
        var retry = action == EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining;
        var keys = retry ? _engine.Operation?.RemainingKeys.ToArray() ?? [] : request.Keys.ToArray();
        var flying = !retry && request.MeasurementMode.Equals("FLYING", StringComparison.OrdinalIgnoreCase)
            && action != EN_MANUAL_INSPECTION_UI_ACTION.Move && action != EN_MANUAL_INSPECTION_UI_ACTION.Measure;
        if (flying)
        {
            var groups = keys.Select(_engine.GetPoint).Select(p => (p.CellNo, p.HeadNo, p.Row, p.BeamNo)).ToHashSet();
            keys = _engine.Points.Where(p => !p.Masked && groups.Contains((p.CellNo, p.HeadNo, p.Row, p.BeamNo))).Select(p => p.Key).ToArray();
        }
        if (action == EN_MANUAL_INSPECTION_UI_ACTION.Measure && keys.Length != 1)
            throw new InvalidOperationException("현재 위치 측정은 확인된 한 위치에서만 실행합니다.");
        var kind = retry ? EN_MANUAL_OPERATION_KIND.MoveMeasure : action switch
        {
            EN_MANUAL_INSPECTION_UI_ACTION.Move => EN_MANUAL_OPERATION_KIND.Move,
            EN_MANUAL_INSPECTION_UI_ACTION.Measure => EN_MANUAL_OPERATION_KIND.Measure,
            _ => flying ? EN_MANUAL_OPERATION_KIND.Flying : EN_MANUAL_OPERATION_KIND.MoveMeasure
        };
        var operation = _engine.BeginOperation(_engine.PreviewOperation(kind, keys, retry));
        _hasProgressTimestamp = false; _pendingPointPublication = false;
        _isMeasurementInProgress = kind != EN_MANUAL_OPERATION_KIND.Move;
        _activeMeasurementPointKey = null;
        _activeMeasurementPointKeys = [];
        try
        {
            if (_backend is CManualInspectionReplayBackend replay) replay.Configure(request with { Keys = keys, Fault = retry ? "NONE" : request.Fault });
            _status = $"{kind} · {operation.Keys.Count:N0}점 · 완료 후 정리 확인까지 탭을 잠급니다.";
            SaveLocalSession(); Publish();
            void Received(CManualInspectionMeasurement value) => OnUi(() =>
            {
                token.ThrowIfCancellationRequested();
                _engine.RecordMeasurement(value.Key, value.Measurement, operation.Id);
                _lastMeasuredPointKey=value.Key; _measurementSequence++;
                _lastMeasurementText=$"{_engine.GetPoint(value.Key).CellNo} Cell · H{_engine.GetPoint(value.Key).HeadNo:00} · S{_engine.GetPoint(value.Key).ShotNo} · B{_engine.GetPoint(value.Key).BeamNo} · {value.Measurement.Judge} · ΔX/Y {CManualInspectionUiPoint.Pair(value.Measurement.ErrorXUm,value.Measurement.ErrorYUm)} µm";
                if (value.Measurement.Judge is not ("OK" or "NG"))
                    throw new InvalidDataException($"{value.Key}: 유효한 측정 응답이 없습니다.");
                _engine.MarkOperationPointCompleted(operation.Id, value.Key);
                _stageX = value.ReviewXmm; _stageY = value.StageYmm;
                _status = $"{_engine.Operation!.CompletedKeys.Count:N0} / {operation.Keys.Count:N0}점 · {value.Key} · {value.Measurement.Judge}";
                PublishProgress(pointsChanged: true, force: _engine.Operation.CompletedKeys.Count == 1
                    || _engine.Operation.CompletedKeys.Count == operation.Keys.Count);
                if (DateTimeOffset.UtcNow - _lastCheckpoint > TimeSpan.FromSeconds(2)) SaveLocalSession();
            });
            void Progress(string text) => OnUi(() => { _status = text; PublishProgress(pointsChanged: false); });
            void ActiveTargets(IReadOnlyList<string> active) => OnUi(() =>
            {
                if (_engine.Operation?.Id != operation.Id || !_isMeasurementInProgress) return;
                var current = active.Distinct(StringComparer.Ordinal).ToArray();
                if (current.Except(operation.Keys,StringComparer.Ordinal).Any())
                    throw new InvalidDataException("현재 Flying 작업에 없는 측정 대상 표시를 받았습니다.");
                _activeMeasurementPointKey = null;
                _activeMeasurementPointKeys = current;
                Publish(reusePoints: true);
            });
            if (flying) await _backend.ExecuteFlyingAsync(_source!, keys, Received, Progress, ActiveTargets, token);
            else foreach (var key in operation.Keys)
            {
                token.ThrowIfCancellationRequested();
                if (_isMeasurementInProgress)
                {
                    _activeMeasurementPointKey = key;
                    _activeMeasurementPointKeys = [key];
                    // Publish the target before awaiting movement/measurement. LastMeasured
                    // is a completed response and must never stand in for a live target.
                    Publish(reusePoints: true);
                }
                var value = await _backend.ExecutePointAsync(_source!, key, kind, Progress, token);
                if (kind == EN_MANUAL_OPERATION_KIND.Move)
                { _engine.MarkOperationPointCompleted(operation.Id, key); _stageX = value.ReviewXmm; _stageY = value.StageYmm;
                  PublishProgress(pointsChanged: false, force: _engine.Operation!.CompletedKeys.Count == 1
                      || _engine.Operation.CompletedKeys.Count == operation.Keys.Count); }
                else Received(value);
            }
            var completed = _engine.CompleteOperation(operation.Id);
            _status = "측정/이동 완료 · 보정 저장 없음 · 필요할 때 일괄 보정을 사용하세요."
                + (completed.Errors.Count > 0 ? "\n" + string.Join("\n", completed.Errors) : "");
        }
        catch (Exception e)
        {
            if (_engine.Operation?.State == EN_MANUAL_OPERATION_STATE.Running)
                _engine.CompleteOperation(operation.Id, e is OperationCanceledException ? EN_MANUAL_OPERATION_STATE.Stopped : EN_MANUAL_OPERATION_STATE.Error, e.Message);
            throw;
        }
        finally
        {
            _isMeasurementInProgress = false;
            _activeMeasurementPointKey = null;
            _activeMeasurementPointKeys = [];
            Publish(reusePoints: true);
        }
    }

    private void InitializeOffsets(CManualInspectionUiRequest request)
    {
        // Validate the post-retirement Init in isolation first. A failed reset must not
        // discard an interrupted queue, old candidate, raw/ledger or create a blocking draft.
        var sandbox=IdleTransitionPreview();
        ApplyOffsetInitialization(sandbox,request);
        var check=sandbox.ValidateCandidate(false);
        if(!check.Ok) throw new InvalidOperationException("Offset Init을 적용하지 않았습니다. 기존 작업과 파일을 유지합니다.\n"+string.Join("\n",check.Errors));
        if(_engine.HasIncompleteMeasurements)
        {
            _engine.ReleaseIncompleteOperation("명시 Offset Init 요청으로 미완료 작업 전환");
        }
        ApplyOffsetInitialization(_engine,request);
    }

    private static void ApplyOffsetInitialization(CManualInspectionWorkspace engine,CManualInspectionUiRequest request)
    {
        if(request.OffsetInitScope=="ALL") { engine.InitializeMeasurementOffsets();return; }
        IEnumerable<ST_MANUAL_POINT> points=engine.Points;
        IReadOnlyList<int> cells=[];
        if(request.OffsetInitScope=="CELL" && request.OffsetInitCellNo is int cell && cell>0)
        { points=points.Where(p=>p.CellNo==cell);cells=[cell]; }
        else if(request.OffsetInitScope=="HEAD" && request.OffsetInitHeadNo is int head && head is >=1 and <=8)
            points=points.Where(p=>p.HeadNo==head);
        else throw new InvalidOperationException("초기화 범위와 Cell/Head를 선택하세요.");
        var keys=points.Select(p=>p.Key).ToArray();
        if(keys.Length==0) throw new InvalidOperationException("해당 범위에 초기화할 좌표가 없습니다.");
        engine.InitializeMeasurementOffsets(keys,cells);
    }

    private CManualInspectionWorkspace IdleTransitionPreview()
    {
        var preview=new CManualInspectionWorkspace();
        preview.RestoreSession(_engine.CaptureSession(),_engine.Context!,_engine.Points,_engine.ActiveOffsets,_source!.FieldEvaluator,
            _engine.ActiveInitializedOffsetKeys.ToHashSet(StringComparer.Ordinal));
        preview.ReleaseIncompleteOperation("명시 수동 작업 전환 사전검증");return preview;
    }

    private async Task CopyReference(CManualInspectionUiRequest request,CancellationToken token)
    {
        await Current(token);
        if(string.IsNullOrWhiteSpace(request.Reason)) throw new InvalidOperationException("대표 복사 사유를 입력하세요.");
        var reference=request.ReferenceKey ?? "";
        if(_engine.HasIncompleteMeasurements)
        {
            var sandbox=IdleTransitionPreview();
            if(sandbox.CanCopyReferenceState(reference)) sandbox.PreviewReferenceStateCopy(reference,request.Keys);
            else sandbox.PreviewReferenceCopy(reference,request.Keys);
            _engine.ReleaseIncompleteOperation("명시 대표 복사 요청으로 미완료 작업 전환");
        }
        if(_engine.CanCopyReferenceState(reference))
        {
            var preview=_engine.PreviewReferenceStateCopy(reference,request.Keys);
            _engine.ConfirmReferenceStateCopy(preview,request.Keys,request.Reason);
            _allowManual=false;
            _status=$"{preview.Targets.Count:N0}점에 대표의 {preview.Judge} 빈 상태를 반영했습니다. 실제 모델 Offset·원측정·파일은 유지합니다.";
        }
        else
        {
            var preview=_engine.PreviewReferenceCopy(reference,request.Keys);
            _engine.ConfirmReferenceCopy(preview,request.Keys,request.Reason);_allowManual=true;
            var eligible=_engine.PreviewBulkCorrection(preview.Targets.Select(p=>p.Key));
            if(eligible.Keys.Count>0)
            {
                _engine.BuildDirectCandidate(eligible.Keys);
                var validation=_engine.ValidateCandidate(true);
                if(!validation.Ok) throw new InvalidOperationException("대표값을 작업화면에 복사했지만 보정 파일을 저장하지 않았습니다.\n"+string.Join("\n",validation.Errors));
                await Apply(token,sourceVerified:true);
            }
            _status=$"대표 {preview.Targets.Count:N0}점 복사 · {eligible.Keys.Count:N0}점 모델 저장 · NG {eligible.ExcludedNg:N0} / 미측정 {eligible.ExcludedUnmeasured:N0} / 수치한계 {eligible.ExcludedNumeric:N0} / 동일 적용값 {eligible.ExcludedUnchanged:N0}점 제외 · 원측정 보존"+(eligible.Keys.Count>0?SaveConfirmationSuffix:"");
        }
    }

    private async Task ForceMeasurement(CManualInspectionUiRequest request,CancellationToken token)
    {
        await Current(token);
        if(_engine.HasIncompleteMeasurements)
        {
            var preview=IdleTransitionPreview();preview.RecordOperatorResults(request.Keys,request.ForcedXUm,request.ForcedYUm,request.Reason);
            _engine.ReleaseIncompleteOperation("명시 수동 측정 완료 요청으로 미완료 작업 전환");
        }
        _engine.RecordOperatorResults(request.Keys,request.ForcedXUm,request.ForcedYUm,request.Reason);
        _allowManual=true;
        _status=$"{request.Keys.Distinct(StringComparer.Ordinal).Count():N0}점 수동 OK 완료 · 원측정 보존 · 실제 보정은 선택점/일괄 보정에서 검증 후 저장";
    }

    private async Task BulkApply(int? currentCellNo,CancellationToken token)
    {
        // Scope is an explicit UI choice, never inferred from selection, focus or a head filter.
        // In the all-cell view no cell is current; rejecting a missing scope prevents an
        // accidental model-wide write from an obsolete caller or a filtered selection.
        if(currentCellNo is not >0) throw new InvalidOperationException("일괄 보정할 Cell 화면을 먼저 선택하세요. 전체 Cell 보기에서는 일괄 보정하지 않습니다.");
        await Current(token);
        var cellKeys=_engine.Points.Where(p=>p.CellNo==currentCellNo.Value).Select(p=>p.Key).ToArray();
        if(cellKeys.Length==0) throw new InvalidOperationException($"Cell #{currentCellNo.Value}의 측정 좌표가 없습니다. 현재 Cell을 다시 선택하세요.");
        var preview=_engine.PreviewBulkCorrection(cellKeys);
        if(preview.Keys.Count==0) throw new InvalidOperationException($"Cell #{currentCellNo.Value}에 일괄 보정할 결과가 없습니다. NG {preview.ExcludedNg:N0} / 미측정 {preview.ExcludedUnmeasured:N0} / 수치한계 {preview.ExcludedNumeric:N0} / 이미 적용 {preview.ExcludedConsumed+preview.ExcludedUnchanged:N0}점 제외");
        _engine.BuildBulkCandidate(cellKeys); _allowManual=preview.ManualCount>0;
        var validation=_engine.ValidateCandidate(_allowManual);
        if(!validation.Ok) throw new InvalidOperationException("일괄 보정을 저장하지 않았습니다.\n"+string.Join("\n",validation.Errors));
        await Apply(token, sourceVerified: true);
        _status=$"Cell #{currentCellNo.Value} 일괄 보정 {preview.Keys.Count:N0}점 저장 · NG {preview.ExcludedNg:N0} / 미측정 {preview.ExcludedUnmeasured:N0} / 수치한계 {preview.ExcludedNumeric:N0} / 적용됨 {preview.ExcludedConsumed+preview.ExcludedUnchanged:N0}점 제외"+SaveConfirmationSuffix;
    }

    private async Task ApplySelectedOffsets(IReadOnlyList<string> keys, CancellationToken token)
    {
        await Current(token);
        var selected = keys.Distinct(StringComparer.Ordinal).Select(_engine.GetPoint).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("보정할 측정 위치를 선택하세요.");
        if (selected.Any(point => point.Masked)) throw new InvalidOperationException("선택한 위치에 Masking이 포함되어 보정을 저장하지 않았습니다.");
        var preview=_engine.PreviewBulkCorrection(selected.Select(p=>p.Key));
        if(preview.Keys.Count==0)
            throw new InvalidOperationException($"선택점 보정 대상이 없습니다. NG {preview.ExcludedNg:N0} / 미측정 {preview.ExcludedUnmeasured:N0} / 수치한계 {preview.ExcludedNumeric:N0} / 이미 적용 {preview.ExcludedConsumed+preview.ExcludedUnchanged:N0} / 출처 확인 {preview.ExcludedInvalid:N0}점 제외");
        var candidate = _engine.BuildDirectCandidate(preview.Keys);
        _allowManual = candidate.Rows.Any(row => row.Source == "MANUAL");
        var validation = _engine.ValidateCandidate(_allowManual);
        if (!validation.Ok) throw new InvalidOperationException("선택점 보정을 저장하지 않았습니다.\n" + string.Join("\n", validation.Errors));
        await Apply(token, sourceVerified: true);
        _status = $"선택점 보정 {preview.Keys.Count:N0}점 저장 · NG {preview.ExcludedNg:N0} / 미측정 {preview.ExcludedUnmeasured:N0} / 수치한계 {preview.ExcludedNumeric:N0} / 이미 적용 {preview.ExcludedConsumed+preview.ExcludedUnchanged:N0}점 제외"+SaveConfirmationSuffix;
    }

    private async Task Apply(CancellationToken token, bool sourceVerified = false)
    {
        if (!sourceVerified) await Current(token);
        var snapshot = _engine.CreateApplySnapshot(_allowManual);
        var committed=false;
        try
        {
            SaveLocalSession(); Publish(reusePoints: true);
            var changedShots=snapshot.Rows.Where(row=>row.Source!="OFFSET_KEEP").Select(row=>row.OffsetKey).ToHashSet(StringComparer.Ordinal);
            var initialized=snapshot.IsOffsetReplacement ? snapshot.InitializedOffsetKeys
                : _engine.Revisions.Last(r=>r.Id==_engine.Context!.RevisionId).InitializedOffsetKeys.Where(k=>!changedShots.Contains(k)).ToArray();
            var identity = JsonSerializer.Serialize(new { Kind = snapshot.IsOffsetReplacement ? snapshot.IsMeasurementOffsetInitialization?"OFFSET_INIT":"OFFSET_BUNDLE_REPLACE":"MANUAL_INSPECTION", snapshot.RecipeId, snapshot.RunId,
                InitializedOffsetKeys=initialized,snapshot.InitializationOffsetKeys,snapshot.InitializationCellNos,
                InputResultPath = _source?.SourcePath,
                OffsetEditBaseRevision = snapshot.IsOffsetReplacement ? snapshot.BaseRevisionId : null,
                FileObservations = snapshot.Rows.Where(r=>_engine.GetPoint(r.Key).AdoptedFileMeasurement is not null)
                    .Select(r=>new { r.Key,Receipt=_engine.GetPoint(r.Key).AdoptedFileMeasurement }),
                Tokens = snapshot.Rows.Select(r => new { r.Key, r.MeasurementToken, r.Source, r.ReferenceKey, r.Reason, r.Final,
                    OperatorResult=_engine.GetPoint(r.Key).OperatorResult,
                    // A representative can be remeasured after the copy. Audit the raw captured by
                    // that copy, not a newer raw currently displayed at the same reference key.
                    ReferenceRaw = r.Source == "MANUAL" ? _engine.GetPoint(r.Key).ProxySource?.ReferenceMeasurementToken
                        : r.MeasurementToken }).OrderBy(r => r.Key) });
            var revision = snapshot.IsOffsetReplacement
                ? await _backend.SaveCumulativeAsync(_source!, snapshot.ValuesByPointKey, identity, snapshot.Rows,
                    snapshot.InitializeAllReviewRows || !snapshot.IsMeasurementOffsetInitialization,token,snapshot.ConsumedMeasurements)
                : await _backend.SaveAsync(_source!, snapshot.ValuesByPointKey, identity, snapshot.Rows, token, snapshot.ConsumedMeasurements);
            _engine.CommitApplied(snapshot, revision, "Manual Inspection 검증된 보정 적용",
                _backend.LastSavedValues.Count > 0 ? _backend.LastSavedValues : null);
            committed=true;
            var written = snapshot.IsOffsetReplacement ? changedShots
                : snapshot.Rows.Select(r=>r.OffsetKey).ToHashSet(StringComparer.Ordinal);
            _source = _source! with
            {
                Context = _engine.Context!, Values = _engine.ActiveOffsets,
                Points=_engine.Points.ToArray(),StoredInitializedOffsetKeys=_engine.ActiveInitializedOffsetKeys.ToHashSet(StringComparer.Ordinal),
                AppliedOffsetKeys = snapshot.IsOffsetReplacement && !snapshot.IsMeasurementOffsetInitialization || snapshot.InitializeAllReviewRows ? _source.Points.Select(p=>p.Key).ToHashSet(StringComparer.Ordinal)
                    : _source.AppliedOffsetKeys.Concat(_source.Points.Where(p => written.Contains(p.OffsetKey)).Select(p => p.Key)).ToHashSet(StringComparer.Ordinal)
            };
            BeginSaveConfirmation();
            await RefreshFinalOffsets(CancellationToken.None,true);
            _status = "모델 통합 Offset 저장 완료 · 보호 Offset·기존 파일/원측정 이력 보존"+SaveConfirmationSuffix;
        }
        catch { if(!committed)_engine.AbortApply(snapshot);throw; }
    }

    private async Task Restore(CancellationToken token,bool redo)
    {
        await Current(token); var snapshot = redo?_engine.CreateRestoreNextSnapshot():_engine.CreateRestorePreviousSnapshot();
        var committed=false;
        try
        {
            SaveLocalSession(); Publish(reusePoints: true);
            var revision = await _backend.SaveAsync(_source!, snapshot.ValuesByPointKey,
                JsonSerializer.Serialize(new { Kind = redo?"REDO":"UNDO", snapshot.RecipeId, snapshot.RunId, snapshot.BaseRevisionId, snapshot.RestoreTargetRevisionId,
                    snapshot.InitializedOffsetKeys,snapshot.InitializationOffsetKeys }), [], token,snapshot.ConsumedMeasurements);
            _engine.CommitRestored(snapshot, revision, redo?"사용자 요청 · 보정 다시 적용":"사용자 요청 · 보정 되돌리기",
                _backend.LastSavedValues.Count > 0 ? _backend.LastSavedValues : null);
            committed=true;
            _source = _source! with { Context = _engine.Context!, Values = _engine.ActiveOffsets,Points=_engine.Points.ToArray(),
                StoredInitializedOffsetKeys=_engine.ActiveInitializedOffsetKeys.ToHashSet(StringComparer.Ordinal) };
            ClearMeasurementReceipt();
            BeginSaveConfirmation();
            await RefreshFinalOffsets(CancellationToken.None,true);
            _status=(redo?"보정을 다시 적용했습니다.":"이전 보정으로 되돌렸습니다.")+$" 되돌리기 {_engine.UndoCount}/10 · 다시 적용 {_engine.RedoCount}회 · 원측정 이력 보존"+SaveConfirmationSuffix;
        }
        catch { if(!committed)_engine.AbortRestore(snapshot);throw; }
    }

    public void RequestStop()
    {
        // Cancellation registrations and device-stop reporting can throw independently. Always
        // attempt both stop paths, keep Busy until the worker returns, and still let StopAsync wait.
        var errors = new List<string>();
        try { _cancellation?.Cancel(); }
        catch (Exception error) { errors.Add("취소 전달: " + error.Message); TraceObserverFailure("cancellation callback", error); }
        try { if (_engine.Operation?.State == EN_MANUAL_OPERATION_STATE.Running) _backend.Stop(); }
        catch (Exception error) { errors.Add("장비 STOP: " + error.Message); TraceObserverFailure("device STOP request", error); }
        if (errors.Count > 0) _stopRequestError = string.Join(" / ", errors);
        _status = "STOP 요청 · 이동/측정 정리 완료를 기다립니다."
            + (_stopRequestError.Length > 0 ? "\nSTOP 요청 확인 필요 · " + _stopRequestError : "");
        // A burst may have pending display updates: show every already-recorded point while the
        // device is stopping, without waiting for its potentially delayed cleanup return.
        _message = _status; Publish();
    }
    public async Task StopAsync()
    {
        if (!IsBusy) return;
        var waitForCleanup = _waitForControlStop || _engine.IsBusy;
        var cleanup = _idle;
        RequestStop();
        if (waitForCleanup && cleanup is not null) await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private void UpdateLevels(CManualInspectionUiRequest request)
    {
        if (request.LevelMetric is not ("MAX_AXIS" or "RADIAL") || request.LevelBoundsUm is not { Count: 3 }
            || request.LevelNames is not { Count: 4 } || request.LevelNames.Any(n => string.IsNullOrWhiteSpace(n) || n.Trim().Length > 20)
            || request.LevelNames.Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4
            || request.LevelBoundsUm.Any(x => !double.IsFinite(x) || x <= 0)
            || !(request.LevelBoundsUm[0] < request.LevelBoundsUm[1] && request.LevelBoundsUm[1] < request.LevelBoundsUm[2]))
            throw new InvalidOperationException("표시 레벨은 유효한 지표·증가하는 경계3개·이름4개가 필요합니다.");
        _levelMetric = request.LevelMetric; _levelBounds = request.LevelBoundsUm.ToArray(); _levelNames = request.LevelNames.ToArray();
        _status = "표시 레벨만 변경했습니다. 원측정/판정/활성 보정은 바꾸지 않았습니다.";
    }

    private void ShowPoint(string? key)
    {
        if (key is null) return; var p = _engine.GetPoint(key);
        var text = new StringBuilder(); text.AppendLine($"{p.Key}\nDESIGN {p.XMm:R}/{p.YMm:R} mm\n원시 기록의 작업 판정 {p.Judge} · {p.Source}");
        var display = Snapshot.Points.FirstOrDefault(point => point.Key == key);
        if (display is not null)
        {
            var level = CManualInspectionLevels.Evaluate(display.MeasurementXUm, display.MeasurementYUm, _levelMetric, _levelBounds);
            text.AppendLine($"표시 상태: {display.MeasurementStatusText}\n표시 편차: {display.MeasurementErrorText} µm · {(level.HasValue && !display.IsMasked ? $"L{level.Value + 1}" : "Level 없음")}\n근거: {display.MeasurementBasisText}");
            if (display.UsesAppliedMeasurement) text.AppendLine("보정에 사용된 측정 이력을 표시합니다. 아래 원측정/raw는 그대로 보존하며 표시 편차를 추가 보정으로 재누적하지 않습니다.");
            if (!string.IsNullOrWhiteSpace(display.MeasurementSourcePath)) text.AppendLine("표시 근거 파일: " + display.MeasurementSourcePath);
            if (_displayedImports.TryGetValue(key, out var imported)) text.AppendLine("최근 결과 파일: " + imported.Path);
        }
        text.AppendLine(JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var item in _engine.History.Where(h => h.Key == key)) text.AppendLine(JsonSerializer.Serialize(item));
        ShowDetails("원측정·변경 이력 · DESIGN µm / 원시 단위 명시", text.ToString());
    }

    private async Task ImportSession(CancellationToken token)
    {
        var file = ChooseFile(false, "json"); if (file is null) return;
        var bundle = JsonSerializer.Deserialize<SessionBundle>(await System.IO.File.ReadAllTextAsync(file, token))
            ?? throw new InvalidDataException("세션 파일이 비어 있습니다.");
        if (bundle.SchemaVersion != 1) throw new InvalidDataException("지원하지 않는 네이티브 세션 버전입니다.");
        if (bundle.Simulation)
        {
            if (!_production.IsSimulation) throw new InvalidOperationException("실제 설비 모드에 SIM 세션을 적용할 수 없습니다.");
            var simulation = new CManualInspectionReplayBackend(bundle.Scenario, _production.StateDirectory, bundle.Session);
            _backend = simulation;
        }
        else _backend = _production;
        var next = await _backend.LoadAsync(null, token, bundle.Session.Points.Select(p => p.Key).ToArray());
        _engine.RestoreSession(bundle.Session, next.Context, next.Points, next.Values, next.FieldEvaluator,next.StoredInitializedOffsetKeys);
        _engine.SeedConsumedMeasurements(_backend.ConsumedMeasurements);
        _source = next; ClearMeasurementReceipt(); RestoreOptions(bundle); _status = "실제 Recipe/Run/보정과 대조해 세션을 불러왔습니다. 자동 동작은 재개하지 않습니다.";
        _explicitResultView = false;
        _stageX = _stageY = double.NaN;
        _syncPending = _engine.HasIncompleteMeasurements && next.ImportedResults.Count > 0;
        _syncQueued = _syncPending;
        _synchronizationText = "현재 세션 연결 · 최근 측정·활성 보정 자동 확인";
        _displayedImports.Clear(); ReconcileDisplayedImports(next);
    }

    private async Task ExportSession(CancellationToken token)
    {
        if (_source is null) throw new InvalidOperationException("내보낼 작업 상태가 없습니다.");
        var file = ChooseFile(true, "json"); if (file is null) return;
        var json = JsonSerializer.Serialize(Bundle(), JsonOptions);
        await Task.Run(() => CManualInspectionBackend.AtomicText(file, json), token);
        _status = "현재 세션을 JSON으로 내보냈습니다.";
    }

    private async Task ExportCsv(CancellationToken token)
    {
        if (_source is null) throw new InvalidOperationException("내보낼 결과가 없습니다.");
        var file = ChooseFile(true, "csv"); if (file is null) return;
        static string Q(object? value) => "\"" + (value is double d ? d.ToString("R", CultureInfo.InvariantCulture) : value?.ToString() ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("KEY,CELL,HEAD,SHOT,BEAM,ROW,COLUMN,DESIGN_X_MM,DESIGN_Y_MM,WORK_JUDGE,WORK_DX_DESIGN_UM,WORK_DY_DESIGN_UM,ORIGINAL_JUDGE,ORIGINAL_DX_DESIGN_UM,ORIGINAL_DY_DESIGN_UM,STATE_SOURCE,REFERENCE_KEY,BASE_X_DESIGN_UM,BASE_Y_DESIGN_UM,FINAL_X_DESIGN_UM,FINAL_Y_DESIGN_UM,ACTIVE_REVISION,RUN,ORIGINAL_RAW,MEASUREMENT_HISTORY,MEASUREMENT_BASIS,DISPLAY_DX_DESIGN_UM,DISPLAY_DY_DESIGN_UM,MEASUREMENT_LEVEL\r\n");
        var bases = _engine.ActiveOffsets; var candidates = _engine.Candidate?.Rows.ToDictionary(r => r.Key) ?? [];
        var displayPoints = Snapshot.Points.ToDictionary(point => point.Key, StringComparer.Ordinal);
        foreach (var p in _engine.Points)
        {
            var b = bases[p.Key]; var f = candidates.GetValueOrDefault(p.Key)?.Final;
            var display = displayPoints[p.Key];
            var level = display.HasMeasurementHistory ? CManualInspectionLevels.Evaluate(display.MeasurementXUm, display.MeasurementYUm, _levelMetric, _levelBounds) : null;
            object?[] row = [p.Key,p.CellNo,p.HeadNo,p.ShotNo,p.BeamNo,p.Row+1,p.Column+1,p.XMm,p.YMm,p.Judge,p.ErrorXUm,p.ErrorYUm,
                p.OriginalMeasurement?.Judge,p.OriginalMeasurement?.ErrorXUm,p.OriginalMeasurement?.ErrorYUm,p.Source,p.ProxySource?.ReferenceKey,
                b.XUm,b.YUm,f?.XUm,f?.YUm,_engine.Context!.RevisionId,_engine.Context.RunId,p.OriginalMeasurement?.RawData,
                display.HasMeasurementHistory,display.MeasurementBasisText,display.MeasurementXUm,display.MeasurementYUm,level.HasValue ? $"L{level.Value+1}" : ""];
            sb.AppendLine(string.Join(",", row.Select(Q)));
        }
        await System.IO.File.WriteAllTextAsync(file, sb.ToString(), new UTF8Encoding(true), token); _status = "작업·원측정·보정 기준을 구분한 CSV를 내보냈습니다.";
    }

    private async Task ExportOffsetBundle(string? requestedPath,CancellationToken token)
    {
        if (_source is null) throw new InvalidOperationException("먼저 현재 모델을 불러오세요.");
        var path=requestedPath??ChooseFile(true,"json");
        if (path is null) return;
        await _backend.EnsureCurrentAsync(_source,token);
        var values=_engine.HasPendingOffsetEdit
            ? _engine.Candidate!.Rows.ToDictionary(row=>row.Key,row=>row.Final,StringComparer.Ordinal)
            : _engine.ActiveOffsets;
        var exportSource=_source;
        if (_engine.HasPendingOffsetEdit)
        {
            var edit=_engine.Candidate!;
            var initialized=edit.InitializedOffsetKeys.ToHashSet(StringComparer.Ordinal);
            exportSource=_source with { HasExplicitMeasurementState=true,Points=_source.Points.Select(p=>p with { IsMeasurementOffsetInitialized=initialized.Contains(p.OffsetKey) }).ToArray(),
                InitializationCellNos=edit.InitializationCellNos,InitializationOffsetKeys=edit.InitializationOffsetKeys.ToHashSet(StringComparer.Ordinal) };
        }
        await _backend.ExportOffsetBundleAsync(exportSource,values,path,
            _engine.HasPendingOffsetEdit,
            _engine.HasPendingOffsetEdit && (_engine.Candidate!.InitializeAllReviewRows || !_engine.Candidate.IsMeasurementOffsetInitialization),token);
        _status=_engine.HasPendingOffsetEdit?"통합 Offset 작업파일을 내보냈습니다 · 미저장 표시 · 실제 활성 보정 변경 없음"
            :"현재 적용된 모델 통합 Offset과 보호 설정 참고값·최종 좌표를 파일 하나로 내보냈습니다. 미적용 측정 후보는 제외했습니다.";
    }

    private async Task ExportFinalOffsets(string? requestedPath,CancellationToken token)
    {
        if(_source is null) throw new InvalidOperationException("먼저 현재 모델을 불러오세요.");
        var path=requestedPath;
        if(path is null)
        {
            if(ChooseFile==DefaultChooseFile)
            {
                await RefreshFinalOffsets(token,true);
                if(_finalOffsets is null) throw new InvalidOperationException(_finalOffsetStatus);
                var name=string.Concat(_source.Context.RecipeId.Select(ch=>Path.GetInvalidFileNameChars().Contains(ch)?'_':ch));
                var revision=_finalOffsets.SavedAt?.ToString("yyyyMMddHHmmssfff",CultureInfo.InvariantCulture)??"initial";
                var dialog=new SaveFileDialog { Filter="최종 저장 Offset (*.json)|*.json",DefaultExt=".json",FileName=$"{name}_final_r{revision}.json" };
                path=dialog.ShowDialog()==true?dialog.FileName:null;
            }
            else path=ChooseFile(true,"json");
        }
        if(path is null) return;
        await _finalOffsetReadGate.WaitAsync(token);
        try
        {
            var saved=await _backend.ExportFinalOffsetBundleAsync(_source,path,token);
            if(saved is null) throw new InvalidOperationException("최종 저장 보정 내보내기 결과를 확인할 수 없습니다.");
            AcceptFinalOffsets(saved);
            _status="현재 모델의 최종 저장 누적 Offset·보호 설정 참고값·최종 좌표를 내보냈습니다. 작업 중인 미저장 후보와 원측정은 유지했습니다.";
        }
        catch
        {
            _finalOffsets=null;_finalOffsetStatus="최종 저장 보정 내보내기 실패 · 최신 기준을 다시 확인하세요.";
            throw;
        }
        finally { _finalOffsetReadGate.Release();ScheduleFinalOffsetFollowup(); }
    }

    private async Task ExportCurrentOffsetCsv(string? requestedPath,CancellationToken token)
    {
        if(_source is null) throw new InvalidOperationException("먼저 현재 모델을 불러오세요.");
        var path=requestedPath;
        var overwriteConfirmed=false;
        if(path is null)
        {
            if(ChooseFile==DefaultChooseFile)
            {
                await RefreshFinalOffsets(token,true);
                if(_finalOffsets is null || string.IsNullOrWhiteSpace(_finalOffsets.FilePath))
                    throw new InvalidOperationException("현재 모델에 내보낼 최종 Review Offset CSV가 없습니다.");
                var dialog=CreateCurrentOffsetCsvSaveDialog(_finalOffsets);
                path=dialog.ShowDialog()==true?dialog.FileName:null;
                overwriteConfirmed=path is not null; // Native dialog owns the explicit overwrite confirmation.
            }
            else path=ChooseFile(true,"csv");
        }
        if(path is null) return;
        await _finalOffsetReadGate.WaitAsync(token);
        try
        {
            // The backend copies the latest complete saved CSV bytes, not a working candidate
            // or a current-cell subset. It also checks model/frame and protects active CSVs.
            var saved=await _backend.ExportCurrentOffsetCsvAsync(_source,path,token,overwriteConfirmed);
            if(saved is null) throw new InvalidOperationException("현재 Offset CSV 내보내기 결과를 확인할 수 없습니다.");
            AcceptFinalOffsets(saved);
            var same=StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path),Path.GetFullPath(saved.FilePath));
            _status=same?"기존 최종 Offset CSV를 확인했습니다. 원본 파일·작업값은 유지합니다."
                :"현재 모델의 최종 Offset CSV 전체를 내보냈습니다. 미저장 작업값·원측정은 포함하지 않습니다.";
        }
        finally { _finalOffsetReadGate.Release();ScheduleFinalOffsetFollowup(); }
    }

    private static SaveFileDialog CreateCurrentOffsetCsvSaveDialog(CManualInspectionSavedOffsetSnapshot saved)
        => new() { Filter="현재 Offset CSV (*.csv)|*.csv",DefaultExt=".csv",AddExtension=true,
            InitialDirectory=Path.GetDirectoryName(saved.FilePath),
            FileName=Path.GetFileNameWithoutExtension(saved.FileName)+"_export.csv",OverwritePrompt=true };

    private void ClearMeasurementReceipt() { _lastMeasuredPointKey=null;_lastMeasurementText=""; }

    private SessionBundle Bundle() => new(1, _backend is CManualInspectionReplayBackend, _simulationScenario,
        _engine.CaptureSession(), _allowManual, _levelMetric, _levelBounds, _levelNames);
    private void RestoreOptions(SessionBundle bundle)
    {
        UpdateLevels(new() { LevelMetric = bundle.LevelMetric, LevelBoundsUm = bundle.LevelBounds, LevelNames = bundle.LevelNames });
        _allowManual = bundle.AllowManual; _levelMetric = bundle.LevelMetric;
        if (bundle.LevelBounds?.Length == 3) _levelBounds = bundle.LevelBounds;
        if (bundle.LevelNames?.Length == 4) _levelNames = bundle.LevelNames;
        _simulationScenario = bundle.Scenario;
    }
    private string SessionPath() => SessionPathFor(_engine.Context!,_explicitResultView?_source?.SourcePath:null);
    private string SessionPathFor(ST_MANUAL_CONTEXT context,string? path) => Path.Combine(_backend.StateDirectory,"Sessions",
        CManualInspectionBackend.Digest(context.RecipeId+"|"+context.RunId+"|"+context.CoordinateSignature+"|"+context.RevisionId
            +(path is not null?"|FILE:"+path:""))+".json");
    private void ArchiveCheckpoint(string path)
    {
        if (!System.IO.File.Exists(path)) return;
        var bytes=System.IO.File.ReadAllBytes(path);
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var directory=Path.Combine(_backend.StateDirectory,"Recovery");Directory.CreateDirectory(directory);
        var destination=Path.Combine(directory,"checkpoint-"+hash+".json");
        if (System.IO.File.Exists(destination))
        {
            if (!System.IO.File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes)) throw new IOException("복구 보관 파일의 내용이 일치하지 않습니다.");
            return;
        }
        using var file=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        file.Write(bytes);file.Flush(true);
    }
    private void ArchiveOffsetDraft(byte[] bytes,string kind="offset-draft")
    {
        var directory = Path.Combine(_backend.StateDirectory, "Recovery");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, kind + "-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N") + ".json");
        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { file.Write(bytes); file.Flush(true); }
        if (!System.IO.File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes))
            throw new IOException("미저장 Offset 복구 백업의 원문 검증에 실패했습니다.");
    }
    private void SaveLocalSession()
    {
        if (_source is null || _source.ReadOnly) return;
        var path = SessionPath();
        if (IsOffsetDraftRecoveryBlocked(path)) return;
        CManualInspectionBackend.AtomicText(path, JsonSerializer.Serialize(Bundle(), JsonOptions));
        _lastCheckpoint = DateTimeOffset.UtcNow; _persistence = "자동 보관 " + DateTimeOffset.Now.ToString("HH:mm:ss");
    }
    private bool IsOffsetDraftRecoveryBlocked(string path) => _offsetDraftRecoveryBlockedPath is not null
        && string.Equals(Path.GetFullPath(path), _offsetDraftRecoveryBlockedPath, StringComparison.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public sealed record SessionBundle(int SchemaVersion, bool Simulation, string Scenario, ST_MANUAL_SESSION Session,
        bool AllowManual, string LevelMetric, double[] LevelBounds, string[] LevelNames);

    private void PublishProgress(bool pointsChanged, bool force = false)
    {
        // Flying may deliver a complete saved row as a burst. Record every measurement immediately,
        // but avoid rebuilding/repainting the entire matrix once per received point. This clock
        // affects presentation only, never equipment timing, tokens, validation or file commits.
        _pendingPointPublication |= pointsChanged;
        var now = _presentationTime.GetTimestamp();
        if (!force && _hasProgressTimestamp
            && _presentationTime.GetElapsedTime(_lastProgressTimestamp, now) < TimeSpan.FromMilliseconds(80)) return;
        _lastProgressTimestamp = now; _hasProgressTimestamp = true;
        Publish(reusePoints: !_pendingPointPublication);
    }

    private void Publish(bool reusePoints = false)
    {
        var c = _engine.Context; var candidate = _engine.Candidate; var bases = _engine.ActiveOffsets;
        var offsetEdit=_engine.HasPendingOffsetEdit;
        ST_MANUAL_BULK_PREVIEW? bulk=null;
        var cellBulkCounts=new Dictionary<int,int>();
        var cellBulkTexts=new Dictionary<int,string>();
        if(_source is not null && !offsetEdit && !IsBusy)
            try
            {
                var cells=_engine.PreviewBulkCorrectionByCell();
                foreach(var (cellNo,cell) in cells)
                {
                    cellBulkCounts[cellNo]=cell.Keys.Count;
                    cellBulkTexts[cellNo]=$"Cell #{cellNo} {cell.Keys.Count:N0}점 · NG {cell.ExcludedNg:N0} / 미측정 {cell.ExcludedUnmeasured:N0} / 수치한계 {cell.ExcludedNumeric:N0} / 적용됨 {cell.ExcludedConsumed+cell.ExcludedUnchanged:N0} 제외";
                }
                // Preserve legacy model-summary fields from the same per-cell pass, not
                // by recomputing every value/proof or rebuilding a full Init set per cell.
                var all=cells.Values.ToArray();
                bulk=new(all.SelectMany(p=>p.Keys).ToArray(),all.Sum(p=>p.Total),all.Sum(p=>p.ExcludedMasked),all.Sum(p=>p.ExcludedNg),
                    all.Sum(p=>p.ExcludedUnmeasured),all.Sum(p=>p.ExcludedConsumed),all.Sum(p=>p.ExcludedUnchanged),all.Sum(p=>p.ExcludedInvalid),all.Sum(p=>p.ManualCount))
                    {ExcludedNumeric=all.Sum(p=>p.ExcludedNumeric)};
            }
            catch(InvalidOperationException) { }
        var candidates = candidate?.Rows.ToDictionary(r => r.Key, StringComparer.Ordinal) ?? [];
        var initializedKeys=candidate?.InitializedOffsetKeys.ToHashSet(StringComparer.Ordinal) ?? [];
        var resetKeys=candidate?.InitializationOffsetKeys.ToHashSet(StringComparer.Ordinal) ?? [];
        var points = reusePoints ? Snapshot.Points : _engine.Points.Select(p =>
        {
            var b = bases.GetValueOrDefault(p.Key); var f = candidates.GetValueOrDefault(p.Key)?.Final;
            var editing=offsetEdit && (!candidate!.IsMeasurementOffsetInitialization || candidate.InitializeAllReviewRows || resetKeys.Contains(p.OffsetKey));
            var initialized=editing ? initializedKeys.Contains(p.OffsetKey) : p.IsMeasurementOffsetInitialized;
            if (offsetEdit && f is { } workingOffset) b=workingOffset;
            var correctionBased = !initialized && !p.IsMeasurementStateCleared && !p.Masked && _source?.AppliedOffsetKeys.Contains(p.Key)==true
                && !(p.Judge is "OK" or "NG" && p.HasFiniteError);
            ST_MANUAL_REFERENCE_VALUE? reference = null;
            var referenceReason = "";
            if (p.IsMeasurementStateCleared) referenceReason = "빈 상태는 복사할 수 있으며 실제 모델 Offset은 유지합니다.";
            else if (p.IsMeasurementOffsetInitialized) referenceReason = "초기화·미측정 상태에는 복사할 값이 없습니다.";
            else if (!(p.IsDirectMeasurement && p.HasFiniteError) && p.ProxySource is null && p.OperatorResult is null && !p.HasAppliedOffsetEvidence)
                referenceReason = "현재 모델에서 새로 측정하거나 저장된 보정값이 필요합니다.";
            else
                try { reference = _engine.ResolveReferenceValue(p.Key); }
                catch (InvalidOperationException error) { referenceReason = error.Message; }
            var projected=new CManualInspectionUiPoint
            {
                Key=p.Key,CellNo=p.CellNo,HeadNo=p.HeadNo,ShotNo=p.ShotNo,BeamNo=p.BeamNo,Row=p.Row,Column=p.Column,IsMasked=p.Masked,
                IsMeasurementOffsetInitialized=initialized,
                IsMeasurementStateCleared=p.IsMeasurementStateCleared,CanCopyReferenceState=_engine.CanCopyReferenceState(p.Key),
                IsOperatorResult=p.OperatorResult is not null,OperatorReason=p.OperatorResult?.Reason ?? "",
                DesignXmm=p.XMm,DesignYmm=p.YMm,WorkingJudge=p.Judge,OriginalJudge=p.OriginalMeasurement?.Judge ?? (p.Source == "IMPORTED" ? "UNKNOWN" : "UNMEASURED"),
                WorkingXUm=p.ErrorXUm,WorkingYUm=p.ErrorYUm,OriginalXUm=p.OriginalMeasurement?.ErrorXUm,OriginalYUm=p.OriginalMeasurement?.ErrorYUm,
                BaseXUm=b.XUm,BaseYUm=b.YUm,CandidateXUm=f?.XUm,CandidateYUm=f?.YUm,Source=p.Source,IsProxy=p.ProxySource is not null,
                HasAppliedOffset=_source?.AppliedOffsetKeys.Contains(p.Key)==true,
                CanUseImportedForCorrection=p.AdoptedFileMeasurement is not null && p.IsDirectMeasurement,
                AppliedLevel=!initialized && !p.IsMeasurementStateCleared && !p.Masked && _source?.AppliedOffsetKeys.Contains(p.Key)==true ? CManualInspectionLevels.Evaluate(b.XUm,b.YUm,_levelMetric,_levelBounds) : null,
                AppliedMetricUm=!initialized && !p.IsMeasurementStateCleared && !p.Masked && _source?.AppliedOffsetKeys.Contains(p.Key)==true ? CManualInspectionLevels.Metric(b.XUm,b.YUm,_levelMetric) : null,
                DisplayedMeasurementOrigin=p.OperatorResult is not null?"수동 OK 완료 · 원측정 보존":p.StateCopySource is not null ? "대표 빈 상태 복사 · " + p.StateCopySource.ReferenceKey : correctionBased ? "보정기반 측정 · 기존 적용 Review Offset" : p.ProxySource is not null ? "대표값 복사 · " + p.ProxySource.ReferenceKey
                    : p.Source=="MEASURED" ? "직접 측정 · Manual Inspection" : _displayedImports.GetValueOrDefault(p.Key)?.Origin ?? "",
                MeasurementSourcePath=correctionBased ? _source?.Context.RevisionId ?? "" : p.Source=="IMPORTED" ? _displayedImports.GetValueOrDefault(p.Key)?.Path ?? "" : "",
                ReferenceFinalXUm=reference?.Final.XUm,ReferenceFinalYUm=reference?.Final.YUm,
                ReferenceValueKind=reference?.Kind ?? "",ReferenceValueJudge=reference?.Judge ?? "",ReferenceValueUnavailableReason=referenceReason,
                ReferenceKey=p.ProxySource?.ReferenceKey ?? p.StateCopySource?.ReferenceKey ?? "",MeasurementToken=p.CorrectionMeasurement?.RawToken ?? "",
                Details=$"DESIGN µm · {p.Judge}\n원측정: {p.OriginalMeasurement?.RawData}\n파일 입력: {p.AdoptedFileMeasurement?.RawData}\n{p.ProxySource?.Reason}\n수동 완료: {p.OperatorResult?.Reason}\n이전 기준 수동값(정보만): {p.ArchivedOperatorResult?.FinalOffset} {p.ArchivedOperatorResult?.Reason}"
            };
            return editing ? projected with
            {
                WorkingJudge="UNMEASURED",WorkingXUm=null,WorkingYUm=null,IsProxy=false,ReferenceKey="",
                IsMeasurementStateCleared=false,CanCopyReferenceState=false,
                IsOperatorResult=false,OperatorReason="",
                Source=candidate!.IsMeasurementOffsetInitialization?"OFFSET_INIT":"OFFSET_FILE",CanUseImportedForCorrection=false,
                HasAppliedOffset=!p.Masked,AppliedLevel=p.Masked||initialized?null:CManualInspectionLevels.Evaluate(b.XUm,b.YUm,_levelMetric,_levelBounds),
                AppliedMetricUm=p.Masked||initialized?null:CManualInspectionLevels.Metric(b.XUm,b.YUm,_levelMetric),
                ReferenceFinalXUm=null,ReferenceFinalYUm=null,
                ReferenceValueUnavailableReason="작업 Offset을 저장하거나 편집을 취소한 뒤 복사하세요.",
                DisplayedMeasurementOrigin="작업 통합 Offset · 미저장 · 실제 활성값은 유지",MeasurementSourcePath="저장 전 작업값"
            } : projected;
        }).ToArray();
        if (!reusePoints) _pendingPointPublication = false;
        var op = _engine.Operation; var validation = candidate?.Validation;
        var final=_finalOffsets is not null && c is not null && SameSavedFrame(c,_finalOffsets)?_finalOffsets:null;
        Snapshot = new()
        {
            HasFinalOffsetSnapshot=final is not null,
            FinalOffsetPoints=final is null?new Dictionary<string,CManualInspectionUiOffset>(StringComparer.Ordinal)
                :_finalUiPoints,
            FinalInitializedOffsetKeys=final?.InitializedOffsetKeys??FrozenSet<string>.Empty,FinalInitializedPointKeys=final is null?FrozenSet<string>.Empty:_finalInitializedPointKeys,
            FinalAppliedPointKeys=final?.AppliedPointKeys??FrozenSet<string>.Empty,
            FinalOffsetFilePath=final?.FilePath??"",FinalOffsetFileName=final?.FileName??"",FinalOffsetSavedAt=final?.SavedAt,
            FinalOffsetContentSha256=final?.ContentSha256??"",FinalOffsetRevisionId=final?.RevisionId??"",
            FinalOffsetRowCount=final?.RowCount??0,FinalOffsetInitializedRowCount=final?.InitializedRowCount??0,FinalOffsetStatusText=_finalOffsetStatus,
            LastSavedRevisionId=HasCurrentSaveConfirmation?_lastSavedContext!.RevisionId:"",
            LastSaveVerificationCode=HasCurrentSaveConfirmation?_lastSaveVerificationCode:"NONE",
            LastSaveVerificationText=HasCurrentSaveConfirmation?_lastSaveVerificationText:"",
            CanReturnToPreviousWorkspace=_previousWorkspace is not null,
            IsOffsetEditPending=offsetEdit,OffsetInitPending=offsetEdit&&candidate!.IsMeasurementOffsetInitialization,
            MeasurementSequence=_measurementSequence,LastMeasuredPointKey=_lastMeasuredPointKey,LastMeasurementText=_lastMeasurementText,
            IsMeasurementInProgress=_isMeasurementInProgress,ActiveMeasurementPointKey=_activeMeasurementPointKey,
            ActiveMeasurementPointKeys=_activeMeasurementPointKeys,
            OffsetSummaryText=offsetEdit?"작업 통합 Offset · 미저장 · 모델 Offset 저장 시 반영 · HW/Default/First/Distortion 유지"
                :"모델별 최신 누적 Review Offset 하나 · Simple/Rough/Fine/One/All/Line 통합 · 보호 Offset 별도 유지",
            CellLayouts=_source?.CellLayouts ?? [],GlassWidthMm=_source?.GlassWidthMm ?? 0,GlassHeightMm=_source?.GlassHeightMm ?? 0,
            Version=++_version,RecipeId=c?.RecipeId ?? "—",RunId=c?.RunId ?? "—",CoordinateSignature=c?.CoordinateSignature ?? "",ActiveRevisionId=c?.RevisionId ?? "—",
            SourcePath=_source?.SourcePath ?? "",IsSimulation=_backend.IsSimulation,IsBusy=IsBusy,
            SyncPending=_syncPending,SynchronizationText=_synchronizationText,ImportedResultCount=_source?.ImportedResults.Count ?? 0,
            RecentResults=_source?.RecentResults.Select(r=>new CManualInspectionUiResultFile(r.FilePath,
                $"{r.SavedAt.ToLocalTime():MM-dd HH:mm:ss} · {r.ResultType} · {r.GlassId} · {r.FileName}",r.GlassId,r.SavedAt,r.Source+" / "+r.ResultType)).ToArray() ?? [],
            IsReadOnly=false,HasIncomplete=_engine.HasIncompleteMeasurements,
            RemainingCount=op?.RemainingKeys.Count ?? 0,CompletedCount=op?.CompletedKeys.Count ?? 0,TotalCount=op?.Keys.Count ?? 0,
            StageXmm=_stageX,StageYmm=_stageY,StatusText=_status,Message=_message,PersistenceText=_persistence,Points=points,
            CandidateRows=candidate?.Rows.Select(r=>new CManualInspectionUiCandidateRow { Key=r.Key,Source=r.Source,BaseXUm=r.Base.XUm,BaseYUm=r.Base.YUm,
                DeltaXUm=r.Delta.XUm,DeltaYUm=r.Delta.YUm,FinalXUm=r.Final.XUm,FinalYUm=r.Final.YUm }).ToArray() ?? [],
            Revisions=_engine.Revisions.Select(r=>new CManualInspectionUiRevision { Id=r.Id,CreatedAt=r.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"),Reason=r.Reason }).ToArray(),
            CandidateStatus=candidate?.State.ToString().ToUpperInvariant() ?? "후보 없음",ValidationPassed=validation?.Ok==true,
            CanApply=candidate?.State==EN_MANUAL_CANDIDATE_STATE.Validated && validation?.Ok==true && !_engine.HasIncompleteMeasurements && c?.LineageValid==true && _source?.ReadOnly!=true,
            CanRollback=!offsetEdit && _engine.CanUndoOffsets && !IsBusy && _source?.ReadOnly!=true,
            CanUndoOffsets=!offsetEdit && _engine.CanUndoOffsets && !IsBusy && _source?.ReadOnly!=true,
            CanRedoOffsets=!offsetEdit && _engine.CanRedoOffsets && !IsBusy && _source?.ReadOnly!=true,
            UndoOffsetCount=_engine.UndoCount,RedoOffsetCount=_engine.RedoCount,
            CanBulkApply=!offsetEdit && !IsBusy && !_engine.HasIncompleteMeasurements && c?.LineageValid==true && _source?.ReadOnly!=true && bulk?.Keys.Count>0,
            BulkEligibleCount=bulk?.Keys.Count ?? 0,
            CellBulkEligibleCounts=cellBulkCounts,CellBulkSummaryTexts=cellBulkTexts,
            BulkSummaryText=bulk is null?"측정된 보정 대상 없음":$"모델 전체 {bulk.Keys.Count:N0}점 · NG {bulk.ExcludedNg:N0} / 미측정 {bulk.ExcludedUnmeasured:N0} / 수치한계 {bulk.ExcludedNumeric:N0} / 적용됨 {bulk.ExcludedConsumed+bulk.ExcludedUnchanged:N0} 제외",
            OffsetHistoryText=$"되돌리기 {_engine.UndoCount}/10 · 다시 적용 {_engine.RedoCount}회",
            CanUndoCopy=_engine.CanUndoReferenceCopy,AllowManual=_allowManual,
            ValidationErrors=validation?.Errors ?? [],ValidationWarnings=validation?.Warnings ?? [],MaxCandidateDeltaUm=validation?.MaxDeltaUm ?? 0,
            MaxCandidateTotalUm=validation?.MaxTotalUm ?? 0,ToleranceXUm=_source?.ToleranceXUm ?? 5,ToleranceYUm=_source?.ToleranceYUm ?? 5,
            DeltaLimitUm=_engine.Limits?.MaxDeltaUm ?? 30,TotalLimitUm=_engine.Limits?.MaxTotalUm ?? 200,ScannerFieldMm=_engine.Limits?.FieldWidthMm ?? 5,
            LevelMetric=_levelMetric,LevelBoundsUm=_levelBounds.ToArray(),LevelNames=_levelNames.ToArray()
        };
        // UI/log observers do not own the equipment command gate. Isolate each observer so a
        // rendering exception cannot strand STOP cleanup or conceal a successfully saved offset.
        if (Changed is { } changed)
            foreach (EventHandler observer in changed.GetInvocationList())
                try { observer(this, EventArgs.Empty); }
                catch (Exception error) { TraceObserverFailure("snapshot observer", error); }
    }
    private static void TraceObserverFailure(string observer, Exception error)
    {
        try { System.Diagnostics.Trace.TraceError($"Manual Inspection {observer}: {error}"); }
        catch { /* A broken diagnostic listener must not acquire control of a device transaction. */ }
    }
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action(); else dispatcher.Invoke(action, DispatcherPriority.Normal);
    }
    private static string? DefaultChooseFile(bool save, string? extension)
    {
        var filter = extension=="offset" ? "통합 Offset (*.csv;*.json)|*.csv;*.json|CSV (*.csv)|*.csv|Offset JSON (*.json)|*.json"
            : extension=="csv" ? "CSV (*.csv)|*.csv" : "Native workspace JSON (*.json)|*.json";
        if (save) { var d=new SaveFileDialog { Filter=filter,DefaultExt="."+extension,FileName="A3_Manual_Inspection_"+DateTime.Now.ToString("yyyyMMdd_HHmmss") }; return d.ShowDialog()==true?d.FileName:null; }
        var open=new OpenFileDialog { Filter=filter,CheckFileExists=true }; return open.ShowDialog()==true?open.FileName:null;
    }
}
