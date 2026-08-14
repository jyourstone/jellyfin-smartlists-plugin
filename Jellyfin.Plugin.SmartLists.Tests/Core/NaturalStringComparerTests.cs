using Jellyfin.Plugin.SmartLists.Core;

namespace Jellyfin.Plugin.SmartLists.Tests.Core;

/// <summary>
/// Covers <see cref="OrderUtilities.NaturalStringComparer"/>, which had no tests of its own until
/// issue #493 - the reason a real ordering bug survived every suite in the repo.
///
/// It backs more than one sort, so a defect here is broad and silent: the name-based orders via
/// NameSortHelper, RoundRobinBase.CompareWithinGroup's mixed-type fallback, round-robin GROUP
/// ordering, and the same-day crossover tie-break that compares series Sort Titles.
///
/// The bug it now guards: the comparer only parsed a number off the FRONT of a string, so any
/// embedded number compared as text and "Season 10" sorted before "Season 2" - exactly the order
/// a plain string sort gives, i.e. the natural comparer did nothing for the most common case.
/// Leading-digit names ("2 Fast" before "10 Cloverfield") were always correct, which is what kept
/// it hidden.
/// </summary>
public class NaturalStringComparerTests
{
    private static readonly OrderUtilities.NaturalStringComparer Comparer = OrderUtilities.SharedNaturalComparer;

    private static string[] Sorted(params string[] items) => items.OrderBy(x => x, Comparer).ToArray();

    // ------------------------------------------------------------------ the bug

    /// <summary>
    /// The regression test for issue #493, in the shape users actually reported it.
    /// </summary>
    [Theory]
    [InlineData("Season")]
    [InlineData("Part")]
    [InlineData("Volume")]
    [InlineData("Episode")]
    [InlineData("Chapter")]
    public void EmbeddedNumbers_CompareNumerically_NotAsText(string prefix)
    {
        var sorted = Sorted($"{prefix} 10", $"{prefix} 2", $"{prefix} 1");

        Assert.Equal([$"{prefix} 1", $"{prefix} 2", $"{prefix} 10"], sorted);
    }

    /// <summary>Leading-digit names always worked; they must keep working.</summary>
    [Fact]
    public void LeadingNumbers_StillCompareNumerically()
    {
        Assert.Equal(
            ["2 Fast 2 Furious", "10 Cloverfield Lane", "12 Angry Men"],
            Sorted("12 Angry Men", "10 Cloverfield Lane", "2 Fast 2 Furious"));
    }

    [Fact]
    public void PureNumericStrings_CompareNumerically()
    {
        Assert.Equal(["1", "2", "10", "100"], Sorted("100", "10", "2", "1"));
    }

    // ------------------------------------------------------- multiple number runs

    /// <summary>
    /// More than one number in a string: each run compares in turn, so the second number only
    /// decides once the first ties. A parser that only handled the leading number would order
    /// S1E10 before S1E2, and one that only handled the last would order S2E1 before S1E2.
    /// </summary>
    [Fact]
    public void MultipleNumberRuns_CompareLeftToRight()
    {
        Assert.Equal(
            ["Show S1E2", "Show S1E10", "Show S2E1", "Show S10E1"],
            Sorted("Show S10E1", "Show S1E10", "Show S2E1", "Show S1E2"));
    }

    [Fact]
    public void TextBeforeTheNumber_DecidesFirst()
    {
        Assert.Equal(
            ["Alpha 10", "Beta 2"],
            Sorted("Beta 2", "Alpha 10"));
    }

    // -------------------------------------------------------------- number vs text

    /// <summary>
    /// Numbers sort ahead of letters, which the previous implementation did explicitly
    /// ("put numbered items first") and this one gets from digits being ordinally below letters.
    /// Asserted so the behaviour survives, however it is implemented.
    /// </summary>
    [Fact]
    public void NumberedItems_SortBeforeUnnumberedOnes()
    {
        Assert.Equal(["2 Fast", "Alpha"], Sorted("Alpha", "2 Fast"));
        Assert.True(Comparer.Compare("2 Fast", "Alpha") < 0);
        Assert.True(Comparer.Compare("Alpha", "2 Fast") > 0);
    }

    /// <summary>A prefix sorts before the longer string that extends it.</summary>
    [Fact]
    public void ShorterStringSortsFirst_WhenItIsAPrefix()
    {
        Assert.Equal(["Rocky", "Rocky 2", "Rocky 10"], Sorted("Rocky 10", "Rocky 2", "Rocky"));
    }

    // --------------------------------------------------------------- zero padding

