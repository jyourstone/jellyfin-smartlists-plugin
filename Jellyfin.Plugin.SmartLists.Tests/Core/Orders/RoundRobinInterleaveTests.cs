using Jellyfin.Plugin.SmartLists.Core.Orders;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.Orders;

/// <summary>
/// Covers the round-robin interleave engine shared by every RoundRobin* order - the non-air-block
/// path of <c>RoundRobinBase.BuildInterleavedPositions</c>, <c>PreComputePositions</c>,
/// <c>OrderBy</c> (both overloads) and <c>GetSortKey</c> - plus the four simple group-ordering
/// strategies: <see cref="RoundRobinOrder"/> (A-Z), <see cref="RoundRobinOrderDesc"/> (Z-A),
/// <see cref="RoundRobinRandomOrder"/> (random group order, natural order within groups) and
/// <see cref="RoundRobinShuffledOrder"/> (random group order AND shuffled within groups).
///
/// Air blocks (Collections grouping + air-date within-group order),
/// <c>ExtractGroupKey</c>/<c>CompareWithinGroup</c> in isolation, and
/// <see cref="RoundRobinLeastRecentlyWatchedOrder"/> are covered by other test files - this file
/// only relies on them working, it never exercises them directly.
///
/// What breaks silently if a test here goes red:
/// - The interleave loop is "for each level, for each group: emit one item if the group still has
///   one at that level". Swap the loop nesting and users get "all of show A, then all of show B"
///   instead of a rotation. Drop the bounds check and an unequal-size library either throws or
///   silently skips items from the short groups.
/// - <c>ItemPositions</c> is a FRESH dictionary on every <c>PreComputePositions</c> call, not a
///   merge - SmartList calls it more than once per refresh (per-group limit passes), so a merge
///   would leak stale positions into the final order.
/// - <c>OrderBy</c> (the single-sort fast path) and <c>GetSortKey</c> (multi-sort) must agree, or
///   the same playlist sorts differently depending on how many sorts the user configured -
///   SeasonNumberOrder shipped exactly that defect.
/// </summary>
public class RoundRobinInterleaveTests
{
    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Minimal ILogger that records every call, so the "no GroupByField configured" warning can
    /// be asserted on directly. No mocking library is available in this project, so this is a
    /// plain interface implementation rather than a generated mock.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>Sets GroupByField to "SeriesName" and runs PreComputePositions once.</summary>
    private static T Configured<T>(T order, IEnumerable<BaseItem> items, ILogger? logger = null)
        where T : RoundRobinBase
    {
        order.GroupByField = "SeriesName";
        order.PreComputePositions(items, logger);
        return order;
    }

    private static string[] OrderByNames(RoundRobinBase order, IEnumerable<BaseItem> items) =>
        TestItems.Names(order.OrderBy(items));

    /// <summary>Groups A (4 items), B (2 items), C (1 item) - fed in scrambled order so a
    /// passthrough sort cannot pass by accident.</summary>
    private static List<BaseItem> UnequalGroups() =>
    [
        TestItems.Ep("C", 1, 1),
        TestItems.Ep("A", 1, 3),
        TestItems.Ep("B", 1, 2),
        TestItems.Ep("A", 1, 1),
        TestItems.Ep("A", 1, 4),
        TestItems.Ep("B", 1, 1),
        TestItems.Ep("A", 1, 2),
    ];

    /// <summary>3 shows x 3 episodes each, fed in scrambled, non-natural order.</summary>
    private static List<BaseItem> ThreeGroupsWithOutOfOrderEpisodes() =>
    [
        TestItems.Ep("Show A", 1, 3),
        TestItems.Ep("Show B", 2, 1),
        TestItems.Ep("Show C", 1, 2),
        TestItems.Ep("Show A", 1, 1),
        TestItems.Ep("Show B", 1, 2),
        TestItems.Ep("Show C", 1, 1),
        TestItems.Ep("Show A", 1, 2),
        TestItems.Ep("Show B", 1, 1),
        TestItems.Ep("Show C", 2, 1),
    ];

    // ---------------------------------------------------------------------------------
    // Interleave shape
    // ---------------------------------------------------------------------------------

    [Fact]
    public void BuildInterleavedPositions_EqualGroupSizes_ProducesStrictRotation()
    {
        var items = new List<BaseItem>
        {
            TestItems.Ep("B", 1, 2),
            TestItems.Ep("A", 1, 1),
            TestItems.Ep("C", 1, 1),
            TestItems.Ep("A", 1, 2),
            TestItems.Ep("C", 1, 2),
            TestItems.Ep("B", 1, 1),
        };

        var names = OrderByNames(Configured(new RoundRobinOrder(), items), items);

        Assert.Equal(
            ["A S01E01", "B S01E01", "C S01E01", "A S01E02", "B S01E02", "C S01E02"],
            names);
    }

