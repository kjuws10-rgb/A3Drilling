using Drilling.Common.Log;
using Drilling.Common.Automation;
using Drilling.Common.Interface;
using Drilling.Common.Alarm;
using Drilling.Common.InterLock;
using Drilling.Common.Managers;
using Drilling.Common.Motion;
using Drilling.Common.Product;
using Drilling.Common.Review;
using Drilling.Common.Station;

namespace Drilling.Common.Managers;
public sealed class CManager
{
    public CPowerMeasureService PowerMeasure { get; } = new();

    private readonly string _configRoot;
    private readonly CRecipeFileBase _recipeFile;
    private readonly CSettingFileBase _settingFile;
    private readonly CManualScanFileBase _manualScanFile;
    private readonly CInterfaceFileBase _interfaceFile;
    private readonly CBETFileBase _betFile;
    private readonly CMotorFileBase _motorFile;
    private readonly CMelsecMapFileBase _melsecMapFile;
    private readonly CProductFileBase _productFile;
    private readonly CReviewResultFileBase _reviewResultFile;
    private readonly CReviewOffsetFileBase _reviewOffsetFile;
    private readonly CLogManager _logManager;
    private readonly CAutomationScriptFileBase _automationScriptFile;
    private readonly CConfigStructureFileBase? _configStructureFile;
    private readonly CInterfaceManager _interfaceManager;
    private readonly CAutomationManager _automationManager;
    private readonly CMotionManager _motionManager;
    private readonly CAlarmManager _alarmManager;
    private readonly CSharedDeviceSnapshotService<IReadOnlyList<ST_MOTOR_AXIS_STATUS>> _alarmMotorSnapshots;
    private readonly CSharedDeviceSnapshotService<ST_LASER_STATUS> _alarmLaserSnapshots;
    private readonly CSharedDeviceSnapshotService<ST_BET_STATUS> _alarmBetSnapshots;
    private readonly CInterLockManager _interLockManager = new();
    private readonly CStationManager _stationManager;
    private readonly CRecipeManager _recipeManager;
    private readonly CSettingManager _settingManager;
    private readonly CProductManager _productManager;
    private readonly CReviewManager _reviewManager;
    private readonly object _startupLock = new();
    private readonly List<string> _startupMessages = [];
    private readonly List<ST_MANAGER_STARTUP_STEP> _startupSteps = [];
    private int _startupStepNo;
    private int _loadedInterfaceCount;
    private int _loadedMotorCount;
    private int _loadedMelsecMapCount;
    private bool _activeProductLoaded;
    public CManager(
        string configRoot, 
        CRecipeFileBase recipeFile, 
        CSettingFileBase settingFile, 
        CManualScanFileBase manualScanFile, 
        CInterfaceFileBase interfaceFile, 
        CBETFileBase betFile, 
        CMotorFileBase motorFile, 
        CMelsecMapFileBase melsecMapFile, 
        CProductFileBase productFile, 
        CReviewResultFileBase reviewResultFile, 
        CReviewOffsetFileBase reviewOffsetFile, 
        CLogManager logManager, 
        CAutomationScriptFileBase automationScriptFile, 
        bool? simulationMode = null, 
        CConfigStructureFileBase? configStructureFile = null, 
        CAlarmListFileBase? alarmListFile = null
    )
    {
        _configRoot = configRoot;
        _recipeFile = recipeFile;
        _settingFile = settingFile;
        _manualScanFile = manualScanFile;
        _interfaceFile = interfaceFile;
        _betFile = betFile;
        _motorFile = motorFile;
        _melsecMapFile = melsecMapFile;
        _productFile = productFile;
        _reviewResultFile = reviewResultFile;
        _reviewOffsetFile = reviewOffsetFile;
        _logManager = logManager;
        _alarmManager = new CAlarmManager(logManager);
        _automationScriptFile = automationScriptFile;
        _configStructureFile = configStructureFile;
        CheckConfigRoot();
        if (alarmListFile is not null)
        {
            var definitions = _alarmManager.InitializeDefinitions(alarmListFile);
            AddStartupStep("Load JHMI_ALARMLIST", string.IsNullOrEmpty(definitions.Error) ? EN_MANAGER_STARTUP_RESULT.Ready : EN_MANAGER_STARTUP_RESULT.Warning, string.IsNullOrEmpty(definitions.Error) ? $"Loaded {definitions.Rows.Count} alarm definition(s) into memory." : definitions.Error);
        }

        ValidateConfigStructure();
        _interfaceManager = new CInterfaceManager(simulationMode, _logManager, _betFile);
        AddStartupStep("Create Interface Manager", EN_MANAGER_STARTUP_RESULT.Ready, "CInterfaceManager created.");
        var interfaceData = LoadInterfaceList();
        RegisterInterfaceList(interfaceData);
        _settingManager = new CSettingManager(_settingFile, _interfaceFile, _interfaceManager);
        _settingManager.LoadAllSections();
        var simulatedScanners = _interfaceManager.InitializeSimulatedScannerOutputs(_settingManager.LoadSection(EN_SETTING_TAB.Option));
        AddStartupStep("Initialize Simulated Scanner Outputs", EN_MANAGER_STARTUP_RESULT.Ready,
            $"Initialized {simulatedScanners} simulated scanner(s): Laser ON / Shutter OPEN / Gate ON.");
        _interfaceManager.ConfigureVisionLineProtocol(_settingManager.GetVisionMaxHoleCountPerLine,
            _ => _alarmManager.SetAlarm("VISION_LINE_HOLE_COUNT_EXCEEDED"));
        _interfaceManager.ConfigureAttenuatorSoftwareLimits(() => _settingManager.LoadSection(EN_SETTING_TAB.Option));
        AddStartupStep("Load Setting Cache", EN_MANAGER_STARTUP_RESULT.Ready, "Setting sections loaded to memory cache.");
        _automationManager = new CAutomationManager(_interfaceManager, _settingManager, GetProjectRoot(), GetScriptDirectory());
        AddStartupStep("Create Automation Manager", EN_MANAGER_STARTUP_RESULT.Ready, "CAutomationManager created.");
        var melsecMapData = LoadMelsecMapList();
        _interfaceManager.Melsec.ReloadMap(melsecMapData);
        AddStartupStep("Register JHMI_MELSEC_MAP", EN_MANAGER_STARTUP_RESULT.Ready, $"Registered {melsecMapData.Count} MELSEC map row(s).");
        var motorData = LoadMotorList();
        IReadOnlyList<ST_IO_DATA> ioData = [];
        _motionManager = new CMotionManager(_interfaceManager, motorData, ioData, isSimulation: GetMotionSimulationMode(simulationMode));
        _alarmMotorSnapshots = new(_motionManager.GetAxisStatusAsync);
        _alarmLaserSnapshots = new(_interfaceManager.ReadLaserStatusAsync);
        _alarmBetSnapshots = new(_interfaceManager.ReadBetStatusAsync);
        AddStartupStep("Create Motion Manager", EN_MANAGER_STARTUP_RESULT.Ready, $"CMotionManager created. Axis={motorData.Count}, Simul={_motionManager.IsSimulation}");
        _productManager = new CProductManager(_productFile, _logManager);
        AddStartupStep("Create Product Manager", EN_MANAGER_STARTUP_RESULT.Ready, "CProductManager created.");
        LoadActiveProduct();
        _reviewManager = new CReviewManager(_reviewResultFile, _interfaceManager, _settingManager, _productManager, _logManager);
        AddStartupStep("Create Review Manager", EN_MANAGER_STARTUP_RESULT.Ready, "CReviewManager created.");
        _recipeManager = new CRecipeManager(_recipeFile);
        _stationManager = new CStationManager(
            _interfaceManager, 
            _motionManager, 
            _interLockManager, 
            _settingManager, 
            _automationScriptFile, 
            _automationManager, 
            _reviewOffsetFile, 
            _productManager, 
            _logManager, 
            GetScriptDirectory(), 
            _recipeManager, 
            _reviewManager
        );
        AddStartupStep("Create Station Manager", EN_MANAGER_STARTUP_RESULT.Ready, $"Script={GetScriptDirectory()}");
        _stationManager.StageRequests.Failed += _alarmManager.SetSequenceFailure;
        _interfaceManager.StageReviewSequences.Failed += _alarmManager.SetSequenceFailure;
        AddStartupStep("Create Menu Managers", EN_MANAGER_STARTUP_RESULT.Ready, "Recipe/Setting managers created.");
    }

