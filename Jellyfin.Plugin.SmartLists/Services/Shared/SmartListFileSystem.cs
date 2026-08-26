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
        /// SkippedFiles counts files that were enumerated but failed to read/parse/deserialize;
        /// callers performing destructive cleanup must treat SkippedFiles > 0 as
        /// "the store read is incomplete - do not treat absent lists as deleted".
        /// </summary>
        Task<(SmartPlaylistDto[] Playlists, SmartCollectionDto[] Collections, int SkippedFiles)> GetAllSmartListsAsync();
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

            // Legacy include-only flags never worked for playlists (Jellyfin playlists can only
            // contain media items; container results were silently dropped) - strip them silently
            StripIncludeOnlyFlags(playlist);

            // Container media types are collection-only; drop any that slipped into stored JSON
            playlist.MediaTypes.RemoveAll(Core.Constants.MediaTypes.IsContainerType);

            // Collection-only flag; drop it if it slipped into stored JSON so the list still saves
            playlist.GroupIntoCollections = false;
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

            // Migrate legacy per-rule include-only flags to Collection/Playlist media types
            MigrateIncludeOnlyRulesToMediaTypes(collection);

            // Max Playtime is playlist-only and its input is hidden for collections, but the
            // server-wide Default Max Playtime used to be written into new collections anyway -
            // clear it so those lists stop being truncated by a limit their form never showed.
            // Back to unset rather than 0, so a collection that never carried one is untouched.
            if (collection.MaxPlayTimeMinutes is > 0)
            {
                collection.MaxPlayTimeMinutes = null;
            }
        }

        /// <summary>
        /// One-time migration of the legacy per-rule IncludeCollectionOnly/IncludePlaylistOnly
        /// checkboxes to the Collection/Playlist media types. Each include-only Collections/
        /// Playlists rule becomes a Name rule against the container itself (operator and value
        /// preserved) - sibling rules already evaluated against the container and carry over
        /// unchanged. Pure include-only lists (every rule group has an include-only rule) only
        /// ever produced containers, so their item media types are replaced outright; mixed
        /// lists keep their item types and gain the container type(s). MatchByMembers stays
        /// false, preserving the legacy match-against-container-metadata behavior.
        /// Idempotent: the flags are cleared, so rerunning is a no-op.
        ///
        /// A CollectionSearchDepth set on an include-only rule stays on the rewritten Name rule;
        /// SmartList.ExtractCollectionSearchDepthFromExpressions reads Name rules too, so the
        /// configured nested-collection walk depth survives migration.
        ///
        /// Behavior notes for the rewritten Name rules:
        /// - Equal: the legacy matcher also matched the container name with the configured
        ///   prefix/suffix stripped (users target smart lists by base name), so the engine must
        ///   replicate that fallback when evaluating Name+Equal against container candidates -
        ///   otherwise migrated pure include-only lists lose members.
        /// - Negative operators (NotEqual/NotContains/IsNotIn): the legacy matcher's default arm
        ///   matched nothing, so such rules produced empty groups. After migration they evaluate
        ///   normally - accepted as a bug-fix, documented under "Existing lists may change".
        /// </summary>
        private static void MigrateIncludeOnlyRulesToMediaTypes(SmartCollectionDto collection)
        {
            if (collection.ExpressionSets == null || collection.ExpressionSets.Count == 0)
            {
                return;
            }

            // Mirrors the legacy engine's allRulesAreIncludeOnly check: such lists skipped media
            // item processing entirely and returned only containers
            var allSetsAreIncludeOnly = collection.ExpressionSets.All(set =>
                set?.Expressions?.Any(expr =>
                    (expr.MemberName == "Collections" && expr.IncludeCollectionOnly == true) ||
                    (expr.MemberName == "Playlists" && expr.IncludePlaylistOnly == true)) == true);

            var needsCollectionType = false;
            var needsPlaylistType = false;

            foreach (var set in collection.ExpressionSets)
            {
                if (set?.Expressions == null)
                {
                    continue;
                }

                foreach (var expression in set.Expressions)
                {
                    // The rewrite changes only MemberName - CollectionSearchDepth (when set) is
                    // deliberately kept, and the depth extractor reads it from Name rules too
                    if (expression.MemberName == "Collections" && expression.IncludeCollectionOnly == true)
                    {
                        expression.MemberName = "Name";
                        needsCollectionType = true;
                    }
                    else if (expression.MemberName == "Playlists" && expression.IncludePlaylistOnly == true)
                    {
                        expression.MemberName = "Name";
                        needsPlaylistType = true;
                    }

                    expression.IncludeCollectionOnly = null;
                    expression.IncludePlaylistOnly = null;
                }
            }

            if (!needsCollectionType && !needsPlaylistType)
            {
                return;
            }

            if (allSetsAreIncludeOnly)
            {
                // Pure include-only list: item media types never contributed results - drop them
                collection.MediaTypes.Clear();
            }

            if (needsCollectionType && !collection.MediaTypes.Contains(Core.Constants.MediaTypes.Collection))
            {
                collection.MediaTypes.Add(Core.Constants.MediaTypes.Collection);
            }

            if (needsPlaylistType && !collection.MediaTypes.Contains(Core.Constants.MediaTypes.Playlist))
            {
                collection.MediaTypes.Add(Core.Constants.MediaTypes.Playlist);
            }
        }

        /// <summary>
        /// Strips the legacy IncludeCollectionOnly/IncludePlaylistOnly flags from a playlist's
        /// rules. The include-only feature never worked for playlists (container results were
        /// silently dropped by Jellyfin), so there is nothing to migrate. Idempotent.
        /// </summary>
        private static void StripIncludeOnlyFlags(SmartPlaylistDto playlist)
        {
            if (playlist.ExpressionSets == null)
            {
                return;
            }

            // The legacy engine skipped entire groups containing an include-only rule during item
            // evaluation, so on a playlist such a group contributed nothing. Removing the group is
            // the behavior-preserving migration - stripping only the flag would turn a dead group
            // into an active item filter. (The UI never offered these checkboxes on playlist forms,
            // so this only triggers on hand-edited JSON.)
            var deadGroups = playlist.ExpressionSets
                .Where(set => set?.Expressions?.Any(expr =>
                    expr?.IncludeCollectionOnly == true || expr?.IncludePlaylistOnly == true) == true)
                .ToList();

            // If every group is include-only the legacy playlist was permanently empty; removing
            // them all would flip it to "no rules = match everything". Strip just the flags instead
            // so the groups keep filtering as regular rules - a visible result beats a silent flip.
            if (deadGroups.Count > 0 && deadGroups.Count < playlist.ExpressionSets.Count)
            {
                foreach (var group in deadGroups)
                {
                    playlist.ExpressionSets.Remove(group);
                }
            }

            foreach (var set in playlist.ExpressionSets)
            {
                if (set?.Expressions == null)
                {
                    continue;
                }

                foreach (var expression in set.Expressions)
                {
                    expression.IncludeCollectionOnly = null;
                    expression.IncludePlaylistOnly = null;
                }
            }
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
        /// SkippedFiles counts files that were enumerated but failed to read/parse/deserialize;
        /// callers performing destructive cleanup must treat SkippedFiles > 0 as
        /// "the store read is incomplete - do not treat absent lists as deleted".
        /// </summary>
        public async Task<(SmartPlaylistDto[] Playlists, SmartCollectionDto[] Collections, int SkippedFiles)> GetAllSmartListsAsync()
        {
            var filePaths = GetAllSmartListFilePaths();
            var playlists = new List<SmartPlaylistDto>();
            var collections = new List<SmartCollectionDto>();
            var skippedFiles = 0;

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
                        else
                        {
                            skippedFiles++;
                            _logger?.LogWarning("Skipping smart list file {FilePath}: deserialized to null", filePath);
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
                        else
                        {
                            skippedFiles++;
                            _logger?.LogWarning("Skipping smart list file {FilePath}: deserialized to null", filePath);
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
                        else
                        {
                            skippedFiles++;
                            _logger?.LogWarning("Skipping smart list file {FilePath}: deserialized to null", filePath);
                        }
                    }
                    else
                    {
                        skippedFiles++;
                        _logger?.LogWarning("Skipping smart list file {FilePath}: unrecognized list type", filePath);
                    }
                }
                catch (Exception ex)
                {
                    // Skip invalid files and continue loading others, but log for diagnostics
                    skippedFiles++;
                    _logger?.LogWarning(ex, "Skipping invalid smart list file {FilePath}", filePath);
                }
            }

            return (playlists.ToArray(), collections.ToArray(), skippedFiles);
        }
    }
}

