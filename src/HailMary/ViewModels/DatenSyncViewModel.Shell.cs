using HailMary.Services;

using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class DatenSyncViewModel
{
    private IAsyncRelayCommand? _primaryActionCommand;

    public string PrimaryActionLabel => Loc.T("datensync.primaryAction");

    public IAsyncRelayCommand PrimaryActionCommand =>
        _primaryActionCommand ??= new AsyncRelayCommand(() =>
        {
            StartSync();
            return Task.CompletedTask;
        });

    public bool IsPrimaryActionEnabled => CanStart;

    public string StatusText => Status;

    bool IToolShellHost.IsBusy => IsRunning;

    public bool HasVideoPreview => false;

    public bool HasSettings => false;

    public IRelayCommand? OpenSettingsCommand => null;

    public bool HasOpenFullGui => false;

    public string OpenFullGuiLabel => string.Empty;

    IRelayCommand? IToolShellHost.OpenFullGuiCommand => null;

    public object? SettingsContext => null;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        foreach (var option in Options)
        {
            option.ApplyLocalization();
        }

        OnPropertyChanged(nameof(StatusText));
    }

}
