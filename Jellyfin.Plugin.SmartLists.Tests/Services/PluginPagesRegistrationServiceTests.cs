using Jellyfin.Plugin.SmartLists.Services;

namespace Jellyfin.Plugin.SmartLists.Tests.Services;

/// <summary>
/// Pins the contract on <see cref="PluginPagesRegistrationService.IsUserPageMenuVisible"/>: Plugin Pages
/// resolves it by reflection (GetTypes(), first type with the registered simple name, GetMethod(name)).
/// </summary>
public class PluginPagesRegistrationServiceTests
{
    [Fact]
    public void VisibilityCallback_ResolvesTheWayPluginPagesLooksItUp()
    {
        var types = typeof(PluginPagesRegistrationService).Assembly.GetTypes();

        var type = Assert.Single(types, t => t.Name == nameof(PluginPagesRegistrationService));
        // Throws AmbiguousMatchException if the callback ever gets an overload
        var method = type.GetMethod(nameof(PluginPagesRegistrationService.IsUserPageMenuVisible));

        Assert.NotNull(method);
        Assert.True(method.IsPublic && method.IsStatic);
        Assert.Equal(typeof(bool), method.ReturnType);
        Assert.Equal([typeof(string)], method.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void VisibilityCallback_WithoutRequestOrPlugin_HidesTheEntryWithoutThrowing()
    {
        Assert.False(PluginPagesRegistrationService.IsUserPageMenuVisible("smartlists-user-playlists"));
    }
}
