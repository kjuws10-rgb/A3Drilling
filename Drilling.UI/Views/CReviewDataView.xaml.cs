namespace Drilling.UI.Views
{
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;
    using System.Windows.Media;
    using System.Windows.Shapes;
    using System.Windows.Threading;
    using Drilling.UI.Menu.Menus;

    internal sealed class CBindingProxy : Freezable
    {
        public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
            nameof(Data),
            typeof(object),
            typeof(CBindingProxy));

        public object? Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        protected override Freezable CreateInstanceCore()
        {
            return new CBindingProxy();
        }
    }

    public partial class CReviewDataView : UserControl
    {
        private FrameworkElement? _resultEditDragSurface;
        private Rectangle? _resultEditDragRectangle;
        private Point _resultEditDragStart;
        private bool _resultEditDragIsScatter;

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
                await panel.ReloadWhenVisible();
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

            if (panel.IsResultEditMode)
            {
                BeginResultEditDrag(map, ShotMapEditSelectionRectangle, isScatter: false, eventArgs);
                return;
            }

            var point = eventArgs.GetPosition(map);
            panel.SelectShotAt(point.X, point.Y);
            eventArgs.Handled = true;
        }

        private void ReviewDataScatterPlot_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
        {
            if (sender is not FrameworkElement plot
                || plot.ActualWidth <= 0.0
                || plot.ActualHeight <= 0.0
                || DataContext is not CMenuCorrection.CReviewDataPanel panel)
            {
                return;
            }

            if (panel.IsResultEditMode)
            {
                BeginResultEditDrag(plot, ScatterEditSelectionRectangle, isScatter: true, eventArgs);
                return;
            }

            var point = eventArgs.GetPosition(plot);
            panel.SelectScatterPointAt(
                point.X * panel.ScatterPlotSurfaceWidth / plot.ActualWidth,
                point.Y * panel.ScatterPlotSurfaceHeight / plot.ActualHeight);
            eventArgs.Handled = true;
        }

        private void BeginResultEditDrag(
            FrameworkElement surface,
            Rectangle rectangle,
            bool isScatter,
            MouseButtonEventArgs eventArgs)
        {
            if (eventArgs.ChangedButton != MouseButton.Left)
            {
                return;
            }

            _resultEditDragSurface = surface;
            _resultEditDragRectangle = rectangle;
            _resultEditDragIsScatter = isScatter;
            _resultEditDragStart = eventArgs.GetPosition(surface);
            rectangle.Width = 0.0;
            rectangle.Height = 0.0;
            Canvas.SetLeft(rectangle, _resultEditDragStart.X);
            Canvas.SetTop(rectangle, _resultEditDragStart.Y);
            rectangle.Visibility = Visibility.Visible;
            surface.CaptureMouse();
            eventArgs.Handled = true;
        }

        private void ReviewDataEditSurface_MouseMove(object sender, MouseEventArgs eventArgs)
        {
            if (!ReferenceEquals(sender, _resultEditDragSurface)
                || _resultEditDragSurface is null
                || _resultEditDragRectangle is null)
            {
                return;
            }

            if (eventArgs.LeftButton != MouseButtonState.Pressed)
            {
                CancelResultEditDrag();
                return;
            }

            UpdateResultEditDragRectangle(eventArgs.GetPosition(_resultEditDragSurface));
            eventArgs.Handled = true;
        }

        private void ReviewDataEditSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
        {
            if (!ReferenceEquals(sender, _resultEditDragSurface)
                || _resultEditDragSurface is null
                || _resultEditDragRectangle is null
                || DataContext is not CMenuCorrection.CReviewDataPanel panel)
            {
                return;
            }

            var surface = _resultEditDragSurface;
            var end = eventArgs.GetPosition(surface);
            UpdateResultEditDragRectangle(end);
            var deltaX = Math.Abs(end.X - _resultEditDragStart.X);
            var deltaY = Math.Abs(end.Y - _resultEditDragStart.Y);
            if (deltaX < 5.0 && deltaY < 5.0)
            {
                if (_resultEditDragIsScatter)
                {
                    panel.SelectScatterPointAt(
                        end.X * panel.ScatterPlotSurfaceWidth / Math.Max(1.0, surface.ActualWidth),
                        end.Y * panel.ScatterPlotSurfaceHeight / Math.Max(1.0, surface.ActualHeight));
                }
                else
                {
                    panel.SelectShotAt(
                        end.X * panel.ShotMapSurfaceWidth / Math.Max(1.0, surface.ActualWidth),
                        end.Y * panel.ShotMapSurfaceHeight / Math.Max(1.0, surface.ActualHeight));
                }

                panel.SelectCurrentShotForResultEdit();
            }
            else if (_resultEditDragIsScatter)
            {
                panel.SelectResultEditShotsInScatter(
                    _resultEditDragStart.X * panel.ScatterPlotSurfaceWidth / Math.Max(1.0, surface.ActualWidth),
                    _resultEditDragStart.Y * panel.ScatterPlotSurfaceHeight / Math.Max(1.0, surface.ActualHeight),
                    end.X * panel.ScatterPlotSurfaceWidth / Math.Max(1.0, surface.ActualWidth),
                    end.Y * panel.ScatterPlotSurfaceHeight / Math.Max(1.0, surface.ActualHeight));
            }
            else
            {
                panel.SelectResultEditShotsInMap(
                    _resultEditDragStart.X * panel.ShotMapSurfaceWidth / Math.Max(1.0, surface.ActualWidth),
                    _resultEditDragStart.Y * panel.ShotMapSurfaceHeight / Math.Max(1.0, surface.ActualHeight),
                    end.X * panel.ShotMapSurfaceWidth / Math.Max(1.0, surface.ActualWidth),
                    end.Y * panel.ShotMapSurfaceHeight / Math.Max(1.0, surface.ActualHeight));
            }

            CancelResultEditDrag();
            eventArgs.Handled = true;
        }

        private void ReviewDataEditSurface_LostMouseCapture(object sender, MouseEventArgs eventArgs)
        {
            if (ReferenceEquals(sender, _resultEditDragSurface))
            {
                CancelResultEditDrag();
            }
        }

        private void UpdateResultEditDragRectangle(Point end)
        {
            if (_resultEditDragRectangle is null)
            {
                return;
            }

            var left = Math.Min(_resultEditDragStart.X, end.X);
            var top = Math.Min(_resultEditDragStart.Y, end.Y);
            Canvas.SetLeft(_resultEditDragRectangle, left);
            Canvas.SetTop(_resultEditDragRectangle, top);
            _resultEditDragRectangle.Width = Math.Abs(end.X - _resultEditDragStart.X);
            _resultEditDragRectangle.Height = Math.Abs(end.Y - _resultEditDragStart.Y);
        }

        private void CancelResultEditDrag()
        {
            if (_resultEditDragRectangle is not null)
            {
                _resultEditDragRectangle.Visibility = Visibility.Collapsed;
            }

            if (_resultEditDragSurface?.IsMouseCaptured == true)
            {
                _resultEditDragSurface.ReleaseMouseCapture();
            }

            _resultEditDragSurface = null;
            _resultEditDragRectangle = null;
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

        private void ReviewDataResultGrid_LoadingRow(object sender, DataGridRowEventArgs eventArgs)
        {
            if (DataContext is CMenuCorrection.CReviewDataPanel panel)
            {
                eventArgs.Row.SetCurrentValue(
                    DataGridRow.IsSelectedProperty,
                    ReferenceEquals(eventArgs.Row.Item, panel.SelectedRow));
            }
        }

        private void ReviewDataReinspectionGrid_SelectionChanged(
            object sender,
            SelectionChangedEventArgs eventArgs)
        {
            if (sender is DataGrid
                {
                    SelectedItem: CMenuCorrection.ST_REVIEW_DATA_REINSPECTION_ITEM item
                }
                && DataContext is CMenuCorrection.CReviewDataPanel panel)
            {
                panel.SelectReinspectionShot(item);
            }
        }

        private void ReviewDataReinspectionCheckBox_Changed(object sender, RoutedEventArgs eventArgs)
        {
            if (sender is CheckBox
                {
                    DataContext: CMenuCorrection.ST_REVIEW_DATA_GRID_ROW row,
                    IsChecked: var isChecked
                }
                && DataContext is CMenuCorrection.CReviewDataPanel panel)
            {
                panel.SetReinspectionRowSelection(row, isChecked == true);
            }
        }

        private void ReviewDataResultEditCheckBox_Changed(object sender, RoutedEventArgs eventArgs)
        {
            if (sender is CheckBox
                {
                    DataContext: CMenuCorrection.ST_REVIEW_DATA_GRID_ROW row,
                    IsChecked: var isChecked
                }
                && DataContext is CMenuCorrection.CReviewDataPanel panel)
            {
                panel.SetResultEditRowSelection(row, isChecked == true);
            }
        }

        private void ReviewDataResultEditKeyInGrid_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
        {
            if (sender is DataGrid { SelectedItem: CMenuCorrection.ST_REVIEW_DATA_EDIT_ITEM item }
                && DataContext is CMenuCorrection.CReviewDataPanel panel)
            {
                panel.SelectedResultEditKeyInItem = item;
            }
        }

        private void ReviewDataResultEditCopyGrid_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
        {
            if (sender is DataGrid { SelectedItem: CMenuCorrection.ST_REVIEW_DATA_EDIT_ITEM item }
                && DataContext is CMenuCorrection.CReviewDataPanel panel)
            {
                panel.SelectedResultEditCopyTarget = item;
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
