using System.Text.Json.Serialization;

namespace GenericLauncher.Modrinth.Json;

/// <summary>
/// Response from the Modrinth search API.
/// </summary>
public record ModrinthSearchResponse(
    [property: JsonPropertyName("hits")] ModrinthSearchResult[] Hits,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("total_hits")]
    int TotalHits
);

/// <summary>
/// Individual search result item from Modrinth.
/// </summary>
public record ModrinthSearchResult(
    [property: JsonPropertyName("project_id")]
    string ProjectId,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")]
    string Description,
    [property: JsonPropertyName("categories")]
    string[] Categories,
    [property: JsonPropertyName("project_type")]
    string ProjectType,
    [property: JsonPropertyName("downloads")]
    int Downloads,
    [property: JsonPropertyName("icon_url")]
    string? IconUrl,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("date_created")]
    string DateCreated,
    [property: JsonPropertyName("date_modified")]
    string DateModified
);

/// <summary>
/// Full project details from Modrinth.
/// </summary>
public record ModrinthProject(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("project_type")]
    string ProjectType,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")]
    string Description,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("categories")]
    string[] Categories,
    [property: JsonPropertyName("client_side")]
    string ClientSide,
    [property: JsonPropertyName("server_side")]
    string ServerSide,
    [property: JsonPropertyName("downloads")]
    int Downloads,
    [property: JsonPropertyName("followers")]
    int Followers,
    [property: JsonPropertyName("icon_url")]
    string? IconUrl,
    [property: JsonPropertyName("published")]
    string Published,
    [property: JsonPropertyName("updated")]
    string Updated,
    [property: JsonPropertyName("license")]
    ModrinthLicense? License,
    [property: JsonPropertyName("source_url")]
    string? SourceUrl,
    [property: JsonPropertyName("issues_url")]
    string? IssuesUrl,
    [property: JsonPropertyName("discord_url")]
    string? DiscordUrl,
    [property: JsonPropertyName("wiki_url")]
    string? WikiUrl,
    [property: JsonPropertyName("gallery")]
    ModrinthGalleryItem[]? Gallery,
    [property: JsonPropertyName("game_versions")]
    string[] GameVersions,
    [property: JsonPropertyName("versions")]
    string[] Versions
);

public record ModrinthLicense(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string? Url
);

public record ModrinthGalleryItem(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("featured")]
    bool Featured,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")]
    string? Description
);
