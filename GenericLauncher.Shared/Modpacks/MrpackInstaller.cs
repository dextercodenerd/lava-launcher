using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Database.Model;
using GenericLauncher.Http;
using GenericLauncher.Modpacks.Json;

namespace GenericLauncher.Modpacks;

public sealed class MrpackInstaller(FileDownloader fileDownloader)
{
    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com",
        "github.com",
        "raw.githubusercontent.com",
        "gitlab.com",
    };

    private static readonly HashSet<string> ReservedRootPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "meta.json",
        ".mod-tmp",
        ".pack-tmp",
        "natives",
    };

    public async Task<MrpackClientInstallPlan> CreateClientInstallPlanAsync(
        string mrpackPath,
        string instanceRoot,
        CancellationToken cancellationToken = default)
    {
        await using var fileStream = File.OpenRead(mrpackPath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read, false);
        var index = await ReadIndexAsync(archive, cancellationToken);
        ValidateIndex(index);

        var dependencies = CreateDependencies(index);
        var root = NormalizeRoot(instanceRoot);
        var files = ImmutableArray.CreateBuilder<MrpackPlannedFile>();
        var skipped = ImmutableArray.CreateBuilder<MrpackSkippedFile>();

        foreach (var file in index.Files ?? [])
        {
            var path = RequireValue(file.Path, "Manifest file path is required.");
            ValidateEnvironment(file.Env, path);

            var clientSide = file.Env?.Client;
            if (string.Equals(clientSide, "optional", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(new MrpackSkippedFile(path, "Client file is optional."));
                continue;
            }

            if (string.Equals(clientSide, "unsupported", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(new MrpackSkippedFile(path, "Client file is unsupported."));
                continue;
            }

            if (file.Env is not null
                && clientSide is null)
            {
                skipped.Add(new MrpackSkippedFile(path, "Client file is not required."));
                continue;
            }

            _ = ValidateRelativePath(root, path);
            var hashes = file.Hashes ??
                         throw new InvalidOperationException($"Manifest file '{path}' is missing hashes.");
            var sha1 = RequireHash(hashes.Sha1, 40, path, "sha1");
            var sha512 = RequireHash(hashes.Sha512, 128, path, "sha512");
            var downloads = ValidateDownloads(file.Downloads, path);

            files.Add(new MrpackPlannedFile(
                path,
                downloads,
                sha1,
                sha512,
                file.FileSize));
        }

        var overrides = CreateOverridePlan(archive, root);

        return new MrpackClientInstallPlan(
            new MrpackIdentity(
                RequireValue(index.Name, "Manifest name is required."),
                RequireValue(index.VersionId, "Manifest versionId is required."),
                index.Summary),
            dependencies,
            files.ToImmutable(),
            overrides,
            skipped.ToImmutable());
    }

    public async Task MaterializeClientInstallPlanAsync(
        string mrpackPath,
        MrpackClientInstallPlan plan,
        string instanceRoot,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(instanceRoot);
        var tempRoot = Path.Combine(root, ".pack-tmp", Guid.NewGuid().ToString("N"));
        var backupsRoot = Path.Combine(tempRoot, "backups");
        var writtenPaths = new List<string>();
        var originalBackups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var touchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Directory.CreateDirectory(tempRoot);

            for (var i = 0; i < plan.Files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plannedFile = plan.Files[i];
                var destination = ValidateRelativePath(root, plannedFile.Path);
                var tempDestination = Path.Combine(tempRoot, "downloads", i.ToString(), Path.GetFileName(destination));
                await DownloadWithFallbackAsync(plannedFile, tempDestination, cancellationToken);
                MoveIntoPlace(tempDestination, destination, writtenPaths, originalBackups, touchedPaths, backupsRoot);
            }

            await using var fileStream = File.OpenRead(mrpackPath);
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read, false);

            foreach (var plannedOverride in plan.Overrides)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ValidateRelativePath(root, plannedOverride.Path);
                var entry = archive.GetEntry(plannedOverride.ArchivePath)
                            ?? throw new InvalidOperationException(
                                $"Override entry '{plannedOverride.ArchivePath}' is missing.");
                var tempDestination = Path.Combine(tempRoot, "overrides", Guid.NewGuid().ToString("N"));
                var tempDirectory = Path.GetDirectoryName(tempDestination);
                if (!string.IsNullOrEmpty(tempDirectory))
                {
                    Directory.CreateDirectory(tempDirectory);
                }

                await using (var source = await entry.OpenAsync(cancellationToken))
                await using (var target = File.Create(tempDestination))
                {
                    await source.CopyToAsync(target, cancellationToken);
                }

                MoveIntoPlace(tempDestination, destination, writtenPaths, originalBackups, touchedPaths, backupsRoot);
            }
        }
        catch
        {
            RestoreBackups(writtenPaths, originalBackups);
            throw;
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    private static async Task<MrpackIndex> ReadIndexAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry("modrinth.index.json")
                    ?? throw new InvalidOperationException("The .mrpack archive is missing modrinth.index.json.");
        await using var stream = await entry.OpenAsync(cancellationToken);
        var index = await JsonSerializer.DeserializeAsync(
            stream,
            MrpackJsonContext.Default.MrpackIndex,
            cancellationToken);
        return index ?? throw new InvalidOperationException("The .mrpack manifest is empty.");
    }

    private static void ValidateIndex(MrpackIndex index)
    {
        if (index.FormatVersion != 1)
        {
            throw new InvalidOperationException(
                $"Unsupported .mrpack formatVersion '{index.FormatVersion}'. Only formatVersion 1 is supported.");
        }

        if (!string.Equals(index.Game, "minecraft", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported .mrpack game '{index.Game}'. Only minecraft is supported.");
        }

        _ = RequireValue(index.Name, "Manifest name is required.");
        _ = RequireValue(index.VersionId, "Manifest versionId is required.");
        if (index.Files is null)
        {
            throw new InvalidOperationException("Manifest files are required.");
        }
    }

    private static MrpackDependencies CreateDependencies(MrpackIndex index)
    {
        var dependencies = index.Dependencies
                           ?? throw new InvalidOperationException("Manifest dependencies are required.");
        if (!dependencies.TryGetValue("minecraft", out var minecraftVersion)
            || string.IsNullOrWhiteSpace(minecraftVersion))
        {
            throw new InvalidOperationException("Manifest dependencies must include minecraft.");
        }

        var modLoader = MinecraftInstanceModLoader.Vanilla;
        string? modLoaderVersion = null;

        foreach (var dependency in dependencies)
        {
            switch (dependency.Key)
            {
                case "minecraft":
                    break;
                case "fabric-loader":
                    SetLoader(MinecraftInstanceModLoader.Fabric, dependency.Value);
                    break;
                case "forge":
                    SetLoader(MinecraftInstanceModLoader.Forge, dependency.Value);
                    break;
                case "neoforge":
                    SetLoader(MinecraftInstanceModLoader.NeoForge, dependency.Value);
                    break;
                case "quilt-loader":
                    throw new InvalidOperationException("Unsupported .mrpack loader dependency 'quilt-loader'.");
                default:
                    throw new InvalidOperationException($"Unsupported .mrpack dependency '{dependency.Key}'.");
            }
        }

        return new MrpackDependencies(minecraftVersion, modLoader, modLoaderVersion);

        void SetLoader(MinecraftInstanceModLoader nextLoader, string version)
        {
            if (modLoader != MinecraftInstanceModLoader.Vanilla)
            {
                throw new InvalidOperationException("Manifest declares multiple mod loader dependencies.");
            }

            modLoader = nextLoader;
            modLoaderVersion = RequireValue(version, "Mod loader dependency version is required.");
        }
    }

    private static void ValidateEnvironment(MrpackEnv? env, string path)
    {
        ValidateEnvValue(env?.Client, path, "client");
        ValidateEnvValue(env?.Server, path, "server");
    }

    private static void ValidateEnvValue(string? value, string path, string side)
    {
        if (value is null)
        {
            return;
        }

        if (string.Equals(value, "required", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "optional", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException($"Manifest file '{path}' has unknown env.{side} value '{value}'.");
    }

    private static ImmutableArray<string> ValidateDownloads(string[]? downloads, string path)
    {
        if (downloads is null || downloads.Length == 0)
        {
            throw new InvalidOperationException($"Manifest file '{path}' must include at least one download URL.");
        }

        var builder = ImmutableArray.CreateBuilder<string>(downloads.Length);
        foreach (var download in downloads)
        {
            if (!Uri.TryCreate(download, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Manifest file '{path}' has a non-HTTPS download URL.");
            }

            if (!AllowedDownloadHosts.Contains(uri.Host))
            {
                throw new InvalidOperationException(
                    $"Manifest file '{path}' uses unsupported download host '{uri.Host}'.");
            }

            builder.Add(download);
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<MrpackOverrideEntry> CreateOverridePlan(
        ZipArchive archive,
        string root)
    {
        var builder = ImmutableArray.CreateBuilder<MrpackOverrideEntry>();
        AddLayer("overrides/");
        AddLayer("client-overrides/");
        return builder.ToImmutable();

        void AddLayer(string prefix)
        {
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)
                    || IsDirectoryEntry(entry))
                {
                    continue;
                }

                var relativePath = entry.FullName[prefix.Length..];
                _ = ValidateRelativePath(root, relativePath);
                builder.Add(new MrpackOverrideEntry(entry.FullName, relativePath));
            }
        }
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/')
        || entry.FullName.EndsWith('\\');

    private async Task DownloadWithFallbackAsync(
        MrpackPlannedFile plannedFile,
        string tempDestination,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        foreach (var download in plannedFile.Downloads)
        {
            try
            {
                await fileDownloader.DownloadFileAsync(
                    download,
                    tempDestination,
                    plannedFile.Sha512,
                    cancellationToken: cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = ex;
                if (File.Exists(tempDestination))
                {
                    File.Delete(tempDestination);
                }
            }
        }

        throw new InvalidOperationException(
            $"Failed to download .mrpack file '{plannedFile.Path}'.",
            lastFailure);
    }

    private static void MoveIntoPlace(
        string source,
        string destination,
        List<string> writtenPaths,
        Dictionary<string, string> originalBackups,
        HashSet<string> touchedPaths,
        string backupsRoot)
    {
        var destinationDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        if (File.Exists(destination) && !touchedPaths.Contains(destination))
        {
            Directory.CreateDirectory(backupsRoot);
            var backup = Path.Combine(backupsRoot, Guid.NewGuid().ToString("N"));
            File.Move(destination, backup);
            originalBackups.Add(destination, backup);
        }
        else if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        File.Move(source, destination);
        touchedPaths.Add(destination);
        writtenPaths.Add(destination);
    }

    private static void RestoreBackups(
        List<string> writtenPaths,
        Dictionary<string, string> backups)
    {
        foreach (var path in writtenPaths.AsEnumerable().Reverse())
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        foreach (var backup in backups)
        {
            var directory = Path.GetDirectoryName(backup.Key);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Move(backup.Value, backup.Key, true);
        }
    }

    private static string NormalizeRoot(string instanceRoot)
    {
        var fullPath = Path.GetFullPath(instanceRoot);
        return fullPath.EndsWith(Path.DirectorySeparatorChar)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private static string ValidateRelativePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Pack path is empty.");
        }

        if (Path.IsPathFullyQualified(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"Pack path '{relativePath}' must be relative.");
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith('/')
            || normalized.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Pack path '{relativePath}' contains an empty segment.");
        }

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
        {
            throw new InvalidOperationException($"Pack path '{relativePath}' contains an unsafe segment.");
        }

        if (ReservedRootPaths.Contains(segments[0]))
        {
            throw new InvalidOperationException(
                $"Pack path '{relativePath}' targets launcher-owned path '{segments[0]}'.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Pack path '{relativePath}' resolves outside the instance root.");
        }

        return fullPath;
    }

    private static string RequireValue(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;

    private static string RequireHash(string? value, int length, string path, string algorithm)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != length || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException($"Manifest file '{path}' is missing a valid {algorithm} hash.");
        }

        return value.ToLowerInvariant();
    }
}
