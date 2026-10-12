using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SmartLists.Services.ExternalList;

namespace Jellyfin.Plugin.SmartLists.Core.Models
{
    /// <summary>
    /// The missing items of one external list, from its last successful fetch.
    /// </summary>
    public class MissingItemsListDto
    {
        /// <summary>
        /// Gets the external list URL, as stored in the rule.
        /// </summary>
        public required string Url { get; init; }

        /// <summary>
        /// Gets when the list was last fetched (UTC), or null when it has not been fetched since the server started.
        /// </summary>
        public DateTime? FetchedAt { get; init; }

        /// <summary>
        /// Gets a value indicating whether the list holds only music tracks, which are not checked.
        /// </summary>
        public bool IsMusic { get; init; }

        /// <summary>
        /// Gets a value indicating whether the last fetch returned the complete list.
        /// </summary>
        public bool IsComplete { get; init; }

        /// <summary>
        /// Gets the number of distinct entries in the list.
        /// </summary>
        public int TotalEntries { get; init; }

        /// <summary>
        /// Gets the entries no library item matches, in ascending list position.
        /// </summary>
        public IReadOnlyList<ExternalListEntry> Missing { get; init; } = [];
    }
}
