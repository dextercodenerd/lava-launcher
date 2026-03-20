using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using GenericLauncher.Database.Model;
using GenericLauncher.Minecraft.Json;
using GenericLauncher.Minecraft.ModLoaders;

namespace GenericLauncher.Minecraft;

public interface IMinecraftLauncherFacade
{
    ImmutableList<VersionInfo> AvailableVersions { get; }
    ImmutableList<MinecraftInstanceModLoader> AvailableModLoaders { get; }

    event EventHandler? AvailableVersionsChanged;

    Task<ImmutableList<ModLoaderVersionInfo>> GetLoaderVersionsAsync(
        MinecraftInstanceModLoader modLoader,
        string minecraftVersionId,
        bool reload);

    Task CreateInstance(
        VersionInfo version,
        string name,
        MinecraftInstanceModLoader modLoader,
        string? preferredModLoaderVersion,
        IProgress<ThreadSafeInstallProgressReporter.InstallProgress> progress);
}
