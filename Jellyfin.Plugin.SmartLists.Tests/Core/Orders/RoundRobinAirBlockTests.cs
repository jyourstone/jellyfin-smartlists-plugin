using System.Collections.Concurrent;
using Jellyfin.Plugin.SmartLists.Core.Orders;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.Orders;

/// <summary>
/// Covers the air-block machinery in <see cref="RoundRobinBase"/>: grouping a playlist by
/// Collections with air-date within-group order is meant to replay a franchise the way it aired.
/// A "block" is a run of episodes that aired within a configurable window of each other across
/// DIFFERENT shows - a same-night crossover, or a franchise week - and the interleave emits one
/// WHOLE block per collection per cycle instead of one item, so a crossover night is never split
/// across a rotation.
///
/// Four things this file pins:
/// 1. <see cref="RoundRobinBase.UsesAirBlocks"/> - the public gate: Collections grouping AND
///    air-date within-group order AND no shuffle, all three required independently.
/// 2. <see cref="RoundRobinBase.CompareWithinGroupByAirDate"/> - the tiered comparator: day
///    precision, missing-date-sorts-first, episode-before-non-episode, series-Sort-Title for
///    same-day cross-series ties, then fall-through to <see cref="RoundRobinBase.CompareWithinGroup"/>.
/// 3. <see cref="RoundRobinBase.ChunkIntoAirBlocks"/> - the chaining rule: a block extends only
///    while the NEXT item aired within the window of the PREVIOUS item, never repeats a show, and
///    an item with no air date can neither join nor be joined.
/// 4. The AIR-BLOCK branch of <c>BuildInterleavedPositions</c> (reached through
///    <see cref="RoundRobinBase.PreComputePositions"/> with GroupByField == "Collections",
///    OrderWithinGroupsByAirDate == true, and a populated CollectionGroupKeys map) - block-at-a-time
///    interleaving, contrasted against the plain item-at-a-time interleave.
///
/// NOTE ON THE TWO GATES: <c>UsesAirBlocks</c> (which tells SmartList whether to prepare block
/// state) and the internal flag inside <c>BuildInterleavedPositions</c> (which decides whether the
/// interleave emits blocks) both route through <c>RoundRobinBase.ShouldUseAirBlocks</c>, so they
/// cannot drift. They previously did: the interleave gated on <c>collectionGroupKeys != null</c>
/// alone and never inspected the field name. A dedicated test below guards against a regression.
///
/// OUT OF SCOPE (owned by other test files): ExtractGroupKey/CompareWithinGroup/Shuffle in
/// isolation, the plain non-air-block interleave on its own, and
/// RoundRobinLeastRecentlyWatchedOrder's recency ordering / mid-block hold.
/// </summary>
public class RoundRobinAirBlockTests
{
    // ---------------------------------------------------------------------------------- helpers

    /// <summary>Renders a chunked block list as "a,b | c,d" - one block per " | " segment.</summary>
    private static string RenderBlocks(List<List<BaseItem>> blocks) =>
        string.Join(" | ", blocks.Select(b => string.Join(",", b.Select(i => i.Name))));

    /// <summary>Item names in ascending assigned-position order; unassigned items sort last.</summary>
    private static string[] NamesInPositionOrder(ConcurrentDictionary<Guid, int> positions, IEnumerable<BaseItem> items) =>
        items.OrderBy(i => positions.TryGetValue(i.Id, out var p) ? p : int.MaxValue).Select(i => i.Name).ToArray();

    /// <summary>The group-ordering strategy RoundRobinOrder uses: plain alphabetical ascending.</summary>
    private static readonly Func<IEnumerable<string>, List<string>> OrderKeysAsc =
        keys => keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    // ============================================================================================
    // UsesAirBlocks - the gate
    // ============================================================================================

