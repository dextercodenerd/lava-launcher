using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace GenericLauncher.Converters;

public class JoinStringsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string[] strings)
        {
            return string.Join(", ", strings);
        }

        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