    public string ConfigRoot
    {
        get
        {
            return _configRoot;
        }
    }

    public IReadOnlyList<string> StartupMessages
    {
        get
        {
            lock (_startupLock)
            {
                return _startupMessages.ToArray();
            }
        }
    }

    public IReadOnlyList<ST_MANAGER_STARTUP_STEP> StartupSteps
    {
        get
        {
            lock (_startupLock)
            {
                return _startupSteps.ToArray();
            }
        }
    }

    public ST_CONFIG_LOAD_STATUS ConfigStatus()
    {
        lock (_startupLock)
        {
            return new ST_CONFIG_LOAD_STATUS(_configRoot, _loadedInterfaceCount, _loadedMotorCount, _loadedMelsecMapCount, _activeProductLoaded, _startupMessages.ToArray(), _startupSteps.ToArray());
        }
    }

    public bool IsSimul(int systemId = 0)
    {
        return _interfaceManager.IsSimulation && _motionManager.IsSimulation;
    }

    public bool IsNotSimul(int systemId = 0)
    {
        return !IsSimul(systemId);
    }

    public async Task SetSimul(bool enabled, CancellationToken cancellationToken = default)
    {
        await _interfaceManager.SetSimulationMode(enabled, cancellationToken);
        _motionManager.SetSimulationMode(enabled);
    }