    [Theory]
    [InlineData("Collections", true, true)] // baseline: all conditions met
    [InlineData("SeriesName", true, false)] // wrong field
    [InlineData("Genres", true, false)] // wrong field, another value
    [InlineData("Collections", false, false)] // no air-date within-group order
    public void UsesAirBlocks_RequiresCollectionsGroupingAndAirDateOrder(string groupByField, bool orderByAirDate, bool expected)
    {
        var order = new RoundRobinOrder { GroupByField = groupByField, OrderWithinGroupsByAirDate = orderByAirDate };

        Assert.Equal(expected, order.UsesAirBlocks);
    }

    [Fact]
    public void UsesAirBlocks_False_ForShuffledOrder_EvenWhenConfiguredForCollectionsAndAirDate()
    {
        // RoundRobinShuffledOrder overrides ShuffleWithinGroups => true; shuffle wins over
        // air-date order even when every other condition for blocks is satisfied.
        var shuffled = new RoundRobinShuffledOrder { GroupByField = "Collections", OrderWithinGroupsByAirDate = true };
        Assert.False(shuffled.UsesAirBlocks);

        // Contrast: RoundRobinRandomOrder randomizes GROUP order but never overrides
        // ShuffleWithinGroups, so the identical configuration keeps blocks on for it - proving
        // ShuffleWithinGroups specifically (not "any random-flavoured order") is the differentiator.
        var random = new RoundRobinRandomOrder { GroupByField = "Collections", OrderWithinGroupsByAirDate = true };
        Assert.True(random.UsesAirBlocks);
    }

    /// <summary>
    /// The two air-block gates must agree. <see cref="RoundRobinBase.UsesAirBlocks"/> tells
    /// SmartList whether to prepare block state; the internal flag inside
    /// BuildInterleavedPositions decides whether the interleave actually emits blocks. Both now
    /// route through <see cref="RoundRobinBase.ShouldUseAirBlocks"/>, so a non-Collections field
    /// cannot get block interleaving no matter what else is supplied.
    ///
    /// This previously diverged: the interleave gated on `collectionGroupKeys != null` alone and
    /// never inspected the field name, so grouping by "Genres" got full air-block chunking the
    /// instant ANY non-null map was passed - even an empty one with no relevant entries. It was
    /// unreachable in production only because SmartList.cs happens to set CollectionGroupKeys
    /// exclusively for Collections grouping; nothing enforced that. The assertion below is the
    /// regression guard.
    /// </summary>
    [Fact]
    public void BuildInterleavedPositions_GatesAirBlocksOnGroupByFieldName_NotOnCollectionMapPresenceAlone()
    {
        var day = new DateTime(2024, 1, 1);

        var comedy1 = TestItems.Ep("Gamma", 1, 1, aired: day, name: "Comedy1");
        comedy1.Genres = ["Comedy"];
        var comedy2 = TestItems.Ep("Gamma", 1, 2, aired: day.AddDays(1), name: "Comedy2");
        comedy2.Genres = ["Comedy"];

        var dramaAlpha = TestItems.Ep("Alpha", 1, 1, aired: day, name: "DramaAlpha");
        dramaAlpha.Genres = ["Drama"];
        var dramaBeta = TestItems.Ep("Beta", 1, 2, aired: day, name: "DramaBeta"); // crossover: same day, different show
        dramaBeta.Genres = ["Drama"];

        var items = new List<BaseItem> { comedy1, comedy2, dramaAlpha, dramaBeta };
        var emptyCollectionMap = new Dictionary<Guid, string>(); // non-null, and its (zero) entries are irrelevant to "Genres"

        var withoutMap = RoundRobinBase.BuildInterleavedPositions(
            items, "Genres", OrderKeysAsc, "Test", null, false, collectionGroupKeys: null, airDateWithinGroups: true);
        var withEmptyMap = RoundRobinBase.BuildInterleavedPositions(
            items, "Genres", OrderKeysAsc, "Test", null, false, collectionGroupKeys: emptyCollectionMap, airDateWithinGroups: true);

        // "Genres" grouping is a plain per-item interleave - the Drama pair is split across cycles.
        Assert.Equal(new[] { "Comedy1", "DramaAlpha", "Comedy2", "DramaBeta" }, NamesInPositionOrder(withoutMap, items));

        // Supplying a non-null map does NOT switch it into block mode: the field name is part of
        // the gate, so the result is identical. Before the gates were unified this produced
        // "Comedy1, DramaAlpha, DramaBeta, Comedy2" - the Drama pair glued into one block.
        Assert.Equal(NamesInPositionOrder(withoutMap, items), NamesInPositionOrder(withEmptyMap, items));
        Assert.NotEqual(new[] { "Comedy1", "DramaAlpha", "DramaBeta", "Comedy2" }, NamesInPositionOrder(withEmptyMap, items));

        // And the public gate agrees with what the interleave just did.
        Assert.False(new RoundRobinOrder { GroupByField = "Genres", OrderWithinGroupsByAirDate = true }.UsesAirBlocks);
    }

