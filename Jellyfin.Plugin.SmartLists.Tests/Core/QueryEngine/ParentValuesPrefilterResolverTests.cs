using Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Covers the pure decision logic of <see cref="ParentValuesPrefilterResolver"/> - the
/// per-group operator gating and the normalized name matching that decide what (if
/// anything) a parent-aware Tags/Genres/Studios rule pushes into the candidate queries.
/// Its contract:
///
/// - Tags push Equal ONLY (raw value; the server-side Tags filter is a cleaned exact
///   match, and no tag-name dump API exists to expand substring operators against).
/// - Genre/studio dump matching is deliberately BROADER than plugin per-item semantics:
///   the dumps hold one representative raw variant per server-cleaned group, so matching
///   runs on MatchNormalize (diacritics folded, case folded, all non-alphanumerics
///   dropped) - coarser than both ABIs' cleans, which is exactly what makes one
///   representative safely stand in for its whole group. Broader matching only ever adds
///   candidates.
/// - MatchRegex never rides these fields (plugin regex is case-sensitive on raw variants;
///   no lone representative is a sound superset), nor do negative operators.
/// - Studio resolution additionally requires every rule-matching ItemValues name to have
///   a materialized by-name Studio item under the same CleanValue key (the server's
///   RemoveDiacritics + lowercase - the FINEST clean either ABI applies, so key equality
///   implies StudioIds-join reachability on both). A matching name without one means the
///   per-item path could match items no StudioIds query can reach - fall back.
///
/// The two-query ancestor expansion and the CollectionFolder guard need a live Jellyfin
/// and are exercised there, not here.
/// </summary>
public class ParentValuesPrefilterResolverTests
{
    // ---- PrefilterValueCleaner ----

    [Theory]
    [InlineData("Pathé", "pathe")]
    [InlineData("WARNER", "warner")]
    [InlineData("Sci-Fi", "sci-fi")]
    public void CleanValue_FoldsDiacriticsAndCase_KeepsPunctuation(string input, string expected)
    {
        Assert.Equal(expected, PrefilterValueCleaner.CleanValue(input));
    }

    [Theory]
    [InlineData("Sci-Fi ", "scifi")]
    [InlineData("Warner Bros.", "warnerbros")]
    [InlineData("Pathé!", "pathe")]
    [InlineData("Blade Runner 2049", "bladerunner2049")]
    [InlineData("!!!", "")]
    public void MatchNormalize_DropsEveryNonAlphanumeric(string input, string expected)
    {
        Assert.Equal(expected, PrefilterValueCleaner.MatchNormalize(input));
    }

    // ---- Tags gating ----

    [Fact]
    public void Tags_EqualRides_WithRawValue()
    {
        var push = ParentValuesPrefilterResolver.ResolveTagPushdownValues("Equal", "Halloween");

        Assert.NotNull(push);
        Assert.Equal(["Halloween"], push);
    }

    [Theory]
    [InlineData("Contains")]
    [InlineData("IsIn")]
    [InlineData("MatchRegex")]
    [InlineData("NotEqual")]
    [InlineData("NotContains")]
    [InlineData("IsNotIn")]
    public void Tags_OnlyEqualRides(string ruleOperator)
    {
        Assert.Null(ParentValuesPrefilterResolver.ResolveTagPushdownValues(ruleOperator, "Halloween"));
    }

    [Fact]
    public void Tags_WhitespaceValueStaysPerItem()
    {
        Assert.Null(ParentValuesPrefilterResolver.ResolveTagPushdownValues("Equal", "   "));
    }

    // ---- Dump name matching (Genres Contains/IsIn, Studios all positive operators) ----

    [Fact]
    public void Equal_MatchesDiacriticVariantRepresentative()
    {
        // Plugin-side an item can carry "Pathé" while the dump representative is "Pathe"
        // (one row per server-cleaned group) - plugin-exact OrdinalIgnoreCase matching
        // would miss the group, normalized matching must not.
        var matched = ParentValuesPrefilterResolver.ResolveMatchingNames(["Pathe", "Warner"], "Equal", "Pathé");

        Assert.NotNull(matched);
        Assert.Equal(["Pathe"], matched);
    }

    [Fact]
    public void Equal_MatchesPunctuationVariantRepresentative()
    {
        // Jellyfin 12's clean also collapses punctuation, so "Sci-Fi" and "Sci Fi" share
        // one group there and either spelling can be the dumped representative.
        var matched = ParentValuesPrefilterResolver.ResolveMatchingNames(["Sci Fi"], "Equal", "Sci-Fi");

        Assert.NotNull(matched);
        Assert.Equal(["Sci Fi"], matched);
    }

    [Fact]
    public void Equal_DoesNotSubstringMatch()
    {
        var matched = ParentValuesPrefilterResolver.ResolveMatchingNames(["Action", "Action-Packed"], "Equal", "Action");

        Assert.NotNull(matched);
        Assert.Equal(["Action"], matched);
    }

    [Fact]
    public void Equal_NoMatchReturnsEmpty_NotNull()
    {
        var matched = ParentValuesPrefilterResolver.ResolveMatchingNames(["Action"], "Equal", "Comedy");

        Assert.NotNull(matched);
        Assert.Empty(matched);
    }

    [Fact]
    public void Contains_SubstringOnNormalizedForm()
    {
        var matched = ParentValuesPrefilterResolver.ResolveMatchingNames(["Sci-Fi Thriller", "Drama"], "Contains", "SciFi");

        Assert.NotNull(matched);
        Assert.Equal(["Sci-Fi Thriller"], matched);
    }

