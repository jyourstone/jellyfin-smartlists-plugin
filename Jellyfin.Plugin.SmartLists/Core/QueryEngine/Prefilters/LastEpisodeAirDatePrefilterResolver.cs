using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters
{
    /// <summary>
    /// Prefilter resolver for the LastEpisodeAirDate rule field (Series-only).
    ///
    /// The per-item path runs one AncestorIds episode query PER SERIES to compute the max
    /// PremiereDate (OperandFactory.ExtractLastEpisodeAirDate). This resolver replaces S
    /// per-series queries with ONE date-range episode query via the max/exists equivalence:
    /// - After/NewerThan X: "max episode date passes X" iff the series has ANY non-virtual
    ///   episode with PremiereDate in range - one GetItemList(MinPremiereDate = X) mapped to
    ///   series ids IS the candidate set. Server Min is inclusive (&gt;=, both ABIs) while
    ///   plugin After is strict (&gt;), so boundary series are false positives - harmless,
    ///   the final per-item evaluation always still runs on survivors.
    /// - Before/OlderThan X: "max &lt; X" iff NOT EXISTS episode &gt;= X - candidates are the
    ///   pool's Series MINUS the series returned by the same range query. The complement is
    ///   exact for Before (a series with an episode &gt;= X has max &gt;= X and provably fails
    ///   the strict &lt;); for OlderThan the cutoff is recomputed from UtcNow at every per-item
    ///   evaluation, so the exclusion threshold gets <see cref="OlderThanComplementMargin"/>
    ///   added - excluding only series that stay non-matching for at least that long after the
    ///   plan is built (boundary series stay as false positives instead).
    /// - Equal X: superset window [X, X+1day] (Max inclusive per server &lt;=; the plugin's
    ///   in-day window is [X, X+1day), the extra boundary second is a false positive).
    /// - NotEqual (day-window negation) and Weekday (day-of-week OF the max) need the actual
    ///   max date, not existence - they never ride.
    ///
    /// GATE: the whole resolver is off when the rule sets IncludeUnknownDates - the compiled
    /// rule then matches operand 0 (Engine.BuildDateExpression sentinel OR-branch), which every
    /// series without a dated non-virtual episode and, in mixed pools, every non-Series item
    /// carries, so no date range can bound the rule. Conversely, at the default setting the
    /// 0-sentinel AND-branch means only Series can match at all, which is why the candidate
    /// sets built here contain series ids only.
    ///
    /// Query notes (verified against both ABI server sources):
    /// - User-neutral, GroupByPresentationUniqueKey pinned off, IsVirtualItem = false to mirror
    ///   the extraction query exactly (upcoming/missing episodes must not contaminate the max).
    /// - TopParentIds is deliberately NOT set: the per-series extraction query is not
    ///   library-scoped either, and visibility is restored by intersecting with the pool.
    /// - Episodes are materialized (GetItemList) because ids alone cannot give SeriesId;
    ///   <see cref="MaxEpisodeResults"/> caps the materialization (GetCount runs first) - an
    ///   over-cap query bails to no-shrink rather than loading the world.
    /// - Episode-to-series attribution uses the SeriesId column with a FindSeriesId() parent
    ///   walk fallback. Extraction finds episodes via the AncestorIds table instead; both
    ///   derive from the same parent chain, but a stale AncestorIds row for an orphaned
    ///   episode could still diverge (spec-documented minor risk).
    /// </summary>
    internal sealed class LastEpisodeAirDatePrefilterResolver : IRulePrefilterResolver
    {
        /// <summary>
        /// Materialization cap for the episode range query. Beyond this the resolver bails to
        /// no-shrink: a window this wide means the rule barely filters anyway, and loading that
        /// many BaseItems would cost more than the per-series queries it replaces.
        /// </summary>
        internal const int MaxEpisodeResults = 50_000;

        /// <summary>
        /// Safety margin added to the OlderThan exclusion threshold. The per-item cutoff is
        /// UtcNow-relative and recomputed per evaluation, so it keeps moving forward while the
        /// filter run progresses; excluding only series with an episode at/after
        /// cutoff-at-build-time + margin keeps the complement a guaranteed superset for any
        /// run shorter than the margin (a boundary sliver of extra false positives is the cost).
        /// </summary>
        internal static readonly TimeSpan OlderThanComplementMargin = TimeSpan.FromDays(1);

        /// <inheritdoc />
        public HashSet<Guid>? Resolve(Expression expression, PrefilterContext context)
        {
            if (!string.Equals(expression.MemberName, "LastEpisodeAirDate", StringComparison.Ordinal)
                || context.LibraryManager == null)
            {
                return null;
            }

            var plan = TryBuildPlan(expression.Operator, expression.TargetValue, expression.IncludeUnknownDates, DateTime.UtcNow);
            if (plan == null)
            {
                return null;
            }

            if (plan.IsComplement && context.PoolItems == null)
            {
                // The complement is only exact over a known series universe; without the pool
                // the rule stays per-item.
                return null;
            }

            var stopwatch = Stopwatch.StartNew();
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                MinPremiereDate = plan.MinPremiereDate,
                MaxPremiereDate = plan.MaxPremiereDate,
                IsVirtualItem = false,
                Recursive = true,
                GroupByPresentationUniqueKey = false,
            };

            var episodeCount = context.LibraryManager.GetCount(query);
            if (episodeCount > MaxEpisodeResults)
            {
                context.Logger?.LogDebug(
                    "LastEpisodeAirDate prefilter: {Operator} '{Value}' range holds {Count} episodes (cap {Cap}) - rule stays per-item",
                    expression.Operator, expression.TargetValue, episodeCount, MaxEpisodeResults);
                return null;
            }

            var episodes = context.LibraryManager.GetItemList(query);
            var seriesWithEpisodeInRange = MapToSeriesIds(episodes);

            var result = plan.IsComplement
                ? ComplementOverPoolSeries(context.PoolItems!, seriesWithEpisodeInRange)
                : seriesWithEpisodeInRange;

            context.Logger?.LogDebug(
                "LastEpisodeAirDate prefilter: {Operator} '{Value}' -> {EpisodeCount} episodes across {SeriesCount} series -> {CandidateCount} candidate series ({Mode}) in {Ms}ms",
                expression.Operator, expression.TargetValue, episodes.Count, seriesWithEpisodeInRange.Count, result.Count,
                plan.IsComplement ? "complement" : "direct", stopwatch.ElapsedMilliseconds);

            return result;
        }

        /// <summary>
        /// Immutable description of the episode range query an operator maps to.
        /// </summary>
        /// <param name="MinPremiereDate">Inclusive lower bound pushed to the server.</param>
        /// <param name="MaxPremiereDate">Inclusive upper bound, only set for Equal's day window.</param>
        /// <param name="IsComplement">True when candidates are pool series MINUS the query's series.</param>
        internal sealed record QueryPlan(DateTime MinPremiereDate, DateTime? MaxPremiereDate, bool IsComplement);

        /// <summary>
        /// Maps an operator/value combination to its episode range query, or null when the rule
        /// cannot ride (unknown-dates gate, non-riding operator, or a value the compiled rule
        /// would reject). Value parsing mirrors the Engine's per-item semantics exactly:
        /// absolute dates are strict yyyy-MM-dd (UTC), relative values are number:unit with the
        /// same unit table BuildRelativeDateCutoffExpression uses.
        /// </summary>
        /// <param name="ruleOperator">The rule operator.</param>
        /// <param name="targetValue">The rule target value.</param>
        /// <param name="includeUnknownDates">The rule's IncludeUnknownDates option.</param>
        /// <param name="utcNow">Reference time for relative operators.</param>
        /// <returns>The query plan, or null when the rule stays per-item.</returns>
        internal static QueryPlan? TryBuildPlan(string? ruleOperator, string? targetValue, bool? includeUnknownDates, DateTime utcNow)
        {
            // With unknown dates included, the compiled rule matches every dateless operand
            // (series with zero dated non-virtual episodes, non-Series items) regardless of
            // operator - no date range bounds that, so the rule contributes all items.
            if (includeUnknownDates == true)
            {
                return null;
            }

            switch (ruleOperator)
            {
                case "After":
                    return TryParseAbsoluteDate(targetValue, out var afterDate)
                        ? new QueryPlan(afterDate, null, IsComplement: false)
                        : null;

                case "NewerThan":
                    // Floored to whole seconds: the per-item comparison floors both sides to
                    // unix seconds, so a sub-second Min could drop an episode whose truncated
                    // timestamp still passes the compiled rule.
                    return TryComputeRelativeCutoff(targetValue, utcNow, out var newerCutoff)
                        ? new QueryPlan(FloorToWholeSeconds(newerCutoff), null, IsComplement: false)
                        : null;

                case "Before":
                    return TryParseAbsoluteDate(targetValue, out var beforeDate)
                        ? new QueryPlan(beforeDate, null, IsComplement: true)
                        : null;

                case "OlderThan":
                    return TryComputeRelativeCutoff(targetValue, utcNow, out var olderCutoff)
                        ? new QueryPlan(FloorToWholeSeconds(olderCutoff + OlderThanComplementMargin), null, IsComplement: true)
                        : null;

                case "Equal":
                    return TryParseAbsoluteDate(targetValue, out var day)
                        ? new QueryPlan(day, day.AddDays(1), IsComplement: false)
                        : null;

                default:
                    // NotEqual and Weekday need the actual max date; anything else is not a
                    // date operator. Operator comparison is Ordinal, mirroring the engine switch.
                    return null;
            }
        }

        /// <summary>
        /// Attributes materialized episodes to their series ids: SeriesId column first, then
        /// the FindSeriesId() parent walk (the same chain AncestorIds rows derive from). An
        /// episode with no series ancestor is skipped - the per-series extraction cannot see
        /// it either.
        /// </summary>
        /// <param name="episodes">Materialized items from the range query.</param>
        /// <returns>Distinct series ids owning at least one of the episodes.</returns>
        internal static HashSet<Guid> MapToSeriesIds(IReadOnlyList<BaseItem> episodes)
        {
            var result = new HashSet<Guid>();
            foreach (var item in episodes)
            {
                if (item is not Episode episode)
                {
                    continue;
                }

                var seriesId = episode.SeriesId;
                if (seriesId == Guid.Empty)
                {
                    seriesId = episode.FindSeriesId();
                }

                if (seriesId != Guid.Empty)
                {
                    result.Add(seriesId);
                }
            }

            return result;
        }

        /// <summary>
        /// Exact complement for Before/OlderThan: every pool Series without an episode at/after
        /// the threshold. Series with no dated episodes at all stay candidates (they carry the
        /// 0 sentinel and fail the compiled rule - a harmless false positive); non-Series pool
        /// items cannot match the rule under the unknown-dates gate and are never candidates.
        /// </summary>
        /// <param name="poolItems">The already user-scoped item pool.</param>
        /// <param name="excludedSeriesIds">Series with an episode at/after the threshold.</param>
        /// <returns>Candidate series ids.</returns>
        internal static HashSet<Guid> ComplementOverPoolSeries(IReadOnlyList<BaseItem> poolItems, HashSet<Guid> excludedSeriesIds)
        {
            var result = new HashSet<Guid>();
            foreach (var item in poolItems)
            {
                if (item is Series && !excludedSeriesIds.Contains(item.Id))
                {
                    result.Add(item.Id);
                }
            }

            return result;
        }

        /// <summary>
        /// Parses an absolute rule date with the Engine's exact format (strict yyyy-MM-dd,
        /// invariant culture, treated as UTC midnight).
        /// </summary>
        private static bool TryParseAbsoluteDate(string? targetValue, out DateTime result)
        {
            if (DateTime.TryParseExact(targetValue, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                result = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                return true;
            }

            result = default;
            return false;
        }

        /// <summary>
        /// Computes the relative cutoff (UtcNow minus the rule's number:unit span) with the
        /// exact unit table of Engine.BuildRelativeDateCutoffExpression.
        /// </summary>
        private static bool TryComputeRelativeCutoff(string? targetValue, DateTime utcNow, out DateTime cutoff)
        {
            cutoff = default;

            var parts = (targetValue ?? string.Empty).Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int num) || num < 0)
            {
                return false;
            }

            var reference = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), TimeSpan.Zero);
            DateTimeOffset cutoffDate;
            switch (parts[1].ToLowerInvariant())
            {
                case "hours":
                    cutoffDate = reference.AddHours(-num);
                    break;
                case "days":
                    cutoffDate = reference.AddDays(-num);
                    break;
                case "weeks":
                    cutoffDate = reference.AddDays(-num * 7);
                    break;
                case "months":
                    cutoffDate = reference.AddMonths(-num);
                    break;
                case "years":
                    cutoffDate = reference.AddYears(-num);
                    break;
                default:
                    return false;
            }

            cutoff = cutoffDate.UtcDateTime;
            return true;
        }

        /// <summary>
        /// Truncates to whole unix seconds, matching how both the per-item cutoff and the
        /// extracted max date are floored via ToUnixTimeSeconds.
        /// </summary>
        private static DateTime FloorToWholeSeconds(DateTime value)
        {
            var seconds = new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero).ToUnixTimeSeconds();
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
    }
}
