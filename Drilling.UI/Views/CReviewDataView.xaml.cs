namespace Drilling.UI.Views
{
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;
    using System.Windows.Media;
    using System.Windows.Threading;
    using Drilling.UI.Menu.Menus;

    public partial class CReviewDataView : UserControl
    {
        public CReviewDataView()
        {
            InitializeComponent();
        }

        private async void ReviewDataView_IsVisibleChanged(
            object sender,
            DependencyPropertyChangedEventArgs eventArgs)
        {
            if (IsVisible && DataContext is CMenuCorrection.CReviewDataPanel panel)
            {
                await panel.Reload();
            }
        }

        private void ReviewResultsTree_SelectedItemChanged(
            object sender,
            RoutedPropertyChangedEventArgs<object> eventArgs)
        {
            if (DataContext is CMenuCorrection.CReviewDataPanel panel
                && eventArgs.NewValue is CMenuCorrection.ST_REVIEW_RESULT_TREE_NODE node)
            {
                panel.Select(node);
            }

            ReviewResultsTree.Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => FindVisualChild<ScrollViewer>(ReviewResultsTree)?.ScrollToLeftEnd()));
        }

        private void ReviewDataShotMap_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
        {
            if (sender is not FrameworkElement map || DataContext is not CMenuCorrection.CReviewDataPanel panel)
            {
                return;
            }

            var point = eventArgs.GetPosition(map);
            panel.SelectShotAt(point.X, point.Y);
            eventArgs.Handled = true;
        }

        private void ReviewDataResultGrid_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
        {
            if (sender is not DataGrid { SelectedItem: CMenuCorrection.ST_REVIEW_DATA_GRID_ROW row }
                || DataContext is not CMenuCorrection.CReviewDataPanel panel)
            {
                return;
            }

            panel.SelectRow(row);
            if (panel.SelectedRow is { } selectedRow)
            {
                ReviewDataResultGrid.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => ReviewDataResultGrid.ScrollIntoView(selectedRow)));
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent)
            where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match)
                {
                    return match;
                }

                var descendant = FindVisualChild<T>(child);
                if (descendant is not null)
                {
                    return descendant;
                }
            }

            return null;
        }
    }
}
