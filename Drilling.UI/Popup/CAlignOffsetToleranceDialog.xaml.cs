using System.Windows;
using System.Windows.Input;
using Drilling.Common.Correction;

namespace Drilling.UI.Popup;

public partial class CAlignOffsetToleranceDialog : Window
{
    public CAlignOffsetToleranceDialog(ST_ALIGN_OFFSET_TOLERANCE_CHECK review)
    {
        InitializeComponent();
        DataContext = review;
    }

    private void OnApplyRemaining(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
