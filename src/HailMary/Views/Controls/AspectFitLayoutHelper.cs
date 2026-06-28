using Microsoft.UI.Xaml;

namespace HailMary.Views.Controls;

internal static class AspectFitLayoutHelper
{
    private const double DefaultFallbackWidth = 640;
    private const double DefaultMaxColumnWidth = 960;

    public static double GetAvailableWidth(FrameworkElement element, double maxColumnWidth = DefaultMaxColumnWidth)
    {
        if (element.ActualWidth > 0 && !double.IsNaN(element.ActualWidth))
        {
            return Math.Min(element.ActualWidth, maxColumnWidth);
        }

        for (var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element) as FrameworkElement;
             parent is not null;
             parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent) as FrameworkElement)
        {
            if (parent.ActualWidth > 0 && !double.IsNaN(parent.ActualWidth))
            {
                return Math.Min(parent.ActualWidth, maxColumnWidth);
            }
        }

        return Math.Min(DefaultFallbackWidth, maxColumnWidth);
    }

    public static void WatchParentWidthChanges(FrameworkElement element, Action callback)
    {
        for (var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element) as FrameworkElement;
             parent is not null;
             parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent) as FrameworkElement)
        {
            if (parent.ActualWidth > 0)
            {
                parent.SizeChanged += (_, _) => callback();
                return;
            }
        }
    }

    public static (double DisplayWidth, double DisplayHeight, double RootMinHeight) Compute(
        double availWidth,
        double maxPreviewHeight,
        double minPreviewHeight,
        int contentWidth,
        int contentHeight)
    {
        var availW = availWidth > 0 && !double.IsNaN(availWidth) ? availWidth : 640;
        var maxH = maxPreviewHeight > 0 ? maxPreviewHeight : 720;
        var minH = minPreviewHeight > 0 ? minPreviewHeight : 120;

        if (contentWidth <= 0 || contentHeight <= 0)
        {
            return (availW, minH, minH);
        }

        var scale = Math.Min(availW / contentWidth, maxH / contentHeight);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            scale = 1;
        }

        var displayW = Math.Max(1, contentWidth * scale);
        var displayH = Math.Max(1, contentHeight * scale);
        return (displayW, displayH, displayH);
    }
}
