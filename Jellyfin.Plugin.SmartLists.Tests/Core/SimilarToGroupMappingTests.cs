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
/// A rule block holding nothing but SimilarTo rules produces no compiled rules, so the engine
/// takes a special "matches = true, let similarity decide" path. That path used to leave the item
/// out of _itemGroupMappings entirely - and ApplyPerGroupLimits rebuilds its result purely from
/// those mappings, so an untagged item belongs to no block and is silently dropped. Net effect:
/// "Similar To X" + "Max Items for this block" returned an EMPTY list.
///
/// Similarity is scored once against the blended reference metadata of every SimilarTo rule in the
/// list, so a passing item belongs to every similarity-only block; ApplyPerGroupLimits' consumed-item
/// tracking then hands each block a different slice of that shared pool.
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

        // One blended pool, two blocks: block 0 takes 2, block 1 takes 1 of what is left - no
        // duplicates, and the dissimilar item stays out.
        Assert.Equal(3, result.Length);
        Assert.Equal(3, result.Distinct().Count());
        Assert.DoesNotContain(notebook.Id, result);
    }
}
