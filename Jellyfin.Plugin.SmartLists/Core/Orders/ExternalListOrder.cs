using System;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.Orders
{
    /// <summary>
    /// Sorts items by their position in the external list (ascending = list order).
    /// Items not in any external list are sorted last.
    /// </summary>
    public class ExternalListOrder : PropertyOrder<int>
    {
        public override string Name => "External List Order Ascending";
        protected override bool IsDescending => false;
        protected override int GetSortValue(BaseItem item, User? user = null, IUserDataManager? userDataManager = null, ILogger? logger = null, RefreshQueueService.RefreshCache? refreshCache = null)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (refreshCache?.ExternalListPositions.TryGetValue(item.Id, out var position) == true)
            {
                return position;
            }

            return int.MaxValue; // Items not in any external list sort last
        }
    }

    /// <summary>
    /// Sorts items by their position in the external list (descending = reverse list order).
    /// Items not in any external list are sorted last.
    /// </summary>
    public class ExternalListOrderDesc : PropertyOrder<int>
    {
        public override string Name => "External List Order Descending";
        protected override bool IsDescending => true;
        protected override int GetSortValue(BaseItem item, User? user = null, IUserDataManager? userDataManager = null, ILogger? logger = null, RefreshQueueService.RefreshCache? refreshCache = null)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (refreshCache?.ExternalListPositions.TryGetValue(item.Id, out var position) == true)
            {
                return position;
            }

            return int.MinValue; // Items not in any external list sort last with descending
        }
    }
}
