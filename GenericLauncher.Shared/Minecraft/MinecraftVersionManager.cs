using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Http;
using GenericLauncher.Minecraft.Json;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Minecraft;

public sealed class MinecraftVersionManager : IDisposable
{
    public record Version(
        string VersionId,
        string Type, // no enum, so the parsing does not crash in the future
        int RequiredJavaVersion,
        string ClientJarPath,
        string MainClass,
        string InstallationFolder,
        string LibrariesFolder,
        string NativeLibrariesFolder,
        string AssetsFolder,
        string AssetIndex,
        List<string> ClassPath,
        List<string> GameArguments,
        List<string> JvmArguments);

    private const string SharedAssetsFolder = "assets";
    private const string SharedLibrariesFolder = "libraries";
    private const string NativeLibrariesFolder = "natives";
    private const string MinecraftVersionsFolder = "versions";
    private const string ManifestFilename = "version_manifest_v2.json";

    private readonly HttpClient _httpClient;
    private readonly FileDownloader _fileDownloader;
    private readonly ILogger? _logger;

    // piston-meta is the primary endpoint and launchermeta is just the legacy fallback
    private readonly string[] _manifestUrls =
    [
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
        "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json",
    ];

    private readonly string _minecraftVersionsFolder;
    private readonly string _sharedAssetsFolder;
    private readonly string _sharedLibrariesFolder;

    public MinecraftVersionManager(
        string baseFolder,
        HttpClient httpClient,
        FileDownloader fileDownloader,
        ILogger? logger)
    {
        _logger = logger;
        _httpClient = httpClient;
        _fileDownloader = fileDownloader;

        _minecraftVersionsFolder = Path.Combine(baseFolder, MinecraftVersionsFolder);
        _sharedAssetsFolder = Path.Combine(baseFolder, SharedAssetsFolder);
        _sharedLibrariesFolder = Path.Combine(baseFolder, SharedLibrariesFolder);
    }

    public async Task<IEnumerable<VersionInfo>> GetStableVersionsAsync(
        bool reload,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_minecraftVersionsFolder);
        var manifestJsonPath = Path.Combine(_minecraftVersionsFolder, ManifestFilename);

        if (!reload && File.Exists(manifestJsonPath))
        {
            try
            {
                // TODO: Use a Lazy<> property to cache the parsed manifest
                var manifestJson = await File.ReadAllTextAsync(manifestJsonPath, cancellationToken);
                var manifest = JsonSerializer.Deserialize(manifestJson, MinecraftJsonContext.Default.MinecraftManifest);
                if (manifest is not null)
                {
                    return manifest.Versions.Where(v => v.Type == VersionInfo.TypeRelease);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Problem loading Minecraft manifest from disk, fallback to online manifest");
            }
        }

        // Load from Minecraft API
        Exception? lastException = null;

        foreach (var url in _manifestUrls)
        {
            try
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                // sava/cache first
                var manifestJson = await response.Content.ReadAsStringAsync(cancellationToken);
                await File.WriteAllTextAsync(manifestJsonPath, manifestJson, cancellationToken);

                var manifest =
                    JsonSerializer.Deserialize(manifestJson, MinecraftJsonContext.Default.MinecraftManifest)
                    ?? throw new InvalidOperationException("Parsed manifest is empty");
                return manifest.Versions.Where(v => v.Type == VersionInfo.TypeRelease);
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger?.LogError(ex, "Failed to fetch Minecraft manifest from {url}", url);
            }
        }

