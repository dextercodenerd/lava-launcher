using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;
using GenericLauncher.Database.Model;
using GenericLauncher.Minecraft;
using GenericLauncher.Minecraft.Json;
using GenericLauncher.Minecraft.ModLoaders;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Screens.NewInstanceDialog;

public partial class NewInstanceDialogViewModel : ViewModelBase
{
    private readonly ILogger? _logger;
    private readonly IMinecraftLauncherFacade? _minecraftLauncher;
    private int _modLoaderVersionsRequestId;

    [ObservableProperty] private bool _showNewMinecraftInstanceDialog = false;
    [ObservableProperty] private bool _canCloseOnClickAway = true;

    [ObservableProperty] private ImmutableList<VersionInfo> _availableMinecraftVersions = [];
    [ObservableProperty] private VersionInfo? _selectedMinecraftVersion = null;
    [ObservableProperty] private ImmutableList<MinecraftInstanceModLoader> _availableModLoaders = [];
    [ObservableProperty] private MinecraftInstanceModLoader _selectedModLoader = MinecraftInstanceModLoader.Vanilla;
    [ObservableProperty] private ImmutableList<ModLoaderVersionInfo> _availableModLoaderVersions = [];
    [ObservableProperty] private ModLoaderVersionInfo? _selectedModLoaderVersion = null;
    [ObservableProperty] private string? _newInstanceName = null;
    [ObservableProperty] private bool _preparingInstance = false;
    [ObservableProperty] private bool _loadingModLoaderVersions = false;
    [ObservableProperty] private string _modLoaderVersionStatusText = "";
    [ObservableProperty] private string _instanceNameWatermark = "Vanilla";

    public bool CanInstall =>
        !PreparingInstance
        && !LoadingModLoaderVersions
        && SelectedMinecraftVersion is not null
        && SelectedModLoaderVersion is not null;

    public DialogOpenedEventHandler DialogOpenedHandler { get; }
    public DialogClosingEventHandler DialogClosingHandler { get; }

    public NewInstanceDialogViewModel() : this(null)
    {
    }

    public NewInstanceDialogViewModel(
        IMinecraftLauncherFacade? minecraftLauncher = null,
        ILogger? logger = null)
    {
        _logger = logger;
        _minecraftLauncher = minecraftLauncher;

        // This is the way, how we can bind DialogHost's DialogOpenedCallback and DialogClosingCallback,
        // which are not ICommand types, but event handlers.
        DialogOpenedHandler = OnDialogOpened;
        DialogClosingHandler = OnDialogClosing;

        if (_minecraftLauncher is null)
        {
            return;
        }

        AvailableMinecraftVersions = _minecraftLauncher.AvailableVersions;
        AvailableModLoaders = _minecraftLauncher.AvailableModLoaders;
        SelectedMinecraftVersion = AvailableMinecraftVersions.FirstOrDefault();
        SelectedModLoader = AvailableModLoaders.FirstOrDefault();
        _minecraftLauncher.AvailableVersionsChanged += OnAvailableVersionsChanged;
    }

    private void OnDialogOpened(object sender, DialogOpenedEventArgs args)
    {
        // When displaying the dialog, we must always pre-select the first item. This is because
        // when the dialog is hidden, it detaches the items source and triggers selected item
        // update to null. So, the next time the dialog was opened, nothing was preselected.
        SelectedMinecraftVersion = AvailableMinecraftVersions.FirstOrDefault();
        if (AvailableModLoaders.Count > 0)
        {
            SelectedModLoader = AvailableModLoaders[0];
        }
    }

    private void OnDialogClosing(object sender, DialogClosingEventArgs args)
    {
        CanCloseOnClickAway = true;
        NewInstanceName = null;
        PreparingInstance = false;
        LoadingModLoaderVersions = false;
    }

    [RelayCommand]
    private void OnClickClose()
    {
        ShowNewMinecraftInstanceDialog = false;
    }

    [RelayCommand]
    private void OnClickInstall()
    {
        if (_minecraftLauncher is null || !CanInstall)
        {
            return;
        }

        _logger?.LogInformation("Installing new Minecraft instance: {Version}", SelectedMinecraftVersion);
        var versionInfo = SelectedMinecraftVersion;

        // Displaying "Vanilla" name, instead of the MC version is less confusing when upgrading
        // the instance's base MC version. We would have to rename the instance or what?
        var name = NewInstanceName ?? InstanceNameWatermark;
        var modLoader = SelectedModLoader;
        var preferredModLoaderVersion = SelectedModLoaderVersion?.VersionId;

        NewInstanceName = null;

        if (versionInfo is null)
        {
            return;
        }

        CanCloseOnClickAway = false;
        PreparingInstance = true;

        Task.Run(async () =>
            {
                await _minecraftLauncher.CreateInstance(versionInfo,
                    name,
                    modLoader,
                    preferredModLoaderVersion,
                    new Progress<ThreadSafeInstallProgressReporter.InstallProgress>(p =>
                    {
                        if (!p.IsValidMinecraftVersion)
                        {
                            return;
                        }

                        Dispatcher.UIThread.Post(() =>
                        {
                            if (CanCloseOnClickAway)
                            {
                                return;
                            }

                            CanCloseOnClickAway = true;
                            ShowNewMinecraftInstanceDialog = false;
                        });
                    }));

                _logger?.LogInformation("Installed");
            })
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger?.LogError(t.Exception, "Problem installing instance");

                    // TODO: show error
                }

