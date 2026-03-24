using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenericLauncher.Database.Model;
using GenericLauncher.InstanceMods;
using GenericLauncher.Modrinth;
using GenericLauncher.Modrinth.Json;
using GenericLauncher.Navigation;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Screens.ModrinthSearch;

public partial class ModrinthSearchViewModel : ViewModelBase, IPageViewModel, IDisposable
{
    private readonly ModrinthApiClient? _apiClient;
    private readonly InstanceModsManager? _instanceModsManager;
    private readonly Action<ModrinthSearchResult, ModrinthSearchContext>? _openProjectDetails;
    private readonly Func<Task>? _onInstalled;
    private readonly ILogger? _logger;

    private readonly DispatcherTimer _debounceTimer;
    private CancellationTokenSource? _currentSearchCts;
    private readonly ModrinthSearchContext _searchContext;
    private InstanceModsSnapshot? _targetSnapshot;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private ModrinthProjectType _selectedProjectType = ModrinthProjectType.All;
    [ObservableProperty] private string _selectedSortOrder = "relevance";
    [ObservableProperty] private ObservableCollection<ModrinthSearchResultItemViewModel> _searchResults = [];
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _installMessage = "";
    [ObservableProperty] private string _pendingInstallProjectId = "";
    public ModrinthInstallTargetPickerViewModel InstallTargetPicker { get; } = new();

    private const int PageSize = 20;

    // IPageViewModel implementation
    public string Title => _searchContext.Title;
    public bool HasLockedFilters => _searchContext.IsInstanceInstall;
    public string LockedFiltersSummary => _searchContext.LockedFiltersSummary;

    // Design-time constructor
    public ModrinthSearchViewModel()
    {
        _searchContext = ModrinthSearchContext.CreateRoot();
        _debounceTimer = new DispatcherTimer();
    }

    public ModrinthSearchViewModel(
        ModrinthApiClient? apiClient,
        InstanceModsManager? instanceModsManager,
        ModrinthSearchContext searchContext,
        Action<ModrinthSearchResult, ModrinthSearchContext>? openProjectDetails,
        Func<Task>? onInstalled = null,
        ILogger? logger = null,
        InstanceModsSnapshot? initialSnapshot = null)
    {
        _apiClient = apiClient;
        _instanceModsManager = instanceModsManager;
        _searchContext = searchContext;
        _openProjectDetails = openProjectDetails;
        _onInstalled = onInstalled;
        _logger = logger;
        _targetSnapshot = initialSnapshot;

        if (_searchContext.LockProjectTypeToMods)
        {
            _selectedProjectType = ModrinthProjectType.Mod;
        }

        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _debounceTimer.Tick += OnDebounceTimerTick;
    }

    private void OnDebounceTimerTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();

