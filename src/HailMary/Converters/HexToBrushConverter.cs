using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HailMary.Converters;

public sealed class HexToBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        hex = hex.TrimStart('#');
        if (hex.Length is not (6 or 8))
        {
            return null;
        }

        try
        {
            // #RRGGBB or #AARRGGBB
            var a = hex.Length == 8 ? byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber) : (byte)255;
            var rgb = hex.Length == 8 ? hex[2..] : hex;
            var r = byte.Parse(rgb[..2], System.Globalization.NumberStyles.HexNumber);
            var g = byte.Parse(rgb[2..4], System.Globalization.NumberStyles.HexNumber);
            var b = byte.Parse(rgb[4..6], System.Globalization.NumberStyles.HexNumber);
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
