using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HailMary.Views.Controls;

/// <summary>Hex color (RRGGBB, no '#') with a swatch button that opens a color picker.</summary>
public sealed partial class HexColorField : UserControl
{
    public static readonly DependencyProperty HexProperty =
        DependencyProperty.Register(
            nameof(Hex),
            typeof(string),
            typeof(HexColorField),
            new PropertyMetadata("FFFFFF", OnHexChanged));

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(object),
            typeof(HexColorField),
            new PropertyMetadata(null, (d, e) => ((HexColorField)d).HexBox.Header = e.NewValue));

    private bool _syncing;

    public HexColorField()
    {
        InitializeComponent();
        ApplyHex(Hex);
    }

    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    private static void OnHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HexColorField)d).ApplyHex(e.NewValue as string);

    private void ApplyHex(string? hex)
    {
        _syncing = true;
        try
        {
            var normalized = Normalize(hex);
            if (!string.Equals(HexBox.Text.TrimStart('#'), normalized ?? hex ?? "", StringComparison.OrdinalIgnoreCase))
            {
                HexBox.Text = hex ?? string.Empty;
            }

            if (normalized is not null)
            {
                var color = ToColor(normalized);
                Swatch.Background = new SolidColorBrush(color);
                Picker.Color = color;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void HexBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        // Only commit complete colors; partial input stays in the box until it is valid.
        var normalized = Normalize(HexBox.Text);
        if (normalized is not null && !string.Equals(normalized, Hex, StringComparison.OrdinalIgnoreCase))
        {
            Hex = normalized;
        }
    }

    private void Picker_OnColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncing)
        {
            return;
        }

        var c = args.NewColor;
        Hex = $"{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static string? Normalize(string? raw)
    {
        var h = (raw ?? string.Empty).Trim().TrimStart('#');
        if (h.Length != 6 || !h.All(Uri.IsHexDigit))
        {
            return null;
        }

        return h.ToUpperInvariant();
    }

    private static Color ToColor(string hex) => Color.FromArgb(
        255,
        Convert.ToByte(hex[..2], 16),
        Convert.ToByte(hex[2..4], 16),
        Convert.ToByte(hex[4..6], 16));
}
