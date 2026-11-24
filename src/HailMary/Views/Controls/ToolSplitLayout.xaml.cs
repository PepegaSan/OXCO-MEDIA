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
            // Forms need a scrollable viewport. Preview grids and panels with their own
            // scroll viewers need the finite height supplied by the shell.
            layout.MainPresenter.Content = e.NewValue is StackPanel { Orientation: Orientation.Vertical } form
                ? new ScrollViewer
                {
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = form,
                }
                : e.NewValue;
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
        ((ToolSplitLayout)d).UpdatePanes();
    }

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePanes();

    private void PaneButton_Click(object sender, RoutedEventArgs e) => UpdatePanes();

    private void UpdatePanes()
    {
        var compact = ActualWidth > 0 && ActualWidth < SettingsWidth + 640;
        var showSettings = SettingsPaneButton.IsChecked == true;
        PaneSelector.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        SettingsColumn.Width = new GridLength(compact ? 0 : SettingsWidth);
        Grid.SetColumn(SettingsPane, compact ? 0 : 1);
        MainPresenter.Visibility = compact && showSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPane.Visibility = compact && !showSettings ? Visibility.Collapsed : Visibility.Visible;
    }
}
