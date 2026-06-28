using HailMary.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views;

public sealed partial class BitrateChangerPanel : UserControl
{
    public BitrateChangerViewModel ViewModel { get; }

    public BitrateChangerPanel(BitrateChangerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
