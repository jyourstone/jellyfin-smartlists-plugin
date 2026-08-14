using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Covers <see cref="AncestorValueResolver"/> - the memoized walk that replaced six one-level
/// "parent series" / "parent album" extractors - plus the two compile-time gates that decide
/// whether it runs at all.
///
/// The fixtures here are deliberately built with <c>ParentId</c> links (TestItems.Under) rather
/// than <c>SeriesId</c>. That is the whole point of issue #495: the old extractor resolved an
/// episode's <c>SeriesId</c> and read only that Series' Tags, jumping clean over the Season in
/// between, and never reaching the library at all. Several tests below are written so that they
/// FAIL against a SeriesId-based implementation - see
/// <see cref="Resolve_FindsTagSetOnTheSeason_TheLevelTheOldCodeSkipped"/>.
/// </summary>
public class AncestorWalkTests
{
    private static ConcurrentDictionary<Guid, AncestorValues> NewMemo() => new();

    private static AncestorValues Resolve(BaseItem item, ConcurrentDictionary<Guid, AncestorValues>? memo = null)
        => AncestorValueResolver.Resolve(item, BaseItem.LibraryManager, memo ?? NewMemo(), null);

    // ---------------------------------------------------------------------------------------
    // The walk itself
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Issue #495 proper. The tag lives on the SEASON - the level the old
    /// <c>ExtractParentSeriesTags</c> skipped by resolving <c>episode.SeriesId</c> straight to
    /// the Series. The episode here carries a valid <c>SeriesId</c> on purpose, so a regression
    /// back to that shortcut compiles and runs but returns the Series' tags only, and this test
    /// goes red.
    /// </summary>
    [Fact]
    public void Resolve_FindsTagSetOnTheSeason_TheLevelTheOldCodeSkipped()
    {
        var top = TestItems.PhysicalFolder("shows");
        var series = TestItems.Show("The IT Crowd");
        series.Tags = ["seriestag01"];
        var season = TestItems.SeasonOf("Season 1");
        season.Tags = ["seasontag99"];
        var episode = TestItems.Ep("The IT Crowd", 1, 1, show: series);

        TestItems.Under(series, top);
        TestItems.Under(season, series);
        TestItems.Under(episode, season);

        var values = Resolve(episode);

        // The shortcut the old code took IS available on this fixture - and is still wrong.
        Assert.Equal(series.Id, episode.SeriesId);
        Assert.NotEqual(series.Id, episode.ParentId);

        Assert.Contains("seasontag99", values.Tags);
        Assert.Contains("seriestag01", values.Tags);
    }

    /// <summary>
    /// A Jellyfin library (CollectionFolder) is NEVER in the ParentId chain - it hangs off the
    /// UserRootFolder as a sibling structure - so the walk has to union
    /// <c>GetCollectionFolders(chainTop)</c> on top of the parent chain. Drop that half and a
    /// library-level tag is invisible, which is the second symptom reported in #495.
    /// </summary>
    [Fact]
    public void Resolve_UnionsLibraryFolderValues_NotReachableViaParentChain()
    {
        var library = TestItems.PhysicalFolder("Serier", "librarytag01");
        var top = TestItems.PhysicalFolder("shows");
        TestLibraryManager.CollectionFolders[top.Id] = [library];

        var series = TestItems.Show("The IT Crowd");
        var season = TestItems.SeasonOf("Season 1");
        var episode = TestItems.Ep("The IT Crowd", 1, 1, show: series);

        TestItems.Under(series, top);
        TestItems.Under(season, series);
        TestItems.Under(episode, season);

        // Nothing points at the library: a parents-only walk terminates at `top` and can never
        // see it. This is what makes the assertion below load-bearing.
        Assert.Equal(Guid.Empty, top.ParentId);

        var values = Resolve(episode);

        Assert.Contains("librarytag01", values.Tags);
    }