    public Task SetSimul(int systemId, bool enabled, CancellationToken cancellationToken = default)
    {
        return SetSimul(enabled, cancellationToken);
    }

    public async Task Initialize(CancellationToken cancellationToken = default)
    {
        global::System.Threading.Tasks.Task SelectValue()
        {
            return _interfaceManager.Initialize(cancellationToken);
        }

        await RunInitializeStep("Initialize Interface Connection", SelectValue, cancellationToken);
        global::System.Threading.Tasks.Task SelectTask()
        {
            return _motionManager.Initialize(cancellationToken);
        }

        await RunInitializeStep("Initialize Motion Controller", SelectTask, cancellationToken);
    }

    public async Task Destroy(CancellationToken cancellationToken = default)
    {
        try
        {
            await StopBackgroundMonitoring();
            try
            {
                await _alarmManager.FlushHistoryAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException exception)
            {
                CProgramOpenLog.Write("ALARM_HISTORY_FLUSH_TIMEOUT", exception);
            }

            await PowerMeasure.DisposeAsync();
            await _motionManager.Destroy(cancellationToken);
            await _interfaceManager.Destroy(cancellationToken);
        }
        finally
        {
            try
            {
                await _logManager.StopSequenceLoggingAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error)
            {
                CProgramOpenLog.Write("SEQUENCE_LOG_FLUSH_FAILED", error);
            }
        }
    }

    public void StartBackgroundMonitoring()
    {
        _stationManager.StartZeroLinePrewaitMonitoring(() => _settingManager.GetValue(EN_SETTING_TAB.Option, "SELECT_RECIPE", "DRILL_A01"));
        _stationManager.StartProtectWindowCompletionMonitoring(() => _settingManager.GetValue(EN_SETTING_TAB.Option, "SELECT_RECIPE", "DRILL_A01"));
        _alarmManager.EnsureDefinitionsRequested();
        _alarmMotorSnapshots.Start();
        _alarmLaserSnapshots.Start();
        _alarmBetSnapshots.Start();
        _alarmManager.StartMonitoring(_settingManager, _automationManager, ReadAlarmDeviceSnapshot, _interLockManager, exception => CProgramOpenLog.Write("ALARM_MONITOR_FAILED", exception));
    }

