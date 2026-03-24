using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
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
    int Limit = 20,
    IReadOnlyList<IReadOnlyList<string>>? FacetGroups = null)
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

        var facets = new List<string[]>();
        if (!string.IsNullOrEmpty(projectTypeValue))
        {
            facets.Add([$"project_type:{projectTypeValue}"]);
        }

        if (FacetGroups is not null)
        {
            facets.AddRange(FacetGroups
                .Where(group => group.Count > 0)
                .Select(group => group.ToArray()));
        }

        if (facets.Count == 0)
        {
            return "";
        }

        return JsonSerializer.Serialize(facets.ToArray(), typeof(string[][]), ModrinthJsonContext.Default);
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
