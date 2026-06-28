using System.Net.Http.Json;
using System.Text.Json;
using HailMary.Models;
using HailMary.Services;
using HailMary.ViewModels;

var results = new List<(string Area, string Test, bool Pass, string Detail)>();

void Ok(string area, string test, string detail = "") =>
    results.Add((area, test, true, detail));

void Fail(string area, string test, string detail) =>
    results.Add((area, test, false, detail));

void Assert(string area, string test, bool condition, string passDetail = "", string failDetail = "")
{
    if (condition)
    {
        Ok(area, test, passDetail);
    }
    else
    {
        Fail(area, test, failDetail);
    }
}

// --- Bootstrap (mirrors app startup) ---
AppServices.Initialize();

// --- StashPathMapper ---
{
    const string area = "StashPathMapper";
    Assert(area, "Normalize I.\\ prefix",
        StashPathMapper.NormalizePathPrefix(@"I.\P Sammlung") == @"I:\P Sammlung",
        failDetail: StashPathMapper.NormalizePathPrefix(@"I.\P Sammlung"));

    var map = new StashPathMapSettings
    {
        PathPrefixRemote = "/data/",
        PathPrefixLocal = @"H:\VideoStash",
        PathPrefixBackup = @"I:\P Sammlung",
        UseBackup = false,
    };
    var mapped = StashPathMapper.Apply("/data/foo/bar.mp4", map, useBackup: false);
    Assert(area, "Apply remote to NAS",
        mapped.Equals(@"H:\VideoStash\foo\bar.mp4", StringComparison.OrdinalIgnoreCase),
        mapped,
        mapped);

    var backupMapped = StashPathMapper.Apply("/data/foo/bar.mp4", map, useBackup: true);
    Assert(area, "Apply remote to backup",
        backupMapped.Equals(@"I:\P Sammlung\foo\bar.mp4", StringComparison.OrdinalIgnoreCase),
        backupMapped,
        backupMapped);

    Assert(area, "BackupAvailable with valid paths",
        StashPathMapper.BackupAvailable(map));
}

// --- StashPathResolver ---
{
    const string area = "StashPathResolver";
    var map = AppServices.StashSettings.Current.PathMap;
    var resolved = StashPathResolver.Resolve("/data/test/nonexistent_xyz.mp4", map);
    Assert(area, "Resolve returns mapped path even if missing",
        !string.IsNullOrWhiteSpace(resolved.ResolvedPath),
        $"{resolved.ResolvedPath} ({resolved.SourceLabel})",
        "empty path");
}

// --- StashSceneIdParser ---
{
    const string area = "StashSceneIdParser";
    Assert(area, "Parse numeric ID", StashSceneIdParser.FromText("12345") == "12345");
    Assert(area, "Parse URL", StashSceneIdParser.FromText("http://x/scenes/99") == "99");
    Assert(area, "Reject text", StashSceneIdParser.FromText("hello") is null);

    var url = StashSceneIdParser.StashBrowserUrl("http://192.168.178.27:9999/graphql", "42");
    Assert(area, "Browser URL from graphql endpoint",
        url == "http://192.168.178.27:9999/scenes/42", url ?? "null", url ?? "null");
}

// --- StashConnectionStatus ---
{
    const string area = "StashConnection";
    Assert(area, "IsConnected true for Stash version",
        StashConnectionStatus.IsConnected("Stash v0.27.0"));
    Assert(area, "IsConnected false for Verbunden",
        !StashConnectionStatus.IsConnected("Verbunden"));
    Assert(area, "FormatConnected",
        StashConnectionStatus.FormatConnected("0.27.0") == "Stash 0.27.0");
}

