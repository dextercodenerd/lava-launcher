using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenericLauncher.Auth;
using GenericLauncher.Database;
using GenericLauncher.Minecraft;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Screens.HomeScreen;

public partial class HomeViewModel : ViewModelBase
{
    private const double DefaultSpace = 16;

    private readonly ILogger? _logger;
    private readonly AuthService? _auth;
    private readonly MinecraftLauncher? _minecraftLauncher;

    [ObservableProperty] private ImmutableList<MinecraftInstance> _instances = [];

    public HomeViewModel() : this(null)
    {
    }

    public HomeViewModel(
        AuthService? authService = null,
        MinecraftLauncher? minecraftLauncher = null,
        ILogger? logger = null)
    {
        _logger = logger;
        _auth = authService;
        _minecraftLauncher = minecraftLauncher;

        if (_minecraftLauncher is null)
        {
            return;
        }

        UpdateInstancesUi(_minecraftLauncher.Instances);
        _minecraftLauncher.InstancesChanged += OnInstancesChanged;
        _minecraftLauncher.LaunchedInstancesChanged += OnLaunchedInstancesChanged;
    }

    private void UpdateInstancesUi(ImmutableList<MinecraftInstance> instances)
    {
        Dispatcher.UIThread.VerifyAccess();

        Instances = instances;
    }

    private void UpdateLaunchedInstancesUi(
        IDictionary<string, MinecraftLauncher.RunningState> launchedInstances)
    {
        Dispatcher.UIThread.VerifyAccess();

        _logger?.LogDebug("Launched instances: {LaunchedInstances}", launchedInstances);
    }

    [RelayCommand]
    private async Task ClickInstance(MinecraftInstance instance)
    {
        if (_auth is null || _minecraftLauncher is null || instance.State != MinecraftInstanceState.Ready)
        {
            return;
        }

        var acc = _auth.ActiveAccount;
        if (acc is null)
        {
            return;
        }

        var newAcc = await _auth.AuthenticateAccountAsync(acc);

        await _minecraftLauncher.LaunchInstance(instance, newAcc);
    }

    private void OnInstancesChanged(object? sender, EventArgs e)
    {
        var instances = _minecraftLauncher?.Instances;
        if (instances is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => { UpdateInstancesUi(instances); });
    }

    private void OnLaunchedInstancesChanged(object? sender, EventArgs e)
    {
        // TODO: Make a deep-copy
        var copy = _minecraftLauncher?.LaunchedInstances;
        if (copy is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => { UpdateLaunchedInstancesUi(copy); });
    }
}
