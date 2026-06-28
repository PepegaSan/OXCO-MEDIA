using HailMary.Models;

namespace HailMary.Services;

public sealed class TextOverlayStylePreset
{
    public required string Id { get; init; }

    public required string LabelKey { get; init; }

    public string Label => Loc.T(LabelKey);

    public required Action<TextOverlaySegment, int, int> Apply { get; init; }
}

public static class TextOverlayStylePresets
{
    public static IReadOnlyList<TextOverlayStylePreset> All { get; } =
    [
        new TextOverlayStylePreset
        {
            Id = "subtitle_bottom",
            LabelKey = "texttovideo.preset.subtitleBottom",
            Apply = (seg, w, h) =>
            {
                seg.Fontsize = 36;
                seg.Color = "FFFFFF";
                seg.BoxEnabled = true;
                seg.BoxBorder = 4;
                seg.LineSpacing = -10;
                seg.Bold = false;
                seg.Italic = false;
                seg.Strike = false;
                seg.PosX = Math.Max(40, w / 10);
                seg.PosY = Math.Max(40, (int)(h * 0.82));
            },
        },
        new TextOverlayStylePreset
        {
            Id = "title_top",
            LabelKey = "texttovideo.preset.titleTop",
            Apply = (seg, w, h) =>
            {
                seg.Fontsize = 56;
                seg.Color = "FFFFFF";
                seg.BoxEnabled = true;
                seg.BoxBorder = 3;
                seg.LineSpacing = -8;
                seg.Bold = true;
                seg.Italic = false;
                seg.Strike = false;
                seg.PosX = Math.Max(40, w / 10);
                seg.PosY = Math.Max(24, (int)(h * 0.06));
            },
        },
        new TextOverlayStylePreset
        {
            Id = "watermark",
            LabelKey = "texttovideo.preset.watermark",
            Apply = (seg, w, h) =>
            {
                seg.Fontsize = 24;
                seg.Color = "CCCCCC";
                seg.BoxEnabled = false;
                seg.BoxBorder = 0;
                seg.LineSpacing = -6;
                seg.Bold = false;
                seg.Italic = false;
                seg.Strike = false;
                seg.PosX = Math.Max(20, (int)(w * 0.72));
                seg.PosY = Math.Max(20, (int)(h * 0.90));
            },
        },
        new TextOverlayStylePreset
        {
            Id = "center_large",
            LabelKey = "texttovideo.preset.centerLarge",
            Apply = (seg, w, h) =>
            {
                seg.Fontsize = 48;
                seg.Color = "FFFFFF";
                seg.BoxEnabled = true;
                seg.BoxBorder = 5;
                seg.LineSpacing = -12;
                seg.Bold = true;
                seg.Italic = false;
                seg.Strike = false;
                seg.PosX = Math.Max(40, w / 8);
                seg.PosY = Math.Max(40, (int)(h * 0.42));
            },
        },
    ];

    public static TextOverlayStylePreset? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
