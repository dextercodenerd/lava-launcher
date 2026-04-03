using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Database.Model;
using GenericLauncher.InstanceMods;
using GenericLauncher.Modrinth.Json;
using GenericLauncher.Screens.InstanceDetails;
using Xunit;

namespace GenericLauncher.Tests.Modrinth;

[CollectionDefinition("AvaloniaDispatcher", DisableParallelization = true)]
public sealed class AvaloniaDispatcherCollection;

[Collection("AvaloniaDispatcher")]
public sealed class InstanceDetailsRefreshGateTest
{
    [Fact]
    public async Task LoadModsStateAsync_DoesNotReapplyStaleSnapshotAfterInstanceChange()
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
        var latestVersion = RefreshGateTestSupport.CreateVersion("alpha-2", "alpha", "1.1.0", "alpha-2.jar", [2, 3, 4]);
        using var handler = new RefreshGateTestSupport.RoutingHttpMessageHandler(async (request, token) =>
        {
            if (request.RequestUri?.AbsolutePath == "/v2/project/alpha/version")
            {
                firstRequestEntered.TrySetResult();
                await releaseFirstRequest.Task.WaitAsync(token);
                return RefreshGateTestSupport.JsonResponse(
                    [latestVersion],
                    ModrinthJsonContext.Default.ModrinthVersionArray);
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, handler);
        var viewModel = new InstanceDetailsViewModel(fixture.Instance, null, null, manager, null, null, null);

        var loadTask = RefreshGateTestSupport.InvokeNonPublicTaskAsync(viewModel, "LoadModsStateAsync", false);
        await firstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await RefreshGateTestSupport.DrainUiAsync();

        await manager.DeleteModAsync(fixture.Instance, "alpha", cancellationToken);
        await RefreshGateTestSupport.DrainUiAsync();

        releaseFirstRequest.TrySetResult();
        await loadTask;
        await RefreshGateTestSupport.WaitUntilAsync(
            () => viewModel.InstalledMods.Count == 0
                  && viewModel.RequiredDependencies.Count == 0
                  && !viewModel.CanUpdateAll,
            cancellationToken);

        Assert.Empty(viewModel.InstalledMods);
        Assert.Empty(viewModel.RequiredDependencies);
        Assert.False(viewModel.CanUpdateAll);
    }

    [Fact]
    public async Task LoadModsStateAsync_NewerRefreshKeepsLatestVersionWhenOlderCompletesLast()
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
        var secondRequestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var olderVersion = RefreshGateTestSupport.CreateVersion("alpha-2", "alpha", "1.1.0", "alpha-2.jar", [2, 3, 4]);
        var newerVersion = RefreshGateTestSupport.CreateVersion("alpha-3", "alpha", "1.2.0", "alpha-3.jar", [3, 4, 5]);
        var requestCount = 0;
        using var handler = new RefreshGateTestSupport.RoutingHttpMessageHandler(async (request, token) =>
        {
            if (request.RequestUri?.AbsolutePath != "/v2/project/alpha/version")
            {
                throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
            }

            var requestOrdinal = Interlocked.Increment(ref requestCount);
            if (requestOrdinal == 1)
            {
                firstRequestEntered.TrySetResult();
                await releaseFirstRequest.Task.WaitAsync(token);
                firstRequestReturned.TrySetResult();
                return RefreshGateTestSupport.JsonResponse(
                    [olderVersion],
                    ModrinthJsonContext.Default.ModrinthVersionArray);
            }

            secondRequestEntered.TrySetResult();
            return RefreshGateTestSupport.JsonResponse(
                [newerVersion],
                ModrinthJsonContext.Default.ModrinthVersionArray);
        });
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, handler);
        var viewModel = new InstanceDetailsViewModel(fixture.Instance, null, null, manager, null, null, null);

        var firstLoadTask = RefreshGateTestSupport.InvokeNonPublicTaskAsync(viewModel, "LoadModsStateAsync", false);
        await firstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        var secondLoadTask = RefreshGateTestSupport.InvokeNonPublicTaskAsync(viewModel, "LoadModsStateAsync", true);
        await secondRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await secondLoadTask;
        await WaitForInstalledModAsync(viewModel, "1.2.0", cancellationToken);

        var currentItem = Assert.Single(viewModel.InstalledMods);
        Assert.True(currentItem.HasUpdate);
        Assert.Equal("1.2.0", currentItem.LatestVersionNumber);

        releaseFirstRequest.TrySetResult();
        await firstRequestReturned.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await firstLoadTask;
        await WaitForInstalledModAsync(viewModel, "1.2.0", cancellationToken);

