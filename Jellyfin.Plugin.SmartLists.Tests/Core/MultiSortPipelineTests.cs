using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.Orders;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.SmartLists.Tests.Core;

/// <summary>
/// Covers the multi-sort PIPELINE in SmartList.cs - <c>ApplyMultipleOrders</c>,
/// <c>WrapOrdersWithChildAggregation</c>, <c>ApplySortingCore</c> and <c>IsDescendingOrder</c> - as
/// opposed to any individual Order's own GetSortKey/OrderBy, which is already covered file-by-file
/// under Core/Orders.
///
/// This is a deliberately separate file because three of the four sorting defects fixed in PR #490
/// lived in the PIPELINE, not in any single order class: a correct GetSortKey/OrderBy pair can still
/// sort wrong once it passes through ApplyMultipleOrders/ApplySortingCore, because that code
/// (a) takes a DIFFERENT code path for exactly one sort than for two-or-more (the "single sort
/// optimization"), and (b) actively REWRITES every non-final sort key - a DateTime truncated to a
/// day, an ICompositeSortKey reduced to its PrimaryValue - specifically so secondary sorts are not
/// no-ops. Neither behaviour exists at the single-order level, so no amount of per-order testing can
/// see them; only driving the real loop can.
///
/// All four target methods were widened from private to internal specifically for this file
/// (InternalsVisibleTo is already wired), so everything below calls them directly - no reflection.
/// </summary>
public class MultiSortPipelineTests
{
    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A minimal, valid SmartList with Orders/SortOptions/CollectionSearchDepth set directly, as
    /// suggested by the task: driving everything through a DTO would be a lot of irrelevant setup
    /// for what these tests actually exercise.
    /// </summary>
    private static SmartList MakeSmartList(List<Order>? orders, List<SortOption>? sortOptions = null, int collectionSearchDepth = 0)
    {
        var list = new SmartList(new SmartPlaylistDto { Id = Guid.NewGuid().ToString(), Name = "Test List" })
        {
            Orders = orders!,
            SortOptions = sortOptions,
            CollectionSearchDepth = collectionSearchDepth,
        };
        return list;
    }

    private static string[] Names(IEnumerable<BaseItem> items) => items.Select(i => i.Name).ToArray();

    // ---------------------------------------------------------------------------------
    // Empty / absent Orders
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ApplyMultipleOrders_NullOrders_ReturnsTheItemsUntouched()
    {
        var list = MakeSmartList(orders: null);
        var items = new List<BaseItem> { TestItems.Mov("Zebra"), TestItems.Mov("Alpha"), TestItems.Mov("Middle") };

        var result = list.ApplyMultipleOrders(items, TestItems.User, null, null, new RefreshQueueService.RefreshCache());

        // Same reference, not just the same content - ApplyMultipleOrders hands the input straight
        // back rather than materializing a fresh sequence.
        Assert.Same(items, result);
    }

    [Fact]
    public void ApplyMultipleOrders_EmptyOrders_ReturnsTheItemsUntouched()
    {
        var list = MakeSmartList(orders: []);
        var items = new List<BaseItem> { TestItems.Mov("Zebra"), TestItems.Mov("Alpha"), TestItems.Mov("Middle") };

        var result = list.ApplyMultipleOrders(items, TestItems.User, null, null, new RefreshQueueService.RefreshCache());

        Assert.Same(items, result);
    }