    // ============================================================================================
    // CompareWithinGroupByAirDate - tiered comparator
    // ============================================================================================

    /// <summary>
    /// Two items on the same calendar day but different times of day must tie on date (day
    /// precision, not full DateTime). Proven by making the earlier-time item ALSO the
    /// higher-episode-number item: if raw DateTime were compared, the earlier time would win; day
    /// precision instead ties on date and lets the season/episode fallback decide, producing the
    /// opposite order.
    /// </summary>
    [Fact]
    public void CompareWithinGroupByAirDate_TiesAtDayPrecision_IgnoringTimeOfDay()
    {
        var day = new DateTime(2024, 6, 1);
        var earlyTimeHigherEpisode = TestItems.Ep("Show", 1, 2, aired: day.AddHours(3));
        var lateTimeLowerEpisode = TestItems.Ep("Show", 1, 1, aired: day.AddHours(20));

        // If time-of-day mattered, the 03:00 item would sort first. It does not: both fall on the
        // same day, so the tie falls through to season/episode, and episode 1 sorts before episode 2.
        Assert.True(RoundRobinBase.CompareWithinGroupByAirDate(lateTimeLowerEpisode, earlyTimeHigherEpisode) < 0);
        Assert.True(RoundRobinBase.CompareWithinGroupByAirDate(earlyTimeHigherEpisode, lateTimeLowerEpisode) > 0);
    }

    [Fact]
    public void CompareWithinGroupByAirDate_MissingDate_SortsFirst()
    {
        var noDate = TestItems.Ep("Show", 1, 1, aired: null);
        var withDate = TestItems.Ep("Show", 1, 1, aired: new DateTime(2024, 1, 1));

        Assert.True(RoundRobinBase.CompareWithinGroupByAirDate(noDate, withDate) < 0);
        Assert.True(RoundRobinBase.CompareWithinGroupByAirDate(withDate, noDate) > 0);
    }

    [Fact]
    public void CompareWithinGroupByAirDate_SameDay_EpisodesSortBeforeNonEpisodes()
    {
        var day = new DateTime(2024, 1, 1);
        var episode = TestItems.Ep("Show", 1, 1, aired: day);
        var movie = TestItems.Mov("A Movie", aired: day);

        Assert.True(RoundRobinBase.CompareWithinGroupByAirDate(episode, movie) < 0);
        Assert.True(RoundRobinBase.CompareWithinGroupByAirDate(movie, episode) > 0);
    }

    /// <summary>
    /// The documented knob users edit to order a crossover night: same-day episodes of DIFFERENT
    /// series compare by the series' Sort Title. Both cases below use the SAME two series NAMES
    /// ("Zulu Show", "Alpha Show" - Zulu alphabetically after Alpha) so that only the Sort Title
    /// changes between rows; the flip in expected order proves Sort Title decides, not Name.
    /// Episode numbers are deliberately reversed (Zulu = ep2, Alpha = ep1) relative to the
    /// expected order, so a mutation that dropped this tier and fell through to
    /// CompareWithinGroup's season/episode ordering would fail row 1's assertion.
    /// </summary>
    [Theory]
    [InlineData("1 First", "2 Second", true)] // Zulu's sort title comes first -> Zulu before Alpha
    [InlineData("9 Last", "0 First", false)] // swapped sort titles -> now Alpha before Zulu
    public void CompareWithinGroupByAirDate_SameDayDifferentSeries_DecidesBySeriesSortTitle_NotName(
        string zuluSortTitle, string alphaSortTitle, bool zuluFirst)
    {
        var day = new DateTime(2024, 3, 1);
        var zuluShow = TestItems.Show("Zulu Show", sortName: zuluSortTitle);
        var alphaShow = TestItems.Show("Alpha Show", sortName: alphaSortTitle);
        var zuluEp = TestItems.Ep("Zulu Show", 1, 2, aired: day, show: zuluShow);
        var alphaEp = TestItems.Ep("Alpha Show", 1, 1, aired: day, show: alphaShow);

        var cmp = RoundRobinBase.CompareWithinGroupByAirDate(zuluEp, alphaEp);

        Assert.Equal(zuluFirst, cmp < 0);
    }

