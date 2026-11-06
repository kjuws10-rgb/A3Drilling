using System.Windows.Controls;

namespace Drilling.UI.Views;

public partial class CAlignDistortionView : UserControl
{
    public CAlignDistortionView() => InitializeComponent();
    public CAlignDistortionView(bool previewOnly) : this()
    {
        if (!previewOnly) return;
        ActionRegion.Visibility = System.Windows.Visibility.Collapsed;
        ActionColumn.Width = new System.Windows.GridLength(0);
        GlassPreviewPanel.Margin = new System.Windows.Thickness(0);
    }
}
