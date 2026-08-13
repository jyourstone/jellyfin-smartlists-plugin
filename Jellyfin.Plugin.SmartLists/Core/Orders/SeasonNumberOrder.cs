using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.Orders
{
    public class SeasonNumberOrder : Order
    {
        public override string Name => "SeasonNumber Ascending";

        public override IEnumerable<BaseItem> OrderBy(IEnumerable<BaseItem> items)
        {
            if (items == null) return [];

            // Sort by Season Number -> Episode Number -> Name
            return items
                .OrderBy(item => OrderUtilities.GetSeasonNumber(item))
                .ThenBy(item => OrderUtilities.GetEpisodeNumber(item))
                .ThenBy(item => item.Name ?? "", OrderUtilities.SharedNaturalComparer);
        }

        public override IEnumerable<BaseItem> OrderBy(
            IEnumerable<BaseItem> items,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            // refreshCache not used for season number ordering
            return OrderBy(items);
        }

        public override IComparable GetSortKey(
            BaseItem item,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            Dictionary<Guid, int>? itemRandomKeys = null,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            // Return composite key matching OrderBy logic: SeasonNumber -> EpisodeNumber -> Name.
            // ICompositeSortKey means a non-final SeasonNumber sort strips back to the season
            // alone, letting the user's own secondary sort order episodes within a season.
            var seasonNumber = OrderUtilities.GetSeasonNumber(item);
            var episodeNumber = OrderUtilities.GetEpisodeNumber(item);
            var name = item.Name ?? "";
            return new ComparableTuple4<int, int, string, string>(
                seasonNumber,
                episodeNumber,
                name,
                "", // Fourth element not used, but ComparableTuple4 requires 4 elements
                comparer3: OrderUtilities.SharedNaturalComparer);
        }
    }

    public class SeasonNumberOrderDesc : Order
    {
        public override string Name => "SeasonNumber Descending";

        public override IEnumerable<BaseItem> OrderBy(IEnumerable<BaseItem> items)
        {
            if (items == null) return [];

            // Sort by Season Number (descending) -> Episode Number (descending) -> Name (descending)
            return items
                .OrderByDescending(item => OrderUtilities.GetSeasonNumber(item))
                .ThenByDescending(item => OrderUtilities.GetEpisodeNumber(item))
                .ThenByDescending(item => item.Name ?? "", OrderUtilities.SharedNaturalComparer);
        }

        public override IEnumerable<BaseItem> OrderBy(
            IEnumerable<BaseItem> items,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            // refreshCache not used for season number ordering
            return OrderBy(items);
        }

        public override IComparable GetSortKey(
            BaseItem item,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            Dictionary<Guid, int>? itemRandomKeys = null,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            // Return composite key matching OrderBy logic: SeasonNumber -> EpisodeNumber -> Name.
            // NOTE: Do NOT negate values here! The sorting direction is controlled by
            // OrderBy vs OrderByDescending in ApplyMultipleOrders for multi-level sorting.
            var seasonNumber = OrderUtilities.GetSeasonNumber(item);
            var episodeNumber = OrderUtilities.GetEpisodeNumber(item);
            var name = item.Name ?? "";
            return new ComparableTuple4<int, int, string, string>(
                seasonNumber,
                episodeNumber,
                name,
                "", // Fourth element not used, but ComparableTuple4 requires 4 elements
                comparer3: OrderUtilities.SharedNaturalComparer);
        }
    }
}

