using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Covers <see cref="ListOrigin"/> - the "which list am I building" identity that keeps a smart
/// list out of its own Collections/Playlists results - and the two extractors that consume it.
///
/// Two defects are pinned here, both reproduced live before the fix:
///
/// 1. Issue #499: <c>ExtractCollections</c> was never told which collection was being built, so a
///    smart collection saw its own name in the Collections field. With the default "[Smart]"
///    suffix, a rule like <c>Collections NotContains "smart"</c> matches the collection's own name
///    and the result oscillates 0 -> N -> 0 -> N across refreshes.
/// 2. The per-item extraction caches were keyed on item id (and depth) only, while the values
///    stored in them are ALREADY origin-filtered. The cache is per-user and survives until the
///    whole refresh queue drains, so list B refreshed after list A in one drain inherited A's
///    exclusions and went blind to playlist/collection A.
///
/// The fix also changed WHAT counts as "myself": identity only - the SmartLists provider-ID
/// tether, or a stored Jellyfin item id - never the name. Name matching wrongly excluded an
/// unrelated, manually created list that merely shared the name - see
/// <see cref="ExtractCollections_KeepsAManuallyCreatedCollectionWithTheSameName"/>.
///
/// HARNESS NOTE: neither extractor touches <c>ILibraryManager</c> once its caches are seeded -
/// <c>AllCollections</c> + <c>CollectionDirectChildren</c> for collections (depth membership is
/// then built entirely off that dictionary), <c>AllPlaylists</c> + <c>PlaylistMembershipCache</c>
/// for playlists. The library manager passed in is <see cref="TestLibraryManager"/>, which throws
/// on every unstubbed member, so a regression that starts querying the server fails loudly here.
///
/// PRECONDITION: <c>Plugin.Instance</c> is null in a test process, so
/// <c>NameFormatter.StripPrefixAndSuffix</c> deterministically strips the default "[Smart]"
/// suffix. The fixtures use that suffix for exactly that reason.
/// </summary>
public class ListOriginTests
{
    // ---------------------------------------------------------------------------------------
    // Reaching the private statics (same approach as AncestorWalkTests)
    // ---------------------------------------------------------------------------------------

