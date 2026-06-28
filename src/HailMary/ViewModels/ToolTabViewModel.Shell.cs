using HailMary.Services;

using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class ToolTabViewModel
{
    private IAsyncRelayCommand? _primaryActionCommand;

    public string PrimaryActionLabel => Loc.T("common.start");

    public IAsyncRelayCommand PrimaryActionCommand =>
        _primaryActionCommand ??= new AsyncRelayCommand(() =>
        {
            Launch();
            return Task.CompletedTask;
        });

    public bool IsPrimaryActionEnabled => true;

    public string StatusText => Status;

    public bool IsBusy => false;

    public bool HasVideoPreview => false;

    public bool HasSettings => false;

    public IRelayCommand? OpenSettingsCommand => null;

    public bool HasOpenFullGui => false;

    public string OpenFullGuiLabel => string.Empty;

    public IRelayCommand? OpenFullGuiCommand => null;

    public object? SettingsContext => null;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        OnPropertyChanged(nameof(StatusText));
    }

}
