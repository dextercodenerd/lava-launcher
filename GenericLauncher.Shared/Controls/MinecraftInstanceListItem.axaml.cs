using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace GenericLauncher.Controls;

public class MinecraftInstanceListItem : TemplatedControl
{
    public static readonly StyledProperty<ICommand> ClickCommandProperty =
        AvaloniaProperty.Register<MinecraftInstanceListItem, ICommand>(
            nameof(ClickCommand));

    public ICommand ClickCommand
    {
        get => GetValue(ClickCommandProperty);
        set => SetValue(ClickCommandProperty, value);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (e.Handled || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (ClickCommand.CanExecute(DataContext))
        {
            ClickCommand.Execute(DataContext);
            e.Handled = true;
        }
    }
}