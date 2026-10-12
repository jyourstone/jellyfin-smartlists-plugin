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
}
