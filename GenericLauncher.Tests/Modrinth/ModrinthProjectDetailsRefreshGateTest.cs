using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Database.Model;
using GenericLauncher.InstanceMods;
using GenericLauncher.Modrinth.Json;
using GenericLauncher.Screens.ModrinthProjectDetails;
using GenericLauncher.Screens.ModrinthSearch;
using Xunit;

namespace GenericLauncher.Tests.Modrinth;

[Collection("AvaloniaDispatcher")]
public sealed class ModrinthProjectDetailsRefreshGateTest
{
    [Fact]
    public async Task ConstructorLoad_DoesNotReapplyStaleProjectStateAfterInstanceChange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        await RefreshGateTestSupport.WriteManagedModsAsync(
            fixture,
            RefreshGateTestSupport.CreateManagedMod(
                "alpha",
                "Alpha",
                "alpha-1",
                "1.0.0",
                "alpha.jar",
                "Direct",
                [],
                [1, 2, 3]));

        var firstRequestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestVersion = RefreshGateTestSupport.CreateVersion("alpha-2", "alpha", "1.1.0", "alpha-2.jar", [2, 3, 4]);
        using var handler = new RefreshGateTestSupport.RoutingHttpMessageHandler(async (request, token) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v2/project/alpha/version")
            {
                firstRequestEntered.TrySetResult();
                await releaseFirstRequest.Task.WaitAsync(token);
                firstRequestReturned.TrySetResult();
                return RefreshGateTestSupport.JsonResponse(
                    [latestVersion],
                    ModrinthJsonContext.Default.ModrinthVersionArray);
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, handler);
        var viewModel = new ModrinthProjectDetailsViewModel(
            RefreshGateTestSupport.CreateSearchResult("alpha", "Alpha"),
            null,
            manager,
            ModrinthSearchContext.CreateForInstance(fixture.Instance));

        await firstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await RefreshGateTestSupport.DrainUiAsync();

        await manager.DeleteModAsync(fixture.Instance, "alpha", cancellationToken);
        await RefreshGateTestSupport.DrainUiAsync();

        releaseFirstRequest.TrySetResult();
        await firstRequestReturned.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await RefreshGateTestSupport.WaitUntilAsync(
            () => viewModel.TargetProjectState is null,
            cancellationToken);

        Assert.Null(viewModel.TargetProjectState);
        Assert.True(viewModel.ShowInstallAction);
        Assert.False(viewModel.ShowUpdateAction);
    }

    [Fact]
    public async Task NewerLatestVersionRefresh_WinsWhenOlderRefreshCompletesLast()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        var installedVersionBytes = new byte[] { 2, 3, 4 };
        var latestVersionBytes = new byte[] { 3, 4, 5 };
        await RefreshGateTestSupport.WriteManagedModsAsync(
            fixture,
            RefreshGateTestSupport.CreateManagedMod(
                "alpha",
                "Alpha",
                "alpha-1",
                "1.0.0",
                "alpha.jar",
                "Direct",
                [],
                [1, 2, 3]));

