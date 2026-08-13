using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core.Orders;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.Orders;

/// <summary>
/// Covers <see cref="RoundRobinLeastRecentlyWatchedOrder"/>: the sort that makes a rotation
/// "continue where the user left off" with NO persisted state - group order is derived entirely,
/// on every refresh, from per-user <c>LastPlayedDate</c> data. Three things pinned here:
///
/// 1. <c>OrderGroupKeys</c> - least recently watched first, never-watched groups first of all,
///    alphabetical (natural, numeric-aware) tie-break, and a group in <c>HeldGroups</c> is treated
///    as MinValue (never-watched tier) regardless of how recently it was actually watched.
/// 2. <c>BuildGroupRecencyAndHoldState</c> - builds <c>GroupRecency</c> (and, when air blocks are
///    active, the hold's raw watch-state) for one user from the unfiltered item pool. The single
///    most load-bearing rule here: only FULLY PLAYED items advance the rotation, because Jellyfin
///    stamps <c>LastPlayedDate</c> on any playback (including a half-watched one).
/// 3. <c>ApplyMidBlockHold</c> (and the <c>PreComputePositions</c> override that calls it before
///    the base interleave runs) - the collections+air-date special case: a group whose most
///    recently played item sits in an air block that still has an unwatched, visible item stays
///    at the front of the rotation until that block is finished.
///
/// OUT OF SCOPE (owned by other test files, see RoundRobinGroupingTests / RoundRobinAirBlockTests /
/// RoundRobinInterleaveTests): ExtractGroupKey/CompareWithinGroup/Shuffle in isolation, the plain
/// interleave mechanics, and ChunkIntoAirBlocks/CompareWithinGroupByAirDate in isolation. This file
/// relies on all of those working and only exercises them through the Least Recently Watched
/// subclass's own logic.
///
/// THREE GUARDS THIS FILE DELIBERATELY DOES NOT TEST, because they are unreachable through the
/// public/internal surface given how <c>ApplyMidBlockHold</c> and <c>BuildGroupRecencyAndHoldState</c>
/// are wired together today (see the report for the full reasoning):
/// - The Id-based anchor tie-break (third tier, after LastPlayed and air date). Two items tied on
///   BOTH LastPlayed and air date are always chained into the SAME air block by
///   <c>ChunkIntoAirBlocks</c> (same date, within any non-negative window), so whichever one
///   "wins" as anchor, the hold check inspects the same block either way - the outcome of
///   <c>HeldGroups</c> never changes.
/// - "items with no timestamp never anchor" (<c>if (lastPlayed == DateTime.MinValue) continue;</c>).
///   Any item that reaches the anchor loop with a real timestamp always outranks a MinValue one in
///   the primary comparison, and a group made up ENTIRELY of no-timestamp items never gets a
///   <c>GroupRecency</c> entry in the first place (that requires <c>lastPlayed &gt; DateTime.MinValue</c>),
///   so <c>ApplyMidBlockHold</c>'s own <c>!GroupRecency.ContainsKey(...)</c> guard already skips it.
/// - The <c>bestAir == DateTime.MinValue</c> "no dated watch history" bail-out. An anchor with no
///   air date can never chain with anything in <c>ChunkIntoAirBlocks</c> (chaining requires
///   <c>date &gt; DateTime.MinValue</c>), so its block is always a singleton containing only the
///   (watched) anchor - there is never an unwatched companion in it to find either way.
/// </summary>
public class RoundRobinLeastRecentlyWatchedTests
{
    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Minimal ILogger that records every call, matching the pattern used by the sibling
    /// RoundRobin* test files. No mocking library is available in this project.
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

    public enum CompanionState { Watched, InProgress }

