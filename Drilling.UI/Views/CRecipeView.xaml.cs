using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Drilling.Common.Managers;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Popup;

namespace Drilling.UI.Views;
public partial class CRecipeView : UserControl
{
    private const double PreviewZoomMinimum = 1.0;
    private const double PreviewZoomMaximum = 4.0;
    private const double PreviewZoomStep = 0.2;
    private bool _isRecipePreviewDragging;
    private Point _recipePreviewDragStart;
    private double _recipePreviewDragStartX;
    private double _recipePreviewDragStartY;
    private bool _isHistoryExpanded;
    public CRecipeView()
    {
        InitializeComponent();
    }

    private void ToggleHistory_Click(object sender, RoutedEventArgs e)
    {
        _isHistoryExpanded = !_isHistoryExpanded;
        RecipeFilesPanel.Visibility = _isHistoryExpanded ? Visibility.Collapsed : Visibility.Visible;
        RecipeEditorPanel.Visibility = _isHistoryExpanded ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(HistoryPanel, _isHistoryExpanded ? 0 : 1);
        Grid.SetColumn(HistoryPanel, _isHistoryExpanded ? 0 : 1);
        Grid.SetRowSpan(HistoryPanel, _isHistoryExpanded ? 2 : 1);
        Grid.SetColumnSpan(HistoryPanel, _isHistoryExpanded ? 2 : 1);
        HistoryPanel.Margin = _isHistoryExpanded ? new Thickness(0) : new Thickness(12, 12, 0, 0);
        HistoryExpandButton.Content = _isHistoryExpanded ? "COLLAPSE" : "EXPAND";
        e.Handled = true;
    }

