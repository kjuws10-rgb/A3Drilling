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
    private sealed class GridRow(CManualInspectionUiPoint point, bool selected, Action<GridRow> changed) : INotifyPropertyChanged
    {
        private bool _selected = selected;
        public CManualInspectionUiPoint Point { get; } = point;
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
    private string? _focusKey;
    private string? _representativeKey;
    private string _runId = "";
    private int _cell;
    private int _head;
    private string _judge = "ALL";
    private string _source = "ALL";
    private string _query = "";
    private string _sort = "shot";
    private string _color = "judge";
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

    public CManualInspectionView()
    {
        InitializeComponent();
        _updating = true;
        SetChoices(JudgeFilter, [new("ALL", "전체 결과"), new("OK", "OK"), new("NG", "NG"), new("ERROR", "수신 오류"), new("UNMEASURED", "미측정")], "ALL");
        SetChoices(SourceFilter, [new("ALL", "전체 출처"), new("MEASURED", "MEASURED"), new("IMPORTED", "IMPORTED · 조회 자료"), new("INTERPOLATED", "INTERPOLATED"), new("MANUAL", "MANUAL"), new("KEPT", "KEPT")], "ALL");
        SetChoices(SortFilter, [new("shot", "Shot 순"), new("cell", "Cell 순"), new("error", "편차 크기 순")], "shot");
        SetChoices(ColorFilter, [new("judge", "판정 색상"), new("level", "오차 레벨"), new("head", "Head 색상"), new("source", "후보 출처")], "judge");
        SetChoices(ScatterDataCombo, [new("WORKING", "작업 결과 · 대표값 반영"), new("ORIGINAL", "원측정 기록")], "WORKING");
        SetChoices(MeasurementModeCombo, [new("P2P", "P2P · 선택점"), new("FLYING", "Flying · 해당 행 전체")], "P2P");
        SetChoices(SimulationDemoCombo, [new("normal", "혼합 결과 · NG/미측정"), new("good", "정상 측정 결과"), new("stress", "10,000점")], "normal");
        SetChoices(SimulationScenarioCombo, [new("recover", "검출 오류 회복 예제"), new("same", "같은 편차 유지")], "recover");
        SetChoices(SimulationSpeedCombo, [new("1", "1×"), new("4", "4×"), new("16", "16×")], "16");
        SetChoices(SimulationFaultCombo, [new("NONE", "정상"), new("VISION_TIMEOUT", "Vision 응답 누락"), new("PLC_STALE", "PLC 응답 고착"), new("UID_MISMATCH", "결과 식별 불일치")], "NONE");
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
            _points = _snapshot.Points.GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            _selected.RemoveWhere(k => !_points.ContainsKey(k));
            var workspaceIdentity = _snapshot.RecipeId + "|" + _snapshot.RunId;
            if (_runId != workspaceIdentity)
            {
                _runId = workspaceIdentity; _selected.Clear(); _focusKey = null; _representativeKey = null;
                _cell = _snapshot.Points.FirstOrDefault()?.CellNo ?? 0; _head = 0; _judge = _source = "ALL";
                _query = ""; _page = 0; SearchBox.Text = "";
                MessageText.Text = "";
                _color = "judge"; _scatterScope = "CELL"; _scatterHead = 0; _scatterOriginal = false;
                PositionMap.IsPanTool = false; PositionMap.Cursor = Cursors.Cross;
                PanToolButton.Content = "선택 도구"; PanToolButton.Style = (Style)FindResource("ManualButton");
                _scatterCells.Clear(); if (_cell != 0) _scatterCells.Add(_cell);
            }
            if (_representativeKey is not null && !_points.ContainsKey(_representativeKey)) _representativeKey = null;
            var cells = _snapshot.Points.Select(p => p.CellNo).Distinct().OrderBy(x => x).ToArray();
            var heads = _snapshot.Points.Select(p => p.HeadNo).Distinct().OrderBy(x => x).ToArray();
            if (_cell != 0 && !cells.Contains(_cell)) _cell = cells.FirstOrDefault();
            if (_head != 0 && !heads.Contains(_head)) _head = 0;
            SetChoices(CellFilter, new[] { new Choice("0", "전체 Cell") }.Concat(cells.Select(n => new Choice(n.ToString(), $"Cell {n}"))), _cell.ToString());
            SetChoices(HeadFilter, new[] { new Choice("0", "전체 Head") }.Concat(heads.Select(n => new Choice(n.ToString(), $"Head {n}"))), _head.ToString());
            SelectValue(JudgeFilter, _judge); SelectValue(SourceFilter, _source); SelectValue(SortFilter, _sort);
            SelectValue(ColorFilter, _color); SelectValue(ScatterDataCombo, _scatterOriginal ? "ORIGINAL" : "WORKING");
            _filtered = FilteredPoints();
            ContextText.Text = $"{(_snapshot.IsSimulation ? "SIM · 설비 명령 없음" : "실제 설비 · 현재 Recipe")}  |  Recipe {_snapshot.RecipeId} · Run {_snapshot.RunId} · 활성 보정 {_snapshot.ActiveRevisionId}";
            StatusText.Text = _snapshot.StatusText;
            StagePositionText.Text = $"마지막 도착 확인  X {CManualInspectionUiPoint.Number(_snapshot.StageXmm)} / Y {CManualInspectionUiPoint.Number(_snapshot.StageYmm)} mm";
            PersistenceText.Text = _snapshot.PersistenceText + (string.IsNullOrWhiteSpace(_snapshot.SourcePath) ? "" : " · " + _snapshot.SourcePath);
            if (!string.IsNullOrEmpty(_snapshot.Message)) MessageText.Text = _snapshot.Message;
            if (_snapshot.IsReadOnly) MessageText.Text = "조회 전용 자료입니다. 현재 Recipe/Run 연결을 확인한 자료로 측정·보정을 진행하세요. " + _snapshot.Message;
            var scoped = _snapshot.Points.Where(p => (_cell == 0 || p.CellNo == _cell) && (_head == 0 || p.HeadNo == _head)).ToArray();
            PositionMap.Configure(_snapshot, scoped, _filtered, _selected, FocusPoint?.Key, _representativeKey, _color, $"{_cell}|{_head}");
            SelectionCountText.Text = $"선택 {_selected.Count:N0}점 · 지도 {_filtered.Count:N0} / 전체 {_snapshot.Points.Count:N0}점";
            FilterNoticeText.Text = _head != 0 || _judge != "ALL" || _source != "ALL" || _query.Length > 0
                ? "세부 필터가 지도·표에 적용되어 있습니다. 산포도는 별도 범위를 사용합니다." : "";
            MapLegendText.Text = MapLegend();
            RefreshMapLegend();
            LevelsButton.Visibility = _color == "level" ? Visibility.Visible : Visibility.Collapsed;
            RefreshRows(); RefreshReference(); RefreshCorrection(); RefreshDetails(); RefreshScatter(); RefreshButtons();
        }
        finally { _updating = false; }
    }

    private static void SelectValue(ComboBox combo, string value) => combo.SelectedItem = combo.Items.OfType<Choice>().FirstOrDefault(x => x.Value == value);
    private IReadOnlyList<CManualInspectionUiPoint> FilteredPoints()
    {
        var points = _snapshot.Points.Where(p => (_cell == 0 || p.CellNo == _cell) && (_head == 0 || p.HeadNo == _head)
            && (_judge == "ALL" || p.WorkingJudge == _judge) && (_source == "ALL" || (p.IsProxy ? "MANUAL" : p.Source) == _source)
            && (_query.Length == 0 || (p.Key + " " + p.Identity).Contains(_query, StringComparison.OrdinalIgnoreCase)));
        return (_sort switch
        {
            "cell" => points.OrderBy(p => p.CellNo).ThenBy(p => p.HeadNo).ThenBy(p => p.ShotNo).ThenBy(p => p.BeamNo),
            "error" => points.OrderByDescending(p => CManualInspectionUiPoint.Finite(p.WorkingXUm) && CManualInspectionUiPoint.Finite(p.WorkingYUm)
                ? Math.Max(Math.Abs(p.WorkingXUm!.Value), Math.Abs(p.WorkingYUm!.Value)) : double.NegativeInfinity).ThenBy(p => p.Key, StringComparer.Ordinal),
            _ => points.OrderBy(p => p.ShotNo).ThenBy(p => p.CellNo).ThenBy(p => p.HeadNo).ThenBy(p => p.BeamNo)
        }).ToArray();
    }
    private void RefreshRows()
    {
        var pages = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)PageSize)); _page = Math.Clamp(_page, 0, pages - 1);
        ResultGrid.ItemsSource = _filtered.Skip(_page * PageSize).Take(PageSize).Select(p => new GridRow(p, _selected.Contains(p.Key), RowCheckChanged)).ToArray();
        PageText.Text = $"{_page + 1} / {pages} 페이지";
        PreviousPageButton.IsEnabled = _page > 0; NextPageButton.IsEnabled = _page + 1 < pages;
        ResultStatsText.Text = $"OK {_filtered.Count(p => p.WorkingJudge == "OK"):N0}   ·   NG {_filtered.Count(p => p.WorkingJudge == "NG"):N0}   ·   오류 {_filtered.Count(p => p.WorkingJudge == "ERROR"):N0}   ·   미측정 {_filtered.Count(p => p.WorkingJudge == "UNMEASURED"):N0}";
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
        CandidateStatusText.Text = _snapshot.CandidateStatus;
        CandidateContextText.Text = $"선택 {_selected.Count:N0}점 · 현재 보정 {_snapshot.ActiveRevisionId}";
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
        PointTitleText.Text = p is null ? "선택 없음" : $"Cell {p.CellNo} · Head {p.HeadNo}";
        PointIdentityText.Text = p is null ? "지도에서 점을 선택하세요." : $"Shot {p.ShotNo} · Beam {p.BeamNo}";
        PointJudgesText.Text = p is null ? "" : $"작업 판정 {p.WorkingJudge} · 원측정 {p.OriginalJudge}";
        PointJudgesText.SetResourceReference(TextBlock.ForegroundProperty, p?.WorkingJudge == "NG" ? "ReviewMaskTextBrush" : p?.WorkingJudge == "ERROR" ? "ButtonWarningBrush" : "TextPrimaryBrush");
        PointErrorText.Text = p is null ? "" : $"작업 ΔX / ΔY\n{p.WorkingErrorText} µm\n원측정 {p.OriginalErrorText} µm";
        PointDetailsText.Text = p is null ? "" : $"현재 보정 B  {p.BaseOffsetText} µm\n후보 보정 F  {p.CandidateOffsetText} µm\n설계 X / Y  {p.DesignXmm:0.000} / {p.DesignYmm:0.000} mm\n대표 여부  {(p.Key == _representativeKey ? "★ 대표값" : "대상 위치")}"
            + (p.IsProxy ? $"\n◇ 대표값 복사 · MANUAL\n기준 {p.ReferenceKey}\n새 실측이 아닌 대표값을 사용 중입니다." : "") + "\n" + p.Key;
        JobProgressText.Text = Busy ? $"진행 {_snapshot.CompletedCount:N0} / {_snapshot.TotalCount:N0}점" : _snapshot.HasIncomplete ? $"잔여 {_snapshot.RemainingCount:N0}점 · 자동 보정은 재개하지 않습니다." : "대기";
        JobProgressBar.Value = _snapshot.TotalCount <= 0 ? 0 : Math.Clamp(_snapshot.CompletedCount * 100.0 / _snapshot.TotalCount, 0, 100);
        SimulationOptions.Visibility = _snapshot.IsSimulation ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshButtons()
    {
        var idle = !Busy; var operate = idle && !_snapshot.IsReadOnly && _snapshot.Points.Count > 0;
        var hasSelection = _selected.Any(k => _points.TryGetValue(k, out var p) && !p.IsMasked);
        var hasPoint = FocusPoint is { IsMasked: false };
        foreach (var b in new[] { LoadRecipeButton, LoadResultButton, FilesButton, SimulationDemoButton }) b.IsEnabled = idle;
        foreach (var b in new[] { MoveButton, MoveMeasureButton, MoveMeasureCorrectButton, MeasureButton }) b.IsEnabled = operate && hasPoint;
        RemeasureSelectedButton.IsEnabled = SelectedWorkButton.IsEnabled = operate && hasSelection;
        CalculateButton.IsEnabled = operate && hasSelection && !_snapshot.HasIncomplete;
        ValidateButton.IsEnabled = operate && !_snapshot.HasIncomplete && _snapshot.CandidateRows.Count > 0 && !string.Equals(_snapshot.CandidateStatus, "APPLIED", StringComparison.OrdinalIgnoreCase);
        ApplyButton.IsEnabled = operate && !_snapshot.HasIncomplete && _snapshot.CanApply && _snapshot.ValidationPassed;
        RollbackButton.IsEnabled = operate && _snapshot.CanRollback;
        SetReferenceButton.IsEnabled = operate && FocusPoint?.CanBeReference == true;
        ClearReferenceButton.IsEnabled = idle && _representativeKey is not null;
        CopyReferenceButton.IsEnabled = operate && !_snapshot.HasIncomplete && _representativeKey is not null && _selected.Any(k => k != _representativeKey && _points.TryGetValue(k, out var p) && !p.IsMasked);
        UndoCopyButton.IsEnabled = operate && !_snapshot.HasIncomplete && _snapshot.CanUndoCopy;
        UndoCopyButton.Visibility = _snapshot.CanUndoCopy ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = SideStopButton.IsEnabled = Busy;
        RetryButton.IsEnabled = operate && _snapshot.HasIncomplete && _snapshot.RemainingCount > 0;
        AllowManualCheck.IsEnabled = SaveLimitsButton.IsEnabled = LevelsButton.IsEnabled = idle && !_snapshot.IsReadOnly;
        MeasurementModeCombo.IsEnabled = SimulationDemoCombo.IsEnabled = SimulationScenarioCombo.IsEnabled = SimulationSpeedCombo.IsEnabled = SimulationFaultCombo.IsEnabled = idle;
        PointHistoryButton.IsEnabled = idle && FocusPoint is not null;
    }

    private string MapLegend() => (_color switch
    {
        "head" => "Head별 색상 · 작업 판정은 클릭 정보·표에서 확인",
        "source" => "실측 / 불러온 측정(조회) / 보간 / 수동 후보 / 기존 유지 · 작업 판정은 표에서 확인",
        "level" => string.Join(" · ", _snapshot.LevelNames.Select((n, i) => $"L{i + 1} {n}")) + " · 레벨과 OK/NG는 별도",
        _ => "OK · NG · 수신 오류 ! · 미측정 ·"
    }) + "   |   Masking × · 파란 테두리: 선택 · ★: 대표값 · ◇: 대표값 복사";

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
        if (_color == "head")
            foreach (var h in _snapshot.Points.Select(p => p.HeadNo).Distinct().OrderBy(n => n))
                AddLegend(MapLegendItems, "Head " + h, CManualInspectionDrawing.Color(CManualInspectionDrawing.HeadColors[Math.Max(0, h - 1) % 8]));
        else if (_color == "level")
        {
            var colors = new[] { "#2E9076", "#B88B2A", "#CA754A", "#BE4F68" };
            for (var i = 0; i < 4; i++)
            {
                var bound = i < 3 && _snapshot.LevelBoundsUm.Count > i ? " ≤ " + _snapshot.LevelBoundsUm[i].ToString("G", CultureInfo.InvariantCulture)
                    : _snapshot.LevelBoundsUm.Count > 2 ? " > " + _snapshot.LevelBoundsUm[2].ToString("G", CultureInfo.InvariantCulture) : "";
                AddLegend(MapLegendItems, $"L{i + 1} {(_snapshot.LevelNames.Count > i ? _snapshot.LevelNames[i] : "")} {bound} µm", CManualInspectionDrawing.Color(colors[i]));
            }
        }
        else if (_color == "source")
            foreach (var (name, color) in new[] { ("실측", "#3B9870"), ("불러온 측정 · 조회", "#7899A8"), ("보간", "#77A6CD"), ("수동 후보", "#A482BE"), ("기존 유지", "#DBE4EA") }) AddLegend(MapLegendItems, name, CManualInspectionDrawing.Color(color));
        else
            foreach (var (name, color) in new[] { ("OK", "#3B9870"), ("NG", "#D16776"), ("수신 오류 !", "#D39A45"), ("미측정 ·", "#DBE4EA") }) AddLegend(MapLegendItems, name, CManualInspectionDrawing.Color(color));
        AddLegend(MapLegendItems, "Masking ×", CManualInspectionDrawing.Resource(this, "ReviewMaskBackgroundBrush", Brushes.LightGray));
    }

    private void RefreshScatter()
    {
        var currentCell = _cell != 0 ? _cell : FocusPoint?.CellNo ?? _snapshot.Points.FirstOrDefault()?.CellNo ?? 0;
        var range = _snapshot.Points.Where(p => _scatterScope == "CELL" ? p.CellNo == currentCell : _scatterScope == "HEAD" ? _scatterHead == 0 || p.HeadNo == _scatterHead : _scatterCells.Contains(p.CellNo)).ToArray();
        var display = _scatterOriginal ? range.Select(p => p with { WorkingJudge = p.OriginalJudge, WorkingXUm = p.OriginalXUm, WorkingYUm = p.OriginalYUm, IsProxy = false }).ToArray() : range;
        ScatterPlot.Configure(display, _selected, _scatterScope == "HEAD", _snapshot.ToleranceXUm, _snapshot.ToleranceYUm,
            _scatterScope == "CELLS" && _scatterCells.Count == 0 ? "산포도를 볼 Cell을 위에서 선택하세요." : "이 범위에 유효한 측정값이 없습니다.");
        var m = ScatterPlot.Metrics;
        ScatterMetricsText.Text = $"전체 위치  {m.Total:N0}점\n{(_scatterOriginal ? "유효 원측정값" : "표시한 작업값")}  {m.Count:N0}점"
            + (_scatterOriginal ? "" : $"\n대표값 복사  {m.ProxyCount:N0}점")
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
        ScatterLegendText.Text = string.Join(" · ", groups.Select(g => g.Group)) + "   ＋ 평균 · 중심 (0,0): 목표   " + (_scatterOriginal ? "원측정 기록만 표시" : "● 실측 · ◇ 대표값 복사");
        foreach (var b in new[] { ScatterCellButton, ScatterHeadButton, ScatterCellsButton }) b.Style = (Style)FindResource((string)b.Tag == _scatterScope ? "ManualPrimaryButton" : "ManualButton");
        ScatterScopeControls.Children.Clear();
        if (_scatterScope == "CELL") ScatterScopeControls.Children.Add(Label($"Cell {currentCell} 전체 · 지도 선택·NG·검색 필터와 관계없이 셀 전체를 봅니다."));
        else if (_scatterScope == "HEAD")
        {
            var panel = new WrapPanel(); var combo = new ComboBox { Width = 190, Style = (Style)FindResource("ManualComboBox"), Margin = new Thickness(0, 0, 9, 4) };
            SetChoices(combo, new[] { new Choice("0", "모든 Head 겹쳐 보기") }.Concat(_snapshot.Points.Select(p => p.HeadNo).Distinct().OrderBy(x => x).Select(x => new Choice(x.ToString(), $"Head {x}"))), _scatterHead.ToString());
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
        if (e.FocusKey is not null && _points.TryGetValue(e.FocusKey, out var p) && _cell != 0 && _cell != p.CellNo)
        { _cell = p.CellNo; _head = 0; _judge = _source = "ALL"; _query = ""; _page = 0; SearchBox.Text = ""; }
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
    private void Color_Changed(object sender, SelectionChangedEventArgs e) { if (_updating) return; _color = Value(ColorFilter, "judge"); Refresh(); }
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
    private void PanTool_Click(object sender, RoutedEventArgs e) { PositionMap.IsPanTool = !PositionMap.IsPanTool; PanToolButton.Content = PositionMap.IsPanTool ? "이동 도구" : "선택 도구"; PanToolButton.Style = (Style)FindResource(PositionMap.IsPanTool ? "ManualPrimaryButton" : "ManualButton"); PositionMap.Cursor = PositionMap.IsPanTool ? Cursors.ScrollAll : Cursors.Cross; }
    private void SelectedWork_Click(object sender, RoutedEventArgs e) => PositionMap.ShowOperationMenu();
    private void CorrectionFocus_Click(object sender, RoutedEventArgs e) => CorrectionSection.BringIntoView();
    private void SetReference_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || FocusPoint?.CanBeReference != true) { ShowMessage("대표값 복사본이 아닌 유효한 실측 OK 또는 NG 점을 선택하세요."); return; }
        _representativeKey = FocusPoint.Key; ShowMessage("대표값을 지정했습니다. Ctrl 클릭 또는 드래그로 다른 대상 위치를 선택하세요."); Refresh();
    }
    private void ClearReference_Click(object sender, RoutedEventArgs e) { _representativeKey = null; Refresh(); }

    private CManualInspectionUiRequest Request(IEnumerable<string>? keys = null, long? expectedVersion = null) => new()
    {
        ExpectedVersion = expectedVersion ?? _snapshot.Version,
        Keys = (keys ?? _snapshot.Points.Where(p => _selected.Contains(p.Key)).Select(p => p.Key)).Distinct(StringComparer.Ordinal).ToArray(),
        FocusKey = FocusPoint?.Key, ReferenceKey = _representativeKey,
        MeasurementMode = Value(MeasurementModeCombo, "P2P"),
        Scenario = _snapshot.IsSimulation ? Value(SimulationScenarioCombo, "recover") : "same",
        Fault = _snapshot.IsSimulation ? Value(SimulationFaultCombo, "NONE") : "NONE",
        Speed = _snapshot.IsSimulation && double.TryParse(Value(SimulationSpeedCombo, "1"), CultureInfo.InvariantCulture, out var speed) ? speed : 1,
        AllowManual = AllowManualCheck.IsChecked == true
    };
    private async Task Execute(EN_MANUAL_INSPECTION_UI_ACTION action, CManualInspectionUiRequest? request = null)
    {
        if (_controller is null) { ShowMessage("Manual Inspection 제어기가 연결되지 않았습니다."); return; }
        if (action != EN_MANUAL_INSPECTION_UI_ACTION.Stop && Busy) { ShowMessage("동작 중입니다. 완료 또는 중지 후 조작하세요."); return; }
        _requests++; RefreshButtons();
        try { await _controller.ExecuteAsync(action, request ?? Request()); }
        catch (Exception ex) { ShowMessage(ex.Message); }
        finally { _requests--; Refresh(); }
    }
    private async void Map_OperationRequested(object? sender, CManualInspectionOperationEventArgs e) => await ConfirmOperation(e.Action, e.Keys, e.ExpectedVersion, "P2P");
    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse<EN_MANUAL_INSPECTION_UI_ACTION>(button.Tag as string, out var action)) return;
        if (action is EN_MANUAL_INSPECTION_UI_ACTION.Move or EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure or EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect or EN_MANUAL_INSPECTION_UI_ACTION.Measure)
        {
            var selected = ReferenceEquals(button, RemeasureSelectedButton);
            var keys = selected ? _selected.ToArray() : FocusPoint is null ? [] : new[] { FocusPoint.Key };
            await ConfirmOperation(action, keys, _snapshot.Version, selected ? Value(MeasurementModeCombo, "P2P") : "P2P"); return;
        }
        if (action == EN_MANUAL_INSPECTION_UI_ACTION.NewSimulation)
        { await Execute(action, Request([]) with { Scenario = Value(SimulationDemoCombo, "normal") }); return; }
        await Execute(action, Request(action == EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv ? _snapshot.Points.Select(p => p.Key) : null));
    }

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
        if (Confirm("보정 적용 · 저장", panel, "검증한 보정 적용 · 저장")) await Execute(EN_MANUAL_INSPECTION_UI_ACTION.Apply, request);
    }
    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        var request = Request(); var panel = new StackPanel(); panel.Children.Add(Label("직전 활성 보정값을 새 Revision으로 복원합니다. 원측정 기록은 삭제하지 않습니다."));
        if (Confirm("직전 보정 원복", panel, "원복")) await Execute(EN_MANUAL_INSPECTION_UI_ACTION.Rollback, request);
    }
    private async void AllowManual_Changed(object sender, RoutedEventArgs e)
    {
        if (!_updating) await Execute(EN_MANUAL_INSPECTION_UI_ACTION.SetAllowManual, Request() with { AllowManual = AllowManualCheck.IsChecked == true });
    }
    private async void Limits_Click(object sender, RoutedEventArgs e)
    {
        try { await Execute(EN_MANUAL_INSPECTION_UI_ACTION.UpdateLimits, Request() with { DeltaLimitUm = Number(DeltaLimitInput), TotalLimitUm = Number(TotalLimitInput), ScannerFieldMm = Number(FieldLimitInput) }); }
        catch (Exception ex) { ShowMessage(ex.Message); }
    }

    private async void Levels_Click(object sender, RoutedEventArgs e)
    {
        var request = Request(); var body = new StackPanel();
        body.Children.Add(Label("오차 레벨은 표시용입니다. 경계·이름을 바꿔도 기존 측정값과 OK/NG 판정은 바뀌지 않습니다."));
        var metric = new ComboBox { Style = (Style)FindResource("ManualComboBox"), Margin = new Thickness(0, 5, 0, 9) };
        SetChoices(metric, [new("MAX_AXIS", "축별 최대값 max(|ΔX|,|ΔY|)"), new("RADIAL", "벡터 크기 √(ΔX²+ΔY²)")], _snapshot.LevelMetric); body.Children.Add(metric);
        var names = new List<TextBox>(); var bounds = new List<TextBox>();
        for (var i = 0; i < 4; i++)
        {
            body.Children.Add(Label($"L{i + 1} 이름")); var name = new TextBox { Text = i < _snapshot.LevelNames.Count ? _snapshot.LevelNames[i] : "L" + (i + 1), Style = (Style)FindResource("ManualInput"), Margin = new Thickness(0, 0, 0, 6) }; names.Add(name); body.Children.Add(name);
            if (i < 3) { body.Children.Add(Label($"L{i + 1} 상한 µm")); var bound = new TextBox { Text = (i < _snapshot.LevelBoundsUm.Count ? _snapshot.LevelBoundsUm[i] : 5 * (i + 1)).ToString(CultureInfo.InvariantCulture), Style = (Style)FindResource("ManualInput"), Margin = new Thickness(0, 0, 0, 8) }; bounds.Add(bound); body.Children.Add(bound); }
        }
        if (!Confirm("오차 레벨 설정", body, "레벨 설정 반영")) return;
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
    private async void PointHistory_Click(object sender, RoutedEventArgs e)
    {
        var point = FocusPoint; if (point is null) return;
        await Execute(EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint, Request([point.Key]) with { FocusKey = point.Key });
    }
    private void Files_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = FilesButton };
        menu.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush"); menu.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        foreach (var (label, action) in new[] { ("세션 JSON 불러오기", EN_MANUAL_INSPECTION_UI_ACTION.ImportSession), ("현재 세션 JSON 저장", EN_MANUAL_INSPECTION_UI_ACTION.ExportSession), ("현재 결과 CSV", EN_MANUAL_INSPECTION_UI_ACTION.ExportCsv), ("SIM 예제 열기 · 설비와 분리", EN_MANUAL_INSPECTION_UI_ACTION.NewSimulation) })
        {
            var item = new MenuItem { Header = label, IsEnabled = !Busy && (action != EN_MANUAL_INSPECTION_UI_ACTION.NewSimulation || _snapshot.IsSimulation) };
            if (action == EN_MANUAL_INSPECTION_UI_ACTION.NewSimulation) item.ToolTip = "명시적인 설비 SIM 모드에서만 예제를 사용할 수 있습니다.";
            item.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
            item.Click += async (_, _) =>
            {
                if (action == EN_MANUAL_INSPECTION_UI_ACTION.NewSimulation)
                {
                    var panel = new StackPanel(); panel.Children.Add(Label("SIM 예제로 전환합니다. 실제 설비 명령은 전송하지 않습니다. 실제 작업으로 돌아오려면 현재 Recipe를 다시 불러오세요."));
                    if (!Confirm("SIM 작업공간", panel, "SIM 예제 열기")) return;
                    await Execute(action, Request([]) with { Scenario = "normal" });
                }
                else await Execute(action, Request());
            }; menu.Items.Add(item);
        }
        FilesButton.ContextMenu = menu; menu.IsOpen = true;
    }

    private bool Confirm(string title, UIElement content, string confirmText, bool informationOnly = false)
    {
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
    private void ShowMessage(string message) { MessageText.Text = message; }
    private async void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && PositionMap.TryCancelGesture()) { e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Left or Key.Right or Key.Up or Key.Down
            && Ancestor<TextBox>(e.OriginalSource as DependencyObject) is null && Ancestor<ComboBox>(e.OriginalSource as DependencyObject) is null)
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 100 : 40;
            PositionMap.PanBy(key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), _ => new Vector(0, step) });
            e.Handled = true; return;
        }
        if (e.Key == Key.Escape && Busy) { e.Handled = true; await Execute(EN_MANUAL_INSPECTION_UI_ACTION.Stop); }
        else if ((e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            && e.OriginalSource is not TextBox && e.OriginalSource is not ComboBox)
        { PositionMap.ShowOperationMenu(); e.Handled = true; }
    }
}
