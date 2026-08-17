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
    /// height axis those buckets translate to exact MinHeight/MaxHeight windows against
    /// the denormalized item-row Height - both ABIs translate Min/MaxHeight to indexed
    /// SQL comparisons - so one GetItemIds range query replaces per-item stream
    /// extraction over the whole pool. Width windows are deliberately NOT used: non-16:9
    /// content (4:3, portrait) breaks any width window while the height axis stays exact.
    ///
    /// Operator mapping (bucket boundaries, upper-inclusive):
    /// - Equal "X": (previous bucket height + 1) .. X's height (e.g. 1080p -> 721..1080).
    /// - GreaterThan "X": X's height + 1, no upper bound.
    /// - GreaterThanOrEqual "X": previous bucket height + 1, no upper bound.
    /// - LessThan "X": no lower bound, up to the previous bucket height.
    /// - LessThanOrEqual "X": no lower bound, up to X's height.
    /// - Equal/GreaterThanOrEqual "8K" get NO MaxHeight: the bucketer maps every height
    ///   above 2160 (including &gt; 4320) to "8K", so the top bucket is open-ended.
    /// - LessThanOrEqual "8K" stays per-item for the same reason: it matches EVERY item
    ///   with a valid resolution (all buckets compare &lt;= the top bucket), so no height
    ///   window can bound it without dropping &gt;4320-height items that match per-item.
    /// - NotEqual stays per-item (central negative-operator gate; the complement is not a
    ///   single range and a pool-minus-window complement would inherit the divergence edge
    ///   below in the unsafe direction).
    ///
    /// ACCEPTED DIVERGENCE EDGE (row-vs-streams, both operator directions): the SQL window
    /// filters the denormalized row Width/Height while per-item extraction reads the media
    /// streams, so an item whose row Height is NULL (unfilled dimensions) but whose streams
    /// are readable - or a multi-video-stream file whose non-default stream is taller than
    /// the default the row dims come from - can be dropped here yet classified per-item.
    /// Rare in practice (both require the row dims to disagree with the streams table) and
    /// accepted as a documented divergence per the spec.
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

            var window = TryBuildHeightWindow(expression.Operator, expression.TargetValue);
            if (window == null)
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
                MinHeight = window.MinHeight,
                MaxHeight = window.MaxHeight,
                Recursive = true,
                GroupByPresentationUniqueKey = false,
            };

            var ids = context.LibraryManager.GetItemIds(query);

            context.Logger?.LogDebug(
                "Resolution prefilter: {Operator} '{Value}' -> height window [{Min}..{Max}] -> {Count} candidate items in {Ms}ms",
                expression.Operator, expression.TargetValue, window.MinHeight, window.MaxHeight, ids.Count, stopwatch.ElapsedMilliseconds);

            // Fresh set per call - the caller owns and mutates the result.
            return [.. ids];
        }

        /// <summary>
        /// Immutable height window an operator/value combination maps to. A null bound is
        /// left unset on the query (no constraint in that direction).
        /// </summary>
        /// <param name="MinHeight">Inclusive lower bound, or null for none.</param>
        /// <param name="MaxHeight">Inclusive upper bound, or null for none.</param>
        internal sealed record HeightWindow(int? MinHeight, int? MaxHeight);

        /// <summary>
        /// Maps an operator/value combination to its exact bucket-boundary height window,
        /// or null when the rule stays per-item (NotEqual, LessThanOrEqual on the top
        /// bucket, an operator the whitelist rejects, or a value the compiled rule would
        /// reject). Value lookup mirrors the Engine exactly: ordinal match against
        /// <see cref="ResolutionTypes.AllResolutions"/>.
        /// </summary>
        /// <param name="ruleOperator">The rule operator.</param>
        /// <param name="targetValue">The rule target value (e.g. "1080p").</param>
        /// <returns>The height window, or null when the rule stays per-item.</returns>
        internal static HeightWindow? TryBuildHeightWindow(string? ruleOperator, string? targetValue)
        {
            var target = ResolutionTypes.GetByValue(targetValue ?? string.Empty);
            if (target == null)
            {
                return null;
            }

            var previousBucketHeight = PreviousBucketHeight(target.Height);
            var isTopBucket = target.Height == TopBucketHeight();

            switch (ruleOperator)
            {
                case "Equal":
                    return new HeightWindow(previousBucketHeight + 1, isTopBucket ? null : target.Height);

                case "GreaterThan":
                    return new HeightWindow(target.Height + 1, null);

                case "GreaterThanOrEqual":
                    return new HeightWindow(previousBucketHeight + 1, null);

                case "LessThan":
                    return new HeightWindow(null, previousBucketHeight);

                case "LessThanOrEqual":
                    // The top bucket is open-ended above; see the class doc.
                    return isTopBucket ? null : new HeightWindow(null, target.Height);

                default:
                    // NotEqual and anything else is not a riding resolution operator.
                    // Operator comparison is Ordinal, mirroring the engine switch.
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
        /// (480p windows then start at height 1, matching the compiled rule's positive-
        /// height validity gate).
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

        /// <summary>
        /// The top bucket's height (the bucketer maps everything above the second-highest
        /// boundary into this bucket, so it is open-ended above).
        /// </summary>
        private static int TopBucketHeight()
        {
            var top = 0;
            foreach (var info in ResolutionTypes.AllResolutions)
            {
                if (info.Height > top)
                {
                    top = info.Height;
                }
            }

            return top;
        }
    }
}
