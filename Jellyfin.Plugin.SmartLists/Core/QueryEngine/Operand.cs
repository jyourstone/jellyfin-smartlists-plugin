using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine
{
    public class Operand(string name)
    {

        public float CommunityRating { get; set; } = 0;
        public float CriticRating { get; set; } = 0;
        public List<string> Genres { get; set; } = [];
        public string Name { get; set; } = name;
        public string FolderPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public int ProductionYear { get; set; } = 0;
        public List<string> Studios { get; set; } = [];
        public string MediaType { get; set; } = string.Empty;
        public string ItemType { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string Overview { get; set; } = string.Empty;
        public double DateCreated { get; set; } = 0;
        public double DateLastRefreshed { get; set; } = 0;
        public double DateLastSaved { get; set; } = 0;
        public double DateModified { get; set; } = 0;
        public double ReleaseDate { get; set; } = 0;
        public List<string> Tags { get; set; } = [];
        public List<string> ParentSeriesTags { get; set; } = [];
        public List<string> ParentSeriesStudios { get; set; } = [];
        public List<string> ParentSeriesGenres { get; set; } = [];
        public double RuntimeMinutes { get; set; } = 0;
        public string OfficialRating { get; set; } = string.Empty;
        public List<string> AudioLanguages { get; set; } = [];
        public List<string> DefaultAudioLanguages { get; set; } = [];
        public List<string> People { get; set; } = [];
        public List<string> Actors { get; set; } = [];
        public List<string> ActorRoles { get; set; } = []; // Character/role names (e.g., "Jake Sully")
        public List<string> Directors { get; set; } = [];
        public List<string> Composers { get; set; } = [];
        public List<string> Writers { get; set; } = [];
        public List<string> GuestStars { get; set; } = [];
        public List<string> Producers { get; set; } = [];
        public List<string> Conductors { get; set; } = [];
        public List<string> Lyricists { get; set; } = [];
        public List<string> Arrangers { get; set; } = [];
        public List<string> SoundEngineers { get; set; } = [];
        public List<string> Mixers { get; set; } = [];
        public List<string> Remixers { get; set; } = [];
        public List<string> Creators { get; set; } = [];
        public List<string> PersonArtists { get; set; } = []; // Person role "Artist" (different from music Artists field)
        public List<string> PersonAlbumArtists { get; set; } = []; // Person role "Album Artist" (different from music AlbumArtists field)
        public List<string> Authors { get; set; } = [];
        public List<string> Illustrators { get; set; } = [];
        public List<string> Pencilers { get; set; } = [];
        public List<string> Inkers { get; set; } = [];
        public List<string> Colorists { get; set; } = [];
        public List<string> Letterers { get; set; } = [];
        public List<string> CoverArtists { get; set; } = [];
        public List<string> Editors { get; set; } = [];
        public List<string> Translators { get; set; } = [];

        // Music-specific fields
        public List<string> Artists { get; set; } = [];
        public List<string> AlbumArtists { get; set; } = [];

        // Audio quality fields (from media streams)
        public int AudioBitrate { get; set; } = 0;  // In kbps
        public int AudioSampleRate { get; set; } = 0;  // In Hz (e.g., 44100, 48000, 96000, 192000)
        public int AudioBitDepth { get; set; } = 0;  // In bits (e.g., 16, 24)
        public string AudioCodec { get; set; } = string.Empty;  // e.g., FLAC, MP3, AAC, ALAC
        public string AudioProfile { get; set; } = string.Empty;  // e.g., Dolby TrueHD, Dolby Atmos
        public int AudioChannels { get; set; } = 0;  // e.g., 2 for stereo, 6 for 5.1

        // Video quality fields (from media streams)
        public string Resolution { get; set; } = string.Empty;  // e.g., 480p, 720p, 1080p, 4K, 8K
        public float? Framerate { get; set; } = null;  // e.g., 23.976, 29.97, 59.94
        public string VideoCodec { get; set; } = string.Empty;  // e.g., HEVC, H264, AV1, VP9
        public string VideoProfile { get; set; } = string.Empty;  // e.g., Main 10, High
        public string VideoRange { get; set; } = string.Empty;  // e.g., SDR, HDR
        public string VideoRangeType { get; set; } = string.Empty;  // e.g., HDR10, DOVIWithHDR10, HDR10Plus, HLG

        // Collections field - indicates which collections this item belongs to
        public List<string> Collections { get; set; } = [];

        // Playlists field - indicates which playlists this item belongs to
        public List<string> Playlists { get; set; } = [];

        // Series name field - for episodes, contains the name of the parent series
        public string SeriesName { get; set; } = string.Empty;

        // Production locations - countries/regions where the content was produced
        public List<string> ProductionLocations { get; set; } = [];

        // Custom rating - user-defined rating field (can be text or numeric)
        public string CustomRating { get; set; } = string.Empty;

        // Subtitle languages - available subtitle languages from media streams
        public List<string> SubtitleLanguages { get; set; } = [];

        // Last episode air date - for Series, the air date of the most recent episode (Unix timestamp)
        public double LastEpisodeAirDate { get; set; } = 0;

        // User-specific data - Store user ID -> data mappings
        // These will be populated based on which users are referenced in rules
        public Dictionary<string, string> PlaybackStatusByUser { get; set; } = [];
        public Dictionary<string, int> PlayCountByUser { get; set; } = [];
        public Dictionary<string, bool> IsFavoriteByUser { get; set; } = [];
        public Dictionary<string, bool> NextUnwatchedByUser { get; set; } = [];
        public Dictionary<string, double> LastPlayedDateByUser { get; set; } = [];


        // Similarity score - calculated when SimilarTo rules are present
        public float? SimilarityScore { get; set; } = null;

        // Helper methods to check user-specific data
        public string GetPlaybackStatusByUser(string userId)
        {
            return PlaybackStatusByUser.TryGetValue(userId, out var value) ? value : "Unplayed";
        }

        public int GetPlayCountByUser(string userId)
        {
            return PlayCountByUser.TryGetValue(userId, out var value) ? value : 0;
        }

        public bool GetIsFavoriteByUser(string userId)
        {
            var hasKey = IsFavoriteByUser.TryGetValue(userId, out var value);
            // Note: We can't log here as this method is called from compiled expressions
            // If lookup fails, it means the UserId format doesn't match what was used during population
            return hasKey && value;
        }

        public bool GetNextUnwatchedByUser(string userId)
        {
            return NextUnwatchedByUser.TryGetValue(userId, out var value) && value;
        }

        public double GetLastPlayedDateByUser(string userId)
        {
            return LastPlayedDateByUser.TryGetValue(userId, out var value) ? value : -1;
        }
    }
}