using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drilling.Common.Managers;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Popup;

public partial class CAlignBaseEditDialog : Window
{
    private readonly CAlignBaseEditPanel _panel;
    private readonly Func<IReadOnlyDictionary<string, string>, Task> _save;
    private bool _saving;

    public CAlignBaseEditDialog(CAlignBaseEditPanel panel, Func<IReadOnlyDictionary<string, string>, Task> save)
    {
        InitializeComponent();
        _panel = panel;
        _save = save;
        DataContext = panel;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
    }

    private void OpenCoordinateInput(object sender, MouseButtonEventArgs e)
    {
        if (_saving || sender is not TextBox { DataContext: CAlignBaseEditKey key } input) return;
        e.Handled = true;
        var isX = input.Tag as string == "X";
        var parameter = isX ? key.ParameterX : key.ParameterY;
        var dialog = new CValueInputDialog($"{key.Name} Base{(isX ? "X" : "Y")} (mm)",
            isX ? key.BaseX : key.BaseY, EN_RECIPE_DATA_TYPE.Double, parameter.Min, parameter.Max) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (isX) key.BaseX = dialog.ResultValue;
        else key.BaseY = dialog.ResultValue;
    }

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        try
        {
            var values = _panel.BuildValues();
            SetSaving(true);
            await _save(values);
        }
        catch (Exception exception)
        {
            SetSaving(false);
            CRecipeConfirmDialog.ShowWarning("Base 저장 확인", exception.Message, this);
            return;
        }
        SetSaving(false);
        DialogResult = true;
    }

    private void SetSaving(bool saving)
    {
        _saving = saving;
        PreviewArea.IsEnabled = !saving;
        SaveButton.IsEnabled = !saving;
        CancelButton.IsEnabled = !saving;
    }

    private void CancelClicked(object sender, RoutedEventArgs e) { if (!_saving) DialogResult = false; }
    private void OnClosing(object? sender, CancelEventArgs e) { if (_saving) e.Cancel = true; }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (!_saving) DialogResult = false;
    }
}