    /// <summary>
    /// A two-episode "crossover night": Show A (the anchor - watched, most recently played) and
    /// Show B (a same-air-date companion whose watch state the caller controls), both mapped into
    /// the "Crossover Night" collection and pre-processed through BuildGroupRecencyAndHoldState.
    /// Callers build their own filteredItems list from the returned episodes to control who is
    /// visible to the playlist.
    /// </summary>
    private static (RoundRobinLeastRecentlyWatchedOrder Order, Episode Anchor, Episode Companion) SetUpCrossoverNight(
        RefreshQueueService.RefreshCache cache, CompanionState companionState)
    {
        var showA = TestItems.Show("Show A");
        var showB = TestItems.Show("Show B");
        var airDate = new DateTime(2024, 1, 1);
        var anchor = TestItems.Ep("Show A", 1, 1, aired: airDate, show: showA);
        var companion = TestItems.Ep("Show B", 1, 1, aired: airDate, show: showB);

        TestItems.SeedUserData(cache, anchor, TestItems.User, played: true, lastPlayed: new DateTime(2024, 6, 1));
        if (companionState == CompanionState.Watched)
        {
            TestItems.SeedUserData(cache, companion, TestItems.User, played: true, lastPlayed: new DateTime(2024, 6, 1));
        }
        else
        {
            // In-progress: LastPlayedDate set but Played still false - this is what makes the
            // companion show up in the block topology (via WatchedByGroup) while still counting
            // as unwatched (via UnwatchedCollectionItemIds).
            TestItems.SeedUserData(cache, companion, TestItems.User, played: false, lastPlayed: new DateTime(2024, 5, 1));
        }

        var order = new RoundRobinLeastRecentlyWatchedOrder
        {
            GroupByField = "Collections",
            OrderWithinGroupsByAirDate = true,
            CollectionGroupKeys = TestItems.CollectionMap(("Crossover Night", new BaseItem[] { anchor, companion })),
        };

        order.BuildGroupRecencyAndHoldState(new BaseItem[] { anchor, companion }, TestItems.User, TestItems.ThrowingUserData(), cache, logger: null);

        return (order, anchor, companion);
    }

    // =================================================================================
    // OrderGroupKeys
    // =================================================================================

    [Fact]
    public void OrderGroupKeys_SortsAscendingByRecency_LeastRecentlyWatchedFirst()
    {
        var showA = TestItems.Mov("Show A");
        var showB = TestItems.Mov("Show B");
        var showC = TestItems.Mov("Show C");

        var order = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" };
        order.GroupRecency["Show A"] = new DateTime(2024, 1, 1);
        order.GroupRecency["Show B"] = new DateTime(2024, 1, 15);
        order.GroupRecency["Show C"] = new DateTime(2024, 1, 8);

        order.PreComputePositions(new BaseItem[] { showA, showB, showC });

        Assert.Equal(["Show A", "Show C", "Show B"], TestItems.Names(order.OrderBy(new BaseItem[] { showA, showB, showC })));
    }

    [Fact]
    public void OrderGroupKeys_GroupAbsentFromRecency_IsTreatedAsNeverWatched_AndSortsFirst()
    {
        var watched = TestItems.Mov("Watched Show");
        var neverWatched = TestItems.Mov("Never Watched Show");

        var order = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" };
        order.GroupRecency["Watched Show"] = new DateTime(2024, 1, 1);
        // "Never Watched Show" deliberately absent from GroupRecency.

        order.PreComputePositions(new BaseItem[] { watched, neverWatched });

        Assert.Equal(["Never Watched Show", "Watched Show"], TestItems.Names(order.OrderBy(new BaseItem[] { watched, neverWatched })));
    }

    /// <summary>
    /// "Show 2" vs "Show 10" is deliberate: a plain ordinal string compare puts "Show 10" first
    /// (the '1' in "10" &lt; the '2' in "2"), but the natural comparer only special-cases a number at
    /// the very START of the string, so the group names have to lead with the digit ("2 Show" /
    /// "10 Show", not "Show 2" / "Show 10") for the numeric path to even be reachable. Covers both
    /// tie tiers - equal real dates, and the shared MinValue tier when both groups are never watched.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OrderGroupKeys_TiesBreakAlphabetically_UsingTheNaturalNumericAwareComparer(bool tiedOnARealDate)
    {
        var show2 = TestItems.Mov("2 Show");
        var show10 = TestItems.Mov("10 Show");

        var order = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" };
        if (tiedOnARealDate)
        {
            var tie = new DateTime(2024, 1, 1);
            order.GroupRecency["2 Show"] = tie;
            order.GroupRecency["10 Show"] = tie;
        }

        order.PreComputePositions(new BaseItem[] { show10, show2 });
        var result = TestItems.Names(order.OrderBy(new BaseItem[] { show10, show2 }));

        Assert.Equal(["2 Show", "10 Show"], result);
        Assert.NotEqual(["10 Show", "2 Show"], result);
    }

