using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// The provider IDs of one library item, as input to <see cref="MissingItemsCalculator"/>.
    /// </summary>
    /// <param name="Kind">The item's kind, as OperandFactory.GetExternalListItemKind maps it.</param>
    /// <param name="ImdbId">The IMDb ID, if any.</param>
    /// <param name="TmdbId">The TMDB ID, if any.</param>
    /// <param name="TvdbId">The TVDB ID, if any.</param>
    internal readonly record struct LibraryItemIds(ExternalListItemKind Kind, string? ImdbId, string? TmdbId, string? TvdbId);

    /// <summary>
    /// Finds the external-list entries no library item matches, using the refresh's matcher (ExternalListResult.TryGetPosition).
    /// </summary>
    internal static class MissingItemsCalculator
    {
        /// <summary>
        /// Splits the distinct entries of an external list into missing and present.
        /// </summary>
        /// <param name="result">The external list's last fetched result.</param>
        /// <param name="libraryItems">The library items to match against.</param>
        /// <returns>The missing entries in ascending position, and the number of distinct entries.</returns>
        internal static (List<ExternalListEntry> Missing, int TotalEntries) Compute(ExternalListResult result, IEnumerable<LibraryItemIds> libraryItems)
        {
            var hits = libraryItems.SelectMany(item => Positions(result, item.Kind, item.ImdbId, item.TmdbId, item.TvdbId)).ToHashSet();

            var seen = new HashSet<(ExternalListItemKind Kind, string? ImdbId, string? TmdbId, string? TvdbId)>();
            var missing = new List<ExternalListEntry>();

            // Letterboxd adds entries as its concurrent page fetches finish, so sort by position first;
            // the dedupe below then keeps the lowest position of a repeated entry.
            foreach (var entry in result.Entries.OrderBy(e => e.Position))
            {
                if (seen.Add((entry.Kind, entry.ImdbId, entry.TmdbId, entry.TvdbId))
                    && !Positions(result, entry.Kind, entry.ImdbId, entry.TmdbId, entry.TvdbId).Any(hits.Contains))
                {
                    missing.Add(entry);
                }
            }

            return (missing, seen.Count);
        }

        // Each ID is looked up on its own, so an item whose IDs point at different entries marks all of them.
        private static IEnumerable<int> Positions(ExternalListResult result, ExternalListItemKind kind, string? imdbId, string? tmdbId, string? tvdbId)
        {
            if (result.TryGetPosition(kind, imdbId, null, null, out var position))
            {
                yield return position;
            }

            if (result.TryGetPosition(kind, null, tmdbId, null, out position))
            {
                yield return position;
            }

            if (result.TryGetPosition(kind, null, null, tvdbId, out position))
            {
                yield return position;
            }
        }
    }
}
