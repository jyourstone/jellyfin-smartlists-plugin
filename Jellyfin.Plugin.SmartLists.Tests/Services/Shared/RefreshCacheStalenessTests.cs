using System.Reflection;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.Shared;

/// <summary>
/// Pins the per-drain staleness fix on <see cref="RefreshQueueService.RefreshCache"/>.
///
/// The container snapshots (<c>AllCollections</c>/<c>AllPlaylists</c>) and the membership caches
/// behind them are built ONCE per refresh-queue drain, while the cache itself is per user and lives
/// until the drain finishes. So when list A rewrote its own Jellyfin container mid-drain, every list
/// refreshed after it in that drain still evaluated its Collections/Playlists rules against A's
/// pre-drain contents - permanently one refresh behind while lists refresh together, which is the
/// normal case under scheduled auto-refresh.
///
/// Reproduced live before the fix: playlist "ZZTest Alpha" changed from 33 to 49 members, and
/// "ZZTest Beta" (rule: Playlists contains "ZZTest Alpha") reported 33 in the same drain and only
/// caught up on a later one.
///
/// The precondition is easy to miss when reproducing by hand: the earlier list must itself use the
/// Collections/Playlists extractor, because that is what seeds the snapshot BEFORE it writes. A
/// first attempt with a Name-only rule on A came back green for exactly that reason.
/// </summary>
public class RefreshCacheStalenessTests
{
    private static readonly MethodInfo ExtractPlaylistsMethod =
        typeof(OperandFactory).GetMethod("ExtractPlaylists", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ExtractCollectionsMethod =
        typeof(OperandFactory).GetMethod("ExtractCollections", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static List<string> ExtractPlaylists(BaseItem item, RefreshQueueService.RefreshCache cache)
        => (List<string>)ExtractPlaylistsMethod.Invoke(
            null, [item, TestItems.User, BaseItem.LibraryManager, cache, null, null])!;

    private static List<string> ExtractCollections(BaseItem item, RefreshQueueService.RefreshCache cache, int depth)
        => (List<string>)ExtractCollectionsMethod.Invoke(
            null, [item, TestItems.User, BaseItem.LibraryManager, cache, null, depth, null])!;

    private static Playlist PlaylistNamed(string name)
    {
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = name };
        playlist.SortName = name;
        return playlist;
    }

    private static BoxSet CollectionNamed(string name)
    {
        var boxSet = new BoxSet { Id = Guid.NewGuid(), Name = name };
        boxSet.SortName = name;
        return boxSet;
    }

    /// <summary>
    /// A movie added to a playlist mid-drain must be visible to the next list in that same drain.
    /// </summary>
    [Fact]
    public void OnPlaylistWritten_MakesNewMembershipVisibleWithinTheSameDrain()
    {
        var inPlaylist = TestItems.Mov("Blade Runner");
        var addedLater = TestItems.Mov("Alien");
        var playlist = PlaylistNamed("ZZTest Alpha [Smart]");

        var cache = new RefreshQueueService.RefreshCache();
        cache.AllPlaylists = [playlist];
        cache.PlaylistMembershipCache[playlist.Id] = [inPlaylist.Id];

        // Seeds the per-drain snapshot, exactly as the earlier list's own refresh does.
        Assert.Equal(["ZZTest Alpha [Smart]"], ExtractPlaylists(inPlaylist, cache));
        Assert.Empty(ExtractPlaylists(addedLater, cache));

        cache.OnPlaylistWritten(playlist, [inPlaylist.Id, addedLater.Id]);

        Assert.Equal(["ZZTest Alpha [Smart]"], ExtractPlaylists(addedLater, cache));
    }

    /// <summary>
    /// ...and a movie removed from it must stop being reported, which is the half that made a
    /// "not in any smart playlist" rule keep excluding items it should have released.
    /// </summary>
    [Fact]
    public void OnPlaylistWritten_RetractsRemovedMembershipWithinTheSameDrain()
    {
        var dropped = TestItems.Mov("Blade Runner");
        var kept = TestItems.Mov("Alien");
        var playlist = PlaylistNamed("ZZTest Alpha [Smart]");

        var cache = new RefreshQueueService.RefreshCache();
        cache.AllPlaylists = [playlist];
        cache.PlaylistMembershipCache[playlist.Id] = [dropped.Id, kept.Id];

        Assert.Equal(["ZZTest Alpha [Smart]"], ExtractPlaylists(dropped, cache));

        cache.OnPlaylistWritten(playlist, [kept.Id]);

        Assert.Empty(ExtractPlaylists(dropped, cache));
        Assert.Equal(["ZZTest Alpha [Smart]"], ExtractPlaylists(kept, cache));
    }

    /// <summary>
    /// The collection side of the same fix.
    /// </summary>
    [Fact]
    public void OnCollectionWritten_MakesNewMembershipVisibleWithinTheSameDrain()
    {
        var inCollection = TestItems.Mov("Blade Runner");
        var addedLater = TestItems.Mov("Alien");
        var boxSet = CollectionNamed("ZZTest Alpha [Smart]");

        var cache = new RefreshQueueService.RefreshCache();
        cache.AllCollections = [boxSet];
        cache.CollectionDirectChildren[boxSet.Id] = [inCollection];

        Assert.Equal(["ZZTest Alpha [Smart]"], ExtractCollections(inCollection, cache, depth: 1));
        Assert.Empty(ExtractCollections(addedLater, cache, depth: 1));

        cache.OnCollectionWritten(boxSet, [inCollection, addedLater]);

        Assert.Equal(["ZZTest Alpha [Smart]"], ExtractCollections(addedLater, cache, depth: 1));
    }

    /// <summary>
    /// A container created mid-drain is absent from the snapshot altogether, so it has to be added
    /// rather than merely patched.
    /// </summary>
    [Fact]
    public void OnCollectionWritten_AddsACollectionCreatedDuringTheDrain()
    {
        var movie = TestItems.Mov("Blade Runner");
        var existing = CollectionNamed("Existing [Smart]");
        var createdMidDrain = CollectionNamed("ZZTest New [Smart]");

        var cache = new RefreshQueueService.RefreshCache();
        cache.AllCollections = [existing];
        cache.CollectionDirectChildren[existing.Id] = [];

        Assert.Empty(ExtractCollections(movie, cache, depth: 1));

        cache.OnCollectionWritten(createdMidDrain, [movie]);

        Assert.Equal(["ZZTest New [Smart]"], ExtractCollections(movie, cache, depth: 1));
    }

    /// <summary>
    /// Patching must never seed an UNBUILT membership cache: the builders are guarded on the
    /// dictionary being empty, so a single seeded entry would make an empty cache look built and
    /// strand every other container with no children.
    /// </summary>
    [Fact]
    public void OnContainerWritten_DoesNotSeedAnUnbuiltMembershipCache()
    {
        var movie = TestItems.Mov("Blade Runner");
        var boxSet = CollectionNamed("ZZTest Alpha [Smart]");
        var playlist = PlaylistNamed("ZZTest Alpha [Smart]");

        var cache = new RefreshQueueService.RefreshCache();

        cache.OnCollectionWritten(boxSet, [movie]);
        cache.OnPlaylistWritten(playlist, [movie.Id]);

        Assert.True(cache.CollectionDirectChildren.IsEmpty);
        Assert.True(cache.PlaylistMembershipCache.IsEmpty);
    }

    /// <summary>
    /// "Hide when empty" deletes the Jellyfin container mid-drain; lists refreshed after it must
    /// stop seeing it.
    /// </summary>
    [Fact]
    public void OnContainerRemoved_HidesADeletedCollectionFromTheRestOfTheDrain()
    {
        var movie = TestItems.Mov("Blade Runner");
        var boxSet = CollectionNamed("ZZTest Alpha [Smart]");

        var cache = new RefreshQueueService.RefreshCache();
        cache.AllCollections = [boxSet];
        cache.CollectionDirectChildren[boxSet.Id] = [movie];

        Assert.Equal(["ZZTest Alpha [Smart]"], ExtractCollections(movie, cache, depth: 1));

        cache.OnContainerRemoved(boxSet.Id);

        Assert.Empty(ExtractCollections(movie, cache, depth: 1));
    }
}
