using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters
{
    /// <summary>
    /// Everything a rule prefilter resolver may need to run a candidate query.
    /// Candidate queries are always separate <see cref="ILibraryManager"/> calls - resolvers
    /// must never mutate the shared per-(user, mediaTypes) pool fetch or any drain-scoped
    /// membership cache.
    /// </summary>
    internal sealed class PrefilterContext
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PrefilterContext"/> class.
        /// </summary>
        /// <param name="libraryManager">Library manager used for candidate queries.</param>
        /// <param name="user">The list's user (see <see cref="User"/> for scoping rules).</param>
        /// <param name="mediaTypes">The list's media types, or null when unrestricted.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        public PrefilterContext(ILibraryManager libraryManager, User user, IReadOnlyList<string>? mediaTypes, ILogger? logger)
        {
            LibraryManager = libraryManager;
            User = user;
            MediaTypes = mediaTypes;
            Logger = logger;
        }

        /// <summary>
        /// Gets the library manager used for candidate queries.
        /// </summary>
        public ILibraryManager LibraryManager { get; }

        /// <summary>
        /// Gets the list's user. Candidate queries themselves must run USER-NEUTRAL with
        /// GroupByPresentationUniqueKey = false (a user-scoped query collapses alternate
        /// versions and would drop items); visibility is restored by intersecting with the
        /// already user-scoped pool. The user is provided for resolvers that need the user id
        /// to resolve user-specific rules (e.g. IsFavorite).
        /// </summary>
        public User User { get; }

        /// <summary>
        /// Gets the list's media types, or null when unrestricted.
        /// </summary>
        public IReadOnlyList<string>? MediaTypes { get; }

        /// <summary>
        /// Gets the logger for diagnostics.
        /// </summary>
        public ILogger? Logger { get; }
    }
}
