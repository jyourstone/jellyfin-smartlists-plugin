using Jellyfin.Plugin.SmartLists.Services.ExternalList;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.ExternalList;

/// <summary>
/// Issue #544: TMDB movies carry title/release_date and TV shows name/first_air_date; Missing items shows either.
/// </summary>
public class TmdbListProviderTests
{
    [Theory]
    [InlineData("Dune", null, "2021-10-22", null, "Dune", 2021)]               // movie: title + release_date
    [InlineData(null, "Severance", null, "2022-02-18", "Severance", 2022)]     // tv: name + first_air_date
    [InlineData("Movie", "Show", "1999-03-31", "2005-01-01", "Movie", 1999)]   // title and release_date preferred
    [InlineData("", "Show", "", "2005-01-01", "Show", 2005)]                   // empty values fall back
    [InlineData("X", null, "", null, "X", null)]                               // empty date
    [InlineData("X", null, "abcd-01-01", null, "X", null)]                     // not a year
    [InlineData("X", null, "202", null, "X", null)]                            // too short
    [InlineData(null, null, null, null, null, null)]
    public void GetTitleAndYear_ReadsMovieAndTvFields(
        string? title, string? name, string? releaseDate, string? firstAirDate, string? expectedTitle, int? expectedYear)
    {
        var item = new TmdbItem { Title = title, Name = name, ReleaseDate = releaseDate, FirstAirDate = firstAirDate };

        Assert.Equal((expectedTitle, expectedYear), TmdbListProvider.GetTitleAndYear(item));
    }
}
