using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Drilling.Common.Interface;
using Drilling.Common.Motion;
using Drilling.Common.Review;
using Drilling.Common.Alarm;

namespace Drilling.Common.Station;
/// <summary>One shared Process Move / IOF implementation. UI observes Actions and cached signals only.</summary>
public sealed partial class CStageReviewSequences(CInterfaceManager interfaces, TimeSpan? signalTimeout = null)
{
    public const string MoveAble = "HANDSHAKE_STG_REVIEW_ABLE_SDC", MoveComplete = "HANDSHAKE_STG_REVIEW_COMPLETE_SDC";
    public const string MoveStart = "HANDSHAKE_PRC_REVIEW_MOVE_START", MoveAck = "HANDSHAKE_PRC_REVIEW_GENERAL_RUNNING";
    public const string PermitX = "IPS_REVIEW_CONTROL_MODE_X", PermitY = "IPS_REVIEW_CONTROL_MODE_Y";
    public const string IofStart = "HANDSHAKE_PRC_IOF_START_REQUEST", IofComplete = "HANDSHAKE_STG_IOF_COMPLETE";
    public const string IofAck = "HANDSHAKE_PRC_IOF_COMPLETE_RESPONSE", IofRead = "IPS_IOF_READ_COMPLETE", IofRun = "IPS_IOF_BUFFER_RUN_COMPLETE";
    public static readonly string[] MoveActions = ["MOVE_TARGETS", "MOVE_SETTLE", "MOVE_RELEASE", "MOVE_XREADY"];
    public static readonly string[] IofActions = ["IOF_PLAN", "IOF_SIGNALS_CLEAR"];
    public CSequenceActionStatusStore Actions { get; } = new();
    public event Action<CSequenceFailureException>? Failed;
    internal void ReportFailure(CSequenceFailureException failure) => failure.Publish(Failed);

    private readonly ConcurrentDictionary<int, byte> _owners = new();
    private readonly ConcurrentDictionary<int, byte> _cleanupFailures = new();
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _moveStops = new();
    private int _automaticReplies = 1;
    private long _event;
    private ST_STAGE_IOF_PLAN? _lastPlan;
    private int _zeroSegments;
    public int SimulatedZeroLineCompletedSegments => Volatile.Read(ref _zeroSegments);

    public void SetSimulatedZeroLineCompletedSegments(int count)
    {
        var plan = LastIofPlan;
        if (plan is not { ZeroLine: true } || count < 0 || count > plan.Points.Count)
            throw new InvalidOperationException("준비된 0선 계획의 0~N 범위만 입력할 수 있습니다.");
        var row = interfaces.Melsec.Map.Single(item => item.Id == "IOF_SETTING");
        if (!interfaces.IsSimul(EN_EQP_MODULE.Melsec, row.DeviceNo))
            throw new InvalidOperationException("MELSEC 시뮬레이션에서만 변경할 수 있습니다.");
        if (count < SimulatedZeroLineCompletedSegments)
            throw new InvalidOperationException("완료 구간 수는 같은 실행 중 줄일 수 없습니다.");
        Volatile.Write(ref _zeroSegments, count);
    }

    public void ClearZeroLineSimulation()
    {
        if (IsActive)
            throw new InvalidOperationException("Stage Buffer 사용 중에는 초기화할 수 없습니다.");
        if (LastIofPlan is { ZeroLine: true })
            Volatile.Write(ref _lastPlan, null);
        Volatile.Write(ref _zeroSegments, 0);
    }

    public event Action<ST_STAGE_FLOW_EVENT>? Trace;
    public bool IsActive => !_owners.IsEmpty;
    public bool HasActiveMove(int deviceNo) => _moveStops.ContainsKey(deviceNo);

    /// <summary>Operator Stage STOP cancels the owned move and its normal STOP/IO cleanup.</summary>
    public void RequestMoveStop(int deviceNo)
    {
        if (_moveStops.TryGetValue(deviceNo, out var stop))
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { } // The move may have just finished.
    }

    public bool AutomaticSimulationReplies
    {
        get => Volatile.Read(ref _automaticReplies) == 1;
        set
        {
            if (IsActive)
                throw new InvalidOperationException("Process Move / IOF 동작을 정지한 뒤 자동 응답을 변경해 주세요.");
            Volatile.Write(ref _automaticReplies, value ? 1 : 0);
        }
    }

    public ST_STAGE_IOF_PLAN? LastIofPlan => Volatile.Read(ref _lastPlan);

