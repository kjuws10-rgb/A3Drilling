using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Drilling.UI.Menu.Menus;

internal static class CCoordinateOffsetPreviewDrawing
{
    public const double DesignWidth = 520.0;
    public const double DesignHeight = 280.0;
    public const int GridDivisionCount = 20;
    private const double DefaultMinimum = -5.0;
    private const double DefaultMaximum = 5.0;
    private static readonly Rect PlotRect = new(58.0, 24.0, 406.0, 210.0);

    public static ImageSource Create(
        string xAxisLabel,
        string yAxisLabel,
        double offsetX,
        double offsetY,
        Vector xPositiveDirection,
        Vector yPositiveDirection,
        double minimum = DefaultMinimum,
        double maximum = DefaultMaximum)
    {
        xAxisLabel = string.IsNullOrWhiteSpace(xAxisLabel) ? "X" : xAxisLabel.Trim().ToUpperInvariant();
        yAxisLabel = string.IsNullOrWhiteSpace(yAxisLabel) ? "Y" : yAxisLabel.Trim().ToUpperInvariant();
        xPositiveDirection = NormalizeDirection(xPositiveDirection, new Vector(1.0, 0.0));
        yPositiveDirection = NormalizeDirection(yPositiveDirection, new Vector(0.0, 1.0));
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum >= 0.0 || maximum <= 0.0 || minimum >= maximum)
        {
            minimum = DefaultMinimum;
            maximum = DefaultMaximum;
        }

        var span = maximum - minimum;
        var gridStep = span / GridDivisionCount;
        var origin = CalculateOrigin(minimum, maximum, xPositiveDirection, yPositiveDirection);
        var xScale = GetDirectionalScale(xPositiveDirection, PlotRect.Width / span, PlotRect.Height / span);
        var yScale = GetDirectionalScale(yPositiveDirection, PlotRect.Width / span, PlotRect.Height / span);
        var rawApplied = origin + (xPositiveDirection * (offsetX * xScale)) + (yPositiveDirection * (offsetY * yScale));
        var outOfRange = offsetX < minimum || offsetX > maximum || offsetY < minimum || offsetY > maximum;
        var applied = ClipToPlot(origin, rawApplied);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(CreateBrush(17, 24, 32), null, new Rect(0.0, 0.0, DesignWidth, DesignHeight));
            DrawGrid(context, origin, minimum, maximum, xPositiveDirection, yPositiveDirection);