    /// <summary>
    /// A held group sorts as MinValue REGARDLESS of its real recency - not merely "before" a
    /// never-watched group by virtue of an earlier date, but genuinely TIED with it and decided
    /// only by the alphabetical tie-break. "Aaa Held Show" carries a very recent real date but is
    /// held; "Zzz Unwatched Show" is absent from GroupRecency entirely. If HeldGroups were ignored,
    /// the held show's real (very recent) date would push it to sort AFTER the absent one instead.
    ///
    /// This calls the protected OrderGroupKeys directly via reflection rather than going through
    /// PreComputePositions: PreComputePositions runs ApplyMidBlockHold first, which unconditionally
    /// clears HeldGroups (by design - see the ApplyMidBlockHold tests below) before recomputing it,
    /// so a HeldGroups entry seeded by hand here would just be wiped out before OrderGroupKeys ever
    /// saw it. Reflection isolates the one piece under test.
    /// </summary>
    [Fact]
    public void OrderGroupKeys_HeldGroup_TiesWithTheNeverWatchedTier_RatherThanUsingItsRealRecency()
    {
        var order = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" };
        order.GroupRecency["Aaa Held Show"] = new DateTime(2026, 1, 1); // very recent
        order.HeldGroups.Add("Aaa Held Show");
        // "Zzz Unwatched Show" absent from GroupRecency -> never-watched tier (MinValue).

        var result = InvokeOrderGroupKeys(order, ["Zzz Unwatched Show", "Aaa Held Show"]);

        Assert.Equal(["Aaa Held Show", "Zzz Unwatched Show"], result);
    }

    private static List<string> InvokeOrderGroupKeys(RoundRobinLeastRecentlyWatchedOrder order, IEnumerable<string> keys)
    {
        var method = typeof(RoundRobinLeastRecentlyWatchedOrder).GetMethod(
            "OrderGroupKeys", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Named failure instead of a bare NullReferenceException if the method is ever renamed -
        // reflection turns what would be a compile error into a runtime one, so say which member.
        Assert.NotNull(method);

        return (List<string>)method.Invoke(order, [keys])!;
    }

    // =================================================================================
    // BuildGroupRecencyAndHoldState
    // =================================================================================

    [Fact]
    public void BuildGroupRecencyAndHoldState_TakesTheMaximumLastPlayedDateAcrossItemsInAGroup()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var earlyEpisode = TestItems.Ep("Show A", 1, 1);
        var laterEpisode = TestItems.Ep("Show A", 1, 2);
        TestItems.SeedUserData(cache, earlyEpisode, TestItems.User, played: true, lastPlayed: new DateTime(2024, 1, 1));
        TestItems.SeedUserData(cache, laterEpisode, TestItems.User, played: true, lastPlayed: new DateTime(2024, 6, 1));

        var order = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" };
        order.BuildGroupRecencyAndHoldState(new BaseItem[] { earlyEpisode, laterEpisode }, TestItems.User, TestItems.ThrowingUserData(), cache, logger: null);

        Assert.Equal(new DateTime(2024, 6, 1), order.GroupRecency["Show A"]);
    }

