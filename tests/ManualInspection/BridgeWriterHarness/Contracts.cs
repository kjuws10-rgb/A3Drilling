namespace Drilling.Common.Managers {
    public enum EN_RECIPE_DATA_TYPE
    {
        String,
        Int,
        Double,
        Bool
    }

    public sealed record ST_RECIPE_PARAMETER(
        string Name,
        string Value,
        string Unit,
        string Range,
        string DefaultValue,
        string Tab = "",
        string Group = "",
        string Key = "",
        string Description = "",
        bool Show = true,
        bool Use = true,
        int DisplayOrder = 0,
        EN_RECIPE_DATA_TYPE DataType = EN_RECIPE_DATA_TYPE.String,
        double ChangeLimit = 0.0,
        double Min = 0.0,
        double Max = 0.0,
        IReadOnlyDictionary<string, string>? Extra = null
    );
    public sealed record ST_RECIPE_HISTORY(
        DateTimeOffset ChangedAt,
        string ItemName,
        string OldValue,
        string NewValue,
        string OperatorId,
        string RecipeName = "",
        string Action = "",
        string Tab = "",
        string Group = ""
    );
    public sealed record ST_RECIPE_DEFINITION(string Id, string Name, IReadOnlyList<ST_RECIPE_PARAMETER> Parameters, IReadOnlyList<ST_RECIPE_HISTORY> History);
    public enum EN_SETTING_TAB
    {
        Option,
        Interface,
        Io,
        Motor,
        Alarm
    }

    public sealed record ST_SYSTEM_PARAMETER(
        EN_SETTING_TAB Section,
        string Name,
        string Value,
        string Unit,
        string Description,
        string Group = "",
        string Key = "",
        string DefaultValue = "",
        EN_RECIPE_DATA_TYPE DataType = EN_RECIPE_DATA_TYPE.String,
        double Min = 0.0,
        double Max = 0.0,
        bool Show = true,
        bool Use = true,
        int DisplayOrder = 0,
        IReadOnlyDictionary<string, string>? Extra = null
    );
}
namespace Drilling.Common.Review {
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

    public sealed record ST_REVIEW_OFFSET_ROLLBACK_RESULT(
        string RecipeId,
        string RemovedFileName,
        DateTimeOffset RemovedSavedAt,
        string ArchivedFilePath,
        ST_REVIEW_OFFSET_FILE_DATA Current);

    public sealed record ST_REVIEW_OFFSET_SOURCE_APPLICATION(
        string SourceResultFile,
        string OffsetFile,
        DateTimeOffset AppliedAt,
        string HistoryFilePath);

    public sealed class CReviewOffsetDuplicateSourceException(
        ST_REVIEW_OFFSET_SOURCE_APPLICATION existingApplication)
        : InvalidOperationException(
            $"{existingApplication.AppliedAt:yyyy-MM-dd} 적용 이력에 동일한 Review Result가 있습니다: "
            + existingApplication.SourceResultFile)
    {
        public ST_REVIEW_OFFSET_SOURCE_APPLICATION ExistingApplication { get; } = existingApplication;
    }

    public abstract class CReviewOffsetFileBase
    {
        public virtual Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNewIfUnchanged(
            string recipeId, string expectedLatestFilePath, IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,
            string sourceResultFile, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("이 저장소는 Manual Inspection의 활성 Revision 확인 저장을 지원하지 않습니다.");

        public abstract string RootPath { get; }

        public abstract Task<ST_REVIEW_OFFSET_FILE_DATA> LoadLatest(string recipeId, CancellationToken cancellationToken = default);
        public abstract Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNew(string recipeId, IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows, CancellationToken cancellationToken = default);
        public abstract Task<ST_REVIEW_OFFSET_SOURCE_APPLICATION?> FindSourceApplication(
            string recipeId,
            string sourceResultFile,
            CancellationToken cancellationToken = default);
        public abstract Task<ST_REVIEW_OFFSET_FILE_DATA> SaveNewFromSource(
            string recipeId,
            IReadOnlyList<ST_REVIEW_OFFSET_ROW> rows,
            string sourceResultFile,
            bool allowDuplicate,
            CancellationToken cancellationToken = default);
        public abstract Task<ST_REVIEW_OFFSET_ROLLBACK_RESULT> RollbackLatest(
            string recipeId,
            string expectedLatestFilePath,
            CancellationToken cancellationToken = default);
    }

}
namespace Drilling.Common.Correction {
    public sealed class CAlignInterpolation
    {
        public const string AlgorithmName = "Lagrange";
        private readonly double[] _normalizedY;
        private readonly double[] _weights;
        public IReadOnlyList<ST_ALIGN_INTERPOLATION_ROW> Rows { get; }

