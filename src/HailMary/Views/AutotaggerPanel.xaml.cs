using HailMary.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views;

public sealed partial class AutotaggerPanel : UserControl
{
    public AutotaggerViewModel ViewModel { get; }

    public AutotaggerPanel(AutotaggerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void QueueList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedQueueItem = QueueList.SelectedItem as AutotaggerQueueRow;
    }
}
