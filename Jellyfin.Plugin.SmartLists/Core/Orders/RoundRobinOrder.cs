using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.Orders
{
    /// <summary>
    /// Sorts items by interleaving across groups defined by a configurable field.
    /// For example, grouping by SeriesName produces: Show A Ep1, Show B Ep1, Show C Ep1, Show A Ep2, ...
    /// </summary>
    public class RoundRobinOrder : Order
    {
        public override string Name => "Round Robin Ascending";

        /// <summary>
        /// The field used to group items (e.g., "SeriesName", "AlbumName", "Artist", "Genres", "Studios").
        /// Set by SmartList before sorting.
        /// </summary>
        public string? GroupByField { get; set; }

        /// <summary>
        /// Pre-computed interleave positions for each item.
        /// Set by <see cref="PreComputePositions"/> before sorting.
        /// </summary>
        public ConcurrentDictionary<Guid, int> ItemPositions { get; set; } = new();

        /// <summary>
        /// Pre-computes interleave positions for all items.
        /// Groups items by the configured field, sorts within each group by natural order,
        /// sorts groups alphabetically, then assigns positions via round-robin interleaving.
        /// </summary>
        public void PreComputePositions(IEnumerable<BaseItem> items, bool reverseGroupOrder = false, ILogger? logger = null)
        {
            ItemPositions.Clear();

            var itemsList = items.ToList();
            if (itemsList.Count == 0 || string.IsNullOrEmpty(GroupByField))
            {
                return;
            }

            // Group items by the configured field
            var groups = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in itemsList)
            {
                var key = ExtractGroupKey(item);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new List<BaseItem>();
                    groups[key] = group;
                }

                group.Add(item);
            }

            logger?.LogDebug("RoundRobinOrder: Grouped {ItemCount} items into {GroupCount} groups by '{Field}'",
                itemsList.Count, groups.Count, GroupByField);

            // Sort items within each group by natural order (season/episode for TV, disc/track for audio, name for others)
            foreach (var kvp in groups)
            {
                kvp.Value.Sort((a, b) => CompareWithinGroup(a, b));
            }

            // Sort groups alphabetically (or reverse for descending)
            var sortedGroupKeys = reverseGroupOrder
                ? groups.Keys.OrderByDescending(k => k, OrderUtilities.SharedNaturalComparer).ToList()
                : groups.Keys.OrderBy(k => k, OrderUtilities.SharedNaturalComparer).ToList();

            // Interleave: round-robin across groups
            int position = 0;
            int maxGroupSize = groups.Values.Max(g => g.Count);

            for (int level = 0; level < maxGroupSize; level++)
            {
                foreach (var groupKey in sortedGroupKeys)
                {
                    var group = groups[groupKey];
                    if (level < group.Count)
                    {
                        ItemPositions[group[level].Id] = position++;
                    }
                }
            }

            logger?.LogDebug("RoundRobinOrder: Assigned {PositionCount} interleave positions across {GroupCount} groups",
                ItemPositions.Count, groups.Count);
        }

        public override IEnumerable<BaseItem> OrderBy(IEnumerable<BaseItem> items)
        {
            if (ItemPositions.Count == 0)
            {
                return items;
            }

            return items.OrderBy(item =>
                ItemPositions.TryGetValue(item.Id, out var pos) ? pos : int.MaxValue);
        }

        public override IEnumerable<BaseItem> OrderBy(
            IEnumerable<BaseItem> items,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
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
            return ItemPositions.TryGetValue(item.Id, out var pos) ? pos : int.MaxValue;
        }

        /// <summary>
        /// Extracts the group key from an item based on the configured GroupByField.
        /// </summary>
        internal static string ExtractGroupKey(BaseItem item, string? groupByField)
        {
            if (string.IsNullOrEmpty(groupByField))
            {
                return string.Empty;
            }

            switch (groupByField)
            {
                case "SeriesName":
                    if (item is Episode episode)
                    {
                        return episode.SeriesName ?? string.Empty;
                    }

                    return item.Name ?? string.Empty;

                case "AlbumName":
                    return item.Album ?? string.Empty;

                case "Artist":
                    if (item is Audio audio && audio.Artists != null && audio.Artists.Count > 0)
                    {
                        return audio.Artists[0];
                    }

                    return string.Empty;

                case "Genres":
                    if (item.Genres != null && item.Genres.Length > 0)
                    {
                        return item.Genres[0];
                    }

                    return string.Empty;

                case "Studios":
                    if (item.Studios != null && item.Studios.Length > 0)
                    {
                        return item.Studios[0];
                    }

                    return string.Empty;

                default:
                    return item.Name ?? string.Empty;
            }
        }

        private string ExtractGroupKey(BaseItem item)
        {
            return ExtractGroupKey(item, GroupByField);
        }

        /// <summary>
        /// Compares two items within the same group for natural ordering.
        /// Episodes sort by season then episode number, audio by disc then track, others by name.
        /// </summary>
        private static int CompareWithinGroup(BaseItem a, BaseItem b)
        {
            // Episodes: sort by season number, then episode number
            if (a is Episode && b is Episode)
            {
                var seasonCompare = OrderUtilities.GetSeasonNumber(a).CompareTo(OrderUtilities.GetSeasonNumber(b));
                if (seasonCompare != 0) return seasonCompare;
                return OrderUtilities.GetEpisodeNumber(a).CompareTo(OrderUtilities.GetEpisodeNumber(b));
            }

            // Audio: sort by disc number, then track number
            if (a is Audio && b is Audio)
            {
                var discCompare = OrderUtilities.GetDiscNumber(a).CompareTo(OrderUtilities.GetDiscNumber(b));
                if (discCompare != 0) return discCompare;
                return OrderUtilities.GetTrackNumber(a).CompareTo(OrderUtilities.GetTrackNumber(b));
            }

            // Fallback: sort by name
            return OrderUtilities.SharedNaturalComparer.Compare(
                a.SortName ?? a.Name ?? string.Empty,
                b.SortName ?? b.Name ?? string.Empty);
        }
    }

    /// <summary>
    /// Round Robin sort with groups in descending (Z→A) order.
    /// </summary>
    public class RoundRobinOrderDesc : Order
    {
        public override string Name => "Round Robin Descending";

        /// <inheritdoc cref="RoundRobinOrder.GroupByField"/>
        public string? GroupByField { get; set; }

        /// <inheritdoc cref="RoundRobinOrder.ItemPositions"/>
        public ConcurrentDictionary<Guid, int> ItemPositions { get; set; } = new();

        /// <summary>
        /// Pre-computes interleave positions with groups in reverse alphabetical order.
        /// </summary>
        public void PreComputePositions(IEnumerable<BaseItem> items, ILogger? logger = null)
        {
            // Delegate to the ascending order's algorithm with reverse flag
            var ascending = new RoundRobinOrder { GroupByField = GroupByField };
            ascending.PreComputePositions(items, reverseGroupOrder: true, logger: logger);
            ItemPositions = ascending.ItemPositions;
        }

        public override IEnumerable<BaseItem> OrderBy(IEnumerable<BaseItem> items)
        {
            if (ItemPositions.Count == 0)
            {
                return items;
            }

            return items.OrderBy(item =>
                ItemPositions.TryGetValue(item.Id, out var pos) ? pos : int.MaxValue);
        }

        public override IEnumerable<BaseItem> OrderBy(
            IEnumerable<BaseItem> items,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
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
            return ItemPositions.TryGetValue(item.Id, out var pos) ? pos : int.MaxValue;
        }
    }
}
