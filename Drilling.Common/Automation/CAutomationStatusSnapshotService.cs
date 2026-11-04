namespace Drilling.Common.Automation
{
    using System.Diagnostics;

    /// <summary>One acquisition owner per Automation manager. Consumers only register/read cached values.</summary>
    public sealed partial class CAutomationStatusSnapshotService : IDisposable
    {
        private readonly CAutomationManager _automation;
        private readonly object _sync = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _stop = new();
        private readonly bool _autoStart;
        private Task? _worker;
        private bool _disposed;
        private const int LeaseMs = 10000;
        private const int StaleMs = 5000;
        internal CAutomationStatusSnapshotService(CAutomationManager automation, bool autoStart = true)
        {
            _automation = automation;
            _autoStart = autoStart;
        }

        public ST_AUTOMATION_STATUS_SNAPSHOT<ST_AUTOMATION_AXIS_STATUS> GetAxis(string axis, int controller, int aio, bool extended) => Get<ST_AUTOMATION_AXIS_STATUS>($"AXIS|{controller}|{axis.Trim().ToUpperInvariant()}|{aio}|{extended}", controller, token => _automation.ReadAxisStatusByNameNoLog(axis, controller, aio, extended, token));
        /// <summary>Invalidate after a command acknowledgement so an earlier in-flight read cannot complete that command.</summary>
        public void InvalidateAxis(string axis, int controller, int aio = 0, bool extended = false)
        {
            GetAxis(axis, controller, aio, extended);
            var key = $"AXIS|{controller}|{axis.Trim().ToUpperInvariant()}|{aio}|{extended}";
            lock (_sync)
            {
                if (_disposed || !_entries.TryGetValue(key, out var entry))
                    return;
                entry.Generation++;
                entry.Version++;
                entry.Value = null;
                entry.State = "LOADING";
                entry.Detail = "Waiting for an Automation snapshot collected after the command.";
                entry.UpdatedAt = default;
                entry.CollectedAt = 0;
                entry.CollectedTimestamp = 0;
            }
        }

        public ST_AUTOMATION_STATUS_SNAPSHOT<string> GetTask(int task, int controller) => Get<string>($"TASK|{controller}|{task}", controller, token => _automation.ReadTaskStatus(task, controller, token).GetAwaiter().GetResult());
        public ST_AUTOMATION_STATUS_SNAPSHOT<string> GetProcessorUsage(int controller) => Get<string>($"CPU|{controller}", controller, token => _automation.ReadProcessorUsage(controller, token).GetAwaiter().GetResult());
        internal ST_AUTOMATION_STATUS_SNAPSHOT<T> Get<T>(string key, int controller, Func<CancellationToken, object> read)
            where T : class
        {
            lock (_sync)
            {
                if (_disposed)
                    return new(null, "STOPPED", "Automation status acquisition stopped.", default);
                var now = Environment.TickCount64;
                if (!_entries.TryGetValue(key, out var entry))
                    _entries.Add(key, entry = new Entry(controller, read));
                entry.LastRequested = now;
                if (_autoStart)
                    _worker ??= Task.Run(Collect);
                if (entry.UpdatedAt == default)
                    return new(null, "LOADING", "Waiting for shared Automation snapshot.", default)
                    {
                        Version = entry.Version
                    };
                if (now - entry.CollectedAt > StaleMs)
                    return new(null, "STALE", "Automation snapshot is older than 5 seconds.", entry.UpdatedAt)
                    {
                        Version = entry.Version,
                        CollectedTimestamp = entry.CollectedTimestamp
                    };
                return new(entry.Value as T, entry.State, entry.Detail, entry.UpdatedAt)
                {
                    Version = entry.Version,
                    CollectedTimestamp = entry.CollectedTimestamp
                };
            }
        }

        private async Task Collect()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var elapsed = Stopwatch.StartNew();
                    await CollectOnce(_stop.Token).ConfigureAwait(false);
                    await Task.Delay(Math.Max(1, 1000 - (int)Math.Min(1000, elapsed.ElapsedMilliseconds)), _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)when (_stop.IsCancellationRequested)
            {
            }
        }

        internal Task CollectOnce(CancellationToken token)
        {
            Entry[] entries;
            ConfiguredHomeSpeedRead[] configuredReads;
            lock (_sync)
            {
                var now = Environment.TickCount64;
                foreach (var key in _entries.Where(pair => now - pair.Value.LastRequested > LeaseMs).Select(pair => pair.Key).ToArray())
                    _entries.Remove(key);
                entries = _entries.Values.ToArray();
                configuredReads = PrepareConfiguredHomeSpeedReads(now);
            }

            // Different controllers are independent; each controller has one sequential read queue.
            return Task.WhenAll(entries.Select(entry => entry.Controller).Concat(configuredReads.Select(read => read.Controller)).Distinct().Select(controller => Task.Run(() =>
            {
                foreach (var entry in entries.Where(entry => entry.Controller == controller))
                {
                    token.ThrowIfCancellationRequested();
                    long generation;
                    long collectedTimestamp;
                    lock (_sync)
                    {
                        if (_disposed)
                            return;
                        generation = entry.Generation;
                        collectedTimestamp = Stopwatch.GetTimestamp();
                    }

                    object? value = null;
                    var state = "OK";
                    var detail = "Shared Automation snapshot";
                    try
                    {
                        if (!_automation.IsSimul(entry.Controller) && !_automation.IsConnect(entry.Controller))
                            (state, detail) = ("OFFLINE", "Automation not connected.");
                        else
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            timeout.CancelAfter(2000);
                            value = entry.Read(timeout.Token);
                            timeout.Token.ThrowIfCancellationRequested();
                        }
                    }
                    catch (OperationCanceledException)when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        (state, detail) = ("ERROR", exception.Message);
                    }

                    lock (_sync)
                    {
                        if (_disposed)
                            return;
                        if (entry.Generation != generation)
                            continue;
                        entry.Value = value;
                        entry.State = state;
                        entry.Detail = detail;
                        entry.UpdatedAt = DateTimeOffset.Now;
                        entry.CollectedAt = Environment.TickCount64;
                        entry.CollectedTimestamp = collectedTimestamp;
                        entry.Version++;
                    }
                }

                foreach (var read in configuredReads.Where(read => read.Controller == controller))
                    CollectConfiguredHomeSpeeds(read, token);
            }, token)));
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _entries.Clear();
                _configuredHomeSpeedEntries.Clear();
            }

            _stop.Cancel();
            // Do not dispose the token source while an SDK read may still be unwinding.
            if (_worker is { } worker)
                _ = worker.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
            else
                _stop.Dispose();
        }

        private sealed class Entry(int controller, Func<CancellationToken, object> read)
        {
            public int Controller { get; } = controller;
            public Func<CancellationToken, object> Read { get; } = read;

            public long LastRequested;
            public long CollectedAt;
            public object? Value;
            public string State = "LOADING";
            public string Detail = "";
            public DateTimeOffset UpdatedAt;
            public long Generation;
            public long Version;
            public long CollectedTimestamp;
        }

        private const int ConfiguredHomeSpeedRefreshMs = 10000;
        private const int ConfiguredHomeSpeedStaleMs = 30000;
        private readonly Dictionary<int, ConfiguredHomeSpeedEntry> _configuredHomeSpeedEntries = [];
        /// <summary>Registers requested axes and immediately returns the centrally acquired saved settings.</summary>
        public ST_AUTOMATION_STATUS_SNAPSHOT<IReadOnlyDictionary<string, double>> GetConfiguredHomeSpeeds(IReadOnlyList<string> axes, int controller)
        {
            ArgumentNullException.ThrowIfNull(axes);
            ArgumentOutOfRangeException.ThrowIfNegative(controller);
            var normalizedAxes = axes.Select(CAutomationManager.ValidateConfiguredHomeAxis).Select(axis => axis.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            lock (_sync)
            {
                if (_disposed)
                    return new(null, "STOPPED", "Automation status acquisition stopped.", default);
                if (normalizedAxes.Length == 0)
                    return new(ReadOnlyValues(new Dictionary<string, double>()), "OK", "No configured axes requested.", DateTimeOffset.Now);
                if (!_configuredHomeSpeedEntries.TryGetValue(controller, out var entry))
                    _configuredHomeSpeedEntries.Add(controller, entry = new ConfiguredHomeSpeedEntry(controller));
                var now = Environment.TickCount64;
                var addedAxis = false;
                foreach (var axis in normalizedAxes)
                {
                    addedAxis |= !entry.RequestedAxes.ContainsKey(axis);
                    entry.RequestedAxes[axis] = now;
                }

                if (addedAxis)
                    InvalidateConfiguredHomeSpeedEntry(entry);
                if (_autoStart)
                    _worker ??= Task.Run(Collect);
                if (entry.UpdatedAt == default)
                    return new(null, "LOADING", "Waiting for shared saved HomeSpeed snapshot.", default);
                if (now - entry.CollectedAt > ConfiguredHomeSpeedStaleMs)
                    return new(null, "STALE", "Saved HomeSpeed snapshot is older than 30 seconds.", entry.UpdatedAt);
                return new(entry.Value, entry.State, entry.Detail, entry.UpdatedAt);
            }
        }

        /// <summary>Requests a new central read; never performs controller I/O on the caller.</summary>
        public void RequestConfiguredHomeSpeedsRefresh(int controller)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(controller);
            lock (_sync)
            {
                if (!_disposed && _configuredHomeSpeedEntries.TryGetValue(controller, out var entry))
                    InvalidateConfiguredHomeSpeedEntry(entry);
            }
        }

        // Caller holds _sync. Each axis has its own demand lease so one consumer cannot retain unused axes.
        private ConfiguredHomeSpeedRead[] PrepareConfiguredHomeSpeedReads(long now)
        {
            var reads = new List<ConfiguredHomeSpeedRead>();
            foreach (var pair in _configuredHomeSpeedEntries.ToArray())
            {
                var entry = pair.Value;
                var expired = entry.RequestedAxes.Where(axis => now - axis.Value > LeaseMs).Select(axis => axis.Key).ToArray();
                foreach (var axis in expired)
                    entry.RequestedAxes.Remove(axis);
                if (entry.RequestedAxes.Count == 0)
                {
                    _configuredHomeSpeedEntries.Remove(pair.Key);
                    continue;
                }

                if (expired.Length > 0)
                    InvalidateConfiguredHomeSpeedEntry(entry);
                if (entry.InFlight || entry.UpdatedAt != default && now - entry.LastAttemptAt < ConfiguredHomeSpeedRefreshMs)
                    continue;
                entry.InFlight = true;
                entry.LastAttemptAt = now;
                reads.Add(new(entry.Controller, entry, entry.RequestedAxes.Keys.ToArray(), entry.Version));
            }

            return reads.ToArray();
        }

        private void CollectConfiguredHomeSpeeds(ConfiguredHomeSpeedRead read, CancellationToken token)
        {
            IReadOnlyDictionary<string, double>? values = null;
            var state = "OK";
            var detail = "Shared saved HomeSpeed snapshot";
            try
            {
                token.ThrowIfCancellationRequested();
                if (!_automation.IsSimul(read.Controller) && !_automation.IsConnect(read.Controller))
                    (state, detail) = ("OFFLINE", "Automation not connected.");
                else
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(2000);
                    var result = _automation.ReadConfiguredHomeSpeeds(read.Axes, read.Controller, timeout.Token).GetAwaiter().GetResult();
                    timeout.Token.ThrowIfCancellationRequested();
                    if (read.Axes.Any(axis => !result.TryGetValue(axis, out var value) || !double.IsFinite(value) || value < 0))
                        throw new InvalidOperationException("Saved HomeSpeed snapshot did not contain valid values for all requested axes.");
                    values = ReadOnlyValues(result);
                }
            }
            catch (OperationCanceledException)when (token.IsCancellationRequested)
            {
                lock (_sync)
                    read.Entry.InFlight = false;
                throw;
            }
            catch (Exception exception)
            {
                (state, detail) = ("ERROR", exception.Message);
            }

            lock (_sync)
            {
                read.Entry.InFlight = false;
                // SET/REFRESH or a new axis may invalidate an already-running read. Never republish it.
                if (_disposed || !_configuredHomeSpeedEntries.TryGetValue(read.Controller, out var current) || !ReferenceEquals(current, read.Entry) || current.Version != read.Version)
                    return;
                current.Value = values;
                current.State = state;
                current.Detail = detail;
                current.UpdatedAt = DateTimeOffset.Now;
                current.CollectedAt = Environment.TickCount64;
            }
        }

        private static IReadOnlyDictionary<string, double> ReadOnlyValues(IEnumerable<KeyValuePair<string, double>> values) => new CConfiguredHomeSpeedValues(values, (values as CConfiguredHomeSpeedValues)?.Units);
        private static void InvalidateConfiguredHomeSpeedEntry(ConfiguredHomeSpeedEntry entry)
        {
            entry.Version++;
            entry.Value = null;
            entry.State = "LOADING";
            entry.Detail = "Waiting for shared saved HomeSpeed snapshot.";
            entry.UpdatedAt = default;
            entry.CollectedAt = 0;
            entry.LastAttemptAt = 0;
        }

        private sealed class ConfiguredHomeSpeedEntry(int controller)
        {
            public int Controller { get; } = controller;
            public Dictionary<string, long> RequestedAxes { get; } = new(StringComparer.OrdinalIgnoreCase);

            public IReadOnlyDictionary<string, double>? Value;
            public string State = "LOADING";
            public string Detail = "";
            public DateTimeOffset UpdatedAt;
            public long CollectedAt;
            public long LastAttemptAt;
            public long Version;
            public bool InFlight;
        }

        private sealed record ConfiguredHomeSpeedRead(int Controller, ConfiguredHomeSpeedEntry Entry, string[] Axes, long Version);
        // Encoder axes need only AuxiliaryFeedback; do not query galvo-only status on them.
        public ST_AUTOMATION_STATUS_SNAPSHOT<ST_AUXILIARY_FEEDBACK> GetAuxiliaryFeedback(string axis, int controller) => Get<ST_AUXILIARY_FEEDBACK>(AuxiliaryKey(axis, controller), controller, token => _automation.ReadAuxiliaryFeedback(axis, controller, token).GetAwaiter().GetResult());
        internal void InvalidateAuxiliaryFeedback(string axis, int controller)
        {
            GetAuxiliaryFeedback(axis, controller);
            InvalidateEntry(AuxiliaryKey(axis, controller));
        }

        public void InvalidateTask(int task, int controller)
        {
            GetTask(task, controller);
            InvalidateEntry($"TASK|{controller}|{task}");
        }

        private void InvalidateEntry(string key)
        {
            lock (_sync)
            {
                if (!_entries.TryGetValue(key, out var entry))
                    return;
                entry.Generation++;
                entry.Version++;
                entry.Value = null;
                entry.State = "LOADING";
                entry.UpdatedAt = default;
                entry.CollectedAt = 0;
                entry.CollectedTimestamp = 0;
            }
        }

        private static string AuxiliaryKey(string axis, int controller) => $"AUX|{controller}|{axis.Trim().ToUpperInvariant()}";
        public ST_AUTOMATION_STATUS_SNAPSHOT<ST_MOF_READINESS> GetMofReadiness(int task, int controller) => Get<ST_MOF_READINESS>($"MOF_READY|{controller}|{task}", controller, token => _automation.ReadMofReadiness(task, controller, token).GetAwaiter().GetResult());
        public void InvalidateMofReadiness(int task, int controller)
        {
            GetMofReadiness(task, controller);
            InvalidateEntry($"MOF_READY|{controller}|{task}");
        }
    }

    public sealed record ST_AUTOMATION_STATUS_SNAPSHOT<T>(T? Value, string State, string Detail, DateTimeOffset UpdatedAt)
        where T : class
    {
        public long Version { get; init; }
        public long CollectedTimestamp { get; init; }

        /// <summary>Age is measured from the device read start, not cache publication.</summary>
        public bool IsFresh(TimeSpan maximumAge)
        {
            if (State != "OK" || Value is null || CollectedTimestamp <= 0)
                return false;
            var now = Stopwatch.GetTimestamp();
            return CollectedTimestamp <= now && Stopwatch.GetElapsedTime(CollectedTimestamp, now) <= maximumAge;
        }
    }

    public sealed record ST_SCANNER_AXIS_LIVE_STATUS(
        int HeadNo, 
        int AutomationNo, 
        int ControllerNo, 
        int TaskNo, 
        string Head, 
        string Automation, 
        string Axis, 
        string AxisNo, 
        string AxisPosition, 
        string AxisCurrentFeedback, 
        string AxisEnableStatus, 
        string AxisHomeStatus, 
        string AxisFault, 
        string GalvoTemperature, 
        string GalvoLaserOutputRaw, 
        string PositionFbkAux, 
        string TaskStatus, 
        string ProcessorUsage, 
        string Error, 
        string Detail
    );
    public static class CScannerStatusLiveSnapshotStore
    {
        private static readonly object SnapshotLock = new();
        private static Guid _ownerId = Guid.Empty;
        private static IReadOnlyList<ST_SCANNER_AXIS_LIVE_STATUS> _snapshot = [];
        public static Guid Begin()
        {
            var ownerId = Guid.NewGuid();
            lock (SnapshotLock)
            {
                _ownerId = ownerId;
                _snapshot = [];
            }

            return ownerId;
        }

        public static void Update(Guid ownerId, IReadOnlyList<ST_SCANNER_AXIS_LIVE_STATUS> snapshot)
        {
            lock (SnapshotLock)
            {
                if (_ownerId != ownerId)
                {
                    return;
                }

                _snapshot = snapshot;
            }
        }

        public static void End(Guid ownerId)
        {
            lock (SnapshotLock)
            {
                if (_ownerId != ownerId)
                {
                    return;
                }

                _ownerId = Guid.Empty;
                _snapshot = [];
            }
        }

        public static bool TryGetHeadSnapshot(int headNo, out IReadOnlyList<ST_SCANNER_AXIS_LIVE_STATUS> snapshot)
        {
            lock (SnapshotLock)
            {
                if (_ownerId == Guid.Empty)
                {
                    snapshot = [];
                    return false;
                }

                var items = new List<ST_SCANNER_AXIS_LIVE_STATUS>();
                foreach (var item in _snapshot)
                {
                    if (item.HeadNo == headNo)
                    {
                        items.Add(item);
                    }
                }

                snapshot = items;
                return true;
            }
        }

        public static bool TryGetTaskStatus(int automationNo, int taskNo, out string taskStatus)
        {
            lock (SnapshotLock)
            {
                taskStatus = "WAIT";
                if (_ownerId == Guid.Empty)
                {
                    return false;
                }

                foreach (var item in _snapshot)
                {
                    if (item.AutomationNo == automationNo && item.TaskNo == taskNo && !string.IsNullOrWhiteSpace(item.TaskStatus))
                    {
                        taskStatus = item.TaskStatus;
                        return true;
                    }
                }

                return true;
            }
        }

        public static bool TryGetProcessorUsage(int automationNo, out string processorUsage)
        {
            lock (SnapshotLock)
            {
                processorUsage = "WAIT";
                if (_ownerId == Guid.Empty)
                {
                    return false;
                }

                foreach (var item in _snapshot)
                {
                    if (item.AutomationNo == automationNo && !string.IsNullOrWhiteSpace(item.ProcessorUsage))
                    {
                        processorUsage = item.ProcessorUsage;
                        return true;
                    }
                }

                return true;
            }
        }
    }
}
