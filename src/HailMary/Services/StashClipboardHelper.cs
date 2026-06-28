using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace HailMary.Services;

public static class StashClipboardHelper
{
    public sealed record ReadResult(string? SceneId, string? Error, bool HadClipboardContent, string? ClipboardPreview);

    private static readonly string[] ExtraTextFormats =
    [
        "UniformResourceLocatorW",
        "UniformResourceLocator",
        "text/uri-list",
        "application/x-moz-url",
        "text/plain",
    ];

    public static async Task<ReadResult> TryReadSceneIdAsync()
    {
        DataPackageView content;
        try
        {
            content = Clipboard.GetContent();
        }
        catch (Exception ex)
        {
            return new ReadResult(null, $"Zwischenablage nicht lesbar: {ex.Message}", false, null);
        }

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            foreach (var part in SplitClipboardPayload(text))
            {
                if (seen.Add(part))
                {
                    candidates.Add(part);
                }
            }
        }

        if (content.Contains(StandardDataFormats.Text))
        {
            await TryAddTextAsync(AddCandidate, content.GetTextAsync());
        }

        if (content.Contains(StandardDataFormats.Uri))
        {
            try
            {
                var uri = await content.GetUriAsync();
                AddCandidate(uri?.AbsoluteUri);
            }
            catch
            {
                // ignore
            }
        }

        if (content.Contains(StandardDataFormats.Html))
        {
            await TryAddTextAsync(AddCandidate, content.GetHtmlFormatAsync());
        }

        foreach (var format in ExtraTextFormats)
        {
            if (content.Contains(format))
            {
                await TryAddRawFormatAsync(AddCandidate, content, format);
            }
        }

        foreach (var format in content.AvailableFormats)
        {
            if (IsKnownFormat(format))
            {
                continue;
            }

            if (format.Contains("text", StringComparison.OrdinalIgnoreCase)
                || format.Contains("uri", StringComparison.OrdinalIgnoreCase)
                || format.Contains("url", StringComparison.OrdinalIgnoreCase)
                || format.Contains("UniformResourceLocator", StringComparison.OrdinalIgnoreCase))
            {
                await TryAddRawFormatAsync(AddCandidate, content, format);
            }
        }

        if (candidates.Count == 0)
        {
            await TryAddTextAsync(AddCandidate, content.GetTextAsync());
        }

        var hadContent = candidates.Count > 0;
        var preview = candidates.FirstOrDefault()?.Trim();
        if (preview?.Length > 80)
        {
            preview = preview[..80] + "…";
        }

        foreach (var chunk in candidates)
        {
            var sid = StashSceneIdParser.FromText(chunk) ?? StashSceneIdParser.FromHtml(chunk);
            if (sid is not null)
            {
                return new ReadResult(sid, null, hadContent, preview);
            }
        }

        var combined = string.Join("\n", candidates);
        var combinedSid = StashSceneIdParser.FromText(combined) ?? StashSceneIdParser.FromHtml(combined);
        if (combinedSid is not null)
        {
            return new ReadResult(combinedSid, null, hadContent, preview);
        }

        return new ReadResult(
            null,
            hadContent
                ? $"Keine Szenen-ID in Zwischenablage erkannt{(preview is null ? "." : $": „{preview}“")}"
                : "Zwischenablage ist leer.",
            hadContent,
            preview);
    }

    private static bool IsKnownFormat(string format) =>
        format == StandardDataFormats.Text
        || format == StandardDataFormats.Uri
        || format == StandardDataFormats.Html
        || ExtraTextFormats.Contains(format, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitClipboardPayload(string text)
    {
        var normalized = text.Replace('\0', '\n').Replace('\r', '\n');
        foreach (var line in normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return line;
        }

        if (!normalized.Contains('\n', StringComparison.Ordinal))
        {
            yield break;
        }

        yield return normalized;
    }

    private static async Task TryAddTextAsync(Action<string?> add, IAsyncOperation<string> read)
    {
        try
        {
            add(await read);
        }
        catch
        {
            // ignore unsupported clipboard formats
        }
    }

    private static async Task TryAddRawFormatAsync(Action<string?> add, DataPackageView content, string format)
    {
        try
        {
            var data = await content.GetDataAsync(format);
            switch (data)
            {
                case string text:
                    add(text);
                    break;
                case Uri uri:
                    add(uri.AbsoluteUri);
                    break;
                case Windows.Storage.Streams.IBuffer buffer:
                    add(ReadBufferAsUtf8(buffer));
                    break;
            }
        }
        catch
        {
            // ignore unsupported clipboard formats
        }
    }

    private static string ReadBufferAsUtf8(Windows.Storage.Streams.IBuffer buffer)
    {
        if (buffer.Length == 0)
        {
            return string.Empty;
        }

        using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
