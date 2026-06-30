using HailMary.Services;

using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class ClipJoinerViewModel
{
    public string PrimaryActionLabel => RunCurrentLabel;

    public IAsyncRelayCommand PrimaryActionCommand => RunCurrentCommand;

    public bool IsPrimaryActionEnabled => !IsBusy && ClipRows.Count > 0;

    public string StatusText => Status;

    bool IToolShellHost.IsBusy => IsBusy;

    public bool HasVideoPreview => false;

    public bool HasSettings => false;

    public IRelayCommand? OpenSettingsCommand => null;

    public bool HasOpenFullGui => true;

    public string OpenFullGuiLabel => Loc.T("common.originalGui");

    IRelayCommand? IToolShellHost.OpenFullGuiCommand => OpenFullGuiCommand;

    public object? SettingsContext => null;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RunCurrentLabel));
        OnPropertyChanged(nameof(RunBatchLabel));
        RefreshBatchRows();
    }

}
