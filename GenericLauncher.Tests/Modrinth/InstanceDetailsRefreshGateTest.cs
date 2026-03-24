using System;
using System.Threading.Tasks;
using GenericLauncher.Modrinth.Json;
using GenericLauncher.Screens.InstanceDetails;
using Xunit;

namespace GenericLauncher.Tests.Modrinth;

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
        await RefreshGateTestSupport.DrainUiAsync();

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

            requestCount++;
            if (requestCount == 1)
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
        await RefreshGateTestSupport.DrainUiAsync();

        var currentItem = Assert.Single(viewModel.InstalledMods);
        Assert.True(currentItem.HasUpdate);
        Assert.Equal("1.2.0", currentItem.LatestVersionNumber);

        releaseFirstRequest.TrySetResult();
        await firstRequestReturned.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await firstLoadTask;
        await RefreshGateTestSupport.DrainUiAsync();

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

            requestCount++;
            if (requestCount == 1)
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
        await RefreshGateTestSupport.DrainUiAsync();

        var currentItem = Assert.Single(viewModel.InstalledMods);
        Assert.True(currentItem.HasUpdate);
        Assert.Equal("1.2.0", currentItem.LatestVersionNumber);

        releaseFirstRequest.TrySetResult();
        await firstRequestReturned.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await firstLoadTask;
        await RefreshGateTestSupport.DrainUiAsync();

        currentItem = Assert.Single(viewModel.InstalledMods);
        Assert.True(currentItem.HasUpdate);
        Assert.Equal("1.2.0", currentItem.LatestVersionNumber);
    }
}
