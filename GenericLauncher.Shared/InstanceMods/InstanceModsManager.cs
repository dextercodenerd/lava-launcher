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
    private readonly ConcurrentDictionary<string, AsyncRwLock> _instanceStateLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, InstanceModsSnapshot> _snapshots =
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
        await ExecuteWithInstanceWriteLockAsync(
            folderPath,
            async token =>
            {
                Directory.CreateDirectory(folderPath);

                var existing = await TryReadMetaUnlockedAsync(GetMetaPath(folderPath), token);
                var meta = existing is null
                    ? CreateEmptyMeta(instance)
                    : existing with
                    {
                        DisplayName = instance.Id,
                        MinecraftVersionId = instance.VersionId,
                        ModLoader = MinecraftInstance.ModLoaderToString(instance.ModLoader),
                        ModLoaderVersion = instance.ModLoaderVersion,
                        LaunchVersionId = instance.LaunchVersionId,
                    };

                await WriteMetaUnlockedAsync(folderPath, meta, token);
            },
            cancellationToken);
        InvalidateSnapshot(instance.Id);
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

        await EnsureInstanceMetadataAsync(instance, cancellationToken);
        var snapshot = await BuildSnapshotAsync(instance, cancellationToken);
        StoreSnapshot(instance.Id, snapshot, false);
        return snapshot;
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
        var folderPath = GetInstanceFolder(instance.Folder);
        await ExecuteWithInstanceWriteLockAsync(
            folderPath,
            async token =>
            {
                await EnsureInstanceMetadataUnlockedAsync(instance, folderPath, token);
                await ApplyDesiredDirectModsUnlockedAsync(
                    instance,
                    folderPath,
                    await BuildDesiredDirectModsUnlockedAsync(instance, projectId, updateProject: false,
                        removeProject: false, updateAll: false, token),
                    token);
            },
            cancellationToken);
        await RefreshSnapshotAsync(instance, cancellationToken);
    }

    public async Task UpdateModAsync(
        MinecraftInstance instance,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var folderPath = GetInstanceFolder(instance.Folder);
        await ExecuteWithInstanceWriteLockAsync(
            folderPath,
            async token =>
            {
                await EnsureInstanceMetadataUnlockedAsync(instance, folderPath, token);
                await ApplyDesiredDirectModsUnlockedAsync(
                    instance,
                    folderPath,
                    await BuildDesiredDirectModsUnlockedAsync(instance, projectId, updateProject: true,
                        removeProject: false, updateAll: false, token),
                    token);
            },
            cancellationToken);
        await RefreshSnapshotAsync(instance, cancellationToken);
    }

    public async Task UpdateAllAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default)
    {
        var folderPath = GetInstanceFolder(instance.Folder);
        await ExecuteWithInstanceWriteLockAsync(
            folderPath,
            async token =>
            {
                await EnsureInstanceMetadataUnlockedAsync(instance, folderPath, token);
                await ApplyDesiredDirectModsUnlockedAsync(
                    instance,
                    folderPath,
                    await BuildDesiredDirectModsUnlockedAsync(instance, null, updateProject: false,
                        removeProject: false, updateAll: true, token),
                    token);
            },
            cancellationToken);
        await RefreshSnapshotAsync(instance, cancellationToken);
    }

    public async Task DeleteModAsync(
        MinecraftInstance instance,
        string modKey,
        CancellationToken cancellationToken = default)
    {
        var folderPath = GetInstanceFolder(instance.Folder);
        var modsFolder = Path.Combine(folderPath, ModsFolderName);
        var changed = await ExecuteWithInstanceWriteLockAsync(
            folderPath,
            async token =>
            {
                Directory.CreateDirectory(modsFolder);

                var meta = await ReadMetaUnlockedAsync(folderPath, token);
                var managed = meta.Mods.FirstOrDefault(m =>
                    string.Equals(m.ProjectId, modKey, StringComparison.Ordinal));
                if (managed is not null)
                {
                    if (managed.InstallKind.Equals(InstallKindDependency, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Required dependencies cannot be deleted manually in v1.");
                    }

                    var soleDependencies = meta.Mods
                        .Where(m => m.InstallKind.Equals(InstallKindDependency, StringComparison.OrdinalIgnoreCase))
                        .Where(m => m.RequiredByProjectIds.Length == 1
                                    && string.Equals(m.RequiredByProjectIds[0], managed.ProjectId,
                                        StringComparison.Ordinal))
                        .Select(m => m.ProjectTitle)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (soleDependencies.Length > 0)
                    {
                        throw new InvalidOperationException(
                            $"Delete blocked. '{managed.ProjectTitle}' is the only parent of: {string.Join(", ", soleDependencies)}.");
                    }

                    await ApplyDesiredDirectModsUnlockedAsync(
                        instance,
                        folderPath,
                        await BuildDesiredDirectModsUnlockedAsync(instance, managed.ProjectId, updateProject: false,
                            removeProject: true, updateAll: false, token),
                        token);
                    return true;
                }

                var manualPath = Path.Combine(modsFolder, modKey);
                if (!File.Exists(manualPath))
                {
                    return false;
                }

                File.Delete(manualPath);
                return true;
            },
            cancellationToken);
        if (changed)
        {
            await RefreshSnapshotAsync(instance, cancellationToken);
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
                var version = await TryResolveLatestCompatibleVersionAsync(instance, projectId, token);
                if (version is null)
                {
                    return;
                }

                targets[index] = new CompatibleInstanceInstallTarget(instance, version.VersionNumber);
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

    private async Task<Dictionary<string, string?>> BuildDesiredDirectModsUnlockedAsync(
        MinecraftInstance instance,
        string? projectId,
        bool updateProject,
        bool removeProject,
        bool updateAll,
        CancellationToken cancellationToken)
    {
        var meta = await ReadMetaUnlockedAsync(GetInstanceFolder(instance.Folder), cancellationToken);
        var directMods = meta.Mods
            .Where(m => m.InstallKind.Equals(InstallKindDirect, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(m => m.ProjectId, m => (string?)m.InstalledVersionId, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            if (removeProject)
            {
                directMods.Remove(projectId);
            }
            else if (updateProject || !directMods.ContainsKey(projectId))
            {
                directMods[projectId] = null;
            }
        }

        if (updateAll)
        {
            foreach (var key in directMods.Keys.ToArray())
            {
                directMods[key] = null;
            }
        }

        return directMods;
    }

    private async Task ApplyDesiredDirectModsUnlockedAsync(
        MinecraftInstance instance,
        string folderPath,
        Dictionary<string, string?> desiredDirectMods,
        CancellationToken cancellationToken)
    {
        var modsFolder = Path.Combine(folderPath, ModsFolderName);
        Directory.CreateDirectory(modsFolder);

        var currentMeta = await ReadMetaUnlockedAsync(folderPath, cancellationToken);
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
            if (resolvedDirectMod is null)
            {
                continue;
            }

            MergeResolutionState(resolution, resolvedDirectMod);
        }

        var tempRoot = Path.Combine(folderPath, ".mod-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var backups = new List<(string BackupPath, string DestinationPath)>();
        var movedNewFiles = new List<string>();
        var downloadedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var obsoleteFiles = CollectObsoleteManagedFiles(currentMeta, resolution);

        try
        {
            foreach (var resolved in resolution.Projects.Values)
            {
                var destinationPath = Path.Combine(modsFolder, resolved.File.Filename);
                if (await IsCurrentFileValidAsync(destinationPath, resolved.File, cancellationToken))
                {
                    continue;
                }

                var tempFilePath = Path.Combine(tempRoot, resolved.File.Filename);
                await _fileDownloader.DownloadFileAsync(
                    resolved.File.Url,
                    tempFilePath,
                    resolved.File.Hashes.Sha512 ?? resolved.File.Hashes.Sha1,
                    cancellationToken: cancellationToken);
                downloadedFiles[resolved.Project.Id] = tempFilePath;
            }

            foreach (var resolved in resolution.Projects.Values)
            {
                if (!downloadedFiles.TryGetValue(resolved.Project.Id, out var tempFilePath))
                {
                    continue;
                }

                var destinationPath = Path.Combine(modsFolder, resolved.File.Filename);
                if (File.Exists(destinationPath))
                {
                    var backupPath = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + ".bak");
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
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }

    private async Task<InstanceModsSnapshot> BuildSnapshotAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken)
    {
        var folderPath = GetInstanceFolder(instance.Folder);
        var snapshotSource = await ExecuteWithInstanceReadLockAsync(
            folderPath,
            async token =>
            {
                var modsFolder = Path.Combine(folderPath, ModsFolderName);
                Directory.CreateDirectory(modsFolder);

                var meta = await ReadMetaUnlockedAsync(folderPath, token);
                var scannedFiles = Directory.GetFiles(modsFolder, "*.jar", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
                return (Meta: meta, ScannedFiles: scannedFiles);
            },
            cancellationToken);
        var meta = snapshotSource.Meta;
        var scannedFiles = snapshotSource.ScannedFiles;
        var managedByFileName = meta.Mods.ToDictionary(m => m.InstalledFileName, StringComparer.OrdinalIgnoreCase);
        var plannedUpdates = await ResolveUpdatesSafelyAsync(instance, meta, cancellationToken);
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
            plannedUpdates.TryGetValue(managed.ProjectId, out var latestVersion);
            var hasUpdate = installKind == InstanceModItemKind.Direct
                            && latestVersion is not null
                            && !string.Equals(latestVersion.Id, managed.InstalledVersionId, StringComparison.Ordinal);

            var item = new InstanceModListItem(
                Key: managed.ProjectId,
                DisplayName: managed.ProjectTitle,
                SecondaryText: $"{managed.InstalledVersionNumber} ({managed.InstalledVersionType})",
                FileName: managed.InstalledFileName,
                ProjectId: managed.ProjectId,
                Kind: displayKind,
                RequiredByDisplay: parentTitles.Length == 0 ? null : string.Join(", ", parentTitles),
                CanUpdate: exists && installKind == InstanceModItemKind.Direct,
                CanDelete: installKind == InstanceModItemKind.Direct,
                HasUpdate: exists && hasUpdate,
                LatestVersionNumber: latestVersion?.VersionNumber);

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
                IsBroken: !exists,
                HasUpdate: hasUpdate,
                LatestVersionNumber: latestVersion?.VersionNumber);
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
                CanUpdate: false,
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

    private async Task<Dictionary<string, ModrinthVersion?>> ResolveUpdatesAsync(
        MinecraftInstance instance,
        InstanceMeta meta,
        CancellationToken cancellationToken)
    {
        var directMods = meta.Mods
            .Where(m => m.InstallKind.Equals(InstallKindDirect, StringComparison.OrdinalIgnoreCase));

        var tasks = directMods.Select(async mod =>
        (
            ProjectId: mod.ProjectId,
            Version: await TryResolveLatestCompatibleVersionAsync(instance, mod.ProjectId, cancellationToken)
        ));

        return (await Task.WhenAll(tasks))
            .ToDictionary(item => item.ProjectId, item => item.Version, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, ModrinthVersion?>> ResolveUpdatesSafelyAsync(
        MinecraftInstance instance,
        InstanceMeta meta,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveUpdatesAsync(instance, meta, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resolve available updates for instance {InstanceId}", instance.Id);
            return new Dictionary<string, ModrinthVersion?>(StringComparer.OrdinalIgnoreCase);
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

    private async Task EnsureInstanceMetadataUnlockedAsync(
        MinecraftInstance instance,
        string folderPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folderPath);

        var existing = await TryReadMetaUnlockedAsync(GetMetaPath(folderPath), cancellationToken);
        var meta = existing is null
            ? CreateEmptyMeta(instance)
            : existing with
            {
                DisplayName = instance.Id,
                MinecraftVersionId = instance.VersionId,
                ModLoader = MinecraftInstance.ModLoaderToString(instance.ModLoader),
                ModLoaderVersion = instance.ModLoaderVersion,
                LaunchVersionId = instance.LaunchVersionId,
            };

        await WriteMetaUnlockedAsync(folderPath, meta, cancellationToken);
    }

    private async Task<InstanceMeta> ReadMetaUnlockedAsync(string instanceFolder, CancellationToken cancellationToken) =>
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

    private async Task RefreshSnapshotAsync(MinecraftInstance instance, CancellationToken cancellationToken)
    {
        var snapshot = await BuildSnapshotAsync(instance, cancellationToken);
        StoreSnapshot(instance.Id, snapshot, raiseEvent: true);
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
    bool CanUpdate,
    bool CanDelete,
    bool HasUpdate,
    string? LatestVersionNumber
);