    private static readonly MethodInfo ExtractCollectionsMethod =
        typeof(OperandFactory).GetMethod("ExtractCollections", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ExtractPlaylistsMethod =
        typeof(OperandFactory).GetMethod("ExtractPlaylists", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static List<string> ExtractCollections(
        BaseItem item,
        RefreshQueueService.RefreshCache cache,
        int depth,
        ListOrigin? origin)
        => (List<string>)ExtractCollectionsMethod.Invoke(
            null,
            [item, TestItems.User, BaseItem.LibraryManager, cache, null, depth, origin])!;

    private static List<string> ExtractPlaylists(
        BaseItem item,
        RefreshQueueService.RefreshCache cache,
        ListOrigin? origin)
        => (List<string>)ExtractPlaylistsMethod.Invoke(
            null,
            [item, TestItems.User, BaseItem.LibraryManager, cache, null, origin])!;

    // ---------------------------------------------------------------------------------------
    // Fixture builders
    // ---------------------------------------------------------------------------------------

    /// <summary>A BoxSet - what Jellyfin calls a collection. Name before SortName, as ever.</summary>
    private static BoxSet CollectionNamed(string name)
    {
        var boxSet = new BoxSet { Id = Guid.NewGuid(), Name = name };
        boxSet.SortName = name;
        return boxSet;
    }

    private static Playlist PlaylistNamed(string name, Guid? id = null)
    {
        var playlist = new Playlist { Id = id ?? Guid.NewGuid(), Name = name };
        playlist.SortName = name;
        return playlist;
    }

    /// <summary>
    /// Registers a collection and its DIRECT children. Seeding
    /// <c>CollectionDirectChildren</c> is what keeps <c>ExtractCollections</c> off the library
    /// manager: the per-depth membership sets are derived from this dictionary alone.
    /// </summary>
    private static void SeedCollection(RefreshQueueService.RefreshCache cache, BoxSet boxSet, params BaseItem[] directChildren)
    {
        cache.AllCollections = [.. cache.AllCollections ?? [], boxSet];
        cache.CollectionDirectChildren[boxSet.Id] = directChildren;
    }

    /// <summary>
    /// Registers a playlist and its members. Playlists cannot nest, so membership is seeded
    /// directly rather than derived.
    /// </summary>
    private static void SeedPlaylist(RefreshQueueService.RefreshCache cache, Playlist playlist, params BaseItem[] members)
    {
        cache.AllPlaylists = [.. cache.AllPlaylists ?? [], playlist];
        cache.PlaylistMembershipCache[playlist.Id] = [.. members.Select(m => m.Id)];
    }

    // ---------------------------------------------------------------------------------------
    // Issue #499 - a collection must not see itself
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Issue #499 in one assertion. Before the fix <c>ExtractCollections</c> took no origin at
    /// all, so the collection being built came straight back in its own Collections field.
    ///
    /// The null-origin call first is deliberate: it proves the fixture really does report
    /// membership, so the empty result below is the guard firing rather than a dead fixture.
    /// </summary>
    [Fact]
    public void ExtractCollections_ExcludesTheCollectionBeingBuilt()
    {
        var movie = TestItems.Mov("Blade Runner");
        var boxSet = CollectionNamed("Movies [Smart]");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, boxSet, movie);

        Assert.Equal(["Movies [Smart]"], ExtractCollections(movie, cache, depth: 1, origin: null));

        var origin = new ListOrigin("col-1", [boxSet.Id]);

        Assert.Empty(ExtractCollections(movie, cache, depth: 1, origin));
    }

    /// <summary>
    /// The name-collision half of the ID-based design. The origin here is a smart PLAYLIST named
    /// "Movies" that has already been created in Jellyfin, so it has an id - and that id is not
    /// the BoxSet's. Under the old base-name comparison (what ExtractPlaylists used to do) the
    /// BoxSet would be excluded purely because "Movies [Smart]" strips to "Movies", silently
    /// hiding a collection the user never asked to hide.
    /// </summary>
    [Fact]
    public void ExtractCollections_KeepsAManuallyCreatedCollectionWithTheSameName()
    {
        var movie = TestItems.Mov("Blade Runner");
        var boxSet = CollectionNamed("Movies [Smart]");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, boxSet, movie);

        // Same base name, different Jellyfin item - a smart playlist, not this collection.
        var origin = new ListOrigin("pl-1", [Guid.NewGuid()]);

        Assert.Equal(["Movies [Smart]"], ExtractCollections(movie, cache, depth: 1, origin));
    }

    /// <summary>
    /// The playlist-side equivalent: building the smart playlist "Movies" must not hide the
    /// unrelated playlist that happens to be called "Movies [Smart]" as well.
    /// </summary>
    [Fact]
    public void ExtractPlaylists_KeepsAPlaylistThatMerelySharesTheName()
    {
        var movie = TestItems.Mov("Blade Runner");
        var namesake = PlaylistNamed("Movies [Smart]");

        var cache = new RefreshQueueService.RefreshCache();
        SeedPlaylist(cache, namesake, movie);

        var origin = new ListOrigin("pl-1", [Guid.NewGuid()]);

        Assert.Equal(["Movies [Smart]"], ExtractPlaylists(movie, cache, origin));
    }

