using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Services.Playlists;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Xunit;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.Playlists;

/// <summary>
/// <see cref="PlaylistService.WeaveBumpers"/> inserts a break of bumpers after every N main items.
/// With one pool and a break size of 1 it must reproduce the original one-bumper weave exactly, so
/// lists saved before multi-bumper breaks keep their woven order.
/// </summary>
public class WeaveBumpersTests
{
    // Labels of 16 characters or fewer round-trip through a Guid, so assertions compare readable strings.
    private static Guid Id(string label) => new(Encoding.ASCII.GetBytes(label.PadRight(16)));

    private static string Label(Guid g) => Encoding.ASCII.GetString(g.ToByteArray()).TrimEnd();

    private static List<Guid> Ids(params string[] labels) => labels.Select(Id).ToList();

    private static List<Guid> Seq(string prefix, int n) => Enumerable.Range(1, n).Select(i => Id(prefix + i)).ToList();

    private static string[] Labels(List<Guid> ids) => ids.Select(Label).ToArray();

    // Verbatim copy of the weave body before #542 (one bumper per break, one pool).
    private static List<Guid> LegacyWeave(List<Guid> mainItemIds, List<Guid> bumperItemIds, int interval)
    {
        var result = new List<Guid>(mainItemIds.Count + (mainItemIds.Count / interval) + 1);
        int bumperIndex = 0;
        for (int i = 0; i < mainItemIds.Count; i++)
        {
            result.Add(mainItemIds[i]);
            bool isLast = i == mainItemIds.Count - 1;
            if (!isLast && (i + 1) % interval == 0)
            {
                result.Add(bumperItemIds[bumperIndex % bumperItemIds.Count]);
                bumperIndex++;
            }
        }

        return result;
    }

    [Fact]
    public void SinglePoolBreakSizeOne_ReproducesPre542WeaveExactly()
    {
        for (int mainCount = 0; mainCount <= 12; mainCount++)
        {
            for (int poolSize = 1; poolSize <= 5; poolSize++)
            {
                for (int interval = 1; interval <= 5; interval++)
                {
                    var main = Seq("M", mainCount);
                    var pool = Seq("B", poolSize);

                    Assert.Equal(LegacyWeave(main, pool, interval), PlaylistService.WeaveBumpers(main, [pool], interval, 1));
                }
            }
        }
    }

    [Fact]
    public void SinglePoolBreakSizeOne_MatchesDocumentedWorkedExample()
    {
        var woven = PlaylistService.WeaveBumpers(Seq("M", 6), [Seq("B", 4)], 1, 1);

        Assert.Equal(["M1", "B1", "M2", "B2", "M3", "B3", "M4", "B4", "M5", "B1", "M6"], Labels(woven));
    }

    [Fact]
    public void BreakSizeThree_SinglePool_DrawsConsecutively()
    {
        var woven = PlaylistService.WeaveBumpers(Seq("M", 3), [Seq("B", 6)], 1, 3);

        Assert.Equal(["M1", "B1", "B2", "B3", "M2", "B4", "B5", "B6", "M3"], Labels(woven));
    }

    [Fact]
    public void BreakSizeLargerThanPool_WrapsWithinAndAcrossBreaks()
    {
        var woven = PlaylistService.WeaveBumpers(Seq("M", 3), [Ids("B1", "B2")], 1, 3);

        Assert.Equal(["M1", "B1", "B2", "B1", "M2", "B2", "B1", "B2", "M3"], Labels(woven));
    }

    [Fact]
    public void TwoPools_TakeTurnsAndRestartAtFirstPoolEachBreak()
    {
        var woven = PlaylistService.WeaveBumpers(Seq("M", 5), [Seq("I", 3), Seq("C", 3)], 1, 3);

        Assert.Equal(
            ["M1", "I1", "C1", "I2", "M2", "I3", "C2", "I1", "M3", "I2", "C3", "I3", "M4", "I1", "C1", "I2", "M5"],
            Labels(woven));
    }

    // Break size 3 over 2 non-empty pools: the third slot returns to the first pool only when the
    // rotation counts non-empty pools (skipping ahead past the empty one would draw C twice).
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EmptyPoolIsDroppedFromRotation(int emptyIndex)
    {
        List<List<Guid>> pools = [Ids("I1", "I2"), Ids("C1", "C2")];
        pools.Insert(emptyIndex, []);

        var woven = PlaylistService.WeaveBumpers(Seq("M", 3), pools, 1, 3);

        Assert.Equal(["M1", "I1", "C1", "I2", "M2", "I1", "C2", "I2", "M3"], Labels(woven));
    }

    [Fact]
    public void OnlyOneNonEmptyPool_BehavesLikeSinglePool()
    {
        var woven = PlaylistService.WeaveBumpers(Seq("M", 3), [[], Seq("B", 3)], 1, 2);

        Assert.Equal(["M1", "B1", "B2", "M2", "B3", "B1", "M3"], Labels(woven));
    }

