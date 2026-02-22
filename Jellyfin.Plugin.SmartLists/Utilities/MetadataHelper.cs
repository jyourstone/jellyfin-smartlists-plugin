using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartLists.Core.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Utilities;

/// <summary>
/// Shared metadata operations for playlists and collections.
/// </summary>
public static class MetadataHelper
{
    /// <summary>
    /// Applies custom metadata (Sort Title, Overview) from the smart list configuration to a Jellyfin item.
    /// Called after metadata refresh to prevent providers from overwriting custom values.
    /// </summary>
    public static async Task ApplyCustomMetadataAsync(BaseItem item, SmartListDto dto, ILogger logger, CancellationToken cancellationToken)
    {
        bool changed = false;

        // Apply Sort Title (ForcedSortName overrides auto-generated SortName)
        var newSortTitle = string.IsNullOrEmpty(dto.SortTitle) ? null : dto.SortTitle;
        if (item.ForcedSortName != newSortTitle)
        {
            item.ForcedSortName = newSortTitle;
            changed = true;
            logger.LogDebug("Set ForcedSortName to '{SortTitle}' for {ItemName}", newSortTitle ?? "(cleared)", item.Name);
        }

        // Apply Overview
        var newOverview = string.IsNullOrEmpty(dto.Overview) ? null : dto.Overview;
        if (item.Overview != newOverview)
        {
            item.Overview = newOverview;
            changed = true;
            logger.LogDebug("Set Overview for {ItemName}", item.Name);
        }

        if (changed)
        {
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }
}
