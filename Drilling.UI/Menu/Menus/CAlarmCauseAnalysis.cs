namespace Drilling.UI.Menu.Menus;

using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Drilling.UI.Menu;

public sealed class CAlarmCauseAnalysisPanel : INotifyPropertyChanged, IDisposable
{
    private const int MaximumRangeSeconds = 3600;
    private readonly CAlarmIncidentLogReader _reader;
    private readonly ST_ALARM_HISTORY_ROW _alarm;
    private CancellationTokenSource? _loadCancellation;
    private string _beforeSeconds = "60";
    private string _afterSeconds = "30";
    private bool _isBusy;
    private string _status = "알람 전후 로그를 불러올 준비가 되었습니다.";
    private string _interval = "";
    private string _cursorTime = "";
    private DateTimeOffset? _cursorOccurredAt;
    private IReadOnlyList<ST_ALARM_ANALYSIS_PANE> _panes = CAlarmIncidentLogReader.EmptyPanes();
    private IReadOnlyList<ST_ALARM_CAUSE_CANDIDATE> _candidates = [];
    private IReadOnlyList<ST_ALARM_TIMELINE_ITEM> _timeline = [];
    private ST_ALARM_FAILURE_SUMMARY _failureSummary;
    private ST_ALARM_ANALYSIS_RESULT? _loadedResult;
    private bool _showStateChangesOnly = true;
    private bool _compareWithReference = true;
    private string _referenceSummary = "비교 기준은 로그를 불러온 뒤 자동으로 찾습니다.";

