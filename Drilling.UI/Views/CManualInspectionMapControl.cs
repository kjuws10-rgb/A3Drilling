namespace Drilling.UI.Views;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

public sealed class CManualInspectionSelectionEventArgs(
    IReadOnlyList<string> keys, string? focusKey, bool additive = false, bool toggle = false, bool clearFocus = false) : EventArgs
{
    public IReadOnlyList<string> Keys { get; } = keys;
    public string? FocusKey { get; } = focusKey;
    public bool Additive { get; } = additive;
    public bool Toggle { get; } = toggle;
    public bool ClearFocus { get; } = clearFocus;
}

public sealed class CManualInspectionOperationEventArgs(
    EN_MANUAL_INSPECTION_UI_ACTION action, IReadOnlyList<string> keys, long expectedVersion) : EventArgs
{
    public EN_MANUAL_INSPECTION_UI_ACTION Action { get; } = action;
    public IReadOnlyList<string> Keys { get; } = keys;
    public long ExpectedVersion { get; } = expectedVersion;
}

/// <summary>
/// One retained WPF drawing surface for the Cell/Head/Shot/Beam matrix. Filtering only
/// hides slots: it never repacks a visible NG point into another physical/logical slot.
/// Selection/zoom/pan are visual operations and have no motion-controller reference.
/// </summary>
public sealed class CManualInspectionMapControl : Control
{
    private sealed record Slot(CManualInspectionUiPoint Point, Rect Bounds);
    private IReadOnlyList<CManualInspectionUiPoint> _all = [];
    private IReadOnlyList<CManualInspectionUiPoint> _visible = [];
    private HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly List<Slot> _slots = [];
    private readonly List<(CManualInspectionUiPoint Point, Rect Bounds)> _hits = [];
    private readonly List<(Rect Bounds, Rect Grid, string Title, string Extents)> _frames = [];
    private readonly Dictionary<(int X, int Y), List<int>> _hitBuckets = [];
    private Dictionary<string, CManualInspectionUiPoint> _pointByKey = new(StringComparer.Ordinal);
    private HashSet<string> _visibleKeys = new(StringComparer.Ordinal);
    private CManualInspectionUiSnapshot _snapshot = new();
    private string? _representative;
    private string? _focusKey;
    private string _colorMode = "applied";
    private string _layoutSignature = "";
    private bool _layoutDirty = true;
    private bool _gesture;
    private bool _panning;
    private bool _additive;
    private bool _moved;
    private sealed record ClickSelection(string[] Keys, string? FocusKey, long Version, string Scope);
    private ClickSelection? _selectionBeforeClick;
    private bool _canRestoreClickSelection;
    private Point _start;
    private Point _last;
    private Vector _startPan;
    private Vector _pan;
    private double _zoom = 1;
    private string? _hoverKey;
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

    public event EventHandler<CManualInspectionSelectionEventArgs>? SelectionRequested;
    public event EventHandler<CManualInspectionOperationEventArgs>? OperationRequested;
    public event EventHandler? ViewChanged;
    public event Action<string>? MessageRequested;
    public double Zoom => _zoom;
    public bool IsPanTool { get; set; }

    public CManualInspectionMapControl()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        MinHeight = 100;
        Cursor = Cursors.Cross;
        SetResourceReference(BackgroundProperty, "GridBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(BorderBrushProperty, "GridLineBrush");
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 180);
        ToolTipService.SetShowDuration(this, 15000);
        AutomationProperties.SetName(this, "리뷰 측정 위치 논리 행렬");
        AutomationProperties.SetHelpText(this, "행과 열의 논리 배치입니다. Ctrl 클릭으로 복수 선택, 드래그로 영역 선택, Ctrl 휠로 확대, Alt 방향키로 화면 이동, 더블클릭으로 선택을 유지한 화면 맞춤, 우클릭으로 동작을 선택합니다.");
        SizeChanged += (_, _) => { _layoutDirty = true; InvalidateVisual(); };
        Unloaded += (_, _) => { CancelGesture(true); _tip.IsOpen = false; if (ContextMenu is not null) ContextMenu.IsOpen = false; };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CManualInspectionSurfacePeer(this);

