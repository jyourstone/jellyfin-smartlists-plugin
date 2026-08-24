using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Playlists;
using Expression = Jellyfin.Plugin.SmartLists.Core.QueryEngine.Expression;
using MediaTypeConstants = Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;

namespace Jellyfin.Plugin.SmartLists.Tests.Core;

/// <summary>
/// Covers the container media types (Collection/Playlist) in the single-path engine that replaced
/// the legacy per-rule "include collections/playlists only" checkboxes:
///
/// - MatchByMembers OFF: container candidates flow through the normal item pipeline and are
///   matched against their OWN metadata (Name, Genres, ...), members are never consulted.
/// - MatchByMembers ON: a container is included iff at least one member item passes the full rule
///   pipeline; member matches project onto the parent container.
/// - Self-reference (#499, engine surface): a list must never include its own container, in
///   EITHER mode - the pool-level Origin guard, distinct from the extraction-level guard covered
///   by ListOriginTests.
///
/// HARNESS NOTE: members are enumerated from RefreshCache.CollectionDirectChildren /
/// PlaylistChildItems, so the library manager (TestLibraryManager, which throws on unstubbed
/// members) is never queried for children; rules are either cheap (ItemLists/TextContent/Dates)
/// or People-backed with RefreshCache.ItemPeople seeded, so no prefilter or library query runs.
/// </summary>
public class ContainerMatchingTests
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

    private static Movie MovieNamed(string name, params string[] genres)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = name, Genres = genres };
        movie.SortName = name;
        return movie;
    }

    /// <summary>
    /// A smart collection targeting the given media types with a single Genres-contains rule.
    /// Genres is a cheap (ItemLists) field, keeping the whole run on the simple pipeline.
    /// </summary>
    private static SmartList MakeList(
        List<string> mediaTypes,
        bool matchByMembers,
        string genre = "Action",
        string? jellyfinCollectionId = null)
    {
        var dto = new SmartCollectionDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Container Test List",
            MediaTypes = mediaTypes,
            MatchByMembers = matchByMembers,
            JellyfinCollectionId = jellyfinCollectionId,
            ExpressionSets =
            [
                new ExpressionSet { Expressions = [new Expression("Genres", "Contains", genre)] },
            ],
        };

        return new SmartList(dto);
    }

    /// <summary>Seeds a collection's direct members so no library query is needed.</summary>
    private static void SeedMembers(RefreshQueueService.RefreshCache cache, BoxSet boxSet, params BaseItem[] members)
    {
        cache.CollectionDirectChildren[boxSet.Id] = members;
    }

    private static Guid[] Filter(SmartList list, RefreshQueueService.RefreshCache cache, params BaseItem[] pool)
        => [.. list.FilterPlaylistItems(pool, BaseItem.LibraryManager, TestItems.User, cache)];

    // ---------------------------------------------------------------------------------------
    // MatchByMembers ON - member matches project onto their container
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void MatchByMembers_IncludesAContainerWhenAtLeastOneMemberPasses()
    {
        var actionBox = CollectionNamed("Arnold Movies");     // no genres of its own
        var dramaBox = CollectionNamed("Sad Movies");

        var cache = new RefreshQueueService.RefreshCache();
        SeedMembers(cache, actionBox, MovieNamed("Predator", "Action"), MovieNamed("Twins", "Comedy"));
        SeedMembers(cache, dramaBox, MovieNamed("The Notebook", "Drama"));

        var list = MakeList([Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes.Collection], matchByMembers: true);

        var result = Filter(list, cache, actionBox, dramaBox);

        Assert.Equal([actionBox.Id], result);
    }

    [Fact]
    public void MatchByMembers_SimilarToReferenceInsideACandidateContainerIsFound()
    {
        // A container-only pool holds no items, so the SimilarTo reference lookup must extend
        // to the candidates' members - otherwise reference metadata comes back empty and every
        // container silently fails (PR #508 review finding).
        var arnoldBox = CollectionNamed("Arnold Movies");
        var dramaBox = CollectionNamed("Sad Movies");

        var cache = new RefreshQueueService.RefreshCache();
        // Predator is the reference; Commando shares its full genre profile, so the box passes
        // on Commando's similarity even if the reference item itself is not counted.
        SeedMembers(cache, arnoldBox,
            MovieNamed("Predator", "Action", "Sci-Fi"),
            MovieNamed("Commando", "Action", "Sci-Fi"));
        SeedMembers(cache, dramaBox, MovieNamed("The Notebook", "Drama"));

        var dto = new SmartCollectionDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Similar To Container List",
            MediaTypes = [Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes.Collection],
            MatchByMembers = true,
            ExpressionSets =
            [
                new ExpressionSet { Expressions = [new Expression("SimilarTo", "Equal", "Predator")] },
            ],
        };

        var result = Filter(new SmartList(dto), cache, arnoldBox, dramaBox);

        Assert.Equal([arnoldBox.Id], result);
    }

    [Fact]
    public void MatchByMembers_SimilarToBlockAndPlainBlockEachMatchTheirOwnContainers()
    {
        // Similarity is scored per rule block, and members project their matched blocks onto the
        // container: the Arnold box comes in on the SimilarTo block without being a drama, and the
        // drama box comes in on the Genres block without being similar to Predator. Pre-fix the
        // global similarity AND cut both, returning nothing.
        var arnoldBox = CollectionNamed("Arnold Movies");
        var dramaBox = CollectionNamed("Sad Movies");

        var cache = new RefreshQueueService.RefreshCache();
        SeedMembers(cache, arnoldBox,
            MovieNamed("Predator", "Action", "Sci-Fi"),
            MovieNamed("Commando", "Action", "Sci-Fi"));
        SeedMembers(cache, dramaBox, MovieNamed("The Notebook", "Drama", "Romance"));

        var list = MakeList(
            [MediaTypeConstants.Collection],
            matchByMembers: true,
            [
                new ExpressionSet { Expressions = [new Expression("SimilarTo", "Equal", "Predator")] },
                new ExpressionSet { Expressions = [new Expression("Genres", "Contains", "Drama")] },
            ],
            orderName: "Rule Block Order Ascending");

        // Rule Block Order can only produce this sequence from projected group mappings: the
        // SimilarTo block is block 0, the Genres block is block 1.
        var result = Filter(list, cache, dramaBox, arnoldBox);

        Assert.Equal([arnoldBox.Id, dramaBox.Id], result);
    }

    [Fact]
    public void MatchByMembers_NeverIncludesTheListsOwnContainer()
    {
        // Both containers hold a passing member; the first one IS the list being built.
        var self = CollectionNamed("Action Collection [Smart]");
        var other = CollectionNamed("Manually Curated Action");

        var cache = new RefreshQueueService.RefreshCache();
        SeedMembers(cache, self, MovieNamed("Commando", "Action"));
        SeedMembers(cache, other, MovieNamed("Predator", "Action"));

        var list = MakeList(
            [Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes.Collection],
            matchByMembers: true,
            jellyfinCollectionId: self.Id.ToString());

        var result = Filter(list, cache, self, other);

        Assert.Equal([other.Id], result);
    }

    [Fact]
    public void MatchByMembers_MixedList_ReturnsMatchedItemsAndTheirContainersWithoutDuplicates()
    {
        var actionMovie = MovieNamed("Predator", "Action");
        var dramaMovie = MovieNamed("The Notebook", "Drama");
        var box = CollectionNamed("Arnold Movies");

        var cache = new RefreshQueueService.RefreshCache();
        SeedMembers(cache, box, actionMovie);

        var list = MakeList(
            [Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes.Movie, Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes.Collection],
            matchByMembers: true);

        var result = Filter(list, cache, actionMovie, dramaMovie, box);

        // The action movie matches as itself AND rescues its container; the drama movie is out.
        Assert.Equal(2, result.Length);
        Assert.Contains(actionMovie.Id, result);
        Assert.Contains(box.Id, result);
    }

    // ---------------------------------------------------------------------------------------
    // MatchByMembers OFF - containers are matched on their own metadata only
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void MatchByMembersOff_MatchesContainersOnTheirOwnMetadataAndIgnoresMembers()
    {
        // The Action-tagged box holds only a drama; the Drama-tagged box holds an action movie.
        // Own-metadata matching must pick the first and must NOT let the member rescue the second.
        var taggedAction = CollectionNamed("Box A", "Action");
        var taggedDrama = CollectionNamed("Box B", "Drama");

        var cache = new RefreshQueueService.RefreshCache();
        SeedMembers(cache, taggedAction, MovieNamed("The Notebook", "Drama"));
        SeedMembers(cache, taggedDrama, MovieNamed("Predator", "Action"));

        var list = MakeList([Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes.Collection], matchByMembers: false);

        var result = Filter(list, cache, taggedAction, taggedDrama);

        Assert.Equal([taggedAction.Id], result);
    }

    [Fact]
    public void MatchByMembersOff_NeverIncludesTheListsOwnContainer()
    {
        var self = CollectionNamed("Action Collection [Smart]", "Action");
        var other = CollectionNamed("Manually Curated Action", "Action");

        var list = MakeList(
            [Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes.Collection],
            matchByMembers: false,
            jellyfinCollectionId: self.Id.ToString());

        var result = Filter(list, new RefreshQueueService.RefreshCache(), self, other);

        Assert.Equal([other.Id], result);
    }

    // ---------------------------------------------------------------------------------------
    // Name + Equal container fallback (migration parity: users target smart lists by base name)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// PRECONDITION: Plugin.Instance is null in a test process, so StripPrefixAndSuffix
    /// deterministically strips the default "[Smart]" suffix (same as ListOriginTests).
    /// </summary>
    [Theory]
    [InlineData("Arnold [Smart]", "Collection", "Arnold", true)]   // container: base name matches
    [InlineData("Arnold [Smart]", "Playlist", "Arnold", true)]     // container: base name matches
    [InlineData("Arnold [Smart]", "Movie", "Arnold", false)]       // non-container: no fallback
    [InlineData("Arnold", "Movie", "Arnold", true)]                // plain equality still works
    [InlineData("Arnold", "Collection", "Terminator", false)]      // no match at all
    public void NameEqualsWithContainerBaseName_StripsThePrefixSuffixForContainersOnly(
        string name, string itemType, string target, bool expected)
    {
        Assert.Equal(expected, Engine.NameEqualsWithContainerBaseName(name, itemType, target));
    }

    // ---------------------------------------------------------------------------------------
    // MatchByMembers ON - AND within a group is evaluated per single member
    // ---------------------------------------------------------------------------------------

    /// <summary>A Movie with a production year, for the per-member AND coverage.</summary>
    private static Movie MovieFromYear(string name, int year, params string[] genres)
    {
        var movie = MovieNamed(name, genres);
        movie.ProductionYear = year;
        return movie;
    }

    /// <summary>
    /// Seeds an item's people into the refresh cache so People extraction is a cache hit and the
    /// throwing TestLibraryManager is never queried. Seeding an EMPTY set models Jellyfin's
    /// reality for containers: people are never rolled up onto a BoxSet.
    /// </summary>
    private static void SeedActors(RefreshQueueService.RefreshCache cache, BaseItem item, params string[] actors)
    {
        cache.ItemPeople[item.Id] = new RefreshQueueService.CategorizedPeople
        {
            AllPeople = [.. actors],
            Actors = [.. actors],
        };
    }

    /// <summary>Overload taking fully-shaped expression sets (and optionally a named sort).</summary>
    private static SmartList MakeList(
        List<string> mediaTypes,
        bool matchByMembers,
        List<ExpressionSet> expressionSets,
        string? orderName = null)
    {
        var dto = new SmartCollectionDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Container Test List",
            MediaTypes = mediaTypes,
            MatchByMembers = matchByMembers,
            ExpressionSets = expressionSets,
            Order = orderName == null ? null : new OrderDto { Name = orderName },
        };

        return new SmartList(dto);
    }

    [Fact]
    public void MatchByMembers_AllRulesOfAGroupMustPassOnTheSameMember()
    {
        // The spec's Arnold/Stallone case: "Actors contains Arnold AND year < 1990" means
        // "collections containing a pre-1990 Arnold movie" - ONE member must satisfy the whole
        // group. A collection holding a 2013 Arnold movie and a 1976 Stallone movie satisfies
        // each rule on SOME member but neither on a single one, and must stay out.
        //
        // Actors is an expensive (People) field while ProductionYear is cheap, so this also runs
        // the members through the two-phase pipeline: the year rule gates phase 1, and phase 2
        // resolves Actors from the seeded people cache.
        var terminator = MovieFromYear("The Terminator", 1984);
        var escapePlan = MovieFromYear("Escape Plan", 2013);
        var rocky = MovieFromYear("Rocky", 1976);

        var arnoldClassics = CollectionNamed("Arnold Classics");
        var mixedBag = CollectionNamed("Mixed Bag");

        var cache = new RefreshQueueService.RefreshCache();
        SeedMembers(cache, arnoldClassics, terminator);
        SeedMembers(cache, mixedBag, escapePlan, rocky);
        SeedActors(cache, terminator, "Arnold Schwarzenegger");
        SeedActors(cache, escapePlan, "Arnold Schwarzenegger", "Sylvester Stallone");
        SeedActors(cache, rocky, "Sylvester Stallone");

        var list = MakeList(
            [MediaTypeConstants.Collection],
            matchByMembers: true,
            [
                new ExpressionSet
                {
                    Expressions =
                    [
                        new Expression("Actors", "Contains", "Arnold Schwarzenegger"),
                        new Expression("ProductionYear", "LessThan", "1990"),
                    ],
                },
            ]);

        var result = Filter(list, cache, arnoldClassics, mixedBag);

        Assert.Equal([arnoldClassics.Id], result);
    }

    [Fact]
    public void MatchByMembers_NegativeOperatorKeepsItsItemMeaning_SomeMemberLacksTheValue()
    {
        // "Genres NotContains Horror" with MatchByMembers ON means "at least one member is not
        // Horror" - the operator keeps its per-item meaning, deliberately NOT "no member has
        // Horror". A collection whose members are all Horror stays out; a single non-Horror
        // member lets a collection in even when its siblings are Horror.
        var allHorror = CollectionNamed("Wall To Wall Horror");
        var mostlyHorror = CollectionNamed("Mostly Horror");

        var cache = new RefreshQueueService.RefreshCache();
        SeedMembers(cache, allHorror, MovieNamed("It", "Horror"), MovieNamed("Saw", "Horror"));
        SeedMembers(cache, mostlyHorror, MovieNamed("Scream", "Horror"), MovieNamed("Clue", "Comedy"));

        var list = MakeList(
            [MediaTypeConstants.Collection],
            matchByMembers: true,
            [new ExpressionSet { Expressions = [new Expression("Genres", "NotContains", "Horror")] }]);

        var result = Filter(list, cache, allHorror, mostlyHorror);

        Assert.Equal([mostlyHorror.Id], result);
    }

    // ---------------------------------------------------------------------------------------
    // MatchByMembers OFF - own metadata only, and item-only fields honestly match nothing
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void MatchByMembersOff_MatchesContainersByTheirOwnName()
    {
        var arnoldBox = CollectionNamed("Arnold Movies");
        var stalloneBox = CollectionNamed("Stallone Movies");

        var list = MakeList(
            [MediaTypeConstants.Collection],
            matchByMembers: false,
            [new ExpressionSet { Expressions = [new Expression("Name", "Contains", "Arnold")] }]);

        var result = Filter(list, new RefreshQueueService.RefreshCache(), arnoldBox, stalloneBox);

        Assert.Equal([arnoldBox.Id], result);
    }

    [Fact]
    public void MatchByMembersOff_PeopleRuleMatchesNothingAgainstABareBoxSet()
    {
        // Jellyfin never rolls People up onto a BoxSet, so with the toggle off a People rule
        // evaluates against the container's own (empty) people and returns nothing - even when a
        // member DOES have the actor. The engine must not secretly consult members in this mode;
        // the field picker hides People for container-only + toggle-off lists for the same reason.
        var box = CollectionNamed("Arnold Movies");
        var member = MovieNamed("Predator", "Action");

        var cache = new RefreshQueueService.RefreshCache();
        SeedMembers(cache, box, member);
        SeedActors(cache, member, "Arnold Schwarzenegger");
        SeedActors(cache, box); // empty - a BoxSet has no people rows of its own

        var list = MakeList(
            [MediaTypeConstants.Collection],
            matchByMembers: false,
            [new ExpressionSet { Expressions = [new Expression("Actors", "Contains", "Arnold Schwarzenegger")] }]);

        Assert.Empty(Filter(list, cache, box));
    }

    // ---------------------------------------------------------------------------------------
    // Group tracking - containers inherit their passing members' rule-group mappings
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void MatchByMembers_RuleBlockOrder_PlacesContainersInTheirMembersBlock()
    {
        // Rule Block Order sorts by matched group index and puts UNMAPPED items last. The
        // container's group is deliberately FIRST (set 0 = Drama, matched via its member) to make
        // the assertion discriminating: a container that never inherited its member's mapping
        // would sort after the Action movie instead of before it.
        var actionMovie = MovieNamed("Predator", "Action");
        var box = CollectionNamed("Drama Box");

        var cache = new RefreshQueueService.RefreshCache();
        SeedMembers(cache, box, MovieNamed("The Notebook", "Drama"));

        var list = MakeList(
            [MediaTypeConstants.Movie, MediaTypeConstants.Collection],
            matchByMembers: true,
            [
                new ExpressionSet { Expressions = [new Expression("Genres", "Contains", "Drama")] },
                new ExpressionSet { Expressions = [new Expression("Genres", "Contains", "Action")] },
            ],
            orderName: "Rule Block Order Ascending");

        var result = Filter(list, cache, actionMovie, box);

        Assert.Equal([box.Id, actionMovie.Id], result);
    }

    [Fact]
    public void MatchByMembers_PerGroupLimits_ContainersSurviveInTheirMappedGroup()
    {
        // ApplyPerGroupLimits rebuilds the result from per-group buckets, silently DROPPING any
        // item without a group mapping. The container must land in its member's bucket (set 1):
        // mapped to set 0 it would lose the MaxItems=1 slot to "Alpha Action" (Name sort) and
        // vanish; unmapped it would vanish outright.
        var alpha = MovieNamed("Alpha Action", "Action");
        var beta = MovieNamed("Beta Action", "Action");
        var dramaMovie = MovieNamed("Drama Movie", "Drama");
        var box = CollectionNamed("Zebra Box");

        var cache = new RefreshQueueService.RefreshCache();
        SeedMembers(cache, box, MovieNamed("The Notebook", "Drama"));

        var list = MakeList(
            [MediaTypeConstants.Movie, MediaTypeConstants.Collection],
            matchByMembers: true,
            [
                new ExpressionSet { Expressions = [new Expression("Genres", "Contains", "Action")], MaxItems = 1 },
                new ExpressionSet { Expressions = [new Expression("Genres", "Contains", "Drama")] },
            ]);

        var result = Filter(list, cache, alpha, beta, dramaMovie, box);

        Assert.Equal(3, result.Length);
        Assert.Contains(alpha.Id, result);      // set 0's single slot goes to the first Action name
        Assert.Contains(dramaMovie.Id, result);
        Assert.Contains(box.Id, result);        // the container kept its set-1 slot
    }

    // ---------------------------------------------------------------------------------------
    // Playlist containers - same semantics, PlaylistChildItems-backed
    // ---------------------------------------------------------------------------------------

    /// <summary>A Jellyfin Playlist container candidate.</summary>
    private static Playlist PlaylistContainer(string name)
    {
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = name };
        playlist.SortName = name;
        return playlist;
    }

    [Fact]
    public void MatchByMembers_PlaylistContainersMatchByTheirMembers()
    {
        var actionMix = PlaylistContainer("Action Mix");
        var chillMix = PlaylistContainer("Chill Mix");

        var cache = new RefreshQueueService.RefreshCache();
        cache.PlaylistChildItems[actionMix.Id] = [MovieNamed("Predator", "Action")];
        cache.PlaylistChildItems[chillMix.Id] = [MovieNamed("The Notebook", "Drama")];

        var list = MakeList([MediaTypeConstants.Playlist], matchByMembers: true);

        var result = Filter(list, cache, actionMix, chillMix);

        Assert.Equal([actionMix.Id], result);
    }

    [Fact]
    public void MatchByMembersOff_PlaylistContainersMatchByTheirOwnName()
    {
        var actionMix = PlaylistContainer("Action Mix");
        var chillMix = PlaylistContainer("Chill Mix");

        var list = MakeList(
            [MediaTypeConstants.Playlist],
            matchByMembers: false,
            [new ExpressionSet { Expressions = [new Expression("Name", "Contains", "Action")] }]);

        var result = Filter(list, new RefreshQueueService.RefreshCache(), actionMix, chillMix);

        Assert.Equal([actionMix.Id], result);
    }
}
