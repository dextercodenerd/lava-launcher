using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;
using GenericLauncher.Minecraft;
using GenericLauncher.Minecraft.Json;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Screens.NewInstanceDialog;

public partial class NewInstanceDialogViewModel : ViewModelBase
{
    private readonly ILogger? _logger;
    private readonly MinecraftLauncher? _minecraftLauncher;

    [ObservableProperty] private bool _showNewMinecraftInstanceDialog = false;
    [ObservableProperty] private bool _canCloseOnClickAway = true;

    [ObservableProperty] private ImmutableList<VersionInfo> _availableMinecraftVersions = [];
    [ObservableProperty] private VersionInfo? _selectedMinecraftVersion = null;
    [ObservableProperty] private string? _newInstanceName = null;
    [ObservableProperty] private bool _preparingInstance = false;
    [ObservableProperty] private string _instanceNameWatermark = "Vanilla"; // prepared for mod launchers e.g., Fabric

    public DialogOpenedEventHandler DialogOpenedHandler { get; }
    public DialogClosingEventHandler DialogClosingHandler { get; }

    public NewInstanceDialogViewModel() : this(null)
    {
    }

    public NewInstanceDialogViewModel(
        MinecraftLauncher? minecraftLauncher = null,
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
        _minecraftLauncher.AvailableVersionsChanged += OnAvailableVersionsChanged;
    }

    private void OnDialogOpened(object sender, DialogOpenedEventArgs args)
    {
        // When displaying the dialog, we must always pre-select the first item. This is because
        // when the dialog is hidden, it detaches the items source and triggers selected item
        // update to null. So, the next time the dialog was opened, nothing was preselected.
        SelectedMinecraftVersion = AvailableMinecraftVersions.FirstOrDefault();
    }

    private void OnDialogClosing(object sender, DialogClosingEventArgs args)
    {
        CanCloseOnClickAway = true;
        NewInstanceName = null;
        PreparingInstance = false;
    }

    [RelayCommand]
    private void OnClickClose()
    {
        ShowNewMinecraftInstanceDialog = false;
    }

    [RelayCommand]
    private void OnClickInstall()
    {
        if (_minecraftLauncher is null)
        {
            return;
        }

        _logger?.LogInformation("Installing new Minecraft instance: {Version}", SelectedMinecraftVersion);
        var versionInfo = SelectedMinecraftVersion;

        // Displaying "Vanilla" name, instead of the MC version is less confusing when upgrading
        // the instance's base MC version. We would have to rename the instance or what?
        var name = NewInstanceName ?? "Vanilla";

        NewInstanceName = null;
        SelectedMinecraftVersion = null;

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
                    () =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            CanCloseOnClickAway = true;
                            ShowNewMinecraftInstanceDialog = false;
                        });
                    });

                _logger?.LogInformation("Installed");
            })
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger?.LogError(t.Exception, "Problem installing instance");

                    // TODO: show error
                }

                PreparingInstance = false;
                CanCloseOnClickAway = true;
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
}
