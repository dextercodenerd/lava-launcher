using GenericLauncher.Misc;

namespace GenericLauncher.Minecraft.ModLoaders;

public sealed record ModLoaderInstallContext(
    LauncherPlatform Platform,
    string JavaExecutablePath,
    string MinecraftClientJarPath);