    /// <summary>
    /// Tags, Studios and Genres are collected in ONE pass, from different levels of the same
    /// chain - that single pass is what replaced six separate extractors.
    /// </summary>
    [Fact]
    public void Resolve_CollectsTagsStudiosAndGenresInOneWalk()
    {
        var top = TestItems.PhysicalFolder("shows");
        top.Genres = ["Documentary"];
        var series = TestItems.Show("The IT Crowd");
        series.Studios = ["Channel 4"];
        var season = TestItems.SeasonOf("Season 1");
        season.Tags = ["seasontag99"];
        var episode = TestItems.Ep("The IT Crowd", 1, 1, show: series);

        TestItems.Under(series, top);
        TestItems.Under(season, series);
        TestItems.Under(episode, season);

        var values = Resolve(episode);

        Assert.Contains("seasontag99", values.Tags);
        Assert.Contains("Channel 4", values.Studios);
        Assert.Contains("Documentary", values.Genres);
    }

    /// <summary>
    /// The memo is keyed on the item's raw <c>ParentId</c> Guid and consulted BEFORE any parent
    /// is materialized, so a hit costs one dictionary lookup and ZERO ILibraryManager calls.
    /// Materializing the parent first (the obvious way to write this) would cost one
    /// <c>GetItemById</c> round-trip per ITEM instead of per container.
    /// </summary>
    [Fact]
    public void Resolve_MemoHitCostsNoLibraryCalls()
    {
        var top = TestItems.PhysicalFolder("shows");
        var series = TestItems.Show("The IT Crowd");
        var season = TestItems.SeasonOf("Season 1");
        season.Tags = ["seasontag99"];
        var first = TestItems.Ep("The IT Crowd", 1, 1, show: series);
        var second = TestItems.Ep("The IT Crowd", 1, 2, show: series);

        TestItems.Under(series, top);
        TestItems.Under(season, series);
        TestItems.Under(first, season);
        TestItems.Under(second, season);

        var memo = NewMemo();
        Resolve(first, memo);

        var before = TestLibraryManager.CallsFor(season.Id, series.Id, top.Id, second.Id);
        var values = Resolve(second, memo);
        var after = TestLibraryManager.CallsFor(season.Id, series.Id, top.Id, second.Id);

        Assert.Equal(before, after);
        Assert.Contains("seasontag99", values.Tags);
    }

    /// <summary>
    /// Memo entries are keyed by ANCESTOR NODE id, never by item id, so ten thousand episodes of
    /// five hundred seasons produce five hundred-ish entries, not ten thousand.
    /// </summary>
    [Fact]
    public void Resolve_MemoizesOneEntryPerAncestorNode()
    {
        var top = TestItems.PhysicalFolder("shows");
        var series = TestItems.Show("The IT Crowd");
        var season = TestItems.SeasonOf("Season 1");
        season.Tags = ["seasontag99"];
        var first = TestItems.Ep("The IT Crowd", 1, 1, show: series);
        var second = TestItems.Ep("The IT Crowd", 1, 2, show: series);

        TestItems.Under(series, top);
        TestItems.Under(season, series);
        TestItems.Under(first, season);
        TestItems.Under(second, season);

        var memo = NewMemo();
        Resolve(first, memo);
        Resolve(second, memo);

        Assert.Equal(3, memo.Count);
        Assert.Contains(season.Id, memo.Keys);
        Assert.Contains(series.Id, memo.Keys);
        Assert.Contains(top.Id, memo.Keys);
        Assert.DoesNotContain(first.Id, memo.Keys);
        Assert.DoesNotContain(second.Id, memo.Keys);
    }

    /// <summary>
    /// A ParentId cycle terminates instead of hanging, and writes NOTHING to the memo: a
    /// truncated walk is missing the top of the chain, so caching it would make later results
    /// depend on which item happened to warm the cache first.
    /// </summary>
    [Fact]
    public void Resolve_CycleTerminatesAndWritesNoMemoEntries()
    {
        var a = TestItems.PhysicalFolder("cycle-a", "atag");
        var b = TestItems.PhysicalFolder("cycle-b", "btag");
        TestItems.Under(a, b);
        TestItems.Under(b, a);

        var item = TestItems.Under(TestItems.Mov("Caught In A Loop"), a);

        var memo = NewMemo();
        var values = Resolve(item, memo);

        Assert.Empty(memo);
        Assert.Contains("atag", values.Tags);
        Assert.Contains("btag", values.Tags);
    }

