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
            else if (p.IsMeasurementOffsetInitialized) unmeasured++;
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

/// <summary>Equal-scale ΔX/ΔY display. Point provenance is textual; NG always overrides series color.</summary>
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
    private string _dataKind = "WORKING";
    private bool IsOffsetData => _dataKind is "OFFSETS" or "OFFSETS_DRAFT";
    private string _emptyText = "이 범위에 유효한 측정값이 없습니다.";
    private bool _autoScale = true;
    private double _halfRange = 5;
    private double _referenceHalfRange = 5;
    private double _centerX, _centerY;
    private Rect _plotBounds;
    private string? _focusKey;
    private string _contextIdentity = "", _scopeIdentity = "";
    private sealed record ClickSelection(string[] Keys, string? FocusKey, string ContextIdentity);
    private ClickSelection? _selectionBeforeClick;
    private bool _selectionDispatching;
    public event EventHandler<CManualInspectionSelectionEventArgs>? SelectionRequested;
    public event EventHandler? ViewChanged;
    public CManualInspectionScatterMetrics Metrics { get; private set; } = new();
    public string DataKind => _dataKind;
    public bool IsAutoScale => _autoScale;
    public double Zoom => _referenceHalfRange / _halfRange;
    public double MinXUm => _centerX - _halfRange;
    public double MaxXUm => _centerX + _halfRange;
    public double MinYUm => _centerY - _halfRange;
    public double MaxYUm => _centerY + _halfRange;
    public string RangeText => $"X {AxisNumber(MinXUm)} ~ {AxisNumber(MaxXUm)} / Y {AxisNumber(MinYUm)} ~ {AxisNumber(MaxYUm)} µm";

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
        AutomationProperties.SetHelpText(this, "가로 ΔX, 세로 ΔY는 µm이며 두 축 축척은 같습니다. 휠은 커서 중심 확대 축소, 더블클릭은 현재 모집단 자동 맞춤입니다. 점선은 실제 공차 경계, 십자는 평균입니다. 겹친 점은 클릭 순환, Ctrl 클릭은 같은 위치의 점을 함께 선택합니다.");
        Unloaded += (_, _) => { _tip.IsOpen = false; _hoverKey = null; };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CManualInspectionSurfacePeer(this);

    public void Configure(IReadOnlyList<CManualInspectionUiPoint> points, IEnumerable<string> selectedKeys,
        bool byHead, double toleranceXUm, double toleranceYUm, string emptyText,
        string? focusKey = null, string contextIdentity = "", string scopeIdentity = "", string dataKind = "WORKING")
    {
        var samePoints = ReferenceEquals(_points, points) || _points.Count == points.Count
            && _points.Zip(points).All(pair => ReferenceEquals(pair.First, pair.Second));
        var changedScale = _toleranceX != toleranceXUm || _toleranceY != toleranceYUm || _byHead != byHead || _dataKind != dataKind;
        if (!_selectionDispatching && !samePoints) _selectionBeforeClick = null;
        UpdateInteractionContext(focusKey, contextIdentity, scopeIdentity);
        _points = points; _selected = new HashSet<string>(selectedKeys, StringComparer.Ordinal);
        _byHead = byHead; _toleranceX = toleranceXUm; _toleranceY = toleranceYUm; _emptyText = emptyText; _dataKind = dataKind;
        AutomationProperties.SetName(this, IsOffsetData ? "통합 Offset 산포도" : "리뷰 측정 편차 산포도");
        AutomationProperties.SetHelpText(this, (IsOffsetData ? "가로/세로는 누적 통합 Offset X/Y µm입니다. 측정 잔류 오차나 공차 판정과 구분합니다. " : "가로 ΔX, 세로 ΔY는 µm이며 점선은 실제 측정 공차입니다. ")
            + "두 축 축척은 같으며 휠은 커서 중심 확대 축소, 더블클릭은 현재 모집단 자동 맞춤입니다. 겹친 점은 클릭 순환, Ctrl 클릭은 같은 위치의 점을 함께 선택합니다.");
        if (!samePoints)
        {
            Metrics = CManualInspectionScatterMetrics.Calculate(points);
            _pointByKey.Clear(); foreach (var point in points) _pointByKey[point.Key] = point;
            if (_autoScale) FitPopulation();
        }
        if (!samePoints || changedScale)
        { _tip.IsOpen = false; _hoverSignature = ""; _hoverKey = null; _cycleCluster = ""; _cycleIndex = 0; }
        InvalidateVisual();
    }

    /// <summary>Updates provenance/focus even when a host reuses the exact chart population.</summary>
    public void UpdateInteractionContext(string? focusKey, string contextIdentity, string scopeIdentity)
    {
        if (_contextIdentity != contextIdentity || !_selectionDispatching && _scopeIdentity != scopeIdentity)
            _selectionBeforeClick = null;
        _focusKey = focusKey; _contextIdentity = contextIdentity; _scopeIdentity = scopeIdentity;
    }

    public void SetFixedHalfRange(double halfRangeUm)
    {
        if (!double.IsFinite(halfRangeUm) || halfRangeUm <= 0)
            throw new ArgumentOutOfRangeException(nameof(halfRangeUm), "고정 축 범위는 0보다 큰 유한한 µm 값이어야 합니다.");
        _selectionBeforeClick = null; SetRange(0, 0, halfRangeUm, false);
    }

    /// <summary>Restores a bookmarked view. Both axes use the same half range; a wider requested axis wins.</summary>
    public void SetFixedRange(double minXUm, double maxXUm, double minYUm, double maxYUm, double referenceZoom = 1)
    {
        if (!new[] { minXUm, maxXUm, minYUm, maxYUm }.All(double.IsFinite) || minXUm >= maxXUm || minYUm >= maxYUm)
            throw new ArgumentOutOfRangeException(nameof(minXUm), "축 범위는 유한한 최소값보다 큰 최대값이어야 합니다.");
        var half = Math.Max(maxXUm / 2 - minXUm / 2, maxYUm / 2 - minYUm / 2);
        var cx = minXUm / 2 + maxXUm / 2; var cy = minYUm / 2 + maxYUm / 2;
        if (!ValidRange(cx, cy, half) || !double.IsFinite(referenceZoom) || referenceZoom <= 0 || !double.IsFinite(half * referenceZoom))
            throw new ArgumentOutOfRangeException(nameof(minXUm), "표현 가능한 축 범위/배율을 벗어났습니다.");
        _selectionBeforeClick = null; SetRange(cx, cy, half, false, referenceZoom);
    }

    public void ResetView() { _selectionBeforeClick = null; FitPopulation(); }

    private void FitPopulation()
    {
        var maximum = Metrics.ValidPoints.Select(point => Math.Max(Math.Abs(point.MeasurementXUm!.Value), Math.Abs(point.MeasurementYUm!.Value))).DefaultIfEmpty(0).Max();
        var padded = maximum * 1.12;
        var range = maximum == 0 ? .001 : double.IsFinite(padded) && padded > 0 ? padded : maximum;
        SetRange(0, 0, range, true);
    }

    private static bool ValidRange(double cx, double cy, double half) => double.IsFinite(cx) && double.IsFinite(cy)
        && double.IsFinite(half) && half > 0 && double.IsFinite(cx - half) && double.IsFinite(cx + half)
        && double.IsFinite(cy - half) && double.IsFinite(cy + half);

    private void SetRange(double cx, double cy, double half, bool automatic, double referenceZoom = 1)
    {
        _centerX = cx; _centerY = cy; _halfRange = half; _referenceHalfRange = half * referenceZoom; _autoScale = automatic;
        ClearHover(); ViewChanged?.Invoke(this, EventArgs.Empty); InvalidateVisual();
    }

    private void ClearHover()
    {
        _tip.IsOpen = false; _hoverSignature = ""; _hoverKey = null; _cycleCluster = ""; _cycleIndex = 0;
        // Until the next retained render, old pixel positions belong to the old population/range.
        // Ignoring input for that short interval is safer than selecting a now off-screen point.
        _hits.Clear(); _hitBuckets.Clear(); _plotBounds = Rect.Empty;
    }

    public void ZoomAt(double factor, Point origin)
    {
        if (!double.IsFinite(factor) || factor <= 0 || factor == 1 || !double.IsFinite(origin.X) || !double.IsFinite(origin.Y)) return;
        var plot = PlotBounds(); if (plot.IsEmpty || plot.Width <= 0) return;
        var next = _halfRange / factor;
        var nx = Math.Clamp((origin.X - (plot.Left + plot.Width / 2)) / (plot.Width / 2), -1, 1);
        var ny = Math.Clamp(((plot.Top + plot.Height / 2) - origin.Y) / (plot.Height / 2), -1, 1);
        var cx = _centerX + nx * (_halfRange - next); var cy = _centerY + ny * (_halfRange - next);
        if (!ValidRange(cx, cy, next) || !double.IsFinite(_referenceHalfRange / next)) return;
        _halfRange = next; _centerX = cx; _centerY = cy; _autoScale = false;
        ClearHover(); ViewChanged?.Invoke(this, EventArgs.Empty); InvalidateVisual();
    }

    private Rect PlotBounds()
    {
        if (ActualWidth < 90 || ActualHeight < 100) return Rect.Empty;
        const double left = 55, right = 26, top = 44, bottom = 52;
        var width = ActualWidth - left - right; var height = ActualHeight - top - bottom;
        var size = Math.Max(1, Math.Min(width, height));
        return new Rect(left + (width - size) / 2, top + (height - size) / 2, size, size);
    }

    private static double Normalized(double value, double center, double half)
    { var delta = value - center; return double.IsFinite(delta) ? delta / half : value / half - center / half; }
    private static string AxisNumber(double value) => value != 0 && (Math.Abs(value) < .01 || Math.Abs(value) >= 10000)
        ? value.ToString("0.##E+0", CultureInfo.InvariantCulture) : value.ToString("0.###", CultureInfo.InvariantCulture);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); _hits.Clear(); _hitBuckets.Clear();
        dc.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        _plotBounds = PlotBounds();
        if (_plotBounds.IsEmpty) return;
        var plot = _plotBounds; var size = plot.Width;
        var cx = plot.Left + size / 2; var cy = plot.Top + size / 2;
        var tolX = double.IsFinite(_toleranceX) ? Math.Abs(_toleranceX) : 0;
        var tolY = double.IsFinite(_toleranceY) ? Math.Abs(_toleranceY) : 0;
        var line = BorderBrush ?? Brushes.LightGray;
        dc.DrawRectangle(null, new Pen(line, .8), plot);
        for (var k = -2; k <= 2; k++)
        {
            var v = _halfRange * (k / 2.0); var x = cx + (k / 4.0) * size; var y = cy - (k / 4.0) * size;
            var pen = new Pen(line, .5);
            dc.DrawLine(pen, new Point(x, cy - size / 2), new Point(x, cy + size / 2));
            dc.DrawLine(pen, new Point(cx - size / 2, y), new Point(cx + size / 2, y));
            CManualInspectionDrawing.Text(dc, this, AxisNumber(_centerX + v), new Point(x - 13, cy + size / 2 + 6), 9, Foreground, false, 47);
            CManualInspectionDrawing.Text(dc, this, AxisNumber(_centerY + v), new Point(Math.Max(1, cx - size / 2 - 49), y - 6), 9, Foreground, false, 47);
        }
        var zx = Normalized(0, _centerX, _halfRange); var zy = Normalized(0, _centerY, _halfRange);
        var zeroPen = new Pen(Foreground ?? line, .95);
        if (Math.Abs(zx) <= 1) dc.DrawLine(zeroPen, new(cx + zx * size / 2, plot.Top), new(cx + zx * size / 2, plot.Bottom));
        if (Math.Abs(zy) <= 1) dc.DrawLine(zeroPen, new(plot.Left, cy - zy * size / 2), new(plot.Right, cy - zy * size / 2));
        var tolerancePen = new Pen(CManualInspectionDrawing.Color("#B99A5B"), 1.2) { DashStyle = DashStyles.Dash };
        var tx0 = Normalized(-tolX, _centerX, _halfRange); var tx1 = Normalized(tolX, _centerX, _halfRange);
        var ty0 = Normalized(-tolY, _centerY, _halfRange); var ty1 = Normalized(tolY, _centerY, _halfRange);
        var left = cx + Math.Clamp(tx0, -1, 1) * size / 2; var right = cx + Math.Clamp(tx1, -1, 1) * size / 2;
        var top = cy - Math.Clamp(ty1, -1, 1) * size / 2; var bottom = cy - Math.Clamp(ty0, -1, 1) * size / 2;
        // Vision residual tolerance is not a limit on cumulative compensation. Showing it as an
        // acceptance box on the offset population would mislabel a useful correction as NG.
        if (!IsOffsetData)
        {
            dc.DrawRectangle(CManualInspectionDrawing.Color("#0AB99A5B"), null, new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)));
            if (Math.Abs(tx0) <= 1) dc.DrawLine(tolerancePen, new(left, top), new(left, bottom));
            if (Math.Abs(tx1) <= 1) dc.DrawLine(tolerancePen, new(right, top), new(right, bottom));
            if (Math.Abs(ty0) <= 1) dc.DrawLine(tolerancePen, new(left, bottom), new(right, bottom));
            if (Math.Abs(ty1) <= 1) dc.DrawLine(tolerancePen, new(left, top), new(right, top));
        }
        CManualInspectionDrawing.Text(dc, this, IsOffsetData ? "Offset X (µm)" : "ΔX (µm)", new Point(Math.Max(0, cx + size / 2 - (IsOffsetData ? 76 : 44)), cy + size / 2 + 22), 10, Foreground);
        CManualInspectionDrawing.Text(dc, this, IsOffsetData ? "Offset Y (µm)" : "ΔY (µm)", new Point(8, 3), 10, Foreground);
        var toleranceOutside = new[] { tx0, tx1, ty0, ty1 }.All(value => Math.Abs(value) > 1);
        var zeroOutside = Math.Abs(zx) > 1 || Math.Abs(zy) > 1;
        CManualInspectionDrawing.Text(dc, this, (IsOffsetData ? "통합 측정 Offset · 누적 보정량 · 동일 축척" + (_dataKind == "OFFSETS_DRAFT" ? " · 미저장" : "")
                : $"공차 X ±{CManualInspectionUiPoint.Number(tolX)} / Y ±{CManualInspectionUiPoint.Number(tolY)} µm · 동일 축척" + (toleranceOutside ? " · 공차 경계 축 밖" : ""))
            + (zeroOutside ? " · 원점 축 밖" : ""),
            new Point(8, 19), 10, Foreground, false, Math.Max(1, ActualWidth - 16), singleLine: true);
        var groups = Metrics.ValidPoints.Select(p => _byHead ? p.HeadNo : p.CellNo).Distinct().OrderBy(x => x).ToArray();
        var groupIndices = groups.Select((number, index) => (number, index)).ToDictionary(pair => pair.number, pair => pair.index);
        dc.PushClip(new RectangleGeometry(plot));
        var selectedHits = new List<(CManualInspectionUiPoint Point, Point At)>();
        foreach (var point in Metrics.ValidPoints)
        {
            var nx = Normalized(point.MeasurementXUm!.Value, _centerX, _halfRange);
            var ny = Normalized(point.MeasurementYUm!.Value, _centerY, _halfRange);
            if (!double.IsFinite(nx) || !double.IsFinite(ny) || Math.Abs(nx) > 1 || Math.Abs(ny) > 1) continue;
            var x = cx + nx * size / 2; var y = cy - ny * size / 2;
            var group = _byHead ? point.HeadNo : point.CellNo;
            var index = _byHead ? Math.Max(0, group - 1) : groupIndices[group];
            var brush = point.WorkingJudge == "NG" ? CManualInspectionDrawing.Color("#D16776")
                : point.WorkingJudge == "ERROR" ? CManualInspectionDrawing.Color("#D39A45")
                : CManualInspectionDrawing.Color(CManualInspectionDrawing.HeadColors[index % CManualInspectionDrawing.HeadColors.Length]);
            var selected = _selected.Contains(point.Key);
            CManualInspectionDrawing.Marker(dc, new Point(x, y), point.WorkingJudge == "NG" ? 3.4 : 2.8,
                brush, "circle", false);
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
            var x = cx + Normalized(Metrics.MeanX!.Value, _centerX, _halfRange) * size / 2;
            var y = cy - Normalized(Metrics.MeanY!.Value, _centerY, _halfRange) * size / 2;
            var meanPen = new Pen(Foreground ?? Brushes.Black, 2);
            if (double.IsFinite(x) && double.IsFinite(y) && plot.Contains(new Point(x, y)))
            {
                dc.DrawLine(meanPen, new Point(x - 6, y), new Point(x + 6, y));
                dc.DrawLine(meanPen, new Point(x, y - 6), new Point(x, y + 6));
            }
        }
        dc.Pop();
        var legend = ActualWidth < 350
            ? $"{(IsOffsetData ? "Offset" : "NG 빨강")} · + 평균 · {_hits.Count:N0}/{Metrics.Count:N0}점"
            : IsOffsetData ? $"통합 Offset · + 평균 · {_hits.Count:N0}/{Metrics.Count:N0}점"
            : $"NG 빨강 · 출처는 점 위에서 확인 · + 평균 · {_hits.Count:N0}/{Metrics.Count:N0}점";
        CManualInspectionDrawing.Text(dc, this, legend, new Point(8, ActualHeight - 16), 9, Foreground, false, Math.Max(1, ActualWidth - 16));
        if (ActualWidth > 470)
        {
            var x = IsOffsetData ? 100d : 65d; var y = 4d;
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
        if (TryResetOnDoubleClick(e.ClickCount)) { e.Handled = true; return; }
        if (!SelectHit(e.GetPosition(this), Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) return;
        e.Handled = true;
    }

    // Click cycles truly coincident screen pixels; Ctrl selects/toggles only that overlap group.
    // Nearby but distinct dots are not silently combined. Screen-space indexing avoids sorting
    // all 10k points on every mouse move/click.
    private List<(string Key, Point Point)> HitGroup(Point at)
    {
        if (_plotBounds.IsEmpty || !_plotBounds.Contains(at)) return [];
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
        _selectionBeforeClick = null;
        var group = HitGroup(at); if (group.Count == 0) return false;
        var cluster = group[0].Key + "|" + group.Count;
        _cycleIndex = cluster == _cycleCluster ? (_cycleIndex + 1) % group.Count : 0;
        _cycleCluster = cluster;
        var focus = group[_cycleIndex].Key;
        // Partial overlap selection expands to the whole group. Once every overlapping point is
        // selected, Ctrl removes the group. A single point retains the ordinary Ctrl toggle.
        var toggle = ctrl && (group.Count == 1 || group.All(hit => _selected.Contains(hit.Key)));
        _selectionBeforeClick = new(_selected.ToArray(), _focusKey, _contextIdentity);
        _selectionDispatching = true;
        try { SelectionRequested?.Invoke(this, new(ctrl ? group.Select(hit => hit.Key).ToArray() : [focus], focus, ctrl, toggle)); }
        finally { _selectionDispatching = false; }
        return true;
    }

    private bool TryResetOnDoubleClick(int clickCount)
    {
        if (clickCount < 2) return false;
        var before = _selectionBeforeClick?.ContextIdentity == _contextIdentity ? _selectionBeforeClick : null;
        _selectionBeforeClick = null;
        if (before is not null && (!_selected.SetEquals(before.Keys) || _focusKey != before.FocusKey))
        {
            _selectionDispatching = true;
            try { SelectionRequested?.Invoke(this, new(before.Keys, before.FocusKey, clearFocus: before.FocusKey is null)); }
            finally { _selectionDispatching = false; }
        }
        ResetView(); return true;
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
        _tip.Content = $"{point.Key}\n" + (IsOffsetData
                ? $"통합 Offset {point.BaseOffsetText} µm\n{(_dataKind == "OFFSETS_DRAFT" ? "미저장 작업 누적 보정량 · 실제 활성값은 아직 유지" : "모델에 저장된 누적 보정량 · 원측정 잔류 오차와 구분") }"
                : $"{point.MeasurementStatusText} · Δ {point.MeasurementErrorText} µm\n출처: {CManualInspectionDrawing.MeasurementOrigin(point)}")
            + (group.Count > 1 ? $"\n같은 화면 위치에 {group.Count:N0}점 겹침 · 클릭: 한 점씩 순환 / Ctrl 클릭: 전체 선택·해제\n"
                + string.Join("\n", group.Take(4).Select(hit => hit.Key)) + (group.Count > 4 ? "\n…" : "") : "\n클릭: 선택 / Ctrl 클릭: 선택 추가·해제");
        _tip.IsOpen = true; InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e); _tip.IsOpen = false; _hoverKey = null; _hoverSignature = ""; InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomAt(Math.Exp(Math.Sign(e.Delta) * .14), e.GetPosition(this)); e.Handled = true;
    }
}
