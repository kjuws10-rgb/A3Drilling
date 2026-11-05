using System.Globalization;
using Drilling.Common.Automation;
using Drilling.Common.Managers;
using Drilling.Common.Station;

namespace Drilling.Common.Interface;

public sealed record ST_POWER_MEASURE_REPORT(string RecipeId, string Origin, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, ST_POWER_MEASURE_SNAPSHOT Snapshot, IReadOnlyDictionary<int, bool> Simulation);

/// <summary>Shared Manual/AUTO transaction. Prepare every route before emission; never moves Stage Y.</summary>
public sealed class CPowerMeasureRun(CManager manager, ST_RECIPE_DEFINITION recipe,
    IReadOnlyList<ST_POWER_MEASURE_TARGET> targets, bool automatic,
    Func<int, bool, IPowerMeasureTable> tables, Action<ST_POWER_MEASURE_REPORT> saveReport) : IAsyncDisposable, IPowerMeasureTable
{
    private readonly CPowerMeasureDeviceSession _session = new(manager, recipe, targets);
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _prepared, _measured, _saved, _reported, _released;
    private ST_POWER_MEASURE_SNAPSHOT? _result;
    public static IReadOnlyList<ST_POWER_MEASURE_TARGET> AutoTargets(CSettingManager settings) => Enumerable.Range(1, 8).Select(head =>
    {
        var toleranceKey = $"H{head:00}_TPM_TOLERANCE_W";
        var attemptsKey = $"H{head:00}_TPM_MAX_ATTEMPTS";
        // Older Setting documents use the common keys. Read that fallback only when a Head key is absent.
        var tolerance = string.IsNullOrWhiteSpace(settings.GetValue(EN_SETTING_TAB.Option, toleranceKey))
            ? Number(settings, "TPM_TOLERANCE_W", .01) : Number(settings, toleranceKey, .01);
        var attempts = string.IsNullOrWhiteSpace(settings.GetValue(EN_SETTING_TAB.Option, attemptsKey))
            ? Integer(settings, "TPM_MAX_ATTEMPTS", 12) : Integer(settings, attemptsKey, 12);
        return new ST_POWER_MEASURE_TARGET(head, head - 1, Number(settings, $"H{head:00}_TPM_TARGET_POWER_W", 1.2),
            new("FINE", Integer(settings, $"H{head:00}_TPM_SETTLE_TIME_MS", 300), Integer(settings, $"H{head:00}_TPM_MEASURE_COUNT", 1), Integer(settings, $"H{head:00}_TPM_INTERVAL_MS", 100))
            {
                ToleranceW = tolerance, MaximumAttempts = attempts,
                CoolingTimeMs = Integer(settings, "TPM_COOLING_TIME_MS", 100)
            });
    }).ToArray();
    internal static double Number(CSettingManager settings, string key, double fallback)
    {
        var text = settings.GetValue(EN_SETTING_TAB.Option, key, fallback.ToString("R", CultureInfo.InvariantCulture));
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0
            ? value : throw new InvalidOperationException($"PowerMeter 설정 오류: {key}={text}");
    }
    internal static int Integer(CSettingManager settings, string key, int fallback)
    {
        var value = Number(settings, key, fallback);
        return value == Math.Truncate(value) && value <= int.MaxValue ? (int)value : throw new InvalidOperationException($"정수 설정이 필요합니다: {key}");
    }
    public async Task Prepare(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_prepared || _released) throw new InvalidOperationException("이미 준비되었거나 종료된 PowerMeasure입니다.");
            CPowerMeasureService.ValidateTargets(targets);
            await _session.PrepareAll(token).ConfigureAwait(false);
            _prepared = true;
            Log("PREPARED", $"Recipe={recipe.Id}; Heads={string.Join(',', targets.Select(t => t.HeadNo))}; Stage Y=0; CENTER(0,0)");
        }
        finally { _gate.Release(); }
    }
    public async Task Measure(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_prepared || _released || _measured) throw new InvalidOperationException("PowerMeasure 준비 상태를 확인해 주세요.");
            await _session.CheckStageReady(token).ConfigureAwait(false);
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
            Exception? stageError = null;
            var watching = Task.Run(async () =>
            {
                try { while (true) { await Task.Delay(100, guard.Token).ConfigureAwait(false); _session.EnsureStageReady(); } }
                catch (OperationCanceledException) when (guard.IsCancellationRequested) { }
                catch (Exception error) { Interlocked.CompareExchange(ref stageError, error, null); guard.Cancel(); }
            });
            // Keep the central Stage frame current without suspending the strict
            // watchdog above while a requested read is waiting for transport.
            var refreshing = Task.Run(async () =>
            {
                try { await _session.MaintainStageFeedback(guard.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (guard.IsCancellationRequested) { }
                catch (Exception error) { Interlocked.CompareExchange(ref stageError, error, null); guard.Cancel(); }
            });
            var started = false;
            try
            {
                var operation = manager.PowerMeasure.Start(targets, _session, this, guard.Token, automatic ? "AUTO TPM" : "MANUAL");
                started = true;
                await operation.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stageError is not null && !token.IsCancellationRequested)
            {
                throw stageError;
            }
            finally
            {
                guard.Cancel(); await Task.WhenAll(watching, refreshing).ConfigureAwait(false);
                if (started) { _result = manager.PowerMeasure.GetSnapshot(); Report(); }
            }
            if (stageError is not null) throw stageError;
            token.ThrowIfCancellationRequested();
            var failed = _result!.Heads.Where(h => h.State != "OK").ToArray();
            if (failed.Length > 0) throw new InvalidOperationException(string.Join("\n", failed.Select(h => $"HEAD {h.Target.HeadNo:00}: {h.State} / {h.Message}")));
            _measured = true;
            Log("MEASURED", $"{_result.Heads.Count} Heads OK");
        }
        catch (Exception error) { if (error is not OperationCanceledException) Log("FAILED", error.Message); throw; }
        finally { _gate.Release(); }
    }
    public async Task Save(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_saved) return;
            if (!_measured || _result is null) throw new InvalidOperationException("정상 완료된 파워 측정 결과가 없습니다.");
            await manager.Recipe().SavePowerMeasureAngles(recipe.Id, _result.Heads.ToDictionary(h => h.Target.HeadNo, h => h.Angle!.Value), token).ConfigureAwait(false);
            _saved = true;
            Log("SAVED", $"Recipe ATT 각도 저장 완료: {recipe.Id}");
        }
        finally { _gate.Release(); }
    }
    public async Task Execute(CancellationToken token)
    {
        try { await Prepare(token).ConfigureAwait(false); await Measure(token).ConfigureAwait(false); await Save(token).ConfigureAwait(false); }
        finally { await DisposeAsync().ConfigureAwait(false); }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_released) return;
            await _session.DisposeAsync().ConfigureAwait(false);
            _released = true;
            if (_result is not null && !_reported) Report();
        }
        finally { _gate.Release(); }
    }
    private void Report()
    {
        if (_reported || _result is null) return;
        saveReport(new(recipe.Id, automatic ? "AUTO" : "MANUAL", _started, DateTimeOffset.Now, _result,
            targets.ToDictionary(t => t.HeadNo, t => _session.IsSimulation(t.HeadNo))));
        _reported = true;
    }
    private void Log(string state, string message) { try { manager.Log().WriteStationState("POWERMEASURE", state, "TPM", message); } catch { } }
    public ST_POWER_MEASURE_START FindStart(int headNo, double power, CancellationToken token) => tables(headNo, _session.IsSimulation(headNo)).FindStart(headNo, power, token);
    public void SaveMeasurement(int headNo, double angle, double measuredPower, CancellationToken token) => tables(headNo, _session.IsSimulation(headNo)).SaveMeasurement(headNo, angle, measuredPower, token);
}

