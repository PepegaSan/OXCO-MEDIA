using HailMary.Services;

using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class BitrateChangerViewModel
{
    public string PrimaryActionLabel => Loc.T("bitrate.startConversion");

    public IAsyncRelayCommand PrimaryActionCommand => ConvertCommand;

    public bool IsPrimaryActionEnabled => !IsBusy && Rows.Any(row => row.CanConvert && row.IsIncluded);

    public string StatusText => Status;

    bool IToolShellHost.IsBusy => IsBusy;

    public bool HasVideoPreview => false;

    public bool UsesSplitPaneLayout => true;

    public bool HasSettings => false;

    public IRelayCommand? OpenSettingsCommand => null;

    public bool HasOpenFullGui => OpenFullGuiCommand is not null;

    public string OpenFullGuiLabel => Loc.T("common.originalGui");

    IRelayCommand? IToolShellHost.OpenFullGuiCommand => OpenFullGuiCommand;

    public object? SettingsContext => null;

    public bool HasStopAction => true;

    public string StopActionLabel => Loc.T("common.stopButton");

    public IRelayCommand? StopActionCommand => StopConversionCommand;

    public bool IsStopActionEnabled => CanStopConversion();

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanEditOutput));
        PickInputFileCommand.NotifyCanExecuteChanged();
        PickInputFolderCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IToolShellHost.IsBusy));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        OnPropertyChanged(nameof(IsStopActionEnabled));
        StopConversionCommand.NotifyCanExecuteChanged();
    }

    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(StopActionLabel));
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        OnPropertyChanged(nameof(StatusText));
        foreach (var row in Rows)
        {
            row.ApplyLocalization();
        }
    }

}
