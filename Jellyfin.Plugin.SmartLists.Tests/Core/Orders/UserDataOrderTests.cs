using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core.Orders;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.Orders;

/// <summary>
/// Covers <see cref="PlayCountOrder"/>/<see cref="PlayCountOrderDesc"/> (via their shared base
/// <see cref="UserDataOrder"/>) and <see cref="LastPlayedOrder"/>/<see cref="LastPlayedOrderDesc"/>
/// (via <see cref="LastPlayedOrderBase"/>) - the only sorts in Core/Orders whose result depends on
/// WHICH USER is refreshing the list. Every other sort produces the same output regardless of who
/// triggers the refresh; these two silently produce a different playlist per viewer, which means a
/// defect here is invisible to whoever tests the feature unless they test it as a second user.
///
/// Four things pinned here, all silent and user-visible when broken:
///
/// 1. OrderBy AND GetSortKey MUST AGREE. OrderBy drives the single-sort fast path
///    (SmartList.ApplyMultipleOrders returns Order.OrderBy directly for exactly one order);
///    GetSortKey drives multi-sort via SmartList.ApplySortingCore. A disagreement means the same
///    list sorts differently depending on how many sorts the user configured - SeasonNumberOrder
///    shipped exactly that defect. NOTE: for PlayCountOrder this file deliberately avoids ties in
///    the agreement assertions - see the "OrderBy applies a DateCreated tie-break GetSortKey does
///    not" note in the accompanying report, which documents a real (unfixed) divergence on tied
///    PlayCount values rather than writing a test that would hide it.
///
/// 2. DIRECTION IS NOT BAKED INTO THE KEY. The Asc and Desc subclasses must return the SAME key for
///    the same item; direction comes from the caller choosing OrderByDescending. A negated key
///    would double-reverse in multi-sort.
///
/// 3. THREE DISTINCT "NO DATA" STATES must all degrade to the documented sentinel (0 for PlayCount,
///    DateTime.MinValue for LastPlayed) without throwing: a user-data row with default values, NO
///    row at all (the negative cache), and a null userDataManager entirely. A throw here aborts the
///    whole refresh for every user, not just the one with missing data.
///
/// 4. CONTAINER AGGREGATION. Series/Season/MusicAlbum read their per-user watch state from their
///    CACHED CHILDREN (keyed by (ItemId, UserId) - aggregation is per-user too) rather than their
///    own user-data row, and only when the refresh cache actually holds those children. Both
///    classes are exercised against <see cref="TestItems.ThrowingUserData"/> throughout: a cache hit
///    (positive or negative) must never fall through to the manager, so any test that accidentally
///    exercises the manager fails loudly instead of quietly making an extra DB round-trip.
/// </summary>
public class UserDataOrderTests
{
    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------

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

    /// <summary>
    /// A minimal <see cref="IUserDataManager"/> that answers only <c>GetUserData</c> with a fixed
    /// value, for the one legitimate path where production calls the manager directly: OrderBy/
    /// GetSortKey with a null <c>refreshCache</c> (no cache to consult, so there is nothing to hit
    /// first). Everywhere a refresh cache is present, tests use
    /// <see cref="TestItems.ThrowingUserData"/> instead, so a stray manager call fails loudly.
    /// </summary>
    private class StubUserDataManager : DispatchProxy
    {
        public UserItemData? Value { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetUserData")
            {
                return Value;
            }

            throw new NotSupportedException($"StubUserDataManager: {targetMethod?.Name} is not stubbed.");
        }
    }

    private static IUserDataManager StubUserData(UserItemData? value)
    {
        var proxy = DispatchProxy.Create<IUserDataManager, StubUserDataManager>();
        ((StubUserDataManager)proxy).Value = value;
        return proxy;
    }

    /// <summary>
    /// Seeds a PlayCount value directly - <see cref="TestItems.SeedUserData"/> only exposes
    /// Played/LastPlayedDate (its only prior consumer, RoundRobinLeastRecentlyWatchedOrder, never
    /// reads PlayCount), so this file declares its own seeding helper rather than editing TestItems.
    /// </summary>
    private static void SeedPlayCount(RefreshQueueService.RefreshCache cache, BaseItem item, User user, int playCount)
    {
        cache.UserDataCache[(item.Id, user.Id)] = new UserItemData
        {
            Key = item.Id.ToString("N"),
            PlayCount = playCount,
        };
    }

    private static int PlayCount(Order order, BaseItem item, User user, IUserDataManager? manager, RefreshQueueService.RefreshCache? cache) =>
        Assert.IsType<int>(order.GetSortKey(item, user, manager, null, null, cache));

    private static DateTime LastPlayed(Order order, BaseItem item, User user, IUserDataManager? manager, RefreshQueueService.RefreshCache? cache) =>
        Assert.IsType<DateTime>(order.GetSortKey(item, user, manager, null, null, cache));

    /// <summary>The path SmartList.ApplyMultipleOrders takes for a single sort.</summary>
    private static string[] SortByOrderBy(Order order, IEnumerable<BaseItem> items, User user, IUserDataManager manager, RefreshQueueService.RefreshCache cache) =>
        TestItems.Names(order.OrderBy(items, user, manager, null, cache));

