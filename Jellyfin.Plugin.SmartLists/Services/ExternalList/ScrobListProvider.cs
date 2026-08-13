using System;
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
    /// Fetches list items from a self-hosted Scrob instance (github.com/ellite/scrob) via its
    /// Astro frontend proxy at /api/proxy/lists/{id}. The server URL and API key are admin-configured
    /// (Settings > External Lists) — CanHandle only matches URLs whose scheme/host/port equal the
    /// configured server, so a non-admin user can never point a rule at an arbitrary host.
    /// Supports URLs like {server}/list/{id}, {server}/lists/{id}, and {server}/api/proxy/lists/{id}.
    /// </summary>
    public partial class ScrobListProvider : IExternalListProvider
    {
        private static readonly string UserAgent =
            "JellyfinSmartLists/" + (typeof(ScrobListProvider).Assembly.GetName().Version?.ToString() ?? "1.0")
            + " (+https://github.com/jyourstone/jellyfin-smartlists-plugin)";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ScrobListProvider> _logger;

        public ScrobListProvider(IHttpClientFactory httpClientFactory, ILogger<ScrobListProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <inheritdoc />
        public bool CanHandle(string url)
        {
            return MatchesConfiguredServer(url, Plugin.Instance?.Configuration?.ScrobServerUrl);
        }

        /// <summary>
        /// Checks whether a user-supplied list URL points at the admin-configured Scrob server.
        /// This is the security gate for this provider: Scrob's host comes from configuration rather
        /// than from a fixed public domain, so a rule may only reference the configured origin —
        /// scheme, host and port must all match.
        /// </summary>
        /// <param name="url">The user-supplied external list URL.</param>
        /// <param name="configuredServerUrl">The admin-configured Scrob server URL.</param>
        /// <returns>True when the URL targets the configured server.</returns>
        internal static bool MatchesConfiguredServer(string url, string? configuredServerUrl)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(configuredServerUrl) || !Uri.TryCreate(configuredServerUrl, UriKind.Absolute, out var configuredUri))
            {
                return false;
            }

            return string.Equals(uri.Scheme, configuredUri.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(uri.Host, configuredUri.Host, StringComparison.OrdinalIgnoreCase)
                && uri.Port == configuredUri.Port;
        }

        /// <inheritdoc />
        public async Task<ExternalListResult> FetchListAsync(string url, CancellationToken cancellationToken, int maxItems = 0)
        {
            var result = new ExternalListResult();

            var serverUrl = Plugin.Instance?.Configuration?.ScrobServerUrl;
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                throw new InvalidOperationException(
                    "Scrob server URL is not configured. Set the server URL in Settings > External Lists.");
            }

            var apiKey = Plugin.Instance?.Configuration?.ScrobApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException(
                    "Scrob API key is not configured. Set the API key in Settings > External Lists.");
            }

            var listId = ParseListId(url);
            if (listId == null)
            {
                throw new InvalidOperationException(
                    "Invalid Scrob list URL. Supported formats: {server}/list/{id}, {server}/lists/{id}, " +
                    "{server}/api/proxy/lists/{id}.");
            }

            _logger.LogInformation("Fetching Scrob list {ListId}", listId);

            // Always request against the admin-configured host, never the user-supplied URL's host.
            var requestUrl = $"{serverUrl.TrimEnd('/')}/api/proxy/lists/{listId.Value.ToString(CultureInfo.InvariantCulture)}";
            var httpClient = _httpClientFactory.CreateClient("Scrob");

            int position = 0;
            HttpStatusCode? failureStatus = null;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation("User-Agent", ExternalListUserAgent.Resolve(UserAgent));
                request.Headers.Add("X-Api-Key", apiKey);

                using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // Recorded here and thrown after the try block: the catch-all below would
                    // otherwise swallow it, and an empty-but-complete result silently empties the
                    // list (or, for a NotEqual rule, matches the whole library) with no warning.
                    failureStatus = response.StatusCode;
                }
                else
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    // A missing items collection means this is not a Scrob list response (an error
                    // envelope, another app on the configured URL, ...). Treating it as an empty list
                    // would be the same silent failure as a parse error, so make it one. A real list
                    // with no items still deserializes to an empty array and passes through.
                    var items = JsonSerializer.Deserialize<ScrobListResponse>(json)?.Items
                        ?? throw new JsonException("The Scrob response did not contain an items collection.");

                    foreach (var item in items)
                    {
                        var media = item.Media;
                        var kind = GetItemKind(media?.Type);

                        // Scrob lists can also hold people (its UI has an add-to-list button on person
                        // pages). A TMDB person ID shares the numeric range of movie/series IDs, and the
                        // Unknown bucket is a cross-kind fallback for every library item, so storing one
                        // would drag an unrelated title into the list. Skip unrecognised kinds instead,
                        // still advancing the position so the remaining items keep their list order.
                        if (media == null || kind == ExternalListItemKind.Unknown)
                        {
                            position++;
                            continue;
                        }

                        // For episodes Scrob resolved from TVDB (no TMDB counterpart) tmdb_id holds the
                        // TVDB *episode* ID, flagged by tvdb_sourced — route it to the TVDB bucket.
                        var isTvdbEpisode = kind == ExternalListItemKind.Episode && media.TvdbSourced;
                        var tmdbId = isTvdbEpisode ? null : media.TmdbId;
                        var tvdbId = isTvdbEpisode
                            ? media.TmdbId
                            : (kind == ExternalListItemKind.Show ? media.ShowTvdbId : null);

                        result.AddProviderIds(kind, null, tmdbId, tvdbId, position);
                        position++;

                        // Stop early if we have enough items
                        if (maxItems > 0 && position >= maxItems)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Scrob fetch cancelled for list {ListId}", listId);
                throw;
            }
            catch (JsonException ex)
            {
                // Same reasoning as the failure status below: an empty-but-complete result silently
                // empties the list (or, for a NotEqual rule, matches the whole library) with no warning.
                throw new InvalidOperationException(
                    $"Scrob returned a response for list {listId} that could not be read. Check that the "
                    + "Scrob Server URL in Settings > External Lists points at Scrob itself.", ex);
            }
            catch (HttpRequestException)
            {
                // Same reasoning, and the same as TraktListProvider: an unreachable server must not be
                // reported as an empty list. Anything else unexpected propagates for the same reason —
                // ExternalListService logs it and records a refresh warning.
                throw;
            }

            if (failureStatus != null)
            {
                // Throwing (rather than returning empty) is what gets the reason in front of the
                // user: ExternalListService only records a refresh warning when a provider throws.
                throw new InvalidOperationException(DescribeFailure(failureStatus.Value, listId.Value));
            }

            result.TotalItems = position;
            result.IsComplete = maxItems <= 0 || position < maxItems;
            _logger.LogInformation(
                "Fetched {Count} items from Scrob list {ListId} (TMDB: {TmdbCount}, TVDB: {TvdbCount})",
                position, listId, result.TmdbIds.Count, result.TvdbIds.Count);

            return result;
        }

        /// <summary>
        /// Turns a Scrob error status into a message that says what the user actually needs to fix.
        /// </summary>
        private static string DescribeFailure(HttpStatusCode statusCode, int listId)
        {
            return statusCode switch
            {
                HttpStatusCode.Unauthorized =>
                    "Scrob rejected the API key. Check the key in Settings > External Lists.",

                // Scrob's list endpoint treats an unrecognised API key as an anonymous visitor rather
                // than rejecting it, so a wrong or revoked key also surfaces here as 403 — name both causes.
                HttpStatusCode.Forbidden =>
                    $"Scrob denied access to list {listId}. Either the API key is wrong or revoked, or the list "
                    + "is private and not owned by that Scrob account. Check the key in Settings > External Lists.",
                HttpStatusCode.NotFound =>
                    $"Scrob list {listId} was not found. Check the list ID and the configured server URL.",

                // Redirects are not followed: the API key travels in a custom header, which .NET replays
                // verbatim to the redirect target (unlike Authorization, which it strips).
                _ when (int)statusCode is >= 300 and < 400 =>
                    $"The configured Scrob server URL redirects elsewhere ({(int)statusCode}). Set the Scrob Server "
                    + "URL in Settings > External Lists to the address that serves Scrob directly, so the API key is "
                    + "never sent to another host.",
                _ => $"Scrob API returned {statusCode} for list {listId}."
            };
        }

        private static ExternalListItemKind GetItemKind(string? mediaType)
        {
            return mediaType?.Trim().ToLowerInvariant() switch
            {
                "movie" => ExternalListItemKind.Movie,
                "series" => ExternalListItemKind.Show,
                "episode" => ExternalListItemKind.Episode,
                _ => ExternalListItemKind.Unknown
            };
        }

        /// <summary>
        /// Parses the list ID out of a Scrob URL path: /list/{id}, /lists/{id}, or /api/proxy/lists/{id},
        /// optionally behind a reverse-proxy sub-path (e.g. /scrob/list/{id}).
        /// Returns null when the URL is not one of those forms or the ID is not a positive integer.
        /// </summary>
        internal static int? ParseListId(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var match = ListPathPattern().Match(uri.AbsolutePath);
            if (!match.Success)
            {
                return null;
            }

            return int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id
                : null;
        }

        /// <summary>
        /// Matches /list/{id}, /lists/{id}, and /api/proxy/lists/{id}. A leading sub-path is tolerated
        /// so a proxied deployment ({server}/scrob/list/{id}) parses: the host is already pinned by
        /// <see cref="MatchesConfiguredServer"/> and the request URL is rebuilt from the configured
        /// server, so only the ID is taken from the user string.
        /// </summary>
        [GeneratedRegex(@"(?:^|/)(?:api/proxy/)?lists?/(\d+)/?$", RegexOptions.IgnoreCase)]
        private static partial Regex ListPathPattern();
    }
}
