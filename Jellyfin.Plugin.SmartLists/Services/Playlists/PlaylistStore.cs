using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.Abstractions;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.Playlists
{
    /// <summary>
    /// Store implementation for smart playlists
    /// Handles JSON serialization/deserialization with type discrimination
    /// </summary>
    public class PlaylistStore : ISmartListStore<SmartPlaylistDto>
    {
        private readonly ISmartListFileSystem _fileSystem;
        private readonly ILogger<PlaylistStore>? _logger;

        public PlaylistStore(
            ISmartListFileSystem fileSystem,
            ILogger<PlaylistStore>? logger = null)
        {
            _fileSystem = fileSystem;
            _logger = logger;
        }

        public async Task<SmartPlaylistDto?> GetByIdAsync(Guid id)
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
                    var playlist = await LoadPlaylistAsync(filePath).ConfigureAwait(false);
                    if (playlist != null && playlist.Type == Core.Enums.SmartListType.Playlist)
                    {
                        _logger?.LogDebug("PlaylistStore.GetByIdAsync: Loaded playlist {Name} with {ImageCount} custom images",
                            playlist.Name, playlist.CustomImages?.Count ?? 0);
                        return playlist;
                    }
                }
                catch (Exception ex)
                {
                    // File exists but couldn't be loaded, fall back to scanning all files
                    _logger?.LogDebug(ex, "Failed to load playlist from direct path {FilePath}, falling back to scan", filePath);
                }
            }

            // Fallback: scan all playlists if direct lookup failed
            var allPlaylists = await GetAllAsync().ConfigureAwait(false);
            var result = allPlaylists.FirstOrDefault(p => string.Equals(p.Id, id.ToString(), StringComparison.OrdinalIgnoreCase));
            if (result != null)
            {
                _logger?.LogDebug("PlaylistStore.GetByIdAsync (fallback): Loaded playlist {Name} with {ImageCount} custom images",
                    result.Name, result.CustomImages?.Count ?? 0);
            }
            return result;
        }

        public async Task<SmartPlaylistDto[]> GetAllAsync()
        {
            // Use shared helper to read files once
            var (playlists, _) = await _fileSystem.GetAllSmartListsAsync().ConfigureAwait(false);
            return playlists;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Playlist ID is validated as GUID before use in file paths, preventing path injection")]
        public async Task<SmartPlaylistDto> SaveAsync(SmartPlaylistDto smartPlaylist)
        {
            ArgumentNullException.ThrowIfNull(smartPlaylist);

            _logger?.LogDebug("PlaylistStore.SaveAsync: Saving playlist {Name} with {ImageCount} custom images",
                smartPlaylist.Name, smartPlaylist.CustomImages?.Count ?? 0);

            // Ensure type is set
            smartPlaylist.Type = Core.Enums.SmartListType.Playlist;

            // Migrate old format to new format if needed
            MigrateToUserPlaylists(smartPlaylist);

            // Remove old fields when using new UserPlaylists format
            if (smartPlaylist.UserPlaylists != null && smartPlaylist.UserPlaylists.Count > 0)
            {
                smartPlaylist.UserId = null;
                smartPlaylist.JellyfinPlaylistId = null;
            }

            // Validate ID is a valid GUID to prevent path injection
            if (string.IsNullOrWhiteSpace(smartPlaylist.Id) || !Guid.TryParse(smartPlaylist.Id, out var parsedId) || parsedId == Guid.Empty)
            {
                throw new ArgumentException("Playlist ID must be a valid non-empty GUID", nameof(smartPlaylist));
            }

            // Normalize ID to canonical GUID string for consistent file lookups
            smartPlaylist.Id = parsedId.ToString();
            smartPlaylist.FileName = "config.json";

            // New unified folder structure: /smartlists/{guid}/config.json
            var folderPath = _fileSystem.GetSmartListFolderPath(smartPlaylist.Id);
            var configPath = _fileSystem.GetSmartListConfigPath(smartPlaylist.Id);
            var tempPath = configPath + ".tmp";

            try
            {
                // Ensure folder exists
                Directory.CreateDirectory(folderPath);

                await using (var writer = File.Create(tempPath))
                {
                    await JsonSerializer.SerializeAsync(writer, smartPlaylist, SmartListFileSystem.SharedJsonOptions).ConfigureAwait(false);
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

                _logger?.LogDebug("PlaylistStore.SaveAsync: File written successfully to {FilePath}", configPath);

                // Clean up all legacy locations after successful save
                CleanupLegacyLocations(smartPlaylist.Id);
            }
            finally
            {
                // Clean up temp file if it still exists
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore cleanup errors */ }
            }

            return smartPlaylist;
        }

        public Task DeleteAsync(Guid id)
        {
            // Validate GUID format to prevent path injection
            if (id == Guid.Empty)
            {
                throw new ArgumentException("Playlist ID cannot be empty", nameof(id));
            }

            var smartListId = id.ToString();

            // Delete the entire folder (new unified structure)
            var folderPath = _fileSystem.GetSmartListFolderPath(smartListId);
            if (Directory.Exists(folderPath))
            {
                try
                {
                    Directory.Delete(folderPath, recursive: true);
                    _logger?.LogDebug("Deleted playlist folder {FolderPath}", folderPath);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete playlist folder {FolderPath}", folderPath);
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

        /// <summary>
        /// Migrates old UserId/JellyfinPlaylistId format to UserPlaylists array for backwards compatibility.
        /// This ensures old playlists continue to work with the new multi-user system.
        /// Also normalizes UserIds to consistent format and deduplicates entries.
        /// </summary>
        private static void MigrateToUserPlaylists(SmartPlaylistDto dto)
        {
            // If UserPlaylists already exists and has values, normalize and deduplicate
            if (dto.UserPlaylists != null && dto.UserPlaylists.Count > 0)
            {
                var normalizedUserPlaylists = new List<SmartPlaylistDto.UserPlaylistMapping>();
                var seenUserIds = new HashSet<Guid>();

                foreach (var userMapping in dto.UserPlaylists)
                {
                    if (!string.IsNullOrEmpty(userMapping.UserId) && Guid.TryParse(userMapping.UserId, out var userId) && userId != Guid.Empty)
                    {
                        // Normalize UserId to standard format (without dashes) and check for duplicates
                        // HashSet.Add() returns true if item was added (didn't exist), false if already exists
                        if (seenUserIds.Add(userId))
                        {
                            normalizedUserPlaylists.Add(new SmartPlaylistDto.UserPlaylistMapping
                            {
                                UserId = userId.ToString("N"), // Standard format without dashes
                                JellyfinPlaylistId = userMapping.JellyfinPlaylistId
                            });
                        }
                    }
                }

                dto.UserPlaylists = normalizedUserPlaylists;
                return;
            }

            // If UserId exists, migrate to UserPlaylists array
            // DEPRECATED: dto.UserId is for backwards compatibility with old single-user playlists.
            // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
            if (!string.IsNullOrEmpty(dto.UserId) && Guid.TryParse(dto.UserId, out var parsedUserId) && parsedUserId != Guid.Empty)
            {
                dto.UserPlaylists = new List<SmartPlaylistDto.UserPlaylistMapping>
                {
                    new SmartPlaylistDto.UserPlaylistMapping
                    {
                        UserId = parsedUserId.ToString("N"), // Normalize to standard format without dashes
                        JellyfinPlaylistId = dto.JellyfinPlaylistId
                    }
                };

                // Note: This creates the UserPlaylists structure from legacy data.
                // The legacy UserId and JellyfinPlaylistId fields will be cleared by SaveAsync
                // when the playlist is saved (see lines 82-86). This is intentional to enforce
                // the new multi-user format and prevent inconsistencies.
                // DEPRECATED: This is for backwards compatibility with old single-user playlists.
                // It is planned to be removed in version 10.12. Use UserPlaylists array instead.
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File path is validated upstream - only valid GUIDs are passed to this method")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Method is part of instance interface implementation")]
        private async Task<SmartPlaylistDto?> LoadPlaylistAsync(string filePath)
        {
            await using var reader = File.OpenRead(filePath);
            var dto = await JsonSerializer.DeserializeAsync<SmartPlaylistDto>(reader, SmartListFileSystem.SharedJsonOptions).ConfigureAwait(false);

            // Only return playlists - if this is a collection, return null
            if (dto == null)
            {
                return null;
            }

            // If type is explicitly set to Collection, this is not a playlist - return null
            if (dto.Type == Core.Enums.SmartListType.Collection)
            {
                return null;
            }

            // Handle backward compatibility: if Type is not set (legacy file), default to Playlist
            if (dto.Type == 0) // Default enum value
            {
                dto.Type = Core.Enums.SmartListType.Playlist;
            }

            // Migrate old UserId/JellyfinPlaylistId format to UserPlaylists array
            // This is playlist-specific and not part of common post-processing
            MigrateToUserPlaylists(dto);

            // Apply common post-processing (sets Type, migrates legacy fields)
            SmartListFileSystem.ApplyPostProcessing(dto);

            return dto;
        }
    }
}

