namespace Drilling.Common.Interface
{
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Collections.Concurrent;
    using Drilling.Common.Managers;
    using Drilling.Common.Log;
    using Drilling.Common.Alarm;
    using Drilling.Common.Interface;
    using Drilling.Common.Interface.Vision;
    using Drilling.Common.InterLock;
    using Drilling.Common.Motion;
    using Drilling.Common.Station;

    public partial class CInterfaceManager
    {
#region 공유 상태·초기값
        public const string AttenuatorSoftwareLimitMessagePrefix = "ATT SW LIMIT: ";
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _attenuatorMoveLocks = new();
        private Func<IReadOnlyList<ST_SYSTEM_PARAMETER>>? _attenuatorLimitSettings;
        private Func<int>? _visionLineCapacity;
        private Action<CVisionLineCountExceededException>? _visionCountLimitAlarm;
        private readonly ConcurrentDictionary<string, CInterfaceDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
        private readonly CLogManager? _logManager;
        private readonly CBETFileBase? _betFile;
        private readonly CMelsec _melsec;
        private readonly CPicoMotorService _picoMotorService = new();
        private bool? _simulationMode;
        public event Func<ST_INTERFACE_RECEIVED_MESSAGE, CancellationToken, Task<string>>? MessageReceived;
        public event Action<ST_INTERFACE_PROTOCOL_TRACE>? ProtocolTrace;
        private readonly Dictionary<int, HashSet<int>> _laserOnHeads = [];
        private readonly ConcurrentDictionary<int, ST_TALON_STATUS> _talonStatuses = new();
        private readonly Dictionary<int, ST_ATTENUATOR_STATUS> _attenuatorStatuses = [];
        private readonly object _attenuatorStatusLock = new();
        private readonly ConcurrentDictionary<int, ST_BET_STATUS> _betStatuses = new();
        private readonly ConcurrentDictionary<int, ST_POWER_METER_STATUS> _powerMeterStatuses = new();
        private readonly Dictionary<int, int> _powerMeterSimulationAttenuators = [];
        private readonly SemaphoreSlim _reloadLock = new(1, 1);
        private readonly ConcurrentDictionary<int, CManualStageMotionSimulation> _stageMotionSimulations = new();
#endregion
#region 생성·초기화
        public CInterfaceManager(
            bool? simulationMode = null, 
            CLogManager? logManager = null, 
            CBETFileBase? betFile = null, 
            IReadOnlyList<ST_MELSEC_MAP_DATA>? melsecMap = null, 
            CMelsecNetApi? melsecNetApi = null
        )
        {
            _simulationMode = simulationMode;
            _logManager = logManager;
            _betFile = betFile;
            _melsec = new CMelsec(this, _logManager, melsecMap, melsecNetApi);
            StageReviewSequences = new CStageReviewSequences(this);
            AttenuatorSnapshots = new CAttenuatorStatusSnapshotService(
                () => GetInterfaceList(EN_EQP_MODULE.Attenuator).Select(data => data.Number).ToArray(), 
                number => IsConnect(EN_EQP_MODULE.Attenuator, number) || IsSimul(EN_EQP_MODULE.Attenuator, number), 
                async (number, command, token) =>
            {
                if (!IsConnect(EN_EQP_MODULE.Attenuator, number) && !IsSimul(EN_EQP_MODULE.Attenuator, number))
                    throw new IOException("ATT가 연결되어 있지 않습니다.");
                var response = await ExecuteAttenuatorCommand(number, command, cancellationToken: token, suppressCommandLog: true).ConfigureAwait(false);
                if (!response.IsSuccess)
                    throw new IOException(response.Message);
                if (!IsSimul(EN_EQP_MODULE.Attenuator, number))
                    return response.Message;
                var status = GetAttenuatorStatusValue(number);
                return command switch
                {
                    EN_ATTENUATOR_COMMAND.PollState => $"TS:0:{status.CommandState}",
                    EN_ATTENUATOR_COMMAND.PollCurrentPosition => $"TP:{status.CurrentPosition.ToString("R", CultureInfo.InvariantCulture)}",
                    EN_ATTENUATOR_COMMAND.PollTargetPosition => $"TH:{status.TargetPosition.ToString("R", CultureInfo.InvariantCulture)}",
                    _ => throw new InvalidOperationException("지원하지 않는 ATT 스냅샷 명령입니다.")};
            }, 
                GetAttenuatorStatusValue
            );
            OpticSnapshots = CreateOpticSnapshots();
        }

#endregion
#region 기본 처리
        public CAttenuatorStatusSnapshotService AttenuatorSnapshots { get; }
        public CStageReviewSequences StageReviewSequences { get; }
        public COpticStatusSnapshotService OpticSnapshots { get; }

        public void ConfigureVisionLineProtocol(Func<int> capacity, Action<CVisionLineCountExceededException>? reportCountAlarm = null)
        {
            ArgumentNullException.ThrowIfNull(capacity);
            _ = CVisionProtocol.ValidateLineCapacity(capacity());
            _visionLineCapacity = capacity;
            _visionCountLimitAlarm = reportCountAlarm;
            foreach (var device in _devices.Values)
                device.ConfigureVisionLineProtocol(capacity);
        }

        public bool IsSimulation
        {
            get
            {
                bool IsIsSimulation(global::Drilling.Common.Interface.CInterfaceDevice device)
                {
                    return device.IsSimulation;
                }

                return _devices.Count == 0 ? _simulationMode ?? true : _devices.Values.All(IsIsSimulation);
            }
        }

        public IReadOnlyList<CInterfaceDevice> Devices
        {
            get
            {
                return _devices.Values.ToArray();
            }
        }

        public CMelsec Melsec
        {
            get
            {
                return _melsec;
            }
        }

        public async Task SetSimulationMode(bool enabled, CancellationToken cancellationToken = default)
        {
            await AttenuatorSnapshots.StopAsync(cancellationToken);
            await OpticSnapshots.StopAsync();
            bool IsMelsecDevice(CInterfaceDevice device)
            {
                return device.Data.Device == EN_EQP_MODULE.Melsec;
            }

            var restartMelsec = _devices.Values.Any(IsMelsecDevice);
            if (restartMelsec)
            {
                _melsec.DeInitialize();
            }

            _simulationMode = enabled;
            foreach (var device in _devices.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await device.SetSimulationMode(enabled, cancellationToken);
            }

            if (restartMelsec)
            {
                _melsec.Initialize();
            }

            AttenuatorSnapshots.Start();
            OpticSnapshots.Start();
        }

        public void Register(ST_INTERFACE_DATA data)
        {
            var key = CreateDeviceKey(data.Device, data.Number);
            if (_devices.ContainsKey(key))
            {
                throw new InvalidOperationException($"Interface device was already registered: {FormatDeviceName(data)}");
            }

            _devices[key] = CreateDevice(data);
        }

        private CInterfaceDevice CreateDevice(ST_INTERFACE_DATA data)
        {
            var device = new CInterfaceDevice(data, _simulationMode ?? data.IsSimulation);
            if (_visionLineCapacity is not null)
                device.ConfigureVisionLineProtocol(_visionLineCapacity);
            if (data.Device == EN_EQP_MODULE.Melsec)
            {
                device.EnableExternalCommunication();
            }

            device.MessageReceived += OnDeviceMessageReceived;
            device.ProtocolTrace += OnDeviceProtocolTrace;
            return device;
        }

        public Task Initialize(CancellationToken cancellationToken = default)
        {
            return Connect(init: true, cancellationToken);
        }

        public async Task Destroy(CancellationToken cancellationToken = default)
        {
            await Disconnect(cancellationToken);
            await AttenuatorSnapshots.DisposeAsync();
            await OpticSnapshots.DisposeAsync();
            _picoMotorService.DisconnectAll();
            _devices.Clear();
            ClearDeviceStateMaps();
        }

        public async Task<int> Connect(bool init = false, CancellationToken cancellationToken = default)
        {
            var connectedCount = 0;
            foreach (var device in _devices.Values)
            {
                if (!device.Data.AutoConnection)
                {
                    continue;
                }

                if (device.ConnectionState is EN_COMM_STATE.Online or EN_COMM_STATE.Simulation && (device.Data.Device != EN_EQP_MODULE.Melsec || _melsec.IsCommunicationAvailable))
                {
                    connectedCount++;
                    continue;
                }

                var beforeState = device.ConnectionState;
                device.ResetAutoReconnectPolicy();
                if (device.Data.Device == EN_EQP_MODULE.Melsec)
                {
                    ConnectMelsecDevice(device, cancellationToken);
                }
                else
                {
                    await device.Connect(cancellationToken);
                }

                WriteConnectionLog(init ? "INIT_CONNECT" : "CONNECT", device, beforeState);
                if (device.ConnectionState is EN_COMM_STATE.Online or EN_COMM_STATE.Simulation)
                {
                    connectedCount++;
                }
            }

            AttenuatorSnapshots.Start();
            OpticSnapshots.Start();
            return connectedCount;
        }

        public async Task<int> Disconnect(CancellationToken cancellationToken = default)
        {
            await AttenuatorSnapshots.StopAsync(cancellationToken);
            await OpticSnapshots.StopAsync();
            foreach (var device in _devices.Values)
            {
                var beforeState = device.ConnectionState;
                device.SuppressAutoReconnect();
                if (device.Data.Device == EN_EQP_MODULE.Melsec)
                {
                    DisconnectMelsecDevice(device, cancellationToken);
                }
                else
                {
                    await device.Disconnect(cancellationToken);
                }

                WriteConnectionLog("DISCONNECT", device, beforeState);
            }

            _melsec.DeInitialize();
            _picoMotorService.DisconnectAll();
            return _devices.Count;
        }

        public async Task Connect(EN_EQP_MODULE module, int number, bool autoConnection = true, CancellationToken cancellationToken = default)
        {
            var device = GetDeviceOrThrow(module, number);
            device.ResetAutoReconnectPolicy();
            await ConnectDevice(device, autoConnection, cancellationToken);
            if (module == EN_EQP_MODULE.Attenuator)
            {
                AttenuatorSnapshots.Invalidate(number, "LOADING", "ATT 연결 후 새 상태를 기다리고 있습니다.");
                AttenuatorSnapshots.Start();
            }

            OpticSnapshots.Start();
        }

        public async Task Disconnect(EN_EQP_MODULE module, int number, CancellationToken cancellationToken = default)
        {
            if (!_devices.TryGetValue(CreateDeviceKey(module, number), out var device))
            {
                return;
            }

            OpticSnapshots.Invalidate(module, number);
            if (module == EN_EQP_MODULE.Attenuator)
                AttenuatorSnapshots.Invalidate(number);
            await DisconnectDevice(device, cancellationToken);
            OpticSnapshots.Invalidate(module, number);
            if (module == EN_EQP_MODULE.Attenuator)
                AttenuatorSnapshots.Invalidate(number);
        }

        public async Task Reconnect(EN_EQP_MODULE module, int number, CancellationToken cancellationToken = default)
        {
            var device = GetDeviceOrThrow(module, number);
            if (module == EN_EQP_MODULE.Attenuator)
                AttenuatorSnapshots.Invalidate(number, "LOADING", "ATT 재연결 후 상태를 기다리고 있습니다.");
            var beforeState = device.ConnectionState;
            if (device.Data.Device == EN_EQP_MODULE.Melsec)
            {
                DisconnectMelsecDevice(device, cancellationToken);
                ConnectMelsecDevice(device, cancellationToken);
            }
            else
            {
                await device.Disconnect(cancellationToken);
                device.ResetAutoReconnectPolicy();
                await device.Connect(cancellationToken);
            }

            WriteConnectionLog("RECONNECT", device, beforeState);
            if (module == EN_EQP_MODULE.Attenuator)
            {
                AttenuatorSnapshots.Invalidate(number, "LOADING", "ATT 재연결 후 새 상태를 기다리고 있습니다.");
                AttenuatorSnapshots.Start();
            }

            OpticSnapshots.Start();
        }

        public virtual async Task<string> ExecuteFunction(EN_EQP_MODULE module, int number, string function, CancellationToken cancellationToken = default)
        {
            var device = GetDeviceOrThrow(module, number);
            return await ExecuteDeviceFunction(device, function, cancellationToken);
        }

        public virtual async Task<string> ExecuteFunctionNoLog(EN_EQP_MODULE module, int number, string function, CancellationToken cancellationToken = default)
        {
            var device = GetDeviceOrThrow(module, number);
            return await ExecuteDeviceFunction(device, function, cancellationToken, suppressLog: true);
        }

        public async Task<string> ExecuteFunctionWithLogText(EN_EQP_MODULE module, int number, string function, string logText, CancellationToken cancellationToken = default)
        {
            var device = GetDeviceOrThrow(module, number);
            return await ExecuteDeviceFunction(device, function, cancellationToken, logText);
        }

        public bool IsConnect(EN_EQP_MODULE module, int number)
        {
            return _devices.TryGetValue(CreateDeviceKey(module, number), out var device) && device.ConnectionState is EN_COMM_STATE.Online or EN_COMM_STATE.Simulation;
        }

        public bool IsSimul(EN_EQP_MODULE module, int number)
        {
            return _devices.TryGetValue(CreateDeviceKey(module, number), out var device) && device.ConnectionState == EN_COMM_STATE.Simulation;
        }

        public ST_INTERFACE_DATA? GetInterfaceData(EN_EQP_MODULE module, int number)
        {
            return _devices.TryGetValue(CreateDeviceKey(module, number), out var device) ? device.Data : null;
        }

        internal void UpdateMelsecCommunicationState(int number, bool online, string lastSent, string lastReceived, string lastError)
        {
            if (!_devices.TryGetValue(CreateDeviceKey(EN_EQP_MODULE.Melsec, number), out var device) || device.IsSimulation)
            {
                return;
            }

            device.SetExternalCommunicationState(online ? EN_COMM_STATE.Online : EN_COMM_STATE.Offline, lastSent, lastReceived, lastError);
        }

        public async Task Connect(string nickName, bool autoConnection = true, CancellationToken cancellationToken = default)
        {
            var device = GetDeviceByNickNameOrThrow(nickName);
            device.ResetAutoReconnectPolicy();
            await ConnectDevice(device, autoConnection, cancellationToken);
            if (device.Data.Device == EN_EQP_MODULE.Attenuator)
            {
                AttenuatorSnapshots.Invalidate(device.Data.Number, "LOADING", "ATT 연결 후 새 상태를 기다리고 있습니다.");
                AttenuatorSnapshots.Start();
            }

            OpticSnapshots.Start();
        }

        public async Task Disconnect(string nickName, CancellationToken cancellationToken = default)
        {
            if (!TryGetDeviceByNickName(nickName, out var device, throwIfAmbiguous: true) || device is null)
            {
                return;
            }

            OpticSnapshots.Invalidate(device.Data.Device, device.Data.Number);
            if (device.Data.Device == EN_EQP_MODULE.Attenuator)
                AttenuatorSnapshots.Invalidate(device.Data.Number);
            await DisconnectDevice(device, cancellationToken);
            OpticSnapshots.Invalidate(device.Data.Device, device.Data.Number);
            if (device.Data.Device == EN_EQP_MODULE.Attenuator)
                AttenuatorSnapshots.Invalidate(device.Data.Number);
        }

        public async Task Reconnect(string nickName, CancellationToken cancellationToken = default)
        {
            var device = GetDeviceByNickNameOrThrow(nickName);
            if (device.Data.Device == EN_EQP_MODULE.Attenuator)
                AttenuatorSnapshots.Invalidate(device.Data.Number, "LOADING", "ATT 재연결 후 상태를 기다리고 있습니다.");
            var beforeState = device.ConnectionState;
            if (device.Data.Device == EN_EQP_MODULE.Melsec)
            {
                DisconnectMelsecDevice(device, cancellationToken);
                ConnectMelsecDevice(device, cancellationToken);
            }
            else
            {
                await device.Disconnect(cancellationToken);
                device.ResetAutoReconnectPolicy();
                await device.Connect(cancellationToken);
            }

            WriteConnectionLog("RECONNECT", device, beforeState);
            if (device.Data.Device == EN_EQP_MODULE.Attenuator)
            {
                AttenuatorSnapshots.Invalidate(device.Data.Number, "LOADING", "ATT 재연결 후 새 상태를 기다리고 있습니다.");
                AttenuatorSnapshots.Start();
            }

            OpticSnapshots.Start();
        }

        public async Task<string> ExecuteFunction(string nickName, string function, CancellationToken cancellationToken = default)
        {
            var device = GetDeviceByNickNameOrThrow(nickName);
            return await ExecuteDeviceFunction(device, function, cancellationToken);
        }

        public bool IsConnect(string nickName)
        {
            return TryGetDeviceByNickName(nickName, out var device, throwIfAmbiguous: true) && device is not null && device.ConnectionState is EN_COMM_STATE.Online or EN_COMM_STATE.Simulation;
        }

        public bool IsSimul(string nickName)
        {
            return TryGetDeviceByNickName(nickName, out var device, throwIfAmbiguous: true) && device is not null && device.ConnectionState == EN_COMM_STATE.Simulation;
        }

        public ST_INTERFACE_DATA? GetInterfaceData(string nickName)
        {
            return TryGetDeviceByNickName(nickName, out var device, throwIfAmbiguous: true) && device is not null ? device.Data : null;
        }

        public IReadOnlyList<ST_INTERFACE_DATA> GetInterfaceList(EN_EQP_MODULE? module = null)
        {
            bool MatchesCinterfacedevice(global::Drilling.Common.Interface.CInterfaceDevice device)
            {
                return module is null || device.Data.Device == module;
            }

            global::Drilling.Common.Interface.ST_INTERFACE_DATA SelectData(global::Drilling.Common.Interface.CInterfaceDevice device)
            {
                return device.Data;
            }

            global::Drilling.Common.Interface.EN_EQP_MODULE SelectDevice(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.Device;
            }

            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.Number;
            }

            string SelectNickName(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.NickName;
            }

