using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SmartLists.Services.Shared;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Orchestrates fetching external lists from various providers and caching results.
    /// Resolves URLs to the appropriate <see cref="IExternalListProvider"/> and handles errors gracefully.
    /// </summary>
    public class ExternalListService
    {
        private readonly IEnumerable<IExternalListProvider> _providers;
        private readonly ILogger<ExternalListService> _logger;

        public ExternalListService(
            IEnumerable<IExternalListProvider> providers,
            ILogger<ExternalListService> logger)
        {
            _providers = providers;
            _logger = logger;
        }

        /// <summary>
        /// Pre-fetches all external lists referenced by the given URLs and caches the results.
        /// Call this before filtering so that Factory.cs can do synchronous lookups from the cache.
        /// </summary>
        /// <param name="urls">The external list URLs to fetch.</param>
        /// <param name="cache">The refresh cache to populate with results.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task PreFetchListsAsync(
            IEnumerable<string> urls,
            RefreshQueueService.RefreshCache cache,
            CancellationToken cancellationToken)
        {
            var urlList = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (urlList.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Pre-fetching {Count} external list(s)", urlList.Count);

            foreach (var url in urlList)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Skip if already cached (e.g., from a previous list in the same refresh batch)
                if (cache.ExternalListData.ContainsKey(url))
                {
                    _logger.LogDebug("External list already cached: {Url}", url);
                    continue;
                }

                var provider = _providers.FirstOrDefault(p => p.CanHandle(url));
                if (provider == null)
                {
                    var msg = "No external list provider found for URL: " + url;
                    _logger.LogWarning("{Warning}", msg);
                    cache.Warnings.Add(msg);
                    cache.ExternalListData[url] = new ExternalListResult();
                    continue;
                }

                try
                {
                    var result = await provider.FetchListAsync(url, cancellationToken).ConfigureAwait(false);
                    cache.ExternalListData[url] = result;
                    _logger.LogDebug("Cached external list {Url}: {Count} items", url, result.TotalItems);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("External list fetch cancelled for {Url}", url);
                    throw;
                }
                catch (Exception ex)
                {
                    var msg = "Failed to fetch external list: " + url + " (" + ex.Message + ")";
                    _logger.LogWarning(ex, "Failed to fetch external list: {Url}. Treating as empty list.", url);
                    cache.Warnings.Add(msg);
                    cache.ExternalListData[url] = new ExternalListResult();
                }
            }
        }
    }
}
