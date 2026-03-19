using System;
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
using GenericLauncher.Navigation;

namespace GenericLauncher.Screens.HomeScreen;

public partial class HomeViewModel : ViewModelBase, IPageViewModel
{
    public string Title => "Library";
    public bool IsRootScreen => true;
    private const double DefaultSpace = 16;

    private readonly ILogger? _logger;
    private readonly AuthService? _auth;
    private readonly MinecraftLauncher? _minecraftLauncher;

    private ImmutableList<MinecraftInstance> _dbInstances = [];
    [ObservableProperty] private ObservableCollection<MinecraftInstanceItem> _instances = [];

    public HomeViewModel() : this(null)
    {
    }

    private readonly Action<MinecraftInstance>? _openDetails;

    public HomeViewModel(
        AuthService? authService = null,
        MinecraftLauncher? minecraftLauncher = null,
        ILogger? logger = null,
        Action<MinecraftInstance>? openDetails = null)
    {
        _logger = logger;
        _auth = authService;
        _minecraftLauncher = minecraftLauncher;
        _openDetails = openDetails;

        if (_minecraftLauncher is null)
        {
            return;
        }

        UpdateInstancesUi(_minecraftLauncher.Instances);
        _minecraftLauncher.InstancesChanged += OnInstancesChanged;
        _minecraftLauncher.InstanceStateChanged += OnInstanceStateChanged;
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
        instances.Select(i =>
            {
                // Init running state
                var state = MinecraftLauncher.RunningState.Stopped;
                if (_minecraftLauncher?.LaunchedInstances.TryGetValue(i.Id, out var s) == true)
                {
                    state = s;
                }

                return new MinecraftInstanceItem(i, null, PlayInstance)
                {
                    RunningState = state,
                };
            })
            .ToList()
            // TODO: update only the changed instances, and add only the new ones, and remove the deleted i.e., diff
            .ForEach(i => Instances.Add(i));
    }

    private void OnInstanceStateChanged(object? sender, (string InstanceId, MinecraftLauncher.RunningState State) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var found = Instances.FirstOrDefault(i => i.Instance.Id == e.InstanceId);
            found?.RunningState = e.State;
        });
    }

    private async Task PlayInstance(MinecraftInstanceItem item)
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

        try
        {
            await _minecraftLauncher.LaunchInstance(item.Instance, () => _auth.AuthenticateAccountAsync(acc));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to launch instance {InstanceId}", item.Instance.Id);
        }
    }

    [RelayCommand]
    private void ClickInstance(MinecraftInstanceItem item)
    {
        _openDetails?.Invoke(item.Instance);
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