    /// <summary>
    /// An episode whose Series cannot be resolved (SeriesId points at nothing registered) falls
    /// back to its denormalized SeriesName rather than throwing or collapsing to empty. The
    /// fallback name is chosen to sort AFTER the resolvable episode's real sort title, so a
    /// mutation that fell back to "" (which would sort before everything) cannot masquerade as
    /// this test passing.
    /// </summary>
    [Fact]
    public void CompareWithinGroupByAirDate_UnresolvableSeries_FallsBackToDenormalizedSeriesName()
    {
        var day = new DateTime(2024, 3, 1);
        var registeredShow = TestItems.Show("Registered Show", sortName: "Aaa Registered Show");
        var resolvable = TestItems.Ep("Registered Show", 1, 1, aired: day, show: registeredShow);

        var unresolvable = TestItems.Ep("Zzz Ghost Show", 1, 1, aired: day);
        unresolvable.SeriesId = Guid.NewGuid(); // never registered with TestLibraryManager -> Series resolves null

        Assert.True(RoundRobinBase.CompareWithinGroupByAirDate(unresolvable, resolvable) > 0);
        Assert.True(RoundRobinBase.CompareWithinGroupByAirDate(resolvable, unresolvable) < 0);
    }

    [Fact]
    public void CompareWithinGroupByAirDate_SameDaySameSeries_FallsThroughToSeasonThenEpisode()
    {
        var day = new DateTime(2024, 3, 1);
        var show = TestItems.Show("Show");

        var earlierSeason = TestItems.Ep("Show", 1, 5, aired: day, show: show);
        var laterSeason = TestItems.Ep("Show", 2, 1, aired: day, show: show);
        Assert.True(RoundRobinBase.CompareWithinGroupByAirDate(earlierSeason, laterSeason) < 0);

        var earlierEpisode = TestItems.Ep("Show", 1, 1, aired: day, show: show);
        var laterEpisode = TestItems.Ep("Show", 1, 3, aired: day, show: show);
        Assert.True(RoundRobinBase.CompareWithinGroupByAirDate(earlierEpisode, laterEpisode) < 0);
    }

    // ============================================================================================
    // ChunkIntoAirBlocks - the chaining rule
    // Input below is always fed already sorted by air date, matching the documented contract
    // (the caller sorts first); unsorted input is out of contract and not exercised here.
    // ============================================================================================

    /// <summary>
    /// A block extends only while the next item aired within the window of the PREVIOUS item, not
    /// of the block's start - so a chain of small link-to-link gaps can span far more than the
    /// window overall. Five items 1 day apart, window 1 day: every consecutive pair chains, so
    /// all five land in one block spanning 4 days.
    /// </summary>
    [Fact]
    public void ChunkIntoAirBlocks_ChainsItemToItem_SoATightChainCanSpanFarMoreThanTheWindow()
    {
        var start = new DateTime(2024, 1, 1);
        var items = new List<BaseItem>
        {
            TestItems.Ep("Show1", 1, 1, aired: start, name: "Ep1"),
            TestItems.Ep("Show2", 1, 1, aired: start.AddDays(1), name: "Ep2"),
            TestItems.Ep("Show3", 1, 1, aired: start.AddDays(2), name: "Ep3"),
            TestItems.Ep("Show4", 1, 1, aired: start.AddDays(3), name: "Ep4"),
            TestItems.Ep("Show5", 1, 1, aired: start.AddDays(4), name: "Ep5"),
        };

        var blocks = RoundRobinBase.ChunkIntoAirBlocks(items, windowDays: 1);

        Assert.Equal("Ep1,Ep2,Ep3,Ep4,Ep5", RenderBlocks(blocks));
    }

