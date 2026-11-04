using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Views;
public partial class CCorrectionView : UserControl
{
    public CCorrectionView()
    {
        InitializeComponent();
    }

    private void OutputViewerCellPreviewLabel_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ST_CELL_PREVIEW_LABEL label })
        {
            HideOutputViewerPreviewHoverTip();
            return;
        }

        OutputViewerPreviewHoverTipText.Text = label.DisplayText;
        OutputViewerPreviewHoverTip.Visibility = Visibility.Visible;
        PositionOutputViewerPreviewHoverTip(e.GetPosition(OutputViewerPreviewViewport));
    }

    private void OutputViewerCellPreviewLabel_MouseLeave(object sender, MouseEventArgs e)
    {
        HideOutputViewerPreviewHoverTip();
    }

    private void PositionOutputViewerPreviewHoverTip(Point pointer)
    {
        const double margin = 4.0;
        const double offsetX = 12.0;
        const double offsetY = 14.0;
        OutputViewerPreviewHoverTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tipSize = OutputViewerPreviewHoverTip.DesiredSize;
        var x = pointer.X + offsetX;
        var y = pointer.Y + offsetY;
        if (OutputViewerPreviewViewport.ActualWidth > 0)
        {
            x = Math.Clamp(x, margin, Math.Max(margin, OutputViewerPreviewViewport.ActualWidth - tipSize.Width - margin));
        }

        if (OutputViewerPreviewViewport.ActualHeight > 0)
        {
            y = Math.Clamp(y, margin, Math.Max(margin, OutputViewerPreviewViewport.ActualHeight - tipSize.Height - margin));
        }

        OutputViewerPreviewHoverTipTranslateTransform.X = x;
        OutputViewerPreviewHoverTipTranslateTransform.Y = y;
    }

    private void HideOutputViewerPreviewHoverTip()
    {
        OutputViewerPreviewHoverTip.Visibility = Visibility.Collapsed;
        OutputViewerPreviewHoverTipText.Text = "";
    }
}
