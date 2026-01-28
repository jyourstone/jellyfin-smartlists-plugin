using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartLists.Core.Models;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Scheduled task that creates automated backups of SmartLists.
    /// Creates timestamped ZIP archives containing all smart list configurations and images.
    /// </summary>
    public class BackupTask : IScheduledTask
    {
        private readonly ISmartListFileSystem _fileSystem;
        private readonly ILogger<BackupTask> _logger;

        public BackupTask(
            ISmartListFileSystem fileSystem,
            ILogger<BackupTask> logger)
        {
            _fileSystem = fileSystem;
            _logger = logger;
        }

        /// <summary>
        /// Gets the name of the task.
        /// </summary>
        public string Name => "SmartLists backup task";

        /// <summary>
        /// Gets the key of the task.
        /// </summary>
        public string Key => "SmartListsBackup";

        /// <summary>
        /// Gets the description of the task.
        /// </summary>
        public string Description => "Creates automated backups of all SmartLists configurations and images.";

        /// <summary>
        /// Gets the category of the task.
        /// </summary>
        public string Category => "SmartLists";

        /// <summary>
        /// Gets the default triggers for this task.
        /// Runs daily at 3:00 AM by default. Schedule can be changed in Jellyfin's Scheduled Tasks.
        /// </summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
                }
            };
        }

        /// <summary>
        /// Executes the backup task.
        /// </summary>
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;

            // Check if backups are enabled
            if (config == null || !config.BackupEnabled)
            {
                _logger.LogDebug("SmartLists backup is disabled, skipping");
                progress.Report(100);
                return;
            }

            _logger.LogInformation("Starting SmartLists backup task");

            try
            {
                progress.Report(10);

                // Get backup directory
                var backupPath = GetBackupDirectory(config);
                if (!Directory.Exists(backupPath))
                {
                    Directory.CreateDirectory(backupPath);
                    _logger.LogDebug("Created backup directory: {BackupPath}", backupPath);
                }

                progress.Report(20);

                // Create backup
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var backupFileName = $"smartlists_backup_{timestamp}.zip";
                var backupFilePath = Path.Combine(backupPath, backupFileName);

                var listCount = await CreateBackupZipAsync(backupFilePath, progress, cancellationToken).ConfigureAwait(false);

                progress.Report(80);

                // Cleanup old backups
                CleanupOldBackups(backupPath, config.BackupRetentionCount, cancellationToken);

                progress.Report(100);
                _logger.LogInformation("SmartLists backup completed: {BackupFile} ({ListCount} lists)", backupFileName, listCount);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("SmartLists backup task was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during SmartLists backup task");
                throw;
            }
        }

        /// <summary>
        /// Gets the backup directory path from configuration or default.
        /// </summary>
        private string GetBackupDirectory(Configuration.PluginConfiguration config)
        {
            if (!string.IsNullOrWhiteSpace(config.BackupCustomPath))
            {
                // Use custom path - normalize it
                var normalizedPath = Path.GetFullPath(config.BackupCustomPath);
                return normalizedPath;
            }

            // Default: {DataPath}/smartlists/backups/
            return Path.Combine(_fileSystem.BasePath, "backups");
        }

        /// <summary>
        /// Creates the backup ZIP file containing all smart lists and their images.
        /// </summary>
        /// <returns>The number of lists backed up.</returns>
        private async Task<int> CreateBackupZipAsync(string backupFilePath, IProgress<double> progress, CancellationToken cancellationToken)
        {
            var (playlists, collections) = await _fileSystem.GetAllSmartListsAsync().ConfigureAwait(false);
            var allLists = playlists.Cast<SmartListDto>().Concat(collections.Cast<SmartListDto>()).ToList();

            if (allLists.Count == 0)
            {
                _logger.LogInformation("No smart lists found to backup");
                // Create empty zip to indicate backup ran successfully
                using var emptyZip = new FileStream(backupFilePath, FileMode.Create);
                using var emptyArchive = new ZipArchive(emptyZip, ZipArchiveMode.Create, true);
                return 0;
            }

            using var zipStream = new FileStream(backupFilePath, FileMode.Create);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true);

            var processedCount = 0;
            foreach (var list in allLists)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(list.Id))
                {
                    continue;
                }

                var folderPath = _fileSystem.GetSmartListFolderPath(list.Id);

                // Add config.json
                var configPath = _fileSystem.GetSmartListConfigPath(list.Id);
                if (File.Exists(configPath))
                {
                    var entryName = $"{list.Id}/config.json";
                    var entry = archive.CreateEntry(entryName);
                    using var entryStream = entry.Open();
                    using var fileStream = File.OpenRead(configPath);
                    await fileStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
                }

                // Add any image files from the folder
                if (Directory.Exists(folderPath))
                {
                    foreach (var imagePath in Directory.GetFiles(folderPath))
                    {
                        var fileName = Path.GetFileName(imagePath);

                        // Skip config.json (already added) and temp files
                        if (fileName.Equals("config.json", StringComparison.OrdinalIgnoreCase) ||
                            fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var imageEntryName = $"{list.Id}/{fileName}";
                        var imageEntry = archive.CreateEntry(imageEntryName);
                        using var imageEntryStream = imageEntry.Open();
                        using var imageFileStream = File.OpenRead(imagePath);
                        await imageFileStream.CopyToAsync(imageEntryStream, cancellationToken).ConfigureAwait(false);
                    }
                }

                processedCount++;
                var progressPercent = 20 + (60 * processedCount / allLists.Count);
                progress.Report(progressPercent);
            }

            _logger.LogDebug("Backed up {ListCount} smart lists to {BackupFile}", allLists.Count, backupFilePath);
            return allLists.Count;
        }

        /// <summary>
        /// Deletes old backup files exceeding the retention count.
        /// </summary>
        private void CleanupOldBackups(string backupPath, int retentionCount, CancellationToken cancellationToken)
        {
            if (retentionCount <= 0)
            {
                return;
            }

            var backupFiles = Directory.GetFiles(backupPath, "smartlists_backup_*.zip")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .ToList();

            if (backupFiles.Count <= retentionCount)
            {
                return;
            }

            var filesToDelete = backupFiles.Skip(retentionCount);
            var deletedCount = 0;

            foreach (var file in filesToDelete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    file.Delete();
                    deletedCount++;
                    _logger.LogDebug("Deleted old backup: {BackupFile}", file.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete old backup: {BackupFile}", file.FullName);
                }
            }

            if (deletedCount > 0)
            {
                _logger.LogInformation("Cleaned up {DeletedCount} old backup files", deletedCount);
            }
        }
    }
}