    [Fact]
    public void ChunkIntoAirBlocks_NeverContainsTheSameShowTwice_EvenOnTheSameDay()
    {
        var day = new DateTime(2024, 1, 1);
        var items = new List<BaseItem>
        {
            TestItems.Ep("ShowX", 1, 1, aired: day, name: "X1"),
            TestItems.Ep("ShowX", 1, 2, aired: day, name: "X2"), // same show, same day - must NOT chain
        };

        var blocks = RoundRobinBase.ChunkIntoAirBlocks(items, windowDays: 3);

        Assert.Equal("X1 | X2", RenderBlocks(blocks));
    }

    /// <summary>
    /// An item with no air date can never chain onto a preceding block (forms a block of one) and
    /// also breaks the chain for whatever follows it - even when the item after it would otherwise
    /// have been within the window of the item before the gap.
    /// </summary>
    [Fact]
    public void ChunkIntoAirBlocks_ItemWithNoAirDate_FormsItsOwnBlock_AndBreaksTheChainAfterIt()
    {
        var day = new DateTime(2024, 1, 1);
        var items = new List<BaseItem>
        {
            TestItems.Ep("ShowA", 1, 1, aired: day, name: "A1"),
            TestItems.Ep("ShowB", 1, 1, aired: null, name: "NoDate"),
            TestItems.Ep("ShowC", 1, 1, aired: day, name: "C1"), // same day as A1 - would chain if NoDate weren't between them
        };

        var blocks = RoundRobinBase.ChunkIntoAirBlocks(items, windowDays: 3);

        Assert.Equal("A1 | NoDate | C1", RenderBlocks(blocks));
    }

    [Theory]
    [InlineData(0, "First,Second")] // same day (0-day gap) chains when the window is 0
    [InlineData(1, "First | Second")] // 1-day gap does not chain when the window is 0
    public void ChunkIntoAirBlocks_WindowZero_MeansSameDayOnly(int gapDays, string expected)
    {
        var day = new DateTime(2024, 1, 1);
        var items = new List<BaseItem>
        {
            TestItems.Ep("ShowA", 1, 1, aired: day, name: "First"),
            TestItems.Ep("ShowB", 1, 1, aired: day.AddDays(gapDays), name: "Second"),
        };

        var blocks = RoundRobinBase.ChunkIntoAirBlocks(items, windowDays: 0);

        Assert.Equal(expected, RenderBlocks(blocks));
    }

    // ============================================================================================
    // BuildInterleavedPositions - windowDays clamping
    // Clamping itself happens at the CALL SITE (Math.Clamp before invoking ChunkIntoAirBlocks),
    // not inside ChunkIntoAirBlocks, which takes windowDays as given. Each test below proves the
    // clamp actually ran by contrasting against what the UNCLAMPED value would have produced.
    // ============================================================================================

    [Fact]
    public void BuildInterleavedPositions_ClampsNegativeAirBlockWindowDaysToZero()
    {
        var day = new DateTime(2024, 1, 1);
        var itemA = TestItems.Ep("ShowA", 1, 1, aired: day, name: "A");
        var itemB = TestItems.Ep("ShowB", 1, 1, aired: day, name: "B"); // same day as A, different show
        var itemC = TestItems.Ep("ShowC", 1, 1, aired: day.AddDays(10), name: "C"); // far away - never chains either way
        var itemD = TestItems.Ep("ShowD", 1, 1, aired: day, name: "D");

        var franchiseItems = new List<BaseItem> { itemA, itemB, itemC };
        var allItems = new List<BaseItem> { itemA, itemB, itemC, itemD };
        var map = TestItems.CollectionMap(("Franchise", [.. franchiseItems]), ("Solo", [itemD]));

        var withNegative = RoundRobinBase.BuildInterleavedPositions(
            allItems, "Collections", OrderKeysAsc, "Test", null, false, map, true, airBlockWindowDays: -5);

        // Clamped to 0: A and B (0-day gap) chain into one block; C stays separate.
        Assert.Equal(new[] { "A", "B", "D", "C" }, NamesInPositionOrder(withNegative, allItems));

        // Ground truth: fed the raw -5 with no clamp, ChunkIntoAirBlocks treats every gap - even
        // the 0-day gap between A and B - as outside the window (0 <= -5 is false), so nothing
        // chains. This is what -5 would have produced had the clamp not run.
        var unclampedBlocks = RoundRobinBase.ChunkIntoAirBlocks(franchiseItems, windowDays: -5);
        Assert.Equal(3, unclampedBlocks.Count);
    }

