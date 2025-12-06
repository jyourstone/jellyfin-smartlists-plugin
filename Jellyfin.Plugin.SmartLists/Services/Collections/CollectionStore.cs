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

        public CollectionStore(ISmartListFileSystem fileSystem, ILogger<CollectionStore>? logger = null)
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
            var fileName = smartCollection.Id;
            smartCollection.FileName = $"{fileName}.json";

            var filePath = _fileSystem.GetSmartListPath(fileName);
            var tempPath = filePath + ".tmp";

            // Check if this collection exists in the legacy directory (for migration)
            var legacyPath = _fileSystem.GetLegacyPath(fileName);
            bool existsInLegacy = File.Exists(legacyPath);

            try
            {
                await using (var writer = File.Create(tempPath))
                {
                    await JsonSerializer.SerializeAsync(writer, smartCollection, SmartListFileSystem.SharedJsonOptions).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }

                if (File.Exists(filePath))
                {
                    // Replace is atomic on the same volume
                    File.Replace(tempPath, filePath, null);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }

                // After successfully saving to new location, delete legacy file if it exists
                // This migrates the collection from old directory to new directory
                if (existsInLegacy)
                {
                    try
                    {
                        File.Delete(legacyPath);
                    }
                    catch (Exception ex)
                    {
                        // Log but don't fail the save operation if legacy deletion fails
                        // The file will be in both locations, but the new location takes precedence
                        System.Diagnostics.Debug.WriteLine($"Warning: Failed to delete legacy collection file {legacyPath}: {ex.Message}");
                    }
                }
            }
            finally
            {
                // Clean up temp file if it still exists
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore cleanup errors */ }
            }

            return smartCollection;
        }

        public async Task DeleteAsync(Guid id)
        {
            var collection = await GetByIdAsync(id).ConfigureAwait(false);
            if (collection == null)
                return;

            // Use the actual filename to construct the path
            var fileName = string.IsNullOrWhiteSpace(collection.FileName)
                ? collection.Id
                : Path.GetFileNameWithoutExtension(collection.FileName);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("Collection ID cannot be null or empty", nameof(id));
            }

            // Validate that fileName is a valid GUID before constructing paths
            // GetSmartListPath and GetLegacyPath expect a GUID to prevent path injection
            if (!Guid.TryParse(fileName, out _))
            {
                throw new ArgumentException($"Collection ID must be a valid GUID, but got: {fileName}", nameof(id));
            }

            var filePath = _fileSystem.GetSmartListPath(fileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            // Also check legacy directory
            var legacyPath = _fileSystem.GetLegacyPath(fileName);
            if (File.Exists(legacyPath))
            {
                File.Delete(legacyPath);
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

