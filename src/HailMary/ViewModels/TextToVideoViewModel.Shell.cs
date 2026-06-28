using HailMary.Services;

using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class TextToVideoViewModel
{
    public string PrimaryActionLabel => Loc.T("texttovideo.exportFfmpeg");

    public IAsyncRelayCommand PrimaryActionCommand => ExportCommand;

    public bool IsPrimaryActionEnabled => HasVideo && !IsBusy;

    public string StatusText => Status;

    bool IToolShellHost.IsBusy => IsBusy;

    public bool HasVideoPreview => false;

    public bool HasSettings => true;

    IRelayCommand? IToolShellHost.OpenSettingsCommand => SaveSettingsCommand;

    public bool HasOpenFullGui => true;

    public string OpenFullGuiLabel => Loc.T("common.originalGui");

    IRelayCommand? IToolShellHost.OpenFullGuiCommand => OpenFullGuiCommand;

    public object? SettingsContext => null;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IToolShellHost.IsBusy));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    partial void OnHasVideoChanged(bool value) => OnPropertyChanged(nameof(IsPrimaryActionEnabled));

    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        UpdateVideoInfoLine();
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SegmentEditorTitle));
        OnPropertyChanged(nameof(CommitSegmentLabel));
    }

}
