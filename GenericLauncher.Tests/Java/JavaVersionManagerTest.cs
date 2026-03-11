using System;
using System.IO;
using GenericLauncher.Java;
using GenericLauncher.Misc;
using JetBrains.Annotations;
using LavaLauncher;
using Xunit;

namespace GenericLauncher.Tests.Java;

[TestSubject(typeof(JavaVersionManager))]
public class JavaVersionManagerTest
{
    [Fact]
    public void BuildJavaExecutablePath_OnMac_UsesContentsHomeLayout()
    {
        var platform = new LauncherPlatform(
            "osx",
            "arm64",
            new Version(14, 0),
            AppConfig.MacBundleIdentifier,
            "/tmp/lavalancher-tests");
        var installationPath = Path.Combine("/tmp/lavalancher-tests", "java", "21-mac-aarch64");

        var javaPath = JavaVersionManager.BuildJavaExecutablePath(installationPath, platform);

        Assert.Equal(Path.Combine(installationPath, "Contents", "Home", "bin", "java"), javaPath);
    }
}
