using System.Text.Json.Serialization;

namespace GenericLauncher.Modpacks.Json;

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(MrpackIndex))]
[JsonSerializable(typeof(MrpackFile))]
[JsonSerializable(typeof(MrpackHashes))]
[JsonSerializable(typeof(MrpackEnv))]
public partial class MrpackJsonContext : JsonSerializerContext;
