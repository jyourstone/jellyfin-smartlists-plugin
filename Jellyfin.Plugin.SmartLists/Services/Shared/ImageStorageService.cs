using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// Service for storing and retrieving custom images uploaded by users for smart lists.
    /// Images are stored in {DataPath}/smartlists/images/{list-id}/primary.{ext}
    /// </summary>
    public class ImageStorageService
    {
        private readonly ILogger _logger;
        private readonly string _imagesBasePath;

        // Image validation constants (based on Jellyfin's typical limits and web standards)
        private const long MaxFileSizeBytes = 20 * 1024 * 1024; // 20MB
        private const int MinDimension = 50; // Minimum width or height
        private const int MaxDimension = 10000; // Maximum width or height

        private static readonly string[] AllowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        private static readonly string[] AllowedMimeTypes = new[]
        {
            "image/jpeg",
            "image/png",
            "image/webp",
            "image/gif"
        };

        public ImageStorageService(string dataPath, ILogger logger)
        {
            _logger = logger;
            _imagesBasePath = Path.Combine(dataPath, "smartlists", "images");

            if (!Directory.Exists(_imagesBasePath))
            {
                Directory.CreateDirectory(_imagesBasePath);
                _logger.LogDebug("Created custom images directory: {ImagesPath}", _imagesBasePath);
            }
        }

        /// <summary>
        /// Validates an uploaded image file (size, format, dimensions).
        /// </summary>
        /// <param name="stream">The image file stream</param>
        /// <param name="fileName">The original filename</param>
        /// <param name="contentType">The MIME type</param>
        /// <returns>Tuple of (IsValid, ErrorMessage, ImageFormat)</returns>
        public (bool IsValid, string ErrorMessage, IImageFormat? Format) ValidateImage(
            Stream stream,
            string fileName,
            string? contentType)
        {
            try
            {
                // Check file size
                if (stream.Length > MaxFileSizeBytes)
                {
                    return (false, $"Image file is too large. Maximum size is {MaxFileSizeBytes / 1024 / 1024}MB.", null);
                }

                if (stream.Length == 0)
                {
                    return (false, "Image file is empty.", null);
                }

                // Check file extension
                var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
                if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
                {
                    return (false, $"Invalid image format. Allowed formats: {string.Join(", ", AllowedExtensions)}", null);
                }

                // Check MIME type if provided
                if (!string.IsNullOrEmpty(contentType) && !AllowedMimeTypes.Contains(contentType.ToLowerInvariant()))
                {
                    return (false, $"Invalid MIME type. Allowed types: {string.Join(", ", AllowedMimeTypes)}", null);
                }

                // Validate image using ImageSharp (also detects format)
                stream.Position = 0; // Reset stream position
                IImageFormat? format = null;

                try
                {
                    using (var image = Image.Load(stream))
                    {
                        // Get format from the image
                        format = image.Metadata.DecodedImageFormat;

                        // Check dimensions
                        if (image.Width < MinDimension || image.Height < MinDimension)
                        {
                            return (false, $"Image is too small. Minimum dimensions: {MinDimension}x{MinDimension}px", null);
                        }

                        if (image.Width > MaxDimension || image.Height > MaxDimension)
                        {
                            return (false, $"Image is too large. Maximum dimensions: {MaxDimension}x{MaxDimension}px", null);
                        }

                        _logger.LogDebug("Image validated successfully: {Width}x{Height}, format: {Format}",
                            image.Width, image.Height, format?.Name ?? "Unknown");
                    }

                    stream.Position = 0; // Reset for subsequent operations
                    return (true, string.Empty, format);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load image with ImageSharp");
                    return (false, "Invalid or corrupted image file.", null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating image");
                return (false, "Error validating image file.", null);
            }
        }

        /// <summary>
        /// Saves a custom image for a smart list.
        /// </summary>
        /// <param name="smartListId">The smart list ID</param>
        /// <param name="imageStream">The image file stream</param>
        /// <param name="format">The image format (detected during validation)</param>
        /// <returns>The relative path to the saved image (for storing in DTO)</returns>
        public string SaveCustomImage(string smartListId, Stream imageStream, IImageFormat format)
        {
            try
            {
                // Validate smartListId to prevent path injection
                if (string.IsNullOrWhiteSpace(smartListId) || !Guid.TryParse(smartListId, out _))
                {
                    throw new ArgumentException("Invalid smart list ID format", nameof(smartListId));
                }

                var listImageDir = GetImageDirectory(smartListId);

                // Create directory if it doesn't exist
                if (!Directory.Exists(listImageDir))
                {
                    Directory.CreateDirectory(listImageDir);
                }

                // Delete any existing custom images for this list (only one primary image allowed)
                DeleteExistingImages(listImageDir);

                // Determine file extension from format
                var extension = format.FileExtensions.First();
                var fileName = $"primary.{extension}";
                var fullPath = Path.Combine(listImageDir, fileName);

                // Save the image
                imageStream.Position = 0;
                using (var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write))
                {
                    imageStream.CopyTo(fileStream);
                }

                _logger.LogInformation("Saved custom image for list {ListId}: {FileName} ({Size} bytes)",
                    smartListId, fileName, imageStream.Length);

                // Return relative path (relative to smartlists directory)
                return Path.Combine("images", smartListId, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving custom image for list {ListId}", smartListId);
                throw;
            }
        }

        /// <summary>
        /// Gets the full path to a custom image if it exists.
        /// </summary>
        /// <param name="smartListId">The smart list ID</param>
        /// <returns>Full path to the image, or null if not found</returns>
        public string? GetCustomImagePath(string smartListId)
        {
            try
            {
                // Validate smartListId to prevent path injection
                if (string.IsNullOrWhiteSpace(smartListId) || !Guid.TryParse(smartListId, out _))
                {
                    _logger.LogWarning("Invalid smart list ID format: {ListId}", smartListId);
                    return null;
                }

                var listImageDir = GetImageDirectory(smartListId);

                if (!Directory.Exists(listImageDir))
                {
                    return null;
                }

                // Look for primary image with any allowed extension
                foreach (var ext in AllowedExtensions)
                {
                    var imagePath = Path.Combine(listImageDir, $"primary{ext}");
                    if (File.Exists(imagePath))
                    {
                        return imagePath;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting custom image path for list {ListId}", smartListId);
                return null;
            }
        }

        /// <summary>
        /// Deletes the custom image for a smart list.
        /// </summary>
        /// <param name="smartListId">The smart list ID</param>
        /// <returns>True if image was deleted, false if not found</returns>
        public bool DeleteCustomImage(string smartListId)
        {
            try
            {
                // Validate smartListId to prevent path injection
                if (string.IsNullOrWhiteSpace(smartListId) || !Guid.TryParse(smartListId, out _))
                {
                    _logger.LogWarning("Invalid smart list ID format: {ListId}", smartListId);
                    return false;
                }

                var listImageDir = GetImageDirectory(smartListId);

                if (!Directory.Exists(listImageDir))
                {
                    return false;
                }

                var deletedAny = DeleteExistingImages(listImageDir);

                // Remove empty directory
                if (deletedAny && Directory.Exists(listImageDir) && !Directory.EnumerateFileSystemEntries(listImageDir).Any())
                {
                    Directory.Delete(listImageDir);
                    _logger.LogDebug("Removed empty image directory for list {ListId}", smartListId);
                }

                return deletedAny;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting custom image for list {ListId}", smartListId);
                return false;
            }
        }

        /// <summary>
        /// Gets the image directory for a specific smart list.
        /// </summary>
        private string GetImageDirectory(string smartListId)
        {
            return Path.Combine(_imagesBasePath, smartListId);
        }

        /// <summary>
        /// Deletes all existing images in the specified directory.
        /// </summary>
        /// <returns>True if any files were deleted</returns>
        private bool DeleteExistingImages(string directory)
        {
            var deletedAny = false;

            if (!Directory.Exists(directory))
            {
                return false;
            }

            foreach (var ext in AllowedExtensions)
            {
                var imagePath = Path.Combine(directory, $"primary{ext}");
                if (File.Exists(imagePath))
                {
                    File.Delete(imagePath);
                    _logger.LogDebug("Deleted existing image: {ImagePath}", imagePath);
                    deletedAny = true;
                }
            }

            return deletedAny;
        }

        /// <summary>
        /// Gets information about image validation limits (for displaying in UI).
        /// </summary>
        public static (long MaxSizeBytes, int MinDimension, int MaxDimension, string[] AllowedFormats) GetLimits()
        {
            return (MaxFileSizeBytes, MinDimension, MaxDimension, AllowedExtensions);
        }
    }
}
