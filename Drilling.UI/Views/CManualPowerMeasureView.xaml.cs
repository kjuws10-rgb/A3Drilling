using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drilling.Common.Managers;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Popup;

namespace Drilling.UI.Views;

public partial class CManualPowerMeasureView : UserControl
{
    public CManualPowerMeasureView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            var visibility = DataContext is CManualPowerMeasurePanel { IsReadOnly: true } ? Visibility.Visible : Visibility.Collapsed;
            AutoToleranceColumn.Visibility = visibility;
            AutoAttemptsColumn.Visibility = visibility;
        };
    }

    private void OpenPowerMeasureInput(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowPowerMeasureInput(sender);
    }

    private void PowerMeasureInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        ShowPowerMeasureInput(sender);
    }

    private void ShowPowerMeasureInput(object sender)
    {
        if (sender is not TextBox { DataContext: CPowerMeasureRow row } textBox ||
            DataContext is not CManualPowerMeasurePanel { CanEdit: true } panel) return;

        var binding = textBox.GetBindingExpression(TextBox.TextProperty);
        if (binding is null) return;
        var isPower = binding.ParentBinding.Path.Path == nameof(CPowerMeasureRow.Target);
        panel.SelectedHead = row;
        var dialog = new CValueInputDialog($"{row.HeadName} · {textBox.Tag}", textBox.Text,
            isPower ? EN_RECIPE_DATA_TYPE.Double : EN_RECIPE_DATA_TYPE.Int, 0,
            isPower ? double.MaxValue : int.MaxValue)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true || !panel.CanEdit) return;
        textBox.SetCurrentValue(TextBox.TextProperty, dialog.ResultValue);
        binding.UpdateSource();
    }

    private void FitHeadRows(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.ActualHeight <= grid.ColumnHeaderHeight) return;
        grid.RowHeight = Math.Clamp(Math.Floor((grid.ActualHeight - grid.ColumnHeaderHeight - 3) / 8), 20, 36);
    }
}
