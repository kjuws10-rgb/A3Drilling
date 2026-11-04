using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Drilling.Common.Interface;
using Drilling.Common.Motion;

namespace Drilling.UI.Menu.Menus;

public sealed class CManualPowerMeasurePanel : INotifyPropertyChanged
{
    private readonly CPowerMeasureService _service;
    private readonly Func<IReadOnlyList<ST_INTERFACE_DATA>> _getPowerMeters;
    private readonly Func<IReadOnlyList<ST_POWER_MEASURE_TARGET>, CancellationToken, Task>? _runMeasurement;
    private CancellationTokenSource? _runCancellation;
    private readonly Action<string> _warn;
    private readonly Func<string, bool> _confirm;
    private readonly Dictionary<int, CPowerMeasureRow> _rows = [];
    private CPowerMeasureRow? _selectedHead;
    private bool _busy;
    private bool _stopping;
    private bool _isGraphView;
    private string _mode = "FINE";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? RunningChanged;
    public IReadOnlyList<CPowerMeasureRow> Heads { get; private set; } = [];
    public IReadOnlyList<string> Modes { get; } = ["ROUGH", "FINE", "DETAIL"];
    public string Mode { get => _mode; set { _mode = value; Notify(); Notify(nameof(ToleranceText)); ApplySnapshot(); } }
    public string ToleranceText => IsReadOnly && _service.GetSnapshot().Heads.FirstOrDefault() is { } head
        ? $"±{head.Target.Options.Tolerance:0.0000} W / {head.Target.Options.MaxAttempts} attempts"
        : Mode switch { "ROUGH" => "±0.0500 W / 8 attempts", "DETAIL" => "±0.0030 W / 18 attempts", _ => "±0.0100 W / 12 attempts" };
    public bool IsRunning => _busy;
    public Func<bool>? StartAllowed { get; set; }
    public bool IsReadOnly { get; }
    public bool CanEdit => !_busy && !IsReadOnly;
    public Visibility RunControlsVisibility => IsReadOnly ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AutoSettingsVisibility => IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
    public bool HasHeads => Heads.Count > 0;
    public Visibility GraphViewVisibility => _isGraphView ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GridViewVisibility => _isGraphView ? Visibility.Collapsed : Visibility.Visible;
    public string ViewToggleText => _isGraphView ? "GRID VIEW" : "GRAPH VIEW";
    public int GraphColumns => Heads.Count <= 1 ? 1 : Heads.Count <= 4 ? 2 : 4;
    public string Summary { get; private set; } = "Head Select에서 측정할 Head를 선택해 주세요.";
    public string QueueText { get; private set; } = "PowerMeter를 선택해 주세요.";
    public string StageText { get; private set; } = "Stage Y 0 mm · IDLE";
    public CPowerMeasureGraph Graph => SelectedHead?.Graph ?? CPowerMeasureGraph.Empty;
    public PointCollection GraphPoints => Graph.Points;
    public string DetailTitle => SelectedHead is null ? "Head를 선택하면 측정 이력을 표시합니다." : $"{SelectedHead.HeadName} / {SelectedHead.SelectedPowerMeter?.DisplayText ?? "미선택"} · Target {SelectedHead.Target} W";
    public IReadOnlyList<ST_POWER_MEASURE_SAMPLE> Samples => SelectedHead?.Snapshot?.Samples ?? [];
    public string DetailMessage => SelectedHead?.Snapshot is { } snapshot
        ? $"{snapshot.Message} · {snapshot.StartDetail}" : "아직 측정한 값이 없습니다.";
    public CPowerMeasureRow? SelectedHead
    {
        get => _selectedHead;
        set
        {
            if (ReferenceEquals(_selectedHead, value)) return;
            _selectedHead?.SetGraphSelected(false);
            _selectedHead = value;
            _selectedHead?.SetGraphSelected(true);
            Notify(); RefreshDetail();
        }
    }
    public CButtonCommand StartCommand { get; }
    public CButtonCommand StopCommand { get; }
    public CButtonCommand ToggleViewCommand { get; }
    public CButtonCommand SelectGraphHeadCommand { get; }
    public CManualPowerStagePanel StageMotion { get; }

