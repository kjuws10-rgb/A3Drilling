using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drilling.Common.Managers;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Popup;

public partial class CManualHomeSpeedDialog : Window
{
    public CManualHomeSpeedDialog(string controllerName, CManualHomeSpeedPanel panel)
    {
        InitializeComponent();
        DataContext = panel;
        Title = $"{controllerName} · HOME SPEED SET";
        HeadingText.Text = Title;
        Height = Math.Min(190 + 200 * Math.Max(1, Math.Ceiling(panel.Heads.Count / 2.0)), SystemParameters.WorkArea.Height * 0.88);
    }

    private void OpenHomeSpeedInput(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox { DataContext: CManualHomeSpeedAxis row } || !row.CanSet) return;
        e.Handled = true;
        var dialog = new CValueInputDialog($"{row.AxisName} HomeSpeed ({row.SpeedUnit})", row.NewValue, EN_RECIPE_DATA_TYPE.Double, 0, 0)
        { Owner = this };
        if (dialog.ShowDialog() == true) row.NewValue = dialog.ResultValue;
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}
