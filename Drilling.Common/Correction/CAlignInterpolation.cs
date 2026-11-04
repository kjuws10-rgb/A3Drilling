namespace Drilling.Common.Correction
{
    using Drilling.Common.Recipe;
    using System.Globalization;

    // Interpolate each horizontal row linearly in X, then use global Lagrange interpolation in Y.
    // This also handles rows whose left/right X coordinates do not match the AK columns.
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
    public sealed record ST_ALIGN_OFFSET_KEY_LOG(
        string Name, 
        bool Included, 
        double DesignX, 
        double DesignY, 
        double? BaseX, 
        double? BaseY, 
        double? LastLoadX, 
        double? LastLoadY, 
        double? OffsetX, 
        double? OffsetY
    );
    public sealed record ST_ALIGN_OFFSET_LOG(
        string RecipeId, 
        string RecipeName, 
        bool IsInit, 
        DateTimeOffset ChangedAt, 
        DateTimeOffset? LastLoadedAt, 
        bool IsSimulation, 
        IReadOnlyList<ST_ALIGN_OFFSET_KEY_LOG> Keys, 
        IReadOnlyList<ST_ALIGN_SHOT_OFFSET> Shots
    )
    {
        public double? Tolerance { get; init; }
        public IReadOnlyList<ST_ALIGN_SHOT_OFFSET> CalculatedOffsets { get; init; } = [];
        public IReadOnlyList<ST_ALIGN_SHOT_OFFSET> ExcludedOffsets { get; init; } = [];
    }

    public abstract class CAlignOffsetLogFileBase
    {
        public abstract Task<string> Save(ST_ALIGN_OFFSET_LOG entry, CancellationToken cancellationToken = default);
    }

    public sealed record ST_ALIGN_OFFSET_TOLERANCE_CHECK(double Tolerance, int TotalShots, IReadOnlyList<ST_ALIGN_SHOT_OFFSET> ExceededShots)
    {
        public int ApplyCount => TotalShots - ExceededShots.Count;
        public bool CanApplyRemaining => ApplyCount > 0;
        public string Summary => $"{CAlignInterpolation.AlgorithmName} / Setting Tolerance : ±{Tolerance.ToString("0.000", CultureInfo.InvariantCulture)} mm\n" + $"전체 {TotalShots:N0} Shot · 초과 {ExceededShots.Count:N0} Shot · 적용 가능 {ApplyCount:N0} Shot";
    }

    public static class CAlignOffsetTolerance
    {
        public const string SettingKey = "ALIGN_OFFSET_TOLERANCE";
        public const double DefaultValue = 0.5;
        public static double Parse(string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var tolerance) || !double.IsFinite(tolerance) || tolerance < 0 || tolerance > 100000)
                throw new InvalidOperationException("Setting > Option > Interlock의 Align Offset Tolerance를 0 ~ 100000 mm 범위로 설정해 주세요.");
            return CAlignShotOffsetData.RoundValue(tolerance);
        }

        // Reject the entire shot, including its other axis, if either axis exceeds the limit.
        public static bool Exceeds(ST_ALIGN_SHOT_OFFSET shot, double tolerance) => Math.Abs(shot.X) > tolerance || Math.Abs(shot.Y) > tolerance;
        public static ST_ALIGN_OFFSET_TOLERANCE_CHECK Check(double tolerance, IReadOnlyList<ST_ALIGN_SHOT_OFFSET> offsets) => new(tolerance, offsets.Count, offsets.Where(shot => Exceeds(shot, tolerance)).ToArray());
    }
}
