using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Services.Shared;
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

    /// <summary>
    /// LastPlayed scored across every user on the server: an item's sort value is the most recent
    /// date any of them played it. Everything else - container aggregation, the user-data fallback,
    /// the sort skeleton - is inherited from <see cref="LastPlayedOrderBase"/>; only the per-item
    /// value differs. With no aggregate users injected this degrades to owner-only semantics rather
    /// than scoring everything as never-played.
    /// </summary>
    public abstract class LastPlayedTotalOrderBase : LastPlayedOrderBase, IAggregateUsersOrder
    {
        private List<User> _aggregateUsers = [];

        public void SetAggregateUsers(IEnumerable<User> users)
            => _aggregateUsers = IAggregateUsersOrder.NormalizeUsers(users);

        protected override DateTime GetLastPlayedValue(
            BaseItem item,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache)
        {
            if (_aggregateUsers.Count == 0)
            {
                return base.GetLastPlayedValue(item, user, userDataManager, logger, refreshCache);
            }

            var mostRecent = DateTime.MinValue;
            foreach (var targetUser in _aggregateUsers)
            {
                try
                {
                    var candidate = GetLastPlayedForUser(item, targetUser, userDataManager, refreshCache);
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