    public CAlarmCauseAnalysisPanel(string logDirectory, ST_ALARM_HISTORY_ROW alarm)
    {
        _reader = new(logDirectory);
        _alarm = alarm;
        AlarmHeading = $"ALARM {alarm.Code} · {alarm.Level} · {alarm.Group}";
        AlarmCause = alarm.Cause;
        AlarmTime = alarm.OccurredAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        _failureSummary = CAlarmFailureSummaryAnalyzer.CreatePending(alarm);
        LoadCommand = new(async _ => await LoadAsync(), _ => !IsBusy);
        SetCursor(alarm.OccurredAt);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<DateTimeOffset>? AnalysisLoaded;
    public string AlarmHeading { get; }
    public string AlarmCause { get; }
    public string AlarmTime { get; }
    public CButtonCommand LoadCommand { get; }
    public string BeforeSeconds
    {
        get => _beforeSeconds;
        set
        {
            if (_beforeSeconds == value)
                return;
            _beforeSeconds = value;
            Notify();
        }
    }

    public string AfterSeconds
    {
        get => _afterSeconds;
        set
        {
            if (_afterSeconds == value)
                return;
            _afterSeconds = value;
            Notify();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            Notify();
            Notify(nameof(CanEdit));
            LoadCommand.NotifyCanExecuteChanged();
        }
    }
    public bool CanEdit => !IsBusy;

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            Notify();
        }
    }

    public string Interval
    {
        get => _interval;
        private set
        {
            if (_interval == value)
                return;
            _interval = value;
            Notify();
        }
    }

    public string CursorTime
    {
        get => _cursorTime;
        private set
        {
            if (_cursorTime == value)
                return;
            _cursorTime = value;
            Notify();
        }
    }

    public DateTimeOffset? CursorOccurredAt => _cursorOccurredAt;

    public bool ShowStateChangesOnly
    {
        get => _showStateChangesOnly;
        set
        {
            if (_showStateChangesOnly == value)
                return;
            _showStateChangesOnly = value;
            Notify();
            ApplyVisiblePanes();
        }
    }

    public bool CompareWithReference
    {
        get => _compareWithReference;
        set
        {
            if (_compareWithReference == value)
                return;
            _compareWithReference = value;
            Notify();
            ApplyVisiblePanes();
        }
    }

    public bool HasReference => _loadedResult?.Reference.IsAvailable == true;

    public string ReferenceSummary
    {
        get => _referenceSummary;
        private set
        {
            if (_referenceSummary == value)
                return;
            _referenceSummary = value;
            Notify();
        }
    }

    public IReadOnlyList<ST_ALARM_ANALYSIS_PANE> Panes
    {
        get => _panes;
        private set
        {
            _panes = value;
            Notify();
        }
    }

    public IReadOnlyList<ST_ALARM_CAUSE_CANDIDATE> Candidates
    {
        get => _candidates;
        private set
        {
            _candidates = value;
            Notify();
        }
    }

    public IReadOnlyList<ST_ALARM_TIMELINE_ITEM> Timeline
    {
        get => _timeline;
        private set
        {
            _timeline = value;
            Notify();
        }
    }

    public ST_ALARM_FAILURE_SUMMARY FailureSummary
    {
        get => _failureSummary;
        private set
        {
            _failureSummary = value;
            Notify();
        }
    }

    public async Task LoadAsync()
    {
        if (IsBusy)
            return;
        if (!TryReadSeconds(BeforeSeconds, out var before) || !TryReadSeconds(AfterSeconds, out var after))
        {
            Status = $"전후 시간은 0~{MaximumRangeSeconds:N0}초 사이의 정수로 입력해 주세요.";
            return;
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new();
        var token = _loadCancellation.Token;
        var from = _alarm.OccurredAt.AddSeconds(-before);
        var through = _alarm.OccurredAt.AddSeconds(after);
        Interval = $"{from.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} ~ {through.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff}";
        IsBusy = true;
        Status = "선택한 알람 전후의 로그를 분석하고 있습니다.";
        try
        {
            var result = await Task.Run(() => _reader.Read(from, through, _alarm, token), token);
            token.ThrowIfCancellationRequested();
            _loadedResult = result;
            _compareWithReference = result.Reference.IsAvailable;
            Notify(nameof(CompareWithReference));
            Notify(nameof(HasReference));
            ReferenceSummary = result.Reference.Summary;
            ApplyVisiblePanes(false);
            Candidates = result.Candidates;
            FailureSummary = result.FailureSummary;
            Timeline = CAlarmIncidentTimelineBuilder.Build(result.Panes, result.Candidates, _alarm);
            UpdateStatus();
            SetCursor(_alarm.OccurredAt);
            AnalysisLoaded?.Invoke(_alarm.OccurredAt);
        }
        catch (OperationCanceledException)
        {
            Status = "로그 분석을 취소했습니다.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            Panes = CAlarmIncidentLogReader.EmptyPanes("로그를 읽지 못했습니다.");
            Candidates = [];
            FailureSummary = CAlarmFailureSummaryAnalyzer.CreateLoadFailure(_alarm, exception.Message);
            Timeline = [];
            _loadedResult = null;
            ReferenceSummary = "비교 기준을 만들지 못했습니다.";
            Notify(nameof(HasReference));
            Status = $"로그 분석 실패: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    public void SetCursor(DateTimeOffset occurredAt)
    {
        _cursorOccurredAt = occurredAt;
        CursorTime = $"TIME CURSOR · {occurredAt.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} · 각 영역의 가장 가까운 로그";
    }

    private void ApplyVisiblePanes(bool synchronizeCursor = true)
    {
        if (_loadedResult is not { } result)
            return;

        var source = ShowStateChangesOnly ? result.ChangePanes : result.Panes;
        Panes = source.Select(pane =>
        {
            var rows = CompareWithReference
                ? pane.Rows
                : pane.Rows.Select(row => row.IsBaselineDifference
                    ? row with { IsBaselineDifference = false }
                    : row).ToArray();
            var comparison = CompareWithReference && result.Reference.IsAvailable
                ? $" · 기준 대비 신규 {pane.ReferenceDifferenceCount:N0}건"
                : "";
            return pane with { Summary = pane.Summary + comparison, Rows = rows };
        }).ToArray();
        UpdateStatus();
        if (synchronizeCursor)
            AnalysisLoaded?.Invoke(_cursorOccurredAt ?? _alarm.OccurredAt);
    }

    private void UpdateStatus()
    {
        if (_loadedResult is not { } result)
            return;
        var rowCount = Panes.Sum(pane => pane.Rows.Count);
        var mode = ShowStateChangesOnly ? "변화 로그" : "전체 로그";
        var comparison = CompareWithReference && result.Reference.IsAvailable
            ? $" 정상 후보 대비 신규 유형 {result.Reference.DifferenceTypeCount:N0}개."
            : "";
        Status = result.Warnings.Count == 0
            ? $"{mode} {rowCount:N0}건 표시 / 원본 {result.RawRowCount:N0}건. 파일 {result.FileCount:N0}개 확인. 원인 후보 {result.Candidates.Count:N0}건.{comparison}"
            : $"{mode} {rowCount:N0}건 표시 / 원본 {result.RawRowCount:N0}건. 읽기 경고 {result.Warnings.Count:N0}건: {result.Warnings[0]}";
    }

    private static bool TryReadSeconds(string value, out int seconds)
    {
        return int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
            && seconds is >= 0 and <= MaximumRangeSeconds;
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record ST_ALARM_ANALYSIS_LOG_ROW(
    DateTimeOffset OccurredAt,
    string Time,
    string Source,
    string Message,
    bool IsImportant,
    bool IsAlarmMoment,
    bool IsStateChange = false,
    bool IsBaselineDifference = false);

public sealed record ST_ALARM_ANALYSIS_PANE(
    string Key,
    string Title,
    string Summary,
    IReadOnlyList<ST_ALARM_ANALYSIS_LOG_ROW> Rows,
    int ReferenceDifferenceCount = 0);

public sealed record ST_ALARM_CAUSE_CANDIDATE(
    int Rank,
    string Rule,
    string Title,
    string Reason,
    string PaneKey,
    ST_ALARM_ANALYSIS_LOG_ROW Evidence)
{
    public string RankLabel => $"CANDIDATE {Rank}";
    public string EvidenceCaption => $"{Evidence.Time} · {Evidence.Source}";
};

public sealed record ST_ALARM_FAILURE_SUMMARY_ITEM(string Label, string Value);

public sealed record ST_ALARM_FAILURE_SUMMARY(
    string Title,
    string Category,
    string Source,
    string LeadTime,
    string Detail,
    IReadOnlyList<ST_ALARM_FAILURE_SUMMARY_ITEM> Items);

public sealed record ST_ALARM_TIMELINE_ITEM(
    DateTimeOffset OccurredAt,
    string OffsetText,
    string Source,
    string Summary,
    string PaneKey,
    ST_ALARM_ANALYSIS_LOG_ROW? Evidence,
    bool IsAlarm)
{
    public string FullMessage => Evidence?.Message ?? Summary;
}

internal sealed record ST_ALARM_ANALYSIS_RESULT(
    IReadOnlyList<ST_ALARM_ANALYSIS_PANE> Panes,
    IReadOnlyList<ST_ALARM_ANALYSIS_PANE> ChangePanes,
    IReadOnlyList<ST_ALARM_CAUSE_CANDIDATE> Candidates,
    ST_ALARM_FAILURE_SUMMARY FailureSummary,
    int FileCount,
    int RawRowCount,
    ST_ALARM_REFERENCE_INFO Reference,
    IReadOnlyList<string> Warnings);

internal sealed record ST_ALARM_REFERENCE_INFO(
    bool IsAvailable,
    string Summary,
    DateTimeOffset? From,
    DateTimeOffset? Through,
    int RowCount,
    int DifferenceTypeCount);

internal sealed class CAlarmIncidentLogReader
{
    private const int MaximumRowsPerPane = 1200;
    private const int MaximumReferenceSearchWindows = 8;
    private const int MinimumReferenceRows = 10;
    private static readonly string[] PaneOrder = ["PROCESS", "MELSEC", "CONTEXT", "INTERFACE"];
    private static readonly IReadOnlyDictionary<string, string> PaneTitles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PROCESS"] = "PROCESS FLOW · SEQUENCE / STATION",
        ["MELSEC"] = "MELSEC COMMUNICATION",
        ["CONTEXT"] = "UI / RECIPE / SETTING / PRODUCT",
        ["INTERFACE"] = "OTHER INTERFACE COMMUNICATION"
    };
    private static readonly string[] ImportantTerms =
    [
        "ERROR", "FAIL", "FAILED", "NG", "ALARM", "TIMEOUT", "STOP", "BLOCK", "INTERLOCK",
        "DISCONNECT", "EXCEPTION", "HEAVY", "오류", "실패", "중지", "차단"
    ];
    private static readonly string[] TransitionTerms =
    [
        "START", "BEGIN", "COMPLETE", "DONE", "STOP", "RESET", "READY", "RUN", "IDLE",
        "ON", "OFF", "OPEN", "CLOSE", "CONNECT", "DISCONNECT", "ONLINE", "OFFLINE",
        "INTERLOCK", "ALARM", "STATE", "STATUS", "WRITE", "APPLY", "SAVE", "LOAD",
        "시작", "완료", "중지", "초기화", "준비", "도착", "연결", "해제", "저장", "적용", "변경", "클릭"
    ];
    private static readonly string[] DiscreteMelsecIdTerms =
    [
        "STATE", "STATUS", "ALARM", "READY", "START", "COMPLETE", "ABLE", "INTERLOCK",
        "RUN", "STOP", "ONLINE", "OPEN", "CLOSE", "SERVO", "HOME", "LIMIT", "ERROR"
    ];
    private static readonly Regex ComparisonGuidRegex = new(
        @"\b[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ComparisonDateTimeRegex = new(
        @"\b\d{2,4}[-/]\d{1,2}[-/]\d{1,2}[T ]\d{1,2}:\d{2}:\d{2}(?:\.\d+)?(?:[+-]\d{2}:?\d{2})?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ComparisonNumberRegex = new(
        @"(?<![A-Za-z])[-+]?\d+(?:\.\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ComparisonWhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _logRoot;

    public CAlarmIncidentLogReader(string logRoot)
    {
        _logRoot = Path.GetFullPath(logRoot);
    }

    public ST_ALARM_ANALYSIS_RESULT Read(DateTimeOffset from, DateTimeOffset through, ST_ALARM_HISTORY_ROW alarm, CancellationToken token)
    {
        var alarmTime = alarm.OccurredAt;
        if (from > through)
            throw new ArgumentException("로그 분석 시작 시각은 종료 시각보다 늦을 수 없습니다.");
        if (!Directory.Exists(_logRoot))
            throw new DirectoryNotFoundException($"로그 폴더를 찾을 수 없습니다: {_logRoot}");

        var current = ReadRange(from, through, alarmTime, token);
        var referenceRead = TryReadReference(from, through, token);
        var referenceSignatures = referenceRead is null
            ? null
            : BuildComparisonSignatures(referenceRead.Rows);
        var differenceSignatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in PaneOrder)
        {
            var marked = new List<ST_ALARM_ANALYSIS_LOG_ROW>(current.Rows[key].Count);
            foreach (var row in current.Rows[key])
            {
                var signature = BuildComparisonSignature(key, row);
                var isDifference = referenceSignatures is not null && !referenceSignatures.Contains(signature);
                if (isDifference)
                    differenceSignatures.Add(signature);
                marked.Add(row with { IsBaselineDifference = isDifference });
            }
            current.Rows[key] = ClassifyStateChanges(key, marked);
        }

        var rawRowCount = current.Rows.Values.Sum(source => source.Count);
        var panes = PaneOrder.Select(key => BuildPane(key, current.Rows[key], alarmTime, false)).ToArray();
        var changePanes = PaneOrder.Select(key => BuildPane(key, current.Rows[key], alarmTime, true)).ToArray();
        var candidates = CAlarmCauseCandidateAnalyzer.Analyze(panes, alarm);
        var failureSummary = CAlarmFailureSummaryAnalyzer.Analyze(current.Rows, candidates, alarm);
        var reference = BuildReferenceInfo(referenceRead, differenceSignatures.Count);
        return new(panes, changePanes, candidates, failureSummary, current.FileCount, rawRowCount, reference, current.Warnings);
    }

    public static IReadOnlyList<ST_ALARM_ANALYSIS_PANE> EmptyPanes(string summary = "분석 전")
    {
        return PaneOrder.Select(key => new ST_ALARM_ANALYSIS_PANE(key, PaneTitles[key], summary, [])).ToArray();
    }

    private ST_ALARM_ANALYSIS_PANE BuildPane(
        string key,
        List<ST_ALARM_ANALYSIS_LOG_ROW> source,
        DateTimeOffset alarmTime,
        bool stateChangesOnly)
    {
        var rawTotal = source.Count;
        IEnumerable<ST_ALARM_ANALYSIS_LOG_ROW> filtered = stateChangesOnly
            ? source.Where(row => row.IsStateChange)
            : source;
        var displaySource = key.Equals("MELSEC", StringComparison.Ordinal)
            ? CondenseMelsecPollingRows(filtered)
            : filtered.ToArray();
        var displayTotal = displaySource.Count;
        IEnumerable<ST_ALARM_ANALYSIS_LOG_ROW> selected = displaySource;
        if (displayTotal > MaximumRowsPerPane)
        {
            selected = displaySource
                .OrderBy(row => Math.Abs((row.OccurredAt - alarmTime).TotalMilliseconds))
                .Take(MaximumRowsPerPane);
        }

        var rows = selected.OrderBy(row => row.OccurredAt).ThenBy(row => row.Source, StringComparer.OrdinalIgnoreCase).ToArray();
        string summary;
        if (stateChangesOnly)
            summary = $"변화 {rows.Length:N0} / 원본 {rawTotal:N0}건";
        else if (key.Equals("MELSEC", StringComparison.Ordinal) && rawTotal != displayTotal)
            summary = $"표시 {rows.Length:N0} / 원본 {rawTotal:N0}건 · 정상 폴링 ID·1초 묶음";
        else
            summary = displayTotal <= MaximumRowsPerPane
                ? $"{displayTotal:N0}건"
                : $"표시 {rows.Length:N0} / 전체 {displayTotal:N0}건 · 알람 시각 근접순 선별";
        var referenceDifferenceCount = rows.Count(row => row.IsBaselineDifference);
        return new(key, PaneTitles[key], summary, rows, referenceDifferenceCount);
    }

    private RangeReadResult ReadRange(
        DateTimeOffset from,
        DateTimeOffset through,
        DateTimeOffset? alarmTime,
        CancellationToken token)
    {
        var rows = PaneOrder.ToDictionary(key => key, _ => new List<ST_ALARM_ANALYSIS_LOG_ROW>(), StringComparer.Ordinal);
        var warnings = new List<string>();
        var files = EnumerateFiles(from.LocalDateTime.Date, through.LocalDateTime.Date).ToArray();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ReadFile(file, from, through, alarmTime, rows[file.Pane], token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                warnings.Add($"{Path.GetFileName(file.Path)}: {exception.Message}");
            }
        }
        return new(rows, files.Length, warnings);
    }

    private ReferenceReadResult? TryReadReference(
        DateTimeOffset analysisFrom,
        DateTimeOffset analysisThrough,
        CancellationToken token)
    {
        var duration = analysisThrough - analysisFrom;
        if (duration <= TimeSpan.Zero)
            return null;

        var candidateThrough = analysisFrom.AddTicks(-1);
        for (var attempt = 0; attempt < MaximumReferenceSearchWindows; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var candidateFrom = candidateThrough - duration;
            if (!HasAlarmInRange(candidateFrom, candidateThrough, token))
            {
                var read = ReadRange(candidateFrom, candidateThrough, null, token);
                var rowCount = read.Rows.Values.Sum(rows => rows.Count);
                if (rowCount >= MinimumReferenceRows)
                    return new(candidateFrom, candidateThrough, read.Rows, rowCount);
            }
            candidateThrough = candidateFrom.AddTicks(-1);
        }
        return null;
    }

    private bool HasAlarmInRange(DateTimeOffset from, DateTimeOffset through, CancellationToken token)
    {
        for (var date = from.LocalDateTime.Date;; date = date.AddDays(1))
        {
            token.ThrowIfCancellationRequested();
            var day = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var path = Path.Combine(_logRoot, "Alarm", day, $"Alarm_{day}.jsonl");
            if (File.Exists(path))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8, true);
                    while (reader.ReadLine() is { } line)
                    {
                        token.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(line))
                            continue;
                        using var document = JsonDocument.Parse(line);
                        if (!document.RootElement.TryGetProperty("OccurredAt", out var occurredAtElement)
                            || !DateTimeOffset.TryParse(
                                occurredAtElement.GetString(),
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind,
                                out var occurredAt))
                            continue;
                        if (occurredAt > through)
                            break;
                        if (occurredAt >= from)
                            return true;
                    }
                }
                catch (JsonException)
                {
                    return true;
                }
            }
            if (date == through.LocalDateTime.Date)
                break;
        }
        return false;
    }

    private static ST_ALARM_REFERENCE_INFO BuildReferenceInfo(ReferenceReadResult? reference, int differenceTypeCount)
    {
        if (reference is null)
        {
            return new(
                false,
                "정상 후보 구간을 찾지 못했습니다. 비교 강조 없이 현재 로그만 표시합니다.",
                null,
                null,
                0,
                0);
        }

        var summary =
            $"정상 후보 {reference.From.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff} ~ {reference.Through.LocalDateTime:HH:mm:ss.fff}"
            + $" · 알람 없는 이전 동일 길이 구간 · 로그 {reference.RowCount:N0}건 · 이번 구간 신규 유형 {differenceTypeCount:N0}개";
        return new(true, summary, reference.From, reference.Through, reference.RowCount, differenceTypeCount);
    }

    private static HashSet<string> BuildComparisonSignatures(
        IReadOnlyDictionary<string, List<ST_ALARM_ANALYSIS_LOG_ROW>> rows)
    {
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in PaneOrder)
        {
            foreach (var row in rows[key])
                signatures.Add(BuildComparisonSignature(key, row));
        }
        return signatures;
    }

    private static string BuildComparisonSignature(string paneKey, ST_ALARM_ANALYSIS_LOG_ROW row)
    {
        if (paneKey.Equals("MELSEC", StringComparison.Ordinal)
            && TryGetNormalMelsecPollingId(row, out var id))
        {
            var operation = row.Message.Contains("[쓰기]", StringComparison.OrdinalIgnoreCase) ? "WRITE" : "POLL";
            var value = IsDiscreteMelsecSignal(id)
                ? ReadDelimitedField(row.Message, "값=") ?? "-"
                : "ANALOG";
            return $"MELSEC|{id.ToUpperInvariant()}|{operation}|{value.ToUpperInvariant()}";
        }

        var message = ComparisonGuidRegex.Replace(row.Message, "#GUID");
        message = ComparisonDateTimeRegex.Replace(message, "#TIME");
        message = ComparisonNumberRegex.Replace(message, "#");
        message = ComparisonWhitespaceRegex.Replace(message, " ").Trim();
        return $"{paneKey}|{row.Source.ToUpperInvariant()}|{message.ToUpperInvariant()}";
    }

    private static List<ST_ALARM_ANALYSIS_LOG_ROW> ClassifyStateChanges(
        string paneKey,
        IEnumerable<ST_ALARM_ANALYSIS_LOG_ROW> source)
    {
        var result = new List<ST_ALARM_ANALYSIS_LOG_ROW>();
        var previousMelsecValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in source.OrderBy(item => item.OccurredAt))
        {
            // Alarm proximity is a visual emphasis only. Treating every row near the
            // alarm as a transition keeps high-frequency analog polling in change mode.
            var isChange = row.IsImportant || row.IsBaselineDifference;
            if (paneKey.Equals("MELSEC", StringComparison.Ordinal))
            {
                if (TryGetNormalMelsecPollingId(row, out var id))
                {
                    if (IsDiscreteMelsecSignal(id))
                    {
                        var value = ReadDelimitedField(row.Message, "값=") ?? "";
                        if (previousMelsecValues.TryGetValue(id, out var previous)
                            && !string.Equals(previous, value, StringComparison.OrdinalIgnoreCase))
                            isChange = true;
                        previousMelsecValues[id] = value;
                    }
                }
                else if (ContainsTransitionTerm(row.Message)
                         || row.Message.Contains("[쓰기]", StringComparison.OrdinalIgnoreCase)
                         || row.Message.Contains("지연 진단", StringComparison.OrdinalIgnoreCase))
                    isChange = true;
            }
            else if (ContainsTransitionTerm(row.Message))
                isChange = true;

            result.Add(row with { IsStateChange = isChange });
        }
        return result;
    }

    private static bool IsDiscreteMelsecSignal(string id)
    {
        return DiscreteMelsecIdTerms.Any(term => id.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsTransitionTerm(string message)
    {
        return TransitionTerms.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ST_ALARM_ANALYSIS_LOG_ROW> CondenseMelsecPollingRows(
        IEnumerable<ST_ALARM_ANALYSIS_LOG_ROW> source)
    {
        var passthrough = new List<ST_ALARM_ANALYSIS_LOG_ROW>();
        var buckets = new Dictionary<MelsecPollingBucket, List<ST_ALARM_ANALYSIS_LOG_ROW>>();
        foreach (var row in source)
        {
            if (!TryGetNormalMelsecPollingId(row, out var id))
            {
                passthrough.Add(row);
                continue;
            }

            var second = row.OccurredAt.UtcDateTime.Ticks / TimeSpan.TicksPerSecond;
            var key = new MelsecPollingBucket(id, second);
            if (!buckets.TryGetValue(key, out var rows))
            {
                rows = [];
                buckets.Add(key, rows);
            }
            rows.Add(row);
        }

        foreach (var bucket in buckets.Values)
        {
            bucket.Sort((left, right) => left.OccurredAt.CompareTo(right.OccurredAt));
            if (bucket.Count == 1)
            {
                passthrough.Add(bucket[0]);
                continue;
            }

            var first = bucket[0];
            var last = bucket[^1];
            var id = ReadDelimitedField(last.Message, "ID=") ?? "UNKNOWN";
            var firstValue = ReadDelimitedField(first.Message, "값=") ?? "-";
            var lastValue = ReadDelimitedField(last.Message, "값=") ?? "-";
            var message = $"[정상 폴링 {bucket.Count:N0}건 묶음] ID={id} | {first.Time} ~ {last.Time} | 값={firstValue} → {lastValue}";
            passthrough.Add(new(
                last.OccurredAt,
                last.Time,
                last.Source,
                message,
                false,
                bucket.Any(row => row.IsAlarmMoment),
                bucket.Any(row => row.IsStateChange),
                bucket.Any(row => row.IsBaselineDifference)));
        }

        return passthrough;
    }

    private static bool TryGetNormalMelsecPollingId(ST_ALARM_ANALYSIS_LOG_ROW row, out string id)
    {
        id = "";
        if (row.IsImportant
            || !row.Source.Equals("MELSEC", StringComparison.OrdinalIgnoreCase)
            || !row.Message.Contains("값 수신 완료", StringComparison.OrdinalIgnoreCase)
            || !row.Message.Contains("품질=정상(VALID)", StringComparison.OrdinalIgnoreCase)
            || !row.Message.Contains("경로=중앙 폴링(POLL)", StringComparison.OrdinalIgnoreCase))
            return false;

        id = ReadDelimitedField(row.Message, "ID=") ?? "";
        return id.Length > 0;
    }

    private static string? ReadDelimitedField(string message, string marker)
    {
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;
        start += marker.Length;
        var end = message.IndexOf(" |", start, StringComparison.Ordinal);
        if (end < 0)
            end = message.Length;
        var value = message[start..end].Trim();
        return value.Length == 0 ? null : value;
    }

    private IEnumerable<LogFile> EnumerateFiles(DateTime from, DateTime through)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var date = from.Date;; date = date.AddDays(1))
        {
            var day = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            foreach (var file in EnumerateDayDirectory("Sequence", day, "PROCESS", "SEQUENCE", date))
                if (paths.Add(file.Path)) yield return file;
            foreach (var file in EnumerateDayDirectory("Station", day, "PROCESS", "STATION", date))
                if (paths.Add(file.Path)) yield return file;
            foreach (var category in new[] { "UI", "Recipe", "Setting", "Product" })
                foreach (var file in EnumerateDayDirectory(category, day, "CONTEXT", category.ToUpperInvariant(), date))
                    if (paths.Add(file.Path)) yield return file;
            foreach (var file in EnumerateInterfaceFiles(day, date))
                if (paths.Add(file.Path)) yield return file;
            foreach (var file in EnumerateProgramOpenFiles(day, date))
                if (paths.Add(file.Path)) yield return file;
            if (date == through.Date)
                break;
        }
    }

    private IEnumerable<LogFile> EnumerateDayDirectory(string category, string day, string pane, string source, DateTime date)
    {
        var directory = Path.Combine(_logRoot, category, day);
        if (!Directory.Exists(directory))
            yield break;
        foreach (var path in Directory.EnumerateFiles(directory).Where(IsSupportedLogFile))
            yield return new(path, pane, source, date, Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<LogFile> EnumerateInterfaceFiles(string day, DateTime date)
    {
        var root = Path.Combine(_logRoot, "Interface");
        if (!Directory.Exists(root))
            yield break;
        foreach (var moduleDirectory in Directory.EnumerateDirectories(root))
        {
            var directory = Path.Combine(moduleDirectory, day);
            if (!Directory.Exists(directory))
                continue;
            var source = Path.GetFileName(moduleDirectory).ToUpperInvariant();
            var pane = source.Equals("MELSEC", StringComparison.OrdinalIgnoreCase) ? "MELSEC" : "INTERFACE";
            foreach (var path in Directory.EnumerateFiles(directory).Where(IsSupportedLogFile))
                yield return new(path, pane, source, date, false);
        }
    }

    private IEnumerable<LogFile> EnumerateProgramOpenFiles(string day, DateTime date)
    {
        var root = Path.Combine(_logRoot, "ProgramOpen");
        if (!Directory.Exists(root))
            yield break;
        foreach (var path in Directory.EnumerateFiles(root, $"*{day}*.*", SearchOption.TopDirectoryOnly).Where(IsSupportedLogFile))
            yield return new(path, "CONTEXT", "PROGRAM", date, false);
    }

    private static bool IsSupportedLogFile(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".log" or ".csv" or ".jsonl";
    }

    private static void ReadFile(LogFile file, DateTimeOffset from, DateTimeOffset through, DateTimeOffset? alarmTime, List<ST_ALARM_ANALYSIS_LOG_ROW> rows, CancellationToken token)
    {
        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        DateTimeOffset? previousTime = null;
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            DateTimeOffset occurredAt;
            string message;
            var parsed = file.IsAlarmJson
                ? TryParseAlarmLine(line, out occurredAt, out message)
                : TryParsePlainLine(line, file.Date, out occurredAt, out message);
            if (parsed)
                previousTime = occurredAt;
            else if (previousTime is { } inherited)
            {
                occurredAt = inherited;
                message = line.Trim();
            }
            else
                continue;

            if (occurredAt > through)
                break;
            if (occurredAt < from)
                continue;
            if (message.Length > 2000)
                message = message[..2000] + "...";
            var important = file.IsAlarmJson || ImportantTerms.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));
            var alarmMoment = alarmTime is { } selectedAlarm
                && Math.Abs((occurredAt - selectedAlarm).TotalMilliseconds) <= 500.0;
            rows.Add(new(occurredAt, occurredAt.LocalDateTime.ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), file.Source, message, important, alarmMoment));
        }
    }

    private static bool TryParseAlarmLine(string line, out DateTimeOffset occurredAt, out string message)
    {
        occurredAt = default;
        message = "";
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("OccurredAt", out var occurredAtElement)
            || !DateTimeOffset.TryParse(occurredAtElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out occurredAt))
            return false;
        var code = ReadJsonText(root, "Code");
        var level = ReadJsonText(root, "Level");
        var group = ReadJsonText(root, "Group");
        var cause = ReadJsonText(root, "Cause");
        var solution = ReadJsonText(root, "Solution");
        message = $"[{code}] {level} · {group} · {cause}" + (solution.Length == 0 ? "" : $" / {solution}");
        return true;
    }

    private static string ReadJsonText(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
            return "";
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
    }

    private static bool TryParsePlainLine(string line, DateTime fileDate, out DateTimeOffset occurredAt, out string message)
    {
        if (TryParsePrefix(line, 23, ["yyyy-MM-dd HH:mm:ss.fff", "yyyy/MM/dd HH:mm:ss.fff"], out occurredAt))
        {
            message = line[23..].Trim();
            return true;
        }
        if (TryParsePrefix(line, 21, ["yy/MM/dd HH:mm:ss.fff", "yy-MM-dd HH:mm:ss.fff"], out occurredAt))
        {
            message = line[21..].Trim();
            return true;
        }
        if (line.Length >= 12 && DateTime.TryParseExact(line.AsSpan(0, 12), "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            occurredAt = new DateTimeOffset(DateTime.SpecifyKind(fileDate.Date.Add(time.TimeOfDay), DateTimeKind.Local));
            message = line[12..].Trim();
            return true;
        }

        occurredAt = default;
        message = "";
        return false;
    }

    private static bool TryParsePrefix(string line, int length, string[] formats, out DateTimeOffset occurredAt)
    {
        if (line.Length >= length
            && DateTimeOffset.TryParseExact(line.AsSpan(0, length), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out occurredAt))
            return true;
        occurredAt = default;
        return false;
    }

    private sealed record MelsecPollingBucket(string Id, long Second);
    private sealed record RangeReadResult(
        Dictionary<string, List<ST_ALARM_ANALYSIS_LOG_ROW>> Rows,
        int FileCount,
        IReadOnlyList<string> Warnings);
    private sealed record ReferenceReadResult(
        DateTimeOffset From,
        DateTimeOffset Through,
        Dictionary<string, List<ST_ALARM_ANALYSIS_LOG_ROW>> Rows,
        int RowCount);
    private sealed record LogFile(string Path, string Pane, string Source, DateTime Date, bool IsAlarmJson);
}

