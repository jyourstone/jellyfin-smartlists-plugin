using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Fetches music tracks from the ListenBrainz API (JSPF playlists).
    /// Supports static playlist URLs like https://listenbrainz.org/playlist/{mbid}
    /// and recurring recommendation feed URLs like
    /// https://listenbrainz.org/syndication-feed/user/{user}/recommendations?recommendation_type=weekly-jams,
    /// which are re-resolved to the latest generated playlist on every refresh.
    /// </summary>
    public partial class ListenBrainzListProvider : IExternalListProvider
    {
        private const string ApiBaseUrl = "https://api.listenbrainz.org";
        private const int CreatedForPageSize = 25;
        private const int MaxRateLimitWaitSeconds = 10;

        private const string InvalidUrlMessage =
            "Invalid ListenBrainz URL. Paste either a playlist URL (https://listenbrainz.org/playlist/{mbid}) " +
            "or a syndication feed URL from the ListenBrainz recommendations page " +
            "(https://listenbrainz.org/syndication-feed/user/{user}/recommendations?recommendation_type=weekly-jams).";

        private static readonly string UserAgent =
            "JellyfinSmartLists/" + (typeof(ListenBrainzListProvider).Assembly.GetName().Version?.ToString() ?? "1.0")
            + " (+https://github.com/jyourstone/jellyfin-smartlists-plugin)";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ListenBrainzListProvider> _logger;

        public ListenBrainzListProvider(IHttpClientFactory httpClientFactory, ILogger<ListenBrainzListProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <inheritdoc />
        public bool CanHandle(string url)
        {
            return !string.IsNullOrWhiteSpace(url)
                && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && (uri.Host.Equals("listenbrainz.org", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("www.listenbrainz.org", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("api.listenbrainz.org", StringComparison.OrdinalIgnoreCase));
        }

        /// <inheritdoc />
        public async Task<ExternalListResult> FetchListAsync(string url, CancellationToken cancellationToken, int maxItems = 0)
        {
            var result = new ExternalListResult();
            var httpClient = _httpClientFactory.CreateClient("ListenBrainz");

            var mbid = await ResolvePlaylistMbidAsync(httpClient, url, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Fetching ListenBrainz playlist {Mbid} for {Url}", mbid, url);

            var requestUrl = $"{ApiBaseUrl}/1/playlist/{mbid}?fetch_metadata=true";
            using var response = await SendWithRetryAsync(httpClient, requestUrl, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    $"ListenBrainz returned {(int)response.StatusCode} for playlist {mbid}. " +
                    "The playlist may be private — if so, configure a ListenBrainz user token in Settings > External Lists.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ListenBrainz API returned {response.StatusCode} for playlist {mbid}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var tracks = JsonSerializer.Deserialize<ListenBrainzPlaylistResponse>(json)?.Playlist?.Tracks ?? [];

            int position = 0;
            foreach (var track in tracks)
            {
                // Stop early if we have enough items
                if (maxItems > 0 && position >= maxItems)
                {
                    break;
                }

                result.AddMusicTrack(GetRecordingMbid(track), track.Title, GetArtistNames(track), position);
                position++;
            }

            result.TotalItems = position;
            result.IsComplete = maxItems <= 0 || tracks.Length <= maxItems;
            _logger.LogInformation(
                "Fetched {Count} tracks from ListenBrainz playlist {Mbid} (recording MBIDs: {MbidCount}, title/artist keys: {KeyCount})",
                position, mbid, result.MusicRecordingIds.Count, result.MusicTitleArtistIds.Count);

            return result;
        }

        /// <summary>
        /// Resolves the URL to a playlist MBID: static playlist URLs carry the MBID directly,
        /// syndication feed URLs are resolved to the latest generated playlist for the user.
        /// </summary>
        private async Task<string> ResolvePlaylistMbidAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException(InvalidUrlMessage);
            }

            var staticMbid = TryParseStaticPlaylistMbid(uri);
            if (staticMbid != null)
            {
                return staticMbid;
            }

            var feedMatch = SyndicationFeedPattern().Match(uri.AbsolutePath);
            if (feedMatch.Success)
            {
                var user = Uri.UnescapeDataString(feedMatch.Groups[1].Value);
                var type = GetQueryParameter(uri, "recommendation_type");
                if (!string.IsNullOrWhiteSpace(type))
                {
                    return await ResolveCreatedForPlaylistMbidAsync(httpClient, user, type, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException(InvalidUrlMessage);
        }

        /// <summary>
        /// Resolves the latest generated playlist of the given type (e.g. "weekly-jams") for a user
        /// via the created-for endpoint. Weekly playlists expire, so the MBID is re-resolved on every refresh.
        /// </summary>
        private async Task<string> ResolveCreatedForPlaylistMbidAsync(HttpClient httpClient, string user, string type, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Resolving latest '{Type}' playlist for ListenBrainz user {User}", type, user);

            var requestUrl = $"{ApiBaseUrl}/1/user/{Uri.EscapeDataString(user)}/playlists/createdfor?count={CreatedForPageSize}";
            using var response = await SendWithRetryAsync(httpClient, requestUrl, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ListenBrainz API returned {response.StatusCode} for user '{user}' playlists");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var playlists = JsonSerializer.Deserialize<ListenBrainzCreatedForResponse>(json)?.Playlists ?? [];

            // Entries are newest-first; the first matching source_patch is the latest playlist of that type
            foreach (var entry in playlists)
            {
                var playlist = entry.Playlist;
                var sourcePatch = playlist?.Extension?.MusicBrainzPlaylist?.AdditionalMetadata?.AlgorithmMetadata?.SourcePatch;
                if (!string.Equals(sourcePatch, type, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var mbid = GetIdentifierTailMbid(playlist!.Identifier);
                if (mbid != null)
                {
                    return mbid;
                }
            }

            throw new InvalidOperationException(
                $"No '{type}' playlist found for user '{user}' on ListenBrainz — the list may not be generated yet.");
        }

        /// <summary>
        /// Sends a GET request, retrying once on 429 after waiting for the advertised
        /// rate-limit reset window (capped at <see cref="MaxRateLimitWaitSeconds"/> seconds).
        /// </summary>
        private async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient httpClient, string requestUrl, CancellationToken cancellationToken)
        {
            var response = await SendAsync(httpClient, requestUrl, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            var waitSeconds = MaxRateLimitWaitSeconds;
            if (response.Headers.TryGetValues("X-RateLimit-Reset-In", out var resetValues)
                && int.TryParse(string.Join("", resetValues), CultureInfo.InvariantCulture, out var resetIn)
                && resetIn >= 0)
            {
                waitSeconds = Math.Min(resetIn, MaxRateLimitWaitSeconds);
            }

            response.Dispose();
            _logger.LogWarning("ListenBrainz API rate limited, retrying in {WaitSeconds}s: {Url}", waitSeconds, requestUrl);
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken).ConfigureAwait(false);

            response = await SendAsync(httpClient, requestUrl, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                response.Dispose();
                throw new InvalidOperationException("ListenBrainz API rate limit exceeded — try again later.");
            }

            return response;
        }

        /// <summary>
        /// Sends a GET request with the ListenBrainz headers. The optional user token
        /// (Settings > External Lists) is sent as "Authorization: Token {token}" to allow private playlists.
        /// </summary>
        private static async Task<HttpResponseMessage> SendAsync(HttpClient httpClient, string requestUrl, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("User-Agent", UserAgent);

            var token = Plugin.Instance?.Configuration?.ListenBrainzUserToken;
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
            }

            return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Parses a static playlist URL into its MBID:
        /// https://listenbrainz.org/playlist/{mbid}, https://listenbrainz.org/playlist/?path={mbid}
        /// or https://api.listenbrainz.org/1/playlist/{mbid}. Returns null when the URL is not a playlist URL.
        /// </summary>
        private static string? TryParseStaticPlaylistMbid(Uri uri)
        {
            var match = PlaylistPathPattern().Match(uri.AbsolutePath);
            if (match.Success)
            {
                return match.Groups[1].Value.ToLowerInvariant();
            }

            // Query variant: https://listenbrainz.org/playlist/?path={mbid}
            if (uri.AbsolutePath.TrimEnd('/').EndsWith("/playlist", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = GetQueryParameter(uri, "path");
                if (!string.IsNullOrEmpty(candidate) && IsMbid(candidate))
                {
                    return candidate.ToLowerInvariant();
                }
            }

            return null;
        }

        /// <summary>
        /// Extracts the recording MBID from the first track identifier containing "/recording/".
        /// </summary>
        private static string? GetRecordingMbid(ListenBrainzTrack track)
        {
            if (track.Identifier == null)
            {
                return null;
            }

            foreach (var identifier in track.Identifier)
            {
                if (string.IsNullOrEmpty(identifier) || !identifier.Contains("/recording/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var mbid = GetIdentifierTailMbid(identifier);
                if (mbid != null)
                {
                    return mbid;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the artist credit names for a track: the MusicBrainz extension artists when present,
        /// otherwise the JSPF creator string as a single-element fallback.
        /// </summary>
        private static List<string> GetArtistNames(ListenBrainzTrack track)
        {
            var names = new List<string>();

            var artists = track.Extension?.MusicBrainzTrack?.AdditionalMetadata?.Artists;
            if (artists != null)
            {
                foreach (var artist in artists)
                {
                    if (!string.IsNullOrWhiteSpace(artist.ArtistCreditName))
                    {
                        names.Add(artist.ArtistCreditName);
                    }
                }
            }

            if (names.Count == 0 && !string.IsNullOrWhiteSpace(track.Creator))
            {
                names.Add(track.Creator);
            }

            return names;
        }

        /// <summary>
        /// Extracts the MBID from the tail segment of an identifier URL
        /// (e.g. https://listenbrainz.org/playlist/{mbid}). Returns null when the tail is not a UUID.
        /// </summary>
        private static string? GetIdentifierTailMbid(string? identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                return null;
            }

            var tail = identifier.TrimEnd('/');
            var slashIndex = tail.LastIndexOf('/');
            if (slashIndex >= 0)
            {
                tail = tail[(slashIndex + 1)..];
            }

            return IsMbid(tail) ? tail.ToLowerInvariant() : null;
        }

        /// <summary>
        /// Checks whether a value has the UUID shape of a MusicBrainz ID.
        /// </summary>
        private static bool IsMbid(string value)
        {
            return Guid.TryParseExact(value, "D", out _);
        }

        /// <summary>
        /// Gets a query string parameter value from a URI, or null when absent.
        /// </summary>
        private static string? GetQueryParameter(Uri uri, string name)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
                var key = separatorIndex < 0 ? pair : pair[..separatorIndex];
                if (string.Equals(Uri.UnescapeDataString(key), name, StringComparison.OrdinalIgnoreCase))
                {
                    return separatorIndex < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
                }
            }

            return null;
        }

        /// <summary>
        /// Matches playlist URL paths: /playlist/{mbid} (web) or /1/playlist/{mbid} (API host).
        /// </summary>
        [GeneratedRegex(@"^/(?:1/)?playlist/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/?$", RegexOptions.IgnoreCase)]
        private static partial Regex PlaylistPathPattern();

        /// <summary>
        /// Matches syndication feed URL paths: /syndication-feed/user/{user}/recommendations
        /// (the recommendation type is carried in the query string).
        /// </summary>
        [GeneratedRegex(@"^/syndication-feed/user/([^/?#]+)/recommendations/?$", RegexOptions.IgnoreCase)]
        private static partial Regex SyndicationFeedPattern();
    }
}