        public CAlignInterpolation(IReadOnlyList<ST_ALIGN_INTERPOLATION_KEY> keys, IReadOnlyCollection<string> selectedDkNames)
        {
            var byName = keys.ToDictionary(key => key.Name, StringComparer.Ordinal);
            ST_ALIGN_INTERPOLATION_KEY Find(string name) => byName.TryGetValue(name, out var key) ? key : throw new InvalidOperationException($"{name} 키 위치가 없습니다.");
            var top = new ST_ALIGN_INTERPOLATION_ROW(Find("AK1"), Find("AK3"));
            var bottom = new ST_ALIGN_INTERPOLATION_ROW(Find("AK2"), Find("AK4"));
            ValidateRow(top);
            ValidateRow(bottom);
            if (bottom.Y - top.Y <= 1e-9)
                throw new InvalidOperationException("위쪽 AK와 아래쪽 AK의 Y 위치가 서로 달라야 합니다.");
            var selected = selectedDkNames.Distinct(StringComparer.Ordinal).Select(Find).ToArray();
            var rows = new List<ST_ALIGN_INTERPOLATION_ROW>
            {
                top
            };
            foreach (var group in selected.GroupBy(key => key.Y).OrderBy(group => group.Key))
            {
                var pair = group.OrderBy(key => key.X).ToArray();
                if (pair.Length != 2)
                    throw new InvalidOperationException($"Y={group.Key:0.###} 행에는 DK가 {pair.Length}개 있습니다. 좌우 DK 두 개로 구성된 행을 선택해 주세요.");
                var row = new ST_ALIGN_INTERPOLATION_ROW(pair[0], pair[1]);
                ValidateRow(row);
                if (row.Y <= top.Y || row.Y >= bottom.Y)
                    throw new InvalidOperationException($"{pair[0].Name} / {pair[1].Name}의 Y 위치가 위·아래 AK 사이에 있어야 합니다.");
                rows.Add(row);
            }

            rows.Add(bottom);
            if (rows.Zip(rows.Skip(1), (first, second) => second.Y - first.Y).Any(span => span <= 1e-9))
                throw new InvalidOperationException("선택한 키 행의 Y 간격이 너무 작습니다.");
            Rows = rows.ToArray();
            _normalizedY = rows.Select(row => (row.Y - top.Y) / (bottom.Y - top.Y)).ToArray();
            _weights = _normalizedY.Select((value, index) => 1.0 / _normalizedY.Where((_, other) => other != index).Aggregate(1.0, (product, other) => product * (value - other))).ToArray();
            if (_weights.Any(weight => !double.IsFinite(weight)))
                throw new InvalidOperationException("선택한 키의 Y 간격으로 라그랑쥬 보간을 계산할 수 없습니다.");
        }

        public bool Contains(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || y < Rows[0].Y || y > Rows[^1].Y)
                return false;
            var(first, second, fraction) = FindInterval(y);
            var left = first.Left.X + fraction * (second.Left.X - first.Left.X);
            var right = first.Right.X + fraction * (second.Right.X - first.Right.X);
            return x >= left - 1e-9 && x <= right + 1e-9;
        }

        public void ValidateMeasurements()
        {
            foreach (var row in Rows)
            {
                ReadCorrection(row.Left);
                ReadCorrection(row.Right);
            }
        }

