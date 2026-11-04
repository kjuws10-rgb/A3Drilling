using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using Drilling.Common.Interface;
using Drilling.Common.Motion;

namespace Drilling.UI.Menu.Menus;

/// <summary>Owns only this Manual menu's accepted fixed-position move, including cancellation cleanup.</summary>
public sealed class CManualStageMoveOperation(
    Func<EN_MANUAL_STAGE_AXIS, double, bool, double, CancellationToken, Task> move,
    Action? stopSharedMove = null, Func<bool>? sharedMoveRunning = null)
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource<bool>? _completion;
    public event Action? Changed;
    public bool IsRunning { get { lock (_sync) return _cancellation is not null; } }
    public bool IsStopping { get { lock (_sync) return _cancellation?.IsCancellationRequested == true; } }
    public bool CanStop => IsRunning || sharedMoveRunning?.Invoke() == true;

    public async Task Move(EN_MANUAL_STAGE_AXIS axis, double value, bool relative, double speed)
    {
        CancellationTokenSource cancellation;
        TaskCompletionSource<bool> completion;
        lock (_sync)
        {
            if (_cancellation is not null)
                throw new InvalidOperationException("수동 Stage/Review 이동 또는 정리 작업이 진행 중입니다.");
            _cancellation = cancellation = new();
            _completion = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            Changed?.Invoke();
            await move(axis, value, relative, speed, cancellation.Token);
        }
        finally
        {
            lock (_sync) { _cancellation = null; _completion = null; }
            cancellation.Dispose();
            completion.TrySetResult(true);
            Changed?.Invoke();
        }
    }

    public Task StopAsync() => StopAsync(false);
    public Task StopAsync(bool includeSharedMove)
    {
        CancellationTokenSource? cancellation;
        Task completion;
        lock (_sync) { cancellation = _cancellation; completion = _completion?.Task ?? Task.CompletedTask; }
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { } // The move may have completed between capture and cancellation.
        if (includeSharedMove) stopSharedMove?.Invoke();
        Changed?.Invoke();
        return completion;
    }
    public void Stop() => _ = StopAsync();
    public void Stop(bool includeSharedMove) => _ = StopAsync(includeSharedMove);
}

public sealed class CManualPowerStagePanel : INotifyPropertyChanged
{
    private readonly Func<IReadOnlyList<ST_MOTOR_STATUS_ITEM>> _readCachedStatus;
    private readonly Func<EN_COMM_STATE> _readConnectionState;
    private readonly Func<string, bool> _confirm;
    private readonly Action<string> _warn;
    private readonly CManualStageMotionSimulation _simulation;
    private readonly CManualStageMoveOperation? _stageMove;
    private readonly DispatcherTimer _animation;
    private EN_COMM_STATE _connectionState = EN_COMM_STATE.Offline;
    private bool _active;
    private readonly HashSet<object> _activeViews = new(ReferenceEqualityComparer.Instance);
    private bool _measurementRunning;
    private bool _confirming;
    private int _generation;
    private bool _reserved;
    private bool _sharedMoveRunning;
    private string _motionMessage = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<CManualPowerStageAxis> Axes { get; }
    public CButtonCommand StopCommand { get; }
    public Func<bool>? MoveAllowed { get; set; }
    public string Mode => _connectionState switch { EN_COMM_STATE.Simulation => "SIMULATION", EN_COMM_STATE.Online => "MELSEC ONLINE", _ => "MELSEC OFFLINE" };
    public Brush ModeBackground => _connectionState switch
    {
        EN_COMM_STATE.Simulation => CStatusBrush.Simul,
        EN_COMM_STATE.Online => CStatusBrush.Online,
        _ => CStatusBrush.Offline
    };
    public bool IsSimulation => _connectionState == EN_COMM_STATE.Simulation;
    public bool CanMove => (IsSimulation || _connectionState == EN_COMM_STATE.Online && _stageMove is not null)
        && _active && !_measurementRunning && !_confirming && (!IsSimulation || !_simulation.IsReserved) && _stageMove?.CanStop != true;
    public bool CanJog => IsSimulation && CanMove;
    public bool CanStop => _stageMove?.CanStop == true || IsSimulation && !_simulation.IsReserved;
    public bool CanEdit => !_measurementRunning && !_confirming && (!IsSimulation || !_simulation.IsReserved) && _stageMove?.CanStop != true;
    public string StatusText => _connectionState == EN_COMM_STATE.Offline ? "MELSEC 연결 대기" :
        _stageMove?.IsStopping == true ? "STOPPING · 이동 신호 정리 대기" :
        _stageMove?.CanStop == true ? "Process Move · 허가/이동/완료 확인 중" :
        _measurementRunning ? "PowerMeasure 진행 중 · 수동 이동 차단" :
        _motionMessage.Length > 0 ? _motionMessage :
        !IsSimulation ? (_stageMove is null ? "현재값 표시 · 실제 이동 연결 대기" : "MELSEC · ABS/REL 공통 Process Move · 실제 JOG 미지원") :
        _simulation.IsReserved ? "SIMULATION · MOF 진행 중" :
        "SIMULATION · JOG 속도 5 mm/sec";

