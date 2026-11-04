using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Drilling.Common.Managers;
using Drilling.Common.Recipe;

namespace Drilling.UI.Menu.Menus;

internal static class CHeadAssignmentPreviewBuilder
{
    private const double MinimumPreviewCellAreaWidth = 18.0;
    private const double MinimumPreviewCellAreaHeight = 32.0;
    internal static IReadOnlyList<int> BuildEnabledHeadNumbers(IReadOnlyDictionary<string, string> parameters, int headCount = 8)
    {
        var normalizedHeadCount = Math.Clamp(headCount, 1, 8);
        var heads = new List<int>(normalizedHeadCount);
        for (var headNo = 1; headNo <= normalizedHeadCount; headNo++)
        {
            if (IsHeadEnabled(parameters, headNo))
            {
                heads.Add(headNo);
            }
        }

        return heads;
    }

    private static bool IsHeadEnabled(IReadOnlyDictionary<string, string> parameters, int headNo)
    {
        return parameters.TryGetValue($"H{headNo:00}_USE", out var value) ? ReadBoolOption(value, true) : true;
    }

    private static bool ReadBoolOption(string value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "1" or "Y" or "YES" or "TRUE" or "ON" or "USE" => true,
            "0" or "N" or "NO" or "FALSE" or "OFF" or "SKIP" or "UNUSE" => false,
            _ => defaultValue
        };
    }

    private static int ReadIntAny(IReadOnlyDictionary<string, string> parameters, int defaultValue, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parameters.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    private static double ReadDoubleAny(IReadOnlyDictionary<string, string> parameters, double defaultValue, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (parameters.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    internal static ST_MAIN_RECIPE_PREVIEW BuildRecipePreview(
        IReadOnlyDictionary<string, string> parameters, 
        IReadOnlySet<int> selectedHeadNos, 
        ST_PREVIEW_HEAD_LAYOUT headLayout, 
        bool showKeys = true, 
        IReadOnlySet<int>? visibleCellNos = null
    )
    {
        const double canvasWidth = 860.0;
        const double canvasHeight = 520.0;
        var frame = CreateGlassFrame(parameters);
        var glassWidth = ReadDoubleAny(parameters, 0.0, "GLASS_SIZE_X");
        var glassHeight = ReadDoubleAny(parameters, 0.0, "GLASS_SIZE_Y");
        var akMarginX = ReadDoubleAny(parameters, 55.0, "AK_MARGIN_X");
        var akMarginY = ReadDoubleAny(parameters, 45.0, "AK_MARGIN_Y");
        if (!double.IsFinite(glassWidth) || !double.IsFinite(glassHeight) || glassWidth <= 0.0 || glassHeight <= 0.0)
        {
            return CreateRecipePreviewError(frame, $"GLASS_SIZE_X/Y must be finite values greater than 0. " + $"Current value: {glassWidth} x {glassHeight}");
        }

        if (!double.IsFinite(akMarginX) || !double.IsFinite(akMarginY))
        {
            return CreateRecipePreviewError(frame, $"AK_MARGIN_X/Y must be finite values. Current value: {akMarginX} x {akMarginY}");
        }

        var cellCountText = parameters.TryGetValue("MAX_CELL_NUMBER", out var configuredCellCount) ? configuredCellCount : "1";
        if (!CCellPatternCalculator.TryParseCellCount(cellCountText, out var cellCount, out var cellCountValidationMessage))
        {
            return CreateRecipePreviewError(frame, cellCountValidationMessage);
        }

        double? SelectValue(string key)
        {
            return ReadNullableDoubleAny(parameters, key);
        }

        var distortionKeys = CCellPreviewDrawing.CreateDistortionKeyPreviews(glassWidth, glassHeight, akMarginX, akMarginY, SelectValue);
        var pixelSize = ReadDoubleAny(parameters, 0.0, "PIXEL_SIZE");
        var splitBeamCount = ReadIntAny(parameters, 0, "SPLITED_BEAM_COUNT");
        var modelTypeCount = CRecipeModelPixelCountReader.ReadModelTypeCount(parameters);
        var scannerReferenceOffsetX = ReadDoubleAny(parameters, 0.0, "ESC_EDGE_TO_GLASS_EDGE", "ESC_EDGE_TO_GLASS_EDGE_X") + ReadDoubleAny(parameters, 0.0, "GLASS_CENTER_TO_SCANNER_CENTER", "GLASS_CENTER_TO_SCANNER_CENTER_X");
        const int headCount = 8;
        var activeHeadNos = BuildEnabledHeadNumbers(parameters, headCount);
        var activeHeadSet = activeHeadNos.ToHashSet();
        var assignmentHeadNos = Enumerable.Range(1, headCount).ToArray();
        var scale = Math.Min(frame.Width / glassWidth, frame.Height / glassHeight);
        if (!double.IsFinite(scale) || scale <= 0.0)
        {
            return CreateRecipePreviewError(frame, $"Preview scale must be a finite value greater than 0. Current value: {scale}");
        }

        var drawing = new DrawingGroup();
        var frameBounds = new Rect(frame.CanvasLeft, frame.CanvasTop, frame.Width, frame.Height);
        int SelectHeadNo(int headNo)
        {
            return headNo;
        }

        long SelectLong(int arg0)
        {
            return 0L;
        }

        var headPointCounts = activeHeadNos.ToDictionary(SelectHeadNo, SelectLong);
        var labels = new List<ST_CELL_PREVIEW_LABEL>();
        var drawnCellBoundaries = new List<(StreamGeometry Boundary, Pen Pen)>();
        long unassignedPointCount = 0;
        long totalShotGroups = 0;
        using (var context = drawing.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0, 0, canvasWidth, canvasHeight));
            context.PushClip(new RectangleGeometry(frameBounds));
            for (var cellNo = 1; cellNo <= cellCount; cellNo++)
            {
                if (visibleCellNos is not null && !visibleCellNos.Contains(cellNo)) continue;
                var firstX = ReadDoubleAny(parameters, 0.0, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_X");
                var firstY = ReadDoubleAny(parameters, 0.0, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_Y");
                var rotation = ReadDoubleAny(parameters, 0.0, $"CELL{cellNo}_ROTATION");
                var modelType = CRecipeModelPixelCountReader.ReadCellModelType(parameters, cellNo, modelTypeCount);
                var modelPattern = CRecipeModelPixelCountReader.ReadPatternForModel(parameters, modelType);
                var result = CCellPatternCalculator.Calculate(new ST_CELL_PATTERN_DEFINITION(cellNo, firstX, firstY, rotation, pixelSize, modelPattern.PixelCountX, modelPattern.PixelCountY, modelPattern.ProcessPitch, splitBeamCount, akMarginX, akMarginY));
                if (!result.IsValid)
                {
                    return CreateRecipePreviewError(frame, result.ValidationMessage);
                }

                var nextTotalShotGroups = totalShotGroups + result.ShotCenters.Count;
                if (nextTotalShotGroups > CCellPatternCalculator.MaxTotalShotGroupCount)
                {
                    return CreateRecipePreviewError(frame, $"Shot Center count {nextTotalShotGroups:N0} exceeds the total limit " + $"{CCellPatternCalculator.MaxTotalShotGroupCount:N0}.");
                }

                totalShotGroups = nextTotalShotGroups;
                var radius = result.BeamHalfSpanMillimeters + (pixelSize / 2.0);
                if (!double.IsFinite(radius) || radius < 0.0)
                {
                    return CreateRecipePreviewError(frame, $"Cell{cellNo} preview radius must be a finite value greater than or equal to 0. " + $"Current value: {radius}");
                }

                var cellHeadCounts = activeHeadNos.ToDictionary(SelectHeadNo, SelectLong);
                foreach (var shotCenter in result.ShotCenters)
                {
                    var inside = shotCenter.DesignX - radius >= 0 && shotCenter.DesignX + radius <= glassWidth && shotCenter.DesignY - radius >= 0 && shotCenter.DesignY + radius <= glassHeight;
                    if (!inside)
                    {
                        continue;
                    }

                    var scannerReferenceX = shotCenter.DesignX + scannerReferenceOffsetX;
                    var headNo = AssignPreviewHead(scannerReferenceX, assignmentHeadNos, headLayout, akMarginX, headPointCounts);
                    if (headNo <= 0 || !activeHeadSet.Contains(headNo))
                    {
                        unassignedPointCount++;
                        continue;
                    }

                    headPointCounts[headNo]++;
                    cellHeadCounts[headNo]++;
                }

                var boundaryPadding = pixelSize / 2.0;
                if (!double.IsFinite(boundaryPadding) || boundaryPadding < 0.0 || !TryBuildRecipeCellBoundary(akMarginX + firstX, akMarginY + firstY, rotation, modelPattern.PixelCountX, modelPattern.PixelCountY, pixelSize, pixelSize, boundaryPadding, frame, scale, out var boundary))
                {
                    return CreateRecipePreviewError(frame, $"Cell{cellNo} boundary produces an invalid preview coordinate.");
                }

                var boundaryBounds = boundary.Bounds;
                var displayBounds = ExpandPreviewBounds(boundaryBounds, MinimumPreviewCellAreaWidth, MinimumPreviewCellAreaHeight, frameBounds);
                var useDisplayBounds = ShouldUseExpandedPreviewBounds(boundaryBounds, displayBounds);
                var cellHeadNo = ResolveCellPreviewHead(cellHeadCounts, activeHeadNos, cellNo);
                var isSelectedHead = cellHeadNo <= 0 || selectedHeadNos.Count == 0 || selectedHeadNos.Contains(cellHeadNo);
                var fillBrush = cellHeadNo <= 0
                    ? CreateBrush(230, 33, 33, isSelectedHead ? (byte)120 : (byte)55)
                    : CreateHeadBrush(cellHeadNo, isSelectedHead ? (byte)145 : (byte)62);
                var borderBrush = cellHeadNo <= 0
                    ? CreateBrush(176, 0, 0, isSelectedHead ? (byte)240 : (byte)120)
                    : CreateHeadBrush(cellHeadNo, isSelectedHead ? (byte)250 : (byte)135);
                var boundaryPen = new Pen(borderBrush, isSelectedHead ? 2.0 : 1.25);
                boundaryPen.Freeze();
                context.DrawGeometry(fillBrush, null, boundary);
                context.DrawGeometry(null, boundaryPen, boundary);
                drawnCellBoundaries.Add((boundary, boundaryPen));

                // Filtered previews have transparent hit targets centered on the actual Cell.
                var label = CCellPreviewDrawing.CreateCellLabel(cellNo, visibleCellNos is null && useDisplayBounds ? displayBounds : boundaryBounds, canvasWidth, canvasHeight);
                if (label is not null)
                {
                    labels.Add(label with { CellBounds = boundaryBounds });
                }
            }

            foreach (var drawnCell in drawnCellBoundaries)
            {
                context.DrawGeometry(null, drawnCell.Pen, drawnCell.Boundary);
            }

            context.Pop();
            if (showKeys)
            {
                CCellPreviewDrawing.DrawAlignKeys(context, frame, glassWidth, glassHeight, akMarginX, akMarginY);
                CCellPreviewDrawing.DrawDistortionKeys(context, frame, glassWidth, glassHeight, distortionKeys);
            }
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return new ST_MAIN_RECIPE_PREVIEW(image, frame, headPointCounts, totalShotGroups, unassignedPointCount, labels);
    }

    private static ST_MAIN_RECIPE_PREVIEW CreateRecipePreviewError(ST_GLASS_PREVIEW_FRAME frame, string message)
    {
        return new ST_MAIN_RECIPE_PREVIEW(null, frame, new Dictionary<int, long>(), 0, 0, [], $"PREVIEW ERROR / {message}");
    }

    private static int ResolveCellPreviewHead(IReadOnlyDictionary<int, long> cellHeadCounts, IReadOnlyList<int> activeHeadNos, int cellNo)
    {
        var selectedHeadNo = 0;
        var selectedCount = 0L;
        foreach (var headNo in activeHeadNos)
        {
            var count = cellHeadCounts.TryGetValue(headNo, out var value) ? value : 0L;
            if (count <= selectedCount)
            {
                continue;
            }

            selectedHeadNo = headNo;
            selectedCount = count;
        }

        if (selectedHeadNo > 0)
        {
            return selectedHeadNo;
        }

        return 0;
    }

    private static bool ShouldUseExpandedPreviewBounds(Rect actualBounds, Rect displayBounds)
    {
        return !actualBounds.IsEmpty && (Math.Abs(actualBounds.Width - displayBounds.Width) > 0.001 || Math.Abs(actualBounds.Height - displayBounds.Height) > 0.001);
    }

    private static Rect ExpandPreviewBounds(Rect bounds, double minimumWidth, double minimumHeight, Rect limitBounds)
    {
        var left = ClampPreviewCoordinate(bounds.Left, limitBounds.Left, limitBounds.Right);
        var top = ClampPreviewCoordinate(bounds.Top, limitBounds.Top, limitBounds.Bottom);
        var maximumWidth = Math.Max(1.0, limitBounds.Right - left);
        var maximumHeight = Math.Max(1.0, limitBounds.Bottom - top);
        var width = Math.Min(Math.Max(bounds.Width, minimumWidth), maximumWidth);
        var height = Math.Min(Math.Max(bounds.Height, minimumHeight), maximumHeight);
        return new Rect(left, top, width, height);
    }

    private static double ClampPreviewCoordinate(double value, double minimum, double maximum)
    {
        var safeMaximum = Math.Max(minimum, maximum);
        return Math.Clamp(value, minimum, safeMaximum);
    }

    private static int AssignPreviewHead(double x, IReadOnlyList<int> assignmentHeadNos, ST_PREVIEW_HEAD_LAYOUT headLayout, double akMarginX, IReadOnlyDictionary<int, long> assignedHeadCounts)
    {
        if (assignmentHeadNos.Count == 0)
        {
            return 0;
        }

        global::Drilling.UI.Menu.Menus.ST_PREVIEW_HEAD_ASSIGNMENT_CANDIDATE? BuildPreview(int headNo)
        {
            var range = GetPreviewHeadRange(headNo, headLayout, akMarginX);
            if (x < range.StartX || x > range.EndX)
            {
                return (ST_PREVIEW_HEAD_ASSIGNMENT_CANDIDATE? )null;
            }

            return new ST_PREVIEW_HEAD_ASSIGNMENT_CANDIDATE(headNo, Math.Abs(x - range.CenterX));
        }

        bool IsHasValue(global::Drilling.UI.Menu.Menus.ST_PREVIEW_HEAD_ASSIGNMENT_CANDIDATE? candidate)
        {
            return candidate.HasValue;
        }

        global::Drilling.UI.Menu.Menus.ST_PREVIEW_HEAD_ASSIGNMENT_CANDIDATE SelectItem(global::Drilling.UI.Menu.Menus.ST_PREVIEW_HEAD_ASSIGNMENT_CANDIDATE? candidate)
        {
            return candidate!.Value;
        }

        var candidates = assignmentHeadNos.Select(BuildPreview).Where(IsHasValue).Select(SelectItem).ToArray();
        double SelectDistance(global::Drilling.UI.Menu.Menus.ST_PREVIEW_HEAD_ASSIGNMENT_CANDIDATE candidate)
        {
            return candidate.Distance;
        }

        long SelectValue(global::Drilling.UI.Menu.Menus.ST_PREVIEW_HEAD_ASSIGNMENT_CANDIDATE candidate)
        {
            return assignedHeadCounts.TryGetValue(candidate.HeadNo, out var count) ? count : 0;
        }

        int SelectHeadNo(global::Drilling.UI.Menu.Menus.ST_PREVIEW_HEAD_ASSIGNMENT_CANDIDATE candidate)
        {
            return candidate.HeadNo;
        }

        return candidates.Length == 0 ? 0 : candidates.OrderBy(SelectDistance).ThenBy(SelectValue).ThenBy(SelectHeadNo).First().HeadNo;
    }

    private static (double StartX, double EndX, double CenterX) GetPreviewHeadRange(int headNo, ST_PREVIEW_HEAD_LAYOUT headLayout, double akMarginX)
    {
        var field = headLayout.GetField(headNo);
        var centerX = akMarginX + field.PositionX;
        var halfWidth = field.ScanFieldWidthX / 2.0;
        return (centerX - halfWidth, centerX + halfWidth, centerX);
    }

    private static bool TryProjectRecipePreviewPixel(double designX, double designY, ST_GLASS_PREVIEW_FRAME frame, double scale, out double canvasX, out double canvasY, out int pixelX, out int pixelY)
    {
        canvasX = frame.CanvasLeft + (designX * scale);
        canvasY = frame.CanvasTop + (designY * scale);
        pixelX = 0;
        pixelY = 0;
        if (!double.IsFinite(designX) || !double.IsFinite(designY) || !double.IsFinite(scale) || scale <= 0.0 || !double.IsFinite(canvasX) || !double.IsFinite(canvasY))
        {
            return false;
        }

        var roundedX = Math.Round(canvasX);
        var roundedY = Math.Round(canvasY);
        if (!double.IsFinite(roundedX) || !double.IsFinite(roundedY) || roundedX < int.MinValue || roundedX > int.MaxValue || roundedY < int.MinValue || roundedY > int.MaxValue)
        {
            return false;
        }

        pixelX = (int)roundedX;
        pixelY = (int)roundedY;
        return true;
    }

    private static bool TryBuildRecipeCellBoundary(
        double firstX, 
        double firstY, 
        double rotation, 
        int countX, 
        int countY, 
        double pitchX, 
        double pitchY, 
        double padding, 
        ST_GLASS_PREVIEW_FRAME frame, 
        double scale, 
        out StreamGeometry geometry
    )
    {
        var radians = rotation * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var maxX = ((countX - 1) * pitchX) + padding;
        var maxY = ((countY - 1) * pitchY) + padding;
        var localCorners = new[]
        {
            new Point(-padding, -padding),
            new Point(maxX, -padding),
            new Point(maxX, maxY),
            new Point(-padding, maxY)
        };
        var corners = new Point[localCorners.Length];
        for (var index = 0; index < localCorners.Length; index++)
        {
            var local = localCorners[index];
            var x = firstX + (local.X * cos) - (local.Y * sin);
            var y = firstY + (local.X * sin) + (local.Y * cos);
            if (!TryProjectRecipePreviewPixel(x, y, frame, scale, out var canvasX, out var canvasY, out _, out _))
            {
                geometry = new StreamGeometry();
                return false;
            }

            corners[index] = new Point(canvasX, canvasY);
        }

        geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(corners[0], false, true);
            context.PolyLineTo(corners.Skip(1).ToArray(), true, false);
        }

        geometry.Freeze();
        return true;
    }

    internal static ST_HEAD_ASSIGNMENT_MAP BuildHeadAssignmentMap(IReadOnlyDictionary<string, string> parameters, IReadOnlySet<int> selectedHeadNos, ST_PREVIEW_HEAD_LAYOUT headLayout)
    {
        const int headCount = 8;
        var frame = CreateGlassFrame(parameters);
        var glassWidth = ReadDoubleAny(parameters, 0.0, "GLASS_SIZE_X");
        var akMarginX = ReadDoubleAny(parameters, 55.0, "AK_MARGIN_X");
        var activeHeadNos = BuildEnabledHeadNumbers(parameters, headCount);
        var areas = new List<ST_HEAD_ASSIGNMENT_AREA>(activeHeadNos.Count);
        (double Left, double Top, double Width, double Height, double LabelLeft, double LabelWidth) GetHeadRect(int headNo)
        {
            if (!double.IsFinite(glassWidth) || !double.IsFinite(akMarginX) || glassWidth <= 0.0)
            {
                return (frame.CanvasLeft, frame.CanvasTop, 0.0, frame.Height, frame.CanvasLeft, 0.0);
            }

            var scale = frame.Width / glassWidth;
            if (!double.IsFinite(scale) || scale <= 0.0)
            {
                return (frame.CanvasLeft, frame.CanvasTop, 0.0, frame.Height, frame.CanvasLeft, 0.0);
            }

            var range = GetPreviewHeadRange(headNo, headLayout, akMarginX);
            // A Scan Field belongs to the fixed Head, not to the Glass. Draw its full
            // physical width even when part of the field lies outside the Glass frame.
            var left = frame.CanvasLeft + (range.StartX * scale);
            var top = frame.CanvasTop;
            var width = Math.Max(0.0, (range.EndX - range.StartX) * scale);
            var centerX = akMarginX + headLayout.GetField(headNo).PositionX;
            var centerCanvasX = frame.CanvasLeft + (centerX * scale);
            var labelWidth = Math.Clamp((range.EndX - range.StartX) * scale, 36.0, 96.0);
            var labelLeft = Math.Clamp(centerCanvasX - (labelWidth / 2.0), 0.0, 860.0 - labelWidth);
            return (left, top, width, Math.Max(4.0, frame.Height), labelLeft, labelWidth);
        }

        foreach (var headNo in activeHeadNos)
        {
            var isSelected = selectedHeadNos.Contains(headNo);
            var hasSelectedHead = selectedHeadNos.Count > 0;
            var fillAlpha = hasSelectedHead ? isSelected ? (byte)48 : (byte)8 : (byte)18;
            var strokeAlpha = hasSelectedHead ? isSelected ? (byte)255 : (byte)105 : (byte)180;
            var rect = GetHeadRect(headNo);
            areas.Add(new ST_HEAD_ASSIGNMENT_AREA(
                headNo, 
                $"HEAD {headNo:00}", 
                rect.Left, 
                rect.Top, 
                rect.Width, 
                rect.Height, 
                rect.LabelLeft, 
                rect.LabelWidth, 
                isSelected, 
                0, 
                CreateHeadBrush(headNo, fillAlpha), 
                CreateHeadBrush(headNo, strokeAlpha), 
                new Thickness(isSelected ? 2.3 : 1.1), 
                1.0
            ));
        }

        return new ST_HEAD_ASSIGNMENT_MAP(areas);
    }

    private static double ReadHeadPositionX(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo)
    {
        const double fallbackHead1PositionX = -5.0;
        const double fallbackHeadGapX = 200.0;
        var head1PositionX = ReadSettingDouble(settings, fallbackHead1PositionX, "H01_AK_POSITION_X");
        var headGapX = ReadSettingDouble(settings, fallbackHeadGapX, "HeadGapX");
        return head1PositionX + ((headNo - 1) * headGapX);
    }

    private static double ReadSettingDouble(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, double defaultValue, params string[] keys)
    {
        foreach (var setting in settings)
        {
            bool MatchesItem(string key)
            {
                return key.Equals(setting.Key, StringComparison.OrdinalIgnoreCase) || key.Equals(setting.Name, StringComparison.OrdinalIgnoreCase);
            }

            if (!keys.Any(MatchesItem))
            {
                continue;
            }

            if (double.TryParse(setting.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    private static double? ReadNullableDoubleAny(IReadOnlyDictionary<string, string> parameters, string key)
    {
        return parameters.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static ST_GLASS_PREVIEW_FRAME CreateGlassFrame(IReadOnlyDictionary<string, string> parameters)
    {
        const double maxLeft = 44.0;
        const double maxTop = 62.0;
        const double maxWidth = 772.0;
        const double maxHeight = 420.0;
        var glassSizeX = ReadDoubleAny(parameters, 0.0, "GLASS_SIZE_X");
        var glassSizeY = ReadDoubleAny(parameters, 0.0, "GLASS_SIZE_Y");
        if (!double.IsFinite(glassSizeX) || !double.IsFinite(glassSizeY) || glassSizeX <= 0.0 || glassSizeY <= 0.0)
        {
            return new ST_GLASS_PREVIEW_FRAME(maxLeft, maxTop, maxWidth, maxHeight);
        }

        var scale = Math.Min(maxWidth / glassSizeX, maxHeight / glassSizeY);
        var width = glassSizeX * scale;
        var height = glassSizeY * scale;
        var left = maxLeft + (maxWidth - width) / 2.0;
        var top = maxTop + (maxHeight - height) / 2.0;
        if (!double.IsFinite(scale) || scale <= 0.0 || !double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(left) || !double.IsFinite(top) || width <= 0.0 || height <= 0.0)
        {
            return new ST_GLASS_PREVIEW_FRAME(maxLeft, maxTop, maxWidth, maxHeight);
        }

        return new ST_GLASS_PREVIEW_FRAME(left, top, width, height);
    }

    internal static string FormatGlassSizeText(IReadOnlyDictionary<string, string> parameters)
    {
        var glassSizeX = ReadDoubleAny(parameters, 0.0, "GLASS_SIZE_X");
        var glassSizeY = ReadDoubleAny(parameters, 0.0, "GLASS_SIZE_Y");
        return double.IsFinite(glassSizeX) && double.IsFinite(glassSizeY) && glassSizeX > 0.0 && glassSizeY > 0.0 ? $"Glass {glassSizeX:0.#} x {glassSizeY:0.#} mm" : "Glass size fallback";
    }

    public static Brush CreateHeadBrush(int headNo, byte alpha = 255)
    {
        var palette = new[]
        {
            Color.FromRgb(96, 132, 164),
            Color.FromRgb(105, 150, 126),
            Color.FromRgb(161, 132, 83),
            Color.FromRgb(151, 105, 123),
            Color.FromRgb(95, 142, 140),
            Color.FromRgb(131, 123, 164),
            Color.FromRgb(151, 116, 86),
            Color.FromRgb(92, 119, 156)
        };
        var color = palette[Math.Clamp(headNo, 1, palette.Length) - 1];
        return CreateBrush(color.R, color.G, color.B, alpha);
    }

    private static Brush CreateBrush(byte red, byte green, byte blue, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    internal static ST_PREVIEW_HEAD_LAYOUT BuildHeadLayout(IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
    {
        global::Drilling.UI.Menu.Menus.ST_PREVIEW_HEAD_FIELD BuildPreview(int headNo)
        {
            var position = ReadHeadPositionX(settings, headNo);
            const double fallbackWidthX = 110.0;
            var widthX = ReadSettingDouble(settings, fallbackWidthX, $"H{headNo:00}_SCAN_FIELD_WIDTH_X", $"H{headNo:00}_HEAD_FIELD_WIDTH_X");
            return new ST_PREVIEW_HEAD_FIELD(headNo, position, widthX > 0.0 ? widthX : fallbackWidthX);
        }

        var fields = Enumerable.Range(1, 8).Select(BuildPreview).ToArray();
        return new ST_PREVIEW_HEAD_LAYOUT(fields);
    }
}
