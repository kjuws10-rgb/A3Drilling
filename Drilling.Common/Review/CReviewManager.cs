namespace Drilling.Common.Review
{
    using Drilling.Common.Recipe;
    using System.Text;
    using Drilling.Common.Managers;
    using Drilling.Common.Interface;
    using System.Globalization;
    using System.Runtime.CompilerServices;
    using Drilling.Common.Interface.Vision;
    using Drilling.Common.Log;
    using Drilling.Common.Product;
    using Drilling.Common.Motion;
    using Drilling.Common.Station;
    using System.Collections;
    using System.Collections.Immutable;

    public sealed partial class CReviewManager(
        CReviewResultFileBase reviewResultFile, 
        CInterfaceManager interfaceManager, 
        CSettingManager settingManager, 
        CProductManager? productManager = null, 
        CLogManager? logManager = null
    ) : IReviewManager
    {
#region 공유 상태·초기값
        private const int DefaultReviewBeamNo = 1;
        private const int MaxHeadCount = 8;
        private const int DefaultHeadCount = 8;
        private const int DefaultCellCount = 20;
        private const int IofInspectionType = 0;
        private const double ReviewSequenceRowTolerance = 0.001;
        private static readonly ConditionalWeakTable<ST_RECIPE_DEFINITION, Dictionary<string, string>> RecipeParameterIndexes = new();
        private readonly SemaphoreSlim _sequenceLock = new(1, 1);
        private readonly object _stateLock = new();
        private int _stopRequested;
        private int _sequenceState = (int)EN_REVIEW_SEQUENCE_STATE.Idle;
        private ST_REVIEW_PLAN? _currentPlan;
        private ST_REVIEW_PLAN_POINT? _currentPoint;
        private ST_REVIEW_PLAN_POINT? _lastMeasuredPoint;
        private string? _reviewProductId;
        private CReviewStageMotion? _reviewStageMotion;
        private ST_REVIEW_PLAN? _interruptedReviewPlan;
        private string? _interruptedReviewProductId;
        public CFineReviewMonitorStore FineMonitor { get; } = new();
#endregion
#region 기본 처리
        public string? LastSavedResultPath { get; private set; }
        private double CameraResolution => settingManager.GetVisionCameraResolution();

        public ST_REVIEW_PLAN? CurrentPlan
        {
            get
            {
                lock (_stateLock)
                {
                    return _currentPlan;
                }
            }
        }

        public EN_REVIEW_SEQUENCE_STATE SequenceState { get => (EN_REVIEW_SEQUENCE_STATE)Volatile.Read(ref _sequenceState); private set => Volatile.Write(ref _sequenceState, (int)value); }
        private bool IsStopRequested => Volatile.Read(ref _stopRequested) != 0;

        public ST_REVIEW_PLAN CreatePlan(ST_RECIPE_DEFINITION recipe, IReadOnlyCollection<string> selectedShotKeys)
        {
            return CreatePlan(recipe, selectedShotKeys, 1);
        }

        public ST_REVIEW_PLAN CreatePlan(ST_RECIPE_DEFINITION recipe, IReadOnlyCollection<string> selectedShotKeys, int selectedBeamNo)
        {
            ValidateBeamNo(selectedBeamNo, ReadInt(recipe, 0, "SPLITED_BEAM_COUNT"));
            var selectedSet = selectedShotKeys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(NormalizeShotKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allPlan = CreateAllPlan(recipe, selectedBeamNo);
            if (selectedSet.Count == 0)
            {
                return allPlan with
                {
                    Points = []
                };
            }

            var selectedPoints = new List<ST_REVIEW_PLAN_POINT>(selectedSet.Count);
            foreach (var key in selectedSet)
            {
                if (!TryParseShotKey(key, out var cellNo, out var shotNo) || cellNo > allPlan.CellCount)
                {
                    continue;
                }

                var point = allPlan.FindPoint(cellNo, shotNo);
                if (point is null || !point.Use)
                {
                    continue;
                }

                selectedPoints.Add(point with { Use = true, State = EN_REVIEW_POINT_STATE.Ready, Judge = "WAIT" });
            }

            return allPlan with
            {
                Points = selectedPoints.OrderBy(point => point.PointNo).ToArray()
            };
        }

        public ST_REVIEW_PLAN_POINT? CurrentPoint
        {
            get
            {
                lock (_stateLock)
                {
                    return _currentPoint;
                }
            }
        }

        public ST_REVIEW_PLAN_POINT? LastMeasuredPoint
        {
            get
            {
                lock (_stateLock)
                {
                    return _lastMeasuredPoint;
                }
            }
        }

        public static IReadOnlyList<ST_REVIEW_BEAM_POSITION> GetShotBeamPositions(ST_RECIPE_DEFINITION recipe, ST_REVIEW_PLAN_POINT point, double stageScanDirectionY)
        {
            var calculate = CreateShotBeamPositionCalculator(recipe, stageScanDirectionY);
            var splitBeamCount = ReadInt(recipe, 0, "SPLITED_BEAM_COUNT");
            return Enumerable.Range(1, splitBeamCount * splitBeamCount).Select(beam => calculate(point, beam)).ToArray();
        }

        /// <summary>Build recipe lookup and Cell geometry once for a batch. No device access or live recipe mutation.</summary>
        public static Func<ST_REVIEW_PLAN_POINT, int, ST_REVIEW_BEAM_POSITION> CreateShotBeamPositionCalculator(ST_RECIPE_DEFINITION recipe, double stageScanDirectionY)
        {
            var splitBeamCount = ReadInt(recipe, 0, "SPLITED_BEAM_COUNT");
            ValidateBeamNo(1, splitBeamCount);
            var parameters = recipe.Parameters.GroupBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
            var pixelSize = ReadDouble(recipe, 0.0, "PIXEL_SIZE");
            var marginX = ReadDouble(recipe, 55.0, "AK_MARGIN_X", "ALIGN_MARGIN_X");
            var marginY = ReadDouble(recipe, 45.0, "AK_MARGIN_Y", "ALIGN_MARGIN_Y");
            var cameraAlignX = ReadDouble(recipe, 0.0, "REVIEW_CAMERA_ALIGN_KEY_POS_X");
            var cameraAlignY = ReadDouble(recipe, 0.0, "REVIEW_CAMERA_ALIGN_KEY_POS_Y");
            var cells = new System.Collections.Concurrent.ConcurrentDictionary<int, (double Rotation, double BeamPitch)>();
            return (point, beamNo) =>
            {
                ValidateBeamNo(beamNo, splitBeamCount);
                var cell = cells.GetOrAdd(point.CellNo, cellNo =>
                {
                    var model = CRecipeModelPixelCountReader.ReadPatternForCell(parameters, cellNo);
                    var rotation = ReadDouble(recipe, 0.0, $"CELL{cellNo}_ROTATION");
                    var layout = CCellPatternCalculator.Calculate(new ST_CELL_PATTERN_DEFINITION(cellNo, 0, 0, rotation, pixelSize, model.PixelCountX, model.PixelCountY, model.ProcessPitch, splitBeamCount), includeShotCenters: false);
                    if (!layout.IsValid)
                        throw new InvalidOperationException(layout.ValidationMessage);
                    return (rotation, layout.BeamPitchMillimeters);
                });
                var offset = CalculateBeamOffset(beamNo, splitBeamCount, cell.BeamPitch);
                var target = CalculateReviewTarget(point.DesignX - marginX, point.DesignY - marginY, cameraAlignX, cameraAlignY, offset.X, offset.Y, cell.Rotation, stageScanDirectionY);
                if (!double.IsFinite(target.X) || !double.IsFinite(target.Y))
                    throw new InvalidOperationException($"Beam {beamNo}의 목표 위치를 계산할 수 없습니다.");
                return new ST_REVIEW_BEAM_POSITION(beamNo, target.Y, target.X);
            };
        }

        public ST_REVIEW_PLAN CreateAllPlan(ST_RECIPE_DEFINITION recipe, int selectedBeamNo)
        {
            ValidateBeamNo(selectedBeamNo, ReadInt(recipe, 0, "SPLITED_BEAM_COUNT"));
            return CreatePlanCore(recipe, _ => true, selectedBeamNo, selectAllAssignedPoints: true);
        }

        /// <summary>
        /// Returns the same physical head scan fields used while assigning Review
        /// plan points.  Review UI uses these values to render a global Head scope
        /// exactly like Main, instead of inferring a range from Cell columns.
        /// </summary>
        public IReadOnlyList<ST_REVIEW_HEAD_SCOPE> GetHeadScopes(ST_RECIPE_DEFINITION recipe)
        {
            return LoadReviewCoordinateSettings(recipe).HeadLayout.Fields.Select(field => new ST_REVIEW_HEAD_SCOPE(field.HeadNo, field.PositionX, field.ScanFieldWidthX)).ToArray();
        }

        public Task<ST_REVIEW_SEQUENCE_STATUS> Start(ST_REVIEW_PLAN plan, string? productId, Action<ST_REVIEW_PLAN>? progress = null, CancellationToken cancellationToken = default)
        {
            return Start(plan, productId, null, false, progress, cancellationToken);
        }

        public async Task<ST_REVIEW_SEQUENCE_STATUS> Start(
            ST_REVIEW_PLAN plan, 
            string? productId, 
            ST_PRODUCT_REVIEW_DATA? checkpoint, 
            bool resumeCheckpoint, 
            Action<ST_REVIEW_PLAN>? progress = null, 
            CancellationToken cancellationToken = default
        )
        {
            if (!await _sequenceLock.WaitAsync(0, cancellationToken))
            {
                return CreateStatus(CurrentPlan ?? plan, EN_REVIEW_SEQUENCE_STATE.Running, "Review sequence is already running.");
            }

            string? reviewProductId = null;
            var runInitialized = false;
            try
            {
                LastSavedResultPath = null;
                reviewProductId = ResolveReviewProductId(plan, productId);
                SetReviewProductId(reviewProductId);
                Interlocked.Exchange(ref _stopRequested, 0);
                var unassignedPoint = plan.ReviewPoints.FirstOrDefault(point => point.HeadNo <= 0);
                if (unassignedPoint is not null)
                {
                    SequenceState = EN_REVIEW_SEQUENCE_STATE.Failed;
                    WriteReviewLog("START", "Review could not start because a processing Head is not assigned.");
                    return CreateStatus(plan, SequenceState, $"Review point {unassignedPoint.ShotKey} is not assigned to a processing Head.");
                }

                if (plan.ReviewPoints.Count == 0)
                {
                    SequenceState = EN_REVIEW_SEQUENCE_STATE.Failed;
                    WriteReviewLog("START", "Review could not start because there is no inspection target.");
                    return CreateStatus(plan, SequenceState, "Executable review point is empty.");
                }

                SequenceState = EN_REVIEW_SEQUENCE_STATE.Running;
                SetActivePoints(null, null);
                var workingPlan = resumeCheckpoint && checkpoint is not null ? RestoreCheckpoint(plan, checkpoint) : ResetPlanForRun(plan);
                workingPlan = PrepareRuntimePlan(workingPlan);
                SetCurrentPlan(workingPlan, progress);
                runInitialized = true;
                ValidateReviewMoveMethod(workingPlan);
                _reviewStageMotion = OpenReviewStageMotion(workingPlan.StageSpeed);
                WriteReviewLog(resumeCheckpoint && checkpoint is not null ? "RESUME" : "START", resumeCheckpoint && checkpoint is not null ? "Review resumed." : "Review started.");
                if (!string.IsNullOrWhiteSpace(reviewProductId) && productManager is not null)
                {
                    await productManager.SetReviewRunning(reviewProductId, CreateProductReviewData(workingPlan, resumeCheckpoint ? checkpoint?.SavedAt : null), cancellationToken);
                }

                if (!IsPointToPoint(workingPlan))
                {
                    return await RunIofSequence(workingPlan, reviewProductId, progress, cancellationToken, resumeFromCurrentPosition: resumeCheckpoint);
                }

                foreach (var point in OrderPtoPFromCurrentPosition(workingPlan.ReviewPoints.Where(point => point.State is not (EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng)), resumeCheckpoint))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng)
                    {
                        continue;
                    }

                    if (IsStopRequested)
                    {
                        SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                        workingPlan = SetWaitingPointsReady(workingPlan);
                        SetCurrentPlan(workingPlan, progress);
                        return await CreateStoppedStatus(workingPlan, reviewProductId, "Review sequence stopped.");
                    }

                    var currentPoint = point with
                    {
                        State = EN_REVIEW_POINT_STATE.Current,
                        Judge = "WAIT"
                    };
                    SetActivePoints(currentPoint, LastMeasuredPoint);
                    workingPlan = UpdatePoint(workingPlan, currentPoint);
                    SetCurrentPlan(workingPlan, progress);
                    await MoveStageY(currentPoint, cancellationToken);
                    if (IsStopRequested)
                    {
                        SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                        workingPlan = UpdatePoint(workingPlan, currentPoint with { State = EN_REVIEW_POINT_STATE.Ready });
                        SetCurrentPlan(workingPlan, progress);
                        return await CreateStoppedStatus(workingPlan, reviewProductId, "Review sequence stopped.");
                    }

                    await MoveVisionX(currentPoint, waitForMoveCompletion: true, cancellationToken);
                    if (IsStopRequested)
                    {
                        SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                        workingPlan = UpdatePoint(workingPlan, currentPoint with { State = EN_REVIEW_POINT_STATE.Ready });
                        SetCurrentPlan(workingPlan, progress);
                        return await CreateStoppedStatus(workingPlan, reviewProductId, "Review sequence stopped.");
                    }

                    var measurement = await MeasureVision(currentPoint, cancellationToken);
                    if (IsStopRequested)
                    {
                        SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                        workingPlan = UpdatePoint(workingPlan, currentPoint with { State = EN_REVIEW_POINT_STATE.Ready });
                        SetCurrentPlan(workingPlan, progress);
                        return await CreateStoppedStatus(workingPlan, reviewProductId, "Review sequence stopped.");
                    }

                    var measuredPoint = ApplyMeasurement(workingPlan, currentPoint, measurement);
                    SetActivePoints(null, measuredPoint);
                    workingPlan = UpdatePoint(workingPlan, measuredPoint);
                    SetCurrentPlan(workingPlan, progress);
                    await SaveProgressToProduct(workingPlan, reviewProductId, cancellationToken);
                }

                SequenceState = EN_REVIEW_SEQUENCE_STATE.Completed;
                SetCurrentPlan(workingPlan, progress);
                await SaveResultForProduct(workingPlan, workingPlan.ReviewPoints, reviewProductId, cancellationToken);
                WriteReviewLog("COMPLETE", "Review completed.");
                return CreateStatus(workingPlan, SequenceState, "Review sequence completed.");
            }
            catch (OperationCanceledException)
            {
                _reviewStageMotion?.Stop();
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                var stoppedPlan = SetWaitingPointsReady(CurrentPlan ?? plan);
                SetCurrentPlan(stoppedPlan, progress);
                return await CreateStoppedStatus(stoppedPlan, reviewProductId, "Review sequence canceled.");
            }
            catch (Exception ex)
            {
                _reviewStageMotion?.Stop();
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Failed;
                var failedPlan = SetWaitingPointsReady(CurrentPlan ?? plan);
                SetCurrentPlan(failedPlan, progress);
                WriteReviewError();
                var updateResult = await TrySetProductReviewError(reviewProductId, ex.Message);
                return CreateStatus(failedPlan, SequenceState, AppendUpdateResult(ex.Message, updateResult));
            }
            finally
            {
                if (runInitialized)
                    RememberInterruptedReview(reviewProductId);
                _reviewStageMotion?.Dispose();
                _reviewStageMotion = null;
                _sequenceLock.Release();
            }
        }

        public void Stop()
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            _reviewStageMotion?.Stop();
            var plan = CurrentPlan;
            WriteReviewLog("STOP_REQUEST", "Review stop requested.");
            if (SequenceState == EN_REVIEW_SEQUENCE_STATE.Running)
            {
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopping;
            }
        }

        public Task<ST_PRODUCT_REVIEW_DATA?> FindIncompleteProductReview(ST_REVIEW_PLAN plan, string glassId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FindInterruptedReviewCheckpoint(plan, glassId)is { } interrupted)
                return Task.FromResult<ST_PRODUCT_REVIEW_DATA?>(interrupted);
            if (string.IsNullOrWhiteSpace(glassId) || productManager?.Current is not { } product || !product.GlassId.Equals(glassId, StringComparison.OrdinalIgnoreCase) || !IsMatchingIncompleteReview(plan, product.RecipeId, product.Review))
            {
                return Task.FromResult<ST_PRODUCT_REVIEW_DATA?>(null);
            }

            return Task.FromResult<ST_PRODUCT_REVIEW_DATA?>(product.Review);
        }

        public ST_SCANNER_AXIS_TRANSFORM GetScannerAxisTransform(int headNo)
        {
            if (headNo is < 1 or > DefaultHeadCount)
            {
                throw new ArgumentOutOfRangeException(nameof(headNo), headNo, $"Head number must be between 1 and {DefaultHeadCount}.");
            }

            return CScannerAxisTransform.Read(settingManager.LoadSection(EN_SETTING_TAB.Option), headNo);
        }

        public async Task<ST_REVIEW_SEQUENCE_STATUS> RetryRemaining(Action<ST_REVIEW_PLAN>? progress = null, CancellationToken cancellationToken = default)
        {
            var plan = CurrentPlan;
            if (plan is null)
            {
                return new ST_REVIEW_SEQUENCE_STATUS(EN_REVIEW_SEQUENCE_STATE.Idle, 0, 0, 0, "Review plan is empty.");
            }

            plan = SetWaitingPointsReady(plan);
            SetCurrentPlan(plan, progress);
            var points = plan.ReviewPoints.Where(point => point.State == EN_REVIEW_POINT_STATE.Ready);
            var orderedPoints = OrderByReviewSequence(points).ToArray();
            if (orderedPoints.Length == 0)
            {
                return CreateStatus(plan, SequenceState, "Ready review point is empty.");
            }

            return await RunRetryPoints(orderedPoints, "Review ready point retry completed.", progress, cancellationToken);
        }

        private ST_REVIEW_PLAN CreatePlanCore(ST_RECIPE_DEFINITION recipe, Func<ST_REVIEW_PLAN_POINT, bool> useSelector, int selectedBeamNo = 1, bool selectAllAssignedPoints = false)
        {
            const int headCount = DefaultHeadCount;
            var cellCount = ReadRequiredCellCount(recipe, "MAX_CELL_NUMBER");
            var toleranceX = ReadDouble(recipe, 0.030, "REVIEW_TOLERANCE_X");
            var toleranceY = ReadDouble(recipe, 0.030, "REVIEW_TOLERANCE_Y");
            var coordinateSettings = LoadReviewCoordinateSettings(recipe);
            var runtimeCoordinateParameters = BuildRuntimeCoordinateParameters(recipe);
            var stageScanDirectionY = CShotCoordinatePlanBuilder.ReadDirection(runtimeCoordinateParameters, -1.0, "STAGE_SCAN_DIRECTION_Y");
            var processCoordinatePlan = CShotCoordinatePlanBuilder.Build(runtimeCoordinateParameters);
            var excludedShots = BuildReviewExcludedShots(runtimeCoordinateParameters, processCoordinatePlan.Shots);
            var assignedHeadNos = processCoordinatePlan.Shots.GroupBy(shot => (shot.CellNo, shot.CellShotNo)).ToDictionary(group => group.Key, group => group.Last().HeadNo);
            IReadOnlyList<ST_REVIEW_PLAN_POINT> points = BuildShotPoints(recipe, headCount, cellCount, coordinateSettings, assignedHeadNos, stageScanDirectionY, selectedBeamNo, selectAllAssignedPoints, excludedShots);
            if (!selectAllAssignedPoints)
            {
                points = points.Select(point =>
                {
                    var use = point.HeadNo > 0 && !excludedShots.ContainsKey(point.ShotKey) && useSelector(point);
                    return point with
                    {
                        BeamNo = selectedBeamNo,
                        Use = use,
                        State = use ? EN_REVIEW_POINT_STATE.Ready : EN_REVIEW_POINT_STATE.Skip,
                        Judge = use ? "WAIT" : "-"
                    };
                }).ToArray();
            }

            return new ST_REVIEW_PLAN(recipe.Id, cellCount, toleranceX, toleranceY, coordinateSettings.VisionFlipType, DateTimeOffset.Now, points)
            {
                StageScanDirectionY = stageScanDirectionY,
                StageSpeed = ReadDouble(recipe, 0, "STAGE_SPEED")
            };
        }

        private static IReadOnlyDictionary<string, string> BuildReviewExcludedShots(IReadOnlyDictionary<string, string> runtimeCoordinateParameters, IReadOnlyList<ST_HEAD_SHOT_POINT> processShots)
        {
            var maskingPlan = CRecipeShotMaskingPlan.Build(runtimeCoordinateParameters);
            var excludedShots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var shot in processShots)
            {
                var exclusion = maskingPlan.EvaluateShot(shot);
                if (!exclusion.ShouldSkipLaser)
                {
                    continue;
                }

                excludedShots[NormalizeShotKey(shot.ShotKey)] = exclusion.IsMasked && exclusion.IsChessSkipped ? "MASKING + CHESS" : exclusion.IsMasked ? "MASKING" : "CHESS";
            }

            return excludedShots;
        }

        private IReadOnlyDictionary<string, string> BuildRuntimeCoordinateParameters(ST_RECIPE_DEFINITION recipe)
        {
            var parameters = recipe.Parameters.Where(parameter => !string.IsNullOrWhiteSpace(parameter.Key)).GroupBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
            // Use the same effective parameter set as CStationProcess. Head use,
            // center and scan-field settings can be supplied by Option/Motor and
            // must override the recipe when assigning an actual processing head.
            foreach (var tab in new[]
            {
                EN_SETTING_TAB.Option,
                EN_SETTING_TAB.Motor
            }

            )
            {
                foreach (var setting in settingManager.LoadSection(tab))
                {
                    if (!string.IsNullOrWhiteSpace(setting.Key))
                    {
                        parameters[setting.Key] = setting.Value;
                    }
                }
            }

            return parameters;
        }

        private ST_REVIEW_COORDINATE_SETTINGS LoadReviewCoordinateSettings(ST_RECIPE_DEFINITION recipe)
        {
            var optionSettings = settingManager.LoadSection(EN_SETTING_TAB.Option);
            var visionFlipType = CReviewManager.ParseVisionFlipType(ReadSettingText(optionSettings, "", "VisionXFlip"), ReadSettingText(optionSettings, "", "VisionYFlip"), ReadSettingText(optionSettings, "", "VisionXyFlip"));
            var headFields = Enumerable.Range(1, 8).Select(headNo =>
            {
                var position = ReadHeadPositionX(optionSettings, headNo);
                const double fallbackWidthX = 110.0;
                var widthX = ReadSettingDouble(optionSettings, fallbackWidthX, $"H{headNo:00}_SCAN_FIELD_WIDTH_X", $"H{headNo:00}_HEAD_FIELD_WIDTH_X");
                return new ST_REVIEW_HEAD_FIELD(headNo, position, widthX > 0.0 ? widthX : fallbackWidthX);
            }).ToArray();
            // Keep Review assignment aligned with Main/Process: an explicitly
            // disabled head must not receive a review point.
            var activeHeadNos = headFields.Where(field => ReadSettingBoolean(optionSettings, true, $"H{field.HeadNo:00}_USE")).Select(field => field.HeadNo).ToArray();
            return new ST_REVIEW_COORDINATE_SETTINGS(new ST_REVIEW_HEAD_LAYOUT(headFields), activeHeadNos, visionFlipType);
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
                if (!keys.Any(key => key.Equals(setting.Key, StringComparison.OrdinalIgnoreCase) || key.Equals(setting.Name, StringComparison.OrdinalIgnoreCase)))
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

        private static bool ReadSettingBoolean(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, bool defaultValue, params string[] keys)
        {
            var text = ReadSettingText(settings, "", keys);
            if (string.IsNullOrWhiteSpace(text))
            {
                return defaultValue;
            }

            if (bool.TryParse(text, out var parsed))
            {
                return parsed;
            }

            return text.Trim().ToUpperInvariant() switch
            {
                "1" or "ON" or "YES" or "Y" => true,
                "0" or "OFF" or "NO" or "N" => false,
                _ => defaultValue
            };
        }

        private static string ReadSettingText(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, string defaultValue, params string[] keys)
        {
            foreach (var setting in settings)
            {
                if (!keys.Any(key => key.Equals(setting.Key, StringComparison.OrdinalIgnoreCase) || key.Equals(setting.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                return string.IsNullOrWhiteSpace(setting.Value) ? defaultValue : setting.Value.Trim();
            }

            return defaultValue;
        }

        private static IReadOnlyList<ST_REVIEW_PLAN_POINT> BuildShotPoints(
            ST_RECIPE_DEFINITION recipe, 
            int headCount, 
            int cellCount, 
            ST_REVIEW_COORDINATE_SETTINGS coordinateSettings, 
            IReadOnlyDictionary<(int CellNo, int CellShotNo), int> assignedHeadNos, 
            double stageScanDirectionY, 
            int selectedBeamNo, 
            bool selectAllAssignedPoints, 
            IReadOnlyDictionary<string, string> excludedShots
        )
        {
            var headLayout = coordinateSettings.HeadLayout;
            var akMarginX = ReadDouble(recipe, 55.0, "AK_MARGIN_X", "ALIGN_MARGIN_X");
            var akMarginY = ReadDouble(recipe, 45.0, "AK_MARGIN_Y", "ALIGN_MARGIN_Y");
            var reviewCameraAlignKeyPosX = ReadDouble(recipe, 0.0, "REVIEW_CAMERA_ALIGN_KEY_POS_X");
            var reviewCameraAlignKeyPosY = ReadDouble(recipe, 0.0, "REVIEW_CAMERA_ALIGN_KEY_POS_Y");
            var pixelSize = ReadDouble(recipe, 0.0, "PIXEL_SIZE");
            var splitBeamCount = ReadInt(recipe, 0, "SPLITED_BEAM_COUNT");
            var recipeParameterMap = recipe.Parameters.GroupBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
            var modelTypeCount = CRecipeModelPixelCountReader.ReadModelTypeCount(recipeParameterMap);
            ValidateBeamNo(selectedBeamNo, splitBeamCount);
            var definitions = new List<ST_CELL_PATTERN_DEFINITION>(cellCount);
            for (var cellNo = 1; cellNo <= cellCount; cellNo++)
            {
                var firstTargetX = ReadDouble(recipe, 0.0, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_X");
                var firstTargetY = ReadDouble(recipe, 0.0, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_Y");
                var rotation = ReadDouble(recipe, 0.0, $"CELL{cellNo}_ROTATION");
                var modelType = CRecipeModelPixelCountReader.ReadCellModelType(recipeParameterMap, cellNo, modelTypeCount);
                var modelPattern = CRecipeModelPixelCountReader.ReadPatternForModel(recipeParameterMap, modelType);
                var patternDefinition = new ST_CELL_PATTERN_DEFINITION(cellNo, firstTargetX, firstTargetY, rotation, pixelSize, modelPattern.PixelCountX, modelPattern.PixelCountY, modelPattern.ProcessPitch, splitBeamCount, akMarginX, akMarginY);
                definitions.Add(patternDefinition);
            }

            var cellCatalog = CRecipeCellPatternCatalog.Create(definitions);
            var allPointsSelected = false;
            if (selectAllAssignedPoints && excludedShots.Count == 0)
            {
                // The no-filter fast path is valid only if every catalog Shot has an active processing Head.
                // Count unique, in-range assignment keys without materializing the lazy coordinate list.
                var shotCounts = cellCatalog.Cells.ToDictionary(cell => cell.Definition.CellNo, cell => cell.Count);
                allPointsSelected = assignedHeadNos.Count(pair => pair.Value > 0 && shotCounts.TryGetValue(pair.Key.CellNo, out var count) && pair.Key.CellShotNo >= 1 && pair.Key.CellShotNo <= count) == cellCatalog.Count;
            }

            return new CLazyReviewPointList(cellCatalog, allPointsSelected, (cell, pointIndex) =>
            {
                var shotGroup = CRecipeCellPatternCatalog.GetShotCenter(cell, pointIndex);
                var beamOffset = CalculateBeamOffset(selectedBeamNo, splitBeamCount, cell.Layout.BeamPitchMillimeters);
                var cellNo = cell.Definition.CellNo;
                var shotName = CReviewShotNameFormatter.ToMatrixName(shotGroup.CellShotNo, cell.Layout.GroupCountX);
                var headNo = assignedHeadNos.TryGetValue((cellNo, shotGroup.CellShotNo), out var processHeadNo) ? processHeadNo : 0;
                var reviewTarget = CalculateReviewTarget(shotGroup.DesignX - akMarginX, shotGroup.DesignY - akMarginY, reviewCameraAlignKeyPosX, reviewCameraAlignKeyPosY, beamOffset.X, beamOffset.Y, cell.Definition.RotationDegrees, stageScanDirectionY);
                // TODO: Review coordinate transformation will be redesigned in a separate work item.
                // This final command-boundary guard remains intentional until that pipeline owns validation end to end.
                if (!double.IsFinite(reviewTarget.X) || !double.IsFinite(reviewTarget.Y))
                {
                    throw new InvalidOperationException($"Cell{cellNo} Shot Group {shotGroup.CellShotNo} produced a non-finite review target. " + $"X={reviewTarget.X:R}, Y={reviewTarget.Y:R}.");
                }

                var shotKey = ToShotKey(cellNo, shotGroup.CellShotNo);
                var isExcluded = excludedShots.TryGetValue(shotKey, out var exclusionReason);
                var use = selectAllAssignedPoints && headNo > 0 && !isExcluded;
                return new ST_REVIEW_PLAN_POINT(
                    cell.StartIndex + pointIndex + 1, 
                    shotKey, 
                    headNo, 
                    cellNo, 
                    shotGroup.CellShotNo, 
                    cell.Layout.GroupCountX, 
                    cell.Layout.GroupCountY, 
                    use, 
                    shotGroup.DesignX, 
                    shotGroup.DesignY, 
                    reviewTarget.X, 
                    reviewTarget.Y, 
                    0.0, 
                    0.0, 
                    use ? EN_REVIEW_POINT_STATE.Ready : EN_REVIEW_POINT_STATE.Skip, 
                    use ? "WAIT" : "-"
                )
                {
                    BeamNo = selectedBeamNo,
                    BeamOffsetGx = beamOffset.X,
                    BeamOffsetGy = beamOffset.Y,
                    ExclusionReason = exclusionReason ?? ""
                };
            });
        }

        private static IReadOnlyList<ST_REVIEW_PLAN_POINT> GetMeasuredResultRows(IReadOnlyList<ST_REVIEW_PLAN_POINT> results)
        {
            var measuredRows = new List<ST_REVIEW_PLAN_POINT>();
            foreach (var point in results)
            {
                if (point.Use && point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng)
                {
                    measuredRows.Add(point);
                }
            }

            return measuredRows;
        }

        public static IReadOnlyList<ST_REVIEW_PLAN_POINT> ProjectCorrectionRows(ST_REVIEW_PLAN plan, IReadOnlyList<ST_REVIEW_PLAN_POINT> measuredResults)
        {
            if (plan.CorrectionPlan is not { SourcePoints.Count: > 0 } projection)
            {
                return measuredResults;
            }

            var measuredPoints = measuredResults.Where(point => point.Use && point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng).ToArray();
            if (measuredPoints.Length == 0)
            {
                return measuredResults;
            }

            return projection.Rule switch
            {
                EN_REVIEW_CORRECTION_RULE.Simple => ProjectSimpleResultRows(projection, measuredPoints),
                EN_REVIEW_CORRECTION_RULE.Rough => ProjectRoughResultRows(projection, measuredPoints),
                EN_REVIEW_CORRECTION_RULE.Fine => ProjectFineResultRows(projection.SourcePoints, measuredPoints),
                EN_REVIEW_CORRECTION_RULE.Edge => measuredPoints,
                _ => measuredPoints
            };
        }

        private static IReadOnlyList<ST_REVIEW_PLAN_POINT> ProjectSimpleResultRows(ST_REVIEW_CORRECTION_PLAN projection, IReadOnlyList<ST_REVIEW_PLAN_POINT> measuredPoints)
        {
            if (measuredPoints.Count != 1)
            {
                return measuredPoints;
            }

            var measuredPoint = measuredPoints[0];
            return projection.SourcePoints.Where(point => point.HeadNo == measuredPoint.HeadNo && (projection.Scope != EN_REVIEW_CORRECTION_SCOPE.HeadCell || point.CellNo == measuredPoint.CellNo)).Where(point => point.HeadNo > 0).Select(point => CopyMeasurement(point, measuredPoint)).ToArray();
        }

        private static IReadOnlyList<ST_REVIEW_PLAN_POINT> ProjectRoughResultRows(ST_REVIEW_CORRECTION_PLAN projection, IReadOnlyList<ST_REVIEW_PLAN_POINT> measuredPoints)
        {
            var measurements = measuredPoints.GroupBy(point => projection.Scope == EN_REVIEW_CORRECTION_SCOPE.HeadCell ? (point.HeadNo, point.CellNo) : (point.HeadNo, 0)).ToDictionary(group => group.Key, group => group.First());
            return projection.SourcePoints.Where(point => point.HeadNo > 0).Select(point => new { Point = point, Key = projection.Scope == EN_REVIEW_CORRECTION_SCOPE.HeadCell ? (point.HeadNo, point.CellNo) : (point.HeadNo, 0) }).Where(item => measurements.ContainsKey(item.Key)).Select(item => CopyMeasurement(item.Point, measurements[item.Key])).ToArray();
        }

        private static IReadOnlyList<ST_REVIEW_PLAN_POINT> ProjectFineResultRows(IReadOnlyList<ST_REVIEW_PLAN_POINT> sourcePoints, IReadOnlyList<ST_REVIEW_PLAN_POINT> measuredPoints)
        {
            var anchorsByHeadAndX = measuredPoints.Where(point => point.HeadNo > 0 && double.IsFinite(point.DesignX) && double.IsFinite(point.DesignY)).GroupBy(point => (point.HeadNo, X: FineCoordinateKey(point.DesignX))).ToDictionary(group => group.Key, group => group.GroupBy(point => FineCoordinateKey(point.DesignY)).Select(yGroup => yGroup.First()).OrderBy(point => point.DesignY).ToArray());
            var rows = new List<ST_REVIEW_PLAN_POINT>(sourcePoints.Count);
            foreach (var point in sourcePoints.Where(point => point.HeadNo > 0 && double.IsFinite(point.DesignX) && double.IsFinite(point.DesignY)))
            {
                if (!anchorsByHeadAndX.TryGetValue((point.HeadNo, FineCoordinateKey(point.DesignX)), out var anchors) || anchors.Length == 0)
                {
                    continue;
                }

                rows.Add(InterpolateFineMeasurementAcrossGlass(point, anchors));
            }

            return rows;
        }

        private static ST_REVIEW_PLAN_POINT InterpolateFineMeasurementAcrossGlass(ST_REVIEW_PLAN_POINT point, IReadOnlyList<ST_REVIEW_PLAN_POINT> anchors)
        {
            var projected = InterpolateFineMeasurement(point, anchors);
            var judge = anchors.Any(anchor => anchor.Judge.Equals("NG", StringComparison.OrdinalIgnoreCase)) ? "NG" : "OK";
            return projected with
            {
                State = judge == "NG" ? EN_REVIEW_POINT_STATE.Ng : EN_REVIEW_POINT_STATE.Ok,
                Judge = judge
            };
        }

        private static ST_REVIEW_PLAN_POINT InterpolateFineMeasurement(ST_REVIEW_PLAN_POINT point, IReadOnlyList<ST_REVIEW_PLAN_POINT> anchors)
        {
            if (anchors.Count == 1 || point.DesignY <= anchors[0].DesignY)
            {
                return CopyMeasurement(point, anchors[0]);
            }

            if (point.DesignY >= anchors[^1].DesignY)
            {
                return CopyMeasurement(point, anchors[^1]);
            }

            for (var index = 1; index < anchors.Count; index++)
            {
                var upper = anchors[index];
                if (point.DesignY > upper.DesignY)
                {
                    continue;
                }

                var lower = anchors[index - 1];
                var distance = upper.DesignY - lower.DesignY;
                var ratio = Math.Abs(distance) <= double.Epsilon ? 0.0 : Math.Clamp((point.DesignY - lower.DesignY) / distance, 0.0, 1.0);
                var judge = lower.Judge.Equals("NG", StringComparison.OrdinalIgnoreCase) || upper.Judge.Equals("NG", StringComparison.OrdinalIgnoreCase) ? "NG" : "OK";
                return point with
                {
                    Use = true,
                    ErrorX = lower.ErrorX + ((upper.ErrorX - lower.ErrorX) * ratio),
                    ErrorY = lower.ErrorY + ((upper.ErrorY - lower.ErrorY) * ratio),
                    State = judge == "NG" ? EN_REVIEW_POINT_STATE.Ng : EN_REVIEW_POINT_STATE.Ok,
                    Judge = judge
                };
            }

            return CopyMeasurement(point, anchors[^1]);
        }

        private static ST_REVIEW_PLAN_POINT CopyMeasurement(ST_REVIEW_PLAN_POINT target, ST_REVIEW_PLAN_POINT measurement) => target with
        {
            Use = true,
            ErrorX = measurement.ErrorX,
            ErrorY = measurement.ErrorY,
            State = measurement.State,
            Judge = measurement.Judge
        };
        private static double FineCoordinateKey(double value) => Math.Round(value, 6);
        private sealed class CLazyReviewPointList(CRecipeCellPatternCatalog catalog, bool areAllPointsSelected, Func<ST_RECIPE_CELL_PATTERN_ENTRY, int, ST_REVIEW_PLAN_POINT> pointFactory) : IReadOnlyList<ST_REVIEW_PLAN_POINT>, IReviewPointListInfo, IReviewCellPointSource
        {
            private readonly ST_REVIEW_PLAN_POINT? [] _pointCache = new ST_REVIEW_PLAN_POINT? [catalog.Count];
            public int Count => catalog.Count;
            public bool AreAllPointsSelected => areAllPointsSelected;
            public int PatternCellCount => catalog.CellCount;

            public ST_REVIEW_CELL_POINT_RANGE GetCellRange(int zeroBasedCellIndex)
            {
                if ((uint)zeroBasedCellIndex >= (uint)catalog.CellCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(zeroBasedCellIndex));
                }

                var cell = catalog.Cells[zeroBasedCellIndex];
                return new ST_REVIEW_CELL_POINT_RANGE(cell.StartIndex, cell.Count, cell.Layout.GroupCountX, cell.Layout.GroupCountY);
            }

            public ST_REVIEW_PLAN_POINT this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)Count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }

                    var cached = Volatile.Read(ref _pointCache[index]);
                    if (cached is not null)
                    {
                        return cached;
                    }

                    var(cell, pointIndex) = catalog.Resolve(index);
                    var created = pointFactory(cell, pointIndex);
                    return Interlocked.CompareExchange(ref _pointCache[index], created, null) ?? created;
                }
            }

            public IEnumerator<ST_REVIEW_PLAN_POINT> GetEnumerator()
            {
                for (var index = 0; index < Count; index++)
                {
                    yield return this[index];
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static ST_REVIEW_COORDINATE_OFFSET CalculateBeamOffset(int beamNo, int splitBeamCount, double doePitch)
        {
            ValidateBeamNo(beamNo, splitBeamCount);
            if (!double.IsFinite(doePitch) || doePitch <= 0.0)
            {
                throw new InvalidOperationException($"DOE pitch must be a finite value greater than 0. Current value: {doePitch}");
            }

            var normalizedBeamNo = beamNo - 1;
            var column = normalizedBeamNo % splitBeamCount;
            var row = normalizedBeamNo / splitBeamCount;
            var center = (splitBeamCount - 1) / 2.0;
            var offsetX = (column - center) * doePitch;
            var offsetY = (row - center) * doePitch;
            if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            {
                throw new InvalidOperationException("DOE beam offset calculation produced a non-finite value.");
            }

            return new ST_REVIEW_COORDINATE_OFFSET(offsetX, offsetY);
        }

        private static void ValidateBeamNo(int beamNo, int splitBeamCount = 4)
        {
            if (!CCellPatternCalculator.IsSupportedSplitBeamCount(splitBeamCount))
            {
                throw new InvalidOperationException("SPLITED_BEAM_COUNT must be either 1 or 4 before a review beam can be selected.");
            }

            var maximumBeamNo = (long)splitBeamCount * splitBeamCount;
            if (beamNo < 1 || beamNo > maximumBeamNo)
            {
                throw new ArgumentOutOfRangeException(nameof(beamNo), beamNo, $"Review beam number must be between 1 and {maximumBeamNo}.");
            }
        }

        private static ST_REVIEW_COORDINATE_OFFSET CalculateReviewTarget(
            double shotFromAlignKeyX, 
            double shotFromAlignKeyY, 
            double reviewCameraAlignKeyPosX, 
            double reviewCameraAlignKeyPosY, 
            double beamOffsetLocalX, 
            double beamOffsetLocalY, 
            double cellRotationDegrees, 
            double stageScanDirectionY
        )
        {
            var beamOffset = RotateLocalOffset(beamOffsetLocalX, beamOffsetLocalY, cellRotationDegrees);
            return new ST_REVIEW_COORDINATE_OFFSET(reviewCameraAlignKeyPosX + shotFromAlignKeyX + beamOffset.X, reviewCameraAlignKeyPosY + (stageScanDirectionY * (shotFromAlignKeyY + beamOffset.Y)));
        }

        private static ST_REVIEW_COORDINATE_OFFSET RotateLocalOffset(double localX, double localY, double rotationDegrees)
        {
            var radians = rotationDegrees * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            return new ST_REVIEW_COORDINATE_OFFSET((localX * cos) - (localY * sin), (localX * sin) + (localY * cos));
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        private void WriteReviewLog(string action, string detail)
        {
            if (logManager is null)
            {
                return;
            }

            try
            {
                logManager.WriteSequence("INFO", $"REVIEW_{action}", detail);
                logManager.WriteStationState("PROCESS", "INSPECTION", $"REVIEW_{action}", detail);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError($"Review log write failed. Action={action}, Error={exception.Message}");
            }
        }

        private void WriteReviewError()
        {
            WriteReviewLog("ERROR", "Review failed.");
        }

        private void SetCurrentPlan(ST_REVIEW_PLAN plan, Action<ST_REVIEW_PLAN>? progress)
        {
            lock (_stateLock)
            {
                _currentPlan = plan;
            }

            progress?.Invoke(plan);
        }

        private void SetActivePoints(ST_REVIEW_PLAN_POINT? currentPoint, ST_REVIEW_PLAN_POINT? lastMeasuredPoint)
        {
            lock (_stateLock)
            {
                _currentPoint = currentPoint;
                _lastMeasuredPoint = lastMeasuredPoint;
            }
        }

        private void SetReviewProductId(string? productId)
        {
            lock (_stateLock)
            {
                _reviewProductId = string.IsNullOrWhiteSpace(productId) ? null : productId;
            }
        }

        private string? GetReviewProductId()
        {
            lock (_stateLock)
            {
                return _reviewProductId;
            }
        }

        private static ST_REVIEW_PLAN ResetPlanForRun(ST_REVIEW_PLAN plan)
        {
            var firstShotKey = OrderByReviewSequence(plan.ReviewPoints).Select(point => point.ShotKey).FirstOrDefault();
            return plan with
            {
                Points = plan.Points.Select(point => point.Use ? point with { ErrorX = 0.0, ErrorY = 0.0, State = point.ShotKey.Equals(firstShotKey, StringComparison.OrdinalIgnoreCase) ? EN_REVIEW_POINT_STATE.Current : EN_REVIEW_POINT_STATE.Ready, Judge = "WAIT" } : point with { State = EN_REVIEW_POINT_STATE.Skip, Judge = "-" }).ToArray()
            };
        }

        private static ST_REVIEW_PLAN SetWaitingPointsReady(ST_REVIEW_PLAN plan)
        {
            return UpdatePoints(plan, plan.ReviewPoints.Where(point => point.State == EN_REVIEW_POINT_STATE.Current).Select(point => point with { State = EN_REVIEW_POINT_STATE.Ready }).ToArray());
        }

        private static ST_REVIEW_PLAN PrepareRuntimePlan(ST_REVIEW_PLAN plan) => plan with
        {
            Points = plan.Points is CReviewRuntimePoints ? plan.Points : new CReviewRuntimePoints(plan.Points),
            RuntimeId = Guid.NewGuid(),
            PointUpdates = null
        };
        private static ST_REVIEW_PLAN UpdatePoints(ST_REVIEW_PLAN plan, IReadOnlyList<ST_REVIEW_PLAN_POINT> updates)
        {
            var points = plan.Points as CReviewRuntimePoints ?? new CReviewRuntimePoints(plan.Points);
            return plan with
            {
                Points = points.Update(updates),
                PointUpdates = updates
            };
        }

        private static ST_REVIEW_PLAN UpdatePoint(ST_REVIEW_PLAN plan, ST_REVIEW_PLAN_POINT point)
        {
            return UpdatePoints(plan, [point]);
        }

        private static ST_REVIEW_SEQUENCE_STATUS CreateStatus(ST_REVIEW_PLAN plan, EN_REVIEW_SEQUENCE_STATE state, string message)
        {
            var reviewPoints = plan.ReviewPoints;
            var completedCount = reviewPoints.Count(point => point.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng);
            var ngCount = reviewPoints.Count(point => point.State == EN_REVIEW_POINT_STATE.Ng);
            return new ST_REVIEW_SEQUENCE_STATUS(state, reviewPoints.Count, completedCount, ngCount, message);
        }

        private sealed record ST_REVIEW_MEASURE_RESULT(double X, double Y, string Judge, string RawResponse)
        {
            public ST_VISION_MARK_FIND? MarkFind { get; init; }
            public int Resolution { get; init; } = 1;
            public double CameraResolution { get; init; } = CSettingManager.DefaultVisionCameraResolution;
            public ST_REVIEW_COORDINATE_OFFSET? ErrorMillimeters { get; init; }
        }

        private enum EN_PRODUCT_REVIEW_UPDATE_OUTCOME
        {
            Success,
            NotApplicable,
            ProductReplaced,
            Failed
        }

        private sealed record ST_PRODUCT_REVIEW_UPDATE_RESULT(EN_PRODUCT_REVIEW_UPDATE_OUTCOME Outcome, string Detail)
        {
            public static readonly ST_PRODUCT_REVIEW_UPDATE_RESULT Success = new(EN_PRODUCT_REVIEW_UPDATE_OUTCOME.Success, string.Empty);
            public static readonly ST_PRODUCT_REVIEW_UPDATE_RESULT NotApplicable = new(EN_PRODUCT_REVIEW_UPDATE_OUTCOME.NotApplicable, string.Empty);
        }

        private static IReadOnlyList<ST_REVIEW_PLAN_POINT> OrderByReviewSequence(IEnumerable<ST_REVIEW_PLAN_POINT> points)
        {
            static double GetDesignY(ST_REVIEW_PLAN_POINT point)
            {
                return point.DesignY;
            }

            static double GetDesignX(ST_REVIEW_PLAN_POINT point)
            {
                return point.DesignX;
            }

            static int GetCellNo(ST_REVIEW_PLAN_POINT point)
            {
                return point.CellNo;
            }

            static int GetShotNo(ST_REVIEW_PLAN_POINT point)
            {
                return point.ShotNo;
            }

            var source = points.OrderBy(GetDesignY).ThenBy(GetDesignX).ThenBy(GetCellNo).ThenBy(GetShotNo).ToArray();
            var rows = new List<List<ST_REVIEW_PLAN_POINT>>();
            foreach (var point in source)
            {
                if (rows.Count == 0 || Math.Abs(point.DesignY - rows[^1][0].DesignY) > ReviewSequenceRowTolerance)
                {
                    rows.Add(new List<ST_REVIEW_PLAN_POINT>());
                }

                rows[^1].Add(point);
            }

            var orderedPoints = new List<ST_REVIEW_PLAN_POINT>(source.Length);
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var orderedRow = rowIndex % 2 == 0 ? row.OrderBy(GetDesignX).ThenBy(GetCellNo).ThenBy(GetShotNo) : row.OrderByDescending(GetDesignX).ThenBy(GetCellNo).ThenBy(GetShotNo);
                orderedPoints.AddRange(orderedRow);
            }

            return orderedPoints;
        }

        private sealed record ST_REVIEW_HEAD_LAYOUT(IReadOnlyList<ST_REVIEW_HEAD_FIELD> Fields)
        {
            public ST_REVIEW_HEAD_FIELD GetFieldOrUnassigned(int headNo)
            {
                return Fields.FirstOrDefault(field => field.HeadNo == headNo) ?? new ST_REVIEW_HEAD_FIELD(0, 0.0, 0.0);
            }
        }

        private sealed record ST_REVIEW_HEAD_FIELD(int HeadNo, double PositionX, double ScanFieldWidthX);
        private sealed record ST_REVIEW_COORDINATE_SETTINGS(ST_REVIEW_HEAD_LAYOUT HeadLayout, IReadOnlyList<int> ActiveHeadNos, EN_VISION_FLIP_TYPE VisionFlipType);
        public static string ToShotKey(int cellNo, int shotNo)
        {
            return $"CELL{Math.Max(1, cellNo):000}_SHOT{Math.Max(1, shotNo):0000}";
        }

        public static string NormalizeShotKey(string value)
        {
            var text = value.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(text))
            {
                return "";
            }

            var shotIndex = text.IndexOf("_SHOT", StringComparison.OrdinalIgnoreCase);
            const int keyLength = 5;
            if (shotIndex < 0)
            {
                shotIndex = text.IndexOf("_HOLE", StringComparison.OrdinalIgnoreCase);
            }

            if (text.StartsWith("CELL", StringComparison.OrdinalIgnoreCase) && shotIndex > 4)
            {
                var cellDigits = new string (text[4..shotIndex].Where(char.IsDigit).ToArray());
                var shotDigits = new string (text[(shotIndex + keyLength)..].Where(char.IsDigit).ToArray());
                if (int.TryParse(cellDigits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cellNo) && int.TryParse(shotDigits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shotNo) && cellNo > 0 && shotNo > 0)
                {
                    return ToShotKey(cellNo, shotNo);
                }
            }

            return "";
        }

        private static bool TryParseShotKey(string value, out int cellNo, out int shotNo)
        {
            cellNo = 0;
            shotNo = 0;
            var normalized = NormalizeShotKey(value);
            var separator = normalized.IndexOf("_SHOT", StringComparison.Ordinal);
            return separator > 4 && int.TryParse(normalized.AsSpan(4, separator - 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out cellNo) && int.TryParse(normalized.AsSpan(separator + 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out shotNo) && cellNo > 0 && shotNo > 0;
        }

        private static int ReadInt(ST_RECIPE_DEFINITION recipe, int defaultValue, params string[] keys)
        {
            var value = ReadText(recipe, keys);
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            {
                return intValue;
            }

            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue) ? (int)Math.Round(doubleValue) : defaultValue;
        }

        private static int ReadRequiredCellCount(ST_RECIPE_DEFINITION recipe, string key)
        {
            var parameter = FindParameter(recipe, key);
            var value = parameter?.Value?.Trim() ?? "";
            var minimum = GetParameterMinimum(parameter);
            var maximum = GetParameterMaximum(parameter, minimum);
            if (CCellPatternCalculator.TryParseCellCount(value, minimum, maximum, out var cellCount, out var validationMessage))
            {
                return cellCount;
            }

            throw new InvalidOperationException(validationMessage);
        }

        private static ST_RECIPE_PARAMETER? FindParameter(ST_RECIPE_DEFINITION recipe, string key)
        {
            return recipe.Parameters.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || item.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        private static int GetParameterMinimum(ST_RECIPE_PARAMETER? parameter)
        {
            return parameter is not null && parameter.Max > parameter.Min ? Math.Max(CCellPatternCalculator.MinCellCount, (int)Math.Ceiling(parameter.Min)) : CCellPatternCalculator.MinCellCount;
        }

        private static int? GetParameterMaximum(ST_RECIPE_PARAMETER? parameter, int minimum)
        {
            return parameter is not null && parameter.Max >= minimum ? (int)Math.Floor(parameter.Max) : null;
        }

        private static double ReadDouble(ST_RECIPE_DEFINITION recipe, double defaultValue, params string[] keys)
        {
            var value = ReadText(recipe, keys);
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue) ? doubleValue : defaultValue;
        }

        private static string ReadText(ST_RECIPE_DEFINITION recipe, params string[] keys)
        {
            var parameters = RecipeParameterIndexes.GetValue(recipe, static definition => CreateRecipeParameterIndex(definition));
            foreach (var key in keys)
            {
                if (parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return "";
        }

        private static Dictionary<string, string> CreateRecipeParameterIndex(ST_RECIPE_DEFINITION recipe)
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

#endregion
#region BeamInspection
        public static IReadOnlyList<ST_REVIEW_PLAN_POINT> BuildBeamInspectionTargets(ST_RECIPE_DEFINITION recipe, ST_REVIEW_PLAN allPlan, IReadOnlyCollection<string> shotKeys, IReadOnlyCollection<int> beamNos)
        {
            if (shotKeys.Count == 0 || beamNos.Count == 0)
                throw new InvalidOperationException("Shot과 Beam을 각각 하나 이상 선택해 주세요.");
            var beams = beamNos.Distinct().Order().ToArray();
            var splitCount = ReadInt(recipe, 0, "SPLITED_BEAM_COUNT");
            foreach (var beam in beams)
                ValidateBeamNo(beam, splitCount);
            var shots = shotKeys.Distinct(StringComparer.OrdinalIgnoreCase).Select(key => TryParseShotKey(key, out var cell, out var shot) ? allPlan.FindPoint(cell, shot) : null).ToArray();
            if (shots.Any(point => point is null || point.HeadNo <= 0 || !string.IsNullOrWhiteSpace(point.ExclusionReason)))
                throw new InvalidOperationException("선택한 Shot에 미할당 또는 MASKING/CHESS 제외 항목이 있습니다. 선택을 확인해 주세요.");
            var targets = new List<ST_REVIEW_PLAN_POINT>();
            foreach (var shot in shots.OfType<ST_REVIEW_PLAN_POINT>().OrderBy(p => p.CellNo).ThenBy(p => p.ShotNo))
            {
                var positions = GetShotBeamPositions(recipe, shot, allPlan.StageScanDirectionY).ToDictionary(p => p.BeamNo);
                foreach (var beam in beams)
                {
                    var position = positions[beam];
                    targets.Add(shot with { PointNo = targets.Count + 1, BeamNo = beam, Use = true, ReviewTargetX = position.ReviewX, ReviewTargetY = position.StageY, State = EN_REVIEW_POINT_STATE.Ready, Judge = "WAIT", ErrorX = 0, ErrorY = 0 });
                }
            }

            return targets;
        }

        public async Task<ST_BEAM_INSPECTION_SNAPSHOT> StartBeamInspection(
            ST_RECIPE_DEFINITION recipe, 
            ST_REVIEW_PLAN allPlan, 
            IReadOnlyCollection<string> shotKeys, 
            IReadOnlyCollection<int> beamNos, 
            Action<ST_BEAM_INSPECTION_SNAPSHOT> progress, 
            CancellationToken cancellationToken = default
        )
        {
            var targets = BuildBeamInspectionTargets(recipe, allPlan, shotKeys, beamNos).ToArray();
            var stageSimulation = IsReviewDeviceSimulation(EN_EQP_MODULE.Melsec, GetReviewStageDeviceNo());
            var visionSimulation = IsReviewSimulation();
            if (!await _sequenceLock.WaitAsync(0, cancellationToken))
                throw new InvalidOperationException("다른 Review 검사가 진행 중입니다.");
            var activeIndex = -1;
            CReviewStageMotion? motion = null;
            var glassId = productManager?.Current?.GlassId ?? "";
            ST_BEAM_INSPECTION_SNAPSHOT Publish(string message)
            {
                var snapshot = new ST_BEAM_INSPECTION_SNAPSHOT(recipe.Id, Array.AsReadOnly(targets.ToArray()), SequenceState, activeIndex, message, stageSimulation || visionSimulation)
                {
                    IsStageSimulation = stageSimulation,
                    IsVisionSimulation = visionSimulation
                };
                progress(snapshot);
                return snapshot;
            }

            async Task<ST_BEAM_INSPECTION_SNAPSHOT> Finish(string message)
            {
                motion?.Dispose();
                motion = null;
                var snapshot = new ST_BEAM_INSPECTION_SNAPSHOT(recipe.Id, Array.AsReadOnly(targets.ToArray()), SequenceState, -1, message, stageSimulation || visionSimulation)
                {
                    IsStageSimulation = stageSimulation,
                    IsVisionSimulation = visionSimulation
                };
                try
                {
                    // A user stop cancels motion, but must not discard measurements already taken.
                    var path = await reviewResultFile.SaveBeamInspection(snapshot, glassId, DateTimeOffset.Now, CancellationToken.None);
                    snapshot = snapshot with
                    {
                        SavedPath = path
                    };
                }
                catch (Exception exception)
                {
                    snapshot = snapshot with
                    {
                        SaveError = exception.Message
                    };
                }

                progress(snapshot);
                return snapshot;
            }

            try
            {
                Interlocked.Exchange(ref _stopRequested, 0);
                motion = OpenReviewStageMotion(allPlan.StageSpeed);
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Running;
                Publish("Beam Inspection 시작");
                for (var index = 0; index < targets.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsStopRequested)
                        break;
                    activeIndex = index;
                    targets[index] = targets[index] with
                    {
                        State = EN_REVIEW_POINT_STATE.Current
                    };
                    Publish("측정 중");
                    await motion.MoveAndWait(targets[index], cancellationToken);
                    await Task.Delay(250, cancellationToken);
                    if (IsStopRequested)
                        break;
                    var measurement = await MeasureVision(targets[index], cancellationToken);
                    targets[index] = ApplyMeasurement(allPlan, targets[index], measurement);
                    Publish("측정 완료");
                }

                SequenceState = targets.All(p => p.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng) ? EN_REVIEW_SEQUENCE_STATE.Completed : EN_REVIEW_SEQUENCE_STATE.Stopped;
                for (var i = 0; i < targets.Length; i++)
                    if (targets[i].State == EN_REVIEW_POINT_STATE.Current)
                        targets[i] = targets[i] with
                        {
                            State = EN_REVIEW_POINT_STATE.Ready
                        };
                activeIndex = -1;
                return await Finish(SequenceState == EN_REVIEW_SEQUENCE_STATE.Completed ? "검사 완료" : "검사 중지");
            }
            catch (OperationCanceledException)
            {
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                if (activeIndex >= 0 && targets[activeIndex].State == EN_REVIEW_POINT_STATE.Current)
                    targets[activeIndex] = targets[activeIndex] with
                    {
                        State = EN_REVIEW_POINT_STATE.Ready
                    };
                activeIndex = -1;
                return await Finish("검사 취소");
            }
            catch (Exception exception)
            {
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Failed;
                if (activeIndex >= 0 && targets[activeIndex].State == EN_REVIEW_POINT_STATE.Current)
                    targets[activeIndex] = targets[activeIndex] with
                    {
                        State = EN_REVIEW_POINT_STATE.Ready,
                        Judge = "ERROR"
                    };
                activeIndex = -1;
                return await Finish(exception.Message);
            }
            finally
            {
                motion?.Dispose();
                _sequenceLock.Release();
            }
        }

        public Task<ST_BEAM_INSPECTION_RESULT_FILE_DATA?> LoadLastBeamInspection(CancellationToken cancellationToken = default) => reviewResultFile.LoadLastBeamInspection(cancellationToken);
#endregion
#region Execution
        private string? ResolveReviewProductId(ST_REVIEW_PLAN plan, string? productId)
        {
            if (string.IsNullOrWhiteSpace(productId) || productManager?.Current is not { } product)
            {
                return null;
            }

            if (!product.GlassId.Equals(productId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Review Glass ID mismatch. Current={product.GlassId}, Input={productId}");
            }

            if (!string.IsNullOrWhiteSpace(product.RecipeId) && !product.RecipeId.Equals(plan.RecipeId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Review Recipe mismatch. Product={product.RecipeId}, Review={plan.RecipeId}");
            }

            return product.GlassId;
        }

        private async Task SaveResultForProduct(ST_REVIEW_PLAN plan, IReadOnlyList<ST_REVIEW_PLAN_POINT> results, string? reviewProductId, CancellationToken cancellationToken)
        {
            var savedAt = DateTimeOffset.Now;
            var resultFileRows = GetMeasuredResultRows(results);
            await reviewResultFile.Save(new ST_REVIEW_RESULT_DATA(plan, resultFileRows, savedAt, reviewProductId ?? "", plan.InspectionMethod) { ResultStatus = "COMPLETED", TotalCount = plan.ReviewPoints.Count, TargetFilePath = null }, cancellationToken).ConfigureAwait(false);
            LastSavedResultPath = reviewResultFile.LastSavedPath;
            WriteReviewLog("RESULT_FILE_SAVED", "Review result file saved.");
            if (string.IsNullOrWhiteSpace(reviewProductId) || productManager is null)
            {
                return;
            }

            var productResults = CreateProductReviewResults(results);
            await productManager.SetReviewCompleted(reviewProductId, savedAt, productResults, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task SaveProgressToProduct(ST_REVIEW_PLAN plan, string? reviewProductId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(reviewProductId) || productManager is null)
            {
                return;
            }

            await productManager.SetReviewProgress(reviewProductId, CreateProductReviewData(plan, DateTimeOffset.Now), cancellationToken).ConfigureAwait(false);
        }

        private static ST_PRODUCT_REVIEW_DATA CreateProductReviewData(ST_REVIEW_PLAN plan, DateTimeOffset? savedAt)
        {
            var projection = plan.CorrectionPlan;
            ST_REVIEW_PLAN_POINT? referencePoint = null;
            foreach (var point in plan.ReviewPoints)
            {
                referencePoint = point;
                break;
            }

            return new ST_PRODUCT_REVIEW_DATA(EN_PRODUCT_PROCESS_STATE.Running, savedAt, CreateProductReviewResults(plan.ReviewPoints))
            {
                InspectionMethod = plan.InspectionMethod,
                RecipeId = plan.RecipeId,
                Rule = projection?.Rule.ToString().ToUpperInvariant() ?? "ALL",
                RuleScope = projection is null ? "" : projection.Scope == EN_REVIEW_CORRECTION_SCOPE.HeadCell ? "HEAD_CELL" : "HEAD",
                BeamNo = referencePoint?.BeamNo ?? DefaultReviewBeamNo,
                ReferenceCellNo = projection?.Rule == EN_REVIEW_CORRECTION_RULE.Simple ? referencePoint?.CellNo : null,
                ReferenceShot = projection?.Rule == EN_REVIEW_CORRECTION_RULE.Simple ? FormatShotName(referencePoint) : "",
                RoughReference = projection?.Rule == EN_REVIEW_CORRECTION_RULE.Rough ? projection.RoughReference.ToString().ToUpperInvariant() : "",
                FineLineCount = projection?.Rule == EN_REVIEW_CORRECTION_RULE.Fine ? projection.FineLineCount : 0,
                TotalCount = plan.ReviewPoints.Count
            };
        }

        private static IReadOnlyList<ST_PRODUCT_REVIEW_RESULT> CreateProductReviewResults(IReadOnlyList<ST_REVIEW_PLAN_POINT> results)
        {
            var productResults = new List<ST_PRODUCT_REVIEW_RESULT>();
            foreach (var point in results)
            {
                if (!point.Use || point.State is not (EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng))
                {
                    continue;
                }

                productResults.Add(new ST_PRODUCT_REVIEW_RESULT($"CELL{point.CellNo}_{FormatShotName(point)}", point.HeadNo, point.CellNo, point.BeamNo, point.ErrorX, point.ErrorY, point.Judge));
            }

            return productResults;
        }

        private static bool IsPointToPoint(ST_REVIEW_PLAN plan)
        {
            return plan.InspectionMethod.Equals("P2P", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMatchingIncompleteReview(ST_REVIEW_PLAN plan, string productRecipeId, ST_PRODUCT_REVIEW_DATA review)
        {
            var checkpointRecipeId = string.IsNullOrWhiteSpace(review.RecipeId) ? productRecipeId : review.RecipeId;
            if (review.State is EN_PRODUCT_PROCESS_STATE.NotStarted or EN_PRODUCT_PROCESS_STATE.Completed || review.Results.Count == 0 || (!string.IsNullOrWhiteSpace(checkpointRecipeId) && !checkpointRecipeId.Equals(plan.RecipeId, StringComparison.OrdinalIgnoreCase)) || !review.InspectionMethod.Equals(plan.InspectionMethod, StringComparison.OrdinalIgnoreCase) || review.TotalCount != plan.ReviewPoints.Count)
            {
                return false;
            }

            var projection = plan.CorrectionPlan;
            var expectedRule = projection?.Rule.ToString().ToUpperInvariant() ?? "ALL";
            var expectedScope = projection is null ? "" : projection.Scope == EN_REVIEW_CORRECTION_SCOPE.HeadCell ? "HEAD_CELL" : "HEAD";
            if (!review.Rule.Equals(expectedRule, StringComparison.OrdinalIgnoreCase) || !review.RuleScope.Equals(expectedScope, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            ST_REVIEW_PLAN_POINT? referencePoint = null;
            foreach (var point in plan.ReviewPoints)
            {
                referencePoint = point;
                break;
            }

            var expectedBeamNo = referencePoint?.BeamNo ?? DefaultReviewBeamNo;
            if (review.BeamNo != expectedBeamNo)
            {
                return false;
            }

            if (projection?.Rule == EN_REVIEW_CORRECTION_RULE.Simple && (review.ReferenceCellNo != referencePoint?.CellNo || !review.ReferenceShot.Equals(FormatShotName(referencePoint), StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (projection?.Rule == EN_REVIEW_CORRECTION_RULE.Rough && !review.RoughReference.Equals(projection.RoughReference.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (projection?.Rule == EN_REVIEW_CORRECTION_RULE.Fine && review.FineLineCount != projection.FineLineCount)
            {
                return false;
            }

            var planKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var point in plan.ReviewPoints)
            {
                planKeys.Add(CreateCheckpointPointKey(point));
            }

            foreach (var result in review.Results)
            {
                if (!planKeys.Contains(CreateCheckpointPointKey(result)))
                {
                    return false;
                }
            }

            return true;
        }

        private static ST_REVIEW_PLAN RestoreCheckpoint(ST_REVIEW_PLAN plan, ST_PRODUCT_REVIEW_DATA checkpoint)
        {
            var measuredRows = new Dictionary<string, ST_PRODUCT_REVIEW_RESULT>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in checkpoint.Results)
            {
                measuredRows[CreateCheckpointPointKey(result)] = result;
            }

            var points = new ST_REVIEW_PLAN_POINT[plan.Points.Count];
            for (var index = 0; index < plan.Points.Count; index++)
            {
                var point = plan.Points[index];
                if (!point.Use || !measuredRows.TryGetValue(CreateCheckpointPointKey(point), out var measured))
                {
                    points[index] = point with
                    {
                        ErrorX = 0.0,
                        ErrorY = 0.0,
                        State = point.Use ? EN_REVIEW_POINT_STATE.Ready : EN_REVIEW_POINT_STATE.Skip,
                        Judge = point.Use ? "WAIT" : "SKIP"
                    };
                    continue;
                }

                var state = measured.Judge.Equals("NG", StringComparison.OrdinalIgnoreCase) ? EN_REVIEW_POINT_STATE.Ng : EN_REVIEW_POINT_STATE.Ok;
                points[index] = point with
                {
                    ErrorX = measured.ErrorX,
                    ErrorY = measured.ErrorY,
                    State = state,
                    Judge = state == EN_REVIEW_POINT_STATE.Ng ? "NG" : "OK"
                };
            }

            return plan with
            {
                Points = points
            };
        }

        private static string CreateCheckpointPointKey(ST_REVIEW_PLAN_POINT point)
        {
            return CreateCheckpointPointKey(point.HeadNo, point.CellNo, FormatShotName(point), point.ShotKey);
        }

        private static string FormatShotName(ST_REVIEW_PLAN_POINT? point)
        {
            return point is null ? "" : CReviewShotNameFormatter.ToMatrixName(point.ShotNo, point.PixelCountX);
        }

        private static string CreateCheckpointPointKey(ST_REVIEW_RESULT_FILE_ROW row)
        {
            return CreateCheckpointPointKey(row.HeadNo, row.CellNo, row.ShotName, row.ShotKey);
        }

        private static string CreateCheckpointPointKey(ST_PRODUCT_REVIEW_RESULT result)
        {
            return CreateCheckpointPointKey(result.HeadNo, result.CellNo, "", result.ShotKey);
        }

        private static string CreateCheckpointPointKey(int headNo, int cellNo, string shotName, string shotKey)
        {
            var normalizedShotName = shotName.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalizedShotName))
            {
                var separatorIndex = shotKey.LastIndexOf('_');
                if (separatorIndex >= 0 && separatorIndex + 1 < shotKey.Length)
                {
                    normalizedShotName = shotKey[(separatorIndex + 1)..].Trim().ToUpperInvariant();
                }
            }

            return $"H{headNo:00}|CELL{cellNo:000}|{normalizedShotName}";
        }

        private async Task<ST_PRODUCT_REVIEW_UPDATE_RESULT> TrySetProductReviewError(string? reviewProductId, string message)
        {
            if (string.IsNullOrWhiteSpace(reviewProductId) || productManager is null)
            {
                return ST_PRODUCT_REVIEW_UPDATE_RESULT.NotApplicable;
            }

            try
            {
                await productManager.SetReviewError(reviewProductId, message, CancellationToken.None);
                return ST_PRODUCT_REVIEW_UPDATE_RESULT.Success;
            }
            catch (Exception ex)
            {
                return LogProductReviewUpdateFailure(reviewProductId, "REVIEW ERROR UPDATE", ex);
            }
        }

        private async Task<ST_PRODUCT_REVIEW_UPDATE_RESULT> TrySetProductReviewStopped(string? reviewProductId, string message)
        {
            if (string.IsNullOrWhiteSpace(reviewProductId) || productManager is null)
            {
                return ST_PRODUCT_REVIEW_UPDATE_RESULT.NotApplicable;
            }

            try
            {
                await productManager.SetReviewStopped(reviewProductId, message, CancellationToken.None);
                return ST_PRODUCT_REVIEW_UPDATE_RESULT.Success;
            }
            catch (Exception ex)
            {
                return LogProductReviewUpdateFailure(reviewProductId, "REVIEW STOP UPDATE", ex);
            }
        }

        private async Task<ST_REVIEW_SEQUENCE_STATUS> CreateStoppedStatus(ST_REVIEW_PLAN plan, string? reviewProductId, string message)
        {
            WriteReviewLog("STOP", message);
            var updateResult = await TrySetProductReviewStopped(reviewProductId, message);
            if (updateResult.Outcome == EN_PRODUCT_REVIEW_UPDATE_OUTCOME.Failed)
            {
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Failed;
            }

            return CreateStatus(plan, SequenceState, AppendUpdateResult(message, updateResult));
        }

        private ST_PRODUCT_REVIEW_UPDATE_RESULT LogProductReviewUpdateFailure(string reviewProductId, string action, Exception exception)
        {
            var productWasReplaced = productManager?.Current is not { } current || !string.Equals(current.GlassId, reviewProductId, StringComparison.Ordinal);
            var outcome = productWasReplaced ? EN_PRODUCT_REVIEW_UPDATE_OUTCOME.ProductReplaced : EN_PRODUCT_REVIEW_UPDATE_OUTCOME.Failed;
            var detail = productWasReplaced ? $"Product changed before Review state could be updated. Error={exception.Message}" : $"Failed to persist Product Review state. Error={exception}";
            try
            {
                if (logManager is null)
                {
                    System.Diagnostics.Trace.TraceError(detail);
                }
                else
                {
                    logManager.WriteProductEvent(reviewProductId, action, "REVIEW", outcome == EN_PRODUCT_REVIEW_UPDATE_OUTCOME.Failed ? "FAILED" : "SKIPPED", detail);
                }
            }
            catch (Exception logException)
            {
                System.Diagnostics.Trace.TraceError($"{detail} Product event logging also failed: {logException}");
            }

            return new ST_PRODUCT_REVIEW_UPDATE_RESULT(outcome, detail);
        }

        private static string AppendUpdateResult(string message, ST_PRODUCT_REVIEW_UPDATE_RESULT updateResult)
        {
            return updateResult.Outcome switch
            {
                EN_PRODUCT_REVIEW_UPDATE_OUTCOME.ProductReplaced => $"{message} Product changed; Product Review state update was skipped.",
                EN_PRODUCT_REVIEW_UPDATE_OUTCOME.Failed => $"{message} Product Review state update failed: {updateResult.Detail}",
                _ => message
            };
        }

        private Task MoveStageY(ST_REVIEW_PLAN_POINT point, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStopRequested)
                throw new OperationCanceledException("Review Stop");
            (_reviewStageMotion ?? throw new InvalidOperationException("Review Stage 이동이 준비되지 않았습니다.")).MoveStageY(point.ReviewTargetY);
            return Task.CompletedTask;
        }

        private async Task MoveVisionX(ST_REVIEW_PLAN_POINT point, bool waitForMoveCompletion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStopRequested)
                throw new OperationCanceledException("Review Stop");
            var motion = _reviewStageMotion ?? throw new InvalidOperationException("Review Stage 이동이 준비되지 않았습니다.");
            motion.MoveReviewX(point.ReviewTargetX, cancellationToken);
            WriteReviewLog("MOVE", $"Stage move. X={FormatDouble(point.ReviewTargetX)}, Y={FormatDouble(point.ReviewTargetY)}");
            if (waitForMoveCompletion)
                await motion.WaitForArrival(point, cancellationToken);
        }

        private async Task<ST_REVIEW_MEASURE_RESULT> MeasureVision(ST_REVIEW_PLAN_POINT point, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isSimulation = IsReviewSimulation();
            WriteReviewLog("MEASURE_START", "Measurement started.");
            try
            {
                ST_REVIEW_MEASURE_RESULT measurement;
                if (isSimulation)
                {
                    measurement = CreateTemporaryVisionMeasurement(point);
                }
                else
                {
                    var command = CreateMarkFindSingleCommand(point);
                    var response = await interfaceManager.ExecuteFunction(EN_EQP_MODULE.Vision, 0, command, cancellationToken);
                    if (string.IsNullOrWhiteSpace(response))
                    {
                        throw new InvalidDataException($"Vision Mark Find Single response is empty. Shot={point.ShotKey}");
                    }

                    measurement = ParseVisionResponse(response, point);
                }

                WriteReviewLog("RESULT_RECEIVED", "Inspection result received.");
                return measurement;
            }
            catch (Exception exception)when (exception is not OperationCanceledException)
            {
                WriteReviewLog("MEASURE_ERROR", "Measurement failed.");
                throw;
            }
        }

        private async Task<ST_REVIEW_SEQUENCE_STATUS> RunIofSequence(
            ST_REVIEW_PLAN workingPlan, 
            string? reviewProductId, 
            Action<ST_REVIEW_PLAN>? progress, 
            CancellationToken cancellationToken, 
            IReadOnlyList<ST_REVIEW_PLAN_POINT>? runPoints = null, 
            bool resumeFromCurrentPosition = false
        )
        {
            var pendingPoints = runPoints ?? workingPlan.ReviewPoints.Where(point => point.State is not (EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng)).ToArray();
            var lines = CreateIofLines(pendingPoints);
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsStopRequested)
                {
                    return await StopIofSequence(workingPlan, reviewProductId, progress, "Review IOF sequence stopped.");
                }

                // A resumed line starts at the endpoint closest to the camera's current position.
                // Subsequent lines also follow the current endpoint, including after a reversed retry.
                var line = resumeFromCurrentPosition || runPoints is not null || lineIndex > 0 ? OrderLineFromCurrentPosition(lines[lineIndex], _reviewStageMotion!.ReviewXPosition) : lines[lineIndex];
                workingPlan = SetIofLineCurrent(workingPlan, line);
                SetActivePoints(line[0], LastMeasuredPoint);
                SetCurrentPlan(workingPlan, progress);
                WriteReviewLog("MEASURE_START", "Measurement started.");
                IReadOnlyList<ST_REVIEW_MEASURE_RESULT> measurements;
                try
                {
                    measurements = await MeasureVisionLine(line, cancellationToken);
                    if (IsStopRequested)
                    {
                        WriteReviewLog("MEASURE_STOPPED", "Measurement stopped.");
                    }
                    else
                    {
                        WriteReviewLog("RESULT_RECEIVED", "Inspection result received.");
                    }
                }
                catch (Exception exception)when (exception is not OperationCanceledException)
                {
                    WriteReviewLog("MEASURE_ERROR", "Measurement failed.");
                    throw;
                }

                if (IsStopRequested)
                {
                    return await StopIofSequence(workingPlan, reviewProductId, progress, "Review IOF sequence stopped.");
                }

                var measuredLine = new ST_REVIEW_PLAN_POINT[line.Count];
                for (var index = 0; index < line.Count; index++)
                {
                    var currentPoint = GetPlanPoint(workingPlan, line[index].ShotKey);
                    var measuredPoint = ApplyMeasurement(workingPlan, currentPoint, measurements[index]);
                    measuredLine[index] = measuredPoint;
                }

                workingPlan = UpdatePoints(workingPlan, measuredLine);
                SetActivePoints(null, measuredLine[^1]);
                SetCurrentPlan(workingPlan, progress);
                await SaveProgressToProduct(workingPlan, reviewProductId, cancellationToken);
            }

            SequenceState = EN_REVIEW_SEQUENCE_STATE.Completed;
            SetCurrentPlan(workingPlan, progress);
            await SaveResultForProduct(workingPlan, workingPlan.ReviewPoints, reviewProductId, cancellationToken);
            WriteReviewLog(runPoints is null ? "COMPLETE" : "RETRY_COMPLETE", runPoints is null ? "Review completed." : "Review retry completed.");
            return CreateStatus(workingPlan, SequenceState, "Review IOF sequence completed.");
        }

        private async Task<ST_REVIEW_SEQUENCE_STATUS> StopIofSequence(ST_REVIEW_PLAN workingPlan, string? reviewProductId, Action<ST_REVIEW_PLAN>? progress, string message)
        {
            SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
            workingPlan = SetWaitingPointsReady(workingPlan);
            SetActivePoints(null, LastMeasuredPoint);
            SetCurrentPlan(workingPlan, progress);
            return await CreateStoppedStatus(workingPlan, reviewProductId, message);
        }

        private async Task<IReadOnlyList<ST_REVIEW_MEASURE_RESULT>> MeasureVisionLine(IReadOnlyList<ST_REVIEW_PLAN_POINT> line, CancellationToken cancellationToken)
        {
            if (line.Count == 0)
            {
                throw new InvalidOperationException("Vision IOF line is empty.");
            }

            await MoveIofLineStart(line[0], cancellationToken);
            await (_reviewStageMotion ?? throw new InvalidOperationException("Review Stage 이동이 준비되지 않았습니다.")).PrepareIof(line, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStopRequested)
            {
                return[];
            }

            if (IsReviewSimulation())
            {
                await MoveIofLineEnd(line[^1], cancellationToken);
                if (IsStopRequested)
                {
                    return[];
                }

                var temporaryResults = new ST_REVIEW_MEASURE_RESULT[line.Count];
                for (var index = 0; index < line.Count; index++)
                {
                    temporaryResults[index] = CreateTemporaryVisionMeasurement(line[index]);
                }

                return temporaryResults;
            }

            await ExecuteVisionCommand(CreateFlyingReadyCommand(line), "Vision Flying Ready response is empty.", cancellationToken);
            if (IsStopRequested)
                return[];
            await MoveIofLineEnd(line[^1], cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStopRequested)
            {
                return[];
            }

            await ExecuteVisionCommand("FLYING_MOVE_DONE", "Vision Flying Move Done response is empty.", cancellationToken);
            if (IsStopRequested)
                return[];
            var response = await ExecuteVisionCommand("MARK_FIND_LINE", "Vision Mark Find Line response is empty.", cancellationToken);
            return ParseVisionLineResponse(response, line);
        }

        private async Task<string> ExecuteVisionCommand(string command, string emptyResponseMessage, CancellationToken cancellationToken)
        {
            var response = await interfaceManager.ExecuteFunction(EN_EQP_MODULE.Vision, 0, command, cancellationToken);
            if (string.IsNullOrWhiteSpace(response))
            {
                throw new InvalidDataException(emptyResponseMessage);
            }

            if (response.StartsWith("NCK:1002:", StringComparison.OrdinalIgnoreCase))
                throw new CFlyingCountMismatchException(response);
            return response;
        }

        private async Task MoveIofLineStart(ST_REVIEW_PLAN_POINT point, CancellationToken cancellationToken)
        {
            // Complete the shared Process Move transaction before sending Flying Ready.
            await (_reviewStageMotion ?? throw new InvalidOperationException("Review Stage 이동이 준비되지 않았습니다.")).MoveAndWait(point, cancellationToken);
        }

        private async Task MoveIofLineEnd(ST_REVIEW_PLAN_POINT point, CancellationToken cancellationToken)
        {
            // The common end move waits for Stage completion, position and Busy feedback.
            await (_reviewStageMotion ?? throw new InvalidOperationException("Review Stage 이동이 준비되지 않았습니다.")).MoveAndWait(point, cancellationToken, reviewXOnly: true);
        }

        private string CreateFlyingReadyCommand(IReadOnlyList<ST_REVIEW_PLAN_POINT> line, bool zeroLine = false)
        {
            var maximum = settingManager.GetVisionMaxHoleCountPerLine();
            if (line.Count > maximum)
            {
                throw new InvalidOperationException($"Vision IOF 라인 Shot 수({line.Count})가 MaxHoleCountPerLine({maximum})을 초과합니다.");
            }

            var builder = new StringBuilder();
            builder.Append("FLYING_READY:").Append(zeroLine ? 1 : IofInspectionType).Append(':');
            for (var index = 0; index < line.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(';');
                }

                AppendVisionMarkSearch(builder, line[index]);
            }

            return builder.ToString();
        }

        private static void AppendVisionMarkSearch(StringBuilder builder, ST_REVIEW_PLAN_POINT point)
        {
            var columnCount = Math.Max(1, point.PixelCountX);
            var zeroBasedShotNo = Math.Max(0, point.ShotNo - 1);
            var indexX = (zeroBasedShotNo % columnCount) + 1;
            var indexY = (zeroBasedShotNo / columnCount) + 1;
            builder.Append(point.CellNo).Append(',').Append(point.BeamNo).Append(',').Append(indexY).Append(',').Append(indexX).Append(',').Append(indexY).Append(',').Append(point.HeadNo);
        }

        private IReadOnlyList<ST_REVIEW_MEASURE_RESULT> ParseVisionLineResponse(string response, IReadOnlyList<ST_REVIEW_PLAN_POINT> line)
        {
            const string marksToken = ":MARKS=";
            var marksIndex = response.IndexOf(marksToken, StringComparison.OrdinalIgnoreCase);
            if (marksIndex < 0)
            {
                throw new InvalidDataException($"Vision Mark Find Line response has no result array. Response={response}");
            }

            var headerValues = SplitResponse(response[..marksIndex]);
            if (!TryReadInt(headerValues, "RESOLUTION", out var resolution) || resolution <= 0 || !TryReadInt(headerValues, "COUNT", out var resultCount))
            {
                throw new InvalidDataException($"Vision Mark Find Line header is invalid. Response={response}");
            }

            if (resultCount != line.Count)
            {
                // The transport already received the full Setting-sized frame and removed
                // padding. COUNT here is nFindHoleCount, never the physical slot capacity.
                throw new InvalidDataException($"Vision Mark Find Line count mismatch. Requested={line.Count}, Received={resultCount}. Requested는 이번 라인 검사점 수, Received는 응답 nFindHoleCount(유효 결과 수)이며 고정 수신 버퍼 용량이 아닙니다.");
            }

            var marksText = response[(marksIndex + marksToken.Length)..];
            var markTexts = marksText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (markTexts.Length != resultCount)
            {
                throw new InvalidDataException($"Vision Mark Find Line result array mismatch. Header={resultCount}, Body={markTexts.Length}.");
            }

            var measurements = new ST_REVIEW_MEASURE_RESULT[resultCount];
            var cameraResolution = settingManager.GetVisionCameraResolution();
            for (var index = 0; index < resultCount; index++)
            {
                var fields = markTexts[index].Split(',', StringSplitOptions.TrimEntries);
                if (fields.Length != 5 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var targetX) || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var targetY) || !double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var shotX) || !double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var shotY))
                {
                    throw new InvalidDataException($"Vision Mark Find Line result is invalid. Index={index}, Value={markTexts[index]}");
                }

                var point = line[index];
                measurements[index] = CreateVisionMeasurement(point, new(result, targetX, targetY, shotX, shotY), resolution, markTexts[index], cameraResolution: cameraResolution);
            }

            return measurements;
        }

        private static IReadOnlyList<IReadOnlyList<ST_REVIEW_PLAN_POINT>> CreateIofLines(IReadOnlyList<ST_REVIEW_PLAN_POINT> points)
        {
            var orderedPoints = OrderByReviewSequence(points);
            var lines = new List<IReadOnlyList<ST_REVIEW_PLAN_POINT>>();
            List<ST_REVIEW_PLAN_POINT>? currentLine = null;
            double currentDesignY = 0.0;
            foreach (var point in orderedPoints)
            {
                if (currentLine is null || Math.Abs(point.DesignY - currentDesignY) > ReviewSequenceRowTolerance)
                {
                    currentLine = [];
                    lines.Add(currentLine);
                    currentDesignY = point.DesignY;
                }

                currentLine.Add(point);
            }

            return lines;
        }

        private static ST_REVIEW_PLAN SetIofLineCurrent(ST_REVIEW_PLAN plan, IReadOnlyList<ST_REVIEW_PLAN_POINT> line)
        {
            return UpdatePoints(plan, line.Select(point => GetPlanPoint(plan, point.ShotKey)with { State = EN_REVIEW_POINT_STATE.Current, Judge = "WAIT" }).ToArray());
        }

        private static ST_REVIEW_PLAN_POINT GetPlanPoint(ST_REVIEW_PLAN plan, string shotKey)
        {
            if (plan.Points is CReviewRuntimePoints runtime)
                return runtime.Find(shotKey) ?? throw new InvalidOperationException($"Review point was not found. Shot={shotKey}");
            foreach (var point in plan.Points)
            {
                if (point.ShotKey.Equals(shotKey, StringComparison.OrdinalIgnoreCase))
                {
                    return point;
                }
            }

            throw new InvalidOperationException($"Review point was not found. Shot={shotKey}");
        }

        private static string CreateMarkFindSingleCommand(ST_REVIEW_PLAN_POINT point)
        {
            var columnCount = Math.Max(1, point.PixelCountX);
            var zeroBasedShotNo = Math.Max(0, point.ShotNo - 1);
            var indexX = (zeroBasedShotNo % columnCount) + 1;
            var indexY = (zeroBasedShotNo / columnCount) + 1;
            var lineNo = indexY;
            return string.Create(CultureInfo.InvariantCulture, $"MARK_FIND_SINGLE:{point.CellNo},{point.BeamNo},{lineNo},{indexX},{indexY},{point.HeadNo}");
        }

        private ST_REVIEW_MEASURE_RESULT CreateTemporaryVisionMeasurement(ST_REVIEW_PLAN_POINT point)
        {
            const double normalErrorLimit = 0.0200;
            const double ngErrorMinimum = 0.0350;
            const double ngErrorRange = 0.0200;
            var createNgSample = Random.Shared.Next(10) == 0;
            var errorX = CreateRandomSignedValue(normalErrorLimit);
            var errorY = CreateRandomSignedValue(normalErrorLimit);
            if (createNgSample)
            {
                if (Random.Shared.Next(2) == 0)
                {
                    errorX = CreateRandomSignedValue(ngErrorMinimum, ngErrorRange);
                }
                else
                {
                    errorY = CreateRandomSignedValue(ngErrorMinimum, ngErrorRange);
                }
            }

            errorX = Math.Round(errorX, 4, MidpointRounding.AwayFromZero);
            errorY = Math.Round(errorY, 4, MidpointRounding.AwayFromZero);
            var response = FormatCommand("TEMPORARY_RANDOM_VISION_RESULT", ("X", FormatDouble(point.ReviewTargetX + errorX)), ("Y", FormatDouble(point.ReviewTargetY + errorY)));
            // Preserve the existing random mm error distribution; only encode its
            // raw Target/Hole pair in the newly confirmed pixel units. No error
            // reduction or calibration feedback is fabricated in simulation.
            var cameraResolution = settingManager.GetVisionCameraResolution();
            var mark = new ST_VISION_MARK_FIND(0, point.ReviewTargetX / cameraResolution, point.ReviewTargetY / cameraResolution,
                (point.ReviewTargetX - errorX) / cameraResolution, (point.ReviewTargetY - errorY) / cameraResolution);
            return CreateVisionMeasurement(point, mark, 1, response, cameraResolution: cameraResolution);
        }

        private static double CreateRandomSignedValue(double maximum)
        {
            return ((Random.Shared.NextDouble() * 2.0) - 1.0) * maximum;
        }

        private static double CreateRandomSignedValue(double minimum, double range)
        {
            var sign = Random.Shared.Next(2) == 0 ? -1.0 : 1.0;
            return sign * (minimum + (Random.Shared.NextDouble() * range));
        }

        private bool IsReviewSimulation()
        {
            return IsReviewDeviceSimulation(EN_EQP_MODULE.Vision, 0);
        }

        private static ST_REVIEW_PLAN_POINT ApplyMeasurement(ST_REVIEW_PLAN plan, ST_REVIEW_PLAN_POINT point, ST_REVIEW_MEASURE_RESULT measurement)
        {
            var error = measurement.ErrorMillimeters ?? new ST_REVIEW_COORDINATE_OFFSET(measurement.X - point.ReviewTargetX, measurement.Y - point.ReviewTargetY);
            return ApplyMeasurementError(plan, point, error.X, error.Y, measurement.Judge);
        }

        private static ST_REVIEW_PLAN_POINT ApplyMeasurementError(ST_REVIEW_PLAN plan, ST_REVIEW_PLAN_POINT point, double errorX, double errorY, string measurementJudge)
        {
            var converted = VisionErrorToStageOffset(errorX, errorY, plan.VisionFlipType);
            var findOk = string.IsNullOrWhiteSpace(measurementJudge) || measurementJudge.Trim().Equals("WAIT", StringComparison.OrdinalIgnoreCase) || measurementJudge.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase);
            var judge = findOk && Math.Abs(converted.X) <= plan.ToleranceX && Math.Abs(converted.Y) <= plan.ToleranceY ? "OK" : "NG";
            var state = judge.Equals("OK", StringComparison.OrdinalIgnoreCase) ? EN_REVIEW_POINT_STATE.Ok : EN_REVIEW_POINT_STATE.Ng;
            return point with
            {
                ErrorX = errorX,
                ErrorY = errorY,
                State = state,
                Judge = judge
            };
        }

        private ST_REVIEW_MEASURE_RESULT ParseVisionResponse(string response, ST_REVIEW_PLAN_POINT point)
        {
            var values = SplitResponse(response);
            if (!TryReadInt(values, "RESOLUTION", out var resolution) || !TryReadInt(values, "RESULT", out var result) || !TryReadDouble(values, "TARGET_X", out var targetX) || !TryReadDouble(values, "TARGET_Y", out var targetY) || !TryReadDouble(values, "HOLE_X", out var holeX) || !TryReadDouble(values, "HOLE_Y", out var holeY))
            {
                throw new InvalidDataException($"Vision Mark Find Single response is invalid. Shot={point.ShotKey}, Response={response}");
            }

            return CreateVisionMeasurement(point, new(result, targetX, targetY, holeX, holeY), resolution, response);
        }

        private ST_REVIEW_MEASURE_RESULT CreateVisionMeasurement(ST_REVIEW_PLAN_POINT point, ST_VISION_MARK_FIND mark, int resolution, string response, string? judge = null, double? cameraResolution = null)
        {
            var camera = cameraResolution ?? settingManager.GetVisionCameraResolution();
            var error = VisionPixelsToMillimeters(mark, resolution, camera);
            // 1000 / 1003 / 1010: nResult 0 means found. Tolerance is checked on application.
            return new ST_REVIEW_MEASURE_RESULT(point.ReviewTargetX + error.X, point.ReviewTargetY + error.Y, mark.Result == 0 ? judge ?? "OK" : "NG", response)
            {
                MarkFind = mark,
                Resolution = resolution,
                CameraResolution = camera,
                ErrorMillimeters = error
            };
        }

        /// <summary>Camera pixels to mm, before Vision Flip and independent of Head direction.</summary>
        public static ST_REVIEW_COORDINATE_OFFSET VisionPixelsToMillimeters(ST_VISION_MARK_FIND mark, int resolution, double cameraResolution)
        {
            if (resolution <= 0 || !double.IsFinite(cameraResolution) || cameraResolution <= 0)
                throw new InvalidDataException("Vision nResolution 및 Camera Resolution(mm/pixel)은 0보다 커야 합니다.");
            var factor = cameraResolution * resolution;
            var x = factor * (mark.TargetMarkX - mark.HoleX);
            var y = factor * (mark.TargetMarkY - mark.HoleY);
            if (!double.IsFinite(mark.TargetMarkX) || !double.IsFinite(mark.TargetMarkY) || !double.IsFinite(mark.HoleX) || !double.IsFinite(mark.HoleY) || !double.IsFinite(factor) || !double.IsFinite(x) || !double.IsFinite(y))
                throw new InvalidDataException("Vision Pixel 좌표 또는 mm 환산값이 유효하지 않습니다.");
            return new(x, y);
        }

        private static IReadOnlyDictionary<string, string> SplitResponse(string response)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tokens = response.Split([':', ';', ',', '|', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var token in tokens)
            {
                var separatorIndex = token.IndexOf('=');
                if (separatorIndex <= 0 || separatorIndex == token.Length - 1)
                {
                    continue;
                }

                values[token[..separatorIndex].Trim()] = token[(separatorIndex + 1)..].Trim();
            }

            return values;
        }

        private static bool TryReadDouble(IReadOnlyDictionary<string, string> values, string key, out double value)
        {
            value = 0.0;
            return values.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadInt(IReadOnlyDictionary<string, string> values, string key, out int value)
        {
            value = 0;
            return values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static string FormatCommand(string command, params (string Key, string Value)[] arguments)
        {
            return string.Join(";", new[] { command }.Concat(arguments.Select(argument => $"{argument.Key}={argument.Value}")));
        }

        private async Task<ST_REVIEW_SEQUENCE_STATUS> RunRetryPoints(IReadOnlyList<ST_REVIEW_PLAN_POINT> retryPoints, string completedMessage, Action<ST_REVIEW_PLAN>? progress, CancellationToken cancellationToken)
        {
            if (!await _sequenceLock.WaitAsync(0, cancellationToken))
            {
                return CreateStatus(CurrentPlan!, EN_REVIEW_SEQUENCE_STATE.Running, "Review sequence is already running.");
            }

            var reviewProductId = GetReviewProductId();
            try
            {
                var workingPlan = PrepareRuntimePlan(CurrentPlan!);
                Interlocked.Exchange(ref _stopRequested, 0);
                ValidateReviewMoveMethod(workingPlan);
                _reviewStageMotion = OpenReviewStageMotion(workingPlan.StageSpeed);
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Running;
                SetCurrentPlan(workingPlan, progress);
                WriteReviewLog("RETRY_START", "Review retry started.");
                if (!string.IsNullOrWhiteSpace(reviewProductId) && productManager is not null)
                {
                    await productManager.SetReviewRunning(reviewProductId, CreateProductReviewData(workingPlan, DateTimeOffset.Now), cancellationToken);
                }

                if (!IsPointToPoint(workingPlan))
                {
                    return await RunIofSequence(workingPlan, reviewProductId, progress, cancellationToken, retryPoints);
                }

                foreach (var point in OrderPtoPFromCurrentPosition(retryPoints, resume: true))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsStopRequested)
                    {
                        SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                        return await CreateStoppedStatus(workingPlan, reviewProductId, "Review retry stopped.");
                    }

                    var currentPoint = point with
                    {
                        State = EN_REVIEW_POINT_STATE.Current,
                        Judge = "WAIT"
                    };
                    SetActivePoints(currentPoint, LastMeasuredPoint);
                    workingPlan = UpdatePoint(workingPlan, currentPoint);
                    SetCurrentPlan(workingPlan, progress);
                    await MoveStageY(currentPoint, cancellationToken);
                    if (IsStopRequested)
                    {
                        SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                        workingPlan = UpdatePoint(workingPlan, currentPoint with { State = EN_REVIEW_POINT_STATE.Ready });
                        SetCurrentPlan(workingPlan, progress);
                        return await CreateStoppedStatus(workingPlan, reviewProductId, "Review retry stopped.");
                    }

                    await MoveVisionX(currentPoint, waitForMoveCompletion: true, cancellationToken);
                    if (IsStopRequested)
                    {
                        SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                        workingPlan = UpdatePoint(workingPlan, currentPoint with { State = EN_REVIEW_POINT_STATE.Ready });
                        SetCurrentPlan(workingPlan, progress);
                        return await CreateStoppedStatus(workingPlan, reviewProductId, "Review retry stopped.");
                    }

                    var measurement = await MeasureVision(currentPoint, cancellationToken);
                    if (IsStopRequested)
                    {
                        SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                        workingPlan = UpdatePoint(workingPlan, currentPoint with { State = EN_REVIEW_POINT_STATE.Ready });
                        SetCurrentPlan(workingPlan, progress);
                        return await CreateStoppedStatus(workingPlan, reviewProductId, "Review retry stopped.");
                    }

                    var measuredPoint = ApplyMeasurement(workingPlan, currentPoint, measurement);
                    SetActivePoints(null, measuredPoint);
                    workingPlan = UpdatePoint(workingPlan, measuredPoint);
                    SetCurrentPlan(workingPlan, progress);
                    await SaveProgressToProduct(workingPlan, reviewProductId, cancellationToken);
                }

                SequenceState = EN_REVIEW_SEQUENCE_STATE.Completed;
                await SaveResultForProduct(workingPlan, workingPlan.ReviewPoints, reviewProductId, cancellationToken);
                WriteReviewLog("RETRY_COMPLETE", "Review retry completed.");
                return CreateStatus(workingPlan, SequenceState, completedMessage);
            }
            catch (OperationCanceledException)
            {
                _reviewStageMotion?.Stop();
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                var stoppedPlan = SetWaitingPointsReady(CurrentPlan!);
                SetCurrentPlan(stoppedPlan, progress);
                return await CreateStoppedStatus(stoppedPlan, reviewProductId, "Review retry canceled.");
            }
            catch (Exception ex)
            {
                _reviewStageMotion?.Stop();
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Failed;
                var failedPlan = SetWaitingPointsReady(CurrentPlan!);
                SetCurrentPlan(failedPlan, progress);
                WriteReviewError();
                var updateResult = await TrySetProductReviewError(reviewProductId, ex.Message);
                return CreateStatus(failedPlan, SequenceState, AppendUpdateResult(ex.Message, updateResult));
            }
            finally
            {
                RememberInterruptedReview(reviewProductId);
                _reviewStageMotion?.Dispose();
                _reviewStageMotion = null;
                _sequenceLock.Release();
            }
        }

        private int GetReviewStageDeviceNo() => interfaceManager.Melsec.Map.FirstOrDefault(row => row.Id.Equals("MOTOR_STAGE_Y_CUR_POS", StringComparison.OrdinalIgnoreCase))?.DeviceNo ?? interfaceManager.GetInterfaceList(EN_EQP_MODULE.Melsec).FirstOrDefault()?.Number ?? 0;
        private bool IsReviewDeviceSimulation(EN_EQP_MODULE module, int number)
        {
            // Use this device's effective mode even before it connects. Other devices must not change it.
            var device = interfaceManager.GetInterfaceCommunicationList(module).FirstOrDefault(row => row.Number == number);
            return device?.IsSimulation ?? (interfaceManager.Devices.Count == 0 && interfaceManager.IsSimulation);
        }

        private void ValidateReviewMoveMethod(ST_REVIEW_PLAN plan)
        {
            if (!IsPointToPoint(plan))
            {
                var count = CreateIofLines(plan.ReviewPoints.ToArray()).Select(line => line.Count).DefaultIfEmpty().Max();
                if (count > settingManager.GetVisionMaxHoleCountPerLine())
                    throw new InvalidOperationException($"Vision IOF 라인 Shot 수({count})가 Setting-Vision MaxHoleCountPerLine을 초과합니다.");
                interfaceManager.StageReviewSequences.ValidateIofReadiness(GetReviewStageDeviceNo(), count);
            }
        }

        private CReviewStageMotion OpenReviewStageMotion(double? stageSpeed = null)
        {
            var number = GetReviewStageDeviceNo();
            var simulation = IsReviewDeviceSimulation(EN_EQP_MODULE.Melsec, number);
            // A configured recipe/request speed must reach the PLC unchanged in
            // both modes. Only old simulation-only API callers without any recipe
            // speed retain the compatibility default; never replace an entered 100.
            var moveSpeed = stageSpeed ?? (simulation ? 1000 : double.NaN);
            if (!double.IsFinite(moveSpeed) || moveSpeed <= 0)
                throw new InvalidOperationException("Recipe STAGE_SPEED는 0보다 큰 유한한 숫자여야 합니다.");
            if (!simulation)
            {
                interfaceManager.StageReviewSequences.ValidateMoveReadiness(number);
            }
            var parameters = ST_REVIEW_MOTION_PARAMETERS.Read(settingManager);
            return new(interfaceManager.StageReviewSequences.Open(number, () => IsStopRequested), parameters, moveSpeed);
        }

        private sealed class CReviewStageMotion : IDisposable
        {
            private readonly CStageReviewSequences.Session _session;
            private readonly ST_REVIEW_MOTION_PARAMETERS _motion;
            private readonly double _stageSpeed;
            private double? _pendingY;
            private Task _pending = Task.CompletedTask;
            public CReviewStageMotion(CStageReviewSequences.Session session, ST_REVIEW_MOTION_PARAMETERS motion, double stageSpeed)
            {
                _session = session;
                _motion = motion;
                _stageSpeed = stageSpeed;
            }

            public void MoveStageY(double position) => _pendingY = position;
            public void MoveReviewX(double position, CancellationToken token)
            {
                var y = _pendingY ?? _session.CurrentY;
                _pendingY = null;
                _pending = _session.Move(new(position, y, _motion.Speed, _stageSpeed, _motion.Acceleration), token);
            }

            public double ReviewXPosition => _session.CurrentX;

            public Task MoveAndWait(ST_REVIEW_PLAN_POINT point, CancellationToken token, bool reviewXOnly = false) => _session.Move(new(point.ReviewTargetX, point.ReviewTargetY, _motion.Speed, _stageSpeed, _motion.Acceleration, reviewXOnly ? EN_STAGE_MOVE_MODE.ReviewX : EN_STAGE_MOVE_MODE.XY), token);
            public Task VerifyArrival(ST_REVIEW_PLAN_POINT point, CancellationToken token) => _session.VerifyArrival(new(point.ReviewTargetX, point.ReviewTargetY, _motion.Speed, _stageSpeed, _motion.Acceleration), token);
            public Task WaitForArrival(ST_REVIEW_PLAN_POINT point, CancellationToken token) => _pending.WaitAsync(token);
            public Task<ST_STAGE_IOF_PLAN> PrepareIof(IReadOnlyList<ST_REVIEW_PLAN_POINT> points, CancellationToken token) => _session.PrepareIof(points.Select(point => new ST_STAGE_IOF_POINT(point.ReviewTargetX, point.ReviewTargetY)).ToArray(), false, token);
            public void Stop() => _session.Stop();
            public void Dispose() => _session.Dispose();
        }

        private void RememberInterruptedReview(string? productId)
        {
            lock (_stateLock)
            {
                _interruptedReviewPlan = SequenceState is EN_REVIEW_SEQUENCE_STATE.Stopped or EN_REVIEW_SEQUENCE_STATE.Failed ? _currentPlan : null;
                _interruptedReviewProductId = productId;
            }
        }

        private ST_PRODUCT_REVIEW_DATA? FindInterruptedReviewCheckpoint(ST_REVIEW_PLAN plan, string? productId)
        {
            ST_REVIEW_PLAN? previous;
            lock (_stateLock)
            {
                if (!string.Equals(_interruptedReviewProductId ?? "", productId ?? "", StringComparison.OrdinalIgnoreCase))
                    return null;
                previous = _interruptedReviewPlan;
            }

            // Include a stop before the first result. A different recipe, Beam, target or method must never resume old results.
            if (previous is null || !previous.RecipeId.Equals(plan.RecipeId, StringComparison.OrdinalIgnoreCase) || !previous.InspectionMethod.Equals(plan.InspectionMethod, StringComparison.OrdinalIgnoreCase) || previous.VisionFlipType != plan.VisionFlipType || previous.ToleranceX != plan.ToleranceX || previous.ToleranceY != plan.ToleranceY || previous.Points.Count != plan.Points.Count || !previous.ReviewPoints.Any(point => point.State is EN_REVIEW_POINT_STATE.Ready or EN_REVIEW_POINT_STATE.Current))
                return null;
            var points = previous.Points.ToDictionary(point => point.ShotKey, StringComparer.OrdinalIgnoreCase);
            foreach (var point in plan.Points)
                if (!points.TryGetValue(point.ShotKey, out var old) || old.Use != point.Use || old.HeadNo != point.HeadNo || old.BeamNo != point.BeamNo || old.DesignX != point.DesignX || old.DesignY != point.DesignY || old.ReviewTargetX != point.ReviewTargetX || old.ReviewTargetY != point.ReviewTargetY)
                    return null;
            return CreateProductReviewData(previous, DateTimeOffset.Now)with
            {
                State = EN_PRODUCT_PROCESS_STATE.Stopped
            };
        }

        private static IReadOnlyList<ST_REVIEW_PLAN_POINT> OrderLineFromCurrentPosition(IReadOnlyList<ST_REVIEW_PLAN_POINT> line, double currentReviewX)
        {
            var sorted = line.OrderBy(point => point.ReviewTargetX).ThenBy(point => point.CellNo).ThenBy(point => point.ShotNo).ToArray();
            return Math.Abs(currentReviewX - sorted[0].ReviewTargetX) <= Math.Abs(currentReviewX - sorted[^1].ReviewTargetX) ? sorted : sorted.Reverse().ToArray();
        }

        private IEnumerable<ST_REVIEW_PLAN_POINT> OrderPtoPFromCurrentPosition(IEnumerable<ST_REVIEW_PLAN_POINT> points, bool resume)
        {
            var rows = CreateIofLines(points.ToArray());
            for (var index = 0; index < rows.Count; index++)
            {
                // This iterator advances only after the previous row physically finishes.
                // A partial row may contain one remaining Shot; its old parity cannot determine the next direction.
                var row = resume || index > 0 ? OrderLineFromCurrentPosition(rows[index], _reviewStageMotion!.ReviewXPosition) : rows[index];
                foreach (var point in row)
                    yield return point;
            }
        }

#endregion
#region FineCorrection
        internal void RecordAutoFineLog(string action, string message) => WriteReviewLog(action, message);
        public void ValidateFinePreparation(ST_FINE_REVIEW_REQUEST request)
        {
            ValidateFineRequest(request);
            if (request.Method == EN_FINE_INSPECTION_METHOD.Flying)
            {
                var maximum = settingManager.GetVisionMaxHoleCountPerLine();
                var excessive = request.Lines.FirstOrDefault(line => line.Points.Count > maximum);
                if (excessive is not null)
                    throw new InvalidOperationException($"Line #{excessive.Number} Shot 수({excessive.Points.Count})가 MaxHoleCountPerLine({maximum})을 초과합니다.");
            }
            if (!IsReviewDeviceSimulation(EN_EQP_MODULE.Melsec, GetReviewStageDeviceNo()))
            {
                if (request.Method != EN_FINE_INSPECTION_METHOD.PtoP)
                    interfaceManager.StageReviewSequences.ValidateIofReadiness(GetReviewStageDeviceNo(), request.Lines.Select(line => line.Points.Count).DefaultIfEmpty().Max());
                interfaceManager.StageReviewSequences.ValidateMoveReadiness(GetReviewStageDeviceNo());
                _ = ReadFineStageSpeed(request);
                if (!IsReviewSimulation() && !interfaceManager.IsConnect(EN_EQP_MODULE.Vision, 0))
                    throw new InvalidOperationException("Review의 Vision 장치가 연결되어 있지 않습니다.");
            }
            _ = ST_REVIEW_MOTION_PARAMETERS.Read(settingManager);
        }

        private static double ReadFineStageSpeed(ST_FINE_REVIEW_REQUEST request) => request.Context?.Parameters.TryGetValue("STAGE_SPEED", out var text) == true
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && double.IsFinite(speed) && speed > 0
            ? speed : throw new InvalidOperationException("PtoP의 Recipe STAGE_SPEED는 0보다 큰 유한한 숫자여야 합니다.");

        public ST_AUTO_FINE_PLAN CreateAutoFinePlan(ST_AUTO_REVIEW_INPUT input, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var settings = settingManager.LoadSection(Drilling.Common.Managers.EN_SETTING_TAB.Option, token).Concat(settingManager.LoadSection(Drilling.Common.Managers.EN_SETTING_TAB.Motor, token)).ToArray();
            var plan = CAutoFineReviewCoordinator.BuildPlan(input, settings);
            ValidateFinePreparation(plan.Request);
            var id = FineMonitor.Stage(plan.Request, GetReviewStageDeviceNo());
            return plan with
            {
                Request = plan.Request with
                {
                    PreparedMonitorRunId = id
                }
            };
        }

        /// <summary>PtoP and Flying use the shared Process Move / IOF transactions.
        /// MELSEC and Vision independently follow their owning device's simulation setting.</summary>
        public async Task<ST_FINE_REVIEW_SNAPSHOT> StartFineCorrection(
            ST_FINE_REVIEW_REQUEST request, 
            Action<ST_FINE_REVIEW_SNAPSHOT> progress, 
            CancellationToken cancellationToken = default, 
            Func<IReadOnlyList<ST_REVIEW_PLAN_POINT>, CancellationToken, Task<IReadOnlyList<ST_VISION_MARK_FIND>>>? simulationMeasurement = null
        )
        {
            ValidateFinePreparation(request);
            var deviceNo = GetReviewStageDeviceNo();
            var stageSimulation = IsReviewDeviceSimulation(EN_EQP_MODULE.Melsec, deviceNo);
            var visionSimulation = IsReviewSimulation();
            if (simulationMeasurement is not null && !visionSimulation)
                throw new InvalidOperationException("실제 Vision 모드에서는 시뮬레이션 측정값을 사용할 수 없습니다.");
            var reviewMotion = ST_REVIEW_MOTION_PARAMETERS.Read(settingManager);
            if (!await _sequenceLock.WaitAsync(0, cancellationToken))
                throw new InvalidOperationException("다른 Review 검사가 진행 중입니다.");
            CReviewStageMotion? motion = null;
            var lines = request.Lines.ToArray();
            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var captureAttempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var activeLine = -1;
            var started = false;
            var ngPause = false;
            var runId = Guid.Empty;
            var prefetchedIndex = -1;
            var prefetchedShotKey = "";
            ST_FINE_REVIEW_SNAPSHOT Publish(string message)
            {
                var snapshot = new ST_FINE_REVIEW_SNAPSHOT(request.Plan.RecipeId, Array.AsReadOnly(lines.ToArray()), SequenceState, message, ngPause)
                {
                    RunId = runId
                };
                // Trace the existing progress transition without adding a synchronous file write or device poll.
                logManager?.WriteSequence("INFO", "FINE_PROGRESS", $"Run={runId}; LineIndex={activeLine}; State={SequenceState}; {message}", $"Recipe={request.Plan.RecipeId}; Glass={request.Context?.GlassId}");
                FineMonitor.Publish(snapshot);
                progress(snapshot);
                return snapshot;
            }

            try
            {
                var stageSpeed = stageSimulation && request.Context?.Parameters.ContainsKey("STAGE_SPEED") != true
                    ? request.Plan.StageSpeed : ReadFineStageSpeed(request);
                motion = OpenReviewStageMotion(stageSpeed);
                started = true;
                runId = FineMonitor.Begin(request, deviceNo);
                LastSavedResultPath = null;
                Interlocked.Exchange(ref _stopRequested, 0);
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Running;
                var firstIndex = Array.FindIndex(lines, line => line.Number == request.StartLineNo);
                for (var i = 0; i < lines.Length; i++)
                    if (!request.IsResume || i >= firstIndex)
                    {
                        var line = lines[i];
                        var clean = line.Points.All(point => point.State == EN_REVIEW_POINT_STATE.Ready && point.Judge == "WAIT" && point.ErrorX == 0 && point.ErrorY == 0);
                        if (!clean || line.State != EN_FINE_REVIEW_LINE_STATE.Wait)
                            lines[i] = line with
                            {
                                State = EN_FINE_REVIEW_LINE_STATE.Wait,
                                Points = clean ? line.Points : Array.AsReadOnly(line.Points.Select(ClearFinePoint).ToArray())
                            };
                    }

                Publish("FINE 검사 시작");
                var ptpKeepAliveSent = false;
                for (var index = firstIndex; index < lines.Length; index++)
                {
                    CheckStop();
                    activeLine = index;
                    var original = lines[index];
                    var sorted = original.Points.OrderBy(point => point.ReviewTargetX).ThenBy(point => point.CellNo).ToArray();
                    var ordered = original.Points[0].DesignX <= original.Points[^1].DesignX ? sorted : sorted.Reverse().ToArray();
                    // Resume at the nearest endpoint. After each line the actual current endpoint
                    // determines the next direction, including a reversed NG retry.
                    if (request.Context?.Origin == "AUTO" || request.IsResume || index > firstIndex)
                    {
                        var x = motion.ReviewXPosition;
                        ordered = Math.Abs(x - sorted[0].ReviewTargetX) <= Math.Abs(x - sorted[^1].ReviewTargetX) ? sorted : sorted.Reverse().ToArray();
                    }

                    lines[index] = original with
                    {
                        Points = Array.AsReadOnly(ordered),
                        State = EN_FINE_REVIEW_LINE_STATE.Running
                    };
                    if (request.Method == EN_FINE_INSPECTION_METHOD.PtoP)
                    {
                        for (var pointIndex = 0; pointIndex < ordered.Length; pointIndex++)
                        {
                            var point = ordered[pointIndex];
                            var pointRetry = 0;
                            while (true)
                            {
                                CheckStop();
                                ordered[pointIndex] = point with
                                {
                                    State = EN_REVIEW_POINT_STATE.Current
                                };
                                ReplaceLine(index, ordered, EN_FINE_REVIEW_LINE_STATE.Running);
                                Publish($"Line #{original.Number} / {pointIndex + 1} / {ordered.Length} · " + (pointRetry == 0 ? "위치 이동" : $"Shot 즉시 재시도 {pointRetry} / {request.CountMismatchRetries}"));
                                await MoveAndWait(point, alreadyPositioned: pointRetry > 0);
                                await Task.Delay(250, cancellationToken);
                                CheckStop();
                                if (request.Context?.Origin == "AUTO" && !visionSimulation && !ptpKeepAliveSent)
                                {
                                    await ExecuteVisionCommand("KEEP_ALIVE_REQUEST", "Vision Keep Alive 응답이 없습니다.", cancellationToken);
                                    ptpKeepAliveSent = true;
                                }

                                attempted.Add(point.ShotKey);
                                captureAttempts[point.ShotKey] = pointRetry + 1;
                                ST_REVIEW_MEASURE_RESULT measurement;
                                if (visionSimulation)
                                    measurement = CreateVisionMeasurement(point, (await SimulatedMarks([point]))[0], 1, "FINE_SIMULATION");
                                else
                                    measurement = await MeasureVision(point, cancellationToken);
                                point = ApplyRoughMeasurement(request.Plan, point, measurement);
                                ordered[pointIndex] = point;
                                ReplaceLine(index, ordered, EN_FINE_REVIEW_LINE_STATE.Running);
                                Publish($"Line #{original.Number} · Shot 측정 완료");
                                if (!request.ImmediateRetry || point.State != EN_REVIEW_POINT_STATE.Ng)
                                    break;
                                LogNgAttempt(original.Number, pointRetry + 1, [point]);
                                if (pointRetry >= request.CountMismatchRetries)
                                    throw new InvalidDataException($"Line #{original.Number} / {point.ShotKey} NG 즉시 재시도 한도 {request.CountMismatchRetries}회 초과");
                                pointRetry++;
                            }
                        }
                    }
                    else
                    {
                        var countRetry = 0; // Shared total retry budget: NCK and all-NG attempts cannot reset each other.
                        while (true)
                        {
                            ordered = ordered.Select(point => point with { State = EN_REVIEW_POINT_STATE.Current }).ToArray();
                            ReplaceLine(index, ordered, EN_FINE_REVIEW_LINE_STATE.Running);
                            Publish($"Line #{original.Number} · 시작 위치 이동");
                            await MoveAndWait(ordered[0], alreadyPositioned: countRetry > 0 || prefetchedIndex == index && prefetchedShotKey == ordered[0].ShotKey);
                            prefetchedIndex = -1;
                            CheckStop();
                            // Rebuild the current ordered line on EVERY attempt, including a reversed retry.
                            var triggerBuffer = Array.AsReadOnly(ordered.ToArray());
                            Publish($"Line #{original.Number} · 공통 IOF Buffer 준비 {triggerBuffer.Count}개");
                            await motion!.PrepareIof(triggerBuffer, cancellationToken);
                            CheckStop();
                            if (!visionSimulation)
                            {
                                if (request.Context?.Origin == "AUTO")
                                    await ExecuteVisionCommand("KEEP_ALIVE_REQUEST", "Vision Keep Alive 응답이 없습니다.", cancellationToken);
                                await ExecuteVisionCommand(CreateFlyingReadyCommand(ordered), "Flying Ready 응답이 없습니다.", cancellationToken);
                            }

                            CheckStop();
                            Publish($"Line #{original.Number} · IOF 준비 완료 / X Flying 이동");
                            await MoveAndWait(triggerBuffer[^1], reviewXOnly: true);
                            CheckStop();
                            foreach (var point in triggerBuffer)
                            {
                                attempted.Add(point.ShotKey);
                                captureAttempts[point.ShotKey] = countRetry + 1;
                            }

                            if (!visionSimulation)
                            {
                                try
                                {
                                    await ExecuteVisionCommand("FLYING_MOVE_DONE", "Flying Move Done 응답이 없습니다.", cancellationToken);
                                }
                                catch (CFlyingCountMismatchException)when ((request.Context?.Origin != "AUTO" || request.ImmediateRetry) && countRetry < request.CountMismatchRetries)
                                {
                                    countRetry++;
                                    // The completed end move has placed the camera at the opposite endpoint.
                                    // Keep design IDs; reverse only the transmitted target order, never ask 1003 for an NCK attempt.
                                    ordered = ordered.Reverse().ToArray();
                                    Publish($"Line #{original.Number} Count NCK · 반대 방향 재시도 {countRetry} / {request.CountMismatchRetries}");
                                    continue;
                                }
                                catch (CFlyingCountMismatchException error)
                                {
                                    if (request.Context?.Origin == "AUTO" && !request.ImmediateRetry)
                                        throw new InvalidDataException($"Line #{original.Number} Count NCK / NG 즉시 재시도 OFF", error);
                                    throw new InvalidDataException($"Line #{original.Number} Count NCK 재시도 한도 {request.CountMismatchRetries}회 초과", error);
                                }

                                CheckStop();
                            }

                            async Task ReceiveLine(CancellationToken token)
                            {
                                IReadOnlyList<ST_REVIEW_MEASURE_RESULT> measurements;
                                if (visionSimulation)
                                {
                                    var marks = await SimulatedMarks(triggerBuffer, token);
                                    measurements = triggerBuffer.Select((point, i) => CreateVisionMeasurement(point, marks[i], 1, "FINE_SIMULATION")).ToArray();
                                }
                                else
                                {
                                    var response = await ExecuteVisionCommand("MARK_FIND_LINE", "Mark Find Line 응답이 없습니다.", token);
                                    measurements = ParseVisionLineResponse(response, triggerBuffer);
                                }

                                CheckStop();
                                token.ThrowIfCancellationRequested();
                                if (measurements.Count != triggerBuffer.Count)
                                    throw new InvalidDataException($"라인 결과 수가 일치하지 않습니다. 요청={triggerBuffer.Count}, 수신={measurements.Count}");
                                // Bind to this frozen line/attempt even while the camera moves to the next line.
                                ordered = triggerBuffer.Select((point, i) => ApplyRoughMeasurement(request.Plan, point, measurements[i])).ToArray();
                                ReplaceLine(index, ordered, ordered.Any(point => point.State == EN_REVIEW_POINT_STATE.Ng) ? EN_FINE_REVIEW_LINE_STATE.Ng : EN_FINE_REVIEW_LINE_STATE.Complete);
                                Publish($"Line #{original.Number} · 검사 결과 수신");
                            }

                            if (request.Context?.Origin == "AUTO" && !request.ImmediateRetry && index + 1 < lines.Length)
                            {
                                // OFF overlaps only the next start move with one outstanding 1003 reply.
                                // Do not start the next capture or release the lease before both operations settle.
                                var nextIndex = index + 1;
                                var next = lines[nextIndex].Points.OrderBy(point => point.ReviewTargetX).ThenBy(point => point.CellNo).ToArray();
                                var x = motion.ReviewXPosition;
                                var nextStart = Math.Abs(x - next[0].ReviewTargetX) <= Math.Abs(x - next[^1].ReviewTargetX) ? next[0] : next[^1];
                                using var overlap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                async Task Prefetch()
                                {
                                    try
                                    {
                                        await MoveAndWait(nextStart, overlap.Token);
                                    }
                                    catch (Exception error)when (error is not OperationCanceledException)
                                    {
                                        activeLine = nextIndex;
                                        throw;
                                    }
                                }

                                var receive = ReceiveLine(overlap.Token);
                                var move = Prefetch();
                                var first = await Task.WhenAny(receive, move);
                                if (first.IsFaulted || first.IsCanceled)
                                    overlap.Cancel();
                                try
                                {
                                    await Task.WhenAll(receive, move);
                                }
                                catch
                                {
                                    overlap.Cancel();
                                    throw;
                                }

                                prefetchedIndex = nextIndex;
                                prefetchedShotKey = nextStart.ShotKey;
                            }
                            else
                                await ReceiveLine(cancellationToken);
                            if (request.ImmediateRetry && ordered.Any(point => point.State == EN_REVIEW_POINT_STATE.Ng))
                            {
                                LogNgAttempt(original.Number, countRetry + 1, ordered);
                                if (countRetry >= request.CountMismatchRetries)
                                    throw new InvalidDataException($"Line #{original.Number} NG 즉시 재시도 한도 {request.CountMismatchRetries}회 초과");
                                countRetry++;
                                ordered = ordered.Reverse().ToArray();
                                Publish($"Line #{original.Number} NG · 반대 방향 즉시 재시도 {countRetry} / {request.CountMismatchRetries}");
                                continue;
                            }

                            break;
                        }
                    }

                    var state = ordered.Any(point => point.State == EN_REVIEW_POINT_STATE.Ng) ? EN_FINE_REVIEW_LINE_STATE.Ng : EN_FINE_REVIEW_LINE_STATE.Complete;
                    ReplaceLine(index, ordered, state);
                    activeLine = -1;
                    if (request.StopOnNgLine && request.Method == EN_FINE_INSPECTION_METHOD.Flying && state == EN_FINE_REVIEW_LINE_STATE.Ng)
                    {
                        ngPause = true;
                        SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                        return await Finish($"Line #{original.Number} NG · 라인 끝 위치에서 대기합니다. NG RETRY를 선택해 주세요.");
                    }

                    Publish($"Line #{original.Number} 검사 완료");
                }

                SequenceState = EN_REVIEW_SEQUENCE_STATE.Completed;
                return await Finish("FINE 검사 완료");
            }
            catch (OperationCanceledException)
            {
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                return await Finish("FINE 검사 중지");
            }
            catch (Exception error)
            {
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Failed;
                if (activeLine >= 0)
                {
                    var line = lines[activeLine];
                    lines[activeLine] = line with
                    {
                        State = EN_FINE_REVIEW_LINE_STATE.Error,
                        Points = Array.AsReadOnly(line.Points.Select(point => point.State == EN_REVIEW_POINT_STATE.Current ? ClearFinePoint(point)with { Judge = "ERROR" } : point).ToArray())
                    };
                }

                return await Finish(error.Message);
            }
            finally
            {
                ReleaseMotion();
                _sequenceLock.Release();
            }

            void CheckStop()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsStopRequested)
                    throw new OperationCanceledException("Review Stop");
            }

            void ReplaceLine(int index, IReadOnlyList<ST_REVIEW_PLAN_POINT> points, EN_FINE_REVIEW_LINE_STATE state) => lines[index] = lines[index] with
            {
                Points = Array.AsReadOnly(points.ToArray()),
                State = state
            };
            void LogNgAttempt(int lineNo, int attemptNo, IReadOnlyList<ST_REVIEW_PLAN_POINT> points)
            {
                var ng = points.Where(point => point.State == EN_REVIEW_POINT_STATE.Ng).Select(point => FormattableString.Invariant($"{point.ShotKey}/B{point.BeamNo}:RecvX={point.ErrorX:R},RecvY={point.ErrorY:R}"));
                WriteReviewLog("FINE_NG_ATTEMPT", $"Run={runId},Recipe={request.Plan.RecipeId},Glass={request.Context?.GlassId},Line={lineNo},Attempt={attemptNo},NG={string.Join(";", ng)}");
            }

            async Task MoveAndWait(ST_REVIEW_PLAN_POINT target, CancellationToken? movementToken = null, bool alreadyPositioned = false, bool reviewXOnly = false)
            {
                var token = movementToken ?? cancellationToken;
                CheckStop();
                token.ThrowIfCancellationRequested();
                if (!alreadyPositioned)
                {
                    await motion!.MoveAndWait(target, token, reviewXOnly);
                }
                else
                {
                    // A retry stays at the same point, but must recheck real BUSY/position feedback.
                    await motion!.VerifyArrival(target, token);
                }
            }

            async Task<IReadOnlyList<ST_VISION_MARK_FIND>> SimulatedMarks(IReadOnlyList<ST_REVIEW_PLAN_POINT> targets, CancellationToken? measurementToken = null)
            {
                var token = measurementToken ?? cancellationToken;
                await Task.Delay(80, token);
                CheckStop();
                token.ThrowIfCancellationRequested();
                var marks = simulationMeasurement is not null ? await simulationMeasurement(targets, token) : targets.Select(point => CreateTemporaryVisionMeasurement(point).MarkFind!.Value).ToArray();
                if (marks.Count != targets.Count)
                    throw new InvalidDataException("시뮬레이션 결과 수가 요청과 일치하지 않습니다.");
                return marks;
            }

            void ReleaseMotion()
            {
                motion?.Dispose();
                motion = null;
            }

            async Task<ST_FINE_REVIEW_SNAPSHOT> Finish(string message)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.State == EN_FINE_REVIEW_LINE_STATE.Running || line.Points.Any(point => point.State == EN_REVIEW_POINT_STATE.Current))
                        ReplaceLine(i, line.Points.Select(point => point.State == EN_REVIEW_POINT_STATE.Current ? ClearFinePoint(point) : point).ToArray(), line.Points.Any(point => point.State == EN_REVIEW_POINT_STATE.Ng) ? EN_FINE_REVIEW_LINE_STATE.Ng : line.Points.All(point => point.State == EN_REVIEW_POINT_STATE.Ok) ? EN_FINE_REVIEW_LINE_STATE.Complete : EN_FINE_REVIEW_LINE_STATE.Wait);
                }

                ReleaseMotion();
                var snapshot = Publish(message);
                if (!started)
                    return snapshot;
                try
                {
                    var data = new ST_FINE_REVIEW_RESULT_DATA(request, snapshot, DateTimeOffset.Now, attempted, visionSimulation)
                    {
                        IsStageSimulation = stageSimulation,
                        CaptureAttempts = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(captureAttempts)
                    };
                    var path = await Task.Run(() => reviewResultFile.SaveFineCorrection(data, CancellationToken.None));
                    LastSavedResultPath = path;
                    snapshot = snapshot with
                    {
                        ResultFilePath = path
                    };
                    WriteReviewLog("FINE_RESULT_SAVED", path);
                }
                catch (Exception error)
                {
                    snapshot = snapshot with
                    {
                        ResultSaveError = error.Message,
                        Message = message + " / 검사 결과 저장 실패: " + error.Message
                    };
                    WriteReviewLog("FINE_RESULT_SAVE_ERROR", error.ToString());
                }

                FineMonitor.Publish(snapshot);
                progress(snapshot);
                return snapshot;
            }
        }

        private static ST_REVIEW_PLAN_POINT ClearFinePoint(ST_REVIEW_PLAN_POINT point) => point with
        {
            State = EN_REVIEW_POINT_STATE.Ready,
            Judge = "WAIT",
            ErrorX = 0,
            ErrorY = 0
        };
        private static void ValidateFineRequest(ST_FINE_REVIEW_REQUEST request)
        {
            if (request.CountMismatchRetries is < 0 or > 10)
                throw new InvalidOperationException("Count NCK 재시도 횟수는 0~10이어야 합니다.");
            if (request.ImmediateRetry && request.CountMismatchRetries < 1)
                throw new InvalidOperationException("즉시 재시도 ON일 때는 재시도 횟수가 1회 이상이어야 합니다.");
            if (request.Method is not (EN_FINE_INSPECTION_METHOD.Flying or EN_FINE_INSPECTION_METHOD.PtoP) || request.Lines.Count == 0 || !request.Lines.Any(line => line.Number == request.StartLineNo) || !request.IsResume && request.StartLineNo != 1 || request.Lines.Any(line => line.Points.Count == 0) || request.Lines.Select(line => line.Key).Distinct().Count() != request.Lines.Count || !request.Lines.Select(line => line.Number).SequenceEqual(Enumerable.Range(1, request.Lines.Count)))
                throw new InvalidOperationException("FINE 검사 대상 또는 시작 라인이 올바르지 않습니다.");
            var points = request.Lines.SelectMany(line => line.Points).ToArray();
            if (points.Select(point => point.ShotKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != points.Length || !points.Select(point => point.ShotKey).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(request.Plan.Points.Select(point => point.ShotKey)) || points.Any(point => !point.Use || point.HeadNo <= 0 || !double.IsFinite(point.ReviewTargetX) || !double.IsFinite(point.ReviewTargetY)) || !double.IsFinite(request.Plan.ToleranceX) || request.Plan.ToleranceX < 0 || !double.IsFinite(request.Plan.ToleranceY) || request.Plan.ToleranceY < 0)
                throw new InvalidOperationException("FINE Shot 좌표·중복·사용 여부·공차를 확인해 주세요.");
            if (request.Method == EN_FINE_INSPECTION_METHOD.Flying)
                foreach (var line in request.Lines)
                {
                    if (line.Points.Count > CVisionProtocol.MaximumMarkCount)
                        throw new InvalidOperationException($"Line #{line.Number} 결과가 Vision 패킷 크기 한도를 초과합니다.");
                    if (line.Points.Max(point => point.ReviewTargetY) - line.Points.Min(point => point.ReviewTargetY) > .0005)
                        throw new InvalidOperationException($"Line #{line.Number}에 서로 다른 Stage Y 목표 위치가 포함되어 Flying할 수 없습니다. PtoP를 사용하거나 라인 구성을 확인해 주세요.");
                }
        }

#endregion
#region OneShot
        public async Task<ST_ONE_SHOT_RESULT> ExecuteOneShot(
            ST_REVIEW_PLAN plan, 
            ST_REVIEW_PLAN_POINT point, 
            EN_ONE_SHOT_ACTION action, 
            EN_ONE_SHOT_METHOD method, 
            Action<string> progress, 
            CancellationToken cancellationToken = default
        )
        {
            if (!Enum.IsDefined(action) || !Enum.IsDefined(method))
                throw new ArgumentException("지원하지 않는 One Shot 동작입니다.");
            if (!point.Use || point.HeadNo <= 0 || point.ExclusionReason.Length > 0 || !plan.Points.Any(item => item.ShotKey == point.ShotKey && item.Use) || !double.IsFinite(point.ReviewTargetX) || !double.IsFinite(point.ReviewTargetY))
                throw new InvalidOperationException("가공 가능한 Shot과 Beam을 선택해 주세요.");
            var deviceNo = GetReviewStageDeviceNo();
            var stageSimulation = IsReviewDeviceSimulation(EN_EQP_MODULE.Melsec, deviceNo);
            var move = action != EN_ONE_SHOT_ACTION.Inspection;
            var reviewMotion = ST_REVIEW_MOTION_PARAMETERS.Read(settingManager);
            if (!await _sequenceLock.WaitAsync(0, cancellationToken))
                throw new InvalidOperationException("다른 Review 검사가 진행 중입니다.");
            CReviewStageMotion? motion = null;
            var started = false;
            try
            {
                if (move)
                    motion = OpenReviewStageMotion(plan.StageSpeed);
                Interlocked.Exchange(ref _stopRequested, 0);
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Running;
                started = true;
                CheckStop();
                if (move)
                {
                    progress("Stage Y / Review X 이동 중");
                    CheckStop();
                    await motion!.MoveAndWait(point, cancellationToken);
                    progress("이동 완료");
                }

                ST_VISION_MARK_FIND? mark = null;
                var receivedResolution = 0;
                var cameraResolution = CSettingManager.DefaultVisionCameraResolution;
                var result = point;
                var visionSimulation = IsReviewSimulation();
                if (action != EN_ONE_SHOT_ACTION.Move)
                {
                    if (move)
                        await Task.Delay(250, cancellationToken);
                    CheckStop();
                    progress("Vision 응답 대기 중");
                    ST_REVIEW_MEASURE_RESULT measurement;
                    if (method == EN_ONE_SHOT_METHOD.MarkFindSingle)
                        measurement = await MeasureVision(point, cancellationToken);
                    else if (visionSimulation)
                        measurement = CreateTemporaryVisionMeasurement(point);
                    else
                        measurement = ParseVisionResponse(await ExecuteVisionCommand("MARK_FIND_SETTING_SINGLE", "Mark Find Setting Single 응답이 없습니다.", cancellationToken), point);
                    CheckStop();
                    result = ApplyRoughMeasurement(plan, point, measurement);
                    mark = measurement.MarkFind;
                    receivedResolution = measurement.Resolution;
                    cameraResolution = measurement.CameraResolution;
                }

                CheckStop();
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Completed;
                return new(result, mark, stageSimulation, visionSimulation) { Resolution = receivedResolution, CameraResolution = cameraResolution };
            }
            catch (OperationCanceledException)
            {
                if (started)
                    SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                throw;
            }
            catch
            {
                if (started)
                    SequenceState = EN_REVIEW_SEQUENCE_STATE.Failed;
                throw;
            }
            finally
            {
                motion?.Dispose();
                _sequenceLock.Release();
            }

            void CheckStop()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsStopRequested)
                    throw new OperationCanceledException("Review Stop");
            }
        }

