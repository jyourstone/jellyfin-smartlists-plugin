using System;
using System.Collections.Generic;
using System.Diagnostics;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters
{
    /// <summary>
    /// Prefilter resolver for the Resolution rule field.
    ///
    /// The per-item path buckets EXCLUSIVELY on the max video-stream HEIGHT with
    /// upper-inclusive boundaries (OperandFactory.ExtractResolution: &lt;=480 -> 480p,
    /// &lt;=720 -> 720p, &lt;=1080 -> 1080p, &lt;=1440 -> 1440p, &lt;=2160 -> 4K, else 8K),
    /// and the compiled rule compares bucket heights only after a validity gate that
    /// requires a positive extracted height (Engine.BuildResolutionExpression). On the
    /// height axis a bucket's lower boundary translates to a MinHeight bound against the
    /// denormalized item-row Height, so one GetItemIds query drops everything below it
    /// without per-item stream extraction. Width bounds are deliberately NOT used: non-16:9
    /// content (4:3, portrait) breaks any width bound while the height axis stays exact.
    ///
    /// LOWER BOUNDS ONLY: Jellyfin 12 translates Min/MaxHeight version-aware - an item's
    /// height is the best its version group offers. MinHeight is met when the item OR any
    /// alternate version is tall enough, which only widens the candidate set (safe).
    /// MaxHeight is met only when NO version is too tall, so a 1080p primary with a 2160p
    /// alternate would be dropped by MaxHeight 1080 although the per-item path - which reads
    /// the primary's own streams - classifies it 1080p, breaking the superset contract. No
    /// upper bound is pushed down; the compiled rule enforces it per item. If the per-item
    /// path ever adopts the best-of-versions definition, upper bounds become safe again.
    ///
    /// Operator mapping (bucket boundaries, upper-inclusive):
    /// - Equal / GreaterThanOrEqual "X": previous bucket height + 1 (e.g. 1080p -> 721).
    /// - GreaterThan "X": X's height + 1.
    /// - LessThan / LessThanOrEqual stay per-item: they have no lower bound to push down.
    /// - NotEqual stays per-item (central negative-operator gate; the complement is not a
    ///   single range and a pool-minus-window complement would inherit the divergence edge
    ///   below in the unsafe direction).
    ///
    /// ACCEPTED DIVERGENCE EDGE (row-vs-streams): the SQL bound filters the denormalized
    /// row Height while per-item extraction reads the media streams, so an item whose row
    /// Height is NULL (unfilled dimensions) but whose streams are readable - or a
    /// multi-video-stream file whose non-default stream is taller than the default the row
    /// dims come from - can be dropped here yet classified per-item. Rare in practice (both
    /// require the row dims to disagree with the streams table) and accepted as a
    /// documented divergence per the spec.
    ///
    /// GATE: only applied when the list's media types are exclusively leaf video types
    /// (Movie/Episode/Video/MusicVideo). Folder kinds (Series/Season/BoxSet) carry no row
    /// Width/Height and the Min/Max filters do no descendant walk in either ABI, so a
    /// folder pool would be shrunk to nothing - those pools skip the prefilter entirely.
    /// Series items pulled into leaf pools by the Collections expansion, and extras, are
    /// kept by the central prefilter exemptions regardless of this resolver's output.
    ///
    /// Query notes: user-neutral with GroupByPresentationUniqueKey pinned off (a user
    /// collapses alternate versions and would drop pool items); visibility is restored by
    /// intersecting with the already user-scoped pool. IncludeItemTypes narrows to the
    /// pool's own leaf kinds - safe because every non-exempt pool item is of those kinds.
    /// </summary>
    internal sealed class ResolutionPrefilterResolver : IRulePrefilterResolver
    {
        /// <inheritdoc />
        public HashSet<Guid>? Resolve(Expression expression, PrefilterContext context)
        {
            if (!string.Equals(expression.MemberName, "Resolution", StringComparison.Ordinal)
                || context.LibraryManager == null
                || !AppliesToMediaTypes(context.MediaTypes))
            {
                return null;
            }

            var minHeight = TryBuildMinHeight(expression.Operator, expression.TargetValue);
            if (minHeight == null)
            {
                return null;
            }

            var kinds = MapToLeafVideoKinds(context.MediaTypes!);
            if (kinds == null)
            {
                return null;
            }

            var stopwatch = Stopwatch.StartNew();
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = kinds,
                MinHeight = minHeight,
                Recursive = true,
                GroupByPresentationUniqueKey = false,
            };

            var ids = context.LibraryManager.GetItemIds(query);

            context.Logger?.LogDebug(
                "Resolution prefilter: {Operator} '{Value}' -> MinHeight {Min} -> {Count} candidate items in {Ms}ms",
                expression.Operator, expression.TargetValue, minHeight, ids.Count, stopwatch.ElapsedMilliseconds);

            // Fresh set per call - the caller owns and mutates the result.
            return [.. ids];
        }

        /// <summary>
        /// Maps an operator/value combination to its bucket-boundary MinHeight, or null when
        /// the rule stays per-item (LessThan/LessThanOrEqual, NotEqual, an operator the
        /// whitelist rejects, or a value the compiled rule would reject). Value lookup mirrors
        /// the Engine exactly: ordinal match against <see cref="ResolutionTypes.AllResolutions"/>.
        /// </summary>
        /// <param name="ruleOperator">The rule operator.</param>
        /// <param name="targetValue">The rule target value (e.g. "1080p").</param>
        /// <returns>The inclusive minimum height, or null when the rule stays per-item.</returns>
        internal static int? TryBuildMinHeight(string? ruleOperator, string? targetValue)
        {
            var target = ResolutionTypes.GetByValue(targetValue ?? string.Empty);
            if (target == null)
            {
                return null;
            }

            switch (ruleOperator)
            {
                case "Equal":
                case "GreaterThanOrEqual":
                    return PreviousBucketHeight(target.Height) + 1;

                case "GreaterThan":
                    return target.Height + 1;

                default:
                    // LessThan/LessThanOrEqual have no lower bound (upper bounds are never
                    // pushed down, see the class doc); NotEqual and anything else is not a
                    // riding resolution operator. Operator comparison is Ordinal, mirroring
                    // the engine switch.
                    return null;
            }
        }

        /// <summary>
        /// Gates the prefilter on pools whose media types are exclusively leaf video types
        /// (Movie/Episode/Video/MusicVideo). Null or empty means unrestricted - skip.
        /// </summary>
        /// <param name="mediaTypes">The list's media types.</param>
        /// <returns>True when every media type is a leaf video type.</returns>
        internal static bool AppliesToMediaTypes(IReadOnlyList<string>? mediaTypes)
        {
            if (mediaTypes == null || mediaTypes.Count == 0)
            {
                return false;
            }

            foreach (var mediaType in mediaTypes)
            {
                if (mediaType == null || !MediaTypes.VideoStreamCapableSet.Contains(mediaType))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Maps the (already gate-checked) leaf video media types to their query kinds.
        /// Null when any media type has no kind mapping: a partially mapped list would
        /// narrow the query below the pool (false negatives), and an empty kinds array
        /// would apply no type filter at all.
        /// </summary>
        private static BaseItemKind[]? MapToLeafVideoKinds(IReadOnlyList<string> mediaTypes)
        {
            var kinds = new HashSet<BaseItemKind>();
            foreach (var mediaType in mediaTypes)
            {
                if (!MediaTypes.MediaTypeToBaseItemKind.TryGetValue(mediaType, out var kind))
                {
                    return null;
                }

                kinds.Add(kind);
            }

            return kinds.Count == 0 ? null : [.. kinds];
        }

        /// <summary>
        /// The largest bucket height strictly below the target, or 0 for the lowest bucket
        /// (480p then starts at height 1, matching the compiled rule's positive-height
        /// validity gate).
        /// </summary>
        private static int PreviousBucketHeight(int targetHeight)
        {
            var previous = 0;
            foreach (var info in ResolutionTypes.AllResolutions)
            {
                if (info.Height < targetHeight && info.Height > previous)
                {
                    previous = info.Height;
                }
            }

            return previous;
        }
    }
}
