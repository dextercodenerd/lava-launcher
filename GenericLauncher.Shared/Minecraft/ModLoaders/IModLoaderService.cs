using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Misc;

namespace GenericLauncher.Minecraft.ModLoaders;

public interface IModLoaderService
{
    string DisplayName { get; }

    Task<ImmutableList<ModLoaderVersionInfo>> GetLoaderVersionsAsync(
        string minecraftVersionId,
        bool reload,
        CancellationToken cancellationToken = default);

    Task<ResolvedModLoaderVersion> ResolveAsync(
        string minecraftVersionId,
        string? preferredLoaderVersion,
        LauncherPlatform platform,
        CancellationToken cancellationToken = default);

    Task InstallAsync(
        ResolvedModLoaderVersion resolved,
        ModLoaderInstallContext context,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
