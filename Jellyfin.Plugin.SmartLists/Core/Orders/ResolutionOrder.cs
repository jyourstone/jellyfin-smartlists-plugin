using System;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.Orders
{
    /// <summary>
    /// Sorts by the item's maximum video stream HEIGHT in pixels, not by the resolution label,
    /// so 4K (2160) &gt; 1440p &gt; 1080p. Sorting on the label string would order them
    /// alphabetically ("1080p" &lt; "1440p" &lt; "480p" &lt; "4K" &lt; "720p"), which is meaningless.
    ///
    /// Items with no video stream (audio, books) or an unreadable height collapse to
    /// <see cref="MediaStreamHelper.UnknownVideoHeight"/> (0). Like every other scalar order in
    /// this folder the sentinel is NOT direction-aware, so those items sort FIRST ascending and
    /// LAST descending.
    ///
    /// Both sort paths route through <c>GetSortValue</c> via <see cref="PropertyOrder{T}"/>, so
    /// the single-sort <c>OrderBy</c> path and the multi-sort <c>GetSortKey</c> path cannot
    /// disagree. The height is served from <c>RefreshCache.MediaStreamsCache</c> when a cache is
    /// supplied, so sorting does not re-read streams per item.
    /// </summary>
    public class ResolutionOrder : PropertyOrder<int>
    {
        public override string Name => "Resolution Ascending";
        protected override bool IsDescending => false;

        protected override int GetSortValue(BaseItem item, User? user = null, IUserDataManager? userDataManager = null, ILogger? logger = null, RefreshQueueService.RefreshCache? refreshCache = null)
        {
            ArgumentNullException.ThrowIfNull(item);
            return MediaStreamHelper.GetMaxVideoHeight(item, refreshCache, logger);
        }
    }

    public class ResolutionOrderDesc : PropertyOrder<int>
    {
        public override string Name => "Resolution Descending";
        protected override bool IsDescending => true;

        protected override int GetSortValue(BaseItem item, User? user = null, IUserDataManager? userDataManager = null, ILogger? logger = null, RefreshQueueService.RefreshCache? refreshCache = null)
        {
            ArgumentNullException.ThrowIfNull(item);
            return MediaStreamHelper.GetMaxVideoHeight(item, refreshCache, logger);
        }
    }
}
