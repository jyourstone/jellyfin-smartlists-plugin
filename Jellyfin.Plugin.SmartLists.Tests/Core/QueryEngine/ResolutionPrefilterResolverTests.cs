using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Covers the pure operator/value -> MinHeight/MaxHeight window mapping of
/// <see cref="ResolutionPrefilterResolver"/> and its media-type gate. The mapping contract:
///
/// - The per-item bucketer classifies on max video-stream HEIGHT with upper-inclusive
///   boundaries (&lt;=480 -> 480p, &lt;=720 -> 720p, &lt;=1080 -> 1080p, &lt;=1440 -> 1440p,
///   &lt;=2160 -> 4K, else 8K), so each bucket is exactly the height range
///   (previous bucket height, bucket height].
/// - The lowest bucket's window starts at height 1: the compiled rule's validity gate
///   requires a positive extracted height, so height-0 items never match per-item.
/// - The top bucket (8K) is open-ended above - the bucketer maps EVERY height over 2160
///   to "8K" - so Equal/GreaterThanOrEqual "8K" must carry NO MaxHeight, and
///   LessThanOrEqual "8K" (which matches every valid-resolution item) cannot ride at all.
/// - NotEqual, unsupported operators, and values the compiled rule would reject all stay
///   per-item (null window).
///
/// The GetItemIds query itself needs a live Jellyfin and is exercised there, not here.
/// </summary>
public class ResolutionPrefilterResolverTests
{
    // ---- Full operator -> window mapping table ----

    [Theory]
    // Equal: (previous bucket height + 1) .. bucket height; top bucket open-ended above.
    [InlineData("Equal", "480p", 1, 480)]
    [InlineData("Equal", "720p", 481, 720)]
    [InlineData("Equal", "1080p", 721, 1080)]
    [InlineData("Equal", "1440p", 1081, 1440)]
    [InlineData("Equal", "4K", 1441, 2160)]
    [InlineData("Equal", "8K", 2161, null)]
    // GreaterThan: strictly above the bucket's own height.
    [InlineData("GreaterThan", "480p", 481, null)]
    [InlineData("GreaterThan", "720p", 721, null)]
    [InlineData("GreaterThan", "1080p", 1081, null)]
    [InlineData("GreaterThan", "1440p", 1441, null)]
    [InlineData("GreaterThan", "4K", 2161, null)]
    // Per-item nothing buckets above 8K, so this window's matches are all (harmless) false positives.
    [InlineData("GreaterThan", "8K", 4321, null)]
    // GreaterThanOrEqual: from the bucket's own lower boundary.
    [InlineData("GreaterThanOrEqual", "480p", 1, null)]
    [InlineData("GreaterThanOrEqual", "720p", 481, null)]
    [InlineData("GreaterThanOrEqual", "1080p", 721, null)]
    [InlineData("GreaterThanOrEqual", "1440p", 1081, null)]
    [InlineData("GreaterThanOrEqual", "4K", 1441, null)]
    [InlineData("GreaterThanOrEqual", "8K", 2161, null)]
    // LessThan: up to the previous bucket's height.
    // 480p edge: previous bucket height is 0 - per-item nothing can match (validity gate),
    // so the Height<=0 window's matches are all (harmless) false positives.
    [InlineData("LessThan", "480p", null, 0)]
    [InlineData("LessThan", "720p", null, 480)]
    [InlineData("LessThan", "1080p", null, 720)]
    [InlineData("LessThan", "1440p", null, 1080)]
    [InlineData("LessThan", "4K", null, 1440)]
    [InlineData("LessThan", "8K", null, 2160)]
    // LessThanOrEqual: up to the bucket's own height (top bucket cannot ride, see below).
    [InlineData("LessThanOrEqual", "480p", null, 480)]
    [InlineData("LessThanOrEqual", "720p", null, 720)]
    [InlineData("LessThanOrEqual", "1080p", null, 1080)]
    [InlineData("LessThanOrEqual", "1440p", null, 1440)]
    [InlineData("LessThanOrEqual", "4K", null, 2160)]
    public void OperatorValue_MapsToExactBucketWindow(string ruleOperator, string targetValue, int? expectedMin, int? expectedMax)
    {
        var window = ResolutionPrefilterResolver.TryBuildHeightWindow(ruleOperator, targetValue);

        Assert.NotNull(window);
        Assert.Equal(expectedMin, window.MinHeight);
        Assert.Equal(expectedMax, window.MaxHeight);
    }

