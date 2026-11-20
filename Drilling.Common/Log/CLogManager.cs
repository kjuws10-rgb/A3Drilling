namespace Drilling.Common.Log
{
    using System.Globalization;
    using System.Text;
    using System.Text.Json;
    using Drilling.Common.Alarm;
    using Drilling.Common.Interface;
    using Drilling.Common.InterLock;
    using Drilling.Common.Managers;
    using Drilling.Common.Motion;
    using Drilling.Common.Station;
    using System.Diagnostics;
    using System.Threading.Channels;
    using System.Text.RegularExpressions;
    using System.IO;
    using System.Collections.Concurrent;

    public sealed partial class CLogManager : IAlarmHistoryStore
    {
#region 공유 상태·초기값
        private readonly string _alarmLogRoot;
        private static readonly JsonSerializerOptions AlarmJson = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        private const int DefaultReadDays = 14;
        private const int DefaultRecipeReadRows = 10;
        private const int DefaultSettingReadRows = 20;
        // Managers writing the same file share a gate. Unrelated UI, device,
        // Recipe and Alarm files must not block each other's disk operations.
        private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);
        private static object GetFileLock(string path) => FileLocks.GetOrAdd(Path.GetFullPath(path), static _ => new object());
        private readonly string _recipeLogRoot;
        private readonly string _settingLogRoot;
        private readonly string _interfaceLogRoot;
        private readonly string _stationLogRoot;
        private readonly string _productLogRoot;
        private readonly string _uiLogRoot;
        private readonly string _pmHistoryRoot;
        private readonly object _pmHistoryLock = new();
        private IReadOnlyList<ST_PM_HISTORY_ENTRY>? _pmHistoryCache;
        private readonly object _sequenceLogLock = new();
        private readonly CSequenceLogText _sequenceText = new();
        private Channel<SequenceEntry>? _sequenceQueue;
        private Task? _sequenceWriter;
        private bool _sequenceStopped;
        private string _sequenceLogError = "";
        private readonly object _interfaceLogLock = new();
        private readonly Dictionary<string, InterfaceLogWriter> _interfaceWriters = new(StringComparer.OrdinalIgnoreCase);
        private bool _interfaceLoggingStopped;
        private string _interfaceLogError = "";
#endregion
#region 생성·초기화
        public CLogManager(string configRoot)
        {
            var root = Directory.GetParent(configRoot)?.FullName ?? configRoot;
            LogDirectory = Path.Combine(root, "Log");
            _alarmLogRoot = Path.Combine(root, "Log", "Alarm");
            _recipeLogRoot = Path.Combine(root, "Log", "Recipe");
            _settingLogRoot = Path.Combine(root, "Log", "Setting");
            _interfaceLogRoot = Path.Combine(root, "Log", "Interface");
            _stationLogRoot = Path.Combine(root, "Log", "Station");
            _productLogRoot = Path.Combine(root, "Log", "Product");
            _uiLogRoot = Path.Combine(root, "Log", "UI");
            _pmHistoryRoot = Path.Combine(root, "Log", "PM");
        }

#endregion
#region 기본 처리
        public string LogDirectory { get; }

        public ST_PM_HISTORY_ENTRY StartPmSession(string reason, string operatorName)
        {
            var normalizedReason = reason?.Trim() ?? "";
            if (normalizedReason.Length == 0)
                throw new ArgumentException("PM 사유를 입력해 주세요.", nameof(reason));
            if (normalizedReason.Length > 200)
                throw new ArgumentException("PM 사유는 200자 이하로 입력해 주세요.", nameof(reason));

            var now = DateTimeOffset.Now;
            var sessionId = $"{now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
            var entry = new ST_PM_HISTORY_ENTRY(sessionId, now, null, normalizedReason, string.IsNullOrWhiteSpace(operatorName) ? "-" : operatorName.Trim());
            lock (_pmHistoryLock)
            {
                WritePmHistoryEntry(entry);
                if (_pmHistoryCache is not null)
                    _pmHistoryCache = _pmHistoryCache.Prepend(entry).OrderByDescending(item => item.LockedAt).ToArray();
            }
            return entry;
        }

        public ST_PM_HISTORY_ENTRY CompletePmSession(ST_PM_HISTORY_ENTRY session)
        {
            ArgumentNullException.ThrowIfNull(session);
            lock (_pmHistoryLock)
            {
                var path = PmHistoryPath(session);
                if (!System.IO.File.Exists(path))
                    throw new FileNotFoundException("PM LOCK 이력 파일을 찾을 수 없습니다.", path);
                var current = ReadPmHistoryEntry(path) ?? throw new InvalidDataException("PM LOCK 이력 파일을 읽을 수 없습니다.");
                if (!current.SessionId.Equals(session.SessionId, StringComparison.Ordinal))
                    throw new InvalidDataException("PM LOCK 세션 ID가 일치하지 않습니다.");
                if (current.UnlockedAt is not null)
                    return current;

                var completed = current with { UnlockedAt = DateTimeOffset.Now };
                WritePmHistoryEntry(completed);
                if (_pmHistoryCache is not null)
                    _pmHistoryCache = _pmHistoryCache.Select(item => item.SessionId == completed.SessionId ? completed : item).OrderByDescending(item => item.LockedAt).ToArray();
                return completed;
            }
        }

        public IReadOnlyList<ST_PM_HISTORY_ENTRY> ReadPmHistory(int maxRows = 200)
        {
            if (maxRows <= 0)
                return [];
            lock (_pmHistoryLock)
            {
                _pmHistoryCache ??= LoadPmHistory();
                return _pmHistoryCache.Take(maxRows).ToArray();
            }
        }

        private IReadOnlyList<ST_PM_HISTORY_ENTRY> LoadPmHistory()
        {
            if (!Directory.Exists(_pmHistoryRoot))
                return [];
            return Directory.EnumerateFiles(_pmHistoryRoot, "PM_*.json", SearchOption.AllDirectories)
                .Select(ReadPmHistoryEntry)
                .Where(item => item is not null)
                .Cast<ST_PM_HISTORY_ENTRY>()
                .OrderByDescending(item => item.LockedAt)
                .ToArray();
        }

        private ST_PM_HISTORY_ENTRY? ReadPmHistoryEntry(string path)
        {
            try
            {
                return JsonSerializer.Deserialize<ST_PM_HISTORY_ENTRY>(System.IO.File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                Trace.TraceError($"PM history read failed. Path={path}; Error={exception}");
                return null;
            }
        }

        private void WritePmHistoryEntry(ST_PM_HISTORY_ENTRY entry)
        {
            var path = PmHistoryPath(entry);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                System.IO.File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entry, AlarmJson), new UTF8Encoding(false));
                System.IO.File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (System.IO.File.Exists(temporaryPath))
                    System.IO.File.Delete(temporaryPath);
            }
        }

        private string PmHistoryPath(ST_PM_HISTORY_ENTRY entry) => Path.Combine(
            _pmHistoryRoot,
            entry.LockedAt.ToString("yyyy", CultureInfo.InvariantCulture),
            entry.LockedAt.ToString("MM", CultureInfo.InvariantCulture),
            entry.LockedAt.ToString("dd", CultureInfo.InvariantCulture),
            $"PM_{entry.SessionId}.json");

        public void WriteStationState(string stationName, string stateName, string action, string detail)
        {
            var now = DateTime.Now;
            var directory = Path.Combine(_stationLogRoot, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var path = Path.Combine(directory, $"Station_{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}_Trace.txt");
            var time = now.ToString("yy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = $"{time} \\STATION\\{EscapeField(stationName)}\\{EscapeField(stateName)}\\{EscapeField(action)}\\{EscapeField(detail)}";
            AppendLine(directory, path, line);
        }

        public IReadOnlyList<ST_RECIPE_HISTORY> ReadRecipeRecent(string recipeName, string recipeId, int maxRows = DefaultRecipeReadRows, int days = DefaultReadDays)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                recipeName,
                recipeId
            };
            bool ShouldIncludeItem(global::Drilling.Common.Managers.ST_RECIPE_HISTORY? item)
            {
                return item is not null;
            }

            bool ShouldIncludeRecipe(global::Drilling.Common.Managers.ST_RECIPE_HISTORY item)
            {
                return names.Contains(item.RecipeName);
            }

            global::System.DateTimeOffset SelectChangedAt(global::Drilling.Common.Managers.ST_RECIPE_HISTORY item)
            {
                return item.ChangedAt;
            }

            return EnumerateRecentLogFiles(_recipeLogRoot, days).SelectMany(ReadLogFile).Select(ParseRecipeLine).Where(ShouldIncludeItem).Cast<ST_RECIPE_HISTORY>().Where(ShouldIncludeRecipe).OrderByDescending(SelectChangedAt).Take(maxRows).ToArray();
        }

        public void WriteRecipeCreate(string recipeName)
        {
            WriteRecipeAction(recipeName, "CREATE", "Recipe", "-", recipeName);
        }

        public IReadOnlyList<ST_RECIPE_HISTORY> ReadRecipeRecent(int maxRows = 200, int days = DefaultReadDays)
        {
            return EnumerateRecentLogFiles(_recipeLogRoot, days).SelectMany(ReadLogFile).Select(ParseRecipeLine).OfType<ST_RECIPE_HISTORY>().OrderByDescending(item => item.ChangedAt).Take(maxRows).ToArray();
        }

        public void WriteRecipeSelect(string previousRecipeName, string recipeName)
        {
            WriteRecipeAction(recipeName, "SELECT", "Recipe", string.IsNullOrWhiteSpace(previousRecipeName) ? "-" : previousRecipeName, recipeName);
        }

        public void WriteRecipeDelete(string recipeName)
        {
            WriteRecipeAction(recipeName, "DELETE", "Recipe", recipeName, "-");
        }

        public void WriteRecipeSave(string recipeName)
        {
            WriteRecipeAction(recipeName, "SAVE", "Recipe", "-", recipeName);
        }

        public void WriteRecipeModify(string recipeName, string itemName, string oldValue, string newValue)
        {
            WriteRecipeAction(recipeName, "MODIFY", itemName, oldValue, newValue);
        }

        public void WriteRecipeModify(string recipeName, string tab, string group, string itemName, string oldValue, string newValue)
        {
            WriteRecipeAction(recipeName, "MODIFY", tab, group, itemName, oldValue, newValue);
        }

        public void WriteRecipeModifyBatch(string recipeName, IReadOnlyList<(string Tab, string Group, string ItemName, string OldValue, string NewValue)> changes)
        {
            if (changes.Count == 0)
            {
                return;
            }

            var now = DateTime.Now;
            var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var directory = Path.Combine(_recipeLogRoot, date);
            var path = Path.Combine(directory, $"Recipe_{date}_Trace.txt");
            var time = now.ToString("yy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var escapedRecipeName = EscapeField(recipeName);
            var builder = new StringBuilder(changes.Count * 128);
            foreach (var change in changes)
            {
                builder.Append(time).Append(" \\").Append(escapedRecipeName).Append("\\MODIFY\\").Append(EscapeField(change.Tab)).Append('\\').Append(EscapeField(change.Group)).Append('\\').Append(EscapeField(change.ItemName)).Append('\\').Append(EscapeField(change.OldValue)).Append('\\').Append(EscapeField(change.NewValue)).AppendLine();
            }

            AppendText(directory, path, builder.ToString());
        }

        public void WriteRecipeRename(string oldRecipeName, string newRecipeName)
        {
            WriteRecipeAction(newRecipeName, "RENAME", "Recipe", oldRecipeName, newRecipeName);
        }

        public IReadOnlyList<ST_SETTING_HISTORY> ReadSettingRecent(EN_SETTING_TAB section, int maxRows = DefaultSettingReadRows, int days = DefaultReadDays)
        {
            bool ShouldIncludeItem(global::Drilling.Common.Managers.ST_SETTING_HISTORY? item)
            {
                return item is not null;
            }

            bool MatchesSection(global::Drilling.Common.Managers.ST_SETTING_HISTORY item)
            {
                return item.Section == section;
            }

            global::System.DateTimeOffset SelectChangedAt(global::Drilling.Common.Managers.ST_SETTING_HISTORY item)
            {
                return item.ChangedAt;
            }

            return EnumerateRecentLogFiles(_settingLogRoot, days).SelectMany(ReadLogFile).Select(ParseSettingLine).Where(ShouldIncludeItem).Cast<ST_SETTING_HISTORY>().Where(MatchesSection).OrderByDescending(SelectChangedAt).Take(maxRows).ToArray();
        }

        public void WriteSettingModify(EN_SETTING_TAB section, string parameterName, string oldValue, string newValue)
        {
            WriteSettingAction(section, "MODIFY", parameterName, oldValue, newValue);
        }

        public void WriteSettingSave(EN_SETTING_TAB section)
        {
            WriteSettingAction(section, "SAVE", "Section", "-", section.ToString().ToUpperInvariant());
        }

        public void WriteInterfaceConnection(EN_EQP_MODULE module, string action, string nickName, string oldState, string newState)
        {
            WriteInterfaceAction(module, action, nickName, oldState, newState);
        }

        public void WriteInterfaceCommand(EN_EQP_MODULE module, string nickName, string command, string response, string detail = "")
        {
            var after = string.IsNullOrWhiteSpace(detail) ? response : $"{response} / {detail}";
            WriteInterfaceAction(module, "COMMAND", nickName, command, after);
        }

        public void WriteInterfaceError(EN_EQP_MODULE module, string nickName, string command, string detail)
        {
            WriteInterfaceAction(module, "ERROR", nickName, command, $"ERROR / {detail}");
        }

        public void WriteInterfaceProtocolTrace(EN_EQP_MODULE module, DateTimeOffset occurredAt, string packetLog)
        {
            if (string.IsNullOrWhiteSpace(packetLog))
            {
                return;
            }

            var localTime = occurredAt.LocalDateTime;
            var moduleName = ModuleLogName(module);
            var date = localTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var directory = Path.Combine(_interfaceLogRoot, moduleName, date);
            var path = Path.Combine(directory, $"Interface_{moduleName}_{date}_Trace.txt");
            var time = localTime.ToString("yy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var lines = packetLog.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                builder.Append(time).Append(' ').AppendLine(line);
            }

            QueueInterfaceText(directory, path, builder.ToString());
        }

        public void WriteProductEvent(string productId, string action, string state, string result, string detail)
        {
            var now = DateTime.Now;
            var directory = Path.Combine(_productLogRoot, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var path = Path.Combine(directory, $"Product_{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}_Trace.txt");
            var time = now.ToString("yy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = $"{time} \\PRODUCT\\{EscapeField(productId)}\\{EscapeField(action)}\\{EscapeField(state)}\\{EscapeField(result)}\\{EscapeField(detail)}";
            AppendLine(directory, path, line);
        }

        public void WriteUiClick(string screenName, string controlName, string parameter, string detail = "")
        {
            WriteUiAction("CLICK", screenName, controlName, parameter, detail);
        }

        private void WriteRecipeAction(string recipeName, string action, string itemName, string oldValue, string newValue)
        {
            var now = DateTime.Now;
            var directory = Path.Combine(_recipeLogRoot, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var path = Path.Combine(directory, $"Recipe_{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}_Trace.txt");
            var time = now.ToString("yy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = $"{time} \\{recipeName}\\{action}\\{itemName}\\{oldValue}\\{newValue}";
            AppendLine(directory, path, line);
        }

        private void WriteRecipeAction(string recipeName, string action, string tab, string group, string itemName, string oldValue, string newValue)
        {
            var now = DateTime.Now;
            var directory = Path.Combine(_recipeLogRoot, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var path = Path.Combine(directory, $"Recipe_{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}_Trace.txt");
            var time = now.ToString("yy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = $"{time} \\{EscapeField(recipeName)}\\{action}\\{EscapeField(tab)}\\{EscapeField(group)}\\{EscapeField(itemName)}\\{EscapeField(oldValue)}\\{EscapeField(newValue)}";
            AppendLine(directory, path, line);
        }

        private void WriteSettingAction(EN_SETTING_TAB section, string action, string parameterName, string oldValue, string newValue)
        {
            var now = DateTime.Now;
            var directory = Path.Combine(_settingLogRoot, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var path = Path.Combine(directory, $"Setting_{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}_Trace.txt");
            var time = now.ToString("yy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = $"{time} \\SETTING\\{section.ToString().ToUpperInvariant()}\\{action}\\{EscapeField(parameterName)}\\{EscapeField(oldValue)}\\{EscapeField(newValue)}";
            AppendLine(directory, path, line);
        }

        private void WriteInterfaceAction(EN_EQP_MODULE module, string action, string nickName, string oldState, string newState)
        {
            var now = DateTime.Now;
            var moduleName = ModuleLogName(module);
            var directory = Path.Combine(_interfaceLogRoot, moduleName, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var path = Path.Combine(directory, $"Interface_{moduleName}_{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}_Trace.txt");
            var time = now.ToString("yy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = $"{time} \\INTERFACE\\{moduleName}\\{EscapeField(nickName)}\\{action}\\{EscapeField(oldState)}\\{EscapeField(newState)}";
            QueueInterfaceText(directory, path, line + Environment.NewLine);
        }

        private void WriteUiAction(string action, string screenName, string controlName, string parameter, string detail)
        {
            var now = DateTime.Now;
            var directory = Path.Combine(_uiLogRoot, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var path = Path.Combine(directory, $"UI_{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}_Trace.txt");
            var time = now.ToString("yy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = $"{time} \\UI\\{action}\\{EscapeField(screenName)}\\{EscapeField(controlName)}\\{EscapeField(parameter)}\\{EscapeField(detail)}";
            AppendLine(directory, path, line);
        }

        private static void AppendLine(string directory, string path, string line)
        {
            AppendText(directory, path, line + Environment.NewLine);
        }

        private static void AppendText(string directory, string path, string text)
        {
            lock (GetFileLock(path))
            {
                Directory.CreateDirectory(directory);
                System.IO.File.AppendAllText(path, text, Encoding.Unicode);
            }
        }

        // File I/O belongs to one bounded writer per interface file, never to the
        // MELSEC/Vision command or UI caller. Different device log files cannot block
        // each other, and timestamps/order within each file are captured at enqueue.
        public string InterfaceLogError => Volatile.Read(ref _interfaceLogError);

        private void QueueInterfaceText(string directory, string path, string text)
        {
            lock (_interfaceLogLock)
            {
                if (_interfaceLoggingStopped)
                    return;
                if (!_interfaceWriters.TryGetValue(path, out var writer))
                    _interfaceWriters[path] = writer = new(directory, path, InterfaceWriteFailed);
                if (!writer.TryWrite(text))
                    InterfaceWriteFailed(new IOException("Interface log queue capacity exceeded: " + path));
            }
        }

        private void InterfaceWriteFailed(Exception error)
        {
            // Reporting a diagnostic failure must not invoke user callbacks or disk
            // listeners on a device-control thread. Flush/Stop exposes this failure.
            Interlocked.Exchange(ref _interfaceLogError, "Interface log write failed: " + error.Message);
        }

        public async Task FlushInterfaceAsync(CancellationToken token = default)
        {
            InterfaceLogWriter[] writers;
            lock (_interfaceLogLock)
                writers = _interfaceWriters.Values.ToArray();
            await Task.WhenAll(writers.Select(writer => writer.Flush(token))).ConfigureAwait(false);
            if (InterfaceLogError.Length != 0)
                throw new IOException(InterfaceLogError);
        }

        public async Task StopInterfaceLoggingAsync(CancellationToken token = default)
        {
            Task[] tasks;
            lock (_interfaceLogLock)
            {
                _interfaceLoggingStopped = true;
                tasks = _interfaceWriters.Values.Select(writer => writer.Complete()).ToArray();
            }
            await Task.WhenAll(tasks).WaitAsync(token).ConfigureAwait(false);
            if (InterfaceLogError.Length != 0)
                throw new IOException(InterfaceLogError);
        }

        private sealed class InterfaceLogWriter
        {
            private const long MaximumQueuedBytes = 16 * 1024 * 1024;
            private readonly Channel<(string Text, TaskCompletionSource? Barrier)> _queue = Channel.CreateBounded<(string, TaskCompletionSource?)>(
                new BoundedChannelOptions(16384) { SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait });
            private readonly Task _worker;
            private long _queuedBytes;

            public InterfaceLogWriter(string directory, string path, Action<Exception> reportError)
                => _worker = Task.Run(async () =>
                {
                    var reader = _queue.Reader;
                    while (await reader.WaitToReadAsync().ConfigureAwait(false))
                    {
                        // Coalesce short bursts into a single file open/write, with a
                        // bounded 10ms log-visibility delay and unchanged control latency.
                        await Task.Delay(10).ConfigureAwait(false);
                        var batch = new StringBuilder();
                        TaskCompletionSource? barrier = null;
                        var count = 0;
                        while (count++ < 256 && batch.Length < 128 * 1024 && reader.TryRead(out var entry))
                        {
                            batch.Append(entry.Text);
                            if (entry.Barrier is not null)
                            {
                                barrier = entry.Barrier;
                                break;
                            }
                        }
                        try
                        {
                            if (batch.Length > 0)
                                AppendText(directory, path, batch.ToString());
                        }
                        catch (Exception error) { reportError(error); }
                        finally
                        {
                            Interlocked.Add(ref _queuedBytes, -(long)batch.Length * sizeof(char));
                            barrier?.TrySetResult();
                        }
                    }
                });

            public bool TryWrite(string text)
            {
                var bytes = (long)text.Length * sizeof(char);
                if (Interlocked.Add(ref _queuedBytes, bytes) <= MaximumQueuedBytes && _queue.Writer.TryWrite((text, null)))
                    return true;
                Interlocked.Add(ref _queuedBytes, -bytes);
                return false;
            }

            public async Task Flush(CancellationToken token)
            {
                var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    await _queue.Writer.WriteAsync(("", barrier), token).ConfigureAwait(false);
                    await barrier.Task.WaitAsync(token).ConfigureAwait(false);
                }
                catch (ChannelClosedException) { await _worker.WaitAsync(token).ConfigureAwait(false); }
            }

            public Task Complete()
            {
                _queue.Writer.TryComplete();
                return _worker;
            }
        }

        private static IEnumerable<string> EnumerateRecentLogFiles(string logRoot, int days)
        {
            var today = DateTime.Today;
            for (var offset = 0; offset < days; offset++)
            {
                var date = today.AddDays(-offset).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                var directory = Path.Combine(logRoot, date);
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(directory, "*.txt"))
                {
                    yield return path;
                }
            }
        }

        private static IEnumerable<string> ReadLogFile(string path)
        {
            try
            {
                return System.IO.File.ReadAllLines(path);
            }
            catch (IOException)
            {
                return[];
            }
            catch (UnauthorizedAccessException)
            {
                return[];
            }
        }

        private static ST_RECIPE_HISTORY? ParseRecipeLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var parts = SplitFields(line);
            if (parts.Length < 3)
            {
                return null;
            }

            var changedAt = ParseTimestamp(parts[0].Trim());
            var recipeName = UnescapeField(parts[1].Trim());
            var action = parts[2].Trim();
            if (string.IsNullOrWhiteSpace(recipeName) || string.IsNullOrWhiteSpace(action))
            {
                return null;
            }

            return action.ToUpperInvariant() switch
            {
                "MODIFY" when parts.Length >= 8 => CreateRecipeHistory(changedAt, recipeName, action, UnescapeField(parts[5]), UnescapeField(parts[6]), UnescapeField(parts[7]), UnescapeField(parts[3]), UnescapeField(parts[4])),
                "MODIFY" when parts.Length >= 6 => CreateRecipeHistory(changedAt, recipeName, action, UnescapeField(parts[3]), UnescapeField(parts[4]), UnescapeField(parts[5])),
                "PARAM CHANGE" when parts.Length >= 7 => CreateRecipeHistory(changedAt, recipeName, action, $"{parts[3]} / {parts[4]}", parts[5], parts[6]),
                "PARAM CHANGE OFFSET X" when parts.Length >= 7 => CreateRecipeHistory(changedAt, recipeName, action, $"{parts[3]} / {parts[4]}", parts[5], parts[6]),
                "PARAM CHANGE OFFSET Y" when parts.Length >= 7 => CreateRecipeHistory(changedAt, recipeName, action, $"{parts[3]} / {parts[4]}", parts[5], parts[6]),
                "CREATE" => CreateRecipeHistory(changedAt, recipeName, action, "Recipe", "-", recipeName),
                "HOST RECIPE CREATE" => CreateRecipeHistory(changedAt, recipeName, action, "Recipe", "-", recipeName),
                "DELETE" => CreateRecipeHistory(changedAt, recipeName, action, "Recipe", recipeName, "-"),
                "SELECT" when parts.Length >= 6 => CreateRecipeHistory(changedAt, recipeName, action, "Recipe", UnescapeField(parts[4]), UnescapeField(parts[5])),
                "RENAME" when parts.Length >= 6 => CreateRecipeHistory(changedAt, recipeName, action, parts[3], parts[4], parts[5]),
                "CHANGE" => CreateRecipeHistory(changedAt, recipeName, action, "Recipe", "-", recipeName),
                "SAVE" => CreateRecipeHistory(changedAt, recipeName, action, "Recipe", "-", recipeName),
                _ when parts.Length >= 6 => CreateRecipeHistory(changedAt, recipeName, action, parts[3], parts[4], parts[5]),
                _ => CreateRecipeHistory(changedAt, recipeName, action, "Recipe", "-", "-")};
        }

        private static ST_RECIPE_HISTORY CreateRecipeHistory(DateTimeOffset changedAt, string recipeName, string action, string itemName, string oldValue, string newValue, string tab = "", string group = "")
        {
            return new ST_RECIPE_HISTORY(changedAt, itemName.Trim(), oldValue.Trim(), newValue.Trim(), "LOG", recipeName, action, tab.Trim(), group.Trim());
        }

        private static ST_SETTING_HISTORY? ParseSettingLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var parts = SplitFields(line);
            if (parts.Length < 7 || !parts[1].Trim().Equals("SETTING", StringComparison.OrdinalIgnoreCase) || !TryReadSection(parts[2], out var section))
            {
                return null;
            }

            return new ST_SETTING_HISTORY(ParseTimestamp(parts[0].Trim()), section, UnescapeField(parts[4].Trim()), UnescapeField(parts[5].Trim()), UnescapeField(parts[6].Trim()), "LOG", parts[3].Trim());
        }

        private static string[] SplitFields(string line)
        {
            var fields = new List<string>();
            var value = new StringBuilder();
            for (var index = 0; index < line.Length; index++)
            {
                var current = line[index];
                if (current != '\\')
                {
                    value.Append(current);
                    continue;
                }

                if (index + 1 < line.Length && line[index + 1] == '\\')
                {
                    value.Append('\\');
                    index++;
                    continue;
                }

                fields.Add(value.ToString());
                value.Clear();
            }

            fields.Add(value.ToString());
            return fields.ToArray();
        }

        private static string EscapeField(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\r", " ").Replace("\n", " ");
        }

        private static string ModuleLogName(EN_EQP_MODULE module)
        {
            return module switch
            {
                EN_EQP_MODULE.WonikCtrl => "WONIK_CTRL",
                EN_EQP_MODULE.TalonLaser => "TALON_LASER",
                EN_EQP_MODULE.PowerMeter => "POWER_METER",
                _ => module.ToString().ToUpperInvariant()};
        }

        private static string NormalizeModuleName(string value)
        {
            return new string (value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        }

        private static string UnescapeField(string value)
        {
            return value.Replace("\\\\", "\\");
        }

        private static bool TryReadSection(string value, out EN_SETTING_TAB section)
        {
            section = value.Trim().ToUpperInvariant() switch
            {
                "OPTION" => EN_SETTING_TAB.Option,
                "INTERFACE" => EN_SETTING_TAB.Interface,
                "IO" => EN_SETTING_TAB.Io,
                "MOTOR" => EN_SETTING_TAB.Motor,
                "POSITION" => EN_SETTING_TAB.Option,
                "ALARM" => EN_SETTING_TAB.Alarm,
                _ => EN_SETTING_TAB.Option
            };
            return value.Trim().ToUpperInvariant()is "OPTION" or "INTERFACE" or "IO" or "MOTOR" or "POSITION" or "ALARM";
        }

        private static DateTimeOffset ParseTimestamp(string value)
        {
            var formats = new[]
            {
                "yy/MM/dd HH:mm:ss.fff",
                "yyyy/MM/dd HH:mm:ss.fff",
                "yy-MM-dd HH:mm:ss.fff",
                "yyyy-MM-dd HH:mm:ss.fff"
            };
            return DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var result) ? new DateTimeOffset(result) : DateTimeOffset.Now;
        }

#endregion
#region Alarm
        public void Append(ST_ALARM_DATA alarm)
        {
            var entry = new ST_ALARM_HISTORY_ENTRY(
                alarm.OccurredAt, 
                alarm.Code, 
                alarm.Id, 
                alarm.Group, 
                alarm.Severity == EN_ALARM_LEVEL.Critical ? "HEAVY" : alarm.Severity == EN_ALARM_LEVEL.Warning ? "LIGHT" : "INFO", 
                alarm.Message, 
                alarm.RecoveryAction, 
                alarm.CimAddress
            );
            var path = AlarmLogPath(alarm.OccurredAt.LocalDateTime.Date);
            var line = JsonSerializer.Serialize(entry, AlarmJson) + Environment.NewLine;
            lock (GetFileLock(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                System.IO.File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }

        public ST_ALARM_HISTORY_RESULT Read(DateTime from, DateTime through, CancellationToken token = default)
        {
            from = from.Date;
            through = through.Date;
            if (from > through)
                throw new ArgumentException("시작 날짜는 종료 날짜보다 늦을 수 없습니다.");
            var rows = new List<ST_ALARM_HISTORY_ENTRY>();
            var warnings = new List<string>();
            for (var date = from;; date = date.AddDays(1))
            {
                token.ThrowIfCancellationRequested();
                var path = AlarmLogPath(date);
                try
                {
                    string[] lines;
                    lock (GetFileLock(path))
                        lines = System.IO.File.Exists(path) ? System.IO.File.ReadAllLines(path, Encoding.UTF8) : [];
                    var invalidLines = 0;
                    foreach (var line in lines)
                    {
                        token.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(line))
                            continue;
                        try
                        {
                            var row = JsonSerializer.Deserialize<ST_ALARM_HISTORY_ENTRY>(line, AlarmJson);
                            if (row is null || row.OccurredAt == default || row.Code < 0 || row.Cause is null || row.Group is null)
                            {
                                invalidLines++;
                                continue;
                            }

                            if (row.OccurredAt.LocalDateTime.Date >= from && row.OccurredAt.LocalDateTime.Date <= through)
                                rows.Add(row);
                        }
                        catch (JsonException)
                        {
                            invalidLines++;
                        }
                    }

                    if (invalidLines > 0)
                        warnings.Add($"{date:yyyy-MM-dd}: 손상된 로그 {invalidLines}건 제외");
                }
                catch (Exception exception)when (exception is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"{date:yyyy-MM-dd}: {exception.Message}");
                }

                if (date == through)
                    break;
            }

            return new(rows.OrderByDescending(row => row.OccurredAt).ThenBy(row => row.Code).ToArray(), string.Join(Environment.NewLine, warnings));
        }

        private string AlarmLogPath(DateTime date)
        {
            var day = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            return Path.Combine(_alarmLogRoot, day, $"Alarm_{day}.jsonl");
        }

#endregion
#region Sequence
        private sealed record SequenceEntry(DateTime At, string Line, TaskCompletionSource? Flush = null);
        public string SequenceLogError => Volatile.Read(ref _sequenceLogError);

        /// <summary>Enqueues diagnostics only. No device or file I/O runs on the sequence caller.</summary>
        public void WriteSequence(string level, string source, string detail, string context = "")
        {
            lock (_sequenceLogLock)
            {
                if (_sequenceStopped)
                    return;
                ST_SEQUENCE_LOG_TEXT? entry;
                try
                {
                    entry = _sequenceText.Format(source, detail, context);
                }
                catch (Exception error)
                {
                    SequenceWriteFailed(error);
                    entry = new(source, detail);
                }

                if (entry is null)
                    return;
                EnsureSequenceWriter();
                var now = DateTime.Now;
                static string Field(string? value) => (value ?? "").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", " ");
                var message = level.Equals("ERROR", StringComparison.OrdinalIgnoreCase) && !entry.Message.Contains("실패") ? "오류: " + entry.Message : entry.Message;
                var line = $"{now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} " + $"[GLASSID:{Field(entry.GlassId)}][{Field(entry.Category)}][{Field(message)}]";
                // Bounded memory and nonblocking producers. Overflow is a diagnostic fault, never a motion retry.
                if (!_sequenceQueue!.Writer.TryWrite(new(now, line)))
                    SequenceWriteFailed(new IOException("Sequence log queue is full (16384 events). This event could not be queued."));
            }
        }

        private void EnsureSequenceWriter()
        {
            if (_sequenceQueue is not null)
                return;
            _sequenceQueue = Channel.CreateBounded<SequenceEntry>(new BoundedChannelOptions(16384) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
            _sequenceWriter = Task.Run(() => WriteSequenceQueue(_sequenceQueue.Reader));
        }

        public async Task FlushSequenceAsync(CancellationToken token = default)
        {
            Task? stopped = null;
            TaskCompletionSource? barrier = null;
            Channel<SequenceEntry>? queue = null;
            lock (_sequenceLogLock)
            {
                if (_sequenceStopped)
                    stopped = _sequenceWriter;
                else if (_sequenceQueue is not null)
                {
                    queue = _sequenceQueue;
                    barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            if (stopped is not null)
                await stopped.WaitAsync(token).ConfigureAwait(false);
            if (queue is not null)
            {
                try
                {
                    await queue.Writer.WriteAsync(new(default, "", barrier), token).ConfigureAwait(false);
                    await barrier!.Task.WaitAsync(token).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    if (_sequenceWriter is { } writer)
                        await writer.WaitAsync(token).ConfigureAwait(false);
                }
            }

            if (SequenceLogError.Length != 0)
                throw new IOException(SequenceLogError);
        }

        public async Task StopSequenceLoggingAsync(CancellationToken token = default)
        {
            Task? writer;
            lock (_sequenceLogLock)
            {
                _sequenceStopped = true;
                _sequenceQueue?.Writer.TryComplete();
                writer = _sequenceWriter;
            }

            if (writer is not null)
                await writer.WaitAsync(token).ConfigureAwait(false);
            if (SequenceLogError.Length != 0)
                throw new IOException(SequenceLogError);
        }

        private async Task WriteSequenceQueue(ChannelReader<SequenceEntry> reader)
        {
            var batch = new List<SequenceEntry>(256);
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < 256 && reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                    if (entry.Flush is not null)
                        break;
                }

                try
                {
                    foreach (var day in batch.Where(entry => entry.Flush is null).GroupBy(entry => entry.At.ToString("yyyyMMdd", CultureInfo.InvariantCulture)))
                    {
                        var directory = Path.Combine(LogDirectory, "Sequence", day.Key);
                        var path = Path.Combine(directory, $"Sequence_{day.Key}_Trace.txt");
                        // This worker alone owns Sequence files. Do not contend with synchronous UI/interface log writers.
                        Directory.CreateDirectory(directory);
                        await File.AppendAllTextAsync(path, string.Join(Environment.NewLine, day.Select(entry => entry.Line)) + Environment.NewLine, new UTF8Encoding(true)).ConfigureAwait(false);
                    }
                }
                catch (Exception error)
                {
                    SequenceWriteFailed(error);
                }

                foreach (var entry in batch)
                    entry.Flush?.TrySetResult();
            }
        }

        private void SequenceWriteFailed(Exception error)
        {
            var message = $"Sequence log write failed: {error}";
            var previous = Interlocked.Exchange(ref _sequenceLogError, message);
            if (previous != message)
                try
                {
                    Trace.TraceError(message);
                }
                catch
                { /* Diagnostic listeners cannot affect control. */
                }
        }
#endregion
    }

    /// <summary>Limits log browsing and reading to ordinary files below the configured Log directory.</summary>
    public sealed class CLogFileScope
    {
        public CLogFileScope(string rootDirectory) => RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        public string RootDirectory { get; }

        public string ResolveDirectory(string path)
        {
            var fullPath = ResolvePath(path);
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException("로그 폴더를 찾을 수 없습니다.");
            return fullPath;
        }

        public string ResolveFile(string path)
        {
            var fullPath = ResolvePath(path);
            if (!IsLogFile(fullPath) || Directory.Exists(fullPath))
                throw new ArgumentException("TXT, LOG, CSV 로그 파일만 열 수 있습니다.");
            return fullPath;
        }

        public static bool IsLogFile(string path) => Path.GetExtension(path).ToLowerInvariant()is ".txt" or ".log" or ".csv";
        public static bool IsAlarm(string name) => name.Equals("Alarm", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Alarm_", StringComparison.OrdinalIgnoreCase);
        private string ResolvePath(string path)
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, RootDirectory));
            var relative = Path.GetRelativePath(RootDirectory, fullPath);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("Log 폴더 밖으로 이동하거나 파일을 열 수 없습니다.");
            var parts = relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(IsAlarm))
                throw new ArgumentException("알람 로그는 ALARM 메뉴의 HISTORY/TREND에서 확인해 주세요.");
            // Do not let a junction/symlink under Log redirect browsing or a later page read outside it.
            var current = RootDirectory;
            RejectLink(current);
            foreach (var part in parts)
            {
                current = Path.Combine(current, part);
                RejectLink(current);
            }

            return fullPath;
        }

        private static void RejectLink(string path)
        {
            if ((System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("연결된 폴더나 바로가기 파일은 로그 뷰어에서 열 수 없습니다.");
        }
    }

    internal sealed record ST_SEQUENCE_LOG_TEXT(string Category, string Message, string GlassId = "미확정");
    /// <summary>Operator-facing sequence trace. Consumes existing events; never reads a device or changes sequence state.</summary>
    internal sealed class CSequenceLogText
    {
        private static readonly Regex IdPattern = new(@"\b(?:HANDSHAKE|PROCESS|IOF|IPS)_[A-Z0-9_]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ActionPattern = new(@"^ACTION_(BEGIN|DONE|FAILED|CANCELED) ([A-Z0-9_]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ExceptionPattern = new(@"(?:System\.)[\w.]*Exception: ([^\r\n]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RunIdPattern = new(@"\bRunId=\d+[.;]?\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private readonly Dictionary<string, string> _addresses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _signals = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _writes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _glassByCycle = new(StringComparer.Ordinal);
        private readonly Queue<string> _glassCycles = new();
        private string _reviewCycle = "", _reviewGlass = "";
        private static readonly Dictionary<string, (string Category, string Name)> ActionNames = new()
        {
            ["DISTORTION_PREPARE"] = ("Distortion", "측정 조건 준비·전송"),
            ["DISTORTION_LOAD"] = ("Distortion", "AK·DK 결과 취득"),
            ["DISTORTION_CALCULATE"] = ("Distortion", "라그랑주 계산·허용오차 확인"),
            ["DISTORTION_SAVE"] = ("Distortion", "이번 Glass 보정 저장"),
            ["DISTORTION_SIGNALS_CLEAR"] = ("Distortion", "신호 초기화"),
            ["DOREADY_DATA_PREPARE"] = ("DoReady", "레시피·좌표 데이터 준비"),
            ["DOREADY_SCRIPT_BUILD_UPLOAD"] = ("DoReady", "스크립트 생성·업로드"),
            ["DOREADY_SIGNALS_CLEAR"] = ("DoReady", "신호 초기화"),
            ["PROCESS_READY_POSITION"] = ("Process", "Stage Y 준비 위치 확인"),
            ["PROCESS_MOF_ZERO"] = ("Process", "MOF Zero Set"),
            ["PROCESS_TASK_RUN"] = ("Process", "Task RUN 및 엔코더 대기 확인"),
            ["PROCESS_MACHINING_INSPECTION"] = ("Process", "가공 완료 확인"),
            ["PROCESS_RESULTS"] = ("Process", "가공 결과 저장"),
            ["PROCESS_SIGNALS_CLEAR"] = ("Process", "신호 초기화"),
            ["PROCESS_ZERO_PREPARE"] = ("0선방어", "촬영 준비"),
            ["PROCESS_ZERO_MEASURE"] = ("0선방어", "검사"),
            ["PROCESS_ZERO_RESULTS"] = ("0선방어", "검사 결과 저장"),
            ["REVIEW_PLAN"] = ("Review", "검사 계획 준비"),
            ["REVIEW_MEASURE"] = ("Review", "검사"),
            ["REVIEW_RESULTS"] = ("Review", "검사 결과 확인·보정 저장"),
            ["REVIEW_SIGNALS_CLEAR"] = ("Review", "신호 초기화"),
            ["TPM_PREPARE"] = ("TPM", "파워 측정 조건 준비"),
            ["TPM_MEASURE"] = ("TPM", "ATT 이동 및 파워 측정"),
            ["TPM_RESULTS"] = ("TPM", "파워 측정 결과 저장"),
            ["TPM_SIGNALS_CLEAR"] = ("TPM", "신호 초기화"),
            ["MOVE_TARGETS"] = ("Process Move", "이동 목표 설정·이동"),
            ["MOVE_SETTLE"] = ("Process Move", "도착·안정화 확인"),
            ["MOVE_RELEASE"] = ("Process Move", "이동 신호 초기화"),
            ["MOVE_XREADY"] = ("Process Move", "Review X 준비 완료 확인"),
            ["IOF_PLAN"] = ("IOF", "촬영 좌표 전송·준비 확인"),
            ["IOF_SIGNALS_CLEAR"] = ("IOF", "신호 초기화")
        };
        public ST_SEQUENCE_LOG_TEXT? Format(string source, string detail, string context)
        {
            var cycle = Field(context, "Cycle");
            if (cycle.Length == 0)
                cycle = Field(detail, "Cycle");
            if (detail.Contains("AUTO_MONITOR_START", StringComparison.Ordinal))
                _reviewCycle = _reviewGlass = "";
            if (detail.Contains("CYCLE_ACCEPTED Kind=REVIEW", StringComparison.Ordinal))
            {
                _reviewCycle = cycle;
                _reviewGlass = "";
            }

            var glass = Field(context, "Glass");
            if (glass.Length == 0)
                glass = Field(detail, "Glass");
            if (source == "REVIEW_AUTO_REVIEW_PLAN")
            {
                var separator = detail.IndexOf(" / ", StringComparison.Ordinal);
                if (separator > 0)
                {
                    _reviewGlass = detail[..separator].Trim();
                    glass = _reviewGlass;
                    RememberGlass(_reviewCycle, glass);
                }
            }

            RememberGlass(cycle, glass);
            var entry = FormatCore(source, detail, context);
            if (entry is null)
                return null;
            if (glass.Length == 0 && cycle.Length > 0)
                glass = _glassByCycle.GetValueOrDefault(cycle, "");
            if (glass.Length == 0 && entry.Category == "Review")
                glass = _reviewGlass;
            if (detail.Contains("AUTO_MONITOR_START", StringComparison.Ordinal))
                glass = "";
            var result = entry with
            {
                GlassId = glass.Length == 0 ? "미확정" : glass
            };
            // A Review request can arrive during another glass's Process. Until Review captures its
            // own input, an observed Stage Review signal must not inherit the Process glass ID.
            if (source == "STAGE_SEQUENCE" && entry.Category == "Review" && _reviewCycle.Length == 0 && (detail.Contains("SIGNAL_INITIAL") || detail.Contains("SIGNAL_CHANGED")))
                result = result with
                {
                    GlassId = "미확정"
                };
            if (detail.Contains("CYCLE_COMPLETE Kind=REVIEW", StringComparison.Ordinal))
                _reviewCycle = _reviewGlass = "";
            return result;
        }

        private void RememberGlass(string cycle, string glass)
        {
            if (cycle.Length == 0 || glass.Length == 0)
                return;
            if (!_glassByCycle.ContainsKey(cycle))
                _glassCycles.Enqueue(cycle);
            _glassByCycle[cycle] = glass;
            while (_glassCycles.Count > 128)
                _glassByCycle.Remove(_glassCycles.Dequeue());
        }

        private ST_SEQUENCE_LOG_TEXT? FormatCore(string source, string detail, string context)
        {
            var text = detail.Trim();
            if (text.StartsWith("Cycle=", StringComparison.Ordinal))
            {
                var end = text.IndexOf(' ');
                text = end < 0 ? "" : text[(end + 1)..].TrimStart();
            }

            var id = IdPattern.Match(text).Value;
            var address = Field(text, "Address");
            if (id.Length > 0 && address.Length > 0)
                _addresses[id] = address;
            if (text.StartsWith("OUTPUT_REQUEST") && id.Length > 0)
                _writes[id] = Field(text, "Logical");
            if (source == "STAGE_SEQUENCE")
            {
                var action = ActionPattern.Match(text);
                if (action.Success)
                {
                    var name = ActionNames.GetValueOrDefault(action.Groups[2].Value, ("Sequence", action.Groups[2].Value.Replace('_', ' ')));
                    var phase = action.Groups[1].Value switch
                    {
                        "BEGIN" => "시작",
                        "DONE" => "완료",
                        "CANCELED" => "중지",
                        _ => "실패"
                    };
                    return new(name.Item1, name.Item2 + " " + phase + Duration(text) + (phase is "실패" or "중지" ? ": " + Reason(text) : ""));
                }

                if (text.StartsWith("OUTPUT_SENT "))
                    return Signal(id, Field(text, "Logical"), "");
                if (text.StartsWith("OUTPUT_CONFIRMED "))
                    return null;
                if (text.StartsWith("OUTPUT_FAILED "))
                    return Signal(id, Field(text, "Logical"), "확인 실패: " + Reason(text));
                if (text.StartsWith("WAIT_BEGIN "))
                    return id.StartsWith("HANDSHAKE_STG_") && _signals.GetValueOrDefault(id) != Field(text, "Expected") ? Signal(id, Field(text, "Expected"), "대기") : null;
                if (text.StartsWith("WAIT_FAILED ") || text.StartsWith("WAIT_CANCELED "))
                    return Signal(id, Field(text, "Expected"), "대기 실패: " + Reason(text));
                if (text.StartsWith("SIGNAL_INITIAL ") || text.StartsWith("SIGNAL_CHANGED "))
                {
                    if (text.Contains("MELSEC_CONNECTED"))
                        return new("Sequence", text.EndsWith("True") ? "MELSEC 연결 확인" : "MELSEC 연결 끊김");
                    // Process outputs are recorded after readback. Initial OFF and pending caches add no operational event.
                    var state = Field(text, "State");
                    if (id.StartsWith("HANDSHAKE_STG_"))
                        _signals[id] = state == "Valid" ? Field(text, "Value") : state;
                    if (!id.StartsWith("HANDSHAKE_STG_") || text.StartsWith("SIGNAL_INITIAL ") && Field(text, "Value") == "0")
                        return null;
                    if (state == "Pending")
                        return null;
                    return state == "Valid" ? Signal(id, Field(text, "Value"), "확인") : Signal(id, "", $"신호 확인 불가 (상태: {state}, 원인: {Field(text, "Error")})");
                }

                if (text.StartsWith("CYCLE_ACCEPTED"))
                    return new(text.Contains("Kind=DISTORTION") ? "Distortion" : text.Contains("Kind=REVIEW") ? "Review" : text.Contains("Kind=TPM") ? "TPM" : text.Contains("Kind=PROCESS_ONLY") ? "Process" : "DoReady", "Stage PC 요청 접수");
                if (text.StartsWith("CYCLE_COMPLETE"))
                    return new(text.Contains("Kind=DISTORTION") ? "Distortion" : text.Contains("Kind=REVIEW") ? "Review" : text.Contains("Kind=TPM") ? "TPM" : "Process", "시나리오 완료");
                if (text.StartsWith("AUTO_MONITOR_START"))
                    return new("Sequence", "AUTO 시퀀스 감시 시작");
                if (text.StartsWith("AUTO_MONITOR_STOP_REQUESTED"))
                    return new("Sequence", "AUTO 시퀀스 중지 요청");
                if (text.StartsWith("CLEANUP_OUTPUT_SENT"))
                    return Signal(id, "0", "전송 완료 (중지 정리)");
                if (text.StartsWith("CLEANUP_BEGIN"))
                    return new("Sequence", "중지 후 Task·출력 신호 정리 시작");
                if (text.StartsWith("CLEANUP_END"))
                    return new("Sequence", "중지 후 Task·출력 신호 정리 " + (Field(text, "Success") == "True" ? "완료" : "실패"));
                if (text.StartsWith("SEQUENCE_FAILED") || text.StartsWith("ERROR:"))
                    return new("Sequence", "시퀀스 실패: " + Reason(text));
                if (text.StartsWith("STOPPED:"))
                    return new("Sequence", "AUTO 시퀀스 중지 완료");
                if (text.StartsWith("STOPPING:"))
                    return new("Sequence", "장치 동작 중지 완료 대기");
                if (text.StartsWith("WAIT_REQUEST:") || text.StartsWith("WAIT_DOREADY:"))
                    return new("Sequence", "Stage PC 요청 대기");
                // Request/send/readback polling and UI-state diagnostics describe the same events already recorded above.
                return null;
            }

            if (source == "DISTORTION") return new("Distortion", text);
            if (text.StartsWith("OUTPUT_REQUEST"))
                return null; // Route was remembered above for the confirmed event.
            if (text.StartsWith("OUTPUT_SENT") || text.StartsWith("OUTPUT_UNCHANGED"))
                return null;
            if (source is "STAGE_DATA" or "PROTECT_WINDOW")
            {
                if (text.Contains("confirmed from"))
                    return Data(id, NumberAfterId(text, id), "확인");
                if (text.StartsWith("OUTPUT_FAILED"))
                    return Data(id, "", "전송 실패: " + Reason(text));
                if (text.StartsWith("USAGE_CHECK"))
                    return new("Protect Window", $"사용매수 확인: {Field(text, "Current")} / {Field(text, "Maximum")}매 · " + (Field(text, "ThresholdReached") == "True" ? "교체 요청 조건 도달" : "교체 요청 조건 미도달"));
                if (text.Contains("Current Process Count="))
                    return new("Protect Window", "가공 완료 · 현재 사용매수 " + Field(text, "Current Process Count") + "매 저장");
                if (text.StartsWith("EXCHANGE_REQUEST"))
                    return new("Protect Window", "교체 요청 시작 · 레시피 " + Field(text, "Recipe"));
                if (text.StartsWith("EXCHANGE_SIGNALS"))
                    return new("Protect Window", $"교체 요청 {OnOff(Field(text, "Request"))} / 교체 완료 {OnOff(Field(text, "Complete"))}");
                if (text.StartsWith("EXCHANGE_COMPLETE_ACCEPTED"))
                    return new("Protect Window", "Stage PC 교체 완료 확인 · Tray Count 수신 시작");
                if (text.StartsWith("TRAY_COUNT_RECEIVED"))
                    return new("Protect Window", "Tray Count 수신: " + Field(text, "Count") + "매");
                if (text.StartsWith("TRAY_COUNT_PERSISTED"))
                    return new("Protect Window", "현재 사용매수 저장 완료: " + Field(text, "Count") + "매");
                if (text.StartsWith("Stage replacement complete:"))
                    return new("Protect Window", "교체 완료 처리 완료 · 현재 사용매수 저장·전송, Process PC 교체 요청 OFF");
                if (text.StartsWith("EXCHANGE_RECOVERY_REQUEST"))
                    return new("Protect Window", "유지 중인 교체 요청 확인 · 레시피 " + Field(text, "Recipe"));
            }

            if (source == "SCAN_PC")
                return new("DoReady", $"가공 데이터 준비 완료 · Recipe: {Field(context, "Recipe")}");
            if (source == "PARSER")
                return new("DoReady", text.Replace("Head parameter parsed", "헤드 파라미터 준비 완료").Replace("heads", "Head").Replace("shots", "Shot"));
            if (source == "SCRIPT")
                return new(text.StartsWith("Process 단독") ? "Process" : "DoReady", text.Replace(" generated in ", " 생성 완료 · 저장 경로: ").Replace(" shots", " Shot"));
            if (source == "REVIEW_OFFSET")
                return new("DoReady", text.Replace("Loaded latest Review Offset file:", "리뷰 보정 파일 불러오기 완료:").Replace(" rows", "개 Shot"));
            if (source == "A1_UPLOAD")
                return new("DoReady", text.Replace("Upload requested.", "스크립트 업로드 시작:").Replace("Upload completed.", "스크립트 업로드 완료:").Replace("Automation1 #", "컨트롤러 "));
            if (source is "A1_RUN" or "MOF_READY" or "MOF_ZERO" or "STAGE_PROCESS" or "STAGE_READY")
                return new("Process", ProcessText(text));
            if (source == "STAGE_MOVE_IOF")
                return CommonFlow(text);
            if (source == "FINE_PROGRESS")
            {
                var state = text.IndexOf("State=", StringComparison.Ordinal);
                var start = state < 0 ? -1 : text.IndexOf("; ", state);
                return new("Review", start < 0 ? text : text[(start + 2)..]);
            }

            if (source.StartsWith("REVIEW_"))
            {
                if (source == "REVIEW_AUTO_REVIEW_PLAN")
                {
                    var separator = text.IndexOf(" / ", StringComparison.Ordinal);
                    return new("Review", "검사 계획 확정 · Recipe: " + (separator < 0 ? text : text[(separator + 3)..]));
                }

                if (source == "REVIEW_FINE_NG_ATTEMPT")
                {
                    var ng = text.IndexOf("NG=", StringComparison.Ordinal);
                    return new("Review", $"NG 발생 · 검사 구간 {Field(text, "Line")} / 시도 {Field(text, "Attempt")} · " + (ng < 0 ? text : text[(ng + 3)..]).Replace("RecvX=", "수신 X=").Replace("RecvY=", "수신 Y="));
                }

                var label = source switch
                {
                    "REVIEW_AUTO_REVIEW_PLAN" => "검사 계획 확정: ",
                    "REVIEW_AUTO_REVIEW_OFFSET_SAVED" => "FINE 보정 저장 완료: ",
                    "REVIEW_AUTO_REVIEW_NG" => "검사 NG · ",
                    "REVIEW_FINE_RESULT_SAVED" => "검사 결과 저장 완료: ",
                    "REVIEW_FINE_RESULT_SAVE_ERROR" => "검사 결과 저장 실패: ",
                    "REVIEW_FINE_NG_ATTEMPT" => "NG 측정 결과: ",
                    _ => ""
                };
                return new("Review", label + text);
            }

            if (source.StartsWith("ZERO_LINE"))
                return new("0선방어", text);
            return new(source, text);
        }

        private ST_SEQUENCE_LOG_TEXT? CommonFlow(string text)
        {
            var phaseStart = text.IndexOf("; ", text.IndexOf("MELSEC_", StringComparison.Ordinal));
            if (phaseStart < 0)
                return new("Process Move", text);
            var phaseEnd = text.IndexOf(';', phaseStart + 2);
            if (phaseEnd < 0)
                return new("Process Move", text);
            var phase = text[(phaseStart + 2)..phaseEnd];
            var id = IdPattern.Match(text).Value;
            var address = Field(text, "Address");
            if (id.Length > 0 && address.Length > 0)
                _addresses[id] = address;
            if (phase == "OUTPUT_REQUEST")
            {
                _writes[id] = Field(text, "Logical");
                return null;
            }

            if (phase == "OUTPUT_SENT")
                return id.StartsWith("HANDSHAKE_PRC_") ? Signal(id, _writes.GetValueOrDefault(id, ""), "") : null;
            if (phase.StartsWith("HANDSHAKE_PRC_"))
                return null;
            if (phase.StartsWith("HANDSHAKE_STG_"))
                return Signal(phase, Field(text, "Value"), "확인");
            if (phase == "SIGNAL_OBSERVED" && id.StartsWith("HANDSHAKE_STG_"))
                return Signal(id, Field(text, "Value"), "확인");
            if (phase == "SIGNAL_OBSERVED" && id.StartsWith("IPS_REVIEW_CONTROL_MODE_"))
                return new("Process Move", $"Stage PC Review {id[^1]} 이동 허가 {OnOff(Field(text, "Value"))} 확인" + Address(id));
            if (phase.Contains(':'))
            {
                var parts = phase.Split(':');
                if (ActionNames.TryGetValue(parts[0], out var name))
                    return new(name.Category, name.Name + " " + (parts[1] switch
                    {
                        "RUNNING" => "시작",
                        "DONE" => "완료",
                        "CANCELED" => "중지",
                        _ => "실패"
                    }) + Duration(text));
            }

            if (phase is "MOVE_PLAN")
                return new("Process Move", $"이동 목표: X {Field(text, "TargetX")} / Y {Field(text, "TargetY")} mm · 속도 X {Field(text, "SpeedX")} / Y {Field(text, "SpeedY")} mm/sec");
            if (phase == "MOVE_ARRIVAL_CONFIRMED")
                return new("Process Move", "목표 위치 도착·BUSY OFF 확인");
            if (phase == "IOF_PLAN_CAPTURED")
                return new("IOF", "촬영 계획 준비: " + Field(text, "Count") + "개");
            if (phase == "IOF_COORDINATES_READY")
                return new("IOF", "촬영 좌표 전송 확인");
            if (phase == "ZERO_LINE_SEGMENT_COMPLETE")
                return new("0선방어", "촬영 구간 완료: " + Field(text, "Value"));
            if (phase.Contains("FAILED") || phase.Contains("ERROR"))
                return new("Process Move", "동작 실패: " + Reason(text));
            return null;
        }

        private ST_SEQUENCE_LOG_TEXT Signal(string id, string value, string verb)
        {
            var process = id.StartsWith("HANDSHAKE_PRC_");
            var tail = id.Replace("HANDSHAKE_PRC_", "").Replace("HANDSHAKE_STG_", "");
            var category = tail.StartsWith("DISTORTION_") ? "Distortion" : tail.StartsWith("DOREADY_") ? "DoReady" : tail.StartsWith("PROCESS_") ? "Process" : tail.StartsWith("TPM_") ? "TPM" : "Review";
            var name = tail[(tail.IndexOf('_') + 1)..].Replace('_', ' ');
            if (tail.Contains("SDC") || tail is "REVIEW_MOVE_START" or "REVIEW_GENERAL_RUNNING")
            {
                category = "Process Move";
                name = tail switch
                {
                    "REVIEW_ABLE_SDC" => "이동 허가",
                    "REVIEW_COMPLETE_SDC" => "도착 완료",
                    "REVIEW_MOVE_START" => "이동 START",
                    _ => "이동 응답"
                };
            }

            if (tail.StartsWith("IOF_"))
            {
                category = "IOF";
                name = tail[4..].Replace('_', ' ');
            }

            return new(category, $"{(process ? "Process PC" : "Stage PC")} {name} {OnOff(value)} {verb}".Replace("  ", " ").TrimEnd() + Address(id));
        }

        private ST_SEQUENCE_LOG_TEXT Data(string id, string value, string verb)
        {
            return id switch
            {
                "PROCESS_STAGE_SPEED" => new("Process", $"Process PC Stage 속도 {value} mm/sec 전송 {verb}" + Address(id)),
                "PROCESS_PROCESS_COMPLETE" => new("Process", $"Process PC 가공 완료 {OnOff(value)} {verb}" + Address(id)),
                "PROCESS_SEQUENCE_PTW_PROCESS_COUNT" => new("Protect Window", $"Process PC 현재 사용매수 {value}매 전송 {verb}" + Address(id)),
                "PROCESS_SEQUENCE_TPD_PTW_EXCHANGE_FLAG" => new("Protect Window", $"Process PC 교체 요청 {OnOff(value)} {verb}" + Address(id)),
                _ => new("Sequence", $"{id} {value} {verb}" + Address(id))};
        }

        private string Address(string id) => _addresses.TryGetValue(id, out var address) ? $" ({address})" : "";
        private static string OnOff(string value) => value switch
        {
            "1" or "True" => "ON",
            "0" or "False" => "OFF",
            "" => "",
            _ => "확인 불가: " + value
        };
        private static string Duration(string text) => Field(text, "ElapsedMs")is { Length: > 0 } ms ? $" (소요 {ms} ms)" : "";
        private static string NumberAfterId(string text, string id) => id.Length == 0 ? "" : Field(text, id);
        private static string Reason(string text)
        {
            var error = ExceptionPattern.Match(text);
            if (error.Success)
                return error.Groups[1].Value;
            return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
        }

        private static string Field(string text, string key)
        {
            var start = text.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return "";
            start += key.Length + 1;
            var end = text.IndexOfAny([';', ',', ' ', '\r', '\n', ']'], start);
            return (end < 0 ? text[start..] : text[start..end]).Trim();
        }

        private static string ProcessText(string text) => RunIdPattern.Replace(text, "").Replace("Readiness slot reset requested.", "MOF 준비값 초기화 시작:").Replace("Readiness slot reset complete.", "MOF 준비값 초기화 완료:").Replace("Stage buffered group started.", "버퍼 Task RUN 명령 전송:").Replace("Task RUN requested.", "Task RUN 시작:").Replace("Task RUN command accepted.", "Task RUN 명령 전송 완료:").Replace("Task STOP requested.", "Task 정지 시작:").Replace("Task STOP confirmed.", "Task 정지 확인:").Replace("Task STOP cleanup failed.", "Task 정지 정리 실패:").Replace("Stopped Task Galvo laser OFF command completed.", "정지된 Task의 Galvo 레이저 OFF 완료:").Replace("All participating Tasks initialized for", "전체 Task 엔코더 대기 확인 ·").Replace("Auxiliary zero check requested. Targets=", "보조 엔코더 0점 확인 대상: ").Replace("Task completed.", "Task 가공 완료:").Replace("Process results saved.", "가공 결과 저장 완료:").Replace("Stage Y Ready.", "Stage Y 준비 위치 확인:").Replace("Current=", "현재=").Replace("Target=", "목표=").Replace("Difference=", "오차=").Replace("Tolerance=", "허용오차=").Replace("Automation1 #", "컨트롤러 ").Replace("Automation=", "컨트롤러=").Replace("Heads=", "Head=").Replace("Shots=", "Shot=");
    }

    public sealed record ST_LOG_TEXT_PAGE(string Text, long NextOffset, long FileLength);
    /// <summary>Reads unmodified log text, either once asynchronously or in legacy bounded pages.</summary>
    public static class CLogTextReader
    {
        public const int PageCharacterLimit = 128 * 1024;
        // A one-shot async read for the field Log Viewer. Cancellation keeps a
        // previously selected large file from replacing the current selection.
        public static Task<ST_LOG_TEXT_PAGE> ReadAllAsync(string path, CancellationToken token = default)
        {
            return ReadAllAsync(path, null, token);
        }

        public static async Task<ST_LOG_TEXT_PAGE> ReadAllAsync(
            string path,
            IProgress<double>? progress,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.Equals("Alarm", StringComparison.OrdinalIgnoreCase)) || Path.GetFileName(fullPath).StartsWith("Alarm_", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("알람 로그는 ALARM 메뉴의 HISTORY/TREND에서 확인해 주세요.");
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = new byte[4];
            var count = await stream.ReadAsync(header.AsMemory(), token).ConfigureAwait(false);
            var (encoding, preamble) = DetectEncoding(header.AsSpan(0, count));
            stream.Position = preamble;
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
            var length = stream.Length;
            var text = new StringBuilder();
            var buffer = new char[64 * 1024];
            var lastReportedPercent = -1;
            progress?.Report(0.0);
            while (true)
            {
                var characters = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (characters == 0)
                    break;

                text.Append(buffer, 0, characters);
                var percent = length == 0
                    ? 100
                    : Math.Clamp((int)Math.Floor(stream.Position * 100.0 / length), 0, 99);
                if (percent != lastReportedPercent)
                {
                    lastReportedPercent = percent;
                    progress?.Report(percent);
                }
            }

            progress?.Report(100.0);
            return new(text.ToString(), length, length);
        }

        public static ST_LOG_TEXT_PAGE ReadPage(string path, long offset = 0)
        {
            var fullPath = Path.GetFullPath(path);
            if (fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.Equals("Alarm", StringComparison.OrdinalIgnoreCase)) || Path.GetFileName(fullPath).StartsWith("Alarm_", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("알람 로그는 ALARM 메뉴의 HISTORY/TREND에서 확인해 주세요.");
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            if (offset < 0 || offset > length)
                throw new IOException("로그 파일 크기가 변경되었습니다. 파일을 다시 열어 주세요.");
            Span<byte> header = stackalloc byte[4];
            var count = stream.Read(header);
            var(encoding, preamble) = DetectEncoding(header[..count]);
            var start = offset == 0 ? preamble : offset;
            stream.Position = start;
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false);
            var buffer = new char[PageCharacterLimit];
            var characters = reader.ReadBlock(buffer, 0, buffer.Length);
            if (characters == buffer.Length)
            {
                // Keep complete lines where possible, and never split a surrogate pair across pages.
                var lastNewline = Array.LastIndexOf(buffer, '\n', characters - 1, characters);
                if (lastNewline >= 0)
                    characters = lastNewline + 1;
                else if (char.IsHighSurrogate(buffer[characters - 1]))
                    characters--;
            }

            var text = new string (buffer, 0, characters);
            var next = start + encoding.GetByteCount(text);
            return new(text, next, length);
        }

        private static (Encoding Encoding, int Preamble) DetectEncoding(ReadOnlySpan<byte> header)
        {
            if (header.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }))
                return (new UTF32Encoding(false, true, true), 4);
            if (header.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
                return (new UTF32Encoding(true, true, true), 4);
            if (header.StartsWith(new byte[] { 0xFF, 0xFE }))
                return (new UnicodeEncoding(false, true, true), 2);
            if (header.StartsWith(new byte[] { 0xFE, 0xFF }))
                return (new UnicodeEncoding(true, true, true), 2);
            if (header.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
                return (new UTF8Encoding(false, true), 3);
            return (new UTF8Encoding(false, true), 0);
        }
    }

    public sealed record ST_PM_HISTORY_ENTRY(
        string SessionId,
        DateTimeOffset LockedAt,
        DateTimeOffset? UnlockedAt,
        string Reason,
        string Operator);

    public static class CProgramOpenLog
    {
        private static string LogDirectory
        {
            get
            {
                return Path.Combine(FindProjectRoot(), "Log", "ProgramOpen");
            }
        }

        public static string LogPath
        {
            get
            {
                return Path.Combine(LogDirectory, $"ProgramOpen_{DateTime.Now:yyyyMMdd}.log");
            }
        }

        public static void Write(string title, Exception exception)
        {
            Write(title, exception.ToString());
        }

        public static void Write(string title, string message)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{title}]{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
            // Startup logging must never become another startup failure.
            }
        }

        private static string FindProjectRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (System.IO.File.Exists(Path.Combine(directory.FullName, "Drilling.sln")) || Directory.Exists(Path.Combine(directory.FullName, "Config")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return AppContext.BaseDirectory;
        }
    }
}
