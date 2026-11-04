using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Drilling.UI.Menu.Menus;

namespace Drilling.UI.Views;

public partial class CFineCorrectionView : UserControl
{
    private readonly DispatcherTimer _feedback = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
    public CFineCorrectionView()
    {
        InitializeComponent();
        _feedback.Tick += (_, _) => (DataContext as CFineCorrectionPanel)?.RefreshFeedback();
        Loaded += (_, _) => { (DataContext as CFineCorrectionPanel)?.RefreshFeedback(); _feedback.Start(); };
        Unloaded += (_, _) => _feedback.Stop();
    }
}