    // ---------------------------------------------------------------------------------
    // Single-sort fast path vs. the multi-sort core - must agree
    //
    // ApplyMultipleOrders returns Order.OrderBy() directly for exactly one effective order;
    // ApplySortingCore drives the same single order through GetSortKey() + LINQ OrderBy instead.
    // SeasonNumberOrder is used deliberately: its GetSortKey is a ComparableTuple4
    // (season/episode/name), the exact shape whose OrderBy/GetSortKey pair diverged in the shipped
    // bug this test guards against.
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyMultipleOrders_SingleOrder_FastPathAgreesWithApplySortingCore(bool descending)
    {
        // Deliberately scrambled: season/episode out of order, so neither path can pass by
        // accidentally preserving input order.
        var s2e1 = TestItems.Ep("Show", 2, 1);
        var s1e2 = TestItems.Ep("Show", 1, 2);
        var s1e1 = TestItems.Ep("Show", 1, 1);
        var s2e2 = TestItems.Ep("Show", 2, 2);
        var items = new List<BaseItem> { s2e1, s1e2, s1e1, s2e2 };

        var expected = descending
            ? new[] { s2e2.Name, s2e1.Name, s1e2.Name, s1e1.Name }
            : new[] { s1e1.Name, s1e2.Name, s2e1.Name, s2e2.Name };

        var list = MakeSmartList([descending ? new SeasonNumberOrderDesc() : new SeasonNumberOrder()]);
        var viaFastPath = Names(list.ApplyMultipleOrders(items, TestItems.User, null, null, new RefreshQueueService.RefreshCache()));

        List<Order> singleOrderList = [descending ? new SeasonNumberOrderDesc() : new SeasonNumberOrder()];
        var viaApplySortingCore = Names(SmartList.ApplySortingCore(
            [.. items], singleOrderList, TestItems.User, null, null, new RefreshQueueService.RefreshCache()));

        Assert.Equal(expected, viaFastPath);
        Assert.Equal(expected, viaApplySortingCore);
    }

    // ---------------------------------------------------------------------------------
    // Multi-level layering: primary decides, secondary only breaks ties, tertiary only breaks
    // remaining ties. Direction is independent per level.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ApplyMultipleOrders_ThreeLevelSort_EachLevelOnlyBreaksTheLevelAboveItsTies()
    {
        // sortName is the tie-break key NameOrder actually reads; Name is only for identifying the
        // item in assertions, so two items can tie on "name" while still being distinguishable here.
        var item1 = TestItems.Mov("Item1", sortName: "A");
        item1.ProductionYear = 2000;
        item1.CommunityRating = 5f;

        var item2 = TestItems.Mov("Item2", sortName: "A");
        item2.ProductionYear = 2000;
        item2.CommunityRating = 3f;

        var item3 = TestItems.Mov("Item3", sortName: "B");
        item3.ProductionYear = 2000;
        item3.CommunityRating = 1f;

        var item4 = TestItems.Mov("Item4", sortName: "Z");
        item4.ProductionYear = 1990;
        item4.CommunityRating = 9f;

        // Scrambled input: year-only sorting would leave item1/item3/item2 in this same relative
        // order (stable sort), so a wrong "only primary applied" result would slip through unless
        // the input already contradicts it.
        var items = new List<BaseItem> { item1, item3, item4, item2 };

        var list = MakeSmartList([new ProductionYearOrder(), new NameOrder(), new CommunityRatingOrder()]);

        var result = Names(list.ApplyMultipleOrders(items, TestItems.User, null, null, new RefreshQueueService.RefreshCache()));

        // year asc: Item4 (1990) first.
        // within year 2000, sortName asc: "A" group (Item1/Item2) before "B" (Item3).
        // within the "A" tie, rating asc: Item2 (3) before Item1 (5).
        Assert.Equal(new[] { "Item4", "Item2", "Item1", "Item3" }, result);
    }

    [Fact]
    public void ApplyMultipleOrders_AscendingPrimary_DescendingSecondary_AppliesEachDirectionIndependently()
    {
        var beta = TestItems.Mov("Beta");
        beta.ProductionYear = 2000;
        var alpha = TestItems.Mov("Alpha");
        alpha.ProductionYear = 2000;
        var zulu = TestItems.Mov("Zulu");
        zulu.ProductionYear = 1990;

        var items = new List<BaseItem> { beta, zulu, alpha };

        var list = MakeSmartList([new ProductionYearOrder(), new NameOrderDesc()]);

        var result = Names(list.ApplyMultipleOrders(items, TestItems.User, null, null, new RefreshQueueService.RefreshCache()));

        // year asc: Zulu (1990) first; within year 2000, name DESC: Beta before Alpha.
        Assert.Equal(new[] { "Zulu", "Beta", "Alpha" }, result);
    }

