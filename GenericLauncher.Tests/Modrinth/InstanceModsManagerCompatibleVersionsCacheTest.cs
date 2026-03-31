using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using GenericLauncher.Database.Model;
using GenericLauncher.InstanceMods;
using GenericLauncher.Modrinth.Json;
using Xunit;

namespace GenericLauncher.Tests.Modrinth;

/// <summary>
/// Tests that validate the private compatible-versions cache semantics inside
/// <see cref="InstanceModsManager"/>: TTL, stale-on-failure, unavailable state,
/// force-refresh bypass, and convergence of the install/update path onto the
/// shared cache-backed resolution path.
/// </summary>
public sealed class InstanceModsManagerCompatibleVersionsCacheTest
{
    // 1. Cache hit: second lookup does not trigger a fresh fetch

    [Fact]
    public async Task GetLatestCompatibleVersionsAsync_CacheHit_DoesNotFetchAgain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        var bravoV1 = RefreshGateTestSupport.CreateVersion("bravo-v1", "bravo", "1.0.0", "bravo.jar", [1, 2, 3]);

        var callCount = 0;
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath,
            new RefreshGateTestSupport.RoutingHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri?.AbsolutePath == "/v2/project/bravo/version")
                {
                    callCount++;
                    return Task.FromResult(JsonResponse([bravoV1], ModrinthJsonContext.Default.ModrinthVersionArray));
                }

                throw new InvalidOperationException($"Unexpected: {request.RequestUri}");
            }));

        // First lookup: triggers a network fetch.
        var first = await manager.GetLatestCompatibleVersionsAsync(fixture.Instance, ["bravo"],
            cancellationToken: cancellationToken);

        // Second lookup (same params, TTL still valid): must use the cache.
        var second =
            await manager.GetLatestCompatibleVersionsAsync(fixture.Instance, ["bravo"],
                cancellationToken: cancellationToken);

        Assert.Equal(1, callCount);
        Assert.False(first.HasRefreshFailure);
        Assert.False(second.HasRefreshFailure);
        Assert.True(first.Versions.ContainsKey("bravo"));
        Assert.True(second.Versions.ContainsKey("bravo"));
    }

    // 2. Force refresh: bypasses valid cache entry

    [Fact]
    public async Task GetLatestCompatibleVersionsAsync_ForceRefresh_FetchesEvenWhenCacheValid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        var bravoV1 = RefreshGateTestSupport.CreateVersion("bravo-v1", "bravo", "1.0.0", "bravo.jar", [1, 2, 3]);
        var bravoV2 = RefreshGateTestSupport.CreateVersion("bravo-v2", "bravo", "2.0.0", "bravo.jar", [1, 2, 4]);

        var callCount = 0;
        var returnV2 = false;
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath,
            new RefreshGateTestSupport.RoutingHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri?.AbsolutePath == "/v2/project/bravo/version")
                {
                    callCount++;
                    var version = returnV2 ? bravoV2 : bravoV1;
                    return Task.FromResult(JsonResponse([version], ModrinthJsonContext.Default.ModrinthVersionArray));
                }

                throw new InvalidOperationException($"Unexpected: {request.RequestUri}");
            }));

        await manager.GetLatestCompatibleVersionsAsync(fixture.Instance, ["bravo"],
            cancellationToken: cancellationToken);
        Assert.Equal(1, callCount);

        returnV2 = true;
        var refreshed = await manager.GetLatestCompatibleVersionsAsync(
            fixture.Instance, ["bravo"], true, cancellationToken);

        Assert.Equal(2, callCount);
        Assert.False(refreshed.HasRefreshFailure);
        Assert.Equal("bravo-v2", refreshed.Versions["bravo"].VersionId);
    }

    // 3. TTL expiry: stale cache entry causes a fresh fetch

    [Fact]
    public async Task GetLatestCompatibleVersionsAsync_TtlExpired_FetchesFresh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        var bravoV1 = RefreshGateTestSupport.CreateVersion("bravo-v1", "bravo", "1.0.0", "bravo.jar", [1, 2, 3]);

        var callCount = 0;
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath,
            new RefreshGateTestSupport.RoutingHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri?.AbsolutePath == "/v2/project/bravo/version")
                {
                    callCount++;
                    return Task.FromResult(JsonResponse([bravoV1], ModrinthJsonContext.Default.ModrinthVersionArray));
                }

                throw new InvalidOperationException($"Unexpected: {request.RequestUri}");
            }));

        // Warm the cache.
        await manager.GetLatestCompatibleVersionsAsync(fixture.Instance, ["bravo"],
            cancellationToken: cancellationToken);
        Assert.Equal(1, callCount);

        // Manually expire the cache entry by backdating its FetchedAt.
        ExpireCacheEntry(manager, fixture.Instance, "bravo");

        // Next lookup must perform a fresh fetch because the TTL is exceeded.
        await manager.GetLatestCompatibleVersionsAsync(fixture.Instance, ["bravo"],
            cancellationToken: cancellationToken);
        Assert.Equal(2, callCount);
    }

    // 4. Stale success: failure after prior success keeps old data

    [Fact]
    public async Task GetLatestCompatibleVersionsAsync_FetchFailureWithPriorSuccess_ReturnsStaleWithRefreshFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        var bravoV1 = RefreshGateTestSupport.CreateVersion("bravo-v1", "bravo", "1.0.0", "bravo.jar", [1, 2, 3]);

        var shouldFail = false;
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath,
            new RefreshGateTestSupport.RoutingHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri?.AbsolutePath == "/v2/project/bravo/version")
                {
                    if (shouldFail)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                    }

                    return Task.FromResult(JsonResponse([bravoV1], ModrinthJsonContext.Default.ModrinthVersionArray));
                }

                throw new InvalidOperationException($"Unexpected: {request.RequestUri}");
            }));

        // Successful lookup: warms the cache.
        var success =
            await manager.GetLatestCompatibleVersionsAsync(fixture.Instance, ["bravo"],
                cancellationToken: cancellationToken);
        Assert.False(success.HasRefreshFailure);
        Assert.Equal("bravo-v1", success.Versions["bravo"].VersionId);

        // Expire the cache so the next call triggers a fresh fetch.
        ExpireCacheEntry(manager, fixture.Instance, "bravo");
        shouldFail = true;

        // Failed refresh: must return the prior cached version with HasRefreshFailure=true.
        var stale = await manager.GetLatestCompatibleVersionsAsync(fixture.Instance, ["bravo"],
            cancellationToken: cancellationToken);
        Assert.True(stale.HasRefreshFailure);
        Assert.True(stale.Versions.ContainsKey("bravo"), "Stale cached data must still be returned.");
        Assert.Equal("bravo-v1", stale.Versions["bravo"].VersionId);
    }

    // 5. Unavailable: first fetch fails and no prior data exists

    [Fact]
    public async Task GetLatestCompatibleVersionsAsync_FetchFailureNoPriorSuccess_ReturnsUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();

        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath,
            new RefreshGateTestSupport.RoutingHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri?.AbsolutePath == "/v2/project/bravo/version")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                throw new InvalidOperationException($"Unexpected: {request.RequestUri}");
            }));

        // No prior data and fetch fails: must signal unavailable (no version in dict).
        var result =
            await manager.GetLatestCompatibleVersionsAsync(fixture.Instance, ["bravo"],
                cancellationToken: cancellationToken);

        Assert.True(result.HasRefreshFailure);
        Assert.False(result.Versions.ContainsKey("bravo"), "No cached data should be shown for an unavailable lookup.");
    }

    // 6. Empty compatible versions: distinct from fetch failure

    [Fact]
    public async Task GetLatestCompatibleVersionsAsync_EmptyVersionsFromApi_CachedAndDistinctFromFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();

        var callCount = 0;
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath,
            new RefreshGateTestSupport.RoutingHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri?.AbsolutePath == "/v2/project/bravo/version")
                {
                    callCount++;
                    // API succeeded but no compatible versions for this MC/loader combo.
                    return Task.FromResult(JsonResponse(
                        Array.Empty<ModrinthVersion>(),
                        ModrinthJsonContext.Default.ModrinthVersionArray));
                }

                throw new InvalidOperationException($"Unexpected: {request.RequestUri}");
            }));

        var first = await manager.GetLatestCompatibleVersionsAsync(fixture.Instance, ["bravo"],
            cancellationToken: cancellationToken);
        var second =
            await manager.GetLatestCompatibleVersionsAsync(fixture.Instance, ["bravo"],
                cancellationToken: cancellationToken);

        // No versions but NOT a failure: HasRefreshFailure must be false.
        Assert.False(first.HasRefreshFailure);
        Assert.False(first.Versions.ContainsKey("bravo"));

        // The successful-empty result is cached: second call must not fetch again.
        Assert.Equal(1, callCount);
        Assert.False(second.HasRefreshFailure);
    }

    // 7. Install convergence: UpdateModAsync uses shared cache path

    [Fact]
    public async Task UpdateModAsync_UsesCachedCompatibleVersions_WithoutAdditionalProjectVersionsFetch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await RefreshGateTestSupport.CreateFixtureAsync();
        var bravoV1Bytes = new byte[] { 1, 2, 3 };
        var bravoV2Bytes = new byte[] { 4, 5, 6 };
        await RefreshGateTestSupport.WriteManagedModsAsync(
            fixture,
            RefreshGateTestSupport.CreateManagedMod(
                "bravo", "Bravo", "bravo-v1", "1.0.0", "bravo.jar", "Direct", [], bravoV1Bytes));

        var bravoV2 = RefreshGateTestSupport.CreateVersion("bravo-v2", "bravo", "2.0.0", "bravo.jar", bravoV2Bytes);
        var projectVersionsCallCount = 0;
        var manager = RefreshGateTestSupport.CreateManager(fixture.RootPath,
            new RefreshGateTestSupport.RoutingHttpMessageHandler((request, _) =>
            {
                return request.RequestUri?.AbsolutePath switch
                {
                    "/v2/project/bravo/version" => Task.FromResult(
                        IncAndReturn(ref projectVersionsCallCount,
                            JsonResponse([bravoV2], ModrinthJsonContext.Default.ModrinthVersionArray))),
                    "/v2/version/bravo-v2" => Task.FromResult(
                        JsonResponse(bravoV2, ModrinthJsonContext.Default.ModrinthVersion)),
                    "/v2/project/bravo" => Task.FromResult(
                        JsonResponse(RefreshGateTestSupport.CreateProject("bravo", "Bravo"),
                            ModrinthJsonContext.Default.ModrinthProject)),
                    // File download — serve bravo-v2 bytes for the mods folder write.
                    "/bravo.jar" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(bravoV2Bytes),
                    }),
                    _ => throw new InvalidOperationException($"Unexpected: {request.RequestUri}"),
                };
            }));

        // Warm the cache via the UI-style lookup.
        await manager.GetLatestCompatibleVersionsAsync(fixture.Instance, ["bravo"],
            cancellationToken: cancellationToken);
        Assert.Equal(1, projectVersionsCallCount);

        // Update uses the cached compatible-version list and should not re-fetch project versions.
        await manager.UpdateModAsync(fixture.Instance, "bravo", cancellationToken);
        Assert.Equal(1, projectVersionsCallCount);
    }

    // Helpers

    private static HttpResponseMessage IncAndReturn(ref int counter, HttpResponseMessage response)
    {
        counter++;
        return response;
    }

    /// <summary>
    /// Backdates the <c>FetchedAt</c> of a cache entry so it appears expired.
    /// Uses reflection to reach the private cache, matching the pattern used in the
    /// existing <see cref="InstanceModsManagerEvictionTest"/>.
    /// </summary>
    private static void ExpireCacheEntry(InstanceModsManager manager, MinecraftInstance instance, string projectId)
    {
        var cacheKey = (string)typeof(InstanceModsManager)
            .GetMethod("GetCompatibleVersionsCacheKey",
                BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [instance, projectId])!;

        // The field is Dictionary<string, CompatibleVersionCacheEntry> where the value
        // type is a private nested class, so we use non-generic reflection to access it.
        var cacheField = typeof(InstanceModsManager)
            .GetField("_compatibleVersionsCache", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var cache = cacheField.GetValue(manager)!;

        // Dictionary<TKey,TValue>.TryGetValue via reflection.
        var tryGetValueMethod = cache.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { cacheKey, null };
        if (!(bool)tryGetValueMethod.Invoke(cache, args)!)
        {
            return;
        }

        var entry = args[1]!;
        entry.GetType()
            .GetProperty("FetchedAt", BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(entry, DateTime.UtcNow - TimeSpan.FromHours(1));
    }

    private static HttpResponseMessage JsonResponse<T>(T value, JsonTypeInfo<T> typeInfo) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, typeInfo),
                Encoding.UTF8,
                "application/json"),
        };
}