#endregion
#region RoughCorrection
        public static IReadOnlyList<ST_REVIEW_PLAN_POINT> OrderRoughTargets(IEnumerable<ST_REVIEW_PLAN_POINT> points) => points.GroupBy(point => Math.Round(point.DesignY, 3)).OrderBy(group => group.Key).SelectMany((group, row) => row % 2 == 0 ? group.OrderBy(point => point.DesignX).ThenBy(point => point.CellNo) : group.OrderByDescending(point => point.DesignX).ThenBy(point => point.CellNo)).ToArray();
        public async Task<ST_ROUGH_REVIEW_SNAPSHOT> StartRoughCorrection(
            ST_REVIEW_PLAN plan, 
            bool retryNg, 
            Action<ST_ROUGH_REVIEW_SNAPSHOT> progress, 
            CancellationToken cancellationToken = default, 
            Func<ST_REVIEW_PLAN_POINT, CancellationToken, Task<ST_ROUGH_REVIEW_MEASUREMENT>>? simulationMeasurement = null
        )
        {
            var deviceNo = GetReviewStageDeviceNo();
            var stageSimulation = IsReviewDeviceSimulation(EN_EQP_MODULE.Melsec, deviceNo);
            var reviewMotion = ST_REVIEW_MOTION_PARAMETERS.Read(settingManager);
            if (!await _sequenceLock.WaitAsync(0, cancellationToken))
                throw new InvalidOperationException("다른 Review 검사가 진행 중입니다.");
            var points = plan.Points.ToDictionary(point => point.ShotKey, StringComparer.OrdinalIgnoreCase);
            var targets = OrderRoughTargets(plan.Points.Where(point => !retryNg || point.State == EN_REVIEW_POINT_STATE.Ng));
            CReviewStageMotion? motion = null;
            ST_REVIEW_PLAN_POINT? active = null;
            var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var started = false;
            var visionSimulation = IsReviewSimulation();
            ST_ROUGH_REVIEW_SNAPSHOT Publish(string message)
            {
                var snapshot = new ST_ROUGH_REVIEW_SNAPSHOT(plan.RecipeId, Array.AsReadOnly(plan.Points.Select(point => points[point.ShotKey]).ToArray()), SequenceState, message);
                progress(snapshot);
                return snapshot;
            }

            try
            {
                if (targets.Count == 0)
                    throw new InvalidOperationException(retryNg ? "재검사할 NG Shot이 없습니다." : "검사할 Shot이 없습니다.");
                motion = OpenReviewStageMotion(plan.StageSpeed);
                started = true;
                LastSavedResultPath = null;
                Interlocked.Exchange(ref _stopRequested, 0);
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Running;
                if (!retryNg)
                    foreach (var key in points.Keys.ToArray())
                        points[key] = points[key] with
                        {
                            State = EN_REVIEW_POINT_STATE.Ready,
                            Judge = "WAIT",
                            ErrorX = 0,
                            ErrorY = 0
                        };
                Publish(retryNg ? "NG 재검사 시작" : "ROUGH 검사 시작");
                foreach (var target in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsStopRequested)
                        break;
                    active = target;
                    attempted.Add(target.ShotKey);
                    points[target.ShotKey] = target with
                    {
                        State = EN_REVIEW_POINT_STATE.Current,
                        Judge = "WAIT",
                        ErrorX = 0,
                        ErrorY = 0
                    };
                    Publish("이동 / 측정 중");
                    await motion.MoveAndWait(target, cancellationToken);
                    if (IsStopRequested)
                        break;
                    await Task.Delay(250, cancellationToken);
                    if (IsStopRequested)
                        break;
                    if (simulationMeasurement is null)
                        points[target.ShotKey] = ApplyRoughMeasurement(plan, target, await MeasureVision(target, cancellationToken));
                    else
                    {
                        var measured = await simulationMeasurement(target, cancellationToken);
                        points[target.ShotKey] = ApplyRoughMeasurement(plan, target, CreateVisionMeasurement(target,
                            new ST_VISION_MARK_FIND(measured.Judge == "NG" ? 1 : 0, measured.TargetX, measured.TargetY, measured.HoleX, measured.HoleY), measured.Resolution, "ROUGH_SIMULATION", measured.Judge));
                    }

                    active = null;
                    Publish("측정 완료");
                }

                SequenceState = active is null && !IsStopRequested ? EN_REVIEW_SEQUENCE_STATE.Completed : EN_REVIEW_SEQUENCE_STATE.Stopped;
                return await Finish(SequenceState == EN_REVIEW_SEQUENCE_STATE.Completed ? "검사 완료" : "검사 중지");
            }
            catch (OperationCanceledException)
            {
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Stopped;
                return await Finish("검사 중지");
            }
            catch (Exception error)
            {
                SequenceState = EN_REVIEW_SEQUENCE_STATE.Failed;
                if (active is not null)
                    points[active.ShotKey] = active with
                    {
                        State = EN_REVIEW_POINT_STATE.Ready,
                        Judge = "ERROR"
                    };
                return await Finish(error.Message);
            }
            finally
            {
                ReleaseMotion();
                _sequenceLock.Release();
            }

            void ReleaseMotion()
            {
                motion?.Dispose();
                motion = null;
            }

            async Task<ST_ROUGH_REVIEW_SNAPSHOT> Finish(string message)
            {
                if (active is not null && points[active.ShotKey].State == EN_REVIEW_POINT_STATE.Current)
                    points[active.ShotKey] = active with
                    {
                        State = EN_REVIEW_POINT_STATE.Ready,
                        Judge = "WAIT",
                        ErrorX = 0,
                        ErrorY = 0
                    };
                ReleaseMotion();
                var snapshot = Publish(message);
                if (!started)
                    return snapshot;
                try
                {
                    var result = new ST_ROUGH_REVIEW_RESULT_DATA(plan, snapshot, DateTimeOffset.Now, retryNg, attempted, stageSimulation, visionSimulation);
                    // A user Stop cancels motion/measurement, but must not cancel the final result save.
                    var path = await Task.Run(() => reviewResultFile.SaveRoughCorrection(result, CancellationToken.None));
                    LastSavedResultPath = path;
                    snapshot = snapshot with
                    {
                        ResultFilePath = path
                    };
                    WriteReviewLog("ROUGH_RESULT_SAVED", path);
                }
                catch (Exception error)
                {
                    snapshot = snapshot with
                    {
                        ResultSaveError = error.Message,
                        Message = message + " / 검사 결과 저장 실패: " + error.Message
                    };
                    WriteReviewLog("ROUGH_RESULT_SAVE_ERROR", error.ToString());
                }

                progress(snapshot);
                return snapshot;
            }
        }

        private static ST_REVIEW_PLAN_POINT ApplyRoughMeasurement(ST_REVIEW_PLAN plan, ST_REVIEW_PLAN_POINT point, ST_REVIEW_MEASURE_RESULT measurement)
        {
            _ = measurement.MarkFind ?? throw new InvalidDataException("Mark Find 응답에 Target/Hole Pixel 좌표가 없습니다.");
            // Use the already converted mm error; retaining the original pixels
            // alongside it must never lead to a second conversion or lost Resolution.
            return ApplyMeasurement(plan, point, measurement);
        }

