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
        if (hex.Length != 6)
        {
            return null;
        }

        try
        {
            var r = byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
            var g = byte.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
            var b = byte.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);
            return new SolidColorBrush(Color.FromArgb(255, r, g, b));
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
