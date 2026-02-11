using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Response from TMDB user list endpoint (/list/{id}).
    /// </summary>
    public class TmdbListResponse
    {
        [JsonPropertyName("items")]
        public TmdbItem[]? Items { get; set; }

        [JsonPropertyName("total_pages")]
        public int? TotalPages { get; set; }

        [JsonPropertyName("total_results")]
        public int? TotalResults { get; set; }
    }

    /// <summary>
    /// Response from TMDB paginated endpoints (popular, trending, etc.).
    /// </summary>
    public class TmdbPageResponse
    {
        [JsonPropertyName("results")]
        public TmdbItem[]? Results { get; set; }

        [JsonPropertyName("page")]
        public int? Page { get; set; }

        [JsonPropertyName("total_pages")]
        public int? TotalPages { get; set; }

        [JsonPropertyName("total_results")]
        public int? TotalResults { get; set; }
    }

    /// <summary>
    /// A single item from a TMDB response.
    /// </summary>
    public class TmdbItem
    {
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("media_type")]
        public string? MediaType { get; set; }
    }
}
