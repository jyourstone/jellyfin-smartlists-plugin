using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartLists.Configuration;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartLists.Tests.Utilities;

/// <summary>Pins <see cref="UserPageAccess.Evaluate"/>.</summary>
public class UserPageAccessTests
{
    private static readonly User Admin = CreateAdmin();
    private static readonly IUserManager Users = TestItems.UserManagerWithUsers(Admin, TestItems.User, TestItems.OtherUser);
    private static readonly Guid UserId = TestItems.User.Id;
    private static readonly Guid OtherId = TestItems.OtherUser.Id;

    private static User CreateAdmin()
    {
        var admin = new User("admin", "authProviderId", "pwResetProviderId");
        admin.SetPermission(PermissionKind.IsAdministrator, true);
        return admin;
    }

    private static PluginConfiguration Config(bool enabled, params string[] allowed) => new()
    {
        EnableUserPage = enabled,
        AllowedUserPageUsers = [.. allowed],
    };

    private static UserPageAccessResult Evaluate(Guid userId, PluginConfiguration? config) =>
        UserPageAccess.Evaluate(Users, userId, config);

    [Fact]
    public void Admin_IsAllowed_WhateverTheConfig()
    {
        Assert.Equal(UserPageAccessResult.Allowed, Evaluate(Admin.Id, null));
        Assert.Equal(UserPageAccessResult.Allowed, Evaluate(Admin.Id, Config(false)));
        Assert.Equal(UserPageAccessResult.Allowed, Evaluate(Admin.Id, Config(true, OtherId.ToString())));
        Assert.Equal(UserPageAccessResult.Allowed, Evaluate(Admin.Id, Config(false, OtherId.ToString())));
    }

    [Fact]
    public void NonAdmin_WithoutConfig_IsPageDisabled()
    {
        // Fail closed
        Assert.Equal(UserPageAccessResult.PageDisabled, Evaluate(UserId, null));
    }

    [Fact]
    public void NonAdmin_PageDisabled_IsPageDisabled_EvenWhenInAllowedList()
    {
        // Disabled outranks the list, so the API keeps answering with the "disabled by an administrator" message
        Assert.Equal(UserPageAccessResult.PageDisabled, Evaluate(UserId, Config(false)));
        Assert.Equal(UserPageAccessResult.PageDisabled, Evaluate(UserId, Config(false, UserId.ToString())));
    }

    [Fact]
    public void NonAdmin_PageEnabled_NoAllowedList_IsAllowed()
    {
        Assert.Equal(UserPageAccessResult.Allowed, Evaluate(UserId, Config(true)));
        // The default configuration (enabled, null list) lets everyone in
        Assert.Equal(UserPageAccessResult.Allowed, Evaluate(UserId, new PluginConfiguration()));
    }

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    public void NonAdmin_InAllowedList_IsAllowed_WhateverTheIdFormat(string format)
    {
        Assert.Equal(UserPageAccessResult.Allowed, Evaluate(UserId, Config(true, UserId.ToString(format))));
        Assert.Equal(UserPageAccessResult.Allowed, Evaluate(UserId, Config(true, OtherId.ToString(format), UserId.ToString(format))));
    }

    [Fact]
    public void NonAdmin_NotInAllowedList_IsUserNotAllowed()
    {
        Assert.Equal(UserPageAccessResult.UserNotAllowed, Evaluate(UserId, Config(true, OtherId.ToString())));
    }

    [Fact]
    public void UnparseableAllowedEntries_StillRestrictTheList()
    {
        // A non-empty list never means "everyone"
        Assert.Equal(UserPageAccessResult.UserNotAllowed, Evaluate(UserId, Config(true, "not-a-guid")));
    }

    [Fact]
    public void NoIdentifiableUser_IsANonAdminOnNoList()
    {
        // What the sidebar callback and the page see for a request without a user
        Assert.Equal(UserPageAccessResult.Allowed, Evaluate(Guid.Empty, Config(true)));
        Assert.Equal(UserPageAccessResult.UserNotAllowed, Evaluate(Guid.Empty, Config(true, UserId.ToString())));
        Assert.Equal(UserPageAccessResult.PageDisabled, Evaluate(Guid.Empty, Config(false)));
    }
}
