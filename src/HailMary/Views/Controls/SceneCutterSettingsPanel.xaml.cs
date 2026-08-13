using HailMary.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views.Controls;

public sealed partial class SceneCutterSettingsPanel : UserControl
{
    public SceneCutterSettingsPanel(SceneCutterViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SceneCutterViewModel ViewModel { get; }
}
