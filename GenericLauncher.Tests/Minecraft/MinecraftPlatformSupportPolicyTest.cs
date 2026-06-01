using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GenericLauncher.Database.Model;
using GenericLauncher.Minecraft;
using GenericLauncher.Minecraft.Json;
using GenericLauncher.Minecraft.ModLoaders;
using GenericLauncher.Misc;
using GenericLauncher.Screens.NewInstanceDialog;
using JetBrains.Annotations;
using LavaLauncher;
using Xunit;

namespace GenericLauncher.Tests.Minecraft;

[TestSubject(typeof(MinecraftPlatformSupportPolicy))]
public class MinecraftPlatformSupportPolicyTest
{
    [Fact]
    public void FilterSupportedStableVersions_OnMacOsArm64_RemovesVersionsBefore119()
    {
        var versions = ImmutableList.Create(
            CreateRelease("1.18.2"),
            CreateRelease("1.19"),
            CreateRelease("1.21.10"));

        var filtered = MinecraftPlatformSupportPolicy.FilterSupportedStableVersions(
            versions,
            CreatePlatform("osx", "arm64"));

        Assert.Collection(
            filtered,
            version => Assert.Equal("1.19", version.Id),
            version => Assert.Equal("1.21.10", version.Id));
    }

    [Fact]
    public void FilterSupportedStableVersions_OnWindowsX64_KeepsCurrentStableVersions()
    {
        var versions = ImmutableList.Create(
            CreateRelease("1.18.2"),
            CreateRelease("1.19"),
            CreateRelease("1.21.10"));

        var filtered = MinecraftPlatformSupportPolicy.FilterSupportedStableVersions(
            versions,
            CreatePlatform("windows", "x64", new Version(10, 0, 22631)));

        Assert.Equal(["1.18.2", "1.19", "1.21.10"], filtered.Select(version => version.Id));
    }

    [Fact]
    public async Task NewInstanceDialogViewModel_ShowsMinimumVersionMessageAndSkipsLoaderLookupForUnsupportedVersion()
    {
        var version = CreateRelease("1.18.2");
        var loaderLookupCalled = false;
        var fakeLauncher = new FakeLauncherFacade
        {
            AvailableVersionsValue = [version],
            AvailableModLoadersValue = [MinecraftInstanceModLoader.Forge],
            LoaderVersions = (_, _) =>
            {
                loaderLookupCalled = true;
                return Task.FromResult(ImmutableList.Create(new ModLoaderVersionInfo("ignored", "LATEST")));
            },
        };

        var vm = new NewInstanceDialogViewModel(fakeLauncher, null, CreatePlatform("osx", "arm64"));
        await DrainUiAsync();

        Assert.False(loaderLookupCalled);
        Assert.False(vm.CanInstall);
        Assert.Equal(
            "Minecraft 1.19 is the minimum supported version on Apple Silicon because older versions do not include full Java 17 and ARM64 native library support.",
            vm.MinimumVersionMessage);
        Assert.Equal(vm.MinimumVersionMessage, vm.ModLoaderVersionStatusText);
    }

    private static VersionInfo CreateRelease(string id) =>
        new(id, VersionInfo.TypeRelease, $"https://example.test/{id}", default, default, "", 0);

    private static LauncherPlatform CreatePlatform(string os, string architecture, Version? version = null) =>
        new(os,
            architecture,
            version ?? new Version(14, 0),
            AppConfig.MacBundleIdentifier,
            Path.Combine(Path.GetTempPath(), "lavalancher-tests", os, architecture),
            Path.Combine(Path.GetTempPath(), "lavalancher-tests", os, architecture));

    private static Task DrainUiAsync()
    {
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return Task.CompletedTask;
    }

    private sealed class FakeLauncherFacade : IMinecraftLauncherFacade
    {
        public ImmutableList<VersionInfo> AvailableVersionsValue { get; init; } = [];
        public ImmutableList<MinecraftInstanceModLoader> AvailableModLoadersValue { get; init; } = [];

        public Func<MinecraftInstanceModLoader, string, Task<ImmutableList<ModLoaderVersionInfo>>> LoaderVersions
        {
            get;
            init;
        } =
            (_, _) => Task.FromResult(ImmutableList<ModLoaderVersionInfo>.Empty);

        public ImmutableList<VersionInfo> AvailableVersions => AvailableVersionsValue;
        public ImmutableList<MinecraftInstanceModLoader> AvailableModLoaders => AvailableModLoadersValue;
#pragma warning disable CS0067
        public event EventHandler? AvailableVersionsChanged;
#pragma warning restore CS0067

        public Task<ImmutableList<ModLoaderVersionInfo>> GetLoaderVersionsAsync(
            MinecraftInstanceModLoader modLoader,
            string minecraftVersionId,
            bool reload) =>
            LoaderVersions(modLoader, minecraftVersionId);

        public Task CreateInstance(
            VersionInfo version,
            string instanceId,
            MinecraftInstanceModLoader modLoader,
            string? preferredModLoaderVersion,
            IProgress<ThreadSafeInstallProgressReporter.InstallProgress> progress) =>
            Task.CompletedTask;
    }
}
