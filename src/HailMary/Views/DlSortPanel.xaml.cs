using HailMary.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views;

public sealed partial class DlSortPanel : UserControl
{
    public DlSortViewModel ViewModel { get; }

    public DlSortPanel(DlSortViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void ProfilesList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedProfile = ProfilesList.SelectedItem as DlSortProfileRow;
        ProfilesList.SelectedItem = ViewModel.SelectedProfile;
    }
}
