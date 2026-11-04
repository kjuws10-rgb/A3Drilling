namespace Drilling.Common.Automation
{
    using System.Globalization;
    using Aerotech.Automation1.DotNet;
    using System.Diagnostics;
    using System.Collections.Concurrent;
    using System.Text.Json;
    using Drilling.Common.Interface;
    using Drilling.Common.Managers;
    using System.Collections.ObjectModel;

    public partial class CAutomationManager
    {
#region 공유 상태·초기값
        private readonly ConcurrentDictionary<string, double> _simulationConfiguredHomeSpeeds = new(StringComparer.OrdinalIgnoreCase);
        private const string LocalScriptPathKey = "LocalScriptPath";
        private const string AutomationScriptPathKey = "AutomationScriptPath";
        private const long Automation1DriveStatusEnabledMask = 1L;
        private const long Automation1AxisStatusHomedMask = 1L;
        private static readonly IReadOnlyDictionary<string, int> AxisNoMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["GX"] = 0,
            ["GY"] = 1
        };
        private readonly CInterfaceManager _interfaceManager;
        private readonly CSettingManager? _settingManager;
        private readonly string _projectRoot;
        private readonly string _defaultLocalScriptDirectory;
        private readonly ConcurrentDictionary<string, bool> _simulationAxisAbleStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, double> _simulationAxisPositions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<(int Controller, int Task), CSimulatedMofReadiness> _simulatedMofReadiness = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Controller, int Task), CStageSimulatedTask> _stageSimulationTasks = new();
#endregion
#region 생성·초기화
        public CAutomationManager(CInterfaceManager interfaceManager, CSettingManager? settingManager = null, string projectRoot = "", string defaultLocalScriptDirectory = "")
        {
            _interfaceManager = interfaceManager;
            StatusSnapshots = new CAutomationStatusSnapshotService(this);
            _settingManager = settingManager;
            _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? AppContext.BaseDirectory : projectRoot;
            _defaultLocalScriptDirectory = string.IsNullOrWhiteSpace(defaultLocalScriptDirectory) ? Path.Combine(_projectRoot, "Data", "Script") : defaultLocalScriptDirectory;
        }

