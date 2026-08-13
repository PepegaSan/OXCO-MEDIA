using HailMary.Services;
using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class MediaOverlayViewModel
{
    public string PrimaryActionLabel => Loc.T("common.startExport");

    public IAsyncRelayCommand PrimaryActionCommand => ExportCommand;

    public bool IsPrimaryActionEnabled =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(InputPath)
        && !string.IsNullOrWhiteSpace(OverlayPath);

    public string StatusText => Status;

    bool IToolShellHost.IsBusy => IsBusy;

    public bool HasVideoPreview => false;

    public bool UsesSplitPaneLayout => true;

    public bool HasSettings => false;

    public IRelayCommand? OpenSettingsCommand => null;

    public bool HasOpenFullGui => false;

    public string OpenFullGuiLabel => string.Empty;

    IRelayCommand? IToolShellHost.OpenFullGuiCommand => null;

    public object? SettingsContext => null;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IToolShellHost.IsBusy));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(OverlayAudioHint));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        OnPropertyChanged(nameof(LayoutSummary));
        PreviewHint = string.IsNullOrWhiteSpace(PreviewHint) ? PreviewHint : Loc.T("mediaoverlay.previewHint");
    }
}
