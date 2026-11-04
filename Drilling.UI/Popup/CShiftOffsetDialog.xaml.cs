using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Drilling.UI.Popup;
public partial class CShiftOffsetDialog : Window
{
    private readonly Func<double, double, IReadOnlyList<ST_SHIFT_OFFSET_HEAD_ROW>> _headRowsProvider;
    private readonly Func<double, double, string?> _validateApply;
    private bool _isLoading;
    public CShiftOffsetDialog(
        string shiftOffsetX, 
        string shiftOffsetY, 
        Func<double, double, IReadOnlyList<ST_SHIFT_OFFSET_HEAD_ROW>> headRowsProvider, 
        Func<double, double, string?> validateApply
    )
    {
        InitializeComponent();
        _headRowsProvider = headRowsProvider;
        _validateApply = validateApply;
        _isLoading = true;
        ShiftXTextBox.Text = shiftOffsetX;
        ShiftYTextBox.Text = shiftOffsetY;
        _isLoading = false;
        DataObject.AddPastingHandler(ShiftXTextBox, NumericTextBoxPasting);
        DataObject.AddPastingHandler(ShiftYTextBox, NumericTextBoxPasting);
        Loaded += (_, _) =>
        {
            RefreshPreview();
            ShiftXTextBox.Focus();
            ShiftXTextBox.SelectAll();
        };
    }

    public string ResultShiftOffsetX { get; private set; } = "0";
    public string ResultShiftOffsetY { get; private set; } = "0";

    private void OnShiftInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (!TryReadInputs(out var shiftX, out var shiftY, out var error))
        {
            StatusText.Text = error;
            HeadRowsControl.ItemsSource = Array.Empty<ST_SHIFT_OFFSET_HEAD_ROW>();
            DrawPreview(0.0, 0.0, isValid: false);
            return;
        }

