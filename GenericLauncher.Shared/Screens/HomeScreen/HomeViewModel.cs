using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenericLauncher.Auth;
using GenericLauncher.Database.Model;
using GenericLauncher.Minecraft;
using GenericLauncher.Model;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Screens.HomeScreen;

public partial class HomeViewModel : ViewModelBase
{
    private const double DefaultSpace = 16;

    private readonly ILogger? _logger;
    private readonly AuthService? _auth;
    private readonly MinecraftLauncher? _minecraftLauncher;

    private ImmutableList<MinecraftInstance> _dbInstances = [];
    [ObservableProperty] private ObservableCollection<MinecraftInstanceItem> _instances = [];

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
        _minecraftLauncher.InstallProgressUpdated += OnInstallProgressUpdated;
    }

    private void UpdateInstancesUi(ImmutableList<MinecraftInstance> instances)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (_dbInstances == instances
            || (_dbInstances.Count == instances.Count && _dbInstances.SequenceEqual(instances)))
        {
            return;
        }

        _dbInstances = instances;

        Instances.Clear();
        // TODO: Move this merging off the UI thread -- then update also OnInstallProgressUpdated()
        //  because that is enumerating Instances and will crash.
        instances.Select(i => new MinecraftInstanceItem(i, null))
            .ToList()
            // TODO: update only the changed instances, and add only the new ones, and remove the deleted i.e., diff
            .ForEach(i => Instances.Add(i));
    }

    private void UpdateLaunchedInstancesUi(
        IDictionary<string, MinecraftLauncher.RunningState> launchedInstances)
    {
        Dispatcher.UIThread.VerifyAccess();

        _logger?.LogDebug("Launched instances: {LaunchedInstances}", launchedInstances);
    }

    [RelayCommand]
    private async Task ClickInstance(MinecraftInstanceItem item)
    {
        if (_auth is null || _minecraftLauncher is null || item.Instance.State != MinecraftInstanceState.Ready)
        {
            return;
        }

        var acc = _auth.ActiveAccount;
        if (acc is null)
        {
            return;
        }

        var newAcc = await _auth.AuthenticateAccountAsync(acc);

        await _minecraftLauncher.LaunchInstance(item.Instance, newAcc);
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

    private void OnInstallProgressUpdated(object? sender, ThreadSafeInstallProgressReporter.InstallProgress p)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // TODO: We are doing this on the UI thread, because Instances can replaced on the UI
            //  thread too, at the moment, and that will crash when an enumeration is happening
            //  elsewhere.
            // WARN: Such crash is rare, but can be reproduced when creating a new instance, where
            //  everything is already downloaded and we insert the installing state and quickly
            //  update to the ready states, and in parallel we enumerate the "100%" progress here.
            var found = Instances.FirstOrDefault(i => i.Instance.Id == p.InstanceId);
            found?.Progress = p;
        });
    }
}