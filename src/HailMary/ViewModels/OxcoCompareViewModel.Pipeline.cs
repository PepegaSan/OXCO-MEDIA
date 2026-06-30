using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class OxcoCompareViewModel
{
    private string? _lastBitrateScanJsonPath;

    public ObservableCollection<BitrateRowViewModel> BitrateRows { get; } = [];
    [ObservableProperty] private string _bitrateInDir = string.Empty;

    [ObservableProperty] private string _bitrateOutDir = string.Empty;

    [ObservableProperty] private string _taggerInDir = string.Empty;

    [ObservableProperty] private string _taggerOutDir = string.Empty;

    partial void OnCompareExportDirChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(BitrateInDir))
        {
            BitrateInDir = value;
        }
    }

    partial void OnBitrateOutDirChanged(string value)
    {
        ApplyTaggerInFromBitrateIfLinked();
        if (string.IsNullOrWhiteSpace(TaggerInDir))
        {
            TaggerInDir = value;
        }
    }

    [RelayCommand]
    private async Task PickBitrateInDirAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(BitrateInDir);
        if (!string.IsNullOrWhiteSpace(path))
        {
            BitrateInDir = path;
        }
    }

    [RelayCommand]
    private async Task PickBitrateOutDirAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(BitrateOutDir);
        if (!string.IsNullOrWhiteSpace(path))
        {
            BitrateOutDir = path;
        }
    }

    [RelayCommand]
    private async Task PickTaggerInDirAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(TaggerInDir);
        if (!string.IsNullOrWhiteSpace(path))
        {
            TaggerInDir = path;
        }
    }

    [RelayCommand]
    private async Task PickTaggerOutDirAsync()
    {
        var path = await FolderPickerHelper.PickFolderAsync(TaggerOutDir);
        if (!string.IsNullOrWhiteSpace(path))
        {
            TaggerOutDir = path;
        }
    }

    [RelayCommand]
    private async Task BitrateScanAsync()
    {
        if (string.IsNullOrWhiteSpace(BitrateInDir) || !Directory.Exists(BitrateInDir))
        {
            Status = Loc.T("oxco.status.bitrateInMissing");
            return;
        }

        if (string.IsNullOrWhiteSpace(BitrateOutDir))
        {
            Status = Loc.T("oxco.status.bitrateOutMissing");
            return;
        }

        PersistSettings();
        IsBusy = true;
        Status = Loc.T("oxco.status.bitrateScanRunning");
        try
        {
            var settings = BuildBitrateChangerSettings();
            var configPath = Path.Combine(Path.GetTempPath(), $"hm_oxco_bitrate_{Guid.NewGuid():N}.json");
            var outPath = Path.Combine(Path.GetTempPath(), $"hm_oxco_scan_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(configPath, BitrateConfigReader.ToJobJson(settings));
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "bitrate_scan_job.py",
                ["--config-json", configPath, "--output-json", outPath]);
            if (result.Success && File.Exists(outPath))
            {
                _lastBitrateScanJsonPath = outPath;
                BitrateRows.Clear();
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outPath));
                foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
                {
                    var path = row.GetProperty("path").GetString() ?? "";
                    var w = row.GetProperty("width").GetInt32();
                    var h = row.GetProperty("height").GetInt32();
                    var srcKbps = row.TryGetProperty("source_kbps", out var sk) && sk.ValueKind != JsonValueKind.Null
                        ? sk.GetInt32().ToString()
                        : "-";
                    var tgtKbps = row.TryGetProperty("effective_target_kbps", out var tk) && tk.ValueKind != JsonValueKind.Null
                        ? tk.GetInt32().ToString()
                        : "-";
                    var saveMb = row.TryGetProperty("estimated_saved_bytes", out var sb) && sb.ValueKind != JsonValueKind.Null
                        ? $"{sb.GetInt64() / (1024.0 * 1024.0):F1}"
                        : "-";
                    BitrateRows.Add(new BitrateRowViewModel
                    {
                        Path = path,
                        FileName = Path.GetFileName(path),
                        Resolution = $"{w}x{h}",
                        SourceKbps = srcKbps,
                        TargetKbps = tgtKbps,
                        EstSaveMb = saveMb,
                        Action = row.GetProperty("action").GetString() ?? "",
                        Reason = row.GetProperty("reason").GetString() ?? "",
                    });
                }

                var convertCount = BitrateRows.Count(r => r.Action == "convert");
                Status = Loc.F("oxco.status.bitrateScanDone", BitrateRows.Count, convertCount);
            }
            else
            {
                Status = result.Message;
            }
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BitrateConvertAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastBitrateScanJsonPath) || !File.Exists(_lastBitrateScanJsonPath))
        {
            Status = Loc.T("oxco.status.runBitrateScanFirst");
            return;
        }

        PersistSettings();
        IsBusy = true;
        Status = Loc.T("oxco.status.bitrateConvertRunning");
        try
        {
            var settings = BuildBitrateChangerSettings();
            var configPath = Path.Combine(Path.GetTempPath(), $"hm_oxco_bitrate_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(configPath, BitrateConfigReader.ToJobJson(settings));
            var result = await AppServices.JobRunner.RunBridgeAsync(
                "oxco_bitrate_convert_job.py",
                ["--config-json", configPath, "--rows-json", _lastBitrateScanJsonPath]);
            DeleteBitrateSourcesAfterConvert();
            PruneBitrateRowsAfterConvert();
            await RefreshTaggerListCoreAsync(logCount: false);
            Status = result.Success
                ? result.Message
                : Loc.T("oxco.status.bitrateConvertErrors");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void DeleteBitrateSourcesAfterConvert()
    {
        var suffix = string.IsNullOrWhiteSpace(BrSuffix) ? "_bitrate" : BrSuffix.Trim();
        OxcoBitrateCleanup.DeleteSourcesAfterConvert(
            BitrateRows.Select(r => new BitrateConvertRow { Path = r.Path, Action = r.Action }),
            BitrateInDir,
            BitrateOutDir,
            suffix,
            BrOutputMp4,
            BrDeleteSourceAfterOk);
    }

    private void PruneBitrateRowsAfterConvert()
    {
        var suffix = string.IsNullOrWhiteSpace(BrSuffix) ? "_bitrate" : BrSuffix.Trim();
        var remaining = new List<BitrateRowViewModel>();
        foreach (var row in BitrateRows)
        {
            if (row.Action == "convert")
            {
                var outputPath = OxcoBitratePathHelper.ResolveConvertedOutputPath(
                    row.Path, BitrateInDir, BitrateOutDir, suffix, BrOutputMp4);
                if (outputPath is not null && File.Exists(outputPath) && !File.Exists(row.Path))
                {
                    continue;
                }

                if (outputPath is not null && File.Exists(outputPath) && File.Exists(row.Path) && BrDeleteSourceAfterOk)
                {
                    TryDeleteBitrateSource(row.Path);
                    continue;
                }
            }

            if (File.Exists(row.Path))
            {
                remaining.Add(row);
            }
        }

        BitrateRows.Clear();
        foreach (var row in remaining)
        {
            BitrateRows.Add(row);
        }
    }

    internal static void TryDeleteBitrateSource(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                AppServices.Log.Info($"Original gelöscht: {Path.GetFileName(path)}");
            }
        }
        catch (Exception ex)
        {
            AppServices.Log.Error($"Original nicht gelöscht ({Path.GetFileName(path)}): {ex.Message}");
        }
    }
}
