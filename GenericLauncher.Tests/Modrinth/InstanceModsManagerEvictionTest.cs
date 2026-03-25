using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using GenericLauncher.InstanceMods;
using Xunit;

namespace GenericLauncher.Tests.Modrinth;

public sealed class InstanceModsManagerEvictionTest
{
    [Fact]
    public async Task EvictInstanceCaches_RemovesAllEntriesForTargetInstance()
    {
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, CreateThrowingHandler());
        var cache = GetCache(manager);

        cache[$"{fixture.Instance.Id}|mod-a"] = new LatestCompatibleVersionInfo("mod-a", "v1", "1.0.0");
        cache[$"{fixture.Instance.Id}|mod-b"] = new LatestCompatibleVersionInfo("mod-b", "v2", "2.0.0");

        manager.EvictInstanceCaches(fixture.Instance);

        Assert.Empty(cache);
    }

    [Fact]
    public async Task EvictInstanceCaches_RemovesOnlyTargetInstanceCacheEntries()
    {
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        var otherInstance = fixture.Instance with { Id = "Other Pack", Folder = "other-pack" };
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath, CreateThrowingHandler());
        var cache = GetCache(manager);

        cache[$"{fixture.Instance.Id}|mod-a"] = new LatestCompatibleVersionInfo("mod-a", "v1", "1.0.0");
        cache[$"{otherInstance.Id}|mod-a"] = new LatestCompatibleVersionInfo("mod-a", "v3", "3.0.0");

        manager.EvictInstanceCaches(fixture.Instance);

        Assert.DoesNotContain(cache.Keys, k => k.StartsWith($"{fixture.Instance.Id}|"));
        Assert.Contains($"{otherInstance.Id}|mod-a", cache.Keys);
    }

    [Fact]
    public async Task EvictInstanceCaches_InvalidatesSnapshot()
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

        manager.EvictInstanceCaches(fixture.Instance);

        // After eviction, GetSnapshotAsync should re-read from disk and return correct data.
        var snapshotAfter = await manager.GetSnapshotAsync(fixture.Instance, forceRefresh: false, cancellationToken);
        Assert.Single(snapshotAfter.InstalledMods);
    }

    private static Dictionary<string, LatestCompatibleVersionInfo?> GetCache(InstanceModsManager manager)
    {
        var field = typeof(InstanceModsManager)
            .GetField("_latestCompatibleVersionCache", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Dictionary<string, LatestCompatibleVersionInfo?>)field.GetValue(manager)!;
    }

    private static RefreshGateTestSupport.RoutingHttpMessageHandler CreateThrowingHandler() =>
        new((request, _) => throw new System.InvalidOperationException($"Unexpected request: {request.RequestUri}"));
}
