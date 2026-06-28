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
            var title = string.IsNullOrWhiteSpace(Source.SceneTitle) ? "(ohne Titel)" : Source.SceneTitle;
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
        CleanupStatus = "Scanne Szenen… (Seite 1)";
        Status = CleanupStatus;
        try
        {
            await EnsureStashReachableAsync();
            var progress = new Progress<int>(page =>
            {
                CleanupStatus = $"Scanne Szenen… (Seite {page})";
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
                CleanupStatus = $"{total} ungültige Marker in {sceneCount} Szenen gefunden.";
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
            && !await ConfirmAsync("Bereinigung", $"{targets.Count} Marker auf Videolänge kürzen?"))
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
            && !await ConfirmAsync("Bereinigung", $"{CleanupClampItems.Count} Marker auf Videolänge kürzen?"))
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
                CleanupStatus = $"Kürze… {i + 1}/{total}";
                Status = CleanupStatus;
            }

            foreach (var row in targets)
            {
                CleanupClampItems.Remove(row);
            }

            CleanupStatus = $"{clamped} Marker auf Videolänge gekürzt.";
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
            && !await ConfirmAsync("Bereinigung", $"{targets.Count} Marker wirklich löschen?"))
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
            && !await ConfirmAsync("Bereinigung", $"{CleanupDeleteItems.Count} Marker wirklich löschen?"))
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
                CleanupStatus = $"Lösche… {i + 1}/{total}";
                Status = CleanupStatus;
            }

            foreach (var row in targets)
            {
                CleanupDeleteItems.Remove(row);
            }

            CleanupStatus = $"{deleted} Marker gelöscht.";
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
