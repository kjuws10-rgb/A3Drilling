using System.Globalization;
using Drilling.Common.Managers;
using Drilling.Common.Interface;
using Drilling.Common.Motion;
using Drilling.Common.Alarm;
using Drilling.Common.InterLock;
using Drilling.Common.Station;
using Drilling.File.Parser;

namespace Drilling.File.JHMI;
public sealed class CManualScanFile(string configRoot) : CManualScanFileBase
{
    private const string DefaultSettingName = "CIRCLE_TEST.scan";
    private static readonly IReadOnlyList<string> ValueHeaders = ["NAME", "VALUE"];
    private static readonly string[] LaserActionParameterNames = ["LaserOnMode", "Time", "Count", "RepeatCount", "RepeatDelayTime"];
    private readonly string _manualDirectory = Path.Combine(configRoot, "Manual");
    public override Task<IReadOnlyList<string>> List(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_manualDirectory))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        bool ShouldIncludeItem(string? name)
        {
            return !string.IsNullOrWhiteSpace(name);
        }

        string SelectName(string name)
        {
            return name;
        }

        var settingNames = Directory.EnumerateFiles(_manualDirectory, "*.scan").Select(Path.GetFileName).Where(ShouldIncludeItem).Cast<string>().OrderBy(SelectName, StringComparer.OrdinalIgnoreCase).ToArray();
        return Task.FromResult<IReadOnlyList<string>>(settingNames);
    }

    public override Task<IReadOnlyList<ST_MANUAL_SCAN_FORM>> LoadForm(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LoadFormItems());
    }

    public override Task<ST_MANUAL_SCAN_PARAM> Load(CancellationToken cancellationToken = default)
    {
        return Load(GetDefaultSettingName(), cancellationToken);
    }

    public override Task<ST_MANUAL_SCAN_PARAM> Load(string settingName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var formItems = LoadFormItems();
        bool ShouldIncludeItem(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return !string.IsNullOrWhiteSpace(CCsvParser.Get(row, "NAME"));
        }

        string SelectDictionaryKey(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return CCsvParser.Get(row, "NAME");
        }

        string SelectValue(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return CCsvParser.Get(row, "VALUE");
        }

        var values = CCsvParser.Read(GetSettingPath(settingName)).Where(ShouldIncludeItem).ToDictionary(SelectDictionaryKey, SelectValue, StringComparer.OrdinalIgnoreCase);
        var settings = new ST_MANUAL_SCAN_PARAM(
            ReadDouble(values, formItems, "ShapeSize", 0.350), 
            ReadDouble(values, formItems, "OffsetX", 0.000), 
            ReadDouble(values, formItems, "OffsetY", 0.000), 
            ReadString(values, formItems, "Direction", "CW"), 
            ReadString(values, formItems, "ShapeName", "Circle"), 
            ReadDouble(values, formItems, "LaserPower", 1.0), 
            ReadDouble(values, formItems, "JumpSpeed", 1.5), 
            ReadDouble(values, formItems, "MarkSpeed", 0.9), 
            0.0, 
            ReadDouble(values, formItems, "LaserFrequency", 20.0), 
            ReadDouble(values, formItems, "LaserOnDelay", 8.0), 
            ReadDouble(values, formItems, "LaserOffDelay", 12.0), 
            ReadLaserActionDouble(values, formItems, "Time", 1000.0), 
            ReadLaserActionCount(values, formItems, "Count", 1), 
            ReadInt(values, formItems, "GridRowLines", 5), 
            ReadInt(values, formItems, "GridColLines", 5), 
            NormalizeLaserOnMode(ReadString(values, formItems, "LaserOnMode", "TIME")), 
            ReadLaserActionCount(values, formItems, "RepeatCount", 1), 
            ReadLaserActionDouble(values, formItems, "RepeatDelayTime", 0.0)
        );
        return Task.FromResult(settings);
    }

    public override Task Save(ST_MANUAL_SCAN_PARAM settings, CancellationToken cancellationToken = default)
    {
        return Save(GetDefaultSettingName(), settings, cancellationToken);
    }

    public override Task Save(string settingName, ST_MANUAL_SCAN_PARAM settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedName = NormalizeSettingName(settingName);
        var formItems = LoadFormItems();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ShapeSize"] = settings.ShapeSize.ToString("F3", CultureInfo.InvariantCulture),
            ["OffsetX"] = settings.OffsetX.ToString("F3", CultureInfo.InvariantCulture),
            ["OffsetY"] = settings.OffsetY.ToString("F3", CultureInfo.InvariantCulture),
            ["Direction"] = settings.Direction,
            ["ShapeName"] = settings.ShapeName,
            ["LaserPower"] = settings.LaserPower.ToString("F2", CultureInfo.InvariantCulture),
            ["JumpSpeed"] = settings.JumpSpeed.ToString("F3", CultureInfo.InvariantCulture),
            ["MarkSpeed"] = settings.MarkSpeed.ToString("F3", CultureInfo.InvariantCulture),
            ["LaserFrequency"] = settings.LaserFrequency.ToString("F3", CultureInfo.InvariantCulture),
            ["LaserOnDelay"] = settings.LaserOnDelay.ToString("F3", CultureInfo.InvariantCulture),
            ["LaserOffDelay"] = settings.LaserOffDelay.ToString("F3", CultureInfo.InvariantCulture),
            ["GridRowLines"] = settings.GridRowLines.ToString(CultureInfo.InvariantCulture),
            ["GridColLines"] = settings.GridColLines.ToString(CultureInfo.InvariantCulture),
            ["LaserOnMode"] = NormalizeLaserOnMode(settings.LaserOnMode),
            ["Time"] = settings.Time.ToString("R", CultureInfo.InvariantCulture),
            ["Count"] = settings.Count.ToString(CultureInfo.InvariantCulture),
            ["RepeatCount"] = settings.RepeatCount.ToString(CultureInfo.InvariantCulture),
            ["RepeatDelayTime"] = settings.RepeatDelayTime.ToString("R", CultureInfo.InvariantCulture)
        };
        ValidateValues(formItems, values);
        bool IsUse(global::Drilling.Common.Managers.ST_MANUAL_SCAN_FORM item)
        {
            return item.Use;
        }

        int SelectDisplayOrder(global::Drilling.Common.Managers.ST_MANUAL_SCAN_FORM item)
        {
            return item.DisplayOrder;
        }

        global::System.Collections.Generic.Dictionary<string, string> BuildManualSettingValueRow(global::Drilling.Common.Managers.ST_MANUAL_SCAN_FORM item)
        {
            return new Dictionary<string, string>
            {
                ["NAME"] = item.Name,
                ["VALUE"] = values.TryGetValue(item.Name, out var value) ? value : item.DefaultValue
            };
        }

        IReadOnlyList<IReadOnlyDictionary<string, string>> rows = formItems.Where(IsUse).OrderBy(SelectDisplayOrder).Select(BuildManualSettingValueRow).ToArray();
        CCsvParser.Write(GetSettingPath(normalizedName), ValueHeaders, rows);
        ValidateSavedSetting(normalizedName, values);
        return Task.CompletedTask;
    }

    public override Task Rename(string oldSettingName, string newSettingName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var oldPath = GetSettingPath(oldSettingName);
        var newPath = GetSettingPath(newSettingName);
        if (!System.IO.File.Exists(oldPath))
        {
            throw new FileNotFoundException($"Manual setting file was not found: {NormalizeSettingName(oldSettingName)}");
        }

        if (System.IO.File.Exists(newPath))
        {
            throw new IOException($"Manual setting file already exists: {NormalizeSettingName(newSettingName)}");
        }

        Directory.CreateDirectory(_manualDirectory);
        System.IO.File.Move(oldPath, newPath);
        return Task.CompletedTask;
    }

    public override Task Delete(string settingName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetSettingPath(settingName);
        if (System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetDefaultSettingName()
    {
        if (System.IO.File.Exists(GetSettingPath(DefaultSettingName)))
        {
            return DefaultSettingName;
        }

        bool MatchesItem(string? name)
        {
            return !string.IsNullOrWhiteSpace(name);
        }

        return Directory.Exists(_manualDirectory) ? Directory.EnumerateFiles(_manualDirectory, "*.scan").Select(Path.GetFileName).FirstOrDefault(MatchesItem) ?? DefaultSettingName : DefaultSettingName;
    }

    private IReadOnlyList<ST_MANUAL_SCAN_FORM> LoadFormItems()
    {
        global::Drilling.Common.Managers.ST_MANUAL_SCAN_FORM BuildManual(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row, int index)
        {
            var name = CCsvParser.Get(row, "NAME").Trim();
            var unit = name.Equals("LaserOnDelay", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("LaserOffDelay", StringComparison.OrdinalIgnoreCase) ? "msec" : CCsvParser.Get(row, "UNIT");
            return new ST_MANUAL_SCAN_FORM(
                name, 
                GetOrDefault(CCsvParser.Get(row, "DISPLAY NAME"), name), 
                ReadDataType(CCsvParser.Get(row, "DATA TYPE")), 
                unit, 
                ReadBool(CCsvParser.Get(row, "SHOW"), true), 
                ReadBool(CCsvParser.Get(row, "USE"), true), 
                CCsvParser.Get(row, "VALUE"), 
                ReadDoubleValue(CCsvParser.Get(row, "MIN"), 0.0), 
                ReadDoubleValue(CCsvParser.Get(row, "MAX"), 0.0), 
                CCsvParser.Get(row, "DESCRIPTION"), 
                ReadInt(CCsvParser.Get(row, "ORDER"), index + 1)
            );
        }

        bool ShouldIncludeItem(global::Drilling.Common.Managers.ST_MANUAL_SCAN_FORM item)
        {
            return !string.IsNullOrWhiteSpace(item.Name);
        }

        var formItems = CCsvParser.Read(GetFormPath()).Select(BuildManual).Where(ShouldIncludeItem).ToList();
        var defaults = CreateFallbackFormItems();
        var names = formItems.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in defaults)
        {
            if (names.Add(definition.Name)) formItems.Add(definition);
        }
        // Older deployed forms have only the original scan rows. Make the five file-backed laser controls
        // mandatory and normalize their editor metadata without rewriting an operator's form file.
        return formItems.Select(item =>
        {
            if (!LaserActionParameterNames.Contains(item.Name, StringComparer.OrdinalIgnoreCase)) return item;
            var definition = defaults.Single(candidate => candidate.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
            return item with
            {
                Name = definition.Name,
                DisplayName = definition.DisplayName,
                DataType = definition.DataType,
                Unit = definition.Unit,
                Show = true,
                Use = true,
                DefaultValue = GetOrDefault(item.DefaultValue, definition.DefaultValue),
                Min = definition.Min,
                Max = definition.Max,
                DisplayOrder = definition.DisplayOrder
            };
        }).ToArray();
    }

    private void ValidateSavedSetting(string settingName, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool ShouldIncludeItem(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return !string.IsNullOrWhiteSpace(CCsvParser.Get(row, "NAME"));
        }

        string SelectDictionaryKey(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return CCsvParser.Get(row, "NAME");
        }

        string SelectValue(global::System.Collections.Generic.IReadOnlyDictionary<string, string> row)
        {
            return CCsvParser.Get(row, "VALUE");
        }

        var actualValues = CCsvParser.Read(GetSettingPath(settingName)).Where(ShouldIncludeItem).ToDictionary(SelectDictionaryKey, SelectValue, StringComparer.OrdinalIgnoreCase);
        foreach (var(name, expectedValue)in values)
        {
            if (!actualValues.TryGetValue(name, out var actualValue))
            {
                throw new InvalidDataException($"Manual setting CSV validation failed. Missing parameter: {name}");
            }

            if (!actualValue.Equals(expectedValue, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Manual setting CSV validation failed. {name}: expected '{expectedValue}', actual '{actualValue}'");
            }
        }
    }

    private static void ValidateValues(IReadOnlyList<ST_MANUAL_SCAN_FORM> formItems, IReadOnlyDictionary<string, string> values)
    {
        bool IsUse(global::Drilling.Common.Managers.ST_MANUAL_SCAN_FORM item)
        {
            return item.Use;
        }

        string SelectName(global::Drilling.Common.Managers.ST_MANUAL_SCAN_FORM item)
        {
            return item.Name;
        }

        var formItemsByName = formItems.Where(IsUse).ToDictionary(SelectName, StringComparer.OrdinalIgnoreCase);
        foreach (var(name, value)in values)
        {
            if (!formItemsByName.TryGetValue(name, out var formItem))
            {
                throw new InvalidDataException($"Manual setting save blocked. Unknown parameter: {name}");
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException($"Manual setting save blocked. {formItem.DisplayName} cannot be empty.");
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

    private static string ValidateIntParameter(ST_MANUAL_SCAN_FORM formItem, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return $"Manual setting save blocked. {formItem.DisplayName} must be an integer.";
        }

        return ValidateNumericRange(formItem, parsed);
    }

    private static string ValidateDoubleParameter(ST_MANUAL_SCAN_FORM formItem, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
        {
            return $"Manual setting save blocked. {formItem.DisplayName} must be numeric.";
        }

        return ValidateNumericRange(formItem, parsed);
    }

    private static string ValidateBoolParameter(ST_MANUAL_SCAN_FORM formItem, string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        return normalized is "ON" or "OFF" or "TRUE" or "FALSE" or "1" or "0" or "YES" or "NO" ? "" : $"Manual setting save blocked. {formItem.DisplayName} must be ON/OFF or TRUE/FALSE.";
    }

    private static string ValidateNumericRange(ST_MANUAL_SCAN_FORM formItem, double value)
    {
        if (formItem.Min.Equals(formItem.Max))
        {
            return "";
        }

        return value < formItem.Min || value > formItem.Max ? $"Manual setting save blocked. {formItem.DisplayName} must be between {formItem.Min:0.###} and {formItem.Max:0.###} {formItem.Unit}." : "";
    }

    private double ReadDouble(IReadOnlyDictionary<string, string> values, IReadOnlyList<ST_MANUAL_SCAN_FORM> formItems, string key, double defaultValue)
    {
        var value = ReadString(values, formItems, key, defaultValue.ToString(CultureInfo.InvariantCulture));
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
    }

    private int ReadInt(IReadOnlyDictionary<string, string> values, IReadOnlyList<ST_MANUAL_SCAN_FORM> formItems, string key, int defaultValue)
    {
        var value = ReadString(values, formItems, key, defaultValue.ToString(CultureInfo.InvariantCulture));
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
    }

    private double ReadLaserActionDouble(IReadOnlyDictionary<string, string> values, IReadOnlyList<ST_MANUAL_SCAN_FORM> formItems, string key, double defaultValue)
    {
        var text = ReadString(values, formItems, key, defaultValue.ToString(CultureInfo.InvariantCulture));
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || !double.IsFinite(result) || result < 0)
            throw new InvalidDataException($"Manual setting load blocked. {key} must be a finite number greater than or equal to 0 msec.");
        return result;
    }

    private int ReadLaserActionCount(IReadOnlyDictionary<string, string> values, IReadOnlyList<ST_MANUAL_SCAN_FORM> formItems, string key, int defaultValue)
    {
        var text = ReadString(values, formItems, key, defaultValue.ToString(CultureInfo.InvariantCulture));
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < 1)
            throw new InvalidDataException($"Manual setting load blocked. {key} must be an integer greater than or equal to 1.");
        return result;
    }

    private static string NormalizeLaserOnMode(string mode)
    {
        var normalized = mode?.Trim().ToUpperInvariant();
        return normalized is "TIME" or "COUNT" ? normalized : throw new InvalidDataException("Manual setting LaserOnMode must be TIME or COUNT.");
    }

    private static double ReadDoubleValue(string value, double defaultValue)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
    }

    private string ReadString(IReadOnlyDictionary<string, string> values, IReadOnlyList<ST_MANUAL_SCAN_FORM> formItems, string key, string defaultValue)
    {
        bool MatchesItem(global::Drilling.Common.Managers.ST_MANUAL_SCAN_FORM item)
        {
            return item.Name.Equals(key, StringComparison.OrdinalIgnoreCase);
        }

        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : formItems.FirstOrDefault(MatchesItem)?.DefaultValue ?? defaultValue;
    }

    private string GetFormPath()
    {
        return Path.Combine(configRoot, "JHMI_MANUAL_SCAN.csv");
    }

    private string GetSettingPath(string settingName)
    {
        return Path.Combine(_manualDirectory, NormalizeSettingName(settingName));
    }

    private static string NormalizeSettingName(string settingName)
    {
        var normalized = Path.GetFileName(settingName.Trim());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = DefaultSettingName;
        }

        if (!normalized.EndsWith(".scan", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"{normalized}.scan";
        }

        return normalized;
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

    private static string GetOrDefault(string value, string defaultValue)
    {
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static IReadOnlyList<ST_MANUAL_SCAN_FORM> CreateFallbackFormItems()
    {
        return
        [

            new("ShapeName", "Shape Type", EN_RECIPE_DATA_TYPE.String, "", true, true, "Circle", 0.0, 0.0, "Manual shape name", 1),

            new("ShapeSize", "Shape Size", EN_RECIPE_DATA_TYPE.Double, "mm", true, true, "0.350", 0.001, 100.0, "Manual shape size", 2),

            new("Direction", "Direction", EN_RECIPE_DATA_TYPE.String, "", true, true, "CW", 0.0, 0.0, "Manual shape direction", 3),

            new("OffsetX", "Offset X", EN_RECIPE_DATA_TYPE.Double, "mm", true, true, "0.000", -100.0, 100.0, "Manual shape center offset X", 4),

            new("OffsetY", "Offset Y", EN_RECIPE_DATA_TYPE.Double, "mm", true, true, "0.000", -100.0, 100.0, "Manual shape center offset Y", 5),

            new("GridRowLines", "Grid Row Count", EN_RECIPE_DATA_TYPE.Int, "ea", true, true, "5", 2.0, 200.0, "Manual grid row line count", 6),

            new("GridColLines", "Grid Col Count", EN_RECIPE_DATA_TYPE.Int, "ea", true, true, "5", 2.0, 200.0, "Manual grid column line count", 7),

            new("LaserPower", "Laser Power", EN_RECIPE_DATA_TYPE.Double, "W", true, true, "1.00", 0.0, 100.0, "Manual laser power", 10),

            new("JumpSpeed", "Jump Speed", EN_RECIPE_DATA_TYPE.Double, "m/sec", true, true, "1.500", 0.001, 100.0, "Manual jump speed", 20),

            new("MarkSpeed", "Mark Speed", EN_RECIPE_DATA_TYPE.Double, "m/sec", true, true, "0.900", 0.001, 100.0, "Manual mark speed", 30),

            new("LaserFrequency", "Laser Frequency", EN_RECIPE_DATA_TYPE.Double, "kHz", true, true, "20.0", 0.001, 1000.0, "Manual laser frequency", 40),

            new("LaserOnDelay", "Laser On Delay", EN_RECIPE_DATA_TYPE.Double, "msec", true, true, "8", 0.0, 10000.0, "Manual shape pre-mark delay in msec", 50),

            new("LaserOffDelay", "Laser Off Delay", EN_RECIPE_DATA_TYPE.Double, "msec", true, true, "12", 0.0, 10000.0, "Manual shape post-mark delay in msec", 60),

            new("LaserOnMode", "LaserAction_Mode", EN_RECIPE_DATA_TYPE.String, "", true, true, "TIME", 0.0, 0.0, "Laser on mode: TIME or COUNT", 70),

            new("Time", "LaserAction_On Time", EN_RECIPE_DATA_TYPE.Double, "msec", true, true, "1000", 0.0, double.MaxValue, "Laser on duration in msec", 80),

            new("Count", "LaserAction_Shot Count", EN_RECIPE_DATA_TYPE.Int, "count", true, true, "1", 1.0, int.MaxValue, "Laser on shot count", 90),

            new("RepeatCount", "LaserAction_Repeat Count", EN_RECIPE_DATA_TYPE.Int, "count", true, true, "1", 1.0, int.MaxValue, "Laser on repeat count", 100),

            new("RepeatDelayTime", "LaserAction_Repeat Delay Time", EN_RECIPE_DATA_TYPE.Double, "msec", true, true, "0", 0.0, double.MaxValue, "Delay between laser on repetitions in msec", 110)

        ];
    }
}
