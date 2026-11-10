namespace Drilling.Common.Alarm
{
    using Drilling.Common.Alarm;
    using Drilling.Common.Interface;
    using Drilling.Common.InterLock;
    using Drilling.Common.Managers;
    using Drilling.Common.Motion;
    using Drilling.Common.Station;
    using Drilling.Common.Automation;
    using System.Globalization;

    public sealed partial class CAlarmManager
    {
#region 공유 상태·초기값
        private static readonly IReadOnlyDictionary<string, ST_ALARM_RULE> InterLockAlarmRules = new Dictionary<string, ST_ALARM_RULE>(StringComparer.OrdinalIgnoreCase)
        {
            ["EMS"] = new(21001, EN_ALARM_LEVEL.Critical, "INTERLOCK", "COMMON", "Release EMS and check emergency circuit"),
            ["Door"] = new(21002, EN_ALARM_LEVEL.Warning, "INTERLOCK", "COMMON", "Close door and check door lock sensor"),
            ["Leak Sensor"] = new(26001, EN_ALARM_LEVEL.Critical, "INTERLOCK", "COMMON", "Check leak sensor and fluid line"),
            ["Smoke Temp"] = new(26002, EN_ALARM_LEVEL.Critical, "INTERLOCK", "COMMON", "Check smoke and temperature sensor"),
            ["PM Lock"] = new(27001, EN_ALARM_LEVEL.Critical, "INTERLOCK", "COMMON", "Release PM lock before operation")
        };
        private readonly object _definitionLock = new();
        private bool _definitionsInitialized;
        private bool _definitionInitializationRequested;
        private Task? _definitionRetryTask;
        private CAlarmListFileBase? _definitionFile;
        private int _definitionRetriesRemaining;
        private long _nextDefinitionRetryAt;
        private ST_ALARM_DEFINITION_SNAPSHOT _definitions = new([], "알람 목록이 로드되지 않았습니다.");
        private readonly object _currentLock = new();
        private readonly Dictionary<int, ST_ALARM_DATA> _curAlarm = [];
        private readonly IAlarmHistoryStore? _history;
        private readonly Func<DateTimeOffset> _now;
        private ST_CURRENT_ALARM_SNAPSHOT _current = new(0, 0, [], "");
        private string _notificationError = "";
        /// <summary>현재 알람 스냅샷 변경을 상태를 변경한 스레드에서 동기적으로 통지합니다.</summary>
        /// <remarks>
        /// 등록/RESET 호출자, 감시 작업자 또는 이력 저장 작업자에서 호출될 수 있습니다.
        /// UI 구독자는 WPF Dispatcher.BeginInvoke, WinForms BeginInvoke 또는
        /// SynchronizationContext.Post를 사용해 자신의 UI 스레드로 전달해야 합니다.
        /// reportError 콜백에도 같은 UI 전달 규칙이 적용되며, 관리자는 UI 컨텍스트를 자동 전환하지 않습니다.
        /// 구독자는 오래 걸리는 작업을 직접 수행하지 않아야 합니다.
        /// </remarks>
        public event Action? CurrentChanged;
        private const int HistoryQueueCapacity = 1024;
        private readonly object _historyQueueLock = new();
        private readonly Queue<ST_ALARM_DATA> _historyQueue = [];
        private Task? _historyTask;
        private string _historyError = "";
        private long _historyWriteStartedAt;
        private readonly object _monitoringLock = new();
        private CancellationTokenSource? _monitoringStop;
        private Task? _monitoringTask;
        private string _monitoringError = "";
#endregion
#region 생성·초기화
        public CAlarmManager(IAlarmHistoryStore? history = null, Func<DateTimeOffset>? now = null)
        {
            _history = history;
            _now = now ?? (() => DateTimeOffset.Now);
        }

#endregion
#region 기본 처리
        public IReadOnlyList<ST_ALARM_DATA> Build(ST_DEVICE_STATUS status, ST_INTERLOCK_SUMMARY interLock)
        {
            var candidates = new List<ST_ALARM_CANDIDATE>();
            AddInterLockAlarms(candidates, interLock);
            bool IsAlarmOn(global::Drilling.Common.Motion.ST_MOTOR_AXIS_STATUS axis)
            {
                return axis.AlarmOn;
            }

            foreach (var axis in status.Motors.Where(IsAlarmOn))
            {
                candidates.Add(new ST_ALARM_CANDIDATE(24001, EN_ALARM_LEVEL.Warning, $"{axis.AxisId} axis alarm", "Reset axis alarm and confirm servo state", "MOTION", GetStationNameFromAxis(axis.AxisId)));
            }

            if (status.Bet.AlarmOn)
            {
                candidates.Add(new ST_ALARM_CANDIDATE(25001, EN_ALARM_LEVEL.Warning, "BET alarm is active", "Check beam expander controller and reset alarm", "BET", "COMMON"));
            }

            foreach (var alarm in candidates)
            {
                AddCurrent(new(alarm.Code, alarm.Severity, alarm.Message, alarm.RecoveryAction, _now(), alarm.Device, alarm.StationName) { Group = alarm.Device });
            }

            return CurAlarm;
        }

        private static void AddInterLockAlarms(ICollection<ST_ALARM_CANDIDATE> alarms, ST_INTERLOCK_SUMMARY interLock)
        {
            bool ShouldIncludeItem(global::Drilling.Common.InterLock.ST_INTERLOCK_ITEM item)
            {
                return item.Level != EN_INTERLOCK_LEVEL.Ok;
            }

            foreach (var item in interLock.Items.Where(ShouldIncludeItem))
            {
                if (!InterLockAlarmRules.TryGetValue(item.Signal, out var rule))
                {
                    continue;
                }

                alarms.Add(new ST_ALARM_CANDIDATE(rule.Code, rule.Severity, item.Detail, rule.RecoveryAction, rule.Device, rule.StationName));
            }
        }

        private static string GetStationNameFromAxis(string axisId)
        {
            var normalized = axisId.Trim().ToUpperInvariant();
            if (normalized.StartsWith("SCANNER_", StringComparison.OrdinalIgnoreCase))
            {
                var parts = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 ? $"SCANNER_{parts[1]}" : "DRILLING";
            }

            return "DRILLING";
        }

        private sealed record ST_ALARM_RULE(int Code, EN_ALARM_LEVEL Severity, string Device, string StationName, string RecoveryAction);
        private sealed record ST_ALARM_CANDIDATE(int Code, EN_ALARM_LEVEL Severity, string Message, string RecoveryAction, string Device, string StationName);
        public ST_ALARM_DEFINITION_SNAPSHOT GetDefinitionsSnapshot() => Volatile.Read(ref _definitions);
        public ST_ALARM_DEFINITION_SNAPSHOT InitializeDefinitions(CAlarmListFileBase file)
        {
            ArgumentNullException.ThrowIfNull(file);
            Volatile.Write(ref _definitionInitializationRequested, true);
            lock (_definitionLock)
            {
                if (_definitionsInitialized)
                    return _definitions;
                _definitionFile = file;
                _definitionRetriesRemaining = 2;
                LoadDefinitionsLocked(file);
                return _definitions;
            }
        }

        private void LoadDefinitionsLocked(CAlarmListFileBase file)
        {
            try
            {
                var codes = new HashSet<int>();
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var rows = file.Load().Select(row =>
                {
                    var level = row.Level?.Trim().ToUpperInvariant();
                    var id = row.Id?.Trim();
                    if (level is not ("HEAVY" or "LIGHT"))
                        throw new InvalidDataException($"알람 {row.Code}: LEVEL은 HEAVY 또는 LIGHT여야 합니다.");
                    if (string.IsNullOrEmpty(id) || !codes.Add(row.Code) || !ids.Add(id))
                        throw new InvalidDataException($"알람 {row.Code}: CODE/ID가 중복되었거나 ID가 비어 있습니다.");
                    return row with
                    {
                        Id = id,
                        Level = level
                    };
                }).ToArray();
                if (rows.Length == 0)
                    throw new InvalidDataException("알람 정의 목록이 비어 있습니다.");
                Volatile.Write(ref _definitions, new(Array.AsReadOnly(rows), ""));
                // Freeze only a successfully validated catalog. UI reads never reload this file.
                _definitionsInitialized = true;
                _definitionFile = null;
                _definitionRetriesRemaining = 0;
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _definitions, new(_definitions.Rows, exception.Message));
                _nextDefinitionRetryAt = Environment.TickCount64 + 1000;
            }
        }

        private string RetryDefinitionInitialization()
        {
            var snapshot = GetDefinitionsSnapshot();
            if (snapshot.Error == "" && snapshot.Rows.Count > 0)
                return "";
            // A slow file/network load must never hold the monitor's shutdown path.
            if (!global::System.Threading.Monitor.TryEnter(_definitionLock))
                return snapshot.Error;
            try
            {
                if (_definitionsInitialized)
                    return "";
                var file = _definitionFile;
                if (file is null)
                    return _definitions.Error;
                // Recover transient startup failures without turning the catalog into a polled file.
                // After two background retries, an explicit InitializeDefinitions call can retry again.
                if (_definitionRetriesRemaining > 0 && _definitionRetryTask?.IsCompleted != false && Environment.TickCount64 >= _nextDefinitionRetryAt)
                {
                    _definitionRetriesRemaining--;
                    _definitionRetryTask = Task.Run(() =>
                    {
                        lock (_definitionLock)
                        {
                            if (!_definitionsInitialized)
                                LoadDefinitionsLocked(file);
                        }
                    });
                }

                return _definitions.Error;
            }
            finally
            {
                global::System.Threading.Monitor.Exit(_definitionLock);
            }
        }

        internal void EnsureDefinitionsRequested()
        {
            if (!Volatile.Read(ref _definitionInitializationRequested))
                throw new InvalidOperationException("알람 정의 초기화가 필요합니다. InitializeDefinitions 호출 후 감시를 시작해 주세요.");
        }

        public string NotificationError => Volatile.Read(ref _notificationError);
        public IReadOnlyList<ST_ALARM_DATA> CurAlarm => GetCurrentSnapshot().Rows;

        public ST_CURRENT_ALARM_SNAPSHOT GetCurrentSnapshot() => Volatile.Read(ref _current);
        public void SetSequenceFailure(CSequenceFailureException failure)
        {
            // Definitions are already loaded at startup. This lookup never rereads the CSV.
            var rows = GetDefinitionsSnapshot().Rows;
            var id = rows.Any(row => row.Id == failure.AlarmId) ? failure.AlarmId
                : rows.Any(row => row.Id == $"SEQ_{failure.Scenario}_NG") ? $"SEQ_{failure.Scenario}_NG" : "SEQ_AUTO_NG";
            SetAlarm(id);
        }

        public bool SetAlarm(int code)
        {
            var definition = GetDefinitionsSnapshot().Rows.FirstOrDefault(row => row.Code == code) ?? throw new ArgumentException($"등록되지 않은 알람 CODE: {code}", nameof(code));
            return SetAlarm(definition);
        }

        public bool SetAlarm(string id)
        {
            var definition = GetDefinitionsSnapshot().Rows.FirstOrDefault(row => row.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"등록되지 않은 알람 ID: {id}", nameof(id));
            return SetAlarm(definition);
        }

        private bool SetAlarm(ST_ALARM_DEFINITION definition) => AddCurrent(new(definition.Code, definition.Level == "LIGHT" ? EN_ALARM_LEVEL.Warning : EN_ALARM_LEVEL.Critical, definition.Cause, definition.Solution, _now(), definition.Group) { Id = definition.Id, Group = definition.Group, CimAddress = definition.CimAddress });
        private bool AddCurrent(ST_ALARM_DATA alarm)
        {
            lock (_currentLock)
            {
                if (!_curAlarm.TryAdd(alarm.Code, alarm))
                    return false;
                PublishCurrent(_current.OccurrenceVersion + 1, _current.LogError);
                // Only enqueue memory here, in occurrence order. The dedicated worker owns all Append I/O.
                if (!QueueHistory(alarm))
                {
                    var error = $"알람 {alarm.Code} 로그 저장 대기열 포화: {HistoryQueueCapacity}건. 해당 이력은 저장되지 않았습니다.";
                    Volatile.Write(ref _historyError, error);
                    PublishCurrent(_current.OccurrenceVersion, error);
                }
            }

            NotifyCurrentChanged();
            return true;
        }

        public void Reset()
        {
            lock (_currentLock)
            {
                if (_curAlarm.Count == 0)
                    return;
                _curAlarm.Clear();
                PublishCurrent(_current.OccurrenceVersion, _current.LogError);
            }

            NotifyCurrentChanged();
        }

        private void NotifyCurrentChanged()
        {
            var handlers = CurrentChanged;
            var errors = new List<string>();
            if (handlers is not null)
            {
                foreach (Action handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler();
                    }
                    catch (Exception exception)
                    {
                        // Subscribers dispatch to their own UI context. A failed observer must not
                        // change SetAlarm/Reset's result or prevent the other observers from running.
                        errors.Add($"{handler.Method.Name}: {exception.Message}");
                    }
                }
            }

            Volatile.Write(ref _notificationError, string.Join(Environment.NewLine, errors));
        }

        private void PublishCurrent(long occurrenceVersion, string error)
        {
            var rows = Array.AsReadOnly(_curAlarm.Values.OrderByDescending(row => row.Severity).ThenBy(row => row.OccurredAt).ThenBy(row => row.Code).ToArray());
            Volatile.Write(ref _current, new(_current.Revision + 1, occurrenceVersion, rows, error));
        }

        public ST_ALARM_HISTORY_RESULT ReadHistory(DateTime from, DateTime through, CancellationToken token = default) => _history?.Read(from, through, token) ?? new([], "알람 로그 저장소가 연결되지 않았습니다.");
        public string HistoryStorageError
        {
            get
            {
                var error = Volatile.Read(ref _historyError);
                var startedAt = Volatile.Read(ref _historyWriteStartedAt);
                if (startedAt > 0 && Environment.TickCount64 - startedAt >= 5000)
                    return string.IsNullOrEmpty(error) ? "알람 이력 저장이 5초 이상 지연되고 있습니다." : error + Environment.NewLine + "알람 이력 저장이 5초 이상 지연되고 있습니다.";
                return error;
            }
        }

        // Called under _currentLock, after publishing the occurrence. No external I/O or waiting here.
        private bool QueueHistory(ST_ALARM_DATA alarm)
        {
            if (_history is null)
                return true;
            lock (_historyQueueLock)
            {
                if (_historyQueue.Count >= HistoryQueueCapacity)
                    return false;
                _historyQueue.Enqueue(alarm);
                _historyTask ??= Task.Run(WriteQueuedHistory);
                return true;
            }
        }

        private void WriteQueuedHistory()
        {
            while (true)
            {
                ST_ALARM_DATA alarm;
                lock (_historyQueueLock)
                {
                    if (!_historyQueue.TryDequeue(out alarm!))
                    {
                        _historyTask = null;
                        return;
                    }
                }

                Volatile.Write(ref _historyWriteStartedAt, Environment.TickCount64);
                try
                {
                    _history!.Append(alarm);
                }
                catch (Exception exception)
                {
                    var error = $"알람 {alarm.Code} 로그 저장 실패: {exception.Message}";
                    Volatile.Write(ref _historyError, error);
                    lock (_currentLock)
                    {
                        // A later RESET/new alarm must survive a delayed failure from an older write.
                        PublishCurrent(_current.OccurrenceVersion, error);
                    }

                    NotifyCurrentChanged();
                }
                finally
                {
                    Volatile.Write(ref _historyWriteStartedAt, 0);
                }
            // Single attempt only: no retries, persistence journal, or replay after restart.
            }
        }

        /// <summary>현재 저장 대기열의 처리를 비동기로 기다립니다. 일반 감시 처리에서는 호출하지 않습니다.</summary>
        public Task FlushHistoryAsync(CancellationToken cancellationToken = default)
        {
            Task pending;
            lock (_historyQueueLock)
                pending = _historyTask ?? Task.CompletedTask;
            return pending.WaitAsync(cancellationToken);
        }

