using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
/// Only <c>GetItemById(Guid)</c> and the one-argument <c>GetCollectionFolders(BaseItem)</c> are
/// implemented. Every other member throws <see cref="NotSupportedException"/> on purpose: if
/// production code under test ever starts depending on more of the library manager, these tests
/// must fail loudly rather than quietly sort against a default-valued stub.
/// </summary>
public class TestLibraryManager : DispatchProxy
{
    /// <summary>
    /// Id → item, for <c>GetItemById</c>. Process-wide and never cleared - ids are GUIDs, so
    /// entries from different test classes cannot collide, and leaving them in place keeps the
    /// stub safe under xUnit's parallel test collections.
    /// </summary>
    internal static readonly ConcurrentDictionary<Guid, BaseItem> Items = new();

    /// <summary>
    /// Answers <c>GetCollectionFolders(BaseItem)</c>, keyed by the id of the item the resolver
    /// passes as the anchor - i.e. the CHAIN-TOP folder, deliberately NOT the leaf item id.
    ///
    /// This arm exists because a Jellyfin library (a <c>CollectionFolder</c>) is never in an
    /// item's <c>ParentId</c> chain: it hangs off the UserRootFolder as a sibling structure.
    /// A parents-only walk therefore finds season tags but never library tags, so without this
    /// stub the library half of the ancestor walk could not be tested at all.
    /// </summary>
    internal static readonly ConcurrentDictionary<Guid, List<Folder>> CollectionFolders = new();

    /// <summary>
    /// Answers <c>GetVirtualFolders()</c>. Empty by default, so the path-matching supplement in
    /// <c>LibraryManagerHelper.GetLibraryFoldersForItemPath</c> contributes nothing unless a test
    /// deliberately configures a virtual folder (the symlinked-library case).
    /// </summary>
    internal static readonly List<VirtualFolderInfo> VirtualFolders = [];

    /// <summary>
    /// Per-id <c>GetItemById</c> call counter. Keyed by id rather than being a single total so
    /// it stays meaningful under xUnit's parallel test collections: ids are freshly generated
    /// per test, so no other class can perturb the count for the ids one test cares about.
    /// </summary>
    internal static readonly ConcurrentDictionary<Guid, int> GetItemByIdCalls = new();

    /// <summary>Total <c>GetItemById</c> calls recorded for the given ids.</summary>
    internal static int CallsFor(params Guid[] ids)
        => ids.Sum(id => GetItemByIdCalls.TryGetValue(id, out var count) ? count : 0);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "GetItemById" && args is { Length: 1 } && args[0] is Guid id)
        {
            GetItemByIdCalls.AddOrUpdate(id, 1, (_, count) => count + 1);
            return Items.TryGetValue(id, out var item) ? item : null;
        }

        if (targetMethod?.Name == "GetCollectionFolders" && args is { Length: 1 } && args[0] is BaseItem anchor)
        {
            return CollectionFolders.TryGetValue(anchor.Id, out var folders) ? folders : new List<Folder>();
        }

        if (targetMethod?.Name == "GetVirtualFolders" && args is null or { Length: 0 })
        {
            return VirtualFolders.ToList();
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

    /// <summary>
    /// A Season - a container whose recency is aggregated over cached children rather than read
    /// from its own user-data row.
    /// </summary>
    public static Season SeasonOf(string name)
    {
        var season = new Season { Id = Guid.NewGuid(), Name = name };
        season.SortName = name;
        return season;
    }

    /// <summary>
    /// Links <paramref name="child"/> to <paramref name="parent"/> through <c>ParentId</c> - the
    /// only link the ancestor walk follows - and registers BOTH with the library-manager stub so
    /// <c>GetParent()</c> can resolve them.
    ///
    /// Deliberately does NOT touch <c>SeriesId</c>/<c>SeasonId</c>: those drift from the real
    /// tree, and reaching for them instead of the parent chain is exactly what issue #495 was.
    /// </summary>
    public static T Under<T>(T child, BaseItem parent)
        where T : BaseItem
    {
        child.ParentId = parent.Id;
        TestLibraryManager.Items[child.Id] = child;
        TestLibraryManager.Items[parent.Id] = parent;
        return child;
    }

    /// <summary>
    /// A plain physical folder - the <c>/shows</c> or <c>/movies</c> level of the tree, and the
    /// stand-in for a library's CollectionFolder. Returned UNREGISTERED: <see cref="Under{T}"/>
    /// registers it when it is linked into a chain, and library folders are reached through
    /// <see cref="TestLibraryManager.CollectionFolders"/> rather than through <c>GetItemById</c>.
    /// </summary>
    public static Folder PhysicalFolder(string name, params string[] tags)
    {
        var folder = new Folder { Id = Guid.NewGuid(), Name = name };
        folder.SortName = name;

        if (tags.Length > 0)
        {
            folder.Tags = tags;
        }

        return folder;
    }

    /// <summary>A MusicAlbum - the audio equivalent of <see cref="SeasonOf"/>.</summary>
    public static MusicAlbum Album(string name)
    {
        var album = new MusicAlbum { Id = Guid.NewGuid(), Name = name };
        album.SortName = name;
        return album;
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
