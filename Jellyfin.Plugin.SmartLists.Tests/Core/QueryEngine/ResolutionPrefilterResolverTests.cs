using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Covers the pure operator/value -> MinHeight mapping of
/// <see cref="ResolutionPrefilterResolver"/> and its media-type gate. The mapping contract:
///
/// - The per-item bucketer classifies on max video-stream HEIGHT with upper-inclusive
///   boundaries (&lt;=480 -> 480p, &lt;=720 -> 720p, &lt;=1080 -> 1080p, &lt;=1440 -> 1440p,
///   &lt;=2160 -> 4K, else 8K), so each bucket starts one above the previous bucket height.
/// - The lowest bucket starts at height 1: the compiled rule's validity gate requires a
///   positive extracted height, so height-0 items never match per-item.
/// - Only lower bounds are pushed down. Jellyfin 12's MaxHeight drops an item when ANY of
///   its alternate versions is taller, while the per-item path reads the primary only, so
///   an upper bound would drop items that match per-item. LessThan/LessThanOrEqual, which
///   have no lower bound, therefore stay per-item.
/// - NotEqual, unsupported operators, and values the compiled rule would reject all stay
///   per-item (null).
///
/// The GetItemIds query itself needs a live Jellyfin and is exercised there, not here.
/// </summary>
public class ResolutionPrefilterResolverTests
{
    // ---- Full operator -> MinHeight mapping table ----

    [Theory]
    // Equal: previous bucket height + 1 (the upper bound is enforced per-item).
    [InlineData("Equal", "480p", 1)]
    [InlineData("Equal", "720p", 481)]
    [InlineData("Equal", "1080p", 721)]
    [InlineData("Equal", "1440p", 1081)]
    [InlineData("Equal", "4K", 1441)]
    [InlineData("Equal", "8K", 2161)]
    // GreaterThan: strictly above the bucket's own height.
    [InlineData("GreaterThan", "480p", 481)]
    [InlineData("GreaterThan", "720p", 721)]
    [InlineData("GreaterThan", "1080p", 1081)]
    [InlineData("GreaterThan", "1440p", 1441)]
    [InlineData("GreaterThan", "4K", 2161)]
    // Per-item nothing buckets above 8K, so this bound's matches are all (harmless) false positives.
    [InlineData("GreaterThan", "8K", 4321)]
    // GreaterThanOrEqual: from the bucket's own lower boundary.
    [InlineData("GreaterThanOrEqual", "480p", 1)]
    [InlineData("GreaterThanOrEqual", "720p", 481)]
    [InlineData("GreaterThanOrEqual", "1080p", 721)]
    [InlineData("GreaterThanOrEqual", "1440p", 1081)]
    [InlineData("GreaterThanOrEqual", "4K", 1441)]
    [InlineData("GreaterThanOrEqual", "8K", 2161)]
    public void OperatorValue_MapsToBucketMinHeight(string ruleOperator, string targetValue, int expectedMin)
    {
        Assert.Equal(expectedMin, ResolutionPrefilterResolver.TryBuildMinHeight(ruleOperator, targetValue));
    }

    // ---- Combinations that stay per-item ----

    [Theory]
    [InlineData("LessThan")]
    [InlineData("LessThanOrEqual")]
    public void UpperBoundOperators_StayPerItem(string ruleOperator)
    {
        // A pushed-down MaxHeight would drop a lower-resolution primary whose alternate
        // version is taller (Jellyfin 12 version-aware MaxHeight) - false negative.
        foreach (var info in ResolutionTypes.AllResolutions)
        {
            Assert.Null(ResolutionPrefilterResolver.TryBuildMinHeight(ruleOperator, info.Value));
        }
    }

    [Theory]
    [InlineData("480p")]
    [InlineData("1080p")]
    [InlineData("8K")]
    public void NotEqual_StaysPerItem(string targetValue)
    {
        Assert.Null(ResolutionPrefilterResolver.TryBuildMinHeight("NotEqual", targetValue));
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
        Assert.Null(ResolutionPrefilterResolver.TryBuildMinHeight(ruleOperator, "1080p"));
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
        Assert.Null(ResolutionPrefilterResolver.TryBuildMinHeight("Equal", targetValue));
        Assert.Null(ResolutionPrefilterResolver.TryBuildMinHeight("GreaterThan", targetValue));
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
    // Folder kinds have no row Width/Height - the bound would shrink them to nothing.
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

    // ---- Bound/bucketer coherence ----

    [Fact]
    public void EqualMinHeights_StartOneAboveThePreviousBucket()
    {
        // Each Equal bound must sit exactly at the bucketer's lower boundary: one above the
        // previous bucket's height, starting at 1 for the lowest bucket.
        var expectedMin = 1;
        foreach (var info in ResolutionTypes.AllResolutions)
        {
            Assert.Equal(expectedMin, ResolutionPrefilterResolver.TryBuildMinHeight("Equal", info.Value));
            expectedMin = info.Height + 1;
        }
    }
}
