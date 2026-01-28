using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Information about a backup file.
    /// </summary>
    public class BackupFileInfo
    {
        /// <summary>
        /// Gets or sets the backup filename.
        /// </summary>
        public string Filename { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets when the backup was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Gets or sets the file size in bytes.
        /// </summary>
        public long SizeBytes { get; set; }

        /// <summary>
        /// Gets or sets the number of smart lists in the backup.
        /// </summary>
        public int ListCount { get; set; }
    }

    /// <summary>
    /// Result of a backup creation operation.
    /// </summary>
    public class BackupResult
    {
        /// <summary>
        /// Gets or sets whether the backup was successful.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets the backup filename.
        /// </summary>
        public string? Filename { get; set; }

        /// <summary>
        /// Gets or sets the full file path.
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// Gets or sets the number of lists backed up.
        /// </summary>
        public int ListCount { get; set; }

        /// <summary>
        /// Gets or sets the error message if the backup failed.
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Interface for backup operations.
    /// </summary>
    public interface IBackupService
    {
        /// <summary>
        /// Gets the backup directory path.
        /// </summary>
        string GetBackupDirectory();

        /// <summary>
        /// Gets a list of all available backup files.
        /// </summary>
        List<BackupFileInfo> GetBackupFiles();

        /// <summary>
        /// Creates a new backup.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Result containing the backup file path and metadata.</returns>
        Task<BackupResult> CreateBackupAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a backup and returns it as a memory stream (for download without saving to disk).
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Tuple containing the memory stream and the number of lists backed up.</returns>
        Task<(MemoryStream Stream, int ListCount)> CreateBackupStreamAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the full path to a backup file.
        /// </summary>
        /// <param name="filename">The backup filename.</param>
        /// <returns>The full path, or null if the file doesn't exist or is invalid.</returns>
        string? GetBackupFilePath(string filename);

        /// <summary>
        /// Deletes a backup file.
        /// </summary>
        /// <param name="filename">The backup filename.</param>
        /// <returns>True if deleted successfully, false otherwise.</returns>
        bool DeleteBackup(string filename);

        /// <summary>
        /// Cleans up old backups exceeding the retention count.
        /// </summary>
        /// <param name="retentionCount">Number of backups to retain.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        void CleanupOldBackups(int retentionCount, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Service for managing SmartLists backups.
    /// </summary>
    public class BackupService : IBackupService
    {
        private readonly ISmartListFileSystem _fileSystem;
        private readonly ILogger<BackupService> _logger;

        public BackupService(
            ISmartListFileSystem fileSystem,
            ILogger<BackupService> logger)
        {
            _fileSystem = fileSystem;
            _logger = logger;
        }

        /// <inheritdoc />
        public string GetBackupDirectory()
        {
            var config = Plugin.Instance?.Configuration;
            var defaultPath = Path.Combine(_fileSystem.BasePath, "backups");

            if (config != null && !string.IsNullOrWhiteSpace(config.BackupCustomPath))
            {
                var customPath = config.BackupCustomPath.Trim();

                // Detect Windows-style paths on non-Windows systems
                if (!OperatingSystem.IsWindows() && IsWindowsStylePath(customPath))
                {
                    _logger.LogWarning(
                        "Custom backup path '{CustomPath}' appears to be a Windows path but Jellyfin is running on Linux/macOS. " +
                        "Using default backup location instead.",
                        customPath);
                    return defaultPath;
                }

                // Detect Unix-style paths on Windows
                if (OperatingSystem.IsWindows() && IsUnixStyleAbsolutePath(customPath))
                {
                    _logger.LogWarning(
                        "Custom backup path '{CustomPath}' appears to be a Unix path but Jellyfin is running on Windows. " +
                        "Using default backup location instead.",
                        customPath);
                    return defaultPath;
                }

                // If path is absolute for this OS, use it directly
                if (Path.IsPathRooted(customPath))
                {
                    return Path.GetFullPath(customPath);
                }

                // Relative paths are resolved against the SmartLists data folder
                return Path.GetFullPath(Path.Combine(_fileSystem.BasePath, customPath));
            }

            return defaultPath;
        }

        /// <inheritdoc />
        public List<BackupFileInfo> GetBackupFiles()
        {
            var backupPath = GetBackupDirectory();

            if (!Directory.Exists(backupPath))
            {
                return new List<BackupFileInfo>();
            }

            var files = Directory.GetFiles(backupPath, "smartlists_backup_*.zip")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();

            var result = new List<BackupFileInfo>();
            foreach (var f in files)
            {
                result.Add(new BackupFileInfo
                {
                    Filename = f.Name,
                    CreatedAt = f.LastWriteTime,
                    SizeBytes = f.Length,
                    ListCount = CountListsInBackup(f.FullName)
                });
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<BackupResult> CreateBackupAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var backupPath = GetBackupDirectory();
                if (!Directory.Exists(backupPath))
                {
                    Directory.CreateDirectory(backupPath);
                    _logger.LogDebug("Created backup directory: {BackupPath}", backupPath);
                }

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var backupFileName = $"smartlists_backup_{timestamp}.zip";
                var backupFilePath = Path.Combine(backupPath, backupFileName);

                var listCount = await CreateBackupZipAsync(backupFilePath, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Created backup: {BackupFile} ({ListCount} lists)", backupFileName, listCount);

                return new BackupResult
                {
                    Success = true,
                    Filename = backupFileName,
                    FilePath = backupFilePath,
                    ListCount = listCount
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating backup");
                return new BackupResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <inheritdoc />
        public async Task<(MemoryStream Stream, int ListCount)> CreateBackupStreamAsync(CancellationToken cancellationToken = default)
        {
            var (playlists, collections) = await _fileSystem.GetAllSmartListsAsync().ConfigureAwait(false);
            var allLists = playlists.Cast<SmartListDto>().Concat(collections.Cast<SmartListDto>()).ToList();

            var zipStream = new MemoryStream();

            if (allLists.Count == 0)
            {
                // Create empty zip
                using (var emptyArchive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
                {
                    // Empty archive
                }

                zipStream.Position = 0;
                return (zipStream, 0);
            }

            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
            {
                foreach (var list in allLists)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrEmpty(list.Id))
                    {
                        continue;
                    }

                    await AddSmartListToArchiveAsync(archive, list.Id, cancellationToken).ConfigureAwait(false);
                }
            }

            zipStream.Position = 0;
            return (zipStream, allLists.Count);
        }

        /// <inheritdoc />
        public string? GetBackupFilePath(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
            {
                return null;
            }

            // Validate filename format to prevent path traversal
            if (!filename.StartsWith("smartlists_backup_", StringComparison.OrdinalIgnoreCase) ||
                !filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                filename.Contains("..") ||
                filename.Contains('/') ||
                filename.Contains('\\'))
            {
                _logger.LogWarning("Invalid backup filename: {Filename}", filename);
                return null;
            }

            var backupPath = GetBackupDirectory();
            var filePath = Path.Combine(backupPath, filename);

            if (!File.Exists(filePath))
            {
                return null;
            }

            return filePath;
        }

        /// <inheritdoc />
        public bool DeleteBackup(string filename)
        {
            var filePath = GetBackupFilePath(filename);
            if (filePath == null)
            {
                return false;
            }

            try
            {
                File.Delete(filePath);
                _logger.LogInformation("Deleted backup: {Filename}", filename);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting backup: {Filename}", filename);
                return false;
            }
        }

        /// <inheritdoc />
        public void CleanupOldBackups(int retentionCount, CancellationToken cancellationToken = default)
        {
            if (retentionCount <= 0)
            {
                return;
            }

            var backupPath = GetBackupDirectory();
            if (!Directory.Exists(backupPath))
            {
                return;
            }

            var backupFiles = Directory.GetFiles(backupPath, "smartlists_backup_*.zip")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
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

        /// <summary>
        /// Counts the number of smart lists in a backup zip file.
        /// </summary>
        /// <param name="filePath">Path to the zip file.</param>
        /// <returns>The number of smart lists in the backup.</returns>
        public int CountListsInBackup(string filePath)
        {
            try
            {
                using var zipStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                return CountListsInBackupStream(zipStream);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error counting lists in backup {FilePath}", filePath);
                return -1;
            }
        }

        /// <summary>
        /// Counts the number of smart lists in a backup stream.
        /// </summary>
        /// <param name="stream">Stream containing the zip archive.</param>
        /// <returns>The number of smart lists in the backup.</returns>
        public static int CountListsInBackupStream(Stream stream)
        {
            try
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
                var configEntries = archive.Entries
                    .Where(e => e.FullName.EndsWith("/config.json", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.Equals("config.json", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                return configEntries.Count;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Creates the backup ZIP file.
        /// </summary>
        private async Task<int> CreateBackupZipAsync(string backupFilePath, CancellationToken cancellationToken)
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

            foreach (var list in allLists)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(list.Id))
                {
                    continue;
                }

                await AddSmartListToArchiveAsync(archive, list.Id, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogDebug("Backed up {ListCount} smart lists to {BackupFile}", allLists.Count, backupFilePath);
            return allLists.Count;
        }

        /// <summary>
        /// Adds a smart list and its images to the archive.
        /// </summary>
        private async Task AddSmartListToArchiveAsync(ZipArchive archive, string listId, CancellationToken cancellationToken)
        {
            var folderPath = _fileSystem.GetSmartListFolderPath(listId);

            // Add config.json
            var configPath = _fileSystem.GetSmartListConfigPath(listId);
            if (File.Exists(configPath))
            {
                var entryName = $"{listId}/config.json";
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

                    var imageEntryName = $"{listId}/{fileName}";
                    var imageEntry = archive.CreateEntry(imageEntryName);
                    using var imageEntryStream = imageEntry.Open();
                    using var imageFileStream = File.OpenRead(imagePath);
                    await imageFileStream.CopyToAsync(imageEntryStream, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static bool IsWindowsStylePath(string path)
        {
            return path.Length >= 3 &&
                   char.IsLetter(path[0]) &&
                   path[1] == ':' &&
                   (path[2] == '\\' || path[2] == '/');
        }

        private static bool IsUnixStyleAbsolutePath(string path)
        {
            return path.Length >= 1 && path[0] == '/';
        }
    }
}