        public ST_ALIGN_CORRECTION Evaluate(double x, double y)
        {
            if (!Contains(x, y))
                throw new InvalidOperationException("검증 위치가 선택한 키로 구성된 보간 범위를 벗어납니다.");
            ValidateMeasurements();
            for (var index = 0; index < Rows.Count; index++)
                if (y == Rows[index].Y)
                    return EvaluateRow(Rows[index], x);
            var normalized = (y - Rows[0].Y) / (Rows[^1].Y - Rows[0].Y);
            double denominator = 0, numeratorX = 0, numeratorY = 0;
            for (var index = 0; index < Rows.Count; index++)
            {
                var term = _weights[index] / (normalized - _normalizedY[index]);
                var value = EvaluateRow(Rows[index], x);
                denominator += term;
                numeratorX += term * value.X;
                numeratorY += term * value.Y;
            }

            return Finite(new ST_ALIGN_CORRECTION(numeratorX / denominator, numeratorY / denominator));
        }

        private (ST_ALIGN_INTERPOLATION_ROW First, ST_ALIGN_INTERPOLATION_ROW Second, double Fraction) FindInterval(double y)
        {
            for (var index = 1; index < Rows.Count; index++)
                if (y <= Rows[index].Y)
                    return (Rows[index - 1], Rows[index], (y - Rows[index - 1].Y) / (Rows[index].Y - Rows[index - 1].Y));
            throw new InvalidOperationException("검증 위치의 Y 구간을 찾을 수 없습니다.");
        }

        private static void ValidateRow(ST_ALIGN_INTERPOLATION_ROW row)
        {
            if (!double.IsFinite(row.Y) || !double.IsFinite(row.Right.Y) || row.Y != row.Right.Y || !double.IsFinite(row.Left.X) || !double.IsFinite(row.Right.X) || row.Right.X - row.Left.X <= 1e-9)
                throw new InvalidOperationException($"{row.Left.Name} / {row.Right.Name}는 같은 Y에서 서로 다른 X 위치에 있어야 합니다.");
        }

        private static ST_ALIGN_CORRECTION ReadCorrection(ST_ALIGN_INTERPOLATION_KEY key) => key.CorrectionX is { } x && key.CorrectionY is { } y && double.IsFinite(x) && double.IsFinite(y) ? new(x, y) : throw new InvalidOperationException($"{key.Name}의 Base / LastLoad X/Y 값이 없습니다. Load 후 다시 확인해 주세요.");
        private static ST_ALIGN_CORRECTION EvaluateRow(ST_ALIGN_INTERPOLATION_ROW row, double x) => Blend(ReadCorrection(row.Left), ReadCorrection(row.Right), (x - row.Left.X) / (row.Right.X - row.Left.X));
        private static ST_ALIGN_CORRECTION Blend(ST_ALIGN_CORRECTION first, ST_ALIGN_CORRECTION second, double fraction) => Finite(new(first.X + fraction * (second.X - first.X), first.Y + fraction * (second.Y - first.Y)));
        private static ST_ALIGN_CORRECTION Finite(ST_ALIGN_CORRECTION value) => double.IsFinite(value.X) && double.IsFinite(value.Y) ? value : throw new InvalidOperationException("보간 계산 결과가 유한한 값이 아닙니다.");
    }

    public sealed record ST_ALIGN_INTERPOLATION_KEY(string Name, double X, double Y, double? CorrectionX = null, double? CorrectionY = null);
    public sealed record ST_ALIGN_INTERPOLATION_ROW(ST_ALIGN_INTERPOLATION_KEY Left, ST_ALIGN_INTERPOLATION_KEY Right)
    {
        public double Y => Left.Y;
    }

    public readonly record struct ST_ALIGN_CORRECTION(double X, double Y);
}

namespace Drilling.Common.Review {
    public enum EN_VISION_FLIP_TYPE
    {
        Normal = 0,
        XFlip = 1,
        YFlip = 2,
        XyFlip = 4
    }

    public readonly record struct ST_REVIEW_COORDINATE_OFFSET(double X, double Y);
    public static class CReviewManager {
        public static ST_REVIEW_COORDINATE_OFFSET VisionErrorToStageOffset(double visionErrorX, double visionErrorY, EN_VISION_FLIP_TYPE flipType)
        {
            // @좌표 [옵셋적용] Vision→Stage - XY 교환을 먼저 수행한 뒤 X/Y Flip을 순서대로 적용한다.
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
    }
}
