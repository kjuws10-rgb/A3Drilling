namespace Drilling.UI.Views;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
        double sx = 0, sy = 0;
        foreach (var p in valid) { sx += Math.Pow(p.MeasurementXUm!.Value - meanX, 2) / Math.Max(1, valid.Count); sy += Math.Pow(p.MeasurementYUm!.Value - meanY, 2) / Math.Max(1, valid.Count); }
        return new CManualInspectionScatterMetrics
        {
            Total = source.Length, Count = valid.Count, ProxyCount = valid.Count(p => p.IsProxy), CorrectionBasedCount = valid.Count(p => p.UsesAppliedMeasurement),
            Masked = masked, Unmeasured = unmeasured, Errors = errors, Invalid = invalid,
            MeanX = valid.Count == 0 ? null : meanX, MeanY = valid.Count == 0 ? null : meanY,
            SigmaX = valid.Count == 0 ? null : Math.Sqrt(sx), SigmaY = valid.Count == 0 ? null : Math.Sqrt(sy),
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
    }

    public void Configure(IReadOnlyList<CManualInspectionUiPoint> points, IEnumerable<string> selectedKeys,
        bool byHead, double toleranceXUm, double toleranceYUm, string emptyText)
    {
        _points = points; _selected = new HashSet<string>(selectedKeys, StringComparer.Ordinal);
        _byHead = byHead; _toleranceX = toleranceXUm; _toleranceY = toleranceYUm; _emptyText = emptyText;
        Metrics = CManualInspectionScatterMetrics.Calculate(points);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); _hits.Clear();
        dc.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 90 || ActualHeight < 90) return;
        var padLeft = 51.0; var padRight = 24.0; var padTop = 35.0; var padBottom = 37.0;
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
        for (var k = -2; k <= 2; k++)
        {
            var v = limit * (k / 2.0); var x = cx + (k / 4.0) * size; var y = cy - (k / 4.0) * size;
            var pen = new Pen(line, k == 0 ? 1.4 : 0.65);
            dc.DrawLine(pen, new Point(x, cy - size / 2), new Point(x, cy + size / 2));
            dc.DrawLine(pen, new Point(cx - size / 2, y), new Point(cx + size / 2, y));
            var format = Math.Abs(limit) >= 10000 ? "0.##E+0" : limit < 2 || Math.Abs(limit - Math.Round(limit)) > 1e-8 ? "0.0" : "0";
            var text = v.ToString(format, CultureInfo.InvariantCulture);
            CManualInspectionDrawing.Text(dc, this, text, new Point(x - 11, cy + size / 2 + 8), 10, Foreground);
            CManualInspectionDrawing.Text(dc, this, text, new Point(cx - size / 2 - 43, y - 7), 10, Foreground);
        }
        var tolerancePen = new Pen(CManualInspectionDrawing.Color("#B99A5B"), 1.2) { DashStyle = DashStyles.Dash };
        var tx = Math.Min(size / 2, tolX * scale); var ty = Math.Min(size / 2, tolY * scale);
        dc.DrawRectangle(null, tolerancePen, new Rect(cx - tx, cy - ty, tx * 2, ty * 2));
        CManualInspectionDrawing.Text(dc, this, "ΔX (µm)", new Point(Math.Max(0, ActualWidth - 67), ActualHeight - 18), 10, Foreground);
        CManualInspectionDrawing.Text(dc, this, "ΔY (µm)", new Point(8, 3), 10, Foreground);
        CManualInspectionDrawing.Text(dc, this, $"점선: 공차 ±{CManualInspectionUiPoint.Number(tolX)} / ±{CManualInspectionUiPoint.Number(tolY)} µm",
            new Point(cx - size / 2, 15), 10, CManualInspectionDrawing.Color("#B99A5B"));
        var groups = _points.Select(p => _byHead ? p.HeadNo : p.CellNo).Distinct().OrderBy(x => x).ToArray();
        dc.PushClip(new RectangleGeometry(new Rect(cx - size / 2 - 6, cy - size / 2 - 6, size + 12, size + 12)));
        foreach (var point in Metrics.ValidPoints)
        {
            var x = cx + point.MeasurementXUm!.Value * scale; var y = cy - point.MeasurementYUm!.Value * scale;
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
            var group = _byHead ? point.HeadNo : point.CellNo;
            var index = _byHead ? Math.Max(0, group - 1) : Math.Max(0, Array.IndexOf(groups, group));
            var brush = CManualInspectionDrawing.Color(CManualInspectionDrawing.HeadColors[index % CManualInspectionDrawing.HeadColors.Length]);
            var selected = _selected.Contains(point.Key);
            if (point.UsesAppliedMeasurement)
                dc.DrawRectangle(null, new Pen(brush, selected ? 2.3 : 1.3), new Rect(x - 3.5, y - 3.5, 7, 7));
            else if (point.IsProxy)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open()) { g.BeginFigure(new Point(x, y - 4), false, true); g.LineTo(new Point(x + 4, y), true, false); g.LineTo(new Point(x, y + 4), true, false); g.LineTo(new Point(x - 4, y), true, false); }
                geometry.Freeze(); dc.DrawGeometry(null, new Pen(brush, selected ? 2.3 : 1.3), geometry);
            }
            else dc.DrawEllipse(brush, selected ? new Pen(Foreground, 1.2) : null, new Point(x, y), selected ? 4 : 2.5, selected ? 4 : 2.5);
            _hits.Add((point.Key, new Point(x, y)));
        }
        if (Metrics.Count > 0 && CManualInspectionUiPoint.Finite(Metrics.MeanX) && CManualInspectionUiPoint.Finite(Metrics.MeanY))
        {
            var x = cx + Metrics.MeanX!.Value * scale; var y = cy - Metrics.MeanY!.Value * scale;
            var meanPen = new Pen(Foreground ?? Brushes.Black, 2);
            dc.DrawLine(meanPen, new Point(x - 6, y), new Point(x + 6, y));
            dc.DrawLine(meanPen, new Point(x, y - 6), new Point(x, y + 6));
        }
        dc.Pop();
        if (Metrics.Count == 0) CManualInspectionDrawing.Text(dc, this, _emptyText,
            new Point(Math.Max(8, ActualWidth / 2 - 125), ActualHeight / 2 - 10), 12, Foreground, false, Math.Max(1, ActualWidth - 16));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        var p = e.GetPosition(this); var best = _hits.OrderBy(x => (x.Point - p).LengthSquared).FirstOrDefault();
        if (best.Key is null || (best.Point - p).Length > 11) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        SelectionRequested?.Invoke(this, new([best.Key], best.Key, ctrl, ctrl));
        e.Handled = true;
    }
}
