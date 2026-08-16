using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters
{
    /// <summary>
    /// Builds a candidate item-ID set for a list's rule sets by pushing individual rules down
    /// to indexed database queries via registered <see cref="IRulePrefilterResolver"/>s.
    ///
    /// The result is always a SUPERSET of the items the per-item evaluation would match, so
    /// intersecting a phase's input pool with it can never drop a true match:
    /// - Within one rule set (AND): intersection over that set's pushdownable rules only;
    ///   rules no resolver can bound simply do not participate (they still evaluate per-item).
    /// - Across rule sets (OR): union. A set with ZERO pushdownable rules may match any item,
    ///   which forces the overall result to null (= no shrink possible).
    ///
    /// A null result means "no shrink possible" and callers must behave exactly as if no
    /// prefilter existed. An empty (non-null) result is a hard claim that no item matches.
    /// </summary>
    internal sealed class CandidateSetBuilder
    {
        private readonly IReadOnlyList<IRulePrefilterResolver> _resolvers;

        /// <summary>
        /// Initializes a new instance of the <see cref="CandidateSetBuilder"/> class.
        /// </summary>
        /// <param name="resolvers">Resolvers consulted per rule, in order; the first non-null result wins.</param>
        public CandidateSetBuilder(IReadOnlyList<IRulePrefilterResolver> resolvers)
        {
            ArgumentNullException.ThrowIfNull(resolvers);
            _resolvers = resolvers;
        }

        /// <summary>
        /// Creates a builder with the production field resolvers. Fields without a resolver
        /// here simply never ride the prefilter - their rules evaluate per-item as before.
        /// </summary>
        /// <returns>The builder with all production resolvers registered.</returns>
        public static CandidateSetBuilder CreateDefault()
        {
            return new CandidateSetBuilder([new PeoplePrefilterResolver(), new SeriesNamePrefilterResolver(), new NextUnwatchedPrefilterResolver(), new LastEpisodeAirDatePrefilterResolver(), new ResolutionPrefilterResolver()]);
        }

        /// <summary>
        /// Builds the candidate set for the given rule sets.
        /// </summary>
        /// <param name="expressionSets">The list's rule sets (OR of ANDs).</param>
        /// <param name="context">Query context handed to resolvers.</param>
        /// <returns>Candidate item IDs, or null when no shrink is possible.</returns>
        public HashSet<Guid>? Build(IReadOnlyList<ExpressionSet>? expressionSets, PrefilterContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (_resolvers.Count == 0 || expressionSets == null || expressionSets.Count == 0)
            {
                return null;
            }

            var stopwatch = Stopwatch.StartNew();
            HashSet<Guid>? union = null;

            foreach (var set in expressionSets)
            {
                var setCandidates = BuildSetCandidates(set, context);
                if (setCandidates == null)
                {
                    // This OR branch has no pushdownable rules, so it may match any item -
                    // no overall shrink is possible regardless of what other sets contribute.
                    context.Logger?.LogDebug("Prefilter: a rule set has no pushdownable rules - no candidate shrink possible");
                    return null;
                }

                if (union == null)
                {
                    union = setCandidates;
                }
                else
                {
                    union.UnionWith(setCandidates);
                }
            }

            if (union != null)
            {
                context.Logger?.LogDebug("Prefilter candidate set built: {Count} candidates in {Ms}ms", union.Count, stopwatch.ElapsedMilliseconds);
            }

            return union;
        }

        /// <summary>
        /// Intersection over one set's pushdownable rules; null when none are pushdownable.
        /// </summary>
        private HashSet<Guid>? BuildSetCandidates(ExpressionSet? set, PrefilterContext context)
        {
            if (set?.Expressions == null)
            {
                return null;
            }

            HashSet<Guid>? candidates = null;

            foreach (var expression in set.Expressions)
            {
                if (expression == null || !IsEligible(expression, out var requiresNegativeSupport))
                {
                    continue;
                }

                var ruleCandidates = ResolveRule(expression, requiresNegativeSupport, context);
                if (ruleCandidates == null)
                {
                    // Not pushdownable - the rule still evaluates per-item on survivors.
                    continue;
                }

                if (candidates == null)
                {
                    candidates = ruleCandidates;
                }
                else
                {
                    candidates.IntersectWith(ruleCandidates);
                }

                if (candidates.Count == 0)
                {
                    // The AND-intersection is already empty; nothing in this set can match.
                    return candidates;
                }
            }

            return candidates;
        }

        private HashSet<Guid>? ResolveRule(Expression expression, bool requiresNegativeSupport, PrefilterContext context)
        {
            foreach (var resolver in _resolvers)
            {
                if (requiresNegativeSupport && !resolver.SupportsNegativeOperators)
                {
                    continue;
                }

                try
                {
                    var result = resolver.Resolve(expression, context);
                    if (result != null)
                    {
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    // A failing resolver degrades to "no shrink for this rule" - it must never
                    // break the refresh or, worse, pretend to have bounded the rule.
                    context.Logger?.LogDebug(ex, "Prefilter resolver {Resolver} failed for {Field} {Operator}; rule stays per-item",
                        resolver.GetType().Name, expression.MemberName, expression.Operator);
                }
            }

            return null;
        }

        /// <summary>
        /// Central pushdown gates that apply regardless of field: negative operators only ride
        /// resolvers that prove an exact complement, and MatchRegex never rides when its
        /// pattern matches the empty string (empty-list semantics test against "").
        /// </summary>
        private static bool IsEligible(Expression expression, out bool requiresNegativeSupport)
        {
            requiresNegativeSupport = IsNegativeOperator(expression.Operator);

            if (string.Equals(expression.Operator, "MatchRegex", StringComparison.Ordinal))
            {
                try
                {
                    if (Regex.IsMatch(string.Empty, expression.TargetValue))
                    {
                        return false;
                    }
                }
                catch (ArgumentException)
                {
                    // Invalid pattern - leave it to the per-item path.
                    return false;
                }
            }

            return true;
        }

        private static bool IsNegativeOperator(string? op)
        {
            return string.Equals(op, "NotEqual", StringComparison.Ordinal)
                || string.Equals(op, "NotContains", StringComparison.Ordinal)
                || string.Equals(op, "IsNotIn", StringComparison.Ordinal);
        }
    }
}
