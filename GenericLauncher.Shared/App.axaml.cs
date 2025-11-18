using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using GenericLauncher.Auth;
using GenericLauncher.Database;
using GenericLauncher.Http;
using GenericLauncher.Java;
using GenericLauncher.Minecraft;
using GenericLauncher.Misc;
using LavaLauncher;
using Microsoft.Extensions.Logging;

namespace GenericLauncher;

public partial class App : Application
{
    public static ILoggerFactory? LoggerFactory;

    // TODO: Inject the sub-folder/assembly name via constructor?
    private static readonly string BaseFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Product.AssemblyName);

    private static readonly string BaseMinecraftInstallFolder = "mc";
    private static readonly string BaseJavaInstallFolder = "java";
    private static readonly string BaseInstancesFolder = "instances";

    // Manual DI, no runtime magic, ever!
    private static readonly HttpClient HttpClient = HttpRetry.CreateHttpClient(4);
    private static readonly FileDownloader FileDownloader = new(HttpClient);

    private readonly Authenticator _authenticator =
        new(AzureConfig.ClientId,
            AzureConfig.RedirectUrl,
            HttpClient,
            LoggerFactory?.CreateLogger(typeof(Authenticator)));

    private readonly MinecraftVersionManager _minecraftVersionManager =
        new(Path.Combine(BaseFolder, BaseMinecraftInstallFolder),
            HttpClient,
            FileDownloader,
            LoggerFactory?.CreateLogger(typeof(MinecraftVersionManager)));

    private readonly JavaVersionManager _javaVersionManager =
        new(Path.Combine(BaseFolder, BaseJavaInstallFolder),
            HttpClient,
            FileDownloader,
            LoggerFactory?.CreateLogger(typeof(JavaVersionManager)));

    private readonly LauncherRepository _launcherRepository = new(BaseFolder);
    private readonly AuthService _authService;
    private readonly MinecraftLauncher _minecraftLauncher;

    public App()
    {
        _authService = new AuthService(
            _authenticator,
            _launcherRepository,
            LoggerFactory?.CreateLogger(typeof(AuthService)));

        _minecraftLauncher = new MinecraftLauncher(
            Product.CurrentOs,
            Product.Name,
            Product.Version,
            Path.Combine(BaseFolder, BaseInstancesFolder),
            _launcherRepository,
            _minecraftVersionManager,
            _javaVersionManager,
            LoggerFactory?.CreateLogger(typeof(MinecraftLauncher)));
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // We can safely disable the IL2026 warning about reflection, because when AOT compiling, the
    // reflection features are not used in `BindingPlugins`. We don't use just #pragma around the
    // code, because `dotnet publish` will complain with IL2026 during "trimmed" AOT linking.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026")]
    public override void OnFrameworkInitializationCompleted()
    {
        // We are using `CommunityToolkit.Mvvm` (CT), so we must remove Avalonia's `DataAnnotationsValidationPlugin`,
        // which relies on the `INotifyDataErrorInfo/IndeiValidationPlugin`. Without this we will get duplicate
        // validations from both Avalonia and CT.
        // https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            DataContext =
                new ApplicationViewModel(_authService, _minecraftLauncher);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
