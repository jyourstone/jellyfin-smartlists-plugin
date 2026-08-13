using System.Diagnostics;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Covers the internal static matching helpers that <see cref="Engine"/> binds into the
/// compiled rule expressions. These are the primitives every list-field rule ultimately
/// runs on, so their exact semantics (substring vs equality, case handling, null handling,
/// exclusivity) are the contract worth pinning.
///
/// PRECONDITION for the Collections/Playlists tests: the prefix/suffix stripping path goes
/// through NameFormatter, which reads Plugin.Instance?.Configuration. Plugin is never
/// constructed in a test process, so Instance is null and NameFormatter falls back to its
/// hard-coded defaults: prefix "" and suffix "[Smart]". The tests below use "[Smart]"
/// deliberately for that reason - they are testing the stripping *behaviour*, not the
/// configured affix values.
/// </summary>
public class EngineInternalsTests
{
    // ---------------------------------------------------------------------------------
    // AnyItemContains - substring, case-insensitive
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("sci", true)]           // lowercase needle, mixed-case haystack
    [InlineData("SCI-FI", true)]        // uppercase needle
    [InlineData("Action", true)]        // whole element
    [InlineData("ction", true)]         // proper substring, not anchored to word start
    [InlineData("drama", false)]        // absent
    [InlineData("Action Movie", false)] // needle longer than any element
    public void AnyItemContains_MatchesSubstringOfAnyElement_IgnoringCase(string value, bool expected)
    {
        Assert.Equal(expected, Engine.AnyItemContains(["Action", "Sci-Fi"], value));
    }

    [Fact]
    public void AnyItemContains_SkipsNullElements_WithoutThrowing()
    {
        Assert.True(Engine.AnyItemContains([null!, "Action"], "act"));
        Assert.False(Engine.AnyItemContains([null!], "act"));
    }

    // ---------------------------------------------------------------------------------
    // AnyItemEquals - full-value equality, case-insensitive, NO affix stripping
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("Action", true)]
    [InlineData("action", true)]        // OrdinalIgnoreCase
    [InlineData("ACTION", true)]
    [InlineData("Act", false)]          // prefix of an element is not a match
    [InlineData("Action Movie", false)] // element is a prefix of the needle
    [InlineData("", false)]
    public void AnyItemEquals_RequiresWholeElementMatch_IgnoringCase(string value, bool expected)
    {
        Assert.Equal(expected, Engine.AnyItemEquals(["Action", "Sci-Fi"], value));
    }

    /// <summary>
    /// The single behavioural difference between AnyItemEquals and the Collection/Playlist
    /// variants: only the latter two also compare against the name with the configured
    /// prefix/suffix stripped off, so a user can write "Marvel" and still match the
    /// physically-named "Marvel [Smart]".
    /// </summary>
    [Fact]
    public void AnyCollectionAndPlaylistEquals_StripAffixesBeforeComparing_UnlikeAnyItemEquals()
    {
        Assert.False(Engine.AnyItemEquals(["Marvel [Smart]"], "Marvel"));

        Assert.True(Engine.AnyCollectionEquals(["Marvel [Smart]"], "Marvel"));
        Assert.True(Engine.AnyPlaylistEquals(["Marvel [Smart]"], "Marvel"));

        // The unstripped form still matches, and stripping does not make unrelated
        // names equal.
        Assert.True(Engine.AnyCollectionEquals(["Marvel [Smart]"], "marvel [smart]"));
        Assert.False(Engine.AnyCollectionEquals(["Marvel [Smart]"], "DC"));
    }

    // ---------------------------------------------------------------------------------
    // Only* family - "exclusively this value"
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The classic edge case: "the list contains only X" is FALSE for an empty list, not
    /// vacuously true. A list whose every entry is null or empty is treated as empty.
    /// </summary>
    [Fact]
    public void OnlyItemEquals_EmptyOrBlankOnlyList_ReturnsFalse()
    {
        Assert.False(Engine.OnlyItemEquals([], "Action"));
        Assert.False(Engine.OnlyItemEquals(["", null!], "Action"));
        Assert.False(Engine.OnlyItemEquals([], ""));
    }

    [Fact]
    public void OnlyItemEquals_IgnoresNullAndEmptyEntriesWhenCountingTheSoleValue()
    {
        Assert.True(Engine.OnlyItemEquals(["", null!, "Action"], "action"));
    }

    /// <summary>
    /// Exclusivity is decided on entry count, not on distinct values - two identical
    /// entries are still two entries and therefore not "only".
    /// </summary>
    [Fact]
    public void OnlyItemEquals_TwoEntries_ReturnsFalse_EvenWhenTheyAreDuplicates()
    {
        Assert.False(Engine.OnlyItemEquals(["Action", "Action"], "Action"));
        Assert.False(Engine.OnlyItemEquals(["Action", "Drama"], "Action"));
    }

    [Fact]
    public void OnlyCollectionAndPlaylistEquals_StripAffixes_UnlikeOnlyItemEquals()
    {
        Assert.False(Engine.OnlyItemEquals(["Marvel [Smart]"], "Marvel"));

        Assert.True(Engine.OnlyCollectionEquals(["Marvel [Smart]"], "Marvel"));
        Assert.True(Engine.OnlyPlaylistEquals(["Marvel [Smart]"], "Marvel"));

        // Exclusivity still wins over stripping.
        Assert.False(Engine.OnlyCollectionEquals(["Marvel [Smart]", "DC [Smart]"], "Marvel"));
    }

    // ---------------------------------------------------------------------------------
    // AnyRegexMatch
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Regexes are compiled with RegexOptions.None, so matching is case-SENSITIVE - the
    /// only helper in this family that is. The documented workaround is the inline (?i)
    /// flag, which the user guide tells people to use.
    /// </summary>
    [Theory]
    [InlineData("Action", true)]
    [InlineData("action", false)]
    [InlineData("(?i)action", true)]
    [InlineData("^Sci", true)]
    [InlineData("^ction", false)]
    [InlineData("^Action$", true)]
    [InlineData("^Sci$", false)]
    public void AnyRegexMatch_IsCaseSensitiveAndHonoursAnchors(string pattern, bool expected)
    {
        Assert.Equal(expected, Engine.AnyRegexMatch(["Action", "Sci-Fi"], pattern));
    }

    /// <summary>
    /// Special case with real user-facing meaning: an item with no tags/genres at all is
    /// matched by testing the pattern against the empty string, so "^$" selects items
    /// that have none. This only applies to a genuinely empty sequence.
    /// </summary>
    [Fact]
    public void AnyRegexMatch_EmptyList_MatchesThePatternAgainstTheEmptyString()
    {
        Assert.True(Engine.AnyRegexMatch([], "^$"));
        Assert.False(Engine.AnyRegexMatch([], "Action"));
    }

    /// <summary>
    /// A list of one null is NOT an empty list: the empty-string fallback does not kick
    /// in, and the null element is skipped, so "^$" finds nothing. A list containing an
    /// actual empty string does match "^$" - via the normal element path.
    /// </summary>
    [Fact]
    public void AnyRegexMatch_NullElements_AreSkippedAndDoNotTriggerTheEmptyListFallback()
    {
        Assert.False(Engine.AnyRegexMatch([null!], "^$"));
        Assert.True(Engine.AnyRegexMatch([""], "^$"));
        Assert.True(Engine.AnyRegexMatch([null!, "Action"], "^Action$"));
    }

    [Fact]
    public void AnyRegexMatch_InvalidPattern_ThrowsArgumentExceptionNamingThePattern()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Engine.AnyRegexMatch(["Action"], "[unterminated"));

        Assert.Contains("[unterminated", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression test. A pathological pattern must not be able to pin a refresh thread
    /// indefinitely. "^(a+)+$" against 40 non-matching characters is roughly 2^40 backtracking
    /// steps - hours of CPU - and Engine used to compile with InfiniteMatchTimeout.
    ///
    /// GetOrCreateRegex now passes Engine.RegexMatchTimeout, and RegexIsMatch surfaces the
    /// timeout as a descriptive ArgumentException. Failing here is deliberate: swallowing it
    /// would cost the full timeout on every item and block the serialized refresh queue, and
    /// suppressing the pattern after its first timeout would return silently wrong results.
    /// Do not skip or delete this.
    /// </summary>
    [Fact]
    public void AnyRegexMatch_CatastrophicBacktrackingPattern_ReturnsInsteadOfRunningUnbounded()
    {
        var input = new string('a', 40) + "!";

        var stopwatch = Stopwatch.StartNew();
        var ex = Assert.Throws<ArgumentException>(
            () => Engine.AnyRegexMatch([input], "^(a+)+$"));
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Regex evaluation was unbounded: still running after {stopwatch.Elapsed}.");

        // The message is the contract: it must name the pattern and say why it stopped,
        // because failing loudly is only useful if the user can tell what to fix.
        Assert.Contains("^(a+)+$", ex.Message, StringComparison.Ordinal);
        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A timeout must not be mislabelled as a syntax error - those are different problems with
    /// different fixes, and AnyRegexMatch catches them separately to keep them distinguishable.
    /// </summary>
    [Fact]
    public void AnyRegexMatch_Timeout_IsNotReportedAsAnInvalidPattern()
    {
        var input = new string('a', 40) + "!";

        var ex = Assert.Throws<ArgumentException>(
            () => Engine.AnyRegexMatch([input], "^(a+)+$"));

        Assert.DoesNotContain("Invalid regex pattern", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------
    // StringIsInList - the "IsIn" operator for single-valued string fields
    // ---------------------------------------------------------------------------------

    [Theory]
    // Semicolon-separated alternatives, any one of which may match.
    [InlineData("Action Movie", "Drama;Action", true)]
    [InlineData("Action Movie", "Drama;Comedy", false)]
    // Matching is case-insensitive and PARTIAL: a list entry need only be a substring of
    // the field value. Note the asymmetry - the field value being a substring of the
    // entry does not match.
    [InlineData("Action", "ACTION", true)]
    [InlineData("Action", "act", true)]
    [InlineData("Act", "Action", false)]
    // Whitespace around entries is trimmed.
    [InlineData("Action", "  Action  ", true)]
    [InlineData("Action", "Drama ; Action ; Comedy", true)]
    // Empty entries (leading, doubled, trailing delimiters) are dropped, not matched as
    // empty strings - otherwise every field value would match.
    [InlineData("Action", ";Action;", true)]
    [InlineData("Action", "Action;", true)]
    [InlineData("Action", ";;Drama;;Action;;", true)]
    [InlineData("Action", ";", false)]
    [InlineData("Action", ";;;", false)]
    // Entries that are only whitespace survive the split but are dropped after trimming.
    [InlineData("Action", " ; ; ", false)]
    [InlineData("Action", " ", false)]
    // Null/empty guards on both sides.
    [InlineData("", "Action", false)]
    [InlineData(null, "Action", false)]
    [InlineData("Action", "", false)]
    [InlineData("Action", null, false)]
    public void StringIsInList_SplitsOnSemicolonTrimsEntriesAndMatchesPartiallyIgnoringCase(
        string? fieldValue, string? targetList, bool expected)
    {
        Assert.Equal(expected, Engine.StringIsInList(fieldValue!, targetList!));
    }

    // ---------------------------------------------------------------------------------
    // AnyItemIsInList - the "IsIn" operator for list fields
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("Action|Sci-Fi", "Drama;Action", true)]
    [InlineData("Action|Sci-Fi", "Drama;Comedy", false)]
    // Same partial, case-insensitive matching as StringIsInList - here applied per element.
    [InlineData("Action|Sci-Fi", "sci", true)]
    [InlineData("Action|Sci-Fi", "ACTION", true)]
    [InlineData("Act|Sci", "Action", false)]
    // Same delimiter handling.
    [InlineData("Action|Sci-Fi", " Drama ; Action ", true)]
    [InlineData("Action|Sci-Fi", ";;Action;;", true)]
    [InlineData("Action|Sci-Fi", " ; ; ", false)]
    [InlineData("Action|Sci-Fi", ";", false)]
    [InlineData("Action|Sci-Fi", "", false)]
    [InlineData("Action|Sci-Fi", null, false)]
    public void AnyItemIsInList_MatchesAnyElementAgainstAnySemicolonSeparatedEntry(
        string pipeSeparatedCollection, string? targetList, bool expected)
    {
        var collection = pipeSeparatedCollection.Split('|');

        Assert.Equal(expected, Engine.AnyItemIsInList(collection, targetList!));
    }

    /// <summary>
    /// Unlike AnyRegexMatch, an empty collection has no special "match against nothing"
    /// fallback here - it simply matches nothing.
    /// </summary>
    [Fact]
    public void AnyItemIsInList_EmptyCollection_ReturnsFalse()
    {
        Assert.False(Engine.AnyItemIsInList([], "Action"));
        Assert.False(Engine.AnyItemIsInList([], ""));
    }

    [Fact]
    public void AnyItemIsInList_SkipsNullElements_WithoutThrowing()
    {
        Assert.True(Engine.AnyItemIsInList([null!, "Action"], "action"));
        Assert.False(Engine.AnyItemIsInList([null!], "Action"));
    }

    // ---------------------------------------------------------------------------------
    // Cross-cutting null handling
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Every list helper treats a null sequence as "no match" rather than throwing, which
    /// is what lets Factory hand over un-populated Operand properties safely. For
    /// AnyRegexMatch this also pins the ORDER of the guards: the null check runs before
    /// the pattern is compiled, so an invalid pattern is never even reached.
    /// </summary>
    [Fact]
    public void AllListHelpers_NullList_ReturnFalseWithoutThrowing()
    {
        Assert.False(Engine.AnyItemContains(null!, "Action"));
        Assert.False(Engine.AnyItemEquals(null!, "Action"));
        Assert.False(Engine.AnyCollectionEquals(null!, "Action"));
        Assert.False(Engine.AnyPlaylistEquals(null!, "Action"));
        Assert.False(Engine.OnlyItemEquals(null!, "Action"));
        Assert.False(Engine.OnlyCollectionEquals(null!, "Action"));
        Assert.False(Engine.OnlyPlaylistEquals(null!, "Action"));
        Assert.False(Engine.AnyItemIsInList(null!, "Action"));
        Assert.False(Engine.AnyRegexMatch(null!, "[unterminated"));
    }

    /// <summary>
    /// Characterisation, not endorsement: the two "any" helpers disagree about a null
    /// target value. AnyItemEquals delegates to string.Equals, which tolerates null;
    /// AnyItemContains delegates to string.Contains, which does not. Neither guards it,
    /// because in production TargetValue is validated non-empty before compilation.
    /// </summary>
    [Fact]
    public void AnyItemContainsAndAnyItemEquals_DisagreeOnANullTargetValue()
    {
        Assert.False(Engine.AnyItemEquals(["Action"], null!));
        Assert.Throws<ArgumentNullException>(() => Engine.AnyItemContains(["Action"], null!));
    }

    // ---------------------------------------------------------------------------------
    // FixRuleSets / FixRules
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Both are vestigial pass-throughs that return their argument by reference - they
    /// normalise nothing despite the name. Pinned so that a future change from "no-op" to
    /// "quietly rewrites the caller's rules" cannot land unnoticed.
    /// </summary>
    [Fact]
    public void FixRuleSetsAndFixRules_ReturnTheSameInstanceUnmodified()
    {
        var set = new ExpressionSet { Expressions = [new Expression("Genres", "Contains", "Action")] };
        var sets = new List<ExpressionSet> { set };

        Assert.Same(sets, Engine.FixRuleSets(sets));
        Assert.Same(set, Engine.FixRules(set));
    }
}
