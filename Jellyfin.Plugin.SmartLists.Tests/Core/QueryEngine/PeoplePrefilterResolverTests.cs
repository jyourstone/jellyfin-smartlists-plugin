using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Covers the pure name-resolution step of <see cref="PeoplePrefilterResolver"/> - the
/// piece that decides which STORED person names a people rule matches before the per-name
/// item queries run. Its contract:
///
/// - Operator semantics must be EXACTLY the plugin's per-item semantics (it delegates to
///   the same Engine helpers the compiled rules bind): Equal is whole-name
///   OrdinalIgnoreCase, Contains is substring OrdinalIgnoreCase, IsIn is a
///   semicolon-separated substring list, MatchRegex is case-sensitive.
/// - A pattern that matches the empty string never rides (an empty people list is
///   evaluated against "", so such patterns match items with NO people - unreachable from
///   any name-derived set), nor does an invalid pattern or a negative/unknown operator.
/// - More than MaxMatchedNames matches means the rule is too broad to push down.
/// - Null/empty stored names are skipped (per-item extraction drops them); a MATCHED
///   whitespace-only name aborts the pushdown entirely (it cannot be queried, and
///   silently dropping it could drop a true match).
/// - An empty (non-null) result is a hard "no stored name matches" claim.
///
/// The DB-dump and GetItemIds steps need a live Jellyfin and are exercised there, not here.
/// </summary>
public class PeoplePrefilterResolverTests
{
    private static readonly string[] DefaultNames = ["Bob Smith", "bob smith", "Bobby Smith", "Alice Jones"];

    // ---- Equal ----

    [Fact]
    public void Equal_MatchesWholeNameCaseInsensitive()
    {
        var matched = PeoplePrefilterResolver.ResolveMatchingNames(DefaultNames, "Equal", "BOB SMITH");

        Assert.NotNull(matched);
        Assert.Equal(["Bob Smith", "bob smith"], matched);
    }

    [Fact]
    public void Equal_DoesNotSubstringMatch()
    {
        var matched = PeoplePrefilterResolver.ResolveMatchingNames(DefaultNames, "Equal", "Bob");

        Assert.NotNull(matched);
        Assert.Empty(matched);
    }

    // ---- Contains ----

    [Fact]
    public void Contains_SubstringCaseInsensitive()
    {
        var matched = PeoplePrefilterResolver.ResolveMatchingNames(DefaultNames, "Contains", "SMITH");

        Assert.NotNull(matched);
        Assert.Equal(["Bob Smith", "bob smith", "Bobby Smith"], matched);
    }

    [Fact]
    public void Contains_NoMatches_ReturnsEmptyHardClaim()
    {
        var matched = PeoplePrefilterResolver.ResolveMatchingNames(DefaultNames, "Contains", "Zebra");

        Assert.NotNull(matched);
        Assert.Empty(matched);
    }

    // ---- IsIn ----

    [Fact]
    public void IsIn_SemicolonListUsesSubstringSemanticsAndTrims()
    {
        var matched = PeoplePrefilterResolver.ResolveMatchingNames(DefaultNames, "IsIn", " jones ; BOBBY ");

        Assert.NotNull(matched);
        Assert.Equal(["Bobby Smith", "Alice Jones"], matched);
    }

    [Fact]
    public void IsIn_EmptyOrSeparatorOnlyList_MatchesNothing()
    {
        // AnyItemIsInList returns false for every item when the target list parses empty,
        // so the rule matches nothing - an exact empty claim, not a bail-out.
        var empty = PeoplePrefilterResolver.ResolveMatchingNames(DefaultNames, "IsIn", "");
        var separators = PeoplePrefilterResolver.ResolveMatchingNames(DefaultNames, "IsIn", " ; ; ");

        Assert.NotNull(empty);
        Assert.Empty(empty);
        Assert.NotNull(separators);
        Assert.Empty(separators);
    }

    // ---- MatchRegex ----

    [Fact]
    public void MatchRegex_IsCaseSensitiveLikePerItemEvaluation()
    {
        var matched = PeoplePrefilterResolver.ResolveMatchingNames(DefaultNames, "MatchRegex", "^Bob");

        Assert.NotNull(matched);
        Assert.Equal(["Bob Smith", "Bobby Smith"], matched);
    }

    [Theory]
    [InlineData(".*")]
    [InlineData("^$")]
    [InlineData("")]
    [InlineData("(Bob)?")]
    public void MatchRegex_PatternMatchingEmptyString_NeverRides(string pattern)
    {
        Assert.Null(PeoplePrefilterResolver.ResolveMatchingNames(DefaultNames, "MatchRegex", pattern));
    }

