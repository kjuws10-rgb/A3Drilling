using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Popup;

public partial class CAutoReviewMonitorDialog : Window
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly CAutoReviewMonitorPanel _panel;
    private long _lastResults;
    public CAutoReviewMonitorDialog(CAutoReviewMonitorPanel panel)
    {
        InitializeComponent(); _panel = panel; DataContext = panel;
        panel.Preview.ShowLineDetails = ShowLine;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        _timer.Tick += Refresh;
        IsVisibleChanged += (_, _) => { if (IsVisible) { Refresh(null, EventArgs.Empty); _timer.Start(); } else _timer.Stop(); };
        Closed += (_, _) => { _timer.Stop(); _timer.Tick -= Refresh; panel.Preview.ShowLineDetails = null; };
    }
    public bool IsRefreshing => _timer.IsEnabled;
    private void Refresh(object? sender, EventArgs e)
    {
        _panel.RefreshFeedback();
        var now = Environment.TickCount64;
        if (now - _lastResults < 200) return;
        _lastResults = now;
        _panel.RefreshResults();
    }
    private void ShowLine(CFineInspectionLine line)
    {
        var details = new CFineLineDetailsDialog(line) { Owner = this };
        details.Show();
    }
    private void LineDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: CFineInspectionLine line }) ShowLine(line);
    }
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}
