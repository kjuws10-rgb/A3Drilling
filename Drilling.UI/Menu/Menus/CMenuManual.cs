namespace Drilling.UI.Menu.Menus
{
    using System.ComponentModel;
    using System.Globalization;
    using Drilling.Common.Interface;
    using System.Windows.Media;
    using Drilling.Common.Managers;
    using Drilling.Common.InterLock;
    using Drilling.Common.Station;
    using Drilling.Common.Motion;
    using Drilling.Common.Automation;
    using Drilling.Common.Threading;
    using System.IO;
    using Drilling.UI.Popup;
    using System.Windows;
    using System.Text.RegularExpressions;
    using System.Collections.ObjectModel;
    using Drilling.Common.Interface.Vision;
    using Drilling.UI.Menu;
    using System.Diagnostics;
    using Drilling.Common.Recipe;
    using Drilling.Common.Review;
    using Drilling.File.PowerTable;

    // Controller 탭: 축·DIO 스냅샷 표시와 HOME/ENABLE/DISABLE/CLEAR/RESET 명령.
    public sealed partial class CMenuManual : CMenuBase
    {
#region 공유 상태·초기값
        private readonly Dictionary<int, ST_MANUAL_CONTROLLER_HEAD> _controllerHeadCache = [];
        public IReadOnlyList<ST_MANUAL_CONTROLLER_HEAD> ManualControllerHeads { get; private set; } = [];

        private CButtonCommand? _scannerAxisClearFaultCommand;
        private HashSet<(int Controller, string Axis)>? _clearingAxisFaults;
        private CManualHomeSpeedPanel? _homeSpeedPanel;
        private int? _homeSpeedController;
        private CManualHomeSpeedDialog? _homeSpeedDialog;
        private CButtonCommand? _openHomeSpeedSettingsCommand;
        private Func<ST_STATION_PROCESS_STATUS, IReadOnlyDictionary<string, string>, CancellationToken, IReadOnlyList<ST_AUTOMATION_CONTROLLER_STATUS_ITEM>>? _readAutomationStatus;
        private IReadOnlyDictionary<string, string> _automationStatusParameters = new Dictionary<string, string>();
        public IReadOnlyList<ST_AUTOMATION_CONTROLLER_STATUS_ITEM> AutomationControllerStatusItems { get; private set; } = [];

        private bool _mofDisableBusy;
        public CManualMofPreviewPanel MofPreview { get; } = new();

        private const int ManualTaskIndex = 1;
        private const int SettingFilesPageSize = 3;
        private const int UnconfiguredScannerDioNo = -1;
        private static readonly TimeSpan ResetSequenceResetTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ResetSequenceEnableTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ResetSequenceHomeTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ResetSequenceDioTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ResetSequenceCommandTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ResetSequencePollInterval = TimeSpan.FromMilliseconds(500);
        private readonly CManager _manager;
        private readonly CManualScanFileBase _scanFile;
        private readonly CAutomationScriptFileBase _scriptFile;
        private readonly CManualScannerSnapshotReader _scannerStatusSnapshotReader;
        private readonly CManualStageMotionPollingService _stageMotionPollingService;
        private readonly Func<int> _selectedHeadNoProvider;
        private readonly Func<string> _selectedRecipeIdProvider;
        private readonly Func<string> _selectedSettingNameProvider;
        private readonly Action<string> _selectedSettingNameSetter;
        private readonly Action<string> _setStatusMessage;
        private readonly Action _refreshShellStatus;
        private readonly Func<Task> _refreshCurrentScreen;
        private readonly object _menuLifetimeSync = new();
        private CancellationTokenSource _menuLifetimeCancellation = new();
        private CancellationTokenSource? _statusPollingCancellation;
        private string _targetGx = "";
        private string _targetGy = "";
        private string _targetGz = "";
        private int _lastBuiltHeadNo;
        private string _laserState = "OFF";
        private string _centerState = "OFF";
        private string _lastCommand = "-";
        private string _lastResult = "Ready";
        private int? _lastManualScriptAutomationNo;
        private string _lastLoadedSettingName = "";
        private string _lastSettingListSelectedName = "";
        private string _settingFilesPageText = "PAGE 0 / 0";
        private int _settingFilesPageIndex;
        private int _settingFilesPageCount;
        private IReadOnlyList<ST_SYSTEM_PARAMETER> _manualScannerOptionSettings = [];
        private IReadOnlyList<ST_MANUAL_HEAD_GROUP> _headGroups = [];
        private IReadOnlyList<ST_MANUAL_SETTING_FILE> _settingFiles = [];
        private string _shapeSize = "10.000";
        private string _shapeOffsetX = "0.000";
        private string _shapeOffsetY = "0.000";
        private string _shapeDirection = "CW";
        private string _shapeName = "Circle";
        private string _gridRowLines = "5";
        private string _gridColLines = "5";
        private string _laserOnMode = "TIME";
        private string _laserOnTimeMsec = "1000";
        private string _laserShotCount = "1";
        private string _laserRepeatCount = "1";
        private string _laserRepeatDelayTimeMsec = "0";
        private string _laserActionPower = "1.00";
        private string _laserActionFrequency = "20.000";
        private string _dioAddress = "";
        private string _dioAxisName = "-";
        private string _dioNo = "-";
        private string _dioCurrentValue = "UNKNOWN";
        private string _dioLastResult = "Ready";
        private int _dioAutomationNo;
        private long _dioGeneration;
        private IReadOnlyList<ST_MANUAL_SCANNER_AXIS> _scannerAxes = [];
        private bool _hasScannerTargetSnapshot;
        private bool _isDioTargetReady;
        private bool _isDioControlBusy;
        private bool _isScannerWorkspace = true;
        private bool _isReviewWorkspace;
        private ST_MANUAL_STAGE_AXIS _selectedStageAxis = null !;
        private ST_MANUAL_VISION_PROTOCOL_COMMAND _selectedVisionProtocolCommand = null !;
        private string _visionProtocolInspectionType = "0";
        private string _visionProtocolEndpoint = "-";
        private string _visionProtocolStatus = "NOT CONFIGURED";
        private string _visionProtocolKeepAlive = "WAITING";
        private bool _isVisionProtocolBusy;
        public IReadOnlyList<ST_DISPLAY_ITEM> ManualSettings { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> SelectedHeadItems { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> PositionMoveItems { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> ShapeScanItems { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> CommandStateItems { get; private set; } = [];
        public string SelectedHead { get; private set; } = "";
        public string LoadedSettingName { get; private set; } = "";
        public string LoadedSettingPath { get; private set; } = "";
        public IReadOnlyList<ST_MANUAL_HEAD_CARD> HeadCards { get; private set; } = [];
        public IReadOnlyList<ST_MANUAL_PARAMETER> SettingParameters { get; private set; } = [];
        public IReadOnlyList<ST_MANUAL_COMMAND_STATE> CommandStateRows { get; private set; } = [];
        public ObservableCollection<string> VisionProtocolLogs { get; } = [];
        public ObservableCollection<ST_MANUAL_VISION_MARK_SEARCH> VisionProtocolMarkRows { get; } = [];

        private CManualMofAlignPanel? _mofAlign;
        private CManualMofStagePanel? _mofStage;
        private CManualStageMoveOperation? _manualStageMoveOperation;
        public bool IsManualStageMovePending => _manualStageMoveOperation?.CanStop == true;
        private CManualMofRunPanel? _mofRun;
        private readonly Func<(double X, double Y)> _manualMofShiftOffsetProvider;
        private CManualMotionPanel? _motionPanel;
        private CManualMotionSession? _motionSession;
        private readonly HashSet<(int Controller, string Axis)> _manualMotionTargets = [];
        private bool _stoppingMotionJog;
        private bool _motionErrorOpen;
        private CancellationTokenSource? _manualMotionScriptCancellation;
        private (int Controller, int Task)? _manualMotionScriptTask;
        private bool _manualMotionStopPending;
        private CManualScanAssignments? _scanAssignments;
        private IReadOnlyList<string> _manualScanSettingNames = [];
        private CManualShapeScanRun? _activeManualShapeScan;
        private IReadOnlyList<ST_MANUAL_SHAPE_SCAN_PROGRAM>? _manualShapeScanPendingStops;
        private CAttenuatorPositioningService? _manualShapeScanAttenuatorPositioning;
        private bool _manualShapeScanStopInProgress;
        private CManualShapeScanRun? _activeManualLaserAction;
        private bool _manualLaserOffInProgress;
        private bool _manualLaserOffUnconfirmed;
        private IReadOnlyList<ST_MANUAL_LASER_OFF_TARGET> _manualLaserOffPendingTargets = [];
        private string _manualControlTab = "CONTROLLER";
        private readonly HashSet<int> _selectedManualHeadNos = [];
        private bool _manualHeadSelectionInitialized;
        private CButtonCommand _selectFocusedHeadCommand = CButtonCommand.NoOp;
        private CManualPowerMeasurePanel? _powerMeasurePanel;
#endregion
#region 생성·초기화
        public CMenuManual(
            CManager manager, 
            CManualScanFileBase scanFile, 
            CAutomationScriptFileBase scriptFile, 
            Func<int> selectedHeadNoProvider, 
            Func<string> selectedRecipeIdProvider, 
            Func<string> selectedSettingNameProvider, 
            Action<string> selectedSettingNameSetter, 
            CButtonCommand selectHeadCommand, 
            Action<string> setStatusMessage, 
            Action refreshShellStatus, 
            Func<Task> refreshCurrentScreen, 
            Func<(double X, double Y)>? manualMofShiftOffsetProvider = null
        )
        {
            _manager = manager;
            _manualMofShiftOffsetProvider = manualMofShiftOffsetProvider ?? (() => (0, 0));
            _scanFile = scanFile;
            _scriptFile = scriptFile;
            _scannerStatusSnapshotReader = new CManualScannerSnapshotReader(_manager.Automation());
            _stageMotionPollingService = new CManualStageMotionPollingService(_manager.Interface().Melsec);
            _selectedHeadNoProvider = selectedHeadNoProvider;
            _selectedRecipeIdProvider = selectedRecipeIdProvider;
            _selectedSettingNameProvider = selectedSettingNameProvider;
            _selectedSettingNameSetter = selectedSettingNameSetter;
            _setStatusMessage = setStatusMessage;
            _refreshShellStatus = refreshShellStatus;
            _refreshCurrentScreen = refreshCurrentScreen;
            _manager.Interface().ProtocolTrace += OnVisionProtocolTrace;
            _selectFocusedHeadCommand = selectHeadCommand;
            SelectHeadCommand = new CButtonCommand(ToggleManualHead, parameter => _powerMeasurePanel?.IsRunning != true && _mofRun?.IsBusy != true && selectHeadCommand.CanExecute(parameter));
            SelectManualHeadsCommand = new CButtonCommand(SelectManualHeads, _ => _powerMeasurePanel?.IsRunning != true && _mofRun?.IsBusy != true && selectHeadCommand.CanExecute(null));
            selectHeadCommand.CanExecuteChanged += (_, _) => SelectManualHeadsCommand.NotifyCanExecuteChanged();
            SelectManualControlTabCommand = new CButtonCommand(SelectManualControlTab);
            async void SelectSettingCallback(object? parameter)
            {
                await SelectSetting(parameter);
            }

            SelectSettingCommand = new CButtonCommand(SelectSettingCallback);
            async void ShowPreviousSettingPageCallback(object? arg0)
            {
                await ShowPreviousSettingPage();
            }

            bool CanShowPreviousSettingPageCallback(object? arg0)
            {
                return CanShowPreviousSettingPage();
            }

            PreviousSettingPageCommand = new CButtonCommand(ShowPreviousSettingPageCallback, CanShowPreviousSettingPageCallback);
            async void ShowNextSettingPageCallback(object? arg0)
            {
                await ShowNextSettingPage();
            }

            bool CanShowNextSettingPageCallback(object? arg0)
            {
                return CanShowNextSettingPage();
            }

            NextSettingPageCommand = new CButtonCommand(ShowNextSettingPageCallback, CanShowNextSettingPageCallback);
            async void CreateSettingCallback(object? arg0)
            {
                await Create();
            }

            CreateCommand = new CButtonCommand(CreateSettingCallback);
            async void DeleteSettingCallback(object? arg0)
            {
                await Delete();
            }

            DeleteCommand = new CButtonCommand(DeleteSettingCallback);
            async void RenameSettingCallback(object? arg0)
            {
                await Rename();
            }

            RenameCommand = new CButtonCommand(RenameSettingCallback);
            async void SaveSettingCallback(object? arg0)
            {
                await Save();
            }

            SaveCommand = new CButtonCommand(SaveSettingCallback);
            async void CenterMoveCallback(object? arg0)
            {
                await CenterMove();
            }

            CenterMoveCommand = new CButtonCommand(CenterMoveCallback);
            async void PositionMoveCallback(object? arg0)
            {
                await PositionMove();
            }

            PositionMoveCommand = new CButtonCommand(PositionMoveCallback);
            async void ZPositionMoveCallback(object? arg0)
            {
                await ZPositionMove();
            }

            ZPositionMoveCommand = new CButtonCommand(ZPositionMoveCallback);
            async void MoveStopCallback(object? arg0)
            {
                await MoveStop();
            }

            MoveStopCommand = new CButtonCommand(MoveStopCallback);
            async void SelectShapeCallback(object? parameter)
            {
                await SelectShape(parameter);
            }

            SelectShapeCommand = new CButtonCommand(SelectShapeCallback);
            async void ShapeStartCallback(object? arg0)
            {
                await ShapeStart(string.Equals(arg0?.ToString(), "CURPOS", StringComparison.OrdinalIgnoreCase));
            }

            ShapeStartCommand = new CButtonCommand(ShapeStartCallback);
            async void ShapeStopCallback(object? arg0)
            {
                await ShapeStop();
            }

            ShapeStopCommand = new CButtonCommand(ShapeStopCallback);
            async void LaserOnCallback(object? arg0)
            {
                await LaserOn(string.Equals(arg0?.ToString(), "CURPOS", StringComparison.OrdinalIgnoreCase));
            }

            LaserOnCommand = new CButtonCommand(LaserOnCallback);
            async void LaserOffCallback(object? arg0)
            {
                await LaserOff();
            }

            LaserOffCommand = new CButtonCommand(LaserOffCallback);
            async void CenterOnCallback(object? arg0)
            {
                await CenterOn();
            }

            CenterOnCommand = new CButtonCommand(CenterOnCallback);
            async void DioReadCallback(object? arg0)
            {
                await RefreshDioState();
            }

            DioReadCommand = new CButtonCommand(DioReadCallback);
            async void DioOnCallback(object? arg0)
            {
                await SetDioOutput(true);
            }

            DioOnCommand = new CButtonCommand(DioOnCallback);
            async void DioOffCallback(object? arg0)
            {
                await SetDioOutput(false);
            }

            DioOffCommand = new CButtonCommand(DioOffCallback);
            async void ScannerAxisHomeCallback(object? parameter)
            {
                await HomeScannerAxis(parameter);
            }

            ScannerAxisHomeCommand = new CButtonCommand(ScannerAxisHomeCallback);
            async void ScannerAxisEnableCallback(object? parameter)
            {
                await EnableScannerAxis(parameter);
            }

            ScannerAxisEnableCommand = new CButtonCommand(ScannerAxisEnableCallback);
            async void ScannerAxisDisableCallback(object? parameter)
            {
                await DisableScannerAxis(parameter);
            }

            ScannerAxisDisableCommand = new CButtonCommand(ScannerAxisDisableCallback);
            async void ResetAutomationControllerCallback(object? parameter)
            {
                await ResetAutomationController(parameter);
            }

            ResetAutomationControllerCommand = new CButtonCommand(ResetAutomationControllerCallback);
            async void HomeAutomationControllerAxesCallback(object? parameter)
            {
                await HomeAutomationControllerAxes(parameter);
            }

            HomeAutomationControllerAxesCommand = new CButtonCommand(HomeAutomationControllerAxesCallback);
            async void DisableControllerMofCallback(object? parameter) => await DisableControllerMof(parameter);
            DisableControllerMofCommand = new CButtonCommand(DisableControllerMofCallback);
            ShowResetSequenceCommand = new CButtonCommand(ShowResetSequenceDialog);
            SelectManualWorkspaceCommand = new CButtonCommand(SelectManualWorkspace);
            SelectStageAxisCommand = new CButtonCommand(SelectStageAxis);
            StageAbsMoveCommand = new CButtonCommand(StageAbsMove);
            StageRelMoveCommand = new CButtonCommand(StageRelMove);
            StageHomeCommand = new CButtonCommand(StageHome);
            StageStopCommand = new CButtonCommand(StageStop);
            VisionProtocolCommands = [
                new("1000  |  Mark Find Single", "MARK_FIND_SINGLE", "지정한 Shot 1개의 Mark/Hole 위치를 Vision에 요청합니다."), 
                new("1001  |  Flying Ready", "FLYING_READY", "Flying 검사 전에 대상 목록을 전달합니다. 필요한 수만큼 Mark 행을 추가하세요."), 
                new("1002  |  Flying Move Done", "FLYING_MOVE_DONE", "Flying 이동 완료를 Vision에 알립니다. 입력값이 없습니다."), 
                new("1003  |  Mark Find Line", "MARK_FIND_LINE", "한 Line의 Mark 검사 결과를 요청합니다. 입력값이 없습니다."), 
                new("1010  |  Mark Find Setting Single", "MARK_FIND_SETTING_SINGLE", "Setup 시 등록된 Single Mark를 확인합니다. 입력값이 없습니다."), 
                new("1900  |  Keep-Alive Request", "KEEP_ALIVE_REQUEST", "Vision 연결 확인용 Keep-Alive 요청을 전송합니다. 입력값이 없으며 UID는 0입니다.")
            ];
            _selectedVisionProtocolCommand = VisionProtocolCommands[0];
            async void SendVisionProtocolCallback(object? _)
            {
                await SendVisionProtocolCommand();
            }

            VisionProtocolSendCommand = new CButtonCommand(SendVisionProtocolCallback, CanSendVisionProtocol);
            VisionProtocolRefreshCommand = new CButtonCommand(RefreshVisionProtocolCallback);
            VisionProtocolClearLogCommand = new CButtonCommand(ClearVisionProtocolLogCallback);
            VisionProtocolAddMarkCommand = new CButtonCommand(AddVisionProtocolMarkCallback, CanAddVisionProtocolMark);
            VisionProtocolRemoveMarkCommand = new CButtonCommand(RemoveVisionProtocolMarkCallback, CanRemoveVisionProtocolMark);
            ResetVisionProtocolInputs();
            StageAxes = [new("STAGE_Y", "STAGE Y", "mm", "EC_SET_ALIGN_STAGE_Y1_POS_READ", "EC_SET_ALIGN_STAGE_Y1_POS_WRITE"), new("CAMERA_X", "REVIEW X", "mm", "EC_SET_ALIGN_STAGE_X1_POS_READ", "EC_SET_ALIGN_STAGE_X1_POS_WRITE")];
            SelectedStageAxis = StageAxes[0];
            _stageMotionPollingService.SelectAxes(StageAxes);
            bool CanSendVisionProtocol(object? _)
            {
                return !IsVisionProtocolBusy;
            }

            void RefreshVisionProtocolCallback(object? _)
            {
                RefreshVisionProtocolStatus(writeLog: true);
            }

            void ClearVisionProtocolLogCallback(object? _)
            {
                VisionProtocolLogs.Clear();
            }

            void AddVisionProtocolMarkCallback(object? _)
            {
                AddVisionProtocolMarkRow();
            }

            bool CanAddVisionProtocolMark(object? _)
            {
                return VisionProtocolMarkRows.Count < CVisionProtocol.MaximumMarkCount;
            }

            void RemoveVisionProtocolMarkCallback(object? _)
            {
                RemoveVisionProtocolMarkRow();
            }

            bool CanRemoveVisionProtocolMark(object? _)
            {
                return VisionProtocolMarkRows.Count > 0;
            }
        }

#endregion
#region 기본 처리
        public CMenuReview? Review { get; private set; }
        public bool IsReviewWorkspace => _isReviewWorkspace;

        public override EN_MENU Menu
        {
            get
            {
                return EN_MENU.Manual;
            }
        }

        public void StopScannerStatusPolling()
        {
            _manualStageMoveOperation?.Stop();
            if (_manager.Station().ManualMofStatus.IsRunning != true)
                _powerMeasurePanel?.StageMotion.Stop();
            _powerMeasurePanel?.StageMotion.SetActive(false);
            StopStageMotionPolling();
        }

        public string TargetGx
        {
            get
            {
                return _targetGx;
            }

            set
            {
                SetProperty(ref _targetGx, value);
            }
        }

        public string TargetGy
        {
            get
            {
                return _targetGy;
            }

            set
            {
                SetProperty(ref _targetGy, value);
            }
        }

        public string TargetGz
        {
            get
            {
                return _targetGz;
            }

            set
            {
                SetProperty(ref _targetGz, value);
            }
        }

        public bool IsZPositionMoveEnabled
        {
            get
            {
                return IsScannerAxisAvailable(ScannerAxes, "Z");
            }
        }

        public string ShapeSize
        {
            get
            {
                return _shapeSize;
            }

            set
            {
                SetProperty(ref _shapeSize, value);
            }
        }

        public string ShapeOffsetX
        {
            get
            {
                return _shapeOffsetX;
            }

            set
            {
                SetProperty(ref _shapeOffsetX, value);
            }
        }

        public string ShapeOffsetY
        {
            get
            {
                return _shapeOffsetY;
            }

            set
            {
                SetProperty(ref _shapeOffsetY, value);
            }
        }

        public string ShapeDirection
        {
            get
            {
                return _shapeDirection;
            }

            set
            {
                SetProperty(ref _shapeDirection, value);
            }
        }

        public string ShapeName
        {
            get
            {
                return _shapeName;
            }

            set
            {
                SetProperty(ref _shapeName, NormalizeShapeName(value));
            }
        }

        public string GridRowLines
        {
            get
            {
                return _gridRowLines;
            }

            set
            {
                SetProperty(ref _gridRowLines, value);
            }
        }

        public string GridColLines
        {
            get
            {
                return _gridColLines;
            }

            set
            {
                SetProperty(ref _gridColLines, value);
            }
        }

        public string LaserOnMode
        {
            get
            {
                return _laserOnMode;
            }

            set
            {
                var normalizedValue = NormalizeLaserOnMode(value);
                if (SetProperty(ref _laserOnMode, normalizedValue))
                {
                    OnPropertyChanged(nameof(IsLaserTimeMode));
                    OnPropertyChanged(nameof(IsLaserCountMode));
                    RefreshCommandStateRows();
                }
            }
        }

        public bool IsLaserTimeMode
        {
            get
            {
                return IsLaserTimeModeValue(LaserOnMode);
            }
        }

        public bool IsLaserCountMode
        {
            get
            {
                return !IsLaserTimeMode;
            }
        }

        public string LaserOnTimeMsec
        {
            get
            {
                return _laserOnTimeMsec;
            }

            set
            {
                SetProperty(ref _laserOnTimeMsec, value);
            }
        }

        public string LaserShotCount
        {
            get
            {
                return _laserShotCount;
            }

            set
            {
                SetProperty(ref _laserShotCount, value);
            }
        }

        public string LaserRepeatCount { get => _laserRepeatCount; set => SetProperty(ref _laserRepeatCount, value); }
        public string LaserRepeatDelayTimeMsec { get => _laserRepeatDelayTimeMsec; set => SetProperty(ref _laserRepeatDelayTimeMsec, value); }

        public IReadOnlyList<ST_MANUAL_HEAD_GROUP> HeadGroups
        {
            get
            {
                return _headGroups;
            }

            private set
            {
                SetSequenceProperty(ref _headGroups, value);
            }
        }

        public IReadOnlyList<ST_MANUAL_SETTING_FILE> SettingFiles
        {
            get
            {
                return _settingFiles;
            }

            private set
            {
                SetSequenceProperty(ref _settingFiles, value);
            }
        }

        public string SettingFilesPageText
        {
            get
            {
                return _settingFilesPageText;
            }

            private set
            {
                SetProperty(ref _settingFilesPageText, value);
            }
        }

        public IReadOnlyList<ST_MANUAL_SCANNER_AXIS> ScannerAxes
        {
            get
            {
                return _scannerAxes;
            }

            private set
            {
                if (SetSequenceProperty(ref _scannerAxes, value))
                {
                    OnPropertyChanged(nameof(IsZPositionMoveEnabled));
                }
            }
        }

        public IReadOnlyList<ST_MANUAL_STAGE_AXIS> StageAxes { get; }

        public ST_MANUAL_STAGE_AXIS SelectedStageAxis
        {
            get
            {
                return _selectedStageAxis;
            }

            private set
            {
                if (SetProperty(ref _selectedStageAxis, value))
                {
                    RefreshStageAxisSelection();
                }
            }
        }

        public string DioAddress
        {
            get
            {
                return _dioAddress;
            }

            private set
            {
                SetProperty(ref _dioAddress, value);
            }
        }

        public string DioAxisName
        {
            get
            {
                return _dioAxisName;
            }

            private set
            {
                SetProperty(ref _dioAxisName, value);
            }
        }

        public string DioNo
        {
            get
            {
                return _dioNo;
            }

            private set
            {
                SetProperty(ref _dioNo, value);
            }
        }

        public string DioCurrentValue
        {
            get
            {
                return _dioCurrentValue;
            }

            private set
            {
                if (SetProperty(ref _dioCurrentValue, value))
                {
                    OnPropertyChanged(nameof(IsDioOn));
                    OnPropertyChanged(nameof(IsDioOff));
                }
            }
        }

        public bool IsDioOn
        {
            get
            {
                return DioCurrentValue.Equals("ON", StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool IsDioOff
        {
            get
            {
                return DioCurrentValue.Equals("OFF", StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool IsDioTargetReady
        {
            get
            {
                return _isDioTargetReady;
            }

            private set
            {
                if (SetProperty(ref _isDioTargetReady, value))
                {
                    OnPropertyChanged(nameof(IsDioControlEnabled));
                }
            }
        }

        public bool IsDioControlEnabled
        {
            get
            {
                return IsDioTargetReady && !IsDioControlBusy;
            }
        }

        public string DioLastResult
        {
            get
            {
                return _dioLastResult;
            }

            private set
            {
                SetProperty(ref _dioLastResult, value);
            }
        }

        public bool IsDioControlBusy
        {
            get
            {
                return _isDioControlBusy;
            }

            private set
            {
                if (SetProperty(ref _isDioControlBusy, value))
                {
                    OnPropertyChanged(nameof(IsDioControlEnabled));
                }
            }
        }

        public bool IsScannerWorkspace
        {
            get
            {
                return _isScannerWorkspace;
            }

            private set
            {
                if (SetProperty(ref _isScannerWorkspace, value))
                {
                    OnPropertyChanged(nameof(IsStageVisionWorkspace));
                }
            }
        }

        public bool IsStageVisionWorkspace
        {
            get
            {
                return !IsScannerWorkspace && !IsReviewWorkspace;
            }
        }

        public IReadOnlyList<ST_MANUAL_VISION_PROTOCOL_COMMAND> VisionProtocolCommands { get; }

        public ST_MANUAL_VISION_PROTOCOL_COMMAND SelectedVisionProtocolCommand
        {
            get
            {
                return _selectedVisionProtocolCommand;
            }

            set
            {
                if (value is not null && SetProperty(ref _selectedVisionProtocolCommand, value))
                {
                    ResetVisionProtocolInputs();
                }
            }
        }

        public string VisionProtocolCommandDescription
        {
            get
            {
                return SelectedVisionProtocolCommand.Description;
            }
        }

        public bool VisionProtocolUsesMarkRows
        {
            get
            {
                return SelectedVisionProtocolCommand.CommandText is "MARK_FIND_SINGLE" or "FLYING_READY";
            }
        }

        public bool VisionProtocolIsFlyingReady
        {
            get
            {
                return SelectedVisionProtocolCommand.CommandText == "FLYING_READY";
            }
        }

        public bool VisionProtocolHasNoArguments
        {
            get
            {
                return !VisionProtocolUsesMarkRows;
            }
        }

        public string VisionProtocolInspectionType
        {
            get
            {
                return _visionProtocolInspectionType;
            }

            set
            {
                if (SetProperty(ref _visionProtocolInspectionType, value ?? ""))
                {
                    OnPropertyChanged(nameof(VisionProtocolRequestPreview));
                }
            }
        }

        public string VisionProtocolRequestPreview
        {
            get
            {
                return BuildVisionProtocolFunction(validate: false);
            }
        }

        public string VisionProtocolMarkRowCountText
        {
            get
            {
                return $"MARK {VisionProtocolMarkRows.Count} / {CVisionProtocol.MaximumMarkCount}";
            }
        }

        public string VisionProtocolEndpoint
        {
            get
            {
                return _visionProtocolEndpoint;
            }

            private set
            {
                SetProperty(ref _visionProtocolEndpoint, value);
            }
        }

        public string VisionProtocolStatus
        {
            get
            {
                return _visionProtocolStatus;
            }

            private set
            {
                SetProperty(ref _visionProtocolStatus, value);
            }
        }

        public string VisionProtocolKeepAlive
        {
            get
            {
                return _visionProtocolKeepAlive;
            }

            private set
            {
                SetProperty(ref _visionProtocolKeepAlive, value);
            }
        }

        public bool IsVisionProtocolBusy
        {
            get
            {
                return _isVisionProtocolBusy;
            }

            private set
            {
                if (SetProperty(ref _isVisionProtocolBusy, value))
                {
                    VisionProtocolSendCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public CButtonCommand SelectHeadCommand { get; }
        public CButtonCommand SelectSettingCommand { get; }
        public CButtonCommand PreviousSettingPageCommand { get; }
        public CButtonCommand NextSettingPageCommand { get; }
        public CButtonCommand CreateCommand { get; }
        public CButtonCommand DeleteCommand { get; }
        public CButtonCommand RenameCommand { get; }
        public CButtonCommand SaveCommand { get; }
        public CButtonCommand CenterMoveCommand { get; }
        public CButtonCommand PositionMoveCommand { get; }
        public CButtonCommand ZPositionMoveCommand { get; }
        public CButtonCommand MoveStopCommand { get; }
        public CButtonCommand SelectShapeCommand { get; }
        public CButtonCommand ShapeStartCommand { get; }
        public CButtonCommand ShapeStopCommand { get; }
        public CButtonCommand LaserOnCommand { get; }
        public CButtonCommand LaserOffCommand { get; }
        public CButtonCommand CenterOnCommand { get; }
        public CButtonCommand DioReadCommand { get; }
        public CButtonCommand DioOnCommand { get; }
        public CButtonCommand DioOffCommand { get; }
        public CButtonCommand ScannerAxisHomeCommand { get; }
        public CButtonCommand ScannerAxisEnableCommand { get; }
        public CButtonCommand ScannerAxisDisableCommand { get; }
        public CButtonCommand ResetAutomationControllerCommand { get; }
        public CButtonCommand HomeAutomationControllerAxesCommand { get; }
        public CButtonCommand DisableControllerMofCommand { get; }
        public CButtonCommand ShowResetSequenceCommand { get; }
        public CButtonCommand SelectManualWorkspaceCommand { get; }
        public CButtonCommand SelectStageAxisCommand { get; }
        public CButtonCommand StageAbsMoveCommand { get; }
        public CButtonCommand StageRelMoveCommand { get; }
        public CButtonCommand StageHomeCommand { get; }
        public CButtonCommand StageStopCommand { get; }
        public CButtonCommand VisionProtocolSendCommand { get; }
        public CButtonCommand VisionProtocolRefreshCommand { get; }
        public CButtonCommand VisionProtocolClearLogCommand { get; }
        public CButtonCommand VisionProtocolAddMarkCommand { get; }
        public CButtonCommand VisionProtocolRemoveMarkCommand { get; }

        public override void OnActivated()
        {
            lock (_menuLifetimeSync)
            {
                if (_menuLifetimeCancellation.IsCancellationRequested)
                {
                    _menuLifetimeCancellation.Dispose();
                    _menuLifetimeCancellation = new CancellationTokenSource();
                }
            }
        }

        public override void OnDeactivated()
        {
            lock (_menuLifetimeSync)
            {
                _menuLifetimeCancellation.Cancel();
                _statusPollingCancellation?.Cancel();
                _statusPollingCancellation?.Dispose();
                _statusPollingCancellation = null;
            }
        }

        public override async Task<CScreenViewModel> Build(CancellationToken cancellationToken = default)
        {
            if (IsReviewWorkspace && Review is not null)
            {
                await Review.Build(cancellationToken);
                return new CScreenViewModel(EN_MENU.Manual, "MANUAL / CONTROL", "Review inspection operation.", [], [], manual: this);
            }

            var settingNames = await _scanFile.List(cancellationToken);
            var orderedSettingNames = OrderSettingNames(settingNames);
            _manualScanSettingNames = orderedSettingNames;
            var formItems = await _scanFile.LoadForm(cancellationToken);
            var selectedSettingName = ResolveSelectedSettingName(orderedSettingNames, _selectedSettingNameProvider());
            var settings = await _scanFile.Load(selectedSettingName, cancellationToken);
            SyncSettingFilesPage(orderedSettingNames, selectedSettingName);
            var selectedHeadNoRaw = _selectedHeadNoProvider();
            var optionSettings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option, cancellationToken);
            _manualScannerOptionSettings = optionSettings;
            var selectedHeadNo = NormalizeManualScannerHeadNo(optionSettings, Math.Clamp(selectedHeadNoRaw, 1, 8));
            var scannerAxes = ReadManualScannerAxesSnapshot(optionSettings, selectedHeadNo);
            var headCards = ApplyManualHeadSelection(BuildHeadCards(selectedHeadNo, optionSettings, scannerAxes), selectedHeadNo);
            var selectedRecipe = await LoadSelectedRecipe(cancellationToken);
            MofPreview.Update(selectedRecipe, optionSettings);
            MofStage.UpdateTargets(selectedRecipe, _manager.Setting().LoadSection(EN_SETTING_TAB.Motor, cancellationToken));
            MofRun.SetInputs(selectedRecipe, optionSettings, SelectedManualHeadNos);
            SetAutomationStatusInputs(selectedRecipe, optionSettings);
            RefreshMofStageSnapshot();
            RefreshAutomationStatusSnapshot();
            bool IsSelectedHeadCard(global::Drilling.UI.Menu.Menus.ST_MANUAL_HEAD_CARD head)
            {
                return head.HeadNo == selectedHeadNo;
            }

            var selectedHead = headCards.First(IsSelectedHeadCard);
            var xAxisReference = ReadScannerAxisReference(scannerAxes, "X", "X");
            var yAxisReference = ReadScannerAxisReference(scannerAxes, "Y", "Y");
            var zAxisReference = ReadScannerAxisReference(scannerAxes, "Z", "Z");
            var isZAxisAvailable = IsScannerAxisAvailable(scannerAxes, "Z");
            var zTargetDefault = isZAxisAvailable ? FormatDouble(ReadHeadRecipeDouble(selectedRecipe, selectedHeadNo, 0.0, "DOE_Z_POSITION"), 3) : "NOT USE";
            var shouldRefreshPositionTargets = _lastBuiltHeadNo != selectedHeadNo || string.IsNullOrWhiteSpace(TargetGx) || string.IsNullOrWhiteSpace(TargetGy);
            if (shouldRefreshPositionTargets)
            {
                TargetGx = ReadScannerAxisTarget(scannerAxes, "X");
                TargetGy = ReadScannerAxisTarget(scannerAxes, "Y");
                _lastBuiltHeadNo = selectedHeadNo;
                _hasScannerTargetSnapshot = HasReadableScannerPosition(scannerAxes);
            }

            if (!isZAxisAvailable)
            {
                TargetGz = "NOT USE";
            }
            else if (shouldRefreshPositionTargets || string.IsNullOrWhiteSpace(TargetGz) || TargetGz.Equals("NOT USE", StringComparison.OrdinalIgnoreCase))
            {
                TargetGz = ReadScannerAxisTarget(scannerAxes, "Z", zTargetDefault);
            }

            if (!_lastLoadedSettingName.Equals(selectedSettingName, StringComparison.OrdinalIgnoreCase))
            {
                ShapeSize = FormatDouble(settings.ShapeSize, 3);
                ShapeOffsetX = FormatDouble(settings.OffsetX, 3);
                ShapeOffsetY = FormatDouble(settings.OffsetY, 3);
                ShapeDirection = string.IsNullOrWhiteSpace(settings.Direction) ? "CW" : settings.Direction;
                ShapeName = settings.ShapeName;
                GridRowLines = settings.GridRowLines.ToString(CultureInfo.InvariantCulture);
                GridColLines = settings.GridColLines.ToString(CultureInfo.InvariantCulture);
                _lastLoadedSettingName = selectedSettingName;
            }

            var dioGeneration = BeginDioTargetRefresh("Loading");
            try
            {
                var dioTarget = await ResolveDioTarget(selectedHeadNo, cancellationToken);
                if (IsCurrentDioGeneration(dioGeneration))
                {
                    ApplyDioTarget(dioTarget);
                }
            }
            catch (Exception exception)when (exception is InvalidOperationException or InvalidDataException or IOException or TimeoutException or KeyNotFoundException)
            {
                if (IsCurrentDioGeneration(dioGeneration))
                {
                    InvalidateDioTarget($"ERROR: {exception.Message}");
                }
            }

            Apply(
                BuildManualSettings(settings), 
                BuildSelectedHeadItems(selectedHead), 
                [
                new("Center Move", "Ready"), 
                new("Position Move", $"{xAxisReference}/{yAxisReference} Target"), 
                new("Z Move", $"{zAxisReference} Target"), 
                new("Move Stop", "Ready"), 
                new($"{xAxisReference} Target", TargetGx, "mm"), 
                new($"{yAxisReference} Target", TargetGy, "mm"), 
                new($"{zAxisReference} Target", TargetGz, "mm")
            ], 
                [
                new("Shape Size", ShapeSize, "mm"), 
                new("Offset X", ShapeOffsetX, "mm"), 
                new("Offset Y", ShapeOffsetY, "mm"), 
                new("Direction", ShapeDirection), 
                new("Shape", ShapeName), 
                new("Grid Row Lines", GridRowLines, "ea"), 
                new("Grid Col Lines", GridColLines, "ea"), 
                new("Start", "Ready"), 
                new("Stop", "Ready")
            ], 
                [new("Laser", _laserState), new("CENTER", _centerState), new("Last Command", _lastCommand), new("Result", _lastResult)], 
                selectedHead.HeadName, 
                selectedSettingName, 
                $@"Config\Manual\{selectedSettingName}", 
                headCards, 
                BuildSettingFiles(orderedSettingNames, selectedSettingName, _settingFilesPageIndex), 
                BuildSettingParameters(settings, formItems), 
                BuildCommandStateRows(selectedHead), 
                scannerAxes
            );
            RefreshManualControllerHeads();
            RefreshVisionProtocolStatus(writeLog: false);
            return new CScreenViewModel(
                EN_MENU.Manual, 
                "MANUAL / CONTROL", 
                "Scanner, stage and protocol test operation.", 
                [new("Selected Head", selectedHead.HeadName), new("Mode", "Manual"), new("Laser", _laserState)], 
                [new("Manual Setting", BuildManualSettings(settings)), new("Position Move", [new("Center Move", "Ready"), new("Position Move", $"{xAxisReference}/{yAxisReference} target input"), new("Z Move", $"{zAxisReference} target input"), new("Move Stop", "Ready")])], 
                manual: this
            );
        }

        private static int ReadOptionalHeadSettingInt(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int defaultValue, int headNo, params string[] suffixes)
        {
            return TryFindHeadSetting(settings, headNo, suffixes, out var key, out var value) ? ParseHeadSettingInt(headNo, key, value) : defaultValue;
        }

        private static int ReadRequiredHeadSettingInt(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo, params string[] suffixes)
        {
            if (!TryFindHeadSetting(settings, headNo, suffixes, out var key, out var value))
            {
                throw new InvalidOperationException($"H{headNo:00} scanner DIO number is not configured.");
            }

            return ParseHeadSettingInt(headNo, key, value);
        }

        private static bool TryFindHeadSetting(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo, IReadOnlyList<string> suffixes, out string key, out string value)
        {
            var prefix = $"H{headNo:00}_";
            foreach (var suffix in suffixes)
            {
                var searchKey = prefix + suffix;
                bool MatchesHeadSettingKey(global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER item)
                {
                    return searchKey.Equals(item.Key, StringComparison.OrdinalIgnoreCase) || searchKey.Equals(item.Name, StringComparison.OrdinalIgnoreCase);
                }

                var setting = settings.FirstOrDefault(MatchesHeadSettingKey);
                if (setting is not null)
                {
                    key = searchKey;
                    value = setting.Value;
                    return true;
                }
            }

            key = "";
            value = "";
            return false;
        }

        private static int ParseHeadSettingInt(int headNo, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"H{headNo:00} setting {key} is empty.");
            }

            if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new InvalidOperationException($"H{headNo:00} setting {key} must be an integer. Value={value}");
            }

            return parsed;
        }

        private static string ReadHeadSettingText(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, string defaultValue, int headNo, params string[] suffixes)
        {
            var prefix = $"H{headNo:00}_";
            foreach (var suffix in suffixes)
            {
                var key = prefix + suffix;
                bool MatchesHeadSettingKey(global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER item)
                {
                    return key.Equals(item.Key, StringComparison.OrdinalIgnoreCase) || key.Equals(item.Name, StringComparison.OrdinalIgnoreCase);
                }

                var setting = settings.FirstOrDefault(MatchesHeadSettingKey);
                if (setting is not null && !string.IsNullOrWhiteSpace(setting.Value))
                {
                    return setting.Value.Trim();
                }
            }

            return defaultValue;
        }

        private bool TryBlockManualAutomationCommand(string commandName)
        {
            var recovery = commandName.Contains("STOP", StringComparison.OrdinalIgnoreCase) || commandName.EndsWith("OFF", StringComparison.OrdinalIgnoreCase) || commandName.Contains("DISABLE", StringComparison.OrdinalIgnoreCase) || commandName.Contains("FAULT_CLEAR", StringComparison.OrdinalIgnoreCase) || commandName.EndsWith("_RESET", StringComparison.OrdinalIgnoreCase);
            if (!recovery && !CheckOperationAlarm(commandName))
                return true;
            // The move delegate checks this guard after claiming its own pending state.
            // Other starts must wait until the move (including abort cleanup) has finished.
            if (!recovery && !commandName.Equals("STAGE_REVIEW_MOVE", StringComparison.OrdinalIgnoreCase)
                && (IsManualStageMovePending || _manager.Interface().StageReviewSequences.IsActive))
            {
                ShowManualWarning("Stage/Review 이동이 실행 중이거나 정리 중입니다. 이동 완료 또는 STOP 정리 완료 후 실행해 주세요.");
                return true;
            }
            if (_manager.Station().ManualMofStatus is { IsRunning: true } or { RequiresStop: true })
            {
                ShowManualWarning("MOF 가공이 실행 중이거나 정지가 확인되지 않았습니다. MOF STOP 후 실행해 주세요.");
                return true;
            }

            if (IsManualMotionScriptActive && commandName is not "MANUAL_MOTION" and not "MOVE_STOP")
            {
                ShowManualWarning("수동 이동 스크립트가 실행 중이거나 정지가 확인되지 않았습니다. 완료 또는 MOVE STOP 후 실행해 주세요.");
                return true;
            }

            // A faulted Task can require RESET before STOP can be verified on retry.
            var canRecoverStoppedScan = _activeManualShapeScan is null && !_manualShapeScanStopInProgress && _activeManualLaserAction is null && !_manualLaserOffInProgress && (commandName.Equals("SCANNER_FAULT_CLEAR", StringComparison.OrdinalIgnoreCase) || commandName.StartsWith("CONTROLLER_", StringComparison.OrdinalIgnoreCase) && commandName.EndsWith("_RESET", StringComparison.OrdinalIgnoreCase));
            if (IsManualShapeScanActive && !canRecoverStoppedScan && !commandName.Equals("SHAPE_START", StringComparison.OrdinalIgnoreCase))
            {
                const string scanMessage = "수동 가공이 진행 중이거나 정지가 확인되지 않았습니다. LASER OFF 또는 Scan STOP으로 정지한 후 실행해 주세요.";
                _setStatusMessage(scanMessage);
                ShowManualWarning(scanMessage);
                return true;
            }

            if (!IsAutoProcessRunning())
            {
                return false;
            }

            var message = "Auto process is running. Manual Automation1 command is blocked.";
            _lastCommand = commandName;
            _lastResult = message;
            RefreshCommandStateRows();
            _setStatusMessage(message);
            _refreshShellStatus();
            return true;
        }

        private bool IsAutoProcessRunning()
        {
            try
            {
                var snapshot = _manager.Station().GetStatus(CancellationToken.None);
                return snapshot.ScriptStatus == EN_SCRIPT_STATUS.Running || snapshot.ProcessStep is EN_PROCESS_STEP.PowerCheck or EN_PROCESS_STEP.Align or EN_PROCESS_STEP.Process or EN_PROCESS_STEP.Inspection;
            }
            catch
            {
                return false;
            }
        }

        private static bool ContainsAutomationFailureToken(string response)
        {
            var tokens = response.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < tokens.Length; index++)
            {
                if (IsAutomationFailureToken(tokens[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private static string FormatAutomationFailureResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return "<empty>";
            }

            var fields = response.Trim().Split(':', 4, StringSplitOptions.TrimEntries);
            if (fields.Length >= 4 && fields[0].Equals("ERROR", StringComparison.OrdinalIgnoreCase) && fields[1].Equals("AUTOMATION1", StringComparison.OrdinalIgnoreCase))
            {
                return $"{fields[2]}: {fields[3]}";
            }

            return response.Trim();
        }

        private static bool IsAutomationFailureToken(string token)
        {
            return token.Equals("ERROR", StringComparison.OrdinalIgnoreCase) || token.Equals("ERR", StringComparison.OrdinalIgnoreCase) || token.Equals("FAIL", StringComparison.OrdinalIgnoreCase) || token.Equals("FAILED", StringComparison.OrdinalIgnoreCase) || token.Equals("NG", StringComparison.OrdinalIgnoreCase) || token.Equals("NAK", StringComparison.OrdinalIgnoreCase);
        }

        private ST_MANUAL_SCAN_PARAM ReadManualParamFromScreen()
        {
            var settings = new ST_MANUAL_SCAN_PARAM(
                ReadManualDouble("ShapeSize"), 
                ReadManualDouble("OffsetX"), 
                ReadManualDouble("OffsetY"), 
                ReadManualText(FindManualParameter("Direction")), 
                ReadManualText(FindManualParameter("ShapeName")), 
                ReadManualDouble("LaserPower"), 
                ReadManualDouble("JumpSpeed"), 
                ReadManualDouble("MarkSpeed"), 
                0.0, 
                ReadManualDouble("LaserFrequency"), 
                ReadManualDouble("LaserOnDelay"), 
                ReadManualDouble("LaserOffDelay"), 
                ReadManualDouble("Time"), 
                ReadManualInt("Count"), 
                ReadManualInt("GridRowLines"), 
                ReadManualInt("GridColLines"), 
                ReadManualText(FindManualParameter("LaserOnMode")), 
                ReadManualInt("RepeatCount"), 
                ReadManualDouble("RepeatDelayTime")
            );
            ValidateManualScanParameters(settings, []);
            return settings;
        }

        private void Apply(
            IReadOnlyList<ST_DISPLAY_ITEM> manualSettings, 
            IReadOnlyList<ST_DISPLAY_ITEM> selectedHeadItems, 
            IReadOnlyList<ST_DISPLAY_ITEM> positionMoveItems, 
            IReadOnlyList<ST_DISPLAY_ITEM> shapeScanItems, 
            IReadOnlyList<ST_DISPLAY_ITEM> commandStateItems, 
            string selectedHead, 
            string loadedSettingName, 
            string loadedSettingPath, 
            IReadOnlyList<ST_MANUAL_HEAD_CARD> headCards, 
            IReadOnlyList<ST_MANUAL_SETTING_FILE> settingFiles, 
            IReadOnlyList<ST_MANUAL_PARAMETER> settingParameters, 
            IReadOnlyList<ST_MANUAL_COMMAND_STATE> commandStateRows, 
            IReadOnlyList<ST_MANUAL_SCANNER_AXIS> scannerAxes
        )
        {
            ManualSettings = manualSettings;
            SelectedHeadItems = selectedHeadItems;
            PositionMoveItems = positionMoveItems;
            ShapeScanItems = shapeScanItems;
            CommandStateItems = commandStateItems;
            SelectedHead = selectedHead;
            LoadedSettingName = loadedSettingName;
            LoadedSettingPath = loadedSettingPath;
            HeadCards = headCards;
            HeadGroups = BuildHeadGroups(headCards);
            SettingFiles = settingFiles;
            SettingParameters = settingParameters;
            CommandStateRows = commandStateRows;
            ScannerAxes = scannerAxes;
        }

        private static IReadOnlyList<ST_DISPLAY_ITEM> BuildManualSettings(ST_MANUAL_SCAN_PARAM settings)
        {
            return[
                new("Laser Power", FormatDouble(settings.LaserPower, 2), "W"), 
                new("Jump Speed", FormatDouble(settings.JumpSpeed, 3), "m/sec"), 
                new("Mark Speed", FormatDouble(settings.MarkSpeed, 3), "m/sec"), 
                new("Laser Frequency", FormatDouble(settings.LaserFrequency, 3), "kHz"), 
                new("Laser On Delay", FormatDouble(settings.LaserOnDelay, 3), "msec"), 
                new("Laser Off Delay", FormatDouble(settings.LaserOffDelay, 3), "msec")
            ];
        }

        private int ReadEnabledManualScannerHeadNo(CancellationToken cancellationToken)
        {
            var optionSettings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option, cancellationToken);
            var enabledHeadNos = CScannerStatusSnapshotReader.BuildEnabledHeadNumbers(optionSettings);
            if (enabledHeadNos.Count == 0)
            {
                throw new InvalidOperationException("No manual scanner head is enabled. Check Hxx_USE option settings.");
            }

            var selectedHeadNo = Math.Clamp(_selectedHeadNoProvider(), 1, 8);
            return enabledHeadNos.Contains(selectedHeadNo) ? selectedHeadNo : enabledHeadNos[0];
        }

        private static int ReadManualHeadAutomationNo(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo)
        {
            return ReadOptionalHeadSettingInt(settings, headNo <= 4 ? 0 : 1, headNo, "AUTOMATION_NO");
        }

        private static int ReadManualHeadAutomationTaskNo(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo)
        {
            var defaultTaskNo = ((headNo - 1) % 4) + 1;
            return Math.Max(1, ReadOptionalHeadSettingInt(settings, defaultTaskNo, headNo, "AUTOMATION_TASK_NO"));
        }

        private static string FormatControllerHomeRunSummary(IReadOnlyList<ST_CONTROLLER_HOME_RUN> runItems)
        {
            var items = new List<string>(runItems.Count);
            foreach (var runItem in runItems)
            {
                items.Add($"T{runItem.TaskNo}:{runItem.ScriptFileName}");
            }

            return string.Join(", ", items);
        }

        private static void ValidateManualScanParameters(ST_MANUAL_SCAN_PARAM settings, IReadOnlyList<ST_MANUAL_SCAN_FORM> formItems)
        {
            if (!double.IsFinite(settings.ShapeSize) || settings.ShapeSize <= 0)
                throw new InvalidDataException("Shape Size는 0보다 큰 유한한 값이어야 합니다.");
            if (!ST_MANUAL_PARAMETER.ShapeOptions.Contains(settings.ShapeName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"지원하지 않는 Shape Type입니다: {settings.ShapeName}");
            if (!ST_MANUAL_PARAMETER.DirectionOptions.Contains(settings.Direction, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Direction은 CW 또는 CCW여야 합니다.");
            if (settings.GridRowLines < 2 || settings.GridColLines < 2)
                throw new InvalidDataException("Grid Row Count / Col Count는 2 이상의 정수여야 합니다.");
            if (!ST_MANUAL_PARAMETER.LaserModeOptions.Contains(settings.LaserOnMode, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Mode는 TIME 또는 COUNT여야 합니다.");
            if (!double.IsFinite(settings.Time) || settings.Time <= 0)
                throw new InvalidDataException("On Time은 0보다 큰 유한한 값이어야 합니다. (msec)");
            if (settings.Count < 1 || settings.RepeatCount < 1)
                throw new InvalidDataException("Shot Count / Repeat Count는 1 이상의 정수여야 합니다.");
            if (!double.IsFinite(settings.RepeatDelayTime) || settings.RepeatDelayTime < 0)
                throw new InvalidDataException("Repeat Delay Time은 0 이상의 유한한 값이어야 합니다. (msec)");
            foreach (var value in new[]
            {
                settings.OffsetX,
                settings.OffsetY,
                settings.LaserPower,
                settings.JumpSpeed,
                settings.MarkSpeed,
                settings.LaserFrequency,
                settings.LaserOnDelay,
                settings.LaserOffDelay
            }

            )
                if (!double.IsFinite(value))
                    throw new InvalidDataException("Scan Parameter는 유한한 숫자여야 합니다.");
            if (settings.LaserPower < 0 || settings.JumpSpeed <= 0 || settings.MarkSpeed <= 0 || settings.LaserFrequency <= 0 || settings.LaserOnDelay < 0 || settings.LaserOffDelay < 0)
                throw new InvalidDataException("레이저 Power·Delay는 0 이상, Speed·Frequency는 0보다 커야 합니다.");
            var numericValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["ShapeSize"] = settings.ShapeSize,
                ["OffsetX"] = settings.OffsetX,
                ["OffsetY"] = settings.OffsetY,
                ["GridRowLines"] = settings.GridRowLines,
                ["GridColLines"] = settings.GridColLines,
                ["LaserPower"] = settings.LaserPower,
                ["JumpSpeed"] = settings.JumpSpeed,
                ["MarkSpeed"] = settings.MarkSpeed,
                ["LaserFrequency"] = settings.LaserFrequency,
                ["LaserOnDelay"] = settings.LaserOnDelay,
                ["LaserOffDelay"] = settings.LaserOffDelay,
                ["Time"] = settings.Time,
                ["Count"] = settings.Count,
                ["RepeatCount"] = settings.RepeatCount,
                ["RepeatDelayTime"] = settings.RepeatDelayTime
            };
            foreach (var parameter in BuildSettingParameters(settings, formItems))
                if (parameter.DataType is EN_RECIPE_DATA_TYPE.Double or EN_RECIPE_DATA_TYPE.Int)
                    ValidateManualParameterRange(parameter, numericValues.TryGetValue(parameter.Key, out var value) ? value : ReadRequiredDouble(parameter.Value, parameter.Parameter));
        }

        private IReadOnlyList<ST_MANUAL_COMMAND_STATE> BuildCommandStateRows(ST_MANUAL_HEAD_CARD selectedHead)
        {
            void SetLaserActionPower(string value)
            {
                _laserActionPower = value;
            }

            void SetLaserActionFrequency(string value)
            {
                _laserActionFrequency = value;
            }

            void SetLaserOnMode(string value)
            {
                LaserOnMode = value;
            }

            void SetLaserOnTimeMsec(string value)
            {
                LaserOnTimeMsec = value;
            }

            void SetLaserShotCount(string value)
            {
                LaserShotCount = value;
            }

            return[
                new("Selected Head", selectedHead.HeadName), 
                new("Laser Power", _laserActionPower, "W", true, SetLaserActionPower), 
                new("Frequency", _laserActionFrequency, "kHz", true, SetLaserActionFrequency), 
                new("Laser On Mode", LaserOnMode, "-", true, SetLaserOnMode, NormalizeLaserOnMode), 
                new("Laser On Time", LaserOnTimeMsec, "msec", true, SetLaserOnTimeMsec), 
                new("Shot Count", LaserShotCount, "count", true, SetLaserShotCount)
            ];
        }

        private double ReadManualDouble(string parameterName)
        {
            var row = FindManualParameter(parameterName);
            var value = ReadManualText(row);
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result))
            {
                ValidateManualParameterRange(row, result);
                return result;
            }

            throw new InvalidDataException($"{parameterName} value is not a valid number: {value}");
        }

        private static double ReadRequiredDouble(string value, string name)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }

            throw new InvalidDataException($"{name} is not a valid number: {value}");
        }

        private static void ShowManualWarning(string message)
        {
            MessageBox.Show(GetActiveWindow(), message, "수동 스캔 경고", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static Window? GetActiveWindow()
        {
            return Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
        }

        private static string FormatDouble(double value, int decimals)
        {
            var format = decimals <= 0 ? "0" : $"0.{new string ('0', decimals)}";
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        private async Task<ST_RECIPE_DEFINITION?> LoadSelectedRecipe(CancellationToken cancellationToken)
        {
            var selectedRecipeId = (_selectedRecipeIdProvider() ?? "").Trim();
            var recipes = await _manager.Recipe().LoadRecipes(cancellationToken);
            if (!string.IsNullOrWhiteSpace(selectedRecipeId))
            {
                foreach (var recipe in recipes)
                {
                    if (recipe.Id.Equals(selectedRecipeId, StringComparison.OrdinalIgnoreCase))
                    {
                        return recipe;
                    }
                }
            }

            foreach (var recipe in recipes)
            {
                if (recipe.Id.Equals("DRILL_A01", StringComparison.OrdinalIgnoreCase))
                {
                    return recipe;
                }
            }

            return recipes.Count > 0 ? recipes[0] : null;
        }

        private static double ReadHeadRecipeDouble(ST_RECIPE_DEFINITION? recipe, int headNo, double defaultValue, params string[] suffixes)
        {
            if (recipe is null)
            {
                return defaultValue;
            }

            var prefix = $"H{headNo:00}_";
            foreach (var suffix in suffixes)
            {
                var key = prefix + suffix;
                foreach (var parameter in recipe.Parameters)
                {
                    if (!key.Equals(parameter.Key, StringComparison.OrdinalIgnoreCase) && !key.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (double.TryParse(parameter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        return value;
                    }
                }
            }

            return defaultValue;
        }

        private static bool IsConfiguredScannerAxisName(string axisReference)
        {
            var normalized = axisReference.Trim();
            if (normalized.Length == 0 || normalized.Equals("-", StringComparison.Ordinal) || !char.IsAsciiLetter(normalized[0]))
            {
                return false;
            }

            foreach (var character in normalized)
            {
                if (!char.IsAsciiLetter(character) && !char.IsAsciiDigit(character) && character != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private readonly record struct ST_ACTIVE_DIO_TARGET(long Generation, int AutomationNo, string Address);
        private readonly record struct ST_MANUAL_SCRIPT_AXES(string XAxis, string YAxis);
        private readonly record struct ST_MANUAL_SCRIPT_Z_AXIS(string Axis, int AutomationNo);
        private sealed record ST_CONTROLLER_HOME_TASK(int TaskNo, IReadOnlyList<int> HeadNos, IReadOnlyList<string> AxisReferences);
        private sealed record ST_CONTROLLER_HOME_RUN(int TaskNo, string ScriptFileName, string InitialResponse);
        private sealed record ST_RESET_SEQUENCE_AXIS_TARGET(int HeadNo, int AutomationNo, string AxisRole, string AxisReference, int AioNo, bool ReadExtendedStatus);
        private sealed record ST_RESET_SEQUENCE_DIO_TARGET(int HeadNo, int AutomationNo, string Address);
#endregion
#region Controller
        private void RefreshManualControllerHeads()
        {
            var heads = SelectedManualHeadNos;
            _scannerStatusSnapshotReader?.SelectHeads(heads, _manualScannerOptionSettings);
            var snapshot = _scannerStatusSnapshotReader?.GetSnapshot() ?? [];
            var cards = new List<ST_MANUAL_CONTROLLER_HEAD>();
            foreach (var head in heads)
            {
                if (!_controllerHeadCache.TryGetValue(head, out var card))
                {
                    card = new ST_MANUAL_CONTROLLER_HEAD(head, value => RunHeadDioCommand(head, value));
                    _controllerHeadCache.Add(head, card);
                }

                var axes = snapshot.Where(axis => axis.HeadNo == head).ToArray();
                card.SetAxes(axes.Length > 0 ? axes : CManualScannerSnapshotReader.BuildFallbackAxes(_manualScannerOptionSettings, head));
                try
                {
                    card.SetTarget(CreateManualDioTarget(_manualScannerOptionSettings, head), "");
                }
                catch (Exception exception)when (exception is InvalidOperationException or InvalidDataException)
                {
                    card.SetTarget(null, exception.Message);
                }

                cards.Add(card);
            }

            if (!ManualControllerHeads.SequenceEqual(cards))
            {
                ManualControllerHeads = cards;
                OnPropertyChanged(nameof(ManualControllerHeads));
            }

            MotionPanel.RefreshHeads(ManualControllerHeads);
            RefreshManualHomeSpeedTargets();
            RefreshManualScanAssignments();
            RefreshPowerMeasurePanel();
        }

        private async Task RunHeadDioCommand(int headNo, bool? output)
        {
            if (!_selectedManualHeadNos.Contains(headNo) || !_controllerHeadCache.TryGetValue(headNo, out var card) || !card.CanControlDio || card.DioTarget is not { } target)
                return;
            var command = output.HasValue ? output.Value ? "DIO_ON" : "DIO_OFF" : "DIO_READ";
            if (TryBlockManualAutomationCommand(command))
                return;
            card.SetBusy(true);
            try
            {
                var settings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option);
                if (!CScannerStatusSnapshotReader.BuildEnabledHeadNumbers(settings).Contains(headNo) || CreateManualDioTarget(settings, headNo) != target)
                    throw new InvalidOperationException("Head 또는 DIO 설정이 변경되었습니다. 다시 선택해 주세요.");
                var response = output.HasValue ? await _manager.automation.SetIo(target.Address, output.Value, target.AutomationNo) : await _manager.automation.ReadIo(target.Address, target.AutomationNo);
                EnsureAutomationResponse(response, $"H{headNo:00} {command}");
                var value = ReadDioValueFromResponse(response);
                if (card.DioTarget == target)
                    card.SetDioState(value, $"Current {value}");
                _setStatusMessage($"H{headNo:00} {command} · {target.Address}: {value}");
            }
            catch (Exception exception)when (exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                if (card.DioTarget == target)
                    card.SetDioState("UNKNOWN", exception.Message);
                _setStatusMessage($"H{headNo:00} {command} failed. {exception.Message}");
            }
            finally
            {
                card.SetBusy(false);
                _refreshShellStatus();
            }
        }

        private HashSet<(int Controller, string Axis)> ClearingAxisFaults => _clearingAxisFaults ??= [];
        public CButtonCommand ScannerAxisClearFaultCommand => _scannerAxisClearFaultCommand ??= new(async value =>
        {
            if (value is ST_MANUAL_SCANNER_AXIS axis)
                await ClearScannerAxisFault(axis);
        }, value => value is ST_MANUAL_SCANNER_AXIS { CanClearFault: true } axis && !ClearingAxisFaults.Contains((axis.AutomationNo, axis.AxisReference)));

        private async Task ClearScannerAxisFault(ST_MANUAL_SCANNER_AXIS axis)
        {
            if (!axis.CanClearFault || TryBlockManualAutomationCommand("SCANNER_FAULT_CLEAR"))
                return;
            var key = (axis.AutomationNo, axis.AxisReference);
            if (!ClearingAxisFaults.Add(key))
                return;
            ScannerAxisClearFaultCommand.NotifyCanExecuteChanged();
            try
            {
                var limitNote = CAutomationAxisFault.HasLimitFault(axis.FaultMask!.Value) ? "\n리미트 오류는 컨트롤러 설정에 따라 CLEAR 시 축이 리미트 밖으로 이동할 수 있습니다." : "";
                if (!CRecipeConfirmDialog.ShowConfirmation("축 FAULT CLEAR", $"{axis.Head} / {axis.AxisReference}\n{axis.FaultDetail}{limitNote}\n\n해당 축의 Fault를 해제하시겠습니까?", "CLEAR"))
                    return;
                if (TryBlockManualAutomationCommand("SCANNER_FAULT_CLEAR"))
                    return;
                var settings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option);
                var current = CScannerStatusSnapshotReader.BuildAxisDefinitions(settings, axis.HeadNo).SingleOrDefault(item => item.AxisName == axis.AxisRole);
                if (!IsManualControllerTab || !SelectedManualHeadNos.Contains(axis.HeadNo) || !current.IsUsed || current.AutomationNo != axis.AutomationNo || current.AxisReference != axis.AxisReference || !string.IsNullOrEmpty(current.ConfigurationError))
                    throw new InvalidOperationException("Head 또는 축 설정이 변경되었습니다. 다시 선택해 주세요.");
                if (!_manager.automation.IsSimul(axis.AutomationNo) && !_manager.automation.IsConnect(axis.AutomationNo))
                    throw new InvalidOperationException("Automation 장비가 연결되어 있지 않습니다.");
                var snapshot = _manager.automation.StatusSnapshots.GetAxis(axis.AxisReference, axis.AutomationNo, current.AioNo, current.ReadExtendedStatus);
                var mask = snapshot.Value is { } status ? CAutomationAxisFault.ParseMask(status.Fault) : null;
                if (!mask.HasValue)
                    throw new InvalidOperationException("현재 축 Fault 상태를 확인할 수 없습니다. 상태 갱신 후 다시 시도해 주세요.");
                if (CAutomationAxisFault.RequiresReset(mask.Value))
                    throw new InvalidOperationException("InternalFault는 축 CLEAR로 해제할 수 없습니다. 컨트롤러 RESET이 필요합니다.");
                if (mask.Value == 0)
                {
                    _setStatusMessage($"{axis.Head} / {axis.AxisReference}: 활성 Fault가 없습니다.");
                    return;
                }

                EnsureAutomationResponse(await _manager.automation.ResetAlarm(axis.AxisReference, axis.AutomationNo), "FAULT CLEAR");
                // Do not optimistically change FAULT to OK: the common collector verifies the actual state.
                _setStatusMessage($"{axis.Head} / {axis.AxisReference}: FAULT CLEAR 명령 전송. 해제 결과는 상태 갱신 시 표시됩니다.");
            }
            catch (Exception exception)
            {
                _setStatusMessage($"{axis.Head} / {axis.AxisReference}: FAULT CLEAR 실패. {exception.Message}");
                CRecipeConfirmDialog.ShowWarning("축 FAULT CLEAR 실패", exception.Message);
            }
            finally
            {
                ClearingAxisFaults.Remove(key);
                ScannerAxisClearFaultCommand.NotifyCanExecuteChanged();
                _refreshShellStatus();
            }
        }

        public CButtonCommand OpenHomeSpeedSettingsCommand => _openHomeSpeedSettingsCommand ??= new(OpenHomeSpeedSettings, value => _homeSpeedDialog is null && value is ST_MANUAL_HEAD_GROUP group && group.Heads.Any(head => SelectedManualHeadNos.Contains(head.HeadNo)));
        public CManualHomeSpeedPanel HomeSpeedPanel => _homeSpeedPanel ??= new(
            (controller, axes) => _manager.automation.StatusSnapshots.GetConfiguredHomeSpeeds(axes, controller), 
            controller => _manager.automation.StatusSnapshots.RequestConfiguredHomeSpeedsRefresh(controller), 
            SaveManualHomeSpeed, 
            message => CRecipeConfirmDialog.ShowWarning("HOME SPEED SET", message, _homeSpeedDialog is { IsVisible: true } ? _homeSpeedDialog : null), 
            message =>
        {
            _setStatusMessage(message);
            CRecipeConfirmDialog.ShowCompletion("HOME SPEED 저장 완료", message, _homeSpeedDialog is { IsVisible: true } ? _homeSpeedDialog : null);
        }
        );

        private void OpenHomeSpeedSettings(object? parameter)
        {
            if (parameter is not ST_MANUAL_HEAD_GROUP group || _homeSpeedDialog is not null)
                return;
            try
            {
                _homeSpeedController = group.AutomationNo;
                RefreshManualHomeSpeedTargets();
                var dialog = new CManualHomeSpeedDialog(group.Title, HomeSpeedPanel)
                {
                    Owner = Application.Current?.MainWindow
                };
                _homeSpeedDialog = dialog;
                OpenHomeSpeedSettingsCommand.NotifyCanExecuteChanged();
                dialog.ShowDialog();
            }
            finally
            {
                _homeSpeedDialog = null;
                _homeSpeedController = null;
                HomeSpeedPanel.SetActive(false);
                OpenHomeSpeedSettingsCommand.NotifyCanExecuteChanged();
            }
        }

        private void RefreshManualHomeSpeedTargets()
        {
            _openHomeSpeedSettingsCommand?.NotifyCanExecuteChanged();
            if (_homeSpeedController is not { } controller)
            {
                _homeSpeedPanel?.SetActive(false);
                return;
            }

            var targets = SelectedManualHeadNos.SelectMany(head => CScannerStatusSnapshotReader.BuildAxisDefinitions(_manualScannerOptionSettings, head)).Where(axis => axis.AutomationNo == controller).Select(axis => new ST_MANUAL_HOME_SPEED_TARGET(axis.HeadNo, axis.AutomationNo, axis.AxisName, axis.AxisReference, axis.IsUsed && string.IsNullOrEmpty(axis.ConfigurationError), axis.IsUsed ? axis.ConfigurationError : "NOT USE")).ToArray();
            HomeSpeedPanel.UpdateTargets(targets);
            HomeSpeedPanel.SetActive(IsManualControllerTab && IsScannerWorkspace);
            HomeSpeedPanel.ApplySnapshots();
        }

        private async Task<double> SaveManualHomeSpeed(ST_MANUAL_HOME_SPEED_TARGET target, double speed)
        {
            if (TryBlockManualAutomationCommand("HOME_SPEED_SET"))
                throw new InvalidOperationException("자동 운전 중에는 홈 속도를 저장할 수 없습니다.");
            var settings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option);
            var current = CScannerStatusSnapshotReader.BuildAxisDefinitions(settings, target.HeadNo).SingleOrDefault(axis => axis.AxisName == target.Role);
            if (_homeSpeedController != target.Controller || !IsManualControllerTab || !IsScannerWorkspace || !SelectedManualHeadNos.Contains(target.HeadNo) || !current.IsUsed || !string.IsNullOrEmpty(current.ConfigurationError) || current.AutomationNo != target.Controller || current.AxisReference != target.AxisName)
                throw new InvalidOperationException("Head 또는 축 설정이 변경되었습니다. 다시 선택해 주세요.");
            if (!_manager.automation.IsSimul(target.Controller) && !_manager.automation.IsConnect(target.Controller))
                throw new InvalidOperationException($"Automation {target.Controller}: OFFLINE");
            return await _manager.automation.SetConfiguredHomeSpeed(target.AxisName, speed, target.Controller);
        }

        internal void ConfigureAutomationStatusReader(Func<ST_STATION_PROCESS_STATUS, IReadOnlyDictionary<string, string>, CancellationToken, IReadOnlyList<ST_AUTOMATION_CONTROLLER_STATUS_ITEM>> read) => _readAutomationStatus = read;
        private void SetAutomationStatusInputs(ST_RECIPE_DEFINITION? recipe, IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (recipe is not null)
                foreach (var item in recipe.Parameters.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
                    parameters[item.Key] = item.Value;
            foreach (var item in settings.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
                parameters[item.Key] = item.Value;
            _automationStatusParameters = parameters;
        }

        private void RefreshAutomationStatusSnapshot()
        {
            if (_readAutomationStatus is null)
                return;
            var next = _readAutomationStatus(_manager.Station().GetStatus(), _automationStatusParameters, CancellationToken.None);
            if (AutomationControllerStatusItems.Count == next.Count && AutomationControllerStatusItems.Zip(next).All(pair => pair.First.AutomationNo == pair.Second.AutomationNo && pair.First.State == pair.Second.State && pair.First.CpuUsagePercent == pair.Second.CpuUsagePercent && pair.First.Detail == pair.Second.Detail && pair.First.Tasks.SequenceEqual(pair.Second.Tasks)))
                return;
            AutomationControllerStatusItems = next;
            OnPropertyChanged(nameof(AutomationControllerStatusItems));
        }

        private async Task<IReadOnlyDictionary<int, IReadOnlyList<ST_AUTOMATION_ABSOLUTE_MOVE_TARGET>>> PrepareResetRecipeZMoves(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION> controllers, CancellationToken cancellationToken)
        {
            var selectedControllers = controllers.Select(controller => controller.AutomationNo).ToHashSet();
            var hasZ = CScannerStatusSnapshotReader.BuildEnabledHeadNumbers(settings).SelectMany(head => CScannerStatusSnapshotReader.BuildAxisDefinitions(settings, head)).Any(axis => axis.AxisName == "Z" && axis.IsUsed && selectedControllers.Contains(axis.AutomationNo));
            var recipe = hasZ ? await LoadExactMotionRecipe(cancellationToken) : null;
            return controllers.ToDictionary(controller => controller.AutomationNo, controller => BuildResetRecipeZMoves(settings, recipe, controller.AutomationNo));
        }

        private async Task<ST_RECIPE_DEFINITION> LoadExactMotionRecipe(CancellationToken cancellationToken)
        {
            var recipeId = (_selectedRecipeIdProvider() ?? "").Trim();
            if (recipeId.Length == 0)
                throw new InvalidOperationException("DOE Z 이동에 사용할 Recipe를 선택해 주세요.");
            var recipe = await LoadSelectedRecipe(cancellationToken);
            if (recipe is null || !recipe.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase) || !recipeId.Equals((_selectedRecipeIdProvider() ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("선택된 Recipe가 변경되었거나 Recipe를 불러올 수 없습니다.");
            return recipe;
        }

        private static IReadOnlyList<ST_AUTOMATION_ABSOLUTE_MOVE_TARGET> BuildResetRecipeZMoves(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, ST_RECIPE_DEFINITION? recipe, int controller)
        {
            var moves = new List<ST_AUTOMATION_ABSOLUTE_MOVE_TARGET>();
            foreach (var head in CScannerStatusSnapshotReader.BuildEnabledHeadNumbers(settings))
            {
                var axis = CScannerStatusSnapshotReader.BuildAxisDefinitions(settings, head).FirstOrDefault(axis => axis.AxisName == "Z" && axis.IsUsed && axis.AutomationNo == controller);
                if (!axis.IsUsed)
                    continue;
                if (axis.ConfigurationError.Length > 0 || !IsConfiguredScannerAxisName(axis.AxisReference))
                    throw new InvalidDataException($"HEAD {head:00} DOE Z축 설정을 확인해 주세요. {axis.ConfigurationError}");
                var position = ReadMotionRecipeValue(recipe, head, "DOE_Z_POSITION", double.NaN);
                var speed = ReadMotionRecipeValue(recipe, head, "SCANNER_Z_SPEED", 1.0);
                if (speed <= 0)
                    throw new InvalidDataException($"HEAD {head:00} Scanner Z Speed는 0보다 커야 합니다.");
                var move = new ST_AUTOMATION_ABSOLUTE_MOVE_TARGET($"HEAD {head:00} / {axis.AxisReference}", controller, axis.AxisReference, axis.AioNo, axis.ReadExtendedStatus, position, speed);
                var duplicate = moves.FirstOrDefault(existing => existing.AxisReference.Equals(move.AxisReference, StringComparison.OrdinalIgnoreCase));
                if (duplicate is not null)
                {
                    if (duplicate.Position != move.Position || duplicate.Speed != move.Speed)
                        throw new InvalidDataException($"{duplicate.Label}와 {move.Label}에 동일 축의 서로 다른 Recipe 이동값이 지정되어 있습니다.");
                    continue;
                }

                moves.Add(move);
            }

            return moves;
        }

        private static double ReadMotionRecipeValue(ST_RECIPE_DEFINITION? recipe, int head, string suffix, double defaultValue)
        {
            if (recipe is null)
                throw new InvalidDataException("선택된 Recipe를 불러올 수 없습니다.");
            var key = $"H{head:00}_{suffix}";
            var parameter = recipe.Parameters.FirstOrDefault(p => key.Equals(p.Key, StringComparison.OrdinalIgnoreCase) || key.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            var value = defaultValue;
            if (parameter is not null && !double.TryParse(parameter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
                throw new InvalidDataException($"Recipe {recipe.Id}: {key} 값을 확인해 주세요.");
            return value;
        }

        private async Task DisableControllerMof(object? parameter)
        {
            if (parameter is not ST_MANUAL_HEAD_GROUP group || _mofDisableBusy)
                return;
            var commandName = $"CONTROLLER_{group.AutomationNo + 1}_MOF_DISABLE";
            if (TryBlockManualAutomationCommand(commandName))
                return;
            _mofDisableBusy = true;
            try
            {
                var simulation = _manager.automation.IsSimul(group.AutomationNo);
                if (!simulation && !_manager.automation.IsConnect(group.AutomationNo))
                    throw new InvalidOperationException($"{group.Title} Automation 장비가 연결되어 있지 않습니다.");
                var settings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option);
                IReadOnlyList<string> axes = simulation ? BuildControllerHomeTasks(settings, group.AutomationNo).SelectMany(task => task.AxisReferences).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : await _manager.automation.ReadControllerAxes(group.AutomationNo);
                axes = FilterMofAxes(axes, settings, group.AutomationNo);
                if (axes.Count == 0)
                    throw new InvalidOperationException("컨트롤러에 표시할 축이 없습니다.");
                var dialog = new CMofDisableDialog(group.Title, axes)
                {
                    Owner = Application.Current?.MainWindow
                };
                if (dialog.ShowDialog() != true)
                    return;
                if (TryBlockManualAutomationCommand(commandName))
                    return;
                var selected = dialog.SelectedAxes;
                // Re-read the live controller list before creating any executable script.
                var currentAxes = simulation ? axes : await _manager.automation.ReadControllerAxes(group.AutomationNo);
                currentAxes = FilterMofAxes(currentAxes, _manager.Setting().LoadSection(EN_SETTING_TAB.Option), group.AutomationNo);
                if (selected.Any(axis => !currentAxes.Contains(axis, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("컨트롤러 축 구성이 변경되었습니다. 창을 다시 열어 선택하세요.");
                var taskStatus = await _manager.automation.ReadTaskStatus(1, group.AutomationNo);
                var fields = taskStatus.Split(':');
                if (fields.Length < 4 || fields[0] != "OK" || fields[1] != "TASK" || fields[2] != "1" || !(fields[3].Equals("Idle", StringComparison.OrdinalIgnoreCase) || fields[3].Equals("ProgramComplete", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Task 1이 실행 가능한 대기 상태가 아닙니다. {taskStatus}");
                var lines = BuildMofDisableScriptLines(selected);
                var script = _scriptFile.Create("MANUAL_MOF_DISABLE.ascript");
                foreach (var line in lines)
                    script.AddLine(line);
                var saved = script.Save();
                CancelStatusPolling();
                _lastCommand = commandName;
                _lastResult = "Running Task 1";
                RefreshCommandStateRows();
                var upload = await _manager.automation.UploadScript(saved.FilePath, saved.FileName, group.AutomationNo);
                EnsureAutomationResponse(upload, "MOF Disable upload");
                var response = await _manager.automation.RunScriptIfIdle(saved.FileName, 1, group.AutomationNo);
                EnsureAutomationResponse(response, "MOF Disable run Task 1");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var polling = CreateStatusPollingCancellation(timeout.Token);
                try
                {
                    await WaitManualTaskCompleted(response, requireExecutionStart: false, automationNo: group.AutomationNo, taskNo: 1, cancellationToken: polling.Token);
                }
                finally
                {
                    ReleaseStatusPollingCancellation(polling);
                }

                _lastResult = "MOF Disable completed";
                var message = $"{group.Title} MOF 해제 완료: {string.Join(", ", selected)}";
                _setStatusMessage(message);
                CRecipeConfirmDialog.ShowCompletion("MOF Disable", message);
            }
            catch (OperationCanceledException)
            {
                _lastResult = "MOF Disable completion check cancelled";
                _setStatusMessage("MOF 해제 완료 확인이 중단되었습니다. 컨트롤러의 Task 1 상태를 확인하세요.");
            }
            catch (Exception exception)
            {
                _lastResult = $"ERROR: {exception.Message}";
                _setStatusMessage($"{group.Title} MOF 해제 실패: {exception.Message}");
                CRecipeConfirmDialog.ShowWarning("MOF Disable 실패", exception.Message);
            }
            finally
            {
                _mofDisableBusy = false;
                RefreshCommandStateRows();
                _refreshShellStatus();
                await _refreshCurrentScreen();
            }
        }

        internal static IReadOnlyList<string> FilterMofAxes(IReadOnlyList<string> axes, IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int automationNo)
        {
            var zAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var head = 1; head <= 8; head++)
            {
                if (ReadManualHeadAutomationNo(settings, head) != automationNo)
                    continue;
                foreach (var setting in settings.Where(item => item.Key.Equals($"H{head:00}_SCANNER_Z_AXIS", StringComparison.OrdinalIgnoreCase) || item.Key.Equals($"H{head:00}_Z_AXIS", StringComparison.OrdinalIgnoreCase)))
                    zAxes.Add(setting.Value.Trim());
            }

            return axes.Where(axis => !zAxes.Contains(axis.Trim()) && !Regex.IsMatch(axis.Trim(), @"\AG?Z(?:_?[0-9]+)?\z", RegexOptions.IgnoreCase)).ToArray();
        }

        internal static IReadOnlyList<string> BuildMofDisableScriptLines(IReadOnlyList<string> selectedAxes)
        {
            if (selectedAxes.Count == 0)
                throw new InvalidDataException("MOF를 해제할 축을 선택하세요.");
            var lines = new List<string>
            {
                "Program"
            };
            foreach (var axis in selectedAxes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Regex.IsMatch(axis, @"\A(?:[A-Za-z_][A-Za-z0-9_]*|[0-9]+)\z"))
                    throw new InvalidDataException($"스크립트에서 사용할 수 없는 축 이름입니다: {axis}");
                lines.Add($"GalvoEncoderScaleFactorSet({axis}, 0)");
            }

            lines.Add("End");
            return lines;
        }

        public void RefreshScannerStatusSnapshot()
        {
            if (IsReviewWorkspace)
                return;
            if (IsScannerWorkspace && IsManualMofTab)
                RefreshMofStageSnapshot();
            if (IsScannerWorkspace && (IsManualScanTab || IsManualMofTab))
                RefreshAutomationStatusSnapshot();
            if (_manualScannerOptionSettings.Count == 0)
            {
                return;
            }

            var selectedHeadNo = NormalizeManualScannerHeadNo(_manualScannerOptionSettings, Math.Clamp(_selectedHeadNoProvider(), 1, 8));
            var scannerAxes = ReadManualScannerAxesSnapshot(_manualScannerOptionSettings, selectedHeadNo);
            var headCards = ApplyManualHeadSelection(BuildHeadCards(selectedHeadNo, _manualScannerOptionSettings, scannerAxes), selectedHeadNo);
            bool IsSelectedHeadCard(ST_MANUAL_HEAD_CARD head)
            {
                return head.HeadNo == selectedHeadNo;
            }

            var selectedHead = headCards.FirstOrDefault(IsSelectedHeadCard) ?? new ST_MANUAL_HEAD_CARD(selectedHeadNo, $"H{selectedHeadNo:00}", "X -", "Y -", "Z -", "Disabled", true);
            if (!_hasScannerTargetSnapshot && HasReadableScannerPosition(scannerAxes))
            {
                TargetGx = ReadScannerAxisTarget(scannerAxes, "X");
                TargetGy = ReadScannerAxisTarget(scannerAxes, "Y");
                TargetGz = ReadScannerAxisTarget(scannerAxes, "Z", IsScannerAxisAvailable(scannerAxes, "Z") ? TargetGz : "NOT USE");
                _hasScannerTargetSnapshot = true;
            }

            HeadCards = headCards;
            HeadGroups = BuildHeadGroups(headCards);
            SelectedHeadItems = BuildSelectedHeadItems(selectedHead);
            SelectedHead = selectedHead.HeadName;
            ScannerAxes = scannerAxes;
            if (IsStageVisionWorkspace)
            {
                RefreshStageMotionSnapshot();
            }
            else
            {
                StopStageMotionPolling();
            }

            CommandStateRows = BuildCommandStateRows(selectedHead);
            OnPropertyChanged(nameof(HeadCards));
            OnPropertyChanged(nameof(SelectedHeadItems));
            OnPropertyChanged(nameof(SelectedHead));
            OnPropertyChanged(nameof(CommandStateRows));
            OnPropertyChanged(nameof(IsZPositionMoveEnabled));
            RefreshManualControllerHeads();
        }

        private async Task HomeScannerAxis(object? parameter)
        {
            await ExecuteScannerAxisCommand(parameter, "HOME");
        }

        private async Task EnableScannerAxis(object? parameter)
        {
            await ExecuteScannerAxisCommand(parameter, "ENABLE");
        }

        private async Task DisableScannerAxis(object? parameter)
        {
            await ExecuteScannerAxisCommand(parameter, "DISABLE");
        }

        private void ShowResetSequenceDialog(object? parameter)
        {
            if (!IsScannerWorkspace)
            {
                return;
            }

            var dialog = new CControllerResetSequenceDialog(BuildResetSequenceTargetOptions(), ExecuteResetSequence)
            {
                Owner = GetActiveWindow()
            };
            dialog.ShowDialog();
        }

        private async Task ResetAutomationController(object? parameter, CancellationToken cancellationToken = default)
        {
            if (parameter is not ST_MANUAL_HEAD_GROUP group)
            {
                return;
            }

            var commandName = $"CONTROLLER_{group.AutomationNo + 1}_RESET";
            if (TryBlockManualAutomationCommand(commandName))
            {
                return;
            }

            if (!_manager.automation.IsSimul(group.AutomationNo) && !_manager.automation.IsConnect(group.AutomationNo))
            {
                _lastCommand = commandName;
                _lastResult = "OFFLINE";
                _setStatusMessage($"{group.Title} reset failed. Automation is offline.");
                RefreshCommandStateRows();
                _refreshShellStatus();
                CRecipeConfirmDialog.ShowWarning("컨트롤러 리셋 실패", $"{group.Title} Automation 장비가 연결되어 있지 않아 리셋을 실행할 수 없습니다.");
                return;
            }

            if (!CRecipeConfirmDialog.ShowConfirmation("컨트롤러 리셋", $"{group.Title} 컨트롤러를 리셋하시겠습니까?\n축 Enable과 Home 상태가 해제될 수 있습니다."))
            {
                return;
            }

            _lastCommand = commandName;
            _lastResult = "Sending";
            RefreshCommandStateRows();
            _refreshShellStatus();
            try
            {
                var response = await _manager.automation.ResetController(group.AutomationNo, cancellationToken);
                EnsureAutomationResponse(response, $"{group.Title} reset");
                _lastResult = $"OK: {group.Title}";
                _setStatusMessage($"{group.Title} reset command OK.");
                RefreshCommandStateRows();
                _refreshShellStatus();
                await _refreshCurrentScreen();
            }
            catch (Exception exception)when (exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                _lastResult = $"ERROR: {exception.Message}";
                _setStatusMessage($"{group.Title} reset failed. {exception.Message}");
                RefreshCommandStateRows();
                _refreshShellStatus();
                await _refreshCurrentScreen();
                CRecipeConfirmDialog.ShowWarning("컨트롤러 리셋 실패", $"{group.Title} 리셋 명령을 완료하지 못했습니다.\n{exception.Message}");
            }
        }

        private IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION> BuildResetSequenceTargetOptions()
        {
            int SelectInterfaceNumber(ST_INTERFACE_DATA data)
            {
                return data.Number;
            }

            string FormatControllerName(ST_INTERFACE_DATA data)
            {
                var controllerName = $"CONTROLLER {data.Number + 1}";
                return string.IsNullOrWhiteSpace(data.NickName) ? controllerName : $"{controllerName} ({data.NickName})";
            }

            return _manager.Interface().GetInterfaceList(EN_EQP_MODULE.Automation1).OrderBy(SelectInterfaceNumber).Select(data => new ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION(FormatControllerName(data), data.Number, false)).ToArray();
        }

        private async Task ExecuteResetSequence(
            CControllerResetSequenceDialog dialog, 
            IReadOnlyList<ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION> controllers, 
            Func<bool> isStopRequested, 
            CancellationToken cancellationToken
        )
        {
            if (TryBlockManualAutomationCommand("RESET_SEQUENCE"))
            {
                throw new InvalidOperationException("Auto process is running. Reset sequence is blocked.");
            }

            var settings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option, cancellationToken);
            // Validate every selected controller's Recipe targets before the first reset.
            var recipeZMoves = await PrepareResetRecipeZMoves(settings, controllers, cancellationToken);
            _lastCommand = "RESET_SEQUENCE";
            _lastResult = "Running";
            RefreshCommandStateRows();
            _refreshShellStatus();
            foreach (var controller in controllers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isStopRequested())
                {
                    dialog.SkipAllWaitingSteps();
                    break;
                }

                await ExecuteResetSequenceStep(dialog, controller, 1, "Controller Reset", () => ResetSequenceController(controller, cancellationToken));
                if (isStopRequested())
                {
                    dialog.SkipWaitingSteps(controller.AutomationNo);
                    dialog.SkipAllWaitingSteps();
                    break;
                }

                var axes = BuildResetSequenceAxisTargets(settings, controller.AutomationNo);
                await ExecuteResetSequenceStep(dialog, controller, 2, "All Axis Enable", () => ResetSequenceEnableAxes(controller, axes, cancellationToken));
                if (isStopRequested())
                {
                    dialog.SkipWaitingSteps(controller.AutomationNo);
                    dialog.SkipAllWaitingSteps();
                    break;
                }

                await ExecuteResetSequenceStep(dialog, controller, 3, "All Axis Home", () => ResetSequenceHomeAxes(controller, axes, cancellationToken));
                if (isStopRequested())
                {
                    dialog.SkipWaitingSteps(controller.AutomationNo);
                    dialog.SkipAllWaitingSteps();
                    break;
                }

                var dioTargets = BuildResetSequenceDioTargets(settings, controller.AutomationNo);
                await ExecuteResetSequenceStep(dialog, controller, 4, "DIO ON", () => ResetSequenceDioOn(controller, dioTargets, cancellationToken));
                if (isStopRequested())
                {
                    dialog.SkipAllWaitingSteps();
                    break;
                }

                var zMoves = recipeZMoves[controller.AutomationNo];
                if (zMoves.Count == 0)
                {
                    dialog.SetStepState(controller.AutomationNo, 5, "SKIP");
                    dialog.SetStatusMessage($"{controller.DisplayName}: 사용 중인 DOE Z축이 없어 5번 단계를 건너뜁니다.");
                }
                else
                {
                    await ExecuteResetSequenceStep(dialog, controller, 5, "DOE Z Recipe Position Move", () => new CAutomationPositioningService(_manager.automation).MoveAndWait(zMoves, cancellationToken));
                }
            }

            _lastResult = isStopRequested() ? "STOPPED" : "OK";
            RefreshCommandStateRows();
            _refreshShellStatus();
        }

        private async Task ExecuteResetSequenceStep(CControllerResetSequenceDialog dialog, ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION controller, int stepNo, string stepName, Func<Task> action)
        {
            dialog.SetStatusMessage($"{controller.DisplayName} {stepName} running.");
            dialog.SetStepState(controller.AutomationNo, stepNo, "RUNNING");
            try
            {
                await action();
                dialog.SetStepState(controller.AutomationNo, stepNo, "OK");
                dialog.SetStatusMessage($"{controller.DisplayName} {stepName} OK.");
            }
            catch
            {
                dialog.SetStepState(controller.AutomationNo, stepNo, "NG");
                dialog.SkipAllWaitingSteps();
                _lastResult = $"NG: {controller.DisplayName} {stepName}";
                RefreshCommandStateRows();
                _refreshShellStatus();
                throw;
            }
        }

        private async Task ResetSequenceController(ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION controller, CancellationToken cancellationToken)
        {
            if (!_manager.automation.IsSimul(controller.AutomationNo) && !_manager.automation.IsConnect(controller.AutomationNo))
            {
                throw new InvalidOperationException($"{controller.DisplayName} Automation is offline.");
            }

            var response = await WithTimeout(token => _manager.automation.ResetController(controller.AutomationNo, token), ResetSequenceResetTimeout, cancellationToken);
            EnsureAutomationResponse(response, $"{controller.DisplayName} reset");
            await WaitControllerReady(controller, cancellationToken);
        }

        private async Task WaitControllerReady(ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION controller, CancellationToken cancellationToken)
        {
            if (_manager.automation.IsSimul(controller.AutomationNo))
            {
                return;
            }

            var deadline = DateTimeOffset.Now + ResetSequenceResetTimeout;
            while (DateTimeOffset.Now <= deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_manager.automation.IsConnect(controller.AutomationNo))
                {
                    var response = await WithTimeout(token => _manager.automation.ReadStatus(controller.AutomationNo, token), ResetSequenceCommandTimeout, cancellationToken);
                    EnsureAutomationResponse(response, $"{controller.DisplayName} status");
                    return;
                }

                await Task.Delay(ResetSequencePollInterval, cancellationToken);
            }

            throw new TimeoutException($"{controller.DisplayName} reset ready check timed out.");
        }

        private async Task ResetSequenceEnableAxes(ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION controller, IReadOnlyList<ST_RESET_SEQUENCE_AXIS_TARGET> axes, CancellationToken cancellationToken)
        {
            if (axes.Count == 0)
            {
                throw new InvalidOperationException($"{controller.DisplayName} has no configured scanner axis.");
            }

            foreach (var axis in axes)
            {
                var response = await WithTimeout(token => _manager.automation.ServoOn(axis.AxisReference, controller.AutomationNo, token), ResetSequenceCommandTimeout, cancellationToken);
                EnsureAutomationResponse(response, $"{controller.DisplayName} {axis.AxisReference} enable");
            }

            await WaitAxesReady(controller, axes, status => status.Able && !status.HasError, "enable", ResetSequenceEnableTimeout, cancellationToken);
        }

        private async Task ResetSequenceHomeAxes(ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION controller, IReadOnlyList<ST_RESET_SEQUENCE_AXIS_TARGET> axes, CancellationToken cancellationToken)
        {
            if (axes.Count == 0)
            {
                throw new InvalidOperationException($"{controller.DisplayName} has no configured scanner axis.");
            }

            foreach (var axis in axes)
            {
                var response = await WithTimeout(token => _manager.automation.HomeAsync(axis.AxisReference, controller.AutomationNo, token), ResetSequenceCommandTimeout, cancellationToken);
                EnsureAutomationResponse(response, $"{controller.DisplayName} {axis.AxisReference} home");
            }

            await WaitAxesReady(controller, axes, status => status.Able && status.HomeDone && !status.HasError, "home", ResetSequenceHomeTimeout, cancellationToken);
        }

        private async Task WaitAxesReady(
            ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION controller, 
            IReadOnlyList<ST_RESET_SEQUENCE_AXIS_TARGET> axes, 
            Func<ST_AUTOMATION_AXIS_STATUS, bool> isReady, 
            string actionName, 
            TimeSpan timeout, 
            CancellationToken cancellationToken
        )
        {
            var deadline = DateTimeOffset.Now + timeout;
            while (DateTimeOffset.Now <= deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readyCount = 0;
                foreach (var axis in axes)
                {
                    var status = await ReadAxisStatusWithTimeout(axis, cancellationToken);
                    if (status.HasError)
                    {
                        throw new InvalidOperationException($"{controller.DisplayName} {axis.AxisReference} fault during {actionName}. Fault={status.Fault}");
                    }

                    if (isReady(status))
                    {
                        readyCount++;
                    }
                }

                if (readyCount == axes.Count)
                {
                    return;
                }

                await Task.Delay(ResetSequencePollInterval, cancellationToken);
            }

            throw new TimeoutException($"{controller.DisplayName} axis {actionName} check timed out.");
        }

        private async Task<ST_AUTOMATION_AXIS_STATUS> ReadAxisStatusWithTimeout(ST_RESET_SEQUENCE_AXIS_TARGET axis, CancellationToken cancellationToken)
        {
            return await WithTimeout(token => Task.Run(() => _manager.automation.ReadAxisStatusByNameNoLog(axis.AxisReference, axis.AutomationNo, axis.AioNo, axis.ReadExtendedStatus, token), token), ResetSequenceCommandTimeout, cancellationToken);
        }

        private async Task ResetSequenceDioOn(ST_CONTROLLER_RESET_SEQUENCE_TARGET_OPTION controller, IReadOnlyList<ST_RESET_SEQUENCE_DIO_TARGET> dioTargets, CancellationToken cancellationToken)
        {
            if (dioTargets.Count == 0)
            {
                throw new InvalidOperationException($"{controller.DisplayName} has no configured scanner DIO.");
            }

            foreach (var target in dioTargets)
            {
                var response = await WithTimeout(token => _manager.automation.SetIo(target.Address, true, controller.AutomationNo, token), ResetSequenceCommandTimeout, cancellationToken);
                EnsureAutomationResponse(response, $"{controller.DisplayName} {target.Address} ON");
                var setValue = ReadDioValueFromResponse(response);
                if (!setValue.Equals("ON", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{controller.DisplayName} {target.Address} ON response mismatch. Response={setValue}");
                }

                var readResponse = await WithTimeout(token => _manager.automation.ReadIo(target.Address, controller.AutomationNo, token), ResetSequenceDioTimeout, cancellationToken);
                EnsureAutomationResponse(readResponse, $"{controller.DisplayName} {target.Address} read");
                var readValue = ReadDioValueFromResponse(readResponse);
                if (!readValue.Equals("ON", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{controller.DisplayName} {target.Address} readback mismatch. Current={readValue}");
                }
            }
        }

        private static async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> action, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var task = action(timeoutCancellation.Token);
            var completed = await Task.WhenAny(task, Task.Delay(timeout, cancellationToken));
            if (!ReferenceEquals(completed, task))
            {
                timeoutCancellation.Cancel();
                throw new TimeoutException($"Automation command timed out after {timeout.TotalSeconds:0.#} sec.");
            }

            return await task;
        }

        private static IReadOnlyList<ST_RESET_SEQUENCE_AXIS_TARGET> BuildResetSequenceAxisTargets(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int automationNo)
        {
            const int headCount = 8;
            var targets = new List<ST_RESET_SEQUENCE_AXIS_TARGET>();
            var axisSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var headNo = 1; headNo <= headCount; headNo++)
            {
                var definitions = CScannerStatusSnapshotReader.BuildAxisDefinitions(settings, headNo);
                foreach (var definition in definitions)
                {
                    if (definition.AutomationNo != automationNo || !definition.IsUsed)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(definition.ConfigurationError))
                    {
                        throw new InvalidOperationException(definition.ConfigurationError);
                    }

                    if (string.IsNullOrWhiteSpace(definition.AxisReference))
                    {
                        throw new InvalidOperationException($"H{headNo:00} {definition.AxisName} axis name is not configured.");
                    }

                    if (!axisSet.Add(definition.AxisReference))
                    {
                        continue;
                    }

                    targets.Add(new ST_RESET_SEQUENCE_AXIS_TARGET(headNo, definition.AutomationNo, definition.AxisName, definition.AxisReference, definition.AioNo, definition.ReadExtendedStatus));
                }
            }

            return targets;
        }

        private static IReadOnlyList<ST_RESET_SEQUENCE_DIO_TARGET> BuildResetSequenceDioTargets(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int automationNo)
        {
            var targets = new List<ST_RESET_SEQUENCE_DIO_TARGET>();
            var addressSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var enabledHeadNos = CScannerStatusSnapshotReader.BuildEnabledHeadNumbers(settings, 8);
            foreach (var headNo in enabledHeadNos)
            {
                var headAutomationNo = ReadManualHeadAutomationNo(settings, headNo);
                if (headAutomationNo != automationNo)
                {
                    continue;
                }

                var defaultGxAxisNo = ((headNo - 1) % 4) * 2;
                var gxAxisNo = ReadOptionalHeadSettingInt(settings, defaultGxAxisNo, headNo, "GX_AXIS_NO");
                ValidateDioAxisNo(headNo, gxAxisNo);
                var dioNo = ReadRequiredHeadSettingInt(settings, headNo, "SCANNER_DIO_NO");
                ValidateDioNo(headNo, dioNo);
                var address = $"A{gxAxisNo.ToString(CultureInfo.InvariantCulture)}.DO{dioNo.ToString(CultureInfo.InvariantCulture)}";
                if (!addressSet.Add(address))
                {
                    continue;
                }

                targets.Add(new ST_RESET_SEQUENCE_DIO_TARGET(headNo, automationNo, address));
            }

            return targets;
        }

        private async Task HomeAutomationControllerAxes(object? parameter, CancellationToken cancellationToken = default)
        {
            if (parameter is not ST_MANUAL_HEAD_GROUP group)
            {
                return;
            }

            var commandName = $"CONTROLLER_{group.AutomationNo + 1}_ALL_HOME";
            if (TryBlockManualAutomationCommand(commandName))
            {
                return;
            }

            if (!_manager.automation.IsSimul(group.AutomationNo) && !_manager.automation.IsConnect(group.AutomationNo))
            {
                _lastCommand = commandName;
                _lastResult = "OFFLINE";
                _setStatusMessage($"{group.Title} all home failed. Automation is offline.");
                RefreshCommandStateRows();
                _refreshShellStatus();
                CRecipeConfirmDialog.ShowWarning("컨트롤러 ALL HOME 실패", $"{group.Title} Automation 장비가 연결되어 있지 않아 ALL HOME을 실행할 수 없습니다.");
                return;
            }

            IReadOnlyList<ST_CONTROLLER_HOME_TASK> homeTasks;
            try
            {
                var optionSettings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option, cancellationToken);
                homeTasks = BuildControllerHomeTasks(optionSettings, group.AutomationNo);
            }
            catch (Exception exception)when (exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                _lastCommand = commandName;
                _lastResult = $"ERROR: {exception.Message}";
                _setStatusMessage($"{group.Title} all home failed. {exception.Message}");
                RefreshCommandStateRows();
                _refreshShellStatus();
                CRecipeConfirmDialog.ShowWarning("컨트롤러 ALL HOME 실패", exception.Message);
                return;
            }

            if (homeTasks.Count == 0)
            {
                _lastCommand = commandName;
                _lastResult = "NO AXIS";
                _setStatusMessage($"{group.Title} all home skipped. No enabled scanner axis is configured.");
                RefreshCommandStateRows();
                _refreshShellStatus();
                CRecipeConfirmDialog.ShowWarning("컨트롤러 ALL HOME 실패", $"{group.Title}에 사용 설정된 스캐너 축이 없습니다.");
                return;
            }

            var axisList = FormatControllerHomeAxisList(homeTasks);
            var confirmationMessage = $"{axisList} 의 Home을 실행하시겠습니까?";
            if (homeTasks.Count > 1)
            {
                confirmationMessage = $"{confirmationMessage}{Environment.NewLine}{FormatControllerHomeTaskSummary(homeTasks)}";
            }

            if (!CRecipeConfirmDialog.ShowConfirmation("컨트롤러 ALL HOME", confirmationMessage))
            {
                return;
            }

            CancelStatusPolling();
            _lastCommand = commandName;
            _lastResult = "Building";
            RefreshCommandStateRows();
            _refreshShellStatus();
            try
            {
                var runItems = new List<ST_CONTROLLER_HOME_RUN>(homeTasks.Count);
                foreach (var homeTask in homeTasks)
                {
                    var taskAxisList = string.Join(",", homeTask.AxisReferences);
                    var scriptFileName = BuildControllerHomeScriptFileName(homeTask);
                    var script = _scriptFile.Create(scriptFileName);
                    script.Start($"Manual {group.Title} {FormatControllerHomeHeadSummary(homeTask.HeadNos)} ALL HOME");
                    script.AddLine($"Home([{taskAxisList}])");
                    script.End();
                    var savedScript = script.Save(cancellationToken);
                    var uploadResponse = await _manager.automation.UploadScript(savedScript.FilePath, savedScript.FileName, group.AutomationNo, cancellationToken);
                    EnsureAutomationResponse(uploadResponse, $"{commandName} upload T{homeTask.TaskNo}");
                    var runResponse = await _manager.automation.RunScript(savedScript.FileName, homeTask.TaskNo, group.AutomationNo, cancellationToken);
                    EnsureAutomationResponse(runResponse, $"{commandName} run T{homeTask.TaskNo}");
                    runItems.Add(new ST_CONTROLLER_HOME_RUN(homeTask.TaskNo, savedScript.FileName, runResponse));
                }

                _lastResult = $"Running {runItems.Count} task(s)";
                _setStatusMessage($"{group.Title} all home scripts uploaded and started: {FormatControllerHomeRunSummary(runItems)}");
                var statusCancellation = CreateStatusPollingCancellation(cancellationToken);
                var statusToken = statusCancellation.Token;
                try
                {
                    foreach (var runItem in runItems)
                    {
                        await WaitManualTaskCompleted(runItem.InitialResponse, requireExecutionStart: !_manager.automation.IsSimul(group.AutomationNo), automationNo: group.AutomationNo, taskNo: runItem.TaskNo, cancellationToken: statusToken);
                    }

                    statusToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    ReleaseStatusPollingCancellation(statusCancellation);
                }

                _lastResult = $"Completed {runItems.Count} task(s)";
                _setStatusMessage($"{group.Title} all home completed: {FormatControllerHomeRunSummary(runItems)}");
                RefreshCommandStateRows();
                _refreshShellStatus();
                await _refreshCurrentScreen();
            }
            catch (OperationCanceledException)
            {
            // 화면을 벗어나면 완료 상태 조회와 후속 팝업만 중단한다.
            // 이미 Automation1에 전달된 실제 Home 스크립트는 별도의 정지 명령 없이 계속된다.
            }
            catch (Exception exception)when (exception is InvalidDataException or InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                _lastResult = $"ERROR: {exception.Message}";
                _setStatusMessage($"{group.Title} all home failed. {exception.Message}");
                RefreshCommandStateRows();
                _refreshShellStatus();
                CRecipeConfirmDialog.ShowWarning("컨트롤러 ALL HOME 실패", $"ALL HOME 동작을 완료하지 못했습니다.\n{exception.Message}");
            }
        }

        private async Task ExecuteScannerAxisCommand(object? parameter, string commandName, CancellationToken cancellationToken = default)
        {
            if (parameter is not ST_MANUAL_SCANNER_AXIS axis)
            {
                return;
            }

            if (!IsConfiguredScannerAxisName(axis.AxisReference))
            {
                _lastCommand = $"SCANNER_{axis.Head}_{axis.AxisRole}_{commandName}";
                _lastResult = "NOT CONFIGURED";
                _setStatusMessage($"{axis.Head} {axis.AxisRole} axis name is not configured.");
                RefreshCommandStateRows();
                _refreshShellStatus();
                CRecipeConfirmDialog.ShowWarning("스캐너 축 제어 실패", $"{axis.Head} {axis.AxisRole}축의 Automation1 축 이름을 설정한 뒤 다시 시도하십시오.");
                return;
            }

            if (TryBlockManualAutomationCommand($"SCANNER_{axis.Head}_{axis.AxisRole}_{commandName}"))
            {
                return;
            }

            var commandText = commandName switch
            {
                "HOME" => "원점으로 복귀",
                "ENABLE" => "활성화",
                "DISABLE" => "비활성화",
                _ => commandName
            };
            if (!_manager.automation.IsSimul(axis.AutomationNo) && !_manager.automation.IsConnect(axis.AutomationNo))
            {
                _lastCommand = $"SCANNER_{axis.Head}_{axis.AxisRole}_{commandName}";
                _lastResult = "OFFLINE";
                _setStatusMessage($"{axis.Head} {axis.AxisRole} {commandName} failed. Automation is offline.");
                RefreshCommandStateRows();
                _refreshShellStatus();
                CRecipeConfirmDialog.ShowWarning("스캐너 축 제어 실패", "Automation 장비가 연결되어 있지 않아 동작을 실행할 수 없습니다.");
                return;
            }

            if (!CRecipeConfirmDialog.ShowConfirmation("스캐너 축 제어", $"{axis.Head} / {axis.AxisReference}축을 {commandText}하시겠습니까?"))
            {
                return;
            }

            _lastCommand = $"SCANNER_{axis.Head}_{axis.AxisRole}_{commandName}";
            _lastResult = "Sending";
            RefreshCommandStateRows();
            try
            {
                string response;
                if (commandName.Equals("HOME", StringComparison.OrdinalIgnoreCase))
                {
                    response = await _manager.automation.HomeAsync(axis.AxisReference, axis.AutomationNo, cancellationToken);
                }
                else if (commandName.Equals("ENABLE", StringComparison.OrdinalIgnoreCase))
                {
                    response = await _manager.automation.ServoOn(axis.AxisReference, axis.AutomationNo, cancellationToken);
                }
                else if (commandName.Equals("DISABLE", StringComparison.OrdinalIgnoreCase))
                {
                    response = await _manager.automation.ServoOff(axis.AxisReference, axis.AutomationNo, cancellationToken);
                }
                else
                {
                    throw new InvalidOperationException($"Scanner axis command is unknown: {commandName}.");
                }

                EnsureAutomationResponse(response, $"{commandName} {axis.Head} {axis.AxisRole}");
                _lastResult = $"OK: {axis.AxisReference}";
                _setStatusMessage($"{axis.Head} {axis.AxisRole} axis {axis.AxisReference} {commandName} command OK.");
                RefreshCommandStateRows();
                _refreshShellStatus();
                await _refreshCurrentScreen();
            }
            catch (Exception exception)when (exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                _lastResult = $"ERROR: {exception.Message}";
                _setStatusMessage($"{axis.Head} {axis.AxisRole} {commandName} failed. {exception.Message}");
                RefreshCommandStateRows();
                _refreshShellStatus();
                await _refreshCurrentScreen();
                CRecipeConfirmDialog.ShowWarning("스캐너 축 제어 실패", $"{axis.AxisRole}축 {commandText} 동작을 완료하지 못했습니다.\n{exception.Message}");
            }
        }

        private Task<ST_MANUAL_DIO_TARGET> ResolveDioTarget(int headNo, CancellationToken cancellationToken) => Task.FromResult(CreateManualDioTarget(_manager.Setting().LoadSection(EN_SETTING_TAB.Option, cancellationToken), headNo));
        private static ST_MANUAL_DIO_TARGET CreateManualDioTarget(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo)
        {
            if (headNo is < 1 or > 8)
            {
                throw new InvalidOperationException($"Manual scanner DIO head number is invalid: {headNo}. Use H01-H08.");
            }

            var automationNo = ReadOptionalHeadSettingInt(settings, headNo <= 4 ? 0 : 1, headNo, "AUTOMATION_NO");
            var defaultGxAxisNo = ((headNo - 1) % 4) * 2;
            var gxAxisNo = ReadOptionalHeadSettingInt(settings, defaultGxAxisNo, headNo, "GX_AXIS_NO");
            var gxAxisName = ReadHeadSettingText(settings, $"GX_{headNo.ToString(CultureInfo.InvariantCulture)}", headNo, "X_AXIS", "GX_AXIS");
            ValidateDioAxisNo(headNo, gxAxisNo);
            var dioNo = ReadRequiredHeadSettingInt(settings, headNo, "SCANNER_DIO_NO");
            ValidateDioNo(headNo, dioNo);
            var address = $"A{gxAxisNo.ToString(CultureInfo.InvariantCulture)}.DO{dioNo.ToString(CultureInfo.InvariantCulture)}";
            return new ST_MANUAL_DIO_TARGET(automationNo, address, gxAxisName, dioNo);
        }

        private void ApplyDioTarget(ST_MANUAL_DIO_TARGET target)
        {
            _dioAutomationNo = target.AutomationNo;
            DioAddress = target.Address;
            DioAxisName = target.AxisName;
            DioNo = target.DioNo.ToString(CultureInfo.InvariantCulture);
            DioLastResult = "Ready";
            IsDioTargetReady = true;
        }

        private void InvalidateDioTarget(string result)
        {
            _dioAutomationNo = 0;
            DioAddress = "";
            DioAxisName = "-";
            DioNo = "-";
            DioCurrentValue = "UNKNOWN";
            DioLastResult = result;
            IsDioTargetReady = false;
        }

        private long BeginDioTargetRefresh(string result)
        {
            var generation = Interlocked.Increment(ref _dioGeneration);
            InvalidateDioTarget(result);
            return generation;
        }

        private bool IsCurrentDioGeneration(long generation)
        {
            return Interlocked.Read(ref _dioGeneration) == generation;
        }

        private static void ValidateDioAxisNo(int headNo, int axisNo)
        {
            if (axisNo < 0)
            {
                throw new InvalidOperationException($"H{headNo:00} scanner GX axis number is invalid: {axisNo}.");
            }
        }

        private static void ValidateDioNo(int headNo, int dioNo)
        {
            if (dioNo == UnconfiguredScannerDioNo)
            {
                throw new InvalidOperationException($"H{headNo:00} scanner DIO number is not configured.");
            }

            if (dioNo is < 0 or > 63)
            {
                throw new InvalidOperationException($"H{headNo:00} scanner DIO number must be between 0 and 63. Value={dioNo}.");
            }
        }

        private async Task RefreshDioState(
            string commandName = "DIO_READ", 
            bool recordCommand = true, 
            bool showStatus = true, 
            long? expectedGeneration = null, 
            CancellationToken cancellationToken = default
        )
        {
            if (!TryBeginDioCommand(commandName, recordCommand, showStatus, "Reading", expectedGeneration, out var target))
            {
                return;
            }

            try
            {
                var response = await _manager.automation.ReadIo(target.Address, target.AutomationNo, cancellationToken: cancellationToken);
                EnsureAutomationResponse(response, $"{commandName} read");
                var dioValue = ReadDioValueFromResponse(response);
                if (!IsCurrentDioGeneration(target.Generation))
                {
                    return;
                }

                DioCurrentValue = dioValue;
                DioLastResult = $"Current {dioValue}";
                if (recordCommand)
                {
                    _lastResult = DioLastResult;
                }

                if (showStatus)
                {
                    _setStatusMessage($"Manual scanner DIO {target.Address} read: {dioValue}.");
                }
            }
            catch (Exception exception)when (exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                if (!IsCurrentDioGeneration(target.Generation))
                {
                    return;
                }

                DioCurrentValue = "UNKNOWN";
                DioLastResult = $"ERROR: {exception.Message}";
                if (recordCommand)
                {
                    _lastResult = DioLastResult;
                }

                if (showStatus)
                {
                    _setStatusMessage($"{commandName} failed. {exception.Message}");
                }
            }
            finally
            {
                IsDioControlBusy = false;
                if (recordCommand)
                {
                    RefreshCommandStateRows();
                }

                if (showStatus)
                {
                    _refreshShellStatus();
                }
            }
        }

        private async Task SetDioOutput(bool isOn, CancellationToken cancellationToken = default)
        {
            var commandName = isOn ? "DIO_ON" : "DIO_OFF";
            if (!TryBeginDioCommand(commandName, recordCommand: true, showStatus: true, "Sending", expectedGeneration: null, out var target))
            {
                return;
            }

            try
            {
                var response = await _manager.automation.SetIo(target.Address, isOn, target.AutomationNo, cancellationToken: cancellationToken);
                EnsureAutomationResponse(response, $"{commandName} output");
                var dioValue = ReadDioValueFromResponse(response);
                var expectedValue = isOn ? "ON" : "OFF";
                if (!dioValue.Equals(expectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"DIO output state mismatch. Requested={expectedValue}, Response={dioValue}.");
                }

                if (!IsCurrentDioGeneration(target.Generation))
                {
                    return;
                }

                DioCurrentValue = dioValue;
                DioLastResult = $"Output {dioValue}";
                _lastResult = DioLastResult;
                _setStatusMessage($"Manual scanner DIO {target.Address} set to {dioValue}.");
            }
            catch (Exception exception)when (exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                if (!IsCurrentDioGeneration(target.Generation))
                {
                    return;
                }

                DioCurrentValue = "UNKNOWN";
                DioLastResult = $"ERROR: {exception.Message}";
                _lastResult = DioLastResult;
                _setStatusMessage($"{commandName} failed. {exception.Message}");
            }
            finally
            {
                IsDioControlBusy = false;
                RefreshCommandStateRows();
                _refreshShellStatus();
            }
        }

        private bool TryBeginDioCommand(string commandName, bool recordCommand, bool showStatus, string pendingResult, long? expectedGeneration, out ST_ACTIVE_DIO_TARGET target)
        {
            target = default;
            if (expectedGeneration.HasValue && !IsCurrentDioGeneration(expectedGeneration.Value))
            {
                return false;
            }

            if (IsAutoProcessRunning())
            {
                var message = "Auto process is running. Manual scanner DIO command is blocked.";
                if (recordCommand)
                {
                    _lastCommand = commandName;
                    _lastResult = message;
                    RefreshCommandStateRows();
                }

                DioLastResult = message;
                if (showStatus)
                {
                    _setStatusMessage(message);
                    _refreshShellStatus();
                }

                return false;
            }

            if (IsDioControlBusy)
            {
                var message = "Manual scanner DIO command is already running.";
                if (recordCommand)
                {
                    _lastCommand = commandName;
                    _lastResult = message;
                    RefreshCommandStateRows();
                }

                if (showStatus)
                {
                    _setStatusMessage(message);
                    _refreshShellStatus();
                }

                return false;
            }

            var generation = Interlocked.Read(ref _dioGeneration);
            if (expectedGeneration.HasValue && generation != expectedGeneration.Value)
            {
                return false;
            }

            if (!IsDioTargetReady || string.IsNullOrWhiteSpace(DioAddress))
            {
                var message = "Manual scanner DIO target is not ready.";
                DioCurrentValue = "UNKNOWN";
                DioLastResult = message;
                if (recordCommand)
                {
                    _lastCommand = commandName;
                    _lastResult = message;
                    RefreshCommandStateRows();
                }

                if (showStatus)
                {
                    _setStatusMessage(message);
                    _refreshShellStatus();
                }

                return false;
            }

            if (recordCommand)
            {
                _lastCommand = commandName;
                _lastResult = pendingResult;
            }

            DioLastResult = pendingResult;
            IsDioControlBusy = true;
            target = new ST_ACTIVE_DIO_TARGET(generation, _dioAutomationNo, DioAddress);
            return true;
        }

        private static string ReadDioValueFromResponse(string response)
        {
            var tokens = response.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Any(IsAutomationFailureToken))
            {
                throw new InvalidOperationException($"Automation1 DIO response failed: {response}");
            }

            bool IsOnOffToken(string token)
            {
                return token.Equals("ON", StringComparison.OrdinalIgnoreCase) || token.Equals("OFF", StringComparison.OrdinalIgnoreCase);
            }

            var value = tokens.LastOrDefault(IsOnOffToken);
            if (value is null)
            {
                throw new InvalidOperationException($"Automation1 DIO response is invalid: {response}");
            }

            return value.ToUpperInvariant();
        }

        private IReadOnlyList<ST_MANUAL_SCANNER_AXIS> ReadManualScannerAxesSnapshot(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int selectedHeadNo)
        {
            _scannerStatusSnapshotReader.SelectHeads(_manualHeadSelectionInitialized ? SelectedManualHeadNos : [selectedHeadNo], settings);
            var snapshot = _scannerStatusSnapshotReader.GetSnapshot().Where(axis => axis.HeadNo == selectedHeadNo).ToArray();
            if (snapshot.Length > 0)
            {
                return snapshot;
            }

            return CManualScannerSnapshotReader.BuildFallbackAxes(settings, selectedHeadNo);
        }

        private static IReadOnlyList<ST_CONTROLLER_HOME_TASK> BuildControllerHomeTasks(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int automationNo)
        {
            const int headCount = 8;
            var axesByTask = new SortedDictionary<int, List<string>>();
            var headsByTask = new SortedDictionary<int, List<int>>();
            var axisSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var headNo = 1; headNo <= headCount; headNo++)
            {
                var taskNo = ReadManualHeadAutomationTaskNo(settings, headNo);
                var definitions = CScannerStatusSnapshotReader.BuildAxisDefinitions(settings, headNo);
                var headHasAxis = false;
                for (var index = 0; index < definitions.Count; index++)
                {
                    var definition = definitions[index];
                    if (definition.AutomationNo != automationNo)
                    {
                        continue;
                    }

                    if (!definition.IsUsed)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(definition.ConfigurationError))
                    {
                        throw new InvalidOperationException(definition.ConfigurationError);
                    }

                    if (string.IsNullOrWhiteSpace(definition.AxisReference))
                    {
                        throw new InvalidOperationException($"H{headNo:00} {definition.AxisName} axis name is not configured.");
                    }

                    if (!axisSet.Add(definition.AxisReference))
                    {
                        continue;
                    }

                    if (!axesByTask.TryGetValue(taskNo, out var axes))
                    {
                        axes = [];
                        axesByTask.Add(taskNo, axes);
                        headsByTask.Add(taskNo, []);
                    }

                    axes.Add(definition.AxisReference);
                    headHasAxis = true;
                }

                if (headHasAxis && headsByTask.TryGetValue(taskNo, out var heads) && !heads.Contains(headNo))
                {
                    heads.Add(headNo);
                }
            }

            var tasks = new List<ST_CONTROLLER_HOME_TASK>(axesByTask.Count);
            foreach (var item in axesByTask)
            {
                var heads = headsByTask.TryGetValue(item.Key, out var taskHeads) ? taskHeads : [];
                tasks.Add(new ST_CONTROLLER_HOME_TASK(item.Key, heads, item.Value));
            }

            return tasks;
        }

        private static string FormatControllerHomeAxisList(IReadOnlyList<ST_CONTROLLER_HOME_TASK> homeTasks)
        {
            var axes = new List<string>();
            foreach (var homeTask in homeTasks)
            {
                for (var index = 0; index < homeTask.AxisReferences.Count; index++)
                {
                    axes.Add(homeTask.AxisReferences[index]);
                }
            }

            return string.Join(",", axes);
        }

        private static string FormatControllerHomeTaskSummary(IReadOnlyList<ST_CONTROLLER_HOME_TASK> homeTasks)
        {
            var items = new List<string>(homeTasks.Count);
            foreach (var homeTask in homeTasks)
            {
                items.Add($"TASK {homeTask.TaskNo}: {string.Join(",", homeTask.AxisReferences)}");
            }

            return string.Join(Environment.NewLine, items);
        }

        private static string BuildControllerHomeScriptFileName(ST_CONTROLLER_HOME_TASK homeTask)
        {
            return $"MANUAL_{FormatControllerHomeHeadSummary(homeTask.HeadNos)}_ALL_HOME.ascript";
        }

        private static string FormatControllerHomeHeadSummary(IReadOnlyList<int> headNos)
        {
            if (headNos.Count == 0)
            {
                return "HEAD";
            }

            var names = new List<string>(headNos.Count);
            for (var index = 0; index < headNos.Count; index++)
            {
                names.Add($"H{headNos[index]:00}");
            }

            return string.Join("_", names);
        }

        private static string ReadScannerAxisTarget(IReadOnlyList<ST_MANUAL_SCANNER_AXIS> axes, string axisRole, string defaultValue = "0.000")
        {
            foreach (var axis in axes)
            {
                if (axis.AxisRole.Equals(axisRole, StringComparison.OrdinalIgnoreCase) && double.TryParse(axis.CurrentPosition, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    return axis.CurrentPosition;
                }
            }

            return defaultValue;
        }

        private static string ReadScannerAxisReference(IReadOnlyList<ST_MANUAL_SCANNER_AXIS> axes, string axisRole, string defaultValue)
        {
            foreach (var axis in axes)
            {
                if (axis.AxisRole.Equals(axisRole, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(axis.AxisReference))
                {
                    return axis.AxisReference;
                }
            }

            return defaultValue;
        }

        private static ST_MANUAL_SCANNER_AXIS? FindScannerAxis(IReadOnlyList<ST_MANUAL_SCANNER_AXIS> axes, string axisRole)
        {
            foreach (var axis in axes)
            {
                if (axis.AxisRole.Equals(axisRole, StringComparison.OrdinalIgnoreCase))
                {
                    return axis;
                }
            }

            return null;
        }

        private static bool IsScannerAxisAvailable(IReadOnlyList<ST_MANUAL_SCANNER_AXIS> axes, string axisRole)
        {
            var axis = FindScannerAxis(axes, axisRole);
            return axis is not null && !IsManualScannerAxisNotUsed(axis) && IsConfiguredScannerAxisName(axis.AxisReference);
        }

        private static ST_SCANNER_AXIS_SETTING? FindScannerAxisDefinition(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo, string axisRole)
        {
            var definitions = CScannerStatusSnapshotReader.BuildAxisDefinitions(settings, headNo);
            foreach (var definition in definitions)
            {
                if (definition.AxisName.Equals(axisRole, StringComparison.OrdinalIgnoreCase))
                {
                    return definition;
                }
            }

            return null;
        }

#endregion
#region Mof
        public CManualMofAlignPanel MofAlign => _mofAlign ??= new(
            _manager.Interface().Melsec, 
            () => CRecipeConfirmDialog.ShowConfirmation("ALIGN REQUEST", "STAGE PC에 얼라인 요청하시겠습니까?"), 
            message => CRecipeConfirmDialog.ShowWarning("STAGE ALIGN", message), 
            _setStatusMessage, 
            () => TryBlockManualAutomationCommand("STAGE_ALIGN_REQUEST"),
            _manager.Alarm().SetSequenceFailure
        );
        private CManualStageMoveOperation ManualStageMoveOperation => _manualStageMoveOperation ??= new(async (axis, value, relative, speed, token) =>
        {
            if (!CheckManualStageMoveAllowed())
                throw new InvalidOperationException("Stage/Review 수동 이동 허용 조건을 확인해 주세요.");
            var interfaces = _manager.Interface();
            var row = interfaces.Melsec.Map.SingleOrDefault(item => item.Use && item.Id.Equals("MOTOR_STAGE_Y_CUR_POS", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("MOTOR_STAGE_Y_CUR_POS 맵이 사용 설정되어 있지 않습니다.");
            await interfaces.StageReviewSequences.MoveManualAxis(row.DeviceNo, axis, value, relative, speed, token);
        },
            stopSharedMove: () => _manager.Interface().StageReviewSequences.RequestMoveStop(ManualStageDeviceNo()),
            sharedMoveRunning: () => _manager.Interface().StageReviewSequences.HasActiveMove(ManualStageDeviceNo()));

        private int ManualStageDeviceNo() => _manager.Interface().Melsec.Map
            .FirstOrDefault(row => row.Use && row.Id.Equals("MOTOR_STAGE_Y_CUR_POS", StringComparison.OrdinalIgnoreCase))?.DeviceNo ?? 0;

        private bool CheckManualStageMoveAllowed()
        {
            if (TryBlockManualAutomationCommand("STAGE_REVIEW_MOVE"))
                return false;
            if (_manager.Station().StageRequests.IsRunning || _manager.Station().StageRequests.NeedsCleanup
                || _manager.Interface().StageReviewSequences.IsActive
                || _manager.Review().SequenceState is EN_REVIEW_SEQUENCE_STATE.Running or EN_REVIEW_SEQUENCE_STATE.Stopping
                || _powerMeasurePanel?.IsRunning == true)
            {
                ShowManualWarning("AUTO/Review/PowerMeasure가 실행 중이거나 정리 대기 중입니다. 정지와 소유권 해제를 확인한 후 이동해 주세요.");
                return false;
            }
            return true;
        }

        public CManualMofStagePanel MofStage => _mofStage ??= new(
            ReadMofStageConnectionState, 
            () => _manager.Interface().GetStageMotionSimulation(), 
            RefreshMofStageSnapshot, 
            message => CRecipeConfirmDialog.ShowConfirmation("READY POSITION MOVE", message), 
            message => CRecipeConfirmDialog.ShowWarning("STAGE Y", message), 
            _setStatusMessage, 
            () => !CheckManualStageMoveAllowed(),
            ManualStageMoveOperation
        );

        private EN_COMM_STATE ReadMofStageConnectionState()
        {
            var interfaces = _manager.Interface();
            var deviceNo = interfaces.Melsec.Map.FirstOrDefault(row => row.Id.Equals("MOTOR_STAGE_Y_CUR_POS", StringComparison.OrdinalIgnoreCase))?.DeviceNo ?? interfaces.GetInterfaceList(EN_EQP_MODULE.Melsec).FirstOrDefault()?.Number;
            var device = interfaces.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).FirstOrDefault(item => item.Number == deviceNo);
            return device?.IsSimulation == true ? EN_COMM_STATE.Simulation : device?.ConnectionState ?? EN_COMM_STATE.Offline;
        }

        private void RefreshMofStageSnapshot()
        {
            var snapshot = _manager.Interface().Melsec.GetCachedSnapshot(out var map);
            MofStage.UpdateSnapshot(map, snapshot);
            MofAlign.UpdateSnapshot(map, snapshot);
            MofRun.SetSelectedHeads(SelectedManualHeadNos);
            MofRun.Refresh();
        }

        public CManualMofRunPanel MofRun => _mofRun ??= new(
            (parameters, heads) => _manager.Station().GetManualInterlockItems(parameters, heads), 
            group => new CInterlockDetailsDialog(group) { Owner = Application.Current?.MainWindow }.ShowDialog(), 
            _setStatusMessage, 
            RunManualMof, 
            StopManualMof, 
            () => _manager.Station().ManualMofStatus
        );

        private async void RunManualMof()
        {
            if (MofRun.IsBusy || TryBlockManualAutomationCommand("MANUAL_MOF_RUN"))
                return;
            if (_powerMeasurePanel?.IsRunning == true)
            {
                ShowManualWarning("PowerMeasure 완료 후 실행해 주세요.");
                return;
            }

            var heads = SelectedManualHeadNos.ToHashSet();
            MofRun.SetRequesting(true);
            SelectHeadCommand.NotifyCanExecuteChanged();
            SelectManualHeadsCommand.NotifyCanExecuteChanged();
            try
            {
                var recipe = await LoadSelectedRecipe(CancellationToken.None);
                if (recipe is null || !recipe.Id.Equals(_selectedRecipeIdProvider(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("현재 선택한 Recipe를 불러올 수 없습니다.");
                MofRun.SetInputs(recipe, _manager.Setting().LoadSection(EN_SETTING_TAB.Option), heads);
                MofRun.Refresh();
                if (MofRun.State != "OK")
                    throw new InvalidOperationException("Interlock/Safety를 확인해 주세요.");
                var shift = _manualMofShiftOffsetProvider();
                var parameters = recipe.Parameters.Where(item => !string.IsNullOrWhiteSpace(item.Key)).GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
                parameters["SHIFT_OFFSET_X"] = shift.X.ToString("0.000000", CultureInfo.InvariantCulture);
                parameters["SHIFT_OFFSET_Y"] = shift.Y.ToString("0.000000", CultureInfo.InvariantCulture);
                if (!CRecipeConfirmDialog.ShowConfirmation("MOF Run", $"선택한 Head {string.Join(", ", heads.Order())}의 MOF 가공을 실행하시겠습니까?\n\nRecipe: {recipe.Name}\nTask 준비 완료 후 현재 Stage Y 위치에서 Glass Size Y만큼 설정 방향으로 이동합니다."))
                    return;
                if (TryBlockManualAutomationCommand("MANUAL_MOF_RUN"))
                    return;
                var request = new ST_STAGE_PROCESS_REQUEST($"MANUAL_MOF_{DateTime.Now:yyyyMMdd_HHmmss_fff}", recipe.Id, recipe.Name, "", parameters);
                var execution = _manager.Station().RunManualMofAsync(request, heads);
                MofRun.RefreshExecution();
                _refreshShellStatus();
                await execution;
                _setStatusMessage("MOF 가공이 완료되었습니다.");
            }
            catch (OperationCanceledException)
            {
                var stopped = _manager.Station().ManualMofStatus;
                _setStatusMessage(stopped.RequiresStop ? stopped.Message : "MOF 가공을 정지했습니다. Stage 이동은 유지됩니다.");
                if (stopped.RequiresStop)
                    CRecipeConfirmDialog.ShowWarning("MOF STOP", stopped.Message);
            }
            catch (Exception exception)
            {
                _setStatusMessage(exception.Message);
                CRecipeConfirmDialog.ShowWarning("MOF Run", exception.Message);
            }
            finally
            {
                MofRun.SetRequesting(false);
                SelectHeadCommand.NotifyCanExecuteChanged();
                SelectManualHeadsCommand.NotifyCanExecuteChanged();
                _refreshShellStatus();
            }
        }

        private async void StopManualMof()
        {
            try
            {
                await _manager.Station().StopManualMofAsync();
            }
            catch (Exception exception)
            {
                _setStatusMessage(exception.Message);
                CRecipeConfirmDialog.ShowWarning("MOF STOP", exception.Message);
            }
            finally
            {
                MofRun.RefreshExecution();
                _refreshShellStatus();
            }
        }

#endregion
#region Motion
        public CManualMotionPanel MotionPanel => _motionPanel ??= new(this);
        private CManualMotionSession MotionSession => _motionSession ??= new();
        internal bool IsMotionConfirmationOpen { get; private set; }

        internal async Task StopMotionJog()
        {
            // A failed stop can open a dialog and cause another window-deactivation event.
            // Coalesce those events while retaining the session's stop target for a later retry.
            if (_stoppingMotionJog)
            {
                MotionSession.Invalidate();
                return;
            }

            _stoppingMotionJog = true;
            try
            {
                await MotionSession.StopJog();
            }
            catch (Exception exception)
            {
                MotionError(exception);
            }
            finally
            {
                _stoppingMotionJog = false;
            }
        }

        private void MotionError(Exception exception)
        {
            _setStatusMessage?.Invoke($"Manual Motion: {exception.Message}");
            if (_motionErrorOpen)
                return;
            _motionErrorOpen = true;
            try
            {
                CRecipeConfirmDialog.ShowWarning("Manual Motion", exception.Message);
            }
            finally
            {
                _motionErrorOpen = false;
            }
        }

        private ST_SCANNER_AXIS_SETTING ValidateMotionAxis(int head, string role, string expectedAxis)
        {
            if (TryBlockManualAutomationCommand("MANUAL_MOTION"))
                throw new InvalidOperationException("자동 운전 중에는 수동 이동할 수 없습니다.");
            if (!IsManualMotionTab || MotionPanel.SelectedHead?.HeadNo != head || !SelectedManualHeadNos.Contains(head))
                throw new InvalidOperationException("이동할 Head를 다시 선택해 주세요.");
            var settings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option);
            if (!CScannerStatusSnapshotReader.BuildEnabledHeadNumbers(settings).Contains(head))
                throw new InvalidOperationException("사용하지 않는 Head입니다.");
            var axis = CScannerStatusSnapshotReader.BuildAxisDefinitions(settings, head).Single(a => a.AxisName == role);
            if (!axis.IsUsed || axis.ConfigurationError.Length > 0 || axis.AxisReference != expectedAxis)
                throw new InvalidOperationException("축 설정을 확인하고 Head를 다시 선택해 주세요.");
            if (!_manager.automation.IsSimul(axis.AutomationNo) && !_manager.automation.IsConnect(axis.AutomationNo))
                throw new InvalidOperationException($"Automation {axis.AutomationNo}: OFFLINE");
            return axis;
        }

        internal async Task RunMotion(CManualMotionAxis row, string mode)
        {
            if (!CheckOperationAlarm("MANUAL MOTION"))
                return;
            if (IsManualMotionScriptActive)
            {
                MotionError(new InvalidOperationException("이동 스크립트가 실행 중입니다. 완료 또는 MOVE STOP 후 실행해 주세요."));
                return;
            }

            if (mode is "CENTER" or "RECIPE")
            {
                await RunMotionScript(row, mode);
                return;
            }

            var generation = MotionSession.Generation;
            var head = MotionPanel.SelectedHead?.HeadNo ?? 0;
            var axisName = row.AxisName;
            var input = mode == "REL" ? row.Relative : row.Absolute;
            try
            {
                var target = ValidateMotionAxis(head, row.Role, axisName);
                var centerY = mode == "CENTER" ? ValidateMotionAxis(head, "Y", MotionPanel.Axes[1].AxisName) : target;
                var value = mode == "CENTER" ? 0 : mode == "RECIPE" ? double.NaN : ReadRequiredDouble(input, axisName);
                var speed = row.Role == "Z" ? 1.0 : ReadManualDouble("JumpSpeed") * 1000.0;
                if (row.Role == "Z")
                {
                    var recipeId = _selectedRecipeIdProvider();
                    var recipe = await LoadSelectedRecipe(default);
                    if (recipe is null || !recipe.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("선택된 Recipe를 확인해 주세요.");
                    speed = ReadHeadRecipeDouble(recipe, head, 1.0, "SCANNER_Z_SPEED");
                    if (mode == "RECIPE")
                        value = ReadHeadRecipeDouble(recipe, head, double.NaN, "DOE_Z_POSITION");
                }

                if (!double.IsFinite(value) || !double.IsFinite(speed) || speed <= 0)
                    throw new InvalidOperationException("이동값과 Recipe/Manual 이동 속도를 확인해 주세요.");
                if (generation != MotionSession.Generation)
                    return;
                if (mode is "ABS" or "REL")
                {
                    IsMotionConfirmationOpen = true;
                    try
                    {
                        var description = mode == "ABS" ? "절대 목표 위치" : "상대 이동 거리";
                        if (!CRecipeConfirmDialog.ShowConfirmation($"{mode} 이동 확인", $"HEAD {head:00} / {axisName}\n{description}: {value:G} mm\n\n이동하시겠습니까?", "이동"))
                            return;
                    }
                    finally
                    {
                        IsMotionConfirmationOpen = false;
                    }
                }

                await MotionSession.Execute(generation, async () =>
                {
                    if (ValidateMotionAxis(head, row.Role, axisName) != target)
                        throw new InvalidOperationException("이동 축 설정이 변경되었습니다.");
                    _manualMotionTargets.Add((target.AutomationNo, target.AxisReference));
                    EnsureAutomationResponse(await _manager.automation.ManualMove(axisName, value, speed, mode == "REL", target.AutomationNo), "Manual Motion");
                    if (mode == "CENTER" && generation == MotionSession.Generation)
                    {
                        if (ValidateMotionAxis(head, "Y", centerY.AxisReference) != centerY)
                            throw new InvalidOperationException("Y축 설정이 변경되었습니다.");
                        _manualMotionTargets.Add((centerY.AutomationNo, centerY.AxisReference));
                        EnsureAutomationResponse(await _manager.automation.ManualMove(centerY.AxisReference, 0, speed, false, centerY.AutomationNo), "CENTER MOVE Y");
                    }

                    _setStatusMessage($"HEAD {head:00} · {axisName} {mode} {value:0.###} mm 명령 전송");
                });
            }
            catch (Exception exception)
            {
                MotionError(exception);
            }
        }

        internal async Task StartMotionJog(CManualMotionAxis row, int direction)
        {
            if (!CheckOperationAlarm("MANUAL JOG"))
                return;
            if (IsManualMotionScriptActive)
            {
                MotionError(new InvalidOperationException("이동 스크립트 실행 중에는 JOG를 시작할 수 없습니다."));
                return;
            }

            var generation = MotionSession.Generation;
            var head = MotionPanel.SelectedHead?.HeadNo ?? 0;
            var axisName = row.AxisName;
            try
            {
                var target = ValidateMotionAxis(head, row.Role, axisName);
                await MotionSession.Execute(generation, async () =>
                {
                    if (ValidateMotionAxis(head, row.Role, axisName) != target)
                        throw new InvalidOperationException("이동 축 설정이 변경되었습니다.");
                    _manualMotionTargets.Add((target.AutomationNo, axisName));
                    EnsureAutomationResponse(await _manager.automation.JogStart(axisName, direction < 0 ? -5.0 : 5.0, target.AutomationNo), "JOG");
                }, async () => EnsureAutomationResponse(await _manager.automation.JogStop(axisName, target.AutomationNo), "JOG STOP"));
            }
            catch (Exception exception)
            {
                // Release any start that may have reached the controller before reporting failure.
                await StopMotionJog();
                MotionError(exception);
            }
        }

        internal async Task StopAllManualMotion()
        {
            _manualMotionScriptCancellation?.Cancel();
            try
            {
                await MotionSession.StopAll(async () =>
                {
                    var targets = new HashSet<(int Controller, string Axis)>(_manualMotionTargets);
                    var errors = new List<string>();
                    try
                    {
                        await StopMotionScriptTask();
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception.Message);
                    }

                    try
                    {
                        var settings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option);
                        foreach (var head in CScannerStatusSnapshotReader.BuildEnabledHeadNumbers(settings))
                            foreach (var axis in CScannerStatusSnapshotReader.BuildAxisDefinitions(settings, head))
                                if (axis.IsUsed && axis.ConfigurationError.Length == 0)
                                    targets.Add((axis.AutomationNo, axis.AxisReference));
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception.Message);
                    }

                    foreach (var target in targets)
                    {
                        try
                        {
                            EnsureAutomationResponse(await _manager.automation.AbortMotion(target.Axis, target.Controller), "MOVE STOP");
                        }
                        catch (Exception exception)
                        {
                            errors.Add($"Automation {target.Controller} / {target.Axis}: {exception.Message}");
                        }
                    }

                    if (errors.Count > 0)
                        throw new InvalidOperationException(string.Join("\n", errors));
                    _manualMotionStopPending = false;
                    _setStatusMessage("모든 Head 축에 MOVE STOP 명령을 전송했습니다.");
                });
            }
            catch (Exception exception)
            {
                MotionError(exception);
            }
        }

        private bool IsManualMotionScriptActive => _manualMotionStopPending || _manualMotionScriptCancellation is not null || _manualMotionScriptTask is not null;

        private async Task RunMotionScript(CManualMotionAxis row, string mode)
        {
            var generation = MotionSession.Generation;
            var head = MotionPanel.SelectedHead?.HeadNo ?? 0;
            CancellationTokenSource? run = null;
            var attemptedAxes = new List<(int Controller, string Axis)>();
            try
            {
                var axis = ValidateMotionAxis(head, row.Role, row.AxisName);
                var y = mode == "CENTER" ? ValidateMotionAxis(head, "Y", MotionPanel.Axes[1].AxisName) : axis;
                var options = _manager.Setting().LoadSection(EN_SETTING_TAB.Option);
                var task = ReadManualHeadAutomationTaskNo(options, head);
                var position = 0.0;
                var speed = mode == "CENTER" ? ReadManualDouble("JumpSpeed") * 1000.0 : 1.0;
                var ramp = 500000.0;
                if (mode == "RECIPE")
                {
                    var recipe = await LoadExactMotionRecipe(default);
                    position = ReadMotionRecipeValue(recipe, head, "DOE_Z_POSITION", double.NaN);
                    speed = ReadMotionRecipeValue(recipe, head, "SCANNER_Z_SPEED", 1.0);
                    ramp = ReadMotionRecipeValue(recipe, head, "SCANNER_Z_RAMP_RATE", 500000.0);
                }

                if (!double.IsFinite(speed) || speed <= 0 || !double.IsFinite(ramp) || ramp <= 0)
                    throw new InvalidDataException("Recipe/Manual 이동 속도와 가속도를 확인해 주세요.");
                if (generation != MotionSession.Generation || IsManualMotionScriptActive)
                    return;
                run = new CancellationTokenSource();
                _manualMotionScriptCancellation = run;
                var token = run.Token;
                var name = $"MANUAL_H{head:00}_{(mode == "CENTER" ? "CENTER_MOVE" : "Z_RECIPE_POSITION_MOVE")}.ascript";
                var script = _scriptFile.Create(name);
                script.SetDeviceNo(head - 1);
                script.Start($"Manual HEAD {head:00} {mode}");
                AppendMotionPositionScript(script, mode, axis.AxisReference, y.AxisReference, position, speed, ramp);
                script.End();
                var saved = script.Save(token);
                string? response = null;
                var launchedAt = 0L;
                await MotionSession.Execute(generation, async () =>
                {
                    if (ValidateMotionAxis(head, row.Role, row.AxisName) != axis || mode == "CENTER" && ValidateMotionAxis(head, "Y", y.AxisReference) != y)
                        throw new InvalidOperationException("이동 축 설정이 변경되었습니다.");
                    await EnsureMotionScriptTaskReady(axis.AutomationNo, task, token);
                    EnsureAutomationResponse(await _manager.automation.UploadScript(saved.FilePath, saved.FileName, axis.AutomationNo, token), "Motion script upload");
                    token.ThrowIfCancellationRequested();
                    if (generation != MotionSession.Generation)
                        return;
                    await EnsureMotionScriptTaskReady(axis.AutomationNo, task, token);
                    token.ThrowIfCancellationRequested();
                    if (generation != MotionSession.Generation)
                        return;
                    _manualMotionTargets.Add((axis.AutomationNo, axis.AxisReference));
                    if (mode == "CENTER")
                        _manualMotionTargets.Add((y.AutomationNo, y.AxisReference));
                    _manualMotionScriptTask = (axis.AutomationNo, task);
                    attemptedAxes.Add((axis.AutomationNo, axis.AxisReference));
                    if (mode == "CENTER")
                        attemptedAxes.Add((y.AutomationNo, y.AxisReference));
                    response = await _manager.automation.RunScript(saved.FileName, task, axis.AutomationNo, token);
                    EnsureAutomationResponse(response, "Motion script run");
                    launchedAt = Stopwatch.GetTimestamp();
                });
                if (response is null)
                    return;
                _setStatusMessage($"HEAD {head:00}: {saved.FileName} 실행 중");
                await WaitMotionScriptFinished(response, axis.AutomationNo, task, launchedAt, token);
                _manualMotionScriptTask = null;
                _setStatusMessage($"HEAD {head:00}: {saved.FileName} 완료");
            }
            catch (Exception exception)
            {
                var stopErrors = new List<string>();
                try
                {
                    await StopMotionScriptTask();
                }
                catch (Exception stopFailure)
                {
                    stopErrors.Add(stopFailure.Message);
                }

                foreach (var target in attemptedAxes)
                {
                    try
                    {
                        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        EnsureAutomationResponse(await _manager.automation.AbortMotion(target.Axis, target.Controller, stop.Token).WaitAsync(stop.Token), "Motion STOP");
                    }
                    catch (Exception stopFailure)
                    {
                        stopErrors.Add($"{target.Axis}: {stopFailure.Message}");
                    }
                }

                if (stopErrors.Count > 0)
                {
                    _manualMotionStopPending = true;
                    exception = new InvalidOperationException($"{exception.Message}\n정지 실패 (MOVE STOP으로 다시 정지해 주세요): {string.Join("\n", stopErrors)}", exception);
                }

                if (exception is not OperationCanceledException)
                    MotionError(exception);
            }
            finally
            {
                if (ReferenceEquals(_manualMotionScriptCancellation, run))
                    _manualMotionScriptCancellation = null;
                run?.Dispose();
            }
        }

        private static void AppendMotionPositionScript(CAutomation1ScriptBase script, string mode, string axis, string yAxis, double position, double speed, double ramp)
        {
            if (mode == "RECIPE")
            {
                AppendManualZMoveScript(script, axis, position, speed, ramp);
                return;
            }

            script.SetAxis(axis, yAxis);
            script.AddLine("IfovOff()");
            script.AddLine("SetupTaskDistanceUnits(DistanceUnits.Primary)");
            script.AddLine("SetupTaskTimeUnits(TimeUnits.Seconds)");
            script.AddLine("VelocityBlendingOff()");
            script.AddLine("SetupTaskWaitMode(WaitMode.Auto)");
            script.AddLine("SetupTaskTargetMode(TargetMode.Absolute)");
            script.SetJumpSpeed(speed / 1000.0);
            script.Jump(0, 0);
            script.WaitMoveDone();
        }

        private async Task EnsureMotionScriptTaskReady(int controller, int task, CancellationToken token)
        {
            var response = await _manager.automation.ReadTaskStatus(task, controller, token);
            var state = ReadMotionTaskState(response, task);
            if (state is not "Idle" and not "ProgramComplete")
                throw new InvalidOperationException($"CONTROLLER {controller + 1} Task {task}: {state}");
        }

        private async Task WaitMotionScriptFinished(string response, int controller, int task, long launchedAt, CancellationToken token)
        {
            var state = ReadMotionTaskState(response, task);
            if (_manager.automation.IsSimul(controller) && state == "Idle" || state == "ProgramComplete")
                return;
            var started = state is "Running" or "ProgramRunning";
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(60))
            {
                token.ThrowIfCancellationRequested();
                var snapshot = _manager.automation.StatusSnapshots.GetTask(task, controller);
                if (snapshot.State is "ERROR" or "OFFLINE" or "STOPPED")
                    throw new InvalidOperationException(snapshot.Detail);
                if (snapshot.State == "OK" && snapshot.Value is { } status && snapshot.CollectedTimestamp > launchedAt)
                {
                    state = ReadMotionTaskState(status, task);
                    if (state == "ProgramComplete" || started && state == "Idle")
                        return;
                    if (state is "Error" or "Unavailable" or "Inactive")
                        throw new InvalidOperationException(status);
                    started |= state is "Running" or "ProgramRunning";
                }

                await Task.Delay(200, token);
            }

            throw new TimeoutException($"CONTROLLER {controller + 1} Task {task} 이동 완료 확인 시간 초과");
        }

        private static string ReadMotionTaskState(string response, int task)
        {
            var fields = response.Split(':');
            if (fields.Length < 4 || fields[0] != "OK" || fields[1] != "TASK" || fields[2] != task.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidOperationException($"이동 Task 응답이 올바르지 않습니다: {response}");
            return fields[3];
        }

        private async Task StopMotionScriptTask()
        {
            if (_manualMotionScriptTask is not { } target)
                return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            EnsureAutomationResponse(await _manager.automation.StopTask(target.Task, target.Controller, timeout.Token).WaitAsync(timeout.Token), "Motion script STOP");
            _manualMotionScriptTask = null;
        }

        private void StopStageMotionPolling()
        {
            if (!_stageMotionPollingService.IsRunning)
            {
                return;
            }

            try
            {
                _stageMotionPollingService.Stop();
            }
            catch (TimeoutException exception)
            {
                _setStatusMessage($"Manual stage motion polling stop timeout. {exception.Message}");
            }
        }

        private void RefreshStageMotionSnapshot()
        {
            _stageMotionPollingService.SelectAxes(StageAxes);
            _stageMotionPollingService.Start();
            var snapshot = _stageMotionPollingService.GetSnapshot();
            foreach (var axis in StageAxes)
            {
                if (snapshot.TryGetValue(axis.Key, out var value))
                {
                    axis.CurrentPosition = value.Position;
                    axis.CurrentSpeed = value.Speed;
                    axis.Status = value.State;
                    axis.StatusMessage = value.Message;
                }
            }
        }

        private void SelectStageAxis(object? parameter)
        {
            if (parameter is not ST_MANUAL_STAGE_AXIS axis)
            {
                return;
            }

            SelectedStageAxis = axis;
        }

        private async void StageAbsMove(object? parameter)
        {
            await ExecuteMotionMove("ABS", "절대 이동");
        }

        private async void StageRelMove(object? parameter)
        {
            await ExecuteMotionMove("REL", "상대 이동");
        }

        private async Task ExecuteMotionMove(string moveMode, string confirmationTitle)
        {
            if (!CheckManualStageMoveAllowed())
                return;
            var axis = SelectedStageAxis;
            if (!double.TryParse(axis.TargetPosition, NumberStyles.Float, CultureInfo.InvariantCulture, out var targetPosition) || !double.IsFinite(targetPosition))
            {
                var message = $"{axis.DisplayName} position is not a valid number: {axis.TargetPosition}";
                _setStatusMessage(message);
                ShowManualWarning(message);
                return;
            }

            if (!double.TryParse(axis.TargetSpeed, NumberStyles.Float, CultureInfo.InvariantCulture, out var targetSpeed) || !double.IsFinite(targetSpeed) || targetSpeed <= 0.0)
            {
                var message = $"{axis.DisplayName} speed is not a valid positive number: {axis.TargetSpeed}";
                _setStatusMessage(message);
                ShowManualWarning(message);
                return;
            }

            if (!CRecipeConfirmDialog.ShowConfirmation("Motion " + confirmationTitle, $"{axis.DisplayName} 축을 {moveMode} 모드로 이동하시겠습니까?\nPOS={targetPosition:0.###} {axis.Unit}\nSPEED={targetSpeed:0.###} {axis.SpeedUnit}\n공통 Process Move 시퀀스로 이동합니다."))
            {
                return;
            }

            _setStatusMessage($"{axis.DisplayName} {moveMode} move sending. Pos={targetPosition:0.###} {axis.Unit}.");
            try
            {
                var moveAxis = axis.Key switch
                {
                    "STAGE_Y" => EN_MANUAL_STAGE_AXIS.StageY,
                    "CAMERA_X" => EN_MANUAL_STAGE_AXIS.ReviewX,
                    _ => throw new InvalidOperationException("지원하지 않는 Stage/Review 축입니다.")
                };
                await ManualStageMoveOperation.Move(moveAxis, targetPosition, moveMode == "REL", targetSpeed);
                _setStatusMessage($"{axis.DisplayName} {moveMode} 이동 완료 · Process Move.");
                RefreshStageMotionSnapshot();
            }
            catch (OperationCanceledException)
            {
                _setStatusMessage("Stage/Review 이동 중지. 공통 시퀀스 정리 상태를 확인하세요.");
            }
            catch (Exception exception)when (exception is InvalidOperationException or InvalidDataException or IOException or TimeoutException or ArgumentException or OverflowException)
            {
                var message = $"{axis.DisplayName} {moveMode} move failed. {exception.Message}";
                _setStatusMessage(message);
                ShowManualWarning(message);
            }
            finally
            {
                _refreshShellStatus();
            }
        }

        private void StageStop(object? parameter)
        {
            var axis = SelectedStageAxis;
            ManualStageMoveOperation.Stop(includeSharedMove: true);
            _setStatusMessage($"{axis.DisplayName} STOP 요청 · 공통 이동 신호 정리 대기.");
            _refreshShellStatus();
        }

        private void StageHome(object? parameter)
        {
            var axis = SelectedStageAxis;
            if (!CRecipeConfirmDialog.ShowConfirmation("Motion 원점 복귀", $"{axis.DisplayName} 축을 원점으로 복귀하시겠습니까?"))
            {
                return;
            }

            _setStatusMessage($"{axis.DisplayName} home requested (UI only).");
            _refreshShellStatus();
        }

        private void RefreshStageAxisSelection()
        {
            foreach (var axis in StageAxes)
            {
                axis.IsSelected = ReferenceEquals(axis, SelectedStageAxis);
            }
        }

        private async Task CenterMove()
        {
            if (!CRecipeConfirmDialog.ShowConfirmation("중앙 이동", "스캐너를 중앙으로 이동하시겠습니까?"))
            {
                return;
            }

            void AppendCenterMoveScript(global::Drilling.Common.Station.CAutomation1ScriptBase script, global::Drilling.Common.Managers.ST_MANUAL_SCAN_PARAM arg1)
            {
                script.Jump(0.0, 0.0);
                script.WaitMoveDone();
            }

            await RunManualScript("CENTER_MOVE", AppendCenterMoveScript);
        }

        private async Task PositionMove()
        {
            ST_MANUAL_SCRIPT_AXES axes;
            try
            {
                var headNo = ReadEnabledManualScannerHeadNo(CancellationToken.None);
                axes = ReadManualScriptAxes(headNo, CancellationToken.None);
            }
            catch (Exception exception)when (exception is InvalidDataException or InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                _lastCommand = "POSITION_MOVE";
                _lastResult = $"ERROR: {exception.Message}";
                _setStatusMessage($"POSITION_MOVE failed. {exception.Message}");
                RefreshCommandStateRows();
                _refreshShellStatus();
                return;
            }

            if (!CRecipeConfirmDialog.ShowConfirmation("위치 이동", $"스캐너를 {axes.XAxis} = {TargetGx}, {axes.YAxis} = {TargetGy} 위치로 이동하시겠습니까?"))
            {
                return;
            }

            void AppendPositionMoveScript(global::Drilling.Common.Station.CAutomation1ScriptBase script, global::Drilling.Common.Managers.ST_MANUAL_SCAN_PARAM arg1)
            {
                var gx = ReadRequiredDouble(TargetGx, $"{axes.XAxis} Target");
                var gy = ReadRequiredDouble(TargetGy, $"{axes.YAxis} Target");
                script.Jump(gx, gy);
                script.WaitMoveDone();
            }

            await RunManualScript("POSITION_MOVE", AppendPositionMoveScript);
        }

        private async Task ZPositionMove()
        {
            var commandName = "Z_MOVE";
            if (TryBlockManualAutomationCommand(commandName))
            {
                return;
            }

            ST_MANUAL_SCRIPT_Z_AXIS zAxis;
            int headNo;
            double targetPosition;
            double zSpeed;
            double zRampRate;
            try
            {
                headNo = ReadEnabledManualScannerHeadNo(CancellationToken.None);
                zAxis = ReadManualScriptZAxis(headNo, CancellationToken.None);
                targetPosition = ReadRequiredDouble(TargetGz, $"{zAxis.Axis} Target");
                var selectedRecipe = await LoadSelectedRecipe(CancellationToken.None);
                zSpeed = ReadHeadRecipeDouble(selectedRecipe, headNo, 1.0, "SCANNER_Z_SPEED");
                zRampRate = ReadHeadRecipeDouble(selectedRecipe, headNo, 500000.0, "SCANNER_Z_RAMP_RATE");
                if (!double.IsFinite(zSpeed) || zSpeed <= 0.0)
                {
                    throw new InvalidDataException($"H{headNo:00} Scanner Z Speed must be greater than 0. Value={zSpeed.ToString(CultureInfo.InvariantCulture)}");
                }

                if (!double.IsFinite(zRampRate) || zRampRate <= 0.0)
                {
                    throw new InvalidDataException($"H{headNo:00} Scanner Z Ramp Rate must be greater than 0. Value={zRampRate.ToString(CultureInfo.InvariantCulture)}");
                }
            }
            catch (Exception exception)when (exception is InvalidDataException or InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                _lastCommand = commandName;
                _lastResult = $"ERROR: {exception.Message}";
                _setStatusMessage($"{commandName} failed. {exception.Message}");
                RefreshCommandStateRows();
                _refreshShellStatus();
                CRecipeConfirmDialog.ShowWarning("Z축 이동 실패", $"Z축 이동 조건을 확인하지 못했습니다.\n{exception.Message}");
                return;
            }

            if (!_manager.automation.IsSimul(zAxis.AutomationNo) && !_manager.automation.IsConnect(zAxis.AutomationNo))
            {
                _lastCommand = commandName;
                _lastResult = "ERROR: OFFLINE";
                _setStatusMessage($"{commandName} failed. Automation{zAxis.AutomationNo.ToString(CultureInfo.InvariantCulture)} is offline.");
                RefreshCommandStateRows();
                _refreshShellStatus();
                CRecipeConfirmDialog.ShowWarning("Z축 이동 실패", "Automation 장비가 연결되어 있지 않아 Z축 이동을 실행할 수 없습니다.");
                return;
            }

            if (!CRecipeConfirmDialog.ShowConfirmation("Z축 이동", $"{zAxis.Axis}축을 {TargetGz} 위치로 이동하시겠습니까?"))
            {
                return;
            }

            await RunManualZPositionScript(headNo, zAxis, targetPosition, zSpeed, zRampRate);
        }

        private async Task MoveStop()
        {
            await StopManualTask("MOVE_STOP");
        }

        private async Task RunManualZPositionScript(int headNo, ST_MANUAL_SCRIPT_Z_AXIS zAxis, double targetPosition, double zSpeed, double zRampRate, CancellationToken cancellationToken = default)
        {
            var commandName = "Z_MOVE";
            CancelStatusPolling();
            _lastCommand = commandName;
            _lastResult = "Building";
            try
            {
                var fileName = $"MANUAL_H{headNo:00}_Z_MOVE.ascript";
                var script = _scriptFile.Create(fileName);
                script.SetDeviceNo(headNo - 1);
                script.Start($"Manual H{headNo:00} {commandName}");
                AppendManualZMoveScript(script, zAxis.Axis, targetPosition, zSpeed, zRampRate);
                script.End();
                var savedScript = script.Save(cancellationToken);
                var uploadResponse = await _manager.automation.UploadScript(savedScript.FilePath, savedScript.FileName, zAxis.AutomationNo, cancellationToken);
                EnsureAutomationResponse(uploadResponse, $"{commandName} upload");
                var runResponse = await _manager.automation.RunScript(savedScript.FileName, ManualTaskIndex, zAxis.AutomationNo, cancellationToken);
                EnsureAutomationResponse(runResponse, $"{commandName} run");
                _lastResult = $"Running {savedScript.FileName}";
                _setStatusMessage($"{commandName} script uploaded and started: {savedScript.FileName}");
                var statusCancellation = CreateStatusPollingCancellation(cancellationToken);
                var statusToken = statusCancellation.Token;
                try
                {
                    await WaitManualTaskCompleted(runResponse, requireExecutionStart: !_manager.automation.IsSimul(zAxis.AutomationNo), automationNo: zAxis.AutomationNo, taskNo: ManualTaskIndex, cancellationToken: statusToken);
                    statusToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    ReleaseStatusPollingCancellation(statusCancellation);
                }

                TargetGz = FormatDouble(targetPosition, 3);
                _lastResult = $"Completed {savedScript.FileName}";
                _setStatusMessage($"{commandName} completed: {savedScript.FileName}");
                RefreshCommandStateRows();
                _refreshShellStatus();
            }
            catch (OperationCanceledException)
            {
            // 화면을 벗어나면 완료 상태 조회와 후속 팝업만 중단한다.
            // 이미 Automation1에 전달된 실제 동작은 별도의 정지 명령 없이 계속된다.
            }
            catch (Exception exception)when (exception is InvalidDataException or InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                _lastResult = $"ERROR: {exception.Message}";
                _setStatusMessage($"{commandName} failed. {exception.Message}");
                RefreshCommandStateRows();
                _refreshShellStatus();
                CRecipeConfirmDialog.ShowWarning("Z축 이동 실패", $"Z축 이동을 완료하지 못했습니다.\n{exception.Message}");
            }
        }

        private static void AppendManualZMoveScript(CAutomation1ScriptBase script, string zAxis, double targetPosition, double zSpeed, double zRampRate)
        {
            script.AddLine("IfovOff()");
            script.AddLine("SetupTaskDistanceUnits(DistanceUnits.Primary)");
            script.AddLine("SetupTaskTimeUnits(TimeUnits.Seconds)");
            script.AddLine("VelocityBlendingOff()");
            script.AddLine("SetupTaskWaitMode(WaitMode.Auto)");
            script.AddLine("SetupTaskTargetMode(TargetMode.Absolute)");
            script.AddLine($"SetupAxisRampType([{zAxis}], RampType.Sine)");
            script.AddLine($"SetupAxisRampValue([{zAxis}], RampMode.Rate, {FormatDouble(zRampRate, 6)})");
            script.AddLine($"MoveAbsolute({zAxis}, {FormatDouble(targetPosition, 6)}, {FormatDouble(zSpeed, 6)})");
            script.AddLine($"WaitForMotionDone({zAxis})");
        }

        private ST_MANUAL_SCRIPT_Z_AXIS ReadManualScriptZAxis(int headNo, CancellationToken cancellationToken)
        {
            var optionSettings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option, cancellationToken);
            var zAxis = FindScannerAxisDefinition(optionSettings, headNo, "Z");
            if (zAxis is null || !zAxis.Value.IsUsed)
            {
                throw new InvalidOperationException($"H{headNo:00} scanner Z axis is not used.");
            }

            if (!string.IsNullOrWhiteSpace(zAxis.Value.ConfigurationError))
            {
                throw new InvalidOperationException(zAxis.Value.ConfigurationError);
            }

            if (!IsConfiguredScannerAxisName(zAxis.Value.AxisReference))
            {
                throw new InvalidOperationException($"H{headNo:00} scanner Z axis settings are not configured.");
            }

            return new ST_MANUAL_SCRIPT_Z_AXIS(zAxis.Value.AxisReference, zAxis.Value.AutomationNo);
        }

        private static bool HasReadableScannerPosition(IReadOnlyList<ST_MANUAL_SCANNER_AXIS> axes)
        {
            foreach (var axis in axes)
            {
                if (double.TryParse(axis.CurrentPosition, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    return true;
                }
            }

            return false;
        }

#endregion
#region Scan
        public CManualScanAssignments ScanAssignments => _scanAssignments ??= new();

        private void RefreshManualScanAssignments() => ScanAssignments.Update(SelectedManualHeadNos, _manualScanSettingNames, LoadedSettingName);
        private Task<ST_MANUAL_SCAN_PARAM> ReadAssignedManualScanParam(int headNo, CancellationToken cancellationToken) => ReadAssignedManualScanParamCore(headNo, cancellationToken, validateShape: true);
        private async Task<ST_MANUAL_SCAN_PARAM> ReadAssignedManualScanParamCore(int headNo, CancellationToken cancellationToken, bool validateShape)
        {
            if (!SelectedManualHeadNos.Contains(headNo))
                throw new InvalidDataException("이동할 Head를 먼저 선택해 주세요.");
            var name = ScanAssignments.GetSettingName(headNo);
            var files = await _scanFile.List(cancellationToken);
            var file = files.FirstOrDefault(file => file.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(file))
                throw new InvalidDataException($"HEAD {headNo:00}에 사용할 .scan 파일을 지정해 주세요.");
            var settings = await _scanFile.Load(file, cancellationToken);
            var forms = await _scanFile.LoadForm(cancellationToken);
            if (validateShape)
                ValidateManualScanParameters(settings, forms);
            else
                ValidateSavedManualLaserActionSettings(settings);
            return settings;
        }

        private IReadOnlyDictionary<int, (double X, double Y)> CaptureManualScanOrigins(IReadOnlyList<ST_MANUAL_SHAPE_SCAN_ROUTE> routes, IReadOnlyList<ST_SYSTEM_PARAMETER> options, bool useCurrentPosition)
        {
            var origins = new Dictionary<int, (double X, double Y)>();
            // Capture every selected Head before the first asynchronous file read or confirmation.
            foreach (var route in routes)
            {
                if (!_manager.automation.IsSimul(route.AutomationNo) && !_manager.automation.IsConnect(route.AutomationNo))
                    throw new InvalidOperationException($"CONTROLLER {route.AutomationNo + 1}이 연결되어 있지 않습니다.");
                if (!useCurrentPosition)
                {
                    origins.Add(route.HeadNo, (0, 0));
                    continue;
                }

                var definitions = CScannerStatusSnapshotReader.BuildAxisDefinitions(options, route.HeadNo);
                double ReadPosition(string role)
                {
                    var axis = definitions.Single(definition => definition.AxisName == role);
                    var snapshot = _manager.automation.StatusSnapshots.GetAxis(axis.AxisReference, axis.AutomationNo, axis.AioNo, axis.ReadExtendedStatus);
                    if (snapshot.State != "OK" || snapshot.Value is not { } status || !double.IsFinite(status.PositionFeedback))
                        throw new InvalidOperationException($"HEAD {route.HeadNo:00} / {axis.AxisReference}의 현재 위치를 확인할 수 없습니다.\n" + $"상태: {snapshot.State}\n공통 위치 스냅샷이 갱신된 후 CurPos를 다시 눌러 주세요.");
                    return status.PositionFeedback;
                }

                origins.Add(route.HeadNo, (ReadPosition("X"), ReadPosition("Y")));
            }

            return origins;
        }

        private static ST_MANUAL_SCAN_POSITION ResolveManualScanPosition(
            ST_MANUAL_SHAPE_SCAN_ROUTE route, 
            IReadOnlyList<ST_SYSTEM_PARAMETER> options, 
            ST_MANUAL_SCAN_PARAM settings, 
            (double X, double Y) origin, 
            bool useCurrentPosition
        )
        {
            var offset = CScannerAxisTransform.DesignOffsetToScanner(options, route.HeadNo, settings.OffsetX, settings.OffsetY);
            var position = new ST_MANUAL_SCAN_POSITION(useCurrentPosition, route.XAxis, route.YAxis, origin.X, origin.Y, offset.X, offset.Y);
            if (!double.IsFinite(position.TargetX) || !double.IsFinite(position.TargetY))
                throw new InvalidDataException($"HEAD {route.HeadNo:00}의 Offset 적용 위치를 계산할 수 없습니다.");
            return position;
        }

        private static string BuildManualScanConfirmation(IReadOnlyList<ST_MANUAL_SHAPE_SCAN_PROGRAM> programs, bool shapeScan)
        {
            var heads = programs.Select(program =>
            {
                var position = program.Position ?? throw new InvalidOperationException("가공 위치가 준비되지 않았습니다.");
                var action = shapeScan ? program.Settings.ShapeName : $"{program.Settings.LaserOnMode} / Repeat {program.Settings.RepeatCount}";
                var heading = $"HEAD {program.HeadNo:00} · {program.SettingName} / {action}";
                if (!position.UseCurrentPosition)
                    return $"{heading}\nCenter (0, 0) + Offset = {position.XAxis} {Coordinate(position.TargetX)} mm / {position.YAxis} {Coordinate(position.TargetY)} mm";
                return $"{heading}\nCurPos {position.XAxis} {Coordinate(position.BaseX)} mm + Offset {Coordinate(position.OffsetX)} mm = {Coordinate(position.TargetX)} mm\n" + $"CurPos {position.YAxis} {Coordinate(position.BaseY)} mm + Offset {Coordinate(position.OffsetY)} mm = {Coordinate(position.TargetY)} mm";
            });
            var purpose = shapeScan ? "해당 위치를 중심으로 형상 가공하시겠습니까?" : "해당 위치에서 레이저를 동작시키시겠습니까?";
            return $"각 Head의 .scan 출력(W)에 맞춰 ATT를 이동합니다.\nOffset은 헤드별 축 좌표로 변환된 값입니다.\n\n{string.Join("\n\n", heads)}\n\n{purpose}";
        }

        private static string Coordinate(double value) => (Math.Round(value, 3) == 0 ? 0 : value).ToString("F3", CultureInfo.InvariantCulture);
        private sealed record ST_MANUAL_SCAN_POSITION(bool UseCurrentPosition, string XAxis, string YAxis, double BaseX, double BaseY, double OffsetX, double OffsetY)
        {
            public double TargetX => BaseX + OffsetX;
            public double TargetY => BaseY + OffsetY;
            public string ScriptComment => FormattableString.Invariant($"// Position Reference={(UseCurrentPosition ? "CurPos" : "Center")}; Base=({BaseX:R}, {BaseY:R}) mm; Offset=({OffsetX:R}, {OffsetY:R}) mm; Target=({TargetX:R}, {TargetY:R}) mm");
        }

        private bool IsManualShapeScanActive => _activeManualShapeScan is not null || _manualShapeScanStopInProgress || _manualShapeScanPendingStops is { Count: > 0 } || _manualShapeScanAttenuatorPositioning?.HasPendingStops == true || IsManualLaserActionActive;

        private async Task RunSelectedManualShapeScans(bool useCurrentPosition)
        {
            if (IsManualShapeScanActive)
            {
                CRecipeConfirmDialog.ShowWarning("형상 스캔", "수동 가공이 진행 중이거나 정지가 확인되지 않은 Head가 있습니다. LASER OFF 또는 STOP으로 정지한 후 다시 시도해 주세요.");
                return;
            }

            if (TryBlockManualAutomationCommand("SHAPE_START"))
                return;
            var run = new CManualShapeScanRun();
            _activeManualShapeScan = run;
            try
            {
                var programs = await PrepareManualShapeScanPrograms(run.Cancellation.Token, useCurrentPosition);
                run.Programs = programs;
                if (!CRecipeConfirmDialog.ShowConfirmation("형상 레이저 가공", BuildManualScanConfirmation(programs, shapeScan: true)))
                    return;
                run.Cancellation.Token.ThrowIfCancellationRequested();
                if (TryBlockManualAutomationCommand("SHAPE_START"))
                    return;
                _lastCommand = "SHAPE_START";
                _lastResult = $"Preparing ATT / {programs.Count} Heads";
                RefreshCommandStateRows();
                _setStatusMessage($"선택한 {programs.Count}개 Head의 ATT를 설정 출력(W)에 맞는 각도로 이동한 후 형상 가공을 시작합니다.");
                _refreshShellStatus();
                await ExecuteManualShapeScanPrograms(programs, run.Cancellation.Token);
                _lastResult = $"Completed {programs.Count} Heads";
                _setStatusMessage($"선택한 {programs.Count}개 Head의 형상 가공이 완료되었습니다.");
            }
            catch (OperationCanceledException)
            {
                _laserState = "OFF";
                _lastResult = "Stopped";
                _setStatusMessage("선택한 Head의 형상 가공을 정지했습니다.");
            }
            catch (Exception exception)when (exception is InvalidDataException or InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                _lastResult = $"ERROR: {exception.Message}";
                _setStatusMessage($"형상 가공을 완료하지 못했습니다. {exception.Message}");
                CRecipeConfirmDialog.ShowWarning("형상 스캔 실패", exception.Message);
            }
            finally
            {
                _activeManualShapeScan = null;
                run.Completed.TrySetResult();
                run.Cancellation.Dispose();
                RefreshCommandStateRows();
                _refreshShellStatus();
            }
        }

        private async Task StopSelectedManualShapeScans()
        {
            var run = _activeManualShapeScan;
            if (run is null)
            {
                if (_manualShapeScanStopInProgress)
                    return;
                var pending = _manualShapeScanPendingStops ?? [];
                if (pending.Count == 0 && _manualShapeScanAttenuatorPositioning?.HasPendingStops != true)
                {
                    _setStatusMessage("진행 중인 형상 가공이 없습니다.");
                    return;
                }

                _manualShapeScanStopInProgress = true;
                try
                {
                    var failures = (await StopManualShapeScanPrograms(pending)).ToList();
                    if (_manualShapeScanAttenuatorPositioning?.HasPendingStops == true)
                    {
                        try
                        {
                            await _manualShapeScanAttenuatorPositioning.StopPendingAsync();
                        }
                        catch (Exception exception)
                        {
                            failures.Add(exception.Message);
                        }
                    }

                    if (failures.Count > 0)
                        CRecipeConfirmDialog.ShowWarning("형상 스캔 정지 실패", string.Join("\n", failures));
                    else
                    {
                        _laserState = "OFF";
                        _lastResult = "Stopped";
                        _setStatusMessage("형상 가공 Head의 정지를 확인했습니다.");
                    }
                }
                finally
                {
                    _manualShapeScanStopInProgress = false;
                    RefreshCommandStateRows();
                    _refreshShellStatus();
                }

                return;
            }

            _lastCommand = "SHAPE_STOP";
            run.Cancellation.Cancel();
            await run.Completed.Task;
        }

        private async Task<IReadOnlyList<ST_MANUAL_SHAPE_SCAN_PROGRAM>> PrepareManualShapeScanPrograms(CancellationToken cancellationToken, bool useCurrentPosition)
        {
            var options = _manager.Setting().LoadSection(EN_SETTING_TAB.Option, cancellationToken);
            var routes = BuildManualShapeScanRoutes(options, SelectedManualHeadNos);
            var origins = CaptureManualScanOrigins(routes, options, useCurrentPosition);
            var programs = new List<ST_MANUAL_SHAPE_SCAN_PROGRAM>(routes.Count);
            foreach (var route in routes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_manager.automation.IsSimul(route.AutomationNo) && !_manager.automation.IsConnect(route.AutomationNo))
                    throw new InvalidOperationException($"CONTROLLER {route.AutomationNo + 1}이 연결되어 있지 않습니다.");
                ST_MANUAL_SCAN_PARAM settings;
                try
                {
                    settings = await ReadAssignedManualScanParam(route.HeadNo, cancellationToken);
                }
                catch (Exception exception)when (exception is InvalidDataException or IOException or InvalidOperationException)
                {
                    throw new InvalidDataException($"HEAD {route.HeadNo:00} / {ScanAssignments.GetSettingName(route.HeadNo)}\n{exception.Message}", exception);
                }

                var scriptName = BuildShapeScanScriptName(route.HeadNo, settings.ShapeName);
                var script = _scriptFile.Create($"{scriptName}.ascript");
                script.SetDeviceNo(route.HeadNo - 1);
                script.SetAxis(route.XAxis, route.YAxis);
                AppendManualSettingComment(script, settings);
                script.Start($"Manual H{route.HeadNo:00} {scriptName}");
                script.LaserOff();
                ApplyManualFigureScanSetup(script, settings);
                var position = ResolveManualScanPosition(route, options, settings, origins[route.HeadNo], useCurrentPosition);
                script.AddLine(position.ScriptComment);
                AppendShape(script, settings with { OffsetX = position.TargetX, OffsetY = position.TargetY });
                script.WaitMoveDone();
                script.LaserOff();
                script.End();
                var saved = script.Save(cancellationToken);
                programs.Add(new(route.HeadNo, route.AutomationNo, route.TaskNo, ScanAssignments.GetSettingName(route.HeadNo), settings, saved.FileName, saved.FilePath) { LaserAxis = route.XAxis, Position = position });
            }

            await EnsureManualShapeScanTasksReady(programs, cancellationToken);
            return programs;
        }

        private static IReadOnlyList<ST_MANUAL_SHAPE_SCAN_ROUTE> BuildManualShapeScanRoutes(IReadOnlyList<ST_SYSTEM_PARAMETER> options, IReadOnlyList<int> headNos)
        {
            if (headNos.Count == 0)
                throw new InvalidDataException("형상 가공할 Head를 먼저 선택해 주세요.");
            var enabledHeads = CScannerStatusSnapshotReader.BuildEnabledHeadNumbers(options);
            var tasks = new HashSet<(int Controller, int Task)>();
            var axes = new HashSet<(int Controller, string Axis)>();
            var routes = new List<ST_MANUAL_SHAPE_SCAN_ROUTE>(headNos.Count);
            foreach (var headNo in headNos.Distinct().Order())
            {
                if (!enabledHeads.Contains(headNo))
                    throw new InvalidDataException($"HEAD {headNo:00}은 사용 설정된 Head가 아닙니다.");
                var controller = ReadManualHeadAutomationNo(options, headNo);
                var task = ReadManualHeadAutomationTaskNo(options, headNo);
                if (!tasks.Add((controller, task)))
                    throw new InvalidDataException($"선택한 Head에 동일한 CONTROLLER {controller + 1} / Task {task}가 중복 지정되어 있습니다.");
                var definitions = CScannerStatusSnapshotReader.BuildAxisDefinitions(options, headNo);
                var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var role in new[]
                {
                    "X",
                    "Y"
                }

                )
                {
                    var axis = definitions.FirstOrDefault(definition => definition.AxisName.Equals(role, StringComparison.OrdinalIgnoreCase));
                    if (!axis.IsUsed || !string.IsNullOrWhiteSpace(axis.ConfigurationError) || !IsConfiguredScannerAxisName(axis.AxisReference))
                        throw new InvalidDataException($"HEAD {headNo:00} {role}축 설정을 확인해 주세요. {axis.ConfigurationError}");
                    if (axis.AutomationNo != controller || !axes.Add((controller, axis.AxisReference.ToUpperInvariant())))
                        throw new InvalidDataException($"HEAD {headNo:00} {role}축의 컨트롤러 또는 축 중복 설정을 확인해 주세요.");
                    names[role] = axis.AxisReference;
                }

                routes.Add(new(headNo, controller, task, names["X"], names["Y"]));
            }

            return routes;
        }

        private async Task EnsureManualShapeScanTasksReady(IReadOnlyList<ST_MANUAL_SHAPE_SCAN_PROGRAM> programs, CancellationToken cancellationToken)
        {
            foreach (var program in programs)
            {
                var response = await _manager.automation.ReadTaskStatus(program.TaskNo, program.AutomationNo, cancellationToken);
                var fields = response.Split(':');
                if (fields.Length < 4 || !fields[0].Equals("OK", StringComparison.OrdinalIgnoreCase) || !fields[1].Equals("TASK", StringComparison.OrdinalIgnoreCase) || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var task) || task != program.TaskNo || !(fields[3].Equals("Idle", StringComparison.OrdinalIgnoreCase) || fields[3].Equals("ProgramComplete", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"HEAD {program.HeadNo:00}의 Task {program.TaskNo}를 시작할 수 없습니다. {response}");
            }
        }

        private async Task ExecuteManualShapeScanPrograms(IReadOnlyList<ST_MANUAL_SHAPE_SCAN_PROGRAM> programs, CancellationToken cancellationToken)
        {
            var started = new List<ST_MANUAL_SHAPE_SCAN_PROGRAM>(programs.Count);
            try
            {
                // Every upload must succeed before the first Head is allowed to start.
                foreach (var program in programs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var response = await _manager.automation.UploadScript(program.FilePath, program.FileName, program.AutomationNo, cancellationToken);
                    EnsureAutomationResponse(response, $"HEAD {program.HeadNo:00} shape upload");
                }

                await EnsureManualShapeScanTasksReady(programs, cancellationToken);
                EnsureManualShapeScanAutoStopped();
                await PrepareManualShapeScanAttenuators(programs, cancellationToken);
                // Tasks may have changed while the ATT devices were moving.
                await EnsureManualShapeScanTasksReady(programs, cancellationToken);
                var responses = new List<string>(programs.Count);
                foreach (var program in programs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureManualShapeScanAutoStopped();
                    // Include an attempted RUN, because a lost response can still mean the controller started it.
                    started.Add(program);
                    var response = await _manager.automation.RunScript(program.FileName, program.TaskNo, program.AutomationNo, cancellationToken);
                    EnsureAutomationResponse(response, $"HEAD {program.HeadNo:00} shape run");
                    responses.Add(response);
                }

                using var monitors = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                async Task Monitor(ST_MANUAL_SHAPE_SCAN_PROGRAM program, string response)
                {
                    try
                    {
                        await WaitManualTaskCompleted(response, !_manager.automation.IsSimul(program.AutomationNo), program.AutomationNo, program.TaskNo, monitors.Token);
                    }
                    catch
                    {
                        monitors.Cancel();
                        throw;
                    }
                }

                await Task.WhenAll(programs.Select((program, index) => Monitor(program, responses[index])));
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                var failures = await StopManualShapeScanPrograms(started);
                if (failures.Count > 0)
                    throw new IOException($"{exception.Message}\n일부 Head의 정지를 확인하지 못했습니다.\n{string.Join("\n", failures)}", exception);
                throw;
            }
        }

        private void EnsureManualShapeScanAutoStopped()
        {
            if (IsAutoProcessRunning())
                throw new InvalidOperationException("자동 운전이 시작되어 형상 스캔을 차단했습니다. 자동 운전을 정지한 후 다시 시도해 주세요.");
        }

        private async Task PrepareManualShapeScanAttenuators(IReadOnlyList<ST_MANUAL_SHAPE_SCAN_PROGRAM> programs, CancellationToken cancellationToken)
        {
            // Resolve every Head first: a bad or missing table must not leave other Heads moving.
            var targets = await Task.Run(() =>
            {
                var table = new CPowerTableFile(_manager.ConfigRoot);
                return programs.Select(program =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new ST_ATTENUATOR_MOVE_TARGET(program.HeadNo, table.ResolveAttenuatorAngle(program.HeadNo, program.Settings.LaserPower, cancellationToken));
                }).ToArray();
            }, cancellationToken);
            var positioning = _manualShapeScanAttenuatorPositioning ??= new CAttenuatorPositioningService(_manager.Interface());
            await positioning.MoveAndWaitAsync(targets, cancellationToken);
        }

        private async Task<IReadOnlyList<string>> StopManualShapeScanPrograms(IReadOnlyList<ST_MANUAL_SHAPE_SCAN_PROGRAM> programs)
        {
            var failures = new List<string>();
            var pending = new List<ST_MANUAL_SHAPE_SCAN_PROGRAM>();
            foreach (var program in programs)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var failed = false;
                try
                {
                    var response = await _manager.automation.StopTask(program.TaskNo, program.AutomationNo, timeout.Token);
                    EnsureAutomationResponse(response, $"HEAD {program.HeadNo:00} shape stop");
                    await WaitManualTaskCompleted(response, false, program.AutomationNo, program.TaskNo, timeout.Token);
                }
                catch (Exception stopException)
                {
                    failed = true;
                    failures.Add($"HEAD {program.HeadNo:00}: {stopException.Message}");
                }

                if (!string.IsNullOrWhiteSpace(program.LaserAxis))
                {
                    using var offTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        var response = await _manager.automation.SetGalvoLaserOutput(program.LaserAxis, false, program.TaskNo, program.AutomationNo, offTimeout.Token);
                        EnsureAutomationResponse(response, $"HEAD {program.HeadNo:00} laser off");
                    }
                    catch (Exception exception)
                    {
                        failed = true;
                        failures.Add($"HEAD {program.HeadNo:00} LASER OFF: {exception.Message}");
                    }
                }

                if (failed)
                    pending.Add(program);
            }

            // Keep uncertain Tasks available for STOP retries even if Head selection changes or the run has failed.
            _manualShapeScanPendingStops = pending;
            return failures;
        }

        private sealed class CManualShapeScanRun
        {
            public CancellationTokenSource Cancellation { get; } = new();
            public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public IReadOnlyList<ST_MANUAL_SHAPE_SCAN_PROGRAM> Programs { get; set; } = [];
        }

        private sealed record ST_MANUAL_SHAPE_SCAN_ROUTE(int HeadNo, int AutomationNo, int TaskNo, string XAxis, string YAxis);
        private sealed record ST_MANUAL_SHAPE_SCAN_PROGRAM(int HeadNo, int AutomationNo, int TaskNo, string SettingName, ST_MANUAL_SCAN_PARAM Settings, string FileName, string FilePath)
        {
            public string LaserAxis { get; init; } = "";
            public ST_MANUAL_SCAN_POSITION? Position { get; init; }
        }

        private bool IsManualLaserActionActive => _activeManualLaserAction is not null || _manualLaserOffInProgress || _manualLaserOffUnconfirmed || _manualLaserOffPendingTargets is { Count: > 0 };

        private async Task RunSelectedManualLaserActions(bool useCurrentPosition)
        {
            if (IsManualShapeScanActive)
            {
                CRecipeConfirmDialog.ShowWarning("레이저 동작", "수동 가공이 진행 중이거나 정지가 확인되지 않은 Head가 있습니다. LASER OFF로 정지한 후 다시 시도해 주세요.");
                return;
            }

            if (TryBlockManualAutomationCommand("LASER_ON"))
                return;
            var run = new CManualShapeScanRun();
            _activeManualLaserAction = run;
            try
            {
                var programs = await PrepareManualLaserActionPrograms(run.Cancellation.Token, useCurrentPosition);
                run.Programs = programs;
                if (!CRecipeConfirmDialog.ShowConfirmation("레이저 동작", BuildManualScanConfirmation(programs, shapeScan: false)))
                    return;
                run.Cancellation.Token.ThrowIfCancellationRequested();
                EnsureManualShapeScanAutoStopped();
                _lastCommand = "LASER_ON";
                _lastResult = $"Preparing ATT / {programs.Count} Heads";
                _setStatusMessage($"선택한 {programs.Count}개 Head의 .scan 설정으로 레이저 동작을 준비합니다.");
                RefreshCommandStateRows();
                _refreshShellStatus();
                await ExecuteManualShapeScanPrograms(programs, run.Cancellation.Token);
                _laserState = "OFF";
                _lastResult = $"Completed {programs.Count} Heads";
                _setStatusMessage($"선택한 {programs.Count}개 Head의 레이저 동작이 완료되었습니다.");
            }
            catch (OperationCanceledException)
            {
                _lastResult = "Stopped";
                _setStatusMessage("선택한 Head의 레이저 동작을 정지했습니다.");
            }
            catch (Exception exception)when (exception is InvalidDataException or InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                _lastResult = $"ERROR: {exception.Message}";
                _setStatusMessage($"레이저 동작을 완료하지 못했습니다. {exception.Message}");
                CRecipeConfirmDialog.ShowWarning("레이저 동작 실패", exception.Message);
            }
            finally
            {
                _activeManualLaserAction = null;
                run.Completed.TrySetResult();
                run.Cancellation.Dispose();
                RefreshCommandStateRows();
                _refreshShellStatus();
            }
        }

        private async Task<IReadOnlyList<ST_MANUAL_SHAPE_SCAN_PROGRAM>> PrepareManualLaserActionPrograms(CancellationToken cancellationToken, bool useCurrentPosition)
        {
            var options = _manager.Setting().LoadSection(EN_SETTING_TAB.Option, cancellationToken);
            var routes = BuildManualShapeScanRoutes(options, SelectedManualHeadNos);
            var origins = CaptureManualScanOrigins(routes, options, useCurrentPosition);
            var programs = new List<ST_MANUAL_SHAPE_SCAN_PROGRAM>(routes.Count);
            foreach (var route in routes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_manager.automation.IsSimul(route.AutomationNo) && !_manager.automation.IsConnect(route.AutomationNo))
                    throw new InvalidOperationException($"CONTROLLER {route.AutomationNo + 1}이 연결되어 있지 않습니다.");
                var settings = await ReadAssignedManualScanParamCore(route.HeadNo, cancellationToken, validateShape: false);
                var script = _scriptFile.Create($"MANUAL_H{route.HeadNo:00}_LASER_ON.ascript");
                script.SetDeviceNo(route.HeadNo - 1);
                script.SetAxis(route.XAxis, route.YAxis);
                AppendManualSettingComment(script, settings);
                script.AddLine($"// Laser Action Mode={settings.LaserOnMode}, OnTime={settings.Time.ToString("R", CultureInfo.InvariantCulture)} msec, ShotCount={settings.Count}");
                script.AddLine($"// Laser Action RepeatCount={settings.RepeatCount}, RepeatDelay={settings.RepeatDelayTime.ToString("R", CultureInfo.InvariantCulture)} msec");
                script.Start($"Manual H{route.HeadNo:00} LASER ON");
                script.LaserOff();
                ApplySavedManualLaserActionSetup(script, settings);
                var position = ResolveManualScanPosition(route, options, settings, origins[route.HeadNo], useCurrentPosition);
                script.AddLine(position.ScriptComment);
                script.Jump(position.TargetX, position.TargetY);
                script.WaitMoveDone();
                AppendSavedManualLaserOnScript(script, settings);
                script.End();
                var saved = script.Save(cancellationToken);
                programs.Add(new(route.HeadNo, route.AutomationNo, route.TaskNo, ScanAssignments.GetSettingName(route.HeadNo), settings, saved.FileName, saved.FilePath) { LaserAxis = route.XAxis, Position = position });
            }

            await EnsureManualShapeScanTasksReady(programs, cancellationToken);
            return programs;
        }

        private static void ValidateSavedManualLaserActionSettings(ST_MANUAL_SCAN_PARAM settings)
        {
            if (!double.IsFinite(settings.OffsetX) || !double.IsFinite(settings.OffsetY))
                throw new InvalidDataException("Offset X/Y는 유한한 mm 값이어야 합니다.");
            if (!double.IsFinite(settings.JumpSpeed) || settings.JumpSpeed <= 0 || !double.IsFinite(settings.JumpSpeed * 1000.0))
                throw new InvalidDataException("Jump Speed는 0보다 큰 유한한 이동 속도여야 합니다.");
            if (settings.LaserOnMode is not ("TIME" or "COUNT"))
                throw new InvalidDataException("Laser Action Mode는 TIME 또는 COUNT로 설정해 주세요.");
            if (!double.IsFinite(settings.LaserPower) || settings.LaserPower < 0)
                throw new InvalidDataException("Laser Power는 0 이상의 유한한 W 값이어야 합니다.");
            if (!double.IsFinite(settings.LaserFrequency) || settings.LaserFrequency <= 0)
                throw new InvalidDataException("Laser Frequency는 0보다 큰 유한한 값이어야 합니다.");
            if (!double.IsFinite(settings.LaserOnDelay) || settings.LaserOnDelay < 0 || !double.IsFinite(settings.LaserOffDelay) || settings.LaserOffDelay < 0)
                throw new InvalidDataException("Laser On/Off Delay는 0 이상의 유한한 msec 값이어야 합니다.");
            if (!double.IsFinite(settings.Time) || settings.Time <= 0 || settings.Count < 1)
                throw new InvalidDataException("On Time은 0보다 큰 msec 값, Shot Count는 1 이상의 정수로 설정해 주세요.");
            if (settings.RepeatCount < 1 || !double.IsFinite(settings.RepeatDelayTime) || settings.RepeatDelayTime < 0)
                throw new InvalidDataException("Repeat Count는 1 이상의 정수, Repeat Delay Time은 0 이상의 유한한 msec 값으로 설정해 주세요.");
        }

        private static void ApplySavedManualLaserActionSetup(CAutomation1ScriptBase script, ST_MANUAL_SCAN_PARAM settings)
        {
            script.DefaultSetting(resetPso: false);
            script.SetMoveBlending(false);
            script.SetJumpSpeed(settings.JumpSpeed);
            script.SetLaserDelay(0, 0);
            script.SetFrequency(settings.LaserFrequency);
            // The optical W target is applied with the ATT before any of these scripts start.
            script.SetLaserPowerNoDelay(100);
            script.SetLaserMode(0);
        }

        private static void AppendSavedManualLaserOnScript(CAutomation1ScriptBase script, ST_MANUAL_SCAN_PARAM settings)
        {
            ValidateSavedManualLaserActionSettings(settings);
            var onTime = settings.LaserOnMode == "COUNT" ? (settings.Count + 1.0) / settings.LaserFrequency : settings.Time;
            if (!double.IsFinite(onTime) || onTime <= 0)
                throw new InvalidDataException("레이저 동작 시간을 계산할 수 없습니다.");
            void Cycle()
            {
                script.SetMoveDelay(settings.LaserOnDelay, applyMinimumDelay: false);
                script.LaserOn();
                script.SetMoveDelay(onTime, applyMinimumDelay: false);
                script.LaserOff();
                script.SetMoveDelay(settings.LaserOffDelay, applyMinimumDelay: false);
            }

            if (settings.RepeatCount > 1)
            {
                script.AddLine($"repeat {settings.RepeatCount - 1}");
                Cycle();
                script.SetMoveDelay(settings.RepeatDelayTime, applyMinimumDelay: false);
                script.AddLine("end");
            }

            Cycle();
        }

        private async Task StopAllManualLasers()
        {
            if (_manualLaserOffInProgress)
                return;
            _manualLaserOffInProgress = true;
            _manualLaserOffUnconfirmed = true;
            var failures = new List<string>();
            var laser = _activeManualLaserAction;
            var shape = _activeManualShapeScan;
            var originalPrograms = (laser?.Programs ?? []).Concat(shape?.Programs ?? []).Concat(_manualShapeScanPendingStops ?? []).ToArray();
            try
            {
                _lastCommand = "LASER_OFF";
                _lastResult = "Stopping all Heads";
                laser?.Cancellation.Cancel();
                shape?.Cancellation.Cancel();
                CancelStatusPolling();
                if (IsAutoProcessRunning())
                {
                    try
                    {
                        _manager.Station().Stop();
                    }
                    catch (Exception exception)
                    {
                        failures.Add($"자동 운전 정지 요청 실패: {exception.Message}");
                    }
                }

                await Task.WhenAll(new[] { laser?.Completed.Task, shape?.Completed.Task }.OfType<Task>());
                var targets = new List<ST_MANUAL_LASER_OFF_TARGET>();
                try
                {
                    targets.AddRange(BuildManualLaserOffTargets(_manager.Setting().LoadSection(EN_SETTING_TAB.Option)));
                }
                catch (Exception exception)
                {
                    failures.Add($"Head 설정 확인 실패: {exception.Message}");
                }

                targets.AddRange(originalPrograms.Where(program => !string.IsNullOrWhiteSpace(program.LaserAxis)).Select(program => new ST_MANUAL_LASER_OFF_TARGET(program.HeadNo, program.AutomationNo, program.TaskNo, program.LaserAxis)));
                targets.AddRange((_manualLaserOffPendingTargets ?? []).Where(pending => string.IsNullOrWhiteSpace(pending.ConfigurationError) || !targets.Any(target => target.HeadNo == pending.HeadNo && string.IsNullOrWhiteSpace(target.ConfigurationError))));
                failures.AddRange(await ForceManualLasersOff(targets.Distinct().ToArray(), CancellationToken.None));
                if (_manualShapeScanAttenuatorPositioning?.HasPendingStops == true)
                {
                    try
                    {
                        await _manualShapeScanAttenuatorPositioning.StopPendingAsync();
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception.Message);
                    }
                }

                _manualShapeScanPendingStops = (_manualShapeScanPendingStops ?? []).Where(program => (_manualLaserOffPendingTargets ?? []).Any(target => target.AutomationNo == program.AutomationNo && target.TaskNo == program.TaskNo)).ToArray();
                if (failures.Count > 0)
                {
                    _lastResult = "ERROR: LASER OFF";
                    _setStatusMessage("일부 Head의 정지 또는 LASER OFF를 확인하지 못했습니다.");
                    CRecipeConfirmDialog.ShowWarning("LASER OFF 실패", string.Join("\n", failures));
                }
                else
                {
                    _manualLaserOffUnconfirmed = false;
                    _laserState = "OFF";
                    _centerState = "OFF";
                    _lastResult = "All Heads OFF";
                    _setStatusMessage("전체 Head의 스크립트를 정지하고 LASER OFF 명령을 완료했습니다.");
                }
            }
            finally
            {
                _manualLaserOffInProgress = false;
                RefreshCommandStateRows();
                _refreshShellStatus();
            }
        }

        private static IReadOnlyList<ST_MANUAL_LASER_OFF_TARGET> BuildManualLaserOffTargets(IReadOnlyList<ST_SYSTEM_PARAMETER> options)
        {
            var enabled = CScannerStatusSnapshotReader.BuildEnabledHeadNumbers(options);
            var targets = new List<ST_MANUAL_LASER_OFF_TARGET>();
            foreach (var head in Enumerable.Range(1, 8))
            {
                var axis = ReadHeadSettingText(options, "", head, "X_AXIS", "GX_AXIS");
                if (string.IsNullOrWhiteSpace(axis) && !enabled.Contains(head))
                    continue;
                var controller = -1;
                var task = 0;
                try
                {
                    controller = ReadManualHeadAutomationNo(options, head);
                    task = ReadManualHeadAutomationTaskNo(options, head);
                    if (!IsConfiguredScannerAxisName(axis))
                        throw new InvalidDataException($"HEAD {head:00} X축 설정을 확인해 주세요.");
                    targets.Add(new(head, controller, task, axis));
                }
                catch (Exception exception)when (exception is InvalidDataException or InvalidOperationException)
                {
                    targets.Add(new(head, controller, task, axis) { ConfigurationError = exception.Message });
                }
            }

            return targets;
        }

        private async Task<IReadOnlyList<string>> ForceManualLasersOff(IReadOnlyList<ST_MANUAL_LASER_OFF_TARGET> targets, CancellationToken cancellationToken)
        {
            var failures = new List<string>();
            var pending = new HashSet<ST_MANUAL_LASER_OFF_TARGET>();
            var available = new List<ST_MANUAL_LASER_OFF_TARGET>();
            var stoppable = new List<ST_MANUAL_LASER_OFF_TARGET>();
            foreach (var target in targets)
            {
                var validAxis = string.IsNullOrWhiteSpace(target.ConfigurationError) && IsConfiguredScannerAxisName(target.LaserAxis);
                if (!validAxis || target.AutomationNo < 0 || target.TaskNo < 1)
                {
                    pending.Add(target);
                    failures.Add($"HEAD {target.HeadNo:00}: {target.ConfigurationError} LASER OFF 축/Task 설정을 확인해 주세요.");
                }

                if (target.AutomationNo < 0 || target.TaskNo < 1)
                    continue;
                if (!_manager.automation.IsSimul(target.AutomationNo) && !_manager.automation.IsConnect(target.AutomationNo))
                {
                    pending.Add(target);
                    failures.Add($"HEAD {target.HeadNo:00}: CONTROLLER {target.AutomationNo + 1} 미연결로 LASER OFF를 확인할 수 없습니다.");
                }
                else
                {
                    stoppable.Add(target);
                    if (validAxis)
                        available.Add(target);
                }
            }

            // Stop every producer of laser commands before issuing the final OFF commands.
            foreach (var group in stoppable.GroupBy(target => (target.AutomationNo, target.TaskNo)))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var response = await _manager.automation.StopTask(group.Key.TaskNo, group.Key.AutomationNo, timeout.Token);
                    EnsureAutomationResponse(response, "LASER OFF task stop");
                    await WaitManualTaskCompleted(response, false, group.Key.AutomationNo, group.Key.TaskNo, timeout.Token);
                }
                catch (Exception exception)
                {
                    foreach (var target in group)
                        pending.Add(target);
                    failures.Add($"CONTROLLER {group.Key.AutomationNo + 1} / Task {group.Key.TaskNo} 정지 실패: {exception.Message}");
                }
            }

            foreach (var group in available.GroupBy(target => (target.AutomationNo, Axis: target.LaserAxis.ToUpperInvariant())))
            {
                var target = group.First();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var response = await _manager.automation.SetGalvoLaserOutput(target.LaserAxis, false, target.TaskNo, target.AutomationNo, timeout.Token);
                    EnsureAutomationResponse(response, $"HEAD {target.HeadNo:00} LASER OFF");
                }
                catch (Exception exception)
                {
                    foreach (var failed in group)
                        pending.Add(failed);
                    failures.Add($"HEAD {target.HeadNo:00} LASER OFF 실패: {exception.Message}");
                }
            }

            _manualLaserOffPendingTargets = pending.ToArray();
            return failures;
        }

        private sealed record ST_MANUAL_LASER_OFF_TARGET(int HeadNo, int AutomationNo, int TaskNo, string LaserAxis)
        {
            public string ConfigurationError { get; init; } = "";
        }

        private async Task ShowPreviousSettingPage()
        {
            if (!CanShowPreviousSettingPage())
            {
                return;
            }

            _settingFilesPageIndex--;
            NotifySettingFilesPageCommands();
            await _refreshCurrentScreen();
        }

        private async Task ShowNextSettingPage()
        {
            if (!CanShowNextSettingPage())
            {
                return;
            }

            _settingFilesPageIndex++;
            NotifySettingFilesPageCommands();
            await _refreshCurrentScreen();
        }

        private bool CanShowPreviousSettingPage()
        {
            return _settingFilesPageIndex > 0;
        }

        private bool CanShowNextSettingPage()
        {
            return _settingFilesPageIndex + 1 < _settingFilesPageCount;
        }

        private void NotifySettingFilesPageCommands()
        {
            PreviousSettingPageCommand.NotifyCanExecuteChanged();
            NextSettingPageCommand.NotifyCanExecuteChanged();
        }

        private async Task SelectSetting(object? parameter)
        {
            var settingName = GetManualSettingNameFromParameter(parameter);
            if (string.IsNullOrWhiteSpace(settingName))
            {
                return;
            }

            _selectedSettingNameSetter(settingName);
            _setStatusMessage($"Manual setting {settingName} selected.");
            _refreshShellStatus();
            await _refreshCurrentScreen();
        }

        private async Task Create()
        {
            var settingNames = await _scanFile.List();
            string ValidateNewSettingNameCallback(string value)
            {
                return ValidateManualSettingName(NormalizeManualSettingNameInput(value), settingNames);
            }

            var newSettingName = ShowManualSettingNameDialog("Manual Setting 생성", "새 Manual Setting 이름을 입력하세요.", "", ValidateNewSettingNameCallback);
            if (newSettingName is null)
            {
                _setStatusMessage("Manual setting create canceled.");
                return;
            }

            var validationMessage = ValidateManualSettingName(newSettingName, settingNames);
            if (!string.IsNullOrWhiteSpace(validationMessage))
            {
                _setStatusMessage(validationMessage);
                return;
            }

            if (!TryReadManualParamFromScreen(out var settings, out var errorMessage))
            {
                _setStatusMessage(errorMessage);
                ShowManualWarning(errorMessage);
                return;
            }

            if (!await TrySaveSetting(newSettingName, settings))
            {
                return;
            }

            _selectedSettingNameSetter(newSettingName);
            _setStatusMessage($"Manual setting {newSettingName} created and CSV verified.");
            _refreshShellStatus();
            await _refreshCurrentScreen();
        }

        private async Task Save()
        {
            if (string.IsNullOrWhiteSpace(LoadedSettingName))
            {
                _setStatusMessage("Manual setting save skipped. No manual setting is selected.");
                return;
            }

            if (!CRecipeConfirmDialog.ShowConfirmation("Manual Setting 저장", $"{LoadedSettingName} 설정을 저장하시겠습니까?"))
            {
                return;
            }

            if (!TryReadManualParamFromScreen(out var settings, out var errorMessage))
            {
                _setStatusMessage(errorMessage);
                ShowManualWarning(errorMessage);
                return;
            }

            if (!await TrySaveSetting(LoadedSettingName, settings))
            {
                return;
            }

            _selectedSettingNameSetter(LoadedSettingName);
            _setStatusMessage($"Manual setting {LoadedSettingName} saved and CSV verified.");
            _refreshShellStatus();
            await _refreshCurrentScreen();
            CRecipeConfirmDialog.ShowCompletion("Manual Setting 저장 완료", $"{LoadedSettingName} 설정을 저장하였습니다.");
        }

        private async Task Rename()
        {
            var oldSettingName = GetManualSettingNameFromParameter(LoadedSettingName);
            if (string.IsNullOrWhiteSpace(oldSettingName))
            {
                _setStatusMessage("Manual setting rename skipped. No manual setting is selected.");
                return;
            }

            var settingNames = await _scanFile.List();
            string ValidateRenamedSettingNameCallback(string value)
            {
                return ValidateManualSettingName(NormalizeManualSettingNameInput(value), settingNames, oldSettingName);
            }

            var newSettingName = ShowManualSettingNameDialog("Manual Setting 이름 변경", "변경할 Manual Setting 이름을 입력하세요.", Path.GetFileNameWithoutExtension(oldSettingName), ValidateRenamedSettingNameCallback);
            if (newSettingName is null)
            {
                _setStatusMessage("Manual setting rename canceled.");
                return;
            }

            if (newSettingName.Equals(oldSettingName, StringComparison.OrdinalIgnoreCase))
            {
                _setStatusMessage("Manual setting rename skipped. Name was not changed.");
                return;
            }

            var validationMessage = ValidateManualSettingName(newSettingName, settingNames, oldSettingName);
            if (!string.IsNullOrWhiteSpace(validationMessage))
            {
                _setStatusMessage(validationMessage);
                return;
            }

            if (!TryReadManualParamFromScreen(out var settings, out var errorMessage))
            {
                _setStatusMessage(errorMessage);
                ShowManualWarning(errorMessage);
                return;
            }

            if (!await TrySaveSetting(oldSettingName, settings))
            {
                return;
            }

            try
            {
                await _scanFile.Rename(oldSettingName, newSettingName);
                ScanAssignments.RenameSetting(oldSettingName, newSettingName);
            }
            catch (IOException exception)
            {
                _setStatusMessage($"Manual setting rename blocked. {exception.Message}");
                return;
            }

            _selectedSettingNameSetter(newSettingName);
            _setStatusMessage($"Manual setting {oldSettingName} renamed to {newSettingName}.");
            _refreshShellStatus();
            await _refreshCurrentScreen();
        }

        private async Task<bool> TrySaveSetting(string settingName, ST_MANUAL_SCAN_PARAM settings)
        {
            try
            {
                await _scanFile.Save(settingName, settings);
                return true;
            }
            catch (InvalidDataException exception)
            {
                _setStatusMessage(exception.Message);
                ShowManualWarning(exception.Message);
                return false;
            }
            catch (IOException exception)
            {
                _setStatusMessage($"Manual setting save blocked. {exception.Message}");
                return false;
            }
        }

        private async Task Delete()
        {
            var settingName = GetManualSettingNameFromParameter(LoadedSettingName);
            if (string.IsNullOrWhiteSpace(settingName))
            {
                _setStatusMessage("Manual setting delete skipped. No manual setting is selected.");
                return;
            }

            if (!ConfirmManualSettingDelete(settingName))
            {
                _setStatusMessage($"Manual setting {settingName} delete canceled.");
                return;
            }

            await _scanFile.Delete(settingName);
            var remainingSettings = await _scanFile.List();
            _selectedSettingNameSetter(remainingSettings.FirstOrDefault() ?? "CIRCLE_TEST.scan");
            _setStatusMessage($"Manual setting {settingName} deleted.");
            _refreshShellStatus();
            await _refreshCurrentScreen();
            CRecipeConfirmDialog.ShowCompletion("Manual Setting 삭제 완료", $"{settingName} 설정을 삭제하였습니다.");
        }

        private Task SelectShape(object? parameter)
        {
            var shapeName = NormalizeShapeName(parameter?.ToString() ?? "");
            ShapeName = shapeName;
            _lastCommand = "SELECT_SHAPE";
            _lastResult = shapeName;
            _setStatusMessage($"Manual shape selected: {shapeName}.");
            return Task.CompletedTask;
        }

        private async Task ShapeStart(bool useCurrentPosition)
        {
            await RunSelectedManualShapeScans(useCurrentPosition);
        }

        private async Task ShapeStop()
        {
            await StopSelectedManualShapeScans();
        }

        private async Task LaserOn(bool useCurrentPosition)
        {
            await RunSelectedManualLaserActions(useCurrentPosition);
        }

        private void AppendLaserOnScript(CAutomation1ScriptBase script, ST_MANUAL_SCAN_PARAM settings)
        {
            if (!int.TryParse(LaserRepeatCount?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var repeatCount) || repeatCount < 1)
            {
                throw new InvalidDataException("Repeat Count는 1 이상의 정수로 입력해 주세요.");
            }

            var repeatDelayMsec = ReadRequiredDouble(LaserRepeatDelayTimeMsec, "Repeat Delay Time");
            if (!double.IsFinite(repeatDelayMsec) || repeatDelayMsec < 0.0)
            {
                throw new InvalidDataException("Repeat Delay Time은 0 이상의 유한한 숫자로 입력해 주세요. (msec)");
            }

            var laserActionSettings = ReadLaserActionSettings(settings);
            var onTimeMsec = ReadLaserMoveDelaySeconds(laserActionSettings);
            void AppendLaserCycle()
            {
                script.SetMoveDelay(laserActionSettings.LaserOnDelay, applyMinimumDelay: false);
                script.LaserOn();
                script.SetMoveDelay(onTimeMsec);
                script.LaserOff();
                script.SetMoveDelay(laserActionSettings.LaserOffDelay, applyMinimumDelay: false);
            }

            if (repeatCount > 1)
            {
                // Keep script size bounded; emit the last block separately so it has no trailing delay.
                script.AddLine($"repeat {repeatCount - 1}");
                AppendLaserCycle();
                if (repeatDelayMsec > 0.0)
                {
                    script.SetMoveDelay(repeatDelayMsec, applyMinimumDelay: false);
                }

                script.AddLine("end");
            }

            AppendLaserCycle();
        }

        private async Task LaserOff()
        {
            await StopAllManualLasers();
        }

        private async Task CenterOn()
        {
            if (!CRecipeConfirmDialog.ShowConfirmation("레이저 동작", "중앙 위치로 이동한 후 레이저를 동작시키시겠습니까?"))
            {
                return;
            }

            void AppendCenterOnScript(global::Drilling.Common.Station.CAutomation1ScriptBase script, global::Drilling.Common.Managers.ST_MANUAL_SCAN_PARAM settings)
            {
                var laserActionSettings = ReadLaserActionSettings(settings);
                var moveDelaySeconds = ReadLaserMoveDelaySeconds(laserActionSettings);
                script.Jump(0.0, 0.0);
                script.WaitMoveDone();
                script.SetMoveDelay(laserActionSettings.LaserOnDelay, applyMinimumDelay: false);
                script.LaserOn();
                script.SetMoveDelay(moveDelaySeconds);
                script.LaserOff();
                script.SetMoveDelay(laserActionSettings.LaserOffDelay, applyMinimumDelay: false);
            }

            await RunManualScript("CENTER_ON", AppendCenterOnScript, ApplyLaserActionScriptSetup);
            if (!_lastResult.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                _laserState = "OFF";
                _centerState = "OFF";
            }
        }

        private async Task RunManualScript(
            string commandName, 
            Action<CAutomation1ScriptBase, ST_MANUAL_SCAN_PARAM> buildScript, 
            Action<CAutomation1ScriptBase, ST_MANUAL_SCAN_PARAM>? setupScript = null, 
            string? scriptFileName = null, 
            CancellationToken cancellationToken = default
        )
        {
            if (TryBlockManualAutomationCommand(commandName))
            {
                return;
            }

            CancelStatusPolling();
            _lastCommand = commandName;
            _lastResult = "Building";
            try
            {
                var headNo = ReadEnabledManualScannerHeadNo(cancellationToken);
                var options = _manager.Setting().LoadSection(EN_SETTING_TAB.Option, cancellationToken);
                var automationNo = ReadManualHeadAutomationNo(options, headNo);
                if (!_manager.automation.IsSimul(automationNo) && !_manager.automation.IsConnect(automationNo))
                    throw new InvalidOperationException($"CONTROLLER {automationNo + 1}이 연결되어 있지 않습니다.");
                var settings = commandName == "LASER_OFF" ? CreateFallbackManualParam() : await ReadAssignedManualScanParam(headNo, cancellationToken);
                var fileName = string.IsNullOrWhiteSpace(scriptFileName) ? $"MANUAL_H{headNo:00}_{NormalizeScriptName(commandName)}.ascript" : $"{NormalizeScriptName(scriptFileName)}.ascript";
                var script = _scriptFile.Create(fileName);
                script.SetDeviceNo(headNo - 1);
                SetManualScriptAxes(script, headNo, cancellationToken);
                AppendManualSettingComment(script, settings);
                script.Start($"Manual H{headNo:00} {commandName}");
                (setupScript ?? ApplyManualScriptSetup)(script, settings);
                buildScript(script, settings);
                script.End();
                var savedScript = script.Save(cancellationToken);
                var uploadResponse = await _manager.automation.UploadScript(savedScript.FilePath, savedScript.FileName, number: automationNo, cancellationToken: cancellationToken);
                EnsureAutomationResponse(uploadResponse, $"{commandName} upload");
                _lastManualScriptAutomationNo = automationNo;
                var runResponse = await _manager.automation.RunScript(savedScript.FileName, ManualTaskIndex, number: automationNo, cancellationToken: cancellationToken);
                EnsureAutomationResponse(runResponse, $"{commandName} run");
                _lastResult = $"Running {savedScript.FileName}";
                _setStatusMessage($"{commandName} script uploaded and started: {savedScript.FileName}");
                var statusCancellation = CreateStatusPollingCancellation(cancellationToken);
                var statusToken = statusCancellation.Token;
                try
                {
                    await WaitManualTaskCompleted(runResponse, requireExecutionStart: !_manager.automation.IsSimul(automationNo), automationNo: automationNo, cancellationToken: statusToken);
                    statusToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    ReleaseStatusPollingCancellation(statusCancellation);
                }

                _lastResult = $"Completed {savedScript.FileName}";
                _setStatusMessage($"{commandName} completed: {savedScript.FileName}");
                RefreshCommandStateRows();
                _refreshShellStatus();
            }
            catch (OperationCanceledException)
            {
            // 화면을 벗어나면 완료 상태 조회와 후속 팝업만 중단한다.
            // 이미 Automation1에 전달된 실제 동작은 별도의 정지 명령 없이 계속된다.
            }
            catch (Exception exception)when (exception is InvalidDataException or InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                _lastResult = $"ERROR: {exception.Message}";
                _setStatusMessage($"{commandName} failed. {exception.Message}");
                RefreshCommandStateRows();
                _refreshShellStatus();
                if (exception is InvalidDataException)
                {
                    ShowManualWarning(exception.Message);
                }
                else
                {
                    CRecipeConfirmDialog.ShowWarning("수동 동작 실패", $"동작을 완료하지 못했습니다.\n{exception.Message}");
                }
            }
        }

        private void SetManualScriptAxes(CAutomation1ScriptBase script, int headNo, CancellationToken cancellationToken)
        {
            var axes = ReadManualScriptAxes(headNo, cancellationToken);
            script.SetAxis(axes.XAxis, axes.YAxis);
        }

        private ST_MANUAL_SCRIPT_AXES ReadManualScriptAxes(int headNo, CancellationToken cancellationToken)
        {
            var optionSettings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option, cancellationToken);
            var definitions = CScannerStatusSnapshotReader.BuildAxisDefinitions(optionSettings, headNo);
            var xAxis = "";
            var yAxis = "";
            foreach (var definition in definitions)
            {
                if (definition.AxisName.Equals("X", StringComparison.OrdinalIgnoreCase))
                {
                    xAxis = definition.AxisReference;
                }
                else if (definition.AxisName.Equals("Y", StringComparison.OrdinalIgnoreCase))
                {
                    yAxis = definition.AxisReference;
                }
            }

            if (string.IsNullOrWhiteSpace(xAxis) || string.IsNullOrWhiteSpace(yAxis))
            {
                throw new InvalidOperationException($"H{headNo:00} scanner axis settings are not configured.");
            }

            return new ST_MANUAL_SCRIPT_AXES(xAxis, yAxis);
        }

        private async Task StopManualTask(string commandName, CancellationToken cancellationToken = default)
        {
            if (TryBlockManualAutomationCommand(commandName))
            {
                return;
            }

            CancelStatusPolling();
            _lastCommand = commandName;
            _lastResult = "Stopping";
            var automationNo = _lastManualScriptAutomationNo ?? 0;
            if (!_manager.automation.IsSimul(automationNo) && !_manager.automation.IsConnect(automationNo))
            {
                _lastResult = "ERROR: OFFLINE";
                _setStatusMessage($"{commandName} failed. Automation1 is offline.");
                RefreshCommandStateRows();
                _refreshShellStatus();
                CRecipeConfirmDialog.ShowWarning("수동 동작 실패", "Automation 장비가 연결되어 있지 않아 정지 명령을 실행할 수 없습니다.");
                return;
            }

            try
            {
                var response = await _manager.automation.StopTask(ManualTaskIndex, number: automationNo, cancellationToken: cancellationToken);
                EnsureAutomationResponse(response, $"{commandName} stop");
                var statusCancellation = CreateStatusPollingCancellation(cancellationToken);
                var statusToken = statusCancellation.Token;
                try
                {
                    await WaitManualTaskCompleted(response, requireExecutionStart: false, automationNo: automationNo, cancellationToken: statusToken);
                    statusToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    ReleaseStatusPollingCancellation(statusCancellation);
                }

                _laserState = "OFF";
                _centerState = "OFF";
                _lastResult = "Stopped";
                _setStatusMessage($"{commandName} command sent.");
                RefreshCommandStateRows();
                _refreshShellStatus();
            }
            catch (OperationCanceledException)
            {
            // 메뉴 이탈로 상태 확인이 취소된 경우 완료/실패 팝업을 표시하지 않는다.
            }
            catch (Exception exception)when (exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException)
            {
                _lastResult = $"ERROR: {exception.Message}";
                _setStatusMessage($"{commandName} failed. {exception.Message}");
                RefreshCommandStateRows();
                _refreshShellStatus();
                CRecipeConfirmDialog.ShowWarning("수동 동작 실패", $"정지 동작을 완료하지 못했습니다.\n{exception.Message}");
            }
        }

        private CancellationTokenSource CreateStatusPollingCancellation(CancellationToken cancellationToken)
        {
            lock (_menuLifetimeSync)
            {
                _statusPollingCancellation?.Cancel();
                _statusPollingCancellation?.Dispose();
                _statusPollingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _menuLifetimeCancellation.Token);
                return _statusPollingCancellation;
            }
        }

        private void CancelStatusPolling()
        {
            lock (_menuLifetimeSync)
            {
                _statusPollingCancellation?.Cancel();
                _statusPollingCancellation?.Dispose();
                _statusPollingCancellation = null;
            }
        }

        private void ReleaseStatusPollingCancellation(CancellationTokenSource cancellation)
        {
            lock (_menuLifetimeSync)
            {
                if (!ReferenceEquals(_statusPollingCancellation, cancellation))
                {
                    return;
                }

                _statusPollingCancellation = null;
                cancellation.Dispose();
            }
        }

        private async Task WaitManualTaskCompleted(string initialResponse, bool requireExecutionStart, int automationNo = 0, int taskNo = ManualTaskIndex, CancellationToken cancellationToken = default)
        {
            var response = initialResponse;
            var executionStarted = !requireExecutionStart;
            while (true)
            {
                var fields = response.Split(':');
                if (fields.Length < 4 || !fields[0].Equals("OK", StringComparison.OrdinalIgnoreCase) || !fields[1].Equals("TASK", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Automation1 task status response is invalid: {response}");
                }

                var state = fields[3].Trim();
                if (state.Equals("ProgramComplete", StringComparison.OrdinalIgnoreCase) || executionStarted && state.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (state.Equals("Error", StringComparison.OrdinalIgnoreCase) || state.Equals("Unavailable", StringComparison.OrdinalIgnoreCase) || state.Equals("Inactive", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Automation1 manual task failed. State={state}, Response={response}");
                }

                if (!state.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                {
                    executionStarted = true;
                }

                await Task.Delay(100, cancellationToken);
                response = await _manager.automation.ReadTaskStatus(taskNo, automationNo, cancellationToken);
            }
        }

        private void RefreshCommandStateRows()
        {
            bool IsSelectedHeadCard(global::Drilling.UI.Menu.Menus.ST_MANUAL_HEAD_CARD head)
            {
                return head.HeadNo == _selectedHeadNoProvider();
            }

            var selectedHead = HeadCards.FirstOrDefault(IsSelectedHeadCard);
            bool IsFallbackSelectedHeadCard(global::Drilling.UI.Menu.Menus.ST_MANUAL_HEAD_CARD head)
            {
                return head.IsSelected;
            }

            if (selectedHead is null)
            {
                var optionSettings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option, CancellationToken.None);
                var selectedHeadNo = NormalizeManualScannerHeadNo(optionSettings, Math.Clamp(_selectedHeadNoProvider(), 1, 8));
                var scannerAxes = ReadManualScannerAxesSnapshot(optionSettings, selectedHeadNo);
                selectedHead = BuildHeadCards(selectedHeadNo, optionSettings, scannerAxes).FirstOrDefault(IsFallbackSelectedHeadCard);
            }

            selectedHead ??= new ST_MANUAL_HEAD_CARD(Math.Clamp(_selectedHeadNoProvider(), 1, 8), "H--", "X -", "Y -", "Z -", "Disabled", true);
            var settings = TryReadManualParamFromScreen(out var currentSettings, out _) ? currentSettings : CreateFallbackManualParam();
            CommandStateItems = [new("Laser", _laserState), new("CENTER", _centerState), new("Last Command", _lastCommand), new("Result", _lastResult)];
            CommandStateRows = BuildCommandStateRows(selectedHead);
            OnPropertyChanged(nameof(CommandStateItems));
            OnPropertyChanged(nameof(CommandStateRows));
        }

        private static void ApplyManualScriptSetup(CAutomation1ScriptBase script, ST_MANUAL_SCAN_PARAM settings)
        {
            script.DefaultSetting(resetPso: false);
            script.SetFrequency(settings.LaserFrequency);
            script.SetLaserDelay(0.0, 0.0);
            script.SetLaserPower(settings.LaserPower);
            script.SetJumpSpeed(settings.JumpSpeed);
            script.SetMarkSpeed(settings.MarkSpeed);
        }

        private void ApplyLaserActionScriptSetup(CAutomation1ScriptBase script, ST_MANUAL_SCAN_PARAM settings)
        {
            ApplyManualScriptSetup(script, ReadLaserActionSettings(settings));
        }

        private static void ApplyManualLaserOffScriptSetup(CAutomation1ScriptBase script, ST_MANUAL_SCAN_PARAM _)
        {
            script.DefaultSetting(resetPso: false);
        }

        private static void ApplyManualFigureScanSetup(CAutomation1ScriptBase script, ST_MANUAL_SCAN_PARAM settings)
        {
            script.DefaultFigureScanSetting(moveToCenter: false);
            script.SetJumpSpeed(settings.JumpSpeed);
            script.SetMarkSpeed(settings.MarkSpeed);
            script.SetMoveDelay(0.0);
            script.SetScannerAcc(50000.0);
            script.SetLaserDelay(0.0, 0.0);
            script.SetFrequency(settings.LaserFrequency);
            // Optical output (W) is set by the ATT before this script runs.
            script.SetLaserPowerNoDelay(100.0);
            script.SetLaserMode(0);
            script.LaserAuto();
        }

        private static void AppendManualSettingComment(CAutomation1ScriptBase script, ST_MANUAL_SCAN_PARAM settings)
        {
            script.AddLine($"// ManualSetting LaserPower={FormatDouble(settings.LaserPower, 3)} W");
            script.AddLine($"// ManualSetting JumpSpeed={FormatDouble(settings.JumpSpeed, 3)} m/sec");
            script.AddLine($"// ManualSetting MarkSpeed={FormatDouble(settings.MarkSpeed, 3)} m/sec");
            script.AddLine($"// ManualSetting Frequency={FormatDouble(settings.LaserFrequency, 3)} kHz");
            script.AddLine($"// ManualSetting Delay={FormatDouble(settings.LaserOnDelay, 3)}/{FormatDouble(settings.LaserOffDelay, 3)} msec");
            script.AddLine($"// ManualSetting Grid={settings.GridRowLines}x{settings.GridColLines}");
        }

        private void AppendShape(CAutomation1ScriptBase script, ST_MANUAL_SCAN_PARAM settings)
        {
            var shapeName = NormalizeShapeName(settings.ShapeName);
            var cx = settings.OffsetX;
            var cy = settings.OffsetY;
            var half = Math.Abs(settings.ShapeSize) / 2.0;
            switch (shapeName.ToUpperInvariant())
            {
                case "CROSS":
                    DrawLine(script, cx - half, cy, cx + half, cy, settings.LaserOnDelay, settings.LaserOffDelay);
                    DrawLine(script, cx, cy - half, cx, cy + half, settings.LaserOnDelay, settings.LaserOffDelay);
                    break;
                case "RECT":
                    DrawRect(script, cx, cy, half, settings.Direction, settings.LaserOnDelay, settings.LaserOffDelay);
                    break;
                case "GRID":
                    DrawGrid(script, cx, cy, half, settings.GridRowLines, settings.GridColLines, settings.LaserOnDelay, settings.LaserOffDelay);
                    break;
                case "H-LINE":
                case "HLINE":
                    DrawLine(script, cx - half, cy, cx + half, cy, settings.LaserOnDelay, settings.LaserOffDelay);
                    break;
                case "V-LINE":
                case "VLINE":
                    DrawLine(script, cx, cy - half, cx, cy + half, settings.LaserOnDelay, settings.LaserOffDelay);
                    break;
                default:
                    DrawCircle(script, cx, cy, half, settings.Direction, settings.LaserOnDelay, settings.LaserOffDelay);
                    break;
            }
        }

        private static void DrawGrid(CAutomation1ScriptBase script, double cx, double cy, double half, int rows, int cols, double onDelayMsec, double offDelayMsec)
        {
            var rowCount = Math.Max(2, rows);
            var colCount = Math.Max(2, cols);
            var left = cx - half;
            var right = cx + half;
            var bottom = cy - half;
            var top = cy + half;
            for (var row = 0; row < rowCount; row++)
            {
                var y = bottom + ((top - bottom) * row / (rowCount - 1));
                if (row % 2 == 0)
                {
                    DrawLine(script, left, y, right, y, onDelayMsec, offDelayMsec);
                }
                else
                {
                    DrawLine(script, right, y, left, y, onDelayMsec, offDelayMsec);
                }
            }

            for (var col = 0; col < colCount; col++)
            {
                var x = left + ((right - left) * col / (colCount - 1));
                if (col % 2 == 0)
                {
                    DrawLine(script, x, bottom, x, top, onDelayMsec, offDelayMsec);
                }
                else
                {
                    DrawLine(script, x, top, x, bottom, onDelayMsec, offDelayMsec);
                }
            }
        }

        private static void DrawLine(CAutomation1ScriptBase script, double startX, double startY, double endX, double endY, double onDelayMsec, double offDelayMsec)
        {
            script.Jump(startX, startY);
            script.SetMoveDelay(onDelayMsec, applyMinimumDelay: false);
            script.Mark(endX, endY);
            script.SetMoveDelay(offDelayMsec, applyMinimumDelay: false);
        }

        private static void DrawRect(CAutomation1ScriptBase script, double cx, double cy, double half, string direction, double onDelayMsec, double offDelayMsec)
        {
            var points = new List<(double X, double Y)>
            {
                (cx - half, cy - half),
                (cx + half, cy - half),
                (cx + half, cy + half),
                (cx - half, cy + half),
                (cx - half, cy - half)
            };
            if (direction.Equals("CCW", StringComparison.OrdinalIgnoreCase))
            {
                points.Reverse();
            }

            for (var index = 0; index < points.Count - 1; index++)
            {
                DrawLine(script, points[index].X, points[index].Y, points[index + 1].X, points[index + 1].Y, onDelayMsec, offDelayMsec);
            }
        }

        private static void DrawCircle(CAutomation1ScriptBase script, double cx, double cy, double radius, string direction, double onDelayMsec, double offDelayMsec)
        {
            if (radius <= 0.0)
            {
                radius = 0.0005;
            }

            var angle = direction.Equals("CCW", StringComparison.OrdinalIgnoreCase) ? -360.0 : 360.0;
            var startX = cx - radius;
            var startY = cy;
            script.Jump(startX, startY);
            script.SetMoveDelay(onDelayMsec, applyMinimumDelay: false);
            script.Arc(startX, startY, startX, startY, cx, cy, angle);
            script.SetMoveDelay(offDelayMsec, applyMinimumDelay: false);
        }

        private static void EnsureAutomationResponse(string response, string action)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                throw new InvalidOperationException($"{action} failed. Check Automation connection.");
            }

            if (ContainsAutomationFailureToken(response))
            {
                throw new InvalidOperationException($"{action} failed. {FormatAutomationFailureResponse(response)}");
            }
        }

        private bool TryReadManualParamFromScreen(out ST_MANUAL_SCAN_PARAM settings, out string errorMessage)
        {
            try
            {
                settings = ReadManualParamFromScreen();
                errorMessage = "";
                return true;
            }
            catch (InvalidDataException exception)
            {
                settings = CreateFallbackManualParam();
                errorMessage = exception.Message;
                return false;
            }
        }

        private static string ResolveSelectedSettingName(IReadOnlyList<string> settingNames, string selectedSettingName)
        {
            if (settingNames.Count == 0)
            {
                return "CIRCLE_TEST.scan";
            }

            var normalizedSelectedName = NormalizeSettingName(selectedSettingName);
            bool MatchesSelectedSettingName(string name)
            {
                return name.Equals(normalizedSelectedName, StringComparison.OrdinalIgnoreCase);
            }

            bool MatchesDefaultSettingName(string name)
            {
                return name.Equals("CIRCLE_TEST.scan", StringComparison.OrdinalIgnoreCase);
            }

            return settingNames.FirstOrDefault(MatchesSelectedSettingName) ?? settingNames.FirstOrDefault(MatchesDefaultSettingName) ?? settingNames[0];
        }

        private void SyncSettingFilesPage(IReadOnlyList<string> orderedSettingNames, string selectedSettingName)
        {
            _settingFilesPageCount = GetSettingFilesPageCount(orderedSettingNames.Count);
            if (!_lastSettingListSelectedName.Equals(selectedSettingName, StringComparison.OrdinalIgnoreCase))
            {
                var selectedSettingIndex = FindSettingNameIndex(orderedSettingNames, selectedSettingName);
                if (selectedSettingIndex >= 0)
                {
                    _settingFilesPageIndex = selectedSettingIndex / SettingFilesPageSize;
                }
            }

            if (_settingFilesPageCount <= 0)
            {
                _settingFilesPageIndex = 0;
                SettingFilesPageText = "PAGE 0 / 0";
            }
            else
            {
                if (_settingFilesPageIndex < 0)
                {
                    _settingFilesPageIndex = 0;
                }

                if (_settingFilesPageIndex >= _settingFilesPageCount)
                {
                    _settingFilesPageIndex = _settingFilesPageCount - 1;
                }

                SettingFilesPageText = $"PAGE {_settingFilesPageIndex + 1} / {_settingFilesPageCount}";
            }

            _lastSettingListSelectedName = selectedSettingName;
            NotifySettingFilesPageCommands();
        }

        private static int GetSettingFilesPageCount(int settingFileCount)
        {
            if (settingFileCount <= 0)
            {
                return 0;
            }

            return ((settingFileCount - 1) / SettingFilesPageSize) + 1;
        }

        private static int FindSettingNameIndex(IReadOnlyList<string> settingNames, string selectedSettingName)
        {
            for (var index = 0; index < settingNames.Count; index++)
            {
                if (settingNames[index].Equals(selectedSettingName, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        private static IReadOnlyList<string> OrderSettingNames(IReadOnlyList<string> settingNames)
        {
            var orderedSettingNames = new List<string>(settingNames.Count);
            for (var index = 0; index < settingNames.Count; index++)
            {
                orderedSettingNames.Add(settingNames[index]);
            }

            orderedSettingNames.Sort(StringComparer.OrdinalIgnoreCase);
            return orderedSettingNames;
        }

        private static IReadOnlyList<ST_MANUAL_SETTING_FILE> BuildSettingFiles(IReadOnlyList<string> orderedSettingNames, string selectedSettingName, int pageIndex)
        {
            var startIndex = Math.Max(0, pageIndex) * SettingFilesPageSize;
            if (startIndex >= orderedSettingNames.Count)
            {
                return[];
            }

            var itemCount = Math.Min(SettingFilesPageSize, orderedSettingNames.Count - startIndex);
            var settingFiles = new List<ST_MANUAL_SETTING_FILE>(itemCount);
            for (var index = 0; index < itemCount; index++)
            {
                var settingName = orderedSettingNames[startIndex + index];
                settingFiles.Add(new ST_MANUAL_SETTING_FILE(settingName, settingName.Equals(selectedSettingName, StringComparison.OrdinalIgnoreCase)));
            }

            return settingFiles;
        }

        private static IReadOnlyList<ST_MANUAL_PARAMETER> BuildSettingParameters(ST_MANUAL_SCAN_PARAM settings, IReadOnlyList<ST_MANUAL_SCAN_FORM> formItems)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ShapeSize"] = FormatDouble(settings.ShapeSize, 3),
                ["OffsetX"] = FormatDouble(settings.OffsetX, 3),
                ["OffsetY"] = FormatDouble(settings.OffsetY, 3),
                ["Direction"] = settings.Direction,
                ["ShapeName"] = settings.ShapeName,
                ["GridRowLines"] = settings.GridRowLines.ToString(CultureInfo.InvariantCulture),
                ["GridColLines"] = settings.GridColLines.ToString(CultureInfo.InvariantCulture),
                ["LaserPower"] = FormatDouble(settings.LaserPower, 2),
                ["JumpSpeed"] = FormatDouble(settings.JumpSpeed, 3),
                ["MarkSpeed"] = FormatDouble(settings.MarkSpeed, 3),
                ["LaserFrequency"] = FormatDouble(settings.LaserFrequency, 3),
                ["LaserOnDelay"] = FormatDouble(settings.LaserOnDelay, 3),
                ["LaserOffDelay"] = FormatDouble(settings.LaserOffDelay, 3),
                ["LaserOnMode"] = settings.LaserOnMode?.Trim().ToUpperInvariant() ?? "",
                ["Time"] = settings.Time.ToString("R", CultureInfo.InvariantCulture),
                ["Count"] = settings.Count.ToString(CultureInfo.InvariantCulture),
                ["RepeatCount"] = settings.RepeatCount.ToString(CultureInfo.InvariantCulture),
                ["RepeatDelayTime"] = settings.RepeatDelayTime.ToString("R", CultureInfo.InvariantCulture)
            };
            ST_MANUAL_SCAN_FORM[] requiredParameters = [
                new("ShapeName", "Shape Type", EN_RECIPE_DATA_TYPE.String, "", true, true, "Circle", 0, 0, "", 1), 
                new("ShapeSize", "Shape Size", EN_RECIPE_DATA_TYPE.Double, "mm", true, true, "0.350", 0.001, 100, "", 2), 
                new("Direction", "Direction", EN_RECIPE_DATA_TYPE.String, "", true, true, "CW", 0, 0, "", 3), 
                new("OffsetX", "Offset X", EN_RECIPE_DATA_TYPE.Double, "mm", true, true, "0.000", -100, 100, "", 4), 
                new("OffsetY", "Offset Y", EN_RECIPE_DATA_TYPE.Double, "mm", true, true, "0.000", -100, 100, "", 5), 
                new("GridRowLines", "Grid Row Count", EN_RECIPE_DATA_TYPE.Int, "ea", true, true, "5", 2, 200, "", 6), 
                new("GridColLines", "Grid Col Count", EN_RECIPE_DATA_TYPE.Int, "ea", true, true, "5", 2, 200, "", 7), 
                new("LaserOnMode", "LaserAction_Mode", EN_RECIPE_DATA_TYPE.String, "", true, true, "TIME", 0, 0, "", 70), 
                new("Time", "LaserAction_On Time", EN_RECIPE_DATA_TYPE.Double, "msec", true, true, "1000", 0, 0, "", 80), 
                new("Count", "LaserAction_Shot Count", EN_RECIPE_DATA_TYPE.Int, "count", true, true, "1", 1, int.MaxValue, "", 90), 
                new("RepeatCount", "LaserAction_Repeat Count", EN_RECIPE_DATA_TYPE.Int, "count", true, true, "1", 1, int.MaxValue, "", 100), 
                new("RepeatDelayTime", "LaserAction_Repeat Delay Time", EN_RECIPE_DATA_TYPE.Double, "msec", true, true, "0", 0, 0, "", 110)
            ];
            var requiredKeys = requiredParameters.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Older form files hid these fields. They remain editable after upgrading the UI.
            var parameters = requiredParameters.Select(item => formItems.FirstOrDefault(form => form.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))is { } form ? form with { DisplayName = item.DisplayName, Show = true, Use = true, DisplayOrder = item.DisplayOrder } : item).Concat(formItems.Where(item => !requiredKeys.Contains(item.Name)));
            bool IsVisibleManualParameter(global::Drilling.Common.Managers.ST_MANUAL_SCAN_FORM item)
            {
                return item.Show && item.Use;
            }

            int SelectManualParameterDisplayOrder(global::Drilling.Common.Managers.ST_MANUAL_SCAN_FORM item)
            {
                return item.DisplayOrder;
            }

            global::Drilling.UI.Menu.Menus.ST_MANUAL_PARAMETER BuildManualParameterRow(global::Drilling.Common.Managers.ST_MANUAL_SCAN_FORM item)
            {
                return new ST_MANUAL_PARAMETER(item.Name, item.DisplayName, values.TryGetValue(item.Name, out var value) ? value : item.DefaultValue, item.Unit, item.DataType, item.Min, item.Max);
            }

            return parameters.Where(IsVisibleManualParameter).OrderBy(SelectManualParameterDisplayOrder).Select(BuildManualParameterRow).ToArray();
        }

        private double ReadLaserMoveDelaySeconds(ST_MANUAL_SCAN_PARAM settings)
        {
            if (IsLaserTimeMode)
            {
                return ReadLaserOnTimeMsec();
            }

            var shotCount = ReadLaserShotCount();
            if (settings.LaserFrequency <= 0.0)
            {
                throw new InvalidDataException("Laser Frequency must be greater than 0 for COUNT mode.");
            }

            return (shotCount + 1.0) / settings.LaserFrequency;
        }

        private ST_MANUAL_SCAN_PARAM ReadLaserActionSettings(ST_MANUAL_SCAN_PARAM manualSettings)
        {
            return manualSettings with
            {
                LaserPower = ReadRequiredDouble(_laserActionPower, "Laser Action Power"),
                LaserFrequency = ReadRequiredDouble(_laserActionFrequency, "Laser Action Frequency"),
                Time = ReadLaserOnTimeMsec(),
                Count = ReadLaserShotCount()
            };
        }

        private double ReadLaserOnTimeMsec()
        {
            return ReadRequiredDouble(LaserOnTimeMsec, "Laser On Time");
        }

        private int ReadLaserShotCount()
        {
            if (int.TryParse(LaserShotCount?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }

            throw new InvalidDataException($"Shot Count value is not a valid integer: {LaserShotCount}");
        }

        private static int ReadGridLineCount(string value, string name)
        {
            if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            {
                throw new InvalidDataException($"{name} value is not a valid integer: {value}");
            }

            if (result < 2)
            {
                throw new InvalidDataException($"{name} must be greater than or equal to 2.");
            }

            return result;
        }

        private int ReadManualInt(string parameterName)
        {
            var row = FindManualParameter(parameterName);
            var value = ReadManualText(row);
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            {
                ValidateManualParameterRange(row, result);
                return result;
            }

            throw new InvalidDataException($"{parameterName} value is not a valid integer: {value}");
        }

        private string ReadManualText(string parameterName, string defaultValue = "")
        {
            bool MatchesManualParameterDisplayName(global::Drilling.UI.Menu.Menus.ST_MANUAL_PARAMETER parameter)
            {
                return parameter.Parameter.Equals(parameterName, StringComparison.OrdinalIgnoreCase);
            }

            var row = SettingParameters.FirstOrDefault(MatchesManualParameterDisplayName);
            if (row is null)
            {
                if (!string.IsNullOrWhiteSpace(defaultValue))
                {
                    return defaultValue.Trim();
                }

                throw new InvalidDataException($"{parameterName} parameter does not exist in Manual Setting.");
            }

            return ReadManualText(row, defaultValue);
        }

        private ST_MANUAL_PARAMETER FindManualParameter(string parameterName)
        {
            bool MatchesManualParameterName(global::Drilling.UI.Menu.Menus.ST_MANUAL_PARAMETER parameter)
            {
                return parameter.Key.Equals(parameterName, StringComparison.OrdinalIgnoreCase) || parameter.Parameter.Equals(parameterName, StringComparison.OrdinalIgnoreCase);
            }

            return SettingParameters.FirstOrDefault(MatchesManualParameterName) ?? throw new InvalidDataException($"{parameterName} parameter does not exist in Manual Setting.");
        }

        private static string ReadManualText(ST_MANUAL_PARAMETER row, string defaultValue = "")
        {
            var value = row.Value?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (!string.IsNullOrWhiteSpace(defaultValue))
            {
                return defaultValue.Trim();
            }

            throw new InvalidDataException($"{row.Parameter} value is empty.");
        }

        private static void ValidateManualParameterRange(ST_MANUAL_PARAMETER parameter, double value)
        {
            if (parameter.Min.Equals(parameter.Max))
            {
                return;
            }

            if (value < parameter.Min || value > parameter.Max)
            {
                throw new InvalidDataException($"{parameter.Parameter} range warning. Input {value:0.###} {parameter.Unit} is outside {parameter.Min:0.###} - {parameter.Max:0.###} {parameter.Unit}.");
            }
        }

        private static ST_MANUAL_SCAN_PARAM CreateFallbackManualParam()
        {
            return new ST_MANUAL_SCAN_PARAM(10.0, 0.0, 0.0, "CW", "Circle");
        }

        private static string NormalizeLaserOnMode(string? value)
        {
            return value?.Trim().Equals("COUNT", StringComparison.OrdinalIgnoreCase) == true ? "COUNT" : "TIME";
        }

        private static bool IsLaserTimeModeValue(string? value)
        {
            return !NormalizeLaserOnMode(value).Equals("COUNT", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetManualSettingNameFromParameter(object? parameter)
        {
            var value = parameter switch
            {
                ST_MANUAL_SETTING_FILE settingFile => settingFile.Name,
                string text => text,
                _ => ""
            };
            return NormalizeManualSettingNameInput(value);
        }

        private static string? ShowManualSettingNameDialog(string title, string message, string initialValue, Func<string, string>? validate = null)
        {
            var dialog = new CRecipeNameDialog(title, message, initialValue, validate)
            {
                Owner = GetActiveWindow()
            };
            return dialog.ShowDialog() == true ? NormalizeManualSettingNameInput(dialog.RecipeName) : null;
        }

        private static bool ConfirmManualSettingDelete(string settingName)
        {
            var dialog = new CRecipeConfirmDialog("Manual Setting 삭제", $"{settingName} 설정을 삭제하시겠습니까?\n삭제한 설정 파일은 복구할 수 없습니다.", "삭제")
            {
                Owner = GetActiveWindow()
            };
            return dialog.ShowDialog() == true;
        }

        private static string NormalizeManualSettingNameInput(string value)
        {
            var normalized = value.Trim();
            if (normalized.EndsWith(".scan", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^5];
            }

            normalized = normalized.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? "" : $"{normalized}.scan";
        }

        private static string ValidateManualSettingName(string settingName, IReadOnlyList<string> settingNames, string currentSettingName = "")
        {
            if (string.IsNullOrWhiteSpace(settingName))
            {
                return "Manual Setting 이름을 입력하세요.";
            }

            var settingId = Path.GetFileNameWithoutExtension(settingName.Trim());
            foreach (var character in settingId)
            {
                if (Path.GetInvalidFileNameChars().Contains(character))
                {
                    return $"Manual Setting 이름에는 '{character}' 문자를 사용할 수 없습니다.";
                }
            }

            if (settingId is "." or ".." || settingId.EndsWith(".", StringComparison.Ordinal))
            {
                return "파일 이름으로 사용할 수 없는 Manual Setting 이름입니다.";
            }

            var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CON",
                "PRN",
                "AUX",
                "NUL",
                "COM1",
                "COM2",
                "COM3",
                "COM4",
                "COM5",
                "COM6",
                "COM7",
                "COM8",
                "COM9",
                "LPT1",
                "LPT2",
                "LPT3",
                "LPT4",
                "LPT5",
                "LPT6",
                "LPT7",
                "LPT8",
                "LPT9"
            };
            if (reservedNames.Contains(settingId))
            {
                return "Windows에서 예약된 이름은 사용할 수 없습니다.";
            }

            bool IsDuplicateSettingName(string name)
            {
                return name.Equals(settingName, StringComparison.OrdinalIgnoreCase) && !name.Equals(currentSettingName, StringComparison.OrdinalIgnoreCase);
            }

            var exists = settingNames.Any(IsDuplicateSettingName);
            return exists ? $"{settingName} Manual Setting이 이미 존재합니다." : "";
        }

        private static string NormalizeSettingName(string settingName)
        {
            var normalized = Path.GetFileName(settingName.Trim());
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "CIRCLE_TEST.scan";
            }

            return normalized.EndsWith(".scan", StringComparison.OrdinalIgnoreCase) ? normalized : $"{normalized}.scan";
        }

        private static string NormalizeScriptName(string commandName)
        {
            bool IsValidScriptNameCharacter(char character)
            {
                return char.IsLetterOrDigit(character) || character == '_' || character == '-';
            }

            var normalized = new string (commandName.Where(IsValidScriptNameCharacter).ToArray());
            return string.IsNullOrWhiteSpace(normalized) ? "MANUAL" : normalized.ToUpperInvariant();
        }

        private static string BuildShapeScanScriptName(int headNo, string shapeName)
        {
            var normalizedShape = NormalizeShapeName(shapeName).Replace("-", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
            return $"MANUAL_H{Math.Clamp(headNo, 1, 8):00}_SHAPE_{normalizedShape}";
        }

        private static string NormalizeShapeName(string shapeName)
        {
            var normalized = shapeName.Trim();
            return normalized.ToUpperInvariant() switch
            {
                "CROSS" => "Cross",
                "RECT" or "RECTANGLE" => "Rect",
                "GRID" => "Grid",
                "H-LINE" or "HLINE" or "H_LINE" => "H-Line",
                "V-LINE" or "VLINE" or "V_LINE" => "V-Line",
                _ => "Circle"
            };
        }

#endregion
#region Workspace
        public CButtonCommand SelectManualControlTabCommand { get; }
        public bool IsManualControllerTab => _manualControlTab == "CONTROLLER";
        public bool IsManualMotionTab => _manualControlTab == "MOTION";
        public bool IsManualScanTab => _manualControlTab == "SCAN";
        public bool IsManualPowerMeasureTab => _manualControlTab == "POWERMEASURE";
        public bool IsManualMofTab => _manualControlTab == "MOF";
        public bool IsManualStageReviewTab => IsManualScanTab || IsManualPowerMeasureTab || IsManualMofTab;

        private void SelectManualControlTab(object? parameter)
        {
            var tab = parameter?.ToString()?.Trim().ToUpperInvariant();
            if (tab is not ("CONTROLLER" or "MOTION" or "SCAN" or "POWERMEASURE" or "MOF") || tab == _manualControlTab)
                return;
            _manualControlTab = tab;
            if (tab != "MOTION")
                _ = StopMotionJog();
            if (!IsManualStageReviewTab)
                _powerMeasurePanel?.StageMotion.SetActive(false);
            _homeSpeedPanel?.SetActive(_homeSpeedController.HasValue && IsManualControllerTab && IsScannerWorkspace);
            OnPropertyChanged(nameof(IsManualControllerTab));
            OnPropertyChanged(nameof(IsManualMotionTab));
            OnPropertyChanged(nameof(IsManualScanTab));
            OnPropertyChanged(nameof(IsManualPowerMeasureTab));
            OnPropertyChanged(nameof(IsManualStageReviewTab));
            OnPropertyChanged(nameof(IsManualMofTab));
            if (IsManualPowerMeasureTab)
                PowerMeasurePanel.UpdateHeads(SelectedManualHeadNos);
            if (IsScannerWorkspace && (IsManualScanTab || IsManualMofTab))
                RefreshAutomationStatusSnapshot();
        }

        public IReadOnlyList<int> SelectedManualHeadNos => _selectedManualHeadNos.Order().ToArray();
        public CButtonCommand SelectManualHeadsCommand { get; }

        private void SelectManualHeads(object? parameter)
        {
            if (_powerMeasurePanel?.IsRunning == true)
                return;
            var selection = parameter?.ToString()?.Trim().ToUpperInvariant();
            if (selection is not ("ALL" or "ODD" or "EVEN" or "CLEAR") || !_selectFocusedHeadCommand.CanExecute(null))
                return;
            var heads = HeadCards.Where(head => head.IsEnabled && _selectFocusedHeadCommand.CanExecute(head.HeadNo)).Where(head => selection == "ALL" || selection == "ODD" && head.HeadNo % 2 == 1 || selection == "EVEN" && head.HeadNo % 2 == 0).Select(head => head.HeadNo).ToArray();
            _manualHeadSelectionInitialized = true;
            _selectedManualHeadNos.Clear();
            _selectedManualHeadNos.UnionWith(heads);
            HeadCards = ApplyManualHeadSelection(HeadCards, _selectedHeadNoProvider());
            HeadGroups = BuildHeadGroups(HeadCards);
            OnPropertyChanged(nameof(HeadCards));
            RefreshManualControllerHeads();
            if (_selectedManualHeadNos.Count > 0 && !_selectedManualHeadNos.Contains(_selectedHeadNoProvider()))
                _selectFocusedHeadCommand.Execute(_selectedManualHeadNos.Min());
        }

        private void ToggleManualHead(object? parameter)
        {
            if (_powerMeasurePanel?.IsRunning == true)
                return;
            if (!int.TryParse(parameter?.ToString(), out var headNo) || !HeadCards.Any(head => head.HeadNo == headNo && head.IsEnabled) || !_selectFocusedHeadCommand.CanExecute(headNo))
                return;
            _manualHeadSelectionInitialized = true;
            if (!_selectedManualHeadNos.Add(headNo))
                _selectedManualHeadNos.Remove(headNo);
            HeadCards = ApplyManualHeadSelection(HeadCards, _selectedHeadNoProvider());
            HeadGroups = BuildHeadGroups(HeadCards);
            OnPropertyChanged(nameof(HeadCards));
            RefreshManualControllerHeads();
            if (_selectedManualHeadNos.Contains(headNo))
                _selectFocusedHeadCommand.Execute(headNo);
            else if (_selectedManualHeadNos.Count > 0 && !_selectedManualHeadNos.Contains(_selectedHeadNoProvider()))
                _selectFocusedHeadCommand.Execute(_selectedManualHeadNos.Min());
        }

        private IReadOnlyList<ST_MANUAL_HEAD_CARD> ApplyManualHeadSelection(IReadOnlyList<ST_MANUAL_HEAD_CARD> cards, int initialHeadNo)
        {
            var enabled = cards.Where(head => head.IsEnabled).Select(head => head.HeadNo).ToHashSet();
            if (!_manualHeadSelectionInitialized)
            {
                if (enabled.Contains(initialHeadNo))
                    _selectedManualHeadNos.Add(initialHeadNo);
                _manualHeadSelectionInitialized = true;
            }

            _selectedManualHeadNos.IntersectWith(enabled);
            MofPreview.SetSelectedHeads(_selectedManualHeadNos);
            if (_mofRun is not null)
            {
                _mofRun.SetSelectedHeads(_selectedManualHeadNos);
                if (IsScannerWorkspace && IsManualMofTab)
                    _mofRun.Refresh();
            }

            return cards.Select(head => head with { IsSelected = _selectedManualHeadNos.Contains(head.HeadNo) }).ToArray();
        }

        public CManualPowerMeasurePanel PowerMeasurePanel
        {
            get
            {
                if (_powerMeasurePanel is not null)
                    return _powerMeasurePanel;
                _powerMeasurePanel = new(
                    _manager.PowerMeasure, 
                    () => _manager.Interface().GetInterfaceList(EN_EQP_MODULE.PowerMeter), 
                    new CPowerTableFile(_manager.ConfigRoot, simulation: true), 
                    message => CRecipeConfirmDialog.ShowWarning("PowerMeasure", message), 
                    message => CRecipeConfirmDialog.ShowConfirmation("PowerMeasure", message), 
                    () => CMotorStatusPollingService.ReadCachedSnapshot(_manager.Interface()), 
                    () => CMotorStatusPollingService.ReadStageConnectionState(_manager.Interface()), 
                    stageSimulation: _manager.Interface().GetStageMotionSimulation(),
                    stageMove: ManualStageMoveOperation,
                    runMeasurement: async (targets, token) =>
                    {
                        var recipe = await LoadSelectedRecipe(token) ?? throw new InvalidOperationException("선택된 Recipe가 없습니다.");
                        await CPowerTableFile.CreateRun(_manager, recipe, targets, automatic: false).Execute(token);
                    }
                );
                _powerMeasurePanel.StartAllowed = () =>
                {
                    if (ManualStageMoveOperation.IsRunning)
                    {
                        ShowManualWarning("Stage/Review 수동 이동과 정리 작업이 끝난 후 PowerMeasure를 시작해 주세요.");
                        return false;
                    }
                    return CheckManualStageMoveAllowed();
                };
                _powerMeasurePanel.StageMotion.MoveAllowed = CheckManualStageMoveAllowed;
                _powerMeasurePanel.RunningChanged += () =>
                {
                    SelectHeadCommand.NotifyCanExecuteChanged();
                    SelectManualHeadsCommand.NotifyCanExecuteChanged();
                };
                _powerMeasurePanel.UpdateHeads(SelectedManualHeadNos);
                return _powerMeasurePanel;
            }
        }

        private void RefreshPowerMeasurePanel() => _powerMeasurePanel?.UpdateHeads(SelectedManualHeadNos);
        public override async Task StopForHeavyAlarm()
        {
            // Stop only this menu's operations. Stage-owned MOF Y travel remains Stage-owned.
            var stageOwned = _manager.Station().StageRequests.IsRunning || _manager.Station().StageRequests.NeedsCleanup || _manager.Station().ManualMofStatus is { IsRunning: true } or { RequiresStop: true };
            _manualMotionScriptCancellation?.Cancel();
            var laser = _activeManualLaserAction;
            var shape = _activeManualShapeScan;
            laser?.Cancellation.Cancel();
            shape?.Cancellation.Cancel();
            var failures = new List<Exception>();
            async Task Attempt(Func<Task> stop)
            {
                try
                {
                    await stop();
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }

            if (Review is not null)
                await Attempt(Review.StopForHeavyAlarm);
            if (_manualStageMoveOperation is not null)
                await Attempt(_manualStageMoveOperation.StopAsync);
            if (_powerMeasurePanel?.IsRunning == true)
                await Attempt(_powerMeasurePanel.Stop);
            if (!stageOwned)
                _powerMeasurePanel?.StageMotion.Stop();
            if (_motionSession is not null)
                await Attempt(() => _motionSession.StopAll(async () =>
                {
                    var motionFailures = new List<Exception>();
                    try
                    {
                        await StopMotionScriptTask();
                    }
                    catch (Exception error)
                    {
                        motionFailures.Add(error);
                    }

                    foreach (var target in _manualMotionTargets.ToArray())
                        try
                        {
                            EnsureAutomationResponse(await _manager.automation.AbortMotion(target.Axis, target.Controller), "HEAVY ALARM MOVE STOP");
                        }
                        catch (Exception error)
                        {
                            motionFailures.Add(error);
                        }

                    if (motionFailures.Count > 0)
                        throw new AggregateException(motionFailures);
                    _manualMotionTargets.Clear();
                }));
            await Attempt(() => Task.WhenAll(new[] { laser?.Completed.Task, shape?.Completed.Task }.OfType<Task>()));
            if (_manualShapeScanPendingStops is { Count: > 0 } || _manualLaserOffUnconfirmed || _manualLaserOffPendingTargets.Count > 0 || _manualShapeScanAttenuatorPositioning?.HasPendingStops == true || _manualMotionStopPending)
                failures.Add(new InvalidOperationException("정지가 확인되지 않은 Manual 동작이 남아 있습니다. STOP/LASER OFF와 장치 상태를 확인해 주세요."));
            if (failures.Count > 0)
                throw new AggregateException("Manual 동작 정지 실패", failures);
        }

        public void ConfigureReview(CMenuReview review) => Review = review;
        private void SelectManualWorkspace(object? parameter)
        {
            var workspace = parameter?.ToString()?.ToUpperInvariant();
            if (workspace is not ("SCANNER" or "STAGE_VISION" or "REVIEW"))
                return;
            if (Review?.BlocksMenuNavigation == true && workspace != "REVIEW")
            {
                CRecipeConfirmDialog.ShowWarning("검사 진행 중", "검사를 중지한 후 이동해 주세요.");
                return;
            }

            var wasReview = IsReviewWorkspace;
            _isReviewWorkspace = workspace == "REVIEW";
            IsScannerWorkspace = workspace == "SCANNER";
            OnPropertyChanged(nameof(IsReviewWorkspace));
            OnPropertyChanged(nameof(IsStageVisionWorkspace));
            if (IsReviewWorkspace && !wasReview)
                Review?.ResetForMenuOpen();
            if (!IsScannerWorkspace)
                _powerMeasurePanel?.StageMotion.SetActive(false);
            _homeSpeedPanel?.SetActive(_homeSpeedController.HasValue && IsManualControllerTab && IsScannerWorkspace);
            if (IsScannerWorkspace || IsReviewWorkspace)
            {
                StopStageMotionPolling();
            }

            _ = _refreshCurrentScreen();
        }

        private async Task SendVisionProtocolCommand()
        {
            if (IsVisionProtocolBusy)
            {
                return;
            }

            var interfaceManager = _manager.Interface();
            int SelectInterfaceNumber(ST_INTERFACE_DATA data)
            {
                return data.Number;
            }

            var interfaceData = interfaceManager.GetInterfaceList(EN_EQP_MODULE.Vision).OrderBy(SelectInterfaceNumber).FirstOrDefault();
            if (interfaceData is null)
            {
                const string message = "Vision interface is not configured.";
                AppendVisionProtocolLog("ERR", message);
                VisionProtocolStatus = "NOT CONFIGURED";
                _setStatusMessage(message);
                _refreshShellStatus();
                return;
            }

            IsVisionProtocolBusy = true;
            try
            {
                var function = BuildVisionProtocolFunction(validate: true);
                _setStatusMessage($"Vision protocol command sending: {SelectedVisionProtocolCommand.DisplayName}");
                _refreshShellStatus();
                var response = await interfaceManager.ExecuteFunction(EN_EQP_MODULE.Vision, interfaceData.Number, function, _menuLifetimeCancellation.Token);
                RefreshVisionProtocolStatus(writeLog: false);
                if (string.IsNullOrWhiteSpace(response))
                {
                    var status = GetVisionProtocolCommunicationStatus(interfaceData.Number);
                    var error = string.IsNullOrWhiteSpace(status?.LastError) ? "No response was returned." : status.LastError;
                    AppendVisionProtocolLog("ERR", error);
                    _setStatusMessage($"Vision protocol command failed. {error}");
                }
                else
                {
                    _setStatusMessage($"Vision protocol command completed: {SelectedVisionProtocolCommand.DisplayName}");
                }
            }
            catch (OperationCanceledException)
            {
                AppendVisionProtocolLog("ERR", "Command canceled.");
                _setStatusMessage("Vision protocol command canceled.");
            }
            catch (Exception exception)when (exception is InvalidOperationException or IOException or TimeoutException or KeyNotFoundException or FormatException)
            {
                AppendVisionProtocolLog("ERR", exception.Message);
                VisionProtocolStatus = $"ERROR / {exception.Message}";
                _setStatusMessage($"Vision protocol command failed. {exception.Message}");
            }
            finally
            {
                IsVisionProtocolBusy = false;
                _refreshShellStatus();
            }
        }

        private void ResetVisionProtocolInputs()
        {
            _visionProtocolInspectionType = "0";
            VisionProtocolMarkRows.Clear();
            if (VisionProtocolUsesMarkRows)
            {
                VisionProtocolMarkRows.Add(CreateVisionProtocolMarkRow());
            }

            OnPropertyChanged(nameof(VisionProtocolCommandDescription));
            OnPropertyChanged(nameof(VisionProtocolUsesMarkRows));
            OnPropertyChanged(nameof(VisionProtocolIsFlyingReady));
            OnPropertyChanged(nameof(VisionProtocolHasNoArguments));
            OnPropertyChanged(nameof(VisionProtocolInspectionType));
            NotifyVisionProtocolInputChanged();
        }

        private void AddVisionProtocolMarkRow()
        {
            if (!VisionProtocolIsFlyingReady || VisionProtocolMarkRows.Count >= CVisionProtocol.MaximumMarkCount)
            {
                return;
            }

            VisionProtocolMarkRows.Add(CreateVisionProtocolMarkRow());
            NotifyVisionProtocolInputChanged();
        }

        private void RemoveVisionProtocolMarkRow()
        {
            if (!VisionProtocolIsFlyingReady || VisionProtocolMarkRows.Count == 0)
            {
                return;
            }

            VisionProtocolMarkRows.RemoveAt(VisionProtocolMarkRows.Count - 1);
            NotifyVisionProtocolInputChanged();
        }

        private ST_MANUAL_VISION_MARK_SEARCH CreateVisionProtocolMarkRow()
        {
            return new ST_MANUAL_VISION_MARK_SEARCH("1", "1", "1", "1", "1", "1", NotifyVisionProtocolInputChanged);
        }

        private void NotifyVisionProtocolInputChanged()
        {
            OnPropertyChanged(nameof(VisionProtocolRequestPreview));
            OnPropertyChanged(nameof(VisionProtocolMarkRowCountText));
            VisionProtocolAddMarkCommand.NotifyCanExecuteChanged();
            VisionProtocolRemoveMarkCommand.NotifyCanExecuteChanged();
        }

        private string BuildVisionProtocolFunction(bool validate)
        {
            var command = SelectedVisionProtocolCommand.CommandText;
            if (!VisionProtocolUsesMarkRows)
            {
                return command;
            }

            if (command == "MARK_FIND_SINGLE")
            {
                if (VisionProtocolMarkRows.Count != 1)
                {
                    return validate ? throw new FormatException("Mark Find Single은 Mark 입력 행이 1개여야 합니다.") : $"{command}:입력 필요";
                }

                return $"{command}:{BuildVisionProtocolMarkSearch(VisionProtocolMarkRows[0], validate)}";
            }

            var inspectionType = NormalizeVisionProtocolInteger(VisionProtocolInspectionType, "Inspection Type", validate);
            var marks = new string[VisionProtocolMarkRows.Count];
            for (var index = 0; index < VisionProtocolMarkRows.Count; index++)
            {
                marks[index] = BuildVisionProtocolMarkSearch(VisionProtocolMarkRows[index], validate);
            }

            return $"{command}:{inspectionType}:{string.Join(';', marks)}";
        }

        private static string BuildVisionProtocolMarkSearch(ST_MANUAL_VISION_MARK_SEARCH row, bool validate)
        {
            return string.Join(
                ',', 
                NormalizeVisionProtocolInteger(row.CellNo, "Cell No", validate), 
                NormalizeVisionProtocolInteger(row.DoeNo, "DOE/Beam", validate), 
                NormalizeVisionProtocolInteger(row.LineNo, "Line No", validate), 
                NormalizeVisionProtocolInteger(row.IndexX, "Index X", validate), 
                NormalizeVisionProtocolInteger(row.IndexY, "Index Y", validate), 
                NormalizeVisionProtocolInteger(row.HeadNo, "Head No", validate)
            );
        }

        private static string NormalizeVisionProtocolInteger(string value, string fieldName, bool validate)
        {
            var normalized = value.Trim();
            if (!validate)
            {
                return string.IsNullOrWhiteSpace(normalized) ? "?" : normalized;
            }

            return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed.ToString(CultureInfo.InvariantCulture) : throw new FormatException($"{fieldName}에는 정수를 입력해야 합니다. 입력값={value}");
        }

        private void RefreshVisionProtocolStatus(bool writeLog)
        {
            int SelectStatusNumber(ST_INTERFACE_COMM_STATUS item)
            {
                return item.Number;
            }

            var status = _manager.Interface().GetInterfaceCommunicationList(EN_EQP_MODULE.Vision).OrderBy(SelectStatusNumber).FirstOrDefault();
            if (status is null)
            {
                VisionProtocolEndpoint = "-";
                VisionProtocolStatus = "NOT CONFIGURED";
                VisionProtocolKeepAlive = "NOT CONFIGURED";
                if (writeLog)
                {
                    AppendVisionProtocolLog("ERR", "Vision interface is not configured.");
                }

                return;
            }

            VisionProtocolEndpoint = $"{status.NickName}[{status.Number}] / {status.Endpoint}";
            var state = status.IsSimulation ? "SIMULATION" : status.ConnectionState.ToString().ToUpperInvariant();
            if (status.IsSimulation)
            {
                VisionProtocolKeepAlive = "SIMULATION";
            }

            VisionProtocolStatus = string.IsNullOrWhiteSpace(status.LastError) ? state : $"{state} / {status.LastError}";
            if (writeLog)
            {
                AppendVisionProtocolLog("INFO", $"{VisionProtocolEndpoint} / {VisionProtocolStatus}");
            }
        }

        private ST_INTERFACE_COMM_STATUS? GetVisionProtocolCommunicationStatus(int number)
        {
            foreach (var status in _manager.Interface().GetInterfaceCommunicationList(EN_EQP_MODULE.Vision))
            {
                if (status.Number == number)
                {
                    return status;
                }
            }

            return null;
        }

        private ST_INTERFACE_COMM_STATUS? GetPrimaryVisionProtocolCommunicationStatus()
        {
            ST_INTERFACE_COMM_STATUS? selected = null;
            foreach (var status in _manager.Interface().GetInterfaceCommunicationList(EN_EQP_MODULE.Vision))
            {
                if (selected is null || status.Number < selected.Number)
                {
                    selected = status;
                }
            }

            return selected;
        }

        private void OnVisionProtocolTrace(ST_INTERFACE_PROTOCOL_TRACE trace)
        {
            if (trace.Module != EN_EQP_MODULE.Vision)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                ApplyVisionProtocolTrace(trace);
                return;
            }

            void ApplyVisionProtocolTraceCallback()
            {
                ApplyVisionProtocolTrace(trace);
            }

            dispatcher.BeginInvoke(ApplyVisionProtocolTraceCallback);
        }

        private void ApplyVisionProtocolTrace(ST_INTERFACE_PROTOCOL_TRACE trace)
        {
            if (trace.Direction.Equals("PACKET", StringComparison.OrdinalIgnoreCase))
            {
                AppendVisionProtocolPacketLog(trace.OccurredAt, trace.Message);
            }
            else
            {
                AppendVisionProtocolLog(trace.Direction, $"{trace.NickName}[{trace.Number}] {trace.Message}");
            }

            if (trace.Message.Contains("Code : 1999", StringComparison.OrdinalIgnoreCase) || trace.Message.Contains("CMD=1999", StringComparison.OrdinalIgnoreCase))
            {
                VisionProtocolKeepAlive = trace.Message.Contains("[RECV]", StringComparison.OrdinalIgnoreCase) || trace.Direction.Equals("RX", StringComparison.OrdinalIgnoreCase) ? $"{trace.OccurredAt.LocalDateTime:HH:mm:ss.fff} RECEIVED" : $"{trace.OccurredAt.LocalDateTime:HH:mm:ss.fff} ACK SENT";
            }
            else if (trace.Message.Contains("KEEP-ALIVE TIMEOUT", StringComparison.OrdinalIgnoreCase))
            {
                VisionProtocolKeepAlive = "TIMEOUT";
            }
            else if (trace.Message.Contains("CLIENT_DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            {
                VisionProtocolKeepAlive = "DISCONNECTED";
            }
            else if (trace.Message.Contains("CLIENT_CONNECTED", StringComparison.OrdinalIgnoreCase))
            {
                VisionProtocolKeepAlive = "WAITING";
            }

            RefreshVisionProtocolStatus(writeLog: false);
        }

        private void AppendVisionProtocolLog(string direction, string message)
        {
            AddVisionProtocolLogLine($"{DateTime.Now:yy/MM/dd HH:mm:ss.fff} [{direction.Trim().ToUpperInvariant()}] {message}");
        }

        private void AppendVisionProtocolPacketLog(DateTimeOffset occurredAt, string message)
        {
            var timestamp = occurredAt.LocalDateTime.ToString("yy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var lines = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                AddVisionProtocolLogLine($"{timestamp} {line}");
            }
        }

        private void AddVisionProtocolLogLine(string line)
        {
            // UI log retention is independent of the variable-length inspection packet capacity.
            const int maxLogCount = 2048 + 40;
            while (VisionProtocolLogs.Count >= maxLogCount)
            {
                VisionProtocolLogs.RemoveAt(0);
            }

            VisionProtocolLogs.Add(line);
        }

        private static IReadOnlyList<ST_DISPLAY_ITEM> BuildSelectedHeadItems(ST_MANUAL_HEAD_CARD selectedHead)
        {
            return[
                new("Head", selectedHead.HeadName), 
                new("X Axis", selectedHead.XSummary), 
                new("Y Axis", selectedHead.YSummary), 
                new("Z Axis", selectedHead.ZSummary), 
                new("Servo", selectedHead.State), 
                new("Motion", selectedHead.State)
            ];
        }

        private static IReadOnlyList<ST_MANUAL_HEAD_CARD> BuildHeadCards(int selectedHeadNo, IReadOnlyList<ST_SYSTEM_PARAMETER> settings, IReadOnlyList<ST_MANUAL_SCANNER_AXIS> selectedScannerAxes)
        {
            const int headCount = 8;
            var enabledHeadNos = CScannerStatusSnapshotReader.BuildEnabledHeadNumbers(settings, headCount);
            var cards = new List<ST_MANUAL_HEAD_CARD>(headCount);
            for (var headNo = 1; headNo <= headCount; headNo++)
            {
                var automationNo = ReadManualHeadAutomationNo(settings, headNo);
                cards.Add(enabledHeadNos.Contains(headNo) ? Head(headNo, selectedHeadNo, automationNo, settings, selectedScannerAxes) : new ST_MANUAL_HEAD_CARD(headNo, $"H{headNo:00}", "X NOT USE", "Y NOT USE", "Z NOT USE", "NOT USE", enabledHeadNos.Count == 0 && headNo == selectedHeadNo, false, automationNo));
            }

            return cards;
        }

        private static int NormalizeManualScannerHeadNo(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int selectedHeadNo)
        {
            var enabledHeadNos = CScannerStatusSnapshotReader.BuildEnabledHeadNumbers(settings);
            return enabledHeadNos.Count == 0 || enabledHeadNos.Contains(selectedHeadNo) ? selectedHeadNo : enabledHeadNos[0];
        }

        private static IReadOnlyList<ST_MANUAL_HEAD_GROUP> BuildHeadGroups(IReadOnlyList<ST_MANUAL_HEAD_CARD> headCards)
        {
            var groupedHeads = new SortedDictionary<int, List<ST_MANUAL_HEAD_CARD>>();
            for (var index = 0; index < headCards.Count; index++)
            {
                var head = headCards[index];
                if (!groupedHeads.TryGetValue(head.AutomationNo, out var heads))
                {
                    heads = [];
                    groupedHeads.Add(head.AutomationNo, heads);
                }

                heads.Add(head);
            }

            var groups = new List<ST_MANUAL_HEAD_GROUP>(groupedHeads.Count);
            foreach (var item in groupedHeads)
            {
                groups.Add(new ST_MANUAL_HEAD_GROUP(item.Key, $"CONTROLLER {item.Key + 1}", item.Value));
            }

            return groups;
        }

        private static ST_MANUAL_HEAD_CARD Head(int headNo, int selectedHeadNo, int automationNo, IReadOnlyList<ST_SYSTEM_PARAMETER> settings, IReadOnlyList<ST_MANUAL_SCANNER_AXIS> selectedScannerAxes)
        {
            var definitions = CScannerStatusSnapshotReader.BuildAxisDefinitions(settings, headNo);
            var xSummary = FormatHeadAxisSummary(headNo, "X", definitions, selectedScannerAxes);
            var ySummary = FormatHeadAxisSummary(headNo, "Y", definitions, selectedScannerAxes);
            var zSummary = FormatHeadAxisSummary(headNo, "Z", definitions, selectedScannerAxes);
            var state = headNo == selectedHeadNo ? FormatManualHeadState(selectedScannerAxes) : "Ready";
            return new ST_MANUAL_HEAD_CARD(headNo, $"H{headNo:00}", xSummary, ySummary, zSummary, state, headNo == selectedHeadNo, true, automationNo);
        }

        private static string FormatHeadAxisSummary(int headNo, string axisName, IReadOnlyList<ST_SCANNER_AXIS_SETTING> definitions, IReadOnlyList<ST_MANUAL_SCANNER_AXIS> selectedScannerAxes)
        {
            foreach (var axis in selectedScannerAxes)
            {
                if (axis.HeadNo == headNo && axis.AxisRole.Equals(axisName, StringComparison.OrdinalIgnoreCase))
                {
                    if (IsManualScannerAxisNotUsed(axis))
                    {
                        return $"{axisName} NOT USE";
                    }

                    return $"{axisName} {axis.AxisReference} {axis.CurrentPosition}";
                }
            }

            foreach (var definition in definitions)
            {
                if (definition.AxisName.Equals(axisName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!definition.IsUsed)
                    {
                        return $"{axisName} NOT USE";
                    }

                    return $"{axisName} {definition.AxisReference}";
                }
            }

            return $"{axisName} -";
        }

        private static string FormatManualHeadState(IReadOnlyList<ST_MANUAL_SCANNER_AXIS> axes)
        {
            var hasError = false;
            var allReady = axes.Count > 0;
            foreach (var axis in axes)
            {
                if (IsManualScannerAxisNotUsed(axis))
                {
                    continue;
                }

                if (axis.Error.Equals("ERROR", StringComparison.OrdinalIgnoreCase) || axis.Error.Equals("OFFLINE", StringComparison.OrdinalIgnoreCase))
                {
                    hasError = true;
                }

                if (!axis.Enable.Equals("ENABLE", StringComparison.OrdinalIgnoreCase) || !axis.Home.Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    allReady = false;
                }
            }

            if (hasError)
            {
                return "Error";
            }

            return allReady ? "Ready" : "Wait";
        }

        private static bool IsManualScannerAxisNotUsed(ST_MANUAL_SCANNER_AXIS axis)
        {
            return axis.AxisReference.Equals("Z NOT USE", StringComparison.OrdinalIgnoreCase) || axis.Enable.Equals("NOT USE", StringComparison.OrdinalIgnoreCase);
        }
#endregion
    }

    public sealed class CManualMofAlignPanel : INotifyPropertyChanged
    {
        public const string RequestId = "PROCESS_DO_ALIGN";
        public const string AvailableId = "SDC_DO_ALIGN_STATUS";
        public const string CompleteId = "SDC_DO_ALIGN_COMPLETE";
        private readonly CMelsec _melsec;
        private readonly Func<bool> _confirm;
        private readonly Action<string> _warning;
        private readonly Action<string> _status;
        private readonly Func<bool> _isBlocked;
        private readonly Action<Drilling.Common.Alarm.CSequenceFailureException>? _reportFailure;
        private bool _busy;
        public CManualMofAlignPanel(CMelsec melsec, Func<bool> confirm, Action<string> warning, Action<string> status, Func<bool> isBlocked,
            Action<Drilling.Common.Alarm.CSequenceFailureException>? reportFailure = null)
        {
            (_melsec, _confirm, _warning, _status, _isBlocked) = (melsec, confirm, warning, status, isBlocked);
            _reportFailure = reportFailure;
            RequestCommand = new CButtonCommand(async _ => await RequestAsync(), _ => CanRequest);
            CancelRequestCommand = new CButtonCommand(async _ => await CancelRequestAsync(), _ => !_busy);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public CButtonCommand RequestCommand { get; }
        public CButtonCommand CancelRequestCommand { get; }
        public ST_MANUAL_MOF_ALIGN_STATE Availability { get; private set; } = Pending;
        public ST_MANUAL_MOF_ALIGN_STATE Completion { get; private set; } = Pending;
        public ST_MANUAL_MOF_ALIGN_STATE Request { get; private set; } = Pending;
        public bool IsRequested => Request.IsActive;
        public bool CanRequest => !_busy && Availability.IsActive;
        private static ST_MANUAL_MOF_ALIGN_STATE Pending => new("대기", "MELSEC 수신 대기 중입니다.");

        // Display shared readback only. These status boxes do not infer Stage completion from our write.
        public void UpdateSnapshot(IReadOnlyList<ST_MELSEC_MAP_DATA> map, IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE> snapshot)
        {
            var available = Read(AvailableId, "가능", "불가능");
            var complete = Read(CompleteId, "완료", "미완료");
            var request = Read(RequestId, "ON", "OFF");
            if (Availability == available && Completion == complete && Request == request)
                return;
            var couldRequest = CanRequest;
            (Availability, Completion, Request) = (available, complete, request);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            if (couldRequest != CanRequest)
                RequestCommand.NotifyCanExecuteChanged();
            ST_MANUAL_MOF_ALIGN_STATE Read(string id, string onText, string offText)
            {
                var row = map.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (row is not { Use: true } || row.Access == EN_MELSEC_ACCESS.Write || row.PollMs <= 0)
                    return new("미설정", "읽기용 MELSEC 맵이 설정되어 있지 않습니다.");
                if (!snapshot.TryGetValue(id, out var sample))
                    return Pending;
                var message = $"{row.Address} · {CMelsecCachePresentation.Describe(sample)}";
                if (!sample.IsValid)
                    return new(sample.State switch
                    {
                        EN_MELSEC_CACHE_STATE.Pending => "대기",
                        EN_MELSEC_CACHE_STATE.Offline => "OFFLINE",
                        EN_MELSEC_CACHE_STATE.Stale => "STALE",
                        _ => "ERR"
                    }, message);
                var value = CMelsecCachePresentation.NumericValue(sample.Value);
                // Every numeric cache value already includes READ_SCALE from the shared collector.
                value = row.DataType switch
                {
                    EN_MELSEC_DATA_TYPE.Word or EN_MELSEC_DATA_TYPE.DWord or EN_MELSEC_DATA_TYPE.Double or EN_MELSEC_DATA_TYPE.Float => value,
                    _ => double.NaN
                };
                if (!double.IsFinite(value))
                    return new("ERR", message + " · 상태값 형식 오류");
                var active = value == 1;
                var text = id == RequestId && !active && value != 0 ? "UNKNOWN" : active ? onText : offText;
                return new(text, message + " · 스케일 적용값: " + value.ToString("G", CultureInfo.InvariantCulture), active);
            }
        }

        public async Task RequestAsync()
        {
            if (_busy || _isBlocked())
                return;
            RefreshSnapshot();
            if (!Availability.IsActive)
                return;
            SetBusy(true);
            try
            {
                if (!_confirm() || _isBlocked())
                    return;
                // A modal confirmation can remain open while the Stage becomes unavailable.
                RefreshSnapshot();
                if (!Availability.IsActive)
                {
                    const string message = "현재 ALIGN이 불가능한 상태입니다. ALIGN 가능 상태를 확인해 주세요.";
                    _status(message);
                    _warning(message);
                    return;
                }

                // WriteWord serializes the complete two-word Int32 through the existing MELSEC worker.
                await Task.Run(() => _melsec.WriteWord(RequestId, 1));
                _status("STAGE PC에 얼라인을 요청했습니다.");
            }
            catch (Exception exception)
            {
                var message = $"STAGE PC 얼라인 요청 실패: {exception.Message}";
                Drilling.Common.Alarm.CSequenceFailureException.Signal("DO_ALIGN", RequestId, exception is TimeoutException or OperationCanceledException, exception).Publish(_reportFailure);
                _status(message);
                _warning(message);
            }
            finally
            {
                RefreshSnapshot();
                SetBusy(false);
            }
        }

        public async Task CancelRequestAsync()
        {
            if (_busy || _isBlocked())
                return;
            // Clearing our request must remain possible when the Stage is no longer available.
            SetBusy(true);
            try
            {
                await Task.Run(() => _melsec.WriteWord(RequestId, 0));
                _status("STAGE PC 얼라인 요청을 해제했습니다.");
            }
            catch (Exception exception)
            {
                var message = $"STAGE PC 얼라인 요청 해제 실패: {exception.Message}";
                Drilling.Common.Alarm.CSequenceFailureException.Signal("DO_ALIGN", RequestId, exception is TimeoutException or OperationCanceledException, exception).Publish(_reportFailure);
                _status(message);
                _warning(message);
            }
            finally
            {
                RefreshSnapshot();
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            RequestCommand.NotifyCanExecuteChanged();
            CancelRequestCommand.NotifyCanExecuteChanged();
        }

        private void RefreshSnapshot()
        {
            var snapshot = _melsec.GetCachedSnapshot(out var map);
            UpdateSnapshot(map, snapshot);
        }
    }

    public sealed record ST_MANUAL_MOF_ALIGN_STATE(string Text, string Message, bool IsActive = false);
    // A view-only model that follows Manual Head Select without reading devices.
    public sealed class CManualMofPreviewPanel : INotifyPropertyChanged
    {
        private readonly HashSet<int> _selectedHeadNos = [];
        private IReadOnlyDictionary<string, string> _parameters = new Dictionary<string, string>();
        private ST_PREVIEW_HEAD_LAYOUT? _layout;
        private IReadOnlyList<int> _enabledHeadNos = [];
        public event PropertyChangedEventHandler? PropertyChanged;
        public IReadOnlyList<ST_HEAD_PREVIEW> OddHeadPreviews { get; private set; } = [];
        public IReadOnlyList<ST_HEAD_PREVIEW> EvenHeadPreviews { get; private set; } = [];
        public IReadOnlyList<ST_HEAD_ASSIGNMENT_AREA> HeadAssignmentAreas { get; private set; } = [];
        public ST_GLASS_PREVIEW_FRAME GlassFrame { get; private set; } = new(44, 62, 772, 420);
        // Fit the Glass and its key/head labels, rather than the empty 860 x 520 drawing surface.
        // All geometry retains its original design coordinates; only the displayed crop changes.
        public double PreviewContentWidth => GlassFrame.Width + 48;
        public double PreviewContentHeight => GlassFrame.Height + 56;
        public double PreviewContentLeft => 24 - GlassFrame.CanvasLeft;
        public double PreviewContentTop => 32 - GlassFrame.CanvasTop;
        public string GlassPreviewSummary { get; private set; } = "No recipe loaded";
        public ImageSource? RecipePreviewImage { get; private set; }
        public IReadOnlyList<ST_CELL_PREVIEW_LABEL> CellPreviewLabels { get; private set; } = [];

        public void Update(ST_RECIPE_DEFINITION? recipe, IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (recipe is not null)
                foreach (var parameter in recipe.Parameters.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
                    parameters[parameter.Key] = parameter.Value;
            foreach (var setting in settings.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
                parameters[setting.Key] = setting.Value;
            var layout = CHeadAssignmentPreviewBuilder.BuildHeadLayout(settings);
            if (_layout is not null && _layout.Fields.SequenceEqual(layout.Fields) && _parameters.Count == parameters.Count && parameters.All(item => _parameters.TryGetValue(item.Key, out var value) && value == item.Value))
                return;
            _parameters = parameters;
            _layout = layout;
            _enabledHeadNos = CHeadAssignmentPreviewBuilder.BuildEnabledHeadNumbers(parameters);
            Rebuild();
        }

        public void SetSelectedHeads(IEnumerable<int> headNos)
        {
            var selection = headNos.Where(headNo => headNo is >= 1 and <= 8).ToHashSet();
            if (_selectedHeadNos.SetEquals(selection))
                return;
            _selectedHeadNos.Clear();
            _selectedHeadNos.UnionWith(selection);
            Rebuild();
        }

        private void Rebuild()
        {
            if (_layout is null)
                return;
            var selectedHeadNos = _selectedHeadNos.Intersect(_enabledHeadNos).ToHashSet();
            var preview = CHeadAssignmentPreviewBuilder.BuildRecipePreview(_parameters, selectedHeadNos, _layout);
            GlassFrame = preview.Frame;
            RecipePreviewImage = preview.Image;
            CellPreviewLabels = preview.CellLabels;
            HeadAssignmentAreas = CHeadAssignmentPreviewBuilder.BuildHeadAssignmentMap(_parameters, selectedHeadNos, _layout).Areas.Select(area => area with { PointCount = (int)Math.Min(int.MaxValue, preview.HeadPointCounts.GetValueOrDefault(area.HeadNo)) }).ToArray();
            GlassPreviewSummary = string.IsNullOrWhiteSpace(preview.ValidationMessage) ? $"{HeadAssignmentAreas.Count}H / {preview.TotalPointCount:N0}P / {CHeadAssignmentPreviewBuilder.FormatGlassSizeText(_parameters)}" + (preview.UnassignedPointCount > 0 ? $" / U:{preview.UnassignedPointCount:N0}" : "") : preview.ValidationMessage;
            var heads = Enumerable.Range(1, 8).Select(headNo => new ST_HEAD_PREVIEW(headNo, $"HEAD {headNo:00}", "IDLE", selectedHeadNos.Contains(headNo), _enabledHeadNos.Contains(headNo)) { AssignedShotCount = preview.HeadPointCounts.GetValueOrDefault(headNo) }).ToArray();
            OddHeadPreviews = heads.Where(head => head.HeadNo % 2 == 1).ToArray();
            EvenHeadPreviews = heads.Where(head => head.HeadNo % 2 == 0).ToArray();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    public sealed class CManualMofRunPanel : INotifyPropertyChanged
    {
        private readonly Func<IReadOnlyDictionary<string, string>, IReadOnlySet<int>, IReadOnlyList<global::Drilling.Common.InterLock.ST_INTERLOCK_ITEM>>? _readSnapshot;
        private readonly Action<ST_INTERLOCK_GROUP> _showList;
        private readonly Action<string> _status;
        private readonly Func<ST_MANUAL_MOF_STATUS>? _execution;
        private bool _requesting;
        public bool IsRunning { get; private set; }
        public bool RequiresStop { get; private set; }
        public bool IsBusy => _requesting || IsRunning;
        public string Message { get; private set; } = "";
        public string ScriptDirectory { get; private set; } = "";
        public IReadOnlyList<CManualMofActionCard> Actions { get; } = CManualMofActionIds.All.Select((id, index) => new CManualMofActionCard(id, $"{index + 1}. " + new[] { "Prepare Data", "Build Script", "Script Upload", "MOF ZeroSet", "Run Task / Ready", "Stage Move / MOF", "Complete" }[index])).ToArray();

        private IReadOnlyDictionary<string, string> _parameters = new Dictionary<string, string>();
        private HashSet<int> _selectedHeads = [];
        private IReadOnlyList<global::Drilling.Common.InterLock.ST_INTERLOCK_ITEM> _items = [];
        private bool _hasSnapshot;
        public CManualMofRunPanel(
            Func<IReadOnlyDictionary<string, string>, IReadOnlySet<int>, IReadOnlyList<global::Drilling.Common.InterLock.ST_INTERLOCK_ITEM>>? readSnapshot = null, 
            Action<ST_INTERLOCK_GROUP>? showList = null, 
            Action<string>? status = null, 
            Action? run = null, 
            Action? stop = null, 
            Func<ST_MANUAL_MOF_STATUS>? execution = null
        )
        {
            _readSnapshot = readSnapshot;
            _showList = showList ?? (_ =>
            {
            });
            _status = status ?? (_ =>
            {
            });
            InterlockListCommand = new(_ => OpenList());
            _execution = execution;
            RunCommand = new(_ =>
            {
                if (CanRun)
                    run?.Invoke();
            }, _ => CanRun);
            StopCommand = new(_ => stop?.Invoke(), _ => IsRunning || RequiresStop);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public CButtonCommand InterlockListCommand { get; }
        public CButtonCommand RunCommand { get; }
        public CButtonCommand StopCommand { get; }
        public string State => _selectedHeads.Count == 0 ? "HEAD 미선택" : !_hasSnapshot ? "대기" : _items.Count > 0 && _items.All(CMainInterlockGroups.IsOk) ? "OK" : "NOT OK";
        public Brush StateBackground => State == "OK" ? CStatusBrush.Online : State == "NOT OK" ? CStatusBrush.Offline : CStatusBrush.Wait;
        public bool CanRun => State == "OK" && !IsBusy && !RequiresStop;
        public string SelectionText => _selectedHeads.Count == 0 ? "Head Select에서 Head를 선택해 주세요." : "선택 Head: " + string.Join(", ", _selectedHeads.Order().Select(head => head.ToString("00"))) + " / 공통 안전 항목 포함";
        public IReadOnlyList<ST_INTERLOCK_DETAIL> Details => _items.Select(item => new ST_INTERLOCK_DETAIL(item.HeadNo is> 0 ? $"HEAD {item.HeadNo:00} / {item.Signal}" : item.Signal, CMainInterlockGroups.IsOk(item), item.State, item.Detail)).ToArray();

        public void SetInputs(ST_RECIPE_DEFINITION? recipe, IReadOnlyList<ST_SYSTEM_PARAMETER> settings, IEnumerable<int> selectedHeads)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (recipe is not null)
                foreach (var parameter in recipe.Parameters.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
                    parameters[parameter.Key] = parameter.Value;
            foreach (var setting in settings.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
                parameters[setting.Key] = setting.Value;
            if (_parameters.Count != parameters.Count || parameters.Any(item => !_parameters.TryGetValue(item.Key, out var value) || value != item.Value))
            {
                _parameters = parameters;
                _hasSnapshot = false;
            }

            SetSelectedHeads(selectedHeads);
            Notify();
        }

        public void SetSelectedHeads(IEnumerable<int> heads)
        {
            var selected = heads.Where(head => head is >= 1 and <= 8).ToHashSet();
            if (_selectedHeads.SetEquals(selected))
                return;
            _selectedHeads = selected;
            _items = _items.Where(item => item.HeadNo is not > 0 || selected.Contains(item.HeadNo.Value)).ToArray();
            _hasSnapshot = false;
            Notify();
        }

        public void SetRequesting(bool requesting)
        {
            _requesting = requesting;
            RefreshExecution();
            Notify();
        }

        public void RefreshExecution()
        {
            if (_execution is null)
                return;
            var snapshot = _execution();
            var changed = IsRunning != snapshot.IsRunning || RequiresStop != snapshot.RequiresStop || Message != snapshot.Message || ScriptDirectory != snapshot.ScriptDirectory;
            IsRunning = snapshot.IsRunning;
            RequiresStop = snapshot.RequiresStop;
            Message = snapshot.Message;
            ScriptDirectory = snapshot.ScriptDirectory;
            foreach (var card in Actions)
                card.Update(snapshot.Actions.GetValueOrDefault(card.Id));
            if (changed)
                Notify();
        }

        public void Refresh()
        {
            RefreshExecution();
            if (_readSnapshot is null)
                return;
            IReadOnlyList<global::Drilling.Common.InterLock.ST_INTERLOCK_ITEM> items;
            try
            {
                items = _readSnapshot(_parameters, _selectedHeads).Where(item => item.HeadNo is not > 0 || _selectedHeads.Contains(item.HeadNo.Value)).ToArray();
                if (items.Count == 0 && _selectedHeads.Count > 0)
                    items = [new("Interlock", EN_INTERLOCK_LEVEL.Error, "NO DATA", "인터락 상태를 확인할 수 없습니다.")
                    {
                        BlocksAutoRun = true
                    }

                    ];
            }
            catch (Exception exception)
            {
                items = _items.Count > 0 ? _items.Select(item => item with { Level = EN_INTERLOCK_LEVEL.Error, State = "READ FAIL", Detail = exception.Message, BlocksAutoRun = true }).ToArray() : [new("Interlock", EN_INTERLOCK_LEVEL.Error, "READ FAIL", exception.Message)
                {
                    BlocksAutoRun = true
                }

                ];
            }

            if (_hasSnapshot && _items.SequenceEqual(items))
                return;
            _items = items;
            _hasSnapshot = true;
            Notify();
        }

        private void OpenList()
        {
            Refresh();
            _showList(new("MOF Interlock", Details));
        }

        private void Notify()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            RunCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        }
    }

    public sealed class CManualMofActionCard(string id, string title) : INotifyPropertyChanged
    {
        private ST_SEQUENCE_ACTION_STATUS? _status;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Id { get; } = id;
        public string Title { get; } = title;
        public bool IsRunning => _status?.State == EN_SEQUENCE_ACTION_STATE.Running;
        public string Detail => _status?.Detail ?? "";
        public string State => (_status?.State ?? EN_SEQUENCE_ACTION_STATE.Waiting) switch
        {
            EN_SEQUENCE_ACTION_STATE.Running => "RUNNING",
            EN_SEQUENCE_ACTION_STATE.Completed => "DONE",
            EN_SEQUENCE_ACTION_STATE.Error => "ERROR",
            EN_SEQUENCE_ACTION_STATE.Stopped => "STOPPED",
            _ => "WAIT"
        };
        public Brush Background => State switch
        {
            "DONE" or "RUNNING" => Brushes.ForestGreen,
            "ERROR" => Brushes.Firebrick,
            "STOPPED" => Brushes.SlateGray,
            _ => Brushes.Goldenrod
        };

        public void Update(ST_SEQUENCE_ACTION_STATUS? status)
        {
            if (_status == status)
                return;
            _status = status;
            PropertyChanged?.Invoke(this, new(null));
        }
    }

    public sealed partial class CManualMofStagePanel : INotifyPropertyChanged
    {
#region 공유 상태·초기값
        private readonly Func<EN_COMM_STATE> _readConnectionState;
        private readonly Func<CManualStageMotionSimulation>? _simulation;
        private readonly Action _refresh;
        private readonly Func<string, bool> _confirm;
        private readonly Action<string> _warn;
        private readonly Action<string> _status;
        private readonly Func<bool> _isBlocked;
        private readonly CManualStageMoveOperation? _stageMove;
        private EN_COMM_STATE _connectionState;
        private bool _confirming;
        private int _generation;
        private double? _targetPosition;
        private double? _targetSpeed;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string TargetPosition { get; private set; } = "미설정";
        public string TargetSpeed { get; private set; } = "미설정";
        public string TargetSpeedSource { get; private set; } = "선택된 레시피 없음";
        public ST_MANUAL_MOF_STAGE_VALUE CurrentPosition { get; private set; } = Missing;
        public ST_MANUAL_MOF_STAGE_VALUE CurrentSpeed { get; private set; } = Missing;
        public ST_MANUAL_MOF_STAGE_VALUE Busy { get; private set; } = Missing;
        public ST_MANUAL_MOF_STAGE_VALUE AlarmCode { get; private set; } = Missing;
        public ST_MANUAL_MOF_STAGE_VALUE WarningCode { get; private set; } = Missing;

#endregion
#region 생성·초기화
        public CManualMofStagePanel(
            Func<EN_COMM_STATE>? readConnectionState = null, 
            Func<CManualStageMotionSimulation>? simulation = null, 
            Action? refresh = null, 
            Func<string, bool>? confirm = null, 
            Action<string>? warn = null, 
            Action<string>? status = null, 
            Func<bool>? isBlocked = null,
            CManualStageMoveOperation? stageMove = null
        )
        {
            _readConnectionState = readConnectionState ?? (() => EN_COMM_STATE.Offline);
            _simulation = simulation;
            _refresh = refresh ?? (() =>
            {
            });
            _confirm = confirm ?? (_ => false);
            _warn = warn ?? (_ =>
            {
            });
            _status = status ?? (_ =>
            {
            });
            _isBlocked = isBlocked ?? (() => false);
            _stageMove = stageMove;
            MoveCommand = new(async _ => await Move(), _ => CanMove);
            StopCommand = new(_ => Stop(), _ => CanStop);
            if (_stageMove is not null) _stageMove.Changed += NotifyStateChanged;
        }

#endregion
#region 기본 처리
        public bool? IsAtTarget => CurrentPosition.Value is { } position && _targetPosition is { } target && Math.Abs(position) < (double)decimal.MaxValue && Math.Abs(target) < (double)decimal.MaxValue ? Math.Abs(position - target) <= 1 && Math.Abs((decimal)position - (decimal)target) <= 0.03m : null;
        public string PositionComparison => IsAtTarget switch
        {
            true => "위치 일치 (±0.03 mm)",
            false => "위치 불일치 (±0.03 mm)",
            _ => "위치 확인 대기"
        };
        private static ST_MANUAL_MOF_STAGE_VALUE Missing => new("미설정", "MELSEC 맵이 미설정 또는 사용 해제 상태입니다.");

        public void UpdateTargets(ST_RECIPE_DEFINITION? recipe, IReadOnlyList<ST_SYSTEM_PARAMETER> motorSettings)
        {
            _targetPosition = ParseTarget(motorSettings.LastOrDefault(item => string.Equals(item.Key, "STAGE_START_POS_Y", StringComparison.OrdinalIgnoreCase))?.Value);
            _targetSpeed = ParseTarget(recipe?.Parameters.LastOrDefault(item => string.Equals(item.Key, "STAGE_SPEED", StringComparison.OrdinalIgnoreCase))?.Value);
            TargetPosition = FormatTarget(_targetPosition);
            TargetSpeed = FormatTarget(_targetSpeed);
            TargetSpeedSource = recipe is null ? "선택된 레시피 없음" : $"Recipe: {recipe.Name} / Stage Speed";
            NotifyStateChanged();
        }

        public void UpdateSnapshot(IReadOnlyList<ST_MELSEC_MAP_DATA> map, IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE> snapshot)
        {
            var position = Read("MOTOR_STAGE_Y_CUR_POS", ValueKind.Number);
            var speed = Read("MOTOR_STAGE_Y_CUR_SPEED", ValueKind.Number);
            var busy = Read("MOTOR_STAGE_Y_BUSY", ValueKind.Flag);
            var alarm = Read("MOTOR_STAGE_Y_SERVO_ALARM_CODE", ValueKind.Code);
            var warning = Read("MOTOR_STAGE_Y_SERVO_WARNING_CODE", ValueKind.Code);
            var connection = _readConnectionState();
            if (CurrentPosition == position && CurrentSpeed == speed && Busy == busy && AlarmCode == alarm && WarningCode == warning && _connectionState == connection)
                return;
            _connectionState = connection;
            (CurrentPosition, CurrentSpeed, Busy, AlarmCode, WarningCode) = (position, speed, busy, alarm, warning);
            NotifyStateChanged();
            ST_MANUAL_MOF_STAGE_VALUE Read(string id, ValueKind kind)
            {
                var row = map.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (row is null || !row.Use)
                    return Missing;
                if (row.Access == EN_MELSEC_ACCESS.Write || row.PollMs <= 0)
                    return new("미설정", "읽기용 주기 폴링이 설정되어 있지 않습니다.");
                if (!snapshot.TryGetValue(id, out var cached))
                    return new("대기", "MELSEC 수신 대기 중입니다.");
                var message = $"{row.Address} · {CMelsecCachePresentation.Describe(cached)}";
                if (!cached.IsValid)
                    return new(cached.State switch
                    {
                        EN_MELSEC_CACHE_STATE.Pending => "대기",
                        EN_MELSEC_CACHE_STATE.Offline => "OFFLINE",
                        EN_MELSEC_CACHE_STATE.Stale => "STALE",
                        _ => "ERR"
                    }, message);
                var value = CMelsecCachePresentation.NumericValue(cached.Value);
                value = row.DataType switch
                {
                    EN_MELSEC_DATA_TYPE.Word or EN_MELSEC_DATA_TYPE.DWord or EN_MELSEC_DATA_TYPE.Double or EN_MELSEC_DATA_TYPE.Float => value,
                    _ => double.NaN
                };
                if (!double.IsFinite(value))
                    return new("ERR", message + " · 숫자 형식 오류");
                return kind switch
                {
                    ValueKind.Flag when value is 0 or 1 => new(value == 1 ? "ON" : "OFF", message, Value: value),
                    ValueKind.Flag => new("UNKNOWN", message + $" · Busy 값: {value}"),
                    ValueKind.Code when value >= 0 && value == Math.Truncate(value) => new(value.ToString("0", CultureInfo.InvariantCulture), message, value != 0, value),
                    ValueKind.Code => new("ERR", message + " · 상태 코드 형식 오류"),
                    _ => new(value.ToString("F3", CultureInfo.InvariantCulture), message, Value: value)};
            }
        }

        private static double? ParseTarget(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
        private static string FormatTarget(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "미설정";
        private void NotifyStateChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            MoveCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        }

        private enum ValueKind
        {
            Number,
            Flag,
            Code
        }

#endregion
#region Commands
        public CButtonCommand MoveCommand { get; }
        public CButtonCommand StopCommand { get; }
        private bool IsSimulation => _readConnectionState() == EN_COMM_STATE.Simulation;
        private bool HasMoveConnection => IsSimulation && _simulation is not null || _readConnectionState() == EN_COMM_STATE.Online && _stageMove is not null;
        public bool CanStop => _stageMove?.CanStop == true || IsSimulation && _simulation is not null && !_simulation().IsReserved;
        public bool CanMove => HasMoveConnection && !_confirming && _stageMove?.CanStop != true && Busy.Value == 0
            && CurrentPosition.Value.HasValue && _targetPosition.HasValue && _targetSpeed is> 0
            && (!IsSimulation || !_simulation!().IsReserved && _simulation().GetSnapshot().Single(axis => axis.Axis == EN_MANUAL_STAGE_AXIS.StageY).Speed == 0);
        public string MotionMode => _connectionState switch
        {
            EN_COMM_STATE.Simulation => "SIMULATION",
            EN_COMM_STATE.Online => _stageMove?.IsStopping == true ? "STOPPING · 신호 정리 대기" : _stageMove?.CanStop == true ? "Process Move 실행 중" : "MELSEC · Process Move",
            _ => "MELSEC OFFLINE"
        };
        public Brush ModeBackground => _connectionState switch
        {
            EN_COMM_STATE.Simulation => CStatusBrush.Simul,
            EN_COMM_STATE.Online => CStatusBrush.Online,
            _ => CStatusBrush.Offline
        };

        public async Task Move()
        {
            _refresh();
            if (!CanMove || _isBlocked())
                return;
            var target = _targetPosition!.Value;
            var speed = _targetSpeed!.Value;
            var generation = _generation;
            _confirming = true;
            NotifyStateChanged();
            try
            {
                if (!_confirm($"Stage Y를 READY POSITION으로 이동하시겠습니까?\n\n목표 위치: {target:F3} mm\n이동 속도: {speed:F3} mm/sec"))
                    return;
                _refresh();
                if (generation != _generation || _isBlocked())
                    return;
                // Recheck shared feedback and the common simulation model after the modal dialog.
                // Another Manual tab may have started motion while the dialog was open.
                if (!HasMoveConnection || _stageMove?.CanStop == true || Busy.Value != 0 || !CurrentPosition.Value.HasValue || _targetPosition != target || _targetSpeed != speed
                    || IsSimulation && (_simulation!().IsReserved || _simulation().GetSnapshot().Single(axis => axis.Axis == EN_MANUAL_STAGE_AXIS.StageY).Speed != 0))
                {
                    _warn("Stage 상태 또는 목표값이 변경되었습니다. 현재 상태를 확인한 뒤 다시 이동해 주세요.");
                    return;
                }

                if (_stageMove is not null)
                    await _stageMove.Move(EN_MANUAL_STAGE_AXIS.StageY, target, relative: false, speed: speed);
                else
                    _simulation!().Move(EN_MANUAL_STAGE_AXIS.StageY, target, relative: false, speed: speed);
                _status($"{(IsSimulation ? "SIMULATION" : "MELSEC")} · Stage Y READY POSITION 이동 완료: {target:F3} mm");
            }
            catch (OperationCanceledException)
            {
                _status("Stage Y 이동 중지 · 공통 시퀀스 정리 상태를 확인하세요.");
            }
            catch (Exception exception)
            {
                _warn($"Stage Y 이동 실패: {exception.Message}");
            }
            finally
            {
                _confirming = false;
                NotifyStateChanged();
            }
        }

        public void Stop()
        {
            _generation++;
            if (!CanStop)
                return;
            _stageMove?.Stop(includeSharedMove: true);
            if (IsSimulation && _stageMove?.IsRunning != true && !_simulation!().IsReserved)
                _simulation!().Stop(EN_MANUAL_STAGE_AXIS.StageY);
            _status("Stage Y STOP 요청 · 공통 이동 신호 정리 대기");
            _refresh();
            NotifyStateChanged();
        }
#endregion
    }

    public sealed record ST_MANUAL_MOF_STAGE_VALUE(string Text, string Message, bool IsActive = false, double? Value = null);
    public sealed class CManualScanAssignments : INotifyPropertyChanged
    {
        private readonly Dictionary<int, CManualScanAssignment> _headCache = [];
        private IReadOnlyList<string> _settingNames = [];
        public event PropertyChangedEventHandler? PropertyChanged;
        public IReadOnlyList<CManualScanAssignment> Heads { get; private set; } = [];

        public void Update(IReadOnlyList<int> heads, IReadOnlyList<string> settingNames, string defaultSettingName)
        {
            _settingNames = NormalizeNames(settingNames);
            // Hidden Heads retain their choices, but deletion applies to them as well.
            foreach (var row in _headCache.Values)
                row.UpdateAvailableSettings(_settingNames);
            var visible = new List<CManualScanAssignment>();
            foreach (var head in heads.Where(head => head > 0).Distinct().Order())
            {
                if (!_headCache.TryGetValue(head, out var row))
                {
                    row = new CManualScanAssignment(head, _settingNames, defaultSettingName);
                    _headCache.Add(head, row);
                }

                visible.Add(row);
            }

            if (Heads.SequenceEqual(visible))
                return;
            Heads = visible;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Heads)));
        }

        // Call after a successful file rename, before refreshing the file list.
        public void RenameSetting(string oldName, string newName)
        {
            var oldFile = NormalizeName(oldName);
            var newFile = NormalizeName(newName);
            if (oldFile.Length == 0 || newFile.Length == 0 || !_settingNames.Any(name => name.Equals(oldFile, StringComparison.OrdinalIgnoreCase)))
                return;
            _settingNames = NormalizeNames(_settingNames.Select(name => name.Equals(oldFile, StringComparison.OrdinalIgnoreCase) ? newFile : name).ToArray());
            foreach (var row in _headCache.Values)
                row.RenameSetting(oldFile, newFile, _settingNames);
        }

        public string GetSettingName(int head) => _headCache.TryGetValue(head, out var row) ? row.SelectedSettingName : "";
        private static IReadOnlyList<string> NormalizeNames(IReadOnlyList<string> names) => names.Select(NormalizeName).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        private static string NormalizeName(string? value)
        {
            var name = value?.Trim() ?? "";
            if (!name.EndsWith(".scan", StringComparison.OrdinalIgnoreCase) || name.Length <= 5 || name.Any(character => character < 32 || "<>:\"/\\|?*".Contains(character)))
                return "";
            var stem = name.Split('.')[0];
            if (stem.Length == 0 || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
                return "";
            return name;
        }
    }

    public sealed class CManualScanAssignment : INotifyPropertyChanged
    {
        private string _selectedSettingName;
        public event PropertyChangedEventHandler? PropertyChanged;
        public int HeadNo { get; }
        public string HeadName => $"HEAD {HeadNo:00}";
        public IReadOnlyList<string> SettingNames { get; private set; }

        public string SelectedSettingName
        {
            get => _selectedSettingName;
            set
            {
                // ComboBox can write null while its ItemsSource is replaced. Only a
                // choice from the complete current file list can change assignment.
                var canonical = FindSetting(value);
                if (canonical is null || _selectedSettingName == canonical)
                    return;
                _selectedSettingName = canonical;
                Notify(nameof(SelectedSettingName));
            }
        }

        internal CManualScanAssignment(int headNo, IReadOnlyList<string> settingNames, string defaultSettingName)
        {
            HeadNo = headNo;
            SettingNames = settingNames;
            _selectedSettingName = FindSetting(defaultSettingName) ?? "";
        }

        internal void UpdateAvailableSettings(IReadOnlyList<string> names)
        {
            var previous = _selectedSettingName;
            var namesChanged = !SettingNames.SequenceEqual(names);
            SettingNames = names;
            _selectedSettingName = FindSetting(previous) ?? "";
            if (namesChanged)
                Notify(nameof(SettingNames));
            if (_selectedSettingName != previous)
                Notify(nameof(SelectedSettingName));
        }

        internal void RenameSetting(string oldName, string newName, IReadOnlyList<string> names)
        {
            var renamed = _selectedSettingName.Equals(oldName, StringComparison.OrdinalIgnoreCase);
            if (renamed)
                _selectedSettingName = newName;
            UpdateAvailableSettings(names);
            if (renamed)
                Notify(nameof(SelectedSettingName));
        }

        private string? FindSetting(string? name) => string.IsNullOrWhiteSpace(name) ? null : SettingNames.FirstOrDefault(candidate => candidate.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // Presentation adapter only: no timer, thread, or device reads.
    internal sealed class CManualScannerSnapshotReader(CAutomationManager automationManager)
    {
        private readonly object _snapshotLock = new();
        private IReadOnlyList<ST_MANUAL_SCANNER_AXIS> _snapshot = [];
        private IReadOnlyList<ST_SCANNER_AXIS_SETTING> _axisDefinitions = [];
        private int[] _selectedHeadNos = [];
        private long _selectionGeneration;
        public void SelectHead(int headNo, IReadOnlyList<ST_SYSTEM_PARAMETER> settings) => SelectHeads([headNo], settings);
        public void SelectHeads(IEnumerable<int> headNos, IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            var heads = headNos.Where(head => head is >= 1 and <= 8).Distinct().Order().ToArray();
            var axisDefinitions = heads.SelectMany(head => CScannerStatusSnapshotReader.BuildAxisDefinitions(settings, head)).ToArray();
            lock (_snapshotLock)
            {
                if (_selectedHeadNos.SequenceEqual(heads) && IsSameAxisDefinitions(_axisDefinitions, axisDefinitions))
                {
                    return;
                }

                _selectedHeadNos = heads;
                _selectionGeneration++;
                _axisDefinitions = axisDefinitions;
                _snapshot = [];
            }
        }

        public IReadOnlyList<ST_MANUAL_SCANNER_AXIS> GetSnapshot()
        {
            RefreshSnapshot(CancellationToken.None);
            lock (_snapshotLock)
                return _snapshot;
        }

        internal static IReadOnlyList<ST_MANUAL_SCANNER_AXIS> BuildFallbackAxes(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int selectedHeadNo)
        {
            return BuildFallbackAxes(settings, selectedHeadNo, "LOADING", "Waiting for shared Automation snapshot...");
        }

        private static IReadOnlyList<ST_MANUAL_SCANNER_AXIS> BuildFallbackAxes(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int selectedHeadNo, string state, string detail)
        {
            var definitions = CScannerStatusSnapshotReader.BuildAxisDefinitions(settings, selectedHeadNo);
            var axes = new List<ST_MANUAL_SCANNER_AXIS>(definitions.Count);
            foreach (var definition in definitions)
            {
                var automationLabel = $"#{definition.AutomationNo.ToString(CultureInfo.InvariantCulture)}";
                if (!definition.IsUsed)
                {
                    axes.Add(new ST_MANUAL_SCANNER_AXIS(definition.HeadNo, $"H{definition.HeadNo:00}", automationLabel, definition.AutomationNo, definition.AxisName, "Z NOT USE", "NOT USE", "-", "-", "-", "NOT USE", "Scanner Z axis is not used."));
                    continue;
                }

                var axisReference = definition.AxisReference.Trim();
                axes.Add(new ST_MANUAL_SCANNER_AXIS(definition.HeadNo, $"H{definition.HeadNo:00}", automationLabel, definition.AutomationNo, definition.AxisName, axisReference, state, "-", "-", "-", state, detail));
            }

            return axes;
        }

        private void RefreshSnapshot(CancellationToken cancellationToken)
        {
            int[] heads;
            IReadOnlyList<ST_SCANNER_AXIS_SETTING> definitions;
            long generation;
            lock (_snapshotLock)
            {
                heads = _selectedHeadNos;
                definitions = _axisDefinitions;
                generation = _selectionGeneration;
            }

            var axes = new List<ST_MANUAL_SCANNER_AXIS>();
            foreach (var head in heads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (var definition in definitions.Where(axis => axis.HeadNo == head))
                        axes.Add(BuildManualScannerAxis(definition, cancellationToken));
                }
                catch (Exception exception)when (exception is not OperationCanceledException)
                {
                    axes.AddRange(CreatePollingErrorSnapshot(head, exception.Message));
                }
            }

            lock (_snapshotLock)
            {
                if (_selectionGeneration == generation)
                    _snapshot = axes;
            }
        }

        private ST_MANUAL_SCANNER_AXIS BuildManualScannerAxis(ST_SCANNER_AXIS_SETTING axis, CancellationToken cancellationToken)
        {
            var axisReference = axis.AxisReference.Trim();
            var displayAxisReference = string.IsNullOrWhiteSpace(axisReference) ? "-" : axisReference;
            var automationLabel = $"#{axis.AutomationNo.ToString(CultureInfo.InvariantCulture)}";
            if (!axis.IsUsed)
            {
                return new ST_MANUAL_SCANNER_AXIS(axis.HeadNo, $"H{axis.HeadNo:00}", automationLabel, axis.AutomationNo, axis.AxisName, "Z NOT USE", "NOT USE", "-", "-", "-", "NOT USE", "Scanner Z axis is not used.");
            }

            if (!string.IsNullOrWhiteSpace(axis.ConfigurationError))
            {
                return new ST_MANUAL_SCANNER_AXIS(axis.HeadNo, $"H{axis.HeadNo:00}", automationLabel, axis.AutomationNo, axis.AxisName, displayAxisReference, "NOT CONFIGURED", "NO", "-", "-", "NOT CONFIGURED", axis.ConfigurationError);
            }

            try
            {
                if (!automationManager.IsSimul(axis.AutomationNo) && !automationManager.IsConnect(axis.AutomationNo))
                {
                    return new ST_MANUAL_SCANNER_AXIS(axis.HeadNo, $"H{axis.HeadNo:00}", automationLabel, axis.AutomationNo, axis.AxisName, displayAxisReference, "DISABLE", "NO", "-", "-", "OFFLINE", "Automation not connected.");
                }

                var snapshot = automationManager.StatusSnapshots.GetAxis(axisReference, axis.AutomationNo, axis.AioNo, axis.ReadExtendedStatus);
                if (snapshot.Value is not { } status)
                    return new ST_MANUAL_SCANNER_AXIS(axis.HeadNo, $"H{axis.HeadNo:00}", automationLabel, axis.AutomationNo, axis.AxisName, displayAxisReference, snapshot.State, "-", "-", "-", snapshot.State, snapshot.Detail);
                return CreateManualScannerAxis(axis, automationLabel, displayAxisReference, status);
            }
            catch (Exception exception)when (exception is not OperationCanceledException)
            {
                return new ST_MANUAL_SCANNER_AXIS(axis.HeadNo, $"H{axis.HeadNo:00}", automationLabel, axis.AutomationNo, axis.AxisName, displayAxisReference, "ERROR", "NO", "-", "-", "ERROR", exception.Message);
            }
        }

        private static ST_MANUAL_SCANNER_AXIS CreateManualScannerAxis(ST_SCANNER_AXIS_SETTING axis, string automationLabel, string axisReference, ST_AUTOMATION_AXIS_STATUS status)
        {
            return new ST_MANUAL_SCANNER_AXIS(
                axis.HeadNo, 
                $"H{axis.HeadNo:00}", 
                automationLabel, 
                axis.AutomationNo, 
                axis.AxisName, 
                axisReference, 
                status.Able ? "ENABLE" : "DISABLE", 
                status.HomeDone ? "OK" : "NO", 
                FormatScannerPosition(status.PositionFeedback), 
                FormatExtendedScannerPosition(status.AuxiliaryFeedback, axis.ReadExtendedStatus), 
                status.HasError ? "ERROR" : "OK", 
                string.IsNullOrWhiteSpace(status.RawResponse) ? "-" : status.RawResponse
            )
            {
                FaultMask = CAutomationAxisFault.ParseMask(status.Fault)
            };
        }

        private static IReadOnlyList<ST_MANUAL_SCANNER_AXIS> CreatePollingErrorSnapshot(int selectedHeadNo, string message)
        {
            var normalizedHeadNo = NormalizeHeadNo(selectedHeadNo);
            var headName = $"H{normalizedHeadNo:00}";
            var detail = string.IsNullOrWhiteSpace(message) ? "Manual scanner status polling failed." : message;
            return[new ST_MANUAL_SCANNER_AXIS(normalizedHeadNo, headName, "-", 0, "-", "-", "ERROR", "NO", "-", "-", "ERROR", detail)];
        }

        private static bool IsSameAxisDefinitions(IReadOnlyList<ST_SCANNER_AXIS_SETTING> left, IReadOnlyList<ST_SCANNER_AXIS_SETTING> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Count; index++)
            {
                if (!left[index].Equals(right[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static int NormalizeHeadNo(int headNo)
        {
            return Math.Clamp(headNo, 1, 8);
        }

        private static string FormatScannerPosition(double value)
        {
            if (!double.IsFinite(value))
            {
                return "-";
            }

            return value.ToString("F3", CultureInfo.InvariantCulture);
        }

        private static string FormatExtendedScannerPosition(double value, bool readExtendedStatus)
        {
            return readExtendedStatus ? FormatScannerPosition(value) : "-";
        }
    }

    internal sealed class CManualStageMotionPollingService(CMelsec melsec) : CtrlThread
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
        private readonly object _snapshotLock = new();
        private IReadOnlyDictionary<string, ST_MANUAL_STAGE_MOTION_VALUE> _snapshot = new Dictionary<string, ST_MANUAL_STAGE_MOTION_VALUE>(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<ST_MANUAL_STAGE_AXIS> _axes = [];
        public void SelectAxes(IReadOnlyList<ST_MANUAL_STAGE_AXIS> axes)
        {
            lock (_snapshotLock)
            {
                _axes = axes.ToArray();
            }
        }

        public void Start()
        {
            base.Start((int)PollInterval.TotalMilliseconds, "ManualStageMotionPolling");
        }

        public IReadOnlyDictionary<string, ST_MANUAL_STAGE_MOTION_VALUE> GetSnapshot()
        {
            lock (_snapshotLock)
            {
                return _snapshot;
            }
        }

        public override void Run()
        {
            IReadOnlyList<ST_MANUAL_STAGE_AXIS> axes;
            lock (_snapshotLock)
            {
                axes = _axes;
            }

            var cachedValues = melsec.GetCachedSnapshot(axes.Select(axis => axis.ReadPositionId).Where(id => !string.IsNullOrWhiteSpace(id)), out _);
            var values = new Dictionary<string, ST_MANUAL_STAGE_MOTION_VALUE>(StringComparer.OrdinalIgnoreCase);
            foreach (var axis in axes)
            {
                if (string.IsNullOrWhiteSpace(axis.ReadPositionId))
                {
                    continue;
                }

                if (!cachedValues.TryGetValue(axis.ReadPositionId, out var cached) || cached.State == EN_MELSEC_CACHE_STATE.Pending)
                {
                    values[axis.Key] = new ST_MANUAL_STAGE_MOTION_VALUE("-", "-", "WAIT", "Waiting for configured MELSEC feedback.");
                    continue;
                }

                // Every numeric cache type already includes READ_SCALE from the common collector.
                var position = CMelsecCachePresentation.NumericValue(cached.Value);
                if (!cached.IsValid || !double.IsFinite(position))
                {
                    values[axis.Key] = new ST_MANUAL_STAGE_MOTION_VALUE("ERR", "-", cached.IsValid ? "ERROR" : cached.State.ToString().ToUpperInvariant(), cached.IsValid ? $"{axis.ReadPositionId} has invalid numeric feedback." : CMelsecCachePresentation.Describe(cached));
                    continue;
                }

                values[axis.Key] = new ST_MANUAL_STAGE_MOTION_VALUE(FormatDouble(position), "-", "OK", CMelsecCachePresentation.Describe(cached));
            }

            lock (_snapshotLock)
            {
                _snapshot = values;
            }
        }

        private static string FormatDouble(double value)
        {
            return double.IsFinite(value) ? value.ToString("F3", CultureInfo.InvariantCulture) : "-";
        }
    }

    internal sealed record ST_MANUAL_STAGE_MOTION_VALUE(string Position, string Speed, string State, string Message);
    public sealed class ST_MANUAL_CONTROLLER_HEAD : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public int HeadNo { get; }
        public string HeadName => $"HEAD {HeadNo:00}";
        public IReadOnlyList<ST_MANUAL_SCANNER_AXIS> Axes { get; private set; } = [];
        public ST_MANUAL_SCANNER_AXIS? XAxis => Axes.FirstOrDefault(axis => axis.AxisRole == "X");
        public ST_MANUAL_SCANNER_AXIS? YAxis => Axes.FirstOrDefault(axis => axis.AxisRole == "Y");
        public ST_MANUAL_SCANNER_AXIS? ZAxis => Axes.FirstOrDefault(axis => axis.AxisRole == "Z");
        public string XCurrentPosition => FormatCurrentPosition(XAxis);
        public string YCurrentPosition => FormatCurrentPosition(YAxis);
        public string ZCurrentPosition => FormatCurrentPosition(ZAxis);

        private static string FormatCurrentPosition(ST_MANUAL_SCANNER_AXIS? axis) => axis is null ? "-" : axis.IsControlEnabled ? axis.CurrentPosition : "NOT USE";
        public ST_MANUAL_DIO_TARGET? DioTarget { get; private set; }
        public string DioAxisName => DioTarget?.AxisName ?? "-";
        public string DioNo => DioTarget?.DioNo.ToString() ?? "-";
        public string DioState { get; private set; } = "UNKNOWN";
        public string DioDetail { get; private set; } = "";
        public bool IsDioBusy { get; private set; }
        public bool CanControlDio => DioTarget is not null && !IsDioBusy;
        public Brush DioBackground => DioState == "ON" ? CStatusBrush.Online : DioState == "OFF" ? CStatusBrush.Offline : CStatusBrush.Muted;
        public CButtonCommand ReadDioCommand { get; }
        public CButtonCommand DioOnCommand { get; }
        public CButtonCommand DioOffCommand { get; }

        public ST_MANUAL_CONTROLLER_HEAD(int headNo, Func<bool?, Task> executeDio)
        {
            HeadNo = headNo;
            ReadDioCommand = new CButtonCommand(async _ => await executeDio(null), _ => CanControlDio);
            DioOnCommand = new CButtonCommand(async _ => await executeDio(true), _ => CanControlDio);
            DioOffCommand = new CButtonCommand(async _ => await executeDio(false), _ => CanControlDio);
        }

        public void SetAxes(IReadOnlyList<ST_MANUAL_SCANNER_AXIS> axes)
        {
            if (Axes.SequenceEqual(axes))
                return;
            Axes = axes;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Axes)));
            foreach (var property in new[]
            {
                nameof(XAxis),
                nameof(YAxis),
                nameof(ZAxis),
                nameof(XCurrentPosition),
                nameof(YCurrentPosition),
                nameof(ZCurrentPosition)
            }

            )
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }

        public void SetTarget(ST_MANUAL_DIO_TARGET? target, string error)
        {
            if (DioTarget == target && (target is not null || DioDetail == error))
                return;
            DioTarget = target;
            DioState = "UNKNOWN";
            DioDetail = error;
            NotifyDio();
        }

        public void SetDioState(string state, string detail)
        {
            DioState = state;
            DioDetail = detail;
            NotifyDio();
        }

        public void SetBusy(bool busy)
        {
            IsDioBusy = busy;
            NotifyDio();
        }

        private void NotifyDio()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            ReadDioCommand.NotifyCanExecuteChanged();
            DioOnCommand.NotifyCanExecuteChanged();
            DioOffCommand.NotifyCanExecuteChanged();
        }
    }

    public sealed record ST_MANUAL_HOME_SPEED_TARGET(int HeadNo, int Controller, string Role, string AxisName, bool IsUsed, string ConfigurationError);
    public sealed record ST_MANUAL_HOME_SPEED_HEAD(int HeadNo, IReadOnlyList<CManualHomeSpeedAxis> Axes)
    {
        public string HeadName => $"HEAD {HeadNo:00}";
    }

    /// <summary>Only displays shared snapshots. Device reads belong to the common collector.</summary>
    public sealed class CManualHomeSpeedPanel : INotifyPropertyChanged
    {
        private readonly Func<int, IReadOnlyList<string>, ST_AUTOMATION_STATUS_SNAPSHOT<IReadOnlyDictionary<string, double>>> _snapshot;
        private readonly Action<int> _requestRefresh;
        private readonly Func<ST_MANUAL_HOME_SPEED_TARGET, double, Task<double>> _save;
        private readonly Action<string> _warning;
        private readonly Action<string> _completed;
        private IReadOnlyList<ST_MANUAL_HOME_SPEED_TARGET> _targets = [];
        private readonly Dictionary<ST_MANUAL_HOME_SPEED_TARGET, CManualHomeSpeedAxis> _cache = [];
        private bool _active;
        public event PropertyChangedEventHandler? PropertyChanged;
        public IReadOnlyList<ST_MANUAL_HOME_SPEED_HEAD> Heads { get; private set; } = [];
        public CButtonCommand RefreshCommand { get; }

        public CManualHomeSpeedPanel(
            Func<int, IReadOnlyList<string>, ST_AUTOMATION_STATUS_SNAPSHOT<IReadOnlyDictionary<string, double>>> snapshot, 
            Action<int> requestRefresh, 
            Func<ST_MANUAL_HOME_SPEED_TARGET, double, Task<double>> save, 
            Action<string> warning, 
            Action<string> completed
        )
        {
            _snapshot = snapshot;
            _requestRefresh = requestRefresh;
            _save = save;
            _warning = warning;
            _completed = completed;
            RefreshCommand = new(_ => Refresh(), _ => _active && Heads.Count > 0 && !Rows.Any(row => row.IsSaving));
        }

        private IEnumerable<CManualHomeSpeedAxis> Rows => Heads.SelectMany(head => head.Axes);

        public void UpdateTargets(IReadOnlyList<ST_MANUAL_HOME_SPEED_TARGET> targets)
        {
            if (_targets.SequenceEqual(targets))
                return;
            _targets = targets;
            Heads = targets.GroupBy(target => target.HeadNo).Select(group => new ST_MANUAL_HOME_SPEED_HEAD(group.Key, group.Select(target =>
            {
                if (!_cache.TryGetValue(target, out var row))
                    _cache.Add(target, row = new(target, Save));
                return row;
            }).ToArray())).ToArray();
            PropertyChanged?.Invoke(this, new(nameof(Heads)));
            foreach (var row in _cache.Values)
                row.SetActive(_active && targets.Contains(row.Target));
            RefreshCommand.NotifyCanExecuteChanged();
            ApplySnapshots();
        }

        public void SetActive(bool active)
        {
            if (_active == active)
                return;
            _active = active;
            foreach (var row in Rows)
                row.SetActive(active);
            RefreshCommand.NotifyCanExecuteChanged();
            ApplySnapshots();
        }

        public void Refresh()
        {
            if (!_active)
                return;
            foreach (var controller in Rows.Where(row => row.Target.IsUsed).Select(row => row.Target.Controller).Distinct())
                _requestRefresh(controller);
            ApplySnapshots();
        }

        public void ApplySnapshots()
        {
            if (!_active)
                return;
            foreach (var group in Rows.Where(row => row.Target.IsUsed).GroupBy(row => row.Target.Controller))
            {
                try
                {
                    var snapshot = _snapshot(group.Key, group.Select(row => row.Target.AxisName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                    foreach (var row in group)
                    {
                        row.SetUnit(snapshot.Value is CConfiguredHomeSpeedValues unitValues && unitValues.Units.TryGetValue(row.Target.AxisName, out var unit) ? unit : "단위 미확인");
                        if (snapshot.Value is { } values && values.TryGetValue(row.Target.AxisName, out var speed) && double.IsFinite(speed) && speed >= 0)
                            row.SetSaved(speed);
                        else
                            row.SetSnapshotState(snapshot.Value is null ? snapshot.State : "LOADING", snapshot.Detail);
                    }
                }
                catch (Exception exception)
                {
                    foreach (var row in group)
                        row.SetSnapshotState("ERROR", exception.Message);
                }
            }
        }

        private async Task Save(CManualHomeSpeedAxis row)
        {
            if (!row.CanSet || !_targets.Contains(row.Target))
                return;
            if (!double.TryParse(row.NewValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) || !double.IsFinite(speed) || speed <= 0)
            {
                _warning("HomeSpeed는 0보다 큰 숫자로 입력해 주세요.");
                return;
            }

            row.SetSaving(true);
            var unit = row.SpeedUnit;
            RefreshCommand.NotifyCanExecuteChanged();
            try
            {
                var saved = await _save(row.Target, speed);
                if (!double.IsFinite(saved) || saved <= 0)
                    throw new InvalidOperationException("저장 결과를 확인하지 못했습니다. 새로고침 후 확인해 주세요.");
                row.AcceptSavedInput(saved);
                ApplySnapshots();
                _completed($"HEAD {row.Target.HeadNo:00} / {row.Target.AxisName}\n저장된 HomeSpeed: {saved:G} {unit}\n\n변경값을 적용하려면 CONTROLLER {row.Target.Controller + 1}을 RESET해 주세요.");
            }
            catch (Exception exception)
            {
                row.SetSnapshotState("ERROR", exception.Message);
                _warning($"{row.Target.AxisName} 홈 속도 저장 실패\n{exception.Message}\n\n새로고침 후 저장값을 확인해 주세요.");
            }
            finally
            {
                row.SetSaving(false);
                RefreshCommand.NotifyCanExecuteChanged();
                ApplySnapshots();
            }
        }
    }

    public sealed class CManualHomeSpeedAxis : INotifyPropertyChanged
    {
        private double? _saved;
        private string _input = "";
        private bool _dirty;
        private bool _active;
        private string _state = "LOADING";
        public event PropertyChangedEventHandler? PropertyChanged;
        public ST_MANUAL_HOME_SPEED_TARGET Target { get; }
        public string AxisName => Target.IsUsed ? Target.AxisName : Target.ConfigurationError == "NOT USE" ? "Z NOT USE" : Target.AxisName;
        public string SavedValue => !Target.IsUsed ? Target.ConfigurationError == "NOT USE" ? "NOT USE" : "CONFIG ERR" : _saved?.ToString("G", CultureInfo.InvariantCulture) ?? _state;

        public string NewValue
        {
            get => _input;
            set
            {
                _input = value;
                _dirty = true;
                Notify();
            }
        }

        public string Error { get; private set; } = "";
        public string SpeedUnit { get; private set; } = "단위 미확인";
        public bool IsSaving { get; private set; }
        public bool IsLoading { get; private set; }
        public bool CanSet => _active && Target.IsUsed && _saved.HasValue && !IsSaving && !IsLoading;
        public CButtonCommand SetCommand { get; }

        public CManualHomeSpeedAxis(ST_MANUAL_HOME_SPEED_TARGET target, Func<CManualHomeSpeedAxis, Task> save)
        {
            Target = target;
            if (!target.IsUsed)
                SpeedUnit = "-";
            SetCommand = new(async _ => await save(this), _ => CanSet);
        }

        internal void SetActive(bool active)
        {
            _active = active;
            Notify();
        }

        internal void SetUnit(string unit)
        {
            if (SpeedUnit == unit)
                return;
            SpeedUnit = unit;
            PropertyChanged?.Invoke(this, new(nameof(SpeedUnit)));
        }

        internal void SetSaving(bool saving)
        {
            IsSaving = saving;
            Notify();
        }

        internal void SetSaved(double value, bool forceInput = false)
        {
            if (_saved == value && !IsLoading && Error.Length == 0 && !forceInput)
                return;
            _saved = value;
            IsLoading = false;
            Error = "";
            if (!_dirty || forceInput)
            {
                _input = value.ToString("G", CultureInfo.InvariantCulture);
                _dirty = false;
            }

            Notify();
        }

        internal void AcceptSavedInput(double value)
        {
            _input = value.ToString("G", CultureInfo.InvariantCulture);
            _dirty = false;
            Notify();
        }

        internal void SetSnapshotState(string state, string detail)
        {
            if (_saved is null && _state == state && Error == detail && IsLoading == (state == "LOADING"))
                return;
            _saved = null;
            _state = state;
            Error = detail;
            IsLoading = state == "LOADING";
            Notify();
        }

        private void Notify()
        {
            PropertyChanged?.Invoke(this, new(string.Empty));
            SetCommand.NotifyCanExecuteChanged();
        }
    }

    public sealed record ST_MANUAL_HEAD_GROUP(int AutomationNo, string Title, IReadOnlyList<ST_MANUAL_HEAD_CARD> Heads);
    public sealed record ST_MANUAL_HEAD_CARD(int HeadNo, string HeadName, string XSummary, string YSummary, string ZSummary, string State, bool IsSelected, bool IsEnabled = true, int AutomationNo = 0)
    {
        public Brush StateBrush
        {
            get
            {
                return CStatusBrush.ForHeadStatus(State);
            }
        }

        public Brush TextBrush
        {
            get
            {
                return IsSelected ? Brushes.White : CStatusBrush.PrimaryText;
            }
        }
    }

    public sealed record ST_MANUAL_SETTING_FILE(string Name, bool IsSelected);
    public sealed record ST_MANUAL_DIO_TARGET(int AutomationNo, string Address, string AxisName, int DioNo);
    public sealed record ST_MANUAL_VISION_PROTOCOL_COMMAND(string DisplayName, string CommandText, string Description)
    {
        public override string ToString()
        {
            return DisplayName;
        }
    }

    public sealed class ST_MANUAL_VISION_MARK_SEARCH : CBindingBase
    {
        private readonly Action _valueChanged;
        private string _cellNo;
        private string _doeNo;
        private string _lineNo;
        private string _indexX;
        private string _indexY;
        private string _headNo;
        public ST_MANUAL_VISION_MARK_SEARCH(string cellNo, string doeNo, string lineNo, string indexX, string indexY, string headNo, Action valueChanged)
        {
            _cellNo = cellNo;
            _doeNo = doeNo;
            _lineNo = lineNo;
            _indexX = indexX;
            _indexY = indexY;
            _headNo = headNo;
            _valueChanged = valueChanged;
        }

        public string CellNo
        {
            get
            {
                return _cellNo;
            }

            set
            {
                SetValue(ref _cellNo, value);
            }
        }

        public string DoeNo
        {
            get
            {
                return _doeNo;
            }

            set
            {
                SetValue(ref _doeNo, value);
            }
        }

        public string LineNo
        {
            get
            {
                return _lineNo;
            }

            set
            {
                SetValue(ref _lineNo, value);
            }
        }

        public string IndexX
        {
            get
            {
                return _indexX;
            }

            set
            {
                SetValue(ref _indexX, value);
            }
        }

        public string IndexY
        {
            get
            {
                return _indexY;
            }

            set
            {
                SetValue(ref _indexY, value);
            }
        }

        public string HeadNo
        {
            get
            {
                return _headNo;
            }

            set
            {
                SetValue(ref _headNo, value);
            }
        }

        private void SetValue(ref string field, string value)
        {
            if (SetProperty(ref field, value ?? ""))
            {
                _valueChanged();
            }
        }
    }

    public sealed record ST_MANUAL_SCANNER_AXIS(
        int HeadNo, 
        string Head, 
        string Automation, 
        int AutomationNo, 
        string AxisRole, 
        string AxisReference, 
        string Enable, 
        string Home, 
        string CurrentPosition, 
        string AuxFeedback, 
        string Error, 
        string Detail
    )
    {
        public long? FaultMask { get; init; }
        public string FaultState => FaultMask.HasValue ? FaultMask.Value == 0 ? "OK" : "FAULT" : Error switch
        {
            "NOT USE" => "-",
            "OFFLINE" => "OFFLINE",
            "STALE" => "STALE",
            _ => "UNKNOWN"
        };
        public string FaultDetail => FaultMask.HasValue ? CAutomationAxisFault.Describe(FaultMask.Value) : $"Fault 상태를 확인할 수 없습니다.\n{Detail}";
        public Brush FaultBrush => FaultMask.HasValue ? FaultMask.Value == 0 ? CStatusBrush.Online : CStatusBrush.Offline : CStatusBrush.Muted;
        public bool CanClearFault => IsControlEnabled && FaultMask is> 0 && !CAutomationAxisFault.RequiresReset(FaultMask.Value);

        public string AxisTitle
        {
            get
            {
                if (AxisReference.Equals("Z NOT USE", StringComparison.OrdinalIgnoreCase))
                {
                    return "Z NOT USE";
                }

                return $"{AxisRole}  {AxisReference}";
            }
        }

        public bool IsControlEnabled
        {
            get
            {
                return !AxisReference.Equals("Z NOT USE", StringComparison.OrdinalIgnoreCase) && !Enable.Equals("NOT USE", StringComparison.OrdinalIgnoreCase);
            }
        }

        public Brush EnableBrush
        {
            get
            {
                return ScannerStateBrush(Enable);
            }
        }

        public Brush HomeBrush
        {
            get
            {
                return ScannerStateBrush(Home);
            }
        }

        public Brush ErrorBrush
        {
            get
            {
                return ScannerStateBrush(Error);
            }
        }

        private static Brush ScannerStateBrush(string state)
        {
            return state.Trim().ToUpperInvariant() switch
            {
                "ENABLE" or "HOME" or "OK" => CStatusBrush.Online,
                "DISABLE" or "WAIT" => CStatusBrush.Wait,
                "NO" or "ERROR" or "OFFLINE" => CStatusBrush.Offline,
                _ => CStatusBrush.Muted
            };
        }
    }

    public sealed class ST_MANUAL_STAGE_AXIS : CBindingBase
    {
        private string _currentPosition = "-";
        private string _currentSpeed = "-";
        private bool _isSelected;
        private string _status = "WAIT";
        private string _statusMessage = "";
        private string _targetPosition = "0.000";
        private string _targetSpeed = "0.000";
        public ST_MANUAL_STAGE_AXIS(string key, string displayName, string unit, string readPositionId, string writePositionId)
        {
            Key = key;
            DisplayName = displayName;
            Unit = unit;
            ReadPositionId = readPositionId;
            WritePositionId = writePositionId;
        }

        public string Key { get; }
        public string DisplayName { get; }
        public string Unit { get; }
        public string ReadPositionId { get; }
        public string WritePositionId { get; }

        public string SpeedUnit
        {
            get
            {
                return $"{Unit}/s";
            }
        }

        public bool IsSelected
        {
            get
            {
                return _isSelected;
            }

            set
            {
                SetProperty(ref _isSelected, value);
            }
        }

        public string CurrentPosition
        {
            get
            {
                return _currentPosition;
            }

            set
            {
                SetProperty(ref _currentPosition, value);
            }
        }

        public string CurrentSpeed
        {
            get
            {
                return _currentSpeed;
            }

            set
            {
                SetProperty(ref _currentSpeed, value);
            }
        }

        public string Status
        {
            get
            {
                return _status;
            }

            set
            {
                SetProperty(ref _status, value);
            }
        }

        public string StatusMessage
        {
            get
            {
                return _statusMessage;
            }

            set
            {
                SetProperty(ref _statusMessage, value);
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
                SetProperty(ref _targetPosition, value);
            }
        }

        public string TargetSpeed
        {
            get
            {
                return _targetSpeed;
            }

            set
            {
                SetProperty(ref _targetSpeed, value);
            }
        }
    }

    public sealed class ST_MANUAL_PARAMETER : CBindingBase
    {
        public static IReadOnlyList<string> ShapeOptions { get; } = ["Cross", "Circle", "Rect", "Grid", "H-Line", "V-Line"];
        public static IReadOnlyList<string> DirectionOptions { get; } = ["CW", "CCW"];
        public static IReadOnlyList<string> LaserModeOptions { get; } = ["TIME", "COUNT"];

        private string _value;
        public ST_MANUAL_PARAMETER(string key, string parameter, string value, string unit, EN_RECIPE_DATA_TYPE dataType = EN_RECIPE_DATA_TYPE.String, double min = 0.0, double max = 0.0)
        {
            Key = key;
            Parameter = parameter;
            _value = value;
            Unit = unit;
            DataType = dataType;
            Min = min;
            Max = max;
        }

        public string Key { get; }
        public string Parameter { get; }

        public string Value
        {
            get
            {
                return _value;
            }

            set
            {
                SetProperty(ref _value, value);
            }
        }

        public string Unit { get; }
        public EN_RECIPE_DATA_TYPE DataType { get; }

        public bool UsesSelectionEditor
        {
            get
            {
                return DataType == EN_RECIPE_DATA_TYPE.Bool || Key.Equals("ShapeName", StringComparison.OrdinalIgnoreCase) || Key.Equals("Direction", StringComparison.OrdinalIgnoreCase) || Key.Equals("LaserOnMode", StringComparison.OrdinalIgnoreCase);
            }
        }

        public IReadOnlyList<string> ValueOptions
        {
            get
            {
                if (Key.Equals("ShapeName", StringComparison.OrdinalIgnoreCase))
                    return ShapeOptions;
                if (Key.Equals("Direction", StringComparison.OrdinalIgnoreCase))
                    return DirectionOptions;
                if (Key.Equals("LaserOnMode", StringComparison.OrdinalIgnoreCase))
                    return LaserModeOptions;
                return Value.Trim()is "0" or "1" ? ["0", "1"] : ["OFF", "ON"];
            }
        }

        public double Min { get; }
        public double Max { get; }
    }

    public sealed class ST_MANUAL_COMMAND_STATE : CBindingBase
    {
        private readonly Action<string>? _valueChanged;
        private readonly Func<string, string>? _normalizeValue;
        private string _value;
        public ST_MANUAL_COMMAND_STATE(string name, string value, string unit = "", bool isEditable = false, Action<string>? valueChanged = null, Func<string, string>? normalizeValue = null)
        {
            Name = name;
            _value = value;
            Unit = unit;
            IsEditable = isEditable;
            _valueChanged = valueChanged;
            _normalizeValue = normalizeValue;
        }

        public string Name { get; }

        public string Value
        {
            get
            {
                return _value;
            }

            set
            {
                var nextValue = _normalizeValue?.Invoke(value) ?? value;
                if (SetProperty(ref _value, nextValue))
                {
                    _valueChanged?.Invoke(nextValue);
                }
            }
        }

        public string Unit { get; }
        public bool IsEditable { get; }

        public bool IsReadOnly
        {
            get
            {
                return !IsEditable;
            }
        }

        public bool UsesSelectionEditor
        {
            get
            {
                return Name.Equals("Laser On Mode", StringComparison.OrdinalIgnoreCase);
            }
        }

        public IReadOnlyList<string> ValueOptions { get; } = ["TIME", "COUNT"];
    }

    public sealed class CManualMotionPanel : INotifyPropertyChanged
    {
        private readonly CMenuManual _owner;
        private ST_MANUAL_CONTROLLER_HEAD? _selectedHead;
        public event PropertyChangedEventHandler? PropertyChanged;
        public IReadOnlyList<CManualMotionAxis> Axes { get; }

        public ST_MANUAL_CONTROLLER_HEAD? SelectedHead
        {
            get => _selectedHead;
            set
            {
                if (ReferenceEquals(value, _selectedHead))
                    return;
                if (_selectedHead is not null)
                    _selectedHead.PropertyChanged -= HeadChanged;
                _selectedHead = value;
                if (_selectedHead is not null)
                    _selectedHead.PropertyChanged += HeadChanged;
                _ = _owner.StopMotionJog();
                RefreshAxes();
                PropertyChanged?.Invoke(this, new(nameof(SelectedHead)));
                PropertyChanged?.Invoke(this, new(nameof(Title)));
            }
        }

        public string Title => SelectedHead is null ? "표에서 이동할 Head를 선택해 주세요." : $"{SelectedHead.HeadName} · MOTION";
        public CButtonCommand CenterCommand { get; }
        public CButtonCommand RecipeCommand { get; }
        public CButtonCommand StopCommand { get; }

        public CManualMotionPanel(CMenuManual owner)
        {
            _owner = owner;
            Axes = new[]
            {
                "X",
                "Y",
                "Z"
            }.Select(role => new CManualMotionAxis(owner, role)).ToArray();
            CenterCommand = new(async _ => await owner.RunMotion(Axes[0], "CENTER"), _ => Axes[0].CanMove && Axes[1].CanMove);
            RecipeCommand = new(async _ => await owner.RunMotion(Axes[2], "RECIPE"), _ => Axes[2].CanMove);
            StopCommand = new(async _ => await owner.StopAllManualMotion());
        }

        private void HeadChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ST_MANUAL_CONTROLLER_HEAD.Axes))
                RefreshAxes();
        }

        private void RefreshAxes()
        {
            foreach (var row in Axes)
                row.SetAxis(SelectedHead?.Axes.FirstOrDefault(a => a.AxisRole == row.Role));
            CenterCommand.NotifyCanExecuteChanged();
            RecipeCommand.NotifyCanExecuteChanged();
        }

        public void RefreshHeads(IReadOnlyList<ST_MANUAL_CONTROLLER_HEAD> heads)
        {
            if (SelectedHead is not null && !heads.Contains(SelectedHead))
                SelectedHead = null;
        }

        public Task CancelPendingAndStopJog() => _owner.StopMotionJog();
        public bool IsConfirmationOpen => _owner.IsMotionConfirmationOpen;
    }

    public sealed class CManualMotionAxis : INotifyPropertyChanged
    {
        private readonly CMenuManual _owner;
        private ST_MANUAL_SCANNER_AXIS? _axis;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Role { get; }
        public string AxisName => _axis?.AxisReference ?? Role;
        public bool CanMove => _axis is { IsControlEnabled: true } && AxisName != "-";
        public string Absolute { get; set; } = "0.000";
        public string Relative { get; set; } = "0.000";
        public CButtonCommand AbsoluteCommand { get; }
        public CButtonCommand RelativeCommand { get; }

        public CManualMotionAxis(CMenuManual owner, string role)
        {
            _owner = owner;
            Role = role;
            AbsoluteCommand = new(async _ => await owner.RunMotion(this, "ABS"), _ => CanMove);
            RelativeCommand = new(async _ => await owner.RunMotion(this, "REL"), _ => CanMove);
        }

        public Task StartJog(int direction) => _owner.StartMotionJog(this, direction);
        public Task StopJog() => _owner.StopMotionJog();
        internal void SetAxis(ST_MANUAL_SCANNER_AXIS? axis)
        {
            if (_axis == axis)
                return;
            var changed = _axis?.HeadNo != axis?.HeadNo || _axis?.AxisReference != axis?.AxisReference || _axis?.AutomationNo != axis?.AutomationNo;
            _axis = axis;
            if (changed)
            {
                Absolute = "0.000";
                Relative = "0.000";
                _ = StopJog();
            }

            PropertyChanged?.Invoke(this, new(string.Empty));
            AbsoluteCommand.NotifyCanExecuteChanged();
            RelativeCommand.NotifyCanExecuteChanged();
        }
    }
}