    public async Task StopBackgroundMonitoring()
    {
        await _stationManager.StageRequests.StopAsync().ConfigureAwait(false);
        await _stationManager.StopZeroLinePrewaitMonitoring().ConfigureAwait(false);
        await _stationManager.StopProtectWindowCompletionMonitoring().ConfigureAwait(false);
        await _alarmManager.StopMonitoringAsync().ConfigureAwait(false);
        await Task.WhenAll(_alarmMotorSnapshots.StopAsync(), _alarmLaserSnapshots.StopAsync(), _alarmBetSnapshots.StopAsync()).ConfigureAwait(false);
        _automationManager.StatusSnapshots.Dispose();
    }

    private ST_DEVICE_STATUS ReadAlarmDeviceSnapshot()
    {
        // Memory-only: neither a blocked SDK nor a disconnected serial device can hold the alarm worker.
        var motors = _alarmMotorSnapshots.GetSnapshot();
        var laser = _alarmLaserSnapshots.GetSnapshot();
        var bet = _alarmBetSnapshots.GetSnapshot();
        var errors = new List<string>();
        if (motors.State != "OK")
            errors.Add($"MOTION {motors.State}: {motors.Detail}");
        if (laser.State != "OK")
            errors.Add($"LASER {laser.State}: {laser.Detail}");
        if (bet.State != "OK")
            errors.Add($"BET {bet.State}: {bet.Detail}");
        var attenuatorNumber = _interfaceManager.GetInterfaceList(EN_EQP_MODULE.Attenuator).OrderBy(row => row.Number).FirstOrDefault()?.Number ?? 0;
        return new(
            [], 
            motors.Value ?? [], 
            laser.Value ?? new(false, false, false, 0), 
            _interfaceManager.GetCachedAttenuatorStatus(attenuatorNumber), 
            bet.Value ?? new(0, 0, 0, 0, 0, 0, false, false, false, false), 
            _interfaceManager.GetPowerMeterStatus()
        )
        {
            SnapshotError = string.Join(Environment.NewLine, errors)
        };
    }

    public Task<int> ConnectInterface(CancellationToken cancellationToken = default)
    {
        return _interfaceManager.Connect(cancellationToken: cancellationToken);
    }

    public Task<int> DisconnectInterface(CancellationToken cancellationToken = default)
    {
        return _interfaceManager.Disconnect(cancellationToken);
    }

    public Task ReconnectInterface(EN_EQP_MODULE module, int number, CancellationToken cancellationToken = default)
    {
        return _interfaceManager.Reconnect(module, number, cancellationToken);
    }

    public CStationManager Station()
    {
        return _stationManager;
    }

    public CRecipeManager Recipe()
    {
        return _recipeManager;
    }

    public CSettingManager Setting()
    {
        return _settingManager;
    }

    public CProductManager Product()
    {
        return _productManager;
    }

    public CReviewManager Review()
    {
        return _reviewManager;
    }

    public CInterfaceManager Interface()
    {
        return _interfaceManager;
    }

    public CAutomationManager Automation()
    {
        return _automationManager;
    }

    public CAutomationManager automation
    {
        get
        {
            return _automationManager;
        }
    }

    public CMotionManager Motion()
    {
        return _motionManager;
    }

    public CAlarmManager Alarm()
    {
        return _alarmManager;
    }

    public CInterLockManager InterLock()
    {
        return _interLockManager;
    }

    public CLogManager Log()
    {
        return _logManager;
    }

    public CRecipeFileBase RecipeFile()
    {
        return _recipeFile;
    }

    public CSettingFileBase SettingFile()
    {
        return _settingFile;
    }

    public CManualScanFileBase ManualScanFile()
    {
        return _manualScanFile;
    }

    public CInterfaceFileBase InterfaceFile()
    {
        return _interfaceFile;
    }

    public CBETFileBase BETFile()
    {
        return _betFile;
    }

    public CMotorFileBase MotorFile()
    {
        return _motorFile;
    }

    public CMelsecMapFileBase MelsecMapFile()
    {
        return _melsecMapFile;
    }

    public CProductFileBase ProductFile()
    {
        return _productFile;
    }

    public CReviewResultFileBase ReviewResultFile()
    {
        return _reviewResultFile;
    }

    public CReviewOffsetFileBase ReviewOffsetFile()
    {
        return _reviewOffsetFile;
    }

