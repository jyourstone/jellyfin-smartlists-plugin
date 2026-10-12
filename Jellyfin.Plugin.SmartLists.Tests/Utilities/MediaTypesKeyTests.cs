using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Utilities;

namespace Jellyfin.Plugin.SmartLists.Tests.Utilities;

/// <summary>
/// Pins the per-user pool cache key: lists may share a cached pool only when the pool query
/// would be identical. A LibraryName rule changes it (virtual items included, no library scope).
/// </summary>
public class MediaTypesKeyTests
{
    private static SmartPlaylistDto List(params Expression[] rules) => new()
    {
        Name = "Test",
        ExpressionSets = [new ExpressionSet { Expressions = [.. rules] }],
    };

    [Fact]
    public void LibraryNameRule_GetsItsOwnPool()
    {
        var plain = MediaTypesKey.Create(["Movie"], List(new Expression("Name", "Contains", "a")));
        var libraryName = MediaTypesKey.Create(["Movie"], List(new Expression("LibraryName", "Equal", "Movies")));

        Assert.NotEqual(plain, libraryName);
    }

    [Fact]
    public void ListsWithTheSamePoolQuery_ShareTheKey()
    {
        var a = MediaTypesKey.Create(["Movie", "Episode"], List(new Expression("LibraryName", "Equal", "Movies")));
        var b = MediaTypesKey.Create(["Episode", "Movie"], List(new Expression("libraryname", "NotEqual", "Kids")));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