        currentItem = Assert.Single(viewModel.InstalledMods);
        Assert.True(currentItem.HasUpdate);
        Assert.Equal("1.2.0", currentItem.LatestVersionNumber);
    }

    [Fact]
    public async Task LoadModsStateAsync_StaleMissingLatestVersionDoesNotClearNewerUpdateState()
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
        var secondRequestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerVersion = RefreshGateTestSupport.CreateVersion("alpha-3", "alpha", "1.2.0", "alpha-3.jar", [3, 4, 5]);
        var requestCount = 0;
        using var handler = new RefreshGateTestSupport.RoutingHttpMessageHandler(async (request, token) =>
        {
            if (request.RequestUri?.AbsolutePath != "/v2/project/alpha/version")
            {
                throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
            }

            var requestOrdinal = Interlocked.Increment(ref requestCount);
            if (requestOrdinal == 1)
            {
                firstRequestEntered.TrySetResult();
                await releaseFirstRequest.Task.WaitAsync(token);
                firstRequestReturned.TrySetResult();
                return RefreshGateTestSupport.JsonResponse(
                    Array.Empty<ModrinthVersion>(),
                    ModrinthJsonContext.Default.ModrinthVersionArray);
            }

            secondRequestEntered.TrySetResult();
            return RefreshGateTestSupport.JsonResponse(
                [newerVersion],
                ModrinthJsonContext.Default.ModrinthVersionArray);
        });
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, handler);
        var viewModel = new InstanceDetailsViewModel(fixture.Instance, null, null, manager, null, null, null);

        var firstLoadTask = RefreshGateTestSupport.InvokeNonPublicTaskAsync(viewModel, "LoadModsStateAsync", false);
        await firstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        var secondLoadTask = RefreshGateTestSupport.InvokeNonPublicTaskAsync(viewModel, "LoadModsStateAsync", true);
        await secondRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await secondLoadTask;
        await WaitForInstalledModAsync(viewModel, "1.2.0", cancellationToken);

        var currentItem = Assert.Single(viewModel.InstalledMods);
        Assert.True(currentItem.HasUpdate);
        Assert.Equal("1.2.0", currentItem.LatestVersionNumber);

        releaseFirstRequest.TrySetResult();
        await firstRequestReturned.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await firstLoadTask;
        await WaitForInstalledModAsync(viewModel, "1.2.0", cancellationToken);

        currentItem = Assert.Single(viewModel.InstalledMods);
        Assert.True(currentItem.HasUpdate);
        Assert.Equal("1.2.0", currentItem.LatestVersionNumber);
    }

    [Fact]
    public async Task LoadModsStateAsync_MixedPerProjectStatusesRenderPerRowMessages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        await RefreshGateTestSupport.WriteManagedModsAsync(
            fixture,
            RefreshGateTestSupport.CreateManagedMod("alpha", "Alpha", "alpha-1", "1.0.0", "alpha.jar", "Direct", [],
                [1, 2, 3]),
            RefreshGateTestSupport.CreateManagedMod("bravo", "Bravo", "bravo-1", "1.0.0", "bravo.jar", "Direct", [],
                [4, 5, 6]));

        using var handler = new RefreshGateTestSupport.RoutingHttpMessageHandler((request, _) =>
        {
            return request.RequestUri?.AbsolutePath switch
            {
                "/v2/project/alpha/version" => Task.FromResult(
                    RefreshGateTestSupport.JsonResponse(
                        [RefreshGateTestSupport.CreateVersion("alpha-2", "alpha", "1.1.0", "alpha-2.jar", [7, 8, 9])],
                        ModrinthJsonContext.Default.ModrinthVersionArray)),
                "/v2/project/bravo/version" => Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
            };
        });
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, handler);
        var viewModel = new InstanceDetailsViewModel(fixture.Instance, null, null, manager, null, null, null);

        await RefreshGateTestSupport.InvokeNonPublicTaskAsync(viewModel, "LoadModsStateAsync", false);
        await RefreshGateTestSupport.DrainUiAsync();

        var alpha = Assert.Single(viewModel.InstalledMods, item => item.ProjectId == "alpha");
        var bravo = Assert.Single(viewModel.InstalledMods, item => item.ProjectId == "bravo");

        Assert.True(alpha.HasUpdate);
        Assert.Equal("Update available: 1.1.0", alpha.UpdateStatusText);
        Assert.False(bravo.HasUpdate);
        Assert.Equal("Status unavailable.", bravo.UpdateStatusText);
        Assert.True(viewModel.CanUpdateAll);
    }

    [Fact]
    public async Task LoadModsStateAsync_OnlyOutdatedDirectModsExposeUpdateState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        await RefreshGateTestSupport.WriteManagedModsAsync(
            fixture,
            RefreshGateTestSupport.CreateManagedMod("alpha", "Alpha", "alpha-1", "1.0.0", "alpha.jar", "Direct", [],
                [1, 2, 3]),
            RefreshGateTestSupport.CreateManagedMod("bravo", "Bravo", "bravo-1", "1.0.0", "bravo.jar", "Direct", [],
                [4, 5, 6]));

        using var handler = new RefreshGateTestSupport.RoutingHttpMessageHandler((request, _) =>
        {
            return request.RequestUri?.AbsolutePath switch
            {
                "/v2/project/alpha/version" => Task.FromResult(
                    RefreshGateTestSupport.JsonResponse(
                        [RefreshGateTestSupport.CreateVersion("alpha-2", "alpha", "1.1.0", "alpha-2.jar", [7, 8, 9])],
                        ModrinthJsonContext.Default.ModrinthVersionArray)),
                "/v2/project/bravo/version" => Task.FromResult(
                    RefreshGateTestSupport.JsonResponse(
                        [RefreshGateTestSupport.CreateVersion("bravo-1", "bravo", "1.0.0", "bravo.jar", [4, 5, 6])],
                        ModrinthJsonContext.Default.ModrinthVersionArray)),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
            };
        });
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, handler);
        var viewModel = new InstanceDetailsViewModel(fixture.Instance, null, null, manager, null, null, null);

        await RefreshGateTestSupport.InvokeNonPublicTaskAsync(viewModel, "LoadModsStateAsync", false);
        await RefreshGateTestSupport.DrainUiAsync();
        await RefreshGateTestSupport.WaitUntilAsync(
            () => viewModel.InstalledMods.Count == 2
                  && viewModel.InstalledMods.Any(item => item.ProjectId == "alpha" && item.HasUpdate)
                  && viewModel.InstalledMods.Any(item => item.ProjectId == "bravo" && !item.HasUpdate),
            cancellationToken);

        var alpha = Assert.Single(viewModel.InstalledMods, item => item.ProjectId == "alpha");
        var bravo = Assert.Single(viewModel.InstalledMods, item => item.ProjectId == "bravo");

        Assert.True(alpha.CanUpdate);
        Assert.True(alpha.HasUpdate);
        Assert.Equal("1.1.0", alpha.LatestVersionNumber);

        Assert.True(bravo.CanUpdate);
        Assert.False(bravo.HasUpdate);
        Assert.Null(bravo.LatestVersionNumber);

        Assert.True(viewModel.CanUpdateAll);
    }

    [Fact]
    public async Task LoadModsStateAsync_StaleCachedResultShowsQualifier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        await RefreshGateTestSupport.WriteManagedModsAsync(
            fixture,
            RefreshGateTestSupport.CreateManagedMod("alpha", "Alpha", "alpha-1", "1.0.0", "alpha.jar", "Direct", [],
                [1, 2, 3]));

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
                        [RefreshGateTestSupport.CreateVersion("alpha-2", "alpha", "1.1.0", "alpha-2.jar", [7, 8, 9])],
                        ModrinthJsonContext.Default.ModrinthVersionArray));
        });
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, handler);
        var viewModel = new InstanceDetailsViewModel(fixture.Instance, null, null, manager, null, null, null);

        await manager.GetLatestCompatibleVersionsAsync(fixture.Instance, ["alpha"],
            cancellationToken: cancellationToken);
        ExpireCacheEntry(manager, fixture.Instance, "alpha");
        shouldFail = true;

        await RefreshGateTestSupport.InvokeNonPublicTaskAsync(viewModel, "LoadModsStateAsync", false);
        await RefreshGateTestSupport.DrainUiAsync();

        var alpha = Assert.Single(viewModel.InstalledMods);
        Assert.True(alpha.HasUpdate);
        Assert.Equal("Refresh failed; showing cached 1.1.0.", alpha.UpdateStatusText);
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
            entryType.GetProperty("LastRefreshAttemptAtUtc", BindingFlags.Public | BindingFlags.Instance)!
                .GetValue(entry),
            entryType.GetProperty("RefreshState", BindingFlags.Public | BindingFlags.Instance)!.GetValue(entry))!;
        cache.GetType()
            .GetProperty("Item")!
            .SetValue(cache, updatedEntry, [cacheKey]);
    }

    private static async Task WaitForInstalledModAsync(
        InstanceDetailsViewModel viewModel,
        string latestVersionNumber,
        CancellationToken cancellationToken)
    {
        await RefreshGateTestSupport.WaitUntilAsync(
            () => viewModel.InstalledMods.Count == 1
                  && string.Equals(viewModel.InstalledMods[0].ProjectId, "alpha", StringComparison.Ordinal)
                  && viewModel.InstalledMods[0].HasUpdate
                  && string.Equals(
                      viewModel.InstalledMods[0].LatestVersionNumber,
                      latestVersionNumber,
                      StringComparison.Ordinal),
            cancellationToken);
    }
}
