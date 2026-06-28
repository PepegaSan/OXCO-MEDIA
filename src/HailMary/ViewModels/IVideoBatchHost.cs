namespace HailMary.ViewModels;

/// <summary>
/// Tools mit expliziter Video-Warteschlange (mehrere Dateien, Ordner expandieren).
/// </summary>
public interface IVideoBatchHost
{
    void AddDroppedPaths(IEnumerable<string> paths);
}
