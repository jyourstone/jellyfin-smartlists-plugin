using System.Text.Json;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaTypeConstants = Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.Shared;

/// <summary>
/// Pins the one-time load migration in <see cref="SmartListFileSystem.ApplyPostProcessing(SmartCollectionDto)"/>
/// (and the playlist overload) that replaces the legacy per-rule IncludeCollectionOnly/
/// IncludePlaylistOnly checkboxes with the Collection/Playlist media types.
///
/// Collections: each include-only Collections/Playlists rule becomes a Name rule against the
/// container itself (operator and value preserved) and the list gains the matching container
/// media type. Pure include-only lists (every rule group carries an include-only rule) only ever
/// produced containers, so their item media types are replaced outright; mixed lists keep their
/// item types. MatchByMembers stays false, preserving the legacy match-against-container-metadata
/// behavior.
///
/// Playlists: the flags are stripped silently - the feature never worked there because Jellyfin
/// playlists can only contain media items (container results were silently dropped).
/// </summary>
public class SmartListFileSystemMigrationTests
{
    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------

    private static ExpressionSet Group(params Expression[] rules)
        => new() { Expressions = [.. rules] };

    private static Expression IncludeOnlyCollectionsRule(string op = "Contains", string value = "Marvel")
        => new("Collections", op, value) { IncludeCollectionOnly = true };

    private static Expression IncludeOnlyPlaylistsRule(string op = "Equal", string value = "Morning Mix")
        => new("Playlists", op, value) { IncludePlaylistOnly = true };

    // ---------------------------------------------------------------------------------
    // Collections - pure include-only lists
    // ---------------------------------------------------------------------------------

    [Fact]
    public void PureIncludeOnlyCollection_MigratesToCollectionMediaTypeAndNameRule()
    {
        // Legacy pure include-only lists carried an item media type (the UI default) even though
        // their results were only ever containers - the migration replaces it outright.
        var dto = new SmartCollectionDto
        {
            Name = "Legacy",
            MediaTypes = [MediaTypeConstants.Movie],
            ExpressionSets =
            [
                Group(
                    IncludeOnlyCollectionsRule(op: "Contains", value: "Marvel"),
                    new Expression("Studios", "Contains", "Disney")),
            ],
        };

        SmartListFileSystem.ApplyPostProcessing(dto);

        Assert.Equal([MediaTypeConstants.Collection], dto.MediaTypes);

        var rules = Assert.Single(dto.ExpressionSets).Expressions!;
        Assert.Equal(2, rules.Count);

        // The include-only rule becomes a Name rule against the container, operator/value intact
        Assert.Equal("Name", rules[0].MemberName);
        Assert.Equal("Contains", rules[0].Operator);
        Assert.Equal("Marvel", rules[0].TargetValue);
        Assert.Null(rules[0].IncludeCollectionOnly);
        Assert.Null(rules[0].IncludePlaylistOnly);

        // Sibling rules already evaluated against the container and carry over unchanged
        Assert.Equal("Studios", rules[1].MemberName);
        Assert.Equal("Contains", rules[1].Operator);
        Assert.Equal("Disney", rules[1].TargetValue);

        // Toggle stays off: migrated lists keep matching containers by their own metadata
        Assert.False(dto.MatchByMembers);
    }

    [Fact]
    public void LegacyCollectionJson_LoadsWithGroupIntoCollectionsOff()
    {
        // Goes through the real load path (Deserialize -> ApplyPostProcessing) rather than building
        // the DTO in C#, because the risk is on the JSON side: legacy files predate the
        // GroupIntoCollections key entirely, and anything that made it load true would silently turn
        // every existing smart collection into a collection-of-collections on first start.
        const string LegacyJson = """
            {
              "Name": "Legacy",
              "MediaTypes": ["Movie"],
              "ExpressionSets": [
                { "Expressions": [ { "MemberName": "Genres", "Operator": "Contains", "TargetValue": "Action" } ] }
              ]
            }
            """;

        var dto = JsonSerializer.Deserialize<SmartCollectionDto>(LegacyJson, SmartListFileSystem.SharedJsonOptions)!;
        SmartListFileSystem.ApplyPostProcessing(dto);

        Assert.False(dto.GroupIntoCollections);
    }

