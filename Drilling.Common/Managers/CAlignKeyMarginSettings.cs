namespace Drilling.Common.Managers
{
    using System.Globalization;
    using System.IO;

    public readonly record struct ST_ALIGN_KEY_MARGINS(double X, double Y);

    public static class CAlignKeyMarginSettings
    {
        public const string MarginXKey = "AK_MARGIN_X";
        public const string MarginYKey = "AK_MARGIN_Y";
        public const double DefaultMarginX = 15.5;
        public const double DefaultMarginY = 15.5;

        public static ST_ALIGN_KEY_MARGINS Read(IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            return new(Read(settings, MarginXKey), Read(settings, MarginYKey));
        }

        public static ST_ALIGN_KEY_MARGINS Read(IReadOnlyDictionary<string, string> settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            return new(Read(settings, MarginXKey), Read(settings, MarginYKey));
        }

        private static double Read(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, string key)
        {
            var parameter = settings.FirstOrDefault(item =>
                item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                item.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            return Parse(parameter?.Value, key);
        }

        private static double Read(IReadOnlyDictionary<string, string> settings, string key)
        {
            var pair = settings.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            return Parse(pair.Value, key);
        }

        private static double Parse(string? text, string key)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value) ||
                value < 0)
            {
                throw new InvalidDataException($"Setting > Common > ALIGN의 {key}는 0 이상의 유한한 mm 값이어야 합니다.");
            }

            return value;
        }
    }
}
