using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

/// <summary>
/// Maps between the internal rule values stored in config.json (English keys) and the
/// German UI labels — mirrors PAIRS_IF / PAIRS_COND / PAIRS_ACTION in the original tool.
/// </summary>
public static class DlSortLabels
{
    public static readonly (string Label, string Value)[] IfPairs =
    [
        ("Dateiendung", "extension"),
        ("Dateiname", "filename"),
        ("Quell-URL", "source_url"),
    ];

    public static readonly (string Label, string Value)[] ConditionPairs =
    [
        ("enthält", "contains"),
        ("ist gleich", "equals"),
    ];

    public static readonly (string Label, string Value)[] ActionPairs =
    [
        ("Verschieben nach", "move"),
        ("Löschen", "delete"),
        ("Ignorieren", "ignore"),
    ];

    public static IReadOnlyList<string> IfLabels { get; } = IfPairs.Select(p => p.Label).ToList();

    public static IReadOnlyList<string> ConditionLabels { get; } = ConditionPairs.Select(p => p.Label).ToList();

    public static IReadOnlyList<string> ActionLabels { get; } = ActionPairs.Select(p => p.Label).ToList();

    public static string LabelForValue((string Label, string Value)[] pairs, string value) =>
        pairs.FirstOrDefault(p => p.Value == value).Label ?? pairs[0].Label;

    public static string ValueForLabel((string Label, string Value)[] pairs, string label) =>
        pairs.FirstOrDefault(p => p.Label == label).Value ?? pairs[0].Value;
}

public sealed partial class DlSortOrValueRow : ObservableObject
{
    [ObservableProperty] private string _value = string.Empty;

    internal DlSortCriterionRow? Owner;

    [RelayCommand]
    private void Remove() => Owner?.RemoveOrValue(this);
}

public sealed partial class DlSortCriterionRow : ObservableObject
{
    public string CriterionId { get; init; } = Guid.NewGuid().ToString();

    public ObservableCollection<DlSortOrValueRow> OrValues { get; } = [];

    [ObservableProperty] private string _ifType = "extension";

    [ObservableProperty] private string _condition = "contains";

    [ObservableProperty] private string _firstValue = string.Empty;

    [ObservableProperty] private string _prefix = Loc.T("dlsort.ifLabel");

    [ObservableProperty] private bool _canRemove;

    internal DlSortRuleRow? Owner;

    public IReadOnlyList<string> IfLabels => DlSortLabels.IfLabels;

    public IReadOnlyList<string> ConditionLabels => DlSortLabels.ConditionLabels;

    public string IfTypeLabel
    {
        get => DlSortLabels.LabelForValue(DlSortLabels.IfPairs, IfType);
        set => IfType = DlSortLabels.ValueForLabel(DlSortLabels.IfPairs, value);
    }

    public string ConditionLabel
    {
        get => DlSortLabels.LabelForValue(DlSortLabels.ConditionPairs, Condition);
        set => Condition = DlSortLabels.ValueForLabel(DlSortLabels.ConditionPairs, value);
    }

    partial void OnIfTypeChanged(string value) => OnPropertyChanged(nameof(IfTypeLabel));

    partial void OnConditionChanged(string value) => OnPropertyChanged(nameof(ConditionLabel));

    public IEnumerable<string> AllValues =>
        new[] { FirstValue }.Concat(OrValues.Select(o => o.Value))
            .Where(v => !string.IsNullOrWhiteSpace(v));

    public DlSortOrValueRow AddOrValueRow(string initial = "")
    {
        var row = new DlSortOrValueRow { Owner = this, Value = initial };
        OrValues.Add(row);
        return row;
    }

    internal void RemoveOrValue(DlSortOrValueRow row) => OrValues.Remove(row);

    [RelayCommand]
    private void AddOrValue() => AddOrValueRow();

    [RelayCommand]
    private void RemoveCriterion() => Owner?.RemoveCriterion(this);
}

public sealed partial class DlSortRuleRow : ObservableObject
{
    public string RuleId { get; init; } = Guid.NewGuid().ToString();

