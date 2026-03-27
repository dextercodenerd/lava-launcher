using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using GenericLauncher.Database.Model;
using GenericLauncher.Http;
using GenericLauncher.InstanceMods;
using GenericLauncher.InstanceMods.Json;
using GenericLauncher.Misc;
using GenericLauncher.Modrinth;
using GenericLauncher.Modrinth.Json;
using Xunit;

namespace GenericLauncher.Tests.Modrinth;

public sealed class InstanceModsManagerStateTest
{
    [Fact]
    public async Task GetSnapshotAsync_UsesLocalMetadataWithoutCallingModrinth()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync();
        await WriteManagedModsAsync(
            fixture,
            CreateManagedMod("alpha", "Alpha", "alpha-1", "1.0.0", "alpha.jar", "Direct", [], [1, 2, 3]));

        var manager = CreateManager(fixture.RootPath, CreateThrowingHandler());

        var snapshot = await manager.GetSnapshotAsync(fixture.Instance, forceRefresh: true, cancellationToken);

        var installed = Assert.Single(snapshot.InstalledMods);
        Assert.Equal("Alpha", installed.DisplayName);
        Assert.Empty(snapshot.RequiredDependencies);
        Assert.Empty(snapshot.ManualMods);
        Assert.Empty(snapshot.BrokenMods);
    }

    [Fact]
    public async Task DeleteModAsync_RemovesUnusedDependencies_AndPublishesCleanSnapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync();
        await WriteManagedModsAsync(
            fixture,
            CreateManagedMod("alpha", "Alpha", "alpha-1", "1.0.0", "alpha.jar", "Direct", [], [1, 2, 3]),
            CreateManagedMod("dep", "Dependency", "dep-1", "1.0.0", "dep.jar", "Dependency", ["alpha"], [4, 5, 6]));

        var manager = CreateManager(fixture.RootPath, CreateThrowingHandler());
        var changedSnapshot = new TaskCompletionSource<InstanceModsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.InstanceModsChanged += (_, e) =>
        {
            if (string.Equals(e.InstanceId, fixture.Instance.Id, StringComparison.Ordinal))
            {
                changedSnapshot.TrySetResult(e.Snapshot);
            }
        };

        await manager.DeleteModAsync(fixture.Instance, "alpha", cancellationToken);

        var publishedSnapshot = await changedSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Empty(publishedSnapshot.AllItems);
        Assert.False(File.Exists(Path.Combine(fixture.ModsFolder, "alpha.jar")));
        Assert.False(File.Exists(Path.Combine(fixture.ModsFolder, "dep.jar")));

        var meta = await ReadMetaAsync(fixture.InstanceFolder, cancellationToken);
        Assert.Empty(meta.Mods);

        var cachedSnapshot = await manager.GetSnapshotAsync(fixture.Instance, cancellationToken: cancellationToken);
        Assert.Empty(cachedSnapshot.AllItems);
    }

    [Fact]
    public async Task DeleteModAsync_PreservesSharedDependenciesNeededByRemainingDirectMods()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync();
        var sharedBytes = new byte[] { 9, 9, 9 };
        var bravoBytes = new byte[] { 2, 2, 2 };
        await WriteManagedModsAsync(
            fixture,
            CreateManagedMod("alpha", "Alpha", "alpha-1", "1.0.0", "alpha.jar", "Direct", [], [1, 1, 1]),
            CreateManagedMod("bravo", "Bravo", "bravo-1", "1.0.0", "bravo.jar", "Direct", [], bravoBytes),
            CreateManagedMod("dep", "Dependency", "dep-1", "1.0.0", "dep.jar", "Dependency", ["alpha", "bravo"], sharedBytes));

        var bravoVersion = CreateVersion(
            "bravo-1",
            "bravo",
            "1.0.0",
            "bravo.jar",
            bravoBytes,
            new ModrinthDependency("dep-1", "dep", null, "required"));
        var dependencyVersion = CreateVersion("dep-1", "dep", "1.0.0", "dep.jar", sharedBytes);
        var manager = CreateManager(
            fixture.RootPath,
            new RoutingHttpMessageHandler((request, _) =>
            {
                return request.RequestUri?.AbsolutePath switch
                {
                    "/v2/version/bravo-1" => Task.FromResult(JsonResponse(bravoVersion, ModrinthJsonContext.Default.ModrinthVersion)),
                    "/v2/project/bravo" => Task.FromResult(JsonResponse(CreateProject("bravo", "Bravo"), ModrinthJsonContext.Default.ModrinthProject)),
                    "/v2/version/dep-1" => Task.FromResult(JsonResponse(dependencyVersion, ModrinthJsonContext.Default.ModrinthVersion)),
                    "/v2/project/dep" => Task.FromResult(JsonResponse(CreateProject("dep", "Dependency"), ModrinthJsonContext.Default.ModrinthProject)),
                    _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
                };
            }));

        await manager.DeleteModAsync(fixture.Instance, "alpha", cancellationToken);

        Assert.False(File.Exists(Path.Combine(fixture.ModsFolder, "alpha.jar")));
        Assert.True(File.Exists(Path.Combine(fixture.ModsFolder, "bravo.jar")));
        Assert.True(File.Exists(Path.Combine(fixture.ModsFolder, "dep.jar")));

        var meta = await ReadMetaAsync(fixture.InstanceFolder, cancellationToken);
        Assert.Equal(2, meta.Mods.Length);
        Assert.Contains(meta.Mods, mod => mod.ProjectId == "bravo" && mod.InstallKind == "Direct");
        var dependency = Assert.Single(meta.Mods, mod => mod.ProjectId == "dep");
        Assert.Equal("Dependency", dependency.InstallKind);
        Assert.Equal(["bravo"], dependency.RequiredByProjectIds);

        var snapshot = await manager.GetSnapshotAsync(fixture.Instance, forceRefresh: true, cancellationToken);
        Assert.Single(snapshot.InstalledMods);
        Assert.Single(snapshot.RequiredDependencies);
        Assert.DoesNotContain(snapshot.AllItems, item => item.ProjectId == "alpha");
    }

    [Fact]
    public async Task DeleteModAsync_RejectsDependencyRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync();
        await WriteManagedModsAsync(
            fixture,
            CreateManagedMod("dep", "Dependency", "dep-1", "1.0.0", "dep.jar", "Dependency", ["alpha"], [4, 5, 6]));

        var manager = CreateManager(fixture.RootPath, CreateThrowingHandler());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DeleteModAsync(fixture.Instance, "dep", cancellationToken));

        Assert.Contains("cannot be deleted manually", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSnapshotAsync_IsNotBlockedWhileUpdateWaitsOnRemoteResolution()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CreateFixtureAsync();
        var bravoBytes = new byte[] { 2, 2, 2 };
        await WriteManagedModsAsync(
            fixture,
            CreateManagedMod("bravo", "Bravo", "bravo-1", "1.0.0", "bravo.jar", "Direct", [], bravoBytes));

        var remoteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRemoteToContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bravoVersion = CreateVersion("bravo-1", "bravo", "1.0.0", "bravo.jar", bravoBytes);
        var manager = CreateManager(
            fixture.RootPath,
            new RoutingHttpMessageHandler(async (request, token) =>
            {
                switch (request.RequestUri?.AbsolutePath)
                {
                    case "/v2/project/bravo/version":
                        remoteEntered.TrySetResult();
                        await allowRemoteToContinue.Task.WaitAsync(token);
                        return JsonResponse([bravoVersion], ModrinthJsonContext.Default.ModrinthVersionArray);
                    case "/v2/project/bravo":
                        return JsonResponse(CreateProject("bravo", "Bravo"), ModrinthJsonContext.Default.ModrinthProject);
                    default:
                        throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
                }
            }));

        var updateTask = manager.UpdateModAsync(fixture.Instance, "bravo", cancellationToken);
        await remoteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        var snapshotTask = manager.GetSnapshotAsync(fixture.Instance, forceRefresh: true, cancellationToken);
        var completedTask = await Task.WhenAny(snapshotTask, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));

        Assert.Same(snapshotTask, completedTask);
        Assert.Single((await snapshotTask).InstalledMods);

        allowRemoteToContinue.TrySetResult();
        await updateTask;
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "lavalancher-tests", Guid.NewGuid().ToString("N"));
        var instance = new MinecraftInstance(
            "Family Pack",
            "1.21.1",
            "1.21.1-fabric",
            MinecraftInstanceModLoader.Fabric,
            "0.16.10",
            MinecraftInstanceState.Ready,
            "release",
            "family-pack",
            21,
            "",
            "",
            "",
            [],
            [],
            []);
        var instanceFolder = Path.Combine(rootPath, "instances", instance.Folder);
        var modsFolder = Path.Combine(instanceFolder, "mods");
        Directory.CreateDirectory(modsFolder);
        await WriteMetaAsync(instanceFolder, CreateMeta(instance), TestContext.Current.CancellationToken);
        return new TestFixture(rootPath, instance, instanceFolder, modsFolder);
    }

    private static InstanceModsManager CreateManager(string rootPath, HttpMessageHandler handler)
    {
        var platform = new LauncherPlatform(
            "linux",
            "x64",
            new Version(1, 0),
            "lavalancher-tests",
            rootPath,
            rootPath);
        var apiHttpClient = new HttpClient(handler, disposeHandler: false);
        var downloadHttpClient = new HttpClient(handler, disposeHandler: false);
        return new InstanceModsManager(
            platform,
            new ModrinthApiClient(apiHttpClient),
            new FileDownloader(downloadHttpClient));
    }

    private static RoutingHttpMessageHandler CreateThrowingHandler() =>
        new((request, _) => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"));

    private static async Task WriteManagedModsAsync(TestFixture fixture, params ManagedModFixture[] managedMods)
    {
        foreach (var managedMod in managedMods)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(fixture.ModsFolder, managedMod.Meta.InstalledFileName),
                managedMod.FileBytes,
                TestContext.Current.CancellationToken);
        }

        await WriteMetaAsync(
            fixture.InstanceFolder,
            CreateMeta(fixture.Instance, managedMods.Select(managedMod => managedMod.Meta).ToArray()),
            TestContext.Current.CancellationToken);
    }

    private static async Task WriteMetaAsync(
        string instanceFolder,
        InstanceMeta meta,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(instanceFolder);
        var json = JsonSerializer.Serialize(meta, InstanceMetaJsonContext.Default.InstanceMeta);
        await File.WriteAllTextAsync(Path.Combine(instanceFolder, "meta.json"), json, cancellationToken);
    }

    private static async Task<InstanceMeta> ReadMetaAsync(string instanceFolder, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(instanceFolder, "meta.json"), cancellationToken);
        return JsonSerializer.Deserialize(json, InstanceMetaJsonContext.Default.InstanceMeta)
               ?? throw new InvalidOperationException("Failed to deserialize instance metadata.");
    }

    private static InstanceMeta CreateMeta(MinecraftInstance instance, params InstanceMetaMod[] mods) =>
        new(
            1,
            instance.Id,
            instance.VersionId,
            MinecraftInstance.ModLoaderToString(instance.ModLoader),
            instance.ModLoaderVersion,
            instance.LaunchVersionId,
            mods);

    private static ManagedModFixture CreateManagedMod(
        string projectId,
        string title,
        string versionId,
        string versionNumber,
        string fileName,
        string installKind,
        string[] requiredByProjectIds,
        byte[] fileBytes)
    {
        var sha512 = Convert.ToHexString(SHA512.HashData(fileBytes)).ToLowerInvariant();
        return new ManagedModFixture(
            new InstanceMetaMod(
                projectId,
                projectId,
                title,
                versionId,
                versionNumber,
                "release",
                fileName,
                sha512,
                installKind,
                requiredByProjectIds,
                new DateTime(2026, 3, 24, 0, 0, 0, DateTimeKind.Utc)),
            fileBytes);
    }

    private static ModrinthProject CreateProject(string projectId, string title) =>
        new(
            projectId,
            projectId,
            "mod",
            title,
            $"{title} description",
            "",
            [],
            "required",
            "optional",
            0,
            0,
            null,
            UtcInstant.Parse("2026-03-24T00:00:00Z"),
            UtcInstant.Parse("2026-03-24T00:00:00Z"),
            null,
            null,
            null,
            null,
            null,
            [],
            ["1.21.1"],
            ["fabric"],
            []);

    private static ModrinthVersion CreateVersion(
        string versionId,
        string projectId,
        string versionNumber,
        string fileName,
        byte[] fileBytes,
        params ModrinthDependency[] dependencies)
    {
        var sha512 = Convert.ToHexString(SHA512.HashData(fileBytes)).ToLowerInvariant();
        var sha1 = Convert.ToHexString(SHA1.HashData(fileBytes)).ToLowerInvariant();
        return new ModrinthVersion(
            versionId,
            projectId,
            versionNumber,
            versionNumber,
            "release",
            UtcInstant.Parse("2026-03-24T00:00:00Z"),
            ["fabric"],
            ["1.21.1"],
            dependencies,
            [
                new ModrinthVersionFile(
                    new ModrinthFileHashes(sha512, sha1),
                    $"https://example.invalid/{fileName}",
                    fileName,
                    true,
                    fileBytes.Length,
                    null),
            ]);
    }

    private static HttpResponseMessage JsonResponse<T>(T value, JsonTypeInfo<T> typeInfo) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, typeInfo),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed record TestFixture(
        string RootPath,
        MinecraftInstance Instance,
        string InstanceFolder,
        string ModsFolder
    );

    private sealed record ManagedModFixture(
        InstanceMetaMod Meta,
        byte[] FileBytes
    );

    private sealed class RoutingHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handleAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handleAsync(request, cancellationToken);
    }
}
