using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using GenericLauncher.Modrinth.Json;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Modrinth;

/// <summary>
/// Client for interacting with the Modrinth API.
/// </summary>
public class ModrinthApiClient
{
    private const string BaseUrl = "https://api.modrinth.com/v2";

    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    public ModrinthApiClient(HttpClient httpClient, ILogger? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Search for projects on Modrinth.
    /// </summary>
    public async Task<ModrinthSearchResponse?> SearchProjectsAsync(ModrinthSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryString = BuildQueryString(query);
            var url = $"{BaseUrl}/search?{queryString}";
            _logger?.LogInformation("Searching Modrinth: {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync(ModrinthJsonContext.Default.ModrinthSearchResponse,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // TODO: rethrow and handle on the caller's side
            _logger?.LogError(ex, "Failed to search Modrinth projects");
            return null;
        }
    }

    /// <summary>
    /// Get detailed information about a project.
    /// </summary>
    public async Task<ModrinthProject?> GetProjectAsync(string idOrSlug, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{BaseUrl}/project/{Uri.EscapeDataString(idOrSlug)}";
            _logger?.LogInformation("Fetching Modrinth project: {IdOrSlug}", idOrSlug);

            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync(ModrinthJsonContext.Default.ModrinthProject,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // TODO: rethrow and handle on the caller's side
            _logger?.LogError(ex, "Failed to get Modrinth project: {IdOrSlug}", idOrSlug);
            return null;
        }
    }

    private static string BuildQueryString(ModrinthSearchQuery query)
    {
        // Parsing (empty string) returns an internal HttpQSCollection, which we need for the
        // correct escaping, and it's not possible to create it directly...
        var parameters = HttpUtility.ParseQueryString(string.Empty);

        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            parameters["query"] = query.Query;
        }

        parameters["index"] = query.SortOrder;
        parameters["offset"] = query.Offset.ToString();
        parameters["limit"] = query.Limit.ToString();

        // Build facets JSON
        var facetsJson = query.BuildFacetsJson();
        if (!string.IsNullOrWhiteSpace(facetsJson))
        {
            parameters["facets"] = facetsJson;
        }

        return parameters.ToString() ?? "";
    }
}
