using System;
using System.Collections.Immutable;
using System.Linq;
using System.Xml.Linq;

namespace GenericLauncher.Minecraft.ModLoaders;

internal static class MavenCoordinate
{
    internal static ImmutableList<string> ParseMetadataVersions(string xml) => XDocument.Parse(xml)
        .Descendants("version")
        .Select(v => v.Value.Trim())
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Reverse()
        .ToImmutableList();

    internal static string ToRelativePath(string maven)
    {
        // Format: group:artifact:version[:classifier][@ext]
        var extension = "jar";
        var coordinate = maven;
        var at = coordinate.IndexOf('@');
        if (at >= 0 && at + 1 < coordinate.Length)
        {
            extension = coordinate[(at + 1)..];
            coordinate = coordinate[..at];
        }

        var parts = coordinate.Split(':');
        if (parts.Length < 3)
        {
            throw new ArgumentException($"Invalid maven coordinate '{maven}'", nameof(maven));
        }

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length >= 4 ? parts[3] : null;

        var fileName = classifier is null
            ? $"{artifact}-{version}.{extension}"
            : $"{artifact}-{version}-{classifier}.{extension}";

        return $"{group}/{artifact}/{version}/{fileName}";
    }
}
