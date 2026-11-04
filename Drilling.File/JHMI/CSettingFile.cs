using Drilling.Common.Log;
using System.Globalization;
using Drilling.Common.Managers;
using Drilling.Common.Interface;
using Drilling.Common.Interface.Vision;
using Drilling.Common.Motion;
using Drilling.Common.Alarm;
using Drilling.Common.InterLock;
using Drilling.Common.Station;
using Drilling.File.Parser;

namespace Drilling.File.JHMI;
public sealed class CSettingFile(string configRoot) : CSettingFileBase
{
    private static readonly IReadOnlyList<string> FormHeaders = ["TAB", "GROUP", "NAME", "DISPLAY NAME", "DATA TYPE", "UNIT", "SHOW", "USE", "VALUE", "MIN", "MAX", "DESCRIPTION"];
    private static readonly IReadOnlyList<string> ValueHeaders = ["TAB", "NAME", "VALUE"];
    private readonly string _settingDirectory = Path.Combine(configRoot, "Setting");
    private readonly CLogManager _logManager = new(configRoot);
    public override IReadOnlyList<ST_SYSTEM_PARAMETER> Load(EN_SETTING_TAB section, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = LoadSettingValues();
        bool ShouldIncludeItem(global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM item)
        {
            return item.Use && item.Tab.Equals(ToTabText(section), StringComparison.OrdinalIgnoreCase);
        }

        global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER BuildSystem(global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM item)
        {
            return new ST_SYSTEM_PARAMETER(
                section, 
                item.DisplayName, 
                GetValue(values, item.Tab, item.Name, item.DefaultValue), 
                item.Unit, 
                item.Description, 
                item.Group, 
                item.Name, 
                item.DefaultValue, 
                item.DataType, 
                item.Min, 
                item.Max, 
                item.Show, 
                item.Use, 
                item.DisplayOrder, 
                item.Extra
            );
        }

        return LoadFormItems().Where(ShouldIncludeItem).Select(BuildSystem).ToArray();
    }

    public override void Save(EN_SETTING_TAB section, IReadOnlyList<ST_SYSTEM_PARAMETER> parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_settingDirectory);
        var formItems = LoadFormItems();
        var values = LoadSettingValues();
        var sectionTab = ToTabText(section);
        bool ShouldIncludeItem(global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM item)
        {
            return item.Use && item.Tab.Equals(sectionTab, StringComparison.OrdinalIgnoreCase);
        }

        string SelectName(global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM item)
        {
            return item.Name;
        }

        string SelectValue(global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM item)
        {
            return GetValue(values, item.Tab, item.Name, item.DefaultValue);
        }

        var oldValues = formItems.Where(ShouldIncludeItem).ToDictionary(SelectName, SelectValue, StringComparer.OrdinalIgnoreCase);
        bool ShouldIncludeSystem(global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER parameter)
        {
            return !string.IsNullOrWhiteSpace(GetParameterKey(parameter));
        }

        string SelectEditedParameterValue(global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER parameter)
        {
            return parameter.Value;
        }

        var editedValues = parameters.Where(ShouldIncludeSystem).ToDictionary(GetParameterKey, SelectEditedParameterValue, StringComparer.OrdinalIgnoreCase);
        bool ShouldIncludeSetting(global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM item)
        {
            return item.Use && item.Tab.Equals(sectionTab, StringComparison.OrdinalIgnoreCase);
        }

        global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER BuildSystem(global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM item)
        {
            return new ST_SYSTEM_PARAMETER(
                section, 
                item.DisplayName, 
                editedValues.TryGetValue(item.Name, out var editedValue) ? editedValue : GetValue(values, item.Tab, item.Name, item.DefaultValue), 
                item.Unit, 
                item.Description, 
                item.Group, 
                item.Name, 
                item.DefaultValue, 
                item.DataType, 
                item.Min, 
                item.Max, 
                item.Show, 
                item.Use, 
                item.DisplayOrder, 
                item.Extra
            );
        }

        var normalizedParameters = formItems.Where(ShouldIncludeSetting).Select(BuildSystem).ToArray();
        ValidateSectionParameters(sectionTab, normalizedParameters, formItems);
        foreach (var parameter in normalizedParameters)
        {
            values[CreateKey(sectionTab, GetParameterKey(parameter))] = parameter.Value;
        }

        WriteSettingValues(formItems, values);
        ValidateSavedSection(section, normalizedParameters);
        foreach (var parameter in normalizedParameters)
        {
            var oldValue = oldValues.TryGetValue(GetParameterKey(parameter), out var value) ? value : "";
            if (!oldValue.Equals(parameter.Value, StringComparison.Ordinal))
            {
                _logManager.WriteSettingModify(section, parameter.Name, oldValue, parameter.Value);
            }
        }