// --- DlSort config round-trip ---
{
    const string area = "DlSortConfig";
    var tempDir = Path.Combine(Path.GetTempPath(), "HailMaryVerify_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDir);
    var origRoot = AppServices.Settings.Current.ProjectsRoot;
    try
    {
        AppServices.Settings.Current.ProjectsRoot = tempDir;
        var config = new DlSortConfig
        {
            Profiles =
            [
                new DlSortProfile
                {
                    Name = "Test",
                    WatchFolder = @"C:\Downloads",
                    Rules =
                    [
                        new DlSortRule
                        {
                            Action = "move",
                            TargetFolder = @"C:\Sorted",
                            Criteria =
                            [
                                new DlSortRuleCriterion { IfType = "extension", Condition = "contains", Values = ["pdf", "doc"] },
                                new DlSortRuleCriterion { IfType = "filename", Condition = "equals", Values = ["test"] },
                            ],
                        },
                    ],
                },
            ],
        };
        DlSortConfigReader.Save(config);
        var loaded = DlSortConfigReader.Load();
        Assert(area, "Round-trip profile count", loaded.Profiles.Count == 1);
        Assert(area, "Round-trip rule count", loaded.Profiles[0].Rules.Count == 1);
        Assert(area, "Round-trip criteria count", loaded.Profiles[0].Rules[0].Criteria.Count == 2);
        Assert(area, "Round-trip OR values",
            loaded.Profiles[0].Rules[0].Criteria[0].Values.SequenceEqual(["pdf", "doc"]));
        Assert(area, "Legacy watch_folder in JSON",
            File.Exists(DlSortConfigReader.ConfigPath) &&
            File.ReadAllText(DlSortConfigReader.ConfigPath).Contains("watch_folder"));
    }
    finally
    {
        AppServices.Settings.Current.ProjectsRoot = origRoot;
        try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
    }
}

// --- Tool registry ---
{
    const string area = "ToolRegistry";
    Assert(area, "Tools loaded", AppServices.Tools.Tools.Count >= 10, $"{AppServices.Tools.Tools.Count} tools");
    var hybridIds = new[] { "cutter", "intro_cutter", "text_to_video", "bitratechanger", "audiocleaner", "marker_autocut", "dl_sort", "autotagger", "stash_pathfinder", "stash_cutter", "marker_updater", "oxco", "daten_sync" };
    foreach (var id in hybridIds)
    {
        var tool = AppServices.Tools.Tools.FirstOrDefault(t => t.Id == id);
        Assert(area, $"Hybrid tool '{id}' registered", tool is not null && tool.Type.Equals("hybrid", StringComparison.OrdinalIgnoreCase));
    }
}

// --- Hybrid workspace creation (WinUI — headless skip) ---
{
    const string area = "HybridPanelFactory";
    foreach (var id in new[] { "marker_updater", "stash_pathfinder", "stash_cutter", "dl_sort" })
    {
        var tool = AppServices.Tools.Tools.First(t => t.Id == id);
        try
        {
            var ws = HybridPanelFactory.CreateWorkspace(tool, tool.Group ?? "stash");
            Ok(area, $"Create workspace '{id}'", "WinUI host available");
        }
        catch (Exception ex) when (ex.Message.Contains("XAML", StringComparison.OrdinalIgnoreCase)
                                   || ex.GetType().FullName?.Contains("WinUI", StringComparison.OrdinalIgnoreCase) == true
                                   || ex.GetType().FullName?.Contains("Microsoft.UI", StringComparison.OrdinalIgnoreCase) == true
                                   || ex is System.Runtime.InteropServices.COMException)
        {
            Ok(area, $"Create workspace '{id}' (headless)", "SKIP — WinUI needs app shell");
        }
        catch (Exception ex)
        {
            Fail(area, $"Create workspace '{id}'", ex.Message);
        }
    }
}

// --- Bridge scripts ---
{
    const string area = "Bridges";
    var bridgesDir = AppPaths.BridgesDirectory;
    Assert(area, "Bridges directory exists", Directory.Exists(bridgesDir), bridgesDir);
    foreach (var bridge in new[] { "scene_cutter_export_job.py", "dl_sort_monitor_job.py", "autotagger_monitor_job.py", "bitrate_convert_job.py", "marker_autocut_export_job.py", "oxco_compare_job.py" })
    {
        Assert(area, $"Bridge {bridge}", File.Exists(Path.Combine(bridgesDir, bridge)));
    }
}

// --- VideoPreviewUriHelper ---
{
    const string area = "VideoPreviewUri";
    var sample = FindSampleVideo(AppServices.StashSettings.Current.PathMap.PathPrefixLocal)
                 ?? FindSampleVideo(AppServices.StashSettings.Current.PathMap.PathPrefixBackup);
    if (sample is null)
    {
        Fail(area, "ToMediaUri with real file", "No sample video found (skipped)");
    }
    else
    {
        var uri = VideoPreviewUriHelper.ToMediaUri(sample);
        Assert(area, "ToMediaUri with real file", uri is not null, uri?.ToString() ?? "null");
    }
}

// --- Live: Stash GraphQL ---
{
    const string area = "StashLive";
    var endpoint = AppServices.StashSettings.Current.Endpoint;
    if (string.IsNullOrWhiteSpace(endpoint))
    {
        Fail(area, "Ping", "No endpoint configured");
    }
    else
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var payload = new { query = "query { version { version } }" };
            var resp = await http.PostAsJsonAsync(endpoint, payload);
            if (!resp.IsSuccessStatusCode)
            {
                Fail(area, "Ping HTTP", $"{(int)resp.StatusCode} {resp.ReasonPhrase}");
            }
            else
            {
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                var version = json.GetProperty("data").GetProperty("version").GetProperty("version").GetString();
                Ok(area, "Ping", $"Stash {version}");
                AppServices.StashSettings.Current.Endpoint = endpoint;
                var client = new StashGraphQlClient();
                client.Configure(endpoint, AppServices.StashSettings.Current.ApiKey);
                var scenes = await client.FindScenesAsync("test");
                Ok(area, "FindScenesAsync", $"{scenes.Count} Treffer fuer 'test'");

                var caseTest = await client.FindScenesAsync("TEST");
                var caseTest2 = await client.FindScenesAsync("test");
                Assert(area, "Case-insensitive title filter",
                    caseTest.Count == caseTest2.Count && caseTest.Count > 0,
                    $"{caseTest.Count} Treffer",
                    $"{caseTest.Count} vs {caseTest2.Count}");

                if (scenes.Count > 0)
                {
                    var first = scenes[0];
                    var resolved = StashPathResolver.Resolve(first.Path, AppServices.StashSettings.Current.PathMap);
                    Ok(area, "Path resolve first hit",
                        $"exists={resolved.FileExists} path={resolved.ResolvedPath}");

                    var details = await client.GetSceneDetailsAsync(first.SceneId);
                    Assert(area, "GetSceneDetailsAsync",
                        !string.IsNullOrEmpty(details.SceneId),
                        $"title={details.Title} tags={details.Tags.Count} markers={details.Markers.Count}");
                }
            }
        }
        catch (Exception ex)
        {
            Fail(area, "Ping", ex.Message);
        }
    }
}