    [Fact]
    public void AllPoolsEmpty_ReturnsMainItemsUnchanged()
    {
        var woven = PlaylistService.WeaveBumpers(Seq("M", 3), [[], []], 1, 2);

        Assert.Equal(["M1", "M2", "M3"], Labels(woven));
    }

    [Fact]
    public void SingleMainItem_GetsNoBreak()
    {
        var woven = PlaylistService.WeaveBumpers(Ids("M1"), [Ids("I1"), Ids("C1")], 1, 3);

        Assert.Equal(["M1"], Labels(woven));
    }

    [Fact]
    public void EmptyMainList_ReturnsEmpty()
    {
        var woven = PlaylistService.WeaveBumpers([], [Ids("B1")], 1, 3);

        Assert.Empty(woven);
    }

    [Fact]
    public void NoBreakAfterFinalItem_WhenCountIsMultipleOfInterval()
    {
        var woven = PlaylistService.WeaveBumpers(Seq("M", 4), [Seq("B", 3)], 2, 2);

        Assert.Equal(["M1", "M2", "B1", "B2", "M3", "M4"], Labels(woven));
    }

    [Fact]
    public void MorePoolsThanBreakSize_LaterPoolsNeverPlay()
    {
        var woven = PlaylistService.WeaveBumpers(Seq("M", 3), [Ids("A1"), Ids("B1"), Ids("C1")], 1, 2);

        Assert.Equal(["M1", "A1", "B1", "M2", "A1", "B1", "M3"], Labels(woven));
    }

    // SplitBumperPools: the step between the merged bumper filter pass and the weave.

    private static List<List<Guid>> Split(
        List<Guid> sortedIds,
        Dictionary<Guid, List<int>>? mappings,
        int groupCount,
        out int unmappedCount,
        List<Guid>? mainIds = null,
        List<Guid>? unresolved = null)
        => PlaylistService.SplitBumperPools(
            sortedIds, mappings, groupCount, [.. mainIds ?? []], id => unresolved?.Contains(id) != true, out unmappedCount);

    [Fact]
    public void Split_WithoutMappings_PutsEveryIdInOnePoolInOrder()
    {
        var pools = Split(Ids("B3", "B1", "B2"), null, 3, out var unmapped);

        Assert.Equal([["B3", "B1", "B2"]], pools.Select(Labels));
        Assert.Equal(0, unmapped);
    }

    [Fact]
    public void Split_ByOriginalGroupIndex_ItemInBothGroupsJoinsBothPools()
    {
        var mappings = new Dictionary<Guid, List<int>>
        {
            [Id("I1")] = [0],
            [Id("X1")] = [0, 2],
            [Id("C1")] = [2],
        };

        // Group 1 matched nothing: its pool stays empty rather than shifting group 2 down
        var pools = Split(Ids("C1", "X1", "I1"), mappings, 3, out var unmapped);

        Assert.Equal([["X1", "I1"], [], ["C1", "X1"]], pools.Select(Labels));
        Assert.Equal(0, unmapped);
    }

    [Fact]
    public void Split_UnmappedIdIsLeftOutAndCounted()
    {
        var mappings = new Dictionary<Guid, List<int>> { [Id("I1")] = [0], [Id("C1")] = [1] };

        var pools = Split(Ids("I1", "U1", "C1"), mappings, 2, out var unmapped);

        Assert.Equal([["I1"], ["C1"]], pools.Select(Labels));
        Assert.Equal(1, unmapped);
    }

    [Fact]
    public void Split_SkipsMainIdsUnresolvedIdsAndRepeats()
    {
        var mappings = new Dictionary<Guid, List<int>> { [Id("I1")] = [0], [Id("M1")] = [0], [Id("V1")] = [1], [Id("C1")] = [1] };

        var pools = Split(Ids("I1", "M1", "V1", "C1", "I1"), mappings, 2, out var unmapped, mainIds: Ids("M1"), unresolved: Ids("V1"));

        Assert.Equal([["I1"], ["C1"]], pools.Select(Labels));
        Assert.Equal(0, unmapped);
    }

    [Fact]
    public void LegacyBumperJson_WithoutNewFields_DefaultsToOneBumperMergedPool()
    {
        var dto = JsonSerializer.Deserialize<SmartPlaylistDto>(
            "{\"Name\":\"Legacy\",\"Bumpers\":{\"ExpressionSets\":[],\"MediaTypes\":[\"Video\"],\"BumperOrder\":\"Name\",\"Interval\":3}}",
            SmartListFileSystem.SharedJsonOptions)!;

        Assert.NotNull(dto.Bumpers);
        Assert.Equal(1, dto.Bumpers.BreakSize);
        Assert.False(dto.Bumpers.AlternateRuleGroups);
    }
}
