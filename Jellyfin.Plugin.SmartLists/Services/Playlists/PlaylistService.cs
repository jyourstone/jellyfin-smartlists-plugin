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
using MediaBrowser.Controller.Providers;
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
        private readonly IProviderManager _providerManager;
        private readonly SmartListImageService? _imageService;
        private readonly ExternalListService? _externalListService;

        public PlaylistService(
            IUserManager userManager,
            ILibraryManager libraryManager,
            IPlaylistManager playlistManager,
            IUserDataManager userDataManager,
            ILogger<PlaylistService> logger,
            IProviderManager providerManager,
            SmartListImageService? imageService = null,
            ExternalListService? externalListService = null)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
            _playlistManager = playlistManager;
            _userDataManager = userDataManager;
            _logger = logger;
            _providerManager = providerManager;
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

                // Pre-fetch external lists if any ExternalList rules are present
                if (_externalListService != null && dto.ExpressionSets != null)
                {
                    var fieldReqs = FieldRequirements.Analyze(dto.ExpressionSets);
                    if (fieldReqs.NeedsExternalLists && fieldReqs.ExternalListUrls.Count > 0)
                    {
                        // Clear per-item external list caches so items are re-evaluated against this playlist's lists.
                        // ExternalListData (the fetched list data) is kept since it's shared and additive across playlists.
                        refreshCache.ItemExternalLists.Clear();
                        refreshCache.ExternalListPositions.Clear();

                        logger.LogDebug("Pre-fetching {Count} external list(s) for playlist '{PlaylistName}'", fieldReqs.ExternalListUrls.Count, dto.Name);
                        await _externalListService.PreFetchListsAsync(fieldReqs.ExternalListUrls, refreshCache, cancellationToken).ConfigureAwait(false);
                    }
                }

                var newItems = smartPlaylist.FilterPlaylistItems(allUserMedia, _libraryManager, user, refreshCache, _userDataManager, logger, progressCallback).ToArray();
                logger.LogDebug("Playlist {PlaylistName} filtered to {FilteredCount} items from {TotalCount} total items",
                    dto.Name, newItems.Length, allUserMedia.Length);

                // Create a lookup dictionary for O(1) access while preserving order from newItems
                var mediaLookup = allUserMedia.ToDictionary(m => m.Id, m => m);
                var newLinkedChildren = newItems
                    .Where(itemId => mediaLookup.ContainsKey(itemId))
                    .Select(itemId => new LinkedChild { ItemId = itemId, Path = mediaLookup[itemId].Path })
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

                // Now that we've found the existing playlist (or not), apply the new naming format
                var smartPlaylistName = NameFormatter.FormatPlaylistName(dto.Name);

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

                    // Update the playlist if any changes are needed
                    if (nameChanged || ownershipChanged || publicStatusChanged)
                    {
                        await existingPlaylist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
                        logger.LogDebug("Updated existing playlist: {PlaylistName}", existingPlaylist.Name);
                    }

                    // Update the playlist items (includes metadata refresh)
                    await UpdatePlaylistPublicStatusAsync(existingPlaylist, dto.Public, newLinkedChildren, dto, cancellationToken);

                    logger.LogDebug("Successfully updated existing playlist: {PlaylistName} with {ItemCount} items",
                        existingPlaylist.Name, newLinkedChildren.Length);

                    return (true, $"Updated playlist '{existingPlaylist.Name}' with {newLinkedChildren.Length} items", existingPlaylist.Id.ToString("N"));
                }
                else
                {
                    // Create new playlist
                    logger.LogDebug("Creating new playlist: {PlaylistName}", smartPlaylistName);

                    var newPlaylistId = await CreateNewPlaylistAsync(smartPlaylistName, user.Id, dto.Public, newLinkedChildren, dto, cancellationToken);

                    // Check if playlist creation actually succeeded
                    if (string.IsNullOrEmpty(newPlaylistId))
                    {
                        logger.LogError("Failed to create playlist '{PlaylistName}' - no valid playlist ID returned", smartPlaylistName);
                        return (false, $"Failed to create playlist '{smartPlaylistName}' - the playlist could not be retrieved after creation", string.Empty);
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

                if (dto.MediaTypes?.Contains(Core.Constants.MediaTypes.Series) == true)
                {
                    _logger.LogError("Smart playlist '{PlaylistName}' uses 'Series' media type. Series playlists are not supported due to Jellyfin playlist limitations. Use 'Episode' media type instead, or create a Collection for Series support. Skipping playlist refresh.", dto.Name);
                    return (false, "Series media type is not supported for Playlists. Use Episode media type, or create a Collection instead.", string.Empty);
                }

                if (dto.MediaTypes?.Contains(Core.Constants.MediaTypes.MusicAlbum) == true)
                {
                    _logger.LogError("Smart playlist '{PlaylistName}' uses 'MusicAlbum' media type. MusicAlbum playlists are not supported due to Jellyfin playlist limitations. Use 'Audio' media type instead, or create a Collection for Album support. Skipping playlist refresh.", dto.Name);
                    return (false, "MusicAlbum media type is not supported for Playlists. Use Audio media type, or create a Collection instead.", string.Empty);
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
                    var oldName = existingPlaylist.Name;
                    _logger.LogInformation("Removing smart playlist '{PlaylistName}' (ID: {PlaylistId}) for user '{UserName}'",
                        oldName, existingPlaylist.Id, user.Username);

                    // Get the current smart playlist name format to see what needs to be removed
                    var currentSmartName = NameFormatter.FormatPlaylistName(dto.Name);

                    // Check if the playlist name matches the current smart format
                    if (oldName == currentSmartName)
                    {
                        // Remove the smart playlist naming and keep just the base name
                        existingPlaylist.Name = dto.Name;

                        // Save the changes
                        await existingPlaylist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

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

                                // Save the changes
                                await existingPlaylist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

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

        private async Task UpdatePlaylistPublicStatusAsync(Playlist playlist, bool isPublic, LinkedChild[] linkedChildren, SmartPlaylistDto dto, CancellationToken cancellationToken)
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
                await RefreshPlaylistMetadataAsync(playlist, cancellationToken).ConfigureAwait(false);
            }

            // Apply custom metadata after metadata refresh to prevent providers from overwriting
            await ApplyCustomMetadataAsync(playlist, dto, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> CreateNewPlaylistAsync(string playlistName, Guid userId, bool isPublic, LinkedChild[] linkedChildren, SmartPlaylistDto dto, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Creating new smart playlist {PlaylistName} with {ItemCount} items and {PublicStatus} status",
                playlistName, linkedChildren.Length, isPublic ? "public" : "private");

            var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = playlistName,
                UserId = userId,
                Public = isPublic,
            }).ConfigureAwait(false);

            _logger.LogDebug("Playlist creation result: ID = {PlaylistId}", result.Id);

            if (_libraryManager.GetItemById(result.Id) is Playlist newPlaylist)
            {
                _logger.LogDebug("Retrieved new playlist: Name = {Name}, Shares count = {SharesCount}, Public = {Public}",
                    newPlaylist.Name, newPlaylist.Shares?.Count ?? 0, (newPlaylist.Shares?.Any() ?? false));

                newPlaylist.LinkedChildren = linkedChildren;

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
                    await RefreshPlaylistMetadataAsync(newPlaylist, cancellationToken).ConfigureAwait(false);
                }

                // Apply custom metadata after metadata refresh to prevent providers from overwriting
                await ApplyCustomMetadataAsync(newPlaylist, dto, cancellationToken).ConfigureAwait(false);

                return newPlaylist.Id.ToString("N");
            }
            else
            {
                _logger.LogWarning("Failed to retrieve newly created playlist with ID {PlaylistId}", result.Id);
                return string.Empty;
            }
        }

        private Task ApplyCustomMetadataAsync(BaseItem item, SmartListDto dto, CancellationToken cancellationToken)
            => MetadataHelper.ApplyCustomMetadataAsync(item, dto, _logger, cancellationToken);

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

                        // Copy the image to the playlist folder
                        File.Copy(sourcePath, destPath, overwrite: true);
                        _logger.LogDebug("Copied custom {ImageType} image to playlist: {DestPath}", imageTypeName, destPath);

                        // Remove existing image of the same type
                        var existingImage = imageInfos.FirstOrDefault(i => i.Type == imageType);
                        if (existingImage != null)
                        {
                            imageInfos.Remove(existingImage);
                        }

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

            if (mediaTypes?.Contains(Core.Constants.MediaTypes.Series) == true)
            {
                var playlistName = dto?.Name ?? "Unknown";
                _logger?.LogError("Smart playlist '{PlaylistName}' uses 'Series' media type. Series playlists are not supported due to Jellyfin playlist limitations. Use 'Episode' media type instead, or create a Collection for Series support.", playlistName);
                throw new InvalidOperationException("Series media type is not supported for Playlists. Use Episode media type, or create a Collection instead.");
            }

            if (mediaTypes?.Contains(Core.Constants.MediaTypes.MusicAlbum) == true)
            {
                var playlistName = dto?.Name ?? "Unknown";
                _logger?.LogError("Smart playlist '{PlaylistName}' uses 'MusicAlbum' media type. MusicAlbum playlists are not supported due to Jellyfin playlist limitations. Use 'Audio' media type instead, or create a Collection for Album support.", playlistName);
                throw new InvalidOperationException("MusicAlbum media type is not supported for Playlists. Use Audio media type, or create a Collection instead.");
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

            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = baseItemKinds,
                Recursive = true,
                IsVirtualItem = false,
                TopParentIds = validTopParentIds,
            };

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
        /// Refreshes playlist metadata to trigger Jellyfin's auto-generation of cover images.
        /// This is only called when no custom Primary image is uploaded via Smart List.
        /// Jellyfin will automatically generate a collage from playlist items.
        /// </summary>
        private async Task RefreshPlaylistMetadataAsync(Playlist playlist, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var directoryService = new Services.Shared.BasicDirectoryService();

                _logger.LogDebug("Triggering metadata refresh for playlist {PlaylistName} to generate cover image", playlist.Name);

                var refreshOptions = new MetadataRefreshOptions(directoryService)
                {
                    MetadataRefreshMode = MetadataRefreshMode.Default,
                    ImageRefreshMode = MetadataRefreshMode.Default,
                    ReplaceAllMetadata = true, // Force regeneration of playlist metadata
                    ReplaceAllImages = true   // Don't replace existing images - preserves user-uploaded images in Jellyfin
                };

                await _providerManager.RefreshSingleItem(playlist, refreshOptions, cancellationToken).ConfigureAwait(false);

                stopwatch.Stop();
                _logger.LogDebug("Cover image generation completed for playlist {PlaylistName} in {ElapsedTime}ms", playlist.Name, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Failed to refresh metadata for playlist {PlaylistName} after {ElapsedTime}ms. Cover image may not be generated.", playlist.Name, stopwatch.ElapsedMilliseconds);
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
        /// Helper method to delete all Jellyfin playlists for a smart playlist across all users.
        /// Handles both multi-user playlists (UserPlaylists array) and legacy single-user playlists.
        /// This centralizes the deletion logic used by disable, delete, and visibility schedule operations.
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
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete Jellyfin playlist {JellyfinPlaylistId} for user {UserId}, continuing",
                                userMapping.JellyfinPlaylistId, userMapping.UserId);
                        }
                    }
                }
            }
            else if (!string.IsNullOrEmpty(playlistDto.JellyfinPlaylistId))
            {
                // Fallback to single playlist deletion (backwards compatibility)
                _logger.LogDebug("Deleting Jellyfin playlist {JellyfinPlaylistId} for playlist '{PlaylistName}'",
                    playlistDto.JellyfinPlaylistId, playlistDto.Name);
                try
                {
                    await DeleteAsync(playlistDto, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete Jellyfin playlist for '{PlaylistName}'", playlistDto.Name);
                }
            }
        }

    }
}
