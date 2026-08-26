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
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;

namespace Jellyfin.Plugin.SmartLists.Tests.Core;

/// <summary>
/// Covers <c>GroupIntoCollections</c> - the smart-collection toggle that replaces every matched item
/// with the Jellyfin collections that DIRECTLY contain it, turning an item search into a search that
/// produces collections.
///
/// The semantics pinned here, all of which depend on the step running at exactly one point in
/// <c>FilterPlaylistItems</c> (after container matching / nested-collection appending / media-type
/// expansion, before sorting and before BOTH limit kinds):
///
/// - Items in no collection, and results that are themselves containers, pass through unchanged.
/// - Emitted collections are de-duplicated, so N matched members produce ONE entry.
/// - Episodes and Seasons resolve through their series, which is what a TV collection actually holds.
/// - Direct membership only: a grandparent collection is never emitted, even when the rule that
///   matched the item deliberately used a deeper Collection search depth.
/// - The list's own collection is never emitted, and an item whose only container is that collection
///   stays itself rather than vanishing - or, for an episode/season, falls through to its series.
/// - An emitted collection INHERITS its members' rule-group mappings and best similarity score, so
///   Rule Block Order places it in its members' block and per-block MaxItems keeps it alive.
/// - Because grouping precedes both limiters, a grouped entry is what MaxItems counts.
///
/// HARNESS NOTE: membership is inverted out of <c>RefreshCache.AllCollections</c> +
/// <c>CollectionDirectChildren</c>, and <see cref="SeedCollection"/> always seeds BOTH. That is
/// mandatory, not tidiness: <see cref="TestLibraryManager"/> throws <see cref="NotSupportedException"/>
/// for unstubbed <c>GetItemsResult</c>, and <c>BuildDirectCollectionMembershipIndex</c> swallows the
/// throw and returns an EMPTY index - grouping would then silently no-op against a dead fixture.
/// Rules are cheap (Genres) except where a Collections/SimilarTo rule is the point of the test, and
/// container children are never resolved by reflection because the cache answers first.
/// </summary>
public class GroupIntoCollectionsTests
{
    // ---------------------------------------------------------------------------------------
    // Fixture builders
    // ---------------------------------------------------------------------------------------

    /// <summary>A BoxSet - what Jellyfin calls a collection. Name before SortName, as ever.</summary>
    private static BoxSet CollectionNamed(string name, params string[] genres)
    {
        var boxSet = new BoxSet { Id = Guid.NewGuid(), Name = name, Genres = genres };
        boxSet.SortName = name;
        return boxSet;
    }

    /// <summary>
    /// A collection this plugin generates, identified the way <see cref="ListOrigin"/> identifies
    /// one: by the SmartLists provider-ID tether written at creation. Grouping must skip these.
    /// </summary>
    private static BoxSet SmartCollectionNamed(string name, string listKey)
    {
        var boxSet = CollectionNamed(name);
        boxSet.ProviderIds = new Dictionary<string, string> { ["SmartLists"] = listKey };
        return boxSet;
    }

