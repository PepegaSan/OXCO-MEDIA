using HailMary.Services;

using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class BitrateChangerViewModel
{
    public string PrimaryActionLabel => Loc.T("bitrate.startConversion");

    public IAsyncRelayCommand PrimaryActionCommand => ConvertCommand;

    public bool IsPrimaryActionEnabled => !IsBusy;

    public string StatusText => Status;

    bool IToolShellHost.IsBusy => IsBusy;

    public bool HasVideoPreview => false;

    public bool HasSettings => false;

    public IRelayCommand? OpenSettingsCommand => null;

    public bool HasOpenFullGui => OpenFullGuiCommand is not null;

    public string OpenFullGuiLabel => Loc.T("common.originalGui");

    IRelayCommand? IToolShellHost.OpenFullGuiCommand => OpenFullGuiCommand;

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
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        OnPropertyChanged(nameof(StatusText));
        foreach (var row in Rows)
        {
            row.ApplyLocalization();
        }
    }

}
