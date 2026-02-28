using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace GenericLauncher.Converters;

public class IsLessThanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue && parameter is string paramString && int.TryParse(paramString, out var paramInt))
        {
            return intValue < paramInt;
        }

        if (value is int intValue2 && parameter is int paramInt2)
        {
            return intValue2 < paramInt2;
        }

        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
