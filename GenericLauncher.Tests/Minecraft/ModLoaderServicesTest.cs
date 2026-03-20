using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GenericLauncher.Database.Model;
using GenericLauncher.Http;
using GenericLauncher.Misc;
using GenericLauncher.Minecraft;
using GenericLauncher.Minecraft.Json;
using GenericLauncher.Minecraft.ModLoaders;
using GenericLauncher.Minecraft.ModLoaders.Forge;
using GenericLauncher.Minecraft.ModLoaders.Forge.Json;
using GenericLauncher.Minecraft.ModLoaders.NeoForge;
using GenericLauncher.Minecraft.ModLoaders.NeoForge.Json;
using GenericLauncher.Screens.NewInstanceDialog;
using JetBrains.Annotations;
using LavaLauncher;
using Xunit;

namespace GenericLauncher.Tests.Minecraft;

[TestSubject(typeof(IModLoaderService))]
public class ModLoaderServicesTest
{
    [Fact]
    public async Task Forge_GetLoaderVersionsAsync_UsesPromotionsForOrdering()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        using var httpClient = CreateHttpClient(new Dictionary<string, HttpContent>
        {
            ["https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"] = StringContent("""
                <metadata><versioning><versions>
                    <version>1.21.10-60.1.7</version>
                    <version>1.21.10-60.1.8</version>
                    <version>1.21.9-59.0.1</version>
                </versions></versioning></metadata>
                """),
            ["https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json"] = StringContent("""
                {"promos":{"1.21.10-recommended":"60.1.8","1.21.10-latest":"60.1.7"}}
                """),
        });
        var service = new ForgeModLoaderService(
            Path.Combine(root, "forge"),
            Path.Combine(root, "forge", "libraries"),
            httpClient,
            new FileDownloader(httpClient));

        var versions = await service.GetLoaderVersionsAsync("1.21.10", true, cancellationToken);

