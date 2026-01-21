using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartLists.Core.Enums;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.Abstractions;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Collections
{
    /// <summary>
    /// Store implementation for smart collections
    /// Handles JSON serialization/deserialization with type discrimination
    /// </summary>
    public class CollectionStore : ISmartListStore<SmartCollectionDto>
    {
        private readonly ISmartListFileSystem _fileSystem;
        private readonly ILogger<CollectionStore>? _logger;

        public CollectionStore(
            ISmartListFileSystem fileSystem,
            ILogger<CollectionStore>? logger = null)
        {
            _fileSystem = fileSystem;
            _logger = logger;
        }

        public async Task<SmartCollectionDto?> GetByIdAsync(Guid id)
        {
            // Validate GUID format to prevent path injection
            if (id == Guid.Empty)
            {
                return null;
            }

            // Try direct file lookup first (O(1) operation)
            var filePath = _fileSystem.GetSmartListFilePath(id.ToString());
            if (!string.IsNullOrEmpty(filePath))
            {
                try
                {
                    var collection = await LoadCollectionAsync(filePath).ConfigureAwait(false);
                    if (collection != null && collection.Type == Core.Enums.SmartListType.Collection)
                    {
                        return collection;
                    }
                }
                catch (Exception ex)
                {
                    // File exists but couldn't be loaded, fall back to scanning all files
                    _logger?.LogDebug(ex, "Failed to load collection from direct path {FilePath}, falling back to scan", filePath);
                }
            }

            // Fallback: scan all collections if direct lookup failed
            // Use case-insensitive comparison to handle GUID casing differences
            var allCollections = await GetAllAsync().ConfigureAwait(false);
            return allCollections.FirstOrDefault(c => string.Equals(c.Id, id.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        public async Task<SmartCollectionDto[]> GetAllAsync()
        {
            // Use shared helper to read files once
            var (_, collections) = await _fileSystem.GetAllSmartListsAsync().ConfigureAwait(false);
            return collections;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Collection ID is validated as GUID before use in file paths, preventing path injection")]
        public async Task<SmartCollectionDto> SaveAsync(SmartCollectionDto smartCollection)
        {
            ArgumentNullException.ThrowIfNull(smartCollection);

            // Ensure type is set
            smartCollection.Type = Core.Enums.SmartListType.Collection;

            // Validate ID is a valid GUID to prevent path injection
            if (string.IsNullOrWhiteSpace(smartCollection.Id) || !Guid.TryParse(smartCollection.Id, out var parsedId) || parsedId == Guid.Empty)
            {
                throw new ArgumentException("Collection ID must be a valid non-empty GUID", nameof(smartCollection));
            }

            // Normalize ID to canonical GUID string for consistent file lookups
            smartCollection.Id = parsedId.ToString();
            smartCollection.FileName = "config.json";

            // New unified folder structure: /smartlists/{guid}/config.json
            var folderPath = _fileSystem.GetSmartListFolderPath(smartCollection.Id);
            var configPath = _fileSystem.GetSmartListConfigPath(smartCollection.Id);
            var tempPath = configPath + ".tmp";

            try
            {
                // Ensure folder exists
                Directory.CreateDirectory(folderPath);

                await using (var writer = File.Create(tempPath))
                {
                    await JsonSerializer.SerializeAsync(writer, smartCollection, SmartListFileSystem.SharedJsonOptions).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }

                if (File.Exists(configPath))
                {
                    // Replace is atomic on the same volume
                    File.Replace(tempPath, configPath, null);
                }
                else
                {
                    File.Move(tempPath, configPath);
                }

                _logger?.LogDebug("CollectionStore.SaveAsync: File written successfully to {FilePath}", configPath);

                // Clean up all legacy locations after successful save
                CleanupLegacyLocations(smartCollection.Id);
            }
            finally
            {
                // Clean up temp file if it still exists
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore cleanup errors */ }
            }

            return smartCollection;
        }

        public Task DeleteAsync(Guid id)
        {
            // Validate GUID format to prevent path injection
            if (id == Guid.Empty)
            {
                throw new ArgumentException("Collection ID cannot be empty", nameof(id));
            }

            var smartListId = id.ToString();

            // Delete the entire folder (new unified structure)
            var folderPath = _fileSystem.GetSmartListFolderPath(smartListId);
            if (Directory.Exists(folderPath))
            {
                try
                {
                    Directory.Delete(folderPath, recursive: true);
                    _logger?.LogDebug("Deleted collection folder {FolderPath}", folderPath);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete collection folder {FolderPath}", folderPath);
                }
            }

            // Also clean up legacy locations
            CleanupLegacyLocations(smartListId);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Cleans up legacy file locations after migration or deletion.
        /// </summary>
        private void CleanupLegacyLocations(string smartListId)
        {
            // Clean flat file: /smartlists/{guid}.json
            var flatPath = _fileSystem.GetSmartListPath(smartListId);
            SafeDeleteFile(flatPath);

            // Clean legacy file: /smartplaylists/{guid}.json
            var legacyPath = _fileSystem.GetLegacyPath(smartListId);
            SafeDeleteFile(legacyPath);

            // Clean legacy images folder: /smartlists/images/{guid}/
            var legacyImagesPath = Path.Combine(_fileSystem.BasePath, "images", smartListId);
            SafeDeleteDirectory(legacyImagesPath);
        }

        /// <summary>
        /// Safely deletes a file, logging any errors.
        /// </summary>
        private void SafeDeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger?.LogDebug("Deleted legacy file {FilePath}", filePath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete file {FilePath}", filePath);
            }
        }

        /// <summary>
        /// Safely deletes a directory, logging any errors.
        /// </summary>
        private void SafeDeleteDirectory(string directoryPath)
        {
            try
            {
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, recursive: true);
                    _logger?.LogDebug("Deleted legacy directory {DirectoryPath}", directoryPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete directory {DirectoryPath}", directoryPath);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File path is validated upstream - only valid GUIDs are passed to this method")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Method is part of instance interface implementation")]
        private async Task<SmartCollectionDto?> LoadCollectionAsync(string filePath)
        {
            // Read JSON content to check Type field before deserialization
            // This prevents legacy playlists (without Type field) from being misclassified as collections
            // because SmartCollectionDto constructor initializes Type to Collection
            var jsonContent = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            using var jsonDoc = JsonDocument.Parse(jsonContent);

            // Check if Type field exists in JSON
            if (!jsonDoc.RootElement.TryGetProperty("Type", out var typeElement))
            {
                // Legacy file without Type field - return null to let PlaylistStore handle it
                // (legacy files default to Playlist for backward compatibility)
                return null;
            }

            // Determine type from JSON using shared helper
            if (!SmartListFileSystem.TryGetSmartListType(typeElement, out var listType))
            {
                // Invalid type format - return null
                return null;
            }

            // Only deserialize if Type is explicitly Collection
            if (listType != Core.Enums.SmartListType.Collection)
            {
                return null;
            }

            // Now deserialize as collection since we've confirmed it's a collection
            var dto = JsonSerializer.Deserialize<SmartCollectionDto>(jsonContent, SmartListFileSystem.SharedJsonOptions);
            
            if (dto != null)
            {
                // Apply common post-processing (sets Type, migrates legacy fields)
                SmartListFileSystem.ApplyPostProcessing(dto);
            }
            
            return dto;
        }
    }
}

