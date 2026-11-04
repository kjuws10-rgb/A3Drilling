using System.Windows;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Popup;

public partial class CFineLineDetailsDialog : Window
{
    public CFineLineDetailsDialog(CFineInspectionLine line)
    {
        InitializeComponent();
        DataContext = line;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
    }
    public static void Show(CFineInspectionLine line)
    {
        var owner = Application.Current?.MainWindow;
        var dialog = new CFineLineDetailsDialog(line);
        if (owner is not null) dialog.Owner = owner;
        dialog.ShowDialog();
    }
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}
