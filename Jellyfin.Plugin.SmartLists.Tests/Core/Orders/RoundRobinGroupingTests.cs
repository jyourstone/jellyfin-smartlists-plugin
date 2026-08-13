using Jellyfin.Plugin.SmartLists.Core.Orders;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.Orders;

/// <summary>
/// Covers three static helpers inside <see cref="RoundRobinBase"/> that every round-robin sort
/// variant (Round Robin Ascending/Descending, Random, Shuffled, Least Recently Watched) shares:
/// how an item's group key is derived from the configured GroupByField
/// (<see cref="RoundRobinBase.ExtractGroupKey"/>), how two items in the same group are ordered
/// before interleaving (<see cref="RoundRobinBase.CompareWithinGroup"/>), and the Fisher-Yates
/// shuffle used for random group order / random within-group order
/// (<see cref="RoundRobinBase.Shuffle{T}"/>). Air blocks, air-date comparison, the interleave
/// itself, and the Least Recently Watched subclass are covered elsewhere.
///
/// Two things make these worth pinning at this level rather than trusting the interleave's own
/// end-to-end behaviour to catch regressions:
///
/// 1. ExtractGroupKey's switch has a DEFAULT ARM that falls back to item.Name for any field it
///    does not recognise. That is convenient when GroupByField is genuinely unset, but it also
///    means a typo'd field id (or a future field wired into the dropdown but not into this
///    switch) groups everything by item name SILENTLY - no exception, no log line. The test for
///    that arm is the regression guard for "I picked Genres and it grouped everything by name".
/// 2. CompareWithinGroup mixes THREE comparison strategies (season/episode ints, disc/track ints,
///    and a hand-rolled natural string comparer) behind one method, and a group is not guaranteed
///    to be single-type - Collections grouping can put a TV show and a movie franchise in the same
///    collection - so the mixed-type fallback path is the routine path for that field, not a
///    corner case.
///
/// Shuffle is pinned separately because it backs both "random group order" and "shuffle within
/// group": a Fisher-Yates bug (wrong loop bound, wrong swap index) would silently under-shuffle -
/// some permutations become unreachable - without ever throwing, so only a fixed-seed,
/// known-output test catches it.
/// </summary>
public class RoundRobinGroupingTests
{
    /// <summary>TestItems.Track only accepts a single artist; ExtractGroupKey's "first artist"
    /// behaviour needs at least two to be a meaningful test.</summary>
    private static Audio AudioWithArtists(string name, params string[] artists)
    {
        var item = new Audio { Id = Guid.NewGuid(), Name = name };
        item.SortName = item.Name;
        item.Artists = artists;
        return item;
    }

    // =================================================================================
    // ExtractGroupKey
    // =================================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyGroupByField_ReturnsEmptyString(string? groupByField)
    {
        var movie = TestItems.Mov("Anything");

        Assert.Equal(string.Empty, RoundRobinBase.ExtractGroupKey(movie, groupByField, null));
    }

    /// <summary>
    /// Episode grouping reads the denormalized <c>SeriesName</c> field directly, NOT
    /// <c>episode.Series?.Name</c> resolved through the library manager. Proven by registering a
    /// Series under a different name than the episode's own SeriesName: if this ever switched to
    /// reading through Series, the key would silently change to the registered show's name.
    /// </summary>
    [Fact]
    public void SeriesName_Episode_UsesTheDenormalizedSeriesNameProperty_NotTheResolvedSeries()
    {
        var show = TestItems.Show("Real Series Name");
        var episode = TestItems.Ep("Denormalized Name", 1, 1, show: show);

        Assert.Equal("Denormalized Name", RoundRobinBase.ExtractGroupKey(episode, "SeriesName", null));
    }

    [Fact]
    public void SeriesName_NonEpisode_FallsBackToItemName()
    {
        var movie = TestItems.Mov("Some Movie");

        Assert.Equal("Some Movie", RoundRobinBase.ExtractGroupKey(movie, "SeriesName", null));
    }

    [Fact]
    public void AlbumName_ReturnsItemAlbum()
    {
        var track = TestItems.Track("Abbey Road", disc: 1, track: 1);

        Assert.Equal("Abbey Road", RoundRobinBase.ExtractGroupKey(track, "AlbumName", null));
    }

    [Fact]
    public void AlbumName_ItemWithNoAlbum_ReturnsEmptyString()
    {
        var movie = TestItems.Mov("Some Movie");

        Assert.Equal(string.Empty, RoundRobinBase.ExtractGroupKey(movie, "AlbumName", null));
    }

