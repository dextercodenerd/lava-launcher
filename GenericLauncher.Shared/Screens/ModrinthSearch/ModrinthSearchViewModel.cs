using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenericLauncher.Modrinth;
using GenericLauncher.Modrinth.Json;
using GenericLauncher.Navigation;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Screens.ModrinthSearch;

public partial class ModrinthSearchViewModel : ViewModelBase, IPageViewModel
{
    private readonly ModrinthApiClient? _apiClient;
    private readonly Action<ModrinthSearchResult>? _openProjectDetails;
    private readonly ILogger? _logger;

    private readonly DispatcherTimer _debounceTimer;
    private CancellationTokenSource? _currentSearchCts;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private ModrinthProjectType _selectedProjectType = ModrinthProjectType.All;
    [ObservableProperty] private string _selectedSortOrder = "relevance";
    [ObservableProperty] private ObservableCollection<ModrinthSearchResult> _searchResults = [];
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalPages = 1;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = "";

    private const int PageSize = 20;

    // IPageViewModel implementation
    public string Title => "Modrinth Search";

    // Design-time constructor
    public ModrinthSearchViewModel()
    {
        _debounceTimer = new DispatcherTimer();
    }

    public ModrinthSearchViewModel(
        ModrinthApiClient? apiClient,
        Action<ModrinthSearchResult>? openProjectDetails,
        ILogger? logger = null)
    {
        _apiClient = apiClient;
        _openProjectDetails = openProjectDetails;
        _logger = logger;

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
    private void OpenProjectDetails(ModrinthSearchResult result)
    {
        _openProjectDetails?.Invoke(result);
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
                ProjectType = SelectedProjectType,
                SortOrder = SelectedSortOrder,
                Offset = (CurrentPage - 1) * PageSize,
                Limit = PageSize,
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
                SearchResults.Add(hit);
            }

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
}
