using System;
using System.Collections.Generic;
using System.Linq;
using GenericLauncher.Minecraft.ModLoaders;

namespace GenericLauncher.Minecraft;

internal static class MinecraftClassPath
{
    internal static List<string> MergeVanillaAndModLoaderLibraries(
        IEnumerable<string> vanillaClassPath,
        IEnumerable<ResolvedModLoaderLibrary> modLoaderLibraries)
    {
        // Mod-loader libraries are appended after vanilla so the loader wins on logical collisions.
        return Normalize(vanillaClassPath.Concat(modLoaderLibraries.Select(library => library.FilePath)));
    }

    internal static List<string> Normalize(IEnumerable<string> classPathEntries)
    {
        var entries = classPathEntries.ToList();
        if (entries.Count <= 1)
        {
            return entries;
        }

        var seenIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(entries.Count);

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (seenIdentities.Add(GetIdentity(entry)))
            {
                normalized.Add(entry);
            }
        }

        normalized.Reverse();
        return normalized;
    }

    internal static string? TryGetLogicalLibraryIdentity(string classPathEntry)
    {
        if (string.IsNullOrWhiteSpace(classPathEntry))
        {
            return null;
        }

        var pathSegments = classPathEntry
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var librariesIndex = Array.FindLastIndex(pathSegments,
            segment => string.Equals(segment, "libraries", StringComparison.OrdinalIgnoreCase));

        if (librariesIndex < 0 || pathSegments.Length - librariesIndex < 5)
        {
            return null;
        }

        var artifact = pathSegments[^3];
        var version = pathSegments[^2];
        var fileName = pathSegments[^1];

        if (string.IsNullOrWhiteSpace(artifact)
            || string.IsNullOrWhiteSpace(version)
            || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var extensionSeparator = fileName.LastIndexOf('.');
        if (extensionSeparator <= 0 || extensionSeparator == fileName.Length - 1)
        {
            return null;
        }

        var fileBaseName = fileName[..extensionSeparator];
        var extension = fileName[(extensionSeparator + 1)..];
        var expectedPrefix = $"{artifact}-{version}";

        if (!fileBaseName.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string? classifier = null;
        if (fileBaseName.Length > expectedPrefix.Length)
        {
            if (fileBaseName[expectedPrefix.Length] != '-')
            {
                return null;
            }

            classifier = fileBaseName[(expectedPrefix.Length + 1)..];
            if (string.IsNullOrWhiteSpace(classifier))
            {
                return null;
            }
        }

        var groupSegments = pathSegments[(librariesIndex + 1)..^3];
        if (groupSegments.Length == 0 || groupSegments.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        var group = string.Join('.', groupSegments);
        return classifier is null
            ? $"{group}:{artifact}@{extension}"
            : $"{group}:{artifact}:{classifier}@{extension}";
    }

    private static string GetIdentity(string classPathEntry)
    {
        return TryGetLogicalLibraryIdentity(classPathEntry)
               ?? $"path:{classPathEntry.Replace('\\', '/')}";
    }
}
