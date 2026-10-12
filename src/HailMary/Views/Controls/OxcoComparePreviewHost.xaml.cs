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
        if (ViewModel is null || !ViewModel.PreviewIsLoaded || e.OriginalSource is TextBox or NumberBox)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Space:
                ViewModel.TogglePreviewPlayCommand.Execute(null);
                break;
            case VirtualKey.Left:
                ViewModel.PreviewStepBackCommand.Execute(null);
                break;
            case VirtualKey.Right:
                ViewModel.PreviewStepForwardCommand.Execute(null);
                break;
            case VirtualKey.PageUp:
                ViewModel.PreviewJumpPrevCutCommand.Execute(null);
                break;
            case VirtualKey.PageDown:
                ViewModel.PreviewJumpNextCutCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Timeline_SeekRequested(object sender, int frame)
    {
        Focus(FocusState.Programmatic);
        ViewModel?.SeekPreviewToFrame(frame);
    }
}