        throw new InvalidOperationException("All manifest URLs failed", lastException);
    }

    public async Task<Version> GetCachedVersionDetailsAsync(
        string versionId,
        string currentOs,
        CancellationToken cancellationToken = default)
    {
        // TODO: Use Lazy<> or somehow cache these
        var installationFolder = GetInstallationFolder(versionId);
        var clientJsonPath = GetClientJsonPath(versionId);
        var versionDetailsJson = await File.ReadAllTextAsync(clientJsonPath, cancellationToken);

        var versionDetails = JsonSerializer.Deserialize(versionDetailsJson, MinecraftJsonContext.Default.VersionDetails)
                             ?? throw new InvalidOperationException($"Failed to get details for version '{versionId}'");

        return new Version(
            versionId,
            versionDetails.Type,
            versionDetails.JavaVersion?.MajorVersion ?? 8,
            GetClientJarPath(versionId),
            versionDetails.MainClass,
            installationFolder,
            _sharedLibrariesFolder,
            GetNativeLibrariesFolder(versionId),
            _sharedAssetsFolder,
            versionDetails.AssetIndex.Id,
            CreateClassPath(versionDetails.Libraries, currentOs),
            ArgumentsParser.FlattenArguments(versionDetails.Arguments?.Game, currentOs),
            ArgumentsParser.FlattenArguments(versionDetails.Arguments?.Jvm, currentOs)
        );
    }

    private string GetInstallationFolder(string versionId) => Path.Combine(_minecraftVersionsFolder, versionId);

    private string GetClientJsonPath(string versionId) =>
        Path.Combine(GetInstallationFolder(versionId), $"{versionId}.json");

    private string GetClientJarPath(string versionId) =>
        Path.Combine(GetInstallationFolder(versionId), $"{versionId}.jar");

    private string GetNativeLibrariesFolder(string versionId) =>
        Path.Combine(GetInstallationFolder(versionId), NativeLibrariesFolder);

    public async Task<(VersionDetails, Version)> DownloadVersionAsync(
        VersionInfo versionInfo,
        string currentOs,
        CancellationToken cancellationToken = default)
    {
        // We use the official Minecraft launcher's folder structure.
        // 1) assets & libraries are shared between all installations
        // 2) native libraries are extracted per some id/hash, not sure what it is, so we extract
        //    them per-instance
        var installationFolder = GetInstallationFolder(versionInfo.Id);
        Directory.CreateDirectory(installationFolder);

        var detailsResponse = await _httpClient.GetAsync(versionInfo.Url, cancellationToken);
        detailsResponse.EnsureSuccessStatusCode();

        // Save version metadata for future reference/launching the Minecraft
        var versionDetailsJson = await detailsResponse.Content.ReadAsStringAsync(cancellationToken);
        var clientJsonPath = GetClientJsonPath(versionInfo.Id);
        await File.WriteAllTextAsync(clientJsonPath, versionDetailsJson, cancellationToken);

        var versionDetails =
            JsonSerializer.Deserialize(versionDetailsJson, MinecraftJsonContext.Default.VersionDetails)
            ?? throw new InvalidOperationException($"Failed to get details for version '{versionInfo.Id}'");

        // WARN: We are not downloading the logging jar at all, because it will format the logs as
        //  XML and we do not need that. If we will need this in the future, look at the initial
        //  commit.

        var clientJarPath = GetClientJarPath(versionInfo.Id);
        var nativeLibrariesFolder = GetNativeLibrariesFolder(versionInfo.Id);

        return (
            versionDetails,
            new Version(
                versionInfo.Id,
                versionDetails.Type,
                versionDetails.JavaVersion?.MajorVersion ?? 8,
                clientJarPath,
                versionDetails.MainClass,
                installationFolder,
                _sharedLibrariesFolder,
                nativeLibrariesFolder,
                _sharedAssetsFolder,
                versionDetails.AssetIndex.Id,
                CreateClassPath(versionDetails.Libraries, currentOs),
                ArgumentsParser.FlattenArguments(versionDetails.Arguments?.Game, currentOs),
                ArgumentsParser.FlattenArguments(versionDetails.Arguments?.Jvm, currentOs)
            ));
    }

    public async Task DownloadAssetsAndLibraries(
        VersionDetails versionDetails,
        string currentOs,
        IProgress<double> minecraftDownloadProgress,
        IProgress<double> assetsDownloadProgress,
        IProgress<double> librariesDownloadProgress,
        CancellationToken cancellationToken = default)
    {
        var clientJarPath = GetClientJarPath(versionDetails.Id);
        var nativeLibrariesFolder = GetNativeLibrariesFolder(versionDetails.Id);

        var downloadLibrariesTask = DownloadLibrariesAsync(
            versionDetails.Libraries,
            currentOs,
            _sharedLibrariesFolder,
            nativeLibrariesFolder,
            librariesDownloadProgress,
            cancellationToken);

        await Task.WhenAll(
        [
            _fileDownloader.DownloadFileAsync(versionDetails.Downloads.Client.Url,
                clientJarPath,
                versionDetails.Downloads.Client.Sha1,
                minecraftDownloadProgress,
                cancellationToken),
            DownloadAssetsAsync(versionDetails.AssetIndex,
                _sharedAssetsFolder,
                assetsDownloadProgress,
                cancellationToken),
            downloadLibrariesTask,
        ]);
    }

    private List<string> CreateClassPath(List<Library>? libraries, string currentOs)
    {
        if (libraries is null || libraries.Count == 0)
        {
            return [];
        }

        return libraries.Where(l => IsLibraryAllowed(l, currentOs))
            .DistinctBy(l => l.Name)
            .Select(l => l.Downloads?.Artifact?.Path)
            .OfType<string>()
            // The paths use '/' as directory separator, but not every platform is using the same character...
            .Select(p => p.Replace('/', Path.DirectorySeparatorChar))
            .ToList();
    }

    private async Task DownloadLibrariesAsync(
        List<Library>? libraries,
        string currentOs,
        string librariesFolder,
        string nativeLibrariesFolder,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (libraries is null || libraries.Count == 0)
        {
            progress?.Report(100);
            return;
        }

        Directory.CreateDirectory(librariesFolder);
        Directory.CreateDirectory(nativeLibrariesFolder);

        // Download libraries in parallel
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 10,
            CancellationToken = cancellationToken,
        };

        // Every library has the (Maven) Name property, so we can filter-out duplicates
        var librariesToDownload = libraries
            .Where(l => IsLibraryAllowed(l, currentOs))
            .DistinctBy(l => l.Name)
            .ToList();
        var count = (double)librariesToDownload.Count;
        var downloaded = 0;

        await Parallel.ForEachAsync(librariesToDownload,
            parallelOptions,
            async (library, token) =>
            {
                // TODO: handle exceptions here, because first throw will stop starting new tasks

                token.ThrowIfCancellationRequested();

                // Normal libraries
                var artifact = library.Downloads?.Artifact;
                if (artifact is not null)
                {
                    await _fileDownloader.DownloadFileAsync(artifact.Url,
                        Path.Combine(librariesFolder, artifact.Path),
                        artifact.Sha1,
                        null,
                        token);
                }

                // Platform-specific libraries
                if (library.Natives == null || library.Downloads?.Classifiers == null)
                {
                    var d1 = Interlocked.Increment(ref downloaded);
                    progress?.Report(d1 / count);
                    return;
                }

                if (!library.Natives.TryGetValue(currentOs, out var nativeKey) ||
                    !library.Downloads.Classifiers.TryGetValue(nativeKey, out var nativeArtifact))
                {
                    var d2 = Interlocked.Increment(ref downloaded);
                    progress?.Report(d2 / count);
                    return;
                }

                var nativeLibraryPath = Path.Combine(nativeLibrariesFolder, Path.GetFileName(nativeArtifact.Path));
                await _fileDownloader.DownloadFileAsync(nativeArtifact.Url,
                    nativeLibraryPath,
                    nativeArtifact.Sha1,
                    null,
                    token);

                try
                {
                    await using var archive = await ZipFile.OpenReadAsync(nativeLibraryPath, token);
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                            entry.Name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                            entry.Name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
                        {
                            var extractPath = Path.Combine(nativeLibrariesFolder, entry.Name);
                            await entry.ExtractToFileAsync(extractPath, true, token);
                        }
                    }
                }
                catch
                {
                    // TODO
                }

                var d3 = Interlocked.Increment(ref downloaded);
                progress?.Report(d3 / count);

                File.Delete(nativeLibraryPath);
            });

        progress?.Report(1.0);
    }

    private async Task DownloadAssetsAsync(
        AssetIndex assetIndex,
        string assetsFolder,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        _logger?.LogInformation("downloading assets index: {AssetIndexUrl}", assetIndex.Url);

        Directory.CreateDirectory(assetsFolder);

        // Download asset index
        var indexesFolder = Path.Combine(assetsFolder, "indexes");
        Directory.CreateDirectory(indexesFolder);
        var assetsIndexPath = Path.Combine(indexesFolder, $"{assetIndex.Id}.json");
        await _fileDownloader.DownloadFileAsync(assetIndex.Url,
            assetsIndexPath,
            assetIndex.Sha1,
            null,
            cancellationToken);

        // Parse assets index and download assets
        var assetsJson = await File.ReadAllTextAsync(assetsIndexPath, cancellationToken);
        var assetsManifest = JsonSerializer.Deserialize(assetsJson, MinecraftJsonContext.Default.AssetsManifest)
                             ?? throw new InvalidOperationException("Failed to deserialize assets manifest");

        // Minecraft assets can contain duplicate file URLs/hashes e.g., in 1.18, so we take just the unique values.
        var assets = assetsManifest.Objects.Values
            .DistinctBy(a => a.Hash)
            .ToList();

        // Download the objects/assets now
        var objectFolder = Path.Combine(assetsFolder, "objects");
        Directory.CreateDirectory(objectFolder);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 10,
            CancellationToken = cancellationToken,
        };

        var count = (double)assets.Count;
        var downloaded = 0;

        await Parallel.ForEachAsync(assets,
            parallelOptions,
            async (asset, token) =>
            {
                var assetPath = Path.Combine(objectFolder, asset.Hash[..2], asset.Hash);
                if (File.Exists(assetPath))
                {
                    var d1 = Interlocked.Increment(ref downloaded);
                    progress.Report(d1 / count);
                    return;
                }

                token.ThrowIfCancellationRequested();

                var assetUrl = $"https://resources.download.minecraft.net/{asset.Hash[..2]}/{asset.Hash}";
                await _fileDownloader.DownloadFileAsync(assetUrl, assetPath, asset.Hash, null, token);

                var d = Interlocked.Increment(ref downloaded);
                progress.Report(d / count);
            });

        progress.Report(1.0);
    }

    private bool IsLibraryAllowed(Library library, string currentOs)
    {
        // Same implementation as in MinecraftService
        if (library.Rules == null || library.Rules.Count == 0)
        {
            return true;
        }

        var allowed = true;
        foreach (var rule in library.Rules)
        {
            if (rule.Os == null)
            {
                continue;
            }

            // TODO: Handle also the Os.Version, which can have a regex value like "^10\\." -- client_1.17.json
            var matchesOs = rule.Os.Name?.ToLower() switch
            {
                "windows" => currentOs == "windows",
                "linux" => currentOs == "linux",
                "osx" => currentOs == "osx",
                _ => true,
            };

            allowed = rule.Action switch
            {
                "allow" => matchesOs,
                "disallow" => !matchesOs,
                _ => allowed,
            };
        }

        return allowed;
    }

    private bool IsRuleAllowed(List<Rule>? rules, string currentOs)
    {
        if (rules == null || rules.Count == 0)
        {
            return true;
        }

        var allowed = true;
        foreach (var rule in rules)
        {
            if (rule.Os == null)
            {
                continue;
            }

            var matchesOs = rule.Os.Name?.ToLower() switch
            {
                "windows" => currentOs == "windows",
                "linux" => currentOs == "linux",
                "osx" => currentOs == "osx",
                _ => true,
            };

            allowed = rule.Action switch
            {
                "allow" => matchesOs,
                "disallow" => !matchesOs,
                _ => allowed,
            };
        }

        return allowed;
    }

    public bool IsVersionInstalled(string versionId) => File.Exists(GetClientJarPath(versionId));

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
