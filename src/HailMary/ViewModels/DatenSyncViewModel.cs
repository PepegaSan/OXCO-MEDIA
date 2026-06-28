using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed partial class RobocopyOptionViewModel : ObservableObject
{
    private readonly string _labelKey;

    public RobocopyOptionViewModel(string labelKey, string switchName, bool isEnabled)
    {
        _labelKey = labelKey;
        Switch = switchName;
        IsEnabled = isEnabled;
        Label = Loc.T(labelKey);
    }

    [ObservableProperty] private string _label;

    public string Switch { get; }

    [ObservableProperty] private bool _isEnabled;

    public void ApplyLocalization() => Label = Loc.T(_labelKey);
}

public sealed partial class DatenSyncJobRow : ObservableObject
{
    [ObservableProperty] private string _source = string.Empty;

    [ObservableProperty] private string _target = string.Empty;
}

public partial class DatenSyncViewModel : ObservableObject, IToolShellHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private DatenSyncProfile _profile;
    private int? _selectedJobIndex;

    public DatenSyncViewModel(ToolDefinition tool)
    {
        _tool = tool;
        _profile = DatenSyncConfigReader.Load();
        EditorSource = _profile.EditorSource;
        EditorTarget = _profile.EditorTarget;
        IntervalMinutes = _profile.IntervalMinutes;
        ProfileName = _profile.SelectedProfile;
        RefreshProfileNames();
        RefreshOptions();
        RefreshJobs();
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<DatenSyncJobRow> Jobs { get; } = [];

    public ObservableCollection<RobocopyOptionViewModel> Options { get; } = [];

    public ObservableCollection<string> ProfileNames { get; } = [];

    [ObservableProperty] private string _editorSource = string.Empty;
    [ObservableProperty] private string _editorTarget = string.Empty;
    [ObservableProperty] private string _intervalMinutes = "60";
    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private string _selectedProfileName = string.Empty;
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isRunning;

    public bool CanStart => !IsRunning && Jobs.Count > 0;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IToolShellHost.IsBusy));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    private void RefreshOptions()
    {
        Options.Clear();
        foreach (var (labelKey, switchName) in DatenSyncConfigReader.OptionDefinitions)
        {
            var enabled = _profile.Options.GetValueOrDefault(switchName, false);
            Options.Add(new RobocopyOptionViewModel(labelKey, switchName, enabled));
        }
    }

    private void RefreshJobs()
    {
        Jobs.Clear();
        foreach (var job in _profile.Jobs)
        {
            Jobs.Add(new DatenSyncJobRow { Source = job.Source, Target = job.Target });
        }

        OnPropertyChanged(nameof(CanStart));
    }

    private void RefreshProfileNames()
    {
        ProfileNames.Clear();
        foreach (var name in DatenSyncConfigReader.ListProfileNames())
        {
            ProfileNames.Add(name);
        }
    }

    private void SyncProfile()
    {
        _profile.EditorSource = EditorSource;
        _profile.EditorTarget = EditorTarget;
        _profile.IntervalMinutes = IntervalMinutes;
        _profile.SelectedProfile = SelectedProfileName;
        _profile.Options = Options.ToDictionary(o => o.Switch, o => o.IsEnabled, StringComparer.Ordinal);
        _profile.Jobs = Jobs.Select(j => new DatenSyncJob { Source = j.Source, Target = j.Target }).ToList();
    }

    public void SelectJob(int index) => _selectedJobIndex = index;

    [RelayCommand]
    private async Task PickEditorSourceAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(EditorSource);
        if (!string.IsNullOrWhiteSpace(path))
        {
            EditorSource = path;
        }
    }

    [RelayCommand]
    private async Task PickEditorTargetAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(EditorTarget);
        if (!string.IsNullOrWhiteSpace(path))
        {
            EditorTarget = path;
        }
    }

    [RelayCommand]
    private void AddJob()
    {
        if (string.IsNullOrWhiteSpace(EditorSource) || string.IsNullOrWhiteSpace(EditorTarget))
        {
            Status = Loc.T("datensync.fillSourceTarget");
            return;
        }

        if (string.Equals(Path.GetFullPath(EditorSource), Path.GetFullPath(EditorTarget), StringComparison.OrdinalIgnoreCase))
        {
            Status = Loc.T("datensync.sourceTargetSame");
            return;
        }

        Jobs.Add(new DatenSyncJobRow { Source = EditorSource, Target = EditorTarget });
        OnPropertyChanged(nameof(CanStart));
        Status = Loc.T("datensync.jobAdded");
    }

    [RelayCommand]
    private void LoadSelectedJob()
    {
        if (_selectedJobIndex is null or < 0 || _selectedJobIndex >= Jobs.Count)
        {
            Status = Loc.T("datensync.selectJob");
            return;
        }

        var row = Jobs[_selectedJobIndex.Value];
        EditorSource = row.Source;
        EditorTarget = row.Target;
        Status = Loc.T("datensync.jobLoaded");
    }

    [RelayCommand]
    private void UpdateSelectedJob()
    {
        if (_selectedJobIndex is null or < 0 || _selectedJobIndex >= Jobs.Count)
        {
            Status = Loc.T("datensync.selectJobShort");
            return;
        }

        if (string.IsNullOrWhiteSpace(EditorSource) || string.IsNullOrWhiteSpace(EditorTarget))
        {
            Status = Loc.T("datensync.status.fillSourceTarget");
            return;
        }

        var row = Jobs[_selectedJobIndex.Value];
        row.Source = EditorSource;
        row.Target = EditorTarget;
        Status = Loc.T("datensync.jobUpdated");
    }

    [RelayCommand]
    private void RemoveSelectedJob()
    {
        if (_selectedJobIndex is null or < 0 || _selectedJobIndex >= Jobs.Count)
        {
            Status = Loc.T("datensync.selectJobShort");
            return;
        }

        Jobs.RemoveAt(_selectedJobIndex.Value);
        _selectedJobIndex = null;
        OnPropertyChanged(nameof(CanStart));
        Status = Loc.T("datensync.jobRemoved");
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            Status = Loc.T("datensync.profileNameMissing");
            return;
        }

        SyncProfile();
        var safe = DatenSyncConfigReader.SanitizeProfileName(ProfileName);
        if (string.IsNullOrWhiteSpace(safe))
        {
            Status = Loc.T("datensync.profileNameInvalid");
            return;
        }

        DatenSyncConfigReader.Save(_profile, safe);
        DatenSyncConfigReader.Save(_profile);
        SelectedProfileName = safe;
        RefreshProfileNames();
        Status = Loc.F("datensync.profileSaved", safe);
    }

    [RelayCommand]
    private void LoadProfile()
    {
        if (string.IsNullOrWhiteSpace(SelectedProfileName))
        {
            Status = Loc.T("datensync.status.selectProfile");
            return;
        }

        try
        {
            _profile = DatenSyncConfigReader.LoadNamedProfile(SelectedProfileName);
            EditorSource = _profile.EditorSource;
            EditorTarget = _profile.EditorTarget;
            IntervalMinutes = _profile.IntervalMinutes;
            ProfileName = SelectedProfileName;
            RefreshOptions();
            RefreshJobs();
            Status = Loc.F("datensync.profileLoaded", SelectedProfileName);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void SaveDefault()
    {
        SyncProfile();
        DatenSyncConfigReader.Save(_profile);
        Status = Loc.T("datensync.defaultSaved");
    }

    [RelayCommand]
    private void StartSync()
    {
        if (Jobs.Count == 0)
        {
            Status = Loc.T("datensync.noJobs");
            return;
        }

        if (!int.TryParse(IntervalMinutes, out var interval) || interval < 0)
        {
            Status = Loc.T("datensync.invalidInterval");
            return;
        }

        SyncProfile();
        DatenSyncConfigReader.Save(_profile);

        try
        {
            var switches = DatenSyncConfigReader.BuildSwitches(_profile.Options).ToList();
            AppServices.DatenSync.StartJobs(_profile.Jobs, switches, interval);
            IsRunning = true;
            Status = interval > 0
                ? Loc.F("datensync.syncRunningRepeat", interval)
                : Loc.T("datensync.syncRunning");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void StopSync()
    {
        AppServices.DatenSync.StopAll();
        IsRunning = false;
        Status = Loc.T("datensync.syncStopped");
    }

    [RelayCommand]
    private void OpenFullGui() => AppServices.Launcher.Launch(_tool);
}
