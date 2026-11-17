using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Drilling.Common.Review;

namespace Drilling.UI.Popup;

public partial class CColorPickerDialog : Window
{
    public CColorPickerDialog(string parameter, string value)
    {
        InitializeComponent();
        PromptText.Text = parameter;
        HexTextBox.Text = CReviewResultLevelSettings.NormalizeColor(value);
        HexTextBox.SelectAll();
        HexTextBox.Focus();
    }

    public string ResultColor { get; private set; } = CReviewResultLevelSettings.DefaultLevel1Color;

    private void PaletteClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: string color })
        {
            HexTextBox.Text = color;
            HexTextBox.SelectAll();
        }
    }

    private void HexTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (TryReadColor(out var color, out _))
        {
            PreviewBorder.Background = new SolidColorBrush(color);
            ValidationText.Text = "사용할 색상을 확인한 뒤 APPLY를 누르세요.";
            ValidationText.Foreground = FindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            return;
        }

        PreviewBorder.Background = Brushes.Transparent;
        ValidationText.Text = "#RRGGBB 형식의 색상값을 입력하세요.";
        ValidationText.Foreground = FindResource("ButtonDangerBrush") as Brush ?? Brushes.Red;
    }

    private void ApplyClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryReadColor(out _, out var normalized))
        {
            return;
        }

        ResultColor = normalized;
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }

    private void WindowKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            DialogResult = false;
        }
        else if (eventArgs.Key == Key.Enter)
        {
            ApplyClick(sender, eventArgs);
        }
    }

    private bool TryReadColor(out Color color, out string normalized)
    {
        color = Colors.Transparent;
        normalized = "";
        try
        {
            normalized = CReviewResultLevelSettings.NormalizeColor(HexTextBox.Text);
            color = (Color)ColorConverter.ConvertFromString(normalized);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
