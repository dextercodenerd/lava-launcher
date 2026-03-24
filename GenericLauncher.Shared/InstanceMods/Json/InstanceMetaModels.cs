using System;

namespace GenericLauncher.InstanceMods.Json;

public sealed record InstanceMeta(
    int SchemaVersion,
    string DisplayName,
    string MinecraftVersionId,
    string ModLoader,
    string? ModLoaderVersion,
    string LaunchVersionId,
    InstanceMetaMod[] Mods
);

public sealed record InstanceMetaMod(
    string ProjectId,
    string ProjectSlug,
    string ProjectTitle,
    string InstalledVersionId,
    string InstalledVersionNumber,
    string InstalledVersionType,
    string InstalledFileName,
    string? InstalledFileSha512,
    string InstallKind,
    string[] RequiredByProjectIds,
    DateTime InstalledAtUtc
);
