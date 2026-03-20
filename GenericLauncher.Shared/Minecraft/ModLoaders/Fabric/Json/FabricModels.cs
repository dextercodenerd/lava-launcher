using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenericLauncher.Minecraft.ModLoaders.Fabric.Json;

public sealed record FabricLoaderVersion(
    string Separator,
    int Build,
    string Maven,
    string Version,
    bool Stable);

public sealed record FabricLauncherProfile(
    string Id,
    [property: JsonPropertyName("inheritsFrom")]
    string InheritsFrom,
    string MainClass,
    FabricArguments? Arguments,
    List<FabricLibrary>? Libraries);

public sealed record FabricArguments(
    List<JsonElement>? Game,
    List<JsonElement>? Jvm);

public sealed record FabricLibrary(
    string Name,
    string? Url,
    string? Sha1);

