using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenericLauncher.Auth;
using GenericLauncher.Database.Model;
using GenericLauncher.InstanceMods;
using GenericLauncher.Minecraft;
using GenericLauncher.Modrinth;
using GenericLauncher.Modrinth.Json;
using GenericLauncher.Navigation;
using GenericLauncher.Screens.ModrinthSearch;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Screens.InstanceDetails;

public partial class InstanceDetailsViewModel : ViewModelBase, IPageViewModel, IDisposable
{
    public enum InstanceDetailsTab
    {
        Content,
        Mods,
    }

    private readonly AuthService? _auth;
    private readonly MinecraftLauncher? _minecraftLauncher;
    private readonly InstanceModsManager? _instanceModsManager;
    private readonly ModrinthApiClient? _modrinthApiClient;
    private readonly Action<ModrinthSearchResult, ModrinthSearchContext>? _openProjectDetails;
    private readonly ILogger? _logger;

    private InstanceModsSnapshot _modsSnapshot = InstanceModsSnapshot.Empty;
    private readonly Dictionary<string, InstanceInstalledProjectState> _projectStates =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _modsLoaded;

    [ObservableProperty] private MinecraftInstance _instance;
    [ObservableProperty] private ThreadSafeInstallProgressReporter.InstallProgress? _progress;
    [ObservableProperty] private InstanceDetailsTab _selectedTab = InstanceDetailsTab.Content;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClickPlayCommand))]
    private MinecraftLauncher.RunningState _runningState = MinecraftLauncher.RunningState.Stopped;

    [ObservableProperty] private bool _isModsLoading;
    [ObservableProperty] private string _modsStatusMessage = "";
    [ObservableProperty] private string _modsErrorMessage = "";
    [ObservableProperty] private bool _isSearchVisible;
    [ObservableProperty] private ModrinthSearchViewModel? _inlineSearchViewModel;
    [ObservableProperty] private ObservableCollection<InstanceModListItem> _installedMods = [];
    [ObservableProperty] private ObservableCollection<InstanceModListItem> _requiredDependencies = [];
    [ObservableProperty] private ObservableCollection<InstanceModListItem> _manualMods = [];
    [ObservableProperty] private ObservableCollection<InstanceModListItem> _brokenMods = [];

    public string Title => Instance.Id;
    public bool IsContentTab => SelectedTab == InstanceDetailsTab.Content;
    public bool IsModsTab => SelectedTab == InstanceDetailsTab.Mods;
    public bool IsInstalling => Instance.State == MinecraftInstanceState.Installing;
    public bool CanManageMods => Instance.ModLoader is MinecraftInstanceModLoader.Fabric
        or MinecraftInstanceModLoader.Forge
        or MinecraftInstanceModLoader.NeoForge;
    public bool CanUpdateAll => InstalledMods.Any(item => item.HasUpdate);

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
        InstanceModsManager? instanceModsManager,
        ModrinthApiClient? modrinthApiClient,
        Action<ModrinthSearchResult, ModrinthSearchContext>? openProjectDetails,
        ILogger? logger)
    {
        _instance = instance;
        _auth = auth;
        _minecraftLauncher = minecraftLauncher;
        _instanceModsManager = instanceModsManager;
        _modrinthApiClient = modrinthApiClient;
        _openProjectDetails = openProjectDetails;
        _logger = logger;

        if (_minecraftLauncher is not null)
        {
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

        if (_instanceModsManager is not null)
        {
            _instanceModsManager.InstanceModsChanged += OnInstanceModsChanged;
        }
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
                OnPropertyChanged(nameof(CanManageMods));
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

        Dispatcher.UIThread.Post(() => RunningState = e.State);
    }

    private void OnInstallProgressUpdated(object? sender, ThreadSafeInstallProgressReporter.InstallProgress p)
    {
        if (p.InstanceId != Instance.Id)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => Progress = p);
    }

    private void OnInstanceModsChanged(object? sender, InstanceModsSnapshotChangedEventArgs e)
    {
        if (!string.Equals(e.InstanceId, Instance.Id, StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyModsSnapshot(e.Snapshot));
    }

    partial void OnProgressChanged(ThreadSafeInstallProgressReporter.InstallProgress? value)
    {
        OnPropertyChanged(nameof(ProgressMessage));
    }

    partial void OnSelectedTabChanged(InstanceDetailsTab value)
    {
        OnPropertyChanged(nameof(IsContentTab));
        OnPropertyChanged(nameof(IsModsTab));
    }

    partial void OnInstanceChanged(MinecraftInstance value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanManageMods));
    }

    [RelayCommand]
    private void SelectContentTab()
    {
        SelectedTab = InstanceDetailsTab.Content;
    }

    [RelayCommand]
    private void SelectModsTab()
    {
        SelectedTab = InstanceDetailsTab.Mods;
        _ = EnsureModsLoadedAsync();
    }

    [RelayCommand]
    private async Task RefreshModsAsync()
    {
        await LoadModsSnapshotAsync(forceRefresh: true);
    }

    [RelayCommand]
    private void ShowAddMods()
    {
        if (!CanManageMods || _modrinthApiClient is null || _instanceModsManager is null)
        {
            return;
        }

        InlineSearchViewModel?.Dispose();
        InlineSearchViewModel = new ModrinthSearchViewModel(
            _modrinthApiClient,
            _instanceModsManager,
            ModrinthSearchContext.CreateForInstance(Instance),
            _openProjectDetails,
            logger: _logger,
            initialSnapshot: _modsSnapshot);
        InlineSearchViewModel.ApplyTargetSnapshot(_modsSnapshot);
        IsSearchVisible = true;
    }

    [RelayCommand]
    private void CloseInlineSearch()
    {
        InlineSearchViewModel?.Dispose();
        InlineSearchViewModel = null;
        IsSearchVisible = false;
    }

    [RelayCommand]
    private async Task UpdateAllModsAsync()
    {
        if (_instanceModsManager is null)
        {
            return;
        }

        try
        {
            IsModsLoading = true;
            ModsErrorMessage = "";
            ModsStatusMessage = "";
            await _instanceModsManager.UpdateAllAsync(Instance);
            ModsStatusMessage = "Updated installed mods.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update mods for {InstanceId}", Instance.Id);
            ModsErrorMessage = ex.Message;
        }
        finally
        {
            IsModsLoading = false;
        }
    }

    [RelayCommand]
    private async Task UpdateModItemAsync(InstanceModListItem item)
    {
        if (_instanceModsManager is null || !item.CanUpdate || string.IsNullOrWhiteSpace(item.ProjectId))
        {
            return;
        }

        try
        {
            IsModsLoading = true;
            ModsErrorMessage = "";
            ModsStatusMessage = "";
            await _instanceModsManager.UpdateModAsync(Instance, item.ProjectId);
            ModsStatusMessage = $"Updated {item.DisplayName}.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update mod {ProjectId}", item.ProjectId);
            ModsErrorMessage = ex.Message;
        }
        finally
        {
            IsModsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DeleteModItemAsync(InstanceModListItem item)
    {
        if (_instanceModsManager is null || !item.CanDelete)
        {
            return;
        }

        try
        {
            IsModsLoading = true;
            ModsErrorMessage = "";
            ModsStatusMessage = "";
            await _instanceModsManager.DeleteModAsync(Instance, item.Key);
            ModsStatusMessage = $"Deleted {item.DisplayName}.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete mod item {Key}", item.Key);
            ModsErrorMessage = ex.Message;
        }
        finally
        {
            IsModsLoading = false;
        }
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

    private async Task EnsureModsLoadedAsync()
    {
        if (_modsLoaded || !CanManageMods)
        {
            return;
        }

        await LoadModsSnapshotAsync(forceRefresh: false);
    }

    private async Task LoadModsSnapshotAsync(bool forceRefresh)
    {
        if (_instanceModsManager is null || !CanManageMods)
        {
            return;
        }

        try
        {
            IsModsLoading = true;
            ModsErrorMessage = "";
            var snapshot = await _instanceModsManager.GetSnapshotAsync(Instance, forceRefresh);
            Dispatcher.UIThread.Post(() => ApplyModsSnapshot(snapshot));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load mods for {InstanceId}", Instance.Id);
            Dispatcher.UIThread.Post(() => ModsErrorMessage = "Failed to load mods.");
        }
        finally
        {
            Dispatcher.UIThread.Post(() => IsModsLoading = false);
        }
    }

    private void ApplyModsSnapshot(InstanceModsSnapshot snapshot)
    {
        _modsSnapshot = snapshot;
        _modsLoaded = true;
        _projectStates.Clear();
        foreach (var projectState in snapshot.ProjectsById)
        {
            _projectStates[projectState.Key] = projectState.Value;
        }

        Replace(InstalledMods, snapshot.InstalledMods);
        Replace(RequiredDependencies, snapshot.RequiredDependencies);
        Replace(ManualMods, snapshot.ManualMods);
        Replace(BrokenMods, snapshot.BrokenMods);
        InlineSearchViewModel?.ApplyTargetSnapshot(snapshot);
        OnPropertyChanged(nameof(CanUpdateAll));
    }

    private static void Replace(
        ObservableCollection<InstanceModListItem> target,
        IEnumerable<InstanceModListItem> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    public void Dispose()
    {
        InlineSearchViewModel?.Dispose();

        if (_instanceModsManager is not null)
        {
            _instanceModsManager.InstanceModsChanged -= OnInstanceModsChanged;
        }

        if (_minecraftLauncher is not null)
        {
            _minecraftLauncher.InstallProgressUpdated -= OnInstallProgressUpdated;
            _minecraftLauncher.InstancesChanged -= OnInstancesChanged;
            _minecraftLauncher.InstanceStateChanged -= OnInstanceStateChanged;
        }

        GC.SuppressFinalize(this);
    }
}
