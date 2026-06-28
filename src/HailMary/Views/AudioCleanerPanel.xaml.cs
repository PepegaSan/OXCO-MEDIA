using HailMary.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views;

public sealed partial class AudioCleanerPanel : UserControl
{
    public AudioCleanerViewModel ViewModel { get; }

    public AudioCleanerPanel(AudioCleanerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
