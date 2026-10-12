using System.Text.Json;
using Jellyfin.Plugin.SmartLists.Services.ExternalList;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.ExternalList;

/// <summary>
/// Issue #544: ReadTitleAndYear returns nulls for missing or null parts rather than failing the list fetch.
/// </summary>
public class ImdbListProviderTests
{
    [Theory]
    [InlineData("""{"id":"tt1160419","titleText":{"text":"Dune"},"releaseYear":{"year":2021}}""", "Dune", 2021)]
    [InlineData("""{"id":"tt1160419","releaseYear":{"year":2021}}""", null, 2021)]                          // missing titleText
    [InlineData("""{"id":"tt1160419","titleText":{"text":"Dune"},"releaseYear":null}""", "Dune", null)]      // null releaseYear
    [InlineData("""{"id":"tt1160419","titleText":{"text":"Dune"},"releaseYear":{"year":null}}""", "Dune", null)] // null year
    [InlineData("""{"id":"tt1160419","titleText":null,"releaseYear":{"year":"2021"}}""", null, null)]      // null / wrong types
    [InlineData("""{"id":"tt1160419","titleText":{"text":null}}""", null, null)]
    public void ReadTitleAndYear_ReturnsNullsForMissingParts(string json, string? expectedTitle, int? expectedYear)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal((expectedTitle, expectedYear), ImdbListProvider.ReadTitleAndYear(document.RootElement));
    }

    [Theory]
    [InlineData("""{"titleType":{"id":"movie","canHaveEpisodes":false}}""", ExternalListItemKind.Movie)]
    [InlineData("""{"titleType":{"id":"tvMovie","canHaveEpisodes":false}}""", ExternalListItemKind.Movie)]
    [InlineData("""{"titleType":{"id":"tvSeries","canHaveEpisodes":true}}""", ExternalListItemKind.Show)]
    [InlineData("""{"titleType":{"id":"tvMiniSeries","canHaveEpisodes":true}}""", ExternalListItemKind.Show)]
    [InlineData("""{"titleType":{"id":"tvEpisode","canHaveEpisodes":false}}""", ExternalListItemKind.Episode)]
    [InlineData("""{"titleType":{"id":"videoGame","canHaveEpisodes":false}}""", ExternalListItemKind.Unknown)]
    [InlineData("""{"titleType":null}""", ExternalListItemKind.Unknown)]
    [InlineData("""{"id":"tt1160419"}""", ExternalListItemKind.Unknown)]
    public void ReadKind_MapsTitleType(string json, ExternalListItemKind expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, ImdbListProvider.ReadKind(document.RootElement));
    }

    // The IMDb title type is display-only: a library item Jellyfin doesn't class as a movie (a Video in a
    // mixed library) must still match an IMDb entry typed Movie, in rule matching and in Missing items.
    [Fact]
    public void DisplayKind_ShowsInEntriesButDoesNotNarrowMatching()
    {
        var result = new ExternalListResult();
        result.AddProviderIds(ExternalListItemKind.Unknown, "tt0111161", (string?)null, null, 0, "The Shawshank Redemption", 1994, ExternalListItemKind.Movie);

        Assert.Equal(ExternalListItemKind.Movie, Assert.Single(result.Entries).Kind);
        Assert.True(result.TryGetPosition(ExternalListItemKind.Unknown, "tt0111161", null, null, out var position));
        Assert.Equal(0, position);

        var (missing, total) = MissingItemsCalculator.Compute(result, [new LibraryItemIds(ExternalListItemKind.Unknown, "tt0111161", null, null)]);
        Assert.Empty(missing);
        Assert.Equal(1, total);
    }
}
