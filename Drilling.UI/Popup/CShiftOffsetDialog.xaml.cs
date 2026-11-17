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
        var beforeBrush = (Brush)FindResource("TextSecondaryBrush");
        var afterBrush = (Brush)FindResource("StatusReadyBrush");
        var textBrush = (Brush)FindResource("TextSecondaryBrush");
        var originX = width * 0.46;
        var originY = height * 0.55;

        if (isValid)
        {
            var hasShift = Math.Abs(shiftX) >= 0.000001 || Math.Abs(shiftY) >= 0.000001;
            var dx = 0.0;
            var dy = 0.0;
            if (hasShift)
            {
                var magnitude = Math.Sqrt((shiftX * shiftX) + (shiftY * shiftY));
                var displayLength = Math.Clamp(magnitude * 45.0, 24.0, 82.0);
                dx = shiftX / magnitude * displayLength;
                dy = -shiftY / magnitude * displayLength;
            }

            AddPoint(originX, originY, 11.0, beforeBrush, Brushes.Transparent, 2.0);
            if (hasShift)
            {
                var afterX = originX + dx;
                var afterY = originY + dy;
                var beforeLabelX = dx >= 0.0 ? originX - 66.0 : originX + 16.0;
                var afterLabelX = dx >= 0.0 ? afterX + 14.0 : afterX - 62.0;
                var beforeLabelY = dy >= 0.0 ? originY - 28.0 : originY + 12.0;
                var afterLabelY = dy >= 0.0 ? afterY + 12.0 : afterY - 28.0;

                AddArrow(originX, originY, afterX, afterY, afterBrush);
                AddPoint(afterX, afterY, 12.0, afterBrush, afterBrush, 2.0);
                AddText("BEFORE", Math.Clamp(beforeLabelX, 8.0, width - 64.0), Math.Clamp(beforeLabelY, 8.0, height - 32.0), beforeBrush);
                AddText("AFTER", Math.Clamp(afterLabelX, 8.0, width - 58.0), Math.Clamp(afterLabelY, 8.0, height - 32.0), afterBrush);
            }
            else
            {
                AddPoint(originX, originY, 6.0, afterBrush, afterBrush, 1.0);
                AddText("BEFORE", originX - 66.0, originY + 10.0, beforeBrush);
                AddText("AFTER", originX + 14.0, originY - 26.0, afterBrush);
            }
        }

        AddLine(24, height - 26, width - 24, height - 26, gridBrush);
        AddLine(24, height - 26, 24, 24, gridBrush);
        AddText("Design X", width - 78, height - 22, textBrush);
        AddText("Design Y", 8, 28, textBrush);
        PreviewCaptionText.Text = isValid ? $"Glass 기준 이동: X {Format(shiftX)} mm / Y {Format(shiftY)} mm" : "입력값을 확인하세요.";
    }

    private void AddPoint(double centerX, double centerY, double diameter, Brush fill, Brush stroke, double strokeThickness)
    {
        var point = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = strokeThickness
        };
        Canvas.SetLeft(point, centerX - (diameter / 2.0));
        Canvas.SetTop(point, centerY - (diameter / 2.0));
        PreviewCanvas.Children.Add(point);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush stroke, double thickness = 1.0)
    {
        PreviewCanvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = thickness });
    }

    private void AddArrow(double x1, double y1, double x2, double y2, Brush stroke)
    {
        var angle = Math.Atan2(y2 - y1, x2 - x1);
        const double pointRadius = 7.0;
        var startX = x1 + Math.Cos(angle) * pointRadius;
        var startY = y1 + Math.Sin(angle) * pointRadius;
        var endX = x2 - Math.Cos(angle) * pointRadius;
        var endY = y2 - Math.Sin(angle) * pointRadius;
        AddLine(startX, startY, endX, endY, stroke, 2.0);
        const double length = 9.0;
        var left = angle + Math.PI * 0.82;
        var right = angle - Math.PI * 0.82;
        AddLine(endX, endY, endX + Math.Cos(left) * length, endY + Math.Sin(left) * length, stroke, 2.0);
        AddLine(endX, endY, endX + Math.Cos(right) * length, endY + Math.Sin(right) * length, stroke, 2.0);
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
