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
    /// Base class for all round-robin sort orders.
    /// Groups items by a configurable field and interleaves them round-robin;
    /// subclasses control only the group ordering strategy via <see cref="OrderGroupKeys"/>.
    /// </summary>
    public abstract class RoundRobinBase : Order
    {
        /// <summary>
        /// The field used to group items (e.g., "SeriesName", "AlbumName", "Artist", "Genres", "Studios").
        /// Set by SmartList before sorting.
        /// </summary>
        public string? GroupByField { get; set; }

        /// <summary>
        /// When true, items within each group are shuffled instead of sorted in natural order.
        /// </summary>
        protected virtual bool ShuffleWithinGroups => false;

        /// <summary>
        /// Pre-computed interleave positions for each item.
        /// Set by <see cref="PreComputePositions"/> before sorting.
        /// </summary>
        public ConcurrentDictionary<Guid, int> ItemPositions { get; set; } = new();

        /// <summary>
        /// Determines how group keys are ordered before interleaving.
        /// </summary>
        protected abstract List<string> OrderGroupKeys(IEnumerable<string> keys);

        /// <summary>
        /// Pre-computes interleave positions for all items using the subclass group ordering strategy.
        /// Groups items by the configured field, orders items within each group by natural order
        /// (or shuffles them when <see cref="ShuffleWithinGroups"/> is true),
        /// orders groups via <see cref="OrderGroupKeys"/>, then assigns positions via round-robin interleaving.
        /// </summary>
        public void PreComputePositions(IEnumerable<BaseItem> items, ILogger? logger = null)
        {
            ItemPositions = BuildInterleavedPositions(items, GroupByField, OrderGroupKeys, Name, logger, ShuffleWithinGroups);
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
        /// Shared algorithm for all round-robin variants: groups items, orders items within each
        /// group by natural order (or shuffles them when <paramref name="shuffleWithinGroups"/> is true),
        /// orders groups via the supplied strategy, then interleaves round-robin.
        /// </summary>
        internal static ConcurrentDictionary<Guid, int> BuildInterleavedPositions(
            IEnumerable<BaseItem> items,
            string? groupByField,
            Func<IEnumerable<string>, List<string>> orderGroupKeys,
            string logPrefix,
            ILogger? logger,
            bool shuffleWithinGroups = false)
        {
            var positions = new ConcurrentDictionary<Guid, int>();

            var itemsList = items.ToList();
            if (itemsList.Count == 0 || string.IsNullOrEmpty(groupByField))
            {
                if (itemsList.Count > 0)
                {
                    logger?.LogWarning("{LogPrefix}: no GroupByField configured - items returned in original order", logPrefix);
                }

                return positions;
            }

            var groups = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in itemsList)
            {
                var key = ExtractGroupKey(item, groupByField);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new List<BaseItem>();
                    groups[key] = group;
                }

                group.Add(item);
            }

            logger?.LogDebug("{LogPrefix}: Grouped {ItemCount} items into {GroupCount} groups by '{Field}'",
                logPrefix, itemsList.Count, groups.Count, groupByField);

            foreach (var kvp in groups)
            {
                if (shuffleWithinGroups)
                {
                    Shuffle(kvp.Value, Random.Shared);
                }
                else
                {
                    kvp.Value.Sort((a, b) => CompareWithinGroup(a, b));
                }
            }

            var orderedKeys = orderGroupKeys(groups.Keys);

            int position = 0;
            int maxGroupSize = groups.Values.Max(g => g.Count);

            for (int level = 0; level < maxGroupSize; level++)
            {
                foreach (var groupKey in orderedKeys)
                {
                    var group = groups[groupKey];
                    if (level < group.Count)
                    {
                        positions[group[level].Id] = position++;
                    }
                }
            }

            logger?.LogDebug("{LogPrefix}: Assigned {PositionCount} interleave positions across {GroupCount} groups",
                logPrefix, positions.Count, groups.Count);

            return positions;
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

        /// <summary>
        /// Compares two items within the same group for natural ordering.
        /// Episodes sort by season then episode number, audio by disc then track, others by name.
        /// </summary>
        internal static int CompareWithinGroup(BaseItem a, BaseItem b)
        {
            if (a is Episode && b is Episode)
            {
                var seasonCompare = OrderUtilities.GetSeasonNumber(a).CompareTo(OrderUtilities.GetSeasonNumber(b));
                if (seasonCompare != 0) return seasonCompare;
                return OrderUtilities.GetEpisodeNumber(a).CompareTo(OrderUtilities.GetEpisodeNumber(b));
            }

            if (a is Audio && b is Audio)
            {
                var discCompare = OrderUtilities.GetDiscNumber(a).CompareTo(OrderUtilities.GetDiscNumber(b));
                if (discCompare != 0) return discCompare;
                return OrderUtilities.GetTrackNumber(a).CompareTo(OrderUtilities.GetTrackNumber(b));
            }

            return OrderUtilities.SharedNaturalComparer.Compare(
                a.SortName ?? a.Name ?? string.Empty,
                b.SortName ?? b.Name ?? string.Empty);
        }

        /// <summary>
        /// Fisher-Yates in-place shuffle.
        /// </summary>
        internal static void Shuffle<T>(IList<T> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
#pragma warning disable CA5394
                int j = rng.Next(i + 1);
#pragma warning restore CA5394
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }

    /// <summary>
    /// Sorts items by interleaving across groups defined by a configurable field.
    /// Groups are ordered alphabetically (A→Z).
    /// For example, grouping by SeriesName produces: Show A Ep1, Show B Ep1, Show C Ep1, Show A Ep2, ...
    /// </summary>
    public class RoundRobinOrder : RoundRobinBase
    {
        public override string Name => "Round Robin Ascending";

        protected override List<string> OrderGroupKeys(IEnumerable<string> keys)
        {
            return keys.OrderBy(k => k, OrderUtilities.SharedNaturalComparer).ToList();
        }
    }

    /// <summary>
    /// Round Robin sort with groups in descending (Z→A) order.
    /// </summary>
    public class RoundRobinOrderDesc : RoundRobinBase
    {
        public override string Name => "Round Robin Descending";

        protected override List<string> OrderGroupKeys(IEnumerable<string> keys)
        {
            return keys.OrderByDescending(k => k, OrderUtilities.SharedNaturalComparer).ToList();
        }
    }

    /// <summary>
    /// Round Robin sort with groups in random order. Each refresh produces a different
    /// group interleaving while preserving natural order within each group.
    /// </summary>
    public class RoundRobinRandomOrder : RoundRobinBase
    {
        public override string Name => "Random Round Robin";

        protected override List<string> OrderGroupKeys(IEnumerable<string> keys)
        {
            var list = keys.ToList();
            Shuffle(list, Random.Shared);
            return list;
        }
    }

    /// <summary>
    /// Round Robin sort with groups in random order AND items shuffled within each group.
    /// Each refresh produces a fully random rotation: random group interleaving and
    /// random order inside every group ("turning on a TV at a random time").
    /// </summary>
    public class RoundRobinShuffledOrder : RoundRobinRandomOrder
    {
        public override string Name => "Shuffled Round Robin";

        protected override bool ShuffleWithinGroups => true;
    }
}