            var scaleBrush = CreateBrush(148, 163, 184);
            DrawText(context, $"GRID {gridStep.ToString("0.###", CultureInfo.InvariantCulture)} mm / DIV", new Point(PlotRect.Left, 4.0), 9.0, scaleBrush);
            var appliedBrush = CreateBrush(74, 222, 128);
            var deltaBrush = outOfRange ? CreateBrush(248, 113, 113) : appliedBrush;

            var vectorPen = new Pen(appliedBrush, 1.6);
            vectorPen.Freeze();
            context.DrawLine(vectorPen, origin, applied);
            DrawArrowHead(context, vectorPen, origin, applied);

            var designBrush = CreateBrush(203, 213, 225);
            var designPen = new Pen(designBrush, 1.8);
            designPen.Freeze();
            context.DrawEllipse(CreateBrush(30, 41, 59), designPen, origin, 6.0, 6.0);

            var appliedPen = new Pen(CreateBrush(187, 247, 208), 1.0);
            appliedPen.Freeze();
            context.DrawEllipse(appliedBrush, appliedPen, applied, 4.5, 4.5);

            DrawTextWithin(context, "BASE", new Point(origin.X + 9.0, origin.Y + 7.0), 10.0, designBrush, PlotRect);
            var appliedLabel = $"{(outOfRange ? "APPLIED · OUT" : "APPLIED")}\nΔX {offsetX.ToString("0.000", CultureInfo.InvariantCulture)} / ΔY {offsetY.ToString("0.000", CultureInfo.InvariantCulture)} mm";
            DrawTextWithin(context, appliedLabel, GetAppliedLabelOrigin(applied), 9.0, deltaBrush, PlotRect);
            var axisBrush = CreateBrush(125, 211, 252);
            var axisPen = new Pen(axisBrush, 1.4);
            axisPen.Freeze();
            DrawPositiveAxis(context, origin, xPositiveDirection, xAxisLabel, axisPen, axisBrush);
            DrawPositiveAxis(context, origin, yPositiveDirection, yAxisLabel, axisPen, axisBrush);
            context.DrawRectangle(null, new Pen(CreateBrush(71, 85, 105), 1.0), PlotRect);
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    public static ImageSource CreateReviewCamera(
        string xAxisLabel,
        string yAxisLabel,
        double machiningOffsetX,
        double machiningOffsetY,
        double inspectionOffsetX,
        double inspectionOffsetY,
        Vector xPositiveDirection,
        Vector yPositiveDirection,
        double minimum = DefaultMinimum,
        double maximum = DefaultMaximum)
    {
        xAxisLabel = string.IsNullOrWhiteSpace(xAxisLabel) ? "X" : xAxisLabel.Trim().ToUpperInvariant();
        yAxisLabel = string.IsNullOrWhiteSpace(yAxisLabel) ? "Y" : yAxisLabel.Trim().ToUpperInvariant();
        xPositiveDirection = NormalizeDirection(xPositiveDirection, new Vector(1.0, 0.0));
        yPositiveDirection = NormalizeDirection(yPositiveDirection, new Vector(0.0, 1.0));
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum >= 0.0 || maximum <= 0.0 || minimum >= maximum)
        {
            minimum = DefaultMinimum;
            maximum = DefaultMaximum;
        }

        var span = maximum - minimum;
        var gridStep = span / GridDivisionCount;
        var origin = CalculateOrigin(minimum, maximum, xPositiveDirection, yPositiveDirection);
        var xScale = GetDirectionalScale(xPositiveDirection, PlotRect.Width / span, PlotRect.Height / span);
        var yScale = GetDirectionalScale(yPositiveDirection, PlotRect.Width / span, PlotRect.Height / span);
        var rawMachining = origin + (xPositiveDirection * (machiningOffsetX * xScale)) + (yPositiveDirection * (machiningOffsetY * yScale));
        var rawInspection = origin + (xPositiveDirection * (inspectionOffsetX * xScale)) + (yPositiveDirection * (inspectionOffsetY * yScale));
        var machiningOutOfRange = machiningOffsetX < minimum || machiningOffsetX > maximum || machiningOffsetY < minimum || machiningOffsetY > maximum;
        var inspectionOutOfRange = inspectionOffsetX < minimum || inspectionOffsetX > maximum || inspectionOffsetY < minimum || inspectionOffsetY > maximum;
        var machining = ClipToPlot(origin, rawMachining);
        var inspection = ClipToPlot(origin, rawInspection);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(CreateBrush(17, 24, 32), null, new Rect(0.0, 0.0, DesignWidth, DesignHeight));
            DrawGrid(context, origin, minimum, maximum, xPositiveDirection, yPositiveDirection);

            var scaleBrush = CreateBrush(148, 163, 184);
            DrawText(context, $"GRID {gridStep.ToString("0.###", CultureInfo.InvariantCulture)} mm / DIV", new Point(PlotRect.Left, 4.0), 9.0, scaleBrush);

            var machiningBrush = CreateBrush(74, 222, 128);
            var machiningLabelBrush = machiningOutOfRange ? CreateBrush(248, 113, 113) : machiningBrush;
            var machiningVectorPen = new Pen(machiningBrush, 1.4);
            machiningVectorPen.Freeze();
            context.DrawLine(machiningVectorPen, origin, machining);

            var inspectionBrush = CreateBrush(251, 191, 36);
            var inspectionLabelBrush = inspectionOutOfRange ? CreateBrush(248, 113, 113) : inspectionBrush;
            var inspectionVectorPen = new Pen(inspectionBrush, 1.2) { DashStyle = DashStyles.Dash };
            inspectionVectorPen.Freeze();
            context.DrawLine(inspectionVectorPen, origin, inspection);

            var designBrush = CreateBrush(203, 213, 225);
            var designPen = new Pen(designBrush, 1.5);
            designPen.Freeze();
            context.DrawEllipse(CreateBrush(30, 41, 59), designPen, origin, 4.0, 4.0);
            DrawTextWithin(context, "BASE", new Point(origin.X + 8.0, origin.Y + 6.0), 9.0, designBrush, PlotRect);

            DrawInspectionMarker(context, inspection, inspectionBrush);
            var machiningPen = new Pen(CreateBrush(187, 247, 208), 1.0);
            machiningPen.Freeze();
            context.DrawEllipse(machiningBrush, machiningPen, machining, 4.5, 4.5);

            var machiningLabel = $"{(machiningOutOfRange ? "MACHINING · OUT" : "MACHINING")}\nΔX {machiningOffsetX.ToString("0.000", CultureInfo.InvariantCulture)} / ΔY {machiningOffsetY.ToString("0.000", CultureInfo.InvariantCulture)} mm";
            var inspectionLabel = $"{(inspectionOutOfRange ? "INSPECTION · OUT" : "INSPECTION")}\nΔX {inspectionOffsetX.ToString("0.000", CultureInfo.InvariantCulture)} / ΔY {inspectionOffsetY.ToString("0.000", CultureInfo.InvariantCulture)} mm";
            DrawTextWithin(context, machiningLabel, GetMarkerLabelOrigin(machining, above: true), 8.5, machiningLabelBrush, PlotRect);
            DrawTextWithin(context, inspectionLabel, GetMarkerLabelOrigin(inspection, above: false), 8.5, inspectionLabelBrush, PlotRect);

            var axisBrush = CreateBrush(125, 211, 252);
            var axisPen = new Pen(axisBrush, 1.4);
            axisPen.Freeze();
            DrawPositiveAxis(context, origin, xPositiveDirection, xAxisLabel, axisPen, axisBrush);
            DrawPositiveAxis(context, origin, yPositiveDirection, yAxisLabel, axisPen, axisBrush);
            context.DrawRectangle(null, new Pen(CreateBrush(71, 85, 105), 1.0), PlotRect);
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static void DrawInspectionMarker(DrawingContext context, Point position, Brush brush)
    {
        var pen = new Pen(brush, 1.8);
        pen.Freeze();
        const double halfSize = 7.0;
        context.DrawRectangle(CreateBrush(17, 24, 32), pen, new Rect(position.X - halfSize, position.Y - halfSize, halfSize * 2.0, halfSize * 2.0));
        context.DrawLine(pen, new Point(position.X - 5.0, position.Y), new Point(position.X + 5.0, position.Y));
        context.DrawLine(pen, new Point(position.X, position.Y - 5.0), new Point(position.X, position.Y + 5.0));
    }

    private static Point GetMarkerLabelOrigin(Point position, bool above)
    {
        if (above)
        {
            return position.Y <= PlotRect.Top + 32.0
                ? new Point(position.X + 11.0, position.Y + 10.0)
                : new Point(position.X + 11.0, position.Y - 32.0);
        }

        return position.Y >= PlotRect.Bottom - 32.0
            ? new Point(position.X + 11.0, position.Y - 32.0)
            : new Point(position.X + 11.0, position.Y + 10.0);
    }

    private static Point GetAppliedLabelOrigin(Point applied)
    {
        if (applied.Y >= PlotRect.Bottom - 12.0)
            return new Point(applied.X + 12.0, applied.Y - 36.0);
        if (applied.Y <= PlotRect.Top + 12.0)
            return new Point(applied.X + 12.0, applied.Y + 12.0);
        if (applied.X >= PlotRect.Right - 12.0)
            return new Point(applied.X - 86.0, applied.Y - 18.0);
        return new Point(applied.X + 12.0, applied.Y - 18.0);
    }

    private static Point CalculateOrigin(double minimum, double maximum, Vector xDirection, Vector yDirection)
    {
        var origin = new Point(PlotRect.Left + (PlotRect.Width / 2.0), PlotRect.Top + (PlotRect.Height / 2.0));
        SetAxisOrigin(ref origin, xDirection, minimum, maximum);
        SetAxisOrigin(ref origin, yDirection, minimum, maximum);
        return origin;
    }

    private static void SetAxisOrigin(ref Point origin, Vector direction, double minimum, double maximum)
    {
        var span = maximum - minimum;
        if (Math.Abs(direction.X) >= Math.Abs(direction.Y))
        {
            var fraction = direction.X >= 0.0 ? -minimum / span : maximum / span;
            origin.X = PlotRect.Left + (PlotRect.Width * fraction);
        }
        else
        {
            var fraction = direction.Y >= 0.0 ? -minimum / span : maximum / span;
            origin.Y = PlotRect.Top + (PlotRect.Height * fraction);
        }
    }

    private static Point ClipToPlot(Point origin, Point target)
    {
        if (PlotRect.Contains(target))
        {
            return target;
        }

        var delta = target - origin;
        var scale = 1.0;
        if (delta.X > 0.0)
            scale = Math.Min(scale, (PlotRect.Right - origin.X) / delta.X);
        else if (delta.X < 0.0)
            scale = Math.Min(scale, (PlotRect.Left - origin.X) / delta.X);
        if (delta.Y > 0.0)
            scale = Math.Min(scale, (PlotRect.Bottom - origin.Y) / delta.Y);
        else if (delta.Y < 0.0)
            scale = Math.Min(scale, (PlotRect.Top - origin.Y) / delta.Y);
        return origin + (delta * Math.Clamp(scale, 0.0, 1.0));
    }

    private static Vector NormalizeDirection(Vector direction, Vector fallback)
    {
        if (!double.IsFinite(direction.X)
            || !double.IsFinite(direction.Y)
            || direction.LengthSquared <= double.Epsilon)
        {
            direction = fallback;
        }

        direction.Normalize();
        return direction;
    }

    private static double GetDirectionalScale(Vector direction, double horizontalScale, double verticalScale)
    {
        return Math.Abs(direction.X) >= Math.Abs(direction.Y) ? horizontalScale : verticalScale;
    }

    private static void DrawPositiveAxis(
        DrawingContext context,
        Point origin,
        Vector direction,
        string label,
        Pen pen,
        Brush brush)
    {
        var horizontal = Math.Abs(direction.X) >= Math.Abs(direction.Y);
        var length = horizontal
            ? direction.X >= 0.0 ? PlotRect.Right - origin.X - 8.0 : origin.X - PlotRect.Left - 8.0
            : direction.Y >= 0.0 ? PlotRect.Bottom - origin.Y - 8.0 : origin.Y - PlotRect.Top - 8.0;
        var end = origin + (direction * Math.Max(0.0, length));
        DrawArrowHead(context, pen, origin, end);

        var displayText = $"{label}+";
        Point labelPoint;
        if (horizontal)
        {
            var estimatedWidth = Math.Max(48.0, displayText.Length * 6.4);
            labelPoint = direction.X >= 0.0
                ? new Point(PlotRect.Right - estimatedWidth - 4.0, origin.Y + 6.0)
                : new Point(PlotRect.Left + 6.0, origin.Y + 6.0);
        }
        else
        {
            labelPoint = direction.Y >= 0.0
                ? new Point(origin.X + 7.0, PlotRect.Bottom - 18.0)
                : new Point(origin.X + 7.0, PlotRect.Top + 2.0);
        }

        DrawTextWithin(context, displayText, labelPoint, 10.0, brush, PlotRect);
    }

    private static void DrawGrid(
        DrawingContext context,
        Point origin,
        double minimum,
        double maximum,
        Vector xDirection,
        Vector yDirection)
    {
        var minorPen = new Pen(CreateBrush(43, 56, 74), 0.55);
        var majorPen = new Pen(CreateBrush(57, 74, 96), 0.85);
        minorPen.Freeze();
        majorPen.Freeze();
        var horizontalDirection = Math.Abs(xDirection.X) >= Math.Abs(xDirection.Y) ? xDirection : yDirection;
        var verticalDirection = Math.Abs(xDirection.Y) > Math.Abs(xDirection.X) ? xDirection : yDirection;
        var tickBrush = CreateBrush(126, 145, 166);
        var step = (maximum - minimum) / GridDivisionCount;
        for (var index = 0; index <= GridDivisionCount; index++)
        {
            var x = PlotRect.Left + (PlotRect.Width * index / GridDivisionCount);
            var y = PlotRect.Top + (PlotRect.Height * index / GridDivisionCount);
            if (index is > 0 and < GridDivisionCount)
            {
                var pen = index % 2 == 0 ? majorPen : minorPen;
                context.DrawLine(pen, new Point(x, PlotRect.Top), new Point(x, PlotRect.Bottom));
                context.DrawLine(pen, new Point(PlotRect.Left, y), new Point(PlotRect.Right, y));
            }

            if (index % 2 != 0)
            {
                continue;
            }

            var horizontalValue = GetScreenTickValue(index, horizontalDirection.X >= 0.0, minimum, maximum);
            var verticalValue = GetScreenTickValue(index, verticalDirection.Y >= 0.0, minimum, maximum);
            DrawCenteredText(context, FormatTick(horizontalValue, step), new Point(x, PlotRect.Bottom + 3.0), 7.5, tickBrush);
            DrawRightAlignedText(context, FormatTick(verticalValue, step), new Point(PlotRect.Left - 5.0, y - 4.5), 7.5, tickBrush);
        }

        var axisPen = new Pen(CreateBrush(102, 136, 164), 1.3);
        axisPen.Freeze();
        context.DrawLine(axisPen, new Point(PlotRect.Left, origin.Y), new Point(PlotRect.Right, origin.Y));
        context.DrawLine(axisPen, new Point(origin.X, PlotRect.Top), new Point(origin.X, PlotRect.Bottom));
    }

    private static double GetScreenTickValue(int index, bool positiveTowardsIncreasingScreen, double minimum, double maximum)
    {
        var fraction = (double)index / GridDivisionCount;
        return positiveTowardsIncreasingScreen
            ? minimum + ((maximum - minimum) * fraction)
            : maximum - ((maximum - minimum) * fraction);
    }

    private static string FormatTick(double value, double step)
    {
        if (Math.Abs(value) < Math.Abs(step) * 1e-6)
            value = 0.0;
        var format = Math.Abs(step) < 0.01 ? "0.000" : Math.Abs(step) < 0.1 ? "0.00" : Math.Abs(step) < 1.0 ? "0.0" : "0.##";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    private static void DrawArrowHead(DrawingContext context, Pen pen, Point start, Point end)
    {
        var delta = end - start;
        if (delta.Length < 5.0)
        {
            return;
        }

        delta.Normalize();
        var normal = new Vector(-delta.Y, delta.X);
        var basePoint = end - (delta * 9.0);
        context.DrawLine(pen, end, basePoint + (normal * 4.0));
        context.DrawLine(pen, end, basePoint - (normal * 4.0));
    }

    private static void DrawText(DrawingContext context, string text, Point origin, double fontSize, Brush brush)
    {
        context.DrawText(CreateText(text, fontSize, brush), origin);
    }

    private static void DrawCenteredText(DrawingContext context, string text, Point origin, double fontSize, Brush brush)
    {
        var formatted = CreateText(text, fontSize, brush);
        context.DrawText(formatted, new Point(origin.X - (formatted.Width / 2.0), origin.Y));
    }

    private static void DrawRightAlignedText(DrawingContext context, string text, Point origin, double fontSize, Brush brush)
    {
        var formatted = CreateText(text, fontSize, brush);
        context.DrawText(formatted, new Point(origin.X - formatted.Width, origin.Y));
    }

    private static void DrawTextWithin(DrawingContext context, string text, Point origin, double fontSize, Brush brush, Rect bounds)
    {
        var formatted = CreateText(text, fontSize, brush);
        var x = Math.Clamp(origin.X, bounds.Left + 2.0, Math.Max(bounds.Left + 2.0, bounds.Right - formatted.Width - 2.0));
        var y = Math.Clamp(origin.Y, bounds.Top + 2.0, Math.Max(bounds.Top + 2.0, bounds.Bottom - formatted.Height - 2.0));
        context.DrawText(formatted, new Point(x, y));
    }

    private static FormattedText CreateText(string text, double fontSize, Brush brush)
    {
        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            fontSize,
            brush,
            1.0);
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
