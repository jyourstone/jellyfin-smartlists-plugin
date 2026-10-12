using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Builds the Missing items report of a smart list from the last successful fetch of each of its external lists.
    /// </summary>
    internal static class MissingItemsReport
    {
        /// <summary>
        /// Builds the report, checking each external list against the whole server library.
        /// </summary>
        /// <param name="list">The smart list.</param>
        /// <param name="externalListService">The service holding the external list snapshots.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <returns>One entry per ExternalList "Equal" rule URL, in rule order.</returns>
        internal static List<MissingItemsListDto> Build(SmartListDto list, ExternalListService externalListService, ILibraryManager libraryManager)
        {
            var snapshots = FieldRequirements.Analyze(list.ExpressionSets).IncludedExternalListUrls
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(url => (Url: url, Snapshot: externalListService.GetSnapshot(url)))
                .ToList();

            var libraryItems = QueryLibraryIds(libraryManager, snapshots.SelectMany(s => s.Snapshot?.Result.Entries ?? []).ToList());

            return snapshots.ConvertAll(s => BuildList(s.Url, s.Snapshot, libraryItems));
        }

        private static MissingItemsListDto BuildList(string url, ExternalListSnapshot? snapshot, List<LibraryItemIds> libraryItems)
        {
            if (snapshot == null)
            {
                return new MissingItemsListDto { Url = url };
            }

            var result = snapshot.Result;
            var (missing, totalEntries) = MissingItemsCalculator.Compute(result, libraryItems);
            return new MissingItemsListDto
            {
                Url = url,
                FetchedAt = snapshot.FetchedAtUtc,
                IsMusic = result.Entries.Count == 0 && (result.MusicRecordingIds.Count > 0 || result.MusicTitleArtistIds.Count > 0),
                IsComplete = result.IsComplete,
                TotalEntries = totalEntries,
                Missing = missing,
            };
        }

        private static List<LibraryItemIds> QueryLibraryIds(ILibraryManager libraryManager, List<ExternalListEntry> entries)
        {
            var providerIds = new Dictionary<string, string[]>();
            AddIds(providerIds, nameof(MetadataProvider.Imdb), entries.Select(e => e.ImdbId));
            AddIds(providerIds, nameof(MetadataProvider.Tmdb), entries.Select(e => e.TmdbId));
            AddIds(providerIds, nameof(MetadataProvider.Tvdb), entries.Select(e => e.TvdbId));
            if (providerIds.Count == 0)
            {
                return [];
            }

            var query = new InternalItemsQuery
            {
                // Every kind an ExternalList rule can match in a refresh (MusicVideo and Video as Unknown)
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video],
                Recursive = true,
                IsVirtualItem = false,
                // Server-wide on purpose: no user, so every library
                TopParentIds = LibraryManagerHelper.GetLibraryTopParentIds(libraryManager),
                HasAnyProviderIds = providerIds,
                // The ProviderIds field is what loads the items' provider IDs
                DtoOptions = new DtoOptions(false) { Fields = [ItemFields.ProviderIds], EnableImages = false, EnableUserData = false },
            };

            return libraryManager.GetItemList(query).Select(item => new LibraryItemIds(
                OperandFactory.GetExternalListItemKind(item),
                item.GetProviderId(MetadataProvider.Imdb),
                item.GetProviderId(MetadataProvider.Tmdb),
                item.GetProviderId(MetadataProvider.Tvdb))).ToList();
        }

        private static void AddIds(Dictionary<string, string[]> providerIds, string provider, IEnumerable<string?> ids)
        {
            // An empty array would match any value of the provider, so only add providers with IDs
            var values = ids.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length > 0)
            {
                providerIds[provider] = values;
            }
        }
    }
}
