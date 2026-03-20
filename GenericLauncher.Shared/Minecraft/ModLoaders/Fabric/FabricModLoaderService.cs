using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Http;
using GenericLauncher.Minecraft.ModLoaders.Fabric.Json;
using GenericLauncher.Misc;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Minecraft.ModLoaders.Fabric;

public sealed class FabricModLoaderService : IModLoaderService
{
    private const string FabricMetaBaseUrl = "https://meta.fabricmc.net/v2";
    private const string FabricMavenBaseUrl = "https://maven.fabricmc.net/";

    private readonly HttpClient _httpClient;
    private readonly FileDownloader _fileDownloader;
    private readonly ILogger? _logger;

    private readonly string _metadataFolder;
    private readonly string _librariesFolder;
    private readonly string _versionsFolder;

    public string DisplayName => "Fabric";

    public FabricModLoaderService(
        string fabricRootFolder,
        string librariesFolder,
        HttpClient httpClient,
        FileDownloader fileDownloader,
        ILogger? logger = null)
    {
        _httpClient = httpClient;
        _fileDownloader = fileDownloader;
        _logger = logger;

        _metadataFolder = Path.Combine(fabricRootFolder, "metadata");
        _librariesFolder = librariesFolder;
        _versionsFolder = Path.Combine(fabricRootFolder, "versions");
    }

    public async Task<ImmutableList<ModLoaderVersionInfo>> GetLoaderVersionsAsync(
        string minecraftVersionId,
        bool reload,
        CancellationToken cancellationToken = default)
    {
        _ = minecraftVersionId;
        Directory.CreateDirectory(_metadataFolder);

        var cachePath = Path.Combine(_metadataFolder, "loader_versions.json");
        var versionsJson = await GetCachedOrDownloadJsonAsync(
            cachePath,
            $"{FabricMetaBaseUrl}/versions/loader",
            reload,
            cancellationToken);

        var versions = JsonSerializer.Deserialize(
                           versionsJson,
                           FabricJsonContext.Default.ListFabricLoaderVersion)
                       ?? [];

        return versions
            .Select(v => new ModLoaderVersionInfo(v.Version, v.Stable ? "STABLE" : "UNKNOWN"))
            .ToImmutableList();
    }

