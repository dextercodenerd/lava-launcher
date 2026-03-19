namespace GenericLauncher.Minecraft.ModLoaders;

public sealed record ResolvedModLoaderLibrary(
    string Name,
    string Url,
    string FilePath,
    string? Sha1);
