using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Fetches list items from public IMDb list pages.
    /// Extracts IMDb IDs from the server-rendered HTML.
    /// Supports URLs like https://www.imdb.com/list/ls123456789/
    /// </summary>
    public partial class ImdbListProvider : IExternalListProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ImdbListProvider> _logger;

        public ImdbListProvider(IHttpClientFactory httpClientFactory, ILogger<ImdbListProvider> logger)
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
                && (uri.Host.Equals("imdb.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".imdb.com", StringComparison.OrdinalIgnoreCase));
        }

        /// <inheritdoc />
        public async Task<ExternalListResult> FetchListAsync(string url, CancellationToken cancellationToken)
        {
            var result = new ExternalListResult();

            // Validate it looks like a list URL
            if (!ImdbListUrlPattern().IsMatch(url))
            {
                throw new InvalidOperationException(
                    "Invalid IMDb URL. Expected formats: https://www.imdb.com/list/ls123456789/ or https://www.imdb.com/chart/top/");
            }

            // Normalize URL to ensure trailing slash
            var listUrl = url.TrimEnd('/') + "/";

            _logger.LogInformation("Fetching IMDb list: {Url}", listUrl);

            var httpClient = _httpClientFactory.CreateClient("ImdbList");
            using var request = new HttpRequestMessage(HttpMethod.Get, listUrl);
            // IMDb requires a browser-like User-Agent to return full HTML
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"IMDb returned {response.StatusCode} for {listUrl}");
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Extract IMDb IDs from title links in the HTML (e.g., /title/tt1234567/)
            // TryAdd keeps first/lowest position for duplicates (maintains DOM/list order)
            var matches = ImdbTitleIdPattern().Matches(html);
            int position = 0;
            foreach (Match match in matches)
            {
                result.ImdbIds.TryAdd(match.Groups[1].Value, position);
                position++;
            }

            result.TotalItems = result.ImdbIds.Count;
            _logger.LogInformation("Fetched {Count} items from IMDb list {Url}", result.TotalItems, listUrl);

            return result;
        }

        /// <summary>
        /// Matches IMDb list and chart URLs:
        /// https://www.imdb.com/list/ls123456789/
        /// https://www.imdb.com/chart/boxoffice/
        /// https://www.imdb.com/chart/top/
        /// https://www.imdb.com/chart/toptv/
        /// </summary>
        [GeneratedRegex(@"imdb\.com/(list/ls\d+|chart/\w+)", RegexOptions.IgnoreCase)]
        private static partial Regex ImdbListUrlPattern();

        /// <summary>
        /// Extracts IMDb title IDs from href attributes: /title/tt1234567/
        /// Uses a lookbehind to only match IDs in title links, avoiding duplicates from other contexts.
        /// </summary>
        [GeneratedRegex(@"/title/(tt\d{7,})/", RegexOptions.None)]
        private static partial Regex ImdbTitleIdPattern();
    }
}
