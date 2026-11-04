using System.Windows;
using System.Windows.Controls;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Views;

/// <summary>Shared static FINE geometry/camera renderer. No timers, device reads or inspection commands.</summary>
public partial class CFineGlassPreviewView : UserControl
{
    public CFineGlassPreviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ResizePreview();
        Loaded += (_, _) => ResizePreview();
    }
    private void PreviewSizeChanged(object sender, SizeChangedEventArgs e) => ResizePreview();
    private void PreviewScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0) ResizePreview();
    }
    private void ResizePreview()
    {
        if (DataContext is CFineCorrectionPanel panel && FinePreviewScroll is not null)
            panel.ResizePreview(FinePreviewScroll.ViewportWidth > 0 ? FinePreviewScroll.ViewportWidth : FinePreviewScroll.ActualWidth,
                FinePreviewScroll.ViewportHeight > 0 ? FinePreviewScroll.ViewportHeight : FinePreviewScroll.ActualHeight);
    }
}