#endregion
#region ZeroLine
        private Task<string> SaveZeroLineResult(ST_ZERO_LINE_RESULT_DATA data, CancellationToken token) => reviewResultFile.SaveZeroLine(data, token);
        public async Task<ZeroLineVisionSession> OpenZeroLineVision(ST_ZERO_LINE_CYCLE_PLAN plan, bool stageSimulation, CancellationToken token)
        {
            if (!await _sequenceLock.WaitAsync(0, token).ConfigureAwait(false))
                throw new InvalidOperationException("다른 Review 검사가 진행 중입니다.");
            Interlocked.Exchange(ref _stopRequested, 0);
            return new(this, plan, stageSimulation, IsReviewSimulation());
        }

        public sealed class ZeroLineVisionSession : IDisposable
        {
            private readonly CReviewManager _owner;
            private readonly ST_ZERO_LINE_CYCLE_PLAN _plan;
            private readonly bool _stageSimulation;
            private bool _disposed;
            public bool IsSimulation { get; }

            private bool _keepAlive;
            private bool _prepared;
            private int _readStarted;
            internal ZeroLineVisionSession(CReviewManager owner, ST_ZERO_LINE_CYCLE_PLAN plan, bool stageSimulation, bool simulation)
            {
                _owner = owner;
                _plan = plan;
                _stageSimulation = stageSimulation;
                IsSimulation = simulation;
            }

            public async Task PrepareFirst(CancellationToken token)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                token.ThrowIfCancellationRequested();
                if (_keepAlive)
                    throw new InvalidOperationException("0선 Keep Alive가 중복 호출되었습니다.");
                if (!IsSimulation)
                    await _owner.ExecuteVisionCommand("KEEP_ALIVE_REQUEST", "0선 Vision 1900 응답이 없습니다.", token).ConfigureAwait(false);
                _keepAlive = true;
                if (!IsSimulation)
                    await _owner.ExecuteVisionCommand(_owner.CreateFlyingReadyCommand(_plan.VisionPlan.Points, zeroLine: true), "0선 Vision 1001 응답이 없습니다.", token).ConfigureAwait(false);
                _prepared = true;
            }

            /// <summary>Called once after machining and the stopped Stage Y end position are confirmed.</summary>
            public async Task<IReadOnlyList<ST_ZERO_LINE_POINT_RESULT>> MeasureAll(CancellationToken token)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                token.ThrowIfCancellationRequested();
                if (!_prepared)
                    throw new InvalidOperationException("0선 전체 포인트의 Vision 1001 준비가 필요합니다.");
                if (Interlocked.Exchange(ref _readStarted, 1) != 0)
                    throw new InvalidOperationException("현재 0선 가공 주기의 결과 수신은 이미 요청되었습니다.");
                var points = _plan.VisionPlan.Points;
                IReadOnlyList<ST_REVIEW_MEASURE_RESULT> measurements;
                if (IsSimulation)
                {
                    await Task.Delay(80, token).ConfigureAwait(false);
                    var camera = _owner.CameraResolution;
                    measurements = points.Select(point => _owner.CreateVisionMeasurement(point,
                        new(0, _plan.VisionPlan.ToleranceX * .2 / camera, _plan.VisionPlan.ToleranceY * .2 / camera, 0, 0), 1, "ZERO_LINE_SIMULATION", cameraResolution: camera)).ToArray();
                }
                else
                {
                    try
                    {
                        await _owner.ExecuteVisionCommand("FLYING_MOVE_DONE", "0선 Vision 1002 응답이 없습니다.", token).ConfigureAwait(false);
                    }
                    catch (CFlyingCountMismatchException error)
                    {
                        return points.Select(point => new ST_ZERO_LINE_POINT_RESULT(point with { State = EN_REVIEW_POINT_STATE.Ng, Judge = "NG" }, "NCK", Detail: error.Message)).ToArray();
                    }

                    var response = await _owner.ExecuteVisionCommand("MARK_FIND_LINE", "0선 Vision 1003 응답이 없습니다.", token).ConfigureAwait(false);
                    // The wire has no point IDs: keep the frozen 1001 / trigger order unchanged.
                    measurements = _owner.ParseVisionLineResponse(response, points);
                }

                return points.Select((point, index) =>
                {
                    var measurement = measurements[index];
                    var judged = ApplyRoughMeasurement(_plan.VisionPlan, point, measurement);
                    return new ST_ZERO_LINE_POINT_RESULT(judged, judged.State == EN_REVIEW_POINT_STATE.Ok ? "OK" : "NG", measurement.Resolution, measurement.MarkFind);
                }).ToArray();
            }

            public Task<string> Save(ST_ZERO_LINE_CYCLE_STATUS status, CancellationToken token) => _owner.SaveZeroLineResult(new(_plan, status, _stageSimulation, IsSimulation, DateTimeOffset.Now), token);
            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _owner._sequenceLock.Release();
            }
        }

