using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters
{
    /// <summary>
    /// Prefilter resolver for the people rule fields (People, Actors, Directors, ...).
    ///
    /// Two steps, preserving the per-item operator semantics exactly:
    /// 1. Resolve which STORED person names the rule matches by evaluating the operator in
    ///    memory (via the same <see cref="Engine"/> helpers the compiled rules bind) against
    ///    a bulk name dump from the people table. This step is mandatory, not an
    ///    optimization: the item query's Person clause is byte-exact case-sensitive on
    ///    Jellyfin 10.11, so passing the user's raw rule value would silently under-match.
    /// 2. One user-neutral GetItemIds(Person = exact stored name) query per matched name.
    ///    The union over matched names is a guaranteed superset of the items whose people
    ///    list satisfies the rule, because every name in an item's extracted people list
    ///    also exists as a people-table row reachable through the same dump.
    ///
    /// Role handling differs per ABI:
    /// - net10 (Jellyfin 12): names are dumped via GetPeopleNames, whose SQL is a plain
    ///   ordinal Distinct over Name - unlike GetPeople, whose no-ItemId branch collapses to
    ///   ONE arbitrary row per LOWERCASED name and would drop case-only duplicate spellings
    ///   (each stored spelling needs its own byte-exact item query). Role-specific fields
    ///   push the role into the people query itself (InternalPeopleQuery.PersonTypes is
    ///   translated BEFORE the name projection) and into the item query (Person +
    ///   PersonTypes compose). Never role-filter dump results in memory on 12.
    /// - net9 (Jellyfin 10.11): the GetPeople dump has no collapse and returns one row per
    ///   (Name, Type); it runs ONCE per filter run and each role's names are derived from
    ///   it in memory with the same Type.ToString() comparison CategorizePeople uses.
    ///   PersonTypes is a silent no-op on 10.11 item queries, so the per-name item query is
    ///   any-role there - a valid superset; role verification stays per-item.
    ///
    /// ActorRoles never rides: role strings live on the people map row and are not
    /// filterable in either ABI. Negative operators are rejected centrally by
    /// <see cref="CandidateSetBuilder"/> (SupportsNegativeOperators stays false).
    /// </summary>
    internal sealed class PeoplePrefilterResolver : IRulePrefilterResolver
    {
        /// <summary>
        /// Above this many matched names the per-name queries stop being cheap (a rule like
        /// Contains "a" can match most of the people table) - the rule then stays per-item.
        /// </summary>
        internal const int MaxMatchedNames = 200;

        /// <summary>
        /// Field name to people-map role, mirroring CategorizePeople's switch labels
        /// verbatim - including "SoundEngineer" and "Penciler", which match no PersonKind
        /// name ("Engineer"/"Penciller"): per-item extraction therefore always yields empty
        /// lists for those two fields, and the prefilter must agree with that, not fix it.
        /// "People" is role-agnostic (null). ActorRoles is deliberately absent.
        /// </summary>
        private static readonly Dictionary<string, string?> RoleByField = new(StringComparer.Ordinal)
        {
            ["People"] = null,
            ["Actors"] = "Actor",
            ["Directors"] = "Director",
            ["Composers"] = "Composer",
            ["Writers"] = "Writer",
            ["GuestStars"] = "GuestStar",
            ["Producers"] = "Producer",
            ["Conductors"] = "Conductor",
            ["Lyricists"] = "Lyricist",
            ["Arrangers"] = "Arranger",
            ["SoundEngineers"] = "SoundEngineer",
            ["Mixers"] = "Mixer",
            ["Remixers"] = "Remixer",
            ["Creators"] = "Creator",
            ["PersonArtists"] = "Artist",
            ["PersonAlbumArtists"] = "AlbumArtist",
            ["Authors"] = "Author",
            ["Illustrators"] = "Illustrator",
            ["Pencilers"] = "Penciler",
            ["Inkers"] = "Inker",
            ["Colorists"] = "Colorist",
            ["Letterers"] = "Letterer",
            ["CoverArtists"] = "CoverArtist",
            ["Editors"] = "Editor",
            ["Translators"] = "Translator",
        };

        /// <summary>
        /// Per-role name dumps for this filter run (the resolver lives for one
        /// CandidateSetBuilder.Build call). Key is the role, "" for role-agnostic.
        /// </summary>
        private readonly Dictionary<string, IReadOnlyList<string>?> _namesByRole = new(StringComparer.Ordinal);

        /// <summary>
        /// Returns whether this resolver handles the given rule field.
        /// </summary>
        /// <param name="fieldName">The rule field name.</param>
        /// <returns>True when the field is a people field this resolver can bound.</returns>
        internal static bool HandlesField(string fieldName) => RoleByField.ContainsKey(fieldName);

        /// <inheritdoc />
        public HashSet<Guid>? Resolve(Expression expression, PrefilterContext context)
        {
            if (context.LibraryManager == null || !RoleByField.TryGetValue(expression.MemberName, out var role))
            {
                return null;
            }

            var storedNames = GetStoredNames(context, role);
            if (storedNames == null)
            {
                return null;
            }

            var matched = ResolveMatchingNames(storedNames, expression.Operator, expression.TargetValue);
            if (matched == null)
            {
                context.Logger?.LogDebug("People prefilter: rule {Field} {Operator} not pushdownable ({NameCount} stored names)",
                    expression.MemberName, expression.Operator, storedNames.Count);
                return null;
            }

            var stopwatch = Stopwatch.StartNew();
            var result = new HashSet<Guid>();
            foreach (var name in matched)
            {
                // User-neutral with grouping pinned off: the constructor default
                // (GroupByPresentationUniqueKey = true) collapses alternate versions when a
                // user is set and would drop pool items. Visibility is restored later by
                // intersecting with the already user-scoped pool.
                var query = new InternalItemsQuery
                {
                    Person = name,
                    GroupByPresentationUniqueKey = false,
                };
#if NET10_0_OR_GREATER
                // Jellyfin 12 composes Person + PersonTypes into an indexed role-specific
                // lookup. On 10.11 PersonTypes is declared but never read by TranslateQuery,
                // so the query stays any-role there (still a superset).
                if (role != null)
                {
                    query.PersonTypes = [role];
                }
#endif
                result.UnionWith(context.LibraryManager.GetItemIds(query));
            }

            context.Logger?.LogDebug("People prefilter: {Field} {Operator} matched {NameCount} names -> {ItemCount} candidate items in {Ms}ms",
                expression.MemberName, expression.Operator, matched.Count, result.Count, stopwatch.ElapsedMilliseconds);
            return result;
        }

        /// <summary>
        /// Evaluates the rule operator against the stored names with the plugin's exact
        /// per-item semantics (each name is tested as a single-element people list through
        /// the same Engine helpers the compiled rules use).
        /// </summary>
        /// <param name="storedNames">Stored person names from the people-table dump.</param>
        /// <param name="ruleOperator">The rule operator.</param>
        /// <param name="targetValue">The rule target value.</param>
        /// <returns>
        /// The matched names, empty when no stored name satisfies the rule (a hard "nothing
        /// matches" claim), or null when the rule cannot ride the prefilter (unsupported
        /// operator, empty-matching or invalid regex, a matched whitespace-only name that
        /// cannot be queried, or more than <see cref="MaxMatchedNames"/> matches).
        /// </returns>
        internal static List<string>? ResolveMatchingNames(IReadOnlyCollection<string> storedNames, string ruleOperator, string targetValue)
        {
            ArgumentNullException.ThrowIfNull(storedNames);

            Func<string, bool> matches;
            switch (ruleOperator)
            {
                case "Equal":
                    matches = name => Engine.AnyItemEquals([name], targetValue);
                    break;
                case "Contains":
                    matches = name => Engine.AnyItemContains([name], targetValue);
                    break;
                case "IsIn":
                    matches = name => Engine.AnyItemIsInList([name], targetValue);
                    break;
                case "MatchRegex":
                    try
                    {
                        // An empty people list is evaluated against "", so a pattern that
                        // matches the empty string matches items with no people at all -
                        // which no name-derived candidate set can bound.
                        if (Regex.IsMatch(string.Empty, targetValue))
                        {
                            return null;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Invalid pattern - the per-item path surfaces the error.
                        return null;
                    }

                    matches = name => Engine.AnyRegexMatch([name], targetValue);
                    break;
                default:
                    // Negative operators and anything unexpected stay per-item.
                    return null;
            }

            var matched = new List<string>();
            foreach (var name in storedNames)
            {
                if (string.IsNullOrEmpty(name))
                {
                    // Per-item extraction drops null/empty names, so they can never satisfy a rule.
                    continue;
                }

                if (!matches(name))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    // TranslateQuery silently drops a whitespace Person clause (returning ALL
                    // items), so a matched whitespace-only name cannot be queried - and
                    // skipping just this name could drop a true match. Stay per-item.
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
        /// Gets the stored person names for the given role (null = any role), cached for the
        /// duration of this filter run. Returns null when the dump is unavailable.
        /// </summary>
        private IReadOnlyList<string>? GetStoredNames(PrefilterContext context, string? role)
        {
            var cacheKey = role ?? string.Empty;
            if (_namesByRole.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

#if NET10_0_OR_GREATER
            // Jellyfin 12: GetPeopleNames only - its SQL is an ordinal Distinct over Name,
            // so every distinct stored spelling survives and gets its own byte-exact item
            // query. GetPeople's no-ItemId branch instead collapses to one arbitrary row
            // per LOWERCASED name, silently losing case-only duplicate spellings (a false
            // negative for Equal/Contains/IsIn and a wrong hard "nothing matches" for
            // case-sensitive MatchRegex). Role narrowing must happen inside the people
            // query (PersonTypes is ctor-only there and translated before the name
            // projection) - never in memory against the dump.
            var query = role == null
                ? new InternalPeopleQuery()
                : new InternalPeopleQuery([role], []);

            IReadOnlyList<string>? names = context.LibraryManager.GetPeopleNames(query);
#else
            // Jellyfin 10.11 returns one GetPeople row per (Name, Type) with no collapse:
            // dump the table once per filter run and derive each role's names in memory
            // with the same Type.ToString() comparison the per-item CategorizePeople
            // switch uses. (PersonTypes narrowing in the people query is 12-only behavior,
            // and 10.11's GetPeopleNames cannot be used - it drops the Type column.)
            var names = FilterNamesByRole(GetDumpRows(context), role);
#endif
            _namesByRole[cacheKey] = names;
            return names;
        }

#if !NET10_0_OR_GREATER
        /// <summary>
        /// The unrestricted (Name, Type) dump rows for this filter run, materialized at most
        /// once; null when the dump is unavailable. See <see cref="_dumpAttempted"/>.
        /// </summary>
        private IReadOnlyList<(string Name, string? Type)>? _dumpRows;

        /// <summary>
        /// Whether the dump has been attempted, so a failed dump is not retried per role.
        /// </summary>
        private bool _dumpAttempted;

        /// <summary>
        /// Runs the unrestricted people dump through the reflected ABI-shared
        /// ILibraryManager.GetPeople(InternalPeopleQuery), once per filter run.
        /// </summary>
        private IReadOnlyList<(string Name, string? Type)>? GetDumpRows(PrefilterContext context)
        {
            if (_dumpAttempted)
            {
                return _dumpRows;
            }

            _dumpAttempted = true;
            var getPeopleMethod = OperandFactory.GetPeopleQueryMethod(context.LibraryManager);
            if (getPeopleMethod == null)
            {
                return null;
            }

            var result = getPeopleMethod.Invoke(context.LibraryManager, [new InternalPeopleQuery()]);
            if (result is not IEnumerable<object> rows)
            {
                return null;
            }

            var dump = new List<(string Name, string? Type)>();
            PropertyInfo? nameProperty = null;
            PropertyInfo? typeProperty = null;
            foreach (var row in rows)
            {
                if (row == null)
                {
                    continue;
                }

                nameProperty ??= row.GetType().GetProperty("Name");
                typeProperty ??= row.GetType().GetProperty("Type");
                if (nameProperty?.GetValue(row) is not string name || name.Length == 0)
                {
                    continue;
                }

                dump.Add((name, typeProperty?.GetValue(row)?.ToString()));
            }

            _dumpRows = dump;
            return _dumpRows;
        }

        /// <summary>
        /// Derives the distinct names for a role (null = any role) from the dump rows.
        /// Ordinal distinctness is deliberate: names differing only by case are distinct
        /// stored rows and each needs its own byte-exact item query on 10.11.
        /// </summary>
        private static IReadOnlyList<string>? FilterNamesByRole(IReadOnlyList<(string Name, string? Type)>? rows, string? role)
        {
            if (rows == null)
            {
                return null;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, type) in rows)
            {
                if (role != null && !string.Equals(type, role, StringComparison.Ordinal))
                {
                    continue;
                }

                names.Add(name);
            }

            return [.. names];
        }
#endif
    }
}
