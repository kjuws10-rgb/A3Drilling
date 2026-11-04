using System.Globalization;
using Drilling.Common.Managers;

namespace Drilling.Common.Automation;
// One configuration mapping for background alarm checks and all scanner status displays.
public static class CScannerAxisConfiguration
{
    public static IReadOnlyList<ST_SCANNER_AXIS_SETTING> BuildAxisDefinitions(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo)
    {
        var normalizedHeadNo = NormalizeHeadNo(headNo);
        if (!IsHeadEnabled(settings, normalizedHeadNo))
        {
            return[];
        }

        var automationNo = ReadHeadSettingInt(settings, normalizedHeadNo <= 4 ? 0 : 1, normalizedHeadNo, "AUTOMATION_NO");
        var xAxis = ReadHeadSettingText(settings, "", normalizedHeadNo, "X_AXIS", "GX_AXIS");
        var yAxis = ReadHeadSettingText(settings, "", normalizedHeadNo, "Y_AXIS", "GY_AXIS");
        var aioNo = ReadHeadSettingInt(settings, 0, normalizedHeadNo, "SCANNER_AIO_NO");
        var definitions = new List<ST_SCANNER_AXIS_SETTING>(3)
        {
            CreateAxisDefinition(normalizedHeadNo, automationNo, "X", xAxis, aioNo),
            CreateAxisDefinition(normalizedHeadNo, automationNo, "Y", yAxis, aioNo)
        };
        AppendScannerZAxisDefinition(settings, normalizedHeadNo, automationNo, aioNo, definitions);
        return definitions;
    }

    public static IReadOnlyList<ST_SCANNER_AXIS_SETTING> BuildAxisDefinitions(IReadOnlyDictionary<string, string> parameters, int headNo)
    {
        var normalizedHeadNo = NormalizeHeadNo(headNo);
        if (!IsHeadEnabled(parameters, normalizedHeadNo))
        {
            return[];
        }

        var automationNo = ReadHeadSettingInt(parameters, normalizedHeadNo <= 4 ? 0 : 1, normalizedHeadNo, "AUTOMATION_NO");
        var xAxis = ReadHeadSettingText(parameters, "", normalizedHeadNo, "X_AXIS", "GX_AXIS");
        var yAxis = ReadHeadSettingText(parameters, "", normalizedHeadNo, "Y_AXIS", "GY_AXIS");
        var aioNo = ReadHeadSettingInt(parameters, 0, normalizedHeadNo, "SCANNER_AIO_NO");
        var definitions = new List<ST_SCANNER_AXIS_SETTING>(3)
        {
            CreateAxisDefinition(normalizedHeadNo, automationNo, "X", xAxis, aioNo),
            CreateAxisDefinition(normalizedHeadNo, automationNo, "Y", yAxis, aioNo)
        };
        AppendScannerZAxisDefinition(parameters, normalizedHeadNo, automationNo, aioNo, definitions);
        return definitions;
    }

    private static void AppendScannerZAxisDefinition(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo, int automationNo, int aioNo, List<ST_SCANNER_AXIS_SETTING> definitions)
    {
        if (!ReadHeadSettingBool(settings, false, headNo, "SCANNER_Z_USE", "Z_USE"))
        {
            definitions.Add(CreateUnusedAxisDefinition(headNo, automationNo, "Z", aioNo));
            return;
        }

        var zAxis = ReadHeadSettingText(settings, "", headNo, "SCANNER_Z_AXIS", "Z_AXIS");
        definitions.Add(CreateAxisDefinition(headNo, automationNo, "Z", zAxis, aioNo, false));
    }

    private static void AppendScannerZAxisDefinition(IReadOnlyDictionary<string, string> parameters, int headNo, int automationNo, int aioNo, List<ST_SCANNER_AXIS_SETTING> definitions)
    {
        if (!ReadHeadSettingBool(parameters, false, headNo, "SCANNER_Z_USE", "Z_USE"))
        {
            definitions.Add(CreateUnusedAxisDefinition(headNo, automationNo, "Z", aioNo));
            return;
        }

        var zAxis = ReadHeadSettingText(parameters, "", headNo, "SCANNER_Z_AXIS", "Z_AXIS");
        definitions.Add(CreateAxisDefinition(headNo, automationNo, "Z", zAxis, aioNo, false));
    }

    private static ST_SCANNER_AXIS_SETTING CreateUnusedAxisDefinition(int headNo, int automationNo, string axisRole, int aioNo)
    {
        return new ST_SCANNER_AXIS_SETTING(headNo, automationNo, axisRole, "NOT USE", aioNo, "", false, false);
    }

    private static ST_SCANNER_AXIS_SETTING CreateAxisDefinition(int headNo, int automationNo, string axisRole, string axisReference, int aioNo, bool readExtendedStatus = true)
    {
        var normalizedAxisReference = axisReference.Trim();
        var configurationError = ValidateConfiguredAxisName(headNo, axisRole, normalizedAxisReference);
        if (!string.IsNullOrWhiteSpace(configurationError))
        {
            return new ST_SCANNER_AXIS_SETTING(headNo, automationNo, axisRole, "", aioNo, configurationError, true, readExtendedStatus);
        }

        return new ST_SCANNER_AXIS_SETTING(headNo, automationNo, axisRole, normalizedAxisReference, aioNo, "", true, readExtendedStatus);
    }

