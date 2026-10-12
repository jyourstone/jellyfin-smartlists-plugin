using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Expression = Jellyfin.Plugin.SmartLists.Core.QueryEngine.Expression;
using MediaTypeConstants = Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes;

namespace Jellyfin.Plugin.SmartLists.Tests.Core;

/// <summary>
/// <see cref="SmartList.TrackRuleGroups"/> lets a caller run ONE merged filter pass and still learn
/// which rule group(s) each returned item matched - bumper pools that take turns between rule groups
/// split the merged result on <see cref="SmartList.ItemGroupMappings"/>. Running one SmartList per
/// group instead is what this replaced: a group whose rules all fail to compile is skipped in the
/// merged pass, but alone in a SmartList it has no rules at all and matches everything.
///
/// Episodes expanded from a Collections-matched Series skip the Collections rule in every group, so
/// their mapping checks each group's Collections rule against the series instead. The expansion
/// tests seed collection membership in the refresh cache and the series' episodes in
/// <see cref="TestLibraryManager.ItemListByParentId"/>.
/// </summary>
public class RuleGroupTrackingTests
{
    private static Guid[] Filter(SmartList list, params BaseItem[] pool)
        => Filter(list, new RefreshQueueService.RefreshCache(), pool);

    private static Guid[] Filter(SmartList list, RefreshQueueService.RefreshCache cache, params BaseItem[] pool)
        => [.. list.FilterPlaylistItems(pool, BaseItem.LibraryManager, TestItems.User, cache)];

