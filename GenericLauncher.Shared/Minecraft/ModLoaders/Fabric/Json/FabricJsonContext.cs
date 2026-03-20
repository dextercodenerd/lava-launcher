using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GenericLauncher.Minecraft.ModLoaders.Fabric.Json;

[JsonSerializable(typeof(FabricLoaderVersion))]
[JsonSerializable(typeof(List<FabricLoaderVersion>))]
[JsonSerializable(typeof(FabricLauncherProfile))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.AllowReadingFromString
)]
internal partial class FabricJsonContext : JsonSerializerContext;