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
    private readonly List<(int CellNo, Rect Header, Rect AllCells)> _cellHeaders = [];
    private readonly Dictionary<string, Point[]> _overviewPolygons = new(StringComparer.Ordinal);
    private readonly List<Point[]> _overviewFrames = [];
    private readonly List<Point[]?> _hitPolygons = [];
    private bool _showCellOverview;
    private bool _hasRecipeLayout;
    private string _cellLayoutFingerprint = "";
    private Rect _glassBounds;
    private Rect _allCellsButton;
    private readonly Dictionary<(int X, int Y), List<int>> _hitBuckets = [];
    private Dictionary<string, CManualInspectionUiPoint> _pointByKey = new(StringComparer.Ordinal);
    private HashSet<string> _visibleKeys = new(StringComparer.Ordinal);
    private CManualInspectionUiSnapshot _snapshot = new();
    private string? _representative;
    private string? _focusKey;
    private string _colorMode = "level";
    private string _layoutSignature = "";
    private bool _layoutDirty = true;
    private bool _gesture;
    private bool _panning;
    private bool _additive;
    private bool _moved;
    private int? _overviewClickCell;
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
    public event EventHandler<int>? CellRequested;
    public event EventHandler? AllCellsRequested;
    public event Action<string>? MessageRequested;
    public double Zoom => _zoom;
    public Vector Pan => _pan;
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
        _tip.SetResourceReference(Control.BackgroundProperty, "PreviewHoverTipBackgroundBrush");
        _tip.SetResourceReference(Control.ForegroundProperty, "PreviewHoverTipTextBrush");
        ToolTipService.SetInitialShowDelay(this, 180);
        ToolTipService.SetShowDuration(this, 15000);
        AutomationProperties.SetName(this, "리뷰 측정 위치 논리 행렬");
        AutomationProperties.SetHelpText(this, "전체 Cell 보기에서는 셀 내부를 클릭하면 해당 Cell을 엽니다. 드래그로 측정점을 선택하고 Ctrl 드래그로 추가합니다. 단일 Cell에서는 Ctrl 클릭으로 복수 선택합니다. 방향키로 측정점 선택, Space로 동작 메뉴, Esc로 메뉴 닫기, Ctrl 휠 확대, Alt 방향키 화면 이동, 더블클릭 맞춤을 제공합니다.");
        SizeChanged += (_, _) => { _layoutDirty = true; InvalidateVisual(); };
        Unloaded += (_, _) => { CancelGesture(true); _tip.IsOpen = false; if (ContextMenu is not null) ContextMenu.IsOpen = false; };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CManualInspectionSurfacePeer(this);

    public void Configure(CManualInspectionUiSnapshot snapshot,
        IReadOnlyList<CManualInspectionUiPoint> allScopedPoints,
        IReadOnlyList<CManualInspectionUiPoint> visiblePoints, IEnumerable<string> selectedKeys,
        string? focusKey, string? representativeKey, string colorMode, string scopeSignature, bool showCellOverview = false)
    {
        if (ContextMenu?.IsOpen == true) ContextMenu.IsOpen = false;
        var samePoints = ReferenceEquals(_all, allScopedPoints) || _all.Count == allScopedPoints.Count
            && _all.Zip(allScopedPoints).All(pair => ReferenceEquals(pair.First, pair.Second));
        var sameGeometry = samePoints || _all.Count == allScopedPoints.Count && _all.Zip(allScopedPoints).All(pair =>
            pair.First.Key == pair.Second.Key && pair.First.CellNo == pair.Second.CellNo && pair.First.HeadNo == pair.Second.HeadNo
            && pair.First.Row == pair.Second.Row && pair.First.Column == pair.Second.Column && pair.First.BeamNo == pair.Second.BeamNo);
        var nextVisibleKeys = visiblePoints.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var layoutFingerprint = string.Join(";", snapshot.CellLayouts.Select(cell => cell.CellNo + ":" + cell.Enabled + ":"
            + string.Join("/", cell.BoundaryMm.Select(vertex => FormattableString.Invariant($"{vertex.Xmm:R},{vertex.Ymm:R}")))))
            + FormattableString.Invariant($"|{snapshot.CellLayoutYDown}|{snapshot.GlassWidthMm:R}|{snapshot.GlassHeightMm:R}|{showCellOverview}");
        var scopeIdentity = snapshot.RunId + "|" + scopeSignature + "|" + allScopedPoints.Count + "|" + layoutFingerprint;
        if (_cellLayoutFingerprint != layoutFingerprint)
        { _cellLayoutFingerprint = layoutFingerprint; sameGeometry = false; InvalidateClickSelection(); }
        if (!samePoints || !_visibleKeys.SetEquals(nextVisibleKeys) || _snapshot.Version != snapshot.Version
            || _layoutSignature != scopeIdentity)
            InvalidateClickSelection();
        if (_gesture && (!sameGeometry || !_visibleKeys.SetEquals(nextVisibleKeys) || _snapshot.Version != snapshot.Version
            || _layoutSignature != scopeIdentity))
            CancelGesture(true);
        if (!samePoints || _snapshot.Version != snapshot.Version) { _hoverKey = null; _tip.IsOpen = false; }
        _snapshot = snapshot;
        _showCellOverview = showCellOverview;
        _all = allScopedPoints;
        _visible = visiblePoints;
        if (!samePoints) _pointByKey = allScopedPoints.ToDictionary(p => p.Key, StringComparer.Ordinal);
        _visibleKeys = nextVisibleKeys;
        _selected = new HashSet<string>(selectedKeys, StringComparer.Ordinal);
        _focusKey = focusKey;
        _representative = representativeKey;
        _colorMode = colorMode;
        var nextSignature = scopeIdentity;
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

    public void RestoreView(double zoom, Vector pan)
    {
        if (!double.IsFinite(zoom) || !double.IsFinite(pan.X) || !double.IsFinite(pan.Y))
            throw new ArgumentOutOfRangeException(nameof(zoom), "보기 배율과 이동값은 유한한 숫자여야 합니다.");
        CancelGesture(false); _tip.IsOpen = false; _hoverKey = null;
        _zoom = Math.Clamp(zoom, .4, 12); _pan = pan;
        ViewChanged?.Invoke(this, EventArgs.Empty); InvalidateVisual();
    }

    public void PanBy(Vector delta)
    {
        if (!double.IsFinite(delta.X) || !double.IsFinite(delta.Y)) return;
        var next = _pan + delta; if (!double.IsFinite(next.X) || !double.IsFinite(next.Y)) return;
        _pan = next; InvalidateVisual();
    }
    public bool TryCancelGesture()
    {
        if (TryCloseOperationMenu()) return true;
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
        _slots.Clear(); _frames.Clear(); _cellHeaders.Clear(); _overviewPolygons.Clear(); _overviewFrames.Clear(); _glassBounds = Rect.Empty;
        _hasRecipeLayout = false;
        var width = Math.Max(80, ActualWidth);
        var height = Math.Max(80, ActualHeight);
        _allCellsButton = !_showCellOverview && (_snapshot.Points.Count > 0 || _snapshot.CellLayouts.Count > 0)
            ? new Rect(Math.Max(8, width - 93), 1, 82, 18) : Rect.Empty;
        var groups = _all.GroupBy(p => (p.CellNo, p.HeadNo))
            .OrderBy(g => g.Key.CellNo).ThenBy(g => g.Key.HeadNo).ToArray();
        if (_showCellOverview && BuildRecipeOverview(width, height)) { _hasRecipeLayout = true; return; }
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
        var allCellsPlaced = false;
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
            // One overview action per map: prefer the first readable row/column heading.
            // Small multi-head groups retain the single fixed fallback above the matrix.
            var allCells = !_showCellOverview && !allCellsPlaced && groupWidth > 180 && title > 14
                ? new Rect(gx + groupWidth - 77, gy + 2, 73, Math.Min(18, title - 2)) : Rect.Empty;
            _cellHeaders.Add((group.Key.CellNo, new Rect(gx, gy, Math.Min(groupWidth, 160), title), allCells));
            if (!allCells.IsEmpty) { allCellsPlaced = true; _allCellsButton = Rect.Empty; }
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

    private bool BuildRecipeOverview(double width, double height)
    {
        var grouped = _all.GroupBy(point => point.CellNo).ToArray();
        var scopedByCell = grouped.ToDictionary(group => group.Key, group => group.ToDictionary(point => point.Key, StringComparer.Ordinal));
        var completeByCell = _snapshot.Points.GroupBy(point => point.CellNo).ToDictionary(group => group.Key, group => group.ToArray());
        var cells = new Dictionary<int, CManualInspectionUiCellLayout>();
        foreach (var layout in _snapshot.CellLayouts)
        {
            if (layout.CellNo <= 0 || layout.BoundaryMm.Count != 4
                || layout.BoundaryMm.Any(vertex => !double.IsFinite(vertex.Xmm) || !double.IsFinite(vertex.Ymm))
                || !cells.TryAdd(layout.CellNo, layout)) return false;
            var a = layout.BoundaryMm[0]; var b = layout.BoundaryMm[1]; var d = layout.BoundaryMm[3];
            if (Math.Abs((b.Xmm - a.Xmm) * (d.Ymm - a.Ymm) - (b.Ymm - a.Ymm) * (d.Xmm - a.Xmm)) < 1e-12) return false;
            var c = layout.BoundaryMm[2];
            if (Math.Abs(c.Xmm - (b.Xmm + d.Xmm - a.Xmm)) > .000001 || Math.Abs(c.Ymm - (b.Ymm + d.Ymm - a.Ymm)) > .000001) return false;
        }
        if (grouped.Any(group => !cells.ContainsKey(group.Key))) return false;
        var layouts = cells.Values.ToArray(); if (layouts.Length == 0) return false;
        var xmin = layouts.Min(cell => cell.BoundaryMm.Min(vertex => vertex.Xmm));
        var xmax = layouts.Max(cell => cell.BoundaryMm.Max(vertex => vertex.Xmm));
        var ymin = layouts.Min(cell => cell.BoundaryMm.Min(vertex => vertex.Ymm));
        var ymax = layouts.Max(cell => cell.BoundaryMm.Max(vertex => vertex.Ymm));
        var hasGlass = double.IsFinite(_snapshot.GlassWidthMm) && _snapshot.GlassWidthMm > 0
            && double.IsFinite(_snapshot.GlassHeightMm) && _snapshot.GlassHeightMm > 0;
        if (hasGlass) { xmin = Math.Min(xmin, 0); ymin = Math.Min(ymin, 0); xmax = Math.Max(xmax, _snapshot.GlassWidthMm); ymax = Math.Max(ymax, _snapshot.GlassHeightMm); }
        if (!double.IsFinite(xmax - xmin) || !double.IsFinite(ymax - ymin) || xmax <= xmin || ymax <= ymin) return false;
        var scale = Math.Min(Math.Max(1, width - 48) / (xmax - xmin), Math.Max(1, height - 56) / (ymax - ymin));
        var ox = (width - (xmax - xmin) * scale) / 2; var oy = 32 + (height - 48 - (ymax - ymin) * scale) / 2;
        Point Display(double x, double y) => new(ox + (x - xmin) * scale,
            oy + (_snapshot.CellLayoutYDown ? y - ymin : ymax - y) * scale);
        if (hasGlass)
        {
            var a = Display(0, 0); var b = Display(_snapshot.GlassWidthMm, _snapshot.GlassHeightMm);
            _glassBounds = new Rect(a, b);
        }
        foreach (var layout in layouts)
        {
            var corners = layout.BoundaryMm.Select(vertex => Display(vertex.Xmm, vertex.Ymm)).ToArray();
            var bounds = Bounds(corners);
            var wholeCell = completeByCell.GetValueOrDefault(layout.CellNo) ?? [];
            var rows = wholeCell.Select(point => point.Row).Distinct().OrderBy(row => row).ToArray();
            var columns = wholeCell.Select(point => point.Column).Distinct().OrderBy(column => column).ToArray();
            _frames.Add((bounds, bounds, $"Cell {layout.CellNo}" + (layout.Enabled ? "" : " · 미사용"), $"{rows.Length}행 × {columns.Length}열"));
            _overviewFrames.Add(corners);
            _cellHeaders.Add((layout.CellNo, new Rect(bounds.Left, bounds.Top, Math.Min(bounds.Width, 170), Math.Min(19, Math.Max(4, bounds.Height * .2))), Rect.Empty));
            if (rows.Length == 0 || columns.Length == 0) continue;
            var rowIndices = rows.Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index);
            var columnIndices = columns.Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index);
            if (!scopedByCell.TryGetValue(layout.CellNo, out var cellPoints)) continue;
            Point Affine(double u, double v) => corners[0] + (corners[1] - corners[0]) * u + (corners[3] - corners[0]) * v;
            foreach (var bucket in wholeCell.GroupBy(point => (point.Row, point.Column)))
            {
                var points = bucket.OrderBy(point => point.BeamNo).ThenBy(point => point.Key, StringComparer.Ordinal).ToArray();
                var nc = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(points.Length))); var nr = Math.Max(1, (int)Math.Ceiling((double)points.Length / nc));
                for (var index = 0; index < points.Length; index++)
                {
                    if (!cellPoints.TryGetValue(points[index].Key, out var point)) continue;
                    var u0 = (columnIndices[point.Column] + (double)(index % nc) / nc) / columns.Length;
                    var u1 = (columnIndices[point.Column] + (double)(index % nc + 1) / nc) / columns.Length;
                    var v0 = (rowIndices[point.Row] + (double)(index / nc) / nr) / rows.Length;
                    var v1 = (rowIndices[point.Row] + (double)(index / nc + 1) / nr) / rows.Length;
                    var polygon = new[] { Affine(u0, v0), Affine(u1, v0), Affine(u1, v1), Affine(u0, v1) };
                    _overviewPolygons[point.Key] = polygon; _slots.Add(new(point, Bounds(polygon)));
                }
            }
        }
        return true;
    }

    private static Rect Bounds(IEnumerable<Point> points)
    { var values = points.ToArray(); return new Rect(new Point(values.Min(point => point.X), values.Min(point => point.Y)), new Point(values.Max(point => point.X), values.Max(point => point.Y))); }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        BuildLayout(); _hits.Clear(); _hitBuckets.Clear(); _hitPolygons.Clear();
        context.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        context.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        var viewport = new Rect(0, 0, ActualWidth, ActualHeight);
        var line = BorderBrush ?? Brushes.Gray;
        var accent = CManualInspectionDrawing.Resource(this, "AccentBrush", Brushes.SteelBlue);
        if (_frames.Count > 0) CManualInspectionDrawing.Text(context, this,
            _hasRecipeLayout ? "전체 Cell · Recipe 실제 배치 / 타일은 논리 측정 격자 / 이동은 원래 계획 좌표 사용"
                : _showCellOverview ? "실제 배치 정보 미확인 · Cell/Head 논리 분리 표시" : "단일 Cell · 행/열 논리 측정 격자", new Point(12, 2), 9, Foreground, false,
            Math.Max(1, ActualWidth - (_allCellsButton.IsEmpty ? 24 : 112)), singleLine: true);
        if (!_glassBounds.IsEmpty) context.DrawRectangle(null, new Pen(line, 1), Screen(_glassBounds));
        for (var frameIndex = 0; frameIndex < _frames.Count; frameIndex++)
        {
            var frame = _frames[frameIndex];
            var r = Screen(frame.Bounds);
            if (!r.IntersectsWith(viewport)) continue;
            if (r.Width > 25 && r.Height > 22)
            {
                if (_hasRecipeLayout) DrawPolygon(context, _overviewFrames[frameIndex].Select(Screen).ToArray(), null, new Pen(line, 1));
                else context.DrawRoundedRectangle(null, new Pen(line, .7), r, 5, 5);
            }
            if (!_hasRecipeLayout && r.Height > 48 && r.Width > 95)
            {
                var all = _cellHeaders[frameIndex].AllCells;
                var extents = r.Width > (all.IsEmpty ? 195 : 245);
                CManualInspectionDrawing.Text(context, this, frame.Title, new Point(r.Left + 6, r.Top + 2), 11, Foreground, true,
                    Math.Max(1, r.Width - (all.IsEmpty ? 12 : extents ? 164 : 88)), singleLine: true);
                if (extents) CManualInspectionDrawing.Text(context, this, frame.Extents,
                    new Point(r.Right - (all.IsEmpty ? 94 : 154), r.Top + 3), 9, Foreground, false, all.IsEmpty ? 88 : 68, singleLine: true);
                if (!all.IsEmpty)
                {
                    var button = Screen(all);
                    context.DrawRoundedRectangle(CManualInspectionDrawing.Resource(this, "PanelBackgroundBrush", Background ?? Brushes.Transparent), new Pen(accent, .8), button, 3, 3);
                    CManualInspectionDrawing.Text(context, this, "전체 Cell", new Point(button.Left + 6, button.Top + 1), 10, Foreground, true, button.Width - 8, singleLine: true);
                }
            }
        }
        foreach (var slot in _slots)
        {
            if (!_visibleKeys.Contains(slot.Point.Key)) continue;
            var r = Screen(slot.Bounds);
            if (!r.IntersectsWith(viewport)) continue;
            var p = _pointByKey.GetValueOrDefault(slot.Point.Key) ?? slot.Point;
            // Most Recipe cells are axis aligned. Keep their 32k+ tile draws on the cheap rectangle
            // path; only genuinely rotated cells need per-slot polygon geometry and hit testing.
            var polygon = _overviewPolygons.TryGetValue(p.Key, out var sourcePolygon) && !AxisAligned(sourcePolygon)
                ? sourcePolygon.Select(Screen).ToArray() : null;
            var minimum = Math.Min(r.Width, r.Height);
            if (polygon is not null) minimum = Math.Min((polygon[1] - polygon[0]).Length, (polygon[3] - polygon[0]).Length);
            var selected = _selected.Contains(p.Key); var hover = p.Key == _hoverKey; var focused = p.Key == _focusKey;
            var brush = CManualInspectionDrawing.PointBrush(this, p, _colorMode, _snapshot);
            // Screen-space gutters stay small at any zoom. Subpixel overview slots reduce the
            // gutter proportionally, so a large dataset still has a visibly filled heat-map area.
            var gap = Math.Min(1.5, minimum * .04);
            var tile = new Rect(r.Left + gap / 2, r.Top + gap / 2, Math.Max(.001, r.Width - gap), Math.Max(.001, r.Height - gap));
            var textBrush = TileTextBrush(brush);
            Point[]? filledPolygon = polygon is null ? null : InsetPolygon(polygon, gap / 2);
            if (filledPolygon is not null)
            { DrawPolygon(context, filledPolygon, brush, null); context.PushClip(PolygonGeometry(filledPolygon)); }
            else context.DrawRectangle(brush, null, tile);
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
                if (filledPolygon is not null)
                {
                    DrawPolygon(context, InsetPolygon(filledPolygon, 1.4), null, new Pen(textBrush, selected ? 3.8 : 2.2));
                    DrawPolygon(context, InsetPolygon(filledPolygon, 1.4), null, new Pen(accent, selected ? 1.8 : 1));
                }
                else { context.DrawRectangle(null, new Pen(textBrush, selected ? 3.8 : 2.2), outline); context.DrawRectangle(null, new Pen(accent, selected ? 1.8 : 1), outline); }
            }
            if (focused && minimum > 12)
            {
                var focusPen = new Pen(textBrush, 1.2) { DashStyle = DashStyles.Dot };
                if (filledPolygon is not null) DrawPolygon(context, InsetPolygon(filledPolygon, 4.5), null, focusPen);
                else context.DrawRectangle(null, focusPen, Inset(tile, 4.5));
            }
            if (p.Key == _representative && minimum > 12)
            {
                if (filledPolygon is not null)
                {
                    DrawPolygon(context, InsetPolygon(filledPolygon, 3), null, new Pen(textBrush, 3.8));
                    DrawPolygon(context, InsetPolygon(filledPolygon, 3), null, new Pen(CManualInspectionDrawing.Color("#D18B00"), 2));
                }
                else { context.DrawRectangle(null, new Pen(textBrush, 3.8), Inset(tile, 3)); context.DrawRectangle(null, new Pen(CManualInspectionDrawing.Color("#D18B00"), 2), Inset(tile, 3)); }
            }
            if (r.Width > 28 && r.Height > 18 && (polygon is null || minimum > 18))
            {
                // Point identity stays readable; provenance is plain Korean in the tooltip rather
                // than unexplained square/diamond/circle glyphs repeated in every matrix slot.
                var state = p.Key == _representative ? "대표" : p.IsMasked ? "제외" : p.WorkingJudge == "NG" ? "NG"
                    : p.WorkingJudge == "ERROR" ? "오류" : p.IsMeasurementOffsetInitialized ? "초기화" : "";
                var label = "S" + p.ShotNo + (r.Width > 115 ? $" / B{p.BeamNo}" : "")
                    + (r.Width > 75 && state.Length > 0 ? " · " + state : "");
                var textWidth = Math.Max(1, polygon is null ? tile.Width - 10 : Math.Min(tile.Width - 10, minimum * .7));
                var textX = polygon is null ? tile.Left + 5 : polygon.Average(point => point.X) - textWidth / 2;
                CManualInspectionDrawing.Text(context, this, label, new Point(textX, tile.Top + Math.Max(2, tile.Height / 2 - 7)),
                    Math.Clamp(tile.Height / 3.3, 9, 12), textBrush, selected || focused, textWidth, singleLine: true);
            }
            else if (minimum > 12 && r.Width > 18 && p.WorkingJudge == "NG")
                CManualInspectionDrawing.Text(context, this, "NG", new Point(tile.Left + 2, tile.Top + 1), 9, textBrush, true);
            if (filledPolygon is not null) context.Pop();
            AddHit(p, r, viewport, polygon);
        }
        if (_hasRecipeLayout)
        {
            for (var i = 0; i < _cellHeaders.Count; i++)
            {
                var header = _cellHeaders[i];
                var r = Screen(header.Header); if (!r.IntersectsWith(viewport) || r.Width < 15 || r.Height < 6) continue;
                context.DrawRectangle(CManualInspectionDrawing.Resource(this, "PanelBackgroundBrush", Background ?? Brushes.Transparent), new Pen(accent, .7), r);
                CManualInspectionDrawing.Text(context, this, _frames[i].Title + " ▸", new Point(r.Left + 3, r.Top + 1), Math.Min(11, Math.Max(7, r.Height - 3)), Foreground, true, Math.Max(1, r.Width - 5), singleLine: true);
            }
        }
        if (!_allCellsButton.IsEmpty)
        {
            context.DrawRoundedRectangle(CManualInspectionDrawing.Resource(this, "PanelBackgroundBrush", Background ?? Brushes.Transparent), new Pen(accent, .8), _allCellsButton, 3, 3);
            CManualInspectionDrawing.Text(context, this, "전체 Cell", new Point(_allCellsButton.Left + 9, _allCellsButton.Top + 1), 10, Foreground, true, 67, singleLine: true);
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
    private static Point[] InsetPolygon(Point[] polygon, double inset)
    {
        var a = polygon[0]; var u = polygon[1] - a; var v = polygon[3] - a;
        var du = Math.Min(.49, Math.Max(0, inset / Math.Max(.000001, u.Length)));
        var dv = Math.Min(.49, Math.Max(0, inset / Math.Max(.000001, v.Length)));
        return [a + u * du + v * dv, a + u * (1 - du) + v * dv,
            a + u * (1 - du) + v * (1 - dv), a + u * du + v * (1 - dv)];
    }
    private static StreamGeometry PolygonGeometry(Point[] polygon)
    {
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        { drawing.BeginFigure(polygon[0], true, true); for (var i = 1; i < polygon.Length; i++) drawing.LineTo(polygon[i], true, false); }
        geometry.Freeze(); return geometry;
    }
    private static void DrawPolygon(DrawingContext context, Point[] polygon, Brush? fill, Pen? outline)
    {
        if (AxisAligned(polygon))
            context.DrawRectangle(fill, outline, Bounds(polygon));
        else context.DrawGeometry(fill, outline, PolygonGeometry(polygon));
    }
    private static bool AxisAligned(Point[] polygon) => Math.Abs(polygon[0].Y - polygon[1].Y) < .000001 && Math.Abs(polygon[0].X - polygon[3].X) < .000001
        || Math.Abs(polygon[0].X - polygon[1].X) < .000001 && Math.Abs(polygon[0].Y - polygon[3].Y) < .000001;
    private static bool ContainsPolygon(Point[] polygon, Point at)
    {
        var positive = false; var negative = false;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
            var cross = (b.X - a.X) * (at.Y - a.Y) - (b.Y - a.Y) * (at.X - a.X);
            positive |= cross > .000001; negative |= cross < -.000001;
        }
        return !(positive && negative);
    }
    private static Brush TileTextBrush(Brush fill)
    {
        if (fill is not SolidColorBrush solid) return Brushes.Black;
        static double Linear(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        var luminance = .2126 * Linear(solid.Color.R) + .7152 * Linear(solid.Color.G) + .0722 * Linear(solid.Color.B);
        return (luminance + .05) / .05 >= 1.05 / (luminance + .05) ? Brushes.Black : Brushes.White;
    }
    private void AddHit(CManualInspectionUiPoint point, Rect rect, Rect viewport, Point[]? polygon = null)
    {
        var index = _hits.Count; _hits.Add((point, rect));
        _hitPolygons.Add(polygon);
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
        if (HitAllCells(point)) return null;
        if (!_hitBuckets.TryGetValue(((int)(point.X / 32), (int)(point.Y / 32)), out var bucket)) return null;
        for (var i = bucket.Count - 1; i >= 0; i--)
        {
            var index = bucket[i];
            if (_hits[index].Bounds.Contains(point) && (_hitPolygons[index] is not { } polygon || ContainsPolygon(polygon, point))) return _hits[index].Point;
        }
        return null;
    }

    private int? HitCell(Point at)
    {
        for (var i = _cellHeaders.Count - 1; i >= 0; i--) if (Screen(_cellHeaders[i].Header).Contains(at)) return _cellHeaders[i].CellNo;
        return null;
    }
    private int? HitOverviewCell(Point at)
    {
        if(!_showCellOverview || !double.IsFinite(at.X) || !double.IsFinite(at.Y)
            || at.X<0 || at.Y<0 || at.X>ActualWidth || at.Y>ActualHeight) return null;
        BuildLayout();
        // The labelled Cell header remains a button even for a rotated Cell whose axis-aligned
        // label may extend beyond the polygon. Everywhere else must hit the actual Cell shape.
        if(HitCell(at) is int headerCell) return headerCell;
        var modelAt=new Point((at.X-_pan.X)/_zoom,(at.Y-_pan.Y)/_zoom);
        for(var i=_frames.Count-1;i>=0;i--)
        {
            var inside=_hasRecipeLayout
                ?ContainsPolygon(_overviewFrames[i],modelAt)
                :_frames[i].Bounds.Contains(modelAt);
            if(inside) return _cellHeaders[i].CellNo;
        }
        return null;
    }
    private bool HitAllCells(Point at) => !_allCellsButton.IsEmpty && _allCellsButton.Contains(at)
        || _cellHeaders.Any(header => !header.AllCells.IsEmpty && Screen(header.AllCells).Contains(at));
    private bool ActivateCellHeader(Point at, bool control)
    {
        if (HitAllCells(at))
        { CancelGesture(false); AllCellsRequested?.Invoke(this, EventArgs.Empty); return true; }
        var cell = HitCell(at); if (cell is null) return false;
        CancelGesture(false);
        if (_showCellOverview || !control) CellRequested?.Invoke(this, cell.Value);
        else
        {
            var enabled = _snapshot.CellLayouts.FirstOrDefault(layout => layout.CellNo == cell.Value)?.Enabled != false;
            var points = _snapshot.Points.Where(point => point.CellNo == cell.Value && !point.IsMasked && enabled).ToArray();
            if (points.Length > 0) SelectionRequested?.Invoke(this, new(points.Select(point => point.Key).ToArray(), points[0].Key,
                additive: true, toggle: points.All(point => _selected.Contains(point.Key))));
            else MessageRequested?.Invoke($"Cell {cell.Value}에는 선택할 측정 대상이 없습니다.");
        }
        return true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus(); _tip.IsOpen = false;
        BeginPointerGesture(e.GetPosition(this),Keyboard.Modifiers,e.ClickCount);
        if(_gesture) CaptureMouse();
        e.Handled=true;
    }

    private void BeginPointerGesture(Point at,ModifierKeys modifiers,int clickCount)
    {
        if(!double.IsFinite(at.X) || !double.IsFinite(at.Y)) return;
        if(_gesture) CancelGesture(true);
        if(TryResetOnDoubleClick(clickCount)) return;
        var pan=IsPanTool || modifiers.HasFlag(ModifierKeys.Alt);
        // Defer overview navigation until release. A header/body press can become a rectangle
        // selection, and Ctrl changes drag additivity rather than the meaning of a Cell click.
        if(!pan && !_showCellOverview && ActivateCellHeader(at,modifiers.HasFlag(ModifierKeys.Control))) return;
        RememberSelectionBeforeClick();
        _gesture = true; _moved = false;
        _panning = pan;
        if (_panning) InvalidateClickSelection();
        _additive = modifiers.HasFlag(ModifierKeys.Control);
        _overviewClickCell=!pan?HitOverviewCell(at):null;
        _start = _last = at; _startPan = _pan;
        Cursor = _panning ? Cursors.ScrollAll : Cursors.Cross;
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
            UpdatePointerGesture(at);e.Handled = true; return;
        }
        var overviewCell=HitOverviewCell(at);
        if (overviewCell.HasValue || HitCell(at).HasValue || HitAllCells(at))
        {
            _hoverKey = null; Cursor = IsPanTool || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)?Cursors.ScrollAll:Cursors.Hand;
            var cell = overviewCell ?? HitCell(at);
            var disabled = _snapshot.CellLayouts.FirstOrDefault(layout => layout.CellNo == cell)?.Enabled == false;
            _tip.Content = HitAllCells(at) ? "전체 Cell의 Recipe 배치 보기" : $"Cell {cell}" + (disabled ? " · 미사용 Cell · 활성 측정점 없음" : _showCellOverview?" 열기 · 드래그: 측정점 선택 · Ctrl+드래그: 추가 선택":" 열기 · Ctrl+클릭: Cell 전체 측정점 선택 추가/해제");
            _tip.IsOpen = true; InvalidateVisual(); return;
        }
        Cursor = IsPanTool ? Cursors.ScrollAll : Cursors.Cross;
        var point = Hit(at);
        if (_hoverKey == point?.Key) return;
        _hoverKey = point?.Key;
        InvalidateVisual();
        _tip.SetResourceReference(Control.BackgroundProperty, "PreviewHoverTipBackgroundBrush");
        _tip.SetResourceReference(Control.ForegroundProperty, "PreviewHoverTipTextBrush");
        _tip.Content = point is null ? null : $"{point.Identity}\n{point.MeasurementStatusText} · 편차 {point.MeasurementErrorText} µm\n출처: {CManualInspectionDrawing.MeasurementOrigin(point)}"
            + (point.IsMasked ? "\nMasking · 측정 제외" : point.Key == _representative ? "\n대표값으로 선택됨" : "");
        _tip.IsOpen = point is not null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if(CompletePointerGesture(e.GetPosition(this))) e.Handled=true;
    }

    private void UpdatePointerGesture(Point at)
    {
        if(!_gesture || !double.IsFinite(at.X) || !double.IsFinite(at.Y)) return;
        _last=at;_moved|=(at-_start).Length>4;
        if(_moved) InvalidateClickSelection();
        if(_panning) _pan=_startPan+at-_start;
        InvalidateVisual();
    }

    private bool CompletePointerGesture(Point end)
    {
        if (!_gesture) return false;
        if(!double.IsFinite(end.X) || !double.IsFinite(end.Y)) { CancelGesture(true);return true; }
        UpdatePointerGesture(end); // A fast drag may release before WPF delivers the next move event.
        var panning = _panning; var moved = _moved; var additive = _additive;
        var overviewCell=_overviewClickCell;_overviewClickCell=null;_gesture = false;
        ReleaseMouseCapture(); Cursor = IsPanTool ? Cursors.ScrollAll : Cursors.Cross;
        CompleteClickSelectionTracking(panning, moved);
        if (!panning)
        {
            if (moved)
            {
                SelectRectangle(new Rect(_start, end), additive);
            }
            else if(overviewCell is int cell)
            {
                if(HitOverviewCell(end)==cell) CellRequested?.Invoke(this,cell);
            }
            else if(!_showCellOverview)
            {
                var point = Hit(end);
                if (point?.IsMasked == true) MessageRequested?.Invoke("Masking 위치는 측정 대상으로 선택하지 않습니다.");
                else if (point is not null) SelectionRequested?.Invoke(this, new([point.Key], point.Key, additive, additive));
                else if (!additive) SelectionRequested?.Invoke(this, new([], null));
            }
        }
        InvalidateVisual();return true;
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
        CancelGesture(false);
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
        if (ContextMenu?.IsOpen == true) return;
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
        menu.ToolTip = "숨김 선택도 대상에 포함됩니다. 이동 동작은 대상 확인 후 실행합니다.";
        menu.Items.Add(new Separator());
        foreach (var (title, action) in new[]
        {
            ("이동만", EN_MANUAL_INSPECTION_UI_ACTION.Move),
            ("이동 후 측정", EN_MANUAL_INSPECTION_UI_ACTION.MoveMeasure)
        })
        {
            var item = new MenuItem { Header = title, IsEnabled = !_snapshot.IsBusy && !_snapshot.IsOffsetEditPending,
                ToolTip = _snapshot.IsOffsetEditPending ? "통합 Offset을 모델에 저장하거나 편집을 취소한 뒤 이동·측정하세요." : title };
            ToolTipService.SetShowOnDisabled(item, true);
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
                IsEnabled = !_snapshot.IsBusy && (action == EN_MANUAL_INSPECTION_UI_ACTION.ShowPoint || !_snapshot.IsOffsetEditPending),
                ToolTip = action == EN_MANUAL_INSPECTION_UI_ACTION.Measure && _snapshot.IsOffsetEditPending
                    ? "통합 Offset을 모델에 저장하거나 편집을 취소한 뒤 측정하세요." : title };
            ToolTipService.SetShowOnDisabled(item, true);
            item.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            item.Click += (_, _) => OperationRequested?.Invoke(this, new(action, [focus.Key], version));
            menu.Items.Add(item);
        }
        ContextMenu = menu; menu.IsOpen = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HandleMapKey(key, Keyboard.Modifiers, e.IsRepeat))
        { e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }

    private bool HandleMapKey(Key key, ModifierKeys modifiers, bool isRepeat)
    {
        // A ContextMenu has its own keyboard focus/navigation. Never replace it or consume its
        // arrow keys while open; Escape closes only this popup, retaining the point selection.
        if (ContextMenu?.IsOpen == true)
            return key == Key.Escape && TryCloseOperationMenu();
        if (modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var step = modifiers.HasFlag(ModifierKeys.Shift) ? 100 : 40;
            PanBy(key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), _ => new Vector(0, step) });
            return true;
        }
        if (key is Key.Left or Key.Right or Key.Up or Key.Down)
        { SelectDirectionalPoint(key, modifiers.HasFlag(ModifierKeys.Control)); return true; }
        if (key == Key.Space || key == Key.Apps || key == Key.F10 && modifiers.HasFlag(ModifierKeys.Shift))
        { if (!isRepeat) ShowOperationMenu(); return true; }
        if (key == Key.Escape && _gesture) { CancelGesture(true); return true; }
        return false;
    }

    private bool TryCloseOperationMenu()
    {
        if (ContextMenu?.IsOpen != true) return false;
        ContextMenu.IsOpen = false;
        Focus();
        return true;
    }

    private void SelectDirectionalPoint(Key direction, bool additive)
    {
        if (_gesture) CancelGesture(false);
        InvalidateClickSelection(); _tip.IsOpen = false;
        BuildLayout();
        var disabledCells = _snapshot.CellLayouts.Where(cell => !cell.Enabled).Select(cell => cell.CellNo).ToHashSet();
        // Slot geometry is deliberately cached across status refreshes. Consult the latest
        // point object for Masking/eligibility rather than the object retained by that slot.
        var eligible = _slots.Where(slot => _visibleKeys.Contains(slot.Point.Key)
            && _pointByKey.TryGetValue(slot.Point.Key, out var point)
            && !point.IsMasked && !disabledCells.Contains(point.CellNo)).ToArray();
        if (eligible.Length == 0) return;
        static Point Centre(Slot slot) => new(slot.Bounds.Left + slot.Bounds.Width / 2, slot.Bounds.Top + slot.Bounds.Height / 2);
        var current = eligible.FirstOrDefault(slot => slot.Point.Key == _focusKey);
        Slot? next;
        if (current is null)
            next = eligible.OrderBy(slot => Centre(slot).Y).ThenBy(slot => Centre(slot).X)
                .ThenBy(slot => slot.Point.Key, StringComparer.Ordinal).First();
        else
        {
            var origin = Centre(current);
            var candidates = eligible.Where(slot => slot.Point.Key != current.Point.Key).Select(slot =>
            {
                var delta = Centre(slot) - origin;
                var forward = direction switch { Key.Left => -delta.X, Key.Right => delta.X, Key.Up => -delta.Y, _ => delta.Y };
                var cross = Math.Abs(direction is Key.Left or Key.Right ? delta.Y : delta.X);
                return (Slot: slot, Forward: forward, Cross: cross);
            }).Where(candidate => candidate.Forward > 1e-7).ToArray();
            // Favor neighbours within the requested 90-degree cone, then distance/alignment.
            // This follows displayed geometry across Cell/Head borders without wrapping rows or
            // relying on Cell numbers, which need not match the physical Recipe arrangement.
            next = candidates.OrderBy(candidate => candidate.Cross <= candidate.Forward ? 0 : 1)
                .ThenBy(candidate => Math.Sqrt(candidate.Forward * candidate.Forward + candidate.Cross * candidate.Cross)
                    * (1 + 2 * candidate.Cross / candidate.Forward))
                .ThenBy(candidate => candidate.Slot.Point.Key, StringComparer.Ordinal).Select(candidate => candidate.Slot).FirstOrDefault();
        }
        if (next is null) return;
        SelectionRequested?.Invoke(this, new([next.Point.Key], next.Point.Key, additive));
        RevealPoint(next.Point.Key);
    }

    private void RevealPoint(string key)
    {
        BuildLayout();
        var slot = _slots.FirstOrDefault(item => item.Point.Key == key);
        if (slot is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var rect = Screen(slot.Bounds);
        const double margin = 12;
        var dx = rect.Width > ActualWidth - margin * 2 ? ActualWidth / 2 - (rect.Left + rect.Width / 2)
            : rect.Left < margin ? margin - rect.Left : rect.Right > ActualWidth - margin ? ActualWidth - margin - rect.Right : 0;
        var dy = rect.Height > ActualHeight - margin * 2 ? ActualHeight / 2 - (rect.Top + rect.Height / 2)
            : rect.Top < margin ? margin - rect.Top : rect.Bottom > ActualHeight - margin ? ActualHeight - margin - rect.Bottom : 0;
        if (dx == 0 && dy == 0) return;
        PanBy(new(dx, dy));
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelGesture(bool restorePan)
    {
        InvalidateClickSelection();
        if (restorePan && _gesture && _panning) _pan = _startPan;
        _gesture = false;
        _panning = false; _moved = false;_overviewClickCell=null;
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
        if (point.IsMeasurementOffsetInitialized || !point.HasMeasurementHistory) return -1;
        return CManualInspectionLevels.Evaluate(point.MeasurementXUm, point.MeasurementYUm, snapshot.LevelMetric, snapshot.LevelBoundsUm) ?? -1;
    }
    internal static int AppliedLevel(CManualInspectionUiPoint point, CManualInspectionUiSnapshot snapshot) => point.IsMasked || point.IsMeasurementOffsetInitialized || !point.HasAppliedOffset ? -1
        : CManualInspectionLevels.Evaluate(point.BaseXUm, point.BaseYUm, snapshot.LevelMetric, snapshot.LevelBoundsUm) ?? -1;
    internal static Brush PointBrush(FrameworkElement owner, CManualInspectionUiPoint point, string mode, CManualInspectionUiSnapshot snapshot)
    {
        if (point.IsMasked) return Resource(owner, "ReviewMaskBackgroundBrush", Color("#EFF2F5"));
        // A failed measurement must never look successful merely because its numeric error is small.
        if (point.WorkingJudge == "NG") return Color("#D16776");
        if (point.WorkingJudge == "ERROR") return Color("#D39A45");
        if (point.IsMeasurementOffsetInitialized) return Color("#DBE4EA");
        if (mode == "applied") return LevelBrush(AppliedLevel(point, snapshot));
        if (mode == "head") return Color(HeadColors[Math.Max(0, point.HeadNo - 1) % HeadColors.Length]);
        if (mode == "level") return LevelBrush(Level(point, snapshot));
        if (mode == "source") return point.Source switch { "MEASURED" => Color("#3B9870"), "IMPORTED" => Color("#7899A8"), "INTERPOLATED" => Color("#77A6CD"), "MANUAL" => Color("#A482BE"), _ => Color("#DBE4EA") };
        return point.UsesAppliedMeasurement ? Color("#638CA8")
            : point.WorkingJudge switch { "OK" => Color("#3B9870"), "NG" => Color("#D16776"), "ERROR" => Color("#D39A45"), _ => Color("#DBE4EA") };
    }
    internal static string MeasurementOrigin(CManualInspectionUiPoint point) => point.IsMasked ? "사용 제외"
        : point.IsMeasurementOffsetInitialized ? "측정 보정 초기화 · 새 측정 전"
        : point.IsProxy ? "대표값 복사 · " + point.ReferenceKey
        : point.UsesAppliedMeasurement ? "기존 측정 보정 기반"
        : point.Source == "IMPORTED" ? "불러온 측정 결과"
        : point.Source == "MEASURED" ? "실측"
        : "측정 전";
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
        double size, Brush? color, bool bold = false, double maxWidth = double.PositiveInfinity, bool singleLine = false)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, color ?? Brushes.Gray, VisualTreeHelper.GetDpi(owner).PixelsPerDip);
        if (double.IsFinite(maxWidth)) { ft.MaxTextWidth = Math.Max(1, maxWidth); ft.Trimming = TextTrimming.CharacterEllipsis; }
        if (singleLine) ft.MaxLineCount = 1;
        drawing.DrawText(ft, origin);
    }
}

/// <summary>One accessible surface peer, without allocating a WPF/UIA object per measured point.</summary>
internal sealed class CManualInspectionSurfacePeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
{
    protected override string GetClassNameCore() => Owner.GetType().Name;
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
}
