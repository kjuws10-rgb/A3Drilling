namespace Drilling.Common.Recipe
{
    using System.Globalization;
    using System.Text.RegularExpressions;
    using Drilling.Common.Correction;
    using Drilling.Common.Managers;
    using Drilling.Common.Review;

    public static class CShotCoordinatePlanBuilder
    {
        public static ST_SHOT_COORDINATE_PLAN Build(IReadOnlyDictionary<string, string> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            var headCount = Math.Clamp(ReadInt(parameters, 8, "HEAD_COUNT"), 1, 8);
            var cellCount = ReadInt(parameters, 0, "MAX_CELL_NUMBER");
            if (cellCount < CCellPatternCalculator.MinCellCount)
            {
                throw new InvalidOperationException($"MAX_CELL_NUMBER must be greater than or equal to {CCellPatternCalculator.MinCellCount}. Current value: {cellCount}");
            }

            var activeHeadNos = BuildActiveHeadNumbers(parameters, headCount);
            var activeHeadSet = activeHeadNos.ToHashSet();
            var assignmentHeadNos = Enumerable.Range(1, headCount).ToArray();
            var glassSizeX = ReadDouble(parameters, 0.0, "GLASS_SIZE_X");
            var glassSizeY = ReadDouble(parameters, 0.0, "GLASS_SIZE_Y");
            var encoderScale = ReadDouble(parameters, 16000.0, "SCAN_ENCODER_SCALE");
            var stageScanDirectionY = ReadDirection(parameters, -1.0, "STAGE_SCAN_DIRECTION_Y");
            var scanStartDelayLengthY = Math.Abs(ReadDouble(parameters, 0.0, "SCAN_START_DELAY_LENGTH_Y"));
            var akMarginY = ReadDouble(parameters, 0.0, "AK_MARGIN_Y");
            var scannerReferenceOffsetX = ReadScannerReferenceOffsetX(parameters);
            var shiftOffsetX = ReadDouble(parameters, 0.0, "SHIFT_OFFSET_X");
            var shiftOffsetY = ReadDouble(parameters, 0.0, "SHIFT_OFFSET_Y");
            var reviewOffsetScale = ReadOffsetScale(parameters, "REVIEW_OFFSET_SCALE");
            var alignOffsetScale = ReadOffsetScale(parameters, "ALIGN_OFFSET_SCALE");
            int SelectHeadNo(int headNo)
            {
                return headNo;
            }

            int SelectValue(int arg0)
            {
                return 0;
            }

            var assignedHeadCounts = assignmentHeadNos.ToDictionary(SelectHeadNo, SelectValue);
            var designShots = BuildDesignShots(parameters, cellCount);
            var preparedShots = new List<ST_PREPARED_SHOT_POINT>(designShots.Count);
            for (var index = 0; index < designShots.Count; index++)
            {
                var designShot = designShots[index];
                var scannerReferenceX = designShot.DesignX + scannerReferenceOffsetX;
                var headNo = AssignHeadNo(scannerReferenceX, assignmentHeadNos, parameters, assignedHeadCounts);
                if (headNo <= 0 || !activeHeadSet.Contains(headNo))
                {
                    continue;
                }

                assignedHeadCounts[headNo]++;
                var headCenterX = ReadHeadCenterX(parameters, headNo);
                var headCenterY = ReadHeadCenterY(parameters, headNo);
                var scannerTransform = CScannerAxisTransform.Read(parameters, headNo);
                var headDefaultOffsetX = ReadHeadDefaultOffset(parameters, headNo, "X");
                var headDefaultOffsetY = ReadHeadDefaultOffset(parameters, headNo, "Y");
                var firstBeamOffsetX = ReadFirstBeamOffset(parameters, headNo, "X");
                var firstBeamOffsetY = ReadFirstBeamOffset(parameters, headNo, "Y");
                var headEncoderScale = ReadHeadEncoderScale(parameters, headNo, encoderScale);
                var stageShotX = scannerReferenceX;
                var designDistanceYFromAlignKey = designShot.DesignY - akMarginY;
                var stageShotY = ConvertRecipeYToStageY(designDistanceYFromAlignKey);
                var shotStageY = headCenterY + stageShotY;
                preparedShots.Add(new ST_PREPARED_SHOT_POINT(
                    index, 
                    designShot, 
                    headNo, 
                    headCenterX, 
                    scannerTransform, 
                    headDefaultOffsetX, 
                    headDefaultOffsetY, 
                    firstBeamOffsetX, 
                    firstBeamOffsetY, 
                    stageShotX, 
                    shotStageY, 
                    designDistanceYFromAlignKey, 
                    shotStageY, 
                    designDistanceYFromAlignKey, 
                    headEncoderScale
                ));
            }

            var firstShotStageY = 0.0;
            var firstShotDesignDistanceY = 0.0;
            if (preparedShots.Count > 0)
            {
                var firstShot = preparedShots[0];
                firstShotStageY = firstShot.ShotStageY;
                for (var index = 1; index < preparedShots.Count; index++)
                {
                    var preparedShot = preparedShots[index];
                    var shotStageY = preparedShot.ShotStageY;
                    if (stageScanDirectionY < 0.0 && shotStageY > firstShotStageY)
                    {
                        firstShot = preparedShot;
                        firstShotStageY = shotStageY;
                    }
                    else if (stageScanDirectionY >= 0.0 && shotStageY < firstShotStageY)
                    {
                        firstShot = preparedShot;
                        firstShotStageY = shotStageY;
                    }
                }

                firstShotStageY = firstShot.ShotStageY;
                firstShotDesignDistanceY = Math.Abs(firstShot.DesignDistanceYFromAlignKey);
            }

            var scanStartStageY = firstShotStageY - (stageScanDirectionY * scanStartDelayLengthY);
            var calculatedShots = new List<ST_HEAD_SHOT_POINT>(preparedShots.Count);
            for (var index = 0; index < preparedShots.Count; index++)
            {
                var preparedShot = preparedShots[index];
                var baseStageWaitDistanceY = ((preparedShot.ShotStageY - scanStartStageY) * stageScanDirectionY) + firstShotDesignDistanceY;
                var stageWaitDistanceY = baseStageWaitDistanceY - shiftOffsetY;
                if (stageWaitDistanceY < 0.0 && Math.Abs(stageWaitDistanceY) <= 0.000001)
                {
                    stageWaitDistanceY = 0.0;
                }

                if (baseStageWaitDistanceY < 0.0 && Math.Abs(baseStageWaitDistanceY) <= 0.000001)
                {
                    baseStageWaitDistanceY = 0.0;
                }

                var baseScannerX = ApplyScannerAxis(preparedShot.StageShotX, preparedShot.HeadCenterX, 0.0);
                // Scanner follow motion compensates the moving stage, so use the opposite stage-basis direction.
                var baseScannerY = -(stageWaitDistanceY * stageScanDirectionY);
                var noShiftBaseScannerY = -(baseStageWaitDistanceY * stageScanDirectionY);
                var scannerBase = preparedShot.ScannerTransform.Apply(baseScannerX, baseScannerY);
                var noShiftScannerBase = preparedShot.ScannerTransform.Apply(baseScannerX, noShiftBaseScannerY);
                var headDefaultScannerOffset = preparedShot.ScannerTransform.Apply(preparedShot.HeadDefaultOffsetX, ConvertRecipeYToStageY(preparedShot.HeadDefaultOffsetY));
                var firstBeamScannerOffset = preparedShot.ScannerTransform.Apply(preparedShot.FirstBeamOffsetX, ConvertRecipeYToStageY(preparedShot.FirstBeamOffsetY));
                // Align offsets are persisted in design coordinates. Apply the current
                // stage/head direction here, when building scanner/script coordinates.
                var alignScannerOffset = CScannerAxisTransform.DesignOffsetToScanner(parameters, preparedShot.HeadNo, preparedShot.DesignShot.AlignOffsetX, preparedShot.DesignShot.AlignOffsetY);
                var reviewScannerOffset = CReviewOffsetData.ToScanner(parameters, preparedShot.HeadNo, preparedShot.DesignShot.ReviewOffsetX, preparedShot.DesignShot.ReviewOffsetY, preparedShot.DesignShot.ReviewOffsetIsDesign);
                // Scale the current machining contribution only. Stored design offsets
                // remain unchanged, so repeated builds never compound the percentage.
                alignScannerOffset = (alignScannerOffset.X * alignOffsetScale, alignScannerOffset.Y * alignOffsetScale);
                reviewScannerOffset = (reviewScannerOffset.X * reviewOffsetScale, reviewScannerOffset.Y * reviewOffsetScale);
                var shiftScannerOffsetXOnly = preparedShot.ScannerTransform.Apply(shiftOffsetX, 0.0);
                var shiftScannerOffsetX = (scannerBase.X - noShiftScannerBase.X) + shiftScannerOffsetXOnly.X;
                var shiftScannerOffsetY = (scannerBase.Y - noShiftScannerBase.Y) + shiftScannerOffsetXOnly.Y;
                var finalScannerOffsetX = preparedShot.DesignShot.RecipeOffsetX + headDefaultScannerOffset.X + firstBeamScannerOffset.X + reviewScannerOffset.X + alignScannerOffset.X;
                var finalScannerOffsetY = preparedShot.DesignShot.RecipeOffsetY + headDefaultScannerOffset.Y + firstBeamScannerOffset.Y + reviewScannerOffset.Y + alignScannerOffset.Y;
                var scannerGx = scannerBase.X + finalScannerOffsetX + shiftScannerOffsetXOnly.X;
                var scannerGy = scannerBase.Y + finalScannerOffsetY + shiftScannerOffsetXOnly.Y;
                var encoderWaitPosition = stageWaitDistanceY * Math.Abs(preparedShot.EncoderScale) * stageScanDirectionY;
                calculatedShots.Add(new ST_HEAD_SHOT_POINT(
                    preparedShot.Index + 1, 
                    preparedShot.DesignShot.ShotKey, 
                    preparedShot.HeadNo, 
                    preparedShot.DesignShot.CellNo, 
                    preparedShot.DesignShot.CellShotNo, 
                    preparedShot.DesignShot.GroupColumn, 
                    preparedShot.DesignShot.GroupRow, 
                    preparedShot.DesignShot.GroupCountX, 
                    preparedShot.DesignShot.GroupCountY, 
                    preparedShot.DesignShot.DesignX, 
                    preparedShot.DesignShot.DesignY, 
                    scannerGx, 
                    scannerGy, 
                    preparedShot.StageShotX, 
                    preparedShot.ShotStageY, 
                    preparedShot.DesignShot.RecipeOffsetX, 
                    preparedShot.DesignShot.RecipeOffsetY, 
                    preparedShot.HeadDefaultOffsetX, 
                    preparedShot.HeadDefaultOffsetY, 
                    preparedShot.FirstBeamOffsetX, 
                    preparedShot.FirstBeamOffsetY, 
                    reviewScannerOffset.X, 
                    reviewScannerOffset.Y, 
                    shiftOffsetX, 
                    shiftOffsetY, 
                    shiftScannerOffsetX, 
                    shiftScannerOffsetY, 
                    finalScannerOffsetX, 
                    finalScannerOffsetY, 
                    encoderWaitPosition
                ) { AlignOffsetX = preparedShot.DesignShot.AlignOffsetX, AlignOffsetY = preparedShot.DesignShot.AlignOffsetY, AlignScannerOffsetX = alignScannerOffset.X, AlignScannerOffsetY = alignScannerOffset.Y });
            }

            global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT SelectItem(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot, int index)
            {
                return shot with
                {
                    SequenceNo = index + 1
                };
            }

            var shots = OrderShots(calculatedShots, stageScanDirectionY).Select(SelectItem).ToArray();
            return new ST_SHOT_COORDINATE_PLAN(headCount, cellCount, glassSizeX, glassSizeY, encoderScale, shots);
        }

        private static double ReadOffsetScale(IReadOnlyDictionary<string, string> parameters, string key)
        {
            if (!parameters.TryGetValue(key, out var text))
                return 1.0; // Existing configurations retain 100% by default.
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent) || percent < 0)
                throw new InvalidOperationException($"{key}: 0 이상의 정수(%)를 입력해 주세요.");
            return percent / 100.0;
        }

        // All recipe shots, including disabled heads and masked/chess-skipped shots.
        // No head assignment or scanner transform participates in this geometry.
        public static IReadOnlyList<ST_DESIGN_SHOT_POINT> BuildDesignShots(IReadOnlyDictionary<string, string> parameters)
        {
            var cellCount = ReadInt(parameters, 0, "MAX_CELL_NUMBER");
            if (cellCount < CCellPatternCalculator.MinCellCount)
                throw new InvalidOperationException("MAX_CELL_NUMBER must be greater than or equal to 1.");
            return BuildDesignShots(parameters, cellCount);
        }

        private static IReadOnlyList<ST_DESIGN_SHOT_POINT> BuildDesignShots(IReadOnlyDictionary<string, string> parameters, int cellCount)
        {
            var akMarginX = ReadDouble(parameters, 0.0, "AK_MARGIN_X");
            var akMarginY = ReadDouble(parameters, 0.0, "AK_MARGIN_Y");
            var pixelSize = ReadDouble(parameters, 0.09, "PIXEL_SIZE");
            var splitBeamCount = ReadInt(parameters, 1, "SPLITED_BEAM_COUNT");
            var modelTypeCount = CRecipeModelPixelCountReader.ReadModelTypeCount(parameters);
            var designShots = new List<ST_DESIGN_SHOT_POINT>();
            for (var cellNo = 1; cellNo <= cellCount; cellNo++)
            {
                var modelType = CRecipeModelPixelCountReader.ReadCellModelType(parameters, cellNo, modelTypeCount);
                var modelPattern = CRecipeModelPixelCountReader.ReadPatternForModel(parameters, modelType);
                var result = CCellPatternCalculator.Calculate(new ST_CELL_PATTERN_DEFINITION(
                    cellNo, 
                    ReadDouble(parameters, 0.0, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_X"), 
                    ReadDouble(parameters, 0.0, $"CELL{cellNo}_ALIGN_TO_1ST_PIXEL_Y"), 
                    ReadDouble(parameters, 0.0, $"CELL{cellNo}_ROTATION"), 
                    pixelSize, 
                    modelPattern.PixelCountX, 
                    modelPattern.PixelCountY, 
                    modelPattern.ProcessPitch, 
                    splitBeamCount, 
                    akMarginX, 
                    akMarginY
                ));
                if (!result.IsValid)
                {
                    throw new InvalidOperationException(result.ValidationMessage);
                }

                var totalShotGroupCount = (long)designShots.Count + result.ShotCenters.Count;
                if (totalShotGroupCount > CCellPatternCalculator.MaxTotalShotGroupCount)
                {
                    throw new InvalidOperationException($"Shot Group Count {totalShotGroupCount:N0} exceeds the total limit " + $"{CCellPatternCalculator.MaxTotalShotGroupCount:N0}.");
                }

                foreach (var shotCenter in result.ShotCenters)
                {
                    var shotGroupName = CReviewShotNameFormatter.ToMatrixName(shotCenter.CellShotNo, result.GroupCountX);
                    var recipeOffsetPrefix = $"CELL{cellNo}_{shotGroupName}_RECIPE_OFFSET_";
                    var reviewOffsetPrefix = $"CELL{cellNo}_{shotGroupName}_REVIEW_OFFSET_";
                    var alignOffsetPrefix = $"CELL{cellNo}_{shotGroupName}_ALIGN_OFFSET_";
                    // Review correction is per Shot only; missing axes are zero, even in legacy recipes with global defaults.
                    designShots.Add(new ST_DESIGN_SHOT_POINT(
                        ToShotKey(cellNo, shotCenter.CellShotNo), 
                        cellNo, 
                        shotCenter.CellShotNo, 
                        shotCenter.GroupColumn, 
                        shotCenter.GroupRow, 
                        result.GroupCountX, 
                        result.GroupCountY, 
                        shotCenter.DesignX, 
                        shotCenter.DesignY, 
                        ReadDouble(parameters, 0.0, $"{recipeOffsetPrefix}X"), 
                        ReadDouble(parameters, 0.0, $"{recipeOffsetPrefix}Y"), 
                        ReadDouble(parameters, 0.0, $"{reviewOffsetPrefix}X"), 
                        ReadDouble(parameters, 0.0, $"{reviewOffsetPrefix}Y"), 
                        CAlignShotOffsetData.ReadValue(parameters, $"{alignOffsetPrefix}X"), 
                        CAlignShotOffsetData.ReadValue(parameters, $"{alignOffsetPrefix}Y")
                    ) { ReviewOffsetIsDesign = CReviewOffsetData.IsDesignCoordinate(parameters, reviewOffsetPrefix) });
                }
            }

            return designShots;
        }

        private static IReadOnlyList<ST_HEAD_SHOT_POINT> OrderShots(IReadOnlyList<ST_HEAD_SHOT_POINT> shots, double stageScanDirectionY)
        {
            double SelectGroupKey(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return Math.Round(shot.EncoderWaitPosition, 6);
            }

            var rows = shots.GroupBy(SelectGroupKey).ToArray();
            double SelectDescendingRowKey(global::System.Linq.IGrouping<double, global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT> row)
            {
                return row.Key;
            }

            double SelectAscendingRowKey(global::System.Linq.IGrouping<double, global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT> row)
            {
                return row.Key;
            }

            var orderedRows = (stageScanDirectionY < 0.0 ? rows.OrderByDescending(SelectDescendingRowKey) : rows.OrderBy(SelectAscendingRowKey)).ToArray();
            var orderedShots = new List<ST_HEAD_SHOT_POINT>(shots.Count);
            int SelectHeadNo(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.HeadNo;
            }

            int SelectHeadGroupKey(global::System.Linq.IGrouping<int, global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT> headGroup)
            {
                return headGroup.Key;
            }

            double SelectScannerGx(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.ScannerGx;
            }

            double SelectScannerGy(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.ScannerGy;
            }

            int SelectCellNo(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.CellNo;
            }

            int SelectCellShotNo(global::Drilling.Common.Recipe.ST_HEAD_SHOT_POINT shot)
            {
                return shot.CellShotNo;
            }

            foreach (var row in orderedRows)
            {
                var headGroups = row.GroupBy(SelectHeadNo).OrderBy(SelectHeadGroupKey);
                foreach (var headGroup in headGroups)
                {
                    var headRowShots = headGroup.ToArray();
                    // Process each head row from the local machining-axis minus side to plus side.
                    var rowShots = UseScannerGyForMachiningSort(headRowShots) ? headRowShots.OrderBy(SelectScannerGy).ThenBy(SelectCellNo).ThenBy(SelectCellShotNo) : headRowShots.OrderBy(SelectScannerGx).ThenBy(SelectCellNo).ThenBy(SelectCellShotNo);
                    orderedShots.AddRange(rowShots);
                }
            }

            return orderedShots;
        }

        private static bool UseScannerGyForMachiningSort(IReadOnlyList<ST_HEAD_SHOT_POINT> shots)
        {
            if (shots.Count <= 1)
            {
                return false;
            }

            var minGx = shots[0].ScannerGx;
            var maxGx = shots[0].ScannerGx;
            var minGy = shots[0].ScannerGy;
            var maxGy = shots[0].ScannerGy;
            for (var index = 1; index < shots.Count; index++)
            {
                var shot = shots[index];
                minGx = Math.Min(minGx, shot.ScannerGx);
                maxGx = Math.Max(maxGx, shot.ScannerGx);
                minGy = Math.Min(minGy, shot.ScannerGy);
                maxGy = Math.Max(maxGy, shot.ScannerGy);
            }

            return maxGy - minGy > maxGx - minGx;
        }

        private static int AssignHeadNo(double designX, IReadOnlyList<int> assignmentHeadNos, IReadOnlyDictionary<string, string> parameters, IReadOnlyDictionary<int, int> assignedHeadCounts)
        {
            if (assignmentHeadNos.Count <= 0)
            {
                return 0;
            }

            global::Drilling.Common.Recipe.ST_HEAD_ASSIGNMENT_CANDIDATE? SelectItem(int headNo)
            {
                var centerX = ReadHeadCenterX(parameters, headNo);
                var widthX = ReadNullableDouble(parameters, $"H{headNo:00}_SCAN_FIELD_WIDTH_X", $"H{headNo:00}_HEAD_FIELD_WIDTH_X") ?? 110.0;
                widthX = widthX > 0.0 ? widthX : 110.0;
                var halfWidth = widthX / 2.0;
                return designX >= centerX - halfWidth && designX <= centerX + halfWidth ? new ST_HEAD_ASSIGNMENT_CANDIDATE(headNo, Math.Abs(designX - centerX)) : (ST_HEAD_ASSIGNMENT_CANDIDATE? )null;
            }

            bool IsHasValue(global::Drilling.Common.Recipe.ST_HEAD_ASSIGNMENT_CANDIDATE? candidate)
            {
                return candidate.HasValue;
            }

            global::Drilling.Common.Recipe.ST_HEAD_ASSIGNMENT_CANDIDATE SelectHead(global::Drilling.Common.Recipe.ST_HEAD_ASSIGNMENT_CANDIDATE? candidate)
            {
                return candidate!.Value;
            }

            var candidates = assignmentHeadNos.Select(SelectItem).Where(IsHasValue).Select(SelectHead).ToArray();
            return candidates.Length == 0 ? 0 : SelectHeadCandidate(candidates, assignedHeadCounts);
        }

        private static int SelectHeadCandidate(IReadOnlyList<ST_HEAD_ASSIGNMENT_CANDIDATE> candidates, IReadOnlyDictionary<int, int> assignedHeadCounts)
        {
            double SelectDistance(global::Drilling.Common.Recipe.ST_HEAD_ASSIGNMENT_CANDIDATE candidate)
            {
                return candidate.Distance;
            }

            int SelectValue(global::Drilling.Common.Recipe.ST_HEAD_ASSIGNMENT_CANDIDATE candidate)
            {
                return assignedHeadCounts.TryGetValue(candidate.HeadNo, out var count) ? count : 0;
            }

            int SelectHeadNo(global::Drilling.Common.Recipe.ST_HEAD_ASSIGNMENT_CANDIDATE candidate)
            {
                return candidate.HeadNo;
            }

            return candidates.OrderBy(SelectDistance).ThenBy(SelectValue).ThenBy(SelectHeadNo).First().HeadNo;
        }

        private static IReadOnlyList<int> BuildActiveHeadNumbers(IReadOnlyDictionary<string, string> parameters, int headCount)
        {
            var activeHeadNos = new List<int>(headCount);
            for (var headNo = 1; headNo <= headCount; headNo++)
            {
                if (ReadHeadUse(parameters, headNo))
                {
                    activeHeadNos.Add(headNo);
                }
            }

            if (activeHeadNos.Count == 0)
            {
                throw new InvalidOperationException("At least one head must be enabled. Check Hxx_USE option settings.");
            }

            return activeHeadNos;
        }

        private static bool ReadHeadUse(IReadOnlyDictionary<string, string> parameters, int headNo)
        {
            return ReadBool(parameters, true, $"H{headNo:00}_USE");
        }

        private static double ReadHeadCenterX(IReadOnlyDictionary<string, string> parameters, int headNo)
        {
            const double fallbackHead1PositionX = -5.0;
            const double fallbackHeadGapX = 200.0;
            var head1PositionX = ReadDouble(parameters, fallbackHead1PositionX, "H01_AK_POSITION_X");
            var headGapX = ReadDouble(parameters, fallbackHeadGapX, "HeadGapX");
            var akMarginX = ReadDouble(parameters, 0.0, "AK_MARGIN_X");
            return akMarginX + head1PositionX + ((headNo - 1) * headGapX);
        }

        private static double ReadHeadCenterY(IReadOnlyDictionary<string, string> parameters, int headNo)
        {
            var explicitHeadY = ReadNullableDouble(parameters, $"H{headNo:00}_AK_POSITION_Y");
            if (explicitHeadY.HasValue)
            {
                return explicitHeadY.Value;
            }

            var head1CenterY = ReadDouble(parameters, 0.0, "REVIEW_TO_HEAD1_GAP_Y");
            var headGapY = ReadDouble(parameters, 0.0, "HeadGapY");
            return headNo % 2 == 0 ? head1CenterY + headGapY : head1CenterY;
        }

        private static double ReadHeadDefaultOffset(IReadOnlyDictionary<string, string> parameters, int headNo, string axis)
        {
            return ReadDouble(parameters, 0.0, $"H{headNo:00}_DEFAULT_OFFSET_{axis}");
        }

        private static double ReadFirstBeamOffset(IReadOnlyDictionary<string, string> parameters, int headNo, string axis)
        {
            return ReadDouble(parameters, 0.0, $"H{headNo:00}_FIRST_BEAM_OFFSET_{axis}");
        }

        private static double ReadHeadEncoderScale(IReadOnlyDictionary<string, string> parameters, int headNo, double defaultValue)
        {
            return ReadDouble(parameters, defaultValue, $"H{headNo:00}_ENCODER_SCALE", "SCAN_ENCODER_SCALE");
        }

        private static double ConvertRecipeYToStageY(double recipeY)
        {
            return -recipeY;
        }

        private static double ApplyScannerAxis(double designPosition, double stageStartPosition, double offset)
        {
            return (designPosition - stageStartPosition) + offset;
        }

        private static double ReadScannerReferenceOffsetX(IReadOnlyDictionary<string, string> parameters)
        {
            var escEdgeToGlassEdge = ReadDouble(parameters, 0.0, "ESC_EDGE_TO_GLASS_EDGE", "ESC_EDGE_TO_GLASS_EDGE_X");
            var glassCenterToScannerCenter = ReadDouble(parameters, 0.0, "GLASS_CENTER_TO_SCANNER_CENTER", "GLASS_CENTER_TO_SCANNER_CENTER_X");
            return escEdgeToGlassEdge + glassCenterToScannerCenter;
        }

        internal static double ReadDirection(IReadOnlyDictionary<string, string> parameters, double defaultValue, params string[] keys)
        {
            bool ShouldIncludeItem(string key)
            {
                return !string.IsNullOrWhiteSpace(key);
            }

            foreach (var key in keys.Where(ShouldIncludeItem))
            {
                if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var normalized = value.Trim().ToUpperInvariant();
                if (double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                {
                    return NormalizeDirection(number, key);
                }

                if (normalized is "-" or "-1" or "REVERSE" or "REV" or "NEGATIVE" or "MINUS")
                {
                    return -1.0;
                }

                if (normalized is "+" or "+1" or "FORWARD" or "FWD" or "POSITIVE" or "PLUS")
                {
                    return 1.0;
                }

                throw new InvalidOperationException($"Invalid direction value. {key}={value}");
            }

            return NormalizeDirection(defaultValue, "DEFAULT_DIRECTION");
        }

        private static double NormalizeDirection(double value, string key)
        {
            if (!double.IsFinite(value) || (value != -1.0 && value != 1.0))
            {
                throw new InvalidOperationException($"Direction setting {key} must be -1 or +1. Current value: {value}");
            }

            return value;
        }

        private static string ToShotKey(int cellNo, int cellShotNo)
        {
            return $"CELL{Math.Max(1, cellNo):000}_SHOT{Math.Max(1, cellShotNo):0000}";
        }

        private static double ReadDouble(IReadOnlyDictionary<string, string> parameters, double defaultValue, params string[] keys)
        {
            bool ShouldIncludeItem(string key)
            {
                return !string.IsNullOrWhiteSpace(key);
            }

            foreach (var key in keys.Where(ShouldIncludeItem))
            {
                if (parameters.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
                {
                    return result;
                }
            }

            return defaultValue;
        }

        private static double? ReadNullableDouble(IReadOnlyDictionary<string, string> parameters, params string[] keys)
        {
            bool ShouldIncludeItem(string key)
            {
                return !string.IsNullOrWhiteSpace(key);
            }

            foreach (var key in keys.Where(ShouldIncludeItem))
            {
                if (parameters.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
                {
                    return result;
                }
            }

            return null;
        }

        private static int ReadInt(IReadOnlyDictionary<string, string> parameters, int defaultValue, params string[] keys)
        {
            bool ShouldIncludeItem(string key)
            {
                return !string.IsNullOrWhiteSpace(key);
            }

            foreach (var key in keys.Where(ShouldIncludeItem))
            {
                if (!parameters.TryGetValue(key, out var value))
                {
                    continue;
                }

                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intResult))
                {
                    return intResult;
                }

                if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var doubleResult))
                {
                    return (int)Math.Round(doubleResult);
                }
            }

            return defaultValue;
        }

        private static bool ReadBool(IReadOnlyDictionary<string, string> parameters, bool defaultValue, params string[] keys)
        {
            bool ShouldIncludeItem(string key)
            {
                return !string.IsNullOrWhiteSpace(key);
            }

            foreach (var key in keys.Where(ShouldIncludeItem))
            {
                if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var normalized = value.Trim().ToUpperInvariant();
                if (normalized is "1" or "Y" or "YES" or "TRUE" or "ON" or "USE")
                {
                    return true;
                }

                if (normalized is "0" or "N" or "NO" or "FALSE" or "OFF" or "SKIP" or "UNUSEsc")
                {
                    return false;
                }

                return defaultValue;
            }

            return defaultValue;
        }
    }

    public sealed record ST_ALIGN_SHOT_OFFSET(int CellNo, int CellShotNo, string ShotName, double DesignX, double DesignY, double X, double Y);
    public static class CAlignShotOffsetData
    {
        private const double MaximumAbsoluteOffset = 100000;
        private static readonly Regex ParameterPattern = new(@"^CELL[1-9][0-9]*_[A-Z]+[1-9][0-9]*_ALIGN_OFFSET_[XY]$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        public static bool IsParameterKey(string key) => ParameterPattern.IsMatch(key);
        public static string ParameterKey(int cellNo, string shotName, string axis) => $"CELL{cellNo}_{shotName}_ALIGN_OFFSET_{axis}";
        public static double RoundValue(double value)
        {
            if (!double.IsFinite(value) || Math.Abs(value) > MaximumAbsoluteOffset)
                throw new InvalidOperationException("Align Offset은 -100000~100000 mm 범위의 유한한 값이어야 합니다.");
            // Round decimal millimetres at the final result; do not round interpolation intermediates.
            var rounded = (double)Math.Round((decimal)value, 3, MidpointRounding.AwayFromZero);
            return rounded == 0 ? 0 : rounded;
        }

        public static string FormatValue(double value) => RoundValue(value).ToString("0.000", CultureInfo.InvariantCulture);
        public static double ReadValue(IReadOnlyDictionary<string, string> parameters, string key)
        {
            if (!parameters.TryGetValue(key, out var text))
                return 0;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || Math.Abs(value) > MaximumAbsoluteOffset)
                throw new InvalidOperationException($"{key}의 Align Offset은 -100000~100000 mm 범위의 유한한 값이어야 합니다.");
            return RoundValue(value);
        }

        public static IReadOnlyList<ST_ALIGN_SHOT_OFFSET> Calculate(ST_RECIPE_DEFINITION recipe, CAlignInterpolation model)
        {
            model.ValidateMeasurements();
            var parameters = recipe.Parameters.Where(parameter => !IsParameterKey(parameter.Key)).ToDictionary(parameter => parameter.Key, parameter => parameter.Value, StringComparer.OrdinalIgnoreCase);
            var shots = CShotCoordinatePlanBuilder.BuildDesignShots(parameters);
            if (shots.Count == 0)
                throw new InvalidOperationException("적용할 Shot이 없습니다.");
            var values = new List<ST_ALIGN_SHOT_OFFSET>(shots.Count);
            foreach (var shot in shots)
            {
                var name = CReviewShotNameFormatter.ToMatrixName(shot.CellShotNo, shot.GroupCountX);
                if (!model.Contains(shot.DesignX, shot.DesignY))
                    throw new InvalidOperationException($"Cell{shot.CellNo} / {name} (X {shot.DesignX:0.###}, Y {shot.DesignY:0.###})가 선택 키의 보간 범위 밖에 있습니다. 전체 Shot 적용을 취소합니다.");
                var correction = model.Evaluate(shot.DesignX, shot.DesignY);
                if (Math.Abs(correction.X) > MaximumAbsoluteOffset || Math.Abs(correction.Y) > MaximumAbsoluteOffset)
                    throw new InvalidOperationException($"Cell{shot.CellNo} / {name}의 계산값이 레시피 Offset 저장 범위를 벗어납니다.");
                values.Add(new(shot.CellNo, shot.CellShotNo, name, shot.DesignX, shot.DesignY, RoundValue(correction.X), RoundValue(correction.Y)));
            }

            return values;
        }

        public static IReadOnlyList<ST_ALIGN_SHOT_OFFSET> ZeroValues(ST_RECIPE_DEFINITION recipe)
        {
            // Reset must also work if a manually edited old offset is invalid.
            var parameters = recipe.Parameters.Where(parameter => !IsParameterKey(parameter.Key)).ToDictionary(parameter => parameter.Key, parameter => parameter.Value, StringComparer.OrdinalIgnoreCase);
            return CShotCoordinatePlanBuilder.BuildDesignShots(parameters).Select(shot => new ST_ALIGN_SHOT_OFFSET(shot.CellNo, shot.CellShotNo, CReviewShotNameFormatter.ToMatrixName(shot.CellShotNo, shot.GroupCountX), shot.DesignX, shot.DesignY, 0, 0)).ToArray();
        }

        public static ST_RECIPE_DEFINITION Apply(ST_RECIPE_DEFINITION recipe, IReadOnlyList<ST_ALIGN_SHOT_OFFSET> offsets)
        {
            var parameters = recipe.Parameters.ToDictionary(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var offset in offsets)
            {
                Add(offset, "X", offset.X);
                Add(offset, "Y", offset.Y);
            }

            return recipe with
            {
                Parameters = parameters.Values.ToArray()
            };
            void Add(ST_ALIGN_SHOT_OFFSET offset, string axis, double value)
            {
                if (!double.IsFinite(value) || Math.Abs(value) > MaximumAbsoluteOffset)
                    throw new InvalidOperationException("유효하지 않은 Align Offset입니다.");
                var key = ParameterKey(offset.CellNo, offset.ShotName, axis);
                if (!IsParameterKey(key))
                    throw new InvalidOperationException("Shot Align Offset 항목을 확인하세요.");
                var text = FormatValue(value);
                parameters[key] = parameters.TryGetValue(key, out var existing) ? existing with
                {
                    Value = text
                }

                : new ST_RECIPE_PARAMETER(
                    $"Shot Group {offset.ShotName} Align Offset {axis}", 
                    text, 
                    "mm", 
                    "-100000 - 100000", 
                    "0.000", 
                    "CELL", 
                    "SHOT_GROUP", 
                    key, 
                    "Design-coordinate align correction; head direction is applied at script generation.", 
                    false, 
                    true, 
                    DataType: EN_RECIPE_DATA_TYPE.Double, 
                    Min: -MaximumAbsoluteOffset, 
                    Max: MaximumAbsoluteOffset
                );
            }
        }

        // The generic recipe editor does not expose generated align fields. Keep the
        // latest saved values when that editor saves only its visible parameters.
        public static ST_RECIPE_DEFINITION RetainUneditedOffsets(ST_RECIPE_DEFINITION edited, ST_RECIPE_DEFINITION? saved)
        {
            if (saved is null)
                return edited;
            var keys = edited.Parameters.Select(parameter => parameter.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var retained = saved.Parameters.Where(parameter => IsParameterKey(parameter.Key) && !keys.Contains(parameter.Key)).ToArray();
            return retained.Length == 0 ? edited : edited with
            {
                Parameters = edited.Parameters.Concat(retained).ToArray()
            };
        }
    }

    public sealed record ST_RECIPE_SHOT_MASKING_RESULT(IReadOnlyList<int> BeamNumbers, IReadOnlyList<int> HoleNumbers, bool IsEdgeMasked, bool IsChessSkipped = false)
    {
        public static ST_RECIPE_SHOT_MASKING_RESULT Empty { get; } = new([], [], false);

        public bool IsMasked
        {
            get
            {
                return BeamNumbers.Count > 0;
            }
        }

        public bool ShouldSkipLaser
        {
            get
            {
                return IsMasked || IsChessSkipped;
            }
        }
    }

    public sealed class CRecipeShotMaskingPlan
    {
        private const int MaximumCellModelTypeCount = 5;
        private const int MaximumMaskingHoleCount = 3;
        private const double Epsilon = 0.000000001;
        private readonly IReadOnlyDictionary<int, ST_MODEL_MASKING_AREA> _models;
        private readonly IReadOnlyDictionary<int, ST_MODEL_PATTERN_AREA> _modelPatterns;
        private readonly IReadOnlyDictionary<int, int> _cellModelTypes;
        private readonly int _modelTypeCount;
        private readonly int _splitBeamCount;
        private CRecipeShotMaskingPlan(
            IReadOnlyDictionary<int, ST_MODEL_MASKING_AREA> models, 
            IReadOnlyDictionary<int, ST_MODEL_PATTERN_AREA> modelPatterns, 
            IReadOnlyDictionary<int, int> cellModelTypes, 
            int modelTypeCount, 
            int splitBeamCount
        )
        {
            _models = models;
            _modelPatterns = modelPatterns;
            _cellModelTypes = cellModelTypes;
            _modelTypeCount = modelTypeCount;
            _splitBeamCount = splitBeamCount;
        }

        public static CRecipeShotMaskingPlan Build(IReadOnlyDictionary<string, string> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            var modelTypeCount = Math.Clamp(ReadInt(parameters, 1, "CELL_MODEL_TYPE_COUNT"), 1, MaximumCellModelTypeCount);
            var pixelSize = ReadDouble(parameters, 0.09, "PIXEL_SIZE");
            var splitBeamCount = ReadInt(parameters, 1, "SPLITED_BEAM_COUNT");
            var models = new Dictionary<int, ST_MODEL_MASKING_AREA>();
            var modelPatterns = new Dictionary<int, ST_MODEL_PATTERN_AREA>();
            for (var modelType = 1; modelType <= modelTypeCount; modelType++)
            {
                var modelPattern = CRecipeModelPixelCountReader.ReadPatternForModel(parameters, modelType);
                var groupPitchMillimeters = pixelSize * modelPattern.ProcessPitch;
                var beamPitchMillimeters = splitBeamCount <= 0 ? 0.0 : groupPitchMillimeters / splitBeamCount;
                modelPatterns[modelType] = new ST_MODEL_PATTERN_AREA(
                    modelPattern.PixelCountX, 
                    modelPattern.PixelCountY, 
                    pixelSize, 
                    Math.Clamp(modelPattern.Chess, 1, 4), 
                    Math.Clamp(modelPattern.ChessType, 0, 1), 
                    groupPitchMillimeters, 
                    beamPitchMillimeters, 
                    beamPitchMillimeters * (Math.Max(1, splitBeamCount) - 1) / 2.0
                );
                var model = BuildModel(parameters, modelType);
                if (model.HasMasking)
                {
                    models[modelType] = model;
                }
            }

            var cellCount = Math.Max(CCellPatternCalculator.MinCellCount, ReadInt(parameters, CCellPatternCalculator.MinCellCount, "MAX_CELL_NUMBER"));
            var cellModelTypes = new Dictionary<int, int>(cellCount);
            for (var cellNo = 1; cellNo <= cellCount; cellNo++)
            {
                cellModelTypes[cellNo] = Math.Clamp(ReadInt(parameters, 1, $"CELL{cellNo}_MODEL_TYPE"), 1, modelTypeCount);
            }

            return new CRecipeShotMaskingPlan(models, modelPatterns, cellModelTypes, modelTypeCount, splitBeamCount);
        }

        public ST_RECIPE_SHOT_MASKING_RESULT EvaluateShot(ST_HEAD_SHOT_POINT point)
        {
            var modelType = GetModelType(point.CellNo);
            var modelPattern = _modelPatterns.TryGetValue(modelType, out var pattern) ? pattern : new ST_MODEL_PATTERN_AREA(0, 0, 0.0, 1, 1, 0.0, 0.0, 0.0);
            var isChessSkipped = IsChessSkipped(point, modelPattern);
            if (_splitBeamCount <= 0 || !double.IsFinite(modelPattern.BeamPitchMillimeters) || modelPattern.BeamPitchMillimeters <= 0.0 || !double.IsFinite(modelPattern.GroupPitchMillimeters))
            {
                return isChessSkipped ? new ST_RECIPE_SHOT_MASKING_RESULT([], [], false, true) : ST_RECIPE_SHOT_MASKING_RESULT.Empty;
            }

            if (!_models.TryGetValue(modelType, out var model))
            {
                return isChessSkipped ? new ST_RECIPE_SHOT_MASKING_RESULT([], [], false, true) : ST_RECIPE_SHOT_MASKING_RESULT.Empty;
            }

            var edgeArea = model.Edge?.Create(modelPattern.PixelCountX, modelPattern.PixelCountY, modelPattern.PixelSizeMillimeters, modelPattern.BeamHalfSpanMillimeters);
            var maskedBeamNumbers = new List<int>();
            var maskedHoleNumbers = new SortedSet<int>();
            var isEdgeMasked = false;
            var beamHalfSpan = modelPattern.BeamPitchMillimeters / 2.0;
            var centerIndex = (_splitBeamCount - 1) / 2.0;
            // Mask membership is fixed by the Cell's recipe design, just like Chess.
            // Offsets move the machining coordinates only: Recipe/Default/FirstBeam/
            // Align/Review/Shift corrections and Head flips must never reclassify a Shot.
            // The masking rectangles use this same Cell-local, uncorrected design frame.
            var centerX = point.GroupColumn * modelPattern.GroupPitchMillimeters;
            var centerY = point.GroupRow * modelPattern.GroupPitchMillimeters;
            for (var row = 0; row < _splitBeamCount; row++)
            {
                for (var column = 0; column < _splitBeamCount; column++)
                {
                    var beamBounds = ST_MASKING_RECT.FromCenter(centerX + ((column - centerIndex) * modelPattern.BeamPitchMillimeters), centerY + ((row - centerIndex) * modelPattern.BeamPitchMillimeters), beamHalfSpan);
                    var isHoleMasked = false;
                    foreach (var hole in model.Holes)
                    {
                        if (!hole.Intersects(beamBounds))
                        {
                            continue;
                        }

                        maskedHoleNumbers.Add(hole.HoleNo);
                        isHoleMasked = true;
                        break;
                    }

                    var isOutsideEdge = edgeArea is not null && edgeArea.Touches(beamBounds);
                    if (!isHoleMasked && !isOutsideEdge)
                    {
                        continue;
                    }

                    maskedBeamNumbers.Add((row * _splitBeamCount) + column + 1);
                    isEdgeMasked = isEdgeMasked || isOutsideEdge;
                }
            }

            return maskedBeamNumbers.Count == 0 && !isChessSkipped ? ST_RECIPE_SHOT_MASKING_RESULT.Empty : new ST_RECIPE_SHOT_MASKING_RESULT(maskedBeamNumbers, maskedHoleNumbers.ToArray(), isEdgeMasked, isChessSkipped);
        }

        private int GetModelType(int cellNo)
        {
            return _cellModelTypes.TryGetValue(cellNo, out var cellModelType) ? Math.Clamp(cellModelType, 1, _modelTypeCount) : 1;
        }

        private static bool IsChessSkipped(ST_HEAD_SHOT_POINT point, ST_MODEL_PATTERN_AREA modelPattern)
        {
            // Design row/column identity is invariant under machining corrections.
            if (modelPattern.Chess <= 1)
            {
                return false;
            }

            return modelPattern.ChessType == 0 ? PositiveModulo(point.GroupColumn, modelPattern.Chess) != 0 || PositiveModulo(point.GroupRow, modelPattern.Chess) != 0 : PositiveModulo(point.GroupColumn - point.GroupRow, modelPattern.Chess) != 0;
        }

        private static ST_MODEL_MASKING_AREA BuildModel(IReadOnlyDictionary<string, string> parameters, int modelType)
        {
            var prefix = $"MODEL{Math.Clamp(modelType, 1, MaximumCellModelTypeCount)}_";
            var holeCount = Math.Clamp(ReadInt(parameters, 0, $"{prefix}MASKING_HOLE_NUMBER"), 0, MaximumMaskingHoleCount);
            var holes = new List<ST_MASKING_HOLE_AREA>(holeCount);
            for (var holeNo = 1; holeNo <= holeCount; holeNo++)
            {
                var x = ReadDouble(parameters, double.NaN, $"{prefix}MASKING_HOLE{holeNo}_X");
                var y = ReadDouble(parameters, double.NaN, $"{prefix}MASKING_HOLE{holeNo}_Y");
                var sizeX = ReadDouble(parameters, 0.0, $"{prefix}MASKING_HOLE{holeNo}_SIZE_X");
                var sizeY = ReadDouble(parameters, 0.0, $"{prefix}MASKING_HOLE{holeNo}_SIZE_Y");
                if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(sizeX) || !double.IsFinite(sizeY) || sizeX <= 0.0 || sizeY <= 0.0)
                {
                    continue;
                }

                holes.Add(new ST_MASKING_HOLE_AREA(holeNo, x, y, sizeX, sizeY));
            }

            return new ST_MODEL_MASKING_AREA(holes, BuildEdge(parameters, prefix));
        }

        private static ST_EDGE_MASKING_AREA? BuildEdge(IReadOnlyDictionary<string, string> parameters, string prefix)
        {
            const double tolerance = 0.001;
            var upLeft = ST_MASKING_POINT.FromPositiveOffset(ReadDouble(parameters, 0.0, $"{prefix}CELL_UP_LEFT_ROUND_X"), ReadDouble(parameters, 0.0, $"{prefix}CELL_UP_LEFT_ROUND_Y"));
            var upRight = ST_MASKING_POINT.FromPositiveOffset(ReadDouble(parameters, 0.0, $"{prefix}CELL_UP_RIGHT_ROUND_X"), ReadDouble(parameters, 0.0, $"{prefix}CELL_UP_RIGHT_ROUND_Y"));
            var downLeft = ST_MASKING_POINT.FromPositiveOffset(ReadDouble(parameters, 0.0, $"{prefix}CELL_DOWN_LEFT_ROUND_X"), ReadDouble(parameters, 0.0, $"{prefix}CELL_DOWN_LEFT_ROUND_Y"));
            var downRight = ST_MASKING_POINT.FromPositiveOffset(ReadDouble(parameters, 0.0, $"{prefix}CELL_DOWN_RIGHT_ROUND_X"), ReadDouble(parameters, 0.0, $"{prefix}CELL_DOWN_RIGHT_ROUND_Y"));
            var values = new[]
            {
                upLeft.X,
                upLeft.Y,
                upRight.X,
                upRight.Y,
                downLeft.X,
                downLeft.Y,
                downRight.X,
                downRight.Y
            };
            if (values.All(value => Math.Abs(value) <= tolerance))
            {
                return null;
            }

            return values.Any(value => !double.IsFinite(value)) ? null : new ST_EDGE_MASKING_AREA(upLeft, upRight, downLeft, downRight);
        }

        private static int ReadInt(IReadOnlyDictionary<string, string> parameters, int defaultValue, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!parameters.TryGetValue(key, out var value))
                {
                    continue;
                }

                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intResult))
                {
                    return intResult;
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleResult))
                {
                    return (int)Math.Round(doubleResult);
                }
            }

            return defaultValue;
        }

        private static double ReadDouble(IReadOnlyDictionary<string, string> parameters, double defaultValue, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (parameters.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
                {
                    return result;
                }
            }

            return defaultValue;
        }

        private static int PositiveModulo(int value, int divisor)
        {
            return ((value % divisor) + divisor) % divisor;
        }

        private readonly record struct ST_MASKING_POINT(double X, double Y)
        {
            public static ST_MASKING_POINT FromPositiveOffset(double x, double y)
            {
                return new ST_MASKING_POINT(Math.Abs(x), Math.Abs(y));
            }
        }

        private sealed record ST_EDGE_CORNER_MASKING_AREA(IReadOnlyList<ST_MASKING_RECT> Bounds)
        {
            public bool Touches(ST_MASKING_RECT bounds)
            {
                return Bounds.Any(area => area.Intersects(bounds));
            }
        }

        private sealed record ST_MODEL_PATTERN_AREA(
            int PixelCountX, 
            int PixelCountY, 
            double PixelSizeMillimeters, 
            int Chess, 
            int ChessType, 
            double GroupPitchMillimeters, 
            double BeamPitchMillimeters, 
            double BeamHalfSpanMillimeters
        );
        private sealed record ST_MODEL_MASKING_AREA(IReadOnlyList<ST_MASKING_HOLE_AREA> Holes, ST_EDGE_MASKING_AREA? Edge)
        {
            public bool HasMasking
            {
                get
                {
                    return Holes.Count > 0 || Edge is not null;
                }
            }
        }

        private readonly record struct ST_MASKING_RECT(double Left, double Right, double Top, double Bottom)
        {
            public static ST_MASKING_RECT FromCenter(double x, double y, double halfSpan)
            {
                return new ST_MASKING_RECT(x - halfSpan, x + halfSpan, y - halfSpan, y + halfSpan);
            }

            public static ST_MASKING_RECT FromCorners(double left, double right, double top, double bottom)
            {
                return new ST_MASKING_RECT(Math.Min(left, right), Math.Max(left, right), Math.Min(top, bottom), Math.Max(top, bottom));
            }

            public bool Intersects(ST_MASKING_RECT other)
            {
                return Left <= other.Right + Epsilon && Right >= other.Left - Epsilon && Top <= other.Bottom + Epsilon && Bottom >= other.Top - Epsilon;
            }
        }

        private sealed record ST_MASKING_HOLE_AREA(int HoleNo, double X, double Y, double SizeX, double SizeY)
        {
            public bool Intersects(ST_MASKING_RECT bounds)
            {
                return new ST_MASKING_RECT(X, X + SizeX, Y, Y + SizeY).Intersects(bounds);
            }
        }

        private sealed record ST_EDGE_MASKING_AREA(ST_MASKING_POINT UpLeft, ST_MASKING_POINT UpRight, ST_MASKING_POINT DownLeft, ST_MASKING_POINT DownRight)
        {
            public ST_EDGE_CORNER_MASKING_AREA? Create(int pixelCountX, int pixelCountY, double pixelSizeMillimeters, double firstDoeCenterOffsetMillimeters)
            {
                if (pixelCountX <= 0 || pixelCountY <= 0 || !double.IsFinite(pixelSizeMillimeters) || pixelSizeMillimeters <= 0.0 || !double.IsFinite(firstDoeCenterOffsetMillimeters) || firstDoeCenterOffsetMillimeters < 0.0)
                {
                    return null;
                }

                var leftPixelX = -firstDoeCenterOffsetMillimeters;
                var topPixelY = -firstDoeCenterOffsetMillimeters;
                var rightPixelX = leftPixelX + (Math.Max(0, pixelCountX - 1) * pixelSizeMillimeters);
                var bottomPixelY = topPixelY + (Math.Max(0, pixelCountY - 1) * pixelSizeMillimeters);
                var bounds = new List<ST_MASKING_RECT>(4);
                AddCorner(bounds, leftPixelX, leftPixelX + UpLeft.X, topPixelY, topPixelY + UpLeft.Y);
                AddCorner(bounds, rightPixelX - UpRight.X, rightPixelX, topPixelY, topPixelY + UpRight.Y);
                AddCorner(bounds, leftPixelX, leftPixelX + DownLeft.X, bottomPixelY - DownLeft.Y, bottomPixelY);
                AddCorner(bounds, rightPixelX - DownRight.X, rightPixelX, bottomPixelY - DownRight.Y, bottomPixelY);
                return bounds.Count == 0 ? null : new ST_EDGE_CORNER_MASKING_AREA(bounds);
            }

            private static void AddCorner(List<ST_MASKING_RECT> bounds, double left, double right, double top, double bottom)
            {
                if (!double.IsFinite(left) || !double.IsFinite(right) || !double.IsFinite(top) || !double.IsFinite(bottom) || Math.Abs(right - left) <= Epsilon || Math.Abs(bottom - top) <= Epsilon)
                {
                    return;
                }

                bounds.Add(ST_MASKING_RECT.FromCorners(left, right, top, bottom));
            }
        }
    }

    /// <summary>Persist raw offsets and resolve their coordinate basis only when building process coordinates.</summary>
    public static class CReviewOffsetData
    {
        public static bool IsParameterKey(string key)
        {
            if (key.Equals("REVIEW_OFFSET_X", StringComparison.OrdinalIgnoreCase) || key.Equals("REVIEW_OFFSET_Y", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!key.StartsWith("CELL", StringComparison.OrdinalIgnoreCase))
                return false;
            var separator = key.IndexOf('_', 4);
            if (separator < 5 || !int.TryParse(key.AsSpan(4, separator - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var cellNo) || cellNo <= 0)
                return false;
            const string marker = "_REVIEW_OFFSET_";
            var markerAt = key.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            // Legacy Shot IDs may contain underscores; require a nonempty Shot and an exact field suffix.
            if (markerAt <= separator + 1)
                return false;
            var field = key.AsSpan(markerAt + marker.Length);
            return field.Equals("X", StringComparison.OrdinalIgnoreCase) || field.Equals("Y", StringComparison.OrdinalIgnoreCase) || field.Equals("SPACE", StringComparison.OrdinalIgnoreCase);
        }

        public static void RemoveFromParameters(IDictionary<string, string> parameters)
        {
            foreach (var key in parameters.Keys.Where(IsParameterKey).ToArray())
                parameters.Remove(key);
        }

        public static ST_REVIEW_OFFSET_ROW[] ValidateRows(IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows, string source, IReadOnlyList<int>? lineNumbers = null, CancellationToken token = default)
        {
            var normalized = new ST_REVIEW_OFFSET_ROW[rows.Count];
            var seen = new Dictionary<(int Cell, string Shot), int>(rows.Count);
            for (var index = 0; index < rows.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var line = lineNumbers?[index] ?? index + 2;
                var row = rows[index];
                if (row is null)
                    throw new CReviewOffsetValidationException(source, line, "Offset 행이 비어 있습니다.");
                var shot = (row.ShotId ?? "").Trim().Replace('-', '_').Replace('.', '_').ToUpperInvariant();
                if (row.CellId <= 0 || shot.Length == 0)
                    throw new CReviewOffsetValidationException(source, line, "Cell 번호는 1 이상이고 Shot 이름은 비어 있지 않아야 합니다.");
                if (!double.IsFinite(row.ReviewOffsetX) || !double.IsFinite(row.ReviewOffsetY))
                    throw new CReviewOffsetValidationException(source, line, $"Cell{row.CellId} / {shot}: Offset X/Y에 NaN 또는 무한대를 사용할 수 없습니다.");
                if (!seen.TryAdd((row.CellId, shot), line))
                    throw new CReviewOffsetValidationException(source, line, $"Cell{row.CellId} / {shot}이 중복되어 있습니다. 최초 행: {seen[(row.CellId, shot)]}.");
                normalized[index] = row.ShotId == shot ? row : row with
                {
                    ShotId = shot
                };
            }

            return normalized;
        }

        public static void ApplyToParameters(IDictionary<string, string> parameters, ST_REVIEW_OFFSET_FILE_DATA file)
        {
            // Reject the entire input before removing any previously valid offsets.
            var rows = ValidateRows(file.Rows, string.IsNullOrWhiteSpace(file.FilePath) ? file.FileName : file.FilePath);
            // Legacy global REVIEW_OFFSET_X/Y defaults are obsolete and are intentionally cleared as well.
            RemoveFromParameters(parameters);
            foreach (var row in rows)
            {
                var prefix = $"CELL{row.CellId}_{row.ShotId}_REVIEW_OFFSET_";
                parameters[$"{prefix}X"] = row.ReviewOffsetX.ToString("R", CultureInfo.InvariantCulture);
                parameters[$"{prefix}Y"] = row.ReviewOffsetY.ToString("R", CultureInfo.InvariantCulture);
                parameters[$"{prefix}SPACE"] = row.IsDesignCoordinate ? "DESIGN" : "SCANNER";
            }
        }

        public static bool IsDesignCoordinate(IReadOnlyDictionary<string, string> parameters, string prefix)
        {
            var space = parameters.GetValueOrDefault($"{prefix}SPACE", "SCANNER").Trim().ToUpperInvariant();
            return space switch
            {
                "DESIGN" => true,
                "SCANNER" => false,
                _ => throw new InvalidOperationException($"Invalid Review Offset coordinate space: {space}")};
        }

        public static (double X, double Y) ToScanner(IReadOnlyDictionary<string, string> parameters, int headNo, double x, double y, bool isDesign) => isDesign ? CScannerAxisTransform.DesignOffsetToScanner(parameters, headNo, x, y) : (x, y);
        public static (double X, double Y) ToDesign(IReadOnlyDictionary<string, string> parameters, int headNo, double x, double y, bool isDesign) => isDesign ? (x, y) : CScannerAxisTransform.ScannerOffsetToDesign(parameters, headNo, x, y);
    }

    public sealed class CReviewOffsetValidationException : IOException
    {
        public string FilePath { get; }
        public int RowNumber { get; }

        public CReviewOffsetValidationException(string source, int rowNumber, string reason) : base($"Review Offset을 적용할 수 없습니다.\n파일: {source}\n" + (rowNumber > 0 ? $"행: {rowNumber}\n" : "") + $"원인: {reason}")
        {
            FilePath = source;
            RowNumber = rowNumber;
        }
    }

    public readonly record struct ST_SCANNER_AXIS_TRANSFORM(bool XyFlip, bool XFlip, bool YFlip)
    {
        public (double X, double Y) Apply(double x, double y)
        {
            if (XyFlip)
            {
                (x, y) = (y, x);
            }

            if (XFlip)
            {
                x = -x;
            }

            if (YFlip)
            {
                y = -y;
            }

            return (x, y);
        }
    }

    public static class CScannerAxisTransform
    {
        public static double ReadStageDirection(IReadOnlyDictionary<string, string> parameters) => CShotCoordinatePlanBuilder.ReadDirection(parameters, -1.0, "STAGE_SCAN_DIRECTION_Y");
        public static ST_SCANNER_AXIS_TRANSFORM CreateDefault(int headNo)
        {
            return new ST_SCANNER_AXIS_TRANSFORM(false, false, false);
        }

        public static ST_SCANNER_AXIS_TRANSFORM Read(IReadOnlyDictionary<string, string> parameters, int headNo)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            var defaults = CreateDefault(headNo);
            return new ST_SCANNER_AXIS_TRANSFORM(ReadOnOff(parameters, defaults.XyFlip, $"H{headNo:00}_SCANNER_XY_FLIP"), ReadOnOff(parameters, defaults.XFlip, $"H{headNo:00}_SCANNER_X_FLIP"), ReadOnOff(parameters, defaults.YFlip, $"H{headNo:00}_SCANNER_Y_FLIP"));
        }

        public static ST_SCANNER_AXIS_TRANSFORM Read(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var defaults = CreateDefault(headNo);
            return new ST_SCANNER_AXIS_TRANSFORM(ReadOnOff(settings, defaults.XyFlip, $"H{headNo:00}_SCANNER_XY_FLIP"), ReadOnOff(settings, defaults.XFlip, $"H{headNo:00}_SCANNER_X_FLIP"), ReadOnOff(settings, defaults.YFlip, $"H{headNo:00}_SCANNER_Y_FLIP"));
        }

        public static (double X, double Y) DesignOffsetToScanner(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo, double offsetX, double offsetY)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var parameters = settings.GroupBy(setting => string.IsNullOrWhiteSpace(setting.Key) ? setting.Name : setting.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
            return DesignOffsetToScanner(parameters, headNo, offsetX, offsetY);
        }

        public static (double X, double Y) DesignOffsetToScanner(IReadOnlyDictionary<string, string> parameters, int headNo, double offsetX, double offsetY)
        {
            var stageDirectionY = CShotCoordinatePlanBuilder.ReadDirection(parameters, -1.0, "STAGE_SCAN_DIRECTION_Y");
            // Auto-run Shift Offset changes the scanner by Apply(X, Y * stage direction).
            return Read(parameters, headNo).Apply(offsetX, offsetY * stageDirectionY);
        }

        public static string Format(ST_SCANNER_AXIS_TRANSFORM transform)
        {
            return $"X {FormatOnOff(transform.XFlip)} / " + $"Y {FormatOnOff(transform.YFlip)} / " + $"XY {FormatOnOff(transform.XyFlip)}";
        }

        public static (double X, double Y) ScannerOffsetToDesign(IReadOnlyDictionary<string, string> parameters, int headNo, double offsetX, double offsetY)
        {
            var transform = Read(parameters, headNo);
            if (transform.XFlip)
                offsetX = -offsetX;
            if (transform.YFlip)
                offsetY = -offsetY;
            if (transform.XyFlip)
                (offsetX, offsetY) = (offsetY, offsetX);
            return (offsetX, offsetY / CShotCoordinatePlanBuilder.ReadDirection(parameters, -1.0, "STAGE_SCAN_DIRECTION_Y"));
        }

        private static bool ReadOnOff(IReadOnlyDictionary<string, string> parameters, bool defaultValue, string key)
        {
            return parameters.TryGetValue(key, out var value) ? ParseOnOff(value, defaultValue, key) : defaultValue;
        }

        private static bool ReadOnOff(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, bool defaultValue, string key)
        {
            foreach (var setting in settings)
            {
                if (setting.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || setting.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return ParseOnOff(setting.Value, defaultValue, key);
                }
            }

            return defaultValue;
        }

        private static bool ParseOnOff(string? value, bool defaultValue, string key)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            var normalized = value.Trim().ToUpperInvariant();
            if (normalized is "ON" or "TRUE" or "1" or "YES")
            {
                return true;
            }

            if (normalized is "OFF" or "FALSE" or "0" or "NO")
            {
                return false;
            }

            throw new InvalidOperationException($"Scanner axis flip setting {key} must be ON or OFF. Current value: {value}");
        }

        private static string FormatOnOff(bool value)
        {
            return value ? "ON" : "OFF";
        }
    }

    public sealed record ST_HEAD_SHOT_POINT(
        int SequenceNo, 
        string ShotKey, 
        int HeadNo, 
        int CellNo, 
        int CellShotNo, 
        int GroupColumn, 
        int GroupRow, 
        int GroupCountX, 
        int GroupCountY, 
        double DesignX, 
        double DesignY, 
        double ScannerGx, 
        double ScannerGy, 
        double StageX, 
        double StageY, 
        double RecipeOffsetX, 
        double RecipeOffsetY, 
        double HeadDefaultOffsetX, 
        double HeadDefaultOffsetY, 
        double FirstBeamOffsetX, 
        double FirstBeamOffsetY, 
        double ReviewOffsetX, 
        double ReviewOffsetY, 
        double ShiftOffsetX, 
        double ShiftOffsetY, 
        double ShiftScannerOffsetX, 
        double ShiftScannerOffsetY, 
        double FinalScannerOffsetX, 
        double FinalScannerOffsetY, 
        double EncoderWaitPosition
    )
    {
        public double AlignOffsetX { get; init; }
        public double AlignOffsetY { get; init; }
        public double AlignScannerOffsetX { get; init; }
        public double AlignScannerOffsetY { get; init; }
    }

    public sealed record ST_SHOT_COORDINATE_PLAN(int HeadCount, int CellCount, double GlassSizeX, double GlassSizeY, double EncoderScale, IReadOnlyList<ST_HEAD_SHOT_POINT> Shots);
    public sealed record ST_DESIGN_SHOT_POINT(
        string ShotKey, 
        int CellNo, 
        int CellShotNo, 
        int GroupColumn, 
        int GroupRow, 
        int GroupCountX, 
        int GroupCountY, 
        double DesignX, 
        double DesignY, 
        double RecipeOffsetX, 
        double RecipeOffsetY, 
        double ReviewOffsetX, 
        double ReviewOffsetY, 
        double AlignOffsetX = 0, 
        double AlignOffsetY = 0
    )
    {
        public bool ReviewOffsetIsDesign { get; init; }
    }

    internal readonly record struct ST_HEAD_ASSIGNMENT_CANDIDATE(int HeadNo, double Distance);
    internal sealed record ST_PREPARED_SHOT_POINT(
        int Index, 
        ST_DESIGN_SHOT_POINT DesignShot, 
        int HeadNo, 
        double HeadCenterX, 
        ST_SCANNER_AXIS_TRANSFORM ScannerTransform, 
        double HeadDefaultOffsetX, 
        double HeadDefaultOffsetY, 
        double FirstBeamOffsetX, 
        double FirstBeamOffsetY, 
        double StageShotX, 
        double ShotStageY, 
        double DesignDistanceYFromAlignKey, 
        double BaseShotStageY, 
        double BaseDesignDistanceYFromAlignKey, 
        double EncoderScale
    );
}
