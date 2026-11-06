namespace Drilling.Common.Managers
{
    using System.Globalization;
    using Drilling.Common.Review;
    using Drilling.Common.Interface;
    using Drilling.Common.Alarm;
    using Drilling.Common.InterLock;
    using Drilling.Common.Managers;
    using Drilling.Common.Motion;
    using Drilling.Common.Station;
    using System.IO;
    using Drilling.Common.Interface.Vision;

    public sealed class CSettingManager(CSettingFileBase settingFile, CInterfaceFileBase interfaceFile, CInterfaceManager interfaceManager)
    {
        public const string ReviewCameraResolutionKey = "Review_Camera_Resolution";
        public const string VisionCameraResolutionKey = ReviewCameraResolutionKey;
        public const double DefaultVisionCameraResolution = 0.0025;
        public const string AlignCameraResolutionKey = "Align_Camera_Resolution";
        public const double DefaultAlignCameraResolution = 0.00345;
        public const string WapageAlignBaseToleranceKey = "Wapage_AlignBase_Tolerance";
        public const double DefaultWapageAlignBaseTolerance = 0.1;
        private readonly object _cacheLock = new();
        private readonly object _saveLock = new();
        private readonly Dictionary<EN_SETTING_TAB, IReadOnlyList<ST_SYSTEM_PARAMETER>> _sectionCache = new();
        public IReadOnlyList<ST_SYSTEM_PARAMETER> LoadSection(EN_SETTING_TAB section, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_cacheLock)
            {
                if (_sectionCache.TryGetValue(section, out var cachedParameters))
                {
                    return cachedParameters;
                }
            }

            return ReloadSection(section, cancellationToken);
        }

        public IReadOnlyList<ST_SYSTEM_PARAMETER> ReloadSection(EN_SETTING_TAB section, CancellationToken cancellationToken = default)
        {
            lock (_saveLock)
            {
                var loadedParameters = settingFile.Load(section, cancellationToken).ToArray();
                lock (_cacheLock)
                    _sectionCache[section] = loadedParameters;
                return loadedParameters;
            }
        }

        public void LoadAllSections(CancellationToken cancellationToken = default)
        {
            foreach (var section in Enum.GetValues<EN_SETTING_TAB>())
            {
                ReloadSection(section, cancellationToken);
            }
        }

        public string GetValue(EN_SETTING_TAB section, string name, string defaultValue = "", CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var parameters = LoadSection(section, cancellationToken);
            bool MatchesItem(global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER item)
            {
                return item.Key.Equals(name, StringComparison.OrdinalIgnoreCase) || item.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
            }

            var parameter = parameters.FirstOrDefault(MatchesItem);
            return parameter is null || string.IsNullOrWhiteSpace(parameter.Value) ? defaultValue : parameter.Value;
        }

        public void SetValue(EN_SETTING_TAB section, string name, string value, CancellationToken cancellationToken = default)
        {
            lock (_saveLock)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                var parameters = LoadSection(section, cancellationToken);
                var found = false;
                global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER SelectParameter(global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER parameter)
                {
                    var isTarget = parameter.Key.Equals(name, StringComparison.OrdinalIgnoreCase) || parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
                    if (!isTarget)
                    {
                        return parameter;
                    }

                    found = true;
                    return parameter with
                    {
                        Value = value
                    };
                }

                var editedParameters = parameters.Select(SelectParameter).ToArray();
                if (!found)
                {
                    throw new InvalidDataException($"Setting parameter is not defined: {section}/{name}");
                }

                SaveSection(section, editedParameters, cancellationToken);
            }
        }

        public int GetVisionMaxHoleCountPerLine()
        {
            // GetValue uses the shared Setting cache, never a per-command file read.
            var text = GetValue(EN_SETTING_TAB.Option, CVisionProtocol.MaxHoleCountPerLineSettingKey, CVisionProtocol.DefaultMaxHoleCountPerLine.ToString(CultureInfo.InvariantCulture));
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new InvalidDataException("Setting-Review MaxHoleCountPerLine은 정수여야 합니다.");
            return CVisionProtocol.ValidateLineCapacity(value);
        }

        public double GetVisionCameraResolution() => ParseVisionCameraResolution(GetValue(EN_SETTING_TAB.Option, VisionCameraResolutionKey, DefaultVisionCameraResolution.ToString("R", CultureInfo.InvariantCulture)));

        private static double ParseVisionCameraResolution(string text)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0)
                throw new InvalidDataException("Setting-Review Review_Camera_Resolution은 0보다 큰 유한한 mm/pixel 값이어야 합니다.");
            return value;
        }

        public void SaveSection(EN_SETTING_TAB section, IReadOnlyList<ST_SYSTEM_PARAMETER> parameters, CancellationToken cancellationToken = default)
        {
            lock (_saveLock)
            {
                if (section == EN_SETTING_TAB.Option)
                {
                    var resolution = parameters.FirstOrDefault(p => p.Key.Equals(VisionCameraResolutionKey, StringComparison.OrdinalIgnoreCase));
                    if (resolution is not null)
                        _ = ParseVisionCameraResolution(resolution.Value);
                    var alignResolution = parameters.FirstOrDefault(p => p.Key.Equals(AlignCameraResolutionKey, StringComparison.OrdinalIgnoreCase));
                    if (alignResolution is not null && (!double.TryParse(alignResolution.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var alignValue) || !double.IsFinite(alignValue) || alignValue <= 0))
                        throw new InvalidDataException("Setting-Align Align_Camera_Resolution은 0보다 큰 유한한 mm/pixel 값이어야 합니다.");
                    var tolerance = parameters.FirstOrDefault(p => p.Key.Equals(WapageAlignBaseToleranceKey, StringComparison.OrdinalIgnoreCase));
                    if (tolerance is not null && (!double.TryParse(tolerance.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var toleranceValue) || !double.IsFinite(toleranceValue) || toleranceValue < 0))
                        throw new InvalidDataException("Setting-Align Wapage_AlignBase_Tolerance는 0 이상의 유한한 mm 값이어야 합니다.");
                    var edited = CRunOptionSettings.Read(parameters);
                    var current = CRunOptionSettings.Read(LoadSection(section, cancellationToken));
                    var able = interfaceManager?.Melsec.GetCachedValue("HANDSHAKE_PRC_REVIEW_ABLE");
                    if (edited.ReviewOptions != current.ReviewOptions && able?.IsValid == true && Convert.ToDouble(able.Value, System.Globalization.CultureInfo.InvariantCulture) == 1)
                        throw new InvalidDataException("Review ABLE ON 상태에서는 리뷰 Run Option을 변경할 수 없습니다.");
                }

                settingFile.Save(section, parameters, cancellationToken);
                lock (_cacheLock)
                    _sectionCache[section] = parameters.ToArray();
            }
        }

        public IReadOnlyList<ST_SETTING_HISTORY> LoadHistory(EN_SETTING_TAB section, CancellationToken cancellationToken = default)
        {
            return settingFile.LoadHistory(section, cancellationToken);
        }

        public IReadOnlyList<ST_INTERFACE_DATA> LoadInterfaceList(CancellationToken cancellationToken = default)
        {
            return interfaceFile.LoadAll(cancellationToken);
        }

        public async Task SaveInterfaceList(IReadOnlyList<ST_INTERFACE_DATA> interfaces, CancellationToken cancellationToken = default)
        {
            interfaceFile.SaveAll(interfaces, cancellationToken);
            await interfaceManager.Reload(interfaces, reconnect: false, cancellationToken);
        }

        public Task ConnectInterface(EN_EQP_MODULE module, int number, CancellationToken cancellationToken = default)
        {
            return interfaceManager.Connect(module, number, cancellationToken: cancellationToken);
        }

        public Task DisconnectInterface(EN_EQP_MODULE module, int number, CancellationToken cancellationToken = default)
        {
            return interfaceManager.Disconnect(module, number, cancellationToken);
        }

        public Task ReconnectInterface(EN_EQP_MODULE module, int number, CancellationToken cancellationToken = default)
        {
            return interfaceManager.Reconnect(module, number, cancellationToken);
        }
    }

    /// <summary>Numeric Setting storage contract; UI labels and execution counts are decoded here.</summary>
    public static class CRunOptionSettings
    {
        public const string ZeroDefence = "RUNOPTION_ZERO_DEFENCE_USE";
        public const string CurrentGroup = "RUNOPTION_CURRENT_GROUP";
        public const string FollowUp = "RUNOPTION_FOLLOWUP_USE";
        public const string DistortionGroup0 = "RUNOPTION_DISTORTION_GROUP0_USE";
        public const string DistortionGroup1 = "RUNOPTION_DISTORTION_GROUP1_USE";
        public const string DistortionGroup2 = "RUNOPTION_DISTORTION_GROUP2_USE";
        public const string ReviewMethod = "RUNOPTION_REVIEW_METHOD";
        public const string ReviewLine = "RUNOPTION_REVIEW_LINE";
        public const string ImmediateRetry = "RUNOPTION_REVIEW_IMMEDIATE_RETRY";
        public const string RetryCount = "RUNOPTION_REVIEW_RETRY_COUNT";
        private static readonly HashSet<string> Keys = new([ZeroDefence, CurrentGroup, FollowUp, DistortionGroup0, DistortionGroup1, DistortionGroup2, ReviewMethod, ReviewLine, ImmediateRetry, RetryCount], StringComparer.OrdinalIgnoreCase);
        public static bool IsKey(string key) => Keys.Contains(key);
        public static ST_RUN_OPTION_SETTINGS Read(CSettingManager settings) => Read(settings.LoadSection(EN_SETTING_TAB.Option));
        public static ST_RUN_OPTION_SETTINGS Read(IReadOnlyList<ST_SYSTEM_PARAMETER> parameters)
        {
            var values = parameters.Where(p => IsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            return new(
                Number(ZeroDefence, 0, 0, 1) == 1, 
                Number(CurrentGroup, 1, 1, 4), 
                Number(FollowUp, 0, 0, 1) == 1, 
                Number(ReviewMethod, 1, 0, 1), 
                Number(ReviewLine, 0, 0, 3), 
                Number(ImmediateRetry, 0, 0, 1) == 1, 
                Number(RetryCount, 1, 1, 3)
            )
            {
                DistortionGroupMask = Number(DistortionGroup0, 0, 0, 1) | Number(DistortionGroup1, 0, 0, 1) << 1 | Number(DistortionGroup2, 0, 0, 1) << 2
            };
            int Number(string key, int fallback, int min, int max)
            {
                if (!values.TryGetValue(key, out var text))
                    return fallback;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
                    return value;
                throw new InvalidDataException($"{key}: {min}~{max} 범위의 정수를 입력해 주세요.");
            }
        }

        public static int EncodeLine(int count) => count switch
        {
            1 => 0,
            3 => 1,
            5 => 2,
            7 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(count))};
    }

    public sealed record ST_RUN_OPTION_SETTINGS(bool ZeroDefence, int CurrentGroup, bool FollowUp, int ReviewMethod, int ReviewLineIndex, bool ImmediateRetry, int RetryCount)
    {
        public int DistortionGroupMask { get; init; }
        public IReadOnlyList<int> DistortionGroups => Enumerable.Range(0, 3).Where(index => (DistortionGroupMask & (1 << index)) != 0).ToArray();
        public int ReviewLineCount => ReviewLineIndex switch
        {
            0 => 1,
            1 => 3,
            2 => 5,
            3 => 7,
            _ => throw new InvalidDataException("Review Line 설정 오류")};
        public ST_AUTO_REVIEW_OPTIONS ReviewOptions => new(ReviewLineCount, ReviewMethod == 0 ? EN_FINE_INSPECTION_METHOD.PtoP : EN_FINE_INSPECTION_METHOD.Flying, RetryCount, ImmediateRetry);
    }

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
    public sealed record ST_SETTING_HISTORY(DateTimeOffset ChangedAt, EN_SETTING_TAB Section, string ParameterName, string OldValue, string NewValue, string OperatorId, string Action);
    public abstract class CSettingFileBase
    {
        public abstract IReadOnlyList<ST_SYSTEM_PARAMETER> Load(EN_SETTING_TAB section, CancellationToken cancellationToken = default);
        public abstract void Save(EN_SETTING_TAB section, IReadOnlyList<ST_SYSTEM_PARAMETER> parameters, CancellationToken cancellationToken = default);
        public abstract IReadOnlyList<ST_SETTING_HISTORY> LoadHistory(EN_SETTING_TAB section, CancellationToken cancellationToken = default);
    }

    public abstract class CInterfaceFileBase
    {
        public abstract IReadOnlyList<ST_INTERFACE_DATA> LoadAll(CancellationToken cancellationToken = default);
        public abstract void SaveAll(IReadOnlyList<ST_INTERFACE_DATA> interfaces, CancellationToken cancellationToken = default);
    }
}
