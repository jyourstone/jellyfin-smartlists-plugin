using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;

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

        /// <summary>
        /// Maps a supported GroupBy field to the extraction group required to derive its grouping keys.
        /// This is the single source of truth for the GroupBy-to-ExtractionGroup mapping, used both when
        /// analyzing field requirements and when deriving group keys, so they stay in sync.
        /// </summary>
        /// <param name="groupBy">The GroupBy field name.</param>
        /// <returns>The required <see cref="ExtractionGroup"/>, or <see cref="ExtractionGroup.None"/> if unsupported.</returns>
        public static ExtractionGroup GetExtractionGroup(string? groupBy)
        {
            return groupBy switch
            {
                "Artists" or "AlbumArtists" or "Album" => ExtractionGroup.AudioMetadata,
                "SeriesName" => ExtractionGroup.SeriesName,
                "Genres" or "Studios" or "Tags" => ExtractionGroup.ItemLists,
                _ => ExtractionGroup.None,
            };
        }
    }
}
