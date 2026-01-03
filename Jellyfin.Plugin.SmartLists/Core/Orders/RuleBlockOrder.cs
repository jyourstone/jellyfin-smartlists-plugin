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
    /// Sorts items by their rule block (OR group) order.
    /// Items from the first rule block appear before items from the second block, etc.
    /// If an item matches multiple blocks, the lowest block number is used.
    /// </summary>
    public class RuleBlockOrder : Order
    {
        public override string Name => "Rule Block Order Ascending";

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

            // Sort by lowest matching group index
            return items.OrderBy(item =>
            {
                if (GroupMappings.TryGetValue(item.Id, out var groups) && groups.Count > 0)
                {
                    return groups.Min(); // Use lowest group index if item matches multiple groups
                }
                return int.MaxValue; // Items with no group mapping go last
            });
        }

        public override IEnumerable<BaseItem> OrderBy(
            IEnumerable<BaseItem> items,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            // Rule block ordering doesn't depend on user context
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
            if (GroupMappings.TryGetValue(item.Id, out var groups) && groups.Count > 0)
            {
                return groups.Min(); // Use lowest group index
            }
            return int.MaxValue; // Items with no group mapping sort last
        }
    }

    /// <summary>
    /// Sorts items by their rule block (OR group) order in descending order.
    /// Items from the last rule block appear first.
    /// </summary>
    public class RuleBlockOrderDesc : Order
    {
        public override string Name => "Rule Block Order Descending";

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

            // Sort by lowest matching group index in descending order
            return items.OrderByDescending(item =>
            {
                if (GroupMappings.TryGetValue(item.Id, out var groups) && groups.Count > 0)
                {
                    return groups.Min(); // Use lowest group index if item matches multiple groups
                }
                return int.MinValue; // Items with no group mapping go last
            });
        }

        public override IEnumerable<BaseItem> OrderBy(
            IEnumerable<BaseItem> items,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            // Rule block ordering doesn't depend on user context
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
            if (GroupMappings.TryGetValue(item.Id, out var groups) && groups.Count > 0)
            {
                // For descending order in multi-sort, we negate the value.
                // This ensures higher group indices sort first when used with OrderByDescending.
                // Example: group 0 → 0, group 1 → -1, group 2 → -2
                // With OrderByDescending: 0 > -1 > -2, so group 0 appears first (descending behavior).
                return -groups.Min();
            }
            // Items with no group mapping get int.MinValue (-2147483648), which is lower than
            // any negated group index, ensuring they sort last when OrderByDescending is applied.
            return int.MinValue;
        }
    }
}
