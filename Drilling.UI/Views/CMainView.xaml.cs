using System.Windows;
using System.Windows.Controls;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Popup;

namespace Drilling.UI.Views;
public partial class CMainView : UserControl
{
    public CMainView()
    {
        InitializeComponent();
    }

    private void OpenInterlockDetailsDialog(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ST_INTERLOCK_GROUP group }) return;
        var dialog = new CInterlockDetailsDialog(group) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

}
