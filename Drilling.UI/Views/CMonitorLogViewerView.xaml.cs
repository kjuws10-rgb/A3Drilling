using System.Windows.Controls;
using System.Windows;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Views;

public partial class CMonitorLogViewerView : UserControl
{
    public CMonitorLogViewerView() => InitializeComponent();

    private async Task InitializeIfVisible()
    {
        if (IsVisible && DataContext is CMonitorLogViewerPanel panel) await panel.InitializeAsync();
    }

    private async void ViewLoaded(object sender, RoutedEventArgs e) => await InitializeIfVisible();
    private async void ViewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => await InitializeIfVisible();
    private async void ViewDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => await InitializeIfVisible();

    private async void FolderExpanded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CMonitorLogViewerPanel panel && e.OriginalSource is TreeViewItem { DataContext: CLogTreeNode node })
            await panel.ExpandDirectoryAsync(node);
    }

    private async void LogSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is CMonitorLogViewerPanel panel) await panel.SelectNodeAsync(e.NewValue as CLogTreeNode);
    }

    private void LogTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox text) return;
        text.ScrollToHome();
        text.ScrollToHorizontalOffset(0);
    }
}
