namespace Drilling.UI.Views;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

public sealed class CManualInspectionSelectionEventArgs(
    IReadOnlyList<string> keys, string? focusKey, bool additive = false, bool toggle = false) : EventArgs
{
    public IReadOnlyList<string> Keys { get; } = keys;
    public string? FocusKey { get; } = focusKey;
    public bool Additive { get; } = additive;
    public bool Toggle { get; } = toggle;
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
    private readonly List<(Point Origin, string Text, double Height)> _labels = [];
    private CManualInspectionUiSnapshot _snapshot = new();
    private string? _representative;
    private string? _focusKey;
    private string _colorMode = "judge";
    private string _layoutSignature = "";
    private bool _layoutDirty = true;
    private bool _gesture;
    private bool _panning;
    private bool _additive;
    private bool _moved;
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
        MinHeight = 330;
        Cursor = Cursors.Cross;
        SetResourceReference(BackgroundProperty, "GridBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(BorderBrushProperty, "GridLineBrush");
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 180);
        ToolTipService.SetShowDuration(this, 15000);
        SizeChanged += (_, _) => { _layoutDirty = true; InvalidateVisual(); };
        Unloaded += (_, _) => { CancelGesture(true); _tip.IsOpen = false; if (ContextMenu is not null) ContextMenu.IsOpen = false; };
    }

    public void Configure(CManualInspectionUiSnapshot snapshot,
        IReadOnlyList<CManualInspectionUiPoint> allScopedPoints,
        IReadOnlyList<CManualInspectionUiPoint> visiblePoints, IEnumerable<string> selectedKeys,
        string? focusKey, string? representativeKey, string colorMode, string scopeSignature)
    {
        if (ContextMenu?.IsOpen == true) ContextMenu.IsOpen = false;
        _snapshot = snapshot;
        _all = allScopedPoints;
        _visible = visiblePoints;
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
        _layoutDirty = true;
        InvalidateVisual();
    }

    public void ResetView()
    {
        _zoom = 1; _pan = default;
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void PanBy(Vector delta) { _pan += delta; InvalidateVisual(); }
    public bool TryCancelGesture()
    {
        if (!_gesture) return false;
        CancelGesture(true); return true;
    }

    public void ZoomAt(double factor, Point origin)
    {
        var old = _zoom;
        _zoom = Math.Clamp(old * factor, 0.4, 12);
        _pan = new Vector(origin.X - (origin.X - _pan.X) * _zoom / old,
            origin.Y - (origin.Y - _pan.Y) * _zoom / old);
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void BuildLayout()
    {
        if (!_layoutDirty) return;
        _layoutDirty = false;
        _slots.Clear(); _labels.Clear();
        var width = Math.Max(80, ActualWidth);
        var height = Math.Max(80, ActualHeight);
        var groups = _all.GroupBy(p => (p.CellNo, p.HeadNo))
            .OrderBy(g => g.Key.CellNo).ThenBy(g => g.Key.HeadNo).ToArray();
        if (groups.Length == 0) return;
        var columns = Math.Min(groups.Length, width > 750 ? 4 : width > 430 ? 2 : 1);
        var rows = Math.Max(1, (int)Math.Ceiling((double)groups.Length / columns));
        const double margin = 20;
        // A full 6GH dataset may contain hundreds of Cell/Head groups. Scale the
        // gutters and heading bands too, so Fit does not silently crop lower cells.
        var gap = Math.Min(17, Math.Max(0.1, height / Math.Max(1, rows) / 8));
        var groupWidth = Math.Max(2, (width - margin * 2 - gap * (columns - 1)) / columns);
        var groupHeight = Math.Max(0.1, (height - margin * 2 - gap * (rows - 1)) / rows);
        var title = Math.Min(22, groupHeight * 0.2);
        var visible = _visible.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var gx = margin + i % columns * (groupWidth + gap);
            var gy = margin + i / columns * (groupHeight + gap);
            var columnKeys = group.Select(p => p.Column).Distinct().OrderBy(x => x).ToArray();
            var rowKeys = group.Select(p => p.Row).Distinct().OrderBy(x => x).ToArray();
            var columnMap = columnKeys.Select((key, index) => (key, index)).ToDictionary(x => x.key, x => x.index);
            var rowMap = rowKeys.Select((key, index) => (key, index)).ToDictionary(x => x.key, x => x.index);
            var tileWidth = groupWidth / Math.Max(1, columnKeys.Length);
            var tileHeight = Math.Max(0.01, groupHeight - title) / Math.Max(1, rowKeys.Length);
            _labels.Add((new Point(gx, gy), $"Cell {group.Key.CellNo} · H{group.Key.HeadNo:00}", groupHeight));
            foreach (var bucket in group.GroupBy(p => (p.Row, p.Column)))
            {
                var points = bucket.OrderBy(p => p.BeamNo).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
                var subColumns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(points.Length)));
                var subRows = Math.Max(1, (int)Math.Ceiling((double)points.Length / subColumns));
                for (var j = 0; j < points.Length; j++)
                {
                    var p = points[j];
                    if (!visible.Contains(p.Key)) continue;
                    _slots.Add(new Slot(p, new Rect(gx + columnMap[p.Column] * tileWidth + j % subColumns * tileWidth / subColumns,
                        gy + title + rowMap[p.Row] * tileHeight + j / subColumns * tileHeight / subRows,
                        tileWidth / subColumns, tileHeight / subRows)));
                }
            }
        }
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        BuildLayout(); _hits.Clear();
        context.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        context.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        foreach (var label in _labels)
        {
            var p = Screen(label.Origin);
            if (label.Height * _zoom > 26 && p.Y >= -20 && p.Y <= ActualHeight + 10)
                CManualInspectionDrawing.Text(context, this, label.Text, p, 11, Foreground, true);
        }
        var viewport = new Rect(0, 0, ActualWidth, ActualHeight);
        foreach (var slot in _slots)
        {
            var r = new Rect(Screen(slot.Bounds.TopLeft), new Size(slot.Bounds.Width * _zoom, slot.Bounds.Height * _zoom));
            if (!r.IntersectsWith(viewport)) continue;
            var p = slot.Point;
            var gap = Math.Clamp(Math.Min(r.Width, r.Height) * 0.1, 0.6, 3);
            var tile = new Rect(r.X + gap / 2, r.Y + gap / 2, Math.Max(0.5, r.Width - gap), Math.Max(0.5, r.Height - gap));
            context.DrawRectangle(CManualInspectionDrawing.PointBrush(this, p, _colorMode, _snapshot), null, tile);
            if (p.IsMasked && r.Width > 6 && r.Height > 6)
            {
                var pen = new Pen(BorderBrush ?? Brushes.Gray, 1);
                context.DrawLine(pen, new Point(r.Left + 2, r.Top + 2), new Point(r.Right - 2, r.Bottom - 2));
                context.DrawLine(pen, new Point(r.Right - 2, r.Top + 2), new Point(r.Left + 2, r.Bottom - 2));
            }
            if (_selected.Contains(p.Key) && r.Width > 3 && r.Height > 3)
            {
                var outline = new Rect(r.Left + 1, r.Top + 1, Math.Max(0.2, r.Width - 2), Math.Max(0.2, r.Height - 2));
                context.DrawRectangle(null, new Pen(Brushes.White, 3.5), outline);
                context.DrawRectangle(null, new Pen(CManualInspectionDrawing.Resource(this, "AccentBrush", Brushes.SteelBlue), 1.8), outline);
            }
            if (p.Key == _representative && r.Width > 6 && r.Height > 6)
                context.DrawRectangle(null, new Pen(CManualInspectionDrawing.Color("#D18B00"), 2.8),
                    new Rect(r.Left + 3, r.Top + 3, Math.Max(0.5, r.Width - 6), Math.Max(0.5, r.Height - 6)));
            if (r.Width > 22 && r.Height > 19)
            {
                var mark = p.Key == _representative ? "★ " : p.IsProxy ? "◇ " : p.WorkingJudge == "ERROR" ? "! " : p.WorkingJudge == "UNMEASURED" ? "· " : "";
                var label = mark + (r.Width > 50 ? "S" : "") + p.ShotNo + (r.Width > 95 ? $" / B{p.BeamNo}" : "");
                CManualInspectionDrawing.Text(context, this, label, new Point(r.Left + 4, r.Top + Math.Max(1, r.Height / 2 - 7)),
                    Math.Clamp(r.Height / 3, 9, 12), CManualInspectionDrawing.Color("#153E4E"), false, Math.Max(1, r.Width - 7));
            }
            _hits.Add((p, r));
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
    private CManualInspectionUiPoint? Hit(Point point) => _hits.LastOrDefault(x => x.Bounds.Contains(point)).Point;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus(); _tip.IsOpen = false;
        _gesture = true; _moved = false;
        _panning = IsPanTool || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        _additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        _start = _last = e.GetPosition(this); _startPan = _pan;
        Cursor = _panning ? Cursors.ScrollAll : Cursors.Cross;
        CaptureMouse(); e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = e.GetPosition(this);
        if (_gesture)
        {
            _last = at; _moved |= (at - _start).Length > 4;
            if (_panning) _pan = _startPan + at - _start;
            InvalidateVisual(); e.Handled = true; return;
        }
        var point = Hit(at);
        if (_hoverKey == point?.Key) return;
        _hoverKey = point?.Key;
        _tip.SetResourceReference(Control.BackgroundProperty, "PreviewHoverTipBackgroundBrush");
        _tip.SetResourceReference(Control.ForegroundProperty, "PreviewHoverTipTextBrush");
        _tip.Content = point is null ? null : $"{point.Key}\n{point.WorkingJudge} · ΔX/Y {point.WorkingErrorText} µm" +
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
        if (!_gesture) { _tip.IsOpen = false; _hoverKey = null; }
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
        var menu = new ContextMenu { PlacementTarget = this };
        menu.SetResourceReference(BackgroundProperty, "PanelBackgroundBrush");
        menu.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        menu.SetResourceReference(BorderBrushProperty, "PanelBorderBrush");
        menu.Items.Add(new MenuItem { Header = $"선택 {keys.Length}점 · {_focusKey ?? keys[0]}", IsEnabled = false });
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
        if (restorePan && _gesture && _panning) _pan = _startPan;
        _gesture = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = IsPanTool ? Cursors.ScrollAll : Cursors.Cross;
        InvalidateVisual();
    }
}

