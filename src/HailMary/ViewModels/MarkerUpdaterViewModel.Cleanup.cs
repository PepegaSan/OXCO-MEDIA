using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Services;

namespace HailMary.ViewModels;

public sealed partial class MarkerCleanupRowViewModel : ObservableObject
{
    public StashOverflowMarkerItem Source { get; init; } = null!;

    public string MarkerId => Source.MarkerId;

    public string Display
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(Source.SceneTitle) ? Loc.T("markerupdater.noTitle") : Source.SceneTitle;
            if (title.Length > 40)
            {
                title = title[..40];
            }

            var markerLabel = string.IsNullOrWhiteSpace(Source.MarkerTitle)
                ? Source.PrimaryTagName
                : Source.MarkerTitle;
            var start = TimecodeHelper.FormatDisplay(Source.Seconds);
            var end = Source.EndSeconds > 0
                ? TimecodeHelper.FormatDisplay(Source.EndSeconds)
                : "–";
            var dur = TimecodeHelper.FormatDisplay(Source.Duration);
            return $"{Source.SceneId} | {title} | {markerLabel} | {start} → {end} | {dur}";
        }
    }
}

public partial class MarkerUpdaterViewModel
{
    public ObservableCollection<MarkerCleanupRowViewModel> CleanupClampItems { get; } = [];

    public ObservableCollection<MarkerCleanupRowViewModel> CleanupDeleteItems { get; } = [];

    [ObservableProperty] private string _cleanupToleranceText = "10";

    [ObservableProperty] private string _cleanupStatus = string.Empty;

    [ObservableProperty] private bool _cleanupIsScanning;