    public CManualPowerStagePanel(
        Func<IReadOnlyList<ST_MOTOR_STATUS_ITEM>> readCachedStatus,

        Action<string> warn, 
        Func<string, bool> confirm, 
        CManualStageMotionSimulation? simulation = null,

        Func<EN_COMM_STATE>? readConnectionState = null,
        CManualStageMoveOperation? stageMove = null
    )
    {
        _readCachedStatus = readCachedStatus; _warn = warn; _confirm = confirm;
        _readConnectionState = readConnectionState ?? (() => EN_COMM_STATE.Offline);
        _simulation = simulation ?? new();
        _stageMove = stageMove;
        if (_stageMove is not null) _stageMove.Changed += UpdateCommands;
        Axes = [new(this, EN_MANUAL_STAGE_AXIS.StageY, "Stage Y", "Stage Y"), new(this, EN_MANUAL_STAGE_AXIS.ReviewX, "Review X", "Review X")];
        StopCommand = new(_ => Stop(includeSharedMove: true), _ => CanStop);
        // This timer animates local simulation only; MELSEC feedback is read from the shared cache by Refresh.
        _animation = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _animation.Tick += (_, _) => Refresh();
        Refresh();
    }

    // Scan, MOF and PowerMeasure bind the same panel. A hidden/unloaded sibling
    // must not disable commands belonging to another visible view.
    public void SetViewActive(object view, bool active)
    {
        if (active) _activeViews.Add(view);
        else _activeViews.Remove(view);
        SetActive(_activeViews.Count > 0);
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (!active)
        {
            // Hide controls without cancelling an accepted ABS/REL move. JOG still
            // requires a held button, and pending confirmations cannot outlive the tab.
            _generation++;
            _simulation.StopJog();
        }
        UpdateCommands(); Refresh();
    }

    public void SetMeasurementRunning(bool running)
    {
        if (_measurementRunning == running) return;
        _measurementRunning = running;
        if (running) Stop();
        UpdateCommands();
    }

    public void Refresh()
    {
        if (_sharedMoveRunning != (_stageMove?.CanStop == true))
        { _sharedMoveRunning = _stageMove?.CanStop == true; UpdateCommands(); }
        if (_reserved != _simulation.IsReserved) { _reserved = _simulation.IsReserved; UpdateCommands(); }
        var connectionState = _readConnectionState();
        if (connectionState != _connectionState)
        {
            Stop(); _connectionState = connectionState;
            Notify(nameof(Mode)); Notify(nameof(ModeBackground)); Notify(nameof(IsSimulation)); UpdateCommands();
        }
        if (IsSimulation) { RefreshSimulation(); return; }
        if (_connectionState == EN_COMM_STATE.Offline)
        {
            foreach (var axis in Axes) axis.SetFeedback("—", "—", "MELSEC 연결 안 됨", "MELSEC 연결 안 됨");
            return;
        }
        var snapshot = _readCachedStatus();
        foreach (var axis in Axes)
        {
            var value = snapshot.FirstOrDefault(item => item.Name == axis.SnapshotName);
            axis.SetFeedback(value?.Position ?? "—", value?.Speed ?? "—",
                value?.PositionMessage ?? "현재 위치 미설정", value?.SpeedMessage ?? "현재 속도 미설정");
        }
    }

    private void RefreshSimulation()
    {
        if (!IsSimulation) { _animation.Stop(); return; }
        var snapshot = _simulation.GetSnapshot();
        foreach (var axis in Axes)
        {
            var value = snapshot.Single(item => item.Axis == axis.Axis);
            axis.SetFeedback(value.Position.ToString("F3", CultureInfo.InvariantCulture), value.Speed.ToString("F3", CultureInfo.InvariantCulture),
                "시뮬레이션 위치 (실장비 값 아님)", "시뮬레이션 속도 (실장비 값 아님)");
        }
        if (snapshot.All(axis => axis.Speed == 0) && _stageMove?.IsRunning != true) _animation.Stop();
    }