    /// <summary>
    /// On the depth-cap path the library values are STILL resolved, from the deepest node the
    /// walk reached. <c>GetCollectionFolders</c> walks independently of the chain, so dropping it
    /// on truncation would silently reproduce the exact bug this change fixes.
    /// </summary>
    [Fact]
    public void Resolve_TruncatedWalkStillReturnsLibraryValues()
    {
        // The cap is 20; build 25 levels so it definitely binds.
        var folders = new List<Folder>();
        for (var i = 0; i < 25; i++)
        {
            folders.Add(TestItems.PhysicalFolder("deep-" + i, "tag" + i));
        }

        for (var i = 0; i < folders.Count - 1; i++)
        {
            TestItems.Under(folders[i], folders[i + 1]);
        }

        // The walk stops after collecting 20 nodes, so folders[19] is the deepest node reached
        // and therefore the anchor GetCollectionFolders is asked about.
        var library = TestItems.PhysicalFolder("Deep Library", "librarytag-deep");
        TestLibraryManager.CollectionFolders[folders[19].Id] = [library];

        var item = TestItems.Under(TestItems.Mov("Very Deep Movie"), folders[0]);

        var memo = NewMemo();
        var values = Resolve(item, memo);

        Assert.Contains("librarytag-deep", values.Tags);
        Assert.Contains("tag0", values.Tags);
        Assert.Contains("tag19", values.Tags);
        Assert.DoesNotContain("tag20", values.Tags);
        Assert.Empty(memo);
    }

    /// <summary>
    /// Extras have an empty <c>ParentId</c> and an <c>OwnerId</c> pointing at the item they belong
    /// to, so the walk falls back to <c>GetOwner()</c> - the same fallback core's own
    /// <c>GetCollectionFolders</c> uses.
    /// </summary>
    [Fact]
    public void Resolve_ExtraWithNoParentResolvesViaOwner()
    {
        var top = TestItems.PhysicalFolder("shows");
        var series = TestItems.Show("Modern Family");
        series.Tags = ["ownertag"];
        TestItems.Under(series, top);

        var extra = TestItems.Mov("Gag reel season 1");
        extra.OwnerId = series.Id;
        TestLibraryManager.Items[extra.Id] = extra;

        Assert.Equal(Guid.Empty, extra.ParentId);

        var values = Resolve(extra);

        Assert.Contains("ownertag", values.Tags);
    }

    /// <summary>
    /// The walk stops BEFORE AggregateFolder/UserRootFolder/UserView. Those are server plumbing,
    /// not user-facing containers, and climbing into them would leak values across libraries.
    /// </summary>
    [Fact]
    public void Resolve_StopsAtWalkBoundary_DoesNotClimbIntoAggregateFolder()
    {
        var root = new AggregateFolder { Id = Guid.NewGuid(), Name = "root" };
        root.SortName = "root";
        root.Tags = ["roottag"];

        var top = TestItems.PhysicalFolder("shows", "foldertag");
        TestItems.Under(top, root);

        var movie = TestItems.Under(TestItems.Mov("Boundary Movie"), top);
        var values = Resolve(movie);

        Assert.Contains("foldertag", values.Tags);
        Assert.DoesNotContain("roottag", values.Tags);

        // An item hanging directly off the boundary inherits nothing from the chain - and with no
        // library registered for it, gets the shared Empty singleton rather than a fresh allocation.
        var direct = TestItems.Under(TestItems.Mov("Directly Under Root"), root);
        Assert.Same(AncestorValues.Empty, Resolve(direct));
    }

    /// <summary>
    /// "No walkable ancestors" must not mean "no library". <c>GetCollectionFolders</c> resolves
    /// independently of the parent chain, so an item sitting directly under the boundary still
    /// belongs to a library and must still inherit its values. Returning Empty here would drop
    /// library values the same way the old one-level extractors dropped season values in #495.
    /// </summary>
    [Fact]
    public void Resolve_ItemDirectlyUnderBoundary_StillInheritsLibraryValues()
    {
        var root = new AggregateFolder { Id = Guid.NewGuid(), Name = "root" };
        root.SortName = "root";

        var movie = TestItems.Under(TestItems.Mov("Rootless Movie"), root);

        var library = TestItems.PhysicalFolder("Filmer", "movielibtag");
        TestLibraryManager.CollectionFolders[movie.Id] = [library];

        var values = Resolve(movie);

        Assert.Contains("movielibtag", values.Tags);
    }

