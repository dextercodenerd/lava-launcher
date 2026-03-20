using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenericLauncher.Auth;
using GenericLauncher.Database.Model;
using GenericLauncher.Minecraft;
using Microsoft.Extensions.Logging;
using GenericLauncher.Navigation;

namespace GenericLauncher.Screens.InstanceDetails;

public partial class InstanceDetailsViewModel : ViewModelBase, IPageViewModel, IDisposable
{
    private readonly AuthService? _auth;
    private readonly MinecraftLauncher? _minecraftLauncher;
    private readonly ILogger? _logger;

    [ObservableProperty] private MinecraftInstance _instance;
    [ObservableProperty] private ThreadSafeInstallProgressReporter.InstallProgress? _progress;

    public string Title => Instance.Id;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ClickPlayCommand))]
    private MinecraftLauncher.RunningState _runningState = MinecraftLauncher.RunningState.Stopped;

    // Computed property for display logic
    public bool IsInstalling => Instance.State == MinecraftInstanceState.Installing;

    public string ProgressMessage
    {
        get
        {
            if (!Progress.HasValue)
            {
                return "100";
            }

            var average = (long)Progress.Value.MinecraftDownloadProgress * Progress.Value.AssetsDownloadProgress *
                Progress.Value.LibrariesDownloadProgress * Progress.Value.JavaDownloadProgress *
                Progress.Value.ModLoaderInstallProgress / 100000000;

            return average.ToString();
        }
    }

    // Design-time constructor
    public InstanceDetailsViewModel()
    {
        _instance = new MinecraftInstance(
            "test-id",
            "1.21.1",
            "1.21.1",
            MinecraftInstanceModLoader.Vanilla,
            null,
            MinecraftInstanceState.Ready,
            "release",
            "folder",
            21,
            "",
            "",
            "",
            [],
            [],
            []);
    }

    public InstanceDetailsViewModel(
        MinecraftInstance instance,
        AuthService? auth,
        MinecraftLauncher? minecraftLauncher,
        ILogger? logger)
    {
        _instance = instance;
        _auth = auth;
        _minecraftLauncher = minecraftLauncher;
        _logger = logger;

        if (_minecraftLauncher is null)
        {
            return;
        }

        if (_minecraftLauncher.LaunchedInstances.TryGetValue(instance.Id, out var s))
        {
            RunningState = s;
        }

        if (_minecraftLauncher.CurrentInstallProgress.TryGetValue(instance.Id, out var p))
        {
            Progress = p;
        }

        _minecraftLauncher.InstallProgressUpdated += OnInstallProgressUpdated;
        _minecraftLauncher.InstancesChanged += OnInstancesChanged;
        _minecraftLauncher.InstanceStateChanged += OnInstanceStateChanged;
    }

    private void OnInstancesChanged(object? sender, EventArgs e)
    {
        if (_minecraftLauncher is null)
        {
            return;
        }

        // Check if our instance state changed (e.g. from Installing to Ready)
        // We rely on the launcher's list to find the updated version of our instance
        Dispatcher.UIThread.Post(() =>
        {
            var updatedInstance = _minecraftLauncher.Instances.Find(i => i.Id == Instance.Id);
            if (updatedInstance is not null && updatedInstance != Instance)
            {
                Instance = updatedInstance;
                OnPropertyChanged(nameof(IsInstalling));
                ClickPlayCommand.NotifyCanExecuteChanged();
            }
        });
    }

    private void OnInstanceStateChanged(object? sender, (string InstanceId, MinecraftLauncher.RunningState State) e)
    {
        if (e.InstanceId != Instance.Id)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => { RunningState = e.State; });
    }

    private void OnInstallProgressUpdated(object? sender, ThreadSafeInstallProgressReporter.InstallProgress p)
    {
        if (p.InstanceId != Instance.Id)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => { Progress = p; });
    }

    partial void OnProgressChanged(ThreadSafeInstallProgressReporter.InstallProgress? value)
    {
        OnPropertyChanged(nameof(ProgressMessage));
    }

    [RelayCommand(CanExecute = nameof(CanClickPlay))]
    private async Task OnClickPlay()
    {
        if (_auth is null || _minecraftLauncher is null || Instance.State != MinecraftInstanceState.Ready)
        {
            return;
        }

        // Prevent double click
        if (ClickPlayCommand.IsRunning)
        {
            return;
        }

        var acc = _auth.ActiveAccount;
        if (acc is null)
        {
            // TODO: Prompt login if no account? 
            // For now, Button is likely disabled or no-op if no account, or we can rely on MainWindow logic
            return;
        }

        try
        {
            await _minecraftLauncher.LaunchInstance(Instance, () => _auth.AuthenticateAccountAsync(acc));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to launch instance {InstanceId}", Instance.Id);
            // TODO: Show error dialog?
        }
    }

    private bool CanClickPlay()
    {
        if (_minecraftLauncher is null)
        {
            return false;
        }

        if (Instance.State != MinecraftInstanceState.Ready)
        {
            return false;
        }

        return RunningState == MinecraftLauncher.RunningState.Stopped;
    }

    public void Dispose()
    {
        if (_minecraftLauncher is null)
        {
            return;
        }

        _minecraftLauncher.InstallProgressUpdated -= OnInstallProgressUpdated;
        _minecraftLauncher.InstancesChanged -= OnInstancesChanged;
        _minecraftLauncher.InstanceStateChanged -= OnInstanceStateChanged;

        GC.SuppressFinalize(this);
    }
}