#endregion
#region 기본 처리
        public CAutomationStatusSnapshotService StatusSnapshots { get; }

        public Task Connect(int number = 0, CancellationToken cancellationToken = default)
        {
            return _interfaceManager.Connect(EN_EQP_MODULE.Automation1, number, cancellationToken: cancellationToken);
        }

        public Task Disconnect(int number = 0, CancellationToken cancellationToken = default)
        {
            return _interfaceManager.Disconnect(EN_EQP_MODULE.Automation1, number, cancellationToken);
        }

        public bool IsConnect(int number = 0)
        {
            return _interfaceManager.IsConnect(EN_EQP_MODULE.Automation1, number);
        }

        public bool IsSimul(int number = 0)
        {
            return _interfaceManager.IsSimul(EN_EQP_MODULE.Automation1, number);
        }

        public Task<string> ReadStatus(int number = 0, CancellationToken cancellationToken = default)
        {
            return ExecuteRaw("AUTOMATION1:STATUS", number, cancellationToken);
        }

        public async Task<IReadOnlyList<string>> ReadControllerAxes(int number = 0, CancellationToken cancellationToken = default)
        {
            var response = await ExecuteRaw("AUTOMATION1:SYSTEM:AXES", number, cancellationToken, suppressLog: true);
            const string prefix = "OK:AXES:";
            if (!response.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Controller axis list could not be read: {response}");
            return response[prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public Task<string> ReadProcessorUsage(int number = 0, CancellationToken cancellationToken = default)
        {
            return ExecuteRaw("AUTOMATION1:SYSTEM:PROCESSOR_USAGE", number, cancellationToken);
        }

        public Task<string> ResetController(int number = 0, CancellationToken cancellationToken = default)
        {
            if (IsSimul(number))
            {
                return Task.FromResult($"OK:SYSTEM:RESET:{number.ToString(CultureInfo.InvariantCulture)}");
            }

            return ExecuteRaw("AUTOMATION1:SYSTEM:RESET", number, cancellationToken);
        }

        public Task<string> ReadIo(string address, int number = 0, CancellationToken cancellationToken = default)
        {
            var ioAddress = ValidateIoAddress(address, outputOnly: false);
            return ExecuteRaw($"AUTOMATION1:IO:{ioAddress}:READ", number, cancellationToken);
        }

        public Task<string> SetIo(string address, bool isOn, int number = 0, CancellationToken cancellationToken = default)
        {
            var ioAddress = ValidateIoAddress(address, outputOnly: true);
            return ExecuteRaw($"AUTOMATION1:IO:{ioAddress}:{(isOn ? "ON" : "OFF")}", number, cancellationToken);
        }

        private static string ValidateIoAddress(string address, bool outputOnly)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(address);
            var value = address.Trim();
            bool MatchesItem(char character)
            {
                return character is ':' or '|' or '\r' or '\n' || char.IsControl(character) || char.IsWhiteSpace(character);
            }

            if (value.Any(MatchesItem))
            {
                throw new InvalidOperationException($"Automation1 IO address contains an invalid command delimiter: {address}");
            }

            var normalized = value.ToUpperInvariant();
            var parts = normalized.Split(['.', '/', ','], StringSplitOptions.None);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException($"Automation1 IO address is invalid: {address}. Use A0.DO1, A0.DI1, or 0.1.");
            }

            var axisText = TrimIoAxisPrefix(parts[0]);
            var bitText = parts[1];
            var isOutput = false;
            if (bitText.StartsWith("DO", StringComparison.Ordinal))
            {
                isOutput = true;
                bitText = bitText[2..];
            }
            else if (bitText.StartsWith("DI", StringComparison.Ordinal))
            {
                bitText = bitText[2..];
            }
            else if (bitText.StartsWith("Y", StringComparison.Ordinal))
            {
                isOutput = true;
                bitText = bitText[1..];
            }
            else if (bitText.StartsWith("X", StringComparison.Ordinal))
            {
                bitText = bitText[1..];
            }

            if (outputOnly && !isOutput)
            {
                throw new InvalidOperationException($"Automation1 IO output command can use DO/Y address only: {address}");
            }

            if (!IsUnsignedInteger(axisText) || !IsUnsignedInteger(bitText) || !int.TryParse(axisText, NumberStyles.None, CultureInfo.InvariantCulture, out var axisNo) || !int.TryParse(bitText, NumberStyles.None, CultureInfo.InvariantCulture, out var bitNo) || axisNo < 0 || bitNo < 0 || bitNo > 63)
            {
                throw new InvalidOperationException($"Automation1 IO address is invalid: {address}. Use A0.DO1, A0.DI1, or 0.1.");
            }

            return value;
        }

        private static string TrimIoAxisPrefix(string axisText)
        {
            if (axisText.StartsWith("AXIS", StringComparison.Ordinal))
            {
                return axisText[4..];
            }

            return axisText.StartsWith("A", StringComparison.Ordinal) ? axisText[1..] : axisText;
        }

        private static bool IsUnsignedInteger(string value)
        {
            return value.Length > 0 && value.All(char.IsAsciiDigit);
        }

        private Task<string> ExecuteRaw(string command, int number, CancellationToken cancellationToken, bool suppressLog = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            return suppressLog ? _interfaceManager.ExecuteFunctionNoLog(EN_EQP_MODULE.Automation1, number, command, cancellationToken) : _interfaceManager.ExecuteFunction(EN_EQP_MODULE.Automation1, number, command, cancellationToken);
        }

        public Task<string> Move(string axisName, double targetPosition, double velocity = 100.0, int number = 0, CancellationToken cancellationToken = default)
        {
            return ExecuteAxis(axisName, $"MOVE_ABS:{Format(targetPosition)}:{Format(velocity)}", number, cancellationToken);
        }

        public Task<string> MoveRel(string axisName, double distance, double velocity = 100.0, int number = 0, CancellationToken cancellationToken = default)
        {
            return ExecuteAxis(axisName, $"MOVE_REL:{Format(distance)}:{Format(velocity)}", number, cancellationToken);
        }

        public Task<string> Stop(string axisName, int number = 0, CancellationToken cancellationToken = default)
        {
            return ExecuteAxis(axisName, "STOP", number, cancellationToken);
        }

        public Task<string> ManualMove(string axisName, double value, double velocity, bool relative, int number, CancellationToken cancellationToken = default)
        {
            if (!double.IsFinite(value) || !double.IsFinite(velocity) || velocity <= 0)
                throw new ArgumentOutOfRangeException(nameof(velocity), "Position and speed must be finite; speed must be positive.");
            return ExecuteAxis(axisName, $"MANUAL_{(relative ? "REL" : "ABS")}:{Format(value)}:{Format(velocity)}", number, cancellationToken);
        }

        public Task<string> JogStart(string axisName, double velocity, int number)
        {
            if (!double.IsFinite(velocity) || velocity == 0)
                throw new ArgumentOutOfRangeException(nameof(velocity));
            return ExecuteAxis(axisName, $"JOG_START:{Format(velocity)}", number, default);
        }

        public Task<string> JogStop(string axisName, int number) => ExecuteAxis(axisName, "JOG_STOP", number, default);
        public Task<string> AbortMotion(string axisName, int number, CancellationToken cancellationToken = default) => ExecuteAxis(axisName, "ABORT_MOTION", number, cancellationToken);
        public Task<string> ServoOn(string axisName, int number = 0, CancellationToken cancellationToken = default)
        {
            return ExecuteAxis(axisName, "SERVO_ON", number, cancellationToken);
        }

        public Task<string> ServoOff(string axisName, int number = 0, CancellationToken cancellationToken = default)
        {
            return ExecuteAxis(axisName, "SERVO_OFF", number, cancellationToken);
        }

        public Task<string> Home(string axisName, int number = 0, CancellationToken cancellationToken = default)
        {
            return ExecuteAxis(axisName, "HOME", number, cancellationToken);
        }

        public Task<string> HomeAsync(string axisName, int number = 0, CancellationToken cancellationToken = default)
        {
            return ExecuteAxis(axisName, "HOME_ASYNC", number, cancellationToken);
        }

        public Task<string> ResetAlarm(string axisName, int number = 0, CancellationToken cancellationToken = default)
        {
            return ExecuteAxis(axisName, "RESET_ALARM", number, cancellationToken);
        }

        public Task<string> ReadAxis(string axisName, int number = 0, CancellationToken cancellationToken = default)
        {
            return ExecuteAxis(axisName, "READ", number, cancellationToken);
        }

        public ST_AUTOMATION_AXIS_STATUS ReadAxisStatus(string axisName, int number = 0, CancellationToken cancellationToken = default)
        {
            return ReadAxisStatus(axisName, number, 0, cancellationToken);
        }

        public ST_AUTOMATION_AXIS_STATUS ReadAxisStatus(string axisName, int number, int analogInputNo, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(axisName);
            ValidateAnalogInputNo(analogInputNo);
            var normalizedAxis = axisName.Trim();
            if (IsSimul(number))
            {
                return CreateSimulationAxisStatus(number, normalizedAxis);
            }

            var response = ReadAxis(normalizedAxis, number, analogInputNo, cancellationToken).GetAwaiter().GetResult();
            return ParseAxisStatus(number, normalizedAxis, response);
        }

        public ST_AUTOMATION_AXIS_STATUS ReadAxisStatusByName(string axisName, int number = 0, CancellationToken cancellationToken = default)
        {
            return ReadAxisStatusByName(axisName, number, 0, cancellationToken);
        }

        public ST_AUTOMATION_AXIS_STATUS ReadAxisStatusByName(string axisName, int number, int analogInputNo, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(axisName);
            ValidateAnalogInputNo(analogInputNo);
            var normalizedAxis = axisName.Trim();
            ValidateAxisNameOnly(normalizedAxis);
            if (IsSimul(number))
            {
                return CreateSimulationAxisStatus(number, normalizedAxis);
            }

            var response = ReadAxisByName(normalizedAxis, number, analogInputNo, cancellationToken).GetAwaiter().GetResult();
            return ParseAxisStatus(number, normalizedAxis, response);
        }

        public ST_AUTOMATION_AXIS_STATUS ReadAxisStatusByNameNoLog(string axisName, int number, int analogInputNo, bool readExtendedStatus, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(axisName);
            ValidateAnalogInputNo(analogInputNo);
            var normalizedAxis = axisName.Trim();
            ValidateAxisNameOnly(normalizedAxis);
            if (IsSimul(number))
            {
                return CreateSimulationAxisStatus(number, normalizedAxis);
            }

            var response = ReadAxisByName(normalizedAxis, number, analogInputNo, readExtendedStatus, cancellationToken, suppressLog: true).GetAwaiter().GetResult();
            return ParseAxisStatus(number, normalizedAxis, response);
        }

        public async Task<string> RunLocalScript(string localScriptPath, string controllerFileName = "", int taskIndex = 1, int number = 0, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localScriptPath);
            ValidateTaskIndex(taskIndex);
            var resolvedLocalScriptPath = ResolveLocalScriptPath(localScriptPath, cancellationToken);
            var resolvedControllerFileName = string.IsNullOrWhiteSpace(controllerFileName) ? CreateControllerFileName(localScriptPath, cancellationToken) : CreateControllerFileName(controllerFileName, cancellationToken);
            if (IsSimul(number))
            {
                return CreateSimulationTaskStatus(taskIndex, "Idle", resolvedControllerFileName);
            }

            return await ExecuteRaw($"AUTOMATION1:SCRIPT|RUN_LOCAL|{taskIndex}|{resolvedLocalScriptPath}|{resolvedControllerFileName}", number, cancellationToken);
        }

        public virtual async Task<string> UploadScript(string localScriptPath, string scriptFileName = "", int number = 0, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localScriptPath);
            var resolvedLocalScriptPath = ResolveLocalScriptPath(localScriptPath, cancellationToken);
            var controllerFileName = string.IsNullOrWhiteSpace(scriptFileName) ? CreateControllerFileName(localScriptPath, cancellationToken) : CreateControllerFileName(scriptFileName, cancellationToken);
            return await ExecuteRaw($"AUTOMATION1:SCRIPT|UPLOAD|{resolvedLocalScriptPath}|{controllerFileName}", number, cancellationToken);
        }

        public virtual async Task<string> RunScript(string scriptFileName, int taskIndex = 1, int number = 0, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(scriptFileName);
            ValidateTaskIndex(taskIndex);
            var controllerFileName = CreateControllerFileName(scriptFileName, cancellationToken);
            if (IsSimul(number))
            {
                return CreateSimulationTaskStatus(taskIndex, "Idle", controllerFileName);
            }

            return await ExecuteRaw($"AUTOMATION1:SCRIPT|RUN|{taskIndex}|{controllerFileName}", number, cancellationToken);
        }

        public async Task<string> RunScriptIfIdle(string scriptFileName, int taskIndex = 1, int number = 0, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(scriptFileName);
            ValidateTaskIndex(taskIndex);
            var controllerFileName = CreateControllerFileName(scriptFileName, cancellationToken);
            if (IsSimul(number))
                return CreateSimulationTaskStatus(taskIndex, "Idle", controllerFileName);
            return await ExecuteRaw($"AUTOMATION1:SCRIPT|RUN_IF_IDLE|{taskIndex}|{controllerFileName}", number, cancellationToken);
        }

        public async Task<string> RunBufferedScript(
            string localScriptPath, 
            string scriptFileName = "", 
            int taskIndex = 1, 
            int number = 0, 
            int queueSize = 100, 
            int linesPerCommand = 1000, 
            int timeoutMs = 600000, 
            CancellationToken cancellationToken = default
        )
        {
            return await RunBufferedScripts([new ST_BUFFERED_SCRIPT_RUN_ITEM(localScriptPath, scriptFileName, taskIndex)], number, queueSize, linesPerCommand, timeoutMs, cancellationToken);
        }

        public async Task<string> RunBufferedScripts(
            IReadOnlyList<ST_BUFFERED_SCRIPT_RUN_ITEM> scripts, 
            int number = 0, 
            int queueSize = 100, 
            int linesPerCommand = 1000, 
            int timeoutMs = 600000, 
            CancellationToken cancellationToken = default
        ) => await RunBufferedScriptsCore(scripts, number, queueSize, linesPerCommand, timeoutMs, upload: true, stageSession: false, cancellationToken).ConfigureAwait(false);
        // Preparation already uploaded these exact files. ScriptFileName is required;
        // LocalScriptPath is unused here, even if its source file has since been removed.
        // This operation owns the queues until completion/cancellation; the Station
        // observes RUN through shared snapshots.
        public virtual Task<string> RunStagePreparedBufferedScripts(IReadOnlyList<ST_BUFFERED_SCRIPT_RUN_ITEM> scripts, int number, int queueSize, int linesPerCommand, int timeoutMs, CancellationToken cancellationToken) => RunBufferedScriptsCore(scripts, number, queueSize, linesPerCommand, timeoutMs, upload: false, stageSession: true, cancellationToken);
        private async Task<string> RunBufferedScriptsCore(
            IReadOnlyList<ST_BUFFERED_SCRIPT_RUN_ITEM> scripts, 
            int number, 
            int queueSize, 
            int linesPerCommand, 
            int timeoutMs, 
            bool upload, 
            bool stageSession, 
            CancellationToken cancellationToken
        )
        {
            var runItems = scripts.ToArray();
            if (runItems.Length == 0)
            {
                return "OK:SCRIPT:BUFFERED_RUN:EMPTY";
            }

            // Reject duplicate controller-local Tasks before file access, upload or simulation RUN.
            var taskIndexes = new HashSet<int>();
            foreach (var script in runItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateTaskIndex(script.TaskIndex);
                if (!taskIndexes.Add(script.TaskIndex))
                    throw new InvalidOperationException($"Automation1 buffered run group has duplicated task: {script.TaskIndex}");
            }

            var commandQueueSize = Math.Max(1, queueSize);
            var commandLinesPerCommand = Math.Max(2, linesPerCommand);
            var commandTimeoutMs = Math.Max(0, timeoutMs);
            var fields = new List<string>
            {
                "AUTOMATION1:SCRIPT",
                "BUFFERED_RUN_GROUP",
                commandQueueSize.ToString(CultureInfo.InvariantCulture),
                commandLinesPerCommand.ToString(CultureInfo.InvariantCulture),
                commandTimeoutMs.ToString(CultureInfo.InvariantCulture)
            };
            var controllerFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var requests = new List<(int TaskIndex, string ControllerFile, string? LocalPath)>();
            foreach (var script in runItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? resolvedLocalScriptPath = null;
                string controllerFileName;
                if (upload)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(script.LocalScriptPath);
                    resolvedLocalScriptPath = ResolveLocalScriptPath(script.LocalScriptPath, cancellationToken);
                    ValidateBufferedRunScriptFile(resolvedLocalScriptPath);
                    controllerFileName = CreateBufferedRunControllerFileName(script, resolvedLocalScriptPath, cancellationToken);
                }
                else
                {
                    // ExecuteFromControllerFile uses the prepared controller file, not the PC source.
                    ArgumentException.ThrowIfNullOrWhiteSpace(script.ScriptFileName);
                    controllerFileName = CreateControllerFileName(script.ScriptFileName, cancellationToken);
                }

                if (!controllerFileNames.Add(controllerFileName))
                {
                    throw new InvalidOperationException($"Automation1 buffered run controller file is duplicated: {controllerFileName}");
                }

                requests.Add((script.TaskIndex, controllerFileName, resolvedLocalScriptPath));
            }

            // All input validation must succeed before the first external side effect.
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (upload)
                {
                    var uploadResponse = await UploadScript(request.LocalPath!, request.ControllerFile, number, cancellationToken);
                    EnsureAutomationResponseSucceeded(uploadResponse, $"Buffered script upload failed. ControllerFile={request.ControllerFile}");
                }

                fields.Add(request.TaskIndex.ToString(CultureInfo.InvariantCulture));
                fields.Add(request.ControllerFile);
            }

            if (stageSession && IsSimul(number))
            {
                var sessions = new List<CStageSimulatedTask>();
                try
                {
                    foreach (var request in requests)
                        sessions.Add(RegisterStageSimulationTask(number, request.TaskIndex, request.ControllerFile, buffered: true));
                    // DoReady starts RUN; only Process START releases virtual machining.
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (commandTimeoutMs > 0)
                        limit.CancelAfter(commandTimeoutMs);
                    try
                    {
                        await Task.WhenAll(sessions.Select(session => session.Completion)).WaitAsync(limit.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)when (!cancellationToken.IsCancellationRequested && limit.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Automation1 buffered run timeout: {commandTimeoutMs} ms.");
                    }
                }
                catch
                {
                    foreach (var session in sessions)
                        session.Stop();
                    throw;
                }

                return "OK:SCRIPT:BUFFERED_RUN:SIMULATION";
            }

            var response = await ExecuteRaw(string.Join("|", fields), number, cancellationToken).ConfigureAwait(false);
            EnsureAutomationResponseSucceeded(response, "Buffered script run failed.");
            return response;
        }

        private string CreateBufferedRunControllerFileName(ST_BUFFERED_SCRIPT_RUN_ITEM script, string resolvedLocalScriptPath, CancellationToken cancellationToken)
        {
            var fileName = string.IsNullOrWhiteSpace(script.ScriptFileName) ? CreateTaskScopedScriptFileName(script.TaskIndex, resolvedLocalScriptPath) : script.ScriptFileName;
            return CreateControllerFileName(fileName, cancellationToken);
        }

        private static string CreateTaskScopedScriptFileName(int taskIndex, string localScriptPath)
        {
            var fileName = Path.GetFileName(localScriptPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "PROCESS.ascript";
            }

            return $"T{taskIndex.ToString(CultureInfo.InvariantCulture)}_{fileName}";
        }

        public virtual Task<string> StopTask(int taskIndex = 1, int number = 0, CancellationToken cancellationToken = default)
        {
            ValidateTaskIndex(taskIndex);
            if (IsSimul(number))
            {
                if (_stageSimulationTasks.TryGetValue((number, taskIndex), out var session))
                {
                    session.Stop();
                    return Task.FromResult(CreateSimulationTaskStatus(taskIndex, session.State, session.File));
                }

                return Task.FromResult(CreateSimulationTaskStatus(taskIndex, "Idle"));
            }

            return ExecuteRaw($"AUTOMATION1:SCRIPT|STOP|{taskIndex}", number, cancellationToken);
        }

        public virtual Task<string> ReadTaskStatus(int taskIndex = 1, int number = 0, CancellationToken cancellationToken = default)
        {
            ValidateTaskIndex(taskIndex);
            if (IsSimul(number))
            {
                return Task.FromResult(_stageSimulationTasks.TryGetValue((number, taskIndex), out var session) ? CreateSimulationTaskStatus(taskIndex, session.State, session.File) : CreateSimulationTaskStatus(taskIndex, "Idle"));
            }

            return ExecuteRaw($"AUTOMATION1:SCRIPT|STATUS|{taskIndex}", number, cancellationToken, suppressLog: true);
        }

        public virtual Task<string> ReadTaskLineStatus(int taskIndex = 1, int number = 0, CancellationToken cancellationToken = default)
        {
            ValidateTaskIndex(taskIndex);
            if (IsSimul(number))
            {
                return Task.FromResult(string.Join(":", "OK", "TASK_LINE", taskIndex.ToString(CultureInfo.InvariantCulture), "0"));
            }

            return ExecuteRaw($"AUTOMATION1:TASK|LINE_STATUS|{taskIndex}", number, cancellationToken, suppressLog: true);
        }

        public virtual async Task<string> ReleaseEncoderScaleFactor(string galvoAxis, int taskIndex = 1, int number = 0, CancellationToken cancellationToken = default)
        {
            var axis = ResolveAeroScriptAxis(galvoAxis);
            ValidateTaskIndex(taskIndex);
            var response = await ExecuteRaw($"AUTOMATION1:COMMAND|{taskIndex.ToString(CultureInfo.InvariantCulture)}|GalvoEncoderScaleFactorSet({axis}, 0)", number, cancellationToken);
            EnsureAutomationResponseSucceeded(response, $"Automation1 encoder scale release failed. Axis={axis}, Task={taskIndex.ToString(CultureInfo.InvariantCulture)}.");
            return response;
        }

        public virtual async Task<string> SetGalvoLaserOutput(string galvoAxis, bool isOn, int taskIndex = 1, int number = 0, CancellationToken cancellationToken = default)
        {
            var axis = ResolveAeroScriptAxis(galvoAxis);
            ValidateTaskIndex(taskIndex);
            var output = isOn ? "On" : "Off";
            var response = await ExecuteRaw($"AUTOMATION1:COMMAND|{taskIndex.ToString(CultureInfo.InvariantCulture)}|GalvoLaserOutput({axis}, GalvoLaser.{output})", number, cancellationToken);
            EnsureAutomationResponseSucceeded(response, $"Automation1 laser output command failed. Axis={axis}, Output={output}, Task={taskIndex.ToString(CultureInfo.InvariantCulture)}.");
            return response;
        }

        /// <summary>Continuous electrical duty at 10 V; Recipe frequency is kHz and pulse times are microseconds.</summary>
        public virtual async Task ConfigurePowerMeasurement(string galvoAxis, double frequencyKhz, int taskIndex, int number, CancellationToken token)
        {
            var axis = ResolveAeroScriptAxis(galvoAxis);
            ValidateTaskIndex(taskIndex);
            if (!double.IsFinite(frequencyKhz) || frequencyKhz <= 0)
                throw new ArgumentOutOfRangeException(nameof(frequencyKhz));
            var period = (1000.0 / frequencyKhz).ToString("R", CultureInfo.InvariantCulture);
            await SetGalvoLaserOutput(axis, false, taskIndex, number, token).ConfigureAwait(false);
            foreach (var command in new[] {
                "SetupTaskDistanceUnits(DistanceUnits.Primary)", "SetupTaskTimeUnits(TimeUnits.Seconds)",
                "VelocityBlendingOff()", "SetupTaskWaitMode(WaitMode.Auto)", "SetupTaskTargetMode(TargetMode.Absolute)",
                $"GalvoEncoderScaleFactorSet({axis}, 0)", $"GalvoConfigureLaserOutputPeriod({axis}, {period})",
                $"AnalogOutputSet({axis}, 0, 10)", $"GalvoConfigureLaser1PulseWidth({axis}, {period})",
                $"GalvoConfigureLaserDelays({axis}, 0, 0)", $"GalvoConfigureLaserMode({axis}, 0)" })
            {
                var response = await ExecuteRaw($"AUTOMATION1:COMMAND|{taskIndex}|{command}", number, token).ConfigureAwait(false);
                EnsureAutomationResponseSucceeded(response, $"PowerMeasure configuration failed: {command}");
            }
        }

        private static void ValidateTaskIndex(int taskIndex)
        {
            if (taskIndex < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(taskIndex), taskIndex, "Automation1 task index must be 1 or greater.");
            }
        }

        private static string CreateSimulationTaskStatus(int taskIndex, string state, string sourceFile = "", string errorMessage = "")
        {
            return string.Join(":", "OK", "TASK", taskIndex.ToString(CultureInfo.InvariantCulture), state, sourceFile, errorMessage);
        }

        private Task<string> ExecuteAxis(string axisName, string command, int number, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(axisName);
            var automationAxis = ResolveAutomationAxis(axisName);
            if (IsSimul(number) && command is "SERVO_ON" or "SERVO_OFF")
            {
                cancellationToken.ThrowIfCancellationRequested();
                _simulationAxisAbleStates[CreateSimulationAxisKey(number, automationAxis)] = command == "SERVO_ON";
                return Task.FromResult($"OK:AXIS:{automationAxis}:{command}");
            }

            if (IsSimul(number))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parts = command.Split(':');
                var operation = parts[0];
                if (operation is "MANUAL_ABS" or "MANUAL_REL" or "MOVE_ABS" or "MOVE_REL")
                {
                    if (parts.Length < 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                        throw new InvalidOperationException("Simulated axis target is invalid.");
                    var key = CreateSimulationAxisKey(number, automationAxis);
                    _simulationAxisPositions.AddOrUpdate(key, value, (_, previous) => operation is "MANUAL_REL" or "MOVE_REL" ? previous + value : value);
                    return Task.FromResult($"OK:AXIS:{automationAxis}:{operation}");
                }

                if (operation is "HOME" or "HOME_ASYNC")
                {
                    _simulationAxisPositions[CreateSimulationAxisKey(number, automationAxis)] = 0;
                    return Task.FromResult($"OK:AXIS:{automationAxis}:{operation}");
                }

                if (operation is "STOP" or "ABORT_MOTION" or "JOG_STOP")
                    return Task.FromResult($"OK:AXIS:{automationAxis}:{operation}");
            }

            return ExecuteRaw($"AUTOMATION1:AXIS:{automationAxis}:{command}", number, cancellationToken);
        }

        private Task<string> ReadAxis(string axisName, int number, int analogInputNo, CancellationToken cancellationToken)
        {
            ValidateAnalogInputNo(analogInputNo);
            return ExecuteAxis(axisName, $"READ:{analogInputNo.ToString(CultureInfo.InvariantCulture)}", number, cancellationToken);
        }

        private Task<string> ReadAxisByName(string axisName, int number, int analogInputNo, CancellationToken cancellationToken)
        {
            return ReadAxisByName(axisName, number, analogInputNo, true, cancellationToken);
        }

        private Task<string> ReadAxisByName(string axisName, int number, int analogInputNo, bool readExtendedStatus, CancellationToken cancellationToken)
        {
            return ReadAxisByName(axisName, number, analogInputNo, readExtendedStatus, cancellationToken, suppressLog: false);
        }

        private Task<string> ReadAxisByName(string axisName, int number, int analogInputNo, bool readExtendedStatus, CancellationToken cancellationToken, bool suppressLog)
        {
            ValidateAnalogInputNo(analogInputNo);
            ValidateAxisNameOnly(axisName);
            var statusMode = readExtendedStatus ? "FULL" : "BASIC";
            return ExecuteRaw($"AUTOMATION1:AXIS:{axisName.Trim()}:READ:{analogInputNo.ToString(CultureInfo.InvariantCulture)}:{statusMode}", number, cancellationToken, suppressLog);
        }

        private static void ValidateAxisNameOnly(string axisName)
        {
            var normalized = axisName.Trim();
            if (!IsAxisNameOnlyIdentifier(normalized))
            {
                throw new InvalidOperationException($"Automation1 axis must be configured by axis name, not axis number: {axisName}");
            }
        }

        private static void ValidateAnalogInputNo(int analogInputNo)
        {
            if (analogInputNo < 0 || analogInputNo > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(analogInputNo), analogInputNo, "Automation1 analog input number must be between 0 and 3.");
            }
        }

        private static void ValidateBufferedRunScriptFile(string localScriptPath)
        {
            if (!File.Exists(localScriptPath))
            {
                throw new FileNotFoundException("Buffered run script file was not found.", localScriptPath);
            }

            var lineNo = 0;
            foreach (var rawLine in File.ReadLines(localScriptPath))
            {
                lineNo++;
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                var token = GetAeroScriptToken(line);
                if (IsBufferedRunBlockedToken(token) || (token.StartsWith('$') && !IsBufferedGlobalIntegerAssignment(line)))
                {
                    throw new InvalidDataException($"Buffered run script validation failed. {Path.GetFileName(localScriptPath)} line {lineNo} cannot be queued: {line}");
                }
            }
        }

        private static string GetAeroScriptToken(string line)
        {
            var endIndex = line.IndexOfAny(['(', ' ', '\t']);
            return endIndex <= 0 ? line : line[..endIndex];
        }

        // Global arrays already exist on the controller; no declaration or local variable is queued.
        private static bool IsBufferedGlobalIntegerAssignment(string line)
        {
            const string prefix = "$iglobal[";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            var close = line.IndexOf(']', prefix.Length);
            if (close < 0 || !int.TryParse(line.AsSpan(prefix.Length, close - prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)
                return false;
            var assignment = line[(close + 1)..].Trim();
            return assignment.StartsWith('=') && long.TryParse(assignment.AsSpan(1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0;
        }

        private static bool IsBufferedRunBlockedToken(string token)
        {
            return token.Equals("program", StringComparison.OrdinalIgnoreCase) || token.Equals("end", StringComparison.OrdinalIgnoreCase) || token.Equals("var", StringComparison.OrdinalIgnoreCase) || token.Equals("global", StringComparison.OrdinalIgnoreCase) || token.Equals("if", StringComparison.OrdinalIgnoreCase) || token.Equals("else", StringComparison.OrdinalIgnoreCase) || token.Equals("elseif", StringComparison.OrdinalIgnoreCase) || token.Equals("endif", StringComparison.OrdinalIgnoreCase) || token.Equals("while", StringComparison.OrdinalIgnoreCase) || token.Equals("endwhile", StringComparison.OrdinalIgnoreCase) || token.Equals("for", StringComparison.OrdinalIgnoreCase) || token.Equals("endfor", StringComparison.OrdinalIgnoreCase) || token.Equals("repeat", StringComparison.OrdinalIgnoreCase) || token.Equals("endrepeat", StringComparison.OrdinalIgnoreCase) || token.Equals("function", StringComparison.OrdinalIgnoreCase) || token.Equals("endfunction", StringComparison.OrdinalIgnoreCase) || token.Equals("return", StringComparison.OrdinalIgnoreCase) || token.Equals("break", StringComparison.OrdinalIgnoreCase) || token.Equals("continue", StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureAutomationResponseSucceeded(string response, string message)
        {
            if (IsAutomationSuccessResponse(response))
            {
                return;
            }

            throw new InvalidOperationException($"{message} Response={FormatAutomationResponse(response)}");
        }

        private static bool IsAutomationSuccessResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            var trimmed = response.Trim();
            if (trimmed.StartsWith("OK", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("ACK", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return trimmed.StartsWith("SIM:", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith(":OK", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatAutomationResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return "<empty>";
            }

            var automationError = FormatAutomationErrorResponse(response);
            return string.IsNullOrWhiteSpace(automationError) ? response.Trim() : automationError;
        }

        private static string FormatAutomationErrorResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return "";
            }

            var fields = response.Trim().Split(':', 4, StringSplitOptions.TrimEntries);
            if (fields.Length >= 4 && fields[0].Equals("ERROR", StringComparison.OrdinalIgnoreCase) && fields[1].Equals("AUTOMATION1", StringComparison.OrdinalIgnoreCase))
            {
                return $"{fields[2]}: {fields[3]}";
            }

            return "";
        }

        private static string ResolveAutomationAxis(string axisName)
        {
            var normalized = axisName.Trim();
            return AxisNoMap.TryGetValue(normalized, out var axisNo) ? axisNo.ToString(CultureInfo.InvariantCulture) : normalized;
        }

        private static string ResolveAeroScriptAxis(string axisName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(axisName);
            var normalized = axisName.Trim();
            if (!IsAeroScriptAxisIdentifier(normalized))
            {
                throw new InvalidOperationException($"Automation1 axis name must be a numeric axis number or start with a letter and use letters, numbers, and underscores only: {axisName}");
            }

            return AxisNoMap.TryGetValue(normalized, out var axisNo) ? axisNo.ToString(CultureInfo.InvariantCulture) : normalized;
        }

        private static bool IsAeroScriptAxisIdentifier(string value)
        {
            if (value.Length == 0)
            {
                return false;
            }

            if (value.All(char.IsAsciiDigit))
            {
                return true;
            }

            bool MatchesItem(char character)
            {
                return char.IsAsciiLetter(character) || char.IsAsciiDigit(character) || character == '_';
            }

            return char.IsAsciiLetter(value[0]) && value.All(MatchesItem);
        }

        private static bool IsAxisNameOnlyIdentifier(string value)
        {
            if (value.Length == 0 || char.IsAsciiDigit(value[0]))
            {
                return false;
            }

            if (!char.IsAsciiLetter(value[0]))
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

        private ST_AUTOMATION_AXIS_STATUS CreateSimulationAxisStatus(int automationNo, string axisName)
        {
            var able = _simulationAxisAbleStates.GetOrAdd(CreateSimulationAxisKey(automationNo, ResolveAutomationAxis(axisName)), true);
            var position = _simulationAxisPositions.GetValueOrDefault(CreateSimulationAxisKey(automationNo, ResolveAutomationAxis(axisName)));
            return new ST_AUTOMATION_AXIS_STATUS(
                automationNo, 
                axisName, 
                TryGetAxisNo(axisName), 
                position, 
                position, 
                0.0, 
                0.0, 
                0.0, 
                20.0, 
                able, 
                true, 
                false, 
                able ? "Enabled" : "Disabled", 
                "Homed", 
                "0", 
                $"SIM:AUTOMATION1:AXIS:{axisName}:READ:OK", 
                DateTimeOffset.Now
            )
            {
                IsInPosition = true,
                IsMotionDone = true
            };
        }

        private static string CreateSimulationAxisKey(int automationNo, string axisName)
        {
            return $"{automationNo.ToString(CultureInfo.InvariantCulture)}:{axisName.Trim()}";
        }

        private static ST_AUTOMATION_AXIS_STATUS ParseAxisStatus(int automationNo, string requestedAxis, string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return CreateErrorAxisStatus(automationNo, requestedAxis, "No Response", response);
            }

            var automationError = FormatAutomationErrorResponse(response);
            if (!string.IsNullOrWhiteSpace(automationError))
            {
                return CreateErrorAxisStatus(automationNo, requestedAxis, automationError, response);
            }

            var tokens = response.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.None);
            if (tokens.Length < 3 || !tokens[0].Equals("OK", StringComparison.OrdinalIgnoreCase) || !tokens[1].Equals("AXIS", StringComparison.OrdinalIgnoreCase))
            {
                return CreateErrorAxisStatus(automationNo, requestedAxis, "Invalid Response", response);
            }

            var axisName = string.IsNullOrWhiteSpace(tokens[2]) ? requestedAxis : tokens[2];
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 3; index + 1 < tokens.Length; index += 2)
            {
                values[tokens[index]] = tokens[index + 1];
            }

            var driveStatus = GetValue(values, "DRIVE");
            var fault = GetValue(values, "FAULT");
            var homeState = GetValue(values, "HOME");
            var hasMotionPositions = values.TryGetValue("FPOS", out var feedbackText) && double.TryParse(feedbackText, NumberStyles.Float, CultureInfo.InvariantCulture, out var feedback) && double.IsFinite(feedback) && values.TryGetValue("CPOS", out var commandText) && double.TryParse(commandText, NumberStyles.Float, CultureInfo.InvariantCulture, out var command) && double.IsFinite(command);
            return new ST_AUTOMATION_AXIS_STATUS(
                automationNo, 
                axisName, 
                TryGetAxisNo(axisName), 
                ReadDouble(values, "FPOS"), 
                ReadDouble(values, "CPOS"), 
                ReadDouble(values, "AUX"), 
                ReadDouble(values, "CURRENT"), 
                ReadDouble(values, "LASER_RAW"), 
                ReadGalvoTemperatureCelsius(values), 
                IsDriveAble(driveStatus), 
                IsHomeDone(homeState), 
                IsFaultActive(fault), 
                driveStatus, 
                homeState, 
                fault, 
                response, 
                DateTimeOffset.Now
            )
            {
                IsInPosition = hasMotionPositions ? ReadMotionStatusFlag(values, "IN_POSITION") : null,
                IsMotionDone = hasMotionPositions ? ReadMotionStatusFlag(values, "MOTION_DONE") : null
            };
        }

        private static bool? ReadMotionStatusFlag(IReadOnlyDictionary<string, string> values, string key)
        {
            if (!values.TryGetValue(key, out var text))
                return null;
            return text.Trim() switch
            {
                "1" => true,
                "0" => false,
                _ => null
            };
        }

        private static double ReadGalvoTemperatureCelsius(IReadOnlyDictionary<string, string> values)
        {
            return values.TryGetValue("MOTOR_TEMP", out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? CGalvoTemperature.FromAnalogInput(value) : double.NaN;
        }

        private static ST_AUTOMATION_AXIS_STATUS CreateErrorAxisStatus(int automationNo, string axisName, string fault, string response)
        {
            return new ST_AUTOMATION_AXIS_STATUS(automationNo, axisName, TryGetAxisNo(axisName), 0.0, 0.0, 0.0, 0.0, 0.0, double.NaN, false, false, true, "", "", fault, response, DateTimeOffset.Now);
        }

        private static int? TryGetAxisNo(string axisName)
        {
            var normalized = axisName.Trim();
            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var axisNo))
            {
                return axisNo;
            }

            return AxisNoMap.TryGetValue(normalized, out axisNo) ? axisNo : null;
        }

        private static string GetValue(IReadOnlyDictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out var value) ? value : "";
        }

        private static double ReadDouble(IReadOnlyDictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0.0;
        }

        private static bool IsDriveAble(string driveStatus)
        {
            if (TryReadStatusWord(driveStatus, out var statusWord))
            {
                return (statusWord & Automation1DriveStatusEnabledMask) == Automation1DriveStatusEnabledMask;
            }

            return driveStatus.Contains("Enabled", StringComparison.OrdinalIgnoreCase) || driveStatus.Contains("ServoControl", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHomeDone(string homeState)
        {
            var normalized = homeState.Trim();
            if (TryReadStatusWord(normalized, out var statusWord))
            {
                return (statusWord & Automation1AxisStatusHomedMask) == Automation1AxisStatusHomedMask;
            }

            return normalized.Equals("1", StringComparison.OrdinalIgnoreCase) || normalized.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || normalized.Contains("Homed", StringComparison.OrdinalIgnoreCase) || normalized.Contains("Done", StringComparison.OrdinalIgnoreCase) || normalized.Contains("Complete", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadStatusWord(string value, out long statusWord)
        {
            var normalized = value.Trim();
            if (long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out statusWord))
            {
                return true;
            }

            if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(normalized[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out statusWord))
            {
                return true;
            }

            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue) && double.IsFinite(doubleValue))
            {
                statusWord = Convert.ToInt64(doubleValue);
                return Math.Abs(doubleValue - statusWord) <= 0.000001;
            }

            statusWord = 0;
            return false;
        }

        private static bool IsFaultActive(string fault)
        {
            var normalized = fault.Trim();
            return !string.IsNullOrWhiteSpace(normalized) && !normalized.Equals("0", StringComparison.OrdinalIgnoreCase) && !normalized.Equals("None", StringComparison.OrdinalIgnoreCase);
        }

        private string ResolveLocalScriptPath(string fileNameOrPath, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileNameOrPath);
            var trimmed = fileNameOrPath.Trim();
            if (Path.IsPathRooted(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }

            var fileNameOnly = Path.GetFileName(trimmed);
            if (trimmed.Equals(fileNameOnly, StringComparison.OrdinalIgnoreCase))
            {
                var scriptDirectory = GetLocalScriptDirectory(cancellationToken);
                return Path.GetFullPath(Path.Combine(scriptDirectory, trimmed));
            }

            return Path.GetFullPath(Path.Combine(_projectRoot, trimmed));
        }

        private string GetLocalScriptDirectory(CancellationToken cancellationToken)
        {
            var settingValue = _settingManager is null ? "" : _settingManager.GetValue(EN_SETTING_TAB.Option, LocalScriptPathKey, "", cancellationToken);
            return ResolveLocalDirectory(settingValue, _defaultLocalScriptDirectory);
        }

        private string ResolveLocalDirectory(string settingValue, string defaultDirectory)
        {
            var value = string.IsNullOrWhiteSpace(settingValue) ? defaultDirectory : settingValue.Trim();
            return Path.IsPathRooted(value) ? Path.GetFullPath(value) : Path.GetFullPath(Path.Combine(_projectRoot, value));
        }

        private string CreateControllerFileName(string fileNameOrPath, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileNameOrPath);
            var trimmed = fileNameOrPath.Trim().Replace('\\', '/');
            if (trimmed.StartsWith('/'))
            {
                return trimmed;
            }

            var controllerDirectory = GetControllerScriptDirectory(cancellationToken);
            var fileName = Path.GetFileName(trimmed);
            return CombineControllerPath(controllerDirectory, fileName);
        }

        private string GetControllerScriptDirectory(CancellationToken cancellationToken)
        {
            var settingValue = _settingManager is null ? "" : _settingManager.GetValue(EN_SETTING_TAB.Option, AutomationScriptPathKey, "/", cancellationToken);
            if (string.IsNullOrWhiteSpace(settingValue))
            {
                return "/";
            }

            var normalized = settingValue.Trim().Replace('\\', '/');
            return normalized.StartsWith('/') ? normalized : "/" + normalized;
        }

        private static string CombineControllerPath(string directory, string fileName)
        {
            var normalizedDirectory = string.IsNullOrWhiteSpace(directory) ? "/" : directory.Trim().Replace('\\', '/');
            if (!normalizedDirectory.StartsWith('/'))
            {
                normalizedDirectory = "/" + normalizedDirectory;
            }

            normalizedDirectory = normalizedDirectory.TrimEnd('/');
            return normalizedDirectory.Length == 0 ? "/" + fileName : $"{normalizedDirectory}/{fileName}";
        }

        private static string Format(double value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

#endregion
#region AuxiliaryFeedback
        public async Task RunStagePreparedScript(string file, int task, int controller, CancellationToken token)
        {
            var response = await RunScriptIfIdle(file, task, controller, token).ConfigureAwait(false);
            EnsureAutomationResponseSucceeded(response, $"Stage script RUN failed: {file}.");
            if (IsSimul(controller))
                RegisterStageSimulationTask(controller, task, file, buffered: false);
            StatusSnapshots.InvalidateTask(task, controller);
        }

        public async Task StopStagePreparedTask(int task, int controller, CancellationToken token)
        {
            var response = await StopTask(task, controller, token).ConfigureAwait(false);
            EnsureAutomationResponseSucceeded(response, $"Stage Task stop failed: {task}.");
            StatusSnapshots.InvalidateTask(task, controller);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    limit.Token.ThrowIfCancellationRequested();
                    var sample = StatusSnapshots.GetTask(task, controller);
                    if (sample.State is "ERROR" or "OFFLINE" or "STOPPED")
                        throw new InvalidOperationException($"Cannot confirm Task STOP: {task}. {sample.Detail}");
                    var fields = sample.State == "OK" ? sample.Value?.Split(':') : null;
                    if (fields is { Length: >= 4 } && fields[0] == "OK" && fields[1] == "TASK" && int.TryParse(fields[2], out var taskNo) && taskNo == task && fields[3] is "Idle" or "ProgramComplete" or "QueueComplete" or "Stopped")
                        return;
                    await Task.Delay(50, limit.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException error)when (limit.IsCancellationRequested && !token.IsCancellationRequested)
            {
                throw new TimeoutException($"Task STOP 확인 시간 초과(5초): Controller {controller}, Task {task}.", error);
            }
        }

        internal async Task<ST_AUXILIARY_FEEDBACK> ReadAuxiliaryFeedback(string axis, int controller, CancellationToken token)
        {
            ValidateAxisNameOnly(axis);
            if (IsSimul(controller))
                return new(0);
            var response = await ExecuteRaw($"AUTOMATION1:FEEDBACK|READ_AUX|{axis}", controller, token, suppressLog: true).ConfigureAwait(false);
            const string prefix = "OK:AUX:";
            if (!response.StartsWith(prefix, StringComparison.Ordinal) || !double.TryParse(response[prefix.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new InvalidOperationException($"Auxiliary feedback read failed: {axis}. {response}");
            return new(value);
        }

        public async Task SetAuxiliaryFeedbackZero(string axis, int task, int controller, CancellationToken token = default)
        {
            ValidateAxisNameOnly(axis);
            if (task < 1)
                throw new ArgumentOutOfRangeException(nameof(task));
            if (!IsSimul(controller))
            {
                var response = await ExecuteRaw($"AUTOMATION1:FEEDBACK|ZERO_AUX|{axis}|{task}", controller, token).ConfigureAwait(false);
                EnsureAutomationResponseSucceeded(response, $"Auxiliary encoder zero failed: {axis}.");
            }

            StatusSnapshots.InvalidateAuxiliaryFeedback(axis, controller);
        }

        public Task ZeroMofFeedbackIfNeeded(IEnumerable<ST_MOF_ZERO_TARGET> targets, CancellationToken token = default) => new CMofZeroOperation(target => StatusSnapshots.GetAuxiliaryFeedback(target.Axis, target.Controller), (target, ct) => SetAuxiliaryFeedbackZero(target.Axis, target.Task, target.Controller, ct)).Execute(targets, token);
#endregion
#region ConfiguredHomeSpeed
        /// <summary>Reads saved HomeSpeed values, which may differ from the active values until controller reset.</summary>
        public virtual async Task<IReadOnlyDictionary<string, double>> ReadConfiguredHomeSpeeds(IReadOnlyList<string> axisNames, int number = 0, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(axisNames);
            ArgumentOutOfRangeException.ThrowIfNegative(number);
            cancellationToken.ThrowIfCancellationRequested();
            var axes = axisNames.Select(ValidateConfiguredHomeAxis).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (axes.Length == 0)
                return new CConfiguredHomeSpeedValues([]);
            if (IsSimul(number))
                return new CConfiguredHomeSpeedValues(axes.ToDictionary(axis => axis, axis => _simulationConfiguredHomeSpeeds.GetOrAdd(CreateSimulationAxisKey(number, axis), 20.0), StringComparer.OrdinalIgnoreCase), axes.ToDictionary(axis => axis, _ => "mm/sec", StringComparer.OrdinalIgnoreCase));
            var response = await ExecuteRaw($"AUTOMATION1:CONFIG:HOME_SPEEDS:{string.Join(',', axes)}", number, cancellationToken, suppressLog: true).ConfigureAwait(false);
            const string prefix = "OK:CONFIG_HOME_SPEEDS:";
            if (!response.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Saved HomeSpeed values could not be read: {response}");
            using var document = JsonDocument.Parse(response[prefix.Length..]);
            var root = document.RootElement;
            var hasMetadata = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Values", out _);
            var valuesElement = hasMetadata ? root.GetProperty("Values") : root;
            var values = valuesElement.Deserialize<Dictionary<string, double>>() ?? throw new InvalidOperationException("Saved HomeSpeed response was empty.");
            var units = hasMetadata && root.TryGetProperty("Units", out var unitsElement) ? unitsElement.Deserialize<Dictionary<string, string>>() : null;
            var result = new CConfiguredHomeSpeedValues(values, units);
            if (result.Count != axes.Length || axes.Any(axis => !result.TryGetValue(axis, out var value) || !double.IsFinite(value) || value < 0))
                throw new InvalidOperationException("Saved HomeSpeed response did not contain valid values for the requested axes.");
            return result;
        }

        /// <summary>Saves and verifies one HomeSpeed; does not change active parameters or reset the controller.</summary>
        public virtual async Task<double> SetConfiguredHomeSpeed(string axisName, double speed, int number = 0, CancellationToken cancellationToken = default)
        {
            var axis = ValidateConfiguredHomeAxis(axisName);
            ArgumentOutOfRangeException.ThrowIfNegative(number);
            if (!double.IsFinite(speed) || speed <= 0)
                throw new ArgumentOutOfRangeException(nameof(speed), "Home speed must be finite and greater than zero.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (IsSimul(number))
                {
                    var key = CreateSimulationAxisKey(number, axis);
                    _simulationConfiguredHomeSpeeds[key] = speed;
                    return _simulationConfiguredHomeSpeeds[key];
                }

                var response = await ExecuteRaw($"AUTOMATION1:CONFIG:HOME_SPEED:{axis}:{speed.ToString("R", CultureInfo.InvariantCulture)}", number, cancellationToken).ConfigureAwait(false);
                const string prefix = "OK:CONFIG_HOME_SPEED:";
                if (!response.StartsWith(prefix, StringComparison.Ordinal) || !double.TryParse(response[prefix.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var saved) || !double.IsFinite(saved) || saved != speed)
                    throw new InvalidOperationException($"HomeSpeed save could not be verified. Axis={axis}, response={response}");
                return saved;
            }
            finally
            {
                // A failed readback can follow a successful save. Invalidate even when the write reports an error.
                StatusSnapshots.RequestConfiguredHomeSpeedsRefresh(number);
            }
        }

        internal static string ValidateConfiguredHomeAxis(string axisName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(axisName);
            var axis = axisName.Trim();
            if (!IsAeroScriptAxisIdentifier(axis))
                throw new ArgumentException("Axis reference must be an axis name or a nonnegative axis number.", nameof(axisName));
            if (!axis.All(char.IsAsciiDigit))
                return axis;
            if (!int.TryParse(axis, NumberStyles.None, CultureInfo.InvariantCulture, out var axisNo))
                throw new ArgumentException("Axis number is out of range.", nameof(axisName));
            return axisNo.ToString(CultureInfo.InvariantCulture);
        }

#endregion
#region MofReadiness
        public virtual async Task PrepareMofReadiness(int task, int controller, long runId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ValidateTaskIndex(task);
            if (runId <= 0)
                throw new ArgumentOutOfRangeException(nameof(runId));
            if (IsSimul(controller))
                _simulatedMofReadiness[(controller, task)] = new(runId);
            else
            {
                // Send a Task identity, not a raw global slot. The transport checks this
                // controller's actual Task count immediately before accessing its variable.
                var response = await ExecuteRaw($"AUTOMATION1:VARIABLE|SET_MOF_READINESS|{task}|0", controller, token).ConfigureAwait(false);
                EnsureAutomationResponseSucceeded(response, $"MOF readiness reset failed: A{controller}/T{task}.");
            }

            StatusSnapshots.InvalidateMofReadiness(task, controller);
        }

        public virtual async Task<ST_MOF_READINESS> ReadMofReadiness(int task, int controller, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var index = CMofReadiness.VariableIndex(task);
            if (IsSimul(controller))
                return new(_simulatedMofReadiness.TryGetValue((controller, task), out var ready) ? ready.Value : 0);
            var response = await ExecuteRaw($"AUTOMATION1:VARIABLE|GET_MOF_READINESS|{task}", controller, token, suppressLog: true).ConfigureAwait(false);
            var fields = response.Split(':');
            if (fields.Length != 4 || fields[0] != "OK" || fields[1] != "GLOBAL_INTEGER" || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var receivedIndex) || receivedIndex != index || !long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException($"MOF readiness read failed: A{controller}/T{task}. {response}");
            return new(value);
        }

        private async Task PublishSimulatedMofReadiness(int controller, int task, CStageSimulatedTask session)
        {
            if (!_simulatedMofReadiness.TryGetValue((controller, task), out var ready))
                return;
            await Task.Delay(200).ConfigureAwait(false);
            if (!session.IsActive || !_stageSimulationTasks.TryGetValue((controller, task), out var active) || !ReferenceEquals(active, session) || !_simulatedMofReadiness.TryGetValue((controller, task), out var current) || !ReferenceEquals(current, ready))
                return;
            ready.Publish();
            StatusSnapshots.InvalidateMofReadiness(task, controller);
        }

        private sealed class CSimulatedMofReadiness(long expected)
        {
            private long _value;
            public long Value => Interlocked.Read(ref _value);

            public void Publish() => Interlocked.Exchange(ref _value, expected);
        }

#endregion
#region StageSimulation
        private CStageSimulatedTask RegisterStageSimulationTask(int controller, int task, string file, bool buffered)
        {
            var session = new CStageSimulatedTask(file, buffered, () => StatusSnapshots.InvalidateTask(task, controller));
            _stageSimulationTasks.AddOrUpdate((controller, task), session, (_, previous) => previous.IsActive ? throw new InvalidOperationException($"Simulated Task is already running: A{controller}/T{task}.") : session);
            StatusSnapshots.InvalidateTask(task, controller);
            _ = PublishSimulatedMofReadiness(controller, task, session);
            return session;
        }

        /// <summary>Only the Automation interface's own simulation setting permits a virtual completion.</summary>
        public void StartStageMachiningSimulation(IReadOnlyList<ST_STAGE_TASK_SIMULATION_TARGET> targets, CancellationToken token)
        {
            // Simulation-only behavior retained by operator decision: start virtual Tasks in
            // Head order, not as an atomic multi-Head transaction. Targets must already have
            // prepared RUN sessions. A later invalid target does not roll back earlier timers;
            // the calling operation owns STOP/cleanup for its Tasks if this method fails.
            var ordered = targets.OrderBy(target => target.HeadNo).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var target = ordered[index];
                if (!IsSimul(target.Controller))
                    continue;
                if (!_stageSimulationTasks.TryGetValue((target.Controller, target.Task), out var session))
                    throw new InvalidOperationException($"No simulated RUN for H{target.HeadNo:00}: A{target.Controller}/T{target.Task}.");
                // Deliberate simulation test profile: 3, 4, ... seconds exposes per-Head
                // completion changes in the UI. This is not a machine cycle-time estimate.
                // Simulation timeout settings must allow this stagger plus the wait before
                // machining START; non-simulated controllers never start a virtual timer.
                session.Start(TimeSpan.FromSeconds(3 + index), token);
            }
        }

        private sealed class CStageSimulatedTask(string file, bool buffered, Action changed)
        {
            private readonly object _sync = new();
            private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenSource _stop = new();
            private string _state = buffered ? "QueueRunning" : "ProgramRunning";
            private bool _started;
            public string File { get; } = file;

            public string State
            {
                get
                {
                    lock (_sync)
                        return _state;
                }
            }

            public bool IsActive => State is "QueueRunning" or "ProgramRunning";
            public Task Completion => _completion.Task;

            public void Start(TimeSpan duration, CancellationToken token)
            {
                lock (_sync)
                {
                    if (_started)
                        throw new InvalidOperationException("Simulated machining was already started.");
                    if (!IsActive)
                        throw new InvalidOperationException("Simulated Task is no longer running.");
                    _started = true;
                }

                _ = CompleteAfter(duration, token);
            }

            private async Task CompleteAfter(TimeSpan duration, CancellationToken token)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
                try
                {
                    await Task.Delay(duration, limit.Token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        limit.Token.ThrowIfCancellationRequested();
                        if (!IsActive)
                            return;
                        _state = buffered ? "QueueComplete" : "ProgramComplete";
                        _completion.TrySetResult();
                    }

                    changed();
                }
                catch (OperationCanceledException)
                {
                    Stop();
                }
                finally
                {
                    lock (_sync)
                        _stop.Dispose();
                }
            }

            public void Stop()
            {
                lock (_sync)
                {
                    if (!IsActive)
                        return;
                    _state = "Stopped";
                    _completion.TrySetCanceled();
                    _stop.Cancel();
                    if (!_started)
                        _stop.Dispose();
                }

                changed();
            }
        }
#endregion
    }

    public static class CAutomationAxisFault
    {
        public static long? ParseMask(string value)
        {
            var text = value.Trim();
            if (text.Equals("None", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) && hex >= 0 ? hex : null;
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mask) && mask >= 0 ? mask : null;
        }

        public static bool RequiresReset(long mask) => (mask & (long)AxisFault.InternalFault) != 0;
        public static bool HasLimitFault(long mask) => (mask & (long)(AxisFault.CwEndOfTravelLimitFault | AxisFault.CcwEndOfTravelLimitFault | AxisFault.CwSoftwareLimitFault | AxisFault.CcwSoftwareLimitFault)) != 0;
        public static string Describe(long mask)
        {
            if (mask == 0)
                return "활성 Fault 없음";
            var names = new List<string>();
            long known = 0;
            foreach (var name in Enum.GetNames<AxisFault>().Where(name => name.EndsWith("Fault", StringComparison.Ordinal)))
            {
                var fault = Enum.Parse<AxisFault>(name);
                var bit = (long)fault;
                if (bit == 0 || (known & bit) == bit || (mask & bit) != bit)
                    continue;
                names.Add(name);
                known |= bit;
            }

            if ((mask & ~known) != 0)
                names.Add($"Unknown Fault: 0x{mask & ~known:X}");
            if (RequiresReset(mask))
                names.Add("InternalFault는 컨트롤러 RESET이 필요합니다.");
            return $"FAULT: 0x{mask:X}\n{string.Join("\n", names)}";
        }
    }

    public sealed record ST_AUXILIARY_FEEDBACK(double Position);
    public sealed record ST_MOF_ZERO_TARGET(int Controller, int Task, string Axis);
    /// <summary>Uses the shared acquisition cache; zero axes are never sent a write command.</summary>
    public sealed class CMofZeroOperation(Func<ST_MOF_ZERO_TARGET, ST_AUTOMATION_STATUS_SNAPSHOT<ST_AUXILIARY_FEEDBACK>> read, Func<ST_MOF_ZERO_TARGET, CancellationToken, Task> zero)
    {
        public async Task Execute(IEnumerable<ST_MOF_ZERO_TARGET> targets, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var preparedTargets = targets.DistinctBy(t => (t.Controller, t.Axis.Trim().ToUpperInvariant())).ToArray();
            var preparationStarted = Stopwatch.GetTimestamp();
            // Register every axis with the shared collector together; these are memory reads,
            // not additional device polling. No sample from before this operation may skip ZERO.
            foreach (var target in preparedTargets)
            {
                token.ThrowIfCancellationRequested();
                read(target);
            }

            foreach (var target in preparedTargets)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var phase = "새 보조 피드백 수집";
                try
                {
                    var before = await WaitForValue(target, preparationStarted, timeout.Token).ConfigureAwait(false);
                    if (before.Value!.Position == 0)
                        continue;
                    phase = "ZERO_AUX 명령";
                    timeout.Token.ThrowIfCancellationRequested();
                    await zero(target, timeout.Token).ConfigureAwait(false);
                    var commandCompleted = Stopwatch.GetTimestamp();
                    phase = "ZERO_AUX 이후 새 0점 피드백 확인";
                    // The command invalidates the cache generation. Also require a collection
                    // started after its acknowledgement, not an earlier cached or in-flight read.
                    while (true)
                    {
                        var after = await WaitForValue(target, commandCompleted, timeout.Token).ConfigureAwait(false);
                        if (after.Version > before.Version && after.Value!.Position == 0)
                            break;
                        await Task.Delay(50, timeout.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException error)when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    throw new TimeoutException($"MOF Zero Set 확인 시간 초과(10초): Controller {target.Controller}, Task {target.Task}, Axis {target.Axis}, 단계: {phase}.", error);
                }
            }
        }

        private async Task<ST_AUTOMATION_STATUS_SNAPSHOT<ST_AUXILIARY_FEEDBACK>> WaitForValue(ST_MOF_ZERO_TARGET target, long collectedAfter, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var sample = read(target);
                if (sample.IsFresh(TimeSpan.FromSeconds(5)) && sample.CollectedTimestamp >= collectedAfter && sample.Value is { } value && double.IsFinite(value.Position))
                    return sample;
                if (sample.State is "ERROR" or "OFFLINE" or "STOPPED")
                    throw new InvalidOperationException($"MOF feedback unavailable: Controller {target.Controller}, {target.Axis}. {sample.State}: {sample.Detail}");
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
    }

    public sealed record ST_BUFFERED_SCRIPT_RUN_ITEM(string LocalScriptPath, string ScriptFileName, int TaskIndex);
    // MotorTemperature is the converted Galvo temperature in °C (NaN when unavailable); RawResponse retains the original input.
    public sealed record ST_AUTOMATION_AXIS_STATUS(
        int AutomationNo, 
        string AxisName, 
        int? AxisNo, 
        double PositionFeedback, 
        double PositionCommand, 
        double AuxiliaryFeedback, 
        double CurrentFeedback, 
        double GalvoLaserOutputRaw, 
        double MotorTemperature, 
        bool Able, 
        bool HomeDone, 
        bool HasError, 
        string DriveStatus, 
        string HomeState, 
        string Fault, 
        string RawResponse, 
        DateTimeOffset UpdatedAt
    )
    {
        public bool? IsInPosition { get; init; }
        public bool? IsMotionDone { get; init; }
    }

    public sealed record ST_STAGE_TASK_SIMULATION_TARGET(int HeadNo, int Controller, int Task);
    public sealed record ST_AUTOMATION_ABSOLUTE_MOVE_TARGET(string Label, int AutomationNo, string AxisReference, int AioNo, bool ReadExtendedStatus, double Position, double Speed);
    /// <summary>Absolute moves with completion verified exclusively through the shared acquisition cache.</summary>
    public sealed class CAutomationPositioningService(CAutomationManager automation)
    {
        public async Task MoveAndWait(IReadOnlyList<ST_AUTOMATION_ABSOLUTE_MOVE_TARGET> targets, CancellationToken cancellationToken = default)
        {
            var routes = new HashSet<(int, string)>();
            foreach (var target in targets)
            {
                if (!double.IsFinite(target.Position) || !double.IsFinite(target.Speed) || target.Speed <= 0 || string.IsNullOrWhiteSpace(target.AxisReference) || !char.IsAsciiLetter(target.AxisReference[0]) || target.AxisReference.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || !routes.Add((target.AutomationNo, target.AxisReference.ToUpperInvariant())))
                    throw new InvalidOperationException($"{target.Label}: invalid or duplicate absolute move target.");
                EnsureConnected(target);
                Read(target); // Register every axis with the shared worker before sending commands.
            }

            if (targets.Count == 0)
                return;
            var attempted = new List<ST_AUTOMATION_ABSOLUTE_MOVE_TARGET>();
            var fences = new Dictionary<ST_AUTOMATION_ABSOLUTE_MOVE_TARGET, long>();
            try
            {
                await WaitUntil(targets, target => ReadReady(target)is not null, cancellationToken);
                foreach (var target in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureConnected(target);
                    attempted.Add(target);
                    using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    commandTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    EnsureResponse(await automation.ManualMove(target.AxisReference, target.Position, target.Speed, false, target.AutomationNo, commandTimeout.Token).WaitAsync(commandTimeout.Token), target.Label);
                    automation.StatusSnapshots.InvalidateAxis(target.AxisReference, target.AutomationNo, target.AioNo, target.ReadExtendedStatus);
                    fences[target] = Stopwatch.GetTimestamp();
                }

                await WaitUntil(targets, target =>
                {
                    var snapshot = Read(target);
                    if (snapshot.CollectedTimestamp <= fences[target])
                        return false;
                    var status = ReadReady(target, snapshot);
                    return status is { IsInPosition: true, IsMotionDone: true } && double.IsFinite(status.PositionFeedback) && double.IsFinite(status.PositionCommand) && Math.Abs(status.PositionFeedback - target.Position) <= 0.001 && Math.Abs(status.PositionCommand - target.Position) <= 0.001;
                }, cancellationToken);
            }
            catch (Exception failure)
            {
                var stopErrors = new List<string>();
                foreach (var target in attempted)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        EnsureResponse(await automation.AbortMotion(target.AxisReference, target.AutomationNo, timeout.Token).WaitAsync(timeout.Token), $"{target.Label} STOP");
                    }
                    catch (Exception stopFailure)
                    {
                        stopErrors.Add($"{target.Label}: {stopFailure.Message}");
                    }
                }

                if (stopErrors.Count > 0)
                    throw new InvalidOperationException($"{failure.Message}\n축 정지 확인 실패:\n{string.Join("\n", stopErrors)}", failure);
                if (failure is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    throw new TimeoutException("DOE Z 이동 명령 응답 시간이 초과되었습니다.", failure);
                throw;
            }
        }

        private void EnsureConnected(ST_AUTOMATION_ABSOLUTE_MOVE_TARGET target)
        {
            if (!automation.IsSimul(target.AutomationNo) && !automation.IsConnect(target.AutomationNo))
                throw new InvalidOperationException($"{target.Label}: controller is offline.");
        }

        private ST_AUTOMATION_STATUS_SNAPSHOT<ST_AUTOMATION_AXIS_STATUS> Read(ST_AUTOMATION_ABSOLUTE_MOVE_TARGET target) => automation.StatusSnapshots.GetAxis(target.AxisReference, target.AutomationNo, target.AioNo, target.ReadExtendedStatus);
        private ST_AUTOMATION_AXIS_STATUS? ReadReady(ST_AUTOMATION_ABSOLUTE_MOVE_TARGET target, ST_AUTOMATION_STATUS_SNAPSHOT<ST_AUTOMATION_AXIS_STATUS>? snapshot = null)
        {
            EnsureConnected(target);
            snapshot ??= Read(target);
            if (snapshot.State is "ERROR" or "OFFLINE" or "STOPPED")
                throw new InvalidOperationException($"{target.Label}: {snapshot.State} {snapshot.Detail}");
            if (snapshot.State != "OK" || snapshot.Value is not { } status)
                return null;
            if (status.HasError || !status.Able || !status.HomeDone)
                throw new InvalidOperationException($"{target.Label}: Enable/Home/Fault 상태를 확인해 주세요. Fault={status.Fault}");
            return status;
        }

        private static async Task WaitUntil(IReadOnlyList<ST_AUTOMATION_ABSOLUTE_MOVE_TARGET> targets, Func<ST_AUTOMATION_ABSOLUTE_MOVE_TARGET, bool> ready, CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pending = targets.Where(target => !ready(target)).ToArray();
                if (pending.Length == 0)
                    return;
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(60))
                    throw new TimeoutException($"DOE Z 이동 완료 확인 시간 초과: {string.Join(", ", pending.Select(t => t.Label))}");
                await Task.Delay(200, cancellationToken);
            }
        }

        private static void EnsureResponse(string response, string label)
        {
            if (!response.StartsWith("OK:", StringComparison.OrdinalIgnoreCase) && !response.StartsWith("SIM:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{label}: {response}");
        }
    }

    /// <summary>Saved HomeSpeed values and their per-axis primary-unit velocity labels from the same configuration read.</summary>
    public sealed class CConfiguredHomeSpeedValues : ReadOnlyDictionary<string, double>
    {
        public IReadOnlyDictionary<string, string> Units { get; }

        public CConfiguredHomeSpeedValues(IEnumerable<KeyValuePair<string, double>> values, IEnumerable<KeyValuePair<string, string>>? units = null) : base(new Dictionary<string, double>(values, StringComparer.OrdinalIgnoreCase))
        {
            var copiedUnits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (units is not null)
                foreach (var(axis, unit)in units)
                    if (ContainsKey(axis) && !string.IsNullOrWhiteSpace(unit))
                        copiedUnits[axis] = unit.Trim();
            Units = new ReadOnlyDictionary<string, string>(copiedUnits);
        }
    }

    public static class CGalvoTemperature
    {
        public static double FromAnalogInput(double value)
        {
            // Apply the equipment's calibration directly to the value read from the analog input.
            return double.IsFinite(value) ? (2637.0 - value) / 13.6 : double.NaN;
        }

        public static string FormatCelsius(double value)
        {
            return double.IsFinite(value) ? $"{value.ToString("F3", CultureInfo.InvariantCulture)} °C" : "-";
        }
    }

    /// <summary>Orders manual starts and stops, including a release while a start is still pending.</summary>
    public sealed class CManualMotionSession
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private long _generation;
        private Func<Task>? _stopJog;
        public long Generation => Interlocked.Read(ref _generation);

        public void Invalidate() => Interlocked.Increment(ref _generation);
        public async Task Execute(long generation, Func<Task> action, Func<Task>? stopJog = null)
        {
            await _gate.WaitAsync();
            try
            {
                if (generation != Generation)
                    return;
                if (_stopJog is not null)
                    throw new InvalidOperationException("JOG를 먼저 정지해 주세요.");
                // Retain the stop callback even if the start reports an error after transmission.
                _stopJog = stopJog;
                await action();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task StopJog()
        {
            Invalidate();
            await _gate.WaitAsync();
            try
            {
                if (_stopJog is null)
                    return;
                await _stopJog();
                _stopJog = null;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task StopAll(Func<Task> stopAll)
        {
            Invalidate();
            await _gate.WaitAsync();
            try
            {
                var errors = new List<Exception>();
                try
                {
                    if (_stopJog is not null)
                    {
                        await _stopJog();
                        _stopJog = null;
                    }
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }

                try
                {
                    await stopAll();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }

                if (errors.Count > 0)
                    throw new AggregateException(errors);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public sealed record ST_MOF_READINESS(long RunId);
    /// <summary>Controller-local global integers [TaskNo - 1] are reserved for MOF readiness.</summary>
    public static class CMofReadiness
    {
        // Exact even in diagnostics that represent integer status values as doubles (< 2^53).
        private static long _lastRunId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        public static long CreateRunId() => Interlocked.Increment(ref _lastRunId);
        public static int VariableIndex(int task)
        {
            if (task < 1)
                throw new ArgumentOutOfRangeException(nameof(task));
            return task - 1;
        }

        public static int VariableIndex(int task, int controllerTaskCount)
        {
            if (controllerTaskCount < 1)
                throw new InvalidOperationException("Automation1 controller has no available Tasks.");
            if (task < 1 || task > controllerTaskCount)
                throw new ArgumentOutOfRangeException(nameof(task), task, $"Automation1 Task index is out of range. Range=1~{controllerTaskCount}");
            return task - 1;
        }
    }
}
