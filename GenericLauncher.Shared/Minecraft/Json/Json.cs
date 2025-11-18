using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using GenericLauncher.Misc;

namespace GenericLauncher.Minecraft.Json;

// Record types for JSON deserialization
public record MinecraftManifest(
    LatestVersions Latest,
    List<VersionInfo> Versions);

public record LatestVersions(
    string Release,
    string Snapshot);

public record VersionInfo(
    string Id,
    string Type, // no enum, so the parsing does not crash in the future
    string Url,
    UtcInstant Time,
    UtcInstant ReleaseTime,
    string Sha1,
    int ComplianceLevel)
{
    public const string TypeRelease = "release";
}

// https://minecraft.fandom.com/wiki/Client.json
public record VersionDetails(
    string Id,
    string Type, // "release" or "snapshot"; no enum, so the parsing does not crash in the future
    string MainClass,
    string? MinecraftArguments, // Up to all 1.12 versions
    Arguments? Arguments, // Since 1.13, replaces the MinecraftArguments
    string Assets,
    AssetIndex AssetIndex,
    int MinimumLauncherVersion,
    JavaVersionInfo? JavaVersion, // Since 1.17
    Downloads Downloads,
    List<Library>? Libraries,
    Logging? Logging, // Since 1.7.2
    UtcInstant ReleaseTime,
    int ComplianceLevel
);

[JsonConverter(typeof(ArgumentConverter))]
public abstract record Argument;

public record StringArgument(string Value) : Argument;

public record ObjectArgument(List<Rule>? Rules, JsonElement Value) : Argument;

public record ArgumentObject(
    List<Rule>? Rules,
    JsonElement Value // can be string or array of strings[]
) : Argument;

public record Rule(
    string Action,
    OsInfo? Os,
    Dictionary<string, bool>? Features
)
{
    public static string ActionAllow = "allow";
};

public record OsInfo(
    string? Name,
    string? Version,
    string? Arch
);

public record Arguments(
    List<JsonElement> Game, // can be string or ArgumentRule
    List<JsonElement> Jvm // can be string or ArgumentRule
);

public record JavaVersionInfo(
    string Component,
    int MajorVersion);

public record Downloads(
    DownloadItem Client,
    DownloadItem? Server,
    [property: JsonPropertyName("client_mappings")]
    DownloadItem? ClientMappings,
    [property: JsonPropertyName("server_mappings")]
    DownloadItem? ServerMappings);

public record DownloadItem(
    string Sha1,
    long Size,
    string Url);

public record Library(
    LibraryDownloads? Downloads,
    List<Rule>? Rules,

    // the rest is not important
    string Name, // A maven name for the library, in the form of "groupId:artifactId:version".
    string? Url, // The URL of the Maven repository (used by Forge).
    Dictionary<string, string>? Natives,
    LibraryExtract? Extract
);

public record LibraryDownloads(
    Artifact? Artifact,
    Dictionary<string, Artifact>? Classifiers
);

public record LibraryExtract(
    List<string>? Exclude
);

public record Artifact(
    string Path, // Path to store the downloaded artifact, relative to the "libraries" directory in .minecraft.
    string Sha1,
    long Size,
    string Url);

public record AssetIndex(
    string Id,
    string Sha1,
    long Size, // The size of the version.
    long TotalSize, // The total size of the version.
    string Url
);

public record AssetsManifest(
    Dictionary<string, AssetObject> Objects
);

public record AssetObject(
    string Hash,
    long Size
);

public record Logging(
    LoggingClient? Client
);

public record LoggingClient(
    string Argument,
    LoggingFile File,
    string Type
);

public record LoggingFile(
    string Id,
    string Sha1,
    long Size,
    string Url);

public record ArgumentRule(
    List<Rule> Rules,
    JsonElement Value // string or List<string>
);

public record GameFile(
    string Hash,
    long Size
);
