using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Scheduled task that cleans up orphaned data from SmartLists.
    /// This includes orphaned custom images when a smart list is deleted.
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
                // Clean up orphaned images
                await CleanupOrphanedImagesAsync(progress, cancellationToken).ConfigureAwait(false);

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

        private async Task CleanupOrphanedImagesAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            // Get all smart list IDs that have images
            var imageSmartListIds = _imageService.GetAllSmartListIdsWithImages().ToList();
            _logger.LogDebug("Found {Count} image folders to check", imageSmartListIds.Count);

            if (imageSmartListIds.Count == 0)
            {
                progress.Report(100);
                _logger.LogInformation("No orphaned images to clean up");
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

            // Find and delete orphaned image folders
            var orphanedCount = 0;
            var processedCount = 0;

            foreach (var smartListId in imageSmartListIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!existingSmartListIds.Contains(smartListId))
                {
                    _logger.LogDebug("Cleaning up orphaned images for smart list: {SmartListId}", smartListId);
                    await _imageService.DeleteAllImagesAsync(smartListId, cancellationToken).ConfigureAwait(false);
                    orphanedCount++;
                }

                processedCount++;
                progress.Report((double)processedCount / imageSmartListIds.Count * 100);
            }

            _logger.LogInformation(
                "Image cleanup completed. Checked {TotalCount} image folders, removed {OrphanedCount} orphaned folders",
                imageSmartListIds.Count, orphanedCount);
        }
    }
}
