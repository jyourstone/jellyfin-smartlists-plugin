using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters
{
    /// <summary>
    /// Prefilter resolver for parent-aware Tags/Genres/Studios rules (IncludeParent* /
    /// OnlyParent*), whose per-item path runs the AncestorValueResolver walk over the pool.
    ///
    /// Shared two-query shape per rule:
    /// 1. A value-filtered GetItemIds over ALL item types. The Tags/Genres/StudioIds filters
    ///    match any BaseItem row including folders (Series/Season/library folders), so this
    ///    yields both leaf matches and value-carrying containers.
    /// 2. GetItemIds(AncestorIds = containerIds) for everything physically under the matched
    ///    containers - the DB's transitive parent chain + GetCollectionFolders, the same
    ///    edges the plugin walk follows.
    /// Candidates = direct matches UNION descendants. Superset holds because OnlyParent*
    /// only narrows the plugin match and final per-item evaluation always still runs.
    ///
    /// Inheritance edges the DB does NOT model, and how each is kept safe:
    /// - Extras inherit via GetOwner(), which writes no AncestorIds rows. Extras are
    ///   unconditionally exempt at the consumption points (SmartList.IsPrefilterExempt),
    ///   so no extra can ever be shrunk away by this resolver.
    /// - Symlinked/plugin-created virtual libraries: the plugin walk path-matches
    ///   CollectionFolders precisely because the server's GetCollectionFolders (which
    ///   populated the AncestorIds rows) can resolve to the wrong folder. Conservative
    ///   guard: if ANY value-carrying container is a CollectionFolder, the rule falls back
    ///   to no-prefilter (null) instead of attempting path expansion.
    ///
    /// Per-field pushdown (negative operators never ride - SupportsNegativeOperators stays
    /// false; MatchRegex never rides any of these fields, see ResolveMatchingNames):
    /// - Tags: Equal only, raw value into Tags= (the server cleans both sides, so the
    ///   cleaned exact match is a superset of the plugin's OrdinalIgnoreCase equality).
    ///   Tags are not by-name BaseItems and no tag-name dump API exists in either ABI, so
    ///   substring operators cannot be expanded to exact values.
    /// - Genres: Equal via the same raw pushdown into Genres= (name-based filter exists in
    ///   both ABIs and covers music genres - all genres share ItemValueType.Genre);
    ///   Contains/IsIn ride via the ItemValues-backed name dump
    ///   (IItemRepository.GetGenreNames + GetMusicGenreNames - GetGenreNames excludes the
    ///   music item types, so both calls are required), matched on normalized values and
    ///   pushed as exact names.
    /// - Studios: neither ABI has a name-based Studios filter, only the StudioIds join
    ///   through EXISTING Studio rows. Rule values are resolved against the by-name Studio
    ///   item dump on normalized values (never GetStudio(name) - that is CreateItemByName
    ///   and materializes phantom studios), cross-checked against GetStudioNames() so a
    ///   studio string whose by-name item the post-scan validator has not materialized yet
    ///   forces a fallback instead of a silent false negative.
    /// </summary>
    internal sealed class ParentValuesPrefilterResolver : IRulePrefilterResolver
    {
        /// <summary>
        /// Above this many matched dump names the pushdown query stops being cheap (a rule
        /// like Contains "a" can match most of the table) - the rule then stays per-item.
        /// </summary>
        internal const int MaxMatchedNames = 200;

        /// <summary>
        /// Genre name dump for this filter run (the resolver lives for one
        /// CandidateSetBuilder.Build call); null when unavailable.
        /// </summary>
        private IReadOnlyList<string>? _genreNames;
        private bool _genreNamesAttempted;

        /// <summary>
        /// ItemValues-backed studio name dump for this filter run; null when unavailable.
        /// </summary>
        private IReadOnlyList<string>? _studioNames;
        private bool _studioNamesAttempted;

        /// <summary>
        /// Materialized by-name Studio items (id, name) for this filter run; null when
        /// unavailable.
        /// </summary>
        private IReadOnlyList<(Guid Id, string Name)>? _studioItems;
        private bool _studioItemsAttempted;

        /// <inheritdoc />
        public HashSet<Guid>? Resolve(Expression expression, PrefilterContext context)
        {
            if (context.LibraryManager == null || !SmartList.IsParentAwareListExpression(expression))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(expression.TargetValue))
            {
                return null;
            }

            return expression.MemberName switch
            {
                "Tags" => ResolveTags(expression, context),
                "Genres" => ResolveGenres(expression, context),
                "Studios" => ResolveStudios(expression, context),
                _ => null,
            };
        }

        private static HashSet<Guid>? ResolveTags(Expression expression, PrefilterContext context)
        {
            var push = ResolveTagPushdownValues(expression.Operator, expression.TargetValue);
            if (push == null)
            {
                return null;
            }

            return ExpandDirectAndDescendants(context, query => query.Tags = push, expression);
        }

        private HashSet<Guid>? ResolveGenres(Expression expression, PrefilterContext context)
        {
            string[] push;
            if (string.Equals(expression.Operator, "Equal", StringComparison.Ordinal))
            {
                // Raw pushdown - the server cleans both sides of Genres=, so the cleaned
                // exact match is a superset of the plugin's OrdinalIgnoreCase equality.
                // No dump dependency for the most common operator.
                push = [expression.TargetValue];
            }
            else
            {
                var names = GetGenreNames(context);
                if (names == null)
                {
                    return null;
                }

                var matched = ResolveMatchingNames(names, expression.Operator, expression.TargetValue);
                if (matched == null || matched.Count == 0)
                {
                    // Zero matches would be a hard "nothing matches" claim resting on the
                    // completeness of the two-method dump union - stay per-item instead.
                    return null;
                }

                push = [.. matched];
            }

            return ExpandDirectAndDescendants(context, query => query.Genres = push, expression);
        }

        private HashSet<Guid>? ResolveStudios(Expression expression, PrefilterContext context)
        {
            var itemValueNames = GetStudioNames(context);
            if (itemValueNames == null)
            {
                return null;
            }

            var studioItems = GetStudioItems(context);
            if (studioItems == null)
            {
                return null;
            }

            var ids = ResolveStudioIds(itemValueNames, studioItems, expression.Operator, expression.TargetValue);
            if (ids == null || ids.Length == 0)
            {
                return null;
            }

            return ExpandDirectAndDescendants(context, query => query.StudioIds = ids, expression);
        }

        /// <summary>
        /// Decides whether a Tags rule can ride and with which pushdown values.
        /// </summary>
        /// <param name="ruleOperator">The rule operator.</param>
        /// <param name="targetValue">The rule target value.</param>
        /// <returns>The values to push into Tags=, or null when the rule stays per-item.</returns>
        internal static string[]? ResolveTagPushdownValues(string ruleOperator, string targetValue)
        {
            // Equal only: the DB Tags filter is a cleaned EXACT match and tags are not
            // by-name BaseItems, so there is no dump to expand Contains/IsIn/regex
            // against. IsIn in particular is per-term SUBSTRING matching plugin-side and
            // must not be pushed as exact terms.
            if (!string.Equals(ruleOperator, "Equal", StringComparison.Ordinal))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(targetValue))
            {
                return null;
            }

            return [targetValue];
        }

        /// <summary>
        /// Evaluates the rule operator against dumped names on the normalized form
        /// (<see cref="PrefilterValueCleaner.MatchNormalize"/>). Deliberately BROADER than
        /// the plugin's per-item semantics: the dump holds one representative raw variant
        /// per server-cleaned group, so plugin-exact matching would miss groups whose other
        /// variants match (false negatives). Broader matching only adds candidates.
        /// </summary>
        /// <param name="storedNames">Dumped names (one representative per cleaned group).</param>
        /// <param name="ruleOperator">The rule operator.</param>
        /// <param name="targetValue">The rule target value.</param>
        /// <returns>
        /// The matched names (empty when nothing matches), or null when the rule cannot
        /// ride: unsupported operator (MatchRegex is case-sensitive plugin-side and a lone
        /// representative variant cannot soundly stand in for its group; negatives never
        /// ride), an empty normalized needle (would match everything), a matched
        /// whitespace-only name (cannot be pushed as a query value), or more than
        /// <see cref="MaxMatchedNames"/> matches.
        /// </returns>
        internal static List<string>? ResolveMatchingNames(IReadOnlyCollection<string> storedNames, string ruleOperator, string targetValue)
        {
            ArgumentNullException.ThrowIfNull(storedNames);

            Func<string, bool> matches;
            switch (ruleOperator)
            {
                case "Equal":
                    var equalKey = PrefilterValueCleaner.MatchNormalize(targetValue);
                    matches = normalized => string.Equals(normalized, equalKey, StringComparison.Ordinal);
                    break;
                case "Contains":
                    var needle = PrefilterValueCleaner.MatchNormalize(targetValue);
                    if (needle.Length == 0)
                    {
                        return null;
                    }

                    matches = normalized => normalized.Contains(needle, StringComparison.Ordinal);
                    break;
                case "IsIn":
                    // Mirrors Engine.AnyItemIsInList term splitting (semicolons, trimmed,
                    // empties dropped); each term is a substring match plugin-side.
                    var terms = targetValue.Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(term => term.Trim())
                        .Where(term => term.Length > 0)
                        .Select(PrefilterValueCleaner.MatchNormalize)
                        .ToList();
                    if (terms.Count == 0 || terms.Any(term => term.Length == 0))
                    {
                        // No terms, or a term that normalizes to empty (e.g. "!!") - the
                        // plugin still substring-matches such a term against raw values,
                        // so dropping it would under-match. Stay per-item.
                        return null;
                    }

                    matches = normalized => terms.Any(term => normalized.Contains(term, StringComparison.Ordinal));
                    break;
                default:
                    return null;
            }

            var matched = new List<string>();
            foreach (var name in storedNames)
            {
                if (name == null)
                {
                    continue;
                }

                if (!matches(PrefilterValueCleaner.MatchNormalize(name)))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    // A whitespace-only value cannot be pushed (query translation for
                    // blank values is undefined), and silently skipping a MATCHED name
                    // could drop a true match. Stay per-item.
                    return null;
                }

                matched.Add(name);
                if (matched.Count > MaxMatchedNames)
                {
                    return null;
                }
            }

            return matched;
        }

        /// <summary>
        /// Resolves a Studios rule to by-name Studio item ids, with the materialization
        /// coverage check: the StudioIds join only reaches items through an EXISTING Studio
        /// row (CleanName == the item value's CleanValue), so every rule-matching
        /// ItemValues name must have a Studio item under the same <see
        /// cref="PrefilterValueCleaner.CleanValue"/> key - equality on that key implies
        /// join reachability on both ABIs. A matching name without one (post-scan
        /// validator lag) forces a fallback.
        /// </summary>
        /// <param name="itemValueNames">GetStudioNames() dump (complete per cleaned group).</param>
        /// <param name="studioItems">Materialized by-name Studio items.</param>
        /// <param name="ruleOperator">The rule operator.</param>
        /// <param name="targetValue">The rule target value.</param>
        /// <returns>Studio ids to push into StudioIds=, or null when the rule stays per-item.</returns>
        internal static Guid[]? ResolveStudioIds(IReadOnlyCollection<string> itemValueNames, IReadOnlyList<(Guid Id, string Name)> studioItems, string ruleOperator, string targetValue)
        {
            ArgumentNullException.ThrowIfNull(studioItems);

            var matched = ResolveMatchingNames(itemValueNames, ruleOperator, targetValue);
            if (matched == null || matched.Count == 0)
            {
                // Zero matches would be a hard "nothing matches" claim - stay per-item.
                return null;
            }

            var idsByCleanName = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
            foreach (var (id, name) in studioItems)
            {
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var key = PrefilterValueCleaner.CleanValue(name);
                if (!idsByCleanName.TryGetValue(key, out var ids))
                {
                    ids = [];
                    idsByCleanName[key] = ids;
                }

                ids.Add(id);
            }

            var result = new HashSet<Guid>();
            foreach (var name in matched)
            {
                if (!idsByCleanName.TryGetValue(PrefilterValueCleaner.CleanValue(name), out var ids))
                {
                    // Studio string present in ItemValues but its by-name item is not
                    // materialized yet - the per-item path (raw BaseItem.Studios strings)
                    // would still match those items, and no StudioIds query can reach them.
                    return null;
                }

                result.UnionWith(ids);
            }

            return [.. result];
        }

        /// <summary>
        /// Runs the shared two-query expansion: value-filtered direct matches over all item
        /// types, then descendants of every value-carrying container via AncestorIds, with
        /// the conservative CollectionFolder guard. All queries run user-neutral with
        /// GroupByPresentationUniqueKey pinned off (the constructor default collapses
        /// alternate versions when a user is set); visibility is restored by the caller
        /// intersecting with the already user-scoped pool.
        /// </summary>
        private static HashSet<Guid>? ExpandDirectAndDescendants(PrefilterContext context, Action<InternalItemsQuery> applyValueFilter, Expression expression)
        {
            var stopwatch = Stopwatch.StartNew();

            var directQuery = new InternalItemsQuery { GroupByPresentationUniqueKey = false };
            applyValueFilter(directQuery);
            var direct = context.LibraryManager.GetItemIds(directQuery);

            var containerQuery = new InternalItemsQuery { GroupByPresentationUniqueKey = false, IsFolder = true };
            applyValueFilter(containerQuery);
            var containers = context.LibraryManager.GetItemList(containerQuery);

            var containerIds = new List<Guid>();
            foreach (var container in containers)
            {
                if (container == null)
                {
                    continue;
                }

                if (container is CollectionFolder)
                {
                    // Conservative virtual-library guard: the plugin walk path-matches
                    // CollectionFolders because the server's GetCollectionFolders (which
                    // populates AncestorIds) can resolve symlinked/virtual libraries to the
                    // wrong folder - AncestorIds expansion is not a superset then.
                    context.Logger?.LogDebug(
                        "Parent-values prefilter: {Field} {Operator} matched CollectionFolder '{Folder}' - falling back to no-prefilter for this rule",
                        expression.MemberName, expression.Operator, container.Name);
                    return null;
                }

                containerIds.Add(container.Id);
            }

            var result = new HashSet<Guid>(direct);
            if (containerIds.Count > 0)
            {
                var descendantsQuery = new InternalItemsQuery
                {
                    GroupByPresentationUniqueKey = false,
                    AncestorIds = [.. containerIds],
                };
                result.UnionWith(context.LibraryManager.GetItemIds(descendantsQuery));
            }

            context.Logger?.LogDebug(
                "Parent-values prefilter: {Field} {Operator} -> {DirectCount} direct matches + {ContainerCount} containers -> {CandidateCount} candidates in {Ms}ms",
                expression.MemberName, expression.Operator, direct.Count, containerIds.Count, result.Count, stopwatch.ElapsedMilliseconds);
            return result;
        }

        /// <summary>
        /// Gets the ItemValues-backed genre name dump (regular + music genres - all genres
        /// share ItemValueType.Genre on the query side, but GetGenreNames excludes the
        /// music item types, so both calls are required for completeness). Null when the
        /// item repository is unavailable.
        /// </summary>
        private IReadOnlyList<string>? GetGenreNames(PrefilterContext context)
        {
            if (_genreNamesAttempted)
            {
                return _genreNames;
            }

            _genreNamesAttempted = true;
            var repository = context.ItemRepository;
            if (repository == null)
            {
                return null;
            }

            var names = new List<string>();
            names.AddRange(repository.GetGenreNames());
            names.AddRange(repository.GetMusicGenreNames());
            _genreNames = names;
            return _genreNames;
        }

        /// <summary>
        /// Gets the ItemValues-backed studio name dump. Null when the item repository is
        /// unavailable.
        /// </summary>
        private IReadOnlyList<string>? GetStudioNames(PrefilterContext context)
        {
            if (_studioNamesAttempted)
            {
                return _studioNames;
            }

            _studioNamesAttempted = true;
            _studioNames = context.ItemRepository?.GetStudioNames();
            return _studioNames;
        }

        /// <summary>
        /// Gets the materialized by-name Studio items via a plain item query - never
        /// GetStudio(name), which is CreateItemByName in both ABIs and would materialize
        /// phantom Studio items for typo'd rule values.
        /// </summary>
        private IReadOnlyList<(Guid Id, string Name)>? GetStudioItems(PrefilterContext context)
        {
            if (_studioItemsAttempted)
            {
                return _studioItems;
            }

            _studioItemsAttempted = true;
            var items = context.LibraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Studio],
                GroupByPresentationUniqueKey = false,
            });

            var studios = new List<(Guid Id, string Name)>(items.Count);
            foreach (var item in items)
            {
                if (item?.Name is { Length: > 0 } name)
                {
                    studios.Add((item.Id, name));
                }
            }

            _studioItems = studios;
            return _studioItems;
        }
    }
}
