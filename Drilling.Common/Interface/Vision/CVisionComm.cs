namespace Drilling.Common.Interface.Vision
{
    using System.Collections.Concurrent;
    using System.Globalization;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Buffers.Binary;

    [CCommType("SocketServer", "Vision")]
    internal sealed class CVisionComm(ST_INTERFACE_DATA data, ST_INTERFACE_CONNECT_OPTION option) : CCommBase(data, option)
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<ST_VISION_PACKET>> _pending = new();
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly SemaphoreSlim _clientReady = new(0, 1);
        private TcpListener? _listener;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _serverCancellation;
        private Task? _acceptTask;
        private Task? _receiveTask;
        private int _uniqueId;
        private int _flyingLineCapacity;
        // Read the accepted client only; header refresh must not send STATUS or
        // probe the socket. ReceiveLoop clears the client on disconnect.
        internal bool HasConnectedClient => Volatile.Read(ref _client)?.Connected == true;
        internal Func<int>? LineCapacityProvider { get; set; }
        private int ConfiguredLineCapacity => CVisionProtocol.ValidateLineCapacity(LineCapacityProvider?.Invoke() ?? CVisionProtocol.DefaultMaxHoleCountPerLine);
        private int ResponseLineCapacity => Volatile.Read(ref _flyingLineCapacity) is > 0 and var capacity ? capacity : ConfiguredLineCapacity;
        internal event Action<ST_COMM_PROTOCOL_TRACE>? ProtocolTrace;
        public override async Task Connect(CancellationToken cancellationToken = default)
        {
            await _lifecycleLock.WaitAsync(cancellationToken);
            try
            {
                if (_listener is not null && _serverCancellation is not null && !_serverCancellation.IsCancellationRequested)
                {
                    LastError = "";
                    SetState(EN_COMM_STATE.Online);
                    return;
                }

                await StopServer();
                if (Option.Port <= 0)
                {
                    SetError("Vision server port is invalid.");
                    return;
                }

                var listener = new TcpListener(ResolveBindAddress(Option.LocalAddress), Option.Port);
                listener.Start(1);
                _listener = listener;
                _serverCancellation = new CancellationTokenSource();
                Task AcceptLoopCallback()
                {
                    return AcceptLoop(listener, _serverCancellation.Token);
                }

                _acceptTask = Task.Run(AcceptLoopCallback, CancellationToken.None);
                LastSent = "";
                LastReceived = "";
                LastError = "";
                SetState(EN_COMM_STATE.Online);
                RaiseProtocolTrace("INFO", $"SERVER_LISTENING:{Option.Endpoint}");
            }
            catch (Exception ex)when (ex is SocketException or ArgumentException or InvalidOperationException)
            {
                await StopServer();
                SetError(ex);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public override async Task Disconnect(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _lifecycleLock.WaitAsync(cancellationToken);
            try
            {
                await StopServer();
                SetState(EN_COMM_STATE.Offline);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public override async Task<string> Execute(string function, CancellationToken cancellationToken = default)
        {
            try
            {
                var commandText = function.Trim();
                if (commandText.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
                {
                    LastSent = commandText;
                    LastReceived = _client is { Connected: true } ? "OK:VISION:CLIENT_CONNECTED" : "OK:VISION:WAITING_CLIENT";
                    LastError = "";
                    return LastReceived;
                }

                if (_listener is null)
                {
                    await Connect(cancellationToken);
                }

                var stream = await WaitForClient(cancellationToken);
                var command = NormalizeCommand(commandText.Split(':', 2, StringSplitOptions.TrimEntries)[0]);
                var capacity = command == EN_VISION_COMMAND.MarkFindLine ? ResponseLineCapacity : ConfiguredLineCapacity;
                var request = ParseRequest(commandText, NextUniqueId(), capacity);
                if (command == EN_VISION_COMMAND.FlyingReady)
                    Volatile.Write(ref _flyingLineCapacity, capacity);
                var response = await SendRequest(stream, request, cancellationToken);
                LastReceived = DescribeResponse(response, capacity);
                if (command == EN_VISION_COMMAND.MarkFindLine)
                    Volatile.Write(ref _flyingLineCapacity, 0);
                LastError = "";
                SetState(EN_COMM_STATE.Online);
                return LastReceived;
            }
            catch (CVisionLineCountExceededException ex)
            {
                // This is a received protocol fault, not a missing reply. Preserve
                // its type so the interface owner raises the HEAVY alarm and stops inspection.
                SetCommandError(ex.Message);
                throw;
            }
            catch (Exception ex)when (ex is SocketException or IOException or InvalidDataException or InvalidOperationException or FormatException or TimeoutException or OperationCanceledException)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                SetCommandError(ex.Message);
                return "";
            }
        }

        private async Task AcceptLoop(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                    client.NoDelay = true;
                    ReplaceClient(client, cancellationToken);
                }
                catch (Exception ex)when (cancellationToken.IsCancellationRequested || ex is ObjectDisposedException or SocketException or OperationCanceledException)
                {
                    client?.Dispose();
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        LastError = ex.Message;
                        LastChangedAt = DateTimeOffset.Now;
                    }
                }
            }
        }

        private void ReplaceClient(TcpClient client, CancellationToken cancellationToken)
        {
            _client?.Dispose();
            _client = client;
            _stream = client.GetStream();
            Task ReceiveLoopCallback()
            {
                return ReceiveLoop(client, _stream, cancellationToken);
            }

            _receiveTask = Task.Run(ReceiveLoopCallback, CancellationToken.None);
            if (_clientReady.CurrentCount == 0)
            {
                _clientReady.Release();
            }

            LastError = "";
            SetState(EN_COMM_STATE.Online);
            RaiseProtocolTrace("INFO", $"CLIENT_CONNECTED:{client.Client.RemoteEndPoint?.ToString() ?? "UNKNOWN"}");
        }

        private async Task ReceiveLoop(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && client.Connected)
                {
                    var packet = await ReadPacket(stream, cancellationToken);
                    LastReceived = DescribePacket(packet);
                    LastChangedAt = DateTimeOffset.Now;
                    if (packet.Header.Command == (int)EN_VISION_COMMAND.KeepAlive)
                    {
                        LastError = "";
                        SetState(EN_COMM_STATE.Online);
                    }

                    RaiseProtocolTrace("PACKET", CVisionProtocol.FormatPacketLog("RECV", packet, $"{Data.NickName}[{Data.Number}]", ResponseLineCapacity));
                    if (packet.Header.Command == (int)EN_VISION_COMMAND.KeepAlive)
                    {
                        await SendPacket(stream, CVisionProtocol.CreateEmpty(EN_VISION_COMMAND.KeepAlive, packet.Header.UniqueId, packet.Header.ErrorCode), cancellationToken);
                        continue;
                    }

                    if (packet.Header.Command == (int)EN_VISION_COMMAND.MarkFindManual)
                    {
                        await SendPacket(stream, CVisionProtocol.CreateEmpty(EN_VISION_COMMAND.MarkFindManual, packet.Header.UniqueId), cancellationToken);
                        continue;
                    }

                    if (_pending.TryRemove(packet.Header.UniqueId, out var completion))
                    {
                        completion.TrySetResult(packet);
                    }
                }
            }
            catch (Exception ex)when (ex is SocketException or IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    LastError = ex.Message;
                    LastChangedAt = DateTimeOffset.Now;
                }
            }
            finally
            {
                if (ReferenceEquals(_client, client))
                {
                    _stream = null;
                    _client = null;
                    while (_clientReady.CurrentCount > 0)
                    {
                        _clientReady.Wait(0);
                    }

                    FailPending(new IOException("Vision client disconnected."));
                    LastError = "Vision client disconnected.";
                    SetState(IsServerListening() ? EN_COMM_STATE.Online : EN_COMM_STATE.Offline);
                    RaiseProtocolTrace("INFO", "CLIENT_DISCONNECTED");
                }

                client.Dispose();
            }
        }

        private async Task<NetworkStream> WaitForClient(CancellationToken cancellationToken)
        {
            if (_stream is not null && _client is { Connected: true })
            {
                return _stream;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Max(100, Option.TimeoutMs));
            try
            {
                await _clientReady.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Vision client connection timeout.");
            }

            return _stream ?? throw new IOException("Vision client disconnected before command transmission.");
        }

        private async Task<ST_VISION_PACKET> SendRequest(NetworkStream stream, ST_VISION_PACKET request, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<ST_VISION_PACKET>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(request.Header.UniqueId, completion))
            {
                throw new InvalidOperationException($"Vision Unique ID is already pending: {request.Header.UniqueId}.");
            }

            try
            {
                await SendPacket(stream, request, cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(Math.Max(100, Option.TimeoutMs));
                try
                {
                    var response = await completion.Task.WaitAsync(timeout.Token);
                    if (response.Header.Command != request.Header.Command)
                    {
                        throw new InvalidDataException($"Vision response command mismatch. Request={request.Header.Command}, Response={response.Header.Command}.");
                    }

                    if (response.Header.ErrorCode != 0)
                    {
                        throw new InvalidDataException($"Vision returned error code {response.Header.ErrorCode} for command {response.Header.Command}.");
                    }

                    return response;
                }
                catch (OperationCanceledException)when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Vision command {request.Header.Command} response timeout.");
                }
            }
            finally
            {
                _pending.TryRemove(request.Header.UniqueId, out _);
            }
        }

        private async Task SendPacket(NetworkStream stream, ST_VISION_PACKET packet, CancellationToken cancellationToken)
        {
            var bytes = CVisionProtocol.Encode(packet);
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                LastSent = DescribePacket(packet);
                LastChangedAt = DateTimeOffset.Now;
            RaiseProtocolTrace("PACKET", CVisionProtocol.FormatPacketLog("SEND", packet, $"{Data.NickName}[{Data.Number}]", ResponseLineCapacity));
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private static async Task<ST_VISION_PACKET> ReadPacket(NetworkStream stream, CancellationToken cancellationToken)
        {
            var headerBytes = new byte[CVisionProtocol.HeaderSize];
            await stream.ReadExactlyAsync(headerBytes, cancellationToken);
            var header = CVisionProtocol.DecodeHeader(headerBytes);
            var body = new byte[header.DataLength];
            if (body.Length > 0)
            {
                await stream.ReadExactlyAsync(body, cancellationToken);
            }

            return new ST_VISION_PACKET(header, body);
        }

        private static ST_VISION_PACKET ParseRequest(string function, int uniqueId, int maxHoleCountPerLine)
        {
            var fields = function.Split(':', 2, StringSplitOptions.TrimEntries);
            var command = NormalizeCommand(fields[0]);
            var body = fields.Length > 1 ? fields[1] : "";
            return command switch
            {
                EN_VISION_COMMAND.MarkFindSingle => CVisionProtocol.CreateMarkFindSingle(uniqueId, ParseMarkSearch(body)),
                EN_VISION_COMMAND.FlyingReady => ParseFlyingReady(uniqueId, body, maxHoleCountPerLine),
                EN_VISION_COMMAND.FlyingMoveDone => CVisionProtocol.CreateEmpty(command, uniqueId),
                EN_VISION_COMMAND.MarkFindLine => CVisionProtocol.CreateEmpty(command, uniqueId),
                EN_VISION_COMMAND.MarkFindSettingSingle => CVisionProtocol.CreateEmpty(command, uniqueId),
                EN_VISION_COMMAND.KeepAliveRequest => CVisionProtocol.CreateEmpty(command, 0),
                _ => throw new InvalidOperationException($"Vision command cannot be sent by Process: {(int)command}.")};
        }

        private static ST_VISION_PACKET ParseFlyingReady(int uniqueId, string body, int maxHoleCountPerLine)
        {
            var parts = body.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inspectionType))
            {
                throw new FormatException("FLYING_READY format: FLYING_READY:type:cell,doe,line,indexX,indexY,head;...");
            }

            var searches = new List<ST_VISION_MARK_SEARCH>();
            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                var values = parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var value in values)
                {
                    searches.Add(ParseMarkSearch(value));
                }
            }

            return CVisionProtocol.CreateFlyingReady(uniqueId, inspectionType, searches, maxHoleCountPerLine);
        }

        private static ST_VISION_MARK_SEARCH ParseMarkSearch(string value)
        {
            var fields = value.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length != 6)
            {
                throw new FormatException("Mark Search format: cell,doe,line,indexX,indexY,head");
            }

            return new ST_VISION_MARK_SEARCH(ParseInt(fields[0], "cell"), ParseInt(fields[1], "doe"), ParseInt(fields[2], "line"), ParseInt(fields[3], "indexX"), ParseInt(fields[4], "indexY"), ParseInt(fields[5], "head"));
        }

        private static EN_VISION_COMMAND NormalizeCommand(string value)
        {
            var normalized = value.Trim().Replace("-", "_", StringComparison.Ordinal).ToUpperInvariant();
            return normalized switch
            {
                "1000" or "MARK_FIND_SINGLE" => EN_VISION_COMMAND.MarkFindSingle,
                "1001" or "FLYING_READY" => EN_VISION_COMMAND.FlyingReady,
                "1002" or "FLYING_MOVE_DONE" => EN_VISION_COMMAND.FlyingMoveDone,
                "1003" or "MARK_FIND_LINE" => EN_VISION_COMMAND.MarkFindLine,
                "1010" or "MARK_FIND_SETTING_SINGLE" => EN_VISION_COMMAND.MarkFindSettingSingle,
                "1900" or "KEEP_ALIVE_REQUEST" => EN_VISION_COMMAND.KeepAliveRequest,
                _ => throw new InvalidOperationException($"Unsupported Vision command: {value}.")};
        }

        private static int ParseInt(string value, string name) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : throw new FormatException($"Vision {name} must be an integer. Value={value}.");
        private static string DescribeResponse(ST_VISION_PACKET packet, int maxHoleCountPerLine)
        {
            if (packet.Header.Command == (int)EN_VISION_COMMAND.FlyingMoveDone)
            {
                // Count matching is the response body's Int32, independent of the
                // header error code. Preserve NG as a reply so the sequence can retry.
                var countMatch = CVisionProtocol.DecodeFlyingMoveDoneResponse(packet);
                return $"{(countMatch == 0 ? "OK" : "NCK")}:1002:UID={packet.Header.UniqueId}:HOLE_COUNT_MATCH={countMatch}";
            }
            if (packet.Header.Command is (int)EN_VISION_COMMAND.MarkFindSingle or (int)EN_VISION_COMMAND.MarkFindSettingSingle or (int)EN_VISION_COMMAND.MarkFindManual)
            {
                var result = CVisionProtocol.DecodeSingleResult(packet);
                return string.Create(CultureInfo.InvariantCulture, $"OK:{packet.Header.Command}:UID={packet.Header.UniqueId}:RESOLUTION={result.Resolution}:RESULT={result.Mark.Result}:TARGET_X={result.Mark.TargetMarkX:R}:TARGET_Y={result.Mark.TargetMarkY:R}:HOLE_X={result.Mark.HoleX:R}:HOLE_Y={result.Mark.HoleY:R}");
            }

            if (packet.Header.Command == (int)EN_VISION_COMMAND.MarkFindLine)
            {
                var result = CVisionProtocol.DecodeLineResult(packet, maxHoleCountPerLine);
                var builder = new StringBuilder();
                builder.Append("OK:").Append(packet.Header.Command).Append(":UID=").Append(packet.Header.UniqueId).Append(":RESOLUTION=").Append(result.Resolution).Append(":COUNT=").Append(result.FindHoleCount).Append(":MARKS=");
                for (var index = 0; index < result.Marks.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(';');
                    }

                    var mark = result.Marks[index];
                    builder.Append(mark.Result).Append(',').Append(mark.TargetMarkX.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(mark.TargetMarkY.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(mark.HoleX.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(mark.HoleY.ToString("R", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }

            return $"OK:{packet.Header.Command}:UID={packet.Header.UniqueId}";
        }

        private static string DescribePacket(ST_VISION_PACKET packet) => $"CMD={packet.Header.Command},LEN={packet.Header.DataLength},UID={packet.Header.UniqueId},ERR={packet.Header.ErrorCode}";
        private int NextUniqueId()
        {
            var value = Interlocked.Increment(ref _uniqueId);
            if (value > 0)
            {
                return value;
            }

            Interlocked.Exchange(ref _uniqueId, 1);
            return 1;
        }

        private void RaiseProtocolTrace(string direction, string message)
        {
            ProtocolTrace?.Invoke(new ST_COMM_PROTOCOL_TRACE(DateTimeOffset.Now, direction, message));
        }

        private bool IsServerListening()
        {
            return _listener is not null && _serverCancellation is not null && !_serverCancellation.IsCancellationRequested;
        }

        private async Task StopServer()
        {
            Volatile.Write(ref _flyingLineCapacity, 0);
            _serverCancellation?.Cancel();
            _listener?.Stop();
            _client?.Dispose();
            if (_acceptTask is not null)
            {
                await IgnoreCancellation(_acceptTask);
            }

            if (_receiveTask is not null)
            {
                await IgnoreCancellation(_receiveTask);
            }

            _serverCancellation?.Dispose();
            _serverCancellation = null;
            _listener = null;
            _client = null;
            _stream = null;
            _acceptTask = null;
            _receiveTask = null;
            while (_clientReady.CurrentCount > 0)
            {
                _clientReady.Wait(0);
            }

            FailPending(new IOException("Vision server stopped."));
        }

        private void FailPending(Exception exception)
        {
            foreach (var item in _pending.ToArray())
            {
                if (_pending.TryRemove(item.Key, out var completion))
                {
                    completion.TrySetException(exception);
                }
            }
        }

        private static async Task IgnoreCancellation(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception ex)when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
            }
        }

        private static IPAddress ResolveBindAddress(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "*")
            {
                return IPAddress.Any;
            }

            return IPAddress.TryParse(value, out var address) ? address : throw new ArgumentException($"Vision server local address is invalid: {value}");
        }
    }

    public enum EN_VISION_COMMAND
    {
        MarkFindSingle = 1000,
        FlyingReady = 1001,
        FlyingMoveDone = 1002,
        MarkFindLine = 1003,
        MarkFindSettingSingle = 1010,
        MarkFindManual = 1020,
        KeepAliveRequest = 1900,
        KeepAlive = 1999
    }

    public readonly record struct ST_VISION_PACKET_HEADER(int Command, int DataLength, int UniqueId, int ErrorCode);
    public readonly record struct ST_VISION_MARK_SEARCH(int Cell, int Doe, int Line, int IndexX, int IndexY, int Head);
    public readonly record struct ST_VISION_MARK_FIND(int Result, double TargetMarkX, double TargetMarkY, double HoleX, double HoleY);
    public sealed record ST_VISION_PACKET(ST_VISION_PACKET_HEADER Header, byte[] Body);
    public sealed record ST_VISION_SINGLE_RESULT(int Resolution, ST_VISION_MARK_FIND Mark);
    public sealed record ST_VISION_LINE_RESULT(int Resolution, int FindHoleCount, IReadOnlyList<ST_VISION_MARK_FIND> Marks);
    public sealed record ST_VISION_FLYING_READY(int InspectionType, IReadOnlyList<ST_VISION_MARK_SEARCH> Searches);
    public sealed class CVisionLineCountExceededException(int actualCount, int maximumCount) : IOException($"Vision 수신 HoleCount({actualCount})가 Setting-Vision MaxHoleCountPerLine({maximumCount})을 초과했습니다.")
    {
        public int ActualCount { get; } = actualCount;
        public int MaximumCount { get; } = maximumCount;
    }
    public static class CVisionProtocol
    {
        private const string PacketLogSeparator = "----------------------------------------------------------------------";
        private const string PacketLogBodyIndent = "       ";
        public const int HeaderSize = 16;
        public const int MarkSearchSize = 24;
        public const int MarkFindPackedSize = 36;
        public const int SingleResultBodySize = 40;
        public const int MaximumBodySize = 1024 * 1024;
        // Frames contain a configured fixed number of slots. Count indicates only
        // valid entries; the body byte limit still bounds the configurable capacity.
        public const int MaximumMarkCount = (MaximumBodySize - 8) / MarkFindPackedSize;
        public const int DefaultMaxHoleCountPerLine = 300;
        public const string MaxHoleCountPerLineSettingKey = "MaxHoleCountPerLine";
        public static int ValidateLineCapacity(int capacity)
        {
            if (capacity < 1 || capacity > MaximumMarkCount)
                throw new ArgumentOutOfRangeException(nameof(capacity), $"MaxHoleCountPerLine은 1~{MaximumMarkCount} 범위의 정수여야 합니다.");
            return capacity;
        }
        public static ST_VISION_PACKET CreateEmpty(EN_VISION_COMMAND command, int uniqueId, int errorCode = 0) => Create(command, uniqueId, [], errorCode);
        public static ST_VISION_PACKET CreateMarkFindSingle(int uniqueId, ST_VISION_MARK_SEARCH search) => Create(EN_VISION_COMMAND.MarkFindSingle, uniqueId, EncodeMarkSearch(search));
        public static ST_VISION_PACKET CreateFlyingReady(int uniqueId, int inspectionType, IReadOnlyList<ST_VISION_MARK_SEARCH> searches, int maxHoleCountPerLine = DefaultMaxHoleCountPerLine)
        {
            ArgumentNullException.ThrowIfNull(searches);
            ValidateLineCapacity(maxHoleCountPerLine);
            if (searches.Count > maxHoleCountPerLine)
            {
                throw new ArgumentOutOfRangeException(nameof(searches), $"Flying Ready의 검사 개수 {searches.Count}가 MaxHoleCountPerLine({maxHoleCountPerLine})을 초과했습니다.");
            }

            var body = new byte[8 + (maxHoleCountPerLine * MarkSearchSize)];
            Array.Fill(body, (byte)0xFF); // Every unused Int32 field is -1.
            WriteInt32(body, 0, inspectionType);
            WriteInt32(body, 4, searches.Count);
            for (var index = 0; index < searches.Count; index++)
            {
                WriteMarkSearch(body, 8 + (index * MarkSearchSize), searches[index]);
            }

            return Create(EN_VISION_COMMAND.FlyingReady, uniqueId, body);
        }

        public static ST_VISION_PACKET CreateSingleResult(EN_VISION_COMMAND command, int uniqueId, int resolution, ST_VISION_MARK_FIND mark, int errorCode = 0)
        {
            if (command is not EN_VISION_COMMAND.MarkFindSingle and not EN_VISION_COMMAND.MarkFindSettingSingle and not EN_VISION_COMMAND.MarkFindManual)
            {
                throw new ArgumentOutOfRangeException(nameof(command), command, "Vision single result supports commands 1000, 1010 and 1020.");
            }

            var body = new byte[SingleResultBodySize];
            WriteInt32(body, 0, resolution);
            WriteMarkFind(body, 4, mark);
            return Create(command, uniqueId, body, errorCode);
        }

        public static ST_VISION_PACKET CreateLineResult(int uniqueId, int resolution, IReadOnlyList<ST_VISION_MARK_FIND> marks, int errorCode = 0, int maxHoleCountPerLine = DefaultMaxHoleCountPerLine)
        {
            ArgumentNullException.ThrowIfNull(marks);
            ValidateLineCapacity(maxHoleCountPerLine);
            if (marks.Count > maxHoleCountPerLine)
            {
                throw new ArgumentOutOfRangeException(nameof(marks), $"Vision line result supports up to {maxHoleCountPerLine} marks.");
            }

            var body = new byte[8 + (maxHoleCountPerLine * MarkFindPackedSize)];
            WriteInt32(body, 0, resolution);
            WriteInt32(body, 4, marks.Count);
            for (var index = 0; index < marks.Count; index++)
            {
                WriteMarkFind(body, 8 + (index * MarkFindPackedSize), marks[index]);
            }
            for (var index = marks.Count; index < maxHoleCountPerLine; index++)
                WriteMarkFind(body, 8 + (index * MarkFindPackedSize), new(-1, -1, -1, -1, -1));

            return Create(EN_VISION_COMMAND.MarkFindLine, uniqueId, body, errorCode);
        }

        public static ST_VISION_PACKET Create(EN_VISION_COMMAND command, int uniqueId, byte[] body, int errorCode = 0)
        {
            ArgumentNullException.ThrowIfNull(body);
            ValidateBodyLength(body.Length);
            return new ST_VISION_PACKET(new ST_VISION_PACKET_HEADER((int)command, body.Length, uniqueId, errorCode), body);
        }

        public static byte[] Encode(ST_VISION_PACKET packet)
        {
            ArgumentNullException.ThrowIfNull(packet);
            ArgumentNullException.ThrowIfNull(packet.Body);
            if (packet.Header.DataLength != packet.Body.Length)
            {
                throw new InvalidDataException($"Vision packet body length mismatch. Header={packet.Header.DataLength}, Body={packet.Body.Length}.");
            }

            ValidateBodyLength(packet.Body.Length);
            var bytes = new byte[HeaderSize + packet.Body.Length];
            WriteInt32(bytes, 0, packet.Header.Command);
            WriteInt32(bytes, 4, packet.Header.DataLength);
            WriteInt32(bytes, 8, packet.Header.UniqueId);
            WriteInt32(bytes, 12, packet.Header.ErrorCode);
            packet.Body.CopyTo(bytes, HeaderSize);
            return bytes;
        }

        public static ST_VISION_PACKET_HEADER DecodeHeader(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < HeaderSize)
            {
                throw new InvalidDataException($"Vision header requires {HeaderSize} bytes. Received={bytes.Length}.");
            }

            var header = new ST_VISION_PACKET_HEADER(ReadInt32(bytes, 0), ReadInt32(bytes, 4), ReadInt32(bytes, 8), ReadInt32(bytes, 12));
            ValidateBodyLength(header.DataLength);
            return header;
        }

        public static ST_VISION_PACKET Decode(ReadOnlySpan<byte> bytes)
        {
            var header = DecodeHeader(bytes);
            var packetLength = HeaderSize + header.DataLength;
            if (bytes.Length != packetLength)
            {
                throw new InvalidDataException($"Vision packet length mismatch. Expected={packetLength}, Received={bytes.Length}.");
            }

            return new ST_VISION_PACKET(header, bytes.Slice(HeaderSize, header.DataLength).ToArray());
        }

        public static ST_VISION_MARK_SEARCH DecodeMarkSearchRequest(ST_VISION_PACKET packet)
        {
            ArgumentNullException.ThrowIfNull(packet);
            if (packet.Header.Command != (int)EN_VISION_COMMAND.MarkFindSingle)
            {
                throw new InvalidDataException($"Command {packet.Header.Command} is not a Mark Find Single request.");
            }

            if (packet.Body.Length != MarkSearchSize)
            {
                throw new InvalidDataException($"Mark Find Single request requires {MarkSearchSize} bytes. Received={packet.Body.Length}.");
            }

            return ReadMarkSearch(packet.Body, 0);
        }

        public static ST_VISION_FLYING_READY DecodeFlyingReadyRequest(ST_VISION_PACKET packet, int maxHoleCountPerLine = DefaultMaxHoleCountPerLine)
        {
            ArgumentNullException.ThrowIfNull(packet);
            if (packet.Header.Command != (int)EN_VISION_COMMAND.FlyingReady)
            {
                throw new InvalidDataException($"Command {packet.Header.Command} is not a Flying Ready request.");
            }

            if (packet.Body.Length < 8)
            {
                throw new InvalidDataException($"Flying Ready request requires at least 8 bytes. Received={packet.Body.Length}.");
            }

            ValidateLineCapacity(maxHoleCountPerLine);
            var inspectionType = ReadInt32(packet.Body, 0);
            var count = ReadInt32(packet.Body, 4);
            if (count < 0 || count > maxHoleCountPerLine)
            {
                throw new InvalidDataException($"Flying Ready mark count must be between 0 and {maxHoleCountPerLine}. Received={count}.");
            }

            var requiredLength = 8 + (maxHoleCountPerLine * MarkSearchSize);
            if (packet.Body.Length != requiredLength)
            {
                throw new InvalidDataException($"Flying Ready request body length does not match its mark count. Expected={requiredLength}, Received={packet.Body.Length}.");
            }

            var searches = new ST_VISION_MARK_SEARCH[count];
            for (var index = 0; index < count; index++)
            {
                searches[index] = ReadMarkSearch(packet.Body, 8 + (index * MarkSearchSize));
            }

            return new ST_VISION_FLYING_READY(inspectionType, searches);
        }

        /// <summary>1002 response: one Int32, 0 = matching OK, 1 = no matching (NG).</summary>
        public static int DecodeFlyingMoveDoneResponse(ST_VISION_PACKET packet)
        {
            ArgumentNullException.ThrowIfNull(packet);
            if (packet.Header.Command != (int)EN_VISION_COMMAND.FlyingMoveDone)
                throw new InvalidDataException($"Command {packet.Header.Command} is not a Flying Move Done response.");
            if (packet.Body.Length != sizeof(int))
                throw new InvalidDataException($"Flying Move Done response requires 4 bytes for nHoleCountMatch. Received={packet.Body.Length}.");
            var countMatch = ReadInt32(packet.Body, 0);
            if (countMatch is not (0 or 1))
                throw new InvalidDataException($"Flying Move Done nHoleCountMatch must be 0 (OK) or 1 (NG). Received={countMatch}.");
            return countMatch;
        }

        public static ST_VISION_SINGLE_RESULT DecodeSingleResult(ST_VISION_PACKET packet)
        {
            ArgumentNullException.ThrowIfNull(packet);
            if (packet.Header.Command is not ((int)EN_VISION_COMMAND.MarkFindSingle) and not ((int)EN_VISION_COMMAND.MarkFindSettingSingle) and not ((int)EN_VISION_COMMAND.MarkFindManual))
            {
                throw new InvalidDataException($"Command {packet.Header.Command} is not a single mark result.");
            }

            if (packet.Body.Length < SingleResultBodySize)
            {
                throw new InvalidDataException($"Single mark response requires {SingleResultBodySize} bytes. Received={packet.Body.Length}.");
            }

            return new ST_VISION_SINGLE_RESULT(ReadInt32(packet.Body, 0), ReadPackedMarkFind(packet.Body, 4));
        }

        public static ST_VISION_LINE_RESULT DecodeLineResult(ST_VISION_PACKET packet, int maxHoleCountPerLine = DefaultMaxHoleCountPerLine)
        {
            ArgumentNullException.ThrowIfNull(packet);

            if (packet.Header.Command != (int)EN_VISION_COMMAND.MarkFindLine)
            {
                throw new InvalidDataException($"Command {packet.Header.Command} is not a line mark result.");
            }

            if (packet.Body.Length < 8)
            {
                throw new InvalidDataException("Line mark response requires at least 8 bytes.");
            }

            ValidateLineCapacity(maxHoleCountPerLine);
            var resolution = ReadInt32(packet.Body, 0);
            var count = ReadInt32(packet.Body, 4);
            if (count > maxHoleCountPerLine)
                throw new CVisionLineCountExceededException(count, maxHoleCountPerLine);
            if (count < 0)
            {
                throw new InvalidDataException($"Line mark response count must be between 0 and {maxHoleCountPerLine}. Received={count}.");
            }

            // Count describes valid entries; the physical frame always has the
            // configured slot count. Ignore the remaining padded result entries.
            var expectedLength = 8 + (maxHoleCountPerLine * MarkFindPackedSize);
            if (packet.Body.Length != expectedLength)
            {
                throw new InvalidDataException($"Line mark response body length does not match its result count. Expected={expectedLength}, Received={packet.Body.Length}.");
            }

            var marks = new ST_VISION_MARK_FIND[count];
            for (var index = 0; index < count; index++)
            {
                marks[index] = ReadPackedMarkFind(packet.Body, 8 + (index * MarkFindPackedSize));
            }

            return new ST_VISION_LINE_RESULT(resolution, count, marks);
        }

        public static byte[] EncodeMarkSearch(ST_VISION_MARK_SEARCH search)
        {
            var bytes = new byte[MarkSearchSize];
            WriteMarkSearch(bytes, 0, search);
            return bytes;
        }

        public static string FormatPacketLog(string direction, ST_VISION_PACKET packet, string source = "", int maxHoleCountPerLine = DefaultMaxHoleCountPerLine)
        {
            ArgumentNullException.ThrowIfNull(packet);
            var builder = new StringBuilder();
            builder.AppendLine(PacketLogSeparator);
            builder.Append('[').Append(direction.Trim().ToUpperInvariant()).Append("] ");
            if (!string.IsNullOrWhiteSpace(source))
            {
                builder.Append(source.Trim()).Append(' ');
            }

            builder.AppendLine(GetCommandName(packet.Header.Command));
            builder.Append("[HEAD] Code : ").Append(packet.Header.Command).Append(" / UniqueID : ").Append(packet.Header.UniqueId).Append(" / Length : ").Append(packet.Header.DataLength).Append(" / Error : ").Append(packet.Header.ErrorCode);
            AppendPacketLogBody(builder, packet, maxHoleCountPerLine);
            return builder.ToString();
        }

        public static string GetCommandName(int command)
        {
            return command switch
            {
                (int)EN_VISION_COMMAND.MarkFindSingle => "Mark Find Single",
                (int)EN_VISION_COMMAND.FlyingReady => "Flying Ready",
                (int)EN_VISION_COMMAND.FlyingMoveDone => "Flying Move Done",
                (int)EN_VISION_COMMAND.MarkFindLine => "Mark Find Line",
                (int)EN_VISION_COMMAND.MarkFindSettingSingle => "Mark Find Setting Single",
                (int)EN_VISION_COMMAND.MarkFindManual => "Mark Find Manual",
                (int)EN_VISION_COMMAND.KeepAliveRequest => "Keep-Alive Request",
                (int)EN_VISION_COMMAND.KeepAlive => "Keep-Alive",
                _ => $"Unknown Command ({command})"};
        }

        private static void AppendPacketLogBody(StringBuilder builder, ST_VISION_PACKET packet, int maxHoleCountPerLine)
        {
            builder.AppendLine();
            if (packet.Body.Length == 0)
            {
                builder.Append("[BODY] Empty");
                return;
            }

            try
            {
                switch (packet.Header.Command)
                {
                    case (int)EN_VISION_COMMAND.MarkFindSingle:
                        AppendMarkFindSingleLogBody(builder, packet);
                        return;
                    case (int)EN_VISION_COMMAND.FlyingReady:
                        AppendFlyingReadyLogBody(builder, packet, maxHoleCountPerLine);
                        return;
                    case (int)EN_VISION_COMMAND.FlyingMoveDone:
                        var countMatch = DecodeFlyingMoveDoneResponse(packet);
                        builder.Append("[BODY] Hole Count Match : ").Append(countMatch)
                            .Append(countMatch == 0 ? " / Matching OK" : " / No Matching (NG)");
                        return;
                    case (int)EN_VISION_COMMAND.MarkFindLine:
                        AppendLineResultLogBody(builder, packet, maxHoleCountPerLine);
                        return;
                    case (int)EN_VISION_COMMAND.MarkFindSettingSingle:
                    case (int)EN_VISION_COMMAND.MarkFindManual:
                        AppendSingleResultLogBody(builder, packet);
                        return;
                    default:
                        builder.Append("[BODY] Binary Data Length : ").Append(packet.Body.Length);
                        return;
                }
            }
            catch (Exception exception)when (exception is IOException or ArgumentException)
            {
                builder.Append("[BODY] Decode Error : ").Append(exception.Message);
            }
        }

        private static void AppendMarkFindSingleLogBody(StringBuilder builder, ST_VISION_PACKET packet)
        {
            if (packet.Body.Length == MarkSearchSize)
            {
                var search = DecodeMarkSearchRequest(packet);
                builder.Append("[BODY] Cell No : ").Append(search.Cell).AppendLine();
                builder.Append(PacketLogBodyIndent).Append("DOE / Beam : ").Append(search.Doe).AppendLine();
                builder.Append(PacketLogBodyIndent).Append("Line No : ").Append(search.Line).AppendLine();
                builder.Append(PacketLogBodyIndent).Append("Index X : ").Append(search.IndexX).AppendLine();
                builder.Append(PacketLogBodyIndent).Append("Index Y : ").Append(search.IndexY).AppendLine();
                builder.Append(PacketLogBodyIndent).Append("Head No : ").Append(search.Head);
                return;
            }

            AppendSingleResultLogBody(builder, packet);
        }

        private static void AppendFlyingReadyLogBody(StringBuilder builder, ST_VISION_PACKET packet, int maxHoleCountPerLine)
        {
            var ready = DecodeFlyingReadyRequest(packet, maxHoleCountPerLine);
            builder.Append("[BODY] Inspection Type : ").Append(ready.InspectionType).AppendLine();
            builder.Append(PacketLogBodyIndent).Append("Mark Count : ").Append(ready.Searches.Count);
            for (var index = 0; index < ready.Searches.Count; index++)
            {
                var search = ready.Searches[index];
                builder.AppendLine();
                builder.Append(PacketLogBodyIndent).Append("Mark[").Append(index).Append("] : Cell=").Append(search.Cell).Append(" / DOE=").Append(search.Doe).Append(" / Line=").Append(search.Line).Append(" / IndexX=").Append(search.IndexX).Append(" / IndexY=").Append(search.IndexY).Append(" / Head=").Append(search.Head);
            }
        }

        private static void AppendSingleResultLogBody(StringBuilder builder, ST_VISION_PACKET packet)
        {
            var result = DecodeSingleResult(packet);
            builder.Append("[BODY] Resolution : ").Append(result.Resolution).AppendLine();
            AppendMarkResultLog(builder, result.Mark, null);
        }

        private static void AppendLineResultLogBody(StringBuilder builder, ST_VISION_PACKET packet, int maxHoleCountPerLine)
        {
            var result = DecodeLineResult(packet, maxHoleCountPerLine);
            builder.Append("[BODY] Resolution : ").Append(result.Resolution).AppendLine();
            builder.Append(PacketLogBodyIndent).Append("Find Hole Count : ").Append(result.FindHoleCount);
            for (var index = 0; index < result.Marks.Count; index++)
            {
                builder.AppendLine();
                AppendMarkResultLog(builder, result.Marks[index], index);
            }
        }

        private static void AppendMarkResultLog(StringBuilder builder, ST_VISION_MARK_FIND mark, int? index)
        {
            builder.Append(PacketLogBodyIndent);
            if (index.HasValue)
            {
                builder.Append("Mark[").Append(index.Value).Append("] ");
            }
            else
            {
                builder.Append("Mark ");
            }

            builder.Append("Result : ").Append(mark.Result).Append(" / Target X : ").Append(FormatLogNumber(mark.TargetMarkX)).Append(" / Target Y : ").Append(FormatLogNumber(mark.TargetMarkY)).Append(" / Hole X : ").Append(FormatLogNumber(mark.HoleX)).Append(" / Hole Y : ").Append(FormatLogNumber(mark.HoleY));
        }

        private static string FormatLogNumber(double value)
        {
            return value.ToString("0.################", CultureInfo.InvariantCulture);
        }

        private static void WriteMarkSearch(Span<byte> destination, int offset, ST_VISION_MARK_SEARCH search)
        {
            WriteInt32(destination, offset, search.Cell);
            WriteInt32(destination, offset + 4, search.Doe);
            WriteInt32(destination, offset + 8, search.Line);
            WriteInt32(destination, offset + 12, search.IndexX);
            WriteInt32(destination, offset + 16, search.IndexY);
            WriteInt32(destination, offset + 20, search.Head);
        }

        private static void WriteMarkFind(Span<byte> destination, int offset, ST_VISION_MARK_FIND mark)
        {
            WriteInt32(destination, offset, mark.Result);
            WriteDouble(destination, offset + 4, mark.TargetMarkX);
            WriteDouble(destination, offset + 12, mark.TargetMarkY);
            WriteDouble(destination, offset + 20, mark.HoleX);
            WriteDouble(destination, offset + 28, mark.HoleY);
        }

        private static ST_VISION_MARK_FIND ReadPackedMarkFind(ReadOnlySpan<byte> source, int offset) => new(ReadInt32(source, offset), ReadDouble(source, offset + 4), ReadDouble(source, offset + 12), ReadDouble(source, offset + 20), ReadDouble(source, offset + 28));
        private static ST_VISION_MARK_SEARCH ReadMarkSearch(ReadOnlySpan<byte> source, int offset) => new(ReadInt32(source, offset), ReadInt32(source, offset + 4), ReadInt32(source, offset + 8), ReadInt32(source, offset + 12), ReadInt32(source, offset + 16), ReadInt32(source, offset + 20));
        private static void ValidateBodyLength(int length)
        {
            if (length is < 0 or > MaximumBodySize)
            {
                throw new InvalidDataException($"Vision body length must be between 0 and {MaximumBodySize}. Received={length}.");
            }
        }

        private static int ReadInt32(ReadOnlySpan<byte> source, int offset) => BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, sizeof(int)));
        private static double ReadDouble(ReadOnlySpan<byte> source, int offset)
        {
            var bits = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(offset, sizeof(long)));
            return BitConverter.Int64BitsToDouble(bits);
        }

        private static void WriteInt32(Span<byte> destination, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), value);
        private static void WriteDouble(Span<byte> destination, int offset, double value) => BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), BitConverter.DoubleToInt64Bits(value));
    }
}