            return _devices.Values.Where(MatchesCinterfacedevice).Select(SelectData).OrderBy(SelectDevice).ThenBy(SelectNumber).ThenBy(SelectNickName, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public IReadOnlyList<ST_INTERFACE_COMM_STATUS> GetInterfaceCommunicationList(EN_EQP_MODULE? module = null)
        {
            bool MatchesCinterfacedevice(global::Drilling.Common.Interface.CInterfaceDevice device)
            {
                return module is null || device.Data.Device == module;
            }

            global::Drilling.Common.Interface.ST_INTERFACE_COMM_STATUS SelectValue(global::Drilling.Common.Interface.CInterfaceDevice device)
            {
                return device.GetCommunicationStatus();
            }

            global::Drilling.Common.Interface.EN_EQP_MODULE SelectModule(global::Drilling.Common.Interface.ST_INTERFACE_COMM_STATUS status)
            {
                return status.Module;
            }

            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_COMM_STATUS status)
            {
                return status.Number;
            }

            string SelectNickName(global::Drilling.Common.Interface.ST_INTERFACE_COMM_STATUS status)
            {
                return status.NickName;
            }

            return _devices.Values.Where(MatchesCinterfacedevice).Select(SelectValue).OrderBy(SelectModule).ThenBy(SelectNumber).ThenBy(SelectNickName, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public IReadOnlyList<ST_DEVICE_COMM_STATUS> GetCommunicationStatus(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            global::Drilling.Common.Interface.EN_EQP_MODULE SelectValue(global::Drilling.Common.Interface.CInterfaceDevice device)
            {
                return device.Data.Device;
            }

            global::Drilling.Common.Interface.EN_COMM_STATE SelectConnectionState(global::Drilling.Common.Interface.CInterfaceDevice device)
            {
                return device.ConnectionState;
            }

            global::Drilling.Common.Interface.ST_DEVICE_COMM_STATUS BuildDevice(global::System.Linq.IGrouping<global::Drilling.Common.Interface.EN_EQP_MODULE, global::Drilling.Common.Interface.CInterfaceDevice> group)
            {
                return new ST_DEVICE_COMM_STATUS(group.Key, CollapseConnectionState(group.Select(SelectConnectionState)));
            }

            global::Drilling.Common.Interface.EN_EQP_MODULE SelectModule(global::Drilling.Common.Interface.ST_DEVICE_COMM_STATUS status)
            {
                return status.Module;
            }

            return _devices.Values.GroupBy(SelectValue).Select(BuildDevice).OrderBy(SelectModule).ToArray();
        }

        private static EN_COMM_STATE CollapseConnectionState(IEnumerable<EN_COMM_STATE> states)
        {
            var stateArray = states.ToArray();
            bool MatchesItem(global::Drilling.Common.Interface.EN_COMM_STATE state)
            {
                return state == EN_COMM_STATE.Offline;
            }

            if (stateArray.Any(MatchesItem))
            {
                return EN_COMM_STATE.Offline;
            }

            bool MatchesItem2(global::Drilling.Common.Interface.EN_COMM_STATE state)
            {
                return state == EN_COMM_STATE.Simulation;
            }

            if (stateArray.All(MatchesItem2))
            {
                return EN_COMM_STATE.Simulation;
            }

            return EN_COMM_STATE.Online;
        }

        private async Task ConnectDevice(CInterfaceDevice device, bool autoConnection, CancellationToken cancellationToken)
        {
            if (!autoConnection && device.Data.AutoConnection)
            {
                return;
            }

            var beforeState = device.ConnectionState;
            if (device.Data.Device == EN_EQP_MODULE.Melsec)
            {
                ConnectMelsecDevice(device, cancellationToken);
            }
            else
            {
                await device.Connect(cancellationToken);
            }

            WriteConnectionLog("CONNECT", device, beforeState);
        }

        private async Task AutoReconnectDevice(EN_EQP_MODULE module, int number, CancellationToken cancellationToken)
        {
            var device = GetDeviceOrThrow(module, number);
            var beforeState = device.ConnectionState;
            await device.Connect(cancellationToken);
            if (device.ConnectionState != beforeState || device.ConnectionState is EN_COMM_STATE.Online or EN_COMM_STATE.Simulation)
            {
                WriteConnectionLog("AUTO_RECONNECT", device, beforeState);
            }
        }

        private async Task DisconnectDevice(CInterfaceDevice device, CancellationToken cancellationToken)
        {
            var beforeState = device.ConnectionState;
            device.SuppressAutoReconnect();
            if (device.Data.Device == EN_EQP_MODULE.Melsec)
            {
                DisconnectMelsecDevice(device, cancellationToken);
            }
            else
            {
                await device.Disconnect(cancellationToken);
            }

            WriteConnectionLog("DISCONNECT", device, beforeState);
        }

        private void ConnectMelsecDevice(CInterfaceDevice device, CancellationToken cancellationToken)
        {
            _melsec.Initialize();
            if (device.IsSimulation)
            {
                _melsec.Open(device.Data.Number, cancellationToken);
                device.SetExternalCommunicationState(EN_COMM_STATE.Simulation, "[SIMULATION] OPEN", "[SIMULATION] READY", "");
                return;
            }

            try
            {
                _melsec.Open(device.Data.Number, cancellationToken);
                device.SetExternalCommunicationState(EN_COMM_STATE.Online, "OPEN", "CONNECTED", "");
            }
            catch (Exception exception)when (exception is not OperationCanceledException)
            {
                device.SetExternalCommunicationState(EN_COMM_STATE.Offline, "OPEN", "", exception.Message);
            }
        }

        private void DisconnectMelsecDevice(CInterfaceDevice device, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _melsec.DeInitialize();
            var closedState = device.IsSimulation ? EN_COMM_STATE.Simulation : EN_COMM_STATE.Offline;
            device.SetExternalCommunicationState(closedState, device.IsSimulation ? "[SIMULATION] CLOSE" : "CLOSE", device.IsSimulation ? "[SIMULATION] CLOSED" : "CLOSED", "");
        }

        private async Task<string> ExecuteDeviceFunction(CInterfaceDevice device, string function, CancellationToken cancellationToken, string? logText = null, bool suppressLog = false)
        {
            try
            {
                var response = await device.ExecuteFunction(function, cancellationToken);
                if (!suppressLog)
                {
                    WriteCommandLog(device, function, response, logText);
                }

                return response;
            }
            catch (Exception ex)when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                if (ex is CVisionLineCountExceededException countError)
                {
                    try { _visionCountLimitAlarm?.Invoke(countError); }
                    catch (Exception alarmError) { Trace.TraceError($"Vision count alarm registration failed: {alarmError}"); }
                }
                if (!suppressLog)
                {
                    WriteErrorLog(device, function, ex.Message, logText);
                }

                throw;
            }
        }

        private CInterfaceDevice GetDeviceOrThrow(EN_EQP_MODULE module, int number)
        {
            return _devices.TryGetValue(CreateDeviceKey(module, number), out var device) ? device : throw new KeyNotFoundException($"Interface device was not registered: {FormatDeviceName(module, number)}");
        }

        private CInterfaceDevice GetDeviceByNickNameOrThrow(string nickName)
        {
            return TryGetDeviceByNickName(nickName, out var device, throwIfAmbiguous: true) && device is not null ? device : throw new KeyNotFoundException($"Interface device was not registered: {nickName}");
        }

        private bool TryGetDeviceByNickName(string nickName, out CInterfaceDevice? device, bool throwIfAmbiguous = false)
        {
            var normalized = NormalizeNickName(nickName);
            bool ShouldIncludeItem(global::Drilling.Common.Interface.CInterfaceDevice item)
            {
                return NormalizeNickName(item.Data.NickName).Equals(normalized, StringComparison.OrdinalIgnoreCase);
            }

            var matches = _devices.Values.Where(ShouldIncludeItem).ToArray();
            if (matches.Length == 0)
            {
                device = null;
                return false;
            }

            string SelectItem(global::Drilling.Common.Interface.CInterfaceDevice item)
            {
                return FormatDeviceName(item.Data);
            }

            if (matches.Length > 1 && throwIfAmbiguous)
            {
                var candidates = string.Join(", ", matches.Select(SelectItem));
                throw new InvalidOperationException($"Interface NICKNAME is duplicated: {nickName}. Use DEVICE + NUMBER instead. Candidates: {candidates}");
            }

            device = matches[0];
            return true;
        }

        private void WriteConnectionLog(string action, CInterfaceDevice device, EN_COMM_STATE beforeState)
        {
            if (_logManager is null)
            {
                return;
            }

            var status = device.GetCommunicationStatus();
            var afterState = FormatConnectionState(status.ConnectionState);
            if (!string.IsNullOrWhiteSpace(status.LastError))
            {
                afterState = $"{afterState} / {status.LastError}";
            }

            _logManager.WriteInterfaceConnection(device.Data.Device, action, device.Data.NickName, FormatConnectionState(beforeState), afterState);
        }

        private void WriteCommandLog(CInterfaceDevice device, string command, string response, string? logText = null)
        {
            if (_logManager is null)
            {
                return;
            }

            var status = device.GetCommunicationStatus();
            var sentCommand = !string.IsNullOrWhiteSpace(logText) ? logText : string.IsNullOrWhiteSpace(status.LastSent) ? command : status.LastSent;
            var loggedResponse = !string.IsNullOrWhiteSpace(logText) ? response.Replace(command, logText, StringComparison.Ordinal) : response;
            if (!string.IsNullOrWhiteSpace(status.LastError))
            {
                _logManager.WriteInterfaceError(device.Data.Device, device.Data.NickName, sentCommand, status.LastError);
                return;
            }

            _logManager.WriteInterfaceCommand(device.Data.Device, device.Data.NickName, sentCommand, loggedResponse, FormatConnectionState(status.ConnectionState));
        }

        private void WriteErrorLog(CInterfaceDevice device, string command, string detail, string? logText = null)
        {
            var status = device.GetCommunicationStatus();
            var sentCommand = !string.IsNullOrWhiteSpace(logText) ? logText : string.IsNullOrWhiteSpace(status.LastSent) ? command : status.LastSent;
            _logManager?.WriteInterfaceError(device.Data.Device, device.Data.NickName, sentCommand, detail);
        }

        private async Task<string> OnDeviceMessageReceived(CInterfaceDevice device, ST_COMM_RECEIVED_MESSAGE message, CancellationToken cancellationToken)
        {
            var receivedMessage = new ST_INTERFACE_RECEIVED_MESSAGE(message.ReceivedAt, device.Data.Device, device.Data.Number, device.Data.NickName, message.RemoteEndPoint, message.Message);
            _logManager?.WriteInterfaceCommand(device.Data.Device, device.Data.NickName, $"RECV:{message.RemoteEndPoint}", message.Message, "SOCKET_SERVER");
            var handler = MessageReceived;
            if (handler is null)
            {
                return "ACK";
            }

            var response = "ACK";
            foreach (var callback in handler.GetInvocationList().Cast<Func<ST_INTERFACE_RECEIVED_MESSAGE, CancellationToken, Task<string>>>())
            {
                var callbackResponse = await callback(receivedMessage, cancellationToken);
                if (!string.IsNullOrWhiteSpace(callbackResponse))
                {
                    response = callbackResponse;
                }
            }

            return string.IsNullOrWhiteSpace(response) ? "ACK" : response;
        }

        private void OnDeviceProtocolTrace(CInterfaceDevice device, ST_COMM_PROTOCOL_TRACE trace)
        {
            if (trace.Direction.Equals("PACKET", StringComparison.OrdinalIgnoreCase))
            {
                _logManager?.WriteInterfaceProtocolTrace(device.Data.Device, trace.OccurredAt, trace.Message);
            }

            ProtocolTrace?.Invoke(new ST_INTERFACE_PROTOCOL_TRACE(trace.OccurredAt, device.Data.Device, device.Data.Number, device.Data.NickName, trace.Direction, trace.Message));
        }

        private void WriteTalonCommandLog(ST_INTERFACE_DATA data, string command, string response)
        {
            WriteInterfaceCommandLog(data, command, response);
        }

        private void WriteTalonErrorLog(ST_INTERFACE_DATA data, string command, string detail)
        {
            WriteInterfaceErrorLog(data, command, detail);
        }

        private void WriteInterfaceCommandLog(ST_INTERFACE_DATA data, string command, string response)
        {
            _logManager?.WriteInterfaceCommand(data.Device, data.NickName, command, response, data.IsSimulation ? "SIMULATION" : "");
        }

        private void WriteInterfaceErrorLog(ST_INTERFACE_DATA data, string command, string detail)
        {
            _logManager?.WriteInterfaceError(data.Device, data.NickName, command, detail);
        }

        private static string FormatConnectionState(EN_COMM_STATE state)
        {
            return state switch
            {
                EN_COMM_STATE.Online => "ONLINE",
                EN_COMM_STATE.Simulation => "SIMULATION",
                _ => "OFFLINE"
            };
        }

        private static string NormalizeNickName(string nickName)
        {
            return nickName.Trim().ToUpperInvariant();
        }

        private static string CreateDeviceKey(EN_EQP_MODULE module, int number)
        {
            return $"{module}:{number}";
        }

        private static string FormatDeviceName(ST_INTERFACE_DATA data)
        {
            return $"{data.Device}[{data.Number}]/{data.NickName}";
        }

        private static string FormatDeviceName(EN_EQP_MODULE module, int number)
        {
            return $"{module}[{number}]";
        }

        /// <summary>Seed virtual scanner states once, before startup snapshot readers begin.</summary>
        internal int InitializeSimulatedScannerOutputs(IReadOnlyList<ST_SYSTEM_PARAMETER> settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            var initialized = 0;
            for (var head = 1; head <= 8; head++)
            {
                var key = $"H{head:00}_AUTOMATION_NO";
                var mapping = settings.FirstOrDefault(setting => key.Equals(setting.Key, StringComparison.OrdinalIgnoreCase)
                    || key.Equals(setting.Name, StringComparison.OrdinalIgnoreCase));
                var automationNo = head <= 4 ? 0 : 1;
                if (mapping is not null && !int.TryParse(mapping.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out automationNo))
                    continue;
                var laserNo = head - 1;
                if (!_devices.TryGetValue(CreateDeviceKey(EN_EQP_MODULE.Automation1, automationNo), out var automation)
                    || !automation.IsSimulation
                    || !_devices.TryGetValue(CreateDeviceKey(EN_EQP_MODULE.TalonLaser, laserNo), out var laser)
                    || !laser.IsSimulation)
                    continue;
                // No device command or Galvo emission is issued. Later operator OFF remains authoritative.
                if (_talonStatuses.TryAdd(laserNo, ST_TALON_STATUS.Empty with { LaserOn = true, ShutterOpen = true, GateOpen = true }))
                    initialized++;
            }
            return initialized;
        }

        public ST_LASER_STATUS GetLaserStatus(CancellationToken cancellationToken = default)
        {
            var interfaceData = GetTalonInterfaceData();
            return GetLaserStatus(interfaceData?.Number ?? 0, cancellationToken);
        }

        public ST_LASER_STATUS GetLaserStatus(int number, CancellationToken cancellationToken = default)
        {
            var interfaceData = GetTalonInterfaceData(number);
            if (!IsInterfaceSimulation(interfaceData))
            {
                var liveStatus = RefreshTalonLaserStatus(number, cancellationToken);
                return new ST_LASER_STATUS(liveStatus.LaserOn, liveStatus.ShutterOpen, liveStatus.GateOpen, liveStatus.OutputPower);
            }

            var simulationStatus = GetTalonStatus(number);
            var outputPower = simulationStatus.OutputPower > 0.0 ? simulationStatus.OutputPower : simulationStatus.LaserOn ? 8.5 : 0.0;
            return new ST_LASER_STATUS(simulationStatus.LaserOn, simulationStatus.ShutterOpen, simulationStatus.GateOpen, outputPower);
        }

        public async Task SetLaser(int headNo, bool enabled, CancellationToken cancellationToken = default)
        {
            var interfaceData = GetTalonInterfaceData();
            await SetLaser(interfaceData?.Number ?? 0, headNo, enabled, cancellationToken);
        }

        public async Task SetLaser(int number, int headNo, bool enabled, CancellationToken cancellationToken = default)
        {
            var interfaceData = GetTalonInterfaceData(number);
            if (interfaceData is not null && IsInterfaceSimulation(interfaceData))
            {
                SetSimulLaserHead(number, headNo, enabled);
                var status = CTalonLaser.Apply(EN_TALON_COMMAND.SetLaserOnOff, enabled ? 1.0 : 0.0, "", GetTalonStatus(number), simulation: true);
                SetTalonStatus(number, status);
                return;
            }

            var result = await ExecuteTalonLaserCommand(number, EN_TALON_COMMAND.SetLaserOnOff, enabled ? 1.0 : 0.0, cancellationToken);
            if (result.IsSuccess)
            {
                SetSimulLaserHead(number, headNo, enabled);
            }
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecuteTalonLaserCommand(EN_TALON_COMMAND command, double parameter = 0.0, CancellationToken cancellationToken = default)
        {
            var interfaceData = GetTalonInterfaceData();
            return interfaceData is null ? new ST_DEVICE_COMMAND_RESULT(false, "Talon interface is not registered.") : await ExecuteTalonLaserCommand(interfaceData.Number, command, parameter, cancellationToken);
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecuteTalonLaserCommand(int number, EN_TALON_COMMAND command, double parameter = 0.0, CancellationToken cancellationToken = default, bool suppressCommandLog = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commandText = CTalonLaser.Build(command, parameter);
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"Talon command is not defined: {command}");
            }

            var interfaceData = GetTalonInterfaceData(number);
            if (interfaceData is null)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"Talon interface is not registered: {FormatDeviceName(EN_EQP_MODULE.TalonLaser, number)}");
            }

            if (IsInterfaceSimulation(interfaceData))
            {
                var status = CTalonLaser.Apply(command, parameter, "", GetTalonStatus(number), simulation: true);
                SetTalonStatus(number, status);
                if (!suppressCommandLog)
                {
                    WriteTalonCommandLog(interfaceData, commandText, "SIMULATION OK");
                }

                return new ST_DEVICE_COMMAND_RESULT(true, $"SIM:TALON:{number}:{command}:OK");
            }

            if (!IsConnect(interfaceData.Device, interfaceData.Number))
            {
                await AutoReconnectDevice(interfaceData.Device, interfaceData.Number, cancellationToken);
            }

            if (!IsConnect(interfaceData.Device, interfaceData.Number))
            {
                SetTalonStatus(number, GetTalonStatus(number)with { LastError = EN_TALON_ERROR.Timeout });
                if (!suppressCommandLog)
                {
                    WriteTalonErrorLog(interfaceData, commandText, "OFFLINE");
                }

                return new ST_DEVICE_COMMAND_RESULT(false, $"Talon interface is offline: {FormatDeviceName(interfaceData)}");
            }

