using System;
using System.IO;
using GenericLauncher.Misc;
using JetBrains.Annotations;
using LavaLauncher;
using Xunit;

namespace GenericLauncher.Tests.Misc;

[TestSubject(typeof(LauncherPlatform))]
public class LauncherPlatformTest
{
    [Fact]
    public void ResolveStoragePaths_OnWindows_UsesConfiguredFolderName()
    {
        var localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = LauncherPlatform.ResolveStoragePaths("windows");

        Assert.Equal(AppConfig.WindowsFolderName, paths.appIdentifier);
        Assert.Equal(Path.Combine(localAppDataRoot, AppConfig.WindowsFolderName), paths.appDataPath);
        Assert.Equal(paths.appDataPath, paths.configPath);
    }

    [Fact]
    public void ResolveStoragePaths_OnLinux_UsesConfiguredFolderName()
    {
        var localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var configRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var paths = LauncherPlatform.ResolveStoragePaths("linux");

        Assert.Equal(AppConfig.LinuxFolderName, paths.appIdentifier);
        Assert.Equal(Path.Combine(localAppDataRoot, AppConfig.LinuxFolderName), paths.appDataPath);
        Assert.Equal(Path.Combine(configRoot, AppConfig.LinuxFolderName), paths.configPath);
    }

    [Fact]
    public void ResolveStoragePaths_OnMac_UsesBundleIdentifier()
    {
        var applicationDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var paths = LauncherPlatform.ResolveStoragePaths("osx");

        Assert.Equal(AppConfig.MacBundleIdentifier, paths.appIdentifier);
        Assert.Equal(Path.Combine(applicationDataRoot, AppConfig.MacBundleIdentifier), paths.appDataPath);
        Assert.Equal(paths.appDataPath, paths.configPath);
    }
}