        _logManager.WriteSettingSave(section);
    }

    public override IReadOnlyList<ST_SETTING_HISTORY> LoadHistory(EN_SETTING_TAB section, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _logManager.ReadSettingRecent(section);
    }

    private IReadOnlyList<ST_SETTING_FORM_ITEM> LoadFormItems()
    {
        string[] SelectItem(string header)
        {
            return new[]
            {
                header
            };
        }

        CCsvParser.ValidateRequiredHeaders(GetFormPath(), "JHMI_SETTING", FormHeaders.Select(SelectItem));
        global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM BuildSetting(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row, int index)
        {
            return new ST_SETTING_FORM_ITEM(
                NormalizeTab(CCsvParser.Get(row, "TAB")), 
                NormalizeSettingText(CCsvParser.Get(row, "GROUP"), "COMMON"), 
                CCsvParser.Get(row, "NAME"), 
                GetOrDefault(CCsvParser.Get(row, "DISPLAY NAME"), CCsvParser.Get(row, "NAME")), 
                ReadDataType(CCsvParser.Get(row, "DATA TYPE")), 
                CCsvParser.Get(row, "UNIT"), 
                ReadBool(CCsvParser.Get(row, "SHOW"), true), 
                ReadBool(CCsvParser.Get(row, "USE"), true), 
                CCsvParser.Get(row, "VALUE"), 
                ReadDouble(CCsvParser.Get(row, "MIN"), 0.0), 
                ReadDouble(CCsvParser.Get(row, "MAX"), 0.0), 
                CCsvParser.Get(row, "DESCRIPTION"), 
                index + 1, 
                CCsvParser.GetExtra(row, FormHeaders)
            );
        }

        bool ShouldIncludeItem(global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM item)
        {
            return !string.IsNullOrWhiteSpace(item.Tab) && !string.IsNullOrWhiteSpace(item.Name);
        }

        var formItems = CCsvParser.Read(GetFormPath()).Select(BuildSetting).Where(ShouldIncludeItem).ToArray();
        return AppendBuiltInFormItems(formItems);
    }

    private static IReadOnlyList<ST_SETTING_FORM_ITEM> AppendBuiltInFormItems(IReadOnlyList<ST_SETTING_FORM_ITEM> formItems)
    {
        string SelectItem(global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM item)
        {
            return CreateKey(item.Tab, item.Name);
        }

        var existingKeys = formItems.Select(SelectItem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = new List<ST_SETTING_FORM_ITEM>(formItems);
        foreach (var item in CreateScannerFormItems().Append(new ST_SETTING_FORM_ITEM(
            "OPTION", "VISION", CVisionProtocol.MaxHoleCountPerLineSettingKey, "MaxHoleCountPerLine", EN_RECIPE_DATA_TYPE.Int, "count", true, true,
            CVisionProtocol.DefaultMaxHoleCountPerLine.ToString(CultureInfo.InvariantCulture), 1, CVisionProtocol.MaximumMarkCount,
            "1001 송신·1003 수신 배열의 고정 칸 수; Count는 실제 유효 개수; 송신 잔여 칸은 모든 필드 -1; 수신 Count 초과 시 HEAVY 알람 및 검사 중단", 38)).Append(new ST_SETTING_FORM_ITEM(
            "OPTION", "VISION", CSettingManager.VisionCameraResolutionKey, "Camera Resolution", EN_RECIPE_DATA_TYPE.Double, "mm/pixel", true, true,
            CSettingManager.DefaultVisionCameraResolution.ToString("R", CultureInfo.InvariantCulture), 0, 0,
            "카메라 1 pixel당 길이(mm); 기본 0.0025 mm/pixel=2.5 um/pixel; 오차(mm)=이 값 x 수신 nResolution x (Target-Hole); Vision Flip 적용 후 Tolerance 판정; 양의 유한값만 저장", 39)))
        {
            if (existingKeys.Add(CreateKey(item.Tab, item.Name)))
            {
                merged.Add(item);
            }
        }

        return merged;
    }

    private static IEnumerable<ST_SETTING_FORM_ITEM> CreateScannerFormItems()
    {
        for (var headNo = 1; headNo <= 8; headNo++)
        {
            yield return new ST_SETTING_FORM_ITEM(
                "OPTION", 
                "SCANNER", 
                $"H{headNo:00}_SCANNER_DIO_NO", 
                $"H{headNo:00} Scanner DIO No", 
                EN_RECIPE_DATA_TYPE.Int, 
                "bit", 
                true, 
                true, 
                "-1", 
                0, 
                63, 
                $"H{headNo:00} manual scanner DIO output bit number on GX axis", 
                84 + headNo
            );
            yield return new ST_SETTING_FORM_ITEM("OPTION", "SCANNER", $"H{headNo:00}_SCANNER_Z_USE", $"H{headNo:00} Scanner Z Use", EN_RECIPE_DATA_TYPE.Bool, "", true, true, "OFF", 0, 1, $"H{headNo:00} 스캐너 Z축 사용 여부", 100 + (headNo * 2));
            yield return new ST_SETTING_FORM_ITEM(
                "OPTION", 
                "SCANNER", 
                $"H{headNo:00}_SCANNER_Z_AXIS", 
                $"H{headNo:00} Scanner Z Axis", 
                EN_RECIPE_DATA_TYPE.String, 
                "", 
                true, 
                true, 
                $"Z{headNo}", 
                0, 
                0, 
                $"H{headNo:00} Automation1 스캐너 Z축 이름", 
                101 + (headNo * 2)
            );
        }
    }

    private Dictionary<string, string> LoadSettingValues()
    {
        var valuePath = GetValuePath();
        string[] SelectItem(string header)
        {
            return new[]
            {
                header
            };
        }

        if (System.IO.File.Exists(valuePath))
        {
            CCsvParser.ValidateRequiredHeaders(valuePath, "Setting.csv", ValueHeaders.Select(SelectItem));
        }

        bool HasSettingValueKey(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return !string.IsNullOrWhiteSpace(CCsvParser.Get(row, "TAB")) && !string.IsNullOrWhiteSpace(CCsvParser.Get(row, "NAME"));
        }

        string SelectGroupKey(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return CreateKey(CCsvParser.Get(row, "TAB"), CCsvParser.Get(row, "NAME"));
        }

        string SelectKey(global::System.Linq.IGrouping<string, global::System.Collections.Generic.IReadOnlyDictionary<string, string>> group)
        {
            return group.Key;
        }

        string SelectValue(global::System.Linq.IGrouping<string, global::System.Collections.Generic.IReadOnlyDictionary<string, string>> group)
        {
            return CCsvParser.Get(group.Last(), "VALUE");
        }

        return CCsvParser.Read(GetValuePath()).Where(HasSettingValueKey).GroupBy(SelectGroupKey, StringComparer.OrdinalIgnoreCase).ToDictionary(SelectKey, SelectValue, StringComparer.OrdinalIgnoreCase);
    }

    private void WriteSettingValues(IReadOnlyList<ST_SETTING_FORM_ITEM> formItems, IReadOnlyDictionary<string, string> values)
    {
        bool IsUse(global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM item)
        {
            return item.Use;
        }

        global::System.Collections.Generic.Dictionary<string, string> BuildSettingValueRow(global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM item)
        {
            return new Dictionary<string, string>
            {
                ["TAB"] = item.Tab,
                ["NAME"] = item.Name,
                ["VALUE"] = GetValue(values, item.Tab, item.Name, item.DefaultValue)
            };
        }

        var rows = formItems.Where(IsUse).Select(BuildSettingValueRow);
        CCsvParser.Write(GetValuePath(), ValueHeaders, rows);
    }

    private void ValidateSavedSection(EN_SETTING_TAB section, IReadOnlyList<ST_SYSTEM_PARAMETER> expectedParameters)
    {
        var actualValues = LoadSettingValues();
        var sectionTab = ToTabText(section);
        foreach (var expectedParameter in expectedParameters)
        {
            var parameterKey = GetParameterKey(expectedParameter);
            var key = CreateKey(sectionTab, parameterKey);
            if (!actualValues.TryGetValue(key, out var actualValue))
            {
                throw new InvalidDataException($"Setting CSV validation failed. Missing parameter: {sectionTab}/{parameterKey}");
            }

            if (!actualValue.Equals(expectedParameter.Value, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Setting CSV validation failed. {sectionTab}/{parameterKey}: expected '{expectedParameter.Value}', actual '{actualValue}'");
            }
        }
    }

    private static void ValidateSectionParameters(string sectionTab, IReadOnlyList<ST_SYSTEM_PARAMETER> parameters, IReadOnlyList<ST_SETTING_FORM_ITEM> formItems)
    {
        bool ShouldIncludeItem(global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM item)
        {
            return item.Use && item.Tab.Equals(sectionTab, StringComparison.OrdinalIgnoreCase);
        }

        string SelectName(global::Drilling.File.JHMI.CSettingFile.ST_SETTING_FORM_ITEM item)
        {
            return item.Name;
        }

        var formItemsByName = formItems.Where(ShouldIncludeItem).ToDictionary(SelectName, StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            var parameterKey = GetParameterKey(parameter);
            if (!formItemsByName.TryGetValue(parameterKey, out var formItem))
            {
                throw new InvalidDataException($"Setting save blocked. Unknown parameter: {sectionTab}/{parameter.Name}");
            }

            var value = parameter.Value.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException($"Setting save blocked. {formItem.DisplayName} cannot be empty.");
            }

            var validationMessage = formItem.DataType switch
            {
                EN_RECIPE_DATA_TYPE.Int => ValidateIntParameter(formItem, value),
                EN_RECIPE_DATA_TYPE.Double => ValidateDoubleParameter(formItem, value),
                EN_RECIPE_DATA_TYPE.Bool => ValidateBoolParameter(formItem, value),
                _ => ""
            };
            if (!string.IsNullOrWhiteSpace(validationMessage))
            {
                throw new InvalidDataException(validationMessage);
            }
        }
    }

    private static string GetParameterKey(ST_SYSTEM_PARAMETER parameter)
    {
        return string.IsNullOrWhiteSpace(parameter.Key) ? parameter.Name : parameter.Key;
    }

    private static string ValidateIntParameter(ST_SETTING_FORM_ITEM formItem, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return $"Setting save blocked. {formItem.DisplayName} must be an integer.";
        }

        return ValidateNumericRange(formItem, parsed);
    }

    private static string ValidateDoubleParameter(ST_SETTING_FORM_ITEM formItem, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
        {
            return $"Setting save blocked. {formItem.DisplayName} must be numeric.";
        }

        if (formItem.Unit.Equals("sign", StringComparison.OrdinalIgnoreCase) && parsed != -1.0 && parsed != 1.0)
        {
            return $"Setting save blocked. {formItem.DisplayName} must be -1 or +1.";
        }

        return ValidateNumericRange(formItem, parsed);
    }

    private static string ValidateBoolParameter(ST_SETTING_FORM_ITEM formItem, string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        return normalized is "ON" or "OFF" or "TRUE" or "FALSE" or "1" or "0" or "YES" or "NO" ? "" : $"Setting save blocked. {formItem.DisplayName} must be ON/OFF or TRUE/FALSE.";
    }

    private static string ValidateNumericRange(ST_SETTING_FORM_ITEM formItem, double value)
    {
        if (!formItem.Min.Equals(formItem.Max) && (value < formItem.Min || value > formItem.Max))
        {
            return $"Setting save blocked. {formItem.DisplayName} must be between {formItem.Min:0.###} and {formItem.Max:0.###}.";
        }

        return "";
    }

    private string GetFormPath()
    {
        return Path.Combine(configRoot, "JHMI_SETTING.csv");
    }

    private string GetValuePath()
    {
        return Path.Combine(_settingDirectory, "Setting.csv");
    }

    private static string GetValue(IReadOnlyDictionary<string, string> values, string tab, string name, string defaultValue)
    {
        return values.TryGetValue(CreateKey(tab, name), out var value) ? value : defaultValue;
    }

    private static string CreateKey(string tab, string name)
    {
        return $"{NormalizeTab(tab)}|{name.Trim().ToUpperInvariant()}";
    }

    private static string NormalizeTab(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        return normalized switch
        {
            "IO" => "IO",
            "POSITION" => "OPTION",
            "OPTION" or "INTERFACE" or "MOTOR" or "ALARM" => normalized,
            _ => normalized
        };
    }

    private static string NormalizeSettingText(string value, string defaultValue)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim().ToUpperInvariant();
    }

    private static string GetOrDefault(string value, string defaultValue)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static EN_RECIPE_DATA_TYPE ReadDataType(string value)
    {
        return value.Trim().ToUpperInvariant() switch
        {
            "INT" => EN_RECIPE_DATA_TYPE.Int,
            "DOUBLE" => EN_RECIPE_DATA_TYPE.Double,
            "BOOL" => EN_RECIPE_DATA_TYPE.Bool,
            _ => EN_RECIPE_DATA_TYPE.String
        };
    }

    private static bool ReadBool(string value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || value.Equals("USE", StringComparison.OrdinalIgnoreCase) || value.Equals("ON", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadInt(string value, int defaultValue)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
    }

    private static double ReadDouble(string value, double defaultValue)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
    }

    private static string ToTabText(EN_SETTING_TAB section)
    {
        return section switch
        {
            EN_SETTING_TAB.Io => "IO",
            _ => section.ToString().ToUpperInvariant()};
    }

    private sealed record ST_SETTING_FORM_ITEM(
        string Tab, 
        string Group, 
        string Name, 
        string DisplayName, 
        EN_RECIPE_DATA_TYPE DataType, 
        string Unit, 
        bool Show, 
        bool Use, 
        string DefaultValue, 
        double Min, 
        double Max, 
        string Description, 
        int DisplayOrder, 
        IReadOnlyDictionary<string, string>? Extra = null
    );
}