#endregion
#region Monitoring
        public string MonitoringError
        {
            get
            {
                var checks = Volatile.Read(ref _monitoringError);
                var history = HistoryStorageError;
                return string.IsNullOrEmpty(history) ? checks : string.IsNullOrEmpty(checks) ? history : checks + Environment.NewLine + history;
            }
        }

        /// <summary>단일 백그라운드 작업자에서 알람 조건을 감시합니다.</summary>
        /// <remarks>
        /// reportError는 감시 작업자 스레드에서 동기적으로 호출되며 동일 오류는 반복 보고하지 않습니다.
        /// CurrentChanged와 마찬가지로 UI를 갱신하는 콜백은 Dispatcher/BeginInvoke 또는
        /// SynchronizationContext.Post로 UI 스레드에 전달해야 합니다. 로그 전용 콜백은 저장소의 스레드 안전성을 보장해야 합니다.
        /// readDeviceSnapshot은 메모리에 공개된 스냅샷만 반환해야 하며 장치 통신을 수행하면 안 됩니다.
        /// </remarks>
        public void StartMonitoring(
            CSettingManager settings, 
            CAutomationManager automation, 
            Func<ST_DEVICE_STATUS> readDeviceSnapshot, 
            CInterLockManager interLocks, 
            Action<Exception>? reportError = null
        )
        {
            EnsureDefinitionsRequested();
            ArgumentNullException.ThrowIfNull(readDeviceSnapshot);
            var temperatures = new CScannerTemperatureAlarmMonitor(this, settings, automation);
            StartMonitoring([token =>
            {
                token.ThrowIfCancellationRequested();
                temperatures.Check();
            }, token =>
            {
                token.ThrowIfCancellationRequested();
                var status = readDeviceSnapshot();
                Build(status, interLocks.Evaluate(status));
                if (!string.IsNullOrEmpty(status.SnapshotError))
                    throw new IOException(status.SnapshotError);
            }], reportError, TimeSpan.FromSeconds(1));
        }

        // A single worker executes the checks in order; slow checks never overlap the next cycle.
        internal void StartMonitoring(Action<CancellationToken>[] checks, Action<Exception>? reportError, TimeSpan interval)
        {
            ArgumentNullException.ThrowIfNull(checks);
            EnsureDefinitionsRequested();
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval));
            var registeredChecks = checks.ToArray();
            lock (_monitoringLock)
            {
                if (_monitoringTask is not null)
                    return;
                _monitoringStop = new CancellationTokenSource();
                var token = _monitoringStop.Token;
                _monitoringTask = Task.Run(() => Monitor(registeredChecks, reportError, interval, token));
            }
        }

        public async Task StopMonitoringAsync()
        {
            Task task;
            CancellationTokenSource stop;
            lock (_monitoringLock)
            {
                if (_monitoringTask is null || _monitoringStop is null)
                    return;
                task = _monitoringTask;
                stop = _monitoringStop;
                stop.Cancel();
            }

            await task.ConfigureAwait(false);
            lock (_monitoringLock)
            {
                if (!ReferenceEquals(_monitoringTask, task))
                    return;
                _monitoringTask = null;
                _monitoringStop = null;
                stop.Dispose();
            }
        }

        private async Task Monitor(Action<CancellationToken>[] checks, Action<Exception>? reportError, TimeSpan interval, CancellationToken token)
        {
            using var timer = new PeriodicTimer(interval);
            var previousErrors = new string[checks.Length];
            var previousDefinitionError = "";
            try
            {
                do
                {
                    var errors = new List<string>();
                    var definitionError = RetryDefinitionInitialization();
                    if (!string.IsNullOrEmpty(definitionError))
                    {
                        Volatile.Write(ref _monitoringError, $"Alarm definitions: {definitionError}");
                        if (previousDefinitionError != definitionError)
                        {
                            previousDefinitionError = definitionError;
                            try
                            {
                                reportError?.Invoke(new InvalidOperationException($"알람 정의 초기화 대기: {definitionError}"));
                            }
                            catch (Exception reportException)
                            {
                                System.Diagnostics.Debug.WriteLine(reportException);
                            }
                        }

                        continue; // No SetAlarm/Build/device snapshot checks until catalog recovery succeeds.
                    }

                    previousDefinitionError = "";
                    for (var index = 0; index < checks.Length; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            checks[index](token);
                            previousErrors[index] = "";
                        }
                        catch (OperationCanceledException)when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            var message = $"Alarm check {index + 1}: {exception.Message}";
                            errors.Add(message);
                            if (previousErrors[index] != message)
                            {
                                previousErrors[index] = message;
                                try
                                {
                                    reportError?.Invoke(exception);
                                }
                                catch (Exception reportException)
                                {
                                    System.Diagnostics.Debug.WriteLine(reportException);
                                }
                            }
                        }
                    }

                    Volatile.Write(ref _monitoringError, string.Join(Environment.NewLine, errors));
                }
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
            }
            catch (OperationCanceledException)when (token.IsCancellationRequested)
            {
            }
        }
