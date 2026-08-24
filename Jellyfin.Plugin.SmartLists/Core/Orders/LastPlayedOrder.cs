using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.Orders
{
    public class LastPlayedOrder : LastPlayedOrderBase
    {
        public override string Name => "LastPlayed (owner) Ascending";
        protected override bool IsDescending => false;
    }

    public class LastPlayedOrderDesc : LastPlayedOrderBase
    {
        public override string Name => "LastPlayed (owner) Descending";
        protected override bool IsDescending => true;
    }

    public abstract class LastPlayedTotalOrderBase : Order, IAggregateUsersOrder
    {
        private List<User> _aggregateUsers = [];
        protected abstract bool IsDescending { get; }

        public void SetAggregateUsers(IEnumerable<User> users)
        {
            ArgumentNullException.ThrowIfNull(users);

            _aggregateUsers = users
                .Where(u => u != null && u.Id != Guid.Empty)
                .GroupBy(u => u.Id)
                .Select(g => g.First())
                .ToList();
        }

        public override IEnumerable<BaseItem> OrderBy(
            IEnumerable<BaseItem> items,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            if (items == null)
            {
                return [];
            }

            if (userDataManager == null || user == null)
            {
                logger?.LogWarning("UserDataManager or User is null for LastPlayed (all users) sorting, returning unsorted items");
                return items;
            }

            try
            {
                var list = items as IList<BaseItem> ?? items.ToList();
                var sortValueCache = new Dictionary<BaseItem, DateTime>(list.Count);

                foreach (var item in list)
                {
                    sortValueCache[item] = GetMostRecentLastPlayedAcrossUsers(item, user, userDataManager, logger, refreshCache);
                }

                return IsDescending
                    ? list.OrderByDescending(item => sortValueCache[item])
                    : list.OrderBy(item => sortValueCache[item]);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error in LastPlayed (all users) sorting for user {UserId}, returning unsorted items", user.Id);
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
            if (userDataManager == null || user == null)
            {
                return DateTime.MinValue;
            }

            return GetMostRecentLastPlayedAcrossUsers(item, user, userDataManager, logger, refreshCache);
        }

        private DateTime GetMostRecentLastPlayedAcrossUsers(
            BaseItem item,
            User ownerUser,
            IUserDataManager userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache)
        {
            var targetUsers = _aggregateUsers.Count == 0 ? [ownerUser] : _aggregateUsers;
            var mostRecent = DateTime.MinValue;

            foreach (var targetUser in targetUsers)
            {
                try
                {
                    var candidate = GetLastPlayedForSingleUser(item, targetUser, userDataManager, refreshCache);
                    if (candidate > mostRecent)
                    {
                        mostRecent = candidate;
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Error getting LastPlayedDate for item {ItemName} user {UserId} while computing all-users sort", item.Name, targetUser.Id);
                }
            }

            return mostRecent;
        }

        private static DateTime GetLastPlayedForSingleUser(
            BaseItem item,
            User user,
            IUserDataManager userDataManager,
            RefreshQueueService.RefreshCache? refreshCache)
        {
            var aggregateLastPlayedDate = LastPlayedOrderBase.GetAggregateLastPlayedDate(item, user, userDataManager, refreshCache);
            if (aggregateLastPlayedDate.HasValue)
            {
                return aggregateLastPlayedDate.Value;
            }

            object? userData;
            if (refreshCache != null)
            {
                userData = UserDataCacheHelper.GetCachedUserData(user, item, refreshCache, userDataManager);
            }
            else
            {
                userData = userDataManager.GetUserData(user, item);
            }

            return LastPlayedOrderBase.GetLastPlayedDateFromUserData(userData);
        }
    }

    public class LastPlayedTotalOrder : LastPlayedTotalOrderBase
    {
        public override string Name => "LastPlayed (all users) Ascending";
        protected override bool IsDescending => false;
    }

    public class LastPlayedTotalOrderDesc : LastPlayedTotalOrderBase
    {
        public override string Name => "LastPlayed (all users) Descending";
        protected override bool IsDescending => true;
    }
}