    /// <summary>The path SmartList.ApplySortingCore takes for multi-sort.</summary>
    private static string[] SortByKey(Order order, IEnumerable<BaseItem> items, User user, IUserDataManager manager, RefreshQueueService.RefreshCache cache, bool descending) =>
        TestItems.Names(descending
            ? items.OrderByDescending(i => order.GetSortKey(i, user, manager, null, null, cache))
            : items.OrderBy(i => order.GetSortKey(i, user, manager, null, null, cache)));

    // ===================================================================================
    // PlayCountOrder / PlayCountOrderDesc
    // ===================================================================================

    [Fact]
    public void PlayCount_OrderBy_Ascending_SortsLowestPlayCountFirst()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var high = TestItems.Mov("High");
        var low = TestItems.Mov("Low");
        var mid = TestItems.Mov("Mid");
        SeedPlayCount(cache, high, TestItems.User, 9);
        SeedPlayCount(cache, low, TestItems.User, 1);
        SeedPlayCount(cache, mid, TestItems.User, 4);

        var sorted = SortByOrderBy(new PlayCountOrder(), [high, low, mid], TestItems.User, TestItems.ThrowingUserData(), cache);

        Assert.Equal(["Low", "Mid", "High"], sorted);
    }

    [Fact]
    public void PlayCount_OrderBy_Descending_SortsHighestPlayCountFirst()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var high = TestItems.Mov("High");
        var low = TestItems.Mov("Low");
        var mid = TestItems.Mov("Mid");
        SeedPlayCount(cache, high, TestItems.User, 9);
        SeedPlayCount(cache, low, TestItems.User, 1);
        SeedPlayCount(cache, mid, TestItems.User, 4);

        var sorted = SortByOrderBy(new PlayCountOrderDesc(), [high, low, mid], TestItems.User, TestItems.ThrowingUserData(), cache);

        Assert.Equal(["High", "Mid", "Low"], sorted);
    }

    /// <summary>
    /// Every item here has a DISTINCT PlayCount on purpose: UserDataOrder.OrderBy appends
    /// <c>.ThenBy(item.DateCreated)</c> as a tie-break (UserDataOrder.cs:62-63) that the GetSortKey
    /// path (used here to emulate ApplySortingCore) never applies, so tied PlayCounts are a real,
    /// separately-reported divergence rather than something this agreement check can safely cover.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayCount_OrderByAndGetSortKey_ProduceTheSameOrdering_ForDistinctValues(bool descending)
    {
        var cache = new RefreshQueueService.RefreshCache();
        var never = TestItems.Mov("Never");
        var low = TestItems.Mov("Low");
        var mid = TestItems.Mov("Mid");
        var high = TestItems.Mov("High");
        TestItems.SeedNoUserData(cache, never, TestItems.User);
        SeedPlayCount(cache, low, TestItems.User, 2);
        SeedPlayCount(cache, mid, TestItems.User, 5);
        SeedPlayCount(cache, high, TestItems.User, 9);
        BaseItem[] items = [high, never, mid, low];

        Order order = descending ? new PlayCountOrderDesc() : new PlayCountOrder();

        Assert.Equal(
            SortByOrderBy(order, items, TestItems.User, TestItems.ThrowingUserData(), cache),
            SortByKey(order, items, TestItems.User, TestItems.ThrowingUserData(), cache, descending));
    }

    [Fact]
    public void PlayCount_GetSortKey_Descending_ReturnsTheSameKeyAsAscending()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = TestItems.Mov("X");
        SeedPlayCount(cache, movie, TestItems.User, 4);

        Assert.Equal(4, PlayCount(new PlayCountOrder(), movie, TestItems.User, TestItems.ThrowingUserData(), cache));
        Assert.Equal(4, PlayCount(new PlayCountOrderDesc(), movie, TestItems.User, TestItems.ThrowingUserData(), cache));
    }

    /// <summary>The sentinel (0) is not direction-aware, so unwatched items bookend the list.</summary>
    [Theory]
    [InlineData(false, new[] { "NeverPlayed", "Low", "High" })]
    [InlineData(true, new[] { "High", "Low", "NeverPlayed" })]
    public void PlayCount_UnwatchedItems_SortFirstAscending_AndLastDescending(bool descending, string[] expected)
    {
        var cache = new RefreshQueueService.RefreshCache();
        var never = TestItems.Mov("NeverPlayed");
        var low = TestItems.Mov("Low");
        var high = TestItems.Mov("High");
        TestItems.SeedNoUserData(cache, never, TestItems.User);
        SeedPlayCount(cache, low, TestItems.User, 2);
        SeedPlayCount(cache, high, TestItems.User, 8);

        Order order = descending ? new PlayCountOrderDesc() : new PlayCountOrder();

        Assert.Equal(expected, SortByOrderBy(order, [high, never, low], TestItems.User, TestItems.ThrowingUserData(), cache));
    }

    [Fact]
    public void PlayCount_GetSortKey_SameItem_DifferentUsers_ProducesDifferentKeys()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = TestItems.Mov("Shared");
        SeedPlayCount(cache, movie, TestItems.User, 2);
        SeedPlayCount(cache, movie, TestItems.OtherUser, 9);

        var order = new PlayCountOrder();

        Assert.Equal(2, PlayCount(order, movie, TestItems.User, TestItems.ThrowingUserData(), cache));
        Assert.Equal(9, PlayCount(order, movie, TestItems.OtherUser, TestItems.ThrowingUserData(), cache));
    }

    // -------------------------------------------------------- PlayCount: missing-data states

    [Fact]
    public void PlayCount_UserDataRowWithDefaultValues_ReturnsZero()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = TestItems.Mov("DefaultRow");
        TestItems.SeedUserData(cache, movie, TestItems.User); // Played:false, LastPlayed:null, PlayCount defaults to 0

        Assert.Equal(0, PlayCount(new PlayCountOrder(), movie, TestItems.User, TestItems.ThrowingUserData(), cache));
    }

    [Fact]
    public void PlayCount_NoUserDataRowAtAll_ReturnsZero()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = TestItems.Mov("NoRow");
        TestItems.SeedNoUserData(cache, movie, TestItems.User);

        Assert.Equal(0, PlayCount(new PlayCountOrder(), movie, TestItems.User, TestItems.ThrowingUserData(), cache));
    }

    [Fact]
    public void PlayCount_NullUserDataManager_GetSortKey_ReturnsZero_WithoutThrowing()
    {
        var movie = TestItems.Mov("NullManager");

        Assert.Equal(0, PlayCount(new PlayCountOrder(), movie, TestItems.User, null, new RefreshQueueService.RefreshCache()));
    }

    [Fact]
    public void PlayCount_NullUserDataManager_OrderBy_ReturnsItemsUnsorted_AndLogsAWarning()
    {
        var b = TestItems.Mov("B");
        var a = TestItems.Mov("A"); // deliberately out of both name and playcount order
        var logger = new CapturingLogger();

        var result = TestItems.Names(new PlayCountOrder().OrderBy([b, a], TestItems.User, null, logger, new RefreshQueueService.RefreshCache()));

        Assert.Equal(["B", "A"], result);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// The one path where PlayCountOrder legitimately calls the manager directly: no refresh cache
    /// means there is no cache to consult first. Confirms PlayCount goes through the same
    /// cache-first-then-manager pattern as LastPlayed rather than a different, uncached path.
    /// </summary>
    [Fact]
    public void PlayCount_NullRefreshCache_FallsBackToTheUserDataManagerDirectly()
    {
        var movie = TestItems.Mov("NoCache");
        var manager = StubUserData(new UserItemData { Key = movie.Id.ToString("N"), PlayCount = 6 });

        Assert.Equal(6, PlayCountOrder.GetPlayCountFromUserData(movie, TestItems.User, manager, logger: null, refreshCache: null));
    }

    // -------------------------------------------------------- PlayCount: container aggregation

    /// <summary>
    /// Season/MusicAlbum take the MINIMUM PlayCount across cached children (not the max, unlike
    /// LastPlayed) - a season only counts as "played N times" once every episode has been, so one
    /// never-watched episode should drag the whole season's count down, not get averaged away.
    /// </summary>
    [Theory]
    [MemberData(nameof(PlayCountAggregateContainerCases))]
    public void PlayCount_SeasonAndAlbumContainers_TakeTheMinimumAcrossCachedChildren(
        Func<RefreshQueueService.RefreshCache, BaseItem[], BaseItem> buildContainerAndSeedChildren)
    {
        var cache = new RefreshQueueService.RefreshCache();
        var lowChild = TestItems.Ep("Ignored", 1, 1, name: "Low Child");
        var highChild = TestItems.Ep("Ignored", 1, 2, name: "High Child");
        SeedPlayCount(cache, lowChild, TestItems.User, 2);
        SeedPlayCount(cache, highChild, TestItems.User, 9);

        var container = buildContainerAndSeedChildren(cache, [lowChild, highChild]);

        var result = PlayCountOrder.GetPlayCountFromUserData(container, TestItems.User, TestItems.ThrowingUserData(), logger: null, cache);

        Assert.Equal(2, result);
    }

    public static IEnumerable<object[]> PlayCountAggregateContainerCases()
    {
        yield return new object[]
        {
            (Func<RefreshQueueService.RefreshCache, BaseItem[], BaseItem>)((cache, children) =>
            {
                var season = TestItems.SeasonOf("Aggregate Season");
                cache.SeasonEpisodes[(season.Id, TestItems.User.Id)] = children;
                return season;
            })
        };
        yield return new object[]
        {
            (Func<RefreshQueueService.RefreshCache, BaseItem[], BaseItem>)((cache, children) =>
            {
                var album = TestItems.Album("Aggregate Album");
                cache.AlbumTracks[(album.Id, TestItems.User.Id)] = children;
                return album;
            })
        };
    }

    /// <summary>
    /// FINDING (see report): unlike LastPlayedOrderBase.GetAggregateLastPlayedDate, which aggregates
    /// Series too, PlayCountOrder.TryGetAggregateChildren (PlayCountOrder.cs:92-107) only matches
    /// Season and MusicAlbum. A Series container is silently NOT aggregated - it falls through to
    /// reading its own (nonexistent) user-data row and returns 0, even with fully-watched cached
    /// episodes sitting right there. This test pins the CURRENT behavior (not the desired one) so a
    /// future fix is a deliberate, visible change rather than an accidental one.
    /// </summary>
    [Fact]
    public void PlayCount_SeriesContainer_DoesNotAggregate_UnlikeLastPlayedOrderBase()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var series = TestItems.Show("Aggregate Series");
        var child = TestItems.Ep("Aggregate Series", 1, 1);
        SeedPlayCount(cache, child, TestItems.User, 7);
        cache.SeriesEpisodes[(series.Id, TestItems.User.Id)] = [child];
        TestItems.SeedNoUserData(cache, series, TestItems.User);

        var result = PlayCountOrder.GetPlayCountFromUserData(series, TestItems.User, TestItems.ThrowingUserData(), logger: null, cache);

        Assert.Equal(0, result); // NOT 7 - the cached child is never consulted for a Series container.
    }

    [Fact]
    public void PlayCount_EmptyChildArray_FallsBackToTheContainersOwnRow()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var season = TestItems.SeasonOf("Empty Season");
        cache.SeasonEpisodes[(season.Id, TestItems.User.Id)] = [];
        SeedPlayCount(cache, season, TestItems.User, 3);

        var result = PlayCountOrder.GetPlayCountFromUserData(season, TestItems.User, TestItems.ThrowingUserData(), logger: null, cache);

        Assert.Equal(3, result);
    }

    [Fact]
    public void PlayCount_AggregateChildWithNoRowAtAll_CountsAsZero_AndDragsTheMinimumDown()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var watchedChild = TestItems.Ep("Ignored", 1, 1, name: "Watched");
        var neverPlayedChild = TestItems.Ep("Ignored", 1, 2, name: "NeverPlayed");
        SeedPlayCount(cache, watchedChild, TestItems.User, 5);
        TestItems.SeedNoUserData(cache, neverPlayedChild, TestItems.User);
        var season = TestItems.SeasonOf("Mixed Season");
        cache.SeasonEpisodes[(season.Id, TestItems.User.Id)] = [watchedChild, neverPlayedChild];

        var result = PlayCountOrder.GetPlayCountFromUserData(season, TestItems.User, TestItems.ThrowingUserData(), logger: null, cache);

        Assert.Equal(0, result);
    }

    [Fact]
    public void PlayCount_AggregateChildren_ArePerUser()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var childA = TestItems.Ep("Ignored", 1, 1, name: "ChildA");
        var childB = TestItems.Ep("Ignored", 1, 2, name: "ChildB");
        SeedPlayCount(cache, childA, TestItems.User, 4);
        SeedPlayCount(cache, childB, TestItems.OtherUser, 9);
        var season = TestItems.SeasonOf("PerUser Season");
        cache.SeasonEpisodes[(season.Id, TestItems.User.Id)] = [childA];
        cache.SeasonEpisodes[(season.Id, TestItems.OtherUser.Id)] = [childB];

        Assert.Equal(4, PlayCountOrder.GetPlayCountFromUserData(season, TestItems.User, TestItems.ThrowingUserData(), null, cache));
        Assert.Equal(9, PlayCountOrder.GetPlayCountFromUserData(season, TestItems.OtherUser, TestItems.ThrowingUserData(), null, cache));
    }

    // -------------------------------------------------------- PlayCount: caching behaviour

    [Fact]
    public void PlayCount_OrderBy_CalledTwice_ProducesConsistentResults()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var a = TestItems.Mov("A");
        var b = TestItems.Mov("B");
        SeedPlayCount(cache, a, TestItems.User, 3);
        SeedPlayCount(cache, b, TestItems.User, 1);
        var order = new PlayCountOrder();
        BaseItem[] items = [a, b];

        var first = SortByOrderBy(order, items, TestItems.User, TestItems.ThrowingUserData(), cache);
        var second = SortByOrderBy(order, items, TestItems.User, TestItems.ThrowingUserData(), cache);

        Assert.Equal(first, second);
    }

    /// <summary>
    /// UserDataOrder.OrderBy's own sortValueCache (UserDataOrder.cs:46) is a fresh
    /// Dictionary&lt;BaseItem, int&gt; built inside every call - not a field on the order instance -
    /// so reusing the same order across two users cannot leak one user's values into the other's
    /// sort. This proves it end-to-end against a single shared RefreshCache, the realistic shape of
    /// a multi-user refresh pass.
    /// </summary>
    [Fact]
    public void PlayCount_OrderBy_SameCacheDifferentUser_IsNotCrossContaminated()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var a = TestItems.Mov("A");
        var b = TestItems.Mov("B");
        SeedPlayCount(cache, a, TestItems.User, 9);
        SeedPlayCount(cache, a, TestItems.OtherUser, 1);
        SeedPlayCount(cache, b, TestItems.User, 1);
        SeedPlayCount(cache, b, TestItems.OtherUser, 9);
        var order = new PlayCountOrder();
        BaseItem[] items = [a, b];

        Assert.Equal(["B", "A"], SortByOrderBy(order, items, TestItems.User, TestItems.ThrowingUserData(), cache));
        Assert.Equal(["A", "B"], SortByOrderBy(order, items, TestItems.OtherUser, TestItems.ThrowingUserData(), cache));
    }

    // ===================================================================================
    // LastPlayedOrder / LastPlayedOrderDesc / LastPlayedOrderBase
    // ===================================================================================

    [Fact]
    public void LastPlayed_OrderBy_Ascending_SortsLeastRecentlyPlayedFirst()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var recent = TestItems.Mov("Recent");
        var old = TestItems.Mov("Old");
        var mid = TestItems.Mov("Mid");
        TestItems.SeedUserData(cache, recent, TestItems.User, played: true, lastPlayed: new DateTime(2024, 6, 1));
        TestItems.SeedUserData(cache, old, TestItems.User, played: true, lastPlayed: new DateTime(2020, 1, 1));
        TestItems.SeedUserData(cache, mid, TestItems.User, played: true, lastPlayed: new DateTime(2022, 3, 1));

        var sorted = SortByOrderBy(new LastPlayedOrder(), [recent, old, mid], TestItems.User, TestItems.ThrowingUserData(), cache);

        Assert.Equal(["Old", "Mid", "Recent"], sorted);
    }

    [Fact]
    public void LastPlayed_OrderBy_Descending_SortsMostRecentlyPlayedFirst()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var recent = TestItems.Mov("Recent");
        var old = TestItems.Mov("Old");
        var mid = TestItems.Mov("Mid");
        TestItems.SeedUserData(cache, recent, TestItems.User, played: true, lastPlayed: new DateTime(2024, 6, 1));
        TestItems.SeedUserData(cache, old, TestItems.User, played: true, lastPlayed: new DateTime(2020, 1, 1));
        TestItems.SeedUserData(cache, mid, TestItems.User, played: true, lastPlayed: new DateTime(2022, 3, 1));

        var sorted = SortByOrderBy(new LastPlayedOrderDesc(), [recent, old, mid], TestItems.User, TestItems.ThrowingUserData(), cache);

        Assert.Equal(["Recent", "Mid", "Old"], sorted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LastPlayed_OrderByAndGetSortKey_ProduceTheSameOrdering(bool descending)
    {
        var cache = new RefreshQueueService.RefreshCache();
        var never = TestItems.Mov("Never");
        var old = TestItems.Mov("Old");
        var recent = TestItems.Mov("Recent");
        TestItems.SeedNoUserData(cache, never, TestItems.User);
        TestItems.SeedUserData(cache, old, TestItems.User, played: true, lastPlayed: new DateTime(2020, 1, 1));
        TestItems.SeedUserData(cache, recent, TestItems.User, played: true, lastPlayed: new DateTime(2024, 1, 1));
        BaseItem[] items = [recent, never, old];

        Order order = descending ? new LastPlayedOrderDesc() : new LastPlayedOrder();

        Assert.Equal(
            SortByOrderBy(order, items, TestItems.User, TestItems.ThrowingUserData(), cache),
            SortByKey(order, items, TestItems.User, TestItems.ThrowingUserData(), cache, descending));
    }

    /// <summary>
    /// Unlike PlayCountOrder, LastPlayedOrderBase.OrderBy has NO DateCreated tie-break
    /// ("no tie-breaker to avoid album grouping" - LastPlayedOrderBase.cs:74), so two items with the
    /// EXACT SAME LastPlayedDate must agree between OrderBy and GetSortKey too, not just distinct
    /// values.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LastPlayed_OrderByAndGetSortKey_AgreeEvenWhenDatesTie(bool descending)
    {
        var cache = new RefreshQueueService.RefreshCache();
        var tie = new DateTime(2023, 1, 1);
        var tiedA = TestItems.Mov("TiedA");
        var tiedB = TestItems.Mov("TiedB");
        var distinct = TestItems.Mov("Distinct");
        TestItems.SeedUserData(cache, tiedA, TestItems.User, played: true, lastPlayed: tie);
        TestItems.SeedUserData(cache, tiedB, TestItems.User, played: true, lastPlayed: tie);
        TestItems.SeedUserData(cache, distinct, TestItems.User, played: true, lastPlayed: new DateTime(2024, 1, 1));
        BaseItem[] items = [distinct, tiedA, tiedB];

        Order order = descending ? new LastPlayedOrderDesc() : new LastPlayedOrder();

        Assert.Equal(
            SortByOrderBy(order, items, TestItems.User, TestItems.ThrowingUserData(), cache),
            SortByKey(order, items, TestItems.User, TestItems.ThrowingUserData(), cache, descending));
    }

    [Fact]
    public void LastPlayed_GetSortKey_Descending_ReturnsTheSameKeyAsAscending()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = TestItems.Mov("X");
        var date = new DateTime(2023, 5, 1);
        TestItems.SeedUserData(cache, movie, TestItems.User, played: true, lastPlayed: date);

        Assert.Equal(date, LastPlayed(new LastPlayedOrder(), movie, TestItems.User, TestItems.ThrowingUserData(), cache));
        Assert.Equal(date, LastPlayed(new LastPlayedOrderDesc(), movie, TestItems.User, TestItems.ThrowingUserData(), cache));
    }

    [Theory]
    [InlineData(false, new[] { "NeverPlayed", "Early", "Late" })]
    [InlineData(true, new[] { "Late", "Early", "NeverPlayed" })]
    public void LastPlayed_UnwatchedItems_SortFirstAscending_AndLastDescending(bool descending, string[] expected)
    {
        var cache = new RefreshQueueService.RefreshCache();
        var never = TestItems.Mov("NeverPlayed");
        var early = TestItems.Mov("Early");
        var late = TestItems.Mov("Late");
        TestItems.SeedNoUserData(cache, never, TestItems.User);
        TestItems.SeedUserData(cache, early, TestItems.User, played: true, lastPlayed: new DateTime(2020, 1, 1));
        TestItems.SeedUserData(cache, late, TestItems.User, played: true, lastPlayed: new DateTime(2024, 1, 1));

        LastPlayedOrderBase order = descending ? new LastPlayedOrderDesc() : new LastPlayedOrder();

        Assert.Equal(expected, SortByOrderBy(order, [late, never, early], TestItems.User, TestItems.ThrowingUserData(), cache));
    }

    [Fact]
    public void LastPlayed_GetSortKey_SameItem_DifferentUsers_ProducesDifferentKeys()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = TestItems.Mov("Shared");
        TestItems.SeedUserData(cache, movie, TestItems.User, played: true, lastPlayed: new DateTime(2020, 1, 1));
        TestItems.SeedUserData(cache, movie, TestItems.OtherUser, played: true, lastPlayed: new DateTime(2024, 1, 1));

        var order = new LastPlayedOrder();

        Assert.Equal(new DateTime(2020, 1, 1), LastPlayed(order, movie, TestItems.User, TestItems.ThrowingUserData(), cache));
        Assert.Equal(new DateTime(2024, 1, 1), LastPlayed(order, movie, TestItems.OtherUser, TestItems.ThrowingUserData(), cache));
    }

    // -------------------------------------------------------- LastPlayed: missing-data states

    [Fact]
    public void LastPlayed_UserDataRowWithDefaultValues_ReturnsMinValue()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = TestItems.Mov("DefaultRow");
        TestItems.SeedUserData(cache, movie, TestItems.User); // Played:false, LastPlayed:null

        Assert.Equal(DateTime.MinValue, LastPlayed(new LastPlayedOrder(), movie, TestItems.User, TestItems.ThrowingUserData(), cache));
    }

    [Fact]
    public void LastPlayed_NoUserDataRowAtAll_ReturnsMinValue()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = TestItems.Mov("NoRow");
        TestItems.SeedNoUserData(cache, movie, TestItems.User);

        Assert.Equal(DateTime.MinValue, LastPlayed(new LastPlayedOrder(), movie, TestItems.User, TestItems.ThrowingUserData(), cache));
    }

    [Fact]
    public void LastPlayed_NullUserDataManager_GetSortKey_ReturnsMinValue_WithoutThrowing()
    {
        var movie = TestItems.Mov("NullManager");

        Assert.Equal(DateTime.MinValue, LastPlayed(new LastPlayedOrder(), movie, TestItems.User, null, new RefreshQueueService.RefreshCache()));
    }

    [Fact]
    public void LastPlayed_NullUserDataManager_OrderBy_ReturnsItemsUnsorted_AndLogsAWarning()
    {
        var b = TestItems.Mov("B");
        var a = TestItems.Mov("A");
        var logger = new CapturingLogger();

        var result = TestItems.Names(new LastPlayedOrder().OrderBy([b, a], TestItems.User, null, logger, new RefreshQueueService.RefreshCache()));

        Assert.Equal(["B", "A"], result);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// A null user is not one of the three documented "no data" states, but GetSortKey's own
    /// try/catch (LastPlayedOrderBase.cs:94/119) must still turn the resulting NullReferenceException
    /// (from <c>user.Id</c> inside GetAggregateLastPlayedDate) into the same MinValue sentinel rather
    /// than propagating and aborting the refresh.
    /// </summary>
    [Fact]
    public void LastPlayed_NullUser_GetSortKey_DegradesToMinValue_WithoutThrowing()
    {
        var movie = TestItems.Mov("NullUser");
        IComparable? key = null;

        var exception = Record.Exception(() =>
            key = new LastPlayedOrder().GetSortKey(movie, null!, TestItems.ThrowingUserData(), null, null, new RefreshQueueService.RefreshCache()));

        Assert.Null(exception);
        Assert.Equal(DateTime.MinValue, key);
    }

    // -------------------------------------------------------- LastPlayed: GetLastPlayedDateFromUserData (reflection)

    [Fact]
    public void GetLastPlayedDateFromUserData_NullUserData_ReturnsMinValue()
    {
        Assert.Equal(DateTime.MinValue, LastPlayedOrderBase.GetLastPlayedDateFromUserData(null));
    }

    [Fact]
    public void GetLastPlayedDateFromUserData_ObjectWithNoSuchProperty_ReturnsMinValue()
    {
        Assert.Equal(DateTime.MinValue, LastPlayedOrderBase.GetLastPlayedDateFromUserData(new object()));
    }

    [Fact]
    public void GetLastPlayedDateFromUserData_GenuineUserItemData_ReturnsTheStoredDate()
    {
        var date = new DateTime(2024, 3, 15);
        var userData = new UserItemData { Key = "k", LastPlayedDate = date };

        Assert.Equal(date, LastPlayedOrderBase.GetLastPlayedDateFromUserData(userData));
    }

    [Fact]
    public void GetLastPlayedDateFromUserData_UserItemData_WithNoLastPlayedDate_ReturnsMinValue()
    {
        var userData = new UserItemData { Key = "k" }; // LastPlayedDate left null

        Assert.Equal(DateTime.MinValue, LastPlayedOrderBase.GetLastPlayedDateFromUserData(userData));
    }

    // -------------------------------------------------------- LastPlayed: GetAggregateLastPlayedDate (container aggregation)

    [Fact]
    public void GetAggregateLastPlayedDate_NullRefreshCache_ReturnsNull()
    {
        var series = TestItems.Show("X");

        Assert.Null(LastPlayedOrderBase.GetAggregateLastPlayedDate(series, TestItems.User, TestItems.ThrowingUserData(), null));
    }

    [Theory]
    [MemberData(nameof(LastPlayedContainerCases))]
    public void GetAggregateLastPlayedDate_ContainerTypes_TakeTheMaximumAcrossCachedChildren(
        Func<RefreshQueueService.RefreshCache, BaseItem[], BaseItem> buildContainerAndSeedChildren)
    {
        var cache = new RefreshQueueService.RefreshCache();
        var earlyChild = TestItems.Ep("Ignored", 1, 1, name: "Early");
        var lateChild = TestItems.Ep("Ignored", 1, 2, name: "Late");
        TestItems.SeedUserData(cache, earlyChild, TestItems.User, played: true, lastPlayed: new DateTime(2020, 1, 1));
        TestItems.SeedUserData(cache, lateChild, TestItems.User, played: false, lastPlayed: new DateTime(2024, 1, 1));

        var container = buildContainerAndSeedChildren(cache, [earlyChild, lateChild]);

        var result = LastPlayedOrderBase.GetAggregateLastPlayedDate(container, TestItems.User, TestItems.ThrowingUserData(), cache);

        Assert.Equal(new DateTime(2024, 1, 1), result);
    }

    public static IEnumerable<object[]> LastPlayedContainerCases()
    {
        yield return new object[]
        {
            (Func<RefreshQueueService.RefreshCache, BaseItem[], BaseItem>)((cache, children) =>
            {
                var series = TestItems.Show("Aggregate Series");
                cache.SeriesEpisodes[(series.Id, TestItems.User.Id)] = children;
                return series;
            })
        };
        yield return new object[]
        {
            (Func<RefreshQueueService.RefreshCache, BaseItem[], BaseItem>)((cache, children) =>
            {
                var season = TestItems.SeasonOf("Aggregate Season");
                cache.SeasonEpisodes[(season.Id, TestItems.User.Id)] = children;
                return season;
            })
        };
        yield return new object[]
        {
            (Func<RefreshQueueService.RefreshCache, BaseItem[], BaseItem>)((cache, children) =>
            {
                var album = TestItems.Album("Aggregate Album");
                cache.AlbumTracks[(album.Id, TestItems.User.Id)] = children;
                return album;
            })
        };
    }

    /// <summary>
    /// Proves the switch in GetAggregateLastPlayedDate gates on TYPE, not on whether a cache entry
    /// happens to exist under the item's id: a Movie is never a Series/Season/MusicAlbum, so even a
    /// SeriesEpisodes entry seeded under the exact same id must be ignored.
    /// </summary>
    [Fact]
    public void GetAggregateLastPlayedDate_NonContainerItem_NeverAggregates_EvenWithAMatchingCacheEntry()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = TestItems.Mov("Movie");
        var child = TestItems.Ep("Ignored", 1, 1);
        TestItems.SeedUserData(cache, child, TestItems.User, played: true, lastPlayed: new DateTime(2024, 1, 1));
        cache.SeriesEpisodes[(movie.Id, TestItems.User.Id)] = [child];

        var result = LastPlayedOrderBase.GetAggregateLastPlayedDate(movie, TestItems.User, TestItems.ThrowingUserData(), cache);

        Assert.Null(result);
    }

    [Fact]
    public void GetAggregateLastPlayedDate_CachedChildrenArePerUser()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var series = TestItems.Show("Series");
        var childForUser = TestItems.Ep("Series", 1, 1, name: "ForUser");
        var childForOther = TestItems.Ep("Series", 1, 2, name: "ForOther");
        TestItems.SeedUserData(cache, childForUser, TestItems.User, played: true, lastPlayed: new DateTime(2020, 1, 1));
        TestItems.SeedUserData(cache, childForOther, TestItems.OtherUser, played: true, lastPlayed: new DateTime(2024, 1, 1));
        cache.SeriesEpisodes[(series.Id, TestItems.User.Id)] = [childForUser];
        cache.SeriesEpisodes[(series.Id, TestItems.OtherUser.Id)] = [childForOther];

        Assert.Equal(new DateTime(2020, 1, 1), LastPlayedOrderBase.GetAggregateLastPlayedDate(series, TestItems.User, TestItems.ThrowingUserData(), cache));
        Assert.Equal(new DateTime(2024, 1, 1), LastPlayedOrderBase.GetAggregateLastPlayedDate(series, TestItems.OtherUser, TestItems.ThrowingUserData(), cache));
    }

    /// <summary>
    /// Full pipeline (through GetSortKey, not the isolated static helper): the aggregate must WIN
    /// over the container's own row when both exist, not merely be consulted.
    /// </summary>
    [Fact]
    public void LastPlayed_GetSortKey_ContainerWithBothOwnRowAndCachedChildren_PrefersTheAggregate()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var series = TestItems.Show("Series");
        TestItems.SeedUserData(cache, series, TestItems.User, played: true, lastPlayed: new DateTime(2020, 1, 1)); // own row: OLD
        var child = TestItems.Ep("Series", 1, 1);
        TestItems.SeedUserData(cache, child, TestItems.User, played: false, lastPlayed: new DateTime(2024, 1, 1)); // child: RECENT
        cache.SeriesEpisodes[(series.Id, TestItems.User.Id)] = [child];

        var key = LastPlayed(new LastPlayedOrder(), series, TestItems.User, TestItems.ThrowingUserData(), cache);

        Assert.Equal(new DateTime(2024, 1, 1), key);
    }

    [Fact]
    public void LastPlayed_GetSortKey_ContainerWithNoCachedChildren_FallsBackToItsOwnRow()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var series = TestItems.Show("Series");
        TestItems.SeedUserData(cache, series, TestItems.User, played: true, lastPlayed: new DateTime(2020, 1, 1));
        // No SeriesEpisodes entry seeded at all.

        var key = LastPlayed(new LastPlayedOrder(), series, TestItems.User, TestItems.ThrowingUserData(), cache);

        Assert.Equal(new DateTime(2020, 1, 1), key);
    }

    [Fact]
    public void LastPlayed_GetSortKey_ContainerWithEmptyCachedChildrenArray_FallsBackToItsOwnRow()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var series = TestItems.Show("Series");
        TestItems.SeedUserData(cache, series, TestItems.User, played: true, lastPlayed: new DateTime(2020, 1, 1));
        cache.SeriesEpisodes[(series.Id, TestItems.User.Id)] = [];

        var key = LastPlayed(new LastPlayedOrder(), series, TestItems.User, TestItems.ThrowingUserData(), cache);

        Assert.Equal(new DateTime(2020, 1, 1), key);
    }

    // -------------------------------------------------------- LastPlayed: caching behaviour

    [Fact]
    public void LastPlayed_OrderBy_CalledTwice_ProducesConsistentResults()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var a = TestItems.Mov("A");
        var b = TestItems.Mov("B");
        TestItems.SeedUserData(cache, a, TestItems.User, played: true, lastPlayed: new DateTime(2023, 1, 1));
        TestItems.SeedUserData(cache, b, TestItems.User, played: true, lastPlayed: new DateTime(2021, 1, 1));
        var order = new LastPlayedOrder();
        BaseItem[] items = [a, b];

        var first = SortByOrderBy(order, items, TestItems.User, TestItems.ThrowingUserData(), cache);
        var second = SortByOrderBy(order, items, TestItems.User, TestItems.ThrowingUserData(), cache);

        Assert.Equal(first, second);
    }

    [Fact]
    public void LastPlayed_OrderBy_SameCacheDifferentUser_IsNotCrossContaminated()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var a = TestItems.Mov("A");
        var b = TestItems.Mov("B");
        TestItems.SeedUserData(cache, a, TestItems.User, played: true, lastPlayed: new DateTime(2024, 1, 1));
        TestItems.SeedUserData(cache, a, TestItems.OtherUser, played: true, lastPlayed: new DateTime(2020, 1, 1));
        TestItems.SeedUserData(cache, b, TestItems.User, played: true, lastPlayed: new DateTime(2020, 1, 1));
        TestItems.SeedUserData(cache, b, TestItems.OtherUser, played: true, lastPlayed: new DateTime(2024, 1, 1));
        var order = new LastPlayedOrder();
        BaseItem[] items = [a, b];

        Assert.Equal(["B", "A"], SortByOrderBy(order, items, TestItems.User, TestItems.ThrowingUserData(), cache));
        Assert.Equal(["A", "B"], SortByOrderBy(order, items, TestItems.OtherUser, TestItems.ThrowingUserData(), cache));
    }
}