    private void CheckConfigRoot()
    {
        if (Directory.Exists(_configRoot))
        {
            AddStartupStep("Check Config Root", EN_MANAGER_STARTUP_RESULT.Ready, _configRoot);
            return;
        }

        AddStartupStep("Check Config Root", EN_MANAGER_STARTUP_RESULT.Warning, $"Config root was not found: {_configRoot}");
    }

    private void ValidateConfigStructure()
    {
        if (_configStructureFile is null)
        {
            AddStartupStep("Validate Config Structure", EN_MANAGER_STARTUP_RESULT.Warning, "Config structure validator was not registered.");
            return;
        }

        try
        {
            var statuses = _configStructureFile.Validate().GetAwaiter().GetResult();
            foreach (var status in statuses)
            {
                AddStartupStep($"Check {status.ItemName}", ToStartupResult(status), FormatConfigStatusMessage(status));
            }
        }
        catch (Exception ex)when (IsStartupDataException(ex))
        {
            AddStartupFailure("Validate Config Structure", ex);
        }
    }

    private IReadOnlyList<ST_INTERFACE_DATA> LoadInterfaceList()
    {
        try
        {
            var interfaceData = _interfaceFile.LoadAll();
            _loadedInterfaceCount = interfaceData.Count;
            AddStartupStep("Load JHMI_INTERFACE", EN_MANAGER_STARTUP_RESULT.Ready, $"Loaded {interfaceData.Count} interface row(s).");
            return interfaceData;
        }
        catch (Exception ex)when (IsStartupDataException(ex))
        {
            AddStartupFailure("Load JHMI_INTERFACE", ex);
            return[];
        }
    }

    private void RegisterInterfaceList(IReadOnlyList<ST_INTERFACE_DATA> interfaceData)
    {
        var registeredCount = 0;
        foreach (var data in interfaceData)
        {
            try
            {
                _interfaceManager.Register(data);
                registeredCount++;
            }
            catch (Exception ex)when (ex is InvalidOperationException or InvalidDataException)
            {
                AddStartupFailure($"Register Interface {data.Device}[{data.Number}]", ex);
            }
        }

        AddStartupStep("Register Interface List", registeredCount == interfaceData.Count ? EN_MANAGER_STARTUP_RESULT.Ready : EN_MANAGER_STARTUP_RESULT.Warning, $"Registered {registeredCount}/{interfaceData.Count} interface device(s).");
    }

    private IReadOnlyList<ST_MOTOR_DATA> LoadMotorList()
    {
        try
        {
            var motorData = _motorFile.LoadAll();
            _loadedMotorCount = motorData.Count;
            AddStartupStep("Load JHMI_MOTOR", EN_MANAGER_STARTUP_RESULT.Ready, $"Loaded {motorData.Count} motor row(s).");
            return motorData;
        }
        catch (Exception ex)when (IsStartupDataException(ex))
        {
            AddStartupFailure("Load JHMI_MOTOR", ex);
            return[];
        }
    }

    private IReadOnlyList<ST_MELSEC_MAP_DATA> LoadMelsecMapList()
    {
        try
        {
            var melsecMapData = _melsecMapFile.LoadAll();
            _loadedMelsecMapCount = melsecMapData.Count;
            AddStartupStep("Load JHMI_MELSEC_MAP", EN_MANAGER_STARTUP_RESULT.Ready, $"Loaded {melsecMapData.Count} MELSEC map row(s).");
            return melsecMapData;
        }
        catch (Exception ex)when (IsStartupDataException(ex))
        {
            AddStartupFailure("Load JHMI_MELSEC_MAP", ex);
            return[];
        }
    }

    private void LoadActiveProduct()
    {
        try
        {
            _productManager.LoadActive().GetAwaiter().GetResult();
            _productManager.RecoverInterrupted().GetAwaiter().GetResult();
            _activeProductLoaded = true;
            AddStartupStep("Load Active Product", EN_MANAGER_STARTUP_RESULT.Ready, "Active product loaded.");
        }
        catch (Exception ex)when (IsStartupDataException(ex))
        {
            AddStartupFailure("Load Active Product", ex);
        }
    }