    public ObservableCollection<DlSortCriterionRow> Criteria { get; } = [];

    [ObservableProperty] private string _action = "move";

    [ObservableProperty] private string _targetFolder = string.Empty;

    [ObservableProperty] private string _title = "Regel 1";

    [ObservableProperty] private bool _canMoveUp;

    [ObservableProperty] private bool _canMoveDown = true;

    internal DlSortViewModel? Owner;

    public IReadOnlyList<string> ActionLabels => DlSortLabels.ActionLabels;

    public string ActionLabel
    {
        get => DlSortLabels.LabelForValue(DlSortLabels.ActionPairs, Action);
        set => Action = DlSortLabels.ValueForLabel(DlSortLabels.ActionPairs, value);
    }

    public bool ShowTarget => Action.Equals("move", StringComparison.OrdinalIgnoreCase);

    public string TargetButtonText =>
        string.IsNullOrWhiteSpace(TargetFolder) ? "Zielordner…" : TargetFolder;

    partial void OnActionChanged(string value)
    {
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(ShowTarget));
    }

    partial void OnTargetFolderChanged(string value) => OnPropertyChanged(nameof(TargetButtonText));

    public DlSortCriterionRow AddCriterionRow()
    {
        var crit = new DlSortCriterionRow { Owner = this };
        Criteria.Add(crit);
        RefreshCriteria();
        return crit;
    }

    internal void RemoveCriterion(DlSortCriterionRow crit)
    {
        if (Criteria.Count <= 1)
        {
            return;
        }

        Criteria.Remove(crit);
        RefreshCriteria();
    }

    public void RefreshCriteria()
    {
        var multi = Criteria.Count > 1;
        for (var i = 0; i < Criteria.Count; i++)
        {
            Criteria[i].Owner = this;
            Criteria[i].Prefix = i == 0 ? Loc.T("dlsort.ifLabel") : "UND";
            Criteria[i].CanRemove = multi;
        }
    }

    [RelayCommand]
    private void AddCriterion() => AddCriterionRow();

    [RelayCommand]
    private void MoveUp() => Owner?.MoveRule(this, -1);

    [RelayCommand]
    private void MoveDown() => Owner?.MoveRule(this, 1);

    [RelayCommand]
    private void RemoveRule() => Owner?.RemoveRule(this);

    [RelayCommand]
    private async Task PickTargetAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(TargetFolder);
        if (!string.IsNullOrWhiteSpace(path))
        {
            TargetFolder = path;
        }
    }
}

public sealed partial class DlSortProfileRow : ObservableObject
{
    public string ProfileId { get; init; } = Guid.NewGuid().ToString();

    [ObservableProperty] private string _name = "Profil 1";

    [ObservableProperty] private string _watchFolder = string.Empty;

    [ObservableProperty] private bool _runEnabled;

    public ObservableCollection<DlSortRuleRow> Rules { get; } = [];
}

