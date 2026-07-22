using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Describes the media kind represented by an external-list ID.
    /// </summary>
    public enum ExternalListItemKind
    {
        /// <summary>
        /// The provider did not expose a media kind for the ID.
        /// </summary>
        Unknown,

        /// <summary>
        /// A movie item.
        /// </summary>
        Movie,

        /// <summary>
        /// A TV series/show item.
        /// </summary>
        Show,

        /// <summary>
        /// A TV episode item.
        /// </summary>
        Episode,

        /// <summary>
        /// A music track (audio) item.
        /// </summary>
        Music
    }

    /// <summary>
    /// Represents the result of fetching an external list — provider IDs mapped to their 0-based position in the list.
    /// </summary>
    public class ExternalListResult
    {
        /// <summary>
        /// Gets the IMDb IDs (e.g., "tt1234567") mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> ImdbIds { get; init; } = [];

        /// <summary>
        /// Gets the TMDB IDs as strings (e.g., "917496") mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> TmdbIds { get; init; } = [];

        /// <summary>
        /// Gets the TVDB IDs as strings (e.g., "421968") mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> TvdbIds { get; init; } = [];

        /// <summary>
        /// Gets unknown-kind IMDb IDs mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> UnknownImdbIds { get; init; } = [];

        /// <summary>
        /// Gets unknown-kind TMDB IDs mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> UnknownTmdbIds { get; init; } = [];

        /// <summary>
        /// Gets unknown-kind TVDB IDs mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> UnknownTvdbIds { get; init; } = [];

        /// <summary>
        /// Gets movie IMDb IDs mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> MovieImdbIds { get; init; } = [];

        /// <summary>
        /// Gets movie TMDB IDs mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> MovieTmdbIds { get; init; } = [];

        /// <summary>
        /// Gets movie TVDB IDs mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> MovieTvdbIds { get; init; } = [];

        /// <summary>
        /// Gets show IMDb IDs mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> ShowImdbIds { get; init; } = [];

        /// <summary>
        /// Gets show TMDB IDs mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> ShowTmdbIds { get; init; } = [];

        /// <summary>
        /// Gets show TVDB IDs mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> ShowTvdbIds { get; init; } = [];

        /// <summary>
        /// Gets episode IMDb IDs mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> EpisodeImdbIds { get; init; } = [];

        /// <summary>
        /// Gets episode TMDB IDs mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> EpisodeTmdbIds { get; init; } = [];

        /// <summary>
        /// Gets episode TVDB IDs mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> EpisodeTvdbIds { get; init; } = [];

        /// <summary>
        /// Gets the lowercased MusicBrainz recording MBIDs mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> MusicRecordingIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the normalized "title|artist" keys (see <see cref="MusicMatchKey.TitleArtistKey"/>)
        /// mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> MusicTitleArtistIds { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets the total number of items in the external list.
        /// </summary>
        public int TotalItems { get; set; }

        /// <summary>
        /// Gets or sets whether the result contains all items from the external list.
        /// When false, the result was truncated by a maxItems limit and should not be
        /// reused for requests that need the full list.
        /// </summary>
        public bool IsComplete { get; set; } = true;

        private bool HasKindSpecificIds =>
            UnknownImdbIds.Count > 0 || UnknownTmdbIds.Count > 0 || UnknownTvdbIds.Count > 0 ||
            MovieImdbIds.Count > 0 || MovieTmdbIds.Count > 0 || MovieTvdbIds.Count > 0 ||
            ShowImdbIds.Count > 0 || ShowTmdbIds.Count > 0 || ShowTvdbIds.Count > 0 ||
            EpisodeImdbIds.Count > 0 || EpisodeTmdbIds.Count > 0 || EpisodeTvdbIds.Count > 0;

        /// <summary>
        /// Adds provider IDs to the aggregate and kind-specific ID buckets.
        /// </summary>
        /// <param name="kind">The media kind represented by the IDs.</param>
        /// <param name="imdbId">The IMDb ID, if available.</param>
        /// <param name="tmdbId">The TMDB ID, if available.</param>
        /// <param name="tvdbId">The TVDB ID, if available.</param>
        /// <param name="position">The 0-based position in the external list.</param>
        public void AddProviderIds(ExternalListItemKind kind, string? imdbId, int? tmdbId, int? tvdbId, int position)
        {
            var tmdb = tmdbId is > 0 ? tmdbId.Value.ToString(CultureInfo.InvariantCulture) : null;
            var tvdb = tvdbId is > 0 ? tvdbId.Value.ToString(CultureInfo.InvariantCulture) : null;

            AddToDictionaries(ImdbIds, TmdbIds, TvdbIds, imdbId, tmdb, tvdb, position);

            var (imdbIds, tmdbIds, tvdbIds) = GetDictionaries(kind);
            AddToDictionaries(imdbIds, tmdbIds, tvdbIds, imdbId, tmdb, tvdb, position);
        }

        /// <summary>
        /// Adds provider IDs to the aggregate and kind-specific ID buckets.
        /// </summary>
        /// <param name="kind">The media kind represented by the IDs.</param>
        /// <param name="imdbId">The IMDb ID, if available.</param>
        /// <param name="tmdbId">The TMDB ID, if available.</param>
        /// <param name="tvdbId">The TVDB ID, if available.</param>
        /// <param name="position">The 0-based position in the external list.</param>
        public void AddProviderIds(ExternalListItemKind kind, string? imdbId, string? tmdbId, string? tvdbId, int position)
        {
            AddToDictionaries(ImdbIds, TmdbIds, TvdbIds, imdbId, tmdbId, tvdbId, position);

            var (imdbIds, tmdbIds, tvdbIds) = GetDictionaries(kind);
            AddToDictionaries(imdbIds, tmdbIds, tvdbIds, imdbId, tmdbId, tvdbId, position);
        }

        /// <summary>
        /// Tries to find the position for a provider ID in the requested media-kind bucket.
        /// </summary>
        /// <param name="kind">The media kind to match.</param>
        /// <param name="imdbId">The IMDb ID, if available.</param>
        /// <param name="tmdbId">The TMDB ID, if available.</param>
        /// <param name="tvdbId">The TVDB ID, if available.</param>
        /// <param name="position">The matched position, when found.</param>
        /// <returns>True if any provider ID matched.</returns>
        public bool TryGetPosition(ExternalListItemKind kind, string? imdbId, string? tmdbId, string? tvdbId, out int position)
        {
            if (!HasKindSpecificIds)
            {
                return TryGetPosition(ImdbIds, TmdbIds, TvdbIds, imdbId, tmdbId, tvdbId, out position);
            }

            var (imdbIds, tmdbIds, tvdbIds) = GetDictionaries(kind);
            if (TryGetPosition(imdbIds, tmdbIds, tvdbIds, imdbId, tmdbId, tvdbId, out position))
            {
                return true;
            }

            return TryGetPosition(UnknownImdbIds, UnknownTmdbIds, UnknownTvdbIds, imdbId, tmdbId, tvdbId, out position);
        }

        /// <summary>
        /// Adds a music track to the recording-MBID and normalized title|artist buckets.
        /// </summary>
        /// <param name="recordingMbid">The MusicBrainz recording MBID, if available.</param>
        /// <param name="title">The track title, if available.</param>
        /// <param name="artistNames">The artist credit names, if available.</param>
        /// <param name="position">The 0-based position in the external list.</param>
        public void AddMusicTrack(string? recordingMbid, string? title, IEnumerable<string>? artistNames, int position)
        {
            if (!string.IsNullOrEmpty(recordingMbid))
            {
                AddKeepingLowestPosition(MusicRecordingIds, recordingMbid.ToLowerInvariant(), position);
            }

            if (artistNames == null || MusicMatchKey.NormalizeTitle(title).Length == 0)
            {
                return;
            }

            foreach (var artist in artistNames)
            {
                // Skip artists that normalize to empty (e.g. "!!!") — a "title|" key would
                // cross-match unrelated artists sharing the same title.
                if (MusicMatchKey.NormalizeArtist(artist).Length == 0)
                {
                    continue;
                }

                AddKeepingLowestPosition(MusicTitleArtistIds, MusicMatchKey.TitleArtistKey(title, artist), position);
            }
        }

        /// <summary>
        /// Tries to find the position for a music track by recording MBID first, then by every
        /// normalized title|artist key. Returns the minimum position across all matches.
        /// </summary>
        /// <param name="recordingMbid">The MusicBrainz recording MBID, if available.</param>
        /// <param name="title">The track title, if available.</param>
        /// <param name="artistNames">The artist credit names, if available.</param>
        /// <param name="position">The matched position, when found.</param>
        /// <returns>True if the recording MBID or any title|artist key matched.</returns>
        public bool TryGetMusicPosition(string? recordingMbid, string? title, IEnumerable<string>? artistNames, out int position)
        {
            var found = false;
            position = -1;

            if (!string.IsNullOrEmpty(recordingMbid) && MusicRecordingIds.TryGetValue(recordingMbid, out var recordingPosition))
            {
                position = recordingPosition;
                found = true;
            }

            if (artistNames == null || MusicMatchKey.NormalizeTitle(title).Length == 0)
            {
                return found;
            }

            foreach (var artist in artistNames)
            {
                // Mirror AddMusicTrack: artists that normalize to empty never have keys stored.
                if (MusicMatchKey.NormalizeArtist(artist).Length == 0)
                {
                    continue;
                }

                if (MusicTitleArtistIds.TryGetValue(MusicMatchKey.TitleArtistKey(title, artist), out var keyPosition) &&
                    (!found || keyPosition < position))
                {
                    position = keyPosition;
                    found = true;
                }
            }

            return found;
        }

        private static void AddKeepingLowestPosition(Dictionary<string, int> ids, string key, int position)
        {
            if (!ids.TryAdd(key, position) && position < ids[key])
            {
                ids[key] = position;
            }
        }

        private (Dictionary<string, int> ImdbIds, Dictionary<string, int> TmdbIds, Dictionary<string, int> TvdbIds) GetDictionaries(ExternalListItemKind kind)
        {
            return kind switch
            {
                ExternalListItemKind.Movie => (MovieImdbIds, MovieTmdbIds, MovieTvdbIds),
                ExternalListItemKind.Show => (ShowImdbIds, ShowTmdbIds, ShowTvdbIds),
                ExternalListItemKind.Episode => (EpisodeImdbIds, EpisodeTmdbIds, EpisodeTvdbIds),
                _ => (UnknownImdbIds, UnknownTmdbIds, UnknownTvdbIds)
            };
        }

        private static void AddToDictionaries(
            Dictionary<string, int> imdbIds,
            Dictionary<string, int> tmdbIds,
            Dictionary<string, int> tvdbIds,
            string? imdbId,
            string? tmdbId,
            string? tvdbId,
            int position)
        {
            if (!string.IsNullOrEmpty(imdbId))
            {
                imdbIds.TryAdd(imdbId, position);
            }

            if (!string.IsNullOrEmpty(tmdbId))
            {
                tmdbIds.TryAdd(tmdbId, position);
            }

            if (!string.IsNullOrEmpty(tvdbId))
            {
                tvdbIds.TryAdd(tvdbId, position);
            }
        }

        private static bool TryGetPosition(
            Dictionary<string, int> imdbIds,
            Dictionary<string, int> tmdbIds,
            Dictionary<string, int> tvdbIds,
            string? imdbId,
            string? tmdbId,
            string? tvdbId,
            out int position)
        {
            if (!string.IsNullOrEmpty(imdbId) && imdbIds.TryGetValue(imdbId, out position))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(tmdbId) && tmdbIds.TryGetValue(tmdbId, out position))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(tvdbId) && tvdbIds.TryGetValue(tvdbId, out position))
            {
                return true;
            }

            position = -1;
            return false;
        }
    }

    /// <summary>
    /// Interface for fetching list items from an external source (MDBList, Trakt, etc.).
    /// </summary>
    public interface IExternalListProvider
    {
        /// <summary>
        /// Checks whether this provider can handle the given URL.
        /// </summary>
        /// <param name="url">The external list URL.</param>
        /// <returns>True if this provider can fetch the list.</returns>
        bool CanHandle(string url);

        /// <summary>
        /// Fetches items from the external list and returns their provider IDs.
        /// Each provider reads its own credentials from plugin configuration.
        /// </summary>
        /// <param name="url">The external list URL.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="maxItems">Maximum number of items to fetch (0 = unlimited). When set,
        /// providers may stop fetching early and mark the result as incomplete.</param>
        /// <returns>An <see cref="ExternalListResult"/> containing the provider IDs of items in the list.</returns>
        Task<ExternalListResult> FetchListAsync(string url, CancellationToken cancellationToken, int maxItems = 0);
    }
}
