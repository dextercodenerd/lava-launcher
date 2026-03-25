using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CliWrap;
using GenericLauncher.Database;
using GenericLauncher.Database.Model;
using GenericLauncher.InstanceMods;
using GenericLauncher.Java;
using GenericLauncher.Minecraft.Json;
using GenericLauncher.Minecraft.ModLoaders;
using GenericLauncher.Misc;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Minecraft;

public sealed class MinecraftLauncher : IMinecraftLauncherFacade, IDisposable
{
    public enum RunningState
    {
        Authenticating,
        Launching,
        RendererReady,
        SplashScreen,
        Running,
        Stopped,
    }

    private readonly LauncherPlatform _platform;
    private readonly string _launcherName;
    private readonly string _launcherVersion;
    private readonly string _instancesFolder;
    private readonly string _installationId;
    private readonly LauncherRepository _repository;
    private readonly JavaVersionManager _javaManager;
    private readonly MinecraftVersionManager _minecraftManager;
    private readonly InstanceModsManager _instanceModsManager;
    private readonly IReadOnlyDictionary<MinecraftInstanceModLoader, IModLoaderService> _modLoaderServices;
    private readonly ILogger? _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public ImmutableList<VersionInfo> AvailableVersions
    {
        get;
        private set;
    } = [];

    public ImmutableList<MinecraftInstance> Instances
    {
        get;
        private set;
    } = [];

    public ImmutableList<MinecraftInstanceModLoader> AvailableModLoaders =>
        _modLoaderServices.Keys
            .Where(v => v != MinecraftInstanceModLoader.Unknown)
            .OrderBy(v => v)
            .ToImmutableList();

    public readonly ConcurrentDictionary<string, ThreadSafeInstallProgressReporter.InstallProgress>
        CurrentInstallProgress = [];

    public readonly ConcurrentDictionary<string, RunningState> LaunchedInstances = [];

    public event EventHandler? AvailableVersionsChanged;
    public event EventHandler? InstancesChanged;
    public event EventHandler<(string InstanceId, RunningState State)>? InstanceStateChanged;
    public event EventHandler<ThreadSafeInstallProgressReporter.InstallProgress>? InstallProgressUpdated;