internal sealed class CPowerMeasureDeviceSession(CManager manager, ST_RECIPE_DEFINITION recipe, IReadOnlyList<ST_POWER_MEASURE_TARGET> targets) : IPowerMeasureSession, IAsyncDisposable
{
    private sealed record Route(ST_SCANNER_AXIS_SETTING X, ST_SCANNER_AXIS_SETTING Y, int Task, double Frequency, double Speed);
    private readonly Dictionary<int, Route> _routes = [];
    private readonly Dictionary<int, CAttenuatorPositioningService> _attenuators = [];
    private readonly SemaphoreSlim _emissionGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _emitting = new();
    private bool _flagMayBeOn;
    private CStageReviewSequences.Session? _stage;
    private int _stageDevice;
    private double StageTolerance => CPowerMeasureRun.Number(manager.Setting(), "TPM_STAGE_Y_TOLERANCE_MM", .03);
    private int TimeoutMs => Math.Clamp(CPowerMeasureRun.Integer(manager.Setting(), "TPM_OPERATION_TIMEOUT_MS", 30000), 100, 120000);
    private CInterfaceManager Interfaces => manager.Interface();
    private CAutomationManager Automation => manager.Automation();
    private static readonly string[] StageFeedbackIds = ["MOTOR_STAGE_Y_CUR_POS", "MOTOR_STAGE_Y_BUSY"];
    private bool Sim(EN_EQP_MODULE module, int no) => Interfaces.GetInterfaceCommunicationList(module).Any(d => d.Number == no && d.IsSimulation);
    public bool IsSimulation(int head) => Sim(EN_EQP_MODULE.PowerMeter, head - 1) || Sim(EN_EQP_MODULE.Attenuator, head - 1) || Automation.IsSimul(_routes[head].X.AutomationNo);
    private void Connected(EN_EQP_MODULE module, int no)
    {
        var device = Interfaces.GetInterfaceCommunicationList(module).FirstOrDefault(d => d.Number == no);
        if (device is null || (!device.IsSimulation && device.ConnectionState != EN_COMM_STATE.Online)) throw new InvalidOperationException($"{module} {no}: 연결되어 있지 않습니다.");
    }
    private (IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE> Values, IReadOnlyList<ST_MELSEC_MAP_DATA> Map) StageSnapshot()
    {
        if (manager.Alarm().CurAlarm.Any(a => a.Severity == Drilling.Common.Alarm.EN_ALARM_LEVEL.Critical))
            throw new InvalidOperationException("HEAVY 알람을 RESET한 후 파워 측정을 시작해 주세요.");
        var values = Interfaces.Melsec.GetCachedSnapshot(StageFeedbackIds, out var map);
        foreach (var id in StageFeedbackIds)
        {
            var row = map.FirstOrDefault(r => r.Use && r.Id == id && r.Direction == EN_MELSEC_DIRECTION.In && r.Access != EN_MELSEC_ACCESS.Write
                && !string.IsNullOrWhiteSpace(r.Address) && !r.Address.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase));
            if (row is null) throw new InvalidOperationException($"Stage Y 입력 맵이 없습니다: {id}");
            Connected(EN_EQP_MODULE.Melsec, row.DeviceNo);
            if (id.EndsWith("CUR_POS")) _stageDevice = row.DeviceNo;
            else if (row.DeviceNo != _stageDevice) throw new InvalidOperationException("Stage Y 위치와 BUSY는 같은 MELSEC 장치의 입력이어야 합니다.");
        }
        return (values, map);
    }
    private string StageEvidence(IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE>? values = null, IReadOnlyList<ST_MELSEC_MAP_DATA>? map = null)
    {
        try
        {
            if (values is null || map is null)
                values = Interfaces.Melsec.GetCachedSnapshot(StageFeedbackIds, out map);
            var now = DateTimeOffset.UtcNow;
            return string.Join(" | ", StageFeedbackIds.Select(id =>
            {
                var row = map.FirstOrDefault(r => r.Id == id);
                var sample = values.GetValueOrDefault(id);
                return FormattableString.Invariant($"{id}; Address={row?.Address}; Value={sample?.Value}; State={sample?.State}; SampleAt={sample?.UpdatedAt:O}; AgeMs={(sample?.UpdatedAt is { } at ? (now - at).TotalMilliseconds : (double?)null):F0}; Cycle={sample?.ReadCycleNo}; Error={sample?.ErrorMessage}");
            })) + $"; DriverReadCode={Interfaces.Melsec.LastReadReturnCode}; DriverReadFailures={Interfaces.Melsec.ConsecutiveReadFailureCount}";
        }
        catch (Exception error) { return "Stage diagnostic unavailable: " + error.Message; }
    }
    // Safety checks during emission remain synchronous and fail closed on stale input.
    public void EnsureStageReady()
    {
        var (values, map) = StageSnapshot();
        foreach (var id in StageFeedbackIds)
        {
            if (!values.TryGetValue(id, out var sample) || !sample.IsValid || sample.Value is null || sample.UpdatedAt is null || DateTimeOffset.Now - sample.UpdatedAt > TimeSpan.FromSeconds(2))
                throw new InvalidOperationException("Stage Y의 유효한 현재 위치/BUSY 스냅샷이 필요합니다. " + StageEvidence(values, map));
            var number = Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(number) || (id.EndsWith("BUSY") ? number != 0 : Math.Abs(number) > StageTolerance))
                throw new InvalidOperationException($"Stage Y가 0 mm에서 정지한 상태여야 합니다. {id}={number:0.####}; " + StageEvidence(values, map));
        }
    }
    public async Task CheckStageReady(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var (values, map) = StageSnapshot();
        foreach (var id in StageFeedbackIds)
            if (values.GetValueOrDefault(id)?.State is EN_MELSEC_CACHE_STATE.Error or EN_MELSEC_CACHE_STATE.Offline)
                throw new IOException("TPM Stage Y 입력 수신 오류: " + StageEvidence(values, map));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeoutMs);
        try
        {
            await Task.WhenAll(StageFeedbackIds.Select(id => Interfaces.Melsec.RefreshCachedValueAsync(id, limit.Token))).ConfigureAwait(false);
            limit.Token.ThrowIfCancellationRequested();
            EnsureStageReady();
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && limit.IsCancellationRequested)
        { throw new TimeoutException("TPM Stage Y 최신 입력 확인 시간 초과: " + StageEvidence(), error); }
        catch (IOException error)
        { throw new IOException("TPM Stage Y 최신 입력 확인 실패: " + StageEvidence(), error); }
    }
    internal async Task MaintainStageFeedback(CancellationToken token)
    {
        while (true)
        {
            await CheckStageReady(token).ConfigureAwait(false);
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }
    public async Task PrepareAll(CancellationToken token)
    {
        await CheckStageReady(token).ConfigureAwait(false);
        if (manager.PowerMeasure.GetSnapshot().IsRunning) throw new InvalidOperationException("PowerMeasure가 이미 실행 중입니다.");
        if (targets.Count == 0 || targets.Select(t => t.HeadNo).Distinct().Count() != targets.Count || targets.Select(t => t.PowerMeterNo).Distinct().Count() != targets.Count)
            throw new InvalidOperationException("중복 없는 Head 및 전용 센서가 필요합니다.");
        var settings = manager.Setting().LoadSection(EN_SETTING_TAB.Option);
        var parameters = recipe.Parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (target.HeadNo is < 1 or > 8 || target.PowerMeterNo != target.HeadNo - 1 || !double.IsFinite(target.TargetPower) || target.TargetPower <= 0)
                throw new InvalidOperationException("Head별 전용 PowerMeter 및 목표 W를 확인해 주세요.");
            var head = target.HeadNo;
            if (!CScannerAxisConfiguration.IsHeadEnabled(settings, head)) throw new InvalidOperationException($"HEAD {head:00}: Head USE가 필요합니다.");
            Connected(EN_EQP_MODULE.PowerMeter, target.PowerMeterNo); Connected(EN_EQP_MODULE.Attenuator, head - 1);
            var axes = CScannerAxisConfiguration.BuildAxisDefinitions(settings, head);
            var x = axes.FirstOrDefault(a => a.AxisName == "X"); var y = axes.FirstOrDefault(a => a.AxisName == "Y");
            if (!x.IsUsed || !y.IsUsed || string.IsNullOrWhiteSpace(x.AxisReference) || string.IsNullOrWhiteSpace(y.AxisReference) || x.ConfigurationError.Length > 0 || y.ConfigurationError.Length > 0)
                throw new InvalidOperationException($"HEAD {head:00}: Scanner X/Y 설정을 확인해 주세요.");
            if (!Automation.IsSimul(x.AutomationNo) && !Automation.IsConnect(x.AutomationNo)) throw new InvalidOperationException($"Automation {x.AutomationNo}: OFFLINE");
            int task = int.Parse(settings.Single(p => p.Key == $"H{head:00}_AUTOMATION_TASK_NO").Value, CultureInfo.InvariantCulture);
            var freq = double.Parse(parameters[$"H{head:00}_LASER_FREQUENCY"], CultureInfo.InvariantCulture);
            if (!double.IsFinite(freq) || freq <= 0) throw new InvalidOperationException($"HEAD {head:00}: Recipe Frequency 오류");
            // Recipe and ManualMove both use mm/sec for scanner Jump Speed.
            var speed = parameters.TryGetValue($"H{head:00}_SCANNER_JUMP_SPEED", out var speedText) ? double.Parse(speedText, CultureInfo.InvariantCulture) : 1500;
            if (!double.IsFinite(speed) || speed <= 0) throw new InvalidOperationException($"HEAD {head:00}: Jump Speed 오류");
            _routes.Add(head, new(x, y, task, freq, speed)); _attenuators.Add(head, new(Interfaces));
            Interfaces.SetPowerMeterSimulationAttenuator(target.PowerMeterNo, head - 1);
        }
        if (_routes.Values.GroupBy(r => (r.X.AutomationNo, r.Task)).Any(g => g.Count() > 1) || _routes.Values.SelectMany(r => new[] { r.X, r.Y }).GroupBy(a => (a.AutomationNo, a.AxisReference.ToUpperInvariant())).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Head 간 Automation Task 또는 Scanner 축이 중복되었습니다.");
        _stage = Interfaces.StageReviewSequences.Open(_stageDevice);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeoutMs);
        try
        {
            // Check every Head before CENTER commands or illumination.
            await Task.WhenAll(targets.Select(async target =>
            {
                var route = _routes[target.HeadNo];
                while (true)
                {
                    limit.Token.ThrowIfCancellationRequested();
                    var status = Automation.StatusSnapshots.GetTask(route.Task, route.X.AutomationNo);
                    if (status.IsFresh(TimeSpan.FromSeconds(2)) && status.Value is { } value)
                    {
                        var parts = value.Split(':');
                        if (parts.Length < 4 || parts[0] != "OK" || parts[3] is not ("Idle" or "Stopped" or "ProgramComplete" or "QueueComplete")) throw new InvalidOperationException($"HEAD {target.HeadNo:00}: Task 실행 중 또는 오류 ({value})");
                        break;
                    }
                    await Task.Delay(50, limit.Token).ConfigureAwait(false);
                }
                await GetAttenuatorPosition(target, limit.Token).ConfigureAwait(false);
                await WaitLaserReady(target.HeadNo, limit.Token).ConfigureAwait(false);
            })).ConfigureAwait(false);
            await Task.WhenAll(targets.Select(async target =>
            {
                var r = _routes[target.HeadNo];
                await Automation.ConfigurePowerMeasurement(r.X.AxisReference, r.Frequency, r.Task, r.X.AutomationNo, limit.Token).ConfigureAwait(false);
                await new CAutomationPositioningService(Automation).MoveAndWait(new[] { r.X, r.Y }.Select(a => new ST_AUTOMATION_ABSOLUTE_MOVE_TARGET($"HEAD {target.HeadNo:00} {a.AxisName}", a.AutomationNo, a.AxisReference, a.AioNo, a.ReadExtendedStatus, 0, r.Speed)).ToArray(), limit.Token).ConfigureAwait(false);
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("PowerMeasure 장치 준비 시간 초과"); }
    }
    private async Task WaitLaserReady(int head, CancellationToken token)
    {
        var lasers = Interfaces.GetInterfaceList(EN_EQP_MODULE.TalonLaser);
        var laser = lasers.FirstOrDefault(d => d.Number == head - 1) ?? (lasers.Count == 1 ? lasers[0] : null) ?? throw new InvalidOperationException($"HEAD {head:00}: Laser 설정이 없습니다.");
        Connected(EN_EQP_MODULE.TalonLaser, laser.Number);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = Interfaces.OpticSnapshots.GetLaser(laser.Number);
            if (snapshot.State == "OK" && snapshot.Value is { } value && DateTimeOffset.Now - snapshot.UpdatedAt <= TimeSpan.FromSeconds(2))
            {
                if (!value.PowerOn || !value.GateOn || !value.ShutterOpen) throw new InvalidOperationException($"HEAD {head:00}: Laser ON / Gate ON / Shutter OPEN이 필요합니다.");
                return;
            }
            await Task.Delay(50, token).ConfigureAwait(false);
        }
    }
    public Task Prepare(ST_POWER_MEASURE_TARGET target, CancellationToken token) => CheckStageReady(token);
    public async Task<double> GetAttenuatorPosition(ST_POWER_MEASURE_TARGET target, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeoutMs);
        try
        {
        while (true)
        {
            limit.Token.ThrowIfCancellationRequested(); Connected(EN_EQP_MODULE.Attenuator, target.HeadNo - 1);
            var snapshot = Interfaces.AttenuatorSnapshots.GetSnapshot(target.HeadNo - 1);
            if (snapshot.IsFresh(TimeSpan.FromSeconds(2)) && snapshot.Value is { CommOk: true } value && snapshot.IsInPosition && double.IsFinite(value.CurrentPosition)) return value.CurrentPosition;
            await Task.Delay(50, limit.Token).ConfigureAwait(false);
        }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException($"HEAD {target.HeadNo:00}: ATT 현재 위치 스냅샷 확인 시간 초과"); }
    }
    public async Task MoveAttenuator(ST_POWER_MEASURE_TARGET target, double angle, CancellationToken token)
    {
        await EndMeasurement(target).ConfigureAwait(false);
        await CheckStageReady(token).ConfigureAwait(false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeoutMs);
        try { await _attenuators[target.HeadNo].MoveAndWaitAsync([new(target.HeadNo, angle)], limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException($"HEAD {target.HeadNo:00}: ATT 이동 시간 초과"); }
    }
    public async Task BeginMeasurement(ST_POWER_MEASURE_TARGET target, CancellationToken token)
    {
        await CheckStageReady(token).ConfigureAwait(false); Connected(EN_EQP_MODULE.PowerMeter, target.PowerMeterNo);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeoutMs);
        await WaitLaserReady(target.HeadNo, limit.Token).ConfigureAwait(false);
        var r = _routes[target.HeadNo];
        await _emissionGate.WaitAsync(limit.Token).ConfigureAwait(false);
        try
        {
            // Laser/other-Head gate waits may outlive the fresh frame acquired above.
            EnsureStageReady();
            // Reserve before ON so an ambiguous command failure still forces OFF during cleanup.
            _emitting.TryAdd(target.HeadNo, 0);
            await Automation.SetGalvoLaserOutput(r.X.AxisReference, true, r.Task, r.X.AutomationNo, limit.Token).ConfigureAwait(false);
            _flagMayBeOn = true;
            await Task.Run(() => Interfaces.Melsec.WriteWord("PROCESS_SEQUENCE_TPM_LASER_ON", 1, limit.Token), limit.Token).ConfigureAwait(false);
        }
        finally { _emissionGate.Release(); }
    }
    public async Task EndMeasurement(ST_POWER_MEASURE_TARGET target)
    {
        if (!_routes.TryGetValue(target.HeadNo, out var r)) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // OFF commands on independent controllers must not wait behind another Head's transport.
        if (_emitting.ContainsKey(target.HeadNo))
            await Automation.SetGalvoLaserOutput(r.X.AxisReference, false, r.Task, r.X.AutomationNo, cleanup.Token).ConfigureAwait(false);
        await _emissionGate.WaitAsync(cleanup.Token).ConfigureAwait(false);
        try
        {
            _emitting.TryRemove(target.HeadNo, out _);
            if (_emitting.Count == 0 && _flagMayBeOn)
            {
                await Task.Run(() => Interfaces.Melsec.WriteWord("PROCESS_SEQUENCE_TPM_LASER_ON", 0, cleanup.Token), cleanup.Token).ConfigureAwait(false);
                _flagMayBeOn = false;
            }
        }
        finally { _emissionGate.Release(); }
    }
    public async Task<double> ReadPower(ST_POWER_MEASURE_TARGET target, CancellationToken token)
    {
        EnsureStageReady(); Connected(EN_EQP_MODULE.PowerMeter, target.PowerMeterNo);
        var result = await Interfaces.ExecutePowerMeterCommand(target.PowerMeterNo, EN_POWER_METER_COMMAND.ReadPower, 0, token).ConfigureAwait(false);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
        if (!Sim(EN_EQP_MODULE.PowerMeter, target.PowerMeterNo))
            return CPowerMeter.TryReadPower(result.Message, out var value) ? value : throw new InvalidOperationException("PowerMeter가 유효한 W 값을 응답하지 않았습니다.");
        var status = Interfaces.GetPowerMeterStatus(target.PowerMeterNo, token);
        if (status.LastError != EN_POWER_METER_ERROR.Ok || !double.IsFinite(status.MeasuredPower)) throw new InvalidOperationException("PowerMeter 측정값 오류");
        return status.MeasuredPower;
    }
    public async Task Finish(ST_POWER_MEASURE_TARGET target)
    {
        Exception? failure = null;
        try { await EndMeasurement(target).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        if (_attenuators.TryGetValue(target.HeadNo, out var mover) && mover.HasPendingStops)
        {
            try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await mover.StopPendingAsync(cleanup.Token).ConfigureAwait(false); }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        }
        if (failure is not null) throw new InvalidOperationException($"HEAD {target.HeadNo:00}: 종료 처리 실패 / {failure.Message}", failure);
    }
    public async ValueTask DisposeAsync()
    {
        // Retain the Stage lease if OFF/cleanup cannot be confirmed. A later release can retry.
        await Task.WhenAll(targets.Select(Finish)).ConfigureAwait(false);
        _stage?.Dispose(); _stage = null;
    }
}
