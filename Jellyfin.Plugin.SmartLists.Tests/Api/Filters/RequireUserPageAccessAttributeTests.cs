using System.Reflection;
using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartLists.Api.Controllers;
using Jellyfin.Plugin.SmartLists.Api.Filters;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Jellyfin.Plugin.SmartLists.Tests.Api.Filters;

/// <summary>
/// Pins <see cref="RequireUserPageAccessAttribute"/>: which requests it stops, with which result, and that
/// the user API is behind it. Plugin.Instance is never set in tests, so a non-admin always meets the
/// fail-closed "page disabled" branch; the rule itself is covered by UserPageAccessTests.
/// </summary>
public class RequireUserPageAccessAttributeTests
{
    private static readonly User Admin = CreateAdmin();
    private static readonly IUserManager Users = TestItems.UserManagerWithUsers(Admin, TestItems.User);

    private static User CreateAdmin()
    {
        var admin = new User("admin", "authProviderId", "pwResetProviderId");
        admin.SetPermission(PermissionKind.IsAdministrator, true);
        return admin;
    }

    private sealed class UserManagerProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IUserManager) ? Users : null;
    }

    private static ActionExecutingContext Run(Guid? userId, params object[] endpointMetadata)
    {
        var httpContext = new DefaultHttpContext { RequestServices = new UserManagerProvider() };
        if (userId is Guid id)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", id.ToString())], "test"));
        }

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ControllerActionDescriptor { EndpointMetadata = [.. endpointMetadata] },
            new ModelStateDictionary());
        var context = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: null!);

        new RequireUserPageAccessAttribute().OnActionExecuting(context);
        return context;
    }

    private static string? Message(IActionResult? result)
    {
        var value = Assert.IsAssignableFrom<ObjectResult>(result).Value;
        return value?.GetType().GetProperty("message")?.GetValue(value) as string;
    }

    [Fact]
    public void UserApiController_IsBehindTheFilter()
    {
        Assert.NotNull(typeof(UserSmartListController).GetCustomAttribute<RequireUserPageAccessAttribute>());
    }

    [Fact]
    public void Admin_PassesThrough()
    {
        Assert.Null(Run(Admin.Id).Result);
    }

    [Fact]
    public void NoUser_IsUnauthorized()
    {
        var result = Assert.IsType<UnauthorizedObjectResult>(Run(null).Result);
        Assert.Equal("User not authenticated. Please log in to access SmartLists.", Message(result));
    }

    [Fact]
    public void NonAdmin_WhenPageDisabled_IsForbiddenWithTheDisabledMessage()
    {
        var result = Assert.IsType<ObjectResult>(Run(TestItems.User.Id).Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal(
            "The user page is currently disabled by an administrator. Please contact your administrator for access to SmartLists.",
            Message(result));
    }

    [Fact]
    public void AllowAnonymousAction_IsSkipped()
    {
        Assert.Null(Run(null, new AllowAnonymousAttribute()).Result);
        Assert.Null(Run(TestItems.User.Id, new AllowAnonymousAttribute()).Result);
    }
}
