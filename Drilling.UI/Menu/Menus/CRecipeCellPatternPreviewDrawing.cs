using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Drilling.Common.Recipe;

namespace Drilling.UI.Menu.Menus;
internal static class CRecipeCellPatternPreviewDrawing
{
    public const double CellMapPortraitDesignWidth = 420.0;
    public const double CellMapPortraitDesignHeight = 680.0;
    public const double CellMapLandscapeDesignWidth = 680.0;
    public const double CellMapLandscapeDesignHeight = 420.0;
    public const double ShotDetailDesignWidth = 360.0;
    public const double ShotDetailDesignHeight = 360.0;
    private static readonly Rect CellMapPortraitRect = new(40.0, 22.0, 340.0, 612.0);
    private static readonly Rect CellMapLandscapeRect = new(34.0, 40.0, 612.0, 340.0);
    private static readonly Rect ShotDetailRect = new(42.0, 42.0, 276.0, 276.0);
    public static double GetCellMapDesignWidth(ST_CELL_PATTERN_LAYOUT patternLayout)
    {
        return IsLandscape(patternLayout) ? CellMapLandscapeDesignWidth : CellMapPortraitDesignWidth;
    }

    public static double GetCellMapDesignHeight(ST_CELL_PATTERN_LAYOUT patternLayout)
    {
        return IsLandscape(patternLayout) ? CellMapLandscapeDesignHeight : CellMapPortraitDesignHeight;
    }

