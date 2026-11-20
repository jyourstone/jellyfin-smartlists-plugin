using System.Collections.Concurrent;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Tests.Support;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Expression = Jellyfin.Plugin.SmartLists.Core.QueryEngine.Expression;
using MediaTypeConstants = Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes;

namespace Jellyfin.Plugin.SmartLists.Tests.Core;

/// <summary>
/// Issue #580: "only check parent genres" on music also matches the album artist's genres, because
/// the artist folder is an ancestor like any other. <c>OnlyAlbumGenres</c> narrows a Genres rule to
/// the album alone - not the track, the artist folder or the library.
/// </summary>
public class AlbumOnlyGenresTests
{
    private sealed record Library(BaseItem OnElectronicAlbum, BaseItem OnPopAlbum, BaseItem Loose)
    {
        public BaseItem[] Pool => [OnElectronicAlbum, OnPopAlbum, Loose];
    }

    /// <summary>
    /// One artist tagged Electronic with an Electronic album, a Pop album and a loose track that sits
    /// directly in the artist folder. The Pop album's track and the loose track carry Electronic as
    /// their OWN genre too, so only the album level can tell the three apart.
    /// </summary>
    private static Library BuildLibrary()
    {
        var top = TestItems.PhysicalFolder("music");
        var artist = new MusicArtist { Id = Guid.NewGuid(), Name = "Artist" };
        artist.Genres = ["Electronic"];
        TestItems.Under(artist, top);

        var electronicAlbum = TestItems.Under(TestItems.Album("Electronic Album"), artist);
        electronicAlbum.Genres = ["Electronic"];
        var popAlbum = TestItems.Under(TestItems.Album("Pop Album"), artist);
        popAlbum.Genres = ["Pop"];

        var onElectronic = TestItems.Under(TestItems.Track("Electronic Album", 1, 1, name: "E1"), electronicAlbum);
        var onPop = TestItems.Under(TestItems.Track("Pop Album", 1, 1, name: "P1"), popAlbum);
        onPop.Genres = ["Electronic"];
        var loose = TestItems.Under(TestItems.Track("None", 1, 1, name: "L1"), artist);
        loose.Genres = ["Electronic"];

        return new Library(onElectronic, onPop, loose);
    }

    private static string[] FilterNames(Library library, Expression rule)
    {
        var dto = new SmartPlaylistDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Album only genres test",
            MediaTypes = [MediaTypeConstants.Audio],
            ExpressionSets = [new ExpressionSet { Expressions = [rule] }],
        };

        var byId = library.Pool.ToDictionary(i => i.Id);
        var ids = new SmartList(dto).FilterPlaylistItems(
            library.Pool, BaseItem.LibraryManager, TestItems.User, new RefreshQueueService.RefreshCache());

        return [.. ids.Select(id => byId[id].Name).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void Resolve_AlbumGenresHoldOnlyTheAlbumsShareOfTheWalk()
    {
        var library = BuildLibrary();

        static AncestorValues Resolve(BaseItem item) => AncestorValueResolver.Resolve(
            item, BaseItem.LibraryManager, new ConcurrentDictionary<Guid, AncestorValues>(), null);

        var values = Resolve(library.OnPopAlbum);

        Assert.Equal(["Pop"], values.AlbumGenres);
        Assert.Contains("Pop", values.Genres);
        Assert.Contains("Electronic", values.Genres);

        var looseValues = Resolve(library.Loose);

        Assert.Empty(looseValues.AlbumGenres);
        Assert.Equal(["Electronic"], looseValues.Genres);
    }

    [Fact]
    public void FilterPlaylistItems_OnlyAlbumGenres_IgnoresTrackAndArtistGenres()
    {
        var names = FilterNames(BuildLibrary(), new Expression("Genres", "Equal", "Electronic") { OnlyAlbumGenres = true });

        Assert.Equal(["E1"], names);
    }

    /// <summary>The behaviour #580 reported, pinned as the contrast: the artist folder is a parent.</summary>
    [Fact]
    public void FilterPlaylistItems_OnlyParentGenres_StillMatchesThroughTheArtistFolder()
    {
        var names = FilterNames(
            BuildLibrary(),
            new Expression("Genres", "Equal", "Electronic") { OnlyParentGenres = true, IncludeParentGenres = true });

        Assert.Equal(["E1", "L1", "P1"], names);
    }

    /// <summary>
    /// A track outside any album has no album genres, so a negative rule has nothing to reject it on -
    /// the same way an item with no genres passes a plain "Genres not equals" rule.
    /// </summary>
    [Fact]
    public void FilterPlaylistItems_OnlyAlbumGenres_NegativeOperatorKeepsTracksWithoutAnAlbum()
    {
        var names = FilterNames(BuildLibrary(), new Expression("Genres", "NotEqual", "Pop") { OnlyAlbumGenres = true });

        Assert.Equal(["E1", "L1"], names);
    }

    /// <summary>
    /// The flag stands alone and wins over the other Genres flags, whatever a hand-edited list carries.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompileRule_OnlyAlbumGenres_ReadsOnlyTheAlbumGenres(bool includeParent, bool onlyParent)
    {
        var expression = new Expression("Genres", "Equal", "Electronic") { OnlyAlbumGenres = true };
        if (includeParent) { expression.IncludeParentGenres = true; }
        if (onlyParent) { expression.OnlyParentGenres = true; }

        var rule = Engine.CompileRule<Operand>(expression, string.Empty);

        Assert.True(rule(new Operand("item") { ParentAlbumGenres = ["Electronic"] }));
        Assert.False(rule(new Operand("item")
        {
            Genres = ["Electronic"],
            ParentGenres = ["Electronic", "Pop"],
            ParentAlbumGenres = ["Pop"],
        }));
    }

    /// <summary>
    /// The two gates a parent-aware rule has to pass, or it is evaluated in Phase 1 against an
    /// operand whose album genres were never filled and silently matches nothing: the expensive
    /// classification and the extraction group that makes Factory run the walk. (The rule-set hash
    /// is pinned next to its siblings in AncestorWalkTests.)
    /// </summary>
    [Fact]
    public void OnlyAlbumGenres_PromotesTheRuleAndRequestsTheWalk()
    {
        var plain = new Expression("Genres", "Equal", "Jazz");
        var albumOnly = new Expression("Genres", "Equal", "Jazz") { OnlyAlbumGenres = true };

        Assert.False(SmartList.IsParentAwareListExpression(plain));
        Assert.True(SmartList.IsParentAwareListExpression(albumOnly));

        static bool WantsWalk(Expression expression)
            => FieldRequirements.Analyze([new ExpressionSet { Expressions = [expression] }])
                .RequiredGroups.HasFlag(ExtractionGroup.ParentGenres);

        Assert.False(WantsWalk(plain));
        Assert.True(WantsWalk(albumOnly));

        // The flag belongs to Genres; on another field it means nothing.
        Assert.False(WantsWalk(new Expression("Tags", "Equal", "Jazz") { OnlyAlbumGenres = true }));
        Assert.False(SmartList.IsParentAwareListExpression(new Expression("Tags", "Equal", "Jazz") { OnlyAlbumGenres = true }));
    }
}
