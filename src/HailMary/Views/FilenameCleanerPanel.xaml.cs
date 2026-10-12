using HailMary.Services;
using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views;

public sealed partial class FilenameCleanerPanel : UserControl
{
    public FilenameCleanerViewModel ViewModel { get; }

    public FilenameCleanerPanel(FilenameCleanerViewModel viewModel)
    {
        ViewModel = viewModel;
        ViewModel.ConfirmAsync = ConfirmAsync;
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.EnsureStashTagsAsync();
    }

    private void NewTermBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ViewModel.AddTermCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void RemoveTerm_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FilenameTermRow row })
        {
            ViewModel.RemoveTermCommand.Execute(row);
        }
    }

    private void AppendNumber_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FilenameRenameRow row })
        {
            ViewModel.AppendNumberCommand.Execute(row);
        }
    }

    private void OpenConflict_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FilenameRenameRow row })
        {
            ViewModel.OpenConflictSceneCommand.Execute(row);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords },
            PrimaryButtonText = Loc.T("common.continue"),
            CloseButtonText = Loc.T("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        return (await dialog.ShowAsync()) == ContentDialogResult.Primary;
    }
}
