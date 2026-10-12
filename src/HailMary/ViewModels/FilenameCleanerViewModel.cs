using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public enum RenamePlanState
{
    Ready,
    Conflict,
    Empty,
    Done,
    Failed,
}

public sealed partial class FilenameRenameRow : ObservableObject
{
    public required StashSceneFileItem File { get; init; }

    public required IReadOnlyList<string> RemovedTags { get; init; }

    /// <summary>Set after NewBasename in the initializer, so the first assignment does not re-validate.</summary>
    internal FilenameCleanerViewModel? Owner { get; init; }

    public string Folder => File.Path[..Math.Max(0, File.Path.Length - File.Basename.Length)].TrimEnd('/', '\\');

    private string Separator => File.Path.Contains('/') ? "/" : "\\";

    public string TargetPath => Folder + Separator + NewBasename;

    public string OldBasename => File.Basename;

    public string RemovedText => string.Join(" · ", RemovedTags.Select(t => $"[{t}]"));

    /// <summary>Same file in the local backup (null when no backup mapping applies).</summary>
    public string? BackupOldPath { get; init; }

    public bool BackupOldExists { get; set; }

    public string? BackupNewPath => BackupOldPath is null
        ? null
        : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(BackupOldPath) ?? string.Empty, NewBasename);

    /// <summary>A different file already sits at the backup target.</summary>
    public bool BackupTargetTaken { get; set; }

    [ObservableProperty] private string _newBasename = string.Empty;

    [ObservableProperty] private RenamePlanState _state;

    [ObservableProperty] private string _note = string.Empty;

    [ObservableProperty] private string _backupNote = string.Empty;

    /// <summary>Scene the new name collides with (for "open in Stash").</summary>
    [ObservableProperty] private string? _conflictSceneId;

    /// <summary>Once a row had a problem it stays editable, so the name can be fixed again.</summary>
    [ObservableProperty] private bool _isEditable;

    public bool IsReadOnlyName => !IsEditable;

    public bool IsProblem => State is RenamePlanState.Conflict or RenamePlanState.Empty or RenamePlanState.Failed;

    public bool IsDone => State == RenamePlanState.Done;

    public bool HasBackupNote => BackupNote.Length > 0;

    public bool HasConflictScene => !string.IsNullOrEmpty(ConflictSceneId);

    public bool CanFix => IsEditable && State != RenamePlanState.Done;

    partial void OnNewBasenameChanged(string value)
    {
        OnPropertyChanged(nameof(TargetPath));
        OnPropertyChanged(nameof(BackupNewPath));
        Owner?.OnRowNameEdited(this);
    }

    partial void OnStateChanged(RenamePlanState value)
    {
        OnPropertyChanged(nameof(IsProblem));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(CanFix));
    }

    partial void OnIsEditableChanged(bool value)
    {
        OnPropertyChanged(nameof(IsReadOnlyName));
        OnPropertyChanged(nameof(CanFix));
    }

    partial void OnBackupNoteChanged(string value) => OnPropertyChanged(nameof(HasBackupNote));

    partial void OnConflictSceneIdChanged(string? value) => OnPropertyChanged(nameof(HasConflictScene));
}

internal sealed class InlineProgress(Action<int> report) : IProgress<int>
{
    public void Report(int value) => report(value);
}

internal sealed record RenameLogEntry(
    string FileId,
    string Folder,
    string OldBasename,
    string NewBasename,
    string? BackupOldPath = null,
    string? BackupNewPath = null);

/// <summary>One word in the "remove" list; the Stash switch is only offered when such a tag exists.</summary>
public sealed partial class FilenameTermRow : ObservableObject
{
    public required string Text { get; init; }

    internal FilenameCleanerViewModel? Owner { get; init; }

    [ObservableProperty] private bool _removeStashTag;

    [ObservableProperty] private bool _hasStashTag;

    [ObservableProperty] private string _stashInfo = string.Empty;

    partial void OnRemoveStashTagChanged(bool value) => Owner?.OnTermsChanged();
}

internal sealed class FilenameTermSetting
{
    public string Text { get; set; } = string.Empty;

    public bool RemoveStashTag { get; set; }
}

internal sealed class FilenameCleanerSettings
{
    /// <summary>Old format (comma/line list) — only read to migrate.</summary>
    public string RemoveTags { get; set; } = string.Empty;

    public List<FilenameTermSetting>? Terms { get; set; }

    public bool Anywhere { get; set; }

    public bool RemoveResolution { get; set; }

    public bool ResolutionStashTags { get; set; }

    /// <summary>Old global switch — used as default for migrated terms.</summary>
    public bool RemoveSceneTags { get; set; } = true;

    public string PathFilter { get; set; } = string.Empty;

    public bool RenameBackup { get; set; } = true;
}

