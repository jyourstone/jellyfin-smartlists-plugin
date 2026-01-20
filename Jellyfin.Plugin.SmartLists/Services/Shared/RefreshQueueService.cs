using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core.Enums;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.Collections;
using Jellyfin.Plugin.SmartLists.Services.Playlists;
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
    /// Type of operation in the refresh queue
    /// </summary>
    public enum RefreshOperationType
    {
        Refresh,
        Create,
        Edit
    }

    /// <summary>
    /// Represents an item in the refresh queue
    /// </summary>
    public class RefreshQueueItem
    {
        public string ListId { get; set; } = string.Empty;
        public string ListName { get; set; } = string.Empty;
        public SmartListType ListType { get; set; }
        public RefreshOperationType OperationType { get; set; }
        public SmartListDto? ListData { get; set; }
        public string? UserId { get; set; }
        public RefreshTriggerType TriggerType { get; set; }
        public DateTime QueuedAt { get; set; }
    }

    /// <summary>
    /// Service that manages a global queue for refresh operations.
    /// Processes operations sequentially in FIFO order with deduplication.
    /// </summary>
    public class RefreshQueueService : IDisposable
    {
        private readonly ILogger<RefreshQueueService> _logger;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IPlaylistManager _playlistManager;
        private readonly ICollectionManager _collectionManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IProviderManager _providerManager;
        private readonly IServerApplicationPaths _applicationPaths;
        private readonly RefreshStatusService _refreshStatusService;
        private readonly Microsoft.Extensions.Logging.ILoggerFactory _loggerFactory;
        private readonly SmartListImageService? _imageService;

        // Queue data structures
        private readonly ConcurrentQueue<RefreshQueueItem> _queue = new();
        private readonly ConcurrentDictionary<string, RefreshQueueItem> _queuedItems = new(); // For deduplication by ListId
        private readonly SemaphoreSlim _processingLock = new(1, 1); // Single-threaded processing

        // Cache management - per-user caches to avoid rebuilding when switching between users
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<MediaTypesKey, Lazy<BaseItem[]>>> _userCaches = new();
        
        // Per-user RefreshCache for expensive operations (People, Collections, Series metadata, UserData, MediaStreams)
        private readonly ConcurrentDictionary<Guid, RefreshCache> _refreshCaches = new();

        // Background processing
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private Task? _processingTask;
        private volatile bool _disposed = false;
        private volatile RefreshQueueItem? _currentlyProcessing;

        public RefreshQueueService(
            ILogger<RefreshQueueService> logger,
            IUserManager userManager,
            ILibraryManager libraryManager,
            IPlaylistManager playlistManager,
            ICollectionManager collectionManager,
            IUserDataManager userDataManager,
            IProviderManager providerManager,
            IServerApplicationPaths applicationPaths,
            RefreshStatusService refreshStatusService,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            SmartListImageService? imageService = null)
        {
            _logger = logger;
            _userManager = userManager;
            _libraryManager = libraryManager;
            _playlistManager = playlistManager;
            _collectionManager = collectionManager;
            _userDataManager = userDataManager;
            _providerManager = providerManager;
            _applicationPaths = applicationPaths;
            _refreshStatusService = refreshStatusService;
            _loggerFactory = loggerFactory;
            _imageService = imageService;

            // Start background processing task
            _processingTask = Task.Run(ProcessQueueAsync, _cancellationTokenSource.Token);
            _logger.LogInformation("RefreshQueueService initialized and started");
        }

        /// <summary>
        /// Enqueues an operation. If the list is already queued, skips adding it again (deduplication).
        /// </summary>
        public void EnqueueOperation(RefreshQueueItem item)
        {
            if (_disposed)
            {
                _logger.LogWarning("Attempted to enqueue operation after disposal");
                return;
            }

            // Deduplication: Use TryAdd as atomic gate to prevent race conditions
            if (!_queuedItems.TryAdd(item.ListId, item))
            {
                _logger.LogDebug("List {ListId} ({ListName}) is already queued, skipping duplicate", item.ListId, item.ListName);
                return;
            }

            item.QueuedAt = DateTime.UtcNow;
            _queue.Enqueue(item);

            _logger.LogDebug("Enqueued {OperationType} operation for list {ListId} ({ListName}) of type {ListType}",
                item.OperationType, item.ListId, item.ListName, item.ListType);
        }

        /// <summary>
        /// Gets the count of items waiting in the queue (excludes currently processing item).
        /// </summary>
        public int GetQueueCount()
        {
            return _queue.Count;
        }

        /// <summary>
        /// Gets the item currently being processed, if any.
        /// </summary>
        public RefreshQueueItem? GetCurrentlyProcessing()
        {
            return _currentlyProcessing;
        }

        /// <summary>
        /// Background task that continuously processes the queue
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            _logger.LogInformation("Queue processor started");

            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // Wait for an item to be available
                    while (_queue.IsEmpty && !_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        await Task.Delay(100, _cancellationTokenSource.Token);
                    }

                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                        break;

                    // Process items one at a time
                    if (_queue.TryDequeue(out var item))
                    {
                        // Remove from deduplication dictionary
                        _queuedItems.TryRemove(item.ListId, out _);

                        // Acquire processing lock (single-threaded)
                        await _processingLock.WaitAsync(_cancellationTokenSource.Token);

                        try
                        {
                            _currentlyProcessing = item;
                            await ProcessQueueItemAsync(item, _cancellationTokenSource.Token);
                        }
                        finally
                        {
                            _currentlyProcessing = null;
                            _processingLock.Release();
                        }

                        // Clear all user caches when queue is empty to free memory
                        if (_queue.IsEmpty)
                        {
                            ClearCache();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when shutting down
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in queue processor");
                    // Continue processing other items
                }
            }

            _logger.LogInformation("Queue processor stopped");
        }

        /// <summary>
        /// Processes a single queue item
        /// </summary>
        private async Task ProcessQueueItemAsync(RefreshQueueItem item, CancellationToken cancellationToken)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var listId = item.ListId;
            var operationStarted = false;

            try
            {
                _logger.LogInformation("Processing {OperationType} operation for list {ListId} ({ListName})",
                    item.OperationType, item.ListId, item.ListName);

                // Start status tracking
                operationStarted = true;
                _refreshStatusService.StartOperation(
                    listId,
                    item.ListName,
                    item.ListType,
                    item.TriggerType,
                    0);

                // Process based on operation type
                if (item.OperationType == RefreshOperationType.Refresh)
                {
                    await ProcessRefreshAsync(item, cancellationToken);
                }
                else if (item.OperationType == RefreshOperationType.Create)
                {
                    await ProcessCreateAsync(item, cancellationToken);
                }
                else if (item.OperationType == RefreshOperationType.Edit)
                {
                    await ProcessEditAsync(item, cancellationToken);
                }

                stopwatch.Stop();
                var elapsedTime = stopwatch.Elapsed;
                _refreshStatusService.CompleteOperation(listId, true, elapsedTime, null);

                _logger.LogInformation("Completed {OperationType} operation for list {ListId} ({ListName}) in {ElapsedMs}ms",
                    item.OperationType, item.ListId, item.ListName, elapsedTime.TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                if (operationStarted)
                {
                    _refreshStatusService.CompleteOperation(listId, false, stopwatch.Elapsed, "Operation was cancelled");
                }
                _logger.LogInformation("Operation cancelled for list {ListId} ({ListName})", item.ListId, item.ListName);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                if (operationStarted)
                {
                    _refreshStatusService.CompleteOperation(listId, false, stopwatch.Elapsed, ex.Message);
                }
                _logger.LogError(ex, "Error processing {OperationType} operation for list {ListId} ({ListName})",
                    item.OperationType, item.ListId, item.ListName);
            }
        }

        /// <summary>
        /// Processes a refresh operation
        /// </summary>
        private async Task ProcessRefreshAsync(RefreshQueueItem item, CancellationToken cancellationToken)
        {
            if (item.ListData == null)
            {
                throw new InvalidOperationException($"ListData is required for refresh operation on list {item.ListId}");
            }

            // Reload the DTO from the store to get the latest version
            // This is important because the DTO may have been updated (e.g., CustomImages added)
            // after the queue item was created but before processing started
            var fileSystem = new SmartListFileSystem(_applicationPaths);

            if (item.ListType == SmartListType.Playlist)
            {
                var playlistStore = new PlaylistStore(fileSystem);
                if (Guid.TryParse(item.ListId, out var listGuid))
                {
                    var latestDto = await playlistStore.GetByIdAsync(listGuid);
                    if (latestDto != null)
                    {
                        _logger.LogDebug("Reloaded playlist '{PlaylistName}' from store (CustomImages: {HasImages})",
                            latestDto.Name, latestDto.CustomImages?.Count > 0);
                        await ProcessPlaylistRefreshAsync(latestDto, cancellationToken);
                        return;
                    }
                }
                // Fallback to original DTO if reload fails
                await ProcessPlaylistRefreshAsync((SmartPlaylistDto)item.ListData, cancellationToken);
            }
            else if (item.ListType == SmartListType.Collection)
            {
                var collectionStore = new CollectionStore(fileSystem);
                if (Guid.TryParse(item.ListId, out var listGuid))
                {
                    var latestDto = await collectionStore.GetByIdAsync(listGuid);
                    if (latestDto != null)
                    {
                        _logger.LogDebug("Reloaded collection '{CollectionName}' from store (CustomImages: {HasImages})",
                            latestDto.Name, latestDto.CustomImages?.Count > 0);
                        await ProcessCollectionRefreshAsync(latestDto, cancellationToken);
                        return;
                    }
                }
                // Fallback to original DTO if reload fails
                await ProcessCollectionRefreshAsync((SmartCollectionDto)item.ListData, cancellationToken);
            }
            else
            {
                _logger.LogWarning("Unknown list type {ListType} for list '{ListName}'", item.ListType, item.ListName);
            }
        }

        /// <summary>
        /// Processes a create operation
        /// </summary>
        private async Task ProcessCreateAsync(RefreshQueueItem item, CancellationToken cancellationToken)
        {
            if (item.ListData == null)
            {
                throw new InvalidOperationException($"ListData is required for create operation on list {item.ListId}");
            }

            // For create operations, we refresh the newly created list
            // The list should already be saved by the controller
            await ProcessRefreshAsync(item, cancellationToken);
        }

        /// <summary>
        /// Processes an edit operation
        /// </summary>
        private async Task ProcessEditAsync(RefreshQueueItem item, CancellationToken cancellationToken)
        {
            if (item.ListData == null)
            {
                throw new InvalidOperationException($"ListData is required for edit operation on list {item.ListId}");
            }

            // For edit operations, we refresh the updated list
            await ProcessRefreshAsync(item, cancellationToken);
        }

        /// <summary>
        /// Processes a playlist refresh with caching support
        /// </summary>
        private async Task ProcessPlaylistRefreshAsync(SmartPlaylistDto dto, CancellationToken cancellationToken)
        {
            // Multi-user playlists: Process each user in the UserPlaylists array
            if (dto.UserPlaylists != null && dto.UserPlaylists.Count > 0)
            {
                _logger.LogDebug("Processing multi-user playlist '{PlaylistName}' with {UserCount} users", dto.Name, dto.UserPlaylists.Count);
                
                var validUserCount = 0;
                foreach (var userMapping in dto.UserPlaylists)
                {
                    if (string.IsNullOrEmpty(userMapping.UserId) || !Guid.TryParse(userMapping.UserId, out var userId) || userId == Guid.Empty)
                    {
                        _logger.LogWarning("Skipping invalid user ID in UserPlaylists for playlist {PlaylistName}", dto.Name);
                        continue;
                    }

                    var user = _userManager.GetUserById(userId);
                    if (user == null)
                    {
                        _logger.LogWarning("User {UserId} not found for playlist {PlaylistName}, skipping", userId, dto.Name);
                        continue;
                    }

                    _logger.LogDebug("Processing playlist '{PlaylistName}' for user '{Username}'", dto.Name, user.Username);
                    await ProcessPlaylistForUserAsync(dto, user, cancellationToken);
                    validUserCount++;
                }

                // Warn if no valid users were processed
                if (validUserCount == 0)
                {
                    _logger.LogWarning("Playlist '{PlaylistName}' had no valid users to refresh (all {UserCount} users were invalid or missing)", 
                        dto.Name, dto.UserPlaylists.Count);
                }
            }
            // Single-user playlist (backwards compatibility): Use top-level UserId
            // DEPRECATED: This check is for backwards compatibility with old single-user playlists.
            // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
            else if (!string.IsNullOrEmpty(dto.UserId) && Guid.TryParse(dto.UserId, out var userId) && userId != Guid.Empty)
            {
                var user = _userManager.GetUserById(userId);
                if (user == null)
                {
                    _logger.LogWarning("User {UserId} not found for playlist {PlaylistName}, skipping refresh for this user", userId, dto.Name);
                    return; // Exit early instead of throwing
                }

                _logger.LogDebug("Processing single-user playlist '{PlaylistName}' for user '{Username}'", dto.Name, user.Username);
                await ProcessPlaylistForUserAsync(dto, user, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException($"Invalid user ID for playlist {dto.Name}");
            }
        }

        /// <summary>
        /// Processes a playlist for a single user
        /// </summary>
        private async Task ProcessPlaylistForUserAsync(SmartPlaylistDto dto, User user, CancellationToken cancellationToken)
        {
            // Get or create cache for this user
            var userCache = EnsureCacheForUser(user, dto);
            _logger.LogDebug("Processing playlist '{PlaylistName}' with user cache ({CacheEntryCount} entries)", dto.Name, userCache.Count);

            // Get media for this playlist using cache
            var mediaTypesForClosure = dto.MediaTypes?.ToList() ?? [];
            var mediaTypesKey = MediaTypesKey.Create(mediaTypesForClosure, dto);

            var playlistSpecificMedia = userCache.GetOrAdd(mediaTypesKey, _ =>
                new Lazy<BaseItem[]>(() =>
                {
                    _logger.LogDebug("Cache miss for MediaTypes [{MediaTypes}] for playlist '{PlaylistName}', fetching media", mediaTypesKey, dto.Name);
                    var playlistService = GetPlaylistService();
                    var media = playlistService.GetAllUserMediaForPlaylist(user, mediaTypesForClosure, dto).ToArray();
                    _logger.LogDebug("Cached {MediaCount} items for MediaTypes [{MediaTypes}] for user '{Username}'",
                        media.Length, mediaTypesKey, user.Username);
                    return media;
                }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication)
            ).Value;

            _logger.LogDebug("Retrieved {MediaCount} items from cache for playlist '{PlaylistName}'", playlistSpecificMedia.Length, dto.Name);

            // Update status with media count
            var listId = dto.Id ?? Guid.NewGuid().ToString();
            _refreshStatusService.UpdateProgress(listId, 0, playlistSpecificMedia.Length);

            // Create progress callback
            Action<int, int>? progressCallback = (processed, total) =>
            {
                _refreshStatusService.UpdateProgress(listId, processed, total);
            };

            // Get playlist service and store
            var playlistService = GetPlaylistService();
            var fileSystem = new SmartListFileSystem(_applicationPaths);
            var playlistStore = new PlaylistStore(fileSystem);

            // Get or create RefreshCache for this user
            var refreshCache = GetOrCreateRefreshCacheForUser(user.Id);
            _logger.LogDebug("Using RefreshCache for user '{Username}' (shared across playlists/collections)", user.Username);

            // Process refresh
            var (success, message, playlistId) = await playlistService.ProcessPlaylistRefreshWithCachedMediaAsync(
                dto,
                user,
                playlistSpecificMedia,
                refreshCache,
                async (updatedDto) => await playlistStore.SaveAsync(updatedDto),
                progressCallback,
                cancellationToken);

            if (!success)
            {
                throw new InvalidOperationException($"Playlist refresh failed for user {user.Username}: {message}");
            }
        }

        /// <summary>
        /// Processes a collection refresh with caching support
        /// </summary>
        private async Task ProcessCollectionRefreshAsync(SmartCollectionDto dto, CancellationToken cancellationToken)
        {
            // Get owner user for this collection
            if (string.IsNullOrEmpty(dto.UserId) || !Guid.TryParse(dto.UserId, out var ownerUserId) || ownerUserId == Guid.Empty)
            {
                _logger.LogError("Invalid owner user ID for collection '{CollectionName}': {UserId}", dto.Name, dto.UserId);
                throw new InvalidOperationException($"Invalid owner user ID for collection {dto.Name}");
            }

            var ownerUser = _userManager.GetUserById(ownerUserId);
            if (ownerUser == null)
            {
                _logger.LogError("Owner user {OwnerUserId} not found for collection '{CollectionName}'", ownerUserId, dto.Name);
                throw new InvalidOperationException($"Owner user {ownerUserId} not found for collection {dto.Name}");
            }

            // Get or create cache for this user (will share with playlists if same user/media types)
            var userCache = EnsureCacheForUser(ownerUser, dto);
            _logger.LogDebug("Processing collection '{CollectionName}' with user cache ({CacheEntryCount} entries)", dto.Name, userCache.Count);

            // Get media for this collection using cache
            var mediaTypesForClosure = dto.MediaTypes?.ToList() ?? [];
            var mediaTypesKey = MediaTypesKey.Create(mediaTypesForClosure, dto);

            var collectionSpecificMedia = userCache.GetOrAdd(mediaTypesKey, _ =>
                new Lazy<BaseItem[]>(() =>
                {
                    _logger.LogDebug("Cache miss for MediaTypes [{MediaTypes}] for collection '{CollectionName}', fetching media", mediaTypesKey, dto.Name);
                    var collectionService = GetCollectionService();
                    var media = collectionService.GetAllUserMediaForPlaylist(ownerUser, mediaTypesForClosure, dto).ToArray();
                    _logger.LogDebug("Cached {MediaCount} items for MediaTypes [{MediaTypes}] for user '{Username}' (collection '{CollectionName}')",
                        media.Length, mediaTypesKey, ownerUser.Username, dto.Name);
                    return media;
                }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication)
            ).Value;

            _logger.LogDebug("Retrieved {MediaCount} items from cache for collection '{CollectionName}'", collectionSpecificMedia.Length, dto.Name);

            // Update status with media count
            var listId = dto.Id ?? Guid.NewGuid().ToString();
            _refreshStatusService.UpdateProgress(listId, 0, collectionSpecificMedia.Length);

            // Create progress callback
            Action<int, int>? progressCallback = (processed, total) =>
            {
                _refreshStatusService.UpdateProgress(listId, processed, total);
            };

            // Get collection service and store
            var collectionService = GetCollectionService();
            var fileSystem = new SmartListFileSystem(_applicationPaths);
            var collectionStore = new CollectionStore(fileSystem);

            // Get or create RefreshCache for this user
            var refreshCache = GetOrCreateRefreshCacheForUser(ownerUserId);
            _logger.LogDebug("Using RefreshCache for user '{Username}' (shared across playlists/collections)", ownerUser.Username);

            // Process refresh with cached media
            var (success, message, collectionId) = await collectionService.ProcessPlaylistRefreshWithCachedMediaAsync(
                dto,
                ownerUser,
                collectionSpecificMedia,
                refreshCache,
                async (updatedDto) => await collectionStore.SaveAsync(updatedDto),
                progressCallback,
                cancellationToken);

            if (!success)
            {
                throw new InvalidOperationException($"Collection refresh failed: {message}");
            }
        }

        /// <summary>
        /// Ensures cache exists for the given user, creating if needed. Returns the user's cache.
        /// </summary>
        private ConcurrentDictionary<MediaTypesKey, Lazy<BaseItem[]>> EnsureCacheForUser(User user, SmartListDto dto)
        {
            var userCache = _userCaches.GetOrAdd(user.Id, _ =>
            {
                _logger.LogDebug("Created cache for user '{Username}'", user.Username);
                return new ConcurrentDictionary<MediaTypesKey, Lazy<BaseItem[]>>();
            });

            // Log if we're reusing an existing cache (for debugging)
            if (userCache.Count > 0)
            {
                _logger.LogDebug("Reusing existing cache for user '{Username}' ({CacheCount} entries)", user.Username, userCache.Count);
            }

            return userCache;
        }

        /// <summary>
        /// Gets or creates a RefreshCache for the specified user
        /// </summary>
        private RefreshCache GetOrCreateRefreshCacheForUser(Guid userId)
        {
            return _refreshCaches.GetOrAdd(userId, _ => new RefreshCache());
        }

        /// <summary>
        /// Clears all user caches
        /// </summary>
        private void ClearCache()
        {
            if (_userCaches.Count > 0)
            {
                var userCount = _userCaches.Count;
                foreach (var cache in _userCaches.Values)
                {
                    cache.Clear();
                }
                _userCaches.Clear();
                _logger.LogDebug("Cleared all user caches ({UserCount} users)", userCount);
            }

            if (_refreshCaches.Count > 0)
            {
                var refreshCacheCount = _refreshCaches.Count;
                _refreshCaches.Clear();
                _logger.LogDebug("Cleared all refresh caches ({RefreshCacheCount} users)", refreshCacheCount);
            }
        }

        /// <summary>
        /// Creates a PlaylistService instance
        /// </summary>
        private PlaylistService GetPlaylistService()
        {
            var playlistServiceLogger = _loggerFactory.CreateLogger<PlaylistService>();
            return new PlaylistService(
                _userManager,
                _libraryManager,
                _playlistManager,
                _userDataManager,
                playlistServiceLogger,
                _providerManager,
                _imageService);
        }

        /// <summary>
        /// Creates a CollectionService instance
        /// </summary>
        private CollectionService GetCollectionService()
        {
            var collectionServiceLogger = _loggerFactory.CreateLogger<CollectionService>();
            return new CollectionService(
                _libraryManager,
                _collectionManager,
                _userManager,
                _userDataManager,
                collectionServiceLogger,
                _providerManager,
                _imageService);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _cancellationTokenSource.Cancel();

            try
            {
                _processingTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for processing task to complete");
            }

            _cancellationTokenSource.Dispose();
            _processingLock.Dispose();
            ClearCache();

            _logger.LogInformation("RefreshQueueService disposed");
        }

        /// <summary>
        /// Per-refresh cache for expensive operations within single playlist processing.
        /// Uses ConcurrentDictionary for thread-safety during parallel processing.
        /// </summary>
        public sealed class RefreshCache
        {
            public ConcurrentDictionary<(Guid SeriesId, Guid UserId), BaseItem[]> SeriesEpisodes { get; } = new();
            public ConcurrentDictionary<(Guid SeriesId, Guid UserId, bool IncludeUnwatchedSeries), (Guid? NextEpisodeId, int Season, int Episode)> NextUnwatched { get; } = new();
            public ConcurrentDictionary<Guid, List<string>> ItemCollections { get; } = new();
            public BaseItem[]? AllCollections { get; set; } = null;
            public ConcurrentDictionary<Guid, HashSet<Guid>> CollectionMembershipCache { get; } = new();
            public ConcurrentDictionary<Guid, List<string>> ItemPlaylists { get; } = new();
            public BaseItem[]? AllPlaylists { get; set; } = null;
            public ConcurrentDictionary<Guid, HashSet<Guid>> PlaylistMembershipCache { get; } = new();
            public ConcurrentDictionary<Guid, string> SeriesNameById { get; } = new();
            public ConcurrentDictionary<Guid, string> SeriesSortNameById { get; } = new();
            public ConcurrentDictionary<Guid, List<string>> SeriesTagsById { get; } = new();
            public ConcurrentDictionary<Guid, List<string>> SeriesStudiosById { get; } = new();
            public ConcurrentDictionary<Guid, List<string>> SeriesGenresById { get; } = new();
            public ConcurrentDictionary<Guid, CategorizedPeople> ItemPeople { get; } = new();
            
            // User-specific data cache - keyed by (ItemId, UserId) to support playlist user + additional users in rules
            public ConcurrentDictionary<(Guid ItemId, Guid UserId), MediaBrowser.Controller.Entities.UserItemData> UserDataCache { get; } = new();
            
            // Media streams cache - keyed by ItemId only (user-agnostic)
            public ConcurrentDictionary<Guid, IEnumerable<object>> MediaStreamsCache { get; } = new();

            // Child items cache for sorting collections by child values
            // Maps Collection/Playlist ID → array of child BaseItems (with full item data for property access)
            public ConcurrentDictionary<Guid, BaseItem[]> CollectionChildItems { get; } = new();
            public ConcurrentDictionary<Guid, BaseItem[]> PlaylistChildItems { get; } = new();
        }

        /// <summary>
        /// Holds categorized people data for an item.
        /// </summary>
        public sealed class CategorizedPeople
        {
            public List<string> AllPeople { get; set; } = [];
            public List<string> Actors { get; set; } = [];
            public List<string> ActorRoles { get; set; } = []; // Character/role names
            public List<string> Directors { get; set; } = [];
            public List<string> Composers { get; set; } = [];
            public List<string> Writers { get; set; } = [];
            public List<string> GuestStars { get; set; } = [];
            public List<string> Producers { get; set; } = [];
            public List<string> Conductors { get; set; } = [];
            public List<string> Lyricists { get; set; } = [];
            public List<string> Arrangers { get; set; } = [];
            public List<string> SoundEngineers { get; set; } = [];
            public List<string> Mixers { get; set; } = [];
            public List<string> Remixers { get; set; } = [];
            public List<string> Creators { get; set; } = [];
            public List<string> PersonArtists { get; set; } = []; // Person role "Artist" (different from music Artists field)
            public List<string> PersonAlbumArtists { get; set; } = []; // Person role "Album Artist" (different from music AlbumArtists field)
            public List<string> Authors { get; set; } = [];
            public List<string> Illustrators { get; set; } = [];
            public List<string> Pencilers { get; set; } = [];
            public List<string> Inkers { get; set; } = [];
            public List<string> Colorists { get; set; } = [];
            public List<string> Letterers { get; set; } = [];
            public List<string> CoverArtists { get; set; } = [];
            public List<string> Editors { get; set; } = [];
            public List<string> Translators { get; set; } = [];
        }
    }
}

