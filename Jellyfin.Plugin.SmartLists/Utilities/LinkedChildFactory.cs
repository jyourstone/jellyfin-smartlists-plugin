using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Creates LinkedChild instances with the correct shape for the target Jellyfin ABI.
    /// </summary>
    public static class LinkedChildFactory
    {
        public static LinkedChild Create(Guid itemId, BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

#if NET10_0_OR_GREATER
            return new LinkedChild { ItemId = itemId };
#else
            return new LinkedChild { ItemId = itemId, Path = item.Path };
#endif
        }
    }
}
