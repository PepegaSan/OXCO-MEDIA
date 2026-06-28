using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HailMary.Views.Controls;

public sealed partial class OxcoComparePreviewHost : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(OxcoCompareViewModel),
            typeof(OxcoComparePreviewHost),
            new PropertyMetadata(null));

    public OxcoComparePreviewHost()
    {
        InitializeComponent();
        IsTabStop = true;
    }

    public OxcoCompareViewModel? ViewModel
    {
        get => (OxcoCompareViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void RootHost_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.PreviewIsLoaded)
        {
            return;
        }

        if (e.Key == VirtualKey.Space)
        {
            ViewModel.TogglePreviewPlayCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Left)
        {
            ViewModel.PreviewStepBackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Right)
        {
            ViewModel.PreviewStepForwardCommand.Execute(null);
            e.Handled = true;
        }
    }
}
