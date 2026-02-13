using System;
using System.Collections.Generic;
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
    /// Fetches list items from the MDBList API (api.mdblist.com).
    /// Supports URLs like https://mdblist.com/lists/{username}/{listname}
    /// </summary>
    public partial class MdbListProvider : IExternalListProvider
    {
        private const string ApiBaseUrl = "https://api.mdblist.com";
        private const int PageSize = 1000;

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
        public async Task<ExternalListResult> FetchListAsync(string url, CancellationToken cancellationToken)
        {
            var result = new ExternalListResult();

            var apiKey = Plugin.Instance?.Configuration?.MdbListApiKey ?? string.Empty;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("MDBList API key is not configured. Set the API key in Settings > External Lists.");
            }

            var (username, listname) = ParseMdbListUrl(url);
            if (username == null || listname == null)
            {
                _logger.LogWarning("Could not parse MDBList URL: {Url}. Expected format: https://mdblist.com/lists/{{username}}/{{listname}}", url);
                return result;
            }

            _logger.LogInformation("Fetching external list from MDBList: {Username}/{ListName}", username, listname);

            var httpClient = _httpClientFactory.CreateClient("MdbList");
            int offset = 0;
            int totalFetched = 0;
            int position = 0;

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var apiUrl = $"{ApiBaseUrl}/lists/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(listname)}/items?apikey={Uri.EscapeDataString(apiKey)}&limit={PageSize}&offset={offset}";

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
                                AddItemIds(item, result, ref position);
                                itemsInPage++;
                            }
                        }

                        if (wrapper?.Shows != null)
                        {
                            foreach (var item in wrapper.Shows)
                            {
                                AddItemIds(item, result, ref position);
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
                                    AddItemIds(item, result, ref position);
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
            _logger.LogInformation("Fetched {Count} items from MDBList {Username}/{ListName} (IMDb: {ImdbCount}, TMDB: {TmdbCount}, TVDB: {TvdbCount})",
                totalFetched, username, listname, result.ImdbIds.Count, result.TmdbIds.Count, result.TvdbIds.Count);

            return result;
        }

        private static void AddItemIds(MdbListItem item, ExternalListResult result, ref int position)
        {
            // Add IMDb ID from top-level or nested ids (TryAdd keeps first/lowest position for duplicates)
            var imdbId = item.ImdbId ?? item.Ids?.Imdb;
            if (!string.IsNullOrEmpty(imdbId))
            {
                result.ImdbIds.TryAdd(imdbId, position);
            }

            // Add TMDB ID from nested ids
            if (item.Ids?.Tmdb is int tmdbId and > 0)
            {
                result.TmdbIds.TryAdd(tmdbId.ToString(CultureInfo.InvariantCulture), position);
            }

            // Add TVDB ID from top-level or nested ids
            var tvdbId = item.TvdbId ?? item.Ids?.Tvdb;
            if (tvdbId is int tvdb and > 0)
            {
                result.TvdbIds.TryAdd(tvdb.ToString(CultureInfo.InvariantCulture), position);
            }

            position++;
        }

        /// <summary>
        /// Parses a MDBList URL into username and list name components.
        /// Supports: https://mdblist.com/lists/{username}/{listname}
        /// </summary>
        private static (string? Username, string? ListName) ParseMdbListUrl(string url)
        {
            var match = MdbListUrlPattern().Match(url);
            if (match.Success)
            {
                return (match.Groups[1].Value, match.Groups[2].Value);
            }

            return (null, null);
        }

        [GeneratedRegex(@"mdblist\.com/lists/([^/]+)/([^/?#]+)", RegexOptions.IgnoreCase)]
        private static partial Regex MdbListUrlPattern();
    }
}
