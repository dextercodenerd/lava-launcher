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

/// <summary>
/// The result returned by <c>GetLatestCompatibleVersionsAsync</c>.
/// <see cref="Versions"/> contains the best compatible version per project id for all projects
/// where a result (fresh or stale) is available.
/// <see cref="HasRefreshFailure"/> is <c>true</c> when at least one per-project lookup failed —
/// either because the fetch failed and no prior cached data exists (unavailable), or because
/// the fetch failed but stale cached data was used (stale-success).  Callers should surface a
/// quiet indicator so users know the data may be incomplete or outdated.
/// </summary>
public sealed record LatestCompatibleVersionsResult(
    ImmutableDictionary<string, LatestCompatibleVersionInfo> Versions,
    bool HasRefreshFailure
);

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
