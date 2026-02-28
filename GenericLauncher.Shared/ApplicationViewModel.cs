using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using GenericLauncher.Auth;
using GenericLauncher.Minecraft;
using GenericLauncher.Modrinth;
using GenericLauncher.Screens.MainWindow;

namespace GenericLauncher;

public class ApplicationViewModel : ViewModelBase
{
    private readonly MainWindow _mainWindow;
    private readonly MainWindowViewModel _mainWindowViewModel;

    private readonly AuthService _auth;
    private readonly MinecraftLauncher _minecraftLauncher;
    private readonly ModrinthApiClient _modrinthApiClient;

    public ApplicationViewModel(
        AuthService authService,
        MinecraftLauncher minecraftLauncher,
        ModrinthApiClient modrinthApiClient)
    {
        _auth = authService;
        _minecraftLauncher = minecraftLauncher;
        _modrinthApiClient = modrinthApiClient;

        _mainWindowViewModel =
            new MainWindowViewModel(
                _auth,
                _minecraftLauncher,
                _modrinthApiClient,
                // TODO: inject the logger factory into here?
                App.LoggerFactory?.CreateLogger(typeof(MainWindowViewModel).FullName ?? ""));
        _mainWindow = new MainWindow
        {
            DataContext = _mainWindowViewModel,
            WindowState = WindowState.Normal
        };

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime application)
        {
            return;
        }

        // TODO: Maybe we will need a splash screen in the future, for fast GUI start-up. In that
        //  case, create a SplashScreen window, set it the MainWindow, and then after all is loaded,
        //  switch the MainWindow to our _mainwindow.
        application.MainWindow = _mainWindow;
        _mainWindow.Show();
    }
}
