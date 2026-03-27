using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenericLauncher.InstanceMods;
using GenericLauncher.Misc;
using GenericLauncher.Modrinth;
using GenericLauncher.Modrinth.Json;
using GenericLauncher.Navigation;
using GenericLauncher.Screens.ModrinthSearch;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Screens.ModrinthProjectDetails;

public partial class ModrinthProjectDetailsViewModel : ViewModelBase, IPageViewModel, IDisposable
{
    private readonly ModrinthApiClient? _apiClient;
    private readonly InstanceModsManager? _instanceModsManager;
    private readonly string _projectId;
    private readonly ModrinthSearchContext _searchContext;
    private readonly ILogger? _logger;
    private int _targetStateRefreshGeneration;

    [ObservableProperty] private ModrinthProject _project;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _installMessage = "";
    [ObservableProperty] private InstanceInstalledProjectState? _targetProjectState;
    [ObservableProperty] private LatestCompatibleVersionInfo? _targetLatestCompatibleVersion;

    public ModrinthInstallTargetPickerViewModel InstallTargetPicker { get; } = new();
    public bool CanInstall => string.Equals(Project?.ProjectType, "mod", StringComparison.OrdinalIgnoreCase);
    public bool ShowInstallAction => CanInstall && (_searchContext.TargetInstance is null || TargetProjectState is null);
    public bool ShowUpdateAction => CanInstall
                                    && _searchContext.TargetInstance is not null
                                    && TargetProjectState is { InstallKind: InstanceModItemKind.Direct, IsBroken: false }
                                    && TargetLatestCompatibleVersion is not null
                                    && !string.Equals(
                                        TargetLatestCompatibleVersion.VersionId,
                                        TargetProjectState.InstalledVersionId,
                                        StringComparison.Ordinal);
    public bool HasTargetStateText => !string.IsNullOrWhiteSpace(TargetStateText);

    public string TargetStateText
    {
        get
        {
            if (_searchContext.TargetInstance is null || TargetProjectState is null)
            {
                return "";
            }

            if (TargetProjectState.IsBroken)
            {
                return TargetProjectState.InstallKind == InstanceModItemKind.Dependency
                    ? $"Dependency is tracked but the file is missing ({TargetProjectState.InstalledVersionNumber})."
                    : $"Installed mod file is missing ({TargetProjectState.InstalledVersionNumber}).";
            }

            if (TargetProjectState.InstallKind == InstanceModItemKind.Dependency)
            {
                return $"Installed as a required dependency ({TargetProjectState.InstalledVersionNumber}).";
            }

            return ShowUpdateAction && !string.IsNullOrWhiteSpace(TargetLatestCompatibleVersion?.VersionNumber)
                ? $"Installed {TargetProjectState.InstalledVersionNumber}. Update available: {TargetLatestCompatibleVersion.VersionNumber}."
                : $"Installed {TargetProjectState.InstalledVersionNumber}.";
        }
    }

    public string Title => Project?.Title ?? "Project Details";

    // Design-time constructor
    public ModrinthProjectDetailsViewModel() : this(
        new ModrinthSearchResult("", "", "Project Title", "Description", [], "mod", 0, null, "", UtcInstant.UnixEpoch, UtcInstant.UnixEpoch),
        null,
        null,
        ModrinthSearchContext.CreateRoot())
    {
    }

