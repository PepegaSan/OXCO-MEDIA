namespace HailMary.Services;

/// <summary>Unified FFmpeg encoder labels for UI; wire ids stay bridge-compatible.</summary>
public static class FfmpegEncoderCatalog
{
    public sealed record Option(string Id, string Label);

    public const string DefaultSoftHardwareId = "cpu";
    public const string DefaultLibx264Id = "libx264";

    /// <summary>Perfect Loop / Clip Joiner / Media Overlay (ids: nvidia_*, cpu*).</summary>
    public static IReadOnlyList<Option> SoftHardware { get; } =
    [
        new("cpu", "libx H.264"),
        new("cpu_hevc", "libx H.265"),
        new("nvidia_h264", "NVENC H.264"),
        new("nvidia_hevc", "NVENC H.265"),
    ];

    public static IReadOnlyList<Option> SoftHardwareWithCopy { get; } =
    [
        ..SoftHardware,
        new("copy", "Copy (ohne Re-Encode)"),
    ];

    /// <summary>Scene / Intro / Audio / Bitrate style (ids: libx264, h264_nvenc, …).</summary>
    public static IReadOnlyList<Option> LibAndNvenc { get; } =
    [
        new("libx264", "libx H.264"),
        new("libx265", "libx H.265"),
        new("h264_nvenc", "NVENC H.264"),
        new("hevc_nvenc", "NVENC H.265"),
    ];

    public static IReadOnlyList<Option> LibAndNvencWithCopy { get; } =
    [
        new("copy", "Copy (ohne Re-Encode)"),
        ..LibAndNvenc,
    ];

    public static IReadOnlyList<Option> LibNvencVp9 { get; } =
    [
        new("libx264", "libx H.264"),
        new("libx265", "libx H.265"),
        new("libvpx-vp9", "VP9 (libvpx)"),
        new("h264_nvenc", "NVENC H.264"),
        new("hevc_nvenc", "NVENC H.265"),
    ];

    public static IReadOnlyList<Option> IntroVideoCodecs { get; } =
    [
        new("libx264", "libx H.264"),
        new("libx265", "libx H.265"),
        new("libvpx-vp9", "VP9 (libvpx)"),
        new("libaom-av1", "AV1 (libaom)"),
        new("libsvtav1", "AV1 (SVT)"),
        new("h264_nvenc", "NVENC H.264"),
        new("hevc_nvenc", "NVENC H.265"),
        new("av1_nvenc", "NVENC AV1"),
        new("mpeg4", "MPEG-4"),
        new("copy", "Copy (ohne Re-Encode)"),
    ];

    /// <summary>Text to Video software encoders (no NVENC in that bridge yet).</summary>
    public static IReadOnlyList<Option> TextToVideo { get; } =
    [
        new("libx264", "libx H.264"),
        new("libx265", "libx H.265"),
        new("libvpx-vp9", "VP9 (libvpx)"),
        new("libsvtav1", "AV1 (SVT)"),
    ];

    public static IReadOnlyList<string> Labels(IReadOnlyList<Option> options) =>
        options.Select(o => o.Label).ToList();

    public static string LabelFor(IReadOnlyList<Option> options, string? idOrLabel)
    {
        var key = (idOrLabel ?? "").Trim();
        if (string.IsNullOrEmpty(key))
        {
            return options[0].Label;
        }

        var byId = options.FirstOrDefault(o => o.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId.Label;
        }

        var byLabel = options.FirstOrDefault(o => o.Label.Equals(key, StringComparison.OrdinalIgnoreCase));
        return byLabel?.Label ?? options[0].Label;
    }

    public static string IdFor(IReadOnlyList<Option> options, string? idOrLabel)
    {
        var key = (idOrLabel ?? "").Trim();
        if (string.IsNullOrEmpty(key))
        {
            return options[0].Id;
        }

        var byLabel = options.FirstOrDefault(o => o.Label.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (byLabel is not null)
        {
            return byLabel.Id;
        }

        var byId = options.FirstOrDefault(o => o.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
        return byId?.Id ?? options[0].Id;
    }
}
