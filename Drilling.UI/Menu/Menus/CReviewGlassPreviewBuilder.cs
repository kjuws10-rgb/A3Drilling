using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Drilling.Common.Managers;
using Drilling.Common.Recipe;
using Drilling.Common.Review;

namespace Drilling.UI.Menu.Menus;

internal static class CReviewGlassPreviewBuilder
{
    private const double CanvasWidth = 860.0;
    private const double CanvasHeight = 430.0;
    private const double FrameLeft = 44.0;
    private const double FrameTop = 50.0;
    private const double FrameMaxWidth = 772.0;
    private const double FrameMaxHeight = 340.0;
    // Keep a readable mouse target even when the physical Cell is only a few
    // screen pixels wide. The expanded bounds must not affect Cell geometry,
    // Shot positions, or inspection ranges.
    private const double MinimumLayoutPatternWidth = 18.0;
    private const double MinimumLayoutPatternHeight = 32.0;
    private static readonly ConditionalWeakTable<
        ST_RECIPE_DEFINITION,
        Dictionary<string, string>> RecipeParameterIndexes = new();

    private sealed record ST_REVIEW_CELL_DISPLAY(Rect ActualBounds, Geometry Boundary);

    public static ST_REVIEW_GLASS_PREVIEW Build(

        ST_RECIPE_DEFINITION recipe,

        int defaultCellCount,

        int currentCellNo = 0,

        IReadOnlyList<ST_REVIEW_PLAN_POINT>? reviewPoints = null,

        bool useSampleSelectionColors = false,

        IReadOnlySet<int>? visibleCellNos = null,

        ST_REVIEW_GLASS_AXIS_INDICATOR? axisIndicator = null,

        IReadOnlyList<ST_REVIEW_GLASS_AXIS_INDICATOR>? axisIndicators = null,

        EN_REVIEW_POINT_STATE defaultPointState = EN_REVIEW_POINT_STATE.Ready,

        IReadOnlySet<string>? selectedShotKeys = null,

        bool selectAllShots = false,

        bool showShotPoints = true,

        IReadOnlyList<ST_REVIEW_PLAN_POINT>? inspectionPoints = null,

        EN_REVIEW_CORRECTION_RULE? inspectionRule = null,

        IReadOnlyList<ST_REVIEW_SCOPE_TARGET>? scopeTargets = null,

        IReadOnlyList<ST_REVIEW_PLAN_POINT>? scopePoints = null,

        int scopeHeadNo = 0,

        int scopeCellNo = 0,

        IReadOnlyList<ST_REVIEW_HEAD_SCOPE>? headScopes = null,

        ST_REVIEW_PLAN_POINT? currentHolePoint = null,

        bool expandSmallCellBounds = true,

        bool emphasizeCurrentCell = false,

        IReadOnlySet<int>? currentCellNos = null,

        IReadOnlySet<int>? filledCellNos = null,
        IReadOnlyList<ST_SYSTEM_PARAMETER>? settings = null
    )
    {
        var glassWidth = ReadDouble(recipe, 500.0, "GLASS_SIZE_X");
        var glassHeight = ReadDouble(recipe, 300.0, "GLASS_SIZE_Y");
        var akMarginX = ReadDouble(recipe, 55.0, "AK_MARGIN_X");
        var akMarginY = ReadDouble(recipe, 45.0, "AK_MARGIN_Y");
        // Process/Main decide the owning Head after converting a Recipe Cell
        // design coordinate to the scanner-reference coordinate.  Keep this
        // conversion here as well: the preview itself is drawn in DesignX.
        var scannerReferenceOffsetX =
            ReadDouble(recipe, 0.0, "ESC_EDGE_TO_GLASS_EDGE", "ESC_EDGE_TO_GLASS_EDGE_X") +
            ReadDouble(recipe, 0.0, "GLASS_CENTER_TO_SCANNER_CENTER", "GLASS_CENTER_TO_SCANNER_CENTER_X");
        if (!double.IsFinite(glassWidth) ||
            !double.IsFinite(glassHeight) ||
            glassWidth <= 0.0 ||
            glassHeight <= 0.0)
        {
            return CreateErrorPreview(
                $"GLASS_SIZE_X/Y must be finite values greater than 0. " +
                $"Current value: {glassWidth} x {glassHeight}");
        }

        if (!double.IsFinite(akMarginX) || !double.IsFinite(akMarginY))
        {
            return CreateErrorPreview(
                $"AK_MARGIN_X/Y must be finite values. Current value: {akMarginX} x {akMarginY}");
        }

        if (!double.IsFinite(scannerReferenceOffsetX))
        {
            return CreateErrorPreview(
                $"Scanner reference offset must be finite. Current value: {scannerReferenceOffsetX}");
        }

        var cellCountParameter = FindParameter(recipe, "MAX_CELL_NUMBER");
        var cellCountText = !string.IsNullOrWhiteSpace(cellCountParameter?.Value)
            ? cellCountParameter.Value.Trim()
            : Math.Max(1, defaultCellCount).ToString(CultureInfo.InvariantCulture);
        var cellCountMinimum = GetParameterMinimum(cellCountParameter);
        var cellCountMaximum = GetParameterMaximum(cellCountParameter, cellCountMinimum);
        if (!CCellPatternCalculator.TryParseCellCount(
                cellCountText,
                cellCountMinimum,
                cellCountMaximum,
                out var cellCount,
                out var cellCountValidationMessage))
        {
            return CreateErrorPreview(cellCountValidationMessage);
        }

        var distortionKeys = CCellPreviewDrawing.CreateDistortionKeyPreviews(
            glassWidth,
            glassHeight,
            akMarginX,
            akMarginY,
            key =>
            {
                var text = settings?.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
            });
        var pixelSize = ReadDouble(recipe, 0.0, "PIXEL_SIZE");
        var splitBeamCount = ReadInt(recipe, 0, "SPLITED_BEAM_COUNT");
        var parameterMap = CreateParameterMap(recipe);
        var modelTypeCount = CRecipeModelPixelCountReader.ReadModelTypeCount(parameterMap);
        var reviewPointList = reviewPoints ?? [];
        var shotStates = reviewPointList
            .GroupBy(point => (point.CellNo, point.ShotNo))
            .ToDictionary(group => group.Key, group => group.Last().State);
        var shotLocations = reviewPointList
            .GroupBy(point => (point.CellNo, point.ShotNo))
            .ToDictionary(group => group.Key, group => group.Last());
        var displayedCellNos = visibleCellNos is null
            ? Enumerable.Range(1, cellCount).ToArray()
            : visibleCellNos
                .Where(cellNo => cellNo >= 1 && cellNo <= cellCount)
                .Distinct()
                .OrderBy(cellNo => cellNo)
                .ToArray();
        const int glassPreviewPointBudget = 8000;
        var previewPointBudgetPerCell = Math.Max(
            1,
            glassPreviewPointBudget / Math.Max(1, displayedCellNos.Length));

        var scale = Math.Min(FrameMaxWidth / glassWidth, FrameMaxHeight / glassHeight);
        var frameWidth = glassWidth * scale;
        var frameHeight = glassHeight * scale;
        var frameLeft = FrameLeft + ((FrameMaxWidth - frameWidth) / 2.0);
        var frameTop = FrameTop + ((FrameMaxHeight - frameHeight) / 2.0);
        if (!double.IsFinite(scale) ||
            scale <= 0.0 ||
            !double.IsFinite(frameWidth) ||
            !double.IsFinite(frameHeight) ||
            !double.IsFinite(frameLeft) ||
            !double.IsFinite(frameTop) ||
            frameWidth <= 0.0 ||
            frameHeight <= 0.0)
        {
            return CreateErrorPreview(
                $"Preview scale and frame must be finite positive values. Current scale: {scale}");
        }

        var frame = new ST_GLASS_PREVIEW_FRAME(frameLeft, frameTop, frameWidth, frameHeight);
        var drawing = new DrawingGroup();
        var inspectionOverlayDrawing = new DrawingGroup();
        var labels = new List<ST_CELL_PREVIEW_LABEL>();
        var previewAxisIndicators = axisIndicators ?? (axisIndicator is null ? [] : [axisIndicator]);
        long totalShotCount = 0;
        var cellDisplays = new Dictionary<int, ST_REVIEW_CELL_DISPLAY>();
        var globalHeadScopePoints = new List<Point>();
        var globalRuleScopePoints = new Dictionary<int, List<Point>>();
        var localScopeBounds = new List<(int HeadNo, Rect Bounds)>();
        var selectedCellOutlines = new List<(int HeadNo, Geometry Boundary)>();
        var currentCellOutlines = new List<Geometry>();
        var requestedGlobalScopeHeads = new HashSet<int>();
        var requestedLocalScopeTargets = new HashSet<(int HeadNo, int CellNo)>();
        var roughInspectionHeadNosByCell = new Dictionary<int, int>();
        if (inspectionRule == EN_REVIEW_CORRECTION_RULE.Rough && inspectionPoints is not null)
        {
            foreach (var point in inspectionPoints)
            {
                if (point.CellNo > 0 &&
                    point.HeadNo > 0 &&
                    !roughInspectionHeadNosByCell.ContainsKey(point.CellNo))
                {
                    roughInspectionHeadNosByCell.Add(point.CellNo, point.HeadNo);
                }
            }
        }
        if (scopeHeadNo > 0 && scopeCellNo <= 0)
        {
            requestedGlobalScopeHeads.Add(scopeHeadNo);
        }
        else if (scopeHeadNo > 0 && scopeCellNo > 0)
        {
            requestedLocalScopeTargets.Add((scopeHeadNo, scopeCellNo));
        }

        if (scopeTargets is { Count: > 0 })
        {
            foreach (var target in scopeTargets.Where(target => target.HeadNo > 0))
            {
                if (target.CellNo <= 0)
                {
                    requestedGlobalScopeHeads.Add(target.HeadNo);
                }
                else
                {
                    requestedLocalScopeTargets.Add((target.HeadNo, target.CellNo));
                }
            }
        }

        var physicalHeadScopes = (headScopes ?? [])
            .Where(scope =>
                scope.HeadNo > 0 &&
                double.IsFinite(scope.PositionX) &&
                double.IsFinite(scope.ScanFieldWidthX) &&
                scope.ScanFieldWidthX > 0.0)
            .GroupBy(scope => scope.HeadNo)
            .ToDictionary(group => group.Key, group => group.First());
        var physicalGlobalScopeHeads = requestedGlobalScopeHeads
            .Where(physicalHeadScopes.ContainsKey)
            .ToHashSet();
        var physicalLocalScopeTargets = requestedLocalScopeTargets
            .Where(target => physicalHeadScopes.ContainsKey(target.HeadNo))
            .ToHashSet();
        var requiresPointScope = requestedGlobalScopeHeads.Any(headNo =>
                !physicalGlobalScopeHeads.Contains(headNo)) ||
            requestedLocalScopeTargets.Any(target =>
                !physicalLocalScopeTargets.Contains(target));
        IReadOnlyDictionary<
            (int CellNo, int HeadNo),
            (int FirstColumn, int LastColumn)> pointScopeColumns =
            requiresPointScope && scopePoints is not null
            ? BuildHeadScopeColumnIndex(scopePoints)
            : new Dictionary<
                (int CellNo, int HeadNo),
                (int FirstColumn, int LastColumn)>();

        using (var context = drawing.Open())
        {
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                null,
                new Rect(0.0, 0.0, CanvasWidth, CanvasHeight));

            foreach (var cellNo in displayedCellNos)
            {
                var prefix = $"CELL{cellNo}";
                var firstX = ReadDouble(
                    recipe,
                    0.0,
                    $"{prefix}_ALIGN_TO_1ST_PIXEL_X");
                var firstY = ReadDouble(
                    recipe,
                    0.0,
                    $"{prefix}_ALIGN_TO_1ST_PIXEL_Y");
                var rotation = ReadDouble(
                    recipe,
                    0.0,
                    $"{prefix}_ROTATION");
                var modelType = CRecipeModelPixelCountReader.ReadCellModelType(parameterMap, cellNo, modelTypeCount);
                var modelPattern = CRecipeModelPixelCountReader.ReadPatternForModel(parameterMap, modelType);
                var patternDefinition = new ST_CELL_PATTERN_DEFINITION(

                    cellNo,

                    firstX,

                    firstY,

                    rotation,

                    pixelSize,

                    modelPattern.PixelCountX,

                    modelPattern.PixelCountY,

                    modelPattern.ProcessPitch,

                    splitBeamCount,

                    akMarginX,

                    akMarginY
                );
                var result = CCellPatternCalculator.Calculate(patternDefinition, includeShotCenters: false);
                if (!result.IsValid)
                {
                    return CreateErrorPreview(result.ValidationMessage);
                }

                var cellShotCount = (long)result.GroupCountX * result.GroupCountY;
                totalShotCount += cellShotCount;

                // Match Main and Recipe Preview: draw and calculate with the
                // physical Cell boundary, while expanding only the mouse and
                // tooltip target below.
                if (!TryBuildRecipeCellBoundary(

                    patternDefinition,

                    pixelSize,

                    modelPattern.PixelCountX,

                    modelPattern.PixelCountY,

                    frame,

                    scale,

                    out var boundary
                ))
                {
                    return CreateErrorPreview($"Cell{cellNo} boundary produces an invalid preview coordinate.");
                }

                var displayBounds = expandSmallCellBounds
                    ? ExpandPreviewBounds(

                        boundary.Bounds,

                        MinimumLayoutPatternWidth,

                        MinimumLayoutPatternHeight,

                        new Rect(frameLeft, frameTop, frameWidth, frameHeight)
                    )
                    : boundary.Bounds;
                var useDisplayBounds = expandSmallCellBounds && ShouldUseExpandedPreviewBounds(
                    boundary.Bounds,
                    displayBounds);
                var scopeBounds = boundary.Bounds;
                cellDisplays[cellNo] = new ST_REVIEW_CELL_DISPLAY(boundary.Bounds, boundary);

                // A correction scope is the intersection of the current Cell
                // and the Head's processed columns.  The Cell is later drawn
                // over its base area, so only collect the bounds here and draw
                // the correction overlay after every Cell has been rendered.
                var isCurrentSimpleScopeCell =
                    scopePoints is not null &&
                    scopeHeadNo > 0 &&
                    (scopeCellNo <= 0 || scopeCellNo == cellNo);
                var hasPhysicalSimpleScope = scopeCellNo <= 0
                    ? physicalGlobalScopeHeads.Contains(scopeHeadNo)
                    : physicalLocalScopeTargets.Contains((scopeHeadNo, scopeCellNo));
                if (isCurrentSimpleScopeCell &&
                    !hasPhysicalSimpleScope &&
                         TryGetHeadScopeBounds(

                             pointScopeColumns,

                             cellNo,

                             result.GroupCountX,

                             scopeHeadNo,

                             scopeBounds,

                             out var simpleScopeBounds
                         ))
                {
                    if (scopeCellNo <= 0)
                    {
                        AddScopeCorners(globalHeadScopePoints, simpleScopeBounds);
                    }
                    else
                    {
                        localScopeBounds.Add((scopeHeadNo, simpleScopeBounds));
                    }
                }

                if (scopePoints is not null && scopeTargets is { Count: > 0 })
                {
                    foreach (var target in scopeTargets)
                    {
                        if (target.HeadNo <= 0 ||
                            target.CellNo > 0 && target.CellNo != cellNo)
                        {
                            continue;
                        }

                        var hasPhysicalTargetScope = target.CellNo <= 0
                            ? physicalGlobalScopeHeads.Contains(target.HeadNo)
                            : physicalLocalScopeTargets.Contains((target.HeadNo, target.CellNo));
                        if (hasPhysicalTargetScope)
                        {
                            continue;
                        }

                        if (!TryGetHeadScopeBounds(

                            pointScopeColumns,

                            cellNo,

                            result.GroupCountX,

                            target.HeadNo,

                            scopeBounds,

                            out var roughScopeBounds
                        ))
                        {
                            continue;
                        }

                        List<Point>? collectedPoints = null;
                        if (target.CellNo <= 0 && !physicalGlobalScopeHeads.Contains(target.HeadNo))
                        {
                            if (!globalRuleScopePoints.TryGetValue(target.HeadNo, out collectedPoints))
                            {
                                collectedPoints = [];
                                globalRuleScopePoints[target.HeadNo] = collectedPoints;
                            }
                        }

                        if (collectedPoints is not null)
                        {
                            AddScopeCorners(collectedPoints, roughScopeBounds);
                        }
                        else
                        {
                            localScopeBounds.Add((target.HeadNo, roughScopeBounds));
                        }
                    }
                }

                var previewShotSize = CCellPatternCalculator.CalculatePreviewPointSize(result, scale);
                var beamHalfSpan = CCellPatternCalculator.CalculatePreviewBeamHalfSpan(result, splitBeamCount);
                if (!double.IsFinite(beamHalfSpan) || beamHalfSpan < 0.0)
                {
                    return CreateErrorPreview(
                        $"Cell{cellNo} preview radius must be a finite value greater than or equal to 0. " +
                        $"Current value: {beamHalfSpan}");
                }

                var cellPixels = new HashSet<long>();
                var shotVisuals = new List<(double X, double Y, double Size, EN_REVIEW_POINT_STATE State)>();
                var minCanvasX = double.MaxValue;
                var minCanvasY = double.MaxValue;
                var maxCanvasX = double.MinValue;
                var maxCanvasY = double.MinValue;

                if (showShotPoints)
                {
                    var sampleStep = Math.Max(
                        CCellPatternCalculator.CalculatePreviewSampleStep(result, scale),
                        (int)Math.Ceiling(Math.Sqrt(
                            cellShotCount / (double)previewPointBudgetPerCell)));
                    for (var row = 0; row < result.GroupCountY; row += sampleStep)
                    {
                        for (var column = 0; column < result.GroupCountX; column += sampleStep)
                        {
                            var shotCenter = CCellPatternCalculator.CalculateShotCenter(
                                patternDefinition,
                                column,
                                row,
                                result);
                            AddPreviewShot(shotCenter);
                        }
                    }

                    void AddPreviewShot(ST_SHOT_GROUP_CENTER shotCenter)
                    {
                        var coordinatePoint = shotLocations.GetValueOrDefault((cellNo, shotCenter.CellShotNo));
                        var pointX = coordinatePoint?.DesignX ?? shotCenter.DesignX;
                        var pointY = coordinatePoint?.DesignY ?? shotCenter.DesignY;
                        if (!TryProjectReviewPreviewPixel(

                            pointX,

                            pointY,

                            frame,

                            scale,

                            out var canvasX,

                            out var canvasY,

                            out var pixelX,

                            out var pixelY
                        ))
                        {
                            return;
                        }

                        minCanvasX = Math.Min(minCanvasX, canvasX);
                        minCanvasY = Math.Min(minCanvasY, canvasY);
                        maxCanvasX = Math.Max(maxCanvasX, canvasX);
                        maxCanvasY = Math.Max(maxCanvasY, canvasY);

                        var pixelKey = ((long)pixelX << 32) | (uint)pixelY;
                        var isInside = pointX - beamHalfSpan >= 0.0 &&
                            pointX + beamHalfSpan <= glassWidth &&
                            pointY - beamHalfSpan >= 0.0 &&
                            pointY + beamHalfSpan <= glassHeight;
                        if (cellPixels.Add(pixelKey))
                        {
                            var state = selectAllShots || selectedShotKeys?.Contains(
                                    CReviewManager.ToShotKey(cellNo, shotCenter.CellShotNo)) == true
                                ? EN_REVIEW_POINT_STATE.Ready
                                : shotStates.GetValueOrDefault(
                                    (cellNo, shotCenter.CellShotNo),
                                    useSampleSelectionColors
                                        ? EN_REVIEW_POINT_STATE.Skip
                                        : defaultPointState);
                            shotVisuals.Add((
                                pixelX,
                                pixelY,
                                isInside ? previewShotSize : 4.0,
                                state));
                        }
                    }

                    foreach (var stateGroup in showShotPoints
                                 ? shotVisuals.GroupBy(shot => shot.State)
                                 : [])
                    {
                        var geometry = new StreamGeometry();
                        using (var geometryContext = geometry.Open())
                        {
                            foreach (var shot in stateGroup)
                            {
                                AddCircle(geometryContext, shot.X, shot.Y, shot.Size);
                            }
                        }

                        geometry.Freeze();
                        context.DrawGeometry(
                            useSampleSelectionColors
                                ? CReviewStatusBrush.ForSampleSelection(stateGroup.Key)
                                : CReviewStatusBrush.ForPreviewBaseState(stateGroup.Key),
                            null,
                            geometry);
                    }
                }

                if (!showShotPoints || minCanvasX <= maxCanvasX && minCanvasY <= maxCanvasY)
                {
                    var labelBounds = useDisplayBounds ? displayBounds : boundary.Bounds;
                    // Review is a selection/inspection workspace, not a
                    // Head-assignment map.  Keep its base Cell rendering
                    // neutral like Main so correction overlays and the
                    // selected Shot remain readable.
                    var isSelectedCell = cellNo == currentCellNo ||
                        roughInspectionHeadNosByCell.ContainsKey(cellNo);
                    var selectedHeadNo = 0;
                    if (cellNo == currentCellNo && scopeHeadNo > 0)
                    {
                        selectedHeadNo = scopeHeadNo;
                    }
                    else
                    {
                        roughInspectionHeadNosByCell.TryGetValue(cellNo, out selectedHeadNo);
                    }

                    // HEAD + CELL already draws the Head-colored clipped scope
                    // over this Cell. Adding another selected border produces a
                    // doubled outline, so only HEAD scope needs an extra Cell
                    // outline. Reuse the owning Head color instead of blue.
                    var hasCellClippedScope = selectedHeadNo > 0 &&
                        (scopeHeadNo == selectedHeadNo && scopeCellNo == cellNo ||
                         requestedLocalScopeTargets.Contains((selectedHeadNo, cellNo)));
                    var emphasizeSelectedCell = isSelectedCell &&
                        selectedHeadNo > 0 &&
                        !hasCellClippedScope;
                    if (emphasizeSelectedCell)
                    {
                        selectedCellOutlines.Add((selectedHeadNo, boundary));
                    }

                    var isCurrentCell = currentCellNos is null
                        ? cellNo == currentCellNo
                        : currentCellNos.Contains(cellNo);
                    if (emphasizeCurrentCell && isCurrentCell)
                    {
                        currentCellOutlines.Add(boundary);
                    }

                    var boundaryBrush = CStatusBrush.Frozen(0x9C, 0xA8, 0xB5);
                    var boundaryPen = new Pen(
                        boundaryBrush,
                        inspectionRule == EN_REVIEW_CORRECTION_RULE.Fine ? 0.7 : 1.0);
                    boundaryPen.Freeze();
                    var hasSelectedShots = filledCellNos?.Contains(cellNo) == true;
                    var cellBrush = CStatusBrush.Frozen(0x43, 0x4C, 0x57);
                    context.DrawGeometry(cellBrush, null, boundary);
                    if (hasSelectedShots)
                    {
                        // The shared boundary contains stroke-only figures. Fill a copy
                        // so the normal Cell outline and other preview modes stay intact.
                        var fillGeometry = PathGeometry.CreateFromGeometry(boundary).Clone();
                        foreach (var figure in fillGeometry.Figures)
                        {
                            figure.IsFilled = true;
                        }
                        fillGeometry.Freeze();
                        context.DrawGeometry(CStatusBrush.Frozen(0x90, 0xB8, 0xD6), null, fillGeometry);
                    }
                    context.DrawGeometry(null, boundaryPen, boundary);
                    var label = CCellPreviewDrawing.CreateCellLabel(

                        cellNo,

                        labelBounds,

                        CanvasWidth,

                        CanvasHeight,

                        filledCellNos is null ? isSelectedCell : hasSelectedShots
                    );
                    if (label is not null)
                    {
                        labels.Add(label);
                    }
                }
            }

            CCellPreviewDrawing.DrawAlignKeys(
                context,
                frame,
                glassWidth,
                glassHeight,
                akMarginX,
                akMarginY);
            CCellPreviewDrawing.DrawDistortionKeys(
                context,
                frame,
                glassWidth,
                glassHeight,
                distortionKeys);
        }