    private double CleanupTolerance()
    {
        return double.TryParse(CleanupToleranceText.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : 10;
    }

    [RelayCommand(CanExecute = nameof(CanRunCleanupScan))]
    private async Task CleanupScanAsync()
    {
        CleanupIsScanning = true;
        CleanupClampItems.Clear();
        CleanupDeleteItems.Clear();
        CleanupStatus = Loc.F("markerupdater.cleanupScanning", 1);
        Status = CleanupStatus;
        try
        {
            await EnsureStashReachableAsync();
            var progress = new Progress<int>(page =>
            {
                CleanupStatus = Loc.F("markerupdater.cleanupScanning", page);
                Status = CleanupStatus;
            });
            var items = await _client.FindOverflowingMarkersAsync(progress);
            var tolerance = CleanupTolerance();
            foreach (var item in items)
            {
                var overflowStart = item.Seconds - item.Duration;
                var overflowEnd = item.EndSeconds > 0 ? item.EndSeconds - item.Duration : 0;
                var maxOverflow = Math.Max(overflowStart, overflowEnd);
                var row = new MarkerCleanupRowViewModel { Source = item };
                if (item.Seconds <= item.Duration && maxOverflow > 0 && maxOverflow <= tolerance)
                {
                    CleanupClampItems.Add(row);
                }
                else
                {
                    CleanupDeleteItems.Add(row);
                }
            }

            var total = CleanupClampItems.Count + CleanupDeleteItems.Count;
            if (total == 0)
            {
                CleanupStatus = Loc.T("markerupdater.cleanupNoneFound");
            }
            else
            {
                var sceneCount = CleanupClampItems.Select(r => r.Source.SceneId)
                    .Concat(CleanupDeleteItems.Select(r => r.Source.SceneId))
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                CleanupStatus = Loc.F("markerupdater.cleanupFound", total, sceneCount);
            }

            Status = CleanupStatus;
        }
        catch (Exception ex)
        {
            CleanupStatus = ex.Message;
            Status = ex.Message;
        }
        finally
        {
            CleanupIsScanning = false;
            CleanupScanCommand.NotifyCanExecuteChanged();
            CleanupClampSelectedCommand.NotifyCanExecuteChanged();
            CleanupClampAllCommand.NotifyCanExecuteChanged();
            CleanupDeleteSelectedCommand.NotifyCanExecuteChanged();
            CleanupDeleteAllCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanRunCleanupScan() => !IsBusy && !CleanupIsScanning;

    private readonly List<MarkerCleanupRowViewModel> _cleanupClampSelection = [];

    private readonly List<MarkerCleanupRowViewModel> _cleanupDeleteSelection = [];

    internal void SetCleanupClampSelection(IEnumerable<MarkerCleanupRowViewModel> selected)
    {
        _cleanupClampSelection.Clear();
        _cleanupClampSelection.AddRange(selected);
    }

    internal void SetCleanupDeleteSelection(IEnumerable<MarkerCleanupRowViewModel> selected)
    {
        _cleanupDeleteSelection.Clear();
        _cleanupDeleteSelection.AddRange(selected);
    }

    partial void OnCleanupIsScanningChanged(bool value) => CleanupScanCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task CleanupClampSelectedAsync()
    {
        var targets = _cleanupClampSelection.ToList();
        if (targets.Count == 0)
        {
            Status = Loc.T("markerupdater.status.noMarkersSelected");
            return;
        }

        if (ConfirmAsync is not null
            && !await ConfirmAsync(Loc.T("markerupdater.cleanupConfirmTitle"), Loc.F("markerupdater.cleanupConfirmClamp", targets.Count)))
        {
            return;
        }

        await CleanupClampAsync(targets);
    }

    [RelayCommand]
    private async Task CleanupClampAllAsync()
    {
        if (CleanupClampItems.Count == 0)
        {
            return;
        }

        if (ConfirmAsync is not null
            && !await ConfirmAsync(Loc.T("markerupdater.cleanupConfirmTitle"), Loc.F("markerupdater.cleanupConfirmClamp", CleanupClampItems.Count)))
        {
            return;
        }

        await CleanupClampAsync(CleanupClampItems.ToList());
    }

    private async Task CleanupClampAsync(IReadOnlyList<MarkerCleanupRowViewModel> targets)
    {
        IsBusy = true;
        var clamped = 0;
        try
        {
            await EnsureStashReachableAsync();
            var total = targets.Count;
            for (var i = 0; i < targets.Count; i++)
            {
                var item = targets[i].Source;
                await _client.UpdateMarkerAsync(
                    item.MarkerId,
                    item.MarkerTitle,
                    item.Seconds,
                    item.Duration,
                    item.PrimaryTagId);
                clamped++;
                CleanupStatus = Loc.F("markerupdater.cleanupClamping", i + 1, total);
                Status = CleanupStatus;
            }

            foreach (var row in targets)
            {
                CleanupClampItems.Remove(row);
            }

            CleanupStatus = Loc.F("markerupdater.cleanupClamped", clamped);
            Status = CleanupStatus;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            CleanupStatus = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CleanupDeleteSelectedAsync()
    {
        var targets = _cleanupDeleteSelection.ToList();
        if (targets.Count == 0)
        {
            Status = Loc.T("markerupdater.status.noMarkersSelected");
            return;
        }

        if (ConfirmAsync is not null
            && !await ConfirmAsync(Loc.T("markerupdater.cleanupConfirmTitle"), Loc.F("markerupdater.cleanupConfirmDelete", targets.Count)))
        {
            return;
        }

        await CleanupDeleteAsync(targets);
    }

    [RelayCommand]
    private async Task CleanupDeleteAllAsync()
    {
        if (CleanupDeleteItems.Count == 0)
        {
            return;
        }

        if (ConfirmAsync is not null
            && !await ConfirmAsync(Loc.T("markerupdater.cleanupConfirmTitle"), Loc.F("markerupdater.cleanupConfirmDelete", CleanupDeleteItems.Count)))
        {
            return;
        }

        await CleanupDeleteAsync(CleanupDeleteItems.ToList());
    }

    private async Task CleanupDeleteAsync(IReadOnlyList<MarkerCleanupRowViewModel> targets)
    {
        IsBusy = true;
        var deleted = 0;
        try
        {
            await EnsureStashReachableAsync();
            var total = targets.Count;
            for (var i = 0; i < targets.Count; i++)
            {
                await _client.DeleteMarkerAsync(targets[i].MarkerId);
                deleted++;
                CleanupStatus = Loc.F("markerupdater.cleanupDeleting", i + 1, total);
                Status = CleanupStatus;
            }

            foreach (var row in targets)
            {
                CleanupDeleteItems.Remove(row);
            }

            CleanupStatus = Loc.F("markerupdater.cleanupDeleted", deleted);
            Status = CleanupStatus;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            CleanupStatus = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
