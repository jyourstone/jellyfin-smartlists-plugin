using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using Expression = Jellyfin.Plugin.SmartLists.Core.QueryEngine.Expression;
using MediaTypeConstants = Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes;

namespace Jellyfin.Plugin.SmartLists.Tests.Core;

/// <summary>
/// SimilarTo is scored per rule block: a block containing SimilarTo rules matches an item when
/// that block's own compiled rules pass AND the item is similar to THAT block's references.
/// Blocks still OR together, so a block without SimilarTo is never filtered by another block's
/// reference, and an item's similarity score is the best score of the blocks it matched.
///
/// Two defects this pins down:
/// - A block holding nothing but SimilarTo rules produces no compiled rules. That used to take a
///   "matches = true, let similarity decide" path which left the item out of _itemGroupMappings -
///   and ApplyPerGroupLimits rebuilds its result purely from those mappings, so "Similar To X"
///   plus "Max Items for this block" returned an EMPTY list.
/// - Similarity used to be a single global AND-filter scored against the blended references of
///   every SimilarTo rule in the list. "Similar to Predator" OR "Genres contains Drama" therefore
///   returned the INTERSECTION - usually nothing - instead of the union.
/// </summary>
public class SimilarToGroupMappingTests
{
    /// <summary>Genre is the default similarity comparison field, so shared genres drive the match.</summary>
    private static Guid[] Filter(SmartList list, params BaseItem[] pool)
        => [.. list.FilterPlaylistItems(pool, BaseItem.LibraryManager, TestItems.User, new RefreshQueueService.RefreshCache())];

