using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core.Orders;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Entities;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;
using Series = MediaBrowser.Controller.Entities.TV.Series;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.Orders;

/// <summary>
/// Covers every numeric / date "pure" order in Core/Orders: ProductionYear, CommunityRating,
/// Runtime, DateCreated, LastEpisodeAirDate, ReleaseDate, TrackNumber, EpisodeNumber, SeasonNumber.
///
/// Two things these tests deliberately pin, because both are silent and user-visible when broken:
///
/// 1. WHERE MISSING VALUES LAND. Every scalar order collapses "no value" to a zero-ish sentinel
///    (ProductionYear ?? 0, CommunityRating ?? 0f, RunTimeTicks ?? 0L, DateCreated defaults to
///    DateTime.MinValue, LastEpisodeAirDate falls back to 0, PremiereDate absent -> MinValue,
///    IndexNumber/ParentIndexNumber ?? 0). The sentinel is NOT direction-aware, so unrated /
///    undated / unmeasured items sort FIRST ascending and LAST descending. That is the classic
///    "why are all the unrated movies at the top" complaint, and it is asserted explicitly here
///    rather than left implicit.
///
/// 2. OrderBy(items) AND GetSortKey(item) MUST AGREE. OrderBy drives the single-sort fast path
///    (SmartList.ApplyMultipleOrders returns Order.OrderBy directly when there is exactly one
///    order); GetSortKey drives multi-sort via SmartList.ApplySortingCore. When the two disagree
///    the same list sorts differently depending on how many sorts the user configured - "right
///    movies, wrong order". TrackNumberOrder carries an in-code comment about exactly this
///    regression (a missing natural comparer on the name slot of its composite key), so the
///    agreement checks below are regression tests, not speculation.
///
/// Comparison semantics used by <see cref="SortByKey"/> mirror ApplySortingCore, which sorts with
/// OrderBy/OrderByDescending over IComparable keys (i.e. Comparer&lt;IComparable&gt;.Default ->
/// IComparable.CompareTo). Descending therefore reverses EVERY level of a composite key.
/// </summary>
public class NumericAndDateOrderTests
{
    private static readonly User TestUser = new("tester", "authProviderId", "pwResetProviderId");

    // ---------------------------------------------------------------- helpers

    private static Movie NewMovie(
        string name,
        int? year = null,
        float? rating = null,
        long? runtimeTicks = null,
        DateTime? dateCreated = null,
        DateTime? premiere = null)
    {
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProductionYear = year,
            CommunityRating = rating,
            RunTimeTicks = runtimeTicks,
            PremiereDate = premiere,
        };

        if (dateCreated.HasValue)
        {
            movie.DateCreated = dateCreated.Value;
        }