    private static SmartList MakeList(string mediaType, bool trackRuleGroups, params ExpressionSet[] expressionSets)
        => new(new SmartPlaylistDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Rule Group Tracking List",
            MediaTypes = [mediaType],
            ExpressionSets = [.. expressionSets],
        })
        {
            TrackRuleGroups = trackRuleGroups,
        };

    private static SmartList MovieList(bool trackRuleGroups, params ExpressionSet[] expressionSets)
        => MakeList(MediaTypeConstants.Movie, trackRuleGroups, expressionSets);

    private static ExpressionSet GenreBlock(string genre)
        => new() { Expressions = [new Expression("Genres", "Contains", genre)] };

    /// <summary>A "Collections Equal X" group with "include episodes within series" on.</summary>
    private static ExpressionSet SeriesCollectionBlock(string collection, int? maxItems = null)
        => new()
        {
            Expressions = [new Expression("Collections", "Equal", collection) { IncludeEpisodesWithinSeries = true }],
            MaxItems = maxItems,
        };

    /// <summary>
    /// A series in the named collection, with its episodes registered for expansion. Membership is
    /// seeded into BOTH AllCollections and CollectionDirectChildren - with AllCollections left null,
    /// extraction queries the library manager, which does not stub that query.
    /// </summary>
    private static (Series Show, Episode[] Episodes) SeriesInCollection(RefreshQueueService.RefreshCache cache, string collection, int episodeCount)
    {
        var show = TestItems.Show(collection + " Show");
        var episodes = Enumerable.Range(1, episodeCount)
            .Select(n => TestItems.Ep(show.Name, 1, n, show: show))
            .ToArray();
        TestLibraryManager.ItemListByParentId[show.Id] = [.. episodes];

        var boxSet = new BoxSet { Id = Guid.NewGuid(), Name = collection };
        boxSet.SortName = collection;
        cache.AllCollections = [.. cache.AllCollections ?? [], boxSet];
        cache.CollectionDirectChildren[boxSet.Id] = [show];

        return (show, episodes);
    }

    [Fact]
    public void Tracking_MapsEachReturnedItemToEveryGroupItMatched()
    {
        var drama = TestItems.Mov("Drama Only", genres: ["Drama"]);
        var comedy = TestItems.Mov("Comedy Only", genres: ["Comedy"]);
        var dramedy = TestItems.Mov("Dramedy", genres: ["Drama", "Comedy"]);
        var action = TestItems.Mov("Action Only", genres: ["Action"]);

        var list = MovieList(trackRuleGroups: true, GenreBlock("Drama"), GenreBlock("Comedy"));

        var result = Filter(list, drama, comedy, dramedy, action);

        Assert.Equal(3, result.Length);
        Assert.DoesNotContain(action.Id, result);
        Assert.Equal([0], list.ItemGroupMappings[drama.Id]);
        Assert.Equal([1], list.ItemGroupMappings[comedy.Id]);
        Assert.Equal([0, 1], list.ItemGroupMappings[dramedy.Id].Order());
        Assert.False(list.ItemGroupMappings.ContainsKey(action.Id));
    }

    // The broken group comes first, so a regression that drops it from the compiled list and shifts
    // later groups down an index would map drama to [0] and fail.
    [Fact]
    public void Tracking_GroupWhoseOnlyRuleFailsToCompile_MatchesNothingAndKeepsLaterIndices()
    {
        var drama = TestItems.Mov("Drama Only", genres: ["Drama"]);
        var action = TestItems.Mov("Action Only", genres: ["Action"]);

        var brokenBlock = new ExpressionSet { Expressions = [new Expression("NoSuchField", "Contains", "anything")] };

        // Precondition: alone in a SmartList the broken group has no rules and matches the whole pool
        Assert.Equal(2, Filter(MovieList(trackRuleGroups: true, brokenBlock), drama, action).Length);

        var list = MovieList(trackRuleGroups: true, brokenBlock, GenreBlock("Drama"));

        var result = Filter(list, drama, action);

        // In the merged pass the broken group is skipped, so it contributes - and maps - nothing
        Assert.Equal([drama.Id], result);
        Assert.Equal([1], list.ItemGroupMappings[drama.Id]);
        Assert.DoesNotContain(list.ItemGroupMappings.Values, groups => groups.Contains(0));
    }

    [Fact]
    public void NoTracking_WithoutPerGroupLimitsOrRuleBlockOrder_RecordsNoMappings()
    {
        var drama = TestItems.Mov("Drama Only", genres: ["Drama"]);
        var comedy = TestItems.Mov("Comedy Only", genres: ["Comedy"]);

        var list = MovieList(trackRuleGroups: false, GenreBlock("Drama"), GenreBlock("Comedy"));

        var result = Filter(list, drama, comedy);

        Assert.Equal(2, result.Length);
        Assert.Empty(list.ItemGroupMappings);
    }

    // Expanded episodes skip the Collections rule in EVERY group; checked against the series instead,
    // each episode maps only to the group whose collection holds its series. Mapped to both groups,
    // both bumper pools would hold the same mixed set and taking turns would do nothing.
    [Fact]
    public void Tracking_ExpandedEpisodesMapOnlyToTheGroupThatExpandedTheirSeries()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var (identsShow, idents) = SeriesInCollection(cache, "Idents", 2);
        var (promosShow, promos) = SeriesInCollection(cache, "Promos", 1);

        var list = MakeList(MediaTypeConstants.Episode, trackRuleGroups: true, SeriesCollectionBlock("Idents"), SeriesCollectionBlock("Promos"));

        var result = Filter(list, cache, identsShow, promosShow);

        Assert.Equal(idents.Concat(promos).Select(e => e.Id).Order(), result.Order());
        Assert.All(idents, e => Assert.Equal([0], list.ItemGroupMappings[e.Id]));
        Assert.All(promos, e => Assert.Equal([1], list.ItemGroupMappings[e.Id]));
    }

    // E02 of the Idents series passes only the Promos group's name rule, and only because the
    // Collections rule is skipped for expanded episodes. Its series is not in Promos, so it must not
    // map to that group - it would join the wrong bumper pool or use the wrong group's Max Items.
    [Fact]
    public void Tracking_ExpandedEpisode_NotMappedToGroupWhoseCollectionsRuleItsSeriesFails()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var (identsShow, idents) = SeriesInCollection(cache, "Idents", 2);

        static ExpressionSet CollectionAndName(string collection, string name) => new()
        {
            Expressions =
            [
                new Expression("Collections", "Equal", collection) { IncludeEpisodesWithinSeries = true },
                new Expression("Name", "Contains", name),
            ],
        };

        var list = MakeList(MediaTypeConstants.Episode, trackRuleGroups: true, CollectionAndName("Idents", "E01"), CollectionAndName("Promos", "E02"));

        Filter(list, cache, identsShow);

        Assert.Equal([0], list.ItemGroupMappings[idents[0].Id]);
        Assert.False(list.ItemGroupMappings.ContainsKey(idents[1].Id));
    }

    // Tracking must not change which episodes match: the untracked pass returns the same episodes.
    [Fact]
    public void NoTracking_ExpandedEpisodes_SameResultAsTracked()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var (identsShow, idents) = SeriesInCollection(cache, "Idents", 2);
        var (promosShow, promos) = SeriesInCollection(cache, "Promos", 1);

        var list = MakeList(MediaTypeConstants.Episode, trackRuleGroups: false, SeriesCollectionBlock("Idents"), SeriesCollectionBlock("Promos"));

        var result = Filter(list, cache, identsShow, promosShow);

        Assert.Equal(idents.Concat(promos).Select(e => e.Id).Order(), result.Order());
        Assert.Empty(list.ItemGroupMappings);
    }

    // Per-group Max Items turns tracking on by itself. Expanded episodes used to have no mapping, so
    // ApplyPerGroupLimits dropped every one of them.
    [Fact]
    public void PerGroupMaxItems_KeepsExpandedEpisodesWithinEachGroupsLimit()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var (identsShow, idents) = SeriesInCollection(cache, "Idents", 3);
        var (promosShow, promos) = SeriesInCollection(cache, "Promos", 3);

        var list = MakeList(MediaTypeConstants.Episode, trackRuleGroups: false, SeriesCollectionBlock("Idents", maxItems: 1), SeriesCollectionBlock("Promos", maxItems: 2));

        var result = Filter(list, cache, identsShow, promosShow);

        Assert.Equal(1, result.Count(idents.Select(e => e.Id).Contains));
        Assert.Equal(2, result.Count(promos.Select(e => e.Id).Contains));
    }
}