    public CManualPowerMeasurePanel(
        CPowerMeasureService service, 
        Func<IReadOnlyList<ST_INTERFACE_DATA>> getPowerMeters, 
        IPowerMeasureTable? powerTable,

        Action<string> warn, 
        Func<string, bool> confirm, 
        Func<IReadOnlyList<ST_MOTOR_STATUS_ITEM>>? readStageSnapshot = null,

        Func<EN_COMM_STATE>? readStageConnectionState = null, 
        CManualStageMotionSimulation? stageSimulation = null,
        CManualStageMoveOperation? stageMove = null,
        Func<IReadOnlyList<ST_POWER_MEASURE_TARGET>, CancellationToken, Task>? runMeasurement = null,
        bool readOnly = false
    )
    {
        _service = service; _getPowerMeters = getPowerMeters; _warn = warn; _confirm = confirm;
        _runMeasurement = runMeasurement; IsReadOnly = readOnly;
        StageMotion = new(readStageSnapshot ?? (() => []), warn, confirm, stageSimulation, readStageConnectionState, stageMove);
        StartCommand = new(async _ => await Start(), _ => CanEdit && Heads.Count > 0);
        StopCommand = new(async _ => await Stop(), _ => _busy && !_stopping);
        ToggleViewCommand = new(_ =>
        {
            _isGraphView = !_isGraphView;
            Notify(nameof(GraphViewVisibility)); Notify(nameof(GridViewVisibility)); Notify(nameof(ViewToggleText));
        });
        SelectGraphHeadCommand = new(value =>
        {
            if (value is CPowerMeasureRow row && Heads.Contains(row)) SelectedHead = row;
        });
    }

    public void UpdateHeads(IReadOnlyList<int> selectedHeads)
    {
        if (_busy) { ApplySnapshot(); return; }
        var meters = GetPowerMeterOptions();
        var rows = selectedHeads.Distinct().Order().Select(head =>
        {
            if (!_rows.TryGetValue(head, out var row))
            {
                _rows[head] = row = new(head);
                row.InputChanged += ApplySnapshot;
            }
            row.UpdatePowerMeters(meters);
            return row;
        }).ToArray();
        if (!Heads.SequenceEqual(rows))
        {
            Heads = rows;
            Notify(nameof(Heads)); Notify(nameof(HasHeads)); Notify(nameof(GraphColumns));
            if (SelectedHead is null || !Heads.Contains(SelectedHead)) SelectedHead = Heads.FirstOrDefault();
        }
        ApplySnapshot();
        NotifyCommands();
    }

    private IReadOnlyList<ST_MANUAL_POWER_METER_OPTION> GetPowerMeterOptions() => _getPowerMeters()
        .Where(m => m.Device == EN_EQP_MODULE.PowerMeter && m.Number >= 0)
        .DistinctBy(m => m.Number).OrderBy(m => m.Number)
        .Select(m => new ST_MANUAL_POWER_METER_OPTION(m.Number,
            $"{(string.IsNullOrWhiteSpace(m.NickName) ? "PowerMeter" : m.NickName)} [{m.Number}]")).ToArray();

