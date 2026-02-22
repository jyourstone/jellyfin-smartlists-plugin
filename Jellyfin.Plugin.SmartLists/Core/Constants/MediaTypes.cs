using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.SmartLists.Core.Constants
{
    /// <summary>
    /// Centralized media type constants to prevent typos and improve maintainability
    /// </summary>
    public static class MediaTypes
    {
        // TV Types
        public const string Episode = nameof(Episode);

        // Series media type: Not supported in Playlists (Jellyfin expands to episodes causing playback issues)
        // but supported in Collections (Collections don't play sequentially, so no expansion issues)
        public const string Series = nameof(Series);

        // Movie Types
        public const string Movie = nameof(Movie);

        // Audio Types
        public const string Audio = nameof(Audio);

        // MusicAlbum media type: Not supported in Playlists (Jellyfin expands to individual tracks)
        // but supported in Collections (same pattern as Series)
        public const string MusicAlbum = nameof(MusicAlbum);

        // Music Video Types
        public const string MusicVideo = nameof(MusicVideo);

        // Home Video and Photo Types (matching Jellyfin's backend types)
        public const string Video = nameof(Video);
        public const string Photo = nameof(Photo);

        // Book Types
        public const string Book = nameof(Book);
        public const string AudioBook = nameof(AudioBook);

        // Fallback Type
        public const string Unknown = nameof(Unknown);

        /// <summary>
        /// Centralized mapping between BaseItemKind and MediaTypes.
        /// This is the single source of truth for all media type mappings.
        /// </summary>
        public static readonly Dictionary<BaseItemKind, string> BaseItemKindToMediaType = new()
        {
            { BaseItemKind.Episode, Episode },
            { BaseItemKind.Movie, Movie },
            { BaseItemKind.Audio, Audio },
            { BaseItemKind.MusicVideo, MusicVideo },
            { BaseItemKind.Video, Video },
            { BaseItemKind.Photo, Photo },
            { BaseItemKind.Book, Book },
            { BaseItemKind.AudioBook, AudioBook },
            // Series: Supported in Collections, not in Playlists
            { BaseItemKind.Series, Series },
            // MusicAlbum: Supported in Collections, not in Playlists
            { BaseItemKind.MusicAlbum, MusicAlbum }
        };

        /// <summary>
        /// Reverse mapping from MediaTypes to BaseItemKind.
        /// </summary>
        public static readonly Dictionary<string, BaseItemKind> MediaTypeToBaseItemKind = new()
        {
            { Episode, BaseItemKind.Episode },
            { Movie, BaseItemKind.Movie },
            { Audio, BaseItemKind.Audio },
            { MusicVideo, BaseItemKind.MusicVideo },
            { Video, BaseItemKind.Video },
            { Photo, BaseItemKind.Photo },
            { Book, BaseItemKind.Book },
            { AudioBook, BaseItemKind.AudioBook },
            // Series: Supported in Collections, not in Playlists
            { Series, BaseItemKind.Series },
            // MusicAlbum: Supported in Collections, not in Playlists
            { MusicAlbum, BaseItemKind.MusicAlbum }
        };

        /// <summary>
        /// Gets all supported media types as an array (includes Series for Collections support)
        /// </summary>
        public static readonly string[] All = [.. BaseItemKindToMediaType
            .Select(static kvp => kvp.Value)];

        /// <summary>
        /// Gets non-audio media types (everything except Audio and AudioBook)
        /// </summary>
        public static readonly string[] NonAudioTypes = [Movie, Episode, MusicVideo, Video, Photo, Book];

        /// <summary>
        /// Gets audio-only media types (Audio, AudioBook)
        /// </summary>
        public static readonly string[] AudioOnly = [Audio, AudioBook];

        /// <summary>
        /// Gets book media types (Book, AudioBook)
        /// </summary>
        public static readonly string[] BookTypes = [Book, AudioBook];

        /// <summary>
        /// Gets TV media types (Episode and Series)
        /// </summary>
        public static readonly string[] TV = [Episode, Series];

        /// <summary>
        /// Gets music-related media types (Audio, AudioBook, MusicVideo)
        /// </summary>
        public static readonly string[] MusicRelated = [Audio, AudioBook, MusicVideo, MusicAlbum];

        /// <summary>
        /// Gets media types that can have video streams (excludes Photo, Audio, Book, AudioBook)
        /// </summary>
        public static readonly string[] VideoStreamCapable = [Movie, Episode, MusicVideo, Video];

        // HashSet variants for O(1) membership checks (performance optimization)

        /// <summary>
        /// HashSet variant of AudioOnly for O(1) membership checks
        /// </summary>
        public static readonly HashSet<string> AudioOnlySet = new(AudioOnly, StringComparer.Ordinal);

        /// <summary>
        /// HashSet variant of NonAudioTypes for O(1) membership checks
        /// </summary>
        public static readonly HashSet<string> NonAudioSet = new(NonAudioTypes, StringComparer.Ordinal);

        /// <summary>
        /// HashSet variant of BookTypes for O(1) membership checks
        /// </summary>
        public static readonly HashSet<string> BookTypesSet = new(BookTypes, StringComparer.Ordinal);

        /// <summary>
        /// HashSet variant of MusicRelated for O(1) membership checks
        /// </summary>
        public static readonly HashSet<string> MusicRelatedSet = new(MusicRelated, StringComparer.Ordinal);

        /// <summary>
        /// HashSet variant of VideoStreamCapable for O(1) membership checks
        /// </summary>
        public static readonly HashSet<string> VideoStreamCapableSet = new(VideoStreamCapable, StringComparer.Ordinal);

        /// <summary>
        /// Gets BaseItemKind array for audio-only content (derived from centralized mapping)
        /// </summary>
        public static BaseItemKind[] GetAudioOnlyBaseItemKinds() =>
            AudioOnly.Select(mediaType => MediaTypeToBaseItemKind[mediaType]).ToArray();

        /// <summary>
        /// Gets BaseItemKind array for non-audio content (derived from centralized mapping)
        /// </summary>
        public static BaseItemKind[] GetNonAudioBaseItemKinds() =>
            NonAudioTypes.Select(mediaType => MediaTypeToBaseItemKind[mediaType]).ToArray();

    }
}