    public void Configure(CManualInspectionUiSnapshot snapshot,
        IReadOnlyList<CManualInspectionUiPoint> allScopedPoints,
        IReadOnlyList<CManualInspectionUiPoint> visiblePoints, IEnumerable<string> selectedKeys,
        string? focusKey, string? representativeKey, string colorMode, string scopeSignature)
    {
        if (ContextMenu?.IsOpen == true) ContextMenu.IsOpen = false;
        var samePoints = ReferenceEquals(_all, allScopedPoints) || _all.Count == allScopedPoints.Count
            && _all.Zip(allScopedPoints).All(pair => ReferenceEquals(pair.First, pair.Second));
        var sameGeometry = samePoints || _all.Count == allScopedPoints.Count && _all.Zip(allScopedPoints).All(pair =>
            pair.First.Key == pair.Second.Key && pair.First.CellNo == pair.Second.CellNo && pair.First.HeadNo == pair.Second.HeadNo
            && pair.First.Row == pair.Second.Row && pair.First.Column == pair.Second.Column && pair.First.BeamNo == pair.Second.BeamNo);
        var nextVisibleKeys = visiblePoints.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        if (!samePoints || !_visibleKeys.SetEquals(nextVisibleKeys) || _snapshot.Version != snapshot.Version
            || _layoutSignature != snapshot.RunId + "|" + scopeSignature + "|" + allScopedPoints.Count)
            InvalidateClickSelection();
        if (_gesture && (!sameGeometry || !_visibleKeys.SetEquals(nextVisibleKeys) || _snapshot.Version != snapshot.Version
            || _layoutSignature != snapshot.RunId + "|" + scopeSignature + "|" + allScopedPoints.Count))
            CancelGesture(true);
        if (!samePoints || _snapshot.Version != snapshot.Version) { _hoverKey = null; _tip.IsOpen = false; }
        _snapshot = snapshot;
        _all = allScopedPoints;
        _visible = visiblePoints;
        if (!samePoints) _pointByKey = allScopedPoints.ToDictionary(p => p.Key, StringComparer.Ordinal);
        _visibleKeys = nextVisibleKeys;
        _selected = new HashSet<string>(selectedKeys, StringComparer.Ordinal);
        _focusKey = focusKey;
        _representative = representativeKey;
        _colorMode = colorMode;
        var nextSignature = snapshot.RunId + "|" + scopeSignature + "|" + allScopedPoints.Count;
        if (_layoutSignature != nextSignature)
        {
            _layoutSignature = nextSignature;
            _zoom = 1; _pan = default;
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        // Selection/status changes repaint the retained drawing; they do not regroup/sort every
        // matrix slot. Only real geometry/scope/size changes rebuild layout. Hidden slots remain.
        if (!sameGeometry) _layoutDirty = true;
        if (_hoverKey is not null && !_visibleKeys.Contains(_hoverKey)) { _hoverKey = null; _tip.IsOpen = false; }
        InvalidateVisual();
    }

    public void ResetView()
    {
        CancelGesture(false); _tip.IsOpen = false; _hoverKey = null;
        _zoom = 1; _pan = default;
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void PanBy(Vector delta)
    {
        if (!double.IsFinite(delta.X) || !double.IsFinite(delta.Y)) return;
        var next = _pan + delta; if (!double.IsFinite(next.X) || !double.IsFinite(next.Y)) return;
        _pan = next; InvalidateVisual();
    }
    public bool TryCancelGesture()
    {
        if (!_gesture) return false;
        CancelGesture(true); return true;
    }

    public void ZoomAt(double factor, Point origin)
    {
        if (!double.IsFinite(factor) || factor <= 0 || !double.IsFinite(origin.X) || !double.IsFinite(origin.Y)) return;
        var old = _zoom;
        var zoom = Math.Clamp(old * factor, 0.4, 12);
        var pan = new Vector(origin.X - (origin.X - _pan.X) * zoom / old,
            origin.Y - (origin.Y - _pan.Y) * zoom / old);
        if (!double.IsFinite(pan.X) || !double.IsFinite(pan.Y)) return;
        _zoom = zoom; _pan = pan;
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void BuildLayout()
    {
        if (!_layoutDirty) return;
        _layoutDirty = false;
        _slots.Clear(); _frames.Clear();
        var width = Math.Max(80, ActualWidth);
        var height = Math.Max(80, ActualHeight);
        var groups = _all.GroupBy(p => (p.CellNo, p.HeadNo))
            .OrderBy(g => g.Key.CellNo).ThenBy(g => g.Key.HeadNo).ToArray();
        if (groups.Length == 0) return;
        // A logical Row/Column matrix, not a drawing in stage millimetres. Packing balances the
        // readable slot size; each group's independent width/height then fills its allotted area.
        var shapes = groups.Select(g => (Columns: g.Select(p => p.Column).Distinct().Count(), Rows: g.Select(p => p.Row).Distinct().Count())).ToArray();
        var columns = 1; var bestPitch = 0d;
        var narrowestAspect = Math.Max(.05, shapes.Min(shape => (double)shape.Columns / Math.Max(1, shape.Rows)));
        var maximumColumns = Math.Min(groups.Length, Math.Max(12,
            (int)Math.Ceiling(Math.Sqrt(groups.Length * width / height / narrowestAspect) * 2) + 8));
        for (var candidate = 1; candidate <= maximumColumns; candidate++)
        {
            var lines = (int)Math.Ceiling((double)groups.Length / candidate);
            var candidateGap = Math.Min(17, Math.Max(.1, height / lines / 8));
            var candidateWidth = Math.Max(.01, (width - 40 - (candidate - 1) * candidateGap) / candidate);
            var candidateHeight = Math.Max(.01, (height - 40 - (lines - 1) * candidateGap) / lines);
            var candidateTitle = Math.Min(22, candidateHeight * .2);
            var pitch = shapes.Min(shape => Math.Min(candidateWidth / Math.Max(1, shape.Columns),
                (candidateHeight - candidateTitle) / Math.Max(1, shape.Rows)));
            if (pitch > bestPitch) { bestPitch = pitch; columns = candidate; }
        }
        var rows = Math.Max(1, (int)Math.Ceiling((double)groups.Length / columns));
        const double margin = 20;
        // A full 6GH dataset may contain hundreds of Cell/Head groups. Scale the
        // gutters and heading bands too, so Fit does not silently crop lower cells.
        var gap = Math.Min(17, Math.Max(0.1, height / Math.Max(1, rows) / 8));
        var groupWidth = Math.Max(2, (width - margin * 2 - gap * (columns - 1)) / columns);
        var groupHeight = Math.Max(0.1, (height - margin * 2 - gap * (rows - 1)) / rows);
        var title = Math.Min(22, groupHeight * 0.2);
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var gx = margin + i % columns * (groupWidth + gap);
            var gy = margin + i / columns * (groupHeight + gap);
            var columnKeys = group.Select(p => p.Column).Distinct().OrderBy(x => x).ToArray();
            var rowKeys = group.Select(p => p.Row).Distinct().OrderBy(x => x).ToArray();
            var columnMap = columnKeys.Select((key, index) => (key, index)).ToDictionary(x => x.key, x => x.index);
            var rowMap = rowKeys.Select((key, index) => (key, index)).ToDictionary(x => x.key, x => x.index);
            var gridWidth = groupWidth; var gridHeight = Math.Max(.01, groupHeight - title);
            var tileWidth = gridWidth / Math.Max(1, columnKeys.Length); var tileHeight = gridHeight / Math.Max(1, rowKeys.Length);
            var gridX = gx; var gridY = gy + title;
            _frames.Add((new Rect(gx, gy, groupWidth, groupHeight), new Rect(gridX, gridY, gridWidth, gridHeight),
                $"Cell {group.Key.CellNo}  ·  " + (group.Key.HeadNo > 0 ? $"H{group.Key.HeadNo:00}" : "미배정"), $"{rowKeys.Length}행 × {columnKeys.Length}열"));
            foreach (var bucket in group.GroupBy(p => (p.Row, p.Column)))
            {
                var points = bucket.OrderBy(p => p.BeamNo).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
                var subColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(points.Length)));
                var subRows = Math.Max(1, (int)Math.Ceiling((double)points.Length / subColumns));
                for (var j = 0; j < points.Length; j++)
                {
                    var p = points[j];
                    _slots.Add(new Slot(p, new Rect(gridX + columnMap[p.Column] * tileWidth + j % subColumns * tileWidth / subColumns,
                        gridY + rowMap[p.Row] * tileHeight + j / subColumns * tileHeight / subRows,
                        tileWidth / subColumns, tileHeight / subRows)));
                }
            }
        }
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        BuildLayout(); _hits.Clear(); _hitBuckets.Clear();
        context.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        context.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        var viewport = new Rect(0, 0, ActualWidth, ActualHeight);
        var line = BorderBrush ?? Brushes.Gray;
        var accent = CManualInspectionDrawing.Resource(this, "AccentBrush", Brushes.SteelBlue);
        if (_frames.Count > 0) CManualInspectionDrawing.Text(context, this, "행 · 열 논리 배치  /  실제 기판 mm 축척과 무관", new Point(12, 2), 9, Foreground);
        foreach (var frame in _frames)
        {
            var r = Screen(frame.Bounds);
            if (!r.IntersectsWith(viewport)) continue;
            if (r.Width > 25 && r.Height > 22)
                context.DrawRoundedRectangle(null, new Pen(line, .7), r, 5, 5);
            if (r.Height > 48 && r.Width > 95)
            {
                CManualInspectionDrawing.Text(context, this, frame.Title, new Point(r.Left + 6, r.Top + 2), 11, Foreground, true, r.Width - 12);
                if (r.Width > 195) CManualInspectionDrawing.Text(context, this, frame.Extents,
                    new Point(r.Right - 94, r.Top + 3), 9, Foreground, false, 88);
            }
        }
        foreach (var slot in _slots)
        {
            if (!_visibleKeys.Contains(slot.Point.Key)) continue;
            var r = Screen(slot.Bounds);
            if (!r.IntersectsWith(viewport)) continue;
            var p = _pointByKey.GetValueOrDefault(slot.Point.Key) ?? slot.Point;
            var minimum = Math.Min(r.Width, r.Height);
            var selected = _selected.Contains(p.Key); var hover = p.Key == _hoverKey; var focused = p.Key == _focusKey;
            var brush = CManualInspectionDrawing.PointBrush(this, p, _colorMode, _snapshot);
            // Screen-space gutters stay small at any zoom. Subpixel overview slots reduce the
            // gutter proportionally, so a large dataset still has a visibly filled heat-map area.
            var gap = Math.Min(1.5, minimum * .04);
            var tile = new Rect(r.Left + gap / 2, r.Top + gap / 2, Math.Max(.001, r.Width - gap), Math.Max(.001, r.Height - gap));
            var textBrush = TileTextBrush(brush);
            context.DrawRectangle(brush, null, tile);
            if (p.IsMasked)
            {
                if (minimum > 4)
                {
                    var inset = Math.Min(5, minimum * .2); var pen = new Pen(textBrush, minimum > 12 ? 1.1 : .6);
                    context.DrawLine(pen, new(tile.Left + inset, tile.Top + inset), new(tile.Right - inset, tile.Bottom - inset));
                    context.DrawLine(pen, new(tile.Right - inset, tile.Top + inset), new(tile.Left + inset, tile.Bottom - inset));
                }
            }
            if ((selected || hover) && minimum > 4)
            {
                var outline = Inset(tile, 1.4);
                context.DrawRectangle(null, new Pen(textBrush, selected ? 3.8 : 2.2), outline);
                context.DrawRectangle(null, new Pen(accent, selected ? 1.8 : 1), outline);
            }
            if (focused && minimum > 12)
            {
                var focusPen = new Pen(textBrush, 1.2) { DashStyle = DashStyles.Dot };
                context.DrawRectangle(null, focusPen, Inset(tile, 4.5));
            }
            if (p.Key == _representative && minimum > 12)
            {
                context.DrawRectangle(null, new Pen(textBrush, 3.8), Inset(tile, 3));
                context.DrawRectangle(null, new Pen(CManualInspectionDrawing.Color("#D18B00"), 2), Inset(tile, 3));
            }
            if (r.Width > 28 && r.Height > 18)
            {
                var mark = p.Key == _representative ? "★ " : p.IsMasked ? "× " : p.WorkingJudge == "ERROR" ? "! "
                    : p.WorkingJudge == "NG" ? "NG " : p.IsProxy ? "◇ " : p.UsesAppliedMeasurement ? "□ "
                    : !p.HasMeasurementHistory ? "· " : "";
                var label = mark + "S" + p.ShotNo + (r.Width > 115 ? $" / B{p.BeamNo}" : "");
                CManualInspectionDrawing.Text(context, this, label, new Point(tile.Left + 5, tile.Top + Math.Max(2, tile.Height / 2 - 7)),
                    Math.Clamp(tile.Height / 3.3, 9, 12), textBrush, selected || focused, Math.Max(1, tile.Width - 10));
            }
            else if (minimum > 8 && (p.Key == _representative || p.IsMasked || p.WorkingJudge is "NG" or "ERROR" || !p.HasMeasurementHistory))
                CManualInspectionDrawing.Text(context, this, p.Key == _representative ? "★" : p.IsMasked ? "×"
                    : p.WorkingJudge is "NG" or "ERROR" ? "!" : "·", new Point(tile.Left + 2, tile.Top + 1), 9, textBrush, true);
            AddHit(p, r, viewport);
        }
        if (_slots.Count == 0)
            CManualInspectionDrawing.Text(context, this, "표시할 위치가 없습니다. 현재 Recipe를 불러오거나 필터를 바꾸세요.", new Point(18, 30), 13, Foreground);
        if (_gesture && !_panning && _moved)
        {
            var selection = new Rect(_start, _last);
            context.DrawRectangle(CManualInspectionDrawing.Color("#223F83B5"),
                new Pen(CManualInspectionDrawing.Resource(this, "AccentBrush", Brushes.SteelBlue), 1), selection);
        }
        context.Pop();
        if (IsKeyboardFocusWithin)
            context.DrawRectangle(null, new Pen(CManualInspectionDrawing.Resource(this, "AccentBrush", Brushes.SteelBlue), 2),
                new Rect(1, 1, Math.Max(1, ActualWidth - 2), Math.Max(1, ActualHeight - 2)));
    }

