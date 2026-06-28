using HailMary.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views;

public sealed partial class DatenSyncPanel : UserControl
{
    public DatenSyncViewModel ViewModel { get; }

    public DatenSyncPanel(DatenSyncViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void JobsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JobsList.SelectedIndex >= 0)
        {
            ViewModel.SelectJob(JobsList.SelectedIndex);
        }
    }
}
