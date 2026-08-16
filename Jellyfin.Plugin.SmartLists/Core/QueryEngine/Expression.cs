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

        // Legacy - read via IncludeParent*Effective, no longer written by the UI.
        // Do NOT remove: this is an on-disk JSON key older saved lists still carry.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentSeriesTags { get; set; } = null;

        // Legacy - read via IncludeParent*Effective, no longer written by the UI.
        // Do NOT remove: this is an on-disk JSON key older saved lists still carry.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentAlbumTags { get; set; } = null;

        // Tags-specific option to only check parent tags (skip item's own tags) - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? OnlyParentTags { get; set; } = null;

        // Legacy - read via IncludeParent*Effective, no longer written by the UI.
        // Do NOT remove: this is an on-disk JSON key older saved lists still carry.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentSeriesStudios { get; set; } = null;

        // Legacy - read via IncludeParent*Effective, no longer written by the UI.
        // Do NOT remove: this is an on-disk JSON key older saved lists still carry.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentAlbumStudios { get; set; } = null;

        // Studios-specific option to only check parent studios (skip item's own studios) - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? OnlyParentStudios { get; set; } = null;

        // Legacy - read via IncludeParent*Effective, no longer written by the UI.
        // Do NOT remove: this is an on-disk JSON key older saved lists still carry.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentSeriesGenres { get; set; } = null;

        // Legacy - read via IncludeParent*Effective, no longer written by the UI.
        // Do NOT remove: this is an on-disk JSON key older saved lists still carry.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentAlbumGenres { get; set; } = null;

        // Genres-specific option to only check parent genres (skip item's own genres) - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? OnlyParentGenres { get; set; } = null;

        // Tags-specific option: also match values inherited from ancestors (season, series, album, folder, library) - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentTags { get; set; } = null;

        // Studios-specific option: also match values inherited from ancestors - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentStudios { get; set; } = null;

        // Genres-specific option: also match values inherited from ancestors - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeParentGenres { get; set; } = null;

        // Legacy parent flags fold into these. This is the ONLY place new + legacy are combined —
        // Engine, IsParentAwareListExpression, GenerateRuleSetHash and FieldRequirements.Analyze
        // all read these, never the raw flags.
        [JsonIgnore]
        public bool IncludeParentTagsEffective => IncludeParentTags == true || IncludeParentSeriesTags == true || IncludeParentAlbumTags == true;

        [JsonIgnore]
        public bool IncludeParentStudiosEffective => IncludeParentStudios == true || IncludeParentSeriesStudios == true || IncludeParentAlbumStudios == true;

        [JsonIgnore]
        public bool IncludeParentGenresEffective => IncludeParentGenres == true || IncludeParentSeriesGenres == true || IncludeParentAlbumGenres == true;

        // AudioLanguages-specific option - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? OnlyDefaultAudioLanguage { get; set; } = null;

        // RuntimeMinutes-specific option - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RuntimeUnit { get; set; } = null;

        // Date-field option (ReleaseDate, LastEpisodeAirDate): treat items with no known date as matching - only serialize when meaningful
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IncludeUnknownDates { get; set; } = null;

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
                "Rating" => "GetRatingByUser",
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
                "Rating" => true,
                "IsFavorite" => true,
                "NextUnwatched" => true,
                "LastPlayedDate" => true,
                _ => false,
            };
        }
    }
}
