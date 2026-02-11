using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Fetches list items from the Trakt API.
    /// Supports user lists, watchlists, and chart/trending endpoints.
    /// URLs like https://trakt.tv/users/{user}/lists/{list} or https://trakt.tv/movies/trending
    /// </summary>
    public partial class TraktListProvider : IExternalListProvider
    {
        private const string ApiBaseUrl = "https://api.trakt.tv";
        private const int PageSize = 100;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TraktListProvider> _logger;

        public TraktListProvider(IHttpClientFactory httpClientFactory, ILogger<TraktListProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <inheritdoc />
        public bool CanHandle(string url)
        {
            return !string.IsNullOrWhiteSpace(url)
                && url.Contains("trakt.tv", StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public async Task<ExternalListResult> FetchListAsync(string url, string apiKey, CancellationToken cancellationToken)
        {
            var clientId = Plugin.Instance?.Configuration?.TraktClientId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new InvalidOperationException(
                    "Trakt client ID is not configured. Set the client ID in Settings > External Lists.");
            }

            var result = new ExternalListResult();

            // Determine which API endpoint to call based on the URL
            var apiPath = ResolveApiPath(url);
            if (apiPath == null)
            {
                throw new InvalidOperationException(
                    "Invalid Trakt URL. Supported formats: https://trakt.tv/users/{user}/lists/{list}, " +
                    "https://trakt.tv/users/{user}/watchlist, " +
                    "https://trakt.tv/movies/trending, https://trakt.tv/shows/popular, etc.");
            }

            _logger.LogInformation("Fetching Trakt list: {Url} -> {ApiPath}", url, apiPath);

            var httpClient = _httpClientFactory.CreateClient("TraktList");
            int page = 1;
            int totalFetched = 0;

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var requestUrl = $"{ApiBaseUrl}{apiPath}?page={page}&limit={PageSize}&extended=full";

                    using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.Add("trakt-api-version", "2");
                    request.Headers.Add("trakt-api-key", clientId);
                    request.Headers.Add("User-Agent", "JellyfinSmartLists/1.0");

                    using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        if (totalFetched == 0)
                        {
                            throw new HttpRequestException(
                                $"Trakt API returned {response.StatusCode} for {apiPath}");
                        }

                        _logger.LogWarning("Trakt API returned {StatusCode} on page {Page}, stopping pagination", response.StatusCode, page);
                        break;
                    }

                    var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var items = JsonSerializer.Deserialize<TraktListItem[]>(json);

                    if (items == null || items.Length == 0)
                    {
                        break;
                    }

                    foreach (var item in items)
                    {
                        AddItemIds(item, result);
                        totalFetched++;
                    }

                    // Check pagination headers
                    if (response.Headers.TryGetValues("X-Pagination-Page-Count", out var pageCountValues))
                    {
                        var pageCountStr = string.Join("", pageCountValues);
                        if (int.TryParse(pageCountStr, CultureInfo.InvariantCulture, out var totalPages) && page >= totalPages)
                        {
                            break;
                        }
                    }
                    else if (items.Length < PageSize)
                    {
                        break;
                    }

                    page++;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Trakt fetch cancelled for {Url}", url);
                throw;
            }
            catch (HttpRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error fetching Trakt list: {Url}", url);
            }

            result.TotalItems = totalFetched;
            _logger.LogInformation(
                "Fetched {Count} items from Trakt {Url} (IMDb: {ImdbCount}, TMDB: {TmdbCount}, TVDB: {TvdbCount})",
                totalFetched, url, result.ImdbIds.Count, result.TmdbIds.Count, result.TvdbIds.Count);

            return result;
        }

        private static void AddItemIds(TraktListItem item, ExternalListResult result)
        {
            // Items can be wrapped (list items) or direct (trending/popular)
            var ids = item.Movie?.Ids ?? item.Show?.Ids ?? item.Ids;
            if (ids == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(ids.Imdb))
            {
                result.ImdbIds.Add(ids.Imdb);
            }

            if (ids.Tmdb is int tmdbId and > 0)
            {
                result.TmdbIds.Add(tmdbId.ToString(CultureInfo.InvariantCulture));
            }

            if (ids.Tvdb is int tvdbId and > 0)
            {
                result.TvdbIds.Add(tvdbId.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Resolves a trakt.tv URL to the corresponding API path.
        /// </summary>
        private static string? ResolveApiPath(string url)
        {
            // User list: https://trakt.tv/users/{user}/lists/{list}
            var userListMatch = UserListPattern().Match(url);
            if (userListMatch.Success)
            {
                var user = userListMatch.Groups[1].Value;
                var list = userListMatch.Groups[2].Value;
                return $"/users/{Uri.EscapeDataString(user)}/lists/{Uri.EscapeDataString(list)}/items";
            }

            // User watchlist: https://trakt.tv/users/{user}/watchlist
            var watchlistMatch = WatchlistPattern().Match(url);
            if (watchlistMatch.Success)
            {
                var user = watchlistMatch.Groups[1].Value;
                return $"/users/{Uri.EscapeDataString(user)}/watchlist";
            }

            // Chart endpoints: https://trakt.tv/movies/trending, /shows/popular, etc.
            var chartMatch = ChartPattern().Match(url);
            if (chartMatch.Success)
            {
                var mediaType = chartMatch.Groups[1].Value.ToLowerInvariant();
                var chartType = chartMatch.Groups[2].Value.ToLowerInvariant();

                // Map URL chart types to API paths
                return chartType switch
                {
                    "trending" => $"/{mediaType}/trending",
                    "popular" => $"/{mediaType}/popular",
                    "watched" => $"/{mediaType}/watched/weekly",
                    "played" => $"/{mediaType}/played/weekly",
                    "collected" => $"/{mediaType}/collected/weekly",
                    "anticipated" => $"/{mediaType}/anticipated",
                    "boxoffice" when mediaType == "movies" => "/movies/boxoffice",
                    _ => null
                };
            }

            return null;
        }

        /// <summary>
        /// Matches Trakt user list URLs: https://trakt.tv/users/{user}/lists/{list}
        /// </summary>
        [GeneratedRegex(@"trakt\.tv/users/([^/]+)/lists/([^/?#]+)", RegexOptions.IgnoreCase)]
        private static partial Regex UserListPattern();

        /// <summary>
        /// Matches Trakt watchlist URLs: https://trakt.tv/users/{user}/watchlist
        /// </summary>
        [GeneratedRegex(@"trakt\.tv/users/([^/]+)/watchlist", RegexOptions.IgnoreCase)]
        private static partial Regex WatchlistPattern();

        /// <summary>
        /// Matches Trakt chart URLs: https://trakt.tv/movies/trending, /shows/popular, etc.
        /// </summary>
        [GeneratedRegex(@"trakt\.tv/(movies|shows)/(trending|popular|watched|played|collected|anticipated|boxoffice)", RegexOptions.IgnoreCase)]
        private static partial Regex ChartPattern();
    }
}
