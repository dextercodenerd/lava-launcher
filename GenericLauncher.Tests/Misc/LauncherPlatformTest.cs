using System;
using System.IO;
using System.Net.Http;
using GenericLauncher.Database;
using GenericLauncher.Http;
using GenericLauncher.Java;
using GenericLauncher.Minecraft;
using GenericLauncher.Misc;
using JetBrains.Annotations;
using LavaLauncher;
using Xunit;

namespace GenericLauncher.Tests.Misc;

[TestSubject(typeof(LauncherPlatform))]
public class LauncherPlatformTest
{
    [Fact]
    public void CreateCurrent_OnMac_UsesApplicationDataAndBundleIdentifier()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var platform = LauncherPlatform.CreateCurrent();
        var applicationDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Equal("osx", platform.CurrentOs);
        Assert.Equal(AppConfig.MacBundleIdentifier, platform.AppIdentifier);
        Assert.Equal(Path.Combine(applicationDataRoot, AppConfig.MacBundleIdentifier), platform.AppDataPath);
    }

    [Fact]
    public void LauncherManagedRoots_AreDerivedFromAppDataPath()
    {
        var appDataPath = Path.Combine(Path.GetTempPath(), "lavalancher-tests", Guid.NewGuid().ToString("N"));
        var platform = new LauncherPlatform(
            "osx",
            "arm64",
            new Version(14, 0),
            AppConfig.MacBundleIdentifier,
            appDataPath);
        using var httpClient = new HttpClient();
        var downloader = new FileDownloader(httpClient);
        var repository = new LauncherRepository(platform);
        var javaManager = new JavaVersionManager(platform, httpClient, downloader);
        var minecraftManager = new MinecraftVersionManager(platform, httpClient, downloader, null);

        Assert.StartsWith(appDataPath, repository.DatabasePath, StringComparison.Ordinal);
        Assert.StartsWith(appDataPath, javaManager.JavaInstallationsDirectory, StringComparison.Ordinal);
        Assert.StartsWith(appDataPath, minecraftManager.MinecraftVersionsFolderPath, StringComparison.Ordinal);
        Assert.StartsWith(appDataPath, minecraftManager.SharedAssetsFolderPath, StringComparison.Ordinal);
        Assert.StartsWith(appDataPath, minecraftManager.SharedLibrariesFolderPath, StringComparison.Ordinal);
        Assert.StartsWith(appDataPath, MinecraftLauncher.GetInstancesFolder(platform), StringComparison.Ordinal);
    }
}
