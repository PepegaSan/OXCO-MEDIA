using System.Text.RegularExpressions;

namespace HailMary.Services;

/// <summary>One word to strip from file names; optionally also the Stash tag of the same name.</summary>
public sealed record FilenameTerm(string Text, bool RemoveStashTag);

/// <summary>
/// Which words to strip from file names (and which scene tags to drop in Stash).
/// <see cref="Anywhere"/> = false: only whole bracket groups – [Term], (Term), {Term}.
/// <see cref="Anywhere"/> = true: additionally the term as a whole word anywhere in the name.
/// </summary>
public sealed class FilenameTagRules
{
    // 720p, 1080i, 720x480, 1920 × 1080, 4K, 8K, UHD, FHD, QHD (+ HD/SD only inside brackets)
    private const string ResolutionCore = @"\d{3,4}\s*[pi]|\d{3,4}\s*[x×]\s*\d{3,4}|[248]\s*k|uhd|fhd|qhd";

    private static readonly Regex ResolutionInBrackets = new(
        $@"^\s*({ResolutionCore}|hd|sd)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BracketGroup = new(@"\[([^\[\]]*)\]|\(([^()]*)\)|\{([^{}]*)\}", RegexOptions.CultureInvariant);

    // separator runs left behind: "a__b", "a  b", "My--Clip", "Song -  - Live"
    private static readonly Regex SeparatorRun = new(@"[ _-]{2,}", RegexOptions.CultureInvariant);
    private static readonly Regex EmptyBrackets = new(@"\[\s*\]|\(\s*\)|\{\s*\}", RegexOptions.CultureInvariant);

    private readonly Dictionary<string, FilenameTerm> _terms;
    private readonly Regex? _wordRegex;

    public FilenameTagRules(IEnumerable<FilenameTerm> terms, bool anywhere, bool removeResolution, bool resolutionStashTags)
    {
        _terms = new Dictionary<string, FilenameTerm>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in terms)
        {
            var text = t.Text.Trim().Trim('[', ']', '(', ')', '{', '}').Trim();
            if (text.Length > 0)
            {
                _terms[text] = t with { Text = text };
            }
        }

        Anywhere = anywhere;
        RemoveResolution = removeResolution;
        ResolutionStashTags = removeResolution && resolutionStashTags;

        if (anywhere)
        {
            var alternatives = _terms.Keys
                .OrderByDescending(k => k.Length)
                .Select(Regex.Escape)
                .ToList();
            if (removeResolution)
            {
                alternatives.Add(ResolutionCore);
            }

            if (alternatives.Count > 0)
            {
                // whole word: no letter/digit directly before or after
                _wordRegex = new Regex(
                    $@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", alternatives)})(?![\p{{L}}\p{{N}}])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }
    }

    public bool Anywhere { get; }

    public bool RemoveResolution { get; }

    public bool ResolutionStashTags { get; }

    public bool IsEmpty => _terms.Count == 0 && !RemoveResolution;

    public bool RemovesAnyStashTag => ResolutionStashTags || _terms.Values.Any(t => t.RemoveStashTag);

    public static IReadOnlyList<string> ParseList(string? text) =>
        (text ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.Trim('[', ']').Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsResolution(string name) => ResolutionInBrackets.IsMatch(name ?? string.Empty);

    /// <summary>True when bracket content should go from the file name.</summary>
    public bool Matches(string text)
    {
        var name = (text ?? string.Empty).Trim();
        return name.Length > 0 && (_terms.ContainsKey(name) || (RemoveResolution && ResolutionInBrackets.IsMatch(name)));
    }

    /// <summary>True when a Stash scene tag with this name should be removed.</summary>
    public bool MatchesStashTag(string tagName)
    {
        var name = (tagName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return false;
        }

        if (_terms.TryGetValue(name, out var term))
        {
            return term.RemoveStashTag;
        }

        return ResolutionStashTags && ResolutionInBrackets.IsMatch(name);
    }

    /// <summary>One separator stays; " - " survives when the run was spaced around a dash.</summary>
    private static string CollapseSeparators(Match m)
    {
        var run = m.Value;
        return run[0] == ' ' && run[^1] == ' ' && run.Contains('-') ? " - " : run[..1];
    }

    /// <summary>
    /// New basename with the matching parts removed, or null when nothing changes.
    /// <paramref name="removed"/> lists what was stripped.
    /// </summary>
    public string? Clean(string basename, out IReadOnlyList<string> removed)
    {
        var found = new List<string>();
        var ext = Path.GetExtension(basename);
        var stem = basename[..^ext.Length];

        var cleaned = BracketGroup.Replace(stem, m =>
        {
            var inner = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (!Matches(inner))
            {
                return m.Value;
            }

            found.Add(inner.Trim());
            return string.Empty;
        });

        if (_wordRegex is not null)
        {
            cleaned = _wordRegex.Replace(cleaned, m =>
            {
                found.Add(m.Value.Trim());
                return string.Empty;
            });
            cleaned = EmptyBrackets.Replace(cleaned, string.Empty);
        }

        removed = found;
        if (found.Count == 0)
        {
            return null;
        }

        cleaned = SeparatorRun.Replace(cleaned, CollapseSeparators).Trim(' ', '_', '-', '.');
        return cleaned.Length == 0 ? string.Empty : cleaned + ext;
    }
}