        var rows = _headRowsProvider(shiftX, shiftY);
        HeadRowsControl.ItemsSource = rows;
        DrawPreview(shiftX, shiftY, isValid: true);
        StatusText.Text = $"Design Shift X {Format(shiftX)} mm / Y {Format(shiftY)} mm";
    }

    private bool TryReadInputs(out double shiftX, out double shiftY, out string error)
    {
        error = "";
        if (!TryParseFinite(ShiftXTextBox.Text, out shiftX))
        {
            shiftY = 0.0;
            error = "SHIFT OFFSET X 값을 확인하세요.";
            return false;
        }

        if (!TryParseFinite(ShiftYTextBox.Text, out shiftY))
        {
            error = "SHIFT OFFSET Y 값을 확인하세요.";
            return false;
        }

        return true;
    }

    private static bool TryParseFinite(string? text, out double value)
    {
        return double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private void DrawPreview(double shiftX, double shiftY, bool isValid)
    {
        PreviewCanvas.Children.Clear();
        var width = PreviewCanvas.ActualWidth > 0 ? PreviewCanvas.ActualWidth : 360.0;
        var height = PreviewCanvas.ActualHeight > 0 ? PreviewCanvas.ActualHeight : 220.0;
        var gridBrush = (Brush)FindResource("PanelBorderBrush");
        var offFill = (Brush)FindResource("ButtonSecondaryBrush");
        var offStroke = (Brush)FindResource("TextSecondaryBrush");
        var onStroke = (Brush)FindResource("StatusReadyBrush");
        var textBrush = (Brush)FindResource("TextSecondaryBrush");
        var originX = width * 0.27;
        var originY = height * 0.62;
        const double cellW = 46.0;
        const double cellH = 30.0;
        const double gapX = 12.0;
        const double gapY = 12.0;
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                var x = originX + col * (cellW + gapX);
                var y = originY - row * (cellH + gapY);
                AddRect(x, y, cellW, cellH, offFill, offStroke, 1.0);
            }
        }

        if (isValid)
        {
            var vectorScale = 5.5;
            var dx = Math.Clamp(shiftX * vectorScale, -75.0, 75.0);
            var dy = Math.Clamp(-shiftY * vectorScale, -75.0, 75.0);
            var hasShift = Math.Abs(shiftX) >= 0.000001 || Math.Abs(shiftY) >= 0.000001;
            for (var row = 0; row < 3; row++)
            {
                for (var col = 0; col < 4; col++)
                {
                    var x = originX + col * (cellW + gapX) + dx;
                    var y = originY - row * (cellH + gapY) + dy;
                    AddRect(x, y, cellW, cellH, Brushes.Transparent, onStroke, 2.0);
                }
            }

            if (hasShift)
            {
                AddArrow(originX + 28.0, originY - 94.0, originX + 28.0 + dx, originY - 94.0 + dy, onStroke);
            }
            else
            {
                AddText("NO SHIFT", originX + 12.0, originY - 106.0, onStroke);
            }
        }

        AddLine(24, height - 26, width - 24, height - 26, gridBrush);
        AddLine(24, height - 26, 24, 24, gridBrush);
        AddText("Design X", width - 78, height - 22, textBrush);
        AddText("Design Y", 8, 28, textBrush);
        PreviewCaptionText.Text = isValid ? $"Glass 기준 이동: X {Format(shiftX)} mm / Y {Format(shiftY)} mm" : "입력값을 확인하세요.";
    }

    private void AddRect(double x, double y, double width, double height, Brush fill, Brush stroke, double strokeThickness)
    {
        var rect = new Rectangle
        {
            Width = width,
            Height = height,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = strokeThickness
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        PreviewCanvas.Children.Add(rect);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush stroke)
    {
        PreviewCanvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = 1.0 });
    }

    private void AddArrow(double x1, double y1, double x2, double y2, Brush stroke)
    {
        AddLine(x1, y1, x2, y2, stroke);
        var angle = Math.Atan2(y2 - y1, x2 - x1);
        const double length = 9.0;
        var left = angle + Math.PI * 0.82;
        var right = angle - Math.PI * 0.82;
        AddLine(x2, y2, x2 + Math.Cos(left) * length, y2 + Math.Sin(left) * length, stroke);
        AddLine(x2, y2, x2 + Math.Cos(right) * length, y2 + Math.Sin(right) * length, stroke);
    }

    private void AddText(string text, double x, double y, Brush foreground)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = 12.0,
            FontWeight = FontWeights.SemiBold
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        PreviewCanvas.Children.Add(label);
    }

    private void NumericTextBoxPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        e.Handled = !IsValidNumericInput(CreateProspectiveValue(textBox, e.Text));
    }

    private void NumericTextBoxPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox textBox || !e.DataObject.GetDataPresent(DataFormats.UnicodeText) || e.DataObject.GetData(DataFormats.UnicodeText)is not string pastedText || !IsValidNumericInput(CreateProspectiveValue(textBox, pastedText)))
        {
            e.CancelCommand();
        }
    }

    private static string CreateProspectiveValue(TextBox textBox, string input)
    {
        var text = textBox.Text ?? "";
        var start = Math.Clamp(textBox.SelectionStart, 0, text.Length);
        var length = Math.Clamp(textBox.SelectionLength, 0, text.Length - start);
        return text.Remove(start, length).Insert(start, input);
    }

    private static bool IsValidNumericInput(string value)
    {
        if (value.Length == 0 || value == "-")
        {
            return true;
        }

        var unsigned = value[0] == '-' ? value[1..] : value;
        var decimalPointCount = 0;
        foreach (var character in unsigned)
        {
            if (char.IsDigit(character))
            {
                continue;
            }

            if (character == '.' && ++decimalPointCount == 1)
            {
                continue;
            }

            return false;
        }

        return unsigned.Length > 0;
    }

    private void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        if (!TryReadInputs(out var shiftX, out var shiftY, out var error))
        {
            MessageBox.Show(this, error, "입력값 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var validationMessage = _validateApply(shiftX, shiftY);
        if (!string.IsNullOrWhiteSpace(validationMessage))
        {
            MessageBox.Show(this, validationMessage, "Shift Offset 적용 불가", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResultShiftOffsetX = Format(shiftX);
        ResultShiftOffsetY = Format(shiftY);
        DialogResult = true;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }

    private static string Format(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

public sealed record ST_SHIFT_OFFSET_HEAD_ROW(int HeadNo, string HeadName, string ScannerGx, string ScannerGy);
