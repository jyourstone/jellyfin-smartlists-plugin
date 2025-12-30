using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Core.Enums;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Playlists;
using Jellyfin.Plugin.SmartLists.Services.Abstractions;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Represents the previous state of UserData for change detection
    /// </summary>
    internal sealed class UserDataState
    {
        public bool Played { get; set; }
        public int PlayCount { get; set; }
        public bool IsFavorite { get; set; }
        public DateTime? LastPlayedDate { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    public enum LibraryChangeType
    {
        Added,
        Removed,
        Updated,
    }

    /// <summary>
    /// Service for handling automatic smart playlist refreshes based on library changes.
    /// Implements intelligent batching and debouncing to handle high-frequency library events.
    /// </summary>
    public class AutoRefreshService : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<AutoRefreshService> _logger;
        private readonly ISmartListStore<SmartPlaylistDto> _playlistStore;
        private readonly ISmartListService<SmartPlaylistDto> _playlistService;
        private readonly ISmartListStore<SmartCollectionDto> _collectionStore;
        private readonly ISmartListService<SmartCollectionDto> _collectionService;
        private readonly IUserDataManager _userDataManager;
        private readonly IUserManager _userManager;
        private readonly RefreshCache _playlistRefreshCache;
        private readonly RefreshQueueService _refreshQueueService;

        // Static reference for API access to cache management
        public static AutoRefreshService? Instance { get; private set; }



        // State tracking
        private volatile bool _disposed = false;

        // UserData state tracking for change detection
        private readonly ConcurrentDictionary<string, UserDataState> _userDataStateCache = new();
        private const int MAX_USERDATA_CACHE_SIZE = 1000; // Limit cache size to prevent memory leaks

        // Performance optimization: Cache mapping rule types to playlists that use them
        // Key format: "MediaType+FieldType" (e.g., "Movie+PlaybackStatus", "Episode+SeriesName")
        private readonly ConcurrentDictionary<string, HashSet<string>> _ruleTypeToPlaylistsCache = new();

        // Simpler cache: MediaType -> All playlists for that media type (regardless of fields)
        // Key format: "MediaType" (e.g., "Movie", "Episode", "Audio")
        private readonly ConcurrentDictionary<string, HashSet<string>> _mediaTypeToPlaylistsCache = new();

        // Collection caches (similar to playlist caches)
        private readonly ConcurrentDictionary<string, HashSet<string>> _ruleTypeToCollectionsCache = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _mediaTypeToCollectionsCache = new();

        private volatile bool _cacheInitialized = false;
        private readonly object _cacheInvalidationLock = new();

        // Batch processing for library events (add/remove) to avoid spam during bulk operations
        private readonly ConcurrentDictionary<string, DateTime> _pendingLibraryRefreshes = new();
        private readonly Timer _batchProcessTimer;
        private readonly TimeSpan _batchDelay = TimeSpan.FromSeconds(3); // Short delay for batching library events

        // Schedule checking for custom playlist schedules
        private Timer? _scheduleTimer;

        private readonly RefreshStatusService? _refreshStatusService;

        public AutoRefreshService(
            ILibraryManager libraryManager,
            ILogger<AutoRefreshService> logger,
            ISmartListStore<SmartPlaylistDto> playlistStore,
            ISmartListService<SmartPlaylistDto> playlistService,
            ISmartListStore<SmartCollectionDto> collectionStore,
            ISmartListService<SmartCollectionDto> collectionService,
            IUserDataManager userDataManager,
            IUserManager userManager,
            RefreshQueueService refreshQueueService,
            RefreshStatusService? refreshStatusService = null)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _playlistStore = playlistStore;
            _playlistService = playlistService;
            _collectionStore = collectionStore;
            _collectionService = collectionService;
            _userDataManager = userDataManager;
            _userManager = userManager;
            _refreshStatusService = refreshStatusService;
            _refreshQueueService = refreshQueueService;

            // Initialize cache helper - using interfaces for proper dependency injection
            _playlistRefreshCache = new RefreshCache(
                _libraryManager,
                _userManager,
                _playlistService,
                _playlistStore,
                _logger);

            // Set static instance for API access
            Instance = this;

            // Note: RefreshCache is used for scheduled batch processing to efficiently process multiple lists.
            // The RefreshQueueService handles all refresh operations (manual, auto, and scheduled).
            // Initialize batch processing timer (runs every 1 second to check for pending refreshes)
            _batchProcessTimer = new Timer(ProcessPendingBatchRefreshes, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            // Initialize schedule checking timer - align to 15-minute boundaries (:00, :15, :30, :45)
            InitializeScheduleTimer();



            // Subscribe to library events
            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemRemoved += OnItemRemoved;
            _libraryManager.ItemUpdated += OnItemUpdated;

            // Subscribe to user data events (for playback status changes)
            _userDataManager.UserDataSaved += OnUserDataSaved;

            _logger.LogDebug("AutoRefreshService initialized (batch delay: {DelaySeconds}s for all changes)", _batchDelay.TotalSeconds);

            // Initialize the rule cache in the background
            _ = Task.Run(InitializeRuleCache);
        }

        private async Task InitializeRuleCache()
        {
            try
            {
                _logger.LogDebug("Initializing rule type cache for performance optimization...");

                var playlists = await _playlistStore.GetAllAsync();
                var playlistCacheEntries = 0;

                var collections = await _collectionStore.GetAllAsync();
                var collectionCacheEntries = 0;

                // Protect cache mutations with lock to prevent race conditions with concurrent reads
                lock (_cacheInvalidationLock)
                {
                    foreach (var playlist in playlists)
                    {
                        if (playlist.Enabled && playlist.AutoRefresh != AutoRefreshMode.Never)
                        {
                            AddPlaylistToRuleCache(playlist);
                            playlistCacheEntries++;
                        }
                    }

                    foreach (var collection in collections)
                    {
                        if (collection.Enabled && collection.AutoRefresh != AutoRefreshMode.Never)
                        {
                            AddCollectionToRuleCache(collection);
                            collectionCacheEntries++;
                        }
                    }

                    _cacheInitialized = true;
                }

                _logger.LogInformation("Auto-refresh cache initialized: {PlaylistCount} playlists, {CollectionCount} collections, {MediaTypeCount} media types, {RuleTypeCount} field-based entries",
                    playlistCacheEntries, collectionCacheEntries, _mediaTypeToPlaylistsCache.Count + _mediaTypeToCollectionsCache.Count, 
                    _ruleTypeToPlaylistsCache.Count + _ruleTypeToCollectionsCache.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize rule type cache - will fall back to checking all lists");
            }
        }

        private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            if (_disposed) return;
            if (e.Item == null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleLibraryChangeAsync(e.Item, LibraryChangeType.Added).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling item added event for {ItemName}", e.Item?.Name ?? "unknown");
                }
            });
        }

        private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
        {
            if (_disposed) return;
            if (e.Item == null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleLibraryChangeAsync(e.Item, LibraryChangeType.Removed).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling item removed event for {ItemName}", e.Item?.Name ?? "unknown");
                }
            });
        }

        private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            if (_disposed) return;
            if (e.Item == null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleLibraryChangeAsync(e.Item, LibraryChangeType.Updated).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling item updated event for {ItemName}", e.Item?.Name ?? "unknown");
                }
            });
        }

        private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
        {
            if (_disposed) return;
            if (e.Item == null) return;

            // Handle playback status changes (IsPlayed, PlayCount, etc.)
            // Now uses batching like OnItemUpdated to handle bulk operations (e.g., marking series as watched)
            try
            {
                // Check relevance first to avoid unnecessary DB calls for progress updates
                if (IsRelevantUserDataChange(e))
                {
                    var item = _libraryManager.GetItemById(e.Item.Id);
                    if (item == null)
                    {
                        _logger.LogDebug("Item {ItemId} not found when processing user data change - item may have been deleted", e.Item.Id);
                        return;
                    }

                    _logger.LogDebug("Relevant user data change for item '{ItemName}' by user {UserId} - queuing for batched refresh",
                        item.Name, e.UserId);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Use batched processing to handle bulk operations efficiently
                            // The triggeringUserId allows filtering to OnAllChanges playlists only
                            await HandleLibraryChangeAsync(item, LibraryChangeType.Updated, triggeringUserId: e.UserId).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error handling user data change for {ItemName}", item?.Name ?? "unknown");
                        }
                    });
                }
                else
                {
                    _logger.LogDebug("Ignoring non-relevant user data change for ItemId={ItemId} (progress update, etc.)", e.Item.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling UserDataSaved event for item {ItemId}", e.Item?.Id);
            }
        }

        private bool IsRelevantUserDataChange(UserDataSaveEventArgs e)
        {
            if (e.UserData == null) return false;

            try
            {
                var userData = e.UserData;
                var itemId = e.Item.Id.ToString();
                var userId = e.UserId.ToString();
                var cacheKey = $"{itemId}:{userId}";

                // Extract current state
                var currentState = new UserDataState
                {
                    Played = userData.Played,
                    PlayCount = userData.PlayCount,
                    IsFavorite = userData.IsFavorite,
                    LastPlayedDate = userData.LastPlayedDate,
                    LastUpdated = DateTime.UtcNow,
                };

                // Check if we have previous state
                if (_userDataStateCache.TryGetValue(cacheKey, out var previousState))
                {
                    // Compare with previous state to detect meaningful changes
                    var hasSignificantChange =
                        currentState.Played != previousState.Played ||           // Watch/unwatch
                        currentState.PlayCount != previousState.PlayCount ||     // Play count changed
                        currentState.IsFavorite != previousState.IsFavorite ||   // Favorite status changed
                        currentState.LastPlayedDate != previousState.LastPlayedDate; // Last played date changed (set or cleared)

                    if (hasSignificantChange)
                    {
                        _logger.LogDebug("Detected significant UserData change for item {ItemId}: Played {PrevPlayed}→{CurrPlayed}, PlayCount {PrevCount}→{CurrCount}, Favorite {PrevFav}→{CurrFav}",
                            itemId, previousState.Played, currentState.Played, previousState.PlayCount, currentState.PlayCount,
                            previousState.IsFavorite, currentState.IsFavorite);

                        // Update cache with new state
                        _userDataStateCache[cacheKey] = currentState;

                        // Cleanup cache if it gets too large
                        CleanupUserDataCacheIfNeeded();

                        return true;
                    }
                    else
                    {
                        // No significant change - likely just a progress update
                        return false;
                    }
                }
                else
                {
                    // First time seeing this item/user combo - always store state for future comparisons
                    _userDataStateCache[cacheKey] = currentState;

                    // For first-time events, only trigger if it's a meaningful state (watched, favorite, etc.)
                    // This avoids triggering on initial "empty" state loads
                    var isMeaningfulState = currentState.Played || currentState.PlayCount > 0 ||
                                          currentState.IsFavorite || currentState.LastPlayedDate.HasValue;

                    if (isMeaningfulState)
                    {
                        _logger.LogDebug("First UserData event for item {ItemId} with meaningful state: Played={Played}, PlayCount={PlayCount}, Favorite={IsFavorite}",
                            itemId, currentState.Played, currentState.PlayCount, currentState.IsFavorite);
                    }

                    return isMeaningfulState;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking UserData relevance - assuming relevant");
                return true; // Default to processing if we can't determine,
            }
        }

        private void CleanupUserDataCacheIfNeeded()
        {
            if (_userDataStateCache.Count > MAX_USERDATA_CACHE_SIZE)
            {
                try
                {
                    // Remove oldest entries (simple cleanup - remove 25% of entries)
                    var entriesToRemove = _userDataStateCache.Count / 4;
                    var oldestEntries = _userDataStateCache
                        .OrderBy(kvp => kvp.Value.LastUpdated)
                        .Take(entriesToRemove)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in oldestEntries)
                    {
                        _userDataStateCache.TryRemove(key, out _);
                    }

                    _logger.LogDebug("Cleaned up {Count} old UserData cache entries", oldestEntries.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error cleaning up UserData cache");
                }
            }
        }

        private async Task HandleLibraryChangeAsync(BaseItem item, LibraryChangeType changeType, Guid? triggeringUserId = null)
        {
            try
            {
                // Skip processing if the item is a playlist or collection itself
                if (item is MediaBrowser.Controller.Playlists.Playlist)
                {
                    _logger.LogDebug("Skipping auto-refresh for playlist item '{ItemName}' - playlists don't trigger other playlist refreshes", item.Name);
                    return;
                }

                if (item.GetBaseItemKind() == BaseItemKind.BoxSet)
                {
                    _logger.LogDebug("Skipping auto-refresh for collection item '{ItemName}' - collections don't trigger other collection refreshes", item.Name);
                    return;
                }

                // Find playlists that might be affected by this change
                var affectedPlaylistIds = await GetAffectedPlaylistsAsync(item, changeType, triggeringUserId).ConfigureAwait(false);

                // Find collections that might be affected by this change
                // Collections don't use user-specific fields, so no triggeringUserId needed
                var affectedCollectionIds = await GetAffectedCollectionsAsync(item, changeType).ConfigureAwait(false);

                if (affectedPlaylistIds.Any() || affectedCollectionIds.Any())
                {
                    _logger.LogDebug("Queued {PlaylistCount} playlists and {CollectionCount} collections for batched refresh due to {ChangeType} of '{ItemName}'",
                        affectedPlaylistIds.Count, affectedCollectionIds.Count, changeType, item.Name);

                    foreach (var playlistId in affectedPlaylistIds)
                    {
                        _pendingLibraryRefreshes[playlistId] = DateTime.UtcNow.Add(_batchDelay);
                    }

                    foreach (var collectionId in affectedCollectionIds)
                    {
                        _pendingLibraryRefreshes[collectionId] = DateTime.UtcNow.Add(_batchDelay);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling library change for item {ItemName}", item.Name);
            }
        }

        private void ProcessPendingBatchRefreshes(object? state)
        {
            if (_disposed) return;

            try
            {
                var now = DateTime.UtcNow;
                var readyToProcess = new List<string>();

                // Find playlists that are ready to be refreshed (delay has passed)
                foreach (var kvp in _pendingLibraryRefreshes.ToList())
                {
                    if (now >= kvp.Value)
                    {
                        readyToProcess.Add(kvp.Key);
                        _pendingLibraryRefreshes.TryRemove(kvp.Key, out _);
                    }
                }

                if (readyToProcess.Any())
                {
                    _logger.LogInformation("Auto-refreshing {ListCount} smart lists after library changes", readyToProcess.Count);
                    _logger.LogDebug("Processing batched refresh for {ListCount} lists after {DelaySeconds}s delay",
                        readyToProcess.Count, _batchDelay.TotalSeconds);

                    // Process the batch in background - separate playlists and collections
                    _ = Task.Run(async () => await ProcessListRefreshes(readyToProcess));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing pending batch refreshes");
            }
        }

        private void AddPlaylistToRuleCache(SmartPlaylistDto playlist)
        {
            var mediaTypes = playlist.MediaTypes?.ToList() ?? [.. MediaTypes.All];

            // Add to the simple media type cache (always, regardless of rules)
            if (string.IsNullOrEmpty(playlist.Id))
            {
                return; // Skip playlists without ID
            }

            foreach (var mediaType in mediaTypes)
            {
                _mediaTypeToPlaylistsCache.AddOrUpdate(
                    mediaType,
                    new HashSet<string> { playlist.Id },
                    (key, existing) =>
                    {
                        existing.Add(playlist.Id);
                        return existing;
                    }
                );
            }

            // Also maintain the detailed field-based cache (kept for potential future optimizations)
            // Handle playlists with no rules - they should be refreshed for any change to their media types
            if (playlist.ExpressionSets == null || !playlist.ExpressionSets.Any() ||
                !playlist.ExpressionSets.Any(es => es.Expressions?.Any() == true))
            {
                foreach (var mediaType in mediaTypes)
                {
                    // Use a special cache key for "any field" in this media type
                    var cacheKey = $"{mediaType}+*";

                    _ruleTypeToPlaylistsCache.AddOrUpdate(
                        cacheKey,
                        new HashSet<string> { playlist.Id! },
                        (key, existing) =>
                        {
                            existing.Add(playlist.Id!);
                            return existing;
                        }
                    );
                }
                return;
            }

            // Handle playlists with specific rules
            foreach (var expressionSet in playlist.ExpressionSets)
            {
                if (expressionSet.Expressions == null) continue;

                foreach (var expression in expressionSet.Expressions)
                {
                    // Skip expressions with empty or whitespace-only field names to avoid malformed cache keys
                    if (string.IsNullOrWhiteSpace(expression.MemberName)) continue;

                    foreach (var mediaType in mediaTypes)
                    {
                        var cacheKey = $"{mediaType}+{expression.MemberName}";

                        _ruleTypeToPlaylistsCache.AddOrUpdate(
                            cacheKey,
                            new HashSet<string> { playlist.Id },
                            (key, existing) =>
                            {
                                existing.Add(playlist.Id);
                                return existing;
                            }
                        );
                    }
                }
            }
        }

        private void RemovePlaylistFromRuleCache(string playlistId)
        {
            // Remove from field-based cache
            foreach (var kvp in _ruleTypeToPlaylistsCache.ToList())
            {
                kvp.Value.Remove(playlistId);
                if (kvp.Value.Count == 0)
                {
                    _ruleTypeToPlaylistsCache.TryRemove(kvp.Key, out _);
                }
            }

            // Remove from media type cache
            foreach (var kvp in _mediaTypeToPlaylistsCache.ToList())
            {
                kvp.Value.Remove(playlistId);
                if (kvp.Value.Count == 0)
                {
                    _mediaTypeToPlaylistsCache.TryRemove(kvp.Key, out _);
                }
            }
        }

        public void InvalidateRuleCache()
        {
            lock (_cacheInvalidationLock)
            {
                _ruleTypeToPlaylistsCache.Clear();
                _mediaTypeToPlaylistsCache.Clear();
                _cacheInitialized = false;
            }

            // Start cache initialization outside the lock to avoid holding it unnecessarily
            _ = Task.Run(InitializeRuleCache);
        }

        public void UpdatePlaylistInCache(SmartPlaylistDto playlist)
        {
            ArgumentNullException.ThrowIfNull(playlist);

            lock (_cacheInvalidationLock)
            {
                if (!_cacheInitialized) return;

                try
                {
                    // Remove old entries for this playlist
                    if (!string.IsNullOrEmpty(playlist.Id))
                    {
                        RemovePlaylistFromRuleCache(playlist.Id);
                    }

                    // Add new entries if playlist is enabled and has auto-refresh
                    if (playlist.Enabled && playlist.AutoRefresh != AutoRefreshMode.Never)
                    {
                        AddPlaylistToRuleCache(playlist);
                        _logger.LogDebug("Updated cache for playlist '{PlaylistName}'", playlist.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating cache for playlist '{PlaylistName}' - invalidating cache", playlist.Name);
                    // Clear cache and mark as uninitialized, but don't call InvalidateRuleCache to avoid recursive lock
                    _ruleTypeToPlaylistsCache.Clear();
                    _mediaTypeToPlaylistsCache.Clear();
                    _cacheInitialized = false;
                }
            }

            // If we had an error and cleared the cache, reinitialize it outside the lock
            if (!_cacheInitialized)
            {
                _ = Task.Run(InitializeRuleCache);
            }
        }

        public void RemovePlaylistFromCache(string playlistId)
        {
            lock (_cacheInvalidationLock)
            {
                if (!_cacheInitialized) return;

                try
                {
                    RemovePlaylistFromRuleCache(playlistId);
                    _logger.LogDebug("Removed playlist {PlaylistId} from cache", playlistId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error removing playlist {PlaylistId} from cache - invalidating cache", playlistId);
                    // Clear cache and mark as uninitialized, but don't call InvalidateRuleCache to avoid recursive lock
                    _ruleTypeToPlaylistsCache.Clear();
                    _mediaTypeToPlaylistsCache.Clear();
                    _cacheInitialized = false;
                }
            }

            // If we had an error and cleared the cache, reinitialize it outside the lock
            if (!_cacheInitialized)
            {
                _ = Task.Run(InitializeRuleCache);
            }
        }

        public void UpdateCollectionInCache(SmartCollectionDto collection)
        {
            ArgumentNullException.ThrowIfNull(collection);

            lock (_cacheInvalidationLock)
            {
                if (!_cacheInitialized) return;

                try
                {
                    // Remove old entries for this collection
                    if (!string.IsNullOrEmpty(collection.Id))
                    {
                        RemoveCollectionFromRuleCache(collection.Id);
                    }

                    // Add new entries if collection is enabled and has auto-refresh
                    if (collection.Enabled && collection.AutoRefresh != AutoRefreshMode.Never)
                    {
                        AddCollectionToRuleCache(collection);
                        _logger.LogDebug("Updated cache for collection '{CollectionName}'", collection.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating cache for collection '{CollectionName}' - invalidating cache", collection.Name);
                    _ruleTypeToCollectionsCache.Clear();
                    _mediaTypeToCollectionsCache.Clear();
                    _cacheInitialized = false;
                }
            }

            if (!_cacheInitialized)
            {
                _ = Task.Run(InitializeRuleCache);
            }
        }

        public void RemoveCollectionFromCache(string collectionId)
        {
            lock (_cacheInvalidationLock)
            {
                if (!_cacheInitialized) return;

                try
                {
                    RemoveCollectionFromRuleCache(collectionId);
                    _logger.LogDebug("Removed collection {CollectionId} from cache", collectionId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error removing collection {CollectionId} from cache - invalidating cache", collectionId);
                    _ruleTypeToCollectionsCache.Clear();
                    _mediaTypeToCollectionsCache.Clear();
                    _cacheInitialized = false;
                }
            }

            if (!_cacheInitialized)
            {
                _ = Task.Run(InitializeRuleCache);
            }
        }

        private void AddCollectionToRuleCache(SmartCollectionDto collection)
        {
            var mediaTypes = collection.MediaTypes?.ToList() ?? [.. MediaTypes.All];

            if (string.IsNullOrEmpty(collection.Id))
            {
                return;
            }

            foreach (var mediaType in mediaTypes)
            {
                _mediaTypeToCollectionsCache.AddOrUpdate(
                    mediaType,
                    new HashSet<string> { collection.Id },
                    (key, existing) =>
                    {
                        existing.Add(collection.Id);
                        return existing;
                    }
                );
            }

            // Handle collections with no rules
            if (collection.ExpressionSets == null || !collection.ExpressionSets.Any() ||
                !collection.ExpressionSets.Any(es => es.Expressions?.Any() == true))
            {
                foreach (var mediaType in mediaTypes)
                {
                    var cacheKey = $"{mediaType}+Any";
                    _ruleTypeToCollectionsCache.AddOrUpdate(
                        cacheKey,
                        new HashSet<string> { collection.Id },
                        (key, existing) =>
                        {
                            existing.Add(collection.Id);
                            return existing;
                        }
                    );
                }
            }
            else
            {
                // Add field-based cache entries
                // Note: Collections DO use user-specific fields (PlaybackStatus, IsFavorite, etc.) via the reference user
                foreach (var expressionSet in collection.ExpressionSets)
                {
                    if (expressionSet.Expressions == null) continue;

                    foreach (var expression in expressionSet.Expressions)
                    {
                        if (string.IsNullOrEmpty(expression.MemberName)) continue;

                        foreach (var mediaType in mediaTypes)
                        {
                            var cacheKey = $"{mediaType}+{expression.MemberName}";
                            _ruleTypeToCollectionsCache.AddOrUpdate(
                                cacheKey,
                                new HashSet<string> { collection.Id },
                                (key, existing) =>
                                {
                                    existing.Add(collection.Id);
                                    return existing;
                                }
                            );
                        }
                    }
                }
            }
        }

        private void RemoveCollectionFromRuleCache(string collectionId)
        {
            // Remove from field-based cache
            foreach (var kvp in _ruleTypeToCollectionsCache.ToList())
            {
                kvp.Value.Remove(collectionId);
                if (kvp.Value.Count == 0)
                {
                    _ruleTypeToCollectionsCache.TryRemove(kvp.Key, out _);
                }
            }

            // Remove from media type cache
            foreach (var kvp in _mediaTypeToCollectionsCache.ToList())
            {
                kvp.Value.Remove(collectionId);
                if (kvp.Value.Count == 0)
                {
                    _mediaTypeToCollectionsCache.TryRemove(kvp.Key, out _);
                }
            }
        }

        private async Task<List<string>> GetAffectedPlaylistsAsync(BaseItem item, LibraryChangeType changeType, Guid? triggeringUserId = null)
        {
            // Use cache for performance optimization if available
            if (_cacheInitialized)
            {
                return await GetAffectedPlaylistsFromCacheAsync(item, changeType, triggeringUserId).ConfigureAwait(false);
            }

            // Fallback to checking all playlists if cache not ready
            return await GetAffectedPlaylistsFallbackAsync(item, changeType, triggeringUserId).ConfigureAwait(false);
        }

        private async Task<List<string>> GetAffectedPlaylistsFromCacheAsync(BaseItem item, LibraryChangeType changeType, Guid? triggeringUserId = null)
        {
            var affectedPlaylists = new HashSet<string>();

            try
            {
                var mediaType = GetMediaTypeFromItem(item);

                // For Removed events, skip entirely (Jellyfin auto-removes items from playlists)
                if (changeType == LibraryChangeType.Removed)
                {
                    _logger.LogDebug("Item '{ItemName}' removed - Jellyfin will auto-remove from playlists, no refresh needed", item.Name);
                    return [];
                }

                // Use the simple media type cache to get all playlists for this media type
                // This is much more efficient than looping through all possible fields
                // Protect read operations with lock to prevent race conditions with concurrent mutations
                lock (_cacheInvalidationLock)
                {
                    if (_mediaTypeToPlaylistsCache.TryGetValue(mediaType, out var playlistIds))
                    {
                        // Snapshot to avoid concurrent modification during enumeration
                        affectedPlaylists.UnionWith(playlistIds.ToArray());
                    }
                }

                // Apply filtering based on change type and auto-refresh mode
                // This is where the real filtering happens (OnLibraryChanges vs OnAllChanges)
                var finalPlaylists = await FilterPlaylistsByChangeTypeAsync(affectedPlaylists.ToList(), changeType, triggeringUserId).ConfigureAwait(false);

                if (triggeringUserId.HasValue)
                {
                    _logger.LogDebug("Cache-based filtering: {ItemName} ({MediaType}) affects {PlaylistCount} playlists after user data filtering (user: {UserId})",
                        item.Name, mediaType, finalPlaylists.Count, triggeringUserId.Value);
                }
                else
                {
                    _logger.LogDebug("Cache-based filtering: {ItemName} ({MediaType}) affects {PlaylistCount} playlists after library change filtering",
                        item.Name, mediaType, finalPlaylists.Count);
                }

                return finalPlaylists;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error using cache to determine affected playlists for item {ItemName} - falling back", item.Name);
                return await GetAffectedPlaylistsFallbackAsync(item, changeType, triggeringUserId).ConfigureAwait(false);
            }
        }

        private async Task<List<string>> GetAffectedPlaylistsFallbackAsync(BaseItem item, LibraryChangeType changeType, Guid? triggeringUserId = null)
        {
            var affectedPlaylists = new List<string>();

            try
            {
                // Get all playlists that have auto-refresh enabled
                var allPlaylists = await _playlistStore.GetAllAsync().ConfigureAwait(false);
                var autoRefreshPlaylists = allPlaylists.Where(p => p.AutoRefresh != AutoRefreshMode.Never && p.Enabled);

                foreach (var playlist in autoRefreshPlaylists)
                {
                    // Check if this playlist should be refreshed based on the change type
                    if (ShouldPlaylistRefreshForChangeType(playlist, changeType, triggeringUserId))
                    {
                        // Additional filtering: check if the item type matches the playlist's media types
                        if (IsItemRelevantToPlaylist(item, playlist))
                        {
                            // User-specific filtering for UserData events (playback status changes)
                            if (triggeringUserId.HasValue && !IsUserRelevantToPlaylist(triggeringUserId.Value, playlist))
                            {
                                _logger.LogDebug("Skipping playlist '{PlaylistName}' - user {UserId} not relevant to this playlist",
                                    playlist.Name, triggeringUserId.Value);
                                continue;
                            }

                            if (!string.IsNullOrEmpty(playlist.Id))
                            {
                                affectedPlaylists.Add(playlist.Id);
                            }
                        }
                    }
                }

                _logger.LogDebug("Fallback filtering: {ItemName} potentially affects {PlaylistCount} playlists",
                    item.Name, affectedPlaylists.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error determining affected playlists for item {ItemName}", item.Name);
            }

            return affectedPlaylists;
        }

        private async Task<List<string>> GetAffectedCollectionsAsync(BaseItem item, LibraryChangeType changeType)
        {
            // Use cache for performance optimization if available
            if (_cacheInitialized)
            {
                return await GetAffectedCollectionsFromCacheAsync(item, changeType).ConfigureAwait(false);
            }

            // Fallback to checking all collections if cache not ready
            return await GetAffectedCollectionsFallbackAsync(item, changeType).ConfigureAwait(false);
        }

        private async Task<List<string>> GetAffectedCollectionsFromCacheAsync(BaseItem item, LibraryChangeType changeType)
        {
            var affectedCollections = new HashSet<string>();

            try
            {
                var mediaType = GetMediaTypeFromItem(item);

                // For Removed events, skip entirely (Jellyfin auto-removes items from collections)
                if (changeType == LibraryChangeType.Removed)
                {
                    _logger.LogDebug("Item '{ItemName}' removed - Jellyfin will auto-remove from collections, no refresh needed", item.Name);
                    return [];
                }

                // Use the simple media type cache to get all collections for this media type
                // Protect read operations with lock to prevent race conditions with concurrent mutations
                lock (_cacheInvalidationLock)
                {
                    if (_mediaTypeToCollectionsCache.TryGetValue(mediaType, out var collectionIds))
                    {
                        affectedCollections.UnionWith(collectionIds.ToArray());
                    }

                    // When an episode's watch status changes, also check for Series-based collections
                    // This is needed because marking a series as watched/unwatched in Jellyfin only fires
                    // UserDataSaved events for individual episodes, not for the Series item itself
                    if (item is MediaBrowser.Controller.Entities.TV.Episode episode && episode.SeriesId != Guid.Empty)
                    {
                        // Also get collections that use Series media type
                        if (_mediaTypeToCollectionsCache.TryGetValue(MediaTypes.Series, out var seriesCollectionIds))
                        {
                            affectedCollections.UnionWith(seriesCollectionIds.ToArray());
                            _logger.LogDebug("Episode '{EpisodeName}' change also affects {SeriesCollectionCount} Series-based collections (parent series: {SeriesId})",
                                item.Name, seriesCollectionIds.Count, episode.SeriesId);
                        }
                    }
                }

                // Apply filtering based on change type and auto-refresh mode
                var finalCollections = await FilterCollectionsByChangeTypeAsync(affectedCollections.ToList(), changeType).ConfigureAwait(false);

                _logger.LogDebug("Cache-based filtering: {ItemName} ({MediaType}) affects {CollectionCount} collections after library change filtering",
                    item.Name, mediaType, finalCollections.Count);

                return finalCollections;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error using cache to determine affected collections for item {ItemName} - falling back", item.Name);
                return await GetAffectedCollectionsFallbackAsync(item, changeType).ConfigureAwait(false);
            }
        }

        private async Task<List<string>> GetAffectedCollectionsFallbackAsync(BaseItem item, LibraryChangeType changeType)
        {
            var affectedCollections = new List<string>();

            try
            {
                // Get all collections that have auto-refresh enabled
                var allCollections = await _collectionStore.GetAllAsync().ConfigureAwait(false);
                var autoRefreshCollections = allCollections.Where(c => c.AutoRefresh != AutoRefreshMode.Never && c.Enabled);

                foreach (var collection in autoRefreshCollections)
                {
                    // Check if this collection should be refreshed based on the change type
                    if (ShouldCollectionRefreshForChangeType(collection, changeType))
                    {
                        // Additional filtering: check if the item type matches the collection's media types
                        if (IsItemRelevantToCollection(item, collection))
                        {
                            if (!string.IsNullOrEmpty(collection.Id))
                            {
                                affectedCollections.Add(collection.Id);
                            }
                        }
                        // Also check if this is an episode and the collection uses Series media type
                        // This handles the case where marking a series as watched/unwatched only fires
                        // UserDataSaved events for individual episodes
                        else if (item is MediaBrowser.Controller.Entities.TV.Episode episode &&
                                 episode.SeriesId != Guid.Empty &&
                                 collection.MediaTypes != null &&
                                 collection.MediaTypes.Contains(MediaTypes.Series))
                        {
                            if (!string.IsNullOrEmpty(collection.Id))
                            {
                                affectedCollections.Add(collection.Id);
                                _logger.LogDebug("Episode '{EpisodeName}' affects Series-based collection '{CollectionName}' (fallback path)",
                                    item.Name, collection.Name);
                            }
                        }
                    }
                }

                _logger.LogDebug("Fallback filtering: {ItemName} potentially affects {CollectionCount} collections",
                    item.Name, affectedCollections.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error determining affected collections for item {ItemName}", item.Name);
            }

            return affectedCollections;
        }

        private bool IsUserRelevantToPlaylist(Guid userId, SmartPlaylistDto playlist)
        {
            try
            {
                // Check if the user is in the UserPlaylists array (multi-user playlists)
                // UserPlaylists stores UserId in "N" format (no dashes), so normalize for comparison
                if (playlist.UserPlaylists != null && playlist.UserPlaylists.Count > 0)
                {
                    var normalizedTriggeringUserId = userId.ToString("N");
                    foreach (var userMapping in playlist.UserPlaylists)
                    {
                        if (!string.IsNullOrEmpty(userMapping.UserId))
                        {
                            var normalizedMappingUserId = NormalizeUserIdForComparison(userMapping.UserId);
                            if (string.Equals(normalizedMappingUserId, normalizedTriggeringUserId, StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                    }
                }

                // Check if the user is the playlist user (backwards compatibility for single-user playlists)
                // DEPRECATED: This check is for backwards compatibility with old single-user playlists.
                // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
                if (!string.IsNullOrEmpty(playlist.UserId) && Guid.TryParse(playlist.UserId, out var playlistUserId) && playlistUserId == userId)
                {
                    return true;
                }

                // Check if the playlist has user-specific rules that reference this user
                if (playlist.ExpressionSets != null)
                {
                    foreach (var expressionSet in playlist.ExpressionSets)
                    {
                        if (expressionSet.Expressions != null)
                        {
                            foreach (var expression in expressionSet.Expressions)
                            {
                                // Check if this expression is user-specific and references our user explicitly
                                // Normalize UserId to "N" format for comparison
                                if (!string.IsNullOrEmpty(expression.UserId))
                                {
                                    var normalizedExpressionUserId = NormalizeUserIdForComparison(expression.UserId);
                                    var normalizedTriggeringUserId = userId.ToString("N");
                                    if (normalizedExpressionUserId == normalizedTriggeringUserId)
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking user relevance for playlist {PlaylistName}", playlist.Name);
                return true; // Default to including the playlist if we can't determine relevance,
            }
        }

        /// <summary>
        /// Normalizes a UserId string to "N" format (no dashes) for consistent comparison.
        /// </summary>
        private static string NormalizeUserIdForComparison(string userId)
        {
            if (string.IsNullOrEmpty(userId))
                return userId;

            if (Guid.TryParse(userId, out var guid))
            {
                return guid.ToString("N");
            }

            return userId;
        }

        /// <summary>
        /// Determines if a playlist should be refreshed based on the change type and auto-refresh mode.
        /// </summary>
        /// <param name="playlist">The playlist to check</param>
        /// <param name="changeType">The type of change that occurred</param>
        /// <param name="triggeringUserId">The user ID if this is a user data change, null for library changes</param>
        /// <returns>True if the playlist should be refreshed</returns>
        private static bool ShouldPlaylistRefreshForChangeType(SmartPlaylistDto playlist, LibraryChangeType changeType, Guid? triggeringUserId)
        {
            return changeType switch
            {
                LibraryChangeType.Added =>
                    // Added events trigger OnLibraryChanges playlists (items being added to library)
                    playlist.AutoRefresh >= AutoRefreshMode.OnLibraryChanges,

                LibraryChangeType.Removed =>
                    // Removed events never trigger refreshes - Jellyfin automatically removes items from playlists
                    // No playlist refresh needed regardless of AutoRefreshMode setting
                    false,

                LibraryChangeType.Updated =>
                    // Updated events (metadata or user data changes) only trigger OnAllChanges playlists
                    // OnLibraryChanges is specifically for "when items are added", not for updates to existing items
                    playlist.AutoRefresh >= AutoRefreshMode.OnAllChanges,

                _ => false,
            };
        }

        private async Task<List<string>> FilterPlaylistsByChangeTypeAsync(List<string> playlistIds, LibraryChangeType changeType, Guid? triggeringUserId)
        {
            var filteredPlaylists = new List<string>();

            foreach (var playlistId in playlistIds)
            {
                try
                {
                    // Parse playlist ID and fetch playlist data
                    if (!Guid.TryParse(playlistId, out var playlistGuid))
                    {
                        _logger.LogWarning("Invalid playlist ID format '{PlaylistId}' - skipping", playlistId);
                        continue;
                    }

                    var playlist = await _playlistStore.GetByIdAsync(playlistGuid).ConfigureAwait(false);
                    if (playlist == null)
                    {
                        _logger.LogDebug("Playlist {PlaylistId} not found - skipping", playlistId);
                        continue;
                    }

                    if (!playlist.Enabled)
                    {
                        _logger.LogDebug("Playlist '{PlaylistName}' is disabled - skipping", playlist.Name);
                        continue;
                    }

                    // Check if this playlist should be refreshed based on the change type
                    if (!ShouldPlaylistRefreshForChangeType(playlist, changeType, triggeringUserId))
                    {
                        _logger.LogDebug("Playlist '{PlaylistName}' auto-refresh mode {AutoRefresh} does not match change type {ChangeType} - skipping",
                            playlist.Name, playlist.AutoRefresh, changeType);
                        continue;
                    }

                    // User-specific filtering for UserData events (playback status changes)
                    if (triggeringUserId.HasValue && !IsUserRelevantToPlaylist(triggeringUserId.Value, playlist))
                    {
                        _logger.LogDebug("Playlist '{PlaylistName}' is not relevant to user {UserId} - skipping",
                            playlist.Name, triggeringUserId.Value);
                        continue;
                    }

                    filteredPlaylists.Add(playlistId);
                    _logger.LogDebug("Playlist '{PlaylistName}' will be refreshed for {ChangeType} change",
                        playlist.Name, changeType);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing playlist {PlaylistId} for change type filtering - skipping this playlist", playlistId);
                    // Continue processing other playlists instead of failing the entire operation
                    continue;
                }
            }

            return filteredPlaylists;
        }

        private static string GetMediaTypeFromItem(BaseItem item) => GetMediaTypeForItem(item);

        private static bool IsItemRelevantToPlaylist(BaseItem item, SmartPlaylistDto playlist)
        {
            // If no media types specified, assume all types are relevant
            if (playlist.MediaTypes == null || !playlist.MediaTypes.Any())
                return true;

            // Check if the item's type matches any of the playlist's media types
            var itemMediaType = GetMediaTypeForItem(item);
            return playlist.MediaTypes.Contains(itemMediaType);
        }

        private static bool ShouldCollectionRefreshForChangeType(SmartCollectionDto collection, LibraryChangeType changeType)
        {
            return changeType switch
            {
                LibraryChangeType.Added =>
                    // Added events trigger OnLibraryChanges collections (items being added to library)
                    collection.AutoRefresh >= AutoRefreshMode.OnLibraryChanges,

                LibraryChangeType.Removed =>
                    // Removed events never trigger refreshes - Jellyfin automatically removes items from collections
                    // No collection refresh needed regardless of AutoRefreshMode setting
                    false,

                LibraryChangeType.Updated =>
                    // Updated events (metadata changes) only trigger OnAllChanges collections
                    // OnLibraryChanges is specifically for "when items are added", not for updates to existing items
                    // This matches playlist semantics for consistency
                    collection.AutoRefresh >= AutoRefreshMode.OnAllChanges,

                _ => false
            };
        }

        private async Task<List<string>> FilterCollectionsByChangeTypeAsync(List<string> collectionIds, LibraryChangeType changeType)
        {
            var filteredCollections = new List<string>();

            foreach (var collectionId in collectionIds)
            {
                try
                {
                    if (!Guid.TryParse(collectionId, out var collectionGuid))
                    {
                        continue;
                    }

                    var collection = await _collectionStore.GetByIdAsync(collectionGuid).ConfigureAwait(false);
                    if (collection == null || !collection.Enabled)
                    {
                        continue;
                    }

                    if (!ShouldCollectionRefreshForChangeType(collection, changeType))
                    {
                        _logger.LogDebug("Collection '{CollectionName}' auto-refresh mode {AutoRefresh} does not match change type {ChangeType} - skipping",
                            collection.Name, collection.AutoRefresh, changeType);
                        continue;
                    }

                    filteredCollections.Add(collectionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error filtering collection {CollectionId} by change type", collectionId);
                }
            }

            return filteredCollections;
        }

        private static bool IsItemRelevantToCollection(BaseItem item, SmartCollectionDto collection)
        {
            // If no media types specified, assume all types are relevant
            if (collection.MediaTypes == null || !collection.MediaTypes.Any())
                return true;

            // Check if the item's type matches any of the collection's media types
            var itemMediaType = GetMediaTypeForItem(item);
            return collection.MediaTypes.Contains(itemMediaType);
        }

        private static string GetMediaTypeForItem(BaseItem item)
        {
            // First try direct type matching for common types with concrete classes
            var directMatch = item switch
            {
                MediaBrowser.Controller.Entities.Movies.Movie => MediaTypes.Movie,
                MediaBrowser.Controller.Entities.TV.Series => MediaTypes.Series,
                MediaBrowser.Controller.Entities.TV.Episode => MediaTypes.Episode,
                MediaBrowser.Controller.Entities.Audio.Audio => MediaTypes.Audio,
                MediaBrowser.Controller.Entities.MusicVideo => MediaTypes.MusicVideo,
                MediaBrowser.Controller.Entities.Video => MediaTypes.Video,
                MediaBrowser.Controller.Entities.Photo => MediaTypes.Photo,
                MediaBrowser.Controller.Entities.Book => MediaTypes.Book,
                _ => null,
            };

            if (directMatch != null)
            {
                return directMatch;
            }

            // Fallback to BaseItemKind mapping for types without concrete C# classes (e.g., AudioBook)
            if (MediaTypes.BaseItemKindToMediaType.TryGetValue(item.GetBaseItemKind(), out var mappedType))
            {
                return mappedType;
            }

            return MediaTypes.Unknown;
        }



        private async Task ProcessListRefreshes(List<string> listIds)
        {
            if (_disposed) return;

            try
            {
                // Separate playlists and collections
                var playlistIds = new List<string>();
                var collectionIds = new List<string>();

                foreach (var listId in listIds)
                {
                    try
                    {
                        // Try to determine type by checking stores
                        var playlist = await _playlistStore.GetByIdAsync(Guid.Parse(listId));
                        if (playlist != null)
                        {
                            playlistIds.Add(listId);
                        }
                        else
                        {
                            var collection = await _collectionStore.GetByIdAsync(Guid.Parse(listId));
                            if (collection != null)
                            {
                                collectionIds.Add(listId);
                            }
                        }
                    }
                    catch
                    {
                        // Skip invalid IDs
                    }
                }

                // Process playlists
                if (playlistIds.Any())
                {
                    await ProcessPlaylistRefreshes(playlistIds).ConfigureAwait(false);
                }

                // Process collections
                if (collectionIds.Any())
                {
                    await ProcessCollectionRefreshes(collectionIds).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing list refreshes");
            }
        }

        private async Task ProcessPlaylistRefreshes(List<string> playlistIds)
        {
            if (_disposed) return;

            try
            {
                _logger.LogInformation("Processing {PlaylistCount} smart playlists for auto-refresh", playlistIds.Count);

                // Enqueue all playlists for background processing
                var enqueuedCount = 0;
                foreach (var playlistId in playlistIds)
                {
                    try
                    {
                        var playlist = await _playlistStore.GetByIdAsync(Guid.Parse(playlistId));
                        if (playlist != null)
                        {
                            var listId = string.IsNullOrEmpty(playlist.Id) ? Guid.NewGuid().ToString() : playlist.Id;
                            
                            var queueItem = new RefreshQueueItem
                            {
                                ListId = listId,
                                ListName = playlist.Name,
                                ListType = Core.Enums.SmartListType.Playlist,
                                OperationType = RefreshOperationType.Refresh,
                                ListData = playlist,
                                UserId = playlist.UserId,
                                TriggerType = Core.Enums.RefreshTriggerType.Auto
                            };
                            
                            _refreshQueueService.EnqueueOperation(queueItem);
                            enqueuedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error enqueuing playlist {PlaylistId} for auto-refresh", playlistId);
                    }
                }

                _logger.LogInformation("Enqueued {EnqueuedCount} playlists for auto-refresh", enqueuedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing playlist refreshes");
            }
        }

        private async Task ProcessCollectionRefreshes(List<string> collectionIds)
        {
            if (_disposed) return;

            try
            {
                _logger.LogInformation("Processing {CollectionCount} smart collections for auto-refresh", collectionIds.Count);

                var enqueuedCount = 0;
                foreach (var collectionId in collectionIds)
                {
                    try
                    {
                        var collection = await _collectionStore.GetByIdAsync(Guid.Parse(collectionId));
                        if (collection != null && collection.Enabled)
                        {
                            var listId = string.IsNullOrEmpty(collection.Id) ? Guid.NewGuid().ToString() : collection.Id;
                            
                            var queueItem = new RefreshQueueItem
                            {
                                ListId = listId,
                                ListName = collection.Name,
                                ListType = Core.Enums.SmartListType.Collection,
                                OperationType = RefreshOperationType.Refresh,
                                ListData = collection,
                                UserId = collection.UserId,
                                TriggerType = Core.Enums.RefreshTriggerType.Auto
                            };

                            _refreshQueueService.EnqueueOperation(queueItem);
                            enqueuedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing collection {CollectionId} for auto-refresh", collectionId);
                    }
                }

                _logger.LogInformation("Enqueued {EnqueuedCount} collections for auto-refresh", enqueuedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing collection refreshes");
            }
        }

        // Initialize or restart the schedule timer
        private void InitializeScheduleTimer()
        {
            try
            {
                // Dispose existing timer if it exists
                _scheduleTimer?.Dispose();

                var now = DateTime.UtcNow;
                var nextQuarterHour = CalculateNextQuarterHour(now);
                var delayToNextQuarter = nextQuarterHour - now;

                _logger.LogDebug("Schedule timer: Current time {Now}, next check at {NextCheck}, delay {Delay}",
                    now, nextQuarterHour, delayToNextQuarter);

                // Use a one-shot timer that reschedules itself after each execution
                _scheduleTimer = new Timer(CheckScheduledRefreshes, null, delayToNextQuarter, Timeout.InfiniteTimeSpan);

                _logger.LogInformation("Schedule timer initialized successfully - will check every 15 minutes starting at {NextCheck}", nextQuarterHour);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize schedule timer");
            }
        }

        // Public method to restart the schedule timer (useful for debugging or manual restart)
        public void RestartScheduleTimer()
        {
            _logger.LogInformation("Restarting schedule timer...");
            InitializeScheduleTimer();
        }

        // Public method to check if the schedule timer is running
        public bool IsScheduleTimerRunning()
        {
            return _scheduleTimer != null && !_disposed;
        }

        // Public method to get the next scheduled check time
        public DateTime? GetNextScheduledCheckTime()
        {
            if (_scheduleTimer == null || _disposed)
                return null;

            var now = DateTime.UtcNow;
            return CalculateNextQuarterHour(now);
        }


        // Helper method to calculate next 15-minute boundary
        private static DateTime CalculateNextQuarterHour(DateTime now)
        {
            // Calculate the next 15-minute boundary (0, 15, 30, 45)
            var currentMinute = now.Minute;
            var nextQuarterMinute = ((currentMinute / 15) + 1) * 15;

            // Start with current time truncated to the hour, preserving the DateTimeKind
            var baseTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Kind);

            // Add the calculated minutes - DateTime.AddMinutes handles all rollovers automatically
            var result = baseTime.AddMinutes(nextQuarterMinute);

            // If we're exactly on a quarter hour boundary, add a small buffer (5 seconds)
            // to ensure we don't miss the boundary due to processing delays
            if (currentMinute % 15 == 0 && now.Second < 5)
            {
                result = result.AddSeconds(5);
            }

            return result;
        }

        // Schedule checking methods
        private async void CheckScheduledRefreshes(object? state)
        {
            if (_disposed) return;

            try
            {
                var now = DateTime.UtcNow;
                _logger.LogDebug("Checking for scheduled list refreshes (15-minute boundary check) at {CurrentTime}", now);

                // Check playlists
                var allPlaylists = await _playlistStore.GetAllAsync().ConfigureAwait(false);
                var scheduledPlaylists = allPlaylists.Where(p =>
                    p.Enabled &&
                        p.Schedules != null && p.Schedules.Any(s => s?.Trigger != null)
                    ).ToList();

                // Check collections
                var allCollections = await _collectionStore.GetAllAsync().ConfigureAwait(false);
                var scheduledCollections = allCollections.Where(c =>
                    c.Enabled &&
                        c.Schedules != null && c.Schedules.Any(s => s?.Trigger != null)
                    ).ToList();

                // Check visibility schedules first (independent of refresh schedules)
                await CheckVisibilitySchedules(now).ConfigureAwait(false);

                if (!scheduledPlaylists.Any() && !scheduledCollections.Any())
                {
                    _logger.LogDebug("No lists with custom schedules found");
                    RescheduleTimer();
                    return;
                }

                if (scheduledPlaylists.Any())
                {
                    _logger.LogDebug("Found {Count} playlists with schedules: {PlaylistNames}",
                        scheduledPlaylists.Count,
                        string.Join(", ", scheduledPlaylists.Select(p =>
                        {
                            var triggers = string.Join(", ", p.Schedules.Where(s => s?.Trigger != null).Select(s => s.Trigger.ToString()));
                            return $"'{p.Name}' ({triggers})";
                        })));
                }

                if (scheduledCollections.Any())
                {
                    _logger.LogDebug("Found {Count} collections with schedules: {CollectionNames}",
                        scheduledCollections.Count,
                        string.Join(", ", scheduledCollections.Select(c =>
                        {
                            var triggers = string.Join(", ", c.Schedules.Where(s => s?.Trigger != null).Select(s => s.Trigger.ToString()));
                            return $"'{c.Name}' ({triggers})";
                        })));
                }

                var duePlaylists = scheduledPlaylists.Where(p => IsPlaylistDueForRefresh(p, now)).ToList();
                var dueCollections = scheduledCollections.Where(c => IsCollectionDueForRefresh(c, now)).ToList();

                if (duePlaylists.Any() || dueCollections.Any())
                {
                    if (duePlaylists.Any())
                    {
                        _logger.LogInformation("Found {Count} playlists due for scheduled refresh: {PlaylistNames}",
                            duePlaylists.Count,
                            string.Join(", ", duePlaylists.Select(p => $"'{p.Name}'")));
                        await RefreshScheduledPlaylists(duePlaylists).ConfigureAwait(false);
                    }

                    if (dueCollections.Any())
                    {
                        _logger.LogInformation("Found {Count} collections due for scheduled refresh: {CollectionNames}",
                            dueCollections.Count,
                            string.Join(", ", dueCollections.Select(c => $"'{c.Name}'")));
                        await RefreshScheduledCollections(dueCollections).ConfigureAwait(false);
                    }
                }
                else
                {
                    _logger.LogDebug("No lists are due for scheduled refresh at {CurrentTime}", now);
                }

                // Reschedule the timer for the next quarter-hour boundary
                RescheduleTimer();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking scheduled refreshes");
                // Still reschedule even if there was an error
                RescheduleTimer();
            }
        }

        // Reschedule the timer for the next quarter-hour boundary
        private void RescheduleTimer()
        {
            if (_disposed || _scheduleTimer == null) return;

            try
            {
                var now = DateTime.UtcNow;
                var nextQuarterHour = CalculateNextQuarterHour(now);
                var delayToNextQuarter = nextQuarterHour - now;

                _logger.LogDebug("Rescheduling timer: Current time {Now}, next check at {NextCheck}, delay {Delay}",
                    now, nextQuarterHour, delayToNextQuarter);

                _scheduleTimer.Change(delayToNextQuarter, Timeout.InfiniteTimeSpan);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reschedule timer");
            }
        }

        /// <summary>
        /// Helper method to check if the current time is within a 2-minute buffer of a target time.
        /// </summary>
        /// <param name="now">Current time</param>
        /// <param name="targetTime">Target time to check against</param>
        /// <param name="bufferSeconds">Buffer in seconds (default 120 = 2 minutes)</param>
        /// <returns>True if within buffer, false otherwise</returns>
        private static bool IsWithinTimeBuffer(DateTime now, DateTime targetTime, int bufferSeconds = 120)
        {
            var timeDifference = Math.Abs((now - targetTime).TotalSeconds);
            return timeDifference <= bufferSeconds;
        }

        /// <summary>
        /// Helper method to check if the current time is within a buffer of any interval boundary.
        /// </summary>
        /// <param name="now">Current time</param>
        /// <param name="intervalMinutes">Interval in minutes (e.g., 15 for 15-minute intervals)</param>
        /// <param name="bufferSeconds">Buffer in seconds (default 120 = 2 minutes)</param>
        /// <returns>True if within buffer of any interval boundary</returns>
        private static bool IsWithinIntervalBuffer(DateTime now, int intervalMinutes, int bufferSeconds = 120)
        {
            if (intervalMinutes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(intervalMinutes),
                    $"Interval minutes must be positive, got: {intervalMinutes}");
            }

            var currentMinutesFromMidnight = now.Hour * 60 + now.Minute;
            var currentSecondsFromMidnight = currentMinutesFromMidnight * 60 + now.Second;

            // Find the nearest interval boundary
            var intervalBoundaryMinutes = (currentMinutesFromMidnight / intervalMinutes) * intervalMinutes;
            var nextIntervalBoundaryMinutes = intervalBoundaryMinutes + intervalMinutes;

            var secondsFromLastBoundary = currentSecondsFromMidnight - (intervalBoundaryMinutes * 60);
            var secondsToNextBoundary = (nextIntervalBoundaryMinutes * 60) - currentSecondsFromMidnight;

            return secondsFromLastBoundary <= bufferSeconds || secondsToNextBoundary <= bufferSeconds;
        }

        private bool IsPlaylistDueForRefresh(SmartPlaylistDto playlist, DateTime now)
        {
            // Check if playlist has any schedules configured
            var validSchedules = playlist.Schedules?.Where(s => s?.Trigger != null).ToList();
            if (validSchedules is { Count: > 0 })
            {
                // Check if ANY schedule is due (OR logic across schedules)
                foreach (var schedule in validSchedules)
                {
                    if (IsScheduleDue(schedule, now, playlist.Name))
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }

        private bool IsCollectionDueForRefresh(SmartCollectionDto collection, DateTime now)
        {
            // Check if collection has any schedules configured
            var validSchedules = collection.Schedules?.Where(s => s?.Trigger != null).ToList();
            if (validSchedules is { Count: > 0 })
            {
                foreach (var schedule in validSchedules)
                {
                    if (IsScheduleDue(schedule, now, collection.Name))
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }

        private bool IsScheduleDue(Schedule schedule, DateTime now, string playlistName)
        {
            // Defensive null check
            if (schedule?.Trigger == null)
            {
                _logger.LogWarning("Schedule for playlist '{PlaylistName}' has null Trigger, skipping", playlistName);
                return false;
            }

            return schedule.Trigger switch
            {
                ScheduleTrigger.Daily => IsDailyDue(schedule, now, playlistName),
                ScheduleTrigger.Weekly => IsWeeklyDue(schedule, now, playlistName),
                ScheduleTrigger.Monthly => IsMonthlyDue(schedule, now, playlistName),
                ScheduleTrigger.Yearly => IsYearlyDue(schedule, now, playlistName),
                ScheduleTrigger.Interval => IsIntervalDue(schedule, now, playlistName),
                _ => false,
            };
        }

        // Schedule checking methods for Schedule objects

        private bool IsDailyDue(Schedule schedule, DateTime now, string playlistName)
        {
            if (schedule.Time == null)
            {
                _logger.LogWarning("Daily schedule for '{PlaylistName}' is missing required Time property. Schedule will be skipped.", playlistName);
                return false;
            }
            
            var scheduledTime = schedule.Time.Value;
            var localNow = now.ToLocalTime();

            var todayScheduled = new DateTime(localNow.Year, localNow.Month, localNow.Day, scheduledTime.Hours, scheduledTime.Minutes, 0, DateTimeKind.Local);
            if (IsWithinTimeBuffer(localNow, todayScheduled))
            {
                _logger.LogDebug("Daily schedule check for '{PlaylistName}': Now={Now:HH:mm:ss} (local), Scheduled={Scheduled:hh\\:mm} (today), Due=True",
                    playlistName, localNow, scheduledTime);
                return true;
            }

            var tomorrowScheduled = todayScheduled.AddDays(1);
            var isDue = IsWithinTimeBuffer(localNow, tomorrowScheduled);

            _logger.LogDebug("Daily schedule check for '{PlaylistName}': Now={Now:HH:mm:ss} (local), Scheduled={Scheduled:hh\\:mm} (checked today and tomorrow), Due={Due}",
                playlistName, localNow, scheduledTime, isDue);

            return isDue;
        }

        private bool IsWeeklyDue(Schedule schedule, DateTime now, string playlistName)
        {
            if (schedule.DayOfWeek == null)
            {
                _logger.LogWarning("Weekly schedule for '{PlaylistName}' is missing required DayOfWeek property. Schedule will be skipped.", playlistName);
                return false;
            }
            
            if (schedule.Time == null)
            {
                _logger.LogWarning("Weekly schedule for '{PlaylistName}' is missing required Time property. Schedule will be skipped.", playlistName);
                return false;
            }
            
            var scheduledDay = schedule.DayOfWeek.Value;
            var scheduledTime = schedule.Time.Value;
            var localNow = now.ToLocalTime();

            if (localNow.DayOfWeek != scheduledDay)
            {
                return false;
            }

            var scheduledDateTime = new DateTime(localNow.Year, localNow.Month, localNow.Day, scheduledTime.Hours, scheduledTime.Minutes, 0, DateTimeKind.Local);
            var isDue = IsWithinTimeBuffer(localNow, scheduledDateTime);

            _logger.LogDebug("Weekly schedule check for '{PlaylistName}': Now={Now:dddd HH:mm:ss} (local), Scheduled={ScheduledDay} {Scheduled:hh\\:mm}, Due={Due}",
                playlistName, localNow, scheduledDay, scheduledTime, isDue);

            return isDue;
        }

        private bool IsMonthlyDue(Schedule schedule, DateTime now, string playlistName)
        {
            if (schedule.DayOfMonth == null)
            {
                _logger.LogWarning("Monthly schedule for '{PlaylistName}' is missing required DayOfMonth property. Schedule will be skipped.", playlistName);
                return false;
            }
            
            if (schedule.Time == null)
            {
                _logger.LogWarning("Monthly schedule for '{PlaylistName}' is missing required Time property. Schedule will be skipped.", playlistName);
                return false;
            }
            
            var scheduledDayOfMonth = schedule.DayOfMonth.Value;
            var scheduledTime = schedule.Time.Value;
            var localNow = now.ToLocalTime();

            var daysInCurrentMonth = DateTime.DaysInMonth(localNow.Year, localNow.Month);
            var effectiveDayOfMonth = Math.Min(scheduledDayOfMonth, daysInCurrentMonth);

            if (localNow.Day != effectiveDayOfMonth)
            {
                return false;
            }

            var scheduledDateTime = new DateTime(localNow.Year, localNow.Month, effectiveDayOfMonth, scheduledTime.Hours, scheduledTime.Minutes, 0, DateTimeKind.Local);
            var isDue = IsWithinTimeBuffer(localNow, scheduledDateTime);

            _logger.LogDebug("Monthly schedule check for '{PlaylistName}': Now={Now:yyyy-MM-dd HH:mm:ss} (local), Scheduled=Day {ScheduledDay} at {Scheduled:hh\\:mm}, Due={Due}",
                playlistName, localNow, effectiveDayOfMonth, scheduledTime, isDue);

            return isDue;
        }

        private bool IsYearlyDue(Schedule schedule, DateTime now, string playlistName)
        {
            if (schedule.Month == null)
            {
                _logger.LogWarning("Yearly schedule for '{PlaylistName}' is missing required Month property. Schedule will be skipped.", playlistName);
                return false;
            }
            
            if (schedule.DayOfMonth == null)
            {
                _logger.LogWarning("Yearly schedule for '{PlaylistName}' is missing required DayOfMonth property. Schedule will be skipped.", playlistName);
                return false;
            }
            
            if (schedule.Time == null)
            {
                _logger.LogWarning("Yearly schedule for '{PlaylistName}' is missing required Time property. Schedule will be skipped.", playlistName);
                return false;
            }
            
            var scheduledMonth = Math.Clamp(schedule.Month.Value, 1, 12); // Clamp to valid month range
            var scheduledDayOfMonth = Math.Max(1, schedule.DayOfMonth.Value); // At least day 1
            var scheduledTime = schedule.Time.Value;
            var localNow = now.ToLocalTime();

            if (localNow.Month != scheduledMonth)
            {
                return false;
            }

            var daysInCurrentMonth = DateTime.DaysInMonth(localNow.Year, scheduledMonth);
            var effectiveDayOfMonth = Math.Clamp(scheduledDayOfMonth, 1, daysInCurrentMonth);

            if (localNow.Day != effectiveDayOfMonth)
            {
                return false;
            }

            var scheduledDateTime = new DateTime(localNow.Year, scheduledMonth, effectiveDayOfMonth, scheduledTime.Hours, scheduledTime.Minutes, 0, DateTimeKind.Local);
            var isDue = IsWithinTimeBuffer(localNow, scheduledDateTime);

            _logger.LogDebug("Yearly schedule check for '{PlaylistName}': Now={Now:yyyy-MM-dd HH:mm:ss} (local), Scheduled=Month {ScheduledMonth} Day {ScheduledDay} at {Scheduled:hh\\:mm}, Due={Due}",
                playlistName, localNow, scheduledMonth, effectiveDayOfMonth, scheduledTime, isDue);

            return isDue;
        }

        private bool IsIntervalDue(Schedule schedule, DateTime now, string playlistName)
        {
            if (schedule.Interval == null)
            {
                _logger.LogWarning("Interval schedule for '{PlaylistName}' is missing required Interval property. Schedule will be skipped.", playlistName);
                return false;
            }
            
            var interval = schedule.Interval.Value;
            
            // Guard against invalid intervals
            if (interval <= TimeSpan.Zero)
            {
                _logger.LogWarning("Invalid interval '{Interval}' for playlist '{PlaylistName}'. Schedule will be skipped.",
                    interval, playlistName);
                return false;
            }

            // For intervals, we use UTC time since intervals are about absolute time periods
            var totalMinutes = (int)interval.TotalMinutes;
            bool isDue;

            // For intervals >= 2 hours, check if we're at an hour boundary that aligns with the interval
            if (totalMinutes >= 120)
            {
                var intervalHours = totalMinutes / 60;
                var isHourBoundary = now.Hour % intervalHours == 0;
                isDue = isHourBoundary && IsWithinIntervalBuffer(now, 60);
            }
            else
            {
                // For shorter intervals (< 2 hours), just check if we're within the interval window
                isDue = IsWithinIntervalBuffer(now, totalMinutes);
            }

            _logger.LogDebug("Interval schedule check for '{PlaylistName}': Now={Now:HH:mm:ss}, Interval={Interval}, Due={Due}",
                playlistName, now, interval, isDue);

            return isDue;
        }

        private async Task RefreshScheduledPlaylists(List<SmartPlaylistDto> playlists)
        {
            if (!playlists.Any())
            {
                return;
            }

            _logger.LogDebug("Refreshing {PlaylistCount} scheduled playlists using advanced caching", playlists.Count);

            try
            {
                var totalCount = playlists.Count;
                var playlistIdMap = new Dictionary<string, string>(); // Map playlist ID to listId for batch updates
                var playlistLookup = playlists.ToDictionary(p => p.Id ?? string.Empty, p => p); // Map playlist ID to playlist object

                // Build playlistIdMap without starting operations yet
                foreach (var playlist in playlists)
                {
                    var listId = string.IsNullOrEmpty(playlist.Id) ? Guid.NewGuid().ToString() : playlist.Id;
                    playlistIdMap[playlist.Id ?? string.Empty] = listId;
                }

                // Create callback to start operation and update batch index as each playlist is processed
                Action<string>? batchProgressCallback = null;
                Func<string, Action<int, int>?>? createProgressCallback = null;
                Action<string, bool, TimeSpan, string>? onPlaylistComplete = null;
                if (_refreshStatusService != null)
                {
                    var currentIndex = 0;
                    batchProgressCallback = (playlistId) =>
                    {
                        currentIndex++;
                        
                        // Start operation for this specific playlist right before processing
                        if (playlistLookup.TryGetValue(playlistId, out var playlist))
                        {
                            var listId = playlistIdMap.TryGetValue(playlistId, out var mappedListId) 
                                ? mappedListId 
                                : playlistId;
                            
                            // Only start if not already started (in case of retries or duplicates)
                            if (!_refreshStatusService.HasOngoingOperation(listId))
                            {
                                _refreshStatusService.StartOperation(
                                    listId,
                                    playlist.Name,
                                    Core.Enums.SmartListType.Playlist,
                                    Core.Enums.RefreshTriggerType.Scheduled,
                                    0,
                                    batchCurrentIndex: currentIndex,
                                    batchTotalCount: totalCount);
                            }
                        }
                    };
                    
                    // Create progress callback factory that maps playlistId to listId and updates status service
                    createProgressCallback = (playlistId) =>
                    {
                        var listId = playlistIdMap.TryGetValue(playlistId, out var mappedListId) 
                            ? mappedListId 
                            : playlistId;
                        
                        return (processed, total) =>
                        {
                            _refreshStatusService.UpdateProgress(listId, processed, total);
                        };
                    };
                    
                    // Create completion callback to complete operations immediately as they finish
                    onPlaylistComplete = (playlistId, success, duration, message) =>
                    {
                        var listId = playlistIdMap.TryGetValue(playlistId, out var mappedListId) 
                            ? mappedListId 
                            : playlistId;
                        
                        _refreshStatusService.CompleteOperation(
                            listId,
                            success,
                            duration,
                            success ? null : message);
                    };
                }

                // Use the cache helper for efficient batch processing
                var results = await _playlistRefreshCache.RefreshPlaylistsWithCacheAsync(
                    playlists,
                    updateLastRefreshTime: true, // Update LastRefreshed for scheduled refreshes
                    batchProgressCallback,
                    createProgressCallback,
                    onPlaylistComplete,
                    CancellationToken.None);

                // Log individual results (operations are already completed via callback)
                foreach (var result in results)
                {

                    if (result.Success)
                    {
                        _logger.LogInformation("Successfully refreshed scheduled playlist: {PlaylistName} in {ElapsedTime}ms",
                            result.PlaylistName, result.ElapsedMilliseconds);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to refresh scheduled playlist {PlaylistName}: {Message}",
                            result.PlaylistName, result.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh scheduled playlists with caching - falling back to queueing individual playlist refreshes");

                // Fallback to individual refresh without caching
                foreach (var playlist in playlists)
                {
                    var listId = string.IsNullOrEmpty(playlist.Id) ? Guid.NewGuid().ToString() : playlist.Id;
                    try
                    {
                        _logger.LogDebug("Refreshing scheduled playlist (fallback): {PlaylistName}", playlist.Name);

                        // Enqueue scheduled refresh
                        var queueItem = new RefreshQueueItem
                        {
                            ListId = listId,
                            ListName = playlist.Name,
                            ListType = Core.Enums.SmartListType.Playlist,
                            OperationType = RefreshOperationType.Refresh,
                            ListData = playlist,
                            UserId = playlist.UserId,
                            TriggerType = Core.Enums.RefreshTriggerType.Scheduled
                        };

                        _refreshQueueService.EnqueueOperation(queueItem);
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogError(fallbackEx, "Failed to enqueue scheduled playlist {PlaylistName}", playlist.Name);
                    }
                }
            }
        }

        private Task RefreshScheduledCollections(List<SmartCollectionDto> collections)
        {
            if (!collections.Any())
            {
                return Task.CompletedTask;
            }

            _logger.LogDebug("Refreshing {CollectionCount} scheduled collections", collections.Count);

            try
            {
                var enqueuedCount = 0;

                foreach (var collection in collections)
                {
                    var listId = string.IsNullOrEmpty(collection.Id) ? Guid.NewGuid().ToString() : collection.Id;
                    try
                    {
                        _logger.LogDebug("Enqueuing scheduled collection for refresh: {CollectionName}", collection.Name);
                        
                        var queueItem = new RefreshQueueItem
                        {
                            ListId = listId,
                            ListName = collection.Name,
                            ListType = Core.Enums.SmartListType.Collection,
                            OperationType = RefreshOperationType.Refresh,
                            ListData = collection,
                            UserId = collection.UserId,
                            TriggerType = Core.Enums.RefreshTriggerType.Scheduled
                        };

                        _refreshQueueService.EnqueueOperation(queueItem);
                        enqueuedCount++;
                        _logger.LogDebug("Enqueued scheduled collection: {CollectionName}", collection.Name);
                    }
                    catch (Exception collectionEx)
                    {
                        _logger.LogError(collectionEx, "Failed to enqueue scheduled collection: {CollectionName}", collection.Name);
                    }
                }

                _logger.LogInformation("Enqueued {EnqueuedCount} scheduled collections for refresh", enqueuedCount);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh scheduled collections");
                return Task.CompletedTask;
            }
        }

        // Visibility schedule checking methods

        /// <summary>
        /// Check and process visibility schedules for all lists
        /// </summary>
        private async Task CheckVisibilitySchedules(DateTime now)
        {
            try
            {
                _logger.LogDebug("Checking visibility schedules at {CurrentTime}", now);

                // Get playlists with visibility schedules
                var allPlaylists = await _playlistStore.GetAllAsync().ConfigureAwait(false);
                var playlistsWithVisibility = allPlaylists
                    .Where(p => p.VisibilitySchedules != null && p.VisibilitySchedules.Any(s => s?.Trigger != null && s.Action != null))
                    .ToList();

                // Get collections with visibility schedules
                var allCollections = await _collectionStore.GetAllAsync().ConfigureAwait(false);
                var collectionsWithVisibility = allCollections
                    .Where(c => c.VisibilitySchedules != null && c.VisibilitySchedules.Any(s => s?.Trigger != null && s.Action != null))
                    .ToList();

                if (!playlistsWithVisibility.Any() && !collectionsWithVisibility.Any())
                {
                    _logger.LogDebug("No lists with visibility schedules found");
                    return;
                }

                if (playlistsWithVisibility.Any())
                {
                    _logger.LogDebug("Found {Count} playlists with visibility schedules: {PlaylistNames}",
                        playlistsWithVisibility.Count,
                        string.Join(", ", playlistsWithVisibility.Select(p => $"'{p.Name}'")));
                }

                if (collectionsWithVisibility.Any())
                {
                    _logger.LogDebug("Found {Count} collections with visibility schedules: {CollectionNames}",
                        collectionsWithVisibility.Count,
                        string.Join(", ", collectionsWithVisibility.Select(c => $"'{c.Name}'")));
                }

                // Process visibility schedules for playlists
                foreach (var playlist in playlistsWithVisibility)
                {
                    await ProcessPlaylistVisibilitySchedule(playlist, now).ConfigureAwait(false);
                }

                // Process visibility schedules for collections
                foreach (var collection in collectionsWithVisibility)
                {
                    await ProcessCollectionVisibilitySchedule(collection, now).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking visibility schedules");
            }
        }

        /// <summary>
        /// Process visibility schedule for a playlist with Enable-wins conflict resolution
        /// </summary>
        private async Task ProcessPlaylistVisibilitySchedule(SmartPlaylistDto playlist, DateTime now)
        {
            try
            {
                bool hasEnableAction = false;
                bool hasDisableAction = false;

                // Check all visibility schedules that are due
                foreach (var schedule in playlist.VisibilitySchedules.Where(s => s?.Trigger != null && s.Action != null))
                {
                    if (IsScheduleDue(schedule, now, playlist.Name))
                    {
                        _logger.LogDebug("Visibility schedule due for playlist '{Name}': {Trigger} → {Action}",
                            playlist.Name, schedule.Trigger, schedule.Action);

                        if (schedule.Action == ScheduleAction.Enable)
                        {
                            hasEnableAction = true;
                        }
                        else if (schedule.Action == ScheduleAction.Disable)
                        {
                            hasDisableAction = true;
                        }
                    }
                }

                // Enable takes priority over Disable
                if (hasEnableAction && !playlist.Enabled)
                {
                    _logger.LogInformation("Visibility schedule: Enabling playlist '{Name}'", playlist.Name);
                    playlist.Enabled = true;
                    await _playlistStore.SaveAsync(playlist).ConfigureAwait(false);

                    // Enqueue refresh when enabling (same as manual enable)
                    try
                    {
                        var queueItem = new RefreshQueueItem
                        {
                            ListId = playlist.Id ?? Guid.NewGuid().ToString(),
                            ListName = playlist.Name,
                            ListType = Core.Enums.SmartListType.Playlist,
                            OperationType = RefreshOperationType.Refresh,
                            ListData = playlist,
                            UserId = playlist.UserId,
                            TriggerType = Core.Enums.RefreshTriggerType.Scheduled
                        };
                        _refreshQueueService.EnqueueOperation(queueItem);
                        _logger.LogDebug("Enqueued refresh for enabled playlist '{Name}'", playlist.Name);
                    }
                    catch (Exception enqueueEx)
                    {
                        _logger.LogWarning(enqueueEx, "Failed to enqueue refresh for enabled playlist '{Name}'", playlist.Name);
                    }
                }
                else if (hasDisableAction && !hasEnableAction && playlist.Enabled)
                {
                    _logger.LogInformation("Visibility schedule: Disabling playlist '{Name}'", playlist.Name);
                    
                    // Delete all Jellyfin playlists for all users using service method
                    // Cast to PlaylistService to access the helper method
                    if (_playlistService is Jellyfin.Plugin.SmartLists.Services.Playlists.PlaylistService playlistServiceImpl)
                    {
                        await playlistServiceImpl.DeleteAllJellyfinPlaylistsForUsersAsync(playlist).ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogWarning("Unable to cast playlist service to PlaylistService implementation");
                    }
                    
                    // Clear Jellyfin IDs and disable (same as manual disable)
                    playlist.Enabled = false;
                    playlist.JellyfinPlaylistId = null;
                    if (playlist.UserPlaylists != null)
                    {
                        foreach (var userMapping in playlist.UserPlaylists)
                        {
                            userMapping.JellyfinPlaylistId = null;
                        }
                    }
                    
                    await _playlistStore.SaveAsync(playlist).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing visibility schedule for playlist '{Name}'", playlist.Name);
            }
        }

        /// <summary>
        /// Process visibility schedule for a collection with Enable-wins conflict resolution
        /// </summary>
        private async Task ProcessCollectionVisibilitySchedule(SmartCollectionDto collection, DateTime now)
        {
            try
            {
                bool hasEnableAction = false;
                bool hasDisableAction = false;

                // Check all visibility schedules that are due
                foreach (var schedule in collection.VisibilitySchedules.Where(s => s?.Trigger != null && s.Action != null))
                {
                    if (IsScheduleDue(schedule, now, collection.Name))
                    {
                        _logger.LogDebug("Visibility schedule due for collection '{Name}': {Trigger} → {Action}",
                            collection.Name, schedule.Trigger, schedule.Action);

                        if (schedule.Action == ScheduleAction.Enable)
                        {
                            hasEnableAction = true;
                        }
                        else if (schedule.Action == ScheduleAction.Disable)
                        {
                            hasDisableAction = true;
                        }
                    }
                }

                // Enable takes priority over Disable
                if (hasEnableAction && !collection.Enabled)
                {
                    _logger.LogInformation("Visibility schedule: Enabling collection '{Name}'", collection.Name);
                    collection.Enabled = true;
                    await _collectionStore.SaveAsync(collection).ConfigureAwait(false);

                    // Enqueue refresh when enabling (same as manual enable)
                    try
                    {
                        var queueItem = new RefreshQueueItem
                        {
                            ListId = collection.Id ?? Guid.NewGuid().ToString(),
                            ListName = collection.Name,
                            ListType = Core.Enums.SmartListType.Collection,
                            OperationType = RefreshOperationType.Refresh,
                            ListData = collection,
                            UserId = collection.UserId,
                            TriggerType = Core.Enums.RefreshTriggerType.Scheduled
                        };
                        _refreshQueueService.EnqueueOperation(queueItem);
                        _logger.LogDebug("Enqueued refresh for enabled collection '{Name}'", collection.Name);
                    }
                    catch (Exception enqueueEx)
                    {
                        _logger.LogWarning(enqueueEx, "Failed to enqueue refresh for enabled collection '{Name}'", collection.Name);
                    }
                }
                else if (hasDisableAction && !hasEnableAction && collection.Enabled)
                {
                    _logger.LogInformation("Visibility schedule: Disabling collection '{Name}'", collection.Name);
                    
                    // Delete Jellyfin collection before disabling
                    if (!string.IsNullOrEmpty(collection.JellyfinCollectionId))
                    {
                        try
                        {
                            await _collectionService.DeleteAsync(collection).ConfigureAwait(false);
                            _logger.LogDebug("Deleted Jellyfin collection {JellyfinCollectionId} for collection '{Name}' via visibility schedule", collection.JellyfinCollectionId, collection.Name);
                        }
                        catch (Exception deleteEx)
                        {
                            _logger.LogWarning(deleteEx, "Failed to delete Jellyfin collection for '{Name}'", collection.Name);
                        }
                    }
                    
                    // Clear Jellyfin ID and disable (same as manual disable)
                    collection.Enabled = false;
                    collection.JellyfinCollectionId = null;
                    await _collectionStore.SaveAsync(collection).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing visibility schedule for collection '{Name}'", collection.Name);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Unsubscribe from events
            _libraryManager.ItemAdded -= OnItemAdded;
            _libraryManager.ItemRemoved -= OnItemRemoved;
            _libraryManager.ItemUpdated -= OnItemUpdated;
            _userDataManager.UserDataSaved -= OnUserDataSaved;

            // Dispose timers
            _batchProcessTimer?.Dispose();
            _scheduleTimer?.Dispose();

            // Clear pending refreshes
            _pendingLibraryRefreshes.Clear();

            // Clear UserData state cache
            _userDataStateCache.Clear();

            // Clear static instance
            if (Instance == this)
                Instance = null;

            _logger.LogDebug("AutoRefreshService disposed");
        }
    }
}
