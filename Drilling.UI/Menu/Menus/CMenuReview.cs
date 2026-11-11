namespace Drilling.UI.Menu.Menus
{
    using System.Windows;
    using Drilling.Common.Review;
    using Drilling.UI.Popup;
    using System.Globalization;
    using System.Data;
    using System.IO;
    using System.Windows.Media;
    using Drilling.Common.Managers;
    using Drilling.Common.Product;
    using Drilling.Common.Recipe;
    using Drilling.Common.Interface;
    using Drilling.Common.Station;
    using Drilling.UI.Menu;
    using System.Windows.Threading;
    using System.ComponentModel;

    public sealed partial class CMenuReview : CMenuBase
    {
#region 공유 상태·초기값
        private ST_BEAM_INSPECTION_SNAPSHOT? _beamInspectionSnapshot;
        private int _beamInspectionBrowseIndex;
        private bool _followBeamInspection = true;
        private CancellationTokenSource? _beamInspectionCancellation;
        private ST_BEAM_INSPECTION_RESULT_FILE_DATA? _loadedBeamInspectionResult;
        private bool _isBeamInspectionResultLoading;
        private long _beamInspectionResultLoadGeneration;
        private CancellationTokenSource? _beamInspectionResultLoadCancellation;
        private Func<CancellationToken, Task<ST_BEAM_INSPECTION_RESULT_FILE_DATA?>>? _beamInspectionResultLoader = null;
        private const string PointToPointInspectionMethod = "P2P";
        private const string InspectionOnFlyMethod = "IOF";
        private const int DefaultHeadCount = 8;
        private const int MaxHeadCount = 8;
        private const int DefaultCellCount = 20;
        private readonly CReviewManager _reviewManager;
        private readonly CRecipeManager _recipeManager;
        private readonly CStationManager _stationManager;
        private readonly CProductManager _productManager;
        private readonly Func<string> _selectedRecipeIdProvider;
        private readonly Action<string> _statusReporter;
        private readonly Action _refreshScreen;
        private readonly Action _refreshNavigationAccess;
        private readonly Action? _openCorrectionReview;
        private readonly HashSet<string> _selectedSampleShotKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _selectedInspectionBeamNos = [1];
        private readonly HashSet<string> _selectedInspectionShotKeys = new(StringComparer.OrdinalIgnoreCase);
        private int _headCount = DefaultHeadCount;
        private int _cellCount = DefaultCellCount;
        private int _sampleRuleHeadNo = 1;
        private int _sampleRuleCellNo = 1;
        private int _selectedSampleCellNo = 1;
        private int _selectedRunCellNo = 1;
        private int _totalReviewPointCount;
        private int _activePlanPointCount;
        private int _sampleMapColumnCount = 8;
        private bool _isSampleCellDetailVisible;
        private bool _isRunCellDetailVisible;
        private bool _isStartRequestPending;
        private string _selectedRunShotKey = "";
        private int _selectedRunBeamNo = 1;
        private string _selectedInspectionMethod = PointToPointInspectionMethod;
        private int _splitBeamCount = 1;
        private string _recipeId = "DRILL_A01";
        private string _sampleSelectionRecipeId = "";
        private string _selectedTab = "RUN";
        private string _selectedMode = "ALL SHOT";
        private string _selectionRuleText = "All Shot";
        private string _selectedSampleRuleUi = "ROUGH";
        private string _sampleRuleScope = "HEAD";
        private string _roughReference = "FIRST";
        private int _fineLineCount = 3;
        private string _simpleSampleShotKey = "";
        private string _pendingSimpleShot = "";
        private ST_REVIEW_RULE_PLAN? _lastCorrectionRulePlan;
        private IReadOnlyList<ST_REVIEW_PLAN_POINT>? _lastCorrectionRuleSource;
        private ST_REVIEW_PLAN? _sampleScopeCountPlan;
        private IReadOnlyDictionary<int, int> _sampleHeadPointCounts = new Dictionary<int, int>();
        private IReadOnlyDictionary<(int HeadNo, int CellNo), int> _sampleHeadCellPointCounts = new Dictionary<(int HeadNo, int CellNo), int>();
        private string _planBuildError = "";
        private EN_REVIEW_RULE_TYPE _selectedRuleType = EN_REVIEW_RULE_TYPE.AllPoint;
        private ST_REVIEW_PLAN? _lastAllPlan;
        private ST_RECIPE_DEFINITION? _lastRecipe;
        private int _lastAllPlanBeamNo;
        private IReadOnlyDictionary<string, ST_REVIEW_RUN_SHOT_ROW> _runShotRowsByKey = new Dictionary<string, ST_REVIEW_RUN_SHOT_ROW>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ST_REVIEW_POINT_SELECT_ROW> _sampleShotRowsByKey = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<ST_REVIEW_TAB_ITEM> Tabs { get; private set; } = [];
        public IReadOnlyList<ST_REVIEW_MODE_ITEM> Modes { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> TargetItems { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> SelectionSummaryItems { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> SelectedPointItems { get; private set; } = [];
        public IReadOnlyList<ST_CELL_PREVIEW_LABEL> RunCellPreviewLabels { get; private set; } = [];
        public string RunGlassPreviewSummary { get; private set; } = "0 Cells / 0 Shots";
        public IReadOnlyList<ST_REVIEW_RUN_SHOT_ROW> RunCellShotRows { get; private set; } = [];
        public IReadOnlyList<ST_REVIEW_RUN_SHOT_MATRIX_ROW> RunCellShotMatrixRows { get; private set; } = [];
        public IReadOnlyList<ST_REVIEW_BEAM_MATRIX_ROW> RunBeamRows { get; private set; } = [];
        public IReadOnlyList<ST_REVIEW_POINT_SELECT_ROW> PointSelectRows { get; private set; } = [];
        public IReadOnlyList<ST_CELL_PREVIEW_LABEL> SampleCellPreviewLabels { get; private set; } = [];
        public string SampleGlassPreviewSummary { get; private set; } = "0 Cells / 0 Shots Selected";
        public IReadOnlyList<ST_REVIEW_POINT_SELECT_ROW> SampleCellShotRows { get; private set; } = [];
        public IReadOnlyList<ST_REVIEW_POINT_SELECT_MATRIX_ROW> SampleCellShotMatrixRows { get; private set; } = [];
        public DataView ResultMatrix { get; private set; } = CreateEmptyResultMatrix();

        private CancellationTokenSource? _oneShotCancellation;
        private CancellationTokenSource? _reviewCancellation;
        private bool _oneShotRunning;
        private string _oneShotMethod = "Mark Find Single";
        private ST_ONE_SHOT_RESULT? _oneShotResult;
        public CButtonCommand OneShotActionCommand { get; private set; } = null !;
        public IReadOnlyList<string> OneShotMethods { get; } = ["Mark Find Single", "Mark Find Setting Single"];
        public string OneShotStatus { get; private set; } = "Shot을 선택해 주세요.";

        private readonly object _runtimeUiGate = new();
        private readonly Dictionary<string, ST_REVIEW_PLAN_POINT> _runtimePendingPoints = new(StringComparer.OrdinalIgnoreCase);
        private ST_REVIEW_PLAN? _runtimePendingPlan;
        private ST_REVIEW_PLAN? _runtimeSeedPlan;
        private Guid _runtimeQueuedId;
        private int _runtimeTimerScheduled;
        private DispatcherTimer? _runtimeDisplayTimer;
        private readonly Dictionary<string, int> _runtimeCurrentPointCells = new(StringComparer.OrdinalIgnoreCase);
        private readonly DataView _emptyRuntimeMatrix = CreateEmptyResultMatrix();
        private ST_REVIEW_PLAN? _finishedResultPlan;
        private DataView? _finishedResultMatrix;
        private bool _deferResultMatrix;
        private int _resultDisplayGeneration;
        private HashSet<int> _runtimePreviewCells = [];
        private int _runtimePreviewCell;
        private bool _runtimePreviewActive;
        private ImageSource? _runtimePreviewBase;
        private int _runtimeDisplayRefreshCount;
        private readonly CInterfaceManager? _cameraInterfaces;
        private readonly CSettingManager? _cameraSettings;
        private ST_RECIPE_DEFINITION? _cameraRecipe;
        private IReadOnlyList<ST_SYSTEM_PARAMETER>? _cameraSettingFrame;
        private Dictionary<string, string> _cameraParameters = new(StringComparer.OrdinalIgnoreCase);
        private Rect _cameraGlassBounds = Rect.Empty;
        private Size _cameraCanvasSize;
        public CReviewCameraPreview Camera { get; } = new();

#endregion
#region 생성·초기화
        public CMenuReview(
            CReviewManager reviewManager, 
            CRecipeManager recipeManager, 
            CStationManager stationManager, 
            CProductManager productManager, 
            Func<string> selectedRecipeIdProvider, 
            Action<string> statusReporter, 
            Action refreshScreen, 
            Action refreshNavigationAccess, 
            CInterfaceManager? interfaceManager = null, 
            CSettingManager? settingManager = null, 
            Action? openCorrectionReview = null
        )
        {
            _reviewManager = reviewManager;
            _recipeManager = recipeManager;
            _stationManager = stationManager;
            _productManager = productManager;
            _selectedRecipeIdProvider = selectedRecipeIdProvider;
            _statusReporter = statusReporter;
            _refreshScreen = refreshScreen;
            _refreshNavigationAccess = refreshNavigationAccess;
            _openCorrectionReview = openCorrectionReview;
            _cameraInterfaces = interfaceManager;
            _cameraSettings = settingManager;
            SelectTabCommand = new CButtonCommand(SelectTab);
            SelectModeCommand = new CButtonCommand(SelectMode, _ => !IsReviewExecutionActive);
            OpenCorrectionReviewCommand = new CButtonCommand(_ => RequestOpenCorrectionReview(), _ => _openCorrectionReview is not null && !IsReviewExecutionActive);
            SelectSampleRuleUiCommand = new CButtonCommand(SelectSampleRuleUi, _ => !IsReviewExecutionActive);
            SelectSampleRuleScopeCommand = new CButtonCommand(SelectSampleRuleScope, _ => !IsReviewExecutionActive);
            SelectRoughReferenceCommand = new CButtonCommand(SelectRoughReference, _ => !IsReviewExecutionActive);
            SelectFineLineCountCommand = new CButtonCommand(SelectFineLineCount, _ => !IsReviewExecutionActive);
            TogglePointCommand = new CButtonCommand(ToggleShot, _ => IsSimpleSampleRuleUi && !IsReviewExecutionActive);
            SetSampleShotSelectionCommand = new CButtonCommand(SetSampleShotSelection, _ => IsSimpleSampleRuleUi && !IsReviewExecutionActive);
            QuickSelectCommand = new CButtonCommand(ApplyQuickSelect);
            SelectRunCellCommand = new CButtonCommand(SelectRunCell);
            SelectRunShotCommand = new CButtonCommand(SelectRunShot, _ => CanSelectBeam);
            SelectRunBeamCommand = new CButtonCommand(SelectRunBeam, _ => CanSelectBeam);
            SelectAllInspectionBeamsCommand = new CButtonCommand(SelectAllInspectionBeams, _ => IsBeamInspectionMode && CanSelectBeam);
            ClearInspectionBeamsCommand = new CButtonCommand(ClearInspectionBeams, _ => IsBeamInspectionMode && CanSelectBeam);
            ShowSelectedInspectionShotsCommand = new CButtonCommand(ShowSelectedInspectionShots, _ => IsBeamInspectionMode);
            ClearInspectionShotsCommand = new CButtonCommand(ClearInspectionShots, _ => IsBeamInspectionMode && CanSelectBeam);
            ShowShotBeamPositionsCommand = new CButtonCommand(ShowShotBeamPositions, _ => IsBeamInspectionMode && CanSelectBeam);
            SelectInspectionMethodCommand = new CButtonCommand(SelectInspectionMethod, CanSelectInspectionMethodCommand);
            InspectionMethodItems = BuildInspectionMethodItems();
            InitializeOneShot();
            BackToRunShotMapCommand = new CButtonCommand(BackToRunShotMap);
            BackToRunGlassPreviewCommand = new CButtonCommand(BackToRunGlassPreview);
            SelectSampleCellCommand = new CButtonCommand(SelectSampleCell);
            BackToSampleGlassPreviewCommand = new CButtonCommand(BackToSampleGlassPreview);
            StartCommand = new CButtonCommand(_ => _ = RequestReviewStart(), _ => string.IsNullOrWhiteSpace(_planBuildError) && !IsOneShotReviewMode && !IsSampleSelectTab && (!IsBeamInspectionMode || (_selectedInspectionShotKeys.Count > 0 && _selectedInspectionBeamNos.Count > 0)) && !IsReviewExecutionActive && _loadedBeamInspectionResult is null && !_isBeamInspectionResultLoading);
            PreviousInspectionShotCommand = new CButtonCommand(_ => BrowseInspectionShot(-1), _ => IsInspectionExecutionViewVisible && _beamInspectionBrowseIndex > 0);
            NextInspectionShotCommand = new CButtonCommand(_ => BrowseInspectionShot(1), _ => IsInspectionExecutionViewVisible && _beamInspectionBrowseIndex + 1 < GetInspectionDisplayShotCount());
            LoadLastBeamInspectionResultCommand = new CButtonCommand(_ => _ = LoadLastBeamInspectionResult(), _ => CanLoadBeamInspectionResult);
            ClearLoadedBeamInspectionResultCommand = new CButtonCommand(_ => ClearLoadedBeamInspectionResult(), _ => CanClearLoadedBeamInspectionResult);
            StopCommand = new CButtonCommand(_ => RequestReviewStop(), _ => !IsSampleSelectTab);
            RetryCommand = new CButtonCommand(_ => _ = RetryRemainingReviewPoints(), _ => !IsSampleSelectTab && !IsBeamInspectionMode && !IsOneShotReviewMode && !IsReviewExecutionActive);
        }

#endregion
#region 기본 처리
        public override EN_MENU Menu => EN_MENU.Review;
        public string Title => "REVIEW / INSPECTION";
        public string Subtitle => "Review shot selection, vision measurement result and re-measure workflow";
        public string PlanSummary => IsSampleSelectTab ? $"{GetConfiguredSampleInspectionShotCount()} / {_totalReviewPointCount} shots" : $"{_activePlanPointCount} / {_totalReviewPointCount} shots";
        public int SampleMapColumnCount => Math.Max(1, _sampleMapColumnCount);
        public bool IsRunTab => _selectedTab.Equals("RUN", StringComparison.OrdinalIgnoreCase);
        public bool IsRunCommandsVisible => IsRunTab && !IsOneShotReviewMode;
        public bool IsRunGlassPreviewVisible => IsRunTab && !_isRunCellDetailVisible;
        public bool IsRunCellDetailVisible => IsRunTab && _isRunCellDetailVisible;
        public bool IsAllShotReviewMode => _selectedMode.Equals("ALL SHOT", StringComparison.OrdinalIgnoreCase);
        public bool IsOneShotReviewMode => _selectedMode.Equals("ONE SHOT", StringComparison.OrdinalIgnoreCase);
        public bool IsRuleReviewMode => _selectedMode.Equals("SAMPLE SHOT", StringComparison.OrdinalIgnoreCase);
        public bool IsBeamInspectionMode => _selectedMode.Equals("BEAM INSPECTION", StringComparison.OrdinalIgnoreCase);
        public bool IsBeamSelectionVisible => IsAllShotReviewMode || IsBeamInspectionMode || IsOneShotReviewMode;
        public bool IsLastMeasurementVisible => IsAllShotReviewMode;
        public bool IsRunShotMapVisible => IsRunCellDetailVisible;
        public bool IsRunBeamDetailVisible => false;
        public string RunWorkspaceTitle => _isRunCellDetailVisible ? $"Cell{_selectedRunCellNo} / Shot Selection" : "Glass / Cell Preview";
        public string RunWorkspaceSummary => _isRunCellDetailVisible ? $"{GetCellPointCount(_lastAllPlan, _selectedRunCellNo):N0} Shots" : RunGlassPreviewSummary;
        public string SelectedRunShotName => FindPointByShotKey(_lastAllPlan, _selectedRunShotKey)is { } point ? ToMatrixShotName(point) : "Shot";
        public bool IsSampleSelectTab => _selectedTab.Equals("SAMPLE SELECT", StringComparison.OrdinalIgnoreCase);
        public bool IsSimpleSampleRuleUi => _selectedSampleRuleUi == "SIMPLE";
        public bool IsRoughSampleRuleUi => _selectedSampleRuleUi == "ROUGH";
        public bool IsFineSampleRuleUi => _selectedSampleRuleUi == "FINE";
        public bool IsSampleScopeVisible => IsSimpleSampleRuleUi || IsRoughSampleRuleUi;
        public bool IsSampleScopeHead => _sampleRuleScope == "HEAD";
        public bool IsSampleScopeHeadCell => _sampleRuleScope == "HEAD_CELL";
        public bool IsRoughFirst => _roughReference == "FIRST";
        public bool IsRoughCenter => _roughReference == "CENTER";
        public bool IsRoughLast => _roughReference == "LAST";
        public bool IsFineOneLine => _fineLineCount == 1;
        public bool IsFineThreeLine => _fineLineCount == 3;
        public bool IsFineFiveLine => _fineLineCount == 5;
        public bool IsFineSevenLine => _fineLineCount == 7;
        public string SelectedSampleRuleUi => _selectedSampleRuleUi;
        public string SampleCorrectionTitle => $"{_selectedSampleRuleUi} CORRECTION";
        public string SampleTargetSectionTitle => IsSimpleSampleRuleUi ? "SELECTED REFERENCE" : "RULE TARGET";
        public string SampleReferenceThirdLabel => _selectedSampleRuleUi switch
        {
            "FINE" => "LINE",
            _ => "SHOT"
        };
        public string SampleCorrectionTarget => string.IsNullOrWhiteSpace(_simpleSampleShotKey) ? "Select one processed Shot from Cell Preview" : $"H{_sampleRuleHeadNo:00} / {(_sampleRuleScope == "HEAD_CELL" ? $"HEAD + Cell{_sampleRuleCellNo}" : "HEAD")}";
        public string SampleReferenceHead => IsSimpleSampleRuleUi ? (GetSimpleReferencePoint()is { } point ? $"H{point.HeadNo:00}" : "-") : "ALL";
        public string SampleReferenceCell => IsSimpleSampleRuleUi ? (GetSimpleReferencePoint()is { } point ? $"Cell{point.CellNo}" : "-") : IsRoughSampleRuleUi ? "HEAD RANGE" : "ALL";
        public string SampleReferenceShot => IsSimpleSampleRuleUi ? (GetSimpleReferencePoint()is { } point ? ToMatrixShotName(point) : "-") : _selectedSampleRuleUi switch
        {
            "ROUGH" => _roughReference,
            "FINE" => _fineLineCount == 1 ? "1 LINE" : $"{_fineLineCount} LINES",
            _ => "-"
        };
        public string SampleOffsetApplyCount => IsSimpleSampleRuleUi ? GetSimpleOffsetApplyCount().ToString("N0", CultureInfo.InvariantCulture) : GetAutomaticOffsetApplyCount().ToString("N0", CultureInfo.InvariantCulture);
        public string SampleInspectionShotCount => IsSimpleSampleRuleUi ? (GetSimpleReferencePoint()is null ? "0" : "1") : GetAutomaticInspectionShotCount().ToString("N0", CultureInfo.InvariantCulture);
        public string SamplePlanStatus => IsSimpleSampleRuleUi && GetSimpleReferencePoint()is null ? "Reference Shot Required" : "Ready";
        public bool IsSampleGlassPreviewVisible => IsSampleSelectTab && !_isSampleCellDetailVisible;
        public bool IsSampleCellDetailVisible => IsSampleSelectTab && _isSampleCellDetailVisible;
        public string SampleWorkspaceTitle => !_isSampleCellDetailVisible ? "Glass / Cell Preview" : IsSimpleSampleRuleUi ? $"Cell{_selectedSampleCellNo} / SIMPLE Reference Shot Selection" : $"Cell{_selectedSampleCellNo} / {_selectedSampleRuleUi} Inspection Shot Preview";
        public string SampleWorkspaceSummary => !_isSampleCellDetailVisible ? SampleGlassPreviewSummary : IsSimpleSampleRuleUi ? (GetSimpleReferencePoint()is null ? "Select one processed Shot" : "1 Reference Shot Selected") : $"{GetSelectedSampleCellShotCount():N0} / {GetSampleCellShotCount():N0} Inspection Shots (Read Only)";
        public string SampleRuleName => GetSelectionRuleText();
        public IReadOnlyList<string> SampleHeadOptions => Enumerable.Range(1, Math.Max(1, _headCount)).Select(headNo => $"H{headNo:00}").ToArray();

        public string SelectedSampleHead
        {
            get => $"H{_sampleRuleHeadNo:00}";
            set
            {
                var normalized = value?.Trim() ?? "";
                if (normalized.StartsWith('H'))
                {
                    normalized = normalized[1..];
                }

                if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var headNo))
                {
                    _sampleRuleHeadNo = Math.Clamp(headNo, 1, Math.Max(1, _headCount));
                }
            }
        }

        public IReadOnlyList<string> SampleCellOptions => Enumerable.Range(1, Math.Max(1, _cellCount)).Select(cellNo => $"Cell{cellNo}").ToArray();

        public string SelectedSampleCell
        {
            get => $"Cell{_sampleRuleCellNo}";
            set
            {
                var normalized = value?.Trim() ?? "";
                if (normalized.StartsWith("Cell", StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized[4..];
                }

                if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cellNo))
                {
                    _sampleRuleCellNo = Math.Clamp(cellNo, 1, Math.Max(1, _cellCount));
                }
            }
        }

        public string SampleRuleSelectionSummary => _selectedSampleRuleUi switch
        {
            "SIMPLE" => string.IsNullOrWhiteSpace(_simpleSampleShotKey) ? "Select 1 Shot" : SampleCorrectionTarget,
            "ROUGH" => $"Every Head / Full Head Range / {_roughReference}",
            "FINE" => $"Each Cell / {_fineLineCount} automatic line(s) per Cell",
            _ => "-"
        };
        public string SampleRuleDescription => _selectedSampleRuleUi switch
        {
            "SIMPLE" => "Measure one Shot and apply the same correction to HEAD or HEAD + CELL.",
            "ROUGH" => "Measure the first processed Shot on the FIRST, CENTER, or LAST line of each Head's full processing range without interpolation.",
            "FINE" => "Measure all processed points on 1, 3, 5, or 7 lines distributed independently within each Cell for linear correction.",
            _ => "-"
        };
        public IReadOnlyList<string> BeamOptions => Enumerable.Range(1, _splitBeamCount * _splitBeamCount).Select(beamNo => $"Beam{beamNo}").ToArray();
        public string SelectedBeam { get => $"Beam{_selectedRunBeamNo}"; set => SelectRunBeam(value); }
        public bool CanSelectBeam => !IsReviewExecutionActive && _loadedBeamInspectionResult is null && !_isBeamInspectionResultLoading;
        public bool CanStopOneShot => IsOneShotReviewMode && IsReviewExecutionActive;
        public string BeamSelectionSummary => GetLoadedInspectionShot()is { } loaded ? $"{loaded.Beams.Count(beam => beam.IsMeasured)} / 16 검사" : IsBeamInspectionMode ? $"{(IsInspectionExecutionViewVisible && _beamInspectionSnapshot is not null ? _beamInspectionSnapshot.Points.Select(p => p.BeamNo).Distinct().Count() : _selectedInspectionBeamNos.Count)} / {_splitBeamCount * _splitBeamCount} Selected" : SelectedBeam;
        public string SelectedInspectionMethod => _selectedInspectionMethod;
        public bool CanSelectInspectionMethod => !IsReviewExecutionActive;
        public IReadOnlyList<ST_REVIEW_INSPECTION_METHOD_ITEM> InspectionMethodItems { get; private set; }
        public ImageSource? RunGlassPreviewImage { get; private set; }
        public ImageSource? RunGlassPreviewInspectionOverlayImage { get; private set; }

        public IReadOnlyList<ST_REVIEW_INSPECTION_HEAD_ROW> InspectionHeadRows
        {
            get
            {
                var counts = (IsOneShotReviewMode ? new[]
                {
                    _selectedRunShotKey
                }

                : _selectedInspectionShotKeys.AsEnumerable()).Select(key => FindPointByShotKey(_lastAllPlan, key)).OfType<ST_REVIEW_PLAN_POINT>().GroupBy(point => point.HeadNo).ToDictionary(group => group.Key, group => group.Count());
                return Enumerable.Range(1, _headCount).Select(headNo => new ST_REVIEW_INSPECTION_HEAD_ROW(headNo, counts.GetValueOrDefault(headNo))).ToArray();
            }
        }

        public ImageSource? SampleGlassPreviewImage { get; private set; }
        public ImageSource? SampleGlassPreviewInspectionOverlayImage { get; private set; }
        public CButtonCommand SelectTabCommand { get; }
        public CButtonCommand SelectModeCommand { get; }
        public CButtonCommand SelectSampleRuleUiCommand { get; }
        public CButtonCommand SelectSampleRuleScopeCommand { get; }
        public CButtonCommand SelectRoughReferenceCommand { get; }
        public CButtonCommand SelectFineLineCountCommand { get; }
        public CButtonCommand TogglePointCommand { get; }
        public CButtonCommand SetSampleShotSelectionCommand { get; }
        public CButtonCommand QuickSelectCommand { get; }
        public CButtonCommand SelectRunCellCommand { get; }
        public CButtonCommand OpenCorrectionReviewCommand { get; }
        public CButtonCommand SelectRunShotCommand { get; }
        public CButtonCommand SelectRunBeamCommand { get; }
        public CButtonCommand SelectAllInspectionBeamsCommand { get; }
        public CButtonCommand ClearInspectionBeamsCommand { get; }
        public CButtonCommand ShowSelectedInspectionShotsCommand { get; }
        public CButtonCommand ClearInspectionShotsCommand { get; }
        public CButtonCommand ShowShotBeamPositionsCommand { get; }
        public CButtonCommand SelectInspectionMethodCommand { get; }
        public CButtonCommand BackToRunShotMapCommand { get; }
        public CButtonCommand BackToRunGlassPreviewCommand { get; }
        public CButtonCommand SelectSampleCellCommand { get; }
        public CButtonCommand BackToSampleGlassPreviewCommand { get; }
        public CButtonCommand StartCommand { get; }
        public CButtonCommand StopCommand { get; }
        public CButtonCommand RetryCommand { get; }

        public void ResetForMenuOpen()
        {
            if (_reviewManager.SequenceState is EN_REVIEW_SEQUENCE_STATE.Running or EN_REVIEW_SEQUENCE_STATE.Stopping)
            {
                return;
            }

            _isRunCellDetailVisible = false;
            _isSampleCellDetailVisible = false;
        }

        public override async Task<CScreenViewModel> Build(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var recipe = await LoadSelectedRecipe(cancellationToken) ?? CreateFallbackRecipe();
                if (!ReferenceEquals(recipe, _lastRecipe))
                {
                    _lastAllPlan = null;
                    _lastAllPlanBeamNo = 0;
                }

                _lastRecipe = recipe;
                ApplySplitBeamContext(recipe);
                var allPlan = _lastAllPlan is not null && _lastAllPlanBeamNo == _selectedRunBeamNo ? _lastAllPlan : _reviewManager.CreateAllPlan(recipe, _selectedRunBeamNo);
                _planBuildError = "";
                _lastAllPlan = allPlan;
                _lastAllPlanBeamNo = _selectedRunBeamNo;
                RestoreSimpleShotSelection(allPlan);
                ApplyRecipeContext(recipe, allPlan);
                ApplyScreenData(recipe, allPlan);
                StartCommand.NotifyCanExecuteChanged();
                return CreateReviewScreen("Ready");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _planBuildError = exception.Message;
                ResetPlanDisplayAfterBuildFailure();
                StartCommand.NotifyCanExecuteChanged();
                _statusReporter($"Review plan cannot be created: {_planBuildError}");
                return CreateReviewScreen("Configuration Error");
            }
        }

        private CScreenViewModel CreateReviewScreen(string result)
        {
            return new CScreenViewModel(
                EN_MENU.Review, 
                Title, 
                Subtitle, 
                [
                new("Mode", ToDisplayModeName(_selectedMode)), 
                new("Shot", IsSampleSelectTab ? $"{GetConfiguredSampleInspectionShotCount()} / {_totalReviewPointCount}" : $"{_activePlanPointCount} / {_totalReviewPointCount}"), 
                new("Result", result)
            ], 
                [new("Review Plan", []), new("Review Result", [])], 
                review: this
            );
        }

        private void ResetPlanDisplayAfterBuildFailure()
        {
            _lastAllPlan = null;
            _activePlanPointCount = 0;
            _totalReviewPointCount = 0;
            Tabs = [new("RUN", IsRunTab, SelectTabCommand)];
            Modes = [Mode("ALL SHOT", ""), Mode("ONE SHOT", ""), Mode("BEAM INSPECTION", "")];
            TargetItems = [new("Recipe Name", _selectedRecipeIdProvider()), new("Glass ID", _productManager.Current?.GlassId ?? "-"), new("Tolerance", "-")];
            SelectionSummaryItems = [new("Status", "Configuration Error"), new("Reason", _planBuildError)];
            SelectedPointItems = [];
            PointSelectRows = [];
            RunCellShotRows = [];
            RunCellShotMatrixRows = [];
            RunBeamRows = [];
            SampleCellShotRows = [];
            SampleCellShotMatrixRows = [];
            RunGlassPreviewImage = null;
            _cameraGlassBounds = Rect.Empty;
            Camera.Clear();
            RunGlassPreviewInspectionOverlayImage = null;
            RunCellPreviewLabels = [];
            RunGlassPreviewSummary = "Review plan unavailable";
            SampleGlassPreviewImage = null;
            SampleGlassPreviewInspectionOverlayImage = null;
            SampleCellPreviewLabels = [];
            SampleGlassPreviewSummary = "Review plan unavailable";
        }

        private ST_RECIPE_DEFINITION CreateFallbackRecipe()
        {
            return new ST_RECIPE_DEFINITION(_recipeId, _recipeId, [], []);
        }

        private async Task<ST_RECIPE_DEFINITION?> LoadSelectedRecipe(CancellationToken cancellationToken)
        {
            var recipes = await _recipeManager.LoadRecipes(cancellationToken);
            if (recipes.Count == 0)
            {
                return null;
            }

            var selectedRecipeId = _selectedRecipeIdProvider();
            if (!string.IsNullOrWhiteSpace(selectedRecipeId))
            {
                var selectedRecipe = recipes.FirstOrDefault(recipe => recipe.Id.Equals(selectedRecipeId, StringComparison.OrdinalIgnoreCase));
                if (selectedRecipe is not null)
                {
                    return selectedRecipe;
                }
            }

            return recipes.FirstOrDefault(recipe => recipe.Id.Equals("DRILL_A01", StringComparison.OrdinalIgnoreCase)) ?? recipes[0];
        }

        private void ApplyRecipeContext(ST_RECIPE_DEFINITION recipe, ST_REVIEW_PLAN allPlan)
        {
            _headCount = MaxHeadCount;
            _sampleRuleHeadNo = Math.Clamp(_sampleRuleHeadNo, 1, _headCount);
            _cellCount = Math.Max(1, allPlan.CellCount);
            _sampleRuleCellNo = Math.Clamp(_sampleRuleCellNo, 1, _cellCount);
            _recipeId = string.IsNullOrWhiteSpace(recipe.Id) ? "DRILL_A01" : recipe.Id;
            var isSampleSelectionRecipeChanged = !_sampleSelectionRecipeId.Equals(_recipeId, StringComparison.OrdinalIgnoreCase);
            _totalReviewPointCount = allPlan.Points.Count;
            ApplySplitBeamContext(recipe);
            bool IsValidShotKey(string key)
            {
                var normalized = CReviewManager.NormalizeShotKey(key);
                var separator = normalized.IndexOf("_SHOT", StringComparison.Ordinal);
                return separator > 4 && int.TryParse(normalized.AsSpan(4, separator - 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cellNo) && int.TryParse(normalized.AsSpan(separator + 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var shotNo) && cellNo >= 1 && cellNo <= allPlan.CellCount && shotNo >= 1 && allPlan.FindPoint(cellNo, shotNo)is not null;
            }

            if (isSampleSelectionRecipeChanged)
            {
                _oneShotResult = null;
                if (IsOneShotReviewMode)
                {
                    _selectedRunShotKey = "";
                    OneShotStatus = "Shot을 선택해 주세요.";
                    NotifyOneShot();
                }

                ResetBeamInspectionResults();
                _selectedSampleShotKeys.Clear();
                _selectedInspectionShotKeys.Clear();
                _simpleSampleShotKey = "";
                _lastCorrectionRulePlan = null;
                _sampleSelectionRecipeId = _recipeId;
            }
            else
            {
                _selectedSampleShotKeys.RemoveWhere(key => !IsValidShotKey(key));
                _selectedInspectionShotKeys.RemoveWhere(key => !IsValidShotKey(key));
            }

            _selectedSampleCellNo = Math.Clamp(_selectedSampleCellNo, 1, _cellCount);
            SetSampleMapColumnCount(CalculateShotMapColumnCount(allPlan, _selectedSampleCellNo));
            _selectedRunCellNo = Math.Clamp(_selectedRunCellNo, 1, _cellCount);
            if (!string.IsNullOrWhiteSpace(_selectedRunShotKey) && !IsValidShotKey(_selectedRunShotKey))
            {
                _selectedRunShotKey = "";
            }
        }

        private static string ReadRecipeString(ST_RECIPE_DEFINITION? recipe, params string[] keys)
        {
            if (recipe is null)
            {
                return "";
            }

            foreach (var key in keys)
            {
                var parameter = recipe.Parameters.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || item.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (parameter is not null && !string.IsNullOrWhiteSpace(parameter.Value))
                {
                    return parameter.Value.Trim();
                }
            }

            return "";
        }

        private static int ReadRecipeInt(ST_RECIPE_DEFINITION recipe, int fallback, params string[] keys) => int.TryParse(ReadRecipeString(recipe, keys), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        private void ApplySplitBeamContext(ST_RECIPE_DEFINITION recipe)
        {
            _splitBeamCount = ReadRecipeInt(recipe, 1, "SPLITED_BEAM_COUNT");
            if (!CCellPatternCalculator.IsSupportedSplitBeamCount(_splitBeamCount))
            {
                _splitBeamCount = 1;
            }

            var maximumBeamNo = _splitBeamCount * _splitBeamCount;
            _selectedRunBeamNo = Math.Clamp(_selectedRunBeamNo, 1, maximumBeamNo);
            _selectedInspectionBeamNos.RemoveWhere(beamNo => beamNo < 1 || beamNo > maximumBeamNo);
        }

        private void SelectTab(object? parameter)
        {
            if (parameter is not string tab || string.IsNullOrWhiteSpace(tab))
            {
                return;
            }

            if (tab != "RUN")
            {
                return;
            }

            if (tab.Equals(_selectedTab, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (IsReviewExecutionActive)
            {
                _statusReporter("Review is running. Stop the current review before changing the tab.");
                CRecipeConfirmDialog.ShowWarning("검사 진행 중", "검사를 중지한 후 탭을 변경해 주세요.");
                return;
            }

            InvalidateBeamInspectionResultLoad(clearLoaded: true);
            _selectedTab = tab;
            if (IsSampleSelectTab)
            {
                _isSampleCellDetailVisible = false;
                _selectedMode = "SAMPLE SHOT";
                _selectionRuleText = _selectedSampleRuleUi;
            }

            NotifyReviewExecutionCommandStates();
            _statusReporter($"Review tab selected: {_selectedTab}");
            _refreshScreen();
        }

        private void SelectSampleRuleUi(object? parameter)
        {
            var rule = parameter?.ToString()?.Trim().ToUpperInvariant();
            if (rule is not ("ROUGH" or "FINE") || rule == _selectedSampleRuleUi)
                return;
            _selectedSampleRuleUi = rule;
            _lastCorrectionRulePlan = null;
            _isSampleCellDetailVisible = false;
            _selectedSampleShotKeys.Clear();
            _selectedMode = "SAMPLE SHOT";
            _selectionRuleText = rule;
            _statusReporter($"Review correction rule selected: {rule}");
            RefreshSampleRuleUi();
        }

        private void SelectSampleRuleScope(object? parameter)
        {
            var scope = parameter?.ToString()?.Trim().ToUpperInvariant();
            if (scope is not ("HEAD" or "HEAD_CELL") || scope == _sampleRuleScope)
                return;
            _sampleRuleScope = scope;
            _lastCorrectionRulePlan = null;
            RefreshSampleRuleUi();
        }

        private void SelectRoughReference(object? parameter)
        {
            var reference = parameter?.ToString()?.Trim().ToUpperInvariant();
            if (reference is not ("FIRST" or "CENTER" or "LAST") || reference == _roughReference)
                return;
            _roughReference = reference;
            _lastCorrectionRulePlan = null;
            RefreshSampleRuleUi();
        }

        private void SelectFineLineCount(object? parameter)
        {
            if (!int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count is not (1 or 3 or 5 or 7) || count == _fineLineCount)
                return;
            _fineLineCount = count;
            _lastCorrectionRulePlan = null;
            RefreshSampleRuleUi();
        }

        private void SelectMode(object? parameter)
        {
            if (IsReviewExecutionActive)
            {
                _statusReporter("Review is running. Stop the current review before changing the mode.");
                return;
            }

            if (parameter is not string mode || string.IsNullOrWhiteSpace(mode))
            {
                return;
            }

            mode = mode.Trim().ToUpperInvariant();
            if (mode is not ("ALL SHOT" or "ONE SHOT" or "BEAM INSPECTION"))
                return;
            InvalidateBeamInspectionResultLoad(clearLoaded: true);
            _selectedMode = mode;
            _selectedRuleType = mode.Trim().ToUpperInvariant() switch
            {
                "ALL SHOT" => EN_REVIEW_RULE_TYPE.AllPoint,
                "ZERO DEFENSE" => EN_REVIEW_RULE_TYPE.ZeroLine,
                _ => EN_REVIEW_RULE_TYPE.SamplePoint
            };
            _selectionRuleText = _selectedRuleType switch
            {
                EN_REVIEW_RULE_TYPE.AllPoint => "All Shot",
                EN_REVIEW_RULE_TYPE.ZeroLine => "0-Line",
                _ => _selectionRuleText is "All Shot" or "0-Line" ? "Manual Sample" : _selectionRuleText
            };
            _isRunCellDetailVisible = false;
            _selectedRunShotKey = "";
            RefreshOneShotSelection(clearResult: true);
            OnPropertyChanged(nameof(IsShotSelectionMode));
            OnPropertyChanged(nameof(InspectionHeadRows));
            _statusReporter($"Review mode selected: {_selectedMode}");
            OnPropertyChanged(nameof(IsAllShotReviewMode));
            OnPropertyChanged(nameof(IsOneShotReviewMode));
            OnPropertyChanged(nameof(IsRunCommandsVisible));
            OnPropertyChanged(nameof(IsRuleReviewMode));
            OnPropertyChanged(nameof(IsBeamInspectionMode));
            OnPropertyChanged(nameof(IsBeamSelectionVisible));
            OnPropertyChanged(nameof(IsLastMeasurementVisible));
            OnPropertyChanged(nameof(BeamSelectionSummary));
            NotifyReviewExecutionCommandStates();
            RefreshRunDisplayFromCachedContext();
        }

        private void ToggleShot(object? parameter)
        {
            if (!IsSimpleSampleRuleUi || IsReviewExecutionActive)
            {
                _statusReporter($"{_selectedSampleRuleUi} Shot selection is determined automatically by its rule settings.");
                return;
            }

            var shotKey = CReviewManager.NormalizeShotKey(parameter?.ToString() ?? "");
            if (string.IsNullOrWhiteSpace(shotKey))
            {
                return;
            }

            var point = FindPointByShotKey(_lastAllPlan, shotKey);
            if (point is null)
            {
                return;
            }

            if (point.HeadNo <= 0)
            {
                _selectedSampleShotKeys.Remove(point.ShotKey);
                _statusReporter($"CELL{point.CellNo:00} / {ToMatrixShotName(point)} cannot be selected because it is not assigned to a processing Head.");
                RefreshSampleSelectionDisplay([point.ShotKey]);
                return;
            }

            if (!point.Use)
            {
                _selectedSampleShotKeys.Remove(point.ShotKey);
                _statusReporter($"CELL{point.CellNo:00} / {ToMatrixShotName(point)} is excluded from Review by Recipe Masking/Chess settings.");
                RefreshSampleSelectionDisplay([point.ShotKey]);
                return;
            }

            var previousSimpleKey = _simpleSampleShotKey;
            _selectedSampleShotKeys.Clear();
            _selectedSampleShotKeys.Add(shotKey);
            _simpleSampleShotKey = shotKey;
            _lastCorrectionRulePlan = null;
            _sampleRuleHeadNo = point.HeadNo;
            _sampleRuleCellNo = point.CellNo;
            _selectedMode = "SAMPLE SHOT";
            _selectedRuleType = EN_REVIEW_RULE_TYPE.SamplePoint;
            _selectionRuleText = _selectedSampleRuleUi;
            _statusReporter($"Review shot selection updated: {_selectedSampleShotKeys.Count} shots.");
            RefreshSampleSelectionDisplay(string.IsNullOrWhiteSpace(previousSimpleKey) ? [shotKey] : [previousSimpleKey, shotKey]);
        }

        private void SetSampleShotSelection(object? parameter)
        {
            if (!IsSimpleSampleRuleUi || IsReviewExecutionActive)
            {
                return;
            }

            if (parameter is not ST_REVIEW_SAMPLE_DRAG_SELECTION selection || selection.ShotKeys.Count == 0)
            {
                return;
            }

            ToggleShot(selection.ShotKeys[^1]);
        }

        private void RefreshSampleSelectionDisplay(IEnumerable<string> changedShotKeys)
        {
            var changedKeys = changedShotKeys.Select(CReviewManager.NormalizeShotKey).Where(key => !string.IsNullOrWhiteSpace(key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in changedKeys)
            {
                if (!_sampleShotRowsByKey.TryGetValue(key, out var row))
                {
                    continue;
                }

                if (row.IsExcluded)
                {
                    row.SetSelection(false, row.ExclusionReason);
                    continue;
                }

                var use = _selectedMode.Equals("ALL SHOT", StringComparison.OrdinalIgnoreCase) || _selectedSampleShotKeys.Contains(row.ShotKey);
                row.SetSelection(use, use ? (IsSimpleSampleRuleUi ? "REFERENCE" : "SAMPLE") : "-");
            }

            var selectedShotCount = GetSelectedSampleShotCount();
            if (IsSampleSelectTab)
            {
                SelectionSummaryItems = CreateSamplePlanSummaryItems();
            }
            else
            {
                SelectionSummaryItems = SelectionSummaryItems.Select(item => item.Name.Equals("Selected", StringComparison.OrdinalIgnoreCase) ? item with { Value = selectedShotCount.ToString(CultureInfo.InvariantCulture) } : item).ToArray();
            }

            SampleGlassPreviewSummary = BuildSampleGlassPreviewSummary();
            OnPropertyChanged(nameof(SelectionSummaryItems));
            OnPropertyChanged(nameof(SampleGlassPreviewSummary));
            OnPropertyChanged(nameof(PlanSummary));
            OnPropertyChanged(nameof(SampleWorkspaceSummary));
            OnPropertyChanged(nameof(SampleRuleSelectionSummary));
            OnPropertyChanged(nameof(SampleReferenceHead));
            OnPropertyChanged(nameof(SampleReferenceCell));
            OnPropertyChanged(nameof(SampleReferenceShot));
            OnPropertyChanged(nameof(SampleOffsetApplyCount));
            OnPropertyChanged(nameof(SampleInspectionShotCount));
            OnPropertyChanged(nameof(SamplePlanStatus));
            // The glass preview is hidden while a Cell shot map is open. Rebuilding
            // that off-screen DrawingGroup on every shot click was the largest part
            // of the selection delay. BackToSampleGlassPreview refreshes it before
            // it becomes visible again.
            if (IsSampleSelectTab && !_isSampleCellDetailVisible)
            {
                RefreshSampleGlassPreview();
            }
        }

        // Sample rule changes only affect the current sample workspace.  Rebuilding
        // the root screen here used to recreate the complete Review view (including
        // the large Cell preview) for every rule/scope click.  Keep the existing
        // visual tree and notify the bindings that changed instead.
        private void RefreshSampleRuleUi()
        {
            if (!IsSampleSelectTab)
            {
                _refreshScreen();
                return;
            }

            if (_isSampleCellDetailVisible)
            {
                RefreshSampleCellDetailRows();
            }
            else
            {
                RefreshSampleGlassPreview();
            }

            OnPropertyChanged(nameof(PlanSummary));
            OnPropertyChanged(nameof(IsSimpleSampleRuleUi));
            OnPropertyChanged(nameof(IsRoughSampleRuleUi));
            OnPropertyChanged(nameof(IsFineSampleRuleUi));
            OnPropertyChanged(nameof(IsSampleScopeVisible));
            OnPropertyChanged(nameof(IsSampleScopeHead));
            OnPropertyChanged(nameof(IsSampleScopeHeadCell));
            OnPropertyChanged(nameof(IsRoughFirst));
            OnPropertyChanged(nameof(IsRoughCenter));
            OnPropertyChanged(nameof(IsRoughLast));
            OnPropertyChanged(nameof(IsFineOneLine));
            OnPropertyChanged(nameof(IsFineThreeLine));
            OnPropertyChanged(nameof(IsFineFiveLine));
            OnPropertyChanged(nameof(IsFineSevenLine));
            OnPropertyChanged(nameof(SelectedSampleRuleUi));
            OnPropertyChanged(nameof(SampleCorrectionTitle));
            OnPropertyChanged(nameof(SampleTargetSectionTitle));
            OnPropertyChanged(nameof(SampleReferenceThirdLabel));
            OnPropertyChanged(nameof(SampleCorrectionTarget));
            OnPropertyChanged(nameof(SampleRuleSelectionSummary));
            OnPropertyChanged(nameof(SampleInspectionShotCount));
            OnPropertyChanged(nameof(SampleOffsetApplyCount));
            OnPropertyChanged(nameof(IsSampleGlassPreviewVisible));
            OnPropertyChanged(nameof(IsSampleCellDetailVisible));
            OnPropertyChanged(nameof(SampleWorkspaceTitle));
            OnPropertyChanged(nameof(SampleWorkspaceSummary));
            TogglePointCommand.NotifyCanExecuteChanged();
            SetSampleShotSelectionCommand.NotifyCanExecuteChanged();
        }

        private void ApplyQuickSelect(object? parameter)
        {
            var allPlan = _lastAllPlan;
            var rule = parameter?.ToString()?.Trim().ToUpperInvariant() ?? "";
            if (allPlan is null || string.IsNullOrWhiteSpace(rule))
            {
                return;
            }

            HashSet<string>? cell1ShotNames = null;
            ST_REVIEW_PLAN_POINT? cell1ReferencePoint = null;
            if (rule == "COPY_CELL1")
            {
                cell1ReferencePoint = allPlan.Points.FirstOrDefault(point => point.CellNo == 1);
                cell1ShotNames = allPlan.Points.Where(point => point.CellNo == 1 && _selectedSampleShotKeys.Contains(point.ShotKey)).Select(ToMatrixShotName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (cell1ReferencePoint is null || cell1ShotNames.Count == 0)
                {
                    _statusReporter("Select at least one shot in CELL01 before copying the pattern.");
                    return;
                }
            }

            _selectedSampleShotKeys.Clear();
            switch (rule)
            {
                case "ALL":
                    _selectedMode = "ALL SHOT";
                    _selectedRuleType = EN_REVIEW_RULE_TYPE.AllPoint;
                    _selectionRuleText = "All Shot";
                    break;
                case "CLEAR":
                    _selectedMode = "SAMPLE SHOT";
                    _selectedRuleType = EN_REVIEW_RULE_TYPE.SamplePoint;
                    _selectionRuleText = "None";
                    break;
                case "EDGE":
                    foreach (var key in SelectEdgeKeys(allPlan))
                    {
                        _selectedSampleShotKeys.Add(key);
                    }

                    _selectedMode = "SAMPLE SHOT";
                    _selectedRuleType = EN_REVIEW_RULE_TYPE.Edge;
                    _selectionRuleText = "Edge";
                    break;
                case "CENTER":
                    foreach (var key in SelectCenterKeys(allPlan))
                    {
                        _selectedSampleShotKeys.Add(key);
                    }

                    _selectedMode = "SAMPLE SHOT";
                    _selectedRuleType = EN_REVIEW_RULE_TYPE.Center;
                    _selectionRuleText = "Center";
                    break;
                case "ONE_LINE":
                    foreach (var key in allPlan.Points.Where(point => point.HeadNo > 0 && GetShotRow(point) == GetCenterRow(point.PixelCountY)).Select(point => point.ShotKey))
                    {
                        _selectedSampleShotKeys.Add(key);
                    }

                    _selectedMode = "SAMPLE SHOT";
                    _selectedRuleType = EN_REVIEW_RULE_TYPE.SamplePoint;
                    _selectionRuleText = "1-Line";
                    break;
                case "THREE_LINE":
                    foreach (var key in allPlan.Points.Where(point =>
                    {
                        if (point.HeadNo <= 0)
                        {
                            return false;
                        }

                        var row = GetShotRow(point);
                        return row == 1 || row == GetCenterRow(point.PixelCountY) || row == point.PixelCountY;
                    }).Select(point => point.ShotKey))
                    {
                        _selectedSampleShotKeys.Add(key);
                    }

                    _selectedMode = "SAMPLE SHOT";
                    _selectedRuleType = EN_REVIEW_RULE_TYPE.SamplePoint;
                    _selectionRuleText = "3-Line";
                    break;
                case "HEAD":
                    foreach (var key in allPlan.Points.Where(point => point.HeadNo == _sampleRuleHeadNo).Select(point => point.ShotKey))
                    {
                        _selectedSampleShotKeys.Add(key);
                    }

                    _selectedMode = "SAMPLE SHOT";
                    _selectedRuleType = EN_REVIEW_RULE_TYPE.HeadPoint;
                    _selectionRuleText = $"Head Shot H{_sampleRuleHeadNo:00}";
                    break;
                case "CELL":
                    _selectedSampleCellNo = _sampleRuleCellNo;
                    foreach (var key in allPlan.Points.Where(point => point.HeadNo > 0 && point.CellNo == _sampleRuleCellNo).Select(point => point.ShotKey))
                    {
                        _selectedSampleShotKeys.Add(key);
                    }

                    _selectedMode = "SAMPLE SHOT";
                    _selectedRuleType = EN_REVIEW_RULE_TYPE.CellPoint;
                    _selectionRuleText = $"Cell Shot CELL{_sampleRuleCellNo:00}";
                    break;
                case "ZERO":
                    foreach (var key in SelectZeroLineKeys(allPlan, 0))
                    {
                        _selectedSampleShotKeys.Add(key);
                    }

                    _selectedMode = "ZERO DEFENSE";
                    _selectedRuleType = EN_REVIEW_RULE_TYPE.ZeroLine;
                    _selectionRuleText = "0-Line";
                    break;
                case "COPY_CELL1":
                    foreach (var key in allPlan.Points.Where(point => point.HeadNo > 0 && point.PixelCountX == cell1ReferencePoint!.PixelCountX && point.PixelCountY == cell1ReferencePoint.PixelCountY && cell1ShotNames!.Contains(ToMatrixShotName(point))).Select(point => point.ShotKey))
                    {
                        _selectedSampleShotKeys.Add(key);
                    }

                    _selectedSampleCellNo = 1;
                    _sampleRuleCellNo = 1;
                    _selectedMode = "SAMPLE SHOT";
                    _selectedRuleType = EN_REVIEW_RULE_TYPE.SamplePoint;
                    _selectionRuleText = "CELL01 Pattern";
                    break;
                default:
                    foreach (var key in CreateDefaultSampleShotKeys(allPlan))
                    {
                        if (FindPointByShotKey(allPlan, key)?.HeadNo > 0)
                        {
                            _selectedSampleShotKeys.Add(key);
                        }
                    }

                    _selectedMode = "SAMPLE SHOT";
                    _selectedRuleType = EN_REVIEW_RULE_TYPE.SamplePoint;
                    _selectionRuleText = "Default Sample";
                    break;
            }

            _selectedSampleShotKeys.RemoveWhere(key => FindPointByShotKey(allPlan, key)?.Use != true);
            if (rule == "COPY_CELL1")
            {
                var appliedCellCount = allPlan.Points.Where(point => _selectedSampleShotKeys.Contains(point.ShotKey)).Select(point => point.CellNo).Distinct().Count();
                var skippedCellCount = Math.Max(0, allPlan.CellCount - appliedCellCount);
                _statusReporter($"CELL01 {cell1ReferencePoint!.PixelCountX}x{cell1ReferencePoint.PixelCountY} pattern copied: " + $"{appliedCellCount} cells applied, {skippedCellCount} cells skipped, " + $"{_selectedSampleShotKeys.Count} shots selected.");
            }
            else
            {
                _statusReporter($"Review shot rule applied: {rule}, {GetSelectedSampleShotCount()} shots.");
            }

            RefreshSampleSelectionDisplay(_sampleShotRowsByKey.Keys.ToArray());
        }

        private void RefreshSampleGlassPreview()
        {
            if (_lastRecipe is null || _lastAllPlan is null)
            {
                return;
            }

            var simpleReferencePoint = IsSimpleSampleRuleUi ? GetSimpleReferencePoint() : null;
            var preview = CReviewGlassPreviewBuilder.Build(
                _lastRecipe, 
                _lastAllPlan.CellCount, 
                _isSampleCellDetailVisible ? _selectedSampleCellNo : simpleReferencePoint?.CellNo ?? 0, 
                useSampleSelectionColors: true, 
                selectedShotKeys: _selectedSampleShotKeys, 
                selectAllShots: false, 
                showShotPoints: false, 
                inspectionPoints: GetAutomaticInspectionPreviewPoints(_lastAllPlan), 
                inspectionRule: GetAutomaticInspectionPreviewRule(), 
                scopeTargets: GetRoughScopeTargets(_lastAllPlan), 
                scopePoints: ShouldDrawSampleScope() ? _lastAllPlan.Points : null, 
                scopeHeadNo: simpleReferencePoint?.HeadNo ?? 0, 
                scopeCellNo: IsSimpleSampleRuleUi && _sampleRuleScope == "HEAD_CELL" ? simpleReferencePoint?.CellNo ?? 0 : 0, 
                headScopes: ShouldDrawSampleScope() ? _reviewManager.GetHeadScopes(_lastRecipe) : null, 
                currentHolePoint: IsSimpleSampleRuleUi ? GetSimpleReferencePoint() : null, 
                expandSmallCellBounds: false
            , settings: _cameraSettings?.LoadSection(EN_SETTING_TAB.Motor));
            SampleGlassPreviewImage = preview.Image;
            SampleGlassPreviewInspectionOverlayImage = preview.InspectionOverlayImage;
            SampleCellPreviewLabels = preview.CellLabels;
            SampleGlassPreviewSummary = BuildSampleGlassPreviewSummary();
            OnPropertyChanged(nameof(SampleGlassPreviewImage));
            OnPropertyChanged(nameof(SampleGlassPreviewInspectionOverlayImage));
            OnPropertyChanged(nameof(SampleCellPreviewLabels));
            OnPropertyChanged(nameof(SampleGlassPreviewSummary));
            OnPropertyChanged(nameof(SampleWorkspaceSummary));
            OnPropertyChanged(nameof(SampleRuleName));
            OnPropertyChanged(nameof(SampleRuleDescription));
            OnPropertyChanged(nameof(SampleCorrectionTarget));
            OnPropertyChanged(nameof(SampleReferenceHead));
            OnPropertyChanged(nameof(SampleReferenceCell));
            OnPropertyChanged(nameof(SampleReferenceShot));
            OnPropertyChanged(nameof(SampleOffsetApplyCount));
            OnPropertyChanged(nameof(SampleInspectionShotCount));
            OnPropertyChanged(nameof(SamplePlanStatus));
        }

        private void SelectRunCell(object? parameter)
        {
            if (IsOneShotReviewMode && IsReviewExecutionActive)
                return;
            if (!int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cellNo))
            {
                return;
            }

            _selectedRunCellNo = Math.Clamp(cellNo, 1, _cellCount);
            _selectedRunShotKey = "";
            _isRunCellDetailVisible = true;
            _statusReporter($"Review Cell selected: Cell{_selectedRunCellNo}");
            RefreshRunCellDetail();
            RefreshOneShotSelection(clearResult: true);
        }

        private void SelectRunShot(object? parameter)
        {
            if (!CanSelectBeam)
            {
                _statusReporter("Shot selection is locked while Review is running. Last Measurement follows the last completed Shot.");
                return;
            }

            var shotKey = CReviewManager.NormalizeShotKey(parameter?.ToString() ?? "");
            if (IsOneShotReviewMode)
            {
                var selected = FindPointByShotKey(_lastAllPlan, shotKey);
                if (selected is not { Use: true, HeadNo: > 0 } || IsReviewPointExcluded(selected))
                    return;
                if (_runShotRowsByKey.TryGetValue(_selectedRunShotKey, out var previous))
                    previous.SetSelected(false);
                _selectedRunShotKey = selected.ShotKey;
                _selectedRunCellNo = selected.CellNo;
                if (_runShotRowsByKey.TryGetValue(selected.ShotKey, out var row))
                    row.SetSelected(true);
                RefreshOneShotSelection(clearResult: true);
                RefreshInspectionCellSelection();
                OnPropertyChanged(nameof(SelectedRunShotName));
                return;
            }

            var displayPlan = _lastRecipe is not null && _lastAllPlan is not null ? ResolveReviewPlan(_lastRecipe, _lastAllPlan) : _reviewManager.CurrentPlan ?? _lastAllPlan;
            var runtimePoint = FindPointByShotKey(_reviewManager.CurrentPlan, shotKey);
            var point = runtimePoint is not null && _runShotRowsByKey.TryGetValue(shotKey, out var visibleRow) && visibleRow.State.Equals(ToRunShotState(runtimePoint), StringComparison.OrdinalIgnoreCase) && visibleRow.Detail.Equals(ToRunShotDetail(runtimePoint), StringComparison.Ordinal) ? runtimePoint : FindPointByShotKey(displayPlan, shotKey) ?? FindPointByShotKey(_lastAllPlan, shotKey);
            if (point is null)
            {
                return;
            }

            var recipePoint = FindPointByShotKey(_lastAllPlan, point.ShotKey) ?? point;
            if (IsReviewPointExcluded(recipePoint))
            {
                _selectedInspectionShotKeys.Remove(point.ShotKey);
                if (point.ShotKey.Equals(_selectedRunShotKey, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedRunShotKey = "";
                }

                if (_runShotRowsByKey.TryGetValue(point.ShotKey, out var excludedRow))
                {
                    excludedRow.SetSelected(false);
                }

                RefreshInspectionCellSelection();
                OnPropertyChanged(nameof(SelectedRunShotName));
                _statusReporter($"Cell{point.CellNo} / {ToMatrixShotName(point)}: {recipePoint.ExclusionReason} 제외 Shot은 선택할 수 없습니다.");
                return;
            }

            if (IsBeamInspectionMode)
            {
                var isSelected = _selectedInspectionShotKeys.Add(point.ShotKey);
                if (!isSelected)
                {
                    _selectedInspectionShotKeys.Remove(point.ShotKey);
                }

                _selectedRunCellNo = point.CellNo;
                _isRunCellDetailVisible = true;
                if (isSelected || point.ShotKey.Equals(_selectedRunShotKey, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedRunShotKey = isSelected ? point.ShotKey : "";
                }

                if (_runShotRowsByKey.TryGetValue(point.ShotKey, out var inspectionRow))
                {
                    inspectionRow.SetSelected(isSelected);
                }

                ResetBeamInspectionResults();
                RefreshInspectionCellSelection();
                OnPropertyChanged(nameof(SelectedRunShotName));
                _statusReporter($"Beam inspection Shot {(isSelected ? "selected" : "deselected")}: Cell{point.CellNo} / {ToMatrixShotName(point)} ({_selectedInspectionShotKeys.Count} selected)");
                return;
            }

            var previousShotKey = _selectedRunShotKey;
            _selectedRunCellNo = point.CellNo;
            _selectedRunShotKey = point.ShotKey;
            _isRunCellDetailVisible = true;
            _statusReporter(IsReviewPointExcluded(point) ? $"Review Shot excluded: Cell{point.CellNo} / {ToMatrixShotName(point)} / {point.ExclusionReason}" : $"Review Shot selected: Cell{point.CellNo} / {ToMatrixShotName(point)} / Beam{_selectedRunBeamNo}");
            if (_runShotRowsByKey.TryGetValue(previousShotKey, out var previousRow))
            {
                previousRow.SetSelected(false);
            }

            if (_runShotRowsByKey.TryGetValue(point.ShotKey, out var selectedRow))
            {
                selectedRow.SetSelected(true);
            }

            SelectedPointItems = BuildSelectedPointItems(point);
            OnPropertyChanged(nameof(SelectedRunShotName));
            OnPropertyChanged(nameof(SelectedPointItems));
        }

        private void SelectRunBeam(object? parameter)
        {
            if (!CanSelectBeam)
            {
                _statusReporter("Beam cannot be changed while Review is running. Stop the Review before selecting another Beam.");
                return;
            }

            var beamText = parameter?.ToString()?.Trim() ?? "";
            if (beamText.StartsWith("Beam", StringComparison.OrdinalIgnoreCase))
            {
                beamText = beamText[4..];
            }

            if (!int.TryParse(beamText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var beamNo) || beamNo < 1 || beamNo > _splitBeamCount * _splitBeamCount)
            {
                return;
            }

            if (IsBeamInspectionMode)
            {
                if (!_selectedInspectionBeamNos.Add(beamNo))
                {
                    _selectedInspectionBeamNos.Remove(beamNo);
                }

                RefreshInspectionBeamSelection();
                return;
            }

            if (_selectedRunBeamNo == beamNo)
            {
                return;
            }

            if (!IsOneShotReviewMode)
                _selectedRunShotKey = ResolveRunShotKeyForBeamChange(_selectedRunShotKey, _reviewManager.CurrentPoint, _reviewManager.LastMeasuredPoint);
            var preservedPoint = FindPointByShotKey(_reviewManager.CurrentPlan, _selectedRunShotKey);
            if (preservedPoint is not null)
            {
                _selectedRunCellNo = preservedPoint.CellNo;
            }

            _selectedRunBeamNo = beamNo;
            _statusReporter($"Review Beam selected: Beam{beamNo}");
            RefreshRunDisplayFromCachedContext(rebuildAllPlan: true);
            RefreshOneShotSelection(clearResult: true);
        }

        private void SelectInspectionMethod(object? parameter)
        {
            if (IsReviewExecutionActive)
            {
                _statusReporter("Inspection Method cannot be changed while Review is running.");
                return;
            }

            var inspectionMethod = NormalizeInspectionMethod(parameter?.ToString());
            if (_selectedInspectionMethod.Equals(inspectionMethod, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedInspectionMethod = inspectionMethod;
            InspectionMethodItems = BuildInspectionMethodItems();
            _statusReporter($"Review Inspection Method selected: {_selectedInspectionMethod}");
            OnPropertyChanged(nameof(SelectedInspectionMethod));
            OnPropertyChanged(nameof(InspectionMethodItems));
            OnPropertyChanged(nameof(SampleRuleSelectionSummary));
        }

        private bool CanSelectInspectionMethodCommand(object? parameter)
        {
            return !IsReviewExecutionActive;
        }

        private void RestoreSimpleShotSelection(ST_REVIEW_PLAN plan)
        {
            _selectedSampleShotKeys.RemoveWhere(key => FindPointByShotKey(plan, key)?.Use != true);
            if (!string.IsNullOrWhiteSpace(_simpleSampleShotKey) && FindPointByShotKey(plan, _simpleSampleShotKey)?.Use != true)
            {
                _simpleSampleShotKey = "";
            }

            if (string.IsNullOrWhiteSpace(_pendingSimpleShot))
            {
                return;
            }

            var savedShot = _pendingSimpleShot;
            _pendingSimpleShot = "";
            var normalizedKey = CReviewManager.NormalizeShotKey(savedShot);
            ST_REVIEW_PLAN_POINT? selectedPoint = null;
            foreach (var point in plan.Points)
            {
                if ((!string.IsNullOrWhiteSpace(normalizedKey) && point.ShotKey.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase)) || FormatSimpleShot(point).Equals(savedShot, StringComparison.OrdinalIgnoreCase))
                {
                    selectedPoint = point.Use ? point : null;
                    break;
                }
            }

            _simpleSampleShotKey = selectedPoint?.ShotKey ?? "";
            _selectedSampleShotKeys.Clear();
            if (selectedPoint is not null)
            {
                _selectedSampleShotKeys.Add(selectedPoint.ShotKey);
                return;
            }

            if (_selectedSampleRuleUi.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase))
            {
                _statusReporter($"Saved SIMPLE Shot is not available in the current Recipe: {savedShot}");
            }
        }

        private static string FormatSimpleShot(ST_REVIEW_PLAN? plan, string shotKey)
        {
            if (plan is null || string.IsNullOrWhiteSpace(shotKey))
            {
                return "";
            }

            foreach (var point in plan.Points)
            {
                if (point.ShotKey.Equals(shotKey, StringComparison.OrdinalIgnoreCase))
                {
                    return FormatSimpleShot(point);
                }
            }

            return "";
        }

        private static string FormatSimpleShot(ST_REVIEW_PLAN_POINT point)
        {
            return $"CELL{point.CellNo}_{ToMatrixShotName(point)}";
        }

        private IReadOnlyList<ST_REVIEW_INSPECTION_METHOD_ITEM> BuildInspectionMethodItems()
        {
            return[new ST_REVIEW_INSPECTION_METHOD_ITEM(PointToPointInspectionMethod, "Point to Point", _selectedInspectionMethod.Equals(PointToPointInspectionMethod, StringComparison.OrdinalIgnoreCase), SelectInspectionMethodCommand), new ST_REVIEW_INSPECTION_METHOD_ITEM(InspectionOnFlyMethod, "IOF", _selectedInspectionMethod.Equals(InspectionOnFlyMethod, StringComparison.OrdinalIgnoreCase), SelectInspectionMethodCommand)];
        }

        private static string NormalizeInspectionMethod(string? inspectionMethod)
        {
            return inspectionMethod?.Trim().Equals(InspectionOnFlyMethod, StringComparison.OrdinalIgnoreCase) == true ? InspectionOnFlyMethod : PointToPointInspectionMethod;
        }

        private void BackToRunShotMap(object? parameter)
        {
            _statusReporter($"Review Shot selection opened: Cell{_selectedRunCellNo}");
        }

        private void BackToRunGlassPreview(object? parameter)
        {
            _isRunCellDetailVisible = false;
            RunCellShotRows = [];
            RunCellShotMatrixRows = [];
            _runShotRowsByKey = new Dictionary<string, ST_REVIEW_RUN_SHOT_ROW>(StringComparer.OrdinalIgnoreCase);
            _statusReporter("Review Glass preview opened.");
            OnPropertyChanged(nameof(IsRunGlassPreviewVisible));
            OnPropertyChanged(nameof(IsRunCellDetailVisible));
            OnPropertyChanged(nameof(IsRunShotMapVisible));
            OnPropertyChanged(nameof(RunWorkspaceTitle));
            OnPropertyChanged(nameof(RunWorkspaceSummary));
            OnPropertyChanged(nameof(RunCellShotRows));
            OnPropertyChanged(nameof(RunCellShotMatrixRows));
        }

        private void SelectSampleCell(object? parameter)
        {
            if (!int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cellNo))
            {
                return;
            }

            _selectedSampleCellNo = Math.Clamp(cellNo, 1, _cellCount);
            _sampleRuleCellNo = _selectedSampleCellNo;
            _isSampleCellDetailVisible = true;
            _statusReporter($"Sample Cell selected: Cell{_selectedSampleCellNo}");
            RefreshSampleCellDetailRows();
            OnPropertyChanged(nameof(SampleMapColumnCount));
            OnPropertyChanged(nameof(IsSampleGlassPreviewVisible));
            OnPropertyChanged(nameof(IsSampleCellDetailVisible));
            OnPropertyChanged(nameof(SampleWorkspaceTitle));
            OnPropertyChanged(nameof(SampleWorkspaceSummary));
            OnPropertyChanged(nameof(SampleCellShotRows));
            OnPropertyChanged(nameof(SampleCellShotMatrixRows));
        }

        private void RefreshSampleCellDetailRows()
        {
            if (!_isSampleCellDetailVisible || _lastAllPlan is null)
            {
                return;
            }

            SetSampleMapColumnCount(CalculateShotMapColumnCount(_lastAllPlan, _selectedSampleCellNo));
            _sampleShotRowsByKey.Clear();
            SampleCellShotRows = [];
            SampleCellShotMatrixRows = CreateLazySampleCellShotRows(_lastAllPlan, _selectedSampleCellNo);
        }

        private void BackToSampleGlassPreview(object? parameter)
        {
            _isSampleCellDetailVisible = false;
            SampleCellShotRows = [];
            SampleCellShotMatrixRows = [];
            // Cell-detail selection can change the reference marker while the
            // Glass preview is hidden.  Always rebuild it when returning so the
            // first visible frame already contains the current selection.
            RefreshSampleGlassPreview();
            _statusReporter("Sample Glass preview opened.");
            OnPropertyChanged(nameof(IsSampleGlassPreviewVisible));
            OnPropertyChanged(nameof(IsSampleCellDetailVisible));
            OnPropertyChanged(nameof(SampleWorkspaceTitle));
            OnPropertyChanged(nameof(SampleWorkspaceSummary));
            OnPropertyChanged(nameof(SampleCellShotRows));
            OnPropertyChanged(nameof(SampleCellShotMatrixRows));
        }

        internal bool IsReviewExecutionActive
        {
            get
            {
                return _oneShotRunning || _isStartRequestPending || _reviewManager.SequenceState is EN_REVIEW_SEQUENCE_STATE.Running or EN_REVIEW_SEQUENCE_STATE.Stopping;
            }
        }

        internal bool BlocksMenuNavigation
        {
            get
            {
                // Only this menu's operation owns its navigation lock. AUTO or
                // Correction may use the shared manager while this menu is idle.
                var cancellation = _oneShotRunning ? _oneShotCancellation : _beamInspectionCancellation ?? _reviewCancellation;
                return (_oneShotRunning || _isStartRequestPending) && cancellation?.IsCancellationRequested != true;
            }
        }

        private ST_REVIEW_PLAN ConfigureReviewCorrectionPlan(ST_REVIEW_PLAN reviewPlan, ST_REVIEW_PLAN allPlan)
        {
            if (!_selectedMode.Equals("SAMPLE SHOT", StringComparison.OrdinalIgnoreCase) || reviewPlan.ReviewPoints.Count == 0)
            {
                return reviewPlan;
            }

            var configuration = CreateCorrectionRuleConfiguration();
            var correctionPlan = new ST_REVIEW_CORRECTION_PLAN(configuration.Rule, configuration.Scope, allPlan.Points, configuration.RoughReference, configuration.FineLineCount);
            return reviewPlan with
            {
                CorrectionPlan = correctionPlan
            };
        }

        private ST_REVIEW_PLAN_POINT? FindPointByShotKey(ST_REVIEW_PLAN? plan, string shotKey)
        {
            if (plan is null || !TryParseShotKey(shotKey, out var cellNo, out var shotNo))
            {
                return null;
            }

            return plan.FindPoint(cellNo, shotNo);
        }

        private int CountSelectedSampleCells()
        {
            if (_selectedMode.Equals("ALL SHOT", StringComparison.OrdinalIgnoreCase))
            {
                return _lastAllPlan?.CellCount ?? 0;
            }

            return _selectedSampleShotKeys.Select(key => TryParseShotKey(key, out var cellNo, out _) ? cellNo : 0).Where(cellNo => cellNo > 0).Distinct().Count();
        }

        private string BuildSampleGlassPreviewSummary()
        {
            if (!IsSimpleSampleRuleUi)
            {
                return $"AUTO / {GetAutomaticInspectionShotCount():N0} Inspection Shots";
            }

            return $"{CountSelectedSampleCells()} Cells / {GetSelectedSampleShotCount()} Shots Selected";
        }

        private int GetConfiguredSampleInspectionShotCount() => IsSimpleSampleRuleUi ? (GetSimpleReferencePoint()is null ? 0 : 1) : GetAutomaticInspectionShotCount();
        private IReadOnlyList<ST_DISPLAY_ITEM> CreateSamplePlanSummaryItems()
        {
            var simplePoint = GetSimpleReferencePoint();
            var isSimple = _selectedSampleRuleUi == "SIMPLE";
            return[
                new("Rule", _selectedSampleRuleUi), 
                new("Head", isSimple ? (simplePoint is null ? "-" : $"H{simplePoint.HeadNo:00}") : "Every assigned Head"), 
                new("Cell", isSimple ? (simplePoint is null ? "-" : $"Cell{simplePoint.CellNo}") : IsRoughSampleRuleUi ? "Head range" : "Every Cell"), 
                new("Shot", isSimple ? (simplePoint is null ? "-" : ToMatrixShotName(simplePoint)) : GetAutomaticReferenceText()), 
                new("Apply Scope", _sampleRuleScope == "HEAD_CELL" ? "HEAD + CELL" : "HEAD"), 
                new("Inspection Points", isSimple ? (simplePoint is null ? "0" : "1") : "Automatic at START"), 
                new("Offset Apply Points", isSimple ? GetSimpleOffsetApplyCount().ToString("N0", CultureInfo.InvariantCulture) : "Calculated at START"), 
                new("Output", "Review Correction Plan")
            ];
        }

        private string GetAutomaticReferenceText() => _selectedSampleRuleUi switch
        {
            "ROUGH" => _roughReference,
            "FINE" => $"{_fineLineCount} automatic line(s) per Cell",
            _ => "-"
        };
        private ST_REVIEW_PLAN_POINT? GetSimpleReferencePoint()
        {
            if (string.IsNullOrWhiteSpace(_simpleSampleShotKey))
            {
                return null;
            }

            var referencePoint = FindPointByShotKey(_lastAllPlan, _simpleSampleShotKey);
            if (referencePoint is not null)
            {
                return referencePoint;
            }

            // A recipe or Plan refresh can leave the stored key stale while the
            // Cell selection still contains the one valid SIMPLE reference.  Do
            // not show a selected tile with an empty Reference panel; restore the
            // reference from that single current selection.
            if (_selectedSampleShotKeys.Count != 1)
            {
                return null;
            }

            referencePoint = FindPointByShotKey(_lastAllPlan, _selectedSampleShotKeys.First());
            if (referencePoint is null)
            {
                return null;
            }

            _simpleSampleShotKey = referencePoint.ShotKey;
            _sampleRuleHeadNo = referencePoint.HeadNo;
            _sampleRuleCellNo = referencePoint.CellNo;
            return referencePoint;
        }

        private int GetSimpleOffsetApplyCount()
        {
            var point = GetSimpleReferencePoint();
            if (point is null || _lastAllPlan is null)
            {
                return 0;
            }

            EnsureSampleScopePointCounts(_lastAllPlan);
            return _sampleRuleScope == "HEAD_CELL" ? _sampleHeadCellPointCounts.GetValueOrDefault((point.HeadNo, point.CellNo)) : _sampleHeadPointCounts.GetValueOrDefault(point.HeadNo);
        }

        private int GetAutomaticInspectionShotCount()
        {
            return _lastAllPlan is null ? 0 : GetOrBuildCorrectionRulePlan(_lastAllPlan).Targets.Count;
        }

        private int GetAutomaticOffsetApplyCount()
        {
            if (_lastAllPlan is null)
            {
                return 0;
            }

            EnsureSampleScopePointCounts(_lastAllPlan);
            var targets = GetOrBuildCorrectionRulePlan(_lastAllPlan).Targets;
            if (_sampleRuleScope == "HEAD_CELL")
            {
                return targets.Select(target => (target.HeadNo, target.CellNo)).Distinct().Sum(key => _sampleHeadCellPointCounts.GetValueOrDefault(key));
            }

            return targets.Select(target => target.HeadNo).Distinct().Sum(headNo => _sampleHeadPointCounts.GetValueOrDefault(headNo));
        }

        private void EnsureSampleScopePointCounts(ST_REVIEW_PLAN plan)
        {
            if (ReferenceEquals(_sampleScopeCountPlan, plan))
            {
                return;
            }

            var headCounts = new Dictionary<int, int>();
            var headCellCounts = new Dictionary<(int HeadNo, int CellNo), int>();
            for (var cellNo = 1; cellNo <= plan.CellCount; cellNo++)
            {
                var cellPoints = plan.GetCellPoints(cellNo);
                for (var index = 0; index < cellPoints.Count; index++)
                {
                    var point = cellPoints[index];
                    AddScopePointCount(point.HeadNo, cellNo, 1, headCounts, headCellCounts);
                }
            }

            _sampleScopeCountPlan = plan;
            _sampleHeadPointCounts = headCounts;
            _sampleHeadCellPointCounts = headCellCounts;
        }

        private static void AddScopePointCount(int headNo, int cellNo, int count, IDictionary<int, int> headCounts, IDictionary<(int HeadNo, int CellNo), int> headCellCounts)
        {
            if (headNo <= 0 || count <= 0)
            {
                return;
            }

            headCounts.TryGetValue(headNo, out var headCount);
            headCounts[headNo] = headCount + count;
            var key = (headNo, cellNo);
            headCellCounts.TryGetValue(key, out var headCellCount);
            headCellCounts[key] = headCellCount + count;
        }

        private int GetSelectedSampleShotCount() => _selectedMode.Equals("ALL SHOT", StringComparison.OrdinalIgnoreCase) ? _lastAllPlan?.ReviewPoints.Count ?? 0 : _selectedSampleShotKeys.Count;
        private static int GetCellPointCount(ST_REVIEW_PLAN? plan, int cellNo) => plan is { CellCount: > 0 } ? plan.GetCellPoints(cellNo).Count : 0;
        private int GetSampleCellShotCount() => _lastAllPlan is { CellCount: > 0 } plan ? plan.GetCellPoints(_selectedSampleCellNo).Count : 0;
        private int GetSelectedSampleCellShotCount()
        {
            if (_selectedMode.Equals("ALL SHOT", StringComparison.OrdinalIgnoreCase))
            {
                return GetSampleCellShotCount();
            }

            var selectedKeys = IsSimpleSampleRuleUi || _lastAllPlan is null ? _selectedSampleShotKeys : GetOrBuildCorrectionRulePlan(_lastAllPlan).ShotKeys;
            return selectedKeys.Count(key => TryParseShotKey(key, out var cellNo, out _) && cellNo == _selectedSampleCellNo);
        }

        private static bool TryParseShotKey(string value, out int cellNo, out int shotNo)
        {
            cellNo = 0;
            shotNo = 0;
            var normalized = CReviewManager.NormalizeShotKey(value);
            var separator = normalized.IndexOf("_SHOT", StringComparison.Ordinal);
            return separator > 4 && int.TryParse(normalized.AsSpan(4, separator - 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out cellNo) && int.TryParse(normalized.AsSpan(separator + 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out shotNo) && cellNo > 0 && shotNo > 0;
        }

        private static int CalculateShotMapColumnCount(ST_REVIEW_PLAN allPlan, int cellNo)
        {
            if (allPlan.CellCount <= 0 || allPlan.Points.Count == 0)
            {
                return 4;
            }

            var cellPoints = allPlan.GetCellPoints(Math.Clamp(cellNo, 1, allPlan.CellCount));
            if (cellPoints.Count == 0)
            {
                return 4;
            }

            // DesignX cannot determine the matrix width after coordinate transforms.
            // Use the recipe's Count X carried by each review point, as the Run matrix does.
            return Math.Clamp(cellPoints[0].PixelCountX, 1, 80);
        }

        private void SetSampleMapColumnCount(int columnCount)
        {
            columnCount = Math.Max(1, columnCount);
            if (_sampleMapColumnCount == columnCount)
            {
                return;
            }

            _sampleMapColumnCount = columnCount;
        }

        private IReadOnlyList<ST_REVIEW_POINT_SELECT_ROW> CreatePointSelectRows(ST_REVIEW_PLAN reviewPlan, IReadOnlySet<string> selectedKeys, CButtonCommand command)
        {
            var rows = new List<ST_REVIEW_POINT_SELECT_ROW>(reviewPlan.Points.Count);
            foreach (var point in reviewPlan.Points.OrderBy(point => point.CellNo).ThenBy(point => point.ShotNo))
            {
                var use = point.Use && selectedKeys.Contains(point.ShotKey);
                var reason = IsReviewPointExcluded(point) ? point.ExclusionReason : GetShotReason(point, use);
                rows.Add(new ST_REVIEW_POINT_SELECT_ROW(
                    point.PointNo, 
                    point.ShotKey, 
                    $"H{point.HeadNo:00}", 
                    $"CELL{point.CellNo:00}", 
                    ToMatrixShotName(point), 
                    point.CellNo.ToString(CultureInfo.InvariantCulture), 
                    point.ShotNo.ToString(CultureInfo.InvariantCulture), 
                    FormatDouble(point.DesignX), 
                    FormatDouble(point.DesignY), 
                    FormatDouble(point.ReviewTargetX), 
                    FormatDouble(point.ReviewTargetY), 
                    use, 
                    reason, 
                    point.ExclusionReason, 
                    command, 
                    SetSampleShotSelectionCommand
                ));
            }

            return rows;
        }

        private IReadOnlyList<ST_REVIEW_POINT_SELECT_MATRIX_ROW> CreateLazySampleCellShotRows(ST_REVIEW_PLAN allPlan, int cellNo)
        {
            if (allPlan.CellCount <= 0 || allPlan.Points.Count == 0)
            {
                return[];
            }

            var normalizedCellNo = Math.Clamp(cellNo, 1, allPlan.CellCount);
            var cellPoints = allPlan.GetCellPoints(normalizedCellNo);
            var shotsPerCell = cellPoints.Count;
            if (shotsPerCell == 0)
            {
                return[];
            }

            var columnCount = Math.Max(1, SampleMapColumnCount);
            var rowCount = (shotsPerCell + columnCount - 1) / columnCount;
            IReadOnlySet<string> selectedKeys = IsSimpleSampleRuleUi ? _selectedSampleShotKeys : GetOrBuildCorrectionRulePlan(allPlan).ShotKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new CLazyReadOnlyList<ST_REVIEW_POINT_SELECT_MATRIX_ROW>(rowCount, rowIndex =>
            {
                var firstShotIndex = rowIndex * columnCount;
                var count = Math.Min(columnCount, shotsPerCell - firstShotIndex);
                var points = Enumerable.Range(0, count).Select(offset => cellPoints[firstShotIndex + offset]).ToArray();
                var rows = CreatePointSelectRows(allPlan with { Points = points }, selectedKeys, TogglePointCommand);
                if (_selectedMode.Equals("ALL SHOT", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var row in rows)
                    {
                        if (!row.IsExcluded)
                        {
                            row.SetSelection(true, "ALL");
                        }
                    }
                }

                foreach (var row in rows)
                {
                    _sampleShotRowsByKey[row.ShotKey] = row;
                }

                return new ST_REVIEW_POINT_SELECT_MATRIX_ROW(rowIndex + 1, rows);
            });
        }

        private string GetSelectionRuleText()
        {
            if (_selectedMode.Equals("ALL SHOT", StringComparison.OrdinalIgnoreCase))
            {
                return "All Shot";
            }

            if (_selectedMode.Equals("ZERO DEFENSE", StringComparison.OrdinalIgnoreCase))
            {
                return "0-Line";
            }

            return _selectionRuleText;
        }

        private IReadOnlyCollection<string> GetPlanShotKeys(ST_REVIEW_PLAN? allPlan)
        {
            if (allPlan is null || IsBeamInspectionMode)
            {
                return[];
            }

            if (_selectedMode.Equals("ALL SHOT", StringComparison.OrdinalIgnoreCase))
            {
                return allPlan.Points.Where(point => point.HeadNo > 0).Select(point => point.ShotKey).ToArray();
            }

            if (_selectedMode.Equals("ZERO DEFENSE", StringComparison.OrdinalIgnoreCase))
            {
                return SelectZeroLineKeys(allPlan, 0);
            }

            if (_selectedMode.Equals("SAMPLE SHOT", StringComparison.OrdinalIgnoreCase))
            {
                return GetOrBuildCorrectionRulePlan(allPlan).ShotKeys;
            }

            return _selectedSampleShotKeys.Where(key => FindPointByShotKey(allPlan, key)?.HeadNo > 0).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static IReadOnlyCollection<string> CreateDefaultSampleShotKeys(ST_REVIEW_PLAN allPlan)
        {
            if (allPlan.CellCount <= 0 || allPlan.Points.Count == 0)
            {
                return[];
            }

            var keys = new List<string>(allPlan.CellCount * 3);
            for (var cellNo = 1; cellNo <= allPlan.CellCount; cellNo++)
            {
                var cellPoints = allPlan.GetCellPoints(cellNo);
                if (cellPoints.Count == 0)
                {
                    continue;
                }

                var offsets = new[]
                {
                    0,
                    cellPoints.Count / 2,
                    cellPoints.Count - 1
                };
                var previousOffset = -1;
                for (var index = 0; index < offsets.Length; index++)
                {
                    var offset = offsets[index];
                    if (offset == previousOffset)
                    {
                        continue;
                    }

                    previousOffset = offset;
                    var point = cellPoints[offset];
                    if (point.HeadNo > 0)
                    {
                        keys.Add(point.ShotKey);
                    }
                }
            }

            return keys;
        }

        private static int GetShotRow(ST_REVIEW_PLAN_POINT point) => ((Math.Max(1, point.ShotNo) - 1) / Math.Max(1, point.PixelCountX)) + 1;
        private static int GetCenterRow(int rowCount) => (Math.Max(1, rowCount) + 1) / 2;
        private static IReadOnlyCollection<string> SelectEdgeKeys(ST_REVIEW_PLAN plan)
        {
            var executableKeys = plan.Points.Where(point => point.HeadNo > 0).Select(point => point.ShotKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return CReviewRulePlanBuilder.SelectEdgeShotKeys(plan).Where(executableKeys.Contains).ToArray();
        }

        private static IReadOnlyCollection<string> SelectCenterKeys(ST_REVIEW_PLAN plan)
        {
            var executableKeys = plan.Points.Where(point => point.HeadNo > 0).Select(point => point.ShotKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return CReviewRulePlanBuilder.SelectCenterShotKeys(plan).Where(executableKeys.Contains).ToArray();
        }

        private static IReadOnlyCollection<string> SelectZeroLineKeys(ST_REVIEW_PLAN plan, int zeroPointCount)
        {
            var executableCount = 0;
            var minY = double.PositiveInfinity;
            var maxY = double.NegativeInfinity;
            foreach (var point in plan.Points)
            {
                if (point.HeadNo <= 0)
                {
                    continue;
                }

                executableCount++;
                minY = Math.Min(minY, point.DesignY);
                maxY = Math.Max(maxY, point.DesignY);
            }

            if (executableCount == 0)
            {
                return[];
            }

            var targetY = (minY + maxY) / 2.0;
            var count = zeroPointCount <= 0 ? Math.Min(5, executableCount) : Math.Min(zeroPointCount, executableCount);
            var nearestPoints = new PriorityQueue<ST_REVIEW_PLAN_POINT, (double Distance, double X)>(Comparer<(double Distance, double X)>.Create((left, right) =>
            {
                var distanceComparison = right.Distance.CompareTo(left.Distance);
                return distanceComparison != 0 ? distanceComparison : right.X.CompareTo(left.X);
            }));
            foreach (var point in plan.Points)
            {
                if (point.HeadNo <= 0)
                {
                    continue;
                }

                nearestPoints.Enqueue(point, (Math.Abs(point.DesignY - targetY), point.DesignX));
                if (nearestPoints.Count > count)
                {
                    nearestPoints.Dequeue();
                }
            }

            return nearestPoints.UnorderedItems.Select(item => item.Element).OrderBy(point => Math.Abs(point.DesignY - targetY)).ThenBy(point => point.DesignX).Select(point => point.ShotKey).ToArray();
        }

        private ST_REVIEW_RULE_PLAN GetOrBuildCorrectionRulePlan(ST_REVIEW_PLAN allPlan)
        {
            var configuration = CreateCorrectionRuleConfiguration();
            if (_lastCorrectionRulePlan is not null && _lastCorrectionRulePlan.Configuration == configuration && ReferenceEquals(_lastCorrectionRuleSource, allPlan.Points))
            {
                return _lastCorrectionRulePlan;
            }

            _lastCorrectionRulePlan = CReviewRulePlanBuilder.Build(allPlan, configuration);
            _lastCorrectionRuleSource = allPlan.Points;
            return _lastCorrectionRulePlan;
        }

        private ST_REVIEW_RULE_CONFIGURATION CreateCorrectionRuleConfiguration()
        {
            var rule = _selectedSampleRuleUi switch
            {
                "ROUGH" => EN_REVIEW_CORRECTION_RULE.Rough,
                "FINE" => EN_REVIEW_CORRECTION_RULE.Fine,
                _ => EN_REVIEW_CORRECTION_RULE.Rough
            };
            var reference = _roughReference switch
            {
                "CENTER" => EN_REVIEW_REFERENCE_LINE.Center,
                "LAST" => EN_REVIEW_REFERENCE_LINE.Last,
                _ => EN_REVIEW_REFERENCE_LINE.First
            };
            return new(rule, _sampleRuleScope == "HEAD_CELL" ? EN_REVIEW_CORRECTION_SCOPE.HeadCell : EN_REVIEW_CORRECTION_SCOPE.Head, _selectedRunBeamNo, _simpleSampleShotKey, reference, _fineLineCount);
        }

        private IReadOnlyList<ST_REVIEW_PLAN_POINT> GetAutomaticInspectionPreviewPoints(ST_REVIEW_PLAN plan)
        {
            if (IsSimpleSampleRuleUi)
            {
                return[];
            }

            return GetOrBuildCorrectionRulePlan(plan).Targets.Select(target => FindPointByShotKey(plan, target.ShotKey)).Where(point => point is not null).Cast<ST_REVIEW_PLAN_POINT>().ToArray();
        }

        private EN_REVIEW_CORRECTION_RULE? GetAutomaticInspectionPreviewRule() => _selectedSampleRuleUi switch
        {
            "ROUGH" => EN_REVIEW_CORRECTION_RULE.Rough,
            "FINE" => EN_REVIEW_CORRECTION_RULE.Fine,
            _ => null
        };
        private bool ShouldDrawSampleScope() => IsRoughSampleRuleUi || IsSimpleSampleRuleUi && !string.IsNullOrWhiteSpace(_simpleSampleShotKey);
        private IReadOnlyList<ST_REVIEW_SCOPE_TARGET> GetRoughScopeTargets(ST_REVIEW_PLAN plan)
        {
            if (!IsRoughSampleRuleUi)
            {
                return[];
            }

            return GetOrBuildCorrectionRulePlan(plan).Targets.Select(target => new ST_REVIEW_SCOPE_TARGET(target.HeadNo, _sampleRuleScope == "HEAD_CELL" ? target.CellNo : 0)).Distinct().ToArray();
        }

        private string GetShotReason(ST_REVIEW_PLAN_POINT point, bool use)
        {
            if (!use)
            {
                return "-";
            }

            if (_selectedMode.Equals("ALL SHOT", StringComparison.OrdinalIgnoreCase))
            {
                return "ALL";
            }

            return _selectionRuleText.Equals("None", StringComparison.OrdinalIgnoreCase) ? "MANUAL" : _selectionRuleText.ToUpperInvariant();
        }

        private static string ToRuleText(EN_REVIEW_RULE_TYPE ruleType)
        {
            return ruleType switch
            {
                EN_REVIEW_RULE_TYPE.AllPoint => "All Shot",
                EN_REVIEW_RULE_TYPE.Edge => "Edge",
                EN_REVIEW_RULE_TYPE.Center => "Center",
                EN_REVIEW_RULE_TYPE.HeadPoint => "Head Shot",
                EN_REVIEW_RULE_TYPE.CellPoint => "Cell Shot",
                EN_REVIEW_RULE_TYPE.ZeroLine => "0-Line",
                _ => "Rule"
            };
        }

        private static string ToDisplayModeName(string mode) => ToReviewModeDisplayName(mode);
        private ST_REVIEW_MODE_ITEM Mode(string name, string detail)
        {
            return new ST_REVIEW_MODE_ITEM(name, detail, name.Equals(_selectedMode, StringComparison.OrdinalIgnoreCase), SelectModeCommand, ToReviewModeDisplayName(name));
        }

        private ST_REVIEW_TAB_ITEM ReviewTab(string name, bool isSelected)
        {
            return new ST_REVIEW_TAB_ITEM(name, isSelected, SelectTabCommand, ToReviewTabDisplayName(name));
        }

        private static string ToReviewModeDisplayName(string mode)
        {
            return mode.Equals("SAMPLE SHOT", StringComparison.OrdinalIgnoreCase) ? "RULE" : mode;
        }

        private static string ToReviewTabDisplayName(string tab)
        {
            return tab.Equals("SAMPLE SELECT", StringComparison.OrdinalIgnoreCase) ? "RULE SETTING" : tab;
        }

        private static string ToStateText(EN_REVIEW_POINT_STATE state)
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

        private static string ToMatrixShotName(ST_REVIEW_PLAN_POINT point)
        {
            return CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX);
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        private static string FormatSigned(double value)
        {
            return value.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture);
        }

        public override Task StopForHeavyAlarm()
        {
            _oneShotCancellation?.Cancel();
            _beamInspectionCancellation?.Cancel();
            _reviewCancellation?.Cancel();
            if (IsReviewExecutionActive)
                _reviewManager.Stop();
            return Task.CompletedTask;
        }

        private void RequestOpenCorrectionReview(Func<string, string, bool>? confirm = null)
        {
            if (!OpenCorrectionReviewCommand.CanExecute(null))
                return;
            confirm ??= (title, message) => CRecipeConfirmDialog.ShowConfirmation(title, message, "OK");
            if (confirm("화면 이동", "SIMPLE/ROUGH/FINE 보정 및 검사 화면으로 이동하시겠습니까?") && OpenCorrectionReviewCommand.CanExecute(null))
                _openCorrectionReview?.Invoke();
        }

        private async Task RequestReviewStart(Func<string, string, bool>? confirm = null)
        {
            if (!CheckOperationAlarm("REVIEW START"))
                return;
            if (!StartCommand.CanExecute(null))
                return;
            var detail = IsBeamInspectionMode ? $"선택 Shot {_selectedInspectionShotKeys.Count:N0}개 / Beam {_selectedInspectionBeamNos.Count}개" : $"{_selectedInspectionMethod} / Beam{_selectedRunBeamNo}";
            var shiftOffset = (X: 0.0, Y: 0.0);
            if (confirm is not null)
            {
                if (!confirm($"{_selectedMode} 검사 시작", $"Recipe: {_recipeId}\n{detail}\n\n{_selectedMode} 검사를 시작하시겠습니까?"))
                    return;
            }
            else if (_stationManager.LastCompletedManualMof is { } completion)
            {
                var choice = CReviewShiftOffsetDialog.Show(completion, $"Recipe: {_recipeId} / {_selectedMode} / {detail}");
                if (choice == EN_REVIEW_SHIFT_OFFSET_CHOICE.Cancel)
                    return;
                if (choice == EN_REVIEW_SHIFT_OFFSET_CHOICE.Apply)
                    shiftOffset = (completion.ShiftOffsetX, completion.ShiftOffsetY);
            }
            else if (!CRecipeConfirmDialog.ShowConfirmation(
                         $"{_selectedMode} 검사 시작",
                         $"Recipe: {_recipeId}\n{detail}\n\n성공 완료한 Manual MOF 이력이 없습니다.\nShift Offset 0.000 / 0.000 mm로 검사를 시작하시겠습니까?",
                         "시작"))
            {
                return;
            }

            await StartReviewSequence(shiftOffset.X, shiftOffset.Y);
        }

        private void RequestReviewStop(Func<string, string, bool>? confirm = null)
        {
            if (!StopCommand.CanExecute(null))
                return;
            if (IsAllShotReviewMode || IsBeamInspectionMode)
            {
                confirm ??= (title, message) => CRecipeConfirmDialog.ShowConfirmation(title, message, "중지", useDangerStyle: true);
                if (!confirm($"{_selectedMode} 검사 중지", $"{_selectedMode} 검사를 중지하시겠습니까?"))
                    return;
            }

            StopReviewSequence();
        }

#endregion
#region BeamInspection
        public CButtonCommand PreviousInspectionShotCommand { get; }
        public CButtonCommand NextInspectionShotCommand { get; }
        public bool IsInspectionExecutionViewVisible => IsBeamInspectionMode && (IsReviewExecutionActive || _loadedBeamInspectionResult is not null);
        public bool IsInspectionSelectionControlsVisible => IsBeamInspectionMode && !IsReviewExecutionActive && _loadedBeamInspectionResult is null && !_isBeamInspectionResultLoading;

        public string InspectionShotSummary
        {
            get
            {
                if (GetLoadedInspectionShot()is { } loaded)
                    return $"{loaded.GlassName} / {loaded.HeadName} / {loaded.CellName} / {loaded.ShotName} · {_beamInspectionBrowseIndex + 1}/{_loadedBeamInspectionResult!.Shots.Count}";
                var shots = GetInspectionDisplayShots();
                if (shots.Count == 0)
                    return "선택된 Shot 없음";
                var index = Math.Clamp(_beamInspectionBrowseIndex, 0, shots.Count - 1);
                var shot = shots[index];
                return $"HEAD {shot.HeadNo:00} / CELL {shot.CellNo:00} / {ToMatrixShotName(shot)} · {index + 1}/{shots.Count}";
            }
        }

        public string InspectionProgressText
        {
            get
            {
                if (_loadedBeamInspectionResult is { } loaded)
                    return $"불러온 결과{(loaded.IsSimulation ? " · SIM" : "")} · {loaded.FileName} · {loaded.SavedAt:yyyy-MM-dd HH:mm:ss}";
                if (_isBeamInspectionResultLoading)
                    return "최근 Beam Inspection 결과를 불러오는 중입니다.";
                var snapshot = _beamInspectionSnapshot;
                if (snapshot is null)
                    return "검사 대기 · 결과: ΔX / ΔY (mm)";
                var mode = $"MELSEC {(snapshot.IsStageSimulation ? "SIM" : "REAL")} / Vision {(snapshot.IsVisionSimulation ? "SIM" : "REAL")} · ";
                var active = snapshot.ActiveIndex >= 0 ? snapshot.Points[snapshot.ActiveIndex] : null;
                return active is null ? $"{mode}{snapshot.Message} · {snapshot.CompletedCount}/{snapshot.Points.Count} Beam" : $"{mode}검사 중: C{active.CellNo} / {ToMatrixShotName(active)} / Beam{active.BeamNo} · {snapshot.CompletedCount}/{snapshot.Points.Count}";
            }
        }

        private IReadOnlyList<ST_REVIEW_PLAN_POINT> GetInspectionDisplayShots() => _beamInspectionSnapshot is { } snapshot ? snapshot.Points.DistinctBy(p => p.ShotKey, StringComparer.OrdinalIgnoreCase).ToArray() : _selectedInspectionShotKeys.Select(key => FindPointByShotKey(_lastAllPlan, key)).OfType<ST_REVIEW_PLAN_POINT>().Where(p => !IsReviewPointExcluded(p)).OrderBy(p => p.CellNo).ThenBy(p => p.ShotNo).ToArray();
        private IReadOnlyDictionary<int, ST_REVIEW_PLAN_POINT> GetDisplayedInspectionResults()
        {
            if (!IsInspectionExecutionViewVisible || _beamInspectionSnapshot is null)
                return new Dictionary<int, ST_REVIEW_PLAN_POINT>();
            var shots = GetInspectionDisplayShots();
            if (shots.Count == 0)
                return new Dictionary<int, ST_REVIEW_PLAN_POINT>();
            var shot = shots[Math.Clamp(_beamInspectionBrowseIndex, 0, shots.Count - 1)];
            return _beamInspectionSnapshot.Points.Where(p => p.ShotKey == shot.ShotKey).ToDictionary(p => p.BeamNo);
        }

        private void BrowseInspectionShot(int delta)
        {
            var count = GetInspectionDisplayShotCount();
            if (!IsInspectionExecutionViewVisible || count == 0)
                return;
            _beamInspectionBrowseIndex = Math.Clamp(_beamInspectionBrowseIndex + delta, 0, count - 1);
            _followBeamInspection = false;
            RefreshBeamInspectionDisplay();
        }

        private void ResetBeamInspectionResults()
        {
            if (IsReviewExecutionActive)
                return;
            _beamInspectionSnapshot = null;
            if (_loadedBeamInspectionResult is null)
            {
                _beamInspectionBrowseIndex = 0;
                _followBeamInspection = true;
            }

            RefreshBeamInspectionDisplay();
        }

        private void RefreshBeamInspectionDisplay()
        {
            RunBeamRows = BuildBeamRows(_selectedRunBeamNo);
            OnPropertyChanged(nameof(RunBeamRows));
            OnPropertyChanged(nameof(BeamSelectionSummary));
            OnPropertyChanged(nameof(InspectionShotSummary));
            OnPropertyChanged(nameof(InspectionProgressText));
            OnPropertyChanged(nameof(IsInspectionExecutionViewVisible));
            OnPropertyChanged(nameof(IsInspectionSelectionControlsVisible));
            OnPropertyChanged(nameof(CanSelectBeam));
            OnPropertyChanged(nameof(CanLoadBeamInspectionResult));
            OnPropertyChanged(nameof(CanClearLoadedBeamInspectionResult));
            LoadLastBeamInspectionResultCommand?.NotifyCanExecuteChanged();
            ClearLoadedBeamInspectionResultCommand?.NotifyCanExecuteChanged();
            SelectRunBeamCommand?.NotifyCanExecuteChanged();
            SelectRunShotCommand?.NotifyCanExecuteChanged();
            SelectAllInspectionBeamsCommand?.NotifyCanExecuteChanged();
            ClearInspectionBeamsCommand?.NotifyCanExecuteChanged();
            ClearInspectionShotsCommand?.NotifyCanExecuteChanged();
            PreviousInspectionShotCommand?.NotifyCanExecuteChanged();
            NextInspectionShotCommand?.NotifyCanExecuteChanged();
            StartCommand?.NotifyCanExecuteChanged();
        }

        private void ApplyBeamInspectionProgress(ST_BEAM_INSPECTION_SNAPSHOT snapshot)
        {
            void Apply()
            {
                _beamInspectionSnapshot = snapshot;
                if (_followBeamInspection && snapshot.ActiveIndex >= 0)
                {
                    var key = snapshot.Points[snapshot.ActiveIndex].ShotKey;
                    _beamInspectionBrowseIndex = GetInspectionDisplayShots().ToList().FindIndex(p => p.ShotKey == key);
                }

                RefreshBeamInspectionDisplay();
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                dispatcher.Invoke(Apply);
            else
                Apply();
        }

        private async Task StartBeamInspectionSequence(double shiftOffsetX, double shiftOffsetY)
        {
            if (IsReviewExecutionActive || IsSampleSelectTab || _loadedBeamInspectionResult is not null || _isBeamInspectionResultLoading)
                return;
            InvalidateBeamInspectionResultLoad(clearLoaded: true);
            ResetBeamInspectionResults();
            _isStartRequestPending = true;
            _beamInspectionCancellation = new CancellationTokenSource();
            var token = _beamInspectionCancellation.Token;
            NotifyReviewExecutionCommandStates();
            try
            {
                var recipe = await LoadSelectedRecipe(token) ?? throw new InvalidOperationException("Recipe를 불러올 수 없습니다.");
                if (_lastRecipe is null || !recipe.Id.Equals(_lastRecipe.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Recipe가 변경되었습니다. Shot과 Beam을 다시 선택해 주세요.");
                var allPlan = _reviewManager.CreateAllPlan(recipe, _selectedRunBeamNo, shiftOffsetX, shiftOffsetY);
                // Snapshot the choices before any screen refresh, and revalidate against the latest Recipe.
                var shots = _selectedInspectionShotKeys.ToArray();
                var beams = _selectedInspectionBeamNos.ToArray();
                _lastRecipe = recipe;
                _lastAllPlan = allPlan;
                _lastAllPlanBeamNo = _selectedRunBeamNo;
                _followBeamInspection = true;
                _beamInspectionBrowseIndex = 0;
                var result = await _reviewManager.StartBeamInspection(recipe, allPlan, shots, beams, ApplyBeamInspectionProgress, token);
                _statusReporter($"{result.Message} ({result.CompletedCount}/{result.Points.Count}, NG={result.NgCount})");
                if (result.SaveError is not null)
                {
                    CRecipeConfirmDialog.ShowWarning("Beam Inspection 결과 저장 실패", $"{result.Message}\n검사 결과를 저장하지 못했습니다.\n{result.SaveError}");
                    return;
                }

                _statusReporter($"{result.Message} · 결과 저장: {result.SavedPath}");
                if (result.State == EN_REVIEW_SEQUENCE_STATE.Failed)
                    CRecipeConfirmDialog.ShowWarning("Beam Inspection 실패", result.Message);
                else if (result.State == EN_REVIEW_SEQUENCE_STATE.Completed)
                    CRecipeConfirmDialog.ShowCompletion("Beam Inspection", $"검사가 완료되었습니다.\n완료 {result.CompletedCount} Beam / NG {result.NgCount}\n결과 저장: {result.SavedPath}");
            }
            catch (OperationCanceledException)
            {
                _statusReporter("Beam Inspection 시작이 취소되었습니다.");
            }
            catch (Exception exception)
            {
                CRecipeConfirmDialog.ShowWarning("Beam Inspection 시작 불가", exception.Message);
            }
            finally
            {
                _beamInspectionCancellation.Dispose();
                _beamInspectionCancellation = null;
                _isStartRequestPending = false;
                NotifyReviewExecutionCommandStates();
                RefreshBeamInspectionDisplay();
                _refreshScreen();
            }
        }

        private void SelectAllInspectionBeams(object? parameter)
        {
            if (!IsBeamInspectionMode || !CanSelectBeam)
            {
                return;
            }

            _selectedInspectionBeamNos.UnionWith(Enumerable.Range(1, _splitBeamCount * _splitBeamCount));
            RefreshInspectionBeamSelection();
        }

        private void ClearInspectionBeams(object? parameter)
        {
            if (!IsBeamInspectionMode || !CanSelectBeam)
            {
                return;
            }

            _selectedInspectionBeamNos.Clear();
            RefreshInspectionBeamSelection();
        }

        private void RefreshInspectionBeamSelection()
        {
            ResetBeamInspectionResults();
            RunBeamRows = BuildBeamRows(_selectedRunBeamNo);
            OnPropertyChanged(nameof(RunBeamRows));
            OnPropertyChanged(nameof(BeamSelectionSummary));
        }

        private IReadOnlyList<ST_REVIEW_SELECTED_SHOT_ROW> BuildSelectedInspectionShotRows() => _selectedInspectionShotKeys.Select(key => FindPointByShotKey(_lastAllPlan, key)).OfType<ST_REVIEW_PLAN_POINT>().OrderBy(point => point.HeadNo > 0 ? point.HeadNo : int.MaxValue).ThenBy(point => point.CellNo).ThenBy(point => point.ShotNo).Select(point => new ST_REVIEW_SELECTED_SHOT_ROW(point.HeadNo, point.CellNo, point.ShotNo, ToMatrixShotName(point))).ToArray();
        private void ClearInspectionShots(object? parameter)
        {
            if (!IsBeamInspectionMode || !CanSelectBeam)
            {
                return;
            }

            if (!CRecipeConfirmDialog.ShowConfirmation("Shot Select Clear", "모든 Cell의 Shot 선택을 초기화하시겠습니까?"))
            {
                return;
            }

            _selectedInspectionShotKeys.Clear();
            ResetBeamInspectionResults();
            _selectedRunShotKey = "";
            foreach (var row in _runShotRowsByKey.Values)
            {
                row.SetSelected(false);
            }

            RefreshInspectionCellSelection();
            OnPropertyChanged(nameof(SelectedRunShotName));
            _statusReporter("Beam inspection Shot selection cleared.");
            CRecipeConfirmDialog.ShowCompletion("Shot Select Clear", "CLEAR 되었습니다");
        }

        private void RefreshInspectionCellSelection()
        {
            RefreshBeamInspectionDisplay();
            OnPropertyChanged(nameof(InspectionHeadRows));
            if (!IsShotSelectionMode || _lastRecipe is null || _lastAllPlan is null)
            {
                return;
            }

            var preview = CReviewGlassPreviewBuilder.Build(_lastRecipe, _lastAllPlan.CellCount, showShotPoints: false, expandSmallCellBounds: false, filledCellNos: GetSelectedInspectionCellNos(), settings: _cameraSettings?.LoadSection(EN_SETTING_TAB.Motor));
            RunGlassPreviewImage = preview.Image;
            SetCameraPreviewGeometry(preview);
            RunGlassPreviewInspectionOverlayImage = preview.InspectionOverlayImage;
            RunCellPreviewLabels = preview.CellLabels;
            OnPropertyChanged(nameof(RunGlassPreviewImage));
            OnPropertyChanged(nameof(RunGlassPreviewInspectionOverlayImage));
            OnPropertyChanged(nameof(RunCellPreviewLabels));
        }

        private IReadOnlySet<int> GetSelectedInspectionCellNos() => (IsOneShotReviewMode ? new[]
        {
            _selectedRunShotKey
        }

        : _selectedInspectionShotKeys.AsEnumerable()).Select(key => FindPointByShotKey(_lastAllPlan, key)).OfType<ST_REVIEW_PLAN_POINT>().Select(point => point.CellNo).ToHashSet();
        private void ShowShotBeamPositions(object? parameter)
        {
            if (!IsBeamInspectionMode || IsReviewExecutionActive)
            {
                return;
            }

            var point = FindPointByShotKey(_lastAllPlan, CReviewManager.NormalizeShotKey(parameter?.ToString() ?? ""));
            if (_lastRecipe is null || _lastAllPlan is null || point is null)
            {
                CRecipeConfirmDialog.ShowWarning("Beam Positions", "현재 Recipe의 Shot 정보를 불러온 후 확인해 주세요.");
                return;
            }

            try
            {
                var positions = CReviewManager.GetShotBeamPositions(_lastRecipe, point, _lastAllPlan.StageScanDirectionY);
                var dialog = new CReviewBeamPositionsDialog(point, positions)
                {
                    Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? Application.Current?.MainWindow
                };
                dialog.ShowDialog();
            }
            catch (Exception exception)
            {
                CRecipeConfirmDialog.ShowWarning("Beam 위치 확인 실패", exception.Message);
            }
        }

        private void ShowSelectedInspectionShots(object? parameter)
        {
            if (!IsBeamInspectionMode)
            {
                return;
            }

            if (_lastAllPlan is null && _selectedInspectionShotKeys.Count > 0)
            {
                CRecipeConfirmDialog.ShowWarning("Cur Selected Shot", "현재 Recipe의 Shot 정보를 불러온 후 확인해 주세요.");
                return;
            }

            var dialog = new CSelectedReviewShotsDialog(BuildSelectedInspectionShotRows(), _headCount)
            {
                Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? Application.Current?.MainWindow
            };
            dialog.ShowDialog();
        }

        public CButtonCommand LoadLastBeamInspectionResultCommand { get; }
        public CButtonCommand ClearLoadedBeamInspectionResultCommand { get; }
        public bool CanLoadBeamInspectionResult => IsBeamInspectionMode && !IsSampleSelectTab && !IsReviewExecutionActive && !_isBeamInspectionResultLoading;
        public bool CanClearLoadedBeamInspectionResult => CanLoadBeamInspectionResult && _loadedBeamInspectionResult is not null;

        private int GetInspectionDisplayShotCount() => _loadedBeamInspectionResult?.Shots.Count ?? GetInspectionDisplayShots().Count;
        private ST_BEAM_INSPECTION_RESULT_SHOT? GetLoadedInspectionShot() => _loadedBeamInspectionResult is { Shots.Count: > 0 } file ? file.Shots[Math.Clamp(_beamInspectionBrowseIndex, 0, file.Shots.Count - 1)] : null;
        private async Task LoadLastBeamInspectionResult()
        {
            if (!CanLoadBeamInspectionResult)
                return;
            var generation = ++_beamInspectionResultLoadGeneration;
            var request = new CancellationTokenSource();
            _beamInspectionResultLoadCancellation = request;
            _isBeamInspectionResultLoading = true;
            RefreshBeamInspectionDisplay();
            try
            {
                var loader = _beamInspectionResultLoader;
                var result = await Task.Run(async () => loader is null ? await _reviewManager.LoadLastBeamInspection(request.Token).ConfigureAwait(false) : await loader(request.Token).ConfigureAwait(false), request.Token);
                if (generation != _beamInspectionResultLoadGeneration || !IsBeamInspectionMode || IsSampleSelectTab || IsReviewExecutionActive)
                    return;
                if (result is null)
                {
                    CRecipeConfirmDialog.ShowWarning("Beam Inspection 결과", "저장된 Beam Inspection 결과가 없습니다.");
                    return;
                }

                ApplyLoadedBeamInspectionResult(result);
                _statusReporter($"Beam Inspection 결과 불러오기: {result.FileName}");
            }
            catch (OperationCanceledException)when (request.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (generation == _beamInspectionResultLoadGeneration && IsBeamInspectionMode && !IsSampleSelectTab)
                    CRecipeConfirmDialog.ShowWarning("Beam Inspection 결과 불러오기 실패", exception.Message);
            }
            finally
            {
                if (ReferenceEquals(_beamInspectionResultLoadCancellation, request))
                {
                    _beamInspectionResultLoadCancellation = null;
                    _isBeamInspectionResultLoading = false;
                    RefreshBeamInspectionDisplay();
                }

                request.Dispose();
            }
        }

        private void ApplyLoadedBeamInspectionResult(ST_BEAM_INSPECTION_RESULT_FILE_DATA result)
        {
            if (IsReviewExecutionActive)
                return;
            if (result.Shots.Count == 0)
                throw new InvalidDataException("Beam Inspection 결과에 Shot이 없습니다.");
            _loadedBeamInspectionResult = result;
            _beamInspectionBrowseIndex = 0;
            RefreshBeamInspectionDisplay();
        }

        private void ClearLoadedBeamInspectionResult()
        {
            if (!CanClearLoadedBeamInspectionResult)
                return;
            InvalidateBeamInspectionResultLoad(clearLoaded: true);
            _beamInspectionBrowseIndex = 0;
            RefreshBeamInspectionDisplay();
            _statusReporter("불러온 Beam Inspection 결과 표시를 해제했습니다.");
        }

        private void InvalidateBeamInspectionResultLoad(bool clearLoaded)
        {
            ++_beamInspectionResultLoadGeneration;
            _beamInspectionResultLoadCancellation?.Cancel();
            if (clearLoaded)
                _loadedBeamInspectionResult = null;
        }

        private IReadOnlyList<ST_REVIEW_BEAM_MATRIX_ROW> BuildLoadedInspectionBeamRows()
        {
            var shot = GetLoadedInspectionShot();
            var beams = shot?.Beams.ToDictionary(beam => beam.BeamNo) ?? [];
            return Enumerable.Range(0, 4).Select(row => new ST_REVIEW_BEAM_MATRIX_ROW(row + 1, Enumerable.Range(0, 4).Select(column =>
            {
                var number = row * 4 + column + 1;
                beams.TryGetValue(number, out var beam);
                var measured = beam?.IsMeasured == true;
                var judge = measured ? beam!.Judge : "미검사";
                return new ST_REVIEW_BEAM_ROW(number, $"Beam{number}", row + 1, column + 1, measured, false, "", measured ? judge : "READY", SelectRunBeamCommand)
                {
                    IsInspectionResult = true,
                    InspectionStatus = judge,
                    InspectionResultText = measured ? $"ΔX {beam!.XMm!.Value.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture)}\nΔY {beam.YMm!.Value.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture)}" : ""
                };
            }).ToArray())).ToArray();
        }

#endregion
#region OneShot
        public string SelectedOneShotMethod
        {
            get => _oneShotMethod;
            set
            {
                if (IsReviewExecutionActive || !OneShotMethods.Contains(value) || value == _oneShotMethod)
                    return;
                _oneShotMethod = value;
                _oneShotResult = null;
                OneShotStatus = "검사 대기";
                NotifyOneShot();
            }
        }

        public string OneShotSelection => SelectedOneShotPoint is { } point ? $"HEAD {point.HeadNo:00} / Cell{point.CellNo:00}-{ToMatrixShotName(point)} / Beam{_selectedRunBeamNo}" : "Shot을 선택해 주세요.";

        public string OneShotResultText
        {
            get
            {
                if (_oneShotResult?.Mark is null || _lastAllPlan is null)
                    return "";
                var x = _oneShotResult.Point.ErrorX;
                var y = _oneShotResult.Point.ErrorY;
                var converted = CReviewManager.VisionErrorToStageOffset(x, y, _lastAllPlan.VisionFlipType);
                return $"Recv (mm): X {FormatSigned(x)} / Y {FormatSigned(y)}\n" + $"Vision Flip (mm): X {FormatSigned(converted.X)} / Y {FormatSigned(converted.Y)}\n" + $"RESULT: {_oneShotResult.Point.Judge}" + (_oneShotResult.IsVisionSimulation ? " (SIMULATION)" : "");
            }
        }

        public bool IsShotSelectionMode => IsOneShotReviewMode || IsBeamInspectionMode;
        private ST_REVIEW_PLAN_POINT? SelectedOneShotPoint => FindPointByShotKey(_lastAllPlan, _selectedRunShotKey);

        private void InitializeOneShot()
        {
            OneShotActionCommand = new(value => _ = ExecuteOneShotAction(value?.ToString() ?? ""), value => IsOneShotReviewMode && !IsReviewExecutionActive && SelectedOneShotPoint is { Use: true, HeadNo: > 0 } point && !IsReviewPointExcluded(point) && value?.ToString()is "MOVE" or "INSPECTION" or "MOVE_INSPECTION");
        }

        private void RefreshOneShotSelection(bool clearResult = false)
        {
            if (!IsOneShotReviewMode)
                return;
            if (clearResult)
            {
                _oneShotResult = null;
                OneShotStatus = SelectedOneShotPoint is null ? "Shot을 선택해 주세요." : "동작 대기";
            }

            RunBeamRows = BuildBeamRows(_selectedRunBeamNo);
            OnPropertyChanged(nameof(RunBeamRows));
            NotifyOneShot();
        }

        private void NotifyOneShot()
        {
            OnPropertyChanged(nameof(OneShotSelection));
            OnPropertyChanged(nameof(OneShotStatus));
            OnPropertyChanged(nameof(OneShotResultText));
            OnPropertyChanged(nameof(SelectedOneShotMethod));
            OnPropertyChanged(nameof(IsShotSelectionMode));
            OnPropertyChanged(nameof(IsOneShotReviewMode));
            OnPropertyChanged(nameof(InspectionHeadRows));
            if (IsOneShotReviewMode)
            {
                ResultMatrix = BuildOneShotResultMatrix();
                OnPropertyChanged(nameof(ResultMatrix));
            }

            OneShotActionCommand?.NotifyCanExecuteChanged();
        }

        private DataView BuildOneShotResultMatrix()
        {
            var table = new DataTable("OneShotResult")
            {
                Locale = CultureInfo.InvariantCulture
            };
            foreach (var name in new[]
            {
                "HEAD",
                "CELL",
                "SHOT",
                "BEAM",
                "RESULT"
            }

            )
                table.Columns.Add(name, typeof(string));
            foreach (var name in new[]
            {
                "Recv X (mm)",
                "Recv Y (mm)",
                "Design X (mm)",
                "Design Y (mm)",
                "Target X (pixel)",
                "Target Y (pixel)",
                "Hole X (pixel)",
                "Hole Y (pixel)"
            }

            )
                table.Columns.Add(name, typeof(double));
            if (_oneShotResult?.Mark is { } mark && _lastAllPlan is { } plan)
            {
                var point = _oneShotResult.Point;
                var x = point.ErrorX;
                var y = point.ErrorY;
                var converted = CReviewManager.VisionErrorToStageOffset(x, y, plan.VisionFlipType);
                table.Rows.Add(
                    $"HEAD {point.HeadNo:00}", 
                    $"Cell{point.CellNo:00}", 
                    ToMatrixShotName(point), 
                    $"Beam{point.BeamNo}", 
                    point.Judge, 
                    x, 
                    y, 
                    converted.X, 
                    converted.Y, 
                    mark.TargetMarkX, 
                    mark.TargetMarkY, 
                    mark.HoleX, 
                    mark.HoleY
                );
            }

            return table.DefaultView;
        }

        private IReadOnlyDictionary<int, ST_REVIEW_BEAM_POSITION> OneShotBeamTargets() => IsOneShotReviewMode && _lastRecipe is not null && _lastAllPlan is not null && SelectedOneShotPoint is { } point ? CReviewManager.GetShotBeamPositions(_lastRecipe, point, _lastAllPlan.StageScanDirectionY).ToDictionary(item => item.BeamNo) : new Dictionary<int, ST_REVIEW_BEAM_POSITION>();
        private async Task ExecuteOneShotAction(string action, Func<string, string, bool>? confirm = null, Action<string, string>? warn = null)
        {
            if (!CheckOperationAlarm("ONE SHOT " + action))
                return;
            if (!OneShotActionCommand.CanExecute(action))
                return;
            var operation = action switch
            {
                "MOVE" => EN_ONE_SHOT_ACTION.Move,
                "INSPECTION" => EN_ONE_SHOT_ACTION.Inspection,
                _ => EN_ONE_SHOT_ACTION.MoveAndInspection
            };
            var method = _oneShotMethod == "Mark Find Single" ? EN_ONE_SHOT_METHOD.MarkFindSingle : EN_ONE_SHOT_METHOD.MarkFindSettingSingle;
            confirm ??= (title, message) => CRecipeConfirmDialog.ShowConfirmation(title, message);
            warn ??= (title, message) => CRecipeConfirmDialog.ShowWarning(title, message);
            _oneShotRunning = true;
            NotifyReviewExecutionCommandStates();
            _oneShotCancellation = new CancellationTokenSource();
            var token = _oneShotCancellation.Token;
            try
            {
                var recipe = await LoadSelectedRecipe(token) ?? throw new InvalidOperationException("현재 레시피가 없습니다.");
                token.ThrowIfCancellationRequested();
                if (recipe.Id != _lastRecipe?.Id)
                    throw new InvalidOperationException("Recipe가 변경되었습니다. Shot을 다시 선택해 주세요.");
                var plan = _reviewManager.CreateAllPlan(recipe, _selectedRunBeamNo);
                var point = FindPointByShotKey(plan, _selectedRunShotKey) ?? throw new InvalidOperationException("선택한 Shot이 없습니다.");
                _lastRecipe = recipe;
                _lastAllPlan = plan;
                _lastAllPlanBeamNo = _selectedRunBeamNo;
                RefreshOneShotSelection();
                var title = action switch
                {
                    "MOVE" => "Move",
                    "INSPECTION" => "Inspection",
                    _ => "Move&Inspection"
                };
                var message = $"HEAD {point.HeadNo:00} / Cell{point.CellNo:00}-{ToMatrixShotName(point)} / Beam{point.BeamNo}\n" + (operation == EN_ONE_SHOT_ACTION.Inspection ? "현재 위치에서 검사합니다.\n" : $"목표: Review X {point.ReviewTargetX:0.000} mm / Stage Y {point.ReviewTargetY:0.000} mm\n") + (operation == EN_ONE_SHOT_ACTION.Move ? "이동하시겠습니까?" : $"{_oneShotMethod} 명령으로 {(operation == EN_ONE_SHOT_ACTION.MoveAndInspection ? "이동 후 " : "")}검사하시겠습니까?");
                if (!confirm(title, message))
                    return;
                token.ThrowIfCancellationRequested();
                _oneShotResult = null;
                OneShotStatus = "동작 준비 중";
                NotifyOneShot();
                var dispatcher = Application.Current?.Dispatcher;
                void Progress(string status)
                {
                    void Apply()
                    {
                        OneShotStatus = status;
                        NotifyOneShot();
                    }

                    if (dispatcher is null || dispatcher.CheckAccess())
                        Apply();
                    else
                        dispatcher.Invoke(Apply);
                }

                var result = await _reviewManager.ExecuteOneShot(plan, point, operation, method, Progress, _oneShotCancellation.Token);
                _oneShotResult = result;
                OneShotStatus = operation == EN_ONE_SHOT_ACTION.Move
                    ? result.IsStageSimulation ? "이동 완료 (SIMULATION)" : "이동 완료"
                    : string.IsNullOrWhiteSpace(result.ResultSaveError)
                        ? $"검사 완료 · {result.Point.Judge}"
                        : $"검사 완료 · {result.Point.Judge} / 결과 저장 실패";
                _statusReporter($"{OneShotSelection} · {OneShotStatus}");
                if (!string.IsNullOrWhiteSpace(result.ResultSaveError))
                    warn("One Shot 결과 저장", result.ResultSaveError);
            }
            catch (OperationCanceledException)
            {
                OneShotStatus = "동작 중지";
            }
            catch (Exception error)
            {
                OneShotStatus = error.Message;
                _statusReporter(error.Message);
                warn("One Shot 확인", error.Message);
            }
            finally
            {
                _oneShotCancellation?.Dispose();
                _oneShotCancellation = null;
                _oneShotRunning = false;
                NotifyReviewExecutionCommandStates();
                NotifyOneShot();
            }
        }

#endregion
#region RuntimeDisplay
        public bool IsReviewResultDeferred => _deferResultMatrix && !IsOneShotReviewMode && !IsBeamInspectionMode;

        private DataView GetRuntimeResultMatrix(ST_REVIEW_PLAN plan)
        {
            if (_deferResultMatrix || _reviewManager.SequenceState is EN_REVIEW_SEQUENCE_STATE.Running or EN_REVIEW_SEQUENCE_STATE.Stopping)
                return _emptyRuntimeMatrix;
            if (!ReferenceEquals(_finishedResultPlan, plan) || _finishedResultMatrix is null)
            {
                _finishedResultPlan = plan;
                _finishedResultMatrix = BuildResultMatrix(plan);
            }

            return _finishedResultMatrix;
        }

        private void BeginDeferredResults()
        {
            _resultDisplayGeneration++;
            _deferResultMatrix = true;
            _finishedResultPlan = null;
            _finishedResultMatrix = null;
            ResultMatrix = _emptyRuntimeMatrix;
            OnPropertyChanged(nameof(ResultMatrix));
            OnPropertyChanged(nameof(IsReviewResultDeferred));
        }

        private async Task CompleteDeferredResults()
        {
            var generation = _resultDisplayGeneration;
            var plan = _reviewManager.CurrentPlan;
            // Build from the final immutable snapshot on a worker, including the
            // measured subset of a stopped/failed run. Bind the table exactly once.
            var matrix = plan is null ? _emptyRuntimeMatrix : await Task.Run(() => BuildResultMatrix(plan));
            if (generation != _resultDisplayGeneration)
                return;
            _finishedResultPlan = plan;
            _finishedResultMatrix = matrix;
            _deferResultMatrix = false;
            ResultMatrix = matrix;
            OnPropertyChanged(nameof(ResultMatrix));
            OnPropertyChanged(nameof(IsReviewResultDeferred));
        }

        private void QueueRuntimeDisplayRefresh(ST_REVIEW_PLAN plan)
        {
            // The sequence publishes immutable snapshots on a worker. Keep only the
            // latest state per changed Shot until the next UI tick; never drop results.
            lock (_runtimeUiGate)
            {
                if (_runtimeQueuedId != plan.RuntimeId || plan.PointUpdates is null)
                {
                    _runtimeQueuedId = plan.RuntimeId;
                    _runtimeSeedPlan = plan;
                    _runtimePendingPoints.Clear();
                }

                _runtimePendingPlan = plan;
                foreach (var point in plan.PointUpdates ?? [])
                    _runtimePendingPoints[point.ShotKey] = point;
            }

            if (Interlocked.Exchange(ref _runtimeTimerScheduled, 1) != 0)
                return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                Interlocked.Exchange(ref _runtimeTimerScheduled, 0);
                return;
            }

            _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                lock (_runtimeUiGate)
                {
                    if (_runtimePendingPlan is null)
                    {
                        Interlocked.Exchange(ref _runtimeTimerScheduled, 0);
                        return;
                    }
                }

                if (_runtimeDisplayTimer is null)
                {
                    _runtimeDisplayTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(200)
                    };
                    _runtimeDisplayTimer.Tick += (_, _) => FlushRuntimeDisplay();
                }

                _runtimeDisplayTimer.Start();
            }));
        }

        private void FlushRuntimeDisplay()
        {
            ST_REVIEW_PLAN? plan;
            ST_REVIEW_PLAN? seed;
            ST_REVIEW_PLAN_POINT[] changes;
            lock (_runtimeUiGate)
            {
                plan = _runtimePendingPlan;
                seed = _runtimeSeedPlan;
                changes = _runtimePendingPoints.Values.ToArray();
                _runtimePendingPlan = null;
                _runtimeSeedPlan = null;
                _runtimePendingPoints.Clear();
            }

            if (plan is not null && !IsOneShotReviewMode && !IsBeamInspectionMode)
            {
                _runtimeDisplayRefreshCount++;
                if (seed is not null)
                {
                    _runtimeCurrentPointCells.Clear();
                    foreach (var point in seed.ReviewPoints)
                        if (point.State == EN_REVIEW_POINT_STATE.Current)
                            _runtimeCurrentPointCells[point.ShotKey] = point.CellNo;
                }

                foreach (var point in changes)
                    if (point.State == EN_REVIEW_POINT_STATE.Current)
                        _runtimeCurrentPointCells[point.ShotKey] = point.CellNo;
                    else
                        _runtimeCurrentPointCells.Remove(point.ShotKey);
                _activePlanPointCount = plan.ReviewPoints.Count;
                var currentPoint = _reviewManager.CurrentPoint;
                var measurementPoint = _reviewManager.LastMeasuredPoint;
                SelectedPointItems = BuildSelectedPointItems(measurementPoint);
                if (seed is not null)
                    SynchronizeMaterializedRunShotRows(plan);
                else
                    foreach (var point in changes)
                        UpdateRunShotRuntimeState(point);
                RefreshRuntimePreview(plan, currentPoint ?? measurementPoint);
                OnPropertyChanged(nameof(PlanSummary));
                OnPropertyChanged(nameof(SelectedPointItems));
                NotifyReviewExecutionCommandStates();
            }

            lock (_runtimeUiGate)
            {
                if (_runtimePendingPlan is null && _reviewManager.SequenceState is not (EN_REVIEW_SEQUENCE_STATE.Running or EN_REVIEW_SEQUENCE_STATE.Stopping))
                {
                    _runtimeDisplayTimer?.Stop();
                    Interlocked.Exchange(ref _runtimeTimerScheduled, 0);
                }
            }
        }

        private void RefreshRuntimePreview(ST_REVIEW_PLAN plan, ST_REVIEW_PLAN_POINT? point)
        {
            if (_lastRecipe is null || point is null || !IsRunTab)
                return;
            var cells = _runtimeCurrentPointCells.Values.ToHashSet();
            var active = _reviewManager.SequenceState is EN_REVIEW_SEQUENCE_STATE.Running or EN_REVIEW_SEQUENCE_STATE.Stopping;
            if (_runtimePreviewCells.SetEquals(cells) && _runtimePreviewCell == point.CellNo && _runtimePreviewActive == active && ReferenceEquals(_runtimePreviewBase, RunGlassPreviewImage))
                return;
            var preview = CReviewGlassPreviewBuilder.Build(
                _lastRecipe, 
                plan.CellCount, 
                point.CellNo, 
                visibleCellNos: IsRuleReviewMode ? plan.ReviewPoints.Select(p => p.CellNo).ToHashSet() : null, 
                defaultPointState: IsRuleReviewMode ? EN_REVIEW_POINT_STATE.Skip : EN_REVIEW_POINT_STATE.Ready, 
                showShotPoints: false, 
                expandSmallCellBounds: false, 
                emphasizeCurrentCell: active, 
                currentCellNos: cells
            , settings: _cameraSettings?.LoadSection(EN_SETTING_TAB.Motor));
            // Cell geometry and the base image are static for the run. Only change
            // the highlight overlay/labels when its Cell set actually changes.
            if (RunGlassPreviewImage is null)
            {
                RunGlassPreviewImage = preview.Image;
                SetCameraPreviewGeometry(preview);
                OnPropertyChanged(nameof(RunGlassPreviewImage));
            }

            RunGlassPreviewInspectionOverlayImage = preview.InspectionOverlayImage;
            RunCellPreviewLabels = preview.CellLabels;
            OnPropertyChanged(nameof(RunGlassPreviewInspectionOverlayImage));
            OnPropertyChanged(nameof(RunCellPreviewLabels));
            _runtimePreviewCells = cells.ToHashSet();
            _runtimePreviewCell = point.CellNo;
            _runtimePreviewActive = active;
            _runtimePreviewBase = RunGlassPreviewImage;
        }

        private void RefreshRunCellDetail()
        {
            if (_lastRecipe is not null && _lastAllPlan is not null)
            {
                var runtimePlan = _reviewManager.CurrentPlan;
                var reviewPlan = IsReviewExecutionActive && runtimePlan is not null && runtimePlan.RecipeId.Equals(_lastRecipe.Id, StringComparison.OrdinalIgnoreCase) && (runtimePlan.ReviewPoints.FirstOrDefault()?.BeamNo ?? _selectedRunBeamNo) == _selectedRunBeamNo ? runtimePlan : ResolveReviewPlan(_lastRecipe, _lastAllPlan);
                RunCellShotMatrixRows = BuildRunCellShotMatrixRows(reviewPlan, _selectedRunCellNo);
                RunCellShotRows = [];
            }

            OnPropertyChanged(nameof(IsRunGlassPreviewVisible));
            OnPropertyChanged(nameof(IsRunCellDetailVisible));
            OnPropertyChanged(nameof(IsRunShotMapVisible));
            OnPropertyChanged(nameof(RunWorkspaceTitle));
            OnPropertyChanged(nameof(RunWorkspaceSummary));
            OnPropertyChanged(nameof(SelectedRunShotName));
            OnPropertyChanged(nameof(RunCellShotRows));
            OnPropertyChanged(nameof(RunCellShotMatrixRows));
        }

        private ST_REVIEW_PLAN ResolveReviewPlan(ST_RECIPE_DEFINITION recipe, ST_REVIEW_PLAN allPlan)
        {
            if (IsOneShotReviewMode)
            {
                return allPlan;
            }

            if (IsBeamInspectionMode)
            {
                // Browse every Cell's Shots independently of the inspection execution plan.
                return allPlan;
            }

            var currentPlan = _reviewManager.CurrentPlan;
            // ALL SHOT previously returned a newly created all-plan unconditionally.
            // Once a run began that discarded the manager's working plan, so the Run
            // screen immediately reverted every point to READY and lost the live marker.
            if (IsRunTab && _selectedMode.Equals("ALL SHOT", StringComparison.OrdinalIgnoreCase))
            {
                if (currentPlan is not null && currentPlan.RecipeId.Equals(recipe.Id, StringComparison.OrdinalIgnoreCase) && currentPlan.ReviewPoints.Count == allPlan.ReviewPoints.Count && currentPlan.ReviewPoints.All(point => point.BeamNo == _selectedRunBeamNo))
                {
                    return currentPlan;
                }

                return allPlan;
            }

            var targetKeys = GetPlanShotKeys(allPlan).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (currentPlan is not null && currentPlan.RecipeId.Equals(recipe.Id, StringComparison.OrdinalIgnoreCase) && currentPlan.ReviewPoints.Count == targetKeys.Count && currentPlan.ReviewPoints.All(point => point.BeamNo == _selectedRunBeamNo) && currentPlan.ReviewPoints.Select(point => point.ShotKey).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(targetKeys))
            {
                return currentPlan;
            }

            return _reviewManager.CreatePlan(recipe, targetKeys, _selectedRunBeamNo);
        }

        private async Task StartReviewSequence(double shiftOffsetX, double shiftOffsetY)
        {
            if (!CheckOperationAlarm("REVIEW START"))
                return;
            if (IsOneShotReviewMode)
                return;
            if (IsBeamInspectionMode)
            {
                await StartBeamInspectionSequence(shiftOffsetX, shiftOffsetY);
                return;
            }

            if (IsSampleSelectTab)
            {
                _statusReporter("Rule Setting is for configuring review shots. Start Review from the Run tab.");
                return;
            }

            if (IsReviewExecutionActive)
            {
                _statusReporter("Review sequence is already running. Stop it before starting another review.");
                return;
            }

            _isStartRequestPending = true;
            _reviewCancellation = new CancellationTokenSource();
            var token = _reviewCancellation.Token;
            NotifyReviewExecutionCommandStates();
            _refreshScreen();
            try
            {
                var isSampleSelectRun = IsSampleSelectTab;
                if (isSampleSelectRun && _selectedSampleShotKeys.Count == 0)
                {
                    _statusReporter("Select at least one rule shot before starting Rule review.");
                    _refreshScreen();
                    return;
                }

                if (isSampleSelectRun)
                {
                    _selectedTab = "RUN";
                    _selectedMode = "SAMPLE SHOT";
                    _selectedRuleType = EN_REVIEW_RULE_TYPE.SamplePoint;
                    _isSampleCellDetailVisible = false;
                    _isRunCellDetailVisible = false;
                    _selectedRunShotKey = "";
                    _statusReporter($"Sample review started: {_selectedSampleShotKeys.Count} shots selected.");
                    _refreshScreen();
                }

                var recipe = await LoadSelectedRecipe(token) ?? CreateFallbackRecipe();
                ApplySplitBeamContext(recipe);
                var allPlan = _reviewManager.CreateAllPlan(recipe, _selectedRunBeamNo, shiftOffsetX, shiftOffsetY);
                ApplyRecipeContext(recipe, allPlan);
                var planShotKeys = _selectedMode.Equals("ALL SHOT", StringComparison.OrdinalIgnoreCase) ? null : GetPlanShotKeys(allPlan);
                if (planShotKeys is { Count: 0 })
                {
                    _statusReporter(IsSimpleSampleRuleUi ? "Select one processed Shot for SIMPLE Review before starting." : $"{_selectedSampleRuleUi} has no executable Review points.");
                    return;
                }

                var reviewPlan = planShotKeys is null
                    ? allPlan
                    : _reviewManager.CreatePlan(recipe, planShotKeys, _selectedRunBeamNo, shiftOffsetX, shiftOffsetY);
                reviewPlan = ConfigureReviewCorrectionPlan(reviewPlan, allPlan);
                reviewPlan = reviewPlan with
                {
                    InspectionMethod = _selectedInspectionMethod
                };
                _selectedRunShotKey = "";
                foreach (var row in _runShotRowsByKey.Values)
                {
                    row.SetSelected(false);
                }

                OnPropertyChanged(nameof(SelectedRunShotName));
                var productLink = await ResolveReviewProduct(reviewPlan, token);
                if (string.IsNullOrWhiteSpace(productLink.ProductId))
                {
                    _statusReporter($"Review start blocked: {productLink.Message}");
                    CRecipeConfirmDialog.ShowWarning("검사 시작 불가", "Review 가능한 Glass가 없습니다.\nGlass 연결 상태를 확인해 주세요.");
                    return;
                }

                ST_PRODUCT_REVIEW_DATA? checkpoint = null;
                var resumeCheckpoint = false;
                checkpoint = await _reviewManager.FindIncompleteProductReview(reviewPlan, productLink.ProductId, token);
                if (checkpoint is not null)
                {
                    var choice = CRecipeConfirmDialog.ShowReviewResumeChoice($"중단된 Review 검사가 있습니다. (완료 {checkpoint.Results.Count}/{checkpoint.TotalCount})\n" + (_selectedInspectionMethod == InspectionOnFlyMethod ? "이어서 검사: 미완료 라인을 현재 카메라 위치에 가까운 끝에서 다시 검사합니다.\n" : "이어서 검사: 완료한 Shot은 유지하고 미검사 Shot부터 검사합니다.\n") + "이어서 검사할지 처음부터 검사할지 선택해 주세요.");
                    if (choice == EN_REVIEW_RESUME_CHOICE.Cancel)
                    {
                        _statusReporter("Review start canceled.");
                        return;
                    }

                    resumeCheckpoint = choice == EN_REVIEW_RESUME_CHOICE.Resume;
                    _statusReporter(resumeCheckpoint ? $"Review resumes: {checkpoint.Results.Count}/{checkpoint.TotalCount}." : "Review restarts from the beginning.");
                }

                BeginDeferredResults();
                var status = await Task.Run(() => _reviewManager.Start(reviewPlan, productLink.ProductId, checkpoint, resumeCheckpoint, QueueRuntimeDisplayRefresh, token));
                FlushRuntimeDisplay();
                await CompleteDeferredResults();
                if (status.State == EN_REVIEW_SEQUENCE_STATE.Failed)
                {
                    ReportAndShowReviewFailure(status.Message);
                }
                else
                {
                    _statusReporter($"{status.Message} ({status.CompletedCount}/{status.TotalCount}, NG={status.NgCount}) " + $"[{productLink.Message}]");
                }

                _refreshScreen();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _statusReporter("Review 검사 중지 요청을 처리했습니다.");
            }
            catch (Exception exception)
            {
                ReportAndShowReviewFailure(exception.Message);
            }
            finally
            {
                _reviewCancellation?.Dispose();
                _reviewCancellation = null;
                _isStartRequestPending = false;
                _deferResultMatrix = false;
                OnPropertyChanged(nameof(IsReviewResultDeferred));
                NotifyReviewExecutionCommandStates();
                _refreshScreen();
            }
        }

        private Task<(string? ProductId, string Message)> ResolveReviewProduct(ST_REVIEW_PLAN reviewPlan, CancellationToken cancellationToken)
        {
            var product = _productManager.Current;
            if (product is null || string.IsNullOrWhiteSpace(product.GlassId))
            {
                return Task.FromResult<(string? ProductId, string Message)>((null, "UNLINKED REVIEW: Glass is unavailable"));
            }

            if (product.Result == EN_PRODUCT_RESULT.Skipped)
            {
                return Task.FromResult<(string? ProductId, string Message)>((null, "UNLINKED REVIEW: Product process was skipped"));
            }

            if (!string.IsNullOrWhiteSpace(product.RecipeId) && !product.RecipeId.Equals(reviewPlan.RecipeId, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<(string? ProductId, string Message)>((null, "UNLINKED REVIEW: Product Recipe does not match"));
            }

            var station = _stationManager.GetStatus(cancellationToken);
            if (string.IsNullOrWhiteSpace(product.ProcessId))
            {
                return Task.FromResult<(string? ProductId, string Message)>((product.GlassId, $"PRODUCT: {product.GlassId}"));
            }

            if (station.ProcessModel is not null && product.ProcessId.Equals(station.ProcessModel.ProcessId, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<(string? ProductId, string Message)>((product.GlassId, $"PRODUCT: {product.GlassId}"));
            }

            if (product.State == EN_PRODUCT_STATE.Completed)
            {
                return Task.FromResult<(string? ProductId, string Message)>((product.GlassId, $"LAST COMPLETED PRODUCT: {product.GlassId}"));
            }

            return Task.FromResult<(string? ProductId, string Message)>((null, "UNLINKED REVIEW: Product process does not match the current Station process"));
        }

        private void NotifyReviewExecutionCommandStates()
        {
            OneShotActionCommand?.NotifyCanExecuteChanged();
            StartCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            RetryCommand.NotifyCanExecuteChanged();
            SelectTabCommand.NotifyCanExecuteChanged();
            SelectModeCommand.NotifyCanExecuteChanged();
            OpenCorrectionReviewCommand.NotifyCanExecuteChanged();
            SelectRunBeamCommand.NotifyCanExecuteChanged();
            SelectAllInspectionBeamsCommand.NotifyCanExecuteChanged();
            ClearInspectionBeamsCommand.NotifyCanExecuteChanged();
            ShowSelectedInspectionShotsCommand.NotifyCanExecuteChanged();
            ClearInspectionShotsCommand.NotifyCanExecuteChanged();
            ShowShotBeamPositionsCommand.NotifyCanExecuteChanged();
            SelectRunShotCommand.NotifyCanExecuteChanged();
            PreviousInspectionShotCommand.NotifyCanExecuteChanged();
            NextInspectionShotCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSelectBeam));
            OnPropertyChanged(nameof(CanStopOneShot));
            RefreshBeamInspectionDisplay();
            SelectInspectionMethodCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSelectInspectionMethod));
            TogglePointCommand.NotifyCanExecuteChanged();
            SetSampleShotSelectionCommand.NotifyCanExecuteChanged();
            _refreshNavigationAccess();
        }

        private void StopReviewSequence()
        {
            _oneShotCancellation?.Cancel();
            _beamInspectionCancellation?.Cancel();
            _reviewCancellation?.Cancel();
            _reviewManager.Stop();
            NotifyReviewExecutionCommandStates();
            _statusReporter("Review sequence stop requested.");
            _refreshScreen();
        }

        private async Task RetryRemainingReviewPoints()
        {
            if (!CheckOperationAlarm("REVIEW RETRY"))
                return;
            if (IsReviewExecutionActive)
                return;
            if (IsOneShotReviewMode)
                return;
            if (IsBeamInspectionMode)
            {
                _statusReporter("BEAM INSPECTION 검사 동작은 아직 연결되지 않았습니다.");
                return;
            }

            _isStartRequestPending = true;
            _reviewCancellation = new CancellationTokenSource();
            var token = _reviewCancellation.Token;
            NotifyReviewExecutionCommandStates();
            try
            {
                BeginDeferredResults();
                var status = await Task.Run(() => _reviewManager.RetryRemaining(QueueRuntimeDisplayRefresh, token));
                FlushRuntimeDisplay();
                await CompleteDeferredResults();
                if (status.State == EN_REVIEW_SEQUENCE_STATE.Failed)
                {
                    ReportAndShowReviewFailure(status.Message);
                }
                else
                {
                    _statusReporter($"{status.Message} ({status.CompletedCount}/{status.TotalCount}, NG={status.NgCount})");
                }

                _refreshScreen();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _statusReporter("Review 검사 중지 요청을 처리했습니다.");
            }
            catch (Exception exception)
            {
                ReportAndShowReviewFailure(exception.Message);
            }
            finally
            {
                _reviewCancellation?.Dispose();
                _reviewCancellation = null;
                _isStartRequestPending = false;
                _deferResultMatrix = false;
                OnPropertyChanged(nameof(IsReviewResultDeferred));
                NotifyReviewExecutionCommandStates();
            }
        }

        private void ReportAndShowReviewFailure(string message)
        {
            var failureMessage = CReviewFailureMessageBuilder.Create(message);
            _statusReporter($"Review failed: {failureMessage.Stage} / {failureMessage.Cause}");
            CRecipeConfirmDialog.ShowWarning("검사 실패", failureMessage.ToDisplayText());
        }

        private void RefreshRunDisplayFromCachedContext(bool rebuildAllPlan = false)
        {
            if (!IsRunTab || _lastRecipe is null || _lastAllPlan is null)
            {
                _refreshScreen();
                return;
            }

            try
            {
                var allPlan = _lastAllPlan;
                if (rebuildAllPlan || _lastAllPlanBeamNo != _selectedRunBeamNo)
                {
                    allPlan = _reviewManager.CreateAllPlan(_lastRecipe, _selectedRunBeamNo);
                    _lastAllPlan = allPlan;
                    _lastAllPlanBeamNo = _selectedRunBeamNo;
                }

                ApplyScreenData(_lastRecipe, allPlan);
                OnPropertyChanged(nameof(PlanSummary));
                OnPropertyChanged(nameof(Modes));
                OnPropertyChanged(nameof(TargetItems));
                OnPropertyChanged(nameof(SelectionSummaryItems));
                OnPropertyChanged(nameof(SelectedPointItems));
                OnPropertyChanged(nameof(RunGlassPreviewImage));
                OnPropertyChanged(nameof(RunGlassPreviewInspectionOverlayImage));
                OnPropertyChanged(nameof(RunCellPreviewLabels));
                OnPropertyChanged(nameof(RunGlassPreviewSummary));
                OnPropertyChanged(nameof(RunWorkspaceTitle));
                OnPropertyChanged(nameof(RunWorkspaceSummary));
                OnPropertyChanged(nameof(RunCellShotRows));
                OnPropertyChanged(nameof(RunCellShotMatrixRows));
                OnPropertyChanged(nameof(RunBeamRows));
                OnPropertyChanged(nameof(SelectedBeam));
                OnPropertyChanged(nameof(BeamSelectionSummary));
                OnPropertyChanged(nameof(IsAllShotReviewMode));
                OnPropertyChanged(nameof(IsRuleReviewMode));
                OnPropertyChanged(nameof(IsBeamInspectionMode));
                OnPropertyChanged(nameof(IsBeamSelectionVisible));
                OnPropertyChanged(nameof(IsLastMeasurementVisible));
                OnPropertyChanged(nameof(SampleRuleSelectionSummary));
                OnPropertyChanged(nameof(SampleInspectionShotCount));
                OnPropertyChanged(nameof(SampleOffsetApplyCount));
                OnPropertyChanged(nameof(ResultMatrix));
                NotifyOneShot();
                StartCommand.NotifyCanExecuteChanged();
            }
            catch (Exception exception)
            {
                _planBuildError = exception.Message;
                _statusReporter($"Review plan cannot be refreshed: {_planBuildError}");
                _refreshScreen();
            }
        }

        private void ApplyScreenData(ST_RECIPE_DEFINITION recipe, ST_REVIEW_PLAN allPlan)
        {
            _selectedInspectionShotKeys.RemoveWhere(key => FindPointByShotKey(allPlan, key)is not { } point || IsReviewPointExcluded(point));
            if (FindPointByShotKey(allPlan, _selectedRunShotKey)is { } selectedPoint && IsReviewPointExcluded(selectedPoint))
            {
                _selectedRunShotKey = "";
            }

            // Sample Select only edits Shot keys. Building an executable plan here would
            // materialize every selected point (over one million with a single DOE beam).
            var reviewPlan = IsSampleSelectTab ? allPlan with
            {
                Points = []
            }

            : ResolveReviewPlan(recipe, allPlan);
            var activePointCount = IsSampleSelectTab ? GetSelectedSampleShotCount() : IsOneShotReviewMode ? (SelectedOneShotPoint is null ? 0 : 1) : reviewPlan.ReviewPoints.Count;
            var hasRuntimePlan = !IsOneShotReviewMode && ReferenceEquals(reviewPlan, _reviewManager.CurrentPlan);
            var currentPoint = hasRuntimePlan ? _reviewManager.CurrentPoint : null;
            var runtimePreviewPoints = hasRuntimePlan ? new[]
            {
                currentPoint,
                _reviewManager.LastMeasuredPoint
            }.Where(point => point is not null).Select(point => point!).DistinctBy(point => point.ShotKey, StringComparer.OrdinalIgnoreCase).ToArray() : [];
            var currentCellNos = hasRuntimePlan && IsReviewExecutionActive ? GetCurrentCellNos(reviewPlan) : null;
            _selectedRunCellNo = Math.Clamp(_selectedRunCellNo, 1, Math.Max(1, allPlan.CellCount));
            IReadOnlySet<int>? visibleRunCellNos = _selectedMode.Equals("SAMPLE SHOT", StringComparison.OrdinalIgnoreCase) ? reviewPlan.ReviewPoints.Select(point => point.CellNo).ToHashSet() : null;
            var emptyPreview = new ST_REVIEW_GLASS_PREVIEW(null, null, [], "");
            var glassPreview = IsRunTab ? CReviewGlassPreviewBuilder.Build(
                recipe, 
                allPlan.CellCount, 
                currentPoint?.CellNo ?? _reviewManager.LastMeasuredPoint?.CellNo ?? 0, 
                runtimePreviewPoints, 
                visibleCellNos: visibleRunCellNos, 
                defaultPointState: _selectedMode.Equals("SAMPLE SHOT", StringComparison.OrdinalIgnoreCase) ? EN_REVIEW_POINT_STATE.Skip : EN_REVIEW_POINT_STATE.Ready, 
                showShotPoints: false, 
                currentHolePoint: currentPoint ?? _reviewManager.LastMeasuredPoint, 
                expandSmallCellBounds: false, 
                emphasizeCurrentCell: IsReviewExecutionActive, 
                currentCellNos: currentCellNos, 
                filledCellNos: IsShotSelectionMode ? GetSelectedInspectionCellNos() : null
            , settings: _cameraSettings?.LoadSection(EN_SETTING_TAB.Motor)) : emptyPreview;
            _selectedSampleCellNo = Math.Clamp(_selectedSampleCellNo, 1, Math.Max(1, allPlan.CellCount));
            SetSampleMapColumnCount(CalculateShotMapColumnCount(allPlan, _selectedSampleCellNo));
            var simpleReferencePoint = IsSimpleSampleRuleUi ? GetSimpleReferencePoint() : null;
            var sampleGlassPreview = IsSampleSelectTab ? CReviewGlassPreviewBuilder.Build(
                recipe, 
                allPlan.CellCount, 
                _isSampleCellDetailVisible ? _selectedSampleCellNo : simpleReferencePoint?.CellNo ?? 0, 
                useSampleSelectionColors: true, 
                selectedShotKeys: _selectedSampleShotKeys, 
                selectAllShots: false, 
                showShotPoints: false, 
                inspectionPoints: GetAutomaticInspectionPreviewPoints(allPlan), 
                inspectionRule: GetAutomaticInspectionPreviewRule(), 
                scopeTargets: GetRoughScopeTargets(allPlan), 
                scopePoints: ShouldDrawSampleScope() ? allPlan.Points : null, 
                scopeHeadNo: simpleReferencePoint?.HeadNo ?? 0, 
                scopeCellNo: IsSimpleSampleRuleUi && _sampleRuleScope == "HEAD_CELL" ? simpleReferencePoint?.CellNo ?? 0 : 0, 
                headScopes: ShouldDrawSampleScope() ? _reviewManager.GetHeadScopes(recipe) : null, 
                currentHolePoint: IsSimpleSampleRuleUi ? GetSimpleReferencePoint() : null, 
                expandSmallCellBounds: false
            , settings: _cameraSettings?.LoadSection(EN_SETTING_TAB.Motor)) : emptyPreview;
            _activePlanPointCount = activePointCount;
            Tabs = [new("RUN", IsRunTab, SelectTabCommand)];
            Modes = [Mode("ALL SHOT", ""), Mode("ONE SHOT", ""), Mode("BEAM INSPECTION", "")];
            TargetItems = [
                new("Recipe Name", reviewPlan.RecipeId), 
                new("Glass ID", _productManager.Current?.GlassId ?? "-"), 
                new("Tolerance", $"X ±{reviewPlan.ToleranceX:0.000} mm\nY ±{reviewPlan.ToleranceY:0.000} mm")
            ];
            SelectionSummaryItems = IsSampleSelectTab ? CreateSamplePlanSummaryItems() : [
                new("Total Shot", allPlan.Points.Count.ToString(CultureInfo.InvariantCulture)), 
                new("Selected", activePointCount.ToString(CultureInfo.InvariantCulture)), 
                new("Review Mode", ToDisplayModeName(_selectedMode)), 
                new("Selection Rule", GetSelectionRuleText()), 
                new("Expected Time", $"{Math.Max(1L, (long)activePointCount * 3L)} sec"), 
                new("Output", "Review Plan")
            ];
            PointSelectRows = [];
            SampleCellShotRows = IsSampleCellDetailVisible ? [] : [];
            _sampleShotRowsByKey.Clear();
            SampleCellShotMatrixRows = IsSampleCellDetailVisible ? CreateLazySampleCellShotRows(allPlan, _selectedSampleCellNo) : [];
            var selectedRunPoint = string.IsNullOrWhiteSpace(_selectedRunShotKey) ? null : FindPointByShotKey(reviewPlan, _selectedRunShotKey);
            var runMeasurementPoint = ResolveRunMeasurementDisplayPoint(IsReviewExecutionActive, currentPoint, hasRuntimePlan ? _reviewManager.LastMeasuredPoint : null, selectedRunPoint, reviewPlan.ReviewPoints.FirstOrDefault());
            SelectedPointItems = BuildSelectedPointItems(runMeasurementPoint);
            RunGlassPreviewImage = glassPreview.Image;
            SetCameraPreviewGeometry(glassPreview);
            RunGlassPreviewInspectionOverlayImage = glassPreview.InspectionOverlayImage;
            RunCellPreviewLabels = glassPreview.CellLabels;
            OnPropertyChanged(nameof(InspectionHeadRows));
            RunGlassPreviewSummary = glassPreview.Summary;
            if (!IsRunCellDetailVisible)
            {
                RunCellShotMatrixRows = [];
                _runShotRowsByKey = new Dictionary<string, ST_REVIEW_RUN_SHOT_ROW>(StringComparer.OrdinalIgnoreCase);
            }
            else if (IsReviewExecutionActive && RunCellShotMatrixRows.Count > 0)
            {
                // Keep the collection currently owned by WPF. Replacing it during a
                // running sequence disconnects the visible tiles from the runtime
                // update dictionary until the operator leaves and re-enters the Cell.
                if (hasRuntimePlan)
                {
                    SynchronizeMaterializedRunShotRows(reviewPlan);
                }
            }
            else
            {
                RunCellShotMatrixRows = BuildRunCellShotMatrixRows(reviewPlan, _selectedRunCellNo);
            }

            RunCellShotRows = [];
            RunBeamRows = BuildBeamRows(_selectedRunBeamNo);
            SampleGlassPreviewImage = sampleGlassPreview.Image;
            SampleGlassPreviewInspectionOverlayImage = sampleGlassPreview.InspectionOverlayImage;
            SampleCellPreviewLabels = sampleGlassPreview.CellLabels;
            SampleGlassPreviewSummary = BuildSampleGlassPreviewSummary();
            // A newly created all-plan contains only READY points. Building the
            // matrix would enumerate and materialize every lazy Recipe Shot even
            // though there is no result to display yet.
            ResultMatrix = IsOneShotReviewMode ? BuildOneShotResultMatrix() : hasRuntimePlan ? GetRuntimeResultMatrix(reviewPlan) : CreateEmptyResultMatrix();
        }

        private IReadOnlyList<ST_DISPLAY_ITEM> BuildSelectedPointItems(ST_REVIEW_PLAN_POINT? selectedPoint)
        {
            if (selectedPoint is null)
            {
                return[
                    new("Measured Shot", "-"), 
                    new("Selected Beam", $"Beam{_selectedRunBeamNo}"), 
                    new("Move Target", "Review X - / Stage Y -"), 
                    new("Error Amount", "X - / Y -", "WAIT"), 
                    new("Judge", "WAIT")
                ];
            }

            var errorX = selectedPoint.ErrorX;
            var errorY = selectedPoint.ErrorY;
            if (IsReviewPointExcluded(selectedPoint))
            {
                return[
                    new("Measured Shot", $"Cell{selectedPoint.CellNo}-{ToMatrixShotName(selectedPoint)}"), 
                    new("Selected Beam", $"Beam{selectedPoint.BeamNo}"), 
                    new("Move Target", "Excluded from Review"), 
                    new("Error Amount", "X - / Y -", "SKIP"), 
                    new("Judge", $"EXCLUDED / {selectedPoint.ExclusionReason}", "SKIP")
                ];
            }

            var judge = string.IsNullOrWhiteSpace(selectedPoint.Judge) ? "WAIT" : selectedPoint.Judge;
            return[
                new("Measured Shot", $"Cell{selectedPoint.CellNo}-{ToMatrixShotName(selectedPoint)}"), 
                new("Selected Beam", $"Beam{selectedPoint.BeamNo}"), 
                new("Move Target", $"Review X {FormatSigned(selectedPoint.ReviewTargetX)} / Stage Y {FormatSigned(selectedPoint.ReviewTargetY)}"), 
                new("Error Amount", $"X {FormatSigned(errorX)} / Y {FormatSigned(errorY)}", judge), 
                new("Judge", judge)
            ];
        }

        private static ST_REVIEW_PLAN_POINT? ResolveRunMeasurementDisplayPoint(
            bool isExecutionActive, 
            ST_REVIEW_PLAN_POINT? currentPoint, 
            ST_REVIEW_PLAN_POINT? lastMeasuredPoint, 
            ST_REVIEW_PLAN_POINT? selectedPoint, 
            ST_REVIEW_PLAN_POINT? fallbackPoint
        )
        {
            return isExecutionActive ? lastMeasuredPoint : currentPoint ?? selectedPoint ?? lastMeasuredPoint ?? fallbackPoint;
        }

        private static string ResolveRunShotKeyForBeamChange(string selectedShotKey, ST_REVIEW_PLAN_POINT? currentPoint, ST_REVIEW_PLAN_POINT? lastMeasuredPoint)
        {
            if (!string.IsNullOrWhiteSpace(selectedShotKey))
            {
                return selectedShotKey;
            }

            return currentPoint?.ShotKey ?? lastMeasuredPoint?.ShotKey ?? "";
        }

        private static IReadOnlySet<int> GetCurrentCellNos(ST_REVIEW_PLAN plan)
        {
            var currentCellNos = new HashSet<int>();
            foreach (var point in plan.ReviewPoints)
            {
                if (point.State == EN_REVIEW_POINT_STATE.Current && point.CellNo > 0)
                {
                    currentCellNos.Add(point.CellNo);
                }
            }

            return currentCellNos;
        }

        private IReadOnlyList<ST_REVIEW_RUN_SHOT_MATRIX_ROW> BuildRunCellShotMatrixRows(ST_REVIEW_PLAN reviewPlan, int cellNo)
        {
            var points = GetCellPoints(reviewPlan, cellNo);
            if (points.Count == 0)
            {
                return[];
            }

            var columnCount = Math.Max(1, points[0].PixelCountX);
            var rowCount = (points.Count + columnCount - 1) / columnCount;
            var materializedRows = new Dictionary<string, ST_REVIEW_RUN_SHOT_ROW>(StringComparer.OrdinalIgnoreCase);
            _runShotRowsByKey = materializedRows;
            return Enumerable.Range(0, rowCount).Select(rowIndex =>
            {
                var start = rowIndex * columnCount;
                var count = Math.Min(columnCount, points.Count - start);
                var shots = Enumerable.Range(0, count).Select(offset => points[start + offset]).Select(point => new ST_REVIEW_RUN_SHOT_ROW(
                    point.ShotKey, 
                    ToMatrixShotName(point), 
                    ToRunShotDetail(point), 
                    ToRunShotState(point), 
                    point.State == EN_REVIEW_POINT_STATE.Current, 
                    IsBeamInspectionMode ? _selectedInspectionShotKeys.Contains(point.ShotKey) : point.ShotKey.Equals(_selectedRunShotKey, StringComparison.OrdinalIgnoreCase), 
                    point.ExclusionReason, 
                    SelectRunShotCommand
                ) { ShowBeamPositionsCommand = ShowShotBeamPositionsCommand }).ToArray();
                foreach (var shot in shots)
                {
                    materializedRows[shot.ShotKey] = shot;
                }

                return new ST_REVIEW_RUN_SHOT_MATRIX_ROW(rowIndex + 1, shots);
            }).ToArray();
        }

        private void UpdateRunShotRuntimeState(ST_REVIEW_PLAN_POINT? point)
        {
            if (point is null || !_runShotRowsByKey.TryGetValue(point.ShotKey, out var row))
            {
                return;
            }

            row.SetRuntimeState(ToRunShotDetail(point), ToRunShotState(point), point.State == EN_REVIEW_POINT_STATE.Current);
        }

        private void SynchronizeMaterializedRunShotRows(ST_REVIEW_PLAN runtimePlan)
        {
            if (RunCellShotMatrixRows.Count == 0)
            {
                return;
            }

            Dictionary<string, ST_REVIEW_PLAN_POINT>? runtimePointsByKey = null;
            var isFullPlan = _lastAllPlan is not null && runtimePlan.Points.Count == _lastAllPlan.Points.Count && runtimePlan.CellCount > 0;
            if (!isFullPlan)
            {
                runtimePointsByKey = runtimePlan.ReviewPoints.ToDictionary(point => point.ShotKey, StringComparer.OrdinalIgnoreCase);
            }

            foreach (var matrixRow in RunCellShotMatrixRows)
            {
                foreach (var row in matrixRow.Shots)
                {
                    var point = isFullPlan ? FindPointByShotKey(runtimePlan, row.ShotKey) : runtimePointsByKey!.GetValueOrDefault(row.ShotKey);
                    if (point is null)
                    {
                        continue;
                    }

                    row.SetRuntimeState(ToRunShotDetail(point), ToRunShotState(point), point.State == EN_REVIEW_POINT_STATE.Current);
                }
            }
        }

        private IReadOnlyList<ST_REVIEW_PLAN_POINT> GetCellPoints(ST_REVIEW_PLAN plan, int cellNo)
        {
            return plan.GetCellPoints(cellNo);
        }

        private IReadOnlyList<ST_REVIEW_BEAM_MATRIX_ROW> BuildBeamRows(int selectedBeamNo)
        {
            if (IsBeamInspectionMode && _loadedBeamInspectionResult is not null)
                return BuildLoadedInspectionBeamRows();
            var inspectionResults = GetDisplayedInspectionResults();
            var targets = OneShotBeamTargets();
            return Enumerable.Range(0, _splitBeamCount).Select(row => new ST_REVIEW_BEAM_MATRIX_ROW(row + 1, Enumerable.Range(0, _splitBeamCount).Select(column =>
            {
                var beamNo = (row * _splitBeamCount) + column + 1;
                inspectionResults.TryGetValue(beamNo, out var beamResult);
                var isSelected = IsBeamInspectionMode ? IsInspectionExecutionViewVisible && _beamInspectionSnapshot is not null ? beamResult is not null : _selectedInspectionBeamNos.Contains(beamNo) : beamNo == selectedBeamNo;
                return new ST_REVIEW_BEAM_ROW(
                    beamNo, 
                    $"Beam{beamNo}", 
                    row + 1, 
                    column + 1, 
                    isSelected, 
                    beamResult?.State == EN_REVIEW_POINT_STATE.Current, 
                    isSelected ? "SELECTED" : "", 
                    beamResult is null ? "READY" : beamResult.Judge == "ERROR" ? "ERROR" : ToStateText(beamResult.State), 
                    SelectRunBeamCommand
                )
                {
                    TargetX = targets.TryGetValue(beamNo, out var target) ? target.ReviewX.ToString("0.000", CultureInfo.InvariantCulture) : "-",
                    TargetY = target is not null ? target.StageY.ToString("0.000", CultureInfo.InvariantCulture) : "-",
                    IsInspectionResult = IsInspectionExecutionViewVisible,
                    InspectionStatus = !isSelected ? "미선택" : beamResult?.Judge == "ERROR" ? "ERROR" : beamResult?.State switch
                    {
                        EN_REVIEW_POINT_STATE.Current => "측정 중",
                        EN_REVIEW_POINT_STATE.Ok => "OK",
                        EN_REVIEW_POINT_STATE.Ng => "NG",
                        _ => "대기"
                    },
                    InspectionResultText = beamResult?.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng ? $"ΔX {FormatSigned(beamResult.ErrorX)}\nΔY {FormatSigned(beamResult.ErrorY)}" : ""
                };
            }).ToArray())).ToArray();
        }

        private static int GetMatrixColumnIndex(ST_REVIEW_PLAN_POINT point)
        {
            return (Math.Max(1, point.ShotNo) - 1) % Math.Max(1, point.PixelCountX);
        }

        private static int GetMatrixRowIndex(ST_REVIEW_PLAN_POINT point)
        {
            return (Math.Max(1, point.ShotNo) - 1) / Math.Max(1, point.PixelCountX);
        }

        private static string ToRunShotDetail(ST_REVIEW_PLAN_POINT point)
        {
            if (IsReviewPointExcluded(point))
            {
                return point.ExclusionReason.Equals("MASKING + CHESS", StringComparison.OrdinalIgnoreCase) ? "MASK/CHESS\nEXCLUDED" : $"{point.ExclusionReason}\nEXCLUDED";
            }

            return point.State switch
            {
                EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng => $"X {FormatSigned(point.ErrorX)}\nY {FormatSigned(point.ErrorY)}",
                EN_REVIEW_POINT_STATE.Current => "CURRENT",
                EN_REVIEW_POINT_STATE.Skip => "SKIP",
                _ => "READY"
            };
        }

        private static string ToRunShotState(ST_REVIEW_PLAN_POINT point) => IsReviewPointExcluded(point) ? point.ExclusionReason : ToStateText(point.State);
        private static bool IsReviewPointExcluded(ST_REVIEW_PLAN_POINT point)
        {
            return !string.IsNullOrWhiteSpace(point.ExclusionReason);
        }

        private static DataView CreateEmptyResultMatrix()
        {
            var table = new DataTable("ReviewResultMatrix")
            {
                Locale = CultureInfo.InvariantCulture
            };
            table.Columns.Add("LINE", typeof(string));
            return table.DefaultView;
        }

        private static DataView BuildResultMatrix(ST_REVIEW_PLAN reviewPlan)
        {
            var measuredPoints = reviewPlan.ReviewPoints.Where(point => point.HeadNo > 0 && point.CellNo > 0 && point.Use && (point.State == EN_REVIEW_POINT_STATE.Ok || point.State == EN_REVIEW_POINT_STATE.Ng)).OrderBy(point => point.ReviewTargetY).ThenBy(point => point.HeadNo).ThenBy(point => point.ShotNo).ToArray();
            if (measuredPoints.Length == 0)
            {
                return CreateEmptyResultMatrix();
            }

            var columnKeys = measuredPoints.Select(point => new ST_REVIEW_RESULT_MATRIX_COLUMN(point.HeadNo, point.ShotNo, CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX))).Distinct().OrderBy(column => column.HeadNo).ThenBy(column => column.ShotNo).ToArray();
            var table = new DataTable("ReviewResultMatrix")
            {
                Locale = CultureInfo.InvariantCulture
            };
            table.Columns.Add("LINE", typeof(string));
            foreach (var column in columnKeys)
            {
                table.Columns.Add($"H{column.HeadNo:00}-{column.ShotName}", typeof(double));
            }

            // A LINE is a physical processing row on the Glass. Cells positioned
            // side-by-side therefore share one result row. DesignY is used here
            // because ReviewTargetY also contains the per-Head camera transform.
            var lineGroups = measuredPoints.GroupBy(point => Math.Round(point.DesignY, 6, MidpointRounding.AwayFromZero)).OrderBy(group => group.Key).ToArray();
            for (var lineIndex = 0; lineIndex < lineGroups.Length; lineIndex++)
            {
                var xRow = table.NewRow();
                var yRow = table.NewRow();
                xRow["LINE"] = $"L{lineIndex + 1:00}-X";
                yRow["LINE"] = $"L{lineIndex + 1:00}-Y";
                foreach (var point in lineGroups[lineIndex])
                {
                    var shotName = CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX);
                    var columnName = $"H{point.HeadNo:00}-{shotName}";
                    xRow[columnName] = point.ErrorX;
                    yRow[columnName] = point.ErrorY;
                }

                table.Rows.Add(xRow);
                table.Rows.Add(yRow);
            }

            return table.DefaultView;
        }

        private void SetCameraPreviewGeometry(ST_REVIEW_GLASS_PREVIEW preview)
        {
            _cameraGlassBounds = preview.GlassBounds;
            _cameraCanvasSize = preview.CanvasSize;
        }

        /// <summary>Refreshes presentation from shared memory only. No device reads or file polling.</summary>
        public void RefreshCameraFeedback(double viewportWidth, double viewportHeight)
        {
            if (_cameraInterfaces is null || _lastRecipe is null || _cameraGlassBounds.IsEmpty || _cameraCanvasSize.Width <= 0 || _cameraCanvasSize.Height <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
            {
                Camera.Clear();
                return;
            }

            var settings = _cameraSettings?.LoadSection(EN_SETTING_TAB.Option);
            if (!ReferenceEquals(_cameraRecipe, _lastRecipe) || !ReferenceEquals(_cameraSettingFrame, settings))
            {
                _cameraRecipe = _lastRecipe;
                _cameraSettingFrame = settings;
                var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in _lastRecipe.Parameters)
                {
                    parameters[item.Name] = item.Value;
                    if (!string.IsNullOrWhiteSpace(item.Key))
                        parameters[item.Key] = item.Value;
                }

                foreach (var item in settings ?? [])
                {
                    parameters[item.Name] = item.Value;
                    if (!string.IsNullOrWhiteSpace(item.Key))
                        parameters[item.Key] = item.Value;
                }

                _cameraParameters = parameters;
            }

            // Match Image.Stretch=Uniform, including the cropped preview's padding and letterboxing.
            var scale = Math.Min(viewportWidth / _cameraCanvasSize.Width, viewportHeight / _cameraCanvasSize.Height);
            var left = (viewportWidth - _cameraCanvasSize.Width * scale) / 2;
            var top = (viewportHeight - _cameraCanvasSize.Height * scale) / 2;
            Camera.Configure(_cameraParameters, new Rect(left + _cameraGlassBounds.Left * scale, top + _cameraGlassBounds.Top * scale, _cameraGlassBounds.Width * scale, _cameraGlassBounds.Height * scale));
            var values = CMotorStatusPollingService.ReadCachedSnapshot(_cameraInterfaces);
            var state = CMotorStatusPollingService.ReadStageConnectionState(_cameraInterfaces);
            Camera.UpdateFeedback(values.FirstOrDefault(row => row.Name == "Stage Y")?.Position ?? "-", values.FirstOrDefault(row => row.Name == "Review X")?.Position ?? "-", state == EN_COMM_STATE.Simulation ? "SIMULATION" : state == EN_COMM_STATE.Online ? "ONLINE" : "OFFLINE");
        }
#endregion
    }

    public sealed record ST_REVIEW_TAB_ITEM(string Name, bool IsSelected, CButtonCommand SelectCommand, string DisplayName)
    {
        public ST_REVIEW_TAB_ITEM(string name, bool isSelected, CButtonCommand selectCommand) : this(name, isSelected, selectCommand, name)
        {
        }
    }

    public sealed record ST_REVIEW_MODE_ITEM(string Name, string Detail, bool IsSelected, CButtonCommand SelectCommand, string DisplayName)
    {
        public ST_REVIEW_MODE_ITEM(string name, string detail, bool isSelected, CButtonCommand selectCommand) : this(name, detail, isSelected, selectCommand, name)
        {
        }
    }

    public sealed record ST_REVIEW_INSPECTION_METHOD_ITEM(string Name, string DisplayName, bool IsSelected, CButtonCommand SelectCommand)
    {
        public Brush BorderBrush => IsSelected ? CStatusBrush.Active : CStatusBrush.CommandDarkBorder;
        public Brush BackgroundBrush => IsSelected ? CStatusBrush.CommandBlue : CStatusBrush.CardBackground;
        public Brush TextBrush => IsSelected ? Brushes.White : CStatusBrush.PrimaryText;
    }

    public sealed class ST_REVIEW_RUN_SHOT_ROW(string shotKey, string shotName, string detail, string state, bool isCurrent, bool isSelected, string exclusionReason, CButtonCommand selectCommand) : CBindingBase
    {
        private string _detail = detail;
        private string _state = state;
        private bool _isCurrent = isCurrent;
        private bool _isSelected = isSelected;
        public string ShotKey { get; } = shotKey;
        public string ShotName { get; } = shotName;
        public string Detail => _detail;
        public string State => _state;
        public bool IsCurrent => _isCurrent;
        public bool IsSelected => _isSelected;
        public string ExclusionReason { get; } = exclusionReason;
        public bool IsExcluded => !string.IsNullOrWhiteSpace(ExclusionReason);
        public CButtonCommand SelectCommand { get; } = selectCommand;
        public CButtonCommand? ShowBeamPositionsCommand { get; init; }
        public Brush StateBrush => IsExcluded ? ExclusionReason.Equals("CHESS", StringComparison.OrdinalIgnoreCase) ? CStatusBrush.Frozen(0x7A, 0x1F, 0xD0) : CStatusBrush.Frozen(0xD7, 0x19, 0x20) : CReviewStatusBrush.ForState(State);
        public Brush BorderBrush => IsCurrent || IsSelected ? CStatusBrush.Active : IsExcluded ? StateBrush : CStatusBrush.Frozen(0x5B, 0x6F, 0x84);
        public Brush BackgroundBrush => IsCurrent ? CStatusBrush.SoftSelectedBackground : IsExcluded ? ExclusionReason.Equals("CHESS", StringComparison.OrdinalIgnoreCase) ? CStatusBrush.ChessBackground : CStatusBrush.MaskBackground : IsSelected ? CStatusBrush.SoftSelectedBackground : CStatusBrush.CardBackground;

        public void SetRuntimeState(string detailValue, string stateValue, bool currentValue)
        {
            if (_detail.Equals(detailValue, StringComparison.Ordinal) && _state.Equals(stateValue, StringComparison.Ordinal) && _isCurrent == currentValue)
            {
                return;
            }

            _detail = detailValue;
            _state = stateValue;
            _isCurrent = currentValue;
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsCurrent));
            OnPropertyChanged(nameof(StateBrush));
            OnPropertyChanged(nameof(BorderBrush));
            OnPropertyChanged(nameof(BackgroundBrush));
        }

        public void SetSelected(bool selectedValue)
        {
            if (_isSelected == selectedValue)
            {
                return;
            }

            _isSelected = selectedValue;
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(BorderBrush));
            OnPropertyChanged(nameof(BackgroundBrush));
        }
    }

    public sealed record ST_REVIEW_RUN_SHOT_MATRIX_ROW(int RowNo, IReadOnlyList<ST_REVIEW_RUN_SHOT_ROW> Shots);
    public sealed record ST_REVIEW_BEAM_ROW(int BeamNo, string BeamName, int Row, int Column, bool IsSelected, bool IsCurrent, string Detail, string State, CButtonCommand SelectCommand)
    {
        public string TargetX { get; init; } = "-";
        public string TargetY { get; init; } = "-";
        public bool IsInspectionResult { get; init; }
        public string InspectionStatus { get; init; } = "";
        public string InspectionResultText { get; init; } = "";
        public Brush StateBrush => CReviewStatusBrush.ForState(State);
        public Brush BorderBrush => IsSelected ? CStatusBrush.Active : CStatusBrush.CommandDarkBorder;
        public Brush BackgroundBrush => IsInspectionResult ? IsCurrent ? CStatusBrush.Frozen(0xFF, 0xE0, 0x82) : State == "OK" ? CStatusBrush.Frozen(0xC8, 0xE6, 0xC9) : State is "NG" or "ERROR" ? CStatusBrush.Frozen(0xFF, 0xCD, 0xD2) : CStatusBrush.CardBackground : IsSelected ? CStatusBrush.CommandBlue : CStatusBrush.CardBackground;
        public Brush TextBrush => IsInspectionResult ? CStatusBrush.PrimaryText : IsSelected ? Brushes.White : CStatusBrush.PrimaryText;
    }

    public sealed record ST_REVIEW_BEAM_MATRIX_ROW(int RowNo, IReadOnlyList<ST_REVIEW_BEAM_ROW> Beams);
    internal static class CReviewStatusBrush
    {
        private static readonly Brush Ready = CStatusBrush.Frozen(0x1F, 0x1F, 0x1F);
        private static readonly Brush Current = CStatusBrush.Frozen(0xB8, 0x7A, 0x00);
        private static readonly Brush Ok = CStatusBrush.Frozen(0x00, 0x72, 0x3A);
        private static readonly Brush Ng = CStatusBrush.Frozen(0xD7, 0x19, 0x20);
        private static readonly Brush SampleSelected = CStatusBrush.Frozen(0x00, 0x5F, 0x7D);
        private static readonly Brush SampleNotSelected = CStatusBrush.Frozen(0x1F, 0x1F, 0x1F);
        public static Brush ForState(string state)
        {
            return state.Trim().ToUpperInvariant() switch
            {
                "READY" => Ready,
                "CURRENT" => Current,
                "OK" => Ok,
                "NG" => Ng,
                "SKIP" => CStatusBrush.Muted,
                _ => CStatusBrush.ForDisplayState(state)};
        }

        public static Brush ForState(EN_REVIEW_POINT_STATE state)
        {
            return state switch
            {
                EN_REVIEW_POINT_STATE.Ready => Ready,
                EN_REVIEW_POINT_STATE.Current => Current,
                EN_REVIEW_POINT_STATE.Ok => Ok,
                EN_REVIEW_POINT_STATE.Ng => Ng,
                EN_REVIEW_POINT_STATE.Skip => CStatusBrush.Muted,
                _ => CStatusBrush.Muted
            };
        }

        public static Brush ForPreviewBaseState(EN_REVIEW_POINT_STATE state)
        {
            // The blinking marker supplies the yellow Current color.
            // Keep the base neutral so the blink remains visible.
            return state == EN_REVIEW_POINT_STATE.Current ? Ready : ForState(state);
        }

        public static Brush ForSampleSelection(EN_REVIEW_POINT_STATE state)
        {
            return state == EN_REVIEW_POINT_STATE.Skip ? SampleNotSelected : SampleSelected;
        }
    }

    public sealed class ST_REVIEW_POINT_SELECT_ROW(
        int no, 
        string shotKey, 
        string head, 
        string cell, 
        string point, 
        string row, 
        string column, 
        string designX, 
        string designY, 
        string reviewX, 
        string reviewY, 
        bool use, 
        string reason, 
        string exclusionReason, 
        CButtonCommand toggleCommand, 
        CButtonCommand dragSelectionCommand
    ) : CBindingBase
    {
        private bool _use = use;
        private string _reason = reason;
        public int No { get; } = no;
        public string ShotKey { get; } = shotKey;
        public string Head { get; } = head;
        public string Cell { get; } = cell;
        public string Point { get; } = point;
        public string Row { get; } = row;
        public string Column { get; } = column;
        public string DesignX { get; } = designX;
        public string DesignY { get; } = designY;
        public string ReviewX { get; } = reviewX;
        public string ReviewY { get; } = reviewY;
        public bool Use => _use;
        public string Reason => _reason;
        public string ExclusionReason { get; } = exclusionReason;
        public bool IsExcluded => !string.IsNullOrWhiteSpace(ExclusionReason);
        public bool CanSelect => !IsExcluded;
        public CButtonCommand ToggleCommand { get; } = toggleCommand;
        public CButtonCommand DragSelectionCommand { get; } = dragSelectionCommand;
        public string UseText => Use ? "USE" : "-";
        public string StateText => IsExcluded ? ExclusionReason.Equals("MASKING + CHESS", StringComparison.OrdinalIgnoreCase) ? "MASK/CHESS" : ExclusionReason : Use ? "Selected" : "Skip";
        public Brush UseBrush => Use ? CStatusBrush.Active : CStatusBrush.Muted;
        public Brush BorderBrush => Use ? CStatusBrush.Active : CStatusBrush.Frozen(0x27, 0x32, 0x41);
        public Brush BackgroundBrush => Use ? CStatusBrush.Frozen(0x0B, 0x3B, 0x78) : CStatusBrush.Frozen(0x0B, 0x11, 0x19);

        public void SetSelection(bool useValue, string reasonValue)
        {
            if (IsExcluded)
            {
                useValue = false;
                reasonValue = ExclusionReason;
            }

            if (_use == useValue && _reason.Equals(reasonValue, StringComparison.Ordinal))
            {
                return;
            }

            _use = useValue;
            _reason = reasonValue;
            OnPropertyChanged(nameof(Use));
            OnPropertyChanged(nameof(Reason));
            OnPropertyChanged(nameof(UseText));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(UseBrush));
            OnPropertyChanged(nameof(BorderBrush));
            OnPropertyChanged(nameof(BackgroundBrush));
        }
    }

    public sealed record ST_REVIEW_POINT_SELECT_MATRIX_ROW(int RowNo, IReadOnlyList<ST_REVIEW_POINT_SELECT_ROW> Shots);
    public sealed record ST_REVIEW_SAMPLE_DRAG_SELECTION(IReadOnlyList<string> ShotKeys, bool Use);
    public sealed record ST_REVIEW_SETTING_ITEM(string Name, bool IsSelected);
    internal sealed class CLazyReadOnlyList<T>(int count, Func<int, T> factory) : IReadOnlyList<T>, System.Collections.IList
    {
        private readonly Dictionary<int, T> _items = [];
        public int Count { get; } = Math.Max(0, count);
        public bool IsFixedSize => true;
        public bool IsReadOnly => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;

        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                if (!_items.TryGetValue(index, out var item))
                {
                    item = factory(index);
                    _items[index] = item;
                }

                return item;
            }
        }

        object? System.Collections.IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }

        public bool Contains(object? value) => IndexOf(value) >= 0;
        public int IndexOf(object? value)
        {
            for (var index = 0; index < Count; index++)
            {
                if (Equals(this[index], value))
                {
                    return index;
                }
            }

            return -1;
        }

        public void CopyTo(Array array, int index)
        {
            for (var itemIndex = 0; itemIndex < Count; itemIndex++)
            {
                array.SetValue(this[itemIndex], index + itemIndex);
            }
        }

        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed record ST_REVIEW_INSPECTION_HEAD_ROW(int HeadNo, int SelectedShotCount)
    {
        public string Name => $"HEAD {HeadNo:00}";
        public bool HasSelectedShots => SelectedShotCount > 0;
        public string Summary => $"{Name} · 선택된 Shot: {SelectedShotCount:N0}개";
    }

    public sealed record ST_REVIEW_RESULT_MATRIX_COLUMN(int HeadNo, int ShotNo, string ShotName);
    /// <summary>Projects shared Stage/Review feedback onto a fixed Glass. Never reads files or sends device commands.</summary>
    public sealed class CReviewCameraPreview : INotifyPropertyChanged
    {
        public const string UseSettingKey = "VISION_ANIMATION_USE";
        public const string XFlipSettingKey = "REVIEW_X_ANIMATION_FLIP";
        private Projection? _projection;
        private bool _hasFeedback;
        private double _stageY, _reviewX;
        public event PropertyChangedEventHandler? PropertyChanged;
        public bool IsVisible { get; private set; }
        public Point Center { get; private set; }
        public double? DesignX { get; private set; }
        public double? DesignY { get; private set; }
        public int ProjectionVersion { get; private set; }

        public void Configure(IReadOnlyDictionary<string, string> parameters, Rect glassBounds)
        {
            Projection? next = null;
            if (!glassBounds.IsEmpty && glassBounds.Width > 0 && glassBounds.Height > 0 && Read(parameters, "GLASS_SIZE_X", double.NaN, out var width) && width > 0 && Read(parameters, "GLASS_SIZE_Y", double.NaN, out var height) && height > 0 && Read(parameters, "REVIEW_CAMERA_ALIGN_KEY_POS_X", 0, out var akX) && Read(parameters, "REVIEW_CAMERA_ALIGN_KEY_POS_Y", 0, out var akY) && Read(parameters, "AK_MARGIN_X", 55, out var marginX, "ALIGN_MARGIN_X") && Read(parameters, "AK_MARGIN_Y", 45, out var marginY, "ALIGN_MARGIN_Y"))
            {
                try
                {
                    next = new(
                        glassBounds, 
                        width, 
                        height, 
                        akX, 
                        akY, 
                        marginX, 
                        marginY, 
                        CScannerAxisTransform.ReadStageDirection(parameters), 
                        IsOn(parameters.GetValueOrDefault(UseSettingKey)), 
                        IsOn(parameters.GetValueOrDefault(XFlipSettingKey)) ? -1 : 1
                    );
                }
                catch (InvalidOperationException)
                { /* An invalid direction must not create a fabricated camera position. */
                }
            }

            if (_projection == next)
                return;
            _projection = next;
            ProjectionVersion++;
            Project(forceNotify: true);
        }

        public void UpdateFeedback(string stageY, string reviewX, string mode)
        {
            _hasFeedback = mode is "ONLINE" or "SIMULATION" && double.TryParse(stageY, NumberStyles.Float, CultureInfo.InvariantCulture, out _stageY) && double.IsFinite(_stageY) && double.TryParse(reviewX, NumberStyles.Float, CultureInfo.InvariantCulture, out _reviewX) && double.IsFinite(_reviewX);
            Project();
        }

        public void Clear()
        {
            _projection = null;
            _hasFeedback = false;
            ProjectionVersion++;
            Project(forceNotify: true);
        }

        private void Project(bool forceNotify = false)
        {
            double? x = null, y = null;
            var center = new Point();
            if (_hasFeedback && _projection is { Enabled: true } p)
            {
                // Animate around the fixed AK reference. X flip affects display only, never motion/measurement coordinates.
                var designX = (_reviewX - p.AkX) * p.XDirection + p.MarginX;
                var designY = (_stageY - p.AkY) / p.Direction + p.MarginY;
                var pixelX = p.Bounds.Left + designX / p.Width * p.Bounds.Width;
                var pixelY = p.Bounds.Top + designY / p.Height * p.Bounds.Height;
                if (double.IsFinite(designX) && double.IsFinite(designY) && double.IsFinite(pixelX) && double.IsFinite(pixelY))
                {
                    x = designX;
                    y = designY;
                    center = new(pixelX, pixelY);
                }
            }

            var visible = x.HasValue && y.HasValue;
            if (!forceNotify && IsVisible == visible && DesignX == x && DesignY == y && Center == center)
                return;
            IsVisible = visible;
            DesignX = x;
            DesignY = y;
            Center = center;
            PropertyChanged?.Invoke(this, new(null));
        }

        private static bool Read(IReadOnlyDictionary<string, string> parameters, string key, double fallback, out double value, string? alias = null)
        {
            if (!parameters.TryGetValue(key, out var text) && (alias is null || !parameters.TryGetValue(alias, out text)))
            {
                value = fallback;
                return double.IsFinite(value);
            }

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        }

        private static bool IsOn(string? value) => value?.Trim().ToUpperInvariant()is "ON" or "TRUE" or "1" or "YES";
        private sealed record Projection(Rect Bounds, double Width, double Height, double AkX, double AkY, double MarginX, double MarginY, double Direction, bool Enabled, double XDirection);
    }
}
