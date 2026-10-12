using System.Text.Json;

namespace HailMary.Services;

/// <summary>One video file of a scene (paths as Stash sees them, e.g. /data/…).</summary>
public sealed record StashSceneFileItem(
    string FileId,
    string SceneId,
    string Path,
    string Basename,
    IReadOnlyList<StashTagItem> SceneTags);

public sealed partial class StashGraphQlClient
{
    /// <summary>Every file of every scene (paged, no limit).</summary>
    public async Task<IReadOnlyList<StashSceneFileItem>> GetAllSceneFilesAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            query FindScenes($filter: FindFilterType) {
              findScenes(filter: $filter) {
                count
                scenes { id files { id path basename } tags { id name } }
              }
            }
            """;

        const int perPage = 500;
        var result = new List<StashSceneFileItem>();
        var seenScenes = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var doc = await GraphQlDocumentAsync(
                query,
                new { filter = new { page, per_page = perPage, sort = "id", direction = "ASC" } },
                cancellationToken);
            var scenes = doc.RootElement.GetProperty("data").GetProperty("findScenes").GetProperty("scenes");
            var count = 0;
            foreach (var scene in scenes.EnumerateArray())
            {
                count++;
                var sceneId = scene.GetProperty("id").GetString() ?? string.Empty;
                if (!seenScenes.Add(sceneId))
                {
                    continue;
                }

                var tags = new List<StashTagItem>();
                if (scene.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in tagsEl.EnumerateArray())
                    {
                        tags.Add(new StashTagItem(tag.GetProperty("id").GetString() ?? "", tag.GetProperty("name").GetString() ?? ""));
                    }
                }

                if (!scene.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var file in filesEl.EnumerateArray())
                {
                    var path = file.GetProperty("path").GetString() ?? string.Empty;
                    var basename = file.TryGetProperty("basename", out var bn) ? bn.GetString() ?? string.Empty : string.Empty;
                    if (basename.Length == 0)
                    {
                        basename = path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];
                    }

                    result.Add(new StashSceneFileItem(file.GetProperty("id").GetString() ?? "", sceneId, path, basename, tags));
                }
            }

            progress?.Report(seenScenes.Count);
            if (count < perPage)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>True when the server knows the moveFiles mutation (Stash ≥ 0.18).</summary>
    public async Task<bool> SupportsMoveFilesAsync(CancellationToken cancellationToken = default)
    {
        const string query = """
            query { __type(name: "MoveFilesInput") { inputFields { name } } }
            """;
        using var doc = await GraphQlDocumentAsync(query, null, cancellationToken);
        var type = doc.RootElement.GetProperty("data").GetProperty("__type");
        if (type.ValueKind != JsonValueKind.Object || !type.TryGetProperty("inputFields", out var fields))
        {
            return false;
        }

        return fields.EnumerateArray().Any(f => f.GetProperty("name").GetString() == "destination_basename");
    }

    /// <summary>Rename a file through Stash: disk and database move together, nothing needs a rescan.</summary>
    public async Task MoveFileAsync(string fileId, string destinationFolder, string destinationBasename, CancellationToken cancellationToken = default)
    {
        const string mutation = """
            mutation MoveFiles($input: MoveFilesInput!) {
              moveFiles(input: $input)
            }
            """;
        using var _ = await GraphQlDocumentAsync(
            mutation,
            new
            {
                input = new
                {
                    ids = new[] { fileId },
                    destination_folder = destinationFolder,
                    destination_basename = destinationBasename,
                },
            },
            cancellationToken);
    }

    /// <summary>Remove tags from many scenes at once (bulkSceneUpdate, mode REMOVE).</summary>
    public async Task BulkRemoveSceneTagsAsync(
        IReadOnlyList<string> sceneIds,
        IReadOnlyList<string> tagIds,
        CancellationToken cancellationToken = default)
    {
        const string mutation = """
            mutation BulkSceneUpdate($input: BulkSceneUpdateInput!) {
              bulkSceneUpdate(input: $input) { id }
            }
            """;
        foreach (var chunk in sceneIds.Chunk(200))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var _ = await GraphQlDocumentAsync(
                mutation,
                new { input = new { ids = chunk, tag_ids = new { ids = tagIds, mode = "REMOVE" } } },
                cancellationToken);
        }
    }
}