    // ---------------------------------------------------------------------------------------
    // Cache poisoning across lists in one queue drain
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The reproduced cross-list leak, on ONE cache - which is what a real refresh queue drain
    /// gives you, since RefreshCache is per USER and only <c>AncestorValuesById</c> is cleared
    /// between queue items.
    ///
    /// Smart playlist "ZZTest Alpha" refreshes first and correctly records "no playlists" for the
    /// movie (its own playlist is the only one, and it is excluded). Smart playlist "ZZTest Beta"
    /// refreshes next in the same drain and asks about the same movie. Before the fix
    /// <c>ItemPlaylists</c> was keyed on item id alone, so Beta got Alpha's origin-filtered answer
    /// back and was blind to the playlist it was explicitly filtering on - 65 items became 0.
    /// </summary>
    [Fact]
    public void ExtractPlaylists_DoesNotReuseAnotherListsOriginFilteredResult()
    {
        var movie = TestItems.Mov("Blade Runner");
        var alphaPlaylist = PlaylistNamed("ZZTest Alpha [Smart]");

        var cache = new RefreshQueueService.RefreshCache();
        SeedPlaylist(cache, alphaPlaylist, movie);

        var alpha = new ListOrigin("alpha", [alphaPlaylist.Id]);
        var beta = new ListOrigin("beta", [Guid.NewGuid()]);

        Assert.Empty(ExtractPlaylists(movie, cache, alpha));

        // Same item, same cache, different list being built.
        Assert.Equal(["ZZTest Alpha [Smart]"], ExtractPlaylists(movie, cache, beta));
    }

    /// <summary>
    /// Same leak on the collections side. This one could not exist before the fix (collections had
    /// no origin at all), and it is the reason the cache re-key had to ship in the same change:
    /// the new guard would otherwise have arrived with the defect already known from playlists.
    /// </summary>
    [Fact]
    public void ExtractCollections_DoesNotReuseAnotherListsOriginFilteredResult()
    {
        var movie = TestItems.Mov("Blade Runner");
        var alphaCollection = CollectionNamed("ZZTest Alpha [Smart]");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, alphaCollection, movie);

        var alpha = new ListOrigin("alpha", [alphaCollection.Id]);
        var beta = new ListOrigin("beta", [Guid.NewGuid()]);

        Assert.Empty(ExtractCollections(movie, cache, depth: 1, alpha));

