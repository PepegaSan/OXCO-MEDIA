using HailMary.Services;

using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class DlSortViewModel
{
    private IAsyncRelayCommand? _primaryActionCommand;

    public string PrimaryActionLabel => Loc.T("common.startMonitor");

    public IAsyncRelayCommand PrimaryActionCommand =>
        _primaryActionCommand ??= new AsyncRelayCommand(() =>
        {
            StartMonitor();
            return Task.CompletedTask;
        });

    public bool IsPrimaryActionEnabled => !MonitorRunning;

    public string StatusText => Status;

    bool IToolShellHost.IsBusy => MonitorRunning;

    public bool HasVideoPreview => false;

    public bool HasSettings => false;

    public IRelayCommand? OpenSettingsCommand => null;

    public bool HasOpenFullGui => true;

    public string OpenFullGuiLabel => Loc.T("common.originalGui");

    IRelayCommand? IToolShellHost.OpenFullGuiCommand => OpenFullGuiCommand;

    public object? SettingsContext => null;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnMonitorRunningChanged(bool value)
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
    }

}
