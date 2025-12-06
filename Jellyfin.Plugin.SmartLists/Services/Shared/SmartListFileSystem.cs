using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartLists.Core.Enums;
using Jellyfin.Plugin.SmartLists.Core.Models;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Shared
{
    /// <summary>
    /// File system interface for smart list storage
    /// Supports both playlists and collections in a unified directory
    /// </summary>
    public interface ISmartListFileSystem
    {
        string BasePath { get; }
        string? GetSmartListFilePath(string smartListId);
        string[] GetAllSmartListFilePaths();
        string GetSmartListPath(string fileName);
        string GetLegacyPath(string fileName);
        Task<(SmartPlaylistDto[] Playlists, SmartCollectionDto[] Collections)> GetAllSmartListsAsync();
    }

    /// <summary>
    /// File system implementation for smart lists
    /// Uses "smartlists" directory (migrated from "smartplaylists" for backward compatibility)
    /// </summary>
    public class SmartListFileSystem : ISmartListFileSystem
    {
        /// <summary>
        /// Shared JSON serializer options used across all smart list stores
        /// Ensures consistent serialization behavior (enum handling, indentation, etc.)
        /// </summary>
        public static readonly JsonSerializerOptions SharedJsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _legacyBasePath;
        private readonly ILogger<SmartListFileSystem>? _logger;

        public SmartListFileSystem(IServerApplicationPaths serverApplicationPaths, ILogger<SmartListFileSystem>? logger = null)
        {
            _logger = logger;
            ArgumentNullException.ThrowIfNull(serverApplicationPaths);

            // New unified directory name
            BasePath = Path.Combine(serverApplicationPaths.DataPath, "smartlists");
            if (!Directory.Exists(BasePath))
            {
                Directory.CreateDirectory(BasePath);
            }

            // Legacy directory for backward compatibility
            _legacyBasePath = Path.Combine(serverApplicationPaths.DataPath, "smartplaylists");
        }

        public string BasePath { get; }

        public string? GetSmartListFilePath(string smartListId)
        {
            // Validate ID format to prevent path injection
            if (string.IsNullOrWhiteSpace(smartListId) || !Guid.TryParse(smartListId, out _))
            {
                return null;
            }

            // Check new directory first using the known flat layout for O(1) lookup
            var candidatePath = Path.Combine(BasePath, $"{smartListId}.json");
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
            
            // Fallback to recursive search in case of nested structure (shouldn't happen but defensive)
            var filePath = Directory.GetFiles(BasePath, $"{smartListId}.json", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrEmpty(filePath))
            {
                return filePath;
            }

            // Fallback to legacy directory for backward compatibility
            if (Directory.Exists(_legacyBasePath))
            {
                return Directory.GetFiles(_legacyBasePath, $"{smartListId}.json", SearchOption.AllDirectories).FirstOrDefault();
            }

            return null;
        }

        public string[] GetAllSmartListFilePaths()
        {
            var files = new System.Collections.Generic.List<string>();

            // Get files from new directory
            if (Directory.Exists(BasePath))
            {
                files.AddRange(Directory.GetFiles(BasePath, "*.json", SearchOption.AllDirectories));
            }

            // Also check legacy directory for backward compatibility
            // Filter out legacy files whose filename already exists in new directory to avoid duplicates
            if (Directory.Exists(_legacyBasePath))
            {
                var legacyFiles = Directory.GetFiles(_legacyBasePath, "*.json", SearchOption.AllDirectories);
                var newDirectoryFileNames = files.Select(f => Path.GetFileName(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                
                foreach (var legacyFile in legacyFiles)
                {
                    var legacyFileName = Path.GetFileName(legacyFile);
                    // Only add legacy file if it doesn't exist in new directory
                    if (!newDirectoryFileNames.Contains(legacyFileName))
                    {
                        files.Add(legacyFile);
                    }
                }
            }

            return files.ToArray();
        }

        public string GetSmartListPath(string fileName)
        {
            // Validate fileName is a valid GUID to prevent path injection
            if (string.IsNullOrWhiteSpace(fileName) || !Guid.TryParse(fileName, out _))
            {
                throw new ArgumentException("File name must be a valid GUID", nameof(fileName));
            }

            return Path.Combine(BasePath, $"{fileName}.json");
        }

        public string GetLegacyPath(string fileName)
        {
            // Validate fileName is a valid GUID to prevent path injection
            if (string.IsNullOrWhiteSpace(fileName) || !Guid.TryParse(fileName, out _))
            {
                throw new ArgumentException("File name must be a valid GUID", nameof(fileName));
            }

            return Path.Combine(_legacyBasePath, $"{fileName}.json");
        }

        /// <summary>
        /// Applies common post-processing to a playlist after deserialization.
        /// This includes setting the Type property and migrating legacy fields.
        /// </summary>
        /// <param name="playlist">The playlist to process</param>
        public static void ApplyPostProcessing(SmartPlaylistDto playlist)
        {
            if (playlist == null)
            {
                return;
            }

            // Ensure type is set
            playlist.Type = SmartListType.Playlist;

            // Migrate legacy fields (e.g. IsPlayed -> PlaybackStatus)
            playlist.MigrateLegacyFields();
        }

        /// <summary>
        /// Applies common post-processing to a collection after deserialization.
        /// This includes setting the Type property and migrating legacy fields.
        /// </summary>
        /// <param name="collection">The collection to process</param>
        public static void ApplyPostProcessing(SmartCollectionDto collection)
        {
            if (collection == null)
            {
                return;
            }

            // Ensure type is set
            collection.Type = SmartListType.Collection;

            // Migrate legacy fields (e.g. IsPlayed -> PlaybackStatus)
            collection.MigrateLegacyFields();
        }

        /// <summary>
        /// Tries to extract SmartListType from a JSON element.
        /// Handles both string and numeric type values for backward compatibility.
        /// </summary>
        /// <param name="typeElement">The JSON element containing the Type field</param>
        /// <param name="listType">The parsed SmartListType, or Playlist if parsing fails</param>
        /// <returns>True if the type element was successfully parsed, false otherwise</returns>
        public static bool TryGetSmartListType(JsonElement typeElement, out SmartListType listType)
        {
            if (typeElement.ValueKind == JsonValueKind.String)
            {
                var typeString = typeElement.GetString();
                if (Enum.TryParse<SmartListType>(typeString, ignoreCase: true, out var parsedType))
                {
                    listType = parsedType;
                    return true;
                }
            }
            else if (typeElement.ValueKind == JsonValueKind.Number)
            {
                var typeValue = typeElement.GetInt32();
                // Legacy numeric format: 1 = Collection, 0 or other = Playlist
                listType = typeValue == 1 ? SmartListType.Collection : SmartListType.Playlist;
                return true;
            }

            // Invalid type format - default to Playlist for backward compatibility
            listType = SmartListType.Playlist;
            return false;
        }

        /// <summary>
        /// Reads all smart list files once and returns them grouped by type.
        /// This is more efficient than having each store read files separately.
        /// </summary>
        public async Task<(SmartPlaylistDto[] Playlists, SmartCollectionDto[] Collections)> GetAllSmartListsAsync()
        {
            var filePaths = GetAllSmartListFilePaths();
            var playlists = new List<SmartPlaylistDto>();
            var collections = new List<SmartCollectionDto>();

            foreach (var filePath in filePaths)
            {
                try
                {
                    // Read file content as JSON document to check Type field first
                    var jsonContent = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
                    using var jsonDoc = JsonDocument.Parse(jsonContent);
                    
                    if (!jsonDoc.RootElement.TryGetProperty("Type", out var typeElement))
                    {
                        // Legacy file without Type field - default to Playlist
                        var playlist = JsonSerializer.Deserialize<SmartPlaylistDto>(jsonContent, SharedJsonOptions);
                        if (playlist != null)
                        {
                            ApplyPostProcessing(playlist);
                            playlists.Add(playlist);
                        }
                        continue;
                    }

                    // Determine type from JSON using shared helper
                    TryGetSmartListType(typeElement, out var listType);

                    // Deserialize to the correct type based on the Type field
                    if (listType == SmartListType.Playlist)
                    {
                        var playlist = JsonSerializer.Deserialize<SmartPlaylistDto>(jsonContent, SharedJsonOptions);
                        if (playlist != null)
                        {
                            ApplyPostProcessing(playlist);
                            playlists.Add(playlist);
                        }
                    }
                    else if (listType == SmartListType.Collection)
                    {
                        var collection = JsonSerializer.Deserialize<SmartCollectionDto>(jsonContent, SharedJsonOptions);
                        if (collection != null)
                        {
                            ApplyPostProcessing(collection);
                            collections.Add(collection);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Skip invalid files and continue loading others, but log for diagnostics
                    _logger?.LogWarning(ex, "Skipping invalid smart list file {FilePath}", filePath);
                }
            }

            return (playlists.ToArray(), collections.ToArray());
        }
    }
}

