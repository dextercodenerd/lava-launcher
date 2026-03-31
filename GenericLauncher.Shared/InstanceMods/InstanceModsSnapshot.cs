using System;
using System.Collections.Immutable;
using System.Linq;
using GenericLauncher.Misc;

namespace GenericLauncher.InstanceMods;

public sealed record CompatibleVersionInfo(
    string VersionId,
    string VersionNumber,
    string VersionType,
    UtcInstant DatePublished
);

public sealed record InstanceInstalledProjectState(
    string ProjectId,
    string DisplayName,
    string InstalledVersionId,
    string InstalledVersionNumber,
    InstanceModItemKind InstallKind,
    bool IsBroken
);

public sealed record LatestCompatibleVersionInfo(
    string ProjectId,
    string VersionId,
    string VersionNumber
);

public enum CompatibilityRefreshState
{
    Fresh,
    Stale,
    Unavailable,
}

public sealed record ProjectCompatibilityStatus(
    string ProjectId,
    LatestCompatibleVersionInfo? LatestVersion,
    CompatibilityRefreshState RefreshState
);

/// <summary>
/// The result returned by <c>GetLatestCompatibleVersionsAsync</c>.
/// <see cref="Projects"/> always contains one status per requested project id.
/// Successful lookups with no compatible versions still appear as <see cref="CompatibilityRefreshState.Fresh"/>
/// with a null <see cref="ProjectCompatibilityStatus.LatestVersion"/>.
/// Failed refreshes surface as either stale cached data or unavailable status.
/// </summary>
public sealed record LatestCompatibleVersionsResult(
    ImmutableDictionary<string, ProjectCompatibilityStatus> Projects)
{
    public bool HasRefreshFailure =>
        Projects.Values.Any(project => project.RefreshState != CompatibilityRefreshState.Fresh);
}

public sealed record InstanceModsSnapshot(
    ImmutableList<InstanceModListItem> InstalledMods,
    ImmutableList<InstanceModListItem> RequiredDependencies,
    ImmutableList<InstanceModListItem> ManualMods,
    ImmutableList<InstanceModListItem> BrokenMods,
    ImmutableDictionary<string, InstanceInstalledProjectState> ProjectsById)
{
    public static InstanceModsSnapshot Empty { get; } = new(
        [],
        [],
        [],
        [],
        ImmutableDictionary<string, InstanceInstalledProjectState>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));

    public ImmutableList<InstanceModListItem> AllItems =>
        InstalledMods
            .Concat(RequiredDependencies)
            .Concat(ManualMods)
            .Concat(BrokenMods)
            .ToImmutableList();
}

public sealed class InstanceModsSnapshotChangedEventArgs(string instanceId, InstanceModsSnapshot snapshot) : EventArgs
{
    public string InstanceId { get; } = instanceId;
    public InstanceModsSnapshot Snapshot { get; } = snapshot;
}
