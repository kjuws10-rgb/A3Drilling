using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drilling.UI;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Views;
public partial class CManualMofView : UserControl
{
    private const double PreviewZoomMinimum = 1.0;
    private bool _isGlassPreviewDragging;
    private Point _glassPreviewDragStart;
    private double _glassPreviewDragStartX;
    private double _glassPreviewDragStartY;
    public CManualMofView()
    {
        InitializeComponent();
    }

    private void GlassPreview_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HideGlassPreviewHoverTip();
        if (sender is not FrameworkElement preview || GlassPreviewScaleTransform.ScaleX <= PreviewZoomMinimum)
        {
            return;
        }

        if (!preview.CaptureMouse())
        {
            return;
        }

        _isGlassPreviewDragging = true;
        _glassPreviewDragStart = e.GetPosition(preview);
        _glassPreviewDragStartX = GlassPreviewTranslateTransform.X;
        _glassPreviewDragStartY = GlassPreviewTranslateTransform.Y;
        preview.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void GlassPreview_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isGlassPreviewDragging || sender is not FrameworkElement preview)
        {
            return;
        }

        var current = e.GetPosition(preview);
        GlassPreviewTranslateTransform.X = _glassPreviewDragStartX + current.X - _glassPreviewDragStart.X;
        GlassPreviewTranslateTransform.Y = _glassPreviewDragStartY + current.Y - _glassPreviewDragStart.Y;
        ClampGlassPreviewTranslation(preview);
        e.Handled = true;
    }

    private void GlassPreview_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isGlassPreviewDragging || sender is not FrameworkElement preview)
        {
            return;
        }

        EndGlassPreviewDrag(preview);
        e.Handled = true;
    }

    private void GlassPreview_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement preview)
        {
            EndGlassPreviewDrag(preview);
        }
    }

    private void EndGlassPreviewDrag(FrameworkElement preview)
    {
        _isGlassPreviewDragging = false;
        if (Mouse.Captured == preview)
        {
            preview.ReleaseMouseCapture();
        }

        preview.Cursor = Cursors.Hand;
    }

    private void CellPreviewLabel_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ST_CELL_PREVIEW_LABEL label })
        {
            HideGlassPreviewHoverTip();
            return;
        }

        GlassPreviewHoverTipText.Text = label.DisplayText;
        GlassPreviewHoverTip.Visibility = Visibility.Visible;
        PositionGlassPreviewHoverTip(e.GetPosition(GlassPreviewViewport));
    }

    private void CellPreviewLabel_MouseLeave(object sender, MouseEventArgs e)
    {
        HideGlassPreviewHoverTip();
    }

    private void PositionGlassPreviewHoverTip(Point pointer)
    {
        const double margin = 4.0;
        const double offsetX = 12.0;
        const double offsetY = 14.0;
        GlassPreviewHoverTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tipSize = GlassPreviewHoverTip.DesiredSize;
        var x = pointer.X + offsetX;
        var y = pointer.Y + offsetY;
        if (GlassPreviewViewport.ActualWidth > 0)
        {
            x = Math.Clamp(x, margin, Math.Max(margin, GlassPreviewViewport.ActualWidth - tipSize.Width - margin));
        }

        if (GlassPreviewViewport.ActualHeight > 0)
        {
            y = Math.Clamp(y, margin, Math.Max(margin, GlassPreviewViewport.ActualHeight - tipSize.Height - margin));
        }

        GlassPreviewHoverTipTranslateTransform.X = x;
        GlassPreviewHoverTipTranslateTransform.Y = y;
    }

    private void HideGlassPreviewHoverTip()
    {
        GlassPreviewHoverTip.Visibility = Visibility.Collapsed;
        GlassPreviewHoverTipText.Text = "";
    }

    private void ClampGlassPreviewTranslation(FrameworkElement preview)
    {
        var scale = GlassPreviewScaleTransform.ScaleX;
        var overflowRatio = Math.Max(0.0, scale - PreviewZoomMinimum);
        var origin = GlassPreviewZoomContent.RenderTransformOrigin;
        var minX = -preview.ActualWidth * (1.0 - origin.X) * overflowRatio;
        var maxX = preview.ActualWidth * origin.X * overflowRatio;
        var minY = -preview.ActualHeight * (1.0 - origin.Y) * overflowRatio;
        var maxY = preview.ActualHeight * origin.Y * overflowRatio;
        GlassPreviewTranslateTransform.X = Math.Clamp(GlassPreviewTranslateTransform.X, minX, maxX);
        GlassPreviewTranslateTransform.Y = Math.Clamp(GlassPreviewTranslateTransform.Y, minY, maxY);
    }

    private void MofView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not false)
        {
            return;
        }

        EndGlassPreviewDrag(GlassPreviewViewport);
        HideGlassPreviewHoverTip();
        GlassPreviewScaleTransform.ScaleX = PreviewZoomMinimum;
        GlassPreviewScaleTransform.ScaleY = PreviewZoomMinimum;
        GlassPreviewTranslateTransform.X = 0;
        GlassPreviewTranslateTransform.Y = 0;
        GlassPreviewZoomContent.RenderTransformOrigin = new Point(0.5, 0.5);
    }
}