    // ---------------------------------------------------------------------------------
    // Non-final key simplification #1: a DateTime key is truncated to .Date for every order
    // EXCEPT the last one, so items from the same day tie and the next sort decides. Two
    // symmetric tests: ignored when non-final, honoured when final.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ApplySortingCore_DateTimeKey_NonFinalPosition_TruncatesToTheDay_LettingSecondaryDecide()
    {
        var morning = TestItems.Mov("Alpha");
        morning.DateCreated = new DateTime(2024, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        var evening = TestItems.Mov("Beta");
        evening.DateCreated = new DateTime(2024, 3, 1, 20, 0, 0, DateTimeKind.Utc);

        Assert.IsType<DateTime>(new DateCreatedOrder().GetSortKey(morning, TestItems.User, null, null));

        var orders = new List<Order> { new DateCreatedOrder(), new NameOrderDesc() };
        var result = Names(SmartList.ApplySortingCore(
            [morning, evening], orders, TestItems.User, null, null, new RefreshQueueService.RefreshCache()));

        // If the day-truncation did NOT happen, ascending-by-exact-time would put the earlier
        // "morning" item first regardless of name. Because both fall on the same day, they tie and
        // the descending name sort decides instead: "Beta" (B) before "Alpha" (A).
        Assert.Equal(new[] { "Beta", "Alpha" }, result);
    }

    [Fact]
    public void ApplySortingCore_DateTimeKey_FinalPosition_KeepsFullTimePrecision()
    {
        var dawn = TestItems.Mov("Dawn");
        dawn.ProductionYear = 2000;
        dawn.DateCreated = new DateTime(2024, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        var dusk = TestItems.Mov("Dusk");
        dusk.ProductionYear = 2000; // ties with Dawn, so DateCreated (final) must decide.
        dusk.DateCreated = new DateTime(2024, 3, 1, 20, 0, 0, DateTimeKind.Utc);

        var orders = new List<Order> { new ProductionYearOrder(), new DateCreatedOrder() };
        // Input order is the OPPOSITE of the expected result, so a truncated (same-day, tied) key
        // would fall back to stable-sort input order and get caught.
        var result = Names(SmartList.ApplySortingCore(
            [dusk, dawn], orders, TestItems.User, null, null, new RefreshQueueService.RefreshCache()));

        Assert.Equal(new[] { "Dawn", "Dusk" }, result);
    }

    // ---------------------------------------------------------------------------------
    // Non-final key simplification #2: an ICompositeSortKey (e.g. TrackNumberOrder's
    // album/disc/track/name tuple) is reduced to its PrimaryValue for every order except the last,
    // stripping the embedded disc/track tiebreaker so the user's own secondary sort decides.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ApplySortingCore_CompositeKey_NonFinalPosition_StripsTheEmbeddedTiebreaker_LettingSecondaryDecide()
    {
        var trackLow = TestItems.Track("X", disc: 1, track: 1, name: "Alpha");
        var trackHigh = TestItems.Track("X", disc: 1, track: 2, name: "Bravo");

        Assert.IsAssignableFrom<ICompositeSortKey>(new TrackNumberOrder().GetSortKey(trackLow, TestItems.User, null, null));

        var orders = new List<Order> { new TrackNumberOrder(), new NameOrderDesc() };
        var result = Names(SmartList.ApplySortingCore(
            [trackLow, trackHigh], orders, TestItems.User, null, null, new RefreshQueueService.RefreshCache()));

        // Same album ("X") ties once reduced to PrimaryValue, so the embedded disc/track tiebreaker
        // (which alone would put Low before High) is ignored, and descending name decides instead:
        // "Bravo" (High) before "Alpha" (Low) - the OPPOSITE of natural track order.
        Assert.Equal(new[] { "Bravo", "Alpha" }, result);
    }

    [Fact]
    public void ApplySortingCore_CompositeKey_FinalPosition_HonoursTheEmbeddedTiebreaker()
    {
        var trackLow = TestItems.Track("X", disc: 1, track: 1, name: "Alpha");
        trackLow.ProductionYear = 2000;
        var trackHigh = TestItems.Track("X", disc: 1, track: 2, name: "Bravo");
        trackHigh.ProductionYear = 2000; // ties with Low, so TrackNumberOrder (final) must decide.

        var orders = new List<Order> { new ProductionYearOrder(), new TrackNumberOrder() };
        // Input order is the opposite of the expected result, guarding against an accidental pass
        // via stable-sort input order if the composite key were wrongly stripped here too.
        var result = Names(SmartList.ApplySortingCore(
            [trackHigh, trackLow], orders, TestItems.User, null, null, new RefreshQueueService.RefreshCache()));

        // Same album, same disc -> track number decides: track 1 (Low/"Alpha") before track 2
        // (High/"Bravo"), regardless of name.
        Assert.Equal(new[] { "Alpha", "Bravo" }, result);
    }

    // ---------------------------------------------------------------------------------
    // RandomOrder in multi-sort: ApplySortingCore pre-generates one random key per item BEFORE
    // sorting, keyed by item.Id, so the randomness is stable within one call.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ApplySortingCore_RandomOrder_NonFinalPosition_StillLetsSecondarySortBreakATie()
    {
        // True Random.Next() collisions can't be relied on for a deterministic test, and this file
        // must not assert an exact sequence for genuine randomness. Instead this exploits a real,
        // documented mechanism (RandomOrder_GetSortKey_KeysByIdNotByInstance in
        // RandomAndNoOrderTests.cs): the pre-generated key dictionary is keyed by item.Id, so two
        // DIFFERENT item instances sharing the same Id deterministically resolve to the SAME random
        // key - a guaranteed, reproducible tie for the primary sort to hand off to the secondary.
        var sharedId = Guid.NewGuid();
        var favoured = TestItems.Mov("Zeta");
        favoured.Id = sharedId;
        var other = TestItems.Mov("Alpha");
        other.Id = sharedId;

        var orders = new List<Order> { new RandomOrder(), new NameOrderDesc() };
        var result = Names(SmartList.ApplySortingCore(
            [favoured, other], orders, TestItems.User, null, null, new RefreshQueueService.RefreshCache()));

        // Guaranteed tie on the random primary key -> descending name decides: "Zeta" before "Alpha".
        Assert.Equal(new[] { "Zeta", "Alpha" }, result);
    }

    [Fact]
    public void ApplyMultipleOrders_SingleRandomOrder_OutputIsAlwaysAPermutationOfTheInput()
    {
        var items = Enumerable.Range(0, 10).Select(i => (BaseItem)TestItems.Mov($"Item{i:00}")).ToList();
        var list = MakeSmartList([new RandomOrder()]);

        var result = list.ApplyMultipleOrders(items, TestItems.User, null, null, new RefreshQueueService.RefreshCache()).ToList();

        Assert.Equal(
            items.Select(i => i.Id).OrderBy(id => id),
            result.Select(i => i.Id).OrderBy(id => id));
    }

    [Fact]
    public void ApplySortingCore_RandomOrderCombinedWithASecondarySort_OutputIsAlwaysAPermutationOfTheInput()
    {
        var items = Enumerable.Range(0, 10).Select(i => (BaseItem)TestItems.Mov($"Item{i:00}")).ToList();
        var orders = new List<Order> { new RandomOrder(), new NameOrder() };

        var result = SmartList.ApplySortingCore(
            items, orders, TestItems.User, null, null, new RefreshQueueService.RefreshCache()).ToList();

        Assert.Equal(
            items.Select(i => i.Id).OrderBy(id => id),
            result.Select(i => i.Id).OrderBy(id => id));
    }

    // ---------------------------------------------------------------------------------
    // WrapOrdersWithChildAggregation
    // ---------------------------------------------------------------------------------

    [Fact]
    public void WrapOrdersWithChildAggregation_NullSortOptions_ReturnsTheSameOrdersListUnchanged()
    {
        var orders = new List<Order> { new DateCreatedOrder() };
        var list = MakeSmartList(orders, sortOptions: null, collectionSearchDepth: 3);

        var result = list.WrapOrdersWithChildAggregation(orders, null);

        Assert.Same(orders, result);
    }

    [Fact]
    public void WrapOrdersWithChildAggregation_EmptySortOptions_ReturnsTheSameOrdersListUnchanged()
    {
        var orders = new List<Order> { new DateCreatedOrder() };
        var list = MakeSmartList(orders, sortOptions: [], collectionSearchDepth: 3);

        var result = list.WrapOrdersWithChildAggregation(orders, null);

        Assert.Same(orders, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WrapOrdersWithChildAggregation_CollectionSearchDepthNotPositive_ReturnsTheSameOrdersListUnchanged(int depth)
    {
        var orders = new List<Order> { new DateCreatedOrder() };
        var sortOptions = new List<SortOption> { new() { SortBy = "DateCreated", SortOrder = SortOrder.Ascending } };
        var list = MakeSmartList(orders, sortOptions, depth);

        var result = list.WrapOrdersWithChildAggregation(orders, null);

        Assert.Same(orders, result);
    }

    [Fact]
    public void WrapOrdersWithChildAggregation_NoSortFieldSupportsAggregation_ReturnsTheSameOrdersListUnchanged()
    {
        var orders = new List<Order> { new NameOrder() };
        var sortOptions = new List<SortOption> { new() { SortBy = "Name", SortOrder = SortOrder.Ascending } };
        var list = MakeSmartList(orders, sortOptions, collectionSearchDepth: 3);

        var result = list.WrapOrdersWithChildAggregation(orders, null);

        Assert.Same(orders, result);
    }

    [Fact]
    public void WrapOrdersWithChildAggregation_MixedFields_WrapsOnlyTheSupportedOneAtItsMatchingIndex()
    {
        var dateOrder = new DateCreatedOrderDesc();
        var nameOrder = new NameOrder();
        var orders = new List<Order> { dateOrder, nameOrder };
        var sortOptions = new List<SortOption>
        {
            new() { SortBy = "DateCreated", SortOrder = SortOrder.Descending },
            new() { SortBy = "Name", SortOrder = SortOrder.Ascending },
        };
        var list = MakeSmartList(orders, sortOptions, collectionSearchDepth: 2);

        var result = list.WrapOrdersWithChildAggregation(orders, null);

        Assert.Equal(2, result.Count);

        // Index 0 ("DateCreated") is a supported field -> wrapped. ChildAggregatingOrder doesn't
        // expose the field it wraps directly, so its Name (innerOrder.Name + " (Child Aggregate)")
        // is the closest observable proof of which order and field got wrapped.
        var wrapped = Assert.IsType<ChildAggregatingOrder>(result[0]);
        Assert.Equal(dateOrder.Name + " (Child Aggregate)", wrapped.Name);
        Assert.True(wrapped.IsDescending);

        // Index 1 ("Name") is not a supported field -> kept as the exact same instance.
        Assert.Same(nameOrder, result[1]);
    }

    [Fact]
    public void WrapOrdersWithChildAggregation_SortOptionsShorterThanOrders_LeavesTheExtraTrailingOrdersUnwrapped()
    {
        // LATENT TRAP, pinned rather than fixed: wrapping pairs orders[i] with SortOptions[i] BY
        // INDEX. If the two lists are ever out of sync, any order past the end of SortOptions is
        // silently left unwrapped - even when its own field would otherwise qualify for child
        // aggregation - instead of erroring or falling back to a field-name lookup.
        var firstDate = new DateCreatedOrder();
        var secondDate = new DateCreatedOrderDesc(); // also a supported field, but has no matching SortOption.
        var orders = new List<Order> { firstDate, secondDate };
        var sortOptions = new List<SortOption>
        {
            new() { SortBy = "DateCreated", SortOrder = SortOrder.Ascending },
            // Deliberately only one entry - shorter than `orders`.
        };
        var list = MakeSmartList(orders, sortOptions, collectionSearchDepth: 2);

        var result = list.WrapOrdersWithChildAggregation(orders, null);

        Assert.Equal(2, result.Count);
        Assert.IsType<ChildAggregatingOrder>(result[0]); // index 0 has a matching SortOption -> wrapped.
        Assert.Same(secondDate, result[1]); // index 1 has none -> left unwrapped despite being a supported field.
    }

    // ---------------------------------------------------------------------------------
    // IsDescendingOrder
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false)] // wrapper says descending; inner order's own type is ascending.
    [InlineData(false, true)] // wrapper says ascending; inner order's own type is descending.
    public void IsDescendingOrder_ForChildAggregatingOrder_ReadsTheWrapperFlag_NotTheInnerOrderType(bool wrapperIsDescending, bool innerOrderIsDescType)
    {
        Order inner = innerOrderIsDescType ? new NameOrderDesc() : new NameOrder();
        var wrapped = new ChildAggregatingOrder(inner, wrapperIsDescending, "DateCreated");

        Assert.Equal(wrapperIsDescending, SmartList.IsDescendingOrder(wrapped));
    }

    /// <summary>
    /// Mirrors OrderFactoryTests.RegisteredSortNames (Core/Orders/OrderFactoryTests.cs) - the full
    /// set of sort names OrderFactory.CreateOrder actually resolves. Kept as a second, independent
    /// hardcoded copy rather than shared, on purpose: this task explicitly rules out reflecting into
    /// OrderFactory's private OrderMap. Keep the two lists in sync - a name added to one without the
    /// other means a newly-registered descending order could join SmartList.IsDescendingOrder's
    /// hand-maintained `order is XDesc ||` chain and never be swept by this exhaustive check.
    /// </summary>
    private static readonly string[] RegisteredSortNames =
    [
        "Name Ascending",
        "Name Descending",
        "Name (Ignore Articles) Ascending",
        "Name (Ignore Articles) Descending",
        "ProductionYear Ascending",
        "ProductionYear Descending",
        "DateCreated Ascending",
        "DateCreated Descending",
        "Similarity Ascending",
        "Similarity Descending",
        "ReleaseDate Ascending",
        "ReleaseDate Descending",
        "CommunityRating Ascending",
        "CommunityRating Descending",
        "PlayCount (owner) Ascending",
        "PlayCount (owner) Descending",
        "PlayCount (all users) Ascending",
        "PlayCount (all users) Descending",
        "LastPlayed (owner) Ascending",
        "LastPlayed (owner) Descending",
        "LastPlayed (all users) Ascending",
        "LastPlayed (all users) Descending",
        "Runtime Ascending",
        "Runtime Descending",
        "Resolution Ascending",
        "Resolution Descending",
        "SeriesName Ascending",
        "SeriesName Descending",
        "SeriesName (Ignore Articles) Ascending",
        "SeriesName (Ignore Articles) Descending",
        "AlbumName Ascending",
        "AlbumName Descending",
        "Artist Ascending",
        "Artist Descending",
        "TrackNumber Ascending",
        "TrackNumber Descending",
        "SeasonNumber Ascending",
        "SeasonNumber Descending",
        "EpisodeNumber Ascending",
        "EpisodeNumber Descending",
        "Random",
        "Rule Block Order Ascending",
        "Rule Block Order Descending",
        "External List Order Ascending",
        "External List Order Descending",
        "LastEpisodeAirDate Ascending",
        "LastEpisodeAirDate Descending",
        "Round Robin Ascending",
        "Round Robin Descending",
        "Random Round Robin",
        "Shuffled Round Robin",
        "Least Recently Watched Round Robin",
        "NoOrder",
    ];

    public static TheoryData<string> AllRegisteredSortNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in RegisteredSortNames)
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>
    /// SmartList.IsDescendingOrder is a hand-maintained ~20-way `order is XDesc ||` chain. Adding a
    /// new *Desc class and forgetting to extend that chain compiles fine and silently sorts the new
    /// descending option ascending in every multi-sort. This sweeps the whole real registry (every
    /// name OrderFactory.CreateOrder actually resolves), so a representative sample is a strict
    /// subset of what this proves; a fully-exhaustive sweep sourced live from OrderFactory's private
    /// OrderMap would need reflection, which this file was told not to use, so the source-of-truth
    /// list above is a hand-maintained mirror instead (see its doc comment).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllRegisteredSortNames))]
    public void IsDescendingOrder_MatchesTheDescendingSuffix_ForEveryRegisteredSortName(string sortName)
    {
        var order = OrderFactory.CreateOrder(sortName);

        // Round Robin Descending is the one exception: its positions already hold the Z-A
        // group order, so they are always sorted ascending.
        var expected = sortName.EndsWith(" Descending", StringComparison.Ordinal)
            && sortName != "Round Robin Descending";

        Assert.Equal(expected, SmartList.IsDescendingOrder(order));
    }

    // ---------------------------------------------------------------------------------
    // PrepareRoundRobinPositions - sorts listed AFTER Least Recently Watched Round Robin
    //
    // The ranking itself is covered in RoundRobinLeastRecentlyWatchedTests; these drive the
    // real wiring: which orders count as "later", in what state they reach the ranking, and
    // that every pass rebuilds it.
    // ---------------------------------------------------------------------------------

    private static string[] PrepareAndSort(SmartList list, List<BaseItem> items, RefreshQueueService.RefreshCache? cache = null)
    {
        cache ??= new RefreshQueueService.RefreshCache();
        list.PrepareRoundRobinPositions(items, TestItems.User, null, null, cache);
        return Names(list.ApplyMultipleOrders(items, TestItems.User, null, null, cache));
    }

    /// <summary>Three never-watched shows whose alphabetical order differs from their premiere order.</summary>
    private static List<BaseItem> ThreeNeverWatchedShows() =>
    [
        TestItems.Mov("Alpha", aired: new DateTime(2020, 1, 1)),
        TestItems.Mov("Bravo", aired: new DateTime(2010, 1, 1)),
        TestItems.Mov("Charlie", aired: new DateTime(2015, 1, 1)),
    ];

    [Fact]
    public void PrepareRoundRobinPositions_LeastRecentlyWatchedThenReleaseDate_StartsTheRotationInPremiereOrder()
    {
        var items = new List<BaseItem>
        {
            TestItems.Ep("Alpha", 1, 1, aired: new DateTime(2020, 1, 1)),
            TestItems.Ep("Alpha", 1, 2, aired: new DateTime(2020, 1, 8)),
            TestItems.Ep("Bravo", 1, 1, aired: new DateTime(2010, 1, 1)),
            TestItems.Ep("Bravo", 1, 2, aired: new DateTime(2010, 1, 8)),
            TestItems.Ep("Charlie", 1, 1, aired: new DateTime(2015, 1, 1)),
            TestItems.Ep("Charlie", 1, 2, aired: new DateTime(2015, 1, 8)),
        };
        var list = MakeSmartList([new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" }, new ReleaseDateOrder()]);

        var result = PrepareAndSort(list, items);

        Assert.Equal(
            ["Bravo S01E01", "Charlie S01E01", "Alpha S01E01", "Bravo S01E02", "Charlie S01E02", "Alpha S01E02"],
            result);
    }

    [Fact]
    public void PrepareRoundRobinPositions_LaterDescendingSort_RanksTheGroupsDescending()
    {
        var list = MakeSmartList([new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" }, new ReleaseDateOrderDesc()]);

        Assert.Equal(["Alpha", "Charlie", "Bravo"], PrepareAndSort(list, ThreeNeverWatchedShows()));
    }

    /// <summary>
    /// The compatibility guarantee: with no sort listed after it, nothing is ranked and the
    /// order is exactly the alphabetical tie-break it has always been.
    /// </summary>
    [Fact]
    public void PrepareRoundRobinPositions_LeastRecentlyWatchedAlone_KeepsTheAlphabeticalTieBreak()
    {
        var roundRobin = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" };
        var list = MakeSmartList([roundRobin]);

        var result = PrepareAndSort(list, ThreeNeverWatchedShows());

        Assert.Null(roundRobin.GroupTieBreakRanks);
        Assert.Equal(["Alpha", "Bravo", "Charlie"], result);
    }

    /// <summary>Only sorts listed AFTER the round robin rank its groups - one before it never does.</summary>
    [Fact]
    public void PrepareRoundRobinPositions_SortListedBeforeTheRoundRobin_IsNotATieBreak()
    {
        var roundRobin = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" };
        var list = MakeSmartList([new ReleaseDateOrder(), roundRobin]);

        list.PrepareRoundRobinPositions(ThreeNeverWatchedShows(), TestItems.User, null, null, new RefreshQueueService.RefreshCache());

        Assert.Null(roundRobin.GroupTieBreakRanks);
    }

    /// <summary>
    /// Per-group limits run this once per rule group before the final pass over the whole list,
    /// so each pass must rank the items it was given and leave nothing behind for the next.
    /// </summary>
    [Fact]
    public void PrepareRoundRobinPositions_RanksAreRebuiltForEveryPass()
    {
        var all = ThreeNeverWatchedShows();
        var subset = new List<BaseItem> { all[0], all[2] };
        var list = MakeSmartList([new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" }, new ReleaseDateOrder()]);

        Assert.Equal(["Charlie", "Alpha"], PrepareAndSort(list, subset));
        Assert.Equal(["Bravo", "Charlie", "Alpha"], PrepareAndSort(list, all));
    }

    /// <summary>
    /// The later sorts must be wrapped for child aggregation BEFORE they are sliced off the list:
    /// WrapOrdersWithChildAggregation pairs Orders with SortOptions by index, so wrapping the
    /// slice would pair the Production Year order with the round robin's sort option and leave
    /// it unwrapped. Unwrapped, both collections have no year of their own, tie, and fall back to
    /// alphabetical (Alpha first); wrapped, they rank by their children's year (Bravo first).
    /// </summary>
    [Fact]
    public void PrepareRoundRobinPositions_LaterSort_KeepsItsChildAggregation()
    {
        var alpha = new BoxSet { Id = Guid.NewGuid(), Name = "Alpha" };
        var bravo = new BoxSet { Id = Guid.NewGuid(), Name = "Bravo" };
        var cache = new RefreshQueueService.RefreshCache();
        cache.CollectionChildItems[alpha.Id] = [new Movie { Id = Guid.NewGuid(), Name = "Alpha Movie", ProductionYear = 2020 }];
        cache.CollectionChildItems[bravo.Id] = [new Movie { Id = Guid.NewGuid(), Name = "Bravo Movie", ProductionYear = 2010 }];

        var list = MakeSmartList(
            [new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" }, new ProductionYearOrder()],
            [
                new() { SortBy = "Least Recently Watched Round Robin", SortOrder = SortOrder.Ascending },
                new() { SortBy = "ProductionYear", SortOrder = SortOrder.Ascending },
            ],
            collectionSearchDepth: 1);

        Assert.Equal(["Bravo", "Alpha"], PrepareAndSort(list, [alpha, bravo], cache));
    }

    /// <summary>
    /// A second round robin listed after the first is a legal (if odd) later sort, and its sort
    /// key IS its pre-computed position - so its positions must exist before the first one ranks
    /// its groups. Processing the orders in list order would rank against empty positions, tie
    /// every group and fall back to alphabetical (Alpha first). The later one groups by genre,
    /// and the genres run opposite to the names, so ready positions put Charlie first.
    /// </summary>
    [Fact]
    public void PrepareRoundRobinPositions_LaterRoundRobin_HasItsPositionsReadyBeforeItIsUsedAsTieBreak()
    {
        var items = new List<BaseItem>
        {
            TestItems.Mov("Alpha", genres: ["Western"]),
            TestItems.Mov("Bravo", genres: ["Drama"]),
            TestItems.Mov("Charlie", genres: ["Action"]),
        };
        var list = MakeSmartList(
        [
            new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" },
            new RoundRobinOrder { GroupByField = "Genres" },
        ]);

        Assert.Equal(["Charlie", "Bravo", "Alpha"], PrepareAndSort(list, items));
    }

    /// <summary>
    /// Round Robin Descending already puts its groups Z-A when it computes positions. Sorting
    /// those positions descending as well reversed the whole playlist - last episodes first -
    /// as soon as any second sort moved it onto the multi-sort path.
    /// </summary>
    [Fact]
    public void RoundRobinDescending_WithALaterSort_PlaysTheSameOrderAsOnItsOwn()
    {
        var items = new List<BaseItem>
        {
            TestItems.Mov("Action 1", genres: ["Action"]),
            TestItems.Mov("Action 2", genres: ["Action"]),
            TestItems.Mov("Drama 1", genres: ["Drama"]),
            TestItems.Mov("Drama 2", genres: ["Drama"]),
        };
        string[] expected = ["Drama 1", "Action 1", "Drama 2", "Action 2"];

        var alone = MakeSmartList([new RoundRobinOrderDesc { GroupByField = "Genres" }]);
        var withLaterSort = MakeSmartList([new RoundRobinOrderDesc { GroupByField = "Genres" }, new NameOrder()]);

        Assert.Equal(expected, PrepareAndSort(alone, items));
        Assert.Equal(expected, PrepareAndSort(withLaterSort, items));
    }
}