    [Fact]
    public void MatchRegex_InvalidPattern_NeverRides()
    {
        Assert.Null(PeoplePrefilterResolver.ResolveMatchingNames(DefaultNames, "MatchRegex", "["));
    }

    // ---- Operator gating ----

    [Theory]
    [InlineData("NotEqual")]
    [InlineData("NotContains")]
    [InlineData("IsNotIn")]
    [InlineData("GreaterThan")]
    [InlineData("SimilarTo")]
    public void NegativeAndUnknownOperators_NeverRide(string ruleOperator)
    {
        Assert.Null(PeoplePrefilterResolver.ResolveMatchingNames(DefaultNames, ruleOperator, "Bob Smith"));
    }

    // ---- Cap ----

    [Fact]
    public void MatchedNamesAtCap_StillRides()
    {
        var names = Enumerable.Range(1, PeoplePrefilterResolver.MaxMatchedNames).Select(i => $"Person {i}").ToList();

        var matched = PeoplePrefilterResolver.ResolveMatchingNames(names, "Contains", "Person");

        Assert.NotNull(matched);
        Assert.Equal(PeoplePrefilterResolver.MaxMatchedNames, matched.Count);
    }

    [Fact]
    public void MatchedNamesOverCap_NeverRides()
    {
        var names = Enumerable.Range(1, PeoplePrefilterResolver.MaxMatchedNames + 1).Select(i => $"Person {i}").ToList();

        Assert.Null(PeoplePrefilterResolver.ResolveMatchingNames(names, "Contains", "Person"));
    }

    [Fact]
    public void ManyStoredNamesButFewMatches_StillRides()
    {
        var names = Enumerable.Range(1, 10_000).Select(i => $"Person {i}").Append("Unique Name").ToList();

        var matched = PeoplePrefilterResolver.ResolveMatchingNames(names, "Equal", "unique name");

        Assert.NotNull(matched);
        Assert.Equal(["Unique Name"], matched);
    }

    // ---- Degenerate stored names ----

    [Fact]
    public void NullAndEmptyStoredNames_AreSkipped()
    {
        var matched = PeoplePrefilterResolver.ResolveMatchingNames([null!, "", "Bob Smith"], "Contains", "bob");

        Assert.NotNull(matched);
        Assert.Equal(["Bob Smith"], matched);
    }

    [Fact]
    public void MatchedWhitespaceOnlyName_AbortsThePushdown()
    {
        // " " matches Contains " " but cannot be queried (TranslateQuery drops a whitespace
        // Person clause); dropping just that name could drop a true match, so the whole
        // rule must stay per-item.
        Assert.Null(PeoplePrefilterResolver.ResolveMatchingNames([" ", "Bob Smith"], "Contains", " "));
    }

    [Fact]
    public void UnmatchedWhitespaceOnlyName_DoesNotAbort()
    {
        var matched = PeoplePrefilterResolver.ResolveMatchingNames([" ", "Bob Smith"], "Equal", "Bob Smith");

        Assert.NotNull(matched);
        Assert.Equal(["Bob Smith"], matched);
    }

    // ---- Field coverage ----

    [Fact]
    public void HandlesEveryRegistryPeopleFieldExceptActorRoles()
    {
        // ActorRoles is the one people field that can never ride: role strings live on the
        // people map row and are not filterable in either ABI.
        foreach (var field in FieldRegistry.GetPeopleRoleFields())
        {
            if (field == "ActorRoles")
            {
                Assert.False(PeoplePrefilterResolver.HandlesField(field));
            }
            else
            {
                Assert.True(PeoplePrefilterResolver.HandlesField(field), $"People field '{field}' has no prefilter mapping");
            }
        }
    }

    [Fact]
    public void Resolve_UnhandledOrUnavailable_ReturnsNull()
    {
        var resolver = new PeoplePrefilterResolver();
        var context = new PrefilterContext(null!, null!, null, null);

        // ActorRoles and non-people fields are not this resolver's to bound; and without a
        // library manager even a people field must degrade to "no shrink possible".
        Assert.Null(resolver.Resolve(new Expression("ActorRoles", "Contains", "x"), context));
        Assert.Null(resolver.Resolve(new Expression("Genres", "Equal", "x"), context));
        Assert.Null(resolver.Resolve(new Expression("Directors", "Equal", "x"), context));
    }
}
