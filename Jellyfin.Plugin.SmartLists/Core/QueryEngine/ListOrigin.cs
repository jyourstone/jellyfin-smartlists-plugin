using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine
{
    /// <summary>
    /// Identifies the smart list currently being built, so that list can be kept out of its own
    /// Collections/Playlists results (self-reference prevention).
    /// Matching is by identity only - the SmartLists provider-ID tether, or a stored Jellyfin item
    /// id - never by name. A separate list that merely shares the name is therefore matched
    /// normally, and renaming the Jellyfin playlist/collection does not break the exclusion.
    /// </summary>
    public sealed class ListOrigin
    {
        private readonly HashSet<Guid> _jellyfinItemIds;

        /// <summary>
        /// Initializes a new instance of the <see cref="ListOrigin"/> class describing the list being built.
        /// </summary>
        /// <param name="key">The plugin's own list id. This is what the plugin writes into the
        /// SmartLists provider-ID tether on the Jellyfin item, and it is also used as part of the
        /// per-item extraction cache keys so origin-filtered results are never shared between lists.</param>
        /// <param name="jellyfinItemIds">Every Jellyfin playlist/collection id belonging to this list
        /// (an AllUsers playlist has one per user). Empty until the list has been created in Jellyfin.</param>
        public ListOrigin(string key, IEnumerable<Guid> jellyfinItemIds)
        {
            ArgumentNullException.ThrowIfNull(jellyfinItemIds);

            Key = key;
            _jellyfinItemIds = [.. jellyfinItemIds];
        }

        /// <summary>
        /// Stable identifier for the list being built. Part of the per-item cache keys in RefreshCache.
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// Returns true when the candidate playlist/collection IS the list currently being built.
        /// </summary>
        /// <param name="candidate">The playlist/collection encountered while extracting membership.</param>
        /// <returns>True if the candidate is this list and must be skipped.</returns>
        public bool Matches(BaseItem candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            // The provider-ID tether is the authoritative identity. It is checked first because the
            // stored Jellyfin id can be stale, and the recovery that repairs it (PlaylistService /
            // CollectionService) runs *after* filtering - so during a recovery refresh the id set
            // alone would miss the real container and let the list see itself again.
            // Every list this plugin creates is tethered at creation.
            if (!string.IsNullOrEmpty(Key)
                && string.Equals(candidate.GetProviderId(ProviderKeys.SmartLists), Key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Before the list exists in Jellyfin this set is empty and nothing matches, which is
            // correct: there is no container of ours yet for an item to be a member of. Matching on
            // name instead would only ever hit somebody else's identically named list.
            return _jellyfinItemIds.Contains(candidate.Id);
        }
    }
}
