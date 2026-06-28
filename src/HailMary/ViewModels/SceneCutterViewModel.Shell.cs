using HailMary.Services;

using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using System.Collections.Specialized;

namespace HailMary.ViewModels;

public partial class SceneCutterViewModel
{
    private void Scenes_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));

        if (e.NewItems is not null)
        {
            foreach (SceneEntry scene in e.NewItems)
            {
                scene.PropertyChanged += Scene_OnPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (SceneEntry scene in e.OldItems)
            {
                scene.PropertyChanged -= Scene_OnPropertyChanged;
            }
        }
    }

    private void Scene_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SceneEntry.IsSelected))
        {
            ScheduleAutosave();
        }
    }

    public string PrimaryActionLabel => UseDavinciExport ? Loc.T("scenecutter.startDavinciExport") : Loc.T("scenecutter.startExport");

    public IAsyncRelayCommand PrimaryActionCommand => PrimaryExportCommand;

    public bool IsPrimaryActionEnabled => !IsRunning && HasVideo && Scenes.Count > 0;

    public string StatusText => Status;

    public bool IsBusy => IsRunning;

    public bool HasVideoPreview => true;

    public bool HasSettings => true;

    IRelayCommand? IToolShellHost.OpenSettingsCommand => SaveSettingsCommand;

    public bool HasOpenFullGui => true;

    public string OpenFullGuiLabel => Loc.T("common.originalGui");

    IRelayCommand? IToolShellHost.OpenFullGuiCommand => OpenFullGuiCommand;

    public virtual object? SettingsContext => null;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    partial void OnHasVideoChanged(bool value) => OnPropertyChanged(nameof(IsPrimaryActionEnabled));

    partial void OnUseDavinciExportChanged(bool value) => OnPropertyChanged(nameof(PrimaryActionLabel));

    [RelayCommand]
    private async Task PrimaryExportAsync()
    {
        if (UseDavinciExport)
        {
            await ExportDavinciAsync();
        }
        else
        {
            await ExportFfmpegAsync();
        }
    }

    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(OpenFullGuiLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PartialSelectButtonText));
        UpdatePendingMarks();
    }

}