public partial class DlSortViewModel : ObservableObject, IToolShellHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private DlSortConfig _config;

    public static IReadOnlyList<string> IfTypeOptions { get; } = ["extension", "filename", "source_url"];

    public static IReadOnlyList<string> ConditionOptions { get; } = ["contains", "equals"];

    public static IReadOnlyList<string> ActionOptions { get; } = ["move", "delete", "ignore"];

    public IReadOnlyList<string> RuleIfTypeOptions => IfTypeOptions;

    public IReadOnlyList<string> RuleConditionOptions => ConditionOptions;

    public IReadOnlyList<string> RuleActionOptions => ActionOptions;

    public DlSortViewModel(ToolDefinition tool)
    {
        _tool = tool;
        _config = DlSortConfigReader.Load();
        RefreshProfiles();
        SelectedProfile = Profiles.FirstOrDefault();
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<DlSortProfileRow> Profiles { get; } = [];

    [ObservableProperty] private string _status = Loc.T("common.ready");

    [ObservableProperty] private bool _monitorRunning;

    [ObservableProperty] private DlSortProfileRow? _selectedProfile;

    public ObservableCollection<DlSortRuleRow> CurrentRules { get; } = [];

    [ObservableProperty] private string _editorName = string.Empty;

    [ObservableProperty] private string _editorWatchFolder = string.Empty;

    partial void OnSelectedProfileChanged(DlSortProfileRow? value)
    {
        if (value is null)
        {
            EditorName = string.Empty;
            EditorWatchFolder = string.Empty;
            CurrentRules.Clear();
            return;
        }

        EditorName = value.Name;
        EditorWatchFolder = value.WatchFolder;
        LoadRulesForCurrentProfile();
    }

    private void LoadRulesForCurrentProfile()
    {
        CurrentRules.Clear();
        if (SelectedProfile is null)
        {
            return;
        }

        foreach (var rule in SelectedProfile.Rules)
        {
            rule.Owner = this;
            rule.RefreshCriteria();
            CurrentRules.Add(rule);
        }

        RenumberRules();
    }

    private void RenumberRules()
    {
        for (var i = 0; i < CurrentRules.Count; i++)
        {
            CurrentRules[i].Title = $"Regel {i + 1}";
            CurrentRules[i].CanMoveUp = i > 0;
            CurrentRules[i].CanMoveDown = i < CurrentRules.Count - 1;
        }
    }

    internal void MoveRule(DlSortRuleRow rule, int delta)
    {
        var index = CurrentRules.IndexOf(rule);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= CurrentRules.Count)
        {
            return;
        }

        CurrentRules.Move(index, target);
        if (SelectedProfile is not null)
        {
            SelectedProfile.Rules.Move(SelectedProfile.Rules.IndexOf(rule), target);
        }

        RenumberRules();
    }

    internal void RemoveRule(DlSortRuleRow rule)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        if (CurrentRules.Count <= 1)
        {
            Status = Loc.T("dlsort.minOneRule");
            return;
        }

        CurrentRules.Remove(rule);
        SelectedProfile.Rules.Remove(rule);
        RenumberRules();
        Status = Loc.T("dlsort.ruleRemoved");
    }

    partial void OnEditorNameChanged(string value)
    {
        if (SelectedProfile is not null)
        {
            SelectedProfile.Name = value;
        }
    }

    partial void OnEditorWatchFolderChanged(string value)
    {
        if (SelectedProfile is not null)
        {
            SelectedProfile.WatchFolder = value;
        }
    }

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var profile in _config.Profiles)
        {
            var profileRow = new DlSortProfileRow
            {
                ProfileId = profile.ProfileId,
                Name = profile.Name,
                WatchFolder = profile.WatchFolder,
                RunEnabled = profile.RunEnabled,
            };

            foreach (var rule in profile.Rules)
            {
                var ruleRow = new DlSortRuleRow
                {
                    Owner = this,
                    Action = rule.Action,
                    TargetFolder = rule.TargetFolder,
                };
                foreach (var criterion in rule.Criteria)
                {
                    var values = criterion.Values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                    var critRow = new DlSortCriterionRow
                    {
                        Owner = ruleRow,
                        IfType = criterion.IfType,
                        Condition = criterion.Condition,
                        FirstValue = values.Count > 0 ? values[0] : string.Empty,
                    };
                    foreach (var extra in values.Skip(1))
                    {
                        critRow.AddOrValueRow(extra);
                    }

                    ruleRow.Criteria.Add(critRow);
                }

                if (ruleRow.Criteria.Count == 0)
                {
                    ruleRow.AddCriterionRow();
                }

                ruleRow.RefreshCriteria();
                profileRow.Rules.Add(ruleRow);
            }

            if (profileRow.Rules.Count == 0)
            {
                profileRow.Rules.Add(NewRuleRow());
            }

            Profiles.Add(profileRow);
        }

        if (Profiles.Count == 0)
        {
            var defaultProfile = new DlSortProfileRow { Name = "Profil 1" };
            defaultProfile.Rules.Add(NewRuleRow());
            Profiles.Add(defaultProfile);
        }
    }

    private DlSortRuleRow NewRuleRow()
    {
        var rule = new DlSortRuleRow { Owner = this };
        rule.AddCriterionRow();
        return rule;
    }

    private void SyncConfigFromUi()
    {
        _config.Profiles = Profiles.Select(p => new DlSortProfile
        {
            ProfileId = p.ProfileId,
            Name = p.Name,
            WatchFolder = p.WatchFolder,
            RunEnabled = p.RunEnabled,
            Rules = p.Rules.Select(r => new DlSortRule
            {
                Action = r.Action,
                TargetFolder = r.Action.Equals("move", StringComparison.OrdinalIgnoreCase) ? r.TargetFolder : string.Empty,
                Criteria = r.Criteria.Select(c => new DlSortRuleCriterion
                {
                    IfType = c.IfType,
                    Condition = c.Condition,
                    Values = c.AllValues.DefaultIfEmpty(string.Empty).ToList(),
                }).ToList(),
            }).ToList(),
        }).ToList();
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new DlSortProfileRow
        {
            Name = $"Profil {Profiles.Count + 1}",
        };
        profile.Rules.Add(NewRuleRow());
        Profiles.Add(profile);
        SelectedProfile = profile;
        Status = Loc.T("dlsort.profileAdded");
    }

    [RelayCommand]
    private void RemoveSelectedProfile()
    {
        if (SelectedProfile is null)
        {
            Status = Loc.T("dlsort.selectProfile");
            return;
        }

        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.FirstOrDefault();
        Status = Loc.T("dlsort.profileRemoved");
    }

    [RelayCommand]
    private void AddRule()
    {
        if (SelectedProfile is null)
        {
            Status = Loc.T("dlsort.selectProfile");
            return;
        }

        var rule = NewRuleRow();
        SelectedProfile.Rules.Add(rule);
        CurrentRules.Add(rule);
        RenumberRules();
        Status = Loc.T("dlsort.ruleAdded");
    }

    [RelayCommand]
    private void RemoveLastRule()
    {
        if (CurrentRules.Count == 0)
        {
            return;
        }

        RemoveRule(CurrentRules[^1]);
    }

    [RelayCommand]
    private async Task PickWatchFolderAsync()
    {
        if (SelectedProfile is null)
        {
            Status = Loc.T("dlsort.selectProfile");
            return;
        }

        var path = await FolderPickerHelper.PickFolderAsync(EditorWatchFolder);
        if (!string.IsNullOrWhiteSpace(path))
        {
            EditorWatchFolder = path;
        }
    }

    [RelayCommand]
    private void SaveConfig()
    {
        SyncConfigFromUi();
        DlSortConfigReader.Save(_config);
        Status = Loc.T("dlsort.configSaved");
    }

    [RelayCommand]
    private void StartMonitor()
    {
        SyncConfigFromUi();
        DlSortConfigReader.Save(_config);

        if (AppServices.MonitorRunner.IsRunning)
        {
            Status = Loc.T("dlsort.monitorAlreadyRunning");
            return;
        }

        if (!Profiles.Any(p => p.RunEnabled))
        {
            Status = Loc.T("dlsort.noActiveProfile");
            return;
        }

        var ok = AppServices.MonitorRunner.Start("dl_sort_monitor_job.py",
            ["--config-dir", DlSortConfigReader.ConfigDirectory]);
        MonitorRunning = ok;
        Status = ok ? Loc.T("dlsort.monitorStarted") : Loc.T("dlsort.monitorStartFailed");
    }

    [RelayCommand]
    private void StopMonitor()
    {
        AppServices.MonitorRunner.Stop();
        MonitorRunning = false;
        Status = Loc.T("autotagger.monitorStopped");
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        SyncConfigFromUi();
        DlSortConfigReader.Save(_config);
        Status = Loc.T("bitrate.scanRunning");
        var result = await AppServices.JobRunner.RunBridgeAsync("dl_sort_scan_job.py",
            ["--config-dir", DlSortConfigReader.ConfigDirectory]);
        Status = result.Message;
    }

    [RelayCommand]
    private void OpenFullGui() => AppServices.Launcher.Launch(_tool);
}
