using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine
{
    /// <summary>
    /// Extraction groups for field operations. Controls when fields are extracted.
    /// Uses [Flags] to allow combining multiple groups.
    ///
    /// FIELD CATEGORIZATION GUIDE:
    /// ===========================
    ///
    /// 1. ExtractionGroup.None (Tier 0 - Always Extracted):
    ///    - Direct BaseItem property access (~0.01ms/item)
    ///    - No caching needed
    ///    - Examples: Name, Id, MediaType, CommunityRating
    ///
    /// 2. Cheap Groups (Tier 1 - Conditional, Fast):
    ///    - FileInfo, LibraryInfo, AudioMetadata, TextContent, ItemLists, UserData, Dates
    ///    - Only extracted when rules use these fields
    ///    - Don't trigger two-phase filtering
    ///    - LibraryInfo uses caching (LibraryNameById in RefreshCache)
    ///
    /// 3. Expensive Groups (Tier 2 - Conditional, Cached):
    ///    - All other groups (People, Collections, VideoQuality, etc.)
    ///    - Trigger two-phase filtering for optimization
    ///    - MUST have caching in RefreshCache (see Services/Shared/RefreshQueueService.cs)
    ///
    /// ADDING NEW FIELDS:
    /// ==================
    /// 1. Add to Operand.cs (property)
    /// 2. Add to FieldRegistry.cs (this file, with correct ExtractionGroup)
    /// 3. Add to Factory.cs (extraction logic)
    /// 4. If ExtractionGroup != None AND makes API/DB calls:
    ///    - Add cache in RefreshQueueService.RefreshCache
    ///    - Update extraction to use cache
    /// 5. If expensive: ensure field goes to expensive group for two-phase filtering
    ///
    /// CACHING REQUIREMENTS BY GROUP:
    /// ==============================
    /// - AudioLanguages: MediaStreamsCache
    /// - AudioQuality: MediaStreamsCache
    /// - VideoQuality: MediaStreamsCache
    /// - People: ItemPeople
    /// - Collections: ItemCollections, CollectionMembershipCache
    /// - Playlists: ItemPlaylists, PlaylistMembershipCache
    /// - NextUnwatched: NextUnwatched cache
    /// - SeriesName: SeriesNameById
    /// - ParentSeriesTags: SeriesTagsById
    /// - ParentSeriesStudios: SeriesStudiosById
    /// - ParentSeriesGenres: SeriesGenresById
    /// - LastEpisodeAirDate: LastEpisodeAirDateById
    /// - LibraryInfo: LibraryNameById
    /// </summary>
    [Flags]
    public enum ExtractionGroup
    {
        None = 0,

        // Expensive extraction groups (require API calls, DB queries - MUST have caching)
        // These trigger two-phase filtering for optimization
        AudioLanguages = 1 << 0,      // Fields: AudioLanguages, SubtitleLanguages | Cache: MediaStreamsCache
        AudioQuality = 1 << 1,        // Fields: AudioBitrate, AudioSampleRate, AudioBitDepth, AudioCodec, AudioProfile, AudioChannels | Cache: MediaStreamsCache
        VideoQuality = 1 << 2,        // Fields: Resolution, Framerate, VideoCodec, VideoProfile, VideoRange, VideoRangeType | Cache: MediaStreamsCache
        People = 1 << 3,              // Fields: All people roles (Actors, Directors, etc.) | Cache: ItemPeople
        Collections = 1 << 4,         // Fields: Collections | Cache: ItemCollections, CollectionMembershipCache
        Playlists = 1 << 5,           // Fields: Playlists | Cache: ItemPlaylists, PlaylistMembershipCache
        NextUnwatched = 1 << 6,       // Fields: NextUnwatched | Cache: NextUnwatched
        SeriesName = 1 << 7,          // Fields: SeriesName | Cache: SeriesNameById
        ParentSeriesTags = 1 << 8,    // Fields: Tags (with IncludeParentSeriesTags) | Cache: SeriesTagsById
        ParentSeriesStudios = 1 << 9, // Fields: Studios (with IncludeParentSeriesStudios) | Cache: SeriesStudiosById
        ParentSeriesGenres = 1 << 10, // Fields: Genres (with IncludeParentSeriesGenres) | Cache: SeriesGenresById
        SimilarTo = 1 << 11,          // Fields: SimilarTo | Special handling in Engine
        LastEpisodeAirDate = 1 << 12, // Fields: LastEpisodeAirDate | Cache: LastEpisodeAirDateById
        ExternalLists = 1 << 20,      // Fields: ExternalList | Cache: ExternalListData, ItemExternalLists

        // Cheap extraction groups (conditional but fast - don't trigger two-phase filtering)
        // Defined in FieldRegistry.CheapExtractionGroups
        FileInfo = 1 << 13,           // Fields: FolderPath, FileName, DateModified | No cache (file system)
        LibraryInfo = 1 << 14,        // Fields: LibraryName | Cache: LibraryNameById
        AudioMetadata = 1 << 15,      // Fields: Album, Artists, AlbumArtists | No cache (reflection, fast)
        TextContent = 1 << 16,        // Fields: Overview, ProductionLocations, RuntimeMinutes | No cache (property/reflection)
        
        // Optimization Groups: Cheap but Conditional (Tier 1)
        ItemLists = 1 << 17,          // Fields: Genres, Tags, Studios | Array allocations
        UserData = 1 << 18,           // Fields: IsFavorite, PlayCount, PlaybackStatus, LastPlayedDate | UserDataManager lookup
        Dates = 1 << 19,              // Fields: PremiereDate, DateCreated, ProductionYear, etc. | Struct copying
    }

    /// <summary>
    /// Type of field for operator and value handling.
    /// </summary>
    public enum FieldType
    {
        Text,           // String fields
        Numeric,
        Date,
        Boolean,
        List,           // IEnumerable&lt;string&gt;
        Resolution,
        Framerate,
        UserData,       // User-specific fields (PlaybackStatus, IsFavorite, etc.)
        Similarity,
        Simple,         // Predefined values (ItemType)
    }

    /// <summary>
    /// UI category for field organization in the frontend.
    /// </summary>
    public enum FieldCategory
    {
        Content,
        Video,
        Audio,
        RatingsPlayback,
        File,
        Library,
        People,
        PeopleSubFields,
        Collection,
        SimilarityComparison,
    }

    /// <summary>
    /// Complete metadata for a single field.
    /// </summary>
    public record FieldMetadata
    {
        public required string Name { get; init; }
        public required string DisplayLabel { get; init; }
        public required FieldType Type { get; init; }
        public required FieldCategory Category { get; init; }
        public ExtractionGroup ExtractionGroup { get; init; } = ExtractionGroup.None;

        /// <summary>
        /// Whether this field requires expensive extraction (API calls, reflection, database queries).
        /// Returns true if ANY bit in ExtractionGroup is not covered by CheapExtractionGroups.
        /// </summary>
        public bool IsExpensive => (ExtractionGroup & ~FieldRegistry.CheapExtractionGroups) != ExtractionGroup.None;
        public string[] AllowedOperators { get; init; } = [];
        public bool IsUserSpecific { get; init; } = false;
        public bool IsPeopleField { get; init; } = false;

        /// <summary>
        /// Operand property name (must match Operand.cs property exactly).
        /// </summary>
        public string OperandPropertyName => Name;
    }

    /// <summary>
    /// Central registry for all field definitions.
    /// Single source of truth for field metadata.
    /// </summary>
    public static class FieldRegistry
    {
        /// <summary>
        /// Global definition of cheap extraction groups.
        /// These are conditionally extracted but fast enough to run in Phase 1 filtering.
        /// Includes: FileInfo, LibraryInfo, AudioMetadata, TextContent, ItemLists, UserData, Dates.
        /// </summary>
        public const ExtractionGroup CheapExtractionGroups =
            ExtractionGroup.FileInfo | ExtractionGroup.LibraryInfo |
            ExtractionGroup.AudioMetadata | ExtractionGroup.TextContent |
            ExtractionGroup.ItemLists | ExtractionGroup.UserData | ExtractionGroup.Dates;

        // Operator arrays for reuse
        private static readonly string[] StringOperators = ["Equal", "NotEqual", "Contains", "NotContains", "IsIn", "IsNotIn", "MatchRegex"];
        private static readonly string[] MultiValueOperators = ["Contains", "NotContains", "IsIn", "IsNotIn", "MatchRegex"];
        private static readonly string[] NumericOperators = ["Equal", "NotEqual", "GreaterThan", "LessThan", "GreaterThanOrEqual", "LessThanOrEqual"];
        private static readonly string[] DateOperators = ["Equal", "NotEqual", "After", "Before", "NewerThan", "OlderThan", "Weekday"];
        private static readonly string[] BooleanOperators = ["Equal", "NotEqual"];
        private static readonly string[] SimpleOperators = ["Equal", "NotEqual"];
        private static readonly string[] SimilarityOperators = ["Equal", "Contains", "IsIn", "MatchRegex"];

        // The canonical registry - all field metadata in one place
        private static readonly Dictionary<string, FieldMetadata> _fields;

        // Derived lookup tables (computed once at startup)
        private static readonly HashSet<string> _dateFields;
        private static readonly HashSet<string> _listFields;
        private static readonly HashSet<string> _peopleFields;
        private static readonly HashSet<string> _numericFields;
        private static readonly HashSet<string> _booleanFields;
        private static readonly HashSet<string> _expensiveFields;
        private static readonly HashSet<string> _userDataFields;
        private static readonly HashSet<string> _simpleFields;
        private static readonly HashSet<string> _resolutionFields;
        private static readonly HashSet<string> _framerateFields;
        private static readonly HashSet<string> _similarityFields;
        private static readonly Dictionary<string, string[]> _fieldOperators;
        private static readonly Dictionary<FieldCategory, List<FieldMetadata>> _fieldsByCategory;
        private static readonly Dictionary<ExtractionGroup, List<string>> _fieldsByExtractionGroup;

        static FieldRegistry()
        {
            _fields = BuildFieldRegistry();

            // Build derived collections for backward compatibility
            _dateFields = BuildFieldSet(f => f.Type == FieldType.Date);
            _listFields = BuildFieldSet(f => f.Type == FieldType.List);
            _peopleFields = BuildFieldSet(f => f.IsPeopleField);
            _numericFields = BuildFieldSet(f => f.Type == FieldType.Numeric);
            _booleanFields = BuildFieldSet(f => f.Type == FieldType.Boolean);
            _expensiveFields = BuildFieldSet(f => f.IsExpensive);
            _userDataFields = BuildFieldSet(f => f.IsUserSpecific);
            _simpleFields = BuildFieldSet(f => f.Type == FieldType.Simple);
            _resolutionFields = BuildFieldSet(f => f.Type == FieldType.Resolution);
            _framerateFields = BuildFieldSet(f => f.Type == FieldType.Framerate);
            _similarityFields = BuildFieldSet(f => f.Type == FieldType.Similarity);
            _fieldOperators = _fields.ToDictionary(kv => kv.Key, kv => kv.Value.AllowedOperators, StringComparer.OrdinalIgnoreCase);
            _fieldsByCategory = BuildCategoryMap();
            _fieldsByExtractionGroup = BuildExtractionGroupMap();
        }

        private static Dictionary<string, FieldMetadata> BuildFieldRegistry()
        {
            var fields = new Dictionary<string, FieldMetadata>(StringComparer.OrdinalIgnoreCase);

            // Content Fields
            AddField(fields, "Name", "Name", FieldType.Text, FieldCategory.Content, StringOperators);
            AddField(fields, "SeriesName", "Series Name", FieldType.Text, FieldCategory.Content, StringOperators, ExtractionGroup.SeriesName);
            AddField(fields, "SimilarTo", "Similar To", FieldType.Similarity, FieldCategory.Content, SimilarityOperators, ExtractionGroup.SimilarTo);
            AddField(fields, "OfficialRating", "Parental Rating", FieldType.Text, FieldCategory.Content, StringOperators);
            AddField(fields, "CustomRating", "Custom Rating", FieldType.Text, FieldCategory.Content, StringOperators);
            AddField(fields, "Overview", "Overview", FieldType.Text, FieldCategory.Content, StringOperators, ExtractionGroup.TextContent);
            AddField(fields, "ProductionYear", "Production Year", FieldType.Numeric, FieldCategory.Content, NumericOperators, ExtractionGroup.Dates);
            AddField(fields, "ReleaseDate", "Release Date", FieldType.Date, FieldCategory.Content, DateOperators, ExtractionGroup.Dates);
            AddField(fields, "LastEpisodeAirDate", "Last Episode Air Date", FieldType.Date, FieldCategory.Content, DateOperators, ExtractionGroup.LastEpisodeAirDate);
            AddField(fields, "ProductionLocations", "Production Locations", FieldType.List, FieldCategory.Content, MultiValueOperators, ExtractionGroup.TextContent);

            // Video Fields
            AddField(fields, "Resolution", "Resolution", FieldType.Resolution, FieldCategory.Video, NumericOperators, ExtractionGroup.VideoQuality);
            AddField(fields, "Framerate", "Framerate", FieldType.Framerate, FieldCategory.Video, NumericOperators, ExtractionGroup.VideoQuality);
            AddField(fields, "VideoCodec", "Video Codec", FieldType.Text, FieldCategory.Video, StringOperators, ExtractionGroup.VideoQuality);
            AddField(fields, "VideoProfile", "Video Profile", FieldType.Text, FieldCategory.Video, StringOperators, ExtractionGroup.VideoQuality);
            AddField(fields, "VideoRange", "Video Range", FieldType.Text, FieldCategory.Video, StringOperators, ExtractionGroup.VideoQuality);
            AddField(fields, "VideoRangeType", "Video Range Type", FieldType.Text, FieldCategory.Video, StringOperators, ExtractionGroup.VideoQuality);

            // Audio Fields
            AddField(fields, "AudioLanguages", "Audio Languages", FieldType.List, FieldCategory.Audio, MultiValueOperators, ExtractionGroup.AudioLanguages);
            AddField(fields, "SubtitleLanguages", "Subtitle Languages", FieldType.List, FieldCategory.Audio, MultiValueOperators, ExtractionGroup.AudioLanguages);
            AddField(fields, "AudioBitrate", "Audio Bitrate (kbps)", FieldType.Numeric, FieldCategory.Audio, NumericOperators, ExtractionGroup.AudioQuality);
            AddField(fields, "AudioSampleRate", "Audio Sample Rate (Hz)", FieldType.Numeric, FieldCategory.Audio, NumericOperators, ExtractionGroup.AudioQuality);
            AddField(fields, "AudioBitDepth", "Audio Bit Depth", FieldType.Numeric, FieldCategory.Audio, NumericOperators, ExtractionGroup.AudioQuality);
            AddField(fields, "AudioCodec", "Audio Codec", FieldType.Text, FieldCategory.Audio, StringOperators, ExtractionGroup.AudioQuality);
            AddField(fields, "AudioProfile", "Audio Profile", FieldType.Text, FieldCategory.Audio, StringOperators, ExtractionGroup.AudioQuality);
            AddField(fields, "AudioChannels", "Audio Channels", FieldType.Numeric, FieldCategory.Audio, NumericOperators, ExtractionGroup.AudioQuality);

            // Ratings/Playback Fields (User-specific)
            AddField(fields, "CommunityRating", "Community Rating", FieldType.Numeric, FieldCategory.RatingsPlayback, NumericOperators);
            AddField(fields, "CriticRating", "Critic Rating", FieldType.Numeric, FieldCategory.RatingsPlayback, NumericOperators);
            AddField(fields, "IsFavorite", "Is Favorite", FieldType.Boolean, FieldCategory.RatingsPlayback, BooleanOperators, ExtractionGroup.UserData, isUserSpecific: true);
            AddField(fields, "PlaybackStatus", "Playback Status", FieldType.UserData, FieldCategory.RatingsPlayback, SimpleOperators, ExtractionGroup.UserData, isUserSpecific: true);
            AddField(fields, "LastPlayedDate", "Last Played", FieldType.Date, FieldCategory.RatingsPlayback, DateOperators, ExtractionGroup.UserData, isUserSpecific: true);
            AddField(fields, "NextUnwatched", "Next Unwatched", FieldType.Boolean, FieldCategory.RatingsPlayback, BooleanOperators, ExtractionGroup.NextUnwatched, isUserSpecific: true);
            AddField(fields, "PlayCount", "Play Count", FieldType.Numeric, FieldCategory.RatingsPlayback, NumericOperators, ExtractionGroup.UserData, isUserSpecific: true);
            AddField(fields, "RuntimeMinutes", "Runtime (Minutes)", FieldType.Numeric, FieldCategory.RatingsPlayback, NumericOperators, ExtractionGroup.TextContent);

            // File Fields - conditionally extracted via ExtractionGroup.FileInfo
            AddField(fields, "FileName", "File Name", FieldType.Text, FieldCategory.File, StringOperators, ExtractionGroup.FileInfo);
            AddField(fields, "FolderPath", "Folder Path", FieldType.Text, FieldCategory.File, StringOperators, ExtractionGroup.FileInfo);
            AddField(fields, "DateModified", "Date Modified", FieldType.Date, FieldCategory.File, DateOperators, ExtractionGroup.FileInfo);

            // Library Fields - conditionally extracted via ExtractionGroup.LibraryInfo
            AddField(fields, "LibraryName", "Library Name", FieldType.Text, FieldCategory.Library, StringOperators, ExtractionGroup.LibraryInfo);
            AddField(fields, "DateCreated", "Date Added to Library", FieldType.Date, FieldCategory.Library, DateOperators, ExtractionGroup.Dates);
            AddField(fields, "DateLastRefreshed", "Last Metadata Refresh", FieldType.Date, FieldCategory.Library, DateOperators, ExtractionGroup.Dates);
            AddField(fields, "DateLastSaved", "Last Database Save", FieldType.Date, FieldCategory.Library, DateOperators, ExtractionGroup.Dates);

            // Collection Fields
            AddField(fields, "Collections", "Collection name", FieldType.List, FieldCategory.Collection, MultiValueOperators, ExtractionGroup.Collections);
            AddField(fields, "Playlists", "Playlist name", FieldType.List, FieldCategory.Collection, MultiValueOperators, ExtractionGroup.Playlists);
            AddField(fields, "Genres", "Genres", FieldType.List, FieldCategory.Collection, MultiValueOperators, ExtractionGroup.ItemLists);
            AddField(fields, "Studios", "Studios", FieldType.List, FieldCategory.Collection, MultiValueOperators, ExtractionGroup.ItemLists);
            AddField(fields, "Tags", "Tags", FieldType.List, FieldCategory.Collection, MultiValueOperators, ExtractionGroup.ItemLists);
            AddField(fields, "Album", "Album", FieldType.Text, FieldCategory.Collection, StringOperators, ExtractionGroup.AudioMetadata);
            AddField(fields, "Artists", "Artists", FieldType.List, FieldCategory.Collection, MultiValueOperators, ExtractionGroup.AudioMetadata);
            AddField(fields, "AlbumArtists", "Album Artists", FieldType.List, FieldCategory.Collection, MultiValueOperators, ExtractionGroup.AudioMetadata);
            AddField(fields, "ExternalList", "External List", FieldType.List, FieldCategory.Collection, SimpleOperators, ExtractionGroup.ExternalLists);

            // Simple Fields
            AddField(fields, "ItemType", "Item Type", FieldType.Simple, FieldCategory.Content, SimpleOperators);
            AddField(fields, "ExtraType", "Extra Type", FieldType.Simple, FieldCategory.Content, SimpleOperators);

            // People Fields (all expensive - require People extraction group)
            AddPeopleField(fields, "People", "People (All)");
            AddPeopleField(fields, "Actors", "Actors");
            AddPeopleField(fields, "ActorRoles", "Actor Roles (Character Names)");
            AddPeopleField(fields, "Directors", "Directors");
            AddPeopleField(fields, "Composers", "Composers");
            AddPeopleField(fields, "Writers", "Writers");
            AddPeopleField(fields, "GuestStars", "Guest Stars");
            AddPeopleField(fields, "Producers", "Producers");
            AddPeopleField(fields, "Conductors", "Conductors");
            AddPeopleField(fields, "Lyricists", "Lyricists");
            AddPeopleField(fields, "Arrangers", "Arrangers");
            AddPeopleField(fields, "SoundEngineers", "Sound Engineers");
            AddPeopleField(fields, "Mixers", "Mixers");
            AddPeopleField(fields, "Remixers", "Remixers");
            AddPeopleField(fields, "Creators", "Creators");
            AddPeopleField(fields, "PersonArtists", "Artists (Person Role)");
            AddPeopleField(fields, "PersonAlbumArtists", "Album Artists (Person Role)");
            AddPeopleField(fields, "Authors", "Authors");
            AddPeopleField(fields, "Illustrators", "Illustrators");
            AddPeopleField(fields, "Pencilers", "Pencilers");
            AddPeopleField(fields, "Inkers", "Inkers");
            AddPeopleField(fields, "Colorists", "Colorists");
            AddPeopleField(fields, "Letterers", "Letterers");
            AddPeopleField(fields, "CoverArtists", "Cover Artists");
            AddPeopleField(fields, "Editors", "Editors");
            AddPeopleField(fields, "Translators", "Translators");

            return fields;
        }

        private static void AddField(
            Dictionary<string, FieldMetadata> fields,
            string name,
            string label,
            FieldType type,
            FieldCategory category,
            string[] operators,
            ExtractionGroup extraction = ExtractionGroup.None,
            bool isUserSpecific = false)
        {
            fields[name] = new FieldMetadata
            {
                Name = name,
                DisplayLabel = label,
                Type = type,
                Category = category,
                ExtractionGroup = extraction,
                AllowedOperators = operators,
                IsUserSpecific = isUserSpecific,
                IsPeopleField = false,
            };
        }

        private static void AddPeopleField(Dictionary<string, FieldMetadata> fields, string name, string label)
        {
            fields[name] = new FieldMetadata
            {
                Name = name,
                DisplayLabel = label,
                Type = FieldType.List,
                Category = FieldCategory.PeopleSubFields,
                ExtractionGroup = ExtractionGroup.People,
                AllowedOperators = MultiValueOperators,
                IsUserSpecific = false,
                IsPeopleField = true,
            };
        }

        // Helper method for building derived collections
        private static HashSet<string> BuildFieldSet(Func<FieldMetadata, bool> predicate)
        {
            return new HashSet<string>(
                _fields.Values.Where(predicate).Select(f => f.Name),
                StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<FieldCategory, List<FieldMetadata>> BuildCategoryMap()
        {
            return _fields.Values
                .GroupBy(f => f.Category)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        private static Dictionary<ExtractionGroup, List<string>> BuildExtractionGroupMap()
        {
            var map = new Dictionary<ExtractionGroup, List<string>>();
            foreach (ExtractionGroup group in Enum.GetValues<ExtractionGroup>())
            {
                if (group == ExtractionGroup.None) continue;
                map[group] = _fields.Values
                    .Where(f => f.ExtractionGroup.HasFlag(group))
                    .Select(f => f.Name)
                    .ToList();
            }

            return map;
        }

        // Public API - field lookup
        public static FieldMetadata? GetField(string fieldName)
        {
            return _fields.TryGetValue(fieldName, out var field) ? field : null;
        }

        public static ExtractionGroup GetExtractionGroup(string fieldName)
        {
            return _fields.TryGetValue(fieldName, out var field) ? field.ExtractionGroup : ExtractionGroup.None;
        }

        public static IEnumerable<FieldMetadata> GetFieldsByCategory(FieldCategory category)
        {
            // Return defensive copy to prevent external mutation of internal state
            return _fieldsByCategory.TryGetValue(category, out var fields) ? fields.ToArray() : [];
        }

        public static IEnumerable<string> GetFieldsInExtractionGroup(ExtractionGroup group)
        {
            // Return defensive copy to prevent external mutation of internal state
            return _fieldsByExtractionGroup.TryGetValue(group, out var fields) ? fields.ToArray() : [];
        }

        // Public API - backward compatibility with FieldDefinitions
        public static bool IsDateField(string fieldName) => _dateFields.Contains(fieldName);
        public static bool IsListField(string fieldName) => _listFields.Contains(fieldName);
        public static bool IsPeopleField(string fieldName) => _peopleFields.Contains(fieldName);
        public static bool IsNumericField(string fieldName) => _numericFields.Contains(fieldName);
        public static bool IsBooleanField(string fieldName) => _booleanFields.Contains(fieldName);
        public static bool IsExpensiveField(string fieldName) => _expensiveFields.Contains(fieldName);
        public static bool IsUserDataField(string fieldName) => _userDataFields.Contains(fieldName);
        public static bool IsSimpleField(string fieldName) => _simpleFields.Contains(fieldName);
        public static bool IsResolutionField(string fieldName) => _resolutionFields.Contains(fieldName);
        public static bool IsFramerateField(string fieldName) => _framerateFields.Contains(fieldName);
        public static bool IsSimilarityField(string fieldName) => _similarityFields.Contains(fieldName);

        // Public API - backward compatibility with Operators
        public static string[] GetOperatorsForField(string fieldName)
        {
            return _fieldOperators.TryGetValue(fieldName, out var ops) ? ops : [];
        }

        public static Dictionary<string, string[]> GetFieldOperatorsDictionary()
        {
            return new Dictionary<string, string[]>(_fieldOperators, StringComparer.OrdinalIgnoreCase);
        }

        // Public API - for HashSet access (backward compatibility)
        public static HashSet<string> GetPeopleRoleFields()
        {
            return new HashSet<string>(_peopleFields, StringComparer.OrdinalIgnoreCase);
        }

        public static HashSet<string> GetDateFields()
        {
            return new HashSet<string>(_dateFields, StringComparer.OrdinalIgnoreCase);
        }

        public static HashSet<string> GetListFields()
        {
            return new HashSet<string>(_listFields, StringComparer.OrdinalIgnoreCase);
        }

        public static HashSet<string> GetNumericFields()
        {
            return new HashSet<string>(_numericFields, StringComparer.OrdinalIgnoreCase);
        }

        public static HashSet<string> GetBooleanFields()
        {
            return new HashSet<string>(_booleanFields, StringComparer.OrdinalIgnoreCase);
        }

        public static HashSet<string> GetSimpleFields()
        {
            return new HashSet<string>(_simpleFields, StringComparer.OrdinalIgnoreCase);
        }

        public static HashSet<string> GetResolutionFields()
        {
            return new HashSet<string>(_resolutionFields, StringComparer.OrdinalIgnoreCase);
        }

        public static HashSet<string> GetFramerateFields()
        {
            return new HashSet<string>(_framerateFields, StringComparer.OrdinalIgnoreCase);
        }

        public static HashSet<string> GetUserDataFields()
        {
            return new HashSet<string>(_userDataFields, StringComparer.OrdinalIgnoreCase);
        }

        public static HashSet<string> GetSimilarityFields()
        {
            return new HashSet<string>(_similarityFields, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets all available field names for API responses.
        /// </summary>
        public static string[] GetAllFieldNames()
        {
            return [.. _fields.Keys];
        }

        /// <summary>
        /// Returns field data formatted for the UI API endpoint.
        /// Matches the existing structure from SharedFieldDefinitions.GetAvailableFields().
        /// </summary>
        public static object GetAvailableFieldsForApi()
        {
            return new
            {
                ContentFields = GetFieldsForCategoryAsApiFormat(FieldCategory.Content)
                    .Where(f => f.Value != "ItemType") // ItemType excluded from UI fields
                    .ToArray(),
                VideoFields = GetFieldsForCategoryAsApiFormat(FieldCategory.Video),
                AudioFields = GetFieldsForCategoryAsApiFormat(FieldCategory.Audio),
                RatingsPlaybackFields = GetFieldsForCategoryAsApiFormat(FieldCategory.RatingsPlayback),
                FileFields = GetFieldsForCategoryAsApiFormat(FieldCategory.File),
                LibraryFields = GetFieldsForCategoryAsApiFormat(FieldCategory.Library),
                PeopleFields = new[] { new { Value = "People", Label = "People" } },
                PeopleSubFields = GetFieldsForCategoryAsApiFormat(FieldCategory.PeopleSubFields)
                    .ToArray(),
                CollectionFields = GetFieldsForCategoryAsApiFormat(FieldCategory.Collection),
                SimilarityComparisonFields = GetSimilarityComparisonFieldsForApi(),
                Operators = Constants.Operators.AllOperators,
                FieldOperators = GetFieldOperatorsDictionary(),
                OrderOptions = GetOrderOptionsForApi(),
            };
        }

        private static IEnumerable<dynamic> GetFieldsForCategoryAsApiFormat(FieldCategory category)
        {
            return GetFieldsByCategory(category)
                .Select(f => new { Value = f.Name, Label = f.DisplayLabel });
        }

        private static object[] GetSimilarityComparisonFieldsForApi()
        {
            // These are specific fields used for similarity comparison, not all fields in a category
            return
            [
                new { Value = "Genre", Label = "Genre" },
                new { Value = "Tags", Label = "Tags" },
                new { Value = "Actors", Label = "Actors" },
                new { Value = "ActorRoles", Label = "Actor Roles (Character Names)" },
                new { Value = "Writers", Label = "Writers" },
                new { Value = "Producers", Label = "Producers" },
                new { Value = "Directors", Label = "Directors" },
                new { Value = "Studios", Label = "Studios" },
                new { Value = "Audio Languages", Label = "Audio Languages" },
                new { Value = "Name", Label = "Name" },
                new { Value = "Production Year", Label = "Production Year" },
                new { Value = "Parental Rating", Label = "Parental Rating" },
            ];
        }

        /// <summary>
        /// Returns a compact set of common sort options for the API.
        /// This is intentionally a subset - the backend OrderFactory supports 38+ order types
        /// (Runtime, SeriesName, Album/Artist, Episode/Season/Track numbers, Rule Block Order, etc.)
        /// and the frontend SORT_OPTIONS provides context-aware filtering based on media type and rules.
        /// See: OrderFactory.OrderMap, Configuration/config-sorts.js SORT_OPTIONS
        /// </summary>
        private static object[] GetOrderOptionsForApi()
        {
            return
            [
                new { Value = "NoOrder", Label = "No Order" },
                new { Value = "Random", Label = "Random" },
                new { Value = "Name Ascending", Label = "Name Ascending" },
                new { Value = "Name Descending", Label = "Name Descending" },
                new { Value = "ProductionYear Ascending", Label = "Production Year Ascending" },
                new { Value = "ProductionYear Descending", Label = "Production Year Descending" },
                new { Value = "DateCreated Ascending", Label = "Date Created Ascending" },
                new { Value = "DateCreated Descending", Label = "Date Created Descending" },
                new { Value = "ReleaseDate Ascending", Label = "Release Date Ascending" },
                new { Value = "ReleaseDate Descending", Label = "Release Date Descending" },
                new { Value = "CommunityRating Ascending", Label = "Community Rating Ascending" },
                new { Value = "CommunityRating Descending", Label = "Community Rating Descending" },
                new { Value = "Similarity Ascending", Label = "Similarity Ascending" },
                new { Value = "Similarity Descending", Label = "Similarity Descending" },
                new { Value = "PlayCount (owner) Ascending", Label = "Play Count (owner) Ascending" },
                new { Value = "PlayCount (owner) Descending", Label = "Play Count (owner) Descending" },
                new { Value = "External List Order Ascending", Label = "External List Order Ascending" },
                new { Value = "External List Order Descending", Label = "External List Order Descending" },
            ];
        }
    }
}