        // Keep correction ranges and inspection lines above the interactive
        // Cell labels.  Putting them in the base drawing makes a translucent
        // label still obscure the guide line beneath it.
        var hasInspectionOverlay =
            localScopeBounds.Count > 0 ||
            selectedCellOutlines.Count > 0 ||
            currentCellOutlines.Count > 0 ||
            physicalGlobalScopeHeads.Count > 0 ||
            physicalLocalScopeTargets.Count > 0 ||
            (globalHeadScopePoints.Count > 0 && scopeHeadNo > 0) ||
            globalRuleScopePoints.Any(item => item.Value.Count > 0) ||
            inspectionPoints is { Count: > 0 };
        if (hasInspectionOverlay)
        {
            using var overlayContext = inspectionOverlayDrawing.Open();

            foreach (var (headNo, bounds) in localScopeBounds)
            {
                DrawHeadCellScope(overlayContext, headNo, new RectangleGeometry(bounds));
            }

            // Head scope is the same continuous physical scan field used by
            // Main.  Only Head + Cell clips that field to the selected Cell.
            foreach (var headNo in physicalGlobalScopeHeads.OrderBy(headNo => headNo))
            {
                DrawPhysicalHeadScope(

                    overlayContext,

                    headNo,

                    physicalHeadScopes[headNo],

                    cellDisplays,

                    frame,

                    scale,

                    akMarginX,

                    scannerReferenceOffsetX,

                    targetCellNo: 0
                );
            }

            foreach (var (headNo, cellNo) in physicalLocalScopeTargets
                         .OrderBy(target => target.HeadNo)
                         .ThenBy(target => target.CellNo))
            {
                DrawPhysicalHeadScope(

                    overlayContext,

                    headNo,

                    physicalHeadScopes[headNo],

                    cellDisplays,

                    frame,

                    scale,

                    akMarginX,

                    scannerReferenceOffsetX,

                    cellNo
                );
            }

            if (globalHeadScopePoints.Count > 0 && scopeHeadNo > 0)
            {
                DrawGlobalHeadScope(overlayContext, scopeHeadNo, globalHeadScopePoints);
            }

            foreach (var (headNo, points) in globalRuleScopePoints.Where(item => item.Value.Count > 0))
            {
                DrawGlobalHeadScope(overlayContext, headNo, points);
            }

            if (inspectionPoints is { Count: > 0 })
            {
                DrawInspectionPoints(

                    overlayContext,

                    inspectionPoints,

                    inspectionRule,

                    frame,

                    scale,

                    cellDisplays
                );
            }

            foreach (var (headNo, boundary) in selectedCellOutlines)
            {
                DrawHeadCellScope(overlayContext, headNo, boundary);
            }

            foreach (var currentCellOutline in currentCellOutlines)
            {
                DrawCurrentCellOutline(overlayContext, currentCellOutline);
            }
        }

