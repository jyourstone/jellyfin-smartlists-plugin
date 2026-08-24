using System;
using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.Orders;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.SmartLists.Tests.Core;

/// <summary>
/// Covers two defects flagged in PR #511 review for the all-users PlayCount/LastPlayed sorting
/// feature, both in <c>SmartList.ConfigureAggregateUserOrders</c>:
///
/// 1. HIGH (Greptile): "(all users)" sorts must aggregate over EVERY user on the server, not just
///    the users a list happens to be shared with - that is the entire point of the "(all users)"
///    name. The legacy "(selected users total)" aliases must keep the original, narrower
///    playlist-scoped resolution so previously-saved lists don't silently change behavior.
///
/// 2. MEDIUM (CodeRabbit): container aggregation (Series/Season/MusicAlbum -> children) reads
///    per-(item,user) child caches that are otherwise only ever warmed for whichever single user
///    happens to trigger an unrelated rule's extraction. Aggregate sorts need those caches warm
///    for EVERY configured aggregate user, or per-user lookups silently miss and fall back to
///    0 / DateTime.MinValue for anyone but that one user.
/// </summary>
public class AggregateUserScopeTests
{
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

    /// <summary>Invokes the private aggregate-user resolution + cache warm-up SmartList performs before sorting.</summary>
    private static void ConfigureAggregateUserOrders(
        SmartList list,
        IReadOnlyCollection<BaseItem> items,
        ILibraryManager libraryManager,
        User currentUser,
        RefreshQueueService.RefreshCache cache)
    {
        var method = typeof(SmartList).GetMethod(
            "ConfigureAggregateUserOrders",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        method.Invoke(list, [items, libraryManager, currentUser, cache, null]);
    }

    private static SmartList ListMappedOnlyTo(User mappedUser, params User[] serverUsers)
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
        };
    }

    [Fact]
    public void AllUsersOrder_AggregatesEveryServerUser_EvenOnesNotSharedWithTheList()
    {
        var movie = TestItems.Mov("Movie");
        var cache = new RefreshQueueService.RefreshCache();
        SeedPlayCount(cache, movie, TestItems.User, 3);
        SeedPlayCount(cache, movie, TestItems.OtherUser, 5);

        var order = new PlayCountTotalOrder();
        var list = ListMappedOnlyTo(TestItems.User, TestItems.User, TestItems.OtherUser);
        list.Orders = [order];

        ConfigureAggregateUserOrders(list, [movie], BaseItem.LibraryManager, TestItems.User, cache);

        // OtherUser was never shared on this list (only in UserPlaylists is TestItems.User), but
        // "(all users)" must still include them.
        Assert.Equal(8, PlayCount(order, movie, TestItems.User, cache));
    }

    [Fact]
    public void SelectedUsersTotalOrder_StaysScopedToPlaylistUsers_NotEveryServerUser()
    {
        var movie = TestItems.Mov("Movie");
        var cache = new RefreshQueueService.RefreshCache();
        SeedPlayCount(cache, movie, TestItems.User, 3);
        SeedPlayCount(cache, movie, TestItems.OtherUser, 5);

        var order = new PlayCountSelectedUsersTotalOrder();
        var list = ListMappedOnlyTo(TestItems.User, TestItems.User, TestItems.OtherUser);
        list.Orders = [order];

        ConfigureAggregateUserOrders(list, [movie], BaseItem.LibraryManager, TestItems.User, cache);

        // Legacy alias must keep the original, narrower behavior: only the playlist's mapped
        // user (TestItems.User), never OtherUser.
        Assert.Equal(3, PlayCount(order, movie, TestItems.User, cache));
    }

    [Fact]
    public void AllUsersOrder_WarmsSeriesEpisodeCache_ForEveryAggregateUser_NotJustTheCurrentUser()
    {
        var series = TestItems.Show("Show");
        var episode = TestItems.Ep("Show", 1, 1, show: series);
        TestLibraryManager.ItemListByParentId[series.Id] = [episode];

        var cache = new RefreshQueueService.RefreshCache();
        SeedPlayCount(cache, episode, TestItems.User, 2);
        SeedPlayCount(cache, episode, TestItems.OtherUser, 4);

        var order = new PlayCountTotalOrder();
        var list = ListMappedOnlyTo(TestItems.User, TestItems.User, TestItems.OtherUser);
        list.Orders = [order];

        // Before this call, refreshCache.SeriesEpisodesForAggregation has NO entry for the series -
        // nothing else warms it for a list whose only rule/sort is an aggregate PlayCount sort.
        Assert.False(cache.SeriesEpisodesForAggregation.ContainsKey(series.Id));

        ConfigureAggregateUserOrders(list, [series], BaseItem.LibraryManager, TestItems.User, cache);

        // The child cache is keyed by container id only (unfiltered by user visibility), so a
        // single warm-up covers every aggregate user - not one entry per user.
        Assert.True(cache.SeriesEpisodesForAggregation.ContainsKey(series.Id));

        // ...and the aggregate sort must actually see both users' data (2 + 4 = 6), not silently
        // fall back to 0 for the user whose cache would otherwise have been cold.
        Assert.Equal(6, PlayCount(order, series, TestItems.User, cache));
    }
}