    [Fact]
    public void PureIncludeOnlyPlaylistsRule_MigratesToPlaylistMediaType()
    {
        var dto = new SmartCollectionDto
        {
            Name = "Legacy",
            MediaTypes = [MediaTypeConstants.Audio],
            ExpressionSets = [Group(IncludeOnlyPlaylistsRule(op: "Equal", value: "Morning Mix"))],
        };

        SmartListFileSystem.ApplyPostProcessing(dto);

        Assert.Equal([MediaTypeConstants.Playlist], dto.MediaTypes);

        var rule = Assert.Single(Assert.Single(dto.ExpressionSets).Expressions!);
        Assert.Equal("Name", rule.MemberName);
        Assert.Equal("Equal", rule.Operator);
        Assert.Equal("Morning Mix", rule.TargetValue);
        Assert.Null(rule.IncludePlaylistOnly);
    }

    [Fact]
    public void PureIncludeOnlyCollection_KeepsCollectionSearchDepthReachable()
    {
        // The nested-collection walk depth lived on the include-only rule. The rewrite must not
        // strand it: the depth stays on the Name rule and SmartList's depth extraction reads it
        // from there, so migrated lists keep their configured depth instead of falling back to 0
        // (which would kill the nested walk and child-aggregation sorting).
        var dto = new SmartCollectionDto
        {
            Id = "legacy-depth",
            Name = "Legacy depth",
            MediaTypes = [MediaTypeConstants.Movie],
            ExpressionSets =
            [
                Group(new Expression("Collections", "Contains", "Marvel")
                {
                    IncludeCollectionOnly = true,
                    CollectionSearchDepth = 3,
                }),
            ],
        };

        SmartListFileSystem.ApplyPostProcessing(dto);

        var rule = Assert.Single(Assert.Single(dto.ExpressionSets).Expressions!);
        Assert.Equal("Name", rule.MemberName);
        Assert.Equal(3, rule.CollectionSearchDepth);
        Assert.Equal(3, new SmartList(dto).CollectionSearchDepth);
    }

    // ---------------------------------------------------------------------------------
    // Collections - mixed legacy lists
    // ---------------------------------------------------------------------------------

    [Fact]
    public void MixedLegacyCollection_KeepsItemMediaTypesAndAppendsContainerType()
    {
        var dto = new SmartCollectionDto
        {
            Name = "Mixed",
            MediaTypes = [MediaTypeConstants.Movie, MediaTypeConstants.Series],
            ExpressionSets =
            [
                Group(IncludeOnlyCollectionsRule()),
                Group(new Expression("Genres", "Contains", "Action")),
            ],
        };

        SmartListFileSystem.ApplyPostProcessing(dto);

        Assert.Equal(
            [MediaTypeConstants.Movie, MediaTypeConstants.Series, MediaTypeConstants.Collection],
            dto.MediaTypes);

        // The include-only rule is rewritten, the normal item group is untouched
        var migratedRule = Assert.Single(dto.ExpressionSets[0].Expressions!);
        Assert.Equal("Name", migratedRule.MemberName);
        Assert.Null(migratedRule.IncludeCollectionOnly);

        var itemRule = Assert.Single(dto.ExpressionSets[1].Expressions!);
        Assert.Equal("Genres", itemRule.MemberName);
        Assert.Equal("Contains", itemRule.Operator);
        Assert.Equal("Action", itemRule.TargetValue);
    }

