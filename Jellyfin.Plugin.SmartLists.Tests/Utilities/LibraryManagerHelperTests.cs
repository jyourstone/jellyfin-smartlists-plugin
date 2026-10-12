using Jellyfin.Plugin.SmartLists.Utilities;

namespace Jellyfin.Plugin.SmartLists.Tests.Utilities;

/// <summary>
/// Pins <see cref="LibraryManagerHelper.IntersectTopParentIds"/>: the pool scope is cut down to the
/// user's libraries and never comes back empty (an empty scope lifts the restriction).
/// </summary>
public class LibraryManagerHelperTests
{
    private static readonly Guid Movies = Guid.Parse("0f0e8a7c-1d2b-4c3a-9e8f-7a6b5c4d3e2f");
    private static readonly Guid Shows = Guid.Parse("1a2b3c4d-5e6f-4a5b-8c7d-9e0f1a2b3c4d");
    private static readonly Guid Music = Guid.Parse("2b3c4d5e-6f7a-4b6c-9d8e-0f1a2b3c4d5e");

    [Fact]
    public void KeepsOnlyLibrariesTheUserCanAccess()
    {
        var scoped = LibraryManagerHelper.IntersectTopParentIds([Movies, Shows, Music], [Shows, Guid.NewGuid()]);

        Assert.Equal([Shows], scoped);
    }

    [Fact]
    public void UserWithAllLibrariesKeepsTheLibraryScope()
    {
        var scoped = LibraryManagerHelper.IntersectTopParentIds([Movies, Shows], [Shows, Movies, Music]);

        Assert.Equal([Movies, Shows], scoped);
    }

    [Fact]
    public void NoSharedLibraryNeverReturnsAnEmptyScope()
    {
        var scoped = LibraryManagerHelper.IntersectTopParentIds([Movies, Shows], [Music]);

        var only = Assert.Single(scoped);
        Assert.DoesNotContain(only, new[] { Movies, Shows, Music });
    }
}
