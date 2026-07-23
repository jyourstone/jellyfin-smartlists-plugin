using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartLists;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Core.Enums;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.Abstractions;
using Jellyfin.Plugin.SmartLists.Services.ExternalList;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Playlists
{
    /// <summary>
    /// Service for handling individual smart playlist operations.
    /// Implements ISmartListService for playlists.
    /// </summary>
    public class PlaylistService : ISmartListService<SmartPlaylistDto>
    {
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IPlaylistManager _playlistManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILogger<PlaylistService> _logger;
        private readonly SmartListImageService? _imageService;
        private readonly ExternalListService? _externalListService;

        public PlaylistService(
            IUserManager userManager,
            ILibraryManager libraryManager,
            IPlaylistManager playlistManager,
            IUserDataManager userDataManager,
            ILogger<PlaylistService> logger,
            SmartListImageService? imageService = null,
            ExternalListService? externalListService = null)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _playlistManager = playlistManager;
            _userDataManager = userDataManager;
            _logger = logger;
            _imageService = imageService;
            _externalListService = externalListService;
        }



        /// <summary>
        /// Core method to process a single playlist refresh with cached media.
        /// This method is used by both single playlist refresh and batch processing.
        /// </summary>
        /// <param name="dto">The playlist DTO to process</param>
        /// <param name="user">The user for this playlist (already resolved)</param>
        /// <param name="allUserMedia">All media items for the user (can be cached)</param>
        /// <param name="refreshCache">RefreshCache instance for caching expensive operations</param>
        /// <param name="saveCallback">Optional callback to save the DTO when JellyfinPlaylistId is updated</param>
        /// <param name="progressCallback">Optional callback to report progress (processed items, total items)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Tuple of (success, message, jellyfinPlaylistId)</returns>
        public async Task<(bool Success, string Message, string JellyfinPlaylistId)> ProcessPlaylistRefreshWithCachedMediaAsync(
            SmartPlaylistDto dto,
            User user,
            BaseItem[] allUserMedia,
            RefreshQueueService.RefreshCache refreshCache,
            Func<SmartPlaylistDto, Task>? saveCallback = null,
            Action<int, int>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dto);
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(allUserMedia);

            var (success, message, jellyfinPlaylistId) = await ProcessPlaylistRefreshAsync(dto, user, allUserMedia, refreshCache, _logger, saveCallback, progressCallback, cancellationToken);

            // Update LastRefreshed timestamp for successful refreshes (any trigger)
            // Note: For new playlists, LastRefreshed was already set in ProcessPlaylistRefreshAsync before the saveCallback,
            // but we update it here to ensure it reflects the exact completion time of the refresh operation.
            if (success)
            {
                dto.LastRefreshed = DateTime.UtcNow;
                _logger.LogDebug("Updated LastRefreshed timestamp for cached playlist: {PlaylistName}", dto.Name);
                
                // Call save callback if provided to persist the LastRefreshed timestamp
                if (saveCallback != null)
                {
                    await saveCallback(dto);
                }
            }

            return (success, message, jellyfinPlaylistId);
        }

        /// <summary>
        /// Core method to process a single playlist refresh. This is the shared logic used by both
        /// single playlist refresh and batch playlist refresh operations.
        /// </summary>
        /// <param name="dto">The playlist DTO to process</param>
        /// <param name="user">The user for this playlist (already resolved)</param>
        /// <param name="allUserMedia">All media items for the user (can be cached)</param>
        /// <param name="logger">Logger to use for this operation</param>
        /// <param name="saveCallback">Optional callback to save the DTO when JellyfinPlaylistId is updated</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Tuple of (success, message, jellyfinPlaylistId)</returns>
        private async Task<(bool Success, string Message, string JellyfinPlaylistId)> ProcessPlaylistRefreshAsync(
            SmartPlaylistDto dto,
            User user,
            BaseItem[] allUserMedia,
            RefreshQueueService.RefreshCache refreshCache,
            ILogger logger,
            Func<SmartPlaylistDto, Task>? saveCallback = null,
            Action<int, int>? progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                logger.LogDebug("Processing playlist refresh: {PlaylistName}", dto.Name);

                // Check if playlist is enabled
                if (!dto.Enabled)
                {
                    logger.LogDebug("Smart playlist '{PlaylistName}' is disabled. Skipping refresh.", dto.Name);
                    return (true, "Playlist is disabled", string.Empty);
                }

                var smartPlaylist = new Core.SmartList(dto)
                {
                    UserManager = _userManager // Set UserManager for Jellyfin 10.11+ user resolution,
                };

                // Log the playlist rules
                logger.LogDebug("Processing playlist {PlaylistName} with {RuleSetCount} rule sets", dto.Name, dto.ExpressionSets?.Count ?? 0);

                logger.LogDebug("Found {MediaCount} total media items for user {User}", allUserMedia.Length, user.Username);

                // Report initial total items count
                progressCallback?.Invoke(0, allUserMedia.Length);

                // Pre-fetch external lists if any ExternalList rules are present.
                // Bumper rule sets are included so ExternalList rules work in bumper pools too.
                if (_externalListService != null && dto.ExpressionSets != null)
                {
                    var expressionSetsForAnalysis = dto.Bumpers?.ExpressionSets is { Count: > 0 } bumperSets
                        ? dto.ExpressionSets.Concat(bumperSets).ToList()
                        : dto.ExpressionSets;
                    var fieldReqs = FieldRequirements.Analyze(expressionSetsForAnalysis);
                    if (fieldReqs.NeedsExternalLists && fieldReqs.ExternalListUrls.Count > 0)
                    {
                        // Clear per-item external list caches so items are re-evaluated against this playlist's lists.
                        // ExternalListData (the fetched list data) is kept since it's shared and additive across playlists.
                        refreshCache.ItemExternalLists.Clear();
                        refreshCache.ExternalListPositions.Clear();
                        refreshCache.MusicListPositionsByUrl.Clear();

                        var fetchLimit = ExternalListService.ComputeFetchLimit();
                        logger.LogDebug("Pre-fetching {Count} external list(s) for playlist '{PlaylistName}' (fetchLimit: {FetchLimit})", fieldReqs.ExternalListUrls.Count, dto.Name, fetchLimit);
                        await _externalListService.PreFetchListsAsync(fieldReqs.ExternalListUrls, refreshCache, cancellationToken, fetchLimit).ConfigureAwait(false);
                    }
                }

                var newItems = smartPlaylist.FilterPlaylistItems(allUserMedia, _libraryManager, user, refreshCache, _userDataManager, logger, progressCallback).ToArray();
                logger.LogDebug("Playlist {PlaylistName} filtered to {FilteredCount} items from {TotalCount} total items",
                    dto.Name, newItems.Length, allUserMedia.Length);

                // Create a lookup dictionary for O(1) access while preserving order from newItems
                var mediaLookup = allUserMedia
                    .GroupBy(m => m.Id)
                    .ToDictionary(g => g.Key, g => g.First());

                var mainItemIds = newItems
                    .Distinct()
                    .Where(itemId => mediaLookup.ContainsKey(itemId))
                    .ToList();

                // Weave bumper items between main items. Bumpers may legitimately repeat,
                // so the woven list must NOT be deduplicated.
                var finalItemIds = mainItemIds;
                if (dto.Bumpers != null && dto.Bumpers.ExpressionSets != null && dto.Bumpers.ExpressionSets.Count > 0 && mainItemIds.Count > 0)
                {
                    var bumperItemIds = GetBumperItemIds(dto, user, refreshCache, mainItemIds, mediaLookup, logger);
                    if (bumperItemIds.Count > 0)
                    {
                        finalItemIds = WeaveBumpers(mainItemIds, bumperItemIds, Math.Max(1, dto.Bumpers.Interval));
                        logger.LogDebug("Wove bumpers into playlist '{PlaylistName}' every {Interval} item(s): {MainCount} main + {BumperPool} pool -> {TotalCount} entries",
                            dto.Name, dto.Bumpers.Interval, mainItemIds.Count, bumperItemIds.Count, finalItemIds.Count);
                    }
                    else
                    {
                        logger.LogDebug("Bumper rules matched no items for playlist '{PlaylistName}'; writing playlist without bumpers", dto.Name);
                    }
                }

                var newLinkedChildren = finalItemIds
                    .Select(itemId => LinkedChildFactory.Create(itemId, mediaLookup[itemId]))
                    .ToArray();

                // Calculate playlist statistics from the same filtered list used for the actual playlist
                dto.ItemCount = newLinkedChildren.Length;
                dto.TotalRuntimeMinutes = RuntimeCalculator.CalculateTotalRuntimeMinutes(
                    newLinkedChildren.Where(lc => lc.ItemId.HasValue).Select(lc => lc.ItemId!.Value).ToArray(),
                    mediaLookup,
                    logger);
                logger.LogDebug("Calculated playlist stats: {ItemCount} items, {TotalRuntime} minutes total playtime",
                    dto.ItemCount, dto.TotalRuntimeMinutes);

                // Try to find existing playlist by Jellyfin playlist ID first, then by current naming format, then by old format
                Playlist? existingPlaylist = null;

                // For multi-user playlists, find the JellyfinPlaylistId for this specific user
                string? jellyfinPlaylistIdForUser = null;
                if (dto.UserPlaylists != null && dto.UserPlaylists.Count > 0)
                {
                    var userMapping = dto.UserPlaylists.FirstOrDefault(m => string.Equals(m.UserId, user.Id.ToString("N"), StringComparison.OrdinalIgnoreCase));
                    jellyfinPlaylistIdForUser = userMapping?.JellyfinPlaylistId;
                }
                else
                {
                    // Fallback to top-level JellyfinPlaylistId (backwards compatibility)
                    jellyfinPlaylistIdForUser = dto.JellyfinPlaylistId;
                }

                logger.LogDebug("Looking for playlist: User={UserId}, JellyfinPlaylistId={JellyfinPlaylistId}",
                    user.Id, jellyfinPlaylistIdForUser);

                // First try to find by Jellyfin playlist ID (most reliable)
                if (!string.IsNullOrEmpty(jellyfinPlaylistIdForUser) && Guid.TryParse(jellyfinPlaylistIdForUser, out var parsedJellyfinPlaylistId))
                {
                    if (_libraryManager.GetItemById(parsedJellyfinPlaylistId) is Playlist playlistById)
                    {
                        existingPlaylist = playlistById;
                        logger.LogDebug("Found existing playlist by Jellyfin playlist ID: {JellyfinPlaylistId} - {PlaylistName}",
                            jellyfinPlaylistIdForUser, existingPlaylist.Name);
                    }
                    else
                    {
                        logger.LogDebug("No playlist found by Jellyfin playlist ID: {JellyfinPlaylistId}", jellyfinPlaylistIdForUser);
                    }
                }

                // Note: Legacy name-based fallback removed - all playlists should now have JellyfinPlaylistId

                // Recovery: if the stored Jellyfin playlist ID is stale (e.g. lost after a DB
                // migration or restore), re-find the playlist via the SmartLists provider ID
                // stamped on it at creation. Never match by name - duplicate names are legal.
                var recoveredViaProviderId = false;
                if (existingPlaylist == null && !string.IsNullOrEmpty(dto.Id))
                {
                    // Note: IPlaylistManager.GetPlaylists hydrates items without provider IDs
                    // (DtoOptions(false)), so query the library directly instead
                    var tetherQuery = new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.Playlist],
                        Recursive = true,
                    };

                    existingPlaylist = _libraryManager.GetItemsResult(tetherQuery).Items
                        .OfType<Playlist>()
                        .FirstOrDefault(p => p.OwnerUserId == user.Id
                            && string.Equals(p.GetProviderId(ProviderKeys.SmartLists), dto.Id, StringComparison.OrdinalIgnoreCase));

                    if (existingPlaylist != null)
                    {
                        recoveredViaProviderId = true;
                        logger.LogInformation("Recovered playlist '{PlaylistName}' ({PlaylistId}) for user {UserId} via SmartLists provider ID; stored Jellyfin playlist ID was stale",
                            existingPlaylist.Name, existingPlaylist.Id, user.Id);
                    }
                }

                // Now that we've found the existing playlist (or not), apply the new naming format
                var smartPlaylistName = NameFormatter.FormatPlaylistName(dto.Name);

                // Hide when empty: don't keep a Jellyfin playlist around while no items match
                if (dto.HideWhenEmpty && newLinkedChildren.Length == 0)
                {
                    if (existingPlaylist != null)
                    {
                        logger.LogInformation("Smart playlist '{PlaylistName}' matched no items - deleting Jellyfin playlist (hide when empty)", dto.Name);
                        _libraryManager.DeleteItem(existingPlaylist, new DeleteOptions { DeleteFileLocation = true }, true);
                    }

                    // Clear the stored Jellyfin playlist ID so a later refresh with items recreates it
                    if (dto.UserPlaylists != null && dto.UserPlaylists.Count > 0)
                    {
                        var emptyUserMapping = dto.UserPlaylists.FirstOrDefault(m => string.Equals(m.UserId, user.Id.ToString("N"), StringComparison.OrdinalIgnoreCase));
                        if (emptyUserMapping != null)
                        {
                            emptyUserMapping.JellyfinPlaylistId = null;
                        }

                        // Update backwards compatibility field (first user's playlist)
                        dto.JellyfinPlaylistId = dto.UserPlaylists[0].JellyfinPlaylistId;
                    }
                    else
                    {
                        dto.JellyfinPlaylistId = null;
                    }

                    if (saveCallback != null)
                    {
                        try
                        {
                            await saveCallback(dto);
                        }
                        catch (Exception saveEx)
                        {
                            logger.LogWarning(saveEx, "Failed to save playlist DTO for {PlaylistName}, but continuing with operation", dto.Name);
                        }
                    }

                    return (true, $"Playlist '{smartPlaylistName}' has no items - hidden (hide when empty)", string.Empty);
                }

                if (existingPlaylist != null)
                {
                    logger.LogDebug("Processing existing playlist: {PlaylistName} (ID: {PlaylistId})", existingPlaylist.Name, existingPlaylist.Id);

                    // Check if the playlist name needs to be updated
                    var currentName = existingPlaylist.Name;
                    var expectedName = smartPlaylistName;
                    var nameChanged = currentName != expectedName;

                    if (nameChanged)
                    {
                        logger.LogDebug("Playlist name changing from '{OldName}' to '{NewName}'", currentName, expectedName);
                        existingPlaylist.Name = expectedName;
                    }

                    // Check if ownership needs to be updated
                    var ownershipChanged = existingPlaylist.OwnerUserId != user.Id;
                    if (ownershipChanged)
                    {
                        logger.LogDebug("Playlist ownership changing from {OldOwner} to {NewOwner}", existingPlaylist.OwnerUserId, user.Id);
                        existingPlaylist.OwnerUserId = user.Id;
                    }

                    // Check if we need to update the playlist due to public/private setting change
                    // Use OpenAccess property instead of Shares.Any() as revealed by debugging
                    var openAccessProperty = existingPlaylist.GetType().GetProperty("OpenAccess");
                    bool isCurrentlyPublic = false;
                    if (openAccessProperty != null)
                    {
                        isCurrentlyPublic = (bool)(openAccessProperty.GetValue(existingPlaylist) ?? false);
                    }
                    else
                    {
                        // Fallback to share manipulation check when OpenAccess property is not available
                        isCurrentlyPublic = existingPlaylist.Shares?.Any() ?? false;
                    }

                    var publicStatusChanged = isCurrentlyPublic != dto.Public;
                    if (publicStatusChanged)
                    {
                        logger.LogDebug("Playlist public status changing from {OldPublic} to {NewPublic}", isCurrentlyPublic, dto.Public);
                    }

                    // Retro-stamp the tether on playlists created before this mechanism existed
                    var providerIdChanged = false;
                    if (!string.IsNullOrEmpty(dto.Id)
                        && !string.Equals(existingPlaylist.GetProviderId(ProviderKeys.SmartLists), dto.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        providerIdChanged = true;
                        existingPlaylist.SetProviderId(ProviderKeys.SmartLists, dto.Id);
                    }

                    // Update the playlist if any changes are needed
                    if (nameChanged || ownershipChanged || publicStatusChanged || providerIdChanged)
                    {
                        await existingPlaylist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
                        logger.LogDebug("Updated existing playlist: {PlaylistName}", existingPlaylist.Name);
                    }

                    // Update the playlist items (includes metadata refresh)
                    await UpdatePlaylistPublicStatusAsync(existingPlaylist, dto.Public, newLinkedChildren, dto, user, cancellationToken);

                    logger.LogDebug("Successfully updated existing playlist: {PlaylistName} with {ItemCount} items",
                        existingPlaylist.Name, newLinkedChildren.Length);

                    if (recoveredViaProviderId)
                    {
                        var recoveredPlaylistId = existingPlaylist.Id.ToString("N");
                        if (dto.UserPlaylists != null && dto.UserPlaylists.Count > 0)
                        {
                            var recoveredMapping = dto.UserPlaylists.FirstOrDefault(m => string.Equals(m.UserId, user.Id.ToString("N"), StringComparison.OrdinalIgnoreCase));
                            if (recoveredMapping != null)
                            {
                                recoveredMapping.JellyfinPlaylistId = recoveredPlaylistId;
                            }
                            else
                            {
                                dto.UserPlaylists.Add(new SmartPlaylistDto.UserPlaylistMapping
                                {
                                    UserId = user.Id.ToString("N"),
                                    JellyfinPlaylistId = recoveredPlaylistId
                                });
                            }

                            dto.JellyfinPlaylistId = dto.UserPlaylists[0].JellyfinPlaylistId;
                        }
                        else
                        {
                            dto.JellyfinPlaylistId = recoveredPlaylistId;
                        }

                        if (saveCallback != null)
                        {
                            try
                            {
                                await saveCallback(dto);
                            }
                            catch (Exception saveEx)
                            {
                                logger.LogWarning(saveEx, "Failed to save recovered Jellyfin playlist ID for {PlaylistName}, but continuing with operation", dto.Name);
                            }
                        }
                    }

                    DeleteOrphanedTetheredPlaylists(dto, user, existingPlaylist.Id);

                    return (true, $"Updated playlist '{existingPlaylist.Name}' with {newLinkedChildren.Length} items", existingPlaylist.Id.ToString("N"));
                }
                else
                {
                    // Create new playlist
                    logger.LogDebug("Creating new playlist: {PlaylistName}", smartPlaylistName);

                    var newPlaylistId = await CreateNewPlaylistAsync(smartPlaylistName, user, dto.Public, newLinkedChildren, dto, cancellationToken);

                    // Check if playlist creation actually succeeded
                    if (string.IsNullOrEmpty(newPlaylistId))
                    {
                        logger.LogError("Failed to create playlist '{PlaylistName}' - no valid playlist ID returned", smartPlaylistName);
                        return (false, $"Failed to create playlist '{smartPlaylistName}' - the playlist could not be retrieved after creation", string.Empty);
                    }

                    if (Guid.TryParse(newPlaylistId, out var createdPlaylistGuid))
                    {
                        DeleteOrphanedTetheredPlaylists(dto, user, createdPlaylistGuid);
                    }

                    // Update the DTO with the new Jellyfin playlist ID
                    // For multi-user playlists, update the specific user's mapping
                    if (dto.UserPlaylists != null && dto.UserPlaylists.Count > 0)
                    {
                        var userMapping = dto.UserPlaylists.FirstOrDefault(m => string.Equals(m.UserId, user.Id.ToString("N"), StringComparison.OrdinalIgnoreCase));
                        if (userMapping != null)
                        {
                            userMapping.JellyfinPlaylistId = newPlaylistId;
                            logger.LogDebug("Updated UserPlaylistMapping for user {UserId} with JellyfinPlaylistId {JellyfinPlaylistId}", user.Id, newPlaylistId);
                        }
                        else
                        {
                            logger.LogWarning("User {UserId} not found in UserPlaylists for playlist {PlaylistName}, adding mapping", user.Id, dto.Name);
                            dto.UserPlaylists.Add(new SmartPlaylistDto.UserPlaylistMapping
                            {
                                UserId = user.Id.ToString("N"),
                                JellyfinPlaylistId = newPlaylistId
                            });
                        }
                        // Update backwards compatibility field (first user's playlist)
                        dto.JellyfinPlaylistId = dto.UserPlaylists[0].JellyfinPlaylistId;
                    }
                    else
                    {
                        // Single-user playlist (backwards compatibility)
                        // DEPRECATED: This is for backwards compatibility with old single-user playlists.
                        // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
                        dto.JellyfinPlaylistId = newPlaylistId;
                    }
                    dto.LastRefreshed = DateTime.UtcNow;

                    // Save the DTO if a callback is provided
                    if (saveCallback != null)
                    {
                        try
                        {
                            await saveCallback(dto);
                            logger.LogDebug("Saved playlist DTO with new Jellyfin playlist ID {JellyfinPlaylistId} for playlist {PlaylistName}",
                                newPlaylistId, dto.Name);
                        }
                        catch (Exception saveEx)
                        {
                            logger.LogWarning(saveEx, "Failed to save playlist DTO for {PlaylistName}, but continuing with operation", dto.Name);
                        }
                    }

                    logger.LogDebug("Successfully created new playlist: {PlaylistName} with {ItemCount} items",
                        smartPlaylistName, newLinkedChildren.Length);

                    return (true, $"Created playlist '{smartPlaylistName}' with {newLinkedChildren.Length} items", newPlaylistId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing playlist refresh for '{PlaylistName}': {ErrorMessage}", dto.Name, ex.Message);
                return (false, $"Error processing playlist '{dto.Name}': {ex.Message}", string.Empty);
            }
        }

        public async Task<(bool Success, string Message, string Id)> RefreshAsync(SmartPlaylistDto dto, Action<int, int>? progressCallback = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dto);

            // This is the internal method that assumes the lock is already held
            var stopwatch = Stopwatch.StartNew();
            try
            {
                _logger.LogDebug("Refreshing single smart playlist: {PlaylistName}", dto.Name);
                _logger.LogDebug("PlaylistService.RefreshSinglePlaylistAsync called with: Name={Name}, User={User}, Public={Public}, Enabled={Enabled}, ExpressionSets={ExpressionSetCount}, MediaTypes={MediaTypes}",
                    dto.Name, dto.UserId, dto.Public, dto.Enabled, dto.ExpressionSets?.Count ?? 0,
                    dto.MediaTypes != null ? string.Join(",", dto.MediaTypes) : "None");

                // Validate media types before processing
                _logger.LogDebug("Validating media types for playlist '{PlaylistName}': {MediaTypes}", dto.Name, dto.MediaTypes != null ? string.Join(",", dto.MediaTypes) : "null");

                var validation = ValidateUnsupportedMediaTypes(dto.MediaTypes, dto.Name);
                if (!validation.IsValid)
                {
                    return validation;
                }

                if (dto.MediaTypes == null || dto.MediaTypes.Count == 0)
                {
                    _logger.LogError("Smart playlist '{PlaylistName}' has no media types specified. At least one media type must be selected. Skipping playlist refresh.", dto.Name);
                    return (false, "No media types specified. At least one media type must be selected.", string.Empty);
                }

                // Get the user for this playlist
                var user = GetPlaylistUser(dto);
                if (user == null)
                {
                    _logger.LogWarning("No user found for playlist '{PlaylistName}'. Skipping.", dto.Name);
                    return (false, "No user found for playlist", string.Empty);
                }

                var allUserMedia = GetAllUserMedia(user, dto.MediaTypes, dto).ToArray();

                // Create a temporary RefreshCache for this refresh (fallback path when queue service unavailable)
                var refreshCache = new RefreshQueueService.RefreshCache();

                var (success, message, jellyfinPlaylistId) = await ProcessPlaylistRefreshAsync(dto, user, allUserMedia, refreshCache, _logger, null, progressCallback, cancellationToken);

                // Update LastRefreshed timestamp for successful refreshes (any trigger)
                if (success)
                {
                    dto.LastRefreshed = DateTime.UtcNow;
                    _logger.LogDebug("Updated LastRefreshed timestamp for playlist: {PlaylistName}", dto.Name);
                }

                stopwatch.Stop();
                _logger.LogDebug("Single playlist refresh completed in {ElapsedMs}ms: {Message}", stopwatch.ElapsedMilliseconds, message);

                return (success, message, jellyfinPlaylistId);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Error in RefreshSinglePlaylistAsync for '{PlaylistName}' after {ElapsedMs}ms: {ErrorMessage}",
                    dto.Name, stopwatch.ElapsedMilliseconds, ex.Message);
                return (false, $"Error refreshing playlist '{dto.Name}': {ex.Message}", string.Empty);
            }
        }


        public Task DeleteAsync(SmartPlaylistDto dto, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dto);

            try
            {
                var user = GetPlaylistUser(dto);
                if (user == null)
                {
                    _logger.LogWarning("No user found for playlist '{PlaylistName}'. Will attempt deletion anyway (user may have been deleted).", dto.Name);
                }

                Playlist? existingPlaylist = null;

                // Try to find by Jellyfin playlist ID only (no name fallback for deletion)
                if (!string.IsNullOrEmpty(dto.JellyfinPlaylistId) && Guid.TryParse(dto.JellyfinPlaylistId, out var jellyfinPlaylistId))
                {
                    if (_libraryManager.GetItemById(jellyfinPlaylistId) is Playlist playlistById)
                    {
                        existingPlaylist = playlistById;
                        _logger.LogDebug("Found playlist by Jellyfin playlist ID for deletion: {JellyfinPlaylistId} - {PlaylistName}",
                            dto.JellyfinPlaylistId, existingPlaylist.Name);
                    }
                    else
                    {
                        _logger.LogWarning("No Jellyfin playlist found by ID '{JellyfinPlaylistId}' for deletion. Playlist may have been manually deleted.", dto.JellyfinPlaylistId);
                    }
                }
                else
                {
                    _logger.LogWarning("No Jellyfin playlist ID available for playlist '{PlaylistName}'. Cannot delete Jellyfin playlist.", dto.Name);
                }

                if (existingPlaylist != null)
                {
                    var userName = user?.Username ?? "Unknown User";
                    _logger.LogInformation("Deleting Jellyfin playlist '{PlaylistName}' (ID: {PlaylistId}) for user '{UserName}'",
                        existingPlaylist.Name, existingPlaylist.Id, userName);
                    _libraryManager.DeleteItem(existingPlaylist, new DeleteOptions { DeleteFileLocation = true }, true);
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting smart playlist {PlaylistName}", dto.Name);
                throw;
            }
        }

        public async Task RemoveSmartSuffixAsync(SmartPlaylistDto dto, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dto);

            try
            {
                var user = GetPlaylistUser(dto);
                if (user == null)
                {
                    _logger.LogWarning("No user found for playlist '{PlaylistName}'. Cannot remove smart suffix.", dto.Name);

                    // Multi-user playlists have no single owner (UserId is legacy/empty). Still strip the
                    // tether from every stored Jellyfin playlist so the weekly cleanup sweep doesn't delete
                    // items the user chose to keep.
                    var idsToClear = new List<string>();
                    if (!string.IsNullOrEmpty(dto.JellyfinPlaylistId))
                    {
                        idsToClear.Add(dto.JellyfinPlaylistId);
                    }

                    if (dto.UserPlaylists != null)
                    {
                        foreach (var mapping in dto.UserPlaylists)
                        {
                            if (!string.IsNullOrEmpty(mapping.JellyfinPlaylistId))
                            {
                                idsToClear.Add(mapping.JellyfinPlaylistId);
                            }
                        }
                    }

                    var handledPlaylistIds = new HashSet<Guid>();
                    foreach (var id in idsToClear)
                    {
                        if (Guid.TryParse(id, out var guid) && _libraryManager.GetItemById(guid) is Playlist playlistToClear)
                        {
                            var tether = playlistToClear.GetProviderId(ProviderKeys.SmartLists);
                            if (!string.IsNullOrEmpty(tether) && !string.Equals(tether, dto.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogWarning("Stored ID '{JellyfinPlaylistId}' points at an item tethered to a different smart list ('{PlaylistName}'). Skipping.",
                                    id, playlistToClear.Name);
                                continue;
                            }

                            playlistToClear.ProviderIds?.Remove(ProviderKeys.SmartLists);
                            await playlistToClear.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                            handledPlaylistIds.Add(guid);
                        }
                    }

                    // Fallback sweep: find any remaining playlists still tethered to this dto (e.g. the
                    // real kept item under a stale or mismatched stored ID) and strip them too, so the
                    // weekly cleanup sweep doesn't delete an item the user chose to keep.
                    if (!string.IsNullOrEmpty(dto.Id))
                    {
                        var tetherQuery = new InternalItemsQuery
                        {
                            IncludeItemTypes = [BaseItemKind.Playlist],
                            Recursive = true,
                        };

                        var remainingTethered = _libraryManager.GetItemsResult(tetherQuery).Items
                            .OfType<Playlist>()
                            .Where(p => !handledPlaylistIds.Contains(p.Id)
                                && string.Equals(p.GetProviderId(ProviderKeys.SmartLists), dto.Id, StringComparison.OrdinalIgnoreCase));

                        foreach (var playlistToClear in remainingTethered)
                        {
                            playlistToClear.ProviderIds?.Remove(ProviderKeys.SmartLists);
                            await playlistToClear.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                            _logger.LogInformation("Stripped smart list tether from playlist '{PlaylistName}' (ID: {PlaylistId}) via fallback sweep; stored ID was stale or mismatched.",
                                playlistToClear.Name, playlistToClear.Id);
                        }
                    }

                    return;
                }

                Playlist? existingPlaylist = null;

                // Try to find by Jellyfin playlist ID only (no name fallback for suffix removal)
                if (!string.IsNullOrEmpty(dto.JellyfinPlaylistId) && Guid.TryParse(dto.JellyfinPlaylistId, out var jellyfinPlaylistId))
                {
                    if (_libraryManager.GetItemById(jellyfinPlaylistId) is Playlist playlistById)
                    {
                        existingPlaylist = playlistById;
                        _logger.LogDebug("Found playlist by Jellyfin playlist ID for suffix removal: {JellyfinPlaylistId} - {PlaylistName}",
                            dto.JellyfinPlaylistId, existingPlaylist.Name);
                    }
                    else
                    {
                        _logger.LogWarning("No Jellyfin playlist found by ID '{JellyfinPlaylistId}' for suffix removal. Playlist may have been manually deleted.", dto.JellyfinPlaylistId);
                    }
                }
                else
                {
                    _logger.LogWarning("No Jellyfin playlist ID available for playlist '{PlaylistName}'.", dto.Name);
                }

                if (existingPlaylist != null)
                {
                    var tether = existingPlaylist.GetProviderId(ProviderKeys.SmartLists);
                    if (!string.IsNullOrEmpty(tether) && !string.Equals(tether, dto.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Stored ID '{JellyfinPlaylistId}' points at an item tethered to a different smart list ('{PlaylistName}'). Skipping.",
                            dto.JellyfinPlaylistId, existingPlaylist.Name);
                        existingPlaylist = null;
                    }
                }

                if (existingPlaylist != null)
                {
                    var oldName = existingPlaylist.Name;
                    _logger.LogInformation("Removing smart playlist '{PlaylistName}' (ID: {PlaylistId}) for user '{UserName}'",
                        oldName, existingPlaylist.Id, user.Username);

                    // Playlist is being handed back to the user - always remove the smart list tether,
                    // regardless of whether the name still matches an expected smart format, so the
                    // weekly cleanup sweep doesn't delete an item the user chose to keep.
                    existingPlaylist.ProviderIds?.Remove(ProviderKeys.SmartLists);

                    // Get the current smart playlist name format to see what needs to be removed
                    var currentSmartName = NameFormatter.FormatPlaylistName(dto.Name);

                    // Check if the playlist name matches the current smart format
                    if (oldName == currentSmartName)
                    {
                        // Remove the smart playlist naming and keep just the base name
                        existingPlaylist.Name = dto.Name;

                        _logger.LogDebug("Successfully renamed playlist from '{OldName}' to '{NewName}' for user '{UserName}'",
                            oldName, dto.Name, user.Username);
                    }
                    else
                    {
                        // Try to remove prefix and suffix even if they don't match current settings
                        // This handles cases where the user changed their prefix/suffix settings
                        var config = Plugin.Instance?.Configuration;
                        if (config != null)
                        {
                            var prefix = config.PlaylistNamePrefix ?? "";
                            var suffix = config.PlaylistNameSuffix ?? "[Smart]";

                            var baseName = dto.Name;
                            var expectedName = NameFormatter.FormatPlaylistNameWithSettings(baseName, prefix, suffix);

                            // If the playlist name matches this pattern, remove the prefix and suffix
                            if (oldName == expectedName)
                            {
                                existingPlaylist.Name = baseName;

                                _logger.LogDebug("Successfully renamed playlist from '{OldName}' to '{NewName}' for user '{UserName}' (removed prefix/suffix)",
                                    oldName, baseName, user.Username);
                            }
                            else
                            {
                                _logger.LogWarning("Playlist name '{OldName}' doesn't match expected smart format '{ExpectedName}'. Skipping rename.",
                                    oldName, expectedName);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Playlist name '{OldName}' doesn't match expected smart format '{ExpectedName}'. Skipping rename.",
                                oldName, currentSmartName);
                        }
                    }

                    // Save the changes (tether removal always applies; name change applies only when matched above)
                    await existingPlaylist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }

                // Fallback sweep: find any remaining playlists still tethered to this dto (e.g. the
                // real kept item under a stale or mismatched stored ID) and strip them too, so the
                // weekly cleanup sweep doesn't delete an item the user chose to keep.
                if (!string.IsNullOrEmpty(dto.Id))
                {
                    var tetherQuery = new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.Playlist],
                        Recursive = true,
                    };

                    var remainingTethered = _libraryManager.GetItemsResult(tetherQuery).Items
                        .OfType<Playlist>()
                        .Where(p => p.OwnerUserId == user.Id
                            && (existingPlaylist == null || p.Id != existingPlaylist.Id)
                            && string.Equals(p.GetProviderId(ProviderKeys.SmartLists), dto.Id, StringComparison.OrdinalIgnoreCase));

                    foreach (var playlistToClear in remainingTethered)
                    {
                        playlistToClear.ProviderIds?.Remove(ProviderKeys.SmartLists);
                        await playlistToClear.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                        _logger.LogInformation("Stripped smart list tether from playlist '{PlaylistName}' (ID: {PlaylistId}) via fallback sweep; stored ID was stale or mismatched.",
                            playlistToClear.Name, playlistToClear.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing smart suffix from playlist {PlaylistName}", dto.Name);
                throw;
            }
        }

        public async Task DisableAsync(SmartPlaylistDto dto, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(dto);

            try
            {
                _logger.LogDebug("Disabling smart playlist: {PlaylistName}", dto.Name);

                // Delete all Jellyfin playlists for all users
                await DeleteAllJellyfinPlaylistsForUsersAsync(dto, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Successfully disabled smart playlist: {PlaylistName}", dto.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disabling smart playlist {PlaylistName}", dto.Name);
                throw;
            }
        }

        private async Task UpdatePlaylistPublicStatusAsync(Playlist playlist, bool isPublic, LinkedChild[] linkedChildren, SmartPlaylistDto dto, User user, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Updating playlist {PlaylistName} public status to {PublicStatus} and items to {ItemCount}",
    playlist.Name, isPublic ? "public" : "private", linkedChildren.Length);

            // Update the playlist items
            playlist.LinkedChildren = linkedChildren;

            // Update the public status by setting the OpenAccess property
            var openAccessProperty = playlist.GetType().GetProperty("OpenAccess");
            if (openAccessProperty != null && openAccessProperty.CanWrite)
            {
                _logger.LogDebug("Setting playlist {PlaylistName} OpenAccess property to {IsPublic}", playlist.Name, isPublic);
                openAccessProperty.SetValue(playlist, isPublic);
            }
            else
            {
                // Fallback to share manipulation if OpenAccess property is not available
                _logger.LogWarning("OpenAccess property not found or not writable, falling back to share manipulation");
                if (isPublic && !(playlist.Shares?.Any() ?? false))
                {
                    _logger.LogDebug("Making playlist {PlaylistName} public by adding share", playlist.Name);
                    var ownerId = playlist.OwnerUserId;
                    var newShare = new MediaBrowser.Model.Entities.PlaylistUserPermissions(ownerId, false);

                    var currentShares = playlist.Shares?.ToList() ?? [];
                    currentShares.Add(newShare);
                    playlist.Shares = currentShares;
                }
                else if (!isPublic && (playlist.Shares?.Any() ?? false))
                {
                    _logger.LogDebug("Making playlist {PlaylistName} private by clearing shares", playlist.Name);
                    playlist.Shares = [];
                }
            }

            // Set the appropriate MediaType based on playlist content
            var mediaType = DeterminePlaylistMediaType(dto);
            SetPlaylistMediaType(playlist, mediaType);

            // Save the changes after updating PlaylistMediaType
            await playlist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            // Log the final state using OpenAccess property
            var finalOpenAccessProperty = playlist.GetType().GetProperty("OpenAccess");
            bool isFinallyPublic = finalOpenAccessProperty != null ? (bool)(finalOpenAccessProperty.GetValue(playlist) ?? false) : (playlist.Shares?.Any() ?? false);
            _logger.LogDebug("Playlist {PlaylistName} updated: OpenAccess = {OpenAccess}, Shares count = {SharesCount}",
                playlist.Name, isFinallyPublic, playlist.Shares?.Count ?? 0);

            // Handle custom images (apply if any exist, or clean up orphaned images)
            var hasCustomPrimaryImage = dto.CustomImages != null && dto.CustomImages.ContainsKey("Primary");
            await ApplyCustomImagesToPlaylistAsync(playlist, dto, cancellationToken).ConfigureAwait(false);

            // Auto-generate Primary cover only if NO custom PRIMARY image exists
            if (!hasCustomPrimaryImage)
            {
                await GeneratePlaylistCoverAsync(playlist, cancellationToken).ConfigureAwait(false);
            }

            // Apply custom metadata after metadata refresh to prevent providers from overwriting
            await ApplyCustomMetadataAsync(playlist, dto, user, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> CreateNewPlaylistAsync(string playlistName, User user, bool isPublic, LinkedChild[] linkedChildren, SmartPlaylistDto dto, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Creating new smart playlist {PlaylistName} with {ItemCount} items and {PublicStatus} status",
                playlistName, linkedChildren.Length, isPublic ? "public" : "private");

            var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = playlistName,
                UserId = user.Id,
                Public = isPublic,
            }).ConfigureAwait(false);

            _logger.LogDebug("Playlist creation result: ID = {PlaylistId}", result.Id);

            if (_libraryManager.GetItemById(result.Id) is Playlist newPlaylist)
            {
                _logger.LogDebug("Retrieved new playlist: Name = {Name}, Shares count = {SharesCount}, Public = {Public}",
                    newPlaylist.Name, newPlaylist.Shares?.Count ?? 0, (newPlaylist.Shares?.Any() ?? false));

                newPlaylist.LinkedChildren = linkedChildren;

                if (!string.IsNullOrEmpty(dto.Id))
                {
                    newPlaylist.SetProviderId(ProviderKeys.SmartLists, dto.Id);
                }

                // Set MediaType before persisting to avoid a second write
                var mediaType = DeterminePlaylistMediaType(dto);
                SetPlaylistMediaType(newPlaylist, mediaType);

                // Persist once with items + media type
                await newPlaylist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                // Log the final state after update
                _logger.LogDebug("After update - Playlist {PlaylistName}: Shares count = {SharesCount}, Public = {Public}",
                    newPlaylist.Name, newPlaylist.Shares?.Count ?? 0, (newPlaylist.Shares?.Any() ?? false));

                // Handle custom images (apply if any exist)
                var hasCustomPrimaryImage = dto.CustomImages != null && dto.CustomImages.ContainsKey("Primary");
                await ApplyCustomImagesToPlaylistAsync(newPlaylist, dto, cancellationToken).ConfigureAwait(false);

                // Auto-generate Primary cover only if NO custom PRIMARY image exists
                if (!hasCustomPrimaryImage)
                {
                    await GeneratePlaylistCoverAsync(newPlaylist, cancellationToken).ConfigureAwait(false);
                }

                // Apply custom metadata after metadata refresh to prevent providers from overwriting
                await ApplyCustomMetadataAsync(newPlaylist, dto, user, cancellationToken).ConfigureAwait(false);

                // Re-assert the tether: guard against anything having rebuilt provider IDs from
                // playlist.xml before the first save (historically the cover-art metadata refresh
                // did exactly that). Persisting it last also writes it into playlist.xml.
                if (!string.IsNullOrEmpty(dto.Id)
                    && !string.Equals(newPlaylist.GetProviderId(ProviderKeys.SmartLists), dto.Id, StringComparison.OrdinalIgnoreCase))
                {
                    newPlaylist.SetProviderId(ProviderKeys.SmartLists, dto.Id);
                    await newPlaylist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }

                return newPlaylist.Id.ToString("N");
            }
            else
            {
                _logger.LogWarning("Failed to retrieve newly created playlist with ID {PlaylistId}", result.Id);
                return string.Empty;
            }
        }

        private Task ApplyCustomMetadataAsync(BaseItem item, SmartListDto dto, User user, CancellationToken cancellationToken)
            => MetadataHelper.ApplyCustomMetadataAsync(item, dto, _logger, cancellationToken, user, _userDataManager);

        /// <summary>
        /// Applies custom images from the smart list configuration to the Jellyfin playlist.
        /// Also removes images that are no longer in CustomImages (e.g., after image type change).
        /// </summary>
        private async Task ApplyCustomImagesToPlaylistAsync(Playlist playlist, SmartPlaylistDto dto, CancellationToken cancellationToken)
        {
            if (_imageService == null || string.IsNullOrEmpty(dto.Id))
            {
                return;
            }

            try
            {
                var itemPath = playlist.ContainingFolderPath;
                if (string.IsNullOrEmpty(itemPath) || !Directory.Exists(itemPath))
                {
                    _logger.LogWarning("Cannot apply custom images: playlist path is invalid: {Path}", itemPath);
                    return;
                }

                var imageInfos = playlist.ImageInfos?.ToList() ?? [];
                var appliedImages = new List<(string Path, ImageType Type)>();
                var customImageTypes = new HashSet<ImageType>();

                // Check if this smart list has ever had custom images uploaded through our system
                // by checking if an image folder exists for this list (even if empty after deletions)
                var hasOrHadSmartListImages = _imageService.HasImageFolder(dto.Id);

                // Apply custom images if any exist
                if (dto.CustomImages != null && dto.CustomImages.Count > 0)
                {
                    foreach (var (imageTypeName, fileName) in dto.CustomImages)
                    {
                        // Get the source image path from the image service
                        var sourcePath = _imageService.GetImagePath(dto.Id, imageTypeName);

                        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                        {
                            _logger.LogWarning("Custom image not found: {ImageType} for playlist {PlaylistName}", imageTypeName, dto.Name);
                            continue;
                        }

                        // Parse the image type
                        if (!Enum.TryParse<ImageType>(imageTypeName, ignoreCase: true, out var imageType))
                        {
                            _logger.LogWarning("Invalid image type: {ImageType}", imageTypeName);
                            continue;
                        }

                        customImageTypes.Add(imageType);

                        // Determine destination filename
                        var extension = Path.GetExtension(sourcePath);
                        var destFileName = GetImageFileName(imageType, extension);
                        var destPath = Path.Combine(itemPath, destFileName);

                        // Copy the image to the playlist folder. With the badge enabled, covers
                        // are center-cropped to Jellyfin's tile proportions at native resolution
                        // and stamped, keeping the badge visible in every view (documented
                        // trade-off: detail pages show the crop instead of the original
                        // proportions). With the badge disabled, uploads are copied untouched.
                        // Raw copy fallback for formats ImageSharp cannot decode, e.g. SVG.
                        var stamped = false;
                        if (CoverBadgeHelper.IsEnabled && (imageType == ImageType.Primary || imageType == ImageType.Thumb))
                        {
                            stamped = await CollageBuilder.TryCreateBadgedTileCoverAsync(sourcePath, destPath, imageType, forPlaylist: true, 0, _logger, cancellationToken).ConfigureAwait(false);
                        }

                        if (!stamped)
                        {
                            File.Copy(sourcePath, destPath, overwrite: true);
                        }

                        _logger.LogDebug("Copied custom {ImageType} image to playlist: {DestPath}", imageTypeName, destPath);

                        // Remove same-slot files after replacement so folder.jpg and folder.jpeg cannot compete.
                        _imageService.DeleteJellyfinImageFilesForType(itemPath, imageType, destPath, cancellationToken);

                        // A custom Primary replaces our auto-generated collage; remove the leftover file.
                        if (imageType == ImageType.Primary)
                        {
                            TryDeleteCoverFile(Path.Combine(itemPath, CollageBuilder.CollageFileName), dto.Name);
                        }

                        // Remove existing images of the same type
                        imageInfos.RemoveAll(i => i.Type == imageType);

                        // Add the new image info
                        var imageInfo = new ItemImageInfo
                        {
                            Path = destPath,
                            Type = imageType,
                            DateModified = DateTime.UtcNow
                        };
                        imageInfos.Add(imageInfo);
                        appliedImages.Add((destPath, imageType));
                    }
                }

                // Only clean up orphaned images if this smart list has/had custom images through our system
                // This prevents removing user-added images from Jellyfin when no smart list images were ever uploaded
                var removedAny = false;
                if (hasOrHadSmartListImages)
                {
                    removedAny = RemoveOrphanedCustomImages(playlist, itemPath, imageInfos, customImageTypes, dto.Id);
                }

                if (appliedImages.Count > 0 || removedAny)
                {
                    playlist.ImageInfos = imageInfos.ToArray();
                    await playlist.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("Applied {AppliedCount} custom image(s) to playlist '{PlaylistName}', removed orphaned: {RemovedAny}",
                        appliedImages.Count, dto.Name, removedAny);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply custom images to playlist '{PlaylistName}'", dto.Name);
            }
        }

        /// <summary>
        /// Cleans up orphaned images from SmartLists storage that are no longer in the CustomImages dictionary.
        /// This handles cases where image type changed (e.g., Primary -> Banner) or images were removed.
        /// NOTE: This only cleans up SmartLists storage - it does NOT touch Jellyfin playlist images.
        /// Jellyfin images are only removed through explicit delete operations (DeleteImageFromPlaylistAsync).
        /// </summary>
        /// <param name="playlist">The Jellyfin playlist (for logging).</param>
        /// <param name="itemPath">Path to the playlist folder (unused, kept for signature compatibility).</param>
        /// <param name="imageInfos">List of image infos (unused, kept for signature compatibility).</param>
        /// <param name="customImageTypes">Image types currently in dto.CustomImages.</param>
        /// <param name="smartListId">The SmartList ID (dto.Id) for checking our image storage.</param>
        /// <returns>True if any orphaned images were found in SmartLists storage.</returns>
        private bool RemoveOrphanedCustomImages(Playlist playlist, string itemPath, List<ItemImageInfo> imageInfos, HashSet<ImageType> customImageTypes, string smartListId)
        {
            if (_imageService == null || string.IsNullOrEmpty(smartListId))
            {
                return false;
            }

            // Get all images currently in SmartLists storage for this list
            var storedImages = _imageService.GetImagesForSmartList(smartListId);
            if (storedImages.Count == 0)
            {
                return false;
            }

            bool foundOrphans = false;

            // Check each stored image - if it's not in customImageTypes, it's orphaned in our storage
            foreach (var (imageTypeName, fileName) in storedImages)
            {
                if (!Enum.TryParse<ImageType>(imageTypeName, ignoreCase: true, out var imageType))
                {
                    continue;
                }

                // If this image type is not in the current CustomImages, it's orphaned
                if (!customImageTypes.Contains(imageType))
                {
                    _logger.LogDebug("Found orphaned image in SmartLists storage: {ImageType} for playlist {PlaylistName}",
                        imageTypeName, playlist.Name);
                    foundOrphans = true;

                    // Note: We don't delete from SmartLists storage here - that's handled by the
                    // explicit delete API when user removes an image. This method just detects orphans.
                    // The orphaned source file in SmartLists storage is harmless and will be cleaned
                    // up if the smart list is deleted.
                }
            }

            return foundOrphans;
        }

        /// <summary>
        /// Gets the standard Jellyfin filename for an image type.
        /// Delegates to the shared helper in SmartListImageService.
        /// </summary>
        private static string GetImageFileName(ImageType imageType, string extension)
            => SmartListImageService.GetJellyfinImageFileName(imageType, extension);

        // Removed: legacy name-based lookup helper (no longer used after migration to JellyfinPlaylistId)

        /// <summary>
        /// Validates that the given media types do not contain types unsupported by playlists.
        /// Returns (true, "", "") if valid, or (false, errorMessage, "") if an unsupported type is found.
        /// </summary>
        private (bool IsValid, string ErrorMessage, string Extra) ValidateUnsupportedMediaTypes(List<string>? mediaTypes, string playlistName)
        {
            if (mediaTypes?.Contains(Core.Constants.MediaTypes.Series) == true)
            {
                _logger.LogError(
                    "Smart playlist '{PlaylistName}' uses '{MediaType}' media type. Series playlists are not supported due to Jellyfin playlist limitations. Use '{SuggestedType}' media type instead, or create a Collection for Series support. Skipping playlist refresh.",
                    playlistName, Core.Constants.MediaTypes.Series, Core.Constants.MediaTypes.Episode);
                return (false, $"{Core.Constants.MediaTypes.Series} media type is not supported for Playlists. Use {Core.Constants.MediaTypes.Episode} media type, or create a Collection instead.", string.Empty);
            }

            if (mediaTypes?.Contains(Core.Constants.MediaTypes.Season) == true)
            {
                _logger.LogError(
                    "Smart playlist '{PlaylistName}' uses '{MediaType}' media type. Season playlists are not supported due to Jellyfin playlist limitations. Use '{SuggestedType}' media type instead, or create a Collection for Season support. Skipping playlist refresh.",
                    playlistName, Core.Constants.MediaTypes.Season, Core.Constants.MediaTypes.Episode);
                return (false, $"{Core.Constants.MediaTypes.Season} media type is not supported for Playlists. Use {Core.Constants.MediaTypes.Episode} media type, or create a Collection instead.", string.Empty);
            }

            if (mediaTypes?.Contains(Core.Constants.MediaTypes.MusicAlbum) == true)
            {
                _logger.LogError(
                    "Smart playlist '{PlaylistName}' uses '{MediaType}' media type. MusicAlbum playlists are not supported due to Jellyfin playlist limitations. Use '{SuggestedType}' media type instead, or create a Collection for Album support. Skipping playlist refresh.",
                    playlistName, Core.Constants.MediaTypes.MusicAlbum, Core.Constants.MediaTypes.Audio);
                return (false, $"{Core.Constants.MediaTypes.MusicAlbum} media type is not supported for Playlists. Use {Core.Constants.MediaTypes.Audio} media type, or create a Collection instead.", string.Empty);
            }

            return (true, string.Empty, string.Empty);
        }

        private User? GetPlaylistUser(SmartPlaylistDto playlist)
        {
            // Parse User field and get the user
            // DEPRECATED: playlist.UserId is for backwards compatibility with old single-user playlists.
            // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
            if (!string.IsNullOrEmpty(playlist.UserId) && Guid.TryParse(playlist.UserId, out var userId) && userId != Guid.Empty)
            {
                return _userManager.GetUserById(userId);
            }

            return null;
        }
        /// <summary>
        /// Gets all user media for a playlist, filtering by the specified media types.
        /// </summary>
        /// <param name="user">The user to get media for.</param>
        /// <param name="mediaTypes">The media types to filter by. Must be non-null and non-empty; will throw InvalidOperationException if null or empty.</param>
        /// <returns>Enumerable of BaseItem matching the specified media types.</returns>
        public IEnumerable<BaseItem> GetAllUserMediaForPlaylist(User user, List<string> mediaTypes)
        {
            return GetAllUserMediaForPlaylist(user, mediaTypes, null, null);
        }

        public IEnumerable<BaseItem> GetAllUserMediaForPlaylist(User user, List<string> mediaTypes, SmartPlaylistDto? dto = null)
        {
            return GetAllUserMediaForPlaylist(user, mediaTypes, dto, null);
        }

        public IEnumerable<BaseItem> GetAllUserMediaForPlaylist(User user, List<string> mediaTypes, SmartPlaylistDto? dto, ConcurrentDictionary<Guid, Guid>? extraOwnerMap)
        {
            // Validate media types before processing (always validate, not just when dto is provided)
            _logger?.LogDebug("GetAllUserMediaForPlaylist validation{PlaylistName}: MediaTypes={MediaTypes}", 
                dto != null ? $" for '{dto.Name}'" : "", 
                mediaTypes != null ? string.Join(",", mediaTypes) : "null");

            var unsupportedCheck = ValidateUnsupportedMediaTypes(mediaTypes, dto?.Name ?? "Unknown");
            if (!unsupportedCheck.IsValid)
            {
                throw new InvalidOperationException(unsupportedCheck.ErrorMessage);
            }

            if (mediaTypes == null || mediaTypes.Count == 0)
            {
                var playlistName = dto?.Name ?? "Unknown";
                _logger?.LogError("Smart playlist '{PlaylistName}' has no media types specified. At least one media type must be selected.", playlistName);
                throw new InvalidOperationException("No media types specified. At least one media type must be selected.");
            }

            return GetAllUserMedia(user, mediaTypes, dto, extraOwnerMap);
        }

        private IEnumerable<BaseItem> GetAllUserMedia(User user, List<string>? mediaTypes = null, SmartPlaylistDto? dto = null, ConcurrentDictionary<Guid, Guid>? extraOwnerMap = null)
        {
            var baseItemKinds = MediaTypeConverter.GetBaseItemKindsFromMediaTypes(mediaTypes, dto, _logger);

            // Build a set of valid TopParentIds from VirtualFolder physical locations.
            // VirtualFolderInfo.ItemId is the CollectionFolder ID which differs from items' TopParentId
            // (which points to the physical folder). We resolve physical folder IDs via FindByPath.
            var validTopParentIds = GetLibraryTopParentIds();

            var includeVirtualItems = SmartListUtilities.UsesLibraryNameRule(dto);
            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = baseItemKinds,
                Recursive = true,
            };

            LibraryManagerHelper.ApplyVirtualItemQueryScope(query, includeVirtualItems, validTopParentIds);

            var items = _libraryManager.GetItemsResult(query).Items;

            if (dto?.IncludeExtras != true)
            {
                return items;
            }

            var extras = LibraryManagerHelper.FetchExtras(
                _libraryManager, user, validTopParentIds, items, extraOwnerMap, _logger, dto.Name);

            return items.Concat(extras);
        }

        private Guid[] GetLibraryTopParentIds() => LibraryManagerHelper.GetLibraryTopParentIds(_libraryManager);

        /// <summary>
        /// Builds the ordered bumper item pool by running the playlist's bumper rule sets
        /// through a synthetic SmartList. Items already in the main list are excluded so an
        /// item is never both content and bumper. Bumper media is added to
        /// <paramref name="mediaLookup"/> so LinkedChildren can be created for woven entries.
        /// </summary>
        private List<Guid> GetBumperItemIds(
            SmartPlaylistDto dto,
            User user,
            RefreshQueueService.RefreshCache refreshCache,
            List<Guid> mainItemIds,
            Dictionary<Guid, BaseItem> mediaLookup,
            ILogger logger)
        {
            try
            {
                var bumpers = dto.Bumpers!;

                // Defense in depth: bumpers are woven into playlists, so bumper media types
                // must be playlist-supported. The validator rejects these at save time; this
                // guard covers lists saved before that check existed.
                var bumperMediaCheck = ValidateUnsupportedMediaTypes(bumpers.MediaTypes, dto.Name + " (Bumpers)");
                if (!bumperMediaCheck.IsValid)
                {
                    logger.LogWarning("Skipping bumpers for playlist '{PlaylistName}': {Error}", dto.Name, bumperMediaCheck.ErrorMessage);
                    return [];
                }

                var sortOption = bumpers.BumperOrder switch
                {
                    "Name" => new SortOption { SortBy = "Name", SortOrder = SortOrder.Ascending },
                    "ReleaseDate" => new SortOption { SortBy = "ReleaseDate", SortOrder = SortOrder.Ascending },
                    _ => new SortOption { SortBy = "Random", SortOrder = SortOrder.Ascending },
                };

                var bumperDto = new SmartPlaylistDto
                {
                    // The SmartList ctor requires a non-null Id; derive one from the parent playlist.
                    Id = (dto.Id ?? Guid.NewGuid().ToString("N")) + "-bumpers",
                    Name = dto.Name + " (Bumpers)",
                    ExpressionSets = bumpers.ExpressionSets,
                    MediaTypes = bumpers.MediaTypes ?? [],
                    Order = new OrderDto { SortOptions = [sortOption] },

                    // Extras (trailers, interstitials, etc.) are the archetypal bumper
                    // content, so bumper pools always fetch them; bumper rules still filter.
                    IncludeExtras = true,
                };

                var bumperList = new Core.SmartList(bumperDto)
                {
                    UserManager = _userManager // Set UserManager for Jellyfin 10.11+ user resolution,
                };
                var bumperMedia = GetAllUserMedia(user, bumperDto.MediaTypes, bumperDto).ToArray();
                var bumperIds = bumperList.FilterPlaylistItems(bumperMedia, _libraryManager, user, refreshCache, _userDataManager, logger, null);

                var mainSet = new HashSet<Guid>(mainItemIds);

                // Single pass over bumperMedia resolving only the ids the filter returned,
                // instead of building a GroupBy lookup over the entire bumper scan.
                var bumperIdSet = new HashSet<Guid>(bumperIds);
                var matchedBumperItems = new Dictionary<Guid, BaseItem>(bumperIdSet.Count);
                foreach (var mediaItem in bumperMedia)
                {
                    if (bumperIdSet.Contains(mediaItem.Id))
                    {
                        matchedBumperItems.TryAdd(mediaItem.Id, mediaItem);
                    }
                }

                var result = new List<Guid>();
                var seen = new HashSet<Guid>();
                foreach (var id in bumperIds)
                {
                    // LibraryName virtual-item queries can emit duplicate ids - skip repeats
                    // so the woven pool never contains the same bumper twice in a row.
                    if (mainSet.Contains(id) || !seen.Add(id) || !matchedBumperItems.TryGetValue(id, out var item))
                    {
                        continue;
                    }

                    result.Add(id);
                    mediaLookup.TryAdd(id, item);
                }

                return result;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to build bumper pool for playlist '{PlaylistName}'. Playlist will be written without bumpers.", dto.Name);
                return [];
            }
        }

        /// <summary>
        /// Inserts one bumper after every <paramref name="interval"/> main items, cycling
        /// through the bumper pool with wraparound. No bumper follows the final main item.
        /// Main item order is never altered.
        /// </summary>
        internal static List<Guid> WeaveBumpers(List<Guid> mainItemIds, List<Guid> bumperItemIds, int interval)
        {
            var result = new List<Guid>(mainItemIds.Count + (mainItemIds.Count / interval) + 1);
            int bumperIndex = 0;
            for (int i = 0; i < mainItemIds.Count; i++)
            {
                result.Add(mainItemIds[i]);
                bool isLast = i == mainItemIds.Count - 1;
                if (!isLast && (i + 1) % interval == 0)
                {
                    result.Add(bumperItemIds[bumperIndex % bumperItemIds.Count]);
                    bumperIndex++;
                }
            }

            return result;
        }

        /// <summary>
        /// Generates the playlist's Primary cover image from its items: a single item's poster
        /// (badged copy when the badge is enabled) or a square 2x2 collage for 2+ items.
        /// The plugin always generates playlist covers itself (instead of triggering Jellyfin's
        /// refresh-based generation) so covers are consistent with collections and can carry
        /// the smart list badge. Only called when no custom Primary image is uploaded via Smart List.
        /// </summary>
        private async Task GeneratePlaylistCoverAsync(Playlist playlist, CancellationToken cancellationToken)
        {
            try
            {
                var itemPath = playlist.ContainingFolderPath;
                if (string.IsNullOrEmpty(itemPath) || !Directory.Exists(itemPath))
                {
                    _logger.LogWarning("Cannot generate cover: playlist path is invalid: {Path}", itemPath);
                    return;
                }

                var collagePath = Path.Combine(itemPath, CollageBuilder.CollageFileName);

                // Respect covers uploaded directly through Jellyfin's UI: those are saved as
                // folder.<ext> inside the playlist folder (core's own generated covers live in
                // the internal metadata dir, and the SmartLists custom-image flow is guarded
                // upstream), so any file in the playlist folder that isn't our generated
                // collage is a manual upload - leave it completely untouched.
                var currentPrimary = playlist.ImageInfos?.FirstOrDefault(i => i.Type == ImageType.Primary);
                if (IsManuallyUploadedPlaylistCover(currentPrimary, itemPath))
                {
                    _logger.LogDebug("Playlist {PlaylistName} has a manually uploaded cover, skipping cover generation", playlist.Name);
                    return;
                }

                // Resolve linked children to items and collect the first four distinct Primary
                // image paths (falling back to the parent's image, e.g. album art for audio tracks).
                var imagePaths = new List<string>();
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var linkedChild in playlist.LinkedChildren ?? [])
                {
                    if (!linkedChild.ItemId.HasValue)
                    {
                        continue;
                    }

                    var item = _libraryManager.GetItemById(linkedChild.ItemId.Value);
                    if (item == null)
                    {
                        continue;
                    }

                    var imagePath = GetPrimaryImagePath(item, seenPaths);
                    if (imagePath != null && seenPaths.Add(imagePath))
                    {
                        imagePaths.Add(imagePath);
                    }

                    if (imagePaths.Count >= 4)
                    {
                        break;
                    }
                }

                if (imagePaths.Count == 0)
                {
                    // No usable item images - clear the cover the plugin set previously, so the
                    // playlist doesn't keep showing an item it no longer contains. The manual-
                    // upload guard above already returned for user-uploaded covers, so the
                    // current Primary is either our generated collage or a direct item-image
                    // reference; only our own collage file is ever deleted from disk.
                    TryDeleteCoverFile(collagePath, playlist.Name);

                    var primary = playlist.ImageInfos?.FirstOrDefault(i => i.Type == ImageType.Primary);
                    if (primary != null)
                    {
                        playlist.ImageInfos = playlist.ImageInfos!.Where(i => i != primary).ToArray();
                        await playlist.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
                        _logger.LogDebug("Cleared cover for playlist {PlaylistName} (no item images available)", playlist.Name);
                    }

                    return;
                }

                string coverPath;
                if (imagePaths.Count == 1)
                {
                    // Single image: with the badge enabled, a square center-cropped badged copy
                    // (Jellyfin's square playlist tiles would hide a corner badge on a poster-
                    // ratio cover); with it disabled, or when the source can't be decoded,
                    // reference the item's image untouched (native Jellyfin behavior). Never
                    // modifies the item's own file.
                    coverPath = CoverBadgeHelper.IsEnabled
                        && await CollageBuilder.TryCreateBadgedTileCoverAsync(imagePaths[0], collagePath, ImageType.Primary, forPlaylist: true, 600, _logger, cancellationToken).ConfigureAwait(false)
                        ? collagePath
                        : imagePaths[0];
                }
                else
                {
                    // Square 2x2 collage, matching the covers Jellyfin generates for playlists.
                    // Cycle the available images to fill all four quadrants (like collections do).
                    var collageSources = new List<string>(4);
                    for (int i = 0; i < 4; i++)
                    {
                        collageSources.Add(imagePaths[i % imagePaths.Count]);
                    }

                    await CollageBuilder.CreateGridCollageAsync(
                        collageSources,
                        collagePath,
                        600,
                        600,
                        CoverBadgeHelper.IsEnabled,
                        _logger,
                        cancellationToken).ConfigureAwait(false);
                    coverPath = collagePath;
                }

                // Remove a stale collage file when the cover now references an item's image directly.
                if (!string.Equals(coverPath, collagePath, StringComparison.OrdinalIgnoreCase) && File.Exists(collagePath))
                {
                    TryDeleteCoverFile(collagePath, playlist.Name);
                }

                playlist.SetImage(new ItemImageInfo
                {
                    Path = coverPath,
                    Type = ImageType.Primary
                }, 0);

                await playlist.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Generated cover for playlist {PlaylistName} from {ImageCount} image(s)", playlist.Name, imagePaths.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to generate cover image for playlist {PlaylistName}", playlist.Name);
            }
        }

        /// <summary>
        /// Gets an item's Primary image path, falling back to its parent's Primary image
        /// (e.g. album art for audio tracks). Episodes are represented by their series
        /// poster instead of the episode screenshot, matching Jellyfin's native playlist
        /// covers. Paths already in <paramref name="seenPaths"/> are returned without a
        /// filesystem check - large playlists hit the same series/album image over and
        /// over, and the caller dedupes anyway. Returns null when no image exists on disk.
        /// </summary>
        private static string? GetPrimaryImagePath(BaseItem item, HashSet<string> seenPaths)
        {
            if (item is MediaBrowser.Controller.Entities.TV.Episode episode && episode.Series != null)
            {
                var seriesPath = episode.Series.ImageInfos?.FirstOrDefault(i => i.Type == ImageType.Primary)?.Path;
                if (!string.IsNullOrEmpty(seriesPath) && (seenPaths.Contains(seriesPath) || File.Exists(seriesPath)))
                {
                    return seriesPath;
                }
            }

            var path = item.ImageInfos?.FirstOrDefault(i => i.Type == ImageType.Primary)?.Path;
            if (!string.IsNullOrEmpty(path) && (seenPaths.Contains(path) || File.Exists(path)))
            {
                return path;
            }

            path = item.GetParent()?.ImageInfos?.FirstOrDefault(i => i.Type == ImageType.Primary)?.Path;
            return !string.IsNullOrEmpty(path) && (seenPaths.Contains(path) || File.Exists(path)) ? path : null;
        }

        /// <summary>
        /// Determines whether the playlist's current Primary image is a cover uploaded
        /// manually (e.g. through Jellyfin's own Edit Images UI). Manual covers live inside
        /// the playlist folder under a name other than the plugin's generated collage.
        /// A dangling path (file deleted by hand) is not treated as manual, so generation
        /// can recover the cover.
        /// </summary>
        private static bool IsManuallyUploadedPlaylistCover(ItemImageInfo? primary, string itemPath)
        {
            if (primary == null || string.IsNullOrEmpty(primary.Path) || !File.Exists(primary.Path))
            {
                return false;
            }

            if (!FileSystemHelper.IsPathInsideFolder(primary.Path, itemPath))
            {
                // Outside the playlist folder: an item's image we referenced ourselves, or a
                // core-generated cover in the internal metadata dir - safe to regenerate.
                return false;
            }

            return !string.Equals(Path.GetFileName(primary.Path), CollageBuilder.CollageFileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Safely attempts to delete an auto-generated cover file, logging any errors.
        /// </summary>
        private void TryDeleteCoverFile(string filePath, string playlistName)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogDebug("Deleted auto-generated cover file for playlist {PlaylistName}", playlistName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete auto-generated cover file for playlist {PlaylistName}", playlistName);
            }
        }

        /// <summary>
        /// Determines the appropriate MediaType based on playlist content.
        /// </summary>
        /// <param name="dto">The smart playlist DTO</param>
        /// <returns>"Video" for video content, "Audio" for audio content</returns>
        private string DeterminePlaylistMediaType(SmartPlaylistDto dto)
        {
            if (dto.MediaTypes?.Count > 0)
            {
                // Check if it's audio-only (Audio or AudioBook)
                if (dto.MediaTypes.All(mt => Core.Constants.MediaTypes.AudioOnlySet.Contains(mt)))
                {
                    _logger.LogDebug("Playlist {PlaylistName} contains only audio content, setting MediaType to Audio", dto.Name);
                    return Core.Constants.MediaTypes.Audio;
                }

                bool hasVideoContent = dto.MediaTypes.Any(mt => Core.Constants.MediaTypes.NonAudioSet.Contains(mt));
                bool hasAudioContent = dto.MediaTypes.Any(mt => Core.Constants.MediaTypes.AudioOnlySet.Contains(mt));

                if (hasVideoContent && !hasAudioContent)
                {
                    _logger.LogDebug("Playlist {PlaylistName} contains only non-audio content, setting MediaType to Video", dto.Name);
                    return Core.Constants.MediaTypes.Video;
                }
            }

            // Default to Audio for mixed/unknown content (Jellyfin standard)
            _logger.LogDebug("Playlist {PlaylistName} has mixed/unknown content, defaulting to Audio", dto.Name);
            return Core.Constants.MediaTypes.Audio;
        }


        /// <summary>
        /// Sets the MediaType of a Jellyfin playlist using reflection (similar to IsPublic implementation).
        /// </summary>
        /// <param name="playlist">The playlist object</param>
        /// <param name="mediaType">The media type to set ("Video" or "Audio")</param>
        private void SetPlaylistMediaType(Playlist playlist, string mediaType)
        {
            try
            {
                var playlistMediaTypeProperty = playlist.GetType().GetProperty("PlaylistMediaType");

                if (playlistMediaTypeProperty != null && playlistMediaTypeProperty.CanWrite)
                {
                    var currentValue = playlistMediaTypeProperty.GetValue(playlist)?.ToString() ?? "null";
                    _logger.LogDebug("Current PlaylistMediaType value for playlist {PlaylistName}: {CurrentValue}", playlist.Name, currentValue);

                    // Convert string to MediaType enum if needed
                    object mediaTypeValue;
                    if (playlistMediaTypeProperty.PropertyType == typeof(string))
                    {
                        mediaTypeValue = mediaType;
                    }
                    else if (playlistMediaTypeProperty.PropertyType.IsEnum)
                    {
                        // Try to parse as enum (e.g., MediaType.Video, MediaType.Audio)
                        if (Enum.TryParse(playlistMediaTypeProperty.PropertyType, mediaType, true, out var enumValue))
                        {
                            mediaTypeValue = enumValue;
                        }
                        else
                        {
                            _logger.LogWarning("Could not parse {MediaType} as {EnumType} for playlist {PlaylistName}", mediaType, playlistMediaTypeProperty.PropertyType.Name, playlist.Name);
                            return;
                        }
                    }
                    else if (playlistMediaTypeProperty.PropertyType.IsGenericType && playlistMediaTypeProperty.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
                    {
                        // Handle nullable enum (MediaType?)
                        var underlyingType = Nullable.GetUnderlyingType(playlistMediaTypeProperty.PropertyType);
                        if (underlyingType != null && underlyingType.IsEnum)
                        {
                            if (Enum.TryParse(underlyingType, mediaType, true, out var enumValue))
                            {
                                mediaTypeValue = enumValue;
                            }
                            else
                            {
                                _logger.LogWarning("Could not parse {MediaType} as nullable {EnumType} for playlist {PlaylistName}", mediaType, underlyingType.Name, playlist.Name);
                                return;
                            }
                        }
                        else
                        {
                            mediaTypeValue = mediaType;
                        }
                    }
                    else
                    {
                        mediaTypeValue = mediaType;
                    }

                    try
                    {
                        _logger.LogDebug("Setting playlist {PlaylistName} PlaylistMediaType to {Value} (Type: {ValueType})",
                            playlist.Name, mediaTypeValue, mediaTypeValue?.GetType()?.Name ?? "null");

                        playlistMediaTypeProperty.SetValue(playlist, mediaTypeValue);

                        var newValue = playlistMediaTypeProperty.GetValue(playlist)?.ToString() ?? "null";
                        _logger.LogDebug("Successfully set playlist {PlaylistName} PlaylistMediaType from {OldValue} to {NewValue}",
                            playlist.Name, currentValue, newValue);
                    }
                    catch (Exception setEx)
                    {
                        _logger.LogError(setEx, "Failed to set PlaylistMediaType property on playlist {PlaylistName}", playlist.Name);
                    }
                }
                else
                {
                    _logger.LogWarning("PlaylistMediaType property not found or not writable on playlist {PlaylistName}.", playlist.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting playlist {PlaylistName} MediaType to {MediaType}", playlist.Name, mediaType);
            }
        }

        /// <summary>
        /// Deletes duplicate Jellyfin playlists that carry this smart playlist's provider-ID
        /// tether for this user but are not the tracked playlist. The tether proves the plugin
        /// created them, so deletion cannot hit user-created playlists.
        /// </summary>
        private void DeleteOrphanedTetheredPlaylists(SmartPlaylistDto dto, User user, Guid canonicalPlaylistId)
        {
            if (string.IsNullOrEmpty(dto.Id))
            {
                return;
            }

            try
            {
                var query = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.Playlist],
                    Recursive = true,
                };

                var orphans = _libraryManager.GetItemsResult(query).Items
                    .OfType<Playlist>()
                    .Where(p => p.Id != canonicalPlaylistId
                        && p.OwnerUserId == user.Id
                        && string.Equals(p.GetProviderId(ProviderKeys.SmartLists), dto.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var orphan in orphans)
                {
                    try
                    {
                        _logger.LogWarning("Deleting orphaned duplicate playlist '{PlaylistName}' ({PlaylistId}) tethered to smart playlist {SmartListId} for user {UserId}",
                            orphan.Name, orphan.Id, dto.Id, user.Id);
                        _libraryManager.DeleteItem(orphan, new DeleteOptions { DeleteFileLocation = true }, true);
                    }
                    catch (Exception deleteEx)
                    {
                        // Per-orphan catch so one failed deletion doesn't strand the rest
                        _logger.LogWarning(deleteEx, "Failed to delete orphaned duplicate playlist '{PlaylistName}' ({PlaylistId}), continuing with remaining orphans",
                            orphan.Name, orphan.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up orphaned duplicate playlists for '{PlaylistName}', continuing", dto.Name);
            }
        }

        /// <summary>
        /// Helper method to delete all Jellyfin playlists for a smart playlist across all users.
        /// Handles both multi-user playlists (UserPlaylists array) and legacy single-user playlists.
        /// This centralizes the deletion logic used by disable, delete, and visibility schedule operations.
        /// Successfully deleted playlists have their stored Jellyfin playlist IDs cleared on the DTO;
        /// failed deletions keep their IDs so deletion can be retried later. Callers are responsible
        /// for persisting the DTO.
        /// </summary>
        /// <param name="playlistDto">The smart playlist DTO containing Jellyfin playlist IDs to delete</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task DeleteAllJellyfinPlaylistsForUsersAsync(SmartPlaylistDto playlistDto, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(playlistDto);

            // Delete all Jellyfin playlists for all users
            if (playlistDto.UserPlaylists != null && playlistDto.UserPlaylists.Count > 0)
            {
                _logger.LogDebug("Deleting {Count} Jellyfin playlists for multi-user playlist '{PlaylistName}'",
                    playlistDto.UserPlaylists.Count, playlistDto.Name);

                foreach (var userMapping in playlistDto.UserPlaylists)
                {
                    if (!string.IsNullOrEmpty(userMapping.JellyfinPlaylistId))
                    {
                        try
                        {
                            // Create a temporary DTO for deletion
                            var tempDto = new SmartPlaylistDto
                            {
                                Id = playlistDto.Id,
                                Name = playlistDto.Name,
                                UserId = userMapping.UserId,
                                JellyfinPlaylistId = userMapping.JellyfinPlaylistId
                            };
                            await DeleteAsync(tempDto, cancellationToken).ConfigureAwait(false);
                            _logger.LogDebug("Deleted Jellyfin playlist {JellyfinPlaylistId} for user {UserId}",
                                userMapping.JellyfinPlaylistId, userMapping.UserId);
                            // Clear the mapping only on successful deletion; a failed delete keeps
                            // its ID so the playlist can be found and deleted on a later attempt
                            userMapping.JellyfinPlaylistId = null;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete Jellyfin playlist {JellyfinPlaylistId} for user {UserId}, continuing",
                                userMapping.JellyfinPlaylistId, userMapping.UserId);
                        }
                    }
                }

                // Keep the backwards-compatibility field in sync (first user's playlist)
                playlistDto.JellyfinPlaylistId = playlistDto.UserPlaylists[0].JellyfinPlaylistId;
            }
            else if (!string.IsNullOrEmpty(playlistDto.JellyfinPlaylistId))
            {
                // Fallback to single playlist deletion (backwards compatibility)
                _logger.LogDebug("Deleting Jellyfin playlist {JellyfinPlaylistId} for playlist '{PlaylistName}'",
                    playlistDto.JellyfinPlaylistId, playlistDto.Name);
                try
                {
                    await DeleteAsync(playlistDto, cancellationToken).ConfigureAwait(false);
                    playlistDto.JellyfinPlaylistId = null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete Jellyfin playlist for '{PlaylistName}'", playlistDto.Name);
                }
            }
        }

    }
}
