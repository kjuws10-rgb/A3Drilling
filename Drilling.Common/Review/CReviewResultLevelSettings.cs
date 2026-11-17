using System.Globalization;
using Drilling.Common.Managers;

namespace Drilling.Common.Review;

public enum EN_REVIEW_RESULT_LEVEL
{
    Level1 = 1,
    Level2 = 2,
    Level3 = 3,
    Level4 = 4
}

public sealed record ST_REVIEW_RESULT_LEVEL_SETTINGS(
    double Level1Maximum,
    double Level2Maximum,
    double Level3Maximum,
    string Level1Color,
    string Level2Color,
    string Level3Color,
    string Level4Color)
{
    public EN_REVIEW_RESULT_LEVEL Resolve(double resultX, double resultY)
    {
        var magnitude = Math.Max(Math.Abs(resultX), Math.Abs(resultY));
        if (!double.IsFinite(magnitude))
        {
            return EN_REVIEW_RESULT_LEVEL.Level4;
        }

        if (magnitude <= Level1Maximum)
        {
            return EN_REVIEW_RESULT_LEVEL.Level1;
        }

        if (magnitude <= Level2Maximum)
        {
            return EN_REVIEW_RESULT_LEVEL.Level2;
        }

        return magnitude <= Level3Maximum
            ? EN_REVIEW_RESULT_LEVEL.Level3
            : EN_REVIEW_RESULT_LEVEL.Level4;
    }

    public string ColorFor(EN_REVIEW_RESULT_LEVEL level)
    {
        return level switch
        {
            EN_REVIEW_RESULT_LEVEL.Level1 => Level1Color,
            EN_REVIEW_RESULT_LEVEL.Level2 => Level2Color,
            EN_REVIEW_RESULT_LEVEL.Level3 => Level3Color,
            _ => Level4Color
        };
    }
}

public static class CReviewResultLevelSettings
{
    public const string Level1MaximumKey = "REVIEW_LEVEL1_MAX";
    public const string Level2MaximumKey = "REVIEW_LEVEL2_MAX";
    public const string Level3MaximumKey = "REVIEW_LEVEL3_MAX";
    public const string Level1ColorKey = "REVIEW_LEVEL1_COLOR";
    public const string Level2ColorKey = "REVIEW_LEVEL2_COLOR";
    public const string Level3ColorKey = "REVIEW_LEVEL3_COLOR";
    public const string Level4ColorKey = "REVIEW_LEVEL4_COLOR";

    public const double DefaultLevel1Maximum = 0.010;
    public const double DefaultLevel2Maximum = 0.020;
    public const double DefaultLevel3Maximum = 0.030;
    public const string DefaultLevel1Color = "#22C55E";
    public const string DefaultLevel2Color = "#84CC16";
    public const string DefaultLevel3Color = "#F59E0B";
    public const string DefaultLevel4Color = "#EF4444";

    private static readonly HashSet<string> ColorKeys = new(
        [Level1ColorKey, Level2ColorKey, Level3ColorKey, Level4ColorKey],
        StringComparer.OrdinalIgnoreCase);

    public static ST_REVIEW_RESULT_LEVEL_SETTINGS Default { get; } = new(
        DefaultLevel1Maximum,
        DefaultLevel2Maximum,
        DefaultLevel3Maximum,
        DefaultLevel1Color,
        DefaultLevel2Color,
        DefaultLevel3Color,
        DefaultLevel4Color);

    public static bool IsColorKey(string? key)
    {
        return !string.IsNullOrWhiteSpace(key) && ColorKeys.Contains(key);
    }

    public static ST_REVIEW_RESULT_LEVEL_SETTINGS Read(IEnumerable<ST_SYSTEM_PARAMETER> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameter.Key))
            {
                values[parameter.Key] = parameter.Value;
            }

            if (!string.IsNullOrWhiteSpace(parameter.Name))
            {
                values.TryAdd(parameter.Name, parameter.Value);
            }
        }

        return Read(values);
    }

    public static ST_REVIEW_RESULT_LEVEL_SETTINGS Read(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var level1 = ReadMaximum(values, Level1MaximumKey, DefaultLevel1Maximum);
        var level2 = ReadMaximum(values, Level2MaximumKey, DefaultLevel2Maximum);
        var level3 = ReadMaximum(values, Level3MaximumKey, DefaultLevel3Maximum);
        if (!(level1 < level2 && level2 < level3))
        {
            throw new InvalidDataException(
                $"Review Level 범위는 Level 1 < Level 2 < Level 3 순서여야 합니다. 현재값: {level1:0.###}, {level2:0.###}, {level3:0.###} mm");
        }

        return new ST_REVIEW_RESULT_LEVEL_SETTINGS(
            level1,
            level2,
            level3,
            ReadColor(values, Level1ColorKey, DefaultLevel1Color),
            ReadColor(values, Level2ColorKey, DefaultLevel2Color),
            ReadColor(values, Level3ColorKey, DefaultLevel3Color),
            ReadColor(values, Level4ColorKey, DefaultLevel4Color));
    }

    public static string NormalizeColor(string value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 6)
        {
            text = $"#{text}";
        }

        if (text.Length != 7 || text[0] != '#' || !text.AsSpan(1).ToString().All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"Review Level Color는 #RRGGBB 형식이어야 합니다. 현재값: '{value}'");
        }

        return text.ToUpperInvariant();
    }

    private static double ReadMaximum(
        IReadOnlyDictionary<string, string> values,
        string key,
        double fallback)
    {
        var text = values.TryGetValue(key, out var value) ? value : fallback.ToString("R", CultureInfo.InvariantCulture);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            || !double.IsFinite(result)
            || result <= 0.0)
        {
            throw new InvalidDataException($"{key}는 0보다 큰 유한한 mm 값이어야 합니다.");
        }

        return result;
    }

    private static string ReadColor(
        IReadOnlyDictionary<string, string> values,
        string key,
        string fallback)
    {
        return NormalizeColor(values.TryGetValue(key, out var value) ? value : fallback);
    }
}
