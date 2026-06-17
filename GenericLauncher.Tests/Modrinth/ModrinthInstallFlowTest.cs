using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GenericLauncher.Database.Model;
using GenericLauncher.InstanceMods;
using GenericLauncher.InstanceMods.Json;
using GenericLauncher.Misc;
using GenericLauncher.Modrinth;
using GenericLauncher.Modrinth.Json;
using GenericLauncher.Screens.ModrinthSearch;
using Xunit;

namespace GenericLauncher.Tests.Modrinth;

public class ModrinthInstallFlowTest
{
    [Fact]
    public void ModrinthSearchQuery_BuildFacetsJson_IncludesProjectTypeAndLockedFilters()
    {
        var query = new ModrinthSearchQuery(
            ProjectType: ModrinthProjectType.Mod,
            FacetGroups:
            [
                ["categories:fabric"],
                ["versions:1.21.1"],
                ["client_side:required", "client_side:optional", "client_side:unknown"],
            ]);

        var json = query.BuildFacetsJson();

        Assert.Equal(
            "[[\"project_type:mod\"],[\"categories:fabric\"],[\"versions:1.21.1\"],[\"client_side:required\",\"client_side:optional\",\"client_side:unknown\"]]",
            json);
    }

    [Fact]
    public void ModrinthSearchViewModel_SelectedSortOrder_ComesFromSelectedOptionValue()
    {
        var viewModel = new ModrinthSearchViewModel();
        var downloads = viewModel.SortOrderOptions.Single(option => option.DisplayName == "Downloads");

        viewModel.SelectedSortOrderOption = downloads;

        Assert.Equal("downloads", viewModel.SelectedSortOrder);
    }

