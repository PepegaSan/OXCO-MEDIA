using HailMary.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views;

public sealed partial class PerfectLoopPanel : UserControl
{
    public PerfectLoopViewModel ViewModel { get; }

    public PerfectLoopPanel(PerfectLoopViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
