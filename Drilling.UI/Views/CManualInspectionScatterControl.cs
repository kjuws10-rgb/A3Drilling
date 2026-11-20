namespace Drilling.UI.Views;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

public sealed record CManualInspectionScatterMetrics
{
    public int Total { get; init; }
    public int Count { get; init; }
    public int ProxyCount { get; init; }
    public int CorrectionBasedCount { get; init; }
    public int Masked { get; init; }
    public int Unmeasured { get; init; }
    public int Errors { get; init; }
    public int Invalid { get; init; }
    public double? MeanX { get; init; }
    public double? MeanY { get; init; }
    public double? SigmaX { get; init; }
    public double? SigmaY { get; init; }
    public double? MaximumVector { get; init; }
    public IReadOnlyList<CManualInspectionUiPoint> ValidPoints { get; init; } = [];

    public static CManualInspectionScatterMetrics Calculate(IEnumerable<CManualInspectionUiPoint> points)
    {
        var source = points.ToArray();
        var valid = new List<CManualInspectionUiPoint>();
        var masked = 0; var unmeasured = 0; var errors = 0; var invalid = 0;
        foreach (var p in source)
        {
            if (p.IsMasked) masked++;
            else if (p.HasMeasurementHistory)
            {
                if (CManualInspectionUiPoint.Finite(p.MeasurementXUm) && CManualInspectionUiPoint.Finite(p.MeasurementYUm)) valid.Add(p);
                else invalid++;
            }
            else if (p.WorkingJudge == "UNMEASURED") unmeasured++;
            else if (p.WorkingJudge == "ERROR") errors++;
            else invalid++;
        }
        double meanX = 0, meanY = 0, max = 0;
        for (var i = 0; i < valid.Count; i++)
        {
            var p = valid[i]; var n = i + 1.0;
            // Weighted means avoid overflowing the sum of otherwise finite imported values.
            meanX = meanX * (i / n) + p.MeasurementXUm!.Value / n;
            meanY = meanY * (i / n) + p.MeasurementYUm!.Value / n;
            var x = Math.Abs(p.MeasurementXUm.Value); var y = Math.Abs(p.MeasurementYUm.Value); var scale = Math.Max(x, y);
            max = Math.Max(max, scale == 0 ? 0 : scale * Math.Sqrt((x / scale) * (x / scale) + (y / scale) * (y / scale)));
        }
        // Scale before squaring: finite imported coordinates must not overflow their population
        // variance merely because (x - mean)^2 exceeds Double's intermediate range.
        var scaleX = valid.Select(p => Math.Abs(p.MeasurementXUm!.Value)).DefaultIfEmpty(0).Max();
        var scaleY = valid.Select(p => Math.Abs(p.MeasurementYUm!.Value)).DefaultIfEmpty(0).Max();
        double sx = 0, sy = 0;
        foreach (var p in valid)
        {
            if (scaleX > 0) sx += Math.Pow(p.MeasurementXUm!.Value / scaleX - meanX / scaleX, 2) / valid.Count;
            if (scaleY > 0) sy += Math.Pow(p.MeasurementYUm!.Value / scaleY - meanY / scaleY, 2) / valid.Count;
        }
        return new CManualInspectionScatterMetrics
        {
            Total = source.Length, Count = valid.Count, ProxyCount = valid.Count(p => p.IsProxy), CorrectionBasedCount = valid.Count(p => p.UsesAppliedMeasurement),
            Masked = masked, Unmeasured = unmeasured, Errors = errors, Invalid = invalid,
            MeanX = valid.Count == 0 ? null : meanX, MeanY = valid.Count == 0 ? null : meanY,
            SigmaX = valid.Count == 0 ? null : Math.Sqrt(sx) * scaleX, SigmaY = valid.Count == 0 ? null : Math.Sqrt(sy) * scaleY,
            MaximumVector = valid.Count == 0 ? null : max, ValidPoints = valid
        };
    }
}

/// <summary>Equal-scale display ΔX/ΔY: raw dots, representative diamonds and correction-derived squares.</summary>
public sealed class CManualInspectionScatterControl : Control
{
    private IReadOnlyList<CManualInspectionUiPoint> _points = [];
    private HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly List<(string Key, Point Point)> _hits = [];
    private readonly Dictionary<(int X, int Y), List<int>> _hitBuckets = [];
    private readonly Dictionary<string, CManualInspectionUiPoint> _pointByKey = new(StringComparer.Ordinal);
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
    private string? _hoverKey;
    private string _hoverSignature = "";
    private string _cycleCluster = "";
    private int _cycleIndex;
    private bool _byHead;
    private double _toleranceX = 5;
    private double _toleranceY = 5;
    private string _emptyText = "이 범위에 유효한 측정값이 없습니다.";
    public event EventHandler<CManualInspectionSelectionEventArgs>? SelectionRequested;
    public CManualInspectionScatterMetrics Metrics { get; private set; } = new();