        var initialRefreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateResolutionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventRefreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installedVersion = RefreshGateTestSupport.CreateVersion(
            "alpha-2",
            "alpha",
            "1.1.0",
            "alpha-2.jar",
            installedVersionBytes);
        var latestVersion = RefreshGateTestSupport.CreateVersion(
            "alpha-3",
            "alpha",
            "1.2.0",
            "alpha-3.jar",
            latestVersionBytes);
        var versionRequestCount = 0;
        using var handler = new RefreshGateTestSupport.RoutingHttpMessageHandler(async (request, token) =>
        {
            switch (request.RequestUri?.AbsolutePath)
            {
                case "/v2/project/alpha/version":
                    var requestOrdinal = Interlocked.Increment(ref versionRequestCount);
                    if (requestOrdinal == 1)
                    {
                        initialRefreshEntered.TrySetResult();
                        await releaseFirstRequest.Task.WaitAsync(token);
                        firstRequestReturned.TrySetResult();
                        return RefreshGateTestSupport.JsonResponse(
                            [installedVersion],
                            ModrinthJsonContext.Default.ModrinthVersionArray);
                    }

                    if (requestOrdinal == 2)
                    {
                        updateResolutionEntered.TrySetResult();
                        return RefreshGateTestSupport.JsonResponse(
                            [installedVersion],
                            ModrinthJsonContext.Default.ModrinthVersionArray);
                    }

                    if (requestOrdinal == 3)
                    {
                        eventRefreshEntered.TrySetResult();
                        return RefreshGateTestSupport.JsonResponse(
                            [latestVersion],
                            ModrinthJsonContext.Default.ModrinthVersionArray);
                    }

                    break;
                // GetVersionAsync is now called by the converged install path after
                // obtaining the version ID from the compatible-versions lookup result.
                case "/v2/version/alpha-2":
                    return RefreshGateTestSupport.JsonResponse(
                        installedVersion,
                        ModrinthJsonContext.Default.ModrinthVersion);
                case "/v2/project/alpha":
                    return RefreshGateTestSupport.JsonResponse(
                        RefreshGateTestSupport.CreateProject("alpha", "Alpha"),
                        ModrinthJsonContext.Default.ModrinthProject);
                case "/alpha-2.jar":
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(installedVersionBytes),
                    };
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, handler);
        var viewModel = new ModrinthProjectDetailsViewModel(
            RefreshGateTestSupport.CreateSearchResult("alpha", "Alpha"),
            null,
            manager,
            ModrinthSearchContext.CreateForInstance(fixture.Instance));

        await initialRefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await RefreshGateTestSupport.DrainUiAsync();

        var updateTask = manager.UpdateModAsync(fixture.Instance, "alpha", cancellationToken);
        await updateResolutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await eventRefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await updateTask;
        await RefreshGateTestSupport.WaitUntilAsync(
            () => string.Equals(viewModel.TargetProjectState?.InstalledVersionNumber, "1.1.0", StringComparison.Ordinal)
                  && string.Equals(viewModel.TargetLatestCompatibleVersion?.VersionNumber, "1.2.0",
                      StringComparison.Ordinal),
            cancellationToken);
        await RefreshGateTestSupport.DrainUiAsync();

        Assert.Equal("1.2.0", viewModel.TargetLatestCompatibleVersion?.VersionNumber);
        Assert.True(viewModel.ShowUpdateAction);
        Assert.Contains("1.2.0", viewModel.TargetStateText, StringComparison.Ordinal);
        Assert.Equal("1.1.0", viewModel.TargetProjectState?.InstalledVersionNumber);

        releaseFirstRequest.TrySetResult();
        await firstRequestReturned.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await RefreshGateTestSupport.WaitUntilAsync(
            () => string.Equals(viewModel.TargetProjectState?.InstalledVersionNumber, "1.1.0", StringComparison.Ordinal)
                  && string.Equals(viewModel.TargetLatestCompatibleVersion?.VersionNumber, "1.2.0",
                      StringComparison.Ordinal),
            cancellationToken);
        await RefreshGateTestSupport.DrainUiAsync();

        Assert.Equal("1.2.0", viewModel.TargetLatestCompatibleVersion?.VersionNumber);
        Assert.True(viewModel.ShowUpdateAction);
        Assert.Contains("1.2.0", viewModel.TargetStateText, StringComparison.Ordinal);
        Assert.Equal("1.1.0", viewModel.TargetProjectState?.InstalledVersionNumber);
    }

    [Fact]
    public async Task StaleMissingLatestVersionResult_DoesNotClearNewerProjectUpdateState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        var installedVersionBytes = new byte[] { 2, 3, 4 };
        var latestVersionBytes = new byte[] { 3, 4, 5 };
        await RefreshGateTestSupport.WriteManagedModsAsync(
            fixture,
            RefreshGateTestSupport.CreateManagedMod(
                "alpha",
                "Alpha",
                "alpha-1",
                "1.0.0",
                "alpha.jar",
                "Direct",
                [],
                [1, 2, 3]));

