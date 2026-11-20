using System.Text.Json;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartLists.Tests.Utilities;

/// <summary>
/// Pins <see cref="LibraryManagerHelper.IntersectTopParentIds"/>: the pool scope is cut down to the
/// user's libraries and never comes back empty (an empty scope lifts the restriction). Also pins
/// <see cref="LibraryManagerHelper.ApplyJellyfinSortKeys"/>.
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

    // ---------------------------------------------------------------------------------------
    // ApplyJellyfinSortKeys - the sort name the Manage Lists page mirrors Jellyfin's order with
    // ---------------------------------------------------------------------------------------

    /// <summary>A registered stand-in for a list's Jellyfin playlist or collection; returns its id.</summary>
    private static string JellyfinItem(string sortName, string? name = null)
    {
        var item = TestItems.PhysicalFolder(name ?? sortName);
        item.SortName = sortName;
        TestLibraryManager.Items[item.Id] = item;
        return item.Id.ToString("N");
    }

    private static string? SortNameOf(SmartListDto list)
    {
        LibraryManagerHelper.ApplyJellyfinSortKeys([list], BaseItem.LibraryManager);
        return list.JellyfinSortName;
    }

    /// <summary>
    /// The item's name comes along for the tie-break: Jellyfin orders equal sort names by the name
    /// it shows, prefix and suffix included, not by the list's own name.
    /// </summary>
    [Fact]
    public void ApplyJellyfinSortKeys_AlsoReadsTheJellyfinItemsName()
    {
        var list = new SmartCollectionDto { Name = "Rock", SortTitle = "rk", JellyfinCollectionId = JellyfinItem("rk", "Rock [Smart]") };

        LibraryManagerHelper.ApplyJellyfinSortKeys([list], BaseItem.LibraryManager);

        Assert.Equal("rk", list.JellyfinSortName);
        Assert.Equal("Rock [Smart]", list.JellyfinName);
    }

    [Fact]
    public void ApplyJellyfinSortKeys_ReadsTheCollectionsOrPlaylistsOwnSortName()
    {
        Assert.Equal("abyss [smart]", SortNameOf(new SmartCollectionDto { Name = "The Abyss", JellyfinCollectionId = JellyfinItem("abyss [smart]") }));
        Assert.Equal("legacy [smart]", SortNameOf(new SmartPlaylistDto { Name = "Legacy", JellyfinPlaylistId = JellyfinItem("legacy [smart]") }));
    }

    /// <summary>
    /// A playlist shared by several users is one Jellyfin playlist per user. A user whose playlist
    /// is gone (or was never created) must not hide the sort name the others carry.
    /// </summary>
    [Fact]
    public void ApplyJellyfinSortKeys_SkipsUserPlaylistsThatDoNotResolve()
    {
        var playlist = new SmartPlaylistDto
        {
            Name = "Shared",
            UserPlaylists =
            [
                new SmartPlaylistDto.UserPlaylistMapping { UserId = "a" },
                new SmartPlaylistDto.UserPlaylistMapping { UserId = "b", JellyfinPlaylistId = Guid.NewGuid().ToString("N") },
                new SmartPlaylistDto.UserPlaylistMapping { UserId = "c", JellyfinPlaylistId = JellyfinItem("shared [smart]") },
            ],
        };

        Assert.Equal("shared [smart]", SortNameOf(playlist));
    }

    [Fact]
    public void ApplyJellyfinSortKeys_LeavesAListWithoutAJellyfinItemEmpty()
    {
        Assert.Null(SortNameOf(new SmartCollectionDto { Name = "Never refreshed" }));
        Assert.Null(SortNameOf(new SmartCollectionDto { Name = "Bad id", JellyfinCollectionId = "not-a-guid" }));
        Assert.Null(SortNameOf(new SmartCollectionDto { Name = "Empty id", JellyfinCollectionId = Guid.Empty.ToString("N") }));

        var deleted = new SmartPlaylistDto { Name = "Deleted in Jellyfin", JellyfinPlaylistId = Guid.NewGuid().ToString("N") };
        Assert.Null(SortNameOf(deleted));
        Assert.Null(deleted.JellyfinName);
    }

    /// <summary>
    /// The sort name is response-only. It is written when a list endpoint has filled it, and never
    /// read back - not from a request body that echoes a fetched list, and not from disk - so it
    /// cannot end up stored in a list's config file.
    /// </summary>
    [Fact]
    public void JellyfinSortName_IsWrittenButNeverReadBack()
    {
        var options = SmartListFileSystem.SharedJsonOptions;

        var plain = JsonSerializer.Serialize(new SmartCollectionDto { Name = "Plain" }, options);
        Assert.DoesNotContain("JellyfinSortName", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("JellyfinName", plain, StringComparison.Ordinal);

        var filled = new SmartCollectionDto { Name = "Filled", JellyfinCollectionId = JellyfinItem("filled [smart]") };
        LibraryManagerHelper.ApplyJellyfinSortKeys([filled], BaseItem.LibraryManager);
        var json = JsonSerializer.Serialize(filled, options);

        Assert.Contains("\"JellyfinSortName\"", json, StringComparison.Ordinal);
        Assert.Contains("\"JellyfinName\"", json, StringComparison.Ordinal);

        var readBack = JsonSerializer.Deserialize<SmartListDto>(json, options)!;
        Assert.Null(readBack.JellyfinSortName);
        Assert.Null(readBack.JellyfinName);
        Assert.Null(JsonSerializer.Deserialize<SmartCollectionDto>(json, options)!.JellyfinSortName);
    }
}