    [Fact]
    public void Artist_Audio_ReturnsTheFirstListedArtist()
    {
        var track = AudioWithArtists("Track", "Zed Leppelin", "Alpha Guest");

        Assert.Equal("Zed Leppelin", RoundRobinBase.ExtractGroupKey(track, "Artist", null));
    }

    [Fact]
    public void Artist_NonAudio_ReturnsEmptyString()
    {
        var movie = TestItems.Mov("Some Movie");

        Assert.Equal(string.Empty, RoundRobinBase.ExtractGroupKey(movie, "Artist", null));
    }

    [Fact]
    public void Artist_AudioWithNoArtists_ReturnsEmptyString()
    {
        var track = TestItems.Track("Album", disc: 1, track: 1); // no artist passed

        Assert.Equal(string.Empty, RoundRobinBase.ExtractGroupKey(track, "Artist", null));
    }

    [Fact]
    public void Genres_ReturnsTheFirstGenre()
    {
        var movie = TestItems.Mov("Movie", genres: ["Action", "Thriller"]);

        Assert.Equal("Action", RoundRobinBase.ExtractGroupKey(movie, "Genres", null));
    }

    [Fact]
    public void Genres_Empty_ReturnsEmptyString()
    {
        var movie = TestItems.Mov("Movie");

        Assert.Equal(string.Empty, RoundRobinBase.ExtractGroupKey(movie, "Genres", null));
    }

    [Fact]
    public void Studios_ReturnsTheFirstStudio()
    {
        var movie = TestItems.Mov("Movie", studios: ["Warner Bros.", "Legendary"]);

        Assert.Equal("Warner Bros.", RoundRobinBase.ExtractGroupKey(movie, "Studios", null));
    }

    [Fact]
    public void Studios_Empty_ReturnsEmptyString()
    {
        var movie = TestItems.Mov("Movie");

        Assert.Equal(string.Empty, RoundRobinBase.ExtractGroupKey(movie, "Studios", null));
    }

    [Fact]
    public void Collections_MapHit_WinsOverTheItemsOwnSeriesName()
    {
        var episode = TestItems.Ep("Some Show", 1, 1);
        var map = TestItems.CollectionMap(("My Collection", new BaseItem[] { episode }));

        Assert.Equal("My Collection", RoundRobinBase.ExtractGroupKey(episode, "Collections", map));
    }

    [Fact]
    public void Collections_MapMiss_Episode_FallsBackToSeriesName()
    {
        var otherEpisode = TestItems.Ep("Other Show", 1, 1);
        var episode = TestItems.Ep("Some Show", 2, 3);
        var map = TestItems.CollectionMap(("Other Collection", new BaseItem[] { otherEpisode }));

        Assert.Equal("Some Show", RoundRobinBase.ExtractGroupKey(episode, "Collections", map));
    }

    [Fact]
    public void Collections_MapMiss_NonEpisode_FallsBackToItemName()
    {
        var movie = TestItems.Mov("Some Movie");
        var map = TestItems.CollectionMap();

        Assert.Equal("Some Movie", RoundRobinBase.ExtractGroupKey(movie, "Collections", map));
    }

    [Fact]
    public void Collections_NullMap_FallsBackTheSameWayAsAMapMiss()
    {
        var episode = TestItems.Ep("Some Show", 1, 1);
        var movie = TestItems.Mov("Some Movie");

        Assert.Equal("Some Show", RoundRobinBase.ExtractGroupKey(episode, "Collections", null));
        Assert.Equal("Some Movie", RoundRobinBase.ExtractGroupKey(movie, "Collections", null));
    }

    /// <summary>
    /// A typo'd or not-yet-wired field id hits the switch's default arm and groups by item name
    /// with no error anywhere - the user just sees an unexplained "Name" grouping.
    /// </summary>
    [Fact]
    public void UnrecognisedGroupByField_FallsBackToItemName()
    {
        var movie = TestItems.Mov("My Movie");

        Assert.Equal("My Movie", RoundRobinBase.ExtractGroupKey(movie, "NotARealField", null));
    }

