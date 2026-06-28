using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Services;

/// <summary>UI string lookup and attached properties for XAML (updates on language change).</summary>
public static class Loc
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<FrameworkElement>> Tracked = [];
    private static bool _subscribed;

    public static string T(string key) => AppServices.Localization.Get(key, key);

    public static string F(string key, params object[] args) =>
        string.Format(T(key), args);

    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached(
            "Key",
            typeof(string),
            typeof(Loc),
            new PropertyMetadata(null, OnKeyChanged));

    public static readonly DependencyProperty HeaderKeyProperty =
        DependencyProperty.RegisterAttached(
            "HeaderKey",
            typeof(string),
            typeof(Loc),
            new PropertyMetadata(null, OnHeaderKeyChanged));

    public static readonly DependencyProperty PlaceholderKeyProperty =
        DependencyProperty.RegisterAttached(
            "PlaceholderKey",
            typeof(string),
            typeof(Loc),
            new PropertyMetadata(null, OnPlaceholderKeyChanged));

    public static readonly DependencyProperty ToolTipKeyProperty =
        DependencyProperty.RegisterAttached(
            "ToolTipKey",
            typeof(string),
            typeof(Loc),
            new PropertyMetadata(null, OnToolTipKeyChanged));

    public static void SetKey(DependencyObject o, string value) => o.SetValue(KeyProperty, value);

    public static string GetKey(DependencyObject o) => (string)o.GetValue(KeyProperty);

    public static void SetHeaderKey(DependencyObject o, string value) => o.SetValue(HeaderKeyProperty, value);

    public static string GetHeaderKey(DependencyObject o) => (string)o.GetValue(HeaderKeyProperty);

    public static void SetPlaceholderKey(DependencyObject o, string value) => o.SetValue(PlaceholderKeyProperty, value);

    public static string GetPlaceholderKey(DependencyObject o) => (string)o.GetValue(PlaceholderKeyProperty);

    public static void SetToolTipKey(DependencyObject o, string value) => o.SetValue(ToolTipKeyProperty, value);

    public static string GetToolTipKey(DependencyObject o) => (string)o.GetValue(ToolTipKeyProperty);

    private static void EnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        AppServices.Localization.LanguageChanged += () => UiDispatcher.Run(RefreshAll);
    }

    private static void Track(FrameworkElement element)
    {
        EnsureSubscribed();
        lock (Gate)
        {
            Tracked.RemoveAll(w => !w.TryGetTarget(out _));
            if (Tracked.All(w => !w.TryGetTarget(out var t) || !ReferenceEquals(t, element)))
            {
                Tracked.Add(new WeakReference<FrameworkElement>(element));
            }
        }
    }

    private static void RefreshAll()
    {
        lock (Gate)
        {
            Tracked.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var weak in Tracked)
            {
                if (!weak.TryGetTarget(out var element))
                {
                    continue;
                }

                ApplyKey(element, GetKey(element));
                ApplyHeaderKey(element, GetHeaderKey(element));
                ApplyPlaceholderKey(element, GetPlaceholderKey(element));
                ApplyToolTipKey(element, GetToolTipKey(element));
            }
        }
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        Track(element);
        ApplyKey(element, e.NewValue as string);
    }

    private static void OnHeaderKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        Track(element);
        ApplyHeaderKey(element, e.NewValue as string);
    }

    private static void OnPlaceholderKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        Track(element);
        ApplyPlaceholderKey(element, e.NewValue as string);
    }

    private static void OnToolTipKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        Track(element);
        ApplyToolTipKey(element, e.NewValue as string);
    }

    private static void ApplyKey(FrameworkElement element, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var text = T(key);
        switch (element)
        {
            case TextBlock tb:
                tb.Text = text;
                break;
            case Button btn:
                btn.Content = text;
                break;
            case CheckBox cb:
                cb.Content = text;
                break;
            case RadioButton rb:
                rb.Content = text;
                break;
            case HyperlinkButton hb:
                hb.Content = text;
                break;
            case Expander exp:
                exp.Header = text;
                break;
            case TabViewItem tab:
                tab.Header = text;
                break;
        }
    }

    private static void ApplyHeaderKey(FrameworkElement element, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var text = T(key);
        switch (element)
        {
            case TextBox tb:
                tb.Header = text;
                break;
            case ComboBox cb:
                cb.Header = text;
                break;
            case NumberBox nb:
                nb.Header = text;
                break;
            case TabViewItem tab:
                tab.Header = text;
                break;
        }
    }

    private static void ApplyPlaceholderKey(FrameworkElement element, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var text = T(key);
        if (element is TextBox tb)
        {
            tb.PlaceholderText = text;
        }
    }

    private static void ApplyToolTipKey(FrameworkElement element, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            ToolTipService.SetToolTip(element, null);
            return;
        }

        ToolTipService.SetToolTip(element, T(key));
    }
}