    public void ApplySnapshot()
    {
        StageMotion.Refresh();
        var snapshot = _service.GetSnapshot();
        StageText = $"Stage Y 0 mm · {snapshot.StageState}";
        foreach (var row in Heads)
        {
            var state = snapshot.Heads.FirstOrDefault(h => h.Target.HeadNo == row.HeadNo);
            if (IsReadOnly && state is not null) row.ApplyTarget(state.Target);
            var matches = state is not null && (IsReadOnly || row.Matches(state.Target, Mode));
            row.Apply(matches ? state : null);
        }
        var queues = Heads.Where(h => h.SelectedPowerMeter is not null).GroupBy(h => h.SelectedPowerMeter!.Number);
        QueueText = string.Join("   |   ", queues.Select(q =>
            $"{q.First().SelectedPowerMeter!.DisplayText}: {string.Join(" / ", q.Select(h => $"H{h.HeadNo:00}"))}"));
        if (QueueText.Length == 0) QueueText = "INTERFACE에 Head별 전용 PowerMeter를 등록해 주세요.";
        var done = Heads.Count(h => h.Snapshot?.State is "OK" or "NG" or "ERROR" or "STOPPED");
        Summary = !HasHeads ? "Head Select에서 측정할 Head를 선택해 주세요." :
            _stopping ? "STOPPING · 진행 중인 측정과 대기 작업을 정리하고 있습니다." :
            $"{snapshot.Origin} · {(snapshot.IsRunning ? "측정 중" : "측정 현황")} · {done} / {Heads.Count} Heads 완료";
        foreach (var name in new[] { nameof(QueueText), nameof(Summary), nameof(StageText), nameof(ToleranceText) }) Notify(name);
        RefreshDetail();
    }

    public async Task Start()
    {
        if (IsReadOnly || StartAllowed?.Invoke() == false) return;
        if (_busy) return;
        try
        {
            var registered = GetPowerMeterOptions().Select(m => m.Number).ToHashSet();
            var targets = Heads.Select(h =>
            {
                if (h.SelectedPowerMeter is not { } meter || !registered.Contains(meter.Number))
                    throw new ArgumentException($"{h.HeadName}: INTERFACE에 등록된 PowerMeter를 선택해 주세요.");
                try
                {
                    return new ST_POWER_MEASURE_TARGET(
                        h.HeadNo, 
                        meter.Number, 
                        ParsePower(h.Target),

                        new(Mode, ParseInt(h.SettleTime, 0, 60000, "Settle Time"),
                            ParseInt(h.MeasureCount, 1, 1000, "Measure Count"), ParseInt(h.MeasureInterval, 0, 60000, "Interval"))
                    );
                }
                catch (ArgumentException error) { throw new ArgumentException($"{h.HeadName}: {error.Message}"); }
            }).ToArray();
            if (targets.Length == 0) throw new ArgumentException("측정할 Head를 선택해 주세요.");
            if (!_confirm($"선택한 {targets.Length}개 Head의 파워 측정을 시작하시겠습니까?\nStage Y가 0 mm에서 정지해 있어야 합니다.\n각 Head를 CENTER(0,0)로 이동한 뒤 전용 센서로 동시에 측정합니다.\n목표 W에 도달하면 최종 ATT 각도를 Recipe에도 저장합니다.")) return;
            if (StartAllowed?.Invoke() == false) return;
            SetBusy(true);
            _runCancellation = new();
            var run = (_runMeasurement ?? throw new InvalidOperationException("PowerMeasure 장치 실행이 연결되지 않았습니다."))(targets, _runCancellation.Token);
            ApplySnapshot();
            await run;
            var saveErrors = _service.GetSnapshot().Heads.Where(h => h.TableState == "ERROR")
                .Select(h => $"HEAD {h.Target.HeadNo:00}: {h.Message}").ToArray();
            if (saveErrors.Length > 0) _warn(string.Join("\n", saveErrors));
        }
        catch (Exception error) { _warn(error.Message); }
        finally { _runCancellation?.Dispose(); _runCancellation = null; SetBusy(false); ApplySnapshot(); }
    }

    public async Task Stop()
    {
        if (!_busy || _stopping) return;
        _stopping = true; NotifyCommands(); ApplySnapshot();
        try { _runCancellation?.Cancel(); await _service.Stop(); }
        finally { _stopping = false; ApplySnapshot(); NotifyCommands(); }
    }

