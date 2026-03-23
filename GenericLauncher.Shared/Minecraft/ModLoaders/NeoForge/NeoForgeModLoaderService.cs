using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CliWrap;
using CliWrap.Buffered;
using GenericLauncher.Http;
using GenericLauncher.Minecraft.Json;
using GenericLauncher.Minecraft.ModLoaders.NeoForge.Json;
using GenericLauncher.Misc;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Minecraft.ModLoaders.NeoForge;

public sealed partial class NeoForgeModLoaderService : IModLoaderService
{
    private const string MavenMetadataUrl =
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

    private readonly HttpClient _httpClient;
    private readonly FileDownloader _fileDownloader;
    private readonly ILogger? _logger;
    private readonly string _loaderRootFolder;
    private readonly string _metadataFolder;
    private readonly string _librariesFolder;
    private readonly string _versionsFolder;

    public string DisplayName => "NeoForge";

    public NeoForgeModLoaderService(
        string loaderRootFolder,
        string librariesFolder,
        HttpClient httpClient,
        FileDownloader fileDownloader,
        ILogger? logger = null)
    {
        _loaderRootFolder = loaderRootFolder;
        _httpClient = httpClient;
        _fileDownloader = fileDownloader;
        _logger = logger;
        _metadataFolder = Path.Combine(loaderRootFolder, "metadata");
        _librariesFolder = librariesFolder;
        _versionsFolder = Path.Combine(loaderRootFolder, "versions");
    }

