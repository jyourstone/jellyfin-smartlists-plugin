using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.Collections;
using Jellyfin.Plugin.SmartLists.Services.Playlists;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Scheduled task that cleans up orphaned data from SmartLists.
    /// This includes orphaned folders without config.json, legacy image folders,
    /// and leftover Jellyfin playlists/collections tethered to deleted or disabled smart lists.
    /// </summary>
    public class CleanupTask : IScheduledTask
    {
        private readonly SmartListImageService _imageService;
        private readonly ISmartListFileSystem _fileSystem;
        private readonly ILibraryManager _libraryManager;
        private readonly PlaylistStore _playlistStore;
        private readonly CollectionStore _collectionStore;
        private readonly ILogger<CleanupTask> _logger;

        public CleanupTask(
            SmartListImageService imageService,
            ISmartListFileSystem fileSystem,
            ILibraryManager libraryManager,
            PlaylistStore playlistStore,
            CollectionStore collectionStore,
            ILogger<CleanupTask> logger)
        {
            _imageService = imageService;
            _fileSystem = fileSystem;
            _libraryManager = libraryManager;
            _playlistStore = playlistStore;
            _collectionStore = collectionStore;
            _logger = logger;
        }

        /// <summary>
        /// Gets the name of the task.
        /// </summary>
        public string Name => "SmartLists cleanup task";

        /// <summary>
        /// Gets the key of the task.
        /// </summary>
        public string Key => "SmartListsCleanup";

        /// <summary>
        /// Gets the description of the task.
        /// </summary>
        public string Description => "Cleans up orphaned data from SmartLists, including custom images from deleted lists and leftover Jellyfin playlists/collections from deleted or disabled smart lists.";

        /// <summary>
        /// Gets the category of the task.
        /// </summary>
        public string Category => "SmartLists";

        /// <summary>
        /// Gets the default triggers for this task.
        /// Runs weekly by default.
        /// </summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                // Run weekly on Sunday at 4:00 AM
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.WeeklyTrigger,
                    DayOfWeek = DayOfWeek.Sunday,
                    TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
                }
            };
        }

        /// <summary>
        /// Executes the task.
        /// </summary>
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting Smart Lists cleanup task");

            try
            {
                // Read the store once and hand the same snapshot to both phases below,
                // so they agree on what "exists" means even if a save lands mid-run.
                var (playlists, collections, skippedFiles) = await _fileSystem.GetAllSmartListsAsync().ConfigureAwait(false);

                // Clean up orphaned folders (folders without config.json). This phase owns
                // progress 0-80; the tether sweep that follows is comparatively quick.
                var folderProgress = new Progress<double>(p => progress.Report(p * 0.8));
                await CleanupOrphanedFoldersAsync(playlists, collections, skippedFiles, folderProgress, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                // Clean up Jellyfin items tethered to deleted or disabled smart lists
                await CleanupOrphanedTetheredItemsAsync(playlists, collections, skippedFiles, cancellationToken).ConfigureAwait(false);

                // Clean up legacy images folder if it exists
                CleanupLegacyImagesFolder();

                progress.Report(100);
                _logger.LogInformation("Smart Lists cleanup task completed");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Cleanup task was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cleanup task");
                throw;
            }
        }

        /// <summary>
        /// Cleans up orphaned folders in the smartlists directory.
        /// A folder is orphaned if:
        /// - It's a GUID folder without a config.json file
        /// - It's a GUID folder whose ID is not in the active smart lists
        /// </summary>
        private Task CleanupOrphanedFoldersAsync(SmartPlaylistDto[] playlists, SmartCollectionDto[] collections, int skippedFiles, IProgress<double> progress, CancellationToken cancellationToken)
        {
            var basePath = _fileSystem.BasePath;

            if (!Directory.Exists(basePath))
            {
                progress.Report(100);
                return Task.CompletedTask;
            }

            // Get all GUID folders in smartlists/, excluding special folders
            var guidFolders = Directory.GetDirectories(basePath)
                .Where(d =>
                {
                    var dirName = Path.GetFileName(d);
                    // Skip the backups folder - it's managed by BackupTask
                    if (dirName.Equals("backups", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    return Guid.TryParse(dirName, out _);
                })
                .ToList();

            _logger.LogDebug("Found {Count} GUID folders to check", guidFolders.Count);

            if (guidFolders.Count == 0)
            {
                progress.Report(100);
                _logger.LogInformation("No folders to clean up");
                return Task.CompletedTask;
            }

            // Get all existing smart list IDs from the snapshot handed down by ExecuteAsync
            var existingSmartListIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var playlist in playlists)
            {
                if (!string.IsNullOrEmpty(playlist.Id))
                {
                    existingSmartListIds.Add(playlist.Id);
                }
            }

            foreach (var collection in collections)
            {
                if (!string.IsNullOrEmpty(collection.Id))
                {
                    existingSmartListIds.Add(collection.Id);
                }
            }

            // A partial store read must not classify unparsed lists' folders as deleted -
            // only apply the ID-based criterion when every smart list file loaded cleanly.
            var applyIdCriterion = skippedFiles == 0;
            if (!applyIdCriterion)
            {
                _logger.LogWarning(
                    "Skipped ID-based orphaned folder cleanup: {SkippedFiles} smart list file(s) failed to load",
                    skippedFiles);
            }

            // Find and delete orphaned folders
            var orphanedCount = 0;
            var processedCount = 0;

            foreach (var folderPath in guidFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var smartListId = Path.GetFileName(folderPath);
                var configPath = Path.Combine(folderPath, "config.json");

                // Folder is orphaned if:
                // 1. No config.json exists, OR
                // 2. Smart list ID is not in active lists (only checked on a full store read)
                var isOrphaned = !File.Exists(configPath) || (applyIdCriterion && !existingSmartListIds.Contains(smartListId));

                if (isOrphaned)
                {
                    _logger.LogDebug("Cleaning up orphaned folder: {FolderPath}", folderPath);
                    try
                    {
                        Directory.Delete(folderPath, recursive: true);
                        orphanedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete orphaned folder: {FolderPath}", folderPath);
                    }
                }

                processedCount++;
                progress.Report((double)processedCount / guidFolders.Count * 100);
            }

            _logger.LogInformation(
                "Folder cleanup completed. Checked {TotalCount} folders, removed {OrphanedCount} orphaned folders",
                guidFolders.Count, orphanedCount);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Deletes Jellyfin playlists/collections that carry the plugin's provider-ID tether
        /// but whose smart list no longer exists or is disabled (leftovers from failed deletions).
        /// The enabled-list exemption is kind- and user-aware: a tethered Playlist item is only
        /// exempt if it belongs to a user still enabled on that smart list, so leftover playlists
        /// of users removed from a still-enabled list get reaped. A partial store read (some smart
        /// list files failed to load) aborts the sweep entirely, since a file that failed to parse
        /// is indistinguishable from a deleted list and treating it as deleted would be destructive.
        /// Stored Jellyfin IDs referencing a deleted item are cleared from the smart list DTOs
        /// after deletion, so this doesn't leave dangling pointers behind.
        /// </summary>
        private async Task CleanupOrphanedTetheredItemsAsync(SmartPlaylistDto[] playlists, SmartCollectionDto[] collections, int skippedFiles, CancellationToken cancellationToken)
        {
            // Items created during (or shortly before) the sweep are skipped as likely
            // mid-creation by a concurrent refresh. Jellyfin persists DateCreated as local
            // wall-clock time on some paths, so the threshold carries a 26-hour margin to be
            // timezone-proof; young leftovers are simply reaped on a later run instead.
            var recencyThreshold = DateTime.UtcNow.AddHours(-26);

            if (skippedFiles > 0)
            {
                _logger.LogWarning(
                    "Skipping tethered item cleanup: {SkippedFiles} smart list file(s) failed to load; cannot safely distinguish deleted lists",
                    skippedFiles);
                return;
            }

            var (enabledCollectionIds, enabledPlaylistUsers) = BuildTetherExemptions(playlists, collections);

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Playlist, BaseItemKind.BoxSet],
                Recursive = true,
                // DB-side narrowing; the in-memory filter below remains the fail-safe.
                HasAnyProviderId = new Dictionary<string, string> { [ProviderKeys.SmartLists] = string.Empty },
            };

            var candidates = _libraryManager.GetItemsResult(query).Items
                .Where(item => IsOrphanedTetheredItem(item, enabledCollectionIds, enabledPlaylistUsers))
                .ToList();

            if (candidates.Count == 0)
            {
                _logger.LogDebug("No orphaned tethered Jellyfin items found");
                return;
            }

            // TOCTOU guard: re-read the store immediately before deleting and re-check every
            // candidate against a fresh snapshot, so a save/enable that happened between the
            // initial read and now isn't misclassified as deleted.
            var (freshPlaylists, freshCollections, freshSkippedFiles) = await _fileSystem.GetAllSmartListsAsync().ConfigureAwait(false);
            if (freshSkippedFiles > 0)
            {
                _logger.LogWarning(
                    "Aborting tethered item cleanup: re-read found {SkippedFiles} smart list file(s) failed to load; cannot safely distinguish deleted lists",
                    freshSkippedFiles);
                return;
            }

            var (freshEnabledCollectionIds, freshEnabledPlaylistUsers) = BuildTetherExemptions(freshPlaylists, freshCollections);

            var orphans = new List<BaseItem>();
            var skippedRecentCount = 0;
            var reExemptedCount = 0;

            foreach (var candidate in candidates)
            {
                if (candidate.DateCreated >= recencyThreshold)
                {
                    // Recently created (possibly mid-sweep by a concurrent refresh) - not an orphan.
                    skippedRecentCount++;
                    continue;
                }

                if (!IsOrphanedTetheredItem(candidate, freshEnabledCollectionIds, freshEnabledPlaylistUsers))
                {
                    reExemptedCount++;
                    continue;
                }

                orphans.Add(candidate);
            }

            if (orphans.Count == 0)
            {
                _logger.LogDebug("No orphaned tethered Jellyfin items remained after re-check");
                return;
            }

            var deletedOrphanIds = new HashSet<Guid>();
            var deletedCount = 0;
            foreach (var orphan in orphans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _logger.LogWarning(
                        "Deleting orphaned {ItemKind} '{ItemName}' ({ItemId}) tethered to missing or disabled smart list {SmartListId}",
                        orphan.GetBaseItemKind(), orphan.Name, orphan.Id, orphan.GetProviderId(ProviderKeys.SmartLists));
                    _libraryManager.DeleteItem(orphan, new DeleteOptions { DeleteFileLocation = true }, true);
                    deletedCount++;
                    deletedOrphanIds.Add(orphan.Id);
                }
                catch (Exception ex)
                {
                    // Per-item catch so one failed deletion doesn't strand the rest
                    _logger.LogWarning(ex, "Failed to delete orphaned tethered item '{ItemName}' ({ItemId}), continuing", orphan.Name, orphan.Id);
                }
            }

            if (deletedOrphanIds.Count > 0)
            {
                // Every other delete path in this plugin clears stored IDs on success; leaving
                // them here would dangle pointers that Jellyfin's deterministic path-based item
                // IDs could later rebind to an unrelated item.
                await ClearDanglingStoredIdsAsync(freshPlaylists, freshCollections, deletedOrphanIds).ConfigureAwait(false);
            }

            _logger.LogInformation("Tethered item cleanup completed. Removed {DeletedCount} of {OrphanCount} orphaned items", deletedCount, orphans.Count);
            if (skippedRecentCount > 0 || reExemptedCount > 0)
            {
                _logger.LogDebug(
                    "Tethered item cleanup: skipped {SkippedRecentCount} recently-created item(s), re-exempted {ReExemptedCount} item(s) on re-check",
                    skippedRecentCount, reExemptedCount);
            }
        }

        /// <summary>
        /// Builds the kind-aware exemption structures from a smart list snapshot: enabled
        /// collection IDs, and enabled playlist IDs mapped to the set of user GUIDs still
        /// configured for that playlist (from <see cref="SmartPlaylistDto.UserPlaylists"/> plus
        /// the legacy single-user field).
        /// </summary>
        private static (HashSet<string> EnabledCollectionIds, Dictionary<string, HashSet<Guid>> EnabledPlaylistUsers) BuildTetherExemptions(
            SmartPlaylistDto[] playlists, SmartCollectionDto[] collections)
        {
            var enabledCollectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var collection in collections)
            {
                if (!string.IsNullOrEmpty(collection.Id) && collection.Enabled)
                {
                    enabledCollectionIds.Add(collection.Id);
                }
            }

            var enabledPlaylistUsers = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
            foreach (var playlist in playlists)
            {
                if (string.IsNullOrEmpty(playlist.Id) || !playlist.Enabled)
                {
                    continue;
                }

                var users = new HashSet<Guid>();

                if (playlist.UserPlaylists != null)
                {
                    foreach (var mapping in playlist.UserPlaylists)
                    {
                        if (Guid.TryParse(mapping.UserId, out var userId))
                        {
                            users.Add(userId);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(playlist.UserId) && Guid.TryParse(playlist.UserId, out var legacyUserId))
                {
                    users.Add(legacyUserId);
                }

                enabledPlaylistUsers[playlist.Id] = users;
            }

            return (enabledCollectionIds, enabledPlaylistUsers);
        }

        /// <summary>
        /// Determines whether a tethered Jellyfin item is orphaned: untethered items are never
        /// orphans; a Playlist is exempt only if its tether matches an enabled playlist and its
        /// owner is still in that playlist's user set; a BoxSet is exempt if its tether matches
        /// an enabled collection; anything else is treated as not orphaned (defensive).
        /// </summary>
        private static bool IsOrphanedTetheredItem(BaseItem item, HashSet<string> enabledCollectionIds, Dictionary<string, HashSet<Guid>> enabledPlaylistUsers)
        {
            var tether = item.GetProviderId(ProviderKeys.SmartLists);
            if (string.IsNullOrEmpty(tether))
            {
                return false;
            }

            return item switch
            {
                Playlist playlist => !(enabledPlaylistUsers.TryGetValue(tether, out var users) && users.Contains(playlist.OwnerUserId)),
                BoxSet => !enabledCollectionIds.Contains(tether),
                _ => false,
            };
        }

        /// <summary>
        /// Nulls any stored Jellyfin playlist/collection ID that references a just-deleted orphan,
        /// then persists the mutated DTOs. Per-DTO save failures are logged and skipped so one
        /// failure doesn't block clearing the rest.
        /// </summary>
        private async Task ClearDanglingStoredIdsAsync(SmartPlaylistDto[] playlists, SmartCollectionDto[] collections, HashSet<Guid> deletedOrphanIds)
        {
            foreach (var playlist in playlists)
            {
                var mutated = false;

                if (playlist.UserPlaylists != null)
                {
                    foreach (var mapping in playlist.UserPlaylists)
                    {
                        if (!string.IsNullOrEmpty(mapping.JellyfinPlaylistId)
                            && Guid.TryParse(mapping.JellyfinPlaylistId, out var mappedId)
                            && deletedOrphanIds.Contains(mappedId))
                        {
                            _logger.LogDebug(
                                "Clearing dangling JellyfinPlaylistId {JellyfinPlaylistId} for user {UserId} on smart list {SmartListId}",
                                mapping.JellyfinPlaylistId, mapping.UserId, playlist.Id);
                            mapping.JellyfinPlaylistId = null;
                            mutated = true;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(playlist.JellyfinPlaylistId)
                    && Guid.TryParse(playlist.JellyfinPlaylistId, out var legacyMappedId)
                    && deletedOrphanIds.Contains(legacyMappedId))
                {
                    _logger.LogDebug(
                        "Clearing dangling legacy JellyfinPlaylistId {JellyfinPlaylistId} on smart list {SmartListId}",
                        playlist.JellyfinPlaylistId, playlist.Id);
                    playlist.JellyfinPlaylistId = null;
                    mutated = true;
                }

                if (mutated)
                {
                    try
                    {
                        await _playlistStore.SaveAsync(playlist).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to clear dangling stored playlist ID(s) for smart list {SmartListId}", playlist.Id);
                    }
                }
            }

            foreach (var collection in collections)
            {
                if (string.IsNullOrEmpty(collection.JellyfinCollectionId)
                    || !Guid.TryParse(collection.JellyfinCollectionId, out var mappedCollectionId)
                    || !deletedOrphanIds.Contains(mappedCollectionId))
                {
                    continue;
                }

                _logger.LogDebug(
                    "Clearing dangling JellyfinCollectionId {JellyfinCollectionId} on smart list {SmartListId}",
                    collection.JellyfinCollectionId, collection.Id);
                collection.JellyfinCollectionId = null;

                try
                {
                    await _collectionStore.SaveAsync(collection).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clear dangling stored collection ID for smart list {SmartListId}", collection.Id);
                }
            }
        }

        /// <summary>
        /// Cleans up the legacy images folder if it exists and is empty or contains only system files.
        /// </summary>
        private void CleanupLegacyImagesFolder()
        {
            var legacyImagesPath = Path.Combine(_fileSystem.BasePath, "images");

            if (!Directory.Exists(legacyImagesPath))
            {
                return;
            }

            try
            {
                // Delete empty subfolders first
                foreach (var subDir in Directory.GetDirectories(legacyImagesPath))
                {
                    if (FileSystemHelper.IsDirectoryEffectivelyEmpty(subDir))
                    {
                        Directory.Delete(subDir, recursive: true);
                        _logger.LogDebug("Deleted empty legacy image folder: {FolderPath}", subDir);
                    }
                }

                // Delete the images folder if it's now effectively empty (only system files)
                if (FileSystemHelper.IsDirectoryEffectivelyEmpty(legacyImagesPath))
                {
                    Directory.Delete(legacyImagesPath, recursive: true);
                    _logger.LogInformation("Deleted empty legacy images folder: {FolderPath}", legacyImagesPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up legacy images folder: {FolderPath}", legacyImagesPath);
            }
        }
    }
}