    public async Task<ResolvedModLoaderVersion> ResolveAsync(
        string minecraftVersionId,
        string? preferredLoaderVersion,
        LauncherPlatform platform,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersionId))
        {
            throw new ArgumentException("Minecraft version id cannot be empty", nameof(minecraftVersionId));
        }

        _ = platform;

        var loaderVersions = await GetLoaderVersionsAsync(minecraftVersionId, false, cancellationToken);
        if (loaderVersions.Count == 0)
        {
            throw new InvalidOperationException("No Fabric loader versions available");
        }

        ModLoaderVersionInfo selectedLoader;
        if (!string.IsNullOrWhiteSpace(preferredLoaderVersion))
        {
            selectedLoader = loaderVersions.FirstOrDefault(v => v.VersionId == preferredLoaderVersion)
                             ?? throw new InvalidOperationException(
                                 $"Fabric loader version '{preferredLoaderVersion}' is not available");
        }
        else
        {
            selectedLoader = loaderVersions.FirstOrDefault(v => v.Channel == "STABLE") ?? loaderVersions[0];
        }

        var launchVersionId = $"fabric-loader-{selectedLoader.VersionId}-{minecraftVersionId}";
        Directory.CreateDirectory(_metadataFolder);
        Directory.CreateDirectory(_versionsFolder);

        var versionFolder = Path.Combine(_versionsFolder, launchVersionId);
        Directory.CreateDirectory(versionFolder);
        var profilePath = Path.Combine(versionFolder, "profile.json");
        var profileUrl =
            $"{FabricMetaBaseUrl}/versions/loader/{Uri.EscapeDataString(minecraftVersionId)}/{Uri.EscapeDataString(selectedLoader.VersionId)}/profile/json";
        var profileJson = await GetCachedOrDownloadJsonAsync(profilePath, profileUrl, true, cancellationToken);

        var profile = JsonSerializer.Deserialize(profileJson, FabricJsonContext.Default.FabricLauncherProfile)
                      ?? throw new InvalidOperationException("Failed to deserialize Fabric launcher profile");

        var extraJvmArgs = FlattenStringArguments(profile.Arguments?.Jvm);
        var extraGameArgs = FlattenStringArguments(profile.Arguments?.Game);
        var libraries = ResolveLibraries(profile.Libraries);

        cancellationToken.ThrowIfCancellationRequested();

        return new ResolvedModLoaderVersion(
            DisplayName,
            minecraftVersionId,
            launchVersionId,
            selectedLoader.VersionId,
            profilePath,
            null,
            null,
            string.IsNullOrWhiteSpace(profile.MainClass) ? null : profile.MainClass,
            extraJvmArgs,
            extraGameArgs,
            libraries);
    }

    public async Task InstallAsync(
        ResolvedModLoaderVersion resolved,
        ModLoaderInstallContext context,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        if (!string.Equals(resolved.DisplayName, DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Resolved version does not belong to Fabric loader", nameof(resolved));
        }

        Directory.CreateDirectory(_librariesFolder);

        if (resolved.Libraries.Count == 0)
        {
            progress?.Report(1.0);
            return;
        }

        var count = (double)resolved.Libraries.Count;
        var done = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 10,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(
            resolved.Libraries,
            parallelOptions,
            async (lib, token) =>
            {
                if (string.IsNullOrWhiteSpace(lib.Url))
                {
                    var skipped = Interlocked.Increment(ref done);
                    progress?.Report(skipped / count);
                    return;
                }

                await _fileDownloader.DownloadFileAsync(
                    lib.Url,
                    lib.FilePath,
                    lib.Sha1,
                    null,
                    token);

                var d = Interlocked.Increment(ref done);
                progress?.Report(d / count);
            });
    }

    private async Task<string> GetCachedOrDownloadJsonAsync(
        string cachePath,
        string url,
        bool reload,
        CancellationToken cancellationToken)
    {
        if (!reload && File.Exists(cachePath))
        {
            return await File.ReadAllTextAsync(cachePath, cancellationToken);
        }

        _logger?.LogInformation("Downloading Fabric metadata: {Url}", url);
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var folder = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        // Do not interrupt saving the data with cancellation, so we don't corrupt the file.
#pragma warning disable CA2016
        // ReSharper disable once MethodSupportsCancellation
        await File.WriteAllTextAsync(cachePath, json);
#pragma warning restore CA2016

        cancellationToken.ThrowIfCancellationRequested();
        return json;
    }

    private static ImmutableList<string> FlattenStringArguments(List<JsonElement>? args)
    {
        if (args is null || args.Count == 0)
        {
            return [];
        }

        return args
            .Where(a => a.ValueKind == JsonValueKind.String)
            .Select(a => a.GetString())
            .OfType<string>()
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToImmutableList();
    }

    private ImmutableList<ResolvedModLoaderLibrary> ResolveLibraries(List<FabricLibrary>? libraries)
    {
        if (libraries is null || libraries.Count == 0)
        {
            return [];
        }

        return libraries
            .Where(l => !string.IsNullOrWhiteSpace(l.Name))
            .DistinctBy(l => l.Name)
            .Select(lib =>
            {
                var relativePath = MavenToRelativePath(lib.Name);
                var baseUrl = string.IsNullOrWhiteSpace(lib.Url) ? FabricMavenBaseUrl : lib.Url!;
                var normalizedBaseUrl = baseUrl.EndsWith('/') ? baseUrl : $"{baseUrl}/";
                var url = $"{normalizedBaseUrl}{relativePath}";
                var filePath = Path.Combine(_librariesFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));

                return new ResolvedModLoaderLibrary(
                    lib.Name,
                    url,
                    filePath,
                    string.IsNullOrWhiteSpace(lib.Sha1) ? null : lib.Sha1);
            })
            .ToImmutableList();
    }

    private static string MavenToRelativePath(string maven) => MavenCoordinate.ToRelativePath(maven);
}
