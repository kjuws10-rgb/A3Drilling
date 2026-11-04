using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Drilling.Common.Managers;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Popup;

namespace Drilling.UI.Views;
public partial class CMonitorView : UserControl
{
    private Window? _hostWindow;
    private CRootView? _rootView;
    private CMenuMonitor? _monitorMenu;

    public CMonitorView()
    {
        InitializeComponent();
    }

    private void MonitorViewLoaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (!ReferenceEquals(_hostWindow, window))
        {
            if (_hostWindow is not null)
            {
                _hostWindow.PreviewKeyDown -= MonitorViewPreviewKeyDown;
            }

            _hostWindow = window;
            if (_hostWindow is not null)
            {
                _hostWindow.PreviewKeyDown += MonitorViewPreviewKeyDown;
            }
        }

        var rootView = DataContext as CRootView ?? _hostWindow?.DataContext as CRootView;
        if (ReferenceEquals(_rootView, rootView))
        {
            return;
        }

        if (_rootView is not null)
        {
            _rootView.CurrentScreenChanged -= RootViewCurrentScreenChanged;
        }

        _rootView = rootView;
        if (_rootView is not null)
        {
            _rootView.CurrentScreenChanged += RootViewCurrentScreenChanged;
            _monitorMenu = _rootView.CurrentScreen.Monitor ?? _monitorMenu;
        }
    }

    private void RootViewCurrentScreenChanged(object? sender, EventArgs e)
    {
        if (_rootView is null)
        {
            return;
        }

        if (_rootView.CurrentScreen.Menu != EN_MENU.Monitor)
        {
            _monitorMenu?.HideAndLockMelsecWrite();
            return;
        }

        _monitorMenu = _rootView.CurrentScreen.Monitor ?? _monitorMenu;
    }

    private void MonitorViewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var rootView = DataContext as CRootView ?? _hostWindow?.DataContext as CRootView;
        var requiredModifiers = ModifierKeys.Control | ModifierKeys.Shift;
        if (e.Key != Key.F12 ||
            (Keyboard.Modifiers & requiredModifiers) != requiredModifiers ||
            rootView?.CurrentScreen.Monitor is not CMenuMonitor monitor)
        {
            return;
        }

        monitor.ToggleMelsecWriteLockVisibility();
        e.Handled = true;
    }

    private void MonitorViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewKeyDown -= MonitorViewPreviewKeyDown;
            _hostWindow = null;
        }

        if (_rootView is not null)
        {
            _rootView.CurrentScreenChanged -= RootViewCurrentScreenChanged;
            _rootView = null;
        }

        _monitorMenu?.HideAndLockMelsecWrite();
        _monitorMenu = null;
    }

    private void XpsMultiMoveGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = XpsOperationScrollViewer;
        var canScrollUp = e.Delta > 0 && scrollViewer.VerticalOffset > 0;
        var canScrollDown = e.Delta < 0 && scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;
        if (!canScrollUp && !canScrollDown)
        {
            return;
        }

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta / 3.0));
        e.Handled = true;
    }

    private void OuterScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var canScrollUp = e.Delta > 0 && scrollViewer.VerticalOffset > 0;
        var canScrollDown = e.Delta < 0 && scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;
        if (!canScrollUp && !canScrollDown)
        {
            return;
        }

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta / 3.0));
        e.Handled = true;
    }

    private void CoordinateCellPreviewLabel_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ST_CELL_PREVIEW_LABEL label })
        {
            HideCoordinateGlassPreviewHoverTip();
            return;
        }

        CoordinateGlassPreviewHoverTipText.Text = label.DisplayText;
        CoordinateGlassPreviewHoverTip.Visibility = Visibility.Visible;
        PositionCoordinateGlassPreviewHoverTip(e.GetPosition(CoordinateGlassPreviewViewport));
    }

    private void CoordinateCellPreviewLabel_MouseLeave(object sender, MouseEventArgs e)
    {
        HideCoordinateGlassPreviewHoverTip();
    }

    private void PositionCoordinateGlassPreviewHoverTip(Point pointer)
    {
        const double margin = 4.0;
        const double offsetX = 12.0;
        const double offsetY = 14.0;
        CoordinateGlassPreviewHoverTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tipSize = CoordinateGlassPreviewHoverTip.DesiredSize;
        var x = pointer.X + offsetX;
        var y = pointer.Y + offsetY;
        if (CoordinateGlassPreviewViewport.ActualWidth > 0)
        {
            x = Math.Clamp(x, margin, Math.Max(margin, CoordinateGlassPreviewViewport.ActualWidth - tipSize.Width - margin));
        }

        if (CoordinateGlassPreviewViewport.ActualHeight > 0)
        {
            y = Math.Clamp(y, margin, Math.Max(margin, CoordinateGlassPreviewViewport.ActualHeight - tipSize.Height - margin));
        }

        CoordinateGlassPreviewHoverTipTranslateTransform.X = x;
        CoordinateGlassPreviewHoverTipTranslateTransform.Y = y;
    }

    private void HideCoordinateGlassPreviewHoverTip()
    {
        CoordinateGlassPreviewHoverTip.Visibility = Visibility.Collapsed;
        CoordinateGlassPreviewHoverTipText.Text = "";
    }

    private void AttenuatorScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || sender is not DependencyObject root)
        {
            return;
        }

        CloseOpenComboBoxes(root);
    }

    private void MelsecGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.Columns.Count == 0)
        {
            return;
        }

        var fixedColumnsWidth = 0.0;
        for (var index = 1; index < grid.Columns.Count; index++)
        {
            fixedColumnsWidth += grid.Columns[index].Width.Value;
        }

        const double minimumIdWidth = 160.0;
        var availableIdWidth = grid.ActualWidth
                               - fixedColumnsWidth
                               - SystemParameters.VerticalScrollBarWidth
                               - 2.0;
        grid.Columns[0].Width = new DataGridLength(Math.Max(minimumIdWidth, availableIdWidth));
    }

    private static void CloseOpenComboBoxes(DependencyObject root)
    {
        if (root is ComboBox { IsDropDownOpen: true } comboBox)
        {
            comboBox.IsDropDownOpen = false;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            CloseOpenComboBoxes(VisualTreeHelper.GetChild(root, index));
        }
    }

    private void OpenPowerMeterSettingInput(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ST_PWM_SETTING_ROW row } || row.UsesSelectionEditor)
        {
            return;
        }

        e.Handled = true;
        var monitor = GetMonitorMenu();
        if (monitor?.TryNotifyPowerMeterTuneLocked("SETTING 변경") == true)
        {
            return;
        }

        var dialog = new CValueInputDialog(row.Parameter, row.Value, row.DataType, 0, 0)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            row.Value = dialog.ResultValue;
        }
    }

    private void PowerMeterSettingSelection_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var monitor = GetMonitorMenu();
        if (monitor?.TryNotifyPowerMeterTuneLocked("SETTING 변경") == true)
        {
            e.Handled = true;
        }
    }

    private void PowerMeterSettingSelection_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsPowerMeterSettingSelectionKey(e.Key))
        {
            return;
        }

        var monitor = GetMonitorMenu();
        if (monitor?.TryNotifyPowerMeterTuneLocked("SETTING 변경") == true)
        {
            e.Handled = true;
        }
    }

    private void PowerMeterSettingSelection_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var monitor = GetMonitorMenu();
        if (monitor?.TryNotifyPowerMeterTuneLocked("SETTING 변경") == true)
        {
            e.Handled = true;
        }
    }

    private void PowerMeterSettingSelection_DropDownOpened(object sender, EventArgs e)
    {
        var monitor = GetMonitorMenu();
        if (monitor?.TryNotifyPowerMeterTuneLocked("SETTING 변경") != true || sender is not ComboBox comboBox)
        {
            return;
        }

        comboBox.IsDropDownOpen = false;
    }

    private static bool IsPowerMeterSettingSelectionKey(Key key)
    {
        switch (key)
        {
            case Key.Enter:
            case Key.Space:
            case Key.Up:
            case Key.Down:
            case Key.PageUp:
            case Key.PageDown:
            case Key.Home:
            case Key.End:
                return true;
            default:
                return false;
        }
    }

    private void OpenLaserSettingInput(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ST_MONITOR_LASER_CONTROL_ROW row } button || !row.CanCommand)
        {
            return;
        }

        e.Handled = true;
        var dialog = new CValueInputDialog(row.CurrentLabel, row.Setting.Value, EN_RECIPE_DATA_TYPE.Int, 0, int.MaxValue)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            row.Setting.Value = dialog.ResultValue;
            button.Content = dialog.ResultValue;
        }
    }

    private void OpenMotorOperationInput(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ST_MONITOR_PARAMETER_ROW row } button)
        {
            return;
        }

        e.Handled = true;
        var dialog = new CValueInputDialog(row.Parameter, row.Value, EN_RECIPE_DATA_TYPE.Double, 0, 0)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            row.Value = dialog.ResultValue;
            button.Content = dialog.ResultValue;
        }
    }

    private void OpenXpsMultiMoveTargetInput(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ST_MONITOR_MULTI_MOVE_AXIS_ROW row })
        {
            return;
        }

        e.Handled = true;
        var dialog = new CValueInputDialog($"Axis {row.Axis} Target Position", row.TargetPosition, EN_RECIPE_DATA_TYPE.Double, 0, 0)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            row.TargetPosition = dialog.ResultValue;
        }
    }

    private async void StartXpsJogNegative(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await ExecuteXpsJogEvent(positive: false);
    }

    private async void StartXpsJogPositive(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await ExecuteXpsJogEvent(positive: true);
    }

    private async void StopXpsJog(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await ExecuteXpsJogEvent(positive: null);
    }

    private async void StopXpsJogOnMouseLeave(object sender, MouseEventArgs e)
    {
        await ExecuteXpsJogEvent(positive: null);
    }

    private async void StopXpsJogOnLostMouseCapture(object sender, MouseEventArgs e)
    {
        await ExecuteXpsJogEvent(positive: null);
    }

    private async void StopXpsJogOnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        await ExecuteXpsJogEvent(positive: null);
    }

    private async Task ExecuteXpsJogEvent(bool? positive)
    {
        if (GetMonitorMenu()is not { } monitor)
        {
            return;
        }

        try
        {
            if (positive.HasValue)
            {
                await monitor.StartXpsJog(positive.Value);
            }
            else
            {
                await monitor.StopXpsJog();
            }
        }
        catch (OperationCanceledException)when (positive.HasValue)
        {
        // Releasing the hold-to-run button normally cancels an in-flight START.
        }
        catch (Exception exception)
        {
            monitor.ReportXpsJogFailure(exception);
        }
    }

    private CMenuMonitor? GetMonitorMenu()
    {
        return DataContext is CRootView rootView ? rootView.CurrentScreen.Monitor : null;
    }

    private void OpenAttenuatorPositionInput(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ST_MONITOR_PARAMETER_ROW row } button)
        {
            return;
        }

        e.Handled = true;
        var parameter = row.Parameter ?? string.Empty;
        var dataType = string.Equals(parameter, "Home Type", StringComparison.OrdinalIgnoreCase) ? EN_RECIPE_DATA_TYPE.Int : EN_RECIPE_DATA_TYPE.Double;
        var dialog = new CValueInputDialog(parameter, row.Value, dataType, 0, 0)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            row.Value = dialog.ResultValue;
            button.Content = dialog.ResultValue;
        }
    }

    private void OpenBetTableInput(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ST_MONITOR_BET_TABLE_ROW row, Tag: string field } button)
        {
            return;
        }

        e.Handled = true;
        var currentValue = field switch
        {
            "DESCRIPTION" => row.Description,
            "DIV" => row.Div,
            "MAG" => row.Mag,
            _ => ""
        };
        var dataType = field == "DESCRIPTION" ? EN_RECIPE_DATA_TYPE.String : EN_RECIPE_DATA_TYPE.Int;
        var max = field is "DIV" or "MAG" ? 4500 : 0;
        var dialog = new CValueInputDialog(field, currentValue, dataType, 0, max)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        switch (field)
        {
            case "DESCRIPTION":
                row.Description = dialog.ResultValue;
                break;
            case "DIV":
                row.Div = dialog.ResultValue;
                break;
            case "MAG":
                row.Mag = dialog.ResultValue;
                break;
        }

        button.Content = dialog.ResultValue;
    }

    private void OpenBetPositionInput(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ST_MONITOR_PARAMETER_ROW row } button)
        {
            return;
        }

        e.Handled = true;
        var dialog = new CValueInputDialog(row.Parameter, row.Value, EN_RECIPE_DATA_TYPE.Int, 0, 4500)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            row.Value = dialog.ResultValue;
            button.Content = dialog.ResultValue;
        }
    }

    private void OpenMelsecWriteInput(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ST_MONITOR_MELSEC_ROW row } || !row.CanWrite || row.UsesWriteSelection)
        {
            return;
        }

        e.Handled = true;
        if (!EnsureMelsecWriteInputUnlocked(e))
        {
            return;
        }

        var dialog = new CValueInputDialog($"{row.Id} Write Value", row.WriteValue, row.WriteInputType, 0, 0)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true && EnsureMelsecWriteInputUnlocked(e))
        {
            row.WriteValue = dialog.ResultValue;
        }
    }

    private void MelsecWriteSelectionPreviewInput(object sender, RoutedEventArgs e)
    {
        // Keep focus navigation available while blocking selection changes.
        if (e is KeyEventArgs key && key.Key is Key.Tab or Key.Escape or
            Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt)
        {
            return;
        }

        EnsureMelsecWriteInputUnlocked(e);
    }

    private bool EnsureMelsecWriteInputUnlocked(RoutedEventArgs e)
    {
        var monitor = GetMonitorMenu();
        if (monitor?.IsMelsecWriteUnlocked == true)
        {
            return true;
        }

        e.Handled = true;
        if (monitor is not null)
        {
            CRecipeConfirmDialog.ShowWarning("MELSEC WRITE LOCK",
                "WRITE LOCK 상태에서는 값을 입력하거나 변경할 수 없습니다.\nWRITE UNLOCK 후 다시 입력해 주세요.",
                Window.GetWindow(this));
        }
        return false;
    }

}