    public MinecraftLauncher(
        LauncherPlatform platform,
        string launcherName,
        string launcherVersion,
        LauncherRepository repository,
        MinecraftVersionManager minecraftVersionManager,
        JavaVersionManager javaVersionManager,
        InstanceModsManager instanceModsManager,
        IReadOnlyDictionary<MinecraftInstanceModLoader, IModLoaderService> modLoaderServices,
        ILogger? logger = null)
    {
        _platform = platform;
        _launcherName = launcherName;
        _launcherVersion = launcherVersion;
        _instancesFolder = Path.Combine(platform.AppDataPath, "instances");
        _repository = repository;
        _javaManager = javaVersionManager;
        _minecraftManager = minecraftVersionManager;
        _instanceModsManager = instanceModsManager;
        _modLoaderServices = modLoaderServices;
        _logger = logger;

        if (!_modLoaderServices.ContainsKey(MinecraftInstanceModLoader.Vanilla))
        {
            throw new InvalidOperationException("Missing mod loader service for VANILLA");
        }

        // TODO: Generate clientid after installation and save it; it is used by Mojang for
        //  telemetry purposes to differentiate installed clients.
        _installationId = "53a66c28-3108-4dfb-ab8b-95b0fcf44b5c";

        Task.WhenAll(RefreshAvailableVersionsAsync(true), RefreshInstancesAsync())
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger?.LogError(t.Exception,
                        "Problem loading available versions or Minecraft instances from database");
                }
            });
    }

    public async Task RefreshAvailableVersionsAsync(bool reload)
    {
        // We don't allow too-old versions yet, because there are too many complications with
        // managing them. For now, we settled on 1.16 as the minimum, which was released during the
        // day 202-06-23.
        var releaseThreshold = new UtcInstant(new DateTime(2020, 6, 23));
        var versions =
            (await _minecraftManager.GetStableVersionsAsync(reload)).Where(v => v.ReleaseTime > releaseThreshold)
            .ToImmutableList();

        await _lock.WaitAsync();
        try
        {
            if (AvailableVersions == versions
                || (AvailableVersions.Count == versions.Count && AvailableVersions.SequenceEqual(versions)))
            {
                return;
            }

            AvailableVersions = versions;
            AvailableVersionsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lock.Release();
            InstancesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task RefreshInstancesAsync()
    {
        var instances = (await _repository.GetAllMinecraftInstancesAsync()).ToImmutableList();
        await ImportPortableInstancesAsync(instances);
        instances = (await _repository.GetAllMinecraftInstancesAsync()).ToImmutableList();

        await _lock.WaitAsync();
        try
        {
            Instances = instances;
        }
        finally
        {
            _lock.Release();
            InstancesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ImportPortableInstancesAsync(ImmutableList<MinecraftInstance> existingInstances)
    {
        var knownFolders = existingInstances
            .Select(instance => instance.Folder)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownIds = existingInstances
            .Select(instance => instance.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var discovered = await _instanceModsManager.DiscoverPortableInstancesAsync();

        foreach (var candidate in discovered)
        {
            if (knownFolders.Contains(candidate.FolderName))
            {
                continue;
            }

            try
            {
                await ImportPortableInstanceAsync(candidate, knownIds);
                knownFolders.Add(candidate.FolderName);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to import portable instance from {Folder}", candidate.FolderPath);
            }
        }
    }

    public async Task<ImmutableList<ModLoaderVersionInfo>> GetLoaderVersionsAsync(
        MinecraftInstanceModLoader modLoader,
        string minecraftVersionId,
        bool reload)
    {
        var service = GetModLoaderService(modLoader);
        return await service.GetLoaderVersionsAsync(minecraftVersionId, reload);
    }

    public async Task CreateInstance(
        VersionInfo version,
        string instanceId,
        MinecraftInstanceModLoader modLoader,
        string? preferredModLoaderVersion,
        IProgress<ThreadSafeInstallProgressReporter.InstallProgress> progress)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new ArgumentException("Instance name cannot be empty or all whitespace!");
        }

        // WARN: This is a race condition, because we insert new record after a while, when we have the MC version info
        //  required to launch it. But we are blocking the app UI so it is "safe". Not the best UX, but good for now.
        var exist = await _repository.MinecraftInstanceExists(instanceId);
        if (exist)
        {
            // TODO: Throw custom/specific exception e.g., create something like AlreadyExistsException? -- also below
            throw new ArgumentException($"Instance '{instanceId}' already exists");
        }


        ////////////////////////////////////////////////////////////////////////////////////////////
        // Prepare instance folder
        Directory.CreateDirectory(_instancesFolder);
        var existingFolders = Directory.GetDirectories(_instancesFolder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToArray();
        var sanitizedAndNumberedInstanceFolderName =
            PathUtils.IncrementNumberedFolderNameIfExistsAndSanitize(instanceId, existingFolders);

        var instanceFolder = Path.Combine(_instancesFolder, sanitizedAndNumberedInstanceFolderName);
        if (Directory.Exists(instanceFolder))
        {
            // TODO: Throw custom/specific exception e.g., create something like AlreadyExistsException? -- also above
            throw new ArgumentException($"Instance '{instanceId}' already exists");
        }

        Directory.CreateDirectory(instanceFolder);
        var instanceNativeLibrariesFolder =
            MinecraftInstance.GetNativeLibrariesFolder(_instancesFolder, sanitizedAndNumberedInstanceFolderName);

        var modLoaderService = GetModLoaderService(modLoader);
        var resolvedModLoader = await modLoaderService.ResolveAsync(
            version.Id,
            preferredModLoaderVersion,
            _platform);

        ////////////////////////////////////////////////////////////////////////////////////////////
        // Download basic information about specific Minecraft version i.e., its client.json file
        var (versionDetails, minecraft) = await _minecraftManager.DownloadVersionAsync(
            version
        );
        var cachedVanillaNativeLibrariesFolder = minecraft.NativeLibrariesFolder;
        minecraft = ApplyModLoaderToVersion(minecraft, resolvedModLoader);

        _logger?.LogInformation("Inserting Minecraft instance into DB");

        // TODO: Create a DB record as soon as possible, sooner then we have here...
        // Now we now, that such MC version exists, and we can save an "installing" state instance into DB
        await _repository.AddInstallingMinecraftInstanceAsync(
            instanceId,
            minecraft,
            sanitizedAndNumberedInstanceFolderName,
            modLoader,
            resolvedModLoader.LaunchVersionId,
            resolvedModLoader.LoaderVersionId);

        await _instanceModsManager.EnsureInstanceMetadataAsync(new MinecraftInstance(
            instanceId,
            minecraft.VersionId,
            resolvedModLoader.LaunchVersionId,
            modLoader,
            resolvedModLoader.LoaderVersionId,
            MinecraftInstanceState.Installing,
            minecraft.Type,
            sanitizedAndNumberedInstanceFolderName,
            minecraft.RequiredJavaVersion,
            minecraft.ClientJarPath,
            minecraft.MainClass,
            minecraft.AssetIndex,
            minecraft.ClassPath,
            minecraft.GameArguments,
            minecraft.JvmArguments));

        await RefreshInstancesAsync();

        // Now create a complex thread-safe progress reporter, that handles parallelism and
        // combining of different progress sources.
        await using var progressReporter = new ThreadSafeInstallProgressReporter(instanceId,
            new Progress<ThreadSafeInstallProgressReporter.InstallProgress>(p =>
            {
                CurrentInstallProgress[p.InstanceId] = p;
                InstallProgressUpdated?.Invoke(this, p);
                progress.Report(p);
            }));

        // Report, that we have a validated the Minecraft version and proceed with its files download
        progressReporter.ReportStart();

        ////////////////////////////////////////////////////////////////////////////////////////////
        // Download Minecraft, its assets & libraries
        var lastMinecraftDownloadProgress = (uint)0;
        var lastAssetsDownloadProgress = (uint)0;
        var lastLibrariesDownloadProgress = (uint)0;

        var downloadMinecraftTask = _minecraftManager.DownloadAssetsAndLibraries(
            versionDetails,
            new Progress<double>(mcProgress =>
            {
                var pp = mcProgress < 1.0
                    ? Math.Min((uint)(100.0 * mcProgress), 99)
                    : 100;

                // Downloading the Minecraft jar file is single-threaded, so no need for atomic
                // progress update.
                if (lastMinecraftDownloadProgress == pp)
                {
                    return;
                }

                lastMinecraftDownloadProgress = pp;

                // ReSharper disable once AccessToDisposedClosure
                progressReporter.ReportMinecraftDownloadProgress(pp);
            }),
            new Progress<double>(assetsProgress =>
            {
                var pp = assetsProgress < 1.0
                    ? Math.Min((uint)(100.0 * assetsProgress), 99)
                    : 100;

                // Downloading assets is a parallel task i.e., every asset is downloaded on a
                // different thread. Thus, we need atomic operations to update the progress in a
                // thread-safe way.
                var v = Interlocked.Exchange(ref lastAssetsDownloadProgress, pp);
                if (v != pp)
                {
                    // ReSharper disable once AccessToDisposedClosure
                    progressReporter.ReportAssetsDownloadProgress(pp);
                }
            }),
            new Progress<double>(libsProgress =>
            {
                var pp = libsProgress < 1.0
                    ? Math.Min((uint)(100.0 * libsProgress), 99)
                    : 100;

                // Downloading libraries is a parallel task i.e., every lib is downloaded on a
                // different thread. Thus, we need atomic operations to update the progress in a
                // thread-safe way.
                var v = Interlocked.Exchange(ref lastLibrariesDownloadProgress, pp);
                if (v != pp)
                {
                    // ReSharper disable once AccessToDisposedClosure
                    progressReporter.ReportLibrariesDownloadProgress(pp);
                }
            })
        );

        ////////////////////////////////////////////////////////////////////////
        // Download Java
        var javaVersion = minecraft.RequiredJavaVersion;
        var lastJavaDownloadProgress = (uint)0;
        var downloadJavaTask = _javaManager.InstallJavaAsync(javaVersion,
            new Progress<double>(p =>
            {
                // Jave reports 95% after downloaded, 98% when extracted and 100% after clean up, so
                // we don't have to cap to 99% here.
                var pp = (uint)(100.0 * p);
                if (lastJavaDownloadProgress == pp)
                {
                    return;
                }

                lastJavaDownloadProgress = pp;

                // ReSharper disable once AccessToDisposedClosure
                progressReporter.ReportJavaDownloadProgress(pp);
            }));

        await Task.WhenAll(downloadMinecraftTask, downloadJavaTask);

        var javaExecutablePath = _javaManager.GetJavaExecutablePath(javaVersion) ??
                                 throw new ArgumentException($"Java {javaVersion} is missing");
        var lastModLoaderInstallProgress = (uint)0;
        await modLoaderService.InstallAsync(
            resolvedModLoader,
            new ModLoaderInstallContext(_platform, javaExecutablePath, minecraft.ClientJarPath),
            new Progress<double>(p =>
            {
                var pp = p < 1.0
                    ? Math.Min((uint)(100.0 * p), 99)
                    : 100;

                var v = Interlocked.Exchange(ref lastModLoaderInstallProgress, pp);
                if (v != pp)
                {
                    // ReSharper disable once AccessToDisposedClosure
                    progressReporter.ReportModLoaderInstallProgress(pp);
                }
            }));

        MaterializeInstanceNativeLibraries(cachedVanillaNativeLibrariesFolder, instanceNativeLibrariesFolder);

        _logger?.LogInformation("Successfully download Minecraft instance '{InstanceId}'", instanceId);

        CurrentInstallProgress.TryRemove(instanceId, out _);

        await _repository.SetMinecraftInstanceStateAsync(instanceId, MinecraftInstanceState.Ready);
        _logger?.LogInformation("Minecraft instance '{InstanceId}' is ready to play", instanceId);

        await _instanceModsManager.EnsureInstanceMetadataAsync(new MinecraftInstance(
            instanceId,
            minecraft.VersionId,
            resolvedModLoader.LaunchVersionId,
            modLoader,
            resolvedModLoader.LoaderVersionId,
            MinecraftInstanceState.Ready,
            minecraft.Type,
            sanitizedAndNumberedInstanceFolderName,
            minecraft.RequiredJavaVersion,
            minecraft.ClientJarPath,
            minecraft.MainClass,
            minecraft.AssetIndex,
            minecraft.ClassPath,
            minecraft.GameArguments,
            minecraft.JvmArguments));

        await RefreshInstancesAsync();
    }

    private async Task ImportPortableInstanceAsync(
        PortableInstanceCandidate candidate,
        HashSet<string> knownIds)
    {
        var modLoader = MinecraftInstance.ModLoaderFromString(candidate.Meta.ModLoader);
        if (modLoader == MinecraftInstanceModLoader.Unknown)
        {
            throw new InvalidOperationException($"Unsupported mod loader '{candidate.Meta.ModLoader}'.");
        }

        var version = AvailableVersions.FirstOrDefault(v => v.Id == candidate.Meta.MinecraftVersionId);
        if (version is null)
        {
            throw new InvalidOperationException(
                $"Minecraft version '{candidate.Meta.MinecraftVersionId}' is unavailable.");
        }

        var instanceId = MakeUniqueInstanceName(candidate.Meta.DisplayName, knownIds);
        var instanceNativeLibrariesFolder =
            MinecraftInstance.GetNativeLibrariesFolder(_instancesFolder, candidate.FolderName);
        var modLoaderService = GetModLoaderService(modLoader);
        var resolvedModLoader = await modLoaderService.ResolveAsync(
            version.Id,
            candidate.Meta.ModLoaderVersion,
            _platform);

        var (versionDetails, minecraft) = await _minecraftManager.DownloadVersionAsync(version);
        var cachedVanillaNativeLibrariesFolder = minecraft.NativeLibrariesFolder;
        minecraft = ApplyModLoaderToVersion(minecraft, resolvedModLoader);

        await _repository.AddInstallingMinecraftInstanceAsync(
            instanceId,
            minecraft,
            candidate.FolderName,
            modLoader,
            resolvedModLoader.LaunchVersionId,
            resolvedModLoader.LoaderVersionId);

        await _minecraftManager.DownloadAssetsAndLibraries(
            versionDetails,
            new Progress<double>(),
            new Progress<double>(),
            new Progress<double>());

        var javaVersion = minecraft.RequiredJavaVersion;
        await _javaManager.InstallJavaAsync(javaVersion, new Progress<double>());
        var javaExecutablePath = _javaManager.GetJavaExecutablePath(javaVersion) ??
                                 throw new ArgumentException($"Java {javaVersion} is missing");
        await modLoaderService.InstallAsync(
            resolvedModLoader,
            new ModLoaderInstallContext(_platform, javaExecutablePath, minecraft.ClientJarPath),
            new Progress<double>());

        MaterializeInstanceNativeLibraries(cachedVanillaNativeLibrariesFolder, instanceNativeLibrariesFolder);

        await _repository.SetMinecraftInstanceStateAsync(instanceId, MinecraftInstanceState.Ready);
        await _instanceModsManager.EnsureInstanceMetadataAsync(new MinecraftInstance(
            instanceId,
            minecraft.VersionId,
            resolvedModLoader.LaunchVersionId,
            modLoader,
            resolvedModLoader.LoaderVersionId,
            MinecraftInstanceState.Ready,
            minecraft.Type,
            candidate.FolderName,
            minecraft.RequiredJavaVersion,
            minecraft.ClientJarPath,
            minecraft.MainClass,
            minecraft.AssetIndex,
            minecraft.ClassPath,
            minecraft.GameArguments,
            minecraft.JvmArguments));
        knownIds.Add(instanceId);
    }

    public async Task DeleteInstanceAsync(string instanceId)
    {
        MinecraftInstance instance;

        // Phase 1: Validate and set Deleting state (under lock)
        await _lock.WaitAsync();
        try
        {
            instance = Instances.Find(i => i.Id == instanceId)
                       ?? throw new InvalidOperationException($"Instance '{instanceId}' not found.");

            if (LaunchedInstances.ContainsKey(instanceId))
            {
                throw new InvalidOperationException(
                    $"Cannot delete instance '{instanceId}' because it is currently running.");
            }

            if (instance.State == MinecraftInstanceState.Installing
                || CurrentInstallProgress.ContainsKey(instanceId))
            {
                throw new InvalidOperationException(
                    $"Cannot delete instance '{instanceId}' because it is currently installing.");
            }

            // Move the DB I/O off the main thread
            await Task.Run(() =>
                _repository.SetMinecraftInstanceStateAsync(instanceId, MinecraftInstanceState.Deleting));
        }
        finally
        {
            _lock.Release();
        }

        // Refresh so UI shows "Deleting" status
        await RefreshInstancesAsync();

        // Phase 2: Delete disk folder
        var instanceFolder = Path.Combine(_instancesFolder, instance.Folder);
        try
        {
            if (Directory.Exists(instanceFolder))
            {
                Directory.Delete(instanceFolder, true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete instance folder '{Folder}'", instanceFolder);

            // Transition to DeleteFailed
            await Task.Run(() =>
                _repository.SetMinecraftInstanceStateAsync(instanceId, MinecraftInstanceState.DeleteFailed));
            await RefreshInstancesAsync();
            throw new InvalidOperationException(
                "Failed to delete instance folder. The instance is marked for retry.",
                ex);
        }

        // Phase 3: Disk is gone -- now delete the DB record and clean the caches
        await Task.Run(() => _repository.RemoveMinecraftInstanceAsync(instanceId));
        _instanceModsManager.EvictInstanceCaches(instance);
        CurrentInstallProgress.TryRemove(instanceId, out _);

        await RefreshInstancesAsync();
    }

    public async Task<ImmutableList<string>> LaunchInstance(
        MinecraftInstance instance,
        Func<Task<Account>> accountProvider)
    {
        if (!LaunchedInstances.TryAdd(instance.Id, RunningState.Authenticating))
        {
            throw new InvalidOperationException($"Instance {instance.Id} is already running or launching.");
        }

        InstanceStateChanged?.Invoke(this, (instance.Id, RunningState.Authenticating));

        Account account;
        try
        {
            account = await accountProvider();
        }
        catch (Exception)
        {
            if (LaunchedInstances.TryRemove(instance.Id, out _))
            {
                InstanceStateChanged?.Invoke(this, (instance.Id, RunningState.Stopped));
            }

            throw;
        }

        // TODO: Is there a better way to handle this? We will test this before calling this method
        //  in the future, so it shouldn't happen then, shouldn't...
        var username = account.Username ?? throw new ArgumentException("Username cannot be null");

        if (LaunchedInstances.TryUpdate(instance.Id, RunningState.Launching, RunningState.Authenticating))
        {
            InstanceStateChanged?.Invoke(this, (instance.Id, RunningState.Launching));
        }

        return await Task.Run(async () =>
            {
                var javaExecutable = _javaManager.GetJavaExecutablePath((int)instance.RequiredJavaVersion) ??
                                     throw new ArgumentException($"Java {instance.RequiredJavaVersion} is missing");
                var cachedMc = await _minecraftManager.GetCachedVersionDetailsAsync(instance.VersionId);
                var nativeLibrariesFolder = instance.GetNativeLibrariesFolder(_instancesFolder);
                var mc = cachedMc with
                {
                    VersionId = string.IsNullOrWhiteSpace(instance.LaunchVersionId)
                        ? cachedMc.VersionId
                        : instance.LaunchVersionId,
                    Type = instance.Type,
                    RequiredJavaVersion = (int)instance.RequiredJavaVersion,
                    ClientJarPath = instance.ClientJarPath,
                    MainClass = instance.MainClass,
                    AssetIndex = instance.AssetIndex,
                    NativeLibrariesFolder = nativeLibrariesFolder,
                    ClassPath = instance.ClassPath,
                    GameArguments = instance.GameArguments,
                    JvmArguments = instance.JvmArguments,
                };
                var workdir = Path.Combine(_instancesFolder, instance.Folder);

                var allArguments = BuildLaunchArguments(
                    mc,
                    workdir,
                    username,
                    account.MinecraftUserId ??
                    "demo", // TODO: "demo" is just an offline/demo mode fallback -- properly handle missing MC profile
                    account.XboxUserId ?? "0", // "0" is just a fallback value
                    account.AccessToken);

                var mcErrors = new ConcurrentQueue<string>();

                var command = Cli.Wrap(javaExecutable)
                    .WithArguments(allArguments)
                    .WithWorkingDirectory(workdir)
                    .WithValidation(CommandResultValidation.None)
                    .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
                    {
                        _logger?.LogInformation("Minecraft '{Instance}': {LogLine}", instance.Id, line);
                        AnalyzeAppStateFromInfoLog(instance, line);

                        // Most of the errors are logged into stdout with "[STDERR]" substring...
                        if (line.Contains("[STDERR]"))
                        {
                            mcErrors.Enqueue(line);
                        }
                    }))
                    .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                    {
                        _logger?.LogError("Minecraft '{Instance}': {LogLine}", instance.Id, line);
                        mcErrors.Enqueue(line);
                    }));

                _logger?.LogInformation("Launching Minecraft {Instance} instance...", instance.Id);

                // This throws when the CLI parameters are invalid and not when the launched app crashes.
                await command.ExecuteAsync();

                return mcErrors.ToList();
            })
            .ContinueWith(t =>
            {
                LaunchedInstances.TryRemove(instance.Id, out _);

                if (t.IsFaulted || t.Result.Count > 0)
                {
                    _logger?.LogError(t.Exception, "Minecraft instance crashed");
                }

                InstanceStateChanged?.Invoke(this, (instance.Id, RunningState.Stopped));

                if (t.IsFaulted)
                {
                    t.Result.Add(t.Exception?.Message ?? "Minecraft instance crashed");
                }

                return t.Result.ToImmutableList();
            });
    }

    private void AnalyzeAppStateFromInfoLog(MinecraftInstance instance, string logLine)
    {
        // Very basic parsing of the log line to estimate the app state.
        // TODO: Tested on 1.21.10 only at the moment, so check other versions too.
        if (logLine.Contains("Backend library:") || logLine.Contains("LWJGL"))
        {
            if (LaunchedInstances.TryUpdate(instance.Id, RunningState.RendererReady, RunningState.Launching))
            {
                InstanceStateChanged?.Invoke(this, (instance.Id, RunningState.RendererReady));
            }
        }
        else if (logLine.Contains("Sound engine started"))
        {
            var changed = LaunchedInstances.TryUpdate(instance.Id, RunningState.Running, RunningState.SplashScreen)
                          || LaunchedInstances.TryUpdate(instance.Id, RunningState.Running, RunningState.Launching);

            if (changed)
            {
                InstanceStateChanged?.Invoke(this, (instance.Id, RunningState.Running));
            }
        }
        else
        {
            if (LaunchedInstances.TryUpdate(instance.Id, RunningState.SplashScreen, RunningState.RendererReady))
            {
                InstanceStateChanged?.Invoke(this, (instance.Id, RunningState.SplashScreen));
            }
        }
    }

    private IEnumerable<string> BuildLaunchArguments(
        MinecraftVersionManager.Version version,
        string workdir,
        string playerName,
        string uuid,
        string xuid,
        string accessToken)
    {
        var arguments = new List<string>();
        var classPath = GetClassPath(version);

        // basic JVM args
        arguments.AddRange([
            "-Xmx4G",
            "-Xms2G",
        ]);

        // version specific JVM args
        arguments.AddRange(
            version.JvmArguments.Select(a =>
                ProcessGamePlaceholders(a, version, workdir, classPath, playerName, uuid, xuid, accessToken)));

        // logging is an JVM argument too, but using the Minecraft's log4j config will start
        // emitting XML-based logs, and we like the default plain logs, so we don't use it.
        // if (version.LoggingConfigPath is not null && version.LoggingArgument is not null)
        // {
        //     arguments.Add(version.LoggingArgument.Replace("${path}", version.LoggingConfigPath));
        // }

        // Main class
        arguments.Add(version.MainClass);

        // version specific game arguments
        arguments.AddRange(
            version.GameArguments.Select(a =>
                ProcessGamePlaceholders(a, version, workdir, classPath, playerName, uuid, xuid, accessToken)));

        // TODO: There are some os rules for Win 10-only and we crudely remove them for now. We need to to move this
        //  into the arguments parsing.
        if ((OperatingSystem.IsWindows() && Environment.OSVersion.Version.Major != 10) ||
            (Environment.OSVersion.Version.Minor == 0 && Environment.OSVersion.Version.Build >= 22000))
        {
            // Windows 10 is from 10.0.xxxx.xxxx and Windows 11 is from 10.0.22000.xxxx
            return arguments.Where(a => !a.StartsWith("-Dos"));
        }

        return arguments;
    }

    private static string GetClassPath(MinecraftVersionManager.Version version)
    {
        var sb = new StringBuilder();
        sb.Append(version.ClientJarPath);

        foreach (var lib in version.ClassPath)
        {
            sb.Append(Path.PathSeparator);
            sb.Append(lib);
        }

        return sb.ToString();
    }

    private IModLoaderService GetModLoaderService(MinecraftInstanceModLoader modLoader)
    {
        if (_modLoaderServices.TryGetValue(modLoader, out var service))
        {
            return service;
        }

        throw new InvalidOperationException($"No mod loader service configured for {modLoader}");
    }

    private static MinecraftVersionManager.Version ApplyModLoaderToVersion(
        MinecraftVersionManager.Version minecraft,
        ResolvedModLoaderVersion modLoader)
    {
        if (modLoader.Libraries.Count == 0
            && modLoader.ExtraGameArguments.Count == 0
            && modLoader.ExtraJvmArguments.Count == 0
            && string.IsNullOrWhiteSpace(modLoader.MainClassOverride))
        {
            return minecraft;
        }

        var classPath = minecraft.ClassPath
            .Concat(modLoader.Libraries.Select(l => l.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gameArgs = minecraft.GameArguments
            .Concat(modLoader.ExtraGameArguments)
            .ToList();

        var jvmArgs = minecraft.JvmArguments
            .Concat(modLoader.ExtraJvmArguments)
            .ToList();

        return minecraft with
        {
            MainClass = string.IsNullOrWhiteSpace(modLoader.MainClassOverride)
                ? minecraft.MainClass
                : modLoader.MainClassOverride,
            ClassPath = classPath,
            GameArguments = gameArgs,
            JvmArguments = jvmArgs,
        };
    }

    private static void MaterializeInstanceNativeLibraries(
        string sourceFolder,
        string destinationFolder)
    {
        if (!Directory.Exists(sourceFolder))
        {
            throw new InvalidOperationException($"Missing cached native libraries folder '{sourceFolder}'");
        }

        if (Directory.Exists(destinationFolder))
        {
            Directory.Delete(destinationFolder, true);
        }

        Directory.CreateDirectory(destinationFolder);

        foreach (var sourceFile in Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceFolder, sourceFile);
            var destinationPath = Path.Combine(destinationFolder, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(sourceFile, destinationPath, true);
        }
    }

    private static string MakeUniqueInstanceName(string preferredName, HashSet<string> knownIds)
    {
        if (knownIds.Add(preferredName))
        {
            return preferredName;
        }

        for (var suffix = 2;; suffix++)
        {
            var candidate = $"{preferredName} ({suffix})";
            if (knownIds.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private string ProcessGamePlaceholders(
        string input,
        MinecraftVersionManager.Version version,
        string workdir,
        string classPath,
        string playerName,
        string uuid,
        string xuid,
        string accessToken) =>
        // https://minecraft.wiki/w/Minecraft_Wiki:Projects/wiki.vg_merge/Launching_the_game
        // TODO: fill-in legacy values for veeery old versions based on the link above ^^^.
        //  I wouldn't go below 1.12, ideally not below 1.16/1.18?
        input
            .Replace("${version_name}", version.VersionId)
            .Replace("${game_directory}", workdir)
            .Replace("${assets_root}", version.AssetsFolder)
            .Replace("${assets_index_name}", version.AssetIndex)
            .Replace("${auth_uuid}", uuid)
            .Replace("${auth_player_name}", playerName)
            .Replace("${auth_session}", accessToken)
            .Replace("${auth_access_token}", accessToken)
            .Replace("${auth_xuid}", xuid)
            // 'msa' for MS accounts, 'mojang' for old Mojang account and 'legacy' for veeery old Minecraft login
            .Replace("${user_type}", "msa")
            .Replace("${version_type}", version.Type)
            .Replace("${natives_directory}", version.NativeLibrariesFolder)
            .Replace("${launcher_name}", _launcherName)
            .Replace("${launcher_version}", _launcherVersion)
            .Replace("${classpath}", classPath)
            .Replace("${clientid}", _installationId);

    public void Dispose()
    {
        _javaManager.Dispose();
        _minecraftManager.Dispose();
    }
}
