using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core.Orders;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.Orders;

/// <summary>
/// Covers ResolutionOrder / ResolutionOrderDesc - the sort config-core.js SORT_OPTIONS has always
/// offered and the docs have always documented, but which had no order class at all until now
/// (CreateOrder("Resolution Ascending") fell through to NoOrder, so the list came out sorted by
/// Name with no error anywhere - see OrderFactoryTests).
///
/// Two properties these tests pin, because both are silent and user-visible when broken:
///
/// 1. IT SORTS BY HEIGHT, NOT BY THE LABEL. The rule engine turns a max video height into a label
///    ("480p"/"720p"/"1080p"/"1440p"/"4K"/"8K"). Sorting on that string would order them
///    "1080p" &lt; "1440p" &lt; "480p" &lt; "4K" &lt; "720p" - i.e. 480p in the middle and 4K below
///    720p. <see cref="OrderBy_Ascending_SortsByPixelHeight_NotByTheLabelString"/> asserts an
///    order that only a numeric key can produce.
///
/// 2. OrderBy AND GetSortKey MUST AGREE. OrderBy drives the single-sort fast path
///    (SmartList.ApplyMultipleOrders returns Order.OrderBy directly for exactly one order);
///    GetSortKey drives multi-sort via SmartList.ApplySortingCore. A disagreement means the same
///    list sorts differently depending on how many sorts the user configured. SeasonNumberOrder
///    shipped exactly that defect, so the agreement checks here are regression tests.
///
/// Items are bare Movies with their streams seeded straight into
/// RefreshCache.MediaStreamsCache. That is deliberate on two counts: it is the shape production
/// hands the order (Factory.ExtractResolution fills the same cache), and a bare Movie reflects to
/// zero streams on its own - so any non-zero height asserted below proves the cache was actually
/// consulted rather than the streams re-read per item.
/// </summary>
public class ResolutionOrderTests
{
    private static readonly User TestUser = new("tester", "authProviderId", "pwResetProviderId");

    // ---------------------------------------------------------------- helpers

    private static MediaStream VideoStream(int? height) => new() { Type = MediaStreamType.Video, Height = height };

    private static MediaStream AudioStream() => new() { Type = MediaStreamType.Audio };

