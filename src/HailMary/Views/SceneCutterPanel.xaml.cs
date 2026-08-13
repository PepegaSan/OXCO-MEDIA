using HailMary.ViewModels;
using HailMary.Views.Controls;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views;

public sealed partial class SceneCutterPanel : UserControl
{
    public SceneCutterViewModel ViewModel { get; }

    public SceneCutterPanel(SceneCutterViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        PreviewHost.Content = new SceneCutterVideoPreviewHost(viewModel);
        ScenesHost.Content = new SceneCutterScenesPanel(viewModel);
        SettingsHost.Content = new SceneCutterSettingsPanel(viewModel);
    }
}
