using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using Drilling.UI.Menu.Menus;
using Drilling.Common.Station;

namespace Drilling.UI.Views;

public partial class CMainSequenceStatusView : UserControl
{
    public CMainSequenceStatusView() => InitializeComponent();
    private void CardClicked(object sender, MouseButtonEventArgs e) => OpenMonitor(sender);
    private void CardKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space) OpenMonitor(sender);
    }
    private void OpenMonitor(object sender)
    {
        if (sender is not FrameworkElement { DataContext: CMainSequenceCard card } || DataContext is not CMainSequenceStatus status) return;
        if (card.Id == "REVIEW_MEASURE") status.OpenReviewMonitor?.Invoke();
        else if (card.Id == "TPM_MEASURE") status.OpenPowerMeasureMonitor?.Invoke();
        else if (card.Id == CSequenceActionIds.ProcessZeroResults) status.OpenZeroLineResults?.Invoke();
    }
}
