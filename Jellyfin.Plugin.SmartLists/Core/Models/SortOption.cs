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
    }
}