    // ---- Combinations that stay per-item ----

    [Fact]
    public void LessThanOrEqualTopBucket_StaysPerItem()
    {
        // Matches every valid-resolution item (a >4320-height file buckets to "8K" and
        // still compares <= 8K per-item) - MaxHeight 4320 would drop it: false negative.
        Assert.Null(ResolutionPrefilterResolver.TryBuildHeightWindow("LessThanOrEqual", "8K"));
    }

    [Theory]
    [InlineData("480p")]
    [InlineData("1080p")]
    [InlineData("8K")]
    public void NotEqual_StaysPerItem(string targetValue)
    {
        Assert.Null(ResolutionPrefilterResolver.TryBuildHeightWindow("NotEqual", targetValue));
    }

    [Theory]
    [InlineData("Contains")]
    [InlineData("MatchRegex")]
    [InlineData("IsIn")]
    [InlineData("equal")] // operator comparison is Ordinal, mirroring the engine switch
    [InlineData("")]
    [InlineData(null)]
    public void UnsupportedOperators_StayPerItem(string? ruleOperator)
    {
        Assert.Null(ResolutionPrefilterResolver.TryBuildHeightWindow(ruleOperator, "1080p"));
    }

    [Theory]
    [InlineData("1080")] // bare height - compiled rule rejects it
    [InlineData("1080P")] // value lookup is ordinal exact, like GetHeightForResolution
    [InlineData("4k")]
    [InlineData("SD")]
    [InlineData("")]
    [InlineData(null)]
    public void ValuesTheCompiledRuleRejects_StayPerItem(string? targetValue)
    {
        Assert.Null(ResolutionPrefilterResolver.TryBuildHeightWindow("Equal", targetValue));
        Assert.Null(ResolutionPrefilterResolver.TryBuildHeightWindow("GreaterThan", targetValue));
    }

    // ---- Media-type gate: exclusively leaf video types ----

    [Theory]
    [InlineData("Movie")]
    [InlineData("Episode")]
    [InlineData("Video")]
    [InlineData("MusicVideo")]
    [InlineData("Movie,Episode,Video,MusicVideo")]
    public void LeafVideoPools_Apply(string mediaTypesCsv)
    {
        Assert.True(ResolutionPrefilterResolver.AppliesToMediaTypes(mediaTypesCsv.Split(',')));
    }

    [Theory]
    // Folder kinds have no row Width/Height - the window would shrink them to nothing.
    [InlineData("Series")]
    [InlineData("Season")]
    [InlineData("MusicAlbum")]
    // One folder type poisons the whole pool.
    [InlineData("Movie,Series")]
    [InlineData("Episode,Season")]
    // Non-video leaf types never carry video heights either.
    [InlineData("Audio")]
    [InlineData("Photo")]
    [InlineData("Book")]
    public void NonLeafVideoPools_Skip(string mediaTypesCsv)
    {
        Assert.False(ResolutionPrefilterResolver.AppliesToMediaTypes(mediaTypesCsv.Split(',')));
    }

    [Fact]
    public void UnrestrictedPools_Skip()
    {
        Assert.False(ResolutionPrefilterResolver.AppliesToMediaTypes(null));
        Assert.False(ResolutionPrefilterResolver.AppliesToMediaTypes([]));
    }

    // ---- Window/bucketer coherence ----

    [Fact]
    public void EqualWindows_TileTheHeightAxisWithoutGapsOrOverlaps()
    {
        // The Equal windows must partition [1, +inf) exactly like the bucketer does:
        // each window starts one above the previous bucket and the top one is open-ended.
        var expectedNextMin = 1;
        foreach (var info in ResolutionTypes.AllResolutions)
        {
            var window = ResolutionPrefilterResolver.TryBuildHeightWindow("Equal", info.Value);

            Assert.NotNull(window);
            Assert.Equal(expectedNextMin, window.MinHeight);
            if (window.MaxHeight != null)
            {
                expectedNextMin = window.MaxHeight.Value + 1;
            }
        }

        // Exactly one open-ended window, and it is the last bucket's.
        var top = ResolutionPrefilterResolver.TryBuildHeightWindow("Equal", "8K");
        Assert.NotNull(top);
        Assert.Null(top.MaxHeight);
    }
}
