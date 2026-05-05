using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Utilities;

/// <summary>
/// Shared metadata operations for playlists and collections.
/// </summary>
public static class MetadataHelper
{
    /// <summary>
    /// Applies custom metadata (Sort Title, Overview, Tags, Favorite) from the smart list configuration to a Jellyfin item.
    /// Called after metadata refresh to prevent providers from overwriting custom values.
    /// </summary>
    public static async Task ApplyCustomMetadataAsync(
        BaseItem item,
        SmartListDto dto,
        ILogger logger,
        CancellationToken cancellationToken,
        User? favoriteUser = null,
        IUserDataManager? userDataManager = null)
    {
        bool changed = false;

        // Apply Sort Title (ForcedSortName overrides auto-generated SortName)
        var newSortTitle = string.IsNullOrWhiteSpace(dto.SortTitle) ? null : dto.SortTitle;
        if (item.ForcedSortName != newSortTitle)
        {
            item.ForcedSortName = newSortTitle;
            changed = true;
            logger.LogDebug("Set ForcedSortName to '{SortTitle}' for {ItemName}", newSortTitle ?? "(cleared)", item.Name);
        }

        // Apply Overview
        var newOverview = string.IsNullOrWhiteSpace(dto.Overview) ? null : dto.Overview;
        if (item.Overview != newOverview)
        {
            item.Overview = newOverview;
            changed = true;
            logger.LogDebug("Set Overview for {ItemName}", item.Name);
        }

        // Apply Tags. Null means SmartLists should leave existing Jellyfin tags alone;
        // an empty list is intentional and clears managed tags.
        if (dto.Tags != null)
        {
            var newTags = dto.Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var currentTags = item.Tags ?? [];
            if (!currentTags.SequenceEqual(newTags, StringComparer.OrdinalIgnoreCase))
            {
                item.Tags = newTags;
                changed = true;
                logger.LogDebug("Set {TagCount} tag(s) for {ItemName}", newTags.Length, item.Name);
            }
        }

        if (changed)
        {
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        // Favorite is stored as user data in Jellyfin, so it is applied separately from item metadata.
        if (dto.Favorite.HasValue)
        {
            if (favoriteUser == null || userDataManager == null)
            {
                logger.LogDebug("Favorite state requested for {ItemName}, but no user context was available", item.Name);
                return;
            }

            var userData = userDataManager.GetUserData(favoriteUser, item);
            if (userData == null)
            {
                logger.LogWarning("Could not load user data for {ItemName} and user {UserId}; favorite state was not applied", item.Name, favoriteUser.Id);
                return;
            }

            if (userData.IsFavorite != dto.Favorite.Value)
            {
                userData.IsFavorite = dto.Favorite.Value;
                userDataManager.SaveUserData(favoriteUser, item, userData, UserDataSaveReason.UpdateUserData, cancellationToken);
                logger.LogDebug("Set favorite state to {Favorite} for {ItemName} and user {UserId}", dto.Favorite.Value, item.Name, favoriteUser.Id);
            }
        }
    }
}
