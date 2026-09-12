using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Fetches list items from the MDBList API (api.mdblist.com).
    /// Supports URLs like https://mdblist.com/lists/{username}/{listname}
    /// and official lists like https://mdblist.com/lists/official/{movies|shows}/{listname}
    /// </summary>
    public partial class MdbListProvider : IExternalListProvider
    {
        private const string ApiBaseUrl = "https://api.mdblist.com";
        private const int PageSize = 1000;

        private const string JustWatchChartSlug = "justwatch-streaming-charts";

        // Website chart filters share their names with the chart API's query parameters.
        private static readonly string[] JustWatchChartParams = ["locale", "country", "rank", "period", "provider", "genre", "subgenre"];

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<MdbListProvider> _logger;

        public MdbListProvider(IHttpClientFactory httpClientFactory, ILogger<MdbListProvider> logger)
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
                && (uri.Host.Equals("mdblist.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".mdblist.com", StringComparison.OrdinalIgnoreCase));
        }

        /// <inheritdoc />
        public async Task<ExternalListResult> FetchListAsync(string url, CancellationToken cancellationToken, int maxItems = 0)
        {
            var result = new ExternalListResult();

            var apiKey = Plugin.Instance?.Configuration?.MdbListApiKey ?? string.Empty;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("MDBList API key is not configured. Set the API key in Settings > External Lists.");
            }

            var (username, listname, mediaType) = ParseMdbListUrl(url);
            if (username == null || listname == null)
            {
                _logger.LogWarning("Could not parse MDBList URL: {Url}. Expected format: https://mdblist.com/lists/{{username}}/{{listname}} or https://mdblist.com/lists/official/{{movies|shows}}/{{listname}}", url);
                return result;
            }

            _logger.LogInformation("Fetching external list from MDBList: {Username}/{ListName} (mediatype: {MediaType})", username, listname, mediaType ?? "all");

            if (username == "official" && mediaType != null && string.Equals(listname, JustWatchChartSlug, StringComparison.OrdinalIgnoreCase))
            {
                return await FetchJustWatchChartAsync(url, mediaType, apiKey, cancellationToken).ConfigureAwait(false);
            }

            var mediaTypeFilter = mediaType == null ? string.Empty : "&mediatype=" + mediaType;

            var httpClient = _httpClientFactory.CreateClient("MdbList");
            int offset = 0;
            int totalFetched = 0;
            int position = 0;

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var apiUrl = $"{ApiBaseUrl}/lists/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(listname)}/items?apikey={Uri.EscapeDataString(apiKey)}&limit={PageSize}&offset={offset}{mediaTypeFilter}";

                    using var response = await httpClient.GetAsync(apiUrl, cancellationToken).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("MDBList API returned {StatusCode} for list {Username}/{ListName}", response.StatusCode, username, listname);
                        break;
                    }

                    var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                    // The API can return either an array directly or a wrapper object with movies/shows arrays
                    int itemsInPage = 0;

                    try
                    {
                        // Try parsing as wrapper object first (newer API format)
                        var wrapper = JsonSerializer.Deserialize<MdbListResponse>(json);
                        if (wrapper?.Movies != null)
                        {
                            foreach (var item in wrapper.Movies)
                            {
                                AddItemIds(item, result, ref position, ExternalListItemKind.Movie);
                                itemsInPage++;
                            }
                        }

                        if (wrapper?.Shows != null)
                        {
                            foreach (var item in wrapper.Shows)
                            {
                                AddItemIds(item, result, ref position, ExternalListItemKind.Show);
                                itemsInPage++;
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Try parsing as flat array (older API format / some endpoints)
                        try
                        {
                            var items = JsonSerializer.Deserialize<MdbListItem[]>(json);
                            if (items != null)
                            {
                                foreach (var item in items)
                                {
                                    AddItemIds(item, result, ref position, GetItemKind(item.MediaType));
                                    itemsInPage++;
                                }
                            }
                        }
                        catch (JsonException ex2)
                        {
                            _logger.LogWarning(ex2, "Failed to parse MDBList API response for {Username}/{ListName}", username, listname);
                            break;
                        }
                    }

                    totalFetched += itemsInPage;

                    // Stop early if we have enough items
                    if (maxItems > 0 && position >= maxItems)
                    {
                        break;
                    }

                    // If we got fewer items than page size, we've reached the end
                    if (itemsInPage < PageSize)
                    {
                        break;
                    }

                    offset += PageSize;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("MDBList fetch cancelled for {Username}/{ListName}", username, listname);
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "HTTP error fetching MDBList {Username}/{ListName}", username, listname);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error fetching MDBList {Username}/{ListName}", username, listname);
            }

            result.TotalItems = totalFetched;
            result.IsComplete = maxItems <= 0 || totalFetched < maxItems;
            _logger.LogInformation("Fetched {Count} items from MDBList {Username}/{ListName} (IMDb: {ImdbCount}, TMDB: {TmdbCount}, TVDB: {TvdbCount})",
                totalFetched, username, listname, result.ImdbIds.Count, result.TmdbIds.Count, result.TvdbIds.Count);

            return result;
        }

        /// <summary>
        /// The official JustWatch page on mdblist.com is a live chart (top 20 for a country/period/provider/genre)
        /// served by a dedicated endpoint. /lists/official/justwatch-streaming-charts/items is an aggregate across
        /// every chart and does not match what the page shows, so the web URL's filters are forwarded instead.
        /// </summary>
        private async Task<ExternalListResult> FetchJustWatchChartAsync(string url, string mediaType, string apiKey, CancellationToken cancellationToken)
        {
            var result = new ExternalListResult();
            var apiUrl = $"{ApiBaseUrl}/justwatch/streaming-charts/{mediaType}?apikey={Uri.EscapeDataString(apiKey)}{BuildJustWatchChartQuery(url)}";
            var httpClient = _httpClientFactory.CreateClient("MdbList");
            int position = 0;

            try
            {
                using var response = await httpClient.GetAsync(apiUrl, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("MDBList API returned {StatusCode} for JustWatch {MediaType} chart", response.StatusCode, mediaType);
                    return result;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var chart = JsonSerializer.Deserialize<MdbListJustWatchChartResponse>(json);
                foreach (var item in chart?.Results ?? [])
                {
                    AddItemIds(item, result, ref position, GetItemKind(item.MediaType ?? mediaType));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                _logger.LogWarning(ex, "Error fetching MDBList JustWatch {MediaType} chart", mediaType);
            }

            result.TotalItems = position;
            result.IsComplete = true;
            _logger.LogInformation("Fetched {Count} items from MDBList JustWatch {MediaType} chart (IMDb: {ImdbCount}, TMDB: {TmdbCount}, TVDB: {TvdbCount})",
                position, mediaType, result.ImdbIds.Count, result.TmdbIds.Count, result.TvdbIds.Count);

            return result;
        }

        /// <summary>
        /// Forwards the chart filters from the web URL query string (locale, rank, provider, genre, ...)
        /// as "&amp;key=value" pairs. Unknown and empty parameters are dropped.
        /// </summary>
        internal static string BuildJustWatchChartQuery(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query))
            {
                return string.Empty;
            }

            var query = new StringBuilder();
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = Uri.UnescapeDataString(pair[..separatorIndex]).ToLowerInvariant();
                var value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
                if (string.IsNullOrWhiteSpace(value) || Array.IndexOf(JustWatchChartParams, key) < 0)
                {
                    continue;
                }

                query.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
            }

            return query.ToString();
        }

        private static void AddItemIds(MdbListItem item, ExternalListResult result, ref int position, ExternalListItemKind kind)
        {
            // Add IMDb ID from top-level or nested ids (TryAdd keeps first/lowest position for duplicates)
            var imdbId = item.ImdbId ?? item.Ids?.Imdb;
            var tvdbId = item.TvdbId ?? item.Ids?.Tvdb;
            result.AddProviderIds(kind, imdbId, item.Ids?.Tmdb, tvdbId, position);
            position++;
        }

        private static ExternalListItemKind GetItemKind(string? mediaType)
        {
            return mediaType?.Trim().ToLowerInvariant() switch
            {
                "movie" or "movies" => ExternalListItemKind.Movie,
                "show" or "shows" or "series" or "tv" => ExternalListItemKind.Show,
                "episode" or "episodes" => ExternalListItemKind.Episode,
                _ => ExternalListItemKind.Unknown
            };
        }

        /// <summary>
        /// Parses a MDBList URL into username, list name and optional media type filter.
        /// Supports: https://mdblist.com/lists/{username}/{listname}
        ///           https://mdblist.com/lists/official/{movies|shows}/{listname}
        /// Official lists live under the reserved "official" username and combine movies and shows;
        /// the web URL's media segment maps to the API's mediatype query filter.
        /// </summary>
        internal static (string? Username, string? ListName, string? MediaType) ParseMdbListUrl(string url)
        {
            var official = MdbListOfficialUrlPattern().Match(url);
            if (official.Success)
            {
                var mediaType = official.Groups[1].Value.Equals("movies", StringComparison.OrdinalIgnoreCase) ? "movie" : "show";
                return ("official", official.Groups[2].Value, mediaType);
            }

            var match = MdbListUrlPattern().Match(url);
            if (match.Success)
            {
                return (match.Groups[1].Value, match.Groups[2].Value, null);
            }

            return (null, null, null);
        }

        [GeneratedRegex(@"mdblist\.com/lists/official/(movies|shows)/([^/?#]+)", RegexOptions.IgnoreCase)]
        private static partial Regex MdbListOfficialUrlPattern();

        [GeneratedRegex(@"mdblist\.com/lists/([^/]+)/([^/?#]+)", RegexOptions.IgnoreCase)]
        private static partial Regex MdbListUrlPattern();
    }
}
