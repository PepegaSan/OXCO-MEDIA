using HailMary.ViewModels;
using HailMary.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HailMary;

public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; } = new();

    public UiShellViewModel Shell { get; } = new();

    public MainPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Initialize();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainPageViewModel.LogText))
            {
                LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
            }
        };
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        flyout.Content = new AppSettingsFlyout();
        if (sender is FrameworkElement anchor)
        {
            flyout.ShowAt(anchor);
        }
    }
}
