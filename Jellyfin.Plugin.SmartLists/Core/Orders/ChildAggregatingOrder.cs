using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.Orders
{
    /// <summary>
    /// Wrapper order that aggregates sort values from child items for Collections and Playlists.
    /// When the item being sorted is a Collection (BoxSet) or Playlist, this order will
    /// retrieve all child items and aggregate their values using the specified aggregation function.
    /// Falls back to the item's own value if it's not a container or has no children.
    ///
    /// Supported sort fields: ProductionYear, CommunityRating, DateCreated, ReleaseDate
    /// </summary>
    public class ChildAggregatingOrder : Order
    {
        private readonly Order _innerOrder;
        private readonly bool _isDescending;
        private readonly string _sortField;
        private readonly int _recursionDepth;

        public override string Name => _innerOrder.Name + " (Child Aggregate)";

        /// <summary>
        /// Initializes a new instance of the <see cref="ChildAggregatingOrder"/> class
        /// that wraps the given inner order for child value aggregation.
        /// </summary>
        /// <param name="innerOrder">The base order to wrap (e.g., DateCreatedOrder).</param>
        /// <param name="isDescending">Whether the sort is descending.</param>
        /// <param name="sortField">The field being sorted (ProductionYear, CommunityRating, DateCreated, ReleaseDate).</param>
        /// <param name="recursionDepth">How many levels deep to traverse nested collections/playlists (0-10, default 0). 0 means no recursion.</param>
        public ChildAggregatingOrder(Order innerOrder, bool isDescending, string sortField, int recursionDepth = 0)
        {
            _innerOrder = innerOrder ?? throw new ArgumentNullException(nameof(innerOrder));
            _isDescending = isDescending;
            _sortField = sortField ?? throw new ArgumentNullException(nameof(sortField));
            _recursionDepth = Math.Max(0, Math.Min(10, recursionDepth));
        }

        /// <summary>
        /// Gets whether this order sorts in descending direction.
        /// Used by SmartList.IsDescendingOrder() to determine sort direction.
        /// </summary>
        public bool IsDescending => _isDescending;

        public override IComparable GetSortKey(
            BaseItem item,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            Dictionary<Guid, int>? itemRandomKeys = null,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            // Check if item is a Collection (BoxSet) or Playlist and recursion is enabled
            // _recursionDepth of 0 means no child traversal - just use the item's own value
            if (_recursionDepth > 0 && IsCollectionOrPlaylist(item) && refreshCache != null)
            {
                var childItems = GetChildItemsRecursive(item, user, refreshCache, logger, 1, _recursionDepth, []);

                if (childItems != null && childItems.Length > 0)
                {
                    var aggregatedValue = AggregateChildValues(childItems, user, userDataManager, logger, refreshCache);
                    if (aggregatedValue != null)
                    {
                        logger?.LogDebug("ChildAggregatingOrder: '{ItemName}' aggregated {SortField} from {ChildCount} children: {AggregatedValue}",
                            item.Name, _sortField, childItems.Length, aggregatedValue);
                        return aggregatedValue;
                    }
                }
            }

            // Fallback to item's own value using the inner order
            return _innerOrder.GetSortKey(item, user, userDataManager, logger, itemRandomKeys, refreshCache);
        }

        public override IEnumerable<BaseItem> OrderBy(IEnumerable<BaseItem> items)
        {
            // For simple OrderBy without context, delegate to inner order
            return _innerOrder.OrderBy(items);
        }

        public override IEnumerable<BaseItem> OrderBy(
            IEnumerable<BaseItem> items,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            if (items == null) return [];

            // Use GetSortKey for sorting to leverage child value aggregation
            var itemsList = items.ToList();

            var itemsWithKeys = itemsList.Select(item => new
            {
                Item = item,
                SortKey = GetSortKey(item, user, userDataManager, logger, null, refreshCache)
            }).ToList();

            return _isDescending
                ? itemsWithKeys.OrderByDescending(x => x.SortKey).Select(x => x.Item)
                : itemsWithKeys.OrderBy(x => x.SortKey).Select(x => x.Item);
        }

        /// <summary>
        /// Determines if the item is a Collection (BoxSet) or Playlist.
        /// </summary>
        private static bool IsCollectionOrPlaylist(BaseItem item)
        {
            if (item == null) return false;

            var itemKind = item.GetBaseItemKind();
            return itemKind == BaseItemKind.BoxSet || itemKind == BaseItemKind.Playlist;
        }

        /// <summary>
        /// Gets child items recursively for a Collection or Playlist, traversing nested collections/playlists
        /// up to the specified depth.
        /// </summary>
        /// <param name="item">The collection or playlist to get children from.</param>
        /// <param name="user">The user context.</param>
        /// <param name="refreshCache">Cache for child items.</param>
        /// <param name="logger">Logger for debugging.</param>
        /// <param name="currentDepth">Current recursion depth (starts at 1).</param>
        /// <param name="maxDepth">Maximum depth to traverse.</param>
        /// <param name="visitedIds">Set of already visited item IDs to prevent circular references.</param>
        /// <returns>Array of all child items found up to maxDepth.</returns>
        private static BaseItem[] GetChildItemsRecursive(
            BaseItem item,
            User user,
            RefreshQueueService.RefreshCache refreshCache,
            ILogger? logger,
            int currentDepth,
            int maxDepth,
            HashSet<Guid> visitedIds)
        {
            // Circular reference protection
            if (visitedIds.Contains(item.Id))
            {
                logger?.LogDebug("ChildAggregatingOrder: Circular reference detected for '{ItemName}', skipping", item.Name);
                return [];
            }
            visitedIds.Add(item.Id);

            // Get direct children
            var directChildren = GetDirectChildItems(item, user, refreshCache, logger);
            if (directChildren == null || directChildren.Length == 0)
            {
                return [];
            }

            // If we're at max depth or depth is 1, just return direct children
            if (currentDepth >= maxDepth)
            {
                return directChildren;
            }

            // Otherwise, recursively get children from nested collections/playlists
            var allChildren = new List<BaseItem>(directChildren);

            foreach (var child in directChildren)
            {
                if (IsCollectionOrPlaylist(child))
                {
                    var nestedChildren = GetChildItemsRecursive(
                        child,
                        user,
                        refreshCache,
                        logger,
                        currentDepth + 1,
                        maxDepth,
                        visitedIds);

                    allChildren.AddRange(nestedChildren);
                }
            }

            return [.. allChildren];
        }

        /// <summary>
        /// Gets direct child items for a Collection or Playlist using the cache or reflection.
        /// </summary>
        private static BaseItem[]? GetDirectChildItems(BaseItem item, User user, RefreshQueueService.RefreshCache refreshCache, ILogger? logger)
        {
            var itemId = item.Id;
            var itemKind = item.GetBaseItemKind();

            // Check cache first
            if (itemKind == BaseItemKind.BoxSet && refreshCache.CollectionChildItems.TryGetValue(itemId, out var cachedCollectionItems))
            {
                return cachedCollectionItems;
            }
            if (itemKind == BaseItemKind.Playlist && refreshCache.PlaylistChildItems.TryGetValue(itemId, out var cachedPlaylistItems))
            {
                return cachedPlaylistItems;
            }

            // Retrieve children using reflection (same approach as Factory.cs)
            BaseItem[]? childItems = null;

            try
            {
                // Approach 1: Try GetChildren method
                var getChildrenMethod = item.GetType().GetMethod("GetChildren", [typeof(User), typeof(bool)]);
                if (getChildrenMethod != null)
                {
                    var children = getChildrenMethod.Invoke(item, [user, true]);
                    if (children is IEnumerable<BaseItem> childrenEnumerable)
                    {
                        childItems = [.. childrenEnumerable];
                        logger?.LogDebug("ChildAggregatingOrder: '{ItemName}' GetChildren() returned {Count} items", item.Name, childItems.Length);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "ChildAggregatingOrder: GetChildren failed for '{ItemName}'", item.Name);
            }

            // Approach 2: Try GetLinkedChildren method
            if (childItems == null || childItems.Length == 0)
            {
                try
                {
                    var getLinkedChildrenMethod = item.GetType().GetMethod("GetLinkedChildren", Type.EmptyTypes);
                    if (getLinkedChildrenMethod != null)
                    {
                        var linkedChildren = getLinkedChildrenMethod.Invoke(item, null);
                        if (linkedChildren is IEnumerable<BaseItem> linkedEnumerable)
                        {
                            childItems = [.. linkedEnumerable];
                            var childNames = string.Join(", ", childItems.Select(c => c.Name + " (" + c.ProductionYear + ")"));
                            logger?.LogDebug("ChildAggregatingOrder: '{ItemName}' GetLinkedChildren() returned {Count} items: [{ChildNames}]", item.Name, childItems.Length, childNames);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "ChildAggregatingOrder: GetLinkedChildren failed for '{ItemName}'", item.Name);
                }
            }

            // Cache the result
            childItems ??= [];
            if (itemKind == BaseItemKind.BoxSet)
            {
                refreshCache.CollectionChildItems[itemId] = childItems;
            }
            else if (itemKind == BaseItemKind.Playlist)
            {
                refreshCache.PlaylistChildItems[itemId] = childItems;
            }

            return childItems;
        }

        /// <summary>
        /// Aggregates child item values based on the sort field and sort direction.
        /// Uses Min() for ascending sorts (earliest/lowest first) and Max() for descending sorts (latest/highest first).
        /// This ensures consistent ordering when collections have overlapping value ranges.
        /// </summary>
        private IComparable? AggregateChildValues(
            BaseItem[] childItems,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache)
        {
            if (childItems == null || childItems.Length == 0) return null;

            switch (_sortField)
            {
                case "ProductionYear":
                    var years = childItems
                        .Select(c => c.ProductionYear ?? 0)
                        .Where(y => y > 0)
                        .ToList();
                    if (years.Count == 0) return null;
                    // For ascending, use Min (earliest year first); for descending, use Max (latest year first)
                    return _isDescending ? years.Max() : years.Min();

                case "CommunityRating":
                    var ratings = childItems
                        .Select(c => c.CommunityRating ?? 0f)
                        .Where(r => r > 0)
                        .ToList();
                    if (ratings.Count == 0) return null;
                    // For ascending, use Min (lowest rating first); for descending, use Max (highest rating first)
                    return _isDescending ? ratings.Max() : ratings.Min();

                case "DateCreated":
                    var createdDates = childItems
                        .Select(c => c.DateCreated)
                        .Where(d => d > DateTime.MinValue)
                        .ToList();
                    if (createdDates.Count == 0) return null;
                    // For ascending, use Min (oldest first); for descending, use Max (newest first)
                    return _isDescending ? createdDates.Max() : createdDates.Min();

                case "ReleaseDate":
                    var releaseDates = childItems
                        .Select(c => OrderUtilities.GetReleaseDate(c))
                        .Where(d => d > DateTime.MinValue)
                        .ToList();
                    // Return as ComparableTuple4 for consistency with ReleaseDateOrder.GetSortKey
                    // Use (ticks, 1, 0, 0) to place aggregated collections after episodes on same date
                    if (releaseDates.Count > 0)
                    {
                        // For ascending, use Min (earliest first); for descending, use Max (latest first)
                        var aggregatedDate = _isDescending ? releaseDates.Max() : releaseDates.Min();
                        return new ComparableTuple4<long, int, int, int>(aggregatedDate.Date.Ticks, 1, 0, 0);
                    }
                    return null;

                default:
                    logger?.LogWarning("ChildAggregatingOrder: Unsupported sort field '{SortField}'", _sortField);
                    return null;
            }
        }

        /// <summary>
        /// Supported sort fields for child value aggregation.
        /// </summary>
        public static readonly string[] SupportedSortFields = ["ProductionYear", "CommunityRating", "DateCreated", "ReleaseDate"];

        /// <summary>
        /// Checks if the given sort field supports child value aggregation.
        /// </summary>
        public static bool IsSupportedSortField(string sortField)
        {
            return Array.Exists(SupportedSortFields, f => f.Equals(sortField, StringComparison.OrdinalIgnoreCase));
        }
    }
}
