using System;
using System.Collections.Generic;
using System.Diagnostics;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters
{
    /// <summary>
    /// Prefilter resolver for the NextUnwatched rule field.
    ///
    /// Only "Equal true" (and the logically identical "NotEqual false") rides: every item that
    /// rule matches is by definition an episode the rule's target user has NOT played - the
    /// plugin's watched test is BaseItem.IsPlayed(user, userData), which in both server ABIs is
    /// exactly "userData is not null &amp;&amp; userData.Played", the same predicate the DB
    /// IsPlayed=false translation applies (a missing userdata row counts as unplayed in both).
    /// One user-scoped GetItemIds(IsPlayed = false, IncludeItemTypes = [Episode]) per target
    /// user is therefore a guaranteed superset of the rule's matches. The complement ("is NOT
    /// the next unwatched" - played episodes, later unplayed ones, and every non-episode)
    /// cannot shrink anything, so all other operator/value combinations stay per-item.
    ///
    /// Query notes (verified against both ABI server sources):
    /// - User is MANDATORY on this query - the IsPlayed translation dereferences filter.User
    ///   in BOTH ABIs (10.11 BaseItemRepository and 12 TranslateQuery). This is the one
    ///   prefilter query that takes a user; GroupByPresentationUniqueKey is still pinned off
    ///   (grouping only removes ids, and the calculated next episode's id must survive).
    ///   Access filtering is symmetric with the per-item path, which fetches each series'
    ///   episodes with an InternalItemsQuery(user) for the same target user.
    /// - IsVirtualItem is deliberately NOT set: the pool may include virtual episodes and a
    ///   virtual episode can be the calculated next-unwatched (the per-item episodes fetch for
    ///   NextUnwatched does not filter virtual either).
    /// - TopParentIds is deliberately NOT set: the pool query drops its TopParentIds scope
    ///   when a LibraryName rule includes virtual items, and the resolver cannot see which
    ///   variant ran - the unscoped query is the safe superset of both (visibility is restored
    ///   by intersecting with the pool).
    ///
    /// The rule's target user mirrors Engine.BuildExpr: Expression.UserId ?? the list's user.
    /// Additional users are resolved through the user manager; the missing-referenced-user
    /// guard has already aborted the list before the prefilter builds, so an unresolvable user
    /// here just conservatively leaves the rule per-item.
    ///
    /// SupportsNegativeOperators is true solely so "NotEqual false" can ride; the boolean
    /// complement proof above is what the central negative-operator gate demands, and
    /// <see cref="RidesPrefilter"/> rejects every other negative combination.
    /// </summary>
    internal sealed class NextUnwatchedPrefilterResolver : IRulePrefilterResolver
    {
        /// <summary>
        /// Per-target-user unplayed-episode id sets for this filter run (the resolver lives
        /// for one CandidateSetBuilder.Build call): the riding combinations all map to the
        /// same query, so shared lists with several NextUnwatched rules query once per user.
        /// </summary>
        private readonly Dictionary<Guid, IReadOnlyList<Guid>> _unplayedEpisodeIdsByUser = [];

        /// <inheritdoc />
        public bool SupportsNegativeOperators => true;

        /// <inheritdoc />
        public HashSet<Guid>? Resolve(Expression expression, PrefilterContext context)
        {
            if (!string.Equals(expression.MemberName, "NextUnwatched", StringComparison.Ordinal)
                || context.LibraryManager == null
                || !RidesPrefilter(expression.Operator, expression.TargetValue))
            {
                return null;
            }

            var targetUser = ResolveTargetUser(expression, context);
            if (targetUser == null)
            {
                return null;
            }

            if (!_unplayedEpisodeIdsByUser.TryGetValue(targetUser.Id, out var ids))
            {
                var stopwatch = Stopwatch.StartNew();
                ids = context.LibraryManager.GetItemIds(new InternalItemsQuery(targetUser)
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    IsPlayed = false,
                    Recursive = true,
                    GroupByPresentationUniqueKey = false,
                });
                _unplayedEpisodeIdsByUser[targetUser.Id] = ids;

                context.Logger?.LogDebug("NextUnwatched prefilter: {Count} unplayed episodes for user {UserId} in {Ms}ms",
                    ids.Count, targetUser.Id, stopwatch.ElapsedMilliseconds);
            }

            // Fresh set per call - the caller owns and mutates the result.
            return [.. ids];
        }

        /// <summary>
        /// Decides whether an operator/value combination may ride the unplayed-episode
        /// prefilter: only "Equal true" and "NotEqual false" qualify. The value is parsed with
        /// the exact per-item semantics (Engine.ValidateAndParseBooleanValue - trim, strip
        /// quotes, case-insensitive bool parse); a value the compiled rule would reject stays
        /// per-item rather than guessing.
        /// </summary>
        /// <param name="ruleOperator">The rule operator.</param>
        /// <param name="targetValue">The rule target value.</param>
        /// <returns>True when the combination is provably "is the next unwatched episode".</returns>
        internal static bool RidesPrefilter(string? ruleOperator, string? targetValue)
        {
            var isEqual = string.Equals(ruleOperator, "Equal", StringComparison.Ordinal);
            if (!isEqual && !string.Equals(ruleOperator, "NotEqual", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(targetValue))
            {
                return false;
            }

            bool boolValue;
            try
            {
                boolValue = Engine.ValidateAndParseBooleanValue(targetValue, "NextUnwatched");
            }
            catch (ArgumentException)
            {
                return false;
            }

            // Equal true, or NotEqual false - both mean "IS the next unwatched episode".
            return isEqual == boolValue;
        }

        /// <summary>
        /// Resolves the rule's target user the same way Engine.BuildExpr does:
        /// Expression.UserId when set (an additional user), else the list's user.
        /// </summary>
        private static User? ResolveTargetUser(Expression expression, PrefilterContext context)
        {
            if (string.IsNullOrEmpty(expression.UserId))
            {
                return context.User;
            }

            if (!Guid.TryParse(expression.UserId, out var userGuid))
            {
                return null;
            }

            if (context.User != null && userGuid == context.User.Id)
            {
                return context.User;
            }

            return context.UserManager == null ? null : OperandFactory.GetUserById(context.UserManager, userGuid);
        }
    }
}