    private readonly TimeSpan _signalTimeout = signalTimeout ?? TimeSpan.FromSeconds(30);
    public void ValidateMoveReadiness(int deviceNo)
    {
        var device = interfaces.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).SingleOrDefault(item => item.Number == deviceNo);
        if (device?.IsSimulation == true)
            return;
        if (device?.ConnectionState != EN_COMM_STATE.Online)
            throw new InvalidOperationException($"MELSEC_{deviceNo}가 연결되어 있지 않습니다.");
        var values = interfaces.Melsec.GetCachedSnapshot(out var map);
        var inputs = new[] { MoveAble, MoveComplete, "MOTOR_STAGE_Y_BUSY", "MOTOR_CAMERA_X_BUSY", "MOTOR_STAGE_Y_CUR_POS", "MOTOR_CAMERA_X_CUR_POS" };
        var outputs = new[] { MoveStart, MoveAck, "PROCESS_REVIEW_STOP_SIGNAL", "PROCESS_REVIEW_CONTROL_MODE", "PROCESS_REVIEW_COMMAND_POSITION_X", "PROCESS_REVIEW_COMMAND_POSITION_Y", "PROCESS_REVIEW_COMMAND_SPEED_X", "PROCESS_REVIEW_COMMAND_SPEED_Y" };
        foreach (var id in inputs.Concat(outputs))
        {
            var input = inputs.Contains(id);
            var row = map.SingleOrDefault(item => item.Use && item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var coordinate = id.Contains("_CUR_POS", StringComparison.Ordinal) || id.Contains("_COMMAND_POSITION_", StringComparison.Ordinal) || id.Contains("_COMMAND_SPEED_", StringComparison.Ordinal);
            if (row is null || row.DeviceNo != deviceNo || string.IsNullOrWhiteSpace(row.Address) || row.Address.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase)
                || row.Direction != (input ? EN_MELSEC_DIRECTION.In : EN_MELSEC_DIRECTION.Out)
                || (input ? row.Access is not (EN_MELSEC_ACCESS.Read or EN_MELSEC_ACCESS.ReadWrite) : row.Access != EN_MELSEC_ACCESS.ReadWrite)
                || (coordinate ? row.DataType != EN_MELSEC_DATA_TYPE.Double : row.DataType is not (EN_MELSEC_DATA_TYPE.Word or EN_MELSEC_DATA_TYPE.DWord)))
                throw new InvalidOperationException($"실제 Process Move 맵의 장치·주소·입출력·자료형을 확인해 주세요: {id}");
            if (input && (values.GetValueOrDefault(id) is not { IsValid: true, Value: not null } sample || !double.IsFinite(Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture))))
                throw new InvalidOperationException($"실제 Process Move 상태 수신을 확인해 주세요: {id}");
        }
        // Permissions may still be OFF here. Move waits for Stage's permission after Review START.
        // This preparation reads the existing shared snapshot; it never sends a move or starts a poller.
    }

    public void ValidateIofReadiness(int deviceNo, int pointCount = 0)
    {
        if (pointCount < 0 || pointCount > 500)
            throw new ArgumentOutOfRangeException(nameof(pointCount), "IOF 한 라인의 촬영 좌표는 최대 500개입니다.");
        var device = interfaces.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).SingleOrDefault(item => item.Number == deviceNo);
        if (device?.IsSimulation == true)
            return;
        if (device?.ConnectionState != EN_COMM_STATE.Online)
            throw new InvalidOperationException($"MELSEC_{deviceNo}가 연결되어 있지 않습니다.");
        foreach (var id in new[] { IofStart, IofAck, IofComplete, "IOF_SETTING", "IOF_TRIGGER_POINT_COUNT", "IOF_TRIGGER_ZERO_POINT_COUNT", "IOF_POS_ARRAY_1" })
        {
            var row = interfaces.Melsec.Map.SingleOrDefault(item => item.Use && item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var input = id == IofComplete;
            var array = id == "IOF_POS_ARRAY_1";
            if (row is null || row.DeviceNo != deviceNo || string.IsNullOrWhiteSpace(row.Address) || row.Address.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase)
                || row.Direction != (input ? EN_MELSEC_DIRECTION.In : EN_MELSEC_DIRECTION.Out)
                || (input ? row.Access is not (EN_MELSEC_ACCESS.Read or EN_MELSEC_ACCESS.ReadWrite) : row.Access != EN_MELSEC_ACCESS.ReadWrite)
                || (array ? row.DataType != EN_MELSEC_DATA_TYPE.Double || interfaces.Melsec.GetDoubleArrayCapacity(id) < Math.Max(1, pointCount) : row.DataType is not (EN_MELSEC_DATA_TYPE.Word or EN_MELSEC_DATA_TYPE.DWord)))
                throw new InvalidOperationException($"IOF 맵의 장치·주소·입출력·자료형·저장 크기를 확인해 주세요: {id}");
        }
        // WC138/WC13A are observable progress only. Stage confirms buffer
        // readiness with WC13C, then Process acknowledges with W823A.
    }

    /// <summary>Manual ABS/REL uses the same permission, START, completion and release transaction as AUTO.</summary>
    public async Task MoveManualAxis(int deviceNo, EN_MANUAL_STAGE_AXIS axis, double value, bool relative, double speed, CancellationToken token)
    {
        if (!Enum.IsDefined(axis) || !double.IsFinite(value) || !double.IsFinite(speed) || speed <= 0)
            throw new ArgumentException("수동 이동 축·위치·속도를 확인해 주세요.");
        ValidateMoveReadiness(deviceNo);
        using var session = Open(deviceNo);
        await session.MoveAxis(axis, value, relative, speed, token).ConfigureAwait(false);
    }

    public Session Open(int deviceNo, Func<bool>? stopRequested = null, bool reviewXOnly = false, bool stageBufferOwner = false)
    {
        if (_cleanupFailures.ContainsKey(deviceNo))
        {
            var values = interfaces.Melsec.GetCachedSnapshot();
            var clean = new[]
            {
                MoveStart,
                MoveAck,
                IofStart,
                IofAck,
                "MOTOR_STAGE_Y_BUSY",
                "MOTOR_CAMERA_X_BUSY"
            }.All(id => values.GetValueOrDefault(id)is { IsValid: true, Value: not null } sample && Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture) == 0);
            if (!clean)
                throw new InvalidOperationException("Stage 공통 시퀀스 정리가 실패했습니다. 이동 정지와 요청/응답 OFF를 먼저 확인해 주세요.");
            _cleanupFailures.TryRemove(deviceNo, out _);
            _owners.TryRemove(deviceNo, out _);
        }

        if (!_owners.TryAdd(deviceNo, 0))
            throw new InvalidOperationException("Stage Process Move / IOF가 이미 사용 중입니다.");
        try
        {
            return new(this, interfaces, deviceNo, stopRequested ?? (() => false), AutomaticSimulationReplies, reviewXOnly, stageBufferOwner);
        }
        catch
        {
            _owners.TryRemove(deviceNo, out _);
            throw;
        }
    }

    private void Publish(int device, string phase, double? value = null, string detail = "")
    {
        var item = new ST_STAGE_FLOW_EVENT(Interlocked.Increment(ref _event), device, phase, value)
        {
            Detail = detail
        };
        foreach (var subscriber in Trace?.GetInvocationList() ?? [])
            try
            {
                ((Action<ST_STAGE_FLOW_EVENT>)subscriber)(item);
            }
            catch (Exception error)
            {
                try
                {
                    System.Diagnostics.Trace.TraceError(error.ToString());
                }
                catch
                {
                }
            }
    }

    public sealed partial class Session : IDisposable
    {
        private readonly CStageReviewSequences _owner;
        private readonly CInterfaceManager _interfaces;
        private readonly int _device;
        private readonly Func<bool> _stopRequested;
        private readonly bool _simulation, _automatic, _reviewXOnly, _stageBufferOwner;
        private readonly CManualStageMotionSimulation? _motion;
        private readonly CManualStageMotionSimulation.MotionLease? _lease;
        private readonly SemaphoreSlim _operation = new(1, 1);
        private bool _disposed, _moveOwned, _iofOwned;
        private Guid _iofPlanId;
        private bool _iofStartConfirmed, _iofAckConfirmed;
        private string _scenario = "PROCESS_MOVE";
        private readonly string _traceSession = Guid.NewGuid().ToString("N");
        private void Record(string phase, string detail = "", double? value = null) => _owner.Publish(_device, phase, value, $"Flow={_traceSession}; Simulation={_simulation}; {detail}");
        public bool IsSimulation => _simulation;
        public double CurrentX => Feedback("MOTOR_CAMERA_X_CUR_POS");
        public double CurrentY => Feedback("MOTOR_STAGE_Y_CUR_POS");

        internal Session(CStageReviewSequences owner, CInterfaceManager interfaces, int device, Func<bool> stop, bool automatic, bool reviewXOnly, bool stageBufferOwner)
        {
            _owner = owner;
            _interfaces = interfaces;
            _device = device;
            _stopRequested = stop;
            _automatic = automatic;
            _reviewXOnly = reviewXOnly;
            _stageBufferOwner = stageBufferOwner;
            var state = interfaces.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).SingleOrDefault(item => item.Number == device);
            _simulation = state?.IsSimulation ?? (interfaces.Devices.Count == 0 && interfaces.IsSimulation);
            if (!_simulation && state?.ConnectionState != EN_COMM_STATE.Online)
                throw new InvalidOperationException($"MELSEC_{device}가 연결되어 있지 않습니다.");
            if (_simulation)
            {
                _motion = interfaces.GetStageMotionSimulation(device);
                _lease = _motion.AcquireExclusiveMotion(allowStageYMoving: reviewXOnly || stageBufferOwner);
            }
        }

        private void Check(CancellationToken token, bool cleanup = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (!cleanup && _stopRequested())
                throw new OperationCanceledException("Review Stop");
            var state = _interfaces.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).SingleOrDefault(item => item.Number == _device);
            if (state is not null && (state.IsSimulation != _simulation || !_simulation && state.ConnectionState != EN_COMM_STATE.Online))
                throw new InvalidOperationException("Stage 동작 중 MELSEC 모드 또는 연결 상태가 변경되었습니다.");
        }

        private ST_MELSEC_MAP_DATA Route(string id)
        {
            var row = _interfaces.Melsec.GetMapData(id);
            if (!row.Use || row.DeviceNo != _device)
                throw new InvalidOperationException($"Stage 공통 시퀀스 맵을 확인해 주세요: {id} / MELSEC_{_device}");
            return row;
        }

        private double Value(string id)
        {
            Route(id);
            var sample = _interfaces.Melsec.GetCachedValue(id);
            if (sample?.IsValid != true || sample.Value is null)
                throw new InvalidOperationException($"Stage 신호가 유효하지 않습니다: {id}");
            var result = Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(result))
                throw new InvalidOperationException($"Stage 값이 유효하지 않습니다: {id}");
            return result;
        }

        private double Feedback(string id) => _simulation ? _interfaces.GetStageMotionSimulationFeedback(_device)[id] : Value(id);
        private async Task Write(string id, double value, CancellationToken token, bool cleanup = false)
        {
            Check(token, cleanup);
            var row = Route(id);
            var clock = Stopwatch.StartNew();
            Record("OUTPUT_REQUEST", FormattableString.Invariant($"{id}; Address={row.Address}; Logical={value}; ReadScale={row.ReadScale}; WriteScale={row.WriteScale}; Cleanup={cleanup}"));
            if (row.Direction != EN_MELSEC_DIRECTION.Out || row.Access != EN_MELSEC_ACCESS.ReadWrite)
                throw new InvalidOperationException("가공 PC 출력 항목이 아닙니다: " + id);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await Task.Run(() =>
                {
                    if (row.DataType == EN_MELSEC_DATA_TYPE.Double)
                        _interfaces.Melsec.WriteDouble(id, value, limit.Token);
                    else
                        _interfaces.Melsec.WriteWord(id, checked((int)value), limit.Token);
                }, limit.Token).ConfigureAwait(false);
                Record("OUTPUT_SENT", $"{id}; ElapsedMs={clock.ElapsedMilliseconds}");
                var sample = await _interfaces.Melsec.RefreshCachedValueAsync(id, limit.Token).ConfigureAwait(false);
                if (!sample.IsValid || sample.Value is null || Math.Abs(Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture) - value) > Math.Max(1e-8, Math.Abs(row.ReadScale) / 2 + 1e-9))
                    throw new IOException("Stage 출력 읽기 확인 실패: " + id);
                Record(id, $"OUTPUT_CONFIRMED; Address={row.Address}; ElapsedMs={clock.ElapsedMilliseconds}; SampleAt={sample.UpdatedAt:O}", value);
            }
            catch (OperationCanceledException)when (!token.IsCancellationRequested && limit.IsCancellationRequested)
            {
                Record("OUTPUT_FAILED", $"{id}; Timeout; ElapsedMs={clock.ElapsedMilliseconds}");
                var failure = new TimeoutException("Stage 출력 확인 시간 초과: " + id);
                if (id is MoveStart or MoveAck or IofStart or IofAck) throw CSequenceFailureException.Signal(_scenario, id, true, failure);
                throw failure;
            }
            catch (Exception error)
            {
                Record("OUTPUT_FAILED", $"{id}; ElapsedMs={clock.ElapsedMilliseconds}; {error}");
                if (error is OperationCanceledException) throw;
                if (id is MoveStart or MoveAck or IofStart or IofAck) throw CSequenceFailureException.Signal(_scenario, id, error is TimeoutException, error);
                throw;
            }
        }

        private async Task Stage(string id, int value, CancellationToken token)
        {
            if (!_simulation || !_automatic)
                return;
            Route(id);
            _interfaces.Melsec.SetStageFlowSimulationValue(id, value);
            await _interfaces.Melsec.RefreshCachedValueAsync(id, token).ConfigureAwait(false);
            Record(id, "SIMULATED_STAGE_REPLY", value);
        }

        // Read the existing cache only: diagnostics must not add SDK reads or alter peer signals.
        private void RecordIofSnapshot(string phase, string reason)
        {
            try
            {
                var ids = new[] { IofStart, IofAck, IofComplete, IofRead, IofRun, "IOF_SETTING", "IOF_TRIGGER_POINT_COUNT", "IOF_TRIGGER_ZERO_POINT_COUNT", "MOTOR_CAMERA_X_BUSY", "MOTOR_STAGE_Y_BUSY" };
                var values = _interfaces.Melsec.GetCachedSnapshot(ids, out var map);
                var now = DateTimeOffset.UtcNow;
                var signals = ids.Select(id =>
                {
                    var row = map.SingleOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    var sample = values.GetValueOrDefault(id);
                    // The cache stores scaled values, not raw received words. Label the inverse as derived.
                    var rawDerived = sample?.Value is not null && row is { ReadScale: not 0 }
                        ? Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture) / row.ReadScale : (double?)null;
                    var age = sample?.UpdatedAt is { } at ? (now - at).TotalMilliseconds : (double?)null;
                    return FormattableString.Invariant($"{id}[Address={row?.Address}; Type={row?.DataType}; Length={row?.Length}; ReadScale={row?.ReadScale}; WriteScale={row?.WriteScale}; Value={sample?.Value}; RawDerived={rawDerived}; State={sample?.State}; SampleAt={sample?.UpdatedAt:O}; AgeMs={age:F0}; Cycle={sample?.ReadCycleNo}; Error={sample?.ErrorMessage}]");
                });
                Record("IOF_SNAPSHOT", $"Plan={_iofPlanId}; Phase={phase}; Reason={reason}; StartConfirmed={_iofStartConfirmed}; AckConfirmed={_iofAckConfirmed}; DriverLastReadAt={_interfaces.Melsec.LastSuccessfulReadAt:O}; DriverReadCode={_interfaces.Melsec.LastReadReturnCode}; DriverReadFailures={_interfaces.Melsec.ConsecutiveReadFailureCount}; " + string.Join(" | ", signals));
            }
            catch (Exception error)
            {
                Record("IOF_DIAGNOSTIC_FAILED", $"Plan={_iofPlanId}; Phase={phase}; Reason={reason}; {error.Message}");
            }
        }

        private async Task Wait(string id, int expected, CancellationToken token, TimeSpan? timeout = null, string? iofPhase = null)
        {
            var clock = Stopwatch.StartNew();
            double? previous = null;
            var nextDiagnostic = TimeSpan.FromSeconds(5);
            Record("WAIT_BEGIN", $"{id}; Expected={expected}; TimeoutMs={(timeout ?? _owner._signalTimeout).TotalMilliseconds}; IofPhase={iofPhase}");
            if (iofPhase is not null) RecordIofSnapshot(iofPhase, "WAIT_BEGIN");
            try
            {
                while (true)
                {
                    Check(token);
                    var number = Value(id);
                    if (previous != number)
                    {
                        Record("SIGNAL_OBSERVED", $"{id}; Address={Route(id).Address}", number);
                        previous = number;
                    }

                    if (number is not (0 or 1))
                        throw new InvalidDataException("Stage 신호는 READ_SCALE 적용 후 0/1이어야 합니다: " + id);
                    if (number == expected)
                    {
                        Record("WAIT_MATCH", $"{id}; Expected={expected}; ElapsedMs={clock.ElapsedMilliseconds}; IofPhase={iofPhase}");
                        return;
                    }

                    if (clock.Elapsed > (timeout ?? _owner._signalTimeout))
                        throw new TimeoutException($"Stage 신호 대기 시간 초과: {id}={expected}; Actual={number}; IofPhase={iofPhase}; StartConfirmed={_iofStartConfirmed}; AckConfirmed={_iofAckConfirmed}");
                    if (iofPhase is not null && clock.Elapsed >= nextDiagnostic)
                    {
                        RecordIofSnapshot(iofPhase, $"WAITING; ElapsedMs={clock.ElapsedMilliseconds}");
                        nextDiagnostic = clock.Elapsed + TimeSpan.FromSeconds(5);
                    }
                    await Task.Delay(20, token).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                if (iofPhase is not null) RecordIofSnapshot(iofPhase, error is OperationCanceledException ? "WAIT_CANCELED" : "WAIT_FAILED");
                Record("WAIT_FAILED", $"{id}; Expected={expected}; Actual={previous}; IofPhase={iofPhase}; ElapsedMs={clock.ElapsedMilliseconds}; {error}");
                if (error is OperationCanceledException) throw;
                throw CSequenceFailureException.Signal(_scenario, id, error is TimeoutException, error);
            }
        }

        private async Task Step(string id, Func<Task> work)
        {
            var clock = Stopwatch.StartNew();
            Record(id + ":RUNNING");
            try
            {
                await _owner.Actions.RunAsync(id, work).ConfigureAwait(false);
                Record(id + ":DONE", $"ElapsedMs={clock.ElapsedMilliseconds}");
            }
            catch (Exception error)
            {
                Record(id + (error is OperationCanceledException ? ":CANCELED" : ":FAILED"), $"ElapsedMs={clock.ElapsedMilliseconds}; {error}");
                if (error is OperationCanceledException) throw;
                var failure = CSequenceFailureException.Action(id, error);
                _owner.ReportFailure(failure);
                throw failure;
            }
        }

        private async Task InitialRead(IEnumerable<string> ids, CancellationToken token)
        {
            // Simulation remains usable before an explicit connection; initialize only its owning simulated device.
            if (_simulation && !_interfaces.Melsec.IsRunning)
                await _interfaces.Connect(EN_EQP_MODULE.Melsec, _device, cancellationToken: token).ConfigureAwait(false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            // Register all refreshes together: IDs in one polling block share one fresh read.
            await Task.WhenAll(ids.Distinct(StringComparer.OrdinalIgnoreCase).Select(async id =>
            {
                Check(token);
                Route(id);
                var sample = await _interfaces.Melsec.RefreshCachedValueAsync(id, limit.Token).ConfigureAwait(false);
                Record("SIGNAL_INITIAL", $"{id}; Address={Route(id).Address}; State={sample.State}; Value={Convert.ToString(sample.Value, CultureInfo.InvariantCulture)}; SampleAt={sample.UpdatedAt:O}");
                if (!sample.IsValid)
                    throw new IOException("Stage 초기 상태 읽기 실패: " + id);
            })).ConfigureAwait(false);
        }

        public void Stop()
        {
            _motion?.Stop(EN_MANUAL_STAGE_AXIS.ReviewX);
            if (!_reviewXOnly && !_stageBufferOwner)
                _motion?.Stop(EN_MANUAL_STAGE_AXIS.StageY);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            Stop();
            _lease?.Dispose();
            _disposed = true;
            if (!_owner._cleanupFailures.ContainsKey(_device))
                _owner._owners.TryRemove(_device, out _);
        }

        internal async Task MoveAxis(EN_MANUAL_STAGE_AXIS axis, double value, bool relative, double speed, CancellationToken token)
        {
            var mode = axis == EN_MANUAL_STAGE_AXIS.ReviewX ? EN_STAGE_MOVE_MODE.ReviewX : EN_STAGE_MOVE_MODE.StageY;
            await InitialRead(["MOTOR_CAMERA_X_BUSY", "MOTOR_STAGE_Y_BUSY"], token).ConfigureAwait(false);
            await WaitStopped(token, mode).ConfigureAwait(false);
            // Resolve REL from fresh hardware feedback while holding the common motion ownership.
            await InitialRead(["MOTOR_CAMERA_X_CUR_POS", "MOTOR_STAGE_Y_CUR_POS"], token).ConfigureAwait(false);
            var x = CurrentX;
            var y = CurrentY;
            var target = relative ? (axis == EN_MANUAL_STAGE_AXIS.ReviewX ? x : y) + value : value;
            await Move(new(axis == EN_MANUAL_STAGE_AXIS.ReviewX ? target : x,
                axis == EN_MANUAL_STAGE_AXIS.StageY ? target : y, speed, speed,
                Mode: mode), token).ConfigureAwait(false);
        }

        public async Task<double> ReadStationaryPosition(EN_MANUAL_STAGE_AXIS axis, CancellationToken token)
        {
            if (!Enum.IsDefined(axis)) throw new ArgumentOutOfRangeException(nameof(axis));
            var stageY = axis == EN_MANUAL_STAGE_AXIS.StageY;
            var busy = stageY ? "MOTOR_STAGE_Y_BUSY" : "MOTOR_CAMERA_X_BUSY";
            var position = stageY ? "MOTOR_STAGE_Y_CUR_POS" : "MOTOR_CAMERA_X_CUR_POS";
            await InitialRead([busy], token).ConfigureAwait(false);
            await WaitStopped(token, stageY ? EN_STAGE_MOVE_MODE.StageY : EN_STAGE_MOVE_MODE.ReviewX).ConfigureAwait(false);
            await InitialRead([position], token).ConfigureAwait(false);
            return stageY ? CurrentY : CurrentX;
        }

        public async Task Move(ST_STAGE_MOVE_TARGET target, CancellationToken token)
        {
            _scenario = "PROCESS_MOVE";
            if ((_reviewXOnly || _stageBufferOwner) && target.Mode != EN_STAGE_MOVE_MODE.ReviewX)
                throw new InvalidOperationException("Review X 선대기/0선 소유자는 Main Y를 이동할 수 없습니다.");
            if (new[]
            {
                target.X,
                target.Y,
                target.SpeedX,
                target.SpeedY,
                target.AccelerationX
            }.Any(value => !double.IsFinite(value)) || target.SpeedX <= 0 || target.SpeedY <= 0 || target.AccelerationX <= 0 || !Enum.IsDefined(target.Mode))
                throw new ArgumentException("Stage 목표 위치·속도·가속도·모드를 확인해 주세요.");
            await _operation.WaitAsync(token).ConfigureAwait(false);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _owner._moveStops[_device] = stop;
            token = stop.Token;
            try
            {
                Check(token);
                _owner.Actions.ResetToWaiting(MoveActions);
                Record("MOVE_PLAN", FormattableString.Invariant($"Mode={target.Mode}; TargetX={target.X}; TargetY={target.Y}; SpeedX={target.SpeedX}; SpeedY={target.SpeedY}; AccX={target.AccelerationX}"));
                await Step("MOVE_TARGETS", async () =>
                {
                    await InitialRead([MoveStart, MoveAck, MoveComplete, MoveAble], token).ConfigureAwait(false);
                    await WaitStopped(token, target.Mode).ConfigureAwait(false);
                    // Never start over an old peer completion or unacknowledged request.
                    if (Value(MoveStart) != 0 || Value(MoveAck) != 0)
                        throw new InvalidOperationException("이전 SDC 이동 요청/응답이 복귀되지 않았습니다.");
                    await Stage(MoveComplete, 0, token).ConfigureAwait(false);
                    await Wait(MoveComplete, 0, token).ConfigureAwait(false);
                    await Write("PROCESS_REVIEW_STOP_SIGNAL", 0, token).ConfigureAwait(false);
                    await Write("PROCESS_REVIEW_CONTROL_MODE", (int)target.Mode, token).ConfigureAwait(false);
                    await Write("PROCESS_REVIEW_COMMAND_POSITION_X", target.X, token).ConfigureAwait(false);
                    await Write("PROCESS_REVIEW_COMMAND_POSITION_Y", target.Y, token).ConfigureAwait(false);
                    await Write("PROCESS_REVIEW_COMMAND_SPEED_X", target.SpeedX, token).ConfigureAwait(false);
                    await Write("PROCESS_REVIEW_COMMAND_SPEED_Y", target.SpeedY, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
                await Step("MOVE_SETTLE", async () =>
                {
                    await Stage(MoveAble, 1, token).ConfigureAwait(false);
                    // Spec 0.08 (PROCESS_REVIEW_MOVE_START, row 193) and the
                    // Review scenario define WC124 -> W8029 -> WC128 -> W8021.
                    // WC120/WC122 describe axis control availability, but are not
                    // additional gates of this Process Move transaction. In the
                    // field WC120 can remain OFF even when WC124 permits XY.
                    // The requested axes are selected with W801B; Busy/position,
                    // old handshake reset and WC124 are still verified.
                    await Wait(MoveAble, 1, token).ConfigureAwait(false);
                    _moveOwned = true;
                    await Write(MoveStart, 1, token).ConfigureAwait(false);
                    if (_simulation)
                    {
                        // Even with manual peer signals, the shared physical-motion preview runs.
                        if (target.Mode != EN_STAGE_MOVE_MODE.ReviewX)
                            _lease!.Move(EN_MANUAL_STAGE_AXIS.StageY, target.Y, target.SpeedY);
                        if (target.Mode != EN_STAGE_MOVE_MODE.StageY)
                            _lease!.Move(EN_MANUAL_STAGE_AXIS.ReviewX, target.X, target.SpeedX, target.AccelerationX);
                        await WaitArrival(target, token).ConfigureAwait(false);
                        await Stage(MoveComplete, 1, token).ConfigureAwait(false);
                    }

                    await Wait(MoveComplete, 1, token).ConfigureAwait(false);
                    await WaitArrival(target, token).ConfigureAwait(false);
                    // Describe the completed validation without reading/validating an unused axis for logging.
                    Record("MOVE_ARRIVAL_CONFIRMED", FormattableString.Invariant($"TargetX={target.X}; TargetY={target.Y}; Mode={target.Mode}; PositionTolerance=0.0005; Busy=0 on participating axes"));
                }).ConfigureAwait(false);
                await Step("MOVE_RELEASE", async () =>
                {
                    await Write(MoveStart, 0, token).ConfigureAwait(false);
                    await Write(MoveAck, 1, token).ConfigureAwait(false);
                    await Stage(MoveComplete, 0, token).ConfigureAwait(false);
                    await Wait(MoveComplete, 0, token).ConfigureAwait(false);
                    await Write(MoveAck, 0, token).ConfigureAwait(false);
                    _moveOwned = false;
                }).ConfigureAwait(false);
                await Step("MOVE_XREADY", () => Task.CompletedTask).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await Abort(move: true).ConfigureAwait(false);
                if (error is OperationCanceledException) throw;
                var failure = CSequenceFailureException.Action("MOVE_TARGETS", error);
                _owner.ReportFailure(failure);
                throw failure;
            }
            finally
            {
                _owner._moveStops.TryRemove(_device, out _);
                _operation.Release();
            }
        }

        private async Task WaitStopped(CancellationToken token, EN_STAGE_MOVE_MODE mode = EN_STAGE_MOVE_MODE.XY)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (true)
            {
                Check(token);
                if ((mode == EN_STAGE_MOVE_MODE.ReviewX || Feedback("MOTOR_STAGE_Y_BUSY") == 0) && (mode == EN_STAGE_MOVE_MODE.StageY || Feedback("MOTOR_CAMERA_X_BUSY") == 0))
                    return;
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException("Stage / Review 정지 확인 시간 초과");
                await Task.Delay(20, token).ConfigureAwait(false);
            }
        }

        internal Task VerifyArrival(ST_STAGE_MOVE_TARGET target, CancellationToken token) => WaitArrival(target, token);

        private async Task WaitArrival(ST_STAGE_MOVE_TARGET target, CancellationToken token)
        {
            if (!_simulation)
                await InitialRead(["MOTOR_STAGE_Y_BUSY", "MOTOR_CAMERA_X_BUSY", "MOTOR_STAGE_Y_CUR_POS", "MOTOR_CAMERA_X_CUR_POS"], token).ConfigureAwait(false);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (true)
            {
                Check(token);
                if ((target.Mode == EN_STAGE_MOVE_MODE.ReviewX || Feedback("MOTOR_STAGE_Y_BUSY") == 0) && (target.Mode == EN_STAGE_MOVE_MODE.StageY || Feedback("MOTOR_CAMERA_X_BUSY") == 0))
                {
                    if ((target.Mode != EN_STAGE_MOVE_MODE.ReviewX && Math.Abs(CurrentY - target.Y) > .0005) || (target.Mode != EN_STAGE_MOVE_MODE.StageY && Math.Abs(CurrentX - target.X) > .0005))
                        throw new InvalidOperationException("Stage / Review가 목표 위치에 도착하기 전에 정지되었습니다.");
                    return;
                }

                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException("Stage / Review 도착 확인 시간 초과");
                await Task.Delay(20, token).ConfigureAwait(false);
            }
        }

        private async Task Abort(bool move)
        {
            Record("CLEANUP_BEGIN", $"Move={move}; MoveOwned={_moveOwned}; IofOwned={_iofOwned}");
            Stop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                if (move && _moveOwned)
                {
                    await Write("PROCESS_REVIEW_STOP_SIGNAL", 1, cleanup.Token, cleanup: true).ConfigureAwait(false);
                    await Write(MoveStart, 0, cleanup.Token, cleanup: true).ConfigureAwait(false);
                    await Write(MoveAck, 0, cleanup.Token, cleanup: true).ConfigureAwait(false);
                    await Stage(MoveComplete, 0, cleanup.Token).ConfigureAwait(false);
                    _moveOwned = false;
                }

                if (!move && _iofOwned)
                {
                    await Write(IofStart, 0, cleanup.Token, cleanup: true).ConfigureAwait(false);
                    await Write(IofAck, 0, cleanup.Token, cleanup: true).ConfigureAwait(false);
                    await Stage(IofComplete, 0, cleanup.Token).ConfigureAwait(false);
                    _iofOwned = false;
                }
            }
            catch (Exception error)
            {
                _owner._cleanupFailures[_device] = 0;
                _owner.Actions.SetError(move ? "MOVE_RELEASE" : "IOF_SIGNALS_CLEAR", "Stage 정리 실패: " + error.Message);
                _owner.ReportFailure(CSequenceFailureException.Action(move ? "MOVE_RELEASE" : "IOF_SIGNALS_CLEAR", error));
                Record("CLEANUP_ERROR", error.ToString());
                System.Diagnostics.Trace.TraceError(error.ToString());
            }
        }

        public async Task<ST_STAGE_IOF_PLAN> PrepareIof(IReadOnlyList<ST_STAGE_IOF_POINT> points, bool zeroLine, CancellationToken token)
        {
            _scenario = "IOF";
            if (_reviewXOnly)
                throw new InvalidOperationException("Review X 선대기 소유자는 IOF Buffer를 갱신할 수 없습니다.");
            if (points.Count == 0 || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                throw new ArgumentException("IOF 촬영 위치를 확인해 주세요.");
            if (zeroLine && points.Count is not (2 or 4))
                throw new ArgumentException("0선방어 촬영 개수는 2 또는 4개여야 합니다.");
            if (points.Count > 500)
                throw new ArgumentException("IOF 한 라인의 촬영 좌표는 최대 500개입니다. (W8300~W86E7)");
            // 2026-11-03 Stage contract: write ordered coordinates, request W8238,
            // wait for WC13C, acknowledge W823A. Keep real and simulated paths equal.
            if (!_simulation)
                _owner.ValidateIofReadiness(_device, points.Count);
            await _operation.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Check(token);
                _owner.Actions.ResetToWaiting(IofActions);
                var plan = new ST_STAGE_IOF_PLAN(Guid.NewGuid(), zeroLine, Array.AsReadOnly(points.ToArray()));
                _iofPlanId = plan.Id;
                _iofStartConfirmed = _iofAckConfirmed = false;
                Record("IOF_PLAN_CAPTURED", $"Plan={plan.Id}; ZeroLine={zeroLine}; Count={points.Count}; Points=" + string.Join(";", points.Select((point, index) => FormattableString.Invariant($"{index + 1}:X={point.X},Y={point.Y}"))));
                await Step("IOF_PLAN", async () =>
                {
                    await InitialRead([IofStart, IofAck, IofComplete], token).ConfigureAwait(false);
                    await WaitStopped(token).ConfigureAwait(false);
                    if (Value(IofStart) != 0 || Value(IofAck) != 0)
                        throw new InvalidOperationException("이전 IOF 요청/응답이 복귀되지 않았습니다.");
                    await Stage(IofComplete, 0, token).ConfigureAwait(false);
                    await Stage(IofRead, 0, token).ConfigureAwait(false);
                    await Stage(IofRun, 0, token).ConfigureAwait(false);
                    await Wait(IofComplete, 0, token, iofPhase: "INITIAL_COMPLETE_OFF").ConfigureAwait(false);
                    await Write("IOF_SETTING", zeroLine ? 1 : 0, token).ConfigureAwait(false);
                    await Write(zeroLine ? "IOF_TRIGGER_ZERO_POINT_COUNT" : "IOF_TRIGGER_POINT_COUNT", points.Count, token).ConfigureAwait(false);
                    // Stage contract confirmed on 2026-11-03: zero-line sends Y
                    // only, NOT the old document's packed [Y...][X...] layout.
                    // Reverse attempts supply their reversed X order unchanged.
                    {
                        var packed = points.Select(p => zeroLine ? p.Y : p.X).ToArray();
                        var buffer = Route("IOF_POS_ARRAY_1");
                        if (buffer.DataType != EN_MELSEC_DATA_TYPE.Double || packed.Length > _interfaces.Melsec.GetDoubleArrayCapacity(buffer.Id))
                            throw new InvalidOperationException("IOF 좌표 배열 맵의 자료형·저장 크기를 확인해 주세요.");
                        await Task.Run(() => _interfaces.Melsec.WriteDoubleArray("IOF_POS_ARRAY_1", packed, token), token).ConfigureAwait(false);
                        var readback = await Task.Run(() => _interfaces.Melsec.ReadDoubleArray("IOF_POS_ARRAY_1", packed.Length, token), token).ConfigureAwait(false);
                        if (readback.Count != packed.Length || readback.Zip(packed).Any(pair => Math.Abs(pair.First - pair.Second) > .000051))
                            throw new IOException("IOF 좌표 배열 읽기 확인 실패");
                        if (zeroLine)
                            Volatile.Write(ref _owner._zeroSegments, 0);
                    }

                    Volatile.Write(ref _owner._lastPlan, plan);
                    _owner.Publish(_device, "IOF_COORDINATES_READY", points.Count);
                    _iofOwned = true;
                    await Write(IofStart, 1, token).ConfigureAwait(false);
                    _iofStartConfirmed = true;
                    await Stage(IofRead, 1, token).ConfigureAwait(false);
                    await Stage(IofRun, 1, token).ConfigureAwait(false);
                    await Stage(IofComplete, 1, token).ConfigureAwait(false);
                    await Wait(IofComplete, 1, token, iofPhase: "REQUEST_COMPLETE_ON").ConfigureAwait(false);
                }).ConfigureAwait(false);
                await Step("IOF_SIGNALS_CLEAR", async () =>
                {
                    // Release START before ACK: Stage clears COMPLETE only after both conditions.
                    await Write(IofStart, 0, token).ConfigureAwait(false);
                    await Write(IofAck, 1, token).ConfigureAwait(false);
                    _iofAckConfirmed = true;
                    await Stage(IofComplete, 0, token).ConfigureAwait(false);
                    await Wait(IofComplete, 0, token, iofPhase: "ACK_COMPLETE_OFF").ConfigureAwait(false);
                    await Write(IofAck, 0, token).ConfigureAwait(false);
                    _iofOwned = false;
                }).ConfigureAwait(false);
                return plan;
            }
            catch (Exception error)
            {
                await Abort(move: false).ConfigureAwait(false);
                if (error is OperationCanceledException) throw;
                var failure = CSequenceFailureException.Action("IOF_PLAN", error);
                _owner.ReportFailure(failure);
                throw failure;
            }
            finally
            {
                _operation.Release();
            }
        }

        public void InitializeZeroLineReadyY(double position)
        {
            if (!_simulation || !_stageBufferOwner)
                throw new InvalidOperationException("0선 Stage 시뮬레이션 소유권이 필요합니다.");
            _lease!.SetReportedPosition(EN_MANUAL_STAGE_AXIS.StageY, position);
        }

        /// <summary>
        /// Starts the Stage-owned scan only in automatic simulation. Real Stage
        /// travel and triggers are owned by the PLC after IOF preparation; this
        /// method neither moves Review X nor compares individual capture positions.
        /// </summary>
        public void BeginZeroLineSimulation(ST_ZERO_LINE_CYCLE_PLAN plan, CancellationToken token)
        {
            if (!_stageBufferOwner)
                throw new InvalidOperationException("0선 Stage Buffer 소유권이 필요합니다.");
            Check(token);
            if (_simulation && _automatic)
            {
                // Stop one MELSEC coordinate unit beyond the threshold in SIM only.
                // A strict passed-target check must not stall at an exact virtual endpoint.
                var simulatedEndY = plan.Motion.StageStartY + plan.Motion.StageDirection * (plan.Motion.StageStroke + .0001);
                _lease!.Move(EN_MANUAL_STAGE_AXIS.StageY, simulatedEndY, plan.Motion.StageSpeed, plan.Motion.StageAcceleration);
            }
        }

        /// <summary>After machining, wait for a stop beyond the target in the scan direction.</summary>
        public async Task WaitZeroLineEndPosition(ST_ZERO_LINE_CYCLE_PLAN plan, CancellationToken token)
        {
            if (!_stageBufferOwner)
                throw new InvalidOperationException("0선 Stage Buffer 소유권이 필요합니다.");
            var endY = plan.Motion.StageStartY + plan.Motion.StageDirection * plan.Motion.StageStroke;
            if (!double.IsFinite(endY))
                throw new InvalidDataException("0선 Stage Y 가공 완료 위치가 유효하지 않습니다.");
            var direction = plan.Motion.StageDirection;
            if (direction is not (-1 or 1))
                throw new InvalidDataException("0선 Stage Y 가공 방향은 -1 또는 +1이어야 합니다.");
            var clock = Stopwatch.StartNew();
            while (true)
            {
                Check(token);
                var values = _simulation ? null : _interfaces.Melsec.GetCachedSnapshot(["MOTOR_STAGE_Y_CUR_POS", "MOTOR_STAGE_Y_BUSY"], out _);
                var y = ZeroLineFeedback("MOTOR_STAGE_Y_CUR_POS", values);
                var busy = ZeroLineFeedback("MOTOR_STAGE_Y_BUSY", values);
                var passedTarget = direction < 0 ? y < endY : y > endY;
                if (passedTarget && busy == 0)
                {
                    Record("ZERO_LINE_STAGE_END_STOPPED", FormattableString.Invariant($"TargetY={endY}; ActualY={y}; Direction={direction}; PassedTarget=True; Busy={busy}"));
                    return;
                }
                if (clock.Elapsed > _owner._signalTimeout)
                    throw new TimeoutException(FormattableString.Invariant($"0선 Stage Y 목표 통과 후 정지 대기 시간 초과: Direction={direction}, TargetY={endY}, ActualY={y}, PassedTarget={passedTarget}, Busy={busy}."));
                await Task.Delay(20, token).ConfigureAwait(false);
            }
        }

        private double ZeroLineFeedback(string id, IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE>? values = null)
        {
            if (_simulation)
                return Feedback(id);
            var row = Route(id);
            var sample = values is null ? _interfaces.Melsec.GetCachedValue(id) : values.GetValueOrDefault(id);
            if (row.Direction != EN_MELSEC_DIRECTION.In || row.Access is not (EN_MELSEC_ACCESS.Read or EN_MELSEC_ACCESS.ReadWrite)
                || string.IsNullOrWhiteSpace(row.Address) || row.Address.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase)
                || sample is not { IsValid: true, Value: not null, UpdatedAt: not null } || DateTimeOffset.UtcNow - sample.UpdatedAt > TimeSpan.FromSeconds(2))
                throw new InvalidOperationException("0선 위치/상태의 유효한 최신 MELSEC 입력 스냅샷이 필요합니다: " + id);
            var result = Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(result))
                throw new InvalidDataException("0선 위치/상태 값이 유효하지 않습니다: " + id);
            return result;
        }

    }
}

public enum EN_STAGE_MOVE_MODE
{
    XY,
    ReviewX,
    StageY
}

public sealed record ST_STAGE_MOVE_TARGET(double X, double Y, double SpeedX, double SpeedY, double AccelerationX = 150, EN_STAGE_MOVE_MODE Mode = EN_STAGE_MOVE_MODE.XY);
public sealed record ST_STAGE_IOF_POINT(double X, double Y);
public sealed record ST_STAGE_IOF_PLAN(Guid Id, bool ZeroLine, IReadOnlyList<ST_STAGE_IOF_POINT> Points);
public sealed record ST_STAGE_FLOW_EVENT(long Number, int DeviceNo, string Phase, double? Value = null)
{
    public string Detail { get; init; } = "";
}
