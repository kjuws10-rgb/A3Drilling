using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drilling.Common.Managers;
using Drilling.UI.Popup;
using Drilling.UI.Menu.Menus;
using System.Windows.Threading;

namespace Drilling.UI.Views;

public partial class CSimpleCorrectionView : UserControl
{
    private void SelectRepresentativeShot(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is CSimpleCorrectionPanel { IsRough: true } panel && sender is FrameworkElement { DataContext: ST_SIMPLE_CORRECTION_SHOT shot } &&
            shot.CanProcess && panel.SelectCorrectionShotCommand.CanExecute(shot.Key))
        { panel.SelectCorrectionShotCommand.Execute(shot.Key); e.Handled = true; }
    }
    private readonly DispatcherTimer _feedbackTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
    public CSimpleCorrectionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { ResizePreview(); UpdateFeedbackTimer(); };
        _feedbackTimer.Tick += (_, _) => (DataContext as CSimpleCorrectionPanel)?.RefreshFeedback();
        Loaded += (_, _) => UpdateFeedbackTimer();
        Unloaded += (_, _) => _feedbackTimer.Stop();
    }

    private void UpdateFeedbackTimer()
    {
        if (IsLoaded && DataContext is CSimpleCorrectionPanel { IsRough: true } panel)
        { panel.RefreshFeedback(); _feedbackTimer.Start(); }
        else _feedbackTimer.Stop();
    }

    private void OpenCorrectionInput(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is CSimpleCorrectionPanel { IsOffsetOperationRunning: true }) { e.Handled = true; return; }
        if (sender is not TextBox { DataContext: CSimpleCorrectionInput row } input) return;
        e.Handled = true;
        var isX = input.Tag as string == "X";
        var dialog = new CValueInputDialog($"{row.Title} · {(isX ? "X" : "Y")} (mm)",
            isX ? row.InputX : row.InputY, EN_RECIPE_DATA_TYPE.Double, 0, 0) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (isX) row.InputX = dialog.ResultValue;
        else row.InputY = dialog.ResultValue;
    }

    private void PreviewSizeChanged(object sender, SizeChangedEventArgs e) => ResizePreview();
    private void PreviewScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) ResizePreview();
    }
    private void ResizePreview()
    {
        if (DataContext is CSimpleCorrectionPanel panel && SimplePreviewScroll is not null)
            panel.ResizePreview(SimplePreviewScroll.ViewportWidth > 0 ? SimplePreviewScroll.ViewportWidth : SimplePreviewScroll.ActualWidth,
                SimplePreviewScroll.ViewportHeight > 0 ? SimplePreviewScroll.ViewportHeight : SimplePreviewScroll.ActualHeight);
    }
}
