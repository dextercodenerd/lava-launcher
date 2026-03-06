using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace GenericLauncher.Minecraft.ModLoaders.Vanilla;

public sealed class VanillaModLoaderService : IModLoaderService
{
    private const string VanillaLoaderVersionId = "vanilla";

    public string DisplayName => "Vanilla";

    public Task<ImmutableList<ModLoaderVersionInfo>> GetLoaderVersionsAsync(
        bool reload,
        CancellationToken cancellationToken = default)
    {
        _ = reload;
        _ = cancellationToken;
        return Task.FromResult(ImmutableList.Create(new ModLoaderVersionInfo(VanillaLoaderVersionId, "STABLE")));
    }

    public Task<ResolvedModLoaderVersion> ResolveAsync(
        string minecraftVersionId,
        string? preferredLoaderVersion,
        string currentOs,
        CancellationToken cancellationToken = default)
    {
        _ = currentOs;
        _ = cancellationToken;

        if (string.IsNullOrWhiteSpace(minecraftVersionId))
        {
            throw new ArgumentException("Minecraft version id cannot be empty", nameof(minecraftVersionId));
        }

        if (!string.IsNullOrWhiteSpace(preferredLoaderVersion)
            && !string.Equals(preferredLoaderVersion, VanillaLoaderVersionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported vanilla loader version '{preferredLoaderVersion}'");
        }

        return Task.FromResult(new ResolvedModLoaderVersion(
            DisplayName,
            minecraftVersionId,
            minecraftVersionId,
            VanillaLoaderVersionId,
            "",
            null,
            [],
            [],
            []));
    }

    public Task DownloadAsync(
        ResolvedModLoaderVersion resolved,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!string.Equals(resolved.DisplayName, DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Resolved version does not belong to Vanilla loader", nameof(resolved));
        }

        progress?.Report(1.0);
        return Task.CompletedTask;
    }
}