    public CManualInspectionScatterControl()
    {
        Focusable = true; MinHeight = 270; ClipToBounds = true;
        SetResourceReference(BackgroundProperty, "GridBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(BorderBrushProperty, "GridLineBrush");
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 180);
        ToolTipService.SetShowDuration(this, 15000);
        AutomationProperties.SetName(this, "리뷰 측정 편차 산포도");
        AutomationProperties.SetHelpText(this, "가로 ΔX, 세로 ΔY 단위는 마이크로미터이며 두 축의 축척은 같습니다. 점선은 공차, 십자는 평균입니다. 겹친 점은 클릭으로 순환하고 Ctrl 클릭으로 같은 화면 위치의 점을 함께 선택합니다.");
        Unloaded += (_, _) => { _tip.IsOpen = false; _hoverKey = null; };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CManualInspectionSurfacePeer(this);

    public void Configure(IReadOnlyList<CManualInspectionUiPoint> points, IEnumerable<string> selectedKeys,
        bool byHead, double toleranceXUm, double toleranceYUm, string emptyText)
    {
        var samePoints = ReferenceEquals(_points, points) || _points.Count == points.Count
            && _points.Zip(points).All(pair => ReferenceEquals(pair.First, pair.Second));
        var changedScale = _toleranceX != toleranceXUm || _toleranceY != toleranceYUm || _byHead != byHead;
        _points = points; _selected = new HashSet<string>(selectedKeys, StringComparer.Ordinal);
        _byHead = byHead; _toleranceX = toleranceXUm; _toleranceY = toleranceYUm; _emptyText = emptyText;
        if (!samePoints)
        {
            Metrics = CManualInspectionScatterMetrics.Calculate(points);
            _pointByKey.Clear(); foreach (var point in points) _pointByKey[point.Key] = point;
        }
        if (!samePoints || changedScale)
        { _tip.IsOpen = false; _hoverSignature = ""; _hoverKey = null; _cycleCluster = ""; _cycleIndex = 0; }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); _hits.Clear(); _hitBuckets.Clear();
        dc.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 90 || ActualHeight < 90) return;
        var padLeft = 55.0; var padRight = 26.0; var padTop = 44.0; var padBottom = 52.0;
        var size = Math.Max(5, Math.Min(ActualWidth - padLeft - padRight, ActualHeight - padTop - padBottom));
        var cx = padLeft + (ActualWidth - padLeft - padRight) / 2;
        var cy = padTop + (ActualHeight - padTop - padBottom) / 2;
        var tolX = double.IsFinite(_toleranceX) ? Math.Abs(_toleranceX) : 0;
        var tolY = double.IsFinite(_toleranceY) ? Math.Abs(_toleranceY) : 0;
        var axis = Math.Max(5, Math.Max(tolX, tolY) * 1.3);
        foreach (var p in Metrics.ValidPoints) axis = Math.Max(axis, Math.Max(Math.Abs(p.MeasurementXUm!.Value), Math.Abs(p.MeasurementYUm!.Value)));
        if (!double.IsFinite(axis) || axis <= 0) axis = 5;
        var power = Math.Pow(10, Math.Floor(Math.Log10(axis)));
        var rounded = Math.Ceiling(axis / power * 2) / 2 * power;
        var limit = double.IsFinite(rounded) && rounded > 0 ? rounded : axis;
        var scale = size / limit / 2;
        var line = BorderBrush ?? Brushes.LightGray;
        var plot = new Rect(cx - size / 2, cy - size / 2, size, size);
        dc.DrawRectangle(null, new Pen(line, .8), plot);
        for (var k = -2; k <= 2; k++)
        {
            var v = limit * (k / 2.0); var x = cx + (k / 4.0) * size; var y = cy - (k / 4.0) * size;
            var pen = new Pen(k == 0 ? Foreground ?? line : line, k == 0 ? .9 : .5);
            dc.DrawLine(pen, new Point(x, cy - size / 2), new Point(x, cy + size / 2));
            dc.DrawLine(pen, new Point(cx - size / 2, y), new Point(cx + size / 2, y));
            var format = Math.Abs(limit) >= 10000 ? "0.##E+0" : limit < 2 || Math.Abs(limit - Math.Round(limit)) > 1e-8 ? "0.0" : "0";
            var text = v.ToString(format, CultureInfo.InvariantCulture);
            CManualInspectionDrawing.Text(dc, this, text, new Point(x - 13, cy + size / 2 + 6), 9, Foreground, false, 43);
            CManualInspectionDrawing.Text(dc, this, text, new Point(Math.Max(1, cx - size / 2 - 49), y - 6), 9, Foreground, false, 47);
        }
        var tolerancePen = new Pen(CManualInspectionDrawing.Color("#B99A5B"), 1.2) { DashStyle = DashStyles.Dash };
        var tx = Math.Min(size / 2, tolX * scale); var ty = Math.Min(size / 2, tolY * scale);
        dc.DrawRectangle(CManualInspectionDrawing.Color("#0AB99A5B"), tolerancePen, new Rect(cx - tx, cy - ty, tx * 2, ty * 2));
        CManualInspectionDrawing.Text(dc, this, "ΔX (µm)", new Point(Math.Max(0, cx + size / 2 - 44), cy + size / 2 + 22), 10, Foreground);
        CManualInspectionDrawing.Text(dc, this, "ΔY (µm)", new Point(8, 3), 10, Foreground);
        CManualInspectionDrawing.Text(dc, this, $"공차 X ±{CManualInspectionUiPoint.Number(tolX)} / Y ±{CManualInspectionUiPoint.Number(tolY)} µm · 동일 축척",
            new Point(8, 19), 10, Foreground, false, Math.Max(1, ActualWidth - 16));
        var groups = Metrics.ValidPoints.Select(p => _byHead ? p.HeadNo : p.CellNo).Distinct().OrderBy(x => x).ToArray();
        var groupIndices = groups.Select((number, index) => (number, index)).ToDictionary(pair => pair.number, pair => pair.index);
        dc.PushClip(new RectangleGeometry(new Rect(cx - size / 2 - 6, cy - size / 2 - 6, size + 12, size + 12)));
        var selectedHits = new List<(CManualInspectionUiPoint Point, Point At)>();
        foreach (var point in Metrics.ValidPoints)
        {
            var x = cx + point.MeasurementXUm!.Value * scale; var y = cy - point.MeasurementYUm!.Value * scale;
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
            var group = _byHead ? point.HeadNo : point.CellNo;
            var index = _byHead ? Math.Max(0, group - 1) : groupIndices[group];
            var brush = CManualInspectionDrawing.Color(CManualInspectionDrawing.HeadColors[index % CManualInspectionDrawing.HeadColors.Length]);
            var selected = _selected.Contains(point.Key);
            CManualInspectionDrawing.Marker(dc, new Point(x, y), point.UsesAppliedMeasurement || point.IsProxy ? 3.4 : 2.4,
                brush, point.UsesAppliedMeasurement ? "square" : point.IsProxy ? "diamond" : "circle", point.UsesAppliedMeasurement || point.IsProxy);
            if (point.WorkingJudge is "NG" or "ERROR")
                dc.DrawLine(new Pen(brush, 1), new Point(x - 3, y + 3), new Point(x + 3, y - 3));
            if (selected || point.Key == _hoverKey) selectedHits.Add((point, new Point(x, y)));
            _hits.Add((point.Key, new Point(x, y)));
            var bucketKey = ((int)Math.Floor(x / 24), (int)Math.Floor(y / 24));
            if (!_hitBuckets.TryGetValue(bucketKey, out var bucket)) _hitBuckets[bucketKey] = bucket = [];
            bucket.Add(_hits.Count - 1);
        }
        foreach (var hit in selectedHits)
        {
            var selected = _selected.Contains(hit.Point.Key);
            dc.DrawEllipse(null, new Pen(Background ?? Brushes.White, selected ? 4 : 3), hit.At, selected ? 6 : 5.5, selected ? 6 : 5.5);
            dc.DrawEllipse(null, new Pen(CManualInspectionDrawing.Resource(this, "AccentBrush", Brushes.SteelBlue), selected ? 2 : 1),
                hit.At, selected ? 6 : 5.5, selected ? 6 : 5.5);
        }
        if (Metrics.Count > 0 && CManualInspectionUiPoint.Finite(Metrics.MeanX) && CManualInspectionUiPoint.Finite(Metrics.MeanY))
        {
            var x = cx + Metrics.MeanX!.Value * scale; var y = cy - Metrics.MeanY!.Value * scale;
            var meanPen = new Pen(Foreground ?? Brushes.Black, 2);
            dc.DrawLine(meanPen, new Point(x - 6, y), new Point(x + 6, y));
            dc.DrawLine(meanPen, new Point(x, y - 6), new Point(x, y + 6));
        }
        dc.Pop();
        var legend = "● 실측  ◇ 복사  □ 보정 기반  ╱ NG·오류  + 평균";
        CManualInspectionDrawing.Text(dc, this, legend, new Point(8, ActualHeight - 16), 9, Foreground, false, Math.Max(1, ActualWidth - 16));
        if (ActualWidth > 470)
        {
            var x = 65d; var y = 4d;
            foreach (var group in groups.Take(8))
            {
                var index = _byHead ? Math.Max(0, group - 1) : groupIndices[group];
                var color = CManualInspectionDrawing.Color(CManualInspectionDrawing.HeadColors[index % CManualInspectionDrawing.HeadColors.Length]);
                CManualInspectionDrawing.Marker(dc, new Point(x + 3, y + 5), 2.5, color, "circle", false);
                CManualInspectionDrawing.Text(dc, this, (_byHead ? "H" : "C") + group, new Point(x + 9, y - 1), 9, Foreground);
                x += 42;
            }
        }
        if (Metrics.Count == 0) CManualInspectionDrawing.Text(dc, this, _emptyText,
            new Point(Math.Max(8, ActualWidth / 2 - 125), ActualHeight / 2 - 10), 12, Foreground, false, Math.Max(1, ActualWidth - 16));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        if (!SelectHit(e.GetPosition(this), Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) return;
        e.Handled = true;
    }

