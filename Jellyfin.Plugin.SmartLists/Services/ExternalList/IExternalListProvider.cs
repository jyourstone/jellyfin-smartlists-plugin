using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SmartLists.Services.ExternalList
{
    /// <summary>
    /// Represents the result of fetching an external list — sets of provider IDs.
    /// </summary>
    public class ExternalListResult
    {
        /// <summary>
        /// Gets the set of IMDb IDs (e.g., "tt1234567").
        /// </summary>
        public HashSet<string> ImdbIds { get; init; } = [];

        /// <summary>
        /// Gets the set of TMDB IDs as strings (e.g., "917496").
        /// </summary>
        public HashSet<string> TmdbIds { get; init; } = [];

        /// <summary>
        /// Gets the set of TVDB IDs as strings (e.g., "421968").
        /// </summary>
        public HashSet<string> TvdbIds { get; init; } = [];

        /// <summary>
        /// Gets or sets the total number of items in the external list.
        /// </summary>
        public int TotalItems { get; set; }
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
        /// Fetches all items from the external list and returns their provider IDs.
        /// Each provider reads its own credentials from plugin configuration.
        /// </summary>
        /// <param name="url">The external list URL.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An <see cref="ExternalListResult"/> containing the provider IDs of all items in the list.</returns>
        Task<ExternalListResult> FetchListAsync(string url, CancellationToken cancellationToken);
    }
}
