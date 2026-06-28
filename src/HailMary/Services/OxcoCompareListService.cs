using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HailMary.Models;

namespace HailMary.Services;

public static class OxcoCompareListService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".ts", ".flv",
    };

    public static readonly IReadOnlyList<string> SortModeKeys =
    [
        "date_desc", "date_asc", "duration_desc", "duration_asc",
        "size_desc", "size_asc", "name_asc", "name_desc",
    ];

    private static readonly IReadOnlyDictionary<string, string> SortModeLocKeys = new Dictionary<string, string>
    {
        ["date_desc"] = "oxco.sortDateNewest",
        ["date_asc"] = "oxco.sortDateOldest",
        ["duration_desc"] = "oxco.sortDurationLongest",
        ["duration_asc"] = "oxco.sortDurationShortest",
        ["size_desc"] = "oxco.sortSizeLargest",
        ["size_asc"] = "oxco.sortSizeSmallest",
        ["name_asc"] = "oxco.sortNameAsc",
        ["name_desc"] = "oxco.sortNameDesc",
    };

    public static readonly IReadOnlyList<string> GroupModeKeys =
        ["none", "folder", "date", "duration", "signature", "letter"];

    private static readonly IReadOnlyDictionary<string, string> GroupModeLocKeys = new Dictionary<string, string>
    {
        ["none"] = "oxco.groupNone",
        ["folder"] = "oxco.groupSubfolder",
        ["date"] = "oxco.groupDate",
        ["duration"] = "oxco.groupVideoLength",
        ["signature"] = "oxco.groupLengthResolution",
        ["letter"] = "oxco.groupFirstLetter",
    };

    public static string LocalizeSortMode(string key) =>
        SortModeLocKeys.TryGetValue(key, out var locKey) ? Loc.T(locKey) : key;

    public static string LocalizeGroupMode(string key) =>
        GroupModeLocKeys.TryGetValue(key, out var locKey) ? Loc.T(locKey) : key;

    public static string? SortKeyFromLabel(string label) =>
        SortModeLocKeys.Keys.FirstOrDefault(k => LocalizeSortMode(k) == label);

    public static string? GroupKeyFromLabel(string label) =>
        GroupModeLocKeys.Keys.FirstOrDefault(k => LocalizeGroupMode(k) == label);

    public static readonly string[] SignaturePalette =
    [
        "#dceefb", "#fde2e2", "#d9f2d9", "#fdecd9", "#e8dcf8", "#d9f2ec",
        "#fce4f3", "#eef6d9", "#dce4f8", "#f6eed9", "#d9eef6", "#f0d9f6",
    ];

    public const string MatchHighlightColor = "#fff3b0";
    public const string ProbeUnknownColor = "#4a4a4a";
    public const string DefaultRowBackground = "#2a2a2a";
    public const string DefaultRowForeground = "#F0F0F0";
    public const string ColoredRowForeground = "#1A1A1A";

    public static string ForegroundForBackground(string? backgroundHex) =>
        backgroundHex switch
        {
            null or "" => DefaultRowForeground,
            var h when h.Equals(ProbeUnknownColor, StringComparison.OrdinalIgnoreCase) => "#EEEEEE",
            var h when h.Equals(DefaultRowBackground, StringComparison.OrdinalIgnoreCase) => DefaultRowForeground,
            _ => ColoredRowForeground,
        };

    public static List<OxcoCompareFileEntry> ScanCompareFolder(string root, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var resolved = Path.GetFullPath(root);
        var outList = new List<OxcoCompareFileEntry>();
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var path in Directory.EnumerateFiles(resolved, "*", option))
        {
            if (!IsVideoFile(path) || IsPartialTempVideo(path))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                var rel = Path.GetRelativePath(resolved, path).Replace('\\', '/');
                outList.Add(new OxcoCompareFileEntry
                {
                    Path = path,
                    Rel = rel,
                    Size = info.Length,
                    Mtime = info.LastWriteTimeUtc.Subtract(DateTime.UnixEpoch).TotalSeconds,
                });
            }
            catch
            {
                // skip unreadable files
            }
        }

        return outList;
    }

    public static async Task ProbeEntriesAsync(
        IEnumerable<OxcoCompareFileEntry> entries,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var list = entries.ToList();
        var total = list.Count;
        var done = 0;
        await Parallel.ForEachAsync(
            list,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (entry, ct) =>
            {
                await EnrichProbeAsync(entry, ct);
                var current = Interlocked.Increment(ref done);
                progress?.Report((current, total));
            });
    }

    public static async Task EnrichProbeAsync(OxcoCompareFileEntry entry, CancellationToken cancellationToken = default)
    {
        var (duration, width, height) = await ProbeCompareMediaAsync(entry.Path, cancellationToken);
        if (duration is null || width is null || height is null)
        {
            entry.ProbeOk = false;
            entry.DurationSec = null;
            entry.Width = null;
            entry.Height = null;
            return;
        }

        entry.DurationSec = duration;
        entry.Width = width;
        entry.Height = height;
        entry.ProbeOk = true;
    }

    public static string? CompareMatchKey(OxcoCompareFileEntry entry)
    {
        if (!entry.ProbeOk || entry.DurationSec is null)
        {
            return null;
        }

        var durLabel = FormatDuration(entry.DurationSec);
        if (entry.Width is null || entry.Height is null)
        {
            return durLabel;
        }

        return $"{durLabel}|{entry.Width}x{entry.Height}";
    }

    public static Dictionary<string, string> BuildSignatureColorMap(
        IEnumerable<OxcoCompareFileEntry> orig,
        IEnumerable<OxcoCompareFileEntry> df)
    {
        var sigs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in orig.Concat(df))
        {
            var sig = CompareMatchKey(entry);
            if (!string.IsNullOrEmpty(sig))
            {
                sigs.Add(sig);
            }
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var sig in sigs)
        {
            map[sig] = SignaturePalette[i % SignaturePalette.Length];
            i++;
        }

        return map;
    }

    public static List<OxcoCompareFileEntry> RankDeepfakesForOriginal(
        OxcoCompareFileEntry orig,
        IReadOnlyList<OxcoCompareFileEntry> dfEntries,
        string patternText)
    {
        List<OxcoCompareFileEntry> candidates = [];
        var matchKey = CompareMatchKey(orig);
        if (!string.IsNullOrEmpty(matchKey))
        {
            candidates = dfEntries.Where(e => CompareMatchKey(e) == matchKey).ToList();
        }

        if (candidates.Count == 0
            && orig.ProbeOk
            && orig.DurationSec is not null
            && orig.Width is not null
            && orig.Height is not null)
        {
            candidates = dfEntries
                .Where(e => e.ProbeOk
                    && e.DurationSec is not null
                    && e.Width == orig.Width
                    && e.Height == orig.Height
                    && Math.Abs(e.DurationSec.Value - orig.DurationSec.Value) <= 2.0)
                .ToList();
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var token = ExtractPatternMatch(orig.Stem, patternText);
        if (string.IsNullOrEmpty(token))
        {
            return candidates;
        }

        var tokenHits = candidates.Where(e => e.Stem.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (tokenHits.Count == 0)
        {
            return candidates;
        }

        var tokenKeys = tokenHits.Select(e => PathKey(e.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rest = candidates.Where(e => !tokenKeys.Contains(PathKey(e.Path))).ToList();
        return [.. tokenHits, .. rest];
    }

    public static List<OxcoCompareFileEntry> RankOriginalsForDeepfake(
        OxcoCompareFileEntry deepfake,
        IReadOnlyList<OxcoCompareFileEntry> origEntries,
        string patternText)
    {
        List<OxcoCompareFileEntry> candidates = [];
        var matchKey = CompareMatchKey(deepfake);
        if (!string.IsNullOrEmpty(matchKey))
        {
            candidates = origEntries.Where(e => CompareMatchKey(e) == matchKey).ToList();
        }

        if (candidates.Count == 0
            && deepfake.ProbeOk
            && deepfake.DurationSec is not null
            && deepfake.Width is not null
            && deepfake.Height is not null)
        {
            candidates = origEntries
                .Where(e => e.ProbeOk
                    && e.DurationSec is not null
                    && e.Width == deepfake.Width
                    && e.Height == deepfake.Height
                    && Math.Abs(e.DurationSec.Value - deepfake.DurationSec.Value) <= 2.0)
                .ToList();
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var token = ExtractPatternMatch(deepfake.Stem, patternText);
        if (string.IsNullOrEmpty(token))
        {
            return candidates;
        }

        var tokenHits = candidates.Where(e => e.Stem.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (tokenHits.Count == 0)
        {
            return candidates;
        }

        var tokenKeys = tokenHits.Select(e => PathKey(e.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rest = candidates.Where(e => !tokenKeys.Contains(PathKey(e.Path))).ToList();
        return [.. tokenHits, .. rest];
    }

    public static (List<(string Label, List<OxcoCompareFileEntry> Items)> Orig,
        List<(string Label, List<OxcoCompareFileEntry> Items)> Df) BuildAlignedGroups(
        IReadOnlyList<OxcoCompareFileEntry> origEntries,
        IReadOnlyList<OxcoCompareFileEntry> dfEntries,
        string group,
        string sortMode,
        string patternText,
        string? dfPrioritizeSignature = null,
        string? dfPrioritizeToken = null,
        string? origPrioritizeSignature = null,
        string? origPrioritizeToken = null,
        OxcoCompareFileEntry? pinGroupForEntry = null,
        OxcoCompareFileEntry? alsoPinGroupForEntry = null)
    {
        group = (group ?? "none").Trim().ToLowerInvariant();
        sortMode = (sortMode ?? "date_desc").Trim().ToLowerInvariant();

        if (group is "none" or "")
        {
            return (
                [("", SortEntriesWithHints(origEntries, sortMode, patternText, origPrioritizeSignature, origPrioritizeToken))],
                [("", SortEntriesWithHints(dfEntries, sortMode, patternText, dfPrioritizeSignature, dfPrioritizeToken))]);
        }

        var bucketsO = new Dictionary<string, List<OxcoCompareFileEntry>>(StringComparer.Ordinal);
        var bucketsD = new Dictionary<string, List<OxcoCompareFileEntry>>(StringComparer.Ordinal);
        foreach (var e in origEntries)
        {
            var key = EntryGroupKey(e, group, patternText);
            if (!bucketsO.TryGetValue(key, out var list))
            {
                list = [];
                bucketsO[key] = list;
            }

            list.Add(e);
        }

        foreach (var e in dfEntries)
        {
            var key = EntryGroupKey(e, group, patternText);
            if (!bucketsD.TryGetValue(key, out var list))
            {
                list = [];
                bucketsD[key] = list;
            }

            list.Add(e);
        }

        var allLabels = bucketsO.Keys.Union(bucketsD.Keys)
            .OrderBy(lbl => GroupOrderKeyValue(
                [.. bucketsO.GetValueOrDefault(lbl, []), .. bucketsD.GetValueOrDefault(lbl, [])],
                sortMode,
                patternText))
            .ToList();

        PinGroupsToFront(allLabels, group, patternText, pinGroupForEntry, alsoPinGroupForEntry);

        var origGroups = new List<(string, List<OxcoCompareFileEntry>)>();
        var dfGroups = new List<(string, List<OxcoCompareFileEntry>)>();
        foreach (var lbl in allLabels)
        {
            origGroups.Add((lbl, SortEntriesWithHints(
                bucketsO.GetValueOrDefault(lbl, []),
                sortMode,
                patternText,
                origPrioritizeSignature,
                origPrioritizeToken)));
            dfGroups.Add((lbl, SortEntriesWithHints(
                bucketsD.GetValueOrDefault(lbl, []),
                sortMode,
                patternText,
                dfPrioritizeSignature,
                dfPrioritizeToken)));
        }

        return (origGroups, dfGroups);
    }

    private static void PinGroupsToFront(
        List<string> allLabels,
        string group,
        string patternText,
        params OxcoCompareFileEntry?[] entries)
    {
        var insertAt = 0;
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            var pinLabel = EntryGroupKey(entry, group, patternText);
            if (!allLabels.Remove(pinLabel))
            {
                continue;
            }

            allLabels.Insert(Math.Min(insertAt, allLabels.Count), pinLabel);
            insertAt++;
        }
    }

    public static string FormatGroupLabel(string groupKey, string raw)
    {
        if (groupKey == "folder" && raw is "." or "")
        {
            return Loc.T("oxco.groupMain");
        }

        if (groupKey == "duration" && raw is "—" or "-" or "")
        {
            return Loc.T("oxco.lengthUnknown");
        }

        if (groupKey == "signature" && raw is "—" or "-" or "")
        {
            return Loc.T("oxco.metadataMissing");
        }

        if (groupKey == "signature" && raw.Contains('|'))
        {
            var parts = raw.Split('|', 2);
            return $"{parts[0]} · {parts[1].Replace('x', '×')}";
        }

        return raw;
    }

    public static string FormatDuration(double? seconds)
    {
        if (seconds is null)
        {
            return "—";
        }

        if (seconds < 60)
        {
            return $"{seconds.Value:F1}s";
        }

        var minutes = (int)(seconds.Value / 60);
        var secs = seconds.Value - minutes * 60;
        return $"{minutes}:{(int)secs:D2}";
    }

    public static string FormatResolution(int? width, int? height) =>
        width is null || height is null ? "—" : $"{width}×{height}";

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }

        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public static string FormatSortTime(OxcoCompareFileEntry entry, string patternText)
    {
        var ts = EntrySortTime(entry, patternText);
        return DateTimeOffset.FromUnixTimeSeconds((long)ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    }

    public static string ExtractPatternMatch(string originalStem, string patternText)
    {
        patternText = (patternText ?? "YYMMDDHHmmSS").Trim().Replace("{", "").Replace("}", "");
        var regex = BuildPatternRegex(patternText);
        var match = regex.Match(originalStem);
        if (!match.Success)
        {
            return originalStem.Contains(patternText, StringComparison.Ordinal) ? patternText : string.Empty;
        }

        return match.Value;
    }

    private static List<OxcoCompareFileEntry> SortEntries(
        IEnumerable<OxcoCompareFileEntry> entries,
        string mode,
        string patternText)
    {
        mode = (mode ?? "date_desc").Trim().ToLowerInvariant();
        var list = entries.ToList();
        return mode switch
        {
            "date_asc" => list.OrderBy(e => DateSortKey(e, patternText)).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
            "date_desc" => list.OrderByDescending(e => EntrySortTime(e, patternText)).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
            "size_asc" => list.OrderBy(e => e.Size).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
            "size_desc" => list.OrderByDescending(e => e.Size).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
            "duration_asc" => list.OrderBy(e => DurationSortKey(e, desc: false)).ToList(),
            "duration_desc" => list.OrderBy(e => DurationSortKey(e, desc: true)).ToList(),
            "name_desc" => list.OrderByDescending(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => list.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static List<OxcoCompareFileEntry> SortEntriesWithHints(
        IEnumerable<OxcoCompareFileEntry> entries,
        string sortMode,
        string patternText,
        string? prioritizeSignature,
        string? prioritizeToken)
    {
        var sorted = SortEntries(entries, sortMode, patternText);
        if (string.IsNullOrEmpty(prioritizeSignature) && string.IsNullOrEmpty(prioritizeToken))
        {
            return sorted;
        }

        return sorted
            .OrderBy(e =>
            {
                var rank = 0;
                if (!string.IsNullOrEmpty(prioritizeSignature) && CompareMatchKey(e) == prioritizeSignature)
                {
                    rank -= 100;
                }

                if (!string.IsNullOrEmpty(prioritizeToken) && e.Stem.Contains(prioritizeToken, StringComparison.OrdinalIgnoreCase))
                {
                    rank -= 10;
                }

                return rank;
            })
            .ToList();
    }

    private static (int Missing, double Duration, string Name) DurationSortKey(OxcoCompareFileEntry entry, bool desc)
    {
        var missing = entry.DurationSec is null ? 1 : 0;
        var dur = entry.DurationSec ?? 0;
        return desc ? (missing, -dur, entry.FileName.ToLowerInvariant()) : (missing, dur, entry.FileName.ToLowerInvariant());
    }

    private static double DateSortKey(OxcoCompareFileEntry entry, string patternText) =>
        EntrySortTime(entry, patternText);

    private static double EntrySortTime(OxcoCompareFileEntry entry, string patternText)
    {
        var ts = FilenamePatternTimestamp(entry.Stem, patternText);
        return ts ?? entry.Mtime;
    }

    private static string EntryGroupKey(OxcoCompareFileEntry entry, string group, string patternText)
    {
        group = (group ?? "none").Trim().ToLowerInvariant();
        return group switch
        {
            "folder" => FolderGroupKey(entry.Rel),
            "date" => DateTimeOffset.FromUnixTimeSeconds((long)EntrySortTime(entry, patternText)).LocalDateTime.ToString("yyyy-MM-dd"),
            "letter" => LetterGroupKey(entry.FileName),
            "duration" => entry.DurationSec is null ? "—" : FormatDuration(Math.Round(entry.DurationSec.Value, 1)),
            "signature" => CompareMatchKey(entry) ?? "—",
            _ => string.Empty,
        };
    }

    private static string FolderGroupKey(string rel)
    {
        var parent = Path.GetDirectoryName(rel.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? ".";
        return string.IsNullOrEmpty(parent) || parent == "." ? "." : parent;
    }

    private static string LetterGroupKey(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrEmpty(stem))
        {
            return "#";
        }

        var c = char.ToUpperInvariant(stem[0]);
        return char.IsLetterOrDigit(c) ? c.ToString() : "#";
    }

    private static double GroupOrderKeyValue(
        IReadOnlyList<OxcoCompareFileEntry> grpItems,
        string sortMode,
        string patternText)
    {
        if (grpItems.Count == 0)
        {
            return 0;
        }

        sortMode = (sortMode ?? "date_desc").Trim().ToLowerInvariant();
        return sortMode switch
        {
            "date_desc" => -grpItems.Max(e => EntrySortTime(e, patternText)),
            "date_asc" => grpItems.Min(e => EntrySortTime(e, patternText)),
            "size_desc" => -grpItems.Max(e => e.Size),
            "size_asc" => grpItems.Min(e => e.Size),
            "duration_desc" => GroupDurationStat(grpItems, desc: true).Duration,
            "duration_asc" => GroupDurationStat(grpItems, desc: false).Duration,
            _ => 0,
        };
    }

    private static (int Missing, double Duration) GroupDurationStat(IReadOnlyList<OxcoCompareFileEntry> items, bool desc)
    {
        var durs = items.Where(e => e.DurationSec is not null).Select(e => e.DurationSec!.Value).ToList();
        if (durs.Count == 0)
        {
            return (1, desc ? double.MaxValue : double.MinValue);
        }

        var max = durs.Max();
        return desc ? (0, -max) : (0, max);
    }

    private static double? FilenamePatternTimestamp(string stem, string patternText)
    {
        var match = BuildPatternRegex(patternText).Match(stem);
        if (!match.Success)
        {
            return null;
        }

        var gd = match.Groups;
        try
        {
            int year;
            if (gd["YYYY"].Success)
            {
                year = int.Parse(gd["YYYY"].Value, CultureInfo.InvariantCulture);
            }
            else if (gd["YY"].Success)
            {
                var yy = int.Parse(gd["YY"].Value, CultureInfo.InvariantCulture);
                year = yy < 100 ? 2000 + yy : yy;
            }
            else
            {
                return null;
            }

            var month = gd["MM"].Success ? int.Parse(gd["MM"].Value, CultureInfo.InvariantCulture) : 1;
            var day = gd["DD"].Success ? int.Parse(gd["DD"].Value, CultureInfo.InvariantCulture) : 1;
            var hour = gd["HH"].Success ? int.Parse(gd["HH"].Value, CultureInfo.InvariantCulture) : 0;
            var minute = gd["mm"].Success ? int.Parse(gd["mm"].Value, CultureInfo.InvariantCulture) : 0;
            var second = gd["SS"].Success ? int.Parse(gd["SS"].Value, CultureInfo.InvariantCulture) : 0;
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local)
                .ToUniversalTime()
                .Subtract(DateTime.UnixEpoch)
                .TotalSeconds;
        }
        catch
        {
            return null;
        }
    }

    private static Regex BuildPatternRegex(string patternText)
    {
        var tokenMap = new Dictionary<string, string>
        {
            ["YYYY"] = @"(?<YYYY>\d{4})",
            ["YY"] = @"(?<YY>\d{2})",
            ["MM"] = @"(?<MM>\d{2})",
            ["DD"] = @"(?<DD>\d{2})",
            ["HH"] = @"(?<HH>\d{2})",
            ["mm"] = @"(?<mm>\d{2})",
            ["SS"] = @"(?<SS>\d{2})",
            ["DIGITS"] = @"(?<DIGITS>\d+)",
            ["LETTERS"] = @"(?<LETTERS>[A-Za-z]+)",
            ["ALNUM"] = @"(?<ALNUM>[A-Za-z0-9]+)",
            ["ANY"] = @"(?<ANY>.+?)",
        };

        var tokenRegex = Regex.Escape(patternText);
        foreach (var token in new[] { "YYYY", "YY", "MM", "DD", "HH", "mm", "SS", "DIGITS", "LETTERS", "ALNUM", "ANY" })
        {
            tokenRegex = tokenRegex.Replace(Regex.Escape(token), tokenMap[token]);
        }

        return new Regex(tokenRegex, RegexOptions.CultureInvariant);
    }

    private static async Task<(double? Duration, int? Width, int? Height)> ProbeCompareMediaAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var ffprobe = FindFfprobe();
        if (ffprobe is null || !File.Exists(path))
        {
            return (null, null, null);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments =
                    $"-v error -print_format json -show_entries format=duration -show_entries stream=width,height -select_streams v:0 \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (null, null, null);
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                return (null, null, null);
            }

            using var doc = JsonDocument.Parse(output);
            double? duration = null;
            if (doc.RootElement.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var durEl)
                && double.TryParse(durEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
            {
                duration = sec;
            }

            int? width = null;
            int? height = null;
            if (doc.RootElement.TryGetProperty("streams", out var streams)
                && streams.GetArrayLength() > 0)
            {
                var video = streams[0];
                if (video.TryGetProperty("width", out var wEl))
                {
                    width = wEl.GetInt32();
                }

                if (video.TryGetProperty("height", out var hEl))
                {
                    height = hEl.GetInt32();
                }
            }

            if (duration is null || width is null || height is null)
            {
                return (null, null, null);
            }

            return (duration, width, height);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static string? FindFfprobe()
    {
        foreach (var name in new[] { "ffprobe.exe", "ffprobe" })
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathEnv))
            {
                continue;
            }

            foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    private static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path));

    private static bool IsPartialTempVideo(string path) =>
        Path.GetFileName(path).Contains(".partial", StringComparison.OrdinalIgnoreCase);

    private static string PathKey(string path)
    {
        try
        {
            return Path.GetFullPath(path).ToLowerInvariant();
        }
        catch
        {
            return path.ToLowerInvariant();
        }
    }
}
