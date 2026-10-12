using CommunityToolkit.Mvvm.ComponentModel;
using HailMary.Services;

namespace HailMary.Models;

public partial class TextOverlaySegment : ObservableObject
{
    [ObservableProperty] private string _text = string.Empty;

    [ObservableProperty] private string _from = string.Empty;

    [ObservableProperty] private string _to = string.Empty;

    [ObservableProperty] private int _fontsize = 42;

    [ObservableProperty] private string _color = "FFFFFF";

    [ObservableProperty] private int _posX = 80;

    [ObservableProperty] private int _posY = 80;

    // 0 = automatic text bounds; positive values are a freely resized output box.
    [ObservableProperty] private int _layoutWidth;

    [ObservableProperty] private int _layoutHeight;

    [ObservableProperty] private int _lineSpacing;

    [ObservableProperty] private int _boxBorder = 3;

    [ObservableProperty] private bool _boxEnabled = true;

    [ObservableProperty] private string _fontPath = string.Empty;

    [ObservableProperty] private string _italicFontPath = string.Empty;

    [ObservableProperty] private bool _bold;

    [ObservableProperty] private bool _italic;

    [ObservableProperty] private bool _strike;

    // "left" | "center" | "right" — alignment of lines inside the text block.
    [ObservableProperty] private string _align = "left";

    [ObservableProperty] private int _outlineWidth;

    [ObservableProperty] private string _outlineColor = "000000";

    [ObservableProperty] private bool _shadow;

    [ObservableProperty] private string _boxColor = "000000";

    // Background opacity in percent (0–100).
    [ObservableProperty] private int _boxOpacity = 35;

    [ObservableProperty] private int _boxRadius = 10;

    // "none" | "fade" | "slide" | "pop" — applied at From/To.
    [ObservableProperty] private string _animation = "none";

    [ObservableProperty] private double _animDuration = 0.4;

