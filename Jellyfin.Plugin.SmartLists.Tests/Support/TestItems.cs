using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;

namespace Jellyfin.Plugin.SmartLists.Tests.Support;

/// <summary>
/// Stand-in for the server's ILibraryManager, needed because <c>Episode.Series</c> is not a stored
/// property - its getter calls <c>BaseItem.LibraryManager.GetItemById(SeriesId)</c>, which throws
/// <see cref="NullReferenceException"/> when no library manager has been installed.
///
/// That matters for real coverage, not convenience: RoundRobinBase.CompareWithinGroupByAirDate
/// reads <c>episode.Series?.SortName</c> to break same-day ties between episodes of DIFFERENT
/// series - the crossover-night ordering users get by editing a series' Sort Title. Without a
/// library manager that comparison throws, so every air-block test would have to avoid the exact
/// case air blocks exist for.
///
/// Only <c>GetItemById(Guid)</c> is implemented. Every other member throws
/// <see cref="NotSupportedException"/> on purpose: if production code under test ever starts
/// depending on more of the library manager, these tests must fail loudly rather than quietly
/// sort against a default-valued stub.
/// </summary>
public class TestLibraryManager : DispatchProxy
{
    /// <summary>
    /// Id → item, for <c>GetItemById</c>. Process-wide and never cleared - ids are GUIDs, so
    /// entries from different test classes cannot collide, and leaving them in place keeps the
    /// stub safe under xUnit's parallel test collections.
    /// </summary>
    internal static readonly ConcurrentDictionary<Guid, BaseItem> Items = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "GetItemById" && args is { Length: 1 } && args[0] is Guid id)
        {
            return Items.TryGetValue(id, out var item) ? item : null;
        }

        throw new NotSupportedException(
            $"TestLibraryManager: {targetMethod?.Name} is not stubbed. Add it deliberately - see Support/TestItems.cs.");
    }
}

/// <summary>
/// An <see cref="IUserDataManager"/> that throws on every call.
///
/// Production reads user data through <c>UserDataCacheHelper.GetCachedUserData</c>, which checks
/// <c>RefreshCache.UserDataCache</c> first and only falls through to the manager on a miss. Tests
/// seed the cache instead (see <see cref="TestItems.SeedUserData"/>), so a throwing manager both
/// avoids stubbing a 13-member interface AND proves the cache-first path is the one being taken -
/// a silent regression to per-item DB round-trips would fail the test rather than slow production
/// down unnoticed.
///
/// The manager reference itself still has to be non-null: RoundRobinLeastRecentlyWatchedOrder
/// .BuildGroupRecencyAndHoldState bails out early (groups fall back to alphabetical) when it is
/// null, which would make the recency tests vacuous.
/// </summary>
public class ThrowingUserDataManager : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        throw new NotSupportedException(
            $"ThrowingUserDataManager: {targetMethod?.Name} was called - the RefreshCache user-data cache should have answered instead.");
    }
}

/// <summary>
/// Builders for the item shapes the round-robin orders group and interleave.
///
/// Two Jellyfin traps these builders exist to close, both of which throw
/// <see cref="NullReferenceException"/> rather than failing an assertion, and both of which have
/// already cost a debugging session:
///
/// 1. Reading <c>item.SortName</c> that was never assigned throws. Every builder assigns it.
/// 2. Assigning <c>Name</c> RESETS the cached sort name, so <c>Name</c> must be set BEFORE
///    <c>SortName</c>. The object-initializer-then-assign shape below is deliberate.
/// </summary>
public static class TestItems
{
    /// <summary>Installs the library manager stub once per test process, before any test runs.</summary>
    [ModuleInitializer]
    internal static void InstallLibraryManager()
    {
        BaseItem.LibraryManager = DispatchProxy.Create<ILibraryManager, TestLibraryManager>();
    }

    public static readonly User User = new("tester", "authProviderId", "pwResetProviderId");

    /// <summary>A second user, for asserting that per-user state is actually keyed by user.</summary>
    public static readonly User OtherUser = new("other", "authProviderId", "pwResetProviderId");