    [Fact]
    public void BuildInterleavedPositions_ClampsHugeAirBlockWindowDaysToMax()
    {
        var day0 = new DateTime(2024, 1, 1);
        var gapBeyondMax = RoundRobinBase.MaxAirBlockWindowDays + 1;
        var itemA = TestItems.Ep("ShowA", 1, 1, aired: day0, name: "A");
        var itemB = TestItems.Ep("ShowB", 1, 1, aired: day0.AddDays(gapBeyondMax), name: "B");
        var itemD = TestItems.Ep("ShowD", 1, 1, aired: day0, name: "D");

        var franchiseItems = new List<BaseItem> { itemA, itemB };
        var allItems = new List<BaseItem> { itemA, itemB, itemD };
        var map = TestItems.CollectionMap(("Franchise", [.. franchiseItems]), ("Solo", [itemD]));

        var withHugeValue = RoundRobinBase.BuildInterleavedPositions(
            allItems, "Collections", OrderKeysAsc, "Test", null, false, map, true, airBlockWindowDays: 99_999);

        // Clamped to MaxAirBlockWindowDays (30): a 31-day gap exceeds it, so A and B stay separate
        // blocks and Solo's single item interleaves between them.
        Assert.Equal(new[] { "A", "D", "B" }, NamesInPositionOrder(withHugeValue, allItems));

        // Ground truth: without the clamp, a 31-day gap fits easily inside a 99999-day window and
        // the two Franchise episodes would chain into a single block instead.
        var unclampedBlocks = RoundRobinBase.ChunkIntoAirBlocks(franchiseItems, windowDays: 99_999);
        Assert.Single(unclampedBlocks);
    }

    // ============================================================================================
    // The air-block interleave itself (PreComputePositions -> BuildInterleavedPositions block branch)
    // ============================================================================================

    /// <summary>
    /// Two collections: Marvel forms a 2-item crossover block plus a solo block (2 blocks total);
    /// DC forms a single solo block. One WHOLE block is emitted per collection per cycle, the
    /// crossover pair stays contiguous, DC drops out of the rotation after its one block while
    /// Marvel keeps rotating, and positions are dense with no gaps or duplicates.
    /// </summary>
    [Fact]
    public void AirBlockInterleave_EmitsOneWholeBlockPerCollectionPerCycle_AndDropsOutCollectionsWithFewerBlocks()
    {
        var day1 = new DateTime(2024, 1, 1);
        var day10 = day1.AddDays(10);

        var showA1 = TestItems.Ep("Alpha", 1, 1, aired: day1, name: "A1");
        var showB1 = TestItems.Ep("Bravo", 1, 2, aired: day1, name: "B1"); // crossover with A1
        var showA2 = TestItems.Ep("Alpha", 1, 3, aired: day10, name: "A2"); // too far from A1/B1 to chain
        var showC1 = TestItems.Ep("Charlie", 1, 1, aired: day1, name: "C1");

        var marvelItems = new BaseItem[] { showA1, showB1, showA2 };
        var dcItems = new BaseItem[] { showC1 };
        var items = marvelItems.Concat(dcItems).ToList();
        var map = TestItems.CollectionMap(("Marvel", marvelItems), ("DC", dcItems));

        var order = new RoundRobinOrder
        {
            GroupByField = "Collections",
            OrderWithinGroupsByAirDate = true,
            CollectionGroupKeys = map,
        };
        order.PreComputePositions(items);

        Assert.Equal(new[] { "C1", "A1", "B1", "A2" }, NamesInPositionOrder(order.ItemPositions, items));

        // The crossover block (A1+B1) stays contiguous.
        Assert.Equal(order.ItemPositions[showA1.Id] + 1, order.ItemPositions[showB1.Id]);

        // DC contributed only its single block at the first cycle, then dropped out of the
        // rotation entirely while Marvel kept going for a second cycle (A2).
        Assert.Equal(0, order.ItemPositions[showC1.Id]);
        Assert.True(order.ItemPositions[showA2.Id] > order.ItemPositions[showB1.Id]);

        // Positions are dense: 0..n-1, no gaps or duplicates.
        Assert.Equal(new[] { 0, 1, 2, 3 }, order.ItemPositions.Values.OrderBy(v => v));
    }

