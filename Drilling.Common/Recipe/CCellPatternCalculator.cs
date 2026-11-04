namespace Drilling.Common.Recipe
{
    using System.Globalization;

    public static class CCellPatternCalculator
    {
        public const int MinCellCount = 1;
        public const int MaxTotalShotGroupCount = 500_000;
        public const int MaxGroupCountPerCell = MaxTotalShotGroupCount;
        // Review preview helpers are deliberately derived from the existing
        // layout calculation so they cannot change Recipe process geometry.
        public const int MaximumPreviewPointsPerCell = 6_000;
        public const double MinimumPreviewPointSize = 2.0;
        public const double MaximumPreviewPointSize = 8.0;
        public static bool IsSupportedSplitBeamCount(int splitBeamCount)
        {
            return splitBeamCount is 1 or 4;
        }

        public static double CalculatePreviewPointSize(ST_CELL_PATTERN_LAYOUT layout, double scale) => Math.Clamp(layout.GroupPitchMillimeters * scale, MinimumPreviewPointSize, MaximumPreviewPointSize);
        public static int CalculatePreviewSampleStep(ST_CELL_PATTERN_LAYOUT layout, double scale)
        {
            var total = (long)layout.GroupCountX * layout.GroupCountY;
            var countStep = total <= MaximumPreviewPointsPerCell ? 1 : (int)Math.Ceiling(Math.Sqrt(total / (double)MaximumPreviewPointsPerCell));
            var pixelStep = layout.GroupPitchMillimeters * scale >= 1.0 ? 1 : (int)Math.Ceiling(1.0 / Math.Max(0.000001, layout.GroupPitchMillimeters * scale));
            return Math.Max(1, Math.Max(countStep, pixelStep));
        }

        public static double CalculatePreviewBeamHalfSpan(ST_CELL_PATTERN_LAYOUT layout, int splitBeamCount) => Math.Max(0.0, layout.BeamHalfSpanMillimeters);
        public static bool TryParseCellCount(string? text, out int cellCount, out string validationMessage)
        {
            return TryParseCellCount(text, MinCellCount, null, out cellCount, out validationMessage);
        }

        public static bool TryParseCellCount(string? text, int minimum, int? maximum, out int cellCount, out string validationMessage)
        {
            var normalized = text?.Trim() ?? "";
            cellCount = 0;
            validationMessage = "";
            var effectiveMinimum = Math.Max(MinCellCount, minimum);
            var effectiveMaximum = maximum is { } value && value >= effectiveMinimum ? value : (int? )null;
            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            {
                cellCount = intValue;
            }
            else if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue) && double.IsFinite(doubleValue) && doubleValue >= int.MinValue && doubleValue <= int.MaxValue && doubleValue == Math.Truncate(doubleValue))
            {
                cellCount = checked((int)doubleValue);
            }
            else
            {
                validationMessage = effectiveMaximum.HasValue ? $"MAX_CELL_NUMBER must be an integer between {effectiveMinimum} and {effectiveMaximum.Value}. Current value: {FormatCurrentValue(normalized)}" : $"MAX_CELL_NUMBER must be an integer greater than or equal to {effectiveMinimum}. Current value: {FormatCurrentValue(normalized)}";
                return false;
            }

            if (cellCount < effectiveMinimum || effectiveMaximum.HasValue && cellCount > effectiveMaximum.Value)
            {
                validationMessage = effectiveMaximum.HasValue ? $"MAX_CELL_NUMBER must be between {effectiveMinimum} and {effectiveMaximum.Value}. Current value: {cellCount}" : $"MAX_CELL_NUMBER must be greater than or equal to {effectiveMinimum}. Current value: {cellCount}";
                return false;
            }

            return true;
        }

        public static ST_CELL_PATTERN_LAYOUT Calculate(ST_CELL_PATTERN_DEFINITION input) => Calculate(input, includeShotCenters: true);
        public static ST_CELL_PATTERN_LAYOUT Calculate(ST_CELL_PATTERN_DEFINITION input, bool includeShotCenters)
        {
            if (input.CellNo <= 0)
            {
                return Invalid("Cell number must be greater than zero.");
            }

            if (!double.IsFinite(input.FirstTargetX) || !double.IsFinite(input.FirstTargetY) || !double.IsFinite(input.RotationDegrees) || !double.IsFinite(input.PixelSizeMillimeters) || !double.IsFinite(input.OriginX) || !double.IsFinite(input.OriginY))
            {
                return Invalid($"Cell{input.CellNo}: Shot group coordinates must be finite values.");
            }

            if (input.PixelSizeMillimeters <= 0.0)
            {
                return Invalid($"Cell{input.CellNo}: Pixel Size must be greater than zero.");
            }

            if (input.PixelCountX <= 0 || input.PixelCountY <= 0)
            {
                return Invalid($"Cell{input.CellNo}: Pixel Count X/Y must be greater than zero.");
            }

            if (input.ProcessPitch <= 0)
            {
                return Invalid($"Cell{input.CellNo}: Process Pitch must be greater than zero.");
            }

            if (!IsSupportedSplitBeamCount(input.SplitBeamCount))
            {
                return Invalid($"Cell{input.CellNo}: DOE Beam Count must be either 1 or 4.");
            }

            var targetCountX = input.PixelCountX / input.ProcessPitch;
            var targetCountY = input.PixelCountY / input.ProcessPitch;
            var groupCountX = targetCountX;
            var groupCountY = targetCountY;
            var processedTargetCountX = targetCountX;
            var processedTargetCountY = targetCountY;
            var processedPixelCountX = targetCountX * input.ProcessPitch;
            var processedPixelCountY = targetCountY * input.ProcessPitch;
            var excludedTargetCountX = input.PixelCountX - processedPixelCountX;
            var excludedTargetCountY = input.PixelCountY - processedPixelCountY;
            if (groupCountX <= 0 || groupCountY <= 0)
            {
                return Invalid(
                    $"Cell{input.CellNo}: Pixel Count {input.PixelCountX} x {input.PixelCountY} does not contain a process pitch interval {input.ProcessPitch}.", 
                    targetCountX, 
                    targetCountY, 
                    groupCountX, 
                    groupCountY, 
                    processedTargetCountX, 
                    processedTargetCountY, 
                    excludedTargetCountX, 
                    excludedTargetCountY
                );
            }

            var groupCount = (long)groupCountX * groupCountY;
            if (groupCount > MaxGroupCountPerCell)
            {
                return Invalid(
                    $"Cell{input.CellNo}: Shot Group Count {groupCount:N0} exceeds the limit " + $"{MaxGroupCountPerCell:N0}.", 
                    targetCountX, 
                    targetCountY, 
                    groupCountX, 
                    groupCountY, 
                    processedTargetCountX, 
                    processedTargetCountY, 
                    excludedTargetCountX, 
                    excludedTargetCountY
                );
            }

            var targetPitchMillimeters = input.PixelSizeMillimeters * input.ProcessPitch;
            var groupPitchMillimeters = targetPitchMillimeters;
            var beamPitchMillimeters = targetPitchMillimeters / input.SplitBeamCount;
            var beamHalfSpanMillimeters = beamPitchMillimeters * (input.SplitBeamCount - 1) / 2.0;
            var radians = input.RotationDegrees * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            if (!double.IsFinite(targetPitchMillimeters) || !double.IsFinite(groupPitchMillimeters) || !double.IsFinite(beamPitchMillimeters) || !double.IsFinite(beamHalfSpanMillimeters) || !double.IsFinite(radians) || !double.IsFinite(cos) || !double.IsFinite(sin))
            {
                return Invalid(
                    $"Cell{input.CellNo}: Shot group pitch or rotation calculation produced a non-finite value.", 
                    targetCountX, 
                    targetCountY, 
                    groupCountX, 
                    groupCountY, 
                    processedTargetCountX, 
                    processedTargetCountY, 
                    excludedTargetCountX, 
                    excludedTargetCountY
                );
            }

            if (!includeShotCenters)
            {
                return new ST_CELL_PATTERN_LAYOUT([], targetCountX, targetCountY, groupCountX, groupCountY, targetPitchMillimeters, groupPitchMillimeters, "")
                {
                    ProcessedTargetCountX = processedTargetCountX,
                    ProcessedTargetCountY = processedTargetCountY,
                    ExcludedTargetCountX = excludedTargetCountX,
                    ExcludedTargetCountY = excludedTargetCountY,
                    PixelCountX = input.PixelCountX,
                    PixelCountY = input.PixelCountY,
                    ProcessedPixelCountX = processedPixelCountX,
                    ProcessedPixelCountY = processedPixelCountY,
                    BeamPitchMillimeters = beamPitchMillimeters,
                    BeamHalfSpanMillimeters = beamHalfSpanMillimeters
                };
            }

            var shotCenters = includeShotCenters ? new List<ST_SHOT_GROUP_CENTER>((int)groupCount) : null;
            var cellShotNo = 1;
            for (var row = 0; row < groupCountY; row++)
            {
                for (var column = 0; column < groupCountX; column++)
                {
                    var firstTargetColumn = column * input.ProcessPitch;
                    var firstTargetRow = row * input.ProcessPitch;
                    var localX = (column * targetPitchMillimeters) + beamHalfSpanMillimeters;
                    var localY = (row * targetPitchMillimeters) + beamHalfSpanMillimeters;
                    var x = input.OriginX + input.FirstTargetX + (localX * cos) - (localY * sin);
                    var y = input.OriginY + input.FirstTargetY + (localX * sin) + (localY * cos);
                    if (!double.IsFinite(localX) || !double.IsFinite(localY) || !double.IsFinite(x) || !double.IsFinite(y))
                    {
                        return Invalid(
                            $"Cell{input.CellNo}: Shot group coordinate calculation produced a non-finite value " + $"at column {column}, row {row}.", 
                            targetCountX, 
                            targetCountY, 
                            groupCountX, 
                            groupCountY, 
                            processedTargetCountX, 
                            processedTargetCountY, 
                            excludedTargetCountX, 
                            excludedTargetCountY
                        );
                    }

                    shotCenters?.Add(new ST_SHOT_GROUP_CENTER(input.CellNo, cellShotNo++, column, row, firstTargetColumn, firstTargetRow, x, y));
                }
            }

            return new ST_CELL_PATTERN_LAYOUT(shotCenters ?? [], targetCountX, targetCountY, groupCountX, groupCountY, targetPitchMillimeters, groupPitchMillimeters, "")
            {
                ProcessedTargetCountX = processedTargetCountX,
                ProcessedTargetCountY = processedTargetCountY,
                ExcludedTargetCountX = excludedTargetCountX,
                ExcludedTargetCountY = excludedTargetCountY,
                PixelCountX = input.PixelCountX,
                PixelCountY = input.PixelCountY,
                ProcessedPixelCountX = processedPixelCountX,
                ProcessedPixelCountY = processedPixelCountY,
                BeamPitchMillimeters = beamPitchMillimeters,
                BeamHalfSpanMillimeters = beamHalfSpanMillimeters
            };
        }

        public static ST_SHOT_GROUP_CENTER CalculateShotCenter(ST_CELL_PATTERN_DEFINITION input, int column, int row, ST_CELL_PATTERN_LAYOUT layout)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(layout);
            if (column < 0 || column >= layout.GroupCountX || row < 0 || row >= layout.GroupCountY)
            {
                throw new ArgumentOutOfRangeException(nameof(column), "Shot group row or column is outside the layout.");
            }

            var radians = input.RotationDegrees * Math.PI / 180.0;
            var localX = (column * layout.TargetPitchMillimeters) + layout.BeamHalfSpanMillimeters;
            var localY = (row * layout.TargetPitchMillimeters) + layout.BeamHalfSpanMillimeters;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            return new ST_SHOT_GROUP_CENTER(
                input.CellNo, 
                (row * layout.GroupCountX) + column + 1, 
                column, 
                row, 
                column * input.ProcessPitch, 
                row * input.ProcessPitch, 
                input.OriginX + input.FirstTargetX + (localX * cos) - (localY * sin), 
                input.OriginY + input.FirstTargetY + (localX * sin) + (localY * cos)
            );
        }

        private static ST_CELL_PATTERN_LAYOUT Invalid(
            string message, 
            int targetCountX = 0, 
            int targetCountY = 0, 
            int groupCountX = 0, 
            int groupCountY = 0, 
            int processedTargetCountX = 0, 
            int processedTargetCountY = 0, 
            int excludedTargetCountX = 0, 
            int excludedTargetCountY = 0
        )
        {
            return new ST_CELL_PATTERN_LAYOUT([], targetCountX, targetCountY, groupCountX, groupCountY, 0.0, 0.0, message)
            {
                ProcessedTargetCountX = processedTargetCountX,
                ProcessedTargetCountY = processedTargetCountY,
                ExcludedTargetCountX = excludedTargetCountX,
                ExcludedTargetCountY = excludedTargetCountY
            };
        }

        private static string FormatCurrentValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
        }
    }

    public sealed record ST_CELL_PATTERN_DEFINITION(
        int CellNo, 
        double FirstTargetX, 
        double FirstTargetY, 
        double RotationDegrees, 
        double PixelSizeMillimeters, 
        int PixelCountX, 
        int PixelCountY, 
        int ProcessPitch, 
        int SplitBeamCount, 
        double OriginX = 0.0, 
        double OriginY = 0.0
    );
    public sealed record ST_SHOT_GROUP_CENTER(int CellNo, int CellShotNo, int GroupColumn, int GroupRow, int FirstTargetColumn, int FirstTargetRow, double DesignX, double DesignY);
    public sealed record ST_CELL_PATTERN_LAYOUT(
        IReadOnlyList<ST_SHOT_GROUP_CENTER> ShotCenters, 
        int TargetCountX, 
        int TargetCountY, 
        int GroupCountX, 
        int GroupCountY, 
        double TargetPitchMillimeters, 
        double GroupPitchMillimeters, 
        string ValidationMessage
    )
    {
        public bool IsValid
        {
            get
            {
                return string.IsNullOrWhiteSpace(ValidationMessage);
            }
        }

        public int ProcessedTargetCountX { get; init; }
        public int ProcessedTargetCountY { get; init; }
        public int ExcludedTargetCountX { get; init; }
        public int ExcludedTargetCountY { get; init; }
        public int PixelCountX { get; init; }
        public int PixelCountY { get; init; }
        public int ProcessedPixelCountX { get; init; }
        public int ProcessedPixelCountY { get; init; }
        public double BeamPitchMillimeters { get; init; }
        public double BeamHalfSpanMillimeters { get; init; }

        public bool HasExcludedTargets
        {
            get
            {
                return ExcludedTargetCountX > 0 || ExcludedTargetCountY > 0;
            }
        }
    }

    public sealed record ST_RECIPE_CELL_PATTERN_ENTRY(ST_CELL_PATTERN_DEFINITION Definition, ST_CELL_PATTERN_LAYOUT Layout, int StartIndex, int Count);
    /// <summary>
    /// Lazily maps Review points to the current Recipe cell layout.  It does not
    /// persist or alter Recipe data; it only prevents large Review plans from
    /// materializing duplicate point lists.
    /// </summary>
    public sealed class CRecipeCellPatternCatalog
    {
        private readonly IReadOnlyList<ST_RECIPE_CELL_PATTERN_ENTRY> _cells;
        private CRecipeCellPatternCatalog(IReadOnlyList<ST_RECIPE_CELL_PATTERN_ENTRY> cells, int count)
        {
            _cells = cells;
            Count = count;
        }

        public int Count { get; }
        public int CellCount => _cells.Count;
        public IReadOnlyList<ST_RECIPE_CELL_PATTERN_ENTRY> Cells => _cells;

        public static CRecipeCellPatternCatalog Create(IEnumerable<ST_CELL_PATTERN_DEFINITION> definitions)
        {
            ArgumentNullException.ThrowIfNull(definitions);
            var cells = new List<ST_RECIPE_CELL_PATTERN_ENTRY>();
            var totalCount = 0;
            foreach (var definition in definitions.OrderBy(item => item.CellNo))
            {
                var layout = CCellPatternCalculator.Calculate(definition);
                if (!layout.IsValid)
                {
                    throw new InvalidOperationException(layout.ValidationMessage);
                }

                var cellCount = checked(layout.GroupCountX * layout.GroupCountY);
                if (cellCount > int.MaxValue - totalCount)
                {
                    throw new InvalidOperationException($"Recipe Cell point count {(long)totalCount + cellCount:N0} exceeds the supported list size.");
                }

                cells.Add(new ST_RECIPE_CELL_PATTERN_ENTRY(definition, layout, totalCount, cellCount));
                totalCount += cellCount;
            }

            return new CRecipeCellPatternCatalog(cells, totalCount);
        }

        public ST_RECIPE_CELL_PATTERN_ENTRY GetCell(int cellNo) => _cells.FirstOrDefault(cell => cell.Definition.CellNo == cellNo) ?? throw new ArgumentOutOfRangeException(nameof(cellNo));
        public ST_SHOT_GROUP_CENTER GetShotCenter(int index)
        {
            var(cell, pointIndex) = Resolve(index);
            return GetShotCenter(cell, pointIndex);
        }

        public static ST_SHOT_GROUP_CENTER GetShotCenter(ST_RECIPE_CELL_PATTERN_ENTRY cell, int pointIndex)
        {
            if ((uint)pointIndex >= (uint)cell.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(pointIndex));
            }

            return CCellPatternCalculator.CalculateShotCenter(cell.Definition, pointIndex % cell.Layout.GroupCountX, pointIndex / cell.Layout.GroupCountX, cell.Layout);
        }

        public (ST_RECIPE_CELL_PATTERN_ENTRY Cell, int PointIndex) Resolve(int index)
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var low = 0;
            var high = _cells.Count - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var cell = _cells[middle];
                if (index < cell.StartIndex)
                {
                    high = middle - 1;
                }
                else if (index >= cell.StartIndex + cell.Count)
                {
                    low = middle + 1;
                }
                else
                {
                    return (cell, index - cell.StartIndex);
                }
            }

            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public readonly record struct ST_RECIPE_MODEL_PIXEL_COUNT(int PixelCountX, int PixelCountY);
    public readonly record struct ST_RECIPE_MODEL_PATTERN(int PixelCountX, int PixelCountY, int ProcessPitch, int Chess, int ChessType);
    public static class CRecipeModelPixelCountReader
    {
        public const int MaximumCellModelTypeCount = 5;
        public static int ReadModelTypeCount(IReadOnlyDictionary<string, string> parameters)
        {
            return Math.Clamp(ReadInt(parameters, 1, "CELL_MODEL_TYPE_COUNT"), 1, MaximumCellModelTypeCount);
        }

        public static int ReadCellModelType(IReadOnlyDictionary<string, string> parameters, int cellNo, int modelTypeCount = 0)
        {
            var maximum = modelTypeCount > 0 ? Math.Min(modelTypeCount, MaximumCellModelTypeCount) : ReadModelTypeCount(parameters);
            return Math.Clamp(ReadInt(parameters, 1, $"CELL{cellNo}_MODEL_TYPE"), 1, Math.Max(1, maximum));
        }

        public static ST_RECIPE_MODEL_PIXEL_COUNT ReadForCell(IReadOnlyDictionary<string, string> parameters, int cellNo)
        {
            return ReadForModel(parameters, ReadCellModelType(parameters, cellNo));
        }

        public static ST_RECIPE_MODEL_PATTERN ReadPatternForCell(IReadOnlyDictionary<string, string> parameters, int cellNo)
        {
            return ReadPatternForModel(parameters, ReadCellModelType(parameters, cellNo));
        }

        public static ST_RECIPE_MODEL_PIXEL_COUNT ReadForModel(IReadOnlyDictionary<string, string> parameters, int modelType)
        {
            var prefix = $"MODEL{Math.Clamp(modelType, 1, MaximumCellModelTypeCount)}_";
            return new ST_RECIPE_MODEL_PIXEL_COUNT(ReadInt(parameters, 0, $"{prefix}NUM_OF_PIXEL_X"), ReadInt(parameters, 0, $"{prefix}NUM_OF_PIXEL_Y"));
        }

        public static ST_RECIPE_MODEL_PATTERN ReadPatternForModel(IReadOnlyDictionary<string, string> parameters, int modelType)
        {
            var prefix = $"MODEL{Math.Clamp(modelType, 1, MaximumCellModelTypeCount)}_";
            return new ST_RECIPE_MODEL_PATTERN(
                ReadInt(parameters, 0, $"{prefix}NUM_OF_PIXEL_X"), 
                ReadInt(parameters, 0, $"{prefix}NUM_OF_PIXEL_Y"), 
                ReadInt(parameters, 1, $"{prefix}PITCH"), 
                Math.Clamp(ReadInt(parameters, 1, $"{prefix}CHESS"), 1, 4), 
                Math.Clamp(ReadInt(parameters, 1, $"{prefix}CHESS_TYPE"), 0, 1)
            );
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
    }
}