    /// <summary>
    /// The `if (level &lt; group.Count)` branch: once B and C run out, A keeps rotating alone
    /// instead of the loop throwing or skipping A's remaining episodes.
    /// </summary>
    [Fact]
    public void BuildInterleavedPositions_UnequalGroupSizes_ShortGroupsDropOut_ButTheRestKeepRotating()
    {
        var items = UnequalGroups();

        var names = OrderByNames(Configured(new RoundRobinOrder(), items), items);

        Assert.Equal(
            ["A S01E01", "B S01E01", "C S01E01", "A S01E02", "B S01E02", "A S01E03", "A S01E04"],
            names);
    }

    [Fact]
    public void BuildInterleavedPositions_AssignsDenseContiguousPositions_WithNoGapsOrDuplicates()
    {
        var items = UnequalGroups();

        var order = Configured(new RoundRobinOrder(), items);

        var positions = order.ItemPositions.Values.OrderBy(p => p).ToList();
        Assert.Equal(Enumerable.Range(0, items.Count), positions);
        Assert.Equal(items.Count, order.ItemPositions.Keys.Distinct().Count());
    }

    /// <summary>
    /// Group keys are folded case-insensitively (the groups Dictionary is built with
    /// StringComparer.OrdinalIgnoreCase), so metadata that disagrees only on casing - "Marvel" vs
    /// "marvel", "Action" vs "action" - is ONE rotation group, not two. Nothing else in the suite
    /// pins this: ExtractGroupKey deliberately returns the raw casing, so the fold happens only
    /// here, and swapping the comparer to Ordinal is a silent change that splits a user's rotation
    /// in half.
    ///
    /// The two outcomes are distinguishable by design. Folded: one group of three, emitted in
    /// natural order (E01, E02, E03). Split: two groups that tie under the case-insensitive
    /// natural comparer used for group ordering, so they interleave and E03 lands in the middle
    /// (E01, E03, E02).
    /// </summary>
    [Fact]
    public void GroupKeys_AreFoldedCaseInsensitively_SoCasingDifferencesDoNotSplitAGroup()
    {
        var items = new List<BaseItem>
        {
            TestItems.Ep("Show", 1, 1),
            TestItems.Ep("Show", 1, 2),
            TestItems.Ep("SHOW", 1, 3),
        };

        var names = OrderByNames(Configured(new RoundRobinOrder(), items), items);

        Assert.Equal(["Show S01E01", "Show S01E02", "SHOW S01E03"], names);
        // What a case-SENSITIVE grouping would have produced instead.
        Assert.NotEqual(["Show S01E01", "SHOW S01E03", "Show S01E02"], names);
    }

    // ---------------------------------------------------------------------------------
    // Within-group order survives every group-ordering strategy
    // ---------------------------------------------------------------------------------

    [Fact]
    public void WithinGroupOrder_IsNaturalSeasonThenEpisode_ForEveryNonShuffledVariant()
    {
        var items = ThreeGroupsWithOutOfOrderEpisodes();
        RoundRobinBase[] orders =
        [
            Configured(new RoundRobinOrder(), items),
            Configured(new RoundRobinOrderDesc(), items),
            Configured(new RoundRobinRandomOrder(), items),
        ];

        foreach (var order in orders)
        {
            var showB = OrderByNames(order, items).Where(n => n.StartsWith("Show B ", StringComparison.Ordinal)).ToArray();
            Assert.Equal(["Show B S01E01", "Show B S01E02", "Show B S02E01"], showB);
        }
    }

    // ---------------------------------------------------------------------------------
    // Group order per subclass
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// SharedNaturalComparer only treats a LEADING number as numeric (see NameOrderTests), so the
    /// group keys must start with the digit for this to matter - "2 Show"/"10 Show", not
    /// "Show 2"/"Show 10" (those have no leading digit, fall back to plain string comparison, and
    /// would coincidentally land in the same order either way).
    /// </summary>
    [Fact]
    public void RoundRobinOrder_OrdersGroupsAscending_UsingTheNaturalComparer_NotPlainStringSort()
    {
        var items = new List<BaseItem>
        {
            TestItems.Ep("10 Show", 1, 1),
            TestItems.Ep("2 Show", 1, 1),
        };

        var names = OrderByNames(Configured(new RoundRobinOrder(), items), items);

        Assert.Equal(["2 Show S01E01", "10 Show S01E01"], names);
        // Plain ordinal string sort would put "10 Show" first - the opposite of natural order.
        Assert.NotEqual(["10 Show S01E01", "2 Show S01E01"], names);
    }

