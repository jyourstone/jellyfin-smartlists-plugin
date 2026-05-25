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
            if (refreshCache.UserDataCache.TryGetValue(cacheKey, out var userData))
            {
                return userData;
            }

            userData = userDataManager.GetUserData(user, item);
            if (userData != null)
            {
                refreshCache.UserDataCache[cacheKey] = userData;
            }

            return userData;
        }
    }
}
