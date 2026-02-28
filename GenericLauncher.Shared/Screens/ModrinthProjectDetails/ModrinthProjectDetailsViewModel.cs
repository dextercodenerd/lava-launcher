using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenericLauncher.Modrinth;
using GenericLauncher.Modrinth.Json;
using GenericLauncher.Navigation;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Screens.ModrinthProjectDetails;

public partial class ModrinthProjectDetailsViewModel : ViewModelBase, IPageViewModel
{
    private readonly ModrinthApiClient? _apiClient;
    private readonly string _projectId;
    private readonly ILogger? _logger;

    [ObservableProperty] private ModrinthProject _project;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = "";

    // IPageViewModel implementation
    public string Title => Project?.Title ?? "Project Details";

    // Design-time constructor
    public ModrinthProjectDetailsViewModel() : this(
        new ModrinthSearchResult("", "", "Project Title", "Description", [], "mod", 0, null, "", "", ""),
        null)
    {
    }

    public ModrinthProjectDetailsViewModel(
        ModrinthSearchResult searchResult,
        ModrinthApiClient? apiClient,
        ILogger? logger = null)
    {
        _projectId = searchResult.ProjectId;
        _apiClient = apiClient;
        _logger = logger;

        // Initialize with partial data from search result
        _project = new ModrinthProject(
            searchResult.ProjectId,
            searchResult.Slug,
            searchResult.ProjectType,
            searchResult.Title,
            searchResult.Description,
            "", // Body not available yet
            searchResult.Categories,
            "",
            "", // Client/Server side unknown
            searchResult.Downloads,
            0, // Followers unknown
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
            [] // License, links, versions unknown
        );

        // Load full project details
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

            Project = project;
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

    partial void OnProjectChanged(ModrinthProject value)
    {
        OnPropertyChanged(nameof(Title));
    }
}