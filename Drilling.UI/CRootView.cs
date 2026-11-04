namespace Drilling.UI
{
    using System.Windows;
    using System.Windows.Threading;
    using Drilling.Common.Alarm;
    using Drilling.Common.Managers;
    using Drilling.Common.Review;
    using Drilling.Common.Station;
    using Drilling.UI.Menu;
    using Drilling.UI.Menu.Menus;
    using Drilling.UI.Popup;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using Drilling.Common.Interface;
    using Drilling.Common.Motion;
    using Drilling.Common.InterLock;
    using Drilling.Common.Product;
    using Drilling.Common.Recipe;

    public sealed partial class CRootView : CBindingBase
    {
#region 공유 상태·초기값
        private readonly Dispatcher _alarmDispatcher = Dispatcher.CurrentDispatcher;
        private readonly CAlarmNavigationTracker _alarmNavigation = new();
        private readonly HashSet<(int Code, DateTimeOffset Time)> _heavyOccurrences = [];
        private int _alarmRefreshQueued;
        private bool _isAlarmNavigation = false;
        private bool _isHeaderAlarmResetRunning;
        private bool _heavyRecoveryBlocked;
        private Task? _heavyAlarmRecoveryTask;
        private string _alarmRecoveryStatus = "";
        private CAlarmPopupDialog? _alarmPopup;
        private CButtonCommand? _headerAlarmResetCommand;
        private static readonly TimeSpan MainLiveStatusRefreshInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MainLiveStatusTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MonitorDefaultRefreshInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MonitorShellStatusRefreshInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ErrorReportInterval = TimeSpan.FromSeconds(10);
        private const string SelectRecipeSettingKey = "SELECT_RECIPE";
        private const string DefaultRecipeId = "DRILL_A01";
        private const string ShiftOffsetXKey = "SHIFT_OFFSET_X";
        private const string ShiftOffsetYKey = "SHIFT_OFFSET_Y";
        private static readonly IReadOnlyList<EN_MENU> OperatorMenus = [EN_MENU.Main, EN_MENU.Manual, EN_MENU.Recipe, EN_MENU.Setting, EN_MENU.Correction, EN_MENU.Alarm, EN_MENU.Monitor, EN_MENU.Pm];
        private static readonly IReadOnlySet<EN_MENU> AutoModeMenus = new HashSet<EN_MENU>
        {
            EN_MENU.Main,
            EN_MENU.Alarm,
            EN_MENU.Monitor,
            EN_MENU.Pm
        };
        private static readonly IReadOnlyList<EN_EQP_MODULE> HeaderModules = [
            EN_EQP_MODULE.Automation1, 
            EN_EQP_MODULE.Vision, 
            EN_EQP_MODULE.TalonLaser, 
            EN_EQP_MODULE.Attenuator, 
            EN_EQP_MODULE.Bet, 
            EN_EQP_MODULE.PowerMeter, 
            EN_EQP_MODULE.Motion, 
            EN_EQP_MODULE.PicoMotor, 
            EN_EQP_MODULE.Melsec
        ];
        private readonly CStationManager _stationManager;
        private readonly CManager _manager;
        private readonly CInterfaceManager _interfaceManager;
        private readonly CMotionManager _motionManager;
        private readonly CAlarmManager _alarmManager;
        private readonly CInterLockManager _interLockManager;
        private readonly CRecipeManager _recipeManager;
        private readonly CProductManager _productManager;
        private readonly CReviewManager _reviewManager;
        private readonly IReadOnlyDictionary<EN_MENU, CMenuBase> _menus;
        private readonly Dictionary<EN_EQP_MODULE, int> _selectedHeaderModuleIndexes = new();
        private readonly SemaphoreSlim _processPreparationGate = new(1, 1);
        private readonly CancellationTokenSource _initialProcessModelCts = new();
        private readonly Task _initialProcessModelTask;
        private CMenuItem _selectedMenu;
        private CScreenViewModel _currentScreen;
        private ST_SYSTEM_STATUS _systemStatus = CreateFallbackST_SYSTEM_STATUS();
        private IReadOnlyList<ST_INTERFACE_COMM_STATUS> _interfaceCommStatuses = [];
        private string _statusMessage = "Simulation mode ready.";
        private string _currentDateText = DateTime.Now.ToString("yyyy-MM-dd");
        private string _currentTimeText = DateTime.Now.ToString("HH:mm:ss");
        private int _selectedHeadNo = 1;
        private string _selectedManualSettingName = "CIRCLE_TEST.scan";
        private string _selectedRecipeId = "";
        private string _viewRecipeId = "";
        private string _selectedRecipeCategory = "ALL";
        private string _selectedSettingTab = "OPTION";
        private string _selectedSettingGroup = "ALL";
        private string _selectedMonitorTab = "PRODUCT";
        private string _shiftOffsetX = "0";
        private string _shiftOffsetY = "0";
        private EN_SYSTEM_MODE _operationMode = EN_SYSTEM_MODE.Manual;
        private EN_AUTO_CYCLE_STATE _autoCycleState = EN_AUTO_CYCLE_STATE.Ready;
        private CancellationTokenSource? _autoCycleCts;
        private TaskCompletionSource<bool>? _autoCycleCompletion;
        private EN_UI_THEME _currentTheme = EN_UI_THEME.Light;
        private bool _statusRefreshRunning;
        private int _screenRefreshRunning;
        private bool _screenRefreshPending;
        private bool _monitorLiveRefreshRunning;
        private bool _manualScannerStatusRefreshRunning;
        private bool _mainLiveRefreshRunning;
        private bool _mainLiveStatusErrorActive;
        private CancellationTokenSource? _mainLiveStatusRefreshCts;
        private long _lastMonitorScreenRefreshTimestamp;
        private long _lastMonitorShellStatusRefreshTimestamp;
        private long _lastMainLiveStatusRefreshTimestamp;
        private long _lastMainLiveStatusErrorReportTimestamp;
        private long _lastClockTickErrorReportTimestamp;
        private string _lastMainLiveStatusError = "";
        private string _lastClockTickError = "";
        private ST_PM_LOCK_STATUS _pmLockStatus = new(false, null);
        private static readonly TimeSpan AutoCycleStopTimeout = TimeSpan.FromSeconds(10);
        private CProtectWindowRequestNotice? _protectWindowNotice;
        private CAutoFineReviewCoordinator? _autoFineReview;
        private CMelsecStageSimulationDialog? _stageSimulationDialog;
        public event EventHandler? MelsecSimulatorVisibilityChanged;
        private Visibility? _lastSimulatorVisibility;
#endregion
#region 생성·초기화
        public CRootView(
            CManager manager, 
            CStationManager stationManager, 
            CInterfaceManager interfaceManager, 
            CMotionManager motionManager, 
            CAlarmManager alarmManager, 
            CInterLockManager interLockManager, 
            CManualScanFileBase manualScanFile, 
            CRecipeManager recipeManager, 
            CSettingManager settingManager, 
            CProductManager productManager, 
            CReviewManager reviewManager, 
            CAutomationScriptFileBase automationScriptFile
        )
        {
            _manager = manager;
            _stationManager = stationManager;
            _interfaceManager = interfaceManager;
            _motionManager = motionManager;
            _alarmManager = alarmManager;
            _interLockManager = interLockManager;
            _recipeManager = recipeManager;
            _productManager = productManager;
            _reviewManager = reviewManager;
            _selectedRecipeId = NormalizeRecipeId(settingManager.GetValue(EN_SETTING_TAB.Option, SelectRecipeSettingKey, DefaultRecipeId));
            if (string.IsNullOrWhiteSpace(_selectedRecipeId))
            {
                _selectedRecipeId = DefaultRecipeId;
            }

            _viewRecipeId = _selectedRecipeId;
            CThemeManager.Apply(_currentTheme);
            global::Drilling.UI.Menu.Menus.CMenuItem BuildCMenuItem(global::Drilling.UI.Menu.Menus.EN_MENU menu)
            {
                return new CMenuItem(menu, GetMenuDisplayName(menu));
            }

            Menus = new ObservableCollection<CMenuItem>(OperatorMenus.Select(BuildCMenuItem));
            _selectedMenu = Menus[0];
            ApplyMenuAccessForOperationMode();
            HeaderStatusItems = new ObservableCollection<ST_HEADER_STATUS_ITEM>();
            HeaderContextStatusItems = new ObservableCollection<ST_HEADER_STATUS_ITEM>();
            HeaderCommunicationStatusItems = new ObservableCollection<ST_HEADER_STATUS_ITEM>();
            FooterStatusItems = new ObservableCollection<ST_HEADER_STATUS_ITEM>();
            _currentScreen = CreateLoadingScreen(EN_MENU.Main, "MAIN");
            async void StartCallback(object? arg0)
            {
                await StartCycle();
            }

            bool CanExecuteStartCommand(object? arg0)
            {
                return SelectedMenu.Menu == EN_MENU.Main && CanStartAutoCycle();
            }

            StartCommand = new CButtonCommand(StartCallback, CanExecuteStartCommand);
            async void StopCallback(object? arg0)
            {
                await StopCycle();
            }

            bool CanExecuteStopCommand(object? arg0)
            {
                return SelectedMenu.Menu == EN_MENU.Main && CanStopAutoCycle();
            }

            StopCommand = new CButtonCommand(StopCallback, CanExecuteStopCommand);
            async void SelectHeadCallback(object? parameter)
            {
                await SelectHead(parameter);
            }

            bool CanExecuteSelectHeadCommand(object? parameter)
            {
                return CanSelectHead && (parameter is null || CanSelectHeadParameter(parameter));
            }

            SelectHeadCommand = new CButtonCommand(SelectHeadCallback, CanExecuteSelectHeadCommand);
            async void ToggleThemeCallback(object? arg0)
            {
                await ToggleTheme();
            }

            ToggleThemeCommand = new CButtonCommand(ToggleThemeCallback);
            OpenMelsecStageSimulationCommand = new CButtonCommand(_ => OpenMelsecStageSimulation());
            async void ToggleOperationModeCallback(object? arg0)
            {
                await ToggleOperationMode();
            }

            bool CanExecuteToggleOperationModeCommand(object? arg0)
            {
                return _autoCycleState == EN_AUTO_CYCLE_STATE.Ready;
            }

            ToggleOperationModeCommand = new CButtonCommand(ToggleOperationModeCallback, CanExecuteToggleOperationModeCommand);
            _systemStatus = _systemStatus with
            {
                OperationMode = _operationMode
            };
            OnPropertyChanged(nameof(IsManualShiftOffsetVisible));
            RefreshShellStatusItems();
            _menus = CreateMenus(stationManager, interfaceManager, motionManager, alarmManager, interLockManager, manualScanFile, recipeManager, settingManager, productManager, reviewManager, automationScriptFile);
            _menus[_selectedMenu.Menu].OnActivated();
            _alarmManager.CurrentChanged += OnCurrentAlarmsChanged;
            OnCurrentAlarmsChanged();
            StartClock();
            _initialProcessModelTask = InitializeProcessModel(_initialProcessModelCts.Token);
        }
#endregion

#region 기본 처리
        private enum EN_AUTO_CYCLE_STATE
        {
            Ready,
            Preparing,
            Running,
            Stopping
        }

        public void StopAutomationStatusAcquisition()
        {
            _protectWindowNotice?.Dispose();
            _stageSimulationDialog?.Close();
            _alarmPopup?.Close();
            _alarmManager.CurrentChanged -= OnCurrentAlarmsChanged;
            _manager.StopBackgroundMonitoring().GetAwaiter().GetResult();
        }

        public ObservableCollection<CMenuItem> Menus { get; }
        public ObservableCollection<ST_HEADER_STATUS_ITEM> HeaderStatusItems { get; }
        public ObservableCollection<ST_HEADER_STATUS_ITEM> HeaderContextStatusItems { get; }
        public ObservableCollection<ST_HEADER_STATUS_ITEM> HeaderCommunicationStatusItems { get; }
        public ObservableCollection<ST_HEADER_STATUS_ITEM> FooterStatusItems { get; }

        public CMenuItem SelectedMenu
        {
            get
            {
                return _selectedMenu;
            }

            set
            {
                if (!_isAlarmNavigation && value is not null && IsReviewNavigationLocked(value.Menu))
                {
                    StatusMessage = "Review is running. Stop the current Review before changing the menu.";
                    OnPropertyChanged(nameof(SelectedMenu));
                    CRecipeConfirmDialog.ShowWarning("검사 진행 중", "검사를 중지한 후 이동해 주세요.");
                    return;
                }

                if (value is null || !CanAccessMenu(value.Menu))
                {
                    if (value is not null)
                    {
                        StatusMessage = $"{value.Name} menu is available in MANUAL mode only.";
                        CRecipeConfirmDialog.ShowWarning("메뉴 진입 불가", "Manual Mode에서만 진입 가능합니다.");
                    }

                    OnPropertyChanged(nameof(SelectedMenu));
                    return;
                }

                var previousMenu = _selectedMenu.Menu;
                if (!SetProperty(ref _selectedMenu, value))
                {
                    return;
                }

                CUiLogService.WriteClick("CRootView", "MENU_SELECT", value.Name, $"{previousMenu} -> {value.Menu}");
                if (previousMenu == EN_MENU.Main && value.Menu != EN_MENU.Main)
                {
                    CancelMainLiveStatusRefresh();
                    QueueMainScannerStatusPollingStop();
                }

                if (previousMenu == EN_MENU.Manual && value.Menu != EN_MENU.Manual)
                {
                    QueueManualScannerStatusPollingStop();
                }

                if (_menus.TryGetValue(previousMenu, out var previousMenuViewModel))
                {
                    previousMenuViewModel.OnDeactivated();
                }

                if (_menus.TryGetValue(value.Menu, out var selectedMenuViewModel))
                {
                    selectedMenuViewModel.OnActivated();
                }

                ResetMenuStateOnOpen(previousMenu, value.Menu);
                if (_menus.TryGetValue(EN_MENU.Monitor, out var monitorMenu) && monitorMenu is CMenuMonitor monitor)
                {
                    monitor.SetPollingActive(value.Menu == EN_MENU.Monitor);
                }

                StartCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
                SelectHeadCommand.NotifyCanExecuteChanged();
                RefreshShellStatusItems();
                _ = RefreshCurrentScreen();
            }
        }

        public CScreenViewModel CurrentScreen
        {
            get
            {
                return _currentScreen;
            }

            private set
            {
                SetProperty(ref _currentScreen, value);
            }
        }

        public string StatusMessage
        {
            get
            {
                return _statusMessage;
            }

            private set
            {
                SetProperty(ref _statusMessage, value);
            }
        }

        public string CurrentDateText
        {
            get
            {
                return _currentDateText;
            }

            private set
            {
                SetProperty(ref _currentDateText, value);
            }
        }

        public string CurrentTimeText
        {
            get
            {
                return _currentTimeText;
            }

            private set
            {
                SetProperty(ref _currentTimeText, value);
            }
        }

        public string CurrentUserText
        {
            get
            {
                return _interfaceManager.IsSimulation && _motionManager.IsSimulation ? "Engineer / Simulation" : "Engineer / Live";
            }
        }

        public string ThemeToggleText
        {
            get
            {
                return _currentTheme == EN_UI_THEME.Light ? "LIGHT MODE" : "DARK MODE";
            }
        }

        public string ThemeModeText
        {
            get
            {
                return _currentTheme == EN_UI_THEME.Light ? "Light Theme" : "Dark Theme";
            }
        }

        public CButtonCommand StartCommand { get; }
        public CButtonCommand StopCommand { get; }
        public CButtonCommand SelectHeadCommand { get; }
        public CButtonCommand ToggleThemeCommand { get; }
        public CButtonCommand ToggleOperationModeCommand { get; }

        public string ShiftOffsetX
        {
            get
            {
                return _shiftOffsetX;
            }

            set
            {
                SetProperty(ref _shiftOffsetX, NormalizeShiftOffsetText(value));
            }
        }

        public string ShiftOffsetY
        {
            get
            {
                return _shiftOffsetY;
            }

            set
            {
                SetProperty(ref _shiftOffsetY, NormalizeShiftOffsetText(value));
            }
        }

        public bool IsManualShiftOffsetVisible
        {
            get
            {
                return _operationMode == EN_SYSTEM_MODE.Manual;
            }
        }

        private bool CanSelectHead
        {
            get
            {
                return SelectedMenu.Menu is EN_MENU.Main or EN_MENU.Manual;
            }
        }

        private bool IsSimulationEnvironment
        {
            get
            {
                return _interfaceManager.IsSimulation && _motionManager.IsSimulation;
            }
        }

        private bool CanStartAutoCycle()
        {
            return _autoCycleState == EN_AUTO_CYCLE_STATE.Ready && IsManualStartEligible();
        }

        private bool CanStopAutoCycle()
        {
            return _autoCycleState is EN_AUTO_CYCLE_STATE.Preparing or EN_AUTO_CYCLE_STATE.Running || (_operationMode == EN_SYSTEM_MODE.Auto && (_stationManager.StageRequests.IsRunning || _stationManager.StageRequests.NeedsCleanup));
        }

        private bool IsManualStartEligible()
        {
            return _operationMode == EN_SYSTEM_MODE.Manual;
        }

        private async Task RefreshCurrentScreen()
        {
            if (Interlocked.Exchange(ref _screenRefreshRunning, 1) != 0)
            {
                _screenRefreshPending = true;
                return;
            }

            var targetMenu = SelectedMenu;
            try
            {
                do
                {
                    targetMenu = SelectedMenu;
                    _screenRefreshPending = false;
                    if (!_menus.TryGetValue(targetMenu.Menu, out var menu))
                    {
                        if (SelectedMenu.Menu == targetMenu.Menu)
                        {
                            CurrentScreen = CreateLoadingScreen(targetMenu.Menu, targetMenu.Name);
                        }

                        continue;
                    }

                    var screen = await menu.Build();
                    if (SelectedMenu.Menu != targetMenu.Menu)
                    {
                        _screenRefreshPending = true;
                        continue;
                    }

                    CurrentScreen = screen;
                    if (targetMenu.Menu == EN_MENU.Manual)
                    {
                        SyncSelectedManualSettingFromScreen();
                    }

                    if (targetMenu.Menu == EN_MENU.Setting)
                    {
                        SyncSelectedSettingSelectionFromScreen();
                    }

                    ApplySystemStatusSnapshot(await GetSystemStatusSnapshot(), refreshOperationModeAccess: false);
                    if (targetMenu.Menu == EN_MENU.Pm)
                    {
                        StatusMessage = "PM lock entered. Operation commands are blocked.";
                    }
                }
                while (_screenRefreshPending);
            }
            finally
            {
                Interlocked.Exchange(ref _screenRefreshRunning, 0);
                if (_screenRefreshPending)
                {
                    _ = RefreshCurrentScreen();
                }
            }
        }

        private void ResetMenuStateOnOpen(EN_MENU previousMenu, EN_MENU selectedMenu)
        {
            if (selectedMenu == EN_MENU.Manual && previousMenu != EN_MENU.Manual)
            {
                ResetSelectedManualHead();
                if (_menus.TryGetValue(EN_MENU.Manual, out var manual) && manual is CMenuManual manualMenu)
                    manualMenu.Review?.ResetForMenuOpen();
            }
        }

        private void ResetSelectedManualHead()
        {
            try
            {
                var settings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option, CancellationToken.None);
                var enabledHeadNos = CScannerStatusSnapshotReader.BuildEnabledHeadNumbers(settings, 8);
                if (enabledHeadNos.Count > 0)
                {
                    _selectedHeadNo = enabledHeadNos[0];
                    return;
                }
            }
            catch (Exception exception)when (exception is InvalidDataException or IOException)
            {
            }

            _selectedHeadNo = Math.Clamp(_selectedHeadNo, 1, 8);
        }

        private async Task ToggleTheme()
        {
            _currentTheme = CThemeManager.Toggle();
            OnPropertyChanged(nameof(ThemeToggleText));
            OnPropertyChanged(nameof(ThemeModeText));
            RefreshShellStatusItems();
            await RefreshCurrentScreen();
            StatusMessage = $"{ThemeModeText} applied.";
        }

        private async Task ToggleOperationMode(Func<string, string, bool>? confirm = null, Action<string, string>? warn = null)
        {
            confirm ??= (title, message) => CRecipeConfirmDialog.ShowConfirmation(title, message);
            warn ??= (title, message) => CRecipeConfirmDialog.ShowWarning(title, message);
            if (_autoCycleState != EN_AUTO_CYCLE_STATE.Ready)
            {
                StatusMessage = "MODE cannot be changed while process is preparing, running, or stopping.";
                return;
            }

            var previousMode = _operationMode;
            var nextMode = previousMode == EN_SYSTEM_MODE.Auto ? EN_SYSTEM_MODE.Manual : EN_SYSTEM_MODE.Auto;
            if (nextMode == EN_SYSTEM_MODE.Auto && !await CheckAutoModeInterlocks(warn))
                return;
            var nextModeText = OperationModeValue(nextMode);
            if (!confirm("MODE 변경", $"{nextModeText} Mode로 전환하시겠습니까?"))
            {
                return;
            }

            // The confirmation dialog can remain open while device snapshots change.
            if (nextMode == EN_SYSTEM_MODE.Auto && !await CheckAutoModeInterlocks(warn))
                return;
            if (_autoCycleState != EN_AUTO_CYCLE_STATE.Ready || _operationMode != previousMode)
                return;
            try
            {
                if (nextMode == EN_SYSTEM_MODE.Auto)
                {
                    await WaitForInitialProcessModelPreparation(CancellationToken.None);
                    if (!await CheckAutoModeInterlocks(warn))
                        return;
                    _stationManager.StartStageRequests(CreateStageProcessRequest, CreateAutoReviewActions(), CreateAutoPowerActions());
                }
                else if (!await _stationManager.StageRequests.StopAsync())
                {
                    StatusMessage = "Previous Stage operation is still stopping. MODE change is blocked.";
                    warn("MODE 변경 불가", "Stage 동작의 정지 확인을 기다리고 있습니다.");
                    return;
                }
            }
            catch (Exception exception)
            {
                StatusMessage = $"Stage sequence mode change failed: {exception.Message}";
                warn("MODE 변경 불가", "Stage 시퀀스를 준비할 수 없습니다.");
                return;
            }

            _operationMode = nextMode;
            RefreshOperationModeAccess();
            StopCommand.NotifyCanExecuteChanged();
            _systemStatus = _systemStatus with
            {
                OperationMode = _operationMode
            };
            OnPropertyChanged(nameof(IsManualShiftOffsetVisible));
            RefreshShellStatusItems();
            StatusMessage = _operationMode == EN_SYSTEM_MODE.Auto ? "AUTO mode selected. Waiting for Stage PC request." : "MANUAL mode selected. Main START is enabled.";
            await RefreshCurrentScreen();
        }

        private async Task<bool> CheckAutoModeInterlocks(Action<string, string> warn)
        {
            if (!CheckHeavyAlarmForOperation("AUTO"))
                return false;
            if (!CheckStageMotionOwnership(warn, "AUTO"))
                return false;
            try
            {
                var blockers = await ((CMenuMain)_menus[EN_MENU.Main]).GetAutoModeBlockers();
                if (blockers.Count == 0)
                    // Status refresh can yield while a Manual move is being accepted.
                    return CheckStageMotionOwnership(warn, "AUTO");
            }
            catch (Exception)
            {
            // A failed status check also blocks AUTO entry.
            }

            StatusMessage = "AUTO mode change blocked by Interlock/Safety.";
            warn("AUTO 전환 불가", "AUTO 모드로 전환할 수 없습니다.\nInterlock/Safety를 확인해 주세요.");
            return false;
        }

        private bool CheckStageMotionOwnership(Action<string, string> warn, string operation)
        {
            var manualMovePending = _menus.TryGetValue(EN_MENU.Manual, out var menu)
                && menu is CMenuManual manual && manual.IsManualStageMovePending;
            if (!manualMovePending && !_interfaceManager.StageReviewSequences.IsActive)
                return true;
            StatusMessage = $"{operation} blocked while Stage/Review motion or cleanup is active.";
            warn(operation == "AUTO" ? "AUTO 전환 불가" : "공정 시작 불가", "Stage/Review 수동 이동 또는 공통 시퀀스 정리가 진행 중입니다.\n이동 완료 또는 STOP 후 신호 정리와 소유권 해제를 확인해 주세요.");
            return false;
        }

        private bool CanAccessMenu(EN_MENU menu)
        {
            if (_autoCycleState != EN_AUTO_CYCLE_STATE.Ready)
            {
                return menu is EN_MENU.Main or EN_MENU.Alarm;
            }

            return _operationMode != EN_SYSTEM_MODE.Auto || AutoModeMenus.Contains(menu);
        }

        private bool IsReviewNavigationLocked(EN_MENU targetMenu)
        {
            // AUTO keeps its existing menu allowlist. A shared AUTO Review state
            // must not lock Monitor/PM/Alarm or stop the sequence when leaving Main.
            if (_operationMode != EN_SYSTEM_MODE.Manual || targetMenu == _selectedMenu.Menu)
                return false;
            // Navigation can resume as soon as the owning manual operation is
            // cancelled. Its execution gate stays held until cleanup finishes.
            return _selectedMenu.Menu switch
            {
                EN_MENU.Manual => _menus.TryGetValue(EN_MENU.Manual, out var manual)
                    && manual is CMenuManual { Review: { BlocksMenuNavigation: true } },
                EN_MENU.Correction => _menus.TryGetValue(EN_MENU.Correction, out var correction)
                    && correction is CMenuCorrection { BlocksMenuNavigation: true },
                _ => false
            };
        }

        private void ApplyMenuAccessForOperationMode()
        {
            foreach (var menu in Menus)
            {
                menu.IsEnabled = CanAccessMenu(menu.Menu);
            }
        }

        private void RefreshOperationModeAccess()
        {
            bool MatchesItem(global::Drilling.UI.Menu.Menus.CMenuItem menu)
            {
                return menu.Menu == EN_MENU.Main;
            }

            if (!CanAccessMenu(SelectedMenu.Menu))
            {
                SelectedMenu = Menus.First(MatchesItem);
            }

            ApplyMenuAccessForOperationMode();
            OnPropertyChanged(nameof(IsManualShiftOffsetVisible));
            StartCommand.NotifyCanExecuteChanged();
            ToggleOperationModeCommand.NotifyCanExecuteChanged();
        }

        private void RefreshShellStatusItems()
        {
            RefreshMelsecSimulatorVisibility();
            if (MelsecSimulatorVisibility != Visibility.Visible)
                _stageSimulationDialog?.Close();
            var headerStatusItems = CreateHeaderStatusItems();
            Replace(HeaderStatusItems, headerStatusItems);
            Replace(HeaderContextStatusItems, headerStatusItems.Where(IsHeaderContextStatusItem));
            Replace(HeaderCommunicationStatusItems, headerStatusItems.Where(item => !IsHeaderContextStatusItem(item)));
            Replace(FooterStatusItems, CreateFooterStatusItems(SelectedMenu.Menu));
            OnPropertyChanged(nameof(CurrentUserText));
        }

        private async Task RefreshSystemStatus()
        {
            if (_statusRefreshRunning)
            {
                return;
            }

            _statusRefreshRunning = true;
            try
            {
                ApplySystemStatusSnapshot(await GetSystemStatusSnapshot());
            }
            catch
            {
            // Communication polling must not stop the operator UI.
            }
            finally
            {
                _statusRefreshRunning = false;
            }
        }

        private async Task WaitForInitialProcessModelPreparation(CancellationToken cancellationToken)
        {
            try
            {
                await _initialProcessModelTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine("Initial process model preparation did not complete before Start. A fresh preparation will be attempted. " + exception);
            }
        }

        private async Task RefreshMonitorLiveData()
        {
            if (_monitorLiveRefreshRunning)
            {
                return;
            }

            _monitorLiveRefreshRunning = true;
            try
            {
                if (_menus.TryGetValue(EN_MENU.Monitor, out var menu) && menu is CMenuMonitor monitor)
                {
                    await monitor.RefreshLiveData();
                }

                var nowTimestamp = Stopwatch.GetTimestamp();
                if (HasElapsed(_lastMonitorShellStatusRefreshTimestamp, nowTimestamp, MonitorShellStatusRefreshInterval))
                {
                    _lastMonitorShellStatusRefreshTimestamp = nowTimestamp;
                    await RefreshShellCommunicationStatus();
                }
            }
            catch
            {
            // Live monitor polling must not block the operator UI.
            }
            finally
            {
                _monitorLiveRefreshRunning = false;
            }
        }

        private async Task RefreshMainLiveStatus()
        {
            if (_mainLiveRefreshRunning)
            {
                return;
            }

            var startedScreen = CurrentScreen;
            var startedMainOperating = startedScreen.MainOperating;
            if (SelectedMenu.Menu != EN_MENU.Main || startedMainOperating is null)
            {
                return;
            }

            _mainLiveRefreshRunning = true;
            CancellationTokenSource? refreshCts = null;
            try
            {
                refreshCts = new CancellationTokenSource(MainLiveStatusTimeout);
                _mainLiveStatusRefreshCts = refreshCts;
                await startedMainOperating.RefreshLiveStatus(refreshCts.Token);
                if (IsMainLiveStatusTargetCurrent(startedScreen, startedMainOperating))
                {
                    ReportMainLiveStatusRestored();
                }
            }
            catch (OperationCanceledException)when (!IsMainLiveStatusTargetCurrent(startedScreen, startedMainOperating))
            {
            // Screen transition cancels stale Main polling without changing the active UI.
            }
            catch (Exception exception)when (IsMainLiveStatusTargetCurrent(startedScreen, startedMainOperating))
            {
                ReportMainLiveStatusError(exception);
            }
            catch (Exception exception)
            {
                ReportStaleMainLiveStatusError(exception);
            }
            finally
            {
                if (refreshCts is not null)
                {
                    if (ReferenceEquals(_mainLiveStatusRefreshCts, refreshCts))
                    {
                        _mainLiveStatusRefreshCts = null;
                    }

                    refreshCts.Dispose();
                }

                _mainLiveRefreshRunning = false;
            }
        }

        private void RefreshManualScannerStatus()
        {
            if (_manualScannerStatusRefreshRunning)
            {
                return;
            }

            var manualMenu = CurrentScreen.Manual;
            if (SelectedMenu.Menu != EN_MENU.Manual || manualMenu is null)
            {
                return;
            }

            _manualScannerStatusRefreshRunning = true;
            try
            {
                manualMenu.RefreshScannerStatusSnapshot();
            }
            catch
            {
            // Manual scanner live status must not block the operator UI.
            }
            finally
            {
                _manualScannerStatusRefreshRunning = false;
            }
        }

        private void CancelMainLiveStatusRefresh()
        {
            try
            {
                _mainLiveStatusRefreshCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            // A completed polling request may dispose at the same time as menu navigation.
            }
        }

        private void StopMainScannerStatusPolling()
        {
            if (!_menus.TryGetValue(EN_MENU.Main, out var menu) || menu is not CMenuMain mainMenu)
            {
                return;
            }

            mainMenu.StopScannerStatusPolling();
        }

        private void QueueMainScannerStatusPollingStop()
        {
            void StopPolling()
            {
                try
                {
                    StopMainScannerStatusPolling();
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Main scanner polling stop failed. {exception}");
                }
            }

            _ = Task.Run(StopPolling);
        }

        private void StopManualScannerStatusPolling()
        {
            if (!_menus.TryGetValue(EN_MENU.Manual, out var menu) || menu is not CMenuManual manualMenu)
            {
                return;
            }

            manualMenu.StopScannerStatusPolling();
        }

        private void QueueManualScannerStatusPollingStop()
        {
            void StopPolling()
            {
                try
                {
                    StopManualScannerStatusPolling();
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Manual scanner polling stop failed. {exception}");
                }
            }

            _ = Task.Run(StopPolling);
        }

        private bool IsMainLiveStatusTargetCurrent(CScreenViewModel startedScreen, CMenuMain? startedMainOperating)
        {
            return SelectedMenu.Menu == EN_MENU.Main && ReferenceEquals(CurrentScreen, startedScreen) && ReferenceEquals(CurrentScreen.MainOperating, startedMainOperating);
        }

        private void ReportMainLiveStatusError(Exception exception)
        {
            var nowTimestamp = Stopwatch.GetTimestamp();
            var detail = $"{exception.GetType().Name}: {exception.Message}";
            var shouldLog = !detail.Equals(_lastMainLiveStatusError, StringComparison.Ordinal) || HasElapsed(_lastMainLiveStatusErrorReportTimestamp, nowTimestamp, ErrorReportInterval);
            _mainLiveStatusErrorActive = true;
            StatusMessage = $"Main live status refresh failed. {detail}";
            if (!shouldLog)
            {
                return;
            }

            _lastMainLiveStatusError = detail;
            _lastMainLiveStatusErrorReportTimestamp = nowTimestamp;
            QueueStationStateLog("MAIN_LIVE_STATUS", "ERROR", detail);
        }

        private void ReportStaleMainLiveStatusError(Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                return;
            }

            var detail = $"{exception.GetType().Name}: {exception.Message}";
            QueueStationStateLog("MAIN_LIVE_STATUS", "STALE_ERROR", $"Stale Main live status failure ignored for UI. {detail}");
        }

        private void ReportMainLiveStatusRestored()
        {
            if (!_mainLiveStatusErrorActive)
            {
                return;
            }

            _mainLiveStatusErrorActive = false;
            _lastMainLiveStatusError = "";
            StatusMessage = "Main live status refresh restored.";
            QueueStationStateLog("MAIN_LIVE_STATUS", "INFO", "Main live status refresh restored.");
        }

        private void ReportClockTickError(Exception exception)
        {
            var nowTimestamp = Stopwatch.GetTimestamp();
            var detail = $"{exception.GetType().Name}: {exception.Message}";
            var shouldLog = !detail.Equals(_lastClockTickError, StringComparison.Ordinal) || HasElapsed(_lastClockTickErrorReportTimestamp, nowTimestamp, ErrorReportInterval);
            StatusMessage = $"UI timer refresh failed. {detail}";
            if (!shouldLog)
            {
                return;
            }

            _lastClockTickError = detail;
            _lastClockTickErrorReportTimestamp = nowTimestamp;
            QueueStationStateLog("UI_CLOCK", "ERROR", detail);
        }

        private void QueueStationStateLog(string stateName, string action, string detail)
        {
            var logManager = _manager.Log();
            void WriteStationStateLog()
            {
                try
                {
                    logManager.WriteStationState("UI", stateName, action, detail);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Station state log failed. {exception}");
                }
            }

            _ = Task.Run(WriteStationStateLog);
        }

        private static bool HasElapsed(long lastTimestamp, long currentTimestamp, TimeSpan interval)
        {
            if (lastTimestamp <= 0)
            {
                return true;
            }

            var elapsedTicks = currentTimestamp - lastTimestamp;
            var requiredTicks = interval.TotalSeconds * Stopwatch.Frequency;
            return elapsedTicks >= requiredTicks;
        }

        private Task RefreshShellCommunicationStatus(CancellationToken cancellationToken = default)
        {
            var modules = _interfaceManager.GetCommunicationStatus(cancellationToken);
            _interfaceCommStatuses = _interfaceManager.GetInterfaceCommunicationList();
            _systemStatus = _systemStatus with
            {
                CurrentRecipeId = string.IsNullOrWhiteSpace(_selectedRecipeId) ? DefaultRecipeId : _selectedRecipeId,
                OperationMode = _operationMode,
                Modules = NormalizeCommunicationStatuses(modules)
            };
            RefreshOperationModeAccess();
            RefreshShellStatusItems();
            return Task.CompletedTask;
        }

        private async Task<ST_SYSTEM_STATUS_SNAPSHOT> GetSystemStatusSnapshot(CancellationToken cancellationToken = default)
        {
            var currentRecipeId = string.IsNullOrWhiteSpace(_selectedRecipeId) ? DefaultRecipeId : _selectedRecipeId;
            var operationMode = _operationMode;
            var pmLockState = _pmLockStatus.IsLocked ? EN_PM_LOCK_STATE.Locked : EN_PM_LOCK_STATE.Released;
            ST_SYSTEM_STATUS_SOURCE ReadSystemStatusSource()
            {
                var modules = _interfaceManager.GetCommunicationStatus(cancellationToken);
                var interfaceCommStatuses = _interfaceManager.GetInterfaceCommunicationList();
                return new ST_SYSTEM_STATUS_SOURCE(currentRecipeId, operationMode, pmLockState, NormalizeCommunicationStatuses(modules), interfaceCommStatuses);
            }

            var source = await Task.Run(ReadSystemStatusSource, cancellationToken);
            var alarms = _alarmManager.CurAlarm;
            var status = new ST_SYSTEM_STATUS(source.CurrentRecipeId, source.OperationMode, alarms.Count > 0 ? EN_ALARM_STATE.Occur : EN_ALARM_STATE.Clear, source.PmLockState, source.Modules);
            return new ST_SYSTEM_STATUS_SNAPSHOT(status, source.InterfaceCommunicationStatuses);
        }

        private void ApplySystemStatusSnapshot(ST_SYSTEM_STATUS_SNAPSHOT snapshot, bool refreshOperationModeAccess = true)
        {
            _systemStatus = snapshot.Status;
            _interfaceCommStatuses = snapshot.InterfaceCommunicationStatuses;
            if (refreshOperationModeAccess)
            {
                RefreshOperationModeAccess();
            }

            RefreshShellStatusItems();
        }

        private ST_PM_LOCK_STATUS GetPMLockStatus()
        {
            return _pmLockStatus;
        }

        private void EnterPMLock()
        {
            if (_pmLockStatus.IsLocked)
            {
                return;
            }

            _pmLockStatus = new ST_PM_LOCK_STATUS(true, DateTimeOffset.Now);
        }

        private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
        {
            var replacement = items as IReadOnlyList<T> ?? items.ToArray();
            if (target.SequenceEqual(replacement))
            {
                return;
            }

            target.Clear();
            foreach (var item in replacement)
            {
                target.Add(item);
            }
        }

        private IReadOnlyList<ST_HEADER_STATUS_ITEM> CreateHeaderStatusItems()
        {
            var status = _systemStatus;
            var alarmState = AlarmStateValue(_alarmManager.CurAlarm.Count > 0 ? EN_ALARM_STATE.Occur : EN_ALARM_STATE.Clear);
            var items = new List<ST_HEADER_STATUS_ITEM>
            {
                new("MODE", OperationModeHeaderValue(status.OperationMode, IsSimulationEnvironment), OperationModeState(IsSimulationEnvironment), OpenCommand: ToggleOperationModeCommand),
                new("RECIPE", GetHeaderRecipeId(status), "RECIPE")
            };
            global::Drilling.UI.Menu.Menus.ST_HEADER_STATUS_ITEM SelectItem(global::Drilling.Common.Interface.EN_EQP_MODULE module)
            {
                return ModuleHeader(status, module);
            }

            items.AddRange(HeaderModules.Select(SelectItem));
            items.Add(new ST_HEADER_STATUS_ITEM("ALARM", alarmState, alarmState, OpenCommand: HeaderAlarmResetCommand));
            return items;
        }

        private static bool IsHeaderContextStatusItem(ST_HEADER_STATUS_ITEM item)
        {
            return item.Name.Equals("MODE", StringComparison.OrdinalIgnoreCase) || item.Name.Equals("RECIPE", StringComparison.OrdinalIgnoreCase);
        }

        private IReadOnlyList<ST_HEADER_STATUS_ITEM> CreateFooterStatusItems(EN_MENU menu)
        {
            if (menu == EN_MENU.Manual)
            {
                return[
                    new("STATE", "Manual Setting Loaded", "SIM"), 
                    new("HEAD", $"H{_selectedHeadNo:00} Selected", "SIM"), 
                    new("SCRIPT", "Script Created", "ONLINE"), 
                    new("TASK", "Task Running", "WAIT"), 
                    new("SIM", "Simulation PASS", "ONLINE")
                ];
            }

            bool IsIsEdited(global::Drilling.UI.Menu.Menus.ST_RECIPE_PARAMETER_EDITOR_ITEM item)
            {
                return item.IsEdited;
            }

            if (menu == EN_MENU.Recipe)
            {
                var hasRecipeChanges = CurrentScreen.Recipe?.AllManagedItems.Any(IsIsEdited) == true;
                return[
                    new("STATE", hasRecipeChanges ? "Recipe Modified" : "Recipe Loaded", hasRecipeChanges ? "WAIT" : "ONLINE"), 
                    new("RECIPE", GetSelectedRecipeFileText(), "SIM"), 
                    new("SIM", "Simulation PASS", "ONLINE")
                ];
            }

            bool IsIsModified(global::Drilling.UI.Menu.Menus.ST_SYSTEM_PARAMETER_ROW item)
            {
                return item.IsModified;
            }

            if (menu == EN_MENU.Setting)
            {
                var modifiedCount = CurrentScreen.Setting?.AllParameterRows.Count(IsIsModified) ?? 0;
                return[
                    new("STATE", modifiedCount > 0 ? "SETTING Modified" : "SETTING Loaded", modifiedCount > 0 ? "WAIT" : "ONLINE"), 
                    new("GROUP", $"{_selectedSettingTab} / {_selectedSettingGroup}", "SIM"), 
                    new("SIM", "Simulation PASS", "ONLINE")
                ];
            }

            if (menu == EN_MENU.Alarm)
            {
                var alarmState = AlarmStateValue(_systemStatus.AlarmState);
                return[new("STATE", $"Alarm {alarmState}", alarmState), new("BUZZER", "Buzzer OFF", "SIM"), new("SIM", "Simulation PASS", "ONLINE")];
            }

            if (menu == EN_MENU.Monitor)
            {
                return CreateMonitorFooterStatusItems();
            }

            if (menu == EN_MENU.Review)
            {
                return[new("SCREEN", "REVIEW / INSPECTION", "SIM"), new("POINT", "Review Standby", "WAIT"), new("SIM", "Simulation PASS", "ONLINE")];
            }

            if (menu == EN_MENU.Correction)
            {
                return[new("SCREEN", "CORRECTION / OFFSET", "SIM"), new("TABLE", "Correction Standby", "WAIT"), new("SIM", "Simulation PASS", "ONLINE")];
            }

            if (menu == EN_MENU.Pm)
            {
                var isLocked = _systemStatus.PMLockState == EN_PM_LOCK_STATE.Locked;
                return[new("PM", isLocked ? "PM Lock Active" : "PM Lock Ready", isLocked ? "WARN" : "ONLINE"), new("OPERATION", isLocked ? "Operation Disabled" : "Operation Enabled", isLocked ? "OCCUR" : "ONLINE")];
            }

            return[new("SCRIPT", "Script Created", "ONLINE"), new("TASK", "Task Running", "SIM"), new("SIM", "Simulation PASS", "ONLINE"), new("MODE", "Auto Mode", "READY")];
        }

        private async Task InitializeProcessModel(CancellationToken cancellationToken)
        {
            try
            {
                await PrepareInitialProcess(cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
            {
                StatusMessage = "Initial process model preparation was canceled.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Initial process model preparation failed: {ex.Message}";
                if (ex is CReviewOffsetValidationException)
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() => CRecipeConfirmDialog.ShowWarning("Review Offset 확인", ex.Message)));
            }
        }

        private async Task PrepareInitialProcess(bool applyShiftOffset = false, CancellationToken cancellationToken = default)
        {
            await _processPreparationGate.WaitAsync(cancellationToken);
            try
            {
                var recipes = await _recipeManager.LoadRecipes(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var selectedRecipe = FindSelectedRecipe(recipes);
                var recipeId = selectedRecipe?.Id ?? (string.IsNullOrWhiteSpace(_selectedRecipeId) ? DefaultRecipeId : _selectedRecipeId);
                _selectedRecipeId = recipeId;
                if (string.IsNullOrWhiteSpace(_viewRecipeId))
                {
                    _viewRecipeId = recipeId;
                }

                var preparedAt = DateTime.Now;
                var processId = $"SCAN-{preparedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
                var activeProduct = _productManager.Current;
                var reusableProduct = activeProduct?.State == EN_PRODUCT_STATE.Completed ? null : activeProduct;
                var fallbackGlassId = $"{(IsSimulationEnvironment ? "SIM" : "DUMMY")}_{preparedAt:yyyyMMdd_HHmmssfff}";
                var glassId = string.IsNullOrWhiteSpace(reusableProduct?.GlassId) ? fallbackGlassId : reusableProduct.GlassId;
                var lotId = reusableProduct?.LotId?.Trim() ?? string.Empty;
                var shiftOffset = applyShiftOffset && IsManualStartEligible() ? ReadShiftOffset() : (X: 0.0, Y: 0.0);
                var parameters = CreateProcessParameters(selectedRecipe, glassId, lotId, shiftOffset.X, shiftOffset.Y);
                _stationManager.PrepareProcess(processId, recipeId, glassId, lotId, parameters, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                StatusMessage = selectedRecipe is null ? "Process model prepared with fallback data." : $"Process model prepared from {recipeId}.csv.";
                await RefreshCurrentScreen();
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                _processPreparationGate.Release();
            }
        }

        private ST_RECIPE_DEFINITION? FindSelectedRecipe(IReadOnlyList<ST_RECIPE_DEFINITION> recipes)
        {
            if (recipes.Count == 0)
            {
                return null;
            }

            bool MatchesItem(global::Drilling.Common.Managers.ST_RECIPE_DEFINITION recipe)
            {
                return recipe.Id.Equals(_selectedRecipeId, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(_selectedRecipeId))
            {
                var selectedRecipe = recipes.FirstOrDefault(MatchesItem);
                if (selectedRecipe is not null)
                {
                    return selectedRecipe;
                }
            }

            bool MatchesItem2(global::Drilling.Common.Managers.ST_RECIPE_DEFINITION recipe)
            {
                return recipe.Id.Equals(DefaultRecipeId, StringComparison.OrdinalIgnoreCase);
            }

            return recipes.FirstOrDefault(MatchesItem2) ?? recipes[0];
        }

        private static IReadOnlyDictionary<string, string> CreateProcessParameters(ST_RECIPE_DEFINITION? recipe, string glassId, string lotId, double shiftOffsetX, double shiftOffsetY)
        {
            bool HasRecipeParameterKey(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return !string.IsNullOrWhiteSpace(parameter.Key);
            }

            string SelectKey(global::Drilling.Common.Managers.ST_RECIPE_PARAMETER parameter)
            {
                return parameter.Key;
            }

            string SelectRecipeParameterGroupKey(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_PARAMETER> group)
            {
                return group.Key;
            }

            string SelectValue(global::System.Linq.IGrouping<string, global::Drilling.Common.Managers.ST_RECIPE_PARAMETER> group)
            {
                return group.Last().Value;
            }

            var parameters = recipe?.Parameters.Where(HasRecipeParameterKey).GroupBy(SelectKey, StringComparer.OrdinalIgnoreCase).ToDictionary(SelectRecipeParameterGroupKey, SelectValue, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            parameters["GlassId"] = glassId;
            parameters["LotId"] = lotId;
            parameters[ShiftOffsetXKey] = shiftOffsetX.ToString("0.000000", CultureInfo.InvariantCulture);
            parameters[ShiftOffsetYKey] = shiftOffsetY.ToString("0.000000", CultureInfo.InvariantCulture);
            parameters.TryAdd("DIAMETER", "0.350");
            parameters.TryAdd("HEAD_COUNT", "8");
            return parameters;
        }

        private (double X, double Y) ReadShiftOffset()
        {
            return (ReadShiftOffsetValue(ShiftOffsetX), ReadShiftOffsetValue(ShiftOffsetY));
        }

        public async Task OpenShiftOffsetDialog(Window? owner)
        {
            try
            {
                var recipes = await _recipeManager.LoadRecipes();
                var selectedRecipe = FindSelectedRecipe(recipes);
                var baseParameters = CreateShiftOffsetPreviewParameters(selectedRecipe);
                IReadOnlyList<ST_SHIFT_OFFSET_HEAD_ROW> BuildRows(double shiftOffsetX, double shiftOffsetY)
                {
                    return BuildShiftOffsetHeadRows(baseParameters, shiftOffsetX, shiftOffsetY);
                }

                string? ValidateApply(double shiftOffsetX, double shiftOffsetY)
                {
                    return ValidateShiftOffsetApply(baseParameters, shiftOffsetX, shiftOffsetY);
                }

                var dialog = new CShiftOffsetDialog(ShiftOffsetX, ShiftOffsetY, BuildRows, ValidateApply)
                {
                    Owner = owner
                };
                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                ShiftOffsetX = dialog.ResultShiftOffsetX;
                ShiftOffsetY = dialog.ResultShiftOffsetY;
            }
            catch (Exception ex)
            {
                CRecipeConfirmDialog.ShowWarning("Shift Offset 설정 실패", ex.Message);
            }
        }

        private IReadOnlyDictionary<string, string> CreateShiftOffsetPreviewParameters(ST_RECIPE_DEFINITION? recipe)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (recipe is not null)
            {
                foreach (var parameter in recipe.Parameters)
                {
                    if (!string.IsNullOrWhiteSpace(parameter.Key))
                    {
                        parameters[parameter.Key] = parameter.Value;
                    }
                }
            }

            foreach (var setting in _manager.Setting().LoadSection(EN_SETTING_TAB.Option))
            {
                if (!string.IsNullOrWhiteSpace(setting.Key))
                {
                    parameters[setting.Key] = setting.Value;
                }
            }

            foreach (var setting in _manager.Setting().LoadSection(EN_SETTING_TAB.Motor))
            {
                if (!string.IsNullOrWhiteSpace(setting.Key))
                {
                    parameters[setting.Key] = setting.Value;
                }
            }

            parameters.TryAdd("HEAD_COUNT", "8");
            return parameters;
        }

        private static IReadOnlyList<ST_SHIFT_OFFSET_HEAD_ROW> BuildShiftOffsetHeadRows(IReadOnlyDictionary<string, string> parameters, double shiftOffsetX, double shiftOffsetY)
        {
            var headCount = Math.Clamp(ReadShiftOffsetPreviewInt(parameters, 8, "HEAD_COUNT"), 1, 8);
            var basePlan = CShotCoordinatePlanBuilder.Build(CreateShiftOffsetParameters(parameters, 0.0, 0.0));
            var shiftedPlan = CShotCoordinatePlanBuilder.Build(CreateShiftOffsetParameters(parameters, shiftOffsetX, shiftOffsetY));
            var baseShots = basePlan.Shots.ToDictionary(CreateShiftOffsetShotKey, StringComparer.OrdinalIgnoreCase);
            var rows = new List<ST_SHIFT_OFFSET_HEAD_ROW>(headCount);
            for (var headNo = 1; headNo <= headCount; headNo++)
            {
                var shiftedShot = shiftedPlan.Shots.FirstOrDefault(shot => shot.HeadNo == headNo);
                if (shiftedShot is null || !baseShots.TryGetValue(CreateShiftOffsetShotKey(shiftedShot), out var baseShot))
                {
                    rows.Add(new ST_SHIFT_OFFSET_HEAD_ROW(headNo, $"H{headNo:00}", "-", "-"));
                    continue;
                }

                rows.Add(new ST_SHIFT_OFFSET_HEAD_ROW(headNo, $"H{headNo:00}", FormatShiftOffsetPreviewValue(shiftedShot.ScannerGx - baseShot.ScannerGx), FormatShiftOffsetPreviewValue(shiftedShot.ScannerGy - baseShot.ScannerGy)));
            }

            return rows;
        }

        private static Dictionary<string, string> CreateShiftOffsetParameters(IReadOnlyDictionary<string, string> source, double shiftOffsetX, double shiftOffsetY)
        {
            return new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase)
            {
                [ShiftOffsetXKey] = shiftOffsetX.ToString("0.000000", CultureInfo.InvariantCulture),
                [ShiftOffsetYKey] = shiftOffsetY.ToString("0.000000", CultureInfo.InvariantCulture)
            };
        }

        private static string CreateShiftOffsetShotKey(ST_HEAD_SHOT_POINT shot)
        {
            return $"{shot.HeadNo}|{shot.CellNo}|{shot.ShotKey}";
        }

        private static string? ValidateShiftOffsetApply(IReadOnlyDictionary<string, string> baseParameters, double shiftOffsetX, double shiftOffsetY)
        {
            const double tolerance = 0.000001;
            var parameters = CreateShiftOffsetParameters(baseParameters, shiftOffsetX, shiftOffsetY);
            var coordinatePlan = CShotCoordinatePlanBuilder.Build(parameters);
            var maskingPlan = CRecipeShotMaskingPlan.Build(parameters);
            var glassSizeX = coordinatePlan.GlassSizeX;
            var glassSizeY = coordinatePlan.GlassSizeY;
            var scannerReferenceOffsetX = ReadShiftOffsetScannerReferenceOffsetX(parameters);
            var issues = new List<string>();
            var totalIssueCount = 0;
            foreach (var shot in coordinatePlan.Shots)
            {
                if (maskingPlan.EvaluateShot(shot).ShouldSkipLaser)
                {
                    continue;
                }

                var shiftedGlassX = shot.DesignX + shiftOffsetX;
                var shiftedGlassY = shot.DesignY - shiftOffsetY;
                if (IsOutsideRange(shiftedGlassX, 0.0, glassSizeX, tolerance) || IsOutsideRange(shiftedGlassY, 0.0, glassSizeY, tolerance))
                {
                    totalIssueCount++;
                    AddShiftOffsetIssue(issues, $"{FormatShiftOffsetShot(shot)}: Glass 영역 밖 X {FormatShiftOffsetPreviewValue(shiftedGlassX)} / Y {FormatShiftOffsetPreviewValue(shiftedGlassY)}");
                    continue;
                }

                var headCenterX = ReadShiftOffsetHeadCenterX(parameters, shot.HeadNo);
                var halfScanFieldX = ReadShiftOffsetHeadScanFieldHalfWidth(parameters, shot.HeadNo);
                var shiftedReferenceX = shiftedGlassX + scannerReferenceOffsetX;
                if (IsOutsideRange(shiftedReferenceX, headCenterX - halfScanFieldX, headCenterX + halfScanFieldX, tolerance))
                {
                    totalIssueCount++;
                    AddShiftOffsetIssue(issues, $"{FormatShiftOffsetShot(shot)}: H{shot.HeadNo:00} Scan Field X 초과 ({FormatShiftOffsetPreviewValue(shiftedReferenceX)})");
                }
            }

            if (totalIssueCount == 0)
            {
                return null;
            }

            var hiddenCount = totalIssueCount - issues.Count;
            var message = "Shift Offset 적용 시 일부 Laser ON 샷이 가공 가능 영역을 벗어납니다.\n" + "값을 적용하지 않았습니다.\n\n" + string.Join("\n", issues);
            if (hiddenCount > 0)
            {
                message += $"\n... 외 {hiddenCount:N0}건";
            }

            return message;
        }

        private static void AddShiftOffsetIssue(ICollection<string> issues, string issue)
        {
            if (issues.Count < 8)
            {
                issues.Add(issue);
            }
        }

        private static bool IsOutsideRange(double value, double minimum, double maximum, double tolerance)
        {
            return value < minimum - tolerance || value > maximum + tolerance;
        }

        private static string FormatShiftOffsetShot(ST_HEAD_SHOT_POINT shot)
        {
            return $"H{shot.HeadNo:00} CELL{shot.CellNo} {shot.ShotKey}";
        }

        private static double ReadShiftOffsetHeadCenterX(IReadOnlyDictionary<string, string> parameters, int headNo)
        {
            const double fallbackHead1PositionX = -5.0;
            const double fallbackHeadGapX = 200.0;
            var head1PositionX = ReadShiftOffsetPreviewDouble(parameters, fallbackHead1PositionX, "H01_AK_POSITION_X");
            var headGapX = ReadShiftOffsetPreviewDouble(parameters, fallbackHeadGapX, "HeadGapX");
            var akMarginX = ReadShiftOffsetPreviewDouble(parameters, 0.0, "AK_MARGIN_X");
            return akMarginX + head1PositionX + ((headNo - 1) * headGapX);
        }

        private static double ReadShiftOffsetHeadScanFieldHalfWidth(IReadOnlyDictionary<string, string> parameters, int headNo)
        {
            var width = Math.Abs(ReadShiftOffsetPreviewDouble(parameters, 110.0, $"H{headNo:00}_SCAN_FIELD_WIDTH_X", $"H{headNo:00}_HEAD_FIELD_WIDTH_X"));
            if (width <= 0.000001)
            {
                width = 110.0;
            }

            return width / 2.0;
        }

        private static double ReadShiftOffsetScannerReferenceOffsetX(IReadOnlyDictionary<string, string> parameters)
        {
            var escEdgeToGlassEdge = ReadShiftOffsetPreviewDouble(parameters, 0.0, "ESC_EDGE_TO_GLASS_EDGE", "ESC_EDGE_TO_GLASS_EDGE_X");
            var glassCenterToScannerCenter = ReadShiftOffsetPreviewDouble(parameters, 0.0, "GLASS_CENTER_TO_SCANNER_CENTER", "GLASS_CENTER_TO_SCANNER_CENTER_X");
            return escEdgeToGlassEdge + glassCenterToScannerCenter;
        }

        private static int ReadShiftOffsetPreviewInt(IReadOnlyDictionary<string, string> parameters, int defaultValue, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (parameters.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }

            return defaultValue;
        }

        private static double ReadShiftOffsetPreviewDouble(IReadOnlyDictionary<string, string> parameters, double defaultValue, params string[] keys)
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

        private static string FormatShiftOffsetPreviewValue(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static double ReadShiftOffsetValue(string value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0.0;
        }

        private static string NormalizeShiftOffsetText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "0";
            }

            return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed.ToString("0.###", CultureInfo.InvariantCulture) : value.Trim();
        }

        private async Task StartCycle()
        {
            if (!CheckHeavyAlarmForOperation("START"))
                return;
            if (!CheckStageMotionOwnership((title, message) => CRecipeConfirmDialog.ShowWarning(title, message), "START"))
                return;
            var recipeFileText = GetSelectedRecipeFileText();
            var confirmShiftOffset = IsManualStartEligible() ? ReadShiftOffset() : (X: 0.0, Y: 0.0);
            var confirmMessage = $"현재 레시피 : {recipeFileText}\n" + $"Shift Offset X : {confirmShiftOffset.X.ToString("0.###", CultureInfo.InvariantCulture)} mm\n" + $"Shift Offset Y : {confirmShiftOffset.Y.ToString("0.###", CultureInfo.InvariantCulture)} mm\n" + "Start 하시겠습니까?";
            if (!CRecipeConfirmDialog.ShowConfirmation("공정 시작", confirmMessage))
            {
                return;
            }
            if (!CheckStageMotionOwnership((title, message) => CRecipeConfirmDialog.ShowWarning(title, message), "START"))
                return;

            if (_autoCycleState != EN_AUTO_CYCLE_STATE.Ready)
            {
                StatusMessage = "Semi Auto Run is already running or stopping.";
                return;
            }

            if (!IsManualStartEligible())
            {
                StatusMessage = "Main START is available in MANUAL mode only.";
                CRecipeConfirmDialog.ShowWarning("공정 시작 불가", "MAIN START는 MANUAL 모드에서만 사용할 수 있습니다.\nAUTO 모드는 Stage PC 요청을 대기합니다.");
                return;
            }

            var cycleCts = new CancellationTokenSource();
            var cycleCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _autoCycleCts = cycleCts;
            _autoCycleCompletion = cycleCompletion;
            SetAutoCycleState(EN_AUTO_CYCLE_STATE.Preparing);
            try
            {
                await WaitForInitialProcessModelPreparation(cycleCts.Token);
                await PrepareInitialProcess(applyShiftOffset: true, cancellationToken: cycleCts.Token);
                if (!IsManualStartEligible())
                {
                    StatusMessage = "Main START was canceled because AUTO mode was selected.";
                    return;
                }
                if (!CheckStageMotionOwnership((title, message) => CRecipeConfirmDialog.ShowWarning(title, message), "START"))
                    return;

                cycleCts.Token.ThrowIfCancellationRequested();
                SetAutoCycleState(EN_AUTO_CYCLE_STATE.Running);
                _stationManager.Start(cycleCts.Token);
                var finalStatus = await WaitForStationCycleFinished(cycleCts.Token);
                cycleCts.Token.ThrowIfCancellationRequested();
                if (finalStatus.State == EN_STATION_STATE.Alarm || finalStatus.SequenceState == EN_STATION_SEQUENCE.Error)
                {
                    StatusMessage = $"Semi Auto Run failed: {finalStatus.LastMessage}";
                    if (finalStatus.LastMessage.Contains("Glass 정보를 생성하지 못해", StringComparison.Ordinal))
                    {
                        CRecipeConfirmDialog.ShowWarning("공정 시작 불가", finalStatus.LastMessage);
                    }

                    return;
                }

                if (finalStatus.State == EN_STATION_STATE.Stopped || finalStatus.SequenceState == EN_STATION_SEQUENCE.Stop)
                {
                    StatusMessage = "Semi Auto Run stopped.";
                    return;
                }

                StatusMessage = IsSimulationEnvironment ? "Cycle completed in simulation mode." : "Cycle completed.";
                SetAutoCycleState(EN_AUTO_CYCLE_STATE.Ready);
                await RefreshCurrentScreen();
            }
            catch (OperationCanceledException)when (cycleCts.IsCancellationRequested)
            {
                StatusMessage = "Semi Auto Run was canceled by Stop.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Semi Auto Run failed: {ex.Message}";
                var message = ex.Message.StartsWith("공정 시작에 실패했습니다.", StringComparison.Ordinal) ? ex.Message : $"공정 시작에 실패했습니다.\n{ex.Message}";
                CRecipeConfirmDialog.ShowWarning("공정 시작 오류", message);
            }
            finally
            {
                if (ReferenceEquals(_autoCycleCts, cycleCts))
                {
                    _autoCycleCts = null;
                }

                cycleCts.Dispose();
                if (_autoCycleState is not EN_AUTO_CYCLE_STATE.Stopping)
                {
                    SetAutoCycleState(EN_AUTO_CYCLE_STATE.Ready);
                }

                cycleCompletion.TrySetResult(true);
                if (ReferenceEquals(_autoCycleCompletion, cycleCompletion))
                {
                    _autoCycleCompletion = null;
                }
            }
        }

        private async Task StopCycle()
        {
            if (_operationMode == EN_SYSTEM_MODE.Auto && (_stationManager.StageRequests.IsRunning || _stationManager.StageRequests.NeedsCleanup))
            {
                StatusMessage = await _stationManager.StageRequests.StopAsync() ? "Stage sequence stopped. Switch MANUAL → AUTO to resume request monitoring." : "Stage operation is still stopping. Restart remains blocked.";
                StopCommand.NotifyCanExecuteChanged();
                await RefreshCurrentScreen();
                return;
            }

            if (!CanStopAutoCycle())
            {
                return;
            }

            SetAutoCycleState(EN_AUTO_CYCLE_STATE.Stopping);
            var cycleCts = _autoCycleCts;
            var cycleCompletion = _autoCycleCompletion;
            try
            {
                try
                {
                    _stationManager.Stop();
                }
                finally
                {
                    cycleCts?.Cancel();
                }

                await WaitForStationCycleFinished(AutoCycleStopTimeout);
                if (cycleCompletion is not null)
                {
                    await cycleCompletion.Task.WaitAsync(AutoCycleStopTimeout);
                }

                StatusMessage = "Cycle stopped.";
                SetAutoCycleState(EN_AUTO_CYCLE_STATE.Ready);
            }
            catch (TimeoutException)
            {
                StatusMessage = "Stop confirmation timed out. Start has been re-enabled. Verify station status before restarting.";
                SetAutoCycleState(EN_AUTO_CYCLE_STATE.Ready);
                _ = CompleteStopAfterTimeout(cycleCompletion);
            }
            catch (Exception ex)
            {
                SetAutoCycleState(EN_AUTO_CYCLE_STATE.Ready);
                StatusMessage = $"Cycle stop failed: {ex.Message}";
                MessageBox.Show(Application.Current?.MainWindow, $"정지 명령에 실패했습니다. Start는 다시 활성화됩니다. 재시작 전 장비 상태를 확인하세요.\n{ex.Message}", "정지 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            if (_autoCycleState == EN_AUTO_CYCLE_STATE.Ready)
            {
                try
                {
                    await RefreshCurrentScreen();
                }
                catch (Exception ex)
                {
                    if (_autoCycleState == EN_AUTO_CYCLE_STATE.Ready)
                    {
                        StatusMessage = $"Cycle state updated, but screen refresh failed: {ex.Message}";
                    }
                }
            }
        }

        private async Task CompleteStopAfterTimeout(TaskCompletionSource<bool>? cycleCompletion)
        {
            try
            {
                await WaitForStationCycleFinished(CancellationToken.None);
                if (cycleCompletion is not null)
                {
                    await cycleCompletion.Task;
                }

                StatusMessage = "Cycle stop and termination confirmed.";
                SetAutoCycleState(EN_AUTO_CYCLE_STATE.Ready);
            }
            catch (Exception ex)
            {
                SetAutoCycleState(EN_AUTO_CYCLE_STATE.Ready);
                StatusMessage = $"Stop confirmation failed: {ex.Message}";
            }
            finally
            {
                try
                {
                    await RefreshCurrentScreen();
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Stop confirmation completed, but screen refresh failed: {ex.Message}";
                }
            }
        }

        private async Task<ST_STATION_STATUS> WaitForStationCycleFinished(TimeSpan timeout)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            try
            {
                return await WaitForStationCycleFinished(timeoutCts.Token);
            }
            catch (OperationCanceledException)when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException("Station sequence did not reach a terminal state within the timeout.");
            }
        }

        private async Task<ST_STATION_STATUS> WaitForStationCycleFinished(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = _stationManager.GetStationStatus(CancellationToken.None).FirstOrDefault();
                if (IsStationCycleFinished(status))
                {
                    return status!;
                }

                await Task.Delay(200, cancellationToken);
            }
        }

        private static bool IsStationCycleFinished(ST_STATION_STATUS? status)
        {
            if (status is null)
            {
                return false;
            }

            if (status.SequenceState == EN_STATION_SEQUENCE.Ready)
            {
                return true;
            }

            return status.State is EN_STATION_STATE.Complete or EN_STATION_STATE.Stopped or EN_STATION_STATE.Alarm;
        }

        private void SetAutoCycleState(EN_AUTO_CYCLE_STATE state)
        {
            if (_autoCycleState == state)
            {
                return;
            }

            _autoCycleState = state;
            RefreshOperationModeAccess();
            NotifyOperationCommandStates();
        }

        private void NotifyOperationCommandStates()
        {
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            ToggleOperationModeCommand.NotifyCanExecuteChanged();
        }

        private async Task SelectHead(object? parameter)
        {
            var headNo = parameter switch
            {
                int value => value,
                string text when int.TryParse(text, out var parsed) => parsed,
                ST_HEAD_PREVIEW head => head.HeadNo,
                _ => _selectedHeadNo
            };
            if (headNo <= 0)
            {
                return;
            }

            if (!IsHeadEnabled(headNo))
            {
                StatusMessage = $"HEAD {headNo:00} is disabled by H{headNo:00}_USE.";
                RefreshShellStatusItems();
                await RefreshCurrentScreen();
                return;
            }

            _selectedHeadNo = headNo;
            StatusMessage = $"HEAD {headNo:00} selected. Preview updated.";
            RefreshShellStatusItems();
            await RefreshCurrentScreen();
        }

        private bool CanSelectHeadParameter(object? parameter)
        {
            var headNo = parameter switch
            {
                int value => value,
                string text when int.TryParse(text, out var parsed) => parsed,
                ST_HEAD_PREVIEW head => head.HeadNo,
                _ => _selectedHeadNo
            };
            return headNo > 0 && IsHeadEnabled(headNo);
        }

        private bool IsHeadEnabled(int headNo)
        {
            if (headNo is < 1 or > 8)
            {
                return false;
            }

            try
            {
                var settings = _manager.Setting().LoadSection(EN_SETTING_TAB.Option, CancellationToken.None);
                return CScannerStatusSnapshotReader.IsHeadEnabled(settings, headNo);
            }
            catch (Exception exception)when (exception is InvalidDataException or IOException)
            {
                return true;
            }
        }

        private void StartClock()
        {
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += async (_, _) =>
            {
                try
                {
                    CurrentDateText = DateTime.Now.ToString("yyyy-MM-dd");
                    CurrentTimeText = DateTime.Now.ToString("HH:mm:ss");
                    if (_operationMode == EN_SYSTEM_MODE.Auto)
                    {
                        var stage = _stationManager.StageRequests.Status;
                        StatusMessage = $"AUTO / {stage.State}: {stage.Detail}";
                        StopCommand.NotifyCanExecuteChanged();
                    }

                    var nowTimestamp = Stopwatch.GetTimestamp();
                    var monitorRefreshInterval = MonitorDefaultRefreshInterval;
                    var shouldRefreshMonitorScreen = SelectedMenu.Menu == EN_MENU.Monitor && HasElapsed(_lastMonitorScreenRefreshTimestamp, nowTimestamp, monitorRefreshInterval);
                    var shouldRefreshManualScannerStatus = SelectedMenu.Menu == EN_MENU.Manual && CurrentScreen.Manual is not null;
                    var shouldRefreshMainLiveStatus = SelectedMenu.Menu == EN_MENU.Main && HasElapsed(_lastMainLiveStatusRefreshTimestamp, nowTimestamp, MainLiveStatusRefreshInterval);
                    if (shouldRefreshMonitorScreen)
                    {
                        _lastMonitorScreenRefreshTimestamp = nowTimestamp;
                        await RefreshMonitorLiveData();
                        return;
                    }

                    if (shouldRefreshManualScannerStatus)
                    {
                        RefreshManualScannerStatus();
                        await RefreshSystemStatus();
                        return;
                    }

                    if (shouldRefreshMainLiveStatus)
                    {
                        _lastMainLiveStatusRefreshTimestamp = nowTimestamp;
                        await RefreshMainLiveStatus();
                        await RefreshSystemStatus();
                        return;
                    }

                    await RefreshSystemStatus();
                }
                catch (Exception exception)
                {
                    ReportClockTickError(exception);
                }
            };
            timer.Start();
        }

        private IReadOnlyDictionary<EN_MENU, CMenuBase> CreateMenus(
            CStationManager stationManager, 
            CInterfaceManager interfaceManager, 
            CMotionManager motionManager, 
            CAlarmManager alarmManager, 
            CInterLockManager interLockManager, 
            CManualScanFileBase manualScanFile, 
            CRecipeManager recipeManager, 
            CSettingManager settingManager, 
            CProductManager productManager, 
            CReviewManager reviewManager, 
            CAutomationScriptFileBase automationScriptFile
        )
        {
            string SelectSelectedRecipeId()
            {
                return _selectedRecipeId;
            }

            void SetMainStatusMessage(string message)
            {
                StatusMessage = message;
            }

            int SelectSelectedHeadNo()
            {
                return _selectedHeadNo;
            }

            string SelectSelectedManualSettingName()
            {
                return _selectedManualSettingName;
            }

            void SetSelectedManualSettingName(string value)
            {
                _selectedManualSettingName = value;
            }

            void SetManualStatusMessage(string message)
            {
                StatusMessage = message;
            }

            string SelectSelectedRecipeId2()
            {
                return _selectedRecipeId;
            }

            void SetSelectedRecipeId(string value)
            {
                _selectedRecipeId = NormalizeRecipeId(value);
            }

            string SelectViewRecipeId()
            {
                return _viewRecipeId;
            }

            void SetViewRecipeId(string value)
            {
                _viewRecipeId = NormalizeRecipeId(value);
            }

            global::Drilling.UI.Menu.Menus.CMenuRecipe? SelectRecipe()
            {
                return CurrentScreen.Recipe;
            }

            string SelectSelectedRecipeCategory()
            {
                return _selectedRecipeCategory;
            }

            void SetSelectedRecipeCategory(string value)
            {
                _selectedRecipeCategory = value;
            }

            void SetRecipeStatusMessage(string message)
            {
                StatusMessage = message;
            }

            void ShowRecipeLoadingScreen(global::Drilling.UI.Menu.Menus.EN_MENU menu, string title)
            {
                CurrentScreen = CreateLoadingScreen(menu, title);
            }

            string SelectSelectedSettingTab()
            {
                return _selectedSettingTab;
            }

            void SetSelectedSettingTab(string value)
            {
                _selectedSettingTab = value;
            }

            string SelectSelectedSettingGroup()
            {
                return _selectedSettingGroup;
            }

            void SetSelectedSettingGroup(string value)
            {
                _selectedSettingGroup = value;
            }

            global::Drilling.UI.Menu.Menus.CMenuSetting? SelectSetting()
            {
                return CurrentScreen.Setting;
            }

            void SetSettingStatusMessage(string message)
            {
                StatusMessage = message;
            }

            void ShowSettingLoadingScreen(global::Drilling.UI.Menu.Menus.EN_MENU menu, string title)
            {
                CurrentScreen = CreateLoadingScreen(menu, title);
            }

            void SetAlarmStatusMessage(string message)
            {
                StatusMessage = message;
            }

            string SelectSelectedRecipeId3()
            {
                return _selectedRecipeId;
            }

            string SelectSelectedMonitorTab()
            {
                return _selectedMonitorTab;
            }

            void SetSelectedMonitorTab(string value)
            {
                _selectedMonitorTab = NormalizeMonitorTab(value);
            }

            void SetMonitorStatusMessage(string message)
            {
                StatusMessage = message;
            }

            bool CanExecuteMonitorWriteCommand()
            {
                return _operationMode == EN_SYSTEM_MODE.Manual;
            }

            string SelectSelectedRecipeId4()
            {
                return _selectedRecipeId;
            }

            void SetReviewStatusMessage(string message)
            {
                StatusMessage = message;
            }

            void RefreshMenuAccessAfterReview()
            {
                _ = RefreshCurrentScreen();
            }

            string SelectSelectedRecipeId5()
            {
                return _selectedRecipeId;
            }

            void SetCorrectionStatusMessage(string message)
            {
                StatusMessage = message;
            }

            var correctionMenu = new CMenuCorrection(
                _manager.ReviewResultFile(), 
                _manager.ReviewOffsetFile(), 
                reviewManager, 
                recipeManager, 
                settingManager, 
                SelectSelectedRecipeId5, 
                SetCorrectionStatusMessage, 
                RefreshCurrentScreen, 
                interfaceManager, 
                new Drilling.File.Correction.CAlignOffsetLogFile(_manager.ConfigRoot)
            );
            bool IsCorrectionMenuItem(CMenuItem menu)
            {
                return menu.Menu == EN_MENU.Correction;
            }

            void OpenCorrectionReview()
            {
                SelectedMenu = Menus.First(IsCorrectionMenuItem);
                if (SelectedMenu.Menu == EN_MENU.Correction)
                    correctionMenu.SelectTabCommand.Execute("REVIEW");
            }

            var manualReview = new CMenuReview(
                reviewManager, 
                recipeManager, 
                stationManager, 
                productManager, 
                SelectSelectedRecipeId4, 
                SetReviewStatusMessage, 
                RefreshMenuAccessAfterReview, 
                ApplyMenuAccessForOperationMode, 
                interfaceManager, 
                settingManager, 
                OpenCorrectionReview
            );
            CMenuBase[] menus = [
                new CMenuMain(stationManager, interfaceManager, _manager.Automation(), recipeManager, settingManager, reviewManager, SelectSelectedRecipeId, SetMainStatusMessage, RefreshCurrentScreen, _manager.PowerMeasure), 
                new CMenuManual(
                _manager, 
                manualScanFile, 
                automationScriptFile, 
                SelectSelectedHeadNo, 
                SelectSelectedRecipeId2, 
                SelectSelectedManualSettingName, 
                SetSelectedManualSettingName, 
                SelectHeadCommand, 
                SetManualStatusMessage, 
                RefreshShellStatusItems, 
                RefreshCurrentScreen, 
                ReadShiftOffset
            ), 
                new CMenuRecipe(
                recipeManager, 
                settingManager, 
                SelectSelectedRecipeId2, 
                SetSelectedRecipeId, 
                SelectViewRecipeId, 
                SetViewRecipeId, 
                SelectSelectedRecipeCategory, 
                SetSelectedRecipeCategory, 
                SelectRecipe, 
                SetRecipeStatusMessage, 
                ShowRecipeLoadingScreen, 
                RefreshShellStatusItems, 
                RefreshCurrentScreen
            ), 
                new CMenuSetting(
                settingManager, 
                SelectSelectedSettingTab, 
                SetSelectedSettingTab, 
                SelectSelectedSettingGroup, 
                SetSelectedSettingGroup, 
                SelectSetting, 
                SetSettingStatusMessage, 
                ShowSettingLoadingScreen, 
                RefreshShellStatusItems, 
                RefreshCurrentScreen
            ), 
                new CMenuAlarm(alarmManager, stationManager, SetAlarmStatusMessage, RefreshShellStatusItems, RefreshCurrentScreen), 
                new CMenuMonitor(
                interfaceManager, 
                motionManager, 
                interLockManager, 
                productManager, 
                recipeManager, 
                settingManager, 
                SelectSelectedRecipeId3, 
                SelectSelectedMonitorTab, 
                SetSelectedMonitorTab, 
                SetMonitorStatusMessage, 
                RefreshShellStatusItems, 
                RefreshCurrentScreen, 
                CanExecuteMonitorWriteCommand, 
                _manager.Log().LogDirectory
            ), 
                correctionMenu, 
                new CMenuPm(GetPMLockStatus, EnterPMLock)
            ];
            global::Drilling.UI.Menu.Menus.EN_MENU SelectMenu(global::Drilling.UI.Menu.Menus.CMenuBase menu)
            {
                return menu.Menu;
            }

            menus.OfType<CMenuManual>().Single().ConfigureReview(manualReview);
            foreach (var menu in menus)
                menu.OperationAllowed = CheckHeavyAlarmForOperation;
            manualReview.OperationAllowed = CheckHeavyAlarmForOperation;
            var mainStatus = menus.OfType<CMenuMain>().Single();
            menus.OfType<CMenuManual>().Single().ConfigureAutomationStatusReader(mainStatus.ReadAutomationControllerStatusSnapshot);
            return menus.ToDictionary(SelectMenu);
        }

        private static CScreenViewModel CreateLoadingScreen(EN_MENU menu, string title)
        {
            return new CScreenViewModel(menu, title, "Loading screen state.", [], []);
        }

        private string GetHeaderRecipeId(ST_SYSTEM_STATUS status)
        {
            return string.IsNullOrWhiteSpace(_selectedRecipeId) ? status.CurrentRecipeId : _selectedRecipeId;
        }

        private void SyncSelectedManualSettingFromScreen()
        {
            var settingName = CurrentScreen.Manual?.LoadedSettingName?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(settingName))
            {
                _selectedManualSettingName = settingName;
            }
        }

        private void SyncSelectedSettingSelectionFromScreen()
        {
            if (CurrentScreen.Setting is null)
            {
                return;
            }

            _selectedSettingTab = CurrentScreen.Setting.SelectedTab;
            _selectedSettingGroup = CurrentScreen.Setting.SelectedGroup;
        }

        private string GetSelectedRecipeFileText()
        {
            if (CurrentScreen.Recipe is not null)
            {
                return CurrentScreen.Recipe.RecipeSelectionSummaryText;
            }

            return string.IsNullOrWhiteSpace(_selectedRecipeId) ? "-" : $"{_selectedRecipeId}.csv";
        }

        private static string NormalizeRecipeId(string value)
        {
            return Path.GetFileNameWithoutExtension((value ?? "").Trim());
        }

        private IReadOnlyList<ST_HEADER_STATUS_ITEM> CreateMonitorFooterStatusItems()
        {
            return _selectedMonitorTab switch
            {
                "XPS" => [new("SCREEN", "MONITOR / SCANNER TILT", "SIM"), new("AXIS", "Selected Axis GX", "SIM"), new("SIM", "Simulation PASS", "ONLINE")],
                "LASER" => [new("SCREEN", "MONITOR / LASER/POWERMETER", "SIM"), new("LASER", "Laser SAFE", "ONLINE"), new("SIM", "Simulation PASS", "ONLINE")],
                "ATTENUATOR" => [new("SCREEN", "MONITOR / ATTENUATOR", "SIM"), new("POSITION", "Position 55.000", "WARN"), new("SIM", "Simulation PASS", "ONLINE")],
                "BET" => [new("SCREEN", "MONITOR / BET", "SIM"), new("BET", "MAG 1.000 / DIV 1.000", "ONLINE"), new("SIM", "Simulation PASS", "ONLINE")],
                "POWER METER" => [new("SCREEN", "MONITOR / POWERMETER", "SIM"), new("POWER", "Power Meter", "ONLINE"), new("SIM", "Simulation PASS", "ONLINE")],
                "PICO MOTOR" => [new("SCREEN", "MONITOR / DOE TILT", "SIM"), new("MOTOR", "PicoMotor", "ONLINE"), new("SIM", "Simulation PASS", "ONLINE")],
                "PRODUCT" => [new("SCREEN", "MONITOR / PRODUCT", "SIM"), new("PRODUCT", "Product Tracking", "ONLINE"), new("SIM", "Simulation PASS", "ONLINE")],
                "MELSEC" => [new("SCREEN", "MONITOR / MELSEC", "SIM"), new("PLC", "Read / Write Map", "WARN"), new("SIM", "Simulation PASS", "ONLINE")],
                "COORDINATE VIEWER" => [new("SCREEN", "MONITOR / COORDINATE VIEWER", "SIM"), new("VIEWER", "Coordinate Viewer", "SIM"), new("SIM", "Simulation PASS", "ONLINE")],
                _ => [new("SCREEN", $"MONITOR / {_selectedMonitorTab}", "SIM"), new("CONTROL", "Status Monitor", "WARN"), new("SIM", "Simulation PASS", "ONLINE")]
            };
        }

        private static string NormalizeMonitorTab(string tab)
        {
            var normalized = string.IsNullOrWhiteSpace(tab) ? "PRODUCT" : tab.Trim().ToUpperInvariant();
            return normalized switch
            {
                "ATT" => "ATTENUATOR",
                "MOTOR" or "XPS" or "SCANNERTILT" or "SCANNER TILT" or "SCANNER_TILT" => "XPS",
                "POWER" or "POWERMETER" or "POWER_METER" or "POWER METER" or "LASER/POWERMETER" => "LASER",
                "PICO" or "PICOMOTOR" or "PICO_MOTOR" or "DOETILT" or "DOE TILT" or "DOE_TILT" => "PICO MOTOR",
                "COORDINATE" or "COORDINATEVIEWER" or "COORDINATE_VIEWER" => "COORDINATE VIEWER",
                "LASER" or "ATTENUATOR" or "BET" or "PICO MOTOR" or "PRODUCT" or "MELSEC" or "COORDINATE VIEWER" or "LOG VIEWER" => normalized,
                _ => "PRODUCT"
            };
        }

        private static ST_SYSTEM_STATUS CreateFallbackST_SYSTEM_STATUS()
        {
            return new ST_SYSTEM_STATUS(
                "DRILL_A01", 
                EN_SYSTEM_MODE.Auto, 
                EN_ALARM_STATE.Clear, 
                EN_PM_LOCK_STATE.Released, 
                [
                new(EN_EQP_MODULE.WonikCtrl, EN_COMM_STATE.Simulation), 
                new(EN_EQP_MODULE.Vision, EN_COMM_STATE.Simulation), 
                new(EN_EQP_MODULE.Automation1, EN_COMM_STATE.Simulation), 
                new(EN_EQP_MODULE.Motion, EN_COMM_STATE.Simulation), 
                new(EN_EQP_MODULE.TalonLaser, EN_COMM_STATE.Simulation), 
                new(EN_EQP_MODULE.Attenuator, EN_COMM_STATE.Simulation), 
                new(EN_EQP_MODULE.Bet, EN_COMM_STATE.Simulation)
            ]
            );
        }

        private static IReadOnlyList<ST_DEVICE_COMM_STATUS> NormalizeCommunicationStatuses(IReadOnlyList<ST_DEVICE_COMM_STATUS> modules)
        {
            global::Drilling.Common.Interface.ST_DEVICE_COMM_STATUS SelectItem(global::Drilling.Common.Interface.EN_EQP_MODULE module)
            {
                bool MatchesItem(global::Drilling.Common.Interface.ST_DEVICE_COMM_STATUS status)
                {
                    return status.Module == module;
                }

                return modules.FirstOrDefault(MatchesItem) ?? new ST_DEVICE_COMM_STATUS(module, EN_COMM_STATE.Offline);
            }

            return Enum.GetValues<EN_EQP_MODULE>().Select(SelectItem).ToArray();
        }

        private ST_HEADER_STATUS_ITEM ModuleHeader(ST_SYSTEM_STATUS status, EN_EQP_MODULE module)
        {
            var moduleStatuses = GetModuleCommunicationStatuses(module);
            if (moduleStatuses.Count == 0)
            {
                var fallbackConnection = status.GetModule(module).ConnectionState;
                var fallbackValue = ConnectionStateValue(fallbackConnection);
                return new ST_HEADER_STATUS_ITEM(ModuleDisplayName(module), fallbackValue, fallbackValue);
            }

            var selectedIndex = GetSelectedModuleIndex(module, moduleStatuses.Count);
            var selectedStatus = moduleStatuses[selectedIndex];
            var connection = selectedStatus.ConnectionState;
            var value = ConnectionStateValue(connection);
            var displayNumber = selectedStatus.Number + 1;
            var displayValue = moduleStatuses.Count > 1 ? $"{displayNumber}: {selectedStatus.ConnectionText}" : selectedStatus.ConnectionText;
            var pageText = moduleStatuses.Count > 1 ? $"{displayNumber}/{moduleStatuses.Count}" : "";
            void SelectPreviousModuleStatusCallback(object? arg0)
            {
                SelectPreviousModuleStatus(module);
            }

            void SelectNextModuleStatusCallback(object? arg0)
            {
                SelectNextModuleStatus(module);
            }

            void ShowModuleStatusPopupCallback(object? arg0)
            {
                ShowModuleStatusPopup(module);
            }

            return new ST_HEADER_STATUS_ITEM(
                ModuleDisplayName(module), 
                displayValue, 
                value, 
                moduleStatuses.Count > 1, 
                pageText, 
                new CButtonCommand(SelectPreviousModuleStatusCallback), 
                new CButtonCommand(SelectNextModuleStatusCallback), 
                new CButtonCommand(ShowModuleStatusPopupCallback)
            );
        }

        private IReadOnlyList<ST_INTERFACE_COMM_STATUS> GetModuleCommunicationStatuses(EN_EQP_MODULE module)
        {
            bool ShouldIncludeItem(global::Drilling.Common.Interface.ST_INTERFACE_COMM_STATUS status)
            {
                return status.Module == module;
            }

            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_COMM_STATUS status)
            {
                return status.Number;
            }

            string SelectNickName(global::Drilling.Common.Interface.ST_INTERFACE_COMM_STATUS status)
            {
                return status.NickName;
            }

            return _interfaceCommStatuses.Where(ShouldIncludeItem).OrderBy(SelectNumber).ThenBy(SelectNickName, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private int GetSelectedModuleIndex(EN_EQP_MODULE module, int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            if (!_selectedHeaderModuleIndexes.TryGetValue(module, out var selectedIndex))
            {
                selectedIndex = 0;
            }

            selectedIndex = Math.Clamp(selectedIndex, 0, count - 1);
            _selectedHeaderModuleIndexes[module] = selectedIndex;
            return selectedIndex;
        }

        private void SelectPreviousModuleStatus(EN_EQP_MODULE module)
        {
            SelectModuleStatus(module, -1);
        }

        private void SelectNextModuleStatus(EN_EQP_MODULE module)
        {
            SelectModuleStatus(module, 1);
        }

        private void SelectModuleStatus(EN_EQP_MODULE module, int offset)
        {
            var moduleStatuses = GetModuleCommunicationStatuses(module);
            if (moduleStatuses.Count <= 1)
            {
                return;
            }

            var selectedIndex = GetSelectedModuleIndex(module, moduleStatuses.Count);
            _selectedHeaderModuleIndexes[module] = (selectedIndex + offset + moduleStatuses.Count) % moduleStatuses.Count;
            RefreshShellStatusItems();
        }

        private void ShowModuleStatusPopup(EN_EQP_MODULE module)
        {
            var moduleStatuses = GetModuleCommunicationStatuses(module);
            if (moduleStatuses.Count == 0)
            {
                return;
            }

            var dialog = new CInterfaceStatusDialog(ModuleDisplayName(module), moduleStatuses);
            var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? Application.Current.MainWindow;
            if (owner is not null && !ReferenceEquals(owner, dialog))
            {
                dialog.Owner = owner;
            }

            dialog.ShowDialog();
        }

        private static string ModuleDisplayName(EN_EQP_MODULE module)
        {
            return module switch
            {
                EN_EQP_MODULE.WonikCtrl => "WONIK CTRL",
                EN_EQP_MODULE.Vision => "VISION",
                EN_EQP_MODULE.Automation1 => "AUTOMATION",
                EN_EQP_MODULE.Motion => "SCANNER TILT",
                EN_EQP_MODULE.TalonLaser => "LASER",
                EN_EQP_MODULE.Attenuator => "ATT",
                EN_EQP_MODULE.Bet => "BET",
                EN_EQP_MODULE.PowerMeter => "POWERMETER",
                EN_EQP_MODULE.PicoMotor => "DOE TILT",
                EN_EQP_MODULE.Melsec => "MELSEC",
                _ => module.ToString().ToUpperInvariant()};
        }

        private static string ConnectionStateValue(EN_COMM_STATE state)
        {
            return state switch
            {
                EN_COMM_STATE.Online => "ONLINE",
                EN_COMM_STATE.Offline => "OFFLINE",
                _ => "SIMULATION"
            };
        }

        private static string OperationModeValue(EN_SYSTEM_MODE mode)
        {
            return mode switch
            {
                EN_SYSTEM_MODE.Auto => "Auto",
                EN_SYSTEM_MODE.Manual => "Manual",
                _ => "Simulation"
            };
        }

        private static string OperationModeHeaderValue(EN_SYSTEM_MODE mode, bool isSimulationEnvironment)
        {
            var modeText = OperationModeValue(mode);
            return isSimulationEnvironment ? $"{modeText}\n(Simulation)" : modeText;
        }

        private static string OperationModeState(bool isSimulationEnvironment)
        {
            return isSimulationEnvironment ? "SIMULATION" : "ONLINE";
        }

        private static string AlarmStateValue(EN_ALARM_STATE state)
        {
            return state == EN_ALARM_STATE.Occur ? "OCCUR" : "CLEAR";
        }

        private static string GetMenuDisplayName(EN_MENU menu)
        {
            return menu switch
            {
                EN_MENU.Main => "MAIN",
                EN_MENU.Manual => "MANUAL",
                EN_MENU.Recipe => "RECIPE",
                EN_MENU.Setting => "SETTING",
                EN_MENU.Alarm => "ALARM",
                EN_MENU.Monitor => "MONITOR",
                EN_MENU.Review => "REVIEW",
                EN_MENU.Correction => "CORRECTION",
                EN_MENU.Pm => "PM",
                EN_MENU.Exit => "EXIT",
                _ => menu.ToString()};
        }
#endregion

#region Alarm
        private Task EnsureHeavyAlarmRecovery() => _heavyAlarmRecoveryTask is { IsCompleted: false } running ? running : _heavyAlarmRecoveryTask = RecoverHeavyAlarm();
        private CButtonCommand HeaderAlarmResetCommand => _headerAlarmResetCommand ??= new(_ => ShowAlarmPopup());

        private bool CheckHeavyAlarmForOperation(string operation)
        {
            if (!_heavyRecoveryBlocked && !_alarmManager.CurAlarm.Any(row => row.Severity == EN_ALARM_LEVEL.Critical))
                return true;
            StatusMessage = $"HEAVY 알람으로 {operation} 동작이 차단되었습니다.";
            ShowAlarmPopup();
            return false;
        }

        private void ShowAlarmPopup()
        {
            if (_alarmPopup is { } open)
            {
                open.Panel.Refresh();
                if (open.WindowState == WindowState.Minimized)
                    open.WindowState = WindowState.Normal;
                open.Activate();
                return;
            }

            var panel = new CAlarmPopupPanel(_alarmManager, ConfirmHeaderAlarmReset);
            panel.SetRecoveryStatus(_alarmRecoveryStatus);
            var window = new CAlarmPopupDialog(panel);
            if (Application.Current?.MainWindow is { IsVisible: true } owner)
                window.Owner = owner;
            _alarmPopup = window;
            window.Closed += (_, _) => _alarmPopup = null;
            window.Show();
        }

        private async Task ConfirmHeaderAlarmReset()
        {
            if (_isHeaderAlarmResetRunning)
                return;
            _isHeaderAlarmResetRunning = true;
            try
            {
                if (!CRecipeConfirmDialog.ShowConfirmation("ALARM RESET", "현재 발생한 알람을 RESET하시겠습니까?", "RESET", useDangerStyle: true))
                    return;
                if (_heavyAlarmRecoveryTask is { IsCompleted: false } pending)
                    await pending;
                if (_heavyRecoveryBlocked)
                    await EnsureHeavyAlarmRecovery();
                if (_heavyRecoveryBlocked)
                    return;
                await ((CMenuAlarm)_menus[EN_MENU.Alarm]).ResetAlarmAsync();
                _alarmPopup?.Panel.Refresh();
            }
            finally
            {
                _isHeaderAlarmResetRunning = false;
            }
        }

        private void SetAlarmRecoveryStatus(string text)
        {
            _alarmRecoveryStatus = text;
            _alarmPopup?.Panel.SetRecoveryStatus(text);
        }

        private async Task RecoverHeavyAlarm()
        {
            _heavyRecoveryBlocked = true;
            SetAlarmRecoveryStatus("HEAVY · 진행 동작 정지 및 우리 측 Handshake OFF 확인 중");
            try
            {
                _autoCycleCts?.Cancel();
                var failures = new List<Exception>();
                async Task Stop(Func<Task> action)
                {
                    try
                    {
                        await action();
                    }
                    catch (Exception error)
                    {
                        failures.Add(error);
                    }
                }

                // Request every cancellation before awaiting any one driver. Always observe all stop tasks.
                var menuStops = _menus.Values.Select(async menu => await menu.StopForHeavyAlarm()).ToArray();
                await Stop(async () =>
                {
                    if (!await _stationManager.StageRequests.StopAsync())
                        throw new InvalidOperationException("Stage 시퀀스의 정지가 아직 확인되지 않았습니다.");
                });
                if (_stationManager.ManualMofStatus is { IsRunning: true } or { RequiresStop: true })
                    await Stop(_stationManager.StopManualMofAsync);
                if (_stationManager.ManualMofStatus.RequiresStop)
                    failures.Add(new InvalidOperationException("Manual MOF 정지가 확인되지 않았습니다."));
                if (_autoCycleCompletion is { } cycle)
                    await Stop(async () =>
                    {
                        _stationManager.Stop();
                        await cycle.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    });
                await Stop(() => Task.WhenAll(menuStops));
                _reviewManager.Stop();
                await Stop(async () =>
                {
                    var until = DateTimeOffset.UtcNow.AddSeconds(15);
                    while (_reviewManager.SequenceState is EN_REVIEW_SEQUENCE_STATE.Running or EN_REVIEW_SEQUENCE_STATE.Stopping || _interfaceManager.StageReviewSequences.IsActive)
                    {
                        if (DateTimeOffset.UtcNow >= until)
                            throw new TimeoutException("Review/Stage 공통 동작 정지 확인 시간 초과");
                        await Task.Delay(50);
                    }
                });
                if (failures.Count > 0)
                    throw new AggregateException("동작 정지 확인 실패", failures);
                await CHandshakeOutputReset.Reset(_interfaceManager);
                _heavyRecoveryBlocked = false;
                SetAlarmRecoveryStatus("HEAVY · 동작 정지 / Handshake OFF 확인 완료 · MANUAL");
            }
            catch (Exception error)
            {
                SetAlarmRecoveryStatus("정지 또는 Handshake OFF 확인 실패 · 재시작 차단: " + error.Message);
                try
                {
                    _manager.Log().WriteStationState("PROCESS", "ALARM", "HEAVY_ALARM_RECOVERY", error.Message);
                }
                catch (Exception logError)
                {
                    System.Diagnostics.Trace.TraceError(logError.ToString());
                }
            }
            finally
            {
                _operationMode = EN_SYSTEM_MODE.Manual;
                _systemStatus = _systemStatus with
                {
                    OperationMode = EN_SYSTEM_MODE.Manual
                };
                SetAutoCycleState(EN_AUTO_CYCLE_STATE.Ready);
                RefreshOperationModeAccess();
                OnPropertyChanged(nameof(IsManualShiftOffsetVisible));
                RefreshShellStatusItems();
                StopCommand.NotifyCanExecuteChanged();
            }
        }

        private void OnCurrentAlarmsChanged()
        {
            if (_alarmDispatcher.HasShutdownStarted || Interlocked.Exchange(ref _alarmRefreshQueued, 1) != 0)
                return;
            _alarmDispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                Interlocked.Exchange(ref _alarmRefreshQueued, 0);
                var snapshot = _alarmManager.GetCurrentSnapshot();
                _systemStatus = _systemStatus with
                {
                    AlarmState = snapshot.Rows.Count > 0 ? EN_ALARM_STATE.Occur : EN_ALARM_STATE.Clear
                };
                RefreshShellStatusItems();
                var currentHeavy = snapshot.Rows.Where(row => row.Severity == EN_ALARM_LEVEL.Critical).Select(row => (row.Code, row.OccurredAt)).ToHashSet();
                _heavyOccurrences.IntersectWith(currentHeavy);
                var newHeavy = false;
                foreach (var occurrence in currentHeavy)
                    if (_heavyOccurrences.Add(occurrence))
                        newHeavy = true;
                _alarmPopup?.Panel.Refresh();
                if (_alarmNavigation.ShouldNavigate(snapshot))
                    ShowAlarmPopup();
                if (newHeavy)
                    _ = EnsureHeavyAlarmRecovery();
                if (SelectedMenu.Menu == EN_MENU.Alarm)
                    _ = RefreshCurrentScreen();
            }));
        }
