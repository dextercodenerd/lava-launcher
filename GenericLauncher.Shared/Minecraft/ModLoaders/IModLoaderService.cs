using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace GenericLauncher.Minecraft.ModLoaders;

public interface IModLoaderService
{
    string DisplayName { get; }

    Task<ImmutableList<ModLoaderVersionInfo>> GetLoaderVersionsAsync(
        bool reload,
        CancellationToken cancellationToken = default);

    Task<ResolvedModLoaderVersion> ResolveAsync(
        string minecraftVersionId,
        string? preferredLoaderVersion,
        string currentOs,
        CancellationToken cancellationToken = default);

    Task DownloadAsync(
        ResolvedModLoaderVersion resolved,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
