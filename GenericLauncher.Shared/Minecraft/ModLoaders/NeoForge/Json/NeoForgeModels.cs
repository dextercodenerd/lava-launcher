using System.Collections.Generic;
using System.Text.Json.Serialization;
using GenericLauncher.Minecraft.Json;

namespace GenericLauncher.Minecraft.ModLoaders.NeoForge.Json;

internal sealed record NeoForgeInstallProfile(
    int Spec,
    string Profile,
    string Version,
    string Minecraft,
    string? Json,
    string? ServerJarPath,
    string? MirrorList,
    Dictionary<string, NeoForgeInstallDataEntry>? Data,
    List<NeoForgeInstallProcessor>? Processors,
    List<Library>? Libraries);

internal sealed record NeoForgeInstallDataEntry(
    [property: JsonPropertyName("client")] string? Client,
    [property: JsonPropertyName("server")] string? Server);

internal sealed record NeoForgeInstallProcessor(
    List<string>? Sides,
    string Jar,
    List<string>? Classpath,
    List<string>? Args,
    Dictionary<string, string>? Outputs);

internal sealed record NeoForgeVersionProfile(
    string Id,
    string? InheritsFrom,
    string? MainClass,
    Arguments? Arguments,
    List<Library>? Libraries);
