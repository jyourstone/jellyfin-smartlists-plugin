namespace Jellyfin.Plugin.SmartLists.Api.Controllers
{
    /// <summary>
    /// Shared field definitions for both admin and user controllers.
    /// Centralizes field definitions to follow DRY principle.
    /// </summary>
    public static class SharedFieldDefinitions
    {
        /// <summary>
        /// Gets the available fields structure for smart playlist rules.
        /// </summary>
        /// <returns>Object containing all field categories, operators, and order options.</returns>
        public static object GetAvailableFields()
        {
            return new
            {
                ContentFields = new[]
                {
                    new { Value = "Name", Label = "Name" },
                    new { Value = "SeriesName", Label = "Series Name" },
                    new { Value = "SimilarTo", Label = "Similar To" },
                    new { Value = "OfficialRating", Label = "Parental Rating" },
                    new { Value = "CustomRating", Label = "Custom Rating" },
                    new { Value = "Overview", Label = "Overview" },
                    new { Value = "ProductionYear", Label = "Production Year" },
                    new { Value = "ReleaseDate", Label = "Release Date" },
                    new { Value = "LastEpisodeAirDate", Label = "Last Episode Air Date" },
                    new { Value = "ProductionLocations", Label = "Production Location" }
                    // Note: ItemType (Media Type) is intentionally excluded from UI fields
                    // because users select media type (Audio/Video) before creating rules
                },
                VideoFields = new[]
                {
                    new { Value = "Resolution", Label = "Resolution" },
                    new { Value = "Framerate", Label = "Framerate" },
                    new { Value = "VideoCodec", Label = "Video Codec" },
                    new { Value = "VideoProfile", Label = "Video Profile" },
                    new { Value = "VideoRange", Label = "Video Range" },
                    new { Value = "VideoRangeType", Label = "Video Range Type" },
                },
                AudioFields = new[]
                {
                    new { Value = "AudioLanguages", Label = "Audio Languages" },
                    new { Value = "SubtitleLanguages", Label = "Subtitle Languages" },
                    new { Value = "AudioBitrate", Label = "Audio Bitrate (kbps)" },
                    new { Value = "AudioSampleRate", Label = "Audio Sample Rate (Hz)" },
                    new { Value = "AudioBitDepth", Label = "Audio Bit Depth" },
                    new { Value = "AudioCodec", Label = "Audio Codec" },
                    new { Value = "AudioProfile", Label = "Audio Profile" },
                    new { Value = "AudioChannels", Label = "Audio Channels" },
                },
                RatingsPlaybackFields = new[]
                {
                    new { Value = "CommunityRating", Label = "Community Rating" },
                    new { Value = "CriticRating", Label = "Critic Rating" },
                    new { Value = "IsFavorite", Label = "Is Favorite" },
                    new { Value = "PlaybackStatus", Label = "Playback Status" },
                    new { Value = "LastPlayedDate", Label = "Last Played" },
                    new { Value = "NextUnwatched", Label = "Next Unwatched" },
                    new { Value = "PlayCount", Label = "Play Count" },
                    new { Value = "RuntimeMinutes", Label = "Runtime (Minutes)" },
                },

                FileFields = new[]
                {
                    new { Value = "FileName", Label = "File Name" },
                    new { Value = "FolderPath", Label = "Folder Path" },
                    new { Value = "DateModified", Label = "Date Modified" },
                },
                LibraryFields = new[]
                {
                    new { Value = "DateCreated", Label = "Date Added to Library" },
                    new { Value = "DateLastRefreshed", Label = "Last Metadata Refresh" },
                    new { Value = "DateLastSaved", Label = "Last Database Save" },
                },
                PeopleFields = new[]
                {
                    new { Value = "People", Label = "People" },
                },
                PeopleSubFields = new[]
                {
                    new { Value = "People", Label = "People (All)" },
                    new { Value = "Actors", Label = "Actors" },
                    new { Value = "ActorRoles", Label = "Actor Roles (Character Names)" },
                    new { Value = "Directors", Label = "Directors" },
                    new { Value = "Composers", Label = "Composers" },
                    new { Value = "Writers", Label = "Writers" },
                    new { Value = "GuestStars", Label = "Guest Stars" },
                    new { Value = "Producers", Label = "Producers" },
                    new { Value = "Conductors", Label = "Conductors" },
                    new { Value = "Lyricists", Label = "Lyricists" },
                    new { Value = "Arrangers", Label = "Arrangers" },
                    new { Value = "SoundEngineers", Label = "Sound Engineers" },
                    new { Value = "Mixers", Label = "Mixers" },
                    new { Value = "Remixers", Label = "Remixers" },
                    new { Value = "Creators", Label = "Creators" },
                    new { Value = "PersonArtists", Label = "Artists (Person Role)" },
                    new { Value = "PersonAlbumArtists", Label = "Album Artists (Person Role)" },
                    new { Value = "Authors", Label = "Authors" },
                    new { Value = "Illustrators", Label = "Illustrators" },
                    new { Value = "Pencilers", Label = "Pencilers" },
                    new { Value = "Inkers", Label = "Inkers" },
                    new { Value = "Colorists", Label = "Colorists" },
                    new { Value = "Letterers", Label = "Letterers" },
                    new { Value = "CoverArtists", Label = "Cover Artists" },
                    new { Value = "Editors", Label = "Editors" },
                    new { Value = "Translators", Label = "Translators" },
                },
                CollectionFields = new[]
                {
                    new { Value = "Collections", Label = "Collection name" },
                    new { Value = "Playlists", Label = "Playlist name" },
                    new { Value = "Genres", Label = "Genres" },
                    new { Value = "Studios", Label = "Studios" },
                    new { Value = "Tags", Label = "Tags" },
                    new { Value = "Album", Label = "Album" },
                    new { Value = "Artists", Label = "Artists" },
                    new { Value = "AlbumArtists", Label = "Album Artists" },
                },
                SimilarityComparisonFields = new[]
                {
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
                },
                Operators = Core.Constants.Operators.AllOperators,
                FieldOperators = Core.Constants.Operators.GetFieldOperatorsDictionary(),
                OrderOptions = new[]
                {
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
                }
            };
        }
    }
}
