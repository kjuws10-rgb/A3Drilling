namespace Drilling.UI.Popup
{
    using System.Windows;
    using System.Windows.Threading;
    using Drilling.Common.Interface;
    using Drilling.Common.Managers;
    using Drilling.Common.Correction;
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Globalization;
    using System.Runtime.CompilerServices;
    using System.Windows.Media;
    using Drilling.UI.Menu;

    public partial class CMelsecStageSimulationDialog : Window
    {
        private readonly DispatcherTimer _timer;
        public CMelsecStageSimulationPanel Panel { get; }

        public CMelsecStageSimulationDialog(CInterfaceManager interfaces, Func<CancellationToken, Task<ST_RECIPE_DEFINITION?>>? readRecipe = null)
        {
            InitializeComponent();
            Panel = new(interfaces, readRecipe);
            DataContext = Panel;
            Height = Math.Min(Height, SystemParameters.WorkArea.Height * 0.9);
            _timer = new(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += RefreshSnapshot;
            Loaded += (_, _) =>
            {
                Panel.Refresh();
                _timer.Start();
            };
            Closed += (_, _) =>
            {
                _timer.Stop();
                _timer.Tick -= RefreshSnapshot;
            };
            Panel.Refresh();
        }

        // UI refresh only copies the existing cache. It never reads a PLC/device directly.
        private void RefreshSnapshot(object? sender, EventArgs e) => Panel.Refresh();
        private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    }

    public sealed class CMelsecStageSimulationPanel(CInterfaceManager interfaces, Func<CancellationToken, Task<ST_RECIPE_DEFINITION?>>? readRecipe = null) : INotifyPropertyChanged
    {
        private IReadOnlyList<ST_MELSEC_MAP_DATA> _map = [];
        private string _connectionText = "";
        private bool _lastCanChangeReplies = true, _lastAutomaticReplies = true, _lastCanSetZeroSegments;
        private string _zeroSegmentInput = "0", _lastZeroSegmentState = "";
        private CButtonCommand? _setZeroSegments;
        private CButtonCommand? _seedMeasurements;
        private bool _seeding, _lastCanSeed;
        private string _measurementStatus = "Distortion 측정값: mm 단위로 입력하세요. BASE 복사 후 일부 값만 바꿔서 시험할 수 있습니다.";
        public string MeasurementStatus => _measurementStatus;
        public bool CanSeedMeasurements => readRecipe is not null && !_seeding &&
            Rows.Count(row => row.IsMeasurement) == 20 && Rows.Where(row => row.IsMeasurement).All(row => row.CanSet);
        public CButtonCommand SeedMeasurementsCommand => _seedMeasurements ??= new(async _ =>
        {
            if (!CanSeedMeasurements) return;
            _seeding = true; _seedMeasurements?.NotifyCanExecuteChanged();
            try
            {
                var recipe = await readRecipe!(CancellationToken.None) ?? throw new InvalidOperationException("선택된 Recipe가 없습니다.");
                var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < 10; index++)
                foreach (var axis in new[] { "X", "Y" })
                {
                    var key = $"ALIGNBASE_{(index < 4 ? "AK" + (index + 1) : "DK" + (index - 3))}_{axis}";
                    var text = recipe.Parameters.SingleOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                        throw new InvalidOperationException($"Recipe {recipe.Name}의 {key} 값을 확인하세요.");
                    values[CAlignDistortionCycle.ResultId(key)] = value;
                }
                interfaces.Melsec.SetDistortionMeasurementSimulationValues(values);
                foreach (var row in Rows.Where(row => row.IsMeasurement)) row.InputValue = values[row.Id].ToString("0.000", CultureInfo.InvariantCulture);
                _measurementStatus = $"Recipe {recipe.Name} BASE → 시뮬레이션 측정값 20개 반영 완료 (mm)";
                PropertyChanged?.Invoke(this, new(nameof(MeasurementStatus)));
                Refresh();
            }
            catch (Exception error) { CRecipeConfirmDialog.ShowWarning("Distortion 시뮬 측정값", error.Message); }
            finally { _seeding = false; _seedMeasurements?.NotifyCanExecuteChanged(); }
        }, _ => CanSeedMeasurements);

        public string ZeroSegmentInput
        {
            get => _zeroSegmentInput;
            set
            {
                if (_zeroSegmentInput == value)
                    return;
                _zeroSegmentInput = value;
                PropertyChanged?.Invoke(this, new(nameof(ZeroSegmentInput)));
            }
        }

        public string ZeroSegmentState => interfaces.StageReviewSequences.LastIofPlan is { ZeroLine: true } plan ? $"{interfaces.StageReviewSequences.SimulatedZeroLineCompletedSegments} / {plan.Points.Count} 구간 완료" : "0선 계획 없음";
        public bool CanSetZeroSegments => !AutomaticReplies && interfaces.StageReviewSequences.LastIofPlan is { ZeroLine: true } && interfaces.IsSimul(EN_EQP_MODULE.Melsec, interfaces.Melsec.Map.Single(row => row.Id == "IOF_SETTING").DeviceNo);
        public CButtonCommand SetZeroSegmentsCommand => _setZeroSegments ??= new(_ =>
        {
            try
            {
                if (!int.TryParse(ZeroSegmentInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    throw new InvalidOperationException("0~N의 정수를 입력해 주세요.");
                interfaces.StageReviewSequences.SetSimulatedZeroLineCompletedSegments(number);
            }
            catch (Exception error)
            {
                CRecipeConfirmDialog.ShowWarning("0선 촬영구간", error.Message);
            }
        }, _ => CanSetZeroSegments);

        public event PropertyChangedEventHandler? PropertyChanged;
        public ObservableCollection<CMelsecStageSimulationRow> Rows { get; } = [];
        public string ConnectionText => _connectionText;
        public bool CanChangeAutomaticReplies => !interfaces.StageReviewSequences.IsActive;

        public bool AutomaticReplies
        {
            get => interfaces.StageReviewSequences.AutomaticSimulationReplies;
            set
            {
                if (value == AutomaticReplies)
                    return;
                try
                {
                    interfaces.StageReviewSequences.AutomaticSimulationReplies = value;
                }
                catch (Exception error)
                {
                    CRecipeConfirmDialog.ShowWarning("Stage 자동 응답", error.Message);
                }

                _lastAutomaticReplies = AutomaticReplies;
                PropertyChanged?.Invoke(this, new(nameof(AutomaticReplies)));
            }
        }

        public void Refresh()
        {
            if (_lastZeroSegmentState != ZeroSegmentState)
            {
                _lastZeroSegmentState = ZeroSegmentState;
                PropertyChanged?.Invoke(this, new(nameof(ZeroSegmentState)));
            }

            if (_lastCanSetZeroSegments != CanSetZeroSegments)
            {
                _lastCanSetZeroSegments = CanSetZeroSegments;
                PropertyChanged?.Invoke(this, new(nameof(CanSetZeroSegments)));
                _setZeroSegments?.NotifyCanExecuteChanged();
            }

            var values = interfaces.Melsec.GetCachedSnapshot(out var map);
            var signals = map.Where(row => CMelsec.IsStageFlowSimulationSignal(row) || CMelsec.IsStageSimulationSignal(row) || CMelsec.IsStageAlignSimulationSignal(row) || CMelsec.IsStageProtectWindowSimulationSignal(row) || CMelsec.IsDistortionMeasurementSimulationValue(row)).OrderBy(row => ScenarioOrder(row.Group)).ThenBy(row => row.DeviceNo).ThenBy(row => SignalOrder(row.Id)).ThenBy(row => row.Id).ToArray();
            if (!_map.SequenceEqual(signals))
            {
                _map = signals;
                Rows.Clear();
                foreach (var signal in signals)
                    Rows.Add(new(signal, value =>
                    {
                        if (CMelsec.IsStageAlignSimulationSignal(signal))
                            interfaces.Melsec.SetStageAlignSimulationValue(signal.Id, value);
                        else if (CMelsec.IsStageProtectWindowSimulationSignal(signal))
                            interfaces.Melsec.SetStageProtectWindowSimulationValue(signal.Id, value);
                        else if (CMelsec.IsStageFlowSimulationSignal(signal))
                            interfaces.Melsec.SetStageFlowSimulationValue(signal.Id, value);
                        else
                            interfaces.Melsec.SetStageSimulationSignal(signal.Id, value == 1);
                    }, setMeasurement: value => interfaces.Melsec.SetDistortionMeasurementSimulationValue(signal.Id, value)));
            }

            var connections = interfaces.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).ToDictionary(row => row.Number);
            var text = string.Join("   |   ", signals.Select(row => row.DeviceNo).Distinct().Select(number =>
            {
                var state = connections.TryGetValue(number, out var status) ? status.IsSimulation ? "SIMULATION" : status.ConnectionState.ToString().ToUpperInvariant() : "OFFLINE";
                return $"MELSEC_{number} : {state}";
            }));
            if (text.Length == 0)
                text = "등록된 Stage 수신 신호가 없습니다.";
            if (_connectionText != text)
            {
                _connectionText = text;
                PropertyChanged?.Invoke(this, new(nameof(ConnectionText)));
            }

            foreach (var row in Rows)
            {
                values.TryGetValue(row.Id, out var sample);
                row.Refresh(sample, interfaces.IsSimul(EN_EQP_MODULE.Melsec, row.DeviceNo));
            }

            if (_lastCanSeed != CanSeedMeasurements)
            {
                _lastCanSeed = CanSeedMeasurements;
                PropertyChanged?.Invoke(this, new(nameof(CanSeedMeasurements)));
                _seedMeasurements?.NotifyCanExecuteChanged();
            }
            if (_lastCanChangeReplies != CanChangeAutomaticReplies)
            {
                _lastCanChangeReplies = CanChangeAutomaticReplies;
                PropertyChanged?.Invoke(this, new(nameof(CanChangeAutomaticReplies)));
            }

            if (_lastAutomaticReplies != AutomaticReplies)
            {
                _lastAutomaticReplies = AutomaticReplies;
                PropertyChanged?.Invoke(this, new(nameof(AutomaticReplies)));
            }
        }

        private static int ScenarioOrder(string group) => group.ToUpperInvariant() switch
        {
            "DISTORTION" => -2,
            "VISION_DISTORTION_DATA" => -1,
            "IF_DOREADY" => 0,
            "IF_PROCESS" => 1,
            "IF_REVIEW" => 2,
            "IF_TOP_POWERMETER" => 3,
            "IF_PROCESS_MOVE" => 4,
            "IF_IOF" => 5,
            "IF_ALIGN" => 6,
            "IF_PTW" => 7,
            _ => 8
        };
        private static int SignalOrder(string id) => id.Equals("SDC_DO_ALIGN_STATUS", StringComparison.OrdinalIgnoreCase) ? 0 : id.Equals("SDC_DO_ALIGN_COMPLETE", StringComparison.OrdinalIgnoreCase) ? 1 : id.Contains("_ABLE", StringComparison.OrdinalIgnoreCase) ? 0 : id.Contains("_START", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    public sealed class CMelsecStageSimulationRow : INotifyPropertyChanged
    {
        private bool _canSet;
        private string _currentValue = "—";
        private string _inputValue;
        private readonly double _readScale;
        private Brush _stateBrush = Brushes.Gray;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Id { get; }
        public int DeviceNo { get; }
        public string Scenario { get; }
        public string Name { get; }
        public string Address { get; }
        public string Description { get; }
        public string CurrentValue => _currentValue;
        public Brush StateBrush => _stateBrush;
        public bool CanSet => _canSet;
        public bool IsNumeric { get; }
        public bool IsCount { get; }
        public bool IsMeasurement { get; }
        public string InputHint => IsMeasurement ? "mm 단위의 부호 포함 숫자 (소수 입력 가능)" : "원시 정수: 0~65535";
        public bool IsAlignBusyWord { get; }

        public string InputValue
        {
            get => _inputValue;
            set
            {
                if (_inputValue == value)
                    return;
                _inputValue = value;
                Changed();
            }
        }

        public CButtonCommand SetOnCommand { get; }
        public CButtonCommand SetOffCommand { get; }
        public CButtonCommand SetValueCommand { get; }

        public CMelsecStageSimulationRow(ST_MELSEC_MAP_DATA data, Action<int> set, Action<string>? showWarning = null, Action<double>? setMeasurement = null)
        {
            Id = data.Id;
            IsMeasurement = CMelsec.IsDistortionMeasurementSimulationValue(data);
            DeviceNo = data.DeviceNo;
            Scenario = data.Group.ToUpperInvariant() switch
            {
                "DISTORTION" => "Distortion",
                "VISION_DISTORTION_DATA" => "Distortion Data",
                "IF_DOREADY" => "DoReady",
                "IF_PROCESS" => "Process",
                "IF_REVIEW" => "Review",
                "IF_TOP_POWERMETER" => "TPM",
                "IF_PROCESS_MOVE" => "Process / Move",
                "IF_IOF" => "IOF",
                "IF_ALIGN" => "Align",
                "IF_PTW" => "Protect Window",
                _ => data.Group
            };
            IsCount = data.Id.Equals(CMelsec.StageProtectWindowCountId, StringComparison.OrdinalIgnoreCase);
            IsAlignBusyWord = data.Id.Equals(CMelsec.StageAlignBusyWordId, StringComparison.OrdinalIgnoreCase);
            Name = data.Id switch
            {
                "IPS_REVIEW_CONTROL_MODE_X" => "Review X Permit",
                "IPS_REVIEW_CONTROL_MODE_Y" => "Stage Y Permit",
                "IPS_IOF_READ_COMPLETE" => "IOF Read Done",
                "IPS_IOF_BUFFER_RUN_COMPLETE" => "IOF Buffer Done",
                CMelsec.StageAlignBusyWordId => "ALIGN BUSY (bit 1)",
                CMelsec.StageProtectWindowCountId => "Tray Count",
                CMelsec.StageProtectWindowCompleteId => "교체완료 신호",
                _ => data.Name
            };
            if (IsMeasurement)
            {
                var parts = data.Id.Split('_'); var position = int.Parse(parts[1].AsSpan(3), CultureInfo.InvariantCulture);
                var camera = int.Parse(parts[2].AsSpan(3, 1), CultureInfo.InvariantCulture); var axis = data.Id[^1];
                Name = position == 0 ? $"AK{camera} {axis} (mm)" : $"Group {position - 1} / DK{(position - 1) * 2 + (camera == 2 ? 1 : 2)} {axis} (mm)";
            }
            Address = CMelsec.IsSimulationOnlyRoute(data) ? $"MELSEC_{data.DeviceNo} / SIM ONLY" : $"MELSEC_{data.DeviceNo} / {data.Address}";
            Description = data.Description;
            IsNumeric = CMelsec.IsStageAlignSimulationSignal(data) || IsCount || IsMeasurement;
            _inputValue = IsMeasurement ? "0" : IsAlignBusyWord ? "2" : IsNumeric && !IsCount ? "10000" : "0";
            _readScale = data.ReadScale;
            var warn = showWarning ?? (message => CRecipeConfirmDialog.ShowWarning("Stage 신호 변경 실패", message));
            void Set(int value)
            {
                if (!_canSet)
                    return;
                try
                {
                    set(value);
                }
                catch (Exception exception)
                {
                    warn(exception.Message);
                }
            }

            SetOnCommand = new(_ => Set(1), _ => _canSet && !IsNumeric);
            SetOffCommand = new(_ => Set(0), _ => _canSet && !IsNumeric);
            SetValueCommand = new(_ =>
            {
                if (!_canSet || !IsNumeric)
                    return;
                if (IsMeasurement)
                {
                    try
                    {
                        if (!double.TryParse(InputValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) || !double.IsFinite(mm))
                            throw new InvalidOperationException("유효한 mm 숫자를 입력하세요. 음수와 소수 입력이 가능합니다.");
                        if (setMeasurement is null) throw new InvalidOperationException("측정값 입력이 연결되어 있지 않습니다.");
                        setMeasurement(mm);
                    }
                    catch (Exception error) { warn(error.Message); }
                    return;
                }
                if (!int.TryParse(InputValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value is < 0 or > ushort.MaxValue)
                {
                    warn("0~65535의 원시 정수를 입력하세요.");
                    return;
                }

                Set(value);
            }, _ => _canSet && IsNumeric);
        }

        public void Refresh(ST_MELSEC_CACHED_VALUE? sample, bool simulation)
        {
            if (_canSet != simulation)
            {
                _canSet = simulation;
                Changed(nameof(CanSet));
                SetOnCommand.NotifyCanExecuteChanged();
                SetOffCommand.NotifyCanExecuteChanged();
                SetValueCommand.NotifyCanExecuteChanged();
            }

            var raw = sample is { IsValid: true } ? Convert.ToString(sample.Value, System.Globalization.CultureInfo.InvariantCulture) : null;
            var text = raw switch
            {
                "1" => "ON (1)",
                "0" => "OFF (0)",
                null => "—",
                _ => raw
            };
            var brush = raw switch
            {
                "1" => Brushes.ForestGreen,
                "0" => Brushes.DarkGoldenrod,
                null => Brushes.Gray,
                _ => Brushes.Firebrick
            };
            if (IsMeasurement && raw is not null)
            {
                text = Convert.ToDouble(sample!.Value, CultureInfo.InvariantCulture).ToString("0.####", CultureInfo.InvariantCulture) + " mm";
                brush = Brushes.SteelBlue;
            }
            else if (IsNumeric && raw is not null)
            {
                var scaled = Convert.ToDouble(sample!.Value, CultureInfo.InvariantCulture);
                var rawValue = scaled / _readScale;
                text = IsCount ? scaled.ToString("0.########", CultureInfo.InvariantCulture) : $"{rawValue.ToString("0", CultureInfo.InvariantCulture)} → {scaled.ToString("0.########", CultureInfo.InvariantCulture)}";
                brush = IsCount ? Brushes.SteelBlue : scaled == 1 ? Brushes.ForestGreen : Brushes.Firebrick;
                if (IsAlignBusyWord)
                {
                    var active = ((int)rawValue & 2) != 0;
                    text = $"{rawValue:0} · ALIGN {(active ? "ON" : "OFF")}";
                    brush = active ? Brushes.ForestGreen : Brushes.DarkGoldenrod;
                }
            }

            if (_currentValue != text)
            {
                _currentValue = text;
                Changed(nameof(CurrentValue));
            }

            if (_stateBrush != brush)
            {
                _stateBrush = brush;
                Changed(nameof(StateBrush));
            }
        }

        private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }
}
