using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CliWrap;
using CliWrap.Buffered;
using GenericLauncher.Http;
using GenericLauncher.Minecraft.Json;
using GenericLauncher.Minecraft.ModLoaders.Forge.Json;
using GenericLauncher.Misc;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Minecraft.ModLoaders.Forge;

public sealed partial class ForgeModLoaderService : IModLoaderService
{
    private const string MavenMetadataUrl =
        "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";

    private const string PromotionsUrl =
        "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

    private readonly HttpClient _httpClient;
    private readonly FileDownloader _fileDownloader;
    private readonly ILogger? _logger;
    private readonly string _loaderRootFolder;
    private readonly string _metadataFolder;
    private readonly string _librariesFolder;
    private readonly string _versionsFolder;

    public string DisplayName => "Forge";

    public ForgeModLoaderService(
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
            .Select(c => new ModLoaderVersionInfo(c.LoaderVersionId, c.Channel))
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

        ForgeVersionCandidate selected;
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

        var launchVersionId = CreateLaunchVersionId(minecraftVersionId, selected);
        var versionFolder = Path.Combine(_versionsFolder, launchVersionId);
        Directory.CreateDirectory(versionFolder);

        var installerJarPath = Path.Combine(versionFolder, "installer.jar");
        var installProfilePath = Path.Combine(versionFolder, "install_profile.json");
        var profilePath = Path.Combine(versionFolder, "profile.json");

        await EnsureInstallerMetadataAsync(selected, installerJarPath, installProfilePath, profilePath,
            cancellationToken);

        var installProfile = await ReadInstallProfileAsync(installProfilePath, cancellationToken);
        var versionProfile = await ReadVersionProfileAsync(profilePath, cancellationToken);

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

        var installProfile = await ReadInstallProfileAsync(resolved.InstallProfileJsonPath, cancellationToken);
        var versionProfile = await ReadVersionProfileAsync(resolved.ProfileJsonPath, cancellationToken);
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
        foreach (var item in downloadItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(item.Url))
            {
                await _fileDownloader.DownloadFileAsync(item.Url!, item.FilePath, item.Sha1, null, cancellationToken);
            }

            completed++;
            progress?.Report((double)completed / totalSteps);
        }

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

    private async Task<ImmutableList<ForgeVersionCandidate>> GetVersionCandidatesAsync(
        string minecraftVersionId,
        bool reload,
        CancellationToken cancellationToken)
    {
        var xml = await GetCachedOrDownloadTextAsync(
            Path.Combine(_metadataFolder, "maven-metadata.xml"),
            MavenMetadataUrl,
            reload,
            cancellationToken);

        var promotionsJson = await GetCachedOrDownloadTextAsync(
            Path.Combine(_metadataFolder, "promotions_slim.json"),
            PromotionsUrl,
            reload,
            cancellationToken);

        var promotions = JsonSerializer.Deserialize(promotionsJson, ForgeJsonContext.Default.ForgePromotions)?.Promos
                         ?? new Dictionary<string, string>();

        var prefix = $"{minecraftVersionId}-";
        var compatible = ParseMavenMetadataVersions(xml)
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .Select(v => new ForgeVersionCandidate(
                v[prefix.Length..],
                v,
                GetChannel(promotions, minecraftVersionId, v[prefix.Length..])))
            .ToList();

        compatible.Sort((left, right) => CompareChannels(left.Channel, right.Channel));
        return compatible.ToImmutableList();
    }

    private static string BuildInstallerUrl(ForgeVersionCandidate candidate) =>
        $"https://maven.minecraftforge.net/net/minecraftforge/forge/{candidate.ArtifactVersionId}/forge-{candidate.ArtifactVersionId}-installer.jar";

