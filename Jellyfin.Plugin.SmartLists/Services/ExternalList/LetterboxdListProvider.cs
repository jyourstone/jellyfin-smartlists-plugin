using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Fetches list items from public Letterboxd list pages.
    /// Uses a two-step approach: scrapes the list page for film slugs,
    /// then fetches each film page to extract the TMDB ID.
    /// Supports URLs like https://letterboxd.com/{user}/list/{name}/
    /// and https://letterboxd.com/{user}/watchlist/
    /// </summary>
    public partial class LetterboxdListProvider : IExternalListProvider
    {
        private const string BaseUrl = "https://letterboxd.com";
        private const int MaxConcurrentFilmRequests = 3;
        private const int DelayBetweenRequestsMs = 350;
        private const int MaxPages = 50;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<LetterboxdListProvider> _logger;

        /// <summary>
        /// In-memory cache of film slug → TMDB ID, persists across refreshes within the same Jellyfin session.
        /// Avoids re-fetching individual film pages for films already resolved.
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> _slugToTmdbCache = new(StringComparer.OrdinalIgnoreCase);

        public LetterboxdListProvider(IHttpClientFactory httpClientFactory, ILogger<LetterboxdListProvider> logger)
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
                && (uri.Host.Equals("letterboxd.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".letterboxd.com", StringComparison.OrdinalIgnoreCase));
        }

        /// <inheritdoc />
        public async Task<ExternalListResult> FetchListAsync(string url, CancellationToken cancellationToken, int maxItems = 0)
        {
            var result = new ExternalListResult();

            if (!LetterboxdUrlPattern().IsMatch(url))
            {
                throw new InvalidOperationException(
                    "Invalid Letterboxd URL. Expected formats: https://letterboxd.com/{user}/list/{name}/ or https://letterboxd.com/{user}/watchlist/");
            }

            _logger.LogInformation("Fetching Letterboxd list: {Url} (maxItems: {MaxItems})", url, maxItems);

            // Step 1: Scrape list pages for film slugs
            var filmSlugs = await FetchAllFilmSlugsAsync(url, maxItems, cancellationToken).ConfigureAwait(false);

            if (filmSlugs.Count == 0)
            {
                _logger.LogWarning("No films found on Letterboxd list: {Url}", url);
                return result;
            }

            _logger.LogInformation("Found {Count} films on Letterboxd list, resolving TMDB IDs...", filmSlugs.Count);

            // Step 2: Resolve TMDB IDs for each film (with concurrency limit and caching)
            await ResolveFilmTmdbIdsAsync(filmSlugs, result, cancellationToken).ConfigureAwait(false);

            result.TotalItems = result.TmdbIds.Count;
            result.IsComplete = maxItems <= 0 || result.TotalItems < maxItems;
            _logger.LogInformation(
                "Fetched {Count} items with TMDB IDs from Letterboxd list {Url} ({Cached} from cache)",
                result.TotalItems,
                url,
                filmSlugs.Count - _uncachedCount);

            return result;
        }

        /// <summary>
        /// Tracks how many film pages were fetched (not cached) during the current FetchListAsync call.
        /// </summary>
        private int _uncachedCount;

        /// <summary>
        /// Fetches all film slugs from the list, handling pagination.
        /// Returns a list of (slug, position) tuples preserving list order.
        /// </summary>
        private async Task<List<string>> FetchAllFilmSlugsAsync(string url, int maxItems, CancellationToken cancellationToken)
        {
            var slugs = new List<string>();
            var normalizedUrl = url.TrimEnd('/') + "/";
            var currentPageUrl = normalizedUrl;

            for (int page = 1; page <= MaxPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var html = await FetchPageAsync(currentPageUrl, cancellationToken).ConfigureAwait(false);

                // Extract film slugs from data-target-link attributes
                var matches = FilmTargetLinkPattern().Matches(html);
                foreach (Match match in matches)
                {
                    slugs.Add(match.Groups[1].Value);

                    // Stop early if we have enough slugs
                    if (maxItems > 0 && slugs.Count >= maxItems)
                    {
                        break;
                    }
                }

                // Stop pagination if we have enough slugs
                if (maxItems > 0 && slugs.Count >= maxItems)
                {
                    break;
                }

                // Check for next page
                var nextMatch = NextPagePattern().Match(html);
                if (!nextMatch.Success)
                {
                    break;
                }

                // Build next page URL from relative href
                currentPageUrl = BaseUrl + nextMatch.Groups[1].Value;
            }

            return slugs;
        }

        /// <summary>
        /// Resolves TMDB IDs for each film slug by fetching individual film pages.
        /// Uses an in-memory cache to avoid redundant requests across refreshes.
        /// </summary>
        private async Task ResolveFilmTmdbIdsAsync(
            List<string> filmSlugs,
            ExternalListResult result,
            CancellationToken cancellationToken)
        {
            _uncachedCount = 0;
            using var semaphore = new SemaphoreSlim(MaxConcurrentFilmRequests, MaxConcurrentFilmRequests);
            var tasks = new List<Task>();

            for (int i = 0; i < filmSlugs.Count; i++)
            {
                var slug = filmSlugs[i];
                var position = i;

                // Check cache first (no need for semaphore)
                if (_slugToTmdbCache.TryGetValue(slug, out var cachedTmdbId))
                {
                    result.TmdbIds.TryAdd(cachedTmdbId, position);
                    continue;
                }

                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                tasks.Add(FetchFilmTmdbIdAsync(slug, position, result, semaphore, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// Fetches a single film page to extract its TMDB ID.
        /// </summary>
        private async Task FetchFilmTmdbIdAsync(
            string slug,
            int position,
            ExternalListResult result,
            SemaphoreSlim semaphore,
            CancellationToken cancellationToken)
        {
            try
            {
                Interlocked.Increment(ref _uncachedCount);

                // Rate-limit: small delay before each request
                await Task.Delay(DelayBetweenRequestsMs, cancellationToken).ConfigureAwait(false);

                var filmUrl = BaseUrl + "/film/" + slug + "/";
                var html = await FetchPageAsync(filmUrl, cancellationToken).ConfigureAwait(false);

                // Extract data-tmdb-id from the <body> tag
                var match = TmdbIdPattern().Match(html);
                if (match.Success)
                {
                    var tmdbId = match.Groups[1].Value;
                    _slugToTmdbCache.TryAdd(slug, tmdbId);
                    result.TmdbIds.TryAdd(tmdbId, position);
                }
                else
                {
                    _logger.LogDebug("No TMDB ID found for Letterboxd film: {Slug}", slug);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch TMDB ID for Letterboxd film: {Slug}", slug);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Fetches a page with browser-like headers to avoid Cloudflare blocks.
        /// </summary>
        private async Task<string> FetchPageAsync(string url, CancellationToken cancellationToken)
        {
            var httpClient = _httpClientFactory.CreateClient("LetterboxdList");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    string.Format(CultureInfo.InvariantCulture, "Letterboxd returned {0} for {1}", response.StatusCode, url));
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Matches Letterboxd list and watchlist URLs:
        /// https://letterboxd.com/{user}/list/{name}/
        /// https://letterboxd.com/{user}/watchlist/
        /// </summary>
        [GeneratedRegex(@"letterboxd\.com/[\w-]+/(list/[\w-]+|watchlist)", RegexOptions.IgnoreCase)]
        private static partial Regex LetterboxdUrlPattern();

        /// <summary>
        /// Extracts film slugs from data-target-link attributes: data-target-link="/film/{slug}/"
        /// </summary>
        [GeneratedRegex(@"data-target-link=""/film/([\w-]+)/""", RegexOptions.None)]
        private static partial Regex FilmTargetLinkPattern();

        /// <summary>
        /// Matches the "Next" pagination link: class="next" href="/user/list/name/page/2/"
        /// </summary>
        [GeneratedRegex(@"class=""next""\s+href=""([^""]+)""", RegexOptions.None)]
        private static partial Regex NextPagePattern();

        /// <summary>
        /// Extracts the TMDB ID from the body tag: data-tmdb-id="238"
        /// </summary>
        [GeneratedRegex(@"data-tmdb-id=""(\d+)""", RegexOptions.None)]
        private static partial Regex TmdbIdPattern();
    }
}
