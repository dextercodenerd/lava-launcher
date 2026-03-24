using CommunityToolkit.Mvvm.ComponentModel;
using GenericLauncher.InstanceMods;
using GenericLauncher.Modrinth.Json;

namespace GenericLauncher.Screens.ModrinthSearch;

public partial class ModrinthSearchResultItemViewModel : ObservableObject
{
    public ModrinthSearchResultItemViewModel(ModrinthSearchResult searchResult, bool canInstall)
    {
        SearchResult = searchResult;
        CanInstall = canInstall;
        ShowInstallButton = canInstall;
    }

    public ModrinthSearchResult SearchResult { get; }
    public bool CanInstall { get; }

    public string ProjectId => SearchResult.ProjectId;
    public string Title => SearchResult.Title;
    public string Description => SearchResult.Description;
    public string[] Categories => SearchResult.Categories;
    public string ProjectType => SearchResult.ProjectType;
    public int Downloads => SearchResult.Downloads;
    public string? IconUrl => SearchResult.IconUrl;

    [ObservableProperty] private bool _showInstallButton;
    [ObservableProperty] private bool _showUpdateButton;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _installedVersionNumber;
    [ObservableProperty] private string? _availableUpdateVersionNumber;

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public void ApplyInstallState(bool isInstanceScopedSearch, InstanceInstalledProjectState? state)
    {
        if (!CanInstall)
        {
            ShowInstallButton = false;
            ShowUpdateButton = false;
            StatusText = "";
            InstalledVersionNumber = null;
            AvailableUpdateVersionNumber = null;
            return;
        }

        if (!isInstanceScopedSearch || state is null)
        {
            ShowInstallButton = true;
            ShowUpdateButton = false;
            StatusText = "";
            InstalledVersionNumber = null;
            AvailableUpdateVersionNumber = null;
            return;
        }

        InstalledVersionNumber = state.InstalledVersionNumber;
        AvailableUpdateVersionNumber = state.LatestVersionNumber;
        ShowInstallButton = false;
        ShowUpdateButton = state.InstallKind == InstanceModItemKind.Direct && state.HasUpdate;

        if (state.IsBroken)
        {
            StatusText = state.InstallKind == InstanceModItemKind.Dependency
                ? $"Dependency missing ({state.InstalledVersionNumber})"
                : $"Managed install missing ({state.InstalledVersionNumber})";
            return;
        }

        StatusText = state.InstallKind == InstanceModItemKind.Dependency
            ? $"Installed as dependency {state.InstalledVersionNumber}"
            : state.HasUpdate && !string.IsNullOrWhiteSpace(state.LatestVersionNumber)
                ? $"Installed {state.InstalledVersionNumber}. Update {state.LatestVersionNumber}"
                : $"Installed {state.InstalledVersionNumber}";
    }

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatusText));
    }
}
