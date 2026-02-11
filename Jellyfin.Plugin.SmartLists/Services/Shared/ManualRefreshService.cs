using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.ExternalList;
using Jellyfin.Plugin.SmartLists.Services.Playlists;
using Jellyfin.Plugin.SmartLists.Services.Collections;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Result of a refresh operation with separate messages for user notification and logging.
    /// </summary>
    public class RefreshResult
    {
        /// <summary>
        /// Whether the operation was successful.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// User-friendly notification message to display in the UI.
        /// </summary>
        public string NotificationMessage { get; set; } = string.Empty;

        /// <summary>
        /// Detailed log message for debugging and logging purposes.
        /// </summary>
        public string LogMessage { get; set; } = string.Empty;

        /// <summary>
        /// Number of successful refresh operations.
        /// </summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// Number of failed refresh operations.
        /// </summary>
        public int FailureCount { get; set; }
    }

    /// <summary>
    /// Service for handling manual refresh operations initiated by users.
    /// This includes both individual playlist refreshes and "Refresh All" operations from the UI.
    /// </summary>
    public interface IManualRefreshService
    {
        /// <summary>
        /// Refresh all smart playlists manually.
        /// This method processes ALL playlists regardless of their ScheduleTrigger settings.
        /// </summary>
        /// <param name="batchOffset">Offset for batch tracking (used when refreshing all lists together)</param>
        /// <param name="totalBatchCount">Total count for unified batch tracking (used when refreshing all lists together)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Refresh result with separate notification and log messages</returns>
        Task<RefreshResult> RefreshAllPlaylistsAsync(int batchOffset = 0, int? totalBatchCount = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Refresh all smart lists (both playlists and collections) manually.
        /// This method processes ALL lists regardless of their ScheduleTrigger settings.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Refresh result with separate notification and log messages</returns>
        Task<RefreshResult> RefreshAllListsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Refresh a single smart playlist manually.
        /// </summary>
        /// <param name="playlist">The playlist to refresh</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Tuple of (success, message, jellyfinPlaylistId)</returns>
        Task<(bool Success, string Message, string? JellyfinPlaylistId)> RefreshSinglePlaylistAsync(Core.Models.SmartPlaylistDto playlist, CancellationToken cancellationToken = default);

        /// <summary>
        /// Refresh a single smart collection manually.
        /// </summary>
        /// <param name="collection">The collection to refresh</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Tuple of (success, message, jellyfinCollectionId)</returns>
        Task<(bool Success, string Message, string? JellyfinCollectionId)> RefreshSingleCollectionAsync(Core.Models.SmartCollectionDto collection, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Implementation of manual refresh service that handles user-initiated refresh operations.
    /// This consolidates logic for both individual playlist refreshes and "refresh all" operations.
    /// </summary>
    public class ManualRefreshService(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IServerApplicationPaths applicationPaths,
        IPlaylistManager playlistManager,
        ICollectionManager collectionManager,
        IUserDataManager userDataManager,
        IProviderManager providerManager,
        ILogger<ManualRefreshService> logger,
        Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
        RefreshQueueService refreshQueueService,
        ExternalListService externalListService) : IManualRefreshService
    {
        private readonly IUserManager _userManager = userManager;
        private readonly ILibraryManager _libraryManager = libraryManager;
        private readonly IServerApplicationPaths _applicationPaths = applicationPaths;
        private readonly IPlaylistManager _playlistManager = playlistManager;
        private readonly ICollectionManager _collectionManager = collectionManager;
        private readonly IUserDataManager _userDataManager = userDataManager;
        private readonly IProviderManager _providerManager = providerManager;
        private readonly ILogger<ManualRefreshService> _logger = logger;
        private readonly Microsoft.Extensions.Logging.ILoggerFactory _loggerFactory = loggerFactory;
        private readonly RefreshQueueService _refreshQueueService = refreshQueueService;
        private readonly ExternalListService _externalListService = externalListService;

        /// <summary>
        /// Gets the user for a playlist, handling migration from old User field to new UserId field.
        /// </summary>
        /// <param name="playlist">The playlist.</param>
        /// <returns>The user, or null if not found.</returns>
        private async Task<User?> GetPlaylistUserAsync(SmartPlaylistDto playlist)
        {
            var userId = await GetPlaylistUserIdAsync(playlist);
            if (userId == Guid.Empty)
            {
                return null;
            }

            return _userManager.GetUserById(userId);
        }

        /// <summary>
        /// Gets the user ID for a playlist.
        /// </summary>
        /// <param name="playlist">The playlist.</param>
        /// <returns>The user ID, or Guid.Empty if not found.</returns>
        private static Task<Guid> GetPlaylistUserIdAsync(SmartPlaylistDto playlist)
        {
            // DEPRECATED: playlist.UserId is for backwards compatibility with old single-user playlists.
            // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
            if (!string.IsNullOrEmpty(playlist.UserId) && Guid.TryParse(playlist.UserId, out var userId))
            {
                return Task.FromResult(userId);
            }
            return Task.FromResult(Guid.Empty);
        }

        /// <summary>
        /// Formats elapsed time for user notifications. Shows seconds if under 60 seconds (rounded to integer if >= 1 second), minutes if 60+ seconds.
        /// </summary>
        /// <param name="elapsedMilliseconds">Elapsed time in milliseconds.</param>
        /// <returns>Formatted time string (e.g., "0.5 seconds", "2 seconds", or "3 minutes").</returns>
        private static string FormatElapsedTime(long elapsedMilliseconds)
        {
            var elapsedSeconds = elapsedMilliseconds / 1000.0;
            
            if (elapsedSeconds < 60)
            {
                if (elapsedSeconds < 1.0)
                {
                    return $"{elapsedSeconds:F1} seconds";
                }
                else
                {
                    var seconds = (int)Math.Round(elapsedSeconds);
                    return seconds == 1 ? "1 second" : $"{seconds} seconds";
                }
            }
            else
            {
                var minutes = (int)Math.Round(elapsedSeconds / 60.0);
                return minutes == 1 ? "1 minute" : $"{minutes} minutes";
            }
        }

        /// <summary>
        /// Checks if a refresh result indicates actual failures (failure count > 0 or Success = false).
        /// </summary>
        /// <param name="result">The refresh result to check.</param>
        /// <returns>True if there are actual failures (FailureCount > 0 or Success = false), false otherwise.</returns>
        private static bool HasActualFailures(RefreshResult? result)
        {
            return result != null && (!result.Success || result.FailureCount > 0);
        }

        /// <summary>
        /// Creates a PlaylistService instance for playlist operations.
        /// </summary>
        private Services.Playlists.PlaylistService GetPlaylistService()
        {
            // Create a logger specifically for PlaylistService
            var playlistServiceLogger = _loggerFactory.CreateLogger<PlaylistService>();

            return new PlaylistService(
                _userManager,
                _libraryManager,
                _playlistManager,
                _userDataManager,
                playlistServiceLogger,
                _providerManager,
                externalListService: _externalListService);
        }

        /// <summary>
        /// Creates a CollectionService instance for collection operations.
        /// </summary>
        private Services.Collections.CollectionService GetCollectionService()
        {
            // Create a logger specifically for CollectionService
            var collectionServiceLogger = _loggerFactory.CreateLogger<Services.Collections.CollectionService>();

            return new Services.Collections.CollectionService(
                _libraryManager,
                _collectionManager,
                _userManager,
                _userDataManager,
                collectionServiceLogger,
                _providerManager,
                externalListService: _externalListService);
        }

        /// <summary>
        /// Refresh all smart playlists manually without using Jellyfin scheduled tasks.
        /// This method enqueues all playlists for processing through the queue system.
        /// </summary>
        /// <param name="batchOffset">Offset for batch tracking (used when refreshing all lists together)</param>
        /// <param name="totalBatchCount">Total count for unified batch tracking (used when refreshing all lists together)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<RefreshResult> RefreshAllPlaylistsAsync(int batchOffset = 0, int? totalBatchCount = null, CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogInformation("Enqueuing all smart playlists for manual refresh");

                // Create playlist store
                var fileSystem = new SmartListFileSystem(_applicationPaths);
                var plStore = new PlaylistStore(fileSystem);

                var allDtos = await plStore.GetAllAsync().ConfigureAwait(false);

                _logger.LogInformation("Found {TotalCount} total playlists for manual refresh", allDtos.Length);

                if (allDtos.Length == 0)
                {
                    stopwatch.Stop();
                    var message = "No playlists found to refresh";
                    var earlyLogMessage = $"{message} (completed in {stopwatch.ElapsedMilliseconds}ms)";
                    _logger.LogInformation(earlyLogMessage);
                    return new RefreshResult
                    {
                        Success = true,
                        NotificationMessage = message,
                        LogMessage = earlyLogMessage,
                        SuccessCount = 0,
                        FailureCount = 0
                    };
                }

                // Log disabled playlists for informational purposes
                var disabledPlaylists = allDtos.Where(dto => !dto.Enabled).ToList();
                if (disabledPlaylists.Count > 0)
                {
                    var disabledNames = string.Join(", ", disabledPlaylists.Select(p => $"'{p.Name}'"));
                    _logger.LogDebug("Skipping {DisabledCount} disabled playlists: {DisabledNames}", disabledPlaylists.Count, disabledNames);
                }

                // Process all enabled playlists - enqueue each one individually
                var enabledPlaylists = allDtos.Where(dto => dto.Enabled).ToList();
                _logger.LogInformation("Enqueuing {EnabledCount} enabled playlists", enabledPlaylists.Count);

                var enqueuedCount = 0;
                var failedCount = 0;
                foreach (var dto in enabledPlaylists)
                {
                    try
                    {
                        var listId = string.IsNullOrEmpty(dto.Id) ? Guid.NewGuid().ToString() : dto.Id;
                        var queueItem = new RefreshQueueItem
                        {
                            ListId = listId,
                            ListName = dto.Name,
                            ListType = Core.Enums.SmartListType.Playlist,
                            OperationType = RefreshOperationType.Refresh,
                            ListData = dto,
                            UserId = dto.UserId,
                            TriggerType = Core.Enums.RefreshTriggerType.Manual
                        };

                        _refreshQueueService.EnqueueOperation(queueItem);
                        enqueuedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error enqueuing playlist {PlaylistName} ({PlaylistId}) for refresh", dto.Name, dto.Id);
                        failedCount++;
                    }
                }

                stopwatch.Stop();
                var elapsedTime = FormatElapsedTime(stopwatch.ElapsedMilliseconds);
                var logMessage = failedCount > 0 
                    ? $"Enqueued {enqueuedCount} playlists for refresh ({failedCount} failed to enqueue) (completed in {stopwatch.ElapsedMilliseconds}ms)"
                    : $"Enqueued {enqueuedCount} playlists for refresh (completed in {stopwatch.ElapsedMilliseconds}ms)";
                var notificationMessage = failedCount > 0
                    ? $"Enqueued {enqueuedCount} playlists for refresh ({failedCount} failed). They will be processed in the background."
                    : $"Enqueued {enqueuedCount} playlists for refresh. They will be processed in the background.";
                
                _logger.LogInformation(logMessage);

                return new RefreshResult
                {
                    Success = failedCount == 0,
                    NotificationMessage = notificationMessage,
                    LogMessage = logMessage,
                    SuccessCount = enqueuedCount,
                    FailureCount = failedCount
                };
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                var message = "Refresh operation was cancelled";
                var logMessage = $"Manual playlist refresh was cancelled (after {stopwatch.ElapsedMilliseconds}ms)";
                _logger.LogInformation(logMessage);
                return new RefreshResult
                {
                    Success = false,
                    NotificationMessage = message,
                    LogMessage = logMessage,
                    SuccessCount = 0,
                    FailureCount = 0
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var notificationMessage = "An error occurred while enqueuing playlists. Please check the logs for details.";
                var logMessage = $"Error during manual playlist refresh (after {stopwatch.ElapsedMilliseconds}ms): {ex.Message}";
                _logger.LogError(ex, logMessage);
                return new RefreshResult
                {
                    Success = false,
                    NotificationMessage = notificationMessage,
                    LogMessage = logMessage,
                    SuccessCount = 0,
                    FailureCount = 0
                };
            }
        }

        /// <summary>
        /// Refresh all smart lists (both playlists and collections) manually.
        /// This method processes ALL lists regardless of their ScheduleTrigger settings, since this is a manual operation.
        /// This method uses immediate failure if another refresh is already in progress.
        /// </summary>
        public async Task<RefreshResult> RefreshAllListsAsync(CancellationToken cancellationToken = default)
        {
            var overallStopwatch = Stopwatch.StartNew();
            RefreshResult? playlistResult = null;
            RefreshResult? collectionResult = null;

            try
            {
                _logger.LogInformation("Starting manual refresh of all smart lists (playlists and collections)");

                // Calculate total count of all lists upfront for unified batch tracking
                var fileSystem = new SmartListFileSystem(_applicationPaths);
                var plStore = new PlaylistStore(fileSystem);
                var collectionStore = new Services.Collections.CollectionStore(fileSystem);

                var allPlaylists = await plStore.GetAllAsync().ConfigureAwait(false);
                var enabledPlaylists = allPlaylists.Where(dto => dto.Enabled).ToList();
                
                var allCollections = await collectionStore.GetAllAsync().ConfigureAwait(false);
                var enabledCollections = allCollections.Where(dto => dto.Enabled).ToList();

                var totalListsCount = enabledPlaylists.Count + enabledCollections.Count;
                _logger.LogInformation("Found {PlaylistCount} enabled playlists and {CollectionCount} enabled collections (total: {TotalCount} lists)",
                    enabledPlaylists.Count, enabledCollections.Count, totalListsCount);

                // Refresh playlists first with unified batch tracking
                _logger.LogInformation("Refreshing all playlists...");
                playlistResult = await RefreshAllPlaylistsAsync(batchOffset: 0, totalBatchCount: totalListsCount, cancellationToken).ConfigureAwait(false);

                // Refresh collections with unified batch tracking (offset by playlist count)
                // Always attempt both, even if playlists had failures
                _logger.LogInformation("Refreshing all collections...");
                collectionResult = await RefreshAllCollectionsAsync(batchOffset: enabledPlaylists.Count, totalBatchCount: totalListsCount, cancellationToken).ConfigureAwait(false);

                overallStopwatch.Stop();
                var elapsedTime = FormatElapsedTime(overallStopwatch.ElapsedMilliseconds);
                
                // Check if there were any actual failures (based on failure count, not Success flag)
                var playlistHasFailures = HasActualFailures(playlistResult);
                var collectionHasFailures = HasActualFailures(collectionResult);
                
                string notificationMessage;
                string logMessage;
                
                if (!playlistHasFailures && !collectionHasFailures)
                {
                    // Simple success message when everything succeeds
                    notificationMessage = $"Enqueued all lists for refresh. They will be processed in the background.";
                }
                else
                {
                    // Include details when there are failures
                    notificationMessage = $"List enqueue completed. Playlists: {playlistResult.NotificationMessage}. Collections: {collectionResult.NotificationMessage}.";
                }
                
                logMessage = $"All lists enqueue completed. Playlists: {playlistResult.LogMessage}. Collections: {collectionResult.LogMessage}. (Total time: {overallStopwatch.ElapsedMilliseconds}ms)";
                _logger.LogInformation(logMessage);

                return new RefreshResult
                {
                    Success = !playlistHasFailures && !collectionHasFailures,
                    NotificationMessage = notificationMessage,
                    LogMessage = logMessage,
                    SuccessCount = playlistResult.SuccessCount + collectionResult.SuccessCount,
                    FailureCount = playlistResult.FailureCount + collectionResult.FailureCount
                };
            }
            catch (OperationCanceledException)
            {
                overallStopwatch.Stop();
                var message = "Refresh operation was cancelled";
                var logMessage = $"Manual refresh of all lists was cancelled (after {overallStopwatch.ElapsedMilliseconds}ms)";
                _logger.LogInformation(logMessage);
                return new RefreshResult
                {
                    Success = false,
                    NotificationMessage = message,
                    LogMessage = logMessage,
                    SuccessCount = 0,
                    FailureCount = 0
                };
            }
            catch (Exception ex)
            {
                overallStopwatch.Stop();
                var notificationMessage = "An error occurred during list refresh. Please check the logs for details.";
                var logMessage = $"Error during manual refresh of all lists (after {overallStopwatch.ElapsedMilliseconds}ms): {ex.Message}";
                _logger.LogError(ex, logMessage);
                return new RefreshResult
                {
                    Success = false,
                    NotificationMessage = notificationMessage,
                    LogMessage = logMessage,
                    SuccessCount = 0,
                    FailureCount = 0
                };
            }
        }

        /// <summary>
        /// Refresh all smart collections manually without using Jellyfin scheduled tasks.
        /// This method enqueues all collections for processing through the queue system.
        /// </summary>
        /// <param name="batchOffset">Offset for batch tracking (used when refreshing all lists together)</param>
        /// <param name="totalBatchCount">Total count for unified batch tracking (used when refreshing all lists together)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        private async Task<RefreshResult> RefreshAllCollectionsAsync(int batchOffset = 0, int? totalBatchCount = null, CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogInformation("Enqueuing all smart collections for manual refresh");

                // Create collection store
                var fileSystem = new SmartListFileSystem(_applicationPaths);
                var collectionStore = new Services.Collections.CollectionStore(fileSystem);

                var allDtos = await collectionStore.GetAllAsync().ConfigureAwait(false);

                _logger.LogInformation("Found {TotalCount} total collections for manual refresh", allDtos.Length);

                if (allDtos.Length == 0)
                {
                    stopwatch.Stop();
                    var message = "No collections found to refresh";
                    var earlyLogMessage = $"{message} (completed in {stopwatch.ElapsedMilliseconds}ms)";
                    _logger.LogInformation(earlyLogMessage);
                    return new RefreshResult
                    {
                        Success = true,
                        NotificationMessage = message,
                        LogMessage = earlyLogMessage,
                        SuccessCount = 0,
                        FailureCount = 0
                    };
                }

                // Log disabled collections for informational purposes
                var disabledCollections = allDtos.Where(dto => !dto.Enabled).ToList();
                if (disabledCollections.Count > 0)
                {
                    var disabledNames = string.Join(", ", disabledCollections.Select(c => $"'{c.Name}'"));
                    _logger.LogDebug("Skipping {DisabledCount} disabled collections: {DisabledNames}", disabledCollections.Count, disabledNames);
                }

                // Process all enabled collections - enqueue each one individually
                var enabledCollections = allDtos.Where(dto => dto.Enabled).ToList();
                _logger.LogInformation("Enqueuing {EnabledCount} enabled collections", enabledCollections.Count);

                var enqueuedCount = 0;
                var failedCount = 0;
                foreach (var dto in enabledCollections)
                {
                    try
                    {
                        var listId = string.IsNullOrEmpty(dto.Id) ? Guid.NewGuid().ToString() : dto.Id;
                        var queueItem = new RefreshQueueItem
                        {
                            ListId = listId,
                            ListName = dto.Name,
                            ListType = Core.Enums.SmartListType.Collection,
                            OperationType = RefreshOperationType.Refresh,
                            ListData = dto,
                            UserId = dto.UserId,
                            TriggerType = Core.Enums.RefreshTriggerType.Manual
                        };

                        _refreshQueueService.EnqueueOperation(queueItem);
                        enqueuedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error enqueuing collection {CollectionName} ({CollectionId}) for refresh", dto.Name, dto.Id);
                        failedCount++;
                    }
                }

                stopwatch.Stop();
                var elapsedTime = FormatElapsedTime(stopwatch.ElapsedMilliseconds);
                var logMessage = failedCount > 0
                    ? $"Enqueued {enqueuedCount} collections for refresh ({failedCount} failed to enqueue) (completed in {stopwatch.ElapsedMilliseconds}ms)"
                    : $"Enqueued {enqueuedCount} collections for refresh (completed in {stopwatch.ElapsedMilliseconds}ms)";
                var notificationMessage = failedCount > 0
                    ? $"Enqueued {enqueuedCount} collections for refresh ({failedCount} failed). They will be processed in the background."
                    : $"Enqueued {enqueuedCount} collections for refresh. They will be processed in the background.";
                
                _logger.LogInformation(logMessage);

                return new RefreshResult
                {
                    Success = failedCount == 0,
                    NotificationMessage = notificationMessage,
                    LogMessage = logMessage,
                    SuccessCount = enqueuedCount,
                    FailureCount = failedCount
                };
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                var message = "Refresh operation was cancelled";
                var logMessage = $"Manual collection refresh was cancelled (after {stopwatch.ElapsedMilliseconds}ms)";
                _logger.LogInformation(logMessage);
                return new RefreshResult
                {
                    Success = false,
                    NotificationMessage = message,
                    LogMessage = logMessage,
                    SuccessCount = 0,
                    FailureCount = 0
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                var notificationMessage = "An error occurred while enqueuing collections. Please check the logs for details.";
                var logMessage = $"Error during manual collection refresh (after {stopwatch.ElapsedMilliseconds}ms): {ex.Message}";
                _logger.LogError(ex, logMessage);
                return new RefreshResult
                {
                    Success = false,
                    NotificationMessage = notificationMessage,
                    LogMessage = logMessage,
                    SuccessCount = 0,
                    FailureCount = 0
                };
            }
        }

        /// <summary>
        /// Refresh a single smart playlist manually.
        /// This method enqueues the playlist for processing through the queue system.
        /// </summary>
        public Task<(bool Success, string Message, string? JellyfinPlaylistId)> RefreshSinglePlaylistAsync(SmartPlaylistDto playlist, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(playlist);

            try
            {
                var listId = string.IsNullOrEmpty(playlist.Id) ? Guid.NewGuid().ToString() : playlist.Id;
                _logger.LogInformation("Enqueuing single playlist for manual refresh: {PlaylistName} ({ListId})", playlist.Name, listId);

                var queueItem = new RefreshQueueItem
                {
                    ListId = listId,
                    ListName = playlist.Name,
                    ListType = Core.Enums.SmartListType.Playlist,
                    OperationType = RefreshOperationType.Refresh,
                    ListData = playlist,
                    UserId = playlist.UserId,
                    TriggerType = Core.Enums.RefreshTriggerType.Manual
                };

                _refreshQueueService.EnqueueOperation(queueItem);

                _logger.LogInformation("Enqueued single playlist: {PlaylistName} ({ListId})", playlist.Name, listId);
                return Task.FromResult<(bool Success, string Message, string? JellyfinPlaylistId)>((true, "Playlist refresh has been queued. It will be processed in the background.", playlist.JellyfinPlaylistId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enqueuing single playlist refresh for playlist: {PlaylistName} ({PlaylistId})", playlist.Name, playlist.Id);
                return Task.FromResult<(bool Success, string Message, string? JellyfinPlaylistId)>((false, $"Error enqueuing playlist refresh: {ex.Message}", string.Empty));
            }
        }

        /// <summary>
        /// Refresh a single smart collection manually.
        /// This method enqueues the collection for processing through the queue system.
        /// </summary>
        public Task<(bool Success, string Message, string? JellyfinCollectionId)> RefreshSingleCollectionAsync(SmartCollectionDto collection, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(collection);

            try
            {
                var listId = string.IsNullOrEmpty(collection.Id) ? Guid.NewGuid().ToString() : collection.Id;
                _logger.LogInformation("Enqueuing single collection for manual refresh: {CollectionName} ({ListId})", collection.Name, listId);

                var queueItem = new RefreshQueueItem
                {
                    ListId = listId,
                    ListName = collection.Name,
                    ListType = Core.Enums.SmartListType.Collection,
                    OperationType = RefreshOperationType.Refresh,
                    ListData = collection,
                    UserId = collection.UserId,
                    TriggerType = Core.Enums.RefreshTriggerType.Manual
                };

                _refreshQueueService.EnqueueOperation(queueItem);

                _logger.LogInformation("Enqueued single collection: {CollectionName} ({ListId})", collection.Name, listId);
                return Task.FromResult<(bool Success, string Message, string? JellyfinCollectionId)>((true, "Collection refresh has been queued. It will be processed in the background.", collection.JellyfinCollectionId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enqueuing single collection refresh for collection: {CollectionName} ({CollectionId})", collection.Name, collection.Id);
                return Task.FromResult<(bool Success, string Message, string? JellyfinCollectionId)>((false, $"Error enqueuing collection refresh: {ex.Message}", string.Empty));
            }
        }
    }
}