    public static IUserDataManager ThrowingUserData() => DispatchProxy.Create<IUserDataManager, ThrowingUserDataManager>();

    /// <summary>
    /// A Series registered with the library manager stub, so episodes pointing at it can resolve
    /// <c>episode.Series</c>. <paramref name="sortName"/> is the custom Sort Title users edit to
    /// order a crossover night; it defaults to the name.
    /// </summary>
    public static Series Show(string name, string? sortName = null)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = name };
        series.SortName = sortName ?? name;
        TestLibraryManager.Items[series.Id] = series;
        return series;
    }

    /// <summary>
    /// An Episode. Pass <paramref name="show"/> to attach it to a registered Series (needed for
    /// the same-day cross-series tie-break); otherwise only the denormalized SeriesName is set,
    /// which is what <c>ExtractGroupKey</c> groups on.
    /// </summary>
    public static Episode Ep(
        string seriesName,
        int season,
        int episode,
        DateTime? aired = null,
        string? name = null,
        Series? show = null)
    {
        var item = new Episode
        {
            Id = Guid.NewGuid(),
            Name = name ?? $"{seriesName} S{season:00}E{episode:00}",
            SeriesName = seriesName,
            ParentIndexNumber = season,
            IndexNumber = episode,
            PremiereDate = aired,
        };

        item.SortName = item.Name;

        if (show != null)
        {
            item.SeriesId = show.Id;
        }

        return item;
    }

    /// <summary>An Audio track. Disc is ParentIndexNumber, track is IndexNumber.</summary>
    public static Audio Track(string album, int disc, int track, string? name = null, string? artist = null)
    {
        var item = new Audio
        {
            Id = Guid.NewGuid(),
            Name = name ?? $"{album} D{disc}T{track:00}",
            Album = album,
            ParentIndexNumber = disc,
            IndexNumber = track,
        };

        item.SortName = item.Name;

        if (artist != null)
        {
            item.Artists = [artist];
        }

        return item;
    }

    /// <summary>A Movie - the generic non-episode, non-audio item.</summary>
    public static Movie Mov(string name, DateTime? aired = null, string? sortName = null, string[]? genres = null, string[]? studios = null)
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = name, PremiereDate = aired };
        item.SortName = sortName ?? name;

        if (genres != null)
        {
            item.Genres = genres;
        }

        if (studios != null)
        {
            item.Studios = studios;
        }

        return item;
    }

    public static string[] Names(IEnumerable<BaseItem> items) => items.Select(i => i.Name).ToArray();

    /// <summary>
    /// Seeds one item's per-user watch state into the refresh cache, the way a real refresh would
    /// after its first read. <paramref name="lastPlayed"/> null means "no timestamp".
    /// </summary>
    public static void SeedUserData(
        RefreshQueueService.RefreshCache cache,
        BaseItem item,
        User user,
        bool played = false,
        DateTime? lastPlayed = null)
    {
        cache.UserDataCache[(item.Id, user.Id)] = new UserItemData
        {
            Key = item.Id.ToString("N"),
            Played = played,
            LastPlayedDate = lastPlayed,
        };
    }

    /// <summary>
    /// Records that an item has no user-data row at all, which is distinct from having a row with
    /// Played=false: production memoizes the miss in a separate negative cache.
    /// </summary>
    public static void SeedNoUserData(RefreshQueueService.RefreshCache cache, BaseItem item, User user)
    {
        cache.UserDataNegativeCache[(item.Id, user.Id)] = 0;
    }

    /// <summary>
    /// Builds the item id → collection name map SmartList hands the order for "Collections"
    /// grouping. Items not passed here are absent from the map on purpose - production falls back
    /// to series-name grouping for them.
    /// </summary>
    public static Dictionary<Guid, string> CollectionMap(params (string Collection, BaseItem[] Items)[] groups)
    {
        var map = new Dictionary<Guid, string>();
        foreach (var (collection, items) in groups)
        {
            foreach (var item in items)
            {
                map[item.Id] = collection;
            }
        }

        return map;
    }
}
