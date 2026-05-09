using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.Abstractions;
using Jellyfin.Plugin.SmartLists.Services.Playlists;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists
{
    /// <summary>
    /// Reusable caching helper for efficient batch playlist refreshes.
    /// Implements the same advanced caching strategy used by legacy scheduled tasks.
    /// </summary>
    public class RefreshCache
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly ISmartListService<SmartPlaylistDto> _playlistService;
        private readonly ISmartListStore<SmartPlaylistDto> _playlistStore;
        private readonly ILogger _logger;

        // No longer using instance-level cache - converted to per-invocation for thread safety

        public RefreshCache(
            ILibraryManager libraryManager,
            IUserManager userManager,
            ISmartListService<SmartPlaylistDto> playlistService,
            ISmartListStore<SmartPlaylistDto> playlistStore,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _playlistService = playlistService;
            _playlistStore = playlistStore;
            _logger = logger;
        }

        /// <summary>
        /// Refreshes multiple playlists efficiently using shared caching.
        /// </summary>
        /// <param name="playlists">Playlists to refresh</param>
        /// <param name="updateLastRefreshTime">Whether to update LastRefreshed timestamp</param>
        /// <param name="batchProgressCallback">Optional callback invoked before processing each playlist (playlist ID)</param>
        /// <param name="createProgressCallback">Optional function to create a progress callback for each playlist (playlist ID -> progress callback)</param>
        /// <param name="onPlaylistComplete">Optional callback invoked immediately after each playlist completes (playlist ID, success, duration, message)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Results for each playlist</returns>
        public async Task<List<PlaylistRefreshResult>> RefreshPlaylistsWithCacheAsync(
            List<SmartPlaylistDto> playlists,
            bool updateLastRefreshTime = false,
            Action<string>? batchProgressCallback = null,
            Func<string, Action<int, int>?>? createProgressCallback = null,
            Action<string, bool, TimeSpan, string>? onPlaylistComplete = null,
            CancellationToken cancellationToken = default)
        {
            var results = new List<PlaylistRefreshResult>();

            if (playlists == null)
            {
                _logger.LogWarning("Playlists parameter is null, returning empty results");
                return results;
            }

            if (!playlists.Any())
            {
                return results;
            }

            _logger.LogDebug("Starting cached refresh of {PlaylistCount} playlists", playlists.Count);
            var totalStopwatch = Stopwatch.StartNew();

            try
            {
                foreach (var playlist in playlists.Where(p => p.AllUsers))
                {
                    PlaylistUserResolver.ExpandAllUsers(playlist, _userManager);
                }

                // Handle playlists with missing/invalid User first
                // Helper predicate to check if a playlist has a valid user ID (either top-level UserId or UserPlaylists array)
                static bool IsValidUserId(SmartPlaylistDto p)
                {
                    // Check for single-user playlist (backwards compatibility)
                    if (!string.IsNullOrEmpty(p.UserId) && 
                        Guid.TryParse(p.UserId, out var userId) && 
                        userId != Guid.Empty)
                    {
                        return true;
                    }
                    
                    // Check for multi-user playlist (UserPlaylists array)
                    if (p.UserPlaylists != null && p.UserPlaylists.Count > 0)
                    {
                        // Validate that at least one user in UserPlaylists is valid
                        return p.UserPlaylists.Any(up => 
                            !string.IsNullOrEmpty(up.UserId) && 
                            Guid.TryParse(up.UserId, out var upUserId) && 
                            upUserId != Guid.Empty);
                    }
                    
                    return false;
                }

                var playlistsWithInvalidUser = playlists
                    .Where(p => !IsValidUserId(p))
                    .ToList();
                if (playlistsWithInvalidUser.Any())
                {
                    _logger.LogWarning("Found {InvalidCount} playlists with missing or invalid User, adding failure results", playlistsWithInvalidUser.Count);

                    // Add failure results for playlists with invalid User
                    foreach (var playlist in playlistsWithInvalidUser)
                    {
                        results.Add(new PlaylistRefreshResult
                        {
                            PlaylistId = playlist.Id ?? string.Empty,
                            PlaylistName = playlist.Name,
                            Success = false,
                            Message = "Missing or invalid User",
                            JellyfinPlaylistId = string.Empty,
                        });
                    }
                }

                // Group playlists by user, handling both single-user and multi-user playlists
                // For multi-user playlists, create an entry for each user in UserPlaylists
                var playlistsByUser = new Dictionary<Guid, List<SmartPlaylistDto>>();
                
                foreach (var playlist in playlists.Where(IsValidUserId))
                {
                    // Handle multi-user playlists (UserPlaylists array)
                    if (playlist.UserPlaylists != null && playlist.UserPlaylists.Count > 0)
                    {
                        foreach (var userMapping in playlist.UserPlaylists)
                        {
                            if (!string.IsNullOrEmpty(userMapping.UserId) && 
                                Guid.TryParse(userMapping.UserId, out var userId) && 
                                userId != Guid.Empty)
                            {
                                if (!playlistsByUser.TryGetValue(userId, out var userPlaylistList))
                                {
                                    userPlaylistList = new List<SmartPlaylistDto>();
                                    playlistsByUser[userId] = userPlaylistList;
                                }
                                userPlaylistList.Add(playlist);
                            }
                        }
                    }
                    // Handle single-user playlists (backwards compatibility)
                    else if (!string.IsNullOrEmpty(playlist.UserId) && 
                             Guid.TryParse(playlist.UserId, out var singleUserId) && 
                             singleUserId != Guid.Empty)
                    {
                        if (!playlistsByUser.TryGetValue(singleUserId, out var singleUserPlaylistList))
                        {
                            singleUserPlaylistList = new List<SmartPlaylistDto>();
                            playlistsByUser[singleUserId] = singleUserPlaylistList;
                        }
                        singleUserPlaylistList.Add(playlist);
                    }
                }

                if (!playlistsByUser.Any())
                {
                    _logger.LogWarning("No playlists with valid UserId found for cached refresh");
                    return results; // Will contain failure results for invalid UserIds if any,
                }

                // Process playlists using per-media-type caching
                foreach (var kvp in playlistsByUser)
                {
                    var userId = kvp.Key;
                    var userPlaylists = kvp.Value;

                    var user = _userManager.GetUserById(userId);
                    if (user == null)
                    {
                        _logger.LogError("User {UserId} not found in UserManager, adding failure results for {PlaylistCount} playlists", userId, userPlaylists.Count);

                        // Add failure results for all playlists of this user
                        foreach (var playlist in userPlaylists)
                        {
                            results.Add(new PlaylistRefreshResult
                            {
                                PlaylistId = playlist.Id ?? string.Empty,
                                PlaylistName = playlist.Name,
                                Success = false,
                                Message = $"User {userId} not found in system",
                                JellyfinPlaylistId = string.Empty,
                            });
                        }
                        continue;
                    }

                    _logger.LogDebug("Processing {PlaylistCount} playlists for user {Username}",
                        userPlaylists.Count, user.Username);

                    // Use the same advanced caching as legacy tasks (per-media-type caching)
                    var userResults = await ProcessUserPlaylistsWithAdvancedCachingAsync(
                        user, userPlaylists, updateLastRefreshTime, batchProgressCallback, createProgressCallback, onPlaylistComplete, cancellationToken);

                    results.AddRange(userResults);
                }

                totalStopwatch.Stop();

                // Log summary
                var successCount = results.Count(r => r.Success);

                _logger.LogInformation("Cached refresh completed in {ElapsedTime}ms: {SuccessCount}/{TotalCount} playlists processed",
                    totalStopwatch.ElapsedMilliseconds, successCount, playlists.Count);

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh playlists with caching");

                // Return failure results for all playlists
                foreach (var playlist in playlists)
                {
                    results.Add(new PlaylistRefreshResult
                    {
                        PlaylistId = playlist.Id ?? string.Empty,
                        PlaylistName = playlist.Name,
                        Success = false,
                        Message = $"Cache refresh failed: {ex.Message}",
                    });
                }

                return results;
            }
            finally
            {
                // No longer need to clear cache - using per-invocation dictionaries for thread safety
            }
        }

        private async Task<List<PlaylistRefreshResult>> ProcessUserPlaylistsWithAdvancedCachingAsync(
            User user,
            List<SmartPlaylistDto> userPlaylists,
            bool updateLastRefreshTime,
            Action<string>? batchProgressCallback,
            Func<string, Action<int, int>?>? createProgressCallback,
            Action<string, bool, TimeSpan, string>? onPlaylistComplete,
            CancellationToken cancellationToken)
        {
            var results = new List<PlaylistRefreshResult>();

            // OPTIMIZATION: Cache media by MediaTypes to avoid redundant queries for playlists with same media types
            // Use Lazy<T> to ensure value factory executes only once per key, even under concurrent access
            var userMediaTypeCache = new ConcurrentDictionary<MediaTypesKey, Lazy<BaseItem[]>>();

            foreach (var playlist in userPlaylists)
            {
                var playlistStopwatch = Stopwatch.StartNew();

                try
                {
                    // Invoke batch progress callback before processing this playlist
                    batchProgressCallback?.Invoke(playlist.Id ?? string.Empty);

                    _logger.LogDebug("Processing playlist {PlaylistName} with {RuleSetCount} rule sets",
                        playlist.Name, playlist.ExpressionSets?.Count ?? 0);

                    // OPTIMIZATION: Get media specifically for this playlist's media types using cache
                    var mediaTypesForClosure = playlist.MediaTypes?.ToList() ?? new List<string>();
                    var mediaTypesKey = MediaTypesKey.Create(mediaTypesForClosure, playlist);

                    var playlistSpecificMedia = userMediaTypeCache.GetOrAdd(mediaTypesKey, _ =>
                        new Lazy<BaseItem[]>(() =>
                        {
                            // Use interface method instead of casting
                            var media = _playlistService.GetAllUserMediaForPlaylist(user, mediaTypesForClosure, playlist).ToArray();
                            _logger.LogDebug("Cached {MediaCount} items for MediaTypes [{MediaTypes}] for user '{Username}'",
                                media.Length, mediaTypesKey, user.Username);
                            return media;
                        }, LazyThreadSafetyMode.ExecutionAndPublication)
                    ).Value;

                    _logger.LogDebug("Playlist {PlaylistName} with MediaTypes [{MediaTypes}] has {PlaylistSpecificCount} specific items",
                        playlist.Name, mediaTypesKey, playlistSpecificMedia.Length);

                    // Create progress callback for this playlist if provided
                    var progressCallback = createProgressCallback?.Invoke(playlist.Id ?? string.Empty);

                    // Create a temporary RefreshCache for this refresh (fallback path when queue service unavailable)
                    var refreshCache = new Services.Shared.RefreshQueueService.RefreshCache();

                    // Use interface method instead of casting
                    var (success, message, jellyfinPlaylistId) = await _playlistService.ProcessPlaylistRefreshWithCachedMediaAsync(
                        playlist,
                        user,
                        playlistSpecificMedia,
                        refreshCache,
                        async (updatedDto) => await _playlistStore.SaveAsync(updatedDto),
                        progressCallback,
                        cancellationToken);

                    if (success && updateLastRefreshTime)
                    {
                        playlist.LastRefreshed = DateTime.UtcNow; // Use UTC for consistent timestamps across timezones
                        await _playlistStore.SaveAsync(playlist);
                    }

                    playlistStopwatch.Stop();
                    
                    var duration = playlistStopwatch.Elapsed;

                    // Notify completion callback immediately so operation can be marked complete
                    onPlaylistComplete?.Invoke(playlist.Id ?? string.Empty, success, duration, message);

                    results.Add(new PlaylistRefreshResult
                    {
                        PlaylistId = playlist.Id ?? string.Empty,
                        PlaylistName = playlist.Name,
                        Success = success,
                        Message = message,
                        ElapsedMilliseconds = playlistStopwatch.ElapsedMilliseconds,
                        JellyfinPlaylistId = jellyfinPlaylistId,
                    });

                    if (success)
                    {
                        _logger.LogDebug("Playlist {PlaylistName} processed successfully in {ElapsedTime}ms: {Message}",
                            playlist.Name, playlistStopwatch.ElapsedMilliseconds, message);
                    }
                    else
                    {
                        _logger.LogWarning("Playlist {PlaylistName} processing failed after {ElapsedTime}ms: {Message}",
                            playlist.Name, playlistStopwatch.ElapsedMilliseconds, message);
                    }
                }
                catch (Exception ex)
                {
                    playlistStopwatch.Stop();
                    _logger.LogError(ex, "Failed to process playlist {PlaylistName} after {ElapsedTime}ms",
                        playlist.Name, playlistStopwatch.ElapsedMilliseconds);

                    var errorDuration = playlistStopwatch.Elapsed;
                    
                    // Notify completion callback for error case too
                    onPlaylistComplete?.Invoke(playlist.Id ?? string.Empty, false, errorDuration, $"Exception: {ex.Message}");

                    results.Add(new PlaylistRefreshResult
                    {
                        PlaylistId = playlist.Id ?? string.Empty,
                        PlaylistName = playlist.Name,
                        Success = false,
                        Message = $"Exception: {ex.Message}",
                        ElapsedMilliseconds = playlistStopwatch.ElapsedMilliseconds,
                    });
                }
            }

            return results;
        }
    }

    /// <summary>
    /// Result of a single playlist refresh operation
    /// </summary>
    public class PlaylistRefreshResult
    {
        public string PlaylistId { get; set; } = string.Empty;
        public string PlaylistName { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public long ElapsedMilliseconds { get; set; }
        public string JellyfinPlaylistId { get; set; } = string.Empty;
    }

}
