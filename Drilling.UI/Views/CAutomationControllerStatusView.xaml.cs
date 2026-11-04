using System.Windows;
using System.Windows.Controls;

namespace Drilling.UI.Views;

public partial class CAutomationControllerStatusView : UserControl
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(CAutomationControllerStatusView), new PropertyMetadata(false));
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }
    public static readonly DependencyProperty TaskColumnsProperty = DependencyProperty.Register(
        nameof(TaskColumns), typeof(int), typeof(CAutomationControllerStatusView), new PropertyMetadata(2),
        value => value is int columns && columns > 0);
    public int TaskColumns
    {
        get => (int)GetValue(TaskColumnsProperty);
        set => SetValue(TaskColumnsProperty, value);
    }
    public CAutomationControllerStatusView() => InitializeComponent();
}