    /// <summary>
    /// THE single most valuable assertion in this file. Jellyfin stamps LastPlayedDate on ANY
    /// playback, so a half-watched episode (Played == false) carries a real, possibly very recent,
    /// timestamp. If that timestamp were allowed to advance recency, stopping partway through an
    /// episode would send the whole show to the BACK of the rotation instead of leaving it at the
    /// front where an unfinished show belongs.
    /// </summary>
    [Fact]
    public void BuildGroupRecencyAndHoldState_OnlyFullyPlayedItems_AdvanceRecency()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var oldButFullyWatched = TestItems.Ep("Show A", 1, 1);
        var recentButHalfWatched = TestItems.Ep("Show B", 1, 1);

        TestItems.SeedUserData(cache, oldButFullyWatched, TestItems.User, played: true, lastPlayed: new DateTime(2020, 1, 1));
        // A very recent timestamp, but Played is still false - the user stopped partway through.
        TestItems.SeedUserData(cache, recentButHalfWatched, TestItems.User, played: false, lastPlayed: new DateTime(2026, 1, 1));

        var order = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" };
        order.BuildGroupRecencyAndHoldState(new BaseItem[] { oldButFullyWatched, recentButHalfWatched }, TestItems.User, TestItems.ThrowingUserData(), cache, logger: null);

