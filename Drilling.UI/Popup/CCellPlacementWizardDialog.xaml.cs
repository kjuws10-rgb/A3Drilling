using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drilling.Common.Managers;

namespace Drilling.UI.Popup;

public partial class CCellPlacementWizardDialog : Window
{
    private const string DefaultModelOption = "DEFAULT";
    private static ST_CELL_PLACEMENT_WIZARD_RESULT? _lastAppliedResult;
    private readonly int _maxCellCount;
    private readonly int _modelTypeCount;
    private readonly double _glassSizeX;
    private readonly double _glassSizeY;
    private readonly double _akMarginX;
    private readonly double _akMarginY;
    private readonly string[] _defaultModelOptions;
    private readonly string[] _overrideModelOptions;
    private bool _isInitialized;
    private int _modelApplySequence;

    public CCellPlacementWizardDialog(ST_CELL_PLACEMENT_WIZARD_CONTEXT context)
    {
        InitializeComponent();
        _maxCellCount = Math.Max(1, context.CellCount);
        _modelTypeCount = Math.Clamp(context.ModelTypeCount, 1, 5);
        _glassSizeX = context.GlassSizeX;
        _glassSizeY = context.GlassSizeY;
        _akMarginX = context.AkMarginX;
        _akMarginY = context.AkMarginY;
        _defaultModelOptions = Enumerable.Range(1, _modelTypeCount).Select(value => value.ToString(CultureInfo.InvariantCulture)).ToArray();
        _overrideModelOptions = [DefaultModelOption, .. _defaultModelOptions];
        Result = ST_CELL_PLACEMENT_WIZARD_RESULT.Empty;

        DirectionComboBox.ItemsSource = ST_CELL_PLACEMENT_WIZARD_DIRECTION.Options;
        DirectionComboBox.SelectedIndex = 0;
        DefaultModelComboBox.ItemsSource = _defaultModelOptions;
        DefaultModelComboBox.SelectedIndex = 0;
        RowModelItemsControl.ItemsSource = RowModelRows;
        ColumnModelItemsControl.ItemsSource = ColumnModelRows;

        var defaultColumnCount = Math.Min(30, _maxCellCount);
        var defaultRowCount = Math.Max(1, (int)Math.Ceiling(_maxCellCount / (double)Math.Max(1, defaultColumnCount)));
        CellCountTextBox.Text = _maxCellCount.ToString(CultureInfo.InvariantCulture);
        ColumnCountTextBox.Text = defaultColumnCount.ToString(CultureInfo.InvariantCulture);
        RowCountTextBox.Text = defaultRowCount.ToString(CultureInfo.InvariantCulture);
        StartXTextBox.Text = "0";
        StartYTextBox.Text = "0";
        PitchXTextBox.Text = "0";
        PitchYTextBox.Text = "0";
        if (_lastAppliedResult is { } lastResult && lastResult.CellCount == _maxCellCount)
        {
            ApplyInitialResult(lastResult);
        }
        else
        {
            RefreshModelRows();
        }

        _isInitialized = true;

        Loaded += (_, _) =>
        {
            ColumnCountTextBox.Focus();
            ColumnCountTextBox.SelectAll();
        };
    }

    public ObservableCollection<ST_CELL_PLACEMENT_MODEL_SELECTOR_ROW> RowModelRows { get; } = [];
    public ObservableCollection<ST_CELL_PLACEMENT_MODEL_SELECTOR_ROW> ColumnModelRows { get; } = [];
    public ST_CELL_PLACEMENT_WIZARD_RESULT Result { get; private set; }

