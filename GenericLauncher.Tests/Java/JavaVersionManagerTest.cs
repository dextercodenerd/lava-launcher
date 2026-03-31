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
            "/tmp/lavalancher-tests",
            "/tmp/lavalancher-tests");
        var installationPath = Path.Combine("/tmp/lavalancher-tests", "java", "21-mac-aarch64");

        var javaPath = JavaVersionManager.BuildJavaExecutablePath(installationPath, platform);

        Assert.Equal(Path.Combine(installationPath, "Contents", "Home", "bin", "java"), javaPath);
    }

    [Fact]
    public void TryParseTemurinApiDownloadInfo_EmptyPayload_ReturnsFalse()
    {
        var found = JavaVersionManager.TryParseTemurinApiDownloadInfo("[ ]", out var info);

        Assert.False(found);
        Assert.Equal(default, info);
    }

    [Fact]
    public void TryParseTemurinGitHubReleaseDownloadInfo_FindsMacTarballAndChecksum()
    {
        const string response = """
                                {
                                  "tag_name": "jdk-16.0.2+7",
                                  "assets": [
                                    {
                                      "name": "OpenJDK16U-jdk_x64_mac_hotspot_16.0.2_7.pkg",
                                      "browser_download_url": "https://example.invalid/jdk.pkg"
                                    },
                                    {
                                      "name": "OpenJDK16U-jdk_x64_mac_hotspot_16.0.2_7.tar.gz",
                                      "browser_download_url": "https://example.invalid/jdk.tar.gz"
                                    },
                                    {
                                      "name": "OpenJDK16U-jdk_x64_mac_hotspot_16.0.2_7.tar.gz.sha256.txt",
                                      "browser_download_url": "https://example.invalid/jdk.tar.gz.sha256.txt"
                                    }
                                  ]
                                }
                                """;

        var found = JavaVersionManager.TryParseTemurinGitHubReleaseDownloadInfo(
            response,
            16,
            "mac",
            "x64",
            out var info);

        Assert.True(found);
        Assert.Equal("https://example.invalid/jdk.tar.gz", info.downloadUrl);
        Assert.Equal("https://example.invalid/jdk.tar.gz.sha256.txt", info.checksumUrl);
        Assert.Equal("jdk-16.0.2+7", info.releaseTag);
    }

    [Fact]
    public void ParseSha256Checksum_ExtractsHashFromChecksumFile()
    {
        var hash = JavaVersionManager.ParseSha256Checksum(
            "abcdef1234567890  OpenJDK16U-jdk_x64_mac_hotspot_16.0.2_7.tar.gz");

        Assert.Equal("abcdef1234567890", hash);
    }
}
