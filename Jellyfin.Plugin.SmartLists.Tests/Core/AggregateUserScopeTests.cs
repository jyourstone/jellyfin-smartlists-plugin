using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.Orders;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.SmartLists.Tests.Core;

/// <summary>
/// Covers the user scope and the container-child resolution behind the "(all users)"
/// PlayCount/LastPlayed sorts.
///
/// 1. "(all users)" sorts aggregate over EVERY user on the server, not just the users a list
///    happens to be shared with - that is the entire point of the name.
///
/// 2. Container aggregation (Series/Season/MusicAlbum -> children) resolves its child list on the
///    READ path, so every sort that aggregates containers sees it. The child caches are keyed by
///    container id and unfiltered by user visibility, so one entry serves every user being scored.
///    A warm-up owned by a single caller would not do: the owner-scoped sorts and Round Robin
///    "Least Recently Watched" read the same caches at different points in the refresh pipeline.
/// </summary>
public class AggregateUserScopeTests
{
    private static RefreshQueueService.RefreshCache LiveCache()
        => new() { LibraryManager = BaseItem.LibraryManager };

    private static void SeedPlayCount(RefreshQueueService.RefreshCache cache, BaseItem item, User user, int playCount)
    {
        cache.UserDataCache[(item.Id, user.Id)] = new UserItemData
        {
            Key = item.Id.ToString("N"),
            PlayCount = playCount,
        };
    }

    private static int PlayCount(Order order, BaseItem item, User user, RefreshQueueService.RefreshCache cache)
    {
        var key = order.GetSortKey(item, user, TestItems.ThrowingUserData(), null, null, cache);
        var composite = Assert.IsAssignableFrom<ICompositeSortKey>(key);
        return Assert.IsType<int>(composite.PrimaryValue);
    }

    private static DateTime LastPlayed(Order order, BaseItem item, User user, RefreshQueueService.RefreshCache cache)
        => Assert.IsType<DateTime>(order.GetSortKey(item, user, TestItems.ThrowingUserData(), null, null, cache));

    /// <summary>A list mapped to <paramref name="mappedUser"/> only, on a server that has <paramref name="serverUsers"/>.</summary>
    private static SmartList ListMappedOnlyTo(Order order, User mappedUser, params User[] serverUsers)
    {
        var dto = new SmartPlaylistDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Aggregate scope test",
            UserPlaylists =
            [
                new SmartPlaylistDto.UserPlaylistMapping { UserId = mappedUser.Id.ToString("N") }
            ],
        };

