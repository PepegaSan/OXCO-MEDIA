using HailMary.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views;

public sealed partial class MediaOverlayPanel : UserControl
{
    public MediaOverlayViewModel ViewModel { get; }

    public MediaOverlayPanel(MediaOverlayViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
