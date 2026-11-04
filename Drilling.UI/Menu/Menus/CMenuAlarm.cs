namespace Drilling.UI.Menu.Menus
{
    using System.ComponentModel;
    using System.Globalization;
    using System.IO;
    using System.Runtime.CompilerServices;
    using System.Windows;
    using Drilling.Common.Alarm;
    using Drilling.Common.Managers;
    using Drilling.Common.Interface;
    using Drilling.Common.Motion;
    using Drilling.Common.InterLock;
    using Drilling.Common.Station;
    using System.Windows.Media;

    public sealed class CMenuAlarm(CAlarmManager alarmManager, CStationManager stationManager, Action<string> setStatusMessage, Action refreshShellStatus, Func<Task> refreshCurrentScreen) : CMenuBase
    {
        private int _selectedTabIndex;
        public event EventHandler? SelectedTabIndexChanged;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (_selectedTabIndex == value)
                    return;
                _selectedTabIndex = value;
                if (value == 1)
                    HistoryPanel.InitializeOnFirstOpen();
                SelectedTabIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public CAlarmListPanel ListPanel { get; } = new(alarmManager);
        public CAlarmHistoryPanel HistoryPanel { get; } = new(alarmManager.ReadHistory);

        public event EventHandler? LogErrorChanged;
        public string LogError => alarmManager.GetCurrentSnapshot().LogError;

        public override EN_MENU Menu
        {
            get
            {
                return EN_MENU.Alarm;
            }
        }

        public CButtonCommand AlarmResetCommand
        {
            get
            {
                async void ResetAlarmCallback(object? arg0)
                {
                    await ResetAlarmAsync();
                }

                return new(ResetAlarmCallback);
            }
        }

        public CButtonCommand BuzzerOffCommand
        {
            get
            {
                void setStatusMessageCallback(object? arg0)
                {
                    setStatusMessage("Buzzer off command sent.");
                }

                return new(setStatusMessageCallback);
            }
        }

        public IReadOnlyList<ST_DISPLAY_ITEM> CurrentAlarms { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> AlarmDetail { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> RecoveryCommands { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> History { get; private set; } = [];
        public IReadOnlyList<ST_DISPLAY_ITEM> Trend { get; private set; } = [];
        public IReadOnlyList<ST_ALARM_CURRENT_ROW> CurrentAlarmRows { get; private set; } = [];
        public IReadOnlyList<ST_ALARM_DETAIL_ROW> DetailRows { get; private set; } = [];
        public IReadOnlyList<ST_ALARM_HISTORY_ROW> HistoryRows { get; private set; } = [];
        public IReadOnlyList<ST_ALARM_TREND_BAR> TrendBars { get; private set; } = [];
        public IReadOnlyList<ST_ALARM_SUMMARY_ITEM> SummaryItems { get; private set; } = [];

        public override Task<CScreenViewModel> Build(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListPanel.RefreshSnapshot();
            var alarms = alarmManager.CurAlarm;
            global::Drilling.UI.Menu.Menus.ST_DISPLAY_ITEM BuildDisplay(global::Drilling.Common.Alarm.ST_ALARM_DATA item)
            {
                return new ST_DISPLAY_ITEM(item.Code.ToString(), item.Message, item.Severity.ToString());
            }

            var currentAlarms = alarms.Select(BuildDisplay).DefaultIfEmpty(new ST_DISPLAY_ITEM("No Alarm", "Normal")).ToArray();
            var firstAlarm = alarms.FirstOrDefault();
            Apply(
                currentAlarms, 
                [
                new("Code", firstAlarm?.Code.ToString() ?? "-"), 
                new("Severity", firstAlarm?.Severity.ToString() ?? "-"), 
                new("Recovery", firstAlarm?.RecoveryAction ?? "No recovery action required")
            ], 
                [new("Alarm Reset", "Ready"), new("Buzzer Off", "Ready")], 
                [], 
                [], 
                BuildCurrentAlarmRows(alarms), 
                BuildDetailRows(firstAlarm), 
                [], 
                [], 
                []
            );
            OnPropertyChanged(nameof(CurrentAlarmRows));
            LogErrorChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(new CScreenViewModel(EN_MENU.Alarm, "ALARM", "Current alarms, history/trend and alarm list.", [new("Current", alarms.Count.ToString())], [new("Current Alarm", currentAlarms)], alarm: this));
        }

        public async Task ResetAlarmAsync()
        {
            alarmManager.Reset();
            var station = stationManager.Reset();
            var isResetBlocked = station.ProcessStep == EN_PROCESS_STEP.Error;
            setStatusMessage(isResetBlocked ? $"Alarm reset blocked. {station.Result?.Message ?? "Check InterLock state."}" : "Alarm reset completed. Station is ready.");
            refreshShellStatus();
            await refreshCurrentScreen();
        }

        private void Apply(
            IReadOnlyList<ST_DISPLAY_ITEM> currentAlarms, 
            IReadOnlyList<ST_DISPLAY_ITEM> alarmDetail, 
            IReadOnlyList<ST_DISPLAY_ITEM> recoveryCommands, 
            IReadOnlyList<ST_DISPLAY_ITEM> history, 
            IReadOnlyList<ST_DISPLAY_ITEM> trend, 
            IReadOnlyList<ST_ALARM_CURRENT_ROW> currentAlarmRows, 
            IReadOnlyList<ST_ALARM_DETAIL_ROW> detailRows, 
            IReadOnlyList<ST_ALARM_HISTORY_ROW> historyRows, 
            IReadOnlyList<ST_ALARM_TREND_BAR> trendBars, 
            IReadOnlyList<ST_ALARM_SUMMARY_ITEM> summaryItems
        )
        {
            CurrentAlarms = currentAlarms;
            AlarmDetail = alarmDetail;
            RecoveryCommands = recoveryCommands;
            History = history;
            Trend = trend;
            CurrentAlarmRows = currentAlarmRows;
            DetailRows = detailRows;
            HistoryRows = historyRows;
            TrendBars = trendBars;
            SummaryItems = summaryItems;
        }

        private IReadOnlyList<ST_ALARM_CURRENT_ROW> BuildCurrentAlarmRows(IReadOnlyList<ST_ALARM_DATA> alarms)
        {
            var groupsByCode = alarmManager.GetDefinitionsSnapshot().Rows.ToDictionary(row => row.Code, row => row.Group);
            global::Drilling.UI.Menu.Menus.ST_ALARM_CURRENT_ROW BuildAlarm(global::Drilling.Common.Alarm.ST_ALARM_DATA alarm, int index)
            {
                var group = groupsByCode.TryGetValue(alarm.Code, out var definedGroup) ? definedGroup : alarm.Group;
                return new ST_ALARM_CURRENT_ROW(index + 1, alarm.Code.ToString(), FormatSeverity(alarm.Severity), group, alarm.Message, alarm.Message, alarm.RecoveryAction, alarm.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss"));
            }

            return alarms.Select(BuildAlarm).ToArray();
        }

        private static IReadOnlyList<ST_ALARM_DETAIL_ROW> BuildDetailRows(ST_ALARM_DATA? alarm)
        {
            if (alarm is null)
            {
                return[new("Selected Code", "-"), new("Device", "-"), new("Level", "CLEAR", "Ok"), new("Occurred Time", "-"), new("Message", "No active alarm")];
            }

            var severity = FormatSeverity(alarm.Severity);
            return[
                new("Selected Code", alarm.Code.ToString(), SeverityState(alarm.Severity)), 
                new("Device", alarm.Device), 
                new("Level", severity, SeverityState(alarm.Severity)), 
                new("Occurred Time", alarm.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")), 
                new("Message", alarm.Message)
            ];
        }

        private static string FormatSeverity(EN_ALARM_LEVEL severity)
        {
            return severity switch
            {
                EN_ALARM_LEVEL.Warning => "LIGHT",
                EN_ALARM_LEVEL.Critical => "HEAVY",
                _ => "INFO"
            };
        }

        private static string SeverityState(EN_ALARM_LEVEL severity)
        {
            return severity switch
            {
                EN_ALARM_LEVEL.Warning => "Warn",
                EN_ALARM_LEVEL.Critical => "Critical",
                _ => "Accent"
            };
        }
    }

    public sealed class CAlarmHistoryPanel : INotifyPropertyChanged
    {
        private readonly Func<DateTime, DateTime, CancellationToken, ST_ALARM_HISTORY_RESULT> _read;
        private readonly Func<DateTime> _now;
        private DateTime? _from = DateTime.Today;
        private DateTime? _through = DateTime.Today;
        private string _fromTime = "00:00:00";
        private string _throughTime = "00:00:00";
        private bool _hasOpened;
        private bool _isCodeView = true;
        private IReadOnlyList<ST_ALARM_HISTORY_ENTRY> _loadedRows = [];
        private DateTime? _searchedFrom;
        private DateTime? _searchedThrough;
        public CAlarmHistoryPanel(Func<DateTime, DateTime, CancellationToken, ST_ALARM_HISTORY_RESULT> read, Func<DateTime>? now = null)
        {
            _read = read;
            _now = now ?? (() => DateTime.Now);
            SetDefaultRange();
            SearchCommand = new(async _ => await SearchAsync(), _ => !IsBusy);
            Last24HoursCommand = new(async _ => await SearchLast24HoursAsync(), _ => !IsBusy);
            HourViewCommand = new(_ => SelectTrendView(false));
            CodeViewCommand = new(_ => SelectTrendView(true));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public DateTime? From
        {
            get => _from;
            set
            {
                _from = value;
                Notify();
            }
        }

        public DateTime? Through
        {
            get => _through;
            set
            {
                _through = value;
                Notify();
            }
        }

        public string FromTime
        {
            get => _fromTime;
            set
            {
                _fromTime = value;
                Notify();
            }
        }

        public string ThroughTime
        {
            get => _throughTime;
            set
            {
                _throughTime = value;
                Notify();
            }
        }

        public bool IsBusy { get; private set; }
        public bool CanEdit => !IsBusy;
        public bool IsCodeView => _isCodeView;
        public bool IsHourView => !_isCodeView;
        public string Status { get; private set; } = "날짜와 시간을 지정하고 SEARCH를 눌러 주세요.";
        public string TrendCaption { get; private set; } = "코드별 발생 횟수 · 많은 순서";
        public string EmptyMessage { get; private set; } = "조회한 알람 이력이 없습니다.";
        public IReadOnlyList<ST_ALARM_HISTORY_ROW> Rows { get; private set; } = [];
        public IReadOnlyList<ST_ALARM_TREND_COLUMN> Bars { get; private set; } = [];
        public CButtonCommand SearchCommand { get; }
        public CButtonCommand Last24HoursCommand { get; }
        public CButtonCommand HourViewCommand { get; }
        public CButtonCommand CodeViewCommand { get; }

        public void InitializeOnFirstOpen()
        {
            if (_hasOpened)
                return;
            _hasOpened = true;
            SetDefaultRange();
        }

        private void SetDefaultRange()
        {
            var now = _now();
            From = Through = now.Date;
            FromTime = "00:00:00";
            ThroughTime = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private void SelectTrendView(bool codeView)
        {
            if (_isCodeView == codeView || IsBusy)
                return;
            _isCodeView = codeView;
            RefreshTrend();
            foreach (var name in new[]
            {
                nameof(IsCodeView),
                nameof(IsHourView),
                nameof(Bars),
                nameof(TrendCaption)
            }

            )
                Notify(name);
        }

        private void RefreshTrend()
        {
            if (_searchedFrom is not { } from || _searchedThrough is not { } through)
            {
                Bars = [];
                TrendCaption = IsCodeView ? "코드별 발생 횟수 · 많은 순서" : "시간별 발생 횟수 · 1시간 기준";
                return;
            }

            Bars = IsCodeView ? BuildCodeBars(_loadedRows) : BuildHourBars(_loadedRows, from, through);
            var summary = IsCodeView ? $"{Bars.Count:N0}개 코드 · 발생 횟수 내림차순" : "시간별 · 1시간 기준";
            TrendCaption = $"{from:yyyy-MM-dd HH:mm:ss.fff} ~ {through:yyyy-MM-dd HH:mm:ss.fff}\n{summary} · 총 {_loadedRows.Count:N0}건";
        }

        public async Task SearchLast24HoursAsync()
        {
            if (IsBusy)
                return;
            var now = _now();
            // Display and query the same millisecond precision, including across midnight.
            var through = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, now.Kind);
            var from = through.AddHours(-24);
            From = from.Date;
            Through = through.Date;
            FromTime = from.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            ThroughTime = through.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            await SearchAsync();
        }

        public async Task SearchAsync()
        {
            if (IsBusy)
                return;
            if (From is not { } from || Through is not { } through || !TryReadTime(FromTime, out var fromTime) || !TryReadTime(ThroughTime, out var throughTime))
            {
                Status = "날짜와 시간을 올바르게 입력해 주세요. 시간 형식: HH:mm 또는 HH:mm:ss (24시간제).";
                Notify(nameof(Status));
                return;
            }

            from = from.Date.Add(fromTime);
            through = through.Date.Add(throughTime);
            if (from > through)
            {
                Status = "시작 일시는 종료 일시보다 늦을 수 없습니다.";
                Notify(nameof(Status));
                return;
            }

            IsBusy = true;
            Status = "알람 이력을 조회하고 있습니다.";
            Notify(nameof(IsBusy));
            Notify(nameof(CanEdit));
            Notify(nameof(Status));
            SearchCommand.NotifyCanExecuteChanged();
            Last24HoursCommand.NotifyCanExecuteChanged();
            try
            {
                var result = await Task.Run(() =>
                {
                    // The store opens only the relevant daily files. Apply exact times before both grid and chart.
                    var loaded = _read(from.Date, through.Date, CancellationToken.None);
                    return loaded with
                    {
                        Rows = loaded.Rows.Where(row => row.OccurredAt.LocalDateTime >= from && row.OccurredAt.LocalDateTime <= through).OrderByDescending(row => row.OccurredAt).ThenBy(row => row.Code).ToArray()
                    };
                });
                Rows = result.Rows.Select((row, index) => new ST_ALARM_HISTORY_ROW(index + 1, row.OccurredAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"), row.Code.ToString(), row.Level, row.Group, row.Cause, row.Solution)).ToArray();
                _loadedRows = result.Rows;
                _searchedFrom = from;
                _searchedThrough = through;
                RefreshTrend();
                Status = result.Warning.Length == 0 ? $"{Rows.Count:N0}건 조회 완료" : $"{Rows.Count:N0}건 조회 · {result.Warning}";
                EmptyMessage = "해당 기간에 발생한 알람이 없습니다.";
            }
            catch (Exception exception)when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Rows = [];
                Bars = [];
                _loadedRows = [];
                _searchedFrom = _searchedThrough = null;
                Status = $"알람 이력 조회 실패: {exception.Message}";
                EmptyMessage = "알람 이력을 읽지 못했습니다.";
                TrendCaption = "조회 실패";
            }
            finally
            {
                IsBusy = false;
                foreach (var name in new[]
                {
                    nameof(Rows),
                    nameof(Bars),
                    nameof(Status),
                    nameof(EmptyMessage),
                    nameof(TrendCaption),
                    nameof(IsBusy),
                    nameof(CanEdit)
                }

                )
                    Notify(name);
                SearchCommand.NotifyCanExecuteChanged();
                Last24HoursCommand.NotifyCanExecuteChanged();
            }
        }

        private static bool TryReadTime(string value, out TimeSpan time)
        {
            string[] formats = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "H:mm:ss.FFF", "HH:mm:ss.FFF"];
            var valid = DateTime.TryParseExact(value?.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed);
            time = parsed.TimeOfDay;
            return valid;
        }

        private static IReadOnlyList<ST_ALARM_TREND_COLUMN> BuildCodeBars(IReadOnlyList<ST_ALARM_HISTORY_ENTRY> rows)
        {
            if (rows.Count == 0)
                return[];
            var counts = rows.GroupBy(row => row.Code).Select(group => new { Code = group.Key, Cause = group.First().Cause, Count = group.Count() }).OrderByDescending(group => group.Count).ThenBy(group => group.Code).ToArray();
            var maximum = counts[0].Count;
            return counts.Select(group => new ST_ALARM_TREND_COLUMN(
                group.Code.ToString(CultureInfo.InvariantCulture), 
                $"{group.Code} · {group.Cause}\n발생 {group.Count:N0}건", 
                group.Cause, 
                group.Count, 
                new GridLength(group.Count, GridUnitType.Star), 
                new GridLength(maximum - group.Count, GridUnitType.Star)
            )).ToArray();
        }

        private static IReadOnlyList<ST_ALARM_TREND_COLUMN> BuildHourBars(IReadOnlyList<ST_ALARM_HISTORY_ENTRY> rows, DateTime from, DateTime through)
        {
            static DateTime Hour(DateTime value) => value.Date.AddHours(value.Hour);
            var counts = rows.GroupBy(row => Hour(row.OccurredAt.LocalDateTime)).ToDictionary(group => group.Key, group => group.Count());
            var maximum = Math.Max(1, counts.Values.DefaultIfEmpty(0).Max());
            var bars = new List<ST_ALARM_TREND_COLUMN>();
            var end = Hour(through);
            for (var hour = Hour(from);; hour = hour.AddHours(1))
            {
                var count = counts.GetValueOrDefault(hour);
                bars.Add(new(
                    hour.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), 
                    $"{hour:yyyy-MM-dd HH:mm} · {count:N0}건", 
                    "", 
                    count, 
                    new GridLength(count, GridUnitType.Star), 
                    new GridLength(maximum - count, GridUnitType.Star), 
                    IsHourly: true
                ));
                if (hour == end)
                    break;
            }

            return bars;
        }

        private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }

    public sealed record ST_ALARM_TREND_COLUMN(string Label, string Detail, string Cause, int Count, GridLength FilledWidth, GridLength EmptyWidth, bool IsHourly = false);
    public sealed class CAlarmListPanel : INotifyPropertyChanged
    {
        private readonly CAlarmManager _alarms;
        private ST_ALARM_DEFINITION_SNAPSHOT? _displayedSnapshot;
        public CAlarmListPanel(CAlarmManager alarms)
        {
            _alarms = alarms;
            RefreshSnapshot();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public IReadOnlyList<ST_ALARM_LIST_ROW> Rows { get; private set; } = [];
        public string Status { get; private set; } = "";
        public bool HasError { get; private set; }

        public void RefreshSnapshot()
        {
            var snapshot = _alarms.GetDefinitionsSnapshot();
            if (ReferenceEquals(snapshot, _displayedSnapshot))
                return;
            _displayedSnapshot = snapshot;
            Rows = snapshot.Rows.Select((row, index) => new ST_ALARM_LIST_ROW(index + 1, row.Code, row.Group, row.Id, row.Level, row.Cause, row.Solution, row.CimAddress)).ToArray();
            HasError = !string.IsNullOrEmpty(snapshot.Error);
            Status = HasError ? $"알람 목록을 불러오지 못했습니다. {snapshot.Error} 파일 확인 후 프로그램을 다시 시작해 주세요." : Rows.Count == 0 ? "등록된 알람이 없습니다." : "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rows)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
        }
    }

    public sealed record ST_ALARM_LIST_ROW(int No, int Code, string Group, string Id, string Level, string Cause, string Solution, string CimAddress);
    public sealed record ST_ALARM_CURRENT_ROW(int No, string Code, string Level, string Group, string Message, string Cause, string Action, string Time)
    {
        public Brush LevelBrush
        {
            get
            {
                return Level switch
                {
                    "LIGHT" or "WARN" => CStatusBrush.Wait,
                    "HEAVY" or "CRITICAL" or "ERROR" => CStatusBrush.Offline,
                    "INFO" => CStatusBrush.Simul,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }
    }

    public sealed record ST_ALARM_DETAIL_ROW(string Name, string Value, string State = "Normal")
    {
        public Brush ValueBrush
        {
            get
            {
                return State switch
                {
                    "Warn" => CStatusBrush.Wait,
                    "Critical" => CStatusBrush.Offline,
                    "Accent" => CStatusBrush.Simul,
                    "Ok" => CStatusBrush.Online,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }
    }

    public sealed record ST_ALARM_HISTORY_ROW(int No, string Time, string Code, string Level, string Group, string Cause, string Solution)
    {
        public Brush LevelBrush
        {
            get
            {
                return Level switch
                {
                    "LIGHT" or "WARN" => CStatusBrush.Wait,
                    "HEAVY" or "CRITICAL" or "ERROR" => CStatusBrush.Offline,
                    "INFO" => CStatusBrush.Simul,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }
    }

    public sealed record ST_ALARM_TREND_BAR(string Time, double Scanner, double Laser, double Motion, double Total, double TotalY);
    public sealed record ST_ALARM_SUMMARY_ITEM(string Name, string Value, string State = "Normal")
    {
        public Brush ValueBrush
        {
            get
            {
                return State switch
                {
                    "Warn" => CStatusBrush.Wait,
                    "Critical" => CStatusBrush.Offline,
                    "Accent" => CStatusBrush.Simul,
                    "Ok" => CStatusBrush.Online,
                    _ => CStatusBrush.PrimaryText
                };
            }
        }
    }
}