    /// <summary>
    /// Every item its own group, so group ordering is the ONLY thing deciding the sequence - and
    /// pins that descending is the exact reverse of ascending over the same group set.
    /// </summary>
    [Fact]
    public void PreComputePositions_EveryItemInItsOwnGroup_AscendingIsNaturalOrder_DescendingIsTheExactReverse()
    {
        var items = new List<BaseItem>
        {
            TestItems.Ep("Zulu", 1, 1),
            TestItems.Ep("Alpha", 1, 1),
            TestItems.Ep("Mike", 1, 1),
        };

        var ascending = OrderByNames(Configured(new RoundRobinOrder(), items), items);
        var descending = OrderByNames(Configured(new RoundRobinOrderDesc(), items), items);

        Assert.Equal(["Alpha S01E01", "Mike S01E01", "Zulu S01E01"], ascending);
        Assert.Equal(ascending.Reverse().ToArray(), descending);
    }

    // ---------------------------------------------------------------------------------
    // Degenerate inputs
    // ---------------------------------------------------------------------------------

    [Fact]
    public void PreComputePositions_EmptyItemList_ProducesNoPositionsAndNoWarning()
    {
        var logger = new CapturingLogger();
        var order = new RoundRobinOrder { GroupByField = "SeriesName" };

        order.PreComputePositions(new List<BaseItem>(), logger);

        Assert.Empty(order.ItemPositions);
        // Distinct from the "missing GroupByField" case below: no items means the count==0 branch
        // of the guard fires and the warning is never reached at all.
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void PreComputePositions_MissingGroupByField_ProducesNoPositions_LogsAWarning_AndOrderByKeepsInputOrder(string? groupByField)
    {
        var items = new List<BaseItem>
        {
            TestItems.Ep("Z", 1, 1),
            TestItems.Ep("A", 1, 1),
        };
        var logger = new CapturingLogger();
        var order = new RoundRobinOrder { GroupByField = groupByField };

        order.PreComputePositions(items, logger);

        Assert.Empty(order.ItemPositions);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("no GroupByField configured", StringComparison.Ordinal));

        // ItemPositions.Count == 0 short-circuits OrderBy back to the untouched input sequence.
        Assert.Equal(TestItems.Names(items), TestItems.Names(order.OrderBy(items)));
    }

    [Fact]
    public void PreComputePositions_SingleGroup_ProducesPlainNaturalOrder()
    {
        var items = new List<BaseItem>
        {
            TestItems.Ep("Show", 1, 3),
            TestItems.Ep("Show", 1, 1),
            TestItems.Ep("Show", 1, 2),
        };

        var names = OrderByNames(Configured(new RoundRobinOrder(), items), items);

        Assert.Equal(["Show S01E01", "Show S01E02", "Show S01E03"], names);
    }

    // ---------------------------------------------------------------------------------
    // OrderBy vs GetSortKey agreement
    // ---------------------------------------------------------------------------------

    [Fact]
    public void OrderByAndGetSortKey_ProduceTheSameSequence()
    {
        var items = UnequalGroups();
        var order = Configured(new RoundRobinOrder(), items);

        var viaOrderBy = OrderByNames(order, items);
        var viaGetSortKey = TestItems.Names(items.OrderBy(i => order.GetSortKey(i, TestItems.User, null, null)));

        Assert.Equal(viaOrderBy, viaGetSortKey);
    }

    /// <summary>
    /// A miss in ItemPositions falls back to int.MaxValue in both OrderBy and GetSortKey, so an
    /// item that was never part of the precomputed set sorts last instead of throwing or landing
    /// at some arbitrary spot.
    /// </summary>
    [Fact]
    public void ItemsAbsentFromPrecomputedPositions_SortLast_ViaBothOrderByAndGetSortKey()
    {
        var items = UnequalGroups();
        var order = Configured(new RoundRobinOrder(), items);
        var stray = TestItems.Ep("Stray", 1, 1);

        Assert.Equal(int.MaxValue, order.GetSortKey(stray, TestItems.User, null, null));

        var withStray = new List<BaseItem>(items) { stray };
        var sorted = OrderByNames(order, withStray);

        Assert.Equal("Stray S01E01", sorted[^1]);
        Assert.Equal(OrderByNames(order, items), sorted[..^1]);
    }

    // ---------------------------------------------------------------------------------
    // Stale state
    // ---------------------------------------------------------------------------------