#endregion
    }

    public sealed record ST_ALARM_DEFINITION(int Code, string Group, string Id, string Level, string Cause, string Solution, string CimAddress);
    public abstract class CAlarmListFileBase
    {
        public abstract IReadOnlyList<ST_ALARM_DEFINITION> Load();
    }

    public sealed record ST_ALARM_DEFINITION_SNAPSHOT(IReadOnlyList<ST_ALARM_DEFINITION> Rows, string Error);
    public enum EN_ALARM_LEVEL
    {
        Info,
        Warning,
        Critical
    }

    public enum EN_ALARM_STATE
    {
        Clear,
        Occur
    }

    public sealed record ST_ALARM_DATA(int Code, EN_ALARM_LEVEL Severity, string Message, string RecoveryAction, DateTimeOffset OccurredAt, string Device = "SYSTEM", string StationName = "COMMON")
    {
        public string Id { get; init; } = "";
        public string Group { get; init; } = "";
        public string CimAddress { get; init; } = "";
    }

    public sealed record ST_CURRENT_ALARM_SNAPSHOT(long Revision, long OccurrenceVersion, IReadOnlyList<ST_ALARM_DATA> Rows, string LogError);
    // Uses the shared settings/Automation caches regardless of the currently selected menu or heads.
    internal sealed class CScannerTemperatureAlarmMonitor(CAlarmManager alarms, CSettingManager settings, CAutomationManager automation)
    {
        public void Check()
        {
            var options = settings.LoadSection(EN_SETTING_TAB.Option);
            var limitText = options.FirstOrDefault(option => option.Key.Equals("SCANNER_TEMP_LIMIT", StringComparison.OrdinalIgnoreCase) || option.Name.Equals("SCANNER_TEMP_LIMIT", StringComparison.OrdinalIgnoreCase))?.Value;
            if ((!double.TryParse(limitText, NumberStyles.Float, CultureInfo.InvariantCulture, out var limit) && !double.TryParse(limitText, NumberStyles.Float, CultureInfo.CurrentCulture, out limit)) || !double.IsFinite(limit) || limit < 0)
            {
                var displayedValue = limitText is null ? "<누락>" : string.IsNullOrWhiteSpace(limitText) ? "<빈 값>" : limitText;
                // Let the owning monitor expose MonitoringError and reportError instead of
                // silently treating a disabled temperature check as a successful cycle.
                throw new InvalidDataException($"Scanner Temp Limit (SCANNER_TEMP_LIMIT) 설정 오류: " + $"0 이상의 유한한 온도(°C)가 필요합니다. 현재 값: {displayedValue}. " + "Setting > OPTION > INTERLOCK에서 확인해 주세요.");
            }

            for (var headNo = 1; headNo <= 8; headNo++)
            {
                var overLimit = false;
                foreach (var axis in CScannerAxisConfiguration.BuildAxisDefinitions(options, headNo))
                {
                    if (!axis.IsUsed || !axis.ReadExtendedStatus || !string.IsNullOrEmpty(axis.ConfigurationError))
                        continue;
                    if (!automation.IsSimul(axis.AutomationNo) && !automation.IsConnect(axis.AutomationNo))
                        continue;
                    // GetAxis only registers/reads the cache; the shared acquisition service owns device reads.
                    var snapshot = automation.StatusSnapshots.GetAxis(axis.AxisReference, axis.AutomationNo, axis.AioNo, axis.ReadExtendedStatus);
                    if (snapshot.State != "OK" || snapshot.Value is not { } status)
                        continue;
                    overLimit |= double.IsFinite(status.MotorTemperature) && status.MotorTemperature > limit;
                }

                // 1048..1055 correspond to SCANNER 1..8. Keep both X/Y subscribed even when one is hot.
                if (overLimit)
                    alarms.SetAlarm(1047 + headNo);
            }
        }
    }

    /// <summary>Preserve the innermost failing scenario/action or signal across nested sequence calls.</summary>
    public sealed class CSequenceFailureException : InvalidOperationException
    {
        private int _reported;
        private CSequenceFailureException(string scenario, string subject, string kind, Exception error)
            : base($"{scenario} / {subject} {kind}: {error.Message}", error)
        { Scenario = scenario; Subject = subject; Kind = kind; }
        public string Scenario { get; }
        public string Subject { get; }
        public string Kind { get; }
        public string AlarmId => Subject.Length == 0 ? $"SEQ_{Scenario}_NG" : $"SEQ_{Scenario}_{Subject}_{Kind}";
        public static string ActionScenario(string id) => id.StartsWith("MOVE_", StringComparison.Ordinal) ? "PROCESS_MOVE"
            : id.StartsWith("DISTORTION_", StringComparison.Ordinal) ? "DISTORTION"
            : id.StartsWith("IOF_", StringComparison.Ordinal) ? "IOF"
            : id.StartsWith("DOREADY_", StringComparison.Ordinal) ? "DOREADY"
            : id.StartsWith("PROCESS_", StringComparison.Ordinal) ? "PROCESS"
            : id.StartsWith("REVIEW_", StringComparison.Ordinal) ? "REVIEW"
            : id.StartsWith("TPM_", StringComparison.Ordinal) ? "TPM" : "AUTO";
        public static string SignalScenario(string id, string fallback)
        {
            if (id.Contains("DISTORTION", StringComparison.Ordinal)) return "DISTORTION";
            if (id.Contains("TOP_POWERMETER", StringComparison.Ordinal) || id == "PROCESS_SEQUENCE_TPM_LASER_ON") return "TPM";
            if (id.Contains("DOREADY", StringComparison.Ordinal)) return "DOREADY";
            if (id.Contains("DO_ALIGN", StringComparison.Ordinal) || id == "STAGE_MCC_BUSY_FLAGS") return "DO_ALIGN";
            if (id.Contains("IOF", StringComparison.Ordinal)) return "IOF";
            if (id.Contains("_SDC", StringComparison.Ordinal) || id.Contains("REVIEW_MOVE_START", StringComparison.Ordinal) || id.Contains("REVIEW_GENERAL_RUNNING", StringComparison.Ordinal) || id.StartsWith("PROCESS_REVIEW_", StringComparison.Ordinal)) return "PROCESS_MOVE";
            if (id.Contains("HANDSHAKE_", StringComparison.Ordinal) && id.Contains("_PROCESS_", StringComparison.Ordinal)) return "PROCESS";
            if (id.Contains("HANDSHAKE_", StringComparison.Ordinal) && id.Contains("_REVIEW_", StringComparison.Ordinal)) return "REVIEW";
            return fallback;
        }
        private static CSequenceFailureException? Existing(Exception error) => error is CSequenceFailureException found ? found
            : error is AggregateException group ? group.InnerExceptions.Select(Existing).FirstOrDefault(item => item is not null)
            : error.InnerException is { } inner ? Existing(inner) : null;
        public static CSequenceFailureException Action(string id, Exception error) => Existing(error)
            ?? (error is CSequenceSignalCanceledException wait ? Signal(wait.Scenario, wait.Signal, true, error) : new(ActionScenario(id), id, "NG", error));
        public static CSequenceFailureException Signal(string scenario, string id, bool timeout, Exception error) => Existing(error)
            ?? new(SignalScenario(id, scenario), id, timeout ? "TIMEOUT" : "NG", error);
        public static CSequenceFailureException ScenarioFailure(string scenario, Exception error) => Existing(error) ?? new(scenario, "", "NG", error);
        public void Publish(Action<CSequenceFailureException>? subscribers)
        {
            if (subscribers is null || Interlocked.Exchange(ref _reported, 1) != 0) return;
            foreach (var subscriber in subscribers.GetInvocationList())
                try { ((Action<CSequenceFailureException>)subscriber)(this); }
                catch (Exception error) { try { System.Diagnostics.Trace.TraceError($"Sequence alarm publish failed: {AlarmId}: {error}"); } catch { } }
        }
    }
    // A parent Action can distinguish its own deadline from an operator's cancellation
    // while retaining the exact signal being awaited.
    public sealed class CSequenceSignalCanceledException(string scenario, string signal, OperationCanceledException error)
        : OperationCanceledException(error.Message, error, error.CancellationToken)
    {
        public string Scenario { get; } = scenario;
        public string Signal { get; } = signal;
    }

    public sealed record ST_ALARM_HISTORY_ENTRY(DateTimeOffset OccurredAt, int Code, string Id, string Group, string Level, string Cause, string Solution, string CimAddress);
    public sealed record ST_ALARM_HISTORY_RESULT(IReadOnlyList<ST_ALARM_HISTORY_ENTRY> Rows, string Warning);
    public interface IAlarmHistoryStore
    {
        // Called in FIFO order by a dedicated writer, outside the alarm-state lock and monitoring worker.
        // Implementations synchronize shared storage with readers/other manager instances themselves.
        void Append(ST_ALARM_DATA alarm);
        ST_ALARM_HISTORY_RESULT Read(DateTime from, DateTime through, CancellationToken token = default);
    }
}
