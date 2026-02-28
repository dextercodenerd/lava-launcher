using System.Text.Json;
using GenericLauncher.Modrinth.Json;

namespace GenericLauncher.Modrinth;

/// <summary>
/// Query parameters for Modrinth search.
/// </summary>
public record ModrinthSearchQuery(
    string Query = "",
    ModrinthProjectType ProjectType = ModrinthProjectType.All,
    string SortOrder = "relevance",
    int Offset = 0,
    int Limit = 20)
{
    /// <summary>
    /// Builds the facets JSON string for the API request.
    /// </summary>
    public string BuildFacetsJson()
    {
        var projectTypeValue = ProjectType switch
        {
            ModrinthProjectType.Mod => "mod",
            ModrinthProjectType.Modpack => "modpack",
            ModrinthProjectType.ResourcePack => "resourcepack",
            ModrinthProjectType.Shader => "shader",
            ModrinthProjectType.Plugins => "plugin",
            _ => "",
        };

        if (string.IsNullOrEmpty(projectTypeValue))
        {
            return "";
        }

        // Facets format: [["project_type:mod"]]
        var facets = new string[][]
        {
            [$"project_type:{projectTypeValue}",],
        };

        return JsonSerializer.Serialize(facets, typeof(string[][]), ModrinthJsonContext.Default);
    }
}

/// <summary>
/// Types of projects on Modrinth.
/// </summary>
public enum ModrinthProjectType
{
    All,
    Mod,
    Modpack,
    ResourcePack,
    Shader,
    Plugins,
}