    private Point Screen(Point point) => new(point.X * _zoom + _pan.X, point.Y * _zoom + _pan.Y);
    private Rect Screen(Rect rect) => new(Screen(rect.TopLeft), new Size(rect.Width * _zoom, rect.Height * _zoom));
    private static Rect Inset(Rect rect, double value)
    {
        var inset = Math.Min(value, Math.Max(0, Math.Min(rect.Width, rect.Height) / 2 - .1));
        return new Rect(rect.Left + inset, rect.Top + inset, Math.Max(.1, rect.Width - inset * 2), Math.Max(.1, rect.Height - inset * 2));
    }
    private static Brush TileTextBrush(Brush fill)
    {
        if (fill is not SolidColorBrush solid) return Brushes.Black;
        static double Linear(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        var luminance = .2126 * Linear(solid.Color.R) + .7152 * Linear(solid.Color.G) + .0722 * Linear(solid.Color.B);
        return (luminance + .05) / .05 >= 1.05 / (luminance + .05) ? Brushes.Black : Brushes.White;
    }
    private void AddHit(CManualInspectionUiPoint point, Rect rect, Rect viewport)
    {
        var index = _hits.Count; _hits.Add((point, rect));
        var clipped = Rect.Intersect(rect, viewport);
        if (clipped.IsEmpty) return;
        for (var y = (int)(clipped.Top / 32); y <= (int)(clipped.Bottom / 32); y++)
            for (var x = (int)(clipped.Left / 32); x <= (int)(clipped.Right / 32); x++)
            {
                if (!_hitBuckets.TryGetValue((x, y), out var bucket)) _hitBuckets[(x, y)] = bucket = [];
                bucket.Add(index);
            }
    }
    private CManualInspectionUiPoint? Hit(Point point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight) return null;
        if (!_hitBuckets.TryGetValue(((int)(point.X / 32), (int)(point.Y / 32)), out var bucket)) return null;
        for (var i = bucket.Count - 1; i >= 0; i--) if (_hits[bucket[i]].Bounds.Contains(point)) return _hits[bucket[i]].Point;
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus(); _tip.IsOpen = false;
        if (TryResetOnDoubleClick(e.ClickCount)) { e.Handled = true; return; }
        RememberSelectionBeforeClick();
        _gesture = true; _moved = false;
        _panning = IsPanTool || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (_panning) InvalidateClickSelection();
        _additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        _start = _last = e.GetPosition(this); _startPan = _pan;
        Cursor = _panning ? Cursors.ScrollAll : Cursors.Cross;
        CaptureMouse(); e.Handled = true;
    }

