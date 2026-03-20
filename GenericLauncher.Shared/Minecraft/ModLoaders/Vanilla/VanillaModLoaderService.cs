using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Misc;

namespace GenericLauncher.Minecraft.ModLoaders.Vanilla;

public sealed class VanillaModLoaderService : IModLoaderService
{
    private const string VanillaLoaderVersionId = "vanilla";

    public string DisplayName => "Vanilla";

    public Task<ImmutableList<ModLoaderVersionInfo>> GetLoaderVersionsAsync(
        string minecraftVersionId,
        bool reload,
        CancellationToken cancellationToken = default)
    {
        _ = minecraftVersionId;
        _ = reload;
        _ = cancellationToken;
        return Task.FromResult(ImmutableList.Create(new ModLoaderVersionInfo(VanillaLoaderVersionId, "STABLE")));
    }

    public Task<ResolvedModLoaderVersion> ResolveAsync(
        string minecraftVersionId,
        string? preferredLoaderVersion,
        LauncherPlatform platform,
        CancellationToken cancellationToken = default)
    {
        _ = platform;
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
            null,
            null,
            [],
            [],
            []));
    }

    public Task InstallAsync(
        ResolvedModLoaderVersion resolved,
        ModLoaderInstallContext context,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        _ = cancellationToken;

        if (!string.Equals(resolved.DisplayName, DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Resolved version does not belong to Vanilla loader", nameof(resolved));
        }

        progress?.Report(1.0);
        return Task.CompletedTask;
    }
}
