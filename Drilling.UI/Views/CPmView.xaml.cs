using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drilling.UI.Popup;

namespace Drilling.UI.Views;
public partial class CPmView : UserControl
{
    private bool _isPmPasswordVisible;

    public CPmView()
    {
        InitializeComponent();
    }

    private void OpenPmPasswordKeyboard(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var dialog = new CPasswordInputDialog(PmPasswordBox.Password)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true)
        {
            PmPasswordBox.Password = dialog.ResultPassword;
            PmPasswordVisibleTextBox.Text = dialog.ResultPassword;
        }
    }

    private void ClearPmPassword(object sender, RoutedEventArgs e)
    {
        PmPasswordBox.Clear();
        PmPasswordVisibleTextBox.Clear();
    }

    private void PmPasswordChanged(object sender, RoutedEventArgs e)
    {
        PmPasswordVisibleTextBox.Text = PmPasswordBox.Password;
    }

    private void TogglePmPasswordVisibility(object sender, RoutedEventArgs e)
    {
        _isPmPasswordVisible = !_isPmPasswordVisible;
        PmPasswordVisibleTextBox.Text = PmPasswordBox.Password;
        PmPasswordBox.Visibility = _isPmPasswordVisible ? Visibility.Collapsed : Visibility.Visible;
        PmPasswordVisibleTextBox.Visibility = _isPmPasswordVisible ? Visibility.Visible : Visibility.Collapsed;
        PmPasswordVisibilitySlash.Visibility = _isPmPasswordVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UnlockPm(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CRootView root)
            return;
        if (await root.UnlockPm(PmPasswordBox.Password))
        {
            PmPasswordBox.Clear();
            PmPasswordVisibleTextBox.Clear();
        }
    }
}