        Assert.Equal(new DateTime(2020, 1, 1), order.GroupRecency["Show A"]);
        // If the Played check were dropped, "Show B" would appear here with the 2026 date instead
        // of being absent (never-watched tier, sorts at the FRONT).
        Assert.False(order.GroupRecency.ContainsKey("Show B"));
    }

    /// <summary>
    /// Series/Season/MusicAlbum use the aggregate date over their cached children (mirroring
    /// LastPlayedOrderBase) INSTEAD of their own user-data row - and that aggregate counts even
    /// when the child itself is only half-watched, because it is read straight off LastPlayedDate
    /// with no Played gate (folder Played flags are unreliable, so this path never trusted one to
    /// begin with). The container's own row is a negative cache hit, proving the date comes
    /// entirely from the children.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContainerCases))]
    public void BuildGroupRecencyAndHoldState_ContainerItems_UseTheAggregateDateOverCachedChildren(
        Func<RefreshQueueService.RefreshCache, BaseItem[], BaseItem> buildContainerAndSeedChildren)
    {
        var cache = new RefreshQueueService.RefreshCache();
        var child = TestItems.Ep("Ignored", 1, 1, name: "Child Episode");
        var childDate = new DateTime(2023, 5, 1);
        TestItems.SeedUserData(cache, child, TestItems.User, played: false, lastPlayed: childDate);

        var container = buildContainerAndSeedChildren(cache, new BaseItem[] { child });
        TestItems.SeedNoUserData(cache, container, TestItems.User);

        var order = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" };
        order.BuildGroupRecencyAndHoldState(new BaseItem[] { container }, TestItems.User, TestItems.ThrowingUserData(), cache, logger: null);

        Assert.Equal(childDate, order.GroupRecency[container.Name!]);
    }

    public static IEnumerable<object[]> ContainerCases()
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

    [Fact]
    public void BuildGroupRecencyAndHoldState_IsPerUser_SoTheSameItemProducesDifferentRecencyForADifferentUser()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = TestItems.Mov("Show A");
        TestItems.SeedUserData(cache, movie, TestItems.User, played: true, lastPlayed: new DateTime(2024, 1, 1));
        TestItems.SeedUserData(cache, movie, TestItems.OtherUser, played: true, lastPlayed: new DateTime(2024, 6, 1));

        var order = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" };

        order.BuildGroupRecencyAndHoldState(new BaseItem[] { movie }, TestItems.User, TestItems.ThrowingUserData(), cache, logger: null);
        Assert.Equal(new DateTime(2024, 1, 1), order.GroupRecency["Show A"]);

        order.BuildGroupRecencyAndHoldState(new BaseItem[] { movie }, TestItems.OtherUser, TestItems.ThrowingUserData(), cache, logger: null);
        Assert.Equal(new DateTime(2024, 6, 1), order.GroupRecency["Show A"]);
    }

    [Fact]
    public void BuildGroupRecencyAndHoldState_MissingGroupByField_LeavesRecencyEmpty_AndLogsAWarning()
    {
        var order = new RoundRobinLeastRecentlyWatchedOrder(); // GroupByField never set
        var logger = new CapturingLogger();

        order.BuildGroupRecencyAndHoldState(new BaseItem[] { TestItems.Mov("Whatever") }, TestItems.User, TestItems.ThrowingUserData(), new RefreshQueueService.RefreshCache(), logger);

        Assert.Empty(order.GroupRecency);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void BuildGroupRecencyAndHoldState_NullUserDataManager_LeavesRecencyEmpty_AndLogsAWarning()
    {
        var order = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" };
        var logger = new CapturingLogger();

        order.BuildGroupRecencyAndHoldState(new BaseItem[] { TestItems.Mov("Whatever") }, TestItems.User, null, new RefreshQueueService.RefreshCache(), logger);

        Assert.Empty(order.GroupRecency);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void BuildGroupRecencyAndHoldState_NullUser_LeavesRecencyEmpty_AndLogsAWarning()
    {
        var order = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" };
        var logger = new CapturingLogger();

        order.BuildGroupRecencyAndHoldState(new BaseItem[] { TestItems.Mov("Whatever") }, null!, TestItems.ThrowingUserData(), new RefreshQueueService.RefreshCache(), logger);

        Assert.Empty(order.GroupRecency);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// Each item's work is wrapped in its own try/catch specifically so one bad item (a cache
    /// miss that reaches a throwing manager, a reflection failure, ...) cannot blank out recency
    /// for every other group in the same refresh. "Show B" has no cache entry at all (neither
    /// positive nor negative), so the cache-first lookup genuinely falls through to the manager -
    /// which is the throwing stub - forcing an honest exception instead of a stubbed one.
    /// </summary>
    [Fact]
    public void BuildGroupRecencyAndHoldState_AnItemThatThrows_IsSkipped_ButOtherItemsStillProcess()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var okItem = TestItems.Mov("Show A");
        var badItem = TestItems.Mov("Show B"); // deliberately not seeded: genuine cache miss
        TestItems.SeedUserData(cache, okItem, TestItems.User, played: true, lastPlayed: new DateTime(2024, 3, 1));
        var logger = new CapturingLogger();

        var order = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "SeriesName" };
        var exception = Record.Exception(() =>
            order.BuildGroupRecencyAndHoldState(new BaseItem[] { okItem, badItem }, TestItems.User, TestItems.ThrowingUserData(), cache, logger));

        Assert.Null(exception);
        Assert.Equal(new DateTime(2024, 3, 1), order.GroupRecency["Show A"]);
        Assert.False(order.GroupRecency.ContainsKey("Show B"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void BuildGroupRecencyAndHoldState_ResetsEverything_OnEveryCall_EvenWhenTheFirstCallUsedAirBlocks()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var showA = TestItems.Show("Show A");
        var epA = TestItems.Ep("Show A", 1, 1, aired: new DateTime(2024, 1, 1), show: showA);
        TestItems.SeedUserData(cache, epA, TestItems.User, played: true, lastPlayed: new DateTime(2024, 1, 2));

        var order = new RoundRobinLeastRecentlyWatchedOrder
        {
            GroupByField = "Collections",
            OrderWithinGroupsByAirDate = true,
            CollectionGroupKeys = TestItems.CollectionMap(("Franchise", new BaseItem[] { epA })),
        };

        order.BuildGroupRecencyAndHoldState(new BaseItem[] { epA }, TestItems.User, TestItems.ThrowingUserData(), cache, logger: null);
        Assert.NotNull(order.WatchedByGroup);
        Assert.NotNull(order.UnwatchedCollectionItemIds);
        Assert.True(order.GroupRecency.ContainsKey("Franchise"));

        // Second call: an unrelated group, no air blocks. Every piece of prior state must vanish.
        order.GroupByField = "SeriesName";
        order.OrderWithinGroupsByAirDate = false;
        var showB = TestItems.Mov("Show B");
        TestItems.SeedUserData(cache, showB, TestItems.User, played: true, lastPlayed: new DateTime(2024, 3, 1));

        order.BuildGroupRecencyAndHoldState(new BaseItem[] { showB }, TestItems.User, TestItems.ThrowingUserData(), cache, logger: null);

        Assert.Null(order.WatchedByGroup);
        Assert.Null(order.UnwatchedCollectionItemIds);
        Assert.False(order.GroupRecency.ContainsKey("Franchise"));
        Assert.True(order.GroupRecency.ContainsKey("Show B"));
    }

    /// <summary>
    /// "Unwatched" for the hold mirrors the Playback Status rule: Played unset means unwatched, so
    /// an imported watch state with NO timestamp (Played == true, LastPlayedDate == null) still
    /// counts as watched - it is not "unwatched because it has no timestamp".
    /// </summary>
    [Fact]
    public void BuildGroupRecencyAndHoldState_UnwatchedSemantics_PlayedFlagUnsetMeansUnwatched_ExceptAnImportedWatchWithNoTimestamp()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var neverPlayed = TestItems.Ep("Show A", 1, 1);
        var importedNoTimestamp = TestItems.Ep("Show A", 1, 2);
        TestItems.SeedUserData(cache, neverPlayed, TestItems.User, played: false, lastPlayed: null);
        TestItems.SeedUserData(cache, importedNoTimestamp, TestItems.User, played: true, lastPlayed: null);

        var order = new RoundRobinLeastRecentlyWatchedOrder
        {
            GroupByField = "Collections",
            OrderWithinGroupsByAirDate = true,
            CollectionGroupKeys = TestItems.CollectionMap(("Group", new BaseItem[] { neverPlayed, importedNoTimestamp })),
        };

        order.BuildGroupRecencyAndHoldState(new BaseItem[] { neverPlayed, importedNoTimestamp }, TestItems.User, TestItems.ThrowingUserData(), cache, logger: null);

        Assert.Contains(neverPlayed.Id, order.UnwatchedCollectionItemIds!);
        Assert.DoesNotContain(importedNoTimestamp.Id, order.UnwatchedCollectionItemIds!);
    }

    /// <summary>
    /// The exception to the Played-flag rule above: folder items (Series/Season/...) count as
    /// watched when they have ANY aggregate activity, even with no reliable Played flag of their
    /// own - but with NO aggregate activity, a folder is still unwatched like anything else.
    /// </summary>
    [Fact]
    public void BuildGroupRecencyAndHoldState_UnwatchedSemantics_FolderItems_CountAsWatchedOnlyWhenTheyHaveAggregateActivity()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var activeSeries = TestItems.Show("Active Series");
        var idleSeries = TestItems.Show("Idle Series");
        TestItems.SeedNoUserData(cache, activeSeries, TestItems.User);
        TestItems.SeedNoUserData(cache, idleSeries, TestItems.User);

        var activeChild = TestItems.Ep("Ignored", 1, 1, name: "Active Child");
        TestItems.SeedUserData(cache, activeChild, TestItems.User, played: false, lastPlayed: new DateTime(2023, 1, 1));
        cache.SeriesEpisodes[(activeSeries.Id, TestItems.User.Id)] = new BaseItem[] { activeChild };
        // idleSeries has no cached children at all -> no aggregate activity.

        var order = new RoundRobinLeastRecentlyWatchedOrder
        {
            GroupByField = "Collections",
            OrderWithinGroupsByAirDate = true,
            CollectionGroupKeys = TestItems.CollectionMap(("Group", new BaseItem[] { activeSeries, idleSeries })),
        };

        order.BuildGroupRecencyAndHoldState(new BaseItem[] { activeSeries, idleSeries }, TestItems.User, TestItems.ThrowingUserData(), cache, logger: null);

        Assert.DoesNotContain(activeSeries.Id, order.UnwatchedCollectionItemIds!);
        Assert.Contains(idleSeries.Id, order.UnwatchedCollectionItemIds!);
    }

    // =================================================================================
    // ApplyMidBlockHold
    // =================================================================================

    [Fact]
    public void ApplyMidBlockHold_NoWatchState_NeverHolds()
    {
        var order = new RoundRobinLeastRecentlyWatchedOrder { GroupByField = "Collections" };
        // BuildGroupRecencyAndHoldState never called: WatchedByGroup stays null.

        order.ApplyMidBlockHold(new List<BaseItem> { TestItems.Mov("Anything") }, logger: null);

        Assert.Empty(order.HeldGroups);
    }

    [Fact]
    public void ApplyMidBlockHold_CollectionGroupKeysMissing_NeverHolds()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var (order, anchor, companion) = SetUpCrossoverNight(cache, CompanionState.InProgress);
        // Simulate CollectionGroupKeys becoming unavailable between the recency pass and the hold pass.
        order.CollectionGroupKeys = null;

        order.ApplyMidBlockHold(new List<BaseItem> { anchor, companion }, logger: null);

        Assert.Empty(order.HeldGroups);
    }

    [Fact]
    public void ApplyMidBlockHold_NothingFromTheGroupIsVisible_NeverHolds()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var (order, _, _) = SetUpCrossoverNight(cache, CompanionState.InProgress);

        order.ApplyMidBlockHold(new List<BaseItem> { TestItems.Mov("Unrelated Item") }, logger: null);

        Assert.Empty(order.HeldGroups);
    }

    /// <summary>
    /// The three cases that decide whether a hold fires, tested as near-misses so a broken
    /// "visible AND unwatched" condition (e.g. dropping either half) is caught precisely:
    /// watched-but-visible does not hold (nothing left to watch), unwatched-but-hidden does not
    /// hold (the playlist can't actually play it), and only unwatched-AND-visible holds.
    /// </summary>
    [Theory]
    [InlineData(CompanionState.Watched, true, false)]
    [InlineData(CompanionState.InProgress, false, false)]
    [InlineData(CompanionState.InProgress, true, true)]
    public void ApplyMidBlockHold_FiresOnlyWhenTheAnchorsBlockHasAnUnwatchedAndVisibleItem(
        CompanionState companionState, bool companionVisible, bool expectHold)
    {
        var cache = new RefreshQueueService.RefreshCache();
        var (order, anchor, companion) = SetUpCrossoverNight(cache, companionState);
        var filtered = companionVisible
            ? new List<BaseItem> { anchor, companion }
            : new List<BaseItem> { anchor };

        order.ApplyMidBlockHold(filtered, logger: null);

        Assert.Equal(expectHold, order.HeldGroups.Contains("Crossover Night"));
    }

    /// <summary>
    /// Block topology is the UNION of filtered items and watched items, not just filtered items:
    /// the anchor itself (watched, so hidden by e.g. a "Playback Status is Unwatched" rule) must
    /// still be found and still anchor its block, purely via WatchedByGroup. If topology were
    /// built from "visible" alone, the anchor would never appear in any block and the hold could
    /// never fire even though the companion is genuinely unwatched and visible.
    /// </summary>
    [Fact]
    public void ApplyMidBlockHold_BlockTopologyIsTheUnionOfFilteredAndWatchedItems_SoAHiddenWatchedAnchorStillAnchors()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var (order, _, companion) = SetUpCrossoverNight(cache, CompanionState.InProgress);

        // Only the companion is visible; the anchor is hidden from the playlist entirely.
        order.ApplyMidBlockHold(new List<BaseItem> { companion }, logger: null);

        Assert.Contains("Crossover Night", order.HeldGroups);
    }

    /// <summary>
    /// Both X and Y were (bulk-)marked played at the exact same moment, so the primary
    /// "most recently played" comparison ties. The tie-break must fall to the LATEST air date -
    /// Y's block - or this inspects the wrong block (X's, which has no unwatched member) and the
    /// hold never fires. X is processed first, so a broken tie-break that just keeps the
    /// first-seen candidate would silently reproduce this exact failure mode.
    /// </summary>
    [Fact]
    public void ApplyMidBlockHold_AnchorTieBreak_FallsBackToTheLatestAirDate_WhenLastPlayedIsEqual()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var showA = TestItems.Show("Show A");
        var showB = TestItems.Show("Show B");
        var showC = TestItems.Show("Show C");

        var x = TestItems.Ep("Show A", 1, 1, aired: new DateTime(2024, 1, 1), show: showA);  // earlier air date
        var y = TestItems.Ep("Show B", 1, 1, aired: new DateTime(2024, 2, 1), show: showB);  // later air date - should win the tie
        var z = TestItems.Ep("Show C", 1, 1, aired: new DateTime(2024, 2, 2), show: showC);  // chains into Y's block

        // Deterministic Ids where Y's Id loses an Id-based comparison against X's: this makes the
        // assertion fail reliably if the air-date tier were ever dropped and the comparison fell
        // through to the (still-present) Id tier instead - rather than passing or failing at
        // random depending on Guid.NewGuid()'s output for this run.
        x.Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        y.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");

        var tiedLastPlayed = new DateTime(2024, 6, 1);
        TestItems.SeedUserData(cache, x, TestItems.User, played: true, lastPlayed: tiedLastPlayed);
        TestItems.SeedUserData(cache, y, TestItems.User, played: true, lastPlayed: tiedLastPlayed);
        TestItems.SeedNoUserData(cache, z, TestItems.User); // unwatched

        var order = new RoundRobinLeastRecentlyWatchedOrder
        {
            GroupByField = "Collections",
            OrderWithinGroupsByAirDate = true,
            CollectionGroupKeys = TestItems.CollectionMap(("Marvel Night", new BaseItem[] { x, y, z })),
        };

        order.BuildGroupRecencyAndHoldState(new BaseItem[] { x, y, z }, TestItems.User, TestItems.ThrowingUserData(), cache, logger: null);
        order.ApplyMidBlockHold(new List<BaseItem> { x, y, z }, logger: null);

        Assert.Contains("Marvel Night", order.HeldGroups);
    }

    [Fact]
    public void HeldGroups_IsRecomputedOnEveryCall_SoAPriorHoldNeverLeaksIntoALaterPass()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var (order, anchor, companion) = SetUpCrossoverNight(cache, CompanionState.InProgress);

        order.ApplyMidBlockHold(new List<BaseItem> { anchor, companion }, logger: null);
        Assert.Contains("Crossover Night", order.HeldGroups);

        // Companion no longer visible: this pass should NOT hold, and must not simply keep the
        // previous pass's result around.
        order.ApplyMidBlockHold(new List<BaseItem> { anchor }, logger: null);

        Assert.DoesNotContain("Crossover Night", order.HeldGroups);
    }

    /// <summary>
    /// End-to-end through the public entry point: PreComputePositions must run the hold BEFORE
    /// computing interleave positions. Without the hold, "Old Watched Show" (2023) is less
    /// recently watched than "Crossover Night" (2024, from the anchor) and would rotate first; the
    /// hold must override that and put the held group's anchor first instead.
    /// </summary>
    [Fact]
    public void PreComputePositions_AppliesTheHoldBeforeComputingPositions()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var (order, anchor, companion) = SetUpCrossoverNight(cache, CompanionState.InProgress);

        var oldShow = TestItems.Show("Old Watched Show");
        var oldEpisode = TestItems.Ep("Old Watched Show", 1, 1, aired: new DateTime(2000, 1, 1), show: oldShow);
        TestItems.SeedUserData(cache, oldEpisode, TestItems.User, played: true, lastPlayed: new DateTime(2023, 1, 1));

        var allItems = new List<BaseItem> { anchor, companion, oldEpisode };
        // Rebuild recency/hold-state over the full pool now that "Old Watched Show" is in play -
        // mirrors production, where this always runs before PreComputePositions.
        order.BuildGroupRecencyAndHoldState(allItems, TestItems.User, TestItems.ThrowingUserData(), cache, logger: null);

        order.PreComputePositions(allItems);

        var firstItem = allItems.OrderBy(i => order.ItemPositions[i.Id]).First();
        Assert.Equal(anchor.Id, firstItem.Id);
    }
}
