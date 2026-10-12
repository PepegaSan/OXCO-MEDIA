using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace HailMary.Converters;

/// <summary>Binds a NumberBox to a string setting (invariant culture, "," accepted). Empty/NaN → "0".</summary>
public sealed class StringToDoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var text = (value as string ?? string.Empty).Trim().Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        var d = value is double v && !double.IsNaN(v) ? v : 0d;
        return d.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
