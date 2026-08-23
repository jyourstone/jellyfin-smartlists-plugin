using System.Reflection;
using Jellyfin.Plugin.SmartLists.Services.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Globalization;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.Collections;

/// <summary>
/// Covers <see cref="CollectionService.UpdateAggregateMetadata"/> - the roll-up of member
/// genres, studios, official rating and cumulative runtime onto a smart collection's BoxSet.
///
/// Smart collections are metadata-locked by design (#433), which also suppresses Jellyfin's own
/// child aggregation - so unlike the PlaylistService counterpart this roll-up must run DESPITE
/// the lock, and it must report whether anything actually changed so no-op refreshes skip the
/// repository write.
///
/// HARNESS NOTE: <c>BaseItem.UpdateRatingToItems</c> resolves rating scores through the static
/// <c>BaseItem.LocalizationManager</c>, which is null offline - the static ctor installs a stub
/// that scores nothing, so rating aggregation falls back to first-distinct order. Fixtures use a
/// single distinct rating per pass, keeping the outcome independent of score ordering.
/// </summary>
public class CollectionAggregateMetadataTests
{
    static CollectionAggregateMetadataTests()
    {
        BaseItem.LocalizationManager = DispatchProxy.Create<ILocalizationManager, NeutralLocalizationManager>();
    }

    /// <summary>
    /// Answers <c>GetRatingScore</c> with null (no rating system loaded), which is exactly what
    /// the real manager returns for a rating it does not know. Everything else throws so a new
    /// dependency on localization fails loudly instead of returning a default silently.
    /// </summary>
    public class NeutralLocalizationManager : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetRatingScore")
            {
                return null;
            }

            throw new NotSupportedException(
                $"NeutralLocalizationManager: {targetMethod?.Name} is not stubbed. Add it deliberately.");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Fixture builders
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A collection under test. <c>PreferredMetadataCountryCode</c> is set because
    /// <c>UpdateRatingToItems</c> resolves it per rating via <c>GetPreferredMetadataCountryCode()</c>,
    /// whose fallback chain dereferences the unstubbed library manager and config statics offline.
    /// </summary>
    private static BoxSet Collection() => new() { Id = Guid.NewGuid(), Name = "Aggregate Test Collection", PreferredMetadataCountryCode = "US" };

    private static Movie MovieWith(
        string name,
        string[]? genres = null,
        string[]? studios = null,
        long? runtimeTicks = null,
        string? rating = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Genres = genres ?? [],
            Studios = studios ?? [],
            RunTimeTicks = runtimeTicks,
            OfficialRating = rating,
        };

    // ---------------------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void RollsUpGenresStudiosRuntimeAndRatingFromMembers()
    {
        var boxSet = Collection();
        var members = new BaseItem[]
        {
            MovieWith("Terminator", genres: ["Action", "Sci-Fi"], studios: ["Carolco"], runtimeTicks: 100, rating: "R"),
            // "action" duplicates "Action" case-insensitively - first casing must win
            MovieWith("Predator", genres: ["action", "Thriller"], studios: ["20th Century Fox"], runtimeTicks: 50, rating: "R"),
        };

        var changed = CollectionService.UpdateAggregateMetadata(boxSet, members);

        Assert.True(changed);
        Assert.Equal(["Action", "Sci-Fi", "Thriller"], boxSet.Genres);
        Assert.Equal(["20th Century Fox", "Carolco"], boxSet.Studios); // sorted for order-independent change detection
        Assert.Equal(150, boxSet.RunTimeTicks);
        Assert.Equal("R", boxSet.OfficialRating);
    }

    [Fact]
    public void SecondPassWithSameMembersIsNoOp()
    {
        var boxSet = Collection();
        var members = new BaseItem[]
        {
            MovieWith("Conan", genres: ["Adventure"], studios: ["Universal"], runtimeTicks: 75, rating: "PG-13"),
        };

        Assert.True(CollectionService.UpdateAggregateMetadata(boxSet, members));

        // Same members again: nothing changed, so the caller must be told to skip the write
        Assert.False(CollectionService.UpdateAggregateMetadata(boxSet, members));
        Assert.Equal(["Adventure"], boxSet.Genres);
        Assert.Equal(75, boxSet.RunTimeTicks);
        Assert.Equal("PG-13", boxSet.OfficialRating);
    }

    [Fact]
    public void FolderMembersAreExcludedFromRuntimeButNotFromGenres()
    {
        var boxSet = Collection();
        var nested = Collection();
        nested.Genres = ["Documentary"];
        nested.RunTimeTicks = 999;

        var members = new BaseItem[]
        {
            MovieWith("Rocky", genres: ["Drama"], runtimeTicks: 60),
            nested, // BoxSet is a folder: contributes genres, never runtime
        };

        CollectionService.UpdateAggregateMetadata(boxSet, members);

        Assert.Equal(60, boxSet.RunTimeTicks);
        Assert.Equal(["Documentary", "Drama"], boxSet.Genres); // sorted for order-independent change detection
    }

    [Fact]
    public void RunsDespiteMetadataLock()
    {
        // The point of difference vs PlaylistService.UpdateAggregateMetadata: smart collections
        // are ALWAYS IsLocked (#433), and the roll-up must run anyway.
        var boxSet = Collection();
        boxSet.IsLocked = true;

        var changed = CollectionService.UpdateAggregateMetadata(
            boxSet,
            [MovieWith("Rambo", genres: ["War"], runtimeTicks: 40, rating: "R")]);

        Assert.True(changed);
        Assert.Equal(["War"], boxSet.Genres);
        Assert.Equal(40, boxSet.RunTimeTicks);
        Assert.Equal("R", boxSet.OfficialRating);
    }

    [Fact]
    public void EmptyMembersClearAggregatesButKeepRating()
    {
        var boxSet = Collection();
        boxSet.Genres = ["Stale"];
        boxSet.Studios = ["Stale Studio"];
        boxSet.RunTimeTicks = 10;
        boxSet.OfficialRating = "PG";

        var changed = CollectionService.UpdateAggregateMetadata(boxSet, []);

        Assert.True(changed);
        Assert.Empty(boxSet.Genres);
        Assert.Empty(boxSet.Studios);
        Assert.Equal(0, boxSet.RunTimeTicks);
        // UpdateRatingToItems keeps the current rating when no member carries one
        Assert.Equal("PG", boxSet.OfficialRating);
    }

    [Fact]
    public void RatingChangeAloneIsReportedAsChanged()
    {
        var boxSet = Collection();
        var movie = MovieWith("Die Hard", genres: ["Action"], runtimeTicks: 80, rating: "R");
        Assert.True(CollectionService.UpdateAggregateMetadata(boxSet, [movie]));

        // Only the member's rating changes: genres/studios/runtime compare equal, rating must
        // still flip the changed flag on its own
        movie.OfficialRating = "PG-13";
        Assert.True(CollectionService.UpdateAggregateMetadata(boxSet, [movie]));
        Assert.Equal("PG-13", boxSet.OfficialRating);
    }
}
