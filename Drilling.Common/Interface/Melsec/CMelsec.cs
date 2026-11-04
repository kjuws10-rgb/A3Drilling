namespace Drilling.Common.Interface
{
    using System.Globalization;
    using System.Buffers.Binary;
    using System.Runtime.ExceptionServices;
    using System.Text;
    using Drilling.Common.Log;
    using Drilling.Common.Threading;
    using System.Diagnostics;
    using System.Runtime.InteropServices;

    public sealed partial class CMelsec : CtrlThread, IDisposable
    {
#region 공유 상태·초기값
        private const int DefaultWriteTimeoutMs = 3000;
        private const int MelsecThreadDelayMs = 2;
        private const int MelsecNetMaximumTransferBytes = 1920;
        private const int MaximumStoredWriteStatusCount = 256;
        private const int MaximumWriteQueueCount = 128;
        private readonly CInterfaceManager _interfaceManager;
        private readonly CLogManager? _logManager;
        private readonly CMelsecNetApi _melsecNetApi;
        private readonly object _ioLock = new object ();
        private readonly object _mapLock = new object ();
        private readonly object _requestLock = new object ();
        private readonly object _writeLock = new object ();
        private readonly object _readStateLock = new object ();
        private readonly object _simulationLock = new object ();
        private readonly Dictionary<string, ushort> _simulationWords = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<CMelsecThreadRequest> _requestQueue = new Queue<CMelsecThreadRequest>();
        private readonly Queue<CMelsecWriteCommand> _writeQueue = new Queue<CMelsecWriteCommand>();
        private readonly Dictionary<int, CMelsecWriteCommand> _writeStatus = new Dictionary<int, CMelsecWriteCommand>();
        private readonly Dictionary<string, CMelsecReadSnapshot> _readSnapshots = new Dictionary<string, CMelsecReadSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, int> _melsecNetPaths = new Dictionary<int, int>();
        private Dictionary<string, ST_MELSEC_MAP_DATA> _map = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<ST_MELSEC_MAP_DATA> _mapSnapshot = Array.AsReadOnly(Array.Empty<ST_MELSEC_MAP_DATA>());
        private volatile bool _acceptRequests = true;
        private int _nextRequestNo;
        private CMelsecWriteCommand? _activeWriteCommand;
        private EN_MELSEC_PROCESS _process = EN_MELSEC_PROCESS.Ready;
        private EN_MELSEC_SIMULATION_READBACK _simulationReadback = EN_MELSEC_SIMULATION_READBACK.AutoEcho;
        private long _readCycleNo;
        private DateTimeOffset? _lastSuccessfulReadAt;
        private int _lastReadReturnCode;
        private int _consecutiveReadFailureCount;
        private bool _communicationAvailable;
        private const int DefaultPollingIntervalMs = 1000;
        private const int PollingFailureBackoffMs = 2000;
        private readonly object _pollLock = new();
        private readonly Dictionary<string, ST_MELSEC_CACHED_VALUE> _cachedValues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PollBlock> _pollBlockById = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, DateTimeOffset> _pollDeviceBackoff = new();
        private List<PollBlock> _pollBlocks = [];
        private readonly Queue<PollBlock> _requestedPollBlocks = new();
        private readonly HashSet<PollBlock> _requestedPollBlockSet = new();
        private bool _serveRequestedPoll = true;
        private int _pollGeneration;
        private int _nextPollBlock;
        public const string StageAlignBusyWordId = "STAGE_MCC_BUSY_FLAGS";
        public const string StageProtectWindowCountId = "PTW_TRAY_COUNT";
        public const string StageProtectWindowCompleteId = "PTW_EXCHANGE_SEQ_COMPLETE";
#endregion
#region 생성·초기화
        public CMelsec(CInterfaceManager interfaceManager, CLogManager? logManager = null, IReadOnlyList<ST_MELSEC_MAP_DATA>? map = null, CMelsecNetApi? melsecNetApi = null)
        {
            _interfaceManager = interfaceManager;
            _logManager = logManager;
            _melsecNetApi = melsecNetApi ?? new CMelsecNetApi();
            ReloadMap(map ?? []);
        }
#endregion

#region 기본 처리
        public EN_MELSEC_PROCESS Process
        {
            get
            {
                lock (_writeLock)
                {
                    return _process;
                }
            }
        }

        public long ReadCycleNo
        {
            get
            {
                lock (_readStateLock)
                {
                    return _readCycleNo;
                }
            }
        }

        public DateTimeOffset? LastSuccessfulReadAt
        {
            get
            {
                lock (_readStateLock)
                {
                    return _lastSuccessfulReadAt;
                }
            }
        }

        public int LastReadReturnCode
        {
            get
            {
                lock (_readStateLock)
                {
                    return _lastReadReturnCode;
                }
            }
        }

        public int ConsecutiveReadFailureCount
        {
            get
            {
                lock (_readStateLock)
                {
                    return _consecutiveReadFailureCount;
                }
            }
        }

        public bool IsCommunicationAvailable
        {
            get
            {
                lock (_readStateLock)
                {
                    return _communicationAvailable;
                }
            }
        }

        public void Initialize()
        {
            if (!IsRunning)
            {
                RestartPollingSchedule();
            }

            lock (_requestLock)
            {
                _acceptRequests = true;
            }

            Start(MelsecThreadDelayMs, "MELSEC_CONTROL");
        }

        public void DeInitialize()
        {
            lock (_requestLock)
            {
                _acceptRequests = false;
            }

            InvalidatePollingCache(EN_MELSEC_CACHE_STATE.Offline, "MELSEC communication is stopped.");
            CancelQueuedRequests("MELSEC control is stopping.");
            CancelQueuedWrites("MELSEC control is stopping.");
            CloseMelsecNetChannels();
            Stop();
            if (IsRunning)
            {
                Stop();
            }

            lock (_simulationLock)
            {
                _simulationWords.Clear();
                _simulationReadback = EN_MELSEC_SIMULATION_READBACK.AutoEcho;
            }

            lock (_readStateLock)
            {
                _readSnapshots.Clear();
                _readCycleNo = 0;
                _lastSuccessfulReadAt = null;
                _lastReadReturnCode = 0;
                _consecutiveReadFailureCount = 0;
                _communicationAvailable = false;
            }

            lock (_writeLock)
            {
                _activeWriteCommand = null;
                _process = EN_MELSEC_PROCESS.Ready;
            }
        }

        public override void Run()
        {
            CMelsecWriteCommand? activeCommand = GetActiveWriteCommand();
            if (activeCommand != null)
            {
                ProcessWriteCommand(activeCommand);
                PollNextCacheBlock();
                return;
            }

            CMelsecThreadRequest? request = GetThreadRequest();
            if (request != null)
            {
                ProcessThreadRequest(request);
                PollNextCacheBlock();
                return;
            }

            CMelsecWriteCommand? writeCommand = GetQueuedWriteCommand();
            if (writeCommand != null)
            {
                ActivateWriteCommand(writeCommand);
            }

            PollNextCacheBlock();
        }

        protected override void OnThreadError(Exception exception)
        {
            _logManager?.WriteInterfaceError(EN_EQP_MODULE.Melsec, "MELSEC_CONTROL", "[I/F][COMM_ERROR] THREAD", exception.Message);
            base.OnThreadError(exception);
        }

        public IReadOnlyList<ST_MELSEC_MAP_DATA> Map
        {
            // Metadata changes only on ReloadMap. Short control/UI reads must not sort
            // the entire map (including 500 IOF definitions) on every observation.
            get => Volatile.Read(ref _mapSnapshot);
        }

        public static void ValidateMapData(ST_MELSEC_MAP_DATA data)
        {
            if (!double.IsFinite(data.ReadScale) || data.ReadScale == 0 || !double.IsFinite(data.WriteScale) || data.WriteScale == 0)
            {
                throw new InvalidDataException("MELSEC READ_SCALE and WRITE_SCALE must be finite and nonzero: " + FormatMap(data));
            }

            ST_MELSEC_ADDRESS address = ParseAddress(data.Address);
            if (address.Number < 0)
            {
                throw new InvalidDataException("MELSECNET device number cannot be negative: " + FormatMap(data));
            }

            if (data.DataType == EN_MELSEC_DATA_TYPE.Bit)
            {
                RequireWordBit(address, data);
            }
            else if (address.BitIndex != null)
            {
                throw new InvalidDataException("MELSEC non-BIT address cannot include a bit index: " + FormatMap(data));
            }

            if ((data.DataType == EN_MELSEC_DATA_TYPE.DWord || data.DataType == EN_MELSEC_DATA_TYPE.Double || data.DataType == EN_MELSEC_DATA_TYPE.Float) && data.Length < 2)
            {
                throw new InvalidDataException("MELSEC 2-word data LENGTH must be at least 2: " + FormatMap(data));
            }
        }

        public void ReloadMap(IReadOnlyList<ST_MELSEC_MAP_DATA> map)
        {
            bool FilterData4(ST_MELSEC_MAP_DATA data)
            {
                return data.Use;
            }

            string HandleMap5(ST_MELSEC_MAP_DATA data)
            {
                return NormalizeId(data.Id);
            }

            Dictionary<string, ST_MELSEC_MAP_DATA> loadedMap = map.Where(FilterData4).ToDictionary(HandleMap5, StringComparer.OrdinalIgnoreCase);
            RebuildPollingMap(loadedMap);
        }

        public IReadOnlyList<ST_MELSEC_MAP_DATA> GetMapList(string group = "")
        {
            var normalizedGroup = group.Trim();
            bool FilterData6(ST_MELSEC_MAP_DATA data)
            {
                return string.IsNullOrWhiteSpace(normalizedGroup) || data.Group.Equals(normalizedGroup, StringComparison.OrdinalIgnoreCase);
            }

            return Map.Where(FilterData6).ToArray();
        }

        public ST_MELSEC_MAP_DATA GetMapData(string id)
        {
            var normalizedId = NormalizeId(id);
            string[] availableKeys;
            lock (_mapLock)
            {
                if (_map.TryGetValue(normalizedId, out var data))
                {
                    return data;
                }

                availableKeys = _map.Keys.ToArray();
            }

            string GetKeySortKey7(string key)
            {
                return key;
            }

            throw new InvalidOperationException($"MELSEC map was not registered: {id}. Available={string.Join(", ", availableKeys.OrderBy(GetKeySortKey7, StringComparer.OrdinalIgnoreCase))}");
        }

        public bool ReadBit(string id, CancellationToken cancellationToken = default)
        {
            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.ReadBit, id, null, cancellationToken);
            ExecuteThreadRequest(request);
            return request.Result is bool result && result;
        }

        public bool ReadBitNoLog(string id, CancellationToken cancellationToken = default)
        {
            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.ReadBit, id, null, cancellationToken, suppressLog: true);
            ExecuteThreadRequest(request);
            return request.Result is bool result && result;
        }

        public void Open(int deviceNo = 0, CancellationToken cancellationToken = default)
        {
            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.Open, "MELSEC_" + deviceNo.ToString(CultureInfo.InvariantCulture), deviceNo, cancellationToken);
            ExecuteThreadRequest(request);
        }

        public void WriteBit(string id, bool value, CancellationToken cancellationToken = default)
        {
            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.WriteBit, id, value, cancellationToken);
            ExecuteThreadRequest(request);
        }

        public double ReadWord(string id, CancellationToken cancellationToken = default)
        {
            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.ReadWord, id, null, cancellationToken);
            ExecuteThreadRequest(request);
            return (double)request.Result!;
        }

        public double ReadWordNoLog(string id, CancellationToken cancellationToken = default)
        {
            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.ReadWord, id, null, cancellationToken, suppressLog: true);
            ExecuteThreadRequest(request);
            return (double)request.Result!;
        }

        public void WriteWord(string id, double value, CancellationToken cancellationToken = default)
        {
            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.WriteWord, id, value, cancellationToken);
            ExecuteThreadRequest(request);
        }

        public double ReadDouble(string id, CancellationToken cancellationToken = default)
        {
            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.ReadDouble, id, null, cancellationToken);
            ExecuteThreadRequest(request);
            return request.Result is double result ? result : 0.0;
        }

        public double ReadDoubleNoLog(string id, CancellationToken cancellationToken = default)
        {
            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.ReadDouble, id, null, cancellationToken, suppressLog: true);
            ExecuteThreadRequest(request);
            return request.Result is double result ? result : 0.0;
        }

        public void WriteDouble(string id, double value, CancellationToken cancellationToken = default)
        {
            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.WriteDouble, id, value, cancellationToken);
            ExecuteThreadRequest(request);
        }

        public string ReadString(string id, CancellationToken cancellationToken = default)
        {
            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.ReadString, id, null, cancellationToken);
            ExecuteThreadRequest(request);
            return request.Result as string ?? "";
        }

        public string ReadStringNoLog(string id, CancellationToken cancellationToken = default)
        {
            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.ReadString, id, null, cancellationToken, suppressLog: true);
            ExecuteThreadRequest(request);
            return request.Result as string ?? "";
        }

        public void WriteString(string id, string value, CancellationToken cancellationToken = default)
        {
            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.WriteString, id, value, cancellationToken);
            ExecuteThreadRequest(request);
        }

        public int QueueWriteBit(string writeId, bool value, string readbackId = "", int timeoutMs = 0, int retryCount = -1)
        {
            return QueueWriteCommand(EN_MELSEC_DATA_TYPE.Bit, writeId, readbackId, value, timeoutMs, retryCount);
        }

        public int QueueWriteWord(string writeId, double value, string readbackId = "", int timeoutMs = 0, int retryCount = -1)
        {
            return QueueWriteCommand(EN_MELSEC_DATA_TYPE.Word, writeId, readbackId, value, timeoutMs, retryCount);
        }

        public int QueueWriteDouble(string writeId, double value, string readbackId = "", int timeoutMs = 0, int retryCount = -1)
        {
            return QueueWriteCommand(EN_MELSEC_DATA_TYPE.Double, writeId, readbackId, value, timeoutMs, retryCount);
        }

        public int QueueWriteString(string writeId, string value, string readbackId = "", int timeoutMs = 0, int retryCount = -1)
        {
            return QueueWriteCommand(EN_MELSEC_DATA_TYPE.String, writeId, readbackId, value, timeoutMs, retryCount);
        }

        public EN_MELSEC_WRITE_RESULT GetWriteResult(int requestNo)
        {
            ST_MELSEC_WRITE_STATUS? status = GetWriteStatus(requestNo);
            return status == null ? EN_MELSEC_WRITE_RESULT.InvalidParameter : status.Result;
        }

        public ST_MELSEC_WRITE_STATUS? GetWriteStatus(int requestNo)
        {
            lock (_writeLock)
            {
                if (!_writeStatus.TryGetValue(requestNo, out CMelsecWriteCommand? command))
                {
                    return null;
                }

                return command.CreateStatus();
            }
        }

        public bool TryGetReadSnapshot(string id, out object? value, out long readCycleNo)
        {
            lock (_readStateLock)
            {
                if (_readSnapshots.TryGetValue(NormalizeId(id), out CMelsecReadSnapshot? snapshot))
                {
                    value = snapshot.Value;
                    readCycleNo = snapshot.ReadCycleNo;
                    return true;
                }
            }

            value = null;
            readCycleNo = 0;
            return false;
        }

        public void SetSimulationReadbackMode(EN_MELSEC_SIMULATION_READBACK mode)
        {
            lock (_simulationLock)
            {
                _simulationReadback = mode;
            }
        }

        public new void Dispose()
        {
            DeInitialize();
            base.Dispose();
        }

        private bool ReadBitCore(string id, CancellationToken cancellationToken, bool suppressLog = false)
        {
            ST_MELSEC_MAP_DATA data = PrepareRead(id, EN_MELSEC_DATA_TYPE.Bit, cancellationToken);
            ST_MELSEC_ADDRESS address = ParseAddress(data.Address);
            int bitIndex = RequireWordBit(address, data);
            ushort[] words = ReadWords(data, address, 1, cancellationToken, suppressLog);
            bool value = (words[0] & (1 << bitIndex)) != 0;
            SaveReadSnapshot(data.Id, value);
            return value;
        }

        private void WriteBitCore(string id, bool value, CancellationToken cancellationToken)
        {
            ST_MELSEC_MAP_DATA data = PrepareWrite(id, EN_MELSEC_DATA_TYPE.Bit, cancellationToken);
            ST_MELSEC_ADDRESS address = ParseAddress(data.Address);
            int bitIndex = RequireWordBit(address, data);
            ushort[] words = ReadWords(data, address, 1, cancellationToken);
            ushort mask = (ushort)(1 << bitIndex);
            if (value)
            {
                words[0] = (ushort)(words[0] | mask);
            }
            else
            {
                words[0] = (ushort)(words[0] & ~mask);
            }

            WriteWords(data, address, words, cancellationToken);
        }

        private double ReadWordCore(string id, CancellationToken cancellationToken, bool suppressLog = false)
        {
            ST_MELSEC_MAP_DATA data = PrepareRead(id, [EN_MELSEC_DATA_TYPE.Word, EN_MELSEC_DATA_TYPE.DWord], cancellationToken);
            int wordCount = data.DataType == EN_MELSEC_DATA_TYPE.DWord ? Math.Max(2, data.Length) : 1;
            ushort[] words = ReadWords(data, ParseAddress(data.Address), wordCount, cancellationToken, suppressLog);
            int rawValue = data.DataType == EN_MELSEC_DATA_TYPE.DWord ? WordsToInt32(words) : words[0];
            double value = rawValue * data.ReadScale;
            SaveReadSnapshot(data.Id, value);
            return value;
        }

        private void WriteWordCore(string id, double value, CancellationToken cancellationToken)
        {
            ST_MELSEC_MAP_DATA data = PrepareWrite(id, [EN_MELSEC_DATA_TYPE.Word, EN_MELSEC_DATA_TYPE.DWord], cancellationToken);
            int wordCount = data.DataType == EN_MELSEC_DATA_TYPE.DWord ? Math.Max(2, data.Length) : 1;
            int rawValue = EncodeIntegerValue(data, value * data.WriteScale);
            ushort[] words = data.DataType == EN_MELSEC_DATA_TYPE.DWord ? Int32ToWords(rawValue, wordCount) : [(ushort)rawValue];
            WriteWords(data, ParseAddress(data.Address), words, cancellationToken);
        }

        private static int EncodeIntegerValue(ST_MELSEC_MAP_DATA data, double rawValue)
        {
            var rounded = Math.Round(rawValue, MidpointRounding.AwayFromZero);
            if (!double.IsFinite(rounded) || rounded < (data.DataType == EN_MELSEC_DATA_TYPE.Word ? 0 : int.MinValue) || rounded > (data.DataType == EN_MELSEC_DATA_TYPE.Word ? ushort.MaxValue : int.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(rawValue), "Scaled MELSEC value must fit the configured integer type.");
            return (int)rounded;
        }

        private double ReadDoubleCore(string id, CancellationToken cancellationToken, bool suppressLog = false)
        {
            ST_MELSEC_MAP_DATA data = PrepareRead(id, [EN_MELSEC_DATA_TYPE.Double, EN_MELSEC_DATA_TYPE.Float], cancellationToken);
            ValidateMapData(data);
            // Scalar APIs expose the first 32-bit value. LENGTH may describe the
            // full IOF array capacity; only ReadDoubleArray reads that entire area.
            int wordCount = 2;
            ushort[] words = ReadWords(data, ParseAddress(data.Address), wordCount, cancellationToken, suppressLog);
            double value;
            if (data.DataType == EN_MELSEC_DATA_TYPE.Float)
            {
                value = WordsToFloat(words) * data.ReadScale;
            }
            else
            {
                value = WordsToInt32(words) * data.ReadScale;
            }

            SaveReadSnapshot(data.Id, value);
            return value;
        }

        private void WriteDoubleCore(string id, double value, CancellationToken cancellationToken)
        {
            ST_MELSEC_MAP_DATA data = PrepareWrite(id, [EN_MELSEC_DATA_TYPE.Double, EN_MELSEC_DATA_TYPE.Float], cancellationToken);
            ValidateMapData(data);
            int wordCount = 2; // A scalar write must not clear adjacent IOF array entries.
            double rawValue = value * data.WriteScale;
            if (!double.IsFinite(rawValue) || (data.DataType == EN_MELSEC_DATA_TYPE.Float && Math.Abs(rawValue) > float.MaxValue))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Scaled MELSEC value must fit the configured numeric type.");
            }

            ushort[] words = data.DataType == EN_MELSEC_DATA_TYPE.Float ? FloatToWords((float)rawValue, wordCount) : Int32ToWords(checked((int)Math.Round(rawValue, MidpointRounding.AwayFromZero)), wordCount);
            WriteWords(data, ParseAddress(data.Address), words, cancellationToken);
        }

        private string ReadStringCore(string id, CancellationToken cancellationToken, bool suppressLog = false)
        {
            ST_MELSEC_MAP_DATA data = PrepareRead(id, EN_MELSEC_DATA_TYPE.String, cancellationToken);
            ushort[] words = ReadWords(data, ParseAddress(data.Address), data.Length, cancellationToken, suppressLog);
            byte[] bytes = new byte[words.Length * 2];
            for (int index = 0; index < words.Length; index++)
            {
                bytes[index * 2] = (byte)(words[index] & 0xFF);
                bytes[index * 2 + 1] = (byte)(words[index] >> 8);
            }

            string value = Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
            SaveReadSnapshot(data.Id, value);
            return value;
        }

        private void WriteStringCore(string id, string value, CancellationToken cancellationToken)
        {
            ST_MELSEC_MAP_DATA data = PrepareWrite(id, EN_MELSEC_DATA_TYPE.String, cancellationToken);
            int byteLength = data.Length * 2;
            byte[] sourceBytes = Encoding.ASCII.GetBytes(value);
            byte[] bytes = Enumerable.Repeat((byte)' ', byteLength).ToArray();
            Array.Copy(sourceBytes, bytes, Math.Min(sourceBytes.Length, bytes.Length));
            ushort[] words = new ushort[data.Length];
            for (int index = 0; index < words.Length; index++)
            {
                words[index] = (ushort)(bytes[index * 2] | (bytes[index * 2 + 1] << 8));
            }

            WriteWords(data, ParseAddress(data.Address), words, cancellationToken);
        }

        private void ExecuteThreadRequest(CMelsecThreadRequest request)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            lock (_requestLock)
            {
                if (!_acceptRequests)
                {
                    throw new InvalidOperationException("MELSEC control is not accepting requests.");
                }

                _requestQueue.Enqueue(request);
            }

            Initialize();
            while (!request.Completed.WaitOne(20))
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                if (!IsRunning)
                {
                    throw new InvalidOperationException("MELSEC control thread stopped before request completion: " + request.Id);
                }
            }

            if (request.Error != null)
            {
                ExceptionDispatchInfo.Capture(request.Error).Throw();
            }
        }

        private CMelsecThreadRequest? GetThreadRequest()
        {
            lock (_requestLock)
            {
                if (_requestQueue.Count == 0)
                {
                    return null;
                }

                return _requestQueue.Dequeue();
            }
        }

        private void ProcessThreadRequest(CMelsecThreadRequest request)
        {
            try
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                switch (request.Command)
                {
                    case EN_MELSEC_THREAD_COMMAND.Open:
                        OpenCore((int)request.Value!, request.CancellationToken);
                        break;
                    case EN_MELSEC_THREAD_COMMAND.Close:
                        if (request.Value is int deviceNo)
                            CloseDeviceCore(deviceNo);
                        else
                            CloseMelsecNetChannelsCore();
                        break;
                    case EN_MELSEC_THREAD_COMMAND.ReadBit:
                        request.Result = ReadBitCore(request.Id, request.CancellationToken, request.SuppressLog);
                        break;
                    case EN_MELSEC_THREAD_COMMAND.WriteBit:
                        WriteBitCore(request.Id, (bool)request.Value!, request.CancellationToken);
                        break;
                    case EN_MELSEC_THREAD_COMMAND.ReadWord:
                        request.Result = ReadWordCore(request.Id, request.CancellationToken, request.SuppressLog);
                        break;
                    case EN_MELSEC_THREAD_COMMAND.WriteWord:
                        WriteWordCore(request.Id, (double)request.Value!, request.CancellationToken);
                        break;
                    case EN_MELSEC_THREAD_COMMAND.ReadDouble:
                        request.Result = ReadDoubleCore(request.Id, request.CancellationToken, request.SuppressLog);
                        break;
                    case EN_MELSEC_THREAD_COMMAND.WriteDouble:
                        WriteDoubleCore(request.Id, (double)request.Value!, request.CancellationToken);
                        break;
                    case EN_MELSEC_THREAD_COMMAND.WriteDoubleArray:
                        WriteDoubleArrayCore(request.Id, (double[])request.Value!, request.CancellationToken);
                        break;
                    case EN_MELSEC_THREAD_COMMAND.ReadDoubleArray:
                        request.Result = ReadDoubleArrayCore(request.Id, (int)request.Value!, request.CancellationToken);
                        break;
                    case EN_MELSEC_THREAD_COMMAND.ReadString:
                        request.Result = ReadStringCore(request.Id, request.CancellationToken, request.SuppressLog);
                        break;
                    case EN_MELSEC_THREAD_COMMAND.WriteString:
                        WriteStringCore(request.Id, (string)request.Value!, request.CancellationToken);
                        break;
                }
            }
            catch (Exception exception)
            {
                request.Error = exception;
            }
            finally
            {
                request.Completed.Set();
            }
        }

        private void OpenCore(int deviceNo, CancellationToken cancellationToken)
        {
            ST_INTERFACE_DATA interfaceData = _interfaceManager.GetInterfaceData(EN_EQP_MODULE.Melsec, deviceNo) ?? throw new InvalidOperationException("MELSEC interface is not configured: MELSEC_" + deviceNo.ToString(CultureInfo.InvariantCulture));
            if (IsSimulation(interfaceData))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RegisterCommunicationSuccess();
                RestartPollingDevice(deviceNo);
                return;
            }

            ST_MELSEC_NET_OPTION option = ReadMelsecNetOption(interfaceData);
            lock (_ioLock)
            {
                if (!_melsecNetPaths.ContainsKey(deviceNo))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int path;
                    int returnCode;
                    try
                    {
                        returnCode = _melsecNetApi.Open(option.ChannelNo, out path);
                    }
                    catch (Exception exception)when (IsMelsecNetRuntimeException(exception))
                    {
                        throw CreateMelsecNetRuntimeException("mdOpen", exception);
                    }

                    if (returnCode != 0)
                    {
                        throw CreateMelsecNetReturnCodeException("mdOpen", returnCode, interfaceData, option, 0, 0);
                    }

                    _melsecNetPaths[deviceNo] = path;
                    WriteMelsecNetOpenLog(interfaceData, option, path);
                }
            }

            try
            {
                ProbeDeviceRead(deviceNo, cancellationToken);
                RegisterCommunicationSuccess();
                RestartPollingDevice(deviceNo);
            }
            catch
            {
                CloseMelsecNetChannelCore(deviceNo, interfaceData, option);
                throw;
            }
        }

        private void CloseMelsecNetChannels()
        {
            if (!IsRunning)
            {
                return;
            }

            CMelsecThreadRequest request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.Close, "", null, CancellationToken.None);
            lock (_requestLock)
            {
                _requestQueue.Enqueue(request);
            }

            if (!request.Completed.WaitOne(3000))
            {
                _logManager?.WriteInterfaceError(EN_EQP_MODULE.Melsec, "MELSEC_CONTROL", "[I/F][COMM_ERROR] mdClose", "MELSECNET close request did not complete within 3000 ms.");
                return;
            }

            if (request.Error != null)
            {
                _logManager?.WriteInterfaceError(EN_EQP_MODULE.Melsec, "MELSEC_CONTROL", "[I/F][COMM_ERROR] mdClose", request.Error.Message);
            }
        }

        private void CloseMelsecNetChannelsCore()
        {
            KeyValuePair<int, int>[] paths;
            lock (_ioLock)
            {
                paths = _melsecNetPaths.ToArray();
            }

            for (int index = 0; index < paths.Length; index++)
            {
                int deviceNo = paths[index].Key;
                ST_INTERFACE_DATA? interfaceData = _interfaceManager.GetInterfaceData(EN_EQP_MODULE.Melsec, deviceNo);
                if (interfaceData == null)
                {
                    continue;
                }

                ST_MELSEC_NET_OPTION option = ReadMelsecNetOption(interfaceData);
                CloseMelsecNetChannelCore(deviceNo, interfaceData, option);
            }
        }

        private void CloseMelsecNetChannelCore(int deviceNo, ST_INTERFACE_DATA interfaceData, ST_MELSEC_NET_OPTION option)
        {
            int path;
            lock (_ioLock)
            {
                if (!_melsecNetPaths.TryGetValue(deviceNo, out path))
                {
                    return;
                }

                _melsecNetPaths.Remove(deviceNo);
            }

            int returnCode;
            try
            {
                returnCode = _melsecNetApi.Close(path);
            }
            catch (Exception exception)when (IsMelsecNetRuntimeException(exception))
            {
                throw CreateMelsecNetRuntimeException("mdClose", exception);
            }

            if (returnCode != 0)
            {
                throw CreateMelsecNetReturnCodeException("mdClose", returnCode, interfaceData, option, path, 0);
            }

            _logManager?.WriteInterfaceCommand(EN_EQP_MODULE.Melsec, interfaceData.NickName, "MELSECNET:mdClose", "OK", FormatMelsecNetContext(option, path, 0, 0));
        }

        private void ProbeDeviceRead(int deviceNo, CancellationToken cancellationToken)
        {
            IReadOnlyList<ST_MELSEC_MAP_DATA> map = Map;
            for (int index = 0; index < map.Count; index++)
            {
                ST_MELSEC_MAP_DATA data = map[index];
                if (data.DeviceNo != deviceNo || data.Access == EN_MELSEC_ACCESS.Write)
                {
                    continue;
                }

                switch (data.DataType)
                {
                    case EN_MELSEC_DATA_TYPE.Bit:
                        ReadBitCore(data.Id, cancellationToken);
                        return;
                    case EN_MELSEC_DATA_TYPE.Word:
                    case EN_MELSEC_DATA_TYPE.DWord:
                        ReadWordCore(data.Id, cancellationToken);
                        return;
                    case EN_MELSEC_DATA_TYPE.Double:
                    case EN_MELSEC_DATA_TYPE.Float:
                        ReadDoubleCore(data.Id, cancellationToken);
                        return;
                    case EN_MELSEC_DATA_TYPE.String:
                        ReadStringCore(data.Id, cancellationToken);
                        return;
                }
            }

            throw new InvalidOperationException("MELSEC initial read map is not configured for device " + deviceNo.ToString(CultureInfo.InvariantCulture) + ".");
        }

        private void CancelQueuedRequests(string message)
        {
            lock (_requestLock)
            {
                while (_requestQueue.Count > 0)
                {
                    CMelsecThreadRequest request = _requestQueue.Dequeue();
                    request.Error = new OperationCanceledException(message);
                    request.Completed.Set();
                }
            }
        }

        private int QueueWriteCommand(EN_MELSEC_DATA_TYPE requestedDataType, string writeId, string readbackId, object expectedValue, int timeoutMs, int retryCount)
        {
            int requestNo;
            lock (_writeLock)
            {
                _nextRequestNo++;
                if (_nextRequestNo <= 0)
                {
                    _nextRequestNo = 1;
                }

                requestNo = _nextRequestNo;
            }

            CMelsecWriteCommand command = new CMelsecWriteCommand(requestNo, NormalizeId(writeId), NormalizeId(readbackId), requestedDataType, expectedValue);
            try
            {
                PrepareQueuedWriteCommand(command, timeoutMs, retryCount);
            }
            catch (Exception exception)
            {
                command.Result = EN_MELSEC_WRITE_RESULT.InvalidParameter;
                command.ErrorMessage = exception.Message;
                StoreWriteStatus(command);
                WriteHandshakeError(command, "[I/F][COMM_ERROR]", exception.Message);
                return requestNo;
            }

            lock (_requestLock)
            {
                if (!_acceptRequests)
                {
                    command.Result = EN_MELSEC_WRITE_RESULT.Cancelled;
                    command.ErrorMessage = "MELSEC control is not accepting requests.";
                    StoreWriteStatus(command);
                    return requestNo;
                }
            }

            if (command.WriteData != null)
            {
                ST_INTERFACE_DATA interfaceData = GetInterfaceData(command.WriteData);
                if (!IsSimulation(interfaceData) && !_interfaceManager.IsConnect(EN_EQP_MODULE.Melsec, interfaceData.Number))
                {
                    command.Result = EN_MELSEC_WRITE_RESULT.CommunicationError;
                    command.ErrorMessage = "MELSEC communication is offline.";
                    StoreWriteStatus(command);
                    WriteHandshakeError(command, "[I/F][COMM_ERROR]", command.ErrorMessage);
                    return requestNo;
                }
            }

            lock (_writeLock)
            {
                int duplicateRequestNo = FindPendingDuplicateRequest(command);
                if (duplicateRequestNo > 0)
                {
                    return duplicateRequestNo;
                }

                if (_writeQueue.Count >= MaximumWriteQueueCount)
                {
                    command.Result = EN_MELSEC_WRITE_RESULT.CommunicationError;
                    command.ErrorMessage = "MELSEC write queue reached its limit.";
                    _writeStatus[requestNo] = command;
                    WriteHandshakeError(command, "[I/F][COMM_ERROR]", command.ErrorMessage);
                    return requestNo;
                }

                command.Result = EN_MELSEC_WRITE_RESULT.Queued;
                _writeStatus[requestNo] = command;
                _writeQueue.Enqueue(command);
            }

            Initialize();
            WriteHandshakeLog(command, "[I/F][SEND]", "Write request queued.");
            return requestNo;
        }

        private int FindPendingDuplicateRequest(CMelsecWriteCommand command)
        {
            if (_activeWriteCommand != null && IsSameWriteRequest(_activeWriteCommand, command))
            {
                return _activeWriteCommand.RequestNo;
            }

            foreach (CMelsecWriteCommand queuedCommand in _writeQueue)
            {
                if (IsSameWriteRequest(queuedCommand, command))
                {
                    return queuedCommand.RequestNo;
                }
            }

            return 0;
        }

        private static bool IsSameWriteRequest(CMelsecWriteCommand left, CMelsecWriteCommand right)
        {
            return left.WriteId.Equals(right.WriteId, StringComparison.OrdinalIgnoreCase) && left.ReadbackId.Equals(right.ReadbackId, StringComparison.OrdinalIgnoreCase) && left.RequestedDataType == right.RequestedDataType && FormatCommandValue(left.ExpectedValue).Equals(FormatCommandValue(right.ExpectedValue), StringComparison.Ordinal);
        }

        private void PrepareQueuedWriteCommand(CMelsecWriteCommand command, int timeoutMs, int retryCount)
        {
            ST_MELSEC_MAP_DATA writeData = GetMapData(command.WriteId);
            ValidateRequestedDataType(writeData, command.RequestedDataType, true);
            PrepareWrite(writeData.Id, GetAcceptedDataTypes(command.RequestedDataType), CancellationToken.None);
            if (string.IsNullOrWhiteSpace(command.ReadbackId))
            {
                command.ReadbackId = ResolveReadbackId(writeData);
            }

            ST_MELSEC_MAP_DATA readbackData = GetMapData(command.ReadbackId);
            ValidateRequestedDataType(readbackData, command.RequestedDataType, false);
            PrepareRead(readbackData.Id, GetAcceptedDataTypes(command.RequestedDataType), CancellationToken.None);
            if (writeData.DeviceNo != readbackData.DeviceNo)
            {
                throw new InvalidOperationException("MELSEC write/readback DEVICE NO does not match: " + writeData.Id + " / " + readbackData.Id);
            }

            ST_INTERFACE_DATA interfaceData = GetInterfaceData(writeData);
            ST_INTERFACE_CONNECT_OPTION option = CInterfaceConnectOption.Parse(interfaceData);
            command.TimeoutMs = timeoutMs > 0 ? timeoutMs : Math.Max(1, option.TimeoutMs > 0 ? option.TimeoutMs : DefaultWriteTimeoutMs);
            command.RetryCount = retryCount >= 0 ? retryCount : Math.Max(0, option.RetryCount);
            command.WriteData = writeData;
            command.ReadbackData = readbackData;
        }

        private string ResolveReadbackId(ST_MELSEC_MAP_DATA writeData)
        {
            const string writeSuffix = "_WRITE";
            if (writeData.Id.EndsWith(writeSuffix, StringComparison.OrdinalIgnoreCase))
            {
                string candidate = writeData.Id[..^writeSuffix.Length] + "_READ";
                bool containsReadback;
                lock (_mapLock)
                {
                    containsReadback = _map.ContainsKey(candidate);
                }

                if (containsReadback)
                {
                    return candidate;
                }
            }

            if (writeData.Access == EN_MELSEC_ACCESS.ReadWrite)
            {
                return writeData.Id;
            }

            throw new InvalidOperationException("MELSEC readback ID is not defined for write ID: " + writeData.Id);
        }

        private static IReadOnlyList<EN_MELSEC_DATA_TYPE> GetAcceptedDataTypes(EN_MELSEC_DATA_TYPE requestedDataType)
        {
            switch (requestedDataType)
            {
                case EN_MELSEC_DATA_TYPE.Word:
                    return[EN_MELSEC_DATA_TYPE.Word, EN_MELSEC_DATA_TYPE.DWord];
                case EN_MELSEC_DATA_TYPE.Double:
                    return[EN_MELSEC_DATA_TYPE.Double, EN_MELSEC_DATA_TYPE.Float];
                default:
                    return[requestedDataType];
            }
        }

        private static void ValidateRequestedDataType(ST_MELSEC_MAP_DATA data, EN_MELSEC_DATA_TYPE requestedDataType, bool isWrite)
        {
            IReadOnlyList<EN_MELSEC_DATA_TYPE> acceptedDataTypes = GetAcceptedDataTypes(requestedDataType);
            if (acceptedDataTypes.Contains(data.DataType))
            {
                return;
            }

            string operation = isWrite ? "write" : "readback";
            throw new InvalidOperationException("MELSEC " + operation + " data type mismatch: " + FormatMap(data) + ", Requested=" + requestedDataType);
        }

        private void StoreWriteStatus(CMelsecWriteCommand command)
        {
            lock (_writeLock)
            {
                _writeStatus[command.RequestNo] = command;
                TrimWriteStatus();
            }
        }

        private CMelsecWriteCommand? GetActiveWriteCommand()
        {
            lock (_writeLock)
            {
                return _activeWriteCommand;
            }
        }

        private CMelsecWriteCommand? GetQueuedWriteCommand()
        {
            lock (_writeLock)
            {
                if (_writeQueue.Count == 0)
                {
                    return null;
                }

                return _writeQueue.Dequeue();
            }
        }

        private void ActivateWriteCommand(CMelsecWriteCommand command)
        {
            lock (_writeLock)
            {
                if (command.CancelRequested)
                {
                    command.Result = EN_MELSEC_WRITE_RESULT.Cancelled;
                    return;
                }

                command.WriteBeforeReadCycle = ReadCycleNo;
                command.Result = EN_MELSEC_WRITE_RESULT.Writing;
                _activeWriteCommand = command;
                _process = EN_MELSEC_PROCESS.PrepareWrite;
            }
        }

        private void ProcessWriteCommand(CMelsecWriteCommand command)
        {
            EN_MELSEC_PROCESS process;
            lock (_writeLock)
            {
                if (command.CancelRequested)
                {
                    CompleteCancelledWrite(command);
                    return;
                }

                process = _process;
            }

            switch (process)
            {
                case EN_MELSEC_PROCESS.PrepareWrite:
                    PrepareActiveWrite(command);
                    break;
                case EN_MELSEC_PROCESS.Write:
                    ExecuteActiveWrite(command);
                    break;
                case EN_MELSEC_PROCESS.WaitReadback:
                    CheckActiveWriteReadback(command);
                    break;
                case EN_MELSEC_PROCESS.RetryWrite:
                    BeginRetryWrite(command);
                    break;
                case EN_MELSEC_PROCESS.CommunicationError:
                    CompleteCommunicationError(command);
                    break;
                case EN_MELSEC_PROCESS.Close:
                    CompleteCancelledWrite(command);
                    break;
            }
        }

        private void PrepareActiveWrite(CMelsecWriteCommand command)
        {
            if (command.WriteData == null)
            {
                CompleteInvalidWrite(command, "MELSEC write map is not available.");
                return;
            }

            ST_INTERFACE_DATA interfaceData = GetInterfaceData(command.WriteData);
            if (!IsSimulation(interfaceData) && !_interfaceManager.IsConnect(EN_EQP_MODULE.Melsec, interfaceData.Number))
            {
                CompleteCommunicationError(command, "MELSEC communication is offline.");
                return;
            }

            lock (_writeLock)
            {
                _process = EN_MELSEC_PROCESS.Write;
            }
        }

        private void ExecuteActiveWrite(CMelsecWriteCommand command)
        {
            try
            {
                WriteCommandValue(command);
                if (command.CancelRequested)
                {
                    CompleteCancelledWrite(command);
                    return;
                }

                long minimumReadCycle = ReadCycleNo + 1;
                lock (_writeLock)
                {
                    command.Result = EN_MELSEC_WRITE_RESULT.WriteSuccess;
                    command.MinimumReadCycle = minimumReadCycle;
                    command.AttemptStartedAt = DateTimeOffset.UtcNow;
                    command.NextReadAt = DateTimeOffset.UtcNow;
                }

                ApplySimulationReadback(command);
                WriteHandshakeLog(command, "[I/F][WRITE_OK]", "PLC write response completed.");
                lock (_writeLock)
                {
                    command.Result = EN_MELSEC_WRITE_RESULT.WaitReadback;
                    _process = EN_MELSEC_PROCESS.WaitReadback;
                }
            }
            catch (Exception exception)when (exception is not OperationCanceledException)
            {
                if (command.CancelRequested)
                {
                    CompleteCancelledWrite(command);
                    return;
                }

                RegisterCommunicationFailure();
                RetryOrCompleteCommunicationError(command, exception.Message);
            }
        }

        private void CheckActiveWriteReadback(CMelsecWriteCommand command)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (_writeLock)
            {
                if (now < command.NextReadAt)
                {
                    return;
                }
            }

            try
            {
                if (GetSimulationReadbackMode() == EN_MELSEC_SIMULATION_READBACK.CommunicationError && command.WriteData != null && IsSimulation(GetInterfaceData(command.WriteData)))
                {
                    RegisterReadFailure(-1);
                    throw new IOException("[SIMULATION] MELSEC readback communication error.");
                }

                object actualValue = ReadCommandValue(command);
                long confirmReadCycle = ReadCycleNo;
                string actualText = FormatCommandValue(actualValue);
                lock (_writeLock)
                {
                    command.ActualValue = actualText;
                    command.ConfirmReadCycle = confirmReadCycle;
                }

                if (confirmReadCycle >= command.MinimumReadCycle && IsExpectedValue(command, actualValue))
                {
                    lock (_writeLock)
                    {
                        command.Result = EN_MELSEC_WRITE_RESULT.Confirmed;
                        command.ErrorMessage = "";
                    }

                    WriteHandshakeLog(command, "[I/F][CONFIRM]", "Readback matched after new read cycle " + confirmReadCycle.ToString(CultureInfo.InvariantCulture) + ".");
                    WriteHandshakeLog(command, "[I/F][COMPLETE]", "Write-confirm completed.");
                    CompleteActiveWrite(command);
                    return;
                }

                int pollMs = command.ReadbackData == null ? MelsecThreadDelayMs : Math.Max(MelsecThreadDelayMs, command.ReadbackData.PollMs);
                lock (_writeLock)
                {
                    command.NextReadAt = now.AddMilliseconds(pollMs);
                }

                if (IsWriteAttemptTimedOut(command, now))
                {
                    RetryOrCompleteTimeout(command);
                }
            }
            catch (Exception exception)when (exception is not OperationCanceledException)
            {
                if (command.CancelRequested)
                {
                    CompleteCancelledWrite(command);
                    return;
                }

                RetryOrCompleteCommunicationError(command, exception.Message);
            }
        }

        private void BeginRetryWrite(CMelsecWriteCommand command)
        {
            lock (_writeLock)
            {
                command.Result = EN_MELSEC_WRITE_RESULT.Writing;
                _process = EN_MELSEC_PROCESS.Write;
            }
        }

        private void RetryOrCompleteTimeout(CMelsecWriteCommand command)
        {
            if (TryScheduleRetry(command, "Readback did not match before timeout."))
            {
                return;
            }

            lock (_writeLock)
            {
                command.Result = EN_MELSEC_WRITE_RESULT.Timeout;
                command.ErrorMessage = "MELSEC readback timeout.";
            }

            WriteHandshakeError(command, "[I/F][TIMEOUT]", "Readback mismatch. TimeoutMs=" + command.TimeoutMs.ToString(CultureInfo.InvariantCulture));
            CompleteActiveWrite(command);
        }

        private void RetryOrCompleteCommunicationError(CMelsecWriteCommand command, string errorMessage)
        {
            if (TryScheduleRetry(command, errorMessage))
            {
                return;
            }

            CompleteCommunicationError(command, errorMessage);
        }

        private bool TryScheduleRetry(CMelsecWriteCommand command, string reason)
        {
            lock (_writeLock)
            {
                if (command.CurrentRetryCount >= command.RetryCount)
                {
                    return false;
                }

                command.CurrentRetryCount++;
                command.ErrorMessage = reason;
                command.Result = EN_MELSEC_WRITE_RESULT.Queued;
                _process = EN_MELSEC_PROCESS.RetryWrite;
            }

            WriteHandshakeLog(command, "[I/F][RETRY]", reason + " Retry=" + command.CurrentRetryCount.ToString(CultureInfo.InvariantCulture) + "/" + command.RetryCount.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        private bool IsWriteAttemptTimedOut(CMelsecWriteCommand command, DateTimeOffset now)
        {
            lock (_writeLock)
            {
                return now - command.AttemptStartedAt >= TimeSpan.FromMilliseconds(command.TimeoutMs);
            }
        }

        private void CompleteCommunicationError(CMelsecWriteCommand command)
        {
            CompleteCommunicationError(command, command.ErrorMessage);
        }

        private void CompleteCommunicationError(CMelsecWriteCommand command, string message)
        {
            lock (_writeLock)
            {
                command.Result = EN_MELSEC_WRITE_RESULT.CommunicationError;
                command.ErrorMessage = message;
                _process = EN_MELSEC_PROCESS.CommunicationError;
            }

            WriteHandshakeError(command, "[I/F][COMM_ERROR]", message);
            CompleteActiveWrite(command);
        }

        private void CompleteInvalidWrite(CMelsecWriteCommand command, string message)
        {
            lock (_writeLock)
            {
                command.Result = EN_MELSEC_WRITE_RESULT.InvalidParameter;
                command.ErrorMessage = message;
            }

            WriteHandshakeError(command, "[I/F][COMM_ERROR]", message);
            CompleteActiveWrite(command);
        }

        private void CompleteCancelledWrite(CMelsecWriteCommand command)
        {
            lock (_writeLock)
            {
                command.Result = EN_MELSEC_WRITE_RESULT.Cancelled;
                if (string.IsNullOrWhiteSpace(command.ErrorMessage))
                {
                    command.ErrorMessage = "MELSEC write was cancelled.";
                }
            }

            CompleteActiveWrite(command);
        }

        private void CompleteActiveWrite(CMelsecWriteCommand command)
        {
            lock (_writeLock)
            {
                if (_activeWriteCommand == command)
                {
                    _activeWriteCommand = null;
                }

                _process = EN_MELSEC_PROCESS.Ready;
                TrimWriteStatus();
            }
        }

        private void CancelQueuedWrites(string message)
        {
            lock (_writeLock)
            {
                while (_writeQueue.Count > 0)
                {
                    CMelsecWriteCommand command = _writeQueue.Dequeue();
                    command.CancelRequested = true;
                    command.Result = EN_MELSEC_WRITE_RESULT.Cancelled;
                    command.ErrorMessage = message;
                }

                if (_activeWriteCommand != null)
                {
                    _activeWriteCommand.CancelRequested = true;
                    _activeWriteCommand.ErrorMessage = message;
                    _activeWriteCommand.Result = EN_MELSEC_WRITE_RESULT.Cancelled;
                    _process = EN_MELSEC_PROCESS.Close;
                }
            }
        }

        private void TrimWriteStatus()
        {
            while (_writeStatus.Count > MaximumStoredWriteStatusCount)
            {
                int oldestRequestNo = int.MaxValue;
                foreach (KeyValuePair<int, CMelsecWriteCommand> pair in _writeStatus)
                {
                    if (pair.Key < oldestRequestNo && IsTerminalWriteResult(pair.Value.Result))
                    {
                        oldestRequestNo = pair.Key;
                    }
                }

                if (oldestRequestNo == int.MaxValue)
                {
                    return;
                }

                _writeStatus.Remove(oldestRequestNo);
            }
        }

        private static bool IsTerminalWriteResult(EN_MELSEC_WRITE_RESULT result)
        {
            return result == EN_MELSEC_WRITE_RESULT.Confirmed || result == EN_MELSEC_WRITE_RESULT.Timeout || result == EN_MELSEC_WRITE_RESULT.CommunicationError || result == EN_MELSEC_WRITE_RESULT.InvalidParameter || result == EN_MELSEC_WRITE_RESULT.Cancelled;
        }

        private void WriteCommandValue(CMelsecWriteCommand command)
        {
            switch (command.RequestedDataType)
            {
                case EN_MELSEC_DATA_TYPE.Bit:
                    WriteBitCore(command.WriteId, (bool)command.ExpectedValue, CancellationToken.None);
                    break;
                case EN_MELSEC_DATA_TYPE.Word:
                    WriteWordCore(command.WriteId, (double)command.ExpectedValue, CancellationToken.None);
                    break;
                case EN_MELSEC_DATA_TYPE.Double:
                    WriteDoubleCore(command.WriteId, (double)command.ExpectedValue, CancellationToken.None);
                    break;
                case EN_MELSEC_DATA_TYPE.String:
                    WriteStringCore(command.WriteId, (string)command.ExpectedValue, CancellationToken.None);
                    break;
                default:
                    throw new InvalidOperationException("MELSEC confirmed write data type is not supported: " + command.RequestedDataType);
            }
        }

        private object ReadCommandValue(CMelsecWriteCommand command)
        {
            switch (command.RequestedDataType)
            {
                case EN_MELSEC_DATA_TYPE.Bit:
                    return ReadBitCore(command.ReadbackId, CancellationToken.None);
                case EN_MELSEC_DATA_TYPE.Word:
                    return ReadWordCore(command.ReadbackId, CancellationToken.None);
                case EN_MELSEC_DATA_TYPE.Double:
                    return ReadDoubleCore(command.ReadbackId, CancellationToken.None);
                case EN_MELSEC_DATA_TYPE.String:
                    return ReadStringCore(command.ReadbackId, CancellationToken.None);
                default:
                    throw new InvalidOperationException("MELSEC readback data type is not supported: " + command.RequestedDataType);
            }
        }

        private static bool IsExpectedValue(CMelsecWriteCommand command, object actualValue)
        {
            switch (command.RequestedDataType)
            {
                case EN_MELSEC_DATA_TYPE.Bit:
                    return (bool)command.ExpectedValue == (bool)actualValue;
                case EN_MELSEC_DATA_TYPE.Word:
                case EN_MELSEC_DATA_TYPE.Double:
                    double expected = (double)command.ExpectedValue;
                    double actual = (double)actualValue;
                    double scale = command.ReadbackData == null ? 1.0 : Math.Abs(command.ReadbackData.ReadScale);
                    double tolerance = Math.Max(double.Epsilon, scale / 2.0);
                    return Math.Abs(expected - actual) <= tolerance;
                case EN_MELSEC_DATA_TYPE.String:
                    return string.Equals((string)command.ExpectedValue, (string)actualValue, StringComparison.Ordinal);
                default:
                    return false;
            }
        }

        private void ApplySimulationReadback(CMelsecWriteCommand command)
        {
            if (command.WriteData == null || command.ReadbackData == null)
            {
                return;
            }

            ST_INTERFACE_DATA interfaceData = GetInterfaceData(command.WriteData);
            if (!IsSimulation(interfaceData))
            {
                return;
            }

            EN_MELSEC_SIMULATION_READBACK mode = GetSimulationReadbackMode();
            if (mode == EN_MELSEC_SIMULATION_READBACK.AutoEcho || (mode == EN_MELSEC_SIMULATION_READBACK.FailFirstAttempt && command.CurrentRetryCount > 0))
            {
                WriteSimulationValue(command.ReadbackData, command.ExpectedValue);
            }
        }

        private EN_MELSEC_SIMULATION_READBACK GetSimulationReadbackMode()
        {
            lock (_simulationLock)
            {
                return _simulationReadback;
            }
        }

        private void WriteSimulationValue(ST_MELSEC_MAP_DATA data, object value)
        {
            ST_MELSEC_ADDRESS address = ParseAddress(data.Address);
            switch (data.DataType)
            {
                case EN_MELSEC_DATA_TYPE.Bit:
                    int bitIndex = RequireWordBit(address, data);
                    ushort[] bitWords = ReadSimulationWords(data.DeviceNo, address, 1);
                    ushort mask = (ushort)(1 << bitIndex);
                    if ((bool)value)
                    {
                        bitWords[0] = (ushort)(bitWords[0] | mask);
                    }
                    else
                    {
                        bitWords[0] = (ushort)(bitWords[0] & ~mask);
                    }

                    WriteSimulationWords(data.DeviceNo, address, bitWords);
                    break;
                case EN_MELSEC_DATA_TYPE.Word:
                    WriteSimulationWords(data.DeviceNo, address, [(ushort)EncodeIntegerValue(data, Convert.ToDouble(value, CultureInfo.InvariantCulture) / data.ReadScale)]);
                    break;
                case EN_MELSEC_DATA_TYPE.DWord:
                    WriteSimulationWords(data.DeviceNo, address, Int32ToWords(EncodeIntegerValue(data, Convert.ToDouble(value, CultureInfo.InvariantCulture) / data.ReadScale), Math.Max(2, data.Length)));
                    break;
                case EN_MELSEC_DATA_TYPE.Double:
                    // This emulates PLC feedback, so invert its READ_SCALE rather than applying WRITE_SCALE.
                    double doubleRawValue = (double)value / data.ReadScale;
                    WriteSimulationWords(data.DeviceNo, address, Int32ToWords(EncodeIntegerValue(data, doubleRawValue), 2));
                    break;
                case EN_MELSEC_DATA_TYPE.Float:
                    double floatRawValue = (double)value / data.ReadScale;
                    WriteSimulationWords(data.DeviceNo, address, FloatToWords((float)floatRawValue, Math.Max(2, data.Length)));
                    break;
                case EN_MELSEC_DATA_TYPE.String:
                    WriteSimulationString(data, address, (string)value);
                    break;
            }
        }

        private void WriteSimulationString(ST_MELSEC_MAP_DATA data, ST_MELSEC_ADDRESS address, string value)
        {
            int byteLength = data.Length * 2;
            byte[] sourceBytes = Encoding.ASCII.GetBytes(value);
            byte[] bytes = Enumerable.Repeat((byte)' ', byteLength).ToArray();
            Array.Copy(sourceBytes, bytes, Math.Min(sourceBytes.Length, bytes.Length));
            ushort[] words = new ushort[data.Length];
            for (int index = 0; index < words.Length; index++)
            {
                words[index] = (ushort)(bytes[index * 2] | (bytes[index * 2 + 1] << 8));
            }

            WriteSimulationWords(data.DeviceNo, address, words);
        }

        private void SaveReadSnapshot(string id, object value)
        {
            var key = NormalizeId(id);
            CMelsecReadSnapshot snapshot;
            lock (_readStateLock)
            {
                snapshot = new CMelsecReadSnapshot(_readCycleNo, DateTimeOffset.UtcNow, value);
                _readSnapshots[key] = snapshot;
            }
            // IOF coordinates are read on demand (actual Count only). Publish
            // their last readback for Monitor without creating 500 poll targets.
            if (IsIofPositionId(key))
                lock (_pollLock)
                    if (_cachedValues.ContainsKey(key) && !_pollBlockById.ContainsKey(key))
                        _cachedValues[key] = new(key, value, EN_MELSEC_CACHE_STATE.Valid, snapshot.ReadAt, snapshot.ReadCycleNo, "");
        }

        private void RegisterReadSuccess()
        {
            lock (_readStateLock)
            {
                _readCycleNo++;
                _lastSuccessfulReadAt = DateTimeOffset.UtcNow;
                _lastReadReturnCode = 0;
                _consecutiveReadFailureCount = 0;
                _communicationAvailable = true;
            }
        }

        private void RegisterReadFailure(int returnCode)
        {
            lock (_readStateLock)
            {
                _lastReadReturnCode = returnCode;
                _consecutiveReadFailureCount++;
                _communicationAvailable = false;
            }
        }

        private void RegisterCommunicationFailure()
        {
            lock (_readStateLock)
            {
                _communicationAvailable = false;
            }
        }

        private void RegisterCommunicationSuccess()
        {
            lock (_readStateLock)
            {
                _communicationAvailable = true;
            }
        }

        private ushort[] ReadWords(ST_MELSEC_MAP_DATA data, ST_MELSEC_ADDRESS address, int wordCount, CancellationToken cancellationToken, bool suppressLog = false)
        {
            if (wordCount <= 0 || wordCount > ushort.MaxValue)
            {
                throw new InvalidOperationException($"MELSEC word count is invalid: {FormatMap(data)}, Count={wordCount}");
            }

            var interfaceData = GetInterfaceData(data);
            var command = CreateCommand("READ_WORD", data, wordCount.ToString(CultureInfo.InvariantCulture));
            if (IsSimulation(interfaceData))
            {
                UpdateStageMotionSimulationWords(data.DeviceNo, address, wordCount);
                var simulationWords = ReadSimulationWords(data.DeviceNo, address, wordCount);
                if (!suppressLog)
                {
                    WriteCommandLog(data, interfaceData, command, $"SIMULATION OK / {WordsToHexText(simulationWords)}");
                }

                RegisterReadSuccess();
                return simulationWords;
            }

            ST_MELSEC_NET_OPTION option = ReadMelsecNetOption(interfaceData);
            int path = GetMelsecNetPath(data.DeviceNo);
            int requestedSize = checked(wordCount * sizeof(short));
            if (requestedSize > MelsecNetMaximumTransferBytes)
            {
                throw new InvalidOperationException("MELSECNET read size exceeds 1920 bytes: " + FormatMap(data) + ", Size=" + requestedSize.ToString(CultureInfo.InvariantCulture));
            }

            int actualSize = requestedSize;
            short[] receiveData = new short[wordCount];
            int returnCode;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                returnCode = _melsecNetApi.ReceiveEx(path, option.NetworkNo, option.StationNo, address.DeviceType, address.Number, ref actualSize, receiveData);
            }
            catch (Exception exception)when (IsMelsecNetRuntimeException(exception))
            {
                RegisterReadFailure(-1);
                if (suppressLog)
                {
                    _interfaceManager.UpdateMelsecCommunicationState(interfaceData.Number, false, "mdReceiveEx", "", exception.Message);
                }
                else
                {
                    RegisterMelsecNetCommunicationError(data, interfaceData, option, path, requestedSize, "mdReceiveEx", exception.Message);
                }

                throw CreateMelsecNetRuntimeException("mdReceiveEx", exception);
            }

            if (returnCode != 0)
            {
                RegisterReadFailure(returnCode);
                IOException exception = CreateMelsecNetReturnCodeException("mdReceiveEx", returnCode, interfaceData, option, path, requestedSize);
                if (suppressLog)
                {
                    _interfaceManager.UpdateMelsecCommunicationState(interfaceData.Number, false, "mdReceiveEx", "", exception.Message);
                }
                else
                {
                    RegisterMelsecNetCommunicationError(data, interfaceData, option, path, requestedSize, "mdReceiveEx", exception.Message);
                }

                throw exception;
            }

            if (actualSize != requestedSize)
            {
                RegisterReadFailure(-5);
                string message = "mdReceiveEx size mismatch. RequestedSize=" + requestedSize.ToString(CultureInfo.InvariantCulture) + ", ActualSize=" + actualSize.ToString(CultureInfo.InvariantCulture);
                if (suppressLog)
                {
                    _interfaceManager.UpdateMelsecCommunicationState(interfaceData.Number, false, "mdReceiveEx", "", message);
                }
                else
                {
                    RegisterMelsecNetCommunicationError(data, interfaceData, option, path, requestedSize, "mdReceiveEx", message);
                }

                throw new IOException(message + " / " + FormatMap(data));
            }

            ushort[] words = new ushort[wordCount];
            for (int index = 0; index < words.Length; index++)
            {
                words[index] = unchecked((ushort)receiveData[index]);
            }

            if (!suppressLog)
            {
                WriteCommandLog(data, interfaceData, command, "mdReceiveEx OK / " + actualSize.ToString(CultureInfo.InvariantCulture) + " bytes");
            }

            _interfaceManager.UpdateMelsecCommunicationState(interfaceData.Number, true, command, "mdReceiveEx OK", "");
            RegisterReadSuccess();
            return words;
        }

        private void WriteWords(ST_MELSEC_MAP_DATA data, ST_MELSEC_ADDRESS address, IReadOnlyList<ushort> words, CancellationToken cancellationToken)
        {
            if (words.Count <= 0 || words.Count > ushort.MaxValue)
            {
                throw new InvalidOperationException($"MELSEC word count is invalid: {FormatMap(data)}, Count={words.Count}");
            }

            var interfaceData = GetInterfaceData(data);
            var command = CreateCommand("WRITE_WORD", data, WordsToHexText(words));
            // A write acknowledgement is not feedback. Only the next central read may
            // publish a valid value, including aliases that share the written words.
            InvalidateWrittenCache(data.DeviceNo, address, words.Count);
            if (IsSimulation(interfaceData))
            {
                WriteSimulationWords(data.DeviceNo, address, words);
                WriteCommandLog(data, interfaceData, command, "SIMULATION OK");
                RegisterCommunicationSuccess();
                return;
            }

            ST_MELSEC_NET_OPTION option = ReadMelsecNetOption(interfaceData);
            int path = GetMelsecNetPath(data.DeviceNo);
            int requestedSize = checked(words.Count * sizeof(short));
            if (requestedSize > MelsecNetMaximumTransferBytes)
            {
                throw new InvalidOperationException("MELSECNET write size exceeds 1920 bytes: " + FormatMap(data) + ", Size=" + requestedSize.ToString(CultureInfo.InvariantCulture));
            }

            short[] sendData = new short[words.Count];
            for (int index = 0; index < words.Count; index++)
            {
                sendData[index] = unchecked((short)words[index]);
            }

            int actualSize = requestedSize;
            int returnCode;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                returnCode = _melsecNetApi.SendEx(path, option.NetworkNo, option.StationNo, address.DeviceType, address.Number, ref actualSize, sendData);
            }
            catch (Exception exception)when (IsMelsecNetRuntimeException(exception))
            {
                RegisterMelsecNetCommunicationError(data, interfaceData, option, path, requestedSize, "mdSendEx", exception.Message);
                throw CreateMelsecNetRuntimeException("mdSendEx", exception);
            }

            if (returnCode != 0)
            {
                IOException exception = CreateMelsecNetReturnCodeException("mdSendEx", returnCode, interfaceData, option, path, requestedSize);
                RegisterMelsecNetCommunicationError(data, interfaceData, option, path, requestedSize, "mdSendEx", exception.Message);
                throw exception;
            }

            if (actualSize != requestedSize)
            {
                string message = "mdSendEx size mismatch. RequestedSize=" + requestedSize.ToString(CultureInfo.InvariantCulture) + ", ActualSize=" + actualSize.ToString(CultureInfo.InvariantCulture);
                RegisterMelsecNetCommunicationError(data, interfaceData, option, path, requestedSize, "mdSendEx", message);
                throw new IOException(message + " / " + FormatMap(data));
            }

            WriteCommandLog(data, interfaceData, command, "mdSendEx OK / " + actualSize.ToString(CultureInfo.InvariantCulture) + " bytes");
            _interfaceManager.UpdateMelsecCommunicationState(interfaceData.Number, true, command, "mdSendEx OK", "");
            RegisterCommunicationSuccess();
        }

        private ST_INTERFACE_DATA GetInterfaceData(ST_MELSEC_MAP_DATA data)
        {
            return _interfaceManager.GetInterfaceData(EN_EQP_MODULE.Melsec, data.DeviceNo) ?? throw new InvalidOperationException($"MELSEC interface is not configured: MELSEC_{data.DeviceNo}");
        }

        private bool IsSimulation(ST_INTERFACE_DATA data)
        {
            return _interfaceManager.IsSimul(EN_EQP_MODULE.Melsec, data.Number);
        }

        private ushort[] ReadSimulationWords(int deviceNo, ST_MELSEC_ADDRESS address, int wordCount)
        {
            var words = new ushort[wordCount];
            lock (_simulationLock)
            {
                for (var i = 0; i < words.Length; i++)
                    _simulationWords.TryGetValue(CreateSimulationKey(deviceNo, address, i), out words[i]);
            }

            return words;
        }

        private void WriteSimulationWords(int deviceNo, ST_MELSEC_ADDRESS address, IReadOnlyList<ushort> words)
        {
            lock (_simulationLock)
            {
                for (var i = 0; i < words.Count; i++)
                    _simulationWords[CreateSimulationKey(deviceNo, address, i)] = words[i];
            }
        }

        private static string CreateSimulationKey(int deviceNo, ST_MELSEC_ADDRESS address, int wordOffset)
        {
            return $"{deviceNo}:{address.Device}:{address.Number + wordOffset:X}";
        }

        private int GetMelsecNetPath(int deviceNo)
        {
            lock (_ioLock)
            {
                if (_melsecNetPaths.TryGetValue(deviceNo, out int path))
                {
                    return path;
                }
            }

            throw new InvalidOperationException("MELSECNET communication line is not open: MELSEC_" + deviceNo.ToString(CultureInfo.InvariantCulture));
        }

        private void RegisterMelsecNetCommunicationError(ST_MELSEC_MAP_DATA data, ST_INTERFACE_DATA interfaceData, ST_MELSEC_NET_OPTION option, int path, int dataSize, string functionName, string detail)
        {
            RegisterCommunicationFailure();
            string message = "[I/F][COMM_ERROR] " + functionName + " / " + detail + " / " + FormatMelsecNetContext(option, path, dataSize, 0);
            WriteErrorLog(data, interfaceData, functionName, message);
            _interfaceManager.UpdateMelsecCommunicationState(interfaceData.Number, false, functionName, "", message);
        }

        private void WriteMelsecNetOpenLog(ST_INTERFACE_DATA interfaceData, ST_MELSEC_NET_OPTION option, int path)
        {
            _logManager?.WriteInterfaceCommand(EN_EQP_MODULE.Melsec, interfaceData.NickName, "MELSECNET:mdOpen", "OK", FormatMelsecNetContext(option, path, 0, 0));
        }

        private string FormatMelsecNetContext(ST_MELSEC_NET_OPTION option, int path, int dataSize, int returnCode)
        {
            int requestNo = 0;
            EN_MELSEC_PROCESS process;
            lock (_writeLock)
            {
                if (_activeWriteCommand != null)
                {
                    requestNo = _activeWriteCommand.RequestNo;
                }

                process = _process;
            }

            return "ChannelNo=" + option.ChannelNo.ToString(CultureInfo.InvariantCulture) + ", NetworkNo=" + option.NetworkNo.ToString(CultureInfo.InvariantCulture) + ", StationNo=" + option.StationNo.ToString(CultureInfo.InvariantCulture) + ", Path=" + path.ToString(CultureInfo.InvariantCulture) + ", DataSize=" + dataSize.ToString(CultureInfo.InvariantCulture) + ", ReturnCode=" + returnCode.ToString(CultureInfo.InvariantCulture) + ", Process=" + process + ", RequestNo=" + requestNo.ToString(CultureInfo.InvariantCulture);
        }

        private IOException CreateMelsecNetReturnCodeException(string functionName, int returnCode, ST_INTERFACE_DATA interfaceData, ST_MELSEC_NET_OPTION option, int path, int dataSize)
        {
            string message = functionName + " failed. " + FormatMelsecNetContext(option, path, dataSize, returnCode) + ", NickName=" + interfaceData.NickName;
            return new IOException(message);
        }

        private static bool IsMelsecNetRuntimeException(Exception exception)
        {
            return exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;
        }

        private static InvalidOperationException CreateMelsecNetRuntimeException(string functionName, Exception exception)
        {
            return new InvalidOperationException(functionName + " cannot use the MELSEC Data Link Library. " + "Install the MELSECNET/H board software with the matching application architecture and " + CMelsecNetApi.NativeLibraryName + ". " + exception.Message, exception);
        }

        private ST_MELSEC_MAP_DATA PrepareRead(string id, EN_MELSEC_DATA_TYPE dataType, CancellationToken cancellationToken)
        {
            return PrepareRead(id, [dataType], cancellationToken);
        }

        private ST_MELSEC_MAP_DATA PrepareRead(string id, IReadOnlyList<EN_MELSEC_DATA_TYPE> dataTypes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = GetMapData(id);
            EnsureDataType(data, dataTypes);
            if (data.Access == EN_MELSEC_ACCESS.Write)
            {
                throw new InvalidOperationException($"MELSEC map is write only: {FormatMap(data)}");
            }

            return data;
        }

        private ST_MELSEC_MAP_DATA PrepareWrite(string id, EN_MELSEC_DATA_TYPE dataType, CancellationToken cancellationToken)
        {
            return PrepareWrite(id, [dataType], cancellationToken);
        }

        private ST_MELSEC_MAP_DATA PrepareWrite(string id, IReadOnlyList<EN_MELSEC_DATA_TYPE> dataTypes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = GetMapData(id);
            EnsureDataType(data, dataTypes);
            if (data.Access == EN_MELSEC_ACCESS.Read)
            {
                throw new InvalidOperationException($"MELSEC map is read only: {FormatMap(data)}");
            }

            return data;
        }

        private static void EnsureDataType(ST_MELSEC_MAP_DATA data, IReadOnlyList<EN_MELSEC_DATA_TYPE> dataTypes)
        {
            if (dataTypes.Contains(data.DataType))
            {
                return;
            }

            throw new InvalidOperationException($"MELSEC map data type mismatch: {FormatMap(data)}. Expected={string.Join("/", dataTypes)}");
        }

        private static ST_MELSEC_ADDRESS ParseAddress(string value)
        {
            var text = value.Trim().ToUpperInvariant();
            var bitIndex = (int? )null;
            var bitSeparatorIndex = text.IndexOf('.', StringComparison.Ordinal);
            if (bitSeparatorIndex >= 0)
            {
                var bitText = text[(bitSeparatorIndex + 1)..];
                if (string.IsNullOrWhiteSpace(bitText) || !int.TryParse(bitText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedBit) || parsedBit < 0 || parsedBit > 15)
                {
                    throw new FormatException($"MELSEC bit index is invalid: {value}");
                }

                bitIndex = parsedBit;
                text = text[..bitSeparatorIndex];
            }

            // Hex addresses can start with A-F (WC102, WECD8). Match the device
            // prefix explicitly instead of consuming those address digits as letters.
            string[] devices = ["SM", "SD", "SB", "SW", "ZR", "X", "Y", "L", "M", "F", "D", "V", "R", "B", "W"];
            var device = devices.FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.Ordinal));
            if (device is null || device.Length >= text.Length)
            {
                throw new FormatException($"MELSEC address is invalid: {value}");
            }

            var numberText = text[device.Length..];
            var number = ParseDeviceNumber(device, numberText);
            return new ST_MELSEC_ADDRESS(device, number, bitIndex, GetMelsecNetDeviceType(device));
        }

        private static int ParseDeviceNumber(string device, string value)
        {
            if (value.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            if (value.EndsWith("H", StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(value[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            var style = IsHexDevice(device) ? NumberStyles.HexNumber : NumberStyles.Integer;
            return int.Parse(value, style, CultureInfo.InvariantCulture);
        }

        private static bool IsHexDevice(string device)
        {
            return device is "X" or "Y" or "B" or "W" or "SB" or "SW";
        }

        private static int GetMelsecNetDeviceType(string device)
        {
            int EvaluateDeviceSwitch1()
            {
                var switchValue = device;
                switch (switchValue)
                {
                    case "X":
                        return 1;
                    case "Y":
                        return 2;
                    case "L":
                        return 3;
                    case "M":
                        return 4;
                    case "SM":
                        return 5;
                    case "F":
                        return 6;
                    case "D":
                        return 13;
                    case "SD":
                        return 14;
                    case "V":
                        return 30;
                    case "R":
                        return 22;
                    case "B":
                        return 23;
                    case "W":
                        return 24;
                    case "SB":
                        return 25;
                    case "SW":
                        return 28;
                    case "ZR":
                        return 220;
                    default:
                        throw new NotSupportedException($"MELSEC device is not supported: {device}");
                }
            }

            return EvaluateDeviceSwitch1();
        }

        private static int RequireWordBit(ST_MELSEC_ADDRESS address, ST_MELSEC_MAP_DATA data)
        {
            if (address.BitIndex is { } bitIndex)
            {
                return bitIndex;
            }

            throw new InvalidOperationException($"MELSEC BIT address must include bit index such as W23458.0: {FormatMap(data)}");
        }

        private static int WordsToInt32(IReadOnlyList<ushort> words)
        {
            if (words.Count <= 1)
            {
                return words.Count == 0 ? 0 : words[0];
            }

            return unchecked((int)(words[0] | ((uint)words[1] << 16)));
        }

        private static ushort[] Int32ToWords(int value, int wordCount)
        {
            var words = new ushort[wordCount];
            words[0] = (ushort)(value & 0xFFFF);
            if (wordCount > 1)
            {
                words[1] = (ushort)((value >> 16) & 0xFFFF);
            }

            return words;
        }

        private static float WordsToFloat(IReadOnlyList<ushort> words)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0, 2), words.Count > 0 ? words[0] : (ushort)0);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), words.Count > 1 ? words[1] : (ushort)0);
            return BitConverter.ToSingle(bytes, 0);
        }

        private static ushort[] FloatToWords(float value, int wordCount)
        {
            var bytes = BitConverter.GetBytes(value);
            var words = new ushort[wordCount];
            words[0] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2));
            if (wordCount > 1)
            {
                words[1] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2));
            }

            return words;
        }

        private static ST_MELSEC_NET_OPTION ReadMelsecNetOption(ST_INTERFACE_DATA data)
        {
            if (data.InterfaceType != EN_INTERFACE_TYPE.MelsecNet)
            {
                throw new InvalidOperationException("MELSEC requires the MELSEC_NET interface type.");
            }

            if (data.Arguments.Count < 3)
            {
                throw new InvalidOperationException("MELSEC_NET requires ARG1 channel, ARG2 network number, and ARG3 station number: " + data.NickName);
            }

            short channelNo = ReadMelsecNetShortArgument(data, 0, "ARG1/CHANNEL_NO");
            int networkNo = ReadMelsecNetIntArgument(data, 1, "ARG2/NETWORK_NO");
            int stationNo = ReadMelsecNetIntArgument(data, 2, "ARG3/STATION_NO");
            // Only convert to the SDK argument types; the driver decides which values it supports.
            return new ST_MELSEC_NET_OPTION(channelNo, networkNo, stationNo);
        }

        private static short ReadMelsecNetShortArgument(ST_INTERFACE_DATA data, int argumentIndex, string argumentName)
        {
            int value = ReadMelsecNetIntArgument(data, argumentIndex, argumentName);
            if (value < short.MinValue || value > short.MaxValue)
            {
                throw new InvalidOperationException("MELSEC_NET " + argumentName + " is outside the Int16 range: " + value.ToString(CultureInfo.InvariantCulture));
            }

            return (short)value;
        }

        private static int ReadMelsecNetIntArgument(ST_INTERFACE_DATA data, int argumentIndex, string argumentName)
        {
            if (argumentIndex >= data.Arguments.Count || !int.TryParse(data.Arguments[argumentIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                throw new InvalidOperationException("MELSEC_NET " + argumentName + " is invalid: " + data.NickName);
            }

            return value;
        }

        private void WriteHandshakeLog(CMelsecWriteCommand command, string category, string detail)
        {
            ST_INTERFACE_DATA? interfaceData = GetHandshakeInterfaceData(command);
            bool simulation = interfaceData != null && IsSimulation(interfaceData);
            string prefix = simulation ? "[SIMULATION] " : "";
            string nickName = interfaceData == null ? "MELSEC" : interfaceData.NickName;
            _logManager?.WriteInterfaceCommand(EN_EQP_MODULE.Melsec, nickName, prefix + category + " " + FormatWriteCommand(command), detail, FormatWriteStatus(command));
        }

        private void WriteHandshakeError(CMelsecWriteCommand command, string category, string detail)
        {
            ST_INTERFACE_DATA? interfaceData = GetHandshakeInterfaceData(command);
            bool simulation = interfaceData != null && IsSimulation(interfaceData);
            string prefix = simulation ? "[SIMULATION] " : "";
            string nickName = interfaceData == null ? "MELSEC" : interfaceData.NickName;
            _logManager?.WriteInterfaceError(EN_EQP_MODULE.Melsec, nickName, prefix + category + " " + FormatWriteCommand(command), FormatWriteStatus(command) + " / " + detail);
        }

        private ST_INTERFACE_DATA? GetHandshakeInterfaceData(CMelsecWriteCommand command)
        {
            if (command.WriteData == null)
            {
                return null;
            }

            return _interfaceManager.GetInterfaceData(EN_EQP_MODULE.Melsec, command.WriteData.DeviceNo);
        }

        private static string FormatWriteCommand(CMelsecWriteCommand command)
        {
            return "RequestNo=" + command.RequestNo.ToString(CultureInfo.InvariantCulture) + ", WriteId=" + command.WriteId + ", ReadbackId=" + command.ReadbackId + ", Expected=" + FormatCommandValue(command.ExpectedValue);
        }

        private static string FormatWriteStatus(CMelsecWriteCommand command)
        {
            return "Result=" + command.Result + ", Actual=" + command.ActualValue + ", TimeoutMs=" + command.TimeoutMs.ToString(CultureInfo.InvariantCulture) + ", Retry=" + command.CurrentRetryCount.ToString(CultureInfo.InvariantCulture) + "/" + command.RetryCount.ToString(CultureInfo.InvariantCulture) + ", ReadCycle=" + command.ConfirmReadCycle.ToString(CultureInfo.InvariantCulture) + ", MinimumReadCycle=" + command.MinimumReadCycle.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatCommandValue(object? value)
        {
            if (value == null)
            {
                return "";
            }

            if (value is bool boolValue)
            {
                return boolValue ? "1" : "0";
            }

            if (value is double doubleValue)
            {
                return doubleValue.ToString("R", CultureInfo.InvariantCulture);
            }

            if (value is float floatValue)
            {
                return floatValue.ToString("R", CultureInfo.InvariantCulture);
            }

            if (value is IFormattable formattable)
            {
                return formattable.ToString(null, CultureInfo.InvariantCulture) ?? "";
            }

            return value.ToString() ?? "";
        }

        private void WriteCommandLog(ST_MELSEC_MAP_DATA data, ST_INTERFACE_DATA interfaceData, string command, string response)
        {
            _logManager?.WriteInterfaceCommand(EN_EQP_MODULE.Melsec, interfaceData.NickName, command, response, FormatMap(data));
        }

        private void WriteErrorLog(ST_MELSEC_MAP_DATA data, ST_INTERFACE_DATA interfaceData, string command, string detail)
        {
            _logManager?.WriteInterfaceError(EN_EQP_MODULE.Melsec, interfaceData.NickName, command, $"{FormatMap(data)} / {detail}");
        }

        private static string CreateCommand(string operation, ST_MELSEC_MAP_DATA data, string value = "")
        {
            var fields = new[]
            {
                "MELSEC",
                operation,
                data.Id,
                data.Address,
                data.DataType.ToString().ToUpperInvariant(),
                "READ_SCALE=" + data.ReadScale.ToString(CultureInfo.InvariantCulture),
                "WRITE_SCALE=" + data.WriteScale.ToString(CultureInfo.InvariantCulture),
                data.Length.ToString(CultureInfo.InvariantCulture),
                value
            };
            return string.Join(":", fields);
        }

        private static string WordsToHexText(IReadOnlyList<ushort> words)
        {
            string SelectWord8(ushort word)
            {
                return word.ToString("X4", CultureInfo.InvariantCulture);
            }

            return string.Join(" ", words.Select(SelectWord8));
        }

        private static string FormatMap(ST_MELSEC_MAP_DATA data)
        {
            return $"{data.Id}({data.Address}, {data.DataType}, MELSEC_{data.DeviceNo})";
        }

        private static string NormalizeId(string id)
        {
            return id.Trim().ToUpperInvariant();
        }

        private enum EN_MELSEC_THREAD_COMMAND
        {
            Open,
            Close,
            ReadBit,
            WriteBit,
            ReadWord,
            WriteWord,
            ReadDouble,
            WriteDouble,
            WriteDoubleArray,
            ReadDoubleArray,
            ReadString,
            WriteString
        }

        private sealed class CMelsecThreadRequest
        {
            public CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND command, string id, object? value, CancellationToken cancellationToken, bool suppressLog = false)
            {
                Command = command;
                Id = id;
                Value = value;
                CancellationToken = cancellationToken;
                SuppressLog = suppressLog;
            }

            public EN_MELSEC_THREAD_COMMAND Command { get; }
            public string Id { get; }
            public object? Value { get; }
            public CancellationToken CancellationToken { get; }
            public bool SuppressLog { get; }
            public ManualResetEvent Completed { get; } = new ManualResetEvent(false);
            public object? Result { get; set; }
            public Exception? Error { get; set; }
        }

        private sealed class CMelsecWriteCommand
        {
            public CMelsecWriteCommand(int requestNo, string writeId, string readbackId, EN_MELSEC_DATA_TYPE requestedDataType, object expectedValue)
            {
                RequestNo = requestNo;
                WriteId = writeId;
                ReadbackId = readbackId;
                RequestedDataType = requestedDataType;
                ExpectedValue = expectedValue;
            }

            public int RequestNo { get; }
            public string WriteId { get; }
            public string ReadbackId { get; set; }
            public EN_MELSEC_DATA_TYPE RequestedDataType { get; }
            public object ExpectedValue { get; }
            public string ActualValue { get; set; } = "";
            public EN_MELSEC_WRITE_RESULT Result { get; set; }
            public int CurrentRetryCount { get; set; }
            public int RetryCount { get; set; }
            public int TimeoutMs { get; set; }
            public long WriteBeforeReadCycle { get; set; }
            public long MinimumReadCycle { get; set; }
            public long ConfirmReadCycle { get; set; }
            public string ErrorMessage { get; set; } = "";
            public DateTimeOffset AttemptStartedAt { get; set; }
            public DateTimeOffset NextReadAt { get; set; }
            public ST_MELSEC_MAP_DATA? WriteData { get; set; }
            public ST_MELSEC_MAP_DATA? ReadbackData { get; set; }

            public volatile bool CancelRequested;
            public ST_MELSEC_WRITE_STATUS CreateStatus()
            {
                return new ST_MELSEC_WRITE_STATUS(
                    RequestNo, 
                    WriteId, 
                    ReadbackId, 
                    FormatCommandValue(ExpectedValue), 
                    ActualValue, 
                    Result, 
                    CurrentRetryCount, 
                    RetryCount, 
                    TimeoutMs, 
                    WriteBeforeReadCycle, 
                    MinimumReadCycle, 
                    ConfirmReadCycle, 
                    ErrorMessage
                );
            }
        }

        private sealed record CMelsecReadSnapshot(long ReadCycleNo, DateTimeOffset ReadAt, object Value);
        private sealed record ST_MELSEC_ADDRESS(string Device, int Number, int? BitIndex, int DeviceType);
        private sealed record ST_MELSEC_NET_OPTION(short ChannelNo, int NetworkNo, int StationNo);
#endregion
#region Connection
        // Settings reload must not stop the shared worker or close another station's path.
        internal void CloseDevice(int deviceNo, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = GetMapList().Where(row => row.DeviceNo == deviceNo).Select(row => NormalizeId(row.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            const string message = "MELSEC interface settings changed.";
            lock (_requestLock)
            {
                var count = _requestQueue.Count;
                for (var index = 0; index < count; index++)
                {
                    var pending = _requestQueue.Dequeue();
                    if (ids.Contains(pending.Id) || (pending.Command == EN_MELSEC_THREAD_COMMAND.Open && Equals(pending.Value, deviceNo)))
                    {
                        pending.Error = new OperationCanceledException(message);
                        pending.Completed.Set();
                    }
                    else
                        _requestQueue.Enqueue(pending);
                }
            }

            lock (_writeLock)
            {
                foreach (var pending in _writeQueue.Concat(_activeWriteCommand is null ? [] : new[] { _activeWriteCommand }))
                {
                    if (!ids.Contains(pending.WriteId) && !ids.Contains(pending.ReadbackId))
                        continue;
                    pending.CancelRequested = true;
                    pending.Result = EN_MELSEC_WRITE_RESULT.Cancelled;
                    pending.ErrorMessage = message;
                }
            }

            if (IsRunning)
                ExecuteThreadRequest(new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.Close, "MELSEC_" + deviceNo.ToString(CultureInfo.InvariantCulture), deviceNo, cancellationToken));
            else
                CloseDeviceCore(deviceNo);
        }

        private void CloseDeviceCore(int deviceNo)
        {
            var data = _interfaceManager.GetInterfaceData(EN_EQP_MODULE.Melsec, deviceNo);
            bool hasPath;
            lock (_ioLock)
                hasPath = _melsecNetPaths.ContainsKey(deviceNo);
            if (hasPath && data is not null)
                CloseMelsecNetChannelCore(deviceNo, data, ReadMelsecNetOption(data));
            lock (_pollLock)
            {
                _pollDeviceBackoff.Remove(deviceNo);
                foreach (var block in _pollBlocks.Where(block => block.DeviceNo == deviceNo))
                {
                    block.NextReadAt = DateTimeOffset.MinValue;
                    SetBlockState(block, EN_MELSEC_CACHE_STATE.Offline, "MELSEC interface settings changed.");
                    lock (_readStateLock)
                        foreach (var item in block.Items)
                            _readSnapshots.Remove(NormalizeId(item.Data.Id));
                }
            }
        }
#endregion

#region DoubleArray
        private static bool IsIofPositionId(string id) => id.StartsWith("IOF_POS_ARRAY_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(id[14..], NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index is >= 1 and <= 500;

        /// <summary>Array capacity from map metadata only; does not perform device I/O.</summary>
        public int GetDoubleArrayCapacity(string id) => ResolveDoubleArrayData(GetMapData(id)).Length / 2;

        private ST_MELSEC_MAP_DATA ResolveDoubleArrayData(ST_MELSEC_MAP_DATA row)
        {
            ValidateMapData(row);
            if (row.DataType != EN_MELSEC_DATA_TYPE.Double || row.Length % 2 != 0)
                throw new InvalidDataException("MELSEC 배열은 DOUBLE / 짝수 Word LENGTH여야 합니다: " + row.Id);
            // Field CSV lists every coordinate as its own 2-word row so that
            // Monitor and technicians can inspect/write each position. Still
            // execute only the supplied Count as one queued transaction.
            // The legacy single row with LENGTH=1000 remains compatible.
            if (!row.Id.Equals("IOF_POS_ARRAY_1", StringComparison.OrdinalIgnoreCase) || row.Length != 2)
                return row;
            var start = ParseAddress(row.Address);
            ValidateArrayAddress(start);
            lock (_mapLock)
            {
                for (var index = 1; index <= 500; index++)
                {
                    var id = "IOF_POS_ARRAY_" + index.ToString(CultureInfo.InvariantCulture);
                    if (!_map.TryGetValue(id, out var item) || !item.Use || item.DataType != EN_MELSEC_DATA_TYPE.Double
                        || item.Length != 2 || item.DeviceNo != row.DeviceNo || item.Direction != row.Direction || item.Access != row.Access
                        || item.ReadScale != row.ReadScale || item.WriteScale != row.WriteScale
                        || ParseAddress(item.Address) != (start with { Number = checked(start.Number + (index - 1) * 2) }))
                        throw new InvalidDataException("IOF 좌표 1~500의 연속 주소·2 Word·장치·입출력·스케일을 확인해 주세요: " + id);
                }
            }
            return row with { Length = 1000 };
        }

        /// <summary>Logical values, each encoded as a signed 32-bit pair using the map's scale. Single communication owner.</summary>
        public void WriteDoubleArray(string id, IReadOnlyList<double> values, CancellationToken token = default)
        {
            var request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.WriteDoubleArray, id, values.ToArray(), token);
            ExecuteThreadRequest(request);
        }

        public IReadOnlyList<double> ReadDoubleArray(string id, int count, CancellationToken token = default)
        {
            var request = new CMelsecThreadRequest(EN_MELSEC_THREAD_COMMAND.ReadDoubleArray, id, count, token, suppressLog: true);
            ExecuteThreadRequest(request);
            return (double[])request.Result!;
        }

        private void WriteDoubleArrayCore(string id, double[] values, CancellationToken token)
        {
            var row = ResolveDoubleArrayData(PrepareWrite(id, [EN_MELSEC_DATA_TYPE.Double], token));
            ValidateMapData(row);
            if (row.Length % 2 != 0 || values.Length == 0 || values.Length * 2 > row.Length)
                throw new ArgumentException("MELSEC 배열 길이가 맵의 2-Word 저장 영역을 벗어납니다.");
            // Capacity is 500, transfer length is the current line's Count.
            // Stage uses the transmitted count; entries after it are untouched.
            var words = new ushort[values.Length * 2];
            for (var i = 0; i < values.Length; i++)
            {
                var raw = values[i] * row.WriteScale;
                if (!double.IsFinite(raw))
                    throw new ArgumentOutOfRangeException(nameof(values));
                var pair = Int32ToWords(checked((int)Math.Round(raw, MidpointRounding.AwayFromZero)), 2);
                words[i * 2] = pair[0];
                words[i * 2 + 1] = pair[1];
            }

            var address = ParseAddress(row.Address);
            ValidateArrayAddress(address);
            // Split only when the actual Count exceeds the native 960-word limit.
            // The caller asserts IOF START only after the requested points succeed.
            var chunkWords = MelsecNetMaximumTransferBytes / sizeof(short);
            for (var offset = 0; offset < words.Length; offset += chunkWords)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(chunkWords, words.Length - offset);
                WriteWords(row, address with { Number = checked(address.Number + offset) }, words.AsSpan(offset, count).ToArray(), token);
            }
        }

        private double[] ReadDoubleArrayCore(string id, int pointCount, CancellationToken token)
        {
            var row = ResolveDoubleArrayData(PrepareRead(id, [EN_MELSEC_DATA_TYPE.Double], token));
            ValidateMapData(row);
            if (pointCount < 1 || pointCount > row.Length / 2)
                throw new ArgumentOutOfRangeException(nameof(pointCount), "MELSEC 배열 읽기 개수가 맵의 저장 범위를 벗어납니다.");
            var address = ParseAddress(row.Address);
            ValidateArrayAddress(address);
            var words = new ushort[pointCount * 2];
            var chunkWords = MelsecNetMaximumTransferBytes / sizeof(short);
            for (var offset = 0; offset < words.Length; offset += chunkWords)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(chunkWords, words.Length - offset);
                var part = ReadWords(row, address with { Number = checked(address.Number + offset) }, count, token, suppressLog: true);
                Array.Copy(part, 0, words, offset, count);
            }
            var result = Enumerable.Range(0, pointCount).Select(i => WordsToInt32([words[2 * i], words[2 * i + 1]]) * row.ReadScale).ToArray();
            if (id.Equals("IOF_POS_ARRAY_1", StringComparison.OrdinalIgnoreCase))
                for (var index = 0; index < result.Length; index++)
                    SaveReadSnapshot("IOF_POS_ARRAY_" + (index + 1).ToString(CultureInfo.InvariantCulture), result[index]);
            return result;
        }

        private static void ValidateArrayAddress(ST_MELSEC_ADDRESS address)
        {
            if (address.BitIndex is not null || address.Device is not ("W" or "SW" or "D" or "SD" or "R" or "ZR"))
                throw new InvalidDataException("MELSEC 배열에는 Word 단위 연속 주소가 필요합니다.");
        }
#endregion

#region Polling
        // Reading a snapshot never starts communication or enqueues a device read.
        // A complete block (including all bits of each word) is published under one lock.
        public IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE> GetCachedSnapshot() => GetCachedSnapshot(out _);
        // Return matching map metadata as well, so a map reload cannot mix a new
        // scale/type with a sample decoded using the previous configuration.
        public IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE> GetCachedSnapshot(out IReadOnlyList<ST_MELSEC_MAP_DATA> map)
        {
            var now = DateTimeOffset.UtcNow;
            lock (_pollLock)
            {
                map = Map;
                return _cachedValues.ToDictionary(pair => pair.Key, pair => WithCacheFreshness(pair.Key, pair.Value, now), StringComparer.OrdinalIgnoreCase);
            }
        }

        // These selective reads retain the same freshness rules and atomic map/sample
        // pairing as a full snapshot. They never start I/O or change polling intervals.
        public IReadOnlyDictionary<string, ST_MELSEC_CACHED_VALUE> GetCachedSnapshot(IEnumerable<string> ids, out IReadOnlyList<ST_MELSEC_MAP_DATA> map)
        {
            ArgumentNullException.ThrowIfNull(ids);
            var now = DateTimeOffset.UtcNow;
            var result = new Dictionary<string, ST_MELSEC_CACHED_VALUE>(StringComparer.OrdinalIgnoreCase);
            lock (_pollLock)
            {
                map = Map;
                foreach (var id in ids)
                {
                    var key = NormalizeId(id);
                    if (_cachedValues.TryGetValue(key, out var sample))
                        result[key] = WithCacheFreshness(key, sample, now);
                }
            }
            return result;
        }

        public ST_MELSEC_CACHED_VALUE? GetCachedValue(string id)
        {
            var key = NormalizeId(id);
            lock (_pollLock)
                return _cachedValues.TryGetValue(key, out var sample) ? WithCacheFreshness(key, sample, DateTimeOffset.UtcNow) : null;
        }

        // Control operations such as REL target calculation can require a new sample.
        // Concurrent requests share the central block read instead of reading individually.
        public async Task<ST_MELSEC_CACHED_VALUE> RefreshCachedValueAsync(string id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = NormalizeId(id);
            if (IsIofPositionId(key) && GetMapData(key).PollMs == 0)
            {
                await Task.Run(() => ReadDouble(key, cancellationToken), cancellationToken).ConfigureAwait(false);
                lock (_pollLock)
                    return _cachedValues[key];
            }
            PollBlock block;
            long previousAttempt;
            int generation;
            lock (_pollLock)
            {
                if (!_pollBlockById.TryGetValue(key, out block!))
                {
                    var reason = _cachedValues.TryGetValue(key, out var entry) ? entry.ErrorMessage : "No readable enabled map entry.";
                    throw new InvalidOperationException($"MELSEC cache cannot refresh {id}: {reason}");
                }

                generation = _pollGeneration;
                previousAttempt = block.AttemptNo;
                if (!block.IsReading)
                {
                    block.NextReadAt = DateTimeOffset.MinValue;
                    if (_requestedPollBlockSet.Add(block))
                        _requestedPollBlocks.Enqueue(block);
                }
                _pollDeviceBackoff.Remove(block.DeviceNo);
            }

            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < DefaultWriteTimeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_acceptRequests || !IsRunning)
                    throw new InvalidOperationException("MELSEC communication is not running.");
                lock (_pollLock)
                {
                    if (generation != _pollGeneration)
                        throw new InvalidOperationException("MELSEC map changed while waiting for feedback.");
                    if (block.AttemptNo > previousAttempt && _cachedValues.TryGetValue(key, out var sample))
                    {
                        sample = WithCacheFreshness(key, sample, DateTimeOffset.UtcNow);
                        if (sample.IsValid)
                            return sample;
                        throw new IOException($"MELSEC feedback is unavailable: {id}. {sample.ErrorMessage}");
                    }
                }

                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException($"MELSEC fresh feedback timed out: {id}.");
        }

        private ST_MELSEC_CACHED_VALUE WithCacheFreshness(string id, ST_MELSEC_CACHED_VALUE sample, DateTimeOffset now)
        {
            if (IsIofPositionId(id) && !_pollBlockById.ContainsKey(id))
                return sample; // Last explicit readback, not live motion/safety feedback.
            var interval = _pollBlockById.TryGetValue(id, out var block) ? block.IntervalMs : DefaultPollingIntervalMs;
            if (sample.IsValid && sample.UpdatedAt is { } updated && now - updated > TimeSpan.FromMilliseconds(Math.Max(1000L, (long)interval * 3)))
                return sample with
                {
                    Value = null,
                    State = EN_MELSEC_CACHE_STATE.Stale,
                    ErrorMessage = "MELSEC feedback has not been updated within its polling interval."
                };
            return sample;
        }

        private void RebuildPollingMap(Dictionary<string, ST_MELSEC_MAP_DATA> map)
        {
            var sortedMap = Array.AsReadOnly(map.Values.OrderBy(row => row.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.DeviceNo).ThenBy(row => row.Id, StringComparer.OrdinalIgnoreCase).ToArray());
            var items = new List<PollItem>();
            var entries = new Dictionary<string, ST_MELSEC_CACHED_VALUE>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in map.Values.Where(row => row.Use && row.Access != EN_MELSEC_ACCESS.Write))
            {
                var id = NormalizeId(row.Id);
                try
                {
                    ValidateMapData(row);
                    if (IsIofPositionId(id) && row.PollMs == 0)
                    {
                        entries[id] = new(id, null, EN_MELSEC_CACHE_STATE.Pending, null, 0, "IOF coordinate: read on demand for actual Count.");
                        continue;
                    }
                    var count = row.DataType switch
                    {
                        EN_MELSEC_DATA_TYPE.Bit or EN_MELSEC_DATA_TYPE.Word => 1,
                        // The cache stores one scalar, not the IOF coordinate array.
                        // Its full LENGTH is read explicitly when preparing IOF.
                        EN_MELSEC_DATA_TYPE.Double => 2,
                        EN_MELSEC_DATA_TYPE.String => row.Length,
                        _ => Math.Max(2, row.Length)};
                    if (count < 1 || count > MelsecNetMaximumTransferBytes / sizeof(short))
                        throw new InvalidDataException($"MELSEC polling length is invalid: {row.Id}, {count} words.");
                    var address = ParseAddress(row.Address);
                    _ = checked(address.Number + count - 1);
                    items.Add(new PollItem(row, address, count, row.PollMs > 0 ? row.PollMs : DefaultPollingIntervalMs));
                    entries[id] = new(id, null, EN_MELSEC_CACHE_STATE.Pending, null, 0, "Waiting for MELSEC feedback.");
                }
                catch (Exception exception)when (exception is FormatException or InvalidDataException or InvalidOperationException or NotSupportedException or OverflowException)
                {
                    entries[id] = new(id, null, EN_MELSEC_CACHE_STATE.Error, null, 0, exception.Message);
                }
            }

            var blocks = new List<PollBlock>();
            foreach (var group in items.GroupBy(item => (item.Data.DeviceNo, item.Address.Device)))
            {
                PollBlock? current = null;
                foreach (var item in group.OrderBy(item => item.Address.Number).ThenBy(item => item.WordCount))
                {
                    var end = checked(item.Address.Number + item.WordCount - 1);
                    // Word addresses advance by one word. Do not assume that address
                    // arithmetic for native bit devices (B/X/Y etc.) has that meaning.
                    var wordAddressed = item.Address.Device is "W" or "SW" or "D" or "SD" or "R" or "ZR";
                    var overlaps = current != null && item.Address.Number <= current.End;
                    var adjacent = current != null && (long)item.Address.Number == (long)current.End + 1 && item.IntervalMs == current.IntervalMs;
                    var canMerge = current != null && (wordAddressed ? overlaps || adjacent : item.Address.Number == current.Start && item.WordCount == current.WordCount) && (long)Math.Max(end, current.End) - current.Start + 1 <= MelsecNetMaximumTransferBytes / sizeof(short);
                    if (!canMerge)
                    {
                        current = new PollBlock(item);
                        blocks.Add(current);
                    }
                    else
                    {
                        current!.Items.Add(item);
                        current.End = Math.Max(current.End, end);
                        current.IntervalMs = Math.Min(current.IntervalMs, item.IntervalMs);
                    }
                }
            }

            lock (_pollLock)
            {
                lock (_mapLock)
                {
                    _map = map;
                    Volatile.Write(ref _mapSnapshot, sortedMap);
                }
                _pollGeneration++;
                _pollBlocks = blocks;
                _requestedPollBlocks.Clear();
                _requestedPollBlockSet.Clear();
                _serveRequestedPoll = true;
                _nextPollBlock = 0;
                _pollDeviceBackoff.Clear();
                _pollBlockById.Clear();
                _cachedValues.Clear();
                foreach (var pair in entries)
                    _cachedValues[pair.Key] = pair.Value;
                foreach (var block in blocks)
                    foreach (var item in block.Items)
                        _pollBlockById[NormalizeId(item.Data.Id)] = block;
                lock (_readStateLock)
                    _readSnapshots.Clear();
            }
        }

        private void RestartPollingSchedule()
        {
            lock (_pollLock)
            {
                _pollDeviceBackoff.Clear();
                foreach (var block in _pollBlocks)
                {
                    block.NextReadAt = DateTimeOffset.MinValue;
                    SetBlockState(block, EN_MELSEC_CACHE_STATE.Pending, "Waiting for MELSEC feedback.");
                }
            }
        }

        private void RestartPollingDevice(int deviceNo)
        {
            lock (_pollLock)
            {
                _pollDeviceBackoff.Remove(deviceNo);
                foreach (var block in _pollBlocks.Where(block => block.DeviceNo == deviceNo))
                {
                    block.NextReadAt = DateTimeOffset.MinValue;
                    SetBlockState(block, EN_MELSEC_CACHE_STATE.Pending, "Waiting for MELSEC feedback.");
                }
            }
        }

        private void InvalidatePollingCache(EN_MELSEC_CACHE_STATE state, string message)
        {
            lock (_pollLock)
            {
                foreach (var block in _pollBlocks)
                    SetBlockState(block, state, message);
            }
        }

        private void InvalidateWrittenCache(int deviceNo, ST_MELSEC_ADDRESS address, int count)
        {
            lock (_pollLock)
            {
                foreach (var block in _pollBlocks.Where(block => block.DeviceNo == deviceNo && block.Address.Device == address.Device && (long)block.Start < (long)address.Number + count && block.End >= address.Number))
                {
                    block.NextReadAt = DateTimeOffset.MinValue;
                    SetBlockState(block, EN_MELSEC_CACHE_STATE.Pending, "Waiting for feedback after write.");
                }
            }
        }

        private void SetBlockState(PollBlock block, EN_MELSEC_CACHE_STATE state, string message)
        {
            foreach (var item in block.Items)
            {
                var id = NormalizeId(item.Data.Id);
                if (_cachedValues.TryGetValue(id, out var previous))
                    _cachedValues[id] = previous with
                    {
                        Value = null,
                        State = state,
                        ErrorMessage = message
                    };
            }
        }

        private void PollNextCacheBlock()
        {
            if (!_acceptRequests)
                return;
            PollBlock? selected = null;
            int generation;
            var now = DateTimeOffset.UtcNow;
            lock (_pollLock)
            {
                generation = _pollGeneration;
                // A sequence readback must not wait behind a complete monitoring sweep.
                // Alternate requested reads with round-robin polling so monitoring still advances.
                var requested = false;
                if (_serveRequestedPoll)
                {
                    while (_requestedPollBlocks.TryDequeue(out var block))
                    {
                        _requestedPollBlockSet.Remove(block);
                        if (block.IsReading || block.NextReadAt > now ||
                            (_pollDeviceBackoff.TryGetValue(block.DeviceNo, out var retryAt) && retryAt > now))
                            continue;
                        selected = block;
                        requested = true;
                        break;
                    }
                }
                for (var scanned = 0; selected is null && scanned < _pollBlocks.Count; scanned++)
                {
                    var index = _nextPollBlock++ % _pollBlocks.Count;
                    if (_nextPollBlock >= _pollBlocks.Count)
                        _nextPollBlock = 0;
                    var block = _pollBlocks[index];
                    if (block.NextReadAt > now || (_pollDeviceBackoff.TryGetValue(block.DeviceNo, out var retryAt) && retryAt > now))
                        continue;
                    selected = block;
                }
                if (selected is not null)
                {
                    selected.IsReading = true;
                    _serveRequestedPoll = !requested;
                }
            }

            if (selected == null)
                return;
            try
            {
                var first = selected.Items[0].Data;
                var connection = GetInterfaceData(first);
                if (!IsSimulation(connection))
                {
                    bool open;
                    lock (_ioLock)
                        open = _melsecNetPaths.ContainsKey(selected.DeviceNo);
                    if (!open)
                    {
                        CompletePollingFailure(selected, generation, EN_MELSEC_CACHE_STATE.Offline, "MELSEC communication line is not open.");
                        return;
                    }
                }

                var words = ReadWords(first, selected.Address, selected.WordCount, CancellationToken.None, suppressLog: true);
                var updatedAt = DateTimeOffset.UtcNow;
                var cycle = ReadCycleNo;
                var values = selected.Items.Select(item => (Item: item, Value: DecodePollingValue(item, words, selected.Start))).ToArray();
                lock (_pollLock)
                {
                    if (generation != _pollGeneration || !_acceptRequests)
                        return;
                    foreach (var pair in values)
                    {
                        var id = NormalizeId(pair.Item.Data.Id);
                        _cachedValues[id] = new(id, pair.Value, EN_MELSEC_CACHE_STATE.Valid, updatedAt, cycle, "");
                        SaveReadSnapshot(id, pair.Value);
                    }

                    selected.AttemptNo++;
                    selected.NextReadAt = updatedAt.AddMilliseconds(selected.IntervalMs);
                    _pollDeviceBackoff.Remove(selected.DeviceNo);
                }
            }
            catch (Exception exception)
            {
                CompletePollingFailure(selected, generation, EN_MELSEC_CACHE_STATE.Error, exception.Message);
            }
            finally
            {
                lock (_pollLock)
                    selected.IsReading = false;
            }
        }

        private void CompletePollingFailure(PollBlock block, int generation, EN_MELSEC_CACHE_STATE state, string message)
        {
            lock (_pollLock)
            {
                if (generation != _pollGeneration || !_acceptRequests)
                    return;
                block.AttemptNo++;
                var retryAt = DateTimeOffset.UtcNow.AddMilliseconds(PollingFailureBackoffMs);
                block.NextReadAt = retryAt;
                _pollDeviceBackoff[block.DeviceNo] = retryAt;
                foreach (var affected in _pollBlocks.Where(candidate => candidate.DeviceNo == block.DeviceNo))
                    SetBlockState(affected, state, message);
            }
        }

        private static object DecodePollingValue(PollItem item, ushort[] blockWords, int start)
        {
            var words = blockWords.AsSpan(item.Address.Number - start, item.WordCount).ToArray();
            switch (item.Data.DataType)
            {
                case EN_MELSEC_DATA_TYPE.Bit:
                    return (words[0] & (1 << item.Address.BitIndex!.Value)) != 0;
                case EN_MELSEC_DATA_TYPE.Word:
                    return ScaleInteger(words[0], item.Data.ReadScale);
                case EN_MELSEC_DATA_TYPE.DWord:
                    return ScaleInteger(WordsToInt32(words), item.Data.ReadScale);
                case EN_MELSEC_DATA_TYPE.Double:
                    return WordsToInt32(words) * item.Data.ReadScale;
                case EN_MELSEC_DATA_TYPE.Float:
                    return WordsToFloat(words) * item.Data.ReadScale;
                case EN_MELSEC_DATA_TYPE.String:
                    var bytes = new byte[words.Length * 2];
                    for (var index = 0; index < words.Length; index++)
                    {
                        bytes[index * 2] = (byte)words[index];
                        bytes[index * 2 + 1] = (byte)(words[index] >> 8);
                    }

                    return Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');
                default:
                    throw new InvalidDataException($"Unsupported MELSEC cache type: {item.Data.DataType}.");
            }
        }

        private static object ScaleInteger(int rawValue, double readScale) => readScale == 1 ? (object)rawValue : rawValue * readScale;
        private sealed record PollItem(ST_MELSEC_MAP_DATA Data, ST_MELSEC_ADDRESS Address, int WordCount, int IntervalMs);
        private sealed class PollBlock(PollItem first)
        {
            public List<PollItem> Items { get; } = [first];
            public int DeviceNo => Items[0].Data.DeviceNo;
            public ST_MELSEC_ADDRESS Address => first.Address with
            {
                BitIndex = null
            };
            public int Start => first.Address.Number;
            public int End { get; set; } = first.Address.Number + first.WordCount - 1;
            public int WordCount => End - Start + 1;
            public int IntervalMs { get; set; } = first.IntervalMs;
            public DateTimeOffset NextReadAt { get; set; } = DateTimeOffset.MinValue;
            public long AttemptNo { get; set; }
            public bool IsReading { get; set; }
        }
