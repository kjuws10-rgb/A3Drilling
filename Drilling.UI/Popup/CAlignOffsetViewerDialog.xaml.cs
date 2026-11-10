using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Popup;

public partial class CAlignOffsetViewerDialog : Window
{
    public CAlignOffsetViewerDialog(CAlignOffsetViewerPanel panel)
    {
        InitializeComponent();
        DataContext = panel;
    }

    private void SelectShotFromMap(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement map || DataContext is not CAlignOffsetViewerPanel panel)
            return;

        var point = e.GetPosition(map);
        panel.SelectShotAt(point.X, point.Y);
        ScrollSelectedShotIntoView(panel.SelectedRow);
        e.Handled = true;
    }

    private void OffsetGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid { SelectedItem: ST_ALIGN_OFFSET_VIEW_ROW row } ||
            DataContext is not CAlignOffsetViewerPanel panel)
            return;

        panel.SelectShotByKey(row.ShotKey);
    }

    private void ScrollSelectedShotIntoView(ST_ALIGN_OFFSET_VIEW_ROW? row)
    {
        if (row is null)
            return;
        _ = OffsetGrid.Dispatcher.BeginInvoke(
            () => OffsetGrid.ScrollIntoView(row),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
    private void OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); }
}