        var initialRefreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateResolutionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventRefreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installedVersion = RefreshGateTestSupport.CreateVersion(
            "alpha-2",
            "alpha",
            "1.1.0",
            "alpha-2.jar",
            installedVersionBytes);
        var latestVersion = RefreshGateTestSupport.CreateVersion(
            "alpha-3",
            "alpha",
            "1.2.0",
            "alpha-3.jar",
            latestVersionBytes);
        var versionRequestCount = 0;
        using var handler = new RefreshGateTestSupport.RoutingHttpMessageHandler(async (request, token) =>
        {
            switch (request.RequestUri?.AbsolutePath)
            {
                case "/v2/project/alpha/version":
                    var requestOrdinal = Interlocked.Increment(ref versionRequestCount);
                    if (requestOrdinal == 1)
                    {
                        initialRefreshEntered.TrySetResult();
                        await releaseFirstRequest.Task.WaitAsync(token);
                        firstRequestReturned.TrySetResult();
                        return RefreshGateTestSupport.JsonResponse(
                            Array.Empty<ModrinthVersion>(),
                            ModrinthJsonContext.Default.ModrinthVersionArray);
                    }

                    if (requestOrdinal == 2)
                    {
                        updateResolutionEntered.TrySetResult();
                        return RefreshGateTestSupport.JsonResponse(
                            [installedVersion],
                            ModrinthJsonContext.Default.ModrinthVersionArray);
                    }

                    if (requestOrdinal == 3)
                    {
                        eventRefreshEntered.TrySetResult();
                        return RefreshGateTestSupport.JsonResponse(
                            [latestVersion],
                            ModrinthJsonContext.Default.ModrinthVersionArray);
                    }

                    break;
                // GetVersionAsync is now called by the converged install path after
                // obtaining the version ID from the compatible-versions lookup result.
                case "/v2/version/alpha-2":
                    return RefreshGateTestSupport.JsonResponse(
                        installedVersion,
                        ModrinthJsonContext.Default.ModrinthVersion);
                case "/v2/project/alpha":
                    return RefreshGateTestSupport.JsonResponse(
                        RefreshGateTestSupport.CreateProject("alpha", "Alpha"),
                        ModrinthJsonContext.Default.ModrinthProject);
                case "/alpha-2.jar":
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(installedVersionBytes),
                    };
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, handler);
        var viewModel = new ModrinthProjectDetailsViewModel(
            RefreshGateTestSupport.CreateSearchResult("alpha", "Alpha"),
            null,
            manager,
            ModrinthSearchContext.CreateForInstance(fixture.Instance));

        await initialRefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await RefreshGateTestSupport.DrainUiAsync();

        var updateTask = manager.UpdateModAsync(fixture.Instance, "alpha", cancellationToken);
        await updateResolutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await eventRefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await updateTask;
        await RefreshGateTestSupport.WaitUntilAsync(
            () => string.Equals(viewModel.TargetProjectState?.InstalledVersionNumber, "1.1.0", StringComparison.Ordinal)
                  && string.Equals(viewModel.TargetLatestCompatibleVersion?.VersionNumber, "1.2.0",
                      StringComparison.Ordinal),
            cancellationToken);
        await RefreshGateTestSupport.DrainUiAsync();

        Assert.Equal("1.2.0", viewModel.TargetLatestCompatibleVersion?.VersionNumber);
        Assert.True(viewModel.ShowUpdateAction);
        Assert.Equal("1.1.0", viewModel.TargetProjectState?.InstalledVersionNumber);

        releaseFirstRequest.TrySetResult();
        await firstRequestReturned.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await RefreshGateTestSupport.WaitUntilAsync(
            () => string.Equals(viewModel.TargetProjectState?.InstalledVersionNumber, "1.1.0", StringComparison.Ordinal)
                  && string.Equals(viewModel.TargetLatestCompatibleVersion?.VersionNumber, "1.2.0",
                      StringComparison.Ordinal),
            cancellationToken);
        await RefreshGateTestSupport.DrainUiAsync();

        Assert.Equal("1.2.0", viewModel.TargetLatestCompatibleVersion?.VersionNumber);
        Assert.True(viewModel.ShowUpdateAction);
        Assert.Equal("1.1.0", viewModel.TargetProjectState?.InstalledVersionNumber);
    }

