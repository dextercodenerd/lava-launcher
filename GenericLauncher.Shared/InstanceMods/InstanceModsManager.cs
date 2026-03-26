using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Database.Model;
using GenericLauncher.Http;
using GenericLauncher.InstanceMods.Json;
using GenericLauncher.Misc;
using GenericLauncher.Modrinth;
using GenericLauncher.Modrinth.Json;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.InstanceMods;

public sealed class InstanceModsManager
{
    private const string MetaFileName = "meta.json";
    private const string ModsFolderName = "mods";
    private const int CurrentSchemaVersion = 1;
    private const int MaxDirectModResolutionConcurrency = 4;
    private const int MaxCompatibleInstanceResolutionConcurrency = 4;
    private const int MaxLatestVersionResolutionConcurrency = 4;
    private const int MaxConcurrentInstanceStateReaders = 8;
    private const string InstallKindDirect = "Direct";
    private const string InstallKindDependency = "Dependency";

    private readonly LauncherPlatform _platform;
    private readonly ModrinthApiClient _modrinthApiClient;
    private readonly FileDownloader _fileDownloader;
    private readonly Func<IEnumerable<MinecraftInstance>>? _instanceProvider;
    private readonly ILogger? _logger;
    private readonly string _instancesRoot;
    private readonly Lock _snapshotLock = new();
    private readonly Lock _latestCompatibleVersionCacheLock = new();

    private readonly ConcurrentDictionary<string, AsyncRwLock> _instanceStateLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, InstanceModsSnapshot> _snapshots =
        new(StringComparer.OrdinalIgnoreCase);

    // TODO: Add cache eviction (size cap or TTL) to prevent unbounded growth across sessions.
    private readonly Dictionary<string, LatestCompatibleVersionInfo?> _latestCompatibleVersionCache =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<InstanceModsSnapshotChangedEventArgs>? InstanceModsChanged;

    public InstanceModsManager(
        LauncherPlatform platform,
        ModrinthApiClient modrinthApiClient,
        FileDownloader fileDownloader,
        Func<IEnumerable<MinecraftInstance>>? instanceProvider = null,
        ILogger? logger = null)
    {
        _platform = platform;
        _modrinthApiClient = modrinthApiClient;
        _fileDownloader = fileDownloader;
        _instanceProvider = instanceProvider;
        _logger = logger;
        _instancesRoot = Path.Combine(platform.AppDataPath, "instances");
    }

    public async Task EnsureInstanceMetadataAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default)
    {
        var folderPath = GetInstanceFolder(instance.Folder);
        var metadataChanged = await ExecuteWithInstanceWriteLockAsync(
            folderPath,
            async token =>
            {
                Directory.CreateDirectory(folderPath);

                var existing = await TryReadMetaUnlockedAsync(GetMetaPath(folderPath), token);
                var updated = existing is null
                    ? CreateEmptyMeta(instance)
                    : existing with
                    {
                        DisplayName = instance.Id,
                        MinecraftVersionId = instance.VersionId,
                        ModLoader = MinecraftInstance.ModLoaderToString(instance.ModLoader),
                        ModLoaderVersion = instance.ModLoaderVersion,
                        LaunchVersionId = instance.LaunchVersionId,
                    };

                if (existing == updated)
                {
                    return false;
                }

                await WriteMetaUnlockedAsync(folderPath, updated, token);
                return true;
            },
            cancellationToken);

        if (metadataChanged)
        {
            InvalidateSnapshot(instance.Id);
        }
    }

    public async Task<InstanceModsSnapshot> GetSnapshotAsync(
        MinecraftInstance instance,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && TryGetSnapshot(instance.Id, out var cached))
        {
            return cached;
        }

        var folderPath = GetInstanceFolder(instance.Folder);
        var localState = await ReadLocalStateAsync(instance, folderPath, cancellationToken);
        var snapshot = BuildSnapshot(localState.Meta, localState.ScannedFiles);
        StoreSnapshot(instance.Id, snapshot, false);
        return snapshot;
    }

    public async Task<ImmutableDictionary<string, LatestCompatibleVersionInfo>> GetLatestCompatibleVersionsAsync(
        MinecraftInstance instance,
        IEnumerable<string> projectIds,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var distinctProjectIds = projectIds
            .Where(projectId => !string.IsNullOrWhiteSpace(projectId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctProjectIds.Length == 0)
        {
            return ImmutableDictionary<string, LatestCompatibleVersionInfo>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase);
        }

        var latestVersions = new LatestCompatibleVersionInfo?[distinctProjectIds.Length];
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxLatestVersionResolutionConcurrency,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, distinctProjectIds.Length),
            parallelOptions,
            async (index, token) =>
            {
                var latestVersion = await GetLatestCompatibleVersionAsync(
                    instance,
                    distinctProjectIds[index],
                    forceRefresh,
                    token);
                if (latestVersion is not null)
                {
                    latestVersions[index] = latestVersion;
                }
            });

        return latestVersions
            .OfType<LatestCompatibleVersionInfo>()
            .ToImmutableDictionary(version => version.ProjectId, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ImmutableList<PortableInstanceCandidate>> DiscoverPortableInstancesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_instancesRoot))
        {
            return [];
        }

        var result = new List<PortableInstanceCandidate>();
        foreach (var folder in Directory.GetDirectories(_instancesRoot))
        {
            var metaPath = Path.Combine(folder, MetaFileName);
            if (!File.Exists(metaPath))
            {
                continue;
            }

            var meta = await ExecuteWithInstanceReadLockAsync(
                folder,
                token => TryReadMetaUnlockedAsync(metaPath, token),
                cancellationToken);
            if (meta is null || !IsPortableMetaValid(meta))
            {
                _logger?.LogWarning("Skipping invalid portable instance metadata at {MetaPath}", metaPath);
                continue;
            }

            result.Add(new PortableInstanceCandidate(
                Path.GetFileName(folder),
                folder,
                meta));
        }

        return result.ToImmutableList();
    }

    public async Task<ImmutableList<InstanceModListItem>> ListModsAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default)
    {
        return (await GetSnapshotAsync(instance, cancellationToken: cancellationToken)).AllItems;
    }

    public async Task InstallProjectAsync(
        MinecraftInstance instance,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ApplyManagedModChangeAsync(
            instance,
            (meta, token) => Task.FromResult(BuildDesiredDirectMods(meta, projectId, ModChangeKind.Install)),
            cancellationToken);
        PublishSnapshot(instance.Id, snapshot);
    }

    public async Task UpdateModAsync(
        MinecraftInstance instance,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ApplyManagedModChangeAsync(
            instance,
            (meta, token) => Task.FromResult(BuildDesiredDirectMods(meta, projectId, ModChangeKind.Update)),
            cancellationToken);
        PublishSnapshot(instance.Id, snapshot);
    }

    public async Task UpdateAllAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ApplyManagedModChangeAsync(
            instance,
            (meta, token) => Task.FromResult(BuildDesiredDirectMods(meta, null, ModChangeKind.UpdateAll)),
            cancellationToken);
        PublishSnapshot(instance.Id, snapshot);
    }

    public async Task DeleteModAsync(
        MinecraftInstance instance,
        string modKey,
        CancellationToken cancellationToken = default)
    {
        var folderPath = GetInstanceFolder(instance.Folder);
        var modsFolder = Path.Combine(folderPath, ModsFolderName);
        await EnsureInstanceMetadataAsync(instance, cancellationToken);

        var deleteTarget = await ExecuteWithInstanceReadLockAsync(
            folderPath,
            async token =>
            {
                var meta = await ReadMetaUnlockedAsync(folderPath, token);
                var managed = meta.Mods.FirstOrDefault(m =>
                    string.Equals(m.ProjectId, modKey, StringComparison.Ordinal));
                if (managed is not null)
                {
                    return managed.InstallKind.Equals(InstallKindDependency, StringComparison.OrdinalIgnoreCase)
                        ? DeleteTarget.Dependency
                        : DeleteTarget.ManagedDirect;
                }

                var manualPath = Path.Combine(modsFolder, modKey);
                return File.Exists(manualPath) ? DeleteTarget.Manual : DeleteTarget.Missing;
            },
            cancellationToken);

        switch (deleteTarget)
        {
            case DeleteTarget.Dependency:
                throw new InvalidOperationException("Required dependencies cannot be deleted manually in v1.");
            case DeleteTarget.ManagedDirect:
            {
                var managedSnapshot = await ApplyManagedModChangeAsync(
                    instance,
                    (meta, token) => Task.FromResult(BuildDesiredDirectMods(meta, modKey, ModChangeKind.Remove)),
                    cancellationToken);
                PublishSnapshot(instance.Id, managedSnapshot);
                return;
            }
            case DeleteTarget.Manual:
            {
                var manualSnapshot = await ExecuteWithInstanceWriteLockAsync(
                    folderPath,
                    async token =>
                    {
                        Directory.CreateDirectory(modsFolder);
                        var manualPath = Path.Combine(modsFolder, modKey);
                        if (!File.Exists(manualPath))
                        {
                            return null;
                        }

                        File.Delete(manualPath);
                        var localState = await ReadLocalStateUnlockedAsync(folderPath, token);
                        var snapshot = BuildSnapshot(localState.Meta, localState.ScannedFiles);
                        StoreSnapshot(instance.Id, snapshot, raiseEvent: false);
                        return snapshot;
                    },
                    cancellationToken);

                if (manualSnapshot is not null)
                {
                    PublishSnapshot(instance.Id, manualSnapshot);
                }

                return;
            }
            default:
                return;
        }
    }

    public async Task<ImmutableList<CompatibleInstanceInstallTarget>> GetCompatibleInstancesAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        if (_instanceProvider is null)
        {
            return [];
        }

        var instances = _instanceProvider()
            .Where(i => i.State == MinecraftInstanceState.Ready)
            .Where(i => i.ModLoader is MinecraftInstanceModLoader.Fabric
                or MinecraftInstanceModLoader.Forge
                or MinecraftInstanceModLoader.NeoForge)
            .ToArray();
        if (instances.Length == 0)
        {
            return [];
        }

        var targets = new CompatibleInstanceInstallTarget?[instances.Length];
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxCompatibleInstanceResolutionConcurrency,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, instances.Length),
            parallelOptions,
            async (index, token) =>
            {
                var instance = instances[index];
                var latestVersion =
                    await GetLatestCompatibleVersionAsync(instance, projectId, forceRefresh: false, token);
                if (latestVersion is null)
                {
                    return;
                }

                targets[index] = new CompatibleInstanceInstallTarget(instance, latestVersion.VersionNumber);
            });

        return targets
            .OfType<CompatibleInstanceInstallTarget>()
            .OrderBy(target => target.Instance.Id, StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();
    }

    internal async Task<ModrinthVersion?> TryResolveLatestCompatibleVersionAsync(
        MinecraftInstance instance,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var versions = await _modrinthApiClient.GetProjectVersionsAsync(
            projectId,
            instance.VersionId,
            GetModrinthLoader(instance.ModLoader),
            cancellationToken);
        if (versions is null || versions.Length == 0)
        {
            return null;
        }

        return SelectBestVersion(versions);
    }

    internal static ModrinthVersion SelectBestVersion(IEnumerable<ModrinthVersion> versions) => versions
        .OrderBy(version => GetVersionTypeRank(version.VersionType))
        .ThenByDescending(version => ParseDate(version.DatePublished))
        .First();

    internal static ModrinthVersionFile SelectInstallFile(ModrinthVersion version)
    {
        var jar = version.Files
            .Where(file => file.Filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.Primary)
            .ThenBy(file => file.Filename, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return jar ?? throw new InvalidOperationException($"No installable jar file found for {version.Id}");
    }

    private static Dictionary<string, string?> BuildDesiredDirectMods(
        InstanceMeta meta,
        string? projectId,
        ModChangeKind changeKind)
    {
        var directMods = meta.Mods
            .Where(m => m.InstallKind.Equals(InstallKindDirect, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(m => m.ProjectId, m => (string?)m.InstalledVersionId, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            switch (changeKind)
            {
                case ModChangeKind.Remove:
                    directMods.Remove(projectId);
                    break;
                case ModChangeKind.Update:
                    directMods[projectId] = null;
                    break;
                case ModChangeKind.Install when !directMods.ContainsKey(projectId):
                    directMods[projectId] = null;
                    break;
            }
        }

        if (changeKind == ModChangeKind.UpdateAll)
        {
            foreach (var key in directMods.Keys.ToArray())
            {
                directMods[key] = null;
            }
        }

        return directMods;
    }

    private async Task<InstanceModsSnapshot> ApplyManagedModChangeAsync(
        MinecraftInstance instance,
        Func<InstanceMeta, CancellationToken, Task<Dictionary<string, string?>>> buildDesiredDirectModsAsync,
        CancellationToken cancellationToken)
    {
        var folderPath = GetInstanceFolder(instance.Folder);
        await EnsureInstanceMetadataAsync(instance, cancellationToken);

        var observedLocalState = await ReadLocalStateAsync(instance, folderPath, cancellationToken);
        var desiredDirectMods = await buildDesiredDirectModsAsync(observedLocalState.Meta, cancellationToken);
        var resolution = await ResolveDesiredDirectModsAsync(instance, desiredDirectMods, cancellationToken);
        var preparedDownloads = await PrepareDownloadsAsync(folderPath, resolution, cancellationToken);

        try
        {
            return await ExecuteWithInstanceWriteLockAsync(
                folderPath,
                async token =>
                {
                    var currentLocalState = await ReadLocalStateUnlockedAsync(folderPath, token);
                    EnsureExpectedMeta(observedLocalState.Meta, currentLocalState.Meta);
                    var snapshot = await CommitPreparedResolutionUnlockedAsync(
                        instance,
                        folderPath,
                        currentLocalState.Meta,
                        resolution,
                        preparedDownloads,
                        token);
                    StoreSnapshot(instance.Id, snapshot, raiseEvent: false);
                    return snapshot;
                },
                cancellationToken);
        }
        finally
        {
            preparedDownloads.Dispose();
        }
    }

    private async Task<ResolutionState> ResolveDesiredDirectModsAsync(
        MinecraftInstance instance,
        Dictionary<string, string?> desiredDirectMods,
        CancellationToken cancellationToken)
    {
        var desiredDirectModEntries = desiredDirectMods
            .OrderBy(desiredDirectMod => desiredDirectMod.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var resolvedDirectMods = new ResolutionState?[desiredDirectModEntries.Length];
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxDirectModResolutionConcurrency,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, desiredDirectModEntries.Length),
            parallelOptions,
            async (index, token) =>
            {
                var desiredDirectMod = desiredDirectModEntries[index];
                var directModResolution = new ResolutionState();
                await ResolveProjectAsync(
                    instance,
                    desiredDirectMod.Key,
                    isDirectInstall: true,
                    requiredByProjectId: null,
                    requestedVersionId: desiredDirectMod.Value,
                    directModResolution,
                    token);
                resolvedDirectMods[index] = directModResolution;
            });

        var resolution = new ResolutionState();
        foreach (var resolvedDirectMod in resolvedDirectMods)
        {
            if (resolvedDirectMod is not null)
            {
                MergeResolutionState(resolution, resolvedDirectMod);
            }
        }

        return resolution;
    }

    private async Task<PreparedDownloads> PrepareDownloadsAsync(
        string folderPath,
        ResolutionState resolution,
        CancellationToken cancellationToken)
    {
        var preparedDownloads = new PreparedDownloads(folderPath);
        var modsFolder = Path.Combine(folderPath, ModsFolderName);
        Directory.CreateDirectory(modsFolder);

        foreach (var resolved in resolution.Projects.Values)
        {
            var destinationPath = Path.Combine(modsFolder, resolved.File.Filename);
            if (await IsCurrentFileValidAsync(destinationPath, resolved.File, cancellationToken))
            {
                continue;
            }

            var tempFilePath = Path.Combine(preparedDownloads.TempRoot, resolved.File.Filename);
            await _fileDownloader.DownloadFileAsync(
                resolved.File.Url,
                tempFilePath,
                resolved.File.Hashes.Sha512 ?? resolved.File.Hashes.Sha1,
                cancellationToken: cancellationToken);
            preparedDownloads.DownloadedFiles[resolved.Project.Id] = tempFilePath;
        }

        return preparedDownloads;
    }

    private async Task<InstanceModsSnapshot> CommitPreparedResolutionUnlockedAsync(
        MinecraftInstance instance,
        string folderPath,
        InstanceMeta currentMeta,
        ResolutionState resolution,
        PreparedDownloads preparedDownloads,
        CancellationToken cancellationToken)
    {
        var modsFolder = Path.Combine(folderPath, ModsFolderName);
        Directory.CreateDirectory(modsFolder);

        var backups = new List<(string BackupPath, string DestinationPath)>();
        var movedNewFiles = new List<string>();
        var obsoleteFiles = CollectObsoleteManagedFiles(currentMeta, resolution);

        try
        {
            foreach (var resolved in resolution.Projects.Values)
            {
                var destinationPath = Path.Combine(modsFolder, resolved.File.Filename);
                if (!preparedDownloads.DownloadedFiles.TryGetValue(resolved.Project.Id, out var tempFilePath))
                {
                    if (await IsCurrentFileValidAsync(destinationPath, resolved.File, cancellationToken))
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        "Instance mod files changed during the operation. Please try again.");
                }

                if (File.Exists(destinationPath))
                {
                    var backupPath = Path.Combine(preparedDownloads.TempRoot, Guid.NewGuid().ToString("N") + ".bak");
                    File.Move(destinationPath, backupPath, true);
                    backups.Add((backupPath, destinationPath));
                }

                File.Move(tempFilePath, destinationPath, true);
                movedNewFiles.Add(destinationPath);
            }

            var meta = BuildMetaFromResolution(instance, resolution);
            await WriteMetaUnlockedAsync(folderPath, meta, cancellationToken);

            foreach (var obsoleteFile in obsoleteFiles)
            {
                var obsoletePath = Path.Combine(modsFolder, obsoleteFile);
                if (File.Exists(obsoletePath))
                {
                    File.Delete(obsoletePath);
                }
            }

            foreach (var backup in backups)
            {
                if (File.Exists(backup.BackupPath))
                {
                    File.Delete(backup.BackupPath);
                }
            }

            var localState = await ReadLocalStateUnlockedAsync(folderPath, cancellationToken);
            return BuildSnapshot(localState.Meta, localState.ScannedFiles);
        }
        catch
        {
            foreach (var movedFile in movedNewFiles.Where(File.Exists))
            {
                File.Delete(movedFile);
            }

            foreach (var backup in backups.Where(b => File.Exists(b.BackupPath)))
            {
                if (File.Exists(backup.DestinationPath))
                {
                    File.Delete(backup.DestinationPath);
                }

                File.Move(backup.BackupPath, backup.DestinationPath, true);
            }

            throw;
        }
    }

    private static void EnsureExpectedMeta(InstanceMeta expectedMeta, InstanceMeta currentMeta)
    {
        if (!AreEquivalent(expectedMeta, currentMeta))
        {
            throw new InvalidOperationException(
                "Instance mod state changed during the operation. Please try again.");
        }
    }

    // Record equality can't be used because arrays (Mods, RequiredByProjectIds) use reference equality.
    // JSON comparison is simple, automatically covers new fields, and is fine here since this is not a hot path.
    private static bool AreEquivalent(InstanceMeta left, InstanceMeta right) =>
        string.Equals(
            JsonSerializer.Serialize(left, InstanceMetaJsonContext.Default.InstanceMeta),
            JsonSerializer.Serialize(right, InstanceMetaJsonContext.Default.InstanceMeta),
            StringComparison.Ordinal);

    private static InstanceModsSnapshot BuildSnapshot(
        InstanceMeta meta,
        ImmutableHashSet<string> scannedFiles)
    {
        var managedByFileName = meta.Mods.ToDictionary(m => m.InstalledFileName, StringComparer.OrdinalIgnoreCase);
        var titleLookup = meta.Mods
            .ToDictionary(m => m.ProjectId, m => m.ProjectTitle, StringComparer.OrdinalIgnoreCase);
        var projectStates = ImmutableDictionary.CreateBuilder<string, InstanceInstalledProjectState>(
            StringComparer.OrdinalIgnoreCase);
        var installed = new List<InstanceModListItem>();
        var dependencies = new List<InstanceModListItem>();
        var manual = new List<InstanceModListItem>();
        var broken = new List<InstanceModListItem>();

        foreach (var managed in meta.Mods)
        {
            var installKind = managed.InstallKind.Equals(InstallKindDependency, StringComparison.OrdinalIgnoreCase)
                ? InstanceModItemKind.Dependency
                : InstanceModItemKind.Direct;
            var exists = scannedFiles.Contains(managed.InstalledFileName);
            var displayKind = exists ? installKind : InstanceModItemKind.Broken;
            var parentTitles = managed.RequiredByProjectIds
                .Select(id => titleLookup.GetValueOrDefault(id, id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var item = new InstanceModListItem(
                Key: managed.ProjectId,
                DisplayName: managed.ProjectTitle,
                SecondaryText: $"{managed.InstalledVersionNumber} ({managed.InstalledVersionType})",
                FileName: managed.InstalledFileName,
                ProjectId: managed.ProjectId,
                Kind: displayKind,
                RequiredByDisplay: parentTitles.Length == 0 ? null : string.Join(", ", parentTitles),
                CanDelete: installKind == InstanceModItemKind.Direct,
                HasUpdate: false,
                LatestVersionNumber: null);

            switch (displayKind)
            {
                case InstanceModItemKind.Direct:
                    installed.Add(item);
                    break;
                case InstanceModItemKind.Dependency:
                    dependencies.Add(item);
                    break;
                case InstanceModItemKind.Broken:
                    broken.Add(item);
                    break;
            }

            projectStates[managed.ProjectId] = new InstanceInstalledProjectState(
                managed.ProjectId,
                managed.ProjectTitle,
                managed.InstalledVersionId,
                managed.InstalledVersionNumber,
                installKind,
                !exists);
        }

        foreach (var scannedFile in scannedFiles)
        {
            if (managedByFileName.ContainsKey(scannedFile))
            {
                continue;
            }

            manual.Add(new InstanceModListItem(
                Key: scannedFile,
                DisplayName: Path.GetFileNameWithoutExtension(scannedFile),
                SecondaryText: "Manual mod",
                FileName: scannedFile,
                ProjectId: null,
                Kind: InstanceModItemKind.Manual,
                RequiredByDisplay: null,
                CanDelete: true,
                HasUpdate: false,
                LatestVersionNumber: null));
        }

        return new InstanceModsSnapshot(
            installed.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToImmutableList(),
            dependencies.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToImmutableList(),
            manual.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToImmutableList(),
            broken.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToImmutableList(),
            projectStates.ToImmutable());
    }

    private async Task ResolveProjectAsync(
        MinecraftInstance instance,
        string projectId,
        bool isDirectInstall,
        string? requiredByProjectId,
        string? requestedVersionId,
        ResolutionState resolution,
        CancellationToken cancellationToken)
    {
        ModrinthVersion selectedVersion;
        if (!string.IsNullOrWhiteSpace(requestedVersionId))
        {
            selectedVersion = await _modrinthApiClient.GetVersionAsync(requestedVersionId, cancellationToken)
                              ?? throw new InvalidOperationException(
                                  $"Missing Modrinth version '{requestedVersionId}'");

            if (!selectedVersion.GameVersions.Contains(instance.VersionId, StringComparer.OrdinalIgnoreCase)
                || !selectedVersion.Loaders.Contains(GetModrinthLoader(instance.ModLoader) ?? "",
                    StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Version '{selectedVersion.VersionNumber}' is not compatible with {instance.VersionId}/{instance.ModLoader}.");
            }
        }
        else
        {
            selectedVersion = await TryResolveLatestCompatibleVersionAsync(instance, projectId, cancellationToken)
                              ?? throw new InvalidOperationException(
                                  $"No compatible Modrinth version found for project '{projectId}' and {instance.VersionId}/{instance.ModLoader}.");
        }

        if (resolution.Projects.TryGetValue(projectId, out var existing))
        {
            if (!string.Equals(existing.Version.Id, selectedVersion.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Conflicting required versions detected for project '{projectId}'.");
            }

            if (isDirectInstall)
            {
                existing.InstallKind = InstallKindDirect;
            }

            if (!string.IsNullOrWhiteSpace(requiredByProjectId))
            {
                existing.RequiredByProjectIds.Add(requiredByProjectId);
            }

            return;
        }

        var project = await _modrinthApiClient.GetProjectAsync(projectId, cancellationToken)
                      ?? throw new InvalidOperationException($"Missing Modrinth project '{projectId}'");
        var resolved = new ResolvedProjectInstall(
            project,
            selectedVersion,
            SelectInstallFile(selectedVersion),
            isDirectInstall ? InstallKindDirect : InstallKindDependency);
        if (!string.IsNullOrWhiteSpace(requiredByProjectId))
        {
            resolved.RequiredByProjectIds.Add(requiredByProjectId);
        }

        resolution.Projects[projectId] = resolved;

        foreach (var dependency in selectedVersion.Dependencies)
        {
            if (!dependency.DependencyType.Equals("required", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(dependency.ProjectId))
            {
                continue;
            }

            await ResolveProjectAsync(
                instance,
                dependency.ProjectId,
                isDirectInstall: false,
                requiredByProjectId: projectId,
                requestedVersionId: dependency.VersionId,
                resolution,
                cancellationToken);
        }
    }

    private static HashSet<string> CollectObsoleteManagedFiles(InstanceMeta currentMeta, ResolutionState resolution)
    {
        var plannedFiles = resolution.Projects.Values
            .Select(p => p.File.Filename)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        return currentMeta.Mods
            .Select(m => m.InstalledFileName)
            .Where(file => !plannedFiles.Contains(file))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void MergeResolutionState(ResolutionState target, ResolutionState source)
    {
        foreach (var (projectId, resolvedProject) in source.Projects)
        {
            if (!target.Projects.TryGetValue(projectId, out var existing))
            {
                target.Projects[projectId] = resolvedProject;
                continue;
            }

            if (!string.Equals(existing.Version.Id, resolvedProject.Version.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Conflicting required versions detected for project '{projectId}'.");
            }

            if (resolvedProject.InstallKind.Equals(InstallKindDirect, StringComparison.OrdinalIgnoreCase))
            {
                existing.InstallKind = InstallKindDirect;
            }

            existing.RequiredByProjectIds.UnionWith(resolvedProject.RequiredByProjectIds);
        }
    }

    private async Task<LatestCompatibleVersionInfo?> GetLatestCompatibleVersionAsync(
        MinecraftInstance instance,
        string projectId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var cacheKey = GetLatestCompatibleVersionCacheKey(instance, projectId);
        if (!forceRefresh && TryGetLatestCompatibleVersionFromCache(cacheKey, out var cachedVersion))
        {
            return cachedVersion;
        }

        try
        {
            var latestVersion = await TryResolveLatestCompatibleVersionAsync(instance, projectId, cancellationToken);
            var latestCompatibleVersion = latestVersion is null
                ? null
                : new LatestCompatibleVersionInfo(projectId, latestVersion.Id, latestVersion.VersionNumber);
            StoreLatestCompatibleVersionInCache(cacheKey, latestCompatibleVersion);
            return latestCompatibleVersion;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resolve latest compatible version for {ProjectId}", projectId);
            return null;
        }
    }

    private static InstanceMeta BuildMetaFromResolution(MinecraftInstance instance, ResolutionState resolution) => new(
        CurrentSchemaVersion,
        instance.Id,
        instance.VersionId,
        MinecraftInstance.ModLoaderToString(instance.ModLoader),
        instance.ModLoaderVersion,
        instance.LaunchVersionId,
        resolution.Projects.Values
            .OrderBy(project => project.Project.Title, StringComparer.OrdinalIgnoreCase)
            .Select(project => new InstanceMetaMod(
                project.Project.Id,
                project.Project.Slug,
                project.Project.Title,
                project.Version.Id,
                project.Version.VersionNumber,
                project.Version.VersionType,
                project.File.Filename,
                project.File.Hashes.Sha512,
                project.InstallKind,
                project.RequiredByProjectIds
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                DateTime.UtcNow))
            .ToArray());

    private static bool IsPortableMetaValid(InstanceMeta meta) =>
        meta.SchemaVersion > 0
        && !string.IsNullOrWhiteSpace(meta.DisplayName)
        && !string.IsNullOrWhiteSpace(meta.MinecraftVersionId)
        && !string.IsNullOrWhiteSpace(meta.ModLoader)
        && !string.IsNullOrWhiteSpace(meta.LaunchVersionId);

    public void EvictInstanceCaches(MinecraftInstance instance)
    {
        InvalidateSnapshot(instance.Id);

        var folderPath = GetInstanceFolder(instance.Folder);
        _instanceStateLocks.TryRemove(folderPath, out _);

        lock (_latestCompatibleVersionCacheLock)
        {
            var prefix = $"{instance.Id}|";
            var keysToRemove = _latestCompatibleVersionCache.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in keysToRemove)
            {
                _latestCompatibleVersionCache.Remove(key);
            }
        }
    }

    private string GetInstanceFolder(string folderName) => Path.Combine(_instancesRoot, folderName);

    private static int GetVersionTypeRank(string versionType) => versionType.ToLowerInvariant() switch
    {
        "release" => 0,
        "beta" => 1,
        "alpha" => 2,
        _ => 3,
    };

    private static DateTime ParseDate(string raw) =>
        DateTime.TryParse(raw, out var parsed) ? parsed : DateTime.MinValue;

    private static string? GetModrinthLoader(MinecraftInstanceModLoader modLoader) => modLoader switch
    {
        MinecraftInstanceModLoader.Fabric => "fabric",
        MinecraftInstanceModLoader.Forge => "forge",
        MinecraftInstanceModLoader.NeoForge => "neoforge",
        _ => null,
    };

    private static InstanceMeta CreateEmptyMeta(MinecraftInstance instance) =>
        new(
            CurrentSchemaVersion,
            instance.Id,
            instance.VersionId,
            MinecraftInstance.ModLoaderToString(instance.ModLoader),
            instance.ModLoaderVersion,
            instance.LaunchVersionId,
            []);

    private async Task<LocalInstanceState> ReadLocalStateAsync(
        MinecraftInstance instance,
        string folderPath,
        CancellationToken cancellationToken)
    {
        var localState = await ExecuteWithInstanceReadLockAsync(
            folderPath,
            token => TryReadLocalStateUnlockedAsync(folderPath, token),
            cancellationToken);
        if (localState is not null)
        {
            return localState;
        }

        await EnsureInstanceMetadataAsync(instance, cancellationToken);
        return await ExecuteWithInstanceReadLockAsync(
            folderPath,
            token => ReadLocalStateUnlockedAsync(folderPath, token),
            cancellationToken);
    }

    private async Task<LocalInstanceState> ReadLocalStateUnlockedAsync(
        string folderPath,
        CancellationToken cancellationToken) =>
        await TryReadLocalStateUnlockedAsync(folderPath, cancellationToken)
        ?? throw new InvalidOperationException($"Missing metadata file in '{folderPath}'.");

    private async Task<LocalInstanceState?> TryReadLocalStateUnlockedAsync(
        string folderPath,
        CancellationToken cancellationToken)
    {
        var meta = await TryReadMetaUnlockedAsync(GetMetaPath(folderPath), cancellationToken);
        if (meta is null)
        {
            return null;
        }

        var modsFolder = Path.Combine(folderPath, ModsFolderName);
        Directory.CreateDirectory(modsFolder);
        var scannedFiles = Directory.GetFiles(modsFolder, "*.jar", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        return new LocalInstanceState(meta, scannedFiles);
    }

    private async Task<InstanceMeta>
        ReadMetaUnlockedAsync(string instanceFolder, CancellationToken cancellationToken) =>
        await TryReadMetaUnlockedAsync(GetMetaPath(instanceFolder), cancellationToken)
        ?? throw new InvalidOperationException($"Missing metadata file in '{instanceFolder}'.");

    private async Task<InstanceMeta?> TryReadMetaUnlockedAsync(string metaPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            return await File.DeserializeJsonAsync(metaPath, InstanceMetaJsonContext.Default.InstanceMeta,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read instance metadata from {MetaPath}", metaPath);
            return null;
        }
    }

    private static string GetMetaPath(string instanceFolder) => Path.Combine(instanceFolder, MetaFileName);

    private static string GetLatestCompatibleVersionCacheKey(MinecraftInstance instance, string projectId) =>
        $"{instance.VersionId}|{instance.ModLoader}|{projectId}";

    private AsyncRwLock GetInstanceStateLock(string instanceFolder) =>
        _instanceStateLocks.GetOrAdd(instanceFolder, _ => new AsyncRwLock(MaxConcurrentInstanceStateReaders));

    private Task ExecuteWithInstanceReadLockAsync(
        string instanceFolder,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken) =>
        ExecuteWithInstanceReadLockAsync(
            instanceFolder,
            async token =>
            {
                await action(token);
                return true;
            },
            cancellationToken);

    private Task<T> ExecuteWithInstanceReadLockAsync<T>(
        string instanceFolder,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken) =>
        GetInstanceStateLock(instanceFolder).ExecuteReadAsync(() => action(cancellationToken), cancellationToken);

    private Task ExecuteWithInstanceWriteLockAsync(
        string instanceFolder,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken) =>
        ExecuteWithInstanceWriteLockAsync(
            instanceFolder,
            async token =>
            {
                await action(token);
                return true;
            },
            cancellationToken);

    private Task<T> ExecuteWithInstanceWriteLockAsync<T>(
        string instanceFolder,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken) =>
        GetInstanceStateLock(instanceFolder).ExecuteWriteAsync(() => action(cancellationToken), cancellationToken);

    private static async Task WriteMetaUnlockedAsync(
        string instanceFolder,
        InstanceMeta meta,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(instanceFolder);
        var tempPath = Path.Combine(instanceFolder, $"{MetaFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, meta, InstanceMetaJsonContext.Default.InstanceMeta,
                    cancellationToken);
            }

            File.Move(tempPath, GetMetaPath(instanceFolder), true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to write instance metadata.", ex);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Cleanup is best-effort; keep the original write outcome.
            }
        }
    }

    private bool TryGetSnapshot(string instanceId, out InstanceModsSnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            return _snapshots.TryGetValue(instanceId, out snapshot!);
        }
    }

    private void InvalidateSnapshot(string instanceId)
    {
        lock (_snapshotLock)
        {
            _snapshots.Remove(instanceId);
        }
    }

    private void StoreSnapshot(string instanceId, InstanceModsSnapshot snapshot, bool raiseEvent)
    {
        lock (_snapshotLock)
        {
            _snapshots[instanceId] = snapshot;
        }

        if (raiseEvent)
        {
            InstanceModsChanged?.Invoke(this, new InstanceModsSnapshotChangedEventArgs(instanceId, snapshot));
        }
    }

    private void PublishSnapshot(string instanceId, InstanceModsSnapshot snapshot) =>
        StoreSnapshot(instanceId, snapshot, raiseEvent: true);

    private bool TryGetLatestCompatibleVersionFromCache(
        string cacheKey,
        out LatestCompatibleVersionInfo? latestCompatibleVersion)
    {
        lock (_latestCompatibleVersionCacheLock)
        {
            return _latestCompatibleVersionCache.TryGetValue(cacheKey, out latestCompatibleVersion);
        }
    }

    private void StoreLatestCompatibleVersionInCache(
        string cacheKey,
        LatestCompatibleVersionInfo? latestCompatibleVersion)
    {
        lock (_latestCompatibleVersionCacheLock)
        {
            _latestCompatibleVersionCache[cacheKey] = latestCompatibleVersion;
        }
    }

    private static async Task<bool> IsCurrentFileValidAsync(
        string destinationPath,
        ModrinthVersionFile file,
        CancellationToken cancellationToken)
    {
        var expectedHash = file.Hashes.Sha512 ?? file.Hashes.Sha1;
        return !string.IsNullOrWhiteSpace(expectedHash)
               && await FileDownloader.VerifyFileHashAsync(destinationPath, expectedHash, cancellationToken);
    }

    private sealed class ResolutionState
    {
        public Dictionary<string, ResolvedProjectInstall> Projects { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record LocalInstanceState(
        InstanceMeta Meta,
        ImmutableHashSet<string> ScannedFiles
    );

    private sealed class PreparedDownloads : IDisposable
    {
        public PreparedDownloads(string instanceFolder)
        {
            TempRoot = Path.Combine(instanceFolder, ".mod-tmp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempRoot);
        }

        public string TempRoot { get; }
        public Dictionary<string, string> DownloadedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(TempRoot))
                {
                    Directory.Delete(TempRoot, true);
                }
            }
            catch
            {
                // Cleanup is best-effort; keep the original operation outcome.
            }
        }
    }

    private enum ModChangeKind
    {
        Install,
        Update,
        Remove,
        UpdateAll,
    }

    private enum DeleteTarget
    {
        Missing,
        Manual,
        ManagedDirect,
        Dependency,
    }

    private sealed class ResolvedProjectInstall(
        ModrinthProject project,
        ModrinthVersion version,
        ModrinthVersionFile file,
        string installKind)
    {
        public ModrinthProject Project { get; } = project;
        public ModrinthVersion Version { get; } = version;
        public ModrinthVersionFile File { get; } = file;
        public string InstallKind { get; set; } = installKind;
        public HashSet<string> RequiredByProjectIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record PortableInstanceCandidate(
    string FolderName,
    string FolderPath,
    InstanceMeta Meta
);

public sealed record CompatibleInstanceInstallTarget(
    MinecraftInstance Instance,
    string CompatibleVersionNumber
);

public enum InstanceModItemKind
{
    Direct,
    Dependency,
    Manual,
    Broken,
}

public sealed record InstanceModListItem(
    string Key,
    string DisplayName,
    string SecondaryText,
    string FileName,
    string? ProjectId,
    InstanceModItemKind Kind,
    string? RequiredByDisplay,
    bool CanDelete,
    bool HasUpdate,
    string? LatestVersionNumber
)
{
    public bool CanUpdate => Kind == InstanceModItemKind.Direct;
}
