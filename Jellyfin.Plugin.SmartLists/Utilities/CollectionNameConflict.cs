using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.Abstractions;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Finds the collection, if any, whose name would land on the same folder as a candidate name.
    /// Every path that saves a collection - create, rename, and playlist-to-collection conversion, on
    /// both the admin and the user API - goes through here, so the checks cannot drift apart.
    /// </summary>
    public static class CollectionNameConflict
    {
        /// <summary>
        /// Returns the existing collection that would share a folder with <paramref name="candidateFormattedName"/>,
        /// or null when there is none.
        /// </summary>
        /// <param name="collectionStore">Store to scan.</param>
        /// <param name="candidateFormattedName">The candidate name, already run through <see cref="NameFormatter"/>.</param>
        /// <param name="selfListId">Id of the list being saved, excluded from the match so a list never conflicts with itself.</param>
        public static async Task<SmartCollectionDto?> FindAsync(
            ISmartListStore<SmartCollectionDto> collectionStore,
            string candidateFormattedName,
            string? selfListId)
        {
            ArgumentNullException.ThrowIfNull(collectionStore);

            var allCollections = await collectionStore.GetAllAsync().ConfigureAwait(false);

            // Compare ids as Guids: a restored backup or a client-supplied body can use another valid
            // representation, and the sanitized-name match means a self-rename (A:B -> A?B) now collides
            // with itself, so this exclusion is what keeps a valid rename from being called a duplicate.
            var selfId = Guid.TryParse(selfListId, out var parsedSelfId) ? parsedSelfId : Guid.Empty;

            return allCollections.FirstOrDefault(c =>
                !(Guid.TryParse(c.Id, out var otherId) && otherId == selfId) &&
                InputValidator.NamesResolveToSameFolder(NameFormatter.FormatPlaylistName(c.Name), candidateFormattedName));
        }
    }
}