    private static string CreateLaunchVersionId(string minecraftVersionId, ForgeVersionCandidate candidate) =>
        $"{minecraftVersionId}-forge-{candidate.LoaderVersionId}";

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
        await File.WriteAllTextAsync(cachePath, payload);
#pragma warning restore CA2016
        cancellationToken.ThrowIfCancellationRequested();
        return payload;
    }

    private static ImmutableList<string> ParseMavenMetadataVersions(string xml) => XDocument.Parse(xml)
        .Descendants("version")
        .Select(v => v.Value.Trim())
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Reverse()
        .ToImmutableList();

    private async Task EnsureInstallerMetadataAsync(
        ForgeVersionCandidate candidate,
        string installerJarPath,
        string installProfilePath,
        string profilePath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(installerJarPath) && File.Exists(installProfilePath) && File.Exists(profilePath))
        {
            return;
        }

        var installerUrl = BuildInstallerUrl(candidate);
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

    private async Task<ForgeInstallProfile> ReadInstallProfileAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize(json, ForgeJsonContext.Default.ForgeInstallProfile)
               ?? throw new InvalidOperationException($"Failed to deserialize {DisplayName} install profile");
    }

    private async Task<ForgeVersionProfile> ReadVersionProfileAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize(json, ForgeJsonContext.Default.ForgeVersionProfile)
               ?? throw new InvalidOperationException($"Failed to deserialize {DisplayName} launcher profile");
    }

    private void ValidateResolvedVersion(
        string minecraftVersionId,
        ForgeVersionCandidate candidate,
        ForgeInstallProfile installProfile,
        ForgeVersionProfile versionProfile)
    {
        if (!string.Equals(installProfile.Minecraft, minecraftVersionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{DisplayName} version '{candidate.LoaderVersionId}' resolves to Minecraft {installProfile.Minecraft}, not {minecraftVersionId}");
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
                var filePath = Path.Combine(_librariesFolder,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
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
        ForgeInstallProfile installProfile,
        ForgeVersionProfile versionProfile,
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

    internal static ImmutableList<ForgeClientProcessorPlan> BuildClientProcessorPlans(
        ForgeInstallProfile installProfile) => installProfile.Processors is null
        ? []
        : installProfile.Processors
            .Where(AppliesToClient)
            .Select(ParseClientProcessorPlan)
            .ToImmutableList();

    internal static ForgeClientProcessorPlan ParseClientProcessorPlan(ForgeInstallProcessor processor)
    {
        if (processor.Args is null || processor.Args.Count == 0)
        {
            throw new InvalidOperationException("Forge client processor is missing arguments");
        }

        if (processor.Jar.StartsWith("net.minecraftforge:installertools:", StringComparison.OrdinalIgnoreCase))
        {
            var task = TryGetProcessorTask(processor.Args)
                       ?? throw new InvalidOperationException("Forge installertools processor is missing --task");

            return task == "DOWNLOAD_MOJMAPS"
                ? CreateClientProcessorPlan(ForgeClientProcessorKind.DownloadMojmaps, processor)
                : throw new InvalidOperationException($"Unsupported Forge client installertools task '{task}'");
        }

        if (processor.Jar.StartsWith("net.minecraftforge:ForgeAutoRenamingTool:", StringComparison.OrdinalIgnoreCase))
        {
            return CreateClientProcessorPlan(ForgeClientProcessorKind.ClientAutoRename, processor);
        }

        return processor.Jar.StartsWith("net.minecraftforge:binarypatcher:", StringComparison.OrdinalIgnoreCase)
            ? CreateClientProcessorPlan(ForgeClientProcessorKind.BinaryPatch, processor)
            : throw new InvalidOperationException($"Unsupported Forge client processor jar '{processor.Jar}'");
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
        ForgeClientProcessorPlan processor,
        ForgeInstallProfile installProfile,
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
                if (!await VerifyFileHashAsync(outputPath, expectedHash, cancellationToken))
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

    internal ForgeProcessorCommandSpec BuildProcessorCommand(
        ForgeClientProcessorPlan processor,
        ForgeInstallProfile installProfile,
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

        var mainClass = ReadJarMainClass(processorJarPath)
                        ?? throw new InvalidOperationException(
                            $"Processor jar '{processorJarPath}' is missing Main-Class");

        var resolvedArgs = processor.Args
            .Select(arg => ResolveProcessorValue(arg, installProfile, resolved, context, versionFolder, "client"))
            .ToImmutableList();

        var classPathEntries = processorClassPathEntries
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();

        return new ForgeProcessorCommandSpec(
            processor.Kind,
            processorJarPath,
            classPathEntries,
            mainClass,
            resolvedArgs);
    }

    private async Task ExecuteProcessorAsync(
        ForgeProcessorCommandSpec commandSpec,
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
        ForgeInstallProfile installProfile,
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
        ForgeInstallProfile installProfile,
        ResolvedModLoaderVersion resolved,
        ModLoaderInstallContext context,
        string versionFolder,
        string side)
    {
        return token switch
        {
            "INSTALLER" => resolved.InstallerJarPath ??
                           throw new InvalidOperationException("Missing installer jar path"),
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
        ForgeInstallProfile installProfile,
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

    private static string? ReadJarMainClass(string jarPath)
    {
        using var archive = ZipFile.OpenRead(jarPath);
        var manifestEntry = archive.GetEntry("META-INF/MANIFEST.MF");
        if (manifestEntry is null)
        {
            return null;
        }

        using var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8, false, leaveOpen: false);
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (line?.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase) == true)
            {
                return line["Main-Class:".Length..].Trim();
            }
        }

        return null;
    }

    private static async Task<bool> VerifyFileHashAsync(
        string filePath,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(filePath);
        var hash = expectedHash.Length switch
        {
            40 => await SHA1.HashDataAsync(fileStream, cancellationToken),
            64 => await SHA256.HashDataAsync(fileStream, cancellationToken),
            _ => throw new ArgumentException($"Unsupported hash length {expectedHash.Length}", nameof(expectedHash)),
        };

        var actualHash = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetChannel(Dictionary<string, string> promotions, string minecraftVersionId,
        string loaderVersionId)
    {
        if (promotions.TryGetValue($"{minecraftVersionId}-recommended", out var recommended) &&
            string.Equals(recommended, loaderVersionId, StringComparison.OrdinalIgnoreCase))
        {
            return "RECOMMENDED";
        }

        if (promotions.TryGetValue($"{minecraftVersionId}-latest", out var latest) &&
            string.Equals(latest, loaderVersionId, StringComparison.OrdinalIgnoreCase))
        {
            return "LATEST";
        }

        return "UNKNOWN";
    }

    private static int CompareChannels(string left, string right)
    {
        static int Rank(string channel) => channel switch
        {
            "RECOMMENDED" => 0,
            "LATEST" => 1,
            _ => 2,
        };

        return Rank(left).CompareTo(Rank(right));
    }

    [GeneratedRegex(@"\{([A-Z0-9_]+)\}")]
    private static partial Regex PlaceholderRegex();

    private static bool AppliesToClient(ForgeInstallProcessor processor)
    {
        return processor.Sides is null ||
               processor.Sides.Count == 0 ||
               processor.Sides.Contains("client", StringComparer.OrdinalIgnoreCase);
    }

    private static ForgeClientProcessorPlan CreateClientProcessorPlan(ForgeClientProcessorKind kind,
        ForgeInstallProcessor processor) => new(
        kind,
        processor.Jar,
        (processor.Classpath ?? []).ToImmutableList(),
        processor.Args?.ToImmutableList() ?? [],
        (processor.Outputs ?? new Dictionary<string, string>())
        .ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal));

    private sealed record ForgeVersionCandidate(
        string LoaderVersionId,
        string ArtifactVersionId,
        string Channel);

    internal sealed record ForgeClientProcessorPlan(
        ForgeClientProcessorKind Kind,
        string Jar,
        ImmutableList<string> Classpath,
        ImmutableList<string> Args,
        ImmutableDictionary<string, string> Outputs);

    internal sealed record ForgeProcessorCommandSpec(
        ForgeClientProcessorKind Kind,
        string JarPath,
        ImmutableList<string> ClassPathEntries,
        string MainClass,
        ImmutableList<string> Arguments);

    internal enum ForgeClientProcessorKind
    {
        DownloadMojmaps,
        ClientAutoRename,
        BinaryPatch,
    }
}
