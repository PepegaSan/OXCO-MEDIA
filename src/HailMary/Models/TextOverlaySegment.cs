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
        from = From,
        to = To,
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
    };

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
    };

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
        // Ensure list bindings refresh even when Text/From/To values were unchanged.
        OnPropertyChanged(nameof(ListLabel));
    }
}
