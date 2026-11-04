namespace Drilling.Common.Motion
{
    using System.Diagnostics;
    using System.Globalization;
    using Drilling.Common.Managers;
    using Drilling.Common.Interface;
    using Drilling.Common.Motion;
    using Drilling.Common.Alarm;
    using Drilling.Common.InterLock;
    using Drilling.Common.Station;
    using System.Reflection;

    public sealed class CMotionManager
    {
        private const string DefaultControllerName = "XPS";
        private static readonly IReadOnlyDictionary<string, Type> MotionControllerTypes = LoadMotionControllerTypes();
        private readonly CInterfaceManager? _interfaceManager;
        private readonly IReadOnlyList<ST_MOTOR_DATA> _motors;
        private readonly Dictionary<string, ST_MOTOR_DATA> _axisData;
        private readonly Dictionary<string, ST_AXIS_STATE> _axes;
        private readonly Dictionary<string, ST_IO_STATE> _io;
        private readonly Dictionary<string, CMotionController> _controllers = new(StringComparer.OrdinalIgnoreCase);
        private bool _simulationMode;
        public CMotionManager(bool isSimulation = true) : this(null, null, null, isSimulation)
        {
        }

        public CMotionManager(CInterfaceManager? interfaceManager, IReadOnlyList<ST_MOTOR_DATA>? motors = null, IReadOnlyList<ST_IO_DATA>? ioData = null, bool isSimulation = true)
        {
            _interfaceManager = interfaceManager;
            _simulationMode = isSimulation;
            _motors = NormalizeMotors(motors);
            bool IsUse(global::Drilling.Common.Motion.ST_MOTOR_DATA axis)
            {
                return axis.Use;
            }

            string SelectDictionaryKey(global::Drilling.Common.Motion.ST_MOTOR_DATA axis)
            {
                return NormalizeAxisId(axis.Name);
            }

            _axisData = _motors.Where(IsUse).ToDictionary(SelectDictionaryKey, StringComparer.OrdinalIgnoreCase);
            _axes = CreateAxes(_motors);
            _io = CreateIo(ioData);
            (string DevType, int DevNo) SelectAxisControllerRequest(global::Drilling.Common.Motion.ST_MOTOR_DATA axis)
            {
                return (axis.DevType, axis.DevNo);
            }

            (string DevType, int DevNo) SelectIoControllerRequest(global::Drilling.Common.Motion.CMotionManager.ST_IO_STATE channel)
            {
                return (channel.DevType, channel.DevNo);
            }

            string SelectGroupKey((string DevType, int DevNo) item)
            {
                return GetControllerKey(item.DevType, item.DevNo);
            }

            var controllerRequests = _axisData.Values.Select(SelectAxisControllerRequest).Concat(_io.Values.Select(SelectIoControllerRequest)).GroupBy(SelectGroupKey);
            foreach (var group in controllerRequests)
            {
                var controllerData = group.First();
                var controller = CreateMotionController(controllerData.DevType, controllerData.DevNo);
                if (controller is not null)
                {
                    _controllers[group.Key] = controller;
                }
            }
        }

        public bool IsSimulation
        {
            get
            {
                bool MatchesItem(global::Drilling.Common.Motion.CMotionController controller)
                {
                    return controller.IsSimulation();
                }

                return _simulationMode || _controllers.Values.All(MatchesItem);
            }
        }

        public void SetSimulationMode(bool enabled)
        {
            _simulationMode = enabled;
            var timestamp = Stopwatch.GetTimestamp();
            foreach (var axis in _axes.Values)
            {
                axis.SimulationLastTimestamp = timestamp;
            }
        }

        public async Task Initialize(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string SelectGroupKey(global::Drilling.Common.Motion.ST_MOTOR_DATA axis)
            {
                return GetControllerKey(axis.DevType, axis.DevNo);
            }

            foreach (var group in _axisData.Values.GroupBy(SelectGroupKey))
            {
                if (_simulationMode)
                {
                    continue;
                }

                var axis = group.First();
                if (!_controllers.TryGetValue(group.Key, out var controller))
                {
                    throw CreateMotionControllerNotRegisteredException(axis.DevType, axis.DevNo);
                }

                if (controller.IsSimulation())
                {
                    continue;
                }

                await controller.Initialize(group.ToArray(), cancellationToken);
            }
        }

        public async Task Destroy(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var controller in _controllers.Values)
            {
                await controller.Destroy(cancellationToken);
            }
        }

        public void RefreshStatus(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_simulationMode)
            {
                return;
            }

            RefreshAxisStatus(cancellationToken);
        }

        public IReadOnlyList<ST_MOTOR_AXIS_STATUS> GetAxisStatus(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshStatus(cancellationToken);
            return GetCachedAxisStatus();
        }

        public async Task<IReadOnlyList<ST_MOTOR_AXIS_STATUS>> GetAxisStatusAsync(CancellationToken cancellationToken = default)
        {
            foreach (var axisData in _axisData.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_simulationMode || !_controllers.TryGetValue(GetControllerKey(axisData.DevType, axisData.DevNo), out var controller) || controller.IsSimulation())
                    continue;
                try
                {
                    var status = await controller.ReadAxisStatusAsync(axisData, cancellationToken).ConfigureAwait(false);
                    if (status is not null)
                        ApplyAxisStatus(status);
                }
                catch (Exception exception)when (exception is InvalidOperationException or TimeoutException or IOException)
                {
                    MarkAxisAlarm(axisData.Name);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return GetCachedAxisStatus();
        }

        public IReadOnlyList<ST_MOTOR_AXIS_STATUS> GetCachedAxisStatus()
        {
            UpdateSimulatedAxisPositions();
            int SelectDisplayOrder(global::Drilling.Common.Motion.CMotionManager.ST_AXIS_STATE axis)
            {
                return axis.DisplayOrder;
            }

            global::Drilling.Common.Motion.ST_MOTOR_AXIS_STATUS BuildMotor(global::Drilling.Common.Motion.CMotionManager.ST_AXIS_STATE axis)
            {
                return new ST_MOTOR_AXIS_STATUS(
                    axis.AxisId, 
                    axis.Name, 
                    axis.CurrentPosition, 
                    axis.TargetPosition, 
                    axis.CommandPosition, 
                    axis.ServoOn, 
                    axis.HomeCompleted, 
                    axis.LimitPlusOn, 
                    axis.LimitMinusOn, 
                    axis.AlarmOn, 
                    axis.FeedbackVelocity, 
                    axis.FeedbackAcceleration, 
                    axis.DisplayOrder
                );
            }

            var axes = _axes.Values.OrderBy(SelectDisplayOrder).Select(BuildMotor).ToArray();
            return axes;
        }

        public IReadOnlyList<ST_IO_STATUS> GetIoStatus(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int SelectDisplayOrder(global::Drilling.Common.Motion.CMotionManager.ST_IO_STATE channel)
            {
                return channel.DisplayOrder;
            }

            global::Drilling.Common.Motion.ST_IO_STATUS BuildIo(global::Drilling.Common.Motion.CMotionManager.ST_IO_STATE channel)
            {
                return new ST_IO_STATUS(channel.Id, channel.Address, channel.Name, channel.IsOn, channel.IsOutput);
            }

            var io = _io.Values.OrderBy(SelectDisplayOrder).Select(BuildIo).ToArray();
            return io;
        }

        public IReadOnlyList<ST_MOTION_STATION_STATUS> GetStationStatus(CancellationToken cancellationToken = default)
        {
            var axes = GetAxisStatus(cancellationToken);
            string SelectAxisId(global::Drilling.Common.Motion.ST_MOTOR_AXIS_STATUS axis)
            {
                return axis.AxisId;
            }

            var statusMap = axes.ToDictionary(SelectAxisId, StringComparer.OrdinalIgnoreCase);
            string SelectValue(global::Drilling.Common.Motion.ST_MOTOR_DATA axis)
            {
                return NormalizeStationName(axis.StationName);
            }

            string SelectKey(global::System.Linq.IGrouping<string, global::Drilling.Common.Motion.ST_MOTOR_DATA> group)
            {
                return group.Key;
            }

            global::Drilling.Common.Motion.ST_MOTOR_AXIS_STATUS? SelectMotor(global::Drilling.Common.Motion.ST_MOTOR_DATA axis)
            {
                return statusMap.TryGetValue(NormalizeAxisId(axis.Name), out var status) ? status : null;
            }

            bool MatchesMotor(global::Drilling.Common.Motion.ST_MOTOR_AXIS_STATUS? status)
            {
                return status is not null;
            }

            string SelectAxisId2(global::Drilling.Common.Motion.ST_MOTOR_AXIS_STATUS status)
            {
                return status.AxisId;
            }

            string SelectSystem(global::Drilling.Common.Motion.ST_MOTOR_DATA axis)
            {
                return axis.System;
            }

            bool MatchesItem(string system)
            {
                return !string.IsNullOrWhiteSpace(system);
            }

            bool IsAlarmOn(global::Drilling.Common.Motion.ST_MOTOR_AXIS_STATUS axis)
            {
                return axis.AlarmOn;
            }

            global::Drilling.Common.Motion.ST_MOTION_STATION_STATUS BuildMOTION_STATION_STATUS(global::System.Linq.IGrouping<string, global::Drilling.Common.Motion.ST_MOTOR_DATA> group)
            {
                var stationAxes = group.Select(SelectMotor).Where(MatchesMotor).Cast<ST_MOTOR_AXIS_STATUS>().OrderBy(SelectAxisId2, StringComparer.OrdinalIgnoreCase).ToArray();
                return new ST_MOTION_STATION_STATUS(group.Key, group.Select(SelectSystem).FirstOrDefault(MatchesItem) ?? "", stationAxes.Any(IsAlarmOn), stationAxes);
            }

            return _axisData.Values.GroupBy(SelectValue).OrderBy(SelectKey, StringComparer.OrdinalIgnoreCase).Select(BuildMOTION_STATION_STATUS).ToArray();
        }

        public Task<IReadOnlyList<ST_MOTION_CONTROLLER_STATUS>> GetControllerStatus(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string SelectValue(global::Drilling.Common.Motion.ST_MOTOR_DATA axis)
            {
                return GetControllerKey(axis.DevType, axis.DevNo);
            }

            string SelectItem(global::Drilling.Common.Motion.ST_MOTOR_DATA axis)
            {
                return NormalizeAxisId(axis.Name);
            }

            string SelectAxis(string axis)
            {
                return axis;
            }

            global::Drilling.Common.Motion.ST_MOTION_CONTROLLER_STATUS BuildMOTION_CONTROLLER_STATUS(global::System.Linq.IGrouping<string, global::Drilling.Common.Motion.ST_MOTOR_DATA> group)
            {
                var first = group.First();
                var registered = _controllers.TryGetValue(group.Key, out var controller);
                var axes = group.Select(SelectItem).OrderBy(SelectAxis, StringComparer.OrdinalIgnoreCase).ToArray();
                return new ST_MOTION_CONTROLLER_STATUS(NormalizeControllerName(first.DevType), first.DevNo, registered, controller?.IsSimulation() ?? true, axes.Length, axes);
            }

            string SelectDevType(global::Drilling.Common.Motion.ST_MOTION_CONTROLLER_STATUS item)
            {
                return item.DevType;
            }

            int SelectDevNo(global::Drilling.Common.Motion.ST_MOTION_CONTROLLER_STATUS item)
            {
                return item.DevNo;
            }

            var status = _axisData.Values.GroupBy(SelectValue, StringComparer.OrdinalIgnoreCase).Select(BuildMOTION_CONTROLLER_STATUS).OrderBy(SelectDevType, StringComparer.OrdinalIgnoreCase).ThenBy(SelectDevNo).ToArray();
            return Task.FromResult<IReadOnlyList<ST_MOTION_CONTROLLER_STATUS>>(status);
        }

        public async Task MoveAxis(string axisId, double targetPosition, CancellationToken cancellationToken = default)
        {
            await Move(axisId, targetPosition, cancellationToken);
        }

        public Task ServoOn(string axisId, CancellationToken cancellationToken = default)
        {
            return ExecuteAxisCommand(axisId, EN_MOTION_COMMAND.ServoOn, cancellationToken: cancellationToken);
        }

        public Task ServoOff(string axisId, CancellationToken cancellationToken = default)
        {
            return ExecuteAxisCommand(axisId, EN_MOTION_COMMAND.ServoOff, cancellationToken: cancellationToken);
        }

        public Task Home(string axisId, CancellationToken cancellationToken = default)
        {
            return ExecuteAxisCommand(axisId, EN_MOTION_COMMAND.Home, cancellationToken: cancellationToken);
        }

        public Task Move(string axisId, double targetPosition, CancellationToken cancellationToken = default)
        {
            return ExecuteAxisCommand(axisId, EN_MOTION_COMMAND.MoveAbs, targetPosition, cancellationToken);
        }

        public Task MoveRel(string axisId, double distance, CancellationToken cancellationToken = default)
        {
            return ExecuteAxisCommand(axisId, EN_MOTION_COMMAND.MoveRel, distance, cancellationToken);
        }

        public Task Stop(string axisId, CancellationToken cancellationToken = default)
        {
            return ExecuteAxisCommand(axisId, EN_MOTION_COMMAND.Stop, cancellationToken: cancellationToken);
        }

        public Task ResetAlarm(string axisId, CancellationToken cancellationToken = default)
        {
            return ExecuteAxisCommand(axisId, EN_MOTION_COMMAND.ResetAlarm, cancellationToken: cancellationToken);
        }

        public async Task ExecuteAxisCommand(string axisId, EN_MOTION_COMMAND command, double parameter = 0.0, CancellationToken cancellationToken = default, double? parameter2 = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedAxisId = NormalizeAxisId(axisId);
            if (!_axisData.TryGetValue(normalizedAxisId, out var axisData))
            {
                throw new InvalidOperationException($"Motion axis was not registered in JHMI_MOTOR.csv: {axisId}");
            }

            if (!_axes.TryGetValue(normalizedAxisId, out var axisState))
            {
                throw new InvalidOperationException($"Motion axis state was not registered: {axisId}");
            }

            var isSimulation = IsAxisSimulation(axisData);
            if (isSimulation)
            {
                UpdateSimulatedAxisPosition(axisData, axisState, Stopwatch.GetTimestamp());
            }

            ValidateAxisCommand(axisData, axisState, command, parameter, parameter2);
            if (!_simulationMode)
            {
                if (!_controllers.TryGetValue(GetControllerKey(axisData.DevType, axisData.DevNo), out var controller))
                {
                    throw CreateMotionControllerNotRegisteredException(axisData.DevType, axisData.DevNo);
                }

                if (!controller.IsSimulation())
                {
                    await controller.ExecuteAxisCommand(axisData, command, parameter, cancellationToken, parameter2);
                }
            }

            ApplyAxisCommand(normalizedAxisId, command, parameter, parameter2 ?? 0.0, isSimulation);
        }

        public async Task StopMotion(string axisId, CancellationToken cancellationToken = default)
        {
            await Stop(axisId, cancellationToken);
        }

        public async Task SetOutput(string ioName, bool isOn, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channel = GetIoChannelOrThrow(ioName);
            if (!channel.IsOutput)
            {
                throw new InvalidOperationException($"Motion IO is input only: {FormatIoReference(channel)}");
            }

            var controller = GetMotionController(channel.DevType, channel.DevNo);
            if (!_simulationMode && controller is null)
            {
                throw CreateMotionControllerNotRegisteredException(channel.DevType, channel.DevNo);
            }

            if (!_simulationMode && controller is not null && !controller.IsSimulation())
            {
                await controller.SetOutput(channel.Address, isOn, cancellationToken);
            }

            channel.IsOn = isOn;
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecuteMotionCommand(string axisId, EN_MOTION_COMMAND command, double parameter = 0.0, CancellationToken cancellationToken = default, double? parameter2 = null)
        {
            try
            {
                await ExecuteAxisCommand(axisId, command, parameter, cancellationToken, parameter2);
                return new ST_DEVICE_COMMAND_RESULT(true, $"Motion {axisId} {FormatMotionCommand(command)} OK.");
            }
            catch (Exception exception)when (exception is InvalidOperationException or TimeoutException or IOException)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"Motion {axisId} {FormatMotionCommand(command)} failed. {exception.Message}");
            }
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecuteAbsoluteMoves(IReadOnlyList<ST_MOTION_ABS_MOVE_REQUEST> requests, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requests is null || requests.Count == 0)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "XPS MULTI MOVE requires at least one axis.");
            }

            var normalizedRequests = new List<ST_MOTION_ABS_MOVE_REQUEST>(requests.Count);
            var uniqueAxisIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var startedAxisIds = new List<string>(requests.Count);
            string SelectAxisId(global::Drilling.Common.Motion.ST_MOTION_ABS_MOVE_REQUEST request)
            {
                return request.AxisId;
            }

            try
            {
                foreach (var request in requests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var axisId = NormalizeAxisId(request.AxisId);
                    if (!uniqueAxisIds.Add(axisId))
                    {
                        throw new InvalidOperationException($"XPS MULTI MOVE contains a duplicate axis: {axisId}");
                    }

                    if (!double.IsFinite(request.TargetPosition) || !double.IsFinite(request.Speed) || !double.IsFinite(request.Acceleration) || request.Speed <= 0.0 || request.Acceleration <= 0.0)
                    {
                        throw new InvalidOperationException($"XPS MULTI MOVE values are invalid. Axis={axisId}, Target={request.TargetPosition}, Speed={request.Speed}, Acc={request.Acceleration}");
                    }

                    if (!_axisData.TryGetValue(axisId, out var axisData) || !_axes.TryGetValue(axisId, out var axisState))
                    {
                        throw new InvalidOperationException($"Motion axis was not registered in JHMI_MOTOR.csv: {request.AxisId}");
                    }

                    if (IsAxisSimulation(axisData))
                    {
                        UpdateSimulatedAxisPosition(axisData, axisState, Stopwatch.GetTimestamp());
                    }

                    ValidateAxisCommand(axisData, axisState, EN_MOTION_COMMAND.MoveAbs, request.TargetPosition, parameter2: null);
                    normalizedRequests.Add(request with { AxisId = axisId });
                }

                // Configure every profile before starting the first axis so profile failure cannot cause a partial move.
                foreach (var request in normalizedRequests)
                {
                    await ExecuteAxisCommand(request.AxisId, EN_MOTION_COMMAND.SetProfile, request.Speed, cancellationToken, request.Acceleration);
                }

                foreach (var request in normalizedRequests)
                {
                    // A lost response does not prove the controller rejected the move.
                    startedAxisIds.Add(request.AxisId);
                    await ExecuteAxisCommand(request.AxisId, EN_MOTION_COMMAND.MoveAbs, request.TargetPosition, cancellationToken);
                }

                return new ST_DEVICE_COMMAND_RESULT(true, $"XPS MULTI ABS MOVE OK. Axes={string.Join(",", normalizedRequests.Select(SelectAxisId))}");
            }
            catch (OperationCanceledException cancellationException)
            {
                var stopFailure = await StopStartedAxes(startedAxisIds);
                if (stopFailure is not null)
                {
                    throw new AggregateException("XPS MULTI ABS MOVE was cancelled and one or more safety stops failed.", cancellationException, stopFailure);
                }

                throw;
            }
            catch (Exception exception)when (exception is InvalidOperationException or TimeoutException or IOException)
            {
                var stopFailure = await StopStartedAxes(startedAxisIds);
                var stopFailureMessage = stopFailure is null ? "" : $" SAFETY STOP FAILED. {FormatAggregateException(stopFailure)}";
                return new ST_DEVICE_COMMAND_RESULT(false, $"XPS MULTI ABS MOVE failed. {exception.Message}{stopFailureMessage}");
            }
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> SetOutputCommand(string ioName, bool isOn, CancellationToken cancellationToken = default)
        {
            var command = isOn ? "ON" : "OFF";
            try
            {
                var channel = GetIoChannelOrThrow(ioName);
                await SetOutput(channel.Address, isOn, cancellationToken);
                return new ST_DEVICE_COMMAND_RESULT(true, $"Motion IO {FormatIoReference(channel)} {command} OK.");
            }
            catch (Exception exception)when (exception is InvalidOperationException or TimeoutException or IOException)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"Motion IO {ioName} {command} failed. {exception.Message}");
            }
        }

        private CMotionController? GetMotionController(string devType, int devNo)
        {
            return _controllers.TryGetValue(GetControllerKey(devType, devNo), out var controller) ? controller : null;
        }

        private bool IsAxisSimulation(ST_MOTOR_DATA axisData)
        {
            return _simulationMode || (_controllers.TryGetValue(GetControllerKey(axisData.DevType, axisData.DevNo), out var controller) && controller.IsSimulation());
        }

        private async Task<AggregateException?> StopStartedAxes(IReadOnlyList<string> axisIds)
        {
            var failures = new List<Exception>();
            foreach (var axisId in axisIds)
            {
                try
                {
                    await ExecuteAxisCommand(axisId, EN_MOTION_COMMAND.Stop, cancellationToken: CancellationToken.None);
                }
                catch (Exception exception)
                {
                    if (_axes.TryGetValue(NormalizeAxisId(axisId), out var axis))
                    {
                        axis.AlarmOn = true;
                    }

                    failures.Add(new InvalidOperationException($"XPS safety stop failed. Axis={axisId}. {exception.Message}", exception));
                }
            }

            return failures.Count == 0 ? null : new AggregateException("One or more XPS safety stops failed.", failures);
        }

        private void RefreshAxisStatus(CancellationToken cancellationToken)
        {
            foreach (var axisData in _axisData.Values)
            {
                if (!_controllers.TryGetValue(GetControllerKey(axisData.DevType, axisData.DevNo), out var controller) || controller.IsSimulation())
                {
                    continue;
                }

                try
                {
                    var status = controller.ReadAxisStatus(axisData, cancellationToken);
                    if (status is not null)
                    {
                        ApplyAxisStatus(status);
                    }
                }
                catch (Exception exception)when (exception is InvalidOperationException or TimeoutException or IOException)
                {
                    MarkAxisAlarm(axisData.Name);
                }
            }
        }

        private void ApplyAxisStatus(ST_MOTOR_AXIS_STATUS status)
        {
            var axisId = NormalizeAxisId(status.AxisId);
            if (!_axes.TryGetValue(axisId, out var axis))
            {
                return;
            }

            axis.CurrentPosition = status.CurrentPosition;
            axis.TargetPosition = status.TargetPosition;
            axis.CommandPosition = status.CommandPosition;
            axis.ServoOn = status.ServoOn;
            axis.HomeCompleted = status.HomeCompleted;
            axis.LimitPlusOn = status.LimitPlusOn;
            axis.LimitMinusOn = status.LimitMinusOn;
            axis.AlarmOn = status.AlarmOn;
            axis.FeedbackVelocity = status.FeedbackVelocity;
            axis.FeedbackAcceleration = status.FeedbackAcceleration;
            axis.SimulationLastTimestamp = Stopwatch.GetTimestamp();
        }

        private void UpdateSimulatedAxisPositions()
        {
            var timestamp = Stopwatch.GetTimestamp();
            foreach (var axisData in _axisData.Values)
            {
                if (!IsAxisSimulation(axisData) || !_axes.TryGetValue(NormalizeAxisId(axisData.Name), out var axisState))
                {
                    continue;
                }

                UpdateSimulatedAxisPosition(axisData, axisState, timestamp);
            }
        }

        private static void UpdateSimulatedAxisPosition(ST_MOTOR_DATA axisData, ST_AXIS_STATE axis, long timestamp)
        {
            if (axis.SimulationLastTimestamp <= 0)
            {
                axis.SimulationLastTimestamp = timestamp;
                return;
            }

            var elapsedSeconds = Stopwatch.GetElapsedTime(axis.SimulationLastTimestamp, timestamp).TotalSeconds;
            axis.SimulationLastTimestamp = timestamp;
            if (elapsedSeconds <= 0.0 || Math.Abs(axis.FeedbackVelocity) <= double.Epsilon)
            {
                return;
            }

            var nextPosition = axis.CurrentPosition + (axis.FeedbackVelocity * elapsedSeconds);
            var hasSoftwareLimits = axisData.Min < axisData.Max;
            if (hasSoftwareLimits && nextPosition > axisData.Max)
            {
                nextPosition = axisData.Max;
                axis.LimitPlusOn = true;
                axis.LimitMinusOn = false;
                axis.FeedbackVelocity = 0.0;
                axis.FeedbackAcceleration = 0.0;
            }
            else if (hasSoftwareLimits && nextPosition < axisData.Min)
            {
                nextPosition = axisData.Min;
                axis.LimitPlusOn = false;
                axis.LimitMinusOn = true;
                axis.FeedbackVelocity = 0.0;
                axis.FeedbackAcceleration = 0.0;
            }
            else
            {
                axis.LimitPlusOn = false;
                axis.LimitMinusOn = false;
            }

            axis.CurrentPosition = nextPosition;
            axis.TargetPosition = nextPosition;
            axis.CommandPosition = nextPosition;
        }

        private void MarkAxisAlarm(string axisId)
        {
            var normalizedAxisId = NormalizeAxisId(axisId);
            if (_axes.TryGetValue(normalizedAxisId, out var axis))
            {
                axis.AlarmOn = true;
            }
        }

        private CMotionController? CreateMotionController(string controller, int deviceNo)
        {
            var controllerName = NormalizeControllerName(controller);
            if (!MotionControllerTypes.TryGetValue(controllerName, out var controllerType))
            {
                return null;
            }

            return Activator.CreateInstance(controllerType, _interfaceManager, deviceNo) as CMotionController ?? throw new InvalidOperationException($"Motion controller creation failed: {controllerName}");
        }

        private void ValidateAxisCommand(ST_MOTOR_DATA axisData, ST_AXIS_STATE axisState, EN_MOTION_COMMAND command, double parameter, double? parameter2)
        {
            ValidateAxisCommandParameters(axisData, command, parameter, parameter2);
            if (command is not (EN_MOTION_COMMAND.Home or EN_MOTION_COMMAND.MoveAbs or EN_MOTION_COMMAND.MoveRel or EN_MOTION_COMMAND.JogPositive or EN_MOTION_COMMAND.JogNegative))
            {
                return;
            }

            ValidatePreCheckIo(axisData);
            if (command is EN_MOTION_COMMAND.JogPositive or EN_MOTION_COMMAND.JogNegative)
            {
                return;
            }

            var targetPosition = command == EN_MOTION_COMMAND.MoveRel ? axisState.CurrentPosition + parameter : parameter;
            if (command == EN_MOTION_COMMAND.Home || axisData.Min >= axisData.Max)
            {
                return;
            }

            if (targetPosition < axisData.Min || targetPosition > axisData.Max)
            {
                throw new InvalidOperationException($"Motion target is out of range. Axis={axisData.Name}, Station={NormalizeStationName(axisData.StationName)}, Target={targetPosition:F3}, Range={axisData.Min:F3}~{axisData.Max:F3}");
            }
        }

        private static void ValidateAxisCommandParameters(ST_MOTOR_DATA axisData, EN_MOTION_COMMAND command, double parameter, double? parameter2)
        {
            if (command is (EN_MOTION_COMMAND.MoveAbs or EN_MOTION_COMMAND.MoveRel) && !double.IsFinite(parameter))
            {
                throw new InvalidOperationException($"Motion position must be finite. Axis={axisData.Name}");
            }

            if (command is not (EN_MOTION_COMMAND.SetProfile or EN_MOTION_COMMAND.JogPositive or EN_MOTION_COMMAND.JogNegative))
            {
                return;
            }

            if (!double.IsFinite(parameter) || parameter <= 0.0)
            {
                throw new InvalidOperationException($"Motion velocity must be a finite value greater than 0. Axis={axisData.Name}");
            }

            if (!parameter2.HasValue || !double.IsFinite(parameter2.Value) || parameter2.Value <= 0.0)
            {
                throw new InvalidOperationException($"Motion acceleration must be specified as a finite value greater than 0. Axis={axisData.Name}");
            }
        }

        private void ValidatePreCheckIo(ST_MOTOR_DATA axisData)
        {
            if (string.IsNullOrWhiteSpace(axisData.PreCheckIo))
            {
                return;
            }

            foreach (var condition in SplitPreCheckIo(axisData.PreCheckIo))
            {
                var channel = GetIoChannelOrThrow(condition.IoName);
                if (channel.IsOn != condition.ExpectedOn)
                {
                    throw new InvalidOperationException($"Motion pre-check IO failed. Axis={axisData.Name}, IO={FormatIoReference(channel)}, Expected={(condition.ExpectedOn ? "ON" : "OFF")}, Current={(channel.IsOn ? "ON" : "OFF")}");
                }
            }
        }

        private static IEnumerable<ST_PRE_CHECK_IO> SplitPreCheckIo(string preCheckIo)
        {
            foreach (var token in preCheckIo.Split([';', ',', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = token.Split(['=', ':'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var ioName = parts[0].Trim();
                var expectedOn = parts.Length < 2 || IsOnText(parts[1]);
                yield return new ST_PRE_CHECK_IO(ioName, expectedOn);
            }
        }

        private void ApplyAxisCommand(string axisId, EN_MOTION_COMMAND command, double parameter, double parameter2, bool isSimulation)
        {
            if (!_axes.TryGetValue(axisId, out var axis))
            {
                return;
            }

            switch (command)
            {
                case EN_MOTION_COMMAND.ServoOn:
                    axis.ServoOn = true;
                    break;
                case EN_MOTION_COMMAND.ServoOff:
                    axis.ServoOn = false;
                    axis.FeedbackVelocity = 0.0;
                    axis.FeedbackAcceleration = 0.0;
                    axis.CommandPosition = axis.CurrentPosition;
                    axis.TargetPosition = axis.CurrentPosition;
                    axis.SimulationLastTimestamp = Stopwatch.GetTimestamp();
                    break;
                case EN_MOTION_COMMAND.Home:
                    axis.CurrentPosition = 0.0;
                    axis.TargetPosition = 0.0;
                    axis.CommandPosition = 0.0;
                    axis.HomeCompleted = true;
                    axis.AlarmOn = false;
                    axis.FeedbackVelocity = 0.0;
                    axis.FeedbackAcceleration = 0.0;
                    axis.SimulationLastTimestamp = Stopwatch.GetTimestamp();
                    break;
                case EN_MOTION_COMMAND.MoveAbs:
                    UpdateAxisPosition(axis, parameter);
                    break;
                case EN_MOTION_COMMAND.MoveRel:
                    UpdateAxisPosition(axis, axis.CurrentPosition + parameter);
                    break;
                case EN_MOTION_COMMAND.JogPositive:
                    axis.FeedbackVelocity = Math.Abs(parameter);
                    axis.FeedbackAcceleration = Math.Abs(parameter2);
                    axis.CommandPosition = axis.CurrentPosition;
                    axis.TargetPosition = axis.CurrentPosition;
                    if (isSimulation)
                    {
                        axis.SimulationLastTimestamp = Stopwatch.GetTimestamp();
                    }

                    break;
                case EN_MOTION_COMMAND.JogNegative:
                    axis.FeedbackVelocity = -Math.Abs(parameter);
                    axis.FeedbackAcceleration = Math.Abs(parameter2);
                    axis.CommandPosition = axis.CurrentPosition;
                    axis.TargetPosition = axis.CurrentPosition;
                    if (isSimulation)
                    {
                        axis.SimulationLastTimestamp = Stopwatch.GetTimestamp();
                    }

                    break;
                case EN_MOTION_COMMAND.JogStop:
                    axis.FeedbackVelocity = 0.0;
                    axis.FeedbackAcceleration = 0.0;
                    axis.CommandPosition = axis.CurrentPosition;
                    axis.TargetPosition = axis.CurrentPosition;
                    axis.SimulationLastTimestamp = Stopwatch.GetTimestamp();
                    break;
                case EN_MOTION_COMMAND.Stop:
                    axis.CommandPosition = axis.CurrentPosition;
                    axis.TargetPosition = axis.CurrentPosition;
                    axis.FeedbackVelocity = 0.0;
                    axis.FeedbackAcceleration = 0.0;
                    axis.SimulationLastTimestamp = Stopwatch.GetTimestamp();
                    break;
                case EN_MOTION_COMMAND.ResetAlarm:
                    axis.AlarmOn = false;
                    break;
            }
        }

        private static void UpdateAxisPosition(ST_AXIS_STATE axis, double targetPosition)
        {
            axis.TargetPosition = targetPosition;
            axis.CommandPosition = targetPosition;
            axis.CurrentPosition = targetPosition;
            axis.FeedbackVelocity = 0.0;
            axis.FeedbackAcceleration = 0.0;
            axis.SimulationLastTimestamp = Stopwatch.GetTimestamp();
        }

        private static IReadOnlyList<ST_MOTOR_DATA> NormalizeMotors(IReadOnlyList<ST_MOTOR_DATA>? motors)
        {
            bool ShouldIncludeItem(global::Drilling.Common.Motion.ST_MOTOR_DATA axis)
            {
                return !string.IsNullOrWhiteSpace(axis.Name);
            }

            var loaded = motors?.Where(ShouldIncludeItem).ToArray();
            return loaded is { Length: > 0 } ? loaded : CreateDefaultMotorData();
        }

        private static Dictionary<string, ST_AXIS_STATE> CreateAxes(IReadOnlyList<ST_MOTOR_DATA> motors)
        {
            bool IsUse(global::Drilling.Common.Motion.ST_MOTOR_DATA axis)
            {
                return axis.Use;
            }

            global::Drilling.Common.Motion.CMotionManager.ST_AXIS_STATE BuildAxis(global::Drilling.Common.Motion.ST_MOTOR_DATA axis)
            {
                var position = GetInitialPosition(axis.Name);
                return new ST_AXIS_STATE(NormalizeAxisId(axis.Name), string.IsNullOrWhiteSpace(axis.DisplayName) ? axis.Name : axis.DisplayName, position, position, position, true, true, false, false, false, axis.Axis);
            }

            string SelectAxisId(global::Drilling.Common.Motion.CMotionManager.ST_AXIS_STATE axis)
            {
                return axis.AxisId;
            }

            return motors.Where(IsUse).Select(BuildAxis).ToDictionary(SelectAxisId, StringComparer.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<ST_MOTOR_DATA> CreateDefaultMotorData()
        {
            return[];
        }

        private static ST_MOTOR_DATA Motor(string name, int axis, string displayName, double initialPosition, string unit)
        {
            _ = initialPosition;
            return new ST_MOTOR_DATA(
                name, 
                true, 
                axis, 
                -1, 
                DefaultControllerName, 
                0, 
                0, 
                0, 
                1000.0, 
                "MOTION", 
                "DRILLING", 
                "", 
                displayName, 
                "", 
                false, 
                false, 
                "", 
                "", 
                "CYAN", 
                false, 
                0.0, 
                0.0, 
                0.0, 
                0.0, 
                0.0, 
                0.0, 
                0.0, 
                unit, 
                300.0, 
                300.0, 
                500.0, 
                -120.0, 
                120.0, 
                0, 
                30000, 
                "", 
                displayName, 
                0.0, 
                ""
            );
        }

        private static double GetInitialPosition(string axisName)
        {
            return NormalizeAxisId(axisName) switch
            {
                "GX" => 12.340,
                "GY" => -8.960,
                "X" => 125.000,
                "Y" => -75.000,
                "Z" => 23.500,
                "THETA" => 0.002,
                "ATTENUATOR" => 55.000,
                "BET_MAG" => 1.000,
                "BET_DIV" => 1.000,
                _ => 0.0
            };
        }

        private static string NormalizeAxisId(string axisId)
        {
            return axisId.Trim().ToUpperInvariant();
        }

        private static string NormalizeAddress(string address)
        {
            return address.Trim().ToUpperInvariant();
        }

        private ST_IO_STATE GetIoChannelOrThrow(string ioName)
        {
            if (string.IsNullOrWhiteSpace(ioName))
            {
                throw new InvalidOperationException("Motion IO name is empty.");
            }

            // Existing UI may still pass the raw address. Process code should pass the logical ID.
            var normalizedAddress = NormalizeAddress(ioName);
            if (_io.TryGetValue(NormalizeIoName(ioName), out var channel))
            {
                return channel;
            }

            bool ShouldIncludeItem(global::Drilling.Common.Motion.CMotionManager.ST_IO_STATE item)
            {
                return item.Address.Equals(normalizedAddress, StringComparison.OrdinalIgnoreCase);
            }

            var addressMatches = _io.Values.Where(ShouldIncludeItem).ToArray();
            if (addressMatches.Length == 1)
            {
                return addressMatches[0];
            }

            var normalizedName = NormalizeIoName(ioName);
            bool ShouldIncludeIo(global::Drilling.Common.Motion.CMotionManager.ST_IO_STATE item)
            {
                return NormalizeIoName(item.Name).Equals(normalizedName, StringComparison.OrdinalIgnoreCase);
            }

            var matches = _io.Values.Where(ShouldIncludeIo).ToArray();
            if (matches.Length == 1)
            {
                return matches[0];
            }

            if (matches.Length > 1)
            {
                throw new InvalidOperationException($"Motion IO name is ambiguous: {ioName}. Matches={string.Join(", ", matches.Select(FormatIoReference))}");
            }

            int SelectDisplayOrder(global::Drilling.Common.Motion.CMotionManager.ST_IO_STATE item)
            {
                return item.DisplayOrder;
            }

            throw new InvalidOperationException($"Motion IO was not registered: {ioName}. Available={string.Join(", ", _io.Values.OrderBy(SelectDisplayOrder).Select(FormatIoReference))}");
        }

        private static string FormatIoReference(ST_IO_STATE channel)
        {
            return $"{channel.Id}({channel.Address})";
        }

        private static string NormalizeIoName(string value)
        {
            char SelectItem(char ch)
            {
                return char.IsLetterOrDigit(ch) ? ch : '_';
            }

            var chars = value.Trim().ToUpperInvariant().Select(SelectItem).ToArray();
            var compact = new string (chars);
            while (compact.Contains("__", StringComparison.Ordinal))
            {
                compact = compact.Replace("__", "_", StringComparison.Ordinal);
            }

            return compact.Trim('_');
        }

        private static Dictionary<string, ST_IO_STATE> CreateIo(IReadOnlyList<ST_IO_DATA>? ioData)
        {
            bool IsUse(global::Drilling.Common.Motion.ST_IO_DATA channel)
            {
                return channel.Use;
            }

            global::Drilling.Common.Motion.CMotionManager.ST_IO_STATE BuildIo(global::Drilling.Common.Motion.ST_IO_DATA channel)
            {
                return new ST_IO_STATE(
                    NormalizeIoName(channel.Id), 
                    NormalizeAddress(channel.Address), 
                    string.IsNullOrWhiteSpace(channel.Name) ? channel.Id : channel.Name.Trim(), 
                    channel.InitialState, 
                    channel.IsOutput, 
                    NormalizeControllerName(channel.DevType), 
                    channel.DevNo, 
                    channel.DisplayOrder, 
                    channel.Description
                );
            }

            string SelectId(global::Drilling.Common.Motion.CMotionManager.ST_IO_STATE channel)
            {
                return channel.Id;
            }

            return (ioData ?? []).Where(IsUse).Select(BuildIo).ToDictionary(SelectId, StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeStationName(string stationName)
        {
            return string.IsNullOrWhiteSpace(stationName) ? "DRILLING" : stationName.Trim().ToUpperInvariant();
        }

        private static string GetControllerKey(string controller, int deviceNo)
        {
            return $"{NormalizeControllerName(controller)}:{deviceNo}";
        }

        private static IReadOnlyDictionary<string, Type> LoadMotionControllerTypes()
        {
            bool ShouldIncludeItem(global::System.Type type)
            {
                return !type.IsAbstract && typeof(CMotionController).IsAssignableFrom(type);
            }

            return typeof(CMotionController).Assembly.GetTypes().Where(ShouldIncludeItem).Select(type => new { Type = type, Attribute = type.GetCustomAttribute<CMotionControllerTypeAttribute>() }).Where(item => item.Attribute is not null && item.Attribute.ControllerNames.Count > 0).SelectMany(item => item.Attribute!.ControllerNames.Select(controller => new { Controller = NormalizeControllerName(controller), item.Type })).GroupBy(item => item.Controller, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First().Type, StringComparer.OrdinalIgnoreCase);
        }

        private static InvalidOperationException CreateMotionControllerNotRegisteredException(string controller, int deviceNo)
        {
            return new InvalidOperationException($"Motion controller is not registered. DevType={controller}, DevNo={deviceNo}. Add a C*Motion.cs class with CMotionControllerType.");
        }

        internal static string NormalizeControllerName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? DefaultControllerName : value.Trim().ToUpperInvariant().Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatMotionCommand(EN_MOTION_COMMAND command)
        {
            return command switch
            {
                EN_MOTION_COMMAND.ServoOn => "SERVO ON",
                EN_MOTION_COMMAND.ServoOff => "SERVO OFF",
                EN_MOTION_COMMAND.MoveAbs => "ABS MOVE",
                EN_MOTION_COMMAND.MoveRel => "REL MOVE",
                EN_MOTION_COMMAND.ResetAlarm => "RESET ALARM",
                EN_MOTION_COMMAND.SetProfile => "SET PROFILE",
                EN_MOTION_COMMAND.JogPositive => "JOG +",
                EN_MOTION_COMMAND.JogNegative => "JOG -",
                EN_MOTION_COMMAND.JogStop => "JOG STOP",
                _ => command.ToString().ToUpperInvariant()};
        }

        private static string FormatAggregateException(AggregateException exception)
        {
            var messages = new List<string>();
            foreach (var innerException in exception.Flatten().InnerExceptions)
            {
                messages.Add(innerException.Message);
            }

            return string.Join(" | ", messages);
        }

        private static bool IsOnText(string value)
        {
            return value.Trim().ToUpperInvariant()is "1" or "ON" or "TRUE" or "YES";
        }

        private sealed record ST_PRE_CHECK_IO(string IoName, bool ExpectedOn);
        private sealed class ST_AXIS_STATE(
            string axisId, 
            string name, 
            double currentPosition, 
            double targetPosition, 
            double commandPosition, 
            bool servoOn, 
            bool homeCompleted, 
            bool limitPlusOn, 
            bool limitMinusOn, 
            bool alarmOn, 
            int displayOrder
        )
        {
            public string AxisId { get; } = axisId;
            public string Name { get; } = name;
            public double CurrentPosition { get; set; } = currentPosition;
            public double TargetPosition { get; set; } = targetPosition;
            public double CommandPosition { get; set; } = commandPosition;
            public double FeedbackVelocity { get; set; }
            public double FeedbackAcceleration { get; set; }
            public long SimulationLastTimestamp { get; set; } = Stopwatch.GetTimestamp();
            public bool ServoOn { get; set; } = servoOn;
            public bool HomeCompleted { get; set; } = homeCompleted;
            public bool LimitPlusOn { get; set; } = limitPlusOn;
            public bool LimitMinusOn { get; set; } = limitMinusOn;
            public bool AlarmOn { get; set; } = alarmOn;
            public int DisplayOrder { get; } = displayOrder;
        }

        private sealed class ST_IO_STATE(string id, string address, string name, bool isOn, bool isOutput, string devType, int devNo, int displayOrder, string description)
        {
            public string Id { get; } = id;
            public string Address { get; } = address;
            public string Name { get; } = name;
            public bool IsOn { get; set; } = isOn;
            public bool IsOutput { get; } = isOutput;
            public string DevType { get; } = devType;
            public int DevNo { get; } = devNo;
            public int DisplayOrder { get; } = displayOrder;
            public string Description { get; } = description;
        }
    }

    public enum EN_MANUAL_STAGE_AXIS
    {
        StageY,
        ReviewX
    }

    public sealed record ST_MANUAL_STAGE_SIMULATION_AXIS(EN_MANUAL_STAGE_AXIS Axis, double Position, double Speed)
    {
        public bool IsMoving { get; init; }
    }

    /// <summary>Local motion preview only. Never sends commands to a device.</summary>
    public sealed class CManualStageMotionSimulation(Func<double>? elapsedSeconds = null)
    {
        public const double MoveSpeed = 5.0;
        private readonly object _sync = new();
        private readonly Func<double> _clock = elapsedSeconds ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        private readonly Dictionary<EN_MANUAL_STAGE_AXIS, AxisState> _axes = new()
        {
            [EN_MANUAL_STAGE_AXIS.StageY] = new(),
            [EN_MANUAL_STAGE_AXIS.ReviewX] = new()
        };
        private double? _updatedAt;
        private MotionLease? _lease;
        public bool IsReserved
        {
            get
            {
                lock (_sync)
                    return _lease is not null;
            }
        }

        public MotionLease AcquireExclusiveMotion(bool allowStageYMoving = false)
        {
            lock (_sync)
            {
                Advance();
                if (_lease is not null || _axes.Where(pair => !allowStageYMoving || pair.Key == EN_MANUAL_STAGE_AXIS.ReviewX).Any(pair => pair.Value.Speed != 0 || pair.Value.Target.HasValue))
                    throw new InvalidOperationException("Stage가 사용 중입니다. 정지 후 다시 실행해 주세요.");
                return _lease = new MotionLease(this);
            }
        }

        public IReadOnlyList<ST_MANUAL_STAGE_SIMULATION_AXIS> GetSnapshot()
        {
            lock (_sync)
            {
                Advance();
                return _axes.Select(pair => new ST_MANUAL_STAGE_SIMULATION_AXIS(pair.Key, pair.Value.Position, pair.Value.Speed) { IsMoving = pair.Value.Target.HasValue || pair.Value.Speed != 0 }).ToArray();
            }
        }

        public void Move(EN_MANUAL_STAGE_AXIS axis, double value, bool relative, double speed = MoveSpeed) => MoveCore(axis, value, relative, speed, null);
        private void MoveCore(EN_MANUAL_STAGE_AXIS axis, double value, bool relative, double speed, MotionLease? owner, double? acceleration = null)
        {
            if (!double.IsFinite(value))
                throw new ArgumentException("이동값은 유한한 숫자로 입력해 주세요.");
            if (!double.IsFinite(speed) || speed <= 0)
                throw new ArgumentException("이동 속도는 0보다 큰 유한한 숫자로 입력해 주세요.");
            lock (_sync)
            {
                if (!ReferenceEquals(_lease, owner))
                    throw new InvalidOperationException("MOF 실행 중에는 Stage 이동값을 변경할 수 없습니다.");
                Advance();
                var state = _axes[axis];
                var target = relative ? state.Position + value : value;
                if (!double.IsFinite(target))
                    throw new ArgumentException("계산된 목표 위치가 범위를 벗어났습니다.");
                var profile = acceleration.HasValue ? new CTrapezoidalMotion(Math.Abs(target - state.Position), speed, acceleration.Value) : null;
                state.Target = target == state.Position ? null : target;
                state.Speed = Math.Sign(target - state.Position) * speed;
                state.Profile = state.Target.HasValue ? profile : null;
                state.ProfileStart = _clock();
                state.ProfileOrigin = state.Position;
            }
        }

        public void StartJog(EN_MANUAL_STAGE_AXIS axis, int direction)
        {
            if (direction is not (-1 or 1))
                throw new ArgumentException("JOG 방향을 확인해 주세요.");
            lock (_sync)
            {
                if (_lease is not null)
                    throw new InvalidOperationException("MOF 실행 중에는 JOG를 사용할 수 없습니다.");
                Advance();
                foreach (var state in _axes.Values)
                {
                    state.Speed = 0;
                    state.Target = null;
                    state.Profile = null;
                }

                _axes[axis].Speed = direction * MoveSpeed;
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                Advance();
                foreach (var state in _axes.Values)
                {
                    state.Speed = 0;
                    state.Target = null;
                    state.Profile = null;
                }
            }
        }

        public void StopJog()
        {
            lock (_sync)
            {
                Advance();
                foreach (var state in _axes.Values.Where(state => state.Target is null))
                    state.Speed = 0;
            }
        }

        public void Stop(EN_MANUAL_STAGE_AXIS axis)
        {
            lock (_sync)
            {
                Advance();
                _axes[axis].Speed = 0;
                _axes[axis].Target = null;
                _axes[axis].Profile = null;
            }
        }

        private void Advance()
        {
            var now = _clock();
            var seconds = _updatedAt.HasValue ? Math.Max(0, now - _updatedAt.Value) : 0;
            _updatedAt = now;
            foreach (var state in _axes.Values)
            {
                if (state.Profile is { } profile && state.Target is { } profileTarget)
                {
                    var elapsed = Math.Max(0, now - state.ProfileStart);
                    var sample = profile.At(elapsed);
                    var direction = Math.Sign(profileTarget - state.ProfileOrigin);
                    state.Position = state.ProfileOrigin + direction * sample.Position;
                    state.Speed = direction * sample.Speed;
                    if (elapsed >= profile.Duration)
                    {
                        state.Position = profileTarget;
                        state.Target = null;
                        state.Profile = null;
                    }

                    continue;
                }

                if (state.Speed == 0)
                    continue;
                var next = state.Position + state.Speed * seconds;
                if (state.Target is { } target && (state.Speed > 0 ? next >= target : next <= target))
                {
                    state.Position = target;
                    state.Speed = 0;
                    state.Target = null;
                }
                else if (double.IsFinite(next))
                    state.Position = next;
                else
                {
                    state.Speed = 0;
                    state.Target = null;
                }
            }
        }

        private sealed class AxisState
        {
            public double Position;
            public double Speed;
            public double? Target;
            public CTrapezoidalMotion? Profile;
            public double ProfileStart;
            public double ProfileOrigin;
        }

        public sealed class MotionLease : IDisposable
        {
            private readonly CManualStageMotionSimulation _owner;
            internal MotionLease(CManualStageMotionSimulation owner) => _owner = owner;
            public void Move(EN_MANUAL_STAGE_AXIS axis, double value, double speed, double? acceleration = null) => _owner.MoveCore(axis, value, false, speed, this, acceleration);
            /// <summary>Simulator-only authoritative Stage arrival, e.g. the next glass's DoReady arrival. No device command.</summary>
            public void SetReportedPosition(EN_MANUAL_STAGE_AXIS axis, double position)
            {
                if (!double.IsFinite(position))
                    throw new ArgumentOutOfRangeException(nameof(position));
                lock (_owner._sync)
                {
                    if (!ReferenceEquals(_owner._lease, this))
                        throw new InvalidOperationException("Stage 소유권이 없습니다.");
                    _owner.Advance();
                    var state = _owner._axes[axis];
                    state.Position = position;
                    state.Target = null;
                    state.Speed = 0;
                    state.Profile = null;
                }
            }

            public void Dispose()
            {
                lock (_owner._sync)
                    if (ReferenceEquals(_owner._lease, this))
                        _owner._lease = null;
            }
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    internal sealed class CMotionControllerTypeAttribute(params string[] controllerNames) : Attribute
    {
        public IReadOnlyList<string> ControllerNames { get; } = controllerNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(NormalizeControllerName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        private static string NormalizeControllerName(string value)
        {
            return value.Trim().ToUpperInvariant().Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal abstract class CMotionController(string controller, CInterfaceManager? interfaceManager, int deviceNo = 0)
    {
        private readonly CInterfaceManager? _interfaceManager = interfaceManager;
        public string Controller { get; } = controller.Trim().ToUpperInvariant();
        public int DeviceNo { get; } = deviceNo;

        protected virtual EN_EQP_MODULE PrimaryModule
        {
            get
            {
                return EN_EQP_MODULE.Motion;
            }
        }

        protected abstract string CommandPrefix { get; }

        public bool IsSimulation()
        {
            var interfaceData = GetInterfaceData();
            return interfaceData is null || _interfaceManager?.IsSimul(interfaceData.Device, interfaceData.Number) != false;
        }

        public virtual async Task Initialize(IReadOnlyList<ST_MOTOR_DATA> axes, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var interfaceData = GetInterfaceData();
            if (_interfaceManager is null || interfaceData is null)
            {
                return;
            }

            if (!_interfaceManager.IsConnect(interfaceData.Device, interfaceData.Number))
            {
                await _interfaceManager.Connect(interfaceData.Device, interfaceData.Number, cancellationToken: cancellationToken);
            }
        }

        public virtual async Task Destroy(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var interfaceData = GetInterfaceData();
            if (_interfaceManager is null || interfaceData is null)
            {
                return;
            }

            if (_interfaceManager.IsConnect(interfaceData.Device, interfaceData.Number))
            {
                await _interfaceManager.Disconnect(interfaceData.Device, interfaceData.Number, cancellationToken);
            }
        }

        public virtual async Task ExecuteAxisCommand(ST_MOTOR_DATA axis, EN_MOTION_COMMAND command, double parameter, CancellationToken cancellationToken = default, double? parameter2 = null)
        {
            await Send(BuildAxisCommand(axis, command, parameter, parameter2), cancellationToken);
        }

        public virtual async Task SetOutput(string address, bool isOn, CancellationToken cancellationToken = default)
        {
            await Send($"{CommandPrefix}:IO:{address}:{(isOn ? "ON" : "OFF")}", cancellationToken);
        }

        public virtual ST_MOTOR_AXIS_STATUS? ReadAxisStatus(ST_MOTOR_DATA axis, CancellationToken cancellationToken = default)
        {
            var response = Send(BuildAxisCommand(axis, EN_MOTION_COMMAND.Refresh, 0.0), cancellationToken).GetAwaiter().GetResult();
            return TryParseAxisStatus(axis, response);
        }

        public async Task<ST_MOTOR_AXIS_STATUS?> ReadAxisStatusAsync(ST_MOTOR_DATA axis, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = GetInterfaceData();
            if (data is null || _interfaceManager?.IsConnect(data.Device, data.Number) != true)
                throw new IOException($"{Controller} 상태 조회 대상이 연결되어 있지 않습니다.");
            var response = await Send(BuildAxisCommand(axis, EN_MOTION_COMMAND.Refresh, 0.0), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return TryParseAxisStatus(axis, response);
        }

        public virtual async Task<bool?> ReadIo(string address, bool isOutput, CancellationToken cancellationToken = default)
        {
            var response = await Send($"{CommandPrefix}:IO:{address}:READ", cancellationToken);
            var tokens = response.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length >= 4 && tokens[0].Equals("OK", StringComparison.OrdinalIgnoreCase) && tokens[1].Equals("IO", StringComparison.OrdinalIgnoreCase) && tokens[2].Equals(address, StringComparison.OrdinalIgnoreCase) ? tokens[3].Equals("ON", StringComparison.OrdinalIgnoreCase) : null;
        }

        protected virtual string BuildAxisCommand(ST_MOTOR_DATA axis, EN_MOTION_COMMAND command, double parameter, double? parameter2 = null)
        {
            var commandText = command switch
            {
                EN_MOTION_COMMAND.ServoOn => "SERVO_ON",
                EN_MOTION_COMMAND.ServoOff => "SERVO_OFF",
                EN_MOTION_COMMAND.Home => "HOME",
                EN_MOTION_COMMAND.MoveAbs => $"MOVE_ABS:{FormatProtocolDouble(parameter, "F3")}",
                EN_MOTION_COMMAND.MoveRel => $"MOVE_REL:{FormatProtocolDouble(parameter, "F3")}",
                EN_MOTION_COMMAND.SetProfile => $"SET_PROFILE:{FormatProtocolDouble(parameter, "F3")}:{FormatProtocolDouble(RequireSecondaryParameter(command, parameter2), "F3")}",
                EN_MOTION_COMMAND.JogPositive => $"JOG:{FormatProtocolDouble(Math.Abs(parameter), "F3")}:{FormatProtocolDouble(Math.Abs(RequireSecondaryParameter(command, parameter2)), "F3")}",
                EN_MOTION_COMMAND.JogNegative => $"JOG:{FormatProtocolDouble(-Math.Abs(parameter), "F3")}:{FormatProtocolDouble(Math.Abs(RequireSecondaryParameter(command, parameter2)), "F3")}",
                EN_MOTION_COMMAND.JogStop => "JOG_STOP",
                EN_MOTION_COMMAND.Stop => "STOP",
                EN_MOTION_COMMAND.ResetAlarm => "RESET_ALARM",
                EN_MOTION_COMMAND.Refresh => "READ",
                _ => "READ"
            };
            return $"{CommandPrefix}:AXIS:{axis.Axis}:{axis.Name}:{commandText}";
        }

        protected static string FormatProtocolDouble(double value, string format)
        {
            if (!double.IsFinite(value))
            {
                throw new InvalidOperationException("Motion command parameter must be finite.");
            }

            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        protected static double RequireSecondaryParameter(EN_MOTION_COMMAND command, double? parameter)
        {
            if (!parameter.HasValue)
            {
                throw new InvalidOperationException($"Motion {command} requires an acceleration parameter.");
            }

            return parameter.Value;
        }

        protected async Task<string> Send(string command, CancellationToken cancellationToken)
        {
            var isPriorityXpsStop = IsPriorityXpsStopCommand(command);
            if (!isPriorityXpsStop)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var interfaceData = GetInterfaceData();
            if (_interfaceManager is null || interfaceData is null)
            {
                throw new InvalidOperationException($"{Controller} motion interface is not registered.");
            }

            if (isPriorityXpsStop && !_interfaceManager.IsConnect(interfaceData.Device, interfaceData.Number))
            {
                throw new InvalidOperationException($"{Controller} priority stop failed because the interface is offline: " + $"{interfaceData.Device}[{interfaceData.Number}]/{interfaceData.NickName}");
            }

            if (!isPriorityXpsStop && !_interfaceManager.IsConnect(interfaceData.Device, interfaceData.Number))
            {
                await _interfaceManager.Connect(interfaceData.Device, interfaceData.Number, cancellationToken: cancellationToken);
            }

            if (!isPriorityXpsStop && !_interfaceManager.IsConnect(interfaceData.Device, interfaceData.Number))
            {
                throw new InvalidOperationException($"{Controller} motion interface is offline: {interfaceData.Device}[{interfaceData.Number}]/{interfaceData.NickName}");
            }

            CancellationTokenSource? priorityStopTimeout = null;
            var commandCancellationToken = cancellationToken;
            var priorityStopTimeoutMs = 0;
            if (isPriorityXpsStop)
            {
                priorityStopTimeoutMs = GetPriorityStopTimeoutMs(interfaceData);
                priorityStopTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(priorityStopTimeoutMs));
                commandCancellationToken = priorityStopTimeout.Token;
            }

            string response;
            try
            {
                response = await _interfaceManager.ExecuteFunction(interfaceData.Device, interfaceData.Number, command, commandCancellationToken);
            }
            catch (OperationCanceledException exception)when (isPriorityXpsStop && priorityStopTimeout?.IsCancellationRequested == true)
            {
                throw new TimeoutException($"{Controller} priority stop timed out after {priorityStopTimeoutMs} ms: {command}", exception);
            }
            finally
            {
                priorityStopTimeout?.Dispose();
            }

            if (string.IsNullOrWhiteSpace(response))
            {
                throw new TimeoutException($"{Controller} motion command timeout: {command}");
            }

            if (response.StartsWith("ERR", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{Controller} motion command failed. Command={command}, Response={response}");
            }

            return response;
        }

        private int GetPriorityStopTimeoutMs(ST_INTERFACE_DATA interfaceData)
        {
            bool MatchesItem(global::Drilling.Common.Interface.CInterfaceDevice device)
            {
                return device.Data.Device == interfaceData.Device && device.Data.Number == interfaceData.Number && device.Data.InterfaceType == interfaceData.InterfaceType;
            }

            var configuredTimeoutMs = _interfaceManager?.Devices.FirstOrDefault(MatchesItem)?.ConnectOption.TimeoutMs ?? 0;
            return configuredTimeoutMs > 0 ? configuredTimeoutMs : 5000;
        }

        private static bool IsPriorityXpsStopCommand(string command)
        {
            return command.StartsWith("XPS:AXIS:", StringComparison.OrdinalIgnoreCase) && (command.EndsWith(":JOG_STOP", StringComparison.OrdinalIgnoreCase) || command.EndsWith(":STOP", StringComparison.OrdinalIgnoreCase));
        }

        protected virtual ST_MOTOR_AXIS_STATUS? TryParseAxisStatus(ST_MOTOR_DATA axis, string response)
        {
            var tokens = response.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 5 || !tokens[0].Equals("OK", StringComparison.OrdinalIgnoreCase) || !tokens[1].Equals("AXIS", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var command = tokens[3].ToUpperInvariant();
            if (command != "FPOS" || !double.TryParse(tokens[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var position) || !double.IsFinite(position))
            {
                return null;
            }

            var axisId = string.IsNullOrWhiteSpace(axis.Name) ? axis.Axis.ToString() : axis.Name.Trim().ToUpperInvariant();
            return new ST_MOTOR_AXIS_STATUS(axisId, string.IsNullOrWhiteSpace(axis.DisplayName) ? axisId : axis.DisplayName, position, position, position, true, true, false, false, false, AxisNo: axis.Axis);
        }

        private ST_INTERFACE_DATA? GetInterfaceData()
        {
            if (_interfaceManager is null)
            {
                return null;
            }

            bool ShouldIncludeItem(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.Number == DeviceNo;
            }

            string SelectNickName(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.NickName;
            }

            var primary = _interfaceManager.GetInterfaceList(PrimaryModule).Where(ShouldIncludeItem).OrderBy(SelectNickName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (primary is not null)
            {
                return primary;
            }

            bool MatchesNumber(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.Number == DeviceNo;
            }

            string SelectNickName2(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.NickName;
            }

            return _interfaceManager.GetInterfaceList(EN_EQP_MODULE.Motion).Where(MatchesNumber).OrderBy(SelectNickName2, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
    }

    public enum EN_MOTION_COMMAND
    {
        ServoOn,
        ServoOff,
        Home,
        MoveAbs,
        MoveRel,
        Stop,
        ResetAlarm,
        SetProfile,
        JogPositive,
        JogNegative,
        JogStop,
        Refresh
    }

    public sealed record ST_IO_STATUS(string Id, string Address, string Name, bool IsOn, bool IsOutput);
    public sealed record ST_IO_DATA(string Id, bool Use, string Address, string Name, bool IsOutput, string DevType, int DevNo, bool InitialState, int DisplayOrder, string Description);
    public sealed record ST_MOTOR_AXIS_STATUS(
        string AxisId, 
        string Name, 
        double CurrentPosition, 
        double TargetPosition, 
        double CommandPosition, 
        bool ServoOn, 
        bool HomeCompleted, 
        bool LimitPlusOn, 
        bool LimitMinusOn, 
        bool AlarmOn, 
        double FeedbackVelocity = 0.0, 
        double FeedbackAcceleration = 0.0, 
        int AxisNo = -1
    );
    public sealed record ST_MOTOR_DATA(
        string Name, 
        bool Use, 
        int Axis, 
        int VirtureAxis, 
        string DevType, 
        int DevNo, 
        int CoordinateNo, 
        int MotorType, 
        double Scale, 
        string System, 
        string StationName, 
        string Subordinate, 
        string DisplayName, 
        string AxisDir, 
        bool AlignReverse, 
        bool ProcessReverse, 
        string Dir, 
        string ProductIndex, 
        string AxisColor, 
        bool ReverseDir, 
        double CorrectionAngle, 
        double OffsetX, 
        double OffsetY, 
        double OffsetZ, 
        double OffsetXT, 
        double OffsetYT, 
        double OffsetZT, 
        string Unit, 
        double MaxVel, 
        double InterlockMaxVel, 
        double MaxAcc, 
        double Min, 
        double Max, 
        int HomePlc, 
        int HomeTimeout, 
        string HomePlcFlag, 
        string Description, 
        double LoadAlarmValue, 
        string PreCheckIo
    );
    public sealed record ST_MOTION_STATION_STATUS(string StationName, string SystemName, bool HasAlarm, IReadOnlyList<ST_MOTOR_AXIS_STATUS> Axes);
    public sealed record ST_MOTION_CONTROLLER_STATUS(string DevType, int DevNo, bool IsRegistered, bool IsSimulation, int AxisCount, IReadOnlyList<string> AxisIds);
    public sealed record ST_MOTION_ABS_MOVE_REQUEST(string AxisId, double TargetPosition, double Speed, double Acceleration);
    public abstract class CMotorFileBase
    {
        public abstract IReadOnlyList<ST_MOTOR_DATA> LoadAll(CancellationToken cancellationToken = default);
    }

    /// <summary>Rest-to-rest trapezoidal/triangular motion, in mm and seconds.</summary>
    public sealed class CTrapezoidalMotion
    {
        public CTrapezoidalMotion(double distance, double speed, double acceleration, double? deceleration = null)
        {
            var dec = deceleration ?? acceleration;
            if (!double.IsFinite(distance) || distance < 0 || !double.IsFinite(speed) || speed <= 0 || !double.IsFinite(acceleration) || acceleration <= 0 || !double.IsFinite(dec) || dec <= 0)
                throw new ArgumentException("이동 거리·속도·가감속 값을 확인해 주세요.");
            Distance = distance;
            Acceleration = acceleration;
            Deceleration = dec;
            PeakSpeed = Math.Min(speed, Math.Sqrt(2 * distance / (1 / acceleration + 1 / dec)));
            AccelerationDistance = PeakSpeed * PeakSpeed / (2 * acceleration);
            DecelerationDistance = PeakSpeed * PeakSpeed / (2 * dec);
            CruiseDistance = Math.Max(0, distance - AccelerationDistance - DecelerationDistance);
            AccelerationTime = PeakSpeed / acceleration;
            CruiseTime = PeakSpeed == 0 ? 0 : CruiseDistance / PeakSpeed;
            Duration = AccelerationTime + CruiseTime + PeakSpeed / dec;
        }

        public double Distance { get; }
        public double PeakSpeed { get; }
        public double Acceleration { get; }
        public double Deceleration { get; }
        public double AccelerationDistance { get; }
        public double DecelerationDistance { get; }
        public double CruiseDistance { get; }
        public double AccelerationTime { get; }
        public double CruiseTime { get; }
        public double Duration { get; }

        public double TimeAt(double distance)
        {
            if (!double.IsFinite(distance) || distance < -1e-8 || distance > Distance + 1e-8)
                throw new ArgumentOutOfRangeException(nameof(distance), "MCC 이동 구간 밖입니다.");
            distance = Math.Clamp(distance, 0, Distance);
            if (distance <= AccelerationDistance)
                return Math.Sqrt(2 * distance / Acceleration);
            if (distance <= AccelerationDistance + CruiseDistance)
                return AccelerationTime + (distance - AccelerationDistance) / PeakSpeed;
            return Duration - Math.Sqrt(2 * (Distance - distance) / Deceleration);
        }

        public (double Position, double Speed) At(double seconds)
        {
            if (seconds >= Duration)
                return (Distance, 0);
            if (seconds <= 0)
                return (0, 0);
            if (seconds < AccelerationTime)
                return (0.5 * Acceleration * seconds * seconds, Acceleration * seconds);
            if (seconds < AccelerationTime + CruiseTime)
                return (AccelerationDistance + PeakSpeed * (seconds - AccelerationTime), PeakSpeed);
            var left = Duration - seconds;
            return (Distance - 0.5 * Deceleration * left * left, Deceleration * left);
        }
    }
}