    private async Task RunInitializeStep(string stepName, Func<Task> action, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await action();
            AddStartupStep(stepName, EN_MANAGER_STARTUP_RESULT.Ready, "OK");
        }
        catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
        {
            AddStartupStep(stepName, EN_MANAGER_STARTUP_RESULT.Warning, "Canceled.");
        }
        catch (Exception ex)when (IsStartupRuntimeException(ex))
        {
            AddStartupFailure(stepName, ex);
        }
    }

    private void AddStartupFailure(string stepName, Exception exception)
    {
        AddStartupStep(stepName, EN_MANAGER_STARTUP_RESULT.Failed, exception.Message);
    }

    private void AddStartupStep(string stepName, EN_MANAGER_STARTUP_RESULT result, string message)
    {
        lock (_startupLock)
        {
            _startupSteps.Add(new ST_MANAGER_STARTUP_STEP(++_startupStepNo, stepName, result, message));
            if (result != EN_MANAGER_STARTUP_RESULT.Ready && !string.IsNullOrWhiteSpace(message))
            {
                _startupMessages.Add($"{stepName}: {message}");
            }
        }
    }

    private bool GetMotionSimulationMode(bool? simulationMode)
    {
        bool MatchesItem(global::Drilling.Common.Interface.ST_INTERFACE_DATA? data)
        {
            return data is null || data.IsSimulation;
        }

        return simulationMode ?? _interfaceManager.GetInterfaceList(EN_EQP_MODULE.Motion).DefaultIfEmpty().All(MatchesItem);
    }

    private string GetScriptDirectory()
    {
        bool MatchesItem(global::Drilling.Common.Managers.ST_SYSTEM_PARAMETER parameter)
        {
            return parameter.Key.Equals("LocalScriptPath", StringComparison.OrdinalIgnoreCase) || parameter.Name.Equals("LocalScriptPath", StringComparison.OrdinalIgnoreCase);
        }

        var settingPath = _settingFile.Load(EN_SETTING_TAB.Option).FirstOrDefault(MatchesItem)?.Value;
        return ResolveProjectPath(settingPath, Path.Combine("Data", "Script"));
    }

    private string GetProjectRoot()
    {
        return Directory.GetParent(_configRoot)?.FullName ?? _configRoot;
    }

    private string ResolveProjectPath(string? settingPath, string defaultRelativePath)
    {
        var path = string.IsNullOrWhiteSpace(settingPath) ? defaultRelativePath : settingPath.Trim();
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(GetProjectRoot(), path));
    }

    private static bool IsStartupDataException(Exception exception)
    {
        return exception is InvalidDataException or IOException or UnauthorizedAccessException;
    }

    private static bool IsStartupRuntimeException(Exception exception)
    {
        return exception is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException;
    }

    private static EN_MANAGER_STARTUP_RESULT ToStartupResult(ST_CONFIG_FILE_STATUS status)
    {
        if (status.IsValid)
        {
            return EN_MANAGER_STARTUP_RESULT.Ready;
        }

        return status.Required ? EN_MANAGER_STARTUP_RESULT.Failed : EN_MANAGER_STARTUP_RESULT.Warning;
    }

    private static string FormatConfigStatusMessage(ST_CONFIG_FILE_STATUS status)
    {
        var requiredText = status.Required ? "Required" : "Optional";
        var existsText = status.Exists ? "Exists" : "Missing";
        return $"{requiredText}, {existsText}, {status.Message} | {status.Path}";
    }
}

public enum EN_SYSTEM_MODE
{
    Simulation,
    Auto,
    Manual
}

public enum EN_PM_LOCK_STATE
{
    Released,
    Locked
}

public enum EN_MANAGER_STARTUP_RESULT
{
    Ready,
    Warning,
    Failed
}

