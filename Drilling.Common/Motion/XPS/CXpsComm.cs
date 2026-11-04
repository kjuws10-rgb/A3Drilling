using System.Diagnostics;
using System.Globalization;
using CommandInterfaceXPS;
using Drilling.Common.Alarm;
using Drilling.Common.Interface;
using Drilling.Common.InterLock;
using Drilling.Common.Managers;
using Drilling.Common.Motion;
using Drilling.Common.Station;

namespace Drilling.Common.Motion;
[CCommType("XpsNet")]
internal sealed class CXpsComm(ST_INTERFACE_DATA data, ST_INTERFACE_CONNECT_OPTION option) : CCommBase(data, option)
{
    private readonly SemaphoreSlim _commLock = new(1, 1);
    private readonly object _diagnosticLock = new();
    private long _diagnosticSequence;
    private long _lastAppliedDiagnosticSequence;
    private long _priorityStopGeneration;
    private XPS? _api;
    public override async Task Connect(CancellationToken cancellationToken = default)
    {
        await _commLock.WaitAsync(cancellationToken);
        try
        {
            ConnectLocked();
        }
        catch (Exception ex)
        {
            CloseApi();
            SetPrimaryError(ex);
        }
        finally
        {
            _commLock.Release();
        }
    }

    public override async Task Disconnect(CancellationToken cancellationToken = default)
    {
        await _commLock.WaitAsync(cancellationToken);
        try
        {
            CloseApi();
            SetPrimaryState(EN_COMM_STATE.Offline);
        }
        finally
        {
            _commLock.Release();
        }
    }

