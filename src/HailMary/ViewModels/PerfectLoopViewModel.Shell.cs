using HailMary.Services;
using CommunityToolkit.Mvvm.Input;

namespace HailMary.ViewModels;

public partial class PerfectLoopViewModel
{
    public string PrimaryActionLabel => ExportButtonLabel;

    public IAsyncRelayCommand PrimaryActionCommand => ExportCommand;

    public bool IsPrimaryActionEnabled => !IsBusy && BatchFiles.Count > 0;

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
        OnPropertyChanged(nameof(ExportButtonLabel));
        OnPropertyChanged(nameof(AnalyzeButtonLabel));
        OnPropertyChanged(nameof(FindBestButtonLabel));
        OnPropertyChanged(nameof(SeamSecondsLabel));
        OnPropertyChanged(nameof(SeamSliderEnabled));
        OnPropertyChanged(nameof(HasVideo));
        RebuildSeamMethodChoices(selectId: SelectedSeamMethodItem?.Id);
        NotifyRangeUi();
        _suppressAutoAnalyze = true;
        try
        {
            RefreshBatchFiles(preserveQualityPreview: true);
        }
        finally
        {
            _suppressAutoAnalyze = false;
        }
    }
}
