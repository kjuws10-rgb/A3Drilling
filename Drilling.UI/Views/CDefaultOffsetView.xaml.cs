using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Drilling.UI.Menu.Menus;
using Drilling.Common.Correction;
using Drilling.Common.Review;
using System.Globalization;

namespace Drilling.UI.Views;

public partial class CDefaultOffsetView : UserControl
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(CDefaultOffsetView), new PropertyMetadata(false));
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); private set => SetValue(IsCompactProperty, value); }
    // Display existing shared snapshots only while this workspace is visible.
    // No communication read, motion command, worker or file polling is started here.
    private readonly DispatcherTimer _display = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
    public CDefaultOffsetView()
    {
        InitializeComponent();
        _display.Tick += (_, _) => RefreshFeedback();
        Loaded += (_, _) => UpdateDisplay();
        Unloaded += (_, _) => _display.Stop();
        IsVisibleChanged += (_, _) => UpdateDisplay();
        DataContextChanged += (_, _) => UpdateDisplay();
        SizeChanged += (_, _) =>
        {
            IsCompact = ActualHeight < 690;
            RecipeRow.Height = new GridLength(IsCompact ? 40 : 48);
            ProgressRow.Height = new GridLength(IsCompact ? 50 : 62);
            PositionRow.Height = new GridLength(IsCompact ? 92 : 94);
            ActionRow.Height = new GridLength(IsCompact ? 104 : 118);
            ApplyRow.Height = new GridLength(IsCompact ? 52 : 62);
            ResultTitleRow.Height = new GridLength(IsCompact ? 24 : 34);
            DefaultHeadResults.ColumnHeaderHeight = IsCompact ? 28 : 38;
        };
        DefaultHeadResults.SizeChanged += (_, _) =>
        {
            var height = Math.Clamp((DefaultHeadResults.ActualHeight - DefaultHeadResults.ColumnHeaderHeight - 2) / 8, 26, 52);
            if (Math.Abs(DefaultHeadResults.RowHeight - height) > .1) DefaultHeadResults.RowHeight = height;
            var width = Math.Max(0, DefaultHeadResults.ActualWidth - 2);
            double[] weights = [.6, .8, 1.1, 1.1, 1.2, 1.2, 1.1];
            for (var index = 0; index < weights.Length; index++)
            {
                var pixels = Math.Floor(width * weights[index] / weights.Sum());
                if (Math.Abs(DefaultHeadResults.Columns[index].ActualWidth - pixels) > .5)
                    DefaultHeadResults.Columns[index].Width = new DataGridLength(pixels);
            }
        };
    }
    private void UpdateDisplay()
    {
        if (IsLoaded && IsVisible) { RefreshFeedback(); _display.Start(); }
        else _display.Stop();
    }
    private void RefreshFeedback() => (DataContext as CDefaultOffsetPanel)?.RefreshFeedback();

    // A setup-session editor, not an equipment poller. Default values themselves
    // continue to be persisted exclusively in the current recipe.
    public static (ST_DEFAULT_OFFSET_OPTIONS Options, int Model)? EditConditions(ST_DEFAULT_OFFSET_OPTIONS current, IReadOnlyList<int> models, int currentModel, IReadOnlyDictionary<int, ST_DEFAULT_HEAD_GEOMETRY> geometries, double designAlignKey3ReviewX)
    {
        var owner = Application.Current?.MainWindow;
        var dialog = new Window { Title = "Default CENTER 측정 조건", Width = 820, Height = 870, MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 40), ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner };
        dialog.SetResourceReference(BackgroundProperty, "GridBackgroundBrush");
        dialog.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var layout = new Grid { Margin = new Thickness(20) };
        layout.SetResourceReference(BackgroundProperty, "GridBackgroundBrush");
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(160) });
        layout.RowDefinitions.Add(new() { Height = new GridLength(43) });
        layout.Children.Add(new TextBlock { Text = "검사 기준 모델 (Recipe-Process-Cell)", VerticalAlignment = VerticalAlignment.Center, FontSize = 15 });
        var model = new ComboBox { Name = "DefaultModel", ItemsSource = models, SelectedItem = currentModel, FontSize = 15, Margin = new Thickness(0,4,0,4), VerticalContentAlignment = VerticalAlignment.Center };
        Grid.SetColumn(model, 1); layout.Children.Add(model);
        layout.RowDefinitions.Add(new() { Height = new GridLength(43) });
        var stageLabel = new TextBlock { Text = "가공할 Stage Y 위치 (mm)", VerticalAlignment = VerticalAlignment.Center, FontSize = 15 };
        Grid.SetRow(stageLabel, 1); layout.Children.Add(stageLabel);
        var input = new TextBox { Name = "DefaultStageY", Text = current.TestStageY.ToString("0.#####", CultureInfo.InvariantCulture), Margin = new Thickness(0,4,0,4), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 15, Padding = new Thickness(6,0,6,0) };
        Grid.SetRow(input, 1); Grid.SetColumn(input, 1); layout.Children.Add(input);
        var alignKey1 = new TextBox
        {
            Name = "DefaultAlignKey1ReviewX",
            Text = current.AlignKey1ReviewX.ToString("0.#####", CultureInfo.InvariantCulture),
            IsReadOnly = true,
            ToolTip = "Recipe > Process > Scan의 Review Camera Align Key Pos X",
            Margin = new Thickness(0,4,0,4),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 15,
            Padding = new Thickness(6,0,6,0)
        };
        var alignKey3 = new TextBox
        {
            Name = "DefaultAlignKey3ReviewX",
            Text = current.AlignKey3ReviewX.ToString("0.#####", CultureInfo.InvariantCulture),
            Margin = new Thickness(0,4,6,4),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 15,
            Padding = new Thickness(6,0,6,0)
        };
        var secondaryButtonStyle = Application.Current?.TryFindResource("SecondaryButton") as Style;
        var calculateAlignKey3 = new Button
        {
            Content = "설계값 계산",
            Style = secondaryButtonStyle,
            Margin = new Thickness(0,4,0,4),
            Padding = new Thickness(8,0,8,0),
            FontSize = 13,
            ToolTip = "AK1 Review X + Glass Size X - (2 × AK Margin X)"
        };
        var alignKey3Editor = new Grid();
        alignKey3Editor.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        alignKey3Editor.ColumnDefinitions.Add(new() { Width = new GridLength(104) });
        alignKey3Editor.Children.Add(alignKey3);
        Grid.SetColumn(calculateAlignKey3, 1); alignKey3Editor.Children.Add(calculateAlignKey3);
        calculateAlignKey3.Click += (_, _) => alignKey3.Text = designAlignKey3ReviewX.ToString("0.#####", CultureInfo.InvariantCulture);
        var machiningModes = new[] { "샷 가공", "십자 가공" };
        var machining = new ComboBox { Name = "DefaultMachiningMode", ItemsSource = machiningModes, SelectedIndex = (int)current.MachiningMode, FontSize = 15, Margin = new Thickness(0,4,0,4), VerticalContentAlignment = VerticalAlignment.Center };
        var crossSize = new TextBox { Name = "DefaultCrossSize", Text = current.CrossSize.ToString("0.#####", CultureInfo.InvariantCulture), Margin = new Thickness(0,4,0,4), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 15, Padding = new Thickness(6,0,6,0), IsEnabled = current.MachiningMode == EN_DEFAULT_MACHINING_MODE.Cross };
        var vision = new TextBox { Name = "DefaultVisionMethod", Text = current.VisionDescription, IsReadOnly = true, Focusable = false, FontSize = 15, Margin = new Thickness(0,4,0,4), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6,0,6,0) };
        void UpdateMachiningMode()
        {
            var cross = machining.SelectedIndex == (int)EN_DEFAULT_MACHINING_MODE.Cross;
            crossSize.IsEnabled = cross;
            vision.Text = cross ? "Mark Find Setting Single" : "Mark Find Single";
        }
        machining.SelectionChanged += (_, _) => UpdateMachiningMode();
        void AddInputRow(int row, string label, FrameworkElement control)
        {
            layout.RowDefinitions.Add(new() { Height = new GridLength(43) });
            var title = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 15 };
            Grid.SetRow(title, row); layout.Children.Add(title);
            Grid.SetRow(control, row); Grid.SetColumn(control, 1); layout.Children.Add(control);
        }
        // The vision protocol names are longer than the numeric inputs.
        layout.ColumnDefinitions[1].Width = new GridLength(300);
        AddInputRow(2, "Align Key 1 Review X (mm) · Recipe", alignKey1);
        AddInputRow(3, "Align Key 3 Review X (mm)", alignKey3Editor);
        AddInputRow(4, "가공 방식", machining);
        AddInputRow(5, "십자 가로·세로 전체 길이 (mm)", crossSize);
        AddInputRow(6, "비전 검사 방식 (자동)", vision);
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var reference = new StackPanel { Margin = new Thickness(0,14,0,12) };
        reference.Children.Add(new TextBlock { Text = "자동 계산 · 설정값 (읽기 전용)", FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,8) });
        var centerPreview = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,6) };
        reference.Children.Add(centerPreview);
        void UpdateCenterPreview()
        {
            centerPreview.Text = double.TryParse(alignKey1.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ak1) && double.IsFinite(ak1) &&
                double.TryParse(alignKey3.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ak3) && double.IsFinite(ak3)
                ? $"AK1·AK3 실측 중앙 Review X   {(ak1 + ak3) / 2:0.000} mm"
                : "AK1·AK3 실측 중앙 Review X   입력값 확인 필요";
        }
        alignKey3.TextChanged += (_, _) => UpdateCenterPreview();
        UpdateCenterPreview();
        reference.Children.Add(new TextBlock { Text = $"Beam간격 및 Beam중심 허용오차   {current.Tolerance:0.000} mm   (Recipe-Common-Review)", FontSize = 14, Margin = new Thickness(0,0,0,10) });
        reference.Children.Add(new TextBlock { Text = "헤드별 설정 (Setting-Common-Scanner)", FontSize = 13, Margin = new Thickness(0,0,0,6) });
        var fields = new Grid();
        foreach (var width in new[] { new GridLength(70), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star) }) fields.ColumnDefinitions.Add(new() { Width = width });
        void AddFieldRow(int row, params string[] values)
        {
            fields.RowDefinitions.Add(new() { Height = new GridLength(27) });
            for (var col = 0; col < values.Length; col++)
            {
                var text = new TextBlock { Text = values[col], FontSize = 13, FontWeight = row == 0 ? FontWeights.Bold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
                var cell = new Border { BorderBrush = System.Windows.Media.Brushes.LightSlateGray, BorderThickness = new Thickness(0,0,0,1), Child = text };
                Grid.SetRow(cell, row); Grid.SetColumn(cell, col); fields.Children.Add(cell);
            }
        }
        AddFieldRow(0, "HEAD", "Scan Field X (mm)", "Scan Field Y (mm)", "DOE 회전 각도 (deg)");
        foreach (var item in geometries.OrderBy(p => p.Key))
            AddFieldRow(fields.RowDefinitions.Count, $"H{item.Key:00}", (item.Value.FieldHalfX * 2).ToString("0.###", CultureInfo.InvariantCulture), (item.Value.FieldHalfY * 2).ToString("0.###", CultureInfo.InvariantCulture), item.Value.DoeAngle.ToString("0.###", CultureInfo.InvariantCulture));
        reference.Children.Add(fields);
        reference.Children.Add(new TextBlock { Text = $"글래스 중심 Review X는 조건에 적용한 AK1·AK3 Review X 엔코더값의 정확한 중간값입니다. AK3의 설계값은 AK1 + Glass Size X - (2 × AK Margin X)로 계산합니다.\n십자 길이 10 mm: 스캐너는 가로·세로 각각 중심 기준 −5~+5 mm를 그리며, DOE가 같은 십자를 {current.InspectionPointCount}개 형상으로 분기합니다.\n십자 가공은 각 DOE 슬롯에서 Mark Find Setting Single로 {current.InspectionPointCount}회 검사하고, 샷 가공은 Mark Find Single로 동일한 DOE 슬롯을 검사합니다.\n십자 속도는 Head별 Recipe Jump Speed, 샷 가공은 Shot Count를 사용합니다.\nField는 전체 폭·높이입니다. Y=55 mm이면 중심 기준 ±27.5 mm입니다.\n허용오차는 Recipe의 Review Tolerance X/Y 중 작은 값입니다.\n각 Head는 입력한 Stage Y 위치에서 가공합니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,0), FontSize = 13 });
        var referenceScroll = new ScrollViewer { Content = reference, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(referenceScroll, 7); Grid.SetColumnSpan(referenceScroll, 2); layout.Children.Add(referenceScroll);
        layout.RowDefinitions.Add(new() { Height = new GridLength(44) });
        var buttons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        var save = new Button { Content = "조건 적용", Style = secondaryButtonStyle, Height = double.NaN, FontSize = 15, Margin = new Thickness(0,0,6,0) }; var cancel = new Button { Content = "취소", Style = secondaryButtonStyle, Height = double.NaN, FontSize = 15, Margin = new Thickness(6,0,0,0), IsCancel = true };
        buttons.Children.Add(save); buttons.Children.Add(cancel); Grid.SetRow(buttons, 8); Grid.SetColumnSpan(buttons, 2); layout.Children.Add(buttons);
        (ST_DEFAULT_OFFSET_OPTIONS Options, int Model)? result = null;
        save.Click += (_, _) =>
        {
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var stageY) || !double.IsFinite(stageY)) { Popup.CRecipeConfirmDialog.ShowWarning("입력 확인", "가공할 Stage Y 위치에 유한한 숫자를 입력해 주세요."); return; }
            if (!double.TryParse(alignKey1.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ak1) || !double.IsFinite(ak1)) { Popup.CRecipeConfirmDialog.ShowWarning("입력 확인", "Align Key 1 Review X 값을 확인해 주세요."); return; }
            if (!double.TryParse(alignKey3.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ak3) || !double.IsFinite(ak3)) { Popup.CRecipeConfirmDialog.ShowWarning("입력 확인", "Align Key 3 Review X에 유한한 숫자를 입력해 주세요."); return; }
            if (model.SelectedItem is not int selectedModel || !models.Contains(selectedModel)) { Popup.CRecipeConfirmDialog.ShowWarning("입력 확인", "검사 기준 모델을 선택해 주세요."); return; }
            var shape = (EN_DEFAULT_MACHINING_MODE)machining.SelectedIndex;
            var size = current.CrossSize;
            if (shape == EN_DEFAULT_MACHINING_MODE.Cross && (!double.TryParse(crossSize.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out size) || !double.IsFinite(size) || size <= 0)) { Popup.CRecipeConfirmDialog.ShowWarning("입력 확인", "십자 전체 길이에 0보다 큰 유한한 mm 값을 입력해 주세요."); return; }
            var options = current with { AlignKey1ReviewX = ak1, AlignKey3ReviewX = ak3, TestStageY = stageY, MachiningMode = shape, CrossSize = size };
            try { CDefaultOffsetCalibration.ValidateOptions(options); }
            catch (InvalidOperationException error) { Popup.CRecipeConfirmDialog.ShowWarning("입력 확인", error.Message); return; }
            result = (options, selectedModel); dialog.DialogResult = true;
        };
        dialog.Content = layout;
        return dialog.ShowDialog() == true ? result : null;
    }
}