    private static double ParsePower(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value) && value > 0 ? value : throw new ArgumentException("Target Power는 0보다 큰 숫자로 입력해 주세요.");
    private static int ParseInt(string text, int min, int max, string name) => int.TryParse(text, out var value) && value >= min && value <= max
        ? value : throw new ArgumentException($"{name}: {min}~{max} 범위의 정수를 입력해 주세요.");
    private void SetBusy(bool busy)
    {
        StageMotion.SetMeasurementRunning(busy);
        _busy = busy; Notify(nameof(IsRunning)); Notify(nameof(CanEdit)); NotifyCommands(); RunningChanged?.Invoke();
    }
    private void NotifyCommands() { StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); }
    private void RefreshDetail()
    {
        foreach (var name in new[] { nameof(Samples), nameof(DetailTitle), nameof(DetailMessage), nameof(Graph), nameof(GraphPoints) }) Notify(name);
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class CPowerMeasureRow(int headNo) : INotifyPropertyChanged
{
    private string _target = "1.2000", _settleTime = "300", _measureCount = "1", _measureInterval = "100";
    private ST_MANUAL_POWER_METER_OPTION? _selectedPowerMeter;
    private bool _metersInitialized;
    private bool _updatingPowerMeters;
    private bool _isGraphSelected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? InputChanged;
    public int HeadNo { get; } = headNo;
    public string HeadName => $"HEAD {HeadNo:00}";
    public bool IsGraphSelected => _isGraphSelected;
    public CPowerMeasureGraph Graph { get; private set; } = CPowerMeasureGraph.Empty;
    public void SetGraphSelected(bool selected)
    {
        if (_isGraphSelected == selected) return;
        _isGraphSelected = selected; Notify(nameof(IsGraphSelected));
    }
    public IReadOnlyList<ST_MANUAL_POWER_METER_OPTION> PowerMeters { get; private set; } = [];
    public ST_MANUAL_POWER_METER_OPTION? SelectedPowerMeter
    {
        get => _selectedPowerMeter;
        set { if (_updatingPowerMeters || _selectedPowerMeter == value) return; _selectedPowerMeter = value; Notify(); InputChanged?.Invoke(); }
    }
    public string Target { get => _target; set => SetInput(ref _target, value); }
    public string SettleTime { get => _settleTime; set => SetInput(ref _settleTime, value); }
    public string MeasureCount { get => _measureCount; set => SetInput(ref _measureCount, value); }
    public string MeasureInterval { get => _measureInterval; set => SetInput(ref _measureInterval, value); }
    public void UpdatePowerMeters(IReadOnlyList<ST_MANUAL_POWER_METER_OPTION> meters)
    {
        if (_metersInitialized && PowerMeters.SequenceEqual(meters)) return;
        var selected = meters.FirstOrDefault(m => m.Number == HeadNo - 1);
        _metersInitialized = true;
        // Replacing the dropdown list temporarily clears its selection. Keep the selection by device ID.
        _updatingPowerMeters = true;
        try
        {
            PowerMeters = meters;
            Notify(nameof(PowerMeters));
            _selectedPowerMeter = selected;
            Notify(nameof(SelectedPowerMeter));
        }
        finally { _updatingPowerMeters = false; }
    }
    public void ApplyTarget(ST_POWER_MEASURE_TARGET target)
    {
        if (ReferenceEquals(Snapshot?.Target, target)) return;
        _target = target.TargetPower.ToString("0.0000", CultureInfo.InvariantCulture);
        _settleTime = target.Options.SettleTimeMs.ToString(CultureInfo.InvariantCulture);
        _measureCount = target.Options.MeasureCount.ToString(CultureInfo.InvariantCulture);
        _measureInterval = target.Options.MeasureIntervalMs.ToString(CultureInfo.InvariantCulture);
        foreach (var name in new[] { nameof(Target), nameof(SettleTime), nameof(MeasureCount), nameof(MeasureInterval) }) Notify(name);
    }
    public bool Matches(ST_POWER_MEASURE_TARGET target, string mode) =>
        SelectedPowerMeter?.Number == target.PowerMeterNo &&
        double.TryParse(Target, NumberStyles.Float, CultureInfo.InvariantCulture, out var power) && power == target.TargetPower &&
        int.TryParse(SettleTime, out var settle) && settle == target.Options.SettleTimeMs &&
        int.TryParse(MeasureCount, out var count) && count == target.Options.MeasureCount &&
        int.TryParse(MeasureInterval, out var interval) && interval == target.Options.MeasureIntervalMs && mode == target.Options.Mode;
    private void SetInput(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value; Notify(name); RefreshGraph(); InputChanged?.Invoke();
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public ST_POWER_MEASURE_HEAD? Snapshot { get; private set; }
    public string Measured => Snapshot?.Power?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "—";
    public string Error => Snapshot?.Power is { } value ? (value - Snapshot.Target.TargetPower).ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture) : "—";
    public string Angle => Snapshot?.Angle?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "—";
    public string Tolerance => Snapshot is { } state ? state.Target.Options.Tolerance.ToString("0.0000", CultureInfo.InvariantCulture) : "—";
    public string AttemptProgress => Snapshot is { } state ? $"{state.Samples.Count} / {state.Target.Options.MaxAttempts}" : "—";
    public string GraphSummary => Snapshot is null ? $"ATT {Angle}°" : $"ATT {Angle}° · ±{Tolerance} W · {AttemptProgress}회";
    public string State => Snapshot?.State ?? "IDLE";
    public Brush StateBackground => State switch
    {
        "OK" => Brushes.SeaGreen, "ERROR" => Brushes.Firebrick, "NG" => Brushes.DarkOrange,
        "POSITIONING" or "MOVING" or "SETTLING" or "MEASURING" or "FINISHING" or "SAVING" => Brushes.SteelBlue,
        _ => Brushes.DimGray
    };
    public void Apply(ST_POWER_MEASURE_HEAD? state)
    {
        if (ReferenceEquals(Snapshot, state)) return;
        Snapshot = state;
        RefreshGraph();
        foreach (var name in new[] { nameof(Snapshot), nameof(Measured), nameof(Error), nameof(Angle), nameof(Tolerance), nameof(AttemptProgress), nameof(GraphSummary), nameof(State), nameof(StateBackground) }) PropertyChanged?.Invoke(this, new(name));
    }
    private void RefreshGraph()
    {
        var target = Snapshot?.Target.TargetPower ??
            (double.TryParse(Target, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : 0);
        Graph = CPowerMeasureGraph.Create(Snapshot?.Samples ?? [], target);
        Notify(nameof(Graph));
    }
}

public sealed record CPowerMeasureGraph(PointCollection Points, double TargetLineY, string Maximum, string Attempts)
{
    public static CPowerMeasureGraph Empty { get; } = Create([], 1.2);
    public Visibility LastPointVisibility => Points.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public double LastPointLeft => Points.Count > 0 ? Points[^1].X - 5 : 0;
    public double LastPointTop => Points.Count > 0 ? Points[^1].Y - 5 : 0;
    public static CPowerMeasureGraph Create(IReadOnlyList<ST_POWER_MEASURE_SAMPLE> samples, double target)
    {
        var maximum = Math.Max(0.1, Math.Max(target, samples.Select(s => s.Power).DefaultIfEmpty(0).Max()) * 1.15);
        double Y(double power) => 152 - 144 * power / maximum;
        var points = new PointCollection(samples.Select((sample, index) => new Point(
            samples.Count == 1 ? 300 : 8 + index * 584.0 / (samples.Count - 1), Y(sample.Power))));
        points.Freeze();
        return new(points, Y(target), $"{maximum:0.0000} W", samples.Count == 0 ? "측정 대기" : $"Attempt 1 – {samples[^1].Attempt}");
    }
}

public sealed record ST_MANUAL_POWER_METER_OPTION(int Number, string DisplayText);