    [Fact]
    public void PreComputePositions_SecondCall_ReplacesItemPositions_RatherThanMerging()
    {
        // SmartList calls PreComputePositions more than once per refresh (intermediate passes for
        // per-group limits), so a merge here would let ids from an earlier pass leak into the
        // final order.
        var firstBatch = new List<BaseItem> { TestItems.Ep("A", 1, 1), TestItems.Ep("B", 1, 1) };
        var order = Configured(new RoundRobinOrder(), firstBatch);
        Assert.Equal(2, order.ItemPositions.Count);

        var secondBatch = new List<BaseItem> { TestItems.Ep("C", 1, 1) };
        order.PreComputePositions(secondBatch);

        Assert.Equal([secondBatch[0].Id], order.ItemPositions.Keys);
        Assert.All(firstBatch, item => Assert.False(order.ItemPositions.ContainsKey(item.Id)));
    }

    // ---------------------------------------------------------------------------------
    // Random variants - not assertable by exact sequence, so these pin invariants instead.
    // ---------------------------------------------------------------------------------

    [Fact]
    public void RoundRobinRandomOrder_GroupOrder_IsActuallyRandomised_AcrossManyRuns()
    {
        // 4 single-item groups isolate the group-order signal from within-group order entirely.
        var items = new List<BaseItem>
        {
            TestItems.Ep("Alpha", 1, 1),
            TestItems.Ep("Bravo", 1, 1),
            TestItems.Ep("Charlie", 1, 1),
            TestItems.Ep("Delta", 1, 1),
        };
        var order = new RoundRobinRandomOrder { GroupByField = "SeriesName" };

        // 50 runs of a 4-group (4! = 24 arrangements) shuffle: the probability of a genuine
        // Fisher-Yates shuffle never producing a second distinct arrangement across 50 independent
        // draws is (1/24)^49 - a false failure here is not realistically possible.
        var seen = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            order.PreComputePositions(items);
            seen.Add(string.Join(",", OrderByNames(order, items)));
        }

        Assert.True(seen.Count > 1, "RoundRobinRandomOrder never produced more than one distinct group ordering across 50 runs.");

        // The permutation invariant (nothing dropped or duplicated) holds on every run too.
        var lastRun = OrderByNames(order, items);
        Assert.Equal(TestItems.Names(items).OrderBy(n => n, StringComparer.Ordinal), lastRun.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void RoundRobinRandomOrder_PreservesWithinGroupNaturalOrder_AcrossManyRuns()
    {
        var items = new List<BaseItem>
        {
            TestItems.Ep("Show", 1, 3),
            TestItems.Ep("Other", 1, 2),
            TestItems.Ep("Show", 2, 1),
            TestItems.Ep("Show", 1, 1),
            TestItems.Ep("Other", 1, 1),
            TestItems.Ep("Show", 1, 2),
        };
        var order = new RoundRobinRandomOrder { GroupByField = "SeriesName" };

        for (var i = 0; i < 20; i++)
        {
            order.PreComputePositions(items);
            var showOrder = OrderByNames(order, items).Where(n => n.StartsWith("Show S", StringComparison.Ordinal)).ToArray();
            Assert.Equal(["Show S01E01", "Show S01E02", "Show S01E03", "Show S02E01"], showOrder);
        }
    }

    [Fact]
    public void RoundRobinShuffledOrder_OutputIsAPermutationOfTheInput()
    {
        var items = ThreeGroupsWithOutOfOrderEpisodes();
        var order = Configured(new RoundRobinShuffledOrder(), items);

        var result = OrderByNames(order, items);

        Assert.Equal(items.Count, result.Length);
        Assert.Equal(TestItems.Names(items).OrderBy(n => n, StringComparer.Ordinal), result.OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    /// The whole difference from RoundRobinRandomOrder: if ShuffleWithinGroups were ever dropped,
    /// RoundRobinShuffledOrder would silently become its base class, and a single group would sort
    /// in natural order on every run instead of varying.
    /// </summary>
    [Fact]
    public void RoundRobinShuffledOrder_ShufflesWithinGroups_AcrossManyRuns_UnlikeRoundRobinRandomOrder()
    {
        // A single group: with only one group, group-order randomisation cannot produce any
        // variation, so ANY variation seen here can only come from ShuffleWithinGroups.
        var items = new List<BaseItem>
        {
            TestItems.Ep("Show", 1, 1),
            TestItems.Ep("Show", 1, 2),
            TestItems.Ep("Show", 1, 3),
            TestItems.Ep("Show", 1, 4),
        };
        var order = new RoundRobinShuffledOrder { GroupByField = "SeriesName" };

        // 50 runs of a 4-item (4! = 24 arrangements) shuffle: probability of never seeing a second
        // arrangement under a genuine shuffle is (1/24)^49 - not realistically reachable by chance.
        var seen = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            order.PreComputePositions(items);
            seen.Add(string.Join(",", OrderByNames(order, items)));
        }

        Assert.True(seen.Count > 1, "RoundRobinShuffledOrder never varied the within-group order across 50 runs - ShuffleWithinGroups may have been dropped.");
    }
}
