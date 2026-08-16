using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters
{
    /// <summary>
    /// Resolves a single rule expression to a database-backed candidate item-ID set.
    ///
    /// Safety invariant (non-negotiable): a resolver may only return a GUARANTEED SUPERSET of
    /// the items the per-item evaluation would match for the rule. False positives are harmless
    /// (final compiled-rule evaluation always still runs on survivors); a false negative is a
    /// correctness bug. When in doubt, return null - the rule then simply stays per-item.
    /// </summary>
    internal interface IRulePrefilterResolver
    {
        /// <summary>
        /// Gets a value indicating whether this resolver may be consulted for negative
        /// operators (NotEqual/NotContains/IsNotIn). Only fields whose complement is provably
        /// exact qualify (per spec: SeriesName and LastEpisodeAirDate-Before); everything else
        /// must leave negatives to the per-item path.
        /// </summary>
        bool SupportsNegativeOperators => false;

        /// <summary>
        /// Returns a guaranteed superset of the item IDs matching <paramref name="expression"/>,
        /// or null when this resolver cannot bound the rule (the rule then does not participate
        /// in the candidate intersection). An EMPTY set is a hard claim that nothing matches.
        /// The returned set is owned by the caller and may be mutated - return a fresh set on
        /// every call.
        /// </summary>
        /// <param name="expression">The rule to push down.</param>
        /// <param name="context">Query context (library manager, user, media types).</param>
        /// <returns>Superset of matching item IDs, or null when the rule cannot be bounded.</returns>
        HashSet<Guid>? Resolve(Expression expression, PrefilterContext context);
    }
}
