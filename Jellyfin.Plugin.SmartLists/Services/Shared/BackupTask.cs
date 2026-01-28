using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly IBackupService _backupService;
        private readonly ILogger<BackupTask> _logger;

        public BackupTask(
            IBackupService backupService,
            ILogger<BackupTask> logger)
        {
            _backupService = backupService;
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

                // Create backup using the service
                var result = await _backupService.CreateBackupAsync(cancellationToken).ConfigureAwait(false);

                if (!result.Success)
                {
                    _logger.LogError("Backup task failed: {ErrorMessage}", result.ErrorMessage);
                    throw new InvalidOperationException(result.ErrorMessage);
                }

                progress.Report(80);

                // Cleanup old backups
                _backupService.CleanupOldBackups(config.BackupRetentionCount, cancellationToken);

                progress.Report(100);
                _logger.LogInformation("SmartLists backup completed: {BackupFile} ({ListCount} lists)", result.Filename, result.ListCount);
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
    }
}