    /// <summary>Bare item plus a seeded MediaStreamsCache entry - no real stream plumbing needed.</summary>
    private static Movie MovieWithStreams(RefreshQueueService.RefreshCache cache, string name, params MediaStream[] streams)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = name };
        cache.MediaStreamsCache[movie.Id] = streams.Cast<object>().ToList();
        return movie;
    }

    private static string[] Names(IEnumerable<BaseItem> items) => items.Select(i => i.Name).ToArray();

    /// <summary>The path SmartList.ApplyMultipleOrders takes for a single sort.</summary>
    private static string[] SortByOrderBy(Order order, IEnumerable<BaseItem> items, RefreshQueueService.RefreshCache cache) =>
        Names(order.OrderBy(items, TestUser, null, null, cache));

    /// <summary>The path SmartList.ApplySortingCore takes for multi-sort.</summary>
    private static string[] SortByKey(Order order, IEnumerable<BaseItem> items, RefreshQueueService.RefreshCache cache, bool descending) =>
        Names(descending
            ? items.OrderByDescending(i => order.GetSortKey(i, TestUser, null, null, null, cache))
            : items.OrderBy(i => order.GetSortKey(i, TestUser, null, null, null, cache)));

    private static int Height(Order order, BaseItem item, RefreshQueueService.RefreshCache? cache) =>
        Assert.IsType<int>(order.GetSortKey(item, TestUser, null, null, null, cache));

    /// <summary>
    /// One list spanning every resolution tier, built once so each test sorts the same input.
    /// Insertion order is deliberately scrambled so a no-op sort cannot pass by accident.
    /// </summary>
    private static (RefreshQueueService.RefreshCache Cache, List<BaseItem> Items) MixedResolutionLibrary()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var items = new List<BaseItem>
        {
            MovieWithStreams(cache, "FullHD", VideoStream(1080)),
            MovieWithStreams(cache, "UltraHD", VideoStream(2160)),
            MovieWithStreams(cache, "SD", VideoStream(480)),
            MovieWithStreams(cache, "QuadHD", VideoStream(1440)),
            MovieWithStreams(cache, "HD", VideoStream(720)),
        };

        return (cache, items);
    }

    // ------------------------------------------------------------- ordering

    /// <summary>
    /// The expected order here is the whole point of the field: it is achievable with a numeric
    /// key and impossible with the label string, whose alphabetical order would be
    /// FullHD (1080p), QuadHD (1440p), SD (480p), UltraHD (4K), HD (720p).
    /// </summary>
    [Fact]
    public void OrderBy_Ascending_SortsByPixelHeight_NotByTheLabelString()
    {
        var (cache, items) = MixedResolutionLibrary();

        var sorted = SortByOrderBy(new ResolutionOrder(), items, cache);

        Assert.Equal(["SD", "HD", "FullHD", "QuadHD", "UltraHD"], sorted);
        Assert.NotEqual(["FullHD", "QuadHD", "SD", "UltraHD", "HD"], sorted);
    }

    [Fact]
    public void OrderBy_Descending_PutsTheHighestResolutionFirst()
    {
        var (cache, items) = MixedResolutionLibrary();

        var sorted = SortByOrderBy(new ResolutionOrderDesc(), items, cache);

        Assert.Equal(["UltraHD", "QuadHD", "FullHD", "HD", "SD"], sorted);
    }

    /// <summary>
    /// Mirrors Factory.ExtractResolution, which takes the MAX height across video streams: a
    /// remux carrying both a 4K and a downscaled 720p track is a 4K item, not a 720p one.
    /// </summary>
    [Fact]
    public void MultipleVideoStreams_UseTheHighestHeight()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var multiTrack = MovieWithStreams(cache, "MultiTrack", VideoStream(720), VideoStream(2160), VideoStream(1080));
        var single = MovieWithStreams(cache, "Single", VideoStream(1440));

        Assert.Equal(2160, Height(new ResolutionOrder(), multiTrack, cache));
        Assert.Equal(["Single", "MultiTrack"], SortByOrderBy(new ResolutionOrder(), [single, multiTrack], cache));
    }

    /// <summary>Audio and subtitle tracks carry no height and must not be mistaken for video.</summary>
    [Fact]
    public void NonVideoStreams_AreIgnored()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = MovieWithStreams(cache, "Mixed", AudioStream(), VideoStream(720), AudioStream());
        var audioOnly = MovieWithStreams(cache, "AudioOnly", AudioStream(), AudioStream());

        Assert.Equal(720, Height(new ResolutionOrder(), movie, cache));
        Assert.Equal(MediaStreamHelper.UnknownVideoHeight, Height(new ResolutionOrder(), audioOnly, cache));
    }

    // ------------------------------------------------- unknown-height sentinel

    /// <summary>
    /// Three distinct ways to have no readable height - no streams at all, only audio, and a
    /// video stream whose Height is null - must all collapse to the same sentinel, otherwise
    /// items sort into arbitrary buckets depending on which flavour of "unknown" they are.
    /// </summary>
    [Fact]
    public void EveryFlavourOfUnknownHeight_CollapsesToTheSameSentinel()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var order = new ResolutionOrder();

        var noStreams = MovieWithStreams(cache, "NoStreams");
        var audioOnly = MovieWithStreams(cache, "AudioOnly", AudioStream());
        var nullHeight = MovieWithStreams(cache, "NullHeight", VideoStream(null));

        Assert.Equal(MediaStreamHelper.UnknownVideoHeight, Height(order, noStreams, cache));
        Assert.Equal(MediaStreamHelper.UnknownVideoHeight, Height(order, audioOnly, cache));
        Assert.Equal(MediaStreamHelper.UnknownVideoHeight, Height(order, nullHeight, cache));
    }

    /// <summary>
    /// The sentinel is 0 and is NOT direction-aware, matching every other scalar order in
    /// Core/Orders (ProductionYear ?? 0, CommunityRating ?? 0f, RunTimeTicks ?? 0L). So unknown
    /// items sort FIRST ascending and LAST descending. Asserted explicitly rather than left
    /// implicit, because "why are the audiobooks at the top" is the resulting user report.
    /// </summary>
    [Theory]
    [InlineData(false, new[] { "Unknown", "HD", "UltraHD" })]
    [InlineData(true, new[] { "UltraHD", "HD", "Unknown" })]
    public void UnknownHeight_SortsFirstAscending_AndLastDescending(bool descending, string[] expected)
    {
        var cache = new RefreshQueueService.RefreshCache();
        var items = new List<BaseItem>
        {
            MovieWithStreams(cache, "UltraHD", VideoStream(2160)),
            MovieWithStreams(cache, "Unknown", AudioStream()),
            MovieWithStreams(cache, "HD", VideoStream(720)),
        };

        Order order = descending ? new ResolutionOrderDesc() : new ResolutionOrder();

        Assert.Equal(expected, SortByOrderBy(order, items, cache));
    }

    // -------------------------------------------------- OrderBy / GetSortKey

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrderByAndGetSortKey_ProduceTheSameOrdering(bool descending)
    {
        var (cache, items) = MixedResolutionLibrary();
        items.Add(MovieWithStreams(cache, "Unknown", AudioStream()));
        items.Add(MovieWithStreams(cache, "AlsoFullHD", VideoStream(1080)));

        Order order = descending ? new ResolutionOrderDesc() : new ResolutionOrder();

        Assert.Equal(SortByOrderBy(order, items, cache), SortByKey(order, items, cache, descending));
    }

    /// <summary>
    /// Both directions must hand back the SAME key - direction is applied by
    /// SmartList.IsDescendingOrder choosing OrderByDescending, not by negating the key. A
    /// negated key would double-reverse in multi-sort.
    /// </summary>
    [Fact]
    public void GetSortKey_Descending_ReturnsTheSameKeyAsAscending()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = MovieWithStreams(cache, "FullHD", VideoStream(1080));

        Assert.Equal(1080, Height(new ResolutionOrder(), movie, cache));
        Assert.Equal(1080, Height(new ResolutionOrderDesc(), movie, cache));
    }

    /// <summary>
    /// The key must be a bare int. ApplySortingCore rewrites non-final sort keys - DateTime keys
    /// get truncated to day precision, ICompositeSortKey keys get stripped to PrimaryValue - so a
    /// wrapped key would change meaning the moment Resolution stops being the last sort. A plain
    /// int passes through both branches untouched and compares numerically.
    /// </summary>
    [Fact]
    public void GetSortKey_IsAPlainInt_SoApplySortingCoreLeavesItAloneInNonFinalPositions()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = MovieWithStreams(cache, "UltraHD", VideoStream(2160));

        var key = new ResolutionOrder().GetSortKey(movie, TestUser, null, null, null, cache);

        Assert.IsType<int>(key);
        Assert.IsNotAssignableFrom<ICompositeSortKey>(key);
        Assert.Equal(2160, key);
    }

    // --------------------------------------------------------- cache handling

    /// <summary>
    /// GetSortKey documents refreshCache as optional, and the parameterless OrderBy overload
    /// passes null. Sorting must degrade to the sentinel rather than throwing - a throw here
    /// aborts the whole refresh.
    /// </summary>
    [Fact]
    public void NullRefreshCache_DegradesToTheSentinel_WithoutThrowing()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "NoCache" };

        Assert.Equal(MediaStreamHelper.UnknownVideoHeight, Height(new ResolutionOrder(), movie, null));
        Assert.Equal(["NoCache"], Names(new ResolutionOrder().OrderBy([movie])));
    }

    /// <summary>
    /// On a cache miss the derived streams are written back, so a second sort option (or a later
    /// rule) over the same refresh reuses them instead of reflecting over the item again.
    /// </summary>
    [Fact]
    public void CacheMiss_WritesTheStreamsBackIntoTheRefreshCache()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Uncached" };

        Assert.False(cache.MediaStreamsCache.ContainsKey(movie.Id));

        Height(new ResolutionOrder(), movie, cache);

        Assert.True(cache.MediaStreamsCache.ContainsKey(movie.Id));
    }

    /// <summary>
    /// The cached entry is authoritative: a seeded 4K entry on an item that owns no real streams
    /// still sorts as 4K. This is what makes the rest of the file a valid test of the order
    /// rather than of Jellyfin's stream plumbing.
    /// </summary>
    [Fact]
    public void SeededCacheEntry_IsUsedInsteadOfRereadingTheItem()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Seeded" };

        Assert.Equal(MediaStreamHelper.UnknownVideoHeight, Height(new ResolutionOrder(), movie, null));

        cache.MediaStreamsCache[movie.Id] = new List<object> { VideoStream(2160) };

        Assert.Equal(2160, Height(new ResolutionOrder(), movie, cache));
    }
}
