using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartLists.Utilities
{
    /// <summary>
    /// Creates LinkedChild instances.
    /// </summary>
    public static class LinkedChildFactory
    {
        public static LinkedChild Create(Guid itemId, BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            return new LinkedChild { ItemId = itemId };
        }
    }
}
