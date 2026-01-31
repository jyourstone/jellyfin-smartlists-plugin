using System;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.Orders
{
    /// <summary>
    /// Sorts Live TV channels by resolution (Height property).
    /// Lower height values sort first in ascending order.
    /// </summary>
    public class ChannelResolutionOrder : PropertyOrder<int>
    {
        public override string Name => "ChannelResolution Ascending";
        protected override bool IsDescending => false;

        protected override int GetSortValue(BaseItem item, User? user = null, IUserDataManager? userDataManager = null, ILogger? logger = null, RefreshQueueService.RefreshCache? refreshCache = null)
        {
            ArgumentNullException.ThrowIfNull(item);
            // Height stores the resolution (720 for HD, 1080 for Full HD, 2160 for UHD)
            return item.Height;
        }
    }

    /// <summary>
    /// Sorts Live TV channels by resolution in descending order (highest resolution first).
    /// </summary>
    public class ChannelResolutionOrderDesc : PropertyOrder<int>
    {
        public override string Name => "ChannelResolution Descending";
        protected override bool IsDescending => true;

        protected override int GetSortValue(BaseItem item, User? user = null, IUserDataManager? userDataManager = null, ILogger? logger = null, RefreshQueueService.RefreshCache? refreshCache = null)
        {
            ArgumentNullException.ThrowIfNull(item);
            // Height stores the resolution (720 for HD, 1080 for Full HD, 2160 for UHD)
            return item.Height;
        }
    }
}
