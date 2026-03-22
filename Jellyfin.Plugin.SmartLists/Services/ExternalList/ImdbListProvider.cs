using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Fetches list items from IMDb using the public GraphQL API.
    /// Supports chart URLs (e.g., /chart/top/), user list URLs (e.g., /list/ls123456789/),
    /// and event/awards URLs (e.g., /event/ev0000003/2022/1/).
    /// </summary>
    public partial class ImdbListProvider : IExternalListProvider
    {
        private const string GraphQlEndpoint = "https://caching.graphql.imdb.com/";
        private const int ListPageSize = 250;
        private const int SearchPageSize = 250;

        private static readonly Dictionary<string, string> ChartTypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["top"] = "TOP_RATED_MOVIES",
            ["toptv"] = "TOP_RATED_TV_SHOWS",
            ["moviemeter"] = "MOST_POPULAR_MOVIES",
            ["tvmeter"] = "MOST_POPULAR_TV_SHOWS",
            ["bottom"] = "LOWEST_RATED_MOVIES",
            ["top-english-movies"] = "TOP_RATED_ENGLISH_MOVIES",
        };

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
        public async Task<ExternalListResult> FetchListAsync(string url, CancellationToken cancellationToken, int maxItems = 0)
        {
            if (!ImdbListUrlPattern().IsMatch(url))
            {
                throw new InvalidOperationException(
                    "Invalid IMDb URL. Expected formats: https://www.imdb.com/list/ls123456789/, https://www.imdb.com/chart/top/, or https://www.imdb.com/event/ev0000003/2022/1/");
            }

            _logger.LogDebug("Fetching IMDb list: {Url}", url);

            // Determine if this is a chart, event, or a user list
            var chartMatch = ImdbChartPattern().Match(url);
            if (chartMatch.Success)
            {
                var chartSlug = chartMatch.Groups[1].Value;
                return await FetchChartAsync(chartSlug, maxItems, cancellationToken).ConfigureAwait(false);
            }

            var eventMatch = ImdbEventPattern().Match(url);
            if (eventMatch.Success)
            {
                var eventId = eventMatch.Groups[1].Value;
                var eventYear = int.Parse(eventMatch.Groups[2].Value, CultureInfo.InvariantCulture);

                // Extract optional category fragment from URL (e.g., #oscar_best_sound)
                string? categoryFragment = null;
                if (Uri.TryCreate(url, UriKind.Absolute, out var eventUri)
                    && !string.IsNullOrEmpty(eventUri.Fragment)
                    && eventUri.Fragment.Length > 1)
                {
                    categoryFragment = eventUri.Fragment.Substring(1);
                }

                return await FetchEventAsync(eventId, eventYear, categoryFragment, maxItems, cancellationToken).ConfigureAwait(false);
            }

            var listMatch = ImdbUserListPattern().Match(url);
            if (listMatch.Success)
            {
                var listId = listMatch.Groups[1].Value;
                return await FetchUserListAsync(listId, url, maxItems, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Could not parse IMDb URL: {url}. Expected /chart/{{type}}/, /list/ls{{id}}/, or /event/ev{{id}}/");
        }

        private async Task<ExternalListResult> FetchChartAsync(string chartSlug, int maxItems, CancellationToken cancellationToken)
        {
            if (!ChartTypeMap.TryGetValue(chartSlug, out var chartType))
            {
                var supported = string.Join(", ", ChartTypeMap.Keys);
                throw new InvalidOperationException(
                    $"Unsupported IMDb chart type: '{chartSlug}'. Supported chart types: {supported}");
            }

            var result = new ExternalListResult();
            int position = 0;
            string? cursor = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fetchCount = maxItems > 0 ? Math.Min(ListPageSize, maxItems - position) : ListPageSize;
                if (fetchCount <= 0)
                {
                    break;
                }

                var afterClause = cursor != null ? ",after:\"" + cursor + "\"" : string.Empty;
                var query = "{chartTitles(chart:{chartType:" + chartType + "},first:" + fetchCount + afterClause
                    + "){edges{node{id}}pageInfo{hasNextPage endCursor}}}";

                using var json = await SendGraphQlAsync(query, cancellationToken).ConfigureAwait(false);

                var chartData = json.RootElement.GetProperty("data").GetProperty("chartTitles");
                var edges = chartData.GetProperty("edges");
                int itemsInPage = 0;

                foreach (var edge in edges.EnumerateArray())
                {
                    var id = edge.GetProperty("node").GetProperty("id").GetString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        result.ImdbIds.TryAdd(id, position);
                        position++;
                        itemsInPage++;
                    }
                }

                var pageInfo = chartData.GetProperty("pageInfo");
                var hasNextPage = pageInfo.GetProperty("hasNextPage").GetBoolean();

                if (!hasNextPage || itemsInPage == 0)
                {
                    break;
                }

                cursor = pageInfo.GetProperty("endCursor").GetString();
            }

            result.TotalItems = result.ImdbIds.Count;
            result.IsComplete = maxItems <= 0 || result.TotalItems < maxItems;
            _logger.LogInformation("Fetched {Count} items from IMDb chart '{Chart}'", result.TotalItems, chartSlug);

            return result;
        }

        private async Task<ExternalListResult> FetchEventAsync(
            string eventId,
            int year,
            string? categoryFragment,
            int maxItems,
            CancellationToken cancellationToken)
        {
            var result = new ExternalListResult();

            // Phase 1: Search for titles nominated at this event, narrowed by release date heuristic.
            // The IMDb GraphQL API does not support filtering by award year directly,
            // so we use a release date range (year-2 to year) to reduce the candidate set.
            var candidateIds = new List<string>();
            string? cursor = null;
            var startYear = year - 2;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var afterClause = cursor != null ? ",after:\"" + cursor + "\"" : string.Empty;
                var query = "{advancedTitleSearch(first:" + SearchPageSize + afterClause
                    + ",constraints:{awardConstraint:{anyEventNominations:[{eventId:\"" + eventId + "\"}]}"
                    + ",releaseDateConstraint:{releaseDateRange:{start:\"" + startYear + "-01-01\""
                    + ",end:\"" + year + "-12-31\"}}})"
                    + "{edges{node{title{id}}}pageInfo{hasNextPage endCursor}}}";

                using var json = await SendGraphQlAsync(query, cancellationToken).ConfigureAwait(false);

                var searchData = json.RootElement.GetProperty("data").GetProperty("advancedTitleSearch");
                var edges = searchData.GetProperty("edges");

                foreach (var edge in edges.EnumerateArray())
                {
                    var id = edge.GetProperty("node").GetProperty("title").GetProperty("id").GetString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        candidateIds.Add(id);
                    }
                }

                var pageInfo = searchData.GetProperty("pageInfo");
                var hasNextPage = pageInfo.GetProperty("hasNextPage").GetBoolean();

                if (!hasNextPage || edges.GetArrayLength() == 0)
                {
                    break;
                }

                cursor = pageInfo.GetProperty("endCursor").GetString();
            }

            _logger.LogDebug(
                "Event search phase 1: {Count} candidates for {EventId} (release date {Start}-{End})",
                candidateIds.Count, eventId, startYear, year);

            if (candidateIds.Count == 0)
            {
                result.TotalItems = 0;
                result.IsComplete = true;
                return result;
            }

            // Phase 2: Verify candidates by checking each title's award.year matches the target year.
            // Optionally filter by category if a URL fragment was provided.
            int position = 0;
            const int verifyBatchSize = 50;
            var categoryFields = categoryFragment != null ? " category{text}" : string.Empty;

            for (int i = 0; i < candidateIds.Count; i += verifyBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (maxItems > 0 && position >= maxItems)
                {
                    break;
                }

                var batch = candidateIds.GetRange(i, Math.Min(verifyBatchSize, candidateIds.Count - i));
                var idsParam = string.Join(",", batch.ConvertAll(id => "\"" + id + "\""));

                var query = "{titles(ids:[" + idsParam + "]){id awardNominations(first:50,filter:{events:[\"" + eventId + "\"]})"
                    + "{edges{node{award{year" + categoryFields + "}}}}}}";

                using var json = await SendGraphQlAsync(query, cancellationToken).ConfigureAwait(false);

                var titles = json.RootElement.GetProperty("data").GetProperty("titles");

                foreach (var title in titles.EnumerateArray())
                {
                    if (title.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    var titleId = title.GetProperty("id").GetString();
                    if (string.IsNullOrEmpty(titleId))
                    {
                        continue;
                    }

                    var nominations = title.GetProperty("awardNominations").GetProperty("edges");
                    bool matches = false;

                    foreach (var nom in nominations.EnumerateArray())
                    {
                        var award = nom.GetProperty("node").GetProperty("award");
                        if (!award.TryGetProperty("year", out var yearProp) || yearProp.GetInt32() != year)
                        {
                            continue;
                        }

                        if (categoryFragment != null)
                        {
                            if (award.TryGetProperty("category", out var catProp)
                                && catProp.ValueKind != JsonValueKind.Null
                                && catProp.TryGetProperty("text", out var textProp))
                            {
                                var catText = textProp.GetString();
                                if (!string.IsNullOrEmpty(catText) && CategoryMatchesFragment(catText, categoryFragment))
                                {
                                    matches = true;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            matches = true;
                            break;
                        }
                    }

                    if (matches)
                    {
                        result.ImdbIds.TryAdd(titleId, position);
                        position++;
                    }
                }
            }

            result.TotalItems = result.ImdbIds.Count;
            result.IsComplete = maxItems <= 0 || result.TotalItems < maxItems;
            _logger.LogInformation(
                "Fetched {Count} items from IMDb event '{EventId}' year {Year}{Category}",
                result.TotalItems, eventId, year,
                categoryFragment != null ? " category '" + categoryFragment + "'" : string.Empty);

            return result;
        }

        /// <summary>
        /// Checks if a category text matches a URL fragment slug.
        /// The fragment may have an event-specific prefix (e.g., "oscar_best_sound" for "Best Sound").
        /// </summary>
        private static bool CategoryMatchesFragment(string categoryText, string fragment)
        {
            var slug = SlugPattern().Replace(categoryText.ToLowerInvariant(), "_").Trim('_');
            var normalizedFragment = fragment.ToLowerInvariant().Trim('_');

            return string.Equals(normalizedFragment, slug, StringComparison.Ordinal)
                || normalizedFragment.EndsWith("_" + slug, StringComparison.Ordinal);
        }

        private async Task<ExternalListResult> FetchUserListAsync(string listId, string url, int maxItems, CancellationToken cancellationToken)
        {
            var result = new ExternalListResult();
            int position = 0;
            string? cursor = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fetchCount = maxItems > 0 ? Math.Min(ListPageSize, maxItems - position) : ListPageSize;
                if (fetchCount <= 0)
                {
                    break;
                }

                var afterClause = cursor != null ? ",after:\"" + cursor + "\"" : string.Empty;
                var query = "{list(id:\"" + listId + "\"){items(first:" + fetchCount + afterClause
                    + "){edges{node{item{... on Title{id}}}}pageInfo{hasNextPage endCursor}}}}";

                using var json = await SendGraphQlAsync(query, cancellationToken).ConfigureAwait(false);

                var listData = json.RootElement.GetProperty("data").GetProperty("list");
                if (listData.ValueKind == JsonValueKind.Null)
                {
                    _logger.LogWarning("IMDb list not found or is private: {Url}", url);
                    break;
                }

                var items = listData.GetProperty("items");
                var edges = items.GetProperty("edges");
                int itemsInPage = 0;

                foreach (var edge in edges.EnumerateArray())
                {
                    var itemNode = edge.GetProperty("node").GetProperty("item");
                    if (itemNode.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    if (itemNode.TryGetProperty("id", out var idProp))
                    {
                        var id = idProp.GetString();
                        if (!string.IsNullOrEmpty(id))
                        {
                            result.ImdbIds.TryAdd(id, position);
                            position++;
                            itemsInPage++;
                        }
                    }
                }

                var pageInfo = items.GetProperty("pageInfo");
                var hasNextPage = pageInfo.GetProperty("hasNextPage").GetBoolean();

                if (!hasNextPage || itemsInPage == 0)
                {
                    break;
                }

                cursor = pageInfo.GetProperty("endCursor").GetString();
            }

            result.TotalItems = result.ImdbIds.Count;
            result.IsComplete = maxItems <= 0 || result.TotalItems < maxItems;
            _logger.LogInformation("Fetched {Count} items from IMDb list '{ListId}'", result.TotalItems, listId);

            return result;
        }

        private async Task<JsonDocument> SendGraphQlAsync(string query, CancellationToken cancellationToken)
        {
            var httpClient = _httpClientFactory.CreateClient("ImdbList");
            var requestBody = JsonSerializer.Serialize(new { query });

            using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
            };

            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36");

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"IMDb GraphQL API returned {response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var document = JsonDocument.Parse(json);

            // Check for GraphQL errors
            if (document.RootElement.TryGetProperty("errors", out var errors)
                && errors.GetArrayLength() > 0)
            {
                var message = errors[0].TryGetProperty("message", out var msg)
                    ? msg.GetString()
                    : "Unknown error";

                document.Dispose();
                throw new HttpRequestException(
                    $"IMDb GraphQL API returned an error: {message}");
            }

            return document;
        }

        /// <summary>
        /// Matches IMDb list, chart, and event URLs:
        /// https://www.imdb.com/list/ls123456789/
        /// https://www.imdb.com/chart/top/
        /// https://www.imdb.com/chart/toptv/
        /// https://www.imdb.com/event/ev0000003/2022/1/
        /// </summary>
        [GeneratedRegex(@"imdb\.com/(list/ls\d+|chart/\w[\w-]*|event/ev\d+/\d{4}/\d+)", RegexOptions.IgnoreCase)]
        private static partial Regex ImdbListUrlPattern();

        /// <summary>
        /// Extracts the chart slug from a chart URL: /chart/top/ → "top"
        /// </summary>
        [GeneratedRegex(@"imdb\.com/chart/(\w[\w-]*)", RegexOptions.IgnoreCase)]
        private static partial Regex ImdbChartPattern();

        /// <summary>
        /// Extracts the event ID, year, and instance from an event URL:
        /// /event/ev0000003/2022/1/ → ("ev0000003", "2022", "1")
        /// </summary>
        [GeneratedRegex(@"imdb\.com/event/(ev\d+)/(\d{4})/(\d+)", RegexOptions.IgnoreCase)]
        private static partial Regex ImdbEventPattern();

        /// <summary>
        /// Extracts the list ID from a user list URL: /list/ls123456789/ → "ls123456789"
        /// </summary>
        [GeneratedRegex(@"imdb\.com/list/(ls\d+)", RegexOptions.IgnoreCase)]
        private static partial Regex ImdbUserListPattern();

        /// <summary>
        /// Matches non-alphanumeric characters for slug generation.
        /// </summary>
        [GeneratedRegex(@"[^a-z0-9]+")]
        private static partial Regex SlugPattern();
    }
}
