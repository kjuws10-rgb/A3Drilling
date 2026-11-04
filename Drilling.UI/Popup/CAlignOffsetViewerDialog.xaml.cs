using System.Windows;
using System.Windows.Input;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Popup;

public partial class CAlignOffsetViewerDialog : Window
{
    public CAlignOffsetViewerDialog(CAlignOffsetViewerPanel panel)
    {
        InitializeComponent();
        DataContext = panel;
    }
    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
    private void OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); }
}
