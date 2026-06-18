using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Database.Model;
using GenericLauncher.Http;
using GenericLauncher.Modpacks;
using Xunit;

namespace GenericLauncher.Tests.Modpacks;

public class MrpackInstallerTest
{
    [Fact]
    public async Task CreateClientInstallPlanAsync_ParsesDependenciesAndFiltersClientFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var requiredBytes = Encoding.UTF8.GetBytes("required");
        var optionalBytes = Encoding.UTF8.GetBytes("optional");
        var mrpackPath = CreateMrpack(
            root,
            CreateIndexJson(
                dependencies: """
                    "minecraft": "1.21.1",
                    "fabric-loader": "0.16.10"
                    """,
                files: $$"""
                    {{CreateFileJson("mods/required.jar", requiredBytes, "required")}},
                    {{CreateFileJson("mods/optional.jar", optionalBytes, "optional")}}
                    """));
        var installer = CreateInstaller(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var plan = await installer.CreateClientInstallPlanAsync(mrpackPath, Path.Combine(root, "instance"), cancellationToken);

        Assert.Equal("Example Pack", plan.Identity.Name);
        Assert.Equal("1.21.1", plan.Dependencies.MinecraftVersionId);
        Assert.Equal(MinecraftInstanceModLoader.Fabric, plan.Dependencies.ModLoader);
        Assert.Equal("0.16.10", plan.Dependencies.ModLoaderVersion);
        var file = Assert.Single(plan.Files);
        Assert.Equal("mods/required.jar", file.Path);
        var skipped = Assert.Single(plan.SkippedFiles);
        Assert.Equal("mods/optional.jar", skipped.Path);
    }

    [Theory]
    [InlineData("""
        {
          "formatVersion": 2,
          "game": "minecraft",
          "versionId": "1.0.0",
          "name": "Example Pack",
          "dependencies": { "minecraft": "1.21.1" },
          "files": []
        }
        """, "formatVersion")]
    [InlineData("""
        {
          "formatVersion": 1,
          "game": "other",
          "versionId": "1.0.0",
          "name": "Example Pack",
          "dependencies": { "minecraft": "1.21.1" },
          "files": []
        }
        """, "game")]
    [InlineData("""
        {
          "formatVersion": 1,
          "game": "minecraft",
          "versionId": "1.0.0",
          "name": "Example Pack",
          "dependencies": { "quilt-loader": "0.28.0", "minecraft": "1.21.1" },
          "files": []
        }
        """, "quilt-loader")]
    [InlineData("""
        {
          "formatVersion": 1,
          "game": "minecraft",
          "versionId": "1.0.0",
          "name": "Example Pack",
          "dependencies": { "minecraft": "1.21.1", "unknown-loader": "1" },
          "files": []
        }
        """, "unknown-loader")]
    [InlineData("""
        {
          "formatVersion": 1,
          "game": "minecraft",
          "versionId": "1.0.0",
          "name": "Example Pack",
          "dependencies": { "fabric-loader": "0.16.10" },
          "files": []
        }
        """, "minecraft")]
    public async Task CreateClientInstallPlanAsync_RejectsUnsupportedManifestValues(
        string indexJson,
        string expectedMessage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var mrpackPath = CreateMrpack(root, indexJson);
        var installer = CreateInstaller(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.CreateClientInstallPlanAsync(mrpackPath, Path.Combine(root, "instance"), cancellationToken));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../mods/escape.jar")]
    [InlineData("mods//empty.jar")]
    [InlineData("/mods/rooted.jar")]
    [InlineData("C:/mods/drive.jar")]
    [InlineData("meta.json")]
    [InlineData(".pack-tmp/file.jar")]
    [InlineData("natives/lib.dll")]
    public async Task CreateClientInstallPlanAsync_RejectsUnsafeManifestPaths(string unsafePath)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var bytes = Encoding.UTF8.GetBytes("payload");
        var mrpackPath = CreateMrpack(
            root,
            CreateIndexJson(files: CreateFileJson(unsafePath, bytes, "required")));
        var installer = CreateInstaller(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.CreateClientInstallPlanAsync(mrpackPath, Path.Combine(root, "instance"), cancellationToken));
    }