internal static class CAlarmFailureSummaryAnalyzer
{
    private const double LegacyVisionLookbackSeconds = 60.0;
    private const string NgSummaryMarker = "NG_SUMMARY |";
    private static readonly Regex VisionResultRegex = new(
        @"Mark\[(?<mark>\d+)\]\s+Result\s*:\s*(?<result>-?\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static ST_ALARM_FAILURE_SUMMARY CreatePending(ST_ALARM_HISTORY_ROW alarm)
    {
        return new(
            "LOG ANALYSIS REQUIRED",
            "NOT LOADED",
            alarm.Group,
            "-",
            "LOAD LOGS를 누르면 실패 직전의 직접 근거와 관련 상태를 분석합니다.",
            CreateAlarmItems(alarm, "-", "-", "-"));
    }

    public static ST_ALARM_FAILURE_SUMMARY CreateLoadFailure(ST_ALARM_HISTORY_ROW alarm, string error)
    {
        return new(
            "LOG ANALYSIS FAILED",
            "LOAD FAILURE",
            alarm.Group,
            "-",
            error,
            CreateAlarmItems(alarm, "-", "-", "-"));
    }

    public static ST_ALARM_FAILURE_SUMMARY Analyze(
        IReadOnlyDictionary<string, List<ST_ALARM_ANALYSIS_LOG_ROW>> rows,
        IReadOnlyList<ST_ALARM_CAUSE_CANDIDATE> candidates,
        ST_ALARM_HISTORY_ROW alarm)
    {
        if (TryCreateReviewSummary(rows, alarm, out var reviewSummary))
            return reviewSummary;

        var candidate = candidates.FirstOrDefault();
        var evidence = candidate?.Evidence ?? rows
            .SelectMany(pair => pair.Value)
            .Where(row => row.OccurredAt <= alarm.OccurredAt && row.IsImportant)
            .OrderByDescending(row => row.OccurredAt)
            .FirstOrDefault();
        var source = evidence?.Source ?? alarm.Group;
        var leadTime = evidence is null ? "-" : FormatLeadTime(evidence.OccurredAt, alarm.OccurredAt);
        var rule = candidate?.Rule ?? "NO_DIRECT_EVIDENCE";
        var category = ResolveCategory(rule);
        var title = candidate?.Title ?? "직접 실패 근거 미확인";
        var detail = evidence?.Message
            ?? $"분석 범위 안에서 직접 실패 로그를 찾지 못했습니다. 알람 내용: {alarm.Cause}";
        return new(
            title,
            category,
            source,
            leadTime,
            detail,
            CreateAlarmItems(alarm, source, rule, leadTime));
    }

    public static bool IsVisionFailureMessage(string message)
    {
        foreach (Match match in VisionResultRegex.Matches(message))
        {
            if (int.TryParse(match.Groups["result"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                && result != 0)
                return true;
        }
        return false;
    }

    private static bool TryCreateReviewSummary(
        IReadOnlyDictionary<string, List<ST_ALARM_ANALYSIS_LOG_ROW>> rows,
        ST_ALARM_HISTORY_ROW alarm,
        out ST_ALARM_FAILURE_SUMMARY summary)
    {
        var allRows = rows.SelectMany(pair => pair.Value)
            .Where(row => row.OccurredAt <= alarm.OccurredAt)
            .OrderBy(row => row.OccurredAt)
            .ToArray();
        var structured = allRows.LastOrDefault(row => row.Message.Contains(NgSummaryMarker, StringComparison.OrdinalIgnoreCase));
        if (structured is not null && TryParseNgSummary(structured.Message, out var values))
        {
            var total = ReadInt(values, "Total");
            var ok = ReadInt(values, "OK");
            var ng = ReadInt(values, "NG");
            var vision = ReadInt(values, "VisionFindNG");
            var toleranceX = ReadInt(values, "ToleranceXNG");
            var toleranceY = ReadInt(values, "ToleranceYNG");
            var toleranceXy = ReadInt(values, "ToleranceXYNG");
            var other = ReadInt(values, "OtherNG");
            var resultCodes = ReadText(values, "ResultCodes", "-");
            var firstNg = ReadText(values, "FirstNG", "-");
            summary = CreateReviewSummary(
                alarm,
                structured,
                total,
                ok,
                ng,
                vision,
                toleranceX,
                toleranceY,
                toleranceXy,
                other,
                resultCodes,
                firstNg);
            return true;
        }

        if (!IsReviewAlarm(alarm))
        {
            summary = null!;
            return false;
        }

        var rawResults = ReadLatestVisionBatch(allRows, alarm.OccurredAt);
        if (rawResults.Count == 0)
        {
            summary = null!;
            return false;
        }

        var resultCodeText = string.Join(",", rawResults
            .GroupBy(sample => sample.Result)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}"));
        var ngResults = rawResults.Where(sample => sample.Result != 0).ToArray();
        var evidence = allRows.Last(row => VisionResultRegex.IsMatch(row.Message));
        var rawFirstNg = ngResults.Length == 0
            ? "-"
            : $"Mark[{ngResults[0].Mark}] / Vision Result={ngResults[0].Result}";
        summary = CreateReviewSummary(
            alarm,
            evidence,
            rawResults.Count,
            rawResults.Count - ngResults.Length,
            ngResults.Length,
            ngResults.Length,
            0,
            0,
            0,
            0,
            resultCodeText,
            rawFirstNg,
            "기존 로그에는 허용오차별 NG 사유가 없어 Vision Result 기준으로 집계했습니다.");
        return true;
    }

    private static ST_ALARM_FAILURE_SUMMARY CreateReviewSummary(
        ST_ALARM_HISTORY_ROW alarm,
        ST_ALARM_ANALYSIS_LOG_ROW evidence,
        int total,
        int ok,
        int ng,
        int vision,
        int toleranceX,
        int toleranceY,
        int toleranceXy,
        int other,
        string resultCodes,
        string firstNg,
        string note = "")
    {
        var tolerance = $"X {toleranceX:N0} / Y {toleranceY:N0} / XY {toleranceXy:N0}";
        var detail = $"Result Code {resultCodes} · First NG {firstNg}";
        if (!string.IsNullOrWhiteSpace(note))
            detail += $" · {note}";
        return new(
            "REVIEW INSPECTION NG",
            "VISION / TOLERANCE",
            evidence.Source,
            FormatLeadTime(evidence.OccurredAt, alarm.OccurredAt),
            detail,
            [
                new("TOTAL", total.ToString("N0", CultureInfo.InvariantCulture)),
                new("OK", ok.ToString("N0", CultureInfo.InvariantCulture)),
                new("NG", ng.ToString("N0", CultureInfo.InvariantCulture)),
                new("VISION FIND", vision.ToString("N0", CultureInfo.InvariantCulture)),
                new("TOLERANCE", tolerance),
                new("OTHER NG", other.ToString("N0", CultureInfo.InvariantCulture))
            ]);
    }

    private static IReadOnlyList<VisionResultSample> ReadLatestVisionBatch(
        IReadOnlyList<ST_ALARM_ANALYSIS_LOG_ROW> rows,
        DateTimeOffset alarmTime)
    {
        var matchingRows = rows
            .Where(row => row.OccurredAt <= alarmTime && VisionResultRegex.IsMatch(row.Message))
            .ToArray();
        if (matchingRows.Length == 0)
            return [];
        var results = new List<VisionResultSample>();
        foreach (var row in matchingRows.Where(row => (alarmTime - row.OccurredAt).TotalSeconds <= LegacyVisionLookbackSeconds))
        {
            foreach (Match match in VisionResultRegex.Matches(row.Message))
            {
                if (!int.TryParse(match.Groups["mark"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var mark)
                    || !int.TryParse(match.Groups["result"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
                    continue;
                results.Add(new(row.OccurredAt, mark, result));
            }
        }
        return results;
    }

    private static bool TryParseNgSummary(string message, out Dictionary<string, string> values)
    {
        values = new(StringComparer.OrdinalIgnoreCase);
        var marker = message.IndexOf(NgSummaryMarker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return false;
        var payload = message[(marker + NgSummaryMarker.Length)..];
        foreach (var field in payload.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0)
                continue;
            values[field[..separator].Trim()] = field[(separator + 1)..].Trim();
        }
        return values.Count > 0;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var text)
               && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static string ReadText(IReadOnlyDictionary<string, string> values, string key, string fallback)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    private static bool IsReviewAlarm(ST_ALARM_HISTORY_ROW alarm)
    {
        var text = $"{alarm.Group} {alarm.Cause} {alarm.Solution}";
        return text.Contains("REVIEW", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VISION", StringComparison.OrdinalIgnoreCase)
            || text.Contains("INSPECTION", StringComparison.OrdinalIgnoreCase)
            || text.Contains("검사", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ST_ALARM_FAILURE_SUMMARY_ITEM> CreateAlarmItems(
        ST_ALARM_HISTORY_ROW alarm,
        string source,
        string rule,
        string leadTime)
    {
        return
        [
            new("ALARM CODE", alarm.Code),
            new("LEVEL", alarm.Level),
            new("GROUP", alarm.Group),
            new("SOURCE", source),
            new("RULE", rule),
            new("LEAD TIME", leadTime)
        ];
    }

    private static string ResolveCategory(string rule)
    {
        return rule switch
        {
            "DIRECT_FAILURE" => "DIRECT FAILURE",
            "INTERLOCK" => "INTERLOCK / BLOCK",
            "SEQUENCE_FAILURE" => "SEQUENCE FAILURE",
            "COMMUNICATION" => "COMMUNICATION FAILURE",
            "STATE_CHANGE" => "STATE CHANGE",
            "OPERATOR_ACTION" => "OPERATOR ACTION",
            "FIRST_ABNORMAL" => "EARLY ABNORMAL",
            _ => "UNCLASSIFIED"
        };
    }

    private static string FormatLeadTime(DateTimeOffset evidenceTime, DateTimeOffset alarmTime)
    {
        return Math.Max(0.0, (alarmTime - evidenceTime).TotalSeconds)
            .ToString("0.###", CultureInfo.InvariantCulture) + " sec before";
    }

    private sealed record VisionResultSample(DateTimeOffset OccurredAt, int Mark, int Result);
}

internal static class CAlarmCauseCandidateAnalyzer
{
    private static readonly Regex NgTokenRegex = new(
        @"(^|[^A-Z0-9])NG([^A-Z0-9]|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly string[] FailureTerms =
    [
        "FAIL", "FAILED", "ERROR", "EXCEPTION", "TIMEOUT", "NACK", "NAK",
        "실패", "오류", "예외", "타임아웃", "확인 NG"
    ];
    private static readonly string[] CommunicationTerms =
    [
        "TIMEOUT", "POLL_GAP", "DELAY", "LATENCY", "DISCONNECT", "OFFLINE", "RETRY", "NACK", "NAK",
        "지연", "타임아웃", "응답 없음", "연결 끊", "통신 실패"
    ];
    private static readonly string[] StateChangeTerms =
    [
        " ON", " OFF", "STATE", "STATUS", "SERVO", "INTERLOCK", "LIMIT", "READY", "HOME", "MODE",
        "상태 변경", "모드 변경", "전환", "허가 ON", "허가 OFF"
    ];
    private static readonly string[] InterlockTerms =
    [
        "INTERLOCK", "BLOCK", "DENIED", "NOT READY", "PERMISSION", "LIMIT",
        "인터록", "차단", "허가 없음", "준비 안", "리미트"
    ];
    private static readonly string[] OperatorTerms = ["CLICK", "BUTTON", "COMMAND"];

    public static IReadOnlyList<ST_ALARM_CAUSE_CANDIDATE> Analyze(
        IReadOnlyList<ST_ALARM_ANALYSIS_PANE> panes,
        ST_ALARM_HISTORY_ROW alarm)
    {
        var alarmTime = alarm.OccurredAt;
        var evidence = new List<CandidateSeed>();
        var precedingRows = EnumeratePrecedingRows(panes, alarmTime);

        AddLatestCandidate(evidence, precedingRows, "DIRECT_FAILURE", "직접 실패 로그",
            "알람과 직접 연결되는 실패, NG 또는 예외 로그가 기록되었습니다.", 150, IsDirectFailure);
        AddLatestCandidate(evidence, precedingRows, "INTERLOCK", "인터록 / 동작 차단",
            "인터록, 준비 조건 또는 리미트에 의해 동작이 차단된 기록이 있습니다.", 130, IsInterlockFailure);
        AddLatestCandidate(evidence, precedingRows, alarmTime, "SEQUENCE_FAILURE", "SEQUENCE 실패",
            "Sequence 또는 Station 처리에서 실패 조건이 기록되었습니다.", 120, IsSequenceFailure);
        AddLatestCandidate(evidence, precedingRows, alarmTime, "COMMUNICATION", "통신 지연 / Timeout",
            "통신 지연, Timeout 또는 연결 이상 표현이 기록되었습니다.", 100, IsCommunicationIssue);
        AddLatestCandidate(evidence, precedingRows, alarmTime, "STATE_CHANGE", "장비 상태 변경",
            "알람 이전에 장비 또는 제어 신호의 상태 변경이 기록되었습니다.", 80, IsEquipmentStateChange);
        AddLatestCandidate(evidence, precedingRows, alarmTime, "OPERATOR_ACTION", "작업자 조작",
            "알람 이전에 작업자 UI 조작이 기록되었습니다.", 60, IsOperatorAction);
        AddFirstAbnormalCandidate(evidence, precedingRows);

        var selected = new List<CandidateSeed>(3);
        foreach (var candidate in evidence.OrderByDescending(item => item.Priority).ThenByDescending(item => item.Evidence.OccurredAt))
        {
            if (selected.Any(item => IsSameEvidence(item, candidate)))
                continue;
            selected.Add(candidate);
            if (selected.Count == 3)
                break;
        }

        return selected
            .Select((candidate, index) => new ST_ALARM_CAUSE_CANDIDATE(
                index + 1,
                candidate.Rule,
                candidate.Title,
                AppendLeadTime(candidate.Reason, candidate.Evidence.OccurredAt, alarmTime),
                candidate.PaneKey,
                candidate.Evidence))
            .ToArray();
    }

    private static IReadOnlyList<PaneRow> EnumeratePrecedingRows(
        IReadOnlyList<ST_ALARM_ANALYSIS_PANE> panes,
        DateTimeOffset alarmTime)
    {
        var rows = new List<PaneRow>();
        foreach (var pane in panes)
        {
            foreach (var row in pane.Rows)
            {
                if (row.OccurredAt <= alarmTime && !pane.Key.Equals("SYSTEM", StringComparison.Ordinal))
                    rows.Add(new(pane.Key, row));
            }
        }
        rows.Sort(ComparePaneRows);
        return rows;
    }

    private static int ComparePaneRows(PaneRow left, PaneRow right)
    {
        var timeComparison = left.Row.OccurredAt.CompareTo(right.Row.OccurredAt);
        return timeComparison != 0
            ? timeComparison
            : string.Compare(left.Row.Source, right.Row.Source, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddFirstAbnormalCandidate(
        ICollection<CandidateSeed> candidates,
        IReadOnlyList<PaneRow> rows)
    {
        foreach (var row in rows)
        {
            if (!IsAbnormal(row))
                continue;
            candidates.Add(new(
                "FIRST_ABNORMAL",
                "최초 이상 징후",
                "분석 구간에서 가장 먼저 확인된 오류 또는 지연 로그입니다.",
                row.PaneKey,
                row.Row,
                40));
            return;
        }
    }

    private static void AddLatestCandidate(
        ICollection<CandidateSeed> candidates,
        IReadOnlyList<PaneRow> rows,
        string rule,
        string title,
        string reason,
        int priority,
        Func<PaneRow, bool> predicate)
    {
        AddLatestCandidate(candidates, rows, default, rule, title, reason, priority, predicate);
    }

    private static void AddLatestCandidate(
        ICollection<CandidateSeed> candidates,
        IReadOnlyList<PaneRow> rows,
        DateTimeOffset alarmTime,
        string rule,
        string title,
        string reason,
        int priority,
        Func<PaneRow, bool> predicate)
    {
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            var row = rows[index];
            if (!predicate(row))
                continue;
            candidates.Add(new(rule, title, reason, row.PaneKey, row.Row, priority));
            return;
        }
    }

    private static bool IsAbnormal(PaneRow row)
    {
        return IsDirectFailure(row) || IsInterlockFailure(row) || IsCommunicationIssue(row);
    }

    private static bool IsDirectFailure(PaneRow row)
    {
        return row.Row.Message.Contains("NG_SUMMARY |", StringComparison.OrdinalIgnoreCase)
            || CAlarmFailureSummaryAnalyzer.IsVisionFailureMessage(row.Row.Message)
            || ContainsAny(row.Row.Message, FailureTerms)
            || NgTokenRegex.IsMatch(row.Row.Message);
    }

    private static bool IsInterlockFailure(PaneRow row)
    {
        return ContainsAny(row.Row.Message, InterlockTerms);
    }

    private static bool IsSequenceFailure(PaneRow row)
    {
        return row.PaneKey.Equals("PROCESS", StringComparison.Ordinal)
            && IsDirectFailure(row);
    }

    private static bool IsCommunicationIssue(PaneRow row)
    {
        return (row.PaneKey.Equals("MELSEC", StringComparison.Ordinal)
                || row.PaneKey.Equals("INTERFACE", StringComparison.Ordinal))
            && ContainsAny(row.Row.Message, CommunicationTerms);
    }

    private static bool IsEquipmentStateChange(PaneRow row)
    {
        if (row.PaneKey.Equals("CONTEXT", StringComparison.Ordinal))
            return false;
        return ContainsAny(row.Row.Message, StateChangeTerms)
            && !row.Row.Message.Contains("값 수신 완료", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOperatorAction(PaneRow row)
    {
        return row.PaneKey.Equals("CONTEXT", StringComparison.Ordinal)
            && row.Row.Source.Equals("UI", StringComparison.OrdinalIgnoreCase)
            && ContainsAny(row.Row.Message, OperatorTerms);
    }

    private static bool ContainsAny(string value, IEnumerable<string> terms)
    {
        foreach (var term in terms)
        {
            if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsSameEvidence(CandidateSeed left, CandidateSeed right)
    {
        return left.Evidence.OccurredAt == right.Evidence.OccurredAt
            && left.Evidence.Source.Equals(right.Evidence.Source, StringComparison.OrdinalIgnoreCase)
            && left.Evidence.Message.Equals(right.Evidence.Message, StringComparison.Ordinal);
    }

    private static string AppendLeadTime(string reason, DateTimeOffset evidenceTime, DateTimeOffset alarmTime)
    {
        var lead = alarmTime - evidenceTime;
        var timeText = Math.Max(0.0, lead.TotalSeconds)
            .ToString("0.###", CultureInfo.InvariantCulture) + " sec";
        return $"{reason} 알람 {timeText} 전.";
    }

    private sealed record PaneRow(string PaneKey, ST_ALARM_ANALYSIS_LOG_ROW Row);
    private sealed record CandidateSeed(
        string Rule,
        string Title,
        string Reason,
        string PaneKey,
        ST_ALARM_ANALYSIS_LOG_ROW Evidence,
        int Priority);
}

internal static class CAlarmIncidentTimelineBuilder
{
    private const int MaximumEvidenceItems = 7;
    private const int MaximumSummaryLength = 86;
    private static readonly string[] TimelineTerms =
    [
        "START", "BEGIN", "COMPLETE", "DONE", "FAIL", "FAILED", "NG", "TIMEOUT", "STOP",
        "INTERLOCK", "CLICK", "시작", "완료", "실패", "지연", "중지", "차단", "이동"
    ];

    public static IReadOnlyList<ST_ALARM_TIMELINE_ITEM> Build(
        IReadOnlyList<ST_ALARM_ANALYSIS_PANE> panes,
        IReadOnlyList<ST_ALARM_CAUSE_CANDIDATE> candidates,
        ST_ALARM_HISTORY_ROW alarm)
    {
        var seeds = new Dictionary<TimelineKey, TimelineSeed>();
        foreach (var candidate in candidates)
            AddSeed(seeds, candidate.PaneKey, candidate.Evidence, 130 - candidate.Rank);

        foreach (var pane in panes)
        {
            foreach (var row in pane.Rows)
            {
                var priority = GetPriority(pane.Key, row);
                if (priority > 0)
                    AddSeed(seeds, pane.Key, row, priority);
            }
        }

        var selected = new List<TimelineSeed>(MaximumEvidenceItems);
        var paneCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var seed in seeds.Values
                     .OrderByDescending(item => item.Priority)
                     .ThenBy(item => Math.Abs((item.Row.OccurredAt - alarm.OccurredAt).TotalMilliseconds)))
        {
            paneCounts.TryGetValue(seed.PaneKey, out var paneCount);
            var paneLimit = seed.PaneKey.Equals("PROCESS", StringComparison.Ordinal) ? 3 : 2;
            if (paneCount >= paneLimit)
                continue;
            selected.Add(seed);
            paneCounts[seed.PaneKey] = paneCount + 1;
            if (selected.Count == MaximumEvidenceItems)
                break;
        }

        var timeline = selected
            .Select(seed => CreateItem(seed.PaneKey, seed.Row, alarm.OccurredAt))
            .ToList();
        timeline.Add(new(
            alarm.OccurredAt,
            "0.000 sec",
            "ALARM",
            $"[{alarm.Code}] {alarm.Cause}",
            "",
            null,
            true));
        return timeline
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => item.IsAlarm ? 1 : 0)
            .ToArray();
    }

    private static void AddSeed(
        IDictionary<TimelineKey, TimelineSeed> seeds,
        string paneKey,
        ST_ALARM_ANALYSIS_LOG_ROW row,
        int priority)
    {
        var key = new TimelineKey(row.OccurredAt, row.Source, row.Message);
        if (!seeds.TryGetValue(key, out var current) || current.Priority < priority)
            seeds[key] = new(paneKey, row, priority);
    }

    private static int GetPriority(string paneKey, ST_ALARM_ANALYSIS_LOG_ROW row)
    {
        if (row.IsImportant)
            return 90;
        if (paneKey.Equals("PROCESS", StringComparison.Ordinal) && ContainsTimelineTerm(row.Message))
            return 75;
        if (paneKey.Equals("CONTEXT", StringComparison.Ordinal)
            && row.Source.Equals("UI", StringComparison.OrdinalIgnoreCase)
            && row.Message.Contains("CLICK", StringComparison.OrdinalIgnoreCase))
            return 65;
        return 0;
    }

    private static bool ContainsTimelineTerm(string message)
    {
        foreach (var term in TimelineTerms)
        {
            if (message.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static ST_ALARM_TIMELINE_ITEM CreateItem(
        string paneKey,
        ST_ALARM_ANALYSIS_LOG_ROW row,
        DateTimeOffset alarmTime)
    {
        var seconds = (row.OccurredAt - alarmTime).TotalSeconds;
        var offset = seconds.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture) + " sec";
        return new(
            row.OccurredAt,
            offset,
            row.Source,
            Summarize(row.Message, row.Source),
            paneKey,
            row,
            false);
    }

    private static string Summarize(string message, string source)
    {
        var summary = message.Trim();
        if (source.Equals("SEQUENCE", StringComparison.OrdinalIgnoreCase))
        {
            var open = summary.LastIndexOf('[');
            var close = open >= 0 ? summary.IndexOf(']', open + 1) : -1;
            if (open >= 0 && close > open)
                summary = summary[(open + 1)..close];
        }
        else if (source.Equals("MELSEC", StringComparison.OrdinalIgnoreCase))
        {
            var marker = summary.IndexOf("[MELSEC]", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
                summary = summary[marker..];
        }
        else
        {
            var command = summary.IndexOf("\\COMMAND\\", StringComparison.OrdinalIgnoreCase);
            if (command >= 0)
                summary = summary[(command + "\\COMMAND\\".Length)..];
            else if (source.Equals("UI", StringComparison.OrdinalIgnoreCase))
            {
                var click = summary.IndexOf("\\CLICK\\", StringComparison.OrdinalIgnoreCase);
                if (click >= 0)
                    summary = summary[(click + "\\CLICK\\".Length)..];
            }
        }

        summary = summary.Replace('\\', ' ').Trim();
        return summary.Length <= MaximumSummaryLength
            ? summary
            : summary[..(MaximumSummaryLength - 3)] + "...";
    }

    private sealed record TimelineKey(DateTimeOffset OccurredAt, string Source, string Message);
    private sealed record TimelineSeed(string PaneKey, ST_ALARM_ANALYSIS_LOG_ROW Row, int Priority);
}
