using System.Windows;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Popup;

public partial class CInterlockDetailsDialog : Window
{
    public CInterlockDetailsDialog(ST_INTERLOCK_GROUP group)
    {
        InitializeComponent();
        Title = $"Interlock / Safety - {group.Item}";
        DataContext = group;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
