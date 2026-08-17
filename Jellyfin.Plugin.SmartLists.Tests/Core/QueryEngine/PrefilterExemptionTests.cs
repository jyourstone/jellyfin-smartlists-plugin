using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Covers the consumption-side exemptions of <see cref="SmartList.FilterByCandidateSet"/> -
/// the items a DB prefilter must never shrink away, per the safety contract:
/// - Extras enter pools via their owner chain (LibraryManagerHelper.FetchExtras), so a
///   candidate query over the main library can never return them - they are always kept.
/// - Series must survive whenever Collections rules are present, so the
///   DoesSeriesMatchCollectionsRules bypass can still see them.
/// Everything else intersects with the candidate set normally.
/// </summary>
public class PrefilterExemptionTests
{
    private static SmartList List(params ExpressionSet[] sets)
    {
        var list = new SmartList(new SmartPlaylistDto { Id = "prefilter-exemption-test", Name = "Exemptions" });
        if (sets.Length > 0)
        {
            list.ExpressionSets = [.. sets];
        }

        return list;
    }

    private static ExpressionSet Set(string memberName)
    {
        return new ExpressionSet { Expressions = [new Expression(memberName, "Equal", "x")] };
    }

    private static Movie Movie(string name)
    {
        return new Movie { Id = Guid.NewGuid(), Name = name };
    }

    [Fact]
    public void CandidateMembershipDecidesOrdinaryItems()
    {
        var kept = Movie("in-set");
        var dropped = Movie("outside-set");
        var list = List(Set("Directors"));

        var result = list.FilterByCandidateSet([kept, dropped], [kept.Id], null, "test");

        Assert.Single(result);
        Assert.Same(kept, result[0]);
    }

    [Fact]
    public void ExtraOutsideCandidateSetIsAlwaysKept()
    {
        var extra = new Video { Id = Guid.NewGuid(), Name = "trailer", ExtraType = ExtraType.Trailer };
        var list = List(Set("Directors"));

        var result = list.FilterByCandidateSet([extra], [], null, "test");

        Assert.Single(result);
        Assert.Same(extra, result[0]);
    }

    [Fact]
    public void SeriesOutsideCandidateSetIsKeptWhenCollectionsRulesPresent()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "series" };
        var list = List(Set("Collections"), Set("Directors"));

        var result = list.FilterByCandidateSet([series], [], null, "test");

        Assert.Single(result);
        Assert.Same(series, result[0]);
    }

    [Fact]
    public void SeriesOutsideCandidateSetIsDroppedWithoutCollectionsRules()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "series" };
        var list = List(Set("Directors"));

        var result = list.FilterByCandidateSet([series], [], null, "test");

        Assert.Empty(result);
    }

    [Fact]
    public void SeriesInsideCandidateSetIsKeptWithoutCollectionsRules()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "series" };
        var list = List(Set("Directors"));

        var result = list.FilterByCandidateSet([series], [series.Id], null, "test");

        Assert.Single(result);
        Assert.Same(series, result[0]);
    }

    [Fact]
    public void NullItemsAreSkipped()
    {
        var kept = Movie("in-set");
        var list = List(Set("Directors"));

        var result = list.FilterByCandidateSet([null!, kept], [kept.Id], null, "test");

        Assert.Single(result);
        Assert.Same(kept, result[0]);
    }
}