public sealed record ST_MANUAL_SCAN_PARAM(
    double ShapeSize, 
    double OffsetX, 
    double OffsetY, 
    string Direction, 
    string ShapeName, 
    double LaserPower = 1.0, 
    double JumpSpeed = 1.5, 
    double MarkSpeed = 0.9, 
    double AttenuatorPosition = 23.50, 
    double LaserFrequency = 20.0, 
    double LaserOnDelay = 8.0, 
    double LaserOffDelay = 12.0, 
    double Time = 1000.0, 
    int Count = 1, 
    int GridRowLines = 5, 
    int GridColLines = 5, 
    string LaserOnMode = "TIME", 
    int RepeatCount = 1, 
    double RepeatDelayTime = 0.0
);
public sealed record ST_MANUAL_SCAN_FORM(
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
    int DisplayOrder
);
public sealed record ST_POWER_METER_STATUS(
    double MeasuredPower, 
    string Unit, 
    DateTimeOffset MeasuredAt, 
    double AveragePower = 0.0, 
    double MinPower = 0.0, 
    double MaxPower = 0.0, 
    double WaveLengthNm = 355.0, 
    double BeamPositionX = 0.0, 
    double BeamPositionY = 0.0, 
    int SampleCount = 0, 
    bool IsMeasuring = false, 
    string ModelName = "PowerMax", 
    string SerialNumber = "-", 
    string LastCommand = "", 
    EN_POWER_METER_ERROR LastError = EN_POWER_METER_ERROR.Ok
)
{
    public static ST_POWER_METER_STATUS Empty
    {
        get
        {
            return new(0.0, "W", DateTimeOffset.Now);
        }
    }
}

public sealed record ST_DEVICE_STATUS(
    IReadOnlyList<ST_IO_STATUS> Io, 
    IReadOnlyList<ST_MOTOR_AXIS_STATUS> Motors, 
    ST_LASER_STATUS Laser, 
    ST_ATTENUATOR_STATUS Attenuator, 
    ST_BET_STATUS Bet, 
    ST_POWER_METER_STATUS PowerMeter
)
{
    public string SnapshotError { get; init; } = "";
}

public sealed record ST_SYSTEM_STATUS(string CurrentRecipeId, EN_SYSTEM_MODE OperationMode, EN_ALARM_STATE AlarmState, EN_PM_LOCK_STATE PMLockState, IReadOnlyList<ST_DEVICE_COMM_STATUS> Modules)
{
    public ST_DEVICE_COMM_STATUS GetModule(EN_EQP_MODULE module)
    {
        foreach (var status in Modules)
        {
            if (status.Module == module)
            {
                return status;
            }
        }

        return new ST_DEVICE_COMM_STATUS(module, EN_COMM_STATE.Offline);
    }
}

public sealed record ST_PM_LOCK_STATUS(bool IsLocked, DateTimeOffset? LockedAt);
public sealed record ST_MANAGER_STARTUP_STEP(int Order, string StepName, EN_MANAGER_STARTUP_RESULT Result, string Message);
public sealed record ST_CONFIG_LOAD_STATUS(
    string ConfigRoot, 
    int InterfaceCount, 
    int MotorCount, 
    int MelsecMapCount, 
    bool ActiveProductLoaded, 
    IReadOnlyList<string> StartupMessages, 
    IReadOnlyList<ST_MANAGER_STARTUP_STEP> StartupSteps
);
public sealed record ST_CONFIG_FILE_STATUS(string ItemName, string Path, bool Required, bool Exists, bool IsValid, string Message);
public abstract class CConfigStructureFileBase
{
    public abstract Task<IReadOnlyList<ST_CONFIG_FILE_STATUS>> Validate(CancellationToken cancellationToken = default);
}

public abstract class CManualScanFileBase
{
    public abstract Task<IReadOnlyList<string>> List(CancellationToken cancellationToken = default);
    public abstract Task<IReadOnlyList<ST_MANUAL_SCAN_FORM>> LoadForm(CancellationToken cancellationToken = default);
    public abstract Task<ST_MANUAL_SCAN_PARAM> Load(CancellationToken cancellationToken = default);
    public abstract Task<ST_MANUAL_SCAN_PARAM> Load(string settingName, CancellationToken cancellationToken = default);
    public abstract Task Save(ST_MANUAL_SCAN_PARAM settings, CancellationToken cancellationToken = default);
    public abstract Task Save(string settingName, ST_MANUAL_SCAN_PARAM settings, CancellationToken cancellationToken = default);
    public abstract Task Rename(string oldSettingName, string newSettingName, CancellationToken cancellationToken = default);
    public abstract Task Delete(string settingName, CancellationToken cancellationToken = default);
}