#endregion

#region StageMotionSimulation
        // Called only by the normal simulation read path. Read-only motor feedback is
        // encoded into the same simulated PLC memory used by the common polling cache.
        private void UpdateStageMotionSimulationWords(int deviceNo, ST_MELSEC_ADDRESS start, int wordCount)
        {
            var feedback = _interfaceManager.GetStageMotionSimulationFeedback(deviceNo);
            var rows = Map.Where(row => row.DeviceNo == deviceNo && row.Access == EN_MELSEC_ACCESS.Read && feedback.ContainsKey(row.Id)).ToArray();
            foreach (var row in rows)
            {
                var address = ParseAddress(row.Address);
                if (address.Device != start.Device || address.Number < start.Number || (long)address.Number + row.Length > (long)start.Number + wordCount)
                    continue;
                var value = feedback[row.Id];
                if (row.DataType is EN_MELSEC_DATA_TYPE.Word or EN_MELSEC_DATA_TYPE.DWord or EN_MELSEC_DATA_TYPE.Double or EN_MELSEC_DATA_TYPE.Float)
                    WriteSimulationValue(row, value);
            }
        }
#endregion

#region StageSimulation
        public static bool IsStageFlowSimulationSignal(ST_MELSEC_MAP_DATA data) => data.Use && data.Direction == EN_MELSEC_DIRECTION.In && data.Access != EN_MELSEC_ACCESS.Write && data.DataType == EN_MELSEC_DATA_TYPE.DWord && data.Length == 2 && (IsStageSimulationSignal(data) || data.Id is "IPS_REVIEW_CONTROL_MODE_X" or "IPS_REVIEW_CONTROL_MODE_Y" or "IPS_IOF_READ_COMPLETE" or "IPS_IOF_BUFFER_RUN_COMPLETE");
        public void SetStageFlowSimulationValue(string id, int value)
        {
            lock (_pollLock)
            {
                var data = GetMapData(id);
                if (!IsStageFlowSimulationSignal(data) || !IsSimulation(GetInterfaceData(data)))
                    throw new InvalidOperationException("MELSEC Simulation의 Stage Move / IOF 수신 항목만 변경할 수 있습니다: " + id);
                if (value is not (0 or 1))
                    throw new ArgumentOutOfRangeException(nameof(value));
                WriteSimulationValue(data, value);
                if (_pollBlockById.TryGetValue(NormalizeId(id), out var block))
                    block.NextReadAt = DateTimeOffset.MinValue;
            }
        }

        public static bool IsStageProtectWindowSimulationSignal(ST_MELSEC_MAP_DATA data) => data.Use && data.Direction == EN_MELSEC_DIRECTION.In && data.Access != EN_MELSEC_ACCESS.Write && double.IsFinite(data.ReadScale) && data.ReadScale > 0 && ((data.Id.Equals(StageProtectWindowCountId, StringComparison.OrdinalIgnoreCase) && data.DataType == EN_MELSEC_DATA_TYPE.Word && data.Length == 1) || (data.Id.Equals(StageProtectWindowCompleteId, StringComparison.OrdinalIgnoreCase) && data.DataType == EN_MELSEC_DATA_TYPE.DWord && data.Length == 2));
        /// <summary>Stage-owned Tray Count / completion input in simulation memory only.</summary>
        public void SetStageProtectWindowSimulationValue(string id, int value)
        {
            lock (_pollLock)
            {
                var data = GetMapData(id);
                if (!IsStageProtectWindowSimulationSignal(data))
                    throw new InvalidOperationException($"Stage 수신용 Protect Window 항목이 아닙니다: {id}");
                var isCount = data.Id.Equals(StageProtectWindowCountId, StringComparison.OrdinalIgnoreCase);
                if (isCount ? value is < 0 or > ushort.MaxValue : value is not (0 or 1))
                    throw new ArgumentOutOfRangeException(nameof(value), isCount ? "Tray Count 원시값은 0~65535의 정수여야 합니다." : "교체완료 신호는 0 또는 1이어야 합니다.");
                if (!IsSimulation(GetInterfaceData(data)))
                    throw new InvalidOperationException($"MELSEC_{data.DeviceNo}가 SIMULATION 상태일 때만 변경할 수 있습니다.");
                WriteSimulationValue(data, isCount ? value * data.ReadScale : value);
                if (_pollBlockById.TryGetValue(NormalizeId(id), out var block))
                    block.NextReadAt = DateTimeOffset.MinValue;
            }
        }

        public static bool IsStageSimulationSignal(ST_MELSEC_MAP_DATA data) => data.Use && data.Id.StartsWith("HANDSHAKE_STG_", StringComparison.OrdinalIgnoreCase) && data.Direction == EN_MELSEC_DIRECTION.In && data.Access != EN_MELSEC_ACCESS.Write && data.DataType == EN_MELSEC_DATA_TYPE.DWord && data.Length == 2 && double.IsFinite(data.ReadScale) && data.ReadScale > 0;
        public static bool IsStageAlignSimulationSignal(ST_MELSEC_MAP_DATA data) => data.Use && (data.Id.Equals("SDC_DO_ALIGN_STATUS", StringComparison.OrdinalIgnoreCase) || data.Id.Equals("SDC_DO_ALIGN_COMPLETE", StringComparison.OrdinalIgnoreCase) || data.Id.Equals(StageAlignBusyWordId, StringComparison.OrdinalIgnoreCase)) && data.Direction == EN_MELSEC_DIRECTION.In && data.Access != EN_MELSEC_ACCESS.Write && data.DataType == EN_MELSEC_DATA_TYPE.Word && data.Length == 1;
        /// <summary>Writes a Stage-owned Align raw Word into simulation memory, never to a PLC.</summary>
        public void SetStageAlignSimulationValue(string id, int rawValue)
        {
            if (rawValue is < 0 or > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(rawValue), "1 WORD 원시값은 0~65535의 정수여야 합니다.");
            lock (_pollLock)
            {
                var data = GetMapData(id);
                if (!IsStageAlignSimulationSignal(data))
                    throw new InvalidOperationException($"Stage 수신용 ALIGN 신호가 아닙니다: {id}");
                if (!IsSimulation(GetInterfaceData(data)))
                    throw new InvalidOperationException($"MELSEC_{data.DeviceNo}가 SIMULATION 상태일 때만 변경할 수 있습니다.");
                WriteSimulationValue(data, rawValue * data.ReadScale);
                if (_pollBlockById.TryGetValue(NormalizeId(id), out var block))
                    block.NextReadAt = DateTimeOffset.MinValue;
            }
        }

        /// <summary>
        /// Emulates the Stage PC's input in memory only. Does not use native writes,
        /// change map access, or publish UI/action flags. The common poller reads it normally.
        /// </summary>
        public void SetStageSimulationSignal(string id, bool on)
        {
            lock (_pollLock)
            {
                var data = GetMapData(id);
                if (!IsStageSimulationSignal(data))
                    throw new InvalidOperationException($"Stage 수신용 핸드셰이크 신호가 아닙니다: {id}");
                if (!IsSimulation(GetInterfaceData(data)))
                    throw new InvalidOperationException($"MELSEC_{data.DeviceNo}가 SIMULATION 상태일 때만 변경할 수 있습니다.");
                WriteSimulationValue(data, on ? 1 : 0);
                if (_pollBlockById.TryGetValue(NormalizeId(id), out var block))
                    block.NextReadAt = DateTimeOffset.MinValue;
            }
        }
