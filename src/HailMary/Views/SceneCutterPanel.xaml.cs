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
        ScenesHost.Content = new SceneCutterScenesPanel(viewModel);
    }
}
