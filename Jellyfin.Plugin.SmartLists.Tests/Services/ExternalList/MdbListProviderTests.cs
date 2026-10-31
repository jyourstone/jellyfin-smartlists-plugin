using System.Text.Json;
using Jellyfin.Plugin.SmartLists.Services.ExternalList;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.ExternalList;

/// <summary>
/// ParseMdbListUrl is internal, reached through the InternalsVisibleTo wiring in
/// Jellyfin.Plugin.SmartLists.csproj. Official lists (issue #525) use a three-segment web URL
/// whose middle segment is a media type, not part of the list slug.
/// </summary>
public class MdbListProviderTests
{
    [Theory]
    [InlineData("https://mdblist.com/lists/jxduffy/star-wars-chronological-order", "jxduffy", "star-wars-chronological-order", null)]
    [InlineData("https://mdblist.com/lists/jxduffy/star-wars-chronological-order/", "jxduffy", "star-wars-chronological-order", null)]  // trailing slash
    [InlineData("https://mdblist.com/lists/jxduffy/star-wars-chronological-order?foo=bar", "jxduffy", "star-wars-chronological-order", null)]
    [InlineData("https://mdblist.com/lists/official/shows/popular", "official", "popular", "show")]
    [InlineData("https://mdblist.com/lists/official/movies/justwatch-streaming-charts", "official", "justwatch-streaming-charts", "movie")]
    [InlineData("https://mdblist.com/lists/official/movies/justwatch-streaming-charts?locale=en_US&rank=1&provider=&genre=", "official", "justwatch-streaming-charts", "movie")]  // web-only filters ignored
    [InlineData("https://MDBLIST.COM/lists/official/SHOWS/popular", "official", "popular", "show")]  // case-insensitive
    [InlineData("https://mdblist.com/lists/official/popular", "official", "popular", null)]  // no media segment: combined list
    [InlineData("https://mdblist.com/lists/jxduffy", null, null, null)]  // missing list name
    [InlineData("https://mdblist.com/lists/", null, null, null)]
    [InlineData("not-a-url", null, null, null)]
    public void ParseMdbListUrl_ExtractsUserListAndOfficialForms(string url, string? username, string? listname, string? mediaType)
    {
        Assert.Equal((username, listname, mediaType), MdbListProvider.ParseMdbListUrl(url));
    }

    // The JustWatch official page is a live chart; its website filters share names with the chart API.
    [Theory]
    [InlineData("https://mdblist.com/lists/official/movies/justwatch-streaming-charts", "")]
    [InlineData("https://mdblist.com/lists/official/movies/justwatch-streaming-charts?locale=en_US&rank=1&provider=&genre=", "&locale=en_US&rank=1")]  // empty values dropped
    [InlineData("https://mdblist.com/lists/official/movies/justwatch-streaming-charts?locale=de_DE&rank=7&provider=nfx&genre=act", "&locale=de_DE&rank=7&provider=nfx&genre=act")]
    [InlineData("https://mdblist.com/lists/official/movies/justwatch-streaming-charts?LOCALE=en_GB&apikey=x&foo=bar", "&locale=en_GB")]  // unknown keys dropped, key case-insensitive
    [InlineData("https://mdblist.com/lists/official/movies/justwatch-streaming-charts?provider=a%26b", "&provider=a%26b")]  // value re-escaped
    [InlineData("not-a-url", "")]
    public void BuildJustWatchChartQuery_ForwardsOnlyKnownNonEmptyFilters(string url, string expected)
    {
        Assert.Equal(expected, MdbListProvider.BuildJustWatchChartQuery(url));
    }

    // Issue #544: each item records its title and release year for the Missing items view.
    [Fact]
    public void AddItemIds_RecordsTitleAndYearAndPrefersTopLevelIds()
    {
        var result = new ExternalListResult();
        var position = 4;
        var item = new MdbListItem
        {
            Title = "Iron Man",
            ReleaseYear = 2008,
            ImdbId = "tt0371746",
            TvdbId = 111,
            Ids = new MdbListItemIds { Imdb = "tt9999999", Tmdb = 1726, Tvdb = 222 },
        };

        MdbListProvider.AddItemIds(item, result, ref position, ExternalListItemKind.Movie);

        Assert.Equal(5, position);
        Assert.Equal(
            new ExternalListEntry(4, ExternalListItemKind.Movie, "tt0371746", "1726", "111", "Iron Man", 2008),
            Assert.Single(result.Entries));
    }