            var response = suppressCommandLog ? await ExecuteFunctionNoLog(interfaceData.Device, interfaceData.Number, commandText, cancellationToken) : await ExecuteFunction(interfaceData.Device, interfaceData.Number, commandText, cancellationToken);
            var expectsResponse = CTalonLaser.ExpectsResponse(command);
            cancellationToken.ThrowIfCancellationRequested();
            if (expectsResponse && !CTalonLaser.IsValidResponse(response))
            {
                SetTalonStatus(number, GetTalonStatus(number)with { LastError = EN_TALON_ERROR.InvalidResponse });
                if (!suppressCommandLog)
                {
                    WriteTalonErrorLog(interfaceData, commandText, $"INVALID RESPONSE / {response}");
                }

                return new ST_DEVICE_COMMAND_RESULT(false, $"Talon invalid response. Command={commandText}, Response={response}");
            }

            var updatedStatus = CTalonLaser.Apply(command, parameter, response, GetTalonStatus(number), simulation: false);
            SetTalonStatus(number, updatedStatus);
            return new ST_DEVICE_COMMAND_RESULT(true, expectsResponse ? response : $"Talon command sent: {commandText}");
        }

        public ST_TALON_STATUS RefreshTalonLaserStatus(CancellationToken cancellationToken = default)
        {
            var interfaceData = GetTalonInterfaceData();
            return interfaceData is null ? ST_TALON_STATUS.Empty : RefreshTalonLaserStatus(interfaceData.Number, cancellationToken);
        }

        public ST_TALON_STATUS RefreshTalonLaserStatus(int number, CancellationToken cancellationToken = default)
        {
            return RefreshTalonLaserStatus(number, cancellationToken, suppressCommandLog: false);
        }

        public ST_TALON_STATUS RefreshTalonLaserStatus(int number, CancellationToken cancellationToken, bool suppressCommandLog) => RefreshTalonLaserStatusAsync(number, cancellationToken, suppressCommandLog).GetAwaiter().GetResult();
        public async Task<ST_TALON_STATUS> RefreshTalonLaserStatusAsync(int number, CancellationToken cancellationToken, bool suppressCommandLog)
        {
            var interfaceData = GetTalonInterfaceData(number);
            if (IsInterfaceSimulation(interfaceData))
            {
                return GetTalonStatus(number);
            }

            EN_TALON_COMMAND[] commands = [
                EN_TALON_COMMAND.GetDiodeCurrent, 
                EN_TALON_COMMAND.GetDiodeTemp, 
                EN_TALON_COMMAND.GetGateOpenClose, 
                EN_TALON_COMMAND.GetShutterOpenClose, 
                EN_TALON_COMMAND.GetExtGateEnableDisable, 
                EN_TALON_COMMAND.GetOutputPower, 
                EN_TALON_COMMAND.GetTowerTemp, 
                EN_TALON_COMMAND.GetQsw, 
                EN_TALON_COMMAND.GetThgSpot, 
                EN_TALON_COMMAND.GetThgHour, 
                EN_TALON_COMMAND.RequestStatusString, 
                EN_TALON_COMMAND.GetQMode
            ];
            foreach (var command in commands)
            {
                var result = await ExecuteTalonLaserCommand(number, command, cancellationToken: cancellationToken, suppressCommandLog: suppressCommandLog).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    return GetTalonStatus(number);
                }
            }

