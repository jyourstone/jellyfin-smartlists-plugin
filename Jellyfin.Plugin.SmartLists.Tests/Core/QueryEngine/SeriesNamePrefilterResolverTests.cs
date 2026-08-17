using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Covers the pure in-memory narrowing of <see cref="SeriesNamePrefilterResolver"/> - the
/// SeriesName counterpart of the People resolver, which derives its candidate set from the
/// bulk-warmed series-name dump and the already-fetched pool instead of DB queries.
///
/// The safety contract under test:
/// - Operator semantics are EXACTLY the per-item semantics (the resolver compiles the rule
///   through the same Engine.CompileRule the compiled rule sets use).
/// - Episodes resolve through SeriesId; a series ABSENT from the dump is unknown and its
///   episodes are always kept (the per-miss GetItemById fallback decides per item).
/// - Seriesless pool items (movies, Series themselves, episodes without a usable SeriesId)
///   evaluate SeriesName "" - they are kept exactly when the rule matches "", which is what
///   makes negative operators an EXACT complement rather than an approximation.
/// - Extras resolve through the owner map, mirroring extraction; unmapped extras evaluate "";
///   with no map at all every extra is kept.
/// - Narrowing requires both the pool and the dump; otherwise the rule stays per-item (null).
///
/// The warmup query itself needs a live Jellyfin and is exercised there, not here.
/// </summary>
public class SeriesNamePrefilterResolverTests
{
    private static PrefilterContext Context(
        IReadOnlyList<BaseItem>? pool,
        IReadOnlyDictionary<Guid, string>? dump,
        IReadOnlyDictionary<Guid, Guid>? extras = null)
        => new(null!, null!, null, null)
        {
            PoolItems = pool,
            SeriesNamesById = dump,
            ExtraOwnerSeriesIds = extras,
        };

    private static Expression Rule(string op, string value) => new("SeriesName", op, value);

    private static HashSet<Guid>? Resolve(
        Expression rule,
        IReadOnlyList<BaseItem>? pool,
        IReadOnlyDictionary<Guid, string>? dump,
        IReadOnlyDictionary<Guid, Guid>? extras = null)
        => new SeriesNamePrefilterResolver().Resolve(rule, Context(pool, dump, extras));

    private static (Series Series, Episode Episode) ShowWithEpisode(string name)
    {
        var series = TestItems.Show(name);
        return (series, TestItems.Ep(name, 1, 1, show: series));
    }

    private static Dictionary<Guid, string> Dump(params Series[] series)
        => series.ToDictionary(s => s.Id, s => s.Name);

    // ---- Gating ----

    [Fact]
    public void NonSeriesNameField_ReturnsNull()
    {
        var (series, episode) = ShowWithEpisode("Breaking Bad");

        var result = new SeriesNamePrefilterResolver()
            .Resolve(new Expression("Name", "Equal", "x"), Context([episode], Dump(series)));

        Assert.Null(result);
    }

    [Fact]
    public void WithoutDump_ReturnsNull()
    {
        var (_, episode) = ShowWithEpisode("Breaking Bad");

        Assert.Null(Resolve(Rule("Equal", "Breaking Bad"), [episode], dump: null));
    }

    [Fact]
    public void WithoutPool_ReturnsNull()
    {
        var (series, _) = ShowWithEpisode("Breaking Bad");

        Assert.Null(Resolve(Rule("Equal", "Breaking Bad"), pool: null, Dump(series)));
    }

    // ---- Episodes ----

    [Fact]
    public void Equal_KeepsOnlyEpisodesOfMatchingSeries_CaseInsensitive()
    {
        var (bb, epBb) = ShowWithEpisode("Breaking Bad");
        var (wire, epWire) = ShowWithEpisode("The Wire");
        var movie = TestItems.Mov("Heat");

        var result = Resolve(Rule("Equal", "BREAKING BAD"), [epBb, epWire, movie], Dump(bb, wire));

        Assert.Equal([epBb.Id], result);
    }