    // Click cycles truly coincident screen pixels; Ctrl selects/toggles only that overlap group.
    // Nearby but distinct dots are not silently combined. Screen-space indexing avoids sorting
    // all 10k points on every mouse move/click.
    private List<(string Key, Point Point)> HitGroup(Point at)
    {
        var candidates = new List<int>(); var bx = (int)Math.Floor(at.X / 24); var by = (int)Math.Floor(at.Y / 24);
        for (var y = by - 1; y <= by + 1; y++) for (var x = bx - 1; x <= bx + 1; x++)
            if (_hitBuckets.TryGetValue((x, y), out var bucket)) candidates.AddRange(bucket);
        var best = -1; var distance = 121d;
        foreach (var index in candidates)
        {
            var squared = (_hits[index].Point - at).LengthSquared;
            if (squared < distance) { best = index; distance = squared; }
        }
        if (best < 0) return [];
        var center = _hits[best].Point;
        return candidates.Where(index => (_hits[index].Point - center).LengthSquared <= 1.5625)
            .Select(index => _hits[index]).OrderBy(hit => hit.Key, StringComparer.Ordinal).ToList();
    }

    private bool SelectHit(Point at, bool ctrl)
    {
        var group = HitGroup(at); if (group.Count == 0) return false;
        var cluster = group[0].Key + "|" + group.Count;
        _cycleIndex = cluster == _cycleCluster ? (_cycleIndex + 1) % group.Count : 0;
        _cycleCluster = cluster;
        var focus = group[_cycleIndex].Key;
        // Partial overlap selection expands to the whole group. Once every overlapping point is
        // selected, Ctrl removes the group. A single point retains the ordinary Ctrl toggle.
        var toggle = ctrl && (group.Count == 1 || group.All(hit => _selected.Contains(hit.Key)));
        SelectionRequested?.Invoke(this, new(ctrl ? group.Select(hit => hit.Key).ToArray() : [focus], focus, ctrl, toggle));
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); var group = HitGroup(e.GetPosition(this));
        var signature = group.Count == 0 ? "" : group[0].Key + "|" + group.Count;
        if (signature == _hoverSignature) return;
        _hoverSignature = signature; _hoverKey = group.FirstOrDefault().Key;
        _tip.SetResourceReference(BackgroundProperty, "PreviewHoverTipBackgroundBrush");
        _tip.SetResourceReference(ForegroundProperty, "PreviewHoverTipTextBrush");
        if (group.Count == 0) { _tip.IsOpen = false; InvalidateVisual(); return; }
        var point = _pointByKey[_hoverKey!];
        _tip.Content = $"{point.Key}\n{point.MeasurementStatusText} · Δ {point.MeasurementErrorText} µm\n{point.MeasurementBasisText}"
            + (group.Count > 1 ? $"\n같은 화면 위치에 {group.Count:N0}점 겹침 · 클릭: 한 점씩 순환 / Ctrl 클릭: 전체 선택·해제\n"
                + string.Join("\n", group.Take(4).Select(hit => hit.Key)) + (group.Count > 4 ? "\n…" : "") : "\n클릭: 선택 / Ctrl 클릭: 선택 추가·해제");
        _tip.IsOpen = true; InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e); _tip.IsOpen = false; _hoverKey = null; _hoverSignature = ""; InvalidateVisual();
    }
}