        // Timer runs on UI thread (DispatcherTimer), so we can directly trigger search
        CurrentPage = 1;
        _ = TriggerSearchAsync();
    }

    /// <summary>
    /// Cancels any active search and starts a new one using the current usage parameters.
    /// </summary>
    private async Task TriggerSearchAsync()
    {
        // Cancel the running API request
        _currentSearchCts?.Cancel();
        _currentSearchCts = new CancellationTokenSource();

        try
        {
            await ExecuteSearchAsync(_currentSearchCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        // Manual trigger (Enter key or Button)
        // Stop any pending debounce to avoid double-search
        _debounceTimer.Stop();

        CurrentPage = 1;
        await TriggerSearchAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CurrentPage < TotalPages)
        {
            // Pagination should also cancel any pending debounce or running search
            _debounceTimer.Stop();

            CurrentPage++;
            await TriggerSearchAsync();
        }
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CurrentPage > 1)
        {
            // Pagination should also cancel any pending debounce or running search
            _debounceTimer.Stop();

            CurrentPage--;
            await TriggerSearchAsync();
        }
    }

    [RelayCommand]
    private void OpenProjectDetails(ModrinthSearchResultItemViewModel result)
    {
        _openProjectDetails?.Invoke(result.SearchResult, _searchContext);
    }

    [RelayCommand]
    private async Task InstallAsync(ModrinthSearchResultItemViewModel result)
    {
        if (_instanceModsManager is null)
        {
            return;
        }

        try
        {
            IsInstalling = true;
            InstallMessage = "";

            if (_searchContext.TargetInstance is not null)
            {
                await _instanceModsManager.InstallProjectAsync(_searchContext.TargetInstance, result.ProjectId);
                InstallMessage = $"Installed into {_searchContext.TargetInstance.Id}.";
                if (_onInstalled is not null)
                {
                    await _onInstalled();
                }
                return;
            }

            InstallTargetPicker.IsOpen = true;
            InstallTargetPicker.IsLoading = true;
            InstallTargetPicker.Title = $"Install {result.Title}";
            InstallTargetPicker.Message = "";
            InstallTargetPicker.Targets.Clear();
            PendingInstallProjectId = result.ProjectId;

            var targets = await _instanceModsManager.GetCompatibleInstancesAsync(result.ProjectId);
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
            _logger?.LogError(ex, "Error installing Modrinth project {ProjectId}", result.ProjectId);
            InstallMessage = ex.Message;
        }
        finally
        {
            IsInstalling = false;
            InstallTargetPicker.IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAsync(ModrinthSearchResultItemViewModel result)
    {
        if (_instanceModsManager is null || _searchContext.TargetInstance is null)
        {
            return;
        }

        try
        {
            IsInstalling = true;
            InstallMessage = "";
            await _instanceModsManager.UpdateModAsync(_searchContext.TargetInstance, result.ProjectId);
            InstallMessage = $"Updated in {_searchContext.TargetInstance.Id}.";
            if (_onInstalled is not null)
            {
                await _onInstalled();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error updating Modrinth project {ProjectId}", result.ProjectId);
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
            InstallMessage = "";

            if (string.IsNullOrWhiteSpace(PendingInstallProjectId))
            {
                return;
            }

            await _instanceModsManager.InstallProjectAsync(target.Instance, PendingInstallProjectId);
            InstallMessage = $"Installed into {target.Instance.Id}.";
            InstallTargetPicker.IsOpen = false;
            if (_onInstalled is not null)
            {
                await _onInstalled();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error installing project into {InstanceId}", target.Instance.Id);
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
        PendingInstallProjectId = "";
    }

    private async Task ExecuteSearchAsync(CancellationToken cancellationToken)
    {
        if (_apiClient is null)
        {
            return;
        }

        IsLoading = true;
        HasError = false;
        ErrorMessage = "";

        try
        {
            var query = new ModrinthSearchQuery
            {
                Query = SearchQuery,
                ProjectType = _searchContext.LockProjectTypeToMods ? ModrinthProjectType.Mod : SelectedProjectType,
                SortOrder = SelectedSortOrder,
                Offset = (CurrentPage - 1) * PageSize,
                Limit = PageSize,
                FacetGroups = BuildFacetGroups(),
            };

            var response = await _apiClient.SearchProjectsAsync(query, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (response is null)
            {
                HasError = true;
                ErrorMessage = "Failed to fetch results from Modrinth. Please try again.";
                SearchResults.Clear();
                return;
            }

            SearchResults.Clear();
            foreach (var hit in response.Hits)
            {
                var item = new ModrinthSearchResultItemViewModel(
                    hit,
                    string.Equals(hit.ProjectType, "mod", StringComparison.OrdinalIgnoreCase));
                SearchResults.Add(item);
            }

            ApplyTargetSnapshot(_targetSnapshot);

            TotalPages = Math.Max(1, (int)Math.Ceiling(response.TotalHits / (double)PageSize));

            _logger?.LogInformation(
                "Search completed: {ResultCount} results, page {CurrentPage}/{TotalPages}",
                response.TotalHits,
                CurrentPage,
                TotalPages);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during Modrinth search");
            HasError = true;
            ErrorMessage = "An error occurred while searching. Please try again.";
            SearchResults.Clear();
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        // User is typing. Reset the timer.
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    partial void OnSelectedProjectTypeChanged(ModrinthProjectType value)
    {
        if (_searchContext.LockProjectTypeToMods)
        {
            if (value != ModrinthProjectType.Mod)
            {
                SelectedProjectType = ModrinthProjectType.Mod;
            }

            return;
        }

        // Filters change immediately triggers search?
        // Usually yes, but we should also cancel pending debounce to be safe.
        _debounceTimer.Stop();

        // Reset to page 1 and trigger search when filter changes
        if (_apiClient != null)
        {
            SearchCommand.Execute(null);
        }
    }

    partial void OnSelectedSortOrderChanged(string value)
    {
        // Sort order change immediately triggers search
        _debounceTimer.Stop();

        // Reset to page 1 and trigger search when sort order changes
        if (_apiClient != null)
        {
            SearchCommand.Execute(null);
        }
    }

    private ReadOnlyCollection<IReadOnlyList<string>>? BuildFacetGroups()
    {
        var groups = new List<IReadOnlyList<string>>();
        if (_searchContext.TargetInstance is not null)
        {
            var loaderFacet = _searchContext.TargetInstance.ModLoader switch
            {
                MinecraftInstanceModLoader.Fabric => "categories:fabric",
                MinecraftInstanceModLoader.Forge => "categories:forge",
                MinecraftInstanceModLoader.NeoForge => "categories:neoforge",
                _ => "",
            };

            if (!string.IsNullOrWhiteSpace(loaderFacet))
            {
                groups.Add([loaderFacet]);
            }

            groups.Add([$"versions:{_searchContext.TargetInstance.VersionId}"]);
            groups.Add(["client_side:required", "client_side:optional", "client_side:unknown"]);
        }

        return groups.Count == 0 ? null : groups.AsReadOnly();
    }

    public void Dispose()
    {
        _currentSearchCts?.Cancel();
        _currentSearchCts?.Dispose();
        _debounceTimer.Stop();
        _debounceTimer.Tick -= OnDebounceTimerTick;
        GC.SuppressFinalize(this);
    }

    public void ApplyTargetSnapshot(InstanceModsSnapshot? snapshot)
    {
        _targetSnapshot = snapshot;
        var isInstanceScopedSearch = _searchContext.TargetInstance is not null;

        foreach (var result in SearchResults)
        {
            InstanceInstalledProjectState? state = null;
            if (isInstanceScopedSearch && snapshot is not null)
            {
                snapshot.ProjectsById.TryGetValue(result.ProjectId, out state);
            }

            result.ApplyInstallState(isInstanceScopedSearch, state);
        }
    }
}