    /// <summary>
    /// Zero-padded and bare numbers are the same number, so they must not be separated by the
    /// padding - "Season 02" belongs next to "Season 2", not off in its own group.
    /// </summary>
    [Fact]
    public void ZeroPadding_DoesNotChangeNumericValue()
    {
        Assert.Equal(["Season 02", "Season 10"], Sorted("Season 10", "Season 02"));
        Assert.Equal(["Season 007", "Season 8"], Sorted("Season 8", "Season 007"));
    }

    /// <summary>
    /// Equal numbers differing only in padding still order deterministically rather than comparing
    /// equal - two distinct strings returning 0 makes the surrounding sort order arbitrary.
    /// </summary>
    [Fact]
    public void ZeroPadding_BreaksAnOtherwiseExactTie_RatherThanComparingEqual()
    {
        Assert.NotEqual(0, Comparer.Compare("Season 02", "Season 2"));
        Assert.Equal(
            -Comparer.Compare("Season 2", "Season 02"),
            Comparer.Compare("Season 02", "Season 2"));
    }

    // ------------------------------------------------------------------ huge runs

    /// <summary>
    /// Digit runs are compared without being parsed into an int. The old implementation called
    /// int.TryParse and, on overflow, silently reported "no leading number" and fell back to a
    /// text comparison - so absurdly long runs compared wrongly instead of just slowly.
    /// </summary>
    [Fact]
    public void DigitRunsTooLargeForAnInt_StillCompareNumerically()
    {
        var big = "Item 99999999999999999999";
        var bigger = "Item 999999999999999999999";

        Assert.True(Comparer.Compare(big, bigger) < 0);
        Assert.True(Comparer.Compare(bigger, big) > 0);
        Assert.Equal([big, bigger], Sorted(bigger, big));
    }

    // -------------------------------------------------------- non-ASCII numerals

    /// <summary>
    /// Digits are compared by NUMERIC VALUE, not by code unit, so non-ASCII decimal digits sort
    /// as the numbers they are. `char.IsDigit` accepts every Unicode decimal digit — Arabic-Indic
    /// ٠-٩, Devanagari ०-९ and the rest — so comparing code units here would order "٢" (2) after
    /// "10" and would not recognise "٠" as a leading zero. Flagged independently by two reviewers
    /// on #494.
    /// </summary>
    [Fact]
    public void NonAsciiDigits_CompareByNumericValue_NotByCodeUnit()
    {
        const string ar2 = "٢", ar10 = "١٠", ar02 = "٠٢";

        // 2 < 10 in Arabic-Indic digits, exactly as in ASCII.
        Assert.True(Comparer.Compare("Track " + ar2, "Track " + ar10) < 0);

        // U+0660 is a zero, so "٠٢" is the number 2 and sorts before 10 - the case CodeRabbit named.
        Assert.True(Comparer.Compare("Track " + ar02, "Track 10") < 0);

        Assert.Equal(
            ["Track " + ar2, "Track " + ar10],
            Sorted("Track " + ar10, "Track " + ar2));
    }

    /// <summary>
    /// Leading zeros are stripped by digit VALUE, so a non-ASCII zero counts as padding just like
    /// '0'. The lengths here are what makes this detectable: "٠٠٥" is 3 characters but the number
    /// 5, and "١٢" is 2 characters and the number 12. Strip by value and 5 sorts below 12; strip
    /// by literal '0' and the run stays 3 long, so the more-digits-means-bigger shortcut declares
    /// 5 the larger number.
    ///
    /// A shorter example does not catch it — "٠٢" vs "10" compares equal-length either way and
    /// the per-digit comparison rescues the result, which is exactly how the first version of this
    /// test passed against a broken implementation.
    /// </summary>
    [Fact]
    public void NonAsciiLeadingZeros_AreStrippedByValue_SoRunLengthReflectsTheNumber()
    {
        Assert.True(Comparer.Compare("٠٠٥", "١٢") < 0);   // 5 < 12
        Assert.True(Comparer.Compare("١٢", "٠٠٥") > 0);
    }

    /// <summary>
    /// KNOWN LIMIT, pinned deliberately. Decimal digits outside the BMP — mathematical
    /// alphanumerics like 𝟐, Osage, Chakma — are encoded as surrogate pairs, and
    /// <c>char.IsDigit</c> inspects only the high surrogate, so they never enter the numeric
    /// branch and compare as text instead.
    ///
    /// Not fixed on purpose (raised on #494 by both reviewers): recognising them means rebuilding
    /// the whole comparison loop — character comparison and case folding included — around
    /// <c>Rune</c>, in a comparer that runs for every name sort, in exchange for titles no
    /// metadata provider emits. This test exists so the limit is a recorded decision rather than
    /// an unnoticed gap, and so it fails loudly if someone later makes the loop scalar-aware and
    /// forgets this file.
    /// </summary>
    [Fact]
    public void SupplementaryPlaneDigits_AreNotRecognised_AndCompareAsText()
    {
        var mathTwo = char.ConvertFromUtf32(0x1D7D0 + 2);   // MATHEMATICAL BOLD DIGIT TWO

        // A BMP digit in the same position WOULD sort numerically...
        Assert.True(Comparer.Compare("Track ٢", "Track 10") < 0);

        // ...but the supplementary-plane one falls through to text ordering, so it lands after.
        Assert.True(Comparer.Compare("Track " + mathTwo, "Track 10") > 0);
    }

