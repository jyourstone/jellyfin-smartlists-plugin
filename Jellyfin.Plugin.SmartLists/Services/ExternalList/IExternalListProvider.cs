using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Represents the result of fetching an external list — provider IDs mapped to their 0-based position in the list.
    /// </summary>
    public class ExternalListResult
    {
        /// <summary>
        /// Gets the IMDb IDs (e.g., "tt1234567") mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> ImdbIds { get; init; } = [];

        /// <summary>
        /// Gets the TMDB IDs as strings (e.g., "917496") mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> TmdbIds { get; init; } = [];

        /// <summary>
        /// Gets the TVDB IDs as strings (e.g., "421968") mapped to their 0-based position in the list.
        /// </summary>
        public Dictionary<string, int> TvdbIds { get; init; } = [];

        /// <summary>
        /// Gets or sets the total number of items in the external list.
        /// </summary>
        public int TotalItems { get; set; }

        /// <summary>
        /// Gets or sets whether the result contains all items from the external list.
        /// When false, the result was truncated by a maxItems limit and should not be
        /// reused for requests that need the full list.
        /// </summary>
        public bool IsComplete { get; set; } = true;
    }

    /// <summary>
    /// Interface for fetching list items from an external source (MDBList, Trakt, etc.).
    /// </summary>
    public interface IExternalListProvider
    {
        /// <summary>
        /// Checks whether this provider can handle the given URL.
        /// </summary>
        /// <param name="url">The external list URL.</param>
        /// <returns>True if this provider can fetch the list.</returns>
        bool CanHandle(string url);

        /// <summary>
        /// Fetches items from the external list and returns their provider IDs.
        /// Each provider reads its own credentials from plugin configuration.
        /// </summary>
        /// <param name="url">The external list URL.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <param name="maxItems">Maximum number of items to fetch (0 = unlimited). When set,
        /// providers may stop fetching early and mark the result as incomplete.</param>
        /// <returns>An <see cref="ExternalListResult"/> containing the provider IDs of items in the list.</returns>
        Task<ExternalListResult> FetchListAsync(string url, CancellationToken cancellationToken, int maxItems = 0);
    }
}
