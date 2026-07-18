using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed partial class AutotaggerQueueRow : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private string _tag = string.Empty;

    [ObservableProperty] private string _outputFolder = string.Empty;
}

public sealed partial class AutotaggerCategoryRow : ObservableObject
{
    [ObservableProperty] private string _label = string.Empty;

    [ObservableProperty] private string _tag = string.Empty;

    [ObservableProperty] private bool _isSelected;
}

public partial class AutotaggerViewModel : ObservableObject, IToolShellHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private AutotaggerSettings _settings;

    public AutotaggerViewModel(ToolDefinition tool)
    {
        _tool = tool;
        _settings = AutotaggerConfigReader.Load();
        InputFolder = _settings.InputFolder;
        OutputFolder = _settings.OutputFolder;
        KeepSuffix = _settings.KeepSuffix;
        IgnoreSuffix = _settings.IgnoreSuffix;
        DropSuffix = _settings.DropSuffix;
        PatternToReplace = _settings.PatternToReplace;
        ProcessExisting = _settings.ProcessExisting;
        ExtraTag = string.Empty;
        RefreshQueue();
        RefreshCategories();
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<AutotaggerQueueRow> Queue { get; } = [];

    public ObservableCollection<AutotaggerCategoryRow> Categories { get; } = [];

    [ObservableProperty] private string _inputFolder = string.Empty;
    [ObservableProperty] private string _outputFolder = string.Empty;
    [ObservableProperty] private string _keepSuffix = "_hyb,_pro,_exp";
    [ObservableProperty] private string _ignoreSuffix = "_p";
    [ObservableProperty] private string _dropSuffix = string.Empty;
    [ObservableProperty] private string _patternToReplace = "YYMMDDHHmmSS";
    [ObservableProperty] private bool _processExisting;
    [ObservableProperty] private string _extraTag = string.Empty;
    [ObservableProperty] private string _manualName = string.Empty;
    [ObservableProperty] private string _manualTag = string.Empty;
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _monitorRunning;

    [ObservableProperty] private AutotaggerQueueRow? _selectedQueueItem;

    private void RefreshQueue()
    {
        Queue.Clear();
        foreach (var item in _settings.Queue)
        {
            Queue.Add(new AutotaggerQueueRow
            {
                Name = item.Name,
                Tag = item.Tag,
                OutputFolder = item.OutputFolder,
            });
        }
    }

    private void RefreshCategories()
    {
        Categories.Clear();
        foreach (var cat in AutotaggerConfigReader.LoadCategories())
        {
            Categories.Add(new AutotaggerCategoryRow { Label = cat.Label, Tag = cat.Tag });
        }
    }

    private AutotaggerSettings BuildSettings()
    {
        return new AutotaggerSettings
        {
            InputFolder = InputFolder,
            OutputFolder = OutputFolder,
            KeepSuffix = KeepSuffix,
            IgnoreSuffix = IgnoreSuffix,
            DropSuffix = DropSuffix,
            PatternToReplace = PatternToReplace,
            ProcessExisting = ProcessExisting,
            Queue = Queue.Select(q => new AutotaggerQueueItem
            {
                Name = q.Name,
                Tag = q.Tag,
                OutputFolder = string.IsNullOrWhiteSpace(q.OutputFolder) ? OutputFolder : q.OutputFolder,
            }).ToList(),
        };
    }

    private void PersistSettings()
    {
        _settings = BuildSettings();
        AutotaggerConfigReader.Save(_settings);
    }

    [RelayCommand]
    private async Task PickInputFolderAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(InputFolder);
        if (!string.IsNullOrWhiteSpace(path))
        {
            InputFolder = path;
        }
    }

    [RelayCommand]
    private async Task PickOutputFolderAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(OutputFolder);
        if (!string.IsNullOrWhiteSpace(path))
        {
            OutputFolder = path;
        }
    }

    [RelayCommand]
    private void AddManualQueueItem()
    {
        if (string.IsNullOrWhiteSpace(OutputFolder) || !Directory.Exists(OutputFolder))
        {
            Status = Loc.T("autotagger.setDefaultOutput");
            return;
        }

        if (string.IsNullOrWhiteSpace(ManualName) || string.IsNullOrWhiteSpace(ManualTag))
        {
            Status = Loc.T("autotagger.nameTagRequired");
            return;
        }

        Queue.Add(new AutotaggerQueueRow
        {
            Name = ManualName.Trim(),
            Tag = ManualTag.Trim(),
            OutputFolder = OutputFolder,
        });
        ManualName = string.Empty;
        ManualTag = string.Empty;
        Status = Loc.T("autotagger.profileQueued");
    }

    [RelayCommand]
    private void AddSelectedCategories()
    {
        if (string.IsNullOrWhiteSpace(OutputFolder) || !Directory.Exists(OutputFolder))
        {
            Status = Loc.T("autotagger.setDefaultOutput");
            return;
        }

        var selected = Categories.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Status = Loc.T("autotagger.selectCategories");
            return;
        }

        foreach (var cat in selected)
        {
            var tag = cat.Tag.Trim();
            if (!string.IsNullOrWhiteSpace(ExtraTag))
            {
                tag = $"{tag} {ExtraTag.Trim()}".Trim();
            }

            Queue.Add(new AutotaggerQueueRow
            {
                Name = cat.Label,
                Tag = tag,
                OutputFolder = OutputFolder,
            });
            cat.IsSelected = false;
        }

        Status = Loc.F("autotagger.categoriesQueued", selected.Count);
    }

    [RelayCommand]
    private void RemoveSelectedQueueItem()
    {
        if (SelectedQueueItem is null)
        {
            Status = Loc.T("autotagger.selectQueueItem");
            return;
        }

        Queue.Remove(SelectedQueueItem);
        SelectedQueueItem = Queue.FirstOrDefault();
        Status = Loc.T("autotagger.entryRemoved");
    }

    [RelayCommand]
    private void MoveQueueUp()
    {
        if (SelectedQueueItem is null)
        {
            return;
        }

        var idx = Queue.IndexOf(SelectedQueueItem);
        if (idx <= 0)
        {
            return;
        }

        Queue.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveQueueDown()
    {
        if (SelectedQueueItem is null)
        {
            return;
        }

        var idx = Queue.IndexOf(SelectedQueueItem);
        if (idx < 0 || idx >= Queue.Count - 1)
        {
            return;
        }

        Queue.Move(idx, idx + 1);
    }

    [RelayCommand]
    private void SaveSettings()
    {
        PersistSettings();
        Status = Loc.T("common.settingsSaved");
    }

    [RelayCommand]
    private void StartMonitor()
    {
        if (string.IsNullOrWhiteSpace(InputFolder) || !Directory.Exists(InputFolder))
        {
            Status = Loc.T("autotagger.inputFolderMissing");
            return;
        }

        if (ProcessExisting && Queue.Count == 0)
        {
            Status = Loc.T("autotagger.queueEmptyForExisting");
            return;
        }

        PersistSettings();
        AutotaggerConfigReader.SaveMonitorConfig(_settings);

        if (AppServices.MonitorRunner.IsRunning)
        {
            AppServices.MonitorRunner.Stop();
        }

        var ok = AppServices.MonitorRunner.Start("autotagger_monitor_job.py",
            ["--config-json", AutotaggerConfigReader.MonitorConfigPath]);
        MonitorRunning = ok;
        Status = ok ? Loc.T("autotagger.monitorStarted") : Loc.T("autotagger.monitorStartFailed");
    }

    [RelayCommand]
    private void StopMonitor()
    {
        AppServices.MonitorRunner.Stop();
        MonitorRunning = false;
        Status = Loc.T("autotagger.monitorStopped");
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        KeepSuffix = "_hyb,_pro,_exp";
        IgnoreSuffix = "_p";
        DropSuffix = string.Empty;
        PatternToReplace = "YYMMDDHHmmSS";
        Status = Loc.T("autotagger.defaultsRestored");
    }

    [RelayCommand]
    private void OpenFullGui() => AppServices.Launcher.Launch(_tool);
}