    public async Task<ImmutableList<ModLoaderVersionInfo>> GetLoaderVersionsAsync(
        string minecraftVersionId,
        bool reload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersionId))
        {
            return [];
        }

        var candidates = await GetVersionCandidatesAsync(minecraftVersionId, reload, cancellationToken);
        return candidates
            .Select(c => new ModLoaderVersionInfo(c.LoaderVersionId, "LATEST"))
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

        var candidates = await GetVersionCandidatesAsync(minecraftVersionId, false, cancellationToken);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No {DisplayName} versions available for Minecraft {minecraftVersionId}");
        }

        NeoForgeVersionCandidate selected;
        if (!string.IsNullOrWhiteSpace(preferredLoaderVersion))
        {
            selected = candidates.FirstOrDefault(v =>
                           string.Equals(v.LoaderVersionId, preferredLoaderVersion,
                               StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(v.ArtifactVersionId, preferredLoaderVersion,
                               StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException(
                           $"{DisplayName} loader version '{preferredLoaderVersion}' is not available for Minecraft {minecraftVersionId}");
        }
        else
        {
            selected = candidates[0];
        }

        Directory.CreateDirectory(_versionsFolder);
        Directory.CreateDirectory(_librariesFolder);

        var launchVersionId = $"neoforge-{selected.LoaderVersionId}";
        var versionFolder = Path.Combine(_versionsFolder, launchVersionId);
        Directory.CreateDirectory(versionFolder);

        var installerJarPath = Path.Combine(versionFolder, "installer.jar");
        var installProfilePath = Path.Combine(versionFolder, "install_profile.json");
        var profilePath = Path.Combine(versionFolder, "profile.json");

        await EnsureInstallerMetadataAsync(selected, installerJarPath, installProfilePath, profilePath,
            cancellationToken);

        var installProfile = await File.DeserializeJsonAsync(installProfilePath,
                                 NeoForgeJsonContext.Default.NeoForgeInstallProfile, cancellationToken)
                             ?? throw new InvalidOperationException(
                                 $"Failed to deserialize {DisplayName} install profile");
        var versionProfile = await File.DeserializeJsonAsync(profilePath,
                                 NeoForgeJsonContext.Default.NeoForgeVersionProfile, cancellationToken)
                             ?? throw new InvalidOperationException(
                                 $"Failed to deserialize {DisplayName} launcher profile");

        ValidateResolvedVersion(minecraftVersionId, selected, installProfile, versionProfile);

        var extraJvmArgs = NormalizeResolvedArguments(
            ArgumentsParser.FlattenArguments(versionProfile.Arguments?.Jvm, platform),
            launchVersionId);
        var extraGameArgs = NormalizeResolvedArguments(
            ArgumentsParser.FlattenArguments(versionProfile.Arguments?.Game, platform),
            launchVersionId);
        var libraries = ResolveLaunchLibraries(versionProfile.Libraries, platform);

        return new ResolvedModLoaderVersion(
            DisplayName,
            minecraftVersionId,
            launchVersionId,
            selected.LoaderVersionId,
            profilePath,
            installProfilePath,
            installerJarPath,
            string.IsNullOrWhiteSpace(versionProfile.MainClass) ? null : versionProfile.MainClass,
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
        if (!string.Equals(resolved.DisplayName, DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Resolved version does not belong to {DisplayName} loader", nameof(resolved));
        }

        if (string.IsNullOrWhiteSpace(resolved.InstallProfileJsonPath) ||
            string.IsNullOrWhiteSpace(resolved.InstallerJarPath))
        {
            throw new InvalidOperationException($"{DisplayName} installation metadata is missing");
        }

        Directory.CreateDirectory(_loaderRootFolder);
        Directory.CreateDirectory(_librariesFolder);

        var installProfile = await File.DeserializeJsonAsync(resolved.InstallProfileJsonPath,
                                 NeoForgeJsonContext.Default.NeoForgeInstallProfile, cancellationToken)
                             ?? throw new InvalidOperationException(
                                 $"Failed to deserialize {DisplayName} install profile");
        var versionProfile = await File.DeserializeJsonAsync(resolved.ProfileJsonPath,
                                 NeoForgeJsonContext.Default.NeoForgeVersionProfile, cancellationToken)
                             ?? throw new InvalidOperationException(
                                 $"Failed to deserialize {DisplayName} launcher profile");
        var versionFolder = Path.GetDirectoryName(resolved.ProfileJsonPath)
                            ?? throw new InvalidOperationException("Resolved profile path does not have a folder");

        var downloadItems = BuildDownloadItems(installProfile, versionProfile, context.Platform);
        var processors = BuildClientProcessorPlans(installProfile);
        var totalSteps = downloadItems.Count + processors.Count;
        if (totalSteps == 0)
        {
            progress?.Report(1.0);
            return;
        }

        var completed = 0;
        await Parallel.ForEachAsync(
            downloadItems,
            new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                if (!string.IsNullOrWhiteSpace(item.Url))
                {
                    await _fileDownloader.DownloadFileAsync(item.Url!, item.FilePath, item.Sha1, null, token);
                }

                var done = Interlocked.Increment(ref completed);
                progress?.Report((double)done / totalSteps);
            });

        foreach (var processor in processors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await AreProcessorOutputsUpToDateAsync(processor, installProfile, resolved, context, versionFolder,
                    cancellationToken))
            {
                completed++;
                progress?.Report((double)completed / totalSteps);
                continue;
            }

            var command = BuildProcessorCommand(processor, installProfile, resolved, context, versionFolder);
            await ExecuteProcessorAsync(command, context.JavaExecutablePath, cancellationToken);
            completed++;
            progress?.Report((double)completed / totalSteps);
        }

        progress?.Report(1.0);
    }

    private async Task<ImmutableList<NeoForgeVersionCandidate>> GetVersionCandidatesAsync(
        string minecraftVersionId,
        bool reload,
        CancellationToken cancellationToken)
    {
        var prefix = NormalizeMinecraftVersionPrefix(minecraftVersionId);
        if (prefix is null)
        {
            return [];
        }

        var xml = await GetCachedOrDownloadTextAsync(
            Path.Combine(_metadataFolder, "maven-metadata.xml"),
            MavenMetadataUrl,
            reload,
            cancellationToken);

        return MavenCoordinate.ParseMetadataVersions(xml)
            .Where(v => v.Equals(prefix, StringComparison.Ordinal) ||
                        v.StartsWith($"{prefix}.", StringComparison.Ordinal))
            .Select(v => new NeoForgeVersionCandidate(v, v))
            .ToImmutableList();
    }

    private async Task<string> GetCachedOrDownloadTextAsync(
        string cachePath,
        string url,
        bool reload,
        CancellationToken cancellationToken)
    {
        if (!reload && File.Exists(cachePath))
        {
            return await File.ReadAllTextAsync(cachePath, cancellationToken);
        }

        _logger?.LogInformation("Downloading {DisplayName} metadata: {Url}", DisplayName, url);
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? _metadataFolder);
#pragma warning disable CA2016
        // ReSharper disable once MethodSupportsCancellation
        await File.WriteAllTextAsync(cachePath, payload);
#pragma warning restore CA2016
        cancellationToken.ThrowIfCancellationRequested();
        return payload;
    }

    private async Task EnsureInstallerMetadataAsync(
        NeoForgeVersionCandidate candidate,
        string installerJarPath,
        string installProfilePath,
        string profilePath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(installerJarPath) && File.Exists(installProfilePath) && File.Exists(profilePath))
        {
            return;
        }

        var installerUrl =
            $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{candidate.ArtifactVersionId}/neoforge-{candidate.ArtifactVersionId}-installer.jar";
        await _fileDownloader.DownloadFileAsync(installerUrl, installerJarPath, null, null, cancellationToken);

        await using var installerStream = File.OpenRead(installerJarPath);
        await using var installerArchive = new ZipArchive(installerStream, ZipArchiveMode.Read, false);
        await ZipUtils.ExtractEntriesAsync(
            installerArchive,
            [
                new ZipExtractionRequest("install_profile.json", installProfilePath),
                new ZipExtractionRequest("version.json", profilePath),
            ],
            cancellationToken);
    }

    private void ValidateResolvedVersion(
        string minecraftVersionId,
        NeoForgeVersionCandidate candidate,
        NeoForgeInstallProfile installProfile,
        NeoForgeVersionProfile versionProfile)
    {
        var installMinecraftVersion = installProfile.Minecraft;
        if (!string.Equals(installMinecraftVersion, minecraftVersionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{DisplayName} version '{candidate.LoaderVersionId}' resolves to Minecraft {installMinecraftVersion}, not {minecraftVersionId}");
        }

        if (!string.IsNullOrWhiteSpace(versionProfile.InheritsFrom) &&
            !string.Equals(versionProfile.InheritsFrom, minecraftVersionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{DisplayName} launcher profile inherits from {versionProfile.InheritsFrom}, not {minecraftVersionId}");
        }
    }

    private ImmutableList<ResolvedModLoaderLibrary> ResolveLaunchLibraries(List<Library>? libraries,
        LauncherPlatform platform)
        => MinecraftLibrarySelector.SelectLibraries(libraries, platform)
            .Where(l => !string.IsNullOrWhiteSpace(l.Name))
            .Select(l =>
            {
                var artifact = l.Downloads?.Artifact;
                var relativePath = artifact?.Path ?? MavenCoordinate.ToRelativePath(l.Name);
                var filePath = Path.Combine(_librariesFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var url = string.IsNullOrWhiteSpace(artifact?.Url)
                    ? null
                    : artifact!.Url;

                return new ResolvedModLoaderLibrary(l.Name, url, filePath, artifact?.Sha1);
            })
            .DistinctBy(l => l.Name)
            .ToImmutableList();

    private ImmutableList<string> NormalizeResolvedArguments(IEnumerable<string> args, string launchVersionId) => args
        .Select(a => a
            .Replace("${library_directory}", _librariesFolder, StringComparison.Ordinal)
            .Replace("${classpath_separator}", Path.PathSeparator.ToString(), StringComparison.Ordinal)
            .Replace("${version_name}", launchVersionId, StringComparison.Ordinal))
        .ToImmutableList();

    private List<ResolvedModLoaderLibrary> BuildDownloadItems(
        NeoForgeInstallProfile installProfile,
        NeoForgeVersionProfile versionProfile,
        LauncherPlatform platform)
    {
        var downloads = new List<ResolvedModLoaderLibrary>();

        if (installProfile.Libraries is not null)
        {
            downloads.AddRange(installProfile.Libraries
                .Where(l => l.Downloads?.Artifact is not null)
                .Select(l =>
                {
                    var artifact = l.Downloads!.Artifact!;
                    return new ResolvedModLoaderLibrary(
                        l.Name,
                        artifact.Url,
                        Path.Combine(_librariesFolder, artifact.Path.Replace('/', Path.DirectorySeparatorChar)),
                        artifact.Sha1);
                }));
        }

        downloads.AddRange(ResolveLaunchLibraries(versionProfile.Libraries, platform)
            .Where(l => !string.IsNullOrWhiteSpace(l.Url)));

        return downloads
            .DistinctBy(l => l.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static ImmutableList<NeoForgeClientProcessorPlan> BuildClientProcessorPlans(
        NeoForgeInstallProfile installProfile) => installProfile.Processors is null
        ? []
        : installProfile.Processors
            .Where(AppliesToClient)
            .Select(ParseClientProcessorPlan)
            .ToImmutableList();

    internal static NeoForgeClientProcessorPlan ParseClientProcessorPlan(NeoForgeInstallProcessor processor)
    {
        if (processor.Args is null || processor.Args.Count == 0)
        {
            throw new InvalidOperationException("NeoForge client processor is missing arguments");
        }

        if (!processor.Jar.StartsWith("net.neoforged.installertools:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported NeoForge client processor jar '{processor.Jar}'");

        var task = TryGetProcessorTask(processor.Args)
                   ?? throw new InvalidOperationException("NeoForge installertools processor is missing --task");

        return task switch
        {
            "DOWNLOAD_MOJMAPS" => CreateClientProcessorPlan(NeoForgeClientProcessorKind.DownloadMojmaps, processor),
            "PROCESS_MINECRAFT_JAR" => CreateClientProcessorPlan(NeoForgeClientProcessorKind.ProcessMinecraftJar,
                processor),
            _ => throw new InvalidOperationException($"Unsupported NeoForge client installertools task '{task}'"),
        };
    }

    internal static string? TryGetProcessorTask(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--task", StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private async Task<bool> AreProcessorOutputsUpToDateAsync(
        NeoForgeClientProcessorPlan processor,
        NeoForgeInstallProfile installProfile,
        ResolvedModLoaderVersion resolved,
        ModLoaderInstallContext context,
        string versionFolder,
        CancellationToken cancellationToken)
    {
        if (processor.Outputs.Count == 0)
        {
            return false;
        }

        foreach (var output in processor.Outputs)
        {
            var outputPath =
                ResolveProcessorValue(output.Key, installProfile, resolved, context, versionFolder, "client");
            var expectedHash =
                ResolveProcessorValue(output.Value, installProfile, resolved, context, versionFolder, "client")
                    .Trim('\'');

            if (!File.Exists(outputPath))
            {
                return false;
            }

            try
            {
                if (!await FileDownloader.VerifyFileHashAsync(outputPath, expectedHash, cancellationToken))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to verify {DisplayName} processor output '{OutputPath}'", DisplayName,
                    outputPath);
                return false;
            }
        }

        return true;
    }

    internal NeoForgeProcessorCommandSpec BuildProcessorCommand(
        NeoForgeClientProcessorPlan processor,
        NeoForgeInstallProfile installProfile,
        ResolvedModLoaderVersion resolved,
        ModLoaderInstallContext context,
        string versionFolder)
    {
        var processorJarPath = ResolveCoordinatePath(processor.Jar);
        var processorClassPathEntries = new List<string> { processorJarPath };
        if (processor.Classpath.Count > 0)
        {
            processorClassPathEntries.AddRange(processor.Classpath.Select(ResolveCoordinatePath));
        }

        var mainClass = ZipUtils.ReadJarMainClass(processorJarPath)
                        ?? throw new InvalidOperationException(
                            $"Processor jar '{processorJarPath}' is missing Main-Class");

        var resolvedArgs = processor.Args
            .Select(arg => ResolveProcessorValue(arg, installProfile, resolved, context, versionFolder, "client"))
            .ToImmutableList();

        var classPathEntries = processorClassPathEntries
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();

        return new NeoForgeProcessorCommandSpec(
            processor.Kind,
            processorJarPath,
            classPathEntries,
            mainClass,
            resolvedArgs);
    }

    private async Task ExecuteProcessorAsync(
        NeoForgeProcessorCommandSpec commandSpec,
        string javaExecutablePath,
        CancellationToken cancellationToken)
    {
        var command = Cli.Wrap(javaExecutablePath)
            .WithArguments(args =>
            {
                args.Add("-cp");
                args.Add(string.Join(Path.PathSeparator, commandSpec.ClassPathEntries));
                args.Add(commandSpec.MainClass);
                foreach (var arg in commandSpec.Arguments)
                {
                    args.Add(arg);
                }
            })
            .WithWorkingDirectory(_loaderRootFolder)
            .WithValidation(CommandResultValidation.None);

        _logger?.LogInformation("Executing {DisplayName} processor {Kind} from {Jar}", DisplayName, commandSpec.Kind,
            commandSpec.JarPath);

        var result = await command.ExecuteBufferedAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _logger?.LogInformation("{DisplayName} processor stdout: {Output}", DisplayName,
                result.StandardOutput.Trim());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            _logger?.LogError("{DisplayName} processor stderr: {Output}", DisplayName, result.StandardError.Trim());
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{DisplayName} processor {commandSpec.Kind} failed with exit code {result.ExitCode}. " +
                $"stdout: {result.StandardOutput.Trim()} stderr: {result.StandardError.Trim()}");
        }
    }

    private string ResolveProcessorValue(
        string raw,
        NeoForgeInstallProfile installProfile,
        ResolvedModLoaderVersion resolved,
        ModLoaderInstallContext context,
        string versionFolder,
        string side)
    {
        if (raw.StartsWith('[') && raw.EndsWith(']'))
        {
            return ResolveCoordinatePath(raw[1..^1]);
        }

        if (raw.StartsWith('\'') && raw.EndsWith('\''))
        {
            return raw[1..^1];
        }

        if (raw.StartsWith('/'))
        {
            return EnsureInstallerPayloadPath(raw, resolved, versionFolder);
        }

        return PlaceholderRegex().Replace(raw, match =>
        {
            var token = match.Groups[1].Value;
            return ResolvePlaceholderToken(token, installProfile, resolved, context, versionFolder, side);
        });
    }

    private string ResolvePlaceholderToken(
        string token,
        NeoForgeInstallProfile installProfile,
        ResolvedModLoaderVersion resolved,
        ModLoaderInstallContext context,
        string versionFolder,
        string side)
    {
        return token switch
        {
            "INSTALLER" => resolved.InstallerJarPath
                           ?? throw new InvalidOperationException("Missing installer jar path"),
            "ROOT" => _loaderRootFolder,
            "LIBRARY_DIR" => _librariesFolder,
            "MINECRAFT_JAR" => context.MinecraftClientJarPath,
            "MINECRAFT_VERSION" => resolved.MinecraftVersionId,
            "SIDE" => side,
            _ => ResolveDataValue(token, installProfile, resolved, context, versionFolder, side),
        };
    }

    private string ResolveDataValue(
        string token,
        NeoForgeInstallProfile installProfile,
        ResolvedModLoaderVersion resolved,
        ModLoaderInstallContext context,
        string versionFolder,
        string side)
    {
        if (installProfile.Data is null || !installProfile.Data.TryGetValue(token, out var entry))
        {
            throw new InvalidOperationException($"Unknown install profile token '{token}'");
        }

        var value = string.Equals(side, "server", StringComparison.OrdinalIgnoreCase)
            ? entry.Server ?? entry.Client
            : entry.Client ?? entry.Server;

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Install profile token '{token}' does not define a usable value");
        }

        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            return ResolveCoordinatePath(value[1..^1]);
        }

        if (value.StartsWith('\'') && value.EndsWith('\''))
        {
            return value[1..^1];
        }

        if (value.StartsWith('/'))
        {
            return EnsureInstallerPayloadPath(value, resolved, versionFolder);
        }

        return ResolveProcessorValue(value, installProfile, resolved, context, versionFolder, side);
    }

    private static string EnsureInstallerPayloadPath(string installerRelativePath, ResolvedModLoaderVersion resolved,
        string versionFolder)
    {
        var normalized = installerRelativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var outputPath = Path.Combine(versionFolder, normalized);
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var installerPath = resolved.InstallerJarPath ??
                            throw new InvalidOperationException("Missing installer jar path");
        using var archive = ZipFile.OpenRead(installerPath);
        ZipUtils.ExtractEntries(
            archive,
            [
                new ZipExtractionRequest(installerRelativePath.TrimStart('/'), outputPath),
            ]);
        return outputPath;
    }

    private string ResolveCoordinatePath(string coordinate)
    {
        return Path.Combine(_librariesFolder,
            MavenCoordinate.ToRelativePath(coordinate).Replace('/', Path.DirectorySeparatorChar));
    }

    private static string? NormalizeMinecraftVersionPrefix(string minecraftVersionId)
    {
        if (!minecraftVersionId.StartsWith("1.", StringComparison.Ordinal))
        {
            return null;
        }

        return minecraftVersionId[2..];
    }

    [GeneratedRegex(@"\{([A-Z0-9_]+)\}")]
    private static partial Regex PlaceholderRegex();

    private static bool AppliesToClient(NeoForgeInstallProcessor processor)
    {
        return processor.Sides is null ||
               processor.Sides.Count == 0 ||
               processor.Sides.Contains("client", StringComparer.OrdinalIgnoreCase);
    }

    private static NeoForgeClientProcessorPlan CreateClientProcessorPlan(
        NeoForgeClientProcessorKind kind,
        NeoForgeInstallProcessor processor)
    {
        return new NeoForgeClientProcessorPlan(
            kind,
            processor.Jar,
            (processor.Classpath ?? []).ToImmutableList(),
            processor.Args?.ToImmutableList() ?? [],
            (processor.Outputs ?? new Dictionary<string, string>())
            .ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal));
    }

    private sealed record NeoForgeVersionCandidate(
        string LoaderVersionId,
        string ArtifactVersionId);

    internal sealed record NeoForgeClientProcessorPlan(
        NeoForgeClientProcessorKind Kind,
        string Jar,
        ImmutableList<string> Classpath,
        ImmutableList<string> Args,
        ImmutableDictionary<string, string> Outputs);

    internal sealed record NeoForgeProcessorCommandSpec(
        NeoForgeClientProcessorKind Kind,
        string JarPath,
        ImmutableList<string> ClassPathEntries,
        string MainClass,
        ImmutableList<string> Arguments);

    internal enum NeoForgeClientProcessorKind
    {
        DownloadMojmaps,
        ProcessMinecraftJar,
    }
}
