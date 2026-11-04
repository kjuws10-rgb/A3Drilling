namespace Drilling.Common.Interface
{
    using System.Globalization;
    using System.Text.Json;
    using Aerotech.Automation1.DotNet;
    using System.Runtime.ExceptionServices;
    using Drilling.Common.Automation;

    [CCommType("Automation1Net", "Automation1", "A1")]
    internal sealed partial class CAutomation1Comm(ST_INTERFACE_DATA data, ST_INTERFACE_CONNECT_OPTION option) : CCommBase(data, option)
    {
#region 공유 상태·초기값
        private const int DefaultTaskIndex = 1;
        private const int CommandQueuePollIntervalMs = 100;
        private const int CommandQueueCleanupTimeoutMs = 5000;
        private readonly SemaphoreSlim _commLock = new(1, 1);
        // Lock order: _commLock -> _statusReadLock. Buffered feeding holds only _commLock;
        // its read-only status lane holds _statusReadLock and never waits for _commLock.
        private readonly SemaphoreSlim _statusReadLock = new(1, 1);
        private readonly object _activeBufferedRunLock = new();
        private Controller? _controller;
        private CancellationTokenSource? _activeBufferedRunCancellation;
        private TaskCompletionSource _bufferedRunChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IReadOnlyDictionary<int, CBufferedRunTaskState> _activeBufferedRunTaskStates = new Dictionary<int, CBufferedRunTaskState>();
        private IReadOnlySet<int> _activeBufferedRunRequestedTasks = new HashSet<int>();
        private readonly Dictionary<int, string> _lastTaskStatusResponses = new();
#endregion
#region 기본 처리
        public override async Task Connect(CancellationToken cancellationToken = default)
        {
            await _commLock.WaitAsync(cancellationToken);
            var statusReadLocked = false;
            try
            {
                await _statusReadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                statusReadLocked = true;
                ConnectLocked();
            }
            catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)when (statusReadLocked)
            {
                CloseController();
                SetError(ex);
            }
            finally
            {
                if (statusReadLocked)
                    _statusReadLock.Release();
                _commLock.Release();
            }
        }

        public override async Task Disconnect(CancellationToken cancellationToken = default)
        {
            CancelActiveBufferedRun();
            await _commLock.WaitAsync(cancellationToken);
            var statusReadLocked = false;
            try
            {
                await _statusReadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                statusReadLocked = true;
                CloseController();
                SetState(EN_COMM_STATE.Offline);
            }
            finally
            {
                if (statusReadLocked)
                    _statusReadLock.Release();
                _commLock.Release();
            }
        }

        public override async Task<string> Execute(string function, CancellationToken cancellationToken = default)
        {
            CancelActiveBufferedRunIfStopCommand(function);
            var activeBufferedRunStatusRead = await AcquireCommandLockOrReadBufferedStatus(function, cancellationToken).ConfigureAwait(false);
            if (activeBufferedRunStatusRead is not null)
            {
                return activeBufferedRunStatusRead;
            }

            CancellationTokenSource? bufferedRunCancellation = null;
            var statusReadLocked = false;
            var executeCancellationToken = cancellationToken;
            try
            {
                // Normal commands and controller lifecycle exclude the buffered status lane.
                await _statusReadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                statusReadLocked = true;
                if (_controller is null || ConnectionState != EN_COMM_STATE.Online)
                {
                    ConnectLocked();
                }

                var controller = EnsureController();
                cancellationToken.ThrowIfCancellationRequested();
                if (IsBufferedRunCommand(function))
                {
                    bufferedRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    executeCancellationToken = bufferedRunCancellation.Token;
                    RegisterActiveBufferedRun(bufferedRunCancellation, function);
                    // Keep readiness/feedback reads available while queue feeding is active.
                    _statusReadLock.Release();
                    statusReadLocked = false;
                }

                string SelectValue()
                {
                    return ExecuteAutomation1Function(controller, function, executeCancellationToken);
                }

                var response = await Task.Run(SelectValue, executeCancellationToken);
                RememberTaskStatusResponse(function, response);
                LastSent = function;
                LastReceived = response;
                LastError = "";
                SetState(EN_COMM_STATE.Online);
                return LastReceived;
            }
            catch (OperationCanceledException)when (executeCancellationToken.IsCancellationRequested)
            {
                LastReceived = "";
                throw;
            }
            catch (Aerotech.Automation1.ControllerOperationException ex)
            {
                var response = CreateErrorResponse(ex);
                LastSent = function;
                LastReceived = response;
                SetCommandError(CreateErrorMessage(ex));
                SetState(EN_COMM_STATE.Online);
                return response;
            }
            catch (Exception ex)
            {
                var response = CreateErrorResponse(ex);
                LastSent = function;
                LastReceived = response;
                SetError(ex);
                return response;
            }
            finally
            {
                try
                {
                    if (statusReadLocked)
                        _statusReadLock.Release();
                    if (bufferedRunCancellation is not null)
                        await FinishBufferedRunAfterStatusReads(bufferedRunCancellation).ConfigureAwait(false);
                }
                finally
                {
                    _commLock.Release();
                }
            }
        }

        private async Task<string?> AcquireCommandLockOrReadBufferedStatus(string function, CancellationToken token)
        {
            if (!IsBufferedRunReadOnlyStatusRequest(function) && !TryReadTaskStatusRequest(function, out _))
            {
                await _commLock.WaitAsync(token).ConfigureAwait(false);
                return null;
            }

            while (true)
            {
                token.ThrowIfCancellationRequested();
                Task changed;
                lock (_activeBufferedRunLock)
                    changed = _bufferedRunChanged.Task;
                if (TryReadActiveBufferedRunTaskStatus(function, out var taskStatus))
                    return taskStatus;
                var status = await TryExecuteActiveBufferedRunStatusRead(function, token).ConfigureAwait(false);
                if (status is not null)
                    return status;
                // A buffered run can start after the check but before this read obtains
                // _commLock. Wake on that transition and re-route, without polling or
                // leaving a queued semaphore waiter that could consume a later release.
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                var acquired = _commLock.WaitAsync(wait.Token);
                if (!acquired.IsCompleted)
                    await Task.WhenAny(acquired, changed.WaitAsync(wait.Token)).ConfigureAwait(false);
                wait.Cancel();
                try
                {
                    await acquired.ConfigureAwait(false);
                    return null; // Command ownership won, including a race with the transition.
                }
                catch (OperationCanceledException)when (!token.IsCancellationRequested)
                {
                }
            }
        }

        private async Task FinishBufferedRunAfterStatusReads(CancellationTokenSource cancellation)
        {
            // Drain status readers before releasing _commLock to normal commands/Disconnect.
            await _statusReadLock.WaitAsync().ConfigureAwait(false);
            try
            {
                UnregisterActiveBufferedRun(cancellation);
                cancellation.Dispose();
            }
            finally
            {
                _statusReadLock.Release();
            }
        }

        private void RegisterActiveBufferedRun(CancellationTokenSource cancellation, string function)
        {
            TryGetAutomationBody(function, out var body);
            var fields = SplitPipe(body);
            var tasks = new HashSet<int>();
            if (fields.Length > 1 && fields[1].Equals("BUFFERED_RUN_GROUP", StringComparison.OrdinalIgnoreCase))
            {
                for (var index = 5; index + 1 < fields.Length; index += 2)
                    tasks.Add(ReadInt(fields[index], "TASK"));
            }
            else
                tasks.Add(ReadInt(fields, 2, DefaultTaskIndex, "TASK"));
            lock (_activeBufferedRunLock)
            {
                _activeBufferedRunCancellation = cancellation;
                _activeBufferedRunTaskStates = new Dictionary<int, CBufferedRunTaskState>();
                _activeBufferedRunRequestedTasks = tasks;
                SignalBufferedRunChanged();
            }
        }

        private void UnregisterActiveBufferedRun(CancellationTokenSource cancellation)
        {
            lock (_activeBufferedRunLock)
            {
                if (ReferenceEquals(_activeBufferedRunCancellation, cancellation))
                {
                    _activeBufferedRunCancellation = null;
                    _activeBufferedRunTaskStates = new Dictionary<int, CBufferedRunTaskState>();
                    _activeBufferedRunRequestedTasks = new HashSet<int>();
                    SignalBufferedRunChanged();
                }
            }
        }

        // Called with _activeBufferedRunLock held; continuations never run inside it.
        private void SignalBufferedRunChanged()
        {
            var previous = _bufferedRunChanged;
            _bufferedRunChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }

        private bool TryReadActiveBufferedRunTaskStatus(string function, out string response)
        {
            response = "";
            if (!TryReadTaskStatusRequest(function, out var taskIndex))
            {
                return false;
            }

            lock (_activeBufferedRunLock)
            {
                if (_activeBufferedRunCancellation is null)
                {
                    return false;
                }

                if (!_activeBufferedRunTaskStates.TryGetValue(taskIndex, out var state))
                {
                    if (!_activeBufferedRunRequestedTasks.Contains(taskIndex) && _lastTaskStatusResponses.TryGetValue(taskIndex, out var lastResponse))
                    {
                        response = lastResponse;
                    }
                    else
                    {
                        response = FormatBufferedRunTaskStatusStarting(taskIndex);
                    }
                }
                else
                {
                    response = FormatActiveBufferedRunTaskStatus(state);
                    _lastTaskStatusResponses[taskIndex] = response;
                }

                LastSent = function;
                LastReceived = response;
                LastError = "";
                return true;
            }
        }

        private async Task<string?> TryExecuteActiveBufferedRunStatusRead(string function, CancellationToken cancellationToken)
        {
            if (!IsBufferedRunReadOnlyStatusRequest(function))
            {
                return null;
            }

            lock (_activeBufferedRunLock)
            {
                if (_activeBufferedRunCancellation is null)
                    return null;
            }

            await _statusReadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // The run/controller can change while waiting for this lane. Never retain
                // a Controller reference obtained before the wait or bypass ordinary RUNs.
                if (!TryGetActiveBufferedRunController(out var controller))
                    return null;
                string ReadBufferedRunStatus()
                {
                    return ExecuteAutomation1Function(controller, function, cancellationToken);
                }

                var response = await Task.Run(ReadBufferedRunStatus, cancellationToken).ConfigureAwait(false);
                RememberTaskStatusResponse(function, response);
                LastSent = function;
                LastReceived = response;
                LastError = "";
                SetState(EN_COMM_STATE.Online);
                return response;
            }
            catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Aerotech.Automation1.ControllerOperationException ex)
            {
                var response = CreateErrorResponse(ex);
                LastSent = function;
                LastReceived = response;
                SetCommandError(CreateErrorMessage(ex));
                SetState(EN_COMM_STATE.Online);
                return response;
            }
            catch (Exception ex)
            {
                var response = CreateErrorResponse(ex);
                LastSent = function;
                LastReceived = response;
                SetError(ex);
                return response;
            }
            finally
            {
                _statusReadLock.Release();
            }
        }

        private bool TryGetActiveBufferedRunController(out Controller controller)
        {
            lock (_activeBufferedRunLock)
            {
                if (_activeBufferedRunCancellation is null || _controller is null || ConnectionState != EN_COMM_STATE.Online)
                {
                    controller = null !;
                    return false;
                }

                controller = _controller;
                return true;
            }
        }

        private static bool IsBufferedRunReadOnlyStatusRequest(string function)
        {
            if (!TryGetAutomationBody(function, out var body))
            {
                return false;
            }

            var section = ReadAutomationSection(body);
            if (section.Equals("VARIABLE", StringComparison.OrdinalIgnoreCase))
            {
                return IsMofReadinessReadRequest(function);
            }

            if (section.Equals("FEEDBACK", StringComparison.OrdinalIgnoreCase))
            {
                var fields = SplitPipe(body);
                return fields.Length >= 2 && fields[1].Equals("READ_AUX", StringComparison.OrdinalIgnoreCase);
            }

            if (section.Equals("AXIS", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = SplitColon(body);
                return tokens.Length >= 3 && tokens[2].Equals("READ", StringComparison.OrdinalIgnoreCase);
            }

            if (section.Equals("IO", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = SplitColon(body);
                return tokens.Length >= 3 && tokens[2].Equals("READ", StringComparison.OrdinalIgnoreCase);
            }

            if (section.Equals("TASK", StringComparison.OrdinalIgnoreCase))
            {
                if (body.Contains('|', StringComparison.Ordinal))
                {
                    var fields = SplitPipe(body);
                    return fields.Length >= 2 && fields[1].Equals("LINE_STATUS", StringComparison.OrdinalIgnoreCase);
                }

                var tokens = SplitColon(body);
                return tokens.Length >= 3 && tokens[2].Equals("LINE_STATUS", StringComparison.OrdinalIgnoreCase);
            }

            if (section.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = SplitColon(body);
                return tokens.Length >= 2 && tokens[1].Equals("PROCESSOR_USAGE", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private void RememberTaskStatusResponse(string function, string response)
        {
            if (!TryReadTaskStatusRequest(function, out var taskIndex))
            {
                return;
            }

            if (!response.StartsWith("OK:TASK:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (_activeBufferedRunLock)
            {
                _lastTaskStatusResponses[taskIndex] = response;
            }
        }

        private void CancelActiveBufferedRunIfStopCommand(string function)
        {
            if (IsBufferedRunStopCommand(function))
            {
                CancelActiveBufferedRun();
            }
        }

        private void CancelActiveBufferedRun()
        {
            lock (_activeBufferedRunLock)
            {
                _activeBufferedRunCancellation?.Cancel();
            }
        }

        private static bool IsBufferedRunCommand(string function)
        {
            if (!TryGetAutomationBody(function, out var body))
            {
                return false;
            }

            var section = ReadAutomationSection(body);
            if (!section.Equals("SCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var fields = SplitPipe(body);
            return fields.Length >= 2 && (fields[1].Equals("BUFFERED_RUN", StringComparison.OrdinalIgnoreCase) || fields[1].Equals("BUFFERED_RUN_GROUP", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsBufferedRunStopCommand(string function)
        {
            if (!TryGetAutomationBody(function, out var body))
            {
                return false;
            }

            var section = ReadAutomationSection(body);
            if (section.Equals("SCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                var fields = SplitPipe(body);
                return fields.Length >= 2 && fields[1].Equals("STOP", StringComparison.OrdinalIgnoreCase);
            }

            if (section.Equals("TASK", StringComparison.OrdinalIgnoreCase))
            {
                return IsTaskStopCommand(body);
            }

            if (section.Equals("AXIS", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = SplitColon(body);
                return tokens.Length >= 3 && tokens[2].Equals("STOP", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static bool IsTaskStopCommand(string body)
        {
            if (body.Contains('|', StringComparison.Ordinal))
            {
                var fields = SplitPipe(body);
                return fields.Length >= 2 && fields[1].Equals("STOP", StringComparison.OrdinalIgnoreCase);
            }

            var tokens = SplitColon(body);
            return tokens.Length >= 3 && tokens[2].Equals("STOP", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadTaskStatusRequest(string function, out int taskIndex)
        {
            taskIndex = DefaultTaskIndex;
            if (!TryGetAutomationBody(function, out var body))
            {
                return false;
            }

            try
            {
                var section = ReadAutomationSection(body);
                if (section.Equals("SCRIPT", StringComparison.OrdinalIgnoreCase))
                {
                    var fields = SplitPipe(body);
                    if (fields.Length >= 2 && fields[1].Equals("STATUS", StringComparison.OrdinalIgnoreCase))
                    {
                        taskIndex = ReadInt(fields, 2, DefaultTaskIndex, "TASK");
                        return true;
                    }
                }

                if (section.Equals("TASK", StringComparison.OrdinalIgnoreCase))
                {
                    if (body.Contains('|', StringComparison.Ordinal))
                    {
                        var fields = SplitPipe(body);
                        if (fields.Length >= 2 && fields[1].Equals("STATUS", StringComparison.OrdinalIgnoreCase))
                        {
                            taskIndex = ReadInt(fields, 2, DefaultTaskIndex, "TASK");
                            return true;
                        }
                    }
                    else
                    {
                        var tokens = SplitColon(body);
                        if (tokens.Length >= 3 && tokens[2].Equals("STATUS", StringComparison.OrdinalIgnoreCase))
                        {
                            taskIndex = ReadInt(tokens[1], "TASK");
                            return true;
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                taskIndex = DefaultTaskIndex;
            }

            return false;
        }

        private static bool TryGetAutomationBody(string function, out string body)
        {
            body = "";
            if (string.IsNullOrWhiteSpace(function))
            {
                return false;
            }

            var command = function.Trim();
            if (!command.StartsWith("AUTOMATION1:", StringComparison.OrdinalIgnoreCase) && !command.StartsWith("A1:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var prefixEnd = command.IndexOf(':', StringComparison.Ordinal);
            if (prefixEnd < 0 || prefixEnd + 1 >= command.Length)
            {
                return false;
            }

            body = command[(prefixEnd + 1)..];
            return true;
        }

        private static string ReadAutomationSection(string body)
        {
            var sectionEnd = body.IndexOfAny([':', '|']);
            return sectionEnd >= 0 ? body[..sectionEnd] : body;
        }

        private void ConnectLocked()
        {
            CloseController();
            var retryCount = Math.Max(1, Option.RetryCount);
            Exception? lastException = null;
            for (var tryNo = 0; tryNo < retryCount; tryNo++)
            {
                try
                {
                    _controller = ConnectController();
                    if (!_controller.IsRunning)
                    {
                        _controller.Start();
                    }

                    LastError = "";
                    SetState(EN_COMM_STATE.Online);
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    CloseController();
                }
            }

            throw new InvalidOperationException($"Automation1 connection failed. Endpoint={Option.Endpoint}", lastException);
        }

        private Controller ConnectController()
        {
            if (string.IsNullOrWhiteSpace(Option.RemoteAddress) || Option.RemoteAddress.Equals("LOCAL", StringComparison.OrdinalIgnoreCase) || Option.RemoteAddress.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
            {
                return Controller.Connect();
            }

            if (Option.Port <= 0)
            {
                return Controller.Connect(Option.RemoteAddress);
            }

            return Controller.Connect(Option.RemoteAddress, Option.Port);
        }

        private string ExecuteAutomation1Function(Controller controller, string function, CancellationToken cancellationToken)
        {
            var command = function.Trim();
            if (command.Equals("STATUS", StringComparison.OrdinalIgnoreCase) || command.Equals("AUTOMATION1:STATUS", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteStatus(controller);
            }

            if (!command.StartsWith("AUTOMATION1:", StringComparison.OrdinalIgnoreCase) && !command.StartsWith("A1:", StringComparison.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                controller.Runtime.Commands.Execute(command, DefaultTaskIndex);
                return "OK:COMMAND";
            }

            var prefixEnd = command.IndexOf(':', StringComparison.Ordinal);
            var body = command[(prefixEnd + 1)..];
            var sectionEnd = body.IndexOfAny([':', '|']);
            var section = sectionEnd >= 0 ? body[..sectionEnd] : body;
            if (section.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteSystemFunction(controller, SplitColon(body));
            }

            return section.ToUpperInvariant() switch
            {
                "SCRIPT" => ExecuteScriptFunction(controller, body, cancellationToken),
                "TASK" => ExecuteTaskFunction(controller, body),
                "COMMAND" => ExecuteCommandFunction(controller, body),
                "AXIS" => ExecuteAxisFunction(controller, SplitColon(body)),
                "CONFIG" => ExecuteConfiguredHomeSpeedFunction(controller, SplitColon(body)),
                "FEEDBACK" => ExecuteFeedbackFunction(controller, SplitPipe(body)),
                "VARIABLE" => ExecuteVariableFunction(controller, SplitPipe(body)),
                "IO" => ExecuteIoFunction(controller, SplitColon(body)),
                _ => throw new InvalidOperationException($"Automation1 command is unknown: {function}")};
        }

        private static string ExecuteFeedbackFunction(Controller controller, string[] fields)
        {
            if (fields.Length < 3 || string.IsNullOrWhiteSpace(fields[2]))
                throw new InvalidOperationException("Auxiliary feedback command requires an axis.");
            var axis = fields[2];
            if (fields[1] == "READ_AUX")
            {
                var configuration = new StatusItemConfiguration();
                configuration.Axis.Add(AxisStatusItem.AuxiliaryFeedback, axis);
                var results = controller.Runtime.Status.GetStatusItems(configuration);
                var value = Convert.ToDouble(GetAxisStatusValue(results, AxisStatusItem.AuxiliaryFeedback, new CAutoAxis(axis, null)), CultureInfo.InvariantCulture);
                return $"OK:AUX:{value.ToString("R", CultureInfo.InvariantCulture)}";
            }

            if (fields[1] != "ZERO_AUX" || fields.Length != 4)
                throw new InvalidOperationException("Unknown auxiliary feedback command.");
            controller.Runtime.Commands.Device.DriveSetEncoderPosition(axis, DriveEncoderChannel.AuxiliaryEncoder, 0, ReadInt(fields[3], "TASK"));
            return "OK:AUX_ZERO";
        }

        private string ExecuteScriptFunction(Controller controller, string body, CancellationToken cancellationToken)
        {
            var fields = SplitPipe(body);
            if (fields.Length < 2)
            {
                throw new InvalidOperationException("Automation1 script command is invalid.");
            }

            var command = fields[1].ToUpperInvariant();
            return command switch
            {
                "UPLOAD" => UploadScript(controller, fields),
                "RUN" => RunControllerScript(controller, fields),
                "RUN_IF_IDLE" => RunControllerScript(controller, fields, requireIdle: true),
                "RUN_LOCAL" => RunLocalScript(controller, fields),
                "BUFFERED_RUN" => RunBufferedScript(controller, fields, cancellationToken),
                "BUFFERED_RUN_GROUP" => RunBufferedScriptGroup(controller, fields, cancellationToken),
                "STOP" => StopTask(controller, ReadInt(fields, 2, DefaultTaskIndex, "TASK")),
                "STATUS" => ReadTaskStatus(controller, ReadInt(fields, 2, DefaultTaskIndex, "TASK")),
                _ => throw new InvalidOperationException($"Automation1 script command is unknown: {command}")};
        }

        private static string ExecuteTaskFunction(Controller controller, string body)
        {
            if (body.Contains('|', StringComparison.Ordinal))
            {
                var fields = SplitPipe(body);
                if (fields.Length < 2)
                {
                    throw new InvalidOperationException("Automation1 task command is invalid.");
                }

                var pipeCommand = fields[1].ToUpperInvariant();
                var pipeTaskIndex = ReadInt(fields, 2, DefaultTaskIndex, "TASK");
                return pipeCommand switch
                {
                    "STATUS" => ReadTaskStatus(controller, pipeTaskIndex),
                    "LINE_STATUS" => ReadTaskLineStatus(controller, pipeTaskIndex),
                    "STOP" => StopTask(controller, pipeTaskIndex),
                    _ => throw new InvalidOperationException($"Automation1 task command is unknown: {pipeCommand}")};
            }

            var tokens = SplitColon(body);
            if (tokens.Length < 3)
            {
                throw new InvalidOperationException("Automation1 task command is invalid.");
            }

            var taskIndex = ReadInt(tokens[1], "TASK");
            var command = tokens[2].ToUpperInvariant();
            return command switch
            {
                "STATUS" => ReadTaskStatus(controller, taskIndex),
                "LINE_STATUS" => ReadTaskLineStatus(controller, taskIndex),
                "STOP" => StopTask(controller, taskIndex),
                _ => throw new InvalidOperationException($"Automation1 task command is unknown: {command}")};
        }

        private static string ExecuteCommandFunction(Controller controller, string body)
        {
            var fields = SplitPipe(body);
            if (fields.Length < 3)
            {
                throw new InvalidOperationException("Automation1 direct command is invalid. Use AUTOMATION1:COMMAND|task|aeroscript.");
            }

            var taskIndex = ReadInt(fields, 1, DefaultTaskIndex, "TASK");
            var aeroScript = fields[2];
            ValidateTask(controller, taskIndex);
            controller.Runtime.Commands.Execute(aeroScript, taskIndex);
            return $"OK:COMMAND:{taskIndex}";
        }

        private static string UploadScript(Controller controller, IReadOnlyList<string> fields)
        {
            if (fields.Count < 4)
            {
                throw new InvalidOperationException("Automation1 script upload command is invalid. Use AUTOMATION1:SCRIPT|UPLOAD|localPath|controllerFile.");
            }

            var localPath = fields[2];
            var controllerFile = fields[3];
            controller.Files.Upload(localPath, controllerFile);
            return $"OK:SCRIPT:UPLOAD:{Sanitize(controllerFile)}";
        }

        private static string RunControllerScript(Controller controller, IReadOnlyList<string> fields, bool requireIdle = false)
        {
            if (fields.Count < 4)
            {
                throw new InvalidOperationException("Automation1 script run command is invalid. Use AUTOMATION1:SCRIPT|RUN|task|controllerFile.");
            }

            var taskIndex = ReadInt(fields, 2, DefaultTaskIndex, "TASK");
            var controllerFile = fields[3];
            ValidateTask(controller, taskIndex);
            var compiled = controller.Compiler.CompileControllerFile(controllerFile, compileWithDebugInformation: true);
            if (requireIdle)
            {
                var status = ReadTaskStatus(controller, taskIndex);
                var state = status.Split(':')[3];
                if (!state.Equals("Idle", StringComparison.OrdinalIgnoreCase) && !state.Equals("ProgramComplete", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Task {taskIndex} is not idle: {state}");
            }

            controller.Runtime.Tasks[taskIndex].Program.Run(compiled);
            return ReadTaskStatus(controller, taskIndex);
        }

        private static string RunLocalScript(Controller controller, IReadOnlyList<string> fields)
        {
            if (fields.Count < 5)
            {
                throw new InvalidOperationException("Automation1 local script run command is invalid. Use AUTOMATION1:SCRIPT|RUN_LOCAL|task|localPath|controllerFile.");
            }

            var taskIndex = ReadInt(fields, 2, DefaultTaskIndex, "TASK");
            var localPath = fields[3];
            var controllerFile = fields[4];
            ValidateTask(controller, taskIndex);
            controller.Files.Upload(localPath, controllerFile);
            var compiled = controller.Compiler.CompileControllerFile(controllerFile, compileWithDebugInformation: true);
            controller.Runtime.Tasks[taskIndex].Program.Run(compiled);
            return ReadTaskStatus(controller, taskIndex);
        }

        private string RunBufferedScript(Controller controller, IReadOnlyList<string> fields, CancellationToken cancellationToken)
        {
            if (fields.Count < 7)
            {
                throw new InvalidOperationException("Automation1 buffered run command is invalid. Use AUTOMATION1:SCRIPT|BUFFERED_RUN|task|controllerFile|queueSize|linesPerCommand|timeoutMs.");
            }

            var taskIndex = ReadInt(fields, 2, DefaultTaskIndex, "TASK");
            var controllerFile = fields[3];
            var queueSize = Math.Max(1, ReadInt(fields, 4, 100, "QUEUE_SIZE"));
            var linesPerCommand = Math.Max(2, ReadInt(fields, 5, 1000, "LINES_PER_COMMAND"));
            var timeoutMs = Math.Max(0, ReadInt(fields, 6, 600000, "TIMEOUT_MS"));
            ValidateTask(controller, taskIndex);
            return RunBufferedControllerFiles(controller, [new CBufferedRunRequest(taskIndex, controllerFile)], queueSize, linesPerCommand, timeoutMs, cancellationToken);
        }

        private string RunBufferedScriptGroup(Controller controller, IReadOnlyList<string> fields, CancellationToken cancellationToken)
        {
            if (fields.Count < 7 || (fields.Count - 5) % 2 != 0)
            {
                throw new InvalidOperationException("Automation1 buffered run group command is invalid. Use AUTOMATION1:SCRIPT|BUFFERED_RUN_GROUP|queueSize|linesPerCommand|timeoutMs|task|controllerFile...");
            }

            var queueSize = Math.Max(1, ReadInt(fields, 2, 100, "QUEUE_SIZE"));
            var linesPerCommand = Math.Max(2, ReadInt(fields, 3, 1000, "LINES_PER_COMMAND"));
            var timeoutMs = Math.Max(0, ReadInt(fields, 4, 600000, "TIMEOUT_MS"));
            var requests = new List<CBufferedRunRequest>();
            var taskIndexes = new HashSet<int>();
            for (var index = 5; index + 1 < fields.Count; index += 2)
            {
                var taskIndex = ReadInt(fields, index, DefaultTaskIndex, "TASK");
                var controllerFile = fields[index + 1];
                if (!taskIndexes.Add(taskIndex))
                {
                    throw new InvalidOperationException($"Automation1 buffered run group has duplicated task: {taskIndex}");
                }

                ValidateTask(controller, taskIndex);
                requests.Add(new CBufferedRunRequest(taskIndex, controllerFile));
            }

            return RunBufferedControllerFiles(controller, requests, queueSize, linesPerCommand, timeoutMs, cancellationToken);
        }

        private string RunBufferedControllerFiles(Controller controller, IReadOnlyList<CBufferedRunRequest> requests, int queueSize, int linesPerCommand, int timeoutMs, CancellationToken cancellationToken)
        {
            var commandQueues = new List<CBufferedRunQueue>();
            var cleanupExceptions = new List<Exception>();
            Exception? runException = null;
            try
            {
                foreach (var request in requests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var commandQueue = controller.Runtime.Commands.BeginCommandQueue(request.TaskIndex, queueSize, shouldBlockIfFull: false);
                    var commandQueueEntry = new CBufferedRunQueue(request, commandQueue, 0);
                    commandQueues.Add(commandQueueEntry);
                    commandQueues[^1] = commandQueueEntry with
                    {
                        InitialNumberOfTimesEmptied = commandQueue.Status.NumberOfTimesEmptied
                    };
                }

                UpdateActiveBufferedRunTaskStates(controller, commandQueues);
                foreach (var item in commandQueues)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    item.Queue.Commands.ExecuteFromControllerFile(item.Request.ControllerFile, linesPerCommand);
                }

                UpdateActiveBufferedRunTaskStates(controller, commandQueues);
                WaitForBufferedRunTasksComplete(controller, commandQueues, timeoutMs, cancellationToken);
            }
            catch (Exception ex)
            {
                runException = ex;
                StopBufferedRunTasks(controller, ReadBufferedRunRequests(commandQueues), cleanupExceptions);
            }
            finally
            {
                var cleanupTimeoutMs = ReadCommandQueueCleanupTimeoutMs(runException, timeoutMs);
                EndBufferedCommandQueues(controller, commandQueues, cleanupTimeoutMs, cleanupExceptions);
            }

            if (runException is not null)
            {
                AddCleanupExceptions(runException, cleanupExceptions);
                ExceptionDispatchInfo.Capture(runException).Throw();
            }

            if (cleanupExceptions.Count > 0)
            {
                throw new AggregateException("Automation1 buffered run cleanup failed.", cleanupExceptions);
            }

            return string.Join(":", "OK", "SCRIPT", "BUFFERED_RUN", requests.Count.ToString(CultureInfo.InvariantCulture), queueSize.ToString(CultureInfo.InvariantCulture), linesPerCommand.ToString(CultureInfo.InvariantCulture));
        }

        private void WaitForBufferedRunTasksComplete(Controller controller, IReadOnlyList<CBufferedRunQueue> commandQueues, int timeoutMs, CancellationToken cancellationToken)
        {
            var queueList = commandQueues.ToArray();
            var startedAt = Environment.TickCount64;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // This wait begins only after every controller file was added in its entirety.
                var states = ReadBufferedRunTaskStates(controller, queueList).Select(state => state with { FeedComplete = true }).ToArray();
                SetActiveBufferedRunTaskStates(states);
                var errorState = FindBufferedRunErrorState(states);
                if (errorState is not null)
                {
                    throw new InvalidOperationException($"Automation1 buffered run task error. Task={errorState.TaskIndex}, Error={errorState.Error}");
                }

                if (AreBufferedRunQueuesComplete(states))
                {
                    return;
                }

                ThrowIfBufferedRunTimeout(startedAt, timeoutMs, states);
                WaitBufferedRunPollInterval(startedAt, timeoutMs, cancellationToken);
            }
        }

        private static CBufferedRunTaskState ReadBufferedRunTaskState(Controller controller, CBufferedRunQueue commandQueue)
        {
            var request = commandQueue.Request;
            ValidateTask(controller, request.TaskIndex);
            var taskStatus = controller.Runtime.Tasks[request.TaskIndex].Status;
            var queueStatus = commandQueue.Queue.Status;
            return new CBufferedRunTaskState(
                request.TaskIndex, 
                request.ControllerFile, 
                taskStatus.TaskState, 
                queueStatus.NumberOfTimesEmptied, 
                commandQueue.InitialNumberOfTimesEmptied, 
                queueStatus.NumberOfExecutedCommands, 
                queueStatus.NumberOfUnexecutedCommands, 
                queueStatus.IsEmpty, 
                taskStatus.Error?.ToString() ?? ""
            );
        }

        private static CBufferedRunTaskState[] ReadBufferedRunTaskStates(Controller controller, IReadOnlyList<CBufferedRunQueue> commandQueues)
        {
            var states = new CBufferedRunTaskState[commandQueues.Count];
            for (var index = 0; index < commandQueues.Count; index++)
            {
                states[index] = ReadBufferedRunTaskState(controller, commandQueues[index]);
            }

            return states;
        }

        private static CBufferedRunTaskState? FindBufferedRunErrorState(IReadOnlyList<CBufferedRunTaskState> states)
        {
            foreach (var state in states)
            {
                if (state.State == TaskState.Error)
                {
                    return state;
                }
            }

            return null;
        }

        private static bool AreBufferedRunQueuesComplete(IReadOnlyList<CBufferedRunTaskState> states)
        {
            foreach (var state in states)
            {
                if (!IsBufferedRunQueueComplete(state))
                {
                    return false;
                }
            }

            return true;
        }

        private static void ThrowIfBufferedRunTimeout(long startedAt, int timeoutMs, IReadOnlyList<CBufferedRunTaskState> states)
        {
            if (timeoutMs <= 0)
            {
                return;
            }

            var elapsedMs = Environment.TickCount64 - startedAt;
            if (elapsedMs >= timeoutMs)
            {
                throw new TimeoutException($"Automation1 buffered run task queue drain timeout. TimeoutMs={timeoutMs}, States={FormatBufferedRunTaskStates(states)}");
            }
        }

        private static void WaitBufferedRunPollInterval(long startedAt, int timeoutMs, CancellationToken cancellationToken)
        {
            var waitMs = CommandQueuePollIntervalMs;
            if (timeoutMs > 0)
            {
                var elapsedMs = Environment.TickCount64 - startedAt;
                var remainingMs = timeoutMs - elapsedMs;
                if (remainingMs <= 0)
                {
                    return;
                }

                waitMs = (int)Math.Min(waitMs, remainingMs);
            }

            if (waitMs <= 0)
            {
                return;
            }

            cancellationToken.WaitHandle.WaitOne(waitMs);
        }

        private static IReadOnlyList<CBufferedRunRequest> ReadBufferedRunRequests(IReadOnlyList<CBufferedRunQueue> commandQueues)
        {
            var requests = new List<CBufferedRunRequest>(commandQueues.Count);
            foreach (var item in commandQueues)
            {
                requests.Add(item.Request);
            }

            return requests;
        }

        private void UpdateActiveBufferedRunTaskStates(Controller controller, IReadOnlyList<CBufferedRunQueue> commandQueues)
        {
            var states = new CBufferedRunTaskState[commandQueues.Count];
            for (var index = 0; index < commandQueues.Count; index++)
            {
                states[index] = ReadBufferedRunTaskState(controller, commandQueues[index]);
            }

            SetActiveBufferedRunTaskStates(states);
        }

        private void SetActiveBufferedRunTaskStates(IReadOnlyList<CBufferedRunTaskState> states)
        {
            var nextStates = new Dictionary<int, CBufferedRunTaskState>();
            foreach (var state in states)
            {
                nextStates[state.TaskIndex] = state;
            }

            lock (_activeBufferedRunLock)
            {
                if (_activeBufferedRunCancellation is not null)
                {
                    _activeBufferedRunTaskStates = nextStates;
                }
            }
        }

        private static string FormatActiveBufferedRunTaskStatus(CBufferedRunTaskState state)
        {
            var taskState = FormatActiveBufferedRunTaskState(state);
            var detail = string.IsNullOrWhiteSpace(state.Error) ? $"QueueEmpty={state.IsQueueEmpty}, QueueExecuted={state.NumberOfExecutedCommands.ToString(CultureInfo.InvariantCulture)}, QueuePending={state.NumberOfUnexecutedCommands.ToString(CultureInfo.InvariantCulture)}" : state.Error;
            return string.Join(":", "OK", "TASK", state.TaskIndex.ToString(CultureInfo.InvariantCulture), Sanitize(taskState), Sanitize(state.SourceFile), Sanitize(detail));
        }

        private static string FormatBufferedRunTaskStatusStarting(int taskIndex)
        {
            return string.Join(":", "OK", "TASK", taskIndex.ToString(CultureInfo.InvariantCulture), "QueuePending", "", "Waiting for buffered queue status");
        }

        private static string FormatActiveBufferedRunTaskState(CBufferedRunTaskState state)
        {
            if (state.State is TaskState.Error or TaskState.Inactive or TaskState.Unavailable)
            {
                return state.State.ToString();
            }

            if (IsBufferedRunQueueComplete(state))
                return "QueueComplete";
            if (state.State == TaskState.QueueRunning)
                return "QueueRunning";
            if (state.State == TaskState.ProgramRunning)
            {
                return state.State.ToString();
            }

            if (!IsBufferedRunQueueComplete(state) && HasBufferedRunQueueActivity(state))
            {
                return "QueueRunning";
            }

            return "QueuePending";
        }

        private static bool HasBufferedRunQueueActivity(CBufferedRunTaskState state)
        {
            return !state.IsQueueEmpty || state.NumberOfExecutedCommands > 0 || state.NumberOfUnexecutedCommands > 0 || state.NumberOfTimesEmptied > state.InitialNumberOfTimesEmptied;
        }

        private static bool IsBufferedRunQueueComplete(CBufferedRunTaskState state)
        {
            if (state.State is TaskState.Error or TaskState.Inactive or TaskState.Unavailable)
            {
                return false;
            }

            // A past starvation or Idle alone is not completion. IsEmpty also excludes an executing command.
            return state.FeedComplete && state.IsQueueEmpty && state.NumberOfUnexecutedCommands == 0 && state.NumberOfExecutedCommands > 0;
        }

        private static string FormatBufferedRunTaskStates(IReadOnlyList<CBufferedRunTaskState> states)
        {
            var details = new List<string>(states.Count);
            foreach (var state in states)
            {
                details.Add($"T{state.TaskIndex}={state.State}/QueueEmpty={state.IsQueueEmpty}/QueueEmptied={state.NumberOfTimesEmptied}/Initial={state.InitialNumberOfTimesEmptied}/Executed={state.NumberOfExecutedCommands}/Pending={state.NumberOfUnexecutedCommands}{(string.IsNullOrWhiteSpace(state.Error) ? "" : $"/{state.Error}")}");
            }

            return string.Join(", ", details);
        }

        private static void StopBufferedRunTasks(Controller controller, IEnumerable<CBufferedRunRequest> requests, ICollection<Exception> cleanupExceptions)
        {
            int SelectTaskIndex(global::Drilling.Common.Interface.CAutomation1Comm.CBufferedRunRequest request)
            {
                return request.TaskIndex;
            }

            foreach (var taskIndex in requests.Select(SelectTaskIndex).Distinct())
            {
                try
                {
                    controller.Runtime.Tasks[taskIndex].Program.Stop(5000);
                }
                catch (Exception ex)
                {
                    cleanupExceptions.Add(new InvalidOperationException($"Automation1 buffered run task stop failed. Task={taskIndex}", ex));
                }
            }
        }

        private static void EndBufferedCommandQueues(Controller controller, IReadOnlyList<CBufferedRunQueue> commandQueues, int timeoutMs, ICollection<Exception> cleanupExceptions)
        {
            foreach (var item in commandQueues.AsEnumerable().Reverse())
            {
                try
                {
                    if (timeoutMs <= 0)
                    {
                        controller.Runtime.Commands.EndCommandQueue(item.Queue);
                    }
                    else
                    {
                        controller.Runtime.Commands.EndCommandQueue(item.Queue, timeoutMs);
                    }
                }
                catch (Exception ex)
                {
                    cleanupExceptions.Add(new InvalidOperationException($"Automation1 command queue end failed. Task={item.Request.TaskIndex}", ex));
                }
            }
        }

        private static int ReadCommandQueueCleanupTimeoutMs(Exception? runException, int timeoutMs)
        {
            if (runException is null)
            {
                return timeoutMs;
            }

            if (timeoutMs <= 0)
            {
                return CommandQueueCleanupTimeoutMs;
            }

            return Math.Min(timeoutMs, CommandQueueCleanupTimeoutMs);
        }

        private static void AddCleanupExceptions(Exception exception, IReadOnlyCollection<Exception> cleanupExceptions)
        {
            if (cleanupExceptions.Count == 0)
            {
                return;
            }

            string SelectMessage(global::System.Exception error)
            {
                return error.Message;
            }

            exception.Data["Automation1BufferedRunCleanupErrors"] = string.Join(Environment.NewLine, cleanupExceptions.Select(SelectMessage));
        }

        private static string StopTask(Controller controller, int taskIndex)
        {
            ValidateTask(controller, taskIndex);
            controller.Runtime.Tasks[taskIndex].Program.Stop(5000);
            return ReadTaskStatus(controller, taskIndex);
        }

        private static string ReadTaskStatus(Controller controller, int taskIndex)
        {
            ValidateTask(controller, taskIndex);
            var status = controller.Runtime.Tasks[taskIndex].Status;
            var error = status.Error?.ToString() ?? "";
            return string.Join(":", "OK", "TASK", taskIndex.ToString(CultureInfo.InvariantCulture), Sanitize(status.TaskState.ToString()), Sanitize(status.AeroScriptSourceFileName ?? ""), Sanitize(error));
        }

        private static string ReadTaskLineStatus(Controller controller, int taskIndex)
        {
            ValidateTask(controller, taskIndex);
            var configuration = new StatusItemConfiguration();
            configuration.Task.Add(TaskStatusItem.ProgramLineNumber, taskIndex);
            var results = controller.Runtime.Status.GetStatusItems(configuration);
            var currentLine = Convert.ToInt32(results.Task[TaskStatusItem.ProgramLineNumber, taskIndex].Value, CultureInfo.InvariantCulture);
            return string.Join(":", "OK", "TASK_LINE", taskIndex.ToString(CultureInfo.InvariantCulture), currentLine.ToString(CultureInfo.InvariantCulture));
        }

        private static string ExecuteAxisFunction(Controller controller, IReadOnlyList<string> tokens)
        {
            if (tokens.Count < 3)
            {
                throw new InvalidOperationException("Automation1 axis command is invalid.");
            }

            var axis = CreateAxisReference(tokens[1]);
            var axisName = tokens[1];
            const int commandIndex = 2;
            var valueIndex = commandIndex + 1;
            var command = tokens[commandIndex].ToUpperInvariant();
            var taskIndex = tokens.Count > valueIndex + 1 && tokens[^2].Equals("TASK", StringComparison.OrdinalIgnoreCase) ? ReadInt(tokens[^1], "TASK") : DefaultTaskIndex;
            void ExecuteIntCallback(int axisNo)
            {
                controller.Runtime.Commands.Motion.Enable(axisNo);
            }

            void ExecuteStringCallback(string axisText)
            {
                controller.Runtime.Commands.Motion.Enable(axisText);
            }

            void ExecuteIntCallback2(int axisNo)
            {
                controller.Runtime.Commands.Motion.Disable(axisNo);
            }

            void ExecuteStringCallback2(string axisText)
            {
                controller.Runtime.Commands.Motion.Disable(axisText);
            }

            void ExecuteIntCallback3(int axisNo)
            {
                controller.Runtime.Commands.Motion.Home(axisNo);
            }

            void ExecuteStringCallback3(string axisText)
            {
                controller.Runtime.Commands.Motion.Home(axisText);
            }

            void ExecuteIntCallback4(int axisNo)
            {
                controller.Runtime.Commands.Motion.HomeAsync(axisNo);
            }

            void ExecuteStringCallback4(string axisText)
            {
                controller.Runtime.Commands.Motion.HomeAsync(axisText);
            }

            void ExecuteIntCallback5(int axisNo, double position, double velocity)
            {
                controller.Runtime.Commands.Motion.MoveAbsolute(axisNo, position, velocity);
            }

            void ExecuteStringCallback5(string axisText, double position, double velocity)
            {
                controller.Runtime.Commands.Motion.MoveAbsolute(axisText, position, velocity);
            }

            void ExecuteIntCallback6(int axisNo, double distance, double velocity)
            {
                controller.Runtime.Commands.Motion.MoveIncremental(axisNo, distance, velocity);
            }

            void ExecuteStringCallback6(string axisText, double distance, double velocity)
            {
                controller.Runtime.Commands.Motion.MoveIncremental(axisText, distance, velocity);
            }

            void ExecuteIntCallback7(int axisNo)
            {
                controller.Runtime.Commands.FaultAndError.FaultAcknowledge(axisNo);
            }

            void ExecuteStringCallback7(string axisText)
            {
                controller.Runtime.Commands.FaultAndError.FaultAcknowledge(axisText);
            }

            switch (command)
            {
                case "MANUAL_ABS":
                case "MANUAL_REL":
                case "JOG_START":
                    controller.Runtime.Commands.Galvo.IfovOff(taskIndex);
                    controller.Runtime.Commands.MotionSetup.SetupTaskDistanceUnits(DistanceUnits.Primary, taskIndex);
                    controller.Runtime.Commands.MotionSetup.SetupTaskTimeUnits(TimeUnits.Seconds, taskIndex);
                    if (command == "JOG_START")
                    {
                        var speed = ReadDouble(tokens, valueIndex, "VELOCITY");
                        ExecuteAxis(axis, n => controller.Runtime.Commands.Motion.MoveFreerun(n, speed, taskIndex), name => controller.Runtime.Commands.Motion.MoveFreerun(name, speed, taskIndex));
                    }
                    else
                    {
                        var value = ReadDouble(tokens, valueIndex, "POSITION");
                        var speed = ReadDouble(tokens, valueIndex + 1, "VELOCITY");
                        if (command == "MANUAL_ABS")
                            ExecuteAxis(axis, ExecuteIntCallback5, ExecuteStringCallback5, value, speed);
                        else
                            ExecuteAxis(axis, ExecuteIntCallback6, ExecuteStringCallback6, value, speed);
                    }

                    return $"OK:AXIS:{Sanitize(axisName)}:{command}";
                case "JOG_STOP":
                    ExecuteAxis(axis, n => controller.Runtime.Commands.Motion.MoveFreerunStop(n, taskIndex), name => controller.Runtime.Commands.Motion.MoveFreerunStop(name, taskIndex));
                    return $"OK:AXIS:{Sanitize(axisName)}:JOG_STOP";
                case "ABORT_MOTION":
                    ExecuteAxis(axis, n => controller.Runtime.Commands.Motion.Abort(n), name => controller.Runtime.Commands.Motion.Abort(name));
                    return $"OK:AXIS:{Sanitize(axisName)}:ABORT_MOTION";
                case "SERVO_ON":
                    ExecuteAxis(axis, ExecuteIntCallback, ExecuteStringCallback);
                    return $"OK:AXIS:{Sanitize(axisName)}:SERVO_ON";
                case "SERVO_OFF":
                    ExecuteAxis(axis, ExecuteIntCallback2, ExecuteStringCallback2);
                    return $"OK:AXIS:{Sanitize(axisName)}:SERVO_OFF";
                case "HOME":
                    ExecuteAxis(axis, ExecuteIntCallback3, ExecuteStringCallback3);
                    return $"OK:AXIS:{Sanitize(axisName)}:HOME";
                case "HOME_ASYNC":
                    ExecuteAxis(axis, ExecuteIntCallback4, ExecuteStringCallback4);
                    return $"OK:AXIS:{Sanitize(axisName)}:HOME_ASYNC";
                case "MOVE_ABS":
                    ExecuteAxis(axis, ExecuteIntCallback5, ExecuteStringCallback5, ReadDouble(tokens, valueIndex, "MOVE_ABS"), ReadDouble(tokens, valueIndex + 1, 100.0, "VELOCITY"));
                    return $"OK:AXIS:{Sanitize(axisName)}:MOVE_ABS";
                case "MOVE_REL":
                    ExecuteAxis(axis, ExecuteIntCallback6, ExecuteStringCallback6, ReadDouble(tokens, valueIndex, "MOVE_REL"), ReadDouble(tokens, valueIndex + 1, 100.0, "VELOCITY"));
                    return $"OK:AXIS:{Sanitize(axisName)}:MOVE_REL";
                case "STOP":
                    controller.Runtime.Commands.Execute($"Abort({axis.CommandText})", taskIndex);
                    return $"OK:AXIS:{Sanitize(axisName)}:STOP";
                case "RESET_ALARM":
                    ExecuteAxis(axis, ExecuteIntCallback7, ExecuteStringCallback7);
                    return $"OK:AXIS:{Sanitize(axisName)}:RESET_ALARM";
                case "READ":
                    var analogInputNo = ReadAxisStatusAnalogInputNo(tokens, valueIndex);
                    var readExtendedStatus = ReadAxisStatusExtendedFlag(tokens, valueIndex + 1);
                    return ReadAxisStatus(controller, axis, axisName, analogInputNo, readExtendedStatus);
                default:
                    throw new InvalidOperationException($"Automation1 axis command is unknown: {command}");
            }
        }

        private static string ReadAxisStatus(Controller controller, CAutoAxis axis, string axisName, int analogInputNo, bool readExtendedStatus)
        {
            var configuration = new StatusItemConfiguration();
            var analogInputItem = ToAnalogInputStatusItem(analogInputNo);
            if (axis.AxisNo.HasValue)
            {
                configuration.Axis.Add(AxisStatusItem.PositionFeedback, axis.AxisNo.Value);
                configuration.Axis.Add(AxisStatusItem.PositionCommand, axis.AxisNo.Value);
                if (readExtendedStatus)
                {
                    configuration.Axis.Add(AxisStatusItem.AuxiliaryFeedback, axis.AxisNo.Value);
                    configuration.Axis.Add(AxisStatusItem.CurrentFeedback, axis.AxisNo.Value);
                    configuration.Axis.Add(AxisStatusItem.GalvoLaserOutputRaw, axis.AxisNo.Value);
                    configuration.Axis.Add(analogInputItem, axis.AxisNo.Value);
                }

                configuration.Axis.Add(AxisStatusItem.DriveStatus, axis.AxisNo.Value);
                configuration.Axis.Add(AxisStatusItem.AxisFault, axis.AxisNo.Value);
                configuration.Axis.Add(AxisStatusItem.AxisStatus, axis.AxisNo.Value);
            }
            else
            {
                configuration.Axis.Add(AxisStatusItem.PositionFeedback, axis.AxisName);
                configuration.Axis.Add(AxisStatusItem.PositionCommand, axis.AxisName);
                if (readExtendedStatus)
                {
                    configuration.Axis.Add(AxisStatusItem.AuxiliaryFeedback, axis.AxisName);
                    configuration.Axis.Add(AxisStatusItem.CurrentFeedback, axis.AxisName);
                    configuration.Axis.Add(AxisStatusItem.GalvoLaserOutputRaw, axis.AxisName);
                    configuration.Axis.Add(analogInputItem, axis.AxisName);
                }

                configuration.Axis.Add(AxisStatusItem.DriveStatus, axis.AxisName);
                configuration.Axis.Add(AxisStatusItem.AxisFault, axis.AxisName);
                configuration.Axis.Add(AxisStatusItem.AxisStatus, axis.AxisName);
            }

            var results = controller.Runtime.Status.GetStatusItems(configuration);
            var feedback = Convert.ToDouble(GetAxisStatusValue(results, AxisStatusItem.PositionFeedback, axis), CultureInfo.InvariantCulture);
            var command = Convert.ToDouble(GetAxisStatusValue(results, AxisStatusItem.PositionCommand, axis), CultureInfo.InvariantCulture);
            var driveStatus = Convert.ToString(GetAxisStatusValue(results, AxisStatusItem.DriveStatus, axis), CultureInfo.InvariantCulture) ?? "";
            var fault = Convert.ToInt64(GetAxisStatusValue(results, AxisStatusItem.AxisFault, axis), CultureInfo.InvariantCulture);
            var axisStatus = Convert.ToString(GetAxisStatusValue(results, AxisStatusItem.AxisStatus, axis), CultureInfo.InvariantCulture) ?? "";
            var fields = new List<string>
            {
                "OK",
                "AXIS",
                Sanitize(axisName),
                "FPOS",
                feedback.ToString("F6", CultureInfo.InvariantCulture),
                "CPOS",
                command.ToString("F6", CultureInfo.InvariantCulture)
            };
            if (readExtendedStatus)
            {
                var auxiliaryFeedback = Convert.ToDouble(GetAxisStatusValue(results, AxisStatusItem.AuxiliaryFeedback, axis), CultureInfo.InvariantCulture);
                var currentFeedback = Convert.ToDouble(GetAxisStatusValue(results, AxisStatusItem.CurrentFeedback, axis), CultureInfo.InvariantCulture);
                var galvoLaserOutputRaw = Convert.ToDouble(GetAxisStatusValue(results, AxisStatusItem.GalvoLaserOutputRaw, axis), CultureInfo.InvariantCulture);
                var motorTemperature = ReadMotorTemperatureFromAnalogInput(results, analogInputItem, axis);
                fields.AddRange([
                    "AUX", 
                    auxiliaryFeedback.ToString("F6", CultureInfo.InvariantCulture), 
                    "CURRENT", 
                    currentFeedback.ToString("F6", CultureInfo.InvariantCulture), 
                    "LASER_RAW", 
                    galvoLaserOutputRaw.ToString("F6", CultureInfo.InvariantCulture), 
                    "AIO_NO", 
                    analogInputNo.ToString(CultureInfo.InvariantCulture), 
                    "MOTOR_TEMP", 
                    motorTemperature.ToString("F6", CultureInfo.InvariantCulture)
                ]);
            }

            fields.AddRange([
                "DRIVE", 
                Sanitize(driveStatus), 
                "FAULT", 
                fault.ToString(CultureInfo.InvariantCulture), 
                "HOME", 
                Sanitize(axisStatus), 
                "IN_POSITION", 
                ReadStatusFlag(driveStatus, (long)DriveStatus.InPosition), 
                "MOTION_DONE", 
                ReadStatusFlag(axisStatus, (long)AxisStatus.MotionDone)
            ]);
            return string.Join(":", fields);
        }

        private static string ReadStatusFlag(string value, long mask)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
                return (numeric & mask) == mask ? "1" : "0";
            if (Enum.TryParse<DriveStatus>(value, out var drive) && mask == (long)DriveStatus.InPosition)
                return ((long)drive & mask) == mask ? "1" : "0";
            if (Enum.TryParse<AxisStatus>(value, out var axis) && mask == (long)AxisStatus.MotionDone)
                return ((long)axis & mask) == mask ? "1" : "0";
            return "UNKNOWN";
        }

        private static double ReadMotorTemperatureFromAnalogInput(StatusItemResults results, AxisStatusItem analogInputItem, CAutoAxis axis)
        {
            return Convert.ToDouble(GetAxisStatusValue(results, analogInputItem, axis), CultureInfo.InvariantCulture);
        }

        private static int ReadAxisStatusAnalogInputNo(IReadOnlyList<string> tokens, int valueIndex)
        {
            if (tokens.Count <= valueIndex)
            {
                return 0;
            }

            if (tokens[valueIndex].Equals("TASK", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return ReadAnalogInputNo(tokens[valueIndex]);
        }

        private static bool ReadAxisStatusExtendedFlag(IReadOnlyList<string> tokens, int valueIndex)
        {
            if (tokens.Count <= valueIndex)
            {
                return true;
            }

            if (tokens[valueIndex].Equals("BASIC", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static int ReadAnalogInputNo(string value)
        {
            var analogInputNo = ReadInt(value, "AIO");
            if (analogInputNo < 0 || analogInputNo > 3)
            {
                throw new InvalidOperationException($"Automation1 analog input number is out of range. AIO={analogInputNo}, Range=0~3");
            }

            return analogInputNo;
        }

        private static AxisStatusItem ToAnalogInputStatusItem(int analogInputNo)
        {
            switch (analogInputNo)
            {
                case 0:
                    return AxisStatusItem.AnalogInput0;
                case 1:
                    return AxisStatusItem.AnalogInput1;
                case 2:
                    return AxisStatusItem.AnalogInput2;
                case 3:
                    return AxisStatusItem.AnalogInput3;
                default:
                    throw new InvalidOperationException($"Automation1 analog input number is out of range. AIO={analogInputNo}, Range=0~3");
            }
        }

        private static CAutoAxis CreateAxisReference(string value)
        {
            var axisName = value.Trim();
            if (int.TryParse(axisName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var axisNo))
            {
                return new CAutoAxis(axisName, axisNo);
            }

            if (string.IsNullOrWhiteSpace(axisName))
            {
                throw new InvalidOperationException("Automation1 axis name is empty.");
            }

            return new CAutoAxis(axisName, null);
        }

        private static void ExecuteAxis(CAutoAxis axis, Action<int> byNumber, Action<string> byName)
        {
            if (axis.AxisNo.HasValue)
            {
                byNumber(axis.AxisNo.Value);
                return;
            }

            byName(axis.AxisName);
        }

        private static void ExecuteAxis(CAutoAxis axis, Action<int, double, double> byNumber, Action<string, double, double> byName, double value, double velocity)
        {
            if (axis.AxisNo.HasValue)
            {
                byNumber(axis.AxisNo.Value, value, velocity);
                return;
            }

            byName(axis.AxisName, value, velocity);
        }

        private static object GetAxisStatusValue(StatusItemResults results, AxisStatusItem item, CAutoAxis axis)
        {
            return axis.AxisNo.HasValue ? results.Axis[item, axis.AxisNo.Value].Value : results.Axis[item, axis.AxisName].Value;
        }

        private static string ExecuteIoFunction(Controller controller, IReadOnlyList<string> tokens)
        {
            if (tokens.Count < 3)
            {
                throw new InvalidOperationException("Automation1 IO command is invalid.");
            }

            var address = tokens[1];
            var(axisNo, bitNo, isOutput) = ParseIoAddress(address);
            var command = tokens[2].ToUpperInvariant();
            if (command == "READ")
            {
                return ReadIo(controller, address, axisNo, bitNo, isOutput);
            }

            if (command is not ("ON" or "OFF"))
            {
                throw new InvalidOperationException($"Automation1 IO command is unknown: {command}");
            }

            if (!isOutput)
            {
                throw new InvalidOperationException($"Automation1 IO output command can use DO/Y address only: {address}");
            }

            controller.Runtime.Commands.IO.DigitalOutputSet(axisNo, bitNo, command == "ON" ? 1 : 0);
            return $"OK:IO:{Sanitize(address)}:{command}";
        }

        private static string ReadIo(Controller controller, string address, int axisNo, int bitNo, bool isOutput)
        {
            var statusItem = isOutput ? AxisStatusItem.DigitalOutput : AxisStatusItem.DigitalInput;
            var configuration = new StatusItemConfiguration();
            configuration.Axis.Add(statusItem, axisNo);
            var results = controller.Runtime.Status.GetStatusItems(configuration);
            var maskValue = Convert.ToInt64(results.Axis[statusItem, axisNo].Value, CultureInfo.InvariantCulture);
            var isOn = (maskValue & (1L << bitNo)) != 0;
            return $"OK:IO:{Sanitize(address)}:{(isOn ? "ON" : "OFF")}";
        }

        private static string ExecuteSystemFunction(Controller controller, IReadOnlyList<string> tokens)
        {
            if (tokens.Count < 2)
            {
                throw new InvalidOperationException("Automation1 system command is invalid.");
            }

            var command = tokens[1].ToUpperInvariant();
            switch (command)
            {
                case "AXES":
                    return "OK:AXES:" + string.Join(",", controller.Runtime.Axes.Select(axis => Uri.EscapeDataString(axis.AxisName)));
                case "PROCESSOR_USAGE":
                    return ReadProcessorUsage(controller);
                case "RESET":
                    return ResetController(controller);
                default:
                    throw new InvalidOperationException($"Automation1 system command is unknown: {command}");
            }
        }

        private static string ResetController(Controller controller)
        {
            controller.Reset();
            return "OK:SYSTEM:RESET";
        }

        private static string ReadProcessorUsage(Controller controller)
        {
            var configuration = new StatusItemConfiguration();
            configuration.System.Add(SystemStatusItem.EstimatedProcessorUsage);
            var results = controller.Runtime.Status.GetStatusItems(configuration);
            var usage = Convert.ToDouble(results.System[SystemStatusItem.EstimatedProcessorUsage].Value, CultureInfo.InvariantCulture);
            return $"OK:SYSTEM:PROCESSOR_USAGE:{usage.ToString("F3", CultureInfo.InvariantCulture)}";
        }

        private static string ExecuteStatus(Controller controller)
        {
            var host = controller.Information.Host ?? "";
            var port = controller.Information.Port;
            var taskCount = controller.IsRunning ? controller.Runtime.Tasks.Count : 0;
            var axisCount = controller.IsRunning ? controller.Runtime.Axes.Count : 0;
            return $"OK:STATUS:{Sanitize(host)}:{port}:{controller.IsRunning}:{taskCount}:{axisCount}";
        }

        private Controller EnsureController()
        {
            if (_controller is null)
            {
                throw new InvalidOperationException("Automation1 is not connected.");
            }

            if (!_controller.IsRunning)
            {
                throw new InvalidOperationException("Automation1 controller is not running.");
            }

            return _controller;
        }

        private void CloseController()
        {
            if (_controller is null)
            {
                return;
            }

            try
            {
                _controller.Disconnect();
            }
            catch
            {
            // Disconnect should never block application shutdown.
            }
            finally
            {
                _controller = null;
            }
        }

        private static void ValidateTask(Controller controller, int taskIndex)
        {
            var taskCount = controller.Runtime.Tasks.Count;
            if (taskIndex <= 0 || taskIndex > taskCount)
            {
                throw new InvalidOperationException($"Automation1 task index is out of range. Task={taskIndex}, Range=1~{taskCount}");
            }
        }

        private static string[] SplitColon(string value)
        {
            return value.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        private static string[] SplitPipe(string value)
        {
            return value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        private static int ReadInt(string value, string fieldName)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : throw new InvalidOperationException($"Automation1 {fieldName} value is invalid: {value}");
        }

        private static int ReadInt(IReadOnlyList<string> values, int index, int defaultValue, string fieldName)
        {
            return values.Count <= index || string.IsNullOrWhiteSpace(values[index]) ? defaultValue : ReadInt(values[index], fieldName);
        }

        private static double ReadDouble(IReadOnlyList<string> values, int index, string fieldName)
        {
            if (values.Count <= index || !double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            {
                throw new InvalidOperationException($"Automation1 {fieldName} value is invalid.");
            }

            return result;
        }

        private static double ReadDouble(IReadOnlyList<string> values, int index, double defaultValue, string fieldName)
        {
            return values.Count <= index || string.IsNullOrWhiteSpace(values[index]) ? defaultValue : ReadDouble(values, index, fieldName);
        }

        private static (int AxisNo, int BitNo, bool IsOutput) ParseIoAddress(string address)
        {
            var normalized = address.Trim().ToUpperInvariant();
            var isOutput = normalized.StartsWith('Y') || normalized.Contains("DO", StringComparison.Ordinal);
            normalized = normalized.Replace("AXIS", "", StringComparison.OrdinalIgnoreCase).Replace("A", "", StringComparison.OrdinalIgnoreCase).Replace("DO", "", StringComparison.OrdinalIgnoreCase).Replace("DI", "", StringComparison.OrdinalIgnoreCase).Replace("Y", "", StringComparison.OrdinalIgnoreCase).Replace("X", "", StringComparison.OrdinalIgnoreCase);
            var parts = normalized.Split(['.', ':', '/', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var axisNo) || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitNo) || axisNo < 0 || bitNo < 0 || bitNo > 63)
            {
                throw new InvalidOperationException($"Automation1 IO address is invalid: {address}. Use A0.DO1, A0.DI1, 0.1, or 0:1.");
            }

            return (axisNo, bitNo, isOutput);
        }

        private static string Sanitize(string value)
        {
            return value.Replace(":", " ", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        }

        private static string CreateErrorResponse(Exception exception)
        {
            return string.Join(":", "ERROR", "AUTOMATION1", Sanitize(exception.GetType().Name), Sanitize(CreateErrorMessage(exception)));
        }

        private static string CreateErrorMessage(Exception exception)
        {
            var message = exception.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = exception.GetBaseException().Message;
            }

            return string.IsNullOrWhiteSpace(message) ? exception.GetType().Name : message.Trim();
        }

        private sealed record CAutoAxis(string AxisName, int? AxisNo)
        {
            public string CommandText
            {
                get
                {
                    return AxisNo.HasValue ? AxisNo.Value.ToString(CultureInfo.InvariantCulture) : AxisName;
                }
            }
        }

        private sealed record CBufferedRunRequest(int TaskIndex, string ControllerFile);
        private sealed record CBufferedRunQueue(CBufferedRunRequest Request, CommandQueue Queue, int InitialNumberOfTimesEmptied);
        private sealed record CBufferedRunTaskState(
            int TaskIndex, 
            string SourceFile, 
            TaskState State, 
            int NumberOfTimesEmptied, 
            int InitialNumberOfTimesEmptied, 
            int NumberOfExecutedCommands, 
            int NumberOfUnexecutedCommands, 
            bool IsQueueEmpty, 
            string Error
        )
        {
            public bool FeedComplete { get; init; }
        }
#endregion

#region ConfiguredHomeSpeed
        // ExecuteAutomation1Function invokes this while holding _commLock. Configuration is not thread safe;
        // keep each fresh read / one-parameter update / save / readback in that same critical section.
        private static string ExecuteConfiguredHomeSpeedFunction(Controller controller, IReadOnlyList<string> tokens)
        {
            if (tokens.Count < 3)
                throw new InvalidOperationException("Automation1 saved HomeSpeed command is invalid.");
            var command = tokens[1].ToUpperInvariant();
            if (command == "HOME_SPEEDS" && tokens.Count == 3)
            {
                var axes = tokens[2].Split(',', StringSplitOptions.None).Select(ValidateConfiguredHomeSpeedAxis).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var configuration = controller.Configuration.Parameters.GetConfiguration();
                var values = axes.ToDictionary(axis => axis, axis => ConfiguredHomeSpeedAxis(configuration, axis).Homing.HomeSpeed.Value, StringComparer.OrdinalIgnoreCase);
                var units = axes.ToDictionary(axis => axis, axis =>
                {
                    var primaryUnits = ConfiguredHomeSpeedAxis(configuration, axis).Units.UnitsName.Value?.Trim();
                    return string.IsNullOrEmpty(primaryUnits) ? string.Empty : $"{primaryUnits}/sec";
                }, StringComparer.OrdinalIgnoreCase);
                return "OK:CONFIG_HOME_SPEEDS:" + JsonSerializer.Serialize(new { Values = values, Units = units });
            }

            if (command == "HOME_SPEED" && tokens.Count == 4)
            {
                var axis = ValidateConfiguredHomeSpeedAxis(tokens[2]);
                var speed = ReadDouble(tokens, 3, "HOME_SPEED");
                if (!double.IsFinite(speed) || speed <= 0)
                    throw new ArgumentOutOfRangeException(nameof(speed), "Home speed must be finite and greater than zero.");
                // Never retain a full configuration between UI reads and writes: other saved parameters
                // may have changed in the meantime. Read it anew and replace only this axis' HomeSpeed.
                var configuration = controller.Configuration.Parameters.GetConfiguration();
                ConfiguredHomeSpeedAxis(configuration, axis).Homing.HomeSpeed.Value = speed;
                controller.Configuration.Parameters.SetConfiguration(configuration);
                var readback = controller.Configuration.Parameters.GetConfiguration();
                var saved = ConfiguredHomeSpeedAxis(readback, axis).Homing.HomeSpeed.Value;
                if (!double.IsFinite(saved) || saved != speed)
                    throw new InvalidOperationException($"Saved HomeSpeed did not match the requested value. Axis={axis}");
                return "OK:CONFIG_HOME_SPEED:" + saved.ToString("R", CultureInfo.InvariantCulture);
            }

            throw new InvalidOperationException($"Automation1 saved HomeSpeed command is unknown: {command}");
        }

        private static AxisParameterCategory ConfiguredHomeSpeedAxis(ConfiguredParameters configuration, string axis) => int.TryParse(axis, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? configuration.Axes[number] : configuration.Axes[axis];
        private static string ValidateConfiguredHomeSpeedAxis(string value)
        {
            var axis = value.Trim();
            var isNumber = axis.Length > 0 && axis.All(char.IsAsciiDigit);
            var isName = axis.Length > 0 && char.IsAsciiLetter(axis[0]) && axis.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
            if (!isNumber && !isName || isNumber && !int.TryParse(axis, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new InvalidOperationException($"Automation1 configured axis reference is invalid: {value}");
            return axis;
        }
#endregion

#region MofReadiness
        private static bool IsMofReadinessReadRequest(string function)
        {
            if (!TryGetAutomationBody(function, out var body))
                return false;
            var fields = SplitPipe(body);
            return fields.Length == 3 && fields[0].Equals("VARIABLE", StringComparison.OrdinalIgnoreCase) && (fields[1].Equals("GET_GLOBAL_INTEGER", StringComparison.OrdinalIgnoreCase) || fields[1].Equals("GET_MOF_READINESS", StringComparison.OrdinalIgnoreCase));
        }

        private static string ExecuteVariableFunction(Controller controller, string[] fields)
        {
            if (fields.Length >= 2 && fields[1] is "GET_MOF_READINESS" or "SET_MOF_READINESS")
                return ExecuteMofReadinessFunction(fields, controller.Runtime.Tasks.Count, index => controller.Runtime.Variables.Global.GetInteger(index), (index, value) => controller.Runtime.Variables.Global.SetInteger(index, value));
            if (fields.Length < 3 || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
                throw new InvalidOperationException("A non-negative controller global integer index is required.");
            if (fields.Length == 3 && fields[1] == "GET_GLOBAL_INTEGER")
            {
                var value = controller.Runtime.Variables.Global.GetInteger(index);
                return FormattableString.Invariant($"OK:GLOBAL_INTEGER:{index}:{value}");
            }

            if (fields.Length != 4 || fields[1] != "SET_GLOBAL_INTEGER" || !long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var written))
                throw new InvalidOperationException("Invalid controller global integer command.");
            controller.Runtime.Variables.Global.SetInteger(index, written);
            return "OK:GLOBAL_INTEGER_WRITE";
        }

        private static string ExecuteMofReadinessFunction(string[] fields, int taskCount, Func<int, long> read, Action<int, long> write)
        {
            if (fields.Length < 3 || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var task))
                throw new InvalidOperationException("MOF readiness requires an Automation1 Task number.");
            // Validate against the same Controller instance used for the variable access.
            // Simulation keeps isolated per-Task state; no hardware upper bound is invented there.
            var index = CMofReadiness.VariableIndex(task, taskCount);
            if (fields.Length == 3 && fields[1] == "GET_MOF_READINESS")
                return FormattableString.Invariant($"OK:GLOBAL_INTEGER:{index}:{read(index)}");
            if (fields.Length != 4 || fields[1] != "SET_MOF_READINESS" || !long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException("Invalid MOF readiness command.");
            write(index, value);
            return "OK:GLOBAL_INTEGER_WRITE";
        }
#endregion
    }
}