    [Fact]
    public async Task RefreshTargetLatestCompatibleVersionAsync_UnavailableResultShowsQuietMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();

        using var handler = new RefreshGateTestSupport.RoutingHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath != "/v2/project/alpha/version")
            {
                throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, handler);
        var viewModel = new ModrinthProjectDetailsViewModel(
            RefreshGateTestSupport.CreateSearchResult("alpha", "Alpha"),
            null,
            manager,
            ModrinthSearchContext.CreateForInstance(fixture.Instance));

        await RefreshGateTestSupport.WaitUntilAsync(
            () => viewModel.TargetStateText == "Compatibility status unavailable.",
            cancellationToken);

        Assert.Equal("Compatibility status unavailable.", viewModel.TargetStateText);
        Assert.False(viewModel.ShowUpdateAction);
    }

    [Fact]
    public async Task RefreshTargetLatestCompatibleVersionAsync_StaleResultMentionsCachedData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        await RefreshGateTestSupport.WriteManagedModsAsync(
            fixture,
            RefreshGateTestSupport.CreateManagedMod("alpha", "Alpha", "alpha-1", "1.0.0", "alpha.jar", "Direct", [], [1, 2, 3]));

        var shouldFail = false;
        using var handler = new RefreshGateTestSupport.RoutingHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri?.AbsolutePath != "/v2/project/alpha/version")
            {
                throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
            }

            return Task.FromResult(
                shouldFail
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : RefreshGateTestSupport.JsonResponse(
                        [RefreshGateTestSupport.CreateVersion("alpha-2", "alpha", "1.1.0", "alpha-2.jar", [4, 5, 6])],
                        ModrinthJsonContext.Default.ModrinthVersionArray));
        });
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, handler);
        await manager.GetLatestCompatibleVersionsAsync(fixture.Instance, ["alpha"], cancellationToken: cancellationToken);
        ExpireCacheEntry(manager, fixture.Instance, "alpha");
        shouldFail = true;

        var viewModel = new ModrinthProjectDetailsViewModel(
            RefreshGateTestSupport.CreateSearchResult("alpha", "Alpha"),
            null,
            manager,
            ModrinthSearchContext.CreateForInstance(fixture.Instance));

        await RefreshGateTestSupport.WaitUntilAsync(
            () => viewModel.TargetStateText.Contains("cached 1.1.0", StringComparison.Ordinal),
            cancellationToken);

        Assert.Contains("cached 1.1.0", viewModel.TargetStateText, StringComparison.Ordinal);
        Assert.True(viewModel.ShowUpdateAction);
    }

    private static void ExpireCacheEntry(InstanceModsManager manager, MinecraftInstance instance, string projectId)
    {
        var cacheKey = (string)typeof(InstanceModsManager)
            .GetMethod("GetCompatibleVersionsCacheKey", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [instance, projectId])!;

        var cacheField = typeof(InstanceModsManager)
            .GetField("_compatibleVersionsCache", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cache = cacheField.GetValue(manager)!;

        var tryGetValueMethod = cache.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { cacheKey, null };
        if (!(bool)tryGetValueMethod.Invoke(cache, args)!)
        {
            return;
        }

        var entry = args[1]!;
        var entryType = entry.GetType();
        var expiredAt = DateTime.UtcNow - TimeSpan.FromHours(1);
        var updatedEntry = Activator.CreateInstance(
            entryType,
            entryType.GetProperty("Versions", BindingFlags.Public | BindingFlags.Instance)!.GetValue(entry),
            expiredAt,
            expiredAt,
            entryType.GetProperty("LastRefreshAttemptAtUtc", BindingFlags.Public | BindingFlags.Instance)!.GetValue(entry),
            entryType.GetProperty("RefreshState", BindingFlags.Public | BindingFlags.Instance)!.GetValue(entry))!;
        cache.GetType()
            .GetProperty("Item")!
            .SetValue(cache, updatedEntry, [cacheKey]);
    }
}