/// <summary>Strip "[Tag]" groups from file names via Stash (moveFiles) and drop the same tags from the scenes.</summary>
public partial class FilenameCleanerViewModel : ObservableObject, IToolShellHost, IStashSettingsContext, IStashToolHost, ISplitPaneToolHost, ILocalizable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ToolDefinition _tool;
    private readonly StashGraphQlClient _client = new();
    private CancellationTokenSource? _cts;
    private List<StashSceneFileItem> _allFiles = [];
    private FilenameTagRules? _planRules;

    public FilenameCleanerViewModel(ToolDefinition tool)
    {
        _tool = tool;
        var s = LoadSettings();
        var terms = s.Terms
                    ?? FilenameTagRules.ParseList(s.RemoveTags)
                        .Select(t => new FilenameTermSetting { Text = t, RemoveStashTag = s.RemoveSceneTags })
                        .ToList();
        _loadingSettings = true;
        foreach (var t in terms.Where(t => !string.IsNullOrWhiteSpace(t.Text)))
        {
            Terms.Add(new FilenameTermRow { Text = t.Text.Trim(), RemoveStashTag = t.RemoveStashTag, Owner = this });
        }

        Anywhere = s.Anywhere;
        RemoveResolution = s.RemoveResolution;
        ResolutionStashTags = s.ResolutionStashTags;
        _loadingSettings = false;
        PathFilter = s.PathFilter;
        RenameBackup = s.RenameBackup;
        ApplyCentralStashSettings();
        StashConnectionSync.SubscribeToCentralChanges(ApplyCentralStashSettings);
        StashConnectionSync.SubscribeToGlobalConnect(v => ConnectionInfo = StashConnectionStatus.FormatConnected(v));
        RefreshUndoInfo();
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<FilenameRenameRow> Plan { get; } = [];

    public ObservableCollection<FilenameRenameRow> VisiblePlan { get; } = [];

    // ---- rules ----
    public ObservableCollection<FilenameTermRow> Terms { get; } = [];

    [ObservableProperty] private string _newTermText = string.Empty;
    [ObservableProperty] private bool _anywhere;
    [ObservableProperty] private bool _removeResolution;
    [ObservableProperty] private bool _resolutionStashTags;
    [ObservableProperty] private string _resolutionStashInfo = string.Empty;

    private bool _loadingSettings;
    private HashSet<string>? _stashTagNames;

    public int SearchModeIndex
    {
        get => Anywhere ? 1 : 0;
        set => Anywhere = value == 1;
    }

    public bool HasTerms => Terms.Count > 0;
    [ObservableProperty] private string _pathFilter = string.Empty;
    [ObservableProperty] private bool _showProblemsOnly;
    [ObservableProperty] private bool _renameBackup = true;

    public bool BackupConfigured => StashPathMapper.BackupAvailable(CurrentPathMap());

    public string BackupRootText => BackupConfigured
        ? Loc.F("filenamecleaner.backupRoot", StashPathMapper.NormalizeMap(CurrentPathMap()).PathPrefixBackup)
        : Loc.T("filenamecleaner.backupNotConfigured");

    private bool BackupActive => RenameBackup && BackupConfigured;

    public bool HasConflicts => Plan.Any(r => r.State == RenamePlanState.Conflict);

    // ---- state ----
    [ObservableProperty] private string _status = Loc.T("common.ready");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _planSummary = Loc.T("filenamecleaner.noPlan");
    [ObservableProperty] private string _sceneTagSummary = string.Empty;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private string _undoInfo = string.Empty;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canCatchUpBackup;
    [ObservableProperty] private string _catchUpInfo = string.Empty;

    // ---- stash connection (shared settings) ----
    [ObservableProperty] private string _endpoint = string.Empty;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _pathPrefixRemote = "/data/";
    [ObservableProperty] private string _pathPrefixLocal = string.Empty;
    [ObservableProperty] private string _pathPrefixBackup = string.Empty;
    [ObservableProperty] private bool _useBackup;
    [ObservableProperty] private string _connectionInfo = Loc.T("stash.notConnected");

    private List<(string SceneId, StashTagItem Tag)> _sceneTagHits = [];

    public int ReadyCount => Plan.Count(r => r.State == RenamePlanState.Ready);

    public bool HasPlan => Plan.Count > 0;

    public string RulesExample => Loc.T(Anywhere ? "filenamecleaner.rulesExampleAnywhere" : "filenamecleaner.rulesExample");

    private void ApplyCentralStashSettings()
    {
        StashConnectionSync.ApplyCentralToTool(
            v => Endpoint = v,
            v => ApiKey = v,
            v => PathPrefixRemote = v,
            v => PathPrefixLocal = v,
            v => PathPrefixBackup = v,
            v => UseBackup = v);
        _client.Configure(Endpoint, ApiKey);
    }

    private FilenameTagRules CurrentRules() =>
        new(Terms.Select(t => new FilenameTerm(t.Text, t.RemoveStashTag)), Anywhere, RemoveResolution, ResolutionStashTags);

    // ======================= terms =======================

    [RelayCommand]
    private void AddTerm()
    {
        foreach (var text in FilenameTagRules.ParseList(NewTermText))
        {
            if (Terms.Any(t => t.Text.Equals(text, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // new terms default to "also remove the Stash tag" when one exists
            var hasTag = _stashTagNames?.Contains(text) == true;
            Terms.Add(new FilenameTermRow { Text = text, RemoveStashTag = hasTag, Owner = this });
        }

        NewTermText = string.Empty;
        OnTermsChanged();
        _ = EnsureStashTagsAsync();
    }

    [RelayCommand]
    private void RemoveTerm(FilenameTermRow? row)
    {
        if (row is not null && Terms.Remove(row))
        {
            OnTermsChanged();
        }
    }

    internal void OnTermsChanged()
    {
        if (_loadingSettings)
        {
            return;
        }

        OnPropertyChanged(nameof(HasTerms));
        OnPropertyChanged(nameof(RulesExample));
        RefreshStashInfo();
        PersistSettings();
        InvalidatePlan();
    }

    /// <summary>Called when the panel is shown: learn which terms also exist as Stash tags.</summary>
    public async Task EnsureStashTagsAsync()
    {
        if (_stashTagNames is not null || string.IsNullOrWhiteSpace(Endpoint))
        {
            return;
        }

        try
        {
            _client.Configure(Endpoint, ApiKey);
            var tags = await _client.GetTagsAsync();
            _stashTagNames = tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            RefreshStashInfo();
        }
        catch
        {
            // offline — the switches stay available, preview reports the details
        }
    }

    private void RefreshStashInfo()
    {
        var sceneCounts = _allFiles
            .DistinctBy(f => f.SceneId)
            .SelectMany(f => f.SceneTags.Select(t => t.Name))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var term in Terms)
        {
            term.HasStashTag = _stashTagNames?.Contains(term.Text) ?? true;
            term.StashInfo = _stashTagNames is null
                ? string.Empty
                : !term.HasStashTag
                    ? Loc.T("filenamecleaner.termNoStashTag")
                    : sceneCounts.TryGetValue(term.Text, out var n)
                        ? Loc.F("filenamecleaner.termStashCount", n)
                        : Loc.T("filenamecleaner.termStashExists");
        }

        if (_stashTagNames is null)
        {
            ResolutionStashInfo = string.Empty;
            return;
        }

        var resolutionTags = _stashTagNames.Where(FilenameTagRules.IsResolution).OrderBy(n => n).ToList();
        ResolutionStashInfo = resolutionTags.Count == 0
            ? Loc.T("filenamecleaner.resolutionNoStashTags")
            : Loc.F("filenamecleaner.resolutionStashTags", string.Join(", ", resolutionTags.Take(10)) + (resolutionTags.Count > 10 ? " …" : string.Empty));
    }

    // ======================= preview =======================

    [RelayCommand]
    private async Task BuildPreviewAsync()
    {
        var rules = CurrentRules();
        if (rules.IsEmpty)
        {
            Status = Loc.T("filenamecleaner.noRules");
            return;
        }

        PersistSettings();
        _client.Configure(Endpoint, ApiKey);
        IsBusy = true;
        Plan.Clear();
        VisiblePlan.Clear();
        _sceneTagHits = [];
        SceneTagSummary = string.Empty;
        Status = Loc.T("filenamecleaner.loadingFiles");
        try
        {
            var version = await _client.PingAsync();
            ConnectionInfo = StashConnectionStatus.FormatConnected(version);
            _stashTagNames = (await _client.GetTagsAsync()).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Inline progress: Progress<T> posts late and would overwrite the final status.
            var progress = new InlineProgress(n => Status = Loc.F("filenamecleaner.loadingFilesCount", n));
            _allFiles = (await _client.GetAllSceneFilesAsync(progress)).ToList();
            await BuildPlanAsync(rules);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyPlan();
        }
    }

    private async Task BuildPlanAsync(FilenameTagRules rules)
    {
        _planRules = rules;
        var filter = PathFilter.Trim();
        var inScope = _allFiles
            .Where(f => filter.Length == 0 || f.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var map = CurrentPathMap();
        var useBackup = BackupActive;
        var backupRoot = StashPathMapper.NormalizeMap(map).PathPrefixBackup;

        // name rules + backup lookups off the UI thread (thousands of File.Exists on the backup disk)
        Status = Loc.T("filenamecleaner.checkingBackup");
        var candidates = await Task.Run(() => inScope
            .Select(file =>
            {
                var newName = rules.Clean(file.Basename, out var removed);
                if (newName is null)
                {
                    return null;
                }

                string? backupOld = null;
                var backupExists = false;
                var backupTargetTaken = false;
                if (useBackup)
                {
                    var mapped = StashPathMapper.Apply(file.Path, map, useBackup: true);
                    if (mapped.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        backupOld = mapped;
                        backupExists = System.IO.File.Exists(mapped);
                        backupTargetTaken = newName.Length > 0 && BackupTargetTaken(mapped, newName);
                    }
                }

                return new { file, newName, removed, backupOld, backupExists, backupTargetTaken };
            })
            .Where(c => c is not null)
            .ToList());

        foreach (var c in candidates)
        {
            Plan.Add(new FilenameRenameRow
            {
                File = c!.file,
                NewBasename = c.newName,
                RemovedTags = c.removed,
                BackupOldPath = c.backupOld,
                BackupOldExists = c.backupExists,
                BackupTargetTaken = c.backupTargetTaken,
                Owner = this,
            });
        }

        Revalidate();

        // scene tags with the same names (all scenes in scope, renamed or not)
        var sceneIds = inScope.Select(f => f.SceneId).ToHashSet(StringComparer.Ordinal);
        _sceneTagHits = _allFiles
            .Where(f => sceneIds.Contains(f.SceneId))
            .DistinctBy(f => f.SceneId)
            .SelectMany(f => f.SceneTags.Where(t => rules.MatchesStashTag(t.Name)).Select(t => (f.SceneId, t)))
            .ToList();
        var tagNames = _sceneTagHits.Select(h => h.Tag.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        var sceneCount = _sceneTagHits.Select(h => h.SceneId).Distinct().Count();
        RefreshStashInfo();
        SceneTagSummary = !rules.RemovesAnyStashTag
            ? Loc.T("filenamecleaner.sceneTagsOff")
            : sceneCount == 0
            ? Loc.T("filenamecleaner.sceneTagsNone")
            : Loc.F("filenamecleaner.sceneTagsSummary", sceneCount, tagNames.Count,
                string.Join(", ", tagNames.Take(12)) + (tagNames.Count > 12 ? " …" : string.Empty));

        Status = Loc.F("filenamecleaner.previewDone", inScope.Count);
        RefreshVisible();
    }

    private static bool BackupTargetTaken(string backupOld, string newName)
    {
        var target = Path.Combine(Path.GetDirectoryName(backupOld) ?? string.Empty, newName);
        return !string.Equals(target, backupOld, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(target);
    }

    /// <summary>Check every pending row against Stash paths, the other planned names and the backup.</summary>
    private void Revalidate()
    {
        var byPath = new Dictionary<string, StashSceneFileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in _allFiles)
        {
            byPath.TryAdd(f.Path, f);
        }

        var planned = new Dictionary<string, FilenameRenameRow>(StringComparer.OrdinalIgnoreCase);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var row in Plan)
        {
            if (row.State is RenamePlanState.Done)
            {
                continue;
            }

            row.ConflictSceneId = null;
            var name = row.NewBasename.Trim();
            if (name.Length == 0 || Path.GetFileNameWithoutExtension(name).Trim().Length == 0)
            {
                SetProblem(row, RenamePlanState.Empty, Loc.T("filenamecleaner.noteEmpty"));
            }
            else if (name.IndexOfAny(invalid) >= 0)
            {
                SetProblem(row, RenamePlanState.Empty, Loc.T("filenamecleaner.noteInvalid"));
            }
            else if (string.Equals(name, row.OldBasename, StringComparison.Ordinal))
            {
                SetProblem(row, RenamePlanState.Empty, Loc.T("filenamecleaner.noteUnchanged"));
            }
            else if (byPath.TryGetValue(row.TargetPath, out var other) && other.FileId != row.File.FileId)
            {
                row.ConflictSceneId = other.SceneId;
                SetProblem(row, RenamePlanState.Conflict, Loc.F("filenamecleaner.noteConflictWith", other.SceneId, other.Basename));
            }
            else if (planned.TryGetValue(row.TargetPath, out var first))
            {
                row.ConflictSceneId = first.File.SceneId;
                SetProblem(row, RenamePlanState.Conflict, Loc.F("filenamecleaner.noteConflictPlanned", first.OldBasename));
            }
            else if (BackupActive && row.BackupTargetTaken)
            {
                SetProblem(row, RenamePlanState.Conflict, Loc.T("filenamecleaner.noteBackupTaken"));
            }
            else
            {
                planned[row.TargetPath] = row;
                row.State = RenamePlanState.Ready;
                row.Note = string.Empty;
            }

            row.BackupNote = BackupActive && row.BackupOldPath is not null && !row.BackupOldExists
                ? Loc.T("filenamecleaner.noteBackupMissing")
                : BackupActive && row.BackupOldPath is null
                    ? Loc.T("filenamecleaner.noteBackupUnmapped")
                    : string.Empty;
        }

        NotifyPlan();
    }

    private static void SetProblem(FilenameRenameRow row, RenamePlanState state, string note)
    {
        row.State = state;
        row.Note = note;
        row.IsEditable = true;
    }

    /// <summary>A name was typed or changed by "append number" — re-check backup target and the whole plan.</summary>
    internal void OnRowNameEdited(FilenameRenameRow row)
    {
        if (row.State == RenamePlanState.Done)
        {
            return;
        }

        row.BackupTargetTaken = row.BackupOldPath is not null
                                && row.NewBasename.Trim().Length > 0
                                && row.NewBasename.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                                && BackupTargetTaken(row.BackupOldPath, row.NewBasename.Trim());
        Revalidate();
    }

    /// <summary>"Clip.mp4" → "Clip (2).mp4", the first number that is free in Stash, the plan and the backup.</summary>
    [RelayCommand]
    private void AppendNumber(FilenameRenameRow? row)
    {
        if (row is null || row.State == RenamePlanState.Done)
        {
            return;
        }

        var baseName = row.NewBasename.Trim();
        if (Path.GetFileNameWithoutExtension(baseName).Trim().Length == 0)
        {
            baseName = row.OldBasename;
        }

        var ext = Path.GetExtension(baseName);
        var stem = baseName[..^ext.Length].TrimEnd();
        var paths = new HashSet<string>(_allFiles.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var planned = Plan.Where(r => r != row && r.State is RenamePlanState.Ready or RenamePlanState.Done)
            .Select(r => r.TargetPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sep = row.TargetPath[row.Folder.Length..][..1];
        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            var target = row.Folder + sep + candidate;
            if (paths.Contains(target) || planned.Contains(target))
            {
                continue;
            }

            if (BackupActive && row.BackupOldPath is not null && BackupTargetTaken(row.BackupOldPath, candidate))
            {
                continue;
            }

            row.NewBasename = candidate;
            return;
        }
    }

    [RelayCommand]
    private void ResolveAllConflicts()
    {
        foreach (var row in Plan.Where(r => r.State == RenamePlanState.Conflict).ToList())
        {
            AppendNumber(row);
        }

        Status = Loc.F("filenamecleaner.conflictsResolved", Plan.Count(r => r.State == RenamePlanState.Conflict));
    }

    [RelayCommand]
    private void OpenConflictScene(FilenameRenameRow? row)
    {
        var url = row?.ConflictSceneId is { } id ? StashSceneIdParser.StashBrowserUrl(Endpoint, id) : null;
        if (url is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private void RefreshVisible()
    {
        VisiblePlan.Clear();
        foreach (var row in ShowProblemsOnly ? Plan.Where(r => r.IsProblem) : Plan)
        {
            VisiblePlan.Add(row);
        }
    }

    partial void OnShowProblemsOnlyChanged(bool value) => RefreshVisible();

    private void NotifyPlan()
    {
        var conflicts = Plan.Count(r => r.State is RenamePlanState.Conflict or RenamePlanState.Empty);
        var done = Plan.Count(r => r.State == RenamePlanState.Done);
        var failed = Plan.Count(r => r.State == RenamePlanState.Failed);
        PlanSummary = Plan.Count == 0
            ? (_allFiles.Count == 0 ? Loc.T("filenamecleaner.noPlan") : Loc.T("filenamecleaner.nothingToDo"))
            : Loc.F("filenamecleaner.planSummary", ReadyCount, conflicts, done, failed);
        OnPropertyChanged(nameof(ReadyCount));
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
    }

    // ======================= apply =======================

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var targets = Plan.Where(r => r.State == RenamePlanState.Ready).ToList();
        var removeTags = _sceneTagHits.Count > 0;
        if (targets.Count == 0 && !removeTags)
        {
            Status = Loc.T("filenamecleaner.nothingToDo");
            return;
        }

        var sceneCount = _sceneTagHits.Select(h => h.SceneId).Distinct().Count();
        if (ConfirmAsync is not null
            && !await ConfirmAsync(
                Loc.T("filenamecleaner.confirmTitle"),
                Loc.F("filenamecleaner.confirmMessage", targets.Count, removeTags ? sceneCount : 0,
                    BackupActive ? Loc.F("filenamecleaner.confirmBackup", targets.Count(r => r.BackupOldExists)) : string.Empty)))
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBusy = true;
        ShowProgress = true;
        ProgressValue = 0;
        var logPath = Path.Combine(LogDirectory, $"rename_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        var log = new List<RenameLogEntry>();
        var ok = 0;
        var failed = 0;
        try
        {
            if (targets.Count > 0 && !await _client.SupportsMoveFilesAsync(ct))
            {
                Status = Loc.T("filenamecleaner.noMoveFiles");
                return;
            }

            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = targets[i];
                var backupFrom = BackupActive && row.BackupOldExists ? row.BackupOldPath : null;
                var backupTo = backupFrom is null ? null : row.BackupNewPath;
                var backupMoved = false;
                try
                {
                    // backup first (local, instant) — if Stash then fails, it is put back right away
                    if (backupFrom is not null && backupTo is not null)
                    {
                        System.IO.File.Move(backupFrom, backupTo);
                        backupMoved = true;
                    }

                    await _client.MoveFileAsync(row.File.FileId, row.Folder, row.NewBasename, ct);
                    row.State = RenamePlanState.Done;
                    log.Add(new RenameLogEntry(row.File.FileId, row.Folder, row.OldBasename, row.NewBasename,
                        backupMoved ? backupFrom : null, backupMoved ? backupTo : null));
                    ok++;
                }
                catch (Exception ex)
                {
                    if (backupMoved)
                    {
                        TryMoveBack(backupTo!, backupFrom!);
                    }

                    if (ex is OperationCanceledException)
                    {
                        throw;
                    }

                    row.State = RenamePlanState.Failed;
                    row.Note = ex.Message;
                    failed++;
                }

                // write the undo log as we go — a crash or cancel must not lose it
                if (log.Count > 0 && (i % 25 == 0 || i == targets.Count - 1))
                {
                    WriteLog(logPath, log);
                }

                ProgressValue = (i + 1) * 100.0 / Math.Max(1, targets.Count) * (removeTags ? 0.9 : 1.0);
                Status = Loc.F("filenamecleaner.renaming", i + 1, targets.Count, failed);
            }

            // tags only after the renames — otherwise a filename→tag plugin could add them right back
            if (removeTags)
            {
                Status = Loc.F("filenamecleaner.removingTags", sceneCount);
                foreach (var group in _sceneTagHits.GroupBy(h => h.Tag.Id))
                {
                    await _client.BulkRemoveSceneTagsAsync(group.Select(h => h.SceneId).Distinct().ToList(), [group.Key], ct);
                }

                _sceneTagHits = [];
                SceneTagSummary = Loc.F("filenamecleaner.sceneTagsRemoved", sceneCount);
            }

            ProgressValue = 100;
            Status = Loc.F("filenamecleaner.finished", ok, failed, removeTags ? sceneCount : 0);
        }
        catch (OperationCanceledException)
        {
            Status = Loc.F("filenamecleaner.cancelled", ok);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            if (log.Count > 0)
            {
                WriteLog(logPath, log);
            }

            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            ShowProgress = false;
            NotifyPlan();
            RefreshUndoInfo();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private static void TryMoveBack(string from, string to)
    {
        try
        {
            if (System.IO.File.Exists(from) && !System.IO.File.Exists(to))
            {
                System.IO.File.Move(from, to);
            }
        }
        catch
        {
            // reported through the failed row; nothing else to do
        }
    }

    // ======================= undo =======================

    private static string LogDirectory
    {
        get
        {
            var dir = Path.Combine(AppPaths.SettingsDirectory, "rename_logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static void WriteLog(string path, List<RenameLogEntry> log) =>
        File.WriteAllText(path, JsonSerializer.Serialize(log, JsonOptions));

    private static string? LatestLog() =>
        Directory.GetFiles(LogDirectory, "rename_*.json").OrderByDescending(f => f).FirstOrDefault();

    private void RefreshUndoInfo()
    {
        var latest = LatestLog();
        CanUndo = latest is not null;
        if (latest is null)
        {
            UndoInfo = Loc.T("filenamecleaner.undoNone");
            return;
        }

        CanCatchUpBackup = false;
        CatchUpInfo = string.Empty;
        try
        {
            var entries = JsonSerializer.Deserialize<List<RenameLogEntry>>(File.ReadAllText(latest)) ?? [];
            UndoInfo = Loc.F("filenamecleaner.undoInfo", File.GetLastWriteTime(latest).ToString("g"), entries.Count);

            // runs from before backup support (or with the backup switch off) left the backup behind
            var behind = entries.Count(e => e.BackupNewPath is null);
            if (behind > 0)
            {
                CanCatchUpBackup = BackupConfigured;
                CatchUpInfo = BackupConfigured
                    ? Loc.F("filenamecleaner.catchUpInfo", behind)
                    : Loc.F("filenamecleaner.catchUpNoBackup", behind);
            }
        }
        catch
        {
            UndoInfo = Path.GetFileName(latest);
        }
    }

    private string? BackupPathFor(string stashFolder, string basename)
    {
        var map = CurrentPathMap();
        var root = StashPathMapper.NormalizeMap(map).PathPrefixBackup;
        var sep = stashFolder.Contains('/') ? "/" : "\\";
        var mapped = StashPathMapper.Apply(stashFolder + sep + basename, map, useBackup: true);
        return mapped.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? mapped : null;
    }

    /// <summary>
    /// Apply the renames of the last run to the backup afterwards (old name → new name),
    /// so the next backup → NAS mirror does not bring the old names back.
    /// </summary>
    [RelayCommand]
    private async Task CatchUpBackupAsync()
    {
        var latest = LatestLog();
        if (latest is null || !BackupConfigured)
        {
            return;
        }

        var entries = JsonSerializer.Deserialize<List<RenameLogEntry>>(File.ReadAllText(latest)) ?? [];
        var pending = entries.Select((e, i) => (Entry: e, Index: i)).Where(x => x.Entry.BackupNewPath is null).ToList();

        // look first, then ask — the dialog shows what will happen
        var plan = await Task.Run(() => pending.Select(x =>
        {
            var oldPath = BackupPathFor(x.Entry.Folder, x.Entry.OldBasename);
            var newPath = BackupPathFor(x.Entry.Folder, x.Entry.NewBasename);
            var oldExists = oldPath is not null && System.IO.File.Exists(oldPath);
            var newExists = newPath is not null && System.IO.File.Exists(newPath);
            return (x.Index, OldPath: oldPath, NewPath: newPath, OldExists: oldExists, NewExists: newExists);
        }).ToList());

        var toMove = plan.Where(p => p.OldExists && !p.NewExists).ToList();
        var already = plan.Count(p => !p.OldExists && p.NewExists);
        var both = plan.Count(p => p.OldExists && p.NewExists);
        var missing = plan.Count(p => !p.OldExists && !p.NewExists);

        if (ConfirmAsync is not null
            && !await ConfirmAsync(
                Loc.T("filenamecleaner.catchUpTitle"),
                Loc.F("filenamecleaner.catchUpConfirm", toMove.Count, already, both, missing)))
        {
            return;
        }

        IsBusy = true;
        ShowProgress = true;
        var moved = 0;
        var failed = 0;
        try
        {
            for (var i = 0; i < toMove.Count; i++)
            {
                var m = toMove[i];
                try
                {
                    await Task.Run(() => System.IO.File.Move(m.OldPath!, m.NewPath!));
                    entries[m.Index] = entries[m.Index] with { BackupOldPath = m.OldPath, BackupNewPath = m.NewPath };
                    moved++;
                }
                catch
                {
                    failed++;
                }

                ProgressValue = (i + 1) * 100.0 / toMove.Count;
                Status = Loc.F("filenamecleaner.catchUpRunning", i + 1, toMove.Count, failed);
            }

            // already renamed in the backup → record it too, so "undo" also reverts the backup
            foreach (var p in plan.Where(p => !p.OldExists && p.NewExists))
            {
                entries[p.Index] = entries[p.Index] with { BackupOldPath = p.OldPath, BackupNewPath = p.NewPath };
            }

            WriteLog(latest, entries);
            Status = Loc.F("filenamecleaner.catchUpDone", moved, already, both + missing + failed);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
            ShowProgress = false;
            RefreshUndoInfo();
        }
    }

    [RelayCommand]
    private async Task UndoLastAsync()
    {
        var latest = LatestLog();
        if (latest is null)
        {
            return;
        }

        var entries = JsonSerializer.Deserialize<List<RenameLogEntry>>(File.ReadAllText(latest)) ?? [];
        if (ConfirmAsync is not null
            && !await ConfirmAsync(Loc.T("filenamecleaner.undoTitle"), Loc.F("filenamecleaner.undoConfirm", entries.Count)))
        {
            return;
        }

        _client.Configure(Endpoint, ApiKey);
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ShowProgress = true;
        var ok = 0;
        var failed = 0;
        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var e = entries[i];
                try
                {
                    await _client.MoveFileAsync(e.FileId, e.Folder, e.OldBasename, _cts.Token);
                    if (e.BackupNewPath is not null && e.BackupOldPath is not null)
                    {
                        TryMoveBack(e.BackupNewPath, e.BackupOldPath);
                    }

                    ok++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    failed++;
                }

                ProgressValue = (i + 1) * 100.0 / entries.Count;
                Status = Loc.F("filenamecleaner.undoing", i + 1, entries.Count, failed);
            }

            // keep the log around but mark it as undone so it is not offered again
            File.Move(latest, Path.ChangeExtension(latest, ".undone"), overwrite: true);
            Status = Loc.F("filenamecleaner.undoDone", ok, failed);
            Plan.Clear();
            VisiblePlan.Clear();
        }
        catch (OperationCanceledException)
        {
            Status = Loc.F("filenamecleaner.cancelled", ok);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            ShowProgress = false;
            NotifyPlan();
            RefreshUndoInfo();
        }
    }

    // ======================= settings / connection =======================

    private static string SettingsPath => Path.Combine(AppPaths.SettingsDirectory, "filename_cleaner.json");

    private static FilenameCleanerSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<FilenameCleanerSettings>(File.ReadAllText(SettingsPath)) ?? new();
            }
        }
        catch
        {
            // defaults
        }

        return new FilenameCleanerSettings();
    }

    private void PersistSettings()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new FilenameCleanerSettings
            {
                Terms = Terms.Select(t => new FilenameTermSetting { Text = t.Text, RemoveStashTag = t.RemoveStashTag }).ToList(),
                Anywhere = Anywhere,
                RemoveResolution = RemoveResolution,
                ResolutionStashTags = ResolutionStashTags,
                PathFilter = PathFilter,
                RenameBackup = RenameBackup,
            }, JsonOptions));
        }
        catch
        {
            // not critical
        }
    }

    private StashPathMapSettings CurrentPathMap() => new()
    {
        PathPrefixRemote = PathPrefixRemote,
        PathPrefixLocal = PathPrefixLocal,
        PathPrefixBackup = PathPrefixBackup,
        UseBackup = UseBackup,
    };

    public bool BackupToggleEnabled => StashPathMapper.BackupAvailable(CurrentPathMap());

    [RelayCommand]
    private async Task ConnectAsync()
    {
        SaveSettings();
        IsBusy = true;
        Status = Loc.T("stash.connecting");
        try
        {
            var version = await _client.PingAsync();
            ConnectionInfo = StashConnectionStatus.FormatConnected(version);
            Status = Loc.T("stash.connected");
        }
        catch (Exception ex)
        {
            ConnectionInfo = Loc.T("stash.connectionFailed");
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        PersistSettings();
        StashConnectionSync.PushToolToCentral(Endpoint, ApiKey, CurrentPathMap());
        _client.Configure(Endpoint, ApiKey);
        Status = Loc.T("common.settingsSaved");
    }

    // A changed rule makes the preview stale — it must be rebuilt before applying.
    partial void OnAnywhereChanged(bool value)
    {
        OnPropertyChanged(nameof(SearchModeIndex));
        OnTermsChanged();
    }

    partial void OnRemoveResolutionChanged(bool value) => OnTermsChanged();

    partial void OnResolutionStashTagsChanged(bool value) => OnTermsChanged();

    partial void OnPathFilterChanged(string value) => InvalidatePlan();

    partial void OnRenameBackupChanged(bool value)
    {
        PersistSettings();
        InvalidatePlan();
    }

    partial void OnPathPrefixBackupChanged(string value) => NotifyBackupRoot();

    partial void OnPathPrefixRemoteChanged(string value) => NotifyBackupRoot();

    private void NotifyBackupRoot()
    {
        RefreshUndoInfo();
        OnPropertyChanged(nameof(BackupConfigured));
        OnPropertyChanged(nameof(BackupRootText));
        OnPropertyChanged(nameof(BackupToggleEnabled));
    }

    private void InvalidatePlan()
    {
        if (Plan.Count == 0 && _sceneTagHits.Count == 0)
        {
            return;
        }

        Plan.Clear();
        VisiblePlan.Clear();
        _sceneTagHits = [];
        SceneTagSummary = string.Empty;
        NotifyPlan();
        PlanSummary = Loc.T("filenamecleaner.planStale");
    }

    // ======================= shell =======================

    public bool IsStashConnected => StashConnectionStatus.IsConnected(ConnectionInfo);

    public string StashConnectionTooltip => StashConnectionStatus.Tooltip(ConnectionInfo);

    public bool UsesSplitPaneLayout => true;

    public string PrimaryActionLabel => Loc.T("filenamecleaner.primaryAction");

    public IAsyncRelayCommand PrimaryActionCommand => ApplyCommand;

    public bool IsPrimaryActionEnabled => !IsBusy && (ReadyCount > 0 || _sceneTagHits.Count > 0);

    public string StatusText => Status;

    bool IToolShellHost.IsBusy => IsBusy;

    public bool HasVideoPreview => false;

    public bool HasSettings => true;

    IRelayCommand? IToolShellHost.OpenSettingsCommand => SaveSettingsCommand;

    public bool HasOpenFullGui => false;

    public string OpenFullGuiLabel => string.Empty;

    IRelayCommand? IToolShellHost.OpenFullGuiCommand => null;

    public object? SettingsContext => this;

    public bool ShowBackupOptions => false;

    public bool HasStopAction => true;

    public string StopActionLabel => Loc.T("common.stopButton");

    public IRelayCommand? StopActionCommand => CancelCommand;

    public bool IsStopActionEnabled => IsBusy && _cts is not null;

    IAsyncRelayCommand IStashSettingsContext.ConnectCommand => ConnectCommand;

    IRelayCommand IStashSettingsContext.SaveSettingsCommand => SaveSettingsCommand;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnConnectionInfoChanged(string value)
    {
        OnPropertyChanged(nameof(IsStashConnected));
        OnPropertyChanged(nameof(StashConnectionTooltip));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IToolShellHost.IsBusy));
        OnPropertyChanged(nameof(IsPrimaryActionEnabled));
        OnPropertyChanged(nameof(IsStopActionEnabled));
    }


    public void RefreshLocalization()
    {
        LocalizationNotify.Description(this);
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RulesExample));
        NotifyPlan();
        RefreshUndoInfo();
    }
}
