using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Drilling.UI;
public static class CPreviewCoordinateBehavior
{
    public static readonly DependencyProperty PositionXProperty = CreateInputProperty("PositionX");
    public static readonly DependencyProperty PositionYProperty = CreateInputProperty("PositionY");
    public static readonly DependencyProperty ContainerWidthProperty = CreateInputProperty("ContainerWidth");
    public static readonly DependencyProperty ContainerHeightProperty = CreateInputProperty("ContainerHeight");
    public static readonly DependencyProperty DesignWidthProperty = CreateInputProperty("DesignWidth");
    public static readonly DependencyProperty DesignHeightProperty = CreateInputProperty("DesignHeight");
    public static readonly DependencyProperty ElementWidthProperty = CreateInputProperty("ElementWidth");
    public static readonly DependencyProperty ElementHeightProperty = CreateInputProperty("ElementHeight");
    public static readonly DependencyProperty UseStretchScaleProperty = DependencyProperty.RegisterAttached("UseStretchScale", typeof(bool), typeof(CPreviewCoordinateBehavior), new PropertyMetadata(false, OnInputChanged));
    public static object? GetPositionX(DependencyObject target)
    {
        return target.GetValue(PositionXProperty);
    }

    public static void SetPositionX(DependencyObject target, object? value)
    {
        target.SetValue(PositionXProperty, value);
    }

    public static object? GetPositionY(DependencyObject target)
    {
        return target.GetValue(PositionYProperty);
    }

    public static void SetPositionY(DependencyObject target, object? value)
    {
        target.SetValue(PositionYProperty, value);
    }

    public static object? GetContainerWidth(DependencyObject target)
    {
        return target.GetValue(ContainerWidthProperty);
    }

    public static void SetContainerWidth(DependencyObject target, object? value)
    {
        target.SetValue(ContainerWidthProperty, value);
    }

    public static object? GetContainerHeight(DependencyObject target)
    {
        return target.GetValue(ContainerHeightProperty);
    }

    public static void SetContainerHeight(DependencyObject target, object? value)
    {
        target.SetValue(ContainerHeightProperty, value);
    }

    public static object? GetDesignWidth(DependencyObject target)
    {
        return target.GetValue(DesignWidthProperty);
    }

    public static void SetDesignWidth(DependencyObject target, object? value)
    {
        target.SetValue(DesignWidthProperty, value);
    }

    public static object? GetDesignHeight(DependencyObject target)
    {
        return target.GetValue(DesignHeightProperty);
    }

    public static void SetDesignHeight(DependencyObject target, object? value)
    {
        target.SetValue(DesignHeightProperty, value);
    }

    public static object? GetElementWidth(DependencyObject target)
    {
        return target.GetValue(ElementWidthProperty);
    }

    public static void SetElementWidth(DependencyObject target, object? value)
    {
        target.SetValue(ElementWidthProperty, value);
    }

    public static object? GetElementHeight(DependencyObject target)
    {
        return target.GetValue(ElementHeightProperty);
    }

    public static void SetElementHeight(DependencyObject target, object? value)
    {
        target.SetValue(ElementHeightProperty, value);
    }

    public static bool GetUseStretchScale(DependencyObject target)
    {
        return (bool)target.GetValue(UseStretchScaleProperty);
    }

    public static void SetUseStretchScale(DependencyObject target, bool value)
    {
        target.SetValue(UseStretchScaleProperty, value);
    }

    private static DependencyProperty CreateInputProperty(string name)
    {
        return DependencyProperty.RegisterAttached(name, typeof(object), typeof(CPreviewCoordinateBehavior), new PropertyMetadata(null, OnInputChanged));
    }

    private static void OnInputChanged(DependencyObject target, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (target is ContentPresenter presenter)
        {
            UpdateCoordinates(presenter);
        }
    }

    private static void UpdateCoordinates(ContentPresenter presenter)
    {
        if (!TryDouble(GetPositionX(presenter), out var positionX) || !TryDouble(GetPositionY(presenter), out var positionY) || !TryDouble(GetContainerWidth(presenter), out var actualWidth) || !TryDouble(GetContainerHeight(presenter), out var actualHeight) || !TryDouble(GetDesignWidth(presenter), out var designWidth) || !TryDouble(GetDesignHeight(presenter), out var designHeight) || !TryDouble(GetElementWidth(presenter), out var elementWidth) || !TryDouble(GetElementHeight(presenter), out var elementHeight) || actualWidth <= 0.0 || actualHeight <= 0.0 || designWidth <= 0.0 || designHeight <= 0.0)
        {
            Canvas.SetLeft(presenter, 0.0);
            Canvas.SetTop(presenter, 0.0);
            return;
        }

        double left;
        double top;
        if (GetUseStretchScale(presenter))
        {
            var scaleX = actualWidth / designWidth;
            var scaleY = actualHeight / designHeight;
            left = (positionX * scaleX) - (elementWidth / 2.0);
            top = (positionY * scaleY) - (elementHeight / 2.0);
        }
        else
        {
            var scale = Math.Min(actualWidth / designWidth, actualHeight / designHeight);
            var offsetX = (actualWidth - (designWidth * scale)) / 2.0;
            var offsetY = (actualHeight - (designHeight * scale)) / 2.0;
            left = offsetX + (positionX * scale) - (elementWidth / 2.0);
            top = offsetY + (positionY * scale) - (elementHeight / 2.0);
        }

        Canvas.SetLeft(presenter, left);
        Canvas.SetTop(presenter, top);
    }

    private static bool TryDouble(object? value, out double result)
    {
        if (value is double number)
        {
            result = number;
            return true;
        }

        return double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }
}