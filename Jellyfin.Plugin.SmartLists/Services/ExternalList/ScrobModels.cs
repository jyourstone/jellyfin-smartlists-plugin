using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Response from the Scrob list endpoint (/api/proxy/lists/{id}).
    /// Items are already sorted by (sort_order, added_at) server-side, so array index reflects list position.
    /// </summary>
    public class ScrobListResponse
    {
        [JsonPropertyName("items")]
        public ScrobListItem[]? Items { get; set; }
    }

    /// <summary>
    /// A single item entry in a Scrob list.
    /// </summary>
    public class ScrobListItem
    {
        [JsonPropertyName("media")]
        public ScrobMedia? Media { get; set; }
    }

    /// <summary>
    /// The media referenced by a Scrob list item.
    /// </summary>
    public class ScrobMedia
    {
        [JsonPropertyName("tmdb_id")]
        public int? TmdbId { get; set; }

        /// <summary>
        /// Gets or sets the media kind: "movie", "series", or "episode".
        /// </summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>
        /// Gets or sets the series' TVDB ID. Only populated for episode items and for
        /// season items (type "series" with a non-null season_number).
        /// </summary>
        [JsonPropertyName("show_tvdb_id")]
        public int? ShowTvdbId { get; set; }

        /// <summary>
        /// Gets or sets whether this episode row was built from TVDB because it has no TMDB
        /// counterpart. Scrob stores the TVDB episode ID in <see cref="TmdbId"/> for those rows,
        /// so the ID must not be treated as a TMDB ID.
        /// </summary>
        [JsonPropertyName("tvdb_sourced")]
        public bool TvdbSourced { get; set; }
    }
}
