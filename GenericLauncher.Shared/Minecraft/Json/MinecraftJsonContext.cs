using System.Text.Json.Serialization;

namespace GenericLauncher.Minecraft.Json;

[JsonSerializable(typeof(MinecraftManifest))]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(VersionDetails))]
[JsonSerializable(typeof(JavaVersionInfo))]
[JsonSerializable(typeof(Downloads))]
[JsonSerializable(typeof(DownloadItem))]
[JsonSerializable(typeof(Library))]
[JsonSerializable(typeof(LibraryDownloads))]
[JsonSerializable(typeof(LibraryExtract))]
[JsonSerializable(typeof(Artifact))]
[JsonSerializable(typeof(AssetIndex))]
[JsonSerializable(typeof(AssetsManifest))]
[JsonSerializable(typeof(AssetObject))]
[JsonSerializable(typeof(Logging))]
[JsonSerializable(typeof(LoggingClient))]
[JsonSerializable(typeof(LoggingFile))]
[JsonSerializable(typeof(Arguments))]
[JsonSerializable(typeof(ArgumentObject))]
[JsonSerializable(typeof(ArgumentRule))]
[JsonSerializable(typeof(Rule))]
[JsonSerializable(typeof(OsInfo))]
[JsonSerializable(typeof(GameFile))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.AllowReadingFromString
)]
internal partial class MinecraftJsonContext : JsonSerializerContext;
