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
using GenericLauncher.Misc;
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

    private readonly LauncherPlatform _platform;
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
        LauncherPlatform platform,
        HttpClient httpClient,
        FileDownloader fileDownloader,
        ILogger? logger)
    {
        _platform = platform;
        _logger = logger;
        _httpClient = httpClient;
        _fileDownloader = fileDownloader;

        var baseFolder = Path.Combine(platform.AppDataPath, "mc");
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
            CreateClassPath(versionDetails.Libraries),
            ArgumentsParser.FlattenArguments(versionDetails.Arguments?.Game, _platform),
            ArgumentsParser.FlattenArguments(versionDetails.Arguments?.Jvm, _platform)
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
                CreateClassPath(versionDetails.Libraries),
                ArgumentsParser.FlattenArguments(versionDetails.Arguments?.Game, _platform),
                ArgumentsParser.FlattenArguments(versionDetails.Arguments?.Jvm, _platform)
            ));
    }

    public async Task DownloadAssetsAndLibraries(
        VersionDetails versionDetails,
        IProgress<double> minecraftDownloadProgress,
        IProgress<double> assetsDownloadProgress,
        IProgress<double> librariesDownloadProgress,
        CancellationToken cancellationToken = default)
    {
        var clientJarPath = GetClientJarPath(versionDetails.Id);
        var nativeLibrariesFolder = GetNativeLibrariesFolder(versionDetails.Id);

        var downloadLibrariesTask = DownloadLibrariesAsync(
            versionDetails.Libraries,
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

    private List<string> CreateClassPath(List<Library>? libraries)
    {
        return SelectLibraries(libraries)
            .Select(l => l.Downloads?.Artifact?.Path)
            .OfType<string>()
            // The paths use '/' as directory separator, but not every platform is using the same character...
            .Select(p => p.Replace('/', Path.DirectorySeparatorChar))
            .ToList();
    }

    private async Task DownloadLibrariesAsync(
        List<Library>? libraries,
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
        var librariesToDownload = SelectLibraries(libraries);
        if (librariesToDownload.Count == 0)
        {
            progress?.Report(1.0);
            return;
        }

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
                if (library.Natives is null || library.Downloads?.Classifiers is null)
                {
                    var d1 = Interlocked.Increment(ref downloaded);
                    progress?.Report(d1 / count);
                    return;
                }

                if (!library.Natives.TryGetValue(_platform.CurrentOs, out var nativeKey) ||
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

    private List<Library> SelectLibraries(List<Library>? libraries)
    {
        // TODO: Still the same implementation as in MinecraftService ???
        if (libraries is null || libraries.Count == 0)
        {
            return [];
        }

        var selected = new List<Library>();
        var groupedDirectNatives = new Dictionary<string, List<(Library Library, int Rank)>>();

        foreach (var library in libraries)
        {
            if (!ArgumentsParser.IsRuleAllowed(library.Rules, _platform))
            {
                continue;
            }

            var directNativeGroupKey = GetDirectNativeGroupKey(library);
            if (directNativeGroupKey is null)
            {
                selected.Add(library);
                continue;
            }

            var directNativeRank = GetDirectNativeRank(library);
            if (directNativeRank is null)
            {
                continue;
            }

            if (!groupedDirectNatives.TryGetValue(directNativeGroupKey, out var group))
            {
                group = [];
                groupedDirectNatives[directNativeGroupKey] = group;
            }

            group.Add((library, directNativeRank.Value));
        }

        foreach (var group in groupedDirectNatives.Values)
        {
            selected.Add(group.OrderBy(v => v.Rank).ThenBy(v => v.Library.Name, StringComparer.Ordinal).First().Library);
        }

        return selected
            .DistinctBy(l => l.Name)
            .ToList();
    }

    internal List<string> CreateClassPathForTesting(List<Library>? libraries) => CreateClassPath(libraries);

    private static string? GetDirectNativeGroupKey(Library library)
    {
        if (library.Downloads?.Artifact?.Path?.Contains("-natives-") != true)
        {
            return null;
        }

        var parts = library.Name.Split(':');
        if (parts.Length < 4 || !parts[3].StartsWith("natives-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return string.Join(':', parts.Take(3));
    }

    private int? GetDirectNativeRank(Library library)
    {
        var parts = library.Name.Split(':');
        if (parts.Length < 4)
        {
            return null;
        }

        var classifier = parts[3].ToLowerInvariant();
        if (!classifier.StartsWith("natives-", StringComparison.Ordinal))
        {
            return null;
        }

        return (_platform.CurrentOs, _platform.Architecture, classifier) switch
        {
            ("osx", "arm64", "natives-macos-arm64") => 0,
            ("osx", "arm64", "natives-osx-arm64") => 0,
            ("osx", "arm64", "natives-macos") => 1,
            ("osx", "arm64", "natives-osx") => 1,
            ("osx", "x64", "natives-macos") => 0,
            ("osx", "x64", "natives-osx") => 0,
            ("osx", "x64", "natives-macos-x64") => 1,
            ("osx", "x64", "natives-osx-x64") => 1,
            ("windows", "arm64", "natives-windows-arm64") => 0,
            ("windows", "arm64", "natives-windows") => 1,
            ("windows", "x64", "natives-windows") => 0,
            ("windows", "x64", "natives-windows-x64") => 1,
            ("windows", "x86", "natives-windows-x86") => 0,
            ("windows", "x86", "natives-windows") => 1,
            ("linux", "arm64", "natives-linux-arm64") => 0,
            ("linux", "arm64", "natives-linux-aarch64") => 0,
            ("linux", "arm64", "natives-linux-aarch_64") => 0,
            ("linux", "arm64", "natives-linux") => 1,
            ("linux", "x64", "natives-linux") => 0,
            ("linux", "x64", "natives-linux-x64") => 1,
            ("linux", "x64", "natives-linux-x86_64") => 1,
            _ => null,
        };
    }

    public bool IsVersionInstalled(string versionId) => File.Exists(GetClientJarPath(versionId));

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
