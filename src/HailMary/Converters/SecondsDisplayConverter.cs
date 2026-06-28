using HailMary.Services;
using Microsoft.UI.Xaml.Data;

namespace HailMary.Converters;

public sealed class SecondsDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            double sec => TimecodeHelper.FormatDisplay(sec),
            float f => TimecodeHelper.FormatDisplay(f),
            int i => TimecodeHelper.FormatDisplay(i),
            _ => value?.ToString() ?? string.Empty,
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
