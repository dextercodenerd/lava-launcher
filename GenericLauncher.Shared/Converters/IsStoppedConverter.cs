using System;
using System.Globalization;
using Avalonia.Data.Converters;
using GenericLauncher.Minecraft;

namespace GenericLauncher.Converters;

public class IsStoppedConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MinecraftLauncher.RunningState state)
        {
            return state == MinecraftLauncher.RunningState.Stopped;
        }

        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
