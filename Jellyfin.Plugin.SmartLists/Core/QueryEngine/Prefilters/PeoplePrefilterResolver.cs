using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    ///    optimization: the item query's Person clause is byte-exact, so passing the
    ///    user's raw rule value would silently under-match.
    /// 2. One user-neutral GetItemIds(Person = exact stored name) query per matched name.
    ///    The union over matched names is a guaranteed superset of the items whose people
    ///    list satisfies the rule, because every name in an item's extracted people list
    ///    also exists as a people-table row reachable through the same dump.
    ///
    /// Names are dumped via GetPeopleNames, whose SQL is a plain ordinal Distinct over
    /// Name - unlike GetPeople, whose no-ItemId branch collapses to ONE arbitrary row per
    /// LOWERCASED name and would drop case-only duplicate spellings (each stored spelling
    /// needs its own byte-exact item query). Role-specific fields push the role into the
    /// people query itself (InternalPeopleQuery.PersonTypes is translated BEFORE the name
    /// projection) and into the item query (Person + PersonTypes compose). Never
    /// role-filter dump results in memory.
    ///
    /// ActorRoles never rides: role strings live on the people map row and are not
    /// filterable. Negative operators are rejected centrally by
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
                // Jellyfin 12 composes Person + PersonTypes into an indexed role-specific
                // lookup.
                if (role != null)
                {
                    query.PersonTypes = [role];
                }
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

            var matches = PrefilterStringMatcher.TryBuildMatcher(ruleOperator, targetValue);
            if (matches == null)
            {
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
            _namesByRole[cacheKey] = names;
            return names;
        }
    }
}
