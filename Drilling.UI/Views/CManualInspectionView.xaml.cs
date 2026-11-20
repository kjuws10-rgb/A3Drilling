namespace Drilling.UI.Views;

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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
    private sealed class GridRow(CManualInspectionUiPoint point, bool selected, Action<GridRow> changed, int appliedLevel, int measurementLevel) : INotifyPropertyChanged
    {
        private bool _selected = selected;
        public CManualInspectionUiPoint Point { get; } = point;
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
    private string _scatterScope = "CELL";
    private int _scatterHead;
    private bool _scatterOriginal;
    private int _page;
    private bool _updating;
    private bool _attached;
    private int _requests;
    private int _refreshPending;
    private long _lastLimitsVersion = -1;
    private string _lastLimitsSignature = "";
    private const int PageSize = 50;
    private bool Busy => _snapshot.IsBusy || _requests > 0;
    // A narrow UI-test seam: tests inspect the exact description and accept/cancel without a modal loop.
    internal Func<string, string, string, bool>? ConfirmationForTesting { get; set; }

    public CManualInspectionView()
    {
        InitializeComponent();
        _updating = true;
        SetChoices(JudgeFilter, [new("ALL", "전체 결과"), new("MEASURED", "측정됨 · 보정 포함"), new("OK", "실측 OK"), new("NG", "실측 NG"), new("ERROR", "최근 수신 오류"), new("UNMEASURED", "미측정")], "ALL");
        SetChoices(SourceFilter, [new("ALL", "전체 출처"), new("MEASURED", "MEASURED"), new("IMPORTED", "IMPORTED · 조회 자료"), new("CORRECTION_BASED", "보정 기반 측정"), new("INTERPOLATED", "INTERPOLATED"), new("MANUAL", "MANUAL"), new("KEPT", "KEPT")], "ALL");
        SetChoices(SortFilter, [new("shot", "Shot 순"), new("cell", "Cell 순"), new("error", "편차 크기 순")], "shot");
        SetChoices(ColorFilter, [new("applied", "적용 보정값 · Level"), new("level", "측정 편차 · Level"), new("judge", "측정 판정 · OK/NG")], "applied");
        SetChoices(ScatterDataCombo, [new("WORKING", "작업 결과 · 대표값 반영"), new("ORIGINAL", "원측정 기록")], "WORKING");
        SetChoices(MeasurementModeCombo, [new("P2P", "P2P · 선택점"), new("FLYING", "Flying · 해당 행 전체")], "P2P");
        _updating = false;
        PositionMap.SelectionRequested += Map_SelectionRequested;
        PositionMap.OperationRequested += Map_OperationRequested;
        PositionMap.MessageRequested += ShowMessage;
        PositionMap.ViewChanged += (_, _) => ZoomResetButton.Content = $"{PositionMap.Zoom * 100:0}% · 맞춤";
        ScatterPlot.SelectionRequested += Scatter_SelectionRequested;
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
        if (!IsInitialized || WorkspaceBody is null || MapPanel.Child is not Grid mapLayout) return;
        var wide = ActualWidth >= 1300 && ActualHeight >= 700;
        if (_wideLayout != wide)
        {
            _wideLayout = wide;
            InspectorColumn.Width = new GridLength(wide ? 320 : 280);
            PointSummaryPanel.Padding = new Thickness(wide ? 12 : 6);
            PointTitleText.FontSize = wide ? 23 : 19;
            PointAppliedText.FontSize = wide ? 15 : 13;
            PointErrorText.FontSize = wide ? 13 : 11;
            SelectionSummaryText.Visibility = SelectionLevelsText.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        }
        // Fit the normal folded workspace to its viewport. If optional content/error text takes
        // more space, grow the scroll content instead of clipping a minimum-height drawing surface.
        var minimum = ResultsSection.IsExpanded || ScatterSection.IsExpanded ? 0
            : mapLayout.RowDefinitions.Where((_, i) => i != 4).Sum(row => row.ActualHeight)
                + PositionMap.MinHeight + 2 + MapPanel.Padding.Top + MapPanel.Padding.Bottom
                + MapPanel.BorderThickness.Top + MapPanel.BorderThickness.Bottom
                + MapPanel.Margin.Top + MapPanel.Margin.Bottom
                + WorkspaceBody.RowDefinitions.Skip(1).Sum(row => row.ActualHeight);
        if (double.IsFinite(minimum) && Math.Abs(WorkspaceBody.MinHeight - Math.Ceiling(minimum)) > .5)
            WorkspaceBody.MinHeight = Math.Ceiling(minimum);
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
            if (pointsChanged)
            {
                _projectedPoints = _snapshot.Points;
                _points = _snapshot.Points.GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                _cellChoices = _snapshot.Points.Select(p => p.CellNo).Distinct().OrderBy(x => x).ToArray();
                // Zero is the "all heads" UI choice; unassigned/masked H00 points remain in the
                // map dataset but must not add a second, indistinguishable value-zero choice.
                _headChoices = _snapshot.Points.Select(p => p.HeadNo).Where(head => head is >= 1 and <= 8).Distinct().OrderBy(x => x).ToArray();
            }
            _selected.RemoveWhere(k => !_points.ContainsKey(k));
            var workspaceIdentity = _snapshot.RecipeId + "|" + _snapshot.RunId;
            if (_runId != workspaceIdentity)
            {
                _runId = workspaceIdentity; _selected.Clear(); _focusKey = null; _representativeKey = null;
                _cell = _snapshot.Points.FirstOrDefault()?.CellNo ?? 0; _head = 0; _judge = _source = "ALL";
                _query = ""; _page = 0; SearchBox.Text = "";
                _localMessage = "";
                _color = "applied"; _scatterScope = "CELL"; _scatterHead = 0; _scatterOriginal = false;
                PositionMap.IsPanTool = false; PositionMap.Cursor = Cursors.Cross;
                _scatterCells.Clear(); if (_cell != 0) _scatterCells.Add(_cell);
            }
            // External Review imports can replace a point without changing its key. A previously
            // selected direct reference must not remain usable after it becomes imported/proxy/B-only.
            if (_representativeKey is not null && (!_points.TryGetValue(_representativeKey, out var reference) || !reference.CanBeReference))
                _representativeKey = null;
            var cells = _cellChoices;
            var heads = _headChoices;
            if (_cell != 0 && !cells.Contains(_cell)) _cell = cells.FirstOrDefault();
            if (_head != 0 && !heads.Contains(_head)) _head = 0;
            if (pointsChanged)
            {
                SetChoices(CellFilter, new[] { new Choice("0", "전체 Cell") }.Concat(cells.Select(n => new Choice(n.ToString(), $"Cell {n}"))), _cell.ToString());
                SetChoices(HeadFilter, new[] { new Choice("0", "전체 Head") }.Concat(heads.Select(n => new Choice(n.ToString(), $"Head {n}"))), _head.ToString());
            }
            else { SelectValue(CellFilter, _cell.ToString()); SelectValue(HeadFilter, _head.ToString()); }
            SelectValue(JudgeFilter, _judge); SelectValue(SourceFilter, _source); SelectValue(SortFilter, _sort);
            SelectValue(ColorFilter, _color); SelectValue(ScatterDataCombo, _scatterOriginal ? "ORIGINAL" : "WORKING");
            _filtered = FilteredPoints();
            ContextText.Text = $"{(_snapshot.IsSimulation ? "SIM · 설비 명령 없음" : "실제 설비")}  |  Recipe {_snapshot.RecipeId}{(_snapshot.IsReadOnly ? " · 조회 전용" : "")}";
            ContextText.ToolTip = $"Recipe {_snapshot.RecipeId}\nRun {_snapshot.RunId}\n활성 보정 {_snapshot.ActiveRevisionId}";
            StatusText.Text = _snapshot.StatusText + (string.IsNullOrWhiteSpace(_snapshot.SynchronizationText) ? "" : "\n" + _snapshot.SynchronizationText);
            if (_snapshot.SyncPending) StatusText.Text += "\n새 결과 대기 중 · 현재 작업을 마치면 반영합니다.";
            StatusText.ToolTip = StatusText.Text;
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, _snapshot.IsReadOnly ? "ButtonWarningBrush" : Busy ? "AccentTextBrush" : "TextPrimaryBrush");
            StagePositionText.Text = $"마지막 도착 확인  X {CManualInspectionUiPoint.Number(_snapshot.StageXmm)} / Y {CManualInspectionUiPoint.Number(_snapshot.StageYmm)} mm";
            PersistenceText.Text = _snapshot.PersistenceText + (string.IsNullOrWhiteSpace(_snapshot.SourcePath) ? "" : " · " + _snapshot.SourcePath);
            PersistenceText.ToolTip = PersistenceText.Text;
            if (_localMessageVersion != _snapshot.Version) _localMessage = "";
            RefreshMessage();
            var scoped = _snapshot.Points.Where(p => (_cell == 0 || p.CellNo == _cell) && (_head == 0 || p.HeadNo == _head)).ToArray();
            PositionMap.Configure(_snapshot, scoped, _filtered, _selected, FocusPoint?.Key, _representativeKey, _color, $"{_cell}|{_head}");
            var visibleSelection = _filtered.Count(p => _selected.Contains(p.Key));
            var hiddenSelection = _selected.Count - visibleSelection;
            SelectionCountText.Text = $"선택 {_selected.Count:N0}점" + (hiddenSelection > 0 ? $" · 숨김 {hiddenSelection:N0}점" : "") + $" · 지도 {_filtered.Count:N0}점";
            SelectionCountText.ToolTip = $"선택 집합 전체 {_selected.Count:N0}점 = 현재 지도에서 보이는 선택 {visibleSelection:N0}점 + 다른 Cell/Head·필터로 숨겨진 선택 {hiddenSelection:N0}점.\n지도 {_filtered.Count:N0} / 전체 {_snapshot.Points.Count:N0}점. 우클릭 동작의 확인창에서 실제 실행 대상을 다시 확인합니다.";
            FilterNoticeText.Text = _head != 0 || _judge != "ALL" || _source != "ALL" || _query.Length > 0
                ? "세부 필터가 지도·표에 적용되어 있습니다. 산포도는 별도 범위를 사용합니다." : "";
            FilterNoticeText.Visibility = string.IsNullOrWhiteSpace(FilterNoticeText.Text) ? Visibility.Collapsed : Visibility.Visible;
            MapLegendText.Text = MapLegend();
            MapLegendText.ToolTip = MapLegendText.Text;
            MapLegendItems.ToolTip = MapLegendText.Text;
            RefreshMapLegend();
            ColorDescriptionText.Text = _color == "applied" ? "색상 = 저장된 활성 보정 B · 미적용 후보 F 제외"
                : _color == "level" ? "색상 = 측정 편차 · 원편차가 없으면 기존 보정 B 기준"
                : "색상 = 측정 판정 · 보정량 Level과 별도";
            ColorDescriptionText.ToolTip = _color == "applied"
                ? "기존 활성 보정값이 있는 위치는 측정 이력이 있는 점입니다. Rough/Fine/복사 모두 같은 공통 Level로 표시합니다. B는 누적 보정량이며 현재 가공의 잔류 오차를 뜻하지 않습니다."
                : _color == "level" ? "직접 측정 편차를 우선 표시합니다. 원편차가 없는 점은 적용 보정 B를 표시하고 □로 구분합니다. 보정 기반 편차는 현재 가공의 잔류 오차를 직접 측정한 값이 아닙니다."
                : "수신한 측정 판정과 기존 보정 기반 이력을 구분합니다. OK/NG와 Level은 서로 다른 기준입니다.";
            RefreshRows(); RefreshReference(); RefreshCorrection(); RefreshDetails(); RefreshScatter(); RefreshButtons();
        }
        finally { _updating = false; }
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
    private void RefreshRows()
    {
        var pages = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)PageSize)); _page = Math.Clamp(_page, 0, pages - 1);
        ResultGrid.ItemsSource = _filtered.Skip(_page * PageSize).Take(PageSize).Select(p => new GridRow(p, _selected.Contains(p.Key), RowCheckChanged,
            CManualInspectionDrawing.AppliedLevel(p,_snapshot),CManualInspectionDrawing.Level(p,_snapshot))).ToArray();
        PageText.Text = $"{_page + 1} / {pages} 페이지";
        PreviousPageButton.IsEnabled = _page > 0; NextPageButton.IsEnabled = _page + 1 < pages;
        ResultStatsText.Text = $"측정됨 {_filtered.Count(p => p.HasMeasurementHistory):N0} (보정 기반 {_filtered.Count(p => p.UsesAppliedMeasurement):N0}) · OK {_filtered.Count(p => p.WorkingJudge == "OK"):N0} · NG {_filtered.Count(p => p.WorkingJudge == "NG"):N0} · 최근 오류 {_filtered.Count(p => p.WorkingJudge == "ERROR"):N0} · 미측정 {_filtered.Count(p => !p.IsMasked && !p.HasMeasurementHistory && p.WorkingJudge == "UNMEASURED"):N0}";
    }
    private void RefreshReference()
    {
        var reference = _representativeKey is not null && _points.TryGetValue(_representativeKey, out var p) ? p : null;
        if (reference is null) ReferenceText.Text = "실측 OK 또는 NG 점을 클릭한 뒤 ‘대표값으로 지정’하세요.";
        else
        {
            var (x, y) = ReferenceValues(reference);
            ReferenceText.Text = $"★ {reference.Key} · {reference.WorkingJudge}\n대표 최종 F {CManualInspectionUiPoint.Pair(x, y)} µm · 대표 편차 {reference.WorkingErrorText} µm";
        }
        ReferenceText.SetResourceReference(TextBlock.ForegroundProperty, reference?.WorkingJudge == "NG" ? "ReviewMaskTextBrush" : "AccentTextBrush");
        var count = _selected.Count(k => k != _representativeKey && _points.TryGetValue(k, out var q) && !q.IsMasked);
        CopyReferenceButton.Content = $"같은 오프셋 · 상태 적용 · {count}점";
    }
    private (double? X, double? Y) ReferenceValues(CManualInspectionUiPoint point)
    {
        // The controller resolves consumed measurements and active/draft revisions in
        // the engine. The preview must use that same F rather than re-add displayed raw.
        if (point.ReferenceFinalXUm.HasValue || point.ReferenceFinalYUm.HasValue)
            return (point.ReferenceFinalXUm, point.ReferenceFinalYUm);
        return !string.Equals(_snapshot.CandidateStatus, "APPLIED", StringComparison.OrdinalIgnoreCase)
            && point.CandidateXUm.HasValue && point.CandidateYUm.HasValue
                ? (point.CandidateXUm, point.CandidateYUm) : (point.BaseXUm + point.WorkingXUm, point.BaseYUm + point.WorkingYUm);
    }
    private void RefreshCorrection()
    {
        var applied = string.Equals(_snapshot.CandidateStatus, "APPLIED", StringComparison.OrdinalIgnoreCase);
        CandidateStatusText.Text = applied ? "적용 완료 · 저장된 B에 반영됨"
            : _snapshot.CanApply && _snapshot.ValidationPassed ? "검증 완료 · 적용 전"
            : _snapshot.CandidateRows.Count > 0 ? "후보 준비됨 · 아직 적용되지 않음" : "활성 보정 B 유지 중";
        CandidateStatusText.ToolTip = "후보 상태: " + _snapshot.CandidateStatus;
        CandidateStatusText.SetResourceReference(TextBlock.ForegroundProperty, applied || _snapshot.CanApply && _snapshot.ValidationPassed ? "StatusReadyBrush" : "AccentTextBrush");
        CorrectionSection.Header = applied ? "보정 · 적용 완료" : _snapshot.CandidateRows.Count > 0 ? $"보정 확인 · 후보 {_snapshot.CandidateRows.Count:N0}점" : "보정 확인 · 적용";
        CandidateContextText.Text = $"선택 전체 {_selected.Count:N0}점 · 후보 {_snapshot.CandidateRows.Count:N0}점\nB(현재) + Δ(증감) → F(적용 후)";
        CandidateContextText.ToolTip = "기준 활성 보정 " + _snapshot.ActiveRevisionId + "\n후보 F는 검증값 적용 전까지 지도 활성 B 색상에 포함되지 않습니다.";
        CandidateGrid.ItemsSource = _snapshot.CandidateRows.Take(8).ToArray();
        CandidateGrid.Visibility = _snapshot.CandidateRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CandidateRowCountText.Text = _snapshot.CandidateRows.Count > 8 ? $"처음 8점 표시 / 전체 {_snapshot.CandidateRows.Count:N0}점 적용" : "";
        AllowManualCheck.IsChecked = _snapshot.AllowManual;
        IncompleteText.Text = _snapshot.HasIncomplete ? $"미완료 측정 {_snapshot.RemainingCount:N0}점이 있습니다. 잔여 재실행 후 보정을 검증·적용하세요." : "";
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
        var selected = _selected.Where(_points.ContainsKey).Select(k => _points[k]).ToArray();
        var visibleKeys = _filtered.Select(point => point.Key).ToHashSet(StringComparer.Ordinal);
        var hidden = selected.Count(point => !visibleKeys.Contains(point.Key));
        PointScopeText.Text = $"포커스 1점 · 선택 {selected.Length:N0}점" + (hidden > 0 ? $" (숨김 {hidden:N0})" : "");
        PointScopeText.ToolTip = "아래 상세는 포커스한 한 점입니다. 선택 통계는 다른 Cell·Head 또는 필터로 숨겨진 선택까지 포함한 전체 선택 집합입니다.";
        PointTitleText.Text = p is null ? "선택 없음" : $"Cell {p.CellNo} · Head {p.HeadNo}";
        if (p is null) PointScopeText.Text = "포커스 위치 없음";
        PointIdentityText.Text = p is null ? "지도에서 점을 선택하세요." : $"Shot {p.ShotNo} · Beam {p.BeamNo}";
        PointJudgesText.Text = p is null ? "" : p.MeasurementStatusText;
        PointJudgesText.SetResourceReference(TextBlock.ForegroundProperty, p?.WorkingJudge == "NG" ? "ReviewMaskTextBrush" : p?.WorkingJudge == "ERROR" ? "ButtonWarningBrush" : "TextPrimaryBrush");
        var measurementLevel = p is null ? -1 : CManualInspectionDrawing.Level(p, _snapshot);
        PointErrorText.Text = p is null ? "" : $"편차 {p.MeasurementErrorText} µm\n{(measurementLevel >= 0 ? $"측정 L{measurementLevel + 1} · " : "")}{(p.UsesAppliedMeasurement ? "□ 보정 기반" : p.IsProxy ? "◇ 대표값 복사" : p.HasWorkingMeasurement ? "● 측정 결과" : "유효 편차 없음")}";
        PointErrorText.ToolTip = p is null ? "" : $"표시 편차 X / Y {p.MeasurementErrorText} µm · DESIGN 좌표계\n{p.MeasurementBasisText}\n작업 편차 {p.WorkingErrorText} µm\n원측정 {p.OriginalErrorText} µm"
            + (p.UsesAppliedMeasurement ? "\n기존 B는 측정에서 유래한 보정량입니다. 현재 가공의 잔류 오차를 새로 측정한 값은 아닙니다." : "");
        var appliedLevel = p is null ? -1 : CManualInspectionDrawing.AppliedLevel(p,_snapshot);
        PointAppliedText.Text = p is null ? "" : p.HasAppliedOffset
            ? $"적용 B  {p.BaseOffsetText} µm\n{(appliedLevel >= 0 ? $"보정 L{appliedLevel + 1} · {_snapshot.LevelNames.ElementAtOrDefault(appliedLevel)}" : "Level 확인 필요")}"
            : "적용된 보정값 없음";
        PointAppliedText.ToolTip = "현재 저장된 활성 보정 B · DESIGN 좌표계 X / Y µm. 보정량의 Level이며 측정 편차 Level 및 OK/NG 판정과 구분합니다.";
        SelectionSummaryText.Text = selected.Length == 0 ? "Ctrl+클릭 또는 드래그로 비교할 점을 선택하세요."
            : $"선택 전체 {selected.Length:N0}점 · 숨김 {hidden:N0}점\n측정 이력 {selected.Count(point => point.HasMeasurementHistory):N0}점 · 그중 보정 기반 {selected.Count(point => point.UsesAppliedMeasurement):N0}점";
        SelectionSummaryText.ToolTip = "현재 선택 집합 통계입니다. 화면에 보이는 Cell 전체 통계나 새로 측정한 점의 개수가 아닙니다. 기존 B 기반 이력과 대표값 복사도 측정 이력에 포함됩니다.";
        var levels = selected.Select(point => _color == "applied" ? CManualInspectionDrawing.AppliedLevel(point, _snapshot) : CManualInspectionDrawing.Level(point, _snapshot)).ToArray();
        SelectionLevelsText.Text = selected.Length == 0 ? "색상 기준과 공통 Level은 모든 Cell·Head에 동일하게 적용됩니다."
            : (_color == "applied" ? "선택 보정 B Level" : "선택 측정 편차 Level") + "\n" + string.Join(" · ", Enumerable.Range(0, 4).Select(level => $"L{level + 1} {levels.Count(value => value == level):N0}"))
                + (levels.Any(level => level < 0) ? $" · 미분류 {levels.Count(level => level < 0):N0}" : "");
        SelectionLevelsText.ToolTip = "보정 B Level은 누적 활성 보정량, 측정 Level은 원편차 우선·없으면 기존 B 기반입니다. OK/NG 판정을 변경하거나 잔류 오차를 추정하지 않습니다.";
        PointDetailsText.Text = p is null ? "" : $"현재 보정 B  {p.BaseOffsetText} µm\n후보 보정 F  {p.CandidateOffsetText} µm"
            + (p.CandidateXUm.HasValue && p.CandidateYUm.HasValue ? $"\n후보 증감 Δ  {CManualInspectionUiPoint.Pair(p.CandidateXUm - p.BaseXUm, p.CandidateYUm - p.BaseYUm)} µm" : "")
            + $"\n설계 X / Y  {p.DesignXmm:0.000} / {p.DesignYmm:0.000} mm\n대표 여부  {(p.Key == _representativeKey ? "★ 대표값" : "대상 위치")}"
            + (p.IsProxy ? $"\n◇ 대표값 복사 · MANUAL\n기준 {p.ReferenceKey}\n새 실측이 아닌 대표값을 사용 중입니다." : "")
            + (string.IsNullOrWhiteSpace(p.DisplayedMeasurementOrigin) ? "" : "\n측정 출처: " + p.DisplayedMeasurementOrigin)
            + (string.IsNullOrWhiteSpace(p.MeasurementSourcePath) ? "" : "\n" + p.MeasurementSourcePath) + "\n" + p.Key;
        JobProgressText.Text = Busy ? $"진행 {_snapshot.CompletedCount:N0} / {_snapshot.TotalCount:N0}점" : _snapshot.HasIncomplete ? $"잔여 {_snapshot.RemainingCount:N0}점 · 자동 보정은 재개하지 않습니다." : "대기";
        JobProgressBar.Value = _snapshot.TotalCount <= 0 ? 0 : Math.Clamp(_snapshot.CompletedCount * 100.0 / _snapshot.TotalCount, 0, 100);
        JobProgressText.Visibility = JobProgressBar.Visibility = Busy || _snapshot.HasIncomplete ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = _snapshot.HasIncomplete ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshButtons()
    {
        var idle = !Busy; var operate = idle && !_snapshot.IsReadOnly && _snapshot.Points.Count > 0;
        var hasSelection = _selected.Any(k => _points.TryGetValue(k, out var p) && !p.IsMasked);
        foreach (var b in new[] { LoadRecipeButton, FilesButton }) b.IsEnabled = idle;
        CalculateButton.IsEnabled = operate && hasSelection && !_snapshot.HasIncomplete;
        ValidateButton.IsEnabled = operate && !_snapshot.HasIncomplete && _snapshot.CandidateRows.Count > 0 && !string.Equals(_snapshot.CandidateStatus, "APPLIED", StringComparison.OrdinalIgnoreCase);
        ApplyButton.IsEnabled = operate && !_snapshot.HasIncomplete && _snapshot.CanApply && _snapshot.ValidationPassed;
        ApplyButton.ToolTip = ApplyButton.IsEnabled ? "검증한 후보 전체를 새 활성 보정으로 저장합니다. 적용 전 확인창에서 B→Δ→F를 확인하세요."
            : _snapshot.IsReadOnly ? "조회 전용 자료입니다. 현재 Recipe/Run을 다시 불러오세요."
            : Busy ? "현재 동작 정리 후 사용할 수 있습니다."
            : _snapshot.HasIncomplete ? "남은 측정을 완료한 뒤 계산·검증하세요."
            : "후보 계산과 검증을 통과해야 활성 보정으로 저장할 수 있습니다.";
        RollbackButton.IsEnabled = operate && _snapshot.CanRollback;
        SetReferenceButton.IsEnabled = operate && FocusPoint?.CanBeReference == true;
        ClearReferenceButton.IsEnabled = idle && _representativeKey is not null;
        CopyReferenceButton.IsEnabled = operate && !_snapshot.HasIncomplete && _representativeKey is not null
            && _points.TryGetValue(_representativeKey, out var reference) && reference.CanBeReference
            && _selected.Any(k => k != _representativeKey && _points.TryGetValue(k, out var p) && !p.IsMasked);
        UndoCopyButton.IsEnabled = operate && !_snapshot.HasIncomplete && _snapshot.CanUndoCopy;
        UndoCopyButton.Visibility = _snapshot.CanUndoCopy ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = Busy;
        RetryButton.IsEnabled = operate && _snapshot.HasIncomplete && _snapshot.RemainingCount > 0;
        AllowManualCheck.IsEnabled = SaveLimitsButton.IsEnabled = LevelsButton.IsEnabled = idle && !_snapshot.IsReadOnly;
        MeasurementModeCombo.IsEnabled = idle;
    }

    private string MapLegend() => (_color switch
    {
        "applied" => "적용 보정 B · Rough/Fine/대표 복사 모두 같은 DESIGN µm 기준 · 회색: 미적용",
        "level" => "실측/보정 기반 편차 · 공통 Level · □ 보정 기반 측정",
        _ => "실측 OK/NG · □ 보정 기반 측정 · 수신 오류 ! · 미측정 ·"
    }) + "   |   Masking × · 선택 테두리 · ★ 대표 · ◇ 복사";

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
            foreach (var (name, color) in new[] { ("실측 OK", "#3B9870"), ("실측 NG", "#D16776"), ("□ 보정 기반 측정", "#638CA8"), ("수신 오류 !", "#D39A45"), ("미측정 ·", "#DBE4EA") }) AddLegend(MapLegendItems, name, CManualInspectionDrawing.Color(color));
        AddLegend(MapLegendItems, "Masking ×", CManualInspectionDrawing.Resource(this, "ReviewMaskBackgroundBrush", Brushes.LightGray));
    }

    private void RefreshScatter()
    {
        var currentCell = _cell != 0 ? _cell : FocusPoint?.CellNo ?? _snapshot.Points.FirstOrDefault()?.CellNo ?? 0;
        var signature = FormattableString.Invariant($"{_scatterScope}|{currentCell}|{_scatterHead}|{_scatterOriginal}|{_snapshot.ToleranceXUm:R}|{_snapshot.ToleranceYUm:R}")
            + "|" + string.Join(",", _scatterCells.OrderBy(cell => cell));
        // Preserve the original-only clone array and chart controls on status-only snapshots.
        // Selection, point data, scope, source kind and tolerance changes still rebuild correctly.
        if (ReferenceEquals(_scatterProjectedPoints, _snapshot.Points) && _scatterRenderSignature == signature
            && _scatterRenderedSelection.SetEquals(_selected)) return;
        _scatterProjectedPoints = _snapshot.Points; _scatterRenderSignature = signature;
        _scatterRenderedSelection.Clear(); _scatterRenderedSelection.UnionWith(_selected);
        var range = _snapshot.Points.Where(p => _scatterScope == "CELL" ? p.CellNo == currentCell : _scatterScope == "HEAD" ? _scatterHead == 0 || p.HeadNo == _scatterHead : _scatterCells.Contains(p.CellNo)).ToArray();
        var display = _scatterOriginal ? range.Select(p => p with { WorkingJudge = p.OriginalJudge, WorkingXUm = p.OriginalXUm, WorkingYUm = p.OriginalYUm, IsProxy = false, HasAppliedOffset = false }).ToArray() : range;
        ScatterPlot.Configure(display, _selected, _scatterScope == "HEAD", _snapshot.ToleranceXUm, _snapshot.ToleranceYUm,
            _scatterScope == "CELLS" && _scatterCells.Count == 0 ? "산포도를 볼 Cell을 위에서 선택하세요." : "이 범위에 유효한 측정값이 없습니다.");
        var m = ScatterPlot.Metrics;
        ScatterMetricsText.Text = $"전체 위치  {m.Total:N0}점\n{(_scatterOriginal ? "유효 원측정값" : "표시한 작업값")}  {m.Count:N0}점"
            + (_scatterOriginal ? "" : $"\n대표값 복사  {m.ProxyCount:N0}점\n□ 보정 기반  {m.CorrectionBasedCount:N0}점")
            + $"\n\n평균 ΔX / ΔY\n{CManualInspectionUiPoint.Pair(m.MeanX, m.MeanY)} µm\n\n표준편차 σX / σY\n{CManualInspectionUiPoint.Number(m.SigmaX)} / {CManualInspectionUiPoint.Number(m.SigmaY)} µm\n\n최대 벡터 편차\n{CManualInspectionUiPoint.Number(m.MaximumVector)} µm";
        ScatterExcludedText.Text = $"제외: Masking {m.Masked} · 미측정 {m.Unmeasured} · 수신 오류 {m.Errors} · 수치 없음 {m.Invalid}점. σ는 모집단 기준입니다.";
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
        ScatterLegendText.Text = string.Join(" · ", groups.Select(g => g.Group)) + "   ＋ 평균 · 중심 (0,0): 목표   " + (_scatterOriginal ? "원측정 기록만 표시" : "● 실측 · ◇ 대표값 복사 · □ 보정 기반 측정");
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
            var panel = new WrapPanel();
            foreach (var cell in _snapshot.Points.Select(p => p.CellNo).Distinct().OrderBy(x => x))
            {
                var check = new CheckBox { Content = "C" + cell, IsChecked = _scatterCells.Contains(cell), Style = (Style)FindResource("ManualCheck"), Margin = new Thickness(0, 0, 10, 6) };
                check.Checked += (_, _) => { if (!_updating) { _scatterCells.Add(cell); Refresh(); } };
                check.Unchecked += (_, _) => { if (!_updating) { _scatterCells.Remove(cell); Refresh(); } }; panel.Children.Add(check);
            }
            ScatterScopeControls.Children.Add(panel);
            var buttons = new WrapPanel();
            buttons.Children.Add(Button("선택점의 Cell 가져오기", () => { _scatterCells.Clear(); foreach (var p in _snapshot.Points.Where(p => _selected.Contains(p.Key))) _scatterCells.Add(p.CellNo); Refresh(); }));
            buttons.Children.Add(Button("모든 셀", () => { _scatterCells.Clear(); foreach (var p in _snapshot.Points) _scatterCells.Add(p.CellNo); Refresh(); }));
            buttons.Children.Add(Button("비우기", () => { _scatterCells.Clear(); Refresh(); })); ScatterScopeControls.Children.Add(buttons);
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
        if (e.FocusKey is not null) _focusKey = e.FocusKey;
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
        if (row.Point.IsMasked) { ShowMessage("Masking 위치는 측정 대상으로 선택하지 않습니다."); e.Handled = true; return; }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        ChangeSelection(new([row.Point.Key], row.Point.Key, ctrl, ctrl)); e.Handled = true;
    }
    private void ResultGrid_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = Ancestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as GridRow; if (row is null) return;
        if (row.Point.IsMasked) { ShowMessage("Masking 위치는 이동·측정·보정 대상에서 제외됩니다."); e.Handled = true; return; }
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
    private void Scatter_Changed(object sender, SelectionChangedEventArgs e) { if (_updating) return; _scatterOriginal = Value(ScatterDataCombo) == "ORIGINAL"; Refresh(); }
    private void ScatterScope_Click(object sender, RoutedEventArgs e) { _scatterScope = ((Button)sender).Tag as string ?? "CELL"; Refresh(); }
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
        if (Busy || FocusPoint?.CanBeReference != true) { ShowMessage("대표값 복사본이 아닌 유효한 실측 OK 또는 NG 점을 선택하세요."); return; }
        var point = FocusPoint; var version = _snapshot.Version;
        var panel = new StackPanel(); panel.Children.Add(Label($"{point.Identity}를 복사의 대표로 지정합니다. 판정 {point.WorkingJudge}와 대표 최종 보정값을 이후 선택점에 복사할 수 있습니다. 지금은 값이나 활성 파일을 변경하지 않습니다."));
        if (!Confirm("대표점 지정", panel, "확인 · 대표점 지정")) return;
        if (_snapshot.Version != version || !_points.TryGetValue(point.Key,out var current) || !current.CanBeReference)
        { ShowMessage("확인 중 측정 기준이 바뀌었습니다. 대표점을 다시 선택하세요."); return; }
        _representativeKey = point.Key; ShowMessage("대표값을 지정했습니다. Ctrl 클릭 또는 드래그로 다른 대상 위치를 선택하세요."); Refresh();
    }
    private void ClearReference_Click(object sender, RoutedEventArgs e) { _representativeKey = null; Refresh(); }

    private CManualInspectionUiRequest Request(IEnumerable<string>? keys = null, long? expectedVersion = null) => new()
    {
        ExpectedVersion = expectedVersion ?? _snapshot.Version,
        Keys = (keys ?? _snapshot.Points.Where(p => _selected.Contains(p.Key)).Select(p => p.Key)).Distinct(StringComparer.Ordinal).ToArray(),
        FocusKey = FocusPoint?.Key, ReferenceKey = _representativeKey,
        MeasurementMode = Value(MeasurementModeCombo, "P2P"),
        Scenario = "same", Fault = "NONE", Speed = 1,
        AllowManual = AllowManualCheck.IsChecked == true
    };
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
            if (current.IsReadOnly && RequiresWritableSource(action))
            { Refresh(); ShowMessage("조회 전용 자료에서는 이동·측정·보정·설정 변경을 실행할 수 없습니다. 현재 Recipe/Run을 다시 불러오세요."); return; }
        }
        _requests++; RefreshButtons();
        try { await _controller.ExecuteAsync(action, frozen); }
        catch (Exception ex) { ShowMessage(ex.Message); }
        finally { _requests--; Refresh(); }
    }

    private static bool RequiresWritableSource(EN_MANUAL_INSPECTION_UI_ACTION action) => action is
        EN_MANUAL_INSPECTION_UI_ACTION.Move or EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure or EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect
        or EN_MANUAL_INSPECTION_UI_ACTION.Measure or EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining
        or EN_MANUAL_INSPECTION_UI_ACTION.Calculate or EN_MANUAL_INSPECTION_UI_ACTION.Validate
        or EN_MANUAL_INSPECTION_UI_ACTION.Apply or EN_MANUAL_INSPECTION_UI_ACTION.Rollback
        or EN_MANUAL_INSPECTION_UI_ACTION.CopyReference or EN_MANUAL_INSPECTION_UI_ACTION.UndoCopy
        or EN_MANUAL_INSPECTION_UI_ACTION.UpdateLimits or EN_MANUAL_INSPECTION_UI_ACTION.SetAllowManual or EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels;
    private async void Map_OperationRequested(object? sender, CManualInspectionOperationEventArgs e)
    {
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
        if (sender is not Button button || !Enum.TryParse<EN_MANUAL_INSPECTION_UI_ACTION>(button.Tag as string, out var action)) return;
        if (action is EN_MANUAL_INSPECTION_UI_ACTION.Move or EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure or EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect or EN_MANUAL_INSPECTION_UI_ACTION.Measure)
        {
            var selected = action != EN_MANUAL_INSPECTION_UI_ACTION.Measure && _selected.Count > 0;
            var keys = selected ? _selected.ToArray() : FocusPoint is null ? [] : new[] { FocusPoint.Key };
            await ConfirmOperation(action, keys, _snapshot.Version, selected ? Value(MeasurementModeCombo, "P2P") : "P2P"); return;
        }
        if (action is EN_MANUAL_INSPECTION_UI_ACTION.Refresh or EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe
            or EN_MANUAL_INSPECTION_UI_ACTION.Calculate or EN_MANUAL_INSPECTION_UI_ACTION.Validate
            or EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining or EN_MANUAL_INSPECTION_UI_ACTION.UndoCopy)
        {
            var frozen = Request(); var panel = new StackPanel();
            panel.Children.Add(Label(DescribeAction(action, _snapshot, frozen.Keys.Count)));
            if (!Confirm(ActionTitle(action), panel, "확인 · " + ActionTitle(action))) return;
            await Execute(action, frozen); return;
        }
        await Execute(action, Request(action == EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv ? _snapshot.Points.Select(p => p.Key) : null));
    }

    internal static string ActionTitle(EN_MANUAL_INSPECTION_UI_ACTION action) => action switch
    {
        EN_MANUAL_INSPECTION_UI_ACTION.Refresh or EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe => "결과 새로고침",
        EN_MANUAL_INSPECTION_UI_ACTION.Calculate => "선택점 보정 계산",
        EN_MANUAL_INSPECTION_UI_ACTION.Validate => "후보 검증",
        EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining => "남은 위치만 재측정",
        EN_MANUAL_INSPECTION_UI_ACTION.UndoCopy => "직전 복사 취소",
        _ => action.ToString()
    };
    internal static string DescribeAction(EN_MANUAL_INSPECTION_UI_ACTION action, CManualInspectionUiSnapshot snapshot, int count) => action switch
    {
        EN_MANUAL_INSPECTION_UI_ACTION.Refresh or EN_MANUAL_INSPECTION_UI_ACTION.LoadCurrentRecipe => "다른 메뉴에서 측정한 결과와 현재 활성 보정값을 다시 불러옵니다. 미적용 후보·복사 작업은 보관 후 새 자료로 열 수 있습니다. 미완료 측정 보호는 유지합니다. 장비는 이동하지 않고 보정 파일도 쓰지 않습니다.",
        EN_MANUAL_INSPECTION_UI_ACTION.Calculate => $"선택한 {count:N0}점의 직접 측정으로 보정 후보 B + 편차를 계산합니다. 기존 후보를 대신할 수 있습니다. 여기서는 활성 보정 파일에 저장하지 않습니다.",
        EN_MANUAL_INSPECTION_UI_ACTION.Validate => $"현재 후보 {snapshot.CandidateRows.Count:N0}점의 출처·중복 적용·보정 한계·Field를 검증합니다. 통과해도 ‘검증값 적용’ 전에는 활성 보정이 바뀌지 않습니다.",
        EN_MANUAL_INSPECTION_UI_ACTION.RetryRemaining => $"미완료 {snapshot.RemainingCount:N0}점만 이동 후 재측정합니다. 이미 완료된 점은 유지하며 중지됐던 자동 보정은 재개하지 않습니다.",
        EN_MANUAL_INSPECTION_UI_ACTION.UndoCopy => "직전 대표값 복사를 취소해 작업값·후보를 이전 상태로 돌립니다. 원측정과 활성 보정 파일은 변경하지 않습니다. 복사 이후 기준이 달라졌으면 취소가 거부됩니다.",
        _ => "선택한 동작을 확인한 뒤 진행합니다."
    };

    private async Task ConfirmOperation(EN_MANUAL_INSPECTION_UI_ACTION action, IEnumerable<string> keys, long expectedVersion, string mode)
    {
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
        var title = action switch { EN_MANUAL_INSPECTION_UI_ACTION.Move => "이동만", EN_MANUAL_INSPECTION_UI_ACTION.Measure => "현재 위치 측정", EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect => "이동 후 측정 & 보정", _ => "이동 후 측정" };
        var panel = new StackPanel();
        panel.Children.Add(Label($"{(_snapshot.IsSimulation ? "SIM · 설비 명령 없음" : "실제 설비 작업")}\nRecipe {_snapshot.RecipeId} · Run {_snapshot.RunId}\n선택 {requested.Length:N0}점 → 실행 {actual.Length:N0}점 · {mode}"));
        panel.Children.Add(Label(string.Join("\n", actual.Take(12)) + (actual.Length > 12 ? $"\n… 전체 {actual.Length:N0}점" : "")));
        panel.Children.Add(Label(action == EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect
            ? "선택점 전체가 새 직접 측정 OK로 완료된 후 보정 계산·검증·저장을 진행합니다. 중지·실패·잔여점이 있으면 자동 보정하지 않습니다."
            : action == EN_MANUAL_INSPECTION_UI_ACTION.Move ? "선택 위치로 순서대로 이동합니다. 측정과 보정은 하지 않습니다."
            : "새 측정 결과만 취득합니다. 보정은 이 화면의 계산·검증·적용에서 별도로 수행합니다."));
        if (!Confirm(title, panel, title + " 실행")) return;
        if (_snapshot.Version != expectedVersion) { ShowMessage("대상 확인 중 작업 기준이 바뀌었습니다. 다시 선택하세요."); return; }
        await Execute(action, request);
    }

    private string[] ExpandFlyingKeys(IEnumerable<string> selected)
    {
        var groups = selected.Where(_points.ContainsKey).Select(k => _points[k]).Select(p => (p.CellNo, p.HeadNo, p.Row, p.BeamNo)).ToHashSet();
        return _snapshot.Points.Where(p => !p.IsMasked && groups.Contains((p.CellNo, p.HeadNo, p.Row, p.BeamNo))).Select(p => p.Key).ToArray();
    }

    private async void CopyReference_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || _snapshot.HasIncomplete) { ShowMessage("미완료 측정의 잔여점을 먼저 완료하세요."); return; }
        if (_representativeKey is null || !_points.TryGetValue(_representativeKey, out var reference) || !reference.CanBeReference)
        { ShowMessage("직접 측정한 OK 또는 NG 점을 대표값으로 지정하세요. 복사본은 대표점으로 사용할 수 없습니다."); return; }
        var keys = _selected.Where(k => k != reference.Key && _points.TryGetValue(k, out var p) && !p.IsMasked).ToArray();
        if (keys.Length == 0) { ShowMessage("대표점 이외에 오프셋·상태를 복사할 대상을 선택하세요."); return; }
        var (x, y) = ReferenceValues(reference);
        if (!CManualInspectionUiPoint.Finite(x) || !CManualInspectionUiPoint.Finite(y)) { ShowMessage("대표 보정값이 유효하지 않습니다."); return; }
        var request = Request(keys); var body = new StackPanel();
        body.Children.Add(Label($"대표 {reference.Key} → 대상 {keys.Length:N0}점\n작업판정 {reference.WorkingJudge} · 같은 최종 F {CManualInspectionUiPoint.Pair(x, y)} µm\n작업 편차 {reference.WorkingErrorText} µm · 원측정은 별도로 보존합니다."));
        var preview = keys.Take(10).Select(k => { var p = _points[k]; return new { 대상 = k, 판정 = p.WorkingJudge + " → " + reference.WorkingJudge,
            최종F = CManualInspectionUiPoint.Pair(x, y), 점별증감 = CManualInspectionUiPoint.Pair(x - p.BaseXUm, y - p.BaseYUm) }; }).ToArray();
        var grid = new DataGrid { ItemsSource = preview, IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false, MaxHeight = 270, Margin = new Thickness(0, 7, 0, 9), Style = (Style)FindResource("PlainDataGrid") };
        body.Children.Add(grid);
        body.Children.Add(Label(reference.WorkingJudge == "NG" ? "대표가 NG이므로 대상도 NG입니다. 상태 복사 후에도 NG는 활성 보정 검증·적용을 통과하지 못합니다."
            : "화면의 OK는 대표를 따른 작업판정이며 새 직접 측정이 아닙니다. 기존 미적용 후보는 보존하고 대상만 MANUAL로 변경합니다."));
        body.Children.Add(Label("복사 사유")); var reason = new TextBox { Text = "사용자가 지정한 대표 상태와 최종 오프셋 동일 반영", Style = (Style)FindResource("ManualInput") }; body.Children.Add(reason);
        if (!Confirm("대표 상태와 오프셋을 함께 복사", body, $"{keys.Length:N0}점 상태 · 후보에 반영")) return;
        if (string.IsNullOrWhiteSpace(reason.Text)) { ShowMessage("복사 사유를 입력하세요."); return; }
        await Execute(EN_MANUAL_INSPECTION_UI_ACTION.CopyReference, request with { Reason = reason.Text.Trim() });
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var request = Request(); var panel = new StackPanel();
        panel.Children.Add(Label($"선택 후보 {_snapshot.CandidateRows.Count:N0}점을 새 활성 보정 버전에 저장합니다.\n원측정 기록과 작업판정은 변경하지 않습니다.\nRecipe {_snapshot.RecipeId} · 기준 {_snapshot.ActiveRevisionId}"));
        panel.Children.Add(new DataGrid { ItemsSource = _snapshot.CandidateRows.Take(12).Select(r => new { 대상 = r.Key, 현재B = r.BaseText, 증감Δ = r.DeltaText, 적용후F = r.FinalText }).ToArray(), AutoGenerateColumns = true,
            IsReadOnly = true, CanUserAddRows = false, MaxHeight = 260, Margin = new Thickness(0, 8, 0, 8), Style = (Style)FindResource("PlainDataGrid") });
        panel.Children.Add(Label("단위: DESIGN µm. 적용이 성공한 뒤 모든 Cell의 활성 보정 Level 지도가 갱신됩니다. 같은 기판에 이미 가공된 구멍이 움직이는 것은 아닙니다."));
        if (Confirm("보정 적용 · 저장", panel, "검증한 보정 적용 · 저장")) await Execute(EN_MANUAL_INSPECTION_UI_ACTION.Apply, request);
    }
    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        var request = Request(); var panel = new StackPanel(); panel.Children.Add(Label("직전 활성 보정값을 새 Revision으로 복원합니다. 원측정 기록은 삭제하지 않습니다."));
        if (Confirm("직전 보정 원복", panel, "원복")) await Execute(EN_MANUAL_INSPECTION_UI_ACTION.Rollback, request);
    }
    private async void AllowManual_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var allowed = AllowManualCheck.IsChecked == true; var frozen = Request() with { AllowManual = allowed };
        var panel = new StackPanel(); panel.Children.Add(Label(allowed
            ? "사유가 기록된 대표값 복사(MANUAL) 후보도 검증·적용 대상으로 허용합니다. 직접 실측으로 바꾸는 기능이 아니며 NG·한계·Field 검증은 유지됩니다. 지금은 파일에 저장하지 않습니다."
            : "대표값 복사 후보의 활성 보정 적용을 허용하지 않도록 변경합니다. 원측정·이미 적용된 파일은 바꾸지 않습니다."));
        if (!Confirm("대표 복사 후보 허용", panel, "확인 · 변경"))
        { _updating = true; AllowManualCheck.IsChecked = _snapshot.AllowManual; _updating = false; return; }
        await Execute(EN_MANUAL_INSPECTION_UI_ACTION.SetAllowManual, frozen);
    }
    private async void Limits_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var request = Request() with { DeltaLimitUm = Number(DeltaLimitInput), TotalLimitUm = Number(TotalLimitInput), ScannerFieldMm = Number(FieldLimitInput) };
            var panel = new StackPanel(); panel.Children.Add(Label($"증감 상한 {request.DeltaLimitUm} µm / 총량 상한 {request.TotalLimitUm} µm / Field {request.ScannerFieldMm} mm로 후보 검증 한계를 변경합니다. 현장 승인 한계를 확인하세요. 기존 검증은 무효화되어 다시 검증해야 합니다. 활성 보정 파일은 바꾸지 않습니다."));
            if (Confirm("보정 한계 변경", panel, "확인 · 한계 변경")) await Execute(EN_MANUAL_INSPECTION_UI_ACTION.UpdateLimits, request);
        }
        catch (Exception ex) { ShowMessage(ex.Message); }
    }

    private async void Levels_Click(object sender, RoutedEventArgs e)
    {
        var request = Request(); var body = new StackPanel();
        body.Children.Add(Label("이 Level 기준을 모든 Cell·Head의 활성 보정값과 측정 편차에 공통 사용합니다. 색상만 바꾸며 원측정·보정 파일·OK/NG 판정은 바뀌지 않습니다. 적용 보정 지도는 후보 F가 아닌 실제 활성 B만 사용합니다."));
        var metric = new ComboBox { Style = (Style)FindResource("ManualComboBox"), Margin = new Thickness(0, 5, 0, 9) };
        SetChoices(metric, [new("MAX_AXIS", "축별 최대값 max(|ΔX|,|ΔY|)"), new("RADIAL", "벡터 크기 √(ΔX²+ΔY²)")], _snapshot.LevelMetric); body.Children.Add(metric);
        var names = new List<TextBox>(); var bounds = new List<TextBox>();
        for (var i = 0; i < 4; i++)
        {
            body.Children.Add(Label($"L{i + 1} 이름")); var name = new TextBox { Text = i < _snapshot.LevelNames.Count ? _snapshot.LevelNames[i] : "L" + (i + 1), Style = (Style)FindResource("ManualInput"), Margin = new Thickness(0, 0, 0, 6) }; names.Add(name); body.Children.Add(name);
            if (i < 3) { body.Children.Add(Label($"L{i + 1} 상한 µm")); var bound = new TextBox { Text = (i < _snapshot.LevelBoundsUm.Count ? _snapshot.LevelBoundsUm[i] : 5 * (i + 1)).ToString(CultureInfo.InvariantCulture), Style = (Style)FindResource("ManualInput"), Margin = new Thickness(0, 0, 0, 8) }; bounds.Add(bound); body.Children.Add(bound); }
        }
        if (!Confirm("공통 Level 설정", body, "확인 · Level 설정 반영")) return;
        try
        {
            var values = bounds.Select(Number).ToArray();
            if (values[0] >= values[1] || values[1] >= values[2]) throw new InvalidOperationException("레벨 경계는 0보다 큰 숫자를 작은 순서로 입력하세요.");
            var levelNames = ValidatedLevelNames(names.Select(n => n.Text));
            await Execute(EN_MANUAL_INSPECTION_UI_ACTION.UpdateLevels, request with { LevelMetric = Value(metric), LevelBoundsUm = values, LevelNames = levelNames });
        }
        catch (Exception ex) { ShowMessage(ex.Message); }
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
        var recent = new MenuItem { Header = "최근 측정 결과 보기", IsEnabled = !Busy };
        recent.Click += async (_, _) => await OpenRecentResults(); menu.Items.Add(recent);
        foreach (var (label, action) in new[] { ("파일 직접 선택해서 보기", EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile), ("작업 세션 불러오기", EN_MANUAL_INSPECTION_UI_ACTION.ImportSession), ("작업 세션 저장", EN_MANUAL_INSPECTION_UI_ACTION.ExportSession), ("결과 CSV 내보내기", EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv) })
        {
            var item = new MenuItem { Header = label, IsEnabled = !Busy };
            item.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            item.Click += async (_, _) =>
            {
                if (action is EN_MANUAL_INSPECTION_UI_ACTION.ImportSession or EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile)
                {
                    var panel = new StackPanel(); panel.Children.Add(Label(action == EN_MANUAL_INSPECTION_UI_ACTION.ImportSession
                        ? "보관한 작업 선택·원측정·후보·이력을 불러옵니다. 실제 Recipe/Run/활성 보정과 일치하는지 검사하며 자동 이동이나 자동 보정을 시작하지 않습니다. 현재 작업을 먼저 보관하세요."
                        : "측정 결과 CSV를 선택해 지도와 표에서 확인합니다. 다른 기판·출처가 불완전한 결과는 조회 전용일 수 있으며 현재 직접 실측으로 승인하지 않습니다. 장비 이동이나 보정 저장은 하지 않습니다."));
                    if (!Confirm(label, panel, "확인 · 파일 선택")) return;
                }
                await Execute(action, Request());
            }; menu.Items.Add(item);
        }
        FilesButton.ContextMenu = menu; menu.IsOpen = true;
    }

    private ContextMenu NewMenu(Button owner)
        => CManualInspectionDrawing.CreateContextMenu(owner);
    private async Task OpenRecentResults()
    {
        if (Busy) return;
        var request = Request(); var panel = new StackPanel();
        panel.Children.Add(Label("다른 메뉴에서 저장한 최근 측정 결과를 선택합니다. 같은 Recipe·기판인지 확인한 뒤 조회합니다. 결과 열기는 장비 이동·보정 적용을 실행하지 않습니다."));
        var combo = new ComboBox { ItemsSource = _snapshot.RecentResults, DisplayMemberPath = nameof(CManualInspectionUiResultFile.DisplayText), Style = (Style)FindResource("ManualComboBox"), SelectedIndex = _snapshot.RecentResults.Count > 0 ? 0 : -1, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(combo);
        var selected = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11 }; selected.SetResourceReference(TextBlock.ForegroundProperty,"TextMutedBrush"); panel.Children.Add(selected);
        void Selection() { selected.Text = combo.SelectedItem is CManualInspectionUiResultFile file ? $"출처 {file.Origin} · 기판 {file.GlassId}\n{file.Path}" : "현재 목록이 없습니다. 파일 직접 선택으로 확인할 수 있습니다."; }
        combo.SelectionChanged += (_, _) => Selection(); Selection();
        var chooseFile = new CheckBox { Content = "목록 대신 파일 직접 선택", IsChecked = _snapshot.RecentResults.Count == 0, Style = (Style)FindResource("ManualCheck"), Margin = new Thickness(0, 9, 0, 0) }; panel.Children.Add(chooseFile);
        if (!Confirm("최근 측정 결과", panel, "확인 · 결과 보기")) return;
        if (chooseFile.IsChecked != true && combo.SelectedItem is not CManualInspectionUiResultFile) { ShowMessage("조회할 결과를 선택하세요."); return; }
        await Execute(EN_MANUAL_INSPECTION_UI_ACTION.LoadResultFile, request with { ResultPath = chooseFile.IsChecked == true ? null : ((CManualInspectionUiResultFile)combo.SelectedItem).Path });
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
        if (_snapshot.IsReadOnly) message = "조회 전용 자료입니다. 현재 Recipe/Run 연결을 확인한 자료로 측정·보정을 진행하세요. " + message;
        MessageText.Text = message; MessageText.ToolTip = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && PositionMap.TryCancelGesture()) { e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && key == Key.F)
        { SearchSection.IsExpanded = true; SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
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
