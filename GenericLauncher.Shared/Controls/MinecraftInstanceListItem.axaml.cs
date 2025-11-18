using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;

namespace GenericLauncher.Controls;

public class MinecraftInstanceListItem : TemplatedControl
{
    public static readonly StyledProperty<ICommand> ClickPlayCommandProperty =
        AvaloniaProperty.Register<MinecraftInstanceListItem, ICommand>(
            nameof(ClickPlayCommand));

    public ICommand ClickPlayCommand
    {
        get => GetValue(ClickPlayCommandProperty);
        set => SetValue(ClickPlayCommandProperty, value);
    }
}