        drawing.Freeze();
        var hasAxisIndicators = previewAxisIndicators.Count > 0;
        var paddingX = hasAxisIndicators
            ? Math.Max(96.0, frameWidth * 0.03)
            : frameWidth * 0.03;
        var paddingY = hasAxisIndicators
            ? Math.Max(68.0, frameHeight * 0.03)
            : Math.Max(22.0, frameHeight * 0.03);
        var previewRect = new Rect(
            0.0,
            0.0,
            frameWidth + (paddingX * 2.0),
            frameHeight + (paddingY * 2.0));
        var glassRect = new Rect(paddingX, paddingY, frameWidth, frameHeight);
        var translatedLabels = labels
            .Select(label => label with
            {
                CanvasCenterX = label.CanvasCenterX - frameLeft + paddingX,
                CanvasCenterY = label.CanvasCenterY - frameTop + paddingY,
                DesignWidth = previewRect.Width,
                DesignHeight = previewRect.Height
            })
            .ToArray();
        var previewDrawing = new DrawingGroup();

        using (var context = previewDrawing.Open())
        {
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                null,
                previewRect);
            var glassPen = new Pen(new SolidColorBrush(Color.FromRgb(102, 136, 164)), 1.8);
            glassPen.Freeze();
            context.DrawRectangle(null, glassPen, glassRect);
            context.PushClip(new RectangleGeometry(previewRect));
            context.PushTransform(new TranslateTransform(
                -(frameLeft - paddingX),
                -(frameTop - paddingY)));
            context.DrawDrawing(drawing);
            context.Pop();
            context.Pop();
            foreach (var previewAxisIndicator in previewAxisIndicators)
            {
                DrawPreviewAxisIndicator(context, previewRect, glassRect, previewAxisIndicator);
            }
        }

        previewDrawing.Freeze();
        var previewImage = new DrawingImage(previewDrawing);
        previewImage.Freeze();

        ImageSource? inspectionOverlayImage = null;
        if (hasInspectionOverlay)
        {
            inspectionOverlayDrawing.Freeze();
            var previewOverlayDrawing = new DrawingGroup();
            using (var context = previewOverlayDrawing.Open())
            {
                // A near-transparent frame gives this overlay exactly the
                // same design bounds as the base image while remaining
                // visually transparent between range/inspection strokes.
                context.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                    null,
                    previewRect);
                context.PushClip(new RectangleGeometry(previewRect));
                context.PushTransform(new TranslateTransform(
                    -(frameLeft - paddingX),
                    -(frameTop - paddingY)));
                context.DrawDrawing(inspectionOverlayDrawing);
                context.Pop();
                context.Pop();
            }

            previewOverlayDrawing.Freeze();
            var image = new DrawingImage(previewOverlayDrawing);
            image.Freeze();
            inspectionOverlayImage = image;
        }

        return new ST_REVIEW_GLASS_PREVIEW(

            previewImage,

            inspectionOverlayImage,

            translatedLabels,

            $"{displayedCellNos.Length} Cells / {totalShotCount:N0} Shot Groups / Glass {glassWidth:0.#} x {glassHeight:0.#} mm"
        )
        { GlassBounds = glassRect, CanvasSize = previewRect.Size };
    }

    private static void DrawInspectionPoints(

        DrawingContext context,

        IReadOnlyList<ST_REVIEW_PLAN_POINT> inspectionPoints,

        EN_REVIEW_CORRECTION_RULE? inspectionRule,

        ST_GLASS_PREVIEW_FRAME frame,

        double scale,

        IReadOnlyDictionary<int, ST_REVIEW_CELL_DISPLAY> cellDisplays
    )
    {
        if (inspectionRule == EN_REVIEW_CORRECTION_RULE.Fine)
        {
            DrawFineInspectionLines(context, inspectionPoints, frame, scale);
            return;
        }

        if (inspectionRule == EN_REVIEW_CORRECTION_RULE.Edge)
        {
            DrawEdgeInspectionAreas(context, inspectionPoints, frame, scale, cellDisplays);
            return;
        }

        // Rough selection is expressed by the selected Cell boundaries in the
        // base preview. Do not add Shot dots or a second outline here.
        if (inspectionRule == EN_REVIEW_CORRECTION_RULE.Rough)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            foreach (var point in inspectionPoints)
            {
                if (!TryProjectCellPoint(
                        point,
                        frame,
                        scale,
                        out var canvasPoint))
                {
                    continue;
                }

                AddCircle(
                    geometryContext,
                    canvasPoint.X,
                    canvasPoint.Y,
                    1.4);
            }
        }

        geometry.Freeze();
        context.DrawGeometry(CStatusBrush.Frozen(0xFF, 0xC6, 0x29), null, geometry);
    }

    private static void DrawEdgeInspectionAreas(

        DrawingContext context,

        IReadOnlyList<ST_REVIEW_PLAN_POINT> inspectionPoints,

        ST_GLASS_PREVIEW_FRAME frame,

        double scale,

        IReadOnlyDictionary<int, ST_REVIEW_CELL_DISPLAY> cellDisplays
    )
    {
        var dashStyle = new DashStyle([3.0, 3.0], 0.0);
        dashStyle.Freeze();
        var edgePen = new Pen(CStatusBrush.Frozen(0x4F, 0x91, 0xB8), 1.15)
        {
            DashStyle = dashStyle,
            DashCap = PenLineCap.Round
        };
        edgePen.Freeze();
        foreach (var cell in inspectionPoints.GroupBy(point => point.CellNo))
        {
            if (cellDisplays.TryGetValue(cell.Key, out var display))
            {
                context.DrawRectangle(null, edgePen, display.ActualBounds);
                continue;
            }

            var projected = cell
                .Select(point => TryProjectCellPoint(
                        point,
                        frame,
                        scale,
                        out var canvasPoint)
                    ? new Point?(canvasPoint)
                    : null)
                .Where(point => point.HasValue)
                .Select(point => point!.Value)
                .ToArray();
            if (projected.Length == 0)
            {
                continue;
            }

            var minimumX = projected.Min(point => point.X);
            var maximumX = projected.Max(point => point.X);
            var minimumY = projected.Min(point => point.Y);
            var maximumY = projected.Max(point => point.Y);
            if (maximumX > minimumX && maximumY > minimumY)
            {
                context.DrawRectangle(
                    null,
                    edgePen,
                    new Rect(minimumX, minimumY, maximumX - minimumX, maximumY - minimumY));
            }
        }
    }

    private static void DrawFineInspectionLines(
        DrawingContext context,
        IReadOnlyList<ST_REVIEW_PLAN_POINT> inspectionPoints,
        ST_GLASS_PREVIEW_FRAME frame,
        double scale)
    {
        var linePen = new Pen(CStatusBrush.Frozen(0x4F, 0x91, 0xB8), 1.6)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        linePen.Freeze();
        var rows = inspectionPoints
            .Where(point => point.CellNo > 0 && point.HeadNo > 0 && double.IsFinite(point.DesignY))
            .GroupBy(point => (point.CellNo, Y: Math.Round(point.DesignY, 6)))
            .OrderBy(group => group.Key.CellNo)
            .ThenBy(group => group.Key.Y)
            .ToArray();

        foreach (var row in rows)
        {
            var projectedRow = row
                .Select(point => TryProjectCellPoint(
                        point,
                        frame,
                        scale,
                        out var canvasPoint)
                    ? new Point?(canvasPoint)
                    : null)
                .Where(point => point.HasValue)
                .Select(point => point!.Value)
                .ToArray();
            if (projectedRow.Length == 0)
            {
                continue;
            }

            // Fine lines belong to individual Cells. Never bridge the gap
            // between Cells whose selected Shot rows happen to share a Y.
            var left = projectedRow.Min(point => point.X);
            var right = projectedRow.Max(point => point.X);
            var y = projectedRow.Average(point => point.Y);
            context.DrawLine(
                linePen,
                new Point(left, y),
                new Point(right, y));
        }
    }

    private static bool TryProjectCellPoint(
        ST_REVIEW_PLAN_POINT point,
        ST_GLASS_PREVIEW_FRAME frame,
        double scale,
        out Point canvasPoint)
    {
        canvasPoint = default;
        if (!TryProjectReviewPreviewPixel(

            point.DesignX,

            point.DesignY,

            frame,

            scale,

            out var canvasX,

            out var canvasY,

            out _,

            out _
        ))
        {
            return false;
        }

        canvasPoint = new Point(canvasX, canvasY);
        return true;
    }

    private static bool TryGetHeadScopeBounds(

        IReadOnlyDictionary<(int CellNo, int HeadNo), (int FirstColumn, int LastColumn)> scopeColumns,

        int cellNo,

        int groupCountX,

        int headNo,

        Rect cellBounds,

        out Rect scopeBounds
    )
    {
        scopeBounds = Rect.Empty;
        if (cellNo <= 0 || groupCountX <= 0 || headNo <= 0)
        {
            return false;
        }

        if (!scopeColumns.TryGetValue((cellNo, headNo), out var columns) ||
            cellBounds.IsEmpty ||
            cellBounds.Width <= 0.0 ||
            cellBounds.Height <= 0.0)
        {
            return false;
        }

        var left = cellBounds.Left + (cellBounds.Width * columns.FirstColumn / groupCountX);
        var right = cellBounds.Left + (cellBounds.Width * (columns.LastColumn + 1) / groupCountX);
        scopeBounds = new Rect(left, cellBounds.Top, right - left, cellBounds.Height);
        return true;
    }

    private static IReadOnlyDictionary<
        (int CellNo, int HeadNo),
        (int FirstColumn, int LastColumn)> BuildHeadScopeColumnIndex(
            IReadOnlyList<ST_REVIEW_PLAN_POINT> scopePoints)
    {
        var result = new Dictionary<
            (int CellNo, int HeadNo),
            (int FirstColumn, int LastColumn)>();
        foreach (var point in scopePoints)
        {
            if (point.CellNo <= 0 || point.HeadNo <= 0 || point.PixelCountX <= 0)
            {
                continue;
            }

            var column = (point.ShotNo - 1) % point.PixelCountX;
            if (column < 0)
            {
                continue;
            }

            var key = (point.CellNo, point.HeadNo);
            if (result.TryGetValue(key, out var columns))
            {
                result[key] = (
                    Math.Min(columns.FirstColumn, column),
                    Math.Max(columns.LastColumn, column));
            }
            else
            {
                result.Add(key, (column, column));
            }
        }

        return result;
    }

    private static void AddScopeCorners(ICollection<Point> points, Rect bounds)
    {
        if (bounds.IsEmpty || bounds.Width <= 0.0 || bounds.Height <= 0.0)
        {
            return;
        }

        points.Add(bounds.TopLeft);
        points.Add(bounds.TopRight);
        points.Add(bounds.BottomRight);
        points.Add(bounds.BottomLeft);
    }

    private static void DrawHeadScope(
        DrawingContext context,
        int headNo,
        Rect scopeBounds)
    {
        if (headNo <= 0 || scopeBounds.IsEmpty || scopeBounds.Width <= 0.0 || scopeBounds.Height <= 0.0)
        {
            return;
        }

        var fill = CMenuMain.CreateHeadBrush(headNo, 0x58);
        var stroke = new Pen(CMenuMain.CreateHeadBrush(headNo, 0xE6), 2.0)
        {
            DashStyle = DashStyles.Dash
        };
        stroke.Freeze();
        context.DrawRectangle(fill, stroke, scopeBounds);
    }

    private static void DrawHeadCellScope(
        DrawingContext context,
        int headNo,
        Geometry cellBoundary)
    {
        if (headNo <= 0 || cellBoundary.Bounds.IsEmpty)
        {
            return;
        }

        var stroke = new Pen(CMenuMain.CreateHeadBrush(headNo, 0xF2), 1.0);
        stroke.Freeze();
        context.DrawGeometry(null, stroke, cellBoundary);
    }

    private static void DrawCurrentCellOutline(
        DrawingContext context,
        Geometry cellBoundary)
    {
        if (cellBoundary.Bounds.IsEmpty)
        {
            return;
        }

        var stroke = new Pen(
            CReviewStatusBrush.ForState(EN_REVIEW_POINT_STATE.Current),
            2.4)
        {
            LineJoin = PenLineJoin.Round
        };
        stroke.Freeze();
        context.DrawGeometry(null, stroke, cellBoundary);
    }

    private static void DrawGlobalHeadScope(
        DrawingContext context,
        int headNo,
        IReadOnlyCollection<Point> scopePoints)
    {
        var minX = scopePoints.Min(point => point.X);
        var minY = scopePoints.Min(point => point.Y);
        var maxX = scopePoints.Max(point => point.X);
        var maxY = scopePoints.Max(point => point.Y);
        if (!double.IsFinite(minX) || !double.IsFinite(minY) ||
            !double.IsFinite(maxX) || !double.IsFinite(maxY) ||
            maxX <= minX || maxY <= minY)
        {
            return;
        }

        var fill = CMenuMain.CreateHeadBrush(headNo, 0x58);
        var stroke = new Pen(CMenuMain.CreateHeadBrush(headNo, 0xE6), 2.0)
        {
            DashStyle = DashStyles.Dash
        };
        stroke.Freeze();
        context.DrawRectangle(fill, stroke, new Rect(minX, minY, maxX - minX, maxY - minY));
    }

    private static void DrawPhysicalHeadScope(

        DrawingContext context,

        int headNo,

        ST_REVIEW_HEAD_SCOPE headScope,

        IReadOnlyDictionary<int, ST_REVIEW_CELL_DISPLAY> cellDisplays,

        ST_GLASS_PREVIEW_FRAME frame,

        double scale,

        double akMarginX,

        double scannerReferenceOffsetX,

        int targetCellNo
    )
    {
        // ST_REVIEW_HEAD_SCOPE is in the scanner-reference frame, whereas
        // Cell bounds and review points are rendered in raw DesignX.  Main
        // assigns Head N with (DesignX + scannerReferenceOffsetX), so shift
        // the physical field back before intersecting it with Cell bounds.
        var centerX = akMarginX + headScope.PositionX - scannerReferenceOffsetX;
        var halfWidth = headScope.ScanFieldWidthX / 2.0;
        var physicalLeft = frame.CanvasLeft + ((centerX - halfWidth) * scale);
        var physicalRight = frame.CanvasLeft + ((centerX + halfWidth) * scale);
        if (!double.IsFinite(physicalLeft) || !double.IsFinite(physicalRight) ||
            physicalRight <= physicalLeft)
        {
            return;
        }

        var clippedLeft = Math.Max(frame.CanvasLeft, physicalLeft);
        var clippedRight = Math.Min(frame.CanvasLeft + frame.Width, physicalRight);
        if (clippedRight <= clippedLeft)
        {
            return;
        }

        if (targetCellNo <= 0)
        {
            DrawHeadScope(
                context,
                headNo,
                new Rect(clippedLeft, frame.CanvasTop, clippedRight - clippedLeft, frame.Height));
            return;
        }

        if (!cellDisplays.TryGetValue(targetCellNo, out var display))
        {
            return;
        }

        var actual = display.ActualBounds;
        if (actual.IsEmpty || actual.Width <= 0.0 || actual.Height <= 0.0)
        {
            return;
        }

        var overlapLeft = Math.Max(physicalLeft, actual.Left);
        var overlapRight = Math.Min(physicalRight, actual.Right);
        if (overlapRight <= overlapLeft)
        {
            return;
        }

        DrawHeadCellScope(context, headNo, display.Boundary);
    }

    private static bool TryBuildRecipeCellBoundary(

        ST_CELL_PATTERN_DEFINITION definition,

        double pixelSize,

        int pixelCountX,

        int pixelCountY,

        ST_GLASS_PREVIEW_FRAME frame,

        double scale,

        out StreamGeometry geometry
    )
    {
        if (pixelCountX <= 0 || pixelCountY <= 0 || !double.IsFinite(pixelSize) || pixelSize <= 0.0)
        {
            geometry = new StreamGeometry();
            return false;
        }

        var radians = definition.RotationDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var halfPixel = pixelSize / 2.0;
        var localMaxX = ((pixelCountX - 1) * pixelSize) + halfPixel;
        var localMaxY = ((pixelCountY - 1) * pixelSize) + halfPixel;
        var localCorners = new[]
        {
            new Point(-halfPixel, -halfPixel),
            new Point(localMaxX, -halfPixel),
            new Point(localMaxX, localMaxY),
            new Point(-halfPixel, localMaxY)
        };
        var corners = new Point[localCorners.Length];
        for (var index = 0; index < localCorners.Length; index++)
        {
            var local = localCorners[index];
            var designX = definition.OriginX + definition.FirstTargetX +
                (local.X * cos) - (local.Y * sin);
            var designY = definition.OriginY + definition.FirstTargetY +
                (local.X * sin) + (local.Y * cos);
            if (!TryProjectReviewPreviewPixel(

                designX,

                designY,

                frame,

                scale,

                out var canvasX,

                out var canvasY,

                out _,

                out _
            ))
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

    private static bool ShouldUseExpandedPreviewBounds(
        Rect actualBounds,
        Rect displayBounds) =>
        !actualBounds.IsEmpty &&
        (Math.Abs(actualBounds.Width - displayBounds.Width) > 0.001 ||
         Math.Abs(actualBounds.Height - displayBounds.Height) > 0.001);

    private static Rect ExpandPreviewBounds(
        Rect bounds,
        double minimumWidth,
        double minimumHeight,
        Rect limitBounds)
    {
        var left = Math.Clamp(bounds.Left, limitBounds.Left, limitBounds.Right);
        var top = Math.Clamp(bounds.Top, limitBounds.Top, limitBounds.Bottom);
        var maximumWidth = Math.Max(1.0, limitBounds.Right - left);
        var maximumHeight = Math.Max(1.0, limitBounds.Bottom - top);
        return new Rect(
            left,
            top,
            Math.Min(Math.Max(bounds.Width, minimumWidth), maximumWidth),
            Math.Min(Math.Max(bounds.Height, minimumHeight), maximumHeight));
    }

    // TODO: Review coordinate generation will be consolidated in a separate work item.
    // Keep this renderer-side boundary contract until that pipeline validates every coordinate source.
    private static bool TryProjectReviewPreviewPixel(

        double designX,

        double designY,

        ST_GLASS_PREVIEW_FRAME frame,

        double scale,

        out double canvasX,

        out double canvasY,

        out int pixelX,

        out int pixelY
    )
    {
        canvasX = frame.CanvasLeft + (designX * scale);
        canvasY = frame.CanvasTop + (designY * scale);
        pixelX = 0;
        pixelY = 0;
        if (!double.IsFinite(designX) ||
            !double.IsFinite(designY) ||
            !double.IsFinite(scale) ||
            scale <= 0.0 ||
            !double.IsFinite(canvasX) ||
            !double.IsFinite(canvasY))
        {
            return false;
        }

        var roundedX = Math.Round(canvasX);
        var roundedY = Math.Round(canvasY);
        if (!double.IsFinite(roundedX) ||
            !double.IsFinite(roundedY) ||
            roundedX < int.MinValue ||
            roundedX > int.MaxValue ||
            roundedY < int.MinValue ||
            roundedY > int.MaxValue)
        {
            return false;
        }

        pixelX = (int)roundedX;
        pixelY = (int)roundedY;
        return true;
    }

    private static ST_REVIEW_GLASS_PREVIEW CreateErrorPreview(string message)
    {
        return new ST_REVIEW_GLASS_PREVIEW(
            null,
            null,
            [],
            $"Preview unavailable: {message}");
    }

    private static void AddCircle(
        StreamGeometryContext context,
        double x,
        double y,
        double size)
    {
        var radius = size / 2.0;
        var control = radius * 0.5522847498;
        context.BeginFigure(new Point(x + radius, y), true, true);
        context.BezierTo(
            new Point(x + radius, y + control),
            new Point(x + control, y + radius),
            new Point(x, y + radius),
            true,
            false);
        context.BezierTo(
            new Point(x - control, y + radius),
            new Point(x - radius, y + control),
            new Point(x - radius, y),
            true,
            false);
        context.BezierTo(
            new Point(x - radius, y - control),
            new Point(x - control, y - radius),
            new Point(x, y - radius),
            true,
            false);
        context.BezierTo(
            new Point(x + control, y - radius),
            new Point(x + radius, y - control),
            new Point(x + radius, y),
            true,
            false);
    }

    private static void DrawPreviewAxisIndicator(
        DrawingContext context,
        Rect previewRect,
        Rect glassRect,
        ST_REVIEW_GLASS_AXIS_INDICATOR axisIndicator)
    {
        var axisBrush = new SolidColorBrush(Color.FromRgb(248, 250, 252));
        axisBrush.Freeze();
        var axisPen = new Pen(axisBrush, 2.0)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        axisPen.Freeze();

        var origin = GetPreviewAxisOrigin(previewRect, glassRect, axisIndicator);
        var xEnd = axisIndicator.XPositiveRight
            ? new Point(origin.X + 46.0, origin.Y)
            : new Point(origin.X - 46.0, origin.Y);
        var yEnd = axisIndicator.YPositiveDown
            ? new Point(origin.X, origin.Y + 42.0)
            : new Point(origin.X, origin.Y - 42.0);

        if (!string.IsNullOrWhiteSpace(axisIndicator.XLabel))
        {
            context.DrawLine(axisPen, origin, xEnd);
            DrawHorizontalArrowHead(context, axisPen, xEnd, axisIndicator.XPositiveRight);
        }

        if (!string.IsNullOrWhiteSpace(axisIndicator.YLabel))
        {
            context.DrawLine(axisPen, origin, yEnd);
            DrawVerticalArrowHead(context, axisPen, yEnd, axisIndicator.YPositiveDown);
        }

        if (!string.IsNullOrWhiteSpace(axisIndicator.XLabel))
        {
            DrawPreviewAxisText(

                context,

                axisIndicator.XLabel,

                new Point(
                    axisIndicator.XPositiveRight ? xEnd.X + 4.0 : Math.Max(2.0, xEnd.X - 24.0),
                    Math.Max(2.0, xEnd.Y - 10.0)),

                axisBrush
            );
        }

        if (!string.IsNullOrWhiteSpace(axisIndicator.YLabel))
        {
            DrawPreviewAxisText(

                context,

                axisIndicator.YLabel,

                new Point(
                    yEnd.X + 4.0,
                    axisIndicator.YPositiveDown ? yEnd.Y - 4.0 : Math.Max(2.0, yEnd.Y - 10.0)),

                axisBrush
            );
        }

        if (!string.IsNullOrWhiteSpace(axisIndicator.Title))
        {
            DrawPreviewAxisText(
                context,
                axisIndicator.Title,
                GetPreviewAxisTitlePoint(axisIndicator, origin, previewRect),
                axisBrush,
                11.0);
        }
    }

    private static Point GetPreviewAxisTitlePoint(
        ST_REVIEW_GLASS_AXIS_INDICATOR axisIndicator,
        Point origin,
        Rect previewRect)
    {
        var anchor = axisIndicator.Anchor.ToUpperInvariant();
        var x = origin.X - 18.0;
        var y = origin.Y - 24.0;

        if (anchor == "BOTTOM_LEFT")
        {
            x = origin.X + 4.0;
            y = origin.Y + 12.0;
        }
        else if (anchor == "BOTTOM_RIGHT")
        {
            x = origin.X - 92.0;
            y = origin.Y + 12.0;
        }

        return new Point(
            ClampPreviewAxisCoordinate(x, previewRect.Left + 4.0, previewRect.Right - 112.0),
            ClampPreviewAxisCoordinate(y, previewRect.Top + 4.0, previewRect.Bottom - 16.0));
    }

    private static Point GetPreviewAxisOrigin(
        Rect previewRect,
        Rect glassRect,
        ST_REVIEW_GLASS_AXIS_INDICATOR axisIndicator)
    {
        var anchor = axisIndicator.Anchor.ToUpperInvariant();
        if (anchor == "TOP_RIGHT")
        {
            var x = axisIndicator.XPositiveRight
                ? glassRect.Right - 54.0
                : glassRect.Right + 14.0;
            var y = axisIndicator.YPositiveDown
                ? glassRect.Top - 42.0
                : glassRect.Top - 8.0;
            return new Point(
                ClampPreviewAxisCoordinate(x, previewRect.Left + 8.0, previewRect.Right - 8.0),
                ClampPreviewAxisCoordinate(y, previewRect.Top + 8.0, previewRect.Bottom - 8.0));
        }

        if (anchor == "BOTTOM_LEFT")
        {
            var x = axisIndicator.XPositiveRight
                ? glassRect.Left - 30.0
                : glassRect.Left + 54.0;
            var y = axisIndicator.YPositiveDown
                ? glassRect.Bottom + 14.0
                : glassRect.Bottom + 42.0;
            return new Point(
                ClampPreviewAxisCoordinate(x, previewRect.Left + 8.0, previewRect.Right - 8.0),
                ClampPreviewAxisCoordinate(y, previewRect.Top + 8.0, previewRect.Bottom - 8.0));
        }

        if (anchor == "BOTTOM_RIGHT")
        {
            var x = axisIndicator.XPositiveRight
                ? glassRect.Right - 54.0
                : glassRect.Right + 14.0;
            var y = axisIndicator.YPositiveDown
                ? glassRect.Bottom + 14.0
                : glassRect.Bottom + 42.0;
            return new Point(
                ClampPreviewAxisCoordinate(x, previewRect.Left + 8.0, previewRect.Right - 8.0),
                ClampPreviewAxisCoordinate(y, previewRect.Top + 8.0, previewRect.Bottom - 8.0));
        }

        var topLeftX = axisIndicator.XPositiveRight
            ? glassRect.Left - 30.0
            : glassRect.Left + 54.0;
        var topLeftY = axisIndicator.YPositiveDown
            ? glassRect.Top - 42.0
            : glassRect.Top - 8.0;
        return new Point(
            ClampPreviewAxisCoordinate(topLeftX, previewRect.Left + 8.0, previewRect.Right - 8.0),
            ClampPreviewAxisCoordinate(topLeftY, previewRect.Top + 8.0, previewRect.Bottom - 8.0));
    }

    private static double ClampPreviewAxisCoordinate(
        double value,
        double min,
        double max)
    {
        var safeMax = Math.Max(min, max);
        return Math.Clamp(value, min, safeMax);
    }

    private static void DrawHorizontalArrowHead(
        DrawingContext context,
        Pen axisPen,
        Point end,
        bool positiveRight)
    {
        var direction = positiveRight ? 1.0 : -1.0;
        context.DrawLine(axisPen, new Point(end.X - (direction * 8.0), end.Y - 4.0), end);
        context.DrawLine(axisPen, new Point(end.X - (direction * 8.0), end.Y + 4.0), end);
    }

    private static void DrawVerticalArrowHead(
        DrawingContext context,
        Pen axisPen,
        Point end,
        bool positiveDown)
    {
        var direction = positiveDown ? 1.0 : -1.0;
        context.DrawLine(axisPen, new Point(end.X - 4.0, end.Y - (direction * 8.0)), end);
        context.DrawLine(axisPen, new Point(end.X + 4.0, end.Y - (direction * 8.0)), end);
    }

    private static void DrawPreviewAxisText(
        DrawingContext context,
        string text,
        Point point,
        Brush brush,
        double fontSize = 13.0)
    {
        var label = new FormattedText(

            text,

            CultureInfo.InvariantCulture,

            FlowDirection.LeftToRight,

            new Typeface("Segoe UI Semibold"),

            fontSize,

            brush,

            1.0
        );

        context.DrawText(label, point);
    }

    private static int ReadInt(
        ST_RECIPE_DEFINITION recipe,
        int defaultValue,
        params string[] keys)
    {
        var text = ReadText(recipe, keys);
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue)
            ? (int)Math.Round(doubleValue)
            : defaultValue;
    }

    private static IReadOnlyDictionary<string, string> CreateParameterMap(ST_RECIPE_DEFINITION recipe)
    {
        return recipe.Parameters
            .GroupBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
    }

    private static double ReadDouble(
        ST_RECIPE_DEFINITION recipe,
        double defaultValue,
        params string[] keys)
    {
        var text = ReadText(recipe, keys);

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    private static double? ReadNullableDouble(
        ST_RECIPE_DEFINITION recipe,
        string key)
    {
        var text = ReadText(recipe, key);

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string ReadText(
        ST_RECIPE_DEFINITION recipe,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryReadParameterText(recipe, key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "";
    }

    private static bool TryReadParameterText(
        ST_RECIPE_DEFINITION recipe,
        string key,
        out string value)
    {
        var parameters = RecipeParameterIndexes.GetValue(
            recipe,
            static definition => CreateRecipeParameterIndex(definition));
        return parameters.TryGetValue(key, out value!);
    }

    private static Dictionary<string, string> CreateRecipeParameterIndex(
        ST_RECIPE_DEFINITION recipe)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in recipe.Parameters)
        {
            var value = parameter.Value?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(parameter.Key))
            {
                parameters.TryAdd(parameter.Key, value);
            }

            if (!string.IsNullOrWhiteSpace(parameter.Name))
            {
                parameters.TryAdd(parameter.Name, value);
            }
        }

        return parameters;
    }

    private static ST_RECIPE_PARAMETER? FindParameter(
        ST_RECIPE_DEFINITION recipe,
        string key)
    {
        return recipe.Parameters.FirstOrDefault(item =>
            item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ||
            item.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private static int GetParameterMinimum(ST_RECIPE_PARAMETER? parameter)
    {
        return parameter is not null && parameter.Max > parameter.Min
            ? Math.Max(CCellPatternCalculator.MinCellCount, (int)Math.Ceiling(parameter.Min))
            : CCellPatternCalculator.MinCellCount;
    }

    private static int? GetParameterMaximum(ST_RECIPE_PARAMETER? parameter, int minimum)
    {
        return parameter is not null && parameter.Max >= minimum
            ? (int)Math.Floor(parameter.Max)
            : null;
    }
}

public sealed record ST_REVIEW_GLASS_PREVIEW(
    ImageSource? Image,
    ImageSource? InspectionOverlayImage,
    IReadOnlyList<ST_CELL_PREVIEW_LABEL> CellLabels,
    string Summary)
{
    public Rect GlassBounds { get; init; } = Rect.Empty;
    public Size CanvasSize { get; init; }
}

internal sealed record ST_REVIEW_SCOPE_TARGET(int HeadNo, int CellNo);

internal sealed record ST_REVIEW_GLASS_AXIS_INDICATOR(
    string XLabel,
    string YLabel,
    bool XPositiveRight,
    bool YPositiveDown,
    string Anchor = "TOP_LEFT",
    string Title = "");
