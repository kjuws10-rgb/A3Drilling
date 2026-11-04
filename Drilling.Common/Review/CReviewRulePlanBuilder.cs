namespace Drilling.Common.Review
{
    public static class CReviewRulePlanBuilder
    {
        public static ST_REVIEW_RULE_PLAN Build(ST_REVIEW_PLAN plan, ST_REVIEW_RULE_CONFIGURATION configuration)
        {
            ArgumentNullException.ThrowIfNull(plan);
            var targets = configuration.Rule switch
            {
                EN_REVIEW_CORRECTION_RULE.Simple => BuildSimple(plan, configuration.ReferenceShotKey),
                EN_REVIEW_CORRECTION_RULE.Rough => BuildRough(plan, configuration.RoughReference, configuration.Scope),
                EN_REVIEW_CORRECTION_RULE.Fine => BuildFine(plan, configuration.FineLineCount),
                EN_REVIEW_CORRECTION_RULE.Edge => BuildEdge(plan),
                _ => []
            };
            return new(configuration, targets);
        }

        private static IReadOnlyList<ST_REVIEW_RULE_TARGET> BuildSimple(ST_REVIEW_PLAN plan, string key)
        {
            var point = TryGetPoint(plan, CReviewManager.NormalizeShotKey(key));
            return point is null || point.HeadNo <= 0 ? [] : [CreateTarget(point, "SIMPLE")];
        }

        private static IReadOnlyList<ST_REVIEW_RULE_TARGET> BuildRough(ST_REVIEW_PLAN plan, EN_REVIEW_REFERENCE_LINE reference, EN_REVIEW_CORRECTION_SCOPE scope)
        {
            if (scope == EN_REVIEW_CORRECTION_SCOPE.HeadCell)
            {
                return BuildRoughByHeadCell(plan, reference);
            }

            var cells = EnumerateCells(plan).ToArray();
            var headRanges = new Dictionary<int, (double MinY, double MaxY)>();
            foreach (var cell in cells)
            {
                for (var column = 0; column < cell.Columns; column++)
                {
                    UpdateHeadRange(GetPoint(plan, cell, 0, column), headRanges);
                    if (cell.Rows > 1)
                    {
                        UpdateHeadRange(GetPoint(plan, cell, cell.Rows - 1, column), headRanges);
                    }
                }
            }

            var targetYByHead = headRanges.ToDictionary(item => item.Key, item => ResolveReferenceY(item.Value, reference));
            var selectedByHead = new Dictionary<int, ST_REVIEW_PLAN_POINT>();
            foreach (var cell in cells)
            {
                for (var row = 0; row < cell.Rows; row++)
                    for (var column = 0; column < cell.Columns; column++)
                    {
                        var point = GetPoint(plan, cell, row, column);
                        if (!IsExecutableRoughPoint(point) || !targetYByHead.TryGetValue(point.HeadNo, out var targetY))
                        {
                            continue;
                        }

                        if (!selectedByHead.TryGetValue(point.HeadNo, out var selected) || IsEarlierRoughPoint(point, selected, targetY))
                        {
                            selectedByHead[point.HeadNo] = point;
                        }
                    }
            }

            return selectedByHead.OrderBy(item => item.Key).Select(item => CreateTarget(item.Value, reference.ToString().ToUpperInvariant())).ToArray();
        }

        private static IReadOnlyList<ST_REVIEW_RULE_TARGET> BuildRoughByHeadCell(ST_REVIEW_PLAN plan, EN_REVIEW_REFERENCE_LINE reference)
        {
            // The plan point list is lazy.  Do not materialize every Shot and
            // then group/sort it for HEAD + CELL.  Rough only needs the edge rows
            // and the three rows around its calculated reference position.
            var cells = EnumerateCells(plan).ToArray();
            if (cells.Length == 0)
            {
                return[];
            }

            var headRanges = new Dictionary<int, (double MinY, double MaxY)>();
            foreach (var cell in cells)
            {
                for (var column = 0; column < cell.Columns; column++)
                {
                    UpdateHeadRange(GetPoint(plan, cell, 0, column), headRanges);
                    if (cell.Rows > 1)
                    {
                        UpdateHeadRange(GetPoint(plan, cell, cell.Rows - 1, column), headRanges);
                    }
                }
            }

            if (headRanges.Count == 0)
            {
                return[];
            }

            var selectionYByHead = ResolveRoughCellSelectionY(plan, cells, headRanges, reference);
            var selectedByHeadCell = new Dictionary<(int HeadNo, int CellNo), ST_REVIEW_PLAN_POINT>();
            foreach (var cell in cells)
            {
                if (!TryGetCellYRange(plan, cell, out var minimumCellY, out var maximumCellY))
                {
                    continue;
                }

                foreach (var headNo in headRanges.Keys)
                {
                    if (!selectionYByHead.TryGetValue(headNo, out var selectionY) || selectionY < minimumCellY - 0.000001 || selectionY > maximumCellY + 0.000001)
                    {
                        continue;
                    }

                    for (var row = 0; row < cell.Rows; row++)
                        for (var column = 0; column < cell.Columns; column++)
                        {
                            var point = GetPoint(plan, cell, row, column);
                            if (point.HeadNo != headNo || !IsExecutableRoughPoint(point))
                            {
                                continue;
                            }

                            var key = (headNo, point.CellNo);
                            if (!selectedByHeadCell.TryGetValue(key, out var selected) || IsEarlierRoughPoint(point, selected, selectionY))
                            {
                                selectedByHeadCell[key] = point;
                            }
                        }
                }
            }

            return selectedByHeadCell.Values.OrderBy(point => point.HeadNo).ThenBy(point => point.CellNo).Select(point => CreateTarget(point, $"{reference.ToString().ToUpperInvariant()} / HEAD + CELL")).ToArray();
        }

        private static Dictionary<int, double> ResolveRoughCellSelectionY(ST_REVIEW_PLAN plan, IReadOnlyList<CellSlice> cells, IReadOnlyDictionary<int, (double MinY, double MaxY)> headRanges, EN_REVIEW_REFERENCE_LINE reference)
        {
            var selectionYByHead = new Dictionary<int, double>();
            foreach (var headRange in headRanges)
            {
                var targetY = ResolveReferenceY(headRange.Value, reference);
                var bestDistance = double.MaxValue;
                var selectedY = targetY;
                foreach (var cell in cells)
                {
                    if (!TryGetCellYRange(plan, cell, out var minimumCellY, out var maximumCellY) || !CellContainsHead(plan, cell, headRange.Key))
                    {
                        continue;
                    }

                    var distance = DistanceToRange(targetY, minimumCellY, maximumCellY);
                    var candidateY = Math.Clamp(targetY, minimumCellY, maximumCellY);
                    if (distance < bestDistance - 0.000001 || Math.Abs(distance - bestDistance) <= 0.000001 && PreferRoughCellSelectionY(candidateY, selectedY, reference))
                    {
                        bestDistance = distance;
                        selectedY = candidateY;
                    }
                }

                if (bestDistance < double.MaxValue)
                {
                    selectionYByHead[headRange.Key] = selectedY;
                }
            }

            return selectionYByHead;
        }

        private static bool CellContainsHead(ST_REVIEW_PLAN plan, CellSlice cell, int headNo)
        {
            for (var column = 0; column < cell.Columns; column++)
            {
                if (GetPoint(plan, cell, 0, column).HeadNo == headNo || cell.Rows > 1 && GetPoint(plan, cell, cell.Rows - 1, column).HeadNo == headNo)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool PreferRoughCellSelectionY(double candidateY, double selectedY, EN_REVIEW_REFERENCE_LINE reference)
        {
            return reference == EN_REVIEW_REFERENCE_LINE.Last ? candidateY > selectedY : candidateY < selectedY;
        }

        private static bool TryGetCellYRange(ST_REVIEW_PLAN plan, CellSlice cell, out double minimumY, out double maximumY)
        {
            minimumY = double.MaxValue;
            maximumY = double.MinValue;
            for (var column = 0; column < cell.Columns; column++)
            {
                var firstPoint = GetPoint(plan, cell, 0, column);
                if (double.IsFinite(firstPoint.DesignY))
                {
                    minimumY = Math.Min(minimumY, firstPoint.DesignY);
                    maximumY = Math.Max(maximumY, firstPoint.DesignY);
                }

                if (cell.Rows > 1)
                {
                    var lastPoint = GetPoint(plan, cell, cell.Rows - 1, column);
                    if (double.IsFinite(lastPoint.DesignY))
                    {
                        minimumY = Math.Min(minimumY, lastPoint.DesignY);
                        maximumY = Math.Max(maximumY, lastPoint.DesignY);
                    }
                }
            }

            return minimumY <= maximumY;
        }

        private static double ResolveReferenceY((double MinY, double MaxY) range, EN_REVIEW_REFERENCE_LINE reference)
        {
            return reference switch
            {
                EN_REVIEW_REFERENCE_LINE.Last => range.MaxY,
                EN_REVIEW_REFERENCE_LINE.Center => (range.MinY + range.MaxY) / 2.0,
                _ => range.MinY
            };
        }

        private static void UpdateHeadRange(ST_REVIEW_PLAN_POINT point, IDictionary<int, (double MinY, double MaxY)> headRanges)
        {
            if (point.HeadNo <= 0 || !double.IsFinite(point.DesignX) || !double.IsFinite(point.DesignY))
            {
                return;
            }

            if (headRanges.TryGetValue(point.HeadNo, out var range))
            {
                headRanges[point.HeadNo] = (Math.Min(range.MinY, point.DesignY), Math.Max(range.MaxY, point.DesignY));
            }
            else
            {
                headRanges[point.HeadNo] = (point.DesignY, point.DesignY);
            }
        }

        private static int FindNearestRow(double firstY, double lastY, int rowCount, double targetY)
        {
            if (rowCount <= 1 || !double.IsFinite(firstY) || !double.IsFinite(lastY) || !double.IsFinite(targetY))
            {
                return 0;
            }

            var rowStepY = (lastY - firstY) / (rowCount - 1);
            if (!double.IsFinite(rowStepY) || Math.Abs(rowStepY) <= double.Epsilon)
            {
                return 0;
            }

            return Math.Clamp((int)Math.Round((targetY - firstY) / rowStepY, MidpointRounding.AwayFromZero), 0, rowCount - 1);
        }

        private static bool IsEarlierRoughPoint(ST_REVIEW_PLAN_POINT candidate, ST_REVIEW_PLAN_POINT selected, double targetY)
        {
            var candidateDistance = Math.Abs(candidate.DesignY - targetY);
            var selectedDistance = Math.Abs(selected.DesignY - targetY);
            var distanceComparison = candidateDistance.CompareTo(selectedDistance);
            if (distanceComparison != 0)
            {
                return distanceComparison < 0;
            }

            var yComparison = candidate.DesignY.CompareTo(selected.DesignY);
            if (yComparison != 0)
            {
                return yComparison < 0;
            }

            var xComparison = candidate.DesignX.CompareTo(selected.DesignX);
            if (xComparison != 0)
            {
                return xComparison < 0;
            }

            var cellComparison = candidate.CellNo.CompareTo(selected.CellNo);
            return cellComparison != 0 ? cellComparison < 0 : candidate.ShotNo < selected.ShotNo;
        }

        private static IReadOnlyList<ST_REVIEW_RULE_TARGET> BuildFine(ST_REVIEW_PLAN plan, int lineCount)
        {
            if (plan.Points is IReviewCellPointSource)
            {
                return BuildFineFromCellSource(plan, lineCount);
            }

            return plan.Points.Where(point => point.CellNo > 0 && point.HeadNo > 0 && double.IsFinite(point.DesignY)).GroupBy(point => point.CellNo).OrderBy(cell => cell.Key).SelectMany(cell =>
            {
                var rows = cell.GroupBy(point => CoordinateKey(point.DesignY)).OrderBy(row => row.Key).ToArray();
                return FineCellRows(rows, lineCount, rows[0].Key, rows[^1].Key).SelectMany((row, lineIndex) => row.OrderBy(point => point.DesignX).ThenBy(point => point.HeadNo).ThenBy(point => point.ShotNo).Select(point => CreateTarget(point, $"CELL {cell.Key:00} LINE {lineIndex + 1}")));
            }).ToArray();
        }

        private static bool IsExecutableRoughPoint(ST_REVIEW_PLAN_POINT point) => point.HeadNo > 0 && point.Use && string.IsNullOrWhiteSpace(point.ExclusionReason) && double.IsFinite(point.DesignX) && double.IsFinite(point.DesignY);
        private static IReadOnlyList<ST_REVIEW_RULE_TARGET> BuildEdge(ST_REVIEW_PLAN plan)
        {
            var targets = new List<ST_REVIEW_RULE_TARGET>();
            foreach (var cell in EnumerateCells(plan))
            {
                for (var row = 1; row <= cell.Rows; row++)
                    for (var column = 1; column <= cell.Columns; column++)
                    {
                        if (row != 1 && row != cell.Rows && column != 1 && column != cell.Columns)
                        {
                            continue;
                        }

                        var point = GetPoint(plan, cell, row - 1, column - 1);
                        if (point.HeadNo > 0)
                        {
                            targets.Add(CreateTarget(point, "EDGE"));
                        }
                    }
            }

            return targets;
        }

        private static IEnumerable<CellSlice> EnumerateCells(ST_REVIEW_PLAN plan)
        {
            if (plan.Points is IReviewCellPointSource cellSource)
            {
                for (var cellIndex = 0; cellIndex < cellSource.PatternCellCount; cellIndex++)
                {
                    var range = cellSource.GetCellRange(cellIndex);
                    if (range.PointCount <= 0)
                    {
                        continue;
                    }

                    yield return new(range.StartIndex, Math.Max(1, range.ColumnCount), Math.Max(1, range.RowCount));
                }

                yield break;
            }

            foreach (var cellPoints in plan.Points.Where(point => point.CellNo > 0).GroupBy(point => point.CellNo).OrderBy(group => group.Key).Select(group => group.OrderBy(point => point.ShotNo).ToArray()))
            {
                if (cellPoints.Length == 0)
                {
                    continue;
                }

                var first = cellPoints[0];
                var columns = Math.Max(1, first.PixelCountX);
                var rows = Math.Max(1, Math.Min(first.PixelCountY, cellPoints.Length / columns));
                var usableCount = Math.Min(cellPoints.Length, rows * columns);
                if (usableCount <= 0)
                {
                    continue;
                }

                // This overload is only used by materialized non-Recipe plans;
                // their per-cell points are already ordered by ShotNo.
                yield return new CellSlice(0, columns, rows, cellPoints[..usableCount]);
            }
        }

        private static IReadOnlyList<ST_REVIEW_RULE_TARGET> BuildFineFromCellSource(ST_REVIEW_PLAN plan, int requestedCount)
        {
            var cells = EnumerateCells(plan).ToArray();
            var cellRanges = cells.Select(cell => CreateFineCellRange(plan, cell)).Where(range => range.HasAssignedPoint).ToArray();
            if (cellRanges.Length == 0)
            {
                return[];
            }

            var selected = new List<ST_REVIEW_RULE_TARGET>();
            foreach (var range in cellRanges)
            {
                // Distribute lines independently within each Cell's Shot range.
                var count = Math.Clamp(requestedCount, 1, Math.Min(7, range.Cell.Rows));
                var targetRows = count == 1 ? new[]
                {
                    range.MinimumY + ((range.MaximumY - range.MinimumY) / 2.0)
                }

                : Enumerable.Range(0, count).Select(index => range.MinimumY + ((range.MaximumY - range.MinimumY) * index / (count - 1.0))).ToArray();
                var selectedRows = new HashSet<int>();
                for (var targetIndex = 0; targetIndex < targetRows.Length; targetIndex++)
                {
                    var nearestRow = FindNearestRow(range.FirstRowY, range.LastRowY, range.Cell.Rows, targetRows[targetIndex]);
                    var rowStep = range.Cell.Rows <= 1 ? 0 : (range.LastRowY - range.FirstRowY) / (range.Cell.Rows - 1);
                    var row = Enumerable.Range(Math.Max(0, nearestRow - 1), Math.Min(range.Cell.Rows - 1, nearestRow + 1) - Math.Max(0, nearestRow - 1) + 1).OrderBy(candidate => Math.Abs(range.FirstRowY + rowStep * candidate - targetRows[targetIndex])).ThenBy(candidate => range.FirstRowY + rowStep * candidate).First();
                    if (!selectedRows.Add(row))
                        continue;
                    for (var column = 0; column < range.Cell.Columns; column++)
                    {
                        var point = GetPoint(plan, range.Cell, row, column);
                        if (point.HeadNo > 0 && double.IsFinite(point.DesignX) && double.IsFinite(point.DesignY))
                        {
                            selected.Add(CreateTarget(point, $"CELL {point.CellNo:00} LINE {targetIndex + 1}"));
                        }
                    }
                }
            }

            return selected.DistinctBy(item => item.ShotKey, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static FineCellRange CreateFineCellRange(ST_REVIEW_PLAN plan, CellSlice cell)
        {
            var corners = new[]
            {
                GetPoint(plan, cell, 0, 0),
                GetPoint(plan, cell, 0, cell.Columns - 1),
                GetPoint(plan, cell, cell.Rows - 1, 0),
                GetPoint(plan, cell, cell.Rows - 1, cell.Columns - 1)
            };
            var assignedCorners = corners.Where(point => point.HeadNo > 0 && double.IsFinite(point.DesignY)).ToArray();
            if (assignedCorners.Length == 0)
            {
                return new FineCellRange(cell, 0.0, 0.0, 0.0, 0.0, false);
            }

            var firstRowY = corners.Take(2).Where(point => double.IsFinite(point.DesignY)).Average(point => point.DesignY);
            var lastRowY = corners.Skip(2).Where(point => double.IsFinite(point.DesignY)).Average(point => point.DesignY);
            return new FineCellRange(cell, assignedCorners.Min(point => point.DesignY), assignedCorners.Max(point => point.DesignY), firstRowY, lastRowY, true);
        }

        private static double DistanceToRange(double value, double minimum, double maximum)
        {
            if (value < minimum)
            {
                return minimum - value;
            }

            return value > maximum ? value - maximum : 0.0;
        }

        private static ST_REVIEW_PLAN_POINT GetPoint(ST_REVIEW_PLAN plan, CellSlice cell, int row, int column) => cell.Points is null ? plan.Points[cell.Start + (row * cell.Columns) + column] : cell.Points[(row * cell.Columns) + column];
        private static IReadOnlyList<IGrouping<double, ST_REVIEW_PLAN_POINT>> FineCellRows(IReadOnlyList<IGrouping<double, ST_REVIEW_PLAN_POINT>> rows, int requestedCount, double minimumY, double maximumY)
        {
            var count = Math.Clamp(requestedCount, 1, Math.Min(7, rows.Count));
            var centerY = minimumY + ((maximumY - minimumY) / 2.0);
            var targets = count == 1 ? [centerY] : Enumerable.Range(0, count).Select(index => minimumY + ((maximumY - minimumY) * index / (count - 1.0))).ToArray();
            if ((count & 1) == 1)
            {
                targets[count / 2] = centerY;
            }

            return targets.Select(target => rows.OrderBy(row => Math.Abs(row.Key - target)).ThenBy(row => row.Key).First()).DistinctBy(row => row.Key).OrderBy(row => row.Key).ToArray();
        }

        private static ST_REVIEW_RULE_TARGET CreateTarget(ST_REVIEW_PLAN_POINT point, string role)
        {
            var columns = Math.Max(1, point.PixelCountX);
            var index = Math.Max(0, point.ShotNo - 1);
            return new(point.ShotKey, point.HeadNo, point.CellNo, (index / columns) + 1, (index % columns) + 1, role);
        }

        private static ST_REVIEW_PLAN_POINT? TryGetPoint(ST_REVIEW_PLAN plan, string key)
        {
            var separator = key.IndexOf("_SHOT", StringComparison.Ordinal);
            if (separator <= 4 || plan.CellCount <= 0 || !int.TryParse(key.AsSpan(4, separator - 4), out var cell) || !int.TryParse(key.AsSpan(separator + 5), out var shot))
                return null;
            return cell >= 1 && cell <= plan.CellCount && shot >= 1 ? plan.FindPoint(cell, shot) : null;
        }

        private static double CoordinateKey(double value) => Math.Round(value, 6);
        private readonly record struct FineCellRange(CellSlice Cell, double MinimumY, double MaximumY, double FirstRowY, double LastRowY, bool HasAssignedPoint);
        private readonly record struct CellSlice(int Start, int Columns, int Rows, IReadOnlyList<ST_REVIEW_PLAN_POINT>? Points = null);
#region 검사점 선택
        public static IReadOnlyCollection<string> SelectEdgeShotKeys(ST_REVIEW_PLAN plan)
        {
            string SelectShotKey(global::Drilling.Common.Review.ST_REVIEW_PLAN_POINT point)
            {
                return point.ShotKey;
            }

            return plan.Points.Where(IsCellEdgeShot).Select(SelectShotKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static IReadOnlyCollection<string> SelectCenterShotKeys(ST_REVIEW_PLAN plan)
        {
            string SelectShotKey(global::Drilling.Common.Review.ST_REVIEW_PLAN_POINT point)
            {
                return point.ShotKey;
            }

            return plan.Points.Where(IsCellCenterShot).Select(SelectShotKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static bool IsCellEdgeShot(ST_REVIEW_PLAN_POINT point)
        {
            var columnCount = Math.Max(1, point.PixelCountX);
            var rowCount = Math.Max(1, point.PixelCountY);
            var zeroBasedShotNo = Math.Max(0, point.ShotNo - 1);
            var column = zeroBasedShotNo % columnCount;
            var row = zeroBasedShotNo / columnCount;
            return column == 0 || column == columnCount - 1 || row == 0 || row == rowCount - 1;
        }

        private static bool IsCellCenterShot(ST_REVIEW_PLAN_POINT point)
        {
            return !IsCellEdgeShot(point);
        }
#endregion
    }

    public enum EN_REVIEW_CORRECTION_RULE
    {
        Simple,
        Rough,
        Fine,
        Edge
    }

    public enum EN_REVIEW_CORRECTION_SCOPE
    {
        Head,
        HeadCell
    }

    public enum EN_REVIEW_REFERENCE_LINE
    {
        First,
        Center,
        Last
    }

    public sealed record ST_REVIEW_RULE_CONFIGURATION(
        EN_REVIEW_CORRECTION_RULE Rule, 
        EN_REVIEW_CORRECTION_SCOPE Scope, 
        int BeamNo, 
        string ReferenceShotKey, 
        EN_REVIEW_REFERENCE_LINE RoughReference, 
        int FineLineCount
    );
    public sealed record ST_REVIEW_RULE_TARGET(string ShotKey, int HeadNo, int CellNo, int RowNo, int ColumnNo, string Role);
    public sealed record ST_REVIEW_RULE_PLAN(ST_REVIEW_RULE_CONFIGURATION Configuration, IReadOnlyList<ST_REVIEW_RULE_TARGET> Targets)
    {
        public IReadOnlyCollection<string> ShotKeys { get; } = Targets.Select(target => target.ShotKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
