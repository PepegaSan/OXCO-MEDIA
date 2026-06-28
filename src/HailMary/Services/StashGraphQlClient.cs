using System.Text;
using System.Text.Json;

namespace HailMary.Services;

public sealed record StashSceneItem(string SceneId, string Title, string Date, string Path);

public sealed record StashTagItem(string Id, string Name);

public sealed record StashMarkerItem(
    string Id,
    string Title,
    double Seconds,
    double? EndSeconds,
    StashTagItem? PrimaryTag);

public sealed record StashSceneDetails(
    string SceneId,
    string Title,
    string Details,
    string Date,
    string Path,
    double? Duration,
    IReadOnlyList<StashTagItem> Tags,
    IReadOnlyList<StashMarkerItem> Markers);

public sealed record StashExportMarkerRow(
    string MarkerId,
    string MarkerTitle,
    string StartSeconds,
    string EndSeconds,
    string PrimaryTag,
    string PrimaryTagId,
    string SecondaryTags,
    string SceneId,
    string SceneTitle,
    string FilePath,
    string FileFrameRate);

public sealed record StashBatchSceneItem(
    string SceneId,
    string Title,
    string Path,
    IReadOnlyList<StashTagItem> Tags);

public sealed record StashOverflowMarkerItem(
    string MarkerId,
    string SceneId,
    string SceneTitle,
    string MarkerTitle,
    string PrimaryTagName,
    string? PrimaryTagId,
    double Seconds,
    double EndSeconds,
    double Duration);

