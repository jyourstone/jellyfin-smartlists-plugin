using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Shared helpers for cache-first user data lookups during smart list refreshes.
    /// </summary>
    public static class UserDataCacheHelper
    {
        public static UserItemData? GetCachedUserData(
            User user,
            BaseItem item,
            RefreshQueueService.RefreshCache refreshCache,
            IUserDataManager userDataManager)
        {
            var cacheKey = (item.Id, user.Id);

            // Positive cache hit
            if (refreshCache.UserDataCache.TryGetValue(cacheKey, out var userData))
            {
                return userData;
            }

            // Negative cache hit — we already know this item has no user data row
            if (refreshCache.UserDataNegativeCache.ContainsKey(cacheKey))
            {
                return null;
            }

            userData = userDataManager.GetUserData(user, item);
            if (userData != null)
            {
                refreshCache.UserDataCache[cacheKey] = userData;
            }
            else
            {
                // Memoize the miss so subsequent calls skip the DB round-trip
                refreshCache.UserDataNegativeCache[cacheKey] = 0;
            }

            return userData;
        }
    }
}