    [Fact]
    public void Contains_EmptyNormalizedNeedleStaysPerItem()
    {
        // "!!!" normalizes to "" and would match every dumped name.
        Assert.Null(ParentValuesPrefilterResolver.ResolveMatchingNames(["Action"], "Contains", "!!!"));
    }

    [Fact]
    public void IsIn_PerTermSubstringSemantics()
    {
        // Mirrors Engine.AnyItemIsInList: semicolon terms, each a substring match -
        // "Com" must also match "Comedy".
        var matched = ParentValuesPrefilterResolver.ResolveMatchingNames(["Action", "Drama", "Comedy"], "IsIn", "Drama; Com");

        Assert.NotNull(matched);
        Assert.Equal(["Drama", "Comedy"], matched);
    }

    [Fact]
    public void IsIn_AllTermsEmptyStaysPerItem()
    {
        Assert.Null(ParentValuesPrefilterResolver.ResolveMatchingNames(["Action"], "IsIn", " ; ;!"));
    }

    [Fact]
    public void IsIn_AnyEmptyNormalizedTermStaysPerItem()
    {
        // "!!" is a live plugin-side substring term against raw values; silently dropping
        // it would under-match, so its presence must force the whole rule per-item.
        Assert.Null(ParentValuesPrefilterResolver.ResolveMatchingNames(["Action", "Drama"], "IsIn", "Drama;!!"));
    }

    [Theory]
    [InlineData("MatchRegex")]
    [InlineData("NotEqual")]
    [InlineData("NotContains")]
    [InlineData("IsNotIn")]
    public void UnsupportedOperatorsStayPerItem(string ruleOperator)
    {
        Assert.Null(ParentValuesPrefilterResolver.ResolveMatchingNames(["Action"], ruleOperator, "Action"));
    }

    [Fact]
    public void MatchedWhitespaceOnlyNameAbortsPushdown()
    {
        // " " normalizes to "" and matches Equal "!!!" - but a blank value cannot be
        // pushed as a query value, and skipping a matched name could drop a true match.
        Assert.Null(ParentValuesPrefilterResolver.ResolveMatchingNames([" "], "Equal", "!!!"));
    }

    [Fact]
    public void MoreThanMaxMatchedNamesStaysPerItem()
    {
        var names = Enumerable.Range(0, ParentValuesPrefilterResolver.MaxMatchedNames + 1)
            .Select(i => $"Genre {i}")
            .ToList();

        Assert.Null(ParentValuesPrefilterResolver.ResolveMatchingNames(names, "Contains", "Genre"));
    }

    // ---- Studio id resolution (materialization coverage) ----

    [Fact]
    public void Studios_ResolvesIdsWhenEveryMatchingNameIsMaterialized()
    {
        var id = Guid.NewGuid();
        var ids = ParentValuesPrefilterResolver.ResolveStudioIds(
            ["Warner Bros."],
            [(id, "Warner Bros.")],
            "Equal",
            "warner bros");

        Assert.NotNull(ids);
        Assert.Equal([id], ids);
    }

    [Fact]
    public void Studios_DiacriticVariantStillCoveredViaCleanValueKey()
    {
        var id = Guid.NewGuid();
        var ids = ParentValuesPrefilterResolver.ResolveStudioIds(
            ["Pathé"],
            [(id, "Pathe")],
            "Equal",
            "Pathé");

        Assert.NotNull(ids);
        Assert.Equal([id], ids);
    }

    [Fact]
    public void Studios_UnmaterializedMatchingNameForcesFallback()
    {
        // "Warhol Films" exists in ItemValues (items carry the string) but its by-name
        // Studio item was never created - a StudioIds query can never reach those items.
        var ids = ParentValuesPrefilterResolver.ResolveStudioIds(
            ["Warner Bros.", "Warhol Films"],
            [(Guid.NewGuid(), "Warner Bros.")],
            "Contains",
            "War");

        Assert.Null(ids);
    }

    [Fact]
    public void Studios_PunctuationVariantIsNotAssumedCovered()
    {
        // CleanValue keeps punctuation (the 10.11 clean, the finest either ABI applies):
        // "Sci-Fi" vs "Sci Fi" may share a group on Jellyfin 12 but not on 10.11, so
        // coverage cannot be claimed and the rule must stay per-item.
        var ids = ParentValuesPrefilterResolver.ResolveStudioIds(
            ["Sci-Fi"],
            [(Guid.NewGuid(), "Sci Fi")],
            "Equal",
            "Sci-Fi");

        Assert.Null(ids);
    }

    [Fact]
    public void Studios_ZeroMatchesStaysPerItem()
    {
        Assert.Null(ParentValuesPrefilterResolver.ResolveStudioIds(
            ["Warner Bros."],
            [(Guid.NewGuid(), "Warner Bros.")],
            "Equal",
            "Paramount"));
    }

    [Fact]
    public void Studios_UnionsAllIdsSharingACleanedName()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var ids = ParentValuesPrefilterResolver.ResolveStudioIds(
            ["Pathe"],
            [(first, "Pathe"), (second, "Pathé")],
            "Equal",
            "pathe");

        Assert.NotNull(ids);
        Assert.Equal(2, ids.Length);
        Assert.Contains(first, ids);
        Assert.Contains(second, ids);
    }

    [Fact]
    public void Studios_MatchRegexStaysPerItem()
    {
        Assert.Null(ParentValuesPrefilterResolver.ResolveStudioIds(
            ["Warner Bros."],
            [(Guid.NewGuid(), "Warner Bros.")],
            "MatchRegex",
            "Warner.*"));
    }
}
