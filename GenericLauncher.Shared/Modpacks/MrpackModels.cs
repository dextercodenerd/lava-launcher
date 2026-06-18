using System.Collections.Immutable;
using GenericLauncher.Database.Model;

namespace GenericLauncher.Modpacks;

public sealed record MrpackIdentity(
    string Name,
    string VersionId,
    string? Summary);

public sealed record MrpackDependencies(
    string MinecraftVersionId,
    MinecraftInstanceModLoader ModLoader,
    string? ModLoaderVersion);

public sealed record MrpackPlannedFile(
    string Path,
    ImmutableArray<string> Downloads,
    string Sha1,
    string Sha512,
    long FileSize);

public sealed record MrpackOverrideEntry(
    string ArchivePath,
    string Path);

public sealed record MrpackSkippedFile(
    string Path,
    string Reason);

public sealed record MrpackClientInstallPlan(
    MrpackIdentity Identity,
    MrpackDependencies Dependencies,
    ImmutableArray<MrpackPlannedFile> Files,
    ImmutableArray<MrpackOverrideEntry> Overrides,
    ImmutableArray<MrpackSkippedFile> SkippedFiles);