    /// <summary>
    /// BuildInterleavedPositions groups keys case-insensitively (StringComparer.OrdinalIgnoreCase
    /// on the Dictionary), but ExtractGroupKey itself must hand back the RAW string - if a future
    /// change normalised casing here, that dictionary's case-insensitivity would become invisible
    /// (nothing would change), masking the regression.
    /// </summary>
    [Fact]
    public void ExtractGroupKey_ReturnsTheRawCasing_ItDoesNotNormaliseItItself()
    {
        var upper = TestItems.Mov("Movie1", genres: ["Action"]);
        var lower = TestItems.Mov("Movie2", genres: ["action"]);

        Assert.Equal("Action", RoundRobinBase.ExtractGroupKey(upper, "Genres", null));
        Assert.Equal("action", RoundRobinBase.ExtractGroupKey(lower, "Genres", null));
    }

    // =================================================================================
    // CompareWithinGroup
    // =================================================================================

    [Fact]
    public void TwoEpisodes_SeasonNumberDominatesEpisodeNumber()
    {
        var lateInSeasonOne = TestItems.Ep("Show", season: 1, episode: 99);
        var earlyInSeasonTwo = TestItems.Ep("Show", season: 2, episode: 1);

        Assert.True(RoundRobinBase.CompareWithinGroup(lateInSeasonOne, earlyInSeasonTwo) < 0);
        Assert.True(RoundRobinBase.CompareWithinGroup(earlyInSeasonTwo, lateInSeasonOne) > 0);
    }

    [Fact]
    public void TwoAudios_DiscNumberDominatesTrackNumber()
    {
        var lateOnDiscOne = TestItems.Track("Album", disc: 1, track: 99);
        var earlyOnDiscTwo = TestItems.Track("Album", disc: 2, track: 1);

        Assert.True(RoundRobinBase.CompareWithinGroup(lateOnDiscOne, earlyOnDiscTwo) < 0);
        Assert.True(RoundRobinBase.CompareWithinGroup(earlyOnDiscTwo, lateOnDiscOne) > 0);
    }

    /// <summary>
    /// Neither "both Episode" nor "both Audio", so this exercises the natural-string-comparer
    /// fallback. Names start with digits so the result distinguishes a numeric-aware comparer
    /// from a plain ordinal one: ordinal compare would put "10 ..." before "2 ..." on the
    /// '1' &lt; '2' byte, but SharedNaturalComparer parses the leading run of digits and compares
    /// it as a number, so "2 ..." sorts first.
    /// </summary>
    [Fact]
    public void MixedEpisodeAndMovie_FallsBackToTheNaturalComparerOnSortName()
    {
        var episode = TestItems.Ep("Show", season: 1, episode: 1, name: "2 Crossover Night");
        var movie = TestItems.Mov("10 Crossover Night");

        Assert.True(RoundRobinBase.CompareWithinGroup(episode, movie) < 0);
        Assert.True(RoundRobinBase.CompareWithinGroup(movie, episode) > 0);
    }

    [Fact]
    public void MixedAudioAndMovie_FallsBackToTheNaturalComparerOnSortName()
    {
        var track = TestItems.Track("Album", disc: 1, track: 1, name: "2 Crossover Track");
        var movie = TestItems.Mov("10 Crossover Track");

        Assert.True(RoundRobinBase.CompareWithinGroup(track, movie) < 0);
        Assert.True(RoundRobinBase.CompareWithinGroup(movie, track) > 0);
    }

    /// <summary>
    /// A comparer that isn't its own mirror image (Compare(a,b) != -Compare(b,a)) breaks List.Sort
    /// in ways that show up as items randomly missing their round-robin turn. Checked across all
    /// three branches (episode/episode, audio/audio, and the natural-comparer fallback) plus
    /// reflexivity (an item never outranks itself).
    /// </summary>
    [Fact]
    public void Comparisons_AreSymmetric_AcrossEveryBranch()
    {
        var epA = TestItems.Ep("Show", season: 1, episode: 1);
        var epB = TestItems.Ep("Show", season: 2, episode: 5);
        var audA = TestItems.Track("Album", disc: 1, track: 1);
        var audB = TestItems.Track("Album", disc: 2, track: 3);
        var movA = TestItems.Mov("Alpha");
        var movB = TestItems.Mov("2 Fast");

        BaseItem[] sample = [epA, epB, audA, audB, movA, movB];

        foreach (var a in sample)
        {
            Assert.Equal(0, RoundRobinBase.CompareWithinGroup(a, a));

            foreach (var b in sample)
            {
                Assert.Equal(
                    RoundRobinBase.CompareWithinGroup(a, b),
                    -RoundRobinBase.CompareWithinGroup(b, a));
            }
        }
    }

