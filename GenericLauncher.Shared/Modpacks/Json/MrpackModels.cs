using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GenericLauncher.Modpacks.Json;

public sealed record MrpackIndex(
    [property: JsonPropertyName("formatVersion")]
    int FormatVersion,
    [property: JsonPropertyName("game")] string? Game,
    [property: JsonPropertyName("versionId")]
    string? VersionId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("dependencies")]
    Dictionary<string, string>? Dependencies,
    [property: JsonPropertyName("files")]
    MrpackFile[]? Files
);

public sealed record MrpackFile(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("hashes")] MrpackHashes? Hashes,
    [property: JsonPropertyName("env")] MrpackEnv? Env,
    [property: JsonPropertyName("downloads")]
    string[]? Downloads,
    [property: JsonPropertyName("fileSize")]
    long FileSize
);

public sealed record MrpackHashes(
    [property: JsonPropertyName("sha1")] string? Sha1,
    [property: JsonPropertyName("sha512")] string? Sha512
);

public sealed record MrpackEnv(
    [property: JsonPropertyName("client")] string? Client,
    [property: JsonPropertyName("server")] string? Server
);
