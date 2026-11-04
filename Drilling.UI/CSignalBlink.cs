using System.Windows;
using System.Windows.Media.Animation;

namespace Drilling.UI;

/// <summary>Runs a pulse only on its overlay; stops old clocks when data, visibility or lifetime changes.</summary>
public static class CSignalBlink
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(CSignalBlink), new PropertyMetadata(false, ActiveChanged));
    private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
        "IsHooked", typeof(bool), typeof(CSignalBlink), new PropertyMetadata(false));
    public static bool GetIsActive(DependencyObject target) => (bool)target.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject target, bool value) => target.SetValue(IsActiveProperty, value);

    private static void ActiveChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        if (!(bool)element.GetValue(IsHookedProperty))
        {
            element.SetValue(IsHookedProperty, true);
            element.Loaded += Loaded;
            element.Unloaded += Unloaded;
            element.IsVisibleChanged += VisibilityChanged;
        }
        if ((bool)e.NewValue) Start(element); else Stop(element);
    }
    private static void Loaded(object sender, RoutedEventArgs e)
    { if (GetIsActive((FrameworkElement)sender)) Start((FrameworkElement)sender); }
    private static void Unloaded(object sender, RoutedEventArgs e) => Stop((FrameworkElement)sender);
    private static void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if ((bool)e.NewValue && GetIsActive(element)) Start(element); else Stop(element);
    }
    private static void Start(FrameworkElement element) => element.BeginAnimation(UIElement.OpacityProperty,
        new DoubleAnimation(0, 0.6, TimeSpan.FromSeconds(0.5)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    private static void Stop(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.SetCurrentValue(UIElement.OpacityProperty, 0d);
    }
}