#endregion

#region ProtectWindow
        public void StartProtectWindowRequestNotice(Window owner)
        {
            _protectWindowNotice?.Dispose();
            _protectWindowNotice = new(_interfaceManager, owner);
            _protectWindowNotice.Start();
        }
#endregion

#region StageRequests
        private ST_STAGE_TPM_ACTIONS CreateAutoPowerActions()
        {
            CPowerMeasureRun? run = null;
            return new(async token =>
            {
                if (run is not null) await run.DisposeAsync().ConfigureAwait(false);
                var recipe = await _recipeManager.RefreshRecipe(_selectedRecipeId, token).ConfigureAwait(false) ?? throw new InvalidOperationException("선택된 Recipe가 없습니다.");
                run = Drilling.File.PowerTable.CPowerTableFile.CreateRun(_manager, recipe, CPowerMeasureRun.AutoTargets(_manager.Setting()), automatic: true);
                await run.Prepare(token).ConfigureAwait(false);
            }, token => (run ?? throw new InvalidOperationException("TPM 준비가 필요합니다.")).Measure(token),
               token => (run ?? throw new InvalidOperationException("TPM 측정이 필요합니다.")).Save(token),
               async () => { if (run is not null) await run.DisposeAsync().ConfigureAwait(false); });
        }

        private Guid? _autoReviewZeroLineRunId;
        private ST_STAGE_REVIEW_ACTIONS CreateAutoReviewActions()
        {
            _autoFineReview ??= new(_reviewManager, _manager.ReviewOffsetFile(), CreateStageReviewInput);
            return new(_autoFineReview.Prepare, async token =>
            {
                try
                {
                    await _autoFineReview.Measure(token).ConfigureAwait(false);
                }
                catch (InvalidDataException) when (_autoReviewZeroLineRunId is not null && _autoFineReview.FollowUpInspectionCompleted)
                {
                    // A saved diagnostic NG is a completed inspection, not a request
                    // to repeat or to correct. Preserve the quality alarm after clearing it.
                    await _stationManager.CompleteZeroLineFollowUp(_autoReviewZeroLineRunId.Value, token).ConfigureAwait(false);
                    _autoReviewZeroLineRunId = null;
                    throw;
                }
            }, async token =>
            {
                await _autoFineReview.Save(token).ConfigureAwait(false);
                if (_autoReviewZeroLineRunId is { } runId)
                {
                    await _stationManager.CompleteZeroLineFollowUp(runId, token).ConfigureAwait(false);
                    _autoReviewZeroLineRunId = null;
                }
            });
        }

        private async Task<ST_AUTO_REVIEW_INPUT> CreateStageReviewInput(CancellationToken token)
        {
            _autoReviewZeroLineRunId = null;
            // Called at Stage Review ABLE, before our ABLE. Later UI changes apply to the next request.
            var options = ((CMenuMain)_menus[EN_MENU.Main]).RunOptions.CaptureReviewOptions();
            var station = _stationManager.GetStatus(token);
            var current = station.ProcessModel;
            var recipeId = current?.RecipeId ?? _selectedRecipeId;
            var recipe = (await _recipeManager.LoadRecipes(token).ConfigureAwait(false)).SingleOrDefault(item => item.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"Review Recipe를 찾을 수 없습니다: {recipeId}");
            if (current is not null)
                recipe = recipe with
                {
                    Parameters = current.Parameters.Select(pair => new ST_RECIPE_PARAMETER(pair.Key, pair.Value, "", "", pair.Value, Key: pair.Key)).ToArray()
                };
            var glass = current?.GlassId;
            if (string.IsNullOrWhiteSpace(glass))
                glass = (await CreateStageProcessRequest(token).ConfigureAwait(false)).GlassId;
            var followUp = station.ZeroLine is { FollowUpRequested: true, Plan: not null };
            if (followUp)
            {
                var plan = station.ZeroLine!.Plan!;
                if (!plan.RecipeId.Equals(recipe.Id, StringComparison.OrdinalIgnoreCase) || !plan.GlassId.Equals(glass, StringComparison.Ordinal))
                    throw new InvalidOperationException("0선 후속 검사 대상의 Glass/Recipe가 현재 Review와 다릅니다.");
                _autoReviewZeroLineRunId = plan.RunId;
            }
            return new(recipe, glass, options) { ZeroLineFollowUp = followUp };
        }

        // Invoked only at DoReady START. Does not update UI or call a device read.
        private async Task<ST_STAGE_PROCESS_REQUEST> CreateStageProcessRequest(CancellationToken token)
        {
            var recipeId = _selectedRecipeId;
            var recipes = await _recipeManager.LoadRecipes(token).ConfigureAwait(false);
            var recipe = recipes.SingleOrDefault(value => value.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"Selected recipe does not exist: {recipeId}.");
            var now = DateTime.Now;
            var product = _productManager.Current;
            if (product?.State == EN_PRODUCT_STATE.Completed)
                product = null;
            var glassId = string.IsNullOrWhiteSpace(product?.GlassId) ? $"{(IsSimulationEnvironment ? "SIM" : "DUMMY")}_{now:yyyyMMdd_HHmmssfff}" : product.GlassId;
            var lotId = product?.LotId?.Trim() ?? "";
            var parameters = CreateProcessParameters(recipe, glassId, lotId, 0, 0);
            // TODO: replace the existing local product identity fallback when Stage Glass/Lot mapping is agreed.
            return new($"AUTO-{now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}", recipe.Id, glassId, lotId, parameters);
        }