    [Fact]
    public async Task ModrinthApiClient_SearchProjectsAsync_UsesSelectedSortOrderValueForIndex()
    {
        Uri? requestedUri = null;
        using var handler = new RefreshGateTestSupport.RoutingHttpMessageHandler((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(
                RefreshGateTestSupport.JsonResponse(
                    new ModrinthSearchResponse([], 0, 20, 0),
                    ModrinthJsonContext.Default.ModrinthSearchResponse));
        });
        using var httpClient = new HttpClient(handler);
        var apiClient = new ModrinthApiClient(httpClient);

        var response = await apiClient.SearchProjectsAsync(
            new ModrinthSearchQuery(
                Query: "lava",
                SortOrder: "downloads"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.NotNull(requestedUri);
        Assert.Equal("/v2/search", requestedUri!.AbsolutePath);
        Assert.Contains("query=lava", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("index=downloads", requestedUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModrinthSearchViewModel_RetrySearchAsync_RerunsFailedSearch()
    {
        var requestCount = 0;
        using var handler = new RefreshGateTestSupport.RoutingHttpMessageHandler((request, _) =>
        {
            requestCount++;
            return Task.FromResult(
                requestCount == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : RefreshGateTestSupport.JsonResponse(
                        new ModrinthSearchResponse(
                            [RefreshGateTestSupport.CreateSearchResult("alpha", "Alpha")],
                            1,
                            20,
                            1),
                        ModrinthJsonContext.Default.ModrinthSearchResponse));
        });
        using var httpClient = new HttpClient(handler);
        var viewModel = new ModrinthSearchViewModel(
            new ModrinthApiClient(httpClient),
            null,
            ModrinthSearchContext.CreateRoot(),
            null);

        await viewModel.SearchCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasError);
        Assert.Equal(1, requestCount);
        Assert.Empty(viewModel.SearchResults);

        await viewModel.RetrySearchCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasError);
        Assert.Equal(2, requestCount);
        Assert.Single(viewModel.SearchResults);
    }

    [Fact]
    public void InstanceModsManager_SelectBestVersion_PrefersReleaseBeforeNewerBeta()
    {
        var versions = new[]
        {
            new ModrinthVersion("beta", "project", "Beta", "2.0.0-beta", "beta", UtcInstant.Parse("2026-03-20T00:00:00Z"), ["fabric"], ["1.21.1"], [], []),
            new ModrinthVersion("release", "project", "Release", "1.9.0", "release", UtcInstant.Parse("2026-03-18T00:00:00Z"), ["fabric"], ["1.21.1"], [], []),
            new ModrinthVersion("older-release", "project", "Older", "1.8.0", "release", UtcInstant.Parse("2026-03-10T00:00:00Z"), ["fabric"], ["1.21.1"], [], []),
        };

        var selected = InstanceModsManager.SelectBestVersion(versions);

        Assert.Equal("release", selected.Id);
    }

    [Fact]
    public void InstanceModsManager_SelectInstallFile_PrefersPrimaryJar()
    {
        var version = new ModrinthVersion(
            "version",
            "project",
            "Test",
            "1.0.0",
            "release",
            UtcInstant.Parse("2026-03-20T00:00:00Z"),
            ["fabric"],
            ["1.21.1"],
            [],
            [
                new ModrinthVersionFile(new ModrinthFileHashes("sha512-a", "sha1-a"), "https://example/a-sources.jar", "a-sources.jar", false, 12, "sources-jar"),
                new ModrinthVersionFile(new ModrinthFileHashes("sha512-b", "sha1-b"), "https://example/a.jar", "a.jar", true, 24, null),
                new ModrinthVersionFile(new ModrinthFileHashes("sha512-c", "sha1-c"), "https://example/b.jar", "b.jar", false, 24, null),
            ]);

        var file = InstanceModsManager.SelectInstallFile(version);

        Assert.Equal("a.jar", file.Filename);
    }

    [Fact]
    public void InstanceMetaJson_RoundTripsPortableMetadata()
    {
        var meta = new InstanceMeta(
            1,
            "Family Pack",
            "1.21.1",
            MinecraftInstance.ModLoaderToString(MinecraftInstanceModLoader.Fabric),
            "0.16.10",
            "1.21.1-fabric",
            [
                new InstanceMetaMod(
                    "proj",
                    "sodium",
                    "Sodium",
                    "ver",
                    "1.0.0",
                    "release",
                    "sodium.jar",
                    "abc",
                    "Dependency",
                    ["parent"],
                    new DateTime(2026, 3, 23, 0, 0, 0, DateTimeKind.Utc)),
            ]);

        var json = JsonSerializer.Serialize(meta, InstanceMetaJsonContext.Default.InstanceMeta);
        var roundTrip = JsonSerializer.Deserialize(json, InstanceMetaJsonContext.Default.InstanceMeta);

        Assert.NotNull(roundTrip);
        Assert.Equal("Family Pack", roundTrip!.DisplayName);
        Assert.Equal("1.21.1", roundTrip.MinecraftVersionId);
        Assert.Contains("\"display_name\":\"Family Pack\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("parent", roundTrip.Mods[0].RequiredByProjectIds[0]);
    }

    [Fact]
    public void ModrinthSearchResultItemViewModel_ApplyInstallState_ShowsInstallWhenMissing()
    {
        var item = new ModrinthSearchResultItemViewModel(
            new ModrinthSearchResult("project", "slug", "Title", "Desc", [], "mod", 0, null, "", UtcInstant.UnixEpoch, UtcInstant.UnixEpoch),
            canInstall: true);

        item.ApplyInstallState(isInstanceScopedSearch: true, state: null, latestCompatibleVersion: null);

        Assert.True(item.ShowInstallButton);
        Assert.False(item.ShowUpdateButton);
        Assert.False(item.HasStatusText);
    }

    [Fact]
    public void ModrinthSearchResultItemViewModel_ApplyInstallState_ShowsUpdateForOutdatedDirectMod()
    {
        var item = new ModrinthSearchResultItemViewModel(
            new ModrinthSearchResult("project", "slug", "Title", "Desc", [], "mod", 0, null, "", UtcInstant.UnixEpoch, UtcInstant.UnixEpoch),
            canInstall: true);

        item.ApplyInstallState(
            isInstanceScopedSearch: true,
            new InstanceInstalledProjectState(
                "project",
                "Title",
                "version-1",
                "1.0.0",
                InstanceModItemKind.Direct,
                IsBroken: false),
            new LatestCompatibleVersionInfo("project", "version-2", "1.1.0"));

        Assert.False(item.ShowInstallButton);
        Assert.True(item.ShowUpdateButton);
        Assert.Contains("1.1.0", item.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ModrinthSearchResultItemViewModel_ApplyInstallState_HidesUpdateForCurrentDirectMod()
    {
        var item = new ModrinthSearchResultItemViewModel(
            new ModrinthSearchResult("project", "slug", "Title", "Desc", [], "mod", 0, null, "", UtcInstant.UnixEpoch, UtcInstant.UnixEpoch),
            canInstall: true);

        item.ApplyInstallState(
            isInstanceScopedSearch: true,
            new InstanceInstalledProjectState(
                "project",
                "Title",
                "version-1",
                "1.0.0",
                InstanceModItemKind.Direct,
                IsBroken: false),
            new LatestCompatibleVersionInfo("project", "version-1", "1.0.0"));

        Assert.False(item.ShowInstallButton);
        Assert.False(item.ShowUpdateButton);
        Assert.Equal("Installed 1.0.0", item.StatusText);
    }

    [Fact]
    public void ModrinthSearchResultItemViewModel_ApplyInstallState_HidesManualActionsForDependency()
    {
        var item = new ModrinthSearchResultItemViewModel(
            new ModrinthSearchResult("project", "slug", "Title", "Desc", [], "mod", 0, null, "", UtcInstant.UnixEpoch, UtcInstant.UnixEpoch),
            canInstall: true);

        item.ApplyInstallState(
            isInstanceScopedSearch: true,
            new InstanceInstalledProjectState(
                "project",
                "Title",
                "version-1",
                "1.0.0",
                InstanceModItemKind.Dependency,
                IsBroken: false),
            latestCompatibleVersion: null);

        Assert.False(item.ShowInstallButton);
        Assert.False(item.ShowUpdateButton);
        Assert.Contains("dependency", item.StatusText, StringComparison.OrdinalIgnoreCase);
    }
}
