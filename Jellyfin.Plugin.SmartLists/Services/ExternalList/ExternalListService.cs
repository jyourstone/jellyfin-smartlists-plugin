using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Microsoft.Extensions.Logging;

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
        /// <param name="maxItems">Maximum number of items to fetch per list (0 = unlimited).
        /// When the primary sort is "External List Order Ascending" with a limit, callers pass the
        /// limit here so providers can stop fetching early.</param>
        public async Task PreFetchListsAsync(
            IEnumerable<string> urls,
            RefreshQueueService.RefreshCache cache,
            CancellationToken cancellationToken,
            int maxItems = 0)
        {
            var urlList = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (urlList.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Pre-fetching {Count} external list(s) (maxItems: {MaxItems})", urlList.Count, maxItems);

            foreach (var url in urlList)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Skip if already cached with a sufficient result.
                // A complete result is always sufficient. A partial result is sufficient only
                // if we also want a limited fetch and the cached result has enough items.
                if (cache.ExternalListData.TryGetValue(url, out var existing))
                {
                    if (existing.IsComplete || (maxItems > 0 && existing.TotalItems >= maxItems))
                    {
                        _logger.LogDebug("External list already cached: {Url} (complete: {IsComplete}, items: {Count})", url, existing.IsComplete, existing.TotalItems);
                        continue;
                    }

                    // Partial cache insufficient for this request — re-fetch
                    _logger.LogDebug("Re-fetching external list {Url}: cached result is partial ({Count} items) but unlimited fetch needed", url, existing.TotalItems);
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
                    var result = await provider.FetchListAsync(url, cancellationToken, maxItems).ConfigureAwait(false);
                    cache.ExternalListData[url] = result;
                    _logger.LogDebug("Cached external list {Url}: {Count} items (complete: {IsComplete})", url, result.TotalItems, result.IsComplete);
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

        /// <summary>
        /// Computes the fetch limit for external list providers based on the smart list's sort order and max items.
        /// When the primary sort is "External List Order Ascending" and a max items limit is set,
        /// providers can stop fetching early since only the first N items from the list are needed.
        /// </summary>
        /// <param name="dto">The smart list DTO containing sort and limit configuration.</param>
        /// <returns>The max items to fetch (0 = unlimited).</returns>
        public static int ComputeFetchLimit(SmartListDto dto)
        {
            if (!dto.MaxItems.HasValue || dto.MaxItems.Value <= 0)
            {
                return 0;
            }

            bool isAscendingExternalListOrder = false;

            if (dto.Order?.SortOptions?.Count > 0)
            {
                var primary = dto.Order.SortOptions[0];
                isAscendingExternalListOrder = primary.SortBy == "External List Order"
                    && primary.SortOrder == SortOrder.Ascending;
            }
            else if (dto.Order?.Name == "External List Order Ascending")
            {
                isAscendingExternalListOrder = true;
            }

            return isAscendingExternalListOrder ? dto.MaxItems.Value : 0;
        }
    }
}
