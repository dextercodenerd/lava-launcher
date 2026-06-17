using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using GenericLauncher.Minecraft.Json;
using GenericLauncher.Misc;

namespace GenericLauncher.Minecraft;

internal static class MinecraftPlatformSupportPolicy
{
    private const string MacOsArm64MinimumStableVersion = "1.19";

    private static readonly DottedNumericVersion MacOsArm64MinimumStableRelease =
        DottedNumericVersion.Parse(MacOsArm64MinimumStableVersion);

    internal static ImmutableList<VersionInfo> FilterSupportedStableVersions(
        IEnumerable<VersionInfo> versions,
        LauncherPlatform platform) =>
        versions
            .Where(version => EvaluateForNewInstance(platform, version).IsSupported)
            .ToImmutableList();

    internal static PlatformSupportResult EvaluateForNewInstance(LauncherPlatform platform, VersionInfo version)
    {
        if (!IsMacOsArm64(platform))
        {
            return PlatformSupportResult.Supported();
        }

        if (!string.Equals(version.Type, VersionInfo.TypeRelease, StringComparison.OrdinalIgnoreCase))
        {
            return PlatformSupportResult.Supported();
        }

        if (!DottedNumericVersion.TryParse(version.Id, out var parsedVersion))
        {
            return PlatformSupportResult.Unsupported(CreateMacOsArm64MinimumVersionMessage());
        }

        return parsedVersion.CompareTo(MacOsArm64MinimumStableRelease) < 0
            ? PlatformSupportResult.Unsupported(CreateMacOsArm64MinimumVersionMessage())
            : PlatformSupportResult.Supported();
    }

    internal static string? GetNewInstanceDialogMessage(LauncherPlatform platform) =>
        IsMacOsArm64(platform)
            ? CreateMacOsArm64MinimumVersionMessage()
            : null;

    private static bool IsMacOsArm64(LauncherPlatform platform) =>
        string.Equals(platform.CurrentOs, "osx", StringComparison.OrdinalIgnoreCase)
        && string.Equals(platform.Architecture, "arm64", StringComparison.OrdinalIgnoreCase);

    private static string CreateMacOsArm64MinimumVersionMessage() =>
        $"Minecraft {MacOsArm64MinimumStableVersion} is the minimum supported version on Apple Silicon because older versions do not include full Java 17 and ARM64 native library support.";

    internal readonly record struct PlatformSupportResult(bool IsSupported, string? Reason)
    {
        internal static PlatformSupportResult Supported() => new(true, null);

        internal static PlatformSupportResult Unsupported(string reason) => new(false, reason);
    }

    private readonly record struct DottedNumericVersion(ImmutableArray<int> Parts) : IComparable<DottedNumericVersion>
    {
        internal static DottedNumericVersion Parse(string value) =>
            TryParse(value, out var parsed)
                ? parsed
                : throw new FormatException($"Invalid dotted numeric version '{value}'.");

        internal static bool TryParse(string value, out DottedNumericVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return false;
            }

            var numbers = ImmutableArray.CreateBuilder<int>(parts.Length);
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var parsedPart))
                {
                    return false;
                }

                numbers.Add(parsedPart);
            }

            version = new DottedNumericVersion(numbers.MoveToImmutable());
            return true;
        }

        public int CompareTo(DottedNumericVersion other)
        {
            var maxLength = Math.Max(Parts.Length, other.Parts.Length);
            for (var index = 0; index < maxLength; index++)
            {
                var left = index < Parts.Length ? Parts[index] : 0;
                var right = index < other.Parts.Length ? other.Parts[index] : 0;
                var comparison = left.CompareTo(right);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return 0;
        }
    }
}