    [Fact]
    public void Equal_NothingMatches_ReturnsEmptySet_AsHardClaim()
    {
        var (bb, epBb) = ShowWithEpisode("Breaking Bad");

        var result = Resolve(Rule("Equal", "Nonexistent"), [epBb], Dump(bb));

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void Episode_OfSeriesMissingFromDump_IsAlwaysKept()
    {
        // The dump deliberately omits this series: the per-item path would resolve it via
        // GetItemById, so the resolver may not claim anything about it.
        var (_, epUnknown) = ShowWithEpisode("Ghost Show");
        var (bb, epBb) = ShowWithEpisode("Breaking Bad");

        var result = Resolve(Rule("Equal", "Breaking Bad"), [epBb, epUnknown], Dump(bb));

        Assert.NotNull(result);
        Assert.Contains(epBb.Id, result);
        Assert.Contains(epUnknown.Id, result);
        Assert.Equal(2, result.Count);
    }

    // ---- Seriesless items + negative operators (exact complement) ----

    [Fact]
    public void NotEqual_IsExactComplement_IncludingSerieslessItems()
    {
        var (bb, epBb) = ShowWithEpisode("Breaking Bad");
        var (wire, epWire) = ShowWithEpisode("The Wire");
        var movie = TestItems.Mov("Heat");
        var epNoSeries = TestItems.Ep("Orphan", 1, 1); // no SeriesId -> SeriesName ""

        var result = Resolve(Rule("NotEqual", "Breaking Bad"), [epBb, epWire, movie, bb, epNoSeries], Dump(bb, wire));

        // The Series item itself and the movie evaluate SeriesName "" and "" != "Breaking Bad".
        Assert.NotNull(result);
        Assert.DoesNotContain(epBb.Id, result);
        Assert.Contains(epWire.Id, result);
        Assert.Contains(movie.Id, result);
        Assert.Contains(bb.Id, result);
        Assert.Contains(epNoSeries.Id, result);
    }

    [Fact]
    public void Seriesless_AreDroppedWhenRuleDoesNotMatchEmpty()
    {
        var (bb, epBb) = ShowWithEpisode("Breaking Bad");
        var movie = TestItems.Mov("Heat");
        var epNoSeries = TestItems.Ep("Orphan", 1, 1);

        var result = Resolve(Rule("Contains", "Breaking"), [epBb, movie, epNoSeries], Dump(bb));

        Assert.Equal([epBb.Id], result);
    }

    // ---- Extras ----

    [Fact]
    public void Extra_MappedToMatchingOwner_IsKept_AndToNonMatchingOwner_IsDropped()
    {
        var (bb, _) = ShowWithEpisode("Breaking Bad");
        var (wire, _) = ShowWithEpisode("The Wire");
        var extraOfBb = TestItems.Mov("Making Of BB");
        extraOfBb.ExtraType = ExtraType.BehindTheScenes;
        var extraOfWire = TestItems.Mov("Making Of Wire");
        extraOfWire.ExtraType = ExtraType.BehindTheScenes;
        var extras = new Dictionary<Guid, Guid> { [extraOfBb.Id] = bb.Id, [extraOfWire.Id] = wire.Id };

        var result = Resolve(Rule("Equal", "Breaking Bad"), [extraOfBb, extraOfWire], Dump(bb, wire), extras);

        Assert.Equal([extraOfBb.Id], result);
    }

    [Fact]
    public void Extra_MappedToOwnerMissingFromDump_IsAlwaysKept()
    {
        var (bb, _) = ShowWithEpisode("Breaking Bad");
        var extra = TestItems.Mov("Making Of");
        extra.ExtraType = ExtraType.BehindTheScenes;
        var extras = new Dictionary<Guid, Guid> { [extra.Id] = Guid.NewGuid() };

        var result = Resolve(Rule("Equal", "Nonexistent"), [extra], Dump(bb), extras);

        Assert.Equal([extra.Id], result);
    }

    [Fact]
    public void Extra_Unmapped_FollowsEmptyStringSemantics()
    {
        var (bb, _) = ShowWithEpisode("Breaking Bad");
        var extra = TestItems.Mov("Making Of");
        extra.ExtraType = ExtraType.BehindTheScenes;
        var noOwners = new Dictionary<Guid, Guid>();

        // Extraction leaves SeriesName "" for an unmapped extra.
        Assert.Empty(Resolve(Rule("Equal", "Breaking Bad"), [extra], Dump(bb), noOwners)!);
        Assert.Equal([extra.Id], Resolve(Rule("NotEqual", "Breaking Bad"), [extra], Dump(bb), noOwners));
    }

    [Fact]
    public void Extra_WithoutOwnerMap_IsAlwaysKept()
    {
        var (bb, _) = ShowWithEpisode("Breaking Bad");
        var extra = TestItems.Mov("Making Of");
        extra.ExtraType = ExtraType.BehindTheScenes;

        var result = Resolve(Rule("Equal", "Nonexistent"), [extra], Dump(bb), extras: null);

        Assert.Equal([extra.Id], result);
    }

    [Fact]
    public void EpisodeThatIsAlsoAnExtra_ResolvesViaSeriesId_MirroringExtractionOrder()
    {
        // ExtractSeriesName checks Episode.SeriesId BEFORE the extras branch, so the owner
        // map must be ignored for an episode with a usable SeriesId.
        var (bb, epBb) = ShowWithEpisode("Breaking Bad");
        var (wire, _) = ShowWithEpisode("The Wire");
        epBb.ExtraType = ExtraType.Trailer;
        var extras = new Dictionary<Guid, Guid> { [epBb.Id] = wire.Id };

        Assert.Equal([epBb.Id], Resolve(Rule("Equal", "Breaking Bad"), [epBb], Dump(bb, wire), extras));
        Assert.Empty(Resolve(Rule("Equal", "The Wire"), [epBb], Dump(bb, wire), extras)!);
    }

    // ---- Remaining operators (semantics come from Engine.CompileRule) ----

    [Fact]
    public void Contains_IsCaseInsensitiveSubstring()
    {
        var (bb, epBb) = ShowWithEpisode("Breaking Bad");
        var (wire, epWire) = ShowWithEpisode("The Wire");

        var result = Resolve(Rule("Contains", "WIRE"), [epBb, epWire], Dump(bb, wire));

        Assert.Equal([epWire.Id], result);
    }

    [Fact]
    public void IsIn_IsSubstringMatchAgainstSemicolonList()
    {
        var (bb, epBb) = ShowWithEpisode("Breaking Bad");
        var (wire, epWire) = ShowWithEpisode("The Wire");

        var result = Resolve(Rule("IsIn", "wire;sopranos"), [epBb, epWire], Dump(bb, wire));

        Assert.Equal([epWire.Id], result);
    }

    [Fact]
    public void IsNotIn_KeepsNonMatchingEpisodesAndSerieslessItems()
    {
        var (bb, epBb) = ShowWithEpisode("Breaking Bad");
        var (wire, epWire) = ShowWithEpisode("The Wire");
        var movie = TestItems.Mov("Heat");

        var result = Resolve(Rule("IsNotIn", "wire"), [epBb, epWire, movie], Dump(bb, wire));

        Assert.NotNull(result);
        Assert.Contains(epBb.Id, result);
        Assert.Contains(movie.Id, result); // "" is not in the list -> IsNotIn matches ""
        Assert.DoesNotContain(epWire.Id, result);
    }

    [Fact]
    public void MatchRegex_IsCaseSensitive()
    {
        var (bb, epBb) = ShowWithEpisode("Breaking Bad");
        var (wire, epWire) = ShowWithEpisode("The Wire");

        Assert.Equal([epWire.Id], Resolve(Rule("MatchRegex", "^The"), [epBb, epWire], Dump(bb, wire)));
        Assert.Empty(Resolve(Rule("MatchRegex", "^the"), [epBb, epWire], Dump(bb, wire))!);
    }

    // ---- Builder wiring ----

    [Fact]
    public void CreateDefault_NegativeSeriesNameRule_Narrows()
    {
        // Proves SupportsNegativeOperators wiring: NotEqual must reach this resolver
        // through the builder's central negative-operator gate.
        var (bb, epBb) = ShowWithEpisode("Breaking Bad");
        var (wire, epWire) = ShowWithEpisode("The Wire");

        var result = CandidateSetBuilder.CreateDefault().Build(
            [new ExpressionSet { Expressions = [Rule("NotEqual", "Breaking Bad")] }],
            Context([epBb, epWire], Dump(bb, wire)));

        Assert.Equal([epWire.Id], result);
    }

    [Fact]
    public void CreateDefault_SeriesNameRule_WithoutDump_NoShrink()
    {
        // Warmup failed (or never ran): SmartList passes no dump, so the rule must stay
        // per-item and the whole build degrades to "no shrink possible".
        var (_, epBb) = ShowWithEpisode("Breaking Bad");

        var result = CandidateSetBuilder.CreateDefault().Build(
            [new ExpressionSet { Expressions = [Rule("Equal", "Breaking Bad")] }],
            Context([epBb], dump: null));

        Assert.Null(result);
    }
}