    public override async Task<string> Execute(string function, CancellationToken cancellationToken = default)
    {
        var observedStopGeneration = Volatile.Read(ref _priorityStopGeneration);
        if (IsPriorityStopCommand(function))
        {
            Interlocked.Increment(ref _priorityStopGeneration);
            return await ExecutePriorityStop(function, cancellationToken);
        }

        await _commLock.WaitAsync(cancellationToken);
        void ThrowIfPriorityStopOccurredCallback()
        {
            ThrowIfPriorityStopOccurred(observedStopGeneration);
        }

        try
        {
            if (observedStopGeneration != Volatile.Read(ref _priorityStopGeneration))
            {
                return CreateErrorResponse("XPS command invalidated by priority stop.");
            }

            var diagnosticSequence = BeginCommandDiagnostic(function);
            try
            {
                if (_api is null || ConnectionState != EN_COMM_STATE.Online)
                {
                    ConnectLocked();
                }

                if (_api is null || ConnectionState != EN_COMM_STATE.Online)
                {
                    throw new InvalidOperationException("XPS primary connection is offline.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfPriorityStopOccurred(observedStopGeneration);
                var response = ExecuteXpsFunction(_api, function, cancellationToken, ThrowIfPriorityStopOccurredCallback);
                CompletePrimaryCommand(diagnosticSequence, response);
                return response;
            }
            catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FailPrimaryCommand(diagnosticSequence, ex);
            }
        }
        finally
        {
            _commLock.Release();
        }
    }

    private void ConnectLocked()
    {
        CloseApi();
        if (string.IsNullOrWhiteSpace(Option.RemoteAddress) || Option.Port <= 0)
        {
            throw new InvalidOperationException("XPS endpoint is invalid.");
        }

        _api = new XPS();
        var result = _api.OpenInstrument(Option.RemoteAddress, Option.Port, Option.TimeoutMs);
        CheckResult(_api, result, "OpenInstrument");
        SetPrimaryConnected();
    }

    private static string ExecuteXpsFunction(XPS api, string function, CancellationToken cancellationToken, Action? validateCommandGeneration = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        validateCommandGeneration?.Invoke();
        var tokens = function.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || !tokens[0].Equals("XPS", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteRawCommand(api, function);
        }

        return tokens[1].ToUpperInvariant() switch
        {
            "AXIS" => ExecuteAxisFunction(api, tokens, cancellationToken, validateCommandGeneration),
            "IO" => ExecuteIoFunction(api, tokens),
            _ => ExecuteRawCommand(api, function)};
    }

    private static string ExecuteAxisFunction(XPS api, IReadOnlyList<string> tokens, CancellationToken cancellationToken, Action? validateCommandGeneration)
    {
        if (tokens.Count < 5)
        {
            throw new InvalidOperationException("XPS axis command is invalid.");
        }

        var axisName = tokens[3];
        var groupName = tokens.Count >= 6 ? tokens[4] : axisName;
        var commandIndex = tokens.Count >= 6 ? 5 : 4;
        var command = tokens[commandIndex].ToUpperInvariant();
        cancellationToken.ThrowIfCancellationRequested();
        validateCommandGeneration?.Invoke();
        switch (command)
        {
            case "SERVO_ON":
                CheckResult(api, api.GroupMotionEnable(groupName, out var servoOnError), servoOnError);
                return $"OK:AXIS:{axisName}:SERVO_ON";
            case "SERVO_OFF":
                CheckResult(api, api.GroupMotionDisable(groupName, out var servoOffError), servoOffError);
                return $"OK:AXIS:{axisName}:SERVO_OFF";
            case "HOME":
                CheckResult(api, api.GroupInitialize(groupName, out var initializeError), initializeError);
                validateCommandGeneration?.Invoke();
                CheckResult(api, api.GroupHomeSearch(groupName, out var homeError), homeError);
                return $"OK:AXIS:{axisName}:HOME";
            case "MOVE_ABS":
                CheckResult(api, api.GroupMoveAbsolute(groupName, [ReadDouble(tokens, commandIndex + 1, "MOVE_ABS")], 1, out var moveAbsError), moveAbsError);
                return $"OK:AXIS:{axisName}:MOVE_ABS";
            case "MOVE_REL":
                CheckResult(api, api.GroupMoveRelative(groupName, [ReadDouble(tokens, commandIndex + 1, "MOVE_REL")], 1, out var moveRelError), moveRelError);
                return $"OK:AXIS:{axisName}:MOVE_REL";
            case "SET_PROFILE":
                CheckResult(api, api.PositionerSGammaVelocityAndAccelerationSet(axisName, ReadPositiveDouble(tokens, commandIndex + 1, "SET_PROFILE VELOCITY"), ReadPositiveDouble(tokens, commandIndex + 2, "SET_PROFILE ACCELERATION"), out var profileError), profileError);
                return $"OK:AXIS:{axisName}:SET_PROFILE";
            case "JOG":
                var jogVelocity = ReadDouble(tokens, commandIndex + 1, "JOG VELOCITY");
                var jogAcceleration = ReadPositiveDouble(tokens, commandIndex + 2, "JOG ACCELERATION");
                if (Math.Abs(jogVelocity) <= double.Epsilon)
                {
                    throw new InvalidOperationException("XPS JOG velocity must not be zero.");
                }

                try
                {
                    validateCommandGeneration?.Invoke();
                    CheckResult(api, api.GroupJogModeEnable(groupName, out var jogEnableError), jogEnableError);
                    cancellationToken.ThrowIfCancellationRequested();
                    validateCommandGeneration?.Invoke();
                    CheckResult(api, api.GroupJogParametersSet(groupName, [jogVelocity], [jogAcceleration], 1, out var jogSetError), jogSetError);
                    cancellationToken.ThrowIfCancellationRequested();
                    validateCommandGeneration?.Invoke();
                }
                catch (Exception jogException)
                {
                    try
                    {
                        StopJogAndDisable(api, groupName);
                    }
                    catch (Exception cleanupException)
                    {
                        throw new AggregateException("XPS jog start and cleanup both failed.", jogException, cleanupException);
                    }

                    throw;
                }

                return $"OK:AXIS:{axisName}:JOG";
            case "JOG_STOP":
                StopJogAndDisable(api, groupName);
                return $"OK:AXIS:{axisName}:JOG_STOP";
            case "STOP":
                CheckResult(api, api.GroupMoveAbort(groupName, out var stopError), stopError);
                return $"OK:AXIS:{axisName}:STOP";
            case "RESET_ALARM":
                CheckResult(api, api.GroupInitialize(groupName, out var resetError), resetError);
                return $"OK:AXIS:{axisName}:RESET_ALARM";
            case "READ":
                CheckResult(api, api.GroupPositionCurrentGet(groupName, out var position, 1, out var readError), readError);
                var positionValue = ReadRequiredFirst(position, "position");
                CheckResult(api, api.GroupStatusGet(groupName, out var groupStatus, out var groupStatusError), groupStatusError);
                CheckResult(api, api.GroupStatusStringGet(groupStatus, out var groupStatusText, out var groupStatusTextError), groupStatusTextError);
                CheckResult(api, api.GroupMotionStatusGet(groupName, out var motionStatus, 1, out var motionStatusError), motionStatusError);
                if (motionStatus.Length == 0)
                {
                    throw new InvalidOperationException("XPS motion status response is empty.");
                }

                CheckResult(api, api.PositionerHardwareStatusGet(axisName, out var hardwareStatus, out var hardwareStatusError), hardwareStatusError);
                CheckResult(api, api.GroupVelocityCurrentGet(groupName, out var currentVelocity, 1, out var velocityError), velocityError);
                var velocity = ReadRequiredFirst(currentVelocity, "velocity");
                CheckResult(api, api.GroupAccelerationCurrentGet(groupName, out var currentAcceleration, 1, out var accelerationError), accelerationError);
                var acceleration = ReadRequiredFirst(currentAcceleration, "acceleration");
                return string.Join(
                    ":", 
                    "OK", 
                    "AXIS", 
                    axisName, 
                    "FPOS", 
                    positionValue.ToString("F6", CultureInfo.InvariantCulture), 
                    "GROUP_STATUS_TEXT", 
                    SanitizeResponseText(groupStatusText), 
                    "MOTION_STATUS", 
                    motionStatus[0].ToString(CultureInfo.InvariantCulture), 
                    "HARDWARE_STATUS", 
                    hardwareStatus.ToString(CultureInfo.InvariantCulture), 
                    "FVEL", 
                    velocity.ToString("F6", CultureInfo.InvariantCulture), 
                    "FACCEL", 
                    acceleration.ToString("F6", CultureInfo.InvariantCulture)
                );
            default:
                throw new InvalidOperationException($"XPS axis command is unknown: {command}");
        }
    }

    private static string ExecuteIoFunction(XPS api, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 4)
        {
            throw new InvalidOperationException("XPS IO command is invalid.");
        }

        var address = tokens[2].Trim().ToUpperInvariant();
        var(gpioName, bitNo) = ParseDigitalIoAddress(address);
        var command = tokens[3].ToUpperInvariant();
        var mask = checked((ushort)(1 << bitNo));
        if (command == "READ")
        {
            CheckResult(api, api.GPIODigitalGet(gpioName, out var value, out var readError), readError);
            return $"OK:IO:{address}:{((value & mask) != 0 ? "ON" : "OFF")}";
        }

        if (command is not ("ON" or "OFF"))
        {
            throw new InvalidOperationException($"XPS IO command is unknown: {command}");
        }

        var output = command == "ON" ? mask : (ushort)0;
        CheckResult(api, api.GPIODigitalSet(gpioName, mask, output, out var writeError), writeError);
        return $"OK:IO:{address}:{command}";
    }