    public string ListLabel
    {
        get
        {
            var a = TimeFieldHelper.FormatShortFromField(From);
            var b = TimeFieldHelper.FormatShortFromField(To);
            var snippet = (Text ?? string.Empty).Replace('\n', ' ').Trim();
            if (snippet.Length > 44)
            {
                snippet = snippet[..41] + "…";
            }

            return $"{a} → {b}  |  {(string.IsNullOrWhiteSpace(snippet) ? Loc.T("texttovideo.noText") : snippet)}";
        }
    }

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(ListLabel));

    partial void OnFromChanged(string value) => OnPropertyChanged(nameof(ListLabel));

    partial void OnToChanged(string value) => OnPropertyChanged(nameof(ListLabel));

    partial void OnFontsizeChanged(int oldValue, int newValue)
    {
        if (oldValue <= 0 || newValue <= 0 || (LayoutWidth <= 0 && LayoutHeight <= 0))
        {
            return;
        }

        var ratio = newValue / (double)oldValue;
        if (LayoutWidth > 0)
        {
            LayoutWidth = Math.Max(8, (int)Math.Round(LayoutWidth * ratio));
        }

        if (LayoutHeight > 0)
        {
            LayoutHeight = Math.Max(8, (int)Math.Round(LayoutHeight * ratio));
        }
    }

    public object ToJsonObject() => new
    {
        text = Text,
        // Bridges parse plain seconds; "1:30" or "30s" would otherwise be ignored there.
        from = NormalizeTimeField(From),
        to = NormalizeTimeField(To),
        fontsize = Fontsize,
        color = Color,
        px = PosX,
        py = PosY,
        layout_width = LayoutWidth,
        layout_height = LayoutHeight,
        line_spacing = LineSpacing,
        box_border = BoxBorder,
        box_enabled = BoxEnabled ? 1 : 0,
        font_path = FontPath,
        italic_font_path = ItalicFontPath,
        bold = Bold ? 1 : 0,
        italic = Italic ? 1 : 0,
        strike = Strike ? 1 : 0,
        align = Align,
        outline_width = OutlineWidth,
        outline_color = OutlineColor,
        shadow = Shadow ? 1 : 0,
        box_color = BoxColor,
        box_opacity = BoxOpacity,
        box_radius = BoxRadius,
        animation = Animation,
        anim_duration = AnimDuration,
    };

    /// <summary>
    /// Identity of the rendered sprite: every field that changes its pixels.
    /// Position, timing and animation are applied on top and are deliberately excluded.
    /// </summary>
    public string RasterKey => System.Text.Json.JsonSerializer.Serialize(new
    {
        Text,
        Fontsize,
        Color,
        LayoutWidth,
        LayoutHeight,
        LineSpacing,
        BoxBorder,
        BoxEnabled,
        FontPath,
        ItalicFontPath,
        Bold,
        Italic,
        Strike,
        Align,
        OutlineWidth,
        OutlineColor,
        Shadow,
        BoxColor,
        BoxOpacity,
        BoxRadius,
    });

    public static TextOverlaySegment FromJson(System.Text.Json.JsonElement el) => new()
    {
        Text = el.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
        From = el.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "",
        To = el.TryGetProperty("to", out var to) ? to.GetString() ?? "" : "",
        Fontsize = el.TryGetProperty("fontsize", out var fs) ? fs.GetInt32() : 42,
        Color = el.TryGetProperty("color", out var c) ? (c.GetString() ?? "FFFFFF").Trim().TrimStart('#') : "FFFFFF",
        PosX = el.TryGetProperty("px", out var px) ? px.GetInt32() : 80,
        PosY = el.TryGetProperty("py", out var py) ? py.GetInt32() : 80,
        LayoutWidth = el.TryGetProperty("layout_width", out var lw) ? Math.Max(0, lw.GetInt32()) : 0,
        LayoutHeight = el.TryGetProperty("layout_height", out var lh) ? Math.Max(0, lh.GetInt32()) : 0,
        LineSpacing = el.TryGetProperty("line_spacing", out var ls) ? ls.GetInt32() : 0,
        BoxBorder = el.TryGetProperty("box_border", out var bb) ? bb.GetInt32() : 3,
        BoxEnabled = !el.TryGetProperty("box_enabled", out var be) || be.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => be.GetInt32() != 0,
        },
        FontPath = el.TryGetProperty("font_path", out var fp) ? fp.GetString() ?? "" : "",
        ItalicFontPath = el.TryGetProperty("italic_font_path", out var ifp) ? ifp.GetString() ?? "" : "",
        Bold = el.TryGetProperty("bold", out var b) && ReadBool(b),
        Italic = el.TryGetProperty("italic", out var i) && ReadBool(i),
        Strike = el.TryGetProperty("strike", out var s) && ReadBool(s),
        Align = el.TryGetProperty("align", out var al) ? al.GetString() ?? "left" : "left",
        OutlineWidth = el.TryGetProperty("outline_width", out var ow) ? ow.GetInt32() : 0,
        OutlineColor = el.TryGetProperty("outline_color", out var oc) ? (oc.GetString() ?? "000000").Trim().TrimStart('#') : "000000",
        Shadow = el.TryGetProperty("shadow", out var sh) && ReadBool(sh),
        BoxColor = el.TryGetProperty("box_color", out var bc) ? (bc.GetString() ?? "000000").Trim().TrimStart('#') : "000000",
        BoxOpacity = el.TryGetProperty("box_opacity", out var bo) ? bo.GetInt32() : 35,
        BoxRadius = el.TryGetProperty("box_radius", out var br) ? br.GetInt32() : 10,
        Animation = el.TryGetProperty("animation", out var an) ? an.GetString() ?? "none" : "none",
        AnimDuration = el.TryGetProperty("anim_duration", out var ad) ? ad.GetDouble() : 0.4,
    };

    private static string NormalizeTimeField(string raw) =>
        TimeFieldHelper.TryParseSeconds(raw, out var sec) ? TimeFieldHelper.FormatForField(sec) : raw;

    private static bool ReadBool(System.Text.Json.JsonElement el) => el.ValueKind switch
    {
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        _ => el.TryGetInt32(out var n) && n != 0,
    };

    public TextOverlaySegment Clone() => new()
    {
        Text = Text,
        From = From,
        To = To,
        Fontsize = Fontsize,
        Color = Color,
        PosX = PosX,
        PosY = PosY,
        LayoutWidth = LayoutWidth,
        LayoutHeight = LayoutHeight,
        LineSpacing = LineSpacing,
        BoxBorder = BoxBorder,
        BoxEnabled = BoxEnabled,
        FontPath = FontPath,
        ItalicFontPath = ItalicFontPath,
        Bold = Bold,
        Italic = Italic,
        Strike = Strike,
        Align = Align,
        OutlineWidth = OutlineWidth,
        OutlineColor = OutlineColor,
        Shadow = Shadow,
        BoxColor = BoxColor,
        BoxOpacity = BoxOpacity,
        BoxRadius = BoxRadius,
        Animation = Animation,
        AnimDuration = AnimDuration,
    };

    public void ApplyFrom(TextOverlaySegment source)
    {
        Text = source.Text;
        From = source.From;
        To = source.To;
        Fontsize = source.Fontsize;
        Color = source.Color;
        PosX = source.PosX;
        PosY = source.PosY;
        LayoutWidth = source.LayoutWidth;
        LayoutHeight = source.LayoutHeight;
        LineSpacing = source.LineSpacing;
        BoxBorder = source.BoxBorder;
        BoxEnabled = source.BoxEnabled;
        FontPath = source.FontPath;
        ItalicFontPath = source.ItalicFontPath;
        Bold = source.Bold;
        Italic = source.Italic;
        Strike = source.Strike;
        Align = source.Align;
        OutlineWidth = source.OutlineWidth;
        OutlineColor = source.OutlineColor;
        Shadow = source.Shadow;
        BoxColor = source.BoxColor;
        BoxOpacity = source.BoxOpacity;
        BoxRadius = source.BoxRadius;
        Animation = source.Animation;
        AnimDuration = source.AnimDuration;
        // Ensure list bindings refresh even when Text/From/To values were unchanged.
        OnPropertyChanged(nameof(ListLabel));
    }
}