    private bool TryResetOnDoubleClick(int clickCount)
    {
        if (clickCount < 2) return false;
        var before = _canRestoreClickSelection && _selectionBeforeClick is { } captured
            && captured.Version == _snapshot.Version && captured.Scope == _layoutSignature ? captured : null;
        InvalidateClickSelection();
        if (before is not null && (!_selected.SetEquals(before.Keys) || _focusKey != before.FocusKey))
            SelectionRequested?.Invoke(this, new(before.Keys, before.FocusKey, clearFocus: before.FocusKey is null));
        ResetView(); return true;
    }

    private void RememberSelectionBeforeClick()
    {
        _selectionBeforeClick = new(_selected.ToArray(), _focusKey, _snapshot.Version, _layoutSignature);
        _canRestoreClickSelection = false;
    }

    private void CompleteClickSelectionTracking(bool panning, bool moved)
    {
        if (panning || moved) { InvalidateClickSelection(); return; }
        _canRestoreClickSelection = _selectionBeforeClick is not null;
    }

    private void InvalidateClickSelection() { _selectionBeforeClick = null; _canRestoreClickSelection = false; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = e.GetPosition(this);
        if (_gesture)
        {
            _last = at; _moved |= (at - _start).Length > 4;
            if (_moved) InvalidateClickSelection();
            if (_panning) _pan = _startPan + at - _start;
            InvalidateVisual(); e.Handled = true; return;
        }
        var point = Hit(at);
        if (_hoverKey == point?.Key) return;
        _hoverKey = point?.Key;
        InvalidateVisual();
        _tip.SetResourceReference(Control.BackgroundProperty, "PreviewHoverTipBackgroundBrush");
        _tip.SetResourceReference(Control.ForegroundProperty, "PreviewHoverTipTextBrush");
        _tip.Content = point is null ? null : $"{point.Key}\n{point.MeasurementStatusText} · 편차 {point.MeasurementErrorText} µm\n{point.MeasurementBasisText}\n" +
            (point.HasAppliedOffset ? $"적용 보정 B {point.BaseOffsetText} µm · {(CManualInspectionDrawing.AppliedLevel(point,_snapshot) >= 0 ? "L"+(CManualInspectionDrawing.AppliedLevel(point,_snapshot)+1) : "Level 확인 필요")}" : "활성 보정 미적용") +
            (string.IsNullOrWhiteSpace(point.DisplayedMeasurementOrigin) ? "" : "\n측정 출처: " + point.DisplayedMeasurementOrigin) +
            (point.IsMasked ? "\nMasking · 측정 선택 제외" : point.IsProxy ? $"\n◇ 대표값 복사: {point.ReferenceKey}" : "");
        _tip.IsOpen = point is not null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_gesture) return;
        var panning = _panning; var moved = _moved; var additive = _additive;
        var end = e.GetPosition(this); _gesture = false;
        ReleaseMouseCapture(); Cursor = IsPanTool ? Cursors.ScrollAll : Cursors.Cross;
        CompleteClickSelectionTracking(panning, moved);
        if (!panning)
        {
            if (moved)
            {
                SelectRectangle(new Rect(_start, end), additive);
            }
            else
            {
                var point = Hit(end);
                if (point?.IsMasked == true) MessageRequested?.Invoke("Masking 위치는 측정 대상으로 선택하지 않습니다.");
                else if (point is not null) SelectionRequested?.Invoke(this, new([point.Key], point.Key, additive, additive));
                else if (!additive) SelectionRequested?.Invoke(this, new([], null));
            }
        }
        InvalidateVisual(); e.Handled = true;
    }

    public void SelectRectangle(Rect rectangle, bool additive)
    {
        var points = _hits.Where(x => !x.Point.IsMasked && rectangle.Contains(
            new Point(x.Bounds.Left + x.Bounds.Width / 2, x.Bounds.Top + x.Bounds.Height / 2))).Select(x => x.Point).ToArray();
        SelectionRequested?.Invoke(this, new(points.Select(x => x.Key).ToArray(), points.LastOrDefault()?.Key, additive));
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_gesture) CancelGesture(true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_gesture) { _tip.IsOpen = false; _hoverKey = null; InvalidateVisual(); }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { base.OnMouseWheel(e); return; }
        ZoomAt(Math.Exp(Math.Sign(e.Delta) * 0.14), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var point = Hit(e.GetPosition(this));
        if (point is null) return;
        Focus();
        if (point.IsMasked) MessageRequested?.Invoke("Masking 위치는 이동·측정·보정 대상에서 제외됩니다.");
        else
        {
            if (!_selected.Contains(point.Key)) SelectionRequested?.Invoke(this, new([point.Key], point.Key));
            else SelectionRequested?.Invoke(this, new([], point.Key, true));
            ShowOperationMenu();
        }
        e.Handled = true;
    }

    public void ShowOperationMenu()
    {
        CancelGesture(false); _tip.IsOpen = false;
        if (_selected.Count == 0 && _focusKey is not null)
            SelectionRequested?.Invoke(this, new([_focusKey], _focusKey));
        var keys = _snapshot.Points.Where(p => _selected.Contains(p.Key) && !p.IsMasked).Select(p => p.Key).ToArray();
        if (keys.Length == 0) { MessageRequested?.Invoke("먼저 작업할 위치를 선택하세요."); return; }
        var version = _snapshot.Version;
        var menu = CManualInspectionDrawing.CreateContextMenu(this);
        var hidden = keys.Count(key => !_visibleKeys.Contains(key));
        menu.Items.Add(new MenuItem { Header = $"선택 {keys.Length}점" + (hidden > 0 ? $" · 현재 화면 숨김 {hidden}점 포함" : "")
            + $"\n{_focusKey ?? keys[0]}", IsEnabled = false });
        menu.ToolTip = "현재 화면의 필터 밖에 숨김 선택이 있어도 선택 범위에 포함합니다. 실제 동작 전 확인 창에서 전체 대상을 확인하세요.";
        menu.Items.Add(new Separator());
        foreach (var (title, action) in new[]
        {
            ("이동만", EN_MANUAL_INSPECTION_UI_ACTION.Move),
            ("이동 후 측정", EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure),
            ("이동 후 측정 & 보정", EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasureCorrect)
        })
        {
            var item = new MenuItem { Header = title, IsEnabled = !_snapshot.IsBusy && !_snapshot.IsReadOnly };
            item.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            item.Click += (_, _) => OperationRequested?.Invoke(this, new(action, keys, version));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var focus = _snapshot.Points.FirstOrDefault(p => p.Key == _focusKey && !p.IsMasked)
            ?? _snapshot.Points.First(p => p.Key == keys[0]);
        foreach (var (title, action) in new[]
        {
            ("현재 위치 한 점 측정", EN_MANUAL_INSPECTION_UI_ACTION.Measure),
            ("측정 · 출처 상세", EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint)
        })
        {
            var item = new MenuItem { Header = title + " · " + focus.Key,
                IsEnabled = !_snapshot.IsBusy && (action == EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint || !_snapshot.IsReadOnly) };
            item.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            item.Click += (_, _) => OperationRequested?.Invoke(this, new(action, [focus.Key], version));
            menu.Items.Add(item);
        }
        ContextMenu = menu; menu.IsOpen = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 100 : 40;
            PanBy(key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), _ => new Vector(0, step) });
            e.Handled = true; return;
        }
        if (key == Key.Apps || key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        { ShowOperationMenu(); e.Handled = true; return; }
        if (key == Key.Escape && _gesture) { CancelGesture(true); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }

    private void CancelGesture(bool restorePan)
    {
        InvalidateClickSelection();
        if (restorePan && _gesture && _panning) _pan = _startPan;
        _gesture = false;
        _panning = false; _moved = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = IsPanTool ? Cursors.ScrollAll : Cursors.Cross;
        InvalidateVisual();
    }
}

