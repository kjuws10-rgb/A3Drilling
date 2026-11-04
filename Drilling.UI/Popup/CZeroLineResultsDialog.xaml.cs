using System.Windows;
using System.Windows.Threading;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Popup;

public partial class CZeroLineResultsDialog : Window
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CZeroLineResultsPanel _panel;
    public CZeroLineResultsDialog(CZeroLineResultsPanel panel)
    {
        InitializeComponent(); _panel = panel; DataContext = panel; panel.Refresh();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        _timer.Tick += Refresh;
        IsVisibleChanged += (_, _) => { if (IsVisible) { panel.Refresh(); _timer.Start(); } else _timer.Stop(); };
        Closed += (_, _) => { _timer.Stop(); _timer.Tick -= Refresh; };
    }
    public bool IsRefreshing => _timer.IsEnabled;
    private void Refresh(object? sender, EventArgs e) => _panel.Refresh();
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}
