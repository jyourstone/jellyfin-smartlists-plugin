using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Core.Models;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Cache key for media types to avoid string collision issues
    /// </summary>
    internal readonly record struct MediaTypesKey : IEquatable<MediaTypesKey>
    {
        private readonly string[] _sortedTypes;
        private readonly bool _hasCollectionsExpansion;
        private readonly bool _includeExtras;

        private MediaTypesKey(string[] sortedTypes, bool hasCollectionsExpansion = false, bool includeExtras = false)
        {
            _sortedTypes = sortedTypes;
            _hasCollectionsExpansion = hasCollectionsExpansion;
            _includeExtras = includeExtras;
        }

        public static MediaTypesKey Create(List<string> mediaTypes)
        {
            return Create(mediaTypes, null);
        }

        public static MediaTypesKey Create(List<string> mediaTypes, SmartListDto? dto)
        {
            if (mediaTypes == null || mediaTypes.Count == 0)
            {
                return new MediaTypesKey([], false);
            }

            // Deduplicate to ensure identical cache keys for equivalent content (e.g., ["Movie", "Movie"] = ["Movie"])
            var sortedTypes = mediaTypes.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

            // Determine Collections expansion flag
            bool collectionsExpansionFlag = false;
            if (dto != null)
            {
                // Include Collections episode expansion in cache key to avoid incorrect caching
                // when same media types have different expansion settings
                var hasCollectionsExpansion = dto.ExpressionSets?.Any(set =>
                    set.Expressions?.Any(expr =>
                        expr.MemberName == "Collections" && expr.IncludeEpisodesWithinSeries == true) == true) == true;

                // Use boolean flag instead of string marker to distinguish caches with Collections expansion
                collectionsExpansionFlag = hasCollectionsExpansion && sortedTypes.Contains(MediaTypes.Episode) && !sortedTypes.Contains(MediaTypes.Series);
            }

            // Include IncludeExtras flag in cache key to avoid sharing cached results
            // between lists that include extras and those that don't
            bool includeExtrasFlag = dto?.IncludeExtras == true;

            return new MediaTypesKey(sortedTypes, collectionsExpansionFlag, includeExtrasFlag);
        }

        public bool Equals(MediaTypesKey other)
        {
            // Handle null arrays (default struct case) and use SequenceEqual for cleaner comparison
            var thisArray = _sortedTypes ?? [];
            var otherArray = other._sortedTypes ?? [];

            return thisArray.AsSpan().SequenceEqual(otherArray.AsSpan()) &&
                   _hasCollectionsExpansion == other._hasCollectionsExpansion &&
                   _includeExtras == other._includeExtras;
        }

        public override int GetHashCode()
        {
            // Handle null array (default struct case)
            var array = _sortedTypes ?? [];

            // Use HashCode.Combine for better distribution
            var hashCode = new HashCode();
            foreach (var item in array)
            {
                hashCode.Add(item, StringComparer.Ordinal);
            }
            hashCode.Add(_hasCollectionsExpansion);
            hashCode.Add(_includeExtras);

            return hashCode.ToHashCode();
        }

        public override string ToString()
        {
            var array = _sortedTypes ?? [];
            var typesString = string.Join(",", array);
            var suffix = "";
            if (_hasCollectionsExpansion) suffix += "[CollectionsExpansion]";
            if (_includeExtras) suffix += "[IncludeExtras]";
            return typesString + suffix;
        }
    }
}