    public ModrinthProjectDetailsViewModel(
        ModrinthSearchResult searchResult,
        ModrinthApiClient? apiClient,
        InstanceModsManager? instanceModsManager,
        ModrinthSearchContext searchContext,
        ILogger? logger = null)
    {
        _projectId = searchResult.ProjectId;
        _apiClient = apiClient;
        _instanceModsManager = instanceModsManager;
        _searchContext = searchContext;
        _logger = logger;

        // Initialize with partial data from search result
        _project = new ModrinthProject(
            searchResult.ProjectId,
            searchResult.Slug,
            searchResult.ProjectType,
            searchResult.Title,
            searchResult.Description,
            "",
            searchResult.Categories,
            "",
            "",
            searchResult.Downloads,
            0,
            searchResult.IconUrl,
            searchResult.DateCreated,
            searchResult.DateModified,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            [],
            []);

        if (_instanceModsManager is not null && _searchContext.TargetInstance is not null)
        {
            _instanceModsManager.InstanceModsChanged += OnInstanceModsChanged;
            LoadTargetProjectStateAsync()
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger?.LogError(t.Exception, "Failed to load installed mod state");
                    }
                });
        }

        if (apiClient is not null)
        {
            LoadProjectAsync()
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger?.LogError(t.Exception, "Failed to load project details");
                    }
                });
        }
    }

    private void OnInstanceModsChanged(object? sender, InstanceModsSnapshotChangedEventArgs e)
    {
        if (_searchContext.TargetInstance is null || !string.Equals(e.InstanceId, _searchContext.TargetInstance.Id, StringComparison.Ordinal))
        {
            return;
        }

        var generation = BeginTargetStateRefresh();
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrentTargetStateRefresh(generation))
            {
                return;
            }

            ApplySnapshot(e.Snapshot);
        });
        _ = RefreshTargetLatestCompatibleVersionAsync(forceRefresh: false, generation);
    }

    private async Task LoadTargetProjectStateAsync()
    {
        if (_instanceModsManager is null || _searchContext.TargetInstance is null)
        {
            return;
        }

        var generation = BeginTargetStateRefresh();

        try
        {
            var snapshot = await _instanceModsManager.GetSnapshotAsync(_searchContext.TargetInstance);
            if (!IsCurrentTargetStateRefresh(generation))
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!IsCurrentTargetStateRefresh(generation))
                {
                    return;
                }

                ApplySnapshot(snapshot);
            });
            await RefreshTargetLatestCompatibleVersionAsync(forceRefresh: false, generation);
        }
        catch (Exception ex)
        {
            if (IsCurrentTargetStateRefresh(generation))
            {
                _logger?.LogWarning(ex, "Failed to load installed mod state for {ProjectId}", _projectId);
            }
        }
    }

    private void ApplySnapshot(InstanceModsSnapshot snapshot)
    {
        snapshot.ProjectsById.TryGetValue(_projectId, out var projectState);
        TargetProjectState = projectState;
    }

    private async Task RefreshTargetLatestCompatibleVersionAsync(bool forceRefresh, int generation)
    {
        if (_instanceModsManager is null || _searchContext.TargetInstance is null)
        {
            return;
        }

        try
        {
            var latestCompatibleVersions = await _instanceModsManager.GetLatestCompatibleVersionsAsync(
                _searchContext.TargetInstance,
                [_projectId],
                forceRefresh);
            if (!IsCurrentTargetStateRefresh(generation))
            {
                return;
            }

            latestCompatibleVersions.TryGetValue(_projectId, out var latestCompatibleVersion);
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsCurrentTargetStateRefresh(generation))
                {
                    return;
                }

                TargetLatestCompatibleVersion = latestCompatibleVersion;
            });
        }
        catch (Exception ex)
        {
            if (IsCurrentTargetStateRefresh(generation))
            {
                _logger?.LogWarning(ex, "Failed to refresh available update for {ProjectId}", _projectId);
            }
        }
    }

    private int BeginTargetStateRefresh() => Interlocked.Increment(ref _targetStateRefreshGeneration);

    private bool IsCurrentTargetStateRefresh(int generation) =>
        generation == Volatile.Read(ref _targetStateRefreshGeneration);

    private async Task LoadProjectAsync()
    {
        if (_apiClient is null || string.IsNullOrEmpty(_projectId) || IsLoading)
        {
            return;
        }

        IsLoading = true;
        HasError = false;
        ErrorMessage = "";

        try
        {
            var project = await _apiClient.GetProjectAsync(_projectId);
            if (project is null)
            {
                HasError = true;
                ErrorMessage = "Failed to load project details. Please try again.";
                return;
            }

            Project = project with
            {
                Loaders = project.Loaders ?? [],
            };
            _logger?.LogInformation("Loaded project: {Title} ({Id})", project.Title, project.Id);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error loading project details for {ProjectId}", _projectId);
            HasError = true;
            ErrorMessage = "An error occurred while loading the project. Please try again.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        await LoadProjectAsync();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (_instanceModsManager is null || !CanInstall)
        {
            return;
        }

        try
        {
            IsInstalling = true;
            InstallMessage = "";

            if (_searchContext.TargetInstance is not null)
            {
                await _instanceModsManager.InstallProjectAsync(_searchContext.TargetInstance, _projectId);
                InstallMessage = $"Installed into {_searchContext.TargetInstance.Id}.";
                return;
            }

            InstallTargetPicker.IsOpen = true;
            InstallTargetPicker.IsLoading = true;
            InstallTargetPicker.Title = $"Install {Project.Title}";
            InstallTargetPicker.Message = "";
            InstallTargetPicker.Targets.Clear();

            var targets = await _instanceModsManager.GetCompatibleInstancesAsync(_projectId);
            foreach (var target in targets)
            {
                InstallTargetPicker.Targets.Add(target);
            }

            if (InstallTargetPicker.Targets.Count == 0)
            {
                InstallTargetPicker.Message = "No compatible modded instances found.";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to install project {ProjectId}", _projectId);
            InstallMessage = ex.Message;
        }
        finally
        {
            IsInstalling = false;
            InstallTargetPicker.IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (_instanceModsManager is null || _searchContext.TargetInstance is null || !ShowUpdateAction)
        {
            return;
        }

        try
        {
            IsInstalling = true;
            InstallMessage = "";
            await _instanceModsManager.UpdateModAsync(_searchContext.TargetInstance, _projectId);
            InstallMessage = $"Updated in {_searchContext.TargetInstance.Id}.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update project {ProjectId}", _projectId);
            InstallMessage = ex.Message;
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private async Task InstallToInstanceAsync(CompatibleInstanceInstallTarget target)
    {
        if (_instanceModsManager is null)
        {
            return;
        }

        try
        {
            IsInstalling = true;
            await _instanceModsManager.InstallProjectAsync(target.Instance, _projectId);
            InstallMessage = $"Installed into {target.Instance.Id}.";
            InstallTargetPicker.IsOpen = false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to install project {ProjectId} to {InstanceId}", _projectId, target.Instance.Id);
            InstallTargetPicker.Message = ex.Message;
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private void CloseInstallPicker()
    {
        InstallTargetPicker.IsOpen = false;
        InstallTargetPicker.Reset();
    }

    partial void OnProjectChanged(ModrinthProject value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(ShowInstallAction));
        OnPropertyChanged(nameof(ShowUpdateAction));
    }

    partial void OnTargetProjectStateChanged(InstanceInstalledProjectState? value)
    {
        OnPropertyChanged(nameof(ShowInstallAction));
        OnPropertyChanged(nameof(ShowUpdateAction));
        OnPropertyChanged(nameof(TargetStateText));
        OnPropertyChanged(nameof(HasTargetStateText));
    }

    partial void OnTargetLatestCompatibleVersionChanged(LatestCompatibleVersionInfo? value)
    {
        OnPropertyChanged(nameof(ShowUpdateAction));
        OnPropertyChanged(nameof(TargetStateText));
        OnPropertyChanged(nameof(HasTargetStateText));
    }

    public void Dispose()
    {
        if (_instanceModsManager is not null)
        {
            _instanceModsManager.InstanceModsChanged -= OnInstanceModsChanged;
        }

        GC.SuppressFinalize(this);
    }
}
