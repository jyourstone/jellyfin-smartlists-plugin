using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Orders;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.Orders;

/// <summary>
/// Covers the "plumbing" half of Core/Orders: the two orders with no sort field of their own
/// (<see cref="NoOrder"/>, <see cref="RandomOrder"/>) plus the two shared mechanisms every other
/// order is built on (<see cref="PropertyOrder{T}"/> and <c>ComparableTuple4</c>).
///
/// Notes that shaped these tests:
/// - Every item gets an explicit Id. BaseItem.Id defaults to Guid.Empty, and RandomOrder keys its
///   pre-generated random keys by Id, so items without distinct Ids silently share a sort key.
/// - Every item gets an explicit SortName. Reading BaseItem.SortName when it was never set throws
///   NullReferenceException outside a running server; none of the code under test reads it today,
///   but a sort that started reading it would otherwise fail as a confusing
///   InvalidOperationException("Failed to compare two elements") instead of a clear assert.
/// - RandomOrder.OrderBy seeds itself from DateTime.Now.Ticks, so no test here asserts a specific
///   shuffled order. Only GetSortKey is deterministic, and that is where the order assertions live.
/// - ComparableTuple4 and ICompositeSortKey are internal; the plugin csproj has
///   InternalsVisibleTo("Jellyfin.Plugin.SmartLists.Tests"), so they are used directly here.
/// </summary>
public class RandomAndNoOrderTests
{
    // ---------------------------------------------------------------------------------
    // Item builders
    // ---------------------------------------------------------------------------------

    private static Movie Item(string name) =>
        new() { Id = Guid.NewGuid(), Name = name, SortName = name };

    private static Movie ItemWithAlbum(string name, string album) =>
        new() { Id = Guid.NewGuid(), Name = name, SortName = name, Album = album };

    private static Movie ItemWithYear(string name, int year) =>
        new() { Id = Guid.NewGuid(), Name = name, SortName = name, ProductionYear = year };

    private static List<string> Names(IEnumerable<BaseItem> items) =>
        items.Select(i => i.Name).ToList();

    // ---------------------------------------------------------------------------------
    // NoOrder - the contract is that it does NOT reorder. It inherits every member from the
    // abstract Order base, so these also pin Order's default implementations.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Name_ForNoOrder_IsNoOrder()
    {
        Assert.Equal("NoOrder", new NoOrder().Name);
    }

    [Fact]
    public void NoOrder_OrderBy_PreservesInputOrderExactly()
    {
        // Deliberately in an order no comparer would produce: not alphabetical either way.
        var items = new List<BaseItem> { Item("Zebra"), Item("Alpha"), Item("Middle") };

        var result = new NoOrder().OrderBy(items).ToList();

        Assert.Equal(new[] { "Zebra", "Alpha", "Middle" }, Names(result));
        // Same instances, not copies - callers rely on identity downstream.
        Assert.Same(items[0], result[0]);
        Assert.Same(items[1], result[1]);
        Assert.Same(items[2], result[2]);
    }

    [Fact]
    public void NoOrder_OrderByWithUserOverload_PreservesInputOrderExactly()
    {
        // Order's 5-arg overload delegates to the 1-arg one; the user/manager/logger/cache
        // arguments must not change the outcome.
        var items = new List<BaseItem> { Item("Zebra"), Item("Alpha"), Item("Middle") };
        var user = new User("tester", "authProviderId", "pwResetProviderId");

        var result = new NoOrder().OrderBy(items, user, null, null, new RefreshQueueService.RefreshCache()).ToList();

        Assert.Equal(new[] { "Zebra", "Alpha", "Middle" }, Names(result));
    }

    [Fact]
    public void NoOrder_OrderBy_NullItems_ReturnsEmptyInsteadOfThrowing()
    {
        Assert.Empty(new NoOrder().OrderBy(null!));
        Assert.Empty(new NoOrder().OrderBy(null!, new User("t", "a", "p"), null, null));
    }

