using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GenericLauncher.Minecraft.ModLoaders.NeoForge.Json;

[JsonSerializable(typeof(NeoForgeInstallProfile))]
[JsonSerializable(typeof(NeoForgeInstallProcessor))]
[JsonSerializable(typeof(List<NeoForgeInstallProcessor>))]
[JsonSerializable(typeof(NeoForgeVersionProfile))]
[JsonSerializable(typeof(Dictionary<string, NeoForgeInstallDataEntry>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.AllowReadingFromString
)]
internal partial class NeoForgeJsonContext : JsonSerializerContext;
