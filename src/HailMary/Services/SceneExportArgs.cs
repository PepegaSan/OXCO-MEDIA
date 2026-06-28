using System.Text;
using System.Text.Json;

namespace HailMary.Services;

internal static class SceneExportArgs
{
    public static string WriteScenesTempFile(IEnumerable<(double Start, double End)> pairs)
    {
        var data = pairs.Select(p => new[] { p.Start, p.End }).ToList();
        var path = Path.Combine(Path.GetTempPath(), $"hailmary_scenes_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
