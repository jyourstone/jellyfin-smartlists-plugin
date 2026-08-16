using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters
{
    /// <summary>
    /// Prefilter resolver for the SeriesName rule field.
    ///
    /// Unlike the DB-query resolvers, this one runs entirely in memory against the bulk-warmed
    /// series-name dump (OperandFactory.WarmSeriesNameCache) and the already-fetched item pool -
    /// zero extra queries:
    /// 1. The rule is compiled through Engine.CompileRule - the exact single-rule compilation
    ///    the per-item path uses - and evaluated against every dumped series name, yielding the
    ///    matching series ids plus whether the rule matches "".
    /// 2. Pool items are then classified through the SAME resolution order ExtractSeriesName
    ///    uses: Episode.SeriesId first, then the extras owner map (gated on being an extra),
    ///    else SeriesName "".
    ///
    /// Negative operators ride (<see cref="SupportsNegativeOperators"/> is true): per-item
    /// evaluation reads the very same cache entries the dump populated, so the complement is
    /// exact rather than approximated. Items whose series is NOT in the dump are always kept -
    /// the per-miss GetItemById fallback then decides per item. MatchRegex patterns matching ""
    /// are rejected centrally by CandidateSetBuilder before this resolver is consulted; the
    /// matchesEmpty inclusion below covers the remaining empty-matching operators (NotEqual,
    /// NotContains, IsNotIn) for seriesless pool items (movies, Series themselves, episodes
    /// without a usable SeriesId).
    ///
    /// Narrowing only happens when the context carries PoolItems + SeriesNamesById (SmartList
    /// sets the dump only after a successful warmup); otherwise the rule stays per-item.
    /// </summary>
    internal sealed class SeriesNamePrefilterResolver : IRulePrefilterResolver
    {
        /// <inheritdoc />
        public bool SupportsNegativeOperators => true;

        /// <inheritdoc />
        public HashSet<Guid>? Resolve(Expression expression, PrefilterContext context)
        {
            if (!string.Equals(expression.MemberName, "SeriesName", StringComparison.Ordinal))
            {
                return null;
            }

            var pool = context.PoolItems;
            var seriesNames = context.SeriesNamesById;
            if (pool == null || seriesNames == null)
            {
                return null;
            }

            var stopwatch = Stopwatch.StartNew();

            // Exact by construction: the same single-rule compilation the compiled rule sets
            // go through, evaluated against an operand carrying only SeriesName.
            var rule = Engine.CompileRule<Operand>(expression, context.User?.Id.ToString("N") ?? string.Empty, context.Logger);
            var probe = new Operand(string.Empty);
            bool Matches(string seriesName)
            {
                probe.SeriesName = seriesName;
                return rule(probe);
            }

            var matchingSeriesIds = new HashSet<Guid>();
            foreach (var entry in seriesNames)
            {
                if (Matches(entry.Value))
                {
                    matchingSeriesIds.Add(entry.Key);
                }
            }

            var matchesEmpty = Matches(string.Empty);

            var extraOwners = context.ExtraOwnerSeriesIds;
            var result = new HashSet<Guid>();
            foreach (var item in pool)
            {
                if (item == null)
                {
                    continue;
                }

                if (OperandFactory.TryGetEpisodeSeriesGuid(item, out var seriesId))
                {
                    // A dumped series resolves per item to exactly the cached name; a series
                    // missing from the dump is unknown and must be kept for the GetItemById
                    // fallback to decide.
                    if (matchingSeriesIds.Contains(seriesId) || !seriesNames.ContainsKey(seriesId))
                    {
                        result.Add(item.Id);
                    }
                }
                else if (OperandFactory.IsExtra(item))
                {
                    // Same gate as extraction (ExtraType non-empty): the owner map decides;
                    // an unmapped extra keeps SeriesName "". Without a map, keep every extra
                    // (the consumption-side exemption keeps them regardless).
                    if (extraOwners == null)
                    {
                        result.Add(item.Id);
                    }
                    else if (extraOwners.TryGetValue(item.Id, out var ownerSeriesId))
                    {
                        if (matchingSeriesIds.Contains(ownerSeriesId) || !seriesNames.ContainsKey(ownerSeriesId))
                        {
                            result.Add(item.Id);
                        }
                    }
                    else if (matchesEmpty)
                    {
                        result.Add(item.Id);
                    }
                }
                else if (matchesEmpty)
                {
                    // Seriesless pool items evaluate SeriesName "".
                    result.Add(item.Id);
                }
            }

            context.Logger?.LogDebug(
                "SeriesName prefilter: {Operator} '{Value}' matched {SeriesCount}/{DumpCount} series (empty match: {MatchesEmpty}) -> {ItemCount}/{PoolCount} candidate items in {Ms}ms",
                expression.Operator, expression.TargetValue, matchingSeriesIds.Count, seriesNames.Count, matchesEmpty, result.Count, pool.Count, stopwatch.ElapsedMilliseconds);

            return result;
        }
    }
}
