using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
    private readonly Action? _onDeleted;
    private readonly ILogger? _logger;

    private InstanceModsSnapshot _modsSnapshot = InstanceModsSnapshot.Empty;
    private IReadOnlyDictionary<string, LatestCompatibleVersionInfo> _latestCompatibleVersions =
        new Dictionary<string, LatestCompatibleVersionInfo>(StringComparer.OrdinalIgnoreCase);
    private int _modsRefreshGeneration;
    private bool _modsLoaded;

    [ObservableProperty] private MinecraftInstance _instance;
    [ObservableProperty] private ThreadSafeInstallProgressReporter.InstallProgress? _progress;
    [ObservableProperty] private InstanceDetailsTab _selectedTab = InstanceDetailsTab.Content;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClickPlayCommand))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private MinecraftLauncher.RunningState _runningState = MinecraftLauncher.RunningState.Stopped;

    [ObservableProperty] private bool _isDeleteConfirmationVisible;
    [ObservableProperty] private string _deleteErrorMessage = "";

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
    public bool IsDeleting => Instance.State == MinecraftInstanceState.Deleting;
    public bool IsDeleteFailed => Instance.State == MinecraftInstanceState.DeleteFailed;
    public bool CanDelete => Instance.State == MinecraftInstanceState.Ready
        && RunningState == MinecraftLauncher.RunningState.Stopped;
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
        Action? onDeleted = null,
        ILogger? logger = null)
    {
        _instance = instance;
        _auth = auth;
        _minecraftLauncher = minecraftLauncher;
        _instanceModsManager = instanceModsManager;
        _modrinthApiClient = modrinthApiClient;
        _openProjectDetails = openProjectDetails;
        _onDeleted = onDeleted;
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

        Dispatcher.UIThread.Post(() =>
        {
            var updatedInstance = _minecraftLauncher.Instances.Find(i => i.Id == Instance.Id);
            if (updatedInstance is null)
            {
                // Instance was fully deleted -- navigate back
                _onDeleted?.Invoke();
                return;
            }

            if (updatedInstance != Instance)
            {
                Instance = updatedInstance;
                OnPropertyChanged(nameof(IsInstalling));
                OnPropertyChanged(nameof(CanManageMods));
                OnPropertyChanged(nameof(IsDeleting));
                OnPropertyChanged(nameof(IsDeleteFailed));
                OnPropertyChanged(nameof(CanDelete));
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

        var generation = BeginModsRefresh();
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrentModsRefresh(generation))
            {
                return;
            }

            ApplyModsSnapshot(e.Snapshot);
        });
        _ = RefreshUpdateStatusesAsync(e.Snapshot, forceRefresh: false, generation);
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
        OnPropertyChanged(nameof(IsDeleting));
        OnPropertyChanged(nameof(IsDeleteFailed));
        OnPropertyChanged(nameof(CanDelete));
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
        await LoadModsStateAsync(forceRefresh: true);
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
            _logger);
        InlineSearchViewModel.ApplyTargetState(_modsSnapshot, _latestCompatibleVersions);
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

    [RelayCommand]
    private void ShowDeleteConfirmation() => IsDeleteConfirmationVisible = true;

    [RelayCommand]
    private void CancelDelete()
    {
        IsDeleteConfirmationVisible = false;
        DeleteErrorMessage = "";
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (_minecraftLauncher is null)
        {
            return;
        }
        try
        {
            DeleteErrorMessage = "";
            await _minecraftLauncher.DeleteInstanceAsync(Instance.Id);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete instance {InstanceId}", Instance.Id);
            DeleteErrorMessage = "Failed to delete instance files. You can retry.";
            IsDeleteConfirmationVisible = false;
        }
    }

    [RelayCommand]
    private async Task RetryDeleteAsync()
    {
        if (_minecraftLauncher is null)
        {
            return;
        }
        try
        {
            DeleteErrorMessage = "";
            await _minecraftLauncher.DeleteInstanceAsync(Instance.Id);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to retry delete for instance {InstanceId}", Instance.Id);
            DeleteErrorMessage = "Failed to delete instance files. You can retry.";
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

        await LoadModsStateAsync(false);
    }

    private async Task LoadModsStateAsync(bool forceRefresh)
    {
        if (_instanceModsManager is null || !CanManageMods)
        {
            return;
        }

        var generation = BeginModsRefresh();

        try
        {
            IsModsLoading = true;
            ModsErrorMessage = "";
            var snapshot = await _instanceModsManager.GetSnapshotAsync(Instance, forceRefresh);
            if (!IsCurrentModsRefresh(generation))
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!IsCurrentModsRefresh(generation))
                {
                    return;
                }

                ApplyModsSnapshot(snapshot);
            });
            await RefreshUpdateStatusesAsync(snapshot, forceRefresh, generation);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load mods for {InstanceId}", Instance.Id);
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsCurrentModsRefresh(generation))
                {
                    return;
                }

                ModsErrorMessage = "Failed to load mods.";
            });
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsCurrentModsRefresh(generation))
                {
                    return;
                }

                IsModsLoading = false;
            });
        }
    }

    private void ApplyModsSnapshot(InstanceModsSnapshot snapshot)
    {
        _modsSnapshot = snapshot;
        _modsLoaded = true;
        RenderModLists();
    }

    private void ApplyLatestCompatibleVersions(IReadOnlyDictionary<string, LatestCompatibleVersionInfo> latestCompatibleVersions)
    {
        _latestCompatibleVersions = latestCompatibleVersions;
        RenderModLists();
    }

    private void RenderModLists()
    {
        Replace(InstalledMods, _modsSnapshot.InstalledMods.Select(ApplyLatestCompatibleVersion));
        Replace(RequiredDependencies, _modsSnapshot.RequiredDependencies);
        Replace(ManualMods, _modsSnapshot.ManualMods);
        Replace(BrokenMods, _modsSnapshot.BrokenMods);
        InlineSearchViewModel?.ApplyTargetState(_modsSnapshot, _latestCompatibleVersions);
        OnPropertyChanged(nameof(CanUpdateAll));
    }

    private async Task<IReadOnlyDictionary<string, LatestCompatibleVersionInfo>> GetLatestCompatibleVersionsAsync(
        InstanceModsSnapshot snapshot,
        bool forceRefresh)
    {
        if (_instanceModsManager is null)
        {
            return new Dictionary<string, LatestCompatibleVersionInfo>(StringComparer.OrdinalIgnoreCase);
        }

        var directProjectIds = snapshot.ProjectsById.Values
            .Where(project => project.InstallKind == InstanceModItemKind.Direct)
            .Select(project => project.ProjectId)
            .ToArray();
        if (directProjectIds.Length == 0)
        {
            return new Dictionary<string, LatestCompatibleVersionInfo>(StringComparer.OrdinalIgnoreCase);
        }

        return await _instanceModsManager.GetLatestCompatibleVersionsAsync(Instance, directProjectIds, forceRefresh);
    }

    private async Task RefreshUpdateStatusesAsync(InstanceModsSnapshot snapshot, bool forceRefresh)
        => await RefreshUpdateStatusesAsync(snapshot, forceRefresh, Volatile.Read(ref _modsRefreshGeneration));

    private async Task RefreshUpdateStatusesAsync(
        InstanceModsSnapshot snapshot,
        bool forceRefresh,
        int generation)
    {
        try
        {
            var latestCompatibleVersions = await GetLatestCompatibleVersionsAsync(snapshot, forceRefresh);
            if (!IsCurrentModsRefresh(generation))
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!IsCurrentModsRefresh(generation))
                {
                    return;
                }

                ApplyLatestCompatibleVersions(latestCompatibleVersions);
            });
        }
        catch (Exception ex)
        {
            if (IsCurrentModsRefresh(generation))
            {
                _logger?.LogWarning(ex, "Failed to refresh available updates for {InstanceId}", Instance.Id);
            }
        }
    }

    private int BeginModsRefresh() => Interlocked.Increment(ref _modsRefreshGeneration);

    private bool IsCurrentModsRefresh(int generation) =>
        generation == Volatile.Read(ref _modsRefreshGeneration);

    private InstanceModListItem ApplyLatestCompatibleVersion(InstanceModListItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ProjectId)
            || !_modsSnapshot.ProjectsById.TryGetValue(item.ProjectId, out var installedProject)
            || installedProject.InstallKind != InstanceModItemKind.Direct
            || installedProject.IsBroken
            || !_latestCompatibleVersions.TryGetValue(item.ProjectId, out var latestCompatibleVersion)
            || string.Equals(latestCompatibleVersion.VersionId, installedProject.InstalledVersionId, StringComparison.Ordinal))
        {
            return item with
            {
                HasUpdate = false,
                LatestVersionNumber = null,
            };
        }

        return item with
        {
            HasUpdate = true,
            LatestVersionNumber = latestCompatibleVersion.VersionNumber,
        };
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
