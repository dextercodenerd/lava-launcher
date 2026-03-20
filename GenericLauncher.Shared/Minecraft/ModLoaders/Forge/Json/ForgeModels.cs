using System.Collections.Generic;
using System.Text.Json.Serialization;
using GenericLauncher.Minecraft.Json;

namespace GenericLauncher.Minecraft.ModLoaders.Forge.Json;

internal sealed record ForgePromotions(
    [property: JsonPropertyName("promos")] Dictionary<string, string>? Promos);

internal sealed record ForgeInstallProfile(
    int Spec,
    string Profile,
    string Version,
    string? Path,
    string Minecraft,
    string? Json,
    string? ServerJarPath,
    Dictionary<string, ForgeInstallDataEntry>? Data,
    List<ForgeInstallProcessor>? Processors,
    List<Library>? Libraries);

internal sealed record ForgeInstallDataEntry(
    [property: JsonPropertyName("client")] string? Client,
    [property: JsonPropertyName("server")] string? Server);

internal sealed record ForgeInstallProcessor(
    List<string>? Sides,
    string Jar,
    List<string>? Classpath,
    List<string>? Args,
    Dictionary<string, string>? Outputs);

internal sealed record ForgeVersionProfile(
    string Id,
    string? InheritsFrom,
    string? MainClass,
    Arguments? Arguments,
    List<Library>? Libraries);
