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
        Assert.Equal(platform.AppDataPath, platform.ConfigPath);
    }
}
