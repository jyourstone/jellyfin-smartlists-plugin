using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Expression = Jellyfin.Plugin.SmartLists.Core.QueryEngine.Expression;
using MediaTypeConstants = Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes;

namespace Jellyfin.Plugin.SmartLists.Tests.Core;

/// <summary>
/// Match alternate versions (#538): a movie whose primary version fails the rules is still added when
/// one of its alternate versions matches. The alternate is checked with its own file and stream fields
/// and the primary's metadata, so a version whose own metadata differs cannot flip a metadata rule.
/// Alternates are registered in <see cref="TestLibraryManager"/>; the pool holds primaries only, as on
/// the server, where the user-scoped library query collapses versions.
/// </summary>
public class AlternateVersionMatchingTests
{
    private static Guid[] Filter(SmartList list, params BaseItem[] pool)
        => Filter(list, new RefreshQueueService.RefreshCache(), pool);

    private static Guid[] Filter(SmartList list, RefreshQueueService.RefreshCache cache, params BaseItem[] pool)
        => [.. list.FilterPlaylistItems(pool, BaseItem.LibraryManager, TestItems.User, cache)];

    private static SmartList MovieList(bool matchAlternateVersions, params ExpressionSet[] expressionSets)
        => new(new SmartPlaylistDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Alternate Versions List",
            MediaTypes = [MediaTypeConstants.Movie],
            ExpressionSets = [.. expressionSets],
            MatchAlternateVersions = matchAlternateVersions,
        });

    private static ExpressionSet Block(params Expression[] expressions) => new() { Expressions = [.. expressions] };

    private static Expression Is3D => new("Is3D", "Equal", "true");

    private static Movie Movie(string name, bool is3D = false, string[]? genres = null)
    {
        var movie = TestItems.Mov(name, genres: genres);
        movie.Video3DFormat = is3D ? Video3DFormat.MVC : null;
        TestLibraryManager.Items[movie.Id] = movie;
        return movie;
    }

    /// <summary>A primary with one local alternate version (another file of the same movie).</summary>
    private static Movie WithLocalVersion(Movie primary, Movie version)
    {
        TestLibraryManager.LocalAlternateVersionIds[primary.Id] = [version.Id];
        return primary;
    }

    [Fact]
    public void Off_ChecksOnlyThePrimaryVersion()
    {
        var movie = WithLocalVersion(Movie("Alita"), Movie("Alita", is3D: true));

        Assert.Empty(Filter(MovieList(false, Block(Is3D)), movie));
    }

    [Fact]
    public void On_AddsThePrimaryWhenALocalVersionMatches()
    {
        var version = Movie("Alita", is3D: true);
        var movie = WithLocalVersion(Movie("Alita"), version);
        var flat = Movie("Flat Movie");
        var native3D = Movie("Native 3D", is3D: true);

        var result = Filter(MovieList(true, Block(Is3D)), movie, flat, native3D);

        Assert.Equivalent(new[] { movie.Id, native3D.Id }, result);
        Assert.DoesNotContain(version.Id, result);
    }

    [Fact]
    public void On_LinkedVersionsCountToo()
    {
        var movie = Movie("Merged");
        TestLibraryManager.LinkedAlternateVersions[movie.Id] = [Movie("Merged", is3D: true)];

        Assert.Equal([movie.Id], Filter(MovieList(true, Block(Is3D)), movie));
    }

    [Fact]
    public void On_VersionIsCheckedWithThePrimarysMetadata()
    {
        // The 3D file carries no genres of its own, the movie does
        var actionMovie = WithLocalVersion(Movie("Action", genres: ["Action"]), Movie("Action", is3D: true));

        // The 3D file claims Action, the movie is a drama
        var drama = WithLocalVersion(Movie("Drama", genres: ["Drama"]), Movie("Drama", is3D: true, genres: ["Action"]));

        var list = MovieList(true, Block(new Expression("Genres", "Contains", "Action"), Is3D));

        Assert.Equal([actionMovie.Id], Filter(list, actionMovie, drama));
    }

    [Fact]
    public void On_GroupTrackingRecordsTheGroupsOfEveryVersion()
    {
        // The primary matches the genre group, only the 3D version matches the 3D group
        var movie = WithLocalVersion(Movie("Both", genres: ["Action"]), Movie("Both", is3D: true));

        var list = MovieList(true, Block(Is3D), Block(new Expression("Genres", "Contains", "Action")));
        list.TrackRuleGroups = true;

        Assert.Equal([movie.Id], Filter(list, movie));
        Assert.Equal([0, 1], list.ItemGroupMappings[movie.Id]);
    }

    [Fact]
    public void On_TwoPhaseFilteringChecksTheVersionsOfAPrimaryThatFailsPhase1()
    {
        // Collections is expensive, so the cheap Is 3D rule runs alone in Phase 1 against the 2D primary
        var cache = new RefreshQueueService.RefreshCache();
        var movie = WithLocalVersion(Movie("Shelved"), Movie("Shelved", is3D: true));
        var boxSet = new BoxSet { Id = Guid.NewGuid(), Name = "3D Shelf" };
        boxSet.SortName = boxSet.Name;
        cache.AllCollections = [boxSet];
        cache.CollectionDirectChildren[boxSet.Id] = [movie];
        var rules = Block(new Expression("Collections", "Equal", "3D Shelf"), Is3D);

        Assert.Empty(Filter(MovieList(false, rules), cache, movie));
        Assert.Equal([movie.Id], Filter(MovieList(true, rules), cache, movie));
    }

    /// <summary>
    /// Similar To comparing Audio Languages reads each version's own audio, so it turns on the version
    /// check even without a version-field rule.
    /// </summary>
    [Fact]
    public void On_SimilarToComparingAudioLanguagesChecksVersions()
    {
        var reference = Movie("German Reference");
        TestMediaSourceManager.Streams[reference.Id] = [Audio("ger")];
        var version = Movie("Dubbed");
        TestMediaSourceManager.Streams[version.Id] = [Audio("ger")];
        var movie = WithLocalVersion(Movie("Dubbed"), version);
        TestMediaSourceManager.Streams[movie.Id] = [Audio("eng")];

        SmartList List(bool matchAlternateVersions) => new(new SmartPlaylistDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Similar Audio List",
            MediaTypes = [MediaTypeConstants.Movie],
            ExpressionSets = [Block(new Expression("SimilarTo", "Equal", "German Reference"))],
            SimilarityComparisonFields = ["Audio Languages"],
            MatchAlternateVersions = matchAlternateVersions,
        });

        Assert.DoesNotContain(movie.Id, Filter(List(false), reference, movie));
        Assert.Contains(movie.Id, Filter(List(true), reference, movie));
    }

    private static MediaStream Audio(string language) => new() { Type = MediaStreamType.Audio, Language = language, Index = 1 };

    /// <summary>
    /// DB prefilters query only the primary version, so a video with versions to check survives the
    /// candidate set. Without a version-field rule every version gives the same result, so nothing is checked.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void FilterByCandidateSet_KeepsAVideoWithVersionsOnlyWhenARuleUsesAVersionField(bool versionRule, bool kept)
    {
        var movie = WithLocalVersion(Movie("Prefiltered"), Movie("Prefiltered", is3D: true));
        var list = MovieList(true, Block(versionRule ? Is3D : new Expression("Genres", "Contains", "Action")));
        list.PrepareAlternateVersionMatching([movie], BaseItem.LibraryManager, TestItems.User, null,
            new FieldRequirements(), [], new RefreshQueueService.RefreshCache(), null);

        Assert.Equal(kept, list.FilterByCandidateSet([movie], [], null, "test").Contains(movie));
    }

    [Fact]
    public void VersionFields_AreAllRegisteredRuleFields()
    {
        Assert.All(FieldRegistry.VersionFields, field => Assert.NotNull(FieldRegistry.GetField(field)));
    }

    [Fact]
    public void WithVersionFields_TakesFileAndStreamFieldsFromTheVersionAndKeepsTheRest()
    {
        var primary = new Operand("Movie")
        {
            Genres = ["Action"],
            FileName = "Movie - 2160p.mkv",
            Resolution = "4K",
            Is3D = false,
            AudioLanguages = ["eng"],
        };
        primary.IsFavoriteByUser["user"] = true;
        var version = new Operand("Movie (3D)")
        {
            Genres = [],
            FileName = "Movie - [3D MVC].mkv",
            Resolution = "1080p",
            Is3D = true,
            AudioLanguages = ["eng", "ger"],
        };

        var merged = primary.WithVersionFields(version);

        Assert.Equal("Movie - [3D MVC].mkv", merged.FileName);
        Assert.Equal("1080p", merged.Resolution);
        Assert.True(merged.Is3D);
        Assert.Equal(["eng", "ger"], merged.AudioLanguages);
        Assert.Equal("Movie", merged.Name);
        Assert.Equal(["Action"], merged.Genres);
        Assert.True(merged.GetIsFavoriteByUser("user"));

        // The primary's own operand is left untouched
        Assert.Equal("Movie - 2160p.mkv", primary.FileName);
        Assert.False(primary.Is3D);
    }
}
