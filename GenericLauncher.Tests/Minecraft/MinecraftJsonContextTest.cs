using System.IO;
using System.Net.Http;
using System.Text.Json;
using GenericLauncher.Http;
using GenericLauncher.Misc;
using GenericLauncher.Minecraft.Json;
using GenericLauncher.Minecraft;
using JetBrains.Annotations;
using LavaLauncher;
using Xunit;

namespace GenericLauncher.Tests.Minecraft;

[TestSubject(typeof(MinecraftJsonContext))]
public class MinecraftJsonContextTest
{
    private static LauncherPlatform CreatePlatform(string os, string architecture, System.Version? version = null) =>
        new(os,
            architecture,
            version ?? new System.Version(14, 0),
            AppConfig.MacBundleIdentifier,
            Path.Combine(Path.GetTempPath(), "lavalancher-tests", os, architecture),
            Path.Combine(Path.GetTempPath(), "lavalancher-tests", os, architecture));

    [Fact]
    public void Test_ParseMinecraftVersionDetailsJson()
    {
        var json = File.ReadAllText("../../../Data/client_1.21.10.json");
        var details = JsonSerializer.Deserialize(json, MinecraftJsonContext.Default.VersionDetails);

        _ = ArgumentsParser.FlattenArguments(details!.Arguments?.Game, CreatePlatform("windows", "x64", new System.Version(10, 0, 22631)));
    }

    [Fact]
    public void FlattenArguments_RespectsArchitectureSpecificRules()
    {
        var json = File.ReadAllText("../../../Data/client_1.21.10.json");
        var details = JsonSerializer.Deserialize(json, MinecraftJsonContext.Default.VersionDetails);

        var x86Args = ArgumentsParser.FlattenArguments(details!.Arguments?.Jvm, CreatePlatform("windows", "x86", new System.Version(10, 0, 19045)));
        var x64Args = ArgumentsParser.FlattenArguments(details.Arguments?.Jvm, CreatePlatform("windows", "x64", new System.Version(10, 0, 19045)));

        Assert.Contains("-Xss1M", x86Args);
        Assert.DoesNotContain("-Xss1M", x64Args);
    }

    [Fact]
    public void FlattenArguments_RespectsOsVersionRegexRules()
    {
        var json = File.ReadAllText("../../../Data/client_1.18.json");
        var details = JsonSerializer.Deserialize(json, MinecraftJsonContext.Default.VersionDetails);

        var windows10Args = ArgumentsParser.FlattenArguments(details!.Arguments?.Jvm, CreatePlatform("windows", "x64", new System.Version(10, 0, 19045)));
        var windows11Args = ArgumentsParser.FlattenArguments(details.Arguments?.Jvm, CreatePlatform("windows", "x64", new System.Version(10, 0, 22631)));

        Assert.Contains("-Dos.name=Windows 10", windows10Args);
        Assert.Contains("-Dos.name=Windows 10", windows11Args);
    }

    [Fact]
    public void CreateClassPath_UsesAppleSiliconNativeLibraries()
    {
        var json = File.ReadAllText("../../../Data/client_1.21.10.json");
        var details = JsonSerializer.Deserialize(json, MinecraftJsonContext.Default.VersionDetails);
        var platform = CreatePlatform("osx", "arm64");
        using var httpClient = new HttpClient();
        var downloader = new FileDownloader(httpClient);
        var manager = new MinecraftVersionManager(platform, httpClient, downloader, null);

        var classPath = manager.CreateClassPathForTesting(details!.Libraries);

        Assert.Contains(classPath, p => p.Contains("lwjgl-3.3.3-natives-macos-arm64.jar"));
        Assert.DoesNotContain(classPath, p => p.Contains("lwjgl-3.3.3-natives-macos.jar"));
        Assert.Contains(classPath, p => p.Contains("jtracy-1.0.36-natives-macos-arm64.jar"));
        Assert.DoesNotContain(classPath, p => p.Contains("jtracy-1.0.36-natives-macos.jar"));
    }
}
