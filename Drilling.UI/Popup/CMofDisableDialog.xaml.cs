using System.ComponentModel;
using System.Windows;

namespace Drilling.UI.Popup;

public partial class CMofDisableDialog : Window
{
    private readonly IReadOnlyList<CMofAxisSelection> _axes;
    public IReadOnlyList<string> SelectedAxes => _axes.Where(axis => axis.IsSelected).Select(axis => axis.Name).ToArray();

    public CMofDisableDialog(string controllerTitle, IReadOnlyList<string> axes)
    {
        InitializeComponent();
        ControllerTitle.Text = $"{controllerTitle} · MOF Disable";
        _axes = axes.Select(axis => new CMofAxisSelection(axis)).ToArray();
        AxisItems.ItemsSource = _axes;
    }

    private void SelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var axis in _axes) axis.IsSelected = true;
    }

    private void ClearAll(object sender, RoutedEventArgs e)
    {
        foreach (var axis in _axes) axis.IsSelected = false;
    }

    private void Confirm(object sender, RoutedEventArgs e)
    {
        if (SelectedAxes.Count == 0)
        {
            CRecipeConfirmDialog.ShowWarning("MOF Disable", "MOF를 해제할 축을 하나 이상 선택하세요.");
            return;
        }
        DialogResult = true;
    }
}

internal sealed class CMofAxisSelection(string name) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
