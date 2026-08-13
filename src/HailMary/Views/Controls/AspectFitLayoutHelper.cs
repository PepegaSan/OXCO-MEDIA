using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HailMary.Views.Controls;

internal static class AspectFitLayoutHelper
{
    private const double DefaultFallbackWidth = 640;
    private const double DefaultMaxColumnWidth = 1200;
    private const double ParentSlack = 8;

    public static double GetAvailableWidth(FrameworkElement element, double maxColumnWidth = DefaultMaxColumnWidth)
    {
        var selfWidth = element.ActualWidth;
        for (var parent = VisualTreeHelper.GetParent(element) as FrameworkElement;
             parent is not null;
             parent = VisualTreeHelper.GetParent(parent) as FrameworkElement)
        {
            var width = parent.ActualWidth;
            if (width <= 0 || double.IsNaN(width))
            {
                continue;
            }

            // Skip parents that were sized by this child — otherwise the preview
            // locks onto its own tiny width and never grows back.
            if (selfWidth > 0 && width <= selfWidth + ParentSlack)
            {
                continue;
            }

            return Math.Min(Math.Max(0, width - ParentSlack), maxColumnWidth);
        }

        return Math.Min(DefaultFallbackWidth, maxColumnWidth);
    }

    public static void WatchParentWidthChanges(FrameworkElement element, Action callback)
    {
        FrameworkElement? fallback = null;
        for (var parent = VisualTreeHelper.GetParent(element) as FrameworkElement;
             parent is not null;
             parent = VisualTreeHelper.GetParent(parent) as FrameworkElement)
        {
            if (parent.ActualWidth <= 0)
            {
                continue;
            }

            fallback = parent;
            if (element.ActualWidth <= 0 || parent.ActualWidth > element.ActualWidth + ParentSlack)
            {
                parent.SizeChanged += (_, _) => callback();
                return;
            }
        }

        if (fallback is not null)
        {
            fallback.SizeChanged += (_, _) => callback();
        }
    }

    public static (double DisplayWidth, double DisplayHeight, double RootMinHeight) Compute(
        double availWidth,
        double maxPreviewHeight,
        double minPreviewHeight,
        int contentWidth,
        int contentHeight)
    {
        var availW = availWidth > 0 && !double.IsNaN(availWidth) ? availWidth : DefaultFallbackWidth;
        var maxH = maxPreviewHeight > 0 ? maxPreviewHeight : 720;
        var minH = minPreviewHeight > 0 ? minPreviewHeight : 180;
        if (minH > maxH)
        {
            minH = maxH;
        }

        if (contentWidth <= 0 || contentHeight <= 0)
        {
            return (availW, minH, minH);
        }

        var aspect = (double)contentWidth / contentHeight;
        var displayH = Math.Min(maxH, availW / aspect);
        if (displayH < minH)
        {
            displayH = minH;
        }

        var displayW = displayH * aspect;
        if (displayW > availW && availW >= 1)
        {
            displayW = availW;
            displayH = displayW / aspect;
        }

        displayW = Math.Max(1, displayW);
        displayH = Math.Max(1, displayH);
        return (displayW, displayH, displayH);
    }
}
