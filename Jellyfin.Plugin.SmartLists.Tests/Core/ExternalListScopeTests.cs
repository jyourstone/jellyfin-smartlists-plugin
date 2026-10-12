using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.ExternalList;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Expression = Jellyfin.Plugin.SmartLists.Core.QueryEngine.Expression;
using MediaTypeConstants = Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes;

namespace Jellyfin.Plugin.SmartLists.Tests.Core;

/// <summary>
/// Issue #543: the refresh cache is shared by every list (and bumper pool) refreshed in one queue
/// drain, and keeps every external list fetched along the way. Membership matching used to loop
/// over all of them and keep the lowest position, so External List Order sorted a list by
/// positions from OTHER lists' external lists. Each list must match only its own URLs.
/// </summary>
public class ExternalListScopeTests
{
    private const string McuUrl = "https://www.imdb.com/list/ls539646485/";
    private const string XMenUrl = "https://www.imdb.com/list/ls539959194/";

    private static Movie Imdb(string name, string imdbId)
    {
        var movie = TestItems.Mov(name);
        movie.SetProviderId(MetadataProvider.Imdb, imdbId);
        return movie;
    }

    private static ExternalListResult ListOf(params string[] imdbIds)
    {
        var result = new ExternalListResult { TotalItems = imdbIds.Length };
        for (var i = 0; i < imdbIds.Length; i++)
        {
            result.AddProviderIds(ExternalListItemKind.Movie, imdbIds[i], (string?)null, null, i);
        }

        return result;
    }

    private static SmartList List(Expression rule)
        => new(new SmartPlaylistDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = "List",
            MediaTypes = [MediaTypeConstants.Movie],
            ExpressionSets = [new ExpressionSet { Expressions = [rule] }],
            Order = new OrderDto { SortOptions = [new SortOption { SortBy = "External List Order", SortOrder = SortOrder.Ascending }] },
        });

    private static string[] Filter(SmartList list, RefreshQueueService.RefreshCache cache, params BaseItem[] pool)
    {
        var byId = pool.ToDictionary(i => i.Id);
        return [.. list.FilterPlaylistItems(pool, BaseItem.LibraryManager, TestItems.User, cache).Select(id => byId[id].Name)];
    }

    [Fact]
    public void ListsInOneDrain_SortOnlyByTheirOwnExternalListPositions()
    {
        var ironMan = Imdb("Iron Man", "tt0371746");
        var thor = Imdb("Thor", "tt0800369");
        var deadpool = Imdb("Deadpool", "tt1431045");
        var logan = Imdb("Logan", "tt3315342");

        // Deadpool is last on the MCU list but first on the X-Men list.
        var cache = new RefreshQueueService.RefreshCache();
        cache.ExternalListData[McuUrl] = ListOf("tt0371746", "tt0800369", "tt1431045");
        cache.ExternalListData[XMenUrl] = ListOf("tt1431045", "tt3315342");

        var mcu = List(new Expression("ExternalList", "Equal", McuUrl));
        var xMen = List(new Expression("ExternalList", "Equal", XMenUrl));

        // Same cache, both orders: the second list must not see the first list's positions.
        Assert.Equal(["Deadpool", "Logan"], Filter(xMen, cache, ironMan, thor, deadpool, logan));
        Assert.Equal(["Iron Man", "Thor", "Deadpool"], Filter(mcu, cache, ironMan, thor, deadpool, logan));
        Assert.Equal(["Deadpool", "Logan"], Filter(xMen, cache, ironMan, thor, deadpool, logan));
    }

    [Fact]
    public void MusicLists_MatchAndSortOnlyByTheirOwnTracks()
    {
        const string RockUrl = "https://www.last.fm/user/x/playlists/rock";
        const string ChillUrl = "https://www.last.fm/user/x/playlists/chill";
        var anthem = TestItems.Track("Album", 1, 1, "Anthem", "Band");
        var ballad = TestItems.Track("Album", 1, 2, "Ballad", "Band");
        var shared = TestItems.Track("Album", 1, 3, "Shared", "Band");

        // "Shared" is last on the rock list but first on the chill list.
        var rock = new ExternalListResult();
        rock.AddMusicTrack(null, "Anthem", ["Band"], 0);
        rock.AddMusicTrack(null, "Ballad", ["Band"], 1);
        rock.AddMusicTrack(null, "Shared", ["Band"], 2);
        var chill = new ExternalListResult();
        chill.AddMusicTrack(null, "Shared", ["Band"], 0);

        var cache = new RefreshQueueService.RefreshCache();
        cache.ExternalListData[RockUrl] = rock;
        cache.ExternalListData[ChillUrl] = chill;

        static SmartList MusicList(string url)
            => new(new SmartPlaylistDto
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Music",
                MediaTypes = [MediaTypeConstants.Audio],
                ExpressionSets = [new ExpressionSet { Expressions = [new Expression("ExternalList", "Equal", url)] }],
                Order = new OrderDto { SortOptions = [new SortOption { SortBy = "External List Order", SortOrder = SortOrder.Ascending }] },
            });

        Assert.Equal(["Shared"], Filter(MusicList(ChillUrl), cache, anthem, ballad, shared));
        Assert.Equal(["Anthem", "Ballad", "Shared"], Filter(MusicList(RockUrl), cache, anthem, ballad, shared));

        // The music dedup groups by these positions, so they must carry this list's URL only.
        Assert.All(cache.MusicListPositionsByUrl.Values, positions => Assert.Equal([RockUrl], positions.Keys));
    }

    [Fact]
    public void ListWithoutExternalListRules_InheritsNoMatchesFromThePreviousList()
    {
        var deadpool = Imdb("Deadpool", "tt1431045");
        deadpool.Genres = ["Action"];

        var cache = new RefreshQueueService.RefreshCache();
        cache.ExternalListData[XMenUrl] = ListOf("tt1431045");

        Filter(List(new Expression("ExternalList", "Equal", XMenUrl)), cache, deadpool);
        Assert.NotEmpty(cache.ExternalListPositions);

        // A bumper pool or the next list in the drain without ExternalList rules.
        Filter(List(new Expression("Genres", "Contains", "Action")), cache, deadpool);

        Assert.Empty(cache.ActiveExternalLists);
        Assert.Empty(cache.ItemExternalLists);
        Assert.Empty(cache.ExternalListPositions);
    }
}