    private void GridCountTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }

        RefreshModelRows();
    }

    private void RefreshModelRows()
    {
        var rowCount = TryReadPositiveInt(RowCountTextBox.Text, out var parsedRowCount)
            ? Math.Clamp(parsedRowCount, 1, _maxCellCount)
            : 1;
        var columnCount = TryReadPositiveInt(ColumnCountTextBox.Text, out var parsedColumnCount)
            ? Math.Clamp(parsedColumnCount, 1, _maxCellCount)
            : 1;

        SyncModelRows(RowModelRows, rowCount, "ROW");
        SyncModelRows(ColumnModelRows, columnCount, "COL");
    }

    private void SyncModelRows(ObservableCollection<ST_CELL_PLACEMENT_MODEL_SELECTOR_ROW> rows, int count, string prefix)
    {
        while (rows.Count > count)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        while (rows.Count < count)
        {
            var row = new ST_CELL_PLACEMENT_MODEL_SELECTOR_ROW(prefix, rows.Count + 1, $"{prefix} {rows.Count + 1}", _overrideModelOptions);
            row.ModelChanged += OnModelRuleChanged;
            rows.Add(row);
        }
    }

    private void OnModelRuleChanged(ST_CELL_PLACEMENT_MODEL_SELECTOR_ROW row)
    {
        row.ApplyOrder = ReadModel(row.SelectedModel, 0) > 0
            ? ++_modelApplySequence
            : 0;
    }

    private void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (!TryCreateResult(out var result, out var error))
        {
            MessageBox.Show(this, error, "입력값 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = result;
        _lastAppliedResult = result;
        DialogResult = true;
    }

    private void ApplyInitialResult(ST_CELL_PLACEMENT_WIZARD_RESULT result)
    {
        ColumnCountTextBox.Text = result.ColumnCount.ToString(CultureInfo.InvariantCulture);
        RowCountTextBox.Text = result.RowCount.ToString(CultureInfo.InvariantCulture);
        StartXTextBox.Text = FormatValidationDouble(Math.Max(0.0, result.StartX));
        StartYTextBox.Text = FormatValidationDouble(Math.Max(0.0, result.StartY));
        PitchXTextBox.Text = FormatValidationDouble(Math.Max(0.0, result.PitchX));
        PitchYTextBox.Text = FormatValidationDouble(Math.Max(0.0, result.PitchY));
        DirectionComboBox.SelectedItem = ST_CELL_PLACEMENT_WIZARD_DIRECTION.Options.FirstOrDefault(option => option.Key.Equals(result.Direction, StringComparison.OrdinalIgnoreCase)) ?? ST_CELL_PLACEMENT_WIZARD_DIRECTION.Options[0];
        DefaultModelComboBox.SelectedItem = Math.Clamp(result.DefaultModelType, 1, _modelTypeCount).ToString(CultureInfo.InvariantCulture);
        RefreshModelRows();

        _modelApplySequence = 0;
        foreach (var modelOverride in result.ModelOverrides)
        {
            if (modelOverride.ModelType < 1 || modelOverride.ModelType > _modelTypeCount)
            {
                continue;
            }

            var rows = modelOverride.Scope.Equals("ROW", StringComparison.OrdinalIgnoreCase)
                ? RowModelRows
                : modelOverride.Scope.Equals("COL", StringComparison.OrdinalIgnoreCase)
                    ? ColumnModelRows
                    : null;
            var rowIndex = modelOverride.Index - 1;
            if (rows is null || rowIndex < 0 || rowIndex >= rows.Count)
            {
                continue;
            }

            rows[rowIndex].SelectedModel = modelOverride.ModelType.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void OpenWizardInputKeypad(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        e.Handled = true;
        var (parameter, dataType, min, max) = GetInputKeypadInfo(textBox);
        var dialog = new CValueInputDialog(parameter, textBox.Text, dataType, min, max)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            textBox.Text = dialog.ResultValue;
        }
    }

    private (string Parameter, EN_RECIPE_DATA_TYPE DataType, double Min, double Max) GetInputKeypadInfo(TextBox textBox)
    {
        const double nonNegativeMaximum = 1000000.0;
        if (ReferenceEquals(textBox, ColumnCountTextBox))
        {
            return ("COLUMN COUNT", EN_RECIPE_DATA_TYPE.Int, 1.0, _maxCellCount);
        }

        if (ReferenceEquals(textBox, RowCountTextBox))
        {
            return ("ROW COUNT", EN_RECIPE_DATA_TYPE.Int, 1.0, _maxCellCount);
        }

        if (ReferenceEquals(textBox, StartXTextBox))
        {
            return ("START X", EN_RECIPE_DATA_TYPE.Double, 0.0, nonNegativeMaximum);
        }

        if (ReferenceEquals(textBox, StartYTextBox))
        {
            return ("START Y", EN_RECIPE_DATA_TYPE.Double, 0.0, nonNegativeMaximum);
        }

        if (ReferenceEquals(textBox, PitchXTextBox))
        {
            return ("CELL PITCH X", EN_RECIPE_DATA_TYPE.Double, 0.0, nonNegativeMaximum);
        }

        return ("CELL PITCH Y", EN_RECIPE_DATA_TYPE.Double, 0.0, nonNegativeMaximum);
    }

    private bool TryCreateResult(out ST_CELL_PLACEMENT_WIZARD_RESULT result, out string error)
    {
        result = ST_CELL_PLACEMENT_WIZARD_RESULT.Empty;
        error = "";

        const int startCell = 1;
        var cellCount = _maxCellCount;

        if (!TryReadPositiveInt(ColumnCountTextBox.Text, out var columnCount))
        {
            error = "COLUMN COUNT must be a positive integer.";
            return false;
        }

        if (!TryReadPositiveInt(RowCountTextBox.Text, out var rowCount))
        {
            error = "ROW COUNT must be a positive integer.";
            return false;
        }

        if (!TryReadDouble(StartXTextBox.Text, out var startX) ||
            !TryReadDouble(StartYTextBox.Text, out var startY) ||
            !TryReadDouble(PitchXTextBox.Text, out var pitchX) ||
            !TryReadDouble(PitchYTextBox.Text, out var pitchY))
        {
            error = "START X/Y and PITCH X/Y must be finite numbers.";
            return false;
        }

        if (startX < 0.0 || startY < 0.0 || pitchX < 0.0 || pitchY < 0.0)
        {
            error = "START X/Y and CELL PITCH X/Y cannot be negative.";
            return false;
        }

        var layoutCellCount = (long)rowCount * columnCount;
        if (layoutCellCount != cellCount)
        {
            error = $"ROW COUNT x COLUMN COUNT must match CELL COUNT.\nCurrent: {rowCount} x {columnCount} = {layoutCellCount}, CELL COUNT = {cellCount}.";
            return false;
        }

        if (!TryValidateGlassBounds(rowCount, columnCount, startX, startY, pitchX, pitchY, out error))
        {
            return false;
        }

        var defaultModelType = ReadModel(DefaultModelComboBox.SelectedItem?.ToString(), 1);
        var direction = DirectionComboBox.SelectedItem as ST_CELL_PLACEMENT_WIZARD_DIRECTION
            ?? ST_CELL_PLACEMENT_WIZARD_DIRECTION.Options[0];
        result = new ST_CELL_PLACEMENT_WIZARD_RESULT(

            startCell,

            cellCount,

            rowCount,

            columnCount,

            startX,

            startY,

            pitchX,

            pitchY,

            direction.Key,

            defaultModelType,

            RowModelRows
                .Concat(ColumnModelRows)
                .Where(row => row.ApplyOrder > 0 && ReadModel(row.SelectedModel, 0) > 0)
                .OrderBy(row => row.ApplyOrder)
                .Select(row => new ST_CELL_PLACEMENT_MODEL_OVERRIDE(row.Scope, row.Index, ReadModel(row.SelectedModel, 0)))
                .ToArray()
        );
        return true;
    }

    private static bool TryReadPositiveInt(string text, out int value)
    {
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    private static bool TryReadDouble(string text, out double value)
    {
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static int ReadModel(string? text, int defaultValue)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }

    private bool TryValidateGlassBounds(

        int rowCount,

        int columnCount,

        double startX,

        double startY,

        double pitchX,

        double pitchY,

        out string error
    )
    {
        error = "";
        if (_glassSizeX <= 0.0 || _glassSizeY <= 0.0)
        {
            return true;
        }

        var firstX = _akMarginX + startX;
        var lastX = _akMarginX + startX + ((columnCount - 1) * pitchX);
        var firstY = _akMarginY + startY;
        var lastY = _akMarginY + startY + ((rowCount - 1) * pitchY);
        var minX = Math.Min(firstX, lastX);
        var maxX = Math.Max(firstX, lastX);
        var minY = Math.Min(firstY, lastY);
        var maxY = Math.Max(firstY, lastY);
        if (minX >= 0.0 && maxX <= _glassSizeX && minY >= 0.0 && maxY <= _glassSizeY)
        {
            return true;
        }

        error =
            "CELL PITCH placement exceeds GLASS SIZE.\n" +
            $"X range: {FormatValidationDouble(minX)} ~ {FormatValidationDouble(maxX)} / Glass X: {FormatValidationDouble(_glassSizeX)}\n" +
            $"Y range: {FormatValidationDouble(minY)} ~ {FormatValidationDouble(maxY)} / Glass Y: {FormatValidationDouble(_glassSizeY)}";
        return false;
    }

    private static string FormatValidationDouble(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private void CancelClick(object sender, RoutedEventArgs e)
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
}

public sealed record ST_CELL_PLACEMENT_WIZARD_CONTEXT(
    int CellCount,
    int ModelTypeCount,
    double GlassSizeX,
    double GlassSizeY,
    double AkMarginX,
    double AkMarginY);

public sealed record ST_CELL_PLACEMENT_WIZARD_RESULT(

    int StartCell,

    int CellCount,

    int RowCount,

    int ColumnCount,

    double StartX,

    double StartY,

    double PitchX,

    double PitchY,

    string Direction,

    int DefaultModelType,

    IReadOnlyList<ST_CELL_PLACEMENT_MODEL_OVERRIDE> ModelOverrides
)
{
    public static ST_CELL_PLACEMENT_WIZARD_RESULT Empty { get; } = new(1, 0, 1, 1, 0.0, 0.0, 0.0, 0.0, ST_CELL_PLACEMENT_WIZARD_DIRECTION.HorizontalFirst, 1, []);
}

public sealed record ST_CELL_PLACEMENT_MODEL_OVERRIDE(string Scope, int Index, int ModelType);

public sealed record ST_CELL_PLACEMENT_WIZARD_DIRECTION(string Key, string DisplayName)
{
    public const string HorizontalFirst = "HORIZONTAL_FIRST";
    public const string VerticalFirst = "VERTICAL_FIRST";

    public static IReadOnlyList<ST_CELL_PLACEMENT_WIZARD_DIRECTION> Options { get; } =
    [
        new(HorizontalFirst, "HORIZONTAL FIRST"),
        new(VerticalFirst, "VERTICAL FIRST")
    ];

    public override string ToString()
    {
        return DisplayName;
    }
}

public sealed class ST_CELL_PLACEMENT_MODEL_SELECTOR_ROW : INotifyPropertyChanged
{
    private string _selectedModel;

    public ST_CELL_PLACEMENT_MODEL_SELECTOR_ROW(string scope, int index, string label, IReadOnlyList<string> modelOptions)
    {
        Scope = scope;
        Index = index;
        Label = label;
        ModelOptions = modelOptions;
        _selectedModel = modelOptions.FirstOrDefault() ?? "DEFAULT";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<ST_CELL_PLACEMENT_MODEL_SELECTOR_ROW>? ModelChanged;
    public string Scope { get; }
    public int Index { get; }
    public string Label { get; }
    public IReadOnlyList<string> ModelOptions { get; }
    public int ApplyOrder { get; set; }

    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (_selectedModel == value)
            {
                return;
            }

            _selectedModel = value;
            OnPropertyChanged();
            ModelChanged?.Invoke(this);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