    /// <summary>
    /// Same principle for an unresolvable parent: <c>ParentId</c> is set but the item it points at
    /// is gone from the library (deleted mid-refresh, stale reference), so both <c>GetParent()</c>
    /// and <c>GetOwner()</c> return null. The library lookup does not depend on that reference and
    /// must still contribute.
    /// </summary>
    [Fact]
    public void Resolve_UnresolvableParent_StillInheritsLibraryValues()
    {
        var orphan = TestItems.Mov("Orphaned Movie");
        orphan.ParentId = Guid.NewGuid();          // points at an id that was never registered
        TestLibraryManager.Items[orphan.Id] = orphan;

        Assert.Null(orphan.GetParent());

        var library = TestItems.PhysicalFolder("Filmer", "orphanlibtag");
        TestLibraryManager.CollectionFolders[orphan.Id] = [library];

        var values = Resolve(orphan);

        Assert.Contains("orphanlibtag", values.Tags);
    }

    /// <summary>
    /// De-duplication across levels is Ordinal, NOT OrdinalIgnoreCase (which is what core's
    /// GetInheritedTags uses). Six of the seven operators are case-insensitive so the difference
    /// is invisible to them, but MatchRegex is case-SENSITIVE, and case-insensitive dedup could
    /// discard the only casing a pattern would have matched.
    /// </summary>
    [Fact]
    public void Resolve_DedupesOrdinallyAcrossLevels()
    {
        var library = TestItems.PhysicalFolder("Serier", "Series01");
        var top = TestItems.PhysicalFolder("shows");
        TestLibraryManager.CollectionFolders[top.Id] = [library];

        var series = TestItems.Show("The IT Crowd");
        var season = TestItems.SeasonOf("Season 1");
        season.Tags = ["series01"];
        var episode = TestItems.Ep("The IT Crowd", 1, 1, show: series);

        TestItems.Under(series, top);
        TestItems.Under(season, series);
        TestItems.Under(episode, season);

        var values = Resolve(episode);

        Assert.Contains("Series01", values.Tags);
        Assert.Contains("series01", values.Tags);
        Assert.Equal(2, values.Tags.Count);
    }

    // ---------------------------------------------------------------------------------------
    // Walk output fed through the compiled rule
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Negative operators AND-fold, so a WIDER ancestor set can only REMOVE items. An episode
    /// whose season carries the tag is excluded by "Tags NotEqual &lt;tag&gt;"; its sibling in an
    /// untagged season survives.
    /// </summary>
    [Fact]
    public void CompileRule_TagsNotEqual_AcrossTwoLevelWalk_ExcludesWhenAnyAncestorMatches()
    {
        var top = TestItems.PhysicalFolder("shows");
        var series = TestItems.Show("The IT Crowd");
        TestItems.Under(series, top);

        var tagged = TestItems.SeasonOf("Season 1");
        tagged.Tags = ["seasontag99"];
        TestItems.Under(tagged, series);

        var clean = TestItems.SeasonOf("Season 2");
        TestItems.Under(clean, series);

        var taggedEpisode = TestItems.Under(TestItems.Ep("The IT Crowd", 1, 1, show: series), tagged);
        var cleanEpisode = TestItems.Under(TestItems.Ep("The IT Crowd", 2, 1, show: series), clean);

        var memo = NewMemo();
        var rule = Engine.CompileRule<Operand>(
            new Expression("Tags", "NotEqual", "seasontag99") { IncludeParentTags = true },
            string.Empty);

        Assert.False(rule(new Operand("tagged") { ParentTags = Resolve(taggedEpisode, memo).Tags }));
        Assert.True(rule(new Operand("clean") { ParentTags = Resolve(cleanEpisode, memo).Tags }));
    }

