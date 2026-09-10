using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartLists.Tests.Utilities;

public class MediaStreamHelperAspectRatioTests
{
    [Fact]
    public void GetAspectRatio_ReturnsFirstValidVideoRatioVerbatim()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Kung Fu Panda" };
        cache.MediaStreamsCache[movie.Id] = new object[]
        {
            new MediaStream { Type = MediaStreamType.Audio, AspectRatio = "16:9" },
            new MediaStream { Type = MediaStreamType.Video, AspectRatio = "invalid" },
            new MediaStream { Type = MediaStreamType.Video, AspectRatio = "2.35:1" },
            new MediaStream { Type = MediaStreamType.Video, AspectRatio = "16:9" },
        };

        Assert.Equal("2.35:1", MediaStreamHelper.GetAspectRatio(movie, cache, null));
    }

    [Fact]
    public void GetAspectRatio_ReturnsEmptyWhenNoVideoHasAValidRatio()
    {
        var cache = new RefreshQueueService.RefreshCache();
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Unknown" };
        cache.MediaStreamsCache[movie.Id] = new object[]
        {
            new MediaStream { Type = MediaStreamType.Audio },
            new MediaStream { Type = MediaStreamType.Video, AspectRatio = null },
        };

        Assert.Empty(MediaStreamHelper.GetAspectRatio(movie, cache, null));
    }
}
