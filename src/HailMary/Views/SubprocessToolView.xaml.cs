using HailMary.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views;

public sealed partial class SubprocessToolView : UserControl
{
    public ToolTabViewModel ViewModel { get; }

    public SubprocessToolView(ToolTabViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