    [Fact]
    public void GroupWithBothIncludeOnlyFlavors_GainsBothContainerTypes()
    {
        // One group carrying BOTH flavors: the list is pure include-only (its only group has an
        // include-only rule), so the item type is dropped and BOTH container types are added.
        var dto = new SmartCollectionDto
        {
            Name = "Both Flavors",
            MediaTypes = [MediaTypeConstants.Movie],
            ExpressionSets = [Group(IncludeOnlyCollectionsRule(), IncludeOnlyPlaylistsRule())],
        };

        SmartListFileSystem.ApplyPostProcessing(dto);

        Assert.Equal([MediaTypeConstants.Collection, MediaTypeConstants.Playlist], dto.MediaTypes);

        var rules = Assert.Single(dto.ExpressionSets).Expressions!;
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal("Name", r.MemberName));
    }

    [Fact]
    public void NormalCollectionsMembershipRule_IsNotRewritten()
    {
        // Collections rules WITHOUT the include-only flag are membership rules
        // ("items belonging to collection X") and must survive migration as-is.
        var dto = new SmartCollectionDto
        {
            Name = "Membership",
            MediaTypes = [MediaTypeConstants.Movie],
            ExpressionSets = [Group(new Expression("Collections", "Contains", "Marvel"))],
        };

        SmartListFileSystem.ApplyPostProcessing(dto);

        Assert.Equal([MediaTypeConstants.Movie], dto.MediaTypes);
        var rule = Assert.Single(Assert.Single(dto.ExpressionSets).Expressions!);
        Assert.Equal("Collections", rule.MemberName);
    }

    // ---------------------------------------------------------------------------------
    // Playlists - flags are stripped, never migrated
    // ---------------------------------------------------------------------------------

    [Fact]
    public void PlaylistWithIncludeOnlyFlags_HasFlagsStrippedWithoutMigration()
    {
        var dto = new SmartPlaylistDto
        {
            Name = "Legacy Playlist",
            MediaTypes = [MediaTypeConstants.Movie],
            ExpressionSets = [Group(IncludeOnlyCollectionsRule(), IncludeOnlyPlaylistsRule())],
        };

        SmartListFileSystem.ApplyPostProcessing(dto);

        // No media-type migration and no rule rewrite - the flags just disappear
        Assert.Equal([MediaTypeConstants.Movie], dto.MediaTypes);

        var rules = Assert.Single(dto.ExpressionSets).Expressions!;
        Assert.Equal("Collections", rules[0].MemberName);
        Assert.Equal("Playlists", rules[1].MemberName);
        Assert.All(rules, r =>
        {
            Assert.Null(r.IncludeCollectionOnly);
            Assert.Null(r.IncludePlaylistOnly);
        });
    }

    [Fact]
    public void PlaylistIncludeOnlyGroup_IsRemovedWhenOtherGroupsRemain()
    {
        // The legacy engine skipped include-only groups on playlists entirely (container results
        // were dropped at write), so the behavior-preserving migration removes the dead group
        // rather than letting its rules start filtering items.
        var normalGroup = Group(new Expression("Genres", "Contains", "Action"));
        var dto = new SmartPlaylistDto
        {
            Name = "Legacy Mixed Playlist",
            MediaTypes = [MediaTypeConstants.Movie],
            ExpressionSets = [Group(IncludeOnlyCollectionsRule()), normalGroup],
        };

        SmartListFileSystem.ApplyPostProcessing(dto);

        var survivor = Assert.Single(dto.ExpressionSets);
        Assert.Same(normalGroup, survivor);
        Assert.Equal("Genres", Assert.Single(survivor.Expressions!).MemberName);
    }

    [Fact]
    public void PlaylistWithContainerMediaTypes_HasThemDropped()
    {
        // The DTO setter accepts container types (they are valid for collections), so stored
        // playlist JSON carrying them is sanitized at load instead.
        var dto = new SmartPlaylistDto
        {
            Name = "Hand-edited",
            MediaTypes = [MediaTypeConstants.Movie, MediaTypeConstants.Collection, MediaTypeConstants.Playlist],
            ExpressionSets = [Group(new Expression("Genres", "Contains", "Action"))],
        };

        SmartListFileSystem.ApplyPostProcessing(dto);

        Assert.Equal([MediaTypeConstants.Movie], dto.MediaTypes);
    }

    [Fact]
    public void PlaylistWithGroupIntoCollections_HasTheFlagCleared()
    {
        // Same shape as the container-media-type sanitizer above: the flag lives on the shared base
        // DTO, so hand-edited (or converted) playlist JSON can carry it. Left set, the list would
        // fail validation on every save; cleared at load, it just saves as a normal playlist.
        var dto = new SmartPlaylistDto
        {
            Name = "Hand-edited",
            MediaTypes = [MediaTypeConstants.Movie],
            GroupIntoCollections = true,
            ExpressionSets = [Group(new Expression("Genres", "Contains", "Action"))],
        };

        SmartListFileSystem.ApplyPostProcessing(dto);

        Assert.False(dto.GroupIntoCollections);
    }

    [Fact]
    public void CollectionWithMaxPlayTime_HasTheLimitCleared()
    {
        // Max Playtime is playlist-only and its input is hidden for collections - but the config
        // page seeded the server-wide Default Max Playtime into every new list regardless of type
        // and sent it, so collections created by an admin with a non-zero default are silently
        // truncated by a limit their form never showed. Clearing it at load repairs those lists.
        var dto = new SmartCollectionDto
        {
            Name = "Truncated By A Hidden Limit",
            MediaTypes = [MediaTypeConstants.Movie],
            MaxPlayTimeMinutes = 120,
            ExpressionSets = [Group(new Expression("Genres", "Contains", "Action"))],
        };

        SmartListFileSystem.ApplyPostProcessing(dto);

        Assert.Null(dto.MaxPlayTimeMinutes);
    }

    [Fact]
    public void ConvertingAPlaylistToACollection_DropsMaxPlayTime()
    {
        // The other half of the same leak: Convert to Collection would otherwise carry the
        // playlist's Max Playtime onto a list whose form has no way to see or clear it.
        var playlist = new SmartPlaylistDto
        {
            Name = "Two Hours Of Action",
            MediaTypes = [MediaTypeConstants.Movie],
            MaxPlayTimeMinutes = 120,
            ExpressionSets = [Group(new Expression("Genres", "Contains", "Action"))],
        };

        var collection = DtoMapper.ToCollectionDto(playlist);

        Assert.Null(collection.MaxPlayTimeMinutes);
    }

    // ---------------------------------------------------------------------------------
    // Idempotency and already-migrated lists
    // ---------------------------------------------------------------------------------

    [Fact]
    public void CollectionMigration_IsIdempotent()
    {
        var dto = new SmartCollectionDto
        {
            Name = "Mixed",
            MediaTypes = [MediaTypeConstants.Movie],
            ExpressionSets =
            [
                Group(IncludeOnlyCollectionsRule()),
                Group(IncludeOnlyPlaylistsRule()),
                Group(new Expression("Genres", "Contains", "Action")),
            ],
        };

        SmartListFileSystem.ApplyPostProcessing(dto);
        var afterFirstRun = JsonSerializer.Serialize(dto);

        SmartListFileSystem.ApplyPostProcessing(dto);
        var afterSecondRun = JsonSerializer.Serialize(dto);

        Assert.Equal(afterFirstRun, afterSecondRun);
    }

    [Fact]
    public void AlreadyMigratedCollection_IsLeftUntouched()
    {
        var dto = new SmartCollectionDto
        {
            Name = "New Format",
            MediaTypes = [MediaTypeConstants.Movie, MediaTypeConstants.Collection],
            MatchByMembers = true,
            ExpressionSets = [Group(new Expression("Name", "Contains", "Marvel"))],
        };
        var before = JsonSerializer.Serialize(dto);

        SmartListFileSystem.ApplyPostProcessing(dto);

        Assert.Equal(before, JsonSerializer.Serialize(dto));
    }
}
