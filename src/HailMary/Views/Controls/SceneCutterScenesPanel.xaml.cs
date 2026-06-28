using HailMary.Models;
using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HailMary.Views.Controls;

public sealed partial class SceneCutterScenesPanel : UserControl
{
    public SceneCutterScenesPanel(SceneCutterViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SceneCutterViewModel ViewModel { get; }

    private void SceneTime_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: SceneEntry entry })
        {
            ViewModel.CommitSceneTimes(entry);
        }
    }

    private void SeekScene_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SceneEntry entry })
        {
            ViewModel.SeekToScene(entry);
        }
    }

    private void RemoveScene_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SceneEntry entry })
        {
            ViewModel.RemoveSceneCommand.Execute(entry);
        }
    }

    private void MoveSceneUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SceneEntry entry })
        {
            ViewModel.MoveSceneUpCommand.Execute(entry);
        }
    }

    private void MoveSceneDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SceneEntry entry })
        {
            ViewModel.MoveSceneDownCommand.Execute(entry);
        }
    }

    private void ScenePosition_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || sender is not TextBox { Tag: SceneEntry entry })
        {
            return;
        }

        ViewModel.ApplyScenePosition(entry);
        e.Handled = true;
    }
}
