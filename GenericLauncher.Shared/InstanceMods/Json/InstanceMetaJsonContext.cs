using System.Text.Json.Serialization;

namespace GenericLauncher.InstanceMods.Json;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(InstanceMeta))]
[JsonSerializable(typeof(InstanceMetaMod))]
[JsonSerializable(typeof(InstanceMetaMod[]))]
public partial class InstanceMetaJsonContext : JsonSerializerContext;
