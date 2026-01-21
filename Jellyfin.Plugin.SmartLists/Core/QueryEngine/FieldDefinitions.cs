using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine
{
    /// <summary>
    /// Centralized field definitions to avoid duplication across the codebase.
    /// </summary>
    public static class FieldDefinitions
    {
        /// <summary>
        /// Date fields that require special handling for date operations.
        /// </summary>
        public static readonly HashSet<string> DateFields =
        [
            "DateCreated",
            "DateLastRefreshed",
            "DateLastSaved",
            "DateModified",
            "ReleaseDate",
            "LastPlayedDate",
            "LastEpisodeAirDate"
        ];

        /// <summary>
        /// List fields that contain collections of strings.
        /// </summary>
        public static readonly HashSet<string> ListFields =
        [
            "Collections",
            "Playlists",
            "AudioLanguages",
            "SubtitleLanguages",
            "ProductionLocations",
            "People",
            "Actors",
            "ActorRoles",
            "Directors",
            "Composers",
            "Writers",
            "GuestStars",
            "Producers",
            "Conductors",
            "Lyricists",
            "Arrangers",
            "SoundEngineers",
            "Mixers",
            "Remixers",
            "Creators",
            "PersonArtists",
            "PersonAlbumArtists",
            "Authors",
            "Illustrators",
            "Pencilers",
            "Inkers",
            "Colorists",
            "Letterers",
            "CoverArtists",
            "Editors",
            "Translators",
            "Genres",
            "Studios",
            "Tags",
            "Artists",
            "AlbumArtists"
        ];

        /// <summary>
        /// People role fields for movies and TV shows (subset of ListFields).
        /// These fields allow filtering by specific cast and crew roles.
        /// Uses case-insensitive comparison to handle legacy playlists with lowercase field names.
        /// </summary>
        public static readonly HashSet<string> PeopleRoleFields = new(System.StringComparer.OrdinalIgnoreCase)
        {
            "People",
            "Actors",
            "ActorRoles",
            "Directors",
            "Composers",
            "Writers",
            "GuestStars",
            "Producers",
            "Conductors",
            "Lyricists",
            "Arrangers",
            "SoundEngineers",
            "Mixers",
            "Remixers",
            "Creators",
            "PersonArtists",
            "PersonAlbumArtists",
            "Authors",
            "Illustrators",
            "Pencilers",
            "Inkers",
            "Colorists",
            "Letterers",
            "CoverArtists",
            "Editors",
            "Translators",
        };

        /// <summary>
        /// Numeric fields that support numeric comparisons.
        /// </summary>
        public static readonly HashSet<string> NumericFields =
        [
            "ProductionYear",
            "CommunityRating",
            "CriticRating",
            "RuntimeMinutes",
            "PlayCount",
            "Framerate",
            "AudioBitrate",
            "AudioSampleRate",
            "AudioBitDepth",
            "AudioChannels"
        ];

        /// <summary>
        /// Boolean fields that only support equality comparisons.
        /// </summary>
        public static readonly HashSet<string> BooleanFields =
        [
            "IsFavorite",
            "NextUnwatched"
        ];

        /// <summary>
        /// Simple fields that have predefined values.
        /// </summary>
        public static readonly HashSet<string> SimpleFields =
        [
            "ItemType"
        ];

        /// <summary>
        /// Similarity fields that require special handling for metadata comparison.
        /// These are expensive operations as they require finding reference items and calculating similarity.
        /// </summary>
        public static readonly HashSet<string> SimilarityFields =
        [
            "SimilarTo"
        ];

        /// <summary>
        /// Resolution fields that support numeric comparisons based on height.
        /// </summary>
        public static readonly HashSet<string> ResolutionFields =
        [
            "Resolution"
        ];

        /// <summary>
        /// Framerate fields that support numeric comparisons.
        /// </summary>
        public static readonly HashSet<string> FramerateFields =
        [
            "Framerate"
        ];

        /// <summary>
        /// User-specific fields that can be filtered by user.
        /// </summary>
        public static readonly HashSet<string> UserDataFields =
        [
            "PlaybackStatus",
            "IsFavorite",
            "PlayCount",
            "NextUnwatched",
            "LastPlayedDate"
        ];

        /// <summary>
        /// Checks if a field is a date field that requires special date handling.
        /// </summary>
        /// <param name="fieldName">The field name to check</param>
        /// <returns>True if it's a date field, false otherwise</returns>
        public static bool IsDateField(string fieldName)
        {
            return DateFields.Contains(fieldName);
        }

        /// <summary>
        /// Checks if a field is a list field that contains collections.
        /// </summary>
        /// <param name="fieldName">The field name to check</param>
        /// <returns>True if it's a list field, false otherwise</returns>
        public static bool IsListField(string fieldName)
        {
            return ListFields.Contains(fieldName);
        }

        /// <summary>
        /// Checks if a field is a numeric field that supports numeric operations.
        /// </summary>
        /// <param name="fieldName">The field name to check</param>
        /// <returns>True if it's a numeric field, false otherwise</returns>
        public static bool IsNumericField(string fieldName)
        {
            return NumericFields.Contains(fieldName);
        }

        /// <summary>
        /// Checks if a field is a boolean field that only supports equality.
        /// </summary>
        /// <param name="fieldName">The field name to check</param>
        /// <returns>True if it's a boolean field, false otherwise</returns>
        public static bool IsBooleanField(string fieldName)
        {
            return BooleanFields.Contains(fieldName);
        }

        /// <summary>
        /// Checks if a field is a simple field with predefined values.
        /// </summary>
        /// <param name="fieldName">The field name to check</param>
        /// <returns>True if it's a simple field, false otherwise</returns>
        public static bool IsSimpleField(string fieldName)
        {
            return SimpleFields.Contains(fieldName);
        }

        /// <summary>
        /// Checks if a field is a resolution field that supports numeric comparisons.
        /// </summary>
        /// <param name="fieldName">The field name to check</param>
        /// <returns>True if it's a resolution field, false otherwise</returns>
        public static bool IsResolutionField(string fieldName)
        {
            return ResolutionFields.Contains(fieldName);
        }

        /// <summary>
        /// Checks if a field is a framerate field that supports numeric comparisons.
        /// </summary>
        /// <param name="fieldName">The field name to check</param>
        /// <returns>True if it's a framerate field, false otherwise</returns>
        public static bool IsFramerateField(string fieldName)
        {
            return FramerateFields.Contains(fieldName);
        }

        /// <summary>
        /// Checks if a field supports user-specific filtering.
        /// </summary>
        /// <param name="fieldName">The field name to check</param>
        /// <returns>True if it's a user data field, false otherwise</returns>
        public static bool IsUserDataField(string fieldName)
        {
            return UserDataFields.Contains(fieldName);
        }

        /// <summary>
        /// Checks if a field is a similarity field that requires metadata comparison.
        /// </summary>
        /// <param name="fieldName">The field name to check</param>
        /// <returns>True if it's a similarity field, false otherwise</returns>
        public static bool IsSimilarityField(string fieldName)
        {
            return SimilarityFields.Contains(fieldName);
        }

        /// <summary>
        /// Checks if a field is a people role field (cast/crew for movies and TV shows).
        /// </summary>
        /// <param name="fieldName">The field name to check</param>
        /// <returns>True if it's a people role field, false otherwise</returns>
        public static bool IsPeopleField(string fieldName)
        {
            return PeopleRoleFields.Contains(fieldName);
        }

        /// <summary>
        /// Gets all available field names for API responses.
        /// </summary>
        /// <returns>Array of all field names</returns>
        public static string[] GetAllFieldNames()
        {
            var allFields = new HashSet<string>();
            allFields.UnionWith(DateFields);
            allFields.UnionWith(ListFields);
            allFields.UnionWith(NumericFields);
            allFields.UnionWith(BooleanFields);
            allFields.UnionWith(SimpleFields);
            allFields.UnionWith(ResolutionFields);
            allFields.UnionWith(FramerateFields);

            // Add other fields that aren't in the main categories
            allFields.Add("Name");
            allFields.Add("Album");
            allFields.Add("AudioLanguages");
            allFields.Add("AudioCodec");
            allFields.Add("AudioProfile");
            allFields.Add("VideoCodec");
            allFields.Add("VideoProfile");
            allFields.Add("VideoRange");
            allFields.Add("VideoRangeType");
            allFields.Add("OfficialRating");
            allFields.Add("Overview");
            allFields.Add("FileName");
            allFields.Add("FolderPath");
            allFields.Add("MediaType");
            allFields.Add("SeriesName");
            allFields.Add("SimilarTo");
            allFields.UnionWith(UserDataFields);

            return [.. allFields];
        }
    }
}