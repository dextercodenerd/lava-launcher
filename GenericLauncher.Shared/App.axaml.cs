using System.Collections.Generic;
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
using GenericLauncher.Database.Model;
using GenericLauncher.Http;
using GenericLauncher.InstanceMods;
using GenericLauncher.Java;
using GenericLauncher.Minecraft;
using GenericLauncher.Minecraft.ModLoaders;
using GenericLauncher.Minecraft.ModLoaders.Fabric;
using GenericLauncher.Minecraft.ModLoaders.Forge;
using GenericLauncher.Minecraft.ModLoaders.NeoForge;
using GenericLauncher.Minecraft.ModLoaders.Vanilla;
using GenericLauncher.Misc;
using GenericLauncher.Modrinth;
using LavaLauncher;
using Microsoft.Extensions.Logging;

namespace GenericLauncher;

public partial class App : Application
{
    public static ILoggerFactory? LoggerFactory;
    private static readonly LauncherPlatform Platform = LauncherPlatform.CreateCurrent();

    private static readonly string BaseMinecraftLibrariesFolder = "libraries";
    private static readonly string BaseMcFolder = "mc";
    private static readonly string BaseModLoadersFolder = "modloaders";
    private static readonly string FabricModLoaderFolder = "fabric";
    private static readonly string NeoForgeModLoaderFolder = "neoforge";
    private static readonly string ForgeModLoaderFolder = "forge";

    // Manual DI, no runtime magic, ever!
    private static readonly HttpClient HttpClient = HttpRetry.CreateHttpClient(4);
    private static readonly FileDownloader FileDownloader = new(HttpClient);

    private readonly Authenticator _authenticator =
        new(AzureConfig.ClientId,
            AzureConfig.RedirectUrl,
            HttpClient,
            LoggerFactory?.CreateLogger(typeof(Authenticator)));

    private readonly MinecraftVersionManager _minecraftVersionManager =
        new(Platform,
            HttpClient,
            FileDownloader,
            LoggerFactory?.CreateLogger(typeof(MinecraftVersionManager)));

    private readonly JavaVersionManager _javaVersionManager =
        new(Platform,
            HttpClient,
            FileDownloader,
            LoggerFactory?.CreateLogger(typeof(JavaVersionManager)));

    private readonly FabricModLoaderService _fabricModLoaderService =
        new(Path.Combine(Platform.AppDataPath, BaseMcFolder, BaseModLoadersFolder, FabricModLoaderFolder),
            Path.Combine(Platform.AppDataPath, BaseMcFolder, BaseModLoadersFolder, FabricModLoaderFolder,
                BaseMinecraftLibrariesFolder),
            HttpClient,
            FileDownloader,
            LoggerFactory?.CreateLogger(typeof(FabricModLoaderService)));

    private readonly NeoForgeModLoaderService _neoForgeModLoaderService =
        new(Path.Combine(Platform.AppDataPath, BaseMcFolder, BaseModLoadersFolder, NeoForgeModLoaderFolder),
            Path.Combine(Platform.AppDataPath, BaseMcFolder, BaseModLoadersFolder, NeoForgeModLoaderFolder,
                BaseMinecraftLibrariesFolder),
            HttpClient,
            FileDownloader,
            LoggerFactory?.CreateLogger(typeof(NeoForgeModLoaderService)));

    private readonly ForgeModLoaderService _forgeModLoaderService =
        new(Path.Combine(Platform.AppDataPath, BaseMcFolder, BaseModLoadersFolder, ForgeModLoaderFolder),
            Path.Combine(Platform.AppDataPath, BaseMcFolder, BaseModLoadersFolder, ForgeModLoaderFolder,
                BaseMinecraftLibrariesFolder),
            HttpClient,
            FileDownloader,
            LoggerFactory?.CreateLogger(typeof(ForgeModLoaderService)));

    private readonly VanillaModLoaderService _vanillaModLoaderService =
        new();

    private readonly LauncherRepository _launcherRepository = new(Platform);
    private readonly AuthService _authService;
    private readonly MinecraftLauncher _minecraftLauncher;
    private readonly InstanceModsManager _instanceModsManager;

    private readonly ModrinthApiClient _modrinthApiClient =
        new(HttpClient, LoggerFactory?.CreateLogger(typeof(ModrinthApiClient)));

    public App()
    {
        _authService = new AuthService(
            _authenticator,
            _launcherRepository,
            LoggerFactory?.CreateLogger(typeof(AuthService)));

        _instanceModsManager = new InstanceModsManager(
            Platform,
            _modrinthApiClient,
            FileDownloader,
            () => _minecraftLauncher!.Instances,
            LoggerFactory?.CreateLogger(typeof(InstanceModsManager)));

        _minecraftLauncher = new MinecraftLauncher(
            Platform,
            Product.Name,
            Product.Version,
            _launcherRepository,
            _minecraftVersionManager,
            _javaVersionManager,
            _instanceModsManager,
            new Dictionary<MinecraftInstanceModLoader, IModLoaderService>
            {
                [MinecraftInstanceModLoader.Vanilla] = _vanillaModLoaderService,
                [MinecraftInstanceModLoader.Fabric] = _fabricModLoaderService,
                [MinecraftInstanceModLoader.NeoForge] = _neoForgeModLoaderService,
                [MinecraftInstanceModLoader.Forge] = _forgeModLoaderService,
            },
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
                new ApplicationViewModel(_authService, _minecraftLauncher, _modrinthApiClient, _instanceModsManager);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
