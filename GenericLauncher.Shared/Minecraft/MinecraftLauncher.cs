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
using GenericLauncher.Java;
using GenericLauncher.Minecraft.Json;
using GenericLauncher.Misc;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Minecraft;

public sealed class MinecraftLauncher : IDisposable
{
    public enum RunningState
    {
        Launching,
        RendererReady,
        SplashScreen,
        Running,
    }

    private readonly string _currentOs;
    private readonly string _launcherName;
    private readonly string _launcherVersion;
    private readonly string _instancesFolder;
    private readonly string _installationId;
    private readonly LauncherRepository _repository;
    private readonly JavaVersionManager _javaManager;
    private readonly MinecraftVersionManager _minecraftManager;
    private readonly ILogger? _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);
    public ImmutableList<VersionInfo> AvailableVersions = [];
    public ImmutableList<MinecraftInstance> Instances = [];
    public readonly ConcurrentDictionary<string, RunningState> LaunchedInstances = [];

    public event EventHandler? AvailableVersionsChanged;
    public event EventHandler? InstancesChanged;
    public event EventHandler? LaunchedInstancesChanged;

    public event EventHandler<ThreadSafeInstallProgressReporter.InstallProgress>? InstallProgressUpdated;

    public MinecraftLauncher(
        string currentOs,
        string launcherName,
        string launcherVersion,
        string instancesFolder,
        LauncherRepository repository,
        MinecraftVersionManager minecraftVersionManager,
        JavaVersionManager javaVersionManager,
        ILogger? logger = null)
    {
        _currentOs = currentOs;
        _launcherName = launcherName;
        _launcherVersion = launcherVersion;
        _instancesFolder = instancesFolder;
        _repository = repository;
        _javaManager = javaVersionManager;
        _minecraftManager = minecraftVersionManager;
        _logger = logger;

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

    public async Task CreateInstance(
        VersionInfo version,
        string name,
        IProgress<ThreadSafeInstallProgressReporter.InstallProgress> progress)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Instance name cannot be empty or all whitespace!");
        }

        // WARN: This is a race condition, because we insert new record after a while, when we have the MC version info
        //  required to launch it. But we are blocking the app UI so it is "safe". Not the best UX, but good for now.
        var exist = await _repository.MinecraftInstanceExists(name);
        if (exist)
        {
            // TODO: Throw custom/specific exception e.g., create something like AlreadyExistsException? -- also below
            throw new ArgumentException($"Instance '{name}' already exists");
        }


        ////////////////////////////////////////////////////////////////////////////////////////////
        // Prepare instance folder
        Directory.CreateDirectory(_instancesFolder);
        var existingFolders = Directory.GetDirectories(_instancesFolder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToArray();
        var sanitizedAndNumberedInstanceFolderName =
            PathUtils.IncrementNumberedFolderNameIfExistsAndSanitize(name, existingFolders);

        var instanceFolder = Path.Combine(_instancesFolder, sanitizedAndNumberedInstanceFolderName);
        if (Directory.Exists(instanceFolder))
        {
            // TODO: Throw custom/specific exception e.g., create something like AlreadyExistsException? -- also above
            throw new ArgumentException($"Instance '{name}' already exists");
        }

        Directory.CreateDirectory(instanceFolder);

        ////////////////////////////////////////////////////////////////////////////////////////////
        // Download basic information about specific Minecraft version i.e., its client.json file
        var (versionDetails, minecraft) = await _minecraftManager.DownloadVersionAsync(
            version,
            _currentOs
        );

        _logger?.LogInformation("Inserting Minecraft instance into DB");

        // TODO: Create a DB record as soon as possible, sooner then we have here...
        // Now we now, that such MC version exists, and we can save an "installing" state instance into DB
        await _repository.AddInstallingMinecraftInstanceAsync(
            name,
            minecraft,
            sanitizedAndNumberedInstanceFolderName);
        await RefreshInstancesAsync();

        // Now create a complex thread-safe progress reporter, that handles parallelism and
        // combining of different progress sources.
        await using var progressReporter = new ThreadSafeInstallProgressReporter(name,
            new Progress<ThreadSafeInstallProgressReporter.InstallProgress>(p =>
            {
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
            _currentOs,
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
                progressReporter.ReportJavaDownloadProgress(pp);
            }));

        await Task.WhenAll(downloadMinecraftTask, downloadJavaTask);

        _logger?.LogInformation("Successfully download Minecraft instance '{InstanceId}'", name);

        await _repository.SetMinecraftInstanceAsReadyAsync(name);
        _logger?.LogInformation("Minecraft instance '{InstanceId}' is ready to play", name);

        await RefreshInstancesAsync();
    }

    public Task<ImmutableList<string>> LaunchInstance(MinecraftInstance instance, Account account)
    {
        LaunchedInstances.TryAdd(instance.Id, RunningState.Launching);
        LaunchedInstancesChanged?.Invoke(this, EventArgs.Empty);

        return Task.Run(async () =>
            {
                var javaExecutable = _javaManager.GetJavaExecutablePath((int)instance.RequiredJavaVersion) ??
                                     throw new ArgumentException($"Java {instance.RequiredJavaVersion} is missing");
                var mc = await _minecraftManager.GetCachedVersionDetailsAsync(instance.VersionId, _currentOs);
                var workdir = Path.Combine(_instancesFolder, instance.Folder);

                var allArguments = BuildLaunchArguments(
                    mc,
                    workdir,
                    account.Username,
                    account.Id,
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

                LaunchedInstancesChanged?.Invoke(this, EventArgs.Empty);

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
                LaunchedInstancesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (logLine.Contains("Sound engine started"))
        {
            var changed = LaunchedInstances.TryUpdate(instance.Id, RunningState.Running, RunningState.SplashScreen)
                          || LaunchedInstances.TryUpdate(instance.Id, RunningState.Running, RunningState.Launching);

            if (changed)
            {
                LaunchedInstancesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            if (LaunchedInstances.TryUpdate(instance.Id, RunningState.SplashScreen, RunningState.RendererReady))
            {
                LaunchedInstancesChanged?.Invoke(this, EventArgs.Empty);
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
            sb.Append(Path.Combine(version.LibrariesFolder, lib));
        }

        return sb.ToString();
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