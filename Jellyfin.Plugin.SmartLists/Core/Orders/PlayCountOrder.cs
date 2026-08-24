using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.Orders
{
    public interface IAggregateUsersOrder
    {
        void SetAggregateUsers(IEnumerable<User> users);
    }

    public abstract class PlayCountTotalOrderBase : UserDataOrder, IAggregateUsersOrder
    {
        private List<User> _aggregateUsers = [];

        public void SetAggregateUsers(IEnumerable<User> users)
        {
            ArgumentNullException.ThrowIfNull(users);

            _aggregateUsers = users
                .Where(u => u != null && u.Id != Guid.Empty)
                .GroupBy(u => u.Id)
                .Select(g => g.First())
                .ToList();
        }

        protected int GetTotalPlayCountAcrossUsers(
            BaseItem item,
            User currentUser,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache)
        {
            // Safe fallback: if no aggregate users were configured, preserve owner semantics.
            if (_aggregateUsers.Count == 0)
            {
                return PlayCountOrder.GetPlayCountFromUserData(item, currentUser, userDataManager, logger, refreshCache);
            }

            int total = 0;
            foreach (var targetUser in _aggregateUsers)
            {
                total += PlayCountOrder.GetPlayCountFromUserData(item, targetUser, userDataManager, logger, refreshCache);
            }

            return total;
        }
    }

    public class PlayCountOrder : UserDataOrder
    {
        public override string Name => "PlayCount (owner) Ascending";
        protected override bool IsDescending => false;

        protected override int GetUserDataValue(
            BaseItem item,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            return GetPlayCountFromUserData(item, user, userDataManager, logger, refreshCache);
        }

        /// <summary>
        /// Shared logic for extracting PlayCount from user data.
        /// For aggregate items, calculates the minimum PlayCount across all cached child media.
        /// </summary>
        public static int GetPlayCountFromUserData(
            BaseItem item,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(user);
            try
            {
                // For aggregate items, calculate from child media when a prior filter populated the cache.
                if (userDataManager != null && refreshCache != null)
                {
                    var children = TryGetAggregateChildren(item, user, refreshCache);
                    if (children != null)
                    {
                        return CalculateMinPlayCountFromTracks(children, user, userDataManager, refreshCache);
                    }
                }

                object? userData = null;

                // Try to get user data from cache if available
                if (refreshCache != null && userDataManager != null)
                {
                    userData = UserDataCacheHelper.GetCachedUserData(user, item, refreshCache, userDataManager);
                }
                else if (userDataManager != null)
                {
                    // Fallback to fetching from userDataManager
                    userData = userDataManager.GetUserData(user, item);
                }

                // Use reflection to safely extract PlayCount from userData
                var playCountProp = userData?.GetType().GetProperty("PlayCount");
                if (playCountProp != null)
                {
                    var playCountValue = playCountProp.GetValue(userData);
                    if (playCountValue is int pc)
                        return pc;
                    if (playCountValue != null)
                        return Convert.ToInt32(playCountValue, System.Globalization.CultureInfo.InvariantCulture);
                }
                return 0;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Error extracting PlayCount from userData for item {ItemName}", item.Name);
                return 0;
            }
        }

        /// <summary>
        /// Returns the cached child array for aggregate items (Series → episodes, Season → episodes,
        /// MusicAlbum → tracks), or null if the item is not an aggregate type or the cache has no
        /// entry for it.
        ///
        /// All three container types aggregate, matching
        /// <see cref="LastPlayedOrderBase.GetAggregateLastPlayedDate"/>. Series was previously
        /// missing, so a fully watched series reported a play count of 0 while its seasons
        /// reported the real figure.
        /// </summary>
        private static BaseItem[]? TryGetAggregateChildren(
            BaseItem item,
            User user,
            RefreshQueueService.RefreshCache refreshCache)
        {
            var key = (item.Id, user.Id);
            if (item is Series && refreshCache.SeriesEpisodes.TryGetValue(key, out var seriesEpisodes) && seriesEpisodes.Length > 0)
            {
                return seriesEpisodes;
            }
            if (item is Season && refreshCache.SeasonEpisodes.TryGetValue(key, out var episodes) && episodes.Length > 0)
            {
                return episodes;
            }
            if (item is MusicAlbum && refreshCache.AlbumTracks.TryGetValue(key, out var tracks) && tracks.Length > 0)
            {
                return tracks;
            }
            return null;
        }

        /// <summary>
        /// Shared helper: calculates the minimum PlayCount across an array of tracks.
        /// Writes fetched UserData back to the cache to avoid redundant DB hits.
        /// </summary>
        internal static int CalculateMinPlayCountFromTracks(
            BaseItem[] tracks,
            User user,
            IUserDataManager userDataManager,
            RefreshQueueService.RefreshCache refreshCache)
        {
            if (tracks.Length == 0)
            {
                return 0;
            }

            int minPlayCount = int.MaxValue;
            foreach (var track in tracks)
            {
                var trackUserData = UserDataCacheHelper.GetCachedUserData(user, track, refreshCache, userDataManager);
                var trackPlayCount = trackUserData?.PlayCount ?? 0;

                if (trackPlayCount < minPlayCount)
                {
                    minPlayCount = trackPlayCount;
                }
            }

            return minPlayCount == int.MaxValue ? 0 : minPlayCount;
        }
    }

    public class PlayCountOrderDesc : UserDataOrder
    {
        public override string Name => "PlayCount (owner) Descending";
        protected override bool IsDescending => true;

        protected override int GetUserDataValue(
            BaseItem item,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            return PlayCountOrder.GetPlayCountFromUserData(item, user, userDataManager, logger, refreshCache);
        }
    }

    public class PlayCountTotalOrder : PlayCountTotalOrderBase
    {
        public override string Name => "PlayCount (all users) Ascending";
        protected override bool IsDescending => false;

        protected override int GetUserDataValue(
            BaseItem item,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            return GetTotalPlayCountAcrossUsers(item, user, userDataManager, logger, refreshCache);
        }
    }

    public class PlayCountTotalOrderDesc : PlayCountTotalOrderBase
    {
        public override string Name => "PlayCount (all users) Descending";
        protected override bool IsDescending => true;

        protected override int GetUserDataValue(
            BaseItem item,
            User user,
            IUserDataManager? userDataManager,
            ILogger? logger,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            return GetTotalPlayCountAcrossUsers(item, user, userDataManager, logger, refreshCache);
        }
    }

    // Backward-compat aliases for previously saved sort names.
    public class PlayCountSelectedUsersTotalOrder : PlayCountTotalOrder
    {
        public override string Name => "PlayCount (selected users total) Ascending";
    }

    public class PlayCountSelectedUsersTotalOrderDesc : PlayCountTotalOrderDesc
    {
        public override string Name => "PlayCount (selected users total) Descending";
    }
}
