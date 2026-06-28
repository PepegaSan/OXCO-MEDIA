using HailMary.Services;

using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class IntroCutterViewModel
{
    public string PrimaryActionLabel
    {
        get
        {
            var included = BatchFiles.Count(i => i.IsIncluded);
            return included > 1 ? Loc.F("intro.primaryActionBatch", included) : Loc.T("intro.primaryAction");
        }
    }

    public IAsyncRelayCommand PrimaryActionCommand => RunCommand;

    public bool IsPrimaryActionEnabled =>
        !IsRunning && BatchFiles.Any(e => e.IsIncluded);

    public string StatusText => Status;

    public bool IsBusy => IsRunning;

    public bool HasVideoPreview => true;

    public bool HasSettings => false;

    public IRelayCommand? OpenSettingsCommand => null;

    public bool HasOpenFullGui => true;

    public string OpenFullGuiLabel => Loc.T("intro.openFullGui");

    IRelayCommand? IToolShellHost.OpenFullGuiCommand => OpenFullGuiCommand;

    public object? SettingsContext => null;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    partial void OnHasVideoChanged(bool value) => OnPropertyChanged(nameof(IsPrimaryActionEnabled));

    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        OnPropertyChanged(nameof(StatusText));
    }

}
