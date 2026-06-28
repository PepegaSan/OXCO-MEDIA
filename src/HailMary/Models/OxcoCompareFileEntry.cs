namespace HailMary.Models;

public sealed class OxcoCompareFileEntry
{
    public required string Path { get; init; }

    public required string Rel { get; init; }

    public long Size { get; init; }

    public double Mtime { get; init; }

    public double? DurationSec { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public bool ProbeOk { get; set; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public string Stem => System.IO.Path.GetFileNameWithoutExtension(Path);
}
