using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Response wrapper for the ListenBrainz playlist endpoint (GET /1/playlist/{mbid}).
    /// </summary>
    public class ListenBrainzPlaylistResponse
    {
        [JsonPropertyName("playlist")]
        public ListenBrainzPlaylist? Playlist { get; set; }
    }

    /// <summary>
    /// Response wrapper for the ListenBrainz created-for endpoint
    /// (GET /1/user/{user}/playlists/createdfor). Entries are newest-first
    /// and always have an empty track array.
    /// </summary>
    public class ListenBrainzCreatedForResponse
    {
        [JsonPropertyName("playlists")]
        public ListenBrainzPlaylistWrapper[]? Playlists { get; set; }
    }

    /// <summary>
    /// Wrapper around a playlist entry in ListenBrainz list endpoints.
    /// </summary>
    public class ListenBrainzPlaylistWrapper
    {
        [JsonPropertyName("playlist")]
        public ListenBrainzPlaylist? Playlist { get; set; }
    }

    /// <summary>
    /// A JSPF playlist object from the ListenBrainz API.
    /// </summary>
    public class ListenBrainzPlaylist
    {
        /// <summary>
        /// Gets or sets the playlist identifier URL: https://listenbrainz.org/playlist/{mbid}.
        /// </summary>
        [JsonPropertyName("identifier")]
        public string? Identifier { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("track")]
        public ListenBrainzTrack[]? Tracks { get; set; }

        [JsonPropertyName("extension")]
        public ListenBrainzPlaylistExtension? Extension { get; set; }
    }

    /// <summary>
    /// The JSPF extension container on a playlist.
    /// </summary>
    public class ListenBrainzPlaylistExtension
    {
        [JsonPropertyName("https://musicbrainz.org/doc/jspf#playlist")]
        public ListenBrainzPlaylistExtensionData? MusicBrainzPlaylist { get; set; }
    }

    /// <summary>
    /// The MusicBrainz playlist extension payload.
    /// </summary>
    public class ListenBrainzPlaylistExtensionData
    {
        [JsonPropertyName("additional_metadata")]
        public ListenBrainzPlaylistAdditionalMetadata? AdditionalMetadata { get; set; }
    }

    /// <summary>
    /// Additional metadata on a playlist.
    /// </summary>
    public class ListenBrainzPlaylistAdditionalMetadata
    {
        [JsonPropertyName("algorithm_metadata")]
        public ListenBrainzAlgorithmMetadata? AlgorithmMetadata { get; set; }
    }

    /// <summary>
    /// Metadata about the algorithm that generated a playlist.
    /// </summary>
    public class ListenBrainzAlgorithmMetadata
    {
        /// <summary>
        /// Gets or sets the machine-readable playlist type, e.g. "weekly-jams" or "weekly-exploration".
        /// </summary>
        [JsonPropertyName("source_patch")]
        public string? SourcePatch { get; set; }
    }

    /// <summary>
    /// A JSPF track from a ListenBrainz playlist.
    /// </summary>
    public class ListenBrainzTrack
    {
        /// <summary>
        /// Gets or sets the identifier URLs; recording MBIDs appear as
        /// https://musicbrainz.org/recording/{mbid} entries.
        /// </summary>
        [JsonPropertyName("identifier")]
        public string[]? Identifier { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        /// <summary>
        /// Gets or sets the artist credit string.
        /// </summary>
        [JsonPropertyName("creator")]
        public string? Creator { get; set; }

        [JsonPropertyName("extension")]
        public ListenBrainzTrackExtension? Extension { get; set; }
    }

    /// <summary>
    /// The JSPF extension container on a track.
    /// </summary>
    public class ListenBrainzTrackExtension
    {
        [JsonPropertyName("https://musicbrainz.org/doc/jspf#track")]
        public ListenBrainzTrackExtensionData? MusicBrainzTrack { get; set; }
    }

    /// <summary>
    /// The MusicBrainz track extension payload.
    /// </summary>
    public class ListenBrainzTrackExtensionData
    {
        [JsonPropertyName("additional_metadata")]
        public ListenBrainzTrackAdditionalMetadata? AdditionalMetadata { get; set; }
    }

    /// <summary>
    /// Additional metadata on a track.
    /// </summary>
    public class ListenBrainzTrackAdditionalMetadata
    {
        [JsonPropertyName("artists")]
        public ListenBrainzArtistCredit[]? Artists { get; set; }
    }

    /// <summary>
    /// A single artist credit on a track.
    /// </summary>
    public class ListenBrainzArtistCredit
    {
        [JsonPropertyName("artist_mbid")]
        public string? ArtistMbid { get; set; }

        [JsonPropertyName("artist_credit_name")]
        public string? ArtistCreditName { get; set; }
    }
}