    /// <summary>
    /// Sorts the same six mixed-type items from two different starting arrangements and requires
    /// an identical result, which only holds if the comparator resolves every pair the same way
    /// regardless of List.Sort's internal pivot choices - i.e. it is a well-defined total order,
    /// not just "symmetric for adjacent pairs".
    /// </summary>
    [Fact]
    public void Sorting_ProducesTheSameOrder_RegardlessOfStartingArrangement()
    {
        var epA = TestItems.Ep("Show", season: 1, episode: 1);
        var epB = TestItems.Ep("Show", season: 2, episode: 5);
        var audA = TestItems.Track("Album", disc: 1, track: 1);
        var audB = TestItems.Track("Album", disc: 2, track: 3);
        var movA = TestItems.Mov("Alpha");
        var movB = TestItems.Mov("2 Fast");

        var forward = new List<BaseItem> { movB, epB, audB, movA, epA, audA };
        var reversed = new List<BaseItem>(forward);
        reversed.Reverse();

        forward.Sort((a, b) => RoundRobinBase.CompareWithinGroup(a, b));
        reversed.Sort((a, b) => RoundRobinBase.CompareWithinGroup(a, b));

        string[] expected = ["2 Fast", "Album D1T01", "Album D2T03", "Alpha", "Show S01E01", "Show S02E05"];
        Assert.Equal(expected, TestItems.Names(forward));
        Assert.Equal(expected, TestItems.Names(reversed));
    }

    // =================================================================================
    // Shuffle
    // =================================================================================

    /// <summary>
    /// Hard-codes the actual output for seed 42 on a 5-element list. This pins the exact
    /// Fisher-Yates recipe (loop from Count-1 down to 1, swap index rng.Next(i + 1)) - a
    /// plausible-looking variant (e.g. looping to 0, or rng.Next(i) instead of rng.Next(i + 1))
    /// would still "shuffle" but produce a different sequence for this seed, so this is the only
    /// kind of test that catches that class of bug.
    /// </summary>
    [Fact]
    public void FixedSeed_ProducesAnExactReproduciblePermutation()
    {
        var list = new List<string> { "A", "B", "C", "D", "E" };

        RoundRobinBase.Shuffle(list, new Random(42));

        Assert.Equal(["C", "B", "E", "A", "D"], list);
    }

    [Fact]
    public void SameSeed_ProducesIdenticalResultsFromIndependentRandomInstances()
    {
        var first = new List<string> { "A", "B", "C", "D", "E" };
        var second = new List<string> { "A", "B", "C", "D", "E" };

        RoundRobinBase.Shuffle(first, new Random(42));
        RoundRobinBase.Shuffle(second, new Random(42));

        Assert.Equal(first, second);
    }

    /// <summary>Guards against a Shuffle that ignores the passed-in Random entirely (e.g. a fixed
    /// rotation) and would otherwise pass every other test in this section by accident.</summary>
    [Fact]
    public void DifferentSeeds_ProduceDifferentPermutations()
    {
        var seed42 = new List<string> { "A", "B", "C", "D", "E" };
        var seed7 = new List<string> { "A", "B", "C", "D", "E" };

        RoundRobinBase.Shuffle(seed42, new Random(42));
        RoundRobinBase.Shuffle(seed7, new Random(7));

        Assert.NotEqual(seed42, seed7);
    }

    [Fact]
    public void IsAPermutation_PreservesTheMultisetAndCount()
    {
        var original = new List<int> { 1, 2, 2, 3, 4, 5, 5, 6, 7, 8 };
        var shuffled = new List<int>(original);

        RoundRobinBase.Shuffle(shuffled, new Random(123));

        Assert.Equal(original.Count, shuffled.Count);
        Assert.Equal(original.OrderBy(x => x), shuffled.OrderBy(x => x));
    }

    [Fact]
    public void EmptyList_IsANoOp_AndDoesNotThrow()
    {
        var list = new List<string>();

        var exception = Record.Exception(() => RoundRobinBase.Shuffle(list, new Random(42)));

        Assert.Null(exception);
        Assert.Empty(list);
    }

    [Fact]
    public void SingleElementList_IsANoOp_AndDoesNotThrow()
    {
        var list = new List<string> { "Solo" };

        var exception = Record.Exception(() => RoundRobinBase.Shuffle(list, new Random(42)));

        Assert.Null(exception);
        Assert.Equal(["Solo"], list);
    }
}
