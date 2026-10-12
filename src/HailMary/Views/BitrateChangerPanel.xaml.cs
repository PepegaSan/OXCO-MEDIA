using HailMary.Services;
using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views;

public sealed partial class BitrateChangerPanel : UserControl
{
    public BitrateChangerViewModel ViewModel { get; }

    public BitrateChangerPanel(BitrateChangerViewModel viewModel)
    {
        ViewModel = viewModel;
        ViewModel.ConfirmAsync = ConfirmAsync;
        InitializeComponent();
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords },
            PrimaryButtonText = Loc.T("common.yes"),
            CloseButtonText = Loc.T("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        return (await dialog.ShowAsync()) == ContentDialogResult.Primary;
    }

    private async void ScenarioHelp_Click(object sender, RoutedEventArgs e)
    {
        var body = new TextBlock
        {
            Text = Loc.T("bitrate.scenarioHelpBody"),
            TextWrapping = TextWrapping.WrapWholeWords,
            IsTextSelectionEnabled = true,
        };

        var dialog = new ContentDialog
        {
            Title = Loc.T("bitrate.scenarioHelpTitle"),
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = body,
            },
            CloseButtonText = Loc.T("common.done"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }
}
