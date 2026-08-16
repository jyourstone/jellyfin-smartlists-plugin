using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
#if NET10_0_OR_GREATER
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
#endif

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters
{
    /// <summary>
    /// Prefilter resolver for the AudioLanguages and SubtitleLanguages rule fields,
    /// Jellyfin 12 only.
    ///
    /// Two steps, preserving the per-item operator semantics exactly:
    /// 1. Dump the distinct STORED language codes for the stream kind via
    ///    ILibraryManager.GetMediaStreamLanguages (one query), then decide which of them the
    ///    rule matches by evaluating the operator in memory - against each code's
    ///    ISO 639-2 B-to-T NORMALIZED form, because that is what per-item extraction sees:
    ///    the server's stream read path (MediaStreamRepository.Map) rewrites stored B codes
    ///    to T codes through ILocalizationManager.TryGetISO6392TFromB, so a track stored as
    ///    'ger' (the Matroska norm) reaches the plugin as 'deu'. Matching the raw dump
    ///    instead would systematically drop B-code libraries (rule "Equal deu" would find no
    ///    raw code and claim nothing matches - a false negative on common real-world data).
    /// 2. One user-neutral GetItemIds query with the matched RAW codes in
    ///    InternalItemsQuery.AudioLanguages/SubtitleLanguages - RAW because the query's
    ///    EXISTS over MediaStreamInfos compares the stored Language column byte-exact, and
    ///    the raw dump codes come verbatim from that column. Total: 2 queries per rule.
    ///
    /// Superset proof: every language code per-item extraction can produce for any item is
    /// the normalized form of some stored code, and every stored code is in the dump. An
    /// item satisfying the rule therefore carries a stream whose raw code is in the matched
    /// set, and the EXISTS query returns it. The reverse direction over-matches only
    /// (harmless false positives): the server treats 'und' in the positive filter as also
    /// matching null/empty stored languages, which per-item extraction skips, and the
    /// filter is folder-aware (a Series/BoxSet with matching descendants can enter the
    /// candidate set) while per-item extraction of folders yields empty lists. Rules with
    /// OnlyDefaultAudioLanguage ride safely for the same reason: default-stream languages
    /// are a subset of all audio-stream languages, so the any-stream candidate set is a
    /// superset and default-ness stays verified per-item.
    ///
    /// Jellyfin 10.11 (net9.0) contributes no candidates: there is no positive language
    /// filter there, and the double-negation route over HasNo*TrackWithLanguage has
    /// verified false-negative traps (the server silently DROPS the filter when it cannot
    /// resolve the code - collapsing pool-minus-result to an empty candidate set - and its
    /// SQL IN over stored codes is case-sensitive where the plugin compare is not). Those
    /// rules simply stay per-item on 10.11.
    ///
    /// Negative operators are rejected centrally by <see cref="CandidateSetBuilder"/>
    /// (SupportsNegativeOperators stays false); MatchRegex patterns matching the empty
    /// string are rejected both centrally and here (empty stream lists test against "").
    /// </summary>
    internal sealed class StreamLanguagePrefilterResolver : IRulePrefilterResolver
    {
        /// <summary>
        /// Mirror of ILocalizationManager.TryGetISO6392TFromB, injectable so the pure
        /// matching step is testable against a fake B-to-T mapping.
        /// </summary>
        /// <param name="rawCode">The raw stored language code.</param>
        /// <param name="normalized">The ISO 639-2/T form when the code is a known B code.</param>
        /// <returns>True when the code was a B code with a T mapping.</returns>
        internal delegate bool TryNormalizeLanguage(string rawCode, out string? normalized);

        /// <inheritdoc />
        public HashSet<Guid>? Resolve(Expression expression, PrefilterContext context)
        {
#if NET10_0_OR_GREATER
            var isAudio = string.Equals(expression.MemberName, "AudioLanguages", StringComparison.Ordinal);
            if ((!isAudio && !string.Equals(expression.MemberName, "SubtitleLanguages", StringComparison.Ordinal))
                || context.LibraryManager == null)
            {
                return null;
            }

            // The server-populated BaseItem static is the same instance MediaStreamRepository
            // normalizes with on the read path. Without it the B-to-T step cannot run, and
            // matching raw codes instead would under-match - so the rule stays per-item.
            var localization = BaseItem.LocalizationManager;
            if (localization == null)
            {
                return null;
            }

            var streamType = isAudio ? MediaStreamType.Audio : MediaStreamType.Subtitle;
            var rawCodes = GetDump(context, streamType);
            if (rawCodes == null)
            {
                return null;
            }

            var matched = ResolveMatchingRawCodes(rawCodes, expression.Operator, expression.TargetValue,
                localization.TryGetISO6392TFromB);
            if (matched == null)
            {
                context.Logger?.LogDebug("Stream language prefilter: rule {Field} {Operator} not pushdownable ({CodeCount} stored codes)",
                    expression.MemberName, expression.Operator, rawCodes.Count);
                return null;
            }

            if (matched.Count == 0)
            {
                // No stored code's normalized form satisfies the rule, so no item can - and an
                // empty ItemIds-style list must never reach the query (it would fail OPEN).
                context.Logger?.LogDebug("Stream language prefilter: {Field} {Operator} '{Value}' matches no stored language code",
                    expression.MemberName, expression.Operator, expression.TargetValue);
                return [];
            }

            var stopwatch = Stopwatch.StartNew();

            // User-neutral with grouping pinned off: the constructor default
            // (GroupByPresentationUniqueKey = true) collapses alternate versions when a user
            // is set and would drop pool items. Visibility is restored later by intersecting
            // with the already user-scoped pool.
            var query = new InternalItemsQuery
            {
                GroupByPresentationUniqueKey = false,
            };
            if (isAudio)
            {
                query.AudioLanguages = matched;
            }
            else
            {
                query.SubtitleLanguages = matched;
            }

            var ids = context.LibraryManager.GetItemIds(query);

            context.Logger?.LogDebug("Stream language prefilter: {Field} {Operator} matched {CodeCount} raw codes -> {ItemCount} candidate items in {Ms}ms",
                expression.MemberName, expression.Operator, matched.Count, ids.Count, stopwatch.ElapsedMilliseconds);

            // Fresh set per call - the caller owns and mutates the result.
            return [.. ids];
#else
            // Jellyfin 10.11: no positive language filter; the double-negation route has
            // verified false-negative traps (see the class doc). Always per-item.
            return null;
#endif
        }

        /// <summary>
        /// Evaluates the rule operator against each raw dump code's B-to-T normalized form
        /// with the plugin's exact per-item semantics (each code is tested as a
        /// single-element list through the same Engine helpers the compiled rules bind),
        /// returning the RAW codes whose normalized form matches.
        /// </summary>
        /// <param name="rawCodes">Distinct raw stored codes from the stream-language dump.</param>
        /// <param name="ruleOperator">The rule operator.</param>
        /// <param name="targetValue">The rule target value.</param>
        /// <param name="tryNormalize">The B-to-T normalization the read path applies.</param>
        /// <returns>
        /// The matched raw codes, empty when no stored code satisfies the rule (a hard
        /// "nothing matches" claim), or null when the rule cannot ride the prefilter
        /// (unsupported operator, or an empty-matching or invalid regex).
        /// </returns>
        internal static List<string>? ResolveMatchingRawCodes(IReadOnlyCollection<string> rawCodes, string ruleOperator, string targetValue, TryNormalizeLanguage tryNormalize)
        {
            ArgumentNullException.ThrowIfNull(rawCodes);
            ArgumentNullException.ThrowIfNull(tryNormalize);

            Func<string, bool> matches;
            switch (ruleOperator)
            {
                case "Equal":
                    matches = code => Engine.AnyItemEquals([code], targetValue);
                    break;
                case "Contains":
                    matches = code => Engine.AnyItemContains([code], targetValue);
                    break;
                case "IsIn":
                    matches = code => Engine.AnyItemIsInList([code], targetValue);
                    break;
                case "MatchRegex":
                    try
                    {
                        // An empty stream-language list is evaluated against "", so a pattern
                        // that matches the empty string matches items with no such streams at
                        // all - which no code-derived candidate set can bound.
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

                    matches = code => Engine.AnyRegexMatch([code], targetValue);
                    break;
                default:
                    // Negative operators and anything unexpected stay per-item.
                    return null;
            }

            var matched = new List<string>();
            foreach (var rawCode in rawCodes)
            {
                if (string.IsNullOrEmpty(rawCode))
                {
                    // The dump maps null/empty stored languages to 'und', and per-item
                    // extraction drops them - an empty code can never satisfy a rule.
                    continue;
                }

                // Per-item extraction sees the T form of stored B codes (read-path Map);
                // unmapped codes pass through raw there and here alike.
                var normalized = tryNormalize(rawCode, out var isoT) && !string.IsNullOrEmpty(isoT) ? isoT : rawCode;
                if (matches(normalized))
                {
                    matched.Add(rawCode);
                }
            }

            return matched;
        }

#if NET10_0_OR_GREATER
        /// <summary>
        /// Per-stream-kind raw code dumps for this filter run (the resolver lives for one
        /// CandidateSetBuilder.Build call); null when the dump failed, so a failure is not
        /// retried per rule.
        /// </summary>
        private readonly Dictionary<MediaStreamType, IReadOnlyList<string>?> _dumpByType = new();

        /// <summary>
        /// Gets the distinct raw stored language codes for the stream kind, cached for the
        /// duration of this filter run. Returns null when the dump is unavailable.
        /// </summary>
        private IReadOnlyList<string>? GetDump(PrefilterContext context, MediaStreamType streamType)
        {
            if (_dumpByType.TryGetValue(streamType, out var cached))
            {
                return cached;
            }

            IReadOnlyList<string>? codes;
            try
            {
                codes = context.LibraryManager.GetMediaStreamLanguages(streamType);
            }
            catch (Exception ex)
            {
                context.Logger?.LogDebug(ex, "Stream language prefilter: {StreamType} language dump failed; rules stay per-item", streamType);
                codes = null;
            }

            _dumpByType[streamType] = codes;
            return codes;
        }
#endif
    }
}
