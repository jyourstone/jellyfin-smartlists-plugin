using System;
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

        public override string Name => _innerOrder.Name + " (Child Aggregate)";

        /// <summary>
        /// Initializes a new instance of the <see cref="ChildAggregatingOrder"/> class
        /// that wraps the given inner order for child value aggregation.
        /// </summary>
        /// <param name="innerOrder">The base order to wrap (e.g., DateCreatedOrder).</param>
        /// <param name="isDescending">Whether the sort is descending.</param>
        /// <param name="sortField">The field being sorted (ProductionYear, CommunityRating, DateCreated, ReleaseDate).</param>
        public ChildAggregatingOrder(Order innerOrder, bool isDescending, string sortField)
        {
            _innerOrder = innerOrder ?? throw new ArgumentNullException(nameof(innerOrder));
            _isDescending = isDescending;
            _sortField = sortField ?? throw new ArgumentNullException(nameof(sortField));
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
            // Check if item is a Collection (BoxSet) or Playlist
            if (IsCollectionOrPlaylist(item) && refreshCache != null)
            {
                var childItems = GetChildItems(item, user, refreshCache, logger);

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

            var typeName = item.GetType().Name;
            return typeName == "BoxSet" || typeName == "Playlist";
        }

        /// <summary>
        /// Gets child items for a Collection or Playlist using the cache or reflection.
        /// </summary>
        private static BaseItem[]? GetChildItems(BaseItem item, User user, RefreshQueueService.RefreshCache refreshCache, ILogger? logger)
        {
            var itemId = item.Id;
            var typeName = item.GetType().Name;

            // Check cache first
            if (typeName == "BoxSet" && refreshCache.CollectionChildItems.TryGetValue(itemId, out var cachedCollectionItems))
            {
                return cachedCollectionItems;
            }
            if (typeName == "Playlist" && refreshCache.PlaylistChildItems.TryGetValue(itemId, out var cachedPlaylistItems))
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
                            logger?.LogDebug("ChildAggregatingOrder: '{ItemName}' GetLinkedChildren() returned {Count} items", item.Name, childItems.Length);
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
            if (typeName == "BoxSet")
            {
                refreshCache.CollectionChildItems[itemId] = childItems;
            }
            else if (typeName == "Playlist")
            {
                refreshCache.PlaylistChildItems[itemId] = childItems;
            }

            return childItems;
        }

        /// <summary>
        /// Aggregates child item values based on the sort field.
        /// Uses Max() for all supported fields to get the "most recent" or "highest" value.
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
                    return years.Count > 0 ? years.Max() : (IComparable?)null;

                case "CommunityRating":
                    var ratings = childItems
                        .Select(c => c.CommunityRating ?? 0f)
                        .Where(r => r > 0)
                        .ToList();
                    return ratings.Count > 0 ? ratings.Max() : (IComparable?)null;

                case "DateCreated":
                    var createdDates = childItems
                        .Select(c => c.DateCreated)
                        .Where(d => d > DateTime.MinValue)
                        .ToList();
                    return createdDates.Count > 0 ? createdDates.Max() : (IComparable?)null;

                case "ReleaseDate":
                    var releaseDates = childItems
                        .Select(c => OrderUtilities.GetReleaseDate(c))
                        .Where(d => d > DateTime.MinValue)
                        .ToList();
                    // Return as ticks for consistency with ReleaseDateOrder
                    return releaseDates.Count > 0 ? releaseDates.Max().Ticks : (IComparable?)null;

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