    private static SmartList SimilarToList(params ExpressionSet[] expressionSets)
        => new(new SmartPlaylistDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Similar To List",
            MediaTypes = [MediaTypeConstants.Movie],
            ExpressionSets = [.. expressionSets],
        });

    private static ExpressionSet SimilarToBlock(string reference, int? maxItems = null)
        => new()
        {
            Expressions = [new Expression("SimilarTo", "Equal", reference)],
            MaxItems = maxItems,
        };

    private static ExpressionSet GenreBlock(string genre)
        => new() { Expressions = [new Expression("Genres", "Contains", genre)] };

    [Fact]
    public void SimilarToOnlyBlock_WithPerBlockLimit_KeepsItsMatches()
    {
        var predator = TestItems.Mov("Predator", genres: ["Action", "Sci-Fi"]);
        var commando = TestItems.Mov("Commando", genres: ["Action", "Sci-Fi"]);
        var terminator = TestItems.Mov("The Terminator", genres: ["Action", "Sci-Fi"]);
        var notebook = TestItems.Mov("The Notebook", genres: ["Drama", "Romance"]);

        var list = SimilarToList(SimilarToBlock("Predator", maxItems: 2));

        var result = Filter(list, predator, commando, terminator, notebook);

        // Pre-fix this was empty: the block's matches were never tagged, so the per-block limiter
        // found no items in block 0 and returned nothing.
        Assert.Equal(2, result.Length);
        Assert.DoesNotContain(notebook.Id, result);
    }

    [Fact]
    public void TwoSimilarToOnlyBlocks_SplitTheSharedPoolAcrossTheirLimits()
    {
        var predator = TestItems.Mov("Predator", genres: ["Action", "Sci-Fi"]);
        var commando = TestItems.Mov("Commando", genres: ["Action", "Sci-Fi"]);
        var terminator = TestItems.Mov("The Terminator", genres: ["Action", "Sci-Fi"]);
        var totalRecall = TestItems.Mov("Total Recall", genres: ["Action", "Sci-Fi"]);
        var notebook = TestItems.Mov("The Notebook", genres: ["Drama", "Romance"]);

        var list = SimilarToList(
            SimilarToBlock("Predator", maxItems: 2),
            SimilarToBlock("Commando", maxItems: 1));

        var result = Filter(list, predator, commando, terminator, totalRecall, notebook);

        // Both references share one genre profile, so the two blocks admit the same four items:
        // block 0 takes 2, block 1 takes 1 of what is left - no duplicates, dissimilar item out.
        Assert.Equal(3, result.Length);
        Assert.Equal(3, result.Distinct().Count());
        Assert.DoesNotContain(notebook.Id, result);
    }

    [Fact]
    public void SimilarToBlockOrGenreBlock_ReturnsTheUnionOfBothBlocks()
    {
        var predator = TestItems.Mov("Predator", genres: ["Action", "Sci-Fi"]);
        var commando = TestItems.Mov("Commando", genres: ["Action", "Sci-Fi"]);
        var notebook = TestItems.Mov("The Notebook", genres: ["Drama", "Romance"]);
        var titanic = TestItems.Mov("Titanic", genres: ["Drama", "Romance"]);

        var list = SimilarToList(SimilarToBlock("Predator"), GenreBlock("Drama"));

        var result = Filter(list, predator, commando, notebook, titanic);

        // Pre-fix this was empty: similarity was one global AND over the whole list, so the dramas
        // were cut for being dissimilar and the Arnold movies for not being dramas.
        Assert.Equal(4, result.Length);
        Assert.Contains(predator.Id, result);
        Assert.Contains(commando.Id, result);
        Assert.Contains(notebook.Id, result);
        Assert.Contains(titanic.Id, result);
    }

    [Fact]
    public void SimilarToInAMixedBlock_StillGatesThatBlocksMatches()
    {
        var predator = TestItems.Mov("Predator", genres: ["Action", "Sci-Fi"]);
        var commando = TestItems.Mov("Commando", genres: ["Action", "Sci-Fi"]);
        var alien = TestItems.Mov("Alien", genres: ["Sci-Fi", "Horror"]);
        var notebook = TestItems.Mov("The Notebook", genres: ["Drama", "Romance"]);

        var list = SimilarToList(new ExpressionSet
        {
            Expressions =
            [
                new Expression("SimilarTo", "Equal", "Predator"),
                new Expression("Genres", "Contains", "Sci-Fi"),
            ],
        });

        var result = Filter(list, predator, commando, alien, notebook);

        // Alien passes the Genres rule and still loses: one shared genre is below the threshold.
        // Similarity refines the block it lives in, exactly as before the per-block split.
        Assert.Equal(2, result.Length);
        Assert.Contains(predator.Id, result);
        Assert.Contains(commando.Id, result);
    }

    [Fact]
    public void TwoSimilarToBlocks_EachFillsItsLimitFromItsOwnReferencePool()
    {
        var predator = TestItems.Mov("Predator", genres: ["Action", "Sci-Fi"]);
        var commando = TestItems.Mov("Commando", genres: ["Action", "Sci-Fi"]);
        var notebook = TestItems.Mov("The Notebook", genres: ["Drama", "Romance"]);
        var titanic = TestItems.Mov("Titanic", genres: ["Drama", "Romance"]);

        var list = SimilarToList(
            SimilarToBlock("Predator", maxItems: 1),
            SimilarToBlock("The Notebook", maxItems: 1));

        var result = Filter(list, predator, commando, notebook, titanic);

        // One blended pool would hand both single-slot blocks an Arnold movie, since every item
        // scores against the merged references. Per block, block 1 can only draw from the dramas.
        Assert.Equal(2, result.Length);
        Assert.Contains(result, id => id == predator.Id || id == commando.Id);
        Assert.Contains(result, id => id == notebook.Id || id == titanic.Id);
    }

    [Fact]
    public void ItemMatchingTwoSimilarToBlocks_SortsOnItsBestBlockScore()
    {
        // Block 0 resolves two references, so every score against it is double block 1's. The
        // crossover matches both blocks; keeping the LAST block's score instead of the best would
        // sink it to the drama-only item's level - and its name sorts last, so the similarity
        // tie-break would then place it behind that item instead of ahead of it.
        var predator = TestItems.Mov("Predator", genres: ["Action", "Sci-Fi"]);
        var predator2 = TestItems.Mov("Predator 2", genres: ["Action", "Sci-Fi"]);
        var notebook = TestItems.Mov("The Notebook", genres: ["Drama", "Romance"]);
        var titanic = TestItems.Mov("Titanic", genres: ["Drama", "Romance"]);
        var crossover = TestItems.Mov("Zulu Crossover", genres: ["Action", "Sci-Fi", "Drama", "Romance"]);

        var list = SimilarToList(
            new ExpressionSet { Expressions = [new Expression("SimilarTo", "Contains", "Predator")] },
            SimilarToBlock("The Notebook"));

        var result = Filter(list, predator, predator2, notebook, titanic, crossover);

        Assert.Equal(5, result.Length);
        Assert.True(
            Array.IndexOf(result, crossover.Id) < Array.IndexOf(result, titanic.Id),
            "the crossover must sort on its best block's score, not on the last block scored");
    }

    [Fact]
    public void SimilarToBlockWithNoReference_LeavesTheOtherBlocksAlone()
    {
        var predator = TestItems.Mov("Predator", genres: ["Action", "Sci-Fi"]);
        var notebook = TestItems.Mov("The Notebook", genres: ["Drama", "Romance"]);

        var list = SimilarToList(SimilarToBlock("Nonexistent Movie"), GenreBlock("Drama"));

        var result = Filter(list, predator, notebook);

        // The unresolvable block contributes nothing. Pre-fix its empty reference metadata failed
        // every item in the list, the drama block's matches included.
        Assert.Equal([notebook.Id], result);
    }
}
