using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Fetches list items from IMDb using the public GraphQL API.
    /// Supports chart URLs (e.g., /chart/top/) and user list URLs (e.g., /list/ls123456789/).
    /// </summary>
    public partial class ImdbListProvider : IExternalListProvider
    {
        private const string GraphQlEndpoint = "https://caching.graphql.imdb.com/";
        private const int ListPageSize = 250;

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
                    "Invalid IMDb URL. Expected formats: https://www.imdb.com/list/ls123456789/ or https://www.imdb.com/chart/top/");
            }

            _logger.LogInformation("Fetching IMDb list: {Url}", url);

            // Determine if this is a chart or a user list
            var chartMatch = ImdbChartPattern().Match(url);
            if (chartMatch.Success)
            {
                var chartSlug = chartMatch.Groups[1].Value;
                return await FetchChartAsync(chartSlug, maxItems, cancellationToken).ConfigureAwait(false);
            }

            var listMatch = ImdbUserListPattern().Match(url);
            if (listMatch.Success)
            {
                var listId = listMatch.Groups[1].Value;
                return await FetchUserListAsync(listId, url, maxItems, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Could not parse IMDb URL: {url}. Expected /chart/{{type}}/ or /list/ls{{id}}/");
        }

        private async Task<ExternalListResult> FetchChartAsync(string chartSlug, int maxItems, CancellationToken cancellationToken)
        {
            if (!ChartTypeMap.TryGetValue(chartSlug, out var chartType))
            {
                var supported = string.Join(", ", ChartTypeMap.Keys);
                throw new InvalidOperationException(
                    $"Unsupported IMDb chart type: '{chartSlug}'. Supported chart types: {supported}");
            }

            var fetchCount = maxItems > 0 ? maxItems : 250;

            var query = "{chartTitles(chart:{chartType:" + chartType + "},first:" + fetchCount
                + "){edges{node{id}}}}";

            var json = await SendGraphQlAsync(query, cancellationToken).ConfigureAwait(false);

            var result = new ExternalListResult();
            int position = 0;

            var dataElement = json.RootElement.GetProperty("data").GetProperty("chartTitles").GetProperty("edges");
            foreach (var edge in dataElement.EnumerateArray())
            {
                var id = edge.GetProperty("node").GetProperty("id").GetString();
                if (!string.IsNullOrEmpty(id))
                {
                    result.ImdbIds.TryAdd(id, position);
                    position++;
                }
            }

            result.TotalItems = result.ImdbIds.Count;
            result.IsComplete = maxItems <= 0 || result.TotalItems < maxItems;
            _logger.LogInformation("Fetched {Count} items from IMDb chart '{Chart}'", result.TotalItems, chartSlug);

            return result;
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

                var json = await SendGraphQlAsync(query, cancellationToken).ConfigureAwait(false);

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

            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

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

                throw new HttpRequestException(
                    $"IMDb GraphQL API returned an error: {message}");
            }

            return document;
        }

        /// <summary>
        /// Matches IMDb list and chart URLs:
        /// https://www.imdb.com/list/ls123456789/
        /// https://www.imdb.com/chart/top/
        /// https://www.imdb.com/chart/toptv/
        /// </summary>
        [GeneratedRegex(@"imdb\.com/(list/ls\d+|chart/\w+)", RegexOptions.IgnoreCase)]
        private static partial Regex ImdbListUrlPattern();

        /// <summary>
        /// Extracts the chart slug from a chart URL: /chart/top/ → "top"
        /// </summary>
        [GeneratedRegex(@"imdb\.com/chart/(\w[\w-]*)", RegexOptions.IgnoreCase)]
        private static partial Regex ImdbChartPattern();

        /// <summary>
        /// Extracts the list ID from a user list URL: /list/ls123456789/ → "ls123456789"
        /// </summary>
        [GeneratedRegex(@"imdb\.com/list/(ls\d+)", RegexOptions.IgnoreCase)]
        private static partial Regex ImdbUserListPattern();
    }
}
