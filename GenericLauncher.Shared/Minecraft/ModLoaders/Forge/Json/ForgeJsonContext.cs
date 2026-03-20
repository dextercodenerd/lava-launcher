using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GenericLauncher.Minecraft.ModLoaders.Forge.Json;

[JsonSerializable(typeof(ForgePromotions))]
[JsonSerializable(typeof(ForgeInstallProfile))]
[JsonSerializable(typeof(ForgeInstallProcessor))]
[JsonSerializable(typeof(List<ForgeInstallProcessor>))]
[JsonSerializable(typeof(ForgeVersionProfile))]
[JsonSerializable(typeof(Dictionary<string, ForgeInstallDataEntry>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.AllowReadingFromString
)]
internal partial class ForgeJsonContext : JsonSerializerContext;
