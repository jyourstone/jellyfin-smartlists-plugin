using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;

namespace Jellyfin.Plugin.SmartLists.Core.Constants
{
    /// <summary>
    /// Represents an operator with its value and display label.
    /// </summary>
    public record OperatorInfo(string Value, string Label);

    /// <summary>
    /// Centralized operator definitions for different field types to reduce duplication.
    /// </summary>
    public static class Operators
    {
        /// <summary>
        /// All available operators for display in the UI.
        /// </summary>
        public static readonly OperatorInfo[] AllOperators =
        [
            new OperatorInfo("Equal", "equals"),
            new OperatorInfo("NotEqual", "not equals"),
            new OperatorInfo("Contains", "contains"),
            new OperatorInfo("NotContains", "not contains"),
            new OperatorInfo("IsIn", "is in"),
            new OperatorInfo("IsNotIn", "is not in"),
            new OperatorInfo("GreaterThan", "greater than"),
            new OperatorInfo("LessThan", "less than"),
            new OperatorInfo("GreaterThanOrEqual", "greater than or equal"),
            new OperatorInfo("LessThanOrEqual", "less than or equal"),
            new OperatorInfo("MatchRegex", "matches regex (.NET syntax)"),
            new OperatorInfo("After", "after"),
            new OperatorInfo("Before", "before"),
            new OperatorInfo("NewerThan", "newer than"),
            new OperatorInfo("OlderThan", "older than"),
            new OperatorInfo("Weekday", "weekday")
        ];

        /// <summary>
        /// Operators for multi-valued fields (collections, lists, etc.).
        /// Supports Equal/NotEqual which check if any item in the list matches.
        /// Use Contains for partial matching or IsIn for matching against multiple values.
        /// </summary>
        public static readonly string[] MultiValuedFieldOperators = ["Equal", "NotEqual", "Contains", "NotContains", "IsIn", "IsNotIn", "MatchRegex"];

        /// <summary>
        /// Operators for string fields (text-based fields like Name, Album, etc).
        /// </summary>
        public static readonly string[] StringFieldOperators = ["Equal", "NotEqual", "Contains", "NotContains", "IsIn", "IsNotIn", "MatchRegex"];

        /// <summary>
        /// Operators for SimilarTo field (excludes negative operators to prevent matching entire library).
        /// </summary>
        public static readonly string[] SimilarToFieldOperators = ["Equal", "Contains", "IsIn", "MatchRegex"];

        /// <summary>
        /// Operators for simple single-choice fields.
        /// </summary>
        public static readonly string[] SimpleFieldOperators = ["Equal", "NotEqual"];

        /// <summary>
        /// Operators for boolean fields.
        /// </summary>
        public static readonly string[] BooleanFieldOperators = ["Equal", "NotEqual"];

        /// <summary>
        /// Operators for numeric fields.
        /// </summary>
        public static readonly string[] NumericFieldOperators = ["Equal", "NotEqual", "GreaterThan", "LessThan", "GreaterThanOrEqual", "LessThanOrEqual"];

        /// <summary>
        /// Operators for date fields.
        /// </summary>
        public static readonly string[] DateFieldOperators = ["Equal", "NotEqual", "After", "Before", "NewerThan", "OlderThan", "Weekday"];

        /// <summary>
        /// Operators for resolution fields that support both equality and numeric comparisons.
        /// </summary>
        public static readonly string[] ResolutionFieldOperators = ["Equal", "NotEqual", "GreaterThan", "LessThan", "GreaterThanOrEqual", "LessThanOrEqual"];

        /// <summary>
        /// Gets the appropriate operators for a given field type.
        /// </summary>
        /// <param name="fieldName">The field name to get operators for</param>
        /// <returns>Array of operator values for the field</returns>
        public static string[] GetOperatorsForField(string fieldName)
        {
            // Check if it's a people field using centralized logic
            if (FieldDefinitions.IsPeopleField(fieldName))
            {
                return MultiValuedFieldOperators;
            }

            return fieldName switch
            {
                // Other multi-valued fields with full operator support
                "Genres" or "Studios" or "Tags" or "Artists" or "AlbumArtists" or "AudioLanguages" 
                    or "SubtitleLanguages" or "ProductionLocations"
                    => MultiValuedFieldOperators,

                // Multi-valued fields - Collections and Playlists now support all operators including NotContains and IsNotIn
                "Collections" or "Playlists"
                    => MultiValuedFieldOperators,

                // Simple fields
                "ItemType"
                    => SimpleFieldOperators,

                // Boolean fields
                "IsFavorite" or "NextUnwatched"
                    => BooleanFieldOperators,

                // PlaybackStatus field (string-based with specific values)
                "PlaybackStatus"
                    => ["Equal", "NotEqual"],

                // Numeric fields
                "ProductionYear" or "CommunityRating" or "CriticRating" or "RuntimeMinutes" or "PlayCount" or "Framerate"
                    or "AudioBitrate" or "AudioSampleRate" or "AudioBitDepth" or "AudioChannels"
                    => NumericFieldOperators,

                // Date fields
                "DateCreated" or "DateLastRefreshed" or "DateLastSaved" or "DateModified" or "ReleaseDate" or "LastPlayedDate"
                    or "LastEpisodeAirDate"
                    => DateFieldOperators,

                // Resolution fields
                "Resolution"
                    => ResolutionFieldOperators,

                // SimilarTo field (excludes negative operators)
                "SimilarTo"
                    => SimilarToFieldOperators,

                // String fields (text-based fields)
                "Name" or "Album" or "SeriesName" or "OfficialRating" or "CustomRating" or "Overview" or "FileName" or "FolderPath"
                    or "AudioCodec" or "AudioProfile" or "VideoCodec" or "VideoProfile" or "VideoRange" or "VideoRangeType"
                    or "LibraryName"
                    => StringFieldOperators,

                // Default: allow all operators for unknown fields
                _ => [.. AllOperators.Select(op => op.Value)],
            };
        }