internal static class CManualInspectionDrawing
{
    private static readonly Dictionary<string, Brush> BrushesByColor = new(StringComparer.Ordinal);
    // Scope these templates to Manual Inspection's flat command menus. The system MenuItem
    // icon gutter otherwise remains white under our dark theme; no global theme is replaced.
    private static readonly ResourceDictionary FlatMenuStyles = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Style TargetType="ContextMenu">
            <Setter Property="OverridesDefaultStyle" Value="True" />
            <Setter Property="Background" Value="{DynamicResource PanelBackgroundBrush}" />
            <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}" />
            <Setter Property="BorderBrush" Value="{DynamicResource PanelBorderBrush}" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="Padding" Value="4" />
            <Setter Property="MinWidth" Value="220" />
            <Setter Property="MaxWidth" Value="580" />
            <Setter Property="FontFamily" Value="Segoe UI" />
            <Setter Property="FontSize" Value="12" />
            <Setter Property="ItemsPanel"><Setter.Value><ItemsPanelTemplate><StackPanel IsItemsHost="True" /></ItemsPanelTemplate></Setter.Value></Setter>
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="ContextMenu">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" CornerRadius="5" SnapsToDevicePixels="True">
                  <ScrollViewer Focusable="False" CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
                    <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Cycle" SnapsToDevicePixels="True" />
                  </ScrollViewer>
                </Border>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style TargetType="MenuItem">
            <Setter Property="OverridesDefaultStyle" Value="True" />
            <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}" />
            <Setter Property="Padding" Value="11,7" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
            <Setter Property="Template"><Setter.Value>
              <ControlTemplate TargetType="MenuItem">
                <Border x:Name="ItemSurface" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="3" Padding="{TemplateBinding Padding}" Margin="1">
                  <ContentPresenter x:Name="HeaderHost" ContentSource="Header" RecognizesAccessKey="True"
                                    VerticalAlignment="Center" TextElement.Foreground="{TemplateBinding Foreground}" />
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsHighlighted" Value="True">
                    <Setter TargetName="ItemSurface" Property="Background" Value="{DynamicResource MenuItemSelectedBackgroundBrush}" />
                    <Setter TargetName="ItemSurface" Property="BorderBrush" Value="{DynamicResource MenuItemSelectedBorderBrush}" />
                    <Setter TargetName="HeaderHost" Property="TextElement.Foreground" Value="{DynamicResource MenuItemSelectedTextBrush}" />
                  </Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True">
                    <Setter TargetName="ItemSurface" Property="Background" Value="{DynamicResource MenuItemSelectedBackgroundBrush}" />
                    <Setter TargetName="ItemSurface" Property="BorderBrush" Value="{DynamicResource AccentBrush}" />
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.48" /></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value></Setter>
          </Style>
          <Style TargetType="Separator">
            <Setter Property="Focusable" Value="False" />
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator">
              <Border Height="1" Margin="5,4" Background="{DynamicResource PanelBorderBrush}" SnapsToDevicePixels="True" />
            </ControlTemplate></Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """);

    internal static ContextMenu CreateContextMenu(UIElement owner)
    {
        var menu = new ContextMenu { PlacementTarget = owner };
        foreach (var key in FlatMenuStyles.Keys) menu.Resources[key] = FlatMenuStyles[key];
        menu.Style = (Style)FlatMenuStyles[typeof(ContextMenu)];
        return menu;
    }
    internal static readonly string[] HeadColors = ["#2B7AA7", "#5A9A76", "#A36A9D", "#B38649", "#697DB5", "#B66575", "#5A9BA2", "#8C9454"];
    internal static Brush Color(string value)
    {
        if (BrushesByColor.TryGetValue(value, out var existing)) return existing;
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!; brush.Freeze();
        BrushesByColor[value] = brush; return brush;
    }
    internal static Brush Resource(FrameworkElement owner, string key, Brush fallback) => owner.TryFindResource(key) as Brush ?? fallback;
    internal static int Level(CManualInspectionUiPoint point, CManualInspectionUiSnapshot snapshot)
    {
        if (!point.HasMeasurementHistory) return -1;
        return CManualInspectionLevels.Evaluate(point.MeasurementXUm, point.MeasurementYUm, snapshot.LevelMetric, snapshot.LevelBoundsUm) ?? -1;
    }
    internal static int AppliedLevel(CManualInspectionUiPoint point, CManualInspectionUiSnapshot snapshot) => point.IsMasked || !point.HasAppliedOffset ? -1
        : CManualInspectionLevels.Evaluate(point.BaseXUm, point.BaseYUm, snapshot.LevelMetric, snapshot.LevelBoundsUm) ?? -1;
    internal static Brush PointBrush(FrameworkElement owner, CManualInspectionUiPoint point, string mode, CManualInspectionUiSnapshot snapshot)
    {
        if (point.IsMasked) return Resource(owner, "ReviewMaskBackgroundBrush", Color("#EFF2F5"));
        if (mode == "applied") return LevelBrush(AppliedLevel(point, snapshot));
        if (mode == "head") return Color(HeadColors[Math.Max(0, point.HeadNo - 1) % HeadColors.Length]);
        if (mode == "level") return LevelBrush(Level(point, snapshot));
        if (mode == "source") return point.Source switch { "MEASURED" => Color("#3B9870"), "IMPORTED" => Color("#7899A8"), "INTERPOLATED" => Color("#77A6CD"), "MANUAL" => Color("#A482BE"), _ => Color("#DBE4EA") };
        return point.UsesAppliedMeasurement ? Color("#638CA8")
            : point.WorkingJudge switch { "OK" => Color("#3B9870"), "NG" => Color("#D16776"), "ERROR" => Color("#D39A45"), _ => Color("#DBE4EA") };
    }
    private static Brush LevelBrush(int level) => Color(level switch { 0 => "#2E9076", 1 => "#B88B2A", 2 => "#CA754A", 3 => "#BE4F68", _ => "#DBE4EA" });
    internal static void Marker(DrawingContext dc, Point at, double radius, Brush color, string shape, bool hollow)
    {
        var pen = hollow ? new Pen(color, 1.5) : null;
        var fill = hollow ? null : color;
        if (shape == "square") dc.DrawRectangle(fill, pen, new Rect(at.X - radius, at.Y - radius, radius * 2, radius * 2));
        else if (shape == "diamond")
        {
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
            {
                drawing.BeginFigure(new Point(at.X, at.Y - radius), !hollow, true);
                drawing.LineTo(new Point(at.X + radius, at.Y), true, false);
                drawing.LineTo(new Point(at.X, at.Y + radius), true, false);
                drawing.LineTo(new Point(at.X - radius, at.Y), true, false);
            }
            geometry.Freeze(); dc.DrawGeometry(fill, pen, geometry);
        }
        else dc.DrawEllipse(fill, pen, at, radius, radius);
    }
    internal static void Text(DrawingContext drawing, FrameworkElement owner, string text, Point origin,
        double size, Brush? color, bool bold = false, double maxWidth = double.PositiveInfinity)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, color ?? Brushes.Gray, VisualTreeHelper.GetDpi(owner).PixelsPerDip);
        if (double.IsFinite(maxWidth)) { ft.MaxTextWidth = Math.Max(1, maxWidth); ft.Trimming = TextTrimming.CharacterEllipsis; }
        drawing.DrawText(ft, origin);
    }
}

/// <summary>One accessible surface peer, without allocating a WPF/UIA object per measured point.</summary>
internal sealed class CManualInspectionSurfacePeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
{
    protected override string GetClassNameCore() => Owner.GetType().Name;
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
}