    private static string ExecuteRawCommand(XPS api, string function)
    {
        return function.Trim().ToUpperInvariant() switch
        {
            "STATUS" => ExecuteStatus(api),
            "SOCKETS" => ExecuteSockets(api),
            _ => throw new InvalidOperationException($"XPS raw command is not supported yet: {function}")};
        string ExecuteStatus(XPS xps)
        {
            CheckResult(xps, xps.ControllerStatusGet(out var status, out var statusError), statusError);
            CheckResult(xps, xps.ControllerStatusStringGet(status, out var statusText, out var statusTextError), statusTextError);
            return $"OK:STATUS:{status}:{statusText}";
        }

        string ExecuteSockets(XPS xps)
        {
            CheckResult(xps, xps.SocketsStatusGet(out var result, out var socketError), socketError);
            return $"OK:SOCKETS:{result}";
        }
    }

    private void CloseApi()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            _api.CloseInstrument();
        }
        catch
        {
        // Close should never block application shutdown.
        }
        finally
        {
            _api = null;
        }
    }

    private static void CheckResult(XPS api, int result, string detail)
    {
        if (result == 0)
        {
            return;
        }

        var error = "";
        try
        {
            _ = api.ErrorStringGet(result, out error, out _);
        }
        catch
        {
            error = "";
        }

        var message = string.IsNullOrWhiteSpace(error) ? detail : error;
        throw new InvalidOperationException($"XPS command failed. Code={result}, Detail={message}");
    }

    private static double ReadDouble(IReadOnlyList<string> tokens, int index, string command)
    {
        if (tokens.Count <= index || !double.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || !double.IsFinite(result))
        {
            throw new InvalidOperationException($"XPS {command} parameter must be finite.");
        }

        return result;
    }

    private async Task<string> ExecutePriorityStop(string function, CancellationToken cancellationToken)
    {
        var diagnosticSequence = BeginCommandDiagnostic(function);
        string SelectValue()
        {
            return ExecutePriorityStopCore(function);
        }

        global::System.AggregateException? SelectAggregateexception(global::System.Threading.Tasks.Task<string> task)
        {
            return _ = task.Exception;
        }

        try
        {
            var stopTask = Task.Factory.StartNew(SelectValue, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            _ = stopTask.ContinueWith(SelectAggregateexception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var response = await stopTask.WaitAsync(cancellationToken);
            CompletePriorityStop(diagnosticSequence, response);
            return response;
        }
        catch (OperationCanceledException exception)when (cancellationToken.IsCancellationRequested)
        {
            return FailPriorityStop(diagnosticSequence, new TimeoutException($"XPS priority stop timed out after {(Option.TimeoutMs > 0 ? Option.TimeoutMs : 5000)} ms.", exception));
        }
        catch (Exception ex)
        {
            return FailPriorityStop(diagnosticSequence, ex);
        }
    }

    private long BeginCommandDiagnostic(string function)
    {
        lock (_diagnosticLock)
        {
            var sequence = ++_diagnosticSequence;
            _lastAppliedDiagnosticSequence = sequence;
            LastSent = function;
            LastReceived = "";
            LastChangedAt = DateTimeOffset.Now;
            return sequence;
        }
    }

    private void CompletePrimaryCommand(long sequence, string response)
    {
        lock (_diagnosticLock)
        {
            if (sequence < _lastAppliedDiagnosticSequence)
            {
                return;
            }

            LastReceived = response;
            LastError = "";
            SetState(EN_COMM_STATE.Online);
        }
    }

    private string FailPrimaryCommand(long sequence, Exception exception)
    {
        var response = CreateErrorResponse(exception);
        lock (_diagnosticLock)
        {
            if (sequence < _lastAppliedDiagnosticSequence)
            {
                return response;
            }

            LastReceived = response;
            LastError = exception.Message;
            SetState(EN_COMM_STATE.Offline);
            return response;
        }
    }

    private void CompletePriorityStop(long sequence, string response)
    {
        lock (_diagnosticLock)
        {
            if (sequence < _lastAppliedDiagnosticSequence)
            {
                return;
            }

            LastReceived = response;
            LastChangedAt = DateTimeOffset.Now;
        }
    }

    private string FailPriorityStop(long sequence, Exception exception)
    {
        var response = CreateErrorResponse(exception);
        lock (_diagnosticLock)
        {
            if (sequence < _lastAppliedDiagnosticSequence)
            {
                return response;
            }

            LastReceived = response;
            LastError = $"XPS priority stop failed. {exception.Message}";
            LastChangedAt = DateTimeOffset.Now;
            return response;
        }
    }

    private static string CreateErrorResponse(Exception exception)
    {
        return CreateErrorResponse(exception.Message);
    }

    private static string CreateErrorResponse(string message)
    {
        var detail = SanitizeResponseText(message);
        return $"ERR:XPS:{(string.IsNullOrWhiteSpace(detail) ? "UNKNOWN" : detail)}";
    }

    private void ThrowIfPriorityStopOccurred(long observedStopGeneration)
    {
        if (observedStopGeneration != Volatile.Read(ref _priorityStopGeneration))
        {
            throw new InvalidOperationException("XPS command invalidated by priority stop.");
        }
    }

    private void SetPrimaryConnected()
    {
        lock (_diagnosticLock)
        {
            LastError = "";
            SetState(EN_COMM_STATE.Online);
        }
    }

    private void SetPrimaryState(EN_COMM_STATE state)
    {
        lock (_diagnosticLock)
        {
            SetState(state);
        }
    }

    private void SetPrimaryError(Exception exception)
    {
        lock (_diagnosticLock)
        {
            SetError(exception);
        }
    }

    private string ExecutePriorityStopCore(string function)
    {
        if (string.IsNullOrWhiteSpace(Option.RemoteAddress) || Option.Port <= 0)
        {
            throw new InvalidOperationException("XPS endpoint is invalid.");
        }

        var stopApi = new XPS();
        try
        {
            CheckResult(stopApi, stopApi.OpenInstrument(Option.RemoteAddress, Option.Port, Option.TimeoutMs), "OpenInstrument for priority stop");
            return ExecuteXpsFunction(stopApi, function, CancellationToken.None);
        }
        finally
        {
            try
            {
                stopApi.CloseInstrument();
            }
            catch
            {
            // The stop result is more important than a temporary socket close failure.
            }
        }
    }

    private static bool IsPriorityStopCommand(string function)
    {
        var tokens = function.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 5 || !tokens[0].Equals("XPS", StringComparison.OrdinalIgnoreCase) || !tokens[1].Equals("AXIS", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commandIndex = tokens.Length >= 6 ? 5 : 4;
        return tokens[commandIndex].Equals("JOG_STOP", StringComparison.OrdinalIgnoreCase) || tokens[commandIndex].Equals("STOP", StringComparison.OrdinalIgnoreCase);
    }

    private static void StopJogAndDisable(XPS api, string groupName)
    {
        CheckResult(api, api.GroupMoveAbort(groupName, out var abortError), abortError);
        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            var disableResult = api.GroupJogModeDisable(groupName, out var disableError);
            if (disableResult == 0)
            {
                return;
            }

            if (!IsGroupJogging(api, groupName))
            {
                return;
            }

            if (Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(2))
            {
                CheckResult(api, disableResult, disableError);
            }

            Thread.Sleep(20);
        }
    }

    private static bool IsGroupJogging(XPS api, string groupName)
    {
        CheckResult(api, api.GroupStatusGet(groupName, out var status, out var statusError), statusError);
        CheckResult(api, api.GroupStatusStringGet(status, out var statusText, out var statusTextError), statusTextError);
        return statusText.Contains("JOGGING", StringComparison.OrdinalIgnoreCase);
    }

    private static double ReadPositiveDouble(IReadOnlyList<string> tokens, int index, string command)
    {
        var value = ReadDouble(tokens, index, command);
        if (value <= 0.0)
        {
            throw new InvalidOperationException($"XPS {command} must be greater than 0.");
        }

        return value;
    }

    private static double ReadRequiredFirst(double[]? values, string valueName)
    {
        if (values is not { Length: > 0 } || !double.IsFinite(values[0]))
        {
            throw new InvalidOperationException($"XPS {valueName} status response is empty or non-finite.");
        }

        return values[0];
    }

    private static (string GpioName, int BitNo) ParseDigitalIoAddress(string address)
    {
        var normalized = address.Trim().ToUpperInvariant();
        var separatorIndex = normalized.LastIndexOfAny(['.', ':', '/', '[']);
        if (separatorIndex < 1)
        {
            throw new InvalidOperationException($"XPS digital IO address is invalid: {address}. Use GPIOName.BitNo, for example GPIO1.0");
        }

        var gpioName = normalized[..separatorIndex].TrimEnd(']');
        var bitText = normalized[(separatorIndex + 1)..].TrimEnd(']');
        if (string.IsNullOrWhiteSpace(gpioName) || !int.TryParse(bitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitNo) || bitNo < 0 || bitNo > 15)
        {
            throw new InvalidOperationException($"XPS digital IO address is invalid: {address}. Bit number must be 0~15.");
        }

        return (gpioName, bitNo);
    }

    private static string SanitizeResponseText(string value)
    {
        return value.Replace(":", " ", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
    }
}
