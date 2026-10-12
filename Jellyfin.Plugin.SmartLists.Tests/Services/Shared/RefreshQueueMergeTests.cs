using Jellyfin.Plugin.SmartLists.Services.Shared;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.Shared;

/// <summary>
/// The queue drops a refresh for a list that is already queued. A pending refresh scoped to some
/// users (a user-data auto refresh) must then be widened, or a scheduled full refresh arriving behind
/// it would only refresh the triggering users' copies. Null means all users.
/// </summary>
public class RefreshQueueMergeTests
{
    [Fact]
    public void FullRefresh_WidensAPendingUserScopedRefresh()
        => Assert.Null(RefreshQueueService.MergeTriggeringUserIds(["a"], null));

    [Fact]
    public void PendingFullRefresh_StaysFull()
        => Assert.Null(RefreshQueueService.MergeTriggeringUserIds(null, ["a"]));

    [Fact]
    public void TwoUserScopedRefreshes_CoverBothUsers()
        => Assert.Equal(["a", "b"], RefreshQueueService.MergeTriggeringUserIds(["a"], ["A", "b"]));
}
