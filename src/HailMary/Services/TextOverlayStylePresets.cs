using HailMary.Models;

namespace HailMary.Services;

public sealed class TextOverlayStylePreset
{
    public required string Id { get; init; }

    public required string LabelKey { get; init; }

    public string Label => Loc.T(LabelKey);

    public required Action<TextOverlaySegment, int, int> Apply { get; init; }

    /// <summary>Center the rendered box horizontally once its real width is known.</summary>
    public bool CenterHorizontally { get; init; }

    /// <summary>Center the rendered box vertically once its real height is known.</summary>
    public bool CenterVertically { get; init; }
}

public static class TextOverlayStylePresets
{
    public static IReadOnlyList<TextOverlayStylePreset> All { get; } =
    [
        new TextOverlayStylePreset
        {
            Id = "subtitle_bottom",
            LabelKey = "texttovideo.preset.subtitleBottom",
            CenterHorizontally = true,
            Apply = (seg, w, h) =>
            {
                Reset(seg);
                seg.Fontsize = 40;
                seg.LineSpacing = -6;
                seg.Align = "center";
                seg.BoxEnabled = true;
                seg.BoxOpacity = 55;
                seg.BoxBorder = 2;
                seg.Animation = "fade";
                seg.AnimDuration = 0.25;
                // Fallback until the real width is known and the box gets centered.
                seg.PosX = Math.Max(40, w / 10);
                seg.PosY = Math.Max(40, (int)(h * 0.82));
            },
        },
        new TextOverlayStylePreset
        {
            Id = "title_top",
            LabelKey = "texttovideo.preset.titleTop",
            CenterHorizontally = true,
            Apply = (seg, w, h) =>
            {
                Reset(seg);
                seg.Fontsize = 64;
                seg.LineSpacing = -8;
                seg.Bold = true;
                seg.Align = "center";
                seg.BoxEnabled = false;
                seg.Shadow = true;
                seg.Animation = "slide";
                seg.AnimDuration = 0.5;
                // Fallback until the real width is known and the box gets centered.
                seg.PosX = Math.Max(40, w / 10);
                seg.PosY = Math.Max(24, (int)(h * 0.07));
            },
        },
        new TextOverlayStylePreset
        {
            Id = "social_caption",
            LabelKey = "texttovideo.preset.social",
            CenterHorizontally = true,
            Apply = (seg, w, h) =>
            {
                Reset(seg);
                seg.Fontsize = 56;
                seg.LineSpacing = -6;
                seg.Bold = true;
                seg.Align = "center";
                seg.BoxEnabled = false;
                seg.OutlineWidth = 5;
                seg.Shadow = true;
                seg.Animation = "pop";
                seg.AnimDuration = 0.3;
                // Fallback until the real width is known and the box gets centered.
                seg.PosX = Math.Max(40, w / 10);
                seg.PosY = Math.Max(40, (int)(h * 0.62));
            },
        },
        new TextOverlayStylePreset
        {
            Id = "lower_third",
            LabelKey = "texttovideo.preset.lowerThird",
            Apply = (seg, w, h) =>
            {
                Reset(seg);
                seg.Fontsize = 38;
                seg.LineSpacing = -4;
                seg.Bold = true;
                seg.BoxEnabled = true;
                seg.BoxColor = "1E88E5";
                seg.BoxOpacity = 92;
                seg.BoxRadius = 6;
                seg.BoxBorder = 4;
                seg.Animation = "slide";
                seg.AnimDuration = 0.45;
                seg.PosX = Math.Max(24, (int)(w * 0.06));
                seg.PosY = Math.Max(40, (int)(h * 0.76));
            },
        },
        new TextOverlayStylePreset
        {
            Id = "center_large",
            LabelKey = "texttovideo.preset.centerLarge",
            CenterHorizontally = true,
            CenterVertically = true,
            Apply = (seg, w, h) =>
            {
                Reset(seg);
                seg.Fontsize = 52;
                seg.LineSpacing = -10;
                seg.Bold = true;
                seg.Align = "center";
                seg.BoxEnabled = true;
                seg.BoxOpacity = 45;
                seg.BoxRadius = 16;
                seg.BoxBorder = 6;
                seg.Animation = "pop";
                seg.AnimDuration = 0.35;
                // Fallback until the real width is known and the box gets centered.
                seg.PosX = Math.Max(40, w / 10);
                seg.PosY = Math.Max(40, (int)(h * 0.42));
            },
        },
        new TextOverlayStylePreset
        {
            Id = "watermark",
            LabelKey = "texttovideo.preset.watermark",
            Apply = (seg, w, h) =>
            {
                Reset(seg);
                seg.Fontsize = 24;
                seg.Color = "E0E0E0";
                seg.LineSpacing = -6;
                seg.BoxEnabled = false;
                seg.Shadow = true;
                seg.PosX = Math.Max(20, (int)(w * 0.74));
                seg.PosY = Math.Max(20, (int)(h * 0.91));
            },
        },
    ];

    public static TextOverlayStylePreset? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Neutral look so a preset never inherits stray effects from the previous style.</summary>
    private static void Reset(TextOverlaySegment seg)
    {
        seg.Color = "FFFFFF";
        seg.Bold = false;
        seg.Italic = false;
        seg.Strike = false;
        seg.Align = "left";
        seg.OutlineWidth = 0;
        seg.OutlineColor = "000000";
        seg.Shadow = false;
        seg.BoxEnabled = false;
        seg.BoxColor = "000000";
        seg.BoxOpacity = 35;
        seg.BoxRadius = 10;
        seg.BoxBorder = 3;
        seg.LayoutWidth = 0;
        seg.LayoutHeight = 0;
        seg.Animation = "none";
        seg.AnimDuration = 0.4;
    }
}
