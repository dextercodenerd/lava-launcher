using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GenericLauncher.Database.Model;
using GenericLauncher.Http;
using GenericLauncher.InstanceMods;
using GenericLauncher.InstanceMods.Json;
using GenericLauncher.Misc;
using GenericLauncher.Modrinth;
using GenericLauncher.Modrinth.Json;
using Xunit;

namespace GenericLauncher.Tests.Modrinth;

internal static class RefreshGateTestSupport
{
    public static async Task<TestFixture> CreateFixtureAsync()
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

    public static InstanceModsManager CreateManager(string rootPath, HttpMessageHandler handler)
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

    public static async Task WriteManagedModsAsync(TestFixture fixture, params ManagedModFixture[] managedMods)
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

    public static ManagedModFixture CreateManagedMod(
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

    public static ModrinthVersion CreateVersion(
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
            "2026-03-24T00:00:00Z",
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

    public static ModrinthSearchResult CreateSearchResult(string projectId, string title) =>
        new(projectId, projectId, title, $"{title} description", [], "mod", 0, null, "", "", "");

    public static ModrinthProject CreateProject(string projectId, string title) =>
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
            "2026-03-24T00:00:00Z",
            "2026-03-24T00:00:00Z",
            null,
            null,
            null,
            null,
            null,
            [],
            ["1.21.1"],
            ["fabric"],
            []);

    public static HttpResponseMessage JsonResponse<T>(T value, JsonTypeInfo<T> typeInfo) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, typeInfo),
                Encoding.UTF8,
                "application/json"),
        };

    public static async Task DrainUiAsync()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
    }

    public static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for test condition.");
            }

            await DrainUiAsync();
            await Task.Delay(10, cancellationToken);
        }
    }

    public static Task InvokeNonPublicTaskAsync(object target, string methodName, params object[] arguments)
    {
        var method = target.GetType().GetMethod(
                         methodName,
                         BindingFlags.Instance | BindingFlags.NonPublic,
                         binder: null,
                         types: arguments.Select(argument => argument.GetType()).ToArray(),
                         modifiers: null)
                     ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        var task = method.Invoke(target, arguments) as Task;
        return task ?? throw new InvalidOperationException($"{methodName} did not return a Task.");
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

    private static InstanceMeta CreateMeta(MinecraftInstance instance, params InstanceMetaMod[] mods) =>
        new(
            1,
            instance.Id,
            instance.VersionId,
            MinecraftInstance.ModLoaderToString(instance.ModLoader),
            instance.ModLoaderVersion,
            instance.LaunchVersionId,
            mods);

    public sealed record TestFixture(
        string RootPath,
        MinecraftInstance Instance,
        string InstanceFolder,
        string ModsFolder
    );

    public sealed record ManagedModFixture(
        InstanceMetaMod Meta,
        byte[] FileBytes
    );

    public sealed class RoutingHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handleAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handleAsync(request, cancellationToken);
    }
}
