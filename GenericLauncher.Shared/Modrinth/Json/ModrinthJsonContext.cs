using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace GenericLauncher.Modrinth.Json;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ModrinthSearchResponse))]
[JsonSerializable(typeof(ModrinthSearchResult))]
[JsonSerializable(typeof(ModrinthSearchResult[]))]
[JsonSerializable(typeof(ModrinthProject))]
[JsonSerializable(typeof(ModrinthLicense))]
[JsonSerializable(typeof(ModrinthGalleryItem))]
[JsonSerializable(typeof(ModrinthGalleryItem[]))]
[JsonSerializable(typeof(ModrinthVersion))]
[JsonSerializable(typeof(ModrinthVersion[]))]
[JsonSerializable(typeof(ModrinthDependency))]
[JsonSerializable(typeof(ModrinthDependency[]))]
[JsonSerializable(typeof(ModrinthVersionFile))]
[JsonSerializable(typeof(ModrinthVersionFile[]))]
[JsonSerializable(typeof(ModrinthFileHashes))]
[JsonSerializable(typeof(ModrinthVersionFilesUpdateRequest))]
[JsonSerializable(typeof(Dictionary<string, ModrinthVersion>))]
[JsonSerializable(typeof(string[][]))]
[JsonSerializable(typeof(string[]))]
public partial class ModrinthJsonContext : JsonSerializerContext;
