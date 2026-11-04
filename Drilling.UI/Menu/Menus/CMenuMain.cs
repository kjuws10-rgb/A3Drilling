namespace Drilling.UI.Menu.Menus
{
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Windows;
    using Drilling.Common.Review;
    using System.Windows.Media;
    using Drilling.Common.InterLock;
    using InterlockItem = Drilling.Common.InterLock.ST_INTERLOCK_ITEM;
    using System.Globalization;
    using System.IO;
    using System.Runtime.CompilerServices;
    using Drilling.Common.Interface;
    using Drilling.Common.Recipe;
    using Drilling.Common.Managers;
    using Drilling.Common.Station;
    using Drilling.UI.Menu;
    using Drilling.Common.Automation;
    using Drilling.Common.Motion;
    using Drilling.Common.Alarm;
    using Drilling.UI.Popup;
    using Drilling.Common.Threading;

    public sealed partial class CMenuMain(
        CStationManager stationManager, 
        CInterfaceManager interfaceManager, 
        CAutomationManager automationManager, 
        CRecipeManager recipeManager, 
        CSettingManager settingManager, 
        CReviewManager reviewManager, 
        Func<string> selectedRecipeIdProvider, 
        Action<string> statusReporter, 
        Func<Task> refreshCurrentScreen,
        CPowerMeasureService? powerMeasure = null
    ) : CMenuBase
    {
#region 공유 상태·초기값
        private const string ParameterTabHead = "HEAD";
        private const string ParameterTabRunOption = "RUN_OPTION";
        private const string ParameterTabProtectWindow = "PROTECT_WINDOW";
        private const string ParameterTabOptic = "OPTIC";
        private const string ParameterTabScanner = "SCANNER";
        private const string ParameterTabMotor = "MOTOR";
        private const string OpticStatusLaser = "LASER";
        private const string OpticStatusAttenuator = "ATT";
        private const string OpticStatusBet = "BET";
        private const double ReviewSequenceRowTolerance = 0.001;
        public CMainSequenceStatus SequenceStatus { get; } = new();
        public CMainProtectWindowPanel ProtectWindow { get; } = new();
        public CMainRunOptionPanel RunOptions { get; } = new(() => ReadReviewAble(interfaceManager?.Melsec.GetCachedValue("HANDSHAKE_PRC_REVIEW_ABLE")), settingManager, statusReporter, initiallyVisible: true);

        private CButtonCommand? _selectParameterTabCommand;
        private CButtonCommand? _selectOpticStatusCommand;
        private CButtonCommand? _selectScannerStatusHeadCommand;
        private string _selectedParameterTabKey = ParameterTabRunOption;
        private string _selectedOpticStatusKey = OpticStatusLaser;
        private int _selectedScannerStatusHeadNo = 1;
        private IReadOnlyList<ST_DISPLAY_ITEM> _cycleItems = [];
        private IReadOnlyList<ST_DISPLAY_ITEM> _resultItems = [];
        private IReadOnlyList<ST_MAIN_PROCESS_SEQUENCE_ITEM> _processSequenceItems = [];
        private IReadOnlyList<ST_DISPLAY_ITEM> _currentStepDetails = [];
        private IReadOnlyList<ST_DISPLAY_ITEM> _processSummaryItems = [];
        private IReadOnlyList<ST_DISPLAY_ITEM> _lifecycleItems = [];
        private IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM> _scriptTaskStatusItems = [];
        private IReadOnlyList<ST_AUTOMATION_CONTROLLER_STATUS_ITEM> _automationControllerStatusItems = [];
        private IReadOnlyDictionary<(int AutomationNo, int TaskNo), ST_SCRIPT_TASK_STATUS_VALUE> _lastScriptTaskStatusValues = new Dictionary<(int AutomationNo, int TaskNo), ST_SCRIPT_TASK_STATUS_VALUE>();
        private IReadOnlyList<ST_INSPECTION_STATUS_ITEM> _inspectionStatusItems = [];
        private string _inspectionSummary = "0 / 0 shots";
        private string _inspectionModeText = "MODE : -";
        private string _inspectionRuleText = "RULE : -";
        private Visibility _inspectionRuleVisibility = Visibility.Collapsed;
        private IReadOnlyList<ST_INTERLOCK_GROUP> _interlockItems = [];
        private readonly CMainInterlockSnapshot _interlockSnapshot = new();
        private IReadOnlyDictionary<string, string>? _previewSourceParameters;
        private ST_RECIPE_DEFINITION? _previewSourceRecipe;
        private IReadOnlyList<ST_SYSTEM_PARAMETER>? _previewSourceSettings;
        private IReadOnlyDictionary<string, string>? _previewParameterCache;
        private IReadOnlyList<ST_OPTIC_PARAMETER_GROUP> _opticParameterGroups = [];
        private IReadOnlyList<ST_OPTIC_HEAD_PARAMETER> _opticHeadParameters = [];
        private IReadOnlyList<ST_MAIN_PARAMETER_TAB_ITEM> _opticStatusTabs = [];
        private IReadOnlyList<ST_MAIN_PARAMETER_TAB_ITEM> _scannerStatusHeadTabs = [];
        private IReadOnlyList<ST_SCANNER_AXIS_STATUS_ITEM> _scannerStatusItems = [];
        private string _processStep = "";
        private string _scriptStatus = "";
        private string _scriptStatusText = "Script Status       -";
        private string _resultMessage = "";
        private string _totalShotsText = "Total Shots         0";
        private string _moveCountText = "Move Count (G0)     0";
        private string _laserOnSegmentsText = "Laser On Segments   0";
        private string _estimatedTimeText = "Estimated Time       00:00:00";
        private string _elapsedTimeText = "Elapsed Time         00:00:00";
        private string _progressText = "Progress             0.0%";
        private string _progressPercentText = "0.0%";
        private double _progressPercent;
        private string _processResultValue = "PENDING";
        private Brush _processResultBrush = CStatusBrush.Wait;
        private readonly CScannerStatusSnapshotReader _scannerStatusSnapshotReader = new(automationManager);
        private readonly CMotorStatusPollingService _motorStatusPollingService = new(interfaceManager);
        private IReadOnlyList<ST_MOTOR_STATUS_ITEM> _motorStatusItems = [];
        public Visibility MotorStatusVisibility { get; private set; } = Visibility.Collapsed;

        private IReadOnlyDictionary<int, long> _previewHeadShotCounts = new Dictionary<int, long>();
        private IReadOnlyList<ST_HEAD_PREVIEW> _headPreviews = [];
        private IReadOnlyList<ST_HEAD_PREVIEW> _oddHeadPreviews = [];
        private IReadOnlyList<ST_HEAD_PREVIEW> _evenHeadPreviews = [];
        public IReadOnlyList<ST_HEAD_PARAMETER> HeadParameters { get; private set; } = [];
        public IReadOnlyList<ST_MAIN_PARAMETER_TAB_ITEM> ParameterTabs { get; private set; } = [];
        public Visibility HeadParameterVisibility { get; private set; } = Visibility.Collapsed;
        public Visibility OpticParameterVisibility { get; private set; } = Visibility.Collapsed;
        public Visibility ScannerStatusVisibility { get; private set; } = Visibility.Collapsed;

        private CAutoReviewMonitorDialog? _reviewMonitorWindow;
        private Window? _powerMeasureWindow;
        private CZeroLineResultsDialog? _zeroLineResultsWindow;
#endregion
#region 기본 처리
        private static bool? ReadReviewAble(ST_MELSEC_CACHED_VALUE? sample)
        {
            if (sample?.IsValid != true || sample.Value is null)
                return null;
            try
            {
                return Convert.ToDouble(sample.Value, CultureInfo.InvariantCulture) switch
                {
                    0 => false,
                    1 => true,
                    _ => null
                };
            }
            catch (Exception error)when (error is FormatException or InvalidCastException or OverflowException)
            {
                return null;
            }
        }

        private void RefreshSequenceStatus(ST_STATION_PROCESS_STATUS snapshot)
        {
            var values = interfaceManager.Melsec.GetCachedSnapshot(out var map);
            var connections = interfaceManager.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).ToDictionary(device => device.Number, device => device.IsSimulation ? EN_COMM_STATE.Simulation : device.ConnectionState);
            SequenceStatus.Refresh(values, map, connections, snapshot.SequenceActions);
            SequenceStatus.RefreshZeroLine(snapshot.ZeroLinePrewait, snapshot.ZeroLine);
            if (_selectedParameterTabKey == ParameterTabProtectWindow)
                ProtectWindow.RefreshSignals(values, map, connections);
            RunOptions.UpdateReviewAble(ReadReviewAble(values.GetValueOrDefault("HANDSHAKE_PRC_REVIEW_ABLE")));
            SequenceStatus.OpenReviewMonitor = OpenReviewMonitor;
            SequenceStatus.OpenPowerMeasureMonitor = OpenPowerMeasureMonitor;
            SequenceStatus.OpenZeroLineResults = OpenZeroLineResults;
            SequenceStatus.RefreshReviewMonitor(reviewManager?.FineMonitor.GetSnapshot());
        }

        public IReadOnlyList<ST_MOTOR_STATUS_ITEM> MotorStatusItems { get => _motorStatusItems; private set => SetSequenceProperty(ref _motorStatusItems, value); }
        public bool IsMotorStatusSelected => _selectedParameterTabKey == ParameterTabMotor;

        private void RefreshMotorStatus()
        {
            if (!IsMotorStatusSelected)
                return;
            _motorStatusPollingService.Start();
            MotorStatusItems = _motorStatusPollingService.GetSnapshot();
        }

        public override EN_MENU Menu
        {
            get
            {
                return EN_MENU.Main;
            }
        }

        public IReadOnlyList<ST_HEAD_PREVIEW> HeadPreviews { get => _headPreviews; private set => SetSequenceProperty(ref _headPreviews, value); }
        public IReadOnlyList<ST_HEAD_PREVIEW> OddHeadPreviews { get => _oddHeadPreviews; private set => SetSequenceProperty(ref _oddHeadPreviews, value); }
        public IReadOnlyList<ST_HEAD_PREVIEW> EvenHeadPreviews { get => _evenHeadPreviews; private set => SetSequenceProperty(ref _evenHeadPreviews, value); }

        public IReadOnlyList<ST_DISPLAY_ITEM> CycleItems
        {
            get
            {
                return _cycleItems;
            }

            private set
            {
                SetSequenceProperty(ref _cycleItems, value);
            }
        }

        public IReadOnlyList<ST_DISPLAY_ITEM> ResultItems
        {
            get
            {
                return _resultItems;
            }

            private set
            {
                SetSequenceProperty(ref _resultItems, value);
            }
        }

        public IReadOnlyList<ST_MAIN_PROCESS_SEQUENCE_ITEM> ProcessSequenceItems
        {
            get
            {
                return _processSequenceItems;
            }

            private set
            {
                SetSequenceProperty(ref _processSequenceItems, value);
            }
        }

        public IReadOnlyList<ST_DISPLAY_ITEM> CurrentStepDetails
        {
            get
            {
                return _currentStepDetails;
            }

            private set
            {
                SetSequenceProperty(ref _currentStepDetails, value);
            }
        }

        public IReadOnlyList<ST_DISPLAY_ITEM> ProcessSummaryItems
        {
            get
            {
                return _processSummaryItems;
            }

            private set
            {
                SetSequenceProperty(ref _processSummaryItems, value);
            }
        }

        public IReadOnlyList<ST_DISPLAY_ITEM> LifecycleItems
        {
            get
            {
                return _lifecycleItems;
            }

            private set
            {
                SetSequenceProperty(ref _lifecycleItems, value);
            }
        }

        public IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM> ScriptTaskStatusItems
        {
            get
            {
                return _scriptTaskStatusItems;
            }

            private set
            {
                SetSequenceProperty(ref _scriptTaskStatusItems, value);
            }
        }

        public IReadOnlyList<ST_AUTOMATION_CONTROLLER_STATUS_ITEM> AutomationControllerStatusItems
        {
            get
            {
                return _automationControllerStatusItems;
            }

            private set
            {
                SetSequenceProperty(ref _automationControllerStatusItems, value);
            }
        }

        public IReadOnlyList<ST_INSPECTION_STATUS_ITEM> InspectionStatusItems
        {
            get
            {
                return _inspectionStatusItems;
            }

            private set
            {
                SetSequenceProperty(ref _inspectionStatusItems, value);
            }
        }

        public string InspectionSummary
        {
            get
            {
                return _inspectionSummary;
            }

            private set
            {
                SetProperty(ref _inspectionSummary, value);
            }
        }

        public string InspectionModeText
        {
            get
            {
                return _inspectionModeText;
            }

            private set
            {
                SetProperty(ref _inspectionModeText, value);
            }
        }

        public string InspectionRuleText
        {
            get
            {
                return _inspectionRuleText;
            }

            private set
            {
                SetProperty(ref _inspectionRuleText, value);
            }
        }

        public Visibility InspectionRuleVisibility
        {
            get
            {
                return _inspectionRuleVisibility;
            }

            private set
            {
                SetProperty(ref _inspectionRuleVisibility, value);
            }
        }

        public IReadOnlyList<ST_INTERLOCK_GROUP> InterlockItems
        {
            get
            {
                return _interlockItems;
            }

            private set
            {
                if (_interlockItems.Count == value.Count && _interlockItems.Zip(value).All(pair => pair.First.Item == pair.Second.Item && pair.First.Details.SequenceEqual(pair.Second.Details)))
                    return;
                SetSequenceProperty(ref _interlockItems, value);
            }
        }

        public IReadOnlyList<ST_MAIN_PARAMETER_TAB_ITEM> OpticStatusTabs
        {
            get
            {
                return _opticStatusTabs;
            }

            private set
            {
                SetSequenceProperty(ref _opticStatusTabs, value);
            }
        }

        public IReadOnlyList<ST_MAIN_PARAMETER_TAB_ITEM> ScannerStatusHeadTabs
        {
            get
            {
                return _scannerStatusHeadTabs;
            }

            private set
            {
                SetSequenceProperty(ref _scannerStatusHeadTabs, value);
            }
        }

        public IReadOnlyList<ST_OPTIC_PARAMETER_GROUP> OpticParameterGroups
        {
            get
            {
                return _opticParameterGroups;
            }

            private set
            {
                SetSequenceProperty(ref _opticParameterGroups, value);
            }
        }

        public IReadOnlyList<ST_OPTIC_HEAD_PARAMETER> OpticHeadParameters
        {
            get
            {
                return _opticHeadParameters;
            }

            private set
            {
                SetSequenceProperty(ref _opticHeadParameters, OrderOpticHeadParameters(value));
            }
        }

        public IReadOnlyList<ST_SCANNER_AXIS_STATUS_ITEM> ScannerStatusItems
        {
            get
            {
                return _scannerStatusItems;
            }

            private set
            {
                SetSequenceProperty(ref _scannerStatusItems, value);
            }
        }

        public Visibility OpticLaserVisibility
        {
            get
            {
                return IsOpticStatusSelected(OpticStatusLaser) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility OpticAttenuatorVisibility
        {
            get
            {
                return IsOpticStatusSelected(OpticStatusAttenuator) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility OpticBetVisibility
        {
            get
            {
                return IsOpticStatusSelected(OpticStatusBet) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public bool IsScannerStatusSelected
        {
            get
            {
                return _selectedParameterTabKey.Equals(ParameterTabScanner, StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool IsOpticParameterSelected
        {
            get
            {
                return _selectedParameterTabKey.Equals(ParameterTabOptic, StringComparison.OrdinalIgnoreCase);
            }
        }

        public string ProcessStep
        {
            get
            {
                return _processStep;
            }

            private set
            {
                SetProperty(ref _processStep, value);
            }
        }

        public string ScriptStatus
        {
            get
            {
                return _scriptStatus;
            }

            private set
            {
                SetProperty(ref _scriptStatus, value);
            }
        }

        public string ScriptStatusText
        {
            get
            {
                return _scriptStatusText;
            }

            private set
            {
                SetProperty(ref _scriptStatusText, value);
            }
        }

        public string ResultMessage
        {
            get
            {
                return _resultMessage;
            }

            private set
            {
                SetProperty(ref _resultMessage, value);
            }
        }

        public string TotalShotsText
        {
            get
            {
                return _totalShotsText;
            }

            private set
            {
                SetProperty(ref _totalShotsText, value);
            }
        }

        public string MoveCountText
        {
            get
            {
                return _moveCountText;
            }

            private set
            {
                SetProperty(ref _moveCountText, value);
            }
        }

        public string LaserOnSegmentsText
        {
            get
            {
                return _laserOnSegmentsText;
            }

            private set
            {
                SetProperty(ref _laserOnSegmentsText, value);
            }
        }

        public string EstimatedTimeText
        {
            get
            {
                return _estimatedTimeText;
            }

            private set
            {
                SetProperty(ref _estimatedTimeText, value);
            }
        }

        public string ElapsedTimeText
        {
            get
            {
                return _elapsedTimeText;
            }

            private set
            {
                SetProperty(ref _elapsedTimeText, value);
            }
        }

        public string ProgressText
        {
            get
            {
                return _progressText;
            }

            private set
            {
                SetProperty(ref _progressText, value);
            }
        }

        public string ProgressPercentText
        {
            get
            {
                return _progressPercentText;
            }

            private set
            {
                SetProperty(ref _progressPercentText, value);
            }
        }

        public double ProgressPercent
        {
            get
            {
                return _progressPercent;
            }

            private set
            {
                SetProperty(ref _progressPercent, value);
            }
        }

        public string ProcessResultValue
        {
            get
            {
                return _processResultValue;
            }

            private set
            {
                SetProperty(ref _processResultValue, value);
            }
        }

        public Brush ProcessResultBrush
        {
            get
            {
                return _processResultBrush;
            }

            private set
            {
                SetProperty(ref _processResultBrush, value);
            }
        }

        public CButtonCommand SelectParameterTabCommand
        {
            get
            {
                async void SelectParameterTabCallback(object? parameter)
                {
                    await SelectParameterTab(parameter);
                }

                return _selectParameterTabCommand ??= new CButtonCommand(SelectParameterTabCallback);
            }
        }

        public CButtonCommand SelectOpticStatusCommand
        {
            get
            {
                async void SelectOpticStatusCallback(object? parameter)
                {
                    await SelectOpticStatus(parameter);
                }

                return _selectOpticStatusCommand ??= new CButtonCommand(SelectOpticStatusCallback);
            }
        }

        public CButtonCommand SelectScannerStatusHeadCommand
        {
            get
            {
                async void SelectScannerStatusHeadCallback(object? parameter)
                {
                    await SelectScannerStatusHead(parameter);
                }

                bool CanSelectScannerStatusHeadCallback(object? parameter)
                {
                    return CanSelectScannerStatusHead(parameter);
                }

                return _selectScannerStatusHeadCommand ??= new CButtonCommand(SelectScannerStatusHeadCallback, CanSelectScannerStatusHeadCallback);
            }
        }

        public override async Task<CScreenViewModel> Build(CancellationToken cancellationToken = default)
        {
            var snapshot = stationManager.GetStatus(cancellationToken);
            RefreshSequenceStatus(snapshot);
            var previewParameters = await LoadPreviewParameters(snapshot, cancellationToken);
            RunOptions.SetVisible(_selectedParameterTabKey == ParameterTabRunOption);
            ProtectWindow.SetVisible(_selectedParameterTabKey == ParameterTabProtectWindow);
            ProtectWindow.UpdateRecipe(previewParameters);
            RunOptions.UpdatePointCount(previewParameters.TryGetValue(CZeroDefenceRecipePlan.CountKey, out var pointCountText) && int.TryParse(pointCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pointCount) ? pointCount : 2);
            var activeHeadNos = BuildEnabledHeadNumbers(previewParameters);
            NormalizeScannerStatusHeadSelection(activeHeadNos);
            var selectedHeadNos = new HashSet<int>();
            var previewHeadLayout = await LoadPreviewHeadLayout(cancellationToken);
            var inspectionStatus = BuildInspectionStatus(snapshot);
            var scriptTaskStatusItems = BuildScriptTaskStatusPlaceholders(snapshot, previewParameters);
            var automationControllerStatusItems = BuildAutomationControllerStatusPlaceholders(scriptTaskStatusItems);
            var liveInterlockItems = LoadLiveInterlockItems(snapshot, previewParameters, cancellationToken);
            var opticParameterGroups = IsOpticParameterSelected ? await BuildOpticParameterGroups(previewParameters, cancellationToken) : [];
            var opticHeadParameters = IsOpticParameterSelected ? await BuildOpticHeadParameters(previewParameters, cancellationToken) : [];
            RefreshMotorStatus();
            if (IsScannerStatusSelected)
            {
                _scannerStatusSnapshotReader.SelectHead(_selectedScannerStatusHeadNo, previewParameters);
            }

            var scannerStatusItems = IsScannerStatusSelected ? _scannerStatusSnapshotReader.GetSnapshot() : [];
            var metrics = new List<ST_DISPLAY_ITEM>
            {
                new("Cycle State", snapshot.ProcessStep.ToString()),
                new("Script", FormatScriptStatus(snapshot.ScriptStatus)),
                new("Heads", snapshot.HeadPreviews.Count.ToString()),
                new("Result", snapshot.Result?.Message ?? "Waiting")
            };
            var recipePreview = CHeadAssignmentPreviewBuilder.BuildRecipePreview(previewParameters, selectedHeadNos, previewHeadLayout);
            _previewHeadShotCounts = recipePreview.HeadPointCounts;
            var powerSnapshot = powerMeasure?.GetSnapshot();
            global::Drilling.UI.Menu.Menus.ST_HEAD_PREVIEW SelectHeadPreviewItem(int headNo)
            {
                var isEnabled = activeHeadNos.Contains(headNo);
                var shotCount = _previewHeadShotCounts.TryGetValue(headNo, out var count) ? count : 0;
                return BuildHeadPreviewItem(headNo, shotCount, snapshot, selectedHeadNos, liveInterlockItems, scriptTaskStatusItems, isEnabled, powerSnapshot);
            }

            var headPreviewItems = Enumerable.Range(1, 8).Select(SelectHeadPreviewItem).ToArray();
            Apply(
                headPreviewItems, 
                [
                new("Cycle State", snapshot.ProcessStep.ToString()), 
                new("Script Status", FormatScriptStatus(snapshot.ScriptStatus)), 
                new("Automation", "Simulation"), 
                new("Preview Source", "Recipe Cell drilling points")
            ], 
                [new("Complete Time", snapshot.Result?.CompletedAt.ToString("HH:mm:ss") ?? "-"), new("Result", FormatProcessResult(snapshot)), new("Message", snapshot.Result?.Message ?? "Ready")], 
                [..BuildProcessSequenceItems(snapshot.ProcessSequence)], 
                [..snapshot.CurrentStepDetails.Select(ToDisplayItem)], 
                [..snapshot.ProcessSummary.Select(ToDisplayItem)], 
                [..snapshot.ScriptLifecycleItems.Select(ToDisplayItem)], 
                scriptTaskStatusItems, 
                automationControllerStatusItems, 
                inspectionStatus.Items, 
                inspectionStatus.Summary, 
                inspectionStatus.ModeText, 
                inspectionStatus.RuleText, 
                inspectionStatus.RuleVisibility, 
                CMainInterlockGroups.Build(liveInterlockItems), 
                BuildHeadParameters(previewParameters, activeHeadNos), 
                BuildParameterTabs(), 
                BuildOpticStatusTabs(), 
                BuildScannerStatusHeadTabs(previewParameters), 
                opticParameterGroups, 
                opticHeadParameters, 
                scannerStatusItems, 
                snapshot.ProcessStep.ToString(), 
                FormatScriptStatus(snapshot.ScriptStatus), 
                snapshot.Result?.Message ?? "Waiting for operator command.", 
                snapshot.Statistics, 
                FormatProcessResult(snapshot)
            );
            return new CScreenViewModel(EN_MENU.Main, "MAIN", "Automatic operation, head status, script status, and cycle result.", metrics, [], showCycleControls: true, this);
        }

        public async Task<IReadOnlyList<Drilling.Common.InterLock.ST_INTERLOCK_ITEM>> GetAutoModeBlockers(CancellationToken cancellationToken = default)
        {
            // Evaluate the same current snapshot as Interlock/Safety, even before Main is opened.
            var snapshot = stationManager.GetStatus(cancellationToken);
            var parameters = await LoadPreviewParameters(snapshot, cancellationToken);
            var items = LoadLiveInterlockItems(snapshot, parameters, cancellationToken);
            InterlockItems = CMainInterlockGroups.Build(items);
            return items.Where(item => !CMainInterlockGroups.IsOk(item)).ToArray();
        }

        public async Task RefreshLiveStatus(CancellationToken cancellationToken = default)
        {
            var snapshot = stationManager.GetStatus(cancellationToken);
            RefreshSequenceStatus(snapshot);
            var previewParameters = await LoadPreviewParameters(snapshot, cancellationToken);
            ProtectWindow.UpdateRecipe(previewParameters);
            var activeHeadNos = BuildEnabledHeadNumbers(previewParameters);
            NormalizeScannerStatusHeadSelection(activeHeadNos);
            var inspectionStatus = BuildInspectionStatus(snapshot);
            var statistics = snapshot.Statistics;
            CycleItems = [
                new("Cycle State", snapshot.ProcessStep.ToString()), 
                new("Script Status", FormatScriptStatus(snapshot.ScriptStatus)), 
                new("Automation", "Simulation"), 
                new("Preview Source", "Recipe Cell drilling points")
            ];
            ResultItems = [new("Complete Time", snapshot.Result?.CompletedAt.ToString("HH:mm:ss") ?? "-"), new("Result", FormatProcessResult(snapshot)), new("Message", snapshot.Result?.Message ?? "Ready")];
            ProcessSequenceItems = BuildProcessSequenceItems(snapshot.ProcessSequence);
            CurrentStepDetails = [..snapshot.CurrentStepDetails.Select(ToDisplayItem)];
            ProcessSummaryItems = [..snapshot.ProcessSummary.Select(ToDisplayItem)];
            LifecycleItems = [..snapshot.ScriptLifecycleItems.Select(ToDisplayItem)];
            InspectionStatusItems = inspectionStatus.Items;
            InspectionSummary = inspectionStatus.Summary;
            InspectionModeText = inspectionStatus.ModeText;
            InspectionRuleText = inspectionStatus.RuleText;
            InspectionRuleVisibility = inspectionStatus.RuleVisibility;
            var liveInterlockItems = LoadLiveInterlockItems(snapshot, previewParameters, cancellationToken);
            InterlockItems = CMainInterlockGroups.Build(liveInterlockItems);
            HeadParameters = BuildHeadParameters(previewParameters, activeHeadNos);
            ScannerStatusHeadTabs = BuildScannerStatusHeadTabs(previewParameters);
            ProcessStep = snapshot.ProcessStep.ToString();
            ScriptStatus = FormatScriptStatus(snapshot.ScriptStatus);
            ScriptStatusText = $"Script Status       {ScriptStatus}";
            ResultMessage = snapshot.Result?.Message ?? "Waiting for operator command.";
            TotalShotsText = $"Total Shots         {statistics.TotalShots:N0}";
            MoveCountText = $"Move Count (G0)     {statistics.MoveCount:N0}";
            LaserOnSegmentsText = $"Laser On Segments   {statistics.LaserOnSegments:N0}";
            EstimatedTimeText = $"Estimated Time       {FormatDuration(statistics.EstimatedTime)}";
            ElapsedTimeText = $"Elapsed Time         {FormatDuration(statistics.ElapsedTime)}";
            ProgressText = $"Progress             {statistics.ProgressPercent:F1}%";
            ProgressPercentText = $"{statistics.ProgressPercent:F1}%";
            ProgressPercent = statistics.ProgressPercent;
            ProcessResultValue = FormatProcessResult(snapshot);
            ProcessResultBrush = CStatusBrush.ForDisplayState(ProcessResultValue);
            if (IsOpticParameterSelected)
            {
                OpticParameterGroups = await BuildOpticParameterGroups(previewParameters, cancellationToken);
                OpticHeadParameters = await BuildOpticHeadParameters(previewParameters, cancellationToken);
            }

            RefreshMotorStatus();
            if (IsScannerStatusSelected)
            {
                _scannerStatusSnapshotReader.SelectHead(_selectedScannerStatusHeadNo, previewParameters);
                ScannerStatusItems = _scannerStatusSnapshotReader.GetSnapshot();
            }

            var scriptTaskStatusItems = await BuildScriptTaskStatusItems(snapshot, previewParameters, cancellationToken);
            ScriptTaskStatusItems = scriptTaskStatusItems;
            var selectedHeadNos = new HashSet<int>();
            var powerSnapshot = powerMeasure?.GetSnapshot();
            SetHeadPreviews(Enumerable.Range(1, 8).Select(headNo => BuildHeadPreviewItem(headNo, _previewHeadShotCounts.TryGetValue(headNo, out var count) ? count : 0, snapshot, selectedHeadNos, liveInterlockItems, scriptTaskStatusItems, activeHeadNos.Contains(headNo), powerSnapshot)).ToArray());
            AutomationControllerStatusItems = await BuildAutomationControllerStatusItems(scriptTaskStatusItems, cancellationToken);
        }

        private void SetHeadPreviews(IReadOnlyList<ST_HEAD_PREVIEW> headPreviews)
        {
            HeadPreviews = headPreviews;
            OddHeadPreviews = headPreviews.Where(head => head.HeadNo % 2 != 0).OrderBy(head => head.HeadNo).ToArray();
            EvenHeadPreviews = headPreviews.Where(head => head.HeadNo % 2 == 0).OrderBy(head => head.HeadNo).ToArray();
        }

        private void Apply(
            IReadOnlyList<ST_HEAD_PREVIEW> headPreviews, 
            IReadOnlyList<ST_DISPLAY_ITEM> cycleItems, 
            IReadOnlyList<ST_DISPLAY_ITEM> resultItems, 
            IReadOnlyList<ST_MAIN_PROCESS_SEQUENCE_ITEM> processSequenceItems, 
            IReadOnlyList<ST_DISPLAY_ITEM> currentStepDetails, 
            IReadOnlyList<ST_DISPLAY_ITEM> processSummaryItems, 
            IReadOnlyList<ST_DISPLAY_ITEM> lifecycleItems, 
            IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM> scriptTaskStatusItems, 
            IReadOnlyList<ST_AUTOMATION_CONTROLLER_STATUS_ITEM> automationControllerStatusItems, 
            IReadOnlyList<ST_INSPECTION_STATUS_ITEM> inspectionStatusItems, 
            string inspectionSummary, 
            string inspectionModeText, 
            string inspectionRuleText, 
            Visibility inspectionRuleVisibility, 
            IReadOnlyList<ST_INTERLOCK_GROUP> interlockItems, 
            IReadOnlyList<ST_HEAD_PARAMETER> headParameters, 
            IReadOnlyList<ST_MAIN_PARAMETER_TAB_ITEM> parameterTabs, 
            IReadOnlyList<ST_MAIN_PARAMETER_TAB_ITEM> opticStatusTabs, 
            IReadOnlyList<ST_MAIN_PARAMETER_TAB_ITEM> scannerStatusHeadTabs, 
            IReadOnlyList<ST_OPTIC_PARAMETER_GROUP> opticParameterGroups, 
            IReadOnlyList<ST_OPTIC_HEAD_PARAMETER> opticHeadParameters, 
            IReadOnlyList<ST_SCANNER_AXIS_STATUS_ITEM> scannerStatusItems, 
            string processStep, 
            string scriptStatus, 
            string resultMessage, 
            ST_PROCESS_STATISTICS statistics, 
            string processResultValue
        )
        {
            SetHeadPreviews(headPreviews);
            CycleItems = cycleItems;
            ResultItems = resultItems;
            ProcessSequenceItems = processSequenceItems;
            CurrentStepDetails = currentStepDetails;
            ProcessSummaryItems = processSummaryItems;
            LifecycleItems = lifecycleItems;
            ScriptTaskStatusItems = scriptTaskStatusItems;
            AutomationControllerStatusItems = automationControllerStatusItems;
            InspectionStatusItems = inspectionStatusItems;
            InspectionSummary = inspectionSummary;
            InspectionModeText = inspectionModeText;
            InspectionRuleText = inspectionRuleText;
            InspectionRuleVisibility = inspectionRuleVisibility;
            InterlockItems = interlockItems;
            HeadParameters = headParameters;
            ParameterTabs = parameterTabs;
            OpticStatusTabs = opticStatusTabs;
            ScannerStatusHeadTabs = scannerStatusHeadTabs;
            OpticParameterGroups = opticParameterGroups;
            OpticHeadParameters = opticHeadParameters;
            ScannerStatusItems = scannerStatusItems;
            MotorStatusVisibility = IsMotorStatusSelected ? Visibility.Visible : Visibility.Collapsed;
            HeadParameterVisibility = _selectedParameterTabKey.Equals(ParameterTabHead, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
            OpticParameterVisibility = _selectedParameterTabKey.Equals(ParameterTabOptic, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
            ScannerStatusVisibility = _selectedParameterTabKey.Equals(ParameterTabScanner, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
            ProcessStep = processStep;
            ScriptStatus = scriptStatus;
            ScriptStatusText = $"Script Status       {scriptStatus}";
            ResultMessage = resultMessage;
            TotalShotsText = $"Total Shots         {statistics.TotalShots:N0}";
            MoveCountText = $"Move Count (G0)     {statistics.MoveCount:N0}";
            LaserOnSegmentsText = $"Laser On Segments   {statistics.LaserOnSegments:N0}";
            EstimatedTimeText = $"Estimated Time       {FormatDuration(statistics.EstimatedTime)}";
            ElapsedTimeText = $"Elapsed Time         {FormatDuration(statistics.ElapsedTime)}";
            ProgressText = $"Progress             {statistics.ProgressPercent:F1}%";
            ProgressPercentText = $"{statistics.ProgressPercent:F1}%";
            ProgressPercent = statistics.ProgressPercent;
            ProcessResultValue = processResultValue;
            ProcessResultBrush = CStatusBrush.ForDisplayState(processResultValue);
        }

        public static string FormatScriptStatus(EN_SCRIPT_STATUS status)
        {
            return status switch
            {
                EN_SCRIPT_STATUS.NotCreated => "Not Created",
                _ => status.ToString()};
        }

        private static ST_DISPLAY_ITEM ToDisplayItem(ST_PROCESS_DISPLAY_ITEM item)
        {
            return new ST_DISPLAY_ITEM(item.Name, item.Value, item.Detail);
        }

        private static IReadOnlyList<ST_MAIN_PROCESS_SEQUENCE_ITEM> BuildProcessSequenceItems(IReadOnlyList<ST_PROCESS_DISPLAY_ITEM> processSequence) => processSequence.Select(item => new ST_MAIN_PROCESS_SEQUENCE_ITEM(item.Name, item.Value, item.Detail)).ToArray();
        private async Task SelectParameterTab(object? parameter)
        {
            var tabKey = parameter is ST_MAIN_PARAMETER_TAB_ITEM tabItem ? tabItem.Key : parameter?.ToString();
            tabKey = tabKey?.Trim().ToUpperInvariant();
            if (tabKey is not ParameterTabRunOption and not ParameterTabProtectWindow and not ParameterTabHead and not ParameterTabOptic and not ParameterTabScanner and not ParameterTabMotor)
            {
                return;
            }

            if (_selectedParameterTabKey.Equals(tabKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StopScannerStatusPolling();
            _selectedParameterTabKey = tabKey;
            RunOptions.SetVisible(tabKey == ParameterTabRunOption);
            ProtectWindow.SetVisible(tabKey == ParameterTabProtectWindow);
            if (IsScannerStatusSelected)
            {
                _scannerStatusSnapshotReader.SelectHead(_selectedScannerStatusHeadNo);
            }

            await refreshCurrentScreen();
        }

        private async Task SelectOpticStatus(object? parameter)
        {
            var statusKey = parameter is ST_MAIN_PARAMETER_TAB_ITEM tabItem ? tabItem.Key : parameter?.ToString();
            statusKey = statusKey?.Trim().ToUpperInvariant();
            if (statusKey is not OpticStatusLaser and not OpticStatusAttenuator and not OpticStatusBet)
            {
                return;
            }

            if (_selectedOpticStatusKey.Equals(statusKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedOpticStatusKey = statusKey;
            NotifyOpticStatusSelectionChanged();
            await refreshCurrentScreen();
        }

        private async Task SelectScannerStatusHead(object? parameter)
        {
            if (!CanSelectScannerStatusHead(parameter))
            {
                return;
            }

            var key = parameter is ST_MAIN_PARAMETER_TAB_ITEM tabItem ? tabItem.Key : parameter?.ToString();
            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var headNo))
            {
                return;
            }

            var normalizedHeadNo = Math.Clamp(headNo, 1, 8);
            if (_selectedScannerStatusHeadNo == normalizedHeadNo)
            {
                return;
            }

            _selectedScannerStatusHeadNo = normalizedHeadNo;
            _scannerStatusSnapshotReader.SelectHead(_selectedScannerStatusHeadNo);
            await refreshCurrentScreen();
        }

        private void NormalizeScannerStatusHeadSelection(IReadOnlyList<int> enabledHeadNos)
        {
            if (enabledHeadNos.Count == 0 || enabledHeadNos.Contains(_selectedScannerStatusHeadNo))
            {
                return;
            }

            _selectedScannerStatusHeadNo = enabledHeadNos[0];
            _scannerStatusSnapshotReader.SelectHead(_selectedScannerStatusHeadNo);
        }

        private IReadOnlyList<ST_MAIN_PARAMETER_TAB_ITEM> BuildParameterTabs()
        {
            return[
                new(ParameterTabRunOption, "Run Option", _selectedParameterTabKey.Equals(ParameterTabRunOption, StringComparison.OrdinalIgnoreCase)), 
                new(ParameterTabProtectWindow, "Protect Window", _selectedParameterTabKey.Equals(ParameterTabProtectWindow, StringComparison.OrdinalIgnoreCase)), 
                new(ParameterTabHead, "Head Parameter", _selectedParameterTabKey.Equals(ParameterTabHead, StringComparison.OrdinalIgnoreCase)), 
                new(ParameterTabOptic, "Optic Parameter", _selectedParameterTabKey.Equals(ParameterTabOptic, StringComparison.OrdinalIgnoreCase)), 
                new(ParameterTabScanner, "Scanner Status", _selectedParameterTabKey.Equals(ParameterTabScanner, StringComparison.OrdinalIgnoreCase)), 
                new(ParameterTabMotor, "Motor Status", IsMotorStatusSelected)
            ];
        }

        private IReadOnlyList<ST_MAIN_PARAMETER_TAB_ITEM> BuildOpticStatusTabs()
        {
            return[
                new(OpticStatusLaser, "LASER", IsOpticStatusSelected(OpticStatusLaser)), 
                new(OpticStatusAttenuator, "ATT", IsOpticStatusSelected(OpticStatusAttenuator)), 
                new(OpticStatusBet, "BET", IsOpticStatusSelected(OpticStatusBet))
            ];
        }

        private IReadOnlyList<ST_MAIN_PARAMETER_TAB_ITEM> BuildScannerStatusHeadTabs(IReadOnlyDictionary<string, string> parameters)
        {
            const int headCount = 8;
            var tabs = new List<ST_MAIN_PARAMETER_TAB_ITEM>(headCount);
            for (var headNo = 1; headNo <= headCount; headNo++)
            {
                var isEnabled = IsHeadEnabled(parameters, headNo);
                var name = isEnabled ? $"H{headNo:00}" : $"H{headNo:00} NOT USE";
                tabs.Add(new ST_MAIN_PARAMETER_TAB_ITEM(headNo.ToString(CultureInfo.InvariantCulture), name, isEnabled && _selectedScannerStatusHeadNo == headNo, isEnabled));
            }

            return tabs;
        }

        private static bool CanSelectScannerStatusHead(object? parameter)
        {
            return parameter switch
            {
                ST_MAIN_PARAMETER_TAB_ITEM tabItem => tabItem.IsEnabled,
                _ => true
            };
        }

        private bool IsOpticStatusSelected(string statusKey)
        {
            return _selectedOpticStatusKey.Equals(statusKey, StringComparison.OrdinalIgnoreCase);
        }

        private void NotifyOpticStatusSelectionChanged()
        {
            OnPropertyChanged(nameof(OpticLaserVisibility));
            OnPropertyChanged(nameof(OpticAttenuatorVisibility));
            OnPropertyChanged(nameof(OpticBetVisibility));
        }

        public void StopScannerStatusPolling()
        {
            try
            {
                _motorStatusPollingService.Stop();
                _motorStatusPollingService.ClearSnapshot();
            }
            catch (TimeoutException exception)
            {
                statusReporter($"Motor status polling stop timeout. {exception.Message}");
            }
        }

        // Shared presentation path for Main and Manual MOF. Reads memory snapshots only.
        public IReadOnlyList<ST_AUTOMATION_CONTROLLER_STATUS_ITEM> ReadAutomationControllerStatusSnapshot(ST_STATION_PROCESS_STATUS snapshot, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default) => BuildAutomationControllerStatusSnapshot(BuildScriptTaskStatusSnapshot(snapshot, parameters, cancellationToken), cancellationToken);
        private Task<IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM>> BuildScriptTaskStatusItems(ST_STATION_PROCESS_STATUS snapshot, IReadOnlyDictionary<string, string> previewParameters, CancellationToken cancellationToken) => Task.FromResult(BuildScriptTaskStatusSnapshot(snapshot, previewParameters, cancellationToken));
        private IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM> BuildScriptTaskStatusSnapshot(ST_STATION_PROCESS_STATUS snapshot, IReadOnlyDictionary<string, string> previewParameters, CancellationToken cancellationToken)
        {
            var definitions = BuildScriptTaskDefinitions(snapshot, previewParameters);
            var statusCache = new Dictionary<(int AutomationNo, int TaskNo), ST_SCRIPT_TASK_STATUS_VALUE>();
            var nextStatusValues = new Dictionary<(int AutomationNo, int TaskNo), ST_SCRIPT_TASK_STATUS_VALUE>(_lastScriptTaskStatusValues);
            var items = new List<ST_SCRIPT_TASK_STATUS_ITEM>(definitions.Count);
            foreach (var definition in definitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = (definition.AutomationNo, definition.TaskNo);
                if (!statusCache.TryGetValue(key, out var status))
                {
                    status = ReadScriptTaskStatusSnapshot(definition.AutomationNo, definition.TaskNo, definition.ScriptFileName, cancellationToken);
                    if (_lastScriptTaskStatusValues.TryGetValue(key, out var previousStatus) && ShouldKeepPreviousScriptTaskStatus(snapshot, status, previousStatus))
                    {
                        status = previousStatus;
                    }
                    else
                    {
                        nextStatusValues[key] = status;
                    }

                    statusCache[key] = status;
                }

                var displayStatus = NormalizeScriptTaskStatusForDisplay(status);
                items.Add(new ST_SCRIPT_TASK_STATUS_ITEM(
                    $"TASK {definition.TaskNo}", 
                    $"H{definition.HeadNo:00}", 
                    $"A{definition.AutomationNo}", 
                    $"T{definition.TaskNo}", 
                    displayStatus.State, 
                    string.IsNullOrWhiteSpace(displayStatus.FileName) ? definition.ScriptFileName : displayStatus.FileName, 
                    displayStatus.Detail, 
                    definition.TotalShots
                ) { IsSimulation = automationManager.IsSimul(definition.AutomationNo) });
            }

            _lastScriptTaskStatusValues = nextStatusValues;
            return items;
        }

        private Task<IReadOnlyList<ST_AUTOMATION_CONTROLLER_STATUS_ITEM>> BuildAutomationControllerStatusItems(IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM> taskStatusItems, CancellationToken cancellationToken) => Task.FromResult(BuildAutomationControllerStatusSnapshot(taskStatusItems, cancellationToken));
        private IReadOnlyList<ST_AUTOMATION_CONTROLLER_STATUS_ITEM> BuildAutomationControllerStatusSnapshot(IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM> taskStatusItems, CancellationToken cancellationToken)
        {
            var controllerNos = BuildAutomationControllerNumbers(taskStatusItems);
            var items = new List<ST_AUTOMATION_CONTROLLER_STATUS_ITEM>(controllerNos.Count);
            foreach (var automationNo in controllerNos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var usage = ReadAutomationControllerUsageSnapshot(automationNo, cancellationToken);
                items.Add(new ST_AUTOMATION_CONTROLLER_STATUS_ITEM(automationNo, usage.State, usage.Percent, usage.Detail, BuildControllerTaskStatusItems(taskStatusItems, automationNo)));
            }

            return items;
        }

        private Task<ST_AUTOMATION_CONTROLLER_USAGE_VALUE> ReadAutomationControllerUsage(int automationNo, CancellationToken cancellationToken) => Task.FromResult(ReadAutomationControllerUsageSnapshot(automationNo, cancellationToken));
        private ST_AUTOMATION_CONTROLLER_USAGE_VALUE ReadAutomationControllerUsageSnapshot(int automationNo, CancellationToken cancellationToken)
        {
            try
            {
                if (automationManager.IsSimul(automationNo))
                {
                    return new ST_AUTOMATION_CONTROLLER_USAGE_VALUE("SIM", 0.0, "Simulation mode");
                }

                if (!automationManager.IsConnect(automationNo))
                {
                    return new ST_AUTOMATION_CONTROLLER_USAGE_VALUE("OFFLINE", 0.0, "Automation1 is not connected");
                }

                var cached = automationManager.StatusSnapshots.GetProcessorUsage(automationNo);
                if (cached.Value is not { } response)
                    return new ST_AUTOMATION_CONTROLLER_USAGE_VALUE(cached.State == "LOADING" ? "WAIT" : cached.State, 0, cached.Detail);
                return ParseAutomationControllerUsage(response);
            }
            catch (Exception exception)when (exception is not OperationCanceledException)
            {
                return new ST_AUTOMATION_CONTROLLER_USAGE_VALUE("ERROR", 0.0, exception.Message);
            }
        }

        private static ST_AUTOMATION_CONTROLLER_USAGE_VALUE ParseLiveAutomationControllerUsage(string processorUsage)
        {
            var value = string.IsNullOrWhiteSpace(processorUsage) ? "WAIT" : processorUsage.Trim();
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var usage))
            {
                var clampedUsage = Math.Clamp(usage, 0.0, 100.0);
                var state = clampedUsage >= 90.0 ? "WARN" : "OK";
                return new ST_AUTOMATION_CONTROLLER_USAGE_VALUE(state, clampedUsage, "Live scanner status snapshot");
            }

            var normalized = value.ToUpperInvariant();
            if (normalized.Equals("SIM", StringComparison.Ordinal))
            {
                return new ST_AUTOMATION_CONTROLLER_USAGE_VALUE("SIM", 0.0, "Simulation mode");
            }

            if (normalized.Equals("OFFLINE", StringComparison.Ordinal))
            {
                return new ST_AUTOMATION_CONTROLLER_USAGE_VALUE("OFFLINE", 0.0, "Automation1 is not connected");
            }

            if (normalized.Equals("ERROR", StringComparison.Ordinal))
            {
                return new ST_AUTOMATION_CONTROLLER_USAGE_VALUE("ERROR", 0.0, "Live scanner status snapshot error");
            }

            return new ST_AUTOMATION_CONTROLLER_USAGE_VALUE("WAIT", 0.0, "Waiting for live scanner status snapshot");
        }

        private static ST_AUTOMATION_CONTROLLER_USAGE_VALUE ParseAutomationControllerUsage(string response)
        {
            var fields = response.Split(':');
            if (fields.Length >= 4 && fields[0].Equals("OK", StringComparison.OrdinalIgnoreCase) && fields[1].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) && fields[2].Equals("PROCESSOR_USAGE", StringComparison.OrdinalIgnoreCase) && double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var usage))
            {
                var clampedUsage = Math.Clamp(usage, 0.0, 100.0);
                var state = clampedUsage >= 90.0 ? "WARN" : "OK";
                return new ST_AUTOMATION_CONTROLLER_USAGE_VALUE(state, clampedUsage, "Estimated SMC processor usage");
            }

            return response.StartsWith("OK", StringComparison.OrdinalIgnoreCase) ? new ST_AUTOMATION_CONTROLLER_USAGE_VALUE("OK", 0.0, response) : new ST_AUTOMATION_CONTROLLER_USAGE_VALUE("ERROR", 0.0, response);
        }

        private static IReadOnlyList<ST_AUTOMATION_CONTROLLER_STATUS_ITEM> BuildAutomationControllerStatusPlaceholders(IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM> taskStatusItems)
        {
            var controllerNos = BuildAutomationControllerNumbers(taskStatusItems);
            var items = new List<ST_AUTOMATION_CONTROLLER_STATUS_ITEM>(controllerNos.Count);
            foreach (var automationNo in controllerNos)
            {
                items.Add(new ST_AUTOMATION_CONTROLLER_STATUS_ITEM(automationNo, "WAIT", 0.0, "Waiting for controller status", BuildControllerTaskStatusItems(taskStatusItems, automationNo)));
            }

            return items;
        }

        private static IReadOnlyList<int> BuildAutomationControllerNumbers(IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM> taskStatusItems)
        {
            var controllerNos = new SortedSet<int>
            {
                0,
                1
            };
            foreach (var item in taskStatusItems)
            {
                controllerNos.Add(item.AutomationNoValue);
            }

            return controllerNos.ToArray();
        }

        private static IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM> BuildControllerTaskStatusItems(IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM> taskStatusItems, int automationNo)
        {
            int SelectHeadNoValue(global::Drilling.UI.Menu.Menus.ST_SCRIPT_TASK_STATUS_ITEM item)
            {
                return item.HeadNoValue;
            }

            string SelectHead(global::Drilling.UI.Menu.Menus.ST_SCRIPT_TASK_STATUS_ITEM item)
            {
                return item.Head;
            }

            bool ShouldIncludeItem(global::Drilling.UI.Menu.Menus.ST_SCRIPT_TASK_STATUS_ITEM item)
            {
                return item.AutomationNoValue == automationNo;
            }

            return taskStatusItems.Where(ShouldIncludeItem).OrderBy(SelectHeadNoValue).ThenBy(SelectHead).ToArray();
        }

        private static IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM> BuildScriptTaskStatusPlaceholders(ST_STATION_PROCESS_STATUS snapshot, IReadOnlyDictionary<string, string> previewParameters)
        {
            global::Drilling.UI.Menu.Menus.ST_SCRIPT_TASK_STATUS_ITEM BuildScript(global::Drilling.UI.Menu.Menus.ST_SCRIPT_TASK_DEFINITION definition)
            {
                return new ST_SCRIPT_TASK_STATUS_ITEM($"TASK {definition.TaskNo}", $"H{definition.HeadNo:00}", $"A{definition.AutomationNo}", $"T{definition.TaskNo}", "WAIT", definition.ScriptFileName, "Waiting for status", definition.TotalShots);
            }

            return BuildScriptTaskDefinitions(snapshot, previewParameters).Select(BuildScript).ToArray();
        }

        private static IReadOnlyList<ST_SCRIPT_TASK_DEFINITION> BuildScriptTaskDefinitions(ST_STATION_PROCESS_STATUS snapshot, IReadOnlyDictionary<string, string> previewParameters)
        {
            int SelectHeadNo(global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN head)
            {
                return head.HeadNo;
            }

            int SelectKey(global::System.Linq.IGrouping<int, global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN> group)
            {
                return group.Key;
            }

            global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN SelectValue(global::System.Linq.IGrouping<int, global::Drilling.Common.Station.ST_HEAD_PROCESS_PLAN> group)
            {
                return group.Last();
            }

            var headMap = ShouldUseProcessModelTaskDefinitions(snapshot) ? snapshot.ProcessModel?.HeadPlans.GroupBy(SelectHeadNo).ToDictionary(SelectKey, SelectValue) ?? [] : [];
            global::Drilling.UI.Menu.Menus.ST_SCRIPT_TASK_DEFINITION ifCallback(int headNo)
            {
                if (headMap.TryGetValue(headNo, out var head))
                {
                    return new ST_SCRIPT_TASK_DEFINITION(headNo, head.AutomationNo, head.TaskNo, head.ScriptFileName, head.Shots.Count);
                }

                return new ST_SCRIPT_TASK_DEFINITION(headNo, ReadMainHeadAutomationNo(previewParameters, headNo), ReadMainHeadAutomationTaskNo(previewParameters, headNo), $"PROCESS_H{headNo:00}.ascript", 0);
            }

            var enabledHeadNos = BuildEnabledHeadNumbers(previewParameters);
            var definitions = new List<ST_SCRIPT_TASK_DEFINITION>(enabledHeadNos.Count);
            foreach (var headNo in enabledHeadNos)
            {
                definitions.Add(ifCallback(headNo));
            }

            return definitions;
        }

        private static bool ShouldUseProcessModelTaskDefinitions(ST_STATION_PROCESS_STATUS snapshot)
        {
            if (snapshot.ProcessModel is null)
            {
                return false;
            }

            if (snapshot.ScriptStatus is EN_SCRIPT_STATUS.Created or EN_SCRIPT_STATUS.Running)
            {
                return true;
            }

            return snapshot.ProcessStep is EN_PROCESS_STEP.PowerCheck or EN_PROCESS_STEP.Align or EN_PROCESS_STEP.Process or EN_PROCESS_STEP.Inspection;
        }

        private Task<ST_SCRIPT_TASK_STATUS_VALUE> ReadScriptTaskStatus(int automationNo, int taskNo, string defaultFileName, CancellationToken cancellationToken) => Task.FromResult(ReadScriptTaskStatusSnapshot(automationNo, taskNo, defaultFileName, cancellationToken));
        private ST_SCRIPT_TASK_STATUS_VALUE ReadScriptTaskStatusSnapshot(int automationNo, int taskNo, string defaultFileName, CancellationToken cancellationToken)
        {
            try
            {
                if (!automationManager.IsSimul(automationNo) && !automationManager.IsConnect(automationNo))
                {
                    return new ST_SCRIPT_TASK_STATUS_VALUE("OFFLINE", defaultFileName, "Automation1 is not connected");
                }

                var cached = automationManager.StatusSnapshots.GetTask(taskNo, automationNo);
                if (cached.Value is not { } response)
                    return new ST_SCRIPT_TASK_STATUS_VALUE(cached.State == "LOADING" ? "WAIT" : cached.State, defaultFileName, cached.Detail);
                var status = ParseScriptTaskStatus(response, defaultFileName);
                return automationManager.IsSimul(automationNo) ? status with
                {
                    Detail = $"SIMULATION · {status.Detail}"}

                : status;
            }
            catch (Exception exception)when (exception is not OperationCanceledException)
            {
                return new ST_SCRIPT_TASK_STATUS_VALUE("ERROR", defaultFileName, exception.Message);
            }
        }

        private static bool ShouldKeepPreviousScriptTaskStatus(ST_STATION_PROCESS_STATUS snapshot, ST_SCRIPT_TASK_STATUS_VALUE status, ST_SCRIPT_TASK_STATUS_VALUE previousStatus)
        {
            if (snapshot.ScriptStatus != EN_SCRIPT_STATUS.Running)
            {
                return false;
            }

            var state = status.State.Trim();
            if (IsBufferedRunPendingTaskState(state))
            {
                return previousStatus.State.Equals("RUNNING", StringComparison.OrdinalIgnoreCase);
            }

            return state.Equals("WAIT", StringComparison.OrdinalIgnoreCase);
        }

        private static ST_SCRIPT_TASK_STATUS_VALUE NormalizeScriptTaskStatusForDisplay(ST_SCRIPT_TASK_STATUS_VALUE status)
        {
            if (IsBufferedRunPendingTaskState(status.State))
            {
                return status with
                {
                    State = "WAIT"
                };
            }

            return status;
        }

        private static ST_SCRIPT_TASK_STATUS_VALUE ParseScriptTaskStatus(string response, string defaultFileName)
        {
            var automationError = FormatAutomationErrorResponse(response);
            if (!string.IsNullOrWhiteSpace(automationError))
            {
                return new ST_SCRIPT_TASK_STATUS_VALUE("ERROR", defaultFileName, automationError);
            }

            var fields = response.Split(':');
            if (fields.Length >= 4 && fields[0].Equals("OK", StringComparison.OrdinalIgnoreCase) && fields[1].Equals("TASK", StringComparison.OrdinalIgnoreCase))
            {
                var state = FormatScriptTaskState(fields[3]);
                var fileName = fields.Length >= 5 && !string.IsNullOrWhiteSpace(fields[4]) ? fields[4].Trim() : defaultFileName;
                var error = fields.Length >= 6 ? fields[5].Trim() : "";
                var detail = string.IsNullOrWhiteSpace(error) ? "OK" : error;
                return new ST_SCRIPT_TASK_STATUS_VALUE(state, fileName, detail);
            }

            return response.StartsWith("OK", StringComparison.OrdinalIgnoreCase) ? new ST_SCRIPT_TASK_STATUS_VALUE("OK", defaultFileName, response) : new ST_SCRIPT_TASK_STATUS_VALUE("ERROR", defaultFileName, response);
        }

        private static string FormatAutomationErrorResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return "";
            }

            var fields = response.Trim().Split(':', 4, StringSplitOptions.TrimEntries);
            if (fields.Length >= 4 && fields[0].Equals("ERROR", StringComparison.OrdinalIgnoreCase) && fields[1].Equals("AUTOMATION1", StringComparison.OrdinalIgnoreCase))
            {
                return $"{fields[2]}: {fields[3]}";
            }

            return "";
        }

        private static string FormatScriptTaskState(string state)
        {
            var normalized = state.Trim().Replace("_", " ", StringComparison.Ordinal).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return "UNKNOWN";
            }

            if (normalized.Equals("QUEUERUNNING", StringComparison.OrdinalIgnoreCase))
            {
                return "RUNNING";
            }

            if (normalized.Equals("QUEUECOMPLETE", StringComparison.OrdinalIgnoreCase))
            {
                return "DONE";
            }

            if (IsBufferedRunPendingTaskState(normalized))
            {
                return "QUEUEPENDING";
            }

            if (normalized.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || normalized.Contains("FAULT", StringComparison.OrdinalIgnoreCase))
            {
                return "ERROR";
            }

            if (normalized.Contains("RUN", StringComparison.OrdinalIgnoreCase) || normalized.Contains("EXEC", StringComparison.OrdinalIgnoreCase))
            {
                return "RUNNING";
            }

            if (normalized.Contains("STOP", StringComparison.OrdinalIgnoreCase))
            {
                return "STOPPED";
            }

            if (normalized.Contains("IDLE", StringComparison.OrdinalIgnoreCase) || normalized.Contains("INACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                return "IDLE";
            }

            if (normalized.Contains("DONE", StringComparison.OrdinalIgnoreCase) || normalized.Contains("COMPLETE", StringComparison.OrdinalIgnoreCase))
            {
                return "DONE";
            }

            return normalized;
        }

        private static bool IsBufferedRunPendingTaskState(string state)
        {
            return state.Equals("QUEUEPENDING", StringComparison.OrdinalIgnoreCase) || state.Equals("QUEUESTARTING", StringComparison.OrdinalIgnoreCase) || state.Equals("QUEUERUNNING", StringComparison.OrdinalIgnoreCase) || state.Equals("QUEUECOMPLETE", StringComparison.OrdinalIgnoreCase);
        }

        private static int ReadMainHeadAutomationNo(IReadOnlyDictionary<string, string> parameters, int headNo)
        {
            return Math.Max(0, ReadIntAny(parameters, headNo <= 4 ? 0 : 1, $"H{headNo:00}_AUTOMATION_NO"));
        }

        private static int ReadMainHeadAutomationTaskNo(IReadOnlyDictionary<string, string> parameters, int headNo)
        {
            return Math.Max(1, ReadIntAny(parameters, ((headNo - 1) % 4) + 1, $"H{headNo:00}_AUTOMATION_TASK_NO"));
        }

        private Task<IReadOnlyList<ST_OPTIC_PARAMETER_GROUP>> BuildOpticParameterGroups(IReadOnlyDictionary<string, string> previewParameters, CancellationToken cancellationToken)
        {
            var groups = new List<ST_OPTIC_PARAMETER_GROUP>();
            return Task.FromResult<IReadOnlyList<ST_OPTIC_PARAMETER_GROUP>>(groups);
        }

        private async Task<IReadOnlyList<ST_OPTIC_HEAD_PARAMETER>> BuildOpticHeadParameters(IReadOnlyDictionary<string, string> previewParameters, CancellationToken cancellationToken)
        {
            var laserDevices = IsOpticStatusSelected(OpticStatusLaser) ? BuildOpticDeviceMap(EN_EQP_MODULE.TalonLaser) : new Dictionary<int, int>();
            var attenuatorDevices = IsOpticStatusSelected(OpticStatusAttenuator) ? BuildOpticDeviceMap(EN_EQP_MODULE.Attenuator) : new Dictionary<int, int>();
            var betDevices = IsOpticStatusSelected(OpticStatusBet) ? BuildOpticDeviceMap(EN_EQP_MODULE.Bet) : new Dictionary<int, int>();
            const int headCount = 8;
            var items = new List<ST_OPTIC_HEAD_PARAMETER>(headCount);
            for (var headNo = 1; headNo <= headCount; headNo++)
            {
                if (!IsHeadEnabled(previewParameters, headNo))
                {
                    items.Add(CreateNotUseOpticHeadParameter(headNo));
                    continue;
                }

                var laser = CreateEmptyLaserHeadParameter();
                var attenuator = CreateEmptyAxisHeadParameter(previewParameters, headNo);
                var bet = CreateEmptyBetHeadParameter(previewParameters, headNo);
                var state = "N/C";
                if (IsOpticStatusSelected(OpticStatusLaser))
                {
                    laser = await BuildLaserHeadParameter(headNo, laserDevices, cancellationToken);
                    state = FormatOpticGroupState([
                        new ST_OPTIC_PARAMETER_ITEM("POWER", laser.Power, "", laser.State), 
                        new ST_OPTIC_PARAMETER_ITEM("GATE", laser.Gate, "", laser.Gate), 
                        new ST_OPTIC_PARAMETER_ITEM("SHUTTER", laser.Shutter, "", laser.Shutter)
                    ]);
                }
                else if (IsOpticStatusSelected(OpticStatusAttenuator))
                {
                    attenuator = await BuildAttenuatorHeadParameter(headNo, attenuatorDevices, previewParameters, cancellationToken);
                    state = attenuator.State;
                }
                else if (IsOpticStatusSelected(OpticStatusBet))
                {
                    bet = await BuildBetHeadParameter(headNo, betDevices, previewParameters, cancellationToken);
                    state = bet.State;
                }

                items.Add(new ST_OPTIC_HEAD_PARAMETER(
                    $"H{headNo:00}", 
                    state, 
                    laser.Power, 
                    laser.Gate, 
                    laser.Shutter, 
                    laser.State, 
                    attenuator.Current, 
                    attenuator.Target, 
                    attenuator.State, 
                    bet.MagnificationCurrentStep, 
                    bet.MagnificationTargetStep, 
                    bet.DivergenceCurrentStep, 
                    bet.DivergenceTargetStep, 
                    bet.State
                ));
            }

            return items;
        }

        private static ST_OPTIC_HEAD_PARAMETER CreateNotUseOpticHeadParameter(int headNo)
        {
            return new ST_OPTIC_HEAD_PARAMETER($"H{headNo:00}", "NOT USE", "-", "-", "-", "NOT USE", "-", "-", "NOT USE", "-", "-", "-", "-", "NOT USE");
        }

        private static ST_OPTIC_LASER_HEAD_PARAMETER CreateEmptyLaserHeadParameter()
        {
            return new ST_OPTIC_LASER_HEAD_PARAMETER("-", "-", "-", "N/C");
        }

        private static ST_OPTIC_AXIS_HEAD_PARAMETER CreateEmptyAxisHeadParameter(IReadOnlyDictionary<string, string> previewParameters, int headNo)
        {
            var target = ReadDoubleAny(previewParameters, 23.50, $"H{headNo:00}_ATTENUATOR_POSITION");
            return new ST_OPTIC_AXIS_HEAD_PARAMETER("-", FormatOpticValue(target, "deg"), "N/C");
        }

        private static ST_OPTIC_BET_HEAD_PARAMETER CreateEmptyBetHeadParameter(IReadOnlyDictionary<string, string> previewParameters, int headNo)
        {
            var targetMag = ReadDoubleAny(
                previewParameters, 
                1.0, 
                $"H{headNo:00}_BET_MAGNIFICATION", 
                $"H{headNo:00}_BET_TARGET_MAGNIFICATION", 
                "BET_MAGNIFICATION", 
                "BET_TARGET_MAGNIFICATION", 
                "BET_MAG_TARGET", 
                "BET_MAG", 
                "BEAM_EXPANDER_MAGNIFICATION"
            );
            var targetDiv = ReadDoubleAny(previewParameters, 1.0, $"H{headNo:00}_BET_DIVERGENCE", $"H{headNo:00}_BET_TARGET_DIVERGENCE", "BET_DIVERGENCE", "BET_TARGET_DIVERGENCE", "BET_DIV_TARGET", "BET_DIV", "BEAM_EXPANDER_DIVERGENCE");
            return new ST_OPTIC_BET_HEAD_PARAMETER("-", FormatBetStep(targetMag), "-", FormatBetStep(targetDiv), "N/C");
        }

        private IReadOnlyDictionary<int, int> BuildOpticDeviceMap(EN_EQP_MODULE module)
        {
            int SelectValue(global::Drilling.Common.Interface.ST_INTERFACE_DATA device)
            {
                return Math.Clamp(device.Number + 1, 1, 8);
            }

            int SelectKey(global::System.Linq.IGrouping<int, global::Drilling.Common.Interface.ST_INTERFACE_DATA> group)
            {
                return group.Key;
            }

            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_DATA device)
            {
                return device.Number;
            }

            string SelectNickName(global::Drilling.Common.Interface.ST_INTERFACE_DATA device)
            {
                return device.NickName;
            }

            int SelectInt(global::System.Linq.IGrouping<int, global::Drilling.Common.Interface.ST_INTERFACE_DATA> group)
            {
                return group.OrderBy(SelectNumber).ThenBy(SelectNickName, StringComparer.OrdinalIgnoreCase).First().Number;
            }

            return interfaceManager.GetInterfaceList(module).GroupBy(SelectValue).ToDictionary(SelectKey, SelectInt);
        }

        private Task<ST_OPTIC_LASER_HEAD_PARAMETER> BuildLaserHeadParameter(int headNo, IReadOnlyDictionary<int, int> devices, CancellationToken cancellationToken)
        {
            if (!devices.TryGetValue(headNo, out var deviceNo))
            {
                return Task.FromResult(new ST_OPTIC_LASER_HEAD_PARAMETER("-", "-", "-", "N/C"));
            }

            try
            {
                var snapshot = interfaceManager.OpticSnapshots.GetLaser(deviceNo);
                var status = snapshot.Value ?? throw new InvalidOperationException(snapshot.Detail);
                var power = status.PowerOn ? "ON" : "OFF";
                return Task.FromResult(new ST_OPTIC_LASER_HEAD_PARAMETER(power, status.GateOn ? "OPEN" : "CLOSE", status.ShutterOpen ? "OPEN" : "CLOSE", "OK"));
            }
            catch (Exception exception)when (exception is not OperationCanceledException)
            {
                return Task.FromResult(new ST_OPTIC_LASER_HEAD_PARAMETER("-", "-", "-", "ERROR"));
            }
        }

        private Task<ST_OPTIC_AXIS_HEAD_PARAMETER> BuildAttenuatorHeadParameter(int headNo, IReadOnlyDictionary<int, int> devices, IReadOnlyDictionary<string, string> previewParameters, CancellationToken cancellationToken)
        {
            var target = ReadDoubleAny(previewParameters, 23.50, $"H{headNo:00}_ATTENUATOR_POSITION");
            if (!devices.TryGetValue(headNo, out var deviceNo))
            {
                return Task.FromResult(new ST_OPTIC_AXIS_HEAD_PARAMETER("-", FormatOpticValue(target, "deg"), "N/C"));
            }

            try
            {
                var status = interfaceManager.GetAttenuatorStatus(deviceNo, cancellationToken);
                var state = FormatOpticValueState(FormatAttenuatorState(status), status.CurrentPosition, target);
                return Task.FromResult(new ST_OPTIC_AXIS_HEAD_PARAMETER(FormatOpticValue(status.CurrentPosition, "deg"), FormatOpticValue(target, "deg"), state));
            }
            catch (Exception exception)when (exception is not OperationCanceledException)
            {
                return Task.FromResult(new ST_OPTIC_AXIS_HEAD_PARAMETER("-", FormatOpticValue(target, "deg"), "ERROR"));
            }
        }

        private Task<ST_OPTIC_BET_HEAD_PARAMETER> BuildBetHeadParameter(int headNo, IReadOnlyDictionary<int, int> devices, IReadOnlyDictionary<string, string> previewParameters, CancellationToken cancellationToken)
        {
            var targetMag = ReadDoubleAny(
                previewParameters, 
                1.0, 
                $"H{headNo:00}_BET_MAGNIFICATION", 
                $"H{headNo:00}_BET_TARGET_MAGNIFICATION", 
                "BET_MAGNIFICATION", 
                "BET_TARGET_MAGNIFICATION", 
                "BET_MAG_TARGET", 
                "BET_MAG", 
                "BEAM_EXPANDER_MAGNIFICATION"
            );
            var targetDiv = ReadDoubleAny(previewParameters, 1.0, $"H{headNo:00}_BET_DIVERGENCE", $"H{headNo:00}_BET_TARGET_DIVERGENCE", "BET_DIVERGENCE", "BET_TARGET_DIVERGENCE", "BET_DIV_TARGET", "BET_DIV", "BEAM_EXPANDER_DIVERGENCE");
            if (!devices.TryGetValue(headNo, out var deviceNo))
            {
                return Task.FromResult(new ST_OPTIC_BET_HEAD_PARAMETER("-", FormatBetStep(targetMag), "-", FormatBetStep(targetDiv), "N/C"));
            }

            try
            {
                var snapshot = interfaceManager.OpticSnapshots.GetBet(deviceNo);
                var status = snapshot.Value ?? throw new InvalidOperationException(snapshot.Detail);
                targetMag = ReadDoubleAny(
                    previewParameters, 
                    status.TargetMagnification, 
                    $"H{headNo:00}_BET_MAGNIFICATION", 
                    $"H{headNo:00}_BET_TARGET_MAGNIFICATION", 
                    "BET_MAGNIFICATION", 
                    "BET_TARGET_MAGNIFICATION", 
                    "BET_MAG_TARGET", 
                    "BET_MAG", 
                    "BEAM_EXPANDER_MAGNIFICATION"
                );
                targetDiv = ReadDoubleAny(
                    previewParameters, 
                    status.TargetDivergence, 
                    $"H{headNo:00}_BET_DIVERGENCE", 
                    $"H{headNo:00}_BET_TARGET_DIVERGENCE", 
                    "BET_DIVERGENCE", 
                    "BET_TARGET_DIVERGENCE", 
                    "BET_DIV_TARGET", 
                    "BET_DIV", 
                    "BEAM_EXPANDER_DIVERGENCE"
                );
                var state = FormatOpticGroupState([new ST_OPTIC_PARAMETER_ITEM("MAG", "", "", FormatBetState(status, status.CurrentMagnification, targetMag)), new ST_OPTIC_PARAMETER_ITEM("DIV", "", "", FormatBetState(status, status.CurrentDivergence, targetDiv))]);
                return Task.FromResult(new ST_OPTIC_BET_HEAD_PARAMETER(FormatBetStep(status.MagnificationAxisPosition), FormatBetStep(targetMag), FormatBetStep(status.DivergenceAxisPosition), FormatBetStep(targetDiv), state));
            }
            catch (Exception exception)when (exception is not OperationCanceledException)
            {
                return Task.FromResult(new ST_OPTIC_BET_HEAD_PARAMETER("-", FormatBetStep(targetMag), "-", FormatBetStep(targetDiv), "ERROR"));
            }
        }

        private static ST_OPTIC_PARAMETER_GROUP BuildOpticGroup(string device, IReadOnlyList<ST_OPTIC_PARAMETER_ITEM> items)
        {
            return new ST_OPTIC_PARAMETER_GROUP(device, FormatOpticGroupState(items), items);
        }

        private static IReadOnlyList<int> CreateOpticHeadOrder()
        {
            return[1, 3, 5, 7, 2, 4, 6, 8];
        }

        private static IReadOnlyList<ST_OPTIC_HEAD_PARAMETER> OrderOpticHeadParameters(IReadOnlyList<ST_OPTIC_HEAD_PARAMETER> headParameters)
        {
            if (headParameters.Count == 0)
            {
                return[];
            }

            string SelectHead(global::Drilling.UI.Menu.Menus.ST_OPTIC_HEAD_PARAMETER head)
            {
                return head.Head;
            }

            global::Drilling.UI.Menu.Menus.ST_OPTIC_HEAD_PARAMETER SelectHead2(global::Drilling.UI.Menu.Menus.ST_OPTIC_HEAD_PARAMETER head)
            {
                return head;
            }

            var headMap = headParameters.ToDictionary(SelectHead, SelectHead2, StringComparer.OrdinalIgnoreCase);
            string SelectItem(int headNo)
            {
                return $"H{headNo:00}";
            }

            global::Drilling.UI.Menu.Menus.ST_OPTIC_HEAD_PARAMETER SelectOPTIC_HEAD_PARAMETER(string head)
            {
                return headMap[head];
            }

            var heads = CreateOpticHeadOrder().Select(SelectItem).Where(headMap.ContainsKey).Select(SelectOPTIC_HEAD_PARAMETER).ToArray();
            return heads;
        }

        private static string FormatOpticValueState(string deviceState, double current, double target, double tolerance = 0.001)
        {
            if (IsOpticState(deviceState, "ERROR", "ALARM", "NG", "OFFLINE", "N/C"))
            {
                return deviceState;
            }

            return IsNear(current, target, tolerance) ? "OK" : "WARN";
        }

        private static string FormatBetStep(double value)
        {
            var step = Math.Clamp((int)Math.Round(value), 0, 4500);
            return step.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatOpticGroupState(IReadOnlyList<ST_OPTIC_PARAMETER_ITEM> items)
        {
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_OPTIC_PARAMETER_ITEM item)
            {
                return IsOpticState(item.State, "N/C");
            }

            if (items.Count > 0 && items.All(MatchesItem))
            {
                return "N/C";
            }

            bool MatchesItem2(global::Drilling.UI.Menu.Menus.ST_OPTIC_PARAMETER_ITEM item)
            {
                return IsOpticState(item.State, "ERROR", "ALARM", "NG", "OFFLINE");
            }

            if (items.Any(MatchesItem2))
            {
                return "ALARM";
            }

            bool MatchesItem3(global::Drilling.UI.Menu.Menus.ST_OPTIC_PARAMETER_ITEM item)
            {
                return IsOpticState(item.State, "N/C");
            }

            if (items.Any(MatchesItem3))
            {
                return "N/C";
            }

            bool MatchesItem4(global::Drilling.UI.Menu.Menus.ST_OPTIC_PARAMETER_ITEM item)
            {
                return IsOpticState(item.State, "MOVING", "RUNNING");
            }

            if (items.Any(MatchesItem4))
            {
                return "MOVING";
            }

            bool MatchesItem5(global::Drilling.UI.Menu.Menus.ST_OPTIC_PARAMETER_ITEM item)
            {
                return IsOpticState(item.State, "WARN", "WAIT", "STOP");
            }

            if (items.Any(MatchesItem5))
            {
                return "WARN";
            }

            bool MatchesItem6(global::Drilling.UI.Menu.Menus.ST_OPTIC_PARAMETER_ITEM item)
            {
                return IsOpticState(item.State, "SAFE");
            }

            if (items.Any(MatchesItem6))
            {
                return "SAFE";
            }

            return "OK";
        }

        private static bool IsOpticState(string state, params string[] expectedStates)
        {
            var normalized = state.Trim().ToUpperInvariant();
            bool MatchesItem(string expected)
            {
                return normalized.Equals(expected, StringComparison.OrdinalIgnoreCase);
            }

            return expectedStates.Any(MatchesItem);
        }

        private static string FormatBetState(ST_BET_STATUS status, double current, double target)
        {
            if (status.AlarmOn || !status.CommOk)
            {
                return "ALARM";
            }

            if (status.IsMoving)
            {
                return "MOVING";
            }

            return IsNear(current, target) ? "OK" : "WARN";
        }

        private static string FormatAttenuatorState(ST_ATTENUATOR_STATUS status)
        {
            if (!status.CommOk || status.LastError != EN_CONEX_AGP_ERROR.Ok)
            {
                return "ALARM";
            }

            return string.IsNullOrWhiteSpace(status.CommandState) ? "OK" : status.CommandState.Trim().ToUpperInvariant();
        }

        private static bool IsNear(double current, double target, double tolerance = 0.001)
        {
            return Math.Abs(current - target) <= tolerance;
        }

        private static string FormatOpticValue(double value, string unit)
        {
            return $"{value.ToString("F3", CultureInfo.InvariantCulture)} {unit}";
        }

        private static IReadOnlyList<int> BuildEnabledHeadNumbers(IReadOnlyDictionary<string, string> parameters, int headCount = 8)
        {
            var normalizedHeadCount = Math.Clamp(headCount, 1, 8);
            var heads = new List<int>(normalizedHeadCount);
            for (var headNo = 1; headNo <= normalizedHeadCount; headNo++)
            {
                if (IsHeadEnabled(parameters, headNo))
                {
                    heads.Add(headNo);
                }
            }

            return heads;
        }

        private static bool IsHeadEnabled(IReadOnlyDictionary<string, string> parameters, int headNo)
        {
            return parameters.TryGetValue($"H{headNo:00}_USE", out var value) ? ReadBoolOption(value, true) : true;
        }

        private static bool ReadBoolOption(string value, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return value.Trim().ToUpperInvariant() switch
            {
                "1" or "Y" or "YES" or "TRUE" or "ON" or "USE" => true,
                "0" or "N" or "NO" or "FALSE" or "OFF" or "SKIP" or "UNUSE" => false,
                _ => defaultValue
            };
        }

        private (IReadOnlyList<ST_INSPECTION_STATUS_ITEM> Items, string Summary, string ModeText, string RuleText, Visibility RuleVisibility) BuildInspectionStatus(ST_STATION_PROCESS_STATUS snapshot)
        {
            var sequenceState = FormatReviewSequenceState(reviewManager.SequenceState);
            var plan = reviewManager.CurrentPlan;
            if (plan is null)
            {
                return ([], "No live inspection data", $"STATE : {sequenceState}", "PLAN : -", Visibility.Visible);
            }

            var items = BuildInspectionStatusItems(plan);
            bool MatchesINSPECTION_STATUS_ITEM(global::Drilling.UI.Menu.Menus.ST_INSPECTION_STATUS_ITEM item)
            {
                return item.State is "OK" or "NG";
            }

            var completedCount = items.Count(MatchesINSPECTION_STATUS_ITEM);
            bool MatchesState(global::Drilling.UI.Menu.Menus.ST_INSPECTION_STATUS_ITEM item)
            {
                return item.State == "NG";
            }

            var ngCount = items.Count(MatchesState);
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_INSPECTION_STATUS_ITEM item)
            {
                return item.State == "Current";
            }

            var currentShot = items.FirstOrDefault(MatchesItem)?.Shot ?? "-";
            var summary = $"State {sequenceState} / Total {items.Count:N0} / Done {completedCount:N0} / NG {ngCount:N0} / Current {currentShot}";
            return (items, summary, $"STATE : {sequenceState}", $"RECIPE : {plan.RecipeId}", Visibility.Visible);
        }

        private static string FormatReviewSequenceState(EN_REVIEW_SEQUENCE_STATE state)
        {
            return state.ToString().ToUpperInvariant();
        }

        private static IReadOnlyList<ST_INSPECTION_STATUS_ITEM> BuildInspectionStatusItems(ST_REVIEW_PLAN plan)
        {
            var points = OrderByInspectionSequence(plan.ReviewPoints);
            if (points.Count == 0)
            {
                return[];
            }

            global::Drilling.UI.Menu.Menus.ST_INSPECTION_STATUS_ITEM BuildINSPECTION_STATUS_ITEM(global::Drilling.Common.Review.ST_REVIEW_PLAN_POINT point, int index)
            {
                var state = ToInspectionStateText(point.State);
                var judge = state switch
                {
                    "OK" => "OK",
                    "NG" => "NG",
                    "Current" => "RUN",
                    _ => point.Judge is "OK" or "NG" ? point.Judge : "WAIT"
                };
                return new ST_INSPECTION_STATUS_ITEM(
                    (index + 1).ToString("00", CultureInfo.InvariantCulture), 
                    $"H{point.HeadNo:00}", 
                    $"CELL{point.CellNo:00}", 
                    CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX), 
                    FormatInspectionErrorValue(point.ErrorX, state), 
                    FormatInspectionErrorValue(point.ErrorY, state), 
                    state, 
                    judge
                );
            }

            return points.Select(BuildINSPECTION_STATUS_ITEM).ToArray();
        }

        private static string FormatInspectionErrorValue(double value, string state)
        {
            return state is "OK" or "NG" ? FormatSigned(value) : "-";
        }

        private static string FormatSigned(double value)
        {
            return value.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        }

        private static IReadOnlyList<ST_REVIEW_PLAN_POINT> OrderByInspectionSequence(IEnumerable<ST_REVIEW_PLAN_POINT> points)
        {
            double SelectDesignY(global::Drilling.Common.Review.ST_REVIEW_PLAN_POINT point)
            {
                return point.DesignY;
            }

            double SelectDesignX(global::Drilling.Common.Review.ST_REVIEW_PLAN_POINT point)
            {
                return point.DesignX;
            }

            int SelectCellNo(global::Drilling.Common.Review.ST_REVIEW_PLAN_POINT point)
            {
                return point.CellNo;
            }

            int SelectShotNo(global::Drilling.Common.Review.ST_REVIEW_PLAN_POINT point)
            {
                return point.ShotNo;
            }

            var source = points.OrderBy(SelectDesignY).ThenBy(SelectDesignX).ThenBy(SelectCellNo).ThenBy(SelectShotNo).ToArray();
            var rows = new List<List<ST_REVIEW_PLAN_POINT>>();
            foreach (var point in source)
            {
                if (rows.Count == 0 || Math.Abs(point.DesignY - rows[^1][0].DesignY) > ReviewSequenceRowTolerance)
                {
                    rows.Add([]);
                }

                rows[^1].Add(point);
            }

            var orderedPoints = new List<ST_REVIEW_PLAN_POINT>(source.Length);
            double SelectDesignX2(global::Drilling.Common.Review.ST_REVIEW_PLAN_POINT point)
            {
                return point.DesignX;
            }

            int SelectCellNo2(global::Drilling.Common.Review.ST_REVIEW_PLAN_POINT point)
            {
                return point.CellNo;
            }

            int SelectShotNo2(global::Drilling.Common.Review.ST_REVIEW_PLAN_POINT point)
            {
                return point.ShotNo;
            }

            double SelectDesignX3(global::Drilling.Common.Review.ST_REVIEW_PLAN_POINT point)
            {
                return point.DesignX;
            }

            int SelectCellNo3(global::Drilling.Common.Review.ST_REVIEW_PLAN_POINT point)
            {
                return point.CellNo;
            }

            int SelectShotNo3(global::Drilling.Common.Review.ST_REVIEW_PLAN_POINT point)
            {
                return point.ShotNo;
            }

            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var orderedRow = rowIndex % 2 == 0 ? row.OrderBy(SelectDesignX2).ThenBy(SelectCellNo2).ThenBy(SelectShotNo2) : row.OrderByDescending(SelectDesignX3).ThenBy(SelectCellNo3).ThenBy(SelectShotNo3);
                orderedPoints.AddRange(orderedRow);
            }

            return orderedPoints;
        }

        private static string ToInspectionStateText(EN_REVIEW_POINT_STATE state)
        {
            return state switch
            {
                EN_REVIEW_POINT_STATE.Current => "Current",
                EN_REVIEW_POINT_STATE.Ok => "OK",
                EN_REVIEW_POINT_STATE.Ng => "NG",
                EN_REVIEW_POINT_STATE.Skip => "Skip",
                _ => "Ready"
            };
        }

        private IReadOnlyList<Drilling.Common.InterLock.ST_INTERLOCK_ITEM> LoadLiveInterlockItems(ST_STATION_PROCESS_STATUS snapshot, IReadOnlyDictionary<string, string> previewParameters, CancellationToken cancellationToken)
        {
            return _interlockSnapshot.Read(() => stationManager.GetLiveInterlockItems(previewParameters, cancellationToken), snapshot.InterlockItems);
        }

        private static string FormatProcessResult(ST_STATION_PROCESS_STATUS snapshot)
        {
            if (snapshot.Result is null)
            {
                return "PENDING";
            }

            return string.IsNullOrWhiteSpace(snapshot.Result.ResultCode) ? snapshot.Result.IsSuccess ? "OK" : "NG" : snapshot.Result.ResultCode.Trim().ToUpperInvariant();
        }

        private static string FormatDuration(TimeSpan value)
        {
            return value == TimeSpan.Zero ? "00:00:00" : value.ToString(@"hh\:mm\:ss");
        }

        private static ST_HEAD_PREVIEW BuildHeadPreviewItem(
            int headNo, 
            long assignedShotCount, 
            ST_STATION_PROCESS_STATUS snapshot, 
            IReadOnlySet<int> selectedHeadNos, 
            IReadOnlyList<Drilling.Common.InterLock.ST_INTERLOCK_ITEM> interlockItems, 
            IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM> taskStatusItems, 
            bool isEnabled = true,
            ST_POWER_MEASURE_SNAPSHOT? powerSnapshot = null
        )
        {
            var headPlan = snapshot.ProcessModel?.HeadPlans.FirstOrDefault(head => head.HeadNo == headNo);
            var isCycleActive = snapshot.ProcessStep is EN_PROCESS_STEP.PowerCheck or EN_PROCESS_STEP.Align or EN_PROCESS_STEP.Process or EN_PROCESS_STEP.Inspection;
            // Count allocated drilling coordinates, not the pulse count configured for each coordinate.
            var shotCount = !isEnabled ? 0 : isCycleActive ? headPlan?.Shots.Count ?? 0 : Math.Max(0, assignedShotCount);
            // Equipment-wide interlocks remain in INTERLOCK / SAFETY; only this head's conditions color its card.
            var blockers = interlockItems.Where(item => item.BlocksAutoRun && item.HeadNo == headNo).ToArray();
            var state = "IDLE";
            var detail = "Ready";
            var powerHead = powerSnapshot is { IsRunning: true } ? powerSnapshot.Heads.FirstOrDefault(head => head.Target.HeadNo == headNo) : null;
            if (!isEnabled)
            {
                state = "NOT USE";
                detail = "Head is disabled in settings.";
            }
            else if (powerHead is not null && powerHead.State is not ("OK" or "NG" or "ERROR" or "STOPPED"))
            {
                // ATT position differs from its old Recipe value during calibration. Head Status
                // follows this active transaction; Interlock/Safety continues to display its checks.
                state = "PowerMeasuring";
                detail = $"Target {powerHead.Target.TargetPower:0.####} W / {powerHead.State} / {powerHead.Message}";
            }
            else if (blockers.Length > 0)
            {
                state = "INTERLOCK";
                detail = string.Join(Environment.NewLine, blockers.Select(item => $"{item.Signal}: {item.State} — {item.Detail}"));
            }
            else if (headPlan?.Shots.Count > 0 && snapshot.ProcessStep == EN_PROCESS_STEP.PowerCheck)
            {
                state = "PowerMeasuring";
                detail = "POWER CHECK sequence is active.";
            }
            else if (headPlan?.Shots.Count > 0 && snapshot.ProcessStep == EN_PROCESS_STEP.Process && snapshot.ScriptStatus == EN_SCRIPT_STATUS.Running)
            {
                var task = taskStatusItems.FirstOrDefault(item => item.HeadNoValue == headNo);
                if (task?.State is "RUNNING" or "SIM")
                {
                    state = "RUNNING";
                    detail = task.State == "SIM" ? "Process is running in simulation." : $"{task.ControllerTaskText}: {task.FileText}";
                }
            }

            return new ST_HEAD_PREVIEW(headNo, $"HEAD {headNo:00}", state, isEnabled && selectedHeadNos.Contains(headNo), isEnabled)
            {
                AssignedShotCount = shotCount,
                StatusDetail = detail
            };
        }

        private async Task<IReadOnlyDictionary<string, string>> LoadPreviewParameters(ST_STATION_PROCESS_STATUS snapshot, CancellationToken cancellationToken)
        {
            var recipes = await recipeManager.LoadRecipes(cancellationToken);
            ST_RECIPE_DEFINITION? recipe = null;
            if (recipes.Count > 0)
            {
                var recipeId = selectedRecipeIdProvider();
                if (string.IsNullOrWhiteSpace(recipeId))
                {
                    recipeId = snapshot.ProcessModel?.RecipeId ?? "DRILL_A01";
                }

                bool MatchesItem(global::Drilling.Common.Managers.ST_RECIPE_DEFINITION item)
                {
                    return item.Id.Equals(recipeId, StringComparison.OrdinalIgnoreCase);
                }

                bool MatchesItem2(global::Drilling.Common.Managers.ST_RECIPE_DEFINITION item)
                {
                    return item.Id.Equals("DRILL_A01", StringComparison.OrdinalIgnoreCase);
                }

                recipe = recipes.FirstOrDefault(MatchesItem) ?? recipes.FirstOrDefault(MatchesItem2) ?? recipes[0];
            }

            var source = snapshot.ProcessModel?.Parameters;
            var settings = settingManager.LoadSection(EN_SETTING_TAB.Option, cancellationToken);
            // Managers replace cached records/lists when saved, selected or reloaded.
            // Thousands of shot-offset parameters need merging only when a source changes.
            if (_previewParameterCache is not null && ReferenceEquals(source, _previewSourceParameters)
                && ReferenceEquals(recipe, _previewSourceRecipe) && ReferenceEquals(settings, _previewSourceSettings))
                return _previewParameterCache;
            var parameters = new Dictionary<string, string>(source ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            if (recipe is not null)
                foreach (var parameter in recipe.Parameters.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
                    parameters[parameter.Key] = parameter.Value;
            foreach (var setting in settings)
            {
                if (!string.IsNullOrWhiteSpace(setting.Key))
                {
                    parameters[setting.Key] = setting.Value;
                }
            }

            _previewSourceParameters = source;
            _previewSourceRecipe = recipe;
            _previewSourceSettings = settings;
            return _previewParameterCache = parameters;
        }

        private Task<ST_PREVIEW_HEAD_LAYOUT> LoadPreviewHeadLayout(CancellationToken cancellationToken)
        {
            return Task.FromResult(CHeadAssignmentPreviewBuilder.BuildHeadLayout(settingManager.LoadSection(EN_SETTING_TAB.Option, cancellationToken)));
        }

        private static int ReadIntAny(IReadOnlyDictionary<string, string> parameters, int defaultValue, params string[] keys)
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

        private static double ReadDoubleAny(IReadOnlyDictionary<string, string> parameters, double defaultValue, params string[] keys)
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

        private static double ReadFiniteDoubleAny(IReadOnlyDictionary<string, string> parameters, double defaultValue, params string[] keys)
        {
            var value = ReadDoubleAny(parameters, defaultValue, keys);
            return double.IsFinite(value) ? value : defaultValue;
        }

        public static Brush CreateHeadBrush(int headNo, byte alpha = 255) => CHeadAssignmentPreviewBuilder.CreateHeadBrush(headNo, alpha);
        private static IReadOnlyList<ST_HEAD_PARAMETER> BuildHeadParameters(IReadOnlyDictionary<string, string> parameters, IReadOnlyList<int> enabledHeadNos)
        {
            global::Drilling.UI.Menu.Menus.ST_HEAD_PARAMETER BuildHead(int headNo)
            {
                var prefix = $"H{headNo:00}";
                return new ST_HEAD_PARAMETER(
                    prefix, 
                    ReadDoubleAny(parameters, 1.2, $"{prefix}_LASER_POWER"), 
                    ReadDoubleAny(parameters, 20.0, $"{prefix}_LASER_FREQUENCY"), 
                    ReadIntAny(parameters, 10, $"{prefix}_SHOT_COUNT"), 
                    ReadDoubleAny(parameters, 0.0, $"{prefix}_SCANNER_JUMP_SPEED"), 
                    ReadDoubleAny(parameters, 0.0, $"{prefix}_DOE_Z_POSITION")
                );
            }

            return enabledHeadNos.Select(BuildHead).ToArray();
        }

#endregion
#region ReviewMonitor
        private void OpenPowerMeasureMonitor()
        {
            if (_powerMeasureWindow is { } open) { open.Activate(); return; }
            if (powerMeasure is null) return;
            var panel = new CManualPowerMeasurePanel(powerMeasure,
                () => interfaceManager.GetInterfaceList(EN_EQP_MODULE.PowerMeter), null,
                _ => { }, _ => false, readOnly: true);
            panel.UpdateHeads(Enumerable.Range(1, 8).ToArray());
            var view = new Drilling.UI.Views.CManualPowerMeasureView { DataContext = panel, Margin = new Thickness(12) };
            var window = new Window { Title = "AUTO TPM · Head Power Measurement", Content = view, Width = 1280, Height = 800,
                MinWidth = 950, MinHeight = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.WhiteSmoke };
            if (Application.Current?.MainWindow is { } owner) window.Owner = owner;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            // UI projection only: never issues sensor reads, motion or measurement commands.
            timer.Tick += (_, _) => panel.ApplySnapshot();
            window.Closed += (_, _) => { timer.Stop(); _powerMeasureWindow = null; };
            _powerMeasureWindow = window;
            window.Show(); timer.Start();
        }

        private void OpenReviewMonitor()
        {
            if (_reviewMonitorWindow is { } open)
            {
                open.Activate();
                return;
            }

            var panel = new CAutoReviewMonitorPanel(reviewManager.FineMonitor, () =>
            {
                var values = CMotorStatusPollingService.ReadCachedSnapshot(interfaceManager);
                var mode = CMotorStatusPollingService.ReadStageConnectionState(interfaceManager);
                return (values.FirstOrDefault(row => row.Name == "Stage Y")?.Position ?? "-", values.FirstOrDefault(row => row.Name == "Review X")?.Position ?? "-", mode == EN_COMM_STATE.Simulation ? "SIMULATION" : mode == EN_COMM_STATE.Online ? "ONLINE" : "OFFLINE");
            });
            var window = new CAutoReviewMonitorDialog(panel);
            if (System.Windows.Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, window))
                window.Owner = owner;
            window.Closed += (_, _) => _reviewMonitorWindow = null;
            _reviewMonitorWindow = window;
            window.Show();
        }

#endregion
#region ZeroLineResults
        private void OpenZeroLineResults()
        {
            if (_zeroLineResultsWindow is { } open)
            {
                open.Activate();
                return;
            }

            var panel = new CZeroLineResultsPanel(() => stationManager?.GetStatus().ZeroLine ?? SequenceStatus.ZeroLineSnapshot);
            var window = new CZeroLineResultsDialog(panel);
            if (System.Windows.Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, window))
                window.Owner = owner;
            window.Closed += (_, _) => _zeroLineResultsWindow = null;
            _zeroLineResultsWindow = window;
            window.Show();
        }
#endregion
    }

    /// <summary>UI projection only. Its owner samples the latest shared frame; closing it cannot stop the sequence.</summary>
    public sealed class CAutoReviewMonitorPanel : INotifyPropertyChanged
    {
        private readonly CFineReviewMonitorStore _store;
        private Guid _runId;
        private long _revision = -1;
        private readonly Dictionary<string, ST_FINE_REVIEW_LINE> _seen = new();
        private readonly Dictionary<string, (int Measured, int Ng)> _counts = new();
        private ST_FINE_MONITOR_RESULT? _selectedNg;
        public CAutoReviewMonitorPanel(CFineReviewMonitorStore store, Func<(string StageY, string ReviewX, string Mode)> feedback)
        {
            _store = store;
            Preview.ReadFeedback = feedback;
            Preview.Clear("FINE Review 검사 대기");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public CFineCorrectionPanel Preview { get; } = new();
        public ObservableCollection<ST_FINE_MONITOR_RESULT> NgResults { get; } = [];
        public IReadOnlyList<ST_FINE_MONITOR_RESULT> FinalResults { get; private set; } = [];
        public string Header { get; private set; } = "Review Monitor · FINE";
        public string ProgressText { get; private set; } = "검사 대기";
        public string Status { get; private set; } = "AUTO Review 실행은 아직 미연결입니다. 현재는 Manual FINE의 실제 진행 스냅샷도 표시합니다.";
        public string Tolerance { get; private set; } = "Tolerance X / Y : -";
        public string OffsetStatus { get; private set; } = "검사 결과 대기 · AUTO 보정 저장 연결 전";
        public string ResultFile { get; private set; } = "";
        public Visibility FinalVisibility { get; private set; } = Visibility.Collapsed;
        public int ResultRefreshCount { get; private set; }

        public ST_FINE_MONITOR_RESULT? SelectedNg
        {
            get => _selectedNg;
            set
            {
                _selectedNg = value;
                Preview.SetMonitorFocus(value?.Point);
                Notify(nameof(SelectedNg));
                Notify(nameof(FocusText));
            }
        }

        public string FocusText => SelectedNg is { } row ? $"LINE #{row.LineNo} · {row.Head} / {row.Cell}-{row.Shot} / Beam {row.Beam}" : "NG 행을 선택하면 프리뷰에 위치를 표시합니다.";

        public void RefreshFeedback() => Preview.RefreshFeedback();
        public void RefreshResults()
        {
            var frame = _store.GetSnapshot();
            if (frame is null || frame.Revision == _revision)
                return;
            if (frame.RunId != _runId)
            {
                Preview.AttachMonitor(frame.Request);
                _runId = frame.RunId;
                _seen.Clear();
                _counts.Clear();
                NgResults.Clear();
                SelectedNg = null;
                FinalResults = [];
                FinalVisibility = Visibility.Collapsed;
            }

            _revision = frame.Revision;
            ResultRefreshCount++;
            var plan = frame.Request.Plan;
            var progress = frame.Progress;
            var selectedNg = SelectedNg;
            Preview.ApplyProgress(progress, Preview.SourceVersion);
            foreach (var incoming in progress.Lines)
            {
                if (_seen.GetValueOrDefault(incoming.Key) == incoming)
                    continue;
                _seen[incoming.Key] = incoming;
                var line = Preview.Lines.Single(item => item.Key == incoming.Key);
                _counts[incoming.Key] = (line.MeasuredCount, line.NgCount);
                for (var i = NgResults.Count - 1; i >= 0; i--)
                    if (NgResults[i].LineKey == incoming.Key)
                        NgResults.RemoveAt(i);
                for (var i = 0; i < line.ShotResults.Count; i++)
                    if (line.ShotResults[i].Judge is "NG" or "ERROR")
                        NgResults.Add(new(incoming.Key, line.OrderNo, line.Points[i], line.ShotResults[i], plan.ToleranceX, plan.ToleranceY));
            }

            Preview.SetMonitorNgPoints(NgResults.Select(row => row.Point).ToArray());
            if (selectedNg is { } selected)
                SelectedNg = NgResults.FirstOrDefault(row => row.LineKey == selected.LineKey && row.Point.ShotKey == selected.Point.ShotKey);
            var context = frame.Request.Context;
            Header = $"{context?.Origin ?? "FINE"} · Recipe {context?.RecipeName ?? plan.RecipeId} · Glass {context?.GlassId ?? "-"} · " + $"{(frame.Request.Method == EN_FINE_INSPECTION_METHOD.Flying ? "IOF" : "PtoP")} / {context?.LinesPerCell ?? 1} Line / " + $"즉시 재시도 {(frame.Request.ImmediateRetry ? "ON" : "OFF")} / {frame.Request.CountMismatchRetries}회";
            var measured = _counts.Values.Sum(count => count.Measured);
            var ng = _counts.Values.Sum(count => count.Ng);
            var active = progress.Lines.FirstOrDefault(line => line.State == EN_FINE_REVIEW_LINE_STATE.Running);
            var current = frame.Request.Method == EN_FINE_INSPECTION_METHOD.PtoP ? active?.Points.FirstOrDefault(point => point.State == EN_REVIEW_POINT_STATE.Current) : null;
            ProgressText = $"Shot {measured:N0} / {plan.Points.Count:N0} · OK {measured - ng:N0} · NG {ng:N0} · " + $"LINE {progress.Lines.Count(line => line.State is EN_FINE_REVIEW_LINE_STATE.Complete or EN_FINE_REVIEW_LINE_STATE.Ng)} / {progress.Lines.Count}" + (active is null ? "" : $" · 현재 #{active.Number}") + (current is null ? "" : $" / Cell{current.CellNo:00}-{CReviewShotNameFormatter.ToMatrixName(current.ShotNo, current.PixelCountX)} / Beam {current.BeamNo}");
            Status = progress.Message;
            Tolerance = $"Tolerance : X ±{plan.ToleranceX:0.000} / Y ±{plan.ToleranceY:0.000} mm · Vision Flip : {plan.VisionFlipType}";
            var finished = progress.State is EN_REVIEW_SEQUENCE_STATE.Completed or EN_REVIEW_SEQUENCE_STATE.Stopped or EN_REVIEW_SEQUENCE_STATE.Failed;
            if (finished && (FinalVisibility != Visibility.Visible || FinalResults.Count != plan.Points.Count || !ReferenceEquals(_finalLines, progress.Lines)))
            {
                FinalResults = Preview.Lines.SelectMany(line => line.ShotResults.Select((row, i) => new ST_FINE_MONITOR_RESULT(line.Key, line.OrderNo, line.Points[i], row, plan.ToleranceX, plan.ToleranceY))).ToArray();
                _finalLines = progress.Lines;
                FinalVisibility = Visibility.Visible;
            }

            ResultFile = progress.ResultSaveError.Length > 0 ? "결과 저장 실패: " + progress.ResultSaveError : progress.ResultFilePath;
            OffsetStatus = frame.OffsetFile.Length > 0 ? $"Review Offset 저장됨 · {frame.OffsetFile}" : progress.State == EN_REVIEW_SEQUENCE_STATE.Idle ? "Stage Review START 대기 · 보정 미저장" : ng > 0 ? "NG 있음 · FINE 보정 전체 적용 불가" : finished && measured < plan.Points.Count ? "미검사 있음 · FINE 보정 적용 불가" : finished ? "전체 OK · 보정 미저장" : "검사 중 · 보정 미저장";
            Notify(null);
        }

        private IReadOnlyList<ST_FINE_REVIEW_LINE>? _finalLines;
        private void Notify(string? name) => PropertyChanged?.Invoke(this, new(name));
    }

    public sealed record ST_FINE_MONITOR_RESULT(string LineKey, int LineNo, ST_REVIEW_PLAN_POINT Point, ST_FINE_SHOT_DETAIL Result, double ToleranceX, double ToleranceY)
    {
        public string Head => Result.Head;
        public string Cell => Result.Cell;
        public string Shot => Result.Shot;
        public int Beam => Result.BeamNo;
        public string Received => $"{Result.ReceivedXText} / {Result.ReceivedYText}";
        public string Converted => $"{Result.ConvertedXText} / {Result.ConvertedYText}";
        public string Judge => Result.Judge;
        public string Excess => string.Join(" / ", new[] { Exceeded("X", Result.ConvertedX, ToleranceX), Exceeded("Y", Result.ConvertedY, ToleranceY) }.Where(value => value.Length > 0))is { Length: > 0 } text ? text : Judge is "NG" or "ERROR" ? "Vision NG / 수치 초과 없음" : "-";

        private static string Exceeded(string axis, double? value, double limit) => value is { } number && Math.Abs(number) > limit ? $"{axis} +{Math.Abs(number) - limit:0.000} mm" : "";
    }

    /// <summary>Presentation of the existing interlock snapshot; performs no device reads or new checks.</summary>
    public static class CMainInterlockGroups
    {
        public static bool IsOk(InterlockItem item) => item.Level == EN_INTERLOCK_LEVEL.Ok && !item.BlocksAutoRun;
        public static IReadOnlyList<ST_INTERLOCK_GROUP> Build(IReadOnlyList<InterlockItem> items)
        {
            var groups = new Dictionary<string, List<ST_INTERLOCK_DETAIL>>(StringComparer.OrdinalIgnoreCase);
            void Add(string name, ST_INTERLOCK_DETAIL detail)
            {
                if (!groups.TryGetValue(name, out var rows))
                    groups[name] = rows = [];
                rows.Add(detail);
            }

            foreach (var item in items)
            {
                var detail = new ST_INTERLOCK_DETAIL(item.Signal, IsOk(item), item.State, item.Detail);
                var category = Category(item);
                if (category is not null)
                    Add(category, detail);
                // Device rows summarize that device across heads. HEAD rows summarize all checks
                // belonging to one head, using the same checks rather than evaluating them again.
                if (item.HeadNo is> 0)
                    Add($"HEAD {item.HeadNo}", detail);
            }

            return groups.OrderBy(group => Order(group.Key)).ThenBy(group => group.Key, StringComparer.Ordinal).Select(group => new ST_INTERLOCK_GROUP(group.Key, group.Value.ToArray())).ToArray();
        }

        private static string? Category(InterlockItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.Group))
                return item.Group.Trim().ToUpperInvariant();
            var name = item.Signal.Trim().ToUpperInvariant();
            if (name.Contains("TILT"))
                return "TILT";
            if (name.Contains("MELSEC"))
                return "MELSEC";
            if (name.Contains("BET") || name.Contains("BEAM EXPANDER"))
                return "BET";
            if (name.Contains("ATT"))
                return "ATT";
            if (name.Contains("LASER"))
                return "LASER";
            if (name.Contains("VISION"))
                return "VISION";
            if (item.HeadNo is> 0)
                return null;
            if (name.Contains("SCANNER") || name.StartsWith("HEAD"))
                return "HEAD";
            if (name == "MOTION")
                return "MOTION";
            if (name is "DOOR" or "EMS" or "KEY SWITCH" or "LEAK SENSOR" or "SMOKE TEMP" or "PM LOCK" or "PANEL IN POSITION" or "VACUUM CHECK")
                return "SAFETY";
            return "SYSTEM";
        }

        private static int Order(string name) => name switch
        {
            "TILT" => 0,
            "MELSEC" => 1,
            "BET" => 2,
            "ATT" => 3,
            "LASER" => 20,
            "VISION" => 21,
            "MOTION" => 22,
            "SAFETY" => 23,
            _ when name.StartsWith("HEAD", StringComparison.Ordinal) => int.TryParse(name.AsSpan(4).Trim(), out var head) ? 4 + head : 4,
            _ => 24
        };
    }

    public sealed record ST_INTERLOCK_GROUP(string Item, IReadOnlyList<ST_INTERLOCK_DETAIL> Details)
    {
        public bool IsOk => Details.Count > 0 && Details.All(detail => detail.IsOk);
        public string State => IsOk ? "OK" : "NOT OK";
        public Brush StateBrush => CStatusBrush.ForDisplayState(IsOk ? "OK" : "NG");
    }

    public sealed record ST_INTERLOCK_DETAIL(string Item, bool IsOk, string Condition, string Detail)
    {
        public string State => IsOk ? "OK" : "NOT OK";
        public Brush StateBrush => CStatusBrush.ForDisplayState(IsOk ? "OK" : "NG");
        public string Description => string.IsNullOrWhiteSpace(Detail) ? Condition : $"{Condition}\n{Detail}";
    }

    /// <summary>Keeps the display independent of the process snapshot cleared by RESET/PrepareProcess.</summary>
    public sealed class CMainInterlockSnapshot
    {
        private IReadOnlyList<InterlockItem> _lastSuccessful = [];
        public IReadOnlyList<InterlockItem> Read(Func<IReadOnlyList<InterlockItem>> read, IReadOnlyList<InterlockItem> processFallback)
        {
            try
            {
                var current = read().ToArray();
                _lastSuccessful = current;
                return current;
            }
            catch (Exception exception)when (exception is not OperationCanceledException)
            {
                var previous = _lastSuccessful.Count > 0 ? _lastSuccessful : processFallback;
                if (previous.Count == 0)
                    return[new("Interlock Refresh", EN_INTERLOCK_LEVEL.Error, "READ FAIL", exception.Message)
                    {
                        BlocksAutoRun = true
                    }

                    ];
                // Retain every known check, but never present a previous OK as a current reading.
                return previous.Select(item => item with { Level = EN_INTERLOCK_LEVEL.Error, State = "READ FAIL", Detail = $"현재 상태를 확인할 수 없습니다. {exception.Message}", BlocksAutoRun = true }).ToArray();
            }
        }
    }

    /// <summary>Read-only presentation of recipe limits and the existing shared MELSEC snapshot.</summary>
    public sealed class CMainProtectWindowPanel : INotifyPropertyChanged
    {
        public const string CurrentCountId = "PROCESS_SEQUENCE_PTW_PROCESS_COUNT";
        public const string RequestId = "PROCESS_SEQUENCE_TPD_PTW_EXCHANGE_FLAG";
        public const string CompleteId = "PTW_EXCHANGE_SEQ_COMPLETE";
        public const string TrayCountId = "PTW_TRAY_COUNT";
        private static readonly Brush Blue = Frozen(0x48, 0x83, 0xB1);
        private static readonly Brush Gray = Frozen(0x70, 0x7D, 0x89);
        private static readonly Brush Green = Frozen(0x28, 0x8D, 0x54);
        private static readonly Brush Red = Frozen(0xDC, 0x2A, 0x2A);
        private static readonly Brush Ochre = Frozen(0xBD, 0x83, 0x00);
        private bool _visible;
        public Visibility PanelVisibility => _visible ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
        private CProtectWindowDisplay _recipeCount = new("—", "현재 레시피의 MAX_PROCESS_COUNT", Blue);
        private CProtectWindowDisplay _currentCount = Missing("현재 레시피의 가공 매수 읽기 대기");
        private CProtectWindowDisplay _trayCount = Missing("Stage Tray Count 수신 대기 · 교체완료 시 현재 매수로 인계");
        private CProtectWindowDisplay _request = Missing("교체요청 신호상태 수신 대기");
        private CProtectWindowDisplay _complete = Missing("교체완료 신호상태 수신 대기");
        private CProtectWindowDisplay _connection = new("OFFLINE", "MELSEC 연결 대기", Red);
        public CProtectWindowDisplay RecipeCount { get => _recipeCount; private set => Set(ref _recipeCount, value); }
        public CProtectWindowDisplay CurrentCount { get => _currentCount; private set => Set(ref _currentCount, value); }
        public CProtectWindowDisplay TrayCount { get => _trayCount; private set => Set(ref _trayCount, value); }
        public CProtectWindowDisplay Request { get => _request; private set => Set(ref _request, value); }
        public CProtectWindowDisplay Complete { get => _complete; private set => Set(ref _complete, value); }
        public CProtectWindowDisplay Connection { get => _connection; private set => Set(ref _connection, value); }

        public void SetVisible(bool visible)
        {
            if (_visible == visible)
                return;
            _visible = visible;
            PropertyChanged?.Invoke(this, new(nameof(PanelVisibility)));
        }

        public void UpdateRecipe(IReadOnlyDictionary<string, string> parameters)
        {
            var text = parameters.GetValueOrDefault(CProtectWindowRecipeData.MaximumCountKey);
            RecipeCount = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var count) && double.IsFinite(count) && count >= 0 && count <= int.MaxValue && count == Math.Truncate(count) ? new(count.ToString("N0", CultureInfo.InvariantCulture) + " 장", "현재 레시피의 MAX_PROCESS_COUNT", Blue) : new("미설정", "레시피 MAX_PROCESS_COUNT를 확인해 주세요.", Red);
            try
            {
                var current = CProtectWindowRecipeData.ParseCount(parameters.GetValueOrDefault(CProtectWindowRecipeData.CurrentCountKey));
                CurrentCount = new(current.ToString("N0", CultureInfo.InvariantCulture) + " 장", "현재 레시피에 저장된 가공 매수", Blue);
            }
            catch (InvalidDataException)
            {
                CurrentCount = new("미설정", "레시피 CURRENT_PROCESS_COUNT를 확인해 주세요.", Red);
            }
        }

        public void RefreshSignals(IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE> values, IReadOnlyList<ST_MELSEC_MAP_DATA> map, IReadOnlyDictionary<int, EN_COMM_STATE> connections)
        {
            var deviceNo = map.FirstOrDefault(row => row.Id == RequestId)?.DeviceNo ?? map.FirstOrDefault(row => row.Id == CompleteId)?.DeviceNo ?? 0;
            var state = connections.GetValueOrDefault(deviceNo, EN_COMM_STATE.Offline);
            Connection = state == EN_COMM_STATE.Simulation ? new("SIMULATION", "MELSEC Simulation 공통 스냅샷", Ochre) : state == EN_COMM_STATE.Online ? new("ONLINE", "MELSEC 공통 스냅샷", Green) : new("OFFLINE", "MELSEC 미연결", Red);
            TrayCount = Count(TrayCountId);
            Request = Flag(RequestId, "요청 중", "요청 없음", blinkWhenOn: true);
            Complete = Flag(CompleteId, "완료 신호 ON", "미완료");
            CProtectWindowDisplay Count(string id)
            {
                var(number, error, detail) = Read(id);
                if (error is not null)
                    return error;
                return number >= 0 && Math.Abs(number - Math.Round(number)) < 1e-8 && number <= int.MaxValue ? new(Math.Round(number).ToString("N0", CultureInfo.InvariantCulture), detail, Blue) : new("ERR", detail + " · 매수는 0 이상의 정수여야 합니다.", Red);
            }

            CProtectWindowDisplay Flag(string id, string on, string off, bool blinkWhenOn = false)
            {
                var(number, error, detail) = Read(id);
                return error ?? (number switch
                {
                    1 => new(on, detail, Green, true, blinkWhenOn),
                    0 => new(off, detail, Gray, false),
                    _ => new("UNKNOWN", detail + " · 0/1 이외의 신호값", Red)});
            }

            (double Value, CProtectWindowDisplay? Error, string Detail) Read(string id)
            {
                var row = map.FirstOrDefault(item => item.Id == id);
                if (row is null || !row.Use || row.Address == "UNASSIGNED")
                    return (0, Missing("MELSEC 주소 사용 미연결: " + id), "");
                var detail = row.Address + " · " + row.Description;
                var connection = connections.GetValueOrDefault(row.DeviceNo, EN_COMM_STATE.Offline);
                if (connection is not (EN_COMM_STATE.Online or EN_COMM_STATE.Simulation))
                    return (0, new("OFFLINE", detail, Red), detail);
                if (!values.TryGetValue(id, out var sample))
                    return (0, new("대기", detail, Gray), detail);
                if (!sample.IsValid || sample.Value is null)
                    return (0, new(sample.State switch
                    {
                        EN_MELSEC_CACHE_STATE.Stale => "STALE",
                        EN_MELSEC_CACHE_STATE.Offline => "OFFLINE",
                        EN_MELSEC_CACHE_STATE.Error => "ERR",
                        _ => "대기"
                    }, detail + " · " + sample.ErrorMessage, sample.State == EN_MELSEC_CACHE_STATE.Pending ? Gray : Red), detail);
                // Values have already been scaled centrally. Never apply READ_SCALE a second time here.
                var number = CMelsecCachePresentation.NumericValue(sample.Value);
                return double.IsFinite(number) ? (number, null, detail) : (0, new("ERR", detail + " · 숫자 수신값 오류", Red), detail);
            }
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new(property));
        }

        private static CProtectWindowDisplay Missing(string detail) => new("미연결", detail, Gray);
        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    public sealed record CProtectWindowDisplay(string Text, string Detail, Brush Color, bool? IsActive = null, bool IsBlinking = false);
    /// <summary>Setting-backed Run Options. The coordinator captures immutable execution options before Prc ABLE.</summary>
    public sealed class CMainRunOptionPanel : INotifyPropertyChanged
    {
        private ST_AUTO_REVIEW_OPTIONS _reviewOptions = new();
        private readonly Func<bool?>? _readReviewAble;
        private readonly CSettingManager? _settings;
        private readonly Action<string>? _reportError;
        private bool _reviewAble;
        public bool CanEditReview => !_reviewAble;
        public string ReviewInfoText => _reviewAble ? "Review ABLE ON · 리뷰 옵션 변경 불가" : "Beam 11 · 옵션은 Prc Review ABLE 전에 확정";

        public ST_AUTO_REVIEW_OPTIONS CaptureReviewOptions() => _settings is null ? Volatile.Read(ref _reviewOptions) : CRunOptionSettings.Read(_settings).ReviewOptions;
        public event PropertyChangedEventHandler? PropertyChanged;
        public bool ZeroLineEnabled { get; private set; }
        public bool FollowUpEnabled { get; private set; }
        public int ReviewLineCount { get; private set; } = 1;
        public string ReviewMethod { get; private set; } = "IOF";
        public int ReviewCountRetries { get; private set; } = 1;
        public bool ReviewImmediateRetry { get; private set; }
        public IReadOnlyList<CMainRunOptionChoice> ReviewImmediateRetryChoices => SwitchChoices(ReviewImmediateRetry);
        public IReadOnlyList<CMainRunOptionChoice> ReviewRetryChoices => new[]
        {
            1,
            2,
            3
        }.Select(count => new CMainRunOptionChoice(count, $"{count}회", count == ReviewCountRetries, Brushes.SteelBlue)).ToArray();
        public int CurrentGroup { get; private set; } = 1;
        public int TotalGroups { get; private set; } = 4;

        private bool _visible;
        public Visibility PanelVisibility => _visible ? Visibility.Visible : Visibility.Collapsed;
        public string GroupText => $"{CurrentGroup} / {TotalGroups}";
        public IReadOnlyList<CMainRunOptionChoice> ZeroLineChoices => SwitchChoices(ZeroLineEnabled);
        public IReadOnlyList<CMainRunOptionChoice> FollowUpChoices => SwitchChoices(FollowUpEnabled);
        public IReadOnlyList<CMainRunOptionChoice> ReviewLineChoices => new[]
        {
            1,
            3,
            5,
            7
        }.Select(count => new CMainRunOptionChoice(count, $"{count}줄", count == ReviewLineCount, Brushes.SteelBlue)).ToArray();
        public IReadOnlyList<CMainRunOptionChoice> ReviewMethodChoices => [new(0, "PtoP", ReviewMethod == "PtoP", Brushes.SteelBlue), new(1, "IOF", ReviewMethod == "IOF", Brushes.SteelBlue)];
        public CButtonCommand SelectZeroLineCommand { get; }
        public CButtonCommand SelectFollowUpCommand { get; }
        public CButtonCommand SelectReviewLineCommand { get; }
        public CButtonCommand SelectReviewMethodCommand { get; }
        public CButtonCommand SelectReviewRetryCommand { get; }
        public CButtonCommand SelectReviewImmediateRetryCommand { get; }
        public CButtonCommand InitGroupCommand { get; }

        public CMainRunOptionPanel(Func<bool?>? readReviewAble = null, CSettingManager? settings = null, Action<string>? reportError = null, bool initiallyVisible = false)
        {
            _readReviewAble = readReviewAble;
            _settings = settings;
            _reportError = reportError;
            _visible = initiallyVisible;
            SelectZeroLineCommand = new(value =>
            {
                if (!TryNumber(value, out var selected) || selected is not (0 or 1))
                    return;
                Commit(CRunOptionSettings.ZeroDefence, selected, () => ZeroLineEnabled = selected == 1);
            });
            SelectFollowUpCommand = new(value =>
            {
                if (!TryNumber(value, out var selected) || selected is not (0 or 1))
                    return;
                Commit(CRunOptionSettings.FollowUp, selected, () => FollowUpEnabled = selected == 1);
            });
            SelectReviewLineCommand = new(value =>
            {
                if (!CanChange() || !TryNumber(value, out var selected) || selected is not (1 or 3 or 5 or 7))
                    return;
                Commit(CRunOptionSettings.ReviewLine, CRunOptionSettings.EncodeLine(selected), () => ReviewLineCount = selected);
            }, _ => CanChange());
            SelectReviewMethodCommand = new(value =>
            {
                if (!CanChange() || !TryNumber(value, out var selected) || selected is not (0 or 1))
                    return;
                Commit(CRunOptionSettings.ReviewMethod, selected, () => ReviewMethod = selected == 0 ? "PtoP" : "IOF");
            }, _ => CanChange());
            InitGroupCommand = new(_ => Commit(CRunOptionSettings.CurrentGroup, 1, () => CurrentGroup = 1));
            SelectReviewRetryCommand = new(value =>
            {
                if (!CanChange() || !TryNumber(value, out var selected) || selected is not (1 or 2 or 3))
                    return;
                Commit(CRunOptionSettings.RetryCount, selected, () => ReviewCountRetries = selected);
            }, _ => CanChange());
            SelectReviewImmediateRetryCommand = new(value =>
            {
                if (!CanChange() || !TryNumber(value, out var selected) || selected is not (0 or 1))
                    return;
                Commit(CRunOptionSettings.ImmediateRetry, selected, () => ReviewImmediateRetry = selected == 1);
            }, _ => CanChange());
            ReloadFromSettings();
        }

        // Reads the SettingManager memory cache, never polls CSV or device I/O.
        public void ReloadFromSettings()
        {
            if (_settings is null)
                return;
            var value = CRunOptionSettings.Read(_settings);
            if ((ZeroLineEnabled, CurrentGroup, FollowUpEnabled, ReviewMethod, ReviewLineCount, ReviewImmediateRetry, ReviewCountRetries) == (value.ZeroDefence, value.CurrentGroup, value.FollowUp, value.ReviewMethod == 0 ? "PtoP" : "IOF", value.ReviewLineCount, value.ImmediateRetry, value.RetryCount))
                return;
            ZeroLineEnabled = value.ZeroDefence;
            CurrentGroup = value.CurrentGroup;
            FollowUpEnabled = value.FollowUp;
            ReviewMethod = value.ReviewMethod == 0 ? "PtoP" : "IOF";
            ReviewLineCount = value.ReviewLineCount;
            ReviewImmediateRetry = value.ImmediateRetry;
            ReviewCountRetries = value.RetryCount;
            Refresh();
        }

        private void Commit(string key, int value, Action apply)
        {
            try
            {
                if (_settings is not null && _settings.GetValue(EN_SETTING_TAB.Option, key) != value.ToString(CultureInfo.InvariantCulture))
                    _settings.SetValue(EN_SETTING_TAB.Option, key, value.ToString(CultureInfo.InvariantCulture));
                apply();
                Refresh();
            }
            catch (Exception error)when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _reportError?.Invoke($"Run Option 저장 실패: {error.Message}");
            }
        }

        // Called by Main's existing shared-snapshot refresh. Unknown data does not clear a known ON.
        public void UpdateReviewAble(bool? on)
        {
            if (on is null || _reviewAble == on.Value)
                return;
            _reviewAble = on.Value;
            PropertyChanged?.Invoke(this, new(null));
            foreach (var command in new[]
            {
                SelectReviewLineCommand,
                SelectReviewMethodCommand,
                SelectReviewRetryCommand,
                SelectReviewImmediateRetryCommand
            }

            )
                command.NotifyCanExecuteChanged();
        }

        private bool CanChange()
        {
            // Recheck memory at execution too, so a click cannot slip through before the next Main refresh.
            if (_readReviewAble is not null)
                UpdateReviewAble(_readReviewAble());
            return CanEditReview;
        }

        // Uses the recipe snapshot already loaded by Main; does not read a file or advance a cycle.
        public void UpdatePointCount(int pointCount)
        {
            var total = pointCount == 4 ? 2 : 4;
            var group = Math.Clamp(CurrentGroup, 1, total);
            if (total == TotalGroups && group == CurrentGroup)
                return;
            TotalGroups = total;
            if (group != CurrentGroup)
                Commit(CRunOptionSettings.CurrentGroup, group, () => CurrentGroup = group);
            else
                Refresh();
        }

        public void SetVisible(bool visible)
        {
            ReloadFromSettings();
            if (_visible == visible)
                return;
            _visible = visible;
            Refresh();
        }

        private static bool TryNumber(object? value, out int number) => int.TryParse(value?.ToString(), out number);
        private static IReadOnlyList<CMainRunOptionChoice> SwitchChoices(bool enabled) => [new(1, "ON", enabled, Brushes.SeaGreen), new(0, "OFF", !enabled, Brushes.Firebrick)];
        private void Refresh()
        {
            Volatile.Write(ref _reviewOptions, new(ReviewLineCount, ReviewMethod == "PtoP" ? EN_FINE_INSPECTION_METHOD.PtoP : EN_FINE_INSPECTION_METHOD.Flying, ReviewCountRetries, ReviewImmediateRetry));
            PropertyChanged?.Invoke(this, new(null));
        }
    }

    public sealed record CMainRunOptionChoice(int Value, string Label, bool IsSelected, Brush SelectedBrush)
    {
        public Brush Background => IsSelected ? SelectedBrush : Brushes.AliceBlue;
        public Brush Foreground => IsSelected ? Brushes.White : Brushes.Black;
    }

    /// <summary>One ordered signal/action flow, using shared communication and execution snapshots.</summary>
    public sealed class CMainSequenceStatus
    {
        private string _selectedKey = "DO_READY";
        private IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE> _values = new Dictionary<string, ST_MELSEC_CACHED_VALUE>();
        private IReadOnlyDictionary<string, ST_MELSEC_MAP_DATA> _map = new Dictionary<string, ST_MELSEC_MAP_DATA>();
        private IReadOnlyDictionary<int, EN_COMM_STATE> _connections = new Dictionary<int, EN_COMM_STATE>();
        private IReadOnlyDictionary<string, ST_SEQUENCE_ACTION_STATUS> _actions = new Dictionary<string, ST_SEQUENCE_ACTION_STATUS>();
        public CMainSequenceStatus()
        {
            SelectScenarioCommand = new CButtonCommand(parameter => Select(parameter?.ToString()));
            Tabs = Scenarios.Select(scenario => new CMainSequenceTab(scenario.Key, scenario.Name)).ToArray();
            Rebuild();
        }

        public event EventHandler? CardsChanged;
        public event EventHandler? FlowRowsChanged;
        public CButtonCommand SelectScenarioCommand { get; }
        public IReadOnlyList<CMainSequenceTab> Tabs { get; }
        public IReadOnlyList<CMainSequenceCard> Cards { get; private set; } = [];
        public IReadOnlyList<CMainSequenceRow> FlowRows { get; private set; } = [];
        public Action? OpenReviewMonitor { get; set; }
        public Action? OpenPowerMeasureMonitor { get; set; }
        public Action? OpenZeroLineResults { get; set; }
        public ST_ZERO_LINE_CYCLE_STATUS? ZeroLineSnapshot => _zeroCycle;

        private string _reviewProgress = "클릭: Review Monitor";
        private ST_ZERO_LINE_PREWAIT_STATUS? _zeroPrewait;
        private ST_ZERO_LINE_CYCLE_STATUS? _zeroCycle;
        public void RefreshZeroLine(ST_ZERO_LINE_PREWAIT_STATUS? prewait, ST_ZERO_LINE_CYCLE_STATUS? cycle)
        {
            _zeroPrewait = prewait;
            _zeroCycle = cycle;
            RefreshZeroProgress();
        }

        private void RefreshZeroProgress()
        {
            foreach (var card in Cards)
            {
                if (card.Id == "MOVE_TARGETS")
                    card.SetProgress(_zeroPrewait?.Detail ?? "");
                if (card.Id == CSequenceActionIds.ProcessZeroPrepare)
                    card.SetProgress(_zeroCycle is null ? "0선방어 OFF" : _zeroCycle.State is "WAIT" or "PREPARING" or "ERROR" or "STOPPED" ? _zeroCycle.Detail : $"Group {_zeroCycle.Group} · {_zeroCycle.Results?.Count ?? 0}개 준비 완료");
                if (card.Id == CSequenceActionIds.ProcessZeroMeasure)
                    card.SetProgress(_zeroCycle is null ? "0선방어 OFF" : $"Group {_zeroCycle.Group} · {_zeroCycle.Results?.Count(result => result.State is "OK" or "NG" or "NCK" or "ERROR") ?? 0}/{_zeroCycle.Results?.Count ?? 0} · NG {_zeroCycle.Results?.Count(result => result.IsNg) ?? 0}");
                if (card.Id == CSequenceActionIds.ProcessZeroResults)
                    card.SetProgress((_zeroCycle is null ? "0선방어 OFF" : string.IsNullOrEmpty(_zeroCycle.ResultFile) ? "결과 저장·최종 Flag 게시 대기" : "결과 파일 저장 완료")
                        + (_zeroCycle?.FollowUpRequested == true ? " · 후속 중앙 1줄 IOF 요청·완료 대기 (보정 없음)" : _zeroCycle?.FollowUpCompleted == true ? " · 후속 중앙 1줄 IOF 완료 (보정 미적용)" : "")
                        + " · 클릭: 0선 원본 결과");
            }
        }

        public void RefreshReviewMonitor(ST_FINE_REVIEW_MONITOR? frame)
        {
            _reviewProgress = frame is null ? "클릭: Review Monitor" : frame.Request.Context?.Origin != "AUTO" ? "최근 MANUAL FINE · 클릭: Monitor" : $"{frame.Progress.Lines.Sum(line => line.Points.Count(point => point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng)):N0} / {frame.Request.Plan.Points.Count:N0} Shots · " + $"NG {frame.Progress.Lines.Sum(line => line.Points.Count(point => point.State == EN_REVIEW_POINT_STATE.Ng))}";
            if (frame?.Request.Context?.ZeroLineFollowUp == true)
                _reviewProgress = "0선 후속 · 중앙 1줄 IOF · 보정 없음 · " + _reviewProgress;
            foreach (var card in Cards.Where(card => card.Id == "REVIEW_MEASURE"))
                card.SetProgress(_reviewProgress);
        }

        public void Refresh(
            IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE> values, 
            IReadOnlyList<ST_MELSEC_MAP_DATA> map, 
            IReadOnlyDictionary<int, EN_COMM_STATE> connections, 
            IReadOnlyDictionary<string, ST_SEQUENCE_ACTION_STATUS>? actions = null
        )
        {
            _values = values;
            _map = map.ToDictionary(row => row.Id, StringComparer.OrdinalIgnoreCase);
            _connections = connections;
            _actions = actions ?? new Dictionary<string, ST_SEQUENCE_ACTION_STATUS>();
            RefreshCards();
            RefreshTabs();
        }

        private void Select(string? key)
        {
            if (key == _selectedKey || !Scenarios.Any(scenario => scenario.Key == key))
                return;
            _selectedKey = key!;
            Rebuild();
        }

        private void Rebuild()
        {
            var scenario = Scenarios.Single(item => item.Key == _selectedKey);
            Cards = scenario.Steps.Select((step, index) => new CMainSequenceCard(step.Id, step.Title, step.Owner, step.IsSignal, index + 1,
                compact: scenario.Steps.Length > 13, runningMask: step.RunningMask, equalsOne: step.EqualsOne)).ToArray();
            // Pair only adjacent steps from opposite PCs. Same-PC work stays on later rows,
            // preserving one timeline rather than independently stacking the two owners.
            var rows = new List<CMainSequenceRow>();
            for (var index = 0; index < Cards.Count; index++)
            {
                var first = Cards[index];
                CMainSequenceCard? stage = first.IsStage ? first : null;
                CMainSequenceCard? process = first.IsStage ? null : first;
                if (index + 1 < Cards.Count && CanShareFlowRow(first, Cards[index + 1]))
                {
                    var next = Cards[++index];
                    if (next.IsStage) stage = next;
                    else process = next;
                }
                rows.Add(new(stage, process, first.IsStage));
            }
            FlowRows = rows;
            foreach (var card in Cards.Where(card => card.Id == "REVIEW_MEASURE"))
                card.SetProgress(_reviewProgress);
            RefreshCards();
            RefreshZeroProgress();
            RefreshTabs();
            CardsChanged?.Invoke(this, EventArgs.Empty);
            FlowRowsChanged?.Invoke(this, EventArgs.Empty);
        }

        private static bool CanShareFlowRow(CMainSequenceCard first, CMainSequenceCard next)
        {
            if (first.IsStage == next.IsStage)
                return false;
            if (!first.IsSignal || !next.IsSignal)
                return true;
            var firstPhase = first.Id[(first.Id.LastIndexOf('_') + 1)..];
            var nextPhase = next.Id[(next.Id.LastIndexOf('_') + 1)..];
            // An ABLE response must not steal the row of the following START exchange.
            return firstPhase is not ("ABLE" or "START" or "COMPLETE")
                || nextPhase is not ("ABLE" or "START" or "COMPLETE") || firstPhase == nextPhase;
        }

        private void RefreshTabs()
        {
            // Use shared snapshots, including scenarios whose cards are not displayed.
            // Keep tab objects stable so unchanged snapshots do not restart their animations.
            for (var index = 0; index < Scenarios.Length; index++)
            {
                var scenario = Scenarios[index];
                var running = scenario.Steps.Any(IsRunning);
                Tabs[index].Refresh(scenario.Key == _selectedKey, running);
            }
        }

        private bool IsRunning(Step step)
        {
            if (!step.IsSignal)
                return _actions.TryGetValue(step.Id, out var action) && action.State == EN_SEQUENCE_ACTION_STATE.Running;
            if (step.RunningMask == 0)
                return false;
            _map.TryGetValue(step.Id, out var row);
            _values.TryGetValue(step.Id, out var value);
            var connected = row is not null && _connections.TryGetValue(row.DeviceNo, out var connection) && connection is EN_COMM_STATE.Online or EN_COMM_STATE.Simulation;
            return CMainSequenceCard.ReadSignalState(row, value, connected, step.RunningMask, step.EqualsOne) == "RUNNING";
        }

        private void RefreshCards()
        {
            foreach (var card in Cards)
            {
                if (card.IsSignal)
                {
                    _map.TryGetValue(card.Id, out var row);
                    _values.TryGetValue(card.Id, out var value);
                    var connected = row is not null && _connections.TryGetValue(row.DeviceNo, out var connection) && connection is EN_COMM_STATE.Online or EN_COMM_STATE.Simulation;
                    card.RefreshSignal(row, value, connected);
                }
                else
                {
                    _actions.TryGetValue(card.Id, out var action);
                    card.RefreshAction(action);
                }
            }
        }

        private sealed record Step(string Id, string Title, string Owner, bool IsSignal, int RunningMask = 0, bool EqualsOne = false);
        private sealed record Scenario(string Key, string Name, Step[] Steps);
        private static Step S(string owner, string scenario, string signal) => new($"HANDSHAKE_{owner}_{scenario}_{signal}",
            $"{ScenarioName(scenario)} {signal.Replace('_', ' ')}",
            owner == "STG" ? "STAGE → PROCESS" : "PROCESS → STAGE", true);
        private static Step A(string id, string title) => new(id, title, "PROCESS ACTION", false);
        private static string ScenarioName(string scenario) => scenario switch
        {
            "DOREADY" => "DoReady",
            "PROCESS" => "Process",
            "REVIEW" => "Review",
            "TOP_POWERMETER" => "TPM",
            _ => scenario
        };
        // Cards display the scenario; selecting/viewing them never writes a signal or starts an action.
        private static readonly Scenario[] Scenarios = [
            new("DO_READY", "DoReady", [
            S("STG", "DOREADY", "ABLE"), 
            S("PRC", "DOREADY", "ABLE"), 
            S("STG", "DOREADY", "START"), 
            S("PRC", "DOREADY", "START"), 
            A(CSequenceActionIds.PrepareData, "좌표 및 MOF Data 준비"), 
            A(CSequenceActionIds.BuildUploadScript, "가공 스크립트 업로드"), 
            S("PRC", "DOREADY", "COMPLETE"), 
            S("STG", "DOREADY", "COMPLETE"), 
            A(CSequenceActionIds.DoReadySignalsClear, "DoReady 신호 초기화")
        ]), 
            new("PROCESS", "Process", [
            S("STG", "PROCESS", "ABLE"), 
            A(CSequenceActionIds.ProcessReadyPosition, "스테이지 준비위치 확인"), 
            A(CSequenceActionIds.ProcessZeroPrepare, "0선방어 준비"), 
            A(CSequenceActionIds.ProcessMofZero, "스캐너 MOF 축 피드백카운트 초기화"), 
            A(CSequenceActionIds.ProcessTaskRun, "가공 스크립트 실행"), 
            S("PRC", "PROCESS", "ABLE"), 
            S("STG", "PROCESS", "START"), 
            S("PRC", "PROCESS", "START"), 
            A("PROCESS_MACHINING_INSPECTION", "MOF 가공"), 
            A(CSequenceActionIds.ProcessZeroMeasure, "0선방어 검사"), 
            new(CStageProcessDataPublisher.MachiningCompleteId, "Actual machining complete", "PROCESS → STAGE", true), 
            A(CSequenceActionIds.ProcessZeroResults, "0선 결과 / 후속 요청"), 
            A(CSequenceActionIds.ProcessResults, "결과 확인 및 저장"), 
            S("PRC", "PROCESS", "COMPLETE"), 
            S("STG", "PROCESS", "COMPLETE"), 
            A("PROCESS_SIGNALS_CLEAR", "Process 신호 초기화")
        ]), 
            new("REVIEW", "Review", [
            S("STG", "REVIEW", "ABLE"), 
            A("REVIEW_PLAN", "검사 좌표 및 조건 준비"), 
            S("PRC", "REVIEW", "ABLE"), 
            S("STG", "REVIEW", "START"), 
            S("PRC", "REVIEW", "START"), 
            A("REVIEW_MEASURE", "검사위치 이동 및 검사"), 
            A("REVIEW_RESULTS", "결과 판정 및 저장"), 
            S("PRC", "REVIEW", "COMPLETE"), 
            S("STG", "REVIEW", "COMPLETE"), 
            A("REVIEW_SIGNALS_CLEAR", "Review 신호 초기화")
        ]), 
            // Existing Manual ALIGN REQUEST owns the output. This scenario only observes
            // scaled available/request/complete values and the raw WFAB3 Align Busy bit.
            new("DO_ALIGN", "Do Align", [
            new("SDC_DO_ALIGN_STATUS", "Do Align ABLE", "STAGE → PROCESS", true, EqualsOne: true),
            new("PROCESS_DO_ALIGN", "Do Align REQUEST", "PROCESS → STAGE", true),
            new("STAGE_MCC_BUSY_FLAGS", "Do Align BUSY", "STAGE → PROCESS", true, RunningMask: 1 << 1),
            new("SDC_DO_ALIGN_COMPLETE", "Do Align COMPLETE", "STAGE → PROCESS", true, EqualsOne: true)
        ]),
            new("TOP_POWERMETER", "TPM", [
            S("STG", "TOP_POWERMETER", "ABLE"), 
            A("TPM_PREPARE", "파워 측정 조건 준비"), 
            S("PRC", "TOP_POWERMETER", "ABLE"), 
            A("TPM_MEASURE", "ATT 이동 및 파워 측정"), 
            A("TPM_RESULTS", "측정 결과 확인 및 저장"), 
            S("PRC", "TOP_POWERMETER", "COMPLETE"), 
            S("STG", "TOP_POWERMETER", "COMPLETE"), 
            A("TPM_SIGNALS_CLEAR", "TPM 신호 초기화")
        ]), 
            new("PROCESS_MOVE", "Process Move", [
            A("MOVE_TARGETS", "이동 모드·목표 위치·속도 설정"), 
            new("HANDSHAKE_STG_REVIEW_ABLE_SDC", "SDC movement permission", "STAGE → PROCESS", true), 
            new("HANDSHAKE_PRC_REVIEW_MOVE_START", "SDC MOVE START", "PROCESS → STAGE", true), 
            new("HANDSHAKE_STG_REVIEW_COMPLETE_SDC", "SDC arrival", "STAGE → PROCESS", true), 
            A("MOVE_SETTLE", "도착 위치 및 안정화 확인"), 
            A("MOVE_RELEASE", "이동 요청 해제 및 신호 초기화 확인"), 
            new("HANDSHAKE_PRC_REVIEW_GENERAL_RUNNING", "Review GENERAL RUNNING", "PROCESS → STAGE", true), 
            A("MOVE_XREADY", "IOF에 현재 X 준비 위치 전달")
        ]), 
            new("IOF", "IOF", [
            A("IOF_PLAN", "IOF 모드·검사 개수·좌표 설정"), 
            new("HANDSHAKE_PRC_IOF_START_REQUEST", "IOF BUFFER START REQUEST", "PROCESS → STAGE", true), 
            new("IPS_IOF_READ_COMPLETE", "IOF data read complete", "STAGE → PROCESS", true), 
            new("IPS_IOF_BUFFER_RUN_COMPLETE", "IOF buffer run complete", "STAGE → PROCESS", true), 
            new("HANDSHAKE_STG_IOF_COMPLETE", "IOF COMPLETE", "STAGE → PROCESS", true), 
            new("HANDSHAKE_PRC_IOF_COMPLETE_RESPONSE", "IOF COMPLETE RESPONSE", "PROCESS → STAGE", true), 
            A("IOF_SIGNALS_CLEAR", "Stage 신호 해제 확인 및 요청·응답 초기화")
        ])
        ];
    }

    public sealed record CMainSequenceRow(CMainSequenceCard? Stage, CMainSequenceCard? Process, bool StageFirst)
    {
        public string Arrow => Stage is not null && Process is not null ? StageFirst ? "→" : "←" : "";
    }

    public sealed class CMainSequenceTab(string key, string name) : CBindingBase
    {
        private static readonly Brush RunningGreen = CStatusBrush.Frozen(0x2E, 0x8B, 0x57);
        private bool _isSelected;
        private bool _isBlinking;
        public event EventHandler? IsBlinkingChanged;
        public string Key { get; } = key;
        public string Name { get; } = name;
        public bool IsSelected => _isSelected;
        public bool IsBlinking => _isBlinking;
        public Brush BackgroundBrush => IsBlinking ? RunningGreen : IsSelected ? CStatusBrush.CommandBlue : CStatusBrush.CommandDark;
        public Brush BorderBrush => IsBlinking ? RunningGreen : IsSelected ? CStatusBrush.CommandBlueBorder : CStatusBrush.CommandDarkBorder;
        public Brush TextBrush => Brushes.White;

        internal void Refresh(bool selected, bool blinking)
        {
            if (_isSelected == selected && _isBlinking == blinking)
                return;
            var blinkChanged = _isBlinking != blinking;
            _isSelected = selected;
            _isBlinking = blinking;
            OnPropertyChanged(nameof(BackgroundBrush));
            OnPropertyChanged(nameof(BorderBrush));
            if (blinkChanged)
                IsBlinkingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public sealed class CMainSequenceCard(string id, string title, string owner, bool isSignal, int order, bool compact = false,
        int runningMask = 0, bool equalsOne = false) : CBindingBase
    {
        private static readonly Brush Yellow = CStatusBrush.Frozen(0xFF, 0xD9, 0x66);
        private static readonly Brush Green = CStatusBrush.Frozen(0x2E, 0x8B, 0x57);
        private static readonly Brush Red = CStatusBrush.Frozen(0xE6, 0x21, 0x21);
        private string _state = isSignal ? "OFFLINE" : "WAIT";
        private string _detail = "";
        private string _progressText = "";
        public string ProgressText => _progressText;
        public string FlowTitle => string.IsNullOrEmpty(_progressText) ? Title : $"{Title} · {_progressText}";

        internal void SetProgress(string text)
        {
            if (_progressText == text)
                return;
            _progressText = text;
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(FlowTitle));
        }

        public event EventHandler? IsBlinkingChanged;
        public string Id { get; } = id;
        public string Title { get; } = title;
        public string DisplayTitle => IsCompact && IsSignal ? Owner.Replace("STAGE", "STG").Replace("PROCESS", "PRC") + " · " + Title.Replace("Actual machining complete", "Machining complete") : Title;
        public string Caption => Owner + " · " + Title;
        public string Owner { get; } = owner;
        public bool IsCompact { get; } = compact;
        public bool OpensMonitor => Id is "REVIEW_MEASURE" or "TPM_MEASURE" or CSequenceActionIds.ProcessZeroResults;
        public bool IsSignal { get; } = isSignal;
        public bool IsStage => IsSignal && Owner == "STAGE → PROCESS";
        public string Order { get; } = order.ToString("00", CultureInfo.InvariantCulture);
        public string State => _state;
        public string Detail => _detail;
        public bool IsBlinking => State == "RUNNING";
        public Brush StateBrush => State is "ON" or "RUNNING" or "DONE" ? Green : State is "OFF" or "WAIT" ? Yellow : Red;
        public Brush TextBrush => State is "OFF" or "WAIT" ? Brushes.Black : Brushes.White;

        internal void RefreshSignal(ST_MELSEC_MAP_DATA? row, ST_MELSEC_CACHED_VALUE? value, bool connected)
        {
            var state = ReadSignalState(row, value, connected, runningMask, equalsOne);
            var detail = row is null ? $"{Id}: Not configured." : $"{Id} / {row.Address}";
            if (value?.IsValid == true)
                detail += $" / Value: {value.Value}";
            if (value is { IsValid: false })
                detail += $" / {value.State}: {value.ErrorMessage}";
            Apply(state, detail);
        }

        internal static string ReadSignalState(ST_MELSEC_MAP_DATA? row, ST_MELSEC_CACHED_VALUE? value, bool connected,
            int runningMask = 0, bool equalsOne = false)
        {
            if (!connected) return "OFFLINE";
            if (row?.Use != true) return "NOT SET";
            if (value?.IsValid != true)
                return value?.State switch
                {
                    EN_MELSEC_CACHE_STATE.Stale => "STALE",
                    EN_MELSEC_CACHE_STATE.Offline => "OFFLINE",
                    EN_MELSEC_CACHE_STATE.Error => "ERROR",
                    _ => "WAIT DATA"
                };
            double number;
            try { number = value.Value is null ? double.NaN : Convert.ToDouble(value.Value, CultureInfo.InvariantCulture); }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException) { return "ERROR"; }
            if (!double.IsFinite(number)) return "ERROR";
            // The MCC busy WORD is unscaled; other bits must never imply Align activity.
            // Available/complete use already-scaled equality to 1, as in Manual MOF.
            if (runningMask != 0)
                return number < 0 || number > ushort.MaxValue || number != Math.Truncate(number) ? "ERROR"
                    : ((int)number & runningMask) != 0 ? "RUNNING" : "OFF";
            if (equalsOne) return number == 1 ? "ON" : "OFF";
            return number == 0 ? "OFF" : number == 1 ? "ON" : "ERROR";
        }

        internal void RefreshAction(ST_SEQUENCE_ACTION_STATUS? action)
        {
            var state = action?.State switch
            {
                EN_SEQUENCE_ACTION_STATE.Running => "RUNNING",
                EN_SEQUENCE_ACTION_STATE.Completed => "DONE",
                EN_SEQUENCE_ACTION_STATE.Error => "ERROR",
                EN_SEQUENCE_ACTION_STATE.Stopped => "STOPPED",
                _ => "WAIT"
            };
            Apply(state, action?.Detail ?? "아직 실행되지 않은 동작입니다.");
        }

        private void Apply(string state, string detail)
        {
            if (SetProperty(ref _state, state, nameof(State)))
            {
                OnPropertyChanged(nameof(StateBrush));
                OnPropertyChanged(nameof(TextBrush));
                IsBlinkingChanged?.Invoke(this, EventArgs.Empty);
            }

            SetProperty(ref _detail, detail, nameof(Detail));
        }
    }

    internal sealed record ST_HEAD_ASSIGNMENT_MAP(IReadOnlyList<ST_HEAD_ASSIGNMENT_AREA> Areas);
    internal sealed record ST_MAIN_RECIPE_PREVIEW(
        ImageSource? Image, 
        ST_GLASS_PREVIEW_FRAME Frame, 
        IReadOnlyDictionary<int, long> HeadPointCounts, 
        long TotalPointCount, 
        long UnassignedPointCount, 
        IReadOnlyList<ST_CELL_PREVIEW_LABEL> CellLabels, 
        string ValidationMessage = "", 
        int MaskingAreaCount = 0
    );
    internal sealed record ST_PREVIEW_HEAD_LAYOUT(IReadOnlyList<ST_PREVIEW_HEAD_FIELD> Fields)
    {
        public ST_PREVIEW_HEAD_FIELD GetField(int headNo)
        {
            var normalizedHeadNo = Math.Clamp(headNo, 1, 8);
            bool MatchesItem(global::Drilling.UI.Menu.Menus.ST_PREVIEW_HEAD_FIELD field)
            {
                return field.HeadNo == normalizedHeadNo;
            }

            return Fields.First(MatchesItem);
        }
    }

    internal sealed record ST_PREVIEW_HEAD_FIELD(int HeadNo, double PositionX, double ScanFieldWidthX);
    public sealed record ST_GLASS_PREVIEW_FRAME(double CanvasLeft, double CanvasTop, double Width, double Height);
    public sealed record ST_HEAD_PREVIEW(int HeadNo, string HeadName, string Status, bool IsSelected, bool IsEnabled = true)
    {
        public long AssignedShotCount { get; init; }
        public string ShotCountText => $"{AssignedShotCount:N0} Shots";
        public string StatusDetail { get; init; } = "";
        public Brush StatusBrush => Status switch
        {
            "RUNNING" => CStatusBrush.CommandGreen,
            "PowerMeasuring" or "POWER MEASURE" => Brushes.Gold,
            "INTERLOCK" => CStatusBrush.CommandRed,
            _ => Brushes.DimGray
        };
        public Brush StatusTextBrush => Status is "PowerMeasuring" or "POWER MEASURE" ? Brushes.Black : Brushes.White;

        public Brush TextBrush
        {
            get
            {
                return IsSelected ? Brushes.White : CStatusBrush.PrimaryText;
            }
        }
    }

    public sealed record ST_MAIN_PROCESS_SEQUENCE_ITEM(string Order, string StepName, string State)
    {
        public Brush StateBrush => CStatusBrush.ForDisplayState(State);
    }

    public sealed record ST_HEAD_ASSIGNMENT_AREA(
        int HeadNo, 
        string HeadName, 
        double CanvasLeft, 
        double CanvasTop, 
        double Width, 
        double Height, 
        double LabelCanvasLeft, 
        double LabelWidth, 
        bool IsSelected, 
        int PointCount, 
        Brush FillBrush, 
        Brush StrokeBrush, 
        Thickness BorderThicknessValue, 
        double Opacity
    )
    {
        public double LabelCanvasTop
        {
            get
            {
                return CanvasTop - 24.0;
            }
        }

        public Visibility RangeVisibility
        {
            get
            {
                return IsSelected ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public string DisplayLabel
        {
            get
            {
                return $"H{HeadNo:00}";
            }
        }
    }

    internal readonly record struct ST_PREVIEW_HEAD_ASSIGNMENT_CANDIDATE(int HeadNo, double Distance);
    internal readonly record struct ST_SCRIPT_TASK_DEFINITION(int HeadNo, int AutomationNo, int TaskNo, string ScriptFileName, int TotalShots);
    internal readonly record struct ST_SCRIPT_TASK_STATUS_VALUE(string State, string FileName, string Detail);
    internal readonly record struct ST_AUTOMATION_CONTROLLER_USAGE_VALUE(string State, double Percent, string Detail);
    public sealed record ST_AUTOMATION_CONTROLLER_STATUS_ITEM(int AutomationNo, string State, double CpuUsagePercent, string Detail, IReadOnlyList<ST_SCRIPT_TASK_STATUS_ITEM> Tasks)
    {
        public string ControllerName
        {
            get
            {
                return $"A1 #{AutomationNo}";
            }
        }

        public string CpuUsageText
        {
            get
            {
                var normalizedState = State.Trim().ToUpperInvariant();
                return normalizedState is "OK" or "WARN" ? $"{CpuUsagePercent.ToString("F1", CultureInfo.InvariantCulture)}%" : normalizedState;
            }
        }

        public string TaskSummary
        {
            get
            {
                return $"{Tasks.Count.ToString("N0", CultureInfo.InvariantCulture)} TASKS";
            }
        }

        public string DetailText
        {
            get
            {
                return string.IsNullOrWhiteSpace(Detail) ? "-" : Detail;
            }
        }

        public Brush StateBrush
        {
            get
            {
                return ControllerStateBrush(State);
            }
        }

        public Brush CpuUsageBrush
        {
            get
            {
                return ControllerStateBrush(State);
            }
        }

        private static Brush ControllerStateBrush(string state)
        {
            return state.Trim().ToUpperInvariant() switch
            {
                "OK" => CStatusBrush.Online,
                "SIM" or "WAIT" or "WARN" => CStatusBrush.Wait,
                "OFFLINE" or "ERROR" or "FAULT" => CStatusBrush.Offline,
                _ => CStatusBrush.Muted
            };
        }
    }

    public sealed record ST_SCRIPT_TASK_STATUS_ITEM(string Task, string Head, string Automation, string TaskNo, string State, string File, string Detail, int Shots)
    {
        public bool IsSimulation { get; init; }
        public string StateText => IsSimulation ? $"{State}(SIMUL)" : State;

        public int AutomationNoValue
        {
            get
            {
                return ReadPrefixedNumber(Automation, 'A', 0);
            }
        }

        public int TaskNoValue
        {
            get
            {
                return ReadPrefixedNumber(TaskNo, 'T', 1);
            }
        }

        public int HeadNoValue
        {
            get
            {
                return ReadPrefixedNumber(Head, 'H', 0);
            }
        }

        public string AutomationText
        {
            get
            {
                return $"A1 #{AutomationNoValue}";
            }
        }

        public string ControllerTaskText
        {
            get
            {
                return $"{AutomationText} / {TaskNo}";
            }
        }

        public string FileText
        {
            get
            {
                return string.IsNullOrWhiteSpace(File) ? "-" : File;
            }
        }

        public string DetailText
        {
            get
            {
                return string.IsNullOrWhiteSpace(Detail) ? "-" : Detail;
            }
        }

        public string ShotText
        {
            get
            {
                return $"{Shots.ToString("N0", CultureInfo.InvariantCulture)} SHOTS";
            }
        }

        public Brush StateBrush
        {
            get
            {
                return TaskStateBrush(State);
            }
        }

        public bool IsStateHighlighted => State.Trim().ToUpperInvariant()is "RUNNING" or "QUEUESTARTING" or "OK" or "DONE" or "READY" or "STOP" or "STOPPED" or "ERROR" or "FAULT" or "OFFLINE";
        public Brush CardTextBrush => Brushes.White;

        private static Brush TaskStateBrush(string state)
        {
            return state.Trim().ToUpperInvariant() switch
            {
                "RUNNING" or "QUEUESTARTING" => CStatusBrush.Wait,
                "OK" or "DONE" or "READY" => CStatusBrush.Online,
                "IDLE" or "SIM" or "UNKNOWN" => CStatusBrush.Muted,
                "STOP" or "STOPPED" or "ERROR" or "FAULT" or "OFFLINE" => CStatusBrush.Offline,
                _ => CStatusBrush.Muted
            };
        }

        private static int ReadPrefixedNumber(string value, char prefix, int defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            var text = value.Trim();
            if (text.Length > 0 && char.ToUpperInvariant(text[0]) == char.ToUpperInvariant(prefix))
            {
                text = text[1..];
            }

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }
    }

    public sealed record ST_INSPECTION_STATUS_ITEM(string No, string Head, string Cell, string Shot, string ErrorX, string ErrorY, string State, string Judge)
    {
        public Brush StateBrush
        {
            get
            {
                return CStatusBrush.ForDisplayState(State);
            }
        }

        public Brush JudgeBrush
        {
            get
            {
                return CStatusBrush.ForDisplayState(Judge);
            }
        }
    }

    public sealed record ST_MAIN_PARAMETER_TAB_ITEM(string Key, string Name, bool IsSelected, bool IsEnabled = true)
    {
        public Brush BackgroundBrush
        {
            get
            {
                return IsEnabled && IsSelected ? CStatusBrush.CommandBlue : CStatusBrush.CommandDark;
            }
        }

        public Brush BorderBrush
        {
            get
            {
                return IsEnabled && IsSelected ? CStatusBrush.CommandBlueBorder : CStatusBrush.CommandDarkBorder;
            }
        }

        public Brush TextBrush
        {
            get
            {
                return IsEnabled ? Brushes.White : CStatusBrush.Muted;
            }
        }
    }

    public sealed record ST_HEAD_PARAMETER(string Head, double Power, double Frequency, int Shot, double JumpSpeed, double DoeZPosition);
    public sealed record ST_OPTIC_HEAD_PARAMETER(
        string Head, 
        string State, 
        string LaserPower, 
        string LaserGate, 
        string LaserShutter, 
        string LaserState, 
        string AttenuatorCurrent, 
        string AttenuatorTarget, 
        string AttenuatorState, 
        string BetMagnificationCurrentStep, 
        string BetMagnificationTargetStep, 
        string BetDivergenceCurrentStep, 
        string BetDivergenceTargetStep, 
        string BetState
    )
    {
        public string LaserPowerText
        {
            get
            {
                return $"P {LaserPower}";
            }
        }

        public string LaserGateText
        {
            get
            {
                return $"G {LaserGate}";
            }
        }

        public string LaserShutterText
        {
            get
            {
                return $"S {LaserShutter}";
            }
        }

        public string AttenuatorText
        {
            get
            {
                return $"{AttenuatorCurrent} / {AttenuatorTarget}";
            }
        }

        public string BetMagnificationText
        {
            get
            {
                return $"MAG {BetMagnificationCurrentStep} / {BetMagnificationTargetStep}";
            }
        }

        public string BetDivergenceText
        {
            get
            {
                return $"DIV {BetDivergenceCurrentStep} / {BetDivergenceTargetStep}";
            }
        }

        public Brush StateBrush
        {
            get
            {
                return ST_OPTIC_PARAMETER_ITEM.OpticStateBrush(State);
            }
        }

        public Brush LaserStateBrush
        {
            get
            {
                return ST_OPTIC_PARAMETER_ITEM.OpticStateBrush(LaserState);
            }
        }

        public Brush LaserPowerBrush
        {
            get
            {
                return ST_OPTIC_PARAMETER_ITEM.OpticStateBrush(LaserPower);
            }
        }

        public Brush LaserGateBrush
        {
            get
            {
                return ST_OPTIC_PARAMETER_ITEM.OpticStateBrush(LaserGate);
            }
        }

        public Brush LaserShutterBrush
        {
            get
            {
                return ST_OPTIC_PARAMETER_ITEM.OpticStateBrush(LaserShutter);
            }
        }

        public Brush AttenuatorStateBrush
        {
            get
            {
                return ST_OPTIC_PARAMETER_ITEM.OpticStateBrush(AttenuatorState);
            }
        }

        public Brush BetStateBrush
        {
            get
            {
                return ST_OPTIC_PARAMETER_ITEM.OpticStateBrush(BetState);
            }
        }
    }

    public sealed record ST_OPTIC_PARAMETER_GROUP(string Device, string State, IReadOnlyList<ST_OPTIC_PARAMETER_ITEM> Items)
    {
        public Brush StateBrush
        {
            get
            {
                return ST_OPTIC_PARAMETER_ITEM.OpticStateBrush(State);
            }
        }
    }

    public sealed record ST_OPTIC_PARAMETER_ITEM(string Item, string Current, string RecipeTarget, string State)
    {
        public Brush StateBrush
        {
            get
            {
                return OpticStateBrush(State);
            }
        }

        public static Brush OpticStateBrush(string state)
        {
            return state.Trim().ToUpperInvariant() switch
            {
                "OK" or "ON" or "OPEN" or "RUN" or "READY" or "SAFE" or "CLOSE" or "DONE" => CStatusBrush.Online,
                "MOVING" or "WARN" or "WAIT" or "STOP" => CStatusBrush.Wait,
                "ALARM" or "ERROR" or "NG" or "OFFLINE" => CStatusBrush.Offline,
                "N/C" => CStatusBrush.Muted,
                _ => CStatusBrush.Muted
            };
        }
    }

    internal readonly record struct ST_OPTIC_LASER_HEAD_PARAMETER(string Power, string Gate, string Shutter, string State);
    internal readonly record struct ST_OPTIC_AXIS_HEAD_PARAMETER(string Current, string Target, string State);
    internal readonly record struct ST_OPTIC_BET_HEAD_PARAMETER(string MagnificationCurrentStep, string MagnificationTargetStep, string DivergenceCurrentStep, string DivergenceTargetStep, string State);
    public sealed record ST_SCANNER_AXIS_STATUS_ITEM(
        string Head, 
        string Automation, 
        string Axis, 
        string AxisNo, 
        string Able, 
        string Position, 
        string AuxFeedback, 
        string CurrentFeedback, 
        string GalvoLaserOutputRaw, 
        string GalvoTemperature, 
        string Home, 
        string Fault, 
        string Error, 
        string Detail
    )
    {
        public Brush AbleBrush
        {
            get
            {
                return ScannerStateBrush(Able);
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

        public Brush FaultBrush
        {
            get
            {
                return IsFaultClear(Fault) ? CStatusBrush.Online : CStatusBrush.Offline;
            }
        }

        private static Brush ScannerStateBrush(string state)
        {
            return state.Trim().ToUpperInvariant() switch
            {
                "ABLE" or "HOME" or "OK" => CStatusBrush.Online,
                "DISABLE" or "WAIT" => CStatusBrush.Wait,
                "NO" or "ERROR" or "OFFLINE" => CStatusBrush.Offline,
                _ => CStatusBrush.Muted
            };
        }

        private static bool IsFaultClear(string fault)
        {
            var normalized = fault.Trim();
            return normalized.Length == 0 || normalized.Equals("-", StringComparison.OrdinalIgnoreCase) || normalized.Equals("0", StringComparison.OrdinalIgnoreCase) || normalized.Equals("0.0", StringComparison.OrdinalIgnoreCase) || normalized.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class CMotorStatusPollingService(CInterfaceManager interfaceManager) : CtrlThread
    {
        private static readonly (string Name, string Key)[] Axes = [("Stage Y", "STAGE_Y"), ("Review X", "CAMERA_X"), ("Window Y", "WINDOW_Y")];
        private readonly object _snapshotLock = new();
        private IReadOnlyList<ST_MOTOR_STATUS_ITEM> _snapshot = CreateEmptySnapshot();
        public void Start()
        {
            if (!IsRunning)
                Run(); // Populate the first frame from shared memory before the menu is shown.
            base.Start(1000, "MotorStatusPolling");
        }

        public IReadOnlyList<ST_MOTOR_STATUS_ITEM> GetSnapshot()
        {
            lock (_snapshotLock)
            {
                return _snapshot;
            }
        }

        public void ClearSnapshot()
        {
            lock (_snapshotLock)
            {
                _snapshot = CreateEmptySnapshot();
            }
        }

        public override void Run()
        {
            var items = ReadCachedSnapshot(interfaceManager);
            lock (_snapshotLock)
            {
                _snapshot = items;
            }
        }

        internal static IReadOnlyList<ST_MOTOR_STATUS_ITEM> ReadCachedSnapshot(CInterfaceManager interfaceManager)
        {
            var cachedValues = interfaceManager.Melsec.GetCachedSnapshot(Axes.SelectMany(axis => new[] { $"MOTOR_{axis.Key}_CUR_POS", $"MOTOR_{axis.Key}_CUR_SPEED", $"MOTOR_{axis.Key}_CUR_ACC" }), out var mapRows);
            var map = mapRows.Where(row => row.Id.StartsWith("MOTOR_", StringComparison.OrdinalIgnoreCase)).ToDictionary(row => row.Id, StringComparer.OrdinalIgnoreCase);
            var simulatedDevices = interfaceManager.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec).Where(row => row.IsSimulation).Select(row => row.Number).ToHashSet();
            var simulationFrames = new Dictionary<int, IReadOnlyDictionary<string, double>>();
            (string Value, string Message) Read(string id)
            {
                map.TryGetValue(id, out var row);
                var number = row?.DeviceNo ?? mapRows.FirstOrDefault(item => item.Id == "MOTOR_STAGE_Y_CUR_POS")?.DeviceNo ?? interfaceManager.GetInterfaceList(EN_EQP_MODULE.Melsec).FirstOrDefault()?.Number;
                (string Value, string Message)? fallback = null;
                if (number is { } deviceNo && simulatedDevices.Contains(deviceNo))
                {
                    if (!simulationFrames.TryGetValue(deviceNo, out var frame))
                        simulationFrames[deviceNo] = frame = interfaceManager.GetStageMotionSimulationFeedback(deviceNo);
                    if (frame.TryGetValue(id, out var value))
                        fallback = (value.ToString("F3", CultureInfo.InvariantCulture), "Shared MELSEC stage simulation feedback.");
                }

                // Simulation's shared in-memory motion frame is newer than the last periodic PLC-cache sample.
                return fallback ?? ReadValue(map, cachedValues, id);
            }

            return Axes.Select(axis =>
            {
                var position = Read($"MOTOR_{axis.Key}_CUR_POS");
                var speed = Read($"MOTOR_{axis.Key}_CUR_SPEED");
                var acceleration = Read($"MOTOR_{axis.Key}_CUR_ACC");
                return new ST_MOTOR_STATUS_ITEM(axis.Name, position.Value, speed.Value, acceleration.Value)
                {
                    PositionMessage = position.Message,
                    SpeedMessage = speed.Message,
                    AccelerationMessage = acceleration.Message
                };
            }).ToArray();
        }

        internal static EN_COMM_STATE ReadStageConnectionState(CInterfaceManager interfaceManager)
        {
            // Simulation is an operating setting and remains usable while the interface is offline.
            // Read the current runtime setting and connection state without device I/O.
            var ids = new[]
            {
                "MOTOR_STAGE_Y_CUR_POS",
                "MOTOR_STAGE_Y_CUR_SPEED",
                "MOTOR_CAMERA_X_CUR_POS",
                "MOTOR_CAMERA_X_CUR_SPEED"
            };
            var map = interfaceManager.Melsec.GetMapList();
            var rows = ids.Select(id => map.FirstOrDefault(row => string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase))).ToArray();
            var connections = interfaceManager.GetInterfaceCommunicationList(EN_EQP_MODULE.Melsec);
            // Unassigned/disabled feedback rows are excluded from the active map. Simulation
            // must still work before addresses are configured; use the registered interfaces.
            IEnumerable<ST_INTERFACE_COMM_STATUS?> axisConnections = rows.Any(row => row is null) ? connections : rows.Select(row => row!.DeviceNo).Distinct().Select(number => connections.FirstOrDefault(device => device.Number == number));
            var states = axisConnections.Select(device => device?.IsSimulation == true ? EN_COMM_STATE.Simulation : device?.ConnectionState ?? EN_COMM_STATE.Offline).Distinct().ToArray();
            return states.Length == 1 ? states[0] : EN_COMM_STATE.Offline;
        }

        private static (string Value, string Message) ReadValue(
            IReadOnlyDictionary<string, ST_MELSEC_MAP_DATA> map, 
            IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE> cachedValues, 
            string id, 
            (string Value, string Message)? simulationFallback = null
        )
        {
            if (!map.TryGetValue(id, out var row) || !row.Use)
            {
                return simulationFallback ?? ("-", "MELSEC feedback is not configured.");
            }

            if (row.Access == EN_MELSEC_ACCESS.Write)
            {
                return ("ERR", $"{id} is write-only.");
            }

            if (!cachedValues.TryGetValue(id, out var cached) || cached.State == EN_MELSEC_CACHE_STATE.Pending)
            {
                return simulationFallback ?? ("-", "Waiting for MELSEC feedback.");
            }

            if (!cached.IsValid)
            {
                if (row.Access == EN_MELSEC_ACCESS.Read && cached.State is EN_MELSEC_CACHE_STATE.Offline or EN_MELSEC_CACHE_STATE.Stale && simulationFallback is { } simulation)
                    return simulation;
                return ("ERR", CMelsecCachePresentation.Describe(cached));
            }

            var value = CMelsecCachePresentation.NumericValue(cached.Value);
            value = row.DataType switch
            {
                EN_MELSEC_DATA_TYPE.Word or EN_MELSEC_DATA_TYPE.DWord or EN_MELSEC_DATA_TYPE.Double or EN_MELSEC_DATA_TYPE.Float => value,
                _ => double.NaN
            };
            return double.IsFinite(value) ? (value.ToString("F3", CultureInfo.InvariantCulture), CMelsecCachePresentation.Describe(cached)) : ("ERR", $"{id} has invalid numeric feedback.");
        }

        private static IReadOnlyList<ST_MOTOR_STATUS_ITEM> CreateEmptySnapshot() => Axes.Select(axis => new ST_MOTOR_STATUS_ITEM(axis.Name, "-", "-", "-")).ToArray();
    }

    public sealed record ST_MOTOR_STATUS_ITEM(string Name, string Position, string Speed, string Acceleration)
    {
        public string PositionMessage { get; init; } = "";
        public string SpeedMessage { get; init; } = "";
        public string AccelerationMessage { get; init; } = "";
    }

    /// <summary>Read-only projection of the coordinator's immutable snapshot. No device or result-file reads.</summary>
    public sealed class CZeroLineResultsPanel(Func<ST_ZERO_LINE_CYCLE_STATUS?> readSnapshot) : INotifyPropertyChanged
    {
        private ST_ZERO_LINE_CYCLE_STATUS? _seen;
        private CZeroLineResultRow? _selected;
        public event PropertyChangedEventHandler? PropertyChanged;
        public IReadOnlyList<CZeroLineResultRow> Rows { get; private set; } = [];
        public string Header { get; private set; } = "0선방어 결과";
        public string Context { get; private set; } = "아직 0선방어 검사 결과가 없습니다.";
        public string Summary { get; private set; } = "검사 대기";
        public string Detail { get; private set; } = "Process 시퀀스의 0선방어 검사가 시작되면 결과를 표시합니다.";
        public string Tolerance { get; private set; } = "허용오차 (Recv X / Y) : -";
        public string GroupJudge { get; private set; } = "미완료";
        public Brush GroupBrush { get; private set; } = Brushes.DarkGoldenrod;
        public string ResultFile { get; private set; } = "결과 파일: 미저장";
        public int RefreshCount { get; private set; }

        public CZeroLineResultRow? SelectedRow
        {
            get => _selected;
            set
            {
                if (ReferenceEquals(_selected, value))
                    return;
                _selected = value;
                Notify(nameof(SelectedRow), nameof(SelectedTitle), nameof(SelectedRaw), nameof(SelectedCoordinates), nameof(SelectedReason));
            }
        }

        public string SelectedTitle => SelectedRow is { } r ? $"{r.Head} / {r.Cell}-{r.Shot} / Beam {r.Beam}" : "결과 행을 선택하면 세부값을 표시합니다.";
        public string SelectedRaw => SelectedRow is { } r ? $"Target X / Y : {r.Target} pixel     Hole X / Y : {r.Hole} pixel     Resolution : {r.Resolution}" : "";
        public string SelectedCoordinates => SelectedRow is { } r ? $"Review X : {r.ReviewX} mm     Stage Y : {r.StageY} mm     Recv 허용오차 초과 X / Y : {r.Excess} mm" : "";
        public string SelectedReason => SelectedRow?.Reason ?? "";

        public void Refresh()
        {
            var frame = readSnapshot();
            if (frame is null || ReferenceEquals(frame, _seen))
                return;
            var prior = SelectedRow;
            var sameRun = _seen?.Plan?.RunId == frame.Plan?.RunId;
            _seen = frame;
            var plan = frame.Plan;
            Rows = (frame.Results ?? []).Select(result => new CZeroLineResultRow(result, plan)).ToArray();
            Context = plan is null ? $"Group {frame.Group} · {Rows.Count} Points" : $"Recipe : {plan.RecipeId}     Glass : {plan.GlassId}     Group : {frame.Group} / {plan.TotalGroups}     {Rows.Count} Points";
            Summary = $"검사 {Rows.Count(r => r.IsFinished)} / {Rows.Count} · OK {Rows.Count(r => r.Judge == "OK")} · NG {Rows.Count(r => r.Judge == "NG")} · 미완료 {Rows.Count(r => !r.IsFinished)}";
            Detail = frame.Detail
                + (frame.FollowUpRequested ? " · 후속 중앙 1줄 IOF 요청·완료 대기 (검사 전용·보정 없음)"
                    : frame.FollowUpCompleted ? " · 후속 중앙 1줄 IOF 완료 (보정 미적용·0선 원본 결과 유지)" : "");
            GroupJudge = frame.Judge;
            GroupBrush = GroupJudge switch
            {
                "OK" => Brushes.ForestGreen,
                "NG" => Brushes.Firebrick,
                _ => Brushes.DarkGoldenrod
            };
            Tolerance = plan is null ? "허용오차 (Recv X / Y) : -" : $"허용오차 (Recv X / Y) : {CZeroLineResultRow.Number(plan.VisionPlan.ToleranceX)} / {CZeroLineResultRow.Number(plan.VisionPlan.ToleranceY)} mm     Vision Flip : {plan.VisionPlan.VisionFlipType}";
            ResultFile = string.IsNullOrEmpty(frame.ResultFile) ? "결과 파일: 미저장" : "결과 파일: " + frame.ResultFile;
            var selected = sameRun && prior is not null ? Rows.FirstOrDefault(row => row.Key == prior.Key) ?? Rows.FirstOrDefault() : Rows.FirstOrDefault();
            RefreshCount++;
            Notify(nameof(Rows), nameof(Context), nameof(Summary), nameof(Detail), nameof(GroupJudge), nameof(GroupBrush), nameof(Tolerance), nameof(ResultFile));
            SelectedRow = selected;
        }

        private void Notify(params string[] properties)
        {
            foreach (var property in properties)
                PropertyChanged?.Invoke(this, new(property));
        }
    }

    public sealed class CZeroLineResultRow
    {
        public CZeroLineResultRow(ST_ZERO_LINE_POINT_RESULT result, ST_ZERO_LINE_CYCLE_PLAN? plan)
        {
            var p = result.Point;
            Key = $"{p.HeadNo}/{p.CellNo}/{p.ShotNo}/{p.BeamNo}";
            Head = $"HEAD {p.HeadNo:00}";
            Cell = $"Cell{p.CellNo:00}";
            Shot = CReviewShotNameFormatter.ToMatrixName(p.ShotNo, p.PixelCountX);
            Beam = p.BeamNo;
            State = result.State switch
            {
                "WAIT" => "대기",
                "RUNNING" => "검사 중",
                "STOPPED" => "중지",
                _ => result.State
            };
            IsFinished = result.State is "OK" or "NG" or "NCK" or "ERROR";
            Judge = result.IsNg ? "NG" : result.State == "OK" ? "OK" : result.State == "RUNNING" ? "검사 중" : "미검사";
            StateBrush = result.IsNg ? Brushes.Firebrick : result.State == "OK" ? Brushes.ForestGreen : Brushes.DarkGoldenrod;
            ReviewX = p.ReviewTargetX.ToString("F3", CultureInfo.InvariantCulture);
            StageY = p.ReviewTargetY.ToString("F3", CultureInfo.InvariantCulture);
            var measured = result.Mark.HasValue;
            RecvX = measured ? Number(p.ErrorX) : "-";
            RecvY = measured ? Number(p.ErrorY) : "-";
            Target = measured ? $"{Number(result.Mark!.Value.TargetMarkX)} / {Number(result.Mark.Value.TargetMarkY)}" : "- / -";
            Hole = measured ? $"{Number(result.Mark!.Value.HoleX)} / {Number(result.Mark.Value.HoleY)}" : "- / -";
            Resolution = measured ? result.Resolution.ToString(CultureInfo.InvariantCulture) : "-";
            double excessX = 0, excessY = 0;
            if (measured && plan is not null)
            {
                var value = CReviewManager.VisionErrorToStageOffset(p.ErrorX, p.ErrorY, plan.VisionPlan.VisionFlipType);
                ConvertedX = Number(value.X);
                ConvertedY = Number(value.Y);
                // Match the common mm conversion -> Vision Flip -> Tolerance order.
                excessX = Math.Max(0, Math.Abs(value.X) - plan.VisionPlan.ToleranceX);
                excessY = Math.Max(0, Math.Abs(value.Y) - plan.VisionPlan.ToleranceY);
                Excess = $"{Number(excessX)} / {Number(excessY)}";
            }

            Reason = result.Detail;
            if (string.IsNullOrEmpty(Reason))
            {
                var causes = new List<string>();
                if (excessX > 0)
                    causes.Add($"X 허용오차 초과 +{Number(excessX)} mm");
                if (excessY > 0)
                    causes.Add($"Y 허용오차 초과 +{Number(excessY)} mm");
                Reason = causes.Count > 0 ? string.Join(" / ", causes) : result.State switch
                {
                    "OK" => "정상",
                    "NCK" => "촬영 개수 불일치",
                    "ERROR" => "측정 실패",
                    "NG" => "Vision NG",
                    "RUNNING" => "검사 진행 중",
                    "STOPPED" => "검사 중지",
                    _ => "검사 대기"
                };
            }
        }

        public static string Number(double value) => value.ToString("F4", CultureInfo.InvariantCulture);
        public string Key { get; }
        public string Head { get; }
        public string Cell { get; }
        public string Shot { get; }
        public int Beam { get; }
        public string State { get; }
        public string Judge { get; }
        public bool IsFinished { get; }
        public Brush StateBrush { get; }
        public string RecvX { get; }
        public string RecvY { get; }
        public string ConvertedX { get; } = "-";
        public string ConvertedY { get; } = "-";
        public string Target { get; }
        public string Hole { get; }
        public string Resolution { get; }
        public string ReviewX { get; }
        public string StageY { get; }
        public string Excess { get; } = "- / -";
        public string Reason { get; }
    }
}
