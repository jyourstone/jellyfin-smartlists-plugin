using Jellyfin.Plugin.SmartLists.Services.ExternalList;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.ExternalList;

/// <summary>
/// Issue #544: ExternalListResult.Entries records every AddProviderIds call (with its display title
/// and year) for the Missing items view, while the matching dictionaries behave exactly as before.
/// </summary>
public class ExternalListResultEntriesTests
{
    [Fact]
    public void IntOverload_RecordsInvariantStringIdsTitleAndYear()
    {
        var result = new ExternalListResult();

        result.AddProviderIds(ExternalListItemKind.Movie, "tt0371746", 1726, 80379, 2, "Iron Man", 2008);

        Assert.Equal(
            new ExternalListEntry(2, ExternalListItemKind.Movie, "tt0371746", "1726", "80379", "Iron Man", 2008),
            Assert.Single(result.Entries));
        Assert.Equal(2, result.MovieTmdbIds["1726"]);
        Assert.Equal(2, result.TvdbIds["80379"]);
    }

    [Fact]
    public void IntOverload_TreatsNonPositiveIdsAsAbsent()
    {
        var result = new ExternalListResult();

        result.AddProviderIds(ExternalListItemKind.Show, "tt0944947", 0, -5, 3);

        Assert.Equal(
            new ExternalListEntry(3, ExternalListItemKind.Show, "tt0944947", null, null, null, null),
            Assert.Single(result.Entries));
    }

    [Fact]
    public void StringOverload_TurnsEmptyIdsIntoNull()
    {
        var result = new ExternalListResult();

        result.AddProviderIds(ExternalListItemKind.Unknown, string.Empty, "1726", string.Empty, 1);

        Assert.Equal(
            new ExternalListEntry(1, ExternalListItemKind.Unknown, null, "1726", null, null, null),
            Assert.Single(result.Entries));
    }

    [Fact]
    public void CallWithoutAnyId_AddsNoEntry()
    {
        var result = new ExternalListResult();

        result.AddProviderIds(ExternalListItemKind.Movie, null, (int?)null, null, 0, "Title", 2000);
        result.AddProviderIds(ExternalListItemKind.Movie, string.Empty, string.Empty, string.Empty, 1, "Title", 2000);
        result.AddProviderIds(ExternalListItemKind.Movie, null, 0, 0, 2);

        Assert.Empty(result.Entries);
    }

    [Fact]
    public void Duplicates_AreKeptInEntriesWhileDictionariesKeepTheFirstPosition()
    {
        var result = new ExternalListResult();

        result.AddProviderIds(ExternalListItemKind.Movie, "tt1", (string?)null, null, 0);
        result.AddProviderIds(ExternalListItemKind.Movie, "tt1", (string?)null, null, 1);

        Assert.Equal(new[] { 0, 1 }, result.Entries.Select(e => e.Position));
        Assert.Equal(0, result.ImdbIds["tt1"]);
        Assert.Equal(0, result.MovieImdbIds["tt1"]);
    }

    [Theory]
    [InlineData("  Thor \n", 2011, "Thor", 2011)]
    [InlineData("   ", 0, null, null)]
    [InlineData("", -1, null, null)]
    [InlineData(null, null, null, null)]
    public void Title_IsTrimmed_AndBlankTitleOrNonPositiveYearBecomeNull(string? title, int? year, string? expectedTitle, int? expectedYear)
    {
        var result = new ExternalListResult();

        result.AddProviderIds(ExternalListItemKind.Movie, "tt1", (string?)null, null, 0, title, year);

        var entry = Assert.Single(result.Entries);
        Assert.Equal((expectedTitle, expectedYear), (entry.Title, entry.Year));
    }

    [Fact]
    public void AddMusicTrack_AddsNoEntries()
    {
        var result = new ExternalListResult();

        result.AddMusicTrack("8f3471b5-7e6a-48da-86a9-c1c07a0f47ae", "Song", ["Artist"], 0);

        Assert.Empty(result.Entries);
        Assert.Single(result.MusicRecordingIds);
    }
}
