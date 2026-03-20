using System;
using System.Collections.Generic;
using System.Linq;
using GenericLauncher.Minecraft.Json;
using GenericLauncher.Misc;

namespace GenericLauncher.Minecraft.ModLoaders;

internal static class MinecraftLibrarySelector
{
    internal static List<Library> SelectLibraries(List<Library>? libraries, LauncherPlatform platform)
    {
        if (libraries is null || libraries.Count == 0)
        {
            return [];
        }

        var selected = new List<Library>();
        var groupedDirectNatives = new Dictionary<string, List<(Library Library, int Rank)>>();

        foreach (var library in libraries)
        {
            if (!ArgumentsParser.IsRuleAllowed(library.Rules, platform))
            {
                continue;
            }

            var directNativeGroupKey = GetDirectNativeGroupKey(library);
            if (directNativeGroupKey is null)
            {
                selected.Add(library);
                continue;
            }

            var directNativeRank = GetDirectNativeRank(library, platform);
            if (directNativeRank is null)
            {
                continue;
            }

            if (!groupedDirectNatives.TryGetValue(directNativeGroupKey, out var group))
            {
                group = [];
                groupedDirectNatives[directNativeGroupKey] = group;
            }

            group.Add((library, directNativeRank.Value));
        }

        foreach (var group in groupedDirectNatives.Values)
        {
            selected.Add(group.OrderBy(v => v.Rank).ThenBy(v => v.Library.Name, StringComparer.Ordinal).First().Library);
        }

        return selected
            .DistinctBy(l => l.Name)
            .ToList();
    }

    private static string? GetDirectNativeGroupKey(Library library)
    {
        if (library.Downloads?.Artifact?.Path?.Contains("-natives-") != true)
        {
            return null;
        }

        var parts = library.Name.Split(':');
        if (parts.Length < 4 || !parts[3].StartsWith("natives-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return string.Join(':', parts.Take(3));
    }

    private static int? GetDirectNativeRank(Library library, LauncherPlatform platform)
    {
        var parts = library.Name.Split(':');
        if (parts.Length < 4)
        {
            return null;
        }

        var classifier = parts[3].ToLowerInvariant();
        if (!classifier.StartsWith("natives-", StringComparison.Ordinal))
        {
            return null;
        }

        return (platform.CurrentOs, platform.Architecture, classifier) switch
        {
            ("osx", "arm64", "natives-macos-arm64") => 0,
            ("osx", "arm64", "natives-osx-arm64") => 0,
            ("osx", "arm64", "natives-macos") => 1,
            ("osx", "arm64", "natives-osx") => 1,
            ("osx", "x64", "natives-macos") => 0,
            ("osx", "x64", "natives-osx") => 0,
            ("osx", "x64", "natives-macos-x64") => 1,
            ("osx", "x64", "natives-osx-x64") => 1,
            ("windows", "arm64", "natives-windows-arm64") => 0,
            ("windows", "arm64", "natives-windows") => 1,
            ("windows", "x64", "natives-windows") => 0,
            ("windows", "x64", "natives-windows-x64") => 1,
            ("windows", "x86", "natives-windows-x86") => 0,
            ("windows", "x86", "natives-windows") => 1,
            ("linux", "arm64", "natives-linux-arm64") => 0,
            ("linux", "arm64", "natives-linux-aarch64") => 0,
            ("linux", "arm64", "natives-linux-aarch_64") => 0,
            ("linux", "arm64", "natives-linux") => 1,
            ("linux", "x64", "natives-linux") => 0,
            ("linux", "x64", "natives-linux-x64") => 1,
            ("linux", "x64", "natives-linux-x86_64") => 1,
            _ => null,
        };
    }
}
