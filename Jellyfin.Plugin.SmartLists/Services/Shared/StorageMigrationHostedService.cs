using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Hosted service that migrates smart list storage from old formats to the new unified folder structure.
    /// Runs at startup before other services to ensure consistent storage format.
    ///
    /// Migration handles:
    /// - Flat JSON files: /smartlists/{guid}.json → /smartlists/{guid}/config.json
    /// - Legacy directory: /smartplaylists/{guid}.json → /smartlists/{guid}/config.json
    /// - Legacy images: /smartlists/images/{guid}/ → /smartlists/{guid}/
    /// </summary>
    public class StorageMigrationHostedService : IHostedService
    {
        private readonly IServerApplicationPaths _applicationPaths;
        private readonly ILogger<StorageMigrationHostedService> _logger;

        private const string MigrationMarkerFile = ".migration_v2_complete";

        public StorageMigrationHostedService(
            IServerApplicationPaths applicationPaths,
            ILogger<StorageMigrationHostedService> logger)
        {
            _applicationPaths = applicationPaths;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var smartListsBasePath = Path.Combine(_applicationPaths.DataPath, "smartlists");
            var markerPath = Path.Combine(smartListsBasePath, MigrationMarkerFile);

            // Check if migration already completed
            if (File.Exists(markerPath))
            {
                _logger.LogDebug("SmartLists storage migration already complete, skipping");
                return;
            }

            _logger.LogInformation("Starting SmartLists storage migration to unified folder structure...");

            try
            {
                // Ensure base directory exists
                if (!Directory.Exists(smartListsBasePath))
                {
                    Directory.CreateDirectory(smartListsBasePath);
                }

                var migratedConfigs = 0;
                var migratedImages = 0;

                // 1. Migrate flat JSON files from /smartlists/{guid}.json
                migratedConfigs += await MigrateFlatJsonFilesAsync(smartListsBasePath, cancellationToken).ConfigureAwait(false);

                // 2. Migrate legacy JSON files from /smartplaylists/{guid}.json
                var legacyBasePath = Path.Combine(_applicationPaths.DataPath, "smartplaylists");
                migratedConfigs += await MigrateLegacyJsonFilesAsync(legacyBasePath, smartListsBasePath, cancellationToken).ConfigureAwait(false);

                // 3. Migrate images from /smartlists/images/{guid}/ to /smartlists/{guid}/
                var legacyImagesPath = Path.Combine(smartListsBasePath, "images");
                migratedImages = await MigrateLegacyImagesAsync(legacyImagesPath, smartListsBasePath, cancellationToken).ConfigureAwait(false);

                // Write marker file on success
                await File.WriteAllTextAsync(markerPath, $"Migration completed at {DateTime.UtcNow:O}", cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("SmartLists storage migration completed successfully. Migrated {ConfigCount} configs, {ImageCount} image folders",
                    migratedConfigs, migratedImages);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("SmartLists storage migration was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                // Log error but don't throw - allow Jellyfin to continue with mixed state
                // Migration will be retried on next startup
                _logger.LogError(ex, "SmartLists storage migration failed - will retry on next startup. Some lists may be in mixed state.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Migrates flat JSON files from /smartlists/{guid}.json to /smartlists/{guid}/config.json.
        /// </summary>
        private async Task<int> MigrateFlatJsonFilesAsync(string smartListsBasePath, CancellationToken cancellationToken)
        {
            var migratedCount = 0;

            foreach (var file in Directory.GetFiles(smartListsBasePath, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileNameWithoutExtension(file);

                // Skip non-GUID files (like marker files)
                if (!Guid.TryParse(fileName, out _))
                {
                    continue;
                }

                try
                {
                    await MigrateSingleConfigFileAsync(file, smartListsBasePath, fileName, cancellationToken).ConfigureAwait(false);
                    migratedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to migrate config file {FilePath}, skipping", file);
                }
            }

            return migratedCount;
        }

        /// <summary>
        /// Migrates JSON files from legacy /smartplaylists/ directory.
        /// </summary>
        private async Task<int> MigrateLegacyJsonFilesAsync(string legacyBasePath, string smartListsBasePath, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(legacyBasePath))
            {
                return 0;
            }

            var migratedCount = 0;

            foreach (var file in Directory.GetFiles(legacyBasePath, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileNameWithoutExtension(file);

                // Skip non-GUID files
                if (!Guid.TryParse(fileName, out _))
                {
                    continue;
                }

                try
                {
                    await MigrateSingleConfigFileAsync(file, smartListsBasePath, fileName, cancellationToken).ConfigureAwait(false);
                    migratedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to migrate legacy config file {FilePath}, skipping", file);
                }
            }

            // Try to clean up empty legacy directory
            FileSystemHelper.TryDeleteEmptyDirectory(legacyBasePath, _logger);

            return migratedCount;
        }

        /// <summary>
        /// Migrates a single config file to the new folder structure.
        /// </summary>
        private async Task MigrateSingleConfigFileAsync(string sourceFile, string smartListsBasePath, string smartListId, CancellationToken cancellationToken)
        {
            var targetFolder = Path.Combine(smartListsBasePath, smartListId);
            var targetConfigPath = Path.Combine(targetFolder, "config.json");

            // Skip if already migrated
            if (File.Exists(targetConfigPath))
            {
                _logger.LogDebug("List {SmartListId} already has config.json, deleting old file", smartListId);
                FileSystemHelper.SafeDeleteFile(sourceFile, _logger);
                return;
            }

            // Create target folder
            Directory.CreateDirectory(targetFolder);

            // Copy file to new location (don't move yet - verify first)
            await CopyFileAsync(sourceFile, targetConfigPath, cancellationToken).ConfigureAwait(false);

            // Verify copy succeeded
            if (!File.Exists(targetConfigPath))
            {
                throw new IOException($"Failed to copy config file to {targetConfigPath}");
            }

            // Delete source file
            FileSystemHelper.SafeDeleteFile(sourceFile, _logger);

            _logger.LogDebug("Migrated config for list {SmartListId}", smartListId);
        }

        /// <summary>
        /// Migrates images from /smartlists/images/{guid}/ to /smartlists/{guid}/.
        /// </summary>
        private Task<int> MigrateLegacyImagesAsync(string legacyImagesPath, string smartListsBasePath, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(legacyImagesPath))
            {
                return Task.FromResult(0);
            }

            var migratedCount = 0;

            foreach (var imageDir in Directory.GetDirectories(legacyImagesPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var smartListId = Path.GetFileName(imageDir);

                // Skip non-GUID directories
                if (!Guid.TryParse(smartListId, out _))
                {
                    continue;
                }

                try
                {
                    var targetFolder = Path.Combine(smartListsBasePath, smartListId);

                    // Ensure target folder exists
                    Directory.CreateDirectory(targetFolder);

                    // Move each image file
                    foreach (var imagePath in Directory.GetFiles(imageDir))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var imageName = Path.GetFileName(imagePath);
                        var targetPath = Path.Combine(targetFolder, imageName);

                        // Skip if already exists in target
                        if (File.Exists(targetPath))
                        {
                            FileSystemHelper.SafeDeleteFile(imagePath, _logger);
                            continue;
                        }

                        // Move the file
                        File.Move(imagePath, targetPath);
                        _logger.LogDebug("Moved image {ImageName} for list {SmartListId}", imageName, smartListId);
                    }

                    // Remove empty legacy image folder
                    FileSystemHelper.TryDeleteEmptyDirectory(imageDir, _logger);

                    migratedCount++;
                    _logger.LogDebug("Migrated images for list {SmartListId}", smartListId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to migrate images for list {SmartListId}, skipping", smartListId);
                }
            }

            // Try to clean up empty legacy images directory
            FileSystemHelper.TryDeleteEmptyDirectory(legacyImagesPath, _logger);

            return Task.FromResult(migratedCount);
        }

        /// <summary>
        /// Copies a file asynchronously.
        /// </summary>
        private static async Task CopyFileAsync(string sourceFile, string targetFile, CancellationToken cancellationToken)
        {
            await using var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            await using var targetStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await sourceStream.CopyToAsync(targetStream, cancellationToken).ConfigureAwait(false);
        }
    }
}
