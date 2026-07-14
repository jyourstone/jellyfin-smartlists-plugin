using System.Text.Json.Serialization;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Plugin.SmartLists.Core.Models
{
    /// <summary>
    /// Represents a single sorting option with field and direction
    /// </summary>
    public class SortOption
    {
        public required string SortBy { get; set; }      // e.g., "Name", "ProductionYear", "SeasonNumber"
        public required SortOrder SortOrder { get; set; }   // Ascending or Descending

        /// <summary>
        /// The field to group items by when using Round Robin sort.
        /// e.g., "SeriesName", "AlbumName", "Artist", "Genres", "Studios"
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GroupByField { get; set; }

        /// <summary>
        /// How items are ordered inside each Round Robin group.
        /// "Natural" (default, also when null/unknown): season/episode, disc/track, or name.
        /// "AirDate": premiere date (day precision), tie-broken by season/episode.
        /// Ignored by Shuffled Round Robin (shuffle wins).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? WithinGroupOrder { get; set; }

        /// <summary>
        /// Window in days for chaining episodes of different shows into one round-robin
        /// interleave block. Only used with GroupByField "Collections" and WithinGroupOrder
        /// "AirDate". Null = default (3). 0 = same-day only. Clamped to [0, 30] at use.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? AirBlockWindowDays { get; set; }
    }
}

