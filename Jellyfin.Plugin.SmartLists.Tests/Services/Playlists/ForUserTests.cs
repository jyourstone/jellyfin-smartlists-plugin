using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.Playlists;
using Xunit;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.Playlists;

/// <summary>
/// <see cref="PlaylistService.ForUser"/> scopes a multi-user playlist to one user's Jellyfin playlist.
/// The keep path restores un-badged covers from CustomImages, so dropping it is a silent no-op.
/// </summary>
public class ForUserTests
{
    [Fact]
    public void ForUser_CarriesIdentityAndCustomImages()
    {
        var images = new Dictionary<string, string> { ["Primary"] = "primary.png" };
        var playlist = new SmartPlaylistDto
        {
            Id = "3c2bd357-054f-486b-8bdf-56f7cb7cc045",
            Name = "Weekend",
            UserId = "owner",
            JellyfinPlaylistId = "first-users-playlist",
            CustomImages = images,
        };
        var mapping = new SmartPlaylistDto.UserPlaylistMapping { UserId = "second", JellyfinPlaylistId = "second-users-playlist" };

        var scoped = PlaylistService.ForUser(playlist, mapping);

        Assert.Equal(playlist.Id, scoped.Id);
        Assert.Equal("Weekend", scoped.Name);
        Assert.Equal("second", scoped.UserId);
        Assert.Equal("second-users-playlist", scoped.JellyfinPlaylistId);
        Assert.Same(images, scoped.CustomImages);
    }
}
