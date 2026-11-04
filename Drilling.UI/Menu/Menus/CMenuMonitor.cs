namespace Drilling.UI.Menu.Menus
{
    using Drilling.Common.Interface;
    using System.Globalization;
    using System.Collections;
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.IO;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Windows;
    using Drilling.Common.Managers;
    using Drilling.Common.Motion;
    using Drilling.Common.Alarm;
    using Drilling.Common.InterLock;
    using Drilling.Common.Product;
    using Drilling.Common.Recipe;
    using Drilling.Common.Review;
    using Drilling.Common.Station;
    using Drilling.UI.Menu;
    using Drilling.UI.Popup;
    using System.Windows.Media;

    public sealed partial class CMenuMonitor : CMenuBase
    {
#region 공유 상태·초기값
        private const double PowerMeterGraphWidth = 720.0;
        private const double PowerMeterGraphHeight = 300.0;
        private const double PowerMeterGraphMinimumHalfRange = 0.05;
        private readonly Dictionary<string, object?> _lastNotifiedLiveValues = new(StringComparer.Ordinal);
        private static readonly object LivePropertyCacheLock = new();
        private static readonly Dictionary<string, PropertyInfo?> LivePropertyCache = new(StringComparer.Ordinal);
        private readonly CInterfaceManager _interfaceManager;
        private readonly CMotionManager _motionManager;
        private readonly CInterLockManager _interLockManager;
        private readonly CProductManager _productManager;
        private readonly CRecipeManager _recipeManager;
        private readonly CSettingManager _settingManager;
        private readonly Func<string> _selectedRecipeIdProvider;
        private readonly Func<string> _selectedTabAccessor;
        private readonly Action<string> _selectedTabSetter;
        private readonly Action<string> _setStatusMessage;
        private readonly Action _refreshShellStatus;
        private readonly Func<Task> _refreshCurrentScreen;
        private readonly Func<bool> _canExecuteMonitorWriteCommand;
        private readonly CMonitorStatusPollingService _statusPollingService;
        private readonly Dictionary<string, string> _operationFieldValues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _melsecWriteValues = new(StringComparer.OrdinalIgnoreCase);
        private const int LaserHeadCount = 8;
        private IReadOnlyList<int> _enabledHeadDeviceNumbers = [0, 1, 2, 3, 4, 5, 6, 7];
        private string _selectedAxisId = "GX";
        private int _selectedLaserNumber;
        private int _selectedAttenuatorNumber;
        private int _selectedBetNumber;
        private int _selectedPowerMeterNumber;
        private int _selectedPicoMotorNo = 1;
        private bool _picoMotorIsConnected;
        private string _coordinateBasis = "DESIGN";
        private int _coordinateSelectedCellNo;
        private string _coordinateSelectedShotKey = "";
        private bool _coordinateIsCellDetailVisible;
        private readonly HashSet<int> _picoAllMoveMotorNos = [1];
        private readonly SemaphoreSlim _xpsJogCommandLock = new(1, 1);
        private readonly SemaphoreSlim _picoJogCommandLock = new(1, 1);
        private CancellationTokenSource? _xpsJogStartCancellationSource;
        private int _xpsJogRequestVersion;
        private bool _xpsJogRequested;
        private bool _xpsJogStopPending;
        private string _xpsJogAxisId = "";
        private CancellationTokenSource? _picoJogCancellationSource;
        private Task? _picoAllMoveTask;
        private Task? _picoMotionTask;
        private CancellationTokenSource? _powerMeterTuneCts;
        private int _powerMeterTuneAttenuatorNumber = -1;
        private int _powerMeterTunePowerMeterNumber = -1;
        private readonly List<ST_POWER_METER_TUNE_SAMPLE> _powerMeterTuneSamples = [];
        private string _powerMeterTuneState = "IDLE";
        private string _powerMeterTuneMessage = "Target power tune is ready.";
        private double _powerMeterTargetPower = 1.2;
        private string _selectedMelsecGroup = "ALL";
        private bool _isMelsecWriteLockVisible;
        private bool _isMelsecWriteUnlocked;
        private bool _liveRefreshRunning;
        private int? _lastLiveRefreshHash;
        private static readonly string[] MonitorTabs = ["PRODUCT", "LASER", "ATTENUATOR", "BET", "MELSEC", "XPS", "PICO MOTOR", "COORDINATE VIEWER", "LOG VIEWER"];
        private static readonly string[] HomeTypeOptions = ["1: Current Position as HOME", "4: Negative End Limit as HOME", "5: End Run Offset Init (Service)"];
        public IReadOnlyList<ST_SCREEN_SECTION> DeviceTabs { get; private set; } = [];
        public string SelectedTab { get; private set; } = "PRODUCT";
        public string Title { get; private set; } = "";
        public string Subtitle { get; private set; } = "";
        public string StatusPanelTitle { get; private set; } = "";
        public string OperationPanelTitle { get; private set; } = "";
        public string ParameterPanelTitle { get; private set; } = "";
        public string TrendPanelTitle { get; private set; } = "";
        public IReadOnlyList<ST_MONITOR_TAB> Tabs { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_AXIS_ROW> AxisRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_MULTI_MOVE_AXIS_ROW> MultiMoveAxisRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_STATUS_ROW> StatusRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> OperationButtons { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_PARAMETER_ROW> OperationFields { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_HEAD_SELECT_ROW> HeadSelectRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_LASER_CONTROL_ROW> LaserControlRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_PARAMETER_ROW> ParameterRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_BET_TABLE_ROW> BetTableRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_TREND_POINT> TrendPoints { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_SUMMARY_ITEM> SummaryItems { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_SUMMARY_ITEM> AttenuatorStatusItems { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_SUMMARY_ITEM> AttenuatorSettingItems { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_POSITION_ROW> PositionRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_PRODUCT_ITEM> ProductItems { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_PRODUCT_HISTORY_ROW> ProductHistoryRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_PRODUCT_ITEM> ProductGlassItems { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_PRODUCT_ITEM> ProductAlignItems { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_PRODUCT_ITEM> ProductDistortionItems { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_PRODUCT_ITEM> ProductProcessItems { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_PRODUCT_ITEM> ProductRecipeItems { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_PRODUCT_ITEM> ProductReviewItems { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_PRODUCT_REVIEW_ROW> ProductReviewRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_MELSEC_GROUP> MelsecGroups { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_MELSEC_ROW> MelsecRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_MELSEC_ROW> MelsecReadRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_MELSEC_ROW> MelsecWriteRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_COORDINATE_BASIS_OPTION> CoordinateBasisOptions { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_COORDINATE_VALUE_ROW> CoordinateValueRows { get; private set; } = [];
        public string CoordinateSelectedRecipeName { get; private set; } = "-";
        public string CoordinateSelectedBasisName { get; private set; } = "Design";
        public string CoordinateBasisDescription { get; private set; } = "Recipe design coordinate from Align Key / Glass";
        public IReadOnlyList<ST_CELL_PREVIEW_LABEL> CoordinateCellPreviewLabels { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_COORDINATE_SHOT_MATRIX_ROW> CoordinateShotMatrixRows { get; private set; } = [];
        public string CoordinateGlassPreviewSummary { get; private set; } = "0 Cells / 0 Shot Centers";
        public IReadOnlyList<ST_PWM_SETTING_ROW> PwmSettingRows { get; private set; } = [];
        public IReadOnlyList<ST_PWM_DEVICE_ROW> PwmDeviceRows { get; private set; } = [];
        public IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> PwmRunButtons { get; private set; } = [];
        public IReadOnlyList<ST_POWER_METER_TUNE_ROW> PowerMeterTuneRows { get; private set; } = [];
        public IReadOnlyList<ST_POWER_METER_TUNE_PROFILE_ROW> PowerMeterTuneProfileRows { get; } = CreatePowerMeterTuneProfileRows();
        public PointCollection PowerMeterGraphLine { get; private set; } = [];
        public double PowerMeterTargetLineY { get; private set; } = PowerMeterGraphHeight / 2.0;
        public string PowerMeterGraphMinText { get; private set; } = "0.0000 W";
        public string PowerMeterGraphMaxText { get; private set; } = "1.2000 W";
        public string PowerMeterGraphTargetText { get; private set; } = "TARGET POWER 1.2000(W)";

#endregion
#region 생성·초기화
        public CMenuMonitor(
            CInterfaceManager interfaceManager, 
            CMotionManager motionManager, 
            CInterLockManager interLockManager, 
            CProductManager productManager, 
            CRecipeManager recipeManager, 
            CSettingManager settingManager, 
            Func<string> selectedRecipeIdProvider, 
            Func<string> selectedTabAccessor, 
            Action<string> selectedTabSetter, 
            Action<string> setStatusMessage, 
            Action refreshShellStatus, 
            Func<Task> refreshCurrentScreen, 
            Func<bool> canExecuteMonitorWriteCommand, 
            string? logDirectory = null
        )
        {
            LogViewer = new CMonitorLogViewerPanel(logDirectory ?? Path.Combine(AppContext.BaseDirectory, "Log"));
            _interfaceManager = interfaceManager;
            _motionManager = motionManager;
            _interLockManager = interLockManager;
            _productManager = productManager;
            _recipeManager = recipeManager;
            _settingManager = settingManager;
            _selectedRecipeIdProvider = selectedRecipeIdProvider;
            _selectedTabAccessor = selectedTabAccessor;
            _selectedTabSetter = selectedTabSetter;
            _setStatusMessage = setStatusMessage;
            _refreshShellStatus = refreshShellStatus;
            _refreshCurrentScreen = refreshCurrentScreen;
            _canExecuteMonitorWriteCommand = canExecuteMonitorWriteCommand;
            _statusPollingService = new CMonitorStatusPollingService(_interfaceManager, _motionManager);
            async void SelectTabCallback(object? parameter)
            {
                await SelectTab(parameter);
            }

            SelectTabCommand = new CButtonCommand(SelectTabCallback);
            async void SelectHeadDeviceCallback(object? parameter)
            {
                await SelectHeadDevice(parameter);
            }

            bool CanSelectHeadDeviceCallback(object? parameter)
            {
                return CanSelectHeadDevice(parameter);
            }

            SelectHeadDeviceCommand = new CButtonCommand(SelectHeadDeviceCallback, CanSelectHeadDeviceCallback);
            async void ExecuteOperationCallback(object? parameter)
            {
                await ExecuteOperation(parameter);
            }

            ExecuteOperationCommand = new CButtonCommand(ExecuteOperationCallback);
            async void SelectMelsecGroupCallback(object? parameter)
            {
                await SelectMelsecGroup(parameter);
            }

            SelectMelsecGroupCommand = new CButtonCommand(SelectMelsecGroupCallback);
            async void WriteMelsecCallback(object? parameter)
            {
                await WriteMelsec(parameter);
            }

            WriteMelsecCommand = new CButtonCommand(WriteMelsecCallback);
            void ToggleMelsecWriteLockCallback(object? parameter)
            {
                ToggleMelsecWriteLock();
            }

            ToggleMelsecWriteLockCommand = new CButtonCommand(ToggleMelsecWriteLockCallback);
            async void PicoJogStartCallback(object? parameter)
            {
                await StartPicoJog(parameter);
            }

            PicoJogStartCommand = new CButtonCommand(PicoJogStartCallback);
            async void PicoJogStopCallback(object? arg0)
            {
                await StopPicoJog();
            }

            PicoJogStopCommand = new CButtonCommand(PicoJogStopCallback);
            async void SelectCoordinateBasisCallback(object? parameter)
            {
                await SelectCoordinateBasis(parameter);
            }

            SelectCoordinateBasisCommand = new CButtonCommand(SelectCoordinateBasisCallback);
            async void SelectCoordinateCellCallback(object? parameter)
            {
                await SelectCoordinateCell(parameter);
            }

            SelectCoordinateCellCommand = new CButtonCommand(SelectCoordinateCellCallback);
            async void SelectCoordinateShotCallback(object? parameter)
            {
                await SelectCoordinateShot(parameter);
            }

            SelectCoordinateShotCommand = new CButtonCommand(SelectCoordinateShotCallback);
            async void BackToCoordinateGlassPreviewCallback(object? arg0)
            {
                await BackToCoordinateGlassPreview();
            }

            BackToCoordinateGlassPreviewCommand = new CButtonCommand(BackToCoordinateGlassPreviewCallback);
            async void CreateGlassCallback(object? arg0)
            {
                await CreateGlass();
            }

            CreateGlassCommand = new CButtonCommand(CreateGlassCallback);
            async void DeleteGlassCallback(object? arg0)
            {
                await DeleteGlass();
            }

            DeleteGlassCommand = new CButtonCommand(DeleteGlassCallback);
        }

#endregion
#region 기본 처리
        public override EN_MENU Menu
        {
            get
            {
                return EN_MENU.Monitor;
            }
        }

        public ImageSource? CoordinateGlassPreviewImage { get; private set; }

        public bool CoordinateIsGlassPreviewVisible
        {
            get
            {
                return !_coordinateIsCellDetailVisible;
            }
        }

        public bool CoordinateIsCellDetailVisible
        {
            get
            {
                return _coordinateIsCellDetailVisible;
            }
        }

        public string CoordinateSelectedCellName
        {
            get
            {
                return _coordinateSelectedCellNo > 0 ? $"Cell{_coordinateSelectedCellNo:000}" : "-";
            }
        }

        public string CoordinateSelectedShotName
        {
            get
            {
                return string.IsNullOrWhiteSpace(_coordinateSelectedShotKey) ? "-" : _coordinateSelectedShotKey;
            }
        }

        public string PowerMeterTuneState
        {
            get
            {
                return _powerMeterTuneState;
            }
        }

        public string PowerMeterTuneMessage
        {
            get
            {
                return _powerMeterTuneMessage;
            }
        }

        public bool IsPowerMeterTuneRunning
        {
            get
            {
                return _powerMeterTuneCts is not null;
            }
        }

        public CButtonCommand SelectTabCommand { get; }
        public CButtonCommand SelectHeadDeviceCommand { get; }
        public CButtonCommand ExecuteOperationCommand { get; }
        public CButtonCommand SelectMelsecGroupCommand { get; }
        public CButtonCommand WriteMelsecCommand { get; }
        public CButtonCommand ToggleMelsecWriteLockCommand { get; }
        public CButtonCommand PicoJogStartCommand { get; }
        public CButtonCommand PicoJogStopCommand { get; }
        public CButtonCommand SelectCoordinateBasisCommand { get; }
        public CButtonCommand SelectCoordinateCellCommand { get; }
        public CButtonCommand SelectCoordinateShotCommand { get; }
        public CButtonCommand BackToCoordinateGlassPreviewCommand { get; }
        public CButtonCommand CreateGlassCommand { get; }
        public CButtonCommand DeleteGlassCommand { get; }

        public string SelectedAxisId
        {
            get
            {
                return _selectedAxisId;
            }
        }

        public int SelectedLaserNumber
        {
            get
            {
                return _selectedLaserNumber;
            }
        }

        public string SelectedLaserName
        {
            get
            {
                return FormatSelectedHeadDeviceName(_selectedLaserNumber);
            }
        }

        public string SelectedHeadDeviceName
        {
            get
            {
                return FormatSelectedHeadDeviceName(GetSelectedHeadNumber(SelectedTab));
            }
        }

        public string HeadDeviceSelectorTitle
        {
            get
            {
                switch (SelectedTab)
                {
                    case "ATTENUATOR":
                    case "POWER METER":
                        return "Attenuator Head";
                    case "BET":
                        return "BET Head";
                    default:
                        return "Laser Head";
                }
            }
        }

        public ST_MONITOR_AXIS_ROW? SelectedAxisRow
        {
            get
            {
                bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_MONITOR_AXIS_ROW row)
                {
                    return row.AxisId.Equals(_selectedAxisId, StringComparison.OrdinalIgnoreCase);
                }

                return AxisRows.FirstOrDefault(MatchesItem);
            }

            set
            {
                if (value is null || string.IsNullOrWhiteSpace(value.AxisId) || value.AxisId.Equals(_selectedAxisId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _selectedAxisId = value.AxisId;
                OnPropertyChanged(nameof(SelectedAxisId));
                OnPropertyChanged(nameof(SelectedAxisRow));
            }
        }

        public bool IsMotor
        {
            get
            {
                return SelectedTab == "XPS";
            }
        }

        public bool IsLaser
        {
            get
            {
                return SelectedTab == "LASER";
            }
        }

        public bool IsAttenuator
        {
            get
            {
                return SelectedTab == "ATTENUATOR";
            }
        }

        public bool IsBet
        {
            get
            {
                return SelectedTab == "BET";
            }
        }

        public bool IsPowerMeter
        {
            get
            {
                return SelectedTab == "POWER METER";
            }
        }

        public bool IsPicoMotor
        {
            get
            {
                return SelectedTab == "PICO MOTOR";
            }
        }

        public IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> PicoConnectionButtons
        {
            get
            {
                return[new("CONNECT", "Servo", _picoMotorIsConnected ? "Dark" : "Green"), new("DISCONNECT", "Stop", _picoMotorIsConnected ? "Red" : "Dark")];
            }
        }

        public IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> PicoMotorSelectButtons
        {
            get
            {
                global::Drilling.UI.Menu.Menus.ST_MONITOR_OPERATION_BUTTON BuildMonitor(int number)
                {
                    return new ST_MONITOR_OPERATION_BUTTON($"MOTOR {number}", "Move", _selectedPicoMotorNo == number ? "Blue" : "Dark");
                }

                return Enumerable.Range(1, 4).Select(BuildMonitor).ToArray();
            }
        }

        public IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> PicoMotionStopButtons
        {
            get
            {
                return[new("STOP MOTION", "Stop", "Red")];
            }
        }

        public IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> PicoMotionMoveButtons
        {
            get
            {
                return[new("HOME", "Home", "Blue"), new("ABS MOVE", "Abs", "Blue")];
            }
        }

        public IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> PicoMotionJogButtons
        {
            get
            {
                return[new("JOG -", "Rel", "Dark"), new("JOG +", "Rel", "Blue")];
            }
        }

        public IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> PicoMotionRelButtons
        {
            get
            {
                return[new("REL -", "Rel", "Dark"), new("REL +", "Rel", "Blue")];
            }
        }

        public IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> PicoAllMotorSelectButtons
        {
            get
            {
                global::Drilling.UI.Menu.Menus.ST_MONITOR_OPERATION_BUTTON BuildMonitor(int number)
                {
                    return new ST_MONITOR_OPERATION_BUTTON($"MOTOR {number}", "Move", _picoAllMoveMotorNos.Contains(number) ? "Blue" : "Dark", $"ALL MOTOR {number}");
                }

                return Enumerable.Range(1, 4).Select(BuildMonitor).ToArray();
            }
        }

        public IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> PicoAllMotorCommandButtons
        {
            get
            {
                return[new("START", "Run", "Green"), new("STOP", "Stop", "Red")];
            }
        }

        public IReadOnlyList<ST_MONITOR_PARAMETER_ROW> PicoErrorRows
        {
            get
            {
                return CreatePicoErrorRows();
            }
        }

        public bool IsProduct
        {
            get
            {
                return SelectedTab == "PRODUCT";
            }
        }

        public bool IsMelsec
        {
            get
            {
                return SelectedTab == "MELSEC";
            }
        }

        public bool IsMelsecWriteLockVisible
        {
            get
            {
                return _isMelsecWriteLockVisible;
            }
        }

        public bool IsMelsecWriteUnlocked
        {
            get
            {
                return _isMelsecWriteUnlocked;
            }
        }

        public string MelsecWriteLockText
        {
            get
            {
                return _isMelsecWriteUnlocked ? "WRITE UNLOCK" : "WRITE LOCK";
            }
        }

        public void ToggleMelsecWriteLockVisibility()
        {
            if (!IsMelsec)
            {
                return;
            }

            if (_isMelsecWriteLockVisible)
            {
                ResetMelsecWriteLock();
                _setStatusMessage("MELSEC WRITE locked and lock control hidden.");
                return;
            }

            _isMelsecWriteLockVisible = true;
            NotifyMonitorLiveProperties(nameof(IsMelsecWriteLockVisible));
            _setStatusMessage("MELSEC WRITE lock control displayed.");
        }

        public void HideAndLockMelsecWrite()
        {
            ResetMelsecWriteLock();
        }

        public CMonitorLogViewerPanel LogViewer { get; }
        public bool IsLogViewer => SelectedTab == "LOG VIEWER";

        public bool IsCoordinateViewer
        {
            get
            {
                return SelectedTab == "COORDINATE VIEWER";
            }
        }

        public bool IsControlDevice
        {
            get
            {
                return false;
            }
        }

        public bool IsGenericDevice
        {
            get
            {
                return !IsMotor && !IsLaser && !IsAttenuator && !IsBet && !IsPowerMeter && !IsPicoMotor && !IsProduct && !IsMelsec && !IsCoordinateViewer && !IsLogViewer;
            }
        }

        public void SetPollingActive(bool active)
        {
            _statusPollingService.SetActive(active && !IsLogViewer);
        }

        public override async Task<CScreenViewModel> Build(CancellationToken cancellationToken = default)
        {
            var selectedTab = NormalizeMonitorScreenTab(_selectedTabAccessor());
            if (selectedTab == "LOG VIEWER")
                return BuildLogViewerScreen();
            _selectedLaserNumber = Math.Clamp(_selectedLaserNumber, 0, LaserHeadCount - 1);
            _selectedAttenuatorNumber = Math.Clamp(_selectedAttenuatorNumber, 0, LaserHeadCount - 1);
            _selectedBetNumber = Math.Clamp(_selectedBetNumber, 0, LaserHeadCount - 1);
            _enabledHeadDeviceNumbers = LoadEnabledHeadDeviceNumbers(cancellationToken);
            NormalizeHeadDeviceSelections();
            if (selectedTab is "LASER" or "POWER METER")
            {
                if (PwmSettingRows.Count > 0)
                    SavePowerMeterSettingValues();
                NormalizePowerMeterSelection();
            }

            if (selectedTab == "COORDINATE VIEWER")
            {
                return await BuildCoordinateViewerScreen(cancellationToken);
            }

            UpdatePollingContext(selectedTab);
            _statusPollingService.Start();
            var monitorSnapshot = _statusPollingService.GetSnapshot();
            if (selectedTab is "LASER" or "POWER METER")
            {
                monitorSnapshot = RefreshPowerMeterSnapshotFromCache(monitorSnapshot, cancellationToken);
            }

            var snapshot = monitorSnapshot.DeviceStatus;
            var communication = monitorSnapshot.Communication;
            var betTable = selectedTab == "BET" ? await _interfaceManager.LoadBETData(cancellationToken) : [];
            var picoMotorStatus = selectedTab == "PICO MOTOR" ? monitorSnapshot.PicoMotorStatus : ST_PICO_MOTOR_STATUS.Empty;
            if (selectedTab == "PICO MOTOR")
            {
                _selectedPicoMotorNo = Math.Clamp(picoMotorStatus.SelectedMotorNo, 1, 4);
                _picoMotorIsConnected = picoMotorStatus.IsConnected;
            }

            if (selectedTab == "POWER METER")
            {
                UpdatePowerMeterGraph();
            }

            var(product, productHistory, productError) = selectedTab == "PRODUCT" ? await LoadProductDisplay(cancellationToken) : (null, [], "");
            var productRecipeDefinition = product is null ? null : await LoadProductRecipeDefinition(product.RecipeId, cancellationToken);
            global::Drilling.UI.Menu.Menus.ST_MONITOR_TAB BuildMonitor(string tab)
            {
                return new ST_MONITOR_TAB(GetMonitorTabDisplayName(tab), tab == selectedTab);
            }

            var tabs = MonitorTabs.Select(BuildMonitor).ToArray();
            var melsecGroups = CreateMelsecGroups(selectedTab);
            SaveMelsecWriteValues();
            var melsecRows = CreateMelsecRows(selectedTab, monitorSnapshot.MelsecValues);
            var coordinateViewerData = ST_MONITOR_COORDINATE_VIEWER_DATA.Empty;
            if (OperationFields.Count > 0 && SelectedTab == selectedTab)
            {
                SaveOperationFieldValues(selectedTab);
            }

            var operationFields = CreateOperationFields(selectedTab, picoMotorStatus);
            var talonStatus = selectedTab == "LASER" ? monitorSnapshot.TalonStatus : ST_TALON_STATUS.Empty;
            var selectedLaserStatus = snapshot.Laser;
            var axisRows = CreateAxisRows(snapshot, _selectedAxisId);
            bool IsIsSelected(global::Drilling.UI.Menu.Menus.ST_MONITOR_AXIS_ROW row)
            {
                return row.IsSelected;
            }

            if (axisRows.Count > 0 && !axisRows.Any(IsIsSelected))
            {
                _selectedAxisId = axisRows[0].AxisId;
                axisRows = CreateAxisRows(snapshot, _selectedAxisId);
            }

            if (selectedTab == "XPS")
            {
                UpdateMultiMoveAxisRows(snapshot);
            }
            else
            {
                MultiMoveAxisRows = [];
            }

            HeadSelectRows = CreateHeadSelectRows(selectedTab, GetSelectedHeadNumber(selectedTab), GetHeadDeviceModule(selectedTab), GetHeadDeviceNickPrefix(selectedTab));
            LaserControlRows = CreateLaserControlRows(selectedTab, selectedLaserStatus, talonStatus, operationFields);
            AttenuatorStatusItems = CreateAttenuatorStatusItems(selectedTab, snapshot);
            AttenuatorSettingItems = CreateAttenuatorSettingItems(selectedTab, snapshot);
            ApplyProductDetails(product, productError, productRecipeDefinition);
            Apply(
                CreateTabSections(tabs), 
                selectedTab, 
                $"MONITOR / {GetMonitorTabDisplayName(selectedTab)}", 
                GetSubtitle(selectedTab), 
                GetStatusPanelTitle(selectedTab), 
                GetOperationPanelTitle(selectedTab), 
                GetParameterPanelTitle(selectedTab), 
                GetTrendPanelTitle(selectedTab), 
                tabs, 
                axisRows, 
                CreateStatusRows(selectedTab, snapshot, communication, GetSelectedHeadConnectionText(selectedTab), talonStatus), 
                CreateOperationButtons(selectedTab, selectedLaserStatus, talonStatus), 
                operationFields, 
                CreateParameterRows(selectedTab), 
                CreateBetTableRows(selectedTab, betTable, snapshot), 
                CreateTrendPoints(selectedTab), 
                CreateSummaryItems(selectedTab, snapshot), 
                CreatePositionRows(selectedTab, snapshot), 
                CreateProductItems(product, productError), 
                CreateProductHistoryRows(productHistory), 
                CreatePwmSettingRows(selectedTab), 
                CreatePwmDeviceRows(selectedTab, snapshot), 
                CreatePwmRunButtons(selectedTab), 
                PowerMeterTuneRows, 
                PowerMeterGraphLine, 
                PowerMeterTargetLineY, 
                PowerMeterGraphMinText, 
                PowerMeterGraphMaxText, 
                PowerMeterGraphTargetText
            );
            MelsecGroups = melsecGroups;
            SetMelsecRows(melsecRows);
            ApplyCoordinateViewerData(coordinateViewerData);
            if (selectedTab == "PICO MOTOR")
            {
                StatusRows = CreatePicoMotorStatusRows(picoMotorStatus);
                OperationButtons = CreatePicoMotorOperationButtons();
                ParameterRows = CreatePicoErrorRows();
                PositionRows = CreatePicoMotorPositionRows(picoMotorStatus);
            }

            _lastLiveRefreshHash = CreateLiveRefreshHash(selectedTab, monitorSnapshot);
            global::Drilling.UI.Menu.Menus.ST_DISPLAY_ITEM BuildDisplay(global::Drilling.UI.Menu.Menus.ST_MONITOR_AXIS_ROW row)
            {
                return new ST_DISPLAY_ITEM(row.Axis, row.CurrentPosition, row.Alarm);
            }

            global::Drilling.UI.Menu.Menus.ST_DISPLAY_ITEM BuildDisplay2(global::Drilling.UI.Menu.Menus.ST_MONITOR_STATUS_ROW row)
            {
                return new ST_DISPLAY_ITEM(row.Item, row.Value, row.State);
            }

            return new CScreenViewModel(EN_MENU.Monitor, Title, Subtitle, [new("Tab", selectedTab)], [new("XPS", AxisRows.Select(BuildDisplay).ToArray()), new("LASER", StatusRows.Select(BuildDisplay2).ToArray())], monitor: this);
        }

        public Task RefreshLiveData(CancellationToken cancellationToken = default)
        {
            if (_liveRefreshRunning)
            {
                return Task.CompletedTask;
            }

            _liveRefreshRunning = true;
            bool IsIsSelected(global::Drilling.UI.Menu.Menus.ST_MONITOR_AXIS_ROW row)
            {
                return row.IsSelected;
            }

            try
            {
                var selectedTab = NormalizeMonitorScreenTab(_selectedTabAccessor());
                if (!selectedTab.Equals(SelectedTab, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.CompletedTask;
                }

                if (selectedTab is "COORDINATE VIEWER" or "LOG VIEWER")
                {
                    return Task.CompletedTask;
                }

                if (OperationFields.Count > 0)
                {
                    SaveOperationFieldValues(selectedTab);
                }

                if ((selectedTab is "LASER" or "POWER METER") && PwmSettingRows.Count > 0)
                {
                    SavePowerMeterSettingValues();
                }

                _enabledHeadDeviceNumbers = LoadEnabledHeadDeviceNumbers(cancellationToken);
                NormalizeHeadDeviceSelections();
                if (selectedTab is "LASER" or "POWER METER")
                {
                    NormalizePowerMeterSelection();
                }

                UpdatePollingContext(selectedTab);
                _statusPollingService.Start();
                var monitorSnapshot = _statusPollingService.GetSnapshot();
                if (selectedTab is "LASER" or "POWER METER")
                {
                    monitorSnapshot = RefreshPowerMeterSnapshotFromCache(monitorSnapshot, cancellationToken);
                }

                var liveRefreshHash = CreateLiveRefreshHash(selectedTab, monitorSnapshot);
                if (_lastLiveRefreshHash == liveRefreshHash)
                {
                    return Task.CompletedTask;
                }

                _lastLiveRefreshHash = liveRefreshHash;
                var snapshot = monitorSnapshot.DeviceStatus;
                var communication = monitorSnapshot.Communication;
                if (selectedTab == "PICO MOTOR")
                {
                    var picoStatus = monitorSnapshot.PicoMotorStatus;
                    _selectedPicoMotorNo = Math.Clamp(picoStatus.SelectedMotorNo, 1, 4);
                    _picoMotorIsConnected = picoStatus.IsConnected;
                    StatusRows = CreatePicoMotorStatusRows(picoStatus);
                    ParameterRows = CreatePicoErrorRows();
                    PositionRows = CreatePicoMotorPositionRows(picoStatus);
                    UpdatePicoMotorOperationFields(picoStatus);
                    NotifyMonitorLiveProperties(nameof(StatusRows), nameof(ParameterRows), nameof(PositionRows), nameof(OperationFields), nameof(PicoConnectionButtons), nameof(PicoMotorSelectButtons));
                    return Task.CompletedTask;
                }

                if (selectedTab == "LASER")
                {
                    var talonStatus = monitorSnapshot.TalonStatus;
                    var selectedLaserStatus = snapshot.Laser;
                    StatusRows = CreateStatusRows(selectedTab, snapshot, communication, GetSelectedHeadConnectionText(selectedTab), talonStatus);
                    OperationButtons = CreateOperationButtons(selectedTab, selectedLaserStatus, talonStatus);
                    HeadSelectRows = CreateHeadSelectRows(selectedTab, _selectedLaserNumber, EN_EQP_MODULE.TalonLaser, "TALON");
                    LaserControlRows = CreateLaserControlRows(selectedTab, selectedLaserStatus, talonStatus, OperationFields);
                    SummaryItems = CreateSummaryItems(selectedTab, snapshot);
                    PwmSettingRows = CreatePwmSettingRows(selectedTab);
                    PwmDeviceRows = CreatePwmDeviceRows(selectedTab, snapshot);
                    NotifyMonitorLiveProperties(nameof(StatusRows), nameof(OperationButtons), nameof(HeadSelectRows), nameof(LaserControlRows), nameof(SelectedHeadDeviceName), nameof(SummaryItems), nameof(PwmSettingRows), nameof(PwmDeviceRows));
                    return Task.CompletedTask;
                }

                StatusRows = CreateStatusRows(selectedTab, snapshot, communication, GetSelectedHeadConnectionText(selectedTab), ST_TALON_STATUS.Empty);
                switch (selectedTab)
                {
                    case "XPS":
                        var axisRows = CreateAxisRows(snapshot, _selectedAxisId);
                        if (axisRows.Count > 0 && !axisRows.Any(IsIsSelected))
                        {
                            _selectedAxisId = axisRows[0].AxisId;
                            axisRows = CreateAxisRows(snapshot, _selectedAxisId);
                            OnPropertyChanged(nameof(SelectedAxisId));
                            OnPropertyChanged(nameof(SelectedAxisRow));
                        }

                        AxisRows = axisRows;
                        UpdateMultiMoveAxisRows(snapshot);
                        NotifyMonitorLiveProperties(nameof(AxisRows), nameof(MultiMoveAxisRows), nameof(StatusRows), nameof(SelectedAxisRow));
                        break;
                    case "ATTENUATOR":
                        HeadSelectRows = CreateHeadSelectRows(selectedTab, _selectedAttenuatorNumber, EN_EQP_MODULE.Attenuator, "ATT");
                        SummaryItems = CreateSummaryItems(selectedTab, snapshot);
                        AttenuatorStatusItems = CreateAttenuatorStatusItems(selectedTab, snapshot);
                        AttenuatorSettingItems = CreateAttenuatorSettingItems(selectedTab, snapshot);
                        PositionRows = CreatePositionRows(selectedTab, snapshot);
                        NotifyMonitorLiveProperties(nameof(StatusRows), nameof(HeadSelectRows), nameof(SelectedHeadDeviceName), nameof(SummaryItems), nameof(AttenuatorStatusItems), nameof(AttenuatorSettingItems), nameof(PositionRows));
                        break;
                    case "BET":
                        HeadSelectRows = CreateHeadSelectRows(selectedTab, _selectedBetNumber, EN_EQP_MODULE.Bet, "BET");
                        SummaryItems = CreateSummaryItems(selectedTab, snapshot);
                        PositionRows = CreatePositionRows(selectedTab, snapshot);
                        NotifyMonitorLiveProperties(nameof(StatusRows), nameof(HeadSelectRows), nameof(SelectedHeadDeviceName), nameof(SummaryItems), nameof(PositionRows));
                        break;
                    case "POWER METER":
                        UpdatePowerMeterGraph();
                        HeadSelectRows = CreateHeadSelectRows(selectedTab, _selectedAttenuatorNumber, EN_EQP_MODULE.Attenuator, "ATT");
                        SummaryItems = CreateSummaryItems(selectedTab, snapshot);
                        PwmSettingRows = CreatePwmSettingRows(selectedTab);
                        PwmDeviceRows = CreatePwmDeviceRows(selectedTab, snapshot);
                        PwmRunButtons = CreatePwmRunButtons(selectedTab);
                        NotifyMonitorLiveProperties(
                            nameof(StatusRows), 
                            nameof(HeadSelectRows), 
                            nameof(SelectedHeadDeviceName), 
                            nameof(SummaryItems), 
                            nameof(PwmSettingRows), 
                            nameof(PwmDeviceRows), 
                            nameof(PwmRunButtons), 
                            nameof(PowerMeterTuneRows), 
                            nameof(PowerMeterGraphLine), 
                            nameof(PowerMeterTargetLineY), 
                            nameof(PowerMeterGraphMinText), 
                            nameof(PowerMeterGraphMaxText), 
                            nameof(PowerMeterGraphTargetText), 
                            nameof(PowerMeterTuneState), 
                            nameof(PowerMeterTuneMessage)
                        );
                        break;
                    case "MELSEC":
                        UpdateMelsecReadValues(monitorSnapshot.MelsecValues);
                        NotifyMonitorLiveProperties(nameof(StatusRows));
                        break;
                    default:
                        NotifyMonitorLiveProperties(nameof(StatusRows));
                        break;
                }
            }
            finally
            {
                _liveRefreshRunning = false;
            }

            return Task.CompletedTask;
        }

        private int CreateLiveRefreshHash(string selectedTab, ST_MONITOR_STATUS_SNAPSHOT monitorSnapshot)
        {
            var hash = new HashCode();
            hash.Add(selectedTab, StringComparer.OrdinalIgnoreCase);
            AddSequenceHash(ref hash, monitorSnapshot.Communication);
            var snapshot = monitorSnapshot.DeviceStatus;
            switch (selectedTab)
            {
                case "XPS":
                    hash.Add(_selectedAxisId, StringComparer.OrdinalIgnoreCase);
                    AddSequenceHash(ref hash, snapshot.Motors);
                    break;
                case "LASER":
                    hash.Add(_selectedLaserNumber);
                    hash.Add(snapshot.Laser);
                    hash.Add(monitorSnapshot.TalonStatus);
                    hash.Add(_selectedPowerMeterNumber);
                    hash.Add(snapshot.PowerMeter);
                    break;
                case "ATTENUATOR":
                    hash.Add(_selectedAttenuatorNumber);
                    hash.Add(snapshot.Attenuator);
                    break;
                case "BET":
                    hash.Add(_selectedBetNumber);
                    hash.Add(snapshot.Bet);
                    break;
                case "POWER METER":
                    hash.Add(_selectedAttenuatorNumber);
                    hash.Add(_selectedPowerMeterNumber);
                    hash.Add(_powerMeterTuneState, StringComparer.OrdinalIgnoreCase);
                    hash.Add(_powerMeterTuneMessage, StringComparer.OrdinalIgnoreCase);
                    hash.Add(_powerMeterTuneSamples.Count);
                    hash.Add(snapshot.PowerMeter);
                    break;
                case "PICO MOTOR":
                    hash.Add(_selectedPicoMotorNo);
                    hash.Add(monitorSnapshot.PicoMotorStatus);
                    break;
                case "MELSEC":
                    hash.Add(_selectedMelsecGroup, StringComparer.OrdinalIgnoreCase);
                    AddMelsecValuesHash(ref hash, monitorSnapshot.MelsecValues);
                    break;
                default:
                    hash.Add(snapshot);
                    break;
            }

            return hash.ToHashCode();
        }

        private static void AddSequenceHash<T>(ref HashCode hash, IEnumerable<T> values)
        {
            foreach (var value in values)
            {
                hash.Add(value);
            }
        }

        private static void AddMelsecValuesHash(ref HashCode hash, IReadOnlyDictionary<string, ST_MONITOR_MELSEC_VALUE> values)
        {
            foreach (var item in values.OrderBy(GetMelsecValueKey, StringComparer.OrdinalIgnoreCase))
            {
                hash.Add(item.Key, StringComparer.OrdinalIgnoreCase);
                hash.Add(item.Value);
            }
        }

        private static string GetMelsecValueKey(KeyValuePair<string, ST_MONITOR_MELSEC_VALUE> item)
        {
            return item.Key;
        }

        private ST_MONITOR_STATUS_SNAPSHOT RefreshPowerMeterSnapshotFromCache(ST_MONITOR_STATUS_SNAPSHOT monitorSnapshot, CancellationToken cancellationToken)
        {
            return monitorSnapshot with
            {
                DeviceStatus = monitorSnapshot.DeviceStatus with
                {
                    PowerMeter = _interfaceManager.GetPowerMeterStatus(_selectedPowerMeterNumber, cancellationToken)
                }
            };
        }

        private void UpdatePollingContext(string selectedTab)
        {
            var pollingTab = IsHeadDeviceTab(selectedTab) && _enabledHeadDeviceNumbers.Count == 0 ? "PRODUCT" : selectedTab;
            _statusPollingService.UpdateContext(pollingTab, _selectedLaserNumber, _selectedAttenuatorNumber, _selectedBetNumber, selectedTab == "MELSEC" ? _selectedMelsecGroup : "ALL");
        }

        private Task<ST_DEVICE_STATUS> GetDeviceStatus(CancellationToken cancellationToken)
        {
            return GetDeviceStatus("ALL", cancellationToken);
        }

        private Task<ST_DEVICE_STATUS> GetDeviceStatus(string selectedTab, CancellationToken cancellationToken)
        {
            var tab = NormalizeMonitorTab(selectedTab);
            IReadOnlyList<ST_IO_STATUS> io = [];
            var motors = tab is "ALL" or "XPS" ? _motionManager.GetAxisStatus(cancellationToken) : [];
            var laserStatus = tab is "ALL" ? _interfaceManager.GetLaserStatus(_selectedLaserNumber, cancellationToken) : CreateEmptyLaserStatus();
            var attenuatorStatus = tab is "ALL" or "ATTENUATOR" ? _interfaceManager.GetAttenuatorStatus(_selectedAttenuatorNumber, cancellationToken) : CreateEmptyAttenuatorStatus();
            var betStatus = tab is "ALL" or "BET" ? _interfaceManager.GetBETStatus(_selectedBetNumber, cancellationToken) : CreateEmptyBETStatus();
            var powerMeterStatus = tab is "ALL" or "POWER METER" ? _interfaceManager.GetPowerMeterStatus(_selectedPowerMeterNumber, cancellationToken) : ST_POWER_METER_STATUS.Empty;
            return Task.FromResult(new ST_DEVICE_STATUS(io, motors, laserStatus, attenuatorStatus, betStatus, powerMeterStatus));
        }

        private static ST_LASER_STATUS CreateEmptyLaserStatus()
        {
            return new ST_LASER_STATUS(false, false, false, 0.0);
        }

        private static ST_ATTENUATOR_STATUS CreateEmptyAttenuatorStatus()
        {
            return new ST_ATTENUATOR_STATUS(0.0, 0.0, "IDLE", false);
        }

        private static ST_BET_STATUS CreateEmptyBETStatus()
        {
            return new ST_BET_STATUS(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, false, false, false, false, false);
        }

        private async Task SelectTab(object? parameter)
        {
            if (parameter is not string tab || string.IsNullOrWhiteSpace(tab))
            {
                return;
            }

            var selectedTab = NormalizeMonitorScreenTab(tab);
            if (!selectedTab.Equals("MELSEC", StringComparison.OrdinalIgnoreCase))
            {
                ResetMelsecWriteLock();
            }

            _selectedTabSetter(selectedTab);
            _setStatusMessage($"Monitor tab {selectedTab} selected.");
            _refreshShellStatus();
            await _refreshCurrentScreen();
        }

        private async Task SelectHeadDevice(object? parameter)
        {
            if (SelectedTab == "POWER METER" && TryNotifyPowerMeterTuneLocked("ATT HEAD 선택"))
            {
                return;
            }

            if (!CanSelectHeadDevice(parameter))
            {
                return;
            }

            var number = parameter switch
            {
                ST_MONITOR_HEAD_SELECT_ROW row => row.Number,
                int value => value,
                string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
                _ => GetSelectedHeadNumber(SelectedTab)};
            number = Math.Clamp(number, 0, LaserHeadCount - 1);
            _enabledHeadDeviceNumbers = LoadEnabledHeadDeviceNumbers(CancellationToken.None);
            if (_enabledHeadDeviceNumbers.Count == 0)
            {
                _setStatusMessage("No monitor head is enabled. Check Hxx_USE option settings.");
                _refreshShellStatus();
                await _refreshCurrentScreen();
                return;
            }

            if (!_enabledHeadDeviceNumbers.Contains(number))
            {
                _setStatusMessage($"Monitor {SelectedTab} H{number + 1:00} is disabled by H{number + 1:00}_USE.");
                _refreshShellStatus();
                await _refreshCurrentScreen();
                return;
            }

            switch (SelectedTab)
            {
                case "ATTENUATOR":
                case "POWER METER":
                    _selectedAttenuatorNumber = number;
                    break;
                case "BET":
                    _selectedBetNumber = number;
                    break;
                default:
                    _selectedLaserNumber = number;
                    break;
            }

            _setStatusMessage($"Monitor {SelectedTab} H{number + 1:00} selected.");
            _refreshShellStatus();
            await _refreshCurrentScreen();
        }

        private bool EnsureMonitorWriteCommandAllowed(string actionName)
        {
            if (_canExecuteMonitorWriteCommand())
            {
                return true;
            }

            var message = $"{actionName}은 Manual Mode에서만 사용할 수 있습니다.\nAuto Mode에서는 Monitor 화면의 조회와 Polling만 가능합니다.";
            _setStatusMessage(message.Replace(Environment.NewLine, " "));
            CRecipeConfirmDialog.ShowWarning("Monitor 제어 불가", message);
            return false;
        }

        private bool CanSelectHeadDevice(object? parameter)
        {
            return parameter switch
            {
                ST_MONITOR_HEAD_SELECT_ROW row => row.IsEnabled,
                int number => _enabledHeadDeviceNumbers.Contains(Math.Clamp(number, 0, LaserHeadCount - 1)),
                string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => _enabledHeadDeviceNumbers.Contains(Math.Clamp(number, 0, LaserHeadCount - 1)),
                _ => true
            };
        }

        private async Task ExecuteOperation(object? parameter)
        {
            var label = GetMonitorOperationLabel(parameter);
            var isPowerMeterCommand = parameter is ST_PWM_DEVICE_ROW;
            if (string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            if (!EnsureMonitorWriteCommandAllowed($"{SelectedTab} {label}"))
            {
                return;
            }

            var confirmationKey = NormalizeMonitorOperation(label);
            if ((isPowerMeterCommand || SelectedTab == "POWER METER") && confirmationKey != "STOP" && TryNotifyPowerMeterTuneLocked("DEVICE COMMAND"))
            {
                return;
            }

            if (SelectedTab == "XPS" && confirmationKey == "MULTIABSMOVE" && !MultiMoveAxisRows.Any(row => row.IsSelected))
            {
                _setStatusMessage("Scanner tilt move skipped. Select at least one axis.");
                return;
            }

            if (SelectedTab == "PICO MOTOR" && confirmationKey == "START")
            {
                if (_picoAllMoveMotorNos.Count == 0)
                {
                    _setStatusMessage("DOE tilt start skipped. Select at least one motor.");
                    return;
                }

                if (Math.Max(0, (int)Math.Round(ReadOperationField("Set Count", 0))) <= 0)
                {
                    _setStatusMessage("DOE tilt start skipped. Set Count must be greater than 0.");
                    return;
                }
            }

            var isAttenuatorReset = SelectedTab == "ATTENUATOR" && confirmationKey == "RESET";
            if (RequiresOperationConfirmation(SelectedTab, label) && !CRecipeConfirmDialog.ShowConfirmation(isAttenuatorReset ? "감쇠기 초기화" : GetOperationConfirmationTitle(SelectedTab), GetOperationConfirmationText(label), isAttenuatorReset ? "초기화" : "확인"))
            {
                return;
            }

            SaveOperationFieldValues(SelectedTab);
            if (isPowerMeterCommand || SelectedTab == "POWER METER")
            {
                SavePowerMeterSettingValues();
            }

            var result = SelectedTab switch
            {
                "XPS" => await ExecuteMotorOperation(label),
                "LASER" when isPowerMeterCommand => await ExecutePowerMeterOperation(label),
                "LASER" => await ExecuteLaserOperation(label),
                "ATTENUATOR" => await ExecuteAttenuatorOperation(label),
                "BET" => await ExecuteBETOperation(label),
                "POWER METER" => await ExecutePowerMeterOperation(label),
                "PICO MOTOR" => await ExecutePicoMotorOperation(label),
                _ => new ST_DEVICE_COMMAND_RESULT(false, $"Monitor {SelectedTab} command is not connected yet: {label}")};
            _setStatusMessage(result.Message);
            if (SelectedTab == "XPS")
            {
                _statusPollingService.RefreshMotorNow();
            }

            if (SelectedTab is "XPS" or "LASER" or "ATTENUATOR" or "BET" or "POWER METER" or "PICO MOTOR")
            {
                _refreshShellStatus();
                await _refreshCurrentScreen();
            }
        }

        private static bool RequiresOperationConfirmation(string tab, string label)
        {
            var key = NormalizeMonitorOperation(label);
            switch (tab)
            {
                case "XPS":
                    return key is "SERVOON" or "SERVOOFF" or "HOME" or "ABSMOVE" or "RELMOVE" or "MULTIABSMOVE";
                case "LASER":
                    return key is "LASERON" or "LASEROFF" or "SHUTTEROPEN" or "SHUTTERCLOSE" or "GATEON" or "GATEOFF" or "EXTGATEENABLE" or "EXTGATEDISABLE" or "SETQSW" or "SETEPRF" or "SETSHG" or "SETQMODE";
                case "ATTENUATOR":
                    return key is "HOME" or "RESET" or "MOVEABS" or "MOVEREL" or "SETHOMETYPE" or "SETNEGLIMIT" or "SETPOSLIMIT" or "SETIGAIN" or "SETPGAIN" or "SETIF";
                case "BET":
                    return key is "HOME" or "MOVEMAG" or "MOVEDIV" or "SAVETABLE" or "MOVE" || key.StartsWith("MOVETABLE", StringComparison.OrdinalIgnoreCase);
                case "POWER METER":
                    return key == "START";
                case "PICO MOTOR":
                    return key is "CONNECT" or "DISCONNECT" or "SETVEL" or "SETVELOCITY" or "PICOSETVEL" or "SETACC" or "SETACCELERATION" or "PICOSETACC" or "HOME" or "ABSMOVE" or "START";
                default:
                    return false;
            }
        }

        private static string GetOperationConfirmationTitle(string tab)
        {
            return tab switch
            {
                "XPS" => "스캐너 틸트 제어",
                "LASER" => "레이저 제어",
                "ATTENUATOR" => "감쇠기 제어",
                "BET" => "BET 제어",
                "POWER METER" => "PowerMeter 제어",
                "PICO MOTOR" => "DOE 틸트 제어",
                _ => "장비 제어"
            };
        }

        private string GetOperationConfirmationText(string label)
        {
            var key = NormalizeMonitorOperation(label);
            if (SelectedTab == "XPS")
            {
                if (key == "HOME")
                {
                    return $"{_selectedAxisId}축을 원점으로 복귀하시겠습니까?";
                }

                if (key == "ABSMOVE")
                {
                    return $"{_selectedAxisId}축을 {ReadOperationField("ABS Target Position", 0.0).ToString("0.###", CultureInfo.InvariantCulture)} mm 위치로 이동하시겠습니까?";
                }

                if (key == "RELMOVE")
                {
                    return $"{_selectedAxisId}축을 현재 위치에서 {ReadOperationField("REL Distance", 0.0).ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture)} mm 이동하시겠습니까?";
                }

                if (key == "MULTIABSMOVE")
                {
                    var targets = MultiMoveAxisRows.Where(row => row.IsSelected).Select(row => $"{row.Axis} = {row.TargetPosition} mm").ToArray();
                    return targets.Length == 0 ? "선택한 축이 없습니다." : $"선택한 축을 {string.Join(", ", targets)} 위치로 이동하시겠습니까?";
                }
            }

            if (SelectedTab == "LASER")
            {
                if (key == "SETQSW")
                {
                    return $"QSW 값을 {ReadOperationField("QSW", 20000.0).ToString("0.###", CultureInfo.InvariantCulture)} Hz로 설정하시겠습니까?";
                }

                if (key == "SETEPRF")
                {
                    return $"EPRF 값을 {ReadOperationField("EPRF", 20000.0).ToString("0.###", CultureInfo.InvariantCulture)} Hz로 설정하시겠습니까?";
                }

                if (key == "SETSHG")
                {
                    return $"SHG 값을 {ReadOperationField("SHG Count", 0.0).ToString("0", CultureInfo.InvariantCulture)} count로 설정하시겠습니까?";
                }

                if (key == "SETQMODE")
                {
                    return $"Q MODE 값을 {ReadOperationField("Q Mode", 0.0).ToString("0", CultureInfo.InvariantCulture)}으로 설정하시겠습니까?";
                }
            }

            if (SelectedTab == "ATTENUATOR")
            {
                if (key == "HOME")
                {
                    return "감쇠기를 원점으로 복귀하시겠습니까?";
                }

                if (key == "MOVEABS")
                {
                    return $"감쇠기를 {ReadOperationField("Target Position", 55.0).ToString("0.000", CultureInfo.InvariantCulture)}° 위치로 이동하시겠습니까?";
                }

                if (key == "MOVEREL")
                {
                    return $"감쇠기를 현재 위치에서 {ReadOperationField("Relative Move", 0.0).ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture)}° 이동하시겠습니까?";
                }

                if (key is "RESET" or "RESETALARM")
                {
                    return "감쇠기를 초기화하시겠습니까?\n초기화 후에는 원점 복귀가 필요합니다.";
                }

                if (key == "SETHOMETYPE")
                {
                    var homeType = OperationFields.FirstOrDefault(field => field.Parameter.Equals("Home Type", StringComparison.OrdinalIgnoreCase))?.Value;
                    var displayValue = string.IsNullOrWhiteSpace(homeType) ? FormatHomeTypeOption(4) : homeType;
                    return $"원점 방식을 설정하시겠습니까?\n원점 방식: {displayValue}";
                }

                if (key is "SETNEGLIMIT" or "SETNEGATIVELIMIT" or "SETNEGATIVESOFTWARELIMIT")
                {
                    return $"음(-) 방향 이동 한계값을 {ReadOperationField("Negative Limit", 0.0).ToString("0.000", CultureInfo.InvariantCulture)}°로 설정하시겠습니까?";
                }

                if (key is "SETPOSLIMIT" or "SETPOSITIVELIMIT" or "SETPOSITIVESOFTWARELIMIT")
                {
                    return $"양(+) 방향 이동 한계값을 {ReadOperationField("Positive Limit", 360.0).ToString("0.000", CultureInfo.InvariantCulture)}°로 설정하시겠습니까?";
                }

                if (key == "SETIGAIN")
                {
                    return $"I Gain 값을 {ReadOperationField("I Gain", 800.0).ToString("0.000", CultureInfo.InvariantCulture)}으로 설정하시겠습니까?";
                }

                if (key == "SETPGAIN")
                {
                    return $"P Gain 값을 {ReadOperationField("P Gain", 10.0).ToString("0.000", CultureInfo.InvariantCulture)}으로 설정하시겠습니까?";
                }

                if (key is "SETIF" or "SETINTERPOLATIONFACTOR")
                {
                    return $"보간 계수를 {ReadOperationField("Interpolation Factor", 1000.0).ToString("0.000", CultureInfo.InvariantCulture)}으로 설정하시겠습니까?";
                }
            }

            if (SelectedTab == "BET")
            {
                if (key == "HOME")
                {
                    return "BET를 원점으로 복귀하시겠습니까?";
                }

                if (key == "MOVEMAG")
                {
                    return $"MAG를 {ReadOperationField("Target MAG POS", 1020.0).ToString("0.###", CultureInfo.InvariantCulture)} step 위치로 이동하시겠습니까?";
                }

                if (key == "MOVEDIV")
                {
                    return $"DIV를 {ReadOperationField("Target DIV POS", 1626.0).ToString("0.###", CultureInfo.InvariantCulture)} step 위치로 이동하시겠습니까?";
                }

                if (key.StartsWith("MOVETABLE", StringComparison.OrdinalIgnoreCase))
                {
                    var tableNo = key["MOVETABLE".Length..];
                    var selectedRow = BetTableRows.FirstOrDefault(row => row.No.Equals(tableNo, StringComparison.OrdinalIgnoreCase));
                    if (selectedRow is not null)
                    {
                        var description = string.IsNullOrWhiteSpace(selectedRow.Description) ? $"테이블 {selectedRow.No}" : selectedRow.Description;
                        return $"{description} 위치로 이동하시겠습니까?\nMAG = {selectedRow.Mag} step, DIV = {selectedRow.Div} step";
                    }
                }
            }

            if (SelectedTab == "POWER METER")
            {
                if (key == "START")
                {
                    var targetPower = ReadPwmSetting("TARGET POWER", 1.2);
                    var mode = ReadPwmText("TOLERANCE MODE", "FINE");
                    return $"H{_selectedAttenuatorNumber + 1:00} ATT를 조절해서 {targetPower.ToString("0.####", CultureInfo.InvariantCulture)} W 목표 파워를 찾으시겠습니까?\n모드: {mode}";
                }
            }

            if (SelectedTab == "PICO MOTOR" && key is "SETVEL" or "SETVELOCITY" or "PICOSETVEL")
            {
                return $"모터 {_selectedPicoMotorNo}: 속도를 {ReadOperationField("Set Velocity", 0.0).ToString("0.00", CultureInfo.InvariantCulture)} mm/sec로 설정하시겠습니까?";
            }

            if (SelectedTab == "PICO MOTOR" && key is "SETACC" or "SETACCELERATION" or "PICOSETACC")
            {
                return $"모터 {_selectedPicoMotorNo}: 가속도를 {ReadOperationField("Set Acceleration", 0.0).ToString("0.00", CultureInfo.InvariantCulture)} msec로 설정하시겠습니까?";
            }

            if (SelectedTab == "PICO MOTOR" && key == "HOME")
            {
                return $"모터 {_selectedPicoMotorNo}: 원점으로 복귀하시겠습니까?";
            }

            if (SelectedTab == "PICO MOTOR" && key == "ABSMOVE")
            {
                return $"모터 {_selectedPicoMotorNo}: {ReadOperationField("Absolute Move", 0.0).ToString("0.###", CultureInfo.InvariantCulture)} mm 위치로 이동하시겠습니까?";
            }

            if (SelectedTab == "PICO MOTOR" && key == "START")
            {
                var motors = _picoAllMoveMotorNos.OrderBy(number => number).Select(number => number.ToString(CultureInfo.InvariantCulture)).ToArray();
                var motorText = motors.Length == 0 ? "선택한 모터 없음" : $"모터 {string.Join(", ", motors)}";
                var position = ReadOperationField("Position", 0.0).ToString("0.###", CultureInfo.InvariantCulture);
                var setCount = Math.Max(0, (int)Math.Round(ReadOperationField("Set Count", 0)));
                return $"설정한 DOE 틸트 동작을 시작하시겠습니까?\n대상: {motorText}\n위치: {position} mm, 반복: {setCount}회";
            }

            return key switch
            {
                "SERVOON" => "서보를 켜시겠습니까?",
                "SERVOOFF" => "서보를 끄시겠습니까?",
                "HOME" => "원점 복귀를 실행하시겠습니까?",
                "ABSMOVE" or "MULTIABSMOVE" => "절대 위치로 이동하시겠습니까?",
                "RELMOVE" => "상대 위치로 이동하시겠습니까?",
                "LASERON" => "레이저를 켜시겠습니까?",
                "LASEROFF" => "레이저를 끄시겠습니까?",
                "SHUTTEROPEN" => "셔터를 여시겠습니까?",
                "SHUTTERCLOSE" => "셔터를 닫으시겠습니까?",
                "GATEON" => "게이트를 켜시겠습니까?",
                "GATEOFF" => "게이트를 끄시겠습니까?",
                "EXTGATEENABLE" => "외부 게이트를 활성화하시겠습니까?",
                "EXTGATEDISABLE" => "외부 게이트를 비활성화하시겠습니까?",
                "SETQSW" => "QSW 값을 설정하시겠습니까?",
                "SETEPRF" => "EPRF 값을 설정하시겠습니까?",
                "SETSHG" => "SHG 값을 설정하시겠습니까?",
                "SETQMODE" => "Q MODE 값을 설정하시겠습니까?",
                "RESET" or "RESETALARM" => "알람 초기화를 실행하시겠습니까?",
                "MOVEABS" => "절대 위치로 이동하시겠습니까?",
                "MOVEREL" => "상대 위치로 이동하시겠습니까?",
                "SETHOMETYPE" => "원점 방식을 설정하시겠습니까?",
                "SETNEGLIMIT" => "음의 한계값을 설정하시겠습니까?",
                "SETPOSLIMIT" => "양의 한계값을 설정하시겠습니까?",
                "SETIGAIN" => "I Gain 값을 설정하시겠습니까?",
                "SETPGAIN" => "P Gain 값을 설정하시겠습니까?",
                "SETIF" => "보간 계수를 설정하시겠습니까?",
                "MOVEMAG" => "MAG 위치로 이동하시겠습니까?",
                "MOVEDIV" => "DIV 위치로 이동하시겠습니까?",
                "SAVETABLE" => "테이블을 저장하시겠습니까?",
                "START" => "동작을 시작하시겠습니까?",
                "CONNECT" => "장비를 연결하시겠습니까?",
                "DISCONNECT" => "장비 연결을 해제하시겠습니까?",
                "SETVEL" or "SETVELOCITY" or "PICOSETVEL" => "속도를 설정하시겠습니까?",
                "SETACC" or "SETACCELERATION" or "PICOSETACC" => "가속도를 설정하시겠습니까?",
                _ when key.StartsWith("MOVETABLE", StringComparison.OrdinalIgnoreCase) => "선택한 테이블 위치로 이동하시겠습니까?",
                _ => $"{label} 명령을 실행하시겠습니까?"};
        }

        private void SetOperationFieldValue(string parameter, string value)
        {
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_MONITOR_PARAMETER_ROW item)
            {
                return item.Parameter.Equals(parameter, StringComparison.OrdinalIgnoreCase);
            }

            var field = OperationFields.FirstOrDefault(MatchesItem);
            if (field is not null)
            {
                field.Value = value;
            }
        }

        private sealed record ST_POWER_METER_OPTION(int Number, string DisplayText);
        private sealed record ST_POWER_METER_TUNE_PROFILE(string Mode, double ToleranceW, int MaxIterations);
        private sealed record ST_POWER_METER_TUNE_SETTINGS(
            double TargetPower, 
            string Mode, 
            double ToleranceW, 
            int MaxIterations, 
            double AttMinimum, 
            double AttMaximum, 
            int SettleTimeMs, 
            int MeasureCount, 
            int MeasureIntervalMs
        )
        {
            public double InitialAttPosition { get; init; } = (AttMinimum + AttMaximum) / 2.0;
        }

        private sealed record ST_POWER_METER_READ_RESULT(bool IsSuccess, double Power, string Message);
        private sealed record ST_POWER_METER_GRAPH_DATA(PointCollection Line, double TargetLineY, string MinText, string MaxText, string TargetText);
        private double ReadOperationField(string parameter, double defaultValue)
        {
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_MONITOR_PARAMETER_ROW field)
            {
                return field.Parameter.Equals(parameter, StringComparison.OrdinalIgnoreCase);
            }

            var value = OperationFields.FirstOrDefault(MatchesItem)?.Value;
            var numericValue = NormalizeOperationNumericValue(value);
            return double.TryParse(numericValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
        }

        private bool TryReadPositiveOperationField(string parameter, out double value)
        {
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_MONITOR_PARAMETER_ROW field)
            {
                return field.Parameter.Equals(parameter, StringComparison.OrdinalIgnoreCase);
            }

            var text = OperationFields.FirstOrDefault(MatchesItem)?.Value;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value > 0.0;
        }

        private static string GetMonitorOperationLabel(object? parameter)
        {
            return parameter switch
            {
                ST_MONITOR_OPERATION_BUTTON button => string.IsNullOrWhiteSpace(button.CommandKey) ? button.Label : button.CommandKey,
                ST_MONITOR_BET_TABLE_ROW row => row.MoveCommandLabel,
                ST_PWM_DEVICE_ROW row => row.Command,
                string text => text,
                _ => ""
            };
        }

        private static string NormalizeMonitorOperation(string label)
        {
            return label.Replace("\r", "", StringComparison.OrdinalIgnoreCase).Replace("\n", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "", StringComparison.OrdinalIgnoreCase).Replace("_", "", StringComparison.OrdinalIgnoreCase).Replace("-", "", StringComparison.OrdinalIgnoreCase).Trim().ToUpperInvariant();
        }

        private void Apply(
            IReadOnlyList<ST_SCREEN_SECTION> deviceTabs, 
            string selectedTab, 
            string title, 
            string subtitle, 
            string statusPanelTitle, 
            string operationPanelTitle, 
            string parameterPanelTitle, 
            string trendPanelTitle, 
            IReadOnlyList<ST_MONITOR_TAB> tabs, 
            IReadOnlyList<ST_MONITOR_AXIS_ROW> axisRows, 
            IReadOnlyList<ST_MONITOR_STATUS_ROW> statusRows, 
            IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> operationButtons, 
            IReadOnlyList<ST_MONITOR_PARAMETER_ROW> operationFields, 
            IReadOnlyList<ST_MONITOR_PARAMETER_ROW> parameterRows, 
            IReadOnlyList<ST_MONITOR_BET_TABLE_ROW> betTableRows, 
            IReadOnlyList<ST_MONITOR_TREND_POINT> trendPoints, 
            IReadOnlyList<ST_MONITOR_SUMMARY_ITEM> summaryItems, 
            IReadOnlyList<ST_MONITOR_POSITION_ROW> positionRows, 
            IReadOnlyList<ST_MONITOR_PRODUCT_ITEM> productItems, 
            IReadOnlyList<ST_MONITOR_PRODUCT_HISTORY_ROW> productHistoryRows, 
            IReadOnlyList<ST_PWM_SETTING_ROW> pwmSettingRows, 
            IReadOnlyList<ST_PWM_DEVICE_ROW> pwmDeviceRows, 
            IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> pwmRunButtons, 
            IReadOnlyList<ST_POWER_METER_TUNE_ROW> powerMeterTuneRows, 
            PointCollection powerMeterGraphLine, 
            double powerMeterTargetLineY, 
            string powerMeterGraphMinText, 
            string powerMeterGraphMaxText, 
            string powerMeterGraphTargetText
        )
        {
            DeviceTabs = deviceTabs;
            SelectedTab = selectedTab;
            Title = title;
            Subtitle = subtitle;
            StatusPanelTitle = statusPanelTitle;
            OperationPanelTitle = operationPanelTitle;
            ParameterPanelTitle = parameterPanelTitle;
            TrendPanelTitle = trendPanelTitle;
            Tabs = tabs;
            AxisRows = axisRows;
            StatusRows = statusRows;
            OperationButtons = operationButtons;
            OperationFields = operationFields;
            ParameterRows = parameterRows;
            BetTableRows = betTableRows;
            TrendPoints = trendPoints;
            SummaryItems = summaryItems;
            PositionRows = positionRows;
            ProductItems = productItems;
            ProductHistoryRows = productHistoryRows;
            PwmSettingRows = pwmSettingRows;
            PwmDeviceRows = pwmDeviceRows;
            PwmRunButtons = pwmRunButtons;
            PowerMeterTuneRows = powerMeterTuneRows;
            PowerMeterGraphLine = powerMeterGraphLine;
            PowerMeterTargetLineY = powerMeterTargetLineY;
            PowerMeterGraphMinText = powerMeterGraphMinText;
            PowerMeterGraphMaxText = powerMeterGraphMaxText;
            PowerMeterGraphTargetText = powerMeterGraphTargetText;
            NotifyMonitorScreenProperties();
        }

        private void NotifyMonitorScreenProperties()
        {
            NotifyMonitorLiveProperties(
                nameof(DeviceTabs), 
                nameof(SelectedTab), 
                nameof(Title), 
                nameof(Subtitle), 
                nameof(StatusPanelTitle), 
                nameof(OperationPanelTitle), 
                nameof(ParameterPanelTitle), 
                nameof(TrendPanelTitle), 
                nameof(Tabs), 
                nameof(IsMotor), 
                nameof(IsLaser), 
                nameof(IsAttenuator), 
                nameof(IsBet), 
                nameof(IsPowerMeter), 
                nameof(IsProduct), 
                nameof(IsMelsec), 
                nameof(IsCoordinateViewer), 
                nameof(IsLogViewer), 
                nameof(IsGenericDevice), 
                nameof(SelectedLaserName), 
                nameof(SelectedHeadDeviceName), 
                nameof(HeadDeviceSelectorTitle)
            );
            switch (SelectedTab)
            {
                case "XPS":
                    NotifyMonitorLiveProperties(
                        nameof(AxisRows), 
                        nameof(SelectedAxisId), 
                        nameof(SelectedAxisRow), 
                        nameof(MultiMoveAxisRows), 
                        nameof(StatusRows), 
                        nameof(OperationButtons), 
                        nameof(OperationFields), 
                        nameof(ParameterRows), 
                        nameof(PositionRows), 
                        nameof(SummaryItems)
                    );
                    break;
                case "LASER":
                    NotifyMonitorLiveProperties(
                        nameof(StatusRows), 
                        nameof(OperationButtons), 
                        nameof(OperationFields), 
                        nameof(HeadSelectRows), 
                        nameof(LaserControlRows), 
                        nameof(ParameterRows), 
                        nameof(TrendPoints), 
                        nameof(SummaryItems), 
                        nameof(PwmSettingRows), 
                        nameof(PwmDeviceRows)
                    );
                    break;
                case "ATTENUATOR":
                    NotifyMonitorLiveProperties(
                        nameof(StatusRows), 
                        nameof(OperationButtons), 
                        nameof(OperationFields), 
                        nameof(HeadSelectRows), 
                        nameof(ParameterRows), 
                        nameof(TrendPoints), 
                        nameof(SummaryItems), 
                        nameof(AttenuatorStatusItems), 
                        nameof(AttenuatorSettingItems), 
                        nameof(PositionRows)
                    );
                    break;
                case "BET":
                    NotifyMonitorLiveProperties(
                        nameof(StatusRows), 
                        nameof(OperationButtons), 
                        nameof(OperationFields), 
                        nameof(HeadSelectRows), 
                        nameof(ParameterRows), 
                        nameof(BetTableRows), 
                        nameof(TrendPoints), 
                        nameof(SummaryItems), 
                        nameof(PositionRows)
                    );
                    break;
                case "POWER METER":
                    NotifyMonitorLiveProperties(
                        nameof(StatusRows), 
                        nameof(OperationButtons), 
                        nameof(OperationFields), 
                        nameof(HeadSelectRows), 
                        nameof(ParameterRows), 
                        nameof(TrendPoints), 
                        nameof(SummaryItems), 
                        nameof(PwmSettingRows), 
                        nameof(PwmDeviceRows), 
                        nameof(PwmRunButtons), 
                        nameof(PowerMeterTuneRows), 
                        nameof(PowerMeterGraphLine), 
                        nameof(PowerMeterTargetLineY), 
                        nameof(PowerMeterGraphMinText), 
                        nameof(PowerMeterGraphMaxText), 
                        nameof(PowerMeterGraphTargetText), 
                        nameof(PowerMeterTuneState), 
                        nameof(PowerMeterTuneMessage)
                    );
                    break;
                case "PICO MOTOR":
                    NotifyMonitorLiveProperties(
                        nameof(StatusRows), 
                        nameof(OperationButtons), 
                        nameof(OperationFields), 
                        nameof(ParameterRows), 
                        nameof(PositionRows), 
                        nameof(SummaryItems), 
                        nameof(PicoConnectionButtons), 
                        nameof(PicoMotorSelectButtons), 
                        nameof(PicoAllMotorSelectButtons)
                    );
                    break;
                case "PRODUCT":
                    NotifyMonitorLiveProperties(
                        nameof(SummaryItems), 
                        nameof(ProductItems), 
                        nameof(ProductHistoryRows), 
                        nameof(ProductGlassItems), 
                        nameof(ProductAlignItems), 
                        nameof(ProductDistortionItems), 
                        nameof(ProductProcessItems), 
                        nameof(ProductRecipeItems), 
                        nameof(ProductReviewItems), 
                        nameof(ProductReviewRows)
                    );
                    break;
                case "MELSEC":
                    NotifyMonitorLiveProperties(
                        nameof(StatusRows), 
                        nameof(OperationButtons), 
                        nameof(OperationFields), 
                        nameof(ParameterRows), 
                        nameof(SummaryItems), 
                        nameof(MelsecGroups), 
                        nameof(MelsecRows), 
                        nameof(MelsecReadRows), 
                        nameof(MelsecWriteRows)
                    );
                    break;
                case "COORDINATE VIEWER":
                    NotifyCoordinateViewerProperties();
                    break;
                default:
                    NotifyMonitorLiveProperties(nameof(StatusRows), nameof(OperationButtons), nameof(OperationFields), nameof(ParameterRows), nameof(TrendPoints), nameof(SummaryItems));
                    break;
            }
        }

        private void NotifyMonitorLiveProperties(params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                var currentValue = GetLivePropertyValue(propertyName);
                if (_lastNotifiedLiveValues.TryGetValue(propertyName, out var previousValue) && LiveValuesEqual(previousValue, currentValue))
                {
                    continue;
                }

                _lastNotifiedLiveValues[propertyName] = currentValue;
                OnPropertyChanged(propertyName);
            }
        }

        private object? GetLivePropertyValue(string propertyName)
        {
            PropertyInfo? property;
            lock (LivePropertyCacheLock)
            {
                if (!LivePropertyCache.TryGetValue(propertyName, out property))
                {
                    property = typeof(CMenuMonitor).GetProperty(propertyName);
                    LivePropertyCache[propertyName] = property;
                }
            }

            return property?.GetValue(this);
        }

        private static bool LiveValuesEqual(object? previousValue, object? currentValue)
        {
            if (ReferenceEquals(previousValue, currentValue) || Equals(previousValue, currentValue))
            {
                return true;
            }

            if (previousValue is string || currentValue is string || previousValue is not IEnumerable previousItems || currentValue is not IEnumerable currentItems)
            {
                return false;
            }

            if (TryGetEnumerableCount(previousItems, out var previousCount) && TryGetEnumerableCount(currentItems, out var currentCount) && previousCount != currentCount)
            {
                return false;
            }

            return previousItems.Cast<object?>().SequenceEqual(currentItems.Cast<object?>());
        }

        private static bool TryGetEnumerableCount(IEnumerable items, out int count)
        {
            if (items is ICollection collection)
            {
                count = collection.Count;
                return true;
            }

            var countProperty = items.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
            if (countProperty?.PropertyType == typeof(int) && countProperty.GetValue(items)is int value)
            {
                count = value;
                return true;
            }

            count = 0;
            return false;
        }

        private static IReadOnlyList<ST_SCREEN_SECTION> CreateTabSections(IReadOnlyList<ST_MONITOR_TAB> tabs)
        {
            global::Drilling.UI.Menu.Menus.ST_SCREEN_SECTION BuildSCREEN_SECTION(global::Drilling.UI.Menu.Menus.ST_MONITOR_TAB tab)
            {
                return new ST_SCREEN_SECTION(tab.Name, Array.Empty<ST_DISPLAY_ITEM>());
            }

            return tabs.Select(BuildSCREEN_SECTION).ToArray();
        }

        private static string NormalizeMonitorScreenTab(string? tab)
        {
            var normalized = NormalizeMonitorTab(tab);
            return normalized == "POWER METER" ? "LASER" : normalized;
        }

        private static string NormalizeMonitorTab(string? tab)
        {
            var normalized = (tab ?? "PRODUCT").Trim().ToUpperInvariant();
            return normalized switch
            {
                "ATT" => "ATTENUATOR",
                "LASER/POWERMETER" => "LASER",
                "POWER" or "POWERMETER" or "POWER_METER" => "POWER METER",
                "MOTOR" or "XPS" or "SCANNERTILT" or "SCANNER TILT" or "SCANNER_TILT" => "XPS",
                "PICO" or "PICOMOTOR" or "PICO_MOTOR" or "DOETILT" or "DOE TILT" or "DOE_TILT" => "PICO MOTOR",
                "COORDINATE" or "COORDINATEVIEWER" or "COORDINATE_VIEWER" => "COORDINATE VIEWER",
                "LASER" or "ATTENUATOR" or "BET" or "POWER METER" or "PICO MOTOR" or "PRODUCT" or "MELSEC" or "COORDINATE VIEWER" or "LOG VIEWER" => normalized,
                _ => "PRODUCT"
            };
        }

        private static string GetSubtitle(string tab)
        {
            return tab switch
            {
                "XPS" => "Newport XPS axis position monitor and service operation",
                "LASER" => "Talon laser control and one-shot PowerMeter queries",
                "ATTENUATOR" => "Conex AGP attenuator position monitor and service operation",
                "BET" => "Beam expander magnification and divergence monitor",
                "POWER METER" => "Power meter measurement monitor and Stage PC measurement-position command",
                "PICO MOTOR" => "PicoMotor controller position and service operation",
                "PRODUCT" => "Active product status and product history monitor",
                "MELSEC" => "PLC MELSEC map monitor and direct read/write operation",
                "COORDINATE VIEWER" => "Coordinate system viewer",
                _ => "Auxiliary monitor status and service information"
            };
        }

        private static string GetStatusPanelTitle(string tab)
        {
            return tab switch
            {
                "LASER" => "Laser Status",
                "ATTENUATOR" => "Attenuator Status",
                "BET" => "BET Status",
                "POWER METER" => "PowerMeter Status",
                "XPS" => "XPS Status",
                "PICO MOTOR" => "PicoMotor Status",
                "PRODUCT" => "Product Status",
                "MELSEC" => "MELSEC Status",
                _ => "Monitor Status"
            };
        }

        private static string GetOperationPanelTitle(string tab)
        {
            return tab switch
            {
                "XPS" => "XPS Axis Operation",
                "LASER" => "Laser Operation",
                "ATTENUATOR" => "Attenuator Operation",
                "BET" => "BET Operation",
                "POWER METER" => "PowerMeter Operation",
                "PICO MOTOR" => "PicoMotor Operation",
                "MELSEC" => "MELSEC Read / Write",
                _ => "Monitor Operation"
            };
        }

        private static string GetParameterPanelTitle(string tab)
        {
            return tab switch
            {
                "XPS" => "XPS Motion Profile",
                "LASER" => "Laser Parameter",
                "ATTENUATOR" => "Attenuator Parameter",
                "BET" => "BET Table",
                "POWER METER" => "PowerMeter Parameter",
                "PICO MOTOR" => "PicoMotor Position",
                "PRODUCT" => "Product Detail",
                "MELSEC" => "MELSEC Map",
                _ => "Monitor Parameter"
            };
        }

        private static string GetTrendPanelTitle(string tab)
        {
            return tab switch
            {
                "LASER" => "Laser Trend",
                "ATTENUATOR" => "Current Position",
                "BET" => "Beam Expander Position",
                "POWER METER" => "Power Trend",
                "PICO MOTOR" => "Motor Position",
                "MELSEC" => "MELSEC Signal",
                _ => "Signal Trend"
            };
        }

        private static IReadOnlyList<ST_MONITOR_STATUS_ROW> CreateStatusRows(
            string tab, 
            ST_DEVICE_STATUS snapshot, 
            IReadOnlyList<ST_DEVICE_COMM_STATUS> communication, 
            string? selectedConnectionText = null, 
            ST_TALON_STATUS? talonStatus = null
        )
        {
            var module = GetMonitorModule(tab);
            var communicationText = !string.IsNullOrWhiteSpace(selectedConnectionText) ? selectedConnectionText : module is null ? "-" : ToCommunicationText(GetModuleState(communication, module.Value));
            var laserTalonStatus = talonStatus ?? ST_TALON_STATUS.Empty;
            return tab switch
            {
                "LASER" => [
                    new("Output Power", snapshot.Laser.OutputPower.ToString("F3"), snapshot.Laser.PowerOn ? "ON" : "SAFE", "W", "Measured output power"), 
                    new("Set Power", "1.200", "-", "W", "Target output power"), 
                    new("Frequency", "20.000", "-", "kHz", "Pulse frequency"), 
                    new("Gate", snapshot.Laser.GateOn ? "OPEN" : "CLOSE", snapshot.Laser.GateOn ? "OPEN" : "CLOSE", "-", "Laser gate state"), 
                    new("Ext Gate", laserTalonStatus.ExtGateEnable ? "ENABLED" : "DISABLED", laserTalonStatus.ExtGateEnable ? "ENABLED" : "DISABLED", "-", "Laser external gate input setting"), 
                    new("Shutter", snapshot.Laser.ShutterOpen ? "OPEN" : "CLOSE", snapshot.Laser.ShutterOpen ? "OPEN" : "CLOSE", "-", "Laser shutter state"), 
                    new("Laser Mode", snapshot.Laser.PowerOn ? "ON" : "SAFE", snapshot.Laser.PowerOn ? "ON" : "SAFE", "-", "Laser operating mode"), 
                    new("Diode State", "OK", "OK", "-", "Diode status"), 
                    new("Temperature", "24.6", "OK", "C", "Laser head temperature")
                ],
                "ATTENUATOR" => [
                    new("Controller", "CONEX_AGP", "OK", "-", "Attenuator controller"), 
                    new("Current Position", snapshot.Attenuator.CurrentPosition.ToString("F3", CultureInfo.InvariantCulture), "OK", "DEG", "Current attenuator position"), 
                    new("Target Position", snapshot.Attenuator.TargetPosition.ToString("F3", CultureInfo.InvariantCulture), "OK", "DEG", "Target attenuator position"), 
                    new("Moving", snapshot.Attenuator.CommandState, snapshot.Attenuator.CommandState, "-", "Motion state"), 
                    new(
                    "In Position", 
                    IsInPosition(snapshot.Attenuator.CurrentPosition, snapshot.Attenuator.TargetPosition) ? "YES" : "NO", 
                    IsInPosition(snapshot.Attenuator.CurrentPosition, snapshot.Attenuator.TargetPosition) ? "YES" : "WARN", 
                    "-", 
                    "In position status"
                ), 
                    new("Home Type", FormatHomeType(snapshot.Attenuator.HomeType), FormatKnownState(snapshot.Attenuator.HomeType), "-", "CONEX-AGP HT home search type"), 
                    new("Negative SW Limit", FormatNullableDouble(snapshot.Attenuator.NegativeSoftwareLimit), FormatKnownState(snapshot.Attenuator.NegativeSoftwareLimit), "DEG", "CONEX-AGP SL negative software limit"), 
                    new("Positive SW Limit", FormatNullableDouble(snapshot.Attenuator.PositiveSoftwareLimit), FormatKnownState(snapshot.Attenuator.PositiveSoftwareLimit), "DEG", "CONEX-AGP SR positive software limit"), 
                    new("I Gain", FormatNullableDouble(snapshot.Attenuator.IGain), FormatKnownState(snapshot.Attenuator.IGain), "-", "CONEX-AGP KI integral gain"), 
                    new("P Gain", FormatNullableDouble(snapshot.Attenuator.PGain), FormatKnownState(snapshot.Attenuator.PGain), "-", "CONEX-AGP KP proportional gain"), 
                    new("Interpolation Factor", FormatNullableDouble(snapshot.Attenuator.InterpolationFactor), FormatKnownState(snapshot.Attenuator.InterpolationFactor), "-", "CONEX-AGP IF interpolation factor"), 
                    new("Last Error", snapshot.Attenuator.LastError.ToString(), snapshot.Attenuator.LastError == EN_CONEX_AGP_ERROR.Ok ? "OK" : "ERROR", "-", "Last CONEX-AGP command error"), 
                    new("Last Command", snapshot.Attenuator.CommandState, "OK", "-", "Last command name")
                ],
                "BET" => [
                    new("Connection", communicationText, communicationText, "-", "Beam expander controller link"), 
                    new("Controller", "BET_CTRL", "OK", "-", "Beam expander controller"), 
                    new("MAG POS", FormatBETStep(snapshot.Bet.CurrentMagnification), "", "step", "Current MAG motor position"), 
                    new("DIV POS", FormatBETStep(snapshot.Bet.CurrentDivergence), "", "step", "Current DIV motor position"), 
                    new("Target MAG POS", FormatBETStep(snapshot.Bet.TargetMagnification), "", "step", "Target MAG motor position"), 
                    new("Target DIV POS", FormatBETStep(snapshot.Bet.TargetDivergence), "", "step", "Target DIV motor position"), 
                    new("Moving", snapshot.Bet.IsMoving ? "MOVING" : "IDLE", snapshot.Bet.IsMoving ? "MOVING" : "IDLE", "-", "Motion state"), 
                    new("Mag Home", snapshot.Bet.MagHomeCompleted ? "DONE" : "NO", snapshot.Bet.MagHomeCompleted ? "DONE" : "WARN", "-", "Magnification home state"), 
                    new("Div Home", snapshot.Bet.DivHomeCompleted ? "DONE" : "NO", snapshot.Bet.DivHomeCompleted ? "DONE" : "WARN", "-", "Divergence home state"), 
                    new("Alarm Code", snapshot.Bet.AlarmOn ? "1" : "0", snapshot.Bet.AlarmOn ? "ALARM" : "OK", "-", "Current alarm code"), 
                    new("Last Command", string.IsNullOrWhiteSpace(snapshot.Bet.LastCommand) ? "-" : snapshot.Bet.LastCommand, "", "-", "Last command name"), 
                    new("Communication State", communicationText, communicationText, "-", "Communication status")
                ],
                "POWER METER" => [
                    new("Connection", communicationText, communicationText, "-", "Power meter controller connection"), 
                    new("Model", snapshot.PowerMeter.ModelName, string.IsNullOrWhiteSpace(snapshot.PowerMeter.ModelName) ? "WARN" : "OK", "-", "Power meter model"), 
                    new(
                    "Serial No", 
                    snapshot.PowerMeter.SerialNumber, 
                    string.IsNullOrWhiteSpace(snapshot.PowerMeter.SerialNumber) || snapshot.PowerMeter.SerialNumber == "-" ? "WARN" : "OK", 
                    "-", 
                    "Power meter serial number"
                ), 
                    new("Current Power", snapshot.PowerMeter.MeasuredPower.ToString("F4", CultureInfo.InvariantCulture), "OK", snapshot.PowerMeter.Unit, "Latest measured power"), 
                    new("Average Power", snapshot.PowerMeter.AveragePower.ToString("F4", CultureInfo.InvariantCulture), "OK", snapshot.PowerMeter.Unit, "Average measured power"), 
                    new("Min Power", snapshot.PowerMeter.MinPower.ToString("F4", CultureInfo.InvariantCulture), "OK", snapshot.PowerMeter.Unit, "Minimum measured power"), 
                    new("Max Power", snapshot.PowerMeter.MaxPower.ToString("F4", CultureInfo.InvariantCulture), "OK", snapshot.PowerMeter.Unit, "Maximum measured power"), 
                    new("WaveLength", snapshot.PowerMeter.WaveLengthNm.ToString("F1", CultureInfo.InvariantCulture), "OK", "nm", "Sensor wavelength setting"), 
                    new("Beam Pos X", snapshot.PowerMeter.BeamPositionX.ToString("F3", CultureInfo.InvariantCulture), "OK", "mm", "Measured beam X position"), 
                    new("Beam Pos Y", snapshot.PowerMeter.BeamPositionY.ToString("F3", CultureInfo.InvariantCulture), "OK", "mm", "Measured beam Y position"), 
                    new("Measure Count", snapshot.PowerMeter.SampleCount.ToString(CultureInfo.InvariantCulture), "OK", "ea", "Accumulated measure count"), 
                    new("Measure State", snapshot.PowerMeter.IsMeasuring ? "RUN" : "IDLE", snapshot.PowerMeter.IsMeasuring ? "RUN" : "IDLE", "-", "Measurement state"), 
                    new(
                    "Last Command", 
                    string.IsNullOrWhiteSpace(snapshot.PowerMeter.LastCommand) ? "-" : snapshot.PowerMeter.LastCommand, 
                    snapshot.PowerMeter.LastError == EN_POWER_METER_ERROR.Ok ? "OK" : "ERROR", 
                    "-", 
                    "Last command name"
                )
                ],
                "MELSEC" => [
                    new("Connection", communicationText, communicationText, "-", "MELSEC PLC connection"), 
                    new("Device", "MELSEC_0", communicationText, "-", "Selected MELSEC device"), 
                    new("Map Source", "JHMI_MELSEC_MAP.csv", "OK", "-", "MELSEC map file"), 
                    new("Read", "ID based", "READY", "-", "Read by map ID"), 
                    new("Write", "ACCESS W/RW", "READY", "-", "Write is enabled only for writable rows")
                ],
                _ => [
                    new("System Mode", "SIM", "SIM", "-", "Device-free monitor mode"), 
                    new("Update Rate", "500", "OK", "ms", "Monitor refresh interval"), 
                    new("Data Source", "Simulation", "OK", "-", "Current data provider"), 
                    new("Operator", "Engineer", "OK", "-", "Current user level")
                ]
            };
        }

        private static EN_EQP_MODULE? GetMonitorModule(string tab)
        {
            return tab switch
            {
                "XPS" => EN_EQP_MODULE.Motion,
                "LASER" => EN_EQP_MODULE.TalonLaser,
                "ATTENUATOR" => EN_EQP_MODULE.Attenuator,
                "BET" => EN_EQP_MODULE.Bet,
                "POWER METER" => EN_EQP_MODULE.PowerMeter,
                "PICO MOTOR" => EN_EQP_MODULE.PicoMotor,
                "MELSEC" => EN_EQP_MODULE.Melsec,
                _ => null
            };
        }

        private static EN_COMM_STATE GetModuleState(IReadOnlyList<ST_DEVICE_COMM_STATUS> communication, EN_EQP_MODULE module)
        {
            bool MatchesItem(global::Drilling.Common.Interface.ST_DEVICE_COMM_STATUS status)
            {
                return status.Module == module;
            }

            return communication.FirstOrDefault(MatchesItem)?.ConnectionState ?? EN_COMM_STATE.Offline;
        }

        private static string ToCommunicationText(EN_COMM_STATE state)
        {
            return state switch
            {
                EN_COMM_STATE.Online => "ONLINE",
                EN_COMM_STATE.Simulation => "SIMULATION",
                _ => "OFFLINE"
            };
        }

        private static IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> CreateOperationButtons(string tab, ST_LASER_STATUS laserStatus, ST_TALON_STATUS talonStatus)
        {
            return tab switch
            {
                "XPS" => [
                    new("SERVO ON", "Servo", "Green"), 
                    new("SERVO OFF", "Servo", "Dark"), 
                    new("HOME", "Home", "Blue"), 
                    new("ABS MOVE", "Abs", "Blue"), 
                    new("REL MOVE", "Rel", "Dark"), 
                    new("STOP", "Stop", "Red"), 
                    new("RESET ALARM", "Alarm", "Dark"), 
                    new("REFRESH", "Refresh", "Dark")
                ],
                "LASER" => CreateLaserOperationButtons(laserStatus, talonStatus),
                "ATTENUATOR" => [new("MOVE ABS", "Move", "Blue"), new("MOVE REL", "Move", "Blue"), new("HOME", "Home", "Blue"), new("STOP", "Stop", "Red"), new("RESET", "Reset", "Dark")],
                "BET" => [new("HOME", "Home", "Blue"), new("STOP", "Stop", "Red"), new("RESET ALARM", "Alarm", "Dark")],
                _ => [new("REFRESH", "Refresh", "Blue"), new("RESET", "Reset", "Dark")]
            };
        }

        private static IReadOnlyList<ST_MONITOR_PARAMETER_ROW> CreateParameterRows(string tab)
        {
            return tab switch
            {
                "XPS" => [],
                "LASER" => [
                    new("Laser Power", "1.200", "W", "Warn"), 
                    new("Frequency", "20.000", "kHz", "Warn"), 
                    new("Mark Speed", "900", "mm/s", "Warn"), 
                    new("Jump Speed", "1500", "mm/s"), 
                    new("Laser On Delay", "8", "us", "Warn"), 
                    new("Laser Off Delay", "12", "us", "Warn"), 
                    new("Shot Count", "48000", "count"), 
                    new("Time Mode", "10", "ms"), 
                    new("Count Mode", "48000", "count")
                ],
                "ATTENUATOR" => [
                    new("Process Target Position", "55.000", "DEG", "Warn"), 
                    new("Position Tolerance", "0.100", "DEG", "Warn"), 
                    new("Negative Limit", "-120.000", "DEG"), 
                    new("Positive Limit", "360.000", "DEG"), 
                    new("Move Timeout", "30.000", "sec", "Warn")
                ],
                "BET" => [
                    new("Default Magnification", "1.000", "x", "Warn"), 
                    new("Default Divergence", "1.000", "x", "Warn"), 
                    new("Mag Move Speed", "0.250", "x/sec", "Warn"), 
                    new("Div Move Speed", "0.250", "x/sec", "Warn"), 
                    new("Mag Tolerance", "0.001", "x"), 
                    new("Div Tolerance", "0.001", "x"), 
                    new("Positive Limit", "4.000", "x"), 
                    new("Negative Limit", "0.250", "x"), 
                    new("Move Timeout", "20.000", "sec", "Warn"), 
                    new("On Delay", "20", "ms"), 
                    new("Off Delay", "20", "ms")
                ],
                "POWER METER" => [
                    new("WaveLength", "355.0", "nm", "Warn"), 
                    new("Power High Limit", "1.5000", "W", "Warn"), 
                    new("Power Low Limit", "0.8000", "W", "Warn"), 
                    new("Average Count", "10", "count", "Warn"), 
                    new("Measure Time", "1000", "ms"), 
                    new("Measure Interval", "100", "ms"), 
                    new("Stage Move Timeout", "30.000", "sec", "Warn"), 
                    new("Stage X Target", "0.000", "mm"), 
                    new("Stage Y Target", "0.000", "mm"), 
                    new("Stage Z Target", "0.000", "mm"), 
                    new("Read Command", "pw?", "-"), 
                    new("Position Command", "pos", "-")
                ],
                "MELSEC" => [
                    new("Map File", "JHMI_MELSEC_MAP.csv", "-"), 
                    new("Call Rule", "ID", "-", "Accent"), 
                    new("Bit Write", "ON/OFF or 1/0", "-"), 
                    new("Numeric Write", "Invariant Number", "-"), 
                    new("Read Access", "R / RW", "-"), 
                    new("Write Access", "W / RW", "-"), 
                    new("Read Scale", "CSV READ_SCALE (multiply)", "-"), 
                    new("Write Scale", "CSV WRITE_SCALE (multiply)", "-"), 
                    new("Device No", "MELSEC instance number", "-")
                ],
                _ => [new("Refresh Interval", "500", "ms", "Warn"), new("Log Retention", "30", "day")]
            };
        }

        private IReadOnlyList<ST_MONITOR_PARAMETER_ROW> CreateOperationFields(string tab, ST_PICO_MOTOR_STATUS picoStatus)
        {
            return tab switch
            {
                "XPS" => [
                    CreateOperationField(tab, "ABS Target Position", "0.000", "mm"), 
                    CreateOperationField(tab, "ABS Speed", "10.000", "mm/sec"), 
                    CreateOperationField(tab, "ABS Acceleration", "100.000", "mm/sec2"), 
                    CreateOperationField(tab, "REL Distance", "0.000", "mm"), 
                    CreateOperationField(tab, "REL Speed", "10.000", "mm/sec"), 
                    CreateOperationField(tab, "REL Acceleration", "100.000", "mm/sec2"), 
                    CreateOperationField(tab, "Jog Speed", "10.000", "mm/sec"), 
                    CreateOperationField(tab, "Jog Acceleration", "100.000", "mm/sec2"), 
                    CreateOperationField(tab, "Multi Speed", "10.000", "mm/sec"), 
                    CreateOperationField(tab, "Multi Acceleration", "100.000", "mm/sec2")
                ],
                "LASER" => [
                    CreateOperationField(tab, "QSW", "20000", "Hz"), 
                    CreateOperationField(tab, "EPRF", "20000", "Hz"), 
                    CreateOperationField(tab, "SHG Count", "0", "count"), 
                    CreateOperationField(tab, "Q Mode", "0", "-")
                ],
                "ATTENUATOR" => [
                    CreateOperationField(tab, "Target Position", "55.000", "DEG", "MOVE ABS"), 
                    CreateOperationField(tab, "Relative Move", "0.000", "DEG", "MOVE REL"), 
                    CreateOperationField(tab, "Home Type", FormatHomeTypeOption(4), "-", "SET HOME TYPE", HomeTypeOptions), 
                    CreateOperationField(tab, "Negative Limit", "0.000", "DEG", "SET NEG LIMIT"), 
                    CreateOperationField(tab, "Positive Limit", "360.000", "DEG", "SET POS LIMIT"), 
                    CreateOperationField(tab, "I Gain", "800.000", "-", "SET I GAIN"), 
                    CreateOperationField(tab, "P Gain", "10.000", "-", "SET P GAIN"), 
                    CreateOperationField(tab, "Interpolation Factor", "1000.000", "-", "SET IF")
                ],
                "BET" => [CreateOperationField(tab, "Target MAG POS", "1020", "step"), CreateOperationField(tab, "Target DIV POS", "1626", "step")],
                "POWER METER" => [
                    CreateOperationField(tab, "WaveLength", "355.0", "nm"), 
                    CreateOperationField(tab, "Stage X", "0.000", "mm"), 
                    CreateOperationField(tab, "Stage Y", "0.000", "mm"), 
                    CreateOperationField(tab, "Stage Z", "0.000", "mm"), 
                    CreateOperationField(tab, "Measure Time", "1000", "ms"), 
                    CreateOperationField(tab, "Measure Count", "10", "ea")
                ],
                "PICO MOTOR" => [
                    new("Current Velocity", picoStatus.CurrentVelocity.ToString("F2", CultureInfo.InvariantCulture), "mm/sec"), 
                    CreateOperationField(tab, "Set Velocity", "0.00", "mm/sec"), 
                    new("Current Acceleration", picoStatus.CurrentAcceleration.ToString("F2", CultureInfo.InvariantCulture), "msec"), 
                    CreateOperationField(tab, "Set Acceleration", "0.00", "msec"), 
                    new("Home Position", picoStatus.HomePosition.ToString(CultureInfo.InvariantCulture), "step"), 
                    CreateOperationField(tab, "Relative Move", "0.000", "mm"), 
                    CreateOperationField(tab, "Absolute Move", "0.000", "mm"), 
                    new("Cur Count", picoStatus.AllMoveCurrentCount.ToString(CultureInfo.InvariantCulture), "count"), 
                    CreateOperationField(tab, "Set Count", "1", "count"), 
                    CreateOperationField(tab, "Position", "0.000", "mm")
                ],
                _ => [CreateOperationField(tab, "Refresh Interval", "500", "ms"), CreateOperationField(tab, "Timeout", "3000", "ms")]
            };
        }

        private ST_MONITOR_PARAMETER_ROW CreateOperationField(string tab, string parameter, string defaultValue, string unit, string commandLabel = "", IReadOnlyList<string>? valueOptions = null)
        {
            var value = _operationFieldValues.TryGetValue(CreateOperationFieldKey(tab, parameter), out var storedValue) ? NormalizeOperationFieldValue(storedValue, valueOptions) ?? defaultValue : defaultValue;
            return new ST_MONITOR_PARAMETER_ROW(parameter, value, unit, commandLabel: commandLabel, valueOptions: valueOptions ?? []);
        }

        private void SaveOperationFieldValues(string tab)
        {
            foreach (var field in OperationFields)
            {
                _operationFieldValues[CreateOperationFieldKey(tab, field.Parameter)] = field.Value;
            }
        }

        private static string CreateOperationFieldKey(string tab, string parameter)
        {
            return $"{NormalizeMonitorTab(tab)}::{parameter.Trim().ToUpperInvariant()}";
        }

        private int GetSelectedHeadNumber(string tab)
        {
            switch (NormalizeMonitorTab(tab))
            {
                case "ATTENUATOR":
                case "POWER METER":
                    return _selectedAttenuatorNumber;
                case "BET":
                    return _selectedBetNumber;
                default:
                    return _selectedLaserNumber;
            }
        }

        private string FormatSelectedHeadDeviceName(int number)
        {
            var normalizedNumber = Math.Clamp(number, 0, LaserHeadCount - 1);
            var name = $"H{normalizedNumber + 1:00}";
            return _enabledHeadDeviceNumbers.Contains(normalizedNumber) ? name : $"{name} NOT USE";
        }

        private IReadOnlyList<int> LoadEnabledHeadDeviceNumbers(CancellationToken cancellationToken)
        {
            try
            {
                var settings = _settingManager.LoadSection(EN_SETTING_TAB.Option, cancellationToken);
                var enabledHeadNos = CScannerStatusSnapshotReader.BuildEnabledHeadNumbers(settings, LaserHeadCount);
                var numbers = new List<int>(enabledHeadNos.Count);
                foreach (var headNo in enabledHeadNos)
                {
                    numbers.Add(headNo - 1);
                }

                return numbers;
            }
            catch (Exception exception)when (exception is InvalidDataException or IOException)
            {
                return[0, 1, 2, 3, 4, 5, 6, 7];
            }
        }

        private void NormalizeHeadDeviceSelections()
        {
            _selectedLaserNumber = NormalizeHeadDeviceNumber(_selectedLaserNumber);
            _selectedAttenuatorNumber = NormalizeHeadDeviceNumber(_selectedAttenuatorNumber);
            _selectedBetNumber = NormalizeHeadDeviceNumber(_selectedBetNumber);
        }

        private int NormalizeHeadDeviceNumber(int number)
        {
            var normalizedNumber = Math.Clamp(number, 0, LaserHeadCount - 1);
            return _enabledHeadDeviceNumbers.Count == 0 || _enabledHeadDeviceNumbers.Contains(normalizedNumber) ? normalizedNumber : _enabledHeadDeviceNumbers[0];
        }

        private ST_INTERFACE_DATA? GetSelectedHeadInterfaceData(string tab)
        {
            switch (NormalizeMonitorTab(tab))
            {
                case "LASER":
                    return _interfaceManager.GetInterfaceData(EN_EQP_MODULE.TalonLaser, _selectedLaserNumber);
                case "ATTENUATOR":
                    return _interfaceManager.GetInterfaceData(EN_EQP_MODULE.Attenuator, _selectedAttenuatorNumber);
                case "POWER METER":
                    return _interfaceManager.GetInterfaceData(EN_EQP_MODULE.PowerMeter, _selectedPowerMeterNumber);
                case "BET":
                    return _interfaceManager.GetInterfaceData(EN_EQP_MODULE.Bet, _selectedBetNumber);
                default:
                    return null;
            }
        }

        private string? GetSelectedHeadConnectionText(string tab)
        {
            if (NormalizeMonitorTab(tab) == "POWER METER")
            {
                return GetSelectedPowerMeterConnectionText();
            }

            var module = GetHeadDeviceModule(tab);
            if (module is null)
            {
                return null;
            }

            var number = GetSelectedHeadNumber(tab);
            bool MatchesItem(global::Drilling.Common.Interface.ST_INTERFACE_COMM_STATUS item)
            {
                return item.Number == number;
            }

            var status = _interfaceManager.GetInterfaceCommunicationList(module.Value).FirstOrDefault(MatchesItem);
            return status is null ? "OFFLINE" : ToCommunicationText(status.ConnectionState);
        }

        private static EN_EQP_MODULE? GetHeadDeviceModule(string tab)
        {
            switch (NormalizeMonitorTab(tab))
            {
                case "LASER":
                    return EN_EQP_MODULE.TalonLaser;
                case "ATTENUATOR":
                case "POWER METER":
                    return EN_EQP_MODULE.Attenuator;
                case "BET":
                    return EN_EQP_MODULE.Bet;
                default:
                    return null;
            }
        }

        private static bool IsHeadDeviceTab(string tab)
        {
            return NormalizeMonitorTab(tab)is "LASER" or "ATTENUATOR" or "BET" or "POWER METER";
        }

        private static string GetHeadDeviceNickPrefix(string tab)
        {
            switch (NormalizeMonitorTab(tab))
            {
                case "ATTENUATOR":
                case "POWER METER":
                    return "ATT";
                case "BET":
                    return "BET";
                default:
                    return "TALON";
            }
        }

        private IReadOnlyList<ST_MONITOR_HEAD_SELECT_ROW> CreateHeadSelectRows(string tab, int selectedHeadNumber, EN_EQP_MODULE? module, string nickPrefix)
        {
            if (module is null)
            {
                return[];
            }

            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_COMM_STATUS status)
            {
                return status.Number;
            }

            global::Drilling.Common.Interface.ST_INTERFACE_COMM_STATUS SelectStatus(global::Drilling.Common.Interface.ST_INTERFACE_COMM_STATUS status)
            {
                return status;
            }

            var communicationMap = _interfaceManager.GetInterfaceCommunicationList(module.Value).ToDictionary(SelectNumber, SelectStatus);
            global::Drilling.UI.Menu.Menus.ST_MONITOR_HEAD_SELECT_ROW BuildMonitor(int number)
            {
                var isEnabled = _enabledHeadDeviceNumbers.Contains(number);
                var registered = communicationMap.TryGetValue(number, out var status);
                var state = isEnabled ? registered ? ToCommunicationText(status!.ConnectionState) : "OFFLINE" : "NOT USE";
                var nickName = registered && !string.IsNullOrWhiteSpace(status!.NickName) ? status.NickName : $"{nickPrefix}_{number}";
                return new ST_MONITOR_HEAD_SELECT_ROW(number, $"H{number + 1:00}", nickName, state, registered, isEnabled && number == selectedHeadNumber, isEnabled);
            }

            return Enumerable.Range(0, LaserHeadCount).Select(BuildMonitor).ToArray();
        }

        private static string GetMonitorTabDisplayName(string tab)
        {
            return tab switch
            {
                "LASER" => "LASER/POWERMETER",
                "POWER METER" => "POWERMETER",
                "XPS" => "SCANNER TILT",
                "PICO MOTOR" => "DOE TILT",
                "COORDINATE VIEWER" => "COORDINATE VIEWER",
                _ => tab
            };
        }

        private static IReadOnlyList<ST_MONITOR_TREND_POINT> CreateTrendPoints(string tab)
        {
            return tab switch
            {
                "POWER METER" => [new("09:54", 126, 130, 134), new("10:00", 124, 129, 132), new("10:06", 128, 131, 136), new("10:12", 122, 128, 131), new("10:18", 125, 130, 135), new("10:24", 127, 132, 138)],
                _ => [new("09:54", 112, 134, 0), new("10:00", 110, 132, 0), new("10:06", 108, 134, 0), new("10:12", 106, 132, 0), new("10:18", 108, 133, 0), new("10:24", 107, 132, 0)]
            };
        }

        private static IReadOnlyList<ST_MONITOR_SUMMARY_ITEM> CreateSummaryItems(string tab, ST_DEVICE_STATUS snapshot)
        {
            return tab switch
            {
                "LASER" => [new("Output Power", snapshot.Laser.OutputPower.ToString("F3"), "W", "Accent"), new("Temperature", "24.6", "C", "Warn")],
                "ATTENUATOR" => [
                    new("Cur Pos", snapshot.Attenuator.CurrentPosition.ToString("F3", CultureInfo.InvariantCulture), "DEG", "Accent"), 
                    new("Target", snapshot.Attenuator.TargetPosition.ToString("F3", CultureInfo.InvariantCulture), "DEG", "Warn"), 
                    new("State", snapshot.Attenuator.CommandState, "", IsAttenuatorMoving(snapshot.Attenuator.CommandState) ? "Warn" : "Ok"), 
                    new("Home Type", FormatHomeType(snapshot.Attenuator.HomeType), "", FormatKnownState(snapshot.Attenuator.HomeType) == "OK" ? "Ok" : "Warn"), 
                    new("Neg Limit", FormatNullableDouble(snapshot.Attenuator.NegativeSoftwareLimit), "DEG", FormatKnownState(snapshot.Attenuator.NegativeSoftwareLimit) == "OK" ? "Ok" : "Warn"), 
                    new("Pos Limit", FormatNullableDouble(snapshot.Attenuator.PositiveSoftwareLimit), "DEG", FormatKnownState(snapshot.Attenuator.PositiveSoftwareLimit) == "OK" ? "Ok" : "Warn"), 
                    new("I Gain", FormatNullableDouble(snapshot.Attenuator.IGain), "", FormatKnownState(snapshot.Attenuator.IGain) == "OK" ? "Ok" : "Warn"), 
                    new("P Gain", FormatNullableDouble(snapshot.Attenuator.PGain), "", FormatKnownState(snapshot.Attenuator.PGain) == "OK" ? "Ok" : "Warn"), 
                    new("Interpolation", FormatNullableDouble(snapshot.Attenuator.InterpolationFactor), "", FormatKnownState(snapshot.Attenuator.InterpolationFactor) == "OK" ? "Ok" : "Warn")
                ],
                "BET" => [
                    new("MAG Pos", FormatBETStep(snapshot.Bet.CurrentMagnification), "step", "Accent"), 
                    new("DIV Pos", FormatBETStep(snapshot.Bet.CurrentDivergence), "step", "Accent"), 
                    new("State", snapshot.Bet.IsMoving ? "MOVING" : "IDLE", "", snapshot.Bet.IsMoving ? "Warn" : "Ok"), 
                    new("Alarm", snapshot.Bet.AlarmOn ? "OCCUR" : "CLEAR", "", snapshot.Bet.AlarmOn ? "Warn" : "Ok")
                ],
                "POWER METER" => [
                    new("Current Power", snapshot.PowerMeter.MeasuredPower.ToString("F4", CultureInfo.InvariantCulture), snapshot.PowerMeter.Unit, "Accent"), 
                    new("Average", snapshot.PowerMeter.AveragePower.ToString("F4", CultureInfo.InvariantCulture), snapshot.PowerMeter.Unit, "Ok"), 
                    new("Max", snapshot.PowerMeter.MaxPower.ToString("F4", CultureInfo.InvariantCulture), snapshot.PowerMeter.Unit, "Warn"), 
                    new("Min", snapshot.PowerMeter.MinPower.ToString("F4", CultureInfo.InvariantCulture), snapshot.PowerMeter.Unit, "Accent")
                ],
                _ => []
            };
        }

        private static string ResultTone(string result)
        {
            return result.Trim().ToUpperInvariant() switch
            {
                "OK" => "Ok",
                "NG" => "Error",
                _ => "Warn"
            };
        }

        private static string FormatProductDouble(double? value)
        {
            return value?.ToString("G", CultureInfo.InvariantCulture) ?? "-";
        }

        private static string OnOffText(bool value)
        {
            return value ? "ON" : "OFF";
        }

        private static string? NormalizeOperationFieldValue(string? value, IReadOnlyList<string>? valueOptions)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (valueOptions is null || valueOptions.Count == 0)
            {
                return value;
            }

            bool MatchesItem(string option)
            {
                return option.Equals(value, StringComparison.OrdinalIgnoreCase);
            }

            if (valueOptions.Any(MatchesItem))
            {
                return value;
            }

            var numericValue = NormalizeOperationNumericValue(value);
            bool MatchesItem2(string option)
            {
                return NormalizeOperationNumericValue(option).Equals(numericValue, StringComparison.OrdinalIgnoreCase);
            }

            return valueOptions.FirstOrDefault(MatchesItem2);
        }

        private static string NormalizeOperationNumericValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            var trimmed = value.Trim();
            var separatorIndex = trimmed.IndexOf(':', StringComparison.Ordinal);
            return separatorIndex > 0 ? trimmed[..separatorIndex].Trim() : trimmed;
        }

#endregion
#region Data
        private CScreenViewModel BuildLogViewerScreen()
        {
            const string selectedTab = "LOG VIEWER";
            _statusPollingService.SetActive(false);
            _lastLiveRefreshHash = null;
            var tabs = MonitorTabs.Select(tab => new ST_MONITOR_TAB(GetMonitorTabDisplayName(tab), tab == selectedTab)).ToArray();
            Apply(
                CreateTabSections(tabs), 
                selectedTab, 
                "MONITOR / LOG VIEWER", 
                "Select a log file from the folder tree", 
                "", 
                "", 
                "", 
                "", 
                tabs, 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                new PointCollection(), 
                PowerMeterGraphHeight / 2.0, 
                "0.0000 W", 
                "1.2000 W", 
                "TARGET POWER 1.2000(W)"
            );
            return new CScreenViewModel(EN_MENU.Monitor, Title, Subtitle, [], [], monitor: this);
        }

        private async Task<CScreenViewModel> BuildCoordinateViewerScreen(CancellationToken cancellationToken)
        {
            var selectedTab = "COORDINATE VIEWER";
            _lastLiveRefreshHash = null;
            global::Drilling.UI.Menu.Menus.ST_MONITOR_TAB BuildMonitor(string tab)
            {
                return new ST_MONITOR_TAB(GetMonitorTabDisplayName(tab), tab == selectedTab);
            }

            var tabs = MonitorTabs.Select(BuildMonitor).ToArray();
            var coordinateViewerData = await BuildCoordinateViewerData(cancellationToken);
            HeadSelectRows = [];
            LaserControlRows = [];
            MultiMoveAxisRows = [];
            MelsecGroups = [];
            SetMelsecRows([]);
            Apply(
                CreateTabSections(tabs), 
                selectedTab, 
                $"MONITOR / {GetMonitorTabDisplayName(selectedTab)}", 
                GetSubtitle(selectedTab), 
                GetStatusPanelTitle(selectedTab), 
                GetOperationPanelTitle(selectedTab), 
                GetParameterPanelTitle(selectedTab), 
                GetTrendPanelTitle(selectedTab), 
                tabs, 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                [], 
                new PointCollection(), 
                PowerMeterGraphHeight / 2.0, 
                "0.0000 W", 
                "1.2000 W", 
                "TARGET POWER 1.2000(W)"
            );
            ApplyCoordinateViewerData(coordinateViewerData);
            global::Drilling.UI.Menu.Menus.ST_DISPLAY_ITEM BuildDisplay(global::Drilling.UI.Menu.Menus.ST_MONITOR_COORDINATE_VALUE_ROW row)
            {
                return new ST_DISPLAY_ITEM(row.Parameter, row.Value, row.Unit);
            }

            return new CScreenViewModel(
                EN_MENU.Monitor, 
                Title, 
                Subtitle, 
                [new("Tab", selectedTab), new("Basis", CoordinateSelectedBasisName), new("Cell", CoordinateSelectedCellName)], 
                [new("COORDINATE VIEWER", CoordinateValueRows.Take(8).Select(BuildDisplay).ToArray())], 
                monitor: this
            );
        }

        private async Task SelectCoordinateBasis(object? parameter)
        {
            var basis = parameter switch
            {
                ST_MONITOR_COORDINATE_BASIS_OPTION option => option.Key,
                string text => text,
                _ => ""
            };
            var normalized = NormalizeCoordinateBasis(basis);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            _coordinateBasis = normalized;
            _coordinateSelectedShotKey = "";
            _setStatusMessage($"Coordinate viewer basis {GetCoordinateBasisName(normalized)} selected.");
            await _refreshCurrentScreen();
        }

        private async Task SelectCoordinateCell(object? parameter)
        {
            var cellNo = parameter switch
            {
                int value => value,
                string text when int.TryParse(text.Replace("CELL", "", StringComparison.OrdinalIgnoreCase), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
                _ => _coordinateSelectedCellNo
            };
            _coordinateSelectedCellNo = Math.Max(1, cellNo);
            _coordinateSelectedShotKey = "";
            _coordinateIsCellDetailVisible = true;
            _setStatusMessage($"Coordinate viewer Cell{_coordinateSelectedCellNo:000} selected.");
            await _refreshCurrentScreen();
        }

        private async Task SelectCoordinateShot(object? parameter)
        {
            if (NormalizeCoordinateBasis(_coordinateBasis) == "SCANNER")
            {
                _coordinateSelectedShotKey = "";
                return;
            }

            var shotKey = parameter switch
            {
                ST_MONITOR_COORDINATE_SHOT_BUTTON row => row.ShotKey,
                string text => text,
                _ => ""
            };
            if (string.IsNullOrWhiteSpace(shotKey))
            {
                return;
            }

            _coordinateSelectedShotKey = shotKey.Trim();
            _setStatusMessage($"Coordinate viewer {_coordinateSelectedShotKey} selected.");
            await _refreshCurrentScreen();
        }

        private async Task BackToCoordinateGlassPreview()
        {
            _coordinateIsCellDetailVisible = false;
            _coordinateSelectedCellNo = 0;
            _coordinateSelectedShotKey = "";
            _setStatusMessage("Coordinate viewer glass preview selected.");
            await _refreshCurrentScreen();
        }

        private async Task SelectMelsecGroup(object? parameter)
        {
            if (parameter is ST_MONITOR_MELSEC_GROUP group)
            {
                _selectedMelsecGroup = group.Name;
            }
            else if (parameter is string groupName && !string.IsNullOrWhiteSpace(groupName))
            {
                _selectedMelsecGroup = groupName.Trim().ToUpperInvariant();
            }
            else
            {
                return;
            }

            _setStatusMessage($"MELSEC group {_selectedMelsecGroup} selected.");
            await _refreshCurrentScreen();
        }

        private async Task WriteMelsec(object? parameter)
        {
            if (parameter is not ST_MONITOR_MELSEC_ROW row)
            {
                return;
            }

            if (!EnsureMonitorWriteCommandAllowed("MELSEC WRITE"))
            {
                return;
            }

            if (!_isMelsecWriteUnlocked)
            {
                const string message = "MELSEC WRITE LOCK 상태입니다.\n관리자에게 문의하세요.";
                _setStatusMessage(message.Replace(Environment.NewLine, " "));
                CRecipeConfirmDialog.ShowWarning("MELSEC WRITE LOCK", message);
                return;
            }

            var validationError = ValidateMelsecWrite(row);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                _setStatusMessage(validationError);
                return;
            }

            if (!CRecipeConfirmDialog.ShowConfirmation("MELSEC 쓰기", $"{row.Id} 주소에 {row.WriteValue} 값을 쓰시겠습니까?"))
            {
                return;
            }

            ST_DEVICE_COMMAND_RESULT ExecuteMelsecWriteCallback()
            {
                return ExecuteMelsecWrite(row);
            }

            var result = await Task.Run(ExecuteMelsecWriteCallback);
            if (result.IsSuccess)
            {
                RefreshMelsecCacheDisplay();
            }

            _setStatusMessage(result.Message);
            _refreshShellStatus();
        }

        private void ToggleMelsecWriteLock()
        {
            if (!IsMelsec || !_isMelsecWriteLockVisible)
            {
                return;
            }

            if (!EnsureMonitorWriteCommandAllowed("MELSEC WRITE LOCK"))
            {
                return;
            }

            _isMelsecWriteUnlocked = !_isMelsecWriteUnlocked;
            _setStatusMessage(_isMelsecWriteUnlocked ? "MELSEC WRITE unlocked." : "MELSEC WRITE locked.");
            NotifyMonitorLiveProperties(nameof(IsMelsecWriteUnlocked), nameof(MelsecWriteLockText));
        }

        private void ResetMelsecWriteLock()
        {
            _isMelsecWriteUnlocked = false;
            _isMelsecWriteLockVisible = false;
            NotifyMonitorLiveProperties(nameof(IsMelsecWriteUnlocked), nameof(IsMelsecWriteLockVisible), nameof(MelsecWriteLockText));
        }

        private static string? ValidateMelsecWrite(ST_MONITOR_MELSEC_ROW row)
        {
            if (!row.CanWrite)
            {
                return $"MELSEC WRITE is blocked by ACCESS={row.Access}: {row.Id}";
            }

            var value = row.WriteValue.Trim();
            if (string.IsNullOrWhiteSpace(value) && !row.DataType.Equals("STRING", StringComparison.OrdinalIgnoreCase))
            {
                return $"MELSEC WRITE value is empty: {row.Id}";
            }

            try
            {
                switch (row.DataType)
                {
                    case "BIT":
                        _ = ParseMelsecBit(value);
                        break;
                    case "WORD":
                    case "DWORD":
                        _ = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                        break;
                    case "DOUBLE":
                    case "FLOAT":
                        _ = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                        break;
                    case "STRING":
                        break;
                    default:
                        return $"Unsupported MELSEC write type: {row.DataType}";
                }
            }
            catch (Exception ex)when (ex is FormatException or OverflowException)
            {
                return $"MELSEC WRITE {row.Id} value is invalid. {ex.Message}";
            }

            return null;
        }

        private ST_DEVICE_COMMAND_RESULT ExecuteMelsecWrite(ST_MONITOR_MELSEC_ROW row)
        {
            try
            {
                if (!row.CanWrite)
                {
                    return new ST_DEVICE_COMMAND_RESULT(false, $"MELSEC WRITE is blocked by ACCESS={row.Access}: {row.Id}");
                }

                var value = row.WriteValue.Trim();
                _melsecWriteValues[row.Id] = value;
                switch (row.DataType)
                {
                    case "BIT":
                        _interfaceManager.Melsec.WriteBit(row.Id, ParseMelsecBit(value));
                        break;
                    case "WORD":
                    case "DWORD":
                        _interfaceManager.Melsec.WriteWord(row.Id, double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));
                        break;
                    case "DOUBLE":
                    case "FLOAT":
                        _interfaceManager.Melsec.WriteDouble(row.Id, double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));
                        if (row.Id.StartsWith("IOF_POS_ARRAY_", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(row.Id[14..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                            _interfaceManager.Melsec.ReadDouble(row.Id); // One requested coordinate; update last-read cache, no array poll.
                        break;
                    case "STRING":
                        _interfaceManager.Melsec.WriteString(row.Id, value);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported MELSEC write type: {row.DataType}");
                }

                return new ST_DEVICE_COMMAND_RESULT(true, $"MELSEC WRITE {row.Id}({row.Address}) = {value}");
            }
            catch (Exception ex)when (ex is InvalidOperationException or NotSupportedException or TimeoutException or IOException or FormatException or ArgumentOutOfRangeException or OverflowException)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"MELSEC WRITE {row.Id} failed. {ex.Message}");
            }
        }

        private void RefreshMelsecCacheDisplay()
        {
            _statusPollingService.RefreshMelsecNow();
            var values = _statusPollingService.GetSnapshot().MelsecValues;
            UpdateMelsecReadValues(values);
            NotifyMonitorLiveProperties(nameof(StatusRows));
        }

        private async Task<ST_MONITOR_COORDINATE_VIEWER_DATA> BuildCoordinateViewerData(CancellationToken cancellationToken)
        {
            var recipe = await LoadSelectedCoordinateRecipe(cancellationToken);
            var settings = new List<ST_SYSTEM_PARAMETER>();
            settings.AddRange(_settingManager.LoadSection(EN_SETTING_TAB.Option, cancellationToken));
            settings.AddRange(_settingManager.LoadSection(EN_SETTING_TAB.Motor, cancellationToken));
            var basis = NormalizeCoordinateBasis(_coordinateBasis);
            _coordinateBasis = string.IsNullOrWhiteSpace(basis) ? "DESIGN" : basis;
            if (recipe is null)
            {
                return new ST_MONITOR_COORDINATE_VIEWER_DATA(
                    CreateCoordinateBasisOptions(_coordinateBasis), 
                    [new("RECIPE", "SELECTED", "Recipe", "-", "", "No recipe is loaded.")], 
                    null, 
                    [], 
                    [], 
                    "0 Cells / 0 Shot Centers", 
                    "-", 
                    GetCoordinateBasisName(_coordinateBasis), 
                    GetCoordinateBasisDescription(_coordinateBasis)
                );
            }

            var parameterMap = BuildCoordinateParameterMap(recipe, settings);
            var recipeCellCount = Math.Max(CCellPatternCalculator.MinCellCount, ReadCoordinateInt(parameterMap, CCellPatternCalculator.MinCellCount, "MAX_CELL_NUMBER"));
            var canSelectCoordinateShot = _coordinateBasis != "SCANNER";
            if (_coordinateIsCellDetailVisible)
            {
                _coordinateSelectedCellNo = Math.Clamp(Math.Max(1, _coordinateSelectedCellNo), 1, recipeCellCount);
                if (!canSelectCoordinateShot)
                {
                    _coordinateSelectedShotKey = "";
                }
            }
            else
            {
                _coordinateSelectedCellNo = 0;
                _coordinateSelectedShotKey = "";
            }

            ST_SHOT_COORDINATE_PLAN? coordinatePlan = null;
            var valueRows = BuildCoordinateValueRows(recipe, settings, recipeCellCount, parameterMap).ToList();
            if (_coordinateIsCellDetailVisible)
            {
                try
                {
                    coordinatePlan = CShotCoordinatePlanBuilder.Build(parameterMap);
                }
                catch (Exception ex)
                {
                    valueRows.Insert(0, new ST_MONITOR_COORDINATE_VALUE_ROW("PLAN", "ERROR", "Coordinate Plan", ex.Message, "", "CShotCoordinatePlanBuilder.Build"));
                }
            }

            var cellCount = Math.Max(CCellPatternCalculator.MinCellCount, coordinatePlan?.CellCount ?? ReadCoordinateInt(parameterMap, CCellPatternCalculator.MinCellCount, "MAX_CELL_NUMBER"));
            if (_coordinateIsCellDetailVisible)
            {
                _coordinateSelectedCellNo = Math.Clamp(Math.Max(1, _coordinateSelectedCellNo), 1, cellCount);
                if (!canSelectCoordinateShot)
                {
                    _coordinateSelectedShotKey = "";
                }
            }
            else
            {
                _coordinateSelectedCellNo = 0;
                _coordinateSelectedShotKey = "";
            }

            bool MatchesCellNo(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.CellNo == _coordinateSelectedCellNo;
            }

            int SelectGroupRow(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.GroupRow;
            }

            int SelectGroupColumn(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.GroupColumn;
            }

            int SelectCellShotNo(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.CellShotNo;
            }

            ST_HEAD_SHOT_POINT? selectedShot = null;
            IReadOnlyList<ST_MONITOR_COORDINATE_SHOT_MATRIX_ROW> shotMatrixRows = [];
            if (_coordinateIsCellDetailVisible && coordinatePlan is not null)
            {
                var selectedCellShots = coordinatePlan.Shots.Where(MatchesCellNo).OrderBy(SelectGroupRow).ThenBy(SelectGroupColumn).ThenBy(SelectCellShotNo).ToArray();
                if (canSelectCoordinateShot)
                {
                    bool MatchesItem(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
                    {
                        return shot.ShotKey.Equals(_coordinateSelectedShotKey, StringComparison.OrdinalIgnoreCase);
                    }

                    if (selectedCellShots.Length == 0)
                    {
                        _coordinateSelectedShotKey = "";
                    }
                    else if (!string.IsNullOrWhiteSpace(_coordinateSelectedShotKey) && !selectedCellShots.Any(MatchesItem))
                    {
                        _coordinateSelectedShotKey = "";
                    }

                    bool MatchesItem2(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
                    {
                        return shot.ShotKey.Equals(_coordinateSelectedShotKey, StringComparison.OrdinalIgnoreCase);
                    }

                    selectedShot = coordinatePlan.Shots.FirstOrDefault(MatchesItem2);
                    if (selectedShot is not null)
                    {
                        InsertCoordinateSelectedShotRows(valueRows, BuildCoordinateSelectedShotRows(selectedShot, parameterMap));
                    }
                }
                else
                {
                    _coordinateSelectedShotKey = "";
                }

                shotMatrixRows = BuildCoordinateShotMatrixRows(coordinatePlan, parameterMap);
            }

            var previewPoints = Array.Empty<ST_REVIEW_PLAN_POINT>();
            var currentCellNo = _coordinateIsCellDetailVisible ? _coordinateSelectedCellNo : 0;
            var glassPreview = CReviewGlassPreviewBuilder.Build(recipe, cellCount, currentCellNo, previewPoints, axisIndicators: CreateCoordinateAxisIndicators(_coordinateBasis, parameterMap), expandSmallCellBounds: false);
            return new ST_MONITOR_COORDINATE_VIEWER_DATA(
                CreateCoordinateBasisOptions(_coordinateBasis), 
                valueRows, 
                glassPreview.Image, 
                glassPreview.CellLabels, 
                shotMatrixRows, 
                glassPreview.Summary, 
                $"{recipe.Id} / {recipe.Name}", 
                GetCoordinateBasisName(_coordinateBasis), 
                GetCoordinateBasisDescription(_coordinateBasis)
            );
        }

        private async Task<ST_RECIPE_DEFINITION?> LoadSelectedCoordinateRecipe(CancellationToken cancellationToken)
        {
            var recipes = await _recipeManager.LoadRecipes(cancellationToken);
            var selectedRecipeId = (_selectedRecipeIdProvider() ?? "").Trim();
            bool MatchesItem(global::Drilling.Common.Managers.ST_RECIPE_DEFINITION recipe)
            {
                return recipe.Id.Equals(selectedRecipeId, StringComparison.OrdinalIgnoreCase);
            }

            bool MatchesItem2(global::Drilling.Common.Managers.ST_RECIPE_DEFINITION recipe)
            {
                return recipe.Name.Equals(selectedRecipeId, StringComparison.OrdinalIgnoreCase);
            }

            return recipes.FirstOrDefault(MatchesItem) ?? recipes.FirstOrDefault(MatchesItem2) ?? recipes.FirstOrDefault();
        }

        private static Dictionary<string, string> BuildCoordinateParameterMap(ST_RECIPE_DEFINITION recipe, IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in recipe.Parameters)
            {
                SetCoordinateParameter(parameters, parameter.Key, parameter.Value);
                SetCoordinateParameter(parameters, parameter.Name, parameter.Value);
            }

            foreach (var setting in settings)
            {
                SetCoordinateParameter(parameters, setting.Key, setting.Value);
                SetCoordinateParameter(parameters, setting.Name, setting.Value);
            }

            parameters.TryAdd("HEAD_COUNT", "8");
            return parameters;
        }

        private static void SetCoordinateParameter(IDictionary<string, string> parameters, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            parameters[key.Trim()] = value;
        }

        private IReadOnlyList<ST_MONITOR_COORDINATE_VALUE_ROW> BuildCoordinateValueRows(ST_RECIPE_DEFINITION recipe, IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int cellCount, IReadOnlyDictionary<string, string> parameters)
        {
            var rows = new List<ST_MONITOR_COORDINATE_VALUE_ROW>
            {
                new("RECIPE", "SELECTED", "Recipe", $"{recipe.Id} / {recipe.Name}", "", "RECIPE_ID")
            };
            global::Drilling.Common.Managers.EN_SETTING_TAB SelectSection(global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER setting)
            {
                return setting.Section;
            }

            int SelectDisplayOrder(global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER setting)
            {
                return setting.DisplayOrder;
            }

            global::Drilling.UI.Menu.Menus.ST_MONITOR_COORDINATE_VALUE_ROW BuildMonitor(global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER setting)
            {
                return new ST_MONITOR_COORDINATE_VALUE_ROW(
                    "SETTING", 
                    string.IsNullOrWhiteSpace(setting.Group) ? "-" : setting.Group, 
                    GetCoordinateDisplayName(setting.Name, setting.Key), 
                    setting.Value, 
                    setting.Unit, 
                    GetCoordinateDisplayKey(setting.Key, setting.Name)
                );
            }

            rows.AddRange(settings.Where(IsCoordinateSetting).OrderBy(SelectSection).ThenBy(SelectDisplayOrder).Select(BuildMonitor));
            bool MatchesRecipe(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return IsCoordinateRecipeParameter(parameter, cellCount);
            }

            string SelectTab(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return parameter.Tab;
            }

            string SelectGroup(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return parameter.Group;
            }

            int SelectDisplayOrder2(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return parameter.DisplayOrder;
            }

            global::Drilling.UI.Menu.Menus.ST_MONITOR_COORDINATE_VALUE_ROW BuildMonitor2(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return new ST_MONITOR_COORDINATE_VALUE_ROW(
                    "RECIPE", 
                    string.IsNullOrWhiteSpace(parameter.Group) ? "-" : parameter.Group, 
                    GetCoordinateRecipeDisplayName(parameter.Name, parameter.Key), 
                    parameter.Value, 
                    parameter.Unit, 
                    GetCoordinateDisplayKey(parameter.Key, parameter.Name)
                );
            }

            rows.AddRange(recipe.Parameters.Where(MatchesRecipe).OrderBy(SelectTab).ThenBy(SelectGroup).ThenBy(SelectDisplayOrder2).Select(BuildMonitor2));
            rows.AddRange(BuildCoordinateCalculatedRows(parameters));
            return rows;
        }

        private static void InsertCoordinateSelectedShotRows(IList<ST_MONITOR_COORDINATE_VALUE_ROW> rows, IReadOnlyList<ST_MONITOR_COORDINATE_VALUE_ROW> selectedShotRows)
        {
            var insertIndex = rows.Count > 0 ? 1 : 0;
            for (var index = 0; index < selectedShotRows.Count; index++)
            {
                rows.Insert(insertIndex + index, selectedShotRows[index]);
            }
        }

        private IReadOnlyList<ST_MONITOR_COORDINATE_VALUE_ROW> BuildCoordinateSelectedShotRows(ST_HEAD_SHOT_POINT point, IReadOnlyDictionary<string, string> parameters)
        {
            var basis = NormalizeCoordinateBasis(_coordinateBasis);
            if (basis == "SCANNER")
            {
                return[
                    new("CALC", "SELECTED SHOT", "Head", point.HeadNo > 0 ? $"H{point.HeadNo:00}" : "-", "", "CALC_SELECTED_HEAD"), 
                    new("CALC", "SELECTED SHOT", "GX", FormatCoordinateValue(point.ScannerGx), "mm", "ST_HEAD_SHOT_POINT.ScannerGx"), 
                    new("CALC", "SELECTED SHOT", "GY", FormatCoordinateValue(point.ScannerGy), "mm", "ST_HEAD_SHOT_POINT.ScannerGy")
                ];
            }

            if (basis == "STAGE")
            {
                return[new("CALC", "SELECTED SHOT", "Stage X", FormatCoordinateValue(point.StageX), "mm", "ST_HEAD_SHOT_POINT.StageX"), new("CALC", "SELECTED SHOT", "Stage Y", FormatCoordinateValue(GetAbsoluteStageY(point, parameters)), "mm", "CALC_ABSOLUTE_STAGE_Y")];
            }

            if (basis == "REVIEW_CAMERA")
            {
                var reviewPosition = GetReviewCameraPosition(point, parameters);
                return[new("CALC", "SELECTED SHOT", "Review X", FormatCoordinateValue(reviewPosition.CameraX), "mm", "CALC_REVIEW_CAMERA_X"), new("CALC", "SELECTED SHOT", "Stage Y", FormatCoordinateValue(reviewPosition.StageY), "mm", "CALC_REVIEW_STAGE_Y")];
            }

            return[new("CALC", "SELECTED SHOT", "Design X", FormatCoordinateValue(GetOffsetAppliedDesignPosition(point).X), "mm", "CALC_DESIGN_X"), new("CALC", "SELECTED SHOT", "Design Y", FormatCoordinateValue(GetOffsetAppliedDesignPosition(point).Y), "mm", "CALC_DESIGN_Y")];
        }

        private IReadOnlyList<ST_MONITOR_COORDINATE_VALUE_ROW> BuildCoordinateCalculatedRows(IReadOnlyDictionary<string, string> parameters)
        {
            if (NormalizeCoordinateBasis(_coordinateBasis) != "STAGE")
            {
                return[];
            }

            var reviewCameraAlignKeyPosY = ReadCoordinateDouble(parameters, 0.0, "REVIEW_CAMERA_ALIGN_KEY_POS_Y");
            var reviewToHead1GapY = ReadCoordinateDouble(parameters, 0.0, "REVIEW_TO_HEAD1_GAP_Y");
            var headGapY = ReadCoordinateDouble(parameters, 0.0, "HeadGapY");
            var akMarginX = ReadCoordinateDouble(parameters, 0.0, "AK_MARGIN_X");
            var h01AkPositionX = ReadCoordinateDouble(parameters, 0.0, "H01_AK_POSITION_X");
            var head1AkStageY = reviewCameraAlignKeyPosY + reviewToHead1GapY;
            var evenHeadAkStageY = head1AkStageY + headGapY;
            var h01CenterX = akMarginX + h01AkPositionX;
            return[
                new("CALC", "STAGE", "H01/H03/H05/H07 AK Stage Y", FormatCoordinateValue(head1AkStageY), "mm", "REVIEW_CAMERA_ALIGN_KEY_POS_Y + REVIEW_TO_HEAD1_GAP_Y"), 
                new("CALC", "STAGE", "H02/H04/H06/H08 AK Stage Y", FormatCoordinateValue(evenHeadAkStageY), "mm", "HEAD1_AK_STAGE_Y + HeadGapY"), 
                new("CALC", "STAGE", "H01 Center X From AK", FormatCoordinateValue(h01CenterX), "mm", "AK_MARGIN_X + H01_AK_POSITION_X")
            ];
        }

        private IReadOnlyList<ST_MONITOR_COORDINATE_SHOT_MATRIX_ROW> BuildCoordinateShotMatrixRows(ST_SHOT_COORDINATE_PLAN coordinatePlan, IReadOnlyDictionary<string, string> parameters)
        {
            var shotMaskingPlan = CRecipeShotMaskingPlan.Build(parameters);
            bool MatchesCellNo(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.CellNo == _coordinateSelectedCellNo;
            }

            int SelectGroupRow(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.GroupRow;
            }

            int SelectKey(global::System.Linq.IGrouping<int, global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT> group)
            {
                return group.Key;
            }

            int SelectGroupColumn(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.GroupColumn;
            }

            int SelectCellShotNo(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.CellShotNo;
            }

            global::Drilling.UI.Menu.Menus.ST_MONITOR_COORDINATE_SHOT_BUTTON BuildMonitor(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                var maskingState = shotMaskingPlan.EvaluateShot(shot);
                return new ST_MONITOR_COORDINATE_SHOT_BUTTON(
                    shot.ShotKey, 
                    CReviewShotNameFormatter.ToMatrixName(shot.CellShotNo, shot.GroupCountX), 
                    shot.HeadNo > 0 ? $"H{shot.HeadNo:00}" : "-", 
                    CreateCoordinateShotButtonDetail(shot, parameters), 
                    shot.ShotKey.Equals(_coordinateSelectedShotKey, StringComparison.OrdinalIgnoreCase), 
                    maskingState.IsChessSkipped, 
                    maskingState.IsMasked, 
                    SelectCoordinateShotCommand
                );
            }

            global::Drilling.UI.Menu.Menus.ST_MONITOR_COORDINATE_SHOT_MATRIX_ROW BuildMonitor2(global::System.Linq.IGrouping<int, global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT> group)
            {
                return new ST_MONITOR_COORDINATE_SHOT_MATRIX_ROW(group.Key + 1, group.OrderBy(SelectGroupColumn).ThenBy(SelectCellShotNo).Select(BuildMonitor).ToArray());
            }

            return coordinatePlan.Shots.Where(MatchesCellNo).GroupBy(SelectGroupRow).OrderBy(SelectKey).Select(BuildMonitor2).ToArray();
        }

        private string CreateCoordinateShotButtonDetail(ST_HEAD_SHOT_POINT point, IReadOnlyDictionary<string, string> parameters)
        {
            var headName = point.HeadNo > 0 ? $"H{point.HeadNo:00}" : "-";
            if (NormalizeCoordinateBasis(_coordinateBasis) == "STAGE")
            {
                var stageY = GetAbsoluteStageY(point, parameters);
                return $"{headName}\nStage Y\n{FormatCoordinateValue(stageY)}";
            }

            if (NormalizeCoordinateBasis(_coordinateBasis) == "SCANNER")
            {
                var scannerPosition = GetViewerScannerPosition(point);
                return $"{headName}\nGX {FormatCoordinateValue(scannerPosition.Gx)}\nGY {FormatCoordinateValue(scannerPosition.Gy)}";
            }

            if (NormalizeCoordinateBasis(_coordinateBasis) == "REVIEW_CAMERA")
            {
                var reviewPosition = GetReviewCameraPosition(point, parameters);
                return $"{headName}\nReview X {FormatCoordinateValue(reviewPosition.CameraX)}\nStage Y {FormatCoordinateValue(reviewPosition.StageY)}";
            }

            var basisPosition = GetCoordinateBasisPosition(_coordinateBasis, point);
            return $"{headName}\nX {FormatCoordinateValue(basisPosition.X)}\nY {FormatCoordinateValue(basisPosition.Y)}";
        }

        private void ApplyCoordinateViewerData(ST_MONITOR_COORDINATE_VIEWER_DATA data)
        {
            CoordinateBasisOptions = data.BasisOptions;
            CoordinateValueRows = data.ValueRows;
            CoordinateGlassPreviewImage = data.GlassPreviewImage;
            CoordinateCellPreviewLabels = data.CellPreviewLabels;
            CoordinateShotMatrixRows = data.ShotMatrixRows;
            CoordinateGlassPreviewSummary = data.GlassPreviewSummary;
            CoordinateSelectedRecipeName = data.RecipeName;
            CoordinateSelectedBasisName = data.BasisName;
            CoordinateBasisDescription = data.BasisDescription;
            if (SelectedTab == "COORDINATE VIEWER")
            {
                NotifyCoordinateViewerProperties();
            }
        }

        private void NotifyCoordinateViewerProperties()
        {
            NotifyMonitorLiveProperties(
                nameof(CoordinateBasisOptions), 
                nameof(CoordinateValueRows), 
                nameof(CoordinateGlassPreviewImage), 
                nameof(CoordinateCellPreviewLabels), 
                nameof(CoordinateShotMatrixRows), 
                nameof(CoordinateGlassPreviewSummary), 
                nameof(CoordinateIsGlassPreviewVisible), 
                nameof(CoordinateIsCellDetailVisible), 
                nameof(CoordinateSelectedRecipeName), 
                nameof(CoordinateSelectedBasisName), 
                nameof(CoordinateBasisDescription), 
                nameof(CoordinateSelectedCellName), 
                nameof(CoordinateSelectedShotName)
            );
        }

        private static IReadOnlyList<ST_MONITOR_COORDINATE_BASIS_OPTION> CreateCoordinateBasisOptions(string selectedBasis)
        {
            return[
                CreateCoordinateBasisOption("DESIGN", selectedBasis), 
                CreateCoordinateBasisOption("STAGE", selectedBasis), 
                CreateCoordinateBasisOption("SCANNER", selectedBasis), 
                CreateCoordinateBasisOption("REVIEW_CAMERA", selectedBasis)
            ];
        }

        private static ST_MONITOR_COORDINATE_BASIS_OPTION CreateCoordinateBasisOption(string key, string selectedBasis)
        {
            return new ST_MONITOR_COORDINATE_BASIS_OPTION(key, GetCoordinateBasisName(key), GetCoordinateBasisDescription(key), key.Equals(selectedBasis, StringComparison.OrdinalIgnoreCase));
        }

        private static IReadOnlyList<ST_REVIEW_GLASS_AXIS_INDICATOR> CreateCoordinateAxisIndicators(string basis, IReadOnlyDictionary<string, string> parameters)
        {
            var normalizedBasis = NormalizeCoordinateBasis(basis);
            if (normalizedBasis == "DESIGN")
            {
                return[new ST_REVIEW_GLASS_AXIS_INDICATOR("X+", "Y+", true, true)];
            }

            if (normalizedBasis == "STAGE")
            {
                var stageScanDirectionY = ReadCoordinateDirection(parameters, -1.0, "STAGE_SCAN_DIRECTION_Y");
                return[new ST_REVIEW_GLASS_AXIS_INDICATOR("", "Y+", true, stageScanDirectionY > 0.0)];
            }

            if (normalizedBasis == "SCANNER")
            {
                return CreateScannerAxisIndicators(parameters);
            }

            if (normalizedBasis == "REVIEW_CAMERA")
            {
                return[new ST_REVIEW_GLASS_AXIS_INDICATOR("Review X+", "", true, true)];
            }

            return[];
        }

        private static IReadOnlyList<ST_REVIEW_GLASS_AXIS_INDICATOR> CreateScannerAxisIndicators(IReadOnlyDictionary<string, string> parameters)
        {
            var headCount = Math.Clamp(ReadCoordinateInt(parameters, 8, "HEAD_COUNT"), 1, 8);
            var groups = new List<ST_SCANNER_AXIS_INDICATOR_GROUP>();
            for (var headNo = 1; headNo <= headCount; headNo++)
            {
                if (!ReadCoordinateOnOff(parameters, true, $"H{headNo:00}_USE"))
                {
                    continue;
                }

                var transform = CScannerAxisTransform.Read(parameters, headNo);
                var groupIndex = FindScannerAxisIndicatorGroupIndex(groups, transform);
                if (groupIndex < 0)
                {
                    groups.Add(new ST_SCANNER_AXIS_INDICATOR_GROUP(transform, headNo));
                    continue;
                }

                groups[groupIndex].HeadNos.Add(headNo);
            }

            var indicators = new List<ST_REVIEW_GLASS_AXIS_INDICATOR>(groups.Count);
            var usedAnchors = new List<string>(groups.Count);
            for (var index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                indicators.Add(CreateScannerAxisIndicator(group.Transform, FormatScannerAxisIndicatorTitle(group.HeadNos), usedAnchors));
            }

            return indicators;
        }

        private static int FindScannerAxisIndicatorGroupIndex(IReadOnlyList<ST_SCANNER_AXIS_INDICATOR_GROUP> groups, ST_SCANNER_AXIS_TRANSFORM transform)
        {
            for (var index = 0; index < groups.Count; index++)
            {
                if (groups[index].Transform == transform)
                {
                    return index;
                }
            }

            return -1;
        }

        private static ST_REVIEW_GLASS_AXIS_INDICATOR CreateScannerAxisIndicator(ST_SCANNER_AXIS_TRANSFORM transform, string title, IList<string> usedAnchors)
        {
            var scannerXVector = transform.Apply(1.0, 0.0);
            var scannerYVector = transform.Apply(0.0, 1.0);
            var xLabel = Math.Abs(scannerXVector.X) >= Math.Abs(scannerXVector.Y) ? "GX+" : "GY+";
            var yLabel = Math.Abs(scannerYVector.Y) >= Math.Abs(scannerYVector.X) ? "GY+" : "GX+";
            var xPositiveRight = Math.Abs(scannerXVector.X) >= Math.Abs(scannerXVector.Y) ? scannerXVector.X >= 0.0 : scannerXVector.Y >= 0.0;
            var yPositiveDown = Math.Abs(scannerYVector.Y) >= Math.Abs(scannerYVector.X) ? scannerYVector.Y >= 0.0 : scannerYVector.X >= 0.0;
            var anchor = ReserveScannerAxisIndicatorAnchor(GetScannerAxisIndicatorAnchor(xPositiveRight, yPositiveDown), usedAnchors);
            return new ST_REVIEW_GLASS_AXIS_INDICATOR(xLabel, yLabel, xPositiveRight, yPositiveDown, anchor, title);
        }

        private static string GetScannerAxisIndicatorAnchor(bool xPositiveRight, bool yPositiveDown)
        {
            if (xPositiveRight && yPositiveDown)
            {
                return "TOP_LEFT";
            }

            if (!xPositiveRight && yPositiveDown)
            {
                return "TOP_RIGHT";
            }

            if (xPositiveRight)
            {
                return "BOTTOM_LEFT";
            }

            return "BOTTOM_RIGHT";
        }

        private static string ReserveScannerAxisIndicatorAnchor(string preferredAnchor, IList<string> usedAnchors)
        {
            if (!ContainsScannerAxisIndicatorAnchor(usedAnchors, preferredAnchor))
            {
                usedAnchors.Add(preferredAnchor);
                return preferredAnchor;
            }

            string[] anchors = ["TOP_LEFT", "TOP_RIGHT", "BOTTOM_LEFT", "BOTTOM_RIGHT"];
            for (var index = 0; index < anchors.Length; index++)
            {
                if (!ContainsScannerAxisIndicatorAnchor(usedAnchors, anchors[index]))
                {
                    usedAnchors.Add(anchors[index]);
                    return anchors[index];
                }
            }

            usedAnchors.Add(preferredAnchor);
            return preferredAnchor;
        }

        private static bool ContainsScannerAxisIndicatorAnchor(IList<string> usedAnchors, string anchor)
        {
            for (var index = 0; index < usedAnchors.Count; index++)
            {
                if (usedAnchors[index].Equals(anchor, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FormatScannerAxisIndicatorTitle(IReadOnlyList<int> headNos)
        {
            if (headNos.Count == 0)
            {
                return "-";
            }

            var labels = new List<string>(headNos.Count);
            for (var index = 0; index < headNos.Count; index++)
            {
                labels.Add(index == 0 ? $"H{headNos[index]:00}" : $"{headNos[index]:00}");
            }

            return string.Join("/", labels);
        }

        private bool IsCoordinateSetting(ST_SYSTEM_PARAMETER setting)
        {
            var key = NormalizeCoordinateKey(GetCoordinateDisplayKey(setting.Key, setting.Name));
            if (IsCoordinateOffsetKey(key))
            {
                return false;
            }

            var basis = NormalizeCoordinateBasis(_coordinateBasis);
            if (basis == "STAGE")
            {
                return key.Contains("STAGESTARTPOS", StringComparison.OrdinalIgnoreCase) || key.Contains("STAGESCANDIRECTION", StringComparison.OrdinalIgnoreCase) || key.Contains("HEADGAP", StringComparison.OrdinalIgnoreCase) || key.Contains("REVIEWTOHEAD", StringComparison.OrdinalIgnoreCase) || key.Contains("AKPOSITION", StringComparison.OrdinalIgnoreCase);
            }

            if (basis == "SCANNER")
            {
                return key.Contains("HEADGAP", StringComparison.OrdinalIgnoreCase) || key.Contains("REVIEWTOHEAD", StringComparison.OrdinalIgnoreCase) || key.Contains("AKPOSITION", StringComparison.OrdinalIgnoreCase) || key.Contains("SCANENCODER", StringComparison.OrdinalIgnoreCase) || key.Contains("STAGESCANDIRECTION", StringComparison.OrdinalIgnoreCase) || key.Contains("SCANNERXYFLIP", StringComparison.OrdinalIgnoreCase) || key.Contains("SCANNERXFLIP", StringComparison.OrdinalIgnoreCase) || key.Contains("SCANNERYFLIP", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private bool IsCoordinateRecipeParameter(ST_RECIPE_PARAMETER parameter, int cellCount)
        {
            var key = NormalizeCoordinateKey(GetCoordinateDisplayKey(parameter.Key, parameter.Name));
            var basis = NormalizeCoordinateBasis(_coordinateBasis);
            if (IsCoordinateOffsetKey(key))
            {
                return basis == "DESIGN" && _coordinateIsCellDetailVisible && IsSelectedCoordinateCellParameter(key);
            }

            if (key is "AKMARGINX" or "AKMARGINY")
            {
                return basis is "DESIGN" or "STAGE" or "SCANNER" or "REVIEW_CAMERA";
            }

            if (key is "ESCEDGETOGLASSEDGE" or "GLASSCENTERTOSCANNERCENTER")
            {
                return basis is "STAGE" or "SCANNER" or "REVIEW_CAMERA";
            }

            if (key is "HEADCOUNT" or "CELLCOUNT" or "GLASSSIZEX" or "GLASSSIZEY")
            {
                return basis is "DESIGN" or "SCANNER" or "REVIEW_CAMERA";
            }

            if (key is "REVIEWCAMERAALIGNKEYPOSX" or "REVIEWCAMERAALIGNKEYPOSY")
            {
                return basis is "STAGE" or "REVIEW_CAMERA";
            }

            if (key.StartsWith("DISTORTIONKEY", StringComparison.OrdinalIgnoreCase))
            {
                return basis is "DESIGN" or "REVIEW_CAMERA";
            }

            var isCellGeometry = TryGetCoordinateCellNo(key, out var cellNo) && cellNo >= 1 && cellNo <= cellCount && (!_coordinateIsCellDetailVisible || cellNo == _coordinateSelectedCellNo) && (key.Contains("ALIGNTO1STPIXEL", StringComparison.OrdinalIgnoreCase) || key.Contains("HOLECOUNT", StringComparison.OrdinalIgnoreCase) || key.Contains("NUMOFPIXEL", StringComparison.OrdinalIgnoreCase) || key.Contains("PITCH", StringComparison.OrdinalIgnoreCase) || key.Contains("PIXELSIZE", StringComparison.OrdinalIgnoreCase) || key.Contains("ROTATION", StringComparison.OrdinalIgnoreCase));
            return isCellGeometry && basis is "DESIGN" or "SCANNER" or "REVIEW_CAMERA";
        }

        private bool IsSelectedCoordinateCellParameter(string normalizedKey)
        {
            return TryGetCoordinateCellNo(normalizedKey, out var cellNo) && cellNo == _coordinateSelectedCellNo;
        }

        private static bool IsCoordinateOffsetKey(string normalizedKey)
        {
            return normalizedKey.Contains("OFFSET", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetCoordinateDisplayName(string name, string key)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }

            return string.IsNullOrWhiteSpace(key) ? "-" : key.Trim();
        }

        private static string GetCoordinateRecipeDisplayName(string name, string key)
        {
            var displayKey = GetCoordinateDisplayKey(key, name);
            if (NormalizeCoordinateKey(displayKey).StartsWith("CELL", StringComparison.OrdinalIgnoreCase))
            {
                return displayKey;
            }

            return GetCoordinateDisplayName(name, key);
        }

        private static string GetCoordinateDisplayKey(string key, string name)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key.Trim();
            }

            return string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
        }

        private static string NormalizeCoordinateKey(string key)
        {
            return key.Replace("_", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "", StringComparison.OrdinalIgnoreCase).Replace("-", "", StringComparison.OrdinalIgnoreCase).Trim().ToUpperInvariant();
        }

        private static bool TryGetCoordinateCellNo(string normalizedKey, out int cellNo)
        {
            cellNo = 0;
            if (!normalizedKey.StartsWith("CELL", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var start = 4;
            var end = start;
            while (end < normalizedKey.Length && char.IsDigit(normalizedKey[end]))
            {
                end++;
            }

            return end > start && int.TryParse(normalizedKey[start..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out cellNo);
        }

        private static int ReadCoordinateInt(IReadOnlyDictionary<string, string> parameters, int defaultValue, string key)
        {
            return parameters.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }

        private static bool ReadCoordinateOnOff(IReadOnlyDictionary<string, string> parameters, bool defaultValue, string key)
        {
            if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            var normalized = value.Trim().ToUpperInvariant();
            if (normalized is "ON" or "TRUE" or "1" or "YES")
            {
                return true;
            }

            if (normalized is "OFF" or "FALSE" or "0" or "NO")
            {
                return false;
            }

            return defaultValue;
        }

        private static double ReadCoordinateDouble(IReadOnlyDictionary<string, string> parameters, double defaultValue, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (parameters.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }

            return defaultValue;
        }

        private static double ReadCoordinateDirection(IReadOnlyDictionary<string, string> parameters, double defaultValue, params string[] keys)
        {
            var value = ReadCoordinateDouble(parameters, defaultValue, keys);
            return value < 0.0 ? -1.0 : 1.0;
        }

        private static string NormalizeCoordinateBasis(string basis)
        {
            var normalized = (basis ?? "").Trim().Replace("_", " ", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
            return normalized switch
            {
                "DESIGN" => "DESIGN",
                "STAGE" => "STAGE",
                "HEAD" or "SCANNER" => "SCANNER",
                "CAMERA" or "VISION" or "REVIEW CAMERA" or "REVIEWCAMERA" => "REVIEW_CAMERA",
                _ => ""
            };
        }

        private static string GetCoordinateBasisName(string basis)
        {
            return NormalizeCoordinateBasis(basis) switch
            {
                "STAGE" => "Stage",
                "SCANNER" => "Scanner",
                "REVIEW_CAMERA" => "Review Camera",
                _ => "Design"
            };
        }

        private static string GetCoordinateBasisDescription(string basis)
        {
            return NormalizeCoordinateBasis(basis) switch
            {
                "STAGE" => "Stage coordinate used for process movement",
                "SCANNER" => "Head local scanner coordinate: GX / GY",
                "REVIEW_CAMERA" => "Review X and stage Y target coordinate",
                _ => "Recipe design coordinate from Align Key / Glass"
            };
        }

        private static (double X, double Y) GetCoordinateBasisPosition(string basis, ST_HEAD_SHOT_POINT point)
        {
            return NormalizeCoordinateBasis(basis) switch
            {
                "STAGE" => (point.StageX, point.StageY),
                "SCANNER" => GetViewerScannerPosition(point),
                "REVIEW_CAMERA" => (point.DesignX, point.DesignY),
                _ => GetOffsetAppliedDesignPosition(point)};
        }

        private static (double Gx, double Gy) GetViewerScannerPosition(ST_HEAD_SHOT_POINT point)
        {
            return (point.ScannerGx, point.ScannerGy);
        }

        private static (double CameraX, double StageY) GetReviewCameraPosition(ST_HEAD_SHOT_POINT point, IReadOnlyDictionary<string, string> parameters)
        {
            var reviewCameraAlignKeyPosX = ReadCoordinateDouble(parameters, 0.0, "REVIEW_CAMERA_ALIGN_KEY_POS_X");
            var reviewCameraAlignKeyPosY = ReadCoordinateDouble(parameters, 0.0, "REVIEW_CAMERA_ALIGN_KEY_POS_Y");
            var stageScanDirectionY = ReadCoordinateDirection(parameters, -1.0, "STAGE_SCAN_DIRECTION_Y");
            return (reviewCameraAlignKeyPosX + GetDesignXFromAlignKey(point, parameters), reviewCameraAlignKeyPosY + (stageScanDirectionY * GetDesignYFromAlignKey(point, parameters)));
        }

        private static (double X, double Y) GetOffsetAppliedDesignPosition(ST_HEAD_SHOT_POINT point)
        {
            return (point.DesignX + point.RecipeOffsetX + point.ReviewOffsetX, point.DesignY + point.RecipeOffsetY + point.ReviewOffsetY);
        }

        private static double GetAbsoluteStageY(ST_HEAD_SHOT_POINT point, IReadOnlyDictionary<string, string> parameters)
        {
            var stageScanDirectionY = ReadCoordinateDirection(parameters, -1.0, "STAGE_SCAN_DIRECTION_Y");
            return GetHeadAlignKeyStageY(point.HeadNo, parameters) + (stageScanDirectionY * GetDesignYFromAlignKey(point, parameters));
        }

        private static double GetHeadAlignKeyStageY(int headNo, IReadOnlyDictionary<string, string> parameters)
        {
            var reviewCameraAlignKeyPosY = ReadCoordinateDouble(parameters, 0.0, "REVIEW_CAMERA_ALIGN_KEY_POS_Y");
            var reviewToHead1GapY = ReadCoordinateDouble(parameters, 0.0, "REVIEW_TO_HEAD1_GAP_Y");
            var headGapY = ReadCoordinateDouble(parameters, 0.0, "HeadGapY");
            var head1AlignKeyStageY = reviewCameraAlignKeyPosY + reviewToHead1GapY;
            return headNo > 0 && headNo % 2 == 0 ? head1AlignKeyStageY + headGapY : head1AlignKeyStageY;
        }

        private static double GetDesignYFromAlignKey(ST_HEAD_SHOT_POINT point, IReadOnlyDictionary<string, string> parameters)
        {
            var akMarginY = ReadCoordinateDouble(parameters, 0.0, "AK_MARGIN_Y");
            return point.DesignY - akMarginY;
        }

        private static double GetDesignXFromAlignKey(ST_HEAD_SHOT_POINT point, IReadOnlyDictionary<string, string> parameters)
        {
            var akMarginX = ReadCoordinateDouble(parameters, 0.0, "AK_MARGIN_X");
            return point.DesignX - akMarginX;
        }

        private static string FormatCoordinateValue(double value)
        {
            return value.ToString("F4", CultureInfo.InvariantCulture);
        }

        private IReadOnlyList<ST_MONITOR_MELSEC_GROUP> CreateMelsecGroups(string tab)
        {
            if (tab != "MELSEC")
            {
                return[];
            }

            string SelectGroup(global::Drilling.Common.Interface.ST_MELSEC_MAP_DATA data)
            {
                return data.Group;
            }

            bool ShouldIncludeItem(string group)
            {
                return !string.IsNullOrWhiteSpace(group);
            }

            string SelectGroup2(string group)
            {
                return group;
            }

            var mapGroups = _interfaceManager.Melsec.Map.Select(SelectGroup).Where(ShouldIncludeItem).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(SelectGroup2, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!_selectedMelsecGroup.Equals("ALL", StringComparison.OrdinalIgnoreCase) && !mapGroups.Contains(_selectedMelsecGroup, StringComparer.OrdinalIgnoreCase))
            {
                _selectedMelsecGroup = "ALL";
            }

            global::Drilling.UI.Menu.Menus.ST_MONITOR_MELSEC_GROUP BuildMonitor(string group)
            {
                return new ST_MONITOR_MELSEC_GROUP(group, group.Equals(_selectedMelsecGroup, StringComparison.OrdinalIgnoreCase), group.Equals("ALL", StringComparison.OrdinalIgnoreCase) ? _interfaceManager.Melsec.Map.Count : _interfaceManager.Melsec.GetMapList(group).Count);
            }

            return new[]
            {
                "ALL"
            }.Concat(mapGroups).Select(BuildMonitor).ToArray();
        }

        private IReadOnlyList<ST_MONITOR_MELSEC_ROW> CreateMelsecRows(string tab, IReadOnlyDictionary<string, ST_MONITOR_MELSEC_VALUE> values)
        {
            if (tab != "MELSEC")
            {
                return[];
            }

            var rows = _selectedMelsecGroup.Equals("ALL", StringComparison.OrdinalIgnoreCase) ? _interfaceManager.Melsec.Map : _interfaceManager.Melsec.GetMapList(_selectedMelsecGroup);
            string SelectGroup(global::Drilling.Common.Interface.ST_MELSEC_MAP_DATA data)
            {
                return data.Group;
            }

            int SelectDeviceNo(global::Drilling.Common.Interface.ST_MELSEC_MAP_DATA data)
            {
                return data.DeviceNo;
            }

            string SelectId(global::Drilling.Common.Interface.ST_MELSEC_MAP_DATA data)
            {
                const string prefix = "IOF_POS_ARRAY_";
                return data.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(data.Id[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    ? prefix + index.ToString("D4", CultureInfo.InvariantCulture) : data.Id;
            }

            global::Drilling.UI.Menu.Menus.ST_MONITOR_MELSEC_ROW BuildMonitor(global::Drilling.Common.Interface.ST_MELSEC_MAP_DATA data)
            {
                values.TryGetValue(data.Id, out var value);
                return new ST_MONITOR_MELSEC_ROW(
                    data.Id, 
                    data.Group, 
                    data.Name, 
                    data.DeviceNo.ToString(CultureInfo.InvariantCulture), 
                    data.Address, 
                    data.DataType.ToString().ToUpperInvariant(), 
                    data.Direction.ToString().ToUpperInvariant(), 
                    MelsecAccessText(data.Access), 
                    data.ReadScale.ToString("G", CultureInfo.InvariantCulture), 
                    data.WriteScale.ToString("G", CultureInfo.InvariantCulture), 
                    data.Length.ToString(CultureInfo.InvariantCulture), 
                    data.PollMs <= 0 ? "-" : data.PollMs.ToString(CultureInfo.InvariantCulture), 
                    value?.Value ?? "-", 
                    MelsecWriteValue(data), 
                    value?.State ?? MelsecRowState(data), 
                    data.Description
                );
            }

            return rows.OrderBy(SelectGroup, StringComparer.OrdinalIgnoreCase).ThenBy(SelectDeviceNo).ThenBy(SelectId, StringComparer.OrdinalIgnoreCase).Select(BuildMonitor).ToArray();
        }

        private void SetMelsecRows(IReadOnlyList<ST_MONITOR_MELSEC_ROW> rows)
        {
            MelsecRows = rows;
            bool ShouldIncludeItem(global::Drilling.UI.Menu.Menus.ST_MONITOR_MELSEC_ROW row)
            {
                return row.CanRead && !row.CanWrite;
            }

            MelsecReadRows = rows.Where(ShouldIncludeItem).ToArray();
            bool IsCanWrite(global::Drilling.UI.Menu.Menus.ST_MONITOR_MELSEC_ROW row)
            {
                return row.CanWrite;
            }

            MelsecWriteRows = rows.Where(IsCanWrite).ToArray();
        }

        private void UpdateMelsecReadValues(IReadOnlyDictionary<string, ST_MONITOR_MELSEC_VALUE> values)
        {
            // Keep the row instances held by the grid and any open input dialog.
            // Feedback refreshes must not replace or overwrite pending write values.
            foreach (var row in MelsecRows)
            {
                if (values.TryGetValue(row.Id, out var value))
                {
                    row.UpdateReadValue(value.Value, value.State);
                }
                else if (row.CanRead)
                {
                    row.UpdateReadValue("-", "WAIT");
                }
            }
        }

        private void SaveMelsecWriteValues()
        {
            bool IsCanWrite(global::Drilling.UI.Menu.Menus.ST_MONITOR_MELSEC_ROW row)
            {
                return row.CanWrite;
            }

            foreach (var row in MelsecWriteRows.Count > 0 ? MelsecWriteRows : MelsecRows.Where(IsCanWrite))
            {
                _melsecWriteValues[row.Id] = row.WriteValue;
            }
        }

        private async Task<(ST_PRODUCT_DATA? Product, IReadOnlyList<ST_PRODUCT_HISTORY> History, string Error)> LoadProductDisplay(CancellationToken cancellationToken)
        {
            try
            {
                var product = _productManager.Current ?? await _productManager.LoadActive(cancellationToken);
                // Product history files are read off the UI thread, only when this screen is built.
                var history = await Task.Run(() => _productManager.LoadHistory(80, 14, cancellationToken), cancellationToken);
                return (product, history, "");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (null, [], ex.Message);
            }
        }

        private async Task CreateGlass()
        {
            if (!EnsureMonitorWriteCommandAllowed("Glass 생성"))
            {
                return;
            }

            try
            {
                var activeProduct = _productManager.Current ?? await _productManager.LoadActive(CancellationToken.None);
                if (activeProduct is not null)
                {
                    if (activeProduct.State != EN_PRODUCT_STATE.Completed)
                    {
                        MessageBox.Show(Application.Current?.MainWindow, "현재 Glass를 삭제한 후 새 Glass를 생성할 수 있습니다.", "Glass 생성", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                }

                var isSimulation = _interfaceManager.IsSimulation && _motionManager.IsSimulation;
                var glassIdPrefix = isSimulation ? "SIM" : "DUMMY";
                var glassId = $"{glassIdPrefix}_{DateTimeOffset.Now:yyyyMMdd_HHmmssfff}";
                await _productManager.CreateProduct("", glassId, "", "", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new Dictionary<int, int>(), CancellationToken.None);
                await _refreshCurrentScreen();
                MessageBox.Show(Application.Current?.MainWindow, $"Glass {glassId} 생성이 완료되었습니다.", "Glass 생성 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Application.Current?.MainWindow, $"Glass를 생성하지 못했습니다.\n{ex.Message}", "Glass 생성 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task DeleteGlass()
        {
            if (!EnsureMonitorWriteCommandAllowed("Glass 삭제"))
            {
                return;
            }

            try
            {
                var product = _productManager.Current ?? await _productManager.LoadActive(CancellationToken.None);
                if (product is null)
                {
                    MessageBox.Show(Application.Current?.MainWindow, "삭제할 Glass가 없습니다.", "Glass 삭제", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var glassId = string.IsNullOrWhiteSpace(product.PanelId) ? "현재 Glass" : $"Glass {product.PanelId}";
                if (!CRecipeConfirmDialog.ShowConfirmation("Glass 삭제", $"{glassId}를 삭제하시겠습니까?", "삭제", useDangerStyle: true))
                {
                    return;
                }

                await _productManager.DeleteProduct(product.GlassId, CancellationToken.None);
                await _refreshCurrentScreen();
                MessageBox.Show(Application.Current?.MainWindow, $"{glassId} 삭제가 완료되었습니다.", "Glass 삭제 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Application.Current?.MainWindow, $"Glass를 삭제하지 못했습니다.\n{ex.Message}", "Glass 삭제 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task<ST_RECIPE_DEFINITION?> LoadProductRecipeDefinition(string recipeId, CancellationToken cancellationToken)
        {
            bool MatchesItem(global::Drilling.Common.Managers.ST_RECIPE_DEFINITION recipe)
            {
                return recipe.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase) || recipe.Name.Equals(recipeId, StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                if (string.IsNullOrWhiteSpace(recipeId))
                {
                    return null;
                }

                var recipes = await _recipeManager.LoadRecipes(cancellationToken);
                return recipes.FirstOrDefault(MatchesItem);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static IReadOnlyList<ST_MONITOR_PRODUCT_ITEM> CreateProductItems(ST_PRODUCT_DATA? product, string error)
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                return[new("State", "DATA ERROR", "Error"), new("Message", error, "Error")];
            }

            if (product is null)
            {
                return[new("State", "NO ACTIVE PRODUCT", "Warn"), new("Result", "-", "Normal")];
            }

            return[
                new("Glass ID", product.PanelId, "Normal"), 
                new("Lot ID", product.LotId, "Normal"), 
                new("Process ID", product.ProcessId, "Normal"), 
                new("Recipe", product.RecipeId, "Accent"), 
                new("State", product.State.ToString().ToUpperInvariant(), ProductStateTone(product.State, product.Result)), 
                new("Result", product.Result.ToString().ToUpperInvariant(), ProductResultTone(product.Result)), 
                new("Created", FormatProductDateTime(product.CreatedAt), "Normal"), 
                new("Started", FormatProductDateTime(product.StartedAt), "Normal"), 
                new("Completed", FormatProductDateTime(product.CompletedAt), "Normal")
            ];
        }

        private void ApplyProductDetails(ST_PRODUCT_DATA? product, string error, ST_RECIPE_DEFINITION? recipeDefinition)
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                var errorItems = CreateProductDataErrorItems(error);
                ProductGlassItems = errorItems;
                ProductAlignItems = errorItems;
                ProductDistortionItems = errorItems;
                ProductProcessItems = errorItems;
                ProductRecipeItems = errorItems;
                ProductReviewItems = [new("State", "DATA ERROR", "Error"), new("Recovery", "CHECK DATA", "Error"), new("Message", error, "Error")];
                ProductReviewRows = [];
                return;
            }

            if (product is null)
            {
                var emptyItems = new ST_MONITOR_PRODUCT_ITEM[]
                {
                    new("State", "NO ACTIVE PRODUCT", "Warn")
                };
                ProductGlassItems = emptyItems;
                ProductAlignItems = emptyItems;
                ProductDistortionItems = emptyItems;
                ProductProcessItems = emptyItems;
                ProductRecipeItems = emptyItems;
                ProductReviewItems = emptyItems;
                ProductReviewRows = [];
                return;
            }

            string ProductValue(string name)
            {
                var value = product.Product.Get(name);
                return string.IsNullOrWhiteSpace(value) ? "-" : value;
            }

            ProductGlassItems = [
                new("H Glass ID", product.PanelId, "Normal"), 
                new("E Glass ID", ProductValue("E_GLASSID"), "Normal"), 
                new("Lot ID", product.LotId, "Normal"), 
                new("Batch ID", ProductValue("BATCHID"), "Normal"), 
                new("Job ID", ProductValue("JOBID"), "Normal"), 
                new("Port ID", ProductValue("PORTID"), "Normal"), 
                new("Slot No", ProductValue("SLOTNO"), "Normal"), 
                new("PPID", product.RecipeId, "Accent"), 
                new("Process ID", product.ProcessId, "Normal"), 
                new("Glass State", ProductValue("GLASS_STATE"), "Normal"), 
                new("Created", FormatProductDateTime(product.CreatedAt), "Normal"), 
                new("Started", FormatProductDateTime(product.StartedAt), "Normal"), 
                new("Completed", FormatProductDateTime(product.CompletedAt), "Normal")
            ];
            ProductAlignItems = [
                ProcessItem("State", product.Align.State), 
                new("Result", product.Align.Result, ResultTone(product.Align.Result)), 
                new("Offset X", FormatProductDouble(product.Align.OffsetX), "Normal"), 
                new("Offset Y", FormatProductDouble(product.Align.OffsetY), "Normal"), 
                new("Rotation", FormatProductDouble(product.Align.Rotation), "Normal")
            ];
            ProductDistortionItems = [
                ProcessItem("State", product.Distortion.State), 
                new("Result", product.Distortion.Result, ResultTone(product.Distortion.Result)), 
                new("Max DA", FormatProductDouble(product.Distortion.MaxDa), "Normal")
            ];
            ProductProcessItems = [
                new("Process ID", product.ProcessId, "Accent"), 
                new("State", product.State.ToString().ToUpperInvariant(), ProductStateTone(product.State, product.Result)), 
                new("Started", FormatProductDateTime(product.StartedAt), "Normal"), 
                new("Completed", FormatProductDateTime(product.CompletedAt), "Normal")
            ];
            bool HasKey(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return !string.IsNullOrWhiteSpace(parameter.Key);
            }

            string SelectRecipeParameterKey(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return parameter.Key;
            }

            string SelectRecipeParameterGroupKey(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_PARAMETER> group)
            {
                return group.Key;
            }

            string SelectValue(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_PARAMETER> group)
            {
                return group.First().Name;
            }

            var recipeDisplayNames = recipeDefinition?.Parameters.Where(HasKey).GroupBy(SelectRecipeParameterKey, StringComparer.OrdinalIgnoreCase).ToDictionary(SelectRecipeParameterGroupKey, SelectValue, StringComparer.OrdinalIgnoreCase);
            global::Drilling.UI.Menu.Menus.ST_MONITOR_PRODUCT_ITEM BuildMonitor(global::System.Collections.Generic.KeyValuePair<string, string> parameter)
            {
                return new ST_MONITOR_PRODUCT_ITEM(GetRecipeDisplayName(parameter.Key, recipeDisplayNames), string.IsNullOrWhiteSpace(parameter.Value) ? "-" : parameter.Value, "Normal");
            }

            ProductRecipeItems = product.Recipe.Parameters.Select(BuildMonitor).ToArray();
            ProductReviewItems = [
                ProcessItem("State", product.Review.State), 
                new("Saved At", FormatProductDateTime(product.Review.SavedAt), "Normal"), 
                new("Result Count", product.Review.Results.Count.ToString(CultureInfo.InvariantCulture), "Normal")
            ];
            int SelectCellNo(global::Drilling.Common.Product.ST_PRODUCT_REVIEW_RESULT result)
            {
                return result.CellNo;
            }

            string SelectShotKey(global::Drilling.Common.Product.ST_PRODUCT_REVIEW_RESULT result)
            {
                return result.ShotKey;
            }

            global::Drilling.UI.Menu.Menus.ST_MONITOR_PRODUCT_REVIEW_ROW BuildMonitor2(global::Drilling.Common.Product.ST_PRODUCT_REVIEW_RESULT result)
            {
                return new ST_MONITOR_PRODUCT_REVIEW_ROW(
                    result.ShotKey, 
                    FormatReviewShotName(result.ShotKey, result.CellNo), 
                    result.HeadNo.ToString(CultureInfo.InvariantCulture), 
                    result.CellNo.ToString(CultureInfo.InvariantCulture), 
                    result.BeamNo > 0 ? result.BeamNo.ToString(CultureInfo.InvariantCulture) : "-", 
                    result.ErrorX.ToString("F6", CultureInfo.InvariantCulture), 
                    result.ErrorY.ToString("F6", CultureInfo.InvariantCulture), 
                    result.Judge
                );
            }

            ProductReviewRows = product.Review.Results.OrderBy(SelectCellNo).ThenBy(SelectShotKey, StringComparer.OrdinalIgnoreCase).Select(BuildMonitor2).ToArray();
        }

        private static string GetRecipeDisplayName(string key, IReadOnlyDictionary<string, string>? displayNames)
        {
            if (displayNames is not null && displayNames.TryGetValue(key, out var displayName) && !string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }

            return key;
        }

        private static string FormatReviewShotName(string shotKey, int cellNo)
        {
            var prefix = $"CELL{cellNo}_";
            return shotKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? shotKey[prefix.Length..] : shotKey;
        }

        private static IReadOnlyList<ST_MONITOR_PRODUCT_ITEM> CreateProductDataErrorItems(string message)
        {
            return[new("State", "DATA ERROR", "Error"), new("Message", message, "Error")];
        }

        private static ST_MONITOR_PRODUCT_ITEM ProcessItem(string name, EN_PRODUCT_PROCESS_STATE state)
        {
            return new(name, state.ToString().ToUpperInvariant(), state switch
            {
                EN_PRODUCT_PROCESS_STATE.Completed => "Ok",
                EN_PRODUCT_PROCESS_STATE.Running => "Accent",
                EN_PRODUCT_PROCESS_STATE.Stopped => "Warn",
                EN_PRODUCT_PROCESS_STATE.Error => "Error",
                _ => "Warn"
            });
        }

        private static IReadOnlyList<ST_MONITOR_PRODUCT_HISTORY_ROW> CreateProductHistoryRows(IReadOnlyList<ST_PRODUCT_HISTORY> history)
        {
            global::System.DateTimeOffset SelectOccurredAt(global::Drilling.Common.Product.ST_PRODUCT_HISTORY item)
            {
                return item.OccurredAt;
            }

            global::Drilling.UI.Menu.Menus.ST_MONITOR_PRODUCT_HISTORY_ROW BuildMonitor(global::Drilling.Common.Product.ST_PRODUCT_HISTORY item)
            {
                return new ST_MONITOR_PRODUCT_HISTORY_ROW(item.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), item.GlassId, item.RecipeId, item.Action, item.State, item.Result, item.Detail);
            }

            return history.OrderByDescending(SelectOccurredAt).Select(BuildMonitor).ToArray();
        }

        private static string ProductStateTone(EN_PRODUCT_STATE state, EN_PRODUCT_RESULT result)
        {
            if (result == EN_PRODUCT_RESULT.NG)
            {
                return "Error";
            }

            if (result == EN_PRODUCT_RESULT.Skipped)
            {
                return "Warn";
            }

            return state switch
            {
                EN_PRODUCT_STATE.Running => "Accent",
                EN_PRODUCT_STATE.Completed => "Ok",
                EN_PRODUCT_STATE.Error or EN_PRODUCT_STATE.Scrapped or EN_PRODUCT_STATE.Stopped => "Error",
                _ => "Warn"
            };
        }

        private static string ProductResultTone(EN_PRODUCT_RESULT result)
        {
            return result switch
            {
                EN_PRODUCT_RESULT.OK => "Ok",
                EN_PRODUCT_RESULT.NG => "Error",
                _ => "Warn"
            };
        }

        private static string FormatProductDateTime(DateTimeOffset? value)
        {
            return value?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-";
        }

        private static bool ParseMelsecBit(string value)
        {
            return value.Trim().ToUpperInvariant() switch
            {
                "1" or "TRUE" or "ON" or "YES" => true,
                "0" or "FALSE" or "OFF" or "NO" => false,
                _ => throw new FormatException($"MELSEC BIT value must be ON/OFF or 1/0: {value}")};
        }

        private static string MelsecAccessText(EN_MELSEC_ACCESS access)
        {
            return access switch
            {
                EN_MELSEC_ACCESS.Read => "R",
                EN_MELSEC_ACCESS.Write => "W",
                EN_MELSEC_ACCESS.ReadWrite => "RW",
                _ => access.ToString().ToUpperInvariant()};
        }

        private static string DefaultMelsecWriteValue(ST_MELSEC_MAP_DATA data)
        {
            if (data.Access == EN_MELSEC_ACCESS.Read)
            {
                return "";
            }

            return data.DataType switch
            {
                EN_MELSEC_DATA_TYPE.Bit => "ON",
                EN_MELSEC_DATA_TYPE.String => "",
                _ => "0"
            };
        }

        private string MelsecWriteValue(ST_MELSEC_MAP_DATA data)
        {
            if (data.Access == EN_MELSEC_ACCESS.Read)
            {
                return "";
            }

            return _melsecWriteValues.TryGetValue(data.Id, out var value) ? value : DefaultMelsecWriteValue(data);
        }

        private static string MelsecRowState(ST_MELSEC_MAP_DATA data)
        {
            if (!data.Use)
            {
                return "DISABLED";
            }

            return data.Access switch
            {
                EN_MELSEC_ACCESS.Read => "READ",
                EN_MELSEC_ACCESS.Write => "WRITE",
                _ => "READY"
            };
        }

#endregion
#region Devices
        private async Task<ST_DEVICE_COMMAND_RESULT> ExecuteMotorOperation(string label)
        {
            var key = NormalizeMonitorOperation(label);
            if (key == "MULTIABSMOVE")
            {
                return await ExecuteXpsMultiMove();
            }

            if (key == "REFRESH")
            {
                return new ST_DEVICE_COMMAND_RESULT(true, $"Motion {_selectedAxisId} status refreshed.");
            }

            var command = key switch
            {
                "SERVOON" => (Command: EN_MOTION_COMMAND.ServoOn, Parameter: 0.0, Name: "SERVO ON"),
                "SERVOOFF" => (Command: EN_MOTION_COMMAND.ServoOff, Parameter: 0.0, Name: "SERVO OFF"),
                "HOME" => (Command: EN_MOTION_COMMAND.Home, Parameter: 0.0, Name: "HOME"),
                "ABSMOVE" => (Command: EN_MOTION_COMMAND.MoveAbs, Parameter: ReadOperationField("ABS Target Position", 0.0), Name: "ABS MOVE"),
                "RELMOVE" => (Command: EN_MOTION_COMMAND.MoveRel, Parameter: ReadOperationField("REL Distance", 0.0), Name: "REL MOVE"),
                "STOP" => (Command: EN_MOTION_COMMAND.Stop, Parameter: 0.0, Name: "STOP"),
                "RESETALARM" or "ALARMRESET" => (Command: EN_MOTION_COMMAND.ResetAlarm, Parameter: 0.0, Name: "ALARM RESET"),
                _ => (Command: EN_MOTION_COMMAND.Refresh, Parameter: 0.0, Name: "")};
            if (string.IsNullOrWhiteSpace(command.Name))
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"Unknown motion monitor command: {label}");
            }

            if (command.Command is EN_MOTION_COMMAND.Home or EN_MOTION_COMMAND.MoveAbs or EN_MOTION_COMMAND.MoveRel)
            {
                var blockDetail = await GetMotorManualMoveBlockDetail();
                if (!string.IsNullOrWhiteSpace(blockDetail))
                {
                    return new ST_DEVICE_COMMAND_RESULT(false, $"Motion {_selectedAxisId} {command.Name} blocked by InterLock. {blockDetail}");
                }
            }

            if (command.Command is EN_MOTION_COMMAND.MoveAbs or EN_MOTION_COMMAND.MoveRel)
            {
                var profileName = command.Command == EN_MOTION_COMMAND.MoveAbs ? "ABS" : "REL";
                var profileResult = await ApplyXpsMoveProfile(profileName);
                if (!profileResult.IsSuccess)
                {
                    return profileResult;
                }
            }

            var result = await _motionManager.ExecuteMotionCommand(_selectedAxisId, command.Command, command.Parameter);
            var message = result.IsSuccess ? $"Motion {_selectedAxisId} {command.Name} command OK." : result.Message;
            return new ST_DEVICE_COMMAND_RESULT(result.IsSuccess, message);
        }

        private async Task<ST_DEVICE_COMMAND_RESULT> ExecuteXpsMultiMove()
        {
            bool IsIsSelected(global::Drilling.UI.Menu.Menus.ST_MONITOR_MULTI_MOVE_AXIS_ROW row)
            {
                return row.IsSelected;
            }

            var selectedRows = MultiMoveAxisRows.Where(IsIsSelected).ToArray();
            if (selectedRows.Length == 0)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "Select at least one XPS axis for MULTI MOVE.");
            }

            if (!TryReadPositiveOperationField("Multi Speed", out var speed) || !TryReadPositiveOperationField("Multi Acceleration", out var acceleration))
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "XPS MULTI MOVE speed and acceleration must be greater than 0.");
            }

            var requests = new List<ST_MOTION_ABS_MOVE_REQUEST>(selectedRows.Length);
            foreach (var row in selectedRows)
            {
                if (!double.TryParse(row.TargetPosition, NumberStyles.Float, CultureInfo.InvariantCulture, out var targetPosition) || !double.IsFinite(targetPosition))
                {
                    return new ST_DEVICE_COMMAND_RESULT(false, $"XPS MULTI MOVE target is invalid. Axis={row.Axis}, Target={row.TargetPosition}");
                }

                requests.Add(new ST_MOTION_ABS_MOVE_REQUEST(row.AxisId, targetPosition, speed, acceleration));
            }

            var blockDetail = await GetMotorManualMoveBlockDetail();
            if (!string.IsNullOrWhiteSpace(blockDetail))
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"XPS MULTI MOVE blocked by InterLock. {blockDetail}");
            }

            return await _motionManager.ExecuteAbsoluteMoves(requests);
        }

        public async Task StartXpsJog(bool positive)
        {
            if (SelectedTab != "XPS")
            {
                return;
            }

            SaveOperationFieldValues(SelectedTab);
            var requestVersion = ++_xpsJogRequestVersion;
            var axisId = _selectedAxisId;
            _xpsJogRequested = true;
            _xpsJogAxisId = axisId;
            var startCancellation = new CancellationTokenSource();
            CancelXpsJogStart(Interlocked.Exchange(ref _xpsJogStartCancellationSource, startCancellation));
            var cancellationToken = startCancellation.Token;
            var commandLockTaken = false;
            try
            {
                var blockDetail = await GetMotorManualMoveBlockDetail(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(blockDetail))
                {
                    CancelXpsJogRequest(requestVersion);
                    _setStatusMessage($"Motion {axisId} JOG blocked by InterLock. {blockDetail}");
                    return;
                }

                var velocity = Math.Abs(ReadOperationField("Jog Speed", 0.0));
                var acceleration = Math.Abs(ReadOperationField("Jog Acceleration", 0.0));
                if (velocity <= 0.0 || acceleration <= 0.0)
                {
                    CancelXpsJogRequest(requestVersion);
                    _setStatusMessage("XPS JOG velocity and acceleration must be greater than 0.");
                    return;
                }

                var command = positive ? EN_MOTION_COMMAND.JogPositive : EN_MOTION_COMMAND.JogNegative;
                await _xpsJogCommandLock.WaitAsync(cancellationToken);
                commandLockTaken = true;
                if (!IsCurrentXpsJogRequest(requestVersion, axisId))
                {
                    return;
                }

                var result = await _motionManager.ExecuteMotionCommand(axisId, command, velocity, cancellationToken, parameter2: acceleration);
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentXpsJogRequest(requestVersion, axisId))
                {
                    return;
                }

                _setStatusMessage(result.IsSuccess ? $"Motion {axisId} JOG {(positive ? "+" : "-")} started." : result.Message);
                _refreshShellStatus();
            }
            catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
            {
            // Button release invalidates an in-flight START; STOP is sent on an independent path.
            }
            finally
            {
                if (commandLockTaken)
                {
                    _xpsJogCommandLock.Release();
                }

                _ = Interlocked.CompareExchange(ref _xpsJogStartCancellationSource, null, startCancellation);
                startCancellation.Dispose();
            }
        }

        public async Task StopXpsJog()
        {
            if (SelectedTab != "XPS")
            {
                return;
            }

            var hasJogTarget = _xpsJogRequested || !string.IsNullOrWhiteSpace(_xpsJogAxisId);
            _xpsJogRequested = false;
            ++_xpsJogRequestVersion;
            CancelXpsJogStart(Volatile.Read(ref _xpsJogStartCancellationSource));
            if (_xpsJogStopPending || !hasJogTarget)
            {
                return;
            }

            _xpsJogStopPending = true;
            var axisId = string.IsNullOrWhiteSpace(_xpsJogAxisId) ? _selectedAxisId : _xpsJogAxisId;
            ST_DEVICE_COMMAND_RESULT result;
            try
            {
                result = await _motionManager.ExecuteMotionCommand(axisId, EN_MOTION_COMMAND.JogStop, cancellationToken: CancellationToken.None);
            }
            finally
            {
                _xpsJogStopPending = false;
                if (!_xpsJogRequested && _xpsJogAxisId.Equals(axisId, StringComparison.OrdinalIgnoreCase))
                {
                    _xpsJogAxisId = "";
                }
            }

            _setStatusMessage(result.IsSuccess ? $"Motion {axisId} JOG stopped." : result.Message);
            _refreshShellStatus();
        }

        public void ReportXpsJogFailure(Exception exception)
        {
            _setStatusMessage($"XPS JOG command failed. {exception.Message}");
            _refreshShellStatus();
        }

        private async Task<ST_DEVICE_COMMAND_RESULT> ApplyXpsMoveProfile(string profileName)
        {
            var velocity = Math.Abs(ReadOperationField($"{profileName} Speed", 0.0));
            var acceleration = Math.Abs(ReadOperationField($"{profileName} Acceleration", 0.0));
            if (velocity <= 0.0 || acceleration <= 0.0)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "XPS move velocity and acceleration must be greater than 0.");
            }

            return await _motionManager.ExecuteMotionCommand(_selectedAxisId, EN_MOTION_COMMAND.SetProfile, velocity, parameter2: acceleration);
        }

        private bool IsCurrentXpsJogRequest(int requestVersion, string axisId)
        {
            return _xpsJogRequested && requestVersion == _xpsJogRequestVersion && _xpsJogAxisId.Equals(axisId, StringComparison.OrdinalIgnoreCase);
        }

        private void CancelXpsJogRequest(int requestVersion)
        {
            if (requestVersion != _xpsJogRequestVersion)
            {
                return;
            }

            _xpsJogRequested = false;
            _xpsJogAxisId = "";
        }

        private static void CancelXpsJogStart(CancellationTokenSource? cancellationSource)
        {
            try
            {
                cancellationSource?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            // A completed START can dispose its token while a release event is being handled.
            }
        }

        private async Task<string> GetMotorManualMoveBlockDetail(CancellationToken cancellationToken = default)
        {
            var interLock = _interLockManager.Evaluate(await GetDeviceStatus(cancellationToken));
            if (interLock.CanManualMove)
            {
                return "";
            }

            bool MatchesItem(global::Drilling.Common.InterLock.ST_INTERLOCK_ITEM item)
            {
                return item.Level != EN_INTERLOCK_LEVEL.Ok;
            }

            var blockedItem = interLock.Items.FirstOrDefault(MatchesItem);
            return blockedItem is null ? "InterLock is not ready." : $"{blockedItem.Signal}: {blockedItem.Detail}";
        }

        private async Task<ST_DEVICE_COMMAND_RESULT> ExecuteLaserOperation(string label)
        {
            var key = NormalizeMonitorOperation(label);
            if (key == "REFRESH")
            {
                var status = _interfaceManager.RefreshTalonLaserStatus(_selectedLaserNumber);
                return new ST_DEVICE_COMMAND_RESULT(true, $"{SelectedLaserName} Talon laser refreshed. Power {status.OutputPower.ToString("F3", CultureInfo.InvariantCulture)} W.");
            }

            var command = key switch
            {
                "LASERON" => (Command: EN_TALON_COMMAND.SetLaserOnOff, Parameter: 1.0, Name: "LASER ON"),
                "LASEROFF" => (Command: EN_TALON_COMMAND.SetLaserOnOff, Parameter: 0.0, Name: "LASER OFF"),
                "GATEON" => (Command: EN_TALON_COMMAND.SetGateOpenClose, Parameter: 1.0, Name: "GATE ON"),
                "GATEOFF" => (Command: EN_TALON_COMMAND.SetGateOpenClose, Parameter: 0.0, Name: "GATE OFF"),
                "EXTGATEON" or "EXTGATEENABLE" => (Command: EN_TALON_COMMAND.SetExtGateEnableDisable, Parameter: 1.0, Name: "EXT GATE ENABLE"),
                "EXTGATEOFF" or "EXTGATEDISABLE" => (Command: EN_TALON_COMMAND.SetExtGateEnableDisable, Parameter: 0.0, Name: "EXT GATE DISABLE"),
                "SHUTTEROPEN" => (Command: EN_TALON_COMMAND.SetShutterOpenClose, Parameter: 1.0, Name: "SHUTTER OPEN"),
                "SHUTTERCLOSE" => (Command: EN_TALON_COMMAND.SetShutterOpenClose, Parameter: 0.0, Name: "SHUTTER CLOSE"),
                "SETQSW" => (Command: EN_TALON_COMMAND.SetQsw, Parameter: ReadOperationField("QSW", 20000.0), Name: "SET QSW"),
                "SETEPRF" => (Command: EN_TALON_COMMAND.SetEprf, Parameter: ReadOperationField("EPRF", 20000.0), Name: "SET EPRF"),
                "SETSHG" => (Command: EN_TALON_COMMAND.SetShg, Parameter: ReadOperationField("SHG Count", 0.0), Name: "SET SHG"),
                "SETQMODE" => (Command: EN_TALON_COMMAND.SetQMode, Parameter: ReadOperationField("Q Mode", 0.0), Name: "SET Q MODE"),
                "SAVE" => (Command: EN_TALON_COMMAND.RequestSave, Parameter: 0.0, Name: "SAVE"),
                _ => (Command: EN_TALON_COMMAND.RequestStatusString, Parameter: 0.0, Name: "")};
            if (string.IsNullOrWhiteSpace(command.Name))
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"Unknown Talon monitor command: {label}");
            }

            var result = await _interfaceManager.ExecuteTalonLaserCommand(_selectedLaserNumber, command.Command, command.Parameter);
            var message = result.IsSuccess ? $"{SelectedLaserName} Talon {command.Name} command OK. Response: {result.Message}" : $"{SelectedLaserName} Talon {command.Name} command failed. {result.Message}";
            return new ST_DEVICE_COMMAND_RESULT(result.IsSuccess, message);
        }

        private async Task<ST_DEVICE_COMMAND_RESULT> ExecuteAttenuatorOperation(string label)
        {
            var key = NormalizeMonitorOperation(label);
            if (key == "REFRESH")
            {
                var status = await Task.Run(() => _interfaceManager.RefreshAttenuatorStatus(_selectedAttenuatorNumber));
                return new ST_DEVICE_COMMAND_RESULT(true, $"H{_selectedAttenuatorNumber + 1:00} CONEX_AGP refreshed. Position {status.CurrentPosition.ToString("F3", CultureInfo.InvariantCulture)} DEG.");
            }

            if (key is "READSETTINGS" or "READCONFIG")
            {
                return await ReadAttenuatorSettings();
            }

            var command = key switch
            {
                "MOVEABS" => (Command: EN_ATTENUATOR_COMMAND.MoveAbs, Parameter: ReadOperationField("Target Position", 55.0), Name: "MOVE ABS"),
                "MOVEREL" => (Command: EN_ATTENUATOR_COMMAND.MoveRel, Parameter: ReadOperationField("Relative Move", 0.0), Name: "MOVE REL"),
                "SETHOMETYPE" => (Command: EN_ATTENUATOR_COMMAND.SetHomeType, Parameter: ReadOperationField("Home Type", 4.0), Name: "SET HOME TYPE"),
                "SETNEGLIMIT" or "SETNEGATIVELIMIT" or "SETNEGATIVESOFTWARELIMIT" => (Command: EN_ATTENUATOR_COMMAND.SetNegativeSoftwareLimit, Parameter: ReadOperationField("Negative Limit", 0.0), Name: "SET NEG LIMIT"),
                "SETPOSLIMIT" or "SETPOSITIVELIMIT" or "SETPOSITIVESOFTWARELIMIT" => (Command: EN_ATTENUATOR_COMMAND.SetPositiveSoftwareLimit, Parameter: ReadOperationField("Positive Limit", 360.0), Name: "SET POS LIMIT"),
                "SETIGAIN" => (Command: EN_ATTENUATOR_COMMAND.SetIGain, Parameter: ReadOperationField("I Gain", 800.0), Name: "SET I GAIN"),
                "SETPGAIN" => (Command: EN_ATTENUATOR_COMMAND.SetPGain, Parameter: ReadOperationField("P Gain", 10.0), Name: "SET P GAIN"),
                "SETIF" or "SETINTERPOLATIONFACTOR" => (Command: EN_ATTENUATOR_COMMAND.SetInterpolationFactor, Parameter: ReadOperationField("Interpolation Factor", 1000.0), Name: "SET IF"),
                "HOME" => (Command: EN_ATTENUATOR_COMMAND.Home, Parameter: 0.0, Name: "HOME"),
                "STOP" => (Command: EN_ATTENUATOR_COMMAND.Stop, Parameter: 0.0, Name: "STOP"),
                "RESET" or "RESETALARM" => (Command: EN_ATTENUATOR_COMMAND.ResetController, Parameter: 0.0, Name: "RESET"),
                _ => (Command: EN_ATTENUATOR_COMMAND.Refresh, Parameter: 0.0, Name: "")};
            if (string.IsNullOrWhiteSpace(command.Name))
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"Unknown CONEX_AGP monitor command: {label}");
            }

            var result = await _interfaceManager.ExecuteAttenuatorCommand(_selectedAttenuatorNumber, command.Command, command.Parameter);
            ShowAttenuatorLimitMessage(result);
            var message = result.IsSuccess ? $"H{_selectedAttenuatorNumber + 1:00} CONEX_AGP {command.Name} command OK. Response: {result.Message}" : $"H{_selectedAttenuatorNumber + 1:00} CONEX_AGP {command.Name} command failed. {result.Message}";
            return new ST_DEVICE_COMMAND_RESULT(result.IsSuccess, message);
        }

        private static void ShowAttenuatorLimitMessage(ST_DEVICE_COMMAND_RESULT result)
        {
            if (!result.IsSuccess && result.Message.StartsWith(CInterfaceManager.AttenuatorSoftwareLimitMessagePrefix, StringComparison.Ordinal))
            {
                CRecipeConfirmDialog.ShowWarning("Attenuator SW Limit", result.Message);
            }
        }

        private async Task<ST_DEVICE_COMMAND_RESULT> ReadAttenuatorSettings()
        {
            var currentStatus = await Task.Run(() => _interfaceManager.RefreshAttenuatorStatus(_selectedAttenuatorNumber));
            var state = currentStatus.CommandState.Trim().ToUpperInvariant();
            (EN_ATTENUATOR_COMMAND Command, string Name)[] commands = state switch
            {
                "NOT_REFERENCED" => [
                    (EN_ATTENUATOR_COMMAND.GetHomeType, "HOME TYPE"), 
                    (EN_ATTENUATOR_COMMAND.GetIGain, "I GAIN"), 
                    (EN_ATTENUATOR_COMMAND.GetPGain, "P GAIN"), 
                    (EN_ATTENUATOR_COMMAND.GetInterpolationFactor, "INTERPOLATION FACTOR")
                ],
                "CONFIGURATION" or "DISABLE" => [
                    (EN_ATTENUATOR_COMMAND.GetHomeType, "HOME TYPE"), 
                    (EN_ATTENUATOR_COMMAND.GetNegativeSoftwareLimit, "NEG LIMIT"), 
                    (EN_ATTENUATOR_COMMAND.GetPositiveSoftwareLimit, "POS LIMIT"), 
                    (EN_ATTENUATOR_COMMAND.GetIGain, "I GAIN"), 
                    (EN_ATTENUATOR_COMMAND.GetPGain, "P GAIN"), 
                    (EN_ATTENUATOR_COMMAND.GetInterpolationFactor, "INTERPOLATION FACTOR")
                ],
                "READY" => [(EN_ATTENUATOR_COMMAND.GetNegativeSoftwareLimit, "NEG LIMIT"), (EN_ATTENUATOR_COMMAND.GetPositiveSoftwareLimit, "POS LIMIT")],
                _ => []
            };
            if (commands.Length == 0)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"H{_selectedAttenuatorNumber + 1:00} CONEX_AGP READ SETTINGS is not allowed in {state} state.");
            }

            var failures = new List<string>();
            foreach (var command in commands)
            {
                var result = await _interfaceManager.ExecuteAttenuatorCommand(_selectedAttenuatorNumber, command.Command);
                if (!result.IsSuccess)
                {
                    failures.Add($"{command.Name}: {result.Message}");
                }
            }

            var status = await Task.Run(() => _interfaceManager.RefreshAttenuatorStatus(_selectedAttenuatorNumber));
            StoreAttenuatorConfigurationFields(status);
            if (failures.Count > 0)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"H{_selectedAttenuatorNumber + 1:00} CONEX_AGP READ SETTINGS partial failed. {string.Join(" / ", failures)}");
            }

            return new ST_DEVICE_COMMAND_RESULT(true, $"H{_selectedAttenuatorNumber + 1:00} CONEX_AGP READ SETTINGS command OK.");
        }

        private void StoreAttenuatorConfigurationFields(ST_ATTENUATOR_STATUS status)
        {
            var tab = "ATTENUATOR";
            if (status.HomeType.HasValue)
            {
                _operationFieldValues[CreateOperationFieldKey(tab, "Home Type")] = FormatHomeTypeOption(status.HomeType.Value);
            }

            if (status.NegativeSoftwareLimit.HasValue)
            {
                _operationFieldValues[CreateOperationFieldKey(tab, "Negative Limit")] = status.NegativeSoftwareLimit.Value.ToString("F3", CultureInfo.InvariantCulture);
            }

            if (status.PositiveSoftwareLimit.HasValue)
            {
                _operationFieldValues[CreateOperationFieldKey(tab, "Positive Limit")] = status.PositiveSoftwareLimit.Value.ToString("F3", CultureInfo.InvariantCulture);
            }

            if (status.IGain.HasValue)
            {
                _operationFieldValues[CreateOperationFieldKey(tab, "I Gain")] = status.IGain.Value.ToString("F3", CultureInfo.InvariantCulture);
            }

            if (status.PGain.HasValue)
            {
                _operationFieldValues[CreateOperationFieldKey(tab, "P Gain")] = status.PGain.Value.ToString("F3", CultureInfo.InvariantCulture);
            }

            if (status.InterpolationFactor.HasValue)
            {
                _operationFieldValues[CreateOperationFieldKey(tab, "Interpolation Factor")] = status.InterpolationFactor.Value.ToString("F3", CultureInfo.InvariantCulture);
            }
        }

        private async Task<ST_DEVICE_COMMAND_RESULT> ExecuteBETOperation(string label)
        {
            var key = NormalizeMonitorOperation(label);
            if (key == "REFRESH")
            {
                var status = await Task.Run(() => _interfaceManager.RefreshBETStatus(_selectedBetNumber));
                return new ST_DEVICE_COMMAND_RESULT(true, $"H{_selectedBetNumber + 1:00} BET refreshed. MAG {status.CurrentMagnification.ToString("F3", CultureInfo.InvariantCulture)}, DIV {status.CurrentDivergence.ToString("F3", CultureInfo.InvariantCulture)}.");
            }

            if (key == "SAVETABLE")
            {
                await _interfaceManager.SaveBETData(CreateBETTableData(BetTableRows));
                return new ST_DEVICE_COMMAND_RESULT(true, "BET TABLE saved to JHMI_BET.csv.");
            }

            if (key.StartsWith("MOVETABLE", StringComparison.OrdinalIgnoreCase))
            {
                var tableNoText = key["MOVETABLE".Length..];
                if (!int.TryParse(tableNoText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tableNo))
                {
                    return new ST_DEVICE_COMMAND_RESULT(false, $"Unknown BET table move command: {label}");
                }

                var tableIndex = Math.Max(0, tableNo);
                var tableResult = await _interfaceManager.ExecuteBETCommand(_selectedBetNumber, EN_BET_COMMAND.MoveTable, tableIndex);
                var tableMessage = tableResult.IsSuccess ? $"H{_selectedBetNumber + 1:00} BET MOVE TABLE {tableIndex} command OK. Response: {tableResult.Message}" : $"H{_selectedBetNumber + 1:00} BET MOVE TABLE {tableIndex} command failed. {tableResult.Message}";
                return new ST_DEVICE_COMMAND_RESULT(tableResult.IsSuccess, tableMessage);
            }

            var targetMag = ReadOperationField("Target MAG POS", 1020.0);
            var targetDiv = ReadOperationField("Target DIV POS", 1626.0);
            var command = key switch
            {
                "SETMAG" or "MOVEMAG" => (Command: EN_BET_COMMAND.MoveMagnification, Parameter1: targetMag, Parameter2: 0.0, Name: "MOVE MAG"),
                "SETDIV" or "MOVEDIV" => (Command: EN_BET_COMMAND.MoveDivergence, Parameter1: 0.0, Parameter2: targetDiv, Name: "MOVE DIV"),
                "HOME" => (Command: EN_BET_COMMAND.InitMotor, Parameter1: 0.0, Parameter2: 0.0, Name: "HOME"),
                "STOP" => (Command: EN_BET_COMMAND.Stop, Parameter1: 0.0, Parameter2: 0.0, Name: "STOP"),
                "RESETALARM" => (Command: EN_BET_COMMAND.ResetAlarm, Parameter1: 0.0, Parameter2: 0.0, Name: "RESET ALARM"),
                _ => (Command: EN_BET_COMMAND.Refresh, Parameter1: 0.0, Parameter2: 0.0, Name: "")};
            if (string.IsNullOrWhiteSpace(command.Name))
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"Unknown BET monitor command: {label}");
            }

            var result = await _interfaceManager.ExecuteBETCommand(_selectedBetNumber, command.Command, command.Parameter1, command.Parameter2);
            var message = result.IsSuccess ? $"H{_selectedBetNumber + 1:00} BET {command.Name} command OK. Response: {result.Message}" : $"H{_selectedBetNumber + 1:00} BET {command.Name} command failed. {result.Message}";
            return new ST_DEVICE_COMMAND_RESULT(result.IsSuccess, message);
        }

        private async Task<ST_DEVICE_COMMAND_RESULT> ExecutePowerMeterOperation(string label)
        {
            var key = NormalizeMonitorOperation(label);
            SavePowerMeterSettingValues();
            NormalizePowerMeterSelection();
            _interfaceManager.SetPowerMeterSimulationAttenuator(_selectedPowerMeterNumber, _selectedAttenuatorNumber);
            if (key == "REFRESH")
            {
                var status = _interfaceManager.RefreshPowerMeterStatus(_selectedPowerMeterNumber);
                return new ST_DEVICE_COMMAND_RESULT(true, $"PowerMeter refreshed. Power {status.MeasuredPower.ToString("F4", CultureInfo.InvariantCulture)} {status.Unit}.");
            }

            if (key == "START")
            {
                return await RunPowerMeterTuneSequence();
            }

            if (key == "STOP")
            {
                return await StopPowerMeterTuneSequence();
            }

            var command = key switch
            {
                "GETMODEL" => (Command: EN_POWER_METER_COMMAND.QueryHardwareDescription, Parameter: 0.0, Name: "GET MODEL"),
                "GETPOWER" or "READPOWER" => (Command: EN_POWER_METER_COMMAND.ReadPower, Parameter: 0.0, Name: "GET POWER"),
                "GETSERIAL" => (Command: EN_POWER_METER_COMMAND.QuerySerialNumber, Parameter: 0.0, Name: "GET SERIAL"),
                "GETWAVELENGTH" => (Command: EN_POWER_METER_COMMAND.QueryWaveLength, Parameter: 0.0, Name: "GET WAVELENGTH"),
                "GETBEAMPOS" or "GETBEAMPOSITION" => (Command: EN_POWER_METER_COMMAND.QueryBeamPosition, Parameter: 0.0, Name: "GET BEAM POS"),
                "RESET" => (Command: EN_POWER_METER_COMMAND.Reset, Parameter: 0.0, Name: "RESET"),
                _ => (Command: EN_POWER_METER_COMMAND.Refresh, Parameter: 0.0, Name: "")};
            if (string.IsNullOrWhiteSpace(command.Name))
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"Unknown PowerMeter monitor command: {label}");
            }

            var result = await _interfaceManager.ExecutePowerMeterCommand(_selectedPowerMeterNumber, command.Command, command.Parameter);
            var message = result.IsSuccess ? $"PowerMeter {command.Name} command OK. Response: {result.Message}" : $"PowerMeter {command.Name} command failed. {result.Message}";
            return new ST_DEVICE_COMMAND_RESULT(result.IsSuccess, message);
        }

        private async Task<ST_DEVICE_COMMAND_RESULT> ExecutePicoMotorOperation(string label)
        {
            var key = NormalizeMonitorOperation(label);
            if (key is "ALLMOTOR1" or "ALLMOTOR2" or "ALLMOTOR3" or "ALLMOTOR4")
            {
                var motorNo = key[^1] - '0';
                if (!_picoAllMoveMotorNos.Add(motorNo))
                {
                    _picoAllMoveMotorNos.Remove(motorNo);
                }

                OnPropertyChanged(nameof(PicoAllMotorSelectButtons));
                return new ST_DEVICE_COMMAND_RESULT(true, $"PICO_MOTOR ALL MOVE MOTOR {motorNo} selection changed.");
            }

            if (key is "MOTOR1" or "MOTOR2" or "MOTOR3" or "MOTOR4")
            {
                _selectedPicoMotorNo = key[^1] - '0';
                OnPropertyChanged(nameof(PicoMotorSelectButtons));
                return await _interfaceManager.ExecutePicoMotorCommand(EN_PICO_MOTOR_COMMAND.SelectMotor, _selectedPicoMotorNo);
            }

            int SelectNumber(int number)
            {
                return number;
            }

            if (key == "START")
            {
                if (!_picoMotorIsConnected)
                {
                    return new ST_DEVICE_COMMAND_RESULT(false, "PICO_MOTOR is disconnected. Connect first.");
                }

                if (_picoAllMoveTask is { IsCompleted: false })
                {
                    return new ST_DEVICE_COMMAND_RESULT(false, "PICO_MOTOR ALL_MOVE is already running.");
                }

                var selectedMotors = _picoAllMoveMotorNos.OrderBy(SelectNumber).ToArray();
                var setCount = Math.Max(0, (int)Math.Round(ReadOperationField("Set Count", 0)));
                var position = ReadOperationField("Position", 0.0);
                if (selectedMotors.Length == 0)
                {
                    return new ST_DEVICE_COMMAND_RESULT(false, "Select at least one motor for PICO_MOTOR ALL_MOVE.");
                }

                if (setCount <= 0)
                {
                    return new ST_DEVICE_COMMAND_RESULT(false, "PICO_MOTOR ALL_MOVE Set Count must be greater than 0.");
                }

                _picoAllMoveTask = RunPicoMotorAllMove(selectedMotors, position, setCount);
                return new ST_DEVICE_COMMAND_RESULT(true, "PICO_MOTOR ALL_MOVE started.");
            }

            var command = key switch
            {
                "CONNECT" => (EN_PICO_MOTOR_COMMAND.Connect, 0.0),
                "DISCONNECT" => (EN_PICO_MOTOR_COMMAND.Disconnect, 0.0),
                "HOME" => (EN_PICO_MOTOR_COMMAND.Home, 0.0),
                "STOPMOTION" => (EN_PICO_MOTOR_COMMAND.StopMotion, 0.0),
                "STOP" or "ALLSTOP" => (EN_PICO_MOTOR_COMMAND.AllMotorStop, 0.0),
                "REL" or "REL+" => label.Contains('+', StringComparison.Ordinal) ? (EN_PICO_MOTOR_COMMAND.MoveRelativePositive, Math.Abs(ReadOperationField("Relative Move", 0.0))) : (EN_PICO_MOTOR_COMMAND.MoveRelativeNegative, Math.Abs(ReadOperationField("Relative Move", 0.0))),
                "ABSMOVE" => (EN_PICO_MOTOR_COMMAND.MoveAbsolute, ReadOperationField("Absolute Move", 0.0)),
                "SETVEL" or "SETVELOCITY" or "PICOSETVEL" => (EN_PICO_MOTOR_COMMAND.SetVelocity, ReadOperationField("Set Velocity", 0.0)),
                "SETACC" or "SETACCELERATION" or "PICOSETACC" => (EN_PICO_MOTOR_COMMAND.SetAcceleration, ReadOperationField("Set Acceleration", 0.0)),
                "REFRESH" => (EN_PICO_MOTOR_COMMAND.Refresh, 0.0),
                _ => ((EN_PICO_MOTOR_COMMAND)(-1), 0.0)};
            if ((int)command.Item1 < 0)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"Unknown PicoMotor command: {label}");
            }

            if (!_picoMotorIsConnected && command.Item1 is not EN_PICO_MOTOR_COMMAND.Connect and not EN_PICO_MOTOR_COMMAND.Disconnect and not EN_PICO_MOTOR_COMMAND.SelectMotor)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "PICO_MOTOR is disconnected. Connect first.");
            }

            var isPositionMove = command.Item1 is EN_PICO_MOTOR_COMMAND.Home or EN_PICO_MOTOR_COMMAND.MoveRelativeNegative or EN_PICO_MOTOR_COMMAND.MoveRelativePositive or EN_PICO_MOTOR_COMMAND.MoveAbsolute;
            if (isPositionMove && _picoMotionTask is { IsCompleted: false })
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "PICO_MOTOR motion is already running.");
            }

            if (isPositionMove)
            {
                _picoMotionTask = RunPicoMotorPositionMove(command.Item1, _selectedPicoMotorNo, command.Item2);
                return new ST_DEVICE_COMMAND_RESULT(true, $"PICO_MOTOR {label} started.");
            }

            var result = await _interfaceManager.ExecutePicoMotorCommand(command.Item1, _selectedPicoMotorNo, command.Item2);
            if (result.IsSuccess)
            {
                if (command.Item1 == EN_PICO_MOTOR_COMMAND.SetVelocity)
                {
                    SetOperationFieldValue("Set Velocity", "0.00");
                }
                else if (command.Item1 == EN_PICO_MOTOR_COMMAND.SetAcceleration)
                {
                    SetOperationFieldValue("Set Acceleration", "0.00");
                }
            }

            return result;
        }

        private async Task StartPicoJog(object? parameter)
        {
            if (!EnsureMonitorWriteCommandAllowed("PICO_MOTOR JOG"))
            {
                return;
            }

            if (SelectedTab != "PICO MOTOR" || !_picoMotorIsConnected)
            {
                _setStatusMessage("PICO_MOTOR is disconnected. Connect first.");
                return;
            }

            var label = GetMonitorOperationLabel(parameter);
            var command = label.Contains('+', StringComparison.Ordinal) ? EN_PICO_MOTOR_COMMAND.JogPositive : EN_PICO_MOTOR_COMMAND.JogNegative;
            await _picoJogCommandLock.WaitAsync();
            try
            {
                _picoJogCancellationSource?.Cancel();
                _picoJogCancellationSource?.Dispose();
                _picoJogCancellationSource = new CancellationTokenSource();
                var result = await _interfaceManager.ExecutePicoMotorCommand(command, _selectedPicoMotorNo);
                _setStatusMessage(result.IsSuccess ? $"PICO_MOTOR {label} started." : $"PICO_MOTOR {label} failed. {result.Message}");
                if (result.IsSuccess && IsPicoMotorSimulation())
                {
                    _ = RunPicoJogSimulation(command, _picoJogCancellationSource.Token);
                }
            }
            finally
            {
                _picoJogCommandLock.Release();
            }
        }

        private async Task StopPicoJog()
        {
            if (!EnsureMonitorWriteCommandAllowed("PICO_MOTOR JOG STOP"))
            {
                return;
            }

            if (SelectedTab != "PICO MOTOR")
            {
                return;
            }

            await _picoJogCommandLock.WaitAsync();
            try
            {
                _picoJogCancellationSource?.Cancel();
                _picoJogCancellationSource?.Dispose();
                _picoJogCancellationSource = null;
                var result = await _interfaceManager.ExecutePicoMotorCommand(EN_PICO_MOTOR_COMMAND.StopMotion, _selectedPicoMotorNo);
                _setStatusMessage(result.IsSuccess ? "PICO_MOTOR JOG stopped." : $"PICO_MOTOR JOG stop failed. {result.Message}");
            }
            finally
            {
                _picoJogCommandLock.Release();
            }
        }

        private bool IsPicoMotorSimulation()
        {
            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_DATA item)
            {
                return item.Number;
            }

            var data = _interfaceManager.GetInterfaceList(EN_EQP_MODULE.PicoMotor).OrderBy(SelectNumber).FirstOrDefault();
            return data is not null && _interfaceManager.IsSimul(data.Device, data.Number);
        }

        private async Task RunPicoJogSimulation(EN_PICO_MOTOR_COMMAND command, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && SelectedTab == "PICO MOTOR")
                {
                    await Task.Delay(300, cancellationToken);
                    await _interfaceManager.ExecutePicoMotorCommand(command, _selectedPicoMotorNo, 0.001, cancellationToken);
                    await RefreshPicoMotorValuesIfVisible(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RunPicoMotorAllMove(IReadOnlyList<int> selectedMotors, double position, int setCount)
        {
            try
            {
                var operation = _interfaceManager.ExecutePicoMotorAllMove(selectedMotors, position, setCount);
                while (!operation.IsCompleted)
                {
                    await Task.Delay(500);
                    await RefreshPicoMotorValuesIfVisible(CancellationToken.None);
                }

                var result = await operation;
                _setStatusMessage(result.Message);
            }
            catch (Exception ex)
            {
                _setStatusMessage($"PICO_MOTOR ALL_MOVE failed. {ex.Message}");
            }
            finally
            {
                await RefreshPicoMotorValuesIfVisible(CancellationToken.None);
            }
        }

        private async Task RunPicoMotorPositionMove(EN_PICO_MOTOR_COMMAND command, int motorNo, double parameter)
        {
            try
            {
                var operation = _interfaceManager.ExecutePicoMotorCommand(command, motorNo, parameter);
                while (!operation.IsCompleted)
                {
                    await Task.Delay(100);
                    await RefreshPicoMotorValuesIfVisible(CancellationToken.None);
                }

                var result = await operation;
                _setStatusMessage(result.Message);
            }
            catch (Exception ex)
            {
                _setStatusMessage($"PICO_MOTOR motion failed. {ex.Message}");
            }
            finally
            {
                await RefreshPicoMotorValuesIfVisible(CancellationToken.None);
            }
        }

        private Task RefreshPicoMotorValuesIfVisible(CancellationToken cancellationToken)
        {
            if (SelectedTab != "PICO MOTOR")
            {
                return Task.CompletedTask;
            }

            var status = _interfaceManager.GetPicoMotorStatus(cancellationToken);
            _selectedPicoMotorNo = Math.Clamp(status.SelectedMotorNo, 1, 4);
            _picoMotorIsConnected = status.IsConnected;
            StatusRows = CreatePicoMotorStatusRows(status);
            PositionRows = CreatePicoMotorPositionRows(status);
            UpdatePicoMotorOperationFields(status);
            NotifyMonitorLiveProperties(nameof(StatusRows), nameof(PositionRows), nameof(OperationFields), nameof(PicoConnectionButtons), nameof(PicoMotorSelectButtons));
            return Task.CompletedTask;
        }

        private void UpdatePicoMotorOperationFields(ST_PICO_MOTOR_STATUS status)
        {
            SetOperationFieldValue("Current Velocity", status.CurrentVelocity.ToString("F2", CultureInfo.InvariantCulture));
            SetOperationFieldValue("Current Acceleration", status.CurrentAcceleration.ToString("F2", CultureInfo.InvariantCulture));
            SetOperationFieldValue("Home Position", status.HomePosition.ToString(CultureInfo.InvariantCulture));
            SetOperationFieldValue("Cur Count", status.AllMoveCurrentCount.ToString(CultureInfo.InvariantCulture));
        }

        private async Task<ST_DEVICE_COMMAND_RESULT> RunPowerMeterTuneSequence()
        {
            if (_powerMeterTuneCts is not null)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "PowerMeter target tune is already running.");
            }

            SavePowerMeterSettingValues();
            NormalizePowerMeterSelection();
            if (!_enabledHeadDeviceNumbers.Contains(_selectedAttenuatorNumber))
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"PowerMeter tune skipped. H{_selectedAttenuatorNumber + 1:00} is disabled by H{_selectedAttenuatorNumber + 1:00}_USE.");
            }

            if (_interfaceManager.GetInterfaceData(EN_EQP_MODULE.Attenuator, _selectedAttenuatorNumber)is null)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"PowerMeter tune skipped. H{_selectedAttenuatorNumber + 1:00} ATT interface is not registered.");
            }

            if (_interfaceManager.GetInterfaceData(EN_EQP_MODULE.PowerMeter, _selectedPowerMeterNumber)is null)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"PowerMeter tune skipped. POWER METER {_selectedPowerMeterNumber + 1:00} interface is not registered.");
            }

            if (!TryCreatePowerMeterTuneSettings(out var settings, out var validationError))
            {
                return new ST_DEVICE_COMMAND_RESULT(false, validationError);
            }

            var tuneAttenuatorNumber = _selectedAttenuatorNumber;
            var tunePowerMeterNumber = _selectedPowerMeterNumber;
            ST_ATTENUATOR_STATUS ReadTuneAttenuatorStatus()
            {
                return _interfaceManager.GetAttenuatorStatus(tuneAttenuatorNumber, CancellationToken.None);
            }

            var currentAttenuatorStatus = await Task.Run(ReadTuneAttenuatorStatus);
            settings = settings with
            {
                InitialAttPosition = NormalizePowerMeterTuneAtt(currentAttenuatorStatus.CurrentPosition, settings)
            };
            _interfaceManager.SetPowerMeterSimulationAttenuator(tunePowerMeterNumber, tuneAttenuatorNumber);
            var tuneCts = new CancellationTokenSource();
            _powerMeterTuneCts = tuneCts;
            _powerMeterTuneAttenuatorNumber = tuneAttenuatorNumber;
            _powerMeterTunePowerMeterNumber = tunePowerMeterNumber;
            _powerMeterTuneSamples.Clear();
            _powerMeterTargetPower = settings.TargetPower;
            SetPowerMeterTuneState("RUN", $"H{tuneAttenuatorNumber + 1:00} ATT / POWER METER {tunePowerMeterNumber + 1:00} target {settings.TargetPower.ToString("F4", CultureInfo.InvariantCulture)} W tune started.");
            RefreshPowerMeterTuneDisplay();
            var cancellationToken = tuneCts.Token;
            try
            {
                var bestAtt = settings.AttMinimum;
                var bestPower = 0.0;
                var bestError = double.MaxValue;
                for (var attempt = 1; attempt <= settings.MaxIterations; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var targetAtt = SelectNextPowerMeterTuneAtt(settings, _powerMeterTuneSamples);
                    SetPowerMeterTuneState("RUN", $"Attempt {attempt:00}: ATT {targetAtt.ToString("F4", CultureInfo.InvariantCulture)} DEG 측정 중.");
                    RefreshPowerMeterTuneDisplay();
                    var moveResult = await _interfaceManager.ExecuteAttenuatorCommand(tuneAttenuatorNumber, EN_ATTENUATOR_COMMAND.MoveAbs, targetAtt, cancellationToken);
                    if (!moveResult.IsSuccess)
                    {
                        ShowAttenuatorLimitMessage(moveResult);
                        SetPowerMeterTuneState("ERROR", $"ATT move failed at {targetAtt.ToString("F4", CultureInfo.InvariantCulture)} DEG. {moveResult.Message}");
                        RefreshPowerMeterTuneDisplay();
                        return new ST_DEVICE_COMMAND_RESULT(false, _powerMeterTuneMessage);
                    }

                    await DelayPowerMeterStep(settings.SettleTimeMs, cancellationToken);
                    var powerResult = await ReadPowerMeterAverage(tunePowerMeterNumber, settings.MeasureCount, settings.MeasureIntervalMs, cancellationToken);
                    if (!powerResult.IsSuccess)
                    {
                        SetPowerMeterTuneState("ERROR", powerResult.Message);
                        RefreshPowerMeterTuneDisplay();
                        return new ST_DEVICE_COMMAND_RESULT(false, powerResult.Message);
                    }

                    var error = powerResult.Power - settings.TargetPower;
                    var absError = Math.Abs(error);
                    var resultText = absError <= settings.ToleranceW ? "OK" : error < 0.0 ? "LOW" : "HIGH";
                    _powerMeterTuneSamples.Add(new ST_POWER_METER_TUNE_SAMPLE(attempt, targetAtt, powerResult.Power, error, resultText));
                    if (absError < bestError)
                    {
                        bestAtt = targetAtt;
                        bestPower = powerResult.Power;
                        bestError = absError;
                    }

                    RefreshPowerMeterTuneDisplay();
                    if (absError <= settings.ToleranceW)
                    {
                        SetPowerMeterTuneState("OK", $"Target reached. ATT={targetAtt.ToString("F4", CultureInfo.InvariantCulture)} DEG, Power={powerResult.Power.ToString("F4", CultureInfo.InvariantCulture)} W, Error={error.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture)} W.");
                        RefreshPowerMeterTuneDisplay();
                        return new ST_DEVICE_COMMAND_RESULT(true, _powerMeterTuneMessage);
                    }
                }

                SetPowerMeterTuneState("WARN", $"Target tolerance not reached. Best ATT={bestAtt.ToString("F4", CultureInfo.InvariantCulture)} DEG, Power={bestPower.ToString("F4", CultureInfo.InvariantCulture)} W, AbsError={bestError.ToString("F4", CultureInfo.InvariantCulture)} W.");
                RefreshPowerMeterTuneDisplay();
                return new ST_DEVICE_COMMAND_RESULT(false, _powerMeterTuneMessage);
            }
            catch (OperationCanceledException)
            {
                SetPowerMeterTuneState("STOPPED", "PowerMeter target tune stopped.");
                RefreshPowerMeterTuneDisplay();
                return new ST_DEVICE_COMMAND_RESULT(false, _powerMeterTuneMessage);
            }
            finally
            {
                if (ReferenceEquals(_powerMeterTuneCts, tuneCts))
                {
                    _powerMeterTuneCts.Dispose();
                    _powerMeterTuneCts = null;
                    _powerMeterTuneAttenuatorNumber = -1;
                    _powerMeterTunePowerMeterNumber = -1;
                    if (_powerMeterTuneState == "RUN")
                    {
                        SetPowerMeterTuneState("IDLE", "PowerMeter target tune is idle.");
                    }

                    RefreshPowerMeterTuneDisplay();
                }
            }
        }

        private async Task<ST_DEVICE_COMMAND_RESULT> StopPowerMeterTuneSequence()
        {
            if (_powerMeterTuneCts is null)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "PowerMeter target tune is not running.");
            }

            _powerMeterTuneCts.Cancel();
            SetPowerMeterTuneState("STOPPING", "PowerMeter target tune stop requested.");
            RefreshPowerMeterTuneDisplay();
            var stopAttenuatorNumber = _powerMeterTuneAttenuatorNumber >= 0 ? _powerMeterTuneAttenuatorNumber : _selectedAttenuatorNumber;
            var stopResult = await _interfaceManager.ExecuteAttenuatorCommand(stopAttenuatorNumber, EN_ATTENUATOR_COMMAND.Stop, cancellationToken: CancellationToken.None);
            return stopResult.IsSuccess ? new ST_DEVICE_COMMAND_RESULT(true, "PowerMeter tune stop requested. ATT stop command sent.") : new ST_DEVICE_COMMAND_RESULT(true, $"PowerMeter tune stop requested. ATT stop command failed: {stopResult.Message}");
        }

        private bool TryCreatePowerMeterTuneSettings(out ST_POWER_METER_TUNE_SETTINGS settings, out string error)
        {
            var targetPower = ReadPwmSetting("TARGET POWER", 1.2);
            var mode = NormalizePowerMeterMode(ReadPwmText("TOLERANCE MODE", "FINE"));
            var profile = GetPowerMeterTuneProfile(mode);
            var attMin = 0.0;
            var attMax = 360.0;
            var settleTimeMs = ReadPwmInt("SETTLE TIME", GetPowerMeterDefaultSettleTime(mode));
            var measureCount = ReadPwmInt("MEASURE COUNT", ReadPwmInt("SAMPLE COUNT", 1));
            var measureIntervalMs = ReadPwmInt("MEASURE INTERVAL", 100);
            settings = new ST_POWER_METER_TUNE_SETTINGS(targetPower, mode, profile.ToleranceW, profile.MaxIterations, attMin, attMax, Math.Max(0, settleTimeMs), Math.Max(1, measureCount), Math.Max(0, measureIntervalMs));
            error = "";
            if (!double.IsFinite(targetPower) || targetPower <= 0.0)
            {
                error = "Target Power must be greater than 0 W.";
                return false;
            }

            return true;
        }

        private async Task<ST_POWER_METER_READ_RESULT> ReadPowerMeterAverage(int powerMeterNumber, int measureCount, int measureIntervalMs, CancellationToken cancellationToken)
        {
            var samples = new List<double>(measureCount);
            for (var sampleIndex = 0; sampleIndex < measureCount; sampleIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readResult = await _interfaceManager.ExecutePowerMeterCommand(powerMeterNumber, EN_POWER_METER_COMMAND.ReadPower, cancellationToken: cancellationToken);
                if (!readResult.IsSuccess)
                {
                    return new ST_POWER_METER_READ_RESULT(false, 0.0, $"PowerMeter read failed. {readResult.Message}");
                }

                samples.Add(_interfaceManager.GetPowerMeterStatus(powerMeterNumber, cancellationToken).MeasuredPower);
                if (sampleIndex + 1 < measureCount)
                {
                    await DelayPowerMeterStep(measureIntervalMs, cancellationToken);
                }
            }

            return new ST_POWER_METER_READ_RESULT(true, samples.Average(), "");
        }

        private void RefreshPowerMeterTuneDisplay()
        {
            UpdatePowerMeterGraph();
            var monitorSnapshot = _statusPollingService.GetSnapshot();
            var displayPowerMeterNumber = _powerMeterTunePowerMeterNumber >= 0 ? _powerMeterTunePowerMeterNumber : _selectedPowerMeterNumber;
            var snapshot = monitorSnapshot.DeviceStatus with
            {
                PowerMeter = _interfaceManager.GetPowerMeterStatus(displayPowerMeterNumber)
            };
            SummaryItems = CreateSummaryItems("POWER METER", snapshot);
            PwmDeviceRows = CreatePwmDeviceRows("POWER METER", snapshot);
            PwmRunButtons = CreatePwmRunButtons("POWER METER");
            NotifyMonitorLiveProperties(
                nameof(SummaryItems), 
                nameof(PwmDeviceRows), 
                nameof(PwmRunButtons), 
                nameof(PowerMeterTuneRows), 
                nameof(PowerMeterGraphLine), 
                nameof(PowerMeterTargetLineY), 
                nameof(PowerMeterGraphMinText), 
                nameof(PowerMeterGraphMaxText), 
                nameof(PowerMeterGraphTargetText), 
                nameof(PowerMeterTuneState), 
                nameof(PowerMeterTuneMessage)
            );
        }

        private void SetPowerMeterTuneState(string state, string message)
        {
            _powerMeterTuneState = state;
            _powerMeterTuneMessage = message;
        }

        public bool TryNotifyPowerMeterTuneLocked(string actionName)
        {
            if (!IsPowerMeterTuneRunning)
            {
                return false;
            }

            var message = $"PowerMeter 측정 중에는 {actionName}을(를) 사용할 수 없습니다.{Environment.NewLine}STOP 후 다시 시도하세요.";
            MessageBox.Show(Application.Current?.MainWindow, message, "PowerMeter 측정 중", MessageBoxButton.OK, MessageBoxImage.Information);
            _setStatusMessage(message.Replace(Environment.NewLine, " "));
            _refreshShellStatus();
            return true;
        }

        private void SavePowerMeterSettingValues()
        {
            foreach (var row in PwmSettingRows)
            {
                _operationFieldValues[CreateOperationFieldKey("POWER METER", row.Parameter)] = row.Value;
            }

            NormalizePowerMeterSelection();
        }

        private string GetPowerMeterSettingValue(string parameter, string defaultValue)
        {
            return _operationFieldValues.TryGetValue(CreateOperationFieldKey("POWER METER", parameter), out var value) ? NormalizePowerMeterSettingValue(parameter, value) ?? defaultValue : defaultValue;
        }

        private static string? NormalizePowerMeterSettingValue(string parameter, string value)
        {
            if (!parameter.Equals("TOLERANCE MODE", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            return NormalizePowerMeterMode(value);
        }

        private static string NormalizePowerMeterMode(string value)
        {
            var mode = value.Trim().ToUpperInvariant();
            return mode is "ROUGH" or "FINE" or "DETAIL" ? mode : "FINE";
        }

        private static ST_POWER_METER_TUNE_PROFILE GetPowerMeterTuneProfile(string mode)
        {
            switch (NormalizePowerMeterMode(mode))
            {
                case "ROUGH":
                    return new ST_POWER_METER_TUNE_PROFILE("ROUGH", 0.0500, 8);
                case "DETAIL":
                    return new ST_POWER_METER_TUNE_PROFILE("DETAIL", 0.0030, 18);
                default:
                    return new ST_POWER_METER_TUNE_PROFILE("FINE", 0.0100, 12);
            }
        }

        private static int GetPowerMeterDefaultSettleTime(string mode)
        {
            switch (NormalizePowerMeterMode(mode))
            {
                case "ROUGH":
                    return 200;
                case "DETAIL":
                    return 500;
                default:
                    return 300;
            }
        }

        private static double SelectNextPowerMeterTuneAtt(ST_POWER_METER_TUNE_SETTINGS settings, IReadOnlyList<ST_POWER_METER_TUNE_SAMPLE> samples)
        {
            if (samples.Count == 0)
            {
                return NormalizePowerMeterTuneAtt(settings.InitialAttPosition, settings);
            }

            if (TryFindPowerMeterTuneBracket(samples, out var firstBracketSample, out var secondBracketSample) && firstBracketSample is not null && secondBracketSample is not null)
            {
                var bracketCandidate = CalculatePowerMeterBracketCandidate(firstBracketSample, secondBracketSample);
                return EnsurePowerMeterTuneCandidate(bracketCandidate, settings, samples);
            }

            var bestSample = FindBestPowerMeterTuneSample(samples);
            var direction = SelectPowerMeterTuneDirection(settings, samples, bestSample);
            var boundary = direction > 0 ? settings.AttMaximum : settings.AttMinimum;
            var candidate = (bestSample.AttPosition + boundary) / 2.0;
            return EnsurePowerMeterTuneCandidate(candidate, settings, samples);
        }

        private static bool TryFindPowerMeterTuneBracket(IReadOnlyList<ST_POWER_METER_TUNE_SAMPLE> samples, out ST_POWER_METER_TUNE_SAMPLE? firstSample, out ST_POWER_METER_TUNE_SAMPLE? secondSample)
        {
            firstSample = null;
            secondSample = null;
            var bestWidth = double.MaxValue;
            for (var firstIndex = 0; firstIndex < samples.Count; firstIndex++)
            {
                for (var secondIndex = firstIndex + 1; secondIndex < samples.Count; secondIndex++)
                {
                    var first = samples[firstIndex];
                    var second = samples[secondIndex];
                    if (Math.Sign(first.Error) == Math.Sign(second.Error))
                    {
                        continue;
                    }

                    var width = Math.Abs(first.AttPosition - second.AttPosition);
                    if (width >= bestWidth)
                    {
                        continue;
                    }

                    bestWidth = width;
                    firstSample = first;
                    secondSample = second;
                }
            }

            return firstSample is not null && secondSample is not null;
        }

        private static double CalculatePowerMeterBracketCandidate(ST_POWER_METER_TUNE_SAMPLE firstSample, ST_POWER_METER_TUNE_SAMPLE secondSample)
        {
            var minimum = Math.Min(firstSample.AttPosition, secondSample.AttPosition);
            var maximum = Math.Max(firstSample.AttPosition, secondSample.AttPosition);
            var errorDelta = secondSample.Error - firstSample.Error;
            if (Math.Abs(errorDelta) < 0.000000001)
            {
                return (minimum + maximum) / 2.0;
            }

            var candidate = firstSample.AttPosition - firstSample.Error * (secondSample.AttPosition - firstSample.AttPosition) / errorDelta;
            if (!double.IsFinite(candidate))
            {
                return (minimum + maximum) / 2.0;
            }

            var guard = Math.Max((maximum - minimum) * 0.1, 0.0001);
            if (maximum - minimum <= guard * 2.0)
            {
                return (minimum + maximum) / 2.0;
            }

            return Math.Clamp(candidate, minimum + guard, maximum - guard);
        }

        private static ST_POWER_METER_TUNE_SAMPLE FindBestPowerMeterTuneSample(IReadOnlyList<ST_POWER_METER_TUNE_SAMPLE> samples)
        {
            var bestSample = samples[0];
            var bestError = Math.Abs(bestSample.Error);
            for (var index = 1; index < samples.Count; index++)
            {
                var error = Math.Abs(samples[index].Error);
                if (error >= bestError)
                {
                    continue;
                }

                bestSample = samples[index];
                bestError = error;
            }

            return bestSample;
        }

        private static int SelectPowerMeterTuneDirection(ST_POWER_METER_TUNE_SETTINGS settings, IReadOnlyList<ST_POWER_METER_TUNE_SAMPLE> samples, ST_POWER_METER_TUNE_SAMPLE bestSample)
        {
            if (samples.Count == 1)
            {
                return bestSample.Error < 0.0 ? 1 : -1;
            }

            var nearestSampleFound = false;
            var nearestSample = bestSample;
            var nearestDistance = double.MaxValue;
            for (var index = 0; index < samples.Count; index++)
            {
                var sample = samples[index];
                if (sample.Attempt == bestSample.Attempt)
                {
                    continue;
                }

                var distance = Math.Abs(sample.AttPosition - bestSample.AttPosition);
                if (distance < 0.0001 || distance >= nearestDistance)
                {
                    continue;
                }

                nearestSample = sample;
                nearestDistance = distance;
                nearestSampleFound = true;
            }

            if (nearestSampleFound)
            {
                var slope = (bestSample.Error - nearestSample.Error) / (bestSample.AttPosition - nearestSample.AttPosition);
                if (double.IsFinite(slope) && Math.Abs(slope) >= 0.000000001)
                {
                    var predictedMove = -bestSample.Error / slope;
                    if (double.IsFinite(predictedMove) && Math.Abs(predictedMove) >= 0.0001)
                    {
                        return predictedMove > 0.0 ? 1 : -1;
                    }
                }
            }

            return bestSample.Error < 0.0 ? 1 : -1;
        }

        private static double EnsurePowerMeterTuneCandidate(double candidate, ST_POWER_METER_TUNE_SETTINGS settings, IReadOnlyList<ST_POWER_METER_TUNE_SAMPLE> samples)
        {
            candidate = NormalizePowerMeterTuneAtt(candidate, settings);
            if (!IsPowerMeterTuneAttSampled(candidate, samples))
            {
                return candidate;
            }

            if (TrySelectPowerMeterLargestGapCandidate(settings, samples, out var fallbackCandidate))
            {
                return fallbackCandidate;
            }

            return candidate;
        }

        private static bool TrySelectPowerMeterLargestGapCandidate(ST_POWER_METER_TUNE_SETTINGS settings, IReadOnlyList<ST_POWER_METER_TUNE_SAMPLE> samples, out double candidate)
        {
            candidate = NormalizePowerMeterTuneAtt((settings.AttMinimum + settings.AttMaximum) / 2.0, settings);
            var positions = new List<double>(samples.Count + 2)
            {
                NormalizePowerMeterTuneAtt(settings.AttMinimum, settings),
                NormalizePowerMeterTuneAtt(settings.AttMaximum, settings)
            };
            foreach (var sample in samples)
            {
                var position = NormalizePowerMeterTuneAtt(sample.AttPosition, settings);
                if (!ContainsPowerMeterTunePosition(positions, position))
                {
                    positions.Add(position);
                }
            }

            positions.Sort();
            var bestGap = 0.0;
            var found = false;
            for (var index = 0; index + 1 < positions.Count; index++)
            {
                var gap = positions[index + 1] - positions[index];
                if (gap <= bestGap || gap < 0.0002)
                {
                    continue;
                }

                var midpoint = NormalizePowerMeterTuneAtt((positions[index] + positions[index + 1]) / 2.0, settings);
                if (IsPowerMeterTuneAttSampled(midpoint, samples))
                {
                    continue;
                }

                bestGap = gap;
                candidate = midpoint;
                found = true;
            }

            return found;
        }

        private static bool ContainsPowerMeterTunePosition(IReadOnlyList<double> positions, double position)
        {
            for (var index = 0; index < positions.Count; index++)
            {
                if (Math.Abs(positions[index] - position) < 0.0001)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPowerMeterTuneAttSampled(double candidate, IReadOnlyList<ST_POWER_METER_TUNE_SAMPLE> samples)
        {
            for (var index = 0; index < samples.Count; index++)
            {
                if (Math.Abs(samples[index].AttPosition - candidate) < 0.0001)
                {
                    return true;
                }
            }

            return false;
        }

        private static double NormalizePowerMeterTuneAtt(double value, ST_POWER_METER_TUNE_SETTINGS settings)
        {
            if (!double.IsFinite(value))
            {
                value = (settings.AttMinimum + settings.AttMaximum) / 2.0;
            }

            return Math.Round(Math.Clamp(value, settings.AttMinimum, settings.AttMaximum), 4, MidpointRounding.AwayFromZero);
        }

        private static IReadOnlyList<ST_POWER_METER_TUNE_PROFILE_ROW> CreatePowerMeterTuneProfileRows()
        {
            var rows = new List<ST_POWER_METER_TUNE_PROFILE_ROW>(3);
            AddPowerMeterTuneProfileRow(rows, "ROUGH");
            AddPowerMeterTuneProfileRow(rows, "FINE");
            AddPowerMeterTuneProfileRow(rows, "DETAIL");
            return rows;
        }

        private static void AddPowerMeterTuneProfileRow(List<ST_POWER_METER_TUNE_PROFILE_ROW> rows, string mode)
        {
            var profile = GetPowerMeterTuneProfile(mode);
            rows.Add(new ST_POWER_METER_TUNE_PROFILE_ROW(profile.Mode, "+/- " + profile.ToleranceW.ToString("F4", CultureInfo.InvariantCulture) + " W", profile.MaxIterations.ToString(CultureInfo.InvariantCulture), "0 - 360 DEG"));
        }

        private void UpdatePowerMeterGraph()
        {
            PowerMeterTuneRows = CreatePowerMeterTuneRows(_powerMeterTuneSamples, _powerMeterTargetPower);
            var graph = CreatePowerMeterGraph(_powerMeterTuneSamples, _powerMeterTargetPower);
            PowerMeterGraphLine = graph.Line;
            PowerMeterTargetLineY = graph.TargetLineY;
            PowerMeterGraphMinText = graph.MinText;
            PowerMeterGraphMaxText = graph.MaxText;
            PowerMeterGraphTargetText = graph.TargetText;
        }

        private static IReadOnlyList<ST_POWER_METER_TUNE_ROW> CreatePowerMeterTuneRows(IReadOnlyList<ST_POWER_METER_TUNE_SAMPLE> samples, double targetPower)
        {
            var rows = new List<ST_POWER_METER_TUNE_ROW>(samples.Count);
            foreach (var sample in samples)
            {
                rows.Add(new ST_POWER_METER_TUNE_ROW(
                    sample.Attempt.ToString("00", CultureInfo.InvariantCulture), 
                    sample.AttPosition.ToString("F4", CultureInfo.InvariantCulture), 
                    targetPower.ToString("F4", CultureInfo.InvariantCulture), 
                    sample.Power.ToString("F4", CultureInfo.InvariantCulture), 
                    sample.Error.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture), 
                    sample.Result
                ));
            }

            return rows;
        }

        private static ST_POWER_METER_GRAPH_DATA CreatePowerMeterGraph(IReadOnlyList<ST_POWER_METER_TUNE_SAMPLE> samples, double targetPower)
        {
            var halfRange = PowerMeterGraphMinimumHalfRange;
            foreach (var sample in samples)
            {
                halfRange = Math.Max(halfRange, Math.Abs(sample.Power - targetPower));
            }

            halfRange *= 1.15;
            var targetY = PowerMeterGraphHeight / 2.0;
            var points = new PointCollection();
            for (var index = 0; index < samples.Count; index++)
            {
                var x = samples.Count == 1 ? PowerMeterGraphWidth / 2.0 : (PowerMeterGraphWidth * index) / (samples.Count - 1);
                var y = targetY - ((samples[index].Power - targetPower) / halfRange * targetY);
                y = Math.Clamp(y, 0.0, PowerMeterGraphHeight);
                points.Add(new Point(x, y));
            }

            var minPower = targetPower - halfRange;
            var maxPower = targetPower + halfRange;
            return new ST_POWER_METER_GRAPH_DATA(
                points, 
                targetY, 
                minPower.ToString("F4", CultureInfo.InvariantCulture) + " W", 
                maxPower.ToString("F4", CultureInfo.InvariantCulture) + " W", 
                "TARGET POWER " + targetPower.ToString("F4", CultureInfo.InvariantCulture) + "(W)"
            );
        }

        private static async Task DelayPowerMeterStep(int milliseconds, CancellationToken cancellationToken)
        {
            if (milliseconds > 0)
            {
                await Task.Delay(milliseconds, cancellationToken);
            }
        }

        private string ReadPwmText(string parameter, string defaultValue)
        {
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_PWM_SETTING_ROW row)
            {
                return row.Parameter.Equals(parameter, StringComparison.OrdinalIgnoreCase);
            }

            return PwmSettingRows.FirstOrDefault(MatchesItem)?.Value.Trim()is { Length: > 0 } value ? value : defaultValue;
        }

        private int ReadPwmInt(string parameter, int defaultValue)
        {
            var value = ReadPwmText(parameter, defaultValue.ToString(CultureInfo.InvariantCulture));
            if (int.TryParse(value, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }

            return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleResult) ? (int)Math.Round(doubleResult, MidpointRounding.AwayFromZero) : defaultValue;
        }

        private double ReadPwmSetting(string parameter, double defaultValue)
        {
            var value = ReadPwmText(parameter, defaultValue.ToString(CultureInfo.InvariantCulture));
            return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
        }

        private static IReadOnlyList<ST_MONITOR_AXIS_ROW> CreateAxisRows(ST_DEVICE_STATUS snapshot, string selectedAxisId)
        {
            global::Drilling.UI.Menu.Menus.ST_MONITOR_AXIS_ROW BuildMonitor(global::Drilling.Common.Motion.ST_MOTOR_AXIS_STATUS axis)
            {
                return new ST_MONITOR_AXIS_ROW(
                    axis.AxisId, 
                    axis.AxisNo >= 0 ? axis.AxisNo.ToString(CultureInfo.InvariantCulture) : axis.AxisId, 
                    axis.Name, 
                    FormatAxisPosition(axis.AxisId, axis.CurrentPosition), 
                    FormatAxisFeedback(axis.FeedbackVelocity), 
                    FormatAxisFeedback(axis.FeedbackAcceleration), 
                    OnOffText(axis.ServoOn), 
                    axis.HomeCompleted ? "YES" : "NO", 
                    axis.LimitPlusOn ? "ON" : "OK", 
                    axis.LimitMinusOn ? "ON" : "OK", 
                    axis.AlarmOn ? "ALARM" : "OK", 
                    axis.AxisId.Equals(selectedAxisId, StringComparison.OrdinalIgnoreCase)
                );
            }

            return snapshot.Motors.Select(BuildMonitor).ToArray();
        }

        private void UpdateMultiMoveAxisRows(ST_DEVICE_STATUS snapshot)
        {
            string SelectAxisId(global::Drilling.UI.Menu.Menus.ST_MONITOR_MULTI_MOVE_AXIS_ROW row)
            {
                return row.AxisId;
            }

            var existingRows = MultiMoveAxisRows.ToDictionary(SelectAxisId, StringComparer.OrdinalIgnoreCase);
            var rows = new List<ST_MONITOR_MULTI_MOVE_AXIS_ROW>(snapshot.Motors.Count);
            int SelectAxisNo(global::Drilling.Common.Motion.ST_MOTOR_AXIS_STATUS axis)
            {
                return axis.AxisNo;
            }

            foreach (var axis in snapshot.Motors.OrderBy(SelectAxisNo))
            {
                var axisNo = axis.AxisNo >= 0 ? axis.AxisNo.ToString(CultureInfo.InvariantCulture) : axis.AxisId;
                var currentPosition = FormatAxisPosition(axis.AxisId, axis.CurrentPosition);
                if (existingRows.TryGetValue(axis.AxisId, out var existingRow))
                {
                    existingRow.UpdateCurrentPosition(currentPosition);
                    rows.Add(existingRow);
                    continue;
                }

                rows.Add(new ST_MONITOR_MULTI_MOVE_AXIS_ROW(axis.AxisId, axisNo, axis.Name, currentPosition, axis.CurrentPosition.ToString("F3", CultureInfo.InvariantCulture)));
            }

            MultiMoveAxisRows = rows;
        }

        private static IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> CreateLaserOperationButtons(ST_LASER_STATUS status, ST_TALON_STATUS talonStatus)
        {
            return[
                new("LASER ON", "", status.PowerOn ? "Green" : "Dark"), 
                new("SHUTTER OPEN", "", status.ShutterOpen ? "Green" : "Dark"), 
                new("GATE ON", "", status.GateOn ? "Green" : "Dark"), 
                new("EXT GATE ENABLE", "", talonStatus.ExtGateEnable ? "Green" : "Dark"), 
                new("LASER OFF", "", status.PowerOn ? "Dark" : "Red"), 
                new("SHUTTER CLOSE", "", status.ShutterOpen ? "Dark" : "Red"), 
                new("GATE OFF", "", status.GateOn ? "Dark" : "Red"), 
                new("EXT GATE DISABLE", "", talonStatus.ExtGateEnable ? "Dark" : "Red")
            ];
        }

        private IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> CreatePicoMotorOperationButtons()
        {
            return[
                ..PicoConnectionButtons, 
                ..PicoMotorSelectButtons, 
                new("SET VEL", "Save", "Blue"), 
                new("SET ACC", "Save", "Blue"), 
                new("STOP MOTION", "Stop", "Red"), 
                new("HOME", "Home", "Blue"), 
                new("ABS MOVE", "Abs", "Blue"), 
                new("JOG -", "Rel", "Dark"), 
                new("JOG +", "Rel", "Dark"), 
                new("REL -", "Rel", "Dark"), 
                new("REL +", "Rel", "Blue"), 
                ..PicoAllMotorSelectButtons, 
                new("START", "Run", "Green"), 
                new("STOP", "Stop", "Red"), 
                new("REFRESH", "Refresh", "Dark")
            ];
        }

        private static IReadOnlyList<ST_MONITOR_STATUS_ROW> CreatePicoMotorStatusRows(ST_PICO_MOTOR_STATUS status)
        {
            return[
                new("Connection", status.IsConnected ? "CONNECTED" : "DISCONNECTED", status.IsConnected ? "ONLINE" : "OFFLINE", "-", "PicoMotor controller connection"), 
                new("Controller", status.Controller, status.CommOk ? "OK" : "ERROR", "-", "Controller identification"), 
                new("Motor 1 Position", status.Motor1Position.ToString("F3", CultureInfo.InvariantCulture), "OK", "mm", "Motor 1 position"), 
                new("Motor 2 Position", status.Motor2Position.ToString("F3", CultureInfo.InvariantCulture), "OK", "mm", "Motor 2 position"), 
                new("Motor 3 Position", status.Motor3Position.ToString("F3", CultureInfo.InvariantCulture), "OK", "mm", "Motor 3 position"), 
                new("Motor 4 Position", status.Motor4Position.ToString("F3", CultureInfo.InvariantCulture), "OK", "mm", "Motor 4 position"), 
                new("Current Velocity", status.CurrentVelocity.ToString("F2", CultureInfo.InvariantCulture), "OK", "mm/sec", "Current velocity"), 
                new("Current Acceleration", status.CurrentAcceleration.ToString("F2", CultureInfo.InvariantCulture), "OK", "msec", "Current acceleration"), 
                new("Motion State", status.MotionState, status.MotionState, "-", "Motion state"), 
                new("Error Code", status.ErrorCode.ToString(CultureInfo.InvariantCulture), status.ErrorCode == 0 ? "OK" : "ERROR", "-", status.LastError.ToString())
            ];
        }

        private static IReadOnlyList<ST_MONITOR_PARAMETER_ROW> CreatePicoErrorRows()
        {
            return[
                new("0", "YASKAWA_ERROR_NO_ERROR", "-", "Ok"), 
                new("1", "YASKAWA_ERROR_OVER_TEMP", "-", "Normal"), 
                new("2", "YASKAWA_ERROR_COMMAND_NOT_EXIST", "-", "Normal"), 
                new("3", "YASKAWA_ERROR_PARAMETER_OUT_OF_RANGE", "-", "Normal"), 
                new("4", "YASKAWA_ERROR_AXIS_NO_OUT_OF_RANGE", "-", "Normal"), 
                new("5", "YASKAWA_ERROR_EEPROM_WRITE_FAIL", "-", "Normal"), 
                new("6", "YASKAWA_ERROR_EEPROM_READ_FAIL", "-", "Normal"), 
                new("7", "YASKAWA_ERROR_AXIS_NO_MISSING", "-", "Normal")
            ];
        }

        private static IReadOnlyList<ST_MONITOR_PARAMETER_ROW> CreatePicoMotorParameterRows(ST_PICO_MOTOR_STATUS status)
        {
            return[
                new("Velocity", status.CurrentVelocity.ToString("F6", CultureInfo.InvariantCulture), "mm/s"), 
                new("Acceleration", status.CurrentAcceleration.ToString("F6", CultureInfo.InvariantCulture), "mm/s2"), 
                new("Home Position", CPicoMotor.StepToMillimeter(status.HomePosition).ToString("F6", CultureInfo.InvariantCulture), "mm"), 
                new("Step Scale", CPicoMotor.StepPerMillimeter.ToString(CultureInfo.InvariantCulture), "step/mm")
            ];
        }

        private static IReadOnlyList<ST_MONITOR_POSITION_ROW> CreatePicoMotorPositionRows(ST_PICO_MOTOR_STATUS status)
        {
            return[
                new("Motor 1", status.Motor1Position.ToString("F6", CultureInfo.InvariantCulture), "mm", status.SelectedMotorNo == 1 ? "Accent" : "Ok"), 
                new("Motor 2", status.Motor2Position.ToString("F6", CultureInfo.InvariantCulture), "mm", status.SelectedMotorNo == 2 ? "Accent" : "Ok"), 
                new("Motor 3", status.Motor3Position.ToString("F6", CultureInfo.InvariantCulture), "mm", status.SelectedMotorNo == 3 ? "Accent" : "Ok"), 
                new("Motor 4", status.Motor4Position.ToString("F6", CultureInfo.InvariantCulture), "mm", status.SelectedMotorNo == 4 ? "Accent" : "Ok")
            ];
        }

        private void NormalizePowerMeterSelection()
        {
            var options = CreatePowerMeterOptions();
            var selectedOption = ResolvePowerMeterOption(options);
            _selectedPowerMeterNumber = selectedOption.Number;
            _operationFieldValues[CreateOperationFieldKey("POWER METER", "POWER METER")] = selectedOption.DisplayText;
        }

        private ST_POWER_METER_OPTION ResolvePowerMeterOption(IReadOnlyList<ST_POWER_METER_OPTION> options)
        {
            var storedValue = _operationFieldValues.TryGetValue(CreateOperationFieldKey("POWER METER", "POWER METER"), out var value) ? value : "";
            foreach (var option in options)
            {
                if (option.DisplayText.Equals(storedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return option;
                }
            }

            foreach (var option in options)
            {
                if (option.Number == _selectedPowerMeterNumber)
                {
                    return option;
                }
            }

            return options.Count == 0 ? new ST_POWER_METER_OPTION(0, CreatePowerMeterOptionText(0, "NOT REGISTERED")) : options[0];
        }

        private IReadOnlyList<ST_POWER_METER_OPTION> CreatePowerMeterOptions()
        {
            var options = new List<ST_POWER_METER_OPTION>();
            foreach (var status in _interfaceManager.GetInterfaceCommunicationList(EN_EQP_MODULE.PowerMeter).OrderBy(GetPowerMeterStatusNumber).ThenBy(GetPowerMeterStatusNickName, StringComparer.OrdinalIgnoreCase))
            {
                options.Add(new ST_POWER_METER_OPTION(status.Number, CreatePowerMeterOptionText(status.Number, status.NickName)));
            }

            if (options.Count == 0)
            {
                options.Add(new ST_POWER_METER_OPTION(0, CreatePowerMeterOptionText(0, "NOT REGISTERED")));
            }

            return options;
        }

        private static IReadOnlyList<string> CreatePowerMeterOptionTexts(IReadOnlyList<ST_POWER_METER_OPTION> options)
        {
            var texts = new List<string>(options.Count);
            foreach (var option in options)
            {
                texts.Add(option.DisplayText);
            }

            return texts;
        }

        private static int GetPowerMeterStatusNumber(ST_INTERFACE_COMM_STATUS status)
        {
            return status.Number;
        }

        private static string GetPowerMeterStatusNickName(ST_INTERFACE_COMM_STATUS status)
        {
            return status.NickName;
        }

        private static string CreatePowerMeterOptionText(int number, string nickName)
        {
            var displayName = string.IsNullOrWhiteSpace(nickName) ? "-" : nickName.Trim();
            return $"POWER METER {number + 1:00} - {displayName}";
        }

        private string GetSelectedPowerMeterConnectionText()
        {
            bool IsSelectedPowerMeter(global::Drilling.Common.Interface.ST_INTERFACE_COMM_STATUS item)
            {
                return item.Number == _selectedPowerMeterNumber;
            }

            var status = _interfaceManager.GetInterfaceCommunicationList(EN_EQP_MODULE.PowerMeter).FirstOrDefault(IsSelectedPowerMeter);
            return status is null ? "OFFLINE" : ToCommunicationText(status.ConnectionState);
        }

        private IReadOnlyList<ST_MONITOR_LASER_CONTROL_ROW> CreateLaserControlRows(string tab, ST_LASER_STATUS laserStatus, ST_TALON_STATUS talonStatus, IReadOnlyList<ST_MONITOR_PARAMETER_ROW> operationFields)
        {
            if (tab != "LASER")
            {
                return[];
            }

            string SelectParameter(global::Drilling.UI.Menu.Menus.ST_MONITOR_PARAMETER_ROW field)
            {
                return field.Parameter;
            }

            var fields = operationFields.ToDictionary(SelectParameter, StringComparer.OrdinalIgnoreCase);
            ST_MONITOR_PARAMETER_ROW Field(string parameter)
            {
                return fields.TryGetValue(parameter, out var field) ? field : new ST_MONITOR_PARAMETER_ROW(parameter, "", "");
            }

            var thgHourLimit = ReadLaserThgHourLimit();
            var thgHourTone = talonStatus.ThgHour >= thgHourLimit ? "Error" : "Dark";
            return[
                new("Diode Current", talonStatus.DiodeCurrent.ToString("F4", CultureInfo.InvariantCulture), "A", new ST_MONITOR_PARAMETER_ROW("Diode Current", "-", "-"), "-", "READ ONLY", "Dark", false), 
                new("Diode Temp", talonStatus.DiodeTemp.ToString("F4", CultureInfo.InvariantCulture), "C", new ST_MONITOR_PARAMETER_ROW("Diode Temp", "-", "-"), "-", "READ ONLY", "Dark", false), 
                new("Tower Temp", talonStatus.TowerTemp.ToString("F4", CultureInfo.InvariantCulture), "C", new ST_MONITOR_PARAMETER_ROW("Tower Temp", "-", "-"), "-", "READ ONLY", "Dark", false), 
                new("Output Power", laserStatus.OutputPower.ToString("F4", CultureInfo.InvariantCulture), "W", new ST_MONITOR_PARAMETER_ROW("Output Power", "-", "-"), "-", "READ ONLY", "Dark", false), 
                new("THG Spot", talonStatus.ThgSpot.ToString(CultureInfo.InvariantCulture), "count", new ST_MONITOR_PARAMETER_ROW("THG Spot", "-", "-"), "-", "READ ONLY", "Dark", false), 
                new("THG Hour", talonStatus.ThgHour.ToString("F1", CultureInfo.InvariantCulture), $"h / {thgHourLimit} h", new ST_MONITOR_PARAMETER_ROW("THG Hour", "-", "-"), "-", "READ ONLY", thgHourTone, false), 
                new("QSW", talonStatus.Qsw.ToString(CultureInfo.InvariantCulture), "Hz", Field("QSW"), "Hz", "SET QSW", "Warn"), 
                new("EPRF", talonStatus.Eprf.ToString(CultureInfo.InvariantCulture), "Hz", Field("EPRF"), "Hz", "SET EPRF"), 
                new("SHG Count", talonStatus.ShgReadBackCount.ToString(CultureInfo.InvariantCulture), "count", Field("SHG Count"), "count", "SET SHG"), 
                new("Q Mode", talonStatus.QMode.ToString(CultureInfo.InvariantCulture), "-", Field("Q Mode"), "-", "SET Q MODE")
            ];
        }

        private int ReadLaserThgHourLimit()
        {
            var value = _settingManager.GetValue(EN_SETTING_TAB.Option, "LASER_THG_HOUR_LIMIT", "0");
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        private static IReadOnlyList<ST_MONITOR_BET_TABLE_ROW> CreateBetTableRows(string tab, IReadOnlyList<ST_BET_TABLE_DATA> table, ST_DEVICE_STATUS snapshot)
        {
            if (tab != "BET")
            {
                return[];
            }

            int SelectIndex(global::Drilling.Common.Interface.ST_BET_TABLE_DATA row)
            {
                return row.Index;
            }

            global::Drilling.UI.Menu.Menus.ST_MONITOR_BET_TABLE_ROW BuildMonitor(global::Drilling.Common.Interface.ST_BET_TABLE_DATA row)
            {
                var active = IsInPosition(row.Magnification, snapshot.Bet.CurrentMagnification) && IsInPosition(row.Divergence, snapshot.Bet.CurrentDivergence);
                return new ST_MONITOR_BET_TABLE_ROW(
                    row.Index.ToString(CultureInfo.InvariantCulture), 
                    row.Description, 
                    row.Magnification.ToString(CultureInfo.InvariantCulture), 
                    row.Divergence.ToString(CultureInfo.InvariantCulture), 
                    active ? "ACTIVE" : "", 
                    active
                );
            }

            return table.OrderBy(SelectIndex).Select(BuildMonitor).ToArray();
        }

        private static IReadOnlyList<ST_BET_TABLE_DATA> CreateBETTableData(IReadOnlyList<ST_MONITOR_BET_TABLE_ROW> rows)
        {
            global::Drilling.Common.Interface.ST_BET_TABLE_DATA BuildBet(global::Drilling.UI.Menu.Menus.ST_MONITOR_BET_TABLE_ROW row, int order)
            {
                return new ST_BET_TABLE_DATA(ReadBETInt(row.No, order), ReadBETStep(row.Mag), ReadBETStep(row.Div), row.Description?.Trim() ?? "");
            }

            int SelectIndex(global::Drilling.Common.Interface.ST_BET_TABLE_DATA row)
            {
                return row.Index;
            }

            return rows.Select(BuildBet).OrderBy(SelectIndex).ToArray();
        }

        private static int ReadBETInt(string value, int defaultValue)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
        }

        private static int ReadBETStep(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? Math.Clamp((int)Math.Round(result), 0, 4500) : 0;
        }

        private static IReadOnlyList<ST_MONITOR_SUMMARY_ITEM> CreateAttenuatorStatusItems(string tab, ST_DEVICE_STATUS snapshot)
        {
            if (!tab.Equals("ATTENUATOR", StringComparison.OrdinalIgnoreCase))
            {
                return[];
            }

            var inPosition = IsInPosition(snapshot.Attenuator.CurrentPosition, snapshot.Attenuator.TargetPosition);
            var moving = IsAttenuatorMoving(snapshot.Attenuator.CommandState);
            var alarmOn = !snapshot.Attenuator.CommOk || snapshot.Attenuator.LastError != EN_CONEX_AGP_ERROR.Ok;
            return[
                new("Cur Pos", snapshot.Attenuator.CurrentPosition.ToString("F3", CultureInfo.InvariantCulture), "DEG", "Accent"), 
                new("State", snapshot.Attenuator.CommandState, "", moving ? "Warn" : "Ok"), 
                new("In Position", inPosition ? "YES" : "NO", "", inPosition ? "Ok" : "Warn"), 
                new("Alarm", alarmOn ? "OCCUR" : "CLEAR", "", alarmOn ? "Warn" : "Ok")
            ];
        }

        private static IReadOnlyList<ST_MONITOR_SUMMARY_ITEM> CreateAttenuatorSettingItems(string tab, ST_DEVICE_STATUS snapshot)
        {
            if (!tab.Equals("ATTENUATOR", StringComparison.OrdinalIgnoreCase))
            {
                return[];
            }

            return[
                new("Home Type", FormatHomeType(snapshot.Attenuator.HomeType), "", FormatKnownState(snapshot.Attenuator.HomeType) == "OK" ? "Ok" : "Warn"), 
                new("Neg Limit", FormatNullableDouble(snapshot.Attenuator.NegativeSoftwareLimit), "DEG", FormatKnownState(snapshot.Attenuator.NegativeSoftwareLimit) == "OK" ? "Ok" : "Warn"), 
                new("Pos Limit", FormatNullableDouble(snapshot.Attenuator.PositiveSoftwareLimit), "DEG", FormatKnownState(snapshot.Attenuator.PositiveSoftwareLimit) == "OK" ? "Ok" : "Warn"), 
                new("I Gain", FormatNullableDouble(snapshot.Attenuator.IGain), "", FormatKnownState(snapshot.Attenuator.IGain) == "OK" ? "Ok" : "Warn"), 
                new("P Gain", FormatNullableDouble(snapshot.Attenuator.PGain), "", FormatKnownState(snapshot.Attenuator.PGain) == "OK" ? "Ok" : "Warn"), 
                new("Interpolation", FormatNullableDouble(snapshot.Attenuator.InterpolationFactor), "", FormatKnownState(snapshot.Attenuator.InterpolationFactor) == "OK" ? "Ok" : "Warn")
            ];
        }

        private static IReadOnlyList<ST_MONITOR_POSITION_ROW> CreatePositionRows(string tab, ST_DEVICE_STATUS snapshot)
        {
            if (tab == "BET")
            {
                return[
                    new("Target Magnification", FormatBETStep(snapshot.Bet.TargetMagnification), "step", "Warn"), 
                    new("Target Divergence", FormatBETStep(snapshot.Bet.TargetDivergence), "step", "Warn"), 
                    new("Mag Error", FormatBETStep(Math.Abs(snapshot.Bet.CurrentMagnification - snapshot.Bet.TargetMagnification)), "step", "Warn"), 
                    new("Div Error", FormatBETStep(Math.Abs(snapshot.Bet.CurrentDivergence - snapshot.Bet.TargetDivergence)), "step", "Warn"), 
                    new(
                    "In Position", 
                    IsInPosition(snapshot.Bet.CurrentMagnification, snapshot.Bet.TargetMagnification) && IsInPosition(snapshot.Bet.CurrentDivergence, snapshot.Bet.TargetDivergence) ? "YES" : "NO", 
                    "", 
                    "Ok"
                ), 
                    new("Moving", snapshot.Bet.IsMoving ? "MOVING" : "IDLE", "", "Ok"), 
                    new("Last Update", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "")
                ];
            }

            if (tab != "ATTENUATOR")
            {
                return[];
            }

            return[
                new("Target Position", snapshot.Attenuator.TargetPosition.ToString("F3"), "DEG", "Warn"), 
                new("Position Error", Math.Abs(snapshot.Attenuator.CurrentPosition - snapshot.Attenuator.TargetPosition).ToString("F3"), "DEG", "Warn"), 
                new("In Position", IsInPosition(snapshot.Attenuator.CurrentPosition, snapshot.Attenuator.TargetPosition) ? "YES" : "NO", "", "Ok"), 
                new("Moving", snapshot.Attenuator.CommandState, "", "Ok"), 
                new("Last Update", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "")
            ];
        }

        private IReadOnlyList<ST_PWM_SETTING_ROW> CreatePwmSettingRows(string tab)
        {
            if (tab is not ("LASER" or "POWER METER"))
            {
                return[];
            }

            NormalizePowerMeterSelection();
            var powerMeterOptions = CreatePowerMeterOptions();
            var selectedPowerMeter = ResolvePowerMeterOption(powerMeterOptions);
            if (tab == "LASER")
                return[new("POWER METER", selectedPowerMeter.DisplayText, "-", CreatePowerMeterOptionTexts(powerMeterOptions))];
            var mode = NormalizePowerMeterMode(GetPowerMeterSettingValue("TOLERANCE MODE", "FINE"));
            return[
                new("POWER METER", selectedPowerMeter.DisplayText, "-", CreatePowerMeterOptionTexts(powerMeterOptions)), 
                new("TARGET POWER", GetPowerMeterSettingValue("TARGET POWER", "1.2000"), "W"), 
                new("TOLERANCE MODE", mode, "-"), 
                new("SETTLE TIME", GetPowerMeterSettingValue("SETTLE TIME", GetPowerMeterDefaultSettleTime(mode).ToString(CultureInfo.InvariantCulture)), "ms"), 
                new("MEASURE COUNT", GetPowerMeterSettingValue("MEASURE COUNT", GetPowerMeterSettingValue("SAMPLE COUNT", "1")), "ea"), 
                new("MEASURE INTERVAL", GetPowerMeterSettingValue("MEASURE INTERVAL", "100"), "ms")
            ];
        }

        private IReadOnlyList<ST_PWM_DEVICE_ROW> CreatePwmDeviceRows(string tab, ST_DEVICE_STATUS snapshot)
        {
            if (tab is not ("LASER" or "POWER METER"))
            {
                return[];
            }

            var serial = string.IsNullOrWhiteSpace(snapshot.PowerMeter.SerialNumber) ? "-" : snapshot.PowerMeter.SerialNumber;
            var power = snapshot.PowerMeter.MeasuredPower <= 0 ? "-" : snapshot.PowerMeter.MeasuredPower.ToString("F4", CultureInfo.InvariantCulture);
            var beamPosition = $"{snapshot.PowerMeter.BeamPositionX.ToString("F3", CultureInfo.InvariantCulture)}, {snapshot.PowerMeter.BeamPositionY.ToString("F3", CultureInfo.InvariantCulture)}";
            return[
                new("MODEL", snapshot.PowerMeter.ModelName, "-", "GET MODEL"), 
                new("SERIAL", serial, "-", "GET SERIAL"), 
                new("WAVELENGTH", snapshot.PowerMeter.WaveLengthNm.ToString("F1", CultureInfo.InvariantCulture), "nm", "GET WAVELENGTH"), 
                new("BEAM POS", beamPosition, "mm", "GET BEAM POS"), 
                new("POWER", power, snapshot.PowerMeter.Unit, "GET POWER")
            ];
        }

        private IReadOnlyList<ST_MONITOR_OPERATION_BUTTON> CreatePwmRunButtons(string tab)
        {
            if (tab != "POWER METER")
            {
                return[];
            }

            return _powerMeterTuneCts is null ? [new("START", "Run", "Green"), new("STOP", "Stop", "Red")] : [new("START", "Run", "Dark"), new("STOP", "Stop", "Red")];
        }

        private static string FormatAxisPosition(string axisId, double value)
        {
            return axisId.StartsWith("BET_", StringComparison.OrdinalIgnoreCase) ? $"{value:F3} step" : value.ToString("F3");
        }

        private static string FormatNullableDouble(double? value)
        {
            return value.HasValue ? value.Value.ToString("F3", CultureInfo.InvariantCulture) : "-";
        }

        private static string FormatNullableInt(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";
        }

        private static string FormatKnownState<T>(T? value)
            where T : struct
        {
            return value.HasValue ? "OK" : "WAIT";
        }

        private static string FormatHomeType(int? value)
        {
            return value.HasValue ? FormatHomeTypeOption(value.Value) : "-";
        }

        private static string FormatHomeTypeOption(int value)
        {
            return value switch
            {
                1 => "1: Current Position as HOME",
                4 => "4: Negative End Limit as HOME",
                5 => "5: End Run Offset Init (Service)",
                _ => $"{value.ToString(CultureInfo.InvariantCulture)}: Unknown"};
        }

        private static string FormatAxisFeedback(double value)
        {
            return value.ToString("F3", CultureInfo.InvariantCulture);
        }

        private static string FormatBETStep(double value)
        {
            return Math.Clamp((int)Math.Round(value), 0, 4500).ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsInPosition(double current, double target)
        {
            return Math.Abs(current - target) <= 0.001;
        }

        private static bool IsAttenuatorMoving(string state)
        {
            return state.Contains("MOV", StringComparison.OrdinalIgnoreCase) || state.Contains("HOM", StringComparison.OrdinalIgnoreCase);
        }
#endregion
    }

    internal static class CMelsecCachePresentation
    {
        public static double NumericValue(object? value) => value switch
        {
            double number => number,
            float number => number,
            int number => number,
            uint number => number,
            short number => number,
            ushort number => number,
            long number => number,
            _ => double.NaN
        };
        public static string Describe(ST_MELSEC_CACHED_VALUE value)
        {
            var state = value.State.ToString().ToUpperInvariant();
            var message = string.IsNullOrWhiteSpace(value.ErrorMessage) ? state : $"{state}: {value.ErrorMessage}";
            return value.UpdatedAt is { } updatedAt ? $"{message} | Last received: {updatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}" : message;
        }
    }

    public sealed record ST_MONITOR_TAB(string Name, bool IsSelected);
    public sealed record ST_MONITOR_COORDINATE_VIEWER_DATA(
        IReadOnlyList<ST_MONITOR_COORDINATE_BASIS_OPTION> BasisOptions, 
        IReadOnlyList<ST_MONITOR_COORDINATE_VALUE_ROW> ValueRows, 
        ImageSource? GlassPreviewImage, 
        IReadOnlyList<ST_CELL_PREVIEW_LABEL> CellPreviewLabels, 
        IReadOnlyList<ST_MONITOR_COORDINATE_SHOT_MATRIX_ROW> ShotMatrixRows, 
        string GlassPreviewSummary, 
        string RecipeName, 
        string BasisName, 
        string BasisDescription
    )
    {
        public static ST_MONITOR_COORDINATE_VIEWER_DATA Empty { get; } = new([], [], null, [], [], "0 Cells / 0 Shot Centers", "-", "Design", "Recipe design coordinate from Align Key / Glass");
    }

    public sealed record ST_MONITOR_COORDINATE_BASIS_OPTION(string Key, string Name, string Description, bool IsSelected)
    {
        public Brush BackgroundBrush
        {
            get
            {
                return IsSelected ? CStatusBrush.CommandBlue : CStatusBrush.CommandDark;
            }
        }

        public Brush BorderBrush
        {
            get
            {
                return IsSelected ? CStatusBrush.CommandBlueBorder : CStatusBrush.CommandDarkBorder;
            }
        }

        public Brush TextBrush
        {
            get
            {
                return Brushes.White;
            }
        }
    }

    public sealed record ST_MONITOR_COORDINATE_VALUE_ROW(string Source, string Group, string Parameter, string Value, string Unit, string Key);
    public sealed record ST_MONITOR_COORDINATE_SHOT_MATRIX_ROW(int RowNo, IReadOnlyList<ST_MONITOR_COORDINATE_SHOT_BUTTON> Shots);
    public sealed record ST_MONITOR_COORDINATE_SHOT_BUTTON(string ShotKey, string ShotName, string Head, string Detail, bool IsSelected, bool IsChessSkipped, bool IsMasked, CButtonCommand SelectCommand)
    {
        public Brush BorderBrush
        {
            get
            {
                if (IsMasked)
                {
                    return CStatusBrush.Frozen(0xD7, 0x19, 0x20);
                }

                return IsSelected ? CStatusBrush.Active : CStatusBrush.Frozen(0x5B, 0x6F, 0x84);
            }
        }

        public Thickness BorderThickness
        {
            get
            {
                return IsMasked ? new Thickness(2.0) : new Thickness(1.0);
            }
        }

        public Brush BackgroundBrush
        {
            get
            {
                if (IsChessSkipped)
                {
                    return IsSelected ? CStatusBrush.SoftSelectedBackground : CStatusBrush.ChessBackground;
                }

                return IsSelected ? CStatusBrush.SoftSelectedBackground : CStatusBrush.CardBackground;
            }
        }

        public Brush DetailBrush
        {
            get
            {
                return CStatusBrush.PrimaryText;
            }
        }
    }

    internal sealed class ST_SCANNER_AXIS_INDICATOR_GROUP
    {
        public ST_SCANNER_AXIS_INDICATOR_GROUP(ST_SCANNER_AXIS_TRANSFORM transform, int headNo)
        {
            Transform = transform;
            HeadNos = new List<int>
            {
                headNo
            };
        }

        public ST_SCANNER_AXIS_TRANSFORM Transform { get; }
        public List<int> HeadNos { get; }
    }

    public sealed record ST_MONITOR_PRODUCT_ITEM(string Name, string Value, string Tone = "Normal")
    {
        public Brush ValueBrush
        {
            get
            {
                return Tone switch
                {
                    "Accent" => CStatusBrush.Simul,
                    "Warn" => CStatusBrush.Wait,
                    "Ok" => CStatusBrush.Online,
                    "Error" => CStatusBrush.Offline,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }
    }

    public sealed record ST_MONITOR_PRODUCT_REVIEW_ROW(string ShotKey, string Shot, string HeadNo, string CellNo, string BeamNo, string ErrorX, string ErrorY, string Judge)
    {
        public Brush JudgeBrush
        {
            get
            {
                return Judge.Trim().ToUpperInvariant() switch
                {
                    "OK" => CStatusBrush.Online,
                    "NG" => CStatusBrush.Offline,
                    _ => CStatusBrush.Wait
                };
            }
        }
    }

    public sealed record ST_MONITOR_PRODUCT_HISTORY_ROW(string Time, string GlassId, string RecipeId, string Action, string State, string Result, string Detail)
    {
        public Brush ResultBrush
        {
            get
            {
                return Result.Trim().ToUpperInvariant() switch
                {
                    "OK" => CStatusBrush.Online,
                    "NG" => CStatusBrush.Offline,
                    _ => CStatusBrush.Wait
                };
            }
        }
    }

    public sealed record ST_MONITOR_MELSEC_GROUP(string Name, bool IsSelected, int Count);
    public sealed class ST_MONITOR_MELSEC_ROW : CBindingBase
    {
        private string _readValue;
        private string _writeValue;
        private string _state;
        public ST_MONITOR_MELSEC_ROW(
            string id, 
            string group, 
            string name, 
            string deviceNo, 
            string address, 
            string dataType, 
            string direction, 
            string access, 
            string readScale, 
            string writeScale, 
            string length, 
            string pollMs, 
            string readValue, 
            string writeValue, 
            string state, 
            string description
        )
        {
            Id = id;
            Group = group;
            Name = name;
            DeviceNo = deviceNo;
            Address = address;
            DataType = dataType;
            Direction = direction;
            Access = access;
            ReadScale = readScale;
            WriteScale = writeScale;
            Length = length;
            PollMs = pollMs;
            _readValue = readValue;
            _writeValue = writeValue;
            _state = state;
            Description = description;
        }

        public string Id { get; }
        public string Group { get; }
        public string Name { get; }
        public string DeviceNo { get; }
        public string Address { get; }
        public string DataType { get; }
        public string Direction { get; }
        public string Access { get; }
        public string ReadScale { get; }
        public string WriteScale { get; }
        public string Length { get; }
        public string PollMs { get; }
        public string ReadValue => _readValue;
        public string WriteValue { get => _writeValue; set => SetProperty(ref _writeValue, value); }

        public void UpdateReadValue(string value, string state)
        {
            var valueChanged = SetProperty(ref _readValue, value, nameof(ReadValue));
            var stateChanged = SetProperty(ref _state, state, nameof(State));
            if (stateChanged)
            {
                OnPropertyChanged(nameof(StateBrush));
            }

            if (valueChanged || stateChanged)
            {
                OnPropertyChanged(nameof(ValueBrush));
            }
        }

        public IReadOnlyList<string> WriteValueOptions
        {
            get
            {
                return DataType == "BIT" ? ["ON", "OFF"] : [];
            }
        }

        public bool UsesWriteSelection
        {
            get
            {
                return WriteValueOptions.Count > 0;
            }
        }

        public EN_RECIPE_DATA_TYPE WriteInputType
        {
            get
            {
                return DataType switch
                {
                    "WORD" or "DWORD" when double.TryParse(WriteScale, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale != 1 => EN_RECIPE_DATA_TYPE.Double,
                    "WORD" or "DWORD" => EN_RECIPE_DATA_TYPE.Int,
                    "DOUBLE" or "FLOAT" => EN_RECIPE_DATA_TYPE.Double,
                    _ => EN_RECIPE_DATA_TYPE.String
                };
            }
        }

        public string State => _state;
        public string Description { get; }

        public bool CanRead
        {
            get
            {
                return Access is "R" or "RW";
            }
        }

        public bool CanWrite
        {
            get
            {
                return Access is "W" or "RW";
            }
        }

        public Brush StateBrush
        {
            get
            {
                return State.Trim().ToUpperInvariant() switch
                {
                    "READY" or "READ" => CStatusBrush.Online,
                    "WRITE" => CStatusBrush.Wait,
                    "ERROR" => CStatusBrush.Offline,
                    _ => CStatusBrush.Muted
                };
            }
        }

        public Brush ValueBrush
        {
            get
            {
                return State.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ? CStatusBrush.Offline : string.IsNullOrWhiteSpace(ReadValue) || ReadValue == "-" ? CStatusBrush.Muted : CStatusBrush.Simul;
            }
        }
    }

    public sealed record ST_MONITOR_AXIS_ROW(
        string AxisId, 
        string Axis, 
        string Name, 
        string CurrentPosition, 
        string FeedbackVelocity, 
        string FeedbackAcceleration, 
        string Servo, 
        string Home, 
        string LimitPlus, 
        string LimitMinus, 
        string Alarm, 
        bool IsSelected = false
    )
    {
        public Brush ServoBrush
        {
            get
            {
                return Servo.Trim().ToUpperInvariant() == "ON" ? CStatusBrush.Online : CStatusBrush.Offline;
            }
        }

        public Brush HomeBrush
        {
            get
            {
                return Home.Trim().ToUpperInvariant() == "YES" ? CStatusBrush.Online : CStatusBrush.Offline;
            }
        }

        public Brush LimitPlusBrush
        {
            get
            {
                return LimitPlus.Trim().ToUpperInvariant() == "ON" ? CStatusBrush.Offline : CStatusBrush.Online;
            }
        }

        public Brush LimitMinusBrush
        {
            get
            {
                return LimitMinus.Trim().ToUpperInvariant() == "ON" ? CStatusBrush.Offline : CStatusBrush.Online;
            }
        }

        public Brush AlarmBrush
        {
            get
            {
                return Alarm.Trim().ToUpperInvariant() == "ALARM" ? CStatusBrush.Offline : CStatusBrush.Online;
            }
        }

        public Brush RowBrush
        {
            get
            {
                return IsSelected ? CStatusBrush.Active : CStatusBrush.PrimaryText;
            }
        }
    }

    public sealed class ST_MONITOR_MULTI_MOVE_AXIS_ROW : CBindingBase
    {
        private bool _isSelected;
        private string _currentPosition;
        private string _targetPosition;
        public ST_MONITOR_MULTI_MOVE_AXIS_ROW(string axisId, string axis, string name, string currentPosition, string targetPosition)
        {
            AxisId = axisId;
            Axis = axis;
            Name = name;
            _currentPosition = currentPosition;
            _targetPosition = targetPosition;
        }

        public string AxisId { get; }
        public string Axis { get; }
        public string Name { get; }

        public bool IsSelected
        {
            get
            {
                return _isSelected;
            }

            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public string CurrentPosition
        {
            get
            {
                return _currentPosition;
            }

            private set
            {
                if (_currentPosition == value)
                {
                    return;
                }

                _currentPosition = value;
                OnPropertyChanged();
            }
        }

        public string TargetPosition
        {
            get
            {
                return _targetPosition;
            }

            set
            {
                if (_targetPosition == value)
                {
                    return;
                }

                _targetPosition = value;
                OnPropertyChanged();
            }
        }

        public void UpdateCurrentPosition(string currentPosition)
        {
            CurrentPosition = currentPosition;
        }
    }

    public sealed record ST_MONITOR_STATUS_ROW(string Item, string Value, string State, string Unit, string Description)
    {
        public Brush StateBrush
        {
            get
            {
                return State.Trim().ToUpperInvariant() switch
                {
                    "ON" or "ONLINE" or "OK" or "READY" or "RUN" or "NORMAL" or "SAFE" or "YES" or "DONE" or "IDLE" or "ENABLED" => CStatusBrush.Online,
                    "OFF" or "CLOSE" or "ERROR" or "ALARM" => CStatusBrush.Offline,
                    "WARN" or "WARNING" or "SET" or "WAIT" or "SIMULATION" or "SIM" => CStatusBrush.Wait,
                    "DISABLED" => CStatusBrush.Muted,
                    _ => CStatusBrush.Muted
                };
            }
        }

        public Brush ValueBrush
        {
            get
            {
                return Value.Trim().ToUpperInvariant() switch
                {
                    "22.4" or "55.000" or "1.200" or "20.000" or "900" or "50.000" or "30.000" => CStatusBrush.Wait,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }
    }

    public sealed record ST_MONITOR_OPERATION_BUTTON(string Label, string Icon, string Tone, string CommandKey = "")
    {
        public Brush BackgroundBrush
        {
            get
            {
                return Tone switch
                {
                    "Green" => CStatusBrush.CommandGreen,
                    "Red" => CStatusBrush.CommandRed,
                    "Blue" => CStatusBrush.CommandBlue,
                    _ => CStatusBrush.CommandDark
                };
            }
        }

        public Brush BorderBrush
        {
            get
            {
                return Tone switch
                {
                    "Green" => CStatusBrush.CommandGreenBorder,
                    "Red" => CStatusBrush.CommandRedBorder,
                    "Blue" => CStatusBrush.CommandBlueBorder,
                    _ => CStatusBrush.CommandDarkBorder
                };
            }
        }

        public Geometry IconGeometry
        {
            get
            {
                return CMonitorIcon.Get(Icon);
            }
        }
    }

    public sealed record ST_MONITOR_HEAD_SELECT_ROW(int Number, string HeadName, string NickName, string State, bool IsRegistered, bool IsSelected, bool IsEnabled = true)
    {
        public Brush StateBrush
        {
            get
            {
                return State.Trim().ToUpperInvariant() switch
                {
                    "ONLINE" => CStatusBrush.Online,
                    "SIMULATION" or "SIMUL" or "SIM" => CStatusBrush.Wait,
                    "NOT USE" => CStatusBrush.Muted,
                    _ => CStatusBrush.Offline
                };
            }
        }

        public Brush StateTextBrush
        {
            get
            {
                return StateBrush;
            }
        }

        public Brush StateIndicatorBrush
        {
            get
            {
                return StateBrush;
            }
        }

        public Brush BorderBrush
        {
            get
            {
                return IsEnabled && IsSelected ? CStatusBrush.Active : CStatusBrush.CommandDarkBorder;
            }
        }

        public Thickness BorderThicknessValue
        {
            get
            {
                return IsSelected ? new Thickness(2.2) : new Thickness(1.0);
            }
        }

        public Brush BackgroundBrush
        {
            get
            {
                return IsSelected ? CStatusBrush.SoftSelectedBackground : CStatusBrush.CardBackground;
            }
        }

        public Brush TextBrush
        {
            get
            {
                if (!IsEnabled || !IsRegistered)
                {
                    return CStatusBrush.Muted;
                }

                return CStatusBrush.PrimaryText;
            }
        }

        public double ButtonOpacity
        {
            get
            {
                return 1.0;
            }
        }
    }

    public sealed record ST_MONITOR_LASER_CONTROL_ROW(
        string CurrentLabel, 
        string CurrentValue, 
        string CurrentUnit, 
        ST_MONITOR_PARAMETER_ROW Setting, 
        string SetUnit, 
        string CommandLabel, 
        string Tone = "Blue", 
        bool CanCommand = true
    )
    {
        public double CommandOpacity
        {
            get
            {
                return CanCommand ? 1.0 : 0.5;
            }
        }

        public Brush CurrentValueBrush
        {
            get
            {
                return Tone switch
                {
                    "Warn" => CStatusBrush.Wait,
                    "Ok" => CStatusBrush.Online,
                    "Error" or "Red" => CStatusBrush.Offline,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }

        public Brush BackgroundBrush
        {
            get
            {
                return Tone switch
                {
                    "Green" => CStatusBrush.CommandGreen,
                    "Red" => CStatusBrush.CommandRed,
                    "Blue" or "Warn" => CStatusBrush.CommandBlue,
                    _ => CStatusBrush.CommandDark
                };
            }
        }

        public Brush BorderBrush
        {
            get
            {
                return Tone switch
                {
                    "Green" => CStatusBrush.CommandGreenBorder,
                    "Red" => CStatusBrush.CommandRedBorder,
                    "Blue" or "Warn" => CStatusBrush.CommandBlueBorder,
                    _ => CStatusBrush.CommandDarkBorder
                };
            }
        }

        public Geometry IconGeometry
        {
            get
            {
                return CMonitorIcon.Get("Move");
            }
        }
    }

    public sealed class ST_MONITOR_PARAMETER_ROW
    {
        public ST_MONITOR_PARAMETER_ROW(string parameter, string value, string unit, string state = "Normal", string commandLabel = "", IReadOnlyList<string>? valueOptions = null)
        {
            Parameter = parameter;
            Value = value;
            Unit = unit;
            State = state;
            CommandLabel = commandLabel;
            ValueOptions = valueOptions ?? [];
        }

        public string Parameter { get; }
        public string Value { get; set; }
        public string Unit { get; }
        public string State { get; }
        public string CommandLabel { get; }

        public bool HasCommand
        {
            get
            {
                return !string.IsNullOrWhiteSpace(CommandLabel);
            }
        }

        public IReadOnlyList<string> ValueOptions { get; }

        public bool UsesSelectionEditor
        {
            get
            {
                return ValueOptions.Count > 0;
            }
        }

        public Brush ValueBrush
        {
            get
            {
                return State switch
                {
                    "Accent" => CStatusBrush.Simul,
                    "Warn" => CStatusBrush.Wait,
                    "Ok" => CStatusBrush.Online,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }
    }

    public sealed class ST_MONITOR_BET_TABLE_ROW : CBindingBase
    {
        private const double DefaultRowBeamSize = 32.64;
        private string _mag;
        private string _div;
        public ST_MONITOR_BET_TABLE_ROW(string no, string description, string mag, string div, string state, bool isSelected = false)
        {
            No = no;
            Description = description;
            _mag = mag;
            _div = div;
            State = state;
            IsSelected = isSelected;
        }

        public string No { get; set; }
        public string Description { get; set; }

        public string Mag
        {
            get
            {
                return _mag;
            }

            set
            {
                if (_mag == value)
                {
                    return;
                }

                _mag = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SpotSize));
            }
        }

        public string Div
        {
            get
            {
                return _div;
            }

            set
            {
                if (_div == value)
                {
                    return;
                }

                _div = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SpotSize));
            }
        }

        public string SpotSize
        {
            get
            {
                return CalculateSpotSizeText(Mag);
            }
        }

        public string State { get; set; }
        public bool IsSelected { get; set; }

        public string MoveCommandLabel
        {
            get
            {
                return $"MOVE TABLE {No}";
            }
        }

        public Brush StateBrush
        {
            get
            {
                return State.Trim().ToUpperInvariant() switch
                {
                    "ACTIVE" or "SELECTED" or "OK" => CStatusBrush.Online,
                    "WARN" => CStatusBrush.Wait,
                    "ERROR" => CStatusBrush.Offline,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }

        public Brush RowBrush
        {
            get
            {
                return IsSelected ? CStatusBrush.Active : CStatusBrush.PrimaryText;
            }
        }

        private static string CalculateSpotSizeText(string mag)
        {
            if (!double.TryParse(mag, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value == 0.0)
            {
                return "0.001000";
            }

            return (DefaultRowBeamSize / value / 1000.0).ToString("F6", CultureInfo.InvariantCulture);
        }
    }

    public sealed record ST_POWER_METER_TUNE_SAMPLE(int Attempt, double AttPosition, double Power, double Error, string Result);
    public sealed record ST_POWER_METER_TUNE_ROW(string Attempt, string AttPosition, string TargetPower, string MeasuredPower, string Error, string Result)
    {
        public Brush ResultBrush
        {
            get
            {
                switch (Result.Trim().ToUpperInvariant())
                {
                    case "OK":
                        return CStatusBrush.Online;
                    case "LOW":
                    case "HIGH":
                        return CStatusBrush.Wait;
                    case "ERROR":
                        return CStatusBrush.Offline;
                    default:
                        return CStatusBrush.PrimaryText;
                }
            }
        }
    }

    public sealed class ST_POWER_METER_TUNE_PROFILE_ROW
    {
        public ST_POWER_METER_TUNE_PROFILE_ROW(string mode, string tolerance, string maxTry, string attRange)
        {
            Mode = mode;
            Tolerance = tolerance;
            MaxTry = maxTry;
            AttRange = attRange;
        }

        public string Mode { get; }
        public string Tolerance { get; }
        public string MaxTry { get; }
        public string AttRange { get; }
    }

    public sealed class ST_PWM_SETTING_ROW(string parameter, string value, string unit, IReadOnlyList<string>? valueOptions = null) : CBindingBase
    {
        private string _value = value;
        public string Parameter { get; } = parameter;

        public string Value
        {
            get
            {
                return _value;
            }

            set
            {
                if (_value == value)
                {
                    return;
                }

                _value = value;
                OnPropertyChanged(nameof(Value));
            }
        }

        public string Unit { get; } = unit;

        public IReadOnlyList<string> ValueOptions
        {
            get
            {
                if (valueOptions is not null)
                {
                    return valueOptions;
                }

                switch (Parameter.ToUpperInvariant())
                {
                    case "TOLERANCE MODE":
                        return["ROUGH", "FINE", "DETAIL"];
                    default:
                        return[];
                }
            }
        }

        public bool UsesSelectionEditor
        {
            get
            {
                return ValueOptions.Count > 0;
            }
        }

        public EN_RECIPE_DATA_TYPE DataType
        {
            get
            {
                switch (Parameter.ToUpperInvariant())
                {
                    case "POWER METER":
                    case "TOLERANCE MODE":
                        return EN_RECIPE_DATA_TYPE.String;
                    case "SETTLE TIME":
                    case "MEASURE COUNT":
                    case "SAMPLE COUNT":
                    case "MEASURE INTERVAL":
                        return EN_RECIPE_DATA_TYPE.Int;
                    default:
                        return EN_RECIPE_DATA_TYPE.Double;
                }
            }
        }

        public Brush ValueBrush
        {
            get
            {
                return Parameter.Contains("POWER", StringComparison.OrdinalIgnoreCase) || Parameter.Contains("ATT", StringComparison.OrdinalIgnoreCase) || Parameter.Contains("MODE", StringComparison.OrdinalIgnoreCase) || Parameter.Contains("WAVELENGTH", StringComparison.OrdinalIgnoreCase) ? CStatusBrush.Wait : CStatusBrush.PrimaryText;
            }
        }
    }

    public sealed record ST_PWM_DEVICE_ROW(string Item, string Value, string Unit, string Command)
    {
        public Brush ValueBrush
        {
            get
            {
                return Item.Contains("POWER", StringComparison.OrdinalIgnoreCase) || Item.Contains("WAVELENGTH", StringComparison.OrdinalIgnoreCase) ? CStatusBrush.Wait : CStatusBrush.PrimaryText;
            }
        }
    }

    public sealed record ST_MONITOR_TREND_POINT(string Time, double PrimaryY, double SecondaryY, double TertiaryY);
    public sealed record ST_MONITOR_SUMMARY_ITEM(string Name, string Value, string Unit, string State = "Normal")
    {
        public Brush ValueBrush
        {
            get
            {
                return State switch
                {
                    "Accent" => CStatusBrush.Simul,
                    "Warn" => CStatusBrush.Wait,
                    "Ok" => CStatusBrush.Online,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }
    }

    public sealed record ST_MONITOR_POSITION_ROW(string Name, string Value, string Unit, string State = "Normal")
    {
        public Brush ValueBrush
        {
            get
            {
                return State switch
                {
                    "Accent" => CStatusBrush.Simul,
                    "Warn" => CStatusBrush.Wait,
                    "Ok" => CStatusBrush.Online,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }
    }

    internal static class CMonitorIcon
    {
        private static readonly IReadOnlyDictionary<string, Geometry> Icons = new Dictionary<string, Geometry>
        {
            ["Laser"] = Icon("M12,2 V8 M12,16 V22 M2,12 H8 M16,12 H22 M5,5 L9,9 M15,15 L19,19 M19,5 L15,9 M9,15 L5,19"),
            ["Gate"] = Icon("M4,8 H20 M4,16 H20 M7,8 V16 M17,8 V16"),
            ["Shutter"] = Icon("M8,4 H16 V20 H8 Z M10,7 H14 M10,17 H14"),
            ["Reset"] = Icon("M18,9 C17,6 15,4 12,4 C8,4 5,7 5,11 C5,15 8,18 12,18 C15,18 17,16 18,14 M18,5 V9 H14"),
            ["Refresh"] = Icon("M18,9 C17,6 15,4 12,4 C8,4 5,7 5,11 C5,15 8,18 12,18 C15,18 17,16 18,14 M18,5 V9 H14"),
            ["Run"] = Icon("M8,5 L20,12 L8,19 Z"),
            ["Stop"] = Icon("M7,7 H17 V17 H7 Z"),
            ["Pump"] = Icon("M7,12 C7,8 10,6 13,7 C17,8 19,12 17,15 C14,19 8,17 7,12 M13,7 V3 M10,20 H16"),
            ["Temp"] = Icon("M10,14 V5 C10,3.9 10.9,3 12,3 C13.1,3 14,3.9 14,5 V14 C15.2,14.7 16,16 16,17.5 C16,19.7 14.2,21 12,21 C9.8,21 8,19.7 8,17.5 C8,16 8.8,14.7 10,14 M12,8 V17"),
            ["Move"] = Icon("M12,3 V21 M3,12 H21 M12,3 L9,6 M12,3 L15,6 M21,12 L18,9 M21,12 L18,15 M12,21 L9,18 M12,21 L15,18 M3,12 L6,9 M3,12 L6,15"),
            ["Home"] = Icon("M4,11 L12,4 L20,11 M6,10 V20 H10 V14 H14 V20 H18 V10"),
            ["Servo"] = Icon("M12,4 A8,8 0 1 1 12,20 A8,8 0 1 1 12,4 M12,8 V12 L16,14"),
            ["Abs"] = Icon("M5,12 H19 M15,8 L19,12 L15,16 M9,8 L5,12 L9,16"),
            ["Rel"] = Icon("M6,6 H12 V12 H18 M18,12 L14,8 M18,12 L14,16"),
            ["Alarm"] = Icon("M12,3 L22,20 H2 Z M12,8 V13 M12,17 V18"),
            ["Measure"] = Icon("M4,18 L9,8 L13,14 L17,5 L20,10 M4,20 H20"),
            ["Wave"] = Icon("M3,12 C5,6 8,6 10,12 C12,18 15,18 17,12 C18,9 20,8 21,8"),
            ["Position"] = Icon("M12,3 A7,7 0 0 1 19,10 C19,15 12,21 12,21 C12,21 5,15 5,10 A7,7 0 0 1 12,3 M12,8 A2,2 0 1 1 12,12 A2,2 0 1 1 12,8"),
            ["Add"] = Icon("M12,5 V19 M5,12 H19"),
            ["Delete"] = Icon("M5,7 H19 M9,7 V5 H15 V7 M8,10 V19 M12,10 V19 M16,10 V19 M7,7 L8,21 H16 L17,7"),
            ["Save"] = Icon("M5,4 H17 L20,7 V20 H4 V4 H5 M8,4 V10 H16 V4 M8,20 V15 H16 V20"),
            ["Edit"] = Icon("M4,17 V20 H7 L18,9 L15,6 L4,17 M14,7 L17,10")
        };
        public static Geometry Get(string icon)
        {
            return Icons.TryGetValue(icon, out var geometry) ? geometry : Geometry.Empty;
        }

        private static Geometry Icon(string data)
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        }
    }
}