                Dispatcher.UIThread.Post(() =>
                {
                    PreparingInstance = false;
                    CanCloseOnClickAway = true;
                });
            });
    }

    private void OnAvailableVersionsChanged(object? sender, EventArgs e)
    {
        if (_minecraftLauncher is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            AvailableMinecraftVersions = _minecraftLauncher.AvailableVersions;
            SelectedMinecraftVersion = AvailableMinecraftVersions.FirstOrDefault();
        });
    }

    partial void OnSelectedModLoaderChanged(MinecraftInstanceModLoader value)
    {
        InstanceNameWatermark = value switch
        {
            MinecraftInstanceModLoader.Fabric => "Fabric",
            MinecraftInstanceModLoader.NeoForge => "NeoForge",
            MinecraftInstanceModLoader.Forge => "Forge",
            _ => "Vanilla",
        };

        _ = LoadSelectedModLoaderVersionsAsync(false);
        OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnSelectedMinecraftVersionChanged(VersionInfo? value)
    {
        _ = LoadSelectedModLoaderVersionsAsync(false);
        OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnSelectedModLoaderVersionChanged(ModLoaderVersionInfo? value)
    {
        OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnPreparingInstanceChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnLoadingModLoaderVersionsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
    }

    private async Task LoadSelectedModLoaderVersionsAsync(bool reload)
    {
        if (_minecraftLauncher is null || SelectedMinecraftVersion is null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                AvailableModLoaderVersions = [];
                SelectedModLoaderVersion = null;
                ModLoaderVersionStatusText = "";
                LoadingModLoaderVersions = false;
            });
            return;
        }

        var currentRequestId = ++_modLoaderVersionsRequestId;
        var modLoader = SelectedModLoader;
        var minecraftVersionId = SelectedMinecraftVersion.Id;
        var preferredVersionId = SelectedModLoaderVersion?.VersionId;

        Dispatcher.UIThread.Post(() =>
        {
            LoadingModLoaderVersions = true;
            ModLoaderVersionStatusText = "Loading compatible loader versions...";
        });

        try
        {
            var versions = await _minecraftLauncher.GetLoaderVersionsAsync(modLoader, minecraftVersionId, reload);
            Dispatcher.UIThread.Post(() =>
            {
                if (currentRequestId != _modLoaderVersionsRequestId)
                {
                    return;
                }

                AvailableModLoaderVersions = versions;
                SelectedModLoaderVersion =
                    AvailableModLoaderVersions.FirstOrDefault(v => v.VersionId == preferredVersionId)
                    ?? AvailableModLoaderVersions.FirstOrDefault();
                ModLoaderVersionStatusText = AvailableModLoaderVersions.Count == 0
                    ? "No compatible loader versions for the selected Minecraft version."
                    : "";
                LoadingModLoaderVersions = false;
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Problem loading mod loader versions for {ModLoader} and Minecraft {MinecraftVersion}",
                modLoader,
                minecraftVersionId);

            Dispatcher.UIThread.Post(() =>
            {
                if (currentRequestId != _modLoaderVersionsRequestId)
                {
                    return;
                }

                AvailableModLoaderVersions = [];
                SelectedModLoaderVersion = null;
                ModLoaderVersionStatusText = "Failed to load compatible loader versions.";
                LoadingModLoaderVersions = false;
            });
        }
    }

    private async Task LoadModLoaderVersionsAsync(MinecraftInstanceModLoader modLoader, string minecraftVersionId,
        bool reload)
    {
        if (_minecraftLauncher is null)
        {
            return;
        }

        try
        {
            var versions = await _minecraftLauncher.GetLoaderVersionsAsync(modLoader, minecraftVersionId, reload);
            Dispatcher.UIThread.Post(() =>
            {
                AvailableModLoaderVersions = versions;
                SelectedModLoaderVersion = AvailableModLoaderVersions.FirstOrDefault();
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Problem loading mod loader versions for {ModLoader}", modLoader);
            Dispatcher.UIThread.Post(() =>
            {
                AvailableModLoaderVersions = [];
                SelectedModLoaderVersion = null;
            });
        }
    }
}
