using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters
{
    /// <summary>
    /// Shared operator-to-predicate mapping for resolvers that evaluate a rule in memory
    /// against server-side string dumps (people names, stream language codes). Each dump
    /// value is tested as a single-element list through the same <see cref="Engine"/>
    /// helpers the compiled rules bind, so the per-item operator semantics are preserved
    /// exactly.
    /// </summary>
    internal static class PrefilterStringMatcher
    {
        /// <summary>
        /// The central MatchRegex pushdown gate: a pattern matching the empty string
        /// matches items with an empty extracted list (empty-list semantics test against
        /// ""), which no dump-derived candidate set can bound, and an invalid pattern is
        /// left to the per-item path to surface.
        /// </summary>
        /// <param name="pattern">The rule's regex pattern.</param>
        /// <returns>True when the pattern matches "" or does not compile.</returns>
        internal static bool RegexMatchesEmptyOrIsInvalid(string pattern)
        {
            try
            {
                return Regex.IsMatch(string.Empty, pattern);
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        /// <summary>
        /// Builds the predicate a rule operator applies to a single dump value, or null
        /// when the rule cannot ride a dump-based prefilter: negative operators and
        /// anything unexpected stay per-item, as does MatchRegex when
        /// <see cref="RegexMatchesEmptyOrIsInvalid"/> rejects the pattern.
        /// </summary>
        /// <param name="ruleOperator">The rule operator.</param>
        /// <param name="targetValue">The rule target value.</param>
        /// <returns>The per-value predicate, or null when the rule stays per-item.</returns>
        internal static Func<string, bool>? TryBuildMatcher(string ruleOperator, string targetValue)
        {
            switch (ruleOperator)
            {
                case "Equal":
                    return value => Engine.AnyItemEquals([value], targetValue);
                case "Contains":
                    return value => Engine.AnyItemContains([value], targetValue);
                case "IsIn":
                    return value => Engine.AnyItemIsInList([value], targetValue);
                case "MatchRegex":
                    if (RegexMatchesEmptyOrIsInvalid(targetValue))
                    {
                        return null;
                    }

                    return value => Engine.AnyRegexMatch([value], targetValue);
                default:
                    return null;
            }
        }
    }
}
