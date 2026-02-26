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
    /// Base class for LastPlayed ordering to eliminate duplication
    /// </summary>
    public abstract class LastPlayedOrderBase : Order
    {
        protected abstract bool IsDescending { get; }

        public override IEnumerable<BaseItem> OrderBy(
            IEnumerable<BaseItem> items,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            if (items == null) return [];
            if (userDataManager == null || user == null)
            {
                logger?.LogWarning("UserDataManager or User is null for LastPlayed sorting, returning unsorted items");
                return items;
            }

            try
            {
                // Pre-fetch all user data to avoid repeated database calls during sorting
                var list = items as IList<BaseItem> ?? items.ToList();
                var sortValueCache = new Dictionary<BaseItem, DateTime>(list.Count);

                foreach (var item in list)
                {
                    try
                    {
                        object? userData = null;
                        
                        // Try to get user data from cache if available
                        if (refreshCache != null && refreshCache.UserDataCache.TryGetValue((item.Id, user.Id), out var cachedUserData))
                        {
                            userData = cachedUserData;
                        }
                        else
                        {
                            userData = userDataManager.GetUserData(user, item);
                        }
                        
                        sortValueCache[item] = GetLastPlayedDateFromUserData(userData);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Error getting user data for item {ItemName} for user {UserId}", item.Name, user.Id);
                        sortValueCache[item] = DateTime.MinValue; // Default to never played
                    }
                }

                // Sort using cached DateTime values directly (no tie-breaker to avoid album grouping)
                return IsDescending
                    ? list.OrderByDescending(item => sortValueCache[item])
                    : list.OrderBy(item => sortValueCache[item]);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error in LastPlayed sorting for user {UserId}, returning unsorted items", user.Id);
                return items;
            }
        }

        public override IComparable GetSortKey(
            BaseItem item,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            Dictionary<Guid, int>? itemRandomKeys = null,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            try
            {
                object? userData = null;
                
                // Try to get user data from cache if available
                if (refreshCache != null && refreshCache.UserDataCache.TryGetValue((item.Id, user.Id), out var cachedUserData))
                {
                    userData = cachedUserData;
                }
                else if (userDataManager != null)
                {
                    userData = userDataManager.GetUserData(user, item);
                }
                
                return GetLastPlayedDateFromUserData(userData);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error getting last played date for item {ItemId} user {UserId}", item.Id, user.Id);
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// Extracts LastPlayedDate from user data, handling both DateTime and Nullable&lt;DateTime&gt;
        /// </summary>
        private static DateTime GetLastPlayedDateFromUserData(object? userData)
        {
            if (userData == null) return DateTime.MinValue;

            var lastPlayedProp = userData.GetType().GetProperty("LastPlayedDate");
            if (lastPlayedProp == null) return DateTime.MinValue;

            var lastPlayedValue = lastPlayedProp.GetValue(userData);

            // Handle non-nullable DateTime
            if (lastPlayedValue is DateTime dt && dt != DateTime.MinValue)
            {
                return dt;
            }

            // Handle nullable DateTime?
            if (lastPlayedValue != null)
            {
                var valueType = lastPlayedValue.GetType();
                if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    var hasValueProp = valueType.GetProperty("HasValue");
                    var valueProp = valueType.GetProperty("Value");

                    if (hasValueProp?.GetValue(lastPlayedValue) is true)
                    {
                        if (valueProp?.GetValue(lastPlayedValue) is DateTime nullableDt && nullableDt != DateTime.MinValue)
                        {
                            return nullableDt;
                        }
                    }
                }
            }

            return DateTime.MinValue;
        }
    }
}