    /// <summary>
    /// MatchRegex is the one operator for which an EMPTY list and a NULL list differ: an empty
    /// list is tested against <c>string.Empty</c>, so <c>^$</c> matches "has no values here".
    /// That is why Operand's parent lists are initialized to <c>[]</c> and why the resolver never
    /// returns null.
    /// </summary>
    [Fact]
    public void CompileRule_TagsMatchRegexEmptyPattern_WithAndWithoutAncestorValues()
    {
        var onlyParent = Engine.CompileRule<Operand>(
            new Expression("Tags", "MatchRegex", "^$") { OnlyParentTags = true, IncludeParentTags = true },
            string.Empty);

        // No inherited tags -> the pattern is tested against string.Empty and matches, regardless
        // of what the item itself carries.
        Assert.True(onlyParent(new Operand("item") { Tags = ["Anime"], ParentTags = [] }));

        // Any inherited value at all switches to per-element matching, and "Anime" is not "".
        Assert.False(onlyParent(new Operand("item") { ParentTags = ["Anime"] }));

        // With the item's own field folded in, the two terms OR: an empty ancestor list alone is
        // enough to satisfy ^$ even when the item is tagged.
        var includeParent = Engine.CompileRule<Operand>(
            new Expression("Tags", "MatchRegex", "^$") { IncludeParentTags = true },
            string.Empty);

        Assert.True(includeParent(new Operand("item") { Tags = ["Anime"], ParentTags = [] }));
        Assert.False(includeParent(new Operand("item") { Tags = ["Anime"], ParentTags = ["Comedy"] }));
    }

    /// <summary>
    /// Engine reaches Operand through <c>Expression.PropertyOrField(param, "&lt;literal&gt;")</c>,
    /// so a spelling mismatch between Engine's string literals and Operand's property names
    /// compiles clean and only throws <see cref="ArgumentException"/> at rule-compile time - once
    /// per refresh, in production. This is the build-time-equivalent guard.
    /// </summary>
    [Fact]
    public void CompileRule_UsingNewParentFields_DoesNotThrowArgumentException()
    {
        var tags = Engine.CompileRule<Operand>(
            new Expression("Tags", "Equal", "Anime") { IncludeParentTags = true }, string.Empty);
        var studios = Engine.CompileRule<Operand>(
            new Expression("Studios", "Equal", "Channel 4") { IncludeParentStudios = true }, string.Empty);
        var genres = Engine.CompileRule<Operand>(
            new Expression("Genres", "Equal", "Jazz") { IncludeParentGenres = true }, string.Empty);

        Assert.True(tags(new Operand("item") { ParentTags = ["Anime"] }));
        Assert.True(studios(new Operand("item") { ParentStudios = ["Channel 4"] }));
        Assert.True(genres(new Operand("item") { ParentGenres = ["Jazz"] }));

        // The only-parent variants resolve a different field list; exercise them too.
        var onlyTags = Engine.CompileRule<Operand>(
            new Expression("Tags", "Equal", "Anime") { OnlyParentTags = true, IncludeParentTags = true }, string.Empty);
        Assert.True(onlyTags(new Operand("item") { ParentTags = ["Anime"] }));
    }