    internal async Task Move(CManualPowerStageAxis axis, bool relative)
    {
        if (MoveAllowed?.Invoke() == false) return;
        Refresh();
        if (!CanMove) return;
        var text = relative ? axis.Relative : axis.Absolute;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        { _warn($"{axis.Name}: 올바른 이동값을 입력해 주세요."); return; }
        var speedText = relative ? axis.RelativeSpeed : axis.AbsoluteSpeed;
        if (!double.TryParse(speedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) || !double.IsFinite(speed) || speed <= 0)
        { _warn($"{axis.Name}: 이동 속도는 0보다 큰 숫자로 입력해 주세요. (mm/sec)"); return; }
        var generation = _generation;
        _confirming = true; UpdateCommands();
        bool accepted;
        try { accepted = _confirm($"[{(IsSimulation ? "SIMULATION" : "MELSEC REAL")}] {axis.Name}\n{(relative ? "REL 이동 거리" : "ABS 목표 위치")}: {value:G} mm\n이동 속도: {speed:G} mm/sec\n\n이동하시겠습니까?"); }
        finally { _confirming = false; UpdateCommands(); }
        Refresh();
        if (!accepted || !CanMove || generation != _generation || MoveAllowed?.Invoke() == false) return;
        try
        {
            _motionMessage = "";
            if (IsSimulation) _animation.Start();
            if (_stageMove is not null) await _stageMove.Move(axis.Axis, value, relative, speed);
            else _simulation.Move(axis.Axis, value, relative, speed);
            _motionMessage = IsSimulation ? "SIMULATION · 이동 완료" : "MELSEC · 이동 완료";
        }
        catch (OperationCanceledException) { _motionMessage = "이동 중지 · 공통 시퀀스 정리 상태를 확인하세요."; }
        catch (Exception error) { _motionMessage = error.Message; _warn(error.Message); }
        finally { Refresh(); UpdateCommands(); }
    }

    public bool StartJog(string direction)
    {
        if (MoveAllowed?.Invoke() == false) return false;
        Refresh();
        if (!CanJog) return false;
        var target = direction switch
        {
            "Y+" => (EN_MANUAL_STAGE_AXIS.StageY, 1), "Y-" => (EN_MANUAL_STAGE_AXIS.StageY, -1),
            "X-" => (EN_MANUAL_STAGE_AXIS.ReviewX, -1), "X+" => (EN_MANUAL_STAGE_AXIS.ReviewX, 1),
            _ => (EN_MANUAL_STAGE_AXIS.StageY, 0)
        };
        if (target.Item2 == 0) return false;
        try { _simulation.StartJog(target.Item1, target.Item2); _animation.Start(); RefreshSimulation(); return true; }
        catch (InvalidOperationException error) { _warn(error.Message); return false; }
    }

    public void Stop(bool includeSharedMove = false)
    {
        _generation++;
        _stageMove?.Stop(includeSharedMove);
        if (_stageMove?.IsRunning != true && !_simulation.IsReserved) _simulation.Stop();
        _animation.Stop();
        if (IsSimulation) RefreshSimulation();
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        Notify(nameof(CanMove)); Notify(nameof(CanJog)); Notify(nameof(CanStop)); Notify(nameof(CanEdit)); Notify(nameof(StatusText));
        foreach (var axis in Axes) { axis.AbsoluteCommand.NotifyCanExecuteChanged(); axis.RelativeCommand.NotifyCanExecuteChanged(); }
        StopCommand.NotifyCanExecuteChanged();
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class CManualPowerStageAxis : INotifyPropertyChanged
{
    private string _absolute = "0.000", _relative = "0.000";
    private string _absoluteSpeed = "5.000", _relativeSpeed = "5.000";
    public event PropertyChangedEventHandler? PropertyChanged;
    public EN_MANUAL_STAGE_AXIS Axis { get; }
    public string Name { get; }
    internal string SnapshotName { get; }
    public string Absolute { get => _absolute; set { _absolute = value; Notify(); } }
    public string Relative { get => _relative; set { _relative = value; Notify(); } }
    public string AbsoluteSpeed { get => _absoluteSpeed; set { _absoluteSpeed = value; Notify(); } }
    public string RelativeSpeed { get => _relativeSpeed; set { _relativeSpeed = value; Notify(); } }
    public string Position { get; private set; } = "—";
    public string Speed { get; private set; } = "—";
    public string PositionMessage { get; private set; } = "";
    public string SpeedMessage { get; private set; } = "";
    public CButtonCommand AbsoluteCommand { get; }
    public CButtonCommand RelativeCommand { get; }
    public CManualPowerStageAxis(CManualPowerStagePanel panel, EN_MANUAL_STAGE_AXIS axis, string name, string snapshotName)
    {
        Axis = axis; Name = name; SnapshotName = snapshotName;
        AbsoluteCommand = new(async _ => await panel.Move(this, false), _ => panel.CanMove);
        RelativeCommand = new(async _ => await panel.Move(this, true), _ => panel.CanMove);
    }
    internal void SetFeedback(string position, string speed, string positionMessage, string speedMessage)
    {
        if (Position == position && Speed == speed && PositionMessage == positionMessage && SpeedMessage == speedMessage) return;
        Position = position; Speed = speed; PositionMessage = positionMessage; SpeedMessage = speedMessage;
        foreach (var name in new[] { nameof(Position), nameof(Speed), nameof(PositionMessage), nameof(SpeedMessage) }) Notify(name);
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
