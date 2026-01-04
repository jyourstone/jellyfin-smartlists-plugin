using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.Orders
{
    /// <summary>
    /// Interleaves items from different rule blocks in a round-robin pattern.
    /// Items are distributed evenly: one from block 1, one from block 2, one from block 3, repeat.
    /// Perfect for creating playlists where you want content from different groups to alternate,
    /// like bumpers between episodes or mixing different genres.
    /// 
    /// Note: There is intentional code duplication between this class and RuleBlockOrderInterleavedDesc
    /// to keep the implementation clear and maintainable. The core interleaving algorithms differ
    /// (ascending vs descending iteration), and the shared grouping logic is minimal.
    /// </summary>
    public class RuleBlockOrderInterleaved : Order
    {
        public override string Name => "Rule Block Order Interleaved";

        /// <summary>
        /// Dictionary mapping item IDs to their matching rule group indices.
        /// Set by SmartList before sorting.
        /// </summary>
        public ConcurrentDictionary<Guid, List<int>> GroupMappings { get; set; } = new();

        public override IEnumerable<BaseItem> OrderBy(IEnumerable<BaseItem> items)
        {
            if (GroupMappings.Count == 0)
            {
                // No group mappings available, return items unsorted
                return items;
            }

            // Group items by their lowest matching group index
            var itemsByGroup = new Dictionary<int, List<BaseItem>>();
            
            foreach (var item in items)
            {
                if (GroupMappings.TryGetValue(item.Id, out var groups) && groups.Count > 0)
                {
                    var lowestGroup = groups.Min();
                    if (!itemsByGroup.TryGetValue(lowestGroup, out var groupList))
                    {
                        groupList = new List<BaseItem>();
                        itemsByGroup[lowestGroup] = groupList;
                    }
                    groupList.Add(item);
                }
            }

            // Sort group indices to ensure consistent order
            var sortedGroupIndices = itemsByGroup.Keys.OrderBy(k => k).ToList();

            // Interleave items from each group in round-robin fashion
            var result = new List<BaseItem>();
            var groupIterators = new Dictionary<int, int>();
            
            // Initialize iterators for each group
            foreach (var groupIndex in sortedGroupIndices)
            {
                groupIterators[groupIndex] = 0;
            }

            // Keep going until all groups are exhausted
            var hasMoreItems = true;
            while (hasMoreItems)
            {
                hasMoreItems = false;
                
                // Take one item from each group in order
                foreach (var groupIndex in sortedGroupIndices)
                {
                    var groupItems = itemsByGroup[groupIndex];
                    var iterator = groupIterators[groupIndex];
                    
                    if (iterator < groupItems.Count)
                    {
                        result.Add(groupItems[iterator]);
                        groupIterators[groupIndex]++;
                        hasMoreItems = true;
                    }
                }
            }

            return result;
        }

        public override IEnumerable<BaseItem> OrderBy(
            IEnumerable<BaseItem> items,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            // Interleaving doesn't depend on user context
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
            // For interleaved ordering, we can't use a simple sort key
            // The interleaving logic requires the full collection
            // So we fall back to using the lowest group index as the key
            // This won't produce perfect interleaving in multi-sort scenarios,
            // but Rule Block Order Interleaved is meant to be used as the primary sort
            if (GroupMappings.TryGetValue(item.Id, out var groups) && groups.Count > 0)
            {
                return groups.Min();
            }
            return int.MaxValue;
        }
    }

    /// <summary>
    /// Interleaves items from different rule blocks in a round-robin pattern, starting from the last block.
    /// Items are distributed evenly in reverse block order: one from last block, one from second-to-last, etc.
    /// The order of items within each block is preserved (respects secondary sorts).
    /// Perfect for creating playlists where you want later rule blocks to have priority while maintaining alternation.
    /// </summary>
    public class RuleBlockOrderInterleavedDesc : Order
    {
        public override string Name => "Rule Block Order Interleaved Descending";

        /// <summary>
        /// Dictionary mapping item IDs to their matching rule group indices.
        /// Set by SmartList before sorting.
        /// </summary>
        public ConcurrentDictionary<Guid, List<int>> GroupMappings { get; set; } = new();

        public override IEnumerable<BaseItem> OrderBy(IEnumerable<BaseItem> items)
        {
            if (GroupMappings.Count == 0)
            {
                // No group mappings available, return items unsorted
                return items;
            }

            // Group items by their lowest matching group index
            var itemsByGroup = new Dictionary<int, List<BaseItem>>();
            
            foreach (var item in items)
            {
                if (GroupMappings.TryGetValue(item.Id, out var groups) && groups.Count > 0)
                {
                    var lowestGroup = groups.Min();
                    if (!itemsByGroup.TryGetValue(lowestGroup, out var groupList))
                    {
                        groupList = new List<BaseItem>();
                        itemsByGroup[lowestGroup] = groupList;
                    }
                    groupList.Add(item);
                }
            }

            // Sort group indices in descending order (reverse of block order)
            var sortedGroupIndices = itemsByGroup.Keys.OrderByDescending(k => k).ToList();

            // Interleave items from each group in round-robin fashion
            // Note: "Descending" only affects block order, not iteration within blocks
            var result = new List<BaseItem>();
            var groupIterators = new Dictionary<int, int>();
            
            // Initialize iterators for each group to start from the beginning
            foreach (var groupIndex in sortedGroupIndices)
            {
                groupIterators[groupIndex] = 0;
            }

            // Keep going until all groups are exhausted
            var hasMoreItems = true;
            while (hasMoreItems)
            {
                hasMoreItems = false;
                
                // Take one item from each group in order
                foreach (var groupIndex in sortedGroupIndices)
                {
                    var groupItems = itemsByGroup[groupIndex];
                    var iterator = groupIterators[groupIndex];
                    
                    if (iterator < groupItems.Count)
                    {
                        result.Add(groupItems[iterator]);
                        groupIterators[groupIndex]++;
                        hasMoreItems = true;
                    }
                }
            }

            return result;
        }

        public override IEnumerable<BaseItem> OrderBy(
            IEnumerable<BaseItem> items,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            // Interleaving doesn't depend on user context
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
            // For interleaved ordering, we can't use a simple sort key
            // The interleaving logic requires the full collection
            // So we fall back to using the lowest group index as the key
            // This won't produce perfect interleaving in multi-sort scenarios,
            // but Rule Block Order Interleaved is meant to be used as the primary sort
            if (GroupMappings.TryGetValue(item.Id, out var groups) && groups.Count > 0)
            {
                return groups.Min();
            }
            return int.MaxValue;
        }
    }
}
