using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Scheduled task that cleans up orphaned data from SmartLists.
    /// This includes orphaned folders without config.json and legacy image folders.
    /// </summary>
    public class CleanupTask : IScheduledTask
    {
        private readonly SmartListImageService _imageService;
        private readonly ISmartListFileSystem _fileSystem;
        private readonly ILogger<CleanupTask> _logger;

        public CleanupTask(
            SmartListImageService imageService,
            ISmartListFileSystem fileSystem,
            ILogger<CleanupTask> logger)
        {
            _imageService = imageService;
            _fileSystem = fileSystem;
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
        public string Description => "Cleans up orphaned data from SmartLists, including custom images from deleted lists.";

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
                // Clean up orphaned folders (folders without config.json)
                await CleanupOrphanedFoldersAsync(progress, cancellationToken).ConfigureAwait(false);

                // Clean up legacy images folder if it exists
                CleanupLegacyImagesFolder();

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
        private async Task CleanupOrphanedFoldersAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var basePath = _fileSystem.BasePath;

            if (!Directory.Exists(basePath))
            {
                progress.Report(100);
                return;
            }

            // Get all GUID folders in smartlists/
            var guidFolders = Directory.GetDirectories(basePath)
                .Where(d =>
                {
                    var dirName = Path.GetFileName(d);
                    return Guid.TryParse(dirName, out _);
                })
                .ToList();

            _logger.LogDebug("Found {Count} GUID folders to check", guidFolders.Count);

            if (guidFolders.Count == 0)
            {
                progress.Report(100);
                _logger.LogInformation("No folders to clean up");
                return;
            }

            // Get all existing smart list IDs
            var (playlists, collections) = await _fileSystem.GetAllSmartListsAsync().ConfigureAwait(false);
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
                // 2. Smart list ID is not in active lists (shouldn't happen if config exists, but defensive)
                var isOrphaned = !File.Exists(configPath) || !existingSmartListIds.Contains(smartListId);

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
