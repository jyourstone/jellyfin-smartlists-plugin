using Jellyfin.Plugin.SmartLists.Services.ExternalList;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.ExternalList;

/// <summary>
/// Issue #544: Trakt items record the title and year of the wrapped (or bare) media for Missing items.
/// </summary>
public class TraktListProviderTests
{
    private static ExternalListEntry AddSingle(TraktListItem item)
    {
        var result = new ExternalListResult();
        var position = 0;

        TraktListProvider.AddItemIds(item, result, ref position);

        Assert.Equal(1, position);
        return Assert.Single(result.Entries);
    }

    [Fact]
    public void MovieWrapper_RecordsMovieKindTitleAndYear()
    {
        var entry = AddSingle(new TraktListItem
        {
            Movie = new TraktMedia { Title = "Inception", Year = 2010, Ids = new TraktIds { Imdb = "tt1375666", Tmdb = 27205 } },
        });

        Assert.Equal(new ExternalListEntry(0, ExternalListItemKind.Movie, "tt1375666", "27205", null, "Inception", 2010), entry);
    }

    [Fact]
    public void EpisodeWrapper_WinsOverItsShow()
    {
        var entry = AddSingle(new TraktListItem
        {
            Episode = new TraktMedia { Title = "Pilot", Ids = new TraktIds { Tvdb = 349232 } },
            Show = new TraktMedia { Title = "Breaking Bad", Year = 2008, Ids = new TraktIds { Tvdb = 81189 } },
        });

        Assert.Equal(new ExternalListEntry(0, ExternalListItemKind.Episode, null, null, "349232", "Pilot", null), entry);
    }

    [Fact]
    public void DirectItem_IsUnknownWithItsOwnTitleAndYear()
    {
        // /movies/popular returns bare {title, year, ids} objects.
        var entry = AddSingle(new TraktListItem
        {
            Title = "Dune",
            Year = 2021,
            Ids = new TraktIds { Imdb = "tt1160419", Tmdb = 438631 },
        });

        Assert.Equal(new ExternalListEntry(0, ExternalListItemKind.Unknown, "tt1160419", "438631", null, "Dune", 2021), entry);
    }
}
