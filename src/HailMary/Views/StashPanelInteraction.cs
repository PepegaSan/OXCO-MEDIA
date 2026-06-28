using CommunityToolkit.Mvvm.Input;
using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HailMary.Views;

internal static class StashPanelInteraction
{
    public static async void SearchBox_OnKeyDown(object sender, KeyRoutedEventArgs e, IAsyncRelayCommand searchCommand)
    {
        if (e.Key == VirtualKey.Enter && searchCommand.CanExecute(null))
        {
            e.Handled = true;
            await searchCommand.ExecuteAsync(null);
        }
    }

    public static async void SceneIdBox_OnKeyDown(object sender, KeyRoutedEventArgs e, IAsyncRelayCommand loadCommand)
    {
        if (e.Key == VirtualKey.Enter && loadCommand.CanExecute(null))
        {
            e.Handled = true;
            await loadCommand.ExecuteAsync(null);
        }
    }

    public static async void ResultsList_OnDoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e,
        ListView list,
        Action<StashSceneRowViewModel> setSelection,
        IAsyncRelayCommand loadCommand)
    {
        if (FindResultRow(e.OriginalSource) is not StashSceneRowViewModel row)
        {
            return;
        }

        setSelection(row);
        list.SelectedItem = row;
        e.Handled = true;

        if (loadCommand.CanExecute(null))
        {
            await loadCommand.ExecuteAsync(null);
        }
    }

    public static StashSceneRowViewModel? FindResultRow(object? source)
    {
        if (source is not DependencyObject node)
        {
            return null;
        }

        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: StashSceneRowViewModel row })
            {
                return row;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }
}
