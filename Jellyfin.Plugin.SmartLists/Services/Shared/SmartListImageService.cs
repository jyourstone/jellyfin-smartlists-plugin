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
    /// Images are stored in {DataPath}/smartlists/{smartListId}/ alongside config.json.
    /// </summary>
    public class SmartListImageService
    {
        private readonly IServerApplicationPaths _applicationPaths;
        private readonly ILogger<SmartListImageService> _logger;

        /// <summary>
        /// Base path for smart list storage (unified folder for config and images).
        /// </summary>
        public string SmartListsBasePath { get; }

        /// <summary>
        /// Legacy base path for image storage (for backward compatibility during migration).
        /// </summary>
        private string LegacyImagesBasePath { get; }

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

            // New unified path: images live alongside config.json in {DataPath}/smartlists/{guid}/
            SmartListsBasePath = Path.Combine(_applicationPaths.DataPath, "smartlists");

            // Legacy path for backward compatibility during migration
            LegacyImagesBasePath = Path.Combine(_applicationPaths.DataPath, "smartlists", "images");
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
        /// Deletes the entire smart list folder, including config.json and all images.
        /// This method is intended to be called when a smart list is being permanently deleted.
        /// </summary>
        /// <param name="smartListId">The smart list ID.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task DeleteSmartListFolderAsync(string smartListId, CancellationToken cancellationToken = default)
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
                _logger.LogDebug("Deleted smart list folder for {SmartListId}", smartListId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete smart list folder for {SmartListId}", smartListId);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets the full path to a specific image for a smart list.
        /// Checks new unified location first, then legacy location for backward compatibility.
        /// </summary>
        /// <returns>The full path if the image exists, null otherwise.</returns>
        public string? GetImagePath(string smartListId, string imageType)
        {
            if (string.IsNullOrEmpty(smartListId) || string.IsNullOrEmpty(imageType))
            {
                return null;
            }

            var pattern = $"{imageType.ToLowerInvariant()}.*";

            // 1. Check new unified location: /smartlists/{guid}/
            var newPath = GetSmartListImageDirectory(smartListId);
            if (Directory.Exists(newPath))
            {
                var files = Directory.GetFiles(newPath, pattern);
                if (files.Length > 0)
                {
                    return files[0];
                }
            }

            // 2. Fallback to legacy location: /smartlists/images/{guid}/
            var legacyPath = GetLegacySmartListImageDirectory(smartListId);
            if (Directory.Exists(legacyPath))
            {
                var files = Directory.GetFiles(legacyPath, pattern);
                if (files.Length > 0)
                {
                    return files[0];
                }
            }

            return null;
        }

        /// <summary>
        /// Checks if the smart list folder exists (used for images and config).
        /// </summary>
        /// <returns>True if the folder exists.</returns>
        public bool HasImageFolder(string smartListId)
        {
            if (string.IsNullOrEmpty(smartListId))
            {
                return false;
            }

            // Check new unified location
            var newPath = GetSmartListImageDirectory(smartListId);
            if (Directory.Exists(newPath))
            {
                return true;
            }

            // Check legacy location
            var legacyPath = GetLegacySmartListImageDirectory(smartListId);
            return Directory.Exists(legacyPath);
        }

        /// <summary>
        /// Gets all images for a smart list.
        /// Checks both new and legacy locations.
        /// </summary>
        /// <returns>Dictionary of ImageType -> FileName.</returns>
        public Dictionary<string, string> GetImagesForSmartList(string smartListId)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(smartListId))
            {
                return result;
            }

            // Check new unified location first
            var newPath = GetSmartListImageDirectory(smartListId);
            if (Directory.Exists(newPath))
            {
                AddImagesFromDirectory(newPath, result);
            }

            // Also check legacy location (images might not be migrated yet)
            var legacyPath = GetLegacySmartListImageDirectory(smartListId);
            if (Directory.Exists(legacyPath))
            {
                AddImagesFromDirectory(legacyPath, result);
            }

            return result;
        }

        /// <summary>
        /// Helper to add images from a directory to the result dictionary.
        /// </summary>
        private static void AddImagesFromDirectory(string directoryPath, Dictionary<string, string> result)
        {
            foreach (var file in Directory.GetFiles(directoryPath))
            {
                var fileName = Path.GetFileName(file);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

                // Skip config.json (it's in the same folder now)
                if (fileName.Equals("config.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Map filename back to ImageType
                var imageType = ValidImageTypes.FirstOrDefault(t =>
                    t.Equals(nameWithoutExt, StringComparison.OrdinalIgnoreCase));

                if (imageType != null && !result.ContainsKey(imageType))
                {
                    result[imageType] = fileName;
                }
            }
        }

        /// <summary>
        /// Gets all smart list IDs that have images stored.
        /// Checks both new unified location and legacy location.
        /// Used by the cleanup task to find orphaned images.
        /// </summary>
        public IEnumerable<string> GetAllSmartListIdsWithImages()
        {
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Check new unified location: /smartlists/{guid}/ folders with image files
            if (Directory.Exists(SmartListsBasePath))
            {
                foreach (var dir in Directory.GetDirectories(SmartListsBasePath))
                {
                    var dirName = Path.GetFileName(dir);
                    // Must be a valid GUID
                    if (!Guid.TryParse(dirName, out _))
                    {
                        continue;
                    }

                    // Check if folder has any image files (not just config.json)
                    var hasImages = Directory.GetFiles(dir)
                        .Any(f => !Path.GetFileName(f).Equals("config.json", StringComparison.OrdinalIgnoreCase));

                    if (hasImages && seenIds.Add(dirName))
                    {
                        yield return dirName;
                    }
                }
            }

            // Check legacy location: /smartlists/images/{guid}/
            if (Directory.Exists(LegacyImagesBasePath))
            {
                foreach (var dir in Directory.GetDirectories(LegacyImagesBasePath))
                {
                    var dirName = Path.GetFileName(dir);
                    // Must be a valid GUID
                    if (!Guid.TryParse(dirName, out _))
                    {
                        continue;
                    }

                    if (seenIds.Add(dirName))
                    {
                        yield return dirName;
                    }
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
        /// Gets the directory path for a smart list (unified location for config and images).
        /// Validates that the smartListId is a valid GUID to prevent path traversal attacks.
        /// </summary>
        /// <param name="smartListId">The smart list ID (must be a valid GUID).</param>
        /// <returns>The full path to the smart list's directory.</returns>
        /// <exception cref="ArgumentException">Thrown if smartListId is not a valid GUID.</exception>
        private string GetSmartListImageDirectory(string smartListId)
        {
            // Validate GUID format to prevent path traversal attacks
            if (string.IsNullOrEmpty(smartListId) || !Guid.TryParse(smartListId, out var parsedId) || parsedId == Guid.Empty)
            {
                throw new ArgumentException("Smart list ID must be a valid non-empty GUID", nameof(smartListId));
            }

            // New unified path: /smartlists/{guid}/
            return Path.Combine(SmartListsBasePath, parsedId.ToString("D"));
        }

        /// <summary>
        /// Gets the legacy directory path for a smart list's images (for backward compatibility).
        /// </summary>
        /// <param name="smartListId">The smart list ID (must be a valid GUID).</param>
        /// <returns>The full path to the legacy image directory.</returns>
        private string GetLegacySmartListImageDirectory(string smartListId)
        {
            // Validate GUID format to prevent path traversal attacks
            if (string.IsNullOrEmpty(smartListId) || !Guid.TryParse(smartListId, out var parsedId) || parsedId == Guid.Empty)
            {
                throw new ArgumentException("Smart list ID must be a valid non-empty GUID", nameof(smartListId));
            }

            // Legacy path: /smartlists/images/{guid}/
            return Path.Combine(LegacyImagesBasePath, parsedId.ToString("D"));
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
