using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views.Controls;

public sealed partial class ToolSplitLayout : UserControl
{
    public static readonly DependencyProperty MainProperty =
        DependencyProperty.Register(
            nameof(Main),
            typeof(object),
            typeof(ToolSplitLayout),
            new PropertyMetadata(null, OnMainChanged));

    public static readonly DependencyProperty SettingsProperty =
        DependencyProperty.Register(
            nameof(Settings),
            typeof(object),
            typeof(ToolSplitLayout),
            new PropertyMetadata(null, OnSettingsChanged));

    public static readonly DependencyProperty SettingsWidthProperty =
        DependencyProperty.Register(
            nameof(SettingsWidth),
            typeof(double),
            typeof(ToolSplitLayout),
            new PropertyMetadata(360d, OnSettingsWidthChanged));

    public ToolSplitLayout()
    {
        InitializeComponent();
    }

    public object? Main
    {
        get => GetValue(MainProperty);
        set => SetValue(MainProperty, value);
    }

    public object? Settings
    {
        get => GetValue(SettingsProperty);
        set => SetValue(SettingsProperty, value);
    }

    public double SettingsWidth
    {
        get => (double)GetValue(SettingsWidthProperty);
        set => SetValue(SettingsWidthProperty, value);
    }

    private static void OnMainChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolSplitLayout layout)
        {
            layout.MainPresenter.Content = e.NewValue;
        }
    }

    private static void OnSettingsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolSplitLayout layout)
        {
            layout.SettingsPresenter.Content = e.NewValue;
        }
    }

    private static void OnSettingsWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolSplitLayout layout && e.NewValue is double width && width > 0)
        {
            layout.SettingsColumn.Width = new GridLength(width);
        }
    }
}
