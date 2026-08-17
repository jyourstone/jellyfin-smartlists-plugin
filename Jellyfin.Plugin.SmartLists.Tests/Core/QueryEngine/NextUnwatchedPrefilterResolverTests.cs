using Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Covers the pure operator/value gate of <see cref="NextUnwatchedPrefilterResolver"/> - the
/// decision whether a NextUnwatched rule may ride the unplayed-episode prefilter. Its contract:
///
/// - Only "Equal true" and the logically identical "NotEqual false" ride (both mean "IS the
///   next unwatched episode", which is provably a subset of the user's unplayed episodes).
/// - The complement ("Equal false" / "NotEqual true") matches played episodes, later unplayed
///   episodes and every non-episode - no unplayed-episode set can bound it, so it never rides.
/// - Value parsing is EXACTLY the per-item semantics (it delegates to the same
///   Engine.ValidateAndParseBooleanValue the compiled rules use): trim whitespace, strip
///   surrounding quotes, case-insensitive bool parse.
/// - Anything the compiled rule would reject (unsupported operator, empty or unparsable
///   value) stays per-item - the gate never guesses.
///
/// The user resolution and GetItemIds steps need a live Jellyfin and are exercised there, not here.
/// </summary>
public class NextUnwatchedPrefilterResolverTests
{
    // ---- Riding combinations ----

    [Theory]
    [InlineData("Equal", "true")]
    [InlineData("Equal", "True")]
    [InlineData("Equal", "TRUE")]
    [InlineData("Equal", " true ")]
    [InlineData("Equal", "\"true\"")]
    [InlineData("Equal", "'true'")]
    [InlineData("NotEqual", "false")]
    [InlineData("NotEqual", "False")]
    [InlineData("NotEqual", " \"false\" ")]
    public void EqualTrue_And_NotEqualFalse_Ride(string ruleOperator, string targetValue)
    {
        Assert.True(NextUnwatchedPrefilterResolver.RidesPrefilter(ruleOperator, targetValue));
    }

    // ---- Complement combinations never ride ----

    [Theory]
    [InlineData("Equal", "false")]
    [InlineData("Equal", "False")]
    [InlineData("Equal", "\"false\"")]
    [InlineData("NotEqual", "true")]
    [InlineData("NotEqual", "True")]
    [InlineData("NotEqual", "'true'")]
    public void ComplementCombinations_StayPerItem(string ruleOperator, string targetValue)
    {
        Assert.False(NextUnwatchedPrefilterResolver.RidesPrefilter(ruleOperator, targetValue));
    }

    // ---- Unsupported operators never ride ----

    [Theory]
    [InlineData("Contains")]
    [InlineData("IsIn")]
    [InlineData("GreaterThan")]
    [InlineData("MatchRegex")]
    [InlineData("equal")] // operator comparison is Ordinal, mirroring the engine switch
    [InlineData("")]
    [InlineData(null)]
    public void UnsupportedOperators_StayPerItem(string? ruleOperator)
    {
        Assert.False(NextUnwatchedPrefilterResolver.RidesPrefilter(ruleOperator, "true"));
    }

    // ---- Values the compiled rule would reject never ride ----

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("truthy")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UnparsableValues_StayPerItem(string? targetValue)
    {
        Assert.False(NextUnwatchedPrefilterResolver.RidesPrefilter("Equal", targetValue));
        Assert.False(NextUnwatchedPrefilterResolver.RidesPrefilter("NotEqual", targetValue));
    }
}
