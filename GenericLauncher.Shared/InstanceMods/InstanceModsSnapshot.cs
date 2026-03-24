using System;
using System.Collections.Immutable;

namespace GenericLauncher.InstanceMods;

public sealed record InstanceInstalledProjectState(
    string ProjectId,
    string DisplayName,
    string InstalledVersionId,
    string InstalledVersionNumber,
    InstanceModItemKind InstallKind,
    bool IsBroken,
    bool HasUpdate,
    string? LatestVersionNumber
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
            .AddRange(RequiredDependencies)
            .AddRange(ManualMods)
            .AddRange(BrokenMods);
}

public sealed class InstanceModsSnapshotChangedEventArgs(string instanceId, InstanceModsSnapshot snapshot) : EventArgs
{
    public string InstanceId { get; } = instanceId;
    public InstanceModsSnapshot Snapshot { get; } = snapshot;
}