        return new SmartList(dto)
        {
            UserManager = TestItems.UserManagerWithUsers(serverUsers),
            Orders = [order],
        };
    }

    [Fact]
    public void AllUsersOrder_AggregatesEveryServerUser_EvenOnesNotSharedWithTheList()
    {
        var movie = TestItems.Mov("Movie");
        var cache = LiveCache();
        SeedPlayCount(cache, movie, TestItems.User, 3);
        SeedPlayCount(cache, movie, TestItems.OtherUser, 5);

        var order = new PlayCountTotalOrder();
        var list = ListMappedOnlyTo(order, TestItems.User, TestItems.User, TestItems.OtherUser);

        list.ConfigureAggregateUserOrders(TestItems.User, null);

        // OtherUser is not shared on this list (UserPlaylists names only TestItems.User), but
        // "(all users)" must still include them.
        Assert.Equal(8, PlayCount(order, movie, TestItems.User, cache));
    }

    [Fact]
    public void AllUsersOrder_WithoutResolvableServerUsers_FallsBackToTheCurrentUser()
    {
        var movie = TestItems.Mov("Movie");
        var cache = LiveCache();
        SeedPlayCount(cache, movie, TestItems.User, 3);
        SeedPlayCount(cache, movie, TestItems.OtherUser, 5);

        var order = new PlayCountTotalOrder();
        var list = ListMappedOnlyTo(order, TestItems.User); // server reports no users at all

        list.ConfigureAggregateUserOrders(TestItems.User, null);

        Assert.Equal(3, PlayCount(order, movie, TestItems.User, cache));
    }

    /// <summary>
    /// The regression guard for the owner-scoped sorts: they aggregate a container's children from
    /// the very same caches the "(all users)" sorts use, and nothing else in a list whose only sort
    /// is <c>PlayCount (owner)</c> ever populates them. Resolution therefore has to happen on the
    /// read path - if it moves back into a warm-up gated on aggregate-user orders, this fails.
    /// </summary>
    [Fact]
    public void OwnerPlayCount_ResolvesSeriesChildrenOnDemand_WithNoAggregateOrderInvolved()
    {
        var series = TestItems.Show("Show");
        var episode = TestItems.Ep("Show", 1, 1, show: series);
        TestLibraryManager.ItemListByParentId[series.Id] = [episode];

        var cache = LiveCache();
        SeedPlayCount(cache, episode, TestItems.User, 6);
        TestItems.SeedNoUserData(cache, series, TestItems.User);

        Assert.False(cache.SeriesEpisodesForAggregation.ContainsKey(series.Id));

        // No SmartList, no ConfigureAggregateUserOrders - just the plain owner sort.
        Assert.Equal(6, PlayCount(new PlayCountOrder(), series, TestItems.User, cache));
        Assert.True(cache.SeriesEpisodesForAggregation.ContainsKey(series.Id));
    }

    [Fact]
    public void OwnerLastPlayed_ResolvesSeriesChildrenOnDemand_WithNoAggregateOrderInvolved()
    {
        var series = TestItems.Show("Show");
        var episode = TestItems.Ep("Show", 1, 1, show: series);
        TestLibraryManager.ItemListByParentId[series.Id] = [episode];

        var played = new DateTime(2024, 3, 2);
        var cache = LiveCache();
        TestItems.SeedUserData(cache, episode, TestItems.User, played: true, lastPlayed: played);
        TestItems.SeedNoUserData(cache, series, TestItems.User);

        Assert.Equal(played, LastPlayed(new LastPlayedOrder(), series, TestItems.User, cache));
    }

    [Fact]
    public void AllUsersPlayCount_SumsEveryUsersCountOverAContainersChildren()
    {
        var series = TestItems.Show("Show");
        var episode = TestItems.Ep("Show", 1, 1, show: series);
        TestLibraryManager.ItemListByParentId[series.Id] = [episode];

        var cache = LiveCache();
        SeedPlayCount(cache, episode, TestItems.User, 2);
        SeedPlayCount(cache, episode, TestItems.OtherUser, 4);

        var order = new PlayCountTotalOrder();
        var list = ListMappedOnlyTo(order, TestItems.User, TestItems.User, TestItems.OtherUser);

        list.ConfigureAggregateUserOrders(TestItems.User, null);

        // One shared, visibility-unfiltered child list; each user's own playback row on top of it.
        Assert.Equal(6, PlayCount(order, series, TestItems.User, cache));
        Assert.Single(cache.SeriesEpisodesForAggregation);
    }

    /// <summary>
    /// With no library manager on the cache - every unit test that seeds the aggregation
    /// dictionaries by hand - a miss must stay a miss rather than reaching for a database.
    /// </summary>
    [Fact]
    public void ChildResolution_WithoutALibraryManager_DoesNotQuery()
    {
        var series = TestItems.Show("Show");
        var episode = TestItems.Ep("Show", 1, 1, show: series);
        TestLibraryManager.ItemListByParentId[series.Id] = [episode];

        var cache = new RefreshQueueService.RefreshCache(); // no LibraryManager
        SeedPlayCount(cache, episode, TestItems.User, 6);
        SeedPlayCount(cache, series, TestItems.User, 1);

        Assert.Equal(1, PlayCount(new PlayCountOrder(), series, TestItems.User, cache));
        Assert.Empty(cache.SeriesEpisodesForAggregation);
    }
}
