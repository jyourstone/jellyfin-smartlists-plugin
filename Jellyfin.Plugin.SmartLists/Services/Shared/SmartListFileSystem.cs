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

        /// <summary>
        /// Gets the folder path for a smart list (e.g., /smartlists/{guid}/).
        /// This is the unified location for config.json and images.
        /// </summary>
        string GetSmartListFolderPath(string smartListId);

        /// <summary>
        /// Gets the config file path for a smart list (e.g., /smartlists/{guid}/config.json).
        /// This is the new unified format.
        /// </summary>
        string GetSmartListConfigPath(string smartListId);

        /// <summary>
        /// Finds the actual file path for a smart list config, checking all locations.
        /// Search order: new folder format, flat format, legacy directory.
        /// </summary>
        string? GetSmartListFilePath(string smartListId);

        /// <summary>
        /// Gets all smart list config file paths from all locations.
        /// </summary>
        string[] GetAllSmartListFilePaths();

        /// <summary>
        /// Gets the flat file path in the smartlists directory (legacy format).
        /// </summary>
        string GetSmartListPath(string fileName);

        /// <summary>
        /// Gets the path in the legacy smartplaylists directory.
        /// </summary>
        string GetLegacyPath(string fileName);

        /// <summary>
        /// Reads all smart list files and returns them grouped by type.
        /// </summary>
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

        /// <inheritdoc />
        public string GetSmartListFolderPath(string smartListId)
        {
            // Validate ID format to prevent path injection
            if (string.IsNullOrWhiteSpace(smartListId) || !Guid.TryParse(smartListId, out _))
            {
                throw new ArgumentException("Smart list ID must be a valid GUID", nameof(smartListId));
            }

            return Path.Combine(BasePath, smartListId);
        }

        /// <inheritdoc />
        public string GetSmartListConfigPath(string smartListId)
        {
            return Path.Combine(GetSmartListFolderPath(smartListId), "config.json");
        }

        /// <inheritdoc />
        public string? GetSmartListFilePath(string smartListId)
        {
            // Validate ID format to prevent path injection
            if (string.IsNullOrWhiteSpace(smartListId) || !Guid.TryParse(smartListId, out _))
            {
                return null;
            }

            // 1. Check new folder format first: /smartlists/{guid}/config.json
            var newFolderConfigPath = Path.Combine(BasePath, smartListId, "config.json");
            if (File.Exists(newFolderConfigPath))
            {
                return newFolderConfigPath;
            }

            // 2. Check flat format in smartlists/: /smartlists/{guid}.json
            var flatPath = Path.Combine(BasePath, $"{smartListId}.json");
            if (File.Exists(flatPath))
            {
                return flatPath;
            }

            // 3. Fallback to legacy smartplaylists/ directory
            if (Directory.Exists(_legacyBasePath))
            {
                var legacyPath = Path.Combine(_legacyBasePath, $"{smartListId}.json");
                if (File.Exists(legacyPath))
                {
                    return legacyPath;
                }
            }

            return null;
        }

        public string[] GetAllSmartListFilePaths()
        {
            var files = new List<string>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(BasePath))
            {
                // 1. Check for new folder format: /smartlists/{guid}/config.json
                foreach (var dir in Directory.GetDirectories(BasePath))
                {
                    var dirName = Path.GetFileName(dir);
                    if (Guid.TryParse(dirName, out _))
                    {
                        var configPath = Path.Combine(dir, "config.json");
                        if (File.Exists(configPath))
                        {
                            files.Add(configPath);
                            seenIds.Add(dirName);
                        }
                    }
                }

                // 2. Check for flat format: /smartlists/{guid}.json
                foreach (var file in Directory.GetFiles(BasePath, "*.json"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    // Skip if this ID was already found in folder format
                    if (Guid.TryParse(fileName, out _) && !seenIds.Contains(fileName))
                    {
                        files.Add(file);
                        seenIds.Add(fileName);
                    }
                }
            }

            // 3. Check legacy smartplaylists/ directory
            if (Directory.Exists(_legacyBasePath))
            {
                foreach (var file in Directory.GetFiles(_legacyBasePath, "*.json"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    // Skip if this ID was already found in new directory
                    if (Guid.TryParse(fileName, out _) && !seenIds.Contains(fileName))
                    {
                        files.Add(file);
                        seenIds.Add(fileName);
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

