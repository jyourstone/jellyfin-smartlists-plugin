using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartLists.Core.Models
{
    /// <summary>
    /// DTO for user-specific smart playlists
    /// </summary>
    [Serializable]
    public class SmartPlaylistDto : SmartListDto
    {
        public SmartPlaylistDto()
        {
            Type = Core.Enums.SmartListType.Playlist;
        }

        // Playlist-specific properties
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? JellyfinPlaylistId { get; set; }  // Jellyfin playlist ID for reliable lookup (backwards compatibility - first user's playlist)
        public bool Public { get; set; } = false; // Default to private
        public bool AllUsers { get; set; } = false; // Create one personalized playlist for every current and future Jellyfin user

        /// <summary>
        /// Multi-user playlist support: Array of user-playlist mappings.
        /// When multiple users are selected, one Jellyfin playlist is created per user.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<UserPlaylistMapping>? UserPlaylists { get; set; }

        /// <summary>
        /// Optional bumper configuration: items matching these rules are woven between
        /// the playlist's main items at refresh time. Null = feature disabled.
        /// Bumpers do not count toward MaxItems/MaxPlayTimeMinutes.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BumperConfigDto? Bumpers { get; set; }

        /// <summary>
        /// Mapping between a user ID and their associated Jellyfin playlist ID
        /// </summary>
        [Serializable]
        public class UserPlaylistMapping
        {
            public required string UserId { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? JellyfinPlaylistId { get; set; }
        }
    }
}
