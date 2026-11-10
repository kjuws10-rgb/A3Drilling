namespace Drilling.UI.Popup;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drilling.UI.Menu.Menus;

public partial class CReviewDataOffsetApplyDialog : Window
{
    public CReviewDataOffsetApplyDialog(CMenuCorrection.CReviewDataPanel.CReviewDataOffsetApplyPanel panel)
    {
        InitializeComponent();
        DataContext = panel;
    }

    private void ShotMap_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is not FrameworkElement map
            || DataContext is not CMenuCorrection.CReviewDataPanel.CReviewDataOffsetApplyPanel panel)
        {
            return;
        }

        var point = eventArgs.GetPosition(map);
        panel.SelectShotAt(point.X, point.Y);
        ScrollSelectedRow(panel.SelectedRow);
        eventArgs.Handled = true;
    }

    private void OffsetPreviewGrid_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (sender is not DataGrid { SelectedItem: CMenuCorrection.ST_REVIEW_OFFSET_APPLY_GRID_ROW row }
            || DataContext is not CMenuCorrection.CReviewDataPanel.CReviewDataOffsetApplyPanel panel)
        {
            return;
        }

        panel.SelectRow(row);
    }

    private void ScrollSelectedRow(CMenuCorrection.ST_REVIEW_OFFSET_APPLY_GRID_ROW? row)
    {
        if (row is null)
        {
            return;
        }

        _ = OffsetPreviewGrid.Dispatcher.BeginInvoke(
            () => OffsetPreviewGrid.ScrollIntoView(row),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SaveClicked(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is CMenuCorrection.CReviewDataPanel.CReviewDataOffsetApplyPanel { CanSave: true })
        {
            DialogResult = true;
        }
    }

    private void CancelClicked(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }

    private void OnKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
