namespace Drilling.UI.Views;

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Drilling.Common.Managers;
using Drilling.UI.Popup;

/// <summary>
/// Native counterpart of A3_Review_Workspace.html. The view owns only presentation
/// state; the controller owns source lineage, motion/vision, correction and storage.
/// </summary>
public partial class CManualInspectionView : UserControl
{
    private sealed record Choice(string Value, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record ScatterGroup(string Group, int Count, string Mean);
    private sealed record WorkspaceViewBookmark
    {
        public string RecipeId { get; init; } = "";
        public string RunId { get; init; } = "";
        public string CoordinateSignature { get; init; } = "";
        public string[] Keys { get; init; } = [];
        public string? FocusKey { get; init; }
        public string? ReferenceKey { get; init; }
        public int Cell { get; init; }
        public int Head { get; init; }
        public string Judge { get; init; } = "ALL";
        public string Source { get; init; } = "ALL";
        public string Query { get; init; } = "";
        public string Sort { get; init; } = "shot";
        public string Color { get; init; } = "level";
        public int Page { get; init; }
        public double MapZoom { get; init; }
        public Vector MapPan { get; init; }
        public string ScatterScope { get; init; } = "HEAD";
        public int ScatterHead { get; init; }
        public bool ScatterOriginal { get; init; }
        public bool ScatterOffsets { get; init; }
        public bool ScatterFinalOffsets { get; init; }
        public bool ScatterFollowSelection { get; init; }
        public int[] ScatterCells { get; init; } = [];
        public bool ScatterAuto { get; init; }
        public double ScatterZoom { get; init; }
        public (double MinX, double MaxX, double MinY, double MaxY) ScatterRange { get; init; }
        public string ScatterHalfRangeInput { get; init; } = "20";
        public string MeasurementMode { get; init; } = "P2P";
        public bool InspectorOpen { get; init; }
        public bool LeftInspectorOpen { get; init; }
        public int LeftPaneIndex { get; init; }
        public int RightPaneIndex { get; init; }
        public int CorrectionPaneIndex { get; init; }
        public int ScatterPaneIndex { get; init; }
        public int SettingsPaneIndex { get; init; }
        public bool ResultsOpen { get; init; }
        public bool ScatterOpen { get; init; }
        public bool WorkInfoOpen { get; init; }
        public bool SearchOpen { get; init; }
        public bool ReferenceOpen { get; init; }
        public bool CorrectionOpen { get; init; }
        public bool SettingsOpen { get; init; }
        public bool PointDetailsOpen { get; init; }
        public double WorkspaceOffset { get; init; }
        public double InspectorOffset { get; init; }
    }
    private sealed class GridRow(CManualInspectionUiPoint point, bool selected, Action<GridRow> changed, int appliedLevel, int measurementLevel) : INotifyPropertyChanged
    {
        private bool _selected = selected;
        public CManualInspectionUiPoint Point { get; } = point;
        public string AppliedOffsetText => !Point.HasAppliedOffset ? "—" : Point.IsMeasurementOffsetInitialized ? "없음" : Point.BaseOffsetText;
        public string AppliedLevelText => !Point.HasAppliedOffset ? "미적용" : appliedLevel >= 0 ? $"L{appliedLevel + 1}" : "확인 필요";
        public string MeasurementLevelText => measurementLevel >= 0 ? $"L{measurementLevel + 1}" + (Point.UsesAppliedMeasurement ? " · 보정" : "") : "—";
        public string MeasurementOriginText => string.IsNullOrWhiteSpace(Point.DisplayedMeasurementOrigin) ? Point.MeasurementBasisText : Point.DisplayedMeasurementOrigin;
        public bool IsSelectable => !Point.IsMasked;
        public bool IsSelected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); changed(this); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private IManualInspectionController? _controller;
    private CManualInspectionUiSnapshot _snapshot = new();
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly HashSet<int> _scatterCells = [];
    private Dictionary<string, CManualInspectionUiPoint> _points = new(StringComparer.Ordinal);
    private IReadOnlyList<CManualInspectionUiPoint> _filtered = [];
    private IReadOnlyList<CManualInspectionUiPoint>? _projectedPoints;
    private IReadOnlyList<CManualInspectionUiCellLayout>? _projectedCellLayouts;
    private int[] _cellChoices = [], _headChoices = [];
    private IReadOnlyList<CManualInspectionUiPoint>? _scatterProjectedPoints;
    private readonly HashSet<string> _scatterRenderedSelection = new(StringComparer.Ordinal);
    private string _scatterRenderSignature = "";
    private bool? _wideLayout;
    private string? _focusKey;
    private string? _representativeKey;
    private string _runId = "";
    private string _localMessage = "";
    private long _localMessageVersion = -1;
    private int _cell;
    private int _head;
    private string _judge = "ALL";
    private string _source = "ALL";
    private string _query = "";
    private string _sort = "shot";
    private string _color = "applied";
    private string _scatterScope = "HEAD";
    private int _scatterHead;
    private bool _scatterOriginal;
    private bool _scatterOffsets;
    private bool _scatterFinalOffsets = true;
    private bool _scatterFollowSelection;
    private WorkspaceViewBookmark? _workspaceViewBookmark;
    private int _page;
    private bool _updating;
    private bool _attached;
    private int _requests;
    private bool _actionConfirmationPending;
    private int _refreshPending;
    private long _lastMeasurementSequence;
    private long _lastLimitsVersion = -1;
    private string _lastLimitsSignature = "";
    private const int PageSize = 50;
    private bool Busy => _snapshot.IsBusy || _requests > 0 || _actionConfirmationPending;
    // A narrow UI-test seam: tests inspect the exact description and accept/cancel without a modal loop.
    internal Func<string, string, string, bool>? ConfirmationForTesting { get; set; }
    internal Func<string, string, string?>? NumericInputForTesting { get; set; }

    public CManualInspectionView()
    {
        InitializeComponent();
        _updating = true;
        SetChoices(JudgeFilter, [new("ALL", "전체 결과"), new("MEASURED", "측정됨 · 보정 포함"), new("OK", "실측 OK"), new("NG", "실측 NG"), new("ERROR", "최근 수신 오류"), new("UNMEASURED", "미측정")], "ALL");
        SetChoices(SourceFilter, [new("ALL", "전체 출처"), new("MEASURED", "MEASURED"), new("IMPORTED", "IMPORTED · 불러온 결과"), new("CORRECTION_BASED", "보정 기반 측정"), new("INTERPOLATED", "INTERPOLATED"), new("MANUAL", "MANUAL"), new("OPERATOR", "OPERATOR · 수동 확정"), new("KEPT", "KEPT")], "ALL");
        SetChoices(SortFilter, [new("shot", "Shot 순"), new("cell", "Cell 순"), new("error", "편차 크기 순")], "shot");
        SetChoices(ColorFilter, [new("level", "측정 편차 · Level"), new("judge", "OK/NG"), new("applied", "최종 Review 보정 · Level")], "applied");
        SetChoices(ScatterDataCombo, [new("WORKING", "측정 편차"), new("ORIGINAL", "원측정 기록"), new("OFFSETS", "작업 누적 보정량"), new("FINAL_OFFSETS", "저장된 최종 Review 보정")], "FINAL_OFFSETS");
        SetChoices(InitScopeCombo, [new("ALL", "모델 전체"), new("CELL", "Cell별"), new("HEAD", "Head별")], "ALL");
        SetChoices(MeasurementModeCombo, [new("P2P", "P2P · 선택점"), new("FLYING", "Flying · 해당 행 전체")], "P2P");
        _updating = false;
        PositionMap.SelectionRequested += Map_SelectionRequested;
        PositionMap.AllCellsRequested += (_, _) => SetMapCellScope(0);
        PositionMap.CellRequested += (_, cell) => SetMapCellScope(cell);
        PositionMap.OperationRequested += Map_OperationRequested;
        PositionMap.ValueInputRequested += async (_, e) =>
        {
            if (e.Keys.FirstOrDefault() is { } key) await InputMeasurementValues(key, e.ExpectedVersion);
        };
        PositionMap.MessageRequested += ShowMessage;
        PositionMap.ViewChanged += (_, _) => ZoomResetButton.Content = $"{PositionMap.Zoom * 100:0}% · 맞춤";
        ScatterPlot.SelectionRequested += Scatter_SelectionRequested;
        ScatterPlot.ViewChanged += (_, _) => RefreshScatterAxis();
    }

    private static void SetChoices(ComboBox combo, IEnumerable<Choice> choices, string selected)
    {
        combo.DisplayMemberPath = nameof(Choice.Label);
        var items = choices.ToArray(); combo.ItemsSource = items;
        combo.SelectedItem = items.FirstOrDefault(x => x.Value == selected) ?? items.FirstOrDefault();
    }
    private static string Value(ComboBox combo, string fallback = "") => (combo.SelectedItem as Choice)?.Value ?? fallback;
    private static double Number(TextBox box)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value <= 0) throw new InvalidOperationException("한계값은 0보다 큰 유한 숫자여야 합니다.");
        return value;
    }

    private void View_Loaded(object sender, RoutedEventArgs e) { AttachController(); Refresh(); }
    private void View_LayoutUpdated(object? sender, EventArgs e)
    {
        if (!IsInitialized || WorkspaceBody is null) return;
        var wide = ActualWidth >= 1300 && ActualHeight >= 700;
        if (_wideLayout != wide)
        {
            _wideLayout = wide;
            // The inspector consumes width only when the user opens its side handle.
            InspectorColumn.Width = GridLength.Auto;
            PointSummaryPanel.Padding = new Thickness(wide ? 12 : 6);
            PointTitleText.FontSize = wide ? 23 : 19;
            PointAppliedText.FontSize = wide ? 15 : 13;
            PointErrorText.FontSize = wide ? 13 : 11;
            SelectionSummaryText.Visibility = SelectionLevelsText.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        }
        // Side pages never grow the central surface. The bottom table takes a bounded share;
        // the remaining height belongs to the map, including on a short notebook viewport.
        var viewport = WorkspaceScroll.ViewportHeight;
        if (PositionMap.MinHeight != 1) PositionMap.MinHeight = 1;
        if (WorkspaceBody.MinHeight != 0) WorkspaceBody.MinHeight = 0;
        var tableHeight = Math.Clamp(double.IsFinite(viewport) ? viewport * .28 : 120, 88, 150);
        if (Math.Abs(ResultsSectionContent.MaxHeight-tableHeight)>.5) ResultsSectionContent.MaxHeight=tableHeight;
        var compact = ActualWidth < 1300;
        var leftWidth = compact ? 230d : 250d; var rightWidth = compact ? 260d : 280d;
        if (Math.Abs(LeftInspectorScroll.Width-leftWidth)>.5) LeftInspectorScroll.Width=leftWidth;
        if (Math.Abs(InspectorScroll.Width-rightWidth)>.5) InspectorScroll.Width=rightWidth;
    }
    private bool _sidePaneTransition;
    private void LeftPane_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (ReferenceEquals(e.OriginalSource,sender)) ActivateSidePane(true); }
    private void RightPane_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (ReferenceEquals(e.OriginalSource,sender)) ActivateSidePane(false); }
    private void CorrectionPane_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (ReferenceEquals(e.OriginalSource,sender) && IsInitialized && !_updating) RefreshButtons(); }
    private void ScatterPane_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource,sender) || !IsInitialized || _updating || _sidePaneTransition) return;
        if (ScatterPaneTabs.SelectedIndex==2) ScatterSettingsSection.IsExpanded=true;
        ScatterPlot.InvalidateVisual();
    }
    private void SettingsPane_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource,sender) || !IsInitialized || _updating || _sidePaneTransition) return;
        if (SettingsPaneTabs.SelectedIndex==2 && LevelSettingsPanel.Children.Count==0) Levels_Click(this,new RoutedEventArgs());
    }
    private void ActivateSidePane(bool left)
    {
        if (!IsInitialized || _updating || _sidePaneTransition || LeftPaneTabs is null || RightPaneTabs is null) return;
        _sidePaneTransition=true;
        try
        {
            if (left)
            {
                LeftInspectorSection.IsExpanded=true;
                if (LeftPaneTabs.SelectedIndex==0) WorkInfoSection.IsExpanded=true;
                else if (LeftPaneTabs.SelectedIndex==3) ScatterSection.IsExpanded=true;
                else if (LeftPaneTabs.SelectedIndex==4) SettingsSection.IsExpanded=true;
            }
            else
            {
                InspectorSection.IsExpanded=true;
                if (RightPaneTabs.SelectedIndex==0) PointDetailsSection.IsExpanded=true;
                else if (RightPaneTabs.SelectedIndex==1) ReferenceSection.IsExpanded=true;
                else if (RightPaneTabs.SelectedIndex==2) CorrectionSection.IsExpanded=true;
            }
        }
        finally { _sidePaneTransition=false; }
    }
    private void SideSection_Expanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource,sender) || !IsInitialized || _updating || _sidePaneTransition
            || LeftPaneTabs is null || RightPaneTabs is null) return;
        _sidePaneTransition=true;
        try
        {
            var name=(sender as FrameworkElement)?.Name;
            int? left=name switch { "WorkInfoSection" or "SearchSection"=>0,"ScatterSection" or "ScatterSettingsSection"=>3,"SettingsSection"=>4,_=>null };
            int? right=name switch { "PointDetailsSection"=>0,"ReferenceSection"=>1,"CorrectionSection"=>2,_=>null };
            if (left is int li) { LeftInspectorSection.IsExpanded=true;LeftPaneTabs.SelectedIndex=li; }
            if (right is int ri) { InspectorSection.IsExpanded=true;RightPaneTabs.SelectedIndex=ri; }
            if(name=="ScatterSettingsSection") ScatterPaneTabs.SelectedIndex=2;
        }
        finally { _sidePaneTransition=false; }
        if (ReferenceEquals(sender,LeftInspectorSection)) ActivateSidePane(true);
        else if (ReferenceEquals(sender,InspectorSection)) ActivateSidePane(false);
    }
    private void View_Unloaded(object sender, RoutedEventArgs e) { DetachController(); }
    private void View_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachController(); _controller = DataContext as IManualInspectionController;
        if (IsLoaded) AttachController();
        if (IsInitialized) Refresh();
    }
    private void AttachController()
    {
        _controller = DataContext as IManualInspectionController;
        if (_controller is not null && !_attached) { _controller.Changed += Controller_Changed; _attached = true; }
    }
    private void DetachController()
    {
        if (_controller is not null && _attached) _controller.Changed -= Controller_Changed;
        _attached = false;
    }
    private void Controller_Changed(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Interlocked.Exchange(ref _refreshPending, 1) != 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { Interlocked.Exchange(ref _refreshPending, 0); Refresh(); })); return;
        }
        Refresh();
    }

    private CManualInspectionUiPoint? FocusPoint => _focusKey is not null && _points.TryGetValue(_focusKey, out var point)
        ? point : _snapshot.Points.FirstOrDefault(p => _selected.Contains(p.Key)) ?? _filtered.FirstOrDefault(p => !p.IsMasked) ?? _filtered.FirstOrDefault();

    private void Refresh()
    {
        if (!IsInitialized || _updating) return;
        _updating = true;
        try
        {
            _snapshot = _controller?.Snapshot ?? new();
            // Progress-only snapshots reuse an immutable point array. Preserve dictionaries and
            // ComboBox items instead of rebuilding the entire project geometry on each status tick.
            var pointsChanged = !ReferenceEquals(_projectedPoints, _snapshot.Points);
            var cellsChanged = !ReferenceEquals(_projectedCellLayouts, _snapshot.CellLayouts);
            if (pointsChanged)
            {
                _projectedPoints = _snapshot.Points;
                _points = _snapshot.Points.GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                // Zero is the "all heads" UI choice; unassigned/masked H00 points remain in the
                // map dataset but must not add a second, indistinguishable value-zero choice.
                _headChoices = _snapshot.Points.Select(p => p.HeadNo).Where(head => head is >= 1 and <= 8).Distinct().OrderBy(x => x).ToArray();
            }
            if (pointsChanged || cellsChanged)
            {
                _projectedCellLayouts = _snapshot.CellLayouts;
                _cellChoices = _snapshot.Points.Select(p => p.CellNo).Concat(_snapshot.CellLayouts.Select(cell => cell.CellNo))
                    .Where(cell => cell > 0).Distinct().OrderBy(cell => cell).ToArray();
                if (cellsChanged) _scatterProjectedPoints = null;
            }
            _selected.RemoveWhere(k => !_points.ContainsKey(k));
            var workspaceIdentity = _snapshot.RecipeId + "|" + _snapshot.RunId + "|" + _snapshot.CoordinateSignature;
            if (_runId != workspaceIdentity)
            {
                _runId = workspaceIdentity; _selected.Clear(); _focusKey = null; _representativeKey = null;
                _cell = _snapshot.Points.FirstOrDefault()?.CellNo ?? 0; _head = 0; _judge = _source = "ALL";
                _query = ""; _page = 0; SearchBox.Text = "";
                _localMessage = "";
                _color = "applied"; _scatterScope = "HEAD"; _scatterHead = 0; _scatterOriginal = false; _scatterOffsets = false; _scatterFinalOffsets = true; _scatterFollowSelection = false;
                PositionMap.IsPanTool = false; PositionMap.Cursor = Cursors.Cross;
                _scatterCells.Clear(); if (_cell != 0) _scatterCells.Add(_cell);
            }
            // Marking a point is independent of whether its current value can be copied.
            if (_representativeKey is not null && !_points.ContainsKey(_representativeKey))
                _representativeKey = null;
            var cells = _cellChoices;
            var heads = _headChoices;
            if (_cell != 0 && !cells.Contains(_cell)) _cell = cells.FirstOrDefault();
            if (_head != 0 && !heads.Contains(_head)) _head = 0;
            if (pointsChanged || cellsChanged)
            {
                SetChoices(CellFilter, new[] { new Choice("0", "전체 Cell") }.Concat(cells.Select(n => new Choice(n.ToString(), $"Cell {n}"))), _cell.ToString());
                SetChoices(HeadFilter, new[] { new Choice("0", "전체 Head") }.Concat(heads.Select(n => new Choice(n.ToString(), $"Head {n}"))), _head.ToString());
            }
            else { SelectValue(CellFilter, _cell.ToString()); SelectValue(HeadFilter, _head.ToString()); }
            RefreshInitScope();
            var newMeasurement = _snapshot.MeasurementSequence > _lastMeasurementSequence;
            _lastMeasurementSequence = _snapshot.MeasurementSequence;
            if (newMeasurement)
            {
                // Only a fresh Vision result changes this presentation. Refresh/import/selection
                // retain the same sequence and must not unexpectedly reopen the inspector.
                if (_snapshot.LastMeasuredPointKey is { } measuredKey && _points.ContainsKey(measuredKey)) _focusKey = measuredKey;
                InspectorSection.IsExpanded = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => InspectorScroll.ScrollToTop()));
            }
            SelectValue(JudgeFilter, _judge); SelectValue(SourceFilter, _source); SelectValue(SortFilter, _sort);
            if (_color is not ("level" or "judge" or "applied")) _color="applied";
            SelectValue(ColorFilter, _color); SelectValue(ScatterDataCombo, _scatterFinalOffsets ? "FINAL_OFFSETS" : _scatterOriginal ? "ORIGINAL" : _scatterOffsets ? "OFFSETS" : "WORKING");
            _filtered = FilteredPoints();
            ContextText.Text = $"{(_snapshot.IsSimulation ? "SIM · 설비 명령 없음" : "실제 설비")}  |  Recipe {_snapshot.RecipeId}{(_snapshot.IsOffsetEditPending ? " · Offset 미저장" : "")}";
            ContextText.ToolTip = $"Recipe {_snapshot.RecipeId}\nRun {_snapshot.RunId}\n활성 보정 {_snapshot.ActiveRevisionId}";
            var statusDetails = _snapshot.StatusText + (string.IsNullOrWhiteSpace(_snapshot.SynchronizationText) ? "" : "\n" + _snapshot.SynchronizationText);
            StatusText.Text = (Busy ? "진행 중 · " : "")
                + (_snapshot.SyncPending ? "동기화 대기 · " : "") + _snapshot.StatusText.ReplaceLineEndings(" · ");
            StatusText.ToolTip = statusDetails + (_snapshot.SyncPending ? "\n새 결과 대기 중 · 현재 작업을 마치면 반영합니다." : "");
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, Busy ? "AccentTextBrush" : "TextPrimaryBrush");
            StagePositionText.Text = $"마지막 도착 확인  X {CManualInspectionUiPoint.Number(_snapshot.StageXmm)} / Y {CManualInspectionUiPoint.Number(_snapshot.StageYmm)} mm";
            PersistenceText.Text = _snapshot.PersistenceText + (string.IsNullOrWhiteSpace(_snapshot.SourcePath) ? "" : " · " + _snapshot.SourcePath);
            PersistenceText.ToolTip = PersistenceText.Text;
            RefreshFinalOffsetInfo();
            if (_localMessageVersion != _snapshot.Version) _localMessage = "";
            RefreshMessage();
            var scoped = _snapshot.Points.Where(p => (_cell == 0 || p.CellNo == _cell) && (_head == 0 || p.HeadNo == _head)).ToArray();
            var finalView = _color == "applied";
            PositionMap.Configure(_snapshot, finalView ? scoped.Select(FinalOffsetPoint).ToArray() : scoped,
                finalView ? _filtered.Select(FinalOffsetPoint).ToArray() : _filtered, _selected, _focusKey, _representativeKey, _color, $"{_cell}|{_head}", showCellOverview: _cell == 0);
            var visibleSelection = _filtered.Count(p => _selected.Contains(p.Key));
            var hiddenSelection = _selected.Count - visibleSelection;
            SelectionCountText.Text = $"선택 {_selected.Count:N0}점" + (hiddenSelection > 0 ? $" · 숨김 {hiddenSelection:N0}점" : "") + $" · 지도 {_filtered.Count:N0}점";
            SelectionCountText.ToolTip = $"선택 집합 전체 {_selected.Count:N0}점 = 현재 지도에서 보이는 선택 {visibleSelection:N0}점 + 다른 Cell/Head·필터로 숨겨진 선택 {hiddenSelection:N0}점.\n지도 {_filtered.Count:N0} / 전체 {_snapshot.Points.Count:N0}점. 우클릭 동작의 확인창에서 실제 실행 대상을 다시 확인합니다.";
            FilterNoticeText.Text = _head != 0 || _judge != "ALL" || _source != "ALL" || _query.Length > 0
                ? "세부 필터가 지도·표에 적용되어 있습니다. 산포도는 별도 범위를 사용합니다." : "";
            FilterNoticeText.Visibility = string.IsNullOrWhiteSpace(FilterNoticeText.Text) ? Visibility.Collapsed : Visibility.Visible;
            WorkInfoSection.Header = string.IsNullOrWhiteSpace(FilterNoticeText.Text) ? "필터 · 범례 · 작업 정보" : "필터 · 범례 · 작업 정보 · 필터 적용 중";
            MapLegendText.Text = MapLegend();
            MapLegendText.ToolTip = MapLegendText.Text;
            MapLegendItems.ToolTip = MapLegendText.Text;
            RefreshMapLegend();
            ColorDescriptionText.Text = _color == "applied" ? "저장된 최종 누적 Review 보정량 · 실측 잔차 아님" : _color == "level" ? "측정 편차 Level · NG 빨강 · 초기화/미측정 회색" : "OK/NG · 초기화/미측정 회색";
            ColorDescriptionText.ToolTip = "최종 보정 보기는 저장된 활성 파일만 표시합니다. 미저장 초안·미적용 측정은 제외하며 보정량이 작다고 가공 정밀도가 좋다는 뜻은 아닙니다. 측정 편차 보기에서는 직접 측정 편차가 우선입니다. 원편차가 없는 보정 이력은 보정 기반 값으로 표시하며, 초기화한 점은 새 측정 전까지 회색입니다.";
            RefreshRows(); RefreshReference(); RefreshCorrection(); RefreshDetails(); RefreshScatter(); RefreshButtons();
        }
        finally { _updating = false; }
    }

    private void RefreshInitScope()
    {
        var cellValue = Value(InitCellCombo);
        if (!int.TryParse(cellValue, out var cell) || !_cellChoices.Contains(cell))
            cell = _cell > 0 ? _cell : FocusPoint?.CellNo ?? _cellChoices.FirstOrDefault();
        if (!_cellChoices.Contains(cell)) cell = _cellChoices.FirstOrDefault();
        var headValue = Value(InitHeadCombo);
        if (!int.TryParse(headValue, out var head) || !_headChoices.Contains(head))
            head = _head > 0 ? _head : FocusPoint?.HeadNo ?? _headChoices.FirstOrDefault();
        if (!_headChoices.Contains(head)) head = _headChoices.FirstOrDefault();
        SetChoices(InitCellCombo, _cellChoices.Select(n => new Choice(n.ToString(CultureInfo.InvariantCulture), $"Cell {n}")), cell.ToString(CultureInfo.InvariantCulture));
        SetChoices(InitHeadCombo, _headChoices.Select(n => new Choice(n.ToString(CultureInfo.InvariantCulture), $"Head {n}")), head.ToString(CultureInfo.InvariantCulture));
        InitCellCombo.Visibility = Value(InitScopeCombo, "ALL") == "CELL" ? Visibility.Visible : Visibility.Collapsed;
        InitHeadCombo.Visibility = Value(InitScopeCombo, "ALL") == "HEAD" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void InitScope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) Refresh();
    }

    private static void SelectValue(ComboBox combo, string value) => combo.SelectedItem = combo.Items.OfType<Choice>().FirstOrDefault(x => x.Value == value);
    private IReadOnlyList<CManualInspectionUiPoint> FilteredPoints()
    {
        var points = _snapshot.Points.Where(p => (_cell == 0 || p.CellNo == _cell) && (_head == 0 || p.HeadNo == _head)
            && (_judge == "ALL" || _judge == "MEASURED" && p.HasMeasurementHistory
                || _judge == "UNMEASURED" && !p.IsMasked && !p.HasMeasurementHistory && p.WorkingJudge == "UNMEASURED"
                || _judge is not ("MEASURED" or "UNMEASURED") && p.WorkingJudge == _judge)
            && (_source == "ALL" || (p.IsProxy ? "MANUAL" : p.MeasurementSourceCode) == _source)
            && (_query.Length == 0 || (p.Key + " " + p.Identity).Contains(_query, StringComparison.OrdinalIgnoreCase)));
        return (_sort switch
        {
            "cell" => points.OrderBy(p => p.CellNo).ThenBy(p => p.HeadNo).ThenBy(p => p.ShotNo).ThenBy(p => p.BeamNo),
            "error" => points.OrderByDescending(p => CManualInspectionLevels.Metric(p.MeasurementXUm,p.MeasurementYUm,_snapshot.LevelMetric)
                ?? double.NegativeInfinity).ThenBy(p => p.Key, StringComparer.Ordinal),
            _ => points.OrderBy(p => p.ShotNo).ThenBy(p => p.CellNo).ThenBy(p => p.HeadNo).ThenBy(p => p.BeamNo)
        }).ToArray();
    }
    private CManualInspectionUiPoint FinalOffsetPoint(CManualInspectionUiPoint point)
    {
        var valid = _snapshot.HasFinalOffsetSnapshot;
        var exists = valid && _snapshot.FinalOffsetPoints.TryGetValue(point.Key, out var value);
        var offset = exists ? _snapshot.FinalOffsetPoints[point.Key] : new CManualInspectionUiOffset(0, 0);
        var initialized = !valid || _snapshot.FinalInitializedPointKeys.Contains(point.Key);
        return point with
        {
            BaseXUm = offset.XUm, BaseYUm = offset.YUm,
            HasAppliedOffset = valid && _snapshot.FinalAppliedPointKeys.Contains(point.Key),
            IsMeasurementOffsetInitialized = initialized, IsMeasurementStateCleared = false,
            WorkingJudge = initialized ? "UNMEASURED" : point.WorkingJudge,
            AppliedLevel = exists && !initialized && !point.IsMasked
                ? CManualInspectionLevels.Evaluate(offset.XUm, offset.YUm, _snapshot.LevelMetric, _snapshot.LevelBoundsUm) : null
        };
    }

    private void RefreshFinalOffsetInfo()
    {
        var known = _snapshot.HasFinalOffsetSnapshot;
        var hasFile = known && !string.IsNullOrWhiteSpace(_snapshot.FinalOffsetFilePath);
        FinalOffsetFileText.Text = hasFile ? _snapshot.FinalOffsetFileName : known ? "아직 저장된 최종 보정 파일 없음" : "최종 저장값 확인 대기";
        FinalOffsetFileText.ToolTip = hasFile ? _snapshot.FinalOffsetFilePath + "\nSHA-256 " + _snapshot.FinalOffsetContentSha256 : _snapshot.FinalOffsetStatusText;
        FinalOffsetInfoText.Text = hasFile
            ? $"{_snapshot.FinalOffsetSavedAt:yyyy-MM-dd HH:mm:ss} · {_snapshot.FinalOffsetRowCount:N0} Shot\n초기화 {_snapshot.FinalOffsetInitializedRowCount:N0} · DESIGN µm 표시"
            : _snapshot.FinalOffsetStatusText;
        FinalOffsetInfoText.ToolTip = _snapshot.FinalOffsetStatusText;
        ShowFinalOffsetsButton.IsEnabled = known;
        // This is the SAME authoritative CSV selected by LoadLatest for machining/monitoring.
        // Keep its identity visible with the side panels closed; never create another store.
        FinalCsvSourceText.Text = "Final Offset · " + (hasFile ? _snapshot.FinalOffsetFileName
            : known ? "저장 CSV 없음" : _snapshot.FinalOffsetStatusText.Contains("실패", StringComparison.Ordinal) ? "CSV 읽기 확인 필요" : "CSV 확인 중");
        FinalCsvSourceButton.ToolTip = (hasFile ? _snapshot.FinalOffsetFilePath + "\nSHA-256 " + _snapshot.FinalOffsetContentSha256 + "\n" : "")
            + _snapshot.FinalOffsetStatusText + "\n기존 Review CSV의 최종 누적값 보기 · 측정 원문/미저장 후보와 구분";
        ContextText.ToolTip += "\n최종 Review Offset: " + (hasFile ? _snapshot.FinalOffsetFilePath : FinalOffsetFileText.Text);
        if (_color == "applied")
            StatusText.ToolTip += "\n" + _snapshot.FinalOffsetStatusText;
    }

    private void ShowFinalOffsets_Click(object sender, RoutedEventArgs e)
    {
        _color = "applied";
        _scatterFinalOffsets = true; _scatterOriginal = false; _scatterOffsets = false;
        Refresh();
    }

    private void RefreshRows()
    {
        ResultOffsetColumn.Visibility = _color == "applied" ? Visibility.Visible : Visibility.Collapsed;
        ResultOffsetColumn.Header = _color == "applied" ? "저장된 최종 Review X/Y µm" : _snapshot.IsOffsetEditPending ? "작업 통합 Offset X/Y µm" : "적용 보정 X/Y µm";
        var pages = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)PageSize)); _page = Math.Clamp(_page, 0, pages - 1);
        ResultGrid.ItemsSource = _filtered.Skip(_page * PageSize).Take(PageSize).Select(point => { var p = _color == "applied" ? FinalOffsetPoint(point) : point; return new GridRow(p, _selected.Contains(p.Key), RowCheckChanged,
            CManualInspectionDrawing.AppliedLevel(p,_snapshot),CManualInspectionDrawing.Level(p,_snapshot)); }).ToArray();
        PageText.Text = $"{_page + 1} / {pages} 페이지";
        PreviousPageButton.IsEnabled = _page > 0; NextPageButton.IsEnabled = _page + 1 < pages;
        ResultStatsText.Text = $"측정됨 {_filtered.Count(p => p.HasMeasurementHistory):N0} (보정 기반 {_filtered.Count(p => p.UsesAppliedMeasurement):N0}) · OK {_filtered.Count(p => p.WorkingJudge == "OK"):N0} · NG {_filtered.Count(p => p.WorkingJudge == "NG"):N0} · 최근 오류 {_filtered.Count(p => p.WorkingJudge == "ERROR"):N0} · 미측정 {_filtered.Count(p => !p.IsMasked && !p.HasMeasurementHistory && p.WorkingJudge == "UNMEASURED"):N0}";
    }
    private void RefreshReference()
    {
        var reference = _representativeKey is not null && _points.TryGetValue(_representativeKey, out var p) ? p : null;
        if (reference is null) ReferenceText.Text = "원하는 점을 선택한 뒤 ‘대표값으로 지정’하세요.";
        else
        {
            var (x, y) = ReferenceValues(reference);
            var source = !reference.CanCopyAsReference && reference.CanCopyReferenceState ? "빈 측정 상태"
                : reference.IsProxy ? "복사값" : reference.ReferenceValueKind == "APPLIED_OFFSET" ? "기존 보정"
                : reference.IsMeasurementOffsetInitialized || !reference.CanCopyAsReference ? "미측정·근거 확인 필요" : "측정값";
            ReferenceText.Text = $"★ {reference.Key} · {(string.IsNullOrEmpty(reference.ReferenceValueJudge)?reference.WorkingJudge:reference.ReferenceValueJudge)} · {source}\n" + (reference.CanCopyAsReference
                ? $"대표 Offset {CManualInspectionUiPoint.Pair(x, y)} µm"
                : reference.CanCopyReferenceState ? "빈 측정 상태 복사 가능 · 기존 모델 보정값은 유지"
                : "대표점 지정됨 · 복사할 상태를 확인하세요.");
        }
        ReferenceText.SetResourceReference(TextBlock.ForegroundProperty, reference?.WorkingJudge == "NG" ? "ButtonDangerBrush" : "AccentTextBrush");
        var count = _selected.Count(k => k != _representativeKey && _points.TryGetValue(k, out var q) && !q.IsMasked);
        CopyReferenceButton.Content = $"같은 오프셋 · 상태 적용 · {count}점";
        CopyReferenceButton.ToolTip = reference?.CanCopyAsReference == true ? "대표 Offset과 현재 판정을 복사합니다. 유효한 OK는 확인 후 보정 파일에 저장하고, NG·미측정은 상태만 복사합니다."
            : reference?.CanCopyReferenceState == true ? "대표점의 빈 측정 상태를 복사합니다. 기존 모델 보정값과 원측정은 유지하며, 실제 보정 대상으로 만들지 않습니다."
            : reference?.ReferenceValueUnavailableReason ?? "대표점의 복사할 값이 필요합니다.";
    }
    private (double? X, double? Y) ReferenceValues(CManualInspectionUiPoint point)
    {
        // The controller resolves consumed measurements and active/draft revisions in
        // the engine. The preview must use that same F rather than re-add displayed raw.
        if (point.ReferenceFinalXUm.HasValue || point.ReferenceFinalYUm.HasValue)
            return (point.ReferenceFinalXUm, point.ReferenceFinalYUm);
        if (!point.CanCopyAsReference) return (null, null);
        if (point.IsProxy || !(point.Source == "MEASURED" || point.CanUseImportedForCorrection))
            return (point.BaseXUm, point.BaseYUm);
        return (point.BaseXUm + point.WorkingXUm, point.BaseYUm + point.WorkingYUm);
    }
    private void RefreshCorrection()
    {
        var applied = string.Equals(_snapshot.CandidateStatus, "APPLIED", StringComparison.OrdinalIgnoreCase);
        CandidateStatusText.Text = _snapshot.IsOffsetEditPending ? "작업 통합 Offset · 미저장"
            : applied ? "적용 완료 · 저장된 B에 반영됨"
            : _snapshot.CanApply && _snapshot.ValidationPassed ? "검증 완료 · 적용 전"
            : _snapshot.CandidateRows.Count > 0 ? "후보 준비됨 · 아직 적용되지 않음" : "활성 보정 B 유지 중";
        CandidateStatusText.ToolTip = "후보 상태: " + _snapshot.CandidateStatus;
        CandidateStatusText.SetResourceReference(TextBlock.ForegroundProperty, _snapshot.IsOffsetEditPending ? "ButtonWarningBrush" : applied || _snapshot.CanApply && _snapshot.ValidationPassed ? "StatusReadyBrush" : "AccentTextBrush");
        CorrectionSection.Header = _snapshot.IsOffsetEditPending ? "통합 Offset · 저장 전" : applied ? "보정 · 적용 완료" : _snapshot.CandidateRows.Count > 0 ? $"보정 확인 · 후보 {_snapshot.CandidateRows.Count:N0}점" : "보정 확인 · 적용";
        CandidateContextText.Text = _snapshot.IsOffsetEditPending
            ? $"작업 Offset {_snapshot.CandidateRows.Count:N0}점 · 저장 시 검증 후 모델에 반영"
            : $"선택점 보정: 선택 {_selected.Count:N0}점만 · 일괄 보정: 현재 모델 전체";
        CandidateContextText.ToolTip = "기준 활성 보정 " + _snapshot.ActiveRevisionId + "\n후보 F는 검증값 적용 전까지 지도 활성 B 색상에 포함되지 않습니다.";
        if (_snapshot.IsOffsetEditPending) CandidateContextText.ToolTip = "화면은 전체 모델의 작업 통합 Offset을 표시합니다. 저장 전에는 실제 활성 보정이 유지됩니다. 범위 초기화 시 나머지 모델 보정은 보존합니다.";
        OffsetStateText.Text = string.IsNullOrWhiteSpace(_snapshot.OffsetSummaryText)
            ? "Simple / Rough / Fine / One·All·Line Shot의 누적 측정 보정을 하나의 통합 Offset으로 관리합니다. 하드웨어·Default·First Beam·Distortion은 보존합니다."
            : _snapshot.OffsetSummaryText;
        OffsetStateText.ToolTip = OffsetStateText.Text;
        CandidateGrid.ItemsSource = _snapshot.CandidateRows.Take(8).ToArray();
        CandidateGrid.Visibility = _snapshot.CandidateRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CandidateRowCountText.Text = _snapshot.CandidateRows.Count > 8 ? $"처음 8점 표시 / 전체 {_snapshot.CandidateRows.Count:N0}점 적용" : "";
        AllowManualCheck.IsChecked = _snapshot.AllowManual;
        IncompleteText.Text = _snapshot.HasIncomplete ? $"미완료 {_snapshot.RemainingCount:N0}점 · 남은 위치 재측정 또는 새 위치를 선택해 측정하세요." : "";
        ValidationText.Text = (_snapshot.ValidationPassed ? "검증 통과" : _snapshot.ValidationErrors.Count > 0 ? "검증 차단" : "")
            + (_snapshot.ValidationErrors.Count > 0 ? "\n" + string.Join("\n", _snapshot.ValidationErrors.Take(8)) : "")
            + (_snapshot.ValidationWarnings.Count > 0 ? "\n" + string.Join("\n", _snapshot.ValidationWarnings.Take(4)) : "")
            + (_snapshot.CandidateRows.Count > 0 ? $"\n최대 증감 {_snapshot.MaxCandidateDeltaUm:0.000} / 총량 {_snapshot.MaxCandidateTotalUm:0.000} µm" : "지도에서 대상을 고른 뒤 계산하거나 대표값을 복사하세요.");
        ValidationText.SetResourceReference(TextBlock.ForegroundProperty, _snapshot.ValidationErrors.Count > 0 ? "ReviewMaskTextBrush" : _snapshot.ValidationPassed ? "StatusReadyBrush" : "TextPrimaryBrush");
        RevisionItems.ItemsSource = _snapshot.Revisions.Reverse().Take(8).ToArray();
        var signature = $"{_snapshot.DeltaLimitUm:R}|{_snapshot.TotalLimitUm:R}|{_snapshot.ScannerFieldMm:R}";
        if (_lastLimitsVersion < 0 || _lastLimitsSignature != signature)
        {
            DeltaLimitInput.Text = _snapshot.DeltaLimitUm.ToString("G", CultureInfo.InvariantCulture);
            TotalLimitInput.Text = _snapshot.TotalLimitUm.ToString("G", CultureInfo.InvariantCulture);
            FieldLimitInput.Text = _snapshot.ScannerFieldMm.ToString("G", CultureInfo.InvariantCulture);
            _lastLimitsVersion = _snapshot.Version; _lastLimitsSignature = signature;
        }
    }
    private void RefreshDetails()
    {
        var p = FocusPoint;
        LastMeasurementText.Text = _snapshot.LastMeasurementText;
        LastMeasurementText.Visibility = string.IsNullOrWhiteSpace(_snapshot.LastMeasurementText) ? Visibility.Collapsed : Visibility.Visible;
        var latest = _snapshot.LastMeasuredPointKey is { } latestKey && _points.TryGetValue(latestKey, out var latestPoint) ? latestPoint : null;
        LastMeasurementText.SetResourceReference(TextBlock.ForegroundProperty, latest?.WorkingJudge == "NG" ? "ButtonDangerBrush" : latest?.WorkingJudge == "ERROR" ? "ButtonWarningBrush" : "AccentTextBrush");
        var selected = _selected.Where(_points.ContainsKey).Select(k => _points[k]).ToArray();
        var visibleKeys = _filtered.Select(point => point.Key).ToHashSet(StringComparer.Ordinal);
        var hidden = selected.Count(point => !visibleKeys.Contains(point.Key));
        PointScopeText.Text = $"선택 {selected.Length:N0}점" + (hidden > 0 ? $" (숨김 {hidden:N0})" : "");
        PointScopeText.ToolTip = "아래 상세는 포커스한 한 점입니다. 선택 통계는 다른 Cell·Head 또는 필터로 숨겨진 선택까지 포함한 전체 선택 집합입니다.";
        PointTitleText.Text = p is null ? "선택 없음" : $"Cell {p.CellNo} · Head {p.HeadNo}";
        if (p is null) PointScopeText.Text = "포커스 위치 없음";
        PointIdentityText.Text = p is null ? "지도에서 점을 선택하세요." : $"Shot {p.ShotNo} · Beam {p.BeamNo}";
        PointJudgesText.Text = p is null ? "" : p.MeasurementStatusText;
        PointJudgesText.SetResourceReference(TextBlock.ForegroundProperty, p?.WorkingJudge == "NG" ? "ButtonDangerBrush" : p?.WorkingJudge == "ERROR" ? "ButtonWarningBrush" : "TextPrimaryBrush");
        var measurementLevel = p is null ? -1 : CManualInspectionDrawing.Level(p, _snapshot);
        PointErrorText.Text = p is null ? "" : p.IsOperatorResult && !p.HasWorkingMeasurement
            ? $"수동 확정 최종 F {p.ReferenceFinalXUm?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—"} / {p.ReferenceFinalYUm?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—"} µm\n잔류 편차 수치 없음"
            : $"편차 {p.MeasurementErrorText} µm\n{(measurementLevel >= 0 ? $"L{measurementLevel + 1} · " : "")}{(p.IsMeasurementOffsetInitialized ? "초기화 · 새 측정 전" : p.IsOperatorResult ? "수동 확정" : p.UsesAppliedMeasurement ? "보정 기반" : p.IsProxy ? "대표 복사" : p.HasWorkingMeasurement ? "실측" : "미측정")}";
        PointErrorText.ToolTip = p is null ? "" : $"표시 편차 X / Y {p.MeasurementErrorText} µm · DESIGN 좌표계\n{p.MeasurementBasisText}\n작업 편차 {p.WorkingErrorText} µm\n원측정 {p.OriginalErrorText} µm"
            + (p.UsesAppliedMeasurement ? "\n기존 B는 측정에서 유래한 보정량입니다. 현재 가공의 잔류 오차를 새로 측정한 값은 아닙니다." : "")
            + (p.IsOperatorResult ? "\n수동 확정: " + p.OperatorReason : "");
        var appliedLevel = p is null ? -1 : CManualInspectionDrawing.AppliedLevel(p,_snapshot);
        PointAppliedText.Text = p is null ? "" : p.HasAppliedOffset
            ? $"{(_snapshot.IsOffsetEditPending ? "작업 Offset · 미저장" : "적용 B")}  {p.BaseOffsetText} µm\n{(appliedLevel >= 0 ? $"보정 L{appliedLevel + 1} · {_snapshot.LevelNames.ElementAtOrDefault(appliedLevel)}" : "Level 확인 필요")}"
            : "적용된 보정값 없음";
        PointAppliedText.ToolTip = _snapshot.IsOffsetEditPending
            ? "저장 전 작업 통합 Offset · DESIGN X/Y µm. 모델 Offset 저장 전까지 실제 설비는 기존 활성 보정값을 유지합니다."
            : "현재 저장된 활성 보정 B · DESIGN 좌표계 X / Y µm. 보정량의 Level이며 측정 편차 Level 및 OK/NG 판정과 구분합니다.";
        PointAppliedText.Visibility = _color == "applied" ? Visibility.Visible : Visibility.Collapsed;
        if (_color == "applied")
        {
            var finalPoint = p is null ? null : FinalOffsetPoint(p);
            PointAppliedText.Text = finalPoint is null ? "" : !_snapshot.HasFinalOffsetSnapshot ? "최종 보정 확인 대기"
                : finalPoint.IsMeasurementOffsetInitialized ? "최종 Review · 값 없음"
                : !finalPoint.HasAppliedOffset ? "최종 Review · 저장 행 없음" : "최종 Review  " + finalPoint.BaseOffsetText + " µm";
            PointAppliedText.ToolTip = "저장된 최종 누적 보정량 · DESIGN µm · 원측정 잔차와 구분\n" + _snapshot.FinalOffsetFilePath;
        }
        SelectionSummaryText.Text = selected.Length == 0 ? "Ctrl+클릭 또는 드래그로 비교할 점을 선택하세요."
            : _snapshot.IsOffsetEditPending ? $"선택 전체 {selected.Length:N0}점 · 숨김 {hidden:N0}점\n작업 통합 Offset · 미저장 · 새 측정 아님"
            : $"선택 전체 {selected.Length:N0}점 · 숨김 {hidden:N0}점\n측정 이력 {selected.Count(point => point.HasMeasurementHistory):N0}점 · 그중 보정 기반 {selected.Count(point => point.UsesAppliedMeasurement):N0}점";
        SelectionSummaryText.ToolTip = "현재 선택 집합 통계입니다. 화면에 보이는 Cell 전체 통계나 새로 측정한 점의 개수가 아닙니다. 기존 B 기반 이력과 대표값 복사도 측정 이력에 포함됩니다.";
        var levels = selected.Select(point => _color == "applied" ? CManualInspectionDrawing.AppliedLevel(FinalOffsetPoint(point), _snapshot) : CManualInspectionDrawing.Level(point, _snapshot)).ToArray();
        SelectionLevelsText.Text = selected.Length == 0 ? "색상 기준과 공통 Level은 모든 Cell·Head에 동일하게 적용됩니다."
            : (_color == "applied" ? "선택 최종 저장 보정 Level" : "선택 측정 편차 Level") + "\n" + string.Join(" · ", Enumerable.Range(0, 4).Select(level => $"L{level + 1} {levels.Count(value => value == level):N0}"))
                + (levels.Any(level => level < 0) ? $" · 미분류 {levels.Count(level => level < 0):N0}" : "");
        SelectionLevelsText.ToolTip = "보정 B Level은 누적 활성 보정량, 측정 Level은 원편차 우선·없으면 기존 B 기반입니다. OK/NG 판정을 변경하거나 잔류 오차를 추정하지 않습니다.";
        PointDetailsText.Text = p is null ? "" : $"{(_snapshot.IsOffsetEditPending ? "작업 통합 Offset · 미저장" : "작업 기준 B")}  {p.BaseOffsetText} µm\n작업 후보 F  {p.CandidateOffsetText} µm"
            + (!_snapshot.IsOffsetEditPending && p.CandidateXUm.HasValue && p.CandidateYUm.HasValue ? $"\n후보 증감 Δ  {CManualInspectionUiPoint.Pair(p.CandidateXUm - p.BaseXUm, p.CandidateYUm - p.BaseYUm)} µm" : "")
            + $"\n설계 X / Y  {p.DesignXmm:0.000} / {p.DesignYmm:0.000} mm\n대표 여부  {(p.Key == _representativeKey ? "★ 대표값" : "대상 위치")}"
            + (p.IsProxy ? $"\n대표값 복사 · MANUAL\n기준 {p.ReferenceKey}\n새 실측이 아닌 대표값을 사용 중입니다." : "")
            + (string.IsNullOrWhiteSpace(p.DisplayedMeasurementOrigin) ? "" : "\n측정 출처: " + p.DisplayedMeasurementOrigin)
            + (string.IsNullOrWhiteSpace(p.MeasurementSourcePath) ? "" : "\n" + p.MeasurementSourcePath) + "\n" + p.Key;
        JobProgressText.Text = Busy ? $"진행 {_snapshot.CompletedCount:N0} / {_snapshot.TotalCount:N0}점" : _snapshot.HasIncomplete ? $"잔여 {_snapshot.RemainingCount:N0}점 · 자동 보정은 재개하지 않습니다." : "대기";
        JobProgressBar.Value = _snapshot.TotalCount <= 0 ? 0 : Math.Clamp(_snapshot.CompletedCount * 100.0 / _snapshot.TotalCount, 0, 100);
        JobProgressText.Visibility = JobProgressBar.Visibility = Busy || _snapshot.HasIncomplete ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = _snapshot.HasIncomplete ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshButtons()
    {
        var idle = !Busy; var writable = idle && _snapshot.Points.Count > 0;
        var operate = writable && !_snapshot.IsOffsetEditPending;
        MeasureCurrentButton.IsEnabled=operate && FocusPoint is {IsMasked:false} && _selected.Count<=1
            && double.IsFinite(_snapshot.StageXmm) && double.IsFinite(_snapshot.StageYmm);
        var hasSelection = _selected.Any(k => _points.TryGetValue(k, out var p) && !p.IsMasked);
        LoadRecipeButton.IsEnabled = idle && !_snapshot.IsOffsetEditPending;
        LoadRecipeButton.ToolTip = _snapshot.IsOffsetEditPending ? "Offset 편집을 저장하거나 취소한 뒤 새로고침하세요." : "현재 표시한 자료를 다시 읽습니다. 파일 작업과 복귀 지점은 유지합니다.";
        FilesButton.IsEnabled = idle;
        var initScope = Value(InitScopeCombo, "ALL");
        var validInitScope = initScope == "ALL" || initScope == "CELL" && int.TryParse(Value(InitCellCombo), out var initCell) && _cellChoices.Contains(initCell)
            || initScope == "HEAD" && int.TryParse(Value(InitHeadCombo), out var initHead) && _headChoices.Contains(initHead);
        OffsetInitButton.IsEnabled = idle && (_snapshot.IsOffsetEditPending || writable && validInitScope);
        InitScopeCombo.IsEnabled = InitCellCombo.IsEnabled = InitHeadCombo.IsEnabled = idle && !_snapshot.IsOffsetEditPending;
        OffsetInitButton.Content = _snapshot.IsOffsetEditPending ? "Offset 편집 취소" : "Offset Init";
        OffsetInitButton.Tag = _snapshot.IsOffsetEditPending ? "CancelOffsetEdit" : "InitializeOffsets";
        OffsetInitButton.ToolTip = _snapshot.IsOffsetEditPending
            ? "초기화/가져오기 전 작업·측정 표시로 돌아갑니다. 활성 모델 보정과 원본 파일은 변경하지 않습니다."
            : $"{(initScope == "CELL" ? "선택 Cell 전체" : initScope == "HEAD" ? "선택 Head 전체" : "모델 전체")}의 측정 누적 보정만 화면에서 초기화합니다. 하드웨어·Default·First Beam·Distortion과 기존 파일은 유지합니다. 모델 저장 시 반영됩니다.";
        CalculateButton.IsEnabled = operate && hasSelection && !_snapshot.HasIncomplete;
        ValidateButton.Visibility = Visibility.Collapsed;
        ValidateButton.IsEnabled = false;
        ApplyButton.Visibility = _snapshot.IsOffsetEditPending ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.Content = _snapshot.OffsetInitPending ? "초기화 저장" : "가져온 Offset 저장";
        ApplyButton.IsEnabled = writable && !_snapshot.HasIncomplete && _snapshot.IsOffsetEditPending && _snapshot.CandidateRows.Count > 0;
        ApplyButton.ToolTip = ApplyButton.IsEnabled
            ? "작업 Offset을 검증한 뒤 현재 모델에 저장합니다. 검증이 실패하면 저장하지 않습니다."
            : Busy ? "현재 동작 정리 후 사용할 수 있습니다."
            : _snapshot.HasIncomplete ? "남은 측정을 완료한 뒤 계산·검증하세요."
            : "저장할 초기화 또는 가져온 Offset 작업이 없습니다.";
        BulkApplyButton.IsEnabled=operate;
        BulkApplyButton.ToolTip=_snapshot.CanBulkApply ? "현재 모델의 새 OK 결과를 확인·검증한 뒤 보정합니다."
            : "현재 보정 가능 여부와 제외 이유를 확인합니다. NG·미측정 결과는 보정하지 않습니다.";
        BulkSummaryText.Text=_snapshot.BulkSummaryText;
        RollbackButton.IsEnabled = operate && _snapshot.CanUndoOffsets;
        RollbackButton.Content=$"되돌리기 ({_snapshot.UndoOffsetCount})";
        RedoOffsetsButton.IsEnabled=operate && _snapshot.CanRedoOffsets;
        RedoOffsetsButton.Content=$"다시 적용 ({_snapshot.RedoOffsetCount})";
        OffsetHistoryText.Text=_snapshot.OffsetHistoryText;
        SetReferenceButton.IsEnabled = idle && FocusPoint?.CanBeReference == true;
        ClearReferenceButton.IsEnabled = idle && _representativeKey is not null;
        CopyReferenceButton.IsEnabled = operate && _representativeKey is not null
            && _points.TryGetValue(_representativeKey, out var reference) && (reference.CanCopyAsReference || reference.CanCopyReferenceState)
            && _selected.Any(k => k != _representativeKey && _points.TryGetValue(k, out var p) && !p.IsMasked);
        UndoCopyButton.IsEnabled = operate && !_snapshot.HasIncomplete && _snapshot.CanUndoCopy;
        UndoCopyButton.Visibility = _snapshot.CanUndoCopy ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = Busy;
        RetryButton.IsEnabled = operate && _snapshot.HasIncomplete && _snapshot.RemainingCount > 0;
        AllowManualCheck.IsEnabled = idle && !_snapshot.IsOffsetEditPending;
        SaveLimitsButton.IsEnabled = LevelsButton.IsEnabled = idle;
        MeasurementModeCombo.IsEnabled = idle && !_snapshot.IsOffsetEditPending;
    }

    private string MapLegend() => (_color switch
    {
        "applied" => "저장된 최종 누적 Review 보정 · DESIGN µm · 미저장 초안 제외 · 실측 잔차 아님 · 회색: 값 없음",
        "level" => "측정 편차 Level · NG 빨강 · 초기화/미측정 회색",
        _ => "OK/NG · 초기화/미측정 회색"
    }) + " · 테두리: 선택/대표 · 출처: 점에 마우스 올리기";

    private void AddLegend(WrapPanel panel, string name, Brush brush)
    {
        var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 13, 5) };
        chip.Children.Add(new Border { Background = brush, Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 5, 0) });
        var text = new TextBlock { Text = name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush"); chip.Children.Add(text); panel.Children.Add(chip);
    }
    private void RefreshMapLegend()
    {
        MapLegendItems.Children.Clear();
        if (_color is "level" or "applied")
        {
            var colors = new[] { "#2E9076", "#B88B2A", "#CA754A", "#BE4F68" };
            for (var i = 0; i < 4; i++)
            {
                var bound = i < 3 && _snapshot.LevelBoundsUm.Count > i ? " ≤ " + _snapshot.LevelBoundsUm[i].ToString("G", CultureInfo.InvariantCulture)
                    : _snapshot.LevelBoundsUm.Count > 2 ? " > " + _snapshot.LevelBoundsUm[2].ToString("G", CultureInfo.InvariantCulture) : "";
                AddLegend(MapLegendItems, $"L{i + 1} {(_snapshot.LevelNames.Count > i ? _snapshot.LevelNames[i] : "")} {bound} µm", CManualInspectionDrawing.Color(colors[i]));
            }
        }
        if (_color is "level" or "applied")
            AddLegend(MapLegendItems, _color == "applied" ? "미적용" : "유효 측정 없음", CManualInspectionDrawing.Color("#DBE4EA"));
        else
            foreach (var (name, color) in new[] { ("OK", "#3B9870"), ("NG", "#C74242"), ("보정 기반", "#638CA8"), ("수신 오류", "#D39A45"), ("초기화/미측정", "#DBE4EA") }) AddLegend(MapLegendItems, name, CManualInspectionDrawing.Color(color));
        if (_color is "level" or "applied") AddLegend(MapLegendItems, "NG", CManualInspectionDrawing.Color("#C74242"));
        AddLegend(MapLegendItems, "Masking", CManualInspectionDrawing.Resource(this, "ReviewMaskBackgroundBrush", Brushes.LightGray));
    }

    private void RefreshScatter()
    {
        if (_scatterScope == "CELLS" && _scatterFollowSelection)
        {
            _scatterCells.Clear();
            foreach (var key in _selected)
                if (_points.TryGetValue(key, out var point) && !point.IsMasked) _scatterCells.Add(point.CellNo);
        }
        var currentCell = _cell != 0 ? _cell : FocusPoint?.CellNo ?? _snapshot.Points.FirstOrDefault()?.CellNo ?? 0;
        var finalOffsets = _scatterFinalOffsets;
        var offsets = finalOffsets || _scatterOffsets && !_scatterOriginal;
        var kind = finalOffsets ? "OFFSETS" : offsets ? _snapshot.IsOffsetEditPending ? "OFFSETS_DRAFT" : "OFFSETS" : _scatterOriginal ? "ORIGINAL" : "WORKING";
        var signature = FormattableString.Invariant($"{_scatterScope}|{(_scatterScope == "CELL" ? currentCell : 0)}|{_scatterHead}|{kind}|{_snapshot.ToleranceXUm:R}|{_snapshot.ToleranceYUm:R}")
            + "|final=" + finalOffsets + "|" + _snapshot.FinalOffsetRevisionId + "|" + _snapshot.FinalOffsetStatusText + "|" + _scatterFollowSelection + "|" + string.Join(",", _scatterCells.OrderBy(cell => cell));
        var contextIdentity = FormattableString.Invariant($"{_snapshot.Version}|{_snapshot.RecipeId}|{_snapshot.RunId}");
        ScatterPlot.UpdateInteractionContext(_focusKey, contextIdentity, signature);
        // Preserve the original-only clone array and chart controls on status-only snapshots.
        // Selection, point data, scope, source kind and tolerance changes still rebuild correctly.
        if (ReferenceEquals(_scatterProjectedPoints, _snapshot.Points) && _scatterRenderSignature == signature
            && _scatterRenderedSelection.SetEquals(_selected)) return;
        _scatterProjectedPoints = _snapshot.Points; _scatterRenderSignature = signature;
        _scatterRenderedSelection.Clear(); _scatterRenderedSelection.UnionWith(_selected);
        var range = _snapshot.Points.Select(p => finalOffsets ? FinalOffsetPoint(p) : p).Where(p => _scatterScope == "CELL" ? p.CellNo == currentCell : _scatterScope == "HEAD" ? _scatterHead == 0 || p.HeadNo == _scatterHead : _scatterCells.Contains(p.CellNo)).ToArray();
        // Offset distribution is its own display population. A fresh raw residual must not hide
        // the accumulated model offset, and a missing offset row must never become a zero sample.
        // These immutable clones cannot authorize correction or replace the source measurement.
        var display = offsets ? range.Select(p => p with { WorkingJudge = "UNMEASURED", WorkingXUm = null, WorkingYUm = null,
                IsProxy = false, IsOperatorResult=false, OperatorReason="", IsMeasurementOffsetInitialized = finalOffsets && p.IsMeasurementOffsetInitialized, IsMeasurementStateCleared=false, CanUseImportedForCorrection = false }).ToArray()
            : _scatterOriginal ? range.Select(p => p with { WorkingJudge = p.OriginalJudge, WorkingXUm = p.OriginalXUm, WorkingYUm = p.OriginalYUm,
                IsProxy = false, IsOperatorResult=false, OperatorReason="", HasAppliedOffset = false, IsMeasurementOffsetInitialized = false, IsMeasurementStateCleared=false, CanUseImportedForCorrection = false }).ToArray() : range;
        ScatterPlot.Configure(display, _selected, _scatterScope == "HEAD", _snapshot.ToleranceXUm, _snapshot.ToleranceYUm,
            _scatterScope == "CELLS" && _scatterCells.Count == 0 ? "산포도를 볼 Cell을 위에서 선택하세요."
                : offsets ? "이 범위에 저장된 통합 Offset 행이 없습니다. 전체 셀·Head별 또는 다른 범위를 확인하세요." : "이 범위에 유효한 측정값이 없습니다.",
            _focusKey, contextIdentity, signature, kind);
        RefreshScatterAxis();
        var m = ScatterPlot.Metrics;
        ScatterMetricsText.Text = $"전체 위치  {m.Total:N0}점\n{(offsets ? finalOffsets ? "저장된 최종 Review 보정" : _snapshot.IsOffsetEditPending ? "작업 통합 Offset · 미저장" : "저장된 통합 Offset" : _scatterOriginal ? "유효 원측정값" : "표시한 작업값")}  {m.Count:N0}점"
            + (offsets || _scatterOriginal ? "" : $"\n대표값 복사  {m.ProxyCount:N0}점\n보정 기반  {m.CorrectionBasedCount:N0}점")
            + $"\n\n평균 {(offsets ? "Offset X / Y" : "ΔX / ΔY")}\n{CManualInspectionUiPoint.Pair(m.MeanX, m.MeanY)} µm\n\n표준편차 σX / σY\n{CManualInspectionUiPoint.Number(m.SigmaX)} / {CManualInspectionUiPoint.Number(m.SigmaY)} µm\n\n최대 벡터 {(offsets ? "보정량" : "편차")}\n{CManualInspectionUiPoint.Number(m.MaximumVector)} µm";
        ScatterExcludedText.Text = offsets
            ? $"제외: Masking {m.Masked} · Offset 행 없음 {m.Unmeasured} · 수치 없음 {m.Invalid + m.Errors}점. 누적 보정량이며 잔류 측정 오차가 아닙니다."
            : $"제외: Masking {m.Masked} · 미측정 {m.Unmeasured} · 수신 오류 {m.Errors} · 수치 없음 {m.Invalid}점. σ는 모집단 기준입니다.";
        var byHead = _scatterScope == "HEAD";
        var groups = display.GroupBy(p => byHead ? p.HeadNo : p.CellNo).OrderBy(g => g.Key).Select(g =>
        {
            var stat = CManualInspectionScatterMetrics.Calculate(g);
            return new ScatterGroup((byHead ? "H" : "C") + g.Key, stat.Count, CManualInspectionUiPoint.Pair(stat.MeanX, stat.MeanY));
        }).ToArray();
        ScatterGroupsGrid.ItemsSource = groups;
        ScatterLegendItems.Children.Clear();
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i]; var index = byHead && int.TryParse(group.Group.AsSpan(1), out var h) ? Math.Max(0, h - 1) : i;
            AddLegend(ScatterLegendItems, group.Group, CManualInspectionDrawing.Color(CManualInspectionDrawing.HeadColors[index % 8]));
        }
        ScatterLegendText.Text = string.Join(" · ", groups.Select(g => g.Group)) + (offsets
            ? "   평균 + · (0,0): 누적 보정 0" + (finalOffsets ? " · 최종 저장값" : _snapshot.IsOffsetEditPending ? " · 미저장 작업값" : " · 저장된 누적값")
            : "   평균 + · (0,0): 목표 · " + (_scatterOriginal ? "원측정 기록" : "실측/대표 복사/보정 기반 · 출처는 점에 마우스 올리기"));
        foreach (var b in new[] { ScatterCellButton, ScatterHeadButton, ScatterCellsButton }) b.Style = (Style)FindResource((string)b.Tag == _scatterScope ? "ManualPrimaryButton" : "ManualButton");
        ScatterScopeControls.Children.Clear();
        if (_scatterScope == "CELL") ScatterScopeControls.Children.Add(Label($"Cell {currentCell} 전체 · 지도 선택·NG·검색 필터와 관계없이 셀 전체를 봅니다."));
        else if (_scatterScope == "HEAD")
        {
            var panel = new WrapPanel(); var combo = new ComboBox { Width = 190, Style = (Style)FindResource("ManualComboBox"), Margin = new Thickness(0, 0, 9, 4) };
            SetChoices(combo, new[] { new Choice("0", "모든 Head 겹쳐 보기") }.Concat(_headChoices.Select(x => new Choice(x.ToString(), $"Head {x}"))), _scatterHead.ToString());
            combo.SelectionChanged += (_, _) => { if (_updating) return; _scatterHead = int.Parse(Value(combo, "0"), CultureInfo.InvariantCulture); Refresh(); };
            panel.Children.Add(combo); panel.Children.Add(Label("지도 Cell·판정 필터와 관계없이 모든 Cell에서 모읍니다.")); ScatterScopeControls.Children.Add(panel);
        }
        else
        {
            ScatterScopeControls.Children.Add(Label(_scatterFollowSelection
                ? $"지도 선택을 따라갑니다 · 선택점이 속한 {_scatterCells.Count:N0}개 Cell의 전체 결과."
                : $"직접 고른 {_scatterCells.Count:N0}개 Cell · 지도 선택과 독립된 범위입니다."));
            var panel = new WrapPanel();
            foreach (var cell in _cellChoices)
            {
                var check = new CheckBox { Content = "C" + cell, IsChecked = _scatterCells.Contains(cell), Style = (Style)FindResource("ManualCheck"), Margin = new Thickness(0, 0, 10, 6) };
                check.Checked += (_, _) => { if (!_updating) { _scatterFollowSelection = false; _scatterCells.Add(cell); Refresh(); } };
                check.Unchecked += (_, _) => { if (!_updating) { _scatterFollowSelection = false; _scatterCells.Remove(cell); Refresh(); } }; panel.Children.Add(check);
            }
            ScatterScopeControls.Children.Add(panel);
            var buttons = new WrapPanel();
            buttons.Children.Add(Button("지도 선택 Cell 따라가기", () => { _scatterFollowSelection = true; Refresh(); }));
            buttons.Children.Add(Button("모든 셀", () => { _scatterFollowSelection = false; _scatterCells.Clear(); _scatterCells.UnionWith(_cellChoices); Refresh(); }));
            buttons.Children.Add(Button("비우기", () => { _scatterFollowSelection = false; _scatterCells.Clear(); Refresh(); })); ScatterScopeControls.Children.Add(buttons);
        }
    }

    private TextBlock Label(string text) { var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 3, 0, 4) }; label.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush"); return label; }
    private Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, Style = (Style)FindResource(primary ? "ManualPrimaryButton" : "ManualButton") };
        button.Click += (_, _) => action(); return button;
    }

    private void ChangeSelection(CManualInspectionSelectionEventArgs e)
    {
        if (!e.Additive) _selected.Clear();
        foreach (var key in e.Keys)
            if (_points.TryGetValue(key, out var p) && !p.IsMasked)
            { if (e.Toggle && _selected.Contains(key)) _selected.Remove(key); else _selected.Add(key); }
        if (e.ClearFocus) _focusKey = null;
        else if (e.FocusKey is not null) _focusKey = e.FocusKey;
        Refresh();
    }
    private void Map_SelectionRequested(object? sender, CManualInspectionSelectionEventArgs e) => ChangeSelection(e);
    private void Scatter_SelectionRequested(object? sender, CManualInspectionSelectionEventArgs e)
    {
        // Scatter scopes are intentionally independent of map filters. Reveal its focused point
        // even when it belongs to the same Cell but was hidden by Head/judge/source/search.
        if (e.FocusKey is not null && _points.TryGetValue(e.FocusKey, out var p) && !_filtered.Any(q => q.Key == p.Key))
        {
            if (_cell != 0 && _cell != p.CellNo) _cell = p.CellNo;
            if (_head != 0 && _head != p.HeadNo) _head = 0;
            _judge = _source = "ALL"; _query = ""; _page = 0; SearchBox.Text = "";
        }
        ChangeSelection(e);
    }
    private void RowCheckChanged(GridRow row)
    {
        if (_updating) return;
        if (row.IsSelected && !row.Point.IsMasked) _selected.Add(row.Point.Key); else _selected.Remove(row.Point.Key);
        _focusKey = row.Point.Key; Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));
    }
    private static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    {
        for (; node is not null;)
        {
            if (node is T match) return match;
            node = node is ContentElement content ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent : VisualTreeHelper.GetParent(node);
        }
        return null;
    }
    private void ResultGrid_LeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<CheckBox>(e.OriginalSource as DependencyObject) is not null) return;
        var row = Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as GridRow; if (row is null) return;
        if (row.Point.IsMasked) { ChangeSelection(new([], row.Point.Key, true)); e.Handled = true; return; }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        ChangeSelection(new([row.Point.Key], row.Point.Key, ctrl, ctrl)); e.Handled = true;
    }
    private void ResultGrid_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as GridRow; if (row is null) return;
        if (row.Point.IsMasked) { ChangeSelection(new([], row.Point.Key, true)); e.Handled = true; return; }
        ChangeSelection(new(_selected.Contains(row.Point.Key) ? [] : [row.Point.Key], row.Point.Key, _selected.Contains(row.Point.Key)));
        PositionMap.ShowOperationMenu(); e.Handled = true;
    }
    private void ResultGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && ResultGrid.SelectedItem is GridRow selected && !selected.Point.IsMasked
            && (Keyboard.IsKeyDown(Key.Up) || Keyboard.IsKeyDown(Key.Down)))
            ChangeSelection(new([selected.Point.Key], selected.Point.Key));
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        _cell = int.Parse(Value(CellFilter, "0"), CultureInfo.InvariantCulture); _head = int.Parse(Value(HeadFilter, "0"), CultureInfo.InvariantCulture);
        _judge = Value(JudgeFilter, "ALL"); _source = Value(SourceFilter, "ALL"); _sort = Value(SortFilter, "shot"); _page = 0; Refresh();
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_updating) return; _query = SearchBox.Text.Trim(); _page = 0; Refresh(); }
    private void ResetFilters_Click(object sender, RoutedEventArgs e) { _head = 0; _judge = _source = "ALL"; _query = ""; _page = 0; _updating = true; SearchBox.Text = ""; _updating = false; Refresh(); }
    private void Color_Changed(object sender, SelectionChangedEventArgs e) { if (_updating) return; _color = Value(ColorFilter, "applied"); Refresh(); }
    private void Scatter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var kind = Value(ScatterDataCombo, "WORKING");
        _scatterFinalOffsets = kind == "FINAL_OFFSETS";
        _scatterOriginal = kind == "ORIGINAL"; _scatterOffsets = kind == "OFFSETS"; Refresh();
    }
    private void ScatterScope_Click(object sender, RoutedEventArgs e)
    {
        _scatterScope = ((Button)sender).Tag as string ?? "CELL";
        if (_scatterScope == "CELLS") _scatterFollowSelection = true;
        Refresh();
    }
    private void SetMapCellScope(int cell)
    {
        if (cell != 0 && !_cellChoices.Contains(cell)) return;
        _cell = cell; _head = 0; _judge = _source = "ALL"; _query = ""; _page = 0;
        if (_focusKey is not null && _points.TryGetValue(_focusKey, out var focus) && cell != 0 && focus.CellNo != cell) _focusKey = null;
        _updating = true; SearchBox.Text = ""; _updating = false;
        Refresh(); PositionMap.ResetView();
    }
    private void RefreshScatterAxis()
    {
        if (!IsInitialized) return;
        ScatterAxisText.Text = (ScatterPlot.IsAutoScale ? "Auto · " : "고정 · ") + ScatterPlot.RangeText + " · X/Y 동일 축척 · 더블클릭 Auto 맞춤";
        ScatterAutoButton.Style = (Style)FindResource(ScatterPlot.IsAutoScale ? "ManualPrimaryButton" : "ManualButton");
        ScatterFitButton.Content = $"{ScatterPlot.Zoom * 100:0}% · 맞춤";
    }
    private void ScatterAuto_Click(object sender, RoutedEventArgs e) => ScatterPlot.ResetView();
    private void ScatterZoomIn_Click(object sender, RoutedEventArgs e) => ScatterPlot.ZoomAt(1.25, new Point(ScatterPlot.ActualWidth / 2, ScatterPlot.ActualHeight / 2));
    private void ScatterZoomOut_Click(object sender, RoutedEventArgs e) => ScatterPlot.ZoomAt(0.8, new Point(ScatterPlot.ActualWidth / 2, ScatterPlot.ActualHeight / 2));
    private void ScatterRangeApply_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ScatterRangeInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var range) || !double.IsFinite(range) || range <= 0)
        { ShowMessage("산포도 ±범위는 0보다 큰 유한 숫자(µm)로 입력하세요."); return; }
        try { ScatterPlot.SetFixedHalfRange(range); }
        catch (ArgumentException error) { ShowMessage(error.Message); }
    }
    private void ScatterRange_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ScatterRangeApply_Click(sender, e); e.Handled = true; }
    }
    private void SelectNg_Click(object sender, RoutedEventArgs e) => ChangeSelection(new(_filtered.Where(p => !p.IsMasked && p.WorkingJudge == "NG").Select(p => p.Key).ToArray(), null));
    private void SelectAll_Click(object sender, RoutedEventArgs e) => ChangeSelection(new(_filtered.Where(p => !p.IsMasked).Select(p => p.Key).ToArray(), null));
    private void ClearSelection_Click(object sender, RoutedEventArgs e) { _selected.Clear(); Refresh(); }
    private void PreviousPage_Click(object sender, RoutedEventArgs e) { _page = Math.Max(0, _page - 1); Refresh(); }
    private void NextPage_Click(object sender, RoutedEventArgs e) { _page++; Refresh(); }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => PositionMap.ZoomAt(1.25, new Point(PositionMap.ActualWidth / 2, PositionMap.ActualHeight / 2));
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => PositionMap.ZoomAt(0.8, new Point(PositionMap.ActualWidth / 2, PositionMap.ActualHeight / 2));
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => PositionMap.ResetView();
    private void SelectionTools_Click(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu((Button)sender);
        foreach (var (label, action) in new (string, Action)[] {
            ("보이는 점 전체 선택", () => SelectAll_Click(sender,e)), ("보이는 NG만 선택", () => SelectNg_Click(sender,e)), ("선택 해제", () => ClearSelection_Click(sender,e)) })
        { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        ((Button)sender).ContextMenu = menu; menu.IsOpen = true;
    }
    private void SetReference_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || FocusPoint?.CanBeReference != true) { ShowMessage("대표점으로 지정할 위치를 선택하세요."); return; }
        _representativeKey=FocusPoint.Key; Refresh();
    }

    private void ClearReference_Click(object sender, RoutedEventArgs e) { _representativeKey = null; Refresh(); }

    private CManualInspectionUiRequest Request(IEnumerable<string>? keys = null, long? expectedVersion = null) => new()
    {
        ExpectedVersion = expectedVersion ?? _snapshot.Version,
        Keys = (keys ?? _snapshot.Points.Where(p => _selected.Contains(p.Key)).Select(p => p.Key)).Distinct(StringComparer.Ordinal).ToArray(),
        FocusKey = FocusPoint?.Key, ReferenceKey = _representativeKey,
        MeasurementMode = Value(MeasurementModeCombo, "P2P"),
        Scenario = "same", Fault = "NONE", Speed = 1,
        AllowManual = AllowManualCheck.IsChecked == true,
        OffsetInitScope = Value(InitScopeCombo, "ALL"),
        OffsetInitCellNo = int.TryParse(Value(InitCellCombo), out var cell) ? cell : null,
        OffsetInitHeadNo = int.TryParse(Value(InitHeadCombo), out var head) ? head : null
    };
    private WorkspaceViewBookmark CaptureWorkspaceView() => new()
    {
        RecipeId = _snapshot.RecipeId, RunId = _snapshot.RunId, CoordinateSignature = _snapshot.CoordinateSignature, Keys = _selected.ToArray(), FocusKey = _focusKey, ReferenceKey = _representativeKey,
        Cell = _cell, Head = _head, Judge = _judge, Source = _source, Query = _query, Sort = _sort, Color = _color, Page = _page,
        MapZoom = PositionMap.Zoom, MapPan = PositionMap.Pan,
        ScatterScope = _scatterScope, ScatterHead = _scatterHead, ScatterOriginal = _scatterOriginal, ScatterOffsets = _scatterOffsets, ScatterFinalOffsets = _scatterFinalOffsets,
        ScatterFollowSelection = _scatterFollowSelection, ScatterCells = _scatterCells.ToArray(),
        ScatterAuto = ScatterPlot.IsAutoScale, ScatterZoom = ScatterPlot.Zoom, ScatterRange = (ScatterPlot.MinXUm, ScatterPlot.MaxXUm, ScatterPlot.MinYUm, ScatterPlot.MaxYUm),
        ScatterHalfRangeInput = ScatterRangeInput.Text, MeasurementMode = Value(MeasurementModeCombo, "P2P"),
        InspectorOpen = InspectorSection.IsExpanded, LeftInspectorOpen=LeftInspectorSection.IsExpanded,
        LeftPaneIndex=LeftPaneTabs.SelectedIndex,RightPaneIndex=RightPaneTabs.SelectedIndex,
        CorrectionPaneIndex=CorrectionPaneTabs.SelectedIndex,ScatterPaneIndex=ScatterPaneTabs.SelectedIndex,SettingsPaneIndex=SettingsPaneTabs.SelectedIndex,
        ResultsOpen = ResultsSection.IsExpanded, ScatterOpen = ScatterSection.IsExpanded,
        WorkInfoOpen = WorkInfoSection.IsExpanded, SearchOpen = SearchSection.IsExpanded,
        ReferenceOpen = ReferenceSection.IsExpanded, CorrectionOpen = CorrectionSection.IsExpanded,
        SettingsOpen = SettingsSection.IsExpanded, PointDetailsOpen = PointDetailsSection.IsExpanded,
        WorkspaceOffset = WorkspaceScroll.VerticalOffset, InspectorOffset = InspectorScroll.VerticalOffset
    };
    private void RestoreWorkspaceView(WorkspaceViewBookmark bookmark)
    {
        // A file view is a temporary navigation context. Restore presentation only after the
        // controller has successfully restored the original workspace and verified its lineage.
        Refresh();
        var sameFrame = bookmark.RecipeId == _snapshot.RecipeId && bookmark.RunId == _snapshot.RunId && bookmark.CoordinateSignature == _snapshot.CoordinateSignature;
        _cell = bookmark.Cell == 0 || _cellChoices.Contains(bookmark.Cell) ? bookmark.Cell : _cellChoices.FirstOrDefault();
        _head = bookmark.Head == 0 || _headChoices.Contains(bookmark.Head) ? bookmark.Head : 0;
        _judge = bookmark.Judge; _source = bookmark.Source; _query = bookmark.Query; _sort = bookmark.Sort; _color = bookmark.Color is "level" or "judge" or "applied" ? bookmark.Color : "applied"; _page = bookmark.Page;
        _selected.Clear();
        if (sameFrame) foreach (var key in bookmark.Keys)
            if (_points.TryGetValue(key, out var point) && !point.IsMasked) _selected.Add(key);
        _focusKey = sameFrame && bookmark.FocusKey is not null && _points.TryGetValue(bookmark.FocusKey, out var focus) && !focus.IsMasked ? bookmark.FocusKey : null;
        _representativeKey = sameFrame && bookmark.ReferenceKey is not null && _points.TryGetValue(bookmark.ReferenceKey, out var reference) && reference.CanBeReference ? bookmark.ReferenceKey : null;
        _scatterScope = bookmark.ScatterScope; _scatterHead = bookmark.ScatterHead == 0 || _headChoices.Contains(bookmark.ScatterHead) ? bookmark.ScatterHead : 0;
        _scatterOriginal = bookmark.ScatterOriginal; _scatterOffsets = bookmark.ScatterOffsets; _scatterFinalOffsets = bookmark.ScatterFinalOffsets; _scatterFollowSelection = bookmark.ScatterFollowSelection;
        _scatterCells.Clear(); _scatterCells.UnionWith(bookmark.ScatterCells.Where(_cellChoices.Contains));
        _updating = true;
        try
        {
            SearchBox.Text = _query; ScatterRangeInput.Text = bookmark.ScatterHalfRangeInput;
            SelectValue(MeasurementModeCombo, bookmark.MeasurementMode);
            InspectorSection.IsExpanded = bookmark.InspectorOpen; ResultsSection.IsExpanded = bookmark.ResultsOpen;
            ScatterSection.IsExpanded = bookmark.ScatterOpen; WorkInfoSection.IsExpanded = bookmark.WorkInfoOpen; SearchSection.IsExpanded = bookmark.SearchOpen;
            ReferenceSection.IsExpanded = bookmark.ReferenceOpen; CorrectionSection.IsExpanded = bookmark.CorrectionOpen;
            SettingsSection.IsExpanded = bookmark.SettingsOpen; PointDetailsSection.IsExpanded = bookmark.PointDetailsOpen;
            LeftInspectorSection.IsExpanded=bookmark.LeftInspectorOpen;
            LeftPaneTabs.SelectedIndex=bookmark.LeftPaneIndex;RightPaneTabs.SelectedIndex=bookmark.RightPaneIndex;
            CorrectionPaneTabs.SelectedIndex=bookmark.CorrectionPaneIndex;ScatterPaneTabs.SelectedIndex=bookmark.ScatterPaneIndex;
            SettingsPaneTabs.SelectedIndex=bookmark.SettingsPaneIndex;
        }
        finally { _updating = false; }
        Refresh(); UpdateLayout();
        if (sameFrame) PositionMap.RestoreView(bookmark.MapZoom, bookmark.MapPan); else PositionMap.ResetView();
        if (sameFrame && !bookmark.ScatterAuto)
            ScatterPlot.SetFixedRange(bookmark.ScatterRange.MinX, bookmark.ScatterRange.MaxX, bookmark.ScatterRange.MinY, bookmark.ScatterRange.MaxY, bookmark.ScatterZoom);
        else ScatterPlot.ResetView();
        WorkspaceScroll.ScrollToVerticalOffset(bookmark.WorkspaceOffset); InspectorScroll.ScrollToVerticalOffset(bookmark.InspectorOffset);
    }
    private async Task Execute(EN_MANUAL_INSPECTION_UI_ACTION action, CManualInspectionUiRequest? request = null)
    {
        if (_controller is null) { ShowMessage("Manual Inspection 제어기가 연결되지 않았습니다."); return; }
        var frozen = request ?? Request();
        var current = _controller.Snapshot;
        if (action != EN_MANUAL_INSPECTION_UI_ACTION.Stop)
        {
            if (Busy || current.IsBusy) { ShowMessage("동작 중입니다. 완료 또는 중지 후 조작하세요."); return; }
            // The controller repeats this check. Reject in the view as well so a confirmation
            // opened before an external Review update never dispatches a stale apply/copy request.
            if (frozen.ExpectedVersion != current.Version)
            { Refresh(); ShowMessage("확인 중 측정 기준이 바뀌었습니다. 현재 자료를 확인한 뒤 다시 요청하세요."); return; }
            if (current.IsOffsetEditPending && !AllowedDuringOffsetEdit(action))
            { ShowMessage("작업 통합 Offset을 모델에 저장하거나 ‘Offset 편집 취소’한 뒤 이동·측정·파일 전환을 진행하세요."); return; }
            if (current.HasIncomplete && (action is EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile or EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace))
            { ShowMessage("미완료 측정의 잔여점을 완료한 뒤 파일을 불러오거나 이전 작업으로 돌아가세요."); return; }
        }
        if (action is EN_MANUAL_INSPECTION_UI_ACTION.BulkApply or EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets or EN_MANUAL_INSPECTION_UI_ACTION.Calculate
            or EN_MANUAL_INSPECTION_UI_ACTION.Validate or EN_MANUAL_INSPECTION_UI_ACTION.Apply or EN_MANUAL_INSPECTION_UI_ACTION.CopyReference)
        {
            if(action==EN_MANUAL_INSPECTION_UI_ACTION.BulkApply && (!current.CanBulkApply || current.BulkEligibleCount==0))
            {
                InspectorSection.IsExpanded=true;RightPaneTabs.SelectedIndex=2;CorrectionSection.IsExpanded=true;CorrectionPaneTabs.SelectedIndex=0;
                ShowMessage(current.HasIncomplete ? "미완료 측정이 있습니다. 남은 위치를 측정하거나 새 측정·Offset Init으로 작업을 정리하세요."
                    : current.BulkEligibleCount>0 ? "현재 모델·좌표 기준을 확인할 수 없어 적용할 수 없습니다. 모델을 확인한 뒤 다시 시도하세요."
                    : "적용할 새 OK 측정값이 없습니다. NG·미측정·빈 상태·이미 적용한 결과는 제외합니다.\n"+current.BulkSummaryText);
                return;
            }
            bool CanConfirm(CManualInspectionUiSnapshot snapshot) => (action == EN_MANUAL_INSPECTION_UI_ACTION.CopyReference || !snapshot.HasIncomplete) && (action switch
            {
                EN_MANUAL_INSPECTION_UI_ACTION.CopyReference => !snapshot.IsOffsetEditPending && frozen.Keys.Count > 0
                    && snapshot.Points.Any(p => p.Key == frozen.ReferenceKey && (p.CanCopyAsReference || p.CanCopyReferenceState)),
                EN_MANUAL_INSPECTION_UI_ACTION.BulkApply => !snapshot.IsOffsetEditPending && snapshot.CanBulkApply && snapshot.BulkEligibleCount > 0,
                EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets => !snapshot.IsOffsetEditPending && frozen.Keys.Count > 0,
                EN_MANUAL_INSPECTION_UI_ACTION.Calculate => !snapshot.IsOffsetEditPending && frozen.Keys.Count > 0,
                EN_MANUAL_INSPECTION_UI_ACTION.Validate => snapshot.CandidateRows.Count > 0 && snapshot.CandidateStatus != "APPLIED",
                _ => snapshot.IsOffsetEditPending ? snapshot.CandidateRows.Count > 0 : snapshot.CanApply && snapshot.ValidationPassed
            });
            if (!CanConfirm(current))
            { ShowMessage("현재 결과에서는 해당 보정 작업을 실행할 수 없습니다. 대상과 후보 상태를 확인하세요."); return; }
            var confirmingController = _controller;
            var panel = new StackPanel();
            panel.Children.Add(Label($"Recipe {current.RecipeId} · Run {current.RunId}"));
            if (action == EN_MANUAL_INSPECTION_UI_ACTION.CopyReference
                && current.Points.FirstOrDefault(p => p.Key == frozen.ReferenceKey) is { } copySource)
            {
                var (copyX, copyY) = ReferenceValues(copySource);
                panel.Children.Add(Label($"대표 상태 {copySource.ReferenceValueJudge} · 최종 Offset X {copyX?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—"} / Y {copyY?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—"} µm"));
            }
            var (title, confirmText, description) = action switch
            {
                EN_MANUAL_INSPECTION_UI_ACTION.CopyReference => ("대표값 복사 적용 확인", "복사 적용",
                    $"대표 {frozen.ReferenceKey}\n선택한 {frozen.Keys.Distinct(StringComparer.Ordinal).Count():N0}점에 대표의 최종 Offset과 상태를 복사합니다.\n"+
                    "유효한 OK 결과는 보정 한계와 Field를 확인한 뒤 같은 모델의 새 누적 CSV로 저장하고 지도를 갱신합니다.\nNG·미측정 대표는 상태만 복사하며 실제 보정 파일은 바꾸지 않습니다.\n같은 Shot의 Beam은 보정값을 공유합니다. 원측정은 보존하며 저장 후 되돌릴 수 있습니다."),
                EN_MANUAL_INSPECTION_UI_ACTION.BulkApply => ("일괄 보정 적용 확인", "일괄 보정 적용",
                    $"현재 모델 전체의 유효한 측정 {current.BulkEligibleCount:N0}점에 보정을 적용합니다.\n{current.BulkSummaryText}\n선택·화면 필터와 관계없이 새 OK 측정과 대표값 복사 결과를 적용합니다.\nNG·미측정·이미 적용한 결과는 제외합니다.\n하드웨어·Default·First Beam·Distortion Offset은 유지하며, 저장 후 최대 10회 되돌릴 수 있습니다."),
                EN_MANUAL_INSPECTION_UI_ACTION.Calculate => ("선택점 보정 계산 확인", "보정 후보 계산",
                    $"선택한 {frozen.Keys.Distinct(StringComparer.Ordinal).Count():N0}점의 유효한 측정값으로 보정 후보를 계산합니다.\n현재 보정값에 측정 편차를 더한 후보이며, 기존 후보가 있으면 새 계산으로 바뀝니다.\n이 단계에서는 모델 Offset 파일에 저장하지 않습니다."),
                EN_MANUAL_INSPECTION_UI_ACTION.ApplySelectedOffsets => ("선택점 보정 확인", "선택점 보정 적용",
                    $"선택한 {frozen.Keys.Distinct(StringComparer.Ordinal).Count():N0}점 중 유효한 OK 결과를 계산·검증하여 현재 모델에 저장합니다.\nNG·미측정·한계 초과·이미 적용한 결과는 제외합니다.\n같은 Shot의 Beam은 보정값을 공유하며 Field나 기준 검증에 실패하면 저장하지 않습니다.\n보호 Offset과 이전 파일은 유지하며, 저장 후 최대 10회 되돌릴 수 있습니다."),
                EN_MANUAL_INSPECTION_UI_ACTION.Validate => ("후보 검증 확인", "후보 검증",
                    $"현재 후보 {current.CandidateRows.Count:N0}점의 출처·중복 적용·보정 한계·Scanner Field를 확인합니다.\n검증 결과는 후보에 표시하며, 통과하더라도 모델 Offset 파일에 저장하지 않습니다."),
                _ => (current.OffsetInitPending ? "초기화 저장 확인" : "가져온 Offset 저장 확인", current.OffsetInitPending ? "초기화 저장" : "가져온 Offset 저장",
                    $"작업 Offset {current.CandidateRows.Count:N0}점의 최종 보정값을 검증한 뒤 현재 모델에 저장합니다.\n검증에 실패하면 저장하지 않습니다.\n"+
                    (current.OffsetInitPending ? "선택 범위의 측정 기반 Offset은 ‘값 없음’ 상태로 저장됩니다.\n" : "이후 가공·Review는 저장된 최신 누적 Review Offset을 사용합니다.\n")+
                    "하드웨어·Default·First Beam·Distortion Offset과 기존 이력 파일은 보존합니다.\n저장된 보정 상태는 최대 10회 되돌릴 수 있습니다.")
            };
            panel.Children.Add(Label(description));
            panel.Children.Add(Label("장비를 이동하지 않습니다. 취소하면 후보와 모델 보정값은 변경되지 않습니다."));
            bool accepted;
            // ShowDialog runs a nested dispatcher. Lock before opening it so a second click
            // cannot create another confirmation or dispatch a second write behind this one.
            _actionConfirmationPending = true; RefreshButtons();
            try { accepted = Confirm(title, panel, confirmText); }
            catch (Exception error) { ShowMessage("작업 확인창을 열지 못했습니다. " + error.Message); return; }
            finally { _actionConfirmationPending = false; RefreshButtons(); }
            if (!accepted) { ShowMessage(confirmText + "을 취소했습니다."); return; }
            if (!ReferenceEquals(confirmingController, _controller) || _controller is null
                || frozen.ExpectedVersion != _controller.Snapshot.Version)
            { Refresh(); ShowMessage("확인 중 측정 기준이 바뀌었습니다. 현재 자료를 확인한 뒤 다시 요청하세요."); return; }
            current = _controller.Snapshot;
            if (Busy || current.IsBusy || !CanConfirm(current))
            { Refresh(); ShowMessage("현재 작업 상태에서는 해당 보정을 실행할 수 없습니다. 현재 결과를 확인하세요."); return; }
        }
        var beforeFile = action == EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile
            ? current.CanReturnToPreviousWorkspace ? _workspaceViewBookmark : CaptureWorkspaceView() : null;
        _requests++; RefreshButtons();
        try
        {
            await _controller.ExecuteAsync(action, frozen);
            var after = _controller.Snapshot;
            if (action == EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile && after.CanReturnToPreviousWorkspace && (!current.CanReturnToPreviousWorkspace || _workspaceViewBookmark is null))
                _workspaceViewBookmark = beforeFile;
            if ((action is EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace or EN_MANUAL_INSPECTION_UI_ACTION.Refresh or EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe)
                && current.CanReturnToPreviousWorkspace && !after.CanReturnToPreviousWorkspace)
            {
                var bookmark = _workspaceViewBookmark; _workspaceViewBookmark = null;
                if (bookmark is not null) RestoreWorkspaceView(bookmark);
            }
            else if (action != EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile && !after.CanReturnToPreviousWorkspace)
                _workspaceViewBookmark = null;
        }
        catch (Exception ex) { ShowMessage(ex.Message); }
        finally { _requests--; Refresh(); }
    }

    private static bool AllowedDuringOffsetEdit(EN_MANUAL_INSPECTION_UI_ACTION action) => action is
        EN_MANUAL_INSPECTION_UI_ACTION.Stop or EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint
        or EN_MANUAL_INSPECTION_UI_ACTION.Validate or EN_MANUAL_INSPECTION_UI_ACTION.Apply
        or EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit or EN_MANUAL_INSPECTION_UI_ACTION.UpdateLimits
        or EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels or EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv
        or EN_MANUAL_INSPECTION_UI_ACTION.ExportOffsetBundle or EN_MANUAL_INSPECTION_UI_ACTION.ExportFinalOffsets;
    private async void Map_OperationRequested(object? sender, CManualInspectionOperationEventArgs e)
    {
        if (e.Action == EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement)
        {
            await Execute(e.Action, Request(e.Keys, e.ExpectedVersion) with { Reason = "사용자 수동 측정 완료 · OK 확정" });
            return;
        }
        if (e.Action == EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint)
        {
            if (e.ExpectedVersion != _snapshot.Version) { ShowMessage("메뉴를 연 뒤 측정 기준이 바뀌었습니다. 다시 선택하세요."); return; }
            var key = e.Keys.FirstOrDefault(); if (key is null) return;
            await Execute(e.Action, Request([key], e.ExpectedVersion) with { FocusKey = key });
            return;
        }
        await ConfirmOperation(e.Action, e.Keys, e.ExpectedVersion,
            e.Action is EN_MANUAL_INSPECTION_UI_ACTION.Move or EN_MANUAL_INSPECTION_UI_ACTION.Measure ? "P2P" : Value(MeasurementModeCombo,"P2P"));
    }
    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse<EN_MANUAL_INSPECTION_UI_ACTION>(button.Tag as string,out var action)) return;
        if (action is EN_MANUAL_INSPECTION_UI_ACTION.Move or EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure or EN_MANUAL_INSPECTION_UI_ACTION.Measure)
        {
            var selected=action!=EN_MANUAL_INSPECTION_UI_ACTION.Measure && _selected.Count>0;
            var keys=selected?_selected.ToArray():FocusPoint is null?[]:new[]{FocusPoint.Key};
            await ConfirmOperation(action,keys,_snapshot.Version,selected?Value(MeasurementModeCombo,"P2P"):"P2P");return;
        }
        if (action==EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining)
        {
            var request=Request();var panel=new StackPanel();panel.Children.Add(Label($"남은 {_snapshot.RemainingCount:N0}점으로 이동 후 측정합니다."));
            if (!Confirm("남은 위치 이동 후 재측정",panel,"이동 후 재측정")) return;
            await Execute(action,request);return;
        }
        await Execute(action,Request(action==EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv?_snapshot.Points.Select(p=>p.Key):null));
    }

    internal static string ActionTitle(EN_MANUAL_INSPECTION_UI_ACTION action) => action switch
    {
        EN_MANUAL_INSPECTION_UI_ACTION.Refresh or EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe => "결과 새로고침",
        EN_MANUAL_INSPECTION_UI_ACTION.Calculate => "선택점 보정 계산",
        EN_MANUAL_INSPECTION_UI_ACTION.Validate => "후보 검증",
        EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining => "남은 위치만 재측정",
        EN_MANUAL_INSPECTION_UI_ACTION.UndoCopy => "직전 복사 취소",
        EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets => "Offset Init · 화면 초기화",
        EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit => "Offset 편집 취소",
        _ => action.ToString()
    };
    internal static string DescribeAction(EN_MANUAL_INSPECTION_UI_ACTION action, CManualInspectionUiSnapshot snapshot, int count) => action switch
    {
        EN_MANUAL_INSPECTION_UI_ACTION.Refresh => "현재 표시한 자료를 다시 읽으며, 파일 작업 중에는 원래 작업 복귀 지점과 현재 측정을 유지합니다. 새 자료의 미적용 후보는 적용 전 재검증해야 하며, 새로고침은 장비를 이동하지 않고 보정 파일도 저장하지 않습니다.",
        EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe => snapshot.CanReturnToPreviousWorkspace
            ? "파일 불러오기 전 작업으로 돌아갑니다. 현재 활성 보정과 실제 Stage 위치는 원복하지 않습니다."
            : "현재 Recipe의 측정 결과와 활성 보정값을 다시 확인합니다. 장비 이동이나 보정 저장은 실행하지 않습니다.",
        EN_MANUAL_INSPECTION_UI_ACTION.Calculate => $"선택한 {count:N0}점의 유효한 실측 또는 검증된 파일 측정값으로 보정 후보 B + 편차를 계산합니다. 기존 후보를 대신할 수 있습니다. 여기서는 활성 보정 파일에 저장하지 않습니다.",
        EN_MANUAL_INSPECTION_UI_ACTION.Validate => $"현재 후보 {snapshot.CandidateRows.Count:N0}점의 출처·중복 적용·보정 한계·Field를 검증합니다. 통과해도 ‘모델 Offset 저장’ 전에는 활성 보정이 바뀌지 않습니다.",
        EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining => $"미완료 {snapshot.RemainingCount:N0}점만 이동 후 재측정합니다. 이미 완료된 점은 유지하며 중지됐던 자동 보정은 재개하지 않습니다.",
        EN_MANUAL_INSPECTION_UI_ACTION.UndoCopy => "직전 대표값 복사를 취소해 작업값·후보를 이전 상태로 돌립니다. 원측정과 활성 보정 파일은 변경하지 않습니다. 복사 이후 기준이 달라졌으면 취소가 거부됩니다.",
        EN_MANUAL_INSPECTION_UI_ACTION.InitializeOffsets => "선택한 모델 전체·Cell·Head 범위의 측정 누적 Offset을 화면에서 0으로 초기화합니다. Simple / Rough / Fine / One Shot / All Shot / Line Shot과 대표 복사에 의한 보정이 대상입니다. 하드웨어·Default Offset·First Beam Offset·Distortion은 보존합니다. 기존 측정/보정 파일을 삭제하지 않으며, ‘모델 Offset 저장’ 전까지 실제 활성 보정도 유지합니다. 저장하거나 편집을 취소한 뒤 다음 측정·파일 전환을 진행하세요.",
        EN_MANUAL_INSPECTION_UI_ACTION.CancelOffsetEdit => "초기화하거나 통합 Offset 파일을 가져오기 전의 화면 작업·측정값·후보로 돌아갑니다. 저장되지 않은 통합 Offset 편집만 취소하며, 실제 활성 모델 보정과 기존 파일은 변경하지 않습니다.",
        _ => "선택한 동작을 확인한 뒤 진행합니다."
    };

    private async Task ConfirmOperation(EN_MANUAL_INSPECTION_UI_ACTION action, IEnumerable<string> keys, long expectedVersion, string mode)
    {
        if(action==EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect)
        { ShowMessage("이동 후 측정한 뒤 일괄 보정을 사용하세요.");return; }
        if (Busy) { ShowMessage("동작 중입니다. 완료 또는 중지 후 조작하세요."); return; }
        if (expectedVersion != _snapshot.Version) { ShowMessage("작업 메뉴를 연 뒤 기준이 바뀌었습니다. 다시 선택하세요."); return; }
        var requested = keys.Distinct(StringComparer.Ordinal).Where(k => _points.TryGetValue(k, out var p) && !p.IsMasked).ToArray();
        if (requested.Length == 0) { ShowMessage("이동·측정할 위치를 선택하세요."); return; }
        var actual = requested;
        if (mode == "FLYING" && action != EN_MANUAL_INSPECTION_UI_ACTION.Move)
        {
            actual = ExpandFlyingKeys(requested);
        }
        var request = Request(actual, expectedVersion) with { MeasurementMode = mode };
        if (action==EN_MANUAL_INSPECTION_UI_ACTION.Measure) { await Execute(action,request); return; }
        var title = action switch { EN_MANUAL_INSPECTION_UI_ACTION.Move => "이동만", EN_MANUAL_INSPECTION_UI_ACTION.Measure => "현재 위치 측정", _ => "이동 후 측정" };
        var panel = new StackPanel();
        panel.Children.Add(Label($"{(_snapshot.IsSimulation ? "SIM · 설비 명령 없음" : "실제 설비 작업")}\nRecipe {_snapshot.RecipeId} · Run {_snapshot.RunId}\n선택 {requested.Length:N0}점 → 실행 {actual.Length:N0}점 · {mode}"));
        panel.Children.Add(Label(string.Join("\n", actual.Take(12)) + (actual.Length > 12 ? $"\n… 전체 {actual.Length:N0}점" : "")));
        panel.Children.Add(Label(action == EN_MANUAL_INSPECTION_UI_ACTION.Move ? "선택 위치로 순서대로 이동합니다. 측정과 보정은 하지 않습니다."
            : "새 측정 결과만 취득합니다. 측정 완료 후 일괄 보정을 별도로 실행하세요."));
        if (_snapshot.HasIncomplete)
            panel.Children.Add(Label("새 작업을 시작하면 이전 잔여 목록 대신 지금 선택한 위치를 실행합니다. 완료한 측정과 실패 이력은 보존합니다."));
        if (!Confirm(title, panel, title + " 실행")) return;
        if (_snapshot.Version != expectedVersion) { ShowMessage("대상 확인 중 작업 기준이 바뀌었습니다. 다시 선택하세요."); return; }
        await Execute(action, request);
    }

    private string[] ExpandFlyingKeys(IEnumerable<string> selected)
    {
        var groups = selected.Where(_points.ContainsKey).Select(k => _points[k]).Select(p => (p.CellNo, p.HeadNo, p.Row, p.BeamNo)).ToHashSet();
        return _snapshot.Points.Where(p => !p.IsMasked && groups.Contains((p.CellNo, p.HeadNo, p.Row, p.BeamNo))).Select(p => p.Key).ToArray();
    }

    private async void CopyReference_Click(object sender,RoutedEventArgs e)
    {
        if (Busy || _representativeKey is null || !_points.TryGetValue(_representativeKey,out var reference) || !(reference.CanCopyAsReference || reference.CanCopyReferenceState)) return;
        var keys=_selected.Where(k=>k!=reference.Key && _points.TryGetValue(k,out var p) && !p.IsMasked).ToArray();
        if (keys.Length==0) { ShowMessage("대표점 이외의 대상을 선택하세요.");return; }
        var reason=string.IsNullOrWhiteSpace(CopyReasonInput.Text)?"대표값 동일 반영":CopyReasonInput.Text.Trim();
        await Execute(EN_MANUAL_INSPECTION_UI_ACTION.CopyReference,Request(keys) with {Reason=reason});
    }

    private async Task InputMeasurementValues(string key, long expectedVersion)
    {
        if (Busy || _controller is null || expectedVersion != _controller.Snapshot.Version
            || _snapshot.IsOffsetEditPending || !_points.TryGetValue(key, out var point) || point.IsMasked)
        { ShowMessage("현재 측정 기준과 선택점을 확인한 뒤 편차를 입력하세요."); return; }
        var controller = _controller;
        string? xText = null, yText = null;
        _actionConfirmationPending = true; RefreshButtons();
        try
        {
            xText = Input("X 편차 · µm · " + point.Identity, point.WorkingXUm);
            if (xText is null) return;
            yText = Input("Y 편차 · µm · " + point.Identity, point.WorkingYUm);
            if (yText is null) return;
        }
        catch (Exception error) { ShowMessage("편차 입력창을 열지 못했습니다. " + error.Message); return; }
        finally { _actionConfirmationPending = false; RefreshButtons(); }
        if (!ReferenceEquals(controller, _controller) || expectedVersion != controller.Snapshot.Version || Busy)
        { Refresh(); ShowMessage("입력 중 측정 기준이 바뀌었습니다. 현재 자료를 확인한 뒤 다시 입력하세요."); return; }
        if (!double.TryParse(xText, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.IsFinite(x)
            || !double.TryParse(yText, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) || !double.IsFinite(y))
        { ShowMessage("X/Y 편차는 유효한 숫자로 입력하세요. 값이 없으면 0으로 만들지 않습니다."); return; }
        await Execute(EN_MANUAL_INSPECTION_UI_ACTION.ForceMeasurement, Request([key], expectedVersion)
            with { ForcedXUm = x, ForcedYUm = y, Reason = "사용자 키패드 편차 입력 · 수동 OK 확정" });

        string? Input(string prompt, double? current)
        {
            var value = current is double number && double.IsFinite(number) ? number.ToString("G17", CultureInfo.InvariantCulture) : "";
            if (NumericInputForTesting is not null) return NumericInputForTesting(prompt, value);
            var dialog = new CValueInputDialog(prompt, value, EN_RECIPE_DATA_TYPE.Double, -1_000_000, 1_000_000)
            { Owner = Window.GetWindow(this) };
            return dialog.ShowDialog() == true ? dialog.ResultValue : null;
        }
    }

    private async void Apply_Click(object sender,RoutedEventArgs e)
        => await Execute(EN_MANUAL_INSPECTION_UI_ACTION.Apply,Request());

    private async void Rollback_Click(object sender,RoutedEventArgs e)
        => await Execute(EN_MANUAL_INSPECTION_UI_ACTION.UndoOffsets,Request());

    private async void AllowManual_Changed(object sender,RoutedEventArgs e)
    {
        if (_updating) return;
        await Execute(EN_MANUAL_INSPECTION_UI_ACTION.SetAllowManual,Request() with {AllowManual=AllowManualCheck.IsChecked==true});
    }

    private async void Limits_Click(object sender,RoutedEventArgs e)
    {
        try { await Execute(EN_MANUAL_INSPECTION_UI_ACTION.UpdateLimits,Request() with {DeltaLimitUm=Number(DeltaLimitInput),TotalLimitUm=Number(TotalLimitInput),ScannerFieldMm=Number(FieldLimitInput)}); }
        catch(Exception error) {ShowMessage(error.Message);}
    }

    private void Levels_Click(object sender,RoutedEventArgs e)
    {
        LeftInspectorSection.IsExpanded=true;LeftPaneTabs.SelectedIndex=4;SettingsSection.IsExpanded=true;SettingsPaneTabs.SelectedIndex=2;
        LevelSettingsPanel.Children.Clear();LevelSettingsPanel.Visibility=Visibility.Visible;
        var metric=new ComboBox {Style=(Style)FindResource("ManualComboBox")};
        SetChoices(metric,[new("MAX_AXIS","max(|X|, |Y|)"),new("RADIAL","벡터 크기")],_snapshot.LevelMetric);LevelSettingsPanel.Children.Add(metric);
        var grid=new Grid{Margin=new Thickness(0,4,0,4)};
        grid.ColumnDefinitions.Add(new(){Width=new GridLength(24)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(64)});
        grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        void Add(UIElement element,int row,int column){Grid.SetRow(element,row);Grid.SetColumn(element,column);grid.Children.Add(element);}
        Add(Label("L"),0,0);Add(Label("이름"),0,1);Add(Label("상한 µm"),0,2);
        var names=new List<TextBox>();var bounds=new List<TextBox>();
        for(var i=0;i<4;i++)
        {
            grid.RowDefinitions.Add(new(){Height=GridLength.Auto});Add(Label((i+1).ToString()),i+1,0);
            var name=new TextBox {Text=_snapshot.LevelNames.ElementAtOrDefault(i)??$"L{i+1}",Style=(Style)FindResource("ManualInput"),Margin=new Thickness(0,2,4,2)};names.Add(name);Add(name,i+1,1);
            if(i<3) {var bound=new TextBox {Text=_snapshot.LevelBoundsUm.ElementAtOrDefault(i).ToString(CultureInfo.InvariantCulture),Style=(Style)FindResource("ManualInput"),Margin=new Thickness(0,2,0,2)};bounds.Add(bound);Add(bound,i+1,2);}
            else Add(Label("초과"),i+1,2);
        }
        LevelSettingsPanel.Children.Add(grid);
        var save=Button("Level 적용",async()=>
        {
            try
            {
                var values=bounds.Select(Number).ToArray();if(values[0]>=values[1]||values[1]>=values[2]) throw new InvalidOperationException("경계값은 작은 순서로 입력하세요.");
                await Execute(EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels,Request() with {LevelMetric=Value(metric),LevelBoundsUm=values,LevelNames=ValidatedLevelNames(names.Select(n=>n.Text))});
            }
            catch(Exception error) {ShowMessage(error.Message);}
        });
        LevelSettingsPanel.Children.Add(save);
    }

    internal static string[] ValidatedLevelNames(IEnumerable<string> names)
    {
        var values = names.Select(n => n?.Trim() ?? "").ToArray();
        if (values.Length != 4 || values.Any(n => n.Length is < 1 or > 20) || values.Distinct(StringComparer.Ordinal).Count() != 4)
            throw new InvalidOperationException("네 레벨의 이름을 중복 없이 1~20자로 입력하세요.");
        return values;
    }
    private void Files_Click(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu(FilesButton);
        var previous = new MenuItem { Header = "파일 불러오기 전 작업으로 돌아가기", IsEnabled = !Busy && !_snapshot.HasIncomplete && !_snapshot.IsOffsetEditPending && _snapshot.CanReturnToPreviousWorkspace,
            ToolTip = _snapshot.IsOffsetEditPending ? "통합 Offset을 저장하거나 편집을 취소한 뒤 돌아가세요."
                : _snapshot.CanReturnToPreviousWorkspace ? "최초 파일을 열기 전 작업·보기로 복귀합니다. 현재 활성 보정과 실제 Stage 위치는 원복하지 않습니다." : "불러온 파일이 열려 있을 때 사용할 수 있습니다." };
        ToolTipService.SetShowOnDisabled(previous, true);
        previous.Click += async (_, _) => await ReturnToPreviousWorkspace(); menu.Items.Add(previous);
        menu.Items.Add(new Separator());
        foreach (var (label, action) in new[] {
            ("통합 Offset 파일 가져오기", EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle),
            ("최종 보정 파일 내보내기", EN_MANUAL_INSPECTION_UI_ACTION.ExportFinalOffsets),
            ("작업 Offset 파일 내보내기", EN_MANUAL_INSPECTION_UI_ACTION.ExportOffsetBundle),
            ("측정 결과 파일 불러오기", EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile),
            ("결과 CSV 내보내기", EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv) })
        {
            var imports = action is EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile or EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle;
            var item = new MenuItem { Header = label, IsEnabled = !Busy && (!imports || !_snapshot.HasIncomplete && !_snapshot.IsOffsetEditPending)
                    && (action != EN_MANUAL_INSPECTION_UI_ACTION.ImportOffsetBundle || _snapshot.Points.Count > 0)
                    && (action is not (EN_MANUAL_INSPECTION_UI_ACTION.ExportOffsetBundle or EN_MANUAL_INSPECTION_UI_ACTION.ExportFinalOffsets) || _snapshot.Points.Count > 0),
                ToolTip = imports && _snapshot.IsOffsetEditPending ? "통합 Offset을 저장하거나 편집을 취소한 뒤 다른 파일을 가져오세요."
                    : imports && _snapshot.HasIncomplete ? "미완료 측정의 잔여점을 먼저 완료하세요."
                    : action == EN_MANUAL_INSPECTION_UI_ACTION.ExportFinalOffsets ? "현재 저장된 최종 누적 Review Offset을 새로 확인하여 모델 전체의 파일로 내보냅니다. 미저장 초안은 포함하지 않고 기존 작업은 유지합니다."
                    : action == EN_MANUAL_INSPECTION_UI_ACTION.ExportOffsetBundle ? "현재 적용 통합 Offset을 보호 설정 참고값·최종 좌표와 JSON 하나로 내보냅니다. Init/파일 편집 중에는 미저장 작업값을 내보내며, 일반 측정의 미적용 후보는 제외합니다. 모델 저장이나 장비 이동은 실행하지 않습니다."
                    : label };
            ToolTipService.SetShowOnDisabled(item, true);
            item.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            item.Click += async (_, _) =>
            {
                var frozen = Request(action == EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv ? _snapshot.Points.Select(p => p.Key) : null);
                await Execute(action, frozen);
            }; menu.Items.Add(item);
        }
        FilesButton.ContextMenu = menu; menu.IsOpen = true;
    }

    private ContextMenu NewMenu(Button owner)
        => CManualInspectionDrawing.CreateContextMenu(owner);
    private async Task ReturnToPreviousWorkspace()
    {
        if (Busy || !_snapshot.CanReturnToPreviousWorkspace) return;
        if (_snapshot.HasIncomplete) {ShowMessage("미완료 측정을 먼저 완료하세요.");return;}
        await Execute(EN_MANUAL_INSPECTION_UI_ACTION.ReturnToPreviousWorkspace,Request());
    }

    private bool Confirm(string title, UIElement content, string confirmText, bool informationOnly = false)
    {
        if (ConfirmationForTesting is not null) return ConfirmationForTesting(title, DescriptionText(content), confirmText);
        var window = new Window { Title = title, Width = 790, SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 70), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize, Owner = Window.GetWindow(this) };
        window.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush"); window.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        var panel = new StackPanel { Margin = new Thickness(18) }; panel.Children.Add(content);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var accept = Button(confirmText, () => window.DialogResult = true, !informationOnly); accept.IsDefault = true;
        buttons.Children.Add(accept);
        if (!informationOnly) { var cancel = Button("취소", () => window.DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel); }
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { window.DialogResult = false; e.Handled = true; } };
        panel.Children.Add(buttons);
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return window.ShowDialog() == true;
    }
    private static string DescriptionText(UIElement element) => element switch
    {
        TextBlock text => text.Text, Panel panel => string.Join("\n", panel.Children.OfType<UIElement>().Select(DescriptionText)),
        ContentControl content when content.Content is UIElement child => DescriptionText(child),
        _ => ""
    };
    private void ShowMessage(string message)
    {
        // Keep a local validation/cancel notice for the current immutable source frame. A real
        // controller update replaces it; a mere layout/selection refresh must not hide the error.
        _localMessage = message;
        _localMessageVersion = _controller?.Snapshot.Version ?? _snapshot.Version;
        RefreshMessage();
    }
    private void RefreshMessage()
    {
        var message = string.IsNullOrWhiteSpace(_localMessage) ? _snapshot.Message : _localMessage;
        MessageText.Text = message; MessageText.ToolTip = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && PositionMap.TryCancelGesture()) { e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && key == Key.F)
        { LeftInspectorSection.IsExpanded = true; LeftPaneTabs.SelectedIndex=0; WorkInfoSection.IsExpanded = true; SearchSection.IsExpanded = true; SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Left or Key.Right or Key.Up or Key.Down
            && Ancestor<TextBox>(e.OriginalSource as DependencyObject) is null && Ancestor<ComboBox>(e.OriginalSource as DependencyObject) is null)
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 100 : 40;
            PositionMap.PanBy(key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), _ => new Vector(0, step) });
            e.Handled = true; return;
        }
        if (e.Key == Key.Escape && Busy) { e.Handled = true; await Execute(EN_MANUAL_INSPECTION_UI_ACTION.Stop); }
        else if ((e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            && Ancestor<TextBox>(e.OriginalSource as DependencyObject) is null
            && Ancestor<ComboBox>(e.OriginalSource as DependencyObject) is null)
        { PositionMap.ShowOperationMenu(); e.Handled = true; }
    }
}