internal static class CManualInspectionDrawing
{
    private static readonly Dictionary<string, Brush> BrushesByColor = new(StringComparer.Ordinal);
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
        if (point.IsMasked || !CManualInspectionUiPoint.Finite(point.WorkingXUm) || !CManualInspectionUiPoint.Finite(point.WorkingYUm)) return -1;
        var x = point.WorkingXUm!.Value; var y = point.WorkingYUm!.Value;
        var value = snapshot.LevelMetric == "RADIAL" ? Math.Sqrt(x * x + y * y) : Math.Max(Math.Abs(x), Math.Abs(y));
        for (var i = 0; i < Math.Min(3, snapshot.LevelBoundsUm.Count); i++) if (value <= snapshot.LevelBoundsUm[i]) return i;
        return 3;
    }
    internal static Brush PointBrush(FrameworkElement owner, CManualInspectionUiPoint point, string mode, CManualInspectionUiSnapshot snapshot)
    {
        if (point.IsMasked) return Resource(owner, "ReviewMaskBackgroundBrush", Color("#EFF2F5"));
        if (mode == "head") return Color(HeadColors[Math.Max(0, point.HeadNo - 1) % HeadColors.Length]);
        if (mode == "level") return Level(point, snapshot) switch { 0 => Color("#2E9076"), 1 => Color("#B88B2A"), 2 => Color("#CA754A"), 3 => Color("#BE4F68"), _ => Color("#DBE4EA") };
        if (mode == "source") return point.Source switch { "MEASURED" => Color("#3B9870"), "IMPORTED" => Color("#7899A8"), "INTERPOLATED" => Color("#77A6CD"), "MANUAL" => Color("#A482BE"), _ => Color("#DBE4EA") };
        return point.WorkingJudge switch { "OK" => Color("#3B9870"), "NG" => Color("#D16776"), "ERROR" => Color("#D39A45"), _ => Color("#DBE4EA") };
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
