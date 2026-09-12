using System.Linq;
using Jellyfin.Data.Enums;
using MediaTypeConstants = Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.Constants;

public class MediaTypesTests
{
    [Fact]
    public void LiveTvChannel_MapsToLiveTvChannelKind_InBothDirections()
    {
        // The query side uses BaseItemKind.LiveTvChannel; the runtime GetBaseItemKind() of a
        // LiveTvChannel item is TvChannel, which is why OperandFactory/AutoRefreshService match
        // the class directly instead.
        Assert.Equal(BaseItemKind.LiveTvChannel, MediaTypeConstants.MediaTypeToBaseItemKind[MediaTypeConstants.LiveTvChannel]);
        Assert.Equal(MediaTypeConstants.LiveTvChannel, MediaTypeConstants.BaseItemKindToMediaType[BaseItemKind.LiveTvChannel]);
    }

    [Fact]
    public void Mappings_RoundTrip_AndAllHasNoDuplicates()
    {
        foreach (var (kind, mediaType) in MediaTypeConstants.BaseItemKindToMediaType)
        {
            Assert.Equal(kind, MediaTypeConstants.MediaTypeToBaseItemKind[mediaType]);
        }

        foreach (var (mediaType, kind) in MediaTypeConstants.MediaTypeToBaseItemKind)
        {
            Assert.Equal(mediaType, MediaTypeConstants.BaseItemKindToMediaType[kind]);
        }

        Assert.Equal(MediaTypeConstants.All.Length, MediaTypeConstants.All.Distinct().Count());
    }
}
