using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.Orders
{
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
        /// For MusicAlbum, calculates the minimum PlayCount across all child tracks.
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
                // For MusicAlbum, calculate from child tracks using cache
                if (item is MusicAlbum && userDataManager != null && refreshCache != null)
                {
                    var key = (item.Id, user.Id);
                    if (refreshCache.AlbumTracks.TryGetValue(key, out var tracks) && tracks.Length > 0)
                    {
                        int minPlayCount = int.MaxValue;
                        foreach (var track in tracks)
                        {
                            int trackPlayCount = 0;
                            if (refreshCache.UserDataCache.TryGetValue((track.Id, user.Id), out var trackUserData))
                            {
                                trackPlayCount = trackUserData?.PlayCount ?? 0;
                            }
                            else
                            {
                                var fetchedData = userDataManager.GetUserData(user, track);
                                trackPlayCount = fetchedData?.PlayCount ?? 0;
                            }

                            if (trackPlayCount < minPlayCount)
                            {
                                minPlayCount = trackPlayCount;
                            }
                        }

                        return minPlayCount == int.MaxValue ? 0 : minPlayCount;
                    }
                }

                object? userData = null;
                
                // Try to get user data from cache if available
                if (refreshCache != null && refreshCache.UserDataCache.TryGetValue((item.Id, user.Id), out var cachedUserData))
                {
                    userData = cachedUserData;
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
}

