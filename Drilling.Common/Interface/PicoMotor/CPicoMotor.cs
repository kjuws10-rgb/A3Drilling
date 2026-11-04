namespace Drilling.Common.Interface
{
    using System.Globalization;
    using NewFocus.Picomotor;

    public static class CPicoMotor
    {
        public const int StepPerMillimeter = 50000;
        public static string BuildAxisCommand(int motorNo, string command, long? value = null, bool isQuery = false)
        {
            var axis = Math.Clamp(motorNo, 1, 4);
            if (isQuery)
            {
                return $"{axis}{command}?\n";
            }

            return value is null ? $"{axis}{command}\n" : $"{axis}{command}{value.Value.ToString(CultureInfo.InvariantCulture)}\n";
        }

        public static string BuildAxisCommand(int motorNo, string command, string value)
        {
            var axis = Math.Clamp(motorNo, 1, 4);
            return $"{axis}{command}{value}\n";
        }

        public static string BuildGlobalCommand(string command, bool isQuery = false)
        {
            return isQuery ? $"{command}?\n" : $"{command}\n";
        }

        public static long MillimeterToStep(double value)
        {
            return Convert.ToInt64(Math.Round(value * StepPerMillimeter, MidpointRounding.AwayFromZero));
        }

        public static double StepToMillimeter(long value)
        {
            return value / (double)StepPerMillimeter;
        }

        public static bool TryParseLong(string response, out long value)
        {
            return long.TryParse(NormalizeResponse(response), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryParseInt(string response, out int value)
        {
            return int.TryParse(NormalizeResponse(response), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public static string NormalizeResponse(string response)
        {
            return response.Trim().TrimEnd('\0').Trim();
        }

        public static EN_PICO_MOTOR_ERROR ToError(int errorCode)
        {
            return Enum.IsDefined(typeof(EN_PICO_MOTOR_ERROR), errorCode) ? (EN_PICO_MOTOR_ERROR)errorCode : EN_PICO_MOTOR_ERROR.Error;
        }
    }

    public enum EN_PICO_MOTOR_COMMAND
    {
        Connect,
        Disconnect,
        SelectMotor,
        SetVelocity,
        SetAcceleration,
        StopMotion,
        Home,
        JogNegative,
        JogPositive,
        MoveRelativeNegative,
        MoveRelativePositive,
        MoveAbsolute,
        AllMotorStart,
        AllMotorStop,
        Refresh
    }

    public enum EN_PICO_MOTOR_ERROR
    {
        Ok = 0,
        Error = 1,
        OverTemp = 3,
        CommandNotExist = 6,
        ParameterOutOfRange = 7,
        AxisNoOutOfRange = 9,
        EepromWriteFail = 10,
        EepromReadFail = 11,
        AxisNoMissing = 37,
        CommandParameterMissing = 38,
        Rs485ExtFault = 46,
        Rs485CrcFault = 47,
        ControllerNoOutOfRange = 48,
        ScanInProgress = 49,
        Timeout = -1,
        InvalidResponse = -2,
        NotSupported = -99
    }

    public sealed record ST_PICO_MOTOR_STATUS(
        bool IsConnected, 
        string Controller, 
        int SelectedMotorNo, 
        double Motor1Position, 
        double Motor2Position, 
        double Motor3Position, 
        double Motor4Position, 
        long HomePosition, 
        double CurrentVelocity, 
        double CurrentAcceleration, 
        string MotionState, 
        int ErrorCode, 
        int AllMoveCurrentCount = 0, 
        int AllMoveSetCount = 0, 
        double AllMovePosition = 0.0, 
        bool CommOk = true, 
        EN_PICO_MOTOR_ERROR LastError = EN_PICO_MOTOR_ERROR.Ok, 
        DateTimeOffset? UpdatedAt = null
    )
    {
        public static ST_PICO_MOTOR_STATUS Empty { get; } = new(false, "8742", 1, 0.0, 0.0, 0.0, 0.0, 0, 0.0, 0.0, "IDLE", 0, 0, 0, 0.0, true, EN_PICO_MOTOR_ERROR.Ok, null);
    }

    public sealed class CPicoMotorCommandSession
    {
        private readonly object _syncRoot = new();
        private CmdLib8742? _cmdLib;
        private string _deviceKey = string.Empty;
        public bool IsConnected
        {
            get
            {
                return _cmdLib is not null && !string.IsNullOrWhiteSpace(_deviceKey);
            }
        }

        public string DeviceKey
        {
            get
            {
                return _deviceKey;
            }
        }

        public bool Connect(int discoveryDelayMs = 5000)
        {
            lock (_syncRoot)
            {
                if (IsConnected)
                {
                    return true;
                }

                var deviceKey = string.Empty;
                var cmdLib = new CmdLib8742(false, discoveryDelayMs, ref deviceKey);
                if (string.IsNullOrWhiteSpace(deviceKey))
                {
                    cmdLib.Shutdown();
                    return false;
                }

                if (!cmdLib.Open(deviceKey))
                {
                    cmdLib.Shutdown();
                    return false;
                }

                _cmdLib = cmdLib;
                _deviceKey = deviceKey;
                return true;
            }
        }

        public void Disconnect()
        {
            lock (_syncRoot)
            {
                if (_cmdLib is null)
                {
                    _deviceKey = string.Empty;
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_deviceKey))
                {
                    _cmdLib.Close(_deviceKey);
                }

                _cmdLib.Shutdown();
                _cmdLib = null;
                _deviceKey = string.Empty;
            }
        }

        public string GetIdentification()
        {
            var id = string.Empty;
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.GetIdentification(_deviceKey, ref id);
            }

            return Execute(MatchesCmdlib8742) ? id : string.Empty;
        }

        public int GetPositionStep(int motorNo)
        {
            var position = 0;
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.GetPosition(_deviceKey, ClampMotorNo(motorNo), ref position);
            }

            ExecuteOrThrow(MatchesCmdlib8742, "GetPosition");
            return position;
        }

        public int GetVelocityStep(int motorNo)
        {
            var velocity = 0;
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.GetVelocity(_deviceKey, ClampMotorNo(motorNo), ref velocity);
            }

            ExecuteOrThrow(MatchesCmdlib8742, "GetVelocity");
            return velocity;
        }

        public int GetAccelerationStep(int motorNo)
        {
            var acceleration = 0;
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.GetAcceleration(_deviceKey, ClampMotorNo(motorNo), ref acceleration);
            }

            ExecuteOrThrow(MatchesCmdlib8742, "GetAcceleration");
            return acceleration;
        }

        public bool GetMotionDone(int motorNo)
        {
            var isDone = false;
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.GetMotionDone(_deviceKey, ClampMotorNo(motorNo), ref isDone);
            }

            ExecuteOrThrow(MatchesCmdlib8742, "GetMotionDone");
            return isDone;
        }

        public int GetErrorCode()
        {
            var error = string.Empty;
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.GetErrorNum(_deviceKey, ref error);
            }

            ExecuteOrThrow(MatchesCmdlib8742, "GetErrorNum");
            return int.TryParse(CPicoMotor.NormalizeResponse(error), NumberStyles.Integer, CultureInfo.InvariantCulture, out var errorCode) ? errorCode : (int)EN_PICO_MOTOR_ERROR.InvalidResponse;
        }

        public long GetHomePositionStep(int motorNo)
        {
            var home = string.Empty;
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.Query(_deviceKey, $"{ClampMotorNo(motorNo)}DH?", ref home);
            }

            ExecuteOrThrow(MatchesCmdlib8742, "GetHomePosition");
            return long.TryParse(CPicoMotor.NormalizeResponse(home), NumberStyles.Integer, CultureInfo.InvariantCulture, out var homeStep) ? homeStep : 0L;
        }

        public void SetVelocity(int motorNo, int velocityStep)
        {
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.SetVelocity(_deviceKey, ClampMotorNo(motorNo), velocityStep);
            }

            ExecuteOrThrow(MatchesCmdlib8742, "SetVelocity");
        }

        public void SetAcceleration(int motorNo, int accelerationStep)
        {
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.SetAcceleration(_deviceKey, ClampMotorNo(motorNo), accelerationStep);
            }

            ExecuteOrThrow(MatchesCmdlib8742, "SetAcceleration");
        }

        public void StopMotion(int motorNo)
        {
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.StopMotion(_deviceKey, ClampMotorNo(motorNo));
            }

            ExecuteOrThrow(MatchesCmdlib8742, "StopMotion");
        }

        public void AbortMotion()
        {
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.AbortMotion(_deviceKey);
            }

            ExecuteOrThrow(MatchesCmdlib8742, "AbortMotion");
        }

        public void MoveHome(int motorNo)
        {
            var homeStep = GetHomePositionStep(motorNo);
            AbsoluteMove(motorNo, homeStep);
        }

        public void JogNegative(int motorNo)
        {
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.JogNegative(_deviceKey, ClampMotorNo(motorNo));
            }

            ExecuteOrThrow(MatchesCmdlib8742, "JogNegative");
        }

        public void JogPositive(int motorNo)
        {
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.JogPositive(_deviceKey, ClampMotorNo(motorNo));
            }

            ExecuteOrThrow(MatchesCmdlib8742, "JogPositive");
        }

        public void RelativeMove(int motorNo, long step)
        {
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.RelativeMove(_deviceKey, ClampMotorNo(motorNo), ToInt32Step(step));
            }

            ExecuteOrThrow(MatchesCmdlib8742, "RelativeMove");
        }

        public void AbsoluteMove(int motorNo, long step)
        {
            bool MatchesCmdlib8742(CmdLib8742 command)
            {
                return command.AbsoluteMove(_deviceKey, ClampMotorNo(motorNo), ToInt32Step(step));
            }

            ExecuteOrThrow(MatchesCmdlib8742, "AbsoluteMove");
        }

        public void Dispose()
        {
            Disconnect();
        }

        private bool Execute(Func<CmdLib8742, bool> action)
        {
            lock (_syncRoot)
            {
                if (_cmdLib is null || string.IsNullOrWhiteSpace(_deviceKey))
                {
                    return false;
                }

                return action(_cmdLib);
            }
        }

        private void ExecuteOrThrow(Func<CmdLib8742, bool> action, string commandName)
        {
            if (!Execute(action))
            {
                throw new InvalidOperationException($"PicoMotor CmdLib command failed. Command={commandName}");
            }
        }

        private static int ClampMotorNo(int motorNo)
        {
            return Math.Clamp(motorNo, 1, 4);
        }

        private static int ToInt32Step(long step)
        {
            return step switch
            {
                > int.MaxValue => int.MaxValue,
                < int.MinValue => int.MinValue,
                _ => (int)step
            };
        }
    }

    public sealed class CPicoMotorService
    {
        private readonly object _syncRoot = new();
        private readonly Dictionary<int, CPicoMotorCommandSession> _sessions = [];
        private readonly Dictionary<int, ST_PICO_MOTOR_STATUS> _statuses = [];
        private readonly Dictionary<int, CancellationTokenSource> _allMoveTokens = [];
        private readonly Dictionary<int, CancellationTokenSource> _motionTokens = [];
        private const int SimulationIntervalMs = 100;
        public ST_PICO_MOTOR_STATUS GetStatus(int number)
        {
            lock (_syncRoot)
            {
                return _statuses.TryGetValue(number, out var status) ? status : ST_PICO_MOTOR_STATUS.Empty;
            }
        }

        public async Task<ST_PICO_MOTOR_STATUS> Refresh(int number, bool simulation, CancellationToken cancellationToken = default)
        {
            if (simulation)
            {
                return GetStatus(number)with
                {
                    CommOk = true,
                    UpdatedAt = DateTimeOffset.Now
                };
            }

            global::Drilling.Common.Interface.ST_PICO_MOTOR_STATUS SelectStatus()
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = GetSession(number, requireConnected: true);
                var before = GetStatus(number);
                var motorNo = Math.Clamp(before.SelectedMotorNo, 1, 4);
                var identification = session.GetIdentification();
                var errorCode = session.GetErrorCode();
                var status = before with
                {
                    IsConnected = true,
                    Controller = string.IsNullOrWhiteSpace(identification) ? "8742" : identification,
                    Motor1Position = CPicoMotor.StepToMillimeter(session.GetPositionStep(1)),
                    Motor2Position = CPicoMotor.StepToMillimeter(session.GetPositionStep(2)),
                    Motor3Position = CPicoMotor.StepToMillimeter(session.GetPositionStep(3)),
                    Motor4Position = CPicoMotor.StepToMillimeter(session.GetPositionStep(4)),
                    HomePosition = session.GetHomePositionStep(motorNo),
                    CurrentVelocity = CPicoMotor.StepToMillimeter(session.GetVelocityStep(motorNo)),
                    CurrentAcceleration = CPicoMotor.StepToMillimeter(session.GetAccelerationStep(motorNo)),
                    MotionState = session.GetMotionDone(motorNo) ? "IDLE" : "MOVING",
                    ErrorCode = errorCode,
                    CommOk = true,
                    LastError = CPicoMotor.ToError(errorCode),
                    UpdatedAt = DateTimeOffset.Now
                };
                SetStatus(number, status);
                return status;
            }

            return await Task.Run(SelectStatus, cancellationToken);
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> Execute(int number, bool simulation, EN_PICO_MOTOR_COMMAND command, int motorNo, double parameter, CancellationToken cancellationToken = default)
        {
            motorNo = Math.Clamp(motorNo, 1, 4);
            void ExecuteLiveCallback()
            {
                ExecuteLive(number, command, motorNo, parameter);
            }

            try
            {
                if (command is EN_PICO_MOTOR_COMMAND.SetVelocity or EN_PICO_MOTOR_COMMAND.SetAcceleration)
                {
                    var stepValue = CPicoMotor.MillimeterToStep(parameter);
                    if (parameter < 0.0 || stepValue > int.MaxValue)
                    {
                        return new ST_DEVICE_COMMAND_RESULT(false, $"PICO_MOTOR {command} value is out of range.");
                    }
                }

                if (command == EN_PICO_MOTOR_COMMAND.AllMotorStop)
                {
                    CancelAllMove(number);
                    CancelMotion(number);
                }
                else if (command == EN_PICO_MOTOR_COMMAND.StopMotion)
                {
                    CancelMotion(number);
                }

                if (simulation)
                {
                    if (IsMoveCommand(command) && !ValidateSimulationMove(number, out var validationError))
                    {
                        return validationError;
                    }

                    if (command is EN_PICO_MOTOR_COMMAND.Home or EN_PICO_MOTOR_COMMAND.MoveRelativeNegative or EN_PICO_MOTOR_COMMAND.MoveRelativePositive or EN_PICO_MOTOR_COMMAND.MoveAbsolute)
                    {
                        await ExecuteSimulationPositionMove(number, command, motorNo, parameter, cancellationToken);
                    }
                    else
                    {
                        ExecuteSimulation(number, command, motorNo, parameter);
                    }

                    return new ST_DEVICE_COMMAND_RESULT(true, $"SIM:PICO_MOTOR:{command}:MOTOR={motorNo}:VALUE={parameter:0.###}");
                }

                await Task.Run(ExecuteLiveCallback, cancellationToken);
                return new ST_DEVICE_COMMAND_RESULT(true, $"PICO_MOTOR {command} OK. MOTOR={motorNo}");
            }
            catch (OperationCanceledException)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "PICO_MOTOR command canceled.");
            }
            catch (Exception ex)
            {
                SetStatus(number, GetStatus(number)with { CommOk = false, LastError = EN_PICO_MOTOR_ERROR.Error, UpdatedAt = DateTimeOffset.Now });
                return new ST_DEVICE_COMMAND_RESULT(false, $"PICO_MOTOR {command} failed: {ex.Message}");
            }
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecuteAllMove(int number, bool simulation, IReadOnlyCollection<int> motorNos, double positionMm, int count, CancellationToken cancellationToken = default)
        {
            bool ShouldIncludeItem(int value)
            {
                return value is >= 1 and <= 4;
            }

            int SelectValue(int value)
            {
                return value;
            }

            var motors = motorNos.Where(ShouldIncludeItem).Distinct().OrderBy(SelectValue).ToArray();
            if (motors.Length == 0)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "PICO_MOTOR all move requires at least one selected motor.");
            }

            if (count <= 0)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "PICO_MOTOR all move count must be greater than zero.");
            }

            var before = GetStatus(number);
            if (!before.IsConnected)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "PICO_MOTOR is disconnected. Connect first.");
            }

            if (simulation && !ValidateSimulationMove(number, out var validationError))
            {
                return validationError;
            }

            CancelAllMove(number);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_syncRoot)
            {
                _allMoveTokens[number] = linked;
            }

            var selectedMotorNo = Math.Clamp(before.SelectedMotorNo, 1, 4);
            SetStatus(number, before with { SelectedMotorNo = selectedMotorNo, MotionState = "ALL MOVE", AllMoveCurrentCount = 0, AllMoveSetCount = count, AllMovePosition = positionMm, CommOk = true, LastError = EN_PICO_MOTOR_ERROR.Ok, UpdatedAt = DateTimeOffset.Now });
            try
            {
                for (var current = 1; current <= count; current++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (simulation)
                    {
                        await SimulateAllMotorMoveByDelta(number, motors, positionMm, linked.Token);
                    }
                    else
                    {
                        var session = GetSession(number, requireConnected: true);
                        var moveStep = CPicoMotor.MillimeterToStep(positionMm);
                        void GeneratedMoveCallback()
                        {
                            foreach (var motorNo in motors)
                            {
                                linked.Token.ThrowIfCancellationRequested();
                                session.RelativeMove(motorNo, moveStep);
                            }
                        }

                        await Task.Run(GeneratedMoveCallback, linked.Token);
                        await WaitAllMotorsDone(session, motors, linked.Token);
                        await Refresh(number, false, linked.Token);
                    }

                    SetStatus(number, GetStatus(number)with { SelectedMotorNo = selectedMotorNo, MotionState = current == count ? "IDLE" : "ALL MOVE", AllMoveCurrentCount = current, AllMoveSetCount = count, AllMovePosition = positionMm, UpdatedAt = DateTimeOffset.Now });
                }

                return new ST_DEVICE_COMMAND_RESULT(true, "PICO_MOTOR all move completed.");
            }
            catch (OperationCanceledException)
            {
                SetStatus(number, GetStatus(number)with { SelectedMotorNo = selectedMotorNo, MotionState = "IDLE", UpdatedAt = DateTimeOffset.Now });
                return new ST_DEVICE_COMMAND_RESULT(true, "PICO_MOTOR all move stopped.");
            }
            catch (TimeoutException ex)
            {
                SetStatus(number, GetStatus(number)with { SelectedMotorNo = selectedMotorNo, MotionState = "IDLE", CommOk = false, LastError = EN_PICO_MOTOR_ERROR.Timeout, UpdatedAt = DateTimeOffset.Now });
                return new ST_DEVICE_COMMAND_RESULT(false, ex.Message);
            }
            finally
            {
                lock (_syncRoot)
                {
                    if (_allMoveTokens.TryGetValue(number, out var token) && ReferenceEquals(token, linked))
                    {
                        _allMoveTokens.Remove(number);
                        token.Dispose();
                    }
                }
            }
        }

        public void CancelAllMove(int number)
        {
            lock (_syncRoot)
            {
                if (_allMoveTokens.TryGetValue(number, out var token))
                    token.Cancel();
            }
        }

        private void CancelMotion(int number)
        {
            lock (_syncRoot)
            {
                if (_motionTokens.TryGetValue(number, out var token))
                {
                    token.Cancel();
                }
            }
        }

        private static bool IsMoveCommand(EN_PICO_MOTOR_COMMAND command)
        {
            return command is EN_PICO_MOTOR_COMMAND.Home or EN_PICO_MOTOR_COMMAND.JogNegative or EN_PICO_MOTOR_COMMAND.JogPositive or EN_PICO_MOTOR_COMMAND.MoveRelativeNegative or EN_PICO_MOTOR_COMMAND.MoveRelativePositive or EN_PICO_MOTOR_COMMAND.MoveAbsolute;
        }

        private bool ValidateSimulationMove(int number, out ST_DEVICE_COMMAND_RESULT error)
        {
            var status = GetStatus(number);
            if (!status.IsConnected)
            {
                error = new ST_DEVICE_COMMAND_RESULT(false, "PICO_MOTOR is disconnected. Connect first.");
                return false;
            }

            if (status.CurrentVelocity <= 0.0)
            {
                error = new ST_DEVICE_COMMAND_RESULT(false, "PICO_MOTOR velocity must be greater than 0 before move.");
                return false;
            }

            if (status.CurrentAcceleration <= 0.0)
            {
                error = new ST_DEVICE_COMMAND_RESULT(false, "PICO_MOTOR acceleration must be greater than 0 before move.");
                return false;
            }

            error = new ST_DEVICE_COMMAND_RESULT(true, string.Empty);
            return true;
        }

        private async Task SimulateAllMotorMoveByDelta(int number, IReadOnlyList<int> motorNos, double deltaPosition, CancellationToken cancellationToken)
        {
            var start = GetStatus(number);
            int SelectMotorNo(int motorNo)
            {
                return motorNo;
            }

            double SelectValue(int motorNo)
            {
                return GetPosition(start, motorNo) + deltaPosition;
            }

            var targets = motorNos.ToDictionary(SelectMotorNo, SelectValue);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = GetStatus(number);
                var next = status;
                var completed = true;
                var stepDistance = GetSimulationStepDistance(status);
                foreach (var motorNo in motorNos)
                {
                    var current = GetPosition(next, motorNo);
                    var distance = targets[motorNo] - current;
                    if (Math.Abs(distance) <= 0.000001)
                    {
                        next = SetPosition(next, motorNo, targets[motorNo]);
                        continue;
                    }

                    completed = false;
                    var position = Math.Abs(distance) <= stepDistance ? targets[motorNo] : current + (Math.Sign(distance) * stepDistance);
                    next = SetPosition(next, motorNo, position);
                }

                SetStatus(number, next with { MotionState = completed ? "IDLE" : "ALL MOVE", UpdatedAt = DateTimeOffset.Now });
                if (completed)
                {
                    return;
                }

                await Task.Delay(SimulationIntervalMs, cancellationToken);
            }
        }

        private static async Task WaitAllMotorsDone(CPicoMotorCommandSession session, IReadOnlyList<int> motorNos, CancellationToken cancellationToken)
        {
            var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(120);
            bool Matches()
            {
                return motorNos.All(session.GetMotionDone);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completed = await Task.Run(Matches, cancellationToken);
                if (completed)
                {
                    return;
                }

                if (DateTimeOffset.UtcNow >= timeoutAt)
                {
                    throw new TimeoutException("PICO_MOTOR all move completion timeout.");
                }

                await Task.Delay(50, cancellationToken);
            }
        }

        private static double GetSimulationStepDistance(ST_PICO_MOTOR_STATUS status)
        {
            return Math.Max(0.000001, status.CurrentVelocity * SimulationIntervalMs / 1000.0);
        }

        public void Disconnect(int number)
        {
            lock (_syncRoot)
            {
                if (_allMoveTokens.Remove(number, out var allMove))
                    allMove.Cancel();
                if (_motionTokens.Remove(number, out var motion))
                    motion.Cancel();
                if (_sessions.Remove(number, out var session))
                    session.Dispose();
                _statuses.Remove(number);
            }
        }

        public void DisconnectAll()
        {
            lock (_syncRoot)
            {
                foreach (var token in _allMoveTokens.Values)
                    token.Cancel();
                foreach (var token in _motionTokens.Values)
                    token.Cancel();
                foreach (var session in _sessions.Values)
                    session.Dispose();
                _allMoveTokens.Clear();
                _motionTokens.Clear();
                _sessions.Clear();
                _statuses.Clear();
            }
        }

        public void Dispose()
        {
            DisconnectAll();
        }

        private void ExecuteLive(int number, EN_PICO_MOTOR_COMMAND command, int motorNo, double parameter)
        {
            if (command == EN_PICO_MOTOR_COMMAND.Connect)
            {
                var session = GetSession(number, requireConnected: false);
                if (!session.Connect())
                    throw new InvalidOperationException("CmdLib device discovery/open failed.");
                SetStatus(number, GetStatus(number)with { IsConnected = true, CommOk = true, UpdatedAt = DateTimeOffset.Now });
                return;
            }

            if (command == EN_PICO_MOTOR_COMMAND.Disconnect)
            {
                if (_sessions.TryGetValue(number, out var session))
                    session.Disconnect();
                SetStatus(number, GetStatus(number)with { IsConnected = false, MotionState = "IDLE", UpdatedAt = DateTimeOffset.Now });
                return;
            }

            var live = GetSession(number, requireConnected: true);
            switch (command)
            {
                case EN_PICO_MOTOR_COMMAND.SelectMotor:
                    break;
                case EN_PICO_MOTOR_COMMAND.SetVelocity:
                    live.SetVelocity(motorNo, checked((int)CPicoMotor.MillimeterToStep(parameter)));
                    break;
                case EN_PICO_MOTOR_COMMAND.SetAcceleration:
                    live.SetAcceleration(motorNo, checked((int)CPicoMotor.MillimeterToStep(parameter)));
                    break;
                case EN_PICO_MOTOR_COMMAND.StopMotion:
                    live.StopMotion(motorNo);
                    break;
                case EN_PICO_MOTOR_COMMAND.AllMotorStop:
                    live.AbortMotion();
                    CancelAllMove(number);
                    break;
                case EN_PICO_MOTOR_COMMAND.Home:
                    live.MoveHome(motorNo);
                    break;
                case EN_PICO_MOTOR_COMMAND.JogNegative:
                    live.JogNegative(motorNo);
                    break;
                case EN_PICO_MOTOR_COMMAND.JogPositive:
                    live.JogPositive(motorNo);
                    break;
                case EN_PICO_MOTOR_COMMAND.MoveRelativeNegative:
                    live.RelativeMove(motorNo, -CPicoMotor.MillimeterToStep(parameter));
                    break;
                case EN_PICO_MOTOR_COMMAND.MoveRelativePositive:
                    live.RelativeMove(motorNo, CPicoMotor.MillimeterToStep(parameter));
                    break;
                case EN_PICO_MOTOR_COMMAND.MoveAbsolute:
                    live.AbsoluteMove(motorNo, CPicoMotor.MillimeterToStep(parameter));
                    break;
                case EN_PICO_MOTOR_COMMAND.Refresh:
                    Refresh(number, false).GetAwaiter().GetResult();
                    return;
            }

            var status = GetStatus(number);
            SetStatus(number, status with { SelectedMotorNo = motorNo, CurrentVelocity = command == EN_PICO_MOTOR_COMMAND.SetVelocity ? parameter : status.CurrentVelocity, CurrentAcceleration = command == EN_PICO_MOTOR_COMMAND.SetAcceleration ? parameter : status.CurrentAcceleration, MotionState = command switch
            {
                EN_PICO_MOTOR_COMMAND.StopMotion or EN_PICO_MOTOR_COMMAND.AllMotorStop => "IDLE",
                EN_PICO_MOTOR_COMMAND.Home => "HOME",
                EN_PICO_MOTOR_COMMAND.JogNegative or EN_PICO_MOTOR_COMMAND.JogPositive => "JOG",
                EN_PICO_MOTOR_COMMAND.MoveRelativeNegative or EN_PICO_MOTOR_COMMAND.MoveRelativePositive => "REL MOVE",
                EN_PICO_MOTOR_COMMAND.MoveAbsolute => "ABS MOVE",
                _ => status.MotionState
            }, CommOk = true, LastError = EN_PICO_MOTOR_ERROR.Ok, UpdatedAt = DateTimeOffset.Now });
        }

        private void ExecuteSimulation(int number, EN_PICO_MOTOR_COMMAND command, int motorNo, double parameter)
        {
            var status = GetStatus(number);
            var position = GetPosition(status, motorNo);
            status = command switch
            {
                EN_PICO_MOTOR_COMMAND.Connect => status with
                {
                    IsConnected = true
                },
                EN_PICO_MOTOR_COMMAND.Disconnect => status with
                {
                    IsConnected = false
                },
                EN_PICO_MOTOR_COMMAND.SetVelocity => status with
                {
                    CurrentVelocity = parameter
                },
                EN_PICO_MOTOR_COMMAND.SetAcceleration => status with
                {
                    CurrentAcceleration = parameter
                },
                EN_PICO_MOTOR_COMMAND.Home => SetPosition(status, motorNo, CPicoMotor.StepToMillimeter(status.HomePosition)),
                EN_PICO_MOTOR_COMMAND.MoveRelativeNegative => SetPosition(status, motorNo, position - parameter),
                EN_PICO_MOTOR_COMMAND.MoveRelativePositive => SetPosition(status, motorNo, position + parameter),
                EN_PICO_MOTOR_COMMAND.MoveAbsolute => SetPosition(status, motorNo, parameter),
                EN_PICO_MOTOR_COMMAND.JogNegative => SetPosition(status, motorNo, position - GetSimulationStepDistance(status)),
                EN_PICO_MOTOR_COMMAND.JogPositive => SetPosition(status, motorNo, position + GetSimulationStepDistance(status)),
                _ => status
            };
            var motionState = command switch
            {
                EN_PICO_MOTOR_COMMAND.JogNegative or EN_PICO_MOTOR_COMMAND.JogPositive => "JOG",
                EN_PICO_MOTOR_COMMAND.StopMotion or EN_PICO_MOTOR_COMMAND.AllMotorStop or EN_PICO_MOTOR_COMMAND.Disconnect => "IDLE",
                _ => status.MotionState
            };
            SetStatus(number, status with { SelectedMotorNo = motorNo, MotionState = motionState, CommOk = true, LastError = EN_PICO_MOTOR_ERROR.Ok, UpdatedAt = DateTimeOffset.Now });
        }

        private async Task ExecuteSimulationPositionMove(int number, EN_PICO_MOTOR_COMMAND command, int motorNo, double parameter, CancellationToken cancellationToken)
        {
            CancelMotion(number);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_syncRoot)
            {
                _motionTokens[number] = linked;
            }

            try
            {
                var status = GetStatus(number);
                var start = GetPosition(status, motorNo);
                var target = command switch
                {
                    EN_PICO_MOTOR_COMMAND.Home => CPicoMotor.StepToMillimeter(status.HomePosition),
                    EN_PICO_MOTOR_COMMAND.MoveRelativeNegative => start - Math.Abs(parameter),
                    EN_PICO_MOTOR_COMMAND.MoveRelativePositive => start + Math.Abs(parameter),
                    _ => parameter
                };
                var motionState = command switch
                {
                    EN_PICO_MOTOR_COMMAND.Home => "HOME",
                    EN_PICO_MOTOR_COMMAND.MoveAbsolute => "ABS MOVE",
                    _ => "REL MOVE"
                };
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    status = GetStatus(number);
                    var current = GetPosition(status, motorNo);
                    var distance = target - current;
                    if (Math.Abs(distance) <= 0.000001)
                    {
                        SetStatus(number, SetPosition(status, motorNo, target)with { SelectedMotorNo = motorNo, MotionState = "IDLE", UpdatedAt = DateTimeOffset.Now });
                        return;
                    }

                    var stepDistance = GetSimulationStepDistance(status);
                    var position = Math.Abs(distance) <= stepDistance ? target : current + (Math.Sign(distance) * stepDistance);
                    SetStatus(number, SetPosition(status, motorNo, position)with { SelectedMotorNo = motorNo, MotionState = motionState, CommOk = true, UpdatedAt = DateTimeOffset.Now });
                    await Task.Delay(SimulationIntervalMs, linked.Token);
                }
            }
            catch (OperationCanceledException)when (!cancellationToken.IsCancellationRequested)
            {
                SetStatus(number, GetStatus(number)with { MotionState = "IDLE", UpdatedAt = DateTimeOffset.Now });
            }
            finally
            {
                lock (_syncRoot)
                {
                    if (_motionTokens.TryGetValue(number, out var token) && ReferenceEquals(token, linked))
                    {
                        _motionTokens.Remove(number);
                        token.Dispose();
                    }
                }
            }
        }

        private CPicoMotorCommandSession GetSession(int number, bool requireConnected)
        {
            lock (_syncRoot)
            {
                if (!_sessions.TryGetValue(number, out var session))
                {
                    session = new CPicoMotorCommandSession();
                    _sessions[number] = session;
                }

                if (requireConnected && !session.IsConnected)
                    throw new InvalidOperationException("PicoMotor is not connected.");
                return session;
            }
        }

        private void SetStatus(int number, ST_PICO_MOTOR_STATUS status)
        {
            lock (_syncRoot)
                _statuses[number] = status;
        }

        private static double GetPosition(ST_PICO_MOTOR_STATUS status, int motorNo)
        {
            return motorNo switch
            {
                1 => status.Motor1Position,
                2 => status.Motor2Position,
                3 => status.Motor3Position,
                _ => status.Motor4Position
            };
        }

        private static ST_PICO_MOTOR_STATUS SetPosition(ST_PICO_MOTOR_STATUS status, int motorNo, double value)
        {
            return motorNo switch
            {
                1 => status with
                {
                    Motor1Position = value
                },
                2 => status with
                {
                    Motor2Position = value
                },
                3 => status with
                {
                    Motor3Position = value
                },
                _ => status with
                {
                    Motor4Position = value
                }
            };
        }
    }
}