    [Theory]
    [InlineData("The Matrix", "The Matrix")]
    [InlineData("", "")]
    [InlineData(null, "")] // null Name collapses to "" so multi-sort never compares against null
    public void NoOrder_GetSortKey_FallsBackToItemName(string? name, string expected)
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = name!, SortName = "sort name is ignored" };

        var key = new NoOrder().GetSortKey(item, new User("t", "a", "p"), null, null);

        Assert.Equal(expected, Assert.IsType<string>(key));
    }

    // ---------------------------------------------------------------------------------
    // RandomOrder.GetSortKey - the deterministic half. ApplySortingCore pre-generates one
    // random int per item Id and hands the dictionary to every order, so the arrangement is a
    // pure function of that dictionary.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void Name_ForRandomOrder_IsRandom()
    {
        Assert.Equal("Random", new RandomOrder().Name);
    }

    [Fact]
    public void RandomOrder_GetSortKey_ReturnsThePreGeneratedKeyForThatItemId()
    {
        var order = new RandomOrder();
        var a = Item("A");
        var b = Item("B");
        var keys = new Dictionary<Guid, int> { [a.Id] = 30, [b.Id] = 10 };

        Assert.Equal(30, Assert.IsType<int>(order.GetSortKey(a, null!, null, null, keys)));
        Assert.Equal(10, Assert.IsType<int>(order.GetSortKey(b, null!, null, null, keys)));
    }

    [Fact]
    public void RandomOrder_GetSortKey_KeysByIdNotByInstance()
    {
        // ApplySortingCore builds the dictionary from item.Id, so a different instance carrying
        // the same Id must resolve to the same key - otherwise a re-fetched item would jump.
        var order = new RandomOrder();
        var id = Guid.NewGuid();
        var first = new Movie { Id = id, Name = "First", SortName = "First" };
        var second = new Movie { Id = id, Name = "Second", SortName = "Second" };
        var keys = new Dictionary<Guid, int> { [id] = 77 };

        Assert.Equal(77, Assert.IsType<int>(order.GetSortKey(first, null!, null, null, keys)));
        Assert.Equal(77, Assert.IsType<int>(order.GetSortKey(second, null!, null, null, keys)));
    }

    [Fact]
    public void RandomOrder_GetSortKey_WithoutAPreGeneratedKey_FallsBackToIdHashCode()
    {
        var order = new RandomOrder();
        var mapped = Item("Mapped");
        var unmapped = Item("Unmapped");
        var keys = new Dictionary<Guid, int> { [mapped.Id] = 111 };

        // Dictionary supplied but this item is absent from it.
        Assert.Equal(unmapped.Id.GetHashCode(), Assert.IsType<int>(order.GetSortKey(unmapped, null!, null, null, keys)));
        // No dictionary at all (the single-sort path).
        Assert.Equal(unmapped.Id.GetHashCode(), Assert.IsType<int>(order.GetSortKey(unmapped, null!, null, null, null)));
        // The mapped item still gets its key, so the fallback is per-item, not all-or-nothing.
        Assert.Equal(111, Assert.IsType<int>(order.GetSortKey(mapped, null!, null, null, keys)));
    }

    [Fact]
    public void RandomOrder_SortingByGetSortKey_IsDeterministicForAGivenKeySet()
    {
        var order = new RandomOrder();
        var a = Item("A");
        var b = Item("B");
        var c = Item("C");
        var items = new List<BaseItem> { a, b, c };
        var keys = new Dictionary<Guid, int> { [a.Id] = 30, [b.Id] = 10, [c.Id] = 20 };

        var first = SortByRandomKey(order, items, keys);
        var second = SortByRandomKey(order, items, keys);

        Assert.Equal(new[] { "B", "C", "A" }, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void RandomOrder_SortingByGetSortKey_WithDifferentKeys_ProducesADifferentArrangement()
    {
        var order = new RandomOrder();
        var a = Item("A");
        var b = Item("B");
        var c = Item("C");
        var items = new List<BaseItem> { a, b, c };

        var arrangedOne = SortByRandomKey(order, items, new Dictionary<Guid, int> { [a.Id] = 30, [b.Id] = 10, [c.Id] = 20 });
        var arrangedTwo = SortByRandomKey(order, items, new Dictionary<Guid, int> { [a.Id] = 1, [b.Id] = 3, [c.Id] = 2 });

        Assert.Equal(new[] { "B", "C", "A" }, arrangedOne);
        Assert.Equal(new[] { "A", "C", "B" }, arrangedTwo);
    }

    private static List<string> SortByRandomKey(RandomOrder order, IReadOnlyList<BaseItem> items, Dictionary<Guid, int> keys) =>
        items.OrderBy(i => order.GetSortKey(i, null!, null, null, keys)).Select(i => i.Name).ToList();

    // ---------------------------------------------------------------------------------
    // RandomOrder.OrderBy - unseeded, so only the invariants that must hold for EVERY shuffle
    // are asserted: nothing is dropped, nothing is duplicated, and it really does shuffle.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void RandomOrder_OrderBy_ReturnsEveryInputItemExactlyOnce()
    {
        var items = Enumerable.Range(0, 25).Select(i => (BaseItem)Item("Item " + i.ToString("00"))).ToList();

        var result = new RandomOrder().OrderBy(items).ToList();

        Assert.Equal(items.Count, result.Count);
        // Names are unique, so comparing the sorted name lists catches both drops and duplicates.
        Assert.Equal(
            items.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal),
            result.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal));
        // And they are the original instances, not reconstructed items.
        var originals = new HashSet<BaseItem>(items, ReferenceEqualityComparer.Instance as IEqualityComparer<BaseItem>);
        Assert.All(result, item => Assert.Contains(item, originals));
    }

    [Fact]
    public void RandomOrder_OrderBy_ActuallyShuffles_RatherThanReturningInputOrder()
    {
        // 25 distinct items: the odds of a genuine shuffle reproducing the input order even once
        // are ~1/25!, so a run where none of the attempts reorder means OrderBy is a passthrough.
        var items = Enumerable.Range(0, 25).Select(i => (BaseItem)Item("Item " + i.ToString("00"))).ToList();
        var inputNames = Names(items);
        var order = new RandomOrder();

        var sawReordering = false;
        for (var attempt = 0; attempt < 5 && !sawReordering; attempt++)
        {
            var resultNames = Names(order.OrderBy(items));
            Assert.Equal(inputNames.Count, resultNames.Count);
            sawReordering = !resultNames.SequenceEqual(inputNames, StringComparer.Ordinal);
        }

        Assert.True(sawReordering, "RandomOrder.OrderBy returned the input order on every attempt - it is not shuffling.");
    }

    [Fact]
    public void RandomOrder_OrderByWithUserOverload_StillShufflesTheSameItems()
    {
        var items = Enumerable.Range(0, 25).Select(i => (BaseItem)Item("Item " + i.ToString("00"))).ToList();
        var user = new User("tester", "authProviderId", "pwResetProviderId");

        var result = new RandomOrder().OrderBy(items, user, null, null, new RefreshQueueService.RefreshCache()).ToList();

        Assert.Equal(
            items.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal),
            result.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void RandomOrder_OrderBy_EmptyOrNullOrSingle_IsHandledWithoutThrowing()
    {
        var order = new RandomOrder();
        var single = Item("Only");

        Assert.Empty(order.OrderBy(null!));
        Assert.Empty(order.OrderBy(new List<BaseItem>()));
        Assert.Equal(new[] { "Only" }, Names(order.OrderBy(new List<BaseItem> { single })));
    }

    // ---------------------------------------------------------------------------------
    // PropertyOrder<T> - the base 20 of the concrete orders derive from. The contract is that
    // OrderBy, the user-aware OrderBy and GetSortKey all route through the single GetSortValue.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void PropertyOrder_OrderBy_SortsByGetSortValue_AndIsDescendingReversesIt()
    {
        var items = new List<BaseItem> { ItemWithYear("Mid", 2000), ItemWithYear("Old", 1980), ItemWithYear("New", 2020) };

        Assert.Equal(new[] { "Old", "Mid", "New" }, Names(new YearProbeOrder(descending: false).OrderBy(items)));
        Assert.Equal(new[] { "New", "Mid", "Old" }, Names(new YearProbeOrder(descending: true).OrderBy(items)));
    }

    [Fact]
    public void PropertyOrder_OrderBy_NullItems_ReturnsEmptyOnBothOverloads()
    {
        var order = new YearProbeOrder(descending: false);

        Assert.Empty(order.OrderBy(null!));
        Assert.Empty(order.OrderBy(null!, new User("t", "a", "p"), null, null));
    }

    [Fact]
    public void PropertyOrder_OrderBy_SimpleOverload_CallsGetSortValueWithAllContextNull()
    {
        var order = new YearProbeOrder(descending: false);

        // Two items on purpose: LINQ's OrderBy skips key computation entirely for a
        // single-element sequence, so a one-item list records no calls at all.
        order.OrderBy(new List<BaseItem> { ItemWithYear("A", 2000), ItemWithYear("B", 1990) }).ToList();

        Assert.Equal(2, order.Calls.Count);
        Assert.All(order.Calls, call =>
        {
            Assert.Null(call.User);
            Assert.Null(call.UserDataManager);
            Assert.Null(call.Logger);
            Assert.Null(call.RefreshCache);
        });
    }

    [Fact]
    public void PropertyOrder_OrderBy_UserOverload_ForwardsUserAndRefreshCacheToGetSortValue()
    {
        var order = new YearProbeOrder(descending: false);
        var user = new User("tester", "authProviderId", "pwResetProviderId");
        var cache = new RefreshQueueService.RefreshCache();

        order.OrderBy(new List<BaseItem> { ItemWithYear("A", 2000), ItemWithYear("B", 1990) }, user, null, null, cache).ToList();

        Assert.Equal(2, order.Calls.Count);
        Assert.All(order.Calls, call =>
        {
            Assert.Same(user, call.User);
            Assert.Same(cache, call.RefreshCache);
        });
    }

    [Fact]
    public void PropertyOrder_GetSortKey_ReturnsTheSameValueTheSortUses_AndForwardsTheCache()
    {
        var order = new YearProbeOrder(descending: false);
        var user = new User("tester", "authProviderId", "pwResetProviderId");
        var cache = new RefreshQueueService.RefreshCache();
        var item = ItemWithYear("A", 1999);

        var key = order.GetSortKey(item, user, null, null, itemRandomKeys: null, refreshCache: cache);

        Assert.Equal(1999, Assert.IsType<int>(key));
        var call = Assert.Single(order.Calls);
        Assert.Same(user, call.User);
        Assert.Same(cache, call.RefreshCache);
    }

    [Fact]
    public void PropertyOrder_GetSortKey_IsIdenticalForAscendingAndDescending()
    {
        // Direction is applied by ApplySortingCore (OrderBy vs OrderByDescending), never by
        // negating the key. A *Desc order that flipped its own key would double-invert.
        var item = ItemWithYear("A", 1999);
        var user = new User("tester", "authProviderId", "pwResetProviderId");

        var ascending = new YearProbeOrder(descending: false).GetSortKey(item, user, null, null);
        var descending = new YearProbeOrder(descending: true).GetSortKey(item, user, null, null);

        Assert.Equal(ascending, descending);
        Assert.Equal(1999, Assert.IsType<int>(descending));
    }

    [Fact]
    public void PropertyOrder_OrderBy_HonoursAnOverriddenComparer()
    {
        // The natural comparer compares leading numbers numerically, so 2 sorts before 10.
        // Comparer<string>.Default would produce the opposite.
        var items = new List<BaseItem> { ItemWithAlbum("Ten", "10 Albums"), ItemWithAlbum("Two", "2 Albums") };

        Assert.Equal(new[] { "Two", "Ten" }, Names(new NaturalAlbumProbeOrder().OrderBy(items)));
        Assert.Equal(new[] { "Ten", "Two" }, Names(new PlainAlbumProbeOrder().OrderBy(items)));
    }

    [Fact]
    public void PropertyOrder_GetSortKey_CarriesAnOverriddenComparer_ButLeavesDefaultComparerValuesBare()
    {
        // Pins what GetSortKey hands back in each of the two shapes. An order that left
        // Comparer at its default returns the unwrapped value, so ApplySortingCore can still
        // see through it (its `key is DateTime` day-precision branch depends on that). An
        // order that overrode Comparer returns a key carrying the comparer, because multi-sort
        // compares keys with no comparer of its own - see the ordering test below.
        var item = ItemWithAlbum("Two", "2 Albums");
        var user = new User("t", "a", "p");

        var bare = new PlainAlbumProbeOrder().GetSortKey(item, user, null, null);
        Assert.Equal("2 Albums", Assert.IsType<string>(bare));

        var wrapped = Assert.IsType<ComparerBackedKey<string>>(
            new NaturalAlbumProbeOrder().GetSortKey(item, user, null, null));
        Assert.Equal("2 Albums", wrapped.Value);
    }

    [Fact]
    public void PropertyOrder_GetSortKeyOrdering_MatchesOrderByOrdering_ForANaturalComparerOrder()
    {
        // Single-sort goes through Order.OrderBy, which applies the overridden Comparer.
        // Multi-sort goes through SmartList.ApplySortingCore, which does
        // itemsWithKeys.OrderBy(x => x.SortKeys[i]) with no comparer - i.e. Comparer<IComparable>
        // .Default, which for a string is culture-sensitive String.CompareTo. The overridden
        // natural comparer never runs, so "10 Albums" sorts before "2 Albums" in multi-sort and
        // after it in single-sort. TrackNumberOrder carries an explicit in-code "FIX: pass
        // SharedNaturalComparer ... to match OrderBy behavior" for exactly this failure mode.
        var items = new List<BaseItem> { ItemWithAlbum("Ten", "10 Albums"), ItemWithAlbum("Two", "2 Albums") };
        var order = new NaturalAlbumProbeOrder();
        var user = new User("tester", "authProviderId", "pwResetProviderId");

        var singleSort = Names(order.OrderBy(items));
        var multiSort = items.OrderBy(i => order.GetSortKey(i, user, null, null)).Select(i => i.Name).ToList();

        Assert.Equal(new[] { "Two", "Ten" }, singleSort);
        Assert.Equal(singleSort, multiSort);
    }

    // ---------------------------------------------------------------------------------
    // ComparableTuple4 - the composite key type behind ReleaseDate/TrackNumber/EpisodeNumber.
    // ---------------------------------------------------------------------------------

    [Theory]
    // item1 differs -> it decides, even though every later level points the other way.
    [InlineData(1, 9, 9, 9, 2, 0, 0, 0, -1)]
    [InlineData(2, 0, 0, 0, 1, 9, 9, 9, 1)]
    // item1 tied -> item2 decides.
    [InlineData(1, 1, 9, 9, 1, 2, 0, 0, -1)]
    // items 1-2 tied -> item3 decides.
    [InlineData(1, 1, 1, 9, 1, 1, 2, 0, -1)]
    // items 1-3 tied -> item4 decides.
    [InlineData(1, 1, 1, 1, 1, 1, 1, 2, -1)]
    // everything tied.
    [InlineData(1, 2, 3, 4, 1, 2, 3, 4, 0)]
    public void ComparableTuple4_CompareTo_ComparesLevelsInOrder_ShortCircuitingAtTheFirstDifference(
        int a1, int a2, int a3, int a4, int b1, int b2, int b3, int b4, int expectedSign)
    {
        var a = new ComparableTuple4<int, int, int, int>(a1, a2, a3, a4);
        var b = new ComparableTuple4<int, int, int, int>(b1, b2, b3, b4);

        Assert.Equal(expectedSign, Math.Sign(a.CompareTo(b)));
    }

    [Fact]
    public void ComparableTuple4_Sorting_ProducesFullMultiLevelOrdering()
    {
        var tuples = new List<ComparableTuple4<int, int, int, int>>
        {
            new(2, 1, 0, 0),
            new(1, 2, 0, 0),
            new(1, 1, 5, 0),
            new(1, 1, 0, 9),
            new(1, 1, 0, 1),
        };

        var sorted = tuples.OrderBy(t => t).Select(t => Assert.IsType<int>(t.PrimaryValue)).ToList();

        // Only the primary values are readable from outside, so verify the full ordering by
        // rebuilding the expected sequence and comparing element-by-element.
        Assert.Equal(new[] { 1, 1, 1, 1, 2 }, sorted);
        var ordered = tuples.OrderBy(t => t).ToList();
        Assert.Same(tuples[3], ordered[1]);  // (1,1,0,1) then (1,1,0,9)
        Assert.Same(tuples[4], ordered[0]);
        Assert.Same(tuples[2], ordered[2]);  // (1,1,5,0)
        Assert.Same(tuples[1], ordered[3]);  // (1,2,0,0)
        Assert.Same(tuples[0], ordered[4]);  // (2,1,0,0)
    }

    [Fact]
    public void ComparableTuple4_CompareTo_Null_ReturnsOne()
    {
        // Sorts nulls first, matching Comparer<T>.Default.
        var tuple = new ComparableTuple4<int, int, int, int>(1, 2, 3, 4);

        Assert.Equal(1, tuple.CompareTo(null));
    }

    [Fact]
    public void ComparableTuple4_CompareTo_WrongType_ThrowsArgumentException()
    {
        var tuple = new ComparableTuple4<int, int, string, string>(1, 2, "a", "b");

        Assert.Throws<ArgumentException>(() => tuple.CompareTo("not a tuple"));
        // Even another ComparableTuple4 is rejected when its type arguments differ.
        Assert.Throws<ArgumentException>(() => tuple.CompareTo(new ComparableTuple4<int, int, int, int>(1, 2, 3, 4)));
    }

    [Fact]
    public void ComparableTuple4_CompareTo_UsesThePerSlotComparerItWasGiven()
    {
        // Slot 3 carries the natural comparer (as EpisodeNumberOrder and TrackNumberOrder do),
        // so "2 - Song" precedes "10 - Song"; with the default comparer the order flips.
        var natural2 = new ComparableTuple4<int, int, string, string>(1, 1, "2 - Song", "", comparer3: OrderUtilities.SharedNaturalComparer);
        var natural10 = new ComparableTuple4<int, int, string, string>(1, 1, "10 - Song", "", comparer3: OrderUtilities.SharedNaturalComparer);
        Assert.True(natural2.CompareTo(natural10) < 0);

        var plain2 = new ComparableTuple4<int, int, string, string>(1, 1, "2 - Song", "");
        var plain10 = new ComparableTuple4<int, int, string, string>(1, 1, "10 - Song", "");
        Assert.True(plain2.CompareTo(plain10) > 0);
    }

    [Fact]
    public void ComparableTuple4_CompareTo_NaturalComparer_OnlyHandlesLeadingNumbers_NotEmbeddedOnes()
    {
        // NaturalStringComparer.ExtractLeadingNumber only looks at the start of the string, so
        // "Track 2"/"Track 10" get a plain ordinal-ignore-case comparison and "Track 10" wins.
        // Pinned deliberately: it is easy to assume the comparer is a full natural sort.
        var natural2 = new ComparableTuple4<int, int, string, string>(1, 1, "Track 2", "", comparer3: OrderUtilities.SharedNaturalComparer);
        var natural10 = new ComparableTuple4<int, int, string, string>(1, 1, "Track 10", "", comparer3: OrderUtilities.SharedNaturalComparer);

        Assert.True(natural2.CompareTo(natural10) > 0);
    }

    [Fact]
    public void ComparableTuple4_CompareTo_NullSlotValue_SortsBeforeANonNullOne()
    {
        var withNull = new ComparableTuple4<int, int, string, string>(1, 1, null!, "");
        var withValue = new ComparableTuple4<int, int, string, string>(1, 1, "anything", "");

        Assert.True(withNull.CompareTo(withValue) < 0);
        Assert.True(withValue.CompareTo(withNull) > 0);
        Assert.Equal(0, withNull.CompareTo(new ComparableTuple4<int, int, string, string>(1, 1, null!, "")));
    }

    [Fact]
    public void ComparableTuple4_PrimaryValue_ReturnsItem1_DiscardingTheEmbeddedTiebreakers()
    {
        // This is what ApplySortingCore strips a non-final sort down to: two keys that differ
        // only in their tiebreakers must compare EQUAL once reduced to PrimaryValue, so the
        // user's secondary sort gets to decide.
        var first = new ComparableTuple4<int, int, string, string>(1999, 3, "Zeta", "");
        var second = new ComparableTuple4<int, int, string, string>(1999, 7, "Alpha", "");

        Assert.IsAssignableFrom<ICompositeSortKey>(first);
        Assert.Equal(1999, Assert.IsType<int>(first.PrimaryValue));
        Assert.NotEqual(0, first.CompareTo(second));
        Assert.Equal(0, first.PrimaryValue.CompareTo(second.PrimaryValue));
    }

    // ---------------------------------------------------------------------------------
    // Probe subclasses. Nested and private so they cannot collide with helpers other test
    // files in this namespace define.
    // ---------------------------------------------------------------------------------

    private sealed class YearProbeOrder : PropertyOrder<int>
    {
        private readonly bool _descending;

        public YearProbeOrder(bool descending)
        {
            _descending = descending;
        }

        public List<(User? User, IUserDataManager? UserDataManager, ILogger? Logger, RefreshQueueService.RefreshCache? RefreshCache)> Calls { get; } = [];

        public override string Name => _descending ? "YearProbe Descending" : "YearProbe Ascending";

        protected override bool IsDescending => _descending;

        protected override int GetSortValue(
            BaseItem item,
            User? user = null,
            IUserDataManager? userDataManager = null,
            ILogger? logger = null,
            RefreshQueueService.RefreshCache? refreshCache = null)
        {
            Calls.Add((user, userDataManager, logger, refreshCache));
            return item.ProductionYear ?? 0;
        }
    }

    /// <summary>Mirrors AlbumNameOrder: a string property order with the natural comparer.</summary>
    private sealed class NaturalAlbumProbeOrder : PropertyOrder<string>
    {
        public override string Name => "AlbumProbe Ascending";

        protected override bool IsDescending => false;

        protected override IComparer<string> Comparer => OrderUtilities.SharedNaturalComparer;

        protected override string GetSortValue(
            BaseItem item,
            User? user = null,
            IUserDataManager? userDataManager = null,
            ILogger? logger = null,
            RefreshQueueService.RefreshCache? refreshCache = null) => item.Album ?? "";
    }

    /// <summary>Same as above but leaving Comparer at its default, to show the default is used.</summary>
    private sealed class PlainAlbumProbeOrder : PropertyOrder<string>
    {
        public override string Name => "PlainAlbumProbe Ascending";

        protected override bool IsDescending => false;

        protected override string GetSortValue(
            BaseItem item,
            User? user = null,
            IUserDataManager? userDataManager = null,
            ILogger? logger = null,
            RefreshQueueService.RefreshCache? refreshCache = null) => item.Album ?? "";
    }
}
