using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine
{
    public class Expression(string memberName, string @operator, string targetValue)
    {
        public string MemberName { get; set; } = memberName;
        public string Operator { get; set; } = @operator;
        public string TargetValue { get; set; } = targetValue;

        // User-specific query support - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UserId { get; set; } = null;

        // NextUnwatched-specific option - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeUnwatchedSeries { get; set; } = null;

        // Collections-specific option - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeEpisodesWithinSeries { get; set; } = null;

        // Collections-specific option - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeCollectionOnly { get; set; } = null;

        // Playlists-specific option - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludePlaylistOnly { get; set; } = null;

        // Tags-specific option - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentSeriesTags { get; set; } = null;

        // Studios-specific option - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentSeriesStudios { get; set; } = null;

        // Genres-specific option - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentSeriesGenres { get; set; } = null;

        // AudioLanguages-specific option - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? OnlyDefaultAudioLanguage { get; set; } = null;

        // Collections-specific depth option - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? CollectionSearchDepth { get; set; } = null;

        // Helper property to check if this is a user-specific expression
        // Only serialize when UserId is not null
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsUserSpecific => !string.IsNullOrEmpty(UserId);

        // Helper property to get the user-specific field name for reflection
        // Only serialize when it's actually a user-specific field (different from MemberName)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string? UserSpecificField => IsUserSpecific && IsUserSpecificField(MemberName) ? GetUserSpecificFieldName() : null;

        private string GetUserSpecificFieldName()
        {
            return MemberName switch
            {
                "PlaybackStatus" => "GetPlaybackStatusByUser",
                "IsPlayed" => "GetPlaybackStatusByUser", // Legacy field - treat as PlaybackStatus
                "PlayCount" => "GetPlayCountByUser",
                "IsFavorite" => "GetIsFavoriteByUser",
                "NextUnwatched" => "GetNextUnwatchedByUser",
                "LastPlayedDate" => "GetLastPlayedDateByUser",
                _ => MemberName,
            };
        }

        public static bool IsUserSpecificField(string memberName)
        {
            return memberName switch
            {
                "PlaybackStatus" => true,
                "IsPlayed" => true, // Legacy field - treat as user-specific
                "PlayCount" => true,
                "IsFavorite" => true,
                "NextUnwatched" => true,
                "LastPlayedDate" => true,
                _ => false,
            };
        }
    }
}