        /// <summary>
        /// Gets the complete field operators dictionary for all supported fields.
        /// </summary>
        /// <returns>Dictionary mapping field names to their allowed operators</returns>
        public static Dictionary<string, string[]> GetFieldOperatorsDictionary()
        {
            var dictionary = new Dictionary<string, string[]>
            {
                // List fields - multi-valued fields
                // Collections and Playlists now support full operator set including NotContains and IsNotIn
                ["Collections"] = MultiValuedFieldOperators,
                ["Playlists"] = MultiValuedFieldOperators,
                ["Genres"] = MultiValuedFieldOperators,
                ["Studios"] = MultiValuedFieldOperators,
                ["Tags"] = MultiValuedFieldOperators,
                ["Artists"] = MultiValuedFieldOperators,
                ["AlbumArtists"] = MultiValuedFieldOperators,
                ["AudioLanguages"] = MultiValuedFieldOperators,
                ["SubtitleLanguages"] = MultiValuedFieldOperators,
                ["ProductionLocations"] = MultiValuedFieldOperators,

                // Simple fields - single-choice fields
                ["ItemType"] = SimpleFieldOperators,

                // Boolean fields - true/false fields
                ["IsFavorite"] = BooleanFieldOperators,
                ["NextUnwatched"] = BooleanFieldOperators,

                // PlaybackStatus field - string field with specific values
                ["PlaybackStatus"] = ["Equal", "NotEqual"],

                // Numeric fields - number-based fields
                ["ProductionYear"] = NumericFieldOperators,
                ["CommunityRating"] = NumericFieldOperators,
                ["CriticRating"] = NumericFieldOperators,
                ["RuntimeMinutes"] = NumericFieldOperators,
                ["PlayCount"] = NumericFieldOperators,
                ["Framerate"] = NumericFieldOperators,
                ["AudioBitrate"] = NumericFieldOperators,
                ["AudioSampleRate"] = NumericFieldOperators,
                ["AudioBitDepth"] = NumericFieldOperators,
                ["AudioChannels"] = NumericFieldOperators,

                // Date fields - date/time fields
                ["DateCreated"] = DateFieldOperators,
                ["DateLastRefreshed"] = DateFieldOperators,
                ["DateLastSaved"] = DateFieldOperators,
                ["DateModified"] = DateFieldOperators,
                ["ReleaseDate"] = DateFieldOperators,
                ["LastPlayedDate"] = DateFieldOperators,
                ["LastEpisodeAirDate"] = DateFieldOperators,

                // Resolution fields - resolution-based fields
                ["Resolution"] = ResolutionFieldOperators,

                // SimilarTo field - excludes negative operators
                ["SimilarTo"] = SimilarToFieldOperators,

                // String fields - text-based fields
                ["Name"] = StringFieldOperators,
                ["Album"] = StringFieldOperators,
                ["SeriesName"] = StringFieldOperators,
                ["OfficialRating"] = StringFieldOperators,
                ["CustomRating"] = StringFieldOperators,
                ["Overview"] = StringFieldOperators,
                ["FileName"] = StringFieldOperators,
                ["FolderPath"] = StringFieldOperators,
                ["AudioCodec"] = StringFieldOperators,
                ["AudioProfile"] = StringFieldOperators,
                ["VideoCodec"] = StringFieldOperators,
                ["VideoProfile"] = StringFieldOperators,
                ["VideoRange"] = StringFieldOperators,
                ["VideoRangeType"] = StringFieldOperators,
                ["LibraryName"] = StringFieldOperators,
            };

            // Add all people role fields dynamically using centralized logic
            foreach (var peopleField in FieldDefinitions.PeopleRoleFields)
            {
                dictionary[peopleField] = MultiValuedFieldOperators;
            }

            return dictionary;
        }

        /// <summary>
        /// Gets a formatted string of all supported operators for a field, useful for error messages.
        /// </summary>
        /// <param name="fieldName">The field name to get operators for</param>
        /// <returns>Comma-separated string of supported operators</returns>
        public static string GetSupportedOperatorsString(string fieldName)
        {
            var operators = GetOperatorsForField(fieldName);
            return string.Join(", ", operators);
        }
    }
}