    public static ImageSource? CreateCellMap(
        ST_RECIPE_CELL_VIEW_MODEL cell, 
        IReadOnlyList<ST_RECIPE_MASKING_AREA> maskingAreas, 
        ST_RECIPE_EDGE_MASKING_AREA? edgeMaskingArea, 
        IReadOnlyList<ST_SHOT_GROUP_EDITOR_ROW> shotGroups, 
        ST_SHOT_GROUP_EDITOR_ROW? selectedGroup
    )
    {
        var countX = cell.PatternLayout.GroupCountX;
        var countY = cell.PatternLayout.GroupCountY;
        if (!cell.PatternLayout.IsValid || countX <= 0 || countY <= 0)
        {
            return null;
        }

        var designWidth = GetCellMapDesignWidth(cell.PatternLayout);
        var designHeight = GetCellMapDesignHeight(cell.PatternLayout);
        var cellMapRect = GetCellMapRect(cell.PatternLayout);
        var processedMapRect = GetProcessedMapRect(cell.PatternLayout, cellMapRect);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.PushClip(new RectangleGeometry(new Rect(0.0, 0.0, designWidth, designHeight)));
            context.DrawRectangle(CreateBrush(1, 0, 0, 0), null, new Rect(0.0, 0.0, designWidth, designHeight));
            context.DrawRectangle(CreateBrush(255, 17, 24, 32), null, cellMapRect);
            DrawExcludedTargetEdges(context, cell.PatternLayout, cellMapRect, processedMapRect);
            DrawEdgeMaskingArea(context, cell, processedMapRect, edgeMaskingArea);
            DrawMaskingAreas(context, cell, processedMapRect, maskingAreas);
            DrawMapGrid(context, processedMapRect, countX, countY);
            DrawShotCenters(context, processedMapRect, countX, countY);
            DrawEdgeMaskingOutline(context, cell, processedMapRect, edgeMaskingArea);
            DrawMaskedShots(context, processedMapRect, cell, shotGroups);
            DrawSelectedShot(context, processedMapRect, cell, selectedGroup);
            DrawAxes(context, cellMapRect);
            context.DrawRectangle(null, CreatePen(255, 102, 136, 164, 1.4), cellMapRect);
            context.Pop();
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    public static ImageSource? CreateShotDetail(double centerPitchMillimeters, int splitBeamCount, IReadOnlyList<int> maskedBeamNumbers, bool isMasked)
    {
        if (!double.IsFinite(centerPitchMillimeters) || centerPitchMillimeters <= 0.0 || !CCellPatternCalculator.IsSupportedSplitBeamCount(splitBeamCount))
        {
            return null;
        }

        var beamPitch = centerPitchMillimeters / splitBeamCount;
        if (!double.IsFinite(beamPitch) || beamPitch <= 0.0)
        {
            return null;
        }

        var domainMinimum = -(centerPitchMillimeters / 2.0);
        var domainMaximum = centerPitchMillimeters / 2.0;
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(CreateBrush(1, 0, 0, 0), null, new Rect(0.0, 0.0, ShotDetailDesignWidth, ShotDetailDesignHeight));
            context.DrawRectangle(CreateBrush(255, 17, 24, 32), null, ShotDetailRect);
            DrawBeamPitchGrid(context, beamPitch, splitBeamCount, domainMinimum, domainMaximum);
            var centerPoint = ToDetailPoint(0.0, 0.0, domainMinimum, domainMaximum);
            var centerPen = CreatePen(255, 251, 191, 36, 2.0);
            var centerArmLength = splitBeamCount == 1 ? 18.0 : 10.0;
            var centerRingRadius = splitBeamCount == 1 ? 17.0 : 5.0;
            context.DrawLine(centerPen, new Point(centerPoint.X - centerArmLength, centerPoint.Y), new Point(centerPoint.X + centerArmLength, centerPoint.Y));
            context.DrawLine(centerPen, new Point(centerPoint.X, centerPoint.Y - centerArmLength), new Point(centerPoint.X, centerPoint.Y + centerArmLength));
            context.DrawEllipse(null, centerPen, centerPoint, centerRingRadius, centerRingRadius);
            DrawDoeBeams(context, beamPitch, splitBeamCount, domainMinimum, domainMaximum, maskedBeamNumbers);
            var totalBeamCount = splitBeamCount * splitBeamCount;
            DrawText(
                context, 
                totalBeamCount == 1 ? "DOE BEAM 01" : $"DOE BEAM 01-{totalBeamCount:00}", 
                new Point(ShotDetailRect.Left, 14.0), 
                11.0, 
                isMasked ? CreateBrush(255, 248, 113, 113) : CreateBrush(255, 74, 222, 128)
            );
            if (isMasked)
            {
                DrawLaserOffStamp(context, maskedBeamNumbers);
            }

            context.DrawRectangle(null, CreatePen(255, 102, 136, 164, 1.4), ShotDetailRect);
            DrawAxes(context, ShotDetailRect);
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    public static bool TryResolveCellShotNo(double x, double y, ST_CELL_PATTERN_LAYOUT patternLayout, out int cellShotNo)
    {
        cellShotNo = 0;
        var groupCountX = patternLayout.GroupCountX;
        var groupCountY = patternLayout.GroupCountY;
        var processedMapRect = GetProcessedMapRect(patternLayout, GetCellMapRect(patternLayout));
        if (groupCountX <= 0 || groupCountY <= 0 || !processedMapRect.Contains(new Point(x, y)))
        {
            return false;
        }

        var column = Math.Clamp((int)((x - processedMapRect.Left) / processedMapRect.Width * groupCountX), 0, groupCountX - 1);
        var row = Math.Clamp((int)((y - processedMapRect.Top) / processedMapRect.Height * groupCountY), 0, groupCountY - 1);
        cellShotNo = (row * groupCountX) + column + 1;
        return true;
    }

    private static void DrawMapGrid(DrawingContext context, Rect mapRect, int countX, int countY)
    {
        var columnStep = Math.Max(1, countX / 6);
        var rowStep = Math.Max(1, countY / 8);
        var gridPen = CreatePen(72, 102, 136, 164, 0.8);
        for (var column = columnStep; column < countX; column += columnStep)
        {
            var x = mapRect.Left + (mapRect.Width * column / countX);
            context.DrawLine(gridPen, new Point(x, mapRect.Top), new Point(x, mapRect.Bottom));
        }

        for (var row = rowStep; row < countY; row += rowStep)
        {
            var y = mapRect.Top + (mapRect.Height * row / countY);
            context.DrawLine(gridPen, new Point(mapRect.Left, y), new Point(mapRect.Right, y));
        }
    }

    private static void DrawShotCenters(DrawingContext context, Rect mapRect, int countX, int countY)
    {
        var cellWidth = mapRect.Width / countX;
        var cellHeight = mapRect.Height / countY;
        var pointSize = Math.Clamp(Math.Min(cellWidth, cellHeight) * 0.38, 1.6, 4.2);
        const int maximumPreviewPoints = 10_000;
        var total = (long)countX * countY;
        var sampleStep = total <= maximumPreviewPoints
            ? 1
            : (int)Math.Ceiling(Math.Sqrt(total / (double)maximumPreviewPoints));
        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            for (var row = 0; row < countY; row += sampleStep)
            {
                for (var column = 0; column < countX; column += sampleStep)
                {
                    var x = mapRect.Left + ((column + 0.5) * cellWidth);
                    var y = mapRect.Top + ((row + 0.5) * cellHeight);
                    AddCircle(geometryContext, x, y, pointSize);
                }
            }
        }

        geometry.Freeze();
        context.DrawGeometry(CreateBrush(235, 45, 212, 191), null, geometry);
    }

    private static void DrawMaskingAreas(DrawingContext context, ST_RECIPE_CELL_VIEW_MODEL cell, Rect mapRect, IReadOnlyList<ST_RECIPE_MASKING_AREA> maskingAreas)
    {
        if (maskingAreas.Count == 0 || cell.PatternLayout.GroupCountX <= 0 || cell.PatternLayout.GroupCountY <= 0 || cell.PatternLayout.GroupPitchMillimeters <= 0.0)
        {
            return;
        }

        var fillBrush = CreateBrush(64, 248, 113, 113);
        var borderPen = CreatePen(220, 248, 113, 113, 1.2);
        var hatchPen = CreatePen(120, 254, 202, 202, 0.9);
        context.PushClip(new RectangleGeometry(mapRect));
        foreach (var area in maskingAreas)
        {
            var geometry = BuildMaskingAreaGeometry(cell, mapRect, area);
            if (geometry is null)
            {
                continue;
            }

            context.DrawGeometry(fillBrush, null, geometry);
            DrawMaskingHatches(context, geometry, hatchPen, 7.0);
            context.DrawGeometry(null, borderPen, geometry);
        }

        context.Pop();
    }

    private static void DrawEdgeMaskingArea(DrawingContext context, ST_RECIPE_CELL_VIEW_MODEL cell, Rect mapRect, ST_RECIPE_EDGE_MASKING_AREA? edgeMaskingArea)
    {
        if (edgeMaskingArea is null || cell.PatternLayout.GroupCountX <= 0 || cell.PatternLayout.GroupCountY <= 0 || cell.PatternLayout.GroupPitchMillimeters <= 0.0)
        {
            return;
        }

        var maskedGeometry = BuildEdgeMaskingGeometry(cell, mapRect, edgeMaskingArea);
        if (maskedGeometry is null)
        {
            return;
        }

        if (maskedGeometry.CanFreeze)
        {
            maskedGeometry.Freeze();
        }

        var fillBrush = CreateBrush(82, 251, 146, 60);
        var borderPen = CreatePen(245, 251, 146, 60, 1.4);
        var hatchPen = CreatePen(165, 253, 186, 116, 0.9);
        context.PushClip(new RectangleGeometry(mapRect));
        context.DrawGeometry(fillBrush, null, maskedGeometry);
        DrawMaskingHatches(context, maskedGeometry, hatchPen, 8.0);
        context.DrawGeometry(null, borderPen, maskedGeometry);
        context.Pop();
    }

    private static void DrawEdgeMaskingOutline(DrawingContext context, ST_RECIPE_CELL_VIEW_MODEL cell, Rect mapRect, ST_RECIPE_EDGE_MASKING_AREA? edgeMaskingArea)
    {
        if (edgeMaskingArea is null)
        {
            return;
        }

        var maskedGeometry = BuildEdgeMaskingGeometry(cell, mapRect, edgeMaskingArea);
        if (maskedGeometry is null)
        {
            return;
        }

        context.PushClip(new RectangleGeometry(mapRect));
        context.DrawGeometry(null, CreatePen(255, 251, 146, 60, 1.8), maskedGeometry);
        context.Pop();
    }

    private static Geometry? BuildEdgeMaskingGeometry(ST_RECIPE_CELL_VIEW_MODEL cell, Rect mapRect, ST_RECIPE_EDGE_MASKING_AREA edgeMaskingArea)
    {
        if (edgeMaskingArea.Bounds.Count == 0)
        {
            return null;
        }

        var geometries = new List<Geometry>();
        foreach (var bounds in edgeMaskingArea.Bounds)
        {
            var geometry = BuildEdgeMaskingRectGeometry(cell, mapRect, bounds);
            if (geometry is not null)
            {
                geometries.Add(geometry);
            }
        }

        if (geometries.Count == 0)
        {
            return null;
        }

        Geometry result = geometries[0];
        for (var index = 1; index < geometries.Count; index++)
        {
            result = new CombinedGeometry(GeometryCombineMode.Union, result, geometries[index]);
        }

        return result;
    }

    private static StreamGeometry? BuildEdgeMaskingRectGeometry(ST_RECIPE_CELL_VIEW_MODEL cell, Rect mapRect, Rect bounds)
    {
        if (bounds.IsEmpty ||
            !TryProjectDoeOriginToCellMap(new Point(bounds.Left, bounds.Top), cell.PatternLayout, mapRect, out var topLeft) ||
            !TryProjectDoeOriginToCellMap(new Point(bounds.Right, bounds.Top), cell.PatternLayout, mapRect, out var topRight) ||
            !TryProjectDoeOriginToCellMap(new Point(bounds.Right, bounds.Bottom), cell.PatternLayout, mapRect, out var bottomRight) ||
            !TryProjectDoeOriginToCellMap(new Point(bounds.Left, bounds.Bottom), cell.PatternLayout, mapRect, out var bottomLeft))
        {
            return null;
        }

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(topLeft, true, true);
            geometryContext.PolyLineTo([topRight, bottomRight, bottomLeft], true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry? BuildMaskingAreaGeometry(ST_RECIPE_CELL_VIEW_MODEL cell, Rect mapRect, ST_RECIPE_MASKING_AREA area)
    {
        var corners = new[]
        {
            new Point(area.Bounds.Left, area.Bounds.Top),
            new Point(area.Bounds.Right, area.Bounds.Top),
            new Point(area.Bounds.Right, area.Bounds.Bottom),
            new Point(area.Bounds.Left, area.Bounds.Bottom)
        };
        var points = new Point[corners.Length];
        for (var index = 0; index < corners.Length; index++)
        {
            if (!TryProjectDoeOriginToCellMap(corners[index], cell.PatternLayout, mapRect, out points[index]))
            {
                return null;
            }
        }

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(points[0], true, true);
            geometryContext.PolyLineTo(points.Skip(1).ToArray(), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static bool TryProjectDoeOriginToCellMap(Point localPoint, ST_CELL_PATTERN_LAYOUT patternLayout, Rect mapRect, out Point mapPoint)
    {
        mapPoint = default;
        if (!double.IsFinite(localPoint.X) || !double.IsFinite(localPoint.Y) || patternLayout.GroupCountX <= 0 || patternLayout.GroupCountY <= 0 || !double.IsFinite(patternLayout.GroupPitchMillimeters) || patternLayout.GroupPitchMillimeters <= 0.0 || mapRect.IsEmpty)
        {
            return false;
        }

        var cellWidth = mapRect.Width / patternLayout.GroupCountX;
        var cellHeight = mapRect.Height / patternLayout.GroupCountY;
        if (!double.IsFinite(cellWidth) || !double.IsFinite(cellHeight) || cellWidth <= 0.0 || cellHeight <= 0.0)
        {
            return false;
        }

        var firstDoeCenter = new Point(mapRect.Left + (cellWidth * 0.5), mapRect.Top + (cellHeight * 0.5));
        mapPoint = new Point(firstDoeCenter.X + (localPoint.X / patternLayout.GroupPitchMillimeters * cellWidth), firstDoeCenter.Y + (localPoint.Y / patternLayout.GroupPitchMillimeters * cellHeight));
        return double.IsFinite(mapPoint.X) && double.IsFinite(mapPoint.Y);
    }

    private static void DrawMaskingHatches(DrawingContext context, Geometry geometry, Pen pen, double step)
    {
        var bounds = geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0.0 || bounds.Height <= 0.0)
        {
            return;
        }

        context.PushClip(geometry);
        for (var offset = -bounds.Height; offset < bounds.Width; offset += step)
        {
            context.DrawLine(pen, new Point(bounds.Left + offset, bounds.Bottom), new Point(bounds.Left + offset + bounds.Height, bounds.Top));
        }

        context.Pop();
    }

    private static void DrawMaskedShots(DrawingContext context, Rect mapRect, ST_RECIPE_CELL_VIEW_MODEL cell, IReadOnlyList<ST_SHOT_GROUP_EDITOR_ROW> shotGroups)
    {
        if (shotGroups.Count == 0)
        {
            return;
        }

        var maskedShotGroups = shotGroups.Where(shot => shot.IsMasked && !shot.IsChessSkipped).ToArray();
        var chessShotGroups = shotGroups.Where(shot => shot.IsChessSkipped).ToArray();
        if (maskedShotGroups.Length == 0 && chessShotGroups.Length == 0)
        {
            return;
        }

        var cellWidth = mapRect.Width / cell.PatternLayout.GroupCountX;
        var cellHeight = mapRect.Height / cell.PatternLayout.GroupCountY;
        var radius = Math.Clamp(Math.Min(cellWidth, cellHeight) * 0.58, 3.2, 7.0);
        var maskedFillBrush = CreateBrush(115, 248, 113, 113);
        var maskedPen = CreatePen(255, 248, 113, 113, 1.5);
        var chessFillBrush = CreateBrush(90, 192, 132, 252);
        var chessPen = CreatePen(255, 192, 132, 252, 1.5);
        const int maximumMaskedPreviewMarkers = 12_000;
        context.PushClip(new RectangleGeometry(mapRect));
        DrawShotMarkers(maskedShotGroups, maskedFillBrush, maskedPen);
        DrawShotMarkers(chessShotGroups, chessFillBrush, chessPen);
        context.Pop();

        void DrawShotMarkers(IReadOnlyList<ST_SHOT_GROUP_EDITOR_ROW> groups, Brush fillBrush, Pen pen)
        {
            var sampleStep = groups.Count <= maximumMaskedPreviewMarkers ? 1 : (int)Math.Ceiling(groups.Count / (double)maximumMaskedPreviewMarkers);
            for (var index = 0; index < groups.Count; index += sampleStep)
            {
                var point = ToShotMapPoint(mapRect, cell, groups[index]);
                context.DrawEllipse(fillBrush, null, point, radius * 0.58, radius * 0.58);
                context.DrawLine(pen, new Point(point.X - radius, point.Y - radius), new Point(point.X + radius, point.Y + radius));
                context.DrawLine(pen, new Point(point.X - radius, point.Y + radius), new Point(point.X + radius, point.Y - radius));
            }

        }
    }

    private static void DrawSelectedShot(DrawingContext context, Rect mapRect, ST_RECIPE_CELL_VIEW_MODEL cell, ST_SHOT_GROUP_EDITOR_ROW? selectedGroup)
    {
        if (selectedGroup is null)
        {
            return;
        }

        var point = ToShotMapPoint(mapRect, cell, selectedGroup);
        var x = Math.Clamp(point.X, mapRect.Left, mapRect.Right);
        var y = Math.Clamp(point.Y, mapRect.Top, mapRect.Bottom);
        var markerBrush = selectedGroup.IsChessSkipped
            ? CreateBrush(255, 192, 132, 252)
            : selectedGroup.IsMasked || !selectedGroup.IsInsideGlass ? CreateBrush(255, 248, 113, 113) : CreateBrush(255, 251, 191, 36);
        var markerPen = new Pen(markerBrush, 2.0);
        markerPen.Freeze();
        context.DrawEllipse(CreateBrush(150, 17, 24, 32), markerPen, new Point(x, y), 8.0, 8.0);
        context.DrawLine(markerPen, new Point(x - 12.0, y), new Point(x + 12.0, y));
        context.DrawLine(markerPen, new Point(x, y - 12.0), new Point(x, y + 12.0));
    }

    private static Point ToShotMapPoint(Rect mapRect, ST_RECIPE_CELL_VIEW_MODEL cell, ST_SHOT_GROUP_EDITOR_ROW shotGroup)
    {
        var countX = cell.PatternLayout.GroupCountX;
        var countY = cell.PatternLayout.GroupCountY;
        var cellWidth = mapRect.Width / countX;
        var cellHeight = mapRect.Height / countY;
        var groupPitch = cell.PatternLayout.GroupPitchMillimeters;
        var offsetScaleX = groupPitch > 0.0 ? cellWidth / groupPitch : 0.0;
        var offsetScaleY = groupPitch > 0.0 ? cellHeight / groupPitch : 0.0;
        return new Point(mapRect.Left + ((shotGroup.Column - 0.5) * cellWidth) + ((shotGroup.CorrectedCenterX - shotGroup.BaseCenterX) * offsetScaleX), mapRect.Top + ((shotGroup.Row - 0.5) * cellHeight) + ((shotGroup.CorrectedCenterY - shotGroup.BaseCenterY) * offsetScaleY));
    }

    private static bool IsLandscape(ST_CELL_PATTERN_LAYOUT patternLayout)
    {
        return patternLayout.PixelCountX > patternLayout.PixelCountY;
    }

    private static Rect GetCellMapRect(ST_CELL_PATTERN_LAYOUT patternLayout)
    {
        return IsLandscape(patternLayout) ? CellMapLandscapeRect : CellMapPortraitRect;
    }

    private static Rect GetProcessedMapRect(ST_CELL_PATTERN_LAYOUT shotGroups, Rect cellMapRect)
    {
        if (shotGroups.PixelCountX > 0 && shotGroups.PixelCountY > 0 && shotGroups.ProcessedPixelCountX > 0 && shotGroups.ProcessedPixelCountY > 0)
        {
            var processedPixelRatioX = Math.Clamp(shotGroups.ProcessedPixelCountX / (double)shotGroups.PixelCountX, 0.0, 1.0);
            var processedPixelRatioY = Math.Clamp(shotGroups.ProcessedPixelCountY / (double)shotGroups.PixelCountY, 0.0, 1.0);
            return new Rect(cellMapRect.Left, cellMapRect.Top, cellMapRect.Width * processedPixelRatioX, cellMapRect.Height * processedPixelRatioY);
        }

        if (shotGroups.TargetCountX <= 0 || shotGroups.TargetCountY <= 0 || shotGroups.ProcessedTargetCountX <= 0 || shotGroups.ProcessedTargetCountY <= 0)
        {
            return cellMapRect;
        }

        var processedRatioX = Math.Clamp(shotGroups.ProcessedTargetCountX / (double)shotGroups.TargetCountX, 0.0, 1.0);
        var processedRatioY = Math.Clamp(shotGroups.ProcessedTargetCountY / (double)shotGroups.TargetCountY, 0.0, 1.0);
        return new Rect(cellMapRect.Left, cellMapRect.Top, cellMapRect.Width * processedRatioX, cellMapRect.Height * processedRatioY);
    }

    private static void DrawExcludedTargetEdges(DrawingContext context, ST_CELL_PATTERN_LAYOUT shotGroups, Rect cellMapRect, Rect processedMapRect)
    {
        if (!shotGroups.HasExcludedTargets)
        {
            return;
        }

        var boundaryPen = CreatePen(155, 100, 116, 139, 1.0);
        if (shotGroups.ExcludedTargetCountX > 0)
        {
            context.DrawLine(boundaryPen, new Point(processedMapRect.Right, cellMapRect.Top), new Point(processedMapRect.Right, cellMapRect.Bottom));
        }

        if (shotGroups.ExcludedTargetCountY > 0)
        {
            context.DrawLine(boundaryPen, new Point(cellMapRect.Left, processedMapRect.Bottom), new Point(cellMapRect.Right, processedMapRect.Bottom));
        }
    }

    private static void DrawBeamPitchGrid(DrawingContext context, double beamPitch, int splitBeamCount, double domainMinimum, double domainMaximum)
    {
        var pitchPen = CreatePen(105, 100, 116, 139, 0.8);
        for (var index = 0; index <= splitBeamCount; index++)
        {
            var coordinate = domainMinimum + (index * beamPitch);
            var verticalStart = ToDetailPoint(coordinate, domainMinimum, domainMinimum, domainMaximum);
            var verticalEnd = ToDetailPoint(coordinate, domainMaximum, domainMinimum, domainMaximum);
            context.DrawLine(pitchPen, verticalStart, verticalEnd);
            var horizontalStart = ToDetailPoint(domainMinimum, coordinate, domainMinimum, domainMaximum);
            var horizontalEnd = ToDetailPoint(domainMaximum, coordinate, domainMinimum, domainMaximum);
            context.DrawLine(pitchPen, horizontalStart, horizontalEnd);
        }
    }

    private static void DrawPixelLattice(DrawingContext context, int beamSpan, int processPitch, int splitBeamCount, double domainMinimum, double domainMaximum)
    {
        var pixelPointCount = beamSpan + 1;
        var sampleStep = Math.Max(1, (int)Math.Ceiling(pixelPointCount / 72.0));
        var pointSize = pixelPointCount <= 48 ? 1.6 : 1.1;
        var displayedPixelIndices = new SortedSet<int>();
        for (var index = 0; index <= beamSpan; index += sampleStep)
        {
            displayedPixelIndices.Add(index);
        }

        displayedPixelIndices.Add(beamSpan);
        for (var beamIndex = 0; beamIndex < splitBeamCount; beamIndex++)
        {
            displayedPixelIndices.Add(beamIndex * processPitch);
        }

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            foreach (var row in displayedPixelIndices)
            {
                foreach (var column in displayedPixelIndices)
                {
                    var point = ToDetailPoint(column, row, domainMinimum, domainMaximum);
                    AddCircle(geometryContext, point.X, point.Y, pointSize);
                }
            }
        }

        geometry.Freeze();
        context.DrawGeometry(CreateBrush(190, 100, 116, 139), null, geometry);
    }

    private static void DrawDoeBeams(DrawingContext context, double beamPitch, int splitBeamCount, double domainMinimum, double domainMaximum, IReadOnlyList<int> maskedBeamNumbers)
    {
        var maskedBeams = maskedBeamNumbers.Count == 0 ? new HashSet<int>() : maskedBeamNumbers.ToHashSet();
        var beamBrush = CreateBrush(128, 34, 197, 94);
        var beamPen = new Pen(CreateBrush(255, 134, 239, 172), 1.2);
        var maskedBeamBrush = CreateBrush(178, 248, 113, 113);
        var maskedBeamPen = new Pen(CreateBrush(255, 254, 202, 202), 1.7);
        var beamTextBrush = CreateBrush(255, 255, 255, 255);
        var targetPixelBrush = CreateBrush(255, 148, 163, 184);
        var markerRadius = splitBeamCount == 1 ? 13.0 : 10.5;
        var markerFontSize = splitBeamCount == 1 ? 9.0 : 7.5;
        var labelOffsetY = splitBeamCount == 1 ? -6.0 : -5.0;
        beamPen.Freeze();
        maskedBeamPen.Freeze();
        var centerIndex = (splitBeamCount - 1) / 2.0;
        for (var row = 0; row < splitBeamCount; row++)
        {
            for (var column = 0; column < splitBeamCount; column++)
            {
                var point = ToDetailPoint((column - centerIndex) * beamPitch, (row - centerIndex) * beamPitch, domainMinimum, domainMaximum);
                var beamNo = (row * splitBeamCount) + column + 1;
                var isMasked = maskedBeams.Contains(beamNo);
                context.DrawEllipse(isMasked ? maskedBeamBrush : beamBrush, isMasked ? maskedBeamPen : beamPen, point, markerRadius, markerRadius);
                context.DrawEllipse(targetPixelBrush, null, point, 2.0, 2.0);
                DrawCenteredText(context, beamNo.ToString(CultureInfo.InvariantCulture), new Point(point.X, point.Y + labelOffsetY), markerFontSize, beamTextBrush);
            }
        }
    }

    private static void DrawLaserOffStamp(DrawingContext context, IReadOnlyList<int> maskedBeamNumbers)
    {
        var stampRect = new Rect(ShotDetailRect.Left + 10.0, ShotDetailRect.Bottom - 44.0, ShotDetailRect.Width - 20.0, 34.0);
        var fillBrush = CreateBrush(205, 127, 29, 29);
        var borderPen = CreatePen(255, 248, 113, 113, 1.5);
        context.DrawRectangle(fillBrush, borderPen, stampRect);
        DrawCenteredText(context, "LASER OFF", new Point(stampRect.Left + (stampRect.Width / 2.0), stampRect.Top + 8.0), 12.0, CreateBrush(255, 255, 255, 255));
        var beamText = maskedBeamNumbers.Count == 0 ? "MASKED" : $"MASKED {FormatBeamList(maskedBeamNumbers)}";
        DrawCenteredText(context, beamText, new Point(stampRect.Left + (stampRect.Width / 2.0), stampRect.Top + 24.0), 8.0, CreateBrush(255, 254, 226, 226));
    }

    private static string FormatBeamList(IReadOnlyList<int> beamNumbers)
    {
        var ordered = beamNumbers.Distinct().OrderBy(number => number).ToArray();
        if (ordered.Length == 0)
        {
            return "BEAM";
        }

        var ranges = new List<string>();
        var start = ordered[0];
        var previous = ordered[0];
        for (var index = 1; index < ordered.Length; index++)
        {
            var current = ordered[index];
            if (current == previous + 1)
            {
                previous = current;
                continue;
            }

            ranges.Add(start == previous ? start.ToString(CultureInfo.InvariantCulture) : $"{start}-{previous}");
            start = current;
            previous = current;
        }

        ranges.Add(start == previous ? start.ToString(CultureInfo.InvariantCulture) : $"{start}-{previous}");
        return $"BEAM {string.Join(",", ranges)}";
    }

    private static Point ToDetailPoint(double x, double y, double domainMinimum, double domainMaximum)
    {
        var domainSize = Math.Max(double.Epsilon, domainMaximum - domainMinimum);
        return new Point(ShotDetailRect.Left + ((x - domainMinimum) / domainSize * ShotDetailRect.Width), ShotDetailRect.Top + ((y - domainMinimum) / domainSize * ShotDetailRect.Height));
    }

    private static void DrawAxes(DrawingContext context, Rect rect)
    {
        var axisPen = CreatePen(255, 226, 232, 240, 1.4);
        var origin = new Point(rect.Left, rect.Bottom + 20.0);
        var xEnd = new Point(origin.X + 42.0, origin.Y);
        var yEnd = new Point(origin.X, origin.Y + 18.0);
        context.DrawLine(axisPen, origin, xEnd);
        context.DrawLine(axisPen, new Point(xEnd.X - 7.0, xEnd.Y - 4.0), xEnd);
        context.DrawLine(axisPen, new Point(xEnd.X - 7.0, xEnd.Y + 4.0), xEnd);
        context.DrawLine(axisPen, origin, yEnd);
        context.DrawLine(axisPen, new Point(yEnd.X - 4.0, yEnd.Y - 7.0), yEnd);
        context.DrawLine(axisPen, new Point(yEnd.X + 4.0, yEnd.Y - 7.0), yEnd);
        var axisBrush = CreateBrush(255, 226, 232, 240);
        DrawText(context, "X+", new Point(xEnd.X + 4.0, xEnd.Y - 9.0), 11.0, axisBrush);
        DrawText(context, "Y+", new Point(yEnd.X + 5.0, yEnd.Y - 8.0), 11.0, axisBrush);
    }

    private static void DrawText(DrawingContext context, string text, Point point, double fontSize, Brush brush)
    {
        var formattedText = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), fontSize, brush, 1.0);
        context.DrawText(formattedText, point);
    }

    private static void DrawCenteredText(DrawingContext context, string text, Point center, double fontSize, Brush brush)
    {
        var formattedText = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), fontSize, brush, 1.0);
        context.DrawText(formattedText, new Point(center.X - (formattedText.Width / 2.0), center.Y - (formattedText.Height / 2.0)));
    }

    private static Pen CreatePen(byte alpha, byte red, byte green, byte blue, double thickness)
    {
        var pen = new Pen(CreateBrush(alpha, red, green, blue), thickness);
        pen.Freeze();
        return pen;
    }

    private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static void AddCircle(StreamGeometryContext context, double x, double y, double size)
    {
        var radius = size / 2.0;
        var control = radius * 0.5522847498;
        context.BeginFigure(new Point(x + radius, y), true, true);
        context.BezierTo(new Point(x + radius, y + control), new Point(x + control, y + radius), new Point(x, y + radius), true, false);
        context.BezierTo(new Point(x - control, y + radius), new Point(x - radius, y + control), new Point(x - radius, y), true, false);
        context.BezierTo(new Point(x - radius, y - control), new Point(x - control, y - radius), new Point(x, y - radius), true, false);
        context.BezierTo(new Point(x + control, y - radius), new Point(x + radius, y - control), new Point(x + radius, y), true, false);
    }
}
