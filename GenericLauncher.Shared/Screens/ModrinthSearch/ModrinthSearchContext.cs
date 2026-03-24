using System;
using GenericLauncher.Database.Model;

namespace GenericLauncher.Screens.ModrinthSearch;

public sealed record ModrinthSearchContext(
    string Title,
    MinecraftInstance? TargetInstance,
    string? LockedMinecraftVersion,
    string? LockedLoader,
    bool LockProjectTypeToMods)
{
    public bool IsInstanceInstall => TargetInstance is not null;

    public string LockedFiltersSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LockedMinecraftVersion) && string.IsNullOrWhiteSpace(LockedLoader))
            {
                return "";
            }

            return $"{LockedMinecraftVersion} / {LockedLoader}";
        }
    }

    public static ModrinthSearchContext CreateRoot() =>
        new("Modrinth Search", null, null, null, false);

    public static ModrinthSearchContext CreateForInstance(MinecraftInstance instance) =>
        new(
            $"Add Mods to {instance.Id}",
            instance,
            instance.VersionId,
            instance.ModLoader.ToString(),
            true);
}
