using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Reflection;

namespace Drilling.UI.Popup;
public partial class CPasswordInputDialog : Window
{
    private static readonly MethodInfo? SelectPasswordText = typeof(PasswordBox).GetMethod(
        "Select",
        BindingFlags.Instance | BindingFlags.NonPublic);

    public CPasswordInputDialog(string password = "")
    {
        InitializeComponent();
        PasswordInput.Password = password;
        BuildKeys();
        Loaded += (_, _) => MoveCaretToEnd();
    }

    public string ResultPassword
    {
        get
        {
            return PasswordInput.Password;
        }
    }

    private void BuildKeys()
    {
        string SelectItem(char value)
        {
            return value.ToString();
        }

        foreach (var key in "1234567890QWERTYUIOPASDFGHJKLZXCVBNM_-./:".Select(SelectItem))
        {
            var button = new Button
            {
                Content = key,
                Tag = key,
                Style = (Style)FindResource("KeyButton")
            };
            button.Click += KeyClick;
            KeyGrid.Children.Add(button);
        }
    }

    private void KeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
        {
            PasswordInput.Password += key;
            MoveCaretToEnd();
        }
    }

    private void SpaceClick(object sender, RoutedEventArgs e)
    {
        PasswordInput.Password += " ";
        MoveCaretToEnd();
    }

    private void BackClick(object sender, RoutedEventArgs e)
    {
        if (PasswordInput.Password.Length > 0)
        {
            PasswordInput.Password = PasswordInput.Password[..^1];
            MoveCaretToEnd();
        }
    }

    private void ClearClick(object sender, RoutedEventArgs e)
    {
        PasswordInput.Clear();
        MoveCaretToEnd();
    }

    private void MoveCaretToEnd()
    {
        PasswordInput.Focus();
        SelectPasswordText?.Invoke(PasswordInput, new object[] { PasswordInput.Password.Length, 0 });
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DialogResult = true;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }
}