    /// <summary>Devanagari, to prove the rule is per-Unicode-digit and not an Arabic special case.</summary>
    [Fact]
    public void DevanagariDigits_AlsoCompareNumerically()
    {
        Assert.True(Comparer.Compare("भाग २", "भाग १०") < 0); // Part 2 before Part 10
    }

    /// <summary>
    /// Cross-script: a digit's value is what counts, so Arabic-Indic 1 sorts below ASCII 2 even
    /// though its code point (U+0661) is far above '2' (U+0032).
    /// </summary>
    [Fact]
    public void DigitsFromDifferentScripts_CompareByValue()
    {
        Assert.True(Comparer.Compare("١", "2") < 0);
    }

    /// <summary>
    /// The ASCII-only rule must not weaken ASCII handling in a string that also carries non-ASCII
    /// characters — a title is not disqualified from natural sorting by containing non-Latin text.
    /// </summary>
    [Fact]
    public void AsciiDigits_StillSortNumerically_InStringsContainingNonAsciiText()
    {
        Assert.Equal(
            ["مسلسل 2", "مسلسل 10"],
            Sorted("مسلسل 10", "مسلسل 2"));
    }

    // ------------------------------------------------------------ case and nulls

    [Fact]
    public void ComparisonIsCaseInsensitive_ForTheSharedInstance()
    {
        Assert.Equal(0, Comparer.Compare("season 2", "SEASON 2"));
    }

    [Fact]
    public void CaseSensitiveInstance_DistinguishesCase_ButStillComparesNumbersNumerically()
    {
        var caseSensitive = new OrderUtilities.NaturalStringComparer(ignoreCase: false);

        Assert.NotEqual(0, caseSensitive.Compare("season 2", "SEASON 2"));
        Assert.True(caseSensitive.Compare("Season 2", "Season 10") < 0);
    }

    [Fact]
    public void Nulls_SortBeforeEverything_AndTwoNullsAreEqual()
    {
        Assert.True(Comparer.Compare(null, "anything") < 0);
        Assert.True(Comparer.Compare("anything", null) > 0);
        Assert.Equal(0, Comparer.Compare(null, null));
    }

    [Fact]
    public void EmptyString_SortsBeforeAnyContent_AndEqualsItself()
    {
        Assert.True(Comparer.Compare("", "a") < 0);
        Assert.Equal(0, Comparer.Compare("", ""));
    }

    // ------------------------------------------------------- total-order sanity

    /// <summary>
    /// A comparer that is not its own mirror image breaks List.Sort in ways that surface as items
    /// randomly missing their place. Checked across every branch: digit-vs-digit, digit-vs-text,
    /// prefixes, padding, and nulls.
    /// </summary>
    [Fact]
    public void ComparisonIsAntisymmetricAndReflexive_AcrossEveryBranch()
    {
        string?[] sample =
        [
            null, "", "Season 2", "Season 10", "Season 02", "season 2",
            "2 Fast", "10 Cloverfield", "Alpha", "Rocky", "Rocky 2", "Show S1E2", "Show S1E10",
        ];

        foreach (var a in sample)
        {
            Assert.Equal(0, Comparer.Compare(a, a));

            foreach (var b in sample)
            {
                Assert.Equal(Math.Sign(Comparer.Compare(a, b)), -Math.Sign(Comparer.Compare(b, a)));
            }
        }
    }

    /// <summary>
    /// Sorting the same set from two different starting arrangements must give the same result,
    /// which only holds if every pair resolves consistently regardless of the pivots List.Sort
    /// happens to pick.
    /// </summary>
    [Fact]
    public void SortingIsIndependentOfTheStartingArrangement()
    {
        string[] items = ["Season 10", "Season 2", "Rocky", "2 Fast", "Alpha", "Rocky 2", "Season 1"];

        var forward = Sorted(items);
        var reversed = Sorted([.. items.Reverse()]);

        Assert.Equal(forward, reversed);
        Assert.Equal(["2 Fast", "Alpha", "Rocky", "Rocky 2", "Season 1", "Season 2", "Season 10"], forward);
    }
}
