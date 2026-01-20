using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Service for managing custom images for smart lists.
    /// Images are stored in {DataPath}/smartlists/images/{smartListId}/
    /// </summary>
    public class SmartListImageService
    {
        private readonly IServerApplicationPaths _applicationPaths;
        private readonly ILogger<SmartListImageService> _logger;

        /// <summary>
        /// Base path for image storage.
        /// </summary>
        public string ImagesBasePath { get; }

        /// <summary>
        /// Allowed image extensions (matching Jellyfin's supported formats).
        /// </summary>
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif", ".svg", ".tiff", ".tif", ".apng", ".ico"
        };

        /// <summary>
        /// Valid image types that can be uploaded.
        /// </summary>
        public static readonly HashSet<string> ValidImageTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Primary", "Backdrop", "Banner", "Thumb", "Logo", "Disc", "Art", "Box", "BoxRear", "Menu"
        };

        /// <summary>
        /// Maximum file size in bytes (20MB).
        /// </summary>
        public const long MaxFileSizeBytes = 20 * 1024 * 1024;

        public SmartListImageService(
            IServerApplicationPaths applicationPaths,
            ILogger<SmartListImageService> logger)
        {
            _applicationPaths = applicationPaths;
            _logger = logger;

            ImagesBasePath = Path.Combine(_applicationPaths.DataPath, "smartlists", "images");
        }

        /// <summary>
        /// Saves an image for a smart list.
        /// </summary>
        /// <param name="smartListId">The smart list ID.</param>
        /// <param name="imageType">The image type (Primary, Backdrop, etc.).</param>
        /// <param name="imageStream">The image data stream.</param>
        /// <param name="originalFileName">The original filename (used to determine extension).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The stored filename.</returns>
        public async Task<string> SaveImageAsync(
            string smartListId,
            string imageType,
            Stream imageStream,
            string originalFileName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(smartListId))
            {
                throw new ArgumentException("Smart list ID cannot be null or empty", nameof(smartListId));
            }

            if (!ValidImageTypes.Contains(imageType))
            {
                throw new ArgumentException($"Invalid image type: {imageType}", nameof(imageType));
            }

            var extension = Path.GetExtension(originalFileName)?.ToLowerInvariant() ?? ".jpg";
            if (!AllowedExtensions.Contains(extension))
            {
                throw new ArgumentException($"Invalid file extension: {extension}. Allowed: {string.Join(", ", AllowedExtensions)}");
            }

            // Create directory for this smart list's images
            var smartListImagePath = GetSmartListImageDirectory(smartListId);
            if (!Directory.Exists(smartListImagePath))
            {
                Directory.CreateDirectory(smartListImagePath);
            }

            // Delete any existing image of this type (different extension)
            // Note: Don't call DeleteImageAsync here as it may clean up the directory we just created
            // Instead, manually delete any existing files with the same image type
            var pattern = $"{imageType.ToLowerInvariant()}.*";
            foreach (var file in Directory.GetFiles(smartListImagePath, pattern))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(file);
                    _logger.LogDebug("Deleted existing image file: {FilePath}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete existing image file: {FilePath}", file);
                }
            }

            // Save with normalized filename: {imageType}{extension}
            var fileName = $"{imageType.ToLowerInvariant()}{extension}";
            var filePath = Path.Combine(smartListImagePath, fileName);

            await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await imageStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Saved {ImageType} image for smart list {SmartListId}: {FileName}",
                imageType, smartListId, fileName);

            return fileName;
        }

        /// <summary>
        /// Deletes a specific image for a smart list.
        /// </summary>
        public Task DeleteImageAsync(string smartListId, string imageType, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(smartListId))
            {
                return Task.CompletedTask;
            }

            var smartListImagePath = GetSmartListImageDirectory(smartListId);
            if (!Directory.Exists(smartListImagePath))
            {
                return Task.CompletedTask;
            }

            // Find and delete any file matching the image type (regardless of extension)
            var pattern = $"{imageType.ToLowerInvariant()}.*";
            foreach (var file in Directory.GetFiles(smartListImagePath, pattern))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    File.Delete(file);
                    _logger.LogDebug("Deleted image file: {FilePath}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete image file: {FilePath}", file);
                }
            }

            // Note: Don't clean up empty directory here - keep it as a marker that this smart list
            // has/had managed images. This allows RemoveOrphanedCustomImages to clean up
            // Jellyfin images on the next refresh. The folder will be deleted when the smart list itself is deleted.

            return Task.CompletedTask;
        }

        /// <summary>
        /// Deletes all images for a smart list.
        /// </summary>
        public Task DeleteAllImagesAsync(string smartListId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(smartListId))
            {
                return Task.CompletedTask;
            }

            var smartListImagePath = GetSmartListImageDirectory(smartListId);
            if (!Directory.Exists(smartListImagePath))
            {
                return Task.CompletedTask;
            }

            try
            {
                Directory.Delete(smartListImagePath, recursive: true);
                _logger.LogDebug("Deleted all images for smart list {SmartListId}", smartListId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete images directory for smart list {SmartListId}", smartListId);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets the full path to a specific image for a smart list.
        /// </summary>
        /// <returns>The full path if the image exists, null otherwise.</returns>
        public string? GetImagePath(string smartListId, string imageType)
        {
            if (string.IsNullOrEmpty(smartListId) || string.IsNullOrEmpty(imageType))
            {
                return null;
            }

            var smartListImagePath = GetSmartListImageDirectory(smartListId);
            if (!Directory.Exists(smartListImagePath))
            {
                return null;
            }

            // Find file matching the image type (regardless of extension)
            var pattern = $"{imageType.ToLowerInvariant()}.*";
            var files = Directory.GetFiles(smartListImagePath, pattern);

            return files.Length > 0 ? files[0] : null;
        }

        /// <summary>
        /// Checks if the image folder exists for a smart list.
        /// This indicates that the smart list has/had managed images through the image service.
        /// </summary>
        /// <returns>True if the image folder exists (even if empty).</returns>
        public bool HasImageFolder(string smartListId)
        {
            if (string.IsNullOrEmpty(smartListId))
            {
                return false;
            }

            var smartListImagePath = GetSmartListImageDirectory(smartListId);
            return Directory.Exists(smartListImagePath);
        }

        /// <summary>
        /// Gets all images for a smart list.
        /// </summary>
        /// <returns>Dictionary of ImageType -> FileName.</returns>
        public Dictionary<string, string> GetImagesForSmartList(string smartListId)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(smartListId))
            {
                return result;
            }

            var smartListImagePath = GetSmartListImageDirectory(smartListId);
            if (!Directory.Exists(smartListImagePath))
            {
                return result;
            }

            foreach (var file in Directory.GetFiles(smartListImagePath))
            {
                var fileName = Path.GetFileName(file);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

                // Map filename back to ImageType
                var imageType = ValidImageTypes.FirstOrDefault(t =>
                    t.Equals(nameWithoutExt, StringComparison.OrdinalIgnoreCase));

                if (imageType != null)
                {
                    result[imageType] = fileName;
                }
            }

            return result;
        }

        /// <summary>
        /// Gets all smart list IDs that have images stored.
        /// Used by the cleanup task to find orphaned images.
        /// </summary>
        public IEnumerable<string> GetAllSmartListIdsWithImages()
        {
            if (!Directory.Exists(ImagesBasePath))
            {
                yield break;
            }

            foreach (var dir in Directory.GetDirectories(ImagesBasePath))
            {
                var smartListId = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(smartListId))
                {
                    yield return smartListId;
                }
            }
        }

        /// <summary>
        /// Validates an image file before upload.
        /// </summary>
        public static (bool IsValid, string? ErrorMessage) ValidateImage(
            string fileName,
            long fileSize,
            string? contentType)
        {
            // Check file size
            if (fileSize > MaxFileSizeBytes)
            {
                return (false, $"File size ({fileSize / 1024 / 1024:F1}MB) exceeds maximum allowed ({MaxFileSizeBytes / 1024 / 1024}MB)");
            }

            if (fileSize == 0)
            {
                return (false, "File is empty");
            }

            // Check extension
            var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
            {
                return (false, $"Invalid file type. Allowed: {string.Join(", ", AllowedExtensions)}");
            }

            // Check content type if provided (matching Jellyfin's supported image MIME types)
            if (!string.IsNullOrEmpty(contentType))
            {
                var validContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "image/jpeg", "image/jpg", "image/png", "image/webp", "image/gif",
                    "image/bmp", "image/avif", "image/svg+xml", "image/tiff", "image/apng", "image/x-icon"
                };

                if (!validContentTypes.Contains(contentType))
                {
                    return (false, $"Invalid content type: {contentType}");
                }
            }

            return (true, null);
        }

        /// <summary>
        /// Gets the directory path for a smart list's images.
        /// Validates that the smartListId is a valid GUID to prevent path traversal attacks.
        /// </summary>
        /// <param name="smartListId">The smart list ID (must be a valid GUID).</param>
        /// <returns>The full path to the smart list's image directory.</returns>
        /// <exception cref="ArgumentException">Thrown if smartListId is not a valid GUID.</exception>
        private string GetSmartListImageDirectory(string smartListId)
        {
            // Validate GUID format to prevent path traversal attacks
            if (string.IsNullOrEmpty(smartListId) || !Guid.TryParse(smartListId, out var parsedId) || parsedId == Guid.Empty)
            {
                throw new ArgumentException("Smart list ID must be a valid non-empty GUID", nameof(smartListId));
            }

            // Use the parsed GUID to ensure consistent format (prevents variations like extra dashes)
            return Path.Combine(ImagesBasePath, parsedId.ToString("D"));
        }

        /// <summary>
        /// Removes empty directory if it has no files.
        /// </summary>
        private void CleanupEmptyDirectory(string directoryPath)
        {
            try
            {
                if (Directory.Exists(directoryPath) && !Directory.EnumerateFileSystemEntries(directoryPath).Any())
                {
                    Directory.Delete(directoryPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not clean up empty directory: {Path}", directoryPath);
            }
        }

        /// <summary>
        /// Gets the standard Jellyfin filename for an image type.
        /// This is a shared helper to avoid duplication between PlaylistService and CollectionService.
        /// </summary>
        /// <param name="imageType">The Jellyfin image type.</param>
        /// <param name="extension">The file extension (e.g., ".jpg").</param>
        /// <returns>The standard filename for the image type.</returns>
        public static string GetJellyfinImageFileName(MediaBrowser.Model.Entities.ImageType imageType, string extension)
        {
            return imageType switch
            {
                MediaBrowser.Model.Entities.ImageType.Primary => $"folder{extension}",
                MediaBrowser.Model.Entities.ImageType.Backdrop => $"backdrop{extension}",
                MediaBrowser.Model.Entities.ImageType.Banner => $"banner{extension}",
                MediaBrowser.Model.Entities.ImageType.Thumb => $"thumb{extension}",
                MediaBrowser.Model.Entities.ImageType.Logo => $"logo{extension}",
                MediaBrowser.Model.Entities.ImageType.Disc => $"disc{extension}",
                MediaBrowser.Model.Entities.ImageType.Art => $"clearart{extension}",
                MediaBrowser.Model.Entities.ImageType.Box => $"box{extension}",
                MediaBrowser.Model.Entities.ImageType.BoxRear => $"boxrear{extension}",
                MediaBrowser.Model.Entities.ImageType.Menu => $"menu{extension}",
                _ => $"{imageType.ToString().ToLowerInvariant()}{extension}"
            };
        }

        /// <summary>
        /// Allowed image file extensions for cleanup operations.
        /// </summary>
        public static readonly string[] ImageFileExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif", ".svg", ".tiff", ".tif", ".apng", ".ico" };

        /// <summary>
        /// Deletes an image from a Jellyfin item's folder (playlist or collection).
        /// This is called when a user explicitly deletes an image through the SmartLists API.
        /// </summary>
        /// <param name="jellyfinItem">The Jellyfin BaseItem (playlist or collection).</param>
        /// <param name="imageType">The image type to delete (e.g., "Primary", "Backdrop").</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Task representing the async operation.</returns>
        public async Task DeleteImageFromJellyfinItemAsync(
            MediaBrowser.Controller.Entities.BaseItem jellyfinItem,
            string imageType,
            CancellationToken cancellationToken = default)
        {
            if (jellyfinItem == null)
            {
                return;
            }

            try
            {
                // Parse the image type
                if (!Enum.TryParse<MediaBrowser.Model.Entities.ImageType>(imageType, ignoreCase: true, out var jellyfinImageType))
                {
                    _logger.LogWarning("Invalid image type for Jellyfin deletion: {ImageType}", imageType);
                    return;
                }

                // First, try to delete using the actual path from ImageInfos (most reliable)
                string? actualImagePath = null;
                if (jellyfinItem.ImageInfos != null)
                {
                    var existingImage = jellyfinItem.ImageInfos.FirstOrDefault(i => i.Type == jellyfinImageType);
                    if (existingImage != null && !string.IsNullOrEmpty(existingImage.Path))
                    {
                        actualImagePath = existingImage.Path;

                        // Delete the actual file
                        if (File.Exists(actualImagePath))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                File.Delete(actualImagePath);
                                _logger.LogDebug("Deleted {ImageType} image from Jellyfin item: {FilePath}", imageType, actualImagePath);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to delete image file: {FilePath}", actualImagePath);
                            }
                        }

                        // Remove from ImageInfos
                        jellyfinItem.ImageInfos = jellyfinItem.ImageInfos.Where(i => i.Type != jellyfinImageType).ToArray();
                        await jellyfinItem.UpdateToRepositoryAsync(
                            MediaBrowser.Controller.Library.ItemUpdateType.ImageUpdate,
                            cancellationToken).ConfigureAwait(false);
                        _logger.LogDebug("Removed {ImageType} from Jellyfin item ImageInfos", imageType);
                    }
                }

                // Also try to delete files with our naming convention (in case ImageInfos doesn't have the path)
                var itemPath = jellyfinItem.ContainingFolderPath;
                if (!string.IsNullOrEmpty(itemPath) && Directory.Exists(itemPath))
                {
                    foreach (var ext in ImageFileExtensions)
                    {
                        var fileName = GetJellyfinImageFileName(jellyfinImageType, ext);
                        var filePath = Path.Combine(itemPath, fileName);

                        // Skip if this is the same path we already deleted
                        if (actualImagePath != null && string.Equals(filePath, actualImagePath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (File.Exists(filePath))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                File.Delete(filePath);
                                _logger.LogDebug("Deleted {ImageType} image file with standard naming: {FilePath}", imageType, filePath);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to delete image file: {FilePath}", filePath);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete image from Jellyfin item {ItemName}", jellyfinItem.Name);
            }
        }
    }
}