            return GetTalonStatus(number);
        }

        public ST_ATTENUATOR_STATUS GetAttenuatorStatus(CancellationToken cancellationToken = default)
        {
            var interfaceData = GetAttenuatorInterfaceData();
            return GetAttenuatorStatus(interfaceData?.Number ?? 0, cancellationToken);
        }

        public ST_ATTENUATOR_STATUS GetAttenuatorStatus(int number, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = AttenuatorSnapshots.GetSnapshot(number);
            if (snapshot.IsValid && snapshot.IsFresh(TimeSpan.FromSeconds(2)))
                return snapshot.Value!;
            return new ST_ATTENUATOR_STATUS(double.NaN, double.NaN, snapshot.State, CommOk: false, LastError: EN_CONEX_AGP_ERROR.Timeout, UpdatedAt: snapshot.UpdatedAt == DateTimeOffset.MinValue ? null : snapshot.UpdatedAt);
        }

        public ST_ATTENUATOR_STATUS GetCachedAttenuatorStatus(int number)
        {
            return GetAttenuatorStatusValue(number);
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecuteAttenuatorCommand(EN_ATTENUATOR_COMMAND command, double parameter = 0.0, CancellationToken cancellationToken = default)
        {
            var interfaceData = GetAttenuatorInterfaceData();
            return interfaceData is null ? new ST_DEVICE_COMMAND_RESULT(false, "CONEX_AGP interface is not registered.") : await ExecuteAttenuatorCommand(interfaceData.Number, command, parameter, cancellationToken);
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecuteAttenuatorCommand(int number, EN_ATTENUATOR_COMMAND command, double parameter = 0.0, CancellationToken cancellationToken = default, bool suppressCommandLog = false)
        {
            var changesMotion = command is EN_ATTENUATOR_COMMAND.MoveAbs or EN_ATTENUATOR_COMMAND.MoveRel or EN_ATTENUATOR_COMMAND.Home or EN_ATTENUATOR_COMMAND.Stop or EN_ATTENUATOR_COMMAND.ResetController;
            if (changesMotion)
                AttenuatorSnapshots.Invalidate(number, "LOADING", "ATT 명령 후 새 상태 수집을 기다리고 있습니다.");
            try
            {
                if (command is EN_ATTENUATOR_COMMAND.MoveAbs or EN_ATTENUATOR_COMMAND.MoveRel)
                {
                    return await ExecuteAttenuatorMoveWithinLimits(number, command, parameter, cancellationToken, suppressCommandLog);
                }

                return await ExecuteAttenuatorCommandCore(number, command, parameter, cancellationToken, suppressCommandLog);
            }
            finally
            {
                // Discard an older READY sample, including a frame that started while the command was in flight.
                if (changesMotion)
                    AttenuatorSnapshots.Invalidate(number, "LOADING", "ATT 명령 후 새 상태 수집을 기다리고 있습니다.");
            }
        }

        private async Task<ST_DEVICE_COMMAND_RESULT> ExecuteAttenuatorCommandCore(int number, EN_ATTENUATOR_COMMAND command, double parameter, CancellationToken cancellationToken, bool suppressCommandLog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command == EN_ATTENUATOR_COMMAND.Refresh)
            {
                var status = RefreshAttenuatorStatus(number, cancellationToken, suppressCommandLog);
                return new ST_DEVICE_COMMAND_RESULT(true, $"CONEX_AGP refreshed. Position {status.CurrentPosition:F3} DEG.");
            }

            string commandText;
            try
            {
                commandText = CConex_AGP.Build(command, parameter);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"CONEX_AGP command parameter is invalid. Command={command}, Parameter={parameter.ToString(CultureInfo.InvariantCulture)}, Reason={ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(commandText))
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"CONEX_AGP command is not defined: {command}");
            }

            var interfaceData = GetAttenuatorInterfaceData(number);
            if (interfaceData is null)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"CONEX_AGP interface is not registered: {FormatDeviceName(EN_EQP_MODULE.Attenuator, number)}");
            }

            if (IsInterfaceSimulation(interfaceData))
            {
                var status = CConex_AGP.Apply(command, parameter, "", GetAttenuatorStatusValue(number), simulation: true);
                SetAttenuatorStatus(number, status);
                if (!suppressCommandLog)
                {
                    WriteInterfaceCommandLog(interfaceData, commandText, "SIMULATION OK");
                }

                return new ST_DEVICE_COMMAND_RESULT(true, $"SIM:CONEX_AGP:{number}:{command}:OK");
            }

            if (!IsConnect(interfaceData.Device, interfaceData.Number) && command is not (EN_ATTENUATOR_COMMAND.PollState or EN_ATTENUATOR_COMMAND.PollCurrentPosition or EN_ATTENUATOR_COMMAND.PollTargetPosition))
            {
                await AutoReconnectDevice(interfaceData.Device, interfaceData.Number, cancellationToken);
            }

            if (!IsConnect(interfaceData.Device, interfaceData.Number))
            {
                SetAttenuatorStatus(number, GetAttenuatorStatusValue(number)with { CommOk = false, LastError = EN_CONEX_AGP_ERROR.Timeout, UpdatedAt = DateTimeOffset.Now });
                if (!suppressCommandLog)
                {
                    WriteInterfaceErrorLog(interfaceData, commandText, "OFFLINE");
                }

                return new ST_DEVICE_COMMAND_RESULT(false, $"CONEX_AGP interface is offline: {FormatDeviceName(interfaceData)}");
            }

            if (IsAttenuatorSelectingCommand(command))
            {
            //var currentStatus = GetAttenuatorStatusValue(number);
            //var validationError = ValidateAttenuatorCommand(command, parameter, currentStatus);
            //if (!string.IsNullOrWhiteSpace(validationError))
            //{
            //    WriteInterfaceErrorLog(interfaceData, commandText, validationError);
            //    return new ST_DEVICE_COMMAND_RESULT(false, $"CONEX_AGP command blocked. Command={commandText}, Reason={validationError}");
            //}
            }

            var response = suppressCommandLog ? await ExecuteFunctionNoLog(interfaceData.Device, interfaceData.Number, commandText, cancellationToken) : await ExecuteFunction(interfaceData.Device, interfaceData.Number, commandText, cancellationToken);
            var updatedStatus = CConex_AGP.Apply(command, parameter, response, GetAttenuatorStatusValue(number), simulation: false);
            SetAttenuatorStatus(number, updatedStatus);
            return CConex_AGP.IsSuccessResponse(response) ? new ST_DEVICE_COMMAND_RESULT(true, response) : new ST_DEVICE_COMMAND_RESULT(false, $"CONEX_AGP command failed. Command={commandText}, Response={response}");
        }

        public ST_ATTENUATOR_STATUS RefreshAttenuatorStatus(CancellationToken cancellationToken = default)
        {
            var interfaceData = GetAttenuatorInterfaceData();
            return interfaceData is null ? CreateDefaultAttenuatorStatus() : RefreshAttenuatorStatus(interfaceData.Number, cancellationToken);
        }

        public ST_ATTENUATOR_STATUS RefreshAttenuatorStatus(int number, CancellationToken cancellationToken = default)
        {
            return RefreshAttenuatorStatus(number, cancellationToken, suppressCommandLog: false);
        }

        public ST_ATTENUATOR_STATUS RefreshAttenuatorStatus(int number, CancellationToken cancellationToken, bool suppressCommandLog)
        {
            var interfaceData = GetAttenuatorInterfaceData(number);
            if (IsInterfaceSimulation(interfaceData))
            {
                return GetAttenuatorStatusValue(number);
            }

            EN_ATTENUATOR_COMMAND[] requiredCommands = [EN_ATTENUATOR_COMMAND.PollState, EN_ATTENUATOR_COMMAND.PollCurrentPosition];
            foreach (var command in requiredCommands)
            {
                var result = ExecuteAttenuatorCommand(number, command, cancellationToken: cancellationToken, suppressCommandLog: suppressCommandLog).GetAwaiter().GetResult();
                if (!result.IsSuccess)
                {
                    return GetAttenuatorStatusValue(number);
                }
            }

            var status = GetAttenuatorStatusValue(number);
            if (status.CommandState is "DISABLE" or "READY" or "HOMING" or "MOVING")
            {
                var targetResult = ExecuteAttenuatorCommand(number, EN_ATTENUATOR_COMMAND.PollTargetPosition, cancellationToken: cancellationToken, suppressCommandLog: suppressCommandLog).GetAwaiter().GetResult();
                if (!targetResult.IsSuccess)
                {
                    return GetAttenuatorStatusValue(number);
                }
            }

            return GetAttenuatorStatusValue(number);
        }

        private static bool IsAttenuatorSelectingCommand(EN_ATTENUATOR_COMMAND command)
        {
            return command is EN_ATTENUATOR_COMMAND.MoveAbs or EN_ATTENUATOR_COMMAND.MoveRel or EN_ATTENUATOR_COMMAND.Home or EN_ATTENUATOR_COMMAND.Stop or EN_ATTENUATOR_COMMAND.ResetController or EN_ATTENUATOR_COMMAND.SetHomeType or EN_ATTENUATOR_COMMAND.SetNegativeSoftwareLimit or EN_ATTENUATOR_COMMAND.SetPositiveSoftwareLimit or EN_ATTENUATOR_COMMAND.SetIGain or EN_ATTENUATOR_COMMAND.SetPGain or EN_ATTENUATOR_COMMAND.SetInterpolationFactor;
        }

        private static string ValidateAttenuatorCommand(EN_ATTENUATOR_COMMAND command, double parameter, ST_ATTENUATOR_STATUS status)
        {
            var state = status.CommandState.Trim().ToUpperInvariant();
            if (!status.CommOk || !status.UpdatedAt.HasValue || string.IsNullOrWhiteSpace(state) || state.StartsWith("STATE_", StringComparison.Ordinal))
            {
                return $"Controller state is unavailable: {status.CommandState}";
            }

            //if (command == EN_ATTENUATOR_COMMAND.Home && state != "NOT_REFERENCED")
            //{
            //    return $"OR is allowed only in NOT_REFERENCED state. Current={state}";
            //}
            if (command is EN_ATTENUATOR_COMMAND.MoveAbs or EN_ATTENUATOR_COMMAND.MoveRel)
            {
                if (state != "READY")
                {
                    return $"PA/PR is allowed only in READY state. Current={state}";
                }

                var target = command == EN_ATTENUATOR_COMMAND.MoveAbs ? parameter : status.TargetPosition + parameter;
                if (status.NegativeSoftwareLimit.HasValue && target < status.NegativeSoftwareLimit.Value)
                {
                    return $"Target {target.ToString(CultureInfo.InvariantCulture)} is below SL {status.NegativeSoftwareLimit.Value.ToString(CultureInfo.InvariantCulture)}.";
                }

                if (status.PositiveSoftwareLimit.HasValue && target > status.PositiveSoftwareLimit.Value)
                {
                    return $"Target {target.ToString(CultureInfo.InvariantCulture)} is above SR {status.PositiveSoftwareLimit.Value.ToString(CultureInfo.InvariantCulture)}.";
                }
            }

            if (command is EN_ATTENUATOR_COMMAND.SetHomeType or EN_ATTENUATOR_COMMAND.SetIGain or EN_ATTENUATOR_COMMAND.SetPGain or EN_ATTENUATOR_COMMAND.SetInterpolationFactor && state is not ("NOT_REFERENCED" or "CONFIGURATION" or "DISABLE"))
            {
                return $"HT/KI/KP/IF cannot be set in {state} state.";
            }

            if (command == EN_ATTENUATOR_COMMAND.SetNegativeSoftwareLimit)
            {
                if (state is not ("CONFIGURATION" or "DISABLE" or "READY"))
                {
                    return $"SL cannot be set in {state} state.";
                }

                if (parameter > status.TargetPosition || status.PositiveSoftwareLimit.HasValue && parameter > status.PositiveSoftwareLimit.Value)
                {
                    return "SL must be less than or equal to the current target and SR.";
                }
            }

            if (command == EN_ATTENUATOR_COMMAND.SetPositiveSoftwareLimit)
            {
                if (state is not ("CONFIGURATION" or "DISABLE" or "READY"))
                {
                    return $"SR cannot be set in {state} state.";
                }

                if (parameter < status.TargetPosition || status.NegativeSoftwareLimit.HasValue && parameter < status.NegativeSoftwareLimit.Value)
                {
                    return "SR must be greater than or equal to the current target and SL.";
                }
            }

            if (command == EN_ATTENUATOR_COMMAND.Stop && state is not ("HOMING" or "MOVING"))
            {
                return $"ST is allowed only while HOMING or MOVING. Current={state}";
            }

            return "";
        }

        public ST_BET_STATUS GetBETStatus(CancellationToken cancellationToken = default)
        {
            var interfaceData = GetBETInterfaceData();
            return GetBETStatus(interfaceData?.Number ?? 0, cancellationToken);
        }

        public ST_BET_STATUS GetBETStatus(int number, CancellationToken cancellationToken = default)
        {
            if (!IsInterfaceSimulation(GetBETInterfaceData(number)))
            {
                RefreshBETStatus(number, cancellationToken);
            }

            return GetBETStatusValue(number);
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecuteBETCommand(EN_BET_COMMAND command, double parameter1 = 0.0, double parameter2 = 0.0, CancellationToken cancellationToken = default)
        {
            var interfaceData = GetBETInterfaceData();
            return interfaceData is null ? new ST_DEVICE_COMMAND_RESULT(false, "BeamExpander interface is not registered.") : await ExecuteBETCommand(interfaceData.Number, command, parameter1, parameter2, cancellationToken);
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecuteBETCommand(
            int number, 
            EN_BET_COMMAND command, 
            double parameter1 = 0.0, 
            double parameter2 = 0.0, 
            CancellationToken cancellationToken = default, 
            bool suppressCommandLog = false
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command == EN_BET_COMMAND.Refresh)
            {
                var status = RefreshBETStatus(number, cancellationToken, suppressCommandLog);
                return new ST_DEVICE_COMMAND_RESULT(true, $"BET refreshed. MAG {status.CurrentMagnification:F3}, DIV {status.CurrentDivergence:F3}.");
            }

            if (command == EN_BET_COMMAND.MoveTable)
            {
                var table = await LoadBETData(cancellationToken);
                var index = (int)Math.Round(parameter1);
                bool MatchesItem(global::Drilling.Common.Interface.ST_BET_TABLE_DATA item)
                {
                    return item.Index == index;
                }

                var row = table.FirstOrDefault(MatchesItem);
                if (row is null)
                {
                    return new ST_DEVICE_COMMAND_RESULT(false, $"BET table row was not found: {index}");
                }

                return await ExecuteBETCommand(number, EN_BET_COMMAND.MoveManual, row.Magnification, row.Divergence, cancellationToken);
            }

            var commandText = CBeamExpander.Build(command, parameter1, parameter2);
            var logCommandText = CBeamExpander.BuildLogText(command, parameter1, parameter2);
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"BeamExpander command is not defined: {command}");
            }

            var interfaceData = GetBETInterfaceData(number);
            if (interfaceData is null)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"BeamExpander interface is not registered: {FormatDeviceName(EN_EQP_MODULE.Bet, number)}");
            }

            if (IsInterfaceSimulation(interfaceData))
            {
                var status = CBeamExpander.Apply(command, parameter1, parameter2, "", GetBETStatusValue(number), simulation: true);
                SetBETStatus(number, status);
                if (!suppressCommandLog)
                {
                    WriteInterfaceCommandLog(interfaceData, logCommandText, "SIMULATION OK");
                }

                return new ST_DEVICE_COMMAND_RESULT(true, $"SIM:BEAM_EXPENDER:{number}:{command}:OK");
            }

            if (!IsConnect(interfaceData.Device, interfaceData.Number))
            {
                await AutoReconnectDevice(interfaceData.Device, interfaceData.Number, cancellationToken);
            }

            if (!IsConnect(interfaceData.Device, interfaceData.Number))
            {
                SetBETStatus(number, GetBETStatusValue(number)with { CommOk = false, LastError = EN_BET_ERROR.Timeout, UpdatedAt = DateTimeOffset.Now });
                if (!suppressCommandLog)
                {
                    WriteInterfaceErrorLog(interfaceData, logCommandText, "OFFLINE");
                }

                return new ST_DEVICE_COMMAND_RESULT(false, $"BeamExpander interface is offline: {FormatDeviceName(interfaceData)}");
            }

            var response = suppressCommandLog ? await ExecuteFunctionNoLog(interfaceData.Device, interfaceData.Number, commandText, cancellationToken) : await ExecuteFunction(interfaceData.Device, interfaceData.Number, commandText, cancellationToken);
            var updatedStatus = CBeamExpander.Apply(command, parameter1, parameter2, response, GetBETStatusValue(number), simulation: false);
            cancellationToken.ThrowIfCancellationRequested();
            SetBETStatus(number, updatedStatus);
            return CBeamExpander.IsSuccessResponse(response) ? new ST_DEVICE_COMMAND_RESULT(true, response) : new ST_DEVICE_COMMAND_RESULT(false, $"BeamExpander command failed. Command={logCommandText}, Response={response}");
        }

        public ST_BET_STATUS RefreshBETStatus(CancellationToken cancellationToken = default)
        {
            var interfaceData = GetBETInterfaceData();
            return interfaceData is null ? CreateDefaultBETStatus() : RefreshBETStatus(interfaceData.Number, cancellationToken);
        }

        public ST_BET_STATUS RefreshBETStatus(int number, CancellationToken cancellationToken = default)
        {
            return RefreshBETStatus(number, cancellationToken, suppressCommandLog: false);
        }

        public ST_BET_STATUS RefreshBETStatus(int number, CancellationToken cancellationToken, bool suppressCommandLog) => RefreshBETStatusAsync(number, cancellationToken, suppressCommandLog).GetAwaiter().GetResult();
        public async Task<ST_BET_STATUS> RefreshBETStatusAsync(int number, CancellationToken cancellationToken, bool suppressCommandLog)
        {
            var interfaceData = GetBETInterfaceData(number);
            if (IsInterfaceSimulation(interfaceData))
            {
                return GetBETStatusValue(number);
            }

            EN_BET_COMMAND[] commands = [EN_BET_COMMAND.PollMagnificationPosition, EN_BET_COMMAND.PollDivergencePosition];
            foreach (var command in commands)
            {
                var result = await ExecuteBETCommand(number, command, cancellationToken: cancellationToken, suppressCommandLog: suppressCommandLog).ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    return GetBETStatusValue(number);
                }
            }

            return GetBETStatusValue(number);
        }

        public Task<IReadOnlyList<ST_BET_TABLE_DATA>> LoadBETData(CancellationToken cancellationToken = default)
        {
            return _betFile is null ? Task.FromResult<IReadOnlyList<ST_BET_TABLE_DATA>>(CreateDefaultBETData()) : _betFile.Load(cancellationToken);
        }

        public Task SaveBETData(IReadOnlyList<ST_BET_TABLE_DATA> table, CancellationToken cancellationToken = default)
        {
            return _betFile is null ? Task.CompletedTask : _betFile.Save(table, cancellationToken);
        }

        public ST_POWER_METER_STATUS GetPowerMeterStatus(CancellationToken cancellationToken = default)
        {
            var interfaceData = GetPowerMeterInterfaceData();
            return GetPowerMeterStatus(interfaceData?.Number ?? 0, cancellationToken);
        }

        public ST_POWER_METER_STATUS GetPowerMeterStatus(int number, CancellationToken cancellationToken = default)
        {
            return GetPowerMeterStatusValue(number);
        }

        public void SetPowerMeterSimulationAttenuator(int powerMeterNumber, int attenuatorNumber)
        {
            if (powerMeterNumber < 0 || attenuatorNumber < 0)
            {
                return;
            }

            _powerMeterSimulationAttenuators[powerMeterNumber] = attenuatorNumber;
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecutePowerMeterCommand(EN_POWER_METER_COMMAND command, double parameter = 0.0, CancellationToken cancellationToken = default)
        {
            var interfaceData = GetPowerMeterInterfaceData();
            return interfaceData is null ? new ST_DEVICE_COMMAND_RESULT(false, "PowerMeter interface is not registered.") : await ExecutePowerMeterCommand(interfaceData.Number, command, parameter, cancellationToken);
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecutePowerMeterCommand(int number, EN_POWER_METER_COMMAND command, double parameter = 0.0, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command == EN_POWER_METER_COMMAND.Refresh)
            {
                var status = GetPowerMeterStatus(number, cancellationToken);
                return new ST_DEVICE_COMMAND_RESULT(true, $"PowerMeter refreshed. Power {status.MeasuredPower.ToString("F4", CultureInfo.InvariantCulture)} {status.Unit}.");
            }

            var commandText = CPowerMeter.Build(command, parameter);
            if (string.IsNullOrWhiteSpace(commandText))
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"PowerMeter command is not defined: {command}");
            }

            var interfaceData = GetPowerMeterInterfaceData(number);
            if (interfaceData is null)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"PowerMeter interface is not registered: {FormatDeviceName(EN_EQP_MODULE.PowerMeter, number)}");
            }

            if (IsInterfaceSimulation(interfaceData))
            {
                var simulationPower = command == EN_POWER_METER_COMMAND.ReadPower ? CreatePowerMeterSimulationPower(number) : null;
                var status = CPowerMeter.Apply(command, parameter, "", GetPowerMeterStatusValue(number), simulation: true, simulationPower: simulationPower);
                SetPowerMeterStatus(number, status);
                WriteInterfaceCommandLog(interfaceData, commandText, "SIMULATION OK");
                return new ST_DEVICE_COMMAND_RESULT(true, $"SIM:POWER_METER:{number}:{command}:OK");
            }

            if (!IsConnect(interfaceData.Device, interfaceData.Number))
            {
                await AutoReconnectDevice(interfaceData.Device, interfaceData.Number, cancellationToken);
            }

            if (!IsConnect(interfaceData.Device, interfaceData.Number))
            {
                SetPowerMeterStatus(number, GetPowerMeterStatusValue(number)with { LastError = EN_POWER_METER_ERROR.Timeout, MeasuredAt = DateTimeOffset.Now });
                WriteInterfaceErrorLog(interfaceData, commandText, "OFFLINE");
                return new ST_DEVICE_COMMAND_RESULT(false, $"PowerMeter interface is offline: {FormatDeviceName(interfaceData)}");
            }

            var response = await ExecuteFunction(interfaceData.Device, interfaceData.Number, commandText, cancellationToken);
            var updatedStatus = CPowerMeter.Apply(command, parameter, response, GetPowerMeterStatusValue(number), simulation: false);
            SetPowerMeterStatus(number, updatedStatus);
            return CPowerMeter.IsSuccessResponse(response) && updatedStatus.LastError == EN_POWER_METER_ERROR.Ok ? new ST_DEVICE_COMMAND_RESULT(true, response) : new ST_DEVICE_COMMAND_RESULT(false, $"PowerMeter command failed. Command={commandText}, Response={response}");
        }

        public ST_POWER_METER_STATUS RefreshPowerMeterStatus(CancellationToken cancellationToken = default)
        {
            var interfaceData = GetPowerMeterInterfaceData();
            return interfaceData is null ? ST_POWER_METER_STATUS.Empty : RefreshPowerMeterStatus(interfaceData.Number, cancellationToken);
        }

        public ST_POWER_METER_STATUS RefreshPowerMeterStatus(int number, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetPowerMeterStatusValue(number);
        }

        public ST_PICO_MOTOR_STATUS GetPicoMotorStatus(CancellationToken cancellationToken = default)
        {
            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_DATA item)
            {
                return item.Number;
            }

            var data = GetInterfaceList(EN_EQP_MODULE.PicoMotor).OrderBy(SelectNumber).FirstOrDefault();
            if (data is null)
            {
                return ST_PICO_MOTOR_STATUS.Empty with
                {
                    CommOk = false,
                    LastError = EN_PICO_MOTOR_ERROR.NotSupported,
                    UpdatedAt = DateTimeOffset.Now
                };
            }

            return GetPicoMotorStatus(data.Number, cancellationToken);
        }

        public ST_PICO_MOTOR_STATUS GetPicoMotorStatus(int number, CancellationToken cancellationToken = default)
        {
            bool MatchesItem(global::Drilling.Common.Interface.ST_INTERFACE_DATA item)
            {
                return item.Number == number;
            }

            var data = GetInterfaceList(EN_EQP_MODULE.PicoMotor).FirstOrDefault(MatchesItem);
            if (data is null)
            {
                return ST_PICO_MOTOR_STATUS.Empty with
                {
                    CommOk = false,
                    LastError = EN_PICO_MOTOR_ERROR.NotSupported,
                    UpdatedAt = DateTimeOffset.Now
                };
            }

            try
            {
                return _picoMotorService.Refresh(data.Number, IsInterfaceSimulation(data), cancellationToken).GetAwaiter().GetResult();
            }
            catch
            {
                return _picoMotorService.GetStatus(data.Number)with
                {
                    CommOk = false,
                    LastError = EN_PICO_MOTOR_ERROR.Timeout,
                    UpdatedAt = DateTimeOffset.Now
                };
            }
        }

        public ST_PICO_MOTOR_STATUS RefreshPicoMotorStatus(CancellationToken cancellationToken = default)
        {
            return GetPicoMotorStatus(cancellationToken);
        }

        public ST_PICO_MOTOR_STATUS RefreshPicoMotorStatus(int number, CancellationToken cancellationToken = default)
        {
            return GetPicoMotorStatus(number, cancellationToken);
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecutePicoMotorCommand(EN_PICO_MOTOR_COMMAND command, int motorNo = 1, double parameter = 0.0, CancellationToken cancellationToken = default)
        {
            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_DATA item)
            {
                return item.Number;
            }

            var data = GetInterfaceList(EN_EQP_MODULE.PicoMotor).OrderBy(SelectNumber).FirstOrDefault();
            if (data is null)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "PicoMotor interface is not registered.");
            }

            return await ExecutePicoMotorCommand(data.Number, command, motorNo, parameter, cancellationToken);
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecutePicoMotorCommand(int number, EN_PICO_MOTOR_COMMAND command, int motorNo = 1, double parameter = 0.0, CancellationToken cancellationToken = default)
        {
            bool MatchesItem(global::Drilling.Common.Interface.ST_INTERFACE_DATA item)
            {
                return item.Number == number;
            }

            var data = GetInterfaceList(EN_EQP_MODULE.PicoMotor).FirstOrDefault(MatchesItem);
            if (data is null)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"PicoMotor interface {number} is not registered.");
            }

            var result = await _picoMotorService.Execute(data.Number, IsInterfaceSimulation(data), command, motorNo, parameter, cancellationToken);
            var commandText = $"{command}:MOTOR={motorNo}:VALUE={parameter.ToString("0.######", CultureInfo.InvariantCulture)}";
            if (result.IsSuccess)
            {
                WriteInterfaceCommandLog(data, commandText, result.Message);
            }
            else
            {
                WriteInterfaceErrorLog(data, commandText, result.Message);
            }

            return result;
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecutePicoMotorAllMove(IReadOnlyCollection<int> motorNos, double positionMm, int count, CancellationToken cancellationToken = default)
        {
            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_DATA item)
            {
                return item.Number;
            }

            var data = GetInterfaceList(EN_EQP_MODULE.PicoMotor).OrderBy(SelectNumber).FirstOrDefault();
            if (data is null)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, "PicoMotor interface is not registered.");
            }

            return await ExecutePicoMotorAllMove(data.Number, motorNos, positionMm, count, cancellationToken);
        }

        public async Task<ST_DEVICE_COMMAND_RESULT> ExecutePicoMotorAllMove(int number, IReadOnlyCollection<int> motorNos, double positionMm, int count, CancellationToken cancellationToken = default)
        {
            bool MatchesItem(global::Drilling.Common.Interface.ST_INTERFACE_DATA item)
            {
                return item.Number == number;
            }

            var data = GetInterfaceList(EN_EQP_MODULE.PicoMotor).FirstOrDefault(MatchesItem);
            if (data is null)
            {
                return new ST_DEVICE_COMMAND_RESULT(false, $"PicoMotor interface {number} is not registered.");
            }

            var result = await _picoMotorService.ExecuteAllMove(data.Number, IsInterfaceSimulation(data), motorNos, positionMm, count, cancellationToken);
            var commandText = $"ALL_MOVE:MOTOR={string.Join(",", motorNos)}:DIST={positionMm.ToString("0.######", CultureInfo.InvariantCulture)}:COUNT={count}";
            if (result.IsSuccess)
            {
                WriteInterfaceCommandLog(data, commandText, result.Message);
            }
            else
            {
                WriteInterfaceErrorLog(data, commandText, result.Message);
            }

            return result;
        }

        private void SetSimulLaserHead(int number, int headNo, bool enabled)
        {
            var laserOnHeads = GetLaserOnHeads(number);
            if (enabled)
            {
                laserOnHeads.Add(headNo);
            }
            else
            {
                laserOnHeads.Remove(headNo);
            }
        }

        private HashSet<int> GetLaserOnHeads(int number)
        {
            if (!_laserOnHeads.TryGetValue(number, out var heads))
            {
                heads = [];
                _laserOnHeads[number] = heads;
            }

            return heads;
        }

        private ST_TALON_STATUS GetTalonStatus(int number)
        {
            return _talonStatuses.GetOrAdd(number, _ => ST_TALON_STATUS.Empty);
        }

        private void SetTalonStatus(int number, ST_TALON_STATUS status)
        {
            _talonStatuses[number] = status;
        }

        private ST_ATTENUATOR_STATUS GetAttenuatorStatusValue(int number)
        {
            lock (_attenuatorStatusLock)
            {
                if (!_attenuatorStatuses.TryGetValue(number, out var status))
                {
                    status = CreateDefaultAttenuatorStatus();
                    _attenuatorStatuses[number] = status;
                }

                return status;
            }
        }

        private void SetAttenuatorStatus(int number, ST_ATTENUATOR_STATUS status)
        {
            lock (_attenuatorStatusLock)
                _attenuatorStatuses[number] = status;
        }

        private ST_BET_STATUS GetBETStatusValue(int number)
        {
            return _betStatuses.GetOrAdd(number, _ => CreateDefaultBETStatus());
        }

        private void SetBETStatus(int number, ST_BET_STATUS status)
        {
            _betStatuses[number] = status;
        }

        private ST_POWER_METER_STATUS GetPowerMeterStatusValue(int number)
        {
            return _powerMeterStatuses.GetOrAdd(number, _ => ST_POWER_METER_STATUS.Empty);
        }

        private void SetPowerMeterStatus(int number, ST_POWER_METER_STATUS status)
        {
            _powerMeterStatuses[number] = status;
        }

        private double? CreatePowerMeterSimulationPower(int powerMeterNumber)
        {
            if (!_powerMeterSimulationAttenuators.TryGetValue(powerMeterNumber, out var attenuatorNumber))
            {
                var attenuatorData = GetAttenuatorInterfaceData();
                if (attenuatorData is null)
                {
                    return null;
                }

                attenuatorNumber = attenuatorData.Number;
            }

            if (GetAttenuatorInterfaceData(attenuatorNumber)is null)
            {
                return null;
            }

            var attenuatorStatus = GetAttenuatorStatusValue(attenuatorNumber);
            var normalizedAttenuator = NormalizePowerMeterSimulationAttenuator(attenuatorStatus.CurrentPosition) / 360.0;
            const double minimumPower = 0.05;
            const double maximumPower = 2.25;
            return Math.Round(minimumPower + ((maximumPower - minimumPower) * normalizedAttenuator), 4);
        }

        private static double NormalizePowerMeterSimulationAttenuator(double position)
        {
            if (!double.IsFinite(position))
            {
                return 0.0;
            }

            return Math.Clamp(position, 0.0, 360.0);
        }

        private void PruneDeviceStateMaps()
        {
            PruneDeviceStateMap(_laserOnHeads, EN_EQP_MODULE.TalonLaser);
            PruneDeviceStateMap(_talonStatuses, EN_EQP_MODULE.TalonLaser);
            lock (_attenuatorStatusLock)
                PruneDeviceStateMap(_attenuatorStatuses, EN_EQP_MODULE.Attenuator);
            PruneDeviceStateMap(_betStatuses, EN_EQP_MODULE.Bet);
            PruneDeviceStateMap(_powerMeterStatuses, EN_EQP_MODULE.PowerMeter);
            PruneDeviceStateMap(_powerMeterSimulationAttenuators, EN_EQP_MODULE.PowerMeter);
        }

        private void ClearDeviceStateMaps()
        {
            _laserOnHeads.Clear();
            _talonStatuses.Clear();
            lock (_attenuatorStatusLock)
                _attenuatorStatuses.Clear();
            _betStatuses.Clear();
            _powerMeterStatuses.Clear();
            _powerMeterSimulationAttenuators.Clear();
        }

        private void PruneDeviceStateMap<T>(IDictionary<int, T> statusMap, EN_EQP_MODULE module)
        {
            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.Number;
            }

            var validNumbers = GetInterfaceList(module).Select(SelectNumber).ToHashSet();
            bool ShouldIncludeItem(int number)
            {
                return !validNumbers.Contains(number);
            }

            foreach (var number in statusMap.Keys.Where(ShouldIncludeItem).ToArray())
            {
                statusMap.Remove(number);
            }
        }

        private ST_INTERFACE_DATA? GetTalonInterfaceData()
        {
            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.Number;
            }

            string SelectNickName(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.NickName;
            }

            return GetInterfaceList(EN_EQP_MODULE.TalonLaser).OrderBy(SelectNumber).ThenBy(SelectNickName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        private ST_INTERFACE_DATA? GetTalonInterfaceData(int number)
        {
            return GetInterfaceData(EN_EQP_MODULE.TalonLaser, number);
        }

        private ST_INTERFACE_DATA? GetAttenuatorInterfaceData()
        {
            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.Number;
            }

            string SelectNickName(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.NickName;
            }

            return GetInterfaceList(EN_EQP_MODULE.Attenuator).OrderBy(SelectNumber).ThenBy(SelectNickName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        private ST_INTERFACE_DATA? GetAttenuatorInterfaceData(int number)
        {
            return GetInterfaceData(EN_EQP_MODULE.Attenuator, number);
        }

        private ST_INTERFACE_DATA? GetBETInterfaceData()
        {
            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.Number;
            }

            string SelectNickName(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.NickName;
            }

            return GetInterfaceList(EN_EQP_MODULE.Bet).OrderBy(SelectNumber).ThenBy(SelectNickName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        private ST_INTERFACE_DATA? GetBETInterfaceData(int number)
        {
            return GetInterfaceData(EN_EQP_MODULE.Bet, number);
        }

        private ST_INTERFACE_DATA? GetPowerMeterInterfaceData()
        {
            int SelectNumber(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.Number;
            }

            string SelectNickName(global::Drilling.Common.Interface.ST_INTERFACE_DATA data)
            {
                return data.NickName;
            }

            return GetInterfaceList(EN_EQP_MODULE.PowerMeter).OrderBy(IsInterfaceSimulation).ThenBy(SelectNumber).ThenBy(SelectNickName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        private ST_INTERFACE_DATA? GetPowerMeterInterfaceData(int number)
        {
            return GetInterfaceData(EN_EQP_MODULE.PowerMeter, number);
        }

        private bool IsInterfaceSimulation(ST_INTERFACE_DATA? interfaceData)
        {
            return interfaceData is null || IsSimul(interfaceData.Device, interfaceData.Number);
        }

        private static ST_ATTENUATOR_STATUS CreateDefaultAttenuatorStatus()
        {
            return new ST_ATTENUATOR_STATUS(55.0, 55.0, "READY");
        }

        private static ST_BET_STATUS CreateDefaultBETStatus()
        {
            return new ST_BET_STATUS(1020.000, 1020.000, 1626.000, 1626.000, 1020.000, 1626.000, false, true, true, false);
        }

        private static IReadOnlyList<ST_BET_TABLE_DATA> CreateDefaultBETData()
        {
            return[new(0, 1020, 1626, "2times"), new(1, 2351, 1118, "3times"), new(2, 3014, 1278, "4times"), new(3, 3410, 1706, "5times"), new(4, 3673, 2267, "6times")];
        }

#endregion
#region CAttenuatorPositioningService
        /// <summary>Read-only absolute-target preflight using the same configured software limits as PA.</summary>
        public ST_DEVICE_COMMAND_RESULT ValidateAttenuatorAbsoluteMoveTarget(int number, double angle)
        {
            var settings = _attenuatorLimitSettings?.Invoke();
            double ReadLimit(string key)
            {
                var text = settings?.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
            }

            var minimum = ReadLimit("ATTENUATOR_DEGREE_MIN_SW_LIMIT");
            var maximum = ReadLimit("ATTENUATOR_DEGREE_MAX_SW_LIMIT");
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
                return Block("Attenuator Degree Min/Max SW Limit 설정을 확인해 주세요.");
            if (!double.IsFinite(angle))
                return Block("이동 각도는 유효한 숫자여야 합니다.");
            var transmitted = double.Parse(angle.ToString("F3", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (angle < minimum || angle > maximum || transmitted < minimum || transmitted > maximum)
                return Block($"목표 각도 {angle.ToString("G17", CultureInfo.InvariantCulture)} deg가 허용 범위 [{minimum.ToString(CultureInfo.InvariantCulture)}, {maximum.ToString(CultureInfo.InvariantCulture)}] deg를 벗어났습니다.");
            return new(true, "ATT target is within software limits.");
            ST_DEVICE_COMMAND_RESULT Block(string message) => new(false, $"{AttenuatorSoftwareLimitMessagePrefix}H{number + 1:00} {message}");
        }

#endregion
#region CAttenuatorSoftwareLimit
        public void ConfigureAttenuatorSoftwareLimits(Func<IReadOnlyList<ST_SYSTEM_PARAMETER>> settingsProvider)
        {
            _attenuatorLimitSettings = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        }

        private async Task<ST_DEVICE_COMMAND_RESULT> ExecuteAttenuatorMoveWithinLimits(int number, EN_ATTENUATOR_COMMAND command, double parameter, CancellationToken cancellationToken, bool suppressCommandLog)
        {
            var gate = _attenuatorMoveLocks.GetOrAdd(number, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                var settings = _attenuatorLimitSettings?.Invoke();
                double ReadLimit(string key)
                {
                    var text = settings?.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
                }

                var minimum = ReadLimit("ATTENUATOR_DEGREE_MIN_SW_LIMIT");
                var maximum = ReadLimit("ATTENUATOR_DEGREE_MAX_SW_LIMIT");
                if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
                {
                    return Block("Attenuator Degree Min/Max SW Limit 설정을 확인하세요. 유효한 숫자와 Min ≤ Max가 필요합니다.");
                }

                if (!double.IsFinite(parameter))
                {
                    return Block("이동 각도는 유효한 숫자여야 합니다.");
                }

                var origin = 0.0;
                if (command == EN_ATTENUATOR_COMMAND.MoveRel)
                {
                    var device = GetAttenuatorInterfaceData(number);
                    if (device is null)
                        return Block("Attenuator 연결 설정을 확인할 수 없어 REL 이동을 차단했습니다.");
                    if (IsInterfaceSimulation(device))
                    {
                        origin = GetAttenuatorStatusValue(number).CurrentPosition;
                    }
                    else
                    {
                        // CONEX PR increments the controller target (TH), which may differ from TP while moving.
                        var read = await ExecuteAttenuatorCommandCore(number, EN_ATTENUATOR_COMMAND.PollTargetPosition, 0, cancellationToken, true);
                        origin = read.IsSuccess ? CConex_AGP.Apply(EN_ATTENUATOR_COMMAND.PollTargetPosition, 0, read.Message, GetAttenuatorStatusValue(number)with { TargetPosition = double.NaN }, false).TargetPosition : double.NaN;
                        if (!double.IsFinite(origin))
                            return Block("장치의 목표 각도를 읽을 수 없어 REL 이동을 차단했습니다.");
                    }
                }

                // Validate both requested and transmitted (three-decimal) targets.
                var transmittedParameter = double.Parse(parameter.ToString("F3", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                foreach (var offset in new[]
                {
                    parameter,
                    transmittedParameter
                }

                )
                {
                    // Decimal addition keeps exact degree boundaries such as 0.1 + 0.2 == 0.3.
                    var target = (double)((decimal)origin + (decimal)offset);
                    if (!double.IsFinite(target) || target < minimum || target > maximum)
                    {
                        return Block($"목표 각도 {target.ToString("G17", CultureInfo.InvariantCulture)} deg가 허용 범위 " + $"[{minimum.ToString(CultureInfo.InvariantCulture)}, {maximum.ToString(CultureInfo.InvariantCulture)}] deg를 벗어나 이동을 차단했습니다.");
                    }
                }

                return await ExecuteAttenuatorCommandCore(number, command, transmittedParameter, cancellationToken, suppressCommandLog);
            }
            catch (Exception exception)when (exception is not OperationCanceledException)
            {
                return Block($"Attenuator 이동 처리에 실패했습니다. {exception.Message}");
            }
            finally
            {
                gate.Release();
            }

            ST_DEVICE_COMMAND_RESULT Block(string message) => new(false, $"{AttenuatorSoftwareLimitMessagePrefix}H{number + 1:00} {message}");
        }

#endregion
#region AlarmSnapshots
        public Task<ST_LASER_STATUS> ReadLaserStatusAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var data = GetTalonInterfaceData();
            if (data is null)
                return Task.FromResult(new ST_LASER_STATUS(false, false, false, 0));
            if (!IsSimul(data.Device, data.Number) && !IsConnect(data.Device, data.Number))
                throw new IOException("LASER가 연결되어 있지 않아 상태 수집을 보류합니다.");
            var snapshot = OpticSnapshots.GetLaser(data.Number);
            return Task.FromResult(snapshot.Value ?? throw new IOException($"LASER {snapshot.State}: {snapshot.Detail}"));
        }

        public Task<ST_BET_STATUS> ReadBetStatusAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var data = GetBETInterfaceData();
            if (data is null)
                return Task.FromResult(CreateDefaultBETStatus());
            if (!IsSimul(data.Device, data.Number) && !IsConnect(data.Device, data.Number))
                throw new IOException("BET가 연결되어 있지 않아 상태 수집을 보류합니다.");
            var snapshot = OpticSnapshots.GetBet(data.Number);
            return Task.FromResult(snapshot.Value ?? throw new IOException($"BET {snapshot.State}: {snapshot.Detail}"));
        }

#endregion
#region OpticSnapshots
        private COpticStatusSnapshotService CreateOpticSnapshots() => new(
            () => GetInterfaceList().Where(data => data.Device is EN_EQP_MODULE.TalonLaser or EN_EQP_MODULE.Bet).Select(data => (data.Device, data.Number)).ToArray(), 
            (module, number) => IsSimul(module, number) || IsConnect(module, number), 
            ReadOpticLaserStatus, 
            ReadOpticBetStatus
        );
        private async Task<ST_LASER_STATUS> ReadOpticLaserStatus(int number, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsSimul(EN_EQP_MODULE.TalonLaser, number) && !IsConnect(EN_EQP_MODULE.TalonLaser, number))
                throw new IOException("LASER가 연결되어 있지 않습니다.");
            var value = await RefreshTalonLaserStatusAsync(number, token, suppressCommandLog: true).ConfigureAwait(false);
            if (value.LastError != EN_TALON_ERROR.Ok)
                throw new IOException($"LASER 상태 수집 실패: {value.LastError}");
            return new(value.LaserOn, value.ShutterOpen, value.GateOpen, value.OutputPower);
        }

        private async Task<ST_BET_STATUS> ReadOpticBetStatus(int number, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsSimul(EN_EQP_MODULE.Bet, number) && !IsConnect(EN_EQP_MODULE.Bet, number))
                throw new IOException("BET가 연결되어 있지 않습니다.");
            var value = await RefreshBETStatusAsync(number, token, suppressCommandLog: true).ConfigureAwait(false);
            if (!value.CommOk || value.LastError != EN_BET_ERROR.Ok)
                throw new IOException($"BET 상태 수집 실패: {value.LastError}");
            return value;
        }

        public ST_LASER_STATUS GetCachedLaserStatus(int number)
        {
            var value = GetTalonStatus(number);
            return new(value.LaserOn, value.ShutterOpen, value.GateOpen, value.OutputPower);
        }

        public ST_BET_STATUS GetCachedBetStatus(int number) => GetBETStatusValue(number);
#endregion
#region Reload
        /// <summary>Applies changed rows only. Unchanged devices retain their live connection and status.</summary>
        public async Task Reload(IReadOnlyList<ST_INTERFACE_DATA> interfaces, bool reconnect = true, CancellationToken cancellationToken = default)
        {
            await _reloadLock.WaitAsync(cancellationToken);
            try
            {
                var requested = new Dictionary<string, ST_INTERFACE_DATA>(StringComparer.OrdinalIgnoreCase);
                foreach (var data in interfaces)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!requested.TryAdd(CreateDeviceKey(data.Device, data.Number), data))
                        throw new InvalidDataException($"Duplicated interface DEVICE/NUMBER: {FormatDeviceName(data)}");
                }

                // Compare collection contents, since Setting creates new argument arrays on every Save.
                var replacements = requested.Where(pair => !_devices.TryGetValue(pair.Key, out var current) || !SameInterfaceSettings(current.Data, pair.Value)).ToDictionary(pair => pair.Key, pair => CreateDevice(pair.Value), StringComparer.OrdinalIgnoreCase);
                var retired = _devices.Where(pair => !requested.ContainsKey(pair.Key) || replacements.ContainsKey(pair.Key)).ToArray();
                if (retired.Length == 0 && replacements.Count == 0)
                    return;
                // Prepare replacements before touching any connection, then retire only affected devices.
                foreach (var pair in retired)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var device = pair.Value;
                    if (device.Data.Device == EN_EQP_MODULE.Attenuator)
                        AttenuatorSnapshots.Invalidate(device.Data.Number);
                    if (device.Data.Device == EN_EQP_MODULE.Melsec)
                    {
                        var before = device.ConnectionState;
                        _melsec.CloseDevice(device.Data.Number, cancellationToken);
                        device.SetExternalCommunicationState(EN_COMM_STATE.Offline, "CLOSE", "CLOSED", "");
                        WriteConnectionLog("DISCONNECT", device, before);
                    }
                    else
                    {
                        await DisconnectDevice(device, cancellationToken);
                    }

                    if (device.Data.Device == EN_EQP_MODULE.PicoMotor)
                        _picoMotorService.Disconnect(device.Data.Number);
                    device.MessageReceived -= OnDeviceMessageReceived;
                    device.ProtocolTrace -= OnDeviceProtocolTrace;
                    _devices.TryRemove(pair.Key, out _);
                    ClearDeviceState(device.Data);
                }

                foreach (var pair in replacements)
                    _devices[pair.Key] = pair.Value;
                OpticSnapshots.Start();
                PruneDeviceStateMaps();
                foreach (var device in replacements.Values)
                {
                    if (reconnect && device.Data.AutoConnection)
                        await ConnectDevice(device, autoConnection: true, cancellationToken);
                    if (device.Data.Device == EN_EQP_MODULE.Attenuator)
                    {
                        AttenuatorSnapshots.Invalidate(device.Data.Number, "LOADING", "ATT 연결 설정 변경 후 새 상태를 기다리고 있습니다.");
                        AttenuatorSnapshots.Start();
                    }
                }
            }
            finally
            {
                _reloadLock.Release();
            }
        }

        private static bool SameInterfaceSettings(ST_INTERFACE_DATA left, ST_INTERFACE_DATA right)
        {
            if (left.InterfaceType != right.InterfaceType || left.Device != right.Device || left.Number != right.Number || left.NickName.Trim() != right.NickName.Trim() || left.SystemSection.Trim() != right.SystemSection.Trim() || left.AutoConnection != right.AutoConnection || left.IsSimulation != right.IsSimulation)
                return false;
            if (!left.Arguments.Concat(Enumerable.Repeat("", 5)).Take(5).Select(value => value.Trim()).SequenceEqual(right.Arguments.Concat(Enumerable.Repeat("", 5)).Take(5).Select(value => value.Trim())))
                return false;
            var leftExtra = NormalizeExtra(left.Extra);
            var rightExtra = NormalizeExtra(right.Extra);
            return leftExtra.Count == rightExtra.Count && leftExtra.All(pair => rightExtra.TryGetValue(pair.Key, out var value) && value == pair.Value);
        }

        private static Dictionary<string, string> NormalizeExtra(IReadOnlyDictionary<string, string>? extra) => (extra ?? new Dictionary<string, string>()).Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        private void ClearDeviceState(ST_INTERFACE_DATA data)
        {
            OpticSnapshots.Invalidate(data.Device, data.Number);
            switch (data.Device)
            {
                case EN_EQP_MODULE.TalonLaser:
                    _laserOnHeads.Remove(data.Number);
                    _talonStatuses.TryRemove(data.Number, out _);
                    break;
                case EN_EQP_MODULE.Attenuator:
                    lock (_attenuatorStatusLock)
                        _attenuatorStatuses.Remove(data.Number);
                    AttenuatorSnapshots.Invalidate(data.Number);
                    break;
                case EN_EQP_MODULE.Bet:
                    _betStatuses.TryRemove(data.Number, out _);
                    break;
                case EN_EQP_MODULE.PowerMeter:
                    _powerMeterStatuses.TryRemove(data.Number, out _);
                    _powerMeterSimulationAttenuators.Remove(data.Number);
                    break;
            }
        }

#endregion
#region StageSimulation
        /// <summary>One shared motion model per MELSEC device, independent of the selected menu.</summary>
        public CManualStageMotionSimulation GetStageMotionSimulation(int? deviceNo = null)
        {
            var number = deviceNo ?? Melsec.Map.FirstOrDefault(row => row.Id.Equals("MOTOR_STAGE_Y_CUR_POS", StringComparison.OrdinalIgnoreCase))?.DeviceNo ?? GetInterfaceList(EN_EQP_MODULE.Melsec).FirstOrDefault()?.Number ?? 0;
            return _stageMotionSimulations.GetOrAdd(number, _ => new());
        }

        public IReadOnlyDictionary<string, double> GetStageMotionSimulationFeedback(int? deviceNo = null)
        {
            var axes = GetStageMotionSimulation(deviceNo).GetSnapshot();
            var stage = axes.Single(row => row.Axis == EN_MANUAL_STAGE_AXIS.StageY);
            var review = axes.Single(row => row.Axis == EN_MANUAL_STAGE_AXIS.ReviewX);
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["MOTOR_STAGE_Y_CUR_POS"] = stage.Position,
                ["MOTOR_STAGE_Y_CUR_SPEED"] = stage.Speed,
                ["MOTOR_STAGE_Y_BUSY"] = stage.IsMoving ? 1 : 0,
                ["MOTOR_CAMERA_X_CUR_POS"] = review.Position,
                ["MOTOR_CAMERA_X_CUR_SPEED"] = review.Speed,
                ["MOTOR_CAMERA_X_BUSY"] = review.IsMoving ? 1 : 0,
                ["MOTOR_WINDOW_Y_CUR_POS"] = 0,
                ["MOTOR_WINDOW_Y_CUR_SPEED"] = 0
            };
        }
#endregion
    }

    public sealed record ST_ATTENUATOR_MOVE_TARGET(int HeadNo, double Angle);
    /// <summary>Issues ATT moves and waits only on the continuously collected shared snapshots.</summary>
    public sealed class CAttenuatorPositioningService
    {
        private const double PositionTolerance = 0.01;
        private static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromSeconds(2);
        private readonly Func<int, bool> _isRegistered;
        private readonly Func<int, double, ST_DEVICE_COMMAND_RESULT> _validateTarget;
        private readonly Func<int, EN_ATTENUATOR_COMMAND, double, CancellationToken, Task<ST_DEVICE_COMMAND_RESULT>> _executeCommand;
        private readonly Func<int, ST_ATTENUATOR_STATUS_SNAPSHOT> _getSnapshot;
        private readonly TimeSpan _moveTimeout;
        private readonly TimeSpan _pollInterval;
        private readonly SemaphoreSlim _operationLock = new(1, 1);
        private readonly object _pendingLock = new();
        private IReadOnlyList<ST_ATTENUATOR_MOVE_TARGET> _pendingStops = [];
        public CAttenuatorPositioningService(CInterfaceManager interfaces) : this(
            number => interfaces.GetInterfaceData(EN_EQP_MODULE.Attenuator, number)is not null, 
            interfaces.ValidateAttenuatorAbsoluteMoveTarget, 
            (number, command, value, token) => interfaces.ExecuteAttenuatorCommand(number, command, value, token), 
            number => interfaces.AttenuatorSnapshots.GetSnapshot(number)
        )
        {
            ArgumentNullException.ThrowIfNull(interfaces);
        }

        // Explicit transport injection keeps command order, failure handling, and timeouts testable offline.
        public CAttenuatorPositioningService(
            Func<int, bool> isRegistered, 
            Func<int, double, ST_DEVICE_COMMAND_RESULT> validateTarget, 
            Func<int, EN_ATTENUATOR_COMMAND, double, CancellationToken, Task<ST_DEVICE_COMMAND_RESULT>> executeCommand, 
            Func<int, ST_ATTENUATOR_STATUS_SNAPSHOT> getSnapshot, 
            TimeSpan? moveTimeout = null, 
            TimeSpan? pollInterval = null
        )
        {
            _isRegistered = isRegistered ?? throw new ArgumentNullException(nameof(isRegistered));
            _validateTarget = validateTarget ?? throw new ArgumentNullException(nameof(validateTarget));
            _executeCommand = executeCommand ?? throw new ArgumentNullException(nameof(executeCommand));
            _getSnapshot = getSnapshot ?? throw new ArgumentNullException(nameof(getSnapshot));
            _moveTimeout = moveTimeout ?? TimeSpan.FromSeconds(30);
            _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
            if (_moveTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(moveTimeout));
            if (_pollInterval < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        public bool HasPendingStops
        {
            get
            {
                lock (_pendingLock)
                    return _pendingStops.Count > 0;
            }
        }

        public async Task MoveAndWaitAsync(IReadOnlyList<ST_ATTENUATOR_MOVE_TARGET> targets, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(targets);
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            var attempted = new List<ST_ATTENUATOR_MOVE_TARGET>();
            try
            {
                if (HasPendingStops)
                    throw new InvalidOperationException("ATT 정지가 확인되지 않은 Head가 있습니다. STOP으로 정지를 확인한 후 다시 시도해 주세요.");
                var prepared = ValidateTargets(targets);
                var fences = new Dictionary<int, ST_ATTENUATOR_COMPLETION_FENCE>();
                // Reject every missing device, invalid limit, or unavailable initial state before the first move.
                foreach (var target in prepared)
                {
                    RequireReady(target, await WaitForInitialSnapshot(target, cancellationToken).ConfigureAwait(false));
                }

                foreach (var target in prepared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    attempted.Add(target); // A lost acknowledgement can still mean that PA was accepted.
                    fences[target.HeadNo] = await MoveOneAndWait(target, cancellationToken).ConfigureAwait(false);
                }

                // A later move can take time; verify all previously completed devices again before allowing RUN.
                foreach (var target in prepared)
                {
                    var status = await WaitForFreshSnapshot(target, fences[target.HeadNo], cancellationToken).ConfigureAwait(false);
                    RequireAtTarget(target, status);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                if (attempted.Count > 0)
                {
                    var failures = await StopTargets(attempted, CancellationToken.None).ConfigureAwait(false);
                    if (failures.Count > 0)
                        throw new IOException($"{exception.Message}\n일부 ATT의 정지를 확인하지 못했습니다. STOP으로 다시 정지해 주세요.\n{string.Join("\n", failures)}", exception);
                }

                throw;
            }
            finally
            {
                _operationLock.Release();
            }
        }

        public async Task StopPendingAsync(CancellationToken cancellationToken = default)
        {
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IReadOnlyList<ST_ATTENUATOR_MOVE_TARGET> pending;
                lock (_pendingLock)
                    pending = _pendingStops.ToArray();
                var failures = await StopTargets(pending, cancellationToken).ConfigureAwait(false);
                if (failures.Count > 0)
                    throw new IOException($"ATT 정지를 확인하지 못했습니다.\n{string.Join("\n", failures)}");
            }
            finally
            {
                _operationLock.Release();
            }
        }

        private IReadOnlyList<ST_ATTENUATOR_MOVE_TARGET> ValidateTargets(IReadOnlyList<ST_ATTENUATOR_MOVE_TARGET> targets)
        {
            if (targets.Count == 0)
                throw new InvalidDataException("ATT를 이동할 Head가 없습니다.");
            var heads = new HashSet<int>();
            var result = new List<ST_ATTENUATOR_MOVE_TARGET>(targets.Count);
            foreach (var target in targets)
            {
                if (target.HeadNo is < 1 or > 8 || !heads.Add(target.HeadNo))
                    throw new InvalidDataException($"HEAD {target.HeadNo:00} ATT 대상 설정이 잘못되었거나 중복되었습니다.");
                if (!_isRegistered(target.HeadNo - 1))
                    throw Failure(target, "ATT 연결 설정이 없습니다.");
                if (!double.IsFinite(target.Angle))
                    throw Failure(target, "목표 각도는 유효한 숫자여야 합니다.");
                var validation = _validateTarget(target.HeadNo - 1, target.Angle);
                if (!validation.IsSuccess)
                    throw Failure(target, validation.Message);
                result.Add(target with { Angle = double.Parse(target.Angle.ToString("F3", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) });
            }

            return result;
        }

        private async Task<ST_ATTENUATOR_COMPLETION_FENCE> MoveOneAndWait(ST_ATTENUATOR_MOVE_TARGET target, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_moveTimeout);
            ST_ATTENUATOR_STATUS_SNAPSHOT? last = null;
            try
            {
                var baseline = GetSnapshot(target).Version;
                await Execute(target, EN_ATTENUATOR_COMMAND.MoveAbs, target.Angle, deadline.Token).ConfigureAwait(false);
                var fence = new ST_ATTENUATOR_COMPLETION_FENCE(baseline, Stopwatch.GetTimestamp());
                while (true)
                {
                    last = await WaitForFreshSnapshot(target, fence, deadline.Token).ConfigureAwait(false);
                    if (last.Value?.CommandState == "READY" && last.IsInPosition && IsAtTarget(target, last))
                        return fence;
                    if (last.Value?.CommandState is not ("READY" or "MOVING"))
                        throw Failure(target, $"이동을 완료할 수 없는 상태입니다. {Describe(last)}");
                    await Task.Delay(_pollInterval, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{Prefix(target)} ATT 이동 완료 제한 시간({_moveTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}초)을 초과했습니다. {Describe(last)}");
            }
        }

        private async Task<ST_ATTENUATOR_STATUS_SNAPSHOT> WaitForInitialSnapshot(ST_ATTENUATOR_MOVE_TARGET target, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_moveTimeout);
            try
            {
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var snapshot = GetSnapshot(target);
                    if (snapshot.IsFresh(MaximumSnapshotAge) && snapshot.IsValid)
                        return snapshot;
                    if (snapshot.State is "OFFLINE" or "STOPPED" or "ERROR" || snapshot.IsFresh(MaximumSnapshotAge) && snapshot.State is not ("LOADING" or "STALE"))
                        throw Failure(target, $"ATT 상태를 확인할 수 없습니다. {Describe(snapshot)}");
                    await Task.Delay(_pollInterval, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{Prefix(target)} ATT 상태 확인 제한 시간을 초과했습니다.");
            }
        }

        private async Task<ST_ATTENUATOR_STATUS_SNAPSHOT> WaitForFreshSnapshot(ST_ATTENUATOR_MOVE_TARGET target, ST_ATTENUATOR_COMPLETION_FENCE fence, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_moveTimeout);
            try
            {
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var snapshot = GetSnapshot(target);
                    if (snapshot.Version > fence.Version && snapshot.CollectedTimestamp > fence.AcknowledgedTimestamp && snapshot.IsFresh(MaximumSnapshotAge))
                    {
                        if (!snapshot.IsValid || snapshot.Value is not { CommOk: true, LastError: EN_CONEX_AGP_ERROR.Ok } value || !double.IsFinite(value.CurrentPosition))
                            throw Failure(target, $"ATT 상태를 확인할 수 없습니다. {Describe(snapshot)}");
                        return snapshot;
                    }

                    if (snapshot.State is "OFFLINE" or "STOPPED" or "ERROR")
                        throw Failure(target, $"ATT 상태를 확인할 수 없습니다. {Describe(snapshot)}");
                    await Task.Delay(_pollInterval, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{Prefix(target)} ATT의 새 상태를 확인하지 못했습니다.");
            }
        }

        private ST_ATTENUATOR_STATUS_SNAPSHOT GetSnapshot(ST_ATTENUATOR_MOVE_TARGET target)
        {
            var snapshot = _getSnapshot(target.HeadNo - 1);
            if (snapshot.Number != target.HeadNo - 1)
                throw Failure(target, "다른 Head의 ATT 상태가 반환되었습니다.");
            return snapshot;
        }

        private async Task<ST_DEVICE_COMMAND_RESULT> Execute(ST_ATTENUATOR_MOVE_TARGET target, EN_ATTENUATOR_COMMAND command, double value, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _executeCommand(target.HeadNo - 1, command, value, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess)
                    throw Failure(target, $"{command} 명령에 실패했습니다. {result.Message}");
                return result;
            }
            catch (Exception exception)when (exception is IOException or TimeoutException)
            {
                throw new InvalidOperationException($"{Prefix(target)} {command} 통신에 실패했습니다. {exception.Message}", exception);
            }
        }

        private async Task<IReadOnlyList<string>> StopTargets(IReadOnlyList<ST_ATTENUATOR_MOVE_TARGET> targets, CancellationToken cancellationToken)
        {
            var failures = new List<string>();
            var pending = new List<ST_ATTENUATOR_MOVE_TARGET>();
            foreach (var target in targets)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var baseline = GetSnapshot(target).Version;
                    await Execute(target, EN_ATTENUATOR_COMMAND.Stop, 0, deadline.Token).ConfigureAwait(false);
                    var fence = new ST_ATTENUATOR_COMPLETION_FENCE(baseline, Stopwatch.GetTimestamp());
                    while (true)
                    {
                        var snapshot = await WaitForFreshSnapshot(target, fence, deadline.Token).ConfigureAwait(false);
                        var state = snapshot.Value?.CommandState;
                        if (state is "READY" or "DISABLE" or "NOT_REFERENCED" or "CONFIGURATION")
                            break;
                        if (state is not ("MOVING" or "HOMING"))
                            throw Failure(target, $"정지 후 상태를 확인할 수 없습니다. State={state}");
                        await Task.Delay(_pollInterval, deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    pending.Add(target);
                    failures.Add($"{Prefix(target)} {exception.Message}");
                }
            }

            lock (_pendingLock)
                _pendingStops = pending;
            return failures;
        }

        private static bool IsAtTarget(ST_ATTENUATOR_MOVE_TARGET target, ST_ATTENUATOR_STATUS_SNAPSHOT snapshot) => snapshot.Value is { } status && double.IsFinite(status.CurrentPosition) && Math.Abs(status.CurrentPosition - target.Angle) <= PositionTolerance;
        private static void RequireReady(ST_ATTENUATOR_MOVE_TARGET target, ST_ATTENUATOR_STATUS_SNAPSHOT snapshot)
        {
            if (!snapshot.IsValid || !snapshot.IsFresh(MaximumSnapshotAge) || snapshot.Value is not { CommOk: true, LastError: EN_CONEX_AGP_ERROR.Ok } status || !double.IsFinite(status.CurrentPosition) || status.CommandState != "READY" || !snapshot.IsInPosition)
                throw Failure(target, $"ATT가 유효한 READY/INPOSITION 상태가 아닙니다. {Describe(snapshot)}");
        }

        private static void RequireAtTarget(ST_ATTENUATOR_MOVE_TARGET target, ST_ATTENUATOR_STATUS_SNAPSHOT status)
        {
            RequireReady(target, status);
            if (!IsAtTarget(target, status))
                throw Failure(target, $"ATT가 목표 각도에 도착하지 않았습니다. {Describe(status)}");
        }

        private static string Describe(ST_ATTENUATOR_STATUS_SNAPSHOT? snapshot) => snapshot is null ? "수집된 상태가 없습니다." : $"State={snapshot.State}/{snapshot.Value?.CommandState}, Current={snapshot.Value?.CurrentPosition.ToString("0.###", CultureInfo.InvariantCulture)} deg, InPosition={snapshot.IsInPosition}, {snapshot.Detail}";
        private static string Prefix(ST_ATTENUATOR_MOVE_TARGET target) => $"HEAD {target.HeadNo:00} / ATT 목표 {target.Angle.ToString("0.###", CultureInfo.InvariantCulture)} deg:";
        private static InvalidOperationException Failure(ST_ATTENUATOR_MOVE_TARGET target, string message) => new($"{Prefix(target)} {message}");
        private sealed record ST_ATTENUATOR_COMPLETION_FENCE(long Version, long AcknowledgedTimestamp);
    }

    public sealed record ST_ATTENUATOR_STATUS_SNAPSHOT(int Number, ST_ATTENUATOR_STATUS? Value, bool IsInPosition, long Version, DateTimeOffset UpdatedAt, long CollectedTimestamp, string State, string Detail)
    {
        public bool IsValid => Value is { CommOk: true, LastError: EN_CONEX_AGP_ERROR.Ok } && double.IsFinite(Value.CurrentPosition);

        public bool IsFresh(TimeSpan maximumAge)
        {
            if (CollectedTimestamp <= 0 || maximumAge < TimeSpan.Zero)
                return false;
            var elapsed = Stopwatch.GetElapsedTime(CollectedTimestamp);
            return elapsed >= TimeSpan.Zero && elapsed <= maximumAge;
        }
    }

    /// <summary>Owns the shared ATT pollers. Readers never send a device query.</summary>
    public sealed class CAttenuatorStatusSnapshotService : IAsyncDisposable
    {
        private readonly Func<IReadOnlyList<int>> _deviceNumbers;
        private readonly Func<int, bool> _isConnected;
        private readonly Func<int, EN_ATTENUATOR_COMMAND, CancellationToken, Task<string>> _read;
        private readonly Func<int, ST_ATTENUATOR_STATUS>? _cachedValue;
        private readonly TimeSpan _period;
        private readonly TimeSpan _readTimeout;
        private readonly bool _autoStartOnRead;
        private readonly object _lifecycle = new();
        private readonly object _publication = new();
        private readonly ConcurrentDictionary<int, ST_ATTENUATOR_STATUS_SNAPSHOT> _snapshots = new();
        private readonly ConcurrentDictionary<int, CDeviceRead> _reads = new();
        private readonly ConcurrentDictionary<int, long> _epochs = new();
        private readonly ConcurrentDictionary<int, Task> _workers = new();
        private CancellationTokenSource? _cancellation;
        private Task? _discovery;
        private long _version;
        private int _generation;
        private bool _allowAutoStart = true;
        private bool _disposed;
        private bool _stopping;
        // All transport calls belong to this service; the injectable overload also supports offline regression.
        public CAttenuatorStatusSnapshotService(
            Func<IReadOnlyList<int>> deviceNumbers, 
            Func<int, bool> isConnected, 
            Func<int, EN_ATTENUATOR_COMMAND, CancellationToken, Task<string>> read, 
            Func<int, ST_ATTENUATOR_STATUS>? cachedValue = null, 
            TimeSpan? pollPeriod = null, 
            TimeSpan? readTimeout = null, 
            bool autoStartOnRead = true
        )
        {
            _deviceNumbers = deviceNumbers ?? throw new ArgumentNullException(nameof(deviceNumbers));
            _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _cachedValue = cachedValue;
            _period = pollPeriod ?? TimeSpan.FromMilliseconds(500);
            _readTimeout = readTimeout ?? TimeSpan.FromSeconds(2);
            _autoStartOnRead = autoStartOnRead;
            if (_period <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(pollPeriod));
            if (_readTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(readTimeout));
        }

        public ST_ATTENUATOR_STATUS_SNAPSHOT GetSnapshot(int number)
        {
            lock (_lifecycle)
            {
                if (_autoStartOnRead && _allowAutoStart)
                    Start();
            }

            if (!_deviceNumbers().Contains(number))
                return Unavailable(number, "UNREGISTERED", "ATT 연결 설정이 없습니다.");
            if (!_isConnected(number))
            {
                if (!_snapshots.TryGetValue(number, out var disconnected) || disconnected.State != "OFFLINE")
                    Invalidate(number, "OFFLINE", "ATT가 연결되어 있지 않습니다.");
            }

            return _snapshots.TryGetValue(number, out var snapshot) ? snapshot : Unavailable(number, "LOADING", "ATT 상태 수집을 기다리고 있습니다.");
        }

        public void Start()
        {
            lock (_lifecycle)
            {
                if (_disposed || _stopping || _cancellation is not null)
                    return;
                _allowAutoStart = true;
                var generation = ++_generation;
                foreach (var number in _snapshots.Keys.Concat(_deviceNumbers()).Distinct())
                    Invalidate(number, "LOADING", "ATT 상태 수집을 다시 시작합니다.");
                _cancellation = new CancellationTokenSource();
                var token = _cancellation.Token;
                _discovery = Task.Run(() => Discover(generation, token), CancellationToken.None);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Task? discovery;
            CancellationTokenSource? cancellation;
            lock (_lifecycle)
            {
                _allowAutoStart = false;
                _stopping = true;
                ++_generation;
                cancellation = _cancellation;
                _cancellation = null;
                discovery = _discovery;
                _discovery = null;
                cancellation?.Cancel();
                foreach (var number in _snapshots.Keys.Concat(_deviceNumbers()).Distinct())
                    Invalidate(number, "STOPPED", "ATT 상태 수집이 중지되었습니다.");
            }

            try
            {
                if (discovery is not null)
                    await IgnoreCancellation(discovery).WaitAsync(cancellationToken).ConfigureAwait(false);
                await IgnoreCancellation(Task.WhenAll(_workers.Values.ToArray())).WaitAsync(cancellationToken).ConfigureAwait(false);
                _workers.Clear();
            }
            finally
            {
                cancellation?.Dispose();
                lock (_lifecycle)
                    _stopping = false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            lock (_lifecycle)
                _disposed = true;
        }

        /// <summary>Invalidation is memory-only and immediately removes any completion indication.</summary>
        public void Invalidate(int number, string state = "OFFLINE", string detail = "ATT 상태를 다시 수집해야 합니다.")
        {
            lock (_publication)
            {
                _epochs.AddOrUpdate(number, 1, (_, previous) => previous + 1);
                _snapshots[number] = new(number, null, false, Interlocked.Increment(ref _version), DateTimeOffset.Now, 0, state, detail);
            }
        }

        /// <summary>Collects one shared sample; simultaneous collectors for the same ATT are serialized.</summary>
        public Task CollectOnceAsync(int number, CancellationToken cancellationToken = default) => Collect(number, Volatile.Read(ref _generation), cancellationToken);
        private async Task Discover(int generation, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var number in _deviceNumbers().Distinct())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_workers.TryGetValue(number, out var previous) && !previous.IsCompleted)
                        continue;
                    if (previous is not null)
                        Observe(previous);
                    _workers[number] = Task.Run(() => PollDevice(number, generation, cancellationToken), CancellationToken.None);
                }

                await Task.Delay(_period, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task PollDevice(int number, int generation, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _deviceNumbers().Contains(number))
            {
                var started = Stopwatch.GetTimestamp();
                await Collect(number, generation, cancellationToken).ConfigureAwait(false);
                var delay = _period - Stopwatch.GetElapsedTime(started);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task Collect(int number, int generation, CancellationToken cancellationToken)
        {
            var device = _reads.GetOrAdd(number, _ => new CDeviceRead());
            await device.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (generation != Volatile.Read(ref _generation))
                    return;
                if (!_deviceNumbers().Contains(number))
                {
                    Invalidate(number, "UNREGISTERED", "ATT 연결 설정이 없습니다.");
                    return;
                }

                // An offline device is never queried or automatically reconnected by this worker.
                if (!_isConnected(number))
                {
                    Invalidate(number, "OFFLINE", "ATT가 연결되어 있지 않습니다.");
                    return;
                }

                if (device.InFlight is { IsCompleted: false })
                {
                    Invalidate(number, "TIMEOUT", "이전 ATT 응답이 끝나지 않아 새 상태 읽기를 보류했습니다.");
                    return;
                }

                var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(_readTimeout);
                var started = Stopwatch.GetTimestamp();
                var epoch = _epochs.GetOrAdd(number, 0);
                var read = ReadFrame(number, deadline.Token);
                device.InFlight = read;
                try
                {
                    var frame = await read.WaitAsync(deadline.Token).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != Volatile.Read(ref _generation))
                        return;
                    var cached = _cachedValue?.Invoke(number);
                    var value = new ST_ATTENUATOR_STATUS(
                        frame.Position, 
                        frame.Target ?? cached?.TargetPosition ?? frame.Position, 
                        frame.State, 
                        CommOk: true, 
                        LastError: EN_CONEX_AGP_ERROR.Ok, 
                        UpdatedAt: DateTimeOffset.Now, 
                        HomeType: cached?.HomeType, 
                        NegativeSoftwareLimit: cached?.NegativeSoftwareLimit, 
                        PositiveSoftwareLimit: cached?.PositiveSoftwareLimit, 
                        IGain: cached?.IGain, 
                        PGain: cached?.PGain, 
                        InterpolationFactor: cached?.InterpolationFactor
                    );
                    lock (_publication)
                    {
                        if (_epochs.GetOrAdd(number, 0) != epoch || generation != Volatile.Read(ref _generation))
                            return;
                        if (!_isConnected(number))
                        {
                            Invalidate(number, "OFFLINE", "ATT 연결이 해제되었습니다.");
                            return;
                        }

                        _snapshots[number] = new(number, value, frame.InPosition, Interlocked.Increment(ref _version), DateTimeOffset.Now, started, frame.State, "");
                    }
                }
                catch (OperationCanceledException)when (!cancellationToken.IsCancellationRequested)
                {
                    if (generation == Volatile.Read(ref _generation))
                        Invalidate(number, "TIMEOUT", "ATT 상태 응답 제한 시간을 초과했습니다.");
                }
                catch (Exception exception)when (exception is not OperationCanceledException)
                {
                    if (generation == Volatile.Read(ref _generation))
                        Invalidate(number, _isConnected(number) ? "ERROR" : "OFFLINE", exception.Message);
                }
                finally
                {
                    if (read.IsCompleted)
                    {
                        Observe(read);
                        deadline.Dispose();
                    }
                    else
                        _ = read.ContinueWith(
                            completed =>
                        {
                            Observe(completed);
                            deadline.Dispose();
                        }, 
                            CancellationToken.None, 
                            TaskContinuationOptions.ExecuteSynchronously, 
                            TaskScheduler.Default
                        );
                }
            }
            finally
            {
                device.Gate.Release();
            }
        }

        private async Task<CFrame> ReadFrame(int number, CancellationToken cancellationToken)
        {
            // Both state samples must indicate READY. A transition during TP cannot report in-position.
            var before = ParseState(await Read(EN_ATTENUATOR_COMMAND.PollState).ConfigureAwait(false));
            var positionText = await Read(EN_ATTENUATOR_COMMAND.PollCurrentPosition).ConfigureAwait(false);
            var fields = positionText.Split(':', StringSplitOptions.TrimEntries);
            if (fields.Length != 2 || !fields[0].Equals("TP", StringComparison.OrdinalIgnoreCase) || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var position) || !double.IsFinite(position))
                throw new InvalidDataException($"ATT TP 위치 응답이 잘못되었습니다. {positionText}");
            double? target = null;
            if (before is "READY" or "MOVING" or "HOMING" or "DISABLE")
            {
                var targetText = await Read(EN_ATTENUATOR_COMMAND.PollTargetPosition).ConfigureAwait(false);
                fields = targetText.Split(':', StringSplitOptions.TrimEntries);
                if (fields.Length != 2 || !fields[0].Equals("TH", StringComparison.OrdinalIgnoreCase) || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTarget) || !double.IsFinite(parsedTarget))
                    throw new InvalidDataException($"ATT TH 목표 위치 응답이 잘못되었습니다. {targetText}");
                target = parsedTarget;
            }

            var after = ParseState(await Read(EN_ATTENUATOR_COMMAND.PollState).ConfigureAwait(false));
            return new(position, target, after, before == "READY" && after == "READY");
            async Task<string> Read(EN_ATTENUATOR_COMMAND command)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_isConnected(number))
                    throw new IOException("ATT가 연결되어 있지 않습니다.");
                return await _read(number, command, cancellationToken).ConfigureAwait(false);
            }
        }

        private static string ParseState(string response)
        {
            var fields = response.Split(':', StringSplitOptions.TrimEntries);
            if (fields.Length != 3 || !fields[0].Equals("TS", StringComparison.OrdinalIgnoreCase) || !uint.TryParse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var error))
                throw new InvalidDataException($"ATT TS 상태 응답이 잘못되었습니다. {response}");
            if (error != 0)
                throw new InvalidDataException($"ATT 컨트롤러 오류: {response}");
            var state = fields[2].ToUpperInvariant();
            if (int.TryParse(state, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                state = code switch
                {
                    >= 0x0A and <= 0x10 => "NOT_REFERENCED",
                    0x14 => "CONFIGURATION",
                    0x1E => "HOMING",
                    0x28 => "MOVING",
                    0x32 or 0x33 or 0x34 => "READY",
                    0x3C or 0x3D => "DISABLE",
                    _ => $"STATE_{code:X2}"};
            if (state is not ("READY" or "MOVING" or "HOMING" or "NOT_REFERENCED" or "CONFIGURATION" or "DISABLE"))
                throw new InvalidDataException($"ATT 상태를 확인할 수 없습니다. {response}");
            return state;
        }

        private static ST_ATTENUATOR_STATUS_SNAPSHOT Unavailable(int number, string state, string detail) => new(number, null, false, 0, DateTimeOffset.MinValue, 0, state, detail);
        private static void Observe(Task task)
        {
            if (task.IsFaulted)
                _ = task.Exception;
        }

        private static async Task IgnoreCancellation(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private sealed class CDeviceRead
        {
            public SemaphoreSlim Gate { get; } = new(1, 1);
            public Task<CFrame>? InFlight { get; set; }
        }

        private sealed record CFrame(double Position, double? Target, string State, bool InPosition);
    }

    public enum EN_COMM_STATE
    {
        Offline,
        Simulation,
        Online
    }

    public enum EN_EQP_MODULE
    {
        WonikCtrl,
        Vision,
        Automation1,
        Motion,
        TalonLaser,
        Attenuator,
        Bet,
        PowerMeter,
        PicoMotor,
        Melsec
    }

    public enum EN_INTERFACE_TYPE
    {
        OpcUa,
        ModbusSerial,
        ModbusTcp,
        Serial,
        SocketClient,
        SocketServer,
        SocketClientUdp,
        SocketServerUdp,
        AcsNet,
        XpsNet,
        Automation1Net,
        PicoMotor,
        MelsecNet
    }

    public sealed record ST_DEVICE_COMM_STATUS(EN_EQP_MODULE Module, EN_COMM_STATE ConnectionState);
    public sealed record ST_INTERFACE_CONNECT_OPTION(
        string Endpoint, 
        string LocalAddress, 
        string RemoteAddress, 
        int Port, 
        int TimeoutMs, 
        int RetryCount, 
        string SerialPort, 
        int BaudRate, 
        string Parity, 
        int DataBits, 
        string StopBits, 
        string Handshake, 
        int MaxClientCount = 8
    );
    public sealed record ST_INTERFACE_COMM_STATUS(
        EN_EQP_MODULE Module, 
        string NickName, 
        EN_INTERFACE_TYPE InterfaceType, 
        int Number, 
        bool AutoConnection, 
        EN_COMM_STATE ConnectionState, 
        bool IsSimulation, 
        string Endpoint, 
        string LastSent, 
        string LastReceived, 
        string LastError, 
        DateTimeOffset? LastChangedAt
    )
    {
        /// <summary>Vision server peer state; null for simulation and other transports.</summary>
        public bool? IsClientConnected { get; init; }
        public string ConnectionText => ConnectionState switch
        {
            EN_COMM_STATE.Online when IsClientConnected.HasValue => IsClientConnected.Value ? "ONLINE (CONNECT)" : "ONLINE (NOT CONNECT)",
            EN_COMM_STATE.Online => "ONLINE",
            EN_COMM_STATE.Offline => "OFFLINE",
            _ => "SIMULATION"
        };
        public string InstanceKey
        {
            get
            {
                return $"{Module}[{Number}]";
            }
        }
    }

    public sealed record ST_INTERFACE_RECEIVED_MESSAGE(DateTimeOffset ReceivedAt, EN_EQP_MODULE Module, int Number, string NickName, string RemoteEndPoint, string Message);
    public sealed record ST_INTERFACE_PROTOCOL_TRACE(DateTimeOffset OccurredAt, EN_EQP_MODULE Module, int Number, string NickName, string Direction, string Message);
    internal sealed record ST_COMM_PROTOCOL_TRACE(DateTimeOffset OccurredAt, string Direction, string Message);
    public sealed record ST_INTERFACE_DATA(
        EN_INTERFACE_TYPE InterfaceType, 
        EN_EQP_MODULE Device, 
        int Number, 
        string NickName, 
        string SystemSection, 
        bool AutoConnection, 
        bool IsSimulation, 
        IReadOnlyList<string> Arguments, 
        IReadOnlyDictionary<string, string>? Extra = null
    )
    {
        public string InstanceKey
        {
            get
            {
                return $"{Device}[{Number}]";
            }
        }
    }

    public sealed record ST_DEVICE_COMMAND_RESULT(bool IsSuccess, string Message);
    public abstract class CBETFileBase
    {
        public abstract Task<IReadOnlyList<ST_BET_TABLE_DATA>> Load(CancellationToken cancellationToken = default);
        public abstract Task Save(IReadOnlyList<ST_BET_TABLE_DATA> table, CancellationToken cancellationToken = default);
    }

    public sealed class CInterfaceDevice
    {
        private bool _simulationMode;
        private readonly CCommBase _comm;
        private string _simulationLastSent = "";
        private string _simulationLastReceived = "";
        private string _simulationLastError = "";
        private DateTimeOffset? _simulationLastChangedAt;
        private bool _externalCommunication;
        private EN_COMM_STATE _externalConnectionState = EN_COMM_STATE.Offline;
        private string _externalLastSent = "";
        private string _externalLastReceived = "";
        private string _externalLastError = "";
        private DateTimeOffset? _externalLastChangedAt;
        internal event Func<CInterfaceDevice, ST_COMM_RECEIVED_MESSAGE, CancellationToken, Task<string>>? MessageReceived;
        internal event Action<CInterfaceDevice, ST_COMM_PROTOCOL_TRACE>? ProtocolTrace;
        public CInterfaceDevice(ST_INTERFACE_DATA data, bool simulationMode)
        {
            Data = data;
            ConnectOption = CInterfaceConnectOption.Parse(data);
            _simulationMode = simulationMode;
            _comm = CComm.Create(Data, ConnectOption);
            if (_comm is CSocketServerComm messageSource)
            {
                messageSource.MessageReceived += OnCommMessageReceived;
            }

            if (_comm is CVisionComm visionComm)
            {
                visionComm.ProtocolTrace += OnCommProtocolTrace;
            }

            if (_simulationMode)
            {
                TouchSimulationState();
            }
        }

        public ST_INTERFACE_DATA Data { get; }
        public ST_INTERFACE_CONNECT_OPTION ConnectOption { get; }

        internal void ConfigureVisionLineProtocol(Func<int> capacity)
        {
            if (_comm is CVisionComm vision)
                vision.LineCapacityProvider = capacity;
        }

        public EN_COMM_STATE ConnectionState
        {
            get
            {
                return _externalCommunication ? _externalConnectionState : _simulationMode ? EN_COMM_STATE.Simulation : _comm.ConnectionState;
            }
        }

        public bool IsSimulation
        {
            get
            {
                return _simulationMode;
            }
        }

        public ST_INTERFACE_COMM_STATUS GetCommunicationStatus()
        {
            if (_externalCommunication)
            {
                return new ST_INTERFACE_COMM_STATUS(
                    Data.Device, 
                    Data.NickName, 
                    Data.InterfaceType, 
                    Data.Number, 
                    Data.AutoConnection, 
                    _externalConnectionState, 
                    _simulationMode, 
                    ConnectOption.Endpoint, 
                    _externalLastSent, 
                    _externalLastReceived, 
                    _externalLastError, 
                    _externalLastChangedAt
                );
            }

            return new ST_INTERFACE_COMM_STATUS(
                Data.Device, 
                Data.NickName, 
                Data.InterfaceType, 
                Data.Number, 
                Data.AutoConnection, 
                ConnectionState, 
                _simulationMode, 
                _comm.Endpoint, 
                _simulationMode ? _simulationLastSent : _comm.LastSent, 
                _simulationMode ? _simulationLastReceived : _comm.LastReceived, 
                _simulationMode ? _simulationLastError : _comm.LastError, 
                _simulationMode ? _simulationLastChangedAt : _comm.LastChangedAt
            )
            {
                IsClientConnected = !_simulationMode && _comm is CVisionComm vision ? vision.HasConnectedClient : null
            };
        }

        public async Task SetSimulationMode(bool enabled, CancellationToken cancellationToken = default)
        {
            if (_simulationMode == enabled)
            {
                return;
            }

            _simulationMode = enabled;
            if (_externalCommunication)
            {
                _externalConnectionState = enabled ? EN_COMM_STATE.Simulation : EN_COMM_STATE.Offline;
                _externalLastError = "";
                _externalLastChangedAt = DateTimeOffset.Now;
                return;
            }

            if (_simulationMode)
            {
                await _comm.Disconnect(cancellationToken);
                TouchSimulationState();
            }
        }

        public Task Connect(CancellationToken cancellationToken = default)
        {
            if (_externalCommunication)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _externalConnectionState = _simulationMode ? EN_COMM_STATE.Simulation : EN_COMM_STATE.Offline;
                _externalLastChangedAt = DateTimeOffset.Now;
                return Task.CompletedTask;
            }

            if (_simulationMode)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _simulationLastError = "";
                TouchSimulationState();
                return Task.CompletedTask;
            }

            return _comm.Connect(cancellationToken);
        }

        public void ResetAutoReconnectPolicy()
        {
            _comm.ResetAutoReconnectPolicy();
        }

        public void SuppressAutoReconnect()
        {
            _comm.SuppressAutoReconnect();
        }

        public Task Disconnect(CancellationToken cancellationToken = default)
        {
            if (_externalCommunication)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _externalConnectionState = EN_COMM_STATE.Offline;
                _externalLastChangedAt = DateTimeOffset.Now;
                return Task.CompletedTask;
            }

            if (_simulationMode)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TouchSimulationState();
                return Task.CompletedTask;
            }

            return _comm.Disconnect(cancellationToken);
        }

        public Task<string> ExecuteFunction(string function, CancellationToken cancellationToken = default)
        {
            if (_externalCommunication)
            {
                throw new InvalidOperationException("MELSEC commands must use CMelsec typed read/write functions.");
            }

            if (_simulationMode)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _simulationLastSent = function;
                _simulationLastReceived = $"SIM:{Data.NickName}:{function}:OK";
                _simulationLastError = "";
                TouchSimulationState();
                return Task.FromResult(_simulationLastReceived);
            }

            return _comm.Execute(function, cancellationToken);
        }

        internal void EnableExternalCommunication()
        {
            _externalCommunication = true;
            _externalConnectionState = _simulationMode ? EN_COMM_STATE.Simulation : EN_COMM_STATE.Offline;
            _externalLastChangedAt = DateTimeOffset.Now;
        }

        internal void SetExternalCommunicationState(EN_COMM_STATE state, string lastSent, string lastReceived, string lastError)
        {
            _externalConnectionState = state;
            _externalLastSent = lastSent;
            _externalLastReceived = lastReceived;
            _externalLastError = lastError;
            _externalLastChangedAt = DateTimeOffset.Now;
        }

        private void TouchSimulationState()
        {
            _simulationLastChangedAt = DateTimeOffset.Now;
        }

        private async Task<string> OnCommMessageReceived(ST_COMM_RECEIVED_MESSAGE message, CancellationToken cancellationToken)
        {
            var handler = MessageReceived;
            if (handler is null)
            {
                return "ACK";
            }

            var response = "ACK";
            foreach (var callback in handler.GetInvocationList().Cast<Func<CInterfaceDevice, ST_COMM_RECEIVED_MESSAGE, CancellationToken, Task<string>>>())
            {
                var callbackResponse = await callback(this, message, cancellationToken);
                if (!string.IsNullOrWhiteSpace(callbackResponse))
                {
                    response = callbackResponse;
                }
            }

            return string.IsNullOrWhiteSpace(response) ? "ACK" : response;
        }

        private void OnCommProtocolTrace(ST_COMM_PROTOCOL_TRACE trace)
        {
            ProtocolTrace?.Invoke(this, trace);
        }
    }

    internal static class CInterfaceConnectOption
    {
        public static ST_INTERFACE_CONNECT_OPTION Parse(ST_INTERFACE_DATA data)
        {
            string SelectItem(string argument)
            {
                return argument.Trim();
            }

            var args = data.Arguments.Select(SelectItem).Concat(Enumerable.Repeat("", 5)).Take(5).ToArray();
            return data.InterfaceType switch
            {
                EN_INTERFACE_TYPE.Serial or EN_INTERFACE_TYPE.ModbusSerial => CreateSerialOption(data, args),
                EN_INTERFACE_TYPE.SocketClient or EN_INTERFACE_TYPE.SocketServer or EN_INTERFACE_TYPE.SocketClientUdp or EN_INTERFACE_TYPE.SocketServerUdp or EN_INTERFACE_TYPE.ModbusTcp => CreateSocketOption(data, args),
                EN_INTERFACE_TYPE.AcsNet => CreateAcsOption(args),
                EN_INTERFACE_TYPE.XpsNet => CreateXpsOption(args),
                EN_INTERFACE_TYPE.Automation1Net => CreateAutomation1Option(args),
                EN_INTERFACE_TYPE.PicoMotor => CreatePicoMotorOption(),
                EN_INTERFACE_TYPE.MelsecNet => CreateMelsecNetOption(args),
                EN_INTERFACE_TYPE.OpcUa => CreateOpcUaOption(args),
                _ => CreateSocketOption(data, args)};
        }

        private static ST_INTERFACE_CONNECT_OPTION CreateSocketOption(ST_INTERFACE_DATA data, IReadOnlyList<string> args)
        {
            var isServer = data.InterfaceType is EN_INTERFACE_TYPE.SocketServer or EN_INTERFACE_TYPE.SocketServerUdp;
            var localAddress = DefaultIfBlank(args[0], "0.0.0.0");
            var remoteAddress = DefaultIfBlank(args[1], isServer ? "*" : "127.0.0.1");
            var port = ReadInt(args[2], 0);
            var timeoutMs = ReadInt(args[3], 3000);
            var retryCount = ReadInt(args[4], 1);
            var maxClientCount = Math.Max(1, ReadInt(ReadExtra(data, "MAX_CLIENT_COUNT"), 8));
            var endpointAddress = isServer ? localAddress : remoteAddress;
            var endpoint = port > 0 ? $"{endpointAddress}:{port}" : endpointAddress;
            return new ST_INTERFACE_CONNECT_OPTION(endpoint, localAddress, remoteAddress, port, timeoutMs, retryCount, "", 0, "", 0, "", "", maxClientCount);
        }

        private static ST_INTERFACE_CONNECT_OPTION CreateAcsOption(IReadOnlyList<string> args)
        {
            var localAddress = DefaultIfBlank(args[0], "0.0.0.0");
            var remoteAddress = DefaultIfBlank(args[1], "127.0.0.1");
            var port = ReadInt(args[2], 701);
            var timeoutMs = ReadInt(args[3], 3000);
            var retryCount = ReadInt(args[4], 1);
            return new ST_INTERFACE_CONNECT_OPTION($"{remoteAddress}:{port}", localAddress, remoteAddress, port, timeoutMs, retryCount, "", 0, "", 0, "", "");
        }

        private static ST_INTERFACE_CONNECT_OPTION CreateXpsOption(IReadOnlyList<string> args)
        {
            var localAddress = DefaultIfBlank(args[0], "0.0.0.0");
            var remoteAddress = DefaultIfBlank(args[1], "192.168.254.254");
            var port = ReadInt(args[2], 5001);
            var timeoutMs = ReadInt(args[3], 3000);
            var retryCount = ReadInt(args[4], 1);
            return new ST_INTERFACE_CONNECT_OPTION($"{remoteAddress}:{port}", localAddress, remoteAddress, port, timeoutMs, retryCount, "", 0, "", 0, "", "");
        }

        private static ST_INTERFACE_CONNECT_OPTION CreateAutomation1Option(IReadOnlyList<string> args)
        {
            var localAddress = DefaultIfBlank(args[0], "0.0.0.0");
            var remoteAddress = DefaultIfBlank(args[1], "127.0.0.1");
            var port = ReadInt(args[2], 12200);
            var timeoutMs = ReadInt(args[3], 3000);
            var retryCount = ReadInt(args[4], 1);
            return new ST_INTERFACE_CONNECT_OPTION($"{remoteAddress}:{port}", localAddress, remoteAddress, port, timeoutMs, retryCount, "", 0, "", 0, "", "");
        }

        private static ST_INTERFACE_CONNECT_OPTION CreatePicoMotorOption()
        {
            return new ST_INTERFACE_CONNECT_OPTION("CmdLib (USB/Ethernet)", "", "", 0, 3000, 1, "", 0, "", 0, "", "");
        }

        private static ST_INTERFACE_CONNECT_OPTION CreateMelsecNetOption(IReadOnlyList<string> args)
        {
            var channel = DefaultIfBlank(args[0], "NOT_CONFIGURED");
            var network = DefaultIfBlank(args[1], "NOT_CONFIGURED");
            var station = DefaultIfBlank(args[2], "NOT_CONFIGURED");
            var timeoutMs = ReadInt(args[3], 3000);
            var retryCount = ReadInt(args[4], 1);
            var endpoint = "MELSECNET:CHANNEL=" + channel + ":NETWORK=" + network + ":STATION=" + station;
            return new ST_INTERFACE_CONNECT_OPTION(endpoint, "", "", 0, timeoutMs, retryCount, "", 0, "", 0, "", "");
        }

        private static ST_INTERFACE_CONNECT_OPTION CreateSerialOption(ST_INTERFACE_DATA data, IReadOnlyList<string> args)
        {
            var port = DefaultIfBlank(args[0], "COM1");
            var baudRate = ReadInt(args[1], 9600);
            var parity = DefaultIfBlank(args[2], "NONE");
            var dataBits = ReadInt(args[3], 8);
            var stopBits = DefaultIfBlank(args[4], "ONE");
            var handshake = ReadExtra(data, "FLOW_CONTROL");
            return new ST_INTERFACE_CONNECT_OPTION($"{port}:{baudRate}", "", "", 0, 3000, 1, port, baudRate, parity, dataBits, stopBits, handshake);
        }

        private static ST_INTERFACE_CONNECT_OPTION CreateOpcUaOption(IReadOnlyList<string> args)
        {
            var endpoint = DefaultIfBlank(args[0], "opc.tcp://127.0.0.1:4840");
            var timeoutMs = ReadInt(args[3], 3000);
            var retryCount = ReadInt(args[4], 1);
            return new ST_INTERFACE_CONNECT_OPTION(endpoint, "", endpoint, 0, timeoutMs, retryCount, "", 0, "", 0, "", "");
        }

        private static string ReadExtra(ST_INTERFACE_DATA data, params string[] names)
        {
            if (data.Extra is null)
            {
                return "";
            }

            foreach (var name in names)
            {
                if (data.Extra.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return "";
        }

        private static string DefaultIfBlank(string value, string defaultValue)
        {
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
        }

        private static int ReadInt(string value, int defaultValue)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : defaultValue;
        }
    }

    /// <summary>Shared Laser/BET readers. Consumers only inspect snapshots; offline devices are never queried.</summary>
    public sealed class COpticStatusSnapshotService : IAsyncDisposable
    {
        private sealed record Versioned<T>(long Version, T Value)
            where T : class;
        private sealed class Entry<T>
            where T : class
        {
            public long Version;
            public CSharedDeviceSnapshotService<Versioned<T>> Reader = null !;
        }

        private readonly Func<IReadOnlyList<(EN_EQP_MODULE Module, int Number)>> _devices;
        private readonly Func<EN_EQP_MODULE, int, bool> _connected;
        private readonly Func<int, CancellationToken, Task<ST_LASER_STATUS>> _laserRead;
        private readonly Func<int, CancellationToken, Task<ST_BET_STATUS>> _betRead;
        private readonly Dictionary<int, Entry<ST_LASER_STATUS>> _lasers = [];
        private readonly Dictionary<int, Entry<ST_BET_STATUS>> _bets = [];
        private readonly object _sync = new();
        private bool _running = true;
        private bool _disposed;
        public COpticStatusSnapshotService(
            Func<IReadOnlyList<(EN_EQP_MODULE Module, int Number)>> devices, 
            Func<EN_EQP_MODULE, int, bool> connected, 
            Func<int, CancellationToken, Task<ST_LASER_STATUS>> laserRead, 
            Func<int, CancellationToken, Task<ST_BET_STATUS>> betRead
        )
        {
            _devices = devices;
            _connected = connected;
            _laserRead = laserRead;
            _betRead = betRead;
        }

        public ST_SHARED_DEVICE_SNAPSHOT<ST_LASER_STATUS> GetLaser(int number) => Get(EN_EQP_MODULE.TalonLaser, number, _lasers, _laserRead);
        public ST_SHARED_DEVICE_SNAPSHOT<ST_BET_STATUS> GetBet(int number) => Get(EN_EQP_MODULE.Bet, number, _bets, _betRead);
        private ST_SHARED_DEVICE_SNAPSHOT<T> Get<T>(EN_EQP_MODULE module, int number, Dictionary<int, Entry<T>> entries, Func<int, CancellationToken, Task<T>> read)
            where T : class
        {
            lock (_sync)
            {
                if (!_devices().Contains((module, number)))
                    return new(null, "N/C", "장치가 등록되지 않았습니다.", default);
                if (!_running || _disposed)
                    return new(null, "STOPPED", "상태 수집이 중지되었습니다.", default);
                if (!_connected(module, number))
                {
                    if (entries.TryGetValue(number, out var previous))
                        Interlocked.Increment(ref previous.Version);
                    return new(null, "OFFLINE", "장치가 연결되지 않았습니다.", default);
                }

                if (!entries.TryGetValue(number, out var entry))
                {
                    entry = new Entry<T>();
                    var target = entry;
                    entry.Reader = new(async token =>
                    {
                        var version = Interlocked.Read(ref target.Version);
                        if (!_connected(module, number))
                            throw new IOException("장치가 연결되지 않아 조회를 보류합니다.");
                        return new Versioned<T>(version, await read(number, token).ConfigureAwait(false));
                    }, readTimeout: TimeSpan.FromSeconds(5));
                    entries.Add(number, entry);
                }

                entry.Reader.Start();
                var snapshot = entry.Reader.GetSnapshot();
                if (snapshot.Value is not { } current)
                    return new(null, snapshot.State, snapshot.Detail, snapshot.UpdatedAt);
                if (current.Version != Interlocked.Read(ref entry.Version))
                    return new(null, "LOADING", "연결 설정 변경 후 새 상태를 기다리고 있습니다.", default);
                return new(current.Value, snapshot.State, snapshot.Detail, snapshot.UpdatedAt);
            }
        }

        public void Invalidate(EN_EQP_MODULE module, int number)
        {
            lock (_sync)
            {
                if (module == EN_EQP_MODULE.TalonLaser && _lasers.TryGetValue(number, out var laser))
                    Interlocked.Increment(ref laser.Version);
                if (module == EN_EQP_MODULE.Bet && _bets.TryGetValue(number, out var bet))
                    Interlocked.Increment(ref bet.Version);
            }
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _running = true;
            }

            foreach (var device in _devices())
            {
                if (device.Module == EN_EQP_MODULE.TalonLaser)
                    GetLaser(device.Number);
                if (device.Module == EN_EQP_MODULE.Bet)
                    GetBet(device.Number);
            }
        }

        public async Task StopAsync()
        {
            Task[] stops;
            lock (_sync)
            {
                _running = false;
                stops = _lasers.Values.Select(entry => entry.Reader.StopAsync()).Concat(_bets.Values.Select(entry => entry.Reader.StopAsync())).ToArray();
            }

            await Task.WhenAll(stops).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            lock (_sync)
                _disposed = true;
            await StopAsync().ConfigureAwait(false);
        }
    }

    public sealed record ST_SHARED_DEVICE_SNAPSHOT<T>(T? Value, string State, string Detail, DateTimeOffset UpdatedAt)
        where T : class;
    /// <summary>Owns a single sequential transport reader; GetSnapshot never performs device I/O.</summary>
    public sealed class CSharedDeviceSnapshotService<T>
        where T : class
    {
        private readonly Func<CancellationToken, Task<T>> _read;
        private readonly TimeSpan _period;
        private readonly TimeSpan _timeout;
        private readonly TimeSpan _maximumAge;
        private readonly object _sync = new();
        private CancellationTokenSource? _stop;
        private Task? _worker;
        private Task<T>? _inflight;
        private bool _stopping;
        private long _generation;
        private long _collectedAt;
        private ST_SHARED_DEVICE_SNAPSHOT<T> _snapshot = new(null, "LOADING", "상태 수집을 기다리고 있습니다.", default);
        public CSharedDeviceSnapshotService(Func<CancellationToken, Task<T>> read, TimeSpan? period = null, TimeSpan? readTimeout = null, TimeSpan? maximumAge = null)
        {
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _period = period ?? TimeSpan.FromSeconds(1);
            _timeout = readTimeout ?? TimeSpan.FromSeconds(2);
            _maximumAge = maximumAge ?? TimeSpan.FromSeconds(5);
            if (_period <= TimeSpan.Zero || _timeout <= TimeSpan.Zero || _maximumAge <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(period));
        }

        public ST_SHARED_DEVICE_SNAPSHOT<T> GetSnapshot()
        {
            lock (_sync)
            {
                if (_snapshot.State == "OK" && Environment.TickCount64 - _collectedAt > _maximumAge.TotalMilliseconds)
                    return _snapshot with
                    {
                        Value = null,
                        State = "STALE",
                        Detail = "최근 장치 상태를 수집하지 못했습니다."
                    };
                return _snapshot;
            }
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_stop is not null || _stopping)
                    return;
                _stop = new CancellationTokenSource();
                var generation = ++_generation;
                _snapshot = new(null, "LOADING", "상태 수집을 기다리고 있습니다.", default);
                var token = _stop.Token;
                _worker = Task.Run(() => Collect(generation, token));
            }
        }

        public async Task StopAsync()
        {
            Task? worker;
            CancellationTokenSource? stop;
            lock (_sync)
            {
                _stopping = true;
                ++_generation;
                stop = _stop;
                stop?.Cancel();
                worker = _worker;
                _snapshot = new(null, "STOPPED", "상태 수집이 중지되었습니다.", default);
            }

            if (worker is not null)
                await worker.ConfigureAwait(false);
            lock (_sync)
            {
                if (ReferenceEquals(stop, _stop))
                {
                    _stop = null;
                    _worker = null;
                    stop?.Dispose();
                    _stopping = false;
                }
            }
        }

        private async Task Collect(long generation, CancellationToken token)
        {
            try
            {
                do
                {
                    token.ThrowIfCancellationRequested();
                    Task<T>? readTask;
                    CancellationTokenSource? readStop = null;
                    lock (_sync)
                    {
                        if (generation != _generation)
                            return;
                        if (_inflight is { IsCompleted: false })
                        {
                            // A cancelled/timed-out driver call is quarantined across cycles and restarts.
                            // Never issue a replacement while that same transport call is still running.
                            _snapshot = new(null, "BLOCKED", "이전 장치 조회가 끝나지 않아 재조회를 보류합니다.", default);
                            readTask = null;
                        }
                        else
                        {
                            readStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                            readStop.CancelAfter(_timeout);
                            var readToken = readStop.Token;
                            _inflight = readTask = Task.Run(() => _read(readToken), CancellationToken.None);
                            var lifetime = readStop;
                            _ = readTask.ContinueWith(
                                task =>
                            {
                                _ = task.Exception;
                                lifetime.Dispose();
                            }, 
                                CancellationToken.None, 
                                TaskContinuationOptions.ExecuteSynchronously, 
                                TaskScheduler.Default
                            );
                        }
                    }

                    if (readTask is not null && readStop is not null)
                    {
                        // A timeout releases this worker, but _inflight remains owned until the driver actually returns.
                        try
                        {
                            var value = await readTask.WaitAsync(_timeout, token).ConfigureAwait(false);
                            lock (_sync)
                            {
                                if (generation != _generation || token.IsCancellationRequested)
                                    return;
                                _collectedAt = Environment.TickCount64;
                                _snapshot = new(value, "OK", "", DateTimeOffset.Now);
                            }
                        }
                        catch (OperationCanceledException)when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            lock (_sync)
                            {
                                if (generation != _generation)
                                    return;
                                _snapshot = new(null, exception is TimeoutException or OperationCanceledException ? "TIMEOUT" : "ERROR", exception.Message, default);
                            }
                        }
                    }

                    await Task.Delay(_period, token).ConfigureAwait(false);
                }
                while (!token.IsCancellationRequested);
            }
            catch (OperationCanceledException)when (token.IsCancellationRequested)
            {
            }
        }
    }
}
