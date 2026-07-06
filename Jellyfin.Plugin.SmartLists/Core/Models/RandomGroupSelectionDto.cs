using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartLists.Core.Models
{
    /// <summary>
    /// Optional post-filter grouping configuration. When enabled, the filtered item pool
    /// is scoped to one randomly selected group before sorting and limits are applied.
    /// </summary>
    [Serializable]
    public class RandomGroupSelectionDto
    {
        public bool Enabled { get; set; }

        public string? GroupBy { get; set; }

        public int? MinimumItems { get; set; }

        private static readonly HashSet<string> _supportedGroupByFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "Artists",
            "AlbumArtists",
            "Album",
            "SeriesName",
            "Genres",
            "Studios",
            "Tags",
        };

        public static IReadOnlyCollection<string> SupportedGroupByFields => _supportedGroupByFields.ToArray();

        public static bool IsSupportedGroupByField(string? fieldName)
        {
            return !string.IsNullOrWhiteSpace(fieldName) && _supportedGroupByFields.Contains(fieldName);
        }
    }
}