    private static string ValidateConfiguredAxisName(int headNo, string axisRole, string axisReference)
    {
        if (string.IsNullOrWhiteSpace(axisReference) || axisReference.Equals("-", StringComparison.Ordinal))
        {
            return $"H{headNo:00} {axisRole} axis name is not configured.";
        }

        if (IsAllAsciiDigits(axisReference))
        {
            return $"H{headNo:00} {axisRole} axis must be configured by axis name, not axis number: {axisReference}.";
        }

        if (!IsAutomationAxisName(axisReference))
        {
            return $"H{headNo:00} {axisRole} axis name is invalid: {axisReference}.";
        }

        return "";
    }

    private static bool IsAllAsciiDigits(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAutomationAxisName(string value)
    {
        if (value.Length == 0 || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetter(character) && !char.IsAsciiDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<int> BuildEnabledHeadNumbers(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headCount = 8)
    {
        var heads = new List<int>(headCount);
        for (var headNo = 1; headNo <= headCount; headNo++)
        {
            if (IsHeadEnabled(settings, headNo))
            {
                heads.Add(headNo);
            }
        }

        return heads;
    }

    public static bool IsHeadEnabled(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int headNo)
    {
        return ReadHeadSettingBool(settings, true, headNo, "USE");
    }

    public static bool IsHeadEnabled(IReadOnlyDictionary<string, string> parameters, int headNo)
    {
        return ReadHeadSettingBool(parameters, true, headNo, "USE");
    }

    private static string ReadHeadSettingText(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, string defaultValue, int headNo, params string[] suffixes)
    {
        var prefix = $"H{headNo:00}_";
        foreach (var suffix in suffixes)
        {
            var key = prefix + suffix;
            foreach (var setting in settings)
            {
                if (!key.Equals(setting.Key, StringComparison.OrdinalIgnoreCase) && !key.Equals(setting.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(setting.Value))
                {
                    return setting.Value.Trim();
                }
            }
        }

        return defaultValue;
    }

    private static bool ReadHeadSettingBool(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, bool defaultValue, int headNo, params string[] suffixes)
    {
        var prefix = $"H{headNo:00}_";
        foreach (var suffix in suffixes)
        {
            var key = prefix + suffix;
            foreach (var setting in settings)
            {
                if (!key.Equals(setting.Key, StringComparison.OrdinalIgnoreCase) && !key.Equals(setting.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(setting.Value))
                {
                    return defaultValue;
                }

                return setting.Value.Trim().ToUpperInvariant() switch
                {
                    "1" or "Y" or "YES" or "TRUE" or "ON" or "USE" => true,
                    "0" or "N" or "NO" or "FALSE" or "OFF" or "SKIP" or "UNUSE" => false,
                    _ => defaultValue
                };
            }
        }

        return defaultValue;
    }

    private static int ReadHeadSettingInt(IReadOnlyList<ST_SYSTEM_PARAMETER> settings, int defaultValue, int headNo, params string[] suffixes)
    {
        var prefix = $"H{headNo:00}_";
        foreach (var suffix in suffixes)
        {
            var key = prefix + suffix;
            foreach (var setting in settings)
            {
                if (!key.Equals(setting.Key, StringComparison.OrdinalIgnoreCase) && !key.Equals(setting.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (int.TryParse(setting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return defaultValue;
    }

    private static string ReadHeadSettingText(IReadOnlyDictionary<string, string> parameters, string defaultValue, int headNo, params string[] suffixes)
    {
        var prefix = $"H{headNo:00}_";
        foreach (var suffix in suffixes)
        {
            var key = prefix + suffix;
            if (parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return defaultValue;
    }

    private static bool ReadHeadSettingBool(IReadOnlyDictionary<string, string> parameters, bool defaultValue, int headNo, params string[] suffixes)
    {
        var prefix = $"H{headNo:00}_";
        foreach (var suffix in suffixes)
        {
            var key = prefix + suffix;
            if (!parameters.TryGetValue(key, out var value))
            {
                continue;
            }

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

        return defaultValue;
    }

    private static int ReadHeadSettingInt(IReadOnlyDictionary<string, string> parameters, int defaultValue, int headNo, params string[] suffixes)
    {
        var prefix = $"H{headNo:00}_";
        foreach (var suffix in suffixes)
        {
            var key = prefix + suffix;
            if (parameters.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    private static int NormalizeHeadNo(int headNo) => Math.Clamp(headNo, 1, 8);
}

public readonly record struct ST_SCANNER_AXIS_SETTING(
    int HeadNo, 
    int AutomationNo, 
    string AxisName, 
    string AxisReference, 
    int AioNo = 0, 
    string ConfigurationError = "", 
    bool IsUsed = true, 
    bool ReadExtendedStatus = true
);
