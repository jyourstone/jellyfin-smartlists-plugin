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
}