    // Issue #565: an episode-only list comes back with empty movies/shows and an "episodes" array whose
    // entries have no "ids" object; the top-level "id" is the TMDB episode id.
    [Fact]
    public void AddWrapperItems_ReadsEpisodeOnlyList()
    {
        const string json = """
            {
              "movies": [],
              "shows": [],
              "seasons": [],
              "episodes": [
                {"id": 62247, "rank": 1, "show_id": 456, "mediatype": "episode", "imdb_id": null, "tvdb_id": null, "title": "Treehouse of Horror", "release_year": 1990, "season_number": 2, "episode_number": 3, "parent_title": "The Simpsons"},
                {"id": 62266, "rank": 2, "show_id": 456, "mediatype": "episode", "imdb_id": null, "tvdb_id": null, "title": "Treehouse of Horror II", "release_year": 1991, "season_number": 3, "episode_number": 7, "parent_title": "The Simpsons"}
              ],
              "pagination": {"offset": 0, "limit": 1000, "total": 2, "has_more": false}
            }
            """;
        var result = new ExternalListResult();
        var position = 0;

        MdbListProvider.AddWrapperItems(JsonSerializer.Deserialize<MdbListResponse>(json), result, ref position);

        Assert.Equal(2, position);
        Assert.Equal(
            new ExternalListEntry(0, ExternalListItemKind.Episode, null, "62247", null, "Treehouse of Horror", 1990),
            result.Entries[0]);
        Assert.True(result.TryGetPosition(ExternalListItemKind.Episode, null, "62266", null, out var episodePosition));
        Assert.Equal(1, episodePosition);
        Assert.False(result.TryGetPosition(ExternalListItemKind.Movie, null, "62247", null, out _));
    }

    [Fact]
    public void AddWrapperItems_KeepsMoviesShowsAndEpisodesInOrder()
    {
        var wrapper = new MdbListResponse
        {
            Movies = [new MdbListItem { Title = "Iron Man", Ids = new MdbListItemIds { Tmdb = 1726 } }],
            Shows = [new MdbListItem { Title = "Lost", Ids = new MdbListItemIds { Tvdb = 73739 } }],
            Episodes = [new MdbListItem { Id = 62247, Title = "Treehouse of Horror" }],
        };
        var result = new ExternalListResult();
        var position = 0;

        MdbListProvider.AddWrapperItems(wrapper, result, ref position);

        Assert.Equal(
            [(0, ExternalListItemKind.Movie), (1, ExternalListItemKind.Show), (2, ExternalListItemKind.Episode)],
            result.Entries.Select(e => (e.Position, e.Kind)));
    }

    // There is no Season kind to match against, so season rows take no list position.
    [Fact]
    public void AddWrapperItems_SkipsSeasons()
    {
        const string json = """{"seasons": [{"id": 3624, "title": "Season 1"}], "episodes": [{"id": 62247, "title": "Treehouse of Horror"}]}""";
        var result = new ExternalListResult();
        var position = 0;

        MdbListProvider.AddWrapperItems(JsonSerializer.Deserialize<MdbListResponse>(json), result, ref position);

        Assert.Equal(1, position);
        Assert.Equal(ExternalListItemKind.Episode, Assert.Single(result.Entries).Kind);
    }

    [Fact]
    public void AddWrapperItems_NullWrapperAddsNothing()
    {
        var result = new ExternalListResult();
        var position = 0;

        MdbListProvider.AddWrapperItems(null, result, ref position);

        Assert.Equal(0, position);
    }

    // Paging follows next_cursor until it is absent (offset is deprecated). The response shapes are the API's own:
    // a middle page carries next_cursor, the last page drops it.
    [Theory]
    [InlineData("""{"offset": 0, "limit": 1000, "total": 1043, "has_more": true, "next_cursor": "abc"}""", null, null, "abc")]
    [InlineData("""{"offset": 0, "limit": 1000, "total": 1043, "has_more": false}""", null, "abc", null)]  // last page
    [InlineData("""{"has_more": true, "next_cursor": "abc"}""", null, "abc", null)]  // repeated cursor would loop
    [InlineData("""{"has_more": true, "next_cursor": ""}""", null, null, null)]
    [InlineData("""{"has_more": true, "next_cursor": "abc"}""", "xyz", null, "abc")]  // body wins over header
    [InlineData(null, "xyz", "abc", "xyz")]  // flat-array page: header only
    [InlineData(null, null, "abc", null)]
    public void GetNextCursor_FollowsNextCursorUntilAbsent(string? paginationJson, string? headerCursor, string? currentCursor, string? expected)
    {
        var pagination = paginationJson == null ? null : JsonSerializer.Deserialize<MdbListPagination>(paginationJson);

        Assert.Equal(expected, MdbListProvider.GetNextCursor(pagination, headerCursor, currentCursor));
    }

    // The top-level "id" is a TMDB id only for episodes; ids.tmdb wins when both are present.
    [Fact]
    public void AddItemIds_UsesTopLevelIdAsTmdbOnlyForEpisodes()
    {
        var result = new ExternalListResult();
        var position = 0;

        MdbListProvider.AddItemIds(new MdbListItem { Id = 5, ImdbId = "tt0371746" }, result, ref position, ExternalListItemKind.Movie);
        MdbListProvider.AddItemIds(
            new MdbListItem { Id = 2, Ids = new MdbListItemIds { Tmdb = 1 } },
            result, ref position, ExternalListItemKind.Episode);

        Assert.Null(result.Entries[0].TmdbId);
        Assert.Equal("1", result.Entries[1].TmdbId);
    }
}
