using System.Text.Json.Serialization;

namespace GenericLauncher.Modrinth.Json;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ModrinthSearchResponse))]
[JsonSerializable(typeof(ModrinthSearchResult))]
[JsonSerializable(typeof(ModrinthSearchResult[]))]
[JsonSerializable(typeof(ModrinthProject))]
[JsonSerializable(typeof(ModrinthLicense))]
[JsonSerializable(typeof(ModrinthGalleryItem))]
[JsonSerializable(typeof(ModrinthGalleryItem[]))]
[JsonSerializable(typeof(string[][]))]
public partial class ModrinthJsonContext : JsonSerializerContext;
