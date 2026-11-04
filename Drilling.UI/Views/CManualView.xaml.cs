using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drilling.Common.Managers;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Popup;

namespace Drilling.UI.Views;
public partial class CManualView : UserControl
{
    public CManualView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _motionWindow = Window.GetWindow(this);
            if (_motionWindow is not null) _motionWindow.Deactivated += MotionWindowDeactivated;
        };
        Unloaded += (_, _) =>
        {
            ReleaseJog();
            CancelPendingMotion();
            if (_motionWindow is not null) _motionWindow.Deactivated -= MotionWindowDeactivated;
            _motionWindow = null;
        };
    }

    private Window? _motionWindow;
    private Button? _jogButton;
    private CManualMotionAxis? _jogAxis;

    private async void OpenShiftOffsetDialog(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CRootView root) return;
        await root.OpenShiftOffsetDialog(Window.GetWindow(this));
    }

    private void FitManualParameterRows(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.Items.Count == 0 || grid.ActualHeight <= 0 ||
            !double.IsFinite(grid.ColumnHeaderHeight)) return;
        var availableHeight = grid.ActualHeight - grid.ColumnHeaderHeight -
            grid.BorderThickness.Top - grid.BorderThickness.Bottom - 2.0;
        var rowHeight = Math.Max(18.0, Math.Floor(availableHeight / grid.Items.Count * 10.0) / 10.0);
        if (Math.Abs(grid.RowHeight - rowHeight) > 0.01) grid.RowHeight = rowHeight;
    }

    private async void JogPressed(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_jogButton is not null || sender is not Button { DataContext: CManualMotionAxis axis } button || !axis.CanMove) return;
        _jogButton = button;
        _jogAxis = axis;
        if (!button.CaptureMouse()) { ReleaseJog(); return; }
        await axis.StartJog(button.Tag?.ToString() == "-" ? -1 : 1);
    }
    private void JogReleased(object sender, MouseButtonEventArgs e) { e.Handled = true; ReleaseJog(); }
    private void JogCaptureLost(object sender, MouseEventArgs e) => ReleaseJog();
    private void MotionWindowDeactivated(object? sender, EventArgs e)
    {
        ReleaseJog();
        // Our modal confirmation deactivates its owner; keep that pending move valid.
        // Jog release and Head/tab changes still invalidate their own pending commands.
        if (ManualMotionPanel?.Tag is not CManualMotionPanel { IsConfirmationOpen: true }) CancelPendingMotion();
    }
    private void MotionVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) { ReleaseJog(); CancelPendingMotion(); }
    }
    private void CancelPendingMotion()
    {
        if (ManualMotionPanel?.Tag is CManualMotionPanel panel) _ = panel.CancelPendingAndStopJog();
    }
    private void ReleaseJog()
    {
        var axis = _jogAxis;
        var button = _jogButton;
        _jogAxis = null;
        _jogButton = null;
        if (axis is not null) _ = axis.StopJog();
        if (button?.IsMouseCaptured == true) button.ReleaseMouseCapture();
    }

    private void OpenTextInput(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox)
            return;
        e.Handled = true;
        var dataType = textBox.Tag as string == "Int" ? EN_RECIPE_DATA_TYPE.Int : EN_RECIPE_DATA_TYPE.Double;
        var prompt = textBox.ToolTip?.ToString() ?? "Value";
        var dialog = new CValueInputDialog(prompt, textBox.Text, dataType, 0, 0)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true)
            return;
        textBox.Text = dialog.ResultValue;
        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }


    private void OpenManualParameterInput(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ST_MANUAL_PARAMETER row })
            return;
        e.Handled = true;
        var dialog = new CValueInputDialog(row.Parameter, row.Value, row.DataType, row.Min, row.Max)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
            row.Value = dialog.ResultValue;
    }

    private void OpenCommandStateInput(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ST_MANUAL_COMMAND_STATE row } || !row.IsEditable)
            return;
        e.Handled = true;
        var dataType = row.Name.Contains("Count", StringComparison.OrdinalIgnoreCase) ? EN_RECIPE_DATA_TYPE.Int : EN_RECIPE_DATA_TYPE.Double;
        var dialog = new CValueInputDialog(row.Name, row.Value, dataType, 0, 0)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
            row.Value = dialog.ResultValue;
    }

    private void OpenStageTargetInput(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ST_MANUAL_STAGE_AXIS row })
            return;
        e.Handled = true;
        var dialog = new CValueInputDialog($"{row.DisplayName} Pos", row.TargetPosition, EN_RECIPE_DATA_TYPE.Double, 0, 0)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
            row.TargetPosition = dialog.ResultValue;
    }

    private void OpenStageSpeedInput(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ST_MANUAL_STAGE_AXIS row })
            return;
        e.Handled = true;
        var dialog = new CValueInputDialog($"{row.DisplayName} Speed", row.TargetSpeed, EN_RECIPE_DATA_TYPE.Double, 0, 0)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
            row.TargetSpeed = dialog.ResultValue;
    }
}
