using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drilling.Common.Managers;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Popup;

namespace Drilling.UI.Views;

public partial class CManualPowerStageView : UserControl
{
    private Window? _window;
    private Button? _heldJog;
    private CManualPowerStagePanel? Panel => DataContext as CManualPowerStagePanel;
    public CManualPowerStageView() => InitializeComponent();

    private void ViewLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null) _window.Deactivated += WindowDeactivated;
        Panel?.SetViewActive(this, IsVisible);
    }
    private void ViewUnloaded(object sender, RoutedEventArgs e)
    {
        // Releasing a held JOG stops it. Tab/view lifetime alone must not cancel
        // an accepted ABS/REL move or deactivate a visible sibling's shared panel.
        ReleaseJog(); Panel?.SetViewActive(this, false);
        if (_window is not null) _window.Deactivated -= WindowDeactivated;
        _window = null;
    }
    private void ViewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) ReleaseJog();
        Panel?.SetViewActive(this, IsLoaded && IsVisible);
    }
    private void ViewDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ReleaseJog();
        (e.OldValue as CManualPowerStagePanel)?.SetViewActive(this, false);
        Panel?.SetViewActive(this, IsLoaded && IsVisible);
    }
    private void WindowDeactivated(object? sender, EventArgs e) => ReleaseJog();

    private void OpenPositionInput(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CManualPowerStageAxis axis } button || Panel is not { CanEdit: true }) return;
        var tag = button.Tag?.ToString();
        var relative = tag is "REL" or "REL_SPEED";
        var speed = tag is "ABS_SPEED" or "REL_SPEED";
        var current = speed ? (relative ? axis.RelativeSpeed : axis.AbsoluteSpeed) : (relative ? axis.Relative : axis.Absolute);
        var dialog = new CValueInputDialog($"{axis.Name} {(relative ? "REL" : "ABS")} {(speed ? "Speed (mm/sec)" : "(mm)")}",
            current, EN_RECIPE_DATA_TYPE.Double, speed ? double.Epsilon : -double.MaxValue, double.MaxValue)
        { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || Panel?.CanEdit != true) return;
        if (speed)
        {
            if (relative) axis.RelativeSpeed = dialog.ResultValue; else axis.AbsoluteSpeed = dialog.ResultValue;
        }
        else if (relative) axis.Relative = dialog.ResultValue; else axis.Absolute = dialog.ResultValue;
    }

    private void JogMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || _heldJog is not null || Panel?.CanJog != true) return;
        button.Focus();
        if (!button.CaptureMouse()) return;
        _heldJog = button;
        if (Panel.StartJog(button.Tag?.ToString() ?? "") != true) ReleaseJog();
    }
    private void JogMouseUp(object sender, MouseButtonEventArgs e) { e.Handled = true; ReleaseJog(); }
    private void JogLostCapture(object sender, MouseEventArgs e) => ReleaseJog();
    private void JogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter)) return;
        e.Handled = true;
        if (e.IsRepeat || _heldJog is not null || sender is not Button button || Panel?.CanJog != true) return;
        _heldJog = button;
        if (Panel.StartJog(button.Tag?.ToString() ?? "") != true) ReleaseJog();
    }
    private void JogKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter)) return;
        e.Handled = true; ReleaseJog();
    }
    private void JogLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ReleaseJog();
    private void ReleaseJog()
    {
        var button = _heldJog;
        if (button is null) return;
        _heldJog = null;
        Panel?.Stop();
        if (button.IsMouseCaptured) button.ReleaseMouseCapture();
    }
}