        Assert.Equal(["60.1.8", "60.1.7"], versions.Select(v => v.VersionId));
        Assert.Equal(["RECOMMENDED", "LATEST"], versions.Select(v => v.Channel));
    }

    [Fact]
    public async Task NeoForge_GetLoaderVersionsAsync_FiltersByMinecraftVersionPrefix()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        using var httpClient = CreateHttpClient(new Dictionary<string, HttpContent>
        {
            ["https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml"] = StringContent("""
                <metadata><versioning><versions>
                    <version>21.3.90</version>
                    <version>21.4.120</version>
                    <version>21.4.150</version>
                </versions></versioning></metadata>
                """),
        });
        var service = new NeoForgeModLoaderService(
            Path.Combine(root, "neoforge"),
            Path.Combine(root, "neoforge", "libraries"),
            httpClient,
            new FileDownloader(httpClient));

        var versions = await service.GetLoaderVersionsAsync("1.21.4", true, cancellationToken);

        Assert.Equal(["21.4.150", "21.4.120"], versions.Select(v => v.VersionId));
    }

    [Fact]
    public async Task Forge_ResolveAsync_ParsesInstallerProfileAndMarksGeneratedLibraries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var universalBytes = Encoding.UTF8.GetBytes("forge-universal");
        var universalSha1 = ComputeSha1(universalBytes);
        var installerBytes = CreateInstallerJar(
            installProfileJson: """
                {
                  "spec": 1,
                  "profile": "forge",
                  "version": "1.21.10-forge-60.1.8",
                  "minecraft": "1.21.10",
                  "json": "/version.json",
                  "data": {},
                  "processors": [],
                  "libraries": []
                }
                """,
            versionJson: $$"""
                {
                  "id": "1.21.10-forge-60.1.8",
                  "inheritsFrom": "1.21.10",
                  "mainClass": "net.minecraftforge.bootstrap.ForgeBootstrap",
                  "arguments": {
                    "game": ["--launchTarget", "forge_client"],
                    "jvm": [
                      "-DlibraryDirectory=${library_directory}",
                      "-Dseparator=${classpath_separator}",
                      "-Dversion=${version_name}"
                    ]
                  },
                  "libraries": [
                    {
                      "name": "net.minecraftforge:forge:1.21.10-60.1.8:universal",
                      "downloads": {
                        "artifact": {
                          "path": "net/minecraftforge/forge/1.21.10-60.1.8/forge-1.21.10-60.1.8-universal.jar",
                          "url": "https://example.test/forge-universal.jar",
                          "sha1": "{{universalSha1}}",
                          "size": {{universalBytes.Length}}
                        }
                      }
                    },
                    {
                      "name": "net.minecraftforge:forge:1.21.10-60.1.8:client",
                      "downloads": {
                        "artifact": {
                          "path": "net/minecraftforge/forge/1.21.10-60.1.8/forge-1.21.10-60.1.8-client.jar",
                          "url": "",
                          "sha1": "0000000000000000000000000000000000000000",
                          "size": 1
                        }
                      }
                    }
                  ]
                }
                """);

        using var httpClient = CreateHttpClient(new Dictionary<string, HttpContent>
        {
            ["https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"] = StringContent("""
                <metadata><versioning><versions><version>1.21.10-60.1.8</version></versions></versioning></metadata>
                """),
            ["https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json"] = StringContent("""
                {"promos":{"1.21.10-recommended":"60.1.8"}}
                """),
            ["https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.10-60.1.8/forge-1.21.10-60.1.8-installer.jar"] = BinaryContent(installerBytes),
            ["https://example.test/forge-universal.jar"] = BinaryContent(universalBytes),
        });

        var service = new ForgeModLoaderService(
            Path.Combine(root, "forge"),
            Path.Combine(root, "forge", "libraries"),
            httpClient,
            new FileDownloader(httpClient));

        var resolved = await service.ResolveAsync("1.21.10", null, CreatePlatform("windows", "x64"), cancellationToken);

        Assert.Equal("1.21.10-forge-60.1.8", resolved.LaunchVersionId);
        Assert.Equal("60.1.8", resolved.LoaderVersionId);
        Assert.Contains(resolved.ExtraJvmArguments, arg => arg.Contains(Path.Combine(root, "forge", "libraries"), StringComparison.Ordinal));
        Assert.Contains(resolved.ExtraJvmArguments, arg => arg.Contains(Path.PathSeparator));
        Assert.Contains(resolved.ExtraJvmArguments, arg => arg.Contains("1.21.10-forge-60.1.8", StringComparison.Ordinal));
        Assert.Contains(resolved.Libraries, lib => lib.Name.EndsWith(":client", StringComparison.Ordinal) && lib.Url is null);

        await service.InstallAsync(
            resolved,
            new ModLoaderInstallContext(CreatePlatform("windows", "x64"), "/bin/true", "/tmp/client.jar"),
            cancellationToken: cancellationToken);

        var downloadedPath = Path.Combine(
            root,
            "forge",
            "libraries",
            "net",
            "minecraftforge",
            "forge",
            "1.21.10-60.1.8",
            "forge-1.21.10-60.1.8-universal.jar");
        Assert.True(File.Exists(downloadedPath));
    }

    [Fact]
    public async Task NeoForge_ResolveAsync_ParsesInstallerProfileAndMarksGeneratedLibraries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var universalBytes = Encoding.UTF8.GetBytes("neoforge-universal");
        var universalSha1 = ComputeSha1(universalBytes);
        var installerBytes = CreateInstallerJar(
            installProfileJson: """
                {
                  "spec": 1,
                  "profile": "neoforge",
                  "version": "neoforge-21.4.150",
                  "minecraft": "1.21.4",
                  "json": "/version.json",
                  "data": {},
                  "processors": [],
                  "libraries": []
                }
                """,
            versionJson: $$"""
                {
                  "id": "neoforge-21.4.150",
                  "inheritsFrom": "1.21.4",
                  "mainClass": "net.neoforged.bootstrap.NeoForgeBootstrap",
                  "arguments": {
                    "game": ["--launchTarget", "neoforge_client"],
                    "jvm": [
                      "-DlibraryDirectory=${library_directory}",
                      "-Dseparator=${classpath_separator}",
                      "-Dversion=${version_name}"
                    ]
                  },
                  "libraries": [
                    {
                      "name": "net.neoforged:neoforge:21.4.150:universal",
                      "downloads": {
                        "artifact": {
                          "path": "net/neoforged/neoforge/21.4.150/neoforge-21.4.150-universal.jar",
                          "url": "https://example.test/neoforge-universal.jar",
                          "sha1": "{{universalSha1}}",
                          "size": {{universalBytes.Length}}
                        }
                      }
                    },
                    {
                      "name": "net.neoforged:neoforge:21.4.150:client",
                      "downloads": {
                        "artifact": {
                          "path": "net/neoforged/neoforge/21.4.150/neoforge-21.4.150-client.jar",
                          "url": "",
                          "sha1": "0000000000000000000000000000000000000000",
                          "size": 1
                        }
                      }
                    }
                  ]
                }
                """);

        using var httpClient = CreateHttpClient(new Dictionary<string, HttpContent>
        {
            ["https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml"] = StringContent("""
                <metadata><versioning><versions><version>21.4.150</version></versions></versioning></metadata>
                """),
            ["https://maven.neoforged.net/releases/net/neoforged/neoforge/21.4.150/neoforge-21.4.150-installer.jar"] = BinaryContent(installerBytes),
            ["https://example.test/neoforge-universal.jar"] = BinaryContent(universalBytes),
        });

        var service = new NeoForgeModLoaderService(
            Path.Combine(root, "neoforge"),
            Path.Combine(root, "neoforge", "libraries"),
            httpClient,
            new FileDownloader(httpClient));

        var resolved = await service.ResolveAsync("1.21.4", null, CreatePlatform("windows", "x64"), cancellationToken);

        Assert.Equal("neoforge-21.4.150", resolved.LaunchVersionId);
        Assert.Equal("21.4.150", resolved.LoaderVersionId);
        Assert.Contains(resolved.ExtraJvmArguments, arg => arg.Contains(Path.Combine(root, "neoforge", "libraries"), StringComparison.Ordinal));
        Assert.Contains(resolved.ExtraJvmArguments, arg => arg.Contains(Path.PathSeparator));
        Assert.Contains(resolved.ExtraJvmArguments, arg => arg.Contains("neoforge-21.4.150", StringComparison.Ordinal));
        Assert.Contains(resolved.Libraries, lib => lib.Name.EndsWith(":client", StringComparison.Ordinal) && lib.Url is null);

        await service.InstallAsync(
            resolved,
            new ModLoaderInstallContext(CreatePlatform("windows", "x64"), "/bin/true", "/tmp/client.jar"),
            cancellationToken: cancellationToken);

        var downloadedPath = Path.Combine(
            root,
            "neoforge",
            "libraries",
            "net",
            "neoforged",
            "neoforge",
            "21.4.150",
            "neoforge-21.4.150-universal.jar");
        Assert.True(File.Exists(downloadedPath));
    }

    [Fact]
    public void Forge_BuildClientProcessorPlans_SelectsCurrentClientPipeline()
    {
        var installProfile = new ForgeInstallProfile(
            1,
            "forge",
            "1.21.11-forge-61.1.4",
            "net.minecraftforge:forge:1.21.11-61.1.4:shim",
            "1.21.11",
            null,
            null,
            null,
            [
                new ForgeInstallProcessor(["server"], "net.minecraftforge:installertools:1.4.3", [], ["--task", "EXTRACT_FILES"], null),
                new ForgeInstallProcessor(null, "net.minecraftforge:installertools:1.4.3", [], ["--task", "DOWNLOAD_MOJMAPS"], null),
                new ForgeInstallProcessor(["server"], "net.minecraftforge:ForgeAutoRenamingTool:1.0.6", [], ["--input", "{MC_UNPACKED}"], null),
                new ForgeInstallProcessor(["client"], "net.minecraftforge:ForgeAutoRenamingTool:1.0.6", [], ["--input", "{MINECRAFT_JAR}"], null),
                new ForgeInstallProcessor(null, "net.minecraftforge:binarypatcher:1.2.0", [], ["--clean", "{MC_OFF}"], null),
            ],
            []);

        var plans = ForgeModLoaderService.BuildClientProcessorPlans(installProfile);

        Assert.Equal(
            [
                ForgeModLoaderService.ForgeClientProcessorKind.DownloadMojmaps,
                ForgeModLoaderService.ForgeClientProcessorKind.ClientAutoRename,
                ForgeModLoaderService.ForgeClientProcessorKind.BinaryPatch,
            ],
            plans.Select(p => p.Kind));
    }

    [Fact]
    public void Forge_ParseClientProcessorPlan_ThrowsOnUnsupportedClientTask()
    {
        var processor = new ForgeInstallProcessor(
            null,
            "net.minecraftforge:installertools:1.4.3",
            [],
            ["--task", "EXTRACT_FILES"],
            null);

        var ex = Assert.Throws<InvalidOperationException>(() => ForgeModLoaderService.ParseClientProcessorPlan(processor));

        Assert.Contains("Unsupported Forge client installertools task", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NeoForge_BuildClientProcessorPlans_SelectsCurrentClientPipeline()
    {
        var installProfile = new NeoForgeInstallProfile(
            1,
            "NeoForge",
            "neoforge-21.11.38-beta",
            "1.21.11",
            null,
            null,
            null,
            null,
            [
                new NeoForgeInstallProcessor(["server"], "net.neoforged.installertools:installertools:4.0.6:fatjar", [], ["--task", "EXTRACT_FILES"], null),
                new NeoForgeInstallProcessor(null, "net.neoforged.installertools:installertools:4.0.6:fatjar", [], ["--task", "DOWNLOAD_MOJMAPS"], null),
                new NeoForgeInstallProcessor(null, "net.neoforged.installertools:installertools:4.0.6:fatjar", [], ["--task", "PROCESS_MINECRAFT_JAR"], null),
            ],
            []);

        var plans = NeoForgeModLoaderService.BuildClientProcessorPlans(installProfile);

        Assert.Equal(
            [
                NeoForgeModLoaderService.NeoForgeClientProcessorKind.DownloadMojmaps,
                NeoForgeModLoaderService.NeoForgeClientProcessorKind.ProcessMinecraftJar,
            ],
            plans.Select(p => p.Kind));
    }

    [Fact]
    public void NeoForge_ParseClientProcessorPlan_ThrowsOnUnsupportedClientTask()
    {
        var processor = new NeoForgeInstallProcessor(
            null,
            "net.neoforged.installertools:installertools:4.0.6:fatjar",
            [],
            ["--task", "EXTRACT_FILES"],
            null);

        var ex = Assert.Throws<InvalidOperationException>(() => NeoForgeModLoaderService.ParseClientProcessorPlan(processor));

        Assert.Contains("Unsupported NeoForge client installertools task", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Forge_BuildProcessorCommand_ResolvesBinaryPatchArgumentsAndExtractsInstallerPayload()
    {
        var root = CreateTempRoot();
        var loaderRoot = Path.Combine(root, "forge");
        var librariesRoot = Path.Combine(loaderRoot, "libraries");
        Directory.CreateDirectory(librariesRoot);

        using var httpClient = CreateHttpClient([]);
        var service = new ForgeModLoaderService(loaderRoot, librariesRoot, httpClient, new FileDownloader(httpClient));

        var processorCoordinate = "net.minecraftforge:binarypatcher:1.2.0";
        var processorJarPath = CreateProcessorJar(librariesRoot, processorCoordinate, "example.Main");

        var versionFolder = Path.Combine(loaderRoot, "versions", "1.21.11-forge-61.1.4");
        Directory.CreateDirectory(versionFolder);
        var installerPath = Path.Combine(versionFolder, "installer.jar");
        File.WriteAllBytes(
            installerPath,
            CreateInstallerJar("{}", "{}", new Dictionary<string, byte[]>
            {
                ["data/client.lzma"] = Encoding.UTF8.GetBytes("patch-data"),
            }));

        var installProfile = new ForgeInstallProfile(
            1,
            "forge",
            "1.21.11-forge-61.1.4",
            "net.minecraftforge:forge:1.21.11-61.1.4:shim",
            "1.21.11",
            null,
            null,
            new Dictionary<string, ForgeInstallDataEntry>
            {
                ["MC_OFF"] = new("[net.minecraft:client:1.21.11:official]", null),
                ["PATCHED"] = new("[net.minecraftforge:forge:1.21.11-61.1.4:client]", null),
                ["BINPATCH"] = new("/data/client.lzma", null),
            },
            [],
            []);

        var plan = ForgeModLoaderService.ParseClientProcessorPlan(
            new ForgeInstallProcessor(
                null,
                processorCoordinate,
                [],
                ["--clean", "{MC_OFF}", "--output", "{PATCHED}", "--apply", "{BINPATCH}", "--data", "--unpatched"],
                null));

        var profilePath = Path.Combine(versionFolder, "profile.json");
        File.WriteAllText(profilePath, "{}");
        var resolved = new ResolvedModLoaderVersion(
            "Forge",
            "1.21.11",
            "1.21.11-forge-61.1.4",
            "61.1.4",
            profilePath,
            Path.Combine(versionFolder, "install_profile.json"),
            installerPath,
            null,
            [],
            [],
            []);

        var command = service.BuildProcessorCommand(
            plan,
            installProfile,
            resolved,
            new ModLoaderInstallContext(CreatePlatform("windows", "x64"), "/bin/true", "/tmp/client.jar"),
            versionFolder);

        var mcOffPath = Path.Combine(librariesRoot, MavenCoordinate.ToRelativePath("net.minecraft:client:1.21.11:official").Replace('/', Path.DirectorySeparatorChar));
        var patchedPath = Path.Combine(librariesRoot, MavenCoordinate.ToRelativePath("net.minecraftforge:forge:1.21.11-61.1.4:client").Replace('/', Path.DirectorySeparatorChar));
        var extractedPatchPath = Path.Combine(versionFolder, "data", "client.lzma");

        Assert.Equal(processorJarPath, command.JarPath);
        Assert.Equal("example.Main", command.MainClass);
        Assert.Contains(mcOffPath, command.Arguments);
        Assert.Contains(patchedPath, command.Arguments);
        Assert.Contains(extractedPatchPath, command.Arguments);
        Assert.True(File.Exists(extractedPatchPath));
    }

    [Fact]
    public void NeoForge_BuildProcessorCommand_ResolvesProcessMinecraftJarArgumentsAndExtractsInstallerPayload()
    {
        var root = CreateTempRoot();
        var loaderRoot = Path.Combine(root, "neoforge");
        var librariesRoot = Path.Combine(loaderRoot, "libraries");
        Directory.CreateDirectory(librariesRoot);

        using var httpClient = CreateHttpClient([]);
        var service = new NeoForgeModLoaderService(loaderRoot, librariesRoot, httpClient, new FileDownloader(httpClient));

        var processorCoordinate = "net.neoforged.installertools:installertools:4.0.6:fatjar";
        var processorJarPath = CreateProcessorJar(librariesRoot, processorCoordinate, "example.Main");

        var versionFolder = Path.Combine(loaderRoot, "versions", "neoforge-21.11.38-beta");
        Directory.CreateDirectory(versionFolder);
        var installerPath = Path.Combine(versionFolder, "installer.jar");
        File.WriteAllBytes(
            installerPath,
            CreateInstallerJar("{}", "{}", new Dictionary<string, byte[]>
            {
                ["data/client.lzma"] = Encoding.UTF8.GetBytes("patch-data"),
            }));

        var installProfile = new NeoForgeInstallProfile(
            1,
            "NeoForge",
            "neoforge-21.11.38-beta",
            "1.21.11",
            null,
            null,
            null,
            new Dictionary<string, NeoForgeInstallDataEntry>
            {
                ["MOJMAPS"] = new("[net.minecraft:client:1.21.11:mappings@txt]", null),
                ["PATCHED"] = new("[net.neoforged:minecraft-client-patched:21.11.38-beta]", null),
                ["BINPATCH"] = new("/data/client.lzma", null),
            },
            [],
            []);

        var plan = NeoForgeModLoaderService.ParseClientProcessorPlan(
            new NeoForgeInstallProcessor(
                null,
                processorCoordinate,
                [processorCoordinate],
                [
                    "--task", "PROCESS_MINECRAFT_JAR",
                    "--input", "{MINECRAFT_JAR}",
                    "--input-mappings", "{MOJMAPS}",
                    "--output", "{PATCHED}",
                    "--extract-libraries-to", "{ROOT}/libraries/",
                    "--neoform-data", "[net.neoforged:neoform:1.21.11-20251209.172050:mappings@tsrg.lzma]",
                    "--apply-patches", "{BINPATCH}",
                ],
                null));

        var profilePath = Path.Combine(versionFolder, "profile.json");
        File.WriteAllText(profilePath, "{}");
        var resolved = new ResolvedModLoaderVersion(
            "NeoForge",
            "1.21.11",
            "neoforge-21.11.38-beta",
            "21.11.38-beta",
            profilePath,
            Path.Combine(versionFolder, "install_profile.json"),
            installerPath,
            null,
            [],
            [],
            []);

        var command = service.BuildProcessorCommand(
            plan,
            installProfile,
            resolved,
            new ModLoaderInstallContext(CreatePlatform("windows", "x64"), "/bin/true", "/tmp/client.jar"),
            versionFolder);

        var mojmapsPath = Path.Combine(librariesRoot, MavenCoordinate.ToRelativePath("net.minecraft:client:1.21.11:mappings@txt").Replace('/', Path.DirectorySeparatorChar));
        var patchedPath = Path.Combine(librariesRoot, MavenCoordinate.ToRelativePath("net.neoforged:minecraft-client-patched:21.11.38-beta").Replace('/', Path.DirectorySeparatorChar));
        var neoformPath = Path.Combine(librariesRoot, MavenCoordinate.ToRelativePath("net.neoforged:neoform:1.21.11-20251209.172050:mappings@tsrg.lzma").Replace('/', Path.DirectorySeparatorChar));
        var extractedPatchPath = Path.Combine(versionFolder, "data", "client.lzma");

        Assert.Equal(processorJarPath, command.JarPath);
        Assert.Equal("example.Main", command.MainClass);
        Assert.Contains("/tmp/client.jar", command.Arguments);
        Assert.Contains(mojmapsPath, command.Arguments);
        Assert.Contains(patchedPath, command.Arguments);
        Assert.Contains(neoformPath, command.Arguments);
        Assert.Contains(extractedPatchPath, command.Arguments);
        Assert.True(File.Exists(extractedPatchPath));
    }

    [Fact]
    public async Task Forge_InstallAsync_SkipsProcessorWhenOutputsAreCurrent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var loaderRoot = Path.Combine(root, "forge");
        var librariesRoot = Path.Combine(loaderRoot, "libraries");
        var versionFolder = Path.Combine(loaderRoot, "versions", "1.21.11-forge-61.1.4");
        Directory.CreateDirectory(librariesRoot);
        Directory.CreateDirectory(versionFolder);

        var patchedOutputPath = Path.Combine(librariesRoot, MavenCoordinate.ToRelativePath("net.minecraftforge:forge:1.21.11-61.1.4:client").Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(patchedOutputPath)!);
        var patchedBytes = Encoding.UTF8.GetBytes("already-current");
        await File.WriteAllBytesAsync(patchedOutputPath, patchedBytes, cancellationToken);
        var patchedSha1 = ComputeSha1(patchedBytes);

        var installProfilePath = Path.Combine(versionFolder, "install_profile.json");
        await File.WriteAllTextAsync(
            installProfilePath,
            $$"""
            {
              "spec": 1,
              "profile": "forge",
              "version": "1.21.11-forge-61.1.4",
              "path": "net.minecraftforge:forge:1.21.11-61.1.4:shim",
              "minecraft": "1.21.11",
              "data": {
                "MC_OFF": { "client": "[net.minecraft:client:1.21.11:official]" },
                "PATCHED": { "client": "[net.minecraftforge:forge:1.21.11-61.1.4:client]" },
                "PATCHED_SHA": { "client": "'{{patchedSha1}}'" }
              },
              "processors": [
                {
                  "jar": "net.minecraftforge:binarypatcher:1.2.0",
                  "args": ["--clean", "{MC_OFF}", "--output", "{PATCHED}"],
                  "outputs": {
                    "{PATCHED}": "{PATCHED_SHA}"
                  }
                }
              ],
              "libraries": []
            }
            """,
            cancellationToken);

        var profilePath = Path.Combine(versionFolder, "profile.json");
        await File.WriteAllTextAsync(
            profilePath,
            """
            {
              "id": "1.21.11-forge-61.1.4",
              "inheritsFrom": "1.21.11",
              "arguments": { "game": [], "jvm": [] },
              "libraries": []
            }
            """,
            cancellationToken);

        var installerPath = Path.Combine(versionFolder, "installer.jar");
        await File.WriteAllBytesAsync(installerPath, CreateInstallerJar("{}", "{}"), cancellationToken);

        var runLogPath = Path.Combine(root, "forge-run.log");
        var captureExecutablePath = CreateArgumentCaptureExecutable(root, runLogPath);

        using var httpClient = CreateHttpClient([]);
        var service = new ForgeModLoaderService(loaderRoot, librariesRoot, httpClient, new FileDownloader(httpClient));
        var resolved = new ResolvedModLoaderVersion(
            "Forge",
            "1.21.11",
            "1.21.11-forge-61.1.4",
            "61.1.4",
            profilePath,
            installProfilePath,
            installerPath,
            null,
            [],
            [],
            []);

        await service.InstallAsync(
            resolved,
            new ModLoaderInstallContext(CreatePlatform("windows", "x64"), captureExecutablePath, "/tmp/client.jar"),
            cancellationToken: cancellationToken);

        Assert.False(File.Exists(runLogPath));
    }

    [Fact]
    public async Task NeoForge_InstallAsync_ExecutesSupportedProcessor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        var loaderRoot = Path.Combine(root, "neoforge");
        var librariesRoot = Path.Combine(loaderRoot, "libraries");
        var versionFolder = Path.Combine(loaderRoot, "versions", "neoforge-21.11.38-beta");
        Directory.CreateDirectory(librariesRoot);
        Directory.CreateDirectory(versionFolder);

        CreateProcessorJar(librariesRoot, "net.neoforged.installertools:installertools:4.0.6:fatjar", "example.Main");

        var installProfilePath = Path.Combine(versionFolder, "install_profile.json");
        await File.WriteAllTextAsync(
            installProfilePath,
            """
            {
              "spec": 1,
              "profile": "NeoForge",
              "version": "neoforge-21.11.38-beta",
              "minecraft": "1.21.11",
              "data": {
                "MOJMAPS": { "client": "[net.minecraft:client:1.21.11:mappings@txt]" },
                "PATCHED": { "client": "[net.neoforged:minecraft-client-patched:21.11.38-beta]" },
                "BINPATCH": { "client": "/data/client.lzma" }
              },
              "processors": [
                {
                  "jar": "net.neoforged.installertools:installertools:4.0.6:fatjar",
                  "classpath": ["net.neoforged.installertools:installertools:4.0.6:fatjar"],
                  "args": [
                    "--task", "PROCESS_MINECRAFT_JAR",
                    "--input", "{MINECRAFT_JAR}",
                    "--input-mappings", "{MOJMAPS}",
                    "--output", "{PATCHED}",
                    "--extract-libraries-to", "{ROOT}/libraries/",
                    "--neoform-data", "[net.neoforged:neoform:1.21.11-20251209.172050:mappings@tsrg.lzma]",
                    "--apply-patches", "{BINPATCH}"
                  ]
                }
              ],
              "libraries": []
            }
            """,
            cancellationToken);

        var profilePath = Path.Combine(versionFolder, "profile.json");
        await File.WriteAllTextAsync(
            profilePath,
            """
            {
              "id": "neoforge-21.11.38-beta",
              "inheritsFrom": "1.21.11",
              "arguments": { "game": [], "jvm": [] },
              "libraries": []
            }
            """,
            cancellationToken);

        var installerPath = Path.Combine(versionFolder, "installer.jar");
        await File.WriteAllBytesAsync(
            installerPath,
            CreateInstallerJar("{}", "{}", new Dictionary<string, byte[]>
            {
                ["data/client.lzma"] = Encoding.UTF8.GetBytes("patch-data"),
            }),
            cancellationToken);

        var runLogPath = Path.Combine(root, "neoforge-run.log");
        var captureExecutablePath = CreateArgumentCaptureExecutable(root, runLogPath);

        using var httpClient = CreateHttpClient([]);
        var service = new NeoForgeModLoaderService(loaderRoot, librariesRoot, httpClient, new FileDownloader(httpClient));
        var resolved = new ResolvedModLoaderVersion(
            "NeoForge",
            "1.21.11",
            "neoforge-21.11.38-beta",
            "21.11.38-beta",
            profilePath,
            installProfilePath,
            installerPath,
            null,
            [],
            [],
            []);

        await service.InstallAsync(
            resolved,
            new ModLoaderInstallContext(CreatePlatform("windows", "x64"), captureExecutablePath, "/tmp/client.jar"),
            cancellationToken: cancellationToken);

        var runLog = await File.ReadAllTextAsync(runLogPath, cancellationToken);
        var neoformPath = Path.Combine(librariesRoot, MavenCoordinate.ToRelativePath("net.neoforged:neoform:1.21.11-20251209.172050:mappings@tsrg.lzma").Replace('/', Path.DirectorySeparatorChar));
        Assert.Contains("PROCESS_MINECRAFT_JAR", runLog, StringComparison.Ordinal);
        Assert.Contains(neoformPath, runLog, StringComparison.Ordinal);
        Assert.DoesNotContain("[net.neoforged:neoform:1.21.11-20251209.172050:mappings@tsrg.lzma]", runLog, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(versionFolder, "data", "client.lzma")));
    }

    [Fact]
    public async Task NewInstanceDialogViewModel_ReloadsCompatibleVersionsWhenMinecraftVersionChanges()
    {
        var version1210 = new VersionInfo("1.21.10", "release", "https://example.test/1.21.10", default, default, "", 0);
        var version1214 = new VersionInfo("1.21.4", "release", "https://example.test/1.21.4", default, default, "", 0);
        var fakeLauncher = new FakeLauncherFacade
        {
            AvailableVersionsValue = [version1210, version1214],
            AvailableModLoadersValue = [MinecraftInstanceModLoader.Forge],
            LoaderVersions = (loader, minecraftVersionId) => Task.FromResult(
                minecraftVersionId switch
                {
                    "1.21.10" => ImmutableList.Create(new ModLoaderVersionInfo("60.1.8", "RECOMMENDED")),
                    "1.21.4" => ImmutableList.Create(new ModLoaderVersionInfo("21.4.150", "LATEST")),
                    _ => ImmutableList<ModLoaderVersionInfo>.Empty,
                }),
        };

        var vm = new NewInstanceDialogViewModel(fakeLauncher, null);
        await DrainUiAsync();

        Assert.Equal("60.1.8", vm.SelectedModLoaderVersion?.VersionId);

        vm.SelectedMinecraftVersion = version1214;
        await DrainUiAsync();

        Assert.Equal("21.4.150", vm.SelectedModLoaderVersion?.VersionId);
        Assert.True(vm.CanInstall);
    }

    [Fact]
    public async Task NewInstanceDialogViewModel_DisablesInstallWhenNoCompatibleLoaderVersionsExist()
    {
        var version = new VersionInfo("1.21.10", "release", "https://example.test/1.21.10", default, default, "", 0);
        var fakeLauncher = new FakeLauncherFacade
        {
            AvailableVersionsValue = [version],
            AvailableModLoadersValue = [MinecraftInstanceModLoader.NeoForge],
            LoaderVersions = (_, _) => Task.FromResult(ImmutableList<ModLoaderVersionInfo>.Empty),
        };

        var vm = new NewInstanceDialogViewModel(fakeLauncher, null);
        await DrainUiAsync();

        Assert.False(vm.CanInstall);
        Assert.Null(vm.SelectedModLoaderVersion);
        Assert.Contains("No compatible loader versions", vm.ModLoaderVersionStatusText, StringComparison.Ordinal);
    }

    private static LauncherPlatform CreatePlatform(string os, string architecture, Version? version = null) =>
        new(os,
            architecture,
            version ?? new Version(14, 0),
            AppConfig.MacBundleIdentifier,
            Path.Combine(Path.GetTempPath(), "lavalancher-tests", os, architecture),
            Path.Combine(Path.GetTempPath(), "lavalancher-tests", os, architecture));

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lavalancher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static HttpClient CreateHttpClient(Dictionary<string, HttpContent> responses) =>
        new(new FakeHttpMessageHandler(responses));

    private static StringContent StringContent(string value) =>
        new(value, Encoding.UTF8, "application/json");

    private static ByteArrayContent BinaryContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/java-archive");
        return content;
    }

    private static string ComputeSha1(byte[] bytes) =>
        Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    private static byte[] CreateInstallerJar(
        string installProfileJson,
        string versionJson,
        Dictionary<string, byte[]>? extraEntries = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var installEntry = archive.CreateEntry("install_profile.json");
            using (var writer = new StreamWriter(installEntry.Open(), Encoding.UTF8))
            {
                writer.Write(installProfileJson);
            }

            var versionEntry = archive.CreateEntry("version.json");
            using (var writer = new StreamWriter(versionEntry.Open(), Encoding.UTF8))
            {
                writer.Write(versionJson);
            }

            if (extraEntries is not null)
            {
                foreach (var extraEntry in extraEntries)
                {
                    var entry = archive.CreateEntry(extraEntry.Key);
                    using var entryStream = entry.Open();
                    entryStream.Write(extraEntry.Value);
                }
            }
        }

        return stream.ToArray();
    }

    private static string CreateProcessorJar(string librariesRoot, string coordinate, string mainClass)
    {
        var relativePath = MavenCoordinate.ToRelativePath(coordinate).Replace('/', Path.DirectorySeparatorChar);
        var jarPath = Path.Combine(librariesRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);

        using var fileStream = File.Create(jarPath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, false);
        var manifestEntry = archive.CreateEntry("META-INF/MANIFEST.MF");
        using (var writer = new StreamWriter(manifestEntry.Open(), Encoding.UTF8))
        {
            writer.Write($"Manifest-Version: 1.0\nMain-Class: {mainClass}\n");
        }

        return jarPath;
    }

    private static string CreateArgumentCaptureExecutable(string root, string outputPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(root, "capture.cmd");
            File.WriteAllText(
                scriptPath,
                $"""
                 @echo off
                 :loop
                 if "%~1"=="" goto end
                 echo %~1>>"{outputPath}"
                 shift
                 goto loop
                 :end
                 """);
            return scriptPath;
        }

        var unixScriptPath = Path.Combine(root, "capture.sh");
        File.WriteAllText(
            unixScriptPath,
            $"""
             #!/bin/sh
             for arg in "$@"; do
               printf '%s\n' "$arg" >> "{outputPath}"
             done
             """);
        File.SetUnixFileMode(
            unixScriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return unixScriptPath;
    }

    private static Task DrainUiAsync()
    {
        Dispatcher.UIThread.RunJobs();
        return Task.CompletedTask;
    }

    private sealed class FakeHttpMessageHandler(Dictionary<string, HttpContent> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (!responses.TryGetValue(url, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Clone(content),
                RequestMessage = request,
            });
        }

        private static HttpContent Clone(HttpContent content)
        {
            var bytes = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var clone = new ByteArrayContent(bytes);
            if (content.Headers.ContentType is not null)
            {
                clone.Headers.ContentType = new(content.Headers.ContentType.MediaType!);
            }

            return clone;
        }
    }

    private sealed class FakeLauncherFacade : IMinecraftLauncherFacade
    {
        public ImmutableList<VersionInfo> AvailableVersionsValue { get; init; } = [];
        public ImmutableList<MinecraftInstanceModLoader> AvailableModLoadersValue { get; init; } = [];
        public Func<MinecraftInstanceModLoader, string, Task<ImmutableList<ModLoaderVersionInfo>>> LoaderVersions { get; init; } =
            (_, _) => Task.FromResult(ImmutableList<ModLoaderVersionInfo>.Empty);

        public ImmutableList<VersionInfo> AvailableVersions => AvailableVersionsValue;
        public ImmutableList<MinecraftInstanceModLoader> AvailableModLoaders => AvailableModLoadersValue;
        public event EventHandler? AvailableVersionsChanged;

        public Task<ImmutableList<ModLoaderVersionInfo>> GetLoaderVersionsAsync(
            MinecraftInstanceModLoader modLoader,
            string minecraftVersionId,
            bool reload) =>
            LoaderVersions(modLoader, minecraftVersionId);

        public Task CreateInstance(
            VersionInfo version,
            string name,
            MinecraftInstanceModLoader modLoader,
            string? preferredModLoaderVersion,
            IProgress<ThreadSafeInstallProgressReporter.InstallProgress> progress) =>
            Task.CompletedTask;

        public void RaiseVersionsChanged() => AvailableVersionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
