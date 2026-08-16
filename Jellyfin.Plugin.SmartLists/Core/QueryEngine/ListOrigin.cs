using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine
{
    /// <summary>
    /// Identifies the smart list currently being built, so that list can be kept out of its own
    /// Collections/Playlists results (self-reference prevention).
    /// Matching is by Jellyfin item id whenever one is known; base-name comparison is only a fallback
    /// for the very first refresh, before the Jellyfin playlist/collection has been created, and even
    /// then only against containers of this list's own kind. Matching by id means a separate, manually
    /// created list that merely shares the name is no longer excluded.
    /// </summary>
    public sealed class ListOrigin
    {
        private readonly HashSet<Guid> _jellyfinItemIds;
        private readonly BaseItemKind _itemKind;
        private readonly string _baseName;

        /// <summary>
        /// Initializes a new instance of the <see cref="ListOrigin"/> class describing the list being built.
        /// </summary>
        /// <param name="key">Stable identifier for this list (the plugin's own list id). Also used as part of the
        /// per-item extraction cache keys so origin-filtered results are never shared between lists.</param>
        /// <param name="name">The list name; used only for the base-name fallback.</param>
        /// <param name="itemKind">What this list is rendered as in Jellyfin: <see cref="BaseItemKind.Playlist"/>
        /// or <see cref="BaseItemKind.BoxSet"/>. Bounds the base-name fallback to that kind.</param>
        /// <param name="jellyfinItemIds">Every Jellyfin playlist/collection id belonging to this list
        /// (an AllUsers playlist has one per user).</param>
        public ListOrigin(string key, string? name, BaseItemKind itemKind, IEnumerable<Guid> jellyfinItemIds)
        {
            ArgumentNullException.ThrowIfNull(jellyfinItemIds);

            Key = key;
            _itemKind = itemKind;
            _jellyfinItemIds = [.. jellyfinItemIds];

            // Stripped once here rather than per candidate inside the extraction loops.
            _baseName = NameFormatter.StripPrefixAndSuffix(name ?? string.Empty);
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
            if (!string.IsNullOrEmpty(Key)
                && string.Equals(candidate.GetProviderId(ProviderKeys.SmartLists), Key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (_jellyfinItemIds.Count > 0)
            {
                return _jellyfinItemIds.Contains(candidate.Id);
            }

            // No Jellyfin id yet (first refresh): fall back to comparing base names, but only against
            // candidates of this list's own kind. A playlist never IS a collection, so excluding a
            // same-named container of the other kind would hide something the user never asked to hide.
            return _baseName.Length > 0
                && candidate.GetBaseItemKind() == _itemKind
                && NameFormatter.StripPrefixAndSuffix(candidate.Name ?? string.Empty)
                    .Equals(_baseName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