    /// <summary>
    /// Same shape as above, but DC also gets 2 items (2 blocks, since they're the same show and
    /// can never chain with each other) so it competes with Marvel across two interleave cycles.
    /// Contrasted against OrderWithinGroupsByAirDate == false on the SAME input: air-date order
    /// keeps the crossover block glued together (DC's second item lands AFTER the whole block),
    /// while plain order interleaves one item at a time (DC's second item lands BETWEEN the
    /// crossover pair) - that difference is the entire feature.
    /// </summary>
    [Fact]
    public void AirBlockInterleave_KeepsMultiItemBlocksContiguous_ContrastedAgainstPlainPerItemInterleave()
    {
        var day1 = new DateTime(2024, 1, 1);
        var day10 = day1.AddDays(10);

        var showA1 = TestItems.Ep("Alpha", 1, 1, aired: day1, name: "A1");
        var showB1 = TestItems.Ep("Bravo", 1, 2, aired: day1, name: "B1"); // crossover with A1
        var showA2 = TestItems.Ep("Alpha", 1, 3, aired: day10, name: "A2"); // too far to chain
        var showC1 = TestItems.Ep("Charlie", 1, 1, aired: day1, name: "C1");
        var showC2 = TestItems.Ep("Charlie", 1, 2, aired: day1, name: "C2"); // same show as C1 - never chains with it

        var marvelItems = new BaseItem[] { showA1, showB1, showA2 };
        var dcItems = new BaseItem[] { showC1, showC2 };
        var items = marvelItems.Concat(dcItems).ToList();
        var map = TestItems.CollectionMap(("Marvel", marvelItems), ("DC", dcItems));

        var blockOrder = new RoundRobinOrder
        {
            GroupByField = "Collections",
            OrderWithinGroupsByAirDate = true,
            CollectionGroupKeys = map,
        };
        blockOrder.PreComputePositions(items);

        var plainOrder = new RoundRobinOrder
        {
            GroupByField = "Collections",
            OrderWithinGroupsByAirDate = false,
            CollectionGroupKeys = map,
        };
        plainOrder.PreComputePositions(items);

        Assert.Equal(new[] { "C1", "A1", "B1", "C2", "A2" }, NamesInPositionOrder(blockOrder.ItemPositions, items));
        Assert.Equal(new[] { "C1", "A1", "C2", "B1", "A2" }, NamesInPositionOrder(plainOrder.ItemPositions, items));

        // Block mode: the crossover pair stays glued together.
        Assert.Equal(blockOrder.ItemPositions[showA1.Id] + 1, blockOrder.ItemPositions[showB1.Id]);

        // Plain mode: DC's second item is interleaved BETWEEN A1 and B1 - one item at a time.
        Assert.True(plainOrder.ItemPositions[showA1.Id] < plainOrder.ItemPositions[showC2.Id]);
        Assert.True(plainOrder.ItemPositions[showC2.Id] < plainOrder.ItemPositions[showB1.Id]);

        foreach (var positions in new[] { blockOrder.ItemPositions, plainOrder.ItemPositions })
        {
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, positions.Values.OrderBy(v => v));
        }
    }
}
