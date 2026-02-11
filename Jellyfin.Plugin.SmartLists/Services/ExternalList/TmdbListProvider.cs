using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Fetches list items from the TMDB API (api.themoviedb.org/3).
    /// Supports user lists, popular, top-rated, trending, and other chart endpoints.
    /// URLs like https://www.themoviedb.org/list/8136 or https://www.themoviedb.org/movie/popular
    /// </summary>
    public partial class TmdbListProvider : IExternalListProvider
    {
        private const string ApiBaseUrl = "https://api.themoviedb.org/3";
        private const int MaxPages = 500; // Safety limit to prevent runaway pagination

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TmdbListProvider> _logger;

        public TmdbListProvider(IHttpClientFactory httpClientFactory, ILogger<TmdbListProvider> logger)
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
                && (uri.Host.Equals("themoviedb.org", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".themoviedb.org", StringComparison.OrdinalIgnoreCase));
        }

        /// <inheritdoc />
        public async Task<ExternalListResult> FetchListAsync(string url, CancellationToken cancellationToken)
        {
            var tmdbApiKey = Plugin.Instance?.Configuration?.TmdbApiKey ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tmdbApiKey))
            {
                throw new InvalidOperationException(
                    "TMDB API key is not configured. Set the API key in Settings > External Lists.");
            }

            var result = new ExternalListResult();

            var (apiPath, isUserList) = ResolveApiPath(url);
            if (apiPath == null)
            {
                throw new InvalidOperationException(
                    "Invalid TMDB URL. Supported formats: https://www.themoviedb.org/list/{id}, " +
                    "https://www.themoviedb.org/movie/popular, https://www.themoviedb.org/tv/top-rated, " +
                    "https://www.themoviedb.org/trending/movie/week, etc.");
            }

            _logger.LogInformation("Fetching TMDB list: {Url} -> {ApiPath}", url, apiPath);

            var httpClient = _httpClientFactory.CreateClient("TmdbList");

            if (isUserList)
            {
                await FetchUserListAsync(httpClient, apiPath, tmdbApiKey, result, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await FetchPaginatedAsync(httpClient, apiPath, tmdbApiKey, result, MaxPages, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Fetched {Count} items from TMDB {Url} (TMDB IDs: {TmdbCount})",
                result.TotalItems, url, result.TmdbIds.Count);

            return result;
        }

        private static async Task FetchUserListAsync(
            HttpClient httpClient,
            string apiPath,
            string tmdbApiKey,
            ExternalListResult result,
            CancellationToken cancellationToken)
        {
            int page = 1;
            int totalFetched = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var requestUrl = $"{ApiBaseUrl}{apiPath}?api_key={Uri.EscapeDataString(tmdbApiKey)}&page={page}";
                using var response = await httpClient.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (totalFetched == 0)
                    {
                        throw new HttpRequestException(
                            $"TMDB API returned {response.StatusCode} for {apiPath}");
                    }

                    break;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var listResponse = JsonSerializer.Deserialize<TmdbListResponse>(json);

                if (listResponse?.Items == null || listResponse.Items.Length == 0)
                {
                    break;
                }

                foreach (var item in listResponse.Items)
                {
                    if (item.Id is int id and > 0)
                    {
                        result.TmdbIds.Add(id.ToString(CultureInfo.InvariantCulture));
                        totalFetched++;
                    }
                }

                if (listResponse.TotalPages == null || page >= listResponse.TotalPages)
                {
                    break;
                }

                page++;
            }

            result.TotalItems = totalFetched;
        }

        private static async Task FetchPaginatedAsync(
            HttpClient httpClient,
            string apiPath,
            string tmdbApiKey,
            ExternalListResult result,
            int maxPages,
            CancellationToken cancellationToken)
        {
            int totalFetched = 0;

            for (int page = 1; page <= maxPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var requestUrl = $"{ApiBaseUrl}{apiPath}?api_key={Uri.EscapeDataString(tmdbApiKey)}&page={page}";
                using var response = await httpClient.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (totalFetched == 0)
                    {
                        throw new HttpRequestException(
                            $"TMDB API returned {response.StatusCode} for {apiPath}");
                    }

                    break;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var pageResponse = JsonSerializer.Deserialize<TmdbPageResponse>(json);

                if (pageResponse?.Results == null || pageResponse.Results.Length == 0)
                {
                    break;
                }

                foreach (var item in pageResponse.Results)
                {
                    if (item.Id is int id and > 0)
                    {
                        result.TmdbIds.Add(id.ToString(CultureInfo.InvariantCulture));
                        totalFetched++;
                    }
                }

                if (pageResponse.TotalPages != null && page >= pageResponse.TotalPages)
                {
                    break;
                }
            }

            result.TotalItems = totalFetched;
        }

        /// <summary>
        /// Resolves a themoviedb.org URL to the corresponding API path.
        /// Returns the API path and whether it's a user list (unlimited pages) or chart (capped).
        /// </summary>
        private static (string? ApiPath, bool IsUserList) ResolveApiPath(string url)
        {
            // User list: https://www.themoviedb.org/list/{id}
            var listMatch = UserListPattern().Match(url);
            if (listMatch.Success)
            {
                return ($"/list/{listMatch.Groups[1].Value}", true);
            }

            // Trending: https://www.themoviedb.org/trending/movie/week
            var trendingMatch = TrendingPattern().Match(url);
            if (trendingMatch.Success)
            {
                var mediaType = trendingMatch.Groups[1].Value.ToLowerInvariant();
                var window = trendingMatch.Groups[2].Value.ToLowerInvariant();
                return ($"/trending/{mediaType}/{window}", false);
            }

            // Movie charts: https://www.themoviedb.org/movie/popular, /movie/top-rated, etc.
            var movieChartMatch = MovieChartPattern().Match(url);
            if (movieChartMatch.Success)
            {
                var chartType = movieChartMatch.Groups[1].Value.ToLowerInvariant();
                var apiChart = chartType switch
                {
                    "popular" => "popular",
                    "top-rated" => "top_rated",
                    "now-playing" => "now_playing",
                    "upcoming" => "upcoming",
                    _ => null
                };

                if (apiChart != null)
                {
                    return ($"/movie/{apiChart}", false);
                }
            }

            // TV charts: https://www.themoviedb.org/tv/popular, /tv/top-rated, etc.
            var tvChartMatch = TvChartPattern().Match(url);
            if (tvChartMatch.Success)
            {
                var chartType = tvChartMatch.Groups[1].Value.ToLowerInvariant();
                var apiChart = chartType switch
                {
                    "popular" => "popular",
                    "top-rated" => "top_rated",
                    "airing-today" => "airing_today",
                    "on-the-air" => "on_the_air",
                    _ => null
                };

                if (apiChart != null)
                {
                    return ($"/tv/{apiChart}", false);
                }
            }

            // Bare /movie or /tv (no suffix) = popular
            // e.g. https://www.themoviedb.org/movie or https://www.themoviedb.org/tv
            var bareMatch = BareMediaPattern().Match(url);
            if (bareMatch.Success)
            {
                var mediaType = bareMatch.Groups[1].Value.ToLowerInvariant();
                return ($"/{mediaType}/popular", false);
            }

            return (null, false);
        }

        [GeneratedRegex(@"themoviedb\.org/list/(\d+)", RegexOptions.IgnoreCase)]
        private static partial Regex UserListPattern();

        [GeneratedRegex(@"themoviedb\.org/trending/(movie|tv|all)/(day|week)", RegexOptions.IgnoreCase)]
        private static partial Regex TrendingPattern();

        [GeneratedRegex(@"themoviedb\.org/movie/(popular|top-rated|now-playing|upcoming)", RegexOptions.IgnoreCase)]
        private static partial Regex MovieChartPattern();

        [GeneratedRegex(@"themoviedb\.org/tv/(popular|top-rated|airing-today|on-the-air)", RegexOptions.IgnoreCase)]
        private static partial Regex TvChartPattern();

        [GeneratedRegex(@"themoviedb\.org/(movie|tv)/?(?:[?#].*)?$", RegexOptions.IgnoreCase)]
        private static partial Regex BareMediaPattern();
    }
}
