using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Views;
public partial class CReviewView : UserControl
{
    public static readonly DependencyProperty ReviewMenuProperty = DependencyProperty.Register(
        nameof(ReviewMenu), typeof(CMenuReview), typeof(CReviewView));
    public CMenuReview? ReviewMenu { get => (CMenuReview?)GetValue(ReviewMenuProperty); set => SetValue(ReviewMenuProperty, value); }
    private readonly DispatcherTimer _cameraTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
    public CReviewView()
    {
        InitializeComponent();
        SetBinding(ReviewMenuProperty, new Binding("CurrentScreen.Review"));
        _cameraTimer.Tick += (_, _) => RefreshCamera();
        Loaded += (_, _) => { RefreshCamera(); if (IsVisible) _cameraTimer.Start(); };
        Unloaded += (_, _) => _cameraTimer.Stop();
    }

    private void ReviewPreview_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshCamera();
    private void RefreshCamera()
    {
        ReviewMenu?.RefreshCameraFeedback(RunGlassPreviewViewport.ActualWidth, RunGlassPreviewViewport.ActualHeight);
    }

    private void RunCellPreviewLabel_MouseMove(object sender, MouseEventArgs e)
    {
        ShowCellPreviewHoverTip(

            sender,

            e,

            RunGlassPreviewViewport,

            RunGlassPreviewHoverTip,

            RunGlassPreviewHoverTipText,

            RunGlassPreviewHoverTipTranslateTransform
        );
    }

    private void RunCellPreviewLabel_MouseLeave(object sender, MouseEventArgs e)
    {
        HideCellPreviewHoverTip(RunGlassPreviewHoverTip, RunGlassPreviewHoverTipText);
    }

    private void CellPreviewLabel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HideCellPreviewHoverTips();
    }

    private void ReviewView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            HideCellPreviewHoverTips();
            _cameraTimer.Stop();
        }
        else if (IsLoaded) { RefreshCamera(); _cameraTimer.Start(); }
    }

    private static void ShowCellPreviewHoverTip(

        object sender,

        MouseEventArgs e,

        FrameworkElement viewport,

        Border hoverTip,

        TextBlock hoverTipText,

        TranslateTransform translateTransform
    )
    {
        if (sender is not FrameworkElement { DataContext: ST_CELL_PREVIEW_LABEL label })
        {
            HideCellPreviewHoverTip(hoverTip, hoverTipText);
            return;
        }

        hoverTipText.Text = label.DisplayText;
        hoverTip.Visibility = Visibility.Visible;
        PositionCellPreviewHoverTip(
            e.GetPosition(viewport),
            viewport,
            hoverTip,
            translateTransform);
    }

    private static void PositionCellPreviewHoverTip(
        Point pointer,
        FrameworkElement viewport,
        FrameworkElement hoverTip,
        TranslateTransform translateTransform)
    {
        const double margin = 4.0;
        const double offsetX = 12.0;
        const double offsetY = 14.0;
        hoverTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tipSize = hoverTip.DesiredSize;
        var x = pointer.X + offsetX;
        var y = pointer.Y + offsetY;
        if (viewport.ActualWidth > 0)
        {
            x = Math.Clamp(x, margin, Math.Max(margin, viewport.ActualWidth - tipSize.Width - margin));
        }

        if (viewport.ActualHeight > 0)
        {
            y = Math.Clamp(y, margin, Math.Max(margin, viewport.ActualHeight - tipSize.Height - margin));
        }

        translateTransform.X = x;
        translateTransform.Y = y;
    }

    private static void HideCellPreviewHoverTip(Border hoverTip, TextBlock hoverTipText)
    {
        hoverTip.Visibility = Visibility.Collapsed;
        hoverTipText.Text = "";
    }

    private void HideCellPreviewHoverTips()
    {
        HideCellPreviewHoverTip(RunGlassPreviewHoverTip, RunGlassPreviewHoverTipText);
    }

    private void ReviewResultDataGrid_AutoGeneratingColumn(
        object? sender,
        DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.Column is not DataGridTextColumn textColumn ||
            e.PropertyName.Equals("LINE", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (textColumn.Binding is Binding binding)
        {
            binding.StringFormat = "0.0000";
        }
    }

}