    // ---------------------------------------------------------------------------------------
    // The gates that decide whether the walk runs at all
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// THE silent-failure mode. Tags/Studios/Genres are registered as CHEAP fields, so
    /// <c>FieldRegistry.IsExpensiveField("Tags")</c> is false and this hardcoded predicate is the
    /// ONLY thing that promotes a parent-aware rule to the expensive tier. Miss it and the rule is
    /// evaluated in Phase 1 against an operand whose ParentTags the Factory reset to <c>[]</c>,
    /// matching nothing - with no exception and no log line.
    /// </summary>
    [Fact]
    public void IsNonExpensiveExpression_ClassifiesParentAwareTagsRuleAsExpensive()
    {
        var method = typeof(SmartList).GetMethod(
            "IsNonExpensiveExpression",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        bool IsNonExpensive(Expression expression) => (bool)method.Invoke(null, [expression])!;

        Assert.True(IsNonExpensive(new Expression("Tags", "Equal", "Anime")));

        Assert.False(IsNonExpensive(new Expression("Tags", "Equal", "Anime") { IncludeParentTags = true }));
        Assert.False(IsNonExpensive(new Expression("Tags", "Equal", "Anime") { OnlyParentTags = true }));
        Assert.False(IsNonExpensive(new Expression("Studios", "Equal", "Channel 4") { IncludeParentStudios = true }));
        Assert.False(IsNonExpensive(new Expression("Genres", "Equal", "Jazz") { IncludeParentGenres = true }));

        // Lists saved by older builds carry only the legacy keys. They must promote too, or a
        // pre-upgrade list silently stops matching after the upgrade.
        Assert.False(IsNonExpensive(new Expression("Tags", "Equal", "Anime") { IncludeParentSeriesTags = true }));
        Assert.False(IsNonExpensive(new Expression("Tags", "Equal", "Anime") { IncludeParentAlbumTags = true }));
        Assert.False(IsNonExpensive(new Expression("Studios", "Equal", "Channel 4") { IncludeParentSeriesStudios = true }));
        Assert.False(IsNonExpensive(new Expression("Genres", "Equal", "Jazz") { IncludeParentAlbumGenres = true }));
    }

    /// <summary>
    /// The compiled-rule cache is process-static, holds 1000 entries and cleans up no more often
    /// than every five minutes, so a rule-set hash that ignores a compilation-affecting flag
    /// produces a stale-rule bug that does NOT self-heal without a Jellyfin restart.
    /// </summary>
    [Fact]
    public void GenerateRuleSetHash_DiffersWhenIncludeParentTagsToggles()
    {
        var plain = Hash(new Expression("Tags", "Equal", "Anime"));
        var withParent = Hash(new Expression("Tags", "Equal", "Anime") { IncludeParentTags = true });
        var withLegacySeries = Hash(new Expression("Tags", "Equal", "Anime") { IncludeParentSeriesTags = true });

        Assert.NotEqual(plain, withParent);

        // The fold is a total function of behaviour: the legacy flag compiles to exactly the same
        // expression tree, so sharing a cache entry is correct, not a collision.
        Assert.Equal(withParent, withLegacySeries);

        Assert.NotEqual(
            Hash(new Expression("Studios", "Equal", "Channel 4")),
            Hash(new Expression("Studios", "Equal", "Channel 4") { IncludeParentStudios = true }));
        Assert.NotEqual(
            Hash(new Expression("Genres", "Equal", "Jazz")),
            Hash(new Expression("Genres", "Equal", "Jazz") { IncludeParentGenres = true }));
        Assert.NotEqual(
            withParent,
            Hash(new Expression("Tags", "Equal", "Anime") { IncludeParentTags = true, OnlyParentTags = true }));
    }

    private static string Hash(Expression expression)
    {
        var dto = new SmartPlaylistDto
        {
            Id = "ancestor-walk-hash-test",
            Name = "ancestor-walk-hash-test",
            ExpressionSets = [new ExpressionSet { Expressions = [expression] }],
        };

        var method = typeof(SmartList).GetMethod(
            "GenerateRuleSetHash",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        return (string)method.Invoke(new SmartList(dto), new object?[] { null })!;
    }

    // ---------------------------------------------------------------------------------------
    // On-disk back-compat
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Reading the legacy keys is permanent: lists saved by older builds still carry them, and
    /// every refresh re-serializes the whole DTO, so anything the model cannot read is ERASED
    /// from disk rather than merely ignored. New saves write only the new key, and the computed
    /// folds must never be serialized.
    /// </summary>
    [Fact]
    public void Expression_LegacyAlbumFlagFoldsIntoIncludeParentTagsEffective()
    {
        const string Json =
            """{"MemberName":"Tags","Operator":"Contains","TargetValue":"seasontag99","IncludeParentAlbumTags":true}""";

        var expr = JsonSerializer.Deserialize<Expression>(Json, SmartListFileSystem.SharedJsonOptions)!;

        Assert.True(expr.IncludeParentAlbumTags);
        Assert.Null(expr.IncludeParentTags);
        Assert.True(expr.IncludeParentTagsEffective);

        var roundTripped = JsonSerializer.Serialize(expr, SmartListFileSystem.SharedJsonOptions);

        Assert.Contains("IncludeParentAlbumTags", roundTripped, StringComparison.Ordinal);
        Assert.DoesNotContain("Effective", roundTripped, StringComparison.Ordinal);
        Assert.DoesNotContain("IncludeParentTags", roundTripped, StringComparison.Ordinal);
    }
}
