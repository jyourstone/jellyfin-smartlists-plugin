using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Response wrapper for MDBList API list items endpoint.
    /// The API returns items in separate "movies" and "shows" arrays.
    /// </summary>
    public class MdbListResponse
    {
        [JsonPropertyName("movies")]
        public MdbListItem[]? Movies { get; set; }

        [JsonPropertyName("shows")]
        public MdbListItem[]? Shows { get; set; }
    }

    /// <summary>
    /// A single item from an MDBList list.
    /// </summary>
    public class MdbListItem
    {
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        [JsonPropertyName("rank")]
        public int? Rank { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("tvdb_id")]
        public int? TvdbId { get; set; }

        [JsonPropertyName("mediatype")]
        public string? MediaType { get; set; }

        [JsonPropertyName("release_year")]
        public int? ReleaseYear { get; set; }

        [JsonPropertyName("ids")]
        public MdbListItemIds? Ids { get; set; }
    }

    /// <summary>
    /// Nested IDs object from the MDBList API response.
    /// </summary>
    public class MdbListItemIds
    {
        [JsonPropertyName("imdb")]
        public string? Imdb { get; set; }

        [JsonPropertyName("tmdb")]
        public int? Tmdb { get; set; }

        [JsonPropertyName("tvdb")]
        public int? Tvdb { get; set; }

        [JsonPropertyName("mdblist")]
        public string? MdbList { get; set; }
    }
}
