using Jellyfin.Plugin.SmartLists.Services.ExternalList;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.ExternalList;

/// <summary>
/// Issue #544: Missing items reports the external-list entries the refresh's matcher finds no library item for.
/// </summary>
public class MissingItemsCalculatorTests
{
    private const ExternalListItemKind Unknown = ExternalListItemKind.Unknown;
    private const ExternalListItemKind MovieKind = ExternalListItemKind.Movie;
    private const ExternalListItemKind ShowKind = ExternalListItemKind.Show;
    private const ExternalListItemKind EpisodeKind = ExternalListItemKind.Episode;

    private static ExternalListEntry Entry(int position, ExternalListItemKind kind, string? imdb = null, string? tmdb = null, string? tvdb = null)
        => new(position, kind, imdb, tmdb, tvdb, null, null);

    private static ExternalListResult ListOf(params ExternalListEntry[] entries)
    {
        var result = new ExternalListResult();
        foreach (var entry in entries)
        {
            result.AddProviderIds(entry.Kind, entry.ImdbId, entry.TmdbId, entry.TvdbId, entry.Position);
        }

        return result;
    }

    private static LibraryItemIds Movie(string? imdb = null, string? tmdb = null, string? tvdb = null) => new(MovieKind, imdb, tmdb, tvdb);

    private static LibraryItemIds Series(string? imdb = null, string? tmdb = null, string? tvdb = null) => new(ShowKind, imdb, tmdb, tvdb);

    private static LibraryItemIds Episode(string? imdb = null, string? tmdb = null, string? tvdb = null) => new(EpisodeKind, imdb, tmdb, tvdb);

    private static int[] MissingPositions(LibraryItemIds[] library, params ExternalListEntry[] entries)
        => MissingItemsCalculator.Compute(ListOf(entries), library).Missing.Select(e => e.Position).ToArray();

    [Fact]
    public void MovieEntry_IsFoundThroughALibraryMovie()
    {
        var (missing, total) = MissingItemsCalculator.Compute(
            ListOf(Entry(0, MovieKind, imdb: "tt1"), Entry(1, MovieKind, imdb: "tt2")),
            [Movie(imdb: "tt1")]);

        Assert.Equal(2, total);
        Assert.Equal("tt2", Assert.Single(missing).ImdbId);
    }

    [Fact]
    public void ShowEntry_IsFoundThroughALibrarySeries()
    {
        Assert.Equal(new[] { 1 }, MissingPositions([Series(tvdb: "81189")], Entry(0, ShowKind, tvdb: "81189"), Entry(1, ShowKind, tvdb: "121361")));
    }

    [Fact]
    public void UntypedEntry_IsFoundThroughAMovieOrAnEpisode()
    {
        Assert.Equal(
            new[] { 2 },
            MissingPositions(
                [Movie(imdb: "tt1"), Episode(imdb: "tt2")],
                Entry(0, Unknown, imdb: "tt1"),
                Entry(1, Unknown, imdb: "tt2"),
                Entry(2, Unknown, imdb: "tt3")));
    }

    [Fact]
    public void EpisodeEntry_IsFoundThroughALibraryEpisode()
    {
        Assert.Equal(new[] { 1 }, MissingPositions([Episode(tvdb: "349232")], Entry(0, EpisodeKind, tvdb: "349232"), Entry(1, EpisodeKind, tvdb: "349233")));
    }

    [Fact]
    public void KindBuckets_StaySeparate()
    {
        // As in a refresh: a typed entry is not satisfied by a library item of another kind with the same ID.
        Assert.Equal(
            new[] { 0, 1, 2, 3 },
            MissingPositions(
                [Series(tmdb: "100"), Movie(tmdb: "200"), Series(tvdb: "300"), new LibraryItemIds(Unknown, "tt4", null, null)],
                Entry(0, MovieKind, tmdb: "100"),
                Entry(1, ShowKind, tmdb: "200"),
                Entry(2, EpisodeKind, tvdb: "300"),
                Entry(3, MovieKind, imdb: "tt4")));
    }

    [Fact]
    public void ItemWhoseIdsHitDifferentEntries_MarksBoth()
    {
        Assert.Empty(MissingPositions([Movie(imdb: "tt1", tmdb: "2")], Entry(0, MovieKind, imdb: "tt1"), Entry(1, MovieKind, tmdb: "2")));
    }

    [Fact]
    public void Duplicates_AreCountedAndReportedOnce()
    {
        var (missing, total) = MissingItemsCalculator.Compute(
            ListOf(Entry(0, MovieKind, imdb: "tt1"), Entry(1, MovieKind, imdb: "tt1"), Entry(2, MovieKind, imdb: "tt2"), Entry(3, MovieKind, imdb: "tt2")),
            [Movie(imdb: "tt2")]);

        Assert.Equal(2, total);
        Assert.Equal(0, Assert.Single(missing).Position);
    }

    [Fact]
    public void OutOfOrderInput_IsReportedByPositionKeepingTheLowestRepeat()
    {
        // Letterboxd adds entries as its concurrent page fetches finish; the result's dictionaries keep
        // the first-added position (5) of TMDB 1, yet the entry at 1 is still matched.
        var entries = new[] { Entry(5, MovieKind, tmdb: "1"), Entry(2, MovieKind, tmdb: "2"), Entry(1, MovieKind, tmdb: "1") };

        Assert.Equal(new[] { 1, 2 }, MissingPositions([], entries));
        Assert.Equal(new[] { 2 }, MissingPositions([Movie(tmdb: "1")], entries));
    }
}
