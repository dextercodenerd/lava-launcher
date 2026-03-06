using System.Collections.Immutable;

namespace GenericLauncher.Minecraft.ModLoaders;

public sealed record ResolvedModLoaderVersion(
    string DisplayName,
    string MinecraftVersionId,

    // Concrete resolved loader profile/version id (e.g., Fabric-combined id) intended for
    // persistence and later launch wiring.
    string LaunchVersionId,

    // Specific loader version used to resolve this profile (e.g., Fabric loader 0.16.x).
    string LoaderVersionId,
    string MetadataJsonPath,
    string? MainClassOverride,
    ImmutableList<string> ExtraJvmArguments,
    ImmutableList<string> ExtraGameArguments,
    ImmutableList<ResolvedModLoaderLibrary> Libraries);
