using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Drilling.Common.Log;

namespace Drilling.UI;

public static class CUiLogService
{
    private static readonly object SyncRoot = new();
    private static CLogManager? _logManager;
    private static bool _isRegistered;

    public static void Configure(CLogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logManager);
        lock (SyncRoot)
        {
            _logManager = logManager;
            if (_isRegistered)
            {
                return;
            }

            EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(OnButtonClicked), true);
            _isRegistered = true;
        }
    }

    public static void WriteClick(string screenName, string controlName, string parameter = "", string detail = "")
    {
        CLogManager? logManager;
        lock (SyncRoot)
        {
            logManager = _logManager;
        }

        if (logManager is null)
        {
            return;
        }

        try
        {
            logManager.WriteUiClick(Normalize(screenName, "UNKNOWN"), Normalize(controlName, "UNKNOWN"), Normalize(parameter, "-"), Normalize(detail, "-"));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void OnButtonClicked(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not ButtonBase button || ShouldSkipButton(button))
        {
            return;
        }

        var screenName = GetScreenName(button);
        var controlName = GetControlName(button);
        var parameter = FormatValue(button.CommandParameter);
        var detail = GetDetail(button);
        WriteClick(screenName, controlName, parameter, detail);
    }

    private static bool ShouldSkipButton(ButtonBase button)
    {
        var window = Window.GetWindow(button);
        if (window is null)
        {
            return false;
        }

        var windowTypeName = window.GetType().Name;
        if (!windowTypeName.Equals("CValueInputDialog", StringComparison.Ordinal) && !windowTypeName.Equals("CPasswordInputDialog", StringComparison.Ordinal))
        {
            return false;
        }

        var content = FormatValue(button.Content);
        if (content.Length == 1 || content.Equals("SPACE", StringComparison.OrdinalIgnoreCase) || content.Equals("BACK", StringComparison.OrdinalIgnoreCase) || content.Equals("CLEAR", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string GetScreenName(FrameworkElement element)
    {
        var window = Window.GetWindow(element);
        if (window is not null && !window.GetType().Name.Equals("CRootView", StringComparison.Ordinal))
        {
            return window.GetType().Name;
        }

        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UserControl control)
            {
                return control.GetType().Name;
            }
        }

        return window?.GetType().Name ?? "UNKNOWN";
    }

    private static string GetControlName(ButtonBase button)
    {
        var name = Normalize(button.Name, "");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var uid = Normalize(button.Uid, "");
        if (!string.IsNullOrWhiteSpace(uid))
        {
            return uid;
        }

        var content = FormatValue(button.Content);
        if (!string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        return button.GetType().Name;
    }

    private static string GetDetail(ButtonBase button)
    {
        var values = new List<string>();
        var tag = FormatValue(button.Tag);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            values.Add($"Tag={tag}");
        }

        var toolTip = FormatValue(button.ToolTip);
        if (!string.IsNullOrWhiteSpace(toolTip))
        {
            values.Add($"ToolTip={toolTip}");
        }

        var dataContext = button.DataContext?.GetType().Name ?? "";
        if (!string.IsNullOrWhiteSpace(dataContext) && !dataContext.Equals("String", StringComparison.Ordinal))
        {
            values.Add($"DataContext={dataContext}");
        }

        return values.Count == 0 ? "-" : string.Join(", ", values);
    }

    private static string FormatValue(object? value)
    {
        if (value is null)
        {
            return "";
        }

        if (value is string text)
        {
            return Normalize(text, "");
        }

        if (value is TextBlock textBlock)
        {
            return Normalize(textBlock.Text, "");
        }

        if (value is FrameworkElement frameworkElement)
        {
            var elementName = Normalize(frameworkElement.Name, "");
            if (!string.IsNullOrWhiteSpace(elementName))
            {
                return elementName;
            }
        }

        if (value is IFormattable formattable)
        {
            return Normalize(formattable.ToString(null, CultureInfo.InvariantCulture) ?? "", "");
        }

        return Normalize(value.ToString() ?? "", "");
    }

    private static string Normalize(string value, string defaultValue)
    {
        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return string.IsNullOrWhiteSpace(normalized) ? defaultValue : normalized;
    }
}