#endregion
    }

    public enum EN_MELSEC_DATA_TYPE
    {
        Bit,
        Word,
        DWord,
        // DOUBLE is the existing map name for a scaled signed Int32 occupying two words.
        // All numeric APIs/snapshots expose READ_SCALE-adjusted values and apply WRITE_SCALE on writes.
        Double,
        Float,
        String
    }

    public enum EN_MELSEC_DIRECTION
    {
        In,
        Out,
        InOut
    }

    public enum EN_MELSEC_ACCESS
    {
        Read,
        Write,
        ReadWrite
    }

    public enum EN_MELSEC_PROCESS
    {
        Ready,
        PrepareWrite,
        Write,
        WaitReadback,
        RetryWrite,
        CommunicationError,
        Close
    }

    public enum EN_MELSEC_WRITE_RESULT
    {
        None,
        Queued,
        Writing,
        WriteSuccess,
        WaitReadback,
        Confirmed,
        Timeout,
        CommunicationError,
        InvalidParameter,
        Cancelled
    }

    public enum EN_MELSEC_SIMULATION_READBACK
    {
        AutoEcho,
        HoldValue,
        FailFirstAttempt,
        CommunicationError
    }

    public sealed record ST_MELSEC_WRITE_STATUS(
        int RequestNo, 
        string WriteId, 
        string ReadbackId, 
        string ExpectedValue, 
        string ActualValue, 
        EN_MELSEC_WRITE_RESULT Result, 
        int CurrentRetryCount, 
        int RetryCount, 
        int TimeoutMs, 
        long WriteBeforeReadCycle, 
        long MinimumReadCycle, 
        long ConfirmReadCycle, 
        string ErrorMessage
    );
    public sealed record ST_MELSEC_MAP_DATA(
        string Id, 
        bool Use, 
        string Group, 
        string Name, 
        int DeviceNo, 
        string Address, 
        EN_MELSEC_DATA_TYPE DataType, 
        EN_MELSEC_DIRECTION Direction, 
        EN_MELSEC_ACCESS Access, 
        double ReadScale, 
        double WriteScale, 
        int Length, 
        int PollMs, 
        string Description
    );
    public abstract class CMelsecMapFileBase
    {
        public abstract IReadOnlyList<ST_MELSEC_MAP_DATA> LoadAll(CancellationToken cancellationToken = default);
    }

    public enum EN_MELSEC_CACHE_STATE
    {
        Pending,
        Valid,
        Stale,
        Error,
        Offline
    }

    public sealed record ST_MELSEC_CACHED_VALUE(string Id, object? Value, EN_MELSEC_CACHE_STATE State, DateTimeOffset? UpdatedAt, long ReadCycleNo, string ErrorMessage)
    {
        public bool IsValid => State == EN_MELSEC_CACHE_STATE.Valid;
    }

    public class CMelsecNetApi
    {
        public const short OpenMode = -1;
        public const string NativeLibraryName = "MDFUNC32.dll";
        public virtual int Open(short channelNo, out int path)
        {
            return CMelsecNetNativeMethods.mdOpen(channelNo, OpenMode, out path);
        }

        public virtual int Close(int path)
        {
            return CMelsecNetNativeMethods.mdClose(path);
        }

        public virtual int SendEx(int path, int networkNo, int stationNo, int deviceType, int deviceNo, ref int size, short[] data)
        {
            return CMelsecNetNativeMethods.mdSendEx(path, networkNo, stationNo, deviceType, deviceNo, ref size, data);
        }

        public virtual int ReceiveEx(int path, int networkNo, int stationNo, int deviceType, int deviceNo, ref int size, short[] data)
        {
            return CMelsecNetNativeMethods.mdReceiveEx(path, networkNo, stationNo, deviceType, deviceNo, ref size, data);
        }
    }

    internal static class CMelsecNetNativeMethods
    {
        [DllImport(CMelsecNetApi.NativeLibraryName, EntryPoint = "mdOpen", CallingConvention = CallingConvention.StdCall)]
        internal static extern short mdOpen(short channelNo, short mode, out int path);
        [DllImport(CMelsecNetApi.NativeLibraryName, EntryPoint = "mdClose", CallingConvention = CallingConvention.StdCall)]
        internal static extern short mdClose(int path);
        [DllImport(CMelsecNetApi.NativeLibraryName, EntryPoint = "mdSendEx", CallingConvention = CallingConvention.StdCall)]
        internal static extern int mdSendEx(int path, int networkNo, int stationNo, int deviceType, int deviceNo, ref int size, [In] short[] data);
        [DllImport(CMelsecNetApi.NativeLibraryName, EntryPoint = "mdReceiveEx", CallingConvention = CallingConvention.StdCall)]
        internal static extern int mdReceiveEx(int path, int networkNo, int stationNo, int deviceType, int deviceNo, ref int size, [Out] short[] data);
    }
}
