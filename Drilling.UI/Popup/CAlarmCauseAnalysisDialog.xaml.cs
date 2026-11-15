using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Popup;

public partial class CAlarmCauseAnalysisDialog : Window
{
    private readonly CAlarmCauseAnalysisPanel _panel;
    private readonly HashSet<DataGrid> _logGrids = [];
    private bool _loaded;
    private bool _synchronizingSelection;

    public CAlarmCauseAnalysisDialog(string logDirectory, ST_ALARM_HISTORY_ROW alarm)
    {
        InitializeComponent();
        _panel = new(logDirectory, alarm);
        DataContext = _panel;
        _panel.AnalysisLoaded += AnalysisLoaded;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        await _panel.LoadAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _panel.AnalysisLoaded -= AnalysisLoaded;
        _panel.Dispose();
    }

    private async void QuickRangeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string rangeText }
            || !int.TryParse(rangeText, out var rangeSeconds))
            return;

        _panel.BeforeSeconds = rangeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _panel.AfterSeconds = rangeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await _panel.LoadAsync();
    }

    private void LogGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;
        _logGrids.Add(grid);
        if (_panel.CursorOccurredAt is { } cursor)
            SynchronizeGrid(grid, cursor);
    }

    private void LogGridUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is DataGrid grid)
            _logGrids.Remove(grid);
    }

    private void LogGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingSelection
            || sender is not DataGrid { SelectedItem: ST_ALARM_ANALYSIS_LOG_ROW row })
            return;
        SynchronizeTo(row.OccurredAt);
    }

    private void CandidateClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ST_ALARM_CAUSE_CANDIDATE candidate })
            return;
        SynchronizeTo(candidate.Evidence.OccurredAt, candidate.Evidence);
    }

    private void TimelineClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ST_ALARM_TIMELINE_ITEM item })
            return;
        SynchronizeTo(item.OccurredAt, item.Evidence);
    }

    private void AnalysisLoaded(DateTimeOffset alarmTime)
    {
        Dispatcher.BeginInvoke(() => SynchronizeTo(alarmTime), DispatcherPriority.Loaded);
    }

    private void SynchronizeTo(DateTimeOffset cursor, ST_ALARM_ANALYSIS_LOG_ROW? preferredRow = null)
    {
        _panel.SetCursor(cursor);
        _synchronizingSelection = true;
        try
        {
            foreach (var grid in _logGrids.ToArray())
                SynchronizeGrid(grid, cursor, preferredRow);
        }
        finally
        {
            _synchronizingSelection = false;
        }
    }

    private static void SynchronizeGrid(
        DataGrid grid,
        DateTimeOffset cursor,
        ST_ALARM_ANALYSIS_LOG_ROW? preferredRow = null)
    {
        if (grid.ItemsSource is not IEnumerable<ST_ALARM_ANALYSIS_LOG_ROW> rows)
            return;

        ST_ALARM_ANALYSIS_LOG_ROW? nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var row in rows)
        {
            if (preferredRow is not null && ReferenceEquals(row, preferredRow))
            {
                nearest = row;
                break;
            }
            var distance = Math.Abs((row.OccurredAt - cursor).TotalMilliseconds);
            if (distance >= nearestDistance)
                continue;
            nearest = row;
            nearestDistance = distance;
        }

        if (nearest is null)
        {
            grid.SelectedItem = null;
            return;
        }

        grid.SelectedItem = nearest;
        grid.ScrollIntoView(nearest);
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}