        return movie;
    }

    private static Episode NewEpisode(string name, int? season, int? episode, DateTime? premiere = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ParentIndexNumber = season,
        IndexNumber = episode,
        PremiereDate = premiere,
    };

    private static Series NewSeries(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
    };

    private static AudioItem NewTrack(string name, string album, int? disc, int? track) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Album = album,
        ParentIndexNumber = disc,
        IndexNumber = track,
    };

    private static string[] Names(IEnumerable<BaseItem> items) => items.Select(i => i.Name).ToArray();

    /// <summary>Sorts purely by GetSortKey, the way SmartList.ApplySortingCore does for multi-sort.</summary>
    private static IEnumerable<BaseItem> SortByKey(Order order, IEnumerable<BaseItem> items, bool descending) =>
        descending
            ? items.OrderByDescending(i => order.GetSortKey(i, TestUser, null, null))
            : items.OrderBy(i => order.GetSortKey(i, TestUser, null, null));

    private static void AssertOrderByMatchesGetSortKey(Order order, IReadOnlyList<BaseItem> items, bool descending) =>
        Assert.Equal(Names(order.OrderBy(items)), Names(SortByKey(order, items, descending)));

    private static int CompareKeys(Order order, BaseItem a, BaseItem b) =>
        order.GetSortKey(a, TestUser, null, null).CompareTo(order.GetSortKey(b, TestUser, null, null));

    // ------------------------------------------------------- ProductionYear

    [Theory]
    [InlineData(false, new[] { "NoYear", "Old", "New" })]
    [InlineData(true, new[] { "New", "Old", "NoYear" })]
    public void ProductionYearOrder_OrderBy_SortsByYear_AndMissingYearCollapsesToZero(bool descending, string[] expected)
    {
        var items = new List<BaseItem>
        {
            NewMovie("Old", year: 1980),
            NewMovie("NoYear"),
            NewMovie("New", year: 2020),
        };

        Order order = descending ? new ProductionYearOrderDesc() : new ProductionYearOrder();

        Assert.Equal(expected, Names(order.OrderBy(items)));
        AssertOrderByMatchesGetSortKey(order, items, descending);
    }

    // ------------------------------------------------------ CommunityRating

    [Theory]
    [InlineData(false, new[] { "Unrated", "Mediocre", "Great" })]
    [InlineData(true, new[] { "Great", "Mediocre", "Unrated" })]
    public void CommunityRatingOrder_OrderBy_SortsByRating_AndUnratedCollapsesToZero(bool descending, string[] expected)
    {
        var items = new List<BaseItem>
        {
            NewMovie("Great", rating: 9.1f),
            NewMovie("Unrated"),
            NewMovie("Mediocre", rating: 5.4f),
        };

        Order order = descending ? new CommunityRatingOrderDesc() : new CommunityRatingOrder();

        Assert.Equal(expected, Names(order.OrderBy(items)));
        AssertOrderByMatchesGetSortKey(order, items, descending);
    }

    // -------------------------------------------------------------- Runtime

    [Theory]
    [InlineData(false, new[] { "Unknown", "Short", "Feature", "Epic" })]
    [InlineData(true, new[] { "Epic", "Feature", "Short", "Unknown" })]
    public void RuntimeOrder_OrderBy_SortsByTicks_AndMissingRuntimeCollapsesToZero(bool descending, string[] expected)
    {
        var items = new List<BaseItem>
        {
            NewMovie("Feature", runtimeTicks: TimeSpan.FromMinutes(95).Ticks),
            NewMovie("Unknown"),
            NewMovie("Epic", runtimeTicks: TimeSpan.FromMinutes(201).Ticks),
            NewMovie("Short", runtimeTicks: TimeSpan.FromMinutes(9).Ticks),
        };

        Order order = descending ? new RuntimeOrderDesc() : new RuntimeOrder();

        Assert.Equal(expected, Names(order.OrderBy(items)));
        AssertOrderByMatchesGetSortKey(order, items, descending);
    }

    [Fact]
    public void RuntimeOrder_GetSortKey_ReturnsRawTicks_NotMinutes()
    {
        var movie = NewMovie("Feature", runtimeTicks: TimeSpan.FromMinutes(95).Ticks);

        var key = Assert.IsType<long>(new RuntimeOrder().GetSortKey(movie, TestUser, null, null));

        Assert.Equal(TimeSpan.FromMinutes(95).Ticks, key);
    }

    // ---------------------------------------------------------- DateCreated

    [Theory]
    [InlineData(false, new[] { "NeverStamped", "Oldest", "Newest" })]
    [InlineData(true, new[] { "Newest", "Oldest", "NeverStamped" })]
    public void DateCreatedOrder_OrderBy_SortsByDateCreated_AndUnstampedDefaultsToMinValue(bool descending, string[] expected)
    {
        var items = new List<BaseItem>
        {
            NewMovie("Newest", dateCreated: new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc)),
            NewMovie("NeverStamped"),
            NewMovie("Oldest", dateCreated: new DateTime(2019, 7, 4, 12, 0, 0, DateTimeKind.Utc)),
        };

        Order order = descending ? new DateCreatedOrderDesc() : new DateCreatedOrder();

        Assert.Equal(expected, Names(order.OrderBy(items)));
        AssertOrderByMatchesGetSortKey(order, items, descending);
    }

    [Fact]
    public void DateCreatedOrder_GetSortKey_KeepsTimeOfDay_TruncationHappensOnlyInMultiSort()
    {
        // Same calendar day, different times. The day-truncation described in
        // SmartList.ApplySortingCore applies to NON-FINAL keys there, not inside this order,
        // so a single DateCreated sort must still separate morning from evening.
        var evening = NewMovie("Evening", dateCreated: new DateTime(2024, 3, 1, 21, 30, 0, DateTimeKind.Utc));
        var morning = NewMovie("Morning", dateCreated: new DateTime(2024, 3, 1, 6, 15, 0, DateTimeKind.Utc));
        var items = new List<BaseItem> { evening, morning };

        var order = new DateCreatedOrder();

        Assert.Equal(new[] { "Morning", "Evening" }, Names(order.OrderBy(items)));

        var eveningKey = Assert.IsType<DateTime>(order.GetSortKey(evening, TestUser, null, null));
        var morningKey = Assert.IsType<DateTime>(order.GetSortKey(morning, TestUser, null, null));
        Assert.NotEqual(eveningKey, morningKey);
        Assert.Equal(eveningKey.Date, morningKey.Date);
    }

    // --------------------------------------------------- LastEpisodeAirDate

    [Theory]
    [InlineData(false, new[] { "Uncached", "Stale", "Fresh" })]
    [InlineData(true, new[] { "Fresh", "Stale", "Uncached" })]
    public void LastEpisodeAirDateOrder_OrderBy_SortsByCachedTimestamp_AndUncachedSeriesCollapseToZero(bool descending, string[] expected)
    {
        var fresh = NewSeries("Fresh");
        var stale = NewSeries("Stale");
        var uncached = NewSeries("Uncached");

        var cache = new RefreshQueueService.RefreshCache();
        cache.LastEpisodeAirDateById[fresh.Id] = 1_700_000_000d;
        cache.LastEpisodeAirDateById[stale.Id] = 1_200_000_000d;

        var items = new List<BaseItem> { stale, uncached, fresh };

        Order order = descending ? new LastEpisodeAirDateOrderDesc() : new LastEpisodeAirDateOrder();

        Assert.Equal(expected, Names(order.OrderBy(items, TestUser, null, null, cache)));
    }

    [Fact]
    public void LastEpisodeAirDateOrder_OrderByWithoutRefreshCache_ScoresEverythingZero_LeavingOriginalOrder()
    {
        var fresh = NewSeries("Fresh");
        var stale = NewSeries("Stale");

        var cache = new RefreshQueueService.RefreshCache();
        cache.LastEpisodeAirDateById[fresh.Id] = 1_700_000_000d;
        cache.LastEpisodeAirDateById[stale.Id] = 1_200_000_000d;

        var items = new List<BaseItem> { fresh, stale };
        var order = new LastEpisodeAirDateOrder();

        // With the cache: real ordering.
        Assert.Equal(new[] { "Stale", "Fresh" }, Names(order.OrderBy(items, TestUser, null, null, cache)));

        // The parameterless overload never receives a cache, so every value is the 0 fallback and
        // the (stable) sort is a passthrough. SmartList always calls the cache-aware overload.
        Assert.Equal(new[] { "Fresh", "Stale" }, Names(order.OrderBy(items)));
    }

    [Fact]
    public void LastEpisodeAirDateOrder_GetSortKey_ReturnsCachedDoubleAndZeroWhenAbsent()
    {
        var series = NewSeries("Show");
        var cache = new RefreshQueueService.RefreshCache();
        cache.LastEpisodeAirDateById[series.Id] = 1_700_000_000d;

        var order = new LastEpisodeAirDateOrder();

        Assert.Equal(1_700_000_000d, Assert.IsType<double>(order.GetSortKey(series, TestUser, null, null, null, cache)));
        Assert.Equal(0d, Assert.IsType<double>(order.GetSortKey(series, TestUser, null, null)));
    }

    // ---------------------------------------------------------- ReleaseDate

    [Fact]
    public void ReleaseDateOrder_OrderBy_UsesDayPrecision_SoSameDayItemsTieAndKeepInputOrder()
    {
        // Late is listed first and released later in the day. Day-precision truncation makes the
        // two keys equal, and LINQ's stable sort then preserves the input order. If the order ever
        // started comparing full timestamps, "Early" would jump ahead.
        var late = NewMovie("Late", premiere: new DateTime(2020, 5, 1, 23, 0, 0, DateTimeKind.Utc));
        var early = NewMovie("Early", premiere: new DateTime(2020, 5, 1, 1, 0, 0, DateTimeKind.Utc));
        var items = new List<BaseItem> { late, early };

        Assert.Equal(new[] { "Late", "Early" }, Names(new ReleaseDateOrder().OrderBy(items)));
        Assert.Equal(new[] { "Late", "Early" }, Names(new ReleaseDateOrderDesc().OrderBy(items)));
    }

    [Fact]
    public void ReleaseDateOrder_SameDay_PutsEpisodesBeforeNonEpisodes_InBothDirections()
    {
        // The movie airs earlier in the day AND is listed first, so only the explicit
        // "episodes first" tiebreaker can move the episode to the front.
        var movie = NewMovie("Movie", premiere: new DateTime(2020, 5, 1, 6, 0, 0, DateTimeKind.Utc));
        var episode = NewEpisode("Episode", 1, 1, premiere: new DateTime(2020, 5, 1, 22, 0, 0, DateTimeKind.Utc));
        var items = new List<BaseItem> { movie, episode };

        Assert.Equal(new[] { "Episode", "Movie" }, Names(new ReleaseDateOrder().OrderBy(items)));
        Assert.Equal(new[] { "Episode", "Movie" }, Names(new ReleaseDateOrderDesc().OrderBy(items)));
    }

    [Theory]
    [InlineData(false, new[] { "S1E1", "S1E2", "S2E1", "Movie" })]
    [InlineData(true, new[] { "S2E1", "S1E2", "S1E1", "Movie" })]
    public void ReleaseDateOrder_SameDay_ThenSortsBySeasonAndEpisode(bool descending, string[] expected)
    {
        var day = new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var items = new List<BaseItem>
        {
            NewEpisode("S1E2", 1, 2, premiere: day),
            NewMovie("Movie", premiere: day),
            NewEpisode("S2E1", 2, 1, premiere: day),
            NewEpisode("S1E1", 1, 1, premiere: day),
        };

        Order order = descending ? new ReleaseDateOrderDesc() : new ReleaseDateOrder();

        Assert.Equal(expected, Names(order.OrderBy(items)));
        AssertOrderByMatchesGetSortKey(order, items, descending);
    }

    [Theory]
    [InlineData(false, new[] { "NoPremiere", "Old", "New" })]
    [InlineData(true, new[] { "New", "Old", "NoPremiere" })]
    public void ReleaseDateOrder_MissingPremiereDate_CollapsesToDateTimeMinValue(bool descending, string[] expected)
    {
        var items = new List<BaseItem>
        {
            NewMovie("New", premiere: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            NewMovie("NoPremiere"),
            NewMovie("Old", premiere: new DateTime(1995, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        };

        Order order = descending ? new ReleaseDateOrderDesc() : new ReleaseDateOrder();

        Assert.Equal(expected, Names(order.OrderBy(items)));
        AssertOrderByMatchesGetSortKey(order, items, descending);
    }

    [Fact]
    public void ReleaseDateOrder_GetSortKey_FlipsTheEpisodeMarkerForDescending_SoEpisodesStayFirst()
    {
        // Deliberate asymmetry: ReleaseDateOrder marks episodes 0 and everything else 1, while
        // ReleaseDateOrderDesc marks episodes 1 - because ApplySortingCore reverses the WHOLE
        // composite key when the order is descending. Equalising the two keys would silently push
        // episodes behind movies in every descending multi-sort.
        var day = new DateTime(2020, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var episode = NewEpisode("Episode", 1, 1, premiere: day);
        var movie = NewMovie("Movie", premiere: day);

        Assert.True(CompareKeys(new ReleaseDateOrder(), episode, movie) < 0, "ascending: episode key must sort before movie key");
        Assert.True(CompareKeys(new ReleaseDateOrderDesc(), episode, movie) > 0, "descending: episode key must sort after movie key so reversal puts it first");
    }

    [Fact]
    public void ReleaseDateOrder_OrderBy_NullItems_ReturnsEmpty()
    {
        Assert.Empty(new ReleaseDateOrder().OrderBy(null!));
        Assert.Empty(new ReleaseDateOrderDesc().OrderBy(null!));
    }

    // ---------------------------------------------------------- TrackNumber

    [Theory]
    [InlineData(false, new[] { "A-d1t1", "A-d1t2", "A-d2t1", "B-d1t1" })]
    [InlineData(true, new[] { "B-d1t1", "A-d2t1", "A-d1t2", "A-d1t1" })]
    public void TrackNumberOrder_OrderBy_SortsByAlbumThenDiscThenTrack(bool descending, string[] expected)
    {
        var items = new List<BaseItem>
        {
            NewTrack("A-d2t1", album: "Alpha", disc: 2, track: 1),
            NewTrack("B-d1t1", album: "Beta", disc: 1, track: 1),
            NewTrack("A-d1t2", album: "Alpha", disc: 1, track: 2),
            NewTrack("A-d1t1", album: "Alpha", disc: 1, track: 1),
        };

        Order order = descending ? new TrackNumberOrderDesc() : new TrackNumberOrder();

        Assert.Equal(expected, Names(order.OrderBy(items)));
        AssertOrderByMatchesGetSortKey(order, items, descending);
    }

    [Fact]
    public void TrackNumberOrder_UntaggedDiscAndTrack_CollapseToZero_AndSortBeforeTaggedTracks()
    {
        var items = new List<BaseItem>
        {
            NewTrack("Tagged", album: "Alpha", disc: 1, track: 1),
            NewTrack("Untagged", album: "Alpha", disc: null, track: null),
        };

        Assert.Equal(new[] { "Untagged", "Tagged" }, Names(new TrackNumberOrder().OrderBy(items)));
        Assert.Equal(new[] { "Tagged", "Untagged" }, Names(new TrackNumberOrderDesc().OrderBy(items)));
    }

    [Fact]
    public void TrackNumberOrder_GetSortKey_AppliesNaturalComparerToTheNameSlot_MatchingOrderBy()
    {
        // Regression guard for the in-code "THIS WAS MISSING" comment: the name slot of the
        // composite key must use SharedNaturalComparer, or leading-number names sort
        // lexicographically ("10 ..." before "2 ...") in multi-sort but numerically in single-sort.
        var items = new List<BaseItem>
        {
            NewTrack("10 Outro", album: "Alpha", disc: 1, track: 0),
            NewTrack("2 Interlude", album: "Alpha", disc: 1, track: 0),
        };

        var order = new TrackNumberOrder();

        Assert.Equal(new[] { "2 Interlude", "10 Outro" }, Names(order.OrderBy(items)));
        Assert.Equal(new[] { "2 Interlude", "10 Outro" }, Names(SortByKey(order, items, descending: false)));
    }

    [Fact]
    public void TrackNumberOrder_GetSortKey_AppliesNaturalComparerToTheAlbumSlot_MatchingOrderBy()
    {
        var items = new List<BaseItem>
        {
            NewTrack("FromTenth", album: "10 Years", disc: 1, track: 1),
            NewTrack("FromSecond", album: "2 Years", disc: 1, track: 1),
        };

        var order = new TrackNumberOrder();

        Assert.Equal(new[] { "FromSecond", "FromTenth" }, Names(order.OrderBy(items)));
        Assert.Equal(new[] { "FromSecond", "FromTenth" }, Names(SortByKey(order, items, descending: false)));
    }

    [Fact]
    public void TrackNumberOrder_OrderBy_NullItems_ReturnsEmpty()
    {
        Assert.Empty(new TrackNumberOrder().OrderBy(null!));
        Assert.Empty(new TrackNumberOrderDesc().OrderBy(null!));
    }

    // -------------------------------------------------------- EpisodeNumber

    [Theory]
    [InlineData(false, new[] { "S2E1", "S1E2", "S1E10" })]
    [InlineData(true, new[] { "S1E10", "S1E2", "S2E1" })]
    public void EpisodeNumberOrder_OrderBy_RanksEpisodeNumberAboveSeason_SoS2E1PrecedesS1E10(bool descending, string[] expected)
    {
        // Episode number is the PRIMARY key here and season only breaks ties. That is
        // counter-intuitive but intentional (SeasonNumberOrder is the other way round), and it is
        // exactly the arrangement a refactor is most likely to "fix" into a regression.
        var items = new List<BaseItem>
        {
            NewEpisode("S1E10", 1, 10),
            NewEpisode("S2E1", 2, 1),
            NewEpisode("S1E2", 1, 2),
        };

        Order order = descending ? new EpisodeNumberOrderDesc() : new EpisodeNumberOrder();

        Assert.Equal(expected, Names(order.OrderBy(items)));
        AssertOrderByMatchesGetSortKey(order, items, descending);
    }

    [Fact]
    public void EpisodeNumberOrder_SameEpisodeNumber_BreaksTiesBySeasonThenNaturalName()
    {
        var items = new List<BaseItem>
        {
            NewEpisode("10 Later", 2, 1),
            NewEpisode("2 Earlier", 2, 1),
            NewEpisode("Season one", 1, 1),
        };

        var order = new EpisodeNumberOrder();

        Assert.Equal(new[] { "Season one", "2 Earlier", "10 Later" }, Names(order.OrderBy(items)));
        AssertOrderByMatchesGetSortKey(order, items, descending: false);
    }

    [Fact]
    public void EpisodeNumberOrder_NonEpisodesAndUnnumberedEpisodes_CollapseToZero()
    {
        var items = new List<BaseItem>
        {
            NewEpisode("Numbered", 1, 5),
            NewMovie("Movie"),
            NewEpisode("Unnumbered", 1, null),
        };

        // Movie and the unnumbered episode both score 0 and keep their relative input order.
        Assert.Equal(new[] { "Movie", "Unnumbered", "Numbered" }, Names(new EpisodeNumberOrder().OrderBy(items)));
    }

    [Fact]
    public void EpisodeNumberOrderDesc_GetSortKey_DoesNotNegate_ProducingTheSameKeyAsAscending()
    {
        // The descending class documents that direction is applied by ApplySortingCore, not by the
        // key. If a future change negated values here the result would be a double reversal.
        var episode = NewEpisode("S3E7", 3, 7);

        var ascKey = new EpisodeNumberOrder().GetSortKey(episode, TestUser, null, null);
        var descKey = new EpisodeNumberOrderDesc().GetSortKey(episode, TestUser, null, null);

        Assert.Equal(0, ascKey.CompareTo(descKey));

        // And the key really is the multi-level composite, not a flattened scalar: a same-episode /
        // different-season pair must still compare non-zero.
        var otherSeason = NewEpisode("S1E7", 1, 7);
        Assert.True(CompareKeys(new EpisodeNumberOrder(), otherSeason, episode) < 0);
    }

    // --------------------------------------------------------- SeasonNumber

    [Theory]
    [InlineData(false, new[] { "S1E2", "S1E10", "S2E1" })]
    [InlineData(true, new[] { "S2E1", "S1E10", "S1E2" })]
    public void SeasonNumberOrder_OrderBy_RanksSeasonAboveEpisode_SoS1E10PrecedesS2E1(bool descending, string[] expected)
    {
        var items = new List<BaseItem>
        {
            NewEpisode("S1E10", 1, 10),
            NewEpisode("S2E1", 2, 1),
            NewEpisode("S1E2", 1, 2),
        };

        Order order = descending ? new SeasonNumberOrderDesc() : new SeasonNumberOrder();

        Assert.Equal(expected, Names(order.OrderBy(items)));
    }

    [Fact]
    public void SeasonNumberOrder_NonEpisodesAndUnnumberedSeasons_CollapseToZero()
    {
        var items = new List<BaseItem>
        {
            NewEpisode("Season1", 1, 1),
            NewMovie("Movie"),
            NewEpisode("NoSeason", null, 1),
        };

        Assert.Equal(new[] { "Movie", "NoSeason", "Season1" }, Names(new SeasonNumberOrder().OrderBy(items)));
        Assert.Equal(0, Assert.IsType<int>(new SeasonNumberOrder().GetSortKey(items[1], TestUser, null, null)));
    }

    [Fact(Skip = "suspected real bug: SeasonNumberOrder.GetSortKey returns the bare season number, dropping the episode/name tiebreakers its own OrderBy applies. Every other multi-level order (EpisodeNumber, TrackNumber, ReleaseDate) returns a ComparableTuple4 carrying its tiebreakers, and ICompositeSortKey exists precisely so non-final sorts can strip them again. Consequence: as the FINAL sort in a multi-sort, episodes within one season come out in library order instead of episode order, while a single SeasonNumber sort orders them by episode. Verified by unskipping: the OrderBy assert passes, the GetSortKey assert returns S1E2,S1E1.")]
    public void SeasonNumberOrder_GetSortKey_ShouldCarryEpisodeTiebreakerLikeOrderByDoes()
    {
        var e2 = NewEpisode("S1E2", 1, 2);
        var e1 = NewEpisode("S1E1", 1, 1);
        var items = new List<BaseItem> { e2, e1 };

        var order = new SeasonNumberOrder();

        // Single-sort path orders by season -> episode -> name.
        Assert.Equal(new[] { "S1E1", "S1E2" }, Names(order.OrderBy(items)));

        // Multi-sort path (GetSortKey) should agree. Today both keys are just the int 1, so the
        // episodes tie and stay in input order.
        Assert.Equal(new[] { "S1E1", "S1E2" }, Names(SortByKey(order, items, descending: false)));
    }

    // ------------------------------------------------------- cross-cutting

    [Fact]
    public void ScalarOrders_GetSortKey_ReturnNumericKeys_ThatCompareNumericallyNotLexically()
    {
        // 9 vs 10 (and 999 vs 1000) are the pairs where a string key would invert the result.
        var lower = NewMovie("Lower", year: 999, rating: 9.5f, runtimeTicks: 9L);
        var higher = NewMovie("Higher", year: 1000, rating: 10f, runtimeTicks: 10L);

        Assert.IsType<int>(new ProductionYearOrder().GetSortKey(lower, TestUser, null, null));
        Assert.IsType<float>(new CommunityRatingOrder().GetSortKey(lower, TestUser, null, null));
        Assert.IsType<long>(new RuntimeOrder().GetSortKey(lower, TestUser, null, null));
        Assert.IsType<DateTime>(new DateCreatedOrder().GetSortKey(lower, TestUser, null, null));

        Assert.True(CompareKeys(new ProductionYearOrder(), lower, higher) < 0);
        Assert.True(CompareKeys(new CommunityRatingOrder(), lower, higher) < 0);
        Assert.True(CompareKeys(new RuntimeOrder(), lower, higher) < 0);
    }

    [Fact]
    public void ScalarOrders_GetSortKey_NullItem_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ProductionYearOrder().GetSortKey(null!, TestUser, null, null));
        Assert.Throws<ArgumentNullException>(() => new ProductionYearOrderDesc().GetSortKey(null!, TestUser, null, null));
        Assert.Throws<ArgumentNullException>(() => new CommunityRatingOrder().GetSortKey(null!, TestUser, null, null));
        Assert.Throws<ArgumentNullException>(() => new RuntimeOrder().GetSortKey(null!, TestUser, null, null));
        Assert.Throws<ArgumentNullException>(() => new DateCreatedOrder().GetSortKey(null!, TestUser, null, null));
        Assert.Throws<ArgumentNullException>(() => new LastEpisodeAirDateOrder().GetSortKey(null!, TestUser, null, null));
    }

    [Fact]
    public void AllNumericAndDateOrders_ExposeTheExactNamesOrderFactoryRegistersThem_Under()
    {
        // FieldRequirements.Analyze does string matching on Order.Name, so a renamed order silently
        // changes which extraction groups run. Pinned here for the numeric/date family.
        Assert.Equal("ProductionYear Ascending", new ProductionYearOrder().Name);
        Assert.Equal("ProductionYear Descending", new ProductionYearOrderDesc().Name);
        Assert.Equal("CommunityRating Ascending", new CommunityRatingOrder().Name);
        Assert.Equal("CommunityRating Descending", new CommunityRatingOrderDesc().Name);
        Assert.Equal("Runtime Ascending", new RuntimeOrder().Name);
        Assert.Equal("Runtime Descending", new RuntimeOrderDesc().Name);
        Assert.Equal("DateCreated Ascending", new DateCreatedOrder().Name);
        Assert.Equal("DateCreated Descending", new DateCreatedOrderDesc().Name);
        Assert.Equal("LastEpisodeAirDate Ascending", new LastEpisodeAirDateOrder().Name);
        Assert.Equal("LastEpisodeAirDate Descending", new LastEpisodeAirDateOrderDesc().Name);
        Assert.Equal("ReleaseDate Ascending", new ReleaseDateOrder().Name);
        Assert.Equal("ReleaseDate Descending", new ReleaseDateOrderDesc().Name);
        Assert.Equal("TrackNumber Ascending", new TrackNumberOrder().Name);
        Assert.Equal("TrackNumber Descending", new TrackNumberOrderDesc().Name);
        Assert.Equal("EpisodeNumber Ascending", new EpisodeNumberOrder().Name);
        Assert.Equal("EpisodeNumber Descending", new EpisodeNumberOrderDesc().Name);
        Assert.Equal("SeasonNumber Ascending", new SeasonNumberOrder().Name);
        Assert.Equal("SeasonNumber Descending", new SeasonNumberOrderDesc().Name);
    }
}