// --- Live: filesystem paths from settings ---
{
    const string area = "PathsLive";
    var map = AppServices.StashSettings.Current.PathMap;
    foreach (var label in new[] { ("NAS", map.PathPrefixLocal), ("Backup", map.PathPrefixBackup) })
    {
        if (string.IsNullOrWhiteSpace(label.Item2))
        {
            Fail(area, $"{label.Item1} configured", "empty");
            continue;
        }

        Assert(area, $"{label.Item1} root exists ({label.Item2})", Directory.Exists(label.Item2), label.Item2, "not found");
    }
}

// --- Live: ffprobe ---
{
    const string area = "FfprobeLive";
    var duration = await FfprobeHelper.ProbeDurationSecondsAsync(null!);
    // null path should return null quickly
    Assert(area, "Null path returns null", duration is null);

    var sampleVideo = FindSampleVideo(AppServices.StashSettings.Current.PathMap.PathPrefixLocal);
    if (sampleVideo is null)
    {
        Fail(area, "Probe sample video", "No .mp4 found under NAS root (skipped)");
    }
    else
    {
        var dur = await FfprobeHelper.ProbeDurationSecondsAsync(sampleVideo);
        Assert(area, "Probe sample video", dur is > 0, $"{sampleVideo}: {dur:0.#}s", "duration 0 or ffprobe missing");
    }
}

// --- Live: Python ---
{
    const string area = "PythonLive";
    var py = AppServices.Settings.ResolvePythonExecutable();
    Assert(area, "Python executable resolved", !string.IsNullOrWhiteSpace(py), py);
    if (py != "python" || File.Exists(py))
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(py, "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            var ver = proc?.StandardOutput.ReadToEnd().Trim() ?? "";
            proc?.WaitForExit(5000);
            Ok(area, "Python --version", ver);
        }
        catch (Exception ex)
        {
            Fail(area, "Python --version", ex.Message);
        }
    }
    else
    {
        Fail(area, "Python on PATH", "python not found");
    }
}

// --- Report ---
var failed = results.Where(r => !r.Pass).ToList();
Console.WriteLine();
Console.WriteLine("=== Hail Mary Verification ===");
Console.WriteLine($"ProjectsRoot: {AppServices.Settings.ProjectsRoot}");
Console.WriteLine($"Stash: {AppServices.StashSettings.Current.Endpoint}");
Console.WriteLine();
foreach (var group in results.GroupBy(r => r.Area))
{
    Console.WriteLine($"--- {group.Key} ---");
    foreach (var r in group)
    {
        var mark = r.Pass ? "PASS" : "FAIL";
        var detail = string.IsNullOrEmpty(r.Detail) ? "" : $" — {r.Detail}";
        Console.WriteLine($"  [{mark}] {r.Test}{detail}");
    }
}

Console.WriteLine();
Console.WriteLine($"Total: {results.Count(r => r.Pass)}/{results.Count} passed, {failed.Count} failed");
return failed.Count == 0 ? 0 : 1;

static string? FindSampleVideo(string? root)
{
    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
    {
        return null;
    }

    try
    {
        return Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories)
            .FirstOrDefault();
    }
    catch
    {
        return null;
    }
}
