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
        /// When true, sort values are aggregated from child items within collections/playlists.
        /// For example, sorting by DateCreated with this enabled will use the most recent
        /// DateCreated value among all child items. Only applicable to collection output types.
        /// Supported for: ProductionYear, CommunityRating, DateCreated, ReleaseDate.
        /// </summary>
        public bool UseChildValues { get; set; } = false;
    }
}

