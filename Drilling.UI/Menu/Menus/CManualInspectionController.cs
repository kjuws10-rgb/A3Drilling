using System.Globalization;
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
    private readonly CManualInspectionWorkspace _engine = new();
    private readonly SemaphoreSlim _command = new(1, 1);
    private readonly Action<string> _report;
    private CManualInspectionSource? _source;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _idle;
    private bool _busy, _allowManual, _waitForControlStop;
    private long _version;
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
    private Dictionary<string, CManualInspectionImportedResult> _displayedImports = new(StringComparer.Ordinal);
    public CManualInspectionUiSnapshot Snapshot { get; private set; } = new();
    public event EventHandler? Changed;
    public bool IsBusy => _busy || _engine.IsBusy;
    public Func<bool, string?, string?> ChooseFile { get; set; } = DefaultChooseFile;
    public Action<string, string> ShowDetails { get; set; } = (title, text) =>
        Popup.CRecipeConfirmDialog.ShowWarning(title, text);

    public CManualInspectionController(IManualInspectionBackend backend, Action<string>? report = null)
    { _production = _backend = backend; _report = report ?? (_ => { }); Publish(); }

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
        if (_explicitResultView)
        { _synchronizationText = "선택한 파일 조회 유지 · 현재 기판의 최신 결과는 결과 새로고침에서 확인하세요."; Publish(reusePoints: true); return; }
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
            _syncPending = true; _synchronizationText = "동기화 중지 · 새로고침 완료 전 조회 전용";
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
        if (request.ExpectedVersion != Snapshot.Version) throw new InvalidOperationException("선택/확인 뒤 작업 기준이 바뀌었습니다. 현재 화면에서 다시 요청하세요.");
        if (!await _command.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("다른 작업이 진행 중입니다.");
        _busy = true; _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopRequestError = "";
        // A modal information/export dialog owns the UI command gate, but no device or offset write.
        // Heavy-alarm shutdown must wait for actual control/write cleanup, not a user closing a dialog.
        _waitForControlStop = action is EN_MANUAL_INSPECTION_UI_ACTION.Move
            or EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure or EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect
            or EN_MANUAL_INSPECTION_UI_ACTION.Measure or EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining
            or EN_MANUAL_INSPECTION_UI_ACTION.Apply or EN_MANUAL_INSPECTION_UI_ACTION.Rollback;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cancellation.Token;
        try
        {
            if (action is not (EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint or EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv or EN_MANUAL_INSPECTION_UI_ACTION.ExportSession))
                Publish();
            switch (action)
            {
                case EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe:
                case EN_MANUAL_INSPECTION_UI_ACTION.Refresh:
                    await LoadSource(null, token); break;
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
                case EN_MANUAL_INSPECTION_UI_ACTION.Calculate:
                    await Current(token); _engine.BuildDirectCandidate(request.Keys); _status = "선택점의 보정 후보를 계산했습니다. 검증 후 적용하세요."; break;
                case EN_MANUAL_INSPECTION_UI_ACTION.Validate:
                    await Current(token); _allowManual = request.AllowManual;
                    var check = _engine.ValidateCandidate(_allowManual);
                    _status = check.Ok ? "현재 후보 검증 통과 · 아직 활성 보정에 저장하지 않았습니다." : string.Join("\n", check.Errors); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.Apply:
                    _allowManual = request.AllowManual; await Apply(token); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.Rollback: await Restore(token); break;
                case EN_MANUAL_INSPECTION_UI_ACTION.CopyReference:
                    await Current(token);
                    var preview = _engine.PreviewReferenceCopy(request.ReferenceKey ?? "", request.Keys);
                    _engine.ConfirmReferenceCopy(preview, request.Keys, request.Reason);
                    _allowManual = true;
                    _status = $"{preview.Targets.Count:N0}점의 작업 판정·편차·최종 F를 대표와 같게 반영했습니다. 원측정은 보존했습니다."; break;
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
                case EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect:
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
                if (_source is not null) _source = _source with { ReadOnly = true };
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

    private async Task LoadSource(string? path, CancellationToken token, bool keepBackend = false, bool automatic = false)
    {
        var previous = _source is not null ? _engine.CaptureSession() : null;
        if (previous is not null) SaveLocalSession();
        if (!keepBackend) _backend = _production;
        _status = "실제 Recipe·Setting·보정·측정 위치를 확인하는 중…"; Publish();
        var next = await _backend.LoadAsync(path, token);
        if (automatic && _source is not null && !_source.ReadOnly
            && next.ObservationFingerprint.Length > 0 && next.ObservationFingerprint == _source.ObservationFingerprint
            && SameFrame(_source.Context, next.Context))
        { _source = next; ReconcileDisplayedImports(next); return; }
        var sameFrame = previous is not null && SameFrame(previous.Context, next.Context);
        _engine.Load(next.Context, next.Points, next.Values, next.Limits, next.FieldEvaluator);
        _source = next; _allowManual = false; _explicitResultView = path is not null;
        _stageX = _stageY = double.NaN;
        var saved = SessionPath();
        if (path is null && (sameFrame || System.IO.File.Exists(saved)))
        {
            try
            {
                if (sameFrame)
                    _engine.RestoreSession(ExpandSession(previous!, next), next.Context, next.Points, next.Values, next.FieldEvaluator);
                else
                {
                    var bundle = JsonSerializer.Deserialize<SessionBundle>(await System.IO.File.ReadAllTextAsync(saved, token))
                        ?? throw new InvalidDataException("빈 보관 자료");
                    _engine.RestoreSession(ExpandSession(bundle.Session, next), next.Context, next.Points, next.Values, next.FieldEvaluator);
                    RestoreOptions(bundle);
                }
                _engine.MergeImportedObservations(next.Points, next.ImportedResults.ToDictionary(p => p.Key, p => p.Value.SavedAt));
                _status = "최근 외부 측정과 같은 기준의 작업 상태를 함께 표시했습니다. 자동 동작은 시작하지 않습니다.";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                // Keep the conflicting saved file and do not quietly clear an interrupted same-run session.
                _source = next with { ReadOnly = true };
                _status = "보관 세션과 현재 기준이 다릅니다. 현재 좌표는 조회 전용입니다.\n" + e.Message;
            }
        }
        else _status = next.Description + (next.ReadOnly ? " · 다른 기판 자료 조회 전용" : " · 실제 설정 기준");
        _engine.SeedConsumedMeasurements(_backend.ConsumedMeasurements);
        if (!sameFrame) _displayedImports.Clear();
        ReconcileDisplayedImports(next);
        _syncPending = _engine.HasIncompleteMeasurements && next.ImportedResults.Count > 0;
        if (automatic) _syncQueued |= _syncPending;
        else _syncQueued = _syncPending;
        _synchronizationText = path is null ? "최근 측정·현재 활성 보정 표시 · " + next.Description
            : "선택 결과 조회 · " + next.SourcePath;
        if (_syncPending) _synchronizationText = "외부 결과 확인 대기 · 미완료 측정을 보존했습니다. 잔여 측정 후 갱신합니다.";
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
        if (_source.ReadOnly) throw new InvalidOperationException("조회 전용 자료입니다. 현재 기판/Recipe 기준을 다시 불러오세요.");
        _backend.GuardOperation(); await _backend.EnsureCurrentAsync(_source, token);
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
            EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect => EN_MANUAL_OPERATION_KIND.MeasureAndCorrect,
            _ => flying ? EN_MANUAL_OPERATION_KIND.Flying : EN_MANUAL_OPERATION_KIND.MoveMeasure
        };
        var operation = _engine.BeginOperation(_engine.PreviewOperation(kind, keys, retry));
        try
        {
            if (_backend is CManualInspectionReplayBackend replay) replay.Configure(request with { Keys = keys, Fault = retry ? "NONE" : request.Fault });
            _status = $"{kind} · {operation.Keys.Count:N0}점 · 완료 후 정리 확인까지 탭을 잠급니다.";
            SaveLocalSession(); Publish();
            void Received(CManualInspectionMeasurement value) => OnUi(() =>
            {
                token.ThrowIfCancellationRequested();
                _engine.RecordMeasurement(value.Key, value.Measurement, operation.Id);
                if (value.Measurement.Judge is not ("OK" or "NG"))
                    throw new InvalidDataException($"{value.Key}: 유효한 측정 응답이 없습니다.");
                _engine.MarkOperationPointCompleted(operation.Id, value.Key);
                _stageX = value.ReviewXmm; _stageY = value.StageYmm;
                _status = $"{_engine.Operation!.CompletedKeys.Count:N0} / {operation.Keys.Count:N0}점 · {value.Key} · {value.Measurement.Judge}";
                Publish();
                if (DateTimeOffset.UtcNow - _lastCheckpoint > TimeSpan.FromSeconds(2)) SaveLocalSession();
            });
            void Progress(string text) => OnUi(() => { _status = text; Publish(reusePoints: true); });
            if (flying) await _backend.ExecuteFlyingAsync(_source!, keys, Received, Progress, token);
            else foreach (var key in operation.Keys)
            {
                token.ThrowIfCancellationRequested();
                var value = await _backend.ExecutePointAsync(_source!, key, kind, Progress, token);
                if (kind == EN_MANUAL_OPERATION_KIND.Move)
                { _engine.MarkOperationPointCompleted(operation.Id, key); _stageX = value.ReviewXmm; _stageY = value.StageYmm; Publish(reusePoints: true); }
                else Received(value);
            }
            var completed = _engine.CompleteOperation(operation.Id);
            if (completed.AutoCorrectionAllowed)
            {
                _allowManual = false;
                _engine.BuildDirectCandidate(operation.Keys);
                var validation = _engine.ValidateCandidate(false);
                if (!validation.Ok) { _status = "측정은 완료했지만 자동 보정 검증에 실패했습니다.\n" + string.Join("\n", validation.Errors); return; }
                await Apply(token);
            }
            else _status = "측정/이동 완료 · 활성 보정 변경 없음" + (completed.Errors.Count > 0 ? "\n" + string.Join("\n", completed.Errors) : "");
        }
        catch (Exception e)
        {
            if (_engine.Operation?.State == EN_MANUAL_OPERATION_STATE.Running)
                _engine.CompleteOperation(operation.Id, e is OperationCanceledException ? EN_MANUAL_OPERATION_STATE.Stopped : EN_MANUAL_OPERATION_STATE.Error, e.Message);
            throw;
        }
    }

    private async Task Apply(CancellationToken token)
    {
        await Current(token);
        var snapshot = _engine.CreateApplySnapshot(_allowManual);
        try
        {
            SaveLocalSession(); Publish(reusePoints: true);
            var identity = JsonSerializer.Serialize(new { Kind = "MANUAL_INSPECTION", snapshot.RecipeId, snapshot.RunId,
                Tokens = snapshot.Rows.Select(r => new { r.Key, r.MeasurementToken, r.Source, r.ReferenceKey, r.Reason, r.Final,
                    // A representative can be remeasured after the copy. Audit the raw captured by
                    // that copy, not a newer raw currently displayed at the same reference key.
                    ReferenceRaw = r.Source == "MANUAL" ? _engine.GetPoint(r.Key).ProxySource?.ReferenceMeasurementToken
                        : r.MeasurementToken }).OrderBy(r => r.Key) });
            var revision = await _backend.SaveAsync(_source!, snapshot.ValuesByPointKey, identity, snapshot.Rows, token, snapshot.ConsumedMeasurements);
            _engine.CommitApplied(snapshot, revision, "Manual Inspection 검증된 보정 적용",
                _backend.LastSavedValues.Count > 0 ? _backend.LastSavedValues : null);
            var written = snapshot.Rows.Where(r => r.Base != r.Final).Select(r => r.OffsetKey).ToHashSet(StringComparer.Ordinal);
            _source = _source! with
            {
                Context = _engine.Context!, Values = _engine.ActiveOffsets,
                AppliedOffsetKeys = _source.AppliedOffsetKeys.Concat(_source.Points.Where(p => written.Contains(p.OffsetKey)).Select(p => p.Key)).ToHashSet(StringComparer.Ordinal)
            };
            _status = "보정 적용·저장 완료 · 새 Revision · 기존 파일/원측정 보존";
        }
        catch { _engine.AbortApply(snapshot); throw; }
    }

    private async Task Restore(CancellationToken token)
    {
        await Current(token); var snapshot = _engine.CreateRestorePreviousSnapshot();
        try
        {
            SaveLocalSession(); Publish(reusePoints: true);
            var revision = await _backend.SaveAsync(_source!, snapshot.ValuesByPointKey,
                JsonSerializer.Serialize(new { Kind = "RESTORE", snapshot.RecipeId, snapshot.RunId, snapshot.BaseRevisionId, snapshot.RestoreTargetRevisionId }), [], token);
            _engine.CommitRestored(snapshot, revision, "사용자 요청 · 직전 활성 보정값 새 버전 복원",
                _backend.LastSavedValues.Count > 0 ? _backend.LastSavedValues : null);
            _source = _source! with { Context = _engine.Context!, Values = _engine.ActiveOffsets };
            _status = "직전 보정값을 새 Revision으로 원복했습니다. 측정·이력·잔여점은 보존했습니다.";
        }
        catch { _engine.AbortRestore(snapshot); throw; }
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
        _message = _status; Publish(reusePoints: true);
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
        _engine.RestoreSession(bundle.Session, next.Context, next.Points, next.Values, next.FieldEvaluator);
        _engine.SeedConsumedMeasurements(_backend.ConsumedMeasurements);
        _source = next; RestoreOptions(bundle); _status = "실제 Recipe/Run/보정과 대조해 세션을 불러왔습니다. 자동 동작은 재개하지 않습니다.";
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
    private string SessionPath() => Path.Combine(_backend.StateDirectory, "Sessions",
        CManualInspectionBackend.Digest(_engine.Context!.RecipeId + "|" + _engine.Context.RunId
            + "|" + _engine.Context.CoordinateSignature + "|" + _engine.Context.RevisionId) + ".json");
    private void SaveLocalSession()
    {
        if (_source is null || _source.ReadOnly) return;
        CManualInspectionBackend.AtomicText(SessionPath(), JsonSerializer.Serialize(Bundle(), JsonOptions));
        _lastCheckpoint = DateTimeOffset.UtcNow; _persistence = "자동 보관 " + DateTimeOffset.Now.ToString("HH:mm:ss");
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public sealed record SessionBundle(int SchemaVersion, bool Simulation, string Scenario, ST_MANUAL_SESSION Session,
        bool AllowManual, string LevelMetric, double[] LevelBounds, string[] LevelNames);

    private void Publish(bool reusePoints = false)
    {
        var c = _engine.Context; var candidate = _engine.Candidate; var bases = _engine.ActiveOffsets;
        var candidates = candidate?.Rows.ToDictionary(r => r.Key, StringComparer.Ordinal) ?? [];
        var points = reusePoints ? Snapshot.Points : _engine.Points.Select(p =>
        {
            var b = bases.GetValueOrDefault(p.Key); var f = candidates.GetValueOrDefault(p.Key)?.Final;
            var correctionBased = !p.Masked && _source?.AppliedOffsetKeys.Contains(p.Key)==true
                && !(p.Judge is "OK" or "NG" && p.HasFiniteError);
            ST_MANUAL_OFFSET? reference = null;
            if (!p.Masked && p.IsDirectMeasurement && p.HasFiniteError && p.Judge is "OK" or "NG")
                try { reference = _engine.ResolveReferenceFinal(p.Key); } catch (InvalidOperationException) { }
            return new CManualInspectionUiPoint
            {
                Key=p.Key,CellNo=p.CellNo,HeadNo=p.HeadNo,ShotNo=p.ShotNo,BeamNo=p.BeamNo,Row=p.Row,Column=p.Column,IsMasked=p.Masked,
                DesignXmm=p.XMm,DesignYmm=p.YMm,WorkingJudge=p.Judge,OriginalJudge=p.OriginalMeasurement?.Judge ?? (p.Source == "IMPORTED" ? "UNKNOWN" : "UNMEASURED"),
                WorkingXUm=p.ErrorXUm,WorkingYUm=p.ErrorYUm,OriginalXUm=p.OriginalMeasurement?.ErrorXUm,OriginalYUm=p.OriginalMeasurement?.ErrorYUm,
                BaseXUm=b.XUm,BaseYUm=b.YUm,CandidateXUm=f?.XUm,CandidateYUm=f?.YUm,Source=p.Source,IsProxy=p.ProxySource is not null,
                HasAppliedOffset=_source?.AppliedOffsetKeys.Contains(p.Key)==true,
                AppliedLevel=!p.Masked && _source?.AppliedOffsetKeys.Contains(p.Key)==true ? CManualInspectionLevels.Evaluate(b.XUm,b.YUm,_levelMetric,_levelBounds) : null,
                AppliedMetricUm=!p.Masked && _source?.AppliedOffsetKeys.Contains(p.Key)==true ? CManualInspectionLevels.Metric(b.XUm,b.YUm,_levelMetric) : null,
                DisplayedMeasurementOrigin=correctionBased ? "보정기반 측정 · 기존 적용 Review Offset" : p.ProxySource is not null ? "대표값 복사 · " + p.ProxySource.ReferenceKey
                    : p.Source=="MEASURED" ? "직접 측정 · Manual Inspection" : _displayedImports.GetValueOrDefault(p.Key)?.Origin ?? "",
                MeasurementSourcePath=correctionBased ? _source?.Context.RevisionId ?? "" : p.Source=="IMPORTED" ? _displayedImports.GetValueOrDefault(p.Key)?.Path ?? "" : "",
                ReferenceFinalXUm=reference?.XUm,ReferenceFinalYUm=reference?.YUm,
                ReferenceKey=p.ProxySource?.ReferenceKey ?? "",MeasurementToken=p.OriginalMeasurement?.RawToken ?? "",
                Details=$"DESIGN µm · {p.Judge}\n원측정: {p.OriginalMeasurement?.RawData}\n{p.ProxySource?.Reason}"
            };
        }).ToArray();
        var op = _engine.Operation; var validation = candidate?.Validation;
        Snapshot = new()
        {
            Version=++_version,RecipeId=c?.RecipeId ?? "—",RunId=c?.RunId ?? "—",ActiveRevisionId=c?.RevisionId ?? "—",
            SourcePath=_source?.SourcePath ?? "",IsSimulation=_backend.IsSimulation,IsBusy=IsBusy,
            SyncPending=_syncPending,SynchronizationText=_synchronizationText,ImportedResultCount=_source?.ImportedResults.Count ?? 0,
            RecentResults=_source?.RecentResults.Select(r=>new CManualInspectionUiResultFile(r.FilePath,
                $"{r.SavedAt.ToLocalTime():MM-dd HH:mm:ss} · {r.ResultType} · {r.GlassId} · {r.FileName}",r.GlassId,r.SavedAt,r.Source+" / "+r.ResultType)).ToArray() ?? [],
            IsReadOnly=_source?.ReadOnly == true || c?.LineageValid == false,HasIncomplete=_engine.HasIncompleteMeasurements,
            RemainingCount=op?.RemainingKeys.Count ?? 0,CompletedCount=op?.CompletedKeys.Count ?? 0,TotalCount=op?.Keys.Count ?? 0,
            StageXmm=_stageX,StageYmm=_stageY,StatusText=_status,Message=_message,PersistenceText=_persistence,Points=points,
            CandidateRows=candidate?.Rows.Select(r=>new CManualInspectionUiCandidateRow { Key=r.Key,Source=r.Source,BaseXUm=r.Base.XUm,BaseYUm=r.Base.YUm,
                DeltaXUm=r.Delta.XUm,DeltaYUm=r.Delta.YUm,FinalXUm=r.Final.XUm,FinalYUm=r.Final.YUm }).ToArray() ?? [],
            Revisions=_engine.Revisions.Select(r=>new CManualInspectionUiRevision { Id=r.Id,CreatedAt=r.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"),Reason=r.Reason }).ToArray(),
            CandidateStatus=candidate?.State.ToString().ToUpperInvariant() ?? "후보 없음",ValidationPassed=validation?.Ok==true,
            CanApply=candidate?.State==EN_MANUAL_CANDIDATE_STATE.Validated && validation?.Ok==true && !_engine.HasIncompleteMeasurements && c?.LineageValid==true && _source?.ReadOnly!=true,
            CanRollback=_engine.Revisions.Count>1 && !IsBusy && _source?.ReadOnly!=true,CanUndoCopy=_engine.CanUndoReferenceCopy,AllowManual=_allowManual,
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
        var filter = extension=="csv" ? "CSV (*.csv)|*.csv" : "Native workspace JSON (*.json)|*.json";
        if (save) { var d=new SaveFileDialog { Filter=filter,DefaultExt="."+extension,FileName="A3_Manual_Inspection_"+DateTime.Now.ToString("yyyyMMdd_HHmmss") }; return d.ShowDialog()==true?d.FileName:null; }
        var open=new OpenFileDialog { Filter=filter,CheckFileExists=true }; return open.ShowDialog()==true?open.FileName:null;
    }
}
