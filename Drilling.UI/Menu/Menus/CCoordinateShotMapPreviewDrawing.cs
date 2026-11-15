using System.Windows;
using System.Windows.Media;

namespace Drilling.UI.Menu.Menus;

internal static class CCoordinateShotMapPreviewDrawing
{
    private const double LandscapeWidth = 680.0;
    private const double LandscapeHeight = 420.0;
    private const double PortraitWidth = 420.0;
    private const double PortraitHeight = 680.0;
    private const double MapMargin = 28.0;

    public static ST_COORDINATE_SHOT_MAP_RENDER Create(
        IReadOnlyList<ST_COORDINATE_SHOT_MAP_POINT> shots,
        string selectedShotKey)
    {
        if (shots.Count == 0)
        {
            return new ST_COORDINATE_SHOT_MAP_RENDER(null, LandscapeWidth, LandscapeHeight);
        }

        var first = shots[0];
        if (first.GroupCountX <= 0 || first.GroupCountY <= 0)
        {
            return new ST_COORDINATE_SHOT_MAP_RENDER(null, LandscapeWidth, LandscapeHeight);
        }

        var size = GetSurfaceSize(first.GroupCountX, first.GroupCountY);
        var mapRect = GetMapRect(first.GroupCountX, first.GroupCountY);
        var cellWidth = mapRect.Width / first.GroupCountX;
        var cellHeight = mapRect.Height / first.GroupCountY;
        var cellInset = Math.Clamp(Math.Min(cellWidth, cellHeight) * 0.07, 0.15, 1.1);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0.0, 0.0, size.Width, size.Height));
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(12, 102, 136, 164)), null, mapRect);
            DrawGrid(context, mapRect, first.GroupCountX, first.GroupCountY);

            var normalGeometry = new StreamGeometry();
            var maskingCellGeometry = new StreamGeometry();
            var maskingGeometry = new StreamGeometry();
            var chessGeometry = new StreamGeometry();
            using (var normalContext = normalGeometry.Open())
            using (var maskingCellContext = maskingCellGeometry.Open())
            using (var maskingContext = maskingGeometry.Open())
            using (var chessContext = chessGeometry.Open())
            {
                foreach (var shot in shots)
                {
                    var x = mapRect.Left + ((shot.GroupColumn + 0.5) * cellWidth);
                    var y = mapRect.Top + ((shot.GroupRow + 0.5) * cellHeight);
                    var cellRect = new Rect(
                        mapRect.Left + (shot.GroupColumn * cellWidth) + cellInset,
                        mapRect.Top + (shot.GroupRow * cellHeight) + cellInset,
                        Math.Max(0.1, cellWidth - (cellInset * 2.0)),
                        Math.Max(0.1, cellHeight - (cellInset * 2.0)));
                    if (shot.IsMasked)
                    {
                        AddRectangle(maskingCellContext, cellRect);
                        AddMaskCross(maskingContext, x, y, Math.Max(1.8, Math.Min(cellWidth, cellHeight) * 0.28));
                    }
                    else if (shot.IsChessSkipped)
                    {
                        AddRectangle(chessContext, cellRect);
                    }
                    else
                    {
                        AddRectangle(normalContext, cellRect);
                    }
                }
            }

            normalGeometry.Freeze();
            maskingCellGeometry.Freeze();
            maskingGeometry.Freeze();
            chessGeometry.Freeze();
            var maskingBrush = CreateBrush(239, 68, 68);
            var chessBrush = CreateBrush(168, 85, 247);
            var maskingPen = new Pen(maskingBrush, 1.8);
            var chessPen = new Pen(chessBrush, 1.5);
            maskingPen.Freeze();
            chessPen.Freeze();
            context.DrawGeometry(new SolidColorBrush(Color.FromArgb(78, 100, 116, 139)), null, normalGeometry);
            context.DrawGeometry(new SolidColorBrush(Color.FromArgb(90, 239, 68, 68)), null, maskingCellGeometry);
            context.DrawGeometry(null, maskingPen, maskingGeometry);
            context.DrawGeometry(new SolidColorBrush(Color.FromArgb(105, 168, 85, 247)), chessPen, chessGeometry);

            ST_COORDINATE_SHOT_MAP_POINT? selectedShot = null;
            foreach (var shot in shots)
            {
                if (shot.ShotKey.Equals(selectedShotKey, StringComparison.OrdinalIgnoreCase))
                {
                    selectedShot = shot;
                    break;
                }
            }

            if (selectedShot is not null)
            {
                DrawSelectedShot(context, mapRect, cellWidth, cellHeight, selectedShot);
            }

            var framePen = new Pen(CreateBrush(102, 136, 164), 1.4);
            framePen.Freeze();
            context.DrawRectangle(null, framePen, mapRect);
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return new ST_COORDINATE_SHOT_MAP_RENDER(image, size.Width, size.Height);
    }

    public static bool TryResolveShot(
        double x,
        double y,
        int groupCountX,
        int groupCountY,
        out int groupColumn,
        out int groupRow)
    {
        groupColumn = -1;
        groupRow = -1;
        if (groupCountX <= 0 || groupCountY <= 0)
        {
            return false;
        }

        var mapRect = GetMapRect(groupCountX, groupCountY);
        if (!mapRect.Contains(new Point(x, y)))
        {
            return false;
        }

        groupColumn = Math.Clamp((int)((x - mapRect.Left) / mapRect.Width * groupCountX), 0, groupCountX - 1);
        groupRow = Math.Clamp((int)((y - mapRect.Top) / mapRect.Height * groupCountY), 0, groupCountY - 1);
        return true;
    }

    private static Size GetSurfaceSize(int groupCountX, int groupCountY)
    {
        return groupCountX > groupCountY
            ? new Size(LandscapeWidth, LandscapeHeight)
            : new Size(PortraitWidth, PortraitHeight);
    }

    private static Rect GetMapRect(int groupCountX, int groupCountY)
    {
        var size = GetSurfaceSize(groupCountX, groupCountY);
        return new Rect(MapMargin, MapMargin, size.Width - (MapMargin * 2.0), size.Height - (MapMargin * 2.0));
    }

    private static void DrawGrid(DrawingContext context, Rect mapRect, int groupCountX, int groupCountY)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(55, 100, 116, 139)), 0.8);
        gridPen.Freeze();
        var columnStep = mapRect.Width / groupCountX >= 1.25 ? 1 : Math.Max(1, groupCountX / 40);
        var rowStep = mapRect.Height / groupCountY >= 1.25 ? 1 : Math.Max(1, groupCountY / 60);
        for (var column = columnStep; column < groupCountX; column += columnStep)
        {
            var x = mapRect.Left + (mapRect.Width * column / groupCountX);
            context.DrawLine(gridPen, new Point(x, mapRect.Top), new Point(x, mapRect.Bottom));
        }

        for (var row = rowStep; row < groupCountY; row += rowStep)
        {
            var y = mapRect.Top + (mapRect.Height * row / groupCountY);
            context.DrawLine(gridPen, new Point(mapRect.Left, y), new Point(mapRect.Right, y));
        }
    }

    private static void DrawSelectedShot(
        DrawingContext context,
        Rect mapRect,
        double cellWidth,
        double cellHeight,
        ST_COORDINATE_SHOT_MAP_POINT selectedShot)
    {
        var x = mapRect.Left + ((selectedShot.GroupColumn + 0.5) * cellWidth);
        var y = mapRect.Top + ((selectedShot.GroupRow + 0.5) * cellHeight);
        var selectedRect = new Rect(
            mapRect.Left + (selectedShot.GroupColumn * cellWidth) + 0.6,
            mapRect.Top + (selectedShot.GroupRow * cellHeight) + 0.6,
            Math.Max(0.1, cellWidth - 1.2),
            Math.Max(0.1, cellHeight - 1.2));
        var selectedBrush = CreateBrush(246, 196, 83);
        var selectedPen = new Pen(selectedBrush, 2.2);
        selectedPen.Freeze();
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(92, 246, 196, 83)), selectedPen, selectedRect);
        var crossRadius = Math.Clamp(Math.Min(cellWidth, cellHeight) * 0.28, 1.5, 6.0);
        context.DrawLine(selectedPen, new Point(x - crossRadius, y), new Point(x + crossRadius, y));
        context.DrawLine(selectedPen, new Point(x, y - crossRadius), new Point(x, y + crossRadius));
    }

    private static void AddRectangle(StreamGeometryContext context, Rect rect)
    {
        context.BeginFigure(rect.TopLeft, true, true);
        context.LineTo(rect.TopRight, true, false);
        context.LineTo(rect.BottomRight, true, false);
        context.LineTo(rect.BottomLeft, true, false);
    }

    private static void AddMaskCross(StreamGeometryContext context, double centerX, double centerY, double radius)
    {
        context.BeginFigure(new Point(centerX - radius, centerY - radius), false, false);
        context.LineTo(new Point(centerX + radius, centerY + radius), true, false);
        context.BeginFigure(new Point(centerX - radius, centerY + radius), false, false);
        context.LineTo(new Point(centerX + radius, centerY - radius), true, false);
    }

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}

internal sealed record ST_COORDINATE_SHOT_MAP_POINT(
    string ShotKey,
    int GroupColumn,
    int GroupRow,
    int GroupCountX,
    int GroupCountY,
    bool IsMasked,
    bool IsChessSkipped);

internal sealed record ST_COORDINATE_SHOT_MAP_RENDER(ImageSource? Image, double Width, double Height);