    [Fact]
    public async Task CreateClientInstallPlanAsync_RejectsUnknownEnv()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var bytes = Encoding.UTF8.GetBytes("payload");
        var mrpackPath = CreateMrpack(
            root,
            CreateIndexJson(files: CreateFileJson(
                "mods/bad.jar",
                bytes,
                "weird",
                downloadUrl: "http://cdn.modrinth.com/data/bad.jar")));
        var installer = CreateInstaller(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.CreateClientInstallPlanAsync(mrpackPath, Path.Combine(root, "instance"), cancellationToken));

        Assert.Contains("env.client", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://cdn.modrinth.com/data/bad.jar", "HTTPS")]
    [InlineData("https://example.invalid/data/bad.jar", "example.invalid")]
    public async Task CreateClientInstallPlanAsync_RejectsUnsafeDownloadUrls(
        string downloadUrl,
        string expectedMessage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var bytes = Encoding.UTF8.GetBytes("payload");
        var mrpackPath = CreateMrpack(
            root,
            CreateIndexJson(files: CreateFileJson(
                "mods/bad.jar",
                bytes,
                "required",
                downloadUrl: downloadUrl)));
        var installer = CreateInstaller(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.CreateClientInstallPlanAsync(mrpackPath, Path.Combine(root, "instance"), cancellationToken));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateClientInstallPlanAsync_RejectsMissingHashes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var mrpackPath = CreateMrpack(
            root,
            CreateIndexJson(files: """
              {
                "path": "mods/missing-hash.jar",
                "hashes": { "sha1": "0123456789012345678901234567890123456789" },
                "env": { "client": "required" },
                "downloads": ["https://cdn.modrinth.com/data/missing-hash.jar"],
                "fileSize": 1
              }
              """));
        var installer = CreateInstaller(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.CreateClientInstallPlanAsync(mrpackPath, Path.Combine(root, "instance"), cancellationToken));

        Assert.Contains("sha512", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MaterializeClientInstallPlanAsync_DownloadsFilesAndAppliesClientOverridesLast()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var instanceRoot = Path.Combine(root, "instance");
        var fileBytes = Encoding.UTF8.GetBytes("downloaded mod");
        var mrpackPath = CreateMrpack(
            root,
            CreateIndexJson(files: CreateFileJson("mods/downloaded.jar", fileBytes, "required")),
            ("overrides/config/app.toml", "base"),
            ("client-overrides/config/app.toml", "client"),
            ("server-overrides/config/server.toml", "server"));
        var installer = CreateInstaller(request =>
            request.RequestUri?.AbsoluteUri == "https://cdn.modrinth.com/data/downloaded.jar"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fileBytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var plan = await installer.CreateClientInstallPlanAsync(mrpackPath, instanceRoot, cancellationToken);
        await installer.MaterializeClientInstallPlanAsync(mrpackPath, plan, instanceRoot, cancellationToken);

        Assert.Equal(fileBytes, await File.ReadAllBytesAsync(Path.Combine(instanceRoot, "mods", "downloaded.jar"), cancellationToken));
        Assert.Equal("client", await File.ReadAllTextAsync(Path.Combine(instanceRoot, "config", "app.toml"), cancellationToken));
        Assert.False(File.Exists(Path.Combine(instanceRoot, "config", "server.toml")));
    }

    [Fact]
    public async Task MaterializeClientInstallPlanAsync_RestoresOriginalAfterSamePathOverrideFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var instanceRoot = Path.Combine(root, "instance");
        var configPath = Path.Combine(instanceRoot, "config", "app.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, "original", cancellationToken);
        var fileBytes = Encoding.UTF8.GetBytes("downloaded mod");
        var mrpackPath = CreateMrpack(
            root,
            CreateIndexJson(files: CreateFileJson("mods/downloaded.jar", fileBytes, "required")),
            ("overrides/config/app.toml", "base"),
            ("client-overrides/config/app.toml", "client"));
        var installer = CreateInstaller(request =>
            request.RequestUri?.AbsoluteUri == "https://cdn.modrinth.com/data/downloaded.jar"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fileBytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var plan = await installer.CreateClientInstallPlanAsync(mrpackPath, instanceRoot, cancellationToken);
        var tamperedPlan = plan with
        {
            Overrides = plan.Overrides.Add(new MrpackOverrideEntry(
                "overrides/missing.toml",
                "config/missing.toml")),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.MaterializeClientInstallPlanAsync(mrpackPath, tamperedPlan, instanceRoot, cancellationToken));

        Assert.Equal("original", await File.ReadAllTextAsync(configPath, cancellationToken));
        Assert.False(File.Exists(Path.Combine(instanceRoot, "mods", "downloaded.jar")));
        Assert.False(File.Exists(Path.Combine(instanceRoot, "config", "missing.toml")));
    }

    [Fact]
    public async Task CreateClientInstallPlanAsync_InstallsNoEnvAndRequiredClientFilesButSkipsServerOnlyFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var bytes = Encoding.UTF8.GetBytes("payload");
        var mrpackPath = CreateMrpack(
            root,
            CreateIndexJson(files: $$"""
                {{CreateFileJsonWithEnvJson("mods/no-env.jar", bytes, null)}},
                {{CreateFileJsonWithEnvJson("mods/client-required.jar", bytes, """
                    "env": { "client": "required", "server": "optional" },
                    """)}},
                {{CreateFileJsonWithEnvJson("mods/server-only.jar", bytes, """
                    "env": { "server": "required" },
                    """)}}
                """));
        var installer = CreateInstaller(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var plan = await installer.CreateClientInstallPlanAsync(mrpackPath, Path.Combine(root, "instance"), cancellationToken);

        Assert.Equal(
            ["mods/no-env.jar", "mods/client-required.jar"],
            plan.Files.Select(file => file.Path).ToArray());
        var skipped = Assert.Single(plan.SkippedFiles);
        Assert.Equal("mods/server-only.jar", skipped.Path);
    }

    [Fact]
    public async Task CreateClientInstallPlanAsync_RejectsUnsafeOverridePaths()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var mrpackPath = CreateMrpack(
            root,
            CreateIndexJson(files: ""),
            ("overrides/../escape.txt", "escape"));
        var installer = CreateInstaller(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.CreateClientInstallPlanAsync(mrpackPath, Path.Combine(root, "instance"), cancellationToken));
    }

    private static MrpackInstaller CreateInstaller(Func<HttpRequestMessage, HttpResponseMessage> handle)
    {
        var httpClient = new HttpClient(new RoutingHandler((request, _) => Task.FromResult(handle(request))));
        return new MrpackInstaller(new FileDownloader(httpClient));
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lavalancher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateMrpack(
        string root,
        string indexJson,
        params (string EntryName, string Content)[] entries)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".mrpack");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteEntry(archive, "modrinth.index.json", indexJson);
        foreach (var entry in entries)
        {
            WriteEntry(archive, entry.EntryName, entry.Content);
        }

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static string CreateIndexJson(
        string dependencies = """
            "minecraft": "1.21.1"
            """,
        string files = "") =>
        $$"""
          {
            "formatVersion": 1,
            "game": "minecraft",
            "versionId": "1.0.0",
            "name": "Example Pack",
            "summary": "Example summary",
            "dependencies": {
              {{dependencies}}
            },
            "files": [
              {{files}}
            ]
          }
          """;

    private static string CreateFileJson(
        string path,
        byte[] bytes,
        string client,
        string downloadUrl = "https://cdn.modrinth.com/data/downloaded.jar") =>
        CreateFileJsonWithEnvJson(
            path,
            bytes,
            $$"""
              "env": {
                "client": "{{client}}",
                "server": "optional"
              },
              """,
            downloadUrl);

    private static string CreateFileJsonWithEnvJson(
        string path,
        byte[] bytes,
        string? envJson,
        string downloadUrl = "https://cdn.modrinth.com/data/downloaded.jar")
    {
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var sha512 = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
        return $$"""
          {
            "path": "{{path}}",
            "hashes": {
              "sha1": "{{sha1}}",
              "sha512": "{{sha512}}"
            },
            {{envJson}}
            "downloads": ["{{downloadUrl}}"],
            "fileSize": {{bytes.Length}}
          }
          """;
    }

    private sealed class RoutingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handleAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handleAsync(request, cancellationToken);
    }
}