#region FINE - 보정 계산
        public static IReadOnlyList<ST_FINE_OFFSET_DELTA> CalculateFineOffsets(IReadOnlyList<ST_REVIEW_PLAN_POINT> targets, IReadOnlyList<ST_REVIEW_PLAN_POINT> measurements, EN_VISION_FLIP_TYPE flip)
        {
            if (measurements.Count == 0)
                throw new InvalidOperationException("검사 결과가 없습니다.");
            if (measurements.Any(point => point.State == EN_REVIEW_POINT_STATE.Ng || string.Equals(point.Judge?.Trim(), "NG", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("NG가 있어 FINE Offset 전체를 적용할 수 없습니다. NG RETRY 후 다시 적용해 주세요.");
            if (measurements.Any(point => point.State != EN_REVIEW_POINT_STATE.Ok || !string.Equals(point.Judge?.Trim(), "OK", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("검사 대상이 모두 OK로 완료되어야 Offset을 적용할 수 있습니다.");
            if (measurements.Select(point => point.ShotKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != measurements.Count)
                throw new InvalidOperationException("중복된 검사 Shot이 있습니다.");
            if (measurements.Concat(targets).Any(point => !double.IsFinite(point.DesignX) || !double.IsFinite(point.DesignY)) || measurements.Any(point => !double.IsFinite(point.ErrorX) || !double.IsFinite(point.ErrorY)))
                throw new InvalidOperationException("Shot 좌표 또는 검사 결과가 올바르지 않습니다.");
            var columns = measurements.GroupBy(FineOffsetColumnKey).ToDictionary(group => group.Key, group => group.OrderBy(point => point.DesignY).ToArray());
            var result = new List<ST_FINE_OFFSET_DELTA>();
            foreach (var column in targets.GroupBy(FineOffsetColumnKey))
            {
                // Masking/CHESS may leave a column without any inspected Shot. Preserve that column's saved values.
                if (!columns.TryGetValue(column.Key, out var anchors))
                    continue;
                if (anchors.Select(point => Math.Round(point.DesignY, 6)).Distinct().Count() != anchors.Length)
                    throw new InvalidOperationException($"Cell{column.Key.Cell:00}의 같은 열/Y에 중복 검사 결과가 있습니다.");
                var values = anchors.Select(point => CReviewManager.VisionErrorToStageOffset(point.ErrorX, point.ErrorY, flip)).ToArray();
                var xs = values.Select(value => value.X).ToArray();
                var ys = values.Select(value => value.Y).ToArray();
                foreach (var shot in column)
                {
                    var x = InterpolateFineOffsetColumn(anchors, xs, shot.DesignY);
                    var y = InterpolateFineOffsetColumn(anchors, ys, shot.DesignY);
                    if (!double.IsFinite(x) || !double.IsFinite(y))
                        throw new InvalidOperationException("FINE 보간 결과가 올바르지 않습니다.");
                    result.Add(new(shot, x, y));
                }
            }

            if (result.Count == 0)
                throw new InvalidOperationException("적용할 Shot이 없습니다.");
            return result;
        }

        private static (int Cell, double X) FineOffsetColumnKey(ST_REVIEW_PLAN_POINT point) => (point.CellNo, Math.Round(point.DesignX, 6));
        private static double InterpolateFineOffsetColumn(ST_REVIEW_PLAN_POINT[] anchors, double[] values, double y)
        {
            if (anchors.Length == 1)
                return values[0];
            var origin = anchors[0].DesignY;
            var range = anchors[^1].DesignY - origin;
            var at = (y - origin) / range;
            var sum = 0.0;
            // At most seven anchors. Normalize Y and retain full precision until the accumulated value is saved.
            for (var i = 0; i < anchors.Length; i++)
            {
                if (Math.Abs(y - anchors[i].DesignY) < 1e-9)
                    return values[i];
                var node = (anchors[i].DesignY - origin) / range;
                var basis = 1.0;
                for (var j = 0; j < anchors.Length; j++)
                    if (i != j)
                    {
                        var other = (anchors[j].DesignY - origin) / range;
                        basis *= (at - other) / (node - other);
                    }

                sum += basis * values[i];
            }

            return sum;
        }
#endregion

#region Vision - 수신값·설계·스캐너 좌표 변환
        public static ST_REVIEW_COORDINATE_OFFSET VisionErrorToStageOffset(double visionErrorX, double visionErrorY, EN_VISION_FLIP_TYPE flipType)
        {
            var transformedX = visionErrorX;
            var transformedY = visionErrorY;
            if (flipType.HasFlag(EN_VISION_FLIP_TYPE.XyFlip))
            {
                (transformedX, transformedY) = (transformedY, transformedX);
            }

            if (flipType.HasFlag(EN_VISION_FLIP_TYPE.XFlip))
            {
                transformedX = -transformedX;
            }

            if (flipType.HasFlag(EN_VISION_FLIP_TYPE.YFlip))
            {
                transformedY = -transformedY;
            }

            return new ST_REVIEW_COORDINATE_OFFSET(transformedX, transformedY);
        }

        public static ST_REVIEW_COORDINATE_OFFSET VisionErrorToScannerOffset(double visionErrorX, double visionErrorY, EN_VISION_FLIP_TYPE visionFlipType, ST_SCANNER_AXIS_TRANSFORM scannerAxisTransform)
        {
            var stageOffset = VisionErrorToStageOffset(visionErrorX, visionErrorY, visionFlipType);
            var scannerOffset = scannerAxisTransform.Apply(stageOffset.X, stageOffset.Y);
            return new ST_REVIEW_COORDINATE_OFFSET(scannerOffset.X, scannerOffset.Y);
        }

        public static ST_REVIEW_COORDINATE_FORMULA VisionErrorToScannerFormula(EN_VISION_FLIP_TYPE visionFlipType, ST_SCANNER_AXIS_TRANSFORM scannerAxisTransform)
        {
            var errorXBasis = VisionErrorToScannerOffset(1.0, 0.0, visionFlipType, scannerAxisTransform);
            var errorYBasis = VisionErrorToScannerOffset(0.0, 1.0, visionFlipType, scannerAxisTransform);
            return new ST_REVIEW_COORDINATE_FORMULA(FormatVisionFormula(errorXBasis.X, errorYBasis.X), FormatVisionFormula(errorXBasis.Y, errorYBasis.Y));
        }

        public static EN_VISION_FLIP_TYPE CreateVisionFlipType(bool xFlip, bool yFlip, bool xyFlip)
        {
            var flipType = EN_VISION_FLIP_TYPE.Normal;
            if (xFlip)
            {
                flipType |= EN_VISION_FLIP_TYPE.XFlip;
            }

            if (yFlip)
            {
                flipType |= EN_VISION_FLIP_TYPE.YFlip;
            }

            if (xyFlip)
            {
                flipType |= EN_VISION_FLIP_TYPE.XyFlip;
            }

            return flipType;
        }

        public static EN_VISION_FLIP_TYPE ParseVisionFlipType(string? xFlipValue, string? yFlipValue, string? xyFlipValue)
        {
            return CreateVisionFlipType(ParseVisionOnOff(xFlipValue), ParseVisionOnOff(yFlipValue), ParseVisionOnOff(xyFlipValue));
        }

        public static string FormatVisionFlipType(EN_VISION_FLIP_TYPE flipType)
        {
            return $"X {FormatVisionOnOff(flipType.HasFlag(EN_VISION_FLIP_TYPE.XFlip))} / " + $"Y {FormatVisionOnOff(flipType.HasFlag(EN_VISION_FLIP_TYPE.YFlip))} / " + $"XY {FormatVisionOnOff(flipType.HasFlag(EN_VISION_FLIP_TYPE.XyFlip))}";
        }

        private static bool ParseVisionOnOff(string? value)
        {
            return (value ?? "").Trim().ToUpperInvariant()is "ON" or "TRUE" or "1" or "YES";
        }

        private static string FormatVisionOnOff(bool value)
        {
            return value ? "ON" : "OFF";
        }

        private static string FormatVisionFormula(double errorXCoefficient, double errorYCoefficient)
        {
            if (Math.Abs(errorXCoefficient) > 0.5)
            {
                return errorXCoefficient > 0.0 ? "+Error X" : "-Error X";
            }

            return errorYCoefficient > 0.0 ? "+Error Y" : "-Error Y";
        }
#endregion
#endregion
    }

    public sealed record ST_FINE_OFFSET_DELTA(ST_REVIEW_PLAN_POINT Shot, double X, double Y);
    internal sealed class CFlyingCountMismatchException(string response) : InvalidOperationException("Vision 촬영 개수 불일치 / " + response);
    [Flags]
    public enum EN_VISION_FLIP_TYPE
    {
        Normal = 0,
        XFlip = 1,
        YFlip = 2,
        XyFlip = 4
    }

    public readonly record struct ST_REVIEW_COORDINATE_OFFSET(double X, double Y);
    public readonly record struct ST_REVIEW_COORDINATE_FORMULA(string Gx, string Gy);
    public sealed record ST_REVIEW_FAILURE_MESSAGE(string Stage, string Cause, string Action, string AdditionalCause = "")
    {
        public string ToDisplayText()
        {
            var builder = new StringBuilder();
            builder.AppendLine("검사를 완료하지 못했습니다.").AppendLine().Append("실패 단계: ").AppendLine(Stage).Append("원인: ").AppendLine(Cause).Append("확인 사항: ").Append(Action);
            if (!string.IsNullOrWhiteSpace(AdditionalCause))
            {
                builder.AppendLine().Append("추가 오류: ").Append(AdditionalCause);
            }

            return builder.ToString();
        }
    }

    public static class CReviewFailureMessageBuilder
    {
        private const string ProductUpdateFailureMarker = " Product Review state update failed: ";
        public static ST_REVIEW_FAILURE_MESSAGE Create(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return Create(exception.Message);
        }

        public static ST_REVIEW_FAILURE_MESSAGE Create(string? message)
        {
            var normalizedMessage = string.IsNullOrWhiteSpace(message) ? "Review 검사 중 알 수 없는 오류가 발생했습니다." : message.Trim();
            var productStateFailure = normalizedMessage.Contains(ProductUpdateFailureMarker, StringComparison.OrdinalIgnoreCase);
            var primaryMessage = normalizedMessage;
            var additionalMessage = "";
            var markerIndex = normalizedMessage.IndexOf(ProductUpdateFailureMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                primaryMessage = normalizedMessage[..markerIndex].Trim();
                additionalMessage = normalizedMessage[(markerIndex + ProductUpdateFailureMarker.Length)..].Trim();
            }

            var primary = Classify(primaryMessage, productStateFailure);
            var additionalCause = "";
            if (!string.IsNullOrWhiteSpace(additionalMessage))
            {
                var additional = Classify(additionalMessage, true);
                if (!additional.Cause.Equals(primary.Cause, StringComparison.Ordinal))
                {
                    additionalCause = $"검사 실패 상태를 저장하지 못했습니다. {additional.Cause}";
                }
            }

            return new ST_REVIEW_FAILURE_MESSAGE(primary.Stage, primary.Cause, primary.Action, additionalCause);
        }

        private static ST_REVIEW_FAILURE_PART Classify(string message, bool productStateFailure)
        {
            if (Contains(message, "being used by another process") || Contains(message, "다른 프로세스에서 사용 중"))
            {
                return new ST_REVIEW_FAILURE_PART(productStateFailure ? "검사 상태 저장" : "검사 파일 저장", productStateFailure ? "검사 상태 파일이 다른 프로그램에서 사용 중입니다." : "검사 관련 파일이 다른 프로그램에서 사용 중입니다.", "열려 있는 CSV 또는 중복 실행된 Drilling 프로그램을 닫고 다시 시도해 주세요.");
            }

            if (Contains(message, "access to the path") || Contains(message, "access is denied") || Contains(message, "UnauthorizedAccessException"))
            {
                return new ST_REVIEW_FAILURE_PART(productStateFailure ? "검사 상태 저장" : "검사 파일 저장", "검사 관련 파일에 쓸 수 있는 권한이 없습니다.", "파일의 읽기 전용 상태와 폴더 쓰기 권한을 확인해 주세요.");
            }

            if (Contains(message, "Vision Mark Find Line count mismatch"))
            {
                var requested = ReadValue(message, "Requested=");
                var received = ReadValue(message, "Received=");
                var countText = string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(received) ? "" : $" (요청 {requested}, 수신 {received})";
                return new ST_REVIEW_FAILURE_PART("Vision 결과 검증", $"IOF 결과 개수가 요청 개수와 다릅니다.{countText}", "테스트 클라이언트의 응답 개수와 해당 Line의 검사 대상 수를 확인해 주세요.");
            }

            if (Contains(message, "Vision Mark Find Line result array mismatch"))
            {
                var header = ReadValue(message, "Header=");
                var body = ReadValue(message, "Body=");
                var countText = string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(body) ? "" : $" (헤더 {header}, 결과 {body})";
                return new ST_REVIEW_FAILURE_PART("Vision 결과 검증", $"IOF 응답의 결과 배열 개수가 올바르지 않습니다.{countText}", "Vision 응답의 COUNT와 실제 결과 배열 개수를 확인해 주세요.");
            }

            if (Contains(message, "Vision Mark Find Single response is empty"))
            {
                return new ST_REVIEW_FAILURE_PART("Vision 응답 확인", AppendShot("P2P 검사 응답이 없습니다.", message), "Vision 또는 테스트 클라이언트가 명령 1000에 응답하는지 확인해 주세요.");
            }

            if (Contains(message, "Vision Flying Ready response is empty"))
            {
                return new ST_REVIEW_FAILURE_PART("Vision 응답 확인", "IOF 검사 준비 응답이 없습니다.", "Vision 또는 테스트 클라이언트가 명령 1001에 응답하는지 확인해 주세요.");
            }

            if (Contains(message, "Vision Flying Move Done response is empty"))
            {
                return new ST_REVIEW_FAILURE_PART("Vision 응답 확인", "IOF 이동 완료 응답이 없습니다.", "Vision 또는 테스트 클라이언트가 명령 1002에 응답하는지 확인해 주세요.");
            }

            if (Contains(message, "Vision Mark Find Line response is empty"))
            {
                return new ST_REVIEW_FAILURE_PART("Vision 응답 확인", "IOF 검사 결과 응답이 없습니다.", "Vision 또는 테스트 클라이언트가 명령 1003에 응답하는지 확인해 주세요.");
            }

            if (Contains(message, "Vision client connection timeout") || Contains(message, "Vision client disconnected") || Contains(message, "Vision client is not connected"))
            {
                return new ST_REVIEW_FAILURE_PART("Vision 연결 확인", "Vision 클라이언트가 연결되어 있지 않거나 연결이 끊어졌습니다.", "상단 Vision 상태와 Vision 또는 테스트 클라이언트의 연결을 확인해 주세요.");
            }

            if (Contains(message, "timeout") && Contains(message, "Vision"))
            {
                return new ST_REVIEW_FAILURE_PART("Vision 응답 확인", "정해진 시간 안에 Vision 응답을 받지 못했습니다.", "Vision 연결 상태와 해당 명령의 처리 시간을 확인해 주세요.");
            }

            if (Contains(message, "Vision") && (Contains(message, "response is invalid") || Contains(message, "header is invalid") || Contains(message, "has no result array") || Contains(message, "result is invalid")))
            {
                return new ST_REVIEW_FAILURE_PART("Vision 결과 해석", AppendShot("Vision 응답 형식이 올바르지 않습니다.", message), "Vision 프로토콜의 Body, COUNT, 배열 형식을 확인해 주세요.");
            }

            if (Contains(message, "Review Glass ID mismatch") || Contains(message, "Product changed"))
            {
                return new ST_REVIEW_FAILURE_PART("Glass 연결 확인", "검사 대상 Glass가 현재 활성 Glass와 일치하지 않습니다.", "Monitor의 Glass ID와 Review 화면의 Glass ID를 확인해 주세요.");
            }

            if (Contains(message, "Review Recipe mismatch"))
            {
                return new ST_REVIEW_FAILURE_PART("Recipe 연결 확인", "Glass에 연결된 Recipe와 Review Recipe가 일치하지 않습니다.", "현재 Glass의 Recipe와 상단 선택 Recipe를 확인해 주세요.");
            }

            if (Contains(message, "not assigned to a processing Head"))
            {
                return new ST_REVIEW_FAILURE_PART("검사 계획 생성", "선택한 Shot에 처리 Head가 배정되지 않았습니다.", "Recipe의 Cell 및 Head 배정을 확인해 주세요.");
            }

            if (Contains(message, "Executable review point is empty") || Contains(message, "Ready review point is empty") || Contains(message, "Review plan is empty") || Contains(message, "Vision IOF line is empty"))
            {
                return new ST_REVIEW_FAILURE_PART("검사 계획 생성", "실행할 수 있는 Review Shot이 없습니다.", "Shot 선택, Rule 조건, Head 배정 및 제외 조건을 확인해 주세요.");
            }

            if (Contains(message, "MAX_CELL_NUMBER") || Contains(message, "PIXEL_SIZE") || Contains(message, "SPLITED_BEAM_COUNT") || Contains(message, "Review coordinate setting") || Contains(message, "DOE pitch") || Contains(message, "non-finite review target"))
            {
                return new ST_REVIEW_FAILURE_PART("Recipe 값 검증", "Review 계산에 필요한 Recipe 값이 올바르지 않습니다.", "팝업 발생 전 선택한 Recipe의 Cell, Pitch, Beam 및 좌표 설정을 확인해 주세요.");
            }

            if (Contains(message, "could not find file") || Contains(message, "FileNotFoundException"))
            {
                return new ST_REVIEW_FAILURE_PART("검사 파일 확인", "검사에 필요한 파일을 찾을 수 없습니다.", "Recipe, Review 설정 및 결과 파일 경로를 확인해 주세요.");
            }

            return new ST_REVIEW_FAILURE_PART(productStateFailure ? "검사 상태 저장" : "Review 검사", FirstLine(message), "상세 로그를 확인한 뒤 동일 조건으로 다시 시도해 주세요.");
        }

        private static string AppendShot(string cause, string message)
        {
            var shot = ReadValue(message, "Shot=");
            return string.IsNullOrWhiteSpace(shot) ? cause : $"{cause} (Shot {shot})";
        }

        private static string ReadValue(string message, string key)
        {
            var start = message.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return "";
            }

            start += key.Length;
            var end = start;
            while (end < message.Length && message[end] != ',' && message[end] != '.' && message[end] != ':' && message[end] != '\r' && message[end] != '\n' && !char.IsWhiteSpace(message[end]))
            {
                end++;
            }

            return message[start..end].Trim();
        }

        private static string FirstLine(string message)
        {
            var end = message.Length;
            var lineEnd = message.IndexOfAny(['\r', '\n']);
            if (lineEnd >= 0)
            {
                end = Math.Min(end, lineEnd);
            }

            var stackStart = message.IndexOf("   at ", StringComparison.Ordinal);
            if (stackStart >= 0)
            {
                end = Math.Min(end, stackStart);
            }

            var firstLine = message[..end].Trim();
            const int maximumLength = 240;
            return firstLine.Length <= maximumLength ? firstLine : $"{firstLine[..maximumLength]}...";
        }

        private static bool Contains(string source, string value)
        {
            return source.Contains(value, StringComparison.OrdinalIgnoreCase);
        }

        private sealed record ST_REVIEW_FAILURE_PART(string Stage, string Cause, string Action);
    }

    public sealed record ST_BEAM_INSPECTION_SNAPSHOT(string RecipeId, IReadOnlyList<ST_REVIEW_PLAN_POINT> Points, EN_REVIEW_SEQUENCE_STATE State, int ActiveIndex, string Message, bool IsSimulation)
    {
        public bool IsStageSimulation { get; init; } = IsSimulation;
        public bool IsVisionSimulation { get; init; } = IsSimulation;
        public string? SavedPath { get; init; }
        public string? SaveError { get; init; }
        public int CompletedCount => Points.Count(p => p.State is EN_REVIEW_POINT_STATE.Ok or EN_REVIEW_POINT_STATE.Ng);
        public int NgCount => Points.Count(p => p.State == EN_REVIEW_POINT_STATE.Ng);
    }

    /// <summary>Saved measurements for display; these are not targets in the current Recipe.</summary>
    public sealed record ST_BEAM_INSPECTION_RESULT_BEAM(int BeamNo, double? XMm, double? YMm, string Judge)
    {
        public bool IsMeasured => Judge is "OK" or "NG";
    }

    public sealed record ST_BEAM_INSPECTION_RESULT_SHOT(string GlassName, string HeadName, int HeadNo, string CellName, int CellNo, string ShotName, IReadOnlyList<ST_BEAM_INSPECTION_RESULT_BEAM> Beams);
    public sealed record ST_BEAM_INSPECTION_RESULT_FILE_DATA(string FilePath, string FileName, DateTimeOffset SavedAt, bool IsSimulation, string GlassName, IReadOnlyList<ST_BEAM_INSPECTION_RESULT_SHOT> Shots);
    internal interface IReviewPointListInfo
    {
        bool AreAllPointsSelected { get; }
    }

    /// <summary>
    /// Supplies the actual Recipe Cell shot-group grid to Review rule builders.
    /// Review must use this layout instead of reconstructing a grid from a
    /// flattened point count: the grid is defined by the current Recipe > Cell
    /// calculation (Process PITCH / DOE), not by the legacy pixel dimensions.
    /// </summary>
    internal readonly record struct ST_REVIEW_CELL_POINT_RANGE(int StartIndex, int PointCount, int ColumnCount, int RowCount);
    internal interface IReviewCellPointSource
    {
        int PatternCellCount { get; }

        ST_REVIEW_CELL_POINT_RANGE GetCellRange(int zeroBasedCellIndex);
    }

    public enum EN_REVIEW_POINT_STATE
    {
        Ready,
        Current,
        Ok,
        Ng,
        Skip
    }

    public enum EN_REVIEW_SEQUENCE_STATE
    {
        Idle,
        Running,
        Stopping,
        Stopped,
        Completed,
        Failed
    }

    public enum EN_REVIEW_RULE_TYPE
    {
        AllPoint,
        SamplePoint,
        Edge,
        Center,
        HeadPoint,
        CellPoint,
        ZeroLine
    }

    public sealed record ST_REVIEW_SEQUENCE_STATUS(EN_REVIEW_SEQUENCE_STATE State, int TotalCount, int CompletedCount, int NgCount, string Message);
    public sealed record ST_REVIEW_PLAN_POINT(
        int PointNo, 
        string ShotKey, 
        int HeadNo, 
        int CellNo, 
        int ShotNo, 
        int PixelCountX, 
        int PixelCountY, 
        bool Use, 
        double DesignX, 
        double DesignY, 
        double ReviewTargetX, 
        double ReviewTargetY, 
        double ErrorX, 
        double ErrorY, 
        EN_REVIEW_POINT_STATE State, 
        string Judge
    )
    {
        public int BeamNo { get; init; } = 1;
        public double BeamOffsetGx { get; init; }
        public double BeamOffsetGy { get; init; }
        public string ExclusionReason { get; init; } = "";
    }

    public sealed record ST_REVIEW_BEAM_POSITION(int BeamNo, double StageY, double ReviewX);
    /// <summary>
    /// Physical scanner field used for both Main head assignment and Review
    /// correction-scope presentation.  The value is intentionally expressed in
    /// glass coordinates so callers do not need to recreate a Cell-grid estimate
    /// of a Head range.
    /// </summary>
    public sealed record ST_REVIEW_HEAD_SCOPE(int HeadNo, double PositionX, double ScanFieldWidthX);
    public static class CReviewShotNameFormatter
    {
        public static string ToMatrixName(int shotNo, int columnCount)
        {
            var safeColumnCount = Math.Max(1, columnCount);
            var zeroBasedShotNo = Math.Max(0, shotNo - 1);
            var column = (zeroBasedShotNo % safeColumnCount) + 1;
            var row = (zeroBasedShotNo / safeColumnCount) + 1;
            return $"{ToColumnLetters(column)}{row}";
        }

        private static string ToColumnLetters(int oneBasedColumn)
        {
            var value = Math.Max(1, oneBasedColumn);
            var text = "";
            while (value > 0)
            {
                value--;
                text = (char)('A' + (value % 26)) + text;
                value /= 26;
            }

            return text;
        }
    }

    public sealed record ST_REVIEW_PLAN(
        string RecipeId, 
        int CellCount, 
        double ToleranceX, 
        double ToleranceY, 
        EN_VISION_FLIP_TYPE VisionFlipType, 
        DateTimeOffset CreatedAt, 
        IReadOnlyList<ST_REVIEW_PLAN_POINT> Points
    )
    {
        public IReadOnlyList<ST_REVIEW_PLAN_POINT> GetCellPoints(int cellNo)
        {
            if (Points is CReviewRuntimePoints runtime)
                return runtime.Cell(cellNo);
            if (Points is IReviewCellPointSource cellSource && cellNo >= 1 && cellNo <= cellSource.PatternCellCount)
            {
                var range = cellSource.GetCellRange(cellNo - 1);
                var cellPoints = new List<ST_REVIEW_PLAN_POINT>(range.PointCount);
                for (var index = 0; index < range.PointCount; index++)
                {
                    cellPoints.Add(Points[range.StartIndex + index]);
                }

                return cellPoints;
            }

            var matchingPoints = new List<ST_REVIEW_PLAN_POINT>();
            foreach (var point in Points)
            {
                if (point.CellNo == cellNo)
                {
                    matchingPoints.Add(point);
                }
            }

            return matchingPoints;
        }

        public ST_REVIEW_PLAN_POINT? FindPoint(int cellNo, int shotNo)
        {
            if (Points is CReviewRuntimePoints runtime)
                return runtime.Find(cellNo, shotNo);
            if (Points is IReviewCellPointSource cellSource && cellNo >= 1 && cellNo <= cellSource.PatternCellCount)
            {
                var range = cellSource.GetCellRange(cellNo - 1);
                return shotNo >= 1 && shotNo <= range.PointCount ? Points[range.StartIndex + shotNo - 1] : null;
            }

            foreach (var point in Points)
            {
                if (point.CellNo == cellNo && point.ShotNo == shotNo)
                {
                    return point;
                }
            }

            return null;
        }

        public ST_REVIEW_CORRECTION_PLAN? CorrectionPlan { get; init; }
        public string InspectionMethod { get; init; } = "P2P";
        public double StageScanDirectionY { get; init; } = -1.0;
        public double? StageSpeed { get; init; }

        private static readonly ConditionalWeakTable<IReadOnlyList<ST_REVIEW_PLAN_POINT>, CReviewPointCache> ReviewPointCaches = new();
        private CReviewPointCache ReviewPointCache => ReviewPointCaches.GetValue(Points, static points => new CReviewPointCache(points));
        public IReadOnlyList<ST_REVIEW_PLAN_POINT> ReviewPoints => Points is CReviewRuntimePoints runtime ? runtime.SelectedPoints : ReviewPointCache.ReviewPoints;
        // A new run/retry supplies a full initial snapshot; later publications carry
        // only changed Shots. Consumers may coalesce these deltas without rescanning Points.
        public Guid RuntimeId { get; init; }
        public IReadOnlyList<ST_REVIEW_PLAN_POINT>? PointUpdates { get; init; }

        private sealed class CReviewPointCache(IReadOnlyList<ST_REVIEW_PLAN_POINT> points)
        {
            public IReadOnlyList<ST_REVIEW_PLAN_POINT> ReviewPoints { get; } = points.Count == 0 || points is IReviewPointListInfo { AreAllPointsSelected: true } || points.All(point => point.Use) ? points : points.Where(point => point.Use).ToArray();
        }
    }

    public sealed record ST_REVIEW_CORRECTION_PLAN(
        EN_REVIEW_CORRECTION_RULE Rule, 
        EN_REVIEW_CORRECTION_SCOPE Scope, 
        IReadOnlyList<ST_REVIEW_PLAN_POINT> SourcePoints, 
        EN_REVIEW_REFERENCE_LINE RoughReference = EN_REVIEW_REFERENCE_LINE.First, 
        int FineLineCount = 0
    );
    public sealed record ST_REVIEW_RESULT_DATA(ST_REVIEW_PLAN Plan, IReadOnlyList<ST_REVIEW_PLAN_POINT> Results, DateTimeOffset SavedAt, string GlassId = "", string InspectionMethod = "P2P")
    {
        public string ResultStatus { get; init; } = "COMPLETED";
        public int TotalCount { get; init; }
        public string? TargetFilePath { get; init; }
    }

    public sealed record ST_REVIEW_RESULT_FILE_ROW(DateTimeOffset SavedAt, string RecipeId, string ShotKey, string ShotName, int HeadNo, int CellNo, double ErrorX, double ErrorY, string Judge);
    public sealed record ST_REVIEW_RESULT_FILE_DATA(string FilePath, string FileName, string RecipeId, DateTimeOffset SavedAt, IReadOnlyList<ST_REVIEW_RESULT_FILE_ROW> Rows)
    {
        public string GlassId { get; init; } = "";
        public string InspectionMethod { get; init; } = "";
        public string Rule { get; init; } = "";
        public string RuleScope { get; init; } = "";
        public int BeamNo { get; init; } = 1;
        public int? ReferenceCellNo { get; init; }
        public string ReferenceShot { get; init; } = "";
        public string RoughReference { get; init; } = "";
        public int FineLineCount { get; init; }
        public string ResultStatus { get; init; } = "COMPLETED";
        public int TotalCount { get; init; }
    }

    public interface IReviewResultFile
    {
        string RootPath { get; }

        Task<ST_REVIEW_RESULT_FILE_DATA> Load(string path, CancellationToken cancellationToken = default);
        Task Save(ST_REVIEW_RESULT_DATA result, CancellationToken cancellationToken = default);
    }

    // The surrounding application still exposes Review files through these base
    // classes.  Keeping the compatibility layer here lets the new Review engine
    // run without changing the existing manager/configuration wiring.
    public abstract class CReviewResultFileBase : IReviewResultFile
    {
        public virtual Task<string> SaveZeroLine(ST_ZERO_LINE_RESULT_DATA result, CancellationToken cancellationToken = default) => throw new NotSupportedException("0선방어 결과 저장이 구현되지 않았습니다.");
        public virtual Task<string> SaveFineCorrection(ST_FINE_REVIEW_RESULT_DATA result, CancellationToken cancellationToken = default) => throw new NotSupportedException("FINE 검사 결과 저장이 구현되지 않았습니다.");
        public virtual Task<string> SaveRoughCorrection(ST_ROUGH_REVIEW_RESULT_DATA result, CancellationToken cancellationToken = default) => throw new NotSupportedException("ROUGH 검사 결과 저장이 구현되지 않았습니다.");
        public virtual Task<ST_BEAM_INSPECTION_RESULT_FILE_DATA?> LoadLastBeamInspection(CancellationToken cancellationToken = default) => throw new NotSupportedException("Beam Inspection 결과 불러오기가 구현되지 않았습니다.");
        public virtual Task<string> SaveBeamInspection(ST_BEAM_INSPECTION_SNAPSHOT result, string glassId, DateTimeOffset savedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException("Beam Inspection 결과 저장이 구현되지 않았습니다.");
        public abstract string RootPath { get; }
        public virtual IReadOnlyList<string> SearchRootPaths => [RootPath];
        public virtual string? LastSavedPath => null;

        public abstract Task<ST_REVIEW_RESULT_FILE_DATA> Load(string path, CancellationToken cancellationToken = default);
        public abstract Task Save(ST_REVIEW_RESULT_DATA result, CancellationToken cancellationToken = default);
    }

    public sealed record ST_REVIEW_OFFSET_ROW(int CellId, string ShotId, double ReviewOffsetX, double ReviewOffsetY)
    {
        // Files without coordinate metadata contain legacy scanner-coordinate offsets.
        public bool IsDesignCoordinate { get; init; }
    }

    public sealed record ST_REVIEW_OFFSET_FILE_DATA(string RecipeId, string FilePath, string FileName, DateTimeOffset SavedAt, IReadOnlyList<ST_REVIEW_OFFSET_ROW> Rows)
    {
        public static ST_REVIEW_OFFSET_FILE_DATA Empty(string recipeId)
        {
            return new ST_REVIEW_OFFSET_FILE_DATA(recipeId, "", "", DateTimeOffset.MinValue, []);
        }
    }

    public abstract class CReviewOffsetFileBase
    {
        public abstract string RootPath { get; }

        public abstract Task<ST_REVIEW_OFFSET_FILE_DATA> LoadLatest(string recipeId, CancellationToken cancellationToken = default);
        public abstract Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNew(string recipeId, IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows, CancellationToken cancellationToken = default);
    }

    public interface IReviewManager
    {
        ST_REVIEW_PLAN? CurrentPlan { get; }

        ST_REVIEW_PLAN_POINT? CurrentPoint { get; }

        ST_REVIEW_PLAN_POINT? LastMeasuredPoint { get; }

        EN_REVIEW_SEQUENCE_STATE SequenceState { get; }

        ST_REVIEW_PLAN CreatePlan(ST_RECIPE_DEFINITION recipe, IReadOnlyCollection<string> selectedShotKeys);
        ST_REVIEW_PLAN CreatePlan(ST_RECIPE_DEFINITION recipe, IReadOnlyCollection<string> selectedShotKeys, int selectedBeamNo);
        ST_REVIEW_PLAN CreateAllPlan(ST_RECIPE_DEFINITION recipe, int selectedBeamNo);
        Task<ST_REVIEW_SEQUENCE_STATUS> Start(ST_REVIEW_PLAN plan, string? productId, Action<ST_REVIEW_PLAN>? progress = null, CancellationToken cancellationToken = default);
        void Stop();
        Task<ST_REVIEW_SEQUENCE_STATUS> RetryRemaining(Action<ST_REVIEW_PLAN>? progress = null, CancellationToken cancellationToken = default);
    }

    public enum EN_FINE_INSPECTION_METHOD
    {
        Flying,
        PtoP
    }

    public enum EN_FINE_REVIEW_LINE_STATE
    {
        Wait,
        Running,
        Complete,
        Ng,
        Error
    }

    public sealed record ST_FINE_REVIEW_LINE(string Key, int Number, int ModelNo, IReadOnlyList<ST_REVIEW_PLAN_POINT> Points, EN_FINE_REVIEW_LINE_STATE State = EN_FINE_REVIEW_LINE_STATE.Wait);
    public sealed record ST_FINE_REVIEW_REQUEST(ST_REVIEW_PLAN Plan, IReadOnlyList<ST_FINE_REVIEW_LINE> Lines, EN_FINE_INSPECTION_METHOD Method, int StartLineNo = 1, bool IsResume = false)
    {
        public ST_FINE_REVIEW_CONTEXT? Context { get; init; }
        public bool StopOnNgLine { get; init; } = true;
        public Guid PreparedMonitorRunId { get; init; }
        public int CountMismatchRetries { get; init; }
        // The selected retry limit is shared by Count NCK and optional all-NG immediate retries.
        public bool ImmediateRetry { get; init; }
    }

    public sealed record ST_FINE_REVIEW_SNAPSHOT(string RecipeId, IReadOnlyList<ST_FINE_REVIEW_LINE> Lines, EN_REVIEW_SEQUENCE_STATE State, string Message, bool IsNgPause = false)
    {
        public Guid RunId { get; init; }
        public string ResultFilePath { get; init; } = "";
        public string ResultSaveError { get; init; } = "";
    }

    public sealed record ST_FINE_REVIEW_RESULT_DATA(ST_FINE_REVIEW_REQUEST Request, ST_FINE_REVIEW_SNAPSHOT Snapshot, DateTimeOffset SavedAt, IReadOnlySet<string> AttemptedShotKeys, bool IsVisionSimulation)
    {
        public bool IsStageSimulation { get; init; } = true;
        public IReadOnlyDictionary<string, int> CaptureAttempts { get; init; } = new Dictionary<string, int>();
    }

    public enum EN_ONE_SHOT_ACTION
    {
        Move,
        Inspection,
        MoveAndInspection
    }

    public enum EN_ONE_SHOT_METHOD
    {
        MarkFindSingle,
        MarkFindSettingSingle
    }

    public sealed record ST_ONE_SHOT_RESULT(ST_REVIEW_PLAN_POINT Point, ST_VISION_MARK_FIND? Mark, bool IsStageSimulation, bool IsVisionSimulation)
    {
        public int Resolution { get; init; }
        public double CameraResolution { get; init; } = CSettingManager.DefaultVisionCameraResolution;
    }
    public sealed record ST_ROUGH_REVIEW_SNAPSHOT(string RecipeId, IReadOnlyList<ST_REVIEW_PLAN_POINT> Points, EN_REVIEW_SEQUENCE_STATE State, string Message)
    {
        public string ResultFilePath { get; init; } = "";
        public string ResultSaveError { get; init; } = "";
    }

    public sealed record ST_ROUGH_REVIEW_RESULT_DATA(
        ST_REVIEW_PLAN Plan, 
        ST_ROUGH_REVIEW_SNAPSHOT Snapshot, 
        DateTimeOffset SavedAt, 
        bool IsNgRetry, 
        IReadOnlySet<string> AttemptedShotKeys, 
        bool IsStageSimulation, 
        bool IsVisionSimulation
    );
    /// <summary>Raw camera pixel coordinates. Resolution is the received integer ATF multiplier.</summary>
    public sealed record ST_ROUGH_REVIEW_MEASUREMENT(double TargetX, double TargetY, double HoleX, double HoleY, string Judge)
    {
        public int Resolution { get; init; } = 1;
    }
    public sealed record ST_REVIEW_MOTION_PARAMETERS(double Speed, double Acceleration)
    {
        public static ST_REVIEW_MOTION_PARAMETERS Read(CSettingManager settings) => Read(settings.LoadSection(EN_SETTING_TAB.Motor));
        public static ST_REVIEW_MOTION_PARAMETERS Read(IEnumerable<ST_SYSTEM_PARAMETER> settings)
        {
            var values = settings.Where(p => !string.IsNullOrEmpty(p.Key)).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
            double Get(string key, double fallback)
            {
                if (!values.TryGetValue(key, out var text))
                    return fallback;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0)
                    throw new InvalidOperationException($"Setting Motor의 {key}는 0보다 큰 유한한 숫자여야 합니다.");
                return value;
            }
            
            return new(Get("REVIEW_X_SPEED", 200), Get("REVIEW_X_ACC", 150));
        }
    }

    // Published plans remain immutable. Updates copy only the affected tree branches,
    // while every snapshot shares the fixed Shot/Cell indexes for this run.
    internal sealed class CReviewRuntimePoints : IReadOnlyList<ST_REVIEW_PLAN_POINT>
    {
        private sealed class Index
        {
            public readonly Dictionary<string, int> Shots = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<(int Cell, int Shot), int> Positions = new();
            public readonly Dictionary<int, List<int>> Cells = new();
            public readonly int[] Selected;
            public Index(IReadOnlyList<ST_REVIEW_PLAN_POINT> points)
            {
                var selected = new List<int>();
                for (var i = 0; i < points.Count; i++)
                {
                    var point = points[i];
                    Shots.Add(point.ShotKey, i);
                    Positions.Add((point.CellNo, point.ShotNo), i);
                    if (!Cells.TryGetValue(point.CellNo, out var cell))
                        Cells.Add(point.CellNo, cell = []);
                    cell.Add(i);
                    if (point.Use)
                        selected.Add(i);
                }

                Selected = selected.ToArray();
            }
        }

        private readonly ImmutableList<ST_REVIEW_PLAN_POINT> _points;
        private readonly Index _index;
        public IReadOnlyList<ST_REVIEW_PLAN_POINT> SelectedPoints { get; }
        public int Count => _points.Count;

        public ST_REVIEW_PLAN_POINT this[int index] => _points[index];
        public CReviewRuntimePoints(IReadOnlyList<ST_REVIEW_PLAN_POINT> points) : this(points.ToImmutableList(), new Index(points))
        {
        }

        private CReviewRuntimePoints(ImmutableList<ST_REVIEW_PLAN_POINT> points, Index index)
        {
            _points = points;
            _index = index;
            SelectedPoints = index.Selected.Length == points.Count ? this : new Selection(this);
        }

        public CReviewRuntimePoints Update(IReadOnlyList<ST_REVIEW_PLAN_POINT> updates)
        {
            var builder = _points.ToBuilder();
            foreach (var point in updates)
            {
                var index = _index.Shots[point.ShotKey];
                if (point.Use != _points[index].Use)
                    throw new InvalidOperationException("The inspection selection cannot change during a run.");
                builder[index] = point;
            }

            return new CReviewRuntimePoints(builder.ToImmutable(), _index);
        }

        public ST_REVIEW_PLAN_POINT? Find(string key) => _index.Shots.TryGetValue(key, out var i) ? _points[i] : null;
        public ST_REVIEW_PLAN_POINT? Find(int cell, int shot) => _index.Positions.TryGetValue((cell, shot), out var i) ? _points[i] : null;
        public IReadOnlyList<ST_REVIEW_PLAN_POINT> Cell(int cell) => _index.Cells.TryGetValue(cell, out var indices) ? indices.Select(i => _points[i]).ToArray() : [];
        public IEnumerator<ST_REVIEW_PLAN_POINT> GetEnumerator() => _points.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private sealed class Selection(CReviewRuntimePoints owner) : IReadOnlyList<ST_REVIEW_PLAN_POINT>
        {
            public int Count => owner._index.Selected.Length;

            public ST_REVIEW_PLAN_POINT this[int index] => owner._points[owner._index.Selected[index]];
            public IEnumerator<ST_REVIEW_PLAN_POINT> GetEnumerator()
            {
                foreach (var index in owner._index.Selected)
                    yield return owner._points[index];
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