    private static Movie MovieNamed(string name, params string[] genres)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = name, Genres = genres };
        movie.SortName = name;
        return movie;
    }

    /// <summary>
    /// An Episode tethered to a registered <see cref="Series"/> via <c>SeriesId</c> - the link the
    /// grouping fallback follows, since a TV collection holds the Series and never the episodes.
    /// </summary>
    private static Episode EpisodeOf(Series series, string name, params string[] genres)
    {
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Name = name,
            SeriesName = series.Name,
            SeriesId = series.Id,
            Genres = genres,
        };
        episode.SortName = name;
        return episode;
    }

    /// <summary>
    /// A Season tethered to a registered <see cref="Series"/> via <c>SeriesId</c> - the same link the
    /// Episode fallback follows, and the other arm of the grouping switch.
    /// </summary>
    private static Season SeasonOf(Series series, string name, params string[] genres)
    {
        var season = new Season
        {
            Id = Guid.NewGuid(),
            Name = name,
            SeriesName = series.Name,
            SeriesId = series.Id,
            Genres = genres,
        };
        season.SortName = name;
        return season;
    }

    /// <summary>
    /// Registers a collection AND its direct children. Both halves are required - see the harness
    /// note on the class: seeding only the children leaves <c>AllCollections</c> null, which sends
    /// the index builder to the throwing library manager and silently disables grouping.
    /// </summary>
    private static void SeedCollection(RefreshQueueService.RefreshCache cache, BoxSet boxSet, params BaseItem[] directChildren)
    {
        cache.AllCollections = [.. cache.AllCollections ?? [], boxSet];
        cache.CollectionDirectChildren[boxSet.Id] = directChildren;
    }

    private static ExpressionSet GenreBlock(string genre, int? maxItems = null)
        => new() { Expressions = [new Expression("Genres", "Contains", genre)], MaxItems = maxItems };

    /// <summary>A smart COLLECTION with the grouping toggle on (the only list kind that accepts it).</summary>
    private static SmartList MakeList(
        List<string> mediaTypes,
        List<ExpressionSet> expressionSets,
        bool groupIntoCollections = true,
        bool matchByMembers = false,
        string? orderName = null,
        int? maxItems = null,
        string? jellyfinCollectionId = null)
    {
        var dto = new SmartCollectionDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Grouping Test List",
            MediaTypes = mediaTypes,
            MatchByMembers = matchByMembers,
            GroupIntoCollections = groupIntoCollections,
            MaxItems = maxItems,
            JellyfinCollectionId = jellyfinCollectionId,
            ExpressionSets = expressionSets,
            Order = orderName == null ? null : new OrderDto { Name = orderName },
        };

        return new SmartList(dto);
    }

    private static Guid[] Filter(SmartList list, RefreshQueueService.RefreshCache cache, params BaseItem[] pool)
        => [.. list.FilterPlaylistItems(pool, BaseItem.LibraryManager, TestItems.User, cache)];

    // ---------------------------------------------------------------------------------------
    // The projection itself
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ReplacesMatchedItemsWithTheirCollections()
    {
        // The whole feature in one assertion: the movie matched, but the COLLECTION is the result.
        var movie = MovieNamed("Predator", "Action");
        var box = CollectionNamed("Arnold Movies");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, box, movie);

        var list = MakeList([MediaTypeConstants.Movie], [GenreBlock("Action")]);

        Assert.Equal([box.Id], Filter(list, cache, movie));
    }

    [Fact]
    public void DedupesItemsSharingACollection()
    {
        // Ten matched Marvel movies must produce ONE Marvel collection, not ten copies of it -
        // the emitted-id set is what makes the result a collection list rather than a multiset.
        var predator = MovieNamed("Predator", "Action");
        var commando = MovieNamed("Commando", "Action");
        var box = CollectionNamed("Arnold Movies");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, box, predator, commando);

        var list = MakeList([MediaTypeConstants.Movie], [GenreBlock("Action")]);

        Assert.Equal([box.Id], Filter(list, cache, predator, commando));
    }

    [Fact]
    public void ItemsInNoCollectionPassThrough()
    {
        // Grouping is a projection, not a filter: an item nobody collected is NOT dropped, it stays
        // itself. Asserting both halves in one run also proves the fixture is live - a dead
        // membership index would leave the collected movie as itself too, and the boxed id absent.
        var uncollected = MovieNamed("Lone Ranger", "Action");
        var collected = MovieNamed("Predator", "Action");
        var box = CollectionNamed("Zulu Box");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, box, collected);

        var list = MakeList([MediaTypeConstants.Movie], [GenreBlock("Action")]);

        var result = Filter(list, cache, uncollected, collected);

        // Default sort is Name ascending: "Lone Ranger" before "Zulu Box".
        Assert.Equal([uncollected.Id, box.Id], result);
        Assert.DoesNotContain(collected.Id, result);
    }

    [Fact]
    public void ContainerResultsPassThroughUnchanged()
    {
        // A result that is ALREADY a collection (matched on its own metadata via the Collection
        // media type) must not be collapsed into the collection that contains IT. Without the
        // container guard the box would be replaced by its own parent box, so asserting the parent
        // is absent is what makes this discriminating.
        var taggedBox = CollectionNamed("Curated Action", "Action");
        var parentBox = CollectionNamed("Everything Box");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, taggedBox);               // no members of its own
        SeedCollection(cache, parentBox, taggedBox);    // ...but it IS a member of another collection

        var list = MakeList([MediaTypeConstants.Movie, MediaTypeConstants.Collection], [GenreBlock("Action")]);

        Assert.Equal([taggedBox.Id], Filter(list, cache, taggedBox));
    }

    [Fact]
    public void EpisodesGroupIntoTheirSeriesCollection()
    {
        // Grouping runs AFTER media-type expansion, so an Episodes list holds Episode items - and
        // an episode is never a direct BoxSet member, its SERIES is. Without the series fallback
        // this feature would silently do nothing for every TV library.
        //
        // The second episode's series is in no collection, so it pins that the fallback is a
        // lookup and not a blanket "episodes always become something else".
        var boxedShow = TestItems.Show("Breaking Bad");
        var loneShow = TestItems.Show("Firefly");
        var boxedEpisode = EpisodeOf(boxedShow, "Breaking Bad S01E01", "Drama");
        var loneEpisode = EpisodeOf(loneShow, "Firefly S01E01", "Drama");
        var box = CollectionNamed("Award Winners");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, box, boxedShow);

        var list = MakeList([MediaTypeConstants.Episode], [GenreBlock("Drama")]);

        var result = Filter(list, cache, boxedEpisode, loneEpisode);

        // Name ascending: "Award Winners" before "Firefly S01E01".
        Assert.Equal([box.Id, loneEpisode.Id], result);
        Assert.DoesNotContain(boxedEpisode.Id, result);
    }

    [Fact]
    public void SeasonsGroupIntoTheirSeriesCollection()
    {
        // Season is a collection-only media type in its own right, and a season is no more a direct
        // BoxSet member than an episode is - the collection holds the Series. The Episode test above
        // does not cover this: the two arms of the SeriesId switch are separate, and deleting the
        // Season arm leaves every other test in the suite green.
        var boxedShow = TestItems.Show("Breaking Bad");
        var loneShow = TestItems.Show("Firefly");
        var boxedSeason = SeasonOf(boxedShow, "Breaking Bad Season 1", "Drama");
        var loneSeason = SeasonOf(loneShow, "Firefly Season 1", "Drama");
        var box = CollectionNamed("Award Winners");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, box, boxedShow);

        var list = MakeList([MediaTypeConstants.Season], [GenreBlock("Drama")]);

        var result = Filter(list, cache, boxedSeason, loneSeason);

        // Name ascending: "Award Winners" before "Firefly Season 1".
        Assert.Equal([box.Id, loneSeason.Id], result);
        Assert.DoesNotContain(boxedSeason.Id, result);
    }

    [Fact]
    public void IgnoresCollectionSearchDepth()
    {
        // Direct membership only. The rule deliberately needs depth 2 to match at all - the movie
        // is a direct member of "Trilogy", and only the nested walk lets it see "Franchise" - so
        // the depth setting is provably live for MATCHING while grouping still emits nothing but
        // the direct parent. A grouping step that reused the depth walk would emit both boxes.
        var movie = MovieNamed("Predator", "Action");
        var inner = CollectionNamed("Trilogy");
        var outer = CollectionNamed("Franchise");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, inner, movie);
        SeedCollection(cache, outer, inner);

        var list = MakeList(
            [MediaTypeConstants.Movie],
            [
                new ExpressionSet
                {
                    Expressions = [new Expression("Collections", "Contains", "Franchise") { CollectionSearchDepth = 2 }],
                },
            ]);

        Assert.Equal([inner.Id], Filter(list, cache, movie));
    }

    // ---------------------------------------------------------------------------------------
    // Self-reference guard (#499)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void NeverEmitsTheListsOwnCollection()
    {
        // Two things at once, and the second is the one that is easy to get wrong:
        // - a matched item that also sits in an unrelated collection emits THAT collection only;
        // - a matched item whose ONLY container is the list being built stays ITSELF rather than
        //   disappearing, because "replaced" is only set when a parent actually got emitted.
        var self = CollectionNamed("Action Collection [Smart]");
        var other = CollectionNamed("Manually Curated Action");
        var onlyInSelf = MovieNamed("Commando", "Action");
        var alsoElsewhere = MovieNamed("Predator", "Action");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, self, onlyInSelf, alsoElsewhere);
        SeedCollection(cache, other, alsoElsewhere);

        var list = MakeList(
            [MediaTypeConstants.Movie],
            [GenreBlock("Action")],
            jellyfinCollectionId: self.Id.ToString());

        var result = Filter(list, cache, onlyInSelf, alsoElsewhere);

        Assert.Equal([onlyInSelf.Id, other.Id], result);
        Assert.DoesNotContain(self.Id, result);
    }

    [Fact]
    public void NeverEmitsAnotherSmartCollection()
    {
        // Not just this list's own collection - ANY plugin-generated one. A smart collection is
        // rebuilt on every refresh, so grouping into one makes the result depend on refresh order,
        // and in a library with a few smart lists it buries the real collections in unrelated ones
        // that merely happen to hold a matched item. Verified against the dev library, where an
        // Arnold search returned 4 real collections and 3 unrelated smart ones.
        var curated = CollectionNamed("Predator Collection");
        var otherSmart = SmartCollectionNamed("Unrelated Test List [Smart]", "some-other-list-id");
        var inBoth = MovieNamed("Predator", "Action");
        var onlyInSmart = MovieNamed("Commando", "Action");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, curated, inBoth);
        SeedCollection(cache, otherSmart, inBoth, onlyInSmart);

        var list = MakeList([MediaTypeConstants.Movie], [GenreBlock("Action")]);

        var result = Filter(list, cache, inBoth, onlyInSmart);

        // The curated collection replaces its member; the movie only the smart list holds stays
        // itself rather than collapsing into that smart list.
        Assert.Equal(2, result.Length);
        Assert.Contains(curated.Id, result);
        Assert.Contains(onlyInSmart.Id, result);
        Assert.DoesNotContain(otherSmart.Id, result);
    }

    [Fact]
    public void OwnCollectionDoesNotShadowTheSeriesFallback()
    {
        // The second-refresh case, and the reason the self-reference guard has to run BEFORE the
        // series fallback rather than after it. Refresh #1 found no collection for the episode and
        // wrote it into the list's own collection - so on refresh #2 the episode DOES have a direct
        // parent: this very list. Treating that as a hit would stop the fallback from ever running,
        // and the list would re-emit its own members forever, never picking up the show's new box.
        var self = CollectionNamed("Best Drama TV [Smart]");
        var awardWinners = CollectionNamed("Award Winners");
        var show = TestItems.Show("Breaking Bad");
        var episode = EpisodeOf(show, "Breaking Bad S01E01", "Drama");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, self, episode);          // written by the previous refresh
        SeedCollection(cache, awardWinners, show);     // the collection the user added afterwards

        var list = MakeList(
            [MediaTypeConstants.Episode],
            [GenreBlock("Drama")],
            jellyfinCollectionId: self.Id.ToString());

        var result = Filter(list, cache, episode);

        Assert.Equal([awardWinners.Id], result);
    }

    // ---------------------------------------------------------------------------------------
    // Inheritance - the emitted collection stands in for its members
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void RuleBlockOrder_CollectionInheritsMemberGroups()
    {
        // Rule Block Order sorts by matched block index and puts UNMAPPED items LAST. The grouped
        // collection's contributing item matches block 0 while the ungrouped item matches block 1,
        // so a collection that failed to inherit its member's mapping would sort second instead of
        // first - the assertion flips, rather than merely losing an item.
        var actionMovie = MovieNamed("Predator", "Action");
        var dramaMovie = MovieNamed("The Notebook", "Drama");
        var box = CollectionNamed("Drama Box");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, box, dramaMovie);

        var list = MakeList(
            [MediaTypeConstants.Movie],
            [GenreBlock("Drama"), GenreBlock("Action")],
            orderName: "Rule Block Order Ascending");

        Assert.Equal([box.Id, actionMovie.Id], Filter(list, cache, actionMovie, dramaMovie));
    }

    [Fact]
    public void PerGroupLimits_CollectionSurvivesInItsMembersGroup()
    {
        // ApplyPerGroupLimits rebuilds its result from the group mappings alone and silently DROPS
        // anything unmapped, so this is the strongest indirect proof that the emitted collection
        // really inherited its member's block. Landing in block 0 instead would also kill it: that
        // block's single slot goes to "Alpha Action" on the Name sort.
        var alpha = MovieNamed("Alpha Action", "Action");
        var beta = MovieNamed("Beta Action", "Action");
        var dramaMovie = MovieNamed("Drama Movie", "Drama");
        var boxedDrama = MovieNamed("The Notebook", "Drama");
        var box = CollectionNamed("Zebra Box");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, box, boxedDrama);

        var list = MakeList(
            [MediaTypeConstants.Movie],
            [GenreBlock("Action", maxItems: 1), GenreBlock("Drama")]);

        var result = Filter(list, cache, alpha, beta, dramaMovie, boxedDrama);

        Assert.Equal(3, result.Length);
        Assert.Contains(alpha.Id, result);       // block 0's single slot
        Assert.Contains(dramaMovie.Id, result);
        Assert.Contains(box.Id, result);         // the collection kept its block-1 slot
        Assert.DoesNotContain(boxedDrama.Id, result);
    }

    [Fact]
    public void SimilarTo_CollectionInheritsBestMemberScore()
    {
        // Similarity sorting looks scores up by RESULT id, and the result is now the collection.
        // Its members score 4 (the reference itself) and 2; the ungrouped item scores 3. Taking the
        // BEST member score puts the collection first, while taking the last-written score (2) or
        // no score at all (0) sinks it behind the ungrouped item - so the order, not the contents,
        // is what fails on a regression. The weak member is deliberately processed last.
        var predator = MovieNamed("Predator", "Action", "Sci-Fi", "Thriller", "Horror");
        var weakMember = MovieNamed("Twins", "Action", "Sci-Fi");
        var midItem = MovieNamed("Aliens", "Action", "Sci-Fi", "Thriller");
        var box = CollectionNamed("Arnold Movies");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, box, predator, weakMember);

        // SimilarTo with no explicit sort resolves to Similarity descending.
        var list = MakeList(
            [MediaTypeConstants.Movie],
            [new ExpressionSet { Expressions = [new Expression("SimilarTo", "Equal", "Predator")] }]);

        Assert.Equal([box.Id, midItem.Id], Filter(list, cache, predator, weakMember, midItem));
    }

    // ---------------------------------------------------------------------------------------
    // Position in the pipeline - grouping precedes the limiters
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void GroupedEntriesCountTowardMaxItems()
    {
        // Four matched movies, two of them sharing one collection, and MaxItems=2. Grouping first
        // gives THREE pre-limit entries - the collection plus the two uncollected movies - so the
        // collection occupies a slot on the Name sort and "Charlie Movie" is the one cut.
        //
        // Every wrong insertion point produces a visibly different answer:
        // - limiter before grouping: the two uncollected movies take both slots and the collection
        //   never appears at all, giving [Bravo, Charlie];
        // - no limit applied: three entries instead of two.
        var boxedFirst = MovieNamed("Mike Movie", "Action");
        var boxedSecond = MovieNamed("November Movie", "Action");
        var survivor = MovieNamed("Bravo Movie", "Action");
        var cutByTheCollection = MovieNamed("Charlie Movie", "Action");
        var box = CollectionNamed("Alpha Box");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, box, boxedFirst, boxedSecond);

        var list = MakeList([MediaTypeConstants.Movie], [GenreBlock("Action")], maxItems: 2);

        var result = Filter(list, cache, boxedFirst, boxedSecond, survivor, cutByTheCollection);

        Assert.Equal([box.Id, survivor.Id], result);
        Assert.DoesNotContain(cutByTheCollection.Id, result);
    }

    // ---------------------------------------------------------------------------------------
    // Coexistence with MatchByMembers
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void CoexistsWithMatchByMembers()
    {
        // The two toggles work in opposite directions and must not interfere: MatchByMembers pulls
        // container candidates out of the pool and admits them on a member match, then grouping
        // projects the plain item results onto their parents. The member-matched container is
        // emitted as ITSELF (it is a container result), while the plain movie is replaced.
        var memberBox = CollectionNamed("Arnold Movies");
        var boxedMovie = MovieNamed("Predator", "Action");     // member only - never in the pool
        var plainMovie = MovieNamed("Commando", "Action");
        var otherBox = CollectionNamed("Manually Curated");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, memberBox, boxedMovie);
        SeedCollection(cache, otherBox, plainMovie);

        var list = MakeList(
            [MediaTypeConstants.Movie, MediaTypeConstants.Collection],
            [GenreBlock("Action")],
            matchByMembers: true);

        var result = Filter(list, cache, plainMovie, memberBox);

        Assert.Equal(2, result.Length);
        Assert.Contains(memberBox.Id, result);
        Assert.Contains(otherBox.Id, result);
        Assert.DoesNotContain(plainMovie.Id, result);
    }

    // ---------------------------------------------------------------------------------------
    // The toggle is a toggle
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ToggleOff_LeavesMatchedItemsAlone()
    {
        // The control case for every assertion above: identical fixture, flag off, items unchanged.
        // Without this, a grouping step that silently no-opped (empty membership index, wrong
        // insertion point) would look the same as one that was never asked to run.
        var movie = MovieNamed("Predator", "Action");
        var box = CollectionNamed("Arnold Movies");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, box, movie);

        var list = MakeList([MediaTypeConstants.Movie], [GenreBlock("Action")], groupIntoCollections: false);

        Assert.Equal([movie.Id], Filter(list, cache, movie));
    }
}