#endregion

#region StageSimulation
        public CButtonCommand OpenMelsecStageSimulationCommand { get; }

        private void RefreshMelsecSimulatorVisibility()
        {
            var value = MelsecSimulatorVisibility;
            if (_lastSimulatorVisibility == value)
                return;
            _lastSimulatorVisibility = value;
            MelsecSimulatorVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }

        public Visibility MelsecSimulatorVisibility => _interfaceManager.GetInterfaceCommunicationList(Drilling.Common.Interface.EN_EQP_MODULE.Melsec).Any(device => device.IsSimulation) ? Visibility.Visible : Visibility.Collapsed;

        private void OpenMelsecStageSimulation()
        {
            if (MelsecSimulatorVisibility != Visibility.Visible)
                return;
            if (_stageSimulationDialog is not null)
            {
                if (_stageSimulationDialog.WindowState == WindowState.Minimized)
                    _stageSimulationDialog.WindowState = WindowState.Normal;
                _stageSimulationDialog.Activate();
                return;
            }

            var dialog = new CMelsecStageSimulationDialog(_interfaceManager)
            {
                Owner = Application.Current?.MainWindow
            };
            _stageSimulationDialog = dialog;
            dialog.Closed += (_, _) => _stageSimulationDialog = null;
            dialog.Show(); // Keep MAIN and AUTO controls usable while injecting Stage inputs.
        }
#endregion
    }

    // OccurrenceVersion changes only on a new insertion, including recurrence after RESET.
    public sealed class CAlarmNavigationTracker
    {
        private long _handledOccurrence;
        public bool ShouldNavigate(ST_CURRENT_ALARM_SNAPSHOT snapshot)
        {
            if (snapshot.OccurrenceVersion <= _handledOccurrence)
                return false;
            _handledOccurrence = snapshot.OccurrenceVersion;
            return snapshot.Rows.Count > 0;
        }
    }

    internal sealed record ST_SYSTEM_STATUS_SOURCE(
        string CurrentRecipeId, 
        EN_SYSTEM_MODE OperationMode, 
        EN_PM_LOCK_STATE PmLockState, 
        IReadOnlyList<ST_DEVICE_COMM_STATUS> Modules, 
        IReadOnlyList<ST_INTERFACE_COMM_STATUS> InterfaceCommunicationStatuses
    );
    internal sealed record ST_SYSTEM_STATUS_SNAPSHOT(ST_SYSTEM_STATUS Status, IReadOnlyList<ST_INTERFACE_COMM_STATUS> InterfaceCommunicationStatuses);
}
