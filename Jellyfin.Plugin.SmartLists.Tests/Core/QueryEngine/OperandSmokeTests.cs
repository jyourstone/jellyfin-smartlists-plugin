using Jellyfin.Plugin.SmartLists.Core.QueryEngine;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Harness smoke test. Proves the test project can reference and construct plugin
/// types. Real behavioural coverage lives in the sibling test classes.
/// </summary>
public class OperandSmokeTests
{
    [Fact]
    public void Constructor_SetsName()
    {
        var operand = new Operand("The Matrix");

        Assert.Equal("The Matrix", operand.Name);
    }

    [Fact]
    public void Genres_RoundTrips()
    {
        var operand = new Operand("The Matrix") { Genres = ["Action", "Sci-Fi"] };

        Assert.Equal(["Action", "Sci-Fi"], operand.Genres);
    }

    /// <summary>
    /// Guards the InternalsVisibleTo wiring in the plugin .csproj. If this stops
    /// compiling, Engine's internal statics are no longer reachable from tests and a
    /// large chunk of the intended coverage becomes untestable - fix the csproj, do not
    /// delete this test. AnyItemContains is a case-insensitive substring match.
    /// </summary>
    [Fact]
    public void EngineInternalStatics_AreVisibleToTests()
    {
        Assert.True(Engine.AnyItemContains(["Action", "Sci-Fi"], "sci"));
        Assert.False(Engine.AnyItemContains(["Action", "Sci-Fi"], "drama"));
    }
}