public sealed class StashGraphQlClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private string _endpoint = string.Empty;
    private string _apiKey = string.Empty;

    public void Configure(string endpoint, string apiKey)
    {
        var ep = (endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(ep) && !ep.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase))
        {
            ep += "/graphql";
        }

        _endpoint = ep;
        _apiKey = (apiKey ?? string.Empty).Trim();
    }

    public async Task<string> PingAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await GraphQlDocumentAsync(
            "query Version { version { version } }",
            null,
            cancellationToken);
        var data = doc.RootElement.GetProperty("data");
        return data.TryGetProperty("version", out var version)
            && version.TryGetProperty("version", out var v)
            ? v.GetString() ?? "unknown"
            : "unknown";
    }

    public async Task<IReadOnlyList<StashSceneItem>> FindScenesAsync(string text, CancellationToken cancellationToken = default)
    {
        const string query = """
            query FindScenes($filter: FindFilterType) {
              findScenes(filter: $filter) {
                scenes { id title date files { path } }
              }
            }
            """;

        var trimmed = (text ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            var sceneId = StashSceneIdParser.FromText(trimmed);
            if (sceneId is not null)
            {
                try
                {
                    var direct = await GetSceneAsync(sceneId, cancellationToken);
                    return [direct];
                }
                catch
                {
                    // fallback to text search
                }
            }
        }

        var textLower = trimmed.ToLowerInvariant();
        var scenes = new List<StashSceneItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var page = 1;
        const int perPage = 200;
        var maxPages = string.IsNullOrEmpty(trimmed) ? 3 : 10;

        for (var i = 0; i < maxPages; i++)
        {
            object filter = string.IsNullOrEmpty(trimmed)
                ? new { page, per_page = perPage }
                : new { page, per_page = perPage, q = trimmed };

            using var doc = await GraphQlDocumentAsync(query, new { filter }, cancellationToken);
            var data = doc.RootElement.GetProperty("data");

            if (!data.TryGetProperty("findScenes", out var findScenes)
                || !findScenes.TryGetProperty("scenes", out var raw)
                || raw.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var scene in raw.EnumerateArray())
            {
                count++;
                var item = ParseSceneListItem(scene);
                if (string.IsNullOrEmpty(item.SceneId) || !seen.Add(item.SceneId))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(textLower)
                    && !item.Title.Contains(textLower, StringComparison.OrdinalIgnoreCase)
                    && !item.Path.Contains(textLower, StringComparison.OrdinalIgnoreCase)
                    && !item.SceneId.Contains(textLower, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                scenes.Add(item);
            }

            if (count < perPage)
            {
                break;
            }

            page++;
        }

        return scenes;
    }

    private static StashSceneItem ParseSceneListItem(JsonElement scene)
    {
        var sceneId = scene.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var title = scene.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
        var date = scene.TryGetProperty("date", out var dateEl) ? dateEl.GetString() ?? "" : "";
        var path = string.Empty;
        if (scene.TryGetProperty("files", out var filesEl)
            && filesEl.ValueKind == JsonValueKind.Array
            && filesEl.GetArrayLength() > 0
            && filesEl[0].TryGetProperty("path", out var pathEl))
        {
            path = pathEl.GetString() ?? "";
        }

        return new StashSceneItem(sceneId, title, date, path);
    }

    public async Task<StashSceneItem> GetSceneAsync(string sceneId, CancellationToken cancellationToken = default)
    {
        const string query = """
            query FindScene($id: ID!) {
              findScene(id: $id) {
                id title date files { path duration }
              }
            }
            """;

        using var doc = await GraphQlDocumentAsync(query, new { id = sceneId }, cancellationToken);
        var data = doc.RootElement.GetProperty("data");
        if (!data.TryGetProperty("findScene", out var scene) || scene.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidOperationException("Szene nicht gefunden.");
        }

        var title = scene.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
        var date = scene.TryGetProperty("date", out var dateEl) ? dateEl.GetString() ?? "" : "";
        var path = string.Empty;
        if (scene.TryGetProperty("files", out var filesEl)
            && filesEl.ValueKind == JsonValueKind.Array
            && filesEl.GetArrayLength() > 0
            && filesEl[0].TryGetProperty("path", out var pathEl))
        {
            path = pathEl.GetString() ?? "";
        }

        var id = scene.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? sceneId : sceneId;
        return new StashSceneItem(id, title, date, path);
    }

    public async Task<StashSceneDetails> GetSceneDetailsAsync(string sceneId, CancellationToken cancellationToken = default)
    {
        const string query = """
            query FindScene($id: ID!) {
              findScene(id: $id) {
                id title date details
                files { path duration }
                tags { id name }
                scene_markers {
                  id title seconds end_seconds
                  primary_tag { id name }
                }
              }
            }
            """;

        using var doc = await GraphQlDocumentAsync(query, new { id = sceneId }, cancellationToken);
        var scene = doc.RootElement.GetProperty("data").GetProperty("findScene");
        if (scene.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidOperationException("Szene nicht gefunden.");
        }

        return ParseSceneDetails(scene, sceneId);
    }

    public async Task<IReadOnlyList<StashTagItem>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        const string query = """
            query FindTags($filter: FindFilterType) {
              findTags(filter: $filter) {
                tags { id name }
              }
            }
            """;

        using var doc = await GraphQlDocumentAsync(
            query,
            new { filter = new { per_page = 5000, sort = "name", direction = "ASC" } },
            cancellationToken);
        var tags = doc.RootElement.GetProperty("data").GetProperty("findTags").GetProperty("tags");
        var result = new List<StashTagItem>();
        foreach (var tag in tags.EnumerateArray())
        {
            var id = tag.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var name = tag.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
            {
                result.Add(new StashTagItem(id, name));
            }
        }

        return result;
    }

    public async Task UpdateSceneTagsAsync(string sceneId, IReadOnlyList<string> tagIds, CancellationToken cancellationToken = default)
    {
        await UpdateSceneAsync(sceneId, tagIds: tagIds, cancellationToken: cancellationToken);
    }

    public async Task UpdateSceneAsync(
        string sceneId,
        string? title = null,
        string? details = null,
        IReadOnlyList<string>? tagIds = null,
        CancellationToken cancellationToken = default)
    {
        const string mutation = """
            mutation SceneUpdate($input: SceneUpdateInput!) {
              sceneUpdate(input: $input) { id }
            }
            """;

        var input = new Dictionary<string, object?> { ["id"] = sceneId };
        if (title is not null)
        {
            input["title"] = title;
        }

        if (details is not null)
        {
            input["details"] = details;
        }

        if (tagIds is not null)
        {
            input["tag_ids"] = tagIds;
        }

        await GraphQlDocumentAsync(mutation, new { input }, cancellationToken);
    }

    public async Task<string> CreateTagAsync(string name, CancellationToken cancellationToken = default)
    {
        const string mutation = """
            mutation TagCreate($input: TagCreateInput!) {
              tagCreate(input: $input) { id name }
            }
            """;

        using var doc = await GraphQlDocumentAsync(
            mutation,
            new { input = new { name } },
            cancellationToken);
        var created = doc.RootElement.GetProperty("data").GetProperty("tagCreate");
        return created.GetProperty("id").GetString() ?? throw new InvalidOperationException("Tag-Erstellung fehlgeschlagen.");
    }

    public async Task CreateMarkerAsync(
        string sceneId,
        string title,
        double seconds,
        double? endSeconds,
        string primaryTagId,
        CancellationToken cancellationToken = default)
    {
        const string mutation = """
            mutation SceneMarkerCreate($input: SceneMarkerCreateInput!) {
              sceneMarkerCreate(input: $input) { id }
            }
            """;

        var input = new Dictionary<string, object?>
        {
            ["scene_id"] = sceneId,
            ["title"] = title,
            ["seconds"] = seconds,
            ["primary_tag_id"] = primaryTagId,
        };
        if (endSeconds.HasValue)
        {
            input["end_seconds"] = endSeconds.Value;
        }

        await GraphQlDocumentAsync(mutation, new { input }, cancellationToken);
    }

    public async Task UpdateMarkerAsync(
        string markerId,
        string title,
        double seconds,
        double? endSeconds,
        string? primaryTagId,
        CancellationToken cancellationToken = default)
    {
        const string mutation = """
            mutation SceneMarkerUpdate($input: SceneMarkerUpdateInput!) {
              sceneMarkerUpdate(input: $input) { id }
            }
            """;

        var input = new Dictionary<string, object?>
        {
            ["id"] = markerId,
            ["title"] = title,
            ["seconds"] = seconds,
        };
        if (endSeconds.HasValue)
        {
            input["end_seconds"] = endSeconds.Value;
        }

        if (!string.IsNullOrEmpty(primaryTagId))
        {
            input["primary_tag_id"] = primaryTagId;
        }

        await GraphQlDocumentAsync(mutation, new { input }, cancellationToken);
    }

    public async Task DeleteMarkerAsync(string markerId, CancellationToken cancellationToken = default)
    {
        const string mutation = """
            mutation SceneMarkerDestroy($id: ID!) {
              sceneMarkerDestroy(id: $id)
            }
            """;

        await GraphQlDocumentAsync(mutation, new { id = markerId }, cancellationToken);
    }

    public async Task DeleteTagAsync(string tagId, CancellationToken cancellationToken = default)
    {
        const string mutation = """
            mutation TagDestroy($input: TagDestroyInput!) {
              tagDestroy(input: $input)
            }
            """;

        await GraphQlDocumentAsync(mutation, new { input = new { id = tagId } }, cancellationToken);
    }

    public async Task<IReadOnlyList<StashBatchSceneItem>> FindScenesWithTagsAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            query FindScenes($filter: FindFilterType) {
              findScenes(filter: $filter) {
                scenes { id title files { path } tags { id name } }
              }
            }
            """;

        var trimmed = (text ?? string.Empty).Trim();
        var textLower = trimmed.ToLowerInvariant();
        var scenes = new List<StashBatchSceneItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var page = 1;
        const int perPage = 200;
        var maxPages = string.IsNullOrEmpty(trimmed) ? 3 : 10;

        for (var i = 0; i < maxPages; i++)
        {
            object filter = string.IsNullOrEmpty(trimmed)
                ? new { page, per_page = perPage }
                : new { page, per_page = perPage, q = trimmed };

            using var doc = await GraphQlDocumentAsync(query, new { filter }, cancellationToken);
            var data = doc.RootElement.GetProperty("data");
            if (!data.TryGetProperty("findScenes", out var findScenes)
                || !findScenes.TryGetProperty("scenes", out var raw)
                || raw.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var scene in raw.EnumerateArray())
            {
                count++;
                var item = ParseBatchScene(scene);
                if (string.IsNullOrEmpty(item.SceneId) || !seen.Add(item.SceneId))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(textLower)
                    && !item.Title.Contains(textLower, StringComparison.OrdinalIgnoreCase)
                    && !item.Path.Contains(textLower, StringComparison.OrdinalIgnoreCase)
                    && !item.SceneId.Contains(textLower, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                scenes.Add(item);
            }

            if (count < perPage)
            {
                break;
            }

            page++;
        }

        return scenes;
    }

    public async Task<string?> FindAdjacentSceneIdAsync(
        string currentSceneId,
        bool next,
        CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(currentSceneId.Trim(), out var numId))
        {
            return null;
        }

        var step = next ? 1L : -1L;
        for (var delta = 1; delta <= 100; delta++)
        {
            var tryId = numId + step * delta;
            if (tryId <= 0)
            {
                break;
            }

            try
            {
                var scene = await GetSceneAsync(tryId.ToString(), cancellationToken);
                return scene.SceneId;
            }
            catch
            {
                // ID-Lücken überspringen
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<StashBatchSceneItem>> FindScenesByTagNameAsync(
        string tagName,
        CancellationToken cancellationToken = default)
    {
        var trimmed = (tagName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return [];
        }

        var tags = await GetTagsAsync(cancellationToken);
        var tag = tags.FirstOrDefault(t => t.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (tag is null)
        {
            return [];
        }

        const string query = """
            query FindScenes($filter: FindFilterType, $scene_filter: SceneFilterType) {
              findScenes(filter: $filter, scene_filter: $scene_filter) {
                scenes { id title files { path } tags { id name } }
              }
            }
            """;

        var scenes = new List<StashBatchSceneItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var page = 1;
        const int perPage = 200;

        while (page <= 50)
        {
            var filter = new { page, per_page = perPage };
            var sceneFilter = new
            {
                tags = new { value = new[] { tag.Id }, modifier = "INCLUDES" },
            };

            using var doc = await GraphQlDocumentAsync(
                query,
                new { filter, scene_filter = sceneFilter },
                cancellationToken);
            var data = doc.RootElement.GetProperty("data");
            if (!data.TryGetProperty("findScenes", out var findScenes)
                || !findScenes.TryGetProperty("scenes", out var raw)
                || raw.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var scene in raw.EnumerateArray())
            {
                count++;
                var item = ParseBatchScene(scene);
                if (!string.IsNullOrEmpty(item.SceneId) && seen.Add(item.SceneId))
                {
                    scenes.Add(item);
                }
            }

            if (count < perPage)
            {
                break;
            }

            page++;
        }

        return scenes;
    }

    public async Task<IReadOnlyList<StashOverflowMarkerItem>> FindOverflowingMarkersAsync(
        IProgress<int>? pageProgress = null,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            query FindScenes($filter: FindFilterType) {
              findScenes(filter: $filter) {
                scenes {
                  id
                  title
                  files { path duration }
                  scene_markers {
                    id
                    title
                    seconds
                    end_seconds
                    primary_tag { id name }
                  }
                }
              }
            }
            """;

        var results = new List<StashOverflowMarkerItem>();
        var seenScenes = new HashSet<string>(StringComparer.Ordinal);
        var page = 1;
        const int perPage = 200;

        while (page <= 500)
        {
            pageProgress?.Report(page);
            using var doc = await GraphQlDocumentAsync(
                query,
                new { filter = new { page, per_page = perPage } },
                cancellationToken);
            var data = doc.RootElement.GetProperty("data");
            if (!data.TryGetProperty("findScenes", out var findScenes)
                || !findScenes.TryGetProperty("scenes", out var raw)
                || raw.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var scene in raw.EnumerateArray())
            {
                count++;
                var sceneId = scene.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(sceneId) || !seenScenes.Add(sceneId))
                {
                    continue;
                }

                if (!scene.TryGetProperty("files", out var filesEl)
                    || filesEl.ValueKind != JsonValueKind.Array
                    || filesEl.GetArrayLength() == 0)
                {
                    continue;
                }

                var file = filesEl[0];
                if (!file.TryGetProperty("duration", out var durEl))
                {
                    continue;
                }

                double duration;
                try
                {
                    duration = durEl.ValueKind == JsonValueKind.Number
                        ? durEl.GetDouble()
                        : double.Parse(durEl.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                }
                catch
                {
                    continue;
                }

                if (duration <= 0)
                {
                    continue;
                }

                var sceneTitle = scene.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
                if (!scene.TryGetProperty("scene_markers", out var markersEl)
                    || markersEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var marker in markersEl.EnumerateArray())
                {
                    var markerId = marker.TryGetProperty("id", out var midEl) ? midEl.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(markerId))
                    {
                        continue;
                    }

                    var seconds = marker.TryGetProperty("seconds", out var secEl) ? secEl.GetDouble() : 0;
                    var endSeconds = marker.TryGetProperty("end_seconds", out var endEl) && endEl.ValueKind != JsonValueKind.Null
                        ? endEl.GetDouble()
                        : 0;
                    if (seconds <= duration && (endSeconds <= 0 || endSeconds <= duration))
                    {
                        continue;
                    }

                    var markerTitle = marker.TryGetProperty("title", out var mtEl) ? mtEl.GetString() ?? "" : "";
                    string primaryTagName = string.Empty;
                    string? primaryTagId = null;
                    if (marker.TryGetProperty("primary_tag", out var ptEl)
                        && ptEl.ValueKind == JsonValueKind.Object)
                    {
                        primaryTagName = ptEl.TryGetProperty("name", out var pnEl) ? pnEl.GetString() ?? "" : "";
                        primaryTagId = ptEl.TryGetProperty("id", out var piEl) ? piEl.GetString() : null;
                    }

                    results.Add(new StashOverflowMarkerItem(
                        markerId,
                        sceneId,
                        sceneTitle,
                        markerTitle,
                        primaryTagName,
                        primaryTagId,
                        seconds,
                        endSeconds,
                        duration));
                }
            }

            if (count < perPage)
            {
                break;
            }

            page++;
        }

        return results;
    }

    public async Task<IReadOnlyList<StashExportMarkerRow>> FindSceneMarkersAsync(
        string? nameFilter,
        string? pathFilter,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            query ExportMarkers($filter: FindFilterType, $scene_marker_filter: SceneMarkerFilterType) {
              findSceneMarkers(filter: $filter, scene_marker_filter: $scene_marker_filter) {
                count
                scene_markers {
                  id title seconds end_seconds
                  primary_tag { id name }
                  tags { id name }
                  scene {
                    id title
                    files { path frame_rate }
                  }
                }
              }
            }
            """;

        var nameQ = (nameFilter ?? string.Empty).Trim();
        var pathQ = (pathFilter ?? string.Empty).Trim();
        var merged = new List<StashExportMarkerRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var nameQueries = string.IsNullOrEmpty(nameQ) ? [""] : nameQ.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pathQueries = string.IsNullOrEmpty(pathQ) ? [""] : pathQ.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var pathTerm in pathQueries)
        {
            foreach (var nameTerm in nameQueries)
            {
                var page = 1;
                const int perPage = 200;
                while (page <= 20)
                {
                    var pageFilter = new Dictionary<string, object> { ["page"] = page, ["per_page"] = perPage };
                    if (!string.IsNullOrEmpty(nameTerm))
                    {
                        pageFilter["q"] = nameTerm;
                    }

                    object? sceneMarkerFilter = null;
                    if (!string.IsNullOrEmpty(pathTerm))
                    {
                        sceneMarkerFilter = new
                        {
                            scene_filter = new
                            {
                                path = new { value = pathTerm, modifier = "INCLUDES" },
                            },
                        };
                    }

                    using var doc = await GraphQlDocumentAsync(
                        query,
                        new { filter = pageFilter, scene_marker_filter = sceneMarkerFilter },
                        cancellationToken);

                    var payload = doc.RootElement.GetProperty("data").GetProperty("findSceneMarkers");
                    if (!payload.TryGetProperty("scene_markers", out var markers)
                        || markers.ValueKind != JsonValueKind.Array)
                    {
                        break;
                    }

                    var count = 0;
                    foreach (var marker in markers.EnumerateArray())
                    {
                        count++;
                        var row = ParseExportMarkerRow(marker);
                        if (string.IsNullOrEmpty(row.MarkerId))
                        {
                            continue;
                        }

                        var key = string.IsNullOrEmpty(row.MarkerId) ? row.FilePath + row.StartSeconds : row.MarkerId;
                        if (!seen.Add(key))
                        {
                            continue;
                        }

                        if (!string.IsNullOrEmpty(pathTerm)
                            && !row.FilePath.Contains(pathTerm, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        merged.Add(row);
                    }

                    if (count < perPage)
                    {
                        break;
                    }

                    page++;
                }
            }
        }

        return merged;
    }

    private static StashBatchSceneItem ParseBatchScene(JsonElement scene)
    {
        var sceneId = scene.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var title = scene.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
        var path = string.Empty;
        if (scene.TryGetProperty("files", out var filesEl)
            && filesEl.ValueKind == JsonValueKind.Array
            && filesEl.GetArrayLength() > 0
            && filesEl[0].TryGetProperty("path", out var pathEl))
        {
            path = pathEl.GetString() ?? "";
        }

        var tags = new List<StashTagItem>();
        if (scene.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tagsEl.EnumerateArray())
            {
                var tagId = tag.TryGetProperty("id", out var tid) ? tid.GetString() ?? "" : "";
                var tagName = tag.TryGetProperty("name", out var tname) ? tname.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(tagId) && !string.IsNullOrEmpty(tagName))
                {
                    tags.Add(new StashTagItem(tagId, tagName));
                }
            }
        }

        return new StashBatchSceneItem(sceneId, title, path, tags);
    }

    private static StashExportMarkerRow ParseExportMarkerRow(JsonElement marker)
    {
        var tagNames = new List<string>();
        if (marker.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tagsEl.EnumerateArray())
            {
                if (tag.TryGetProperty("name", out var tn))
                {
                    var name = tn.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        tagNames.Add(name);
                    }
                }
            }
        }

        var primaryName = string.Empty;
        var primaryId = string.Empty;
        if (marker.TryGetProperty("primary_tag", out var pt) && pt.ValueKind == JsonValueKind.Object)
        {
            primaryName = pt.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";
            primaryId = pt.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "";
        }

        var sceneId = string.Empty;
        var sceneTitle = string.Empty;
        var filePath = string.Empty;
        var frameRate = string.Empty;
        if (marker.TryGetProperty("scene", out var scene) && scene.ValueKind == JsonValueKind.Object)
        {
            sceneId = scene.TryGetProperty("id", out var sid) ? sid.GetString() ?? "" : "";
            sceneTitle = scene.TryGetProperty("title", out var st) ? st.GetString() ?? "" : "";
            if (scene.TryGetProperty("files", out var filesEl)
                && filesEl.ValueKind == JsonValueKind.Array
                && filesEl.GetArrayLength() > 0)
            {
                var f0 = filesEl[0];
                filePath = f0.TryGetProperty("path", out var fp) ? fp.GetString() ?? "" : "";
                if (f0.TryGetProperty("frame_rate", out var fr) && fr.ValueKind == JsonValueKind.Number)
                {
                    frameRate = fr.GetDouble().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }

        static string Sec(JsonElement el, string prop)
        {
            if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Number)
            {
                return "";
            }

            return v.GetDouble().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        return new StashExportMarkerRow(
            marker.TryGetProperty("id", out var mid) ? mid.GetString() ?? "" : "",
            marker.TryGetProperty("title", out var mt) ? mt.GetString() ?? "" : "",
            Sec(marker, "seconds"),
            Sec(marker, "end_seconds"),
            primaryName,
            primaryId,
            string.Join("|", tagNames),
            sceneId,
            sceneTitle,
            filePath,
            frameRate);
    }

    private static StashSceneDetails ParseSceneDetails(JsonElement scene, string fallbackId)
    {
        var title = scene.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
        var details = scene.TryGetProperty("details", out var detailsEl) ? detailsEl.GetString() ?? "" : "";
        var date = scene.TryGetProperty("date", out var dateEl) ? dateEl.GetString() ?? "" : "";
        var path = string.Empty;
        double? duration = null;
        if (scene.TryGetProperty("files", out var filesEl)
            && filesEl.ValueKind == JsonValueKind.Array
            && filesEl.GetArrayLength() > 0)
        {
            var first = filesEl[0];
            if (first.TryGetProperty("path", out var pathEl))
            {
                path = pathEl.GetString() ?? "";
            }

            if (first.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
            {
                duration = durEl.GetDouble();
            }
        }

        var id = scene.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? fallbackId : fallbackId;
        var tags = new List<StashTagItem>();
        if (scene.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tagsEl.EnumerateArray())
            {
                var tagId = tag.TryGetProperty("id", out var tid) ? tid.GetString() ?? "" : "";
                var tagName = tag.TryGetProperty("name", out var tname) ? tname.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(tagId) && !string.IsNullOrEmpty(tagName))
                {
                    tags.Add(new StashTagItem(tagId, tagName));
                }
            }
        }

        var markers = new List<StashMarkerItem>();
        if (scene.TryGetProperty("scene_markers", out var markersEl) && markersEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var marker in markersEl.EnumerateArray())
            {
                var markerId = marker.TryGetProperty("id", out var mid) ? mid.GetString() ?? "" : "";
                var markerTitle = marker.TryGetProperty("title", out var mt) ? mt.GetString() ?? "" : "";
                var seconds = marker.TryGetProperty("seconds", out var secEl) && secEl.ValueKind == JsonValueKind.Number
                    ? secEl.GetDouble()
                    : 0;
                double? endSeconds = marker.TryGetProperty("end_seconds", out var endEl) && endEl.ValueKind == JsonValueKind.Number
                    ? endEl.GetDouble()
                    : null;
                StashTagItem? primaryTag = null;
                if (marker.TryGetProperty("primary_tag", out var ptEl) && ptEl.ValueKind == JsonValueKind.Object)
                {
                    var ptId = ptEl.TryGetProperty("id", out var ptid) ? ptid.GetString() ?? "" : "";
                    var ptName = ptEl.TryGetProperty("name", out var ptname) ? ptname.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(ptId))
                    {
                        primaryTag = new StashTagItem(ptId, ptName);
                    }
                }

                if (!string.IsNullOrEmpty(markerId))
                {
                    markers.Add(new StashMarkerItem(markerId, markerTitle, seconds, endSeconds, primaryTag));
                }
            }
        }

        return new StashSceneDetails(id, title, details, date, path, duration, tags, markers);
    }

    private async Task<JsonDocument> GraphQlDocumentAsync(string query, object? variables, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_endpoint))
        {
            throw new InvalidOperationException("GraphQL-URL fehlt — bitte in den Einstellungen eintragen.");
        }

        var payload = JsonSerializer.Serialize(new { query, variables });
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.TryAddWithoutValidation("ApiKey", _apiKey);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        var bodyText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var snippet = bodyText.Length > 600 ? bodyText[..600] : bodyText;
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {snippet}");
        }

        var doc = JsonDocument.Parse(bodyText);
        if (doc.RootElement.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var first = errors[0];
            var message = first.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
            doc.Dispose();
            throw new InvalidOperationException(message ?? "Unbekannter GraphQL-Fehler");
        }

        if (!doc.RootElement.TryGetProperty("data", out _))
        {
            doc.Dispose();
            throw new InvalidOperationException("GraphQL-Antwort ohne data-Feld.");
        }

        return doc;
    }

    public void Dispose() => _http.Dispose();
}
