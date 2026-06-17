using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using GenericLauncher.InstanceMods;
using GenericLauncher.Misc;
using Xunit;

namespace GenericLauncher.Tests.Modrinth;

public sealed class InstanceModsManagerEvictionTest
{
    [Fact]
    public async Task OnInstanceDeletingAsync_DeletesFolder()
    {
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, CreateThrowingHandler());

        Assert.True(Directory.Exists(fixture.InstanceFolder));

        await manager.OnInstanceDeletingAsync(fixture.Instance);

        Assert.False(Directory.Exists(fixture.InstanceFolder));
    }

    [Fact]
    public async Task OnInstanceDeletingAsync_InvalidatesSnapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        await RefreshGateTestSupport.WriteManagedModsAsync(
            fixture,
            RefreshGateTestSupport.CreateManagedMod("alpha", "Alpha", "alpha-1", "1.0.0", "alpha.jar", "Direct", [], [1, 2, 3]));

        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, CreateThrowingHandler());

        // Warm the snapshot cache.
        var snapshotBefore = await manager.GetSnapshotAsync(fixture.Instance, forceRefresh: false, cancellationToken);
        Assert.Single(snapshotBefore.InstalledMods);

        await manager.OnInstanceDeletingAsync(fixture.Instance);

        // After deletion the folder is gone, so the snapshot should be re-created empty.
        var snapshotAfter = await manager.GetSnapshotAsync(fixture.Instance, forceRefresh: false, cancellationToken);
        Assert.Empty(snapshotAfter.InstalledMods);
    }

    [Fact]
    public async Task OnInstanceDeletingAsync_RemovesStateLock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, CreateThrowingHandler());

        // Trigger a snapshot read to create the lock entry.
        await manager.GetSnapshotAsync(fixture.Instance, forceRefresh: false, cancellationToken);

        var locks = GetInstanceStateLocks(manager);
        Assert.True(locks.ContainsKey(fixture.InstanceFolder));

        await manager.OnInstanceDeletingAsync(fixture.Instance);

        Assert.False(locks.ContainsKey(fixture.InstanceFolder));
    }

    private static ConcurrentDictionary<string, AsyncRwLock> GetInstanceStateLocks(InstanceModsManager manager)
    {
        var field = typeof(InstanceModsManager)
            .GetField("_instanceStateLocks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (ConcurrentDictionary<string, AsyncRwLock>)field.GetValue(manager)!;
    }

    private static RefreshGateTestSupport.RoutingHttpMessageHandler CreateThrowingHandler() =>
        new((request, _) => throw new System.InvalidOperationException($"Unexpected request: {request.RequestUri}"));
}