    private void RecipePreview_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement preview || preview.ActualWidth <= 0 || preview.ActualHeight <= 0)
        {
            return;
        }

        var currentScale = RecipePreviewScaleTransform.ScaleX;
        var requestedScale = currentScale + (e.Delta > 0 ? PreviewZoomStep : -PreviewZoomStep);
        var nextScale = Math.Clamp(requestedScale, PreviewZoomMinimum, PreviewZoomMaximum);
        if (Math.Abs(nextScale - currentScale) < double.Epsilon)
        {
            e.Handled = true;
            return;
        }

        var pointer = e.GetPosition(preview);
        var currentOrigin = RecipePreviewZoomContent.RenderTransformOrigin;
        var nextOrigin = new Point(Math.Clamp(pointer.X / preview.ActualWidth, 0.0, 1.0), Math.Clamp(pointer.Y / preview.ActualHeight, 0.0, 1.0));
        var nextTranslationX = GetPreviewZoomTranslation(pointer.X, currentScale, nextScale, currentOrigin.X * preview.ActualWidth, nextOrigin.X * preview.ActualWidth, RecipePreviewTranslateTransform.X);
        var nextTranslationY = GetPreviewZoomTranslation(pointer.Y, currentScale, nextScale, currentOrigin.Y * preview.ActualHeight, nextOrigin.Y * preview.ActualHeight, RecipePreviewTranslateTransform.Y);
        RecipePreviewZoomContent.RenderTransformOrigin = nextOrigin;
        RecipePreviewScaleTransform.ScaleX = nextScale;
        RecipePreviewScaleTransform.ScaleY = nextScale;
        if (nextScale <= PreviewZoomMinimum)
        {
            RecipePreviewTranslateTransform.X = 0;
            RecipePreviewTranslateTransform.Y = 0;
        }
        else
        {
            RecipePreviewTranslateTransform.X = nextTranslationX;
            RecipePreviewTranslateTransform.Y = nextTranslationY;
            ClampRecipePreviewTranslation(preview);
        }

        e.Handled = true;
    }

    private static double GetPreviewZoomTranslation(double pointer, double currentScale, double nextScale, double currentOrigin, double nextOrigin, double currentTranslation)
    {
        var contentCoordinate = (pointer - ((1.0 - currentScale) * currentOrigin) - currentTranslation) / currentScale;
        return pointer - (nextScale * contentCoordinate) - ((1.0 - nextScale) * nextOrigin);
    }

    private void RecipePreview_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement preview || RecipePreviewScaleTransform.ScaleX <= PreviewZoomMinimum || FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (!preview.CaptureMouse())
        {
            return;
        }

        _isRecipePreviewDragging = true;
        _recipePreviewDragStart = e.GetPosition(preview);
        _recipePreviewDragStartX = RecipePreviewTranslateTransform.X;
        _recipePreviewDragStartY = RecipePreviewTranslateTransform.Y;
        preview.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void RecipePreview_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isRecipePreviewDragging || sender is not FrameworkElement preview)
        {
            return;
        }

        var current = e.GetPosition(preview);
        RecipePreviewTranslateTransform.X = _recipePreviewDragStartX + current.X - _recipePreviewDragStart.X;
        RecipePreviewTranslateTransform.Y = _recipePreviewDragStartY + current.Y - _recipePreviewDragStart.Y;
        ClampRecipePreviewTranslation(preview);
        e.Handled = true;
    }

    private void RecipePreview_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRecipePreviewDragging || sender is not FrameworkElement preview)
        {
            return;
        }

        EndRecipePreviewDrag(preview);
        e.Handled = true;
    }

    private void RecipePreview_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        HideRecipePreviewHoverTip();
        EndRecipePreviewDrag(RecipePreviewViewport);
        if (RecipePreviewViewport.ContextMenu is not { } contextMenu)
        {
            return;
        }

        contextMenu.PlacementTarget = RecipePreviewViewport;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void RecipePreview_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement preview)
        {
            EndRecipePreviewDrag(preview);
        }
    }

    private void RecipePreviewResize_Click(object sender, RoutedEventArgs e)
    {
        ResetRecipePreviewZoom();
        e.Handled = true;
    }

    private void CellShotMap_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement preview || preview.ActualWidth <= 0 || preview.ActualHeight <= 0)
        {
            return;
        }

        var currentScale = CellShotMapScaleTransform.ScaleX;
        var requestedScale = currentScale + (e.Delta > 0 ? PreviewZoomStep : -PreviewZoomStep);
        var nextScale = Math.Clamp(requestedScale, PreviewZoomMinimum, PreviewZoomMaximum);
        if (Math.Abs(nextScale - currentScale) < double.Epsilon)
        {
            e.Handled = true;
            return;
        }

        var pointer = e.GetPosition(preview);
        var currentOrigin = CellShotMapZoomContent.RenderTransformOrigin;
        var nextOrigin = new Point(Math.Clamp(pointer.X / preview.ActualWidth, 0.0, 1.0), Math.Clamp(pointer.Y / preview.ActualHeight, 0.0, 1.0));
        var nextTranslationX = GetPreviewZoomTranslation(pointer.X, currentScale, nextScale, currentOrigin.X * preview.ActualWidth, nextOrigin.X * preview.ActualWidth, CellShotMapTranslateTransform.X);
        var nextTranslationY = GetPreviewZoomTranslation(pointer.Y, currentScale, nextScale, currentOrigin.Y * preview.ActualHeight, nextOrigin.Y * preview.ActualHeight, CellShotMapTranslateTransform.Y);
        CellShotMapZoomContent.RenderTransformOrigin = nextOrigin;
        CellShotMapScaleTransform.ScaleX = nextScale;
        CellShotMapScaleTransform.ScaleY = nextScale;
        if (nextScale <= PreviewZoomMinimum)
        {
            CellShotMapTranslateTransform.X = 0;
            CellShotMapTranslateTransform.Y = 0;
        }
        else
        {
            CellShotMapTranslateTransform.X = nextTranslationX;
            CellShotMapTranslateTransform.Y = nextTranslationY;
            ClampCellShotMapTranslation(preview);
        }

        e.Handled = true;
    }

    private void CellShotMap_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        HideCellShotMapHoverTip();
        if (CellShotMapViewport.ContextMenu is not { } contextMenu)
        {
            return;
        }

        contextMenu.PlacementTarget = CellShotMapViewport;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void CellShotMapResize_Click(object sender, RoutedEventArgs e)
    {
        ResetCellShotMapZoom();
        e.Handled = true;
    }

    private void EndRecipePreviewDrag(FrameworkElement preview)
    {
        _isRecipePreviewDragging = false;
        if (Mouse.Captured == preview)
        {
            preview.ReleaseMouseCapture();
        }

        preview.Cursor = Cursors.Hand;
    }

    private void ClampRecipePreviewTranslation(FrameworkElement preview)
    {
        var scale = RecipePreviewScaleTransform.ScaleX;
        var overflowRatio = Math.Max(0.0, scale - PreviewZoomMinimum);
        var origin = RecipePreviewZoomContent.RenderTransformOrigin;
        var minX = -preview.ActualWidth * (1.0 - origin.X) * overflowRatio;
        var maxX = preview.ActualWidth * origin.X * overflowRatio;
        var minY = -preview.ActualHeight * (1.0 - origin.Y) * overflowRatio;
        var maxY = preview.ActualHeight * origin.Y * overflowRatio;
        RecipePreviewTranslateTransform.X = Math.Clamp(RecipePreviewTranslateTransform.X, minX, maxX);
        RecipePreviewTranslateTransform.Y = Math.Clamp(RecipePreviewTranslateTransform.Y, minY, maxY);
    }

    private void ClampCellShotMapTranslation(FrameworkElement preview)
    {
        var scale = CellShotMapScaleTransform.ScaleX;
        var overflowRatio = Math.Max(0.0, scale - PreviewZoomMinimum);
        var origin = CellShotMapZoomContent.RenderTransformOrigin;
        var minX = -preview.ActualWidth * (1.0 - origin.X) * overflowRatio;
        var maxX = preview.ActualWidth * origin.X * overflowRatio;
        var minY = -preview.ActualHeight * (1.0 - origin.Y) * overflowRatio;
        var maxY = preview.ActualHeight * origin.Y * overflowRatio;
        CellShotMapTranslateTransform.X = Math.Clamp(CellShotMapTranslateTransform.X, minX, maxX);
        CellShotMapTranslateTransform.Y = Math.Clamp(CellShotMapTranslateTransform.Y, minY, maxY);
    }

    private void ResetRecipePreviewZoom()
    {
        HideRecipePreviewHoverTip();
        EndRecipePreviewDrag(RecipePreviewViewport);
        RecipePreviewScaleTransform.ScaleX = PreviewZoomMinimum;
        RecipePreviewScaleTransform.ScaleY = PreviewZoomMinimum;
        RecipePreviewTranslateTransform.X = 0;
        RecipePreviewTranslateTransform.Y = 0;
        RecipePreviewZoomContent.RenderTransformOrigin = new Point(0.5, 0.5);
    }

    private void ResetCellShotMapZoom()
    {
        HideCellShotMapHoverTip();
        CellShotMapScaleTransform.ScaleX = PreviewZoomMinimum;
        CellShotMapScaleTransform.ScaleY = PreviewZoomMinimum;
        CellShotMapTranslateTransform.X = 0;
        CellShotMapTranslateTransform.Y = 0;
        CellShotMapZoomContent.RenderTransformOrigin = new Point(0.5, 0.5);
    }

    private void CellContentScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
            return;

        var canScrollUp = e.Delta > 0 && scrollViewer.VerticalOffset > 0;
        var canScrollDown = e.Delta < 0 && scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;
        if (!canScrollUp && !canScrollDown)
            return;

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta / 3.0));
        e.Handled = true;
    }

    private void ToggleCellPlacement_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CRootView root && root.CurrentScreen.Recipe is CMenuRecipe recipe)
        {
            recipe.ToggleCellPlacementExpanded();
            CellParameterPanel.Visibility = recipe.IsCellPlacementExpanded ? Visibility.Collapsed : Visibility.Visible;
            if (sender is Button button)
            {
                button.Content = recipe.CellPlacementExpandButtonText;
            }
        }

        e.Handled = true;
    }

    private void OpenCellPlacementWizard_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not CRootView root || root.CurrentScreen.Recipe is not CMenuRecipe recipe)
        {
            return;
        }

        if (!CanEditCurrentRecipe())
        {
            return;
        }

        var dialog = new CCellPlacementWizardDialog(recipe.CreateCellPlacementWizardContext())
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            recipe.ApplyCellPlacementWizard(dialog.Result);
        }
    }

    private void OpenManagedItemInput(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ST_RECIPE_PARAMETER_EDITOR_ITEM item })
            return;
        e.Handled = true;
        if (!CanEditCurrentRecipe())
            return;
        var dialog = new CValueInputDialog(item.Item, item.Value, item.DataType, item.Min, item.Max)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
            item.Value = dialog.ResultValue;
    }

    private void OpenCellOverviewInput(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ST_RECIPE_CELL_OVERVIEW_ROW row, Tag: string field })
            return;
        e.Handled = true;
        if (!CanEditCurrentRecipe())
            return;
        var value = field switch
        {
            "FirstX" => row.FirstX,
            "FirstY" => row.FirstY,
            "Rotation" => row.Rotation,
            _ => ""
        };
        var parameterName = field switch
        {
            "FirstX" => "ALIGN_TO_1ST_PIXEL_X",
            "FirstY" => "ALIGN_TO_1ST_PIXEL_Y",
            "Rotation" => "ROTATION",
            _ => ""
        };
        var item = row.GetItem(parameterName);
        var dialog = new CValueInputDialog(item?.Item ?? field, value, item?.DataType ?? EN_RECIPE_DATA_TYPE.Double, item?.Min ?? 0.0, item?.Max ?? 0.0)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true)
            return;
        switch (field)
        {
            case "FirstX":
                row.FirstX = dialog.ResultValue;
                break;
            case "FirstY":
                row.FirstY = dialog.ResultValue;
                break;
            case "Rotation":
                row.Rotation = dialog.ResultValue;
                break;
        }
    }

    private void OpenShotGroupOffsetInput(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ST_SHOT_GROUP_EDITOR_ROW row } element)
            return;
        var field = element.Uid;
        e.Handled = true;
        if (!CanEditCurrentRecipe())
            return;
        var value = field == "OffsetX" ? row.OffsetX : row.OffsetY;
        var dialog = new CValueInputDialog(field, value, EN_RECIPE_DATA_TYPE.Double, 0, 0)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true)
            return;
        if (field == "OffsetX")
            row.OffsetX = dialog.ResultValue;
        else
            row.OffsetY = dialog.ResultValue;
    }

    private bool CanEditCurrentRecipe()
    {
        return DataContext is CRootView root && root.CurrentScreen.Recipe?.IsViewRecipeSelected == true;
    }

    private void SelectShotGroupFromMap(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement map || DataContext is not CRootView root || root.CurrentScreen.Recipe is not CMenuRecipe recipe)
        {
            return;
        }

        var point = e.GetPosition(map);
        recipe.SelectShotGroupAt(point.X, point.Y);
        e.Handled = true;
    }

    private void CellPreviewLabel_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ST_CELL_PREVIEW_LABEL label })
        {
            HideRecipePreviewHoverTip();
            return;
        }

        RecipePreviewHoverTipText.Text = label.DisplayText;
        RecipePreviewHoverTip.Visibility = Visibility.Visible;
        PositionRecipePreviewHoverTip(e.GetPosition(RecipePreviewViewport));
    }

    private void CellPreviewLabel_MouseLeave(object sender, MouseEventArgs e)
    {
        HideRecipePreviewHoverTip();
    }

    private void PositionRecipePreviewHoverTip(Point pointer)
    {
        const double margin = 4.0;
        const double offsetX = 12.0;
        const double offsetY = 14.0;
        RecipePreviewHoverTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tipSize = RecipePreviewHoverTip.DesiredSize;
        var x = pointer.X + offsetX;
        var y = pointer.Y + offsetY;
        if (RecipePreviewViewport.ActualWidth > 0)
        {
            x = Math.Clamp(x, margin, Math.Max(margin, RecipePreviewViewport.ActualWidth - tipSize.Width - margin));
        }

        if (RecipePreviewViewport.ActualHeight > 0)
        {
            y = Math.Clamp(y, margin, Math.Max(margin, RecipePreviewViewport.ActualHeight - tipSize.Height - margin));
        }

        RecipePreviewHoverTipTranslateTransform.X = x;
        RecipePreviewHoverTipTranslateTransform.Y = y;
    }

    private void HideRecipePreviewHoverTip()
    {
        RecipePreviewHoverTip.Visibility = Visibility.Collapsed;
        RecipePreviewHoverTipText.Text = "";
    }

    private void CellShotMapSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement map || DataContext is not CRootView root || root.CurrentScreen.Recipe is not CMenuRecipe recipe)
        {
            HideCellShotMapHoverTip();
            return;
        }

        var point = e.GetPosition(map);
        var shotName = recipe.GetShotGroupNameAt(point.X, point.Y);
        if (string.IsNullOrWhiteSpace(shotName))
        {
            HideCellShotMapHoverTip();
            return;
        }

        CellShotMapHoverTipText.Text = shotName;
        CellShotMapHoverTip.Visibility = Visibility.Visible;
        PositionCellShotMapHoverTip(e.GetPosition(CellShotMapViewport));
    }

    private void CellShotMapSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        HideCellShotMapHoverTip();
    }

    private void PositionCellShotMapHoverTip(Point pointer)
    {
        const double margin = 4.0;
        const double offsetX = 12.0;
        const double offsetY = 14.0;
        CellShotMapHoverTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tipSize = CellShotMapHoverTip.DesiredSize;
        var x = pointer.X + offsetX;
        var y = pointer.Y + offsetY;
        if (CellShotMapViewport.ActualWidth > 0)
        {
            x = Math.Clamp(x, margin, Math.Max(margin, CellShotMapViewport.ActualWidth - tipSize.Width - margin));
        }

        if (CellShotMapViewport.ActualHeight > 0)
        {
            y = Math.Clamp(y, margin, Math.Max(margin, CellShotMapViewport.ActualHeight - tipSize.Height - margin));
        }

        CellShotMapHoverTipTranslateTransform.X = x;
        CellShotMapHoverTipTranslateTransform.Y = y;
    }

    private void HideCellShotMapHoverTip()
    {
        CellShotMapHoverTip.Visibility = Visibility.Collapsed;
        CellShotMapHoverTipText.Text = "";
    }

    private void RecipeView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            ResetRecipePreviewZoom();
            ResetCellShotMapZoom();
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = source is Visual || source is Visual3D ? VisualTreeHelper.GetParent(source) : null;
        }

        return null;
    }
}
