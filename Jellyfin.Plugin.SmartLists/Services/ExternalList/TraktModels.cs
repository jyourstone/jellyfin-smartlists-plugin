using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// A single item from a Trakt list or chart endpoint.
    /// For list items, the media is nested under "movie" or "show".
    /// For trending/popular, the media object includes ids directly plus a wrapper.
    /// </summary>
    public class TraktListItem
    {
        /// <summary>
        /// Gets or sets the movie object (present in list items and movie charts).
        /// </summary>
        [JsonPropertyName("movie")]
        public TraktMedia? Movie { get; set; }

        /// <summary>
        /// Gets or sets the show object (present in list items and show charts).
        /// </summary>
        [JsonPropertyName("show")]
        public TraktMedia? Show { get; set; }

        /// <summary>
        /// Gets or sets the ids directly on the item (some endpoints).
        /// </summary>
        [JsonPropertyName("ids")]
        public TraktIds? Ids { get; set; }
    }

    /// <summary>
    /// Represents a movie or show object from the Trakt API.
    /// </summary>
    public class TraktMedia
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("year")]
        public int? Year { get; set; }

        [JsonPropertyName("ids")]
        public TraktIds? Ids { get; set; }
    }

    /// <summary>
    /// Provider IDs from the Trakt API.
    /// </summary>
    public class TraktIds
    {
        [JsonPropertyName("trakt")]
        public int? Trakt { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        [JsonPropertyName("imdb")]
        public string? Imdb { get; set; }

        [JsonPropertyName("tmdb")]
        public int? Tmdb { get; set; }

        [JsonPropertyName("tvdb")]
        public int? Tvdb { get; set; }
    }
}
