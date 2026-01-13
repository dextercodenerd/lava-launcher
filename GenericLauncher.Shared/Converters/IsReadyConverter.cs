using System;
using System.Globalization;
using Avalonia.Data.Converters;
using GenericLauncher.Database.Model;

namespace GenericLauncher.Converters;

public class IsReadyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MinecraftInstanceState state)
        {
            return state == MinecraftInstanceState.Ready;
        }

        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