        Assert.Equal(["ZZTest Alpha [Smart]"], ExtractCollections(movie, cache, depth: 1, beta));
    }

    /// <summary>
    /// Depth and origin are independent dimensions of the collections cache key. Collapsing the
    /// tuple back to either one alone breaks a different case, so both halves are asserted here:
    ///
    /// - Same origin, two depths: depth 0 sees only the inner BoxSet the movie is directly in;
    ///   depth 1 also sees the outer BoxSet that contains it. Drop Depth from the key and the
    ///   second answer is the first one, stale.
    /// - Same depth, two origins: the exclusion differs per list. Drop OriginKey and this is the
    ///   cross-list leak above.
    /// </summary>
    [Fact]
    public void ExtractCollections_KeepsDepthAndOriginIndependentInTheCacheKey()
    {
        var movie = TestItems.Mov("Blade Runner");
        var inner = CollectionNamed("Trilogy [Smart]");
        var outer = CollectionNamed("Franchise [Smart]");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, inner, movie);
        SeedCollection(cache, outer, inner);

        var unrelated = new ListOrigin("other-list", [Guid.NewGuid()]);

        // Depth is part of the key.
        Assert.Equal(["Trilogy [Smart]"], ExtractCollections(movie, cache, depth: 0, unrelated));
        Assert.Equal(["Trilogy [Smart]", "Franchise [Smart]"], ExtractCollections(movie, cache, depth: 1, unrelated));

        // Origin is part of the key, at the same depth.
        var buildingTrilogy = new ListOrigin("trilogy-list", [inner.Id]);

        Assert.Equal(["Franchise [Smart]"], ExtractCollections(movie, cache, depth: 1, buildingTrilogy));
        Assert.Equal(["Trilogy [Smart]", "Franchise [Smart]"], ExtractCollections(movie, cache, depth: 1, unrelated));
    }

    // ---------------------------------------------------------------------------------------
    // ListOrigin itself - identity only, never name
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Identity is the whole rule: the SmartLists provider-ID tether, or a stored Jellyfin item id.
    /// A name is never consulted, so an identically named container belonging to somebody else is
    /// always visible - including before this list has been created in Jellyfin, when the id set is
    /// still empty and there is no container of ours for an item to be a member of anyway.
    /// </summary>
    [Fact]
    public void ListOrigin_MatchesOnIdentityAndNeverOnName()
    {
        var candidate = CollectionNamed("Movies [Smart]");

        Assert.False(new ListOrigin("col-1", []).Matches(candidate));
        Assert.False(new ListOrigin("col-1", [Guid.NewGuid()]).Matches(candidate));
        Assert.True(new ListOrigin("col-1", [candidate.Id]).Matches(candidate));
    }

    /// <summary>
    /// The SmartLists provider-ID tether identifies the list even when the stored Jellyfin id has
    /// gone stale. This matters because the recovery that repairs a stale id runs AFTER filtering
    /// (PlaylistService.ProcessPlaylistRefreshAsync filters at ~line 177 and recovers at ~line 263;
    /// CollectionService filters at ~line 252 and recovers at ~line 341), so on a recovery refresh
    /// the id set alone points at a container that no longer exists and the real one - still
    /// tethered - would be left visible to its own rules for that whole refresh.
    /// </summary>
    [Fact]
    public void ListOrigin_MatchesTheTetheredContainerEvenWhenTheStoredIdIsStale()
    {
        var tethered = CollectionNamed("Uncollected [Smart]");
        tethered.SetProviderId(ProviderKeys.SmartLists, "col-1");

        // Stored id is stale (points at a deleted container), so only the tether can identify this.
        var origin = new ListOrigin("col-1", [Guid.NewGuid()]);

        Assert.True(origin.Matches(tethered));

        // A container tethered to a DIFFERENT smart list is not this list.
        var other = CollectionNamed("Uncollected [Smart]");
        other.SetProviderId(ProviderKeys.SmartLists, "col-2");
        Assert.False(origin.Matches(other));
    }

    /// <summary>
    /// A brand-new smart list - no tether match, no stored id - must match NOTHING, of either kind.
    /// This is the case a base-name fallback used to cover, and covering it by name was the bug:
    /// a not-yet-created smart PLAYLIST "Marvel" blanked out the hand-made COLLECTION "Marvel" for
    /// every item, and with "hide when empty" the id was never stored, so it never healed.
    /// Nothing is lost by matching nothing here: the list does not exist yet, so no item can be in it.
    /// </summary>
    [Fact]
    public void ListOrigin_WithNoIdentityYet_MatchesNothing()
    {
        var brandNewPlaylist = new ListOrigin("pl-1", []);
        var brandNewCollection = new ListOrigin("col-1", []);

        Assert.False(brandNewPlaylist.Matches(CollectionNamed("Marvel [Smart]")));
        Assert.False(brandNewPlaylist.Matches(PlaylistNamed("Marvel [Smart]")));

        Assert.False(brandNewCollection.Matches(CollectionNamed("Marvel [Smart]")));
        Assert.False(brandNewCollection.Matches(PlaylistNamed("Marvel [Smart]")));
    }

    /// <summary>
    /// The cross-kind case end to end: building the never-yet-created smart playlist "Marvel" must
    /// leave the hand-made collection "Marvel" visible to its Collections rule.
    /// </summary>
    [Fact]
    public void ExtractCollections_KeepsASameNamedCollectionWhenTheOriginIsAPlaylist()
    {
        var movie = TestItems.Mov("Blade Runner");
        var boxSet = CollectionNamed("Marvel [Smart]");

        var cache = new RefreshQueueService.RefreshCache();
        SeedCollection(cache, boxSet, movie);

        // No JellyfinPlaylistId and no tether: the playlist has never been created, so this origin
        // has no identity at all and must not reach across to the same-named collection.
        var list = new SmartList(new SmartPlaylistDto { Id = "pl-1", Name = "Marvel" });

        Assert.Equal(["Marvel [Smart]"], ExtractCollections(movie, cache, depth: 1, list.Origin));
    }

    // ---------------------------------------------------------------------------------------
    // SmartList carries the Jellyfin ids into the origin
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// An AllUsers playlist is ONE smart list rendered as one Jellyfin playlist PER USER, each
    /// with its own id in <c>UserPlaylists</c> (plus the legacy top-level id). A single scalar
    /// origin id would exclude one user's copy and leave every other user's copy visible to the
    /// list's own Playlists rules, so the origin has to carry the whole set.
    ///
    /// Carrying all of them rather than resolving the refreshing user's one is deliberate: the
    /// copies are indistinguishable to a rule (same name, same contents), so seeing a sibling copy
    /// is the same self-reference bug wearing a different id.
    /// </summary>
    [Fact]
    public void SmartList_Origin_CoversEveryJellyfinIdOfAnAllUsersPlaylist()
    {
        var legacyId = Guid.NewGuid();
        var firstUserId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();

        var list = new SmartList(new SmartPlaylistDto
        {
            Id = "pl-1",
            Name = "Movies",
            AllUsers = true,
            JellyfinPlaylistId = legacyId.ToString(),
            UserPlaylists =
            [
                new SmartPlaylistDto.UserPlaylistMapping { UserId = "u1", JellyfinPlaylistId = firstUserId.ToString() },
                new SmartPlaylistDto.UserPlaylistMapping { UserId = "u2", JellyfinPlaylistId = secondUserId.ToString() },
            ],
        });

        Assert.True(list.Origin.Matches(PlaylistNamed("Movies [Smart]", legacyId)));
        Assert.True(list.Origin.Matches(PlaylistNamed("Movies (tester) [Smart]", firstUserId)));
        Assert.True(list.Origin.Matches(PlaylistNamed("Movies (other) [Smart]", secondUserId)));

        // A namesake that is not one of this list's playlists stays visible.
        Assert.False(list.Origin.Matches(PlaylistNamed("Movies [Smart]")));
    }

    /// <summary>
    /// End-to-end through the extractor: every per-user copy of an AllUsers playlist drops out at
    /// once, and an unrelated playlist survives the same pass.
    /// </summary>
    [Fact]
    public void ExtractPlaylists_ExcludesEveryPerUserCopyOfAnAllUsersPlaylist()
    {
        var movie = TestItems.Mov("Blade Runner");
        var firstCopy = PlaylistNamed("Movies (tester) [Smart]");
        var secondCopy = PlaylistNamed("Movies (other) [Smart]");
        var unrelated = PlaylistNamed("Favourites");

        var cache = new RefreshQueueService.RefreshCache();
        SeedPlaylist(cache, firstCopy, movie);
        SeedPlaylist(cache, secondCopy, movie);
        SeedPlaylist(cache, unrelated, movie);

        var list = new SmartList(new SmartPlaylistDto
        {
            Id = "pl-1",
            Name = "Movies",
            AllUsers = true,
            UserPlaylists =
            [
                new SmartPlaylistDto.UserPlaylistMapping { UserId = "u1", JellyfinPlaylistId = firstCopy.Id.ToString() },
                new SmartPlaylistDto.UserPlaylistMapping { UserId = "u2", JellyfinPlaylistId = secondCopy.Id.ToString() },
            ],
        });

        Assert.Equal(["Favourites"], ExtractPlaylists(movie, cache, list.Origin));
    }

    /// <summary>
    /// The collection companion: a smart collection's origin carries its BoxSet id, and an
    /// as-yet-uncreated collection (no id on the DTO) matches nothing - it has no container yet, so
    /// there is nothing of its own for an item to be a member of. The tether covers it from the
    /// moment the collection is created, including when the stored id later goes stale.
    /// </summary>
    [Fact]
    public void SmartList_Origin_UsesTheJellyfinCollectionId()
    {
        var boxSet = CollectionNamed("Movies [Smart]");

        var created = new SmartList(new SmartCollectionDto
        {
            Id = "col-1",
            Name = "Movies",
            JellyfinCollectionId = boxSet.Id.ToString(),
        });

        Assert.True(created.Origin.Matches(boxSet));
        Assert.False(created.Origin.Matches(CollectionNamed("Movies [Smart]")));

        var neverRefreshed = new SmartList(new SmartCollectionDto { Id = "col-2", Name = "Movies" });

        Assert.False(neverRefreshed.Origin.Matches(boxSet));

        // ...but once its collection exists it is tethered, which identifies it by itself.
        var tethered = CollectionNamed("Movies [Smart]");
        tethered.SetProviderId(ProviderKeys.SmartLists, "col-2");
        Assert.True(neverRefreshed.Origin.Matches(tethered));
    }
}
