using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine
{
    /// <summary>
    /// Ancestor-inherited Tags/Studios/Genres for one node of the item tree.
    /// IMMUTABLE: instances are shared across operands via the per-refresh memo and are
    /// assigned to Operand properties BY REFERENCE. Members are IReadOnlyList so that
    /// mutation is a compile error rather than a comment-enforced convention — Empty is a
    /// process-lifetime singleton and a stray mutation would corrupt every list, for every
    /// user, for the rest of the process.
    /// </summary>
    public sealed class AncestorValues
    {
        /// <summary>
        /// Shared, process-lifetime "nothing inherited" instance. The three members are empty
        /// but NEVER null: AnyRegexMatch's empty-list branch tests the pattern against
        /// string.Empty, whereas a null list returns false, so null and empty are observably
        /// different for MatchRegex.
        /// </summary>
        public static readonly AncestorValues Empty = new([], [], []);

        public IReadOnlyList<string> Tags { get; }
        public IReadOnlyList<string> Studios { get; }
        public IReadOnlyList<string> Genres { get; }

        private AncestorValues(IReadOnlyList<string> tags, IReadOnlyList<string> studios, IReadOnlyList<string> genres)
        {
            Tags = tags;
            Studios = studios;
            Genres = genres;
        }

        /// <summary>
        /// Returns a NEW instance holding this instance's values plus the node's own
        /// Tags/Studios/Genres. Returns <c>this</c> unchanged when the node contributes
        /// nothing, so a chain of value-less folders allocates nothing.
        /// </summary>
        /// <param name="node">The ancestor node whose own values are folded in.</param>
        /// <returns>The combined values.</returns>
        public AncestorValues Union(BaseItem node)
        {
            if (node is null)
            {
                return this;
            }

            // BaseItem is nullable-OBLIVIOUS in both ABIs (no NullableContextAttribute), so these
            // three are declared string[] yet can be null at runtime and nothing warns under
            // Nullable=enable. Guard each one individually.
            var nodeTags = node.Tags;
            var nodeStudios = node.Studios;
            var nodeGenres = node.Genres;

            var hasTags = nodeTags is { Length: > 0 };
            var hasStudios = nodeStudios is { Length: > 0 };
            var hasGenres = nodeGenres is { Length: > 0 };

            if (!hasTags && !hasStudios && !hasGenres)
            {
                return this;
            }

            return new AncestorValues(
                hasTags ? Combine(Tags, nodeTags) : Tags,
                hasStudios ? Combine(Studios, nodeStudios) : Studios,
                hasGenres ? Combine(Genres, nodeGenres) : Genres);
        }

        private static IReadOnlyList<string> Combine(IReadOnlyList<string> existing, string[] additions)
        {
            var combined = new List<string>(existing.Count + additions.Length);

            // Ordinal, deliberately NOT OrdinalIgnoreCase (which is what core's GetInheritedTags uses).
            // Six of the seven operators are OrdinalIgnoreCase so dedup strength is invisible to them,
            // but MatchRegex is case-SENSITIVE (GetOrCreateRegex passes RegexOptions.None) — case-insensitive
            // dedup could discard the only casing a pattern would have matched.
            var seen = new HashSet<string>(existing.Count + additions.Length, StringComparer.Ordinal);

            foreach (var value in existing)
            {
                if (value is not null && seen.Add(value))
                {
                    combined.Add(value);
                }
            }

            foreach (var value in additions)
            {
                if (value is not null && seen.Add(value))
                {
                    combined.Add(value);
                }
            }

            return combined;
        }
    }

    internal static class AncestorValueResolver
    {
        // Runaway/cycle insurance ONLY; it must never bind. Measured real depth in the dev
        // library is 4 (episode->season->series->folder), 2 (movie), 3 (audio);
        // /music/Artist/Album/Disc/track reaches 5 and deep genre trees maybe 7.
        // Do NOT "tighten" this to 10 — a binding cap produces truncated results.
        private const int MaxAncestorDepth = 20;

        /// <summary>
        /// Ancestor-inherited values for <paramref name="item"/>, EXCLUDING the item's own
        /// values (Engine unions the item's own field separately).
        ///
        /// The walk is: parent chain (stopping BEFORE AggregateFolder/UserRootFolder/UserView)
        /// UNION libraryManager.GetCollectionFolders(chainTop). The second half is NOT optional:
        /// a CollectionFolder (a Jellyfin library) is never in the ParentId chain — it hangs off
        /// the UserRootFolder as a sibling structure — so a parents-only walk finds season tags
        /// but never library tags. Core's BaseItem.GetInheritedTags()/GetAncestorIds() have the
        /// same two-part shape.
        ///
        /// COST: the memo is keyed on the IMMEDIATE PARENT id and is consulted BEFORE any parent
        /// is materialized, so a hit costs one dictionary lookup and ZERO ILibraryManager calls —
        /// parity with the code this replaces. A miss costs O(distinct containers) GetItemById
        /// plus one GetCollectionFolders path-scan per top physical folder.
        /// NOTE: when a list's ONLY rule is parent-aware, SmartList takes the expensive-only path
        /// (SmartList.cs:3026) and this runs once per CANDIDATE item, not per Phase-2 survivor.
        /// </summary>
        /// <param name="item">The item whose ancestors are walked.</param>
        /// <param name="libraryManager">Library manager used for the library (CollectionFolder) union.</param>
        /// <param name="memo">Per-refresh memo keyed by ANCESTOR NODE id.</param>
        /// <param name="logger">Optional logger.</param>
        /// <returns>The inherited values; never null.</returns>
        internal static AncestorValues Resolve(
            BaseItem item,
            ILibraryManager libraryManager,
            ConcurrentDictionary<Guid, AncestorValues> memo,
            ILogger? logger)
        {
            // MEMO-FIRST on the raw ParentId Guid — do NOT call GetParent() before this line.
            // GetParent() is `ParentId.IsEmpty() ? null : LibraryManager.GetItemById(ParentId)`,
            // so materializing the parent first would cost a library round-trip per ITEM.
            var parentId = item.ParentId;
            if (!parentId.IsEmpty() && memo.TryGetValue(parentId, out var cached))
            {
                return cached;
            }

            var node = item.GetParent() ?? item.GetOwner();   // GetOwner() covers extras (empty ParentId, set OwnerId)
            if (node is null || IsWalkBoundary(node))
            {
                return AncestorValues.Empty;
            }

            var chain = new List<BaseItem>();
            var visited = new HashSet<Guid>();
            AncestorValues? seed = null;
            var truncated = false;

            while (node is not null && !IsWalkBoundary(node))
            {
                if (memo.TryGetValue(node.Id, out var hit)) { seed = hit; break; }
                if (!visited.Add(node.Id) || chain.Count >= MaxAncestorDepth)
                {
                    logger?.LogWarning(
                        "SmartLists ancestor walk stopped early at '{Name}' ({Id}) - cycle or depth cap",
                        node.Name, node.Id);
                    truncated = true;
                    break;
                }
                chain.Add(node);
                node = node.GetParent() ?? node.GetOwner();
            }

            if (seed is null)
            {
                // Library values are resolved from the deepest node reached. GetCollectionFolders
                // walks independently of our chain, so it is valid (and required) even when the
                // chain was truncated — dropping it on truncation would silently reproduce #495.
                seed = GetLibraryValues(chain[^1], libraryManager, logger);
            }

            var acc = seed;
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                acc = acc.Union(chain[i]);

                // NEVER memoize a truncated (partial) result. A truncated walk is missing the TOP
                // of the chain, so caching it would make later walks return a value that depends on
                // WHICH ITEM WARMED THE CACHE FIRST (an episode 4 levels down vs a series 1 level down).
                if (!truncated) { memo[chain[i].Id] = acc; }
            }

            return acc;
        }

        private static bool IsWalkBoundary(BaseItem node)
            => node is AggregateFolder || node is UserRootFolder || node is UserView;

        private static AncestorValues GetLibraryValues(BaseItem anchor, ILibraryManager libraryManager, ILogger? logger)
        {
            try
            {
                var acc = AncestorValues.Empty;
                foreach (var folder in libraryManager.GetCollectionFolders(anchor)) { acc = acc.Union(folder); }
                return acc;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "SmartLists failed to resolve library values for '{Name}'", anchor.Name);
                return AncestorValues.Empty;
            }
        }
    }
}
