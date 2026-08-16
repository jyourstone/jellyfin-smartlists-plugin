using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Covers the pure set math of <see cref="CandidateSetBuilder"/> - the shared
/// union-of-set-intersections machinery every DB-prefilter field resolver plugs into.
///
/// The safety contract under test:
/// - Within one rule set (AND): intersection over the set's pushdownable rules only;
///   a rule no resolver can bound simply does not participate.
/// - Across rule sets (OR): union; a set with ZERO pushdownable rules may match any
///   item and therefore forces the overall result to null (= no shrink possible).
/// - Negative operators (NotEqual/NotContains/IsNotIn) never reach a resolver unless
///   it opts in via SupportsNegativeOperators.
/// - MatchRegex whose pattern matches "" never rides (empty-list semantics test
///   against the empty string), nor does an invalid pattern.
/// - An empty per-rule set is a hard "nothing matches" claim and short-circuits the
///   rest of that set's rules.
///
/// Resolvers are fakes; the builder never touches ILibraryManager itself, so the
/// context carries nulls.
/// </summary>
public class CandidateSetBuilderTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();
    private static readonly Guid D = Guid.NewGuid();

    private static PrefilterContext Context() => new(null!, null!, null, null);

    private static Expression Rule(string field, string op = "Equal", string value = "x") => new(field, op, value);

    private static ExpressionSet Set(params Expression[] expressions) => new() { Expressions = [.. expressions] };

    /// <summary>
    /// Fake resolver: maps field name to the ID set it returns (a fresh copy per call,
    /// since the builder takes ownership and mutates). Unknown fields resolve to null
    /// (= cannot bound). Counts calls so tests can assert consultation behavior.
    /// </summary>
    private sealed class FakeResolver : IRulePrefilterResolver
    {
        private readonly Dictionary<string, Guid[]> _byField;

        public FakeResolver(Dictionary<string, Guid[]> byField, bool supportsNegativeOperators = false)
        {
            _byField = byField;
            SupportsNegativeOperators = supportsNegativeOperators;
        }

        public bool SupportsNegativeOperators { get; }

        public int CallCount { get; private set; }

        public HashSet<Guid>? Resolve(Expression expression, PrefilterContext context)
        {
            CallCount++;
            return _byField.TryGetValue(expression.MemberName, out var ids) ? [.. ids] : null;
        }
    }

    [Fact]
    public void Build_WithNoResolvers_ReturnsNull()
    {
        var builder = new CandidateSetBuilder([]);

        var result = builder.Build([Set(Rule("People"))], Context());

        Assert.Null(result);
    }

    [Fact]
    public void CreateDefault_HasNoResolversYet_ReturnsNull()
    {
        // Foundation stage guarantee: until field resolvers register, the prefilter is a no-op.
        var result = CandidateSetBuilder.CreateDefault().Build([Set(Rule("People"))], Context());

        Assert.Null(result);
    }

    [Fact]
    public void Build_WithNullOrEmptySets_ReturnsNull()
    {
        var resolver = new FakeResolver(new() { ["People"] = [A] });
        var builder = new CandidateSetBuilder([resolver]);

        Assert.Null(builder.Build(null, Context()));
        Assert.Null(builder.Build([], Context()));
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public void Build_SingleBoundedRule_ReturnsItsSet()
    {
        var builder = new CandidateSetBuilder([new FakeResolver(new() { ["People"] = [A, B] })]);

        var result = builder.Build([Set(Rule("People"))], Context());

        Assert.NotNull(result);
        Assert.Equal(new HashSet<Guid> { A, B }, result);
    }

    [Fact]
    public void Build_AndRulesWithinOneSet_Intersect()
    {
        var resolver = new FakeResolver(new()
        {
            ["People"] = [A, B, C],
            ["Genres"] = [B, C, D],
        });
        var builder = new CandidateSetBuilder([resolver]);

        var result = builder.Build([Set(Rule("People"), Rule("Genres"))], Context());

        Assert.NotNull(result);
        Assert.Equal(new HashSet<Guid> { B, C }, result);
    }

    [Fact]
    public void Build_UnboundedRuleInSet_DoesNotParticipateInIntersection()
    {
        // "Framerate" is unknown to the resolver (null = all items); the set is still
        // bounded by the People rule alone.
        var builder = new CandidateSetBuilder([new FakeResolver(new() { ["People"] = [A, B] })]);

        var result = builder.Build([Set(Rule("People"), Rule("Framerate"))], Context());

        Assert.NotNull(result);
        Assert.Equal(new HashSet<Guid> { A, B }, result);
    }

    [Fact]
    public void Build_SetWithZeroPushdownableRules_ForcesNullOverall()
    {
        // OR-union with an unbounded branch is unbounded, even when another set is bounded.
        var builder = new CandidateSetBuilder([new FakeResolver(new() { ["People"] = [A] })]);

        var result = builder.Build([Set(Rule("People")), Set(Rule("Framerate"))], Context());

        Assert.Null(result);
    }

    [Fact]
    public void Build_OrSets_Union()
    {
        var resolver = new FakeResolver(new()
        {
            ["People"] = [A, B],
            ["Genres"] = [C],
        });
        var builder = new CandidateSetBuilder([resolver]);

        var result = builder.Build([Set(Rule("People")), Set(Rule("Genres"))], Context());

        Assert.NotNull(result);
        Assert.Equal(new HashSet<Guid> { A, B, C }, result);
    }

    [Theory]
    [InlineData("NotEqual")]
    [InlineData("NotContains")]
    [InlineData("IsNotIn")]
    public void Build_NegativeOperator_NeverReachesResolverWithoutOptIn(string op)
    {
        var resolver = new FakeResolver(new() { ["People"] = [A] });
        var builder = new CandidateSetBuilder([resolver]);

        var result = builder.Build([Set(Rule("People", op))], Context());

        Assert.Null(result); // Rule did not participate -> set unbounded -> no shrink.
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public void Build_NegativeOperator_ParticipatesWhenResolverOptsIn()
    {
        var resolver = new FakeResolver(new() { ["SeriesName"] = [A, B] }, supportsNegativeOperators: true);
        var builder = new CandidateSetBuilder([resolver]);

        var result = builder.Build([Set(Rule("SeriesName", "NotEqual"))], Context());

        Assert.NotNull(result);
        Assert.Equal(new HashSet<Guid> { A, B }, result);
        Assert.Equal(1, resolver.CallCount);
    }

    [Theory]
    [InlineData(".*")] // matches ""
    [InlineData("a?")] // matches ""
    [InlineData("(")] // invalid pattern
    public void Build_MatchRegexMatchingEmptyStringOrInvalid_NeverRides(string pattern)
    {
        var resolver = new FakeResolver(new() { ["Name"] = [A] });
        var builder = new CandidateSetBuilder([resolver]);

        var result = builder.Build([Set(new Expression("Name", "MatchRegex", pattern))], Context());

        Assert.Null(result);
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public void Build_MatchRegexNotMatchingEmptyString_IsConsulted()
    {
        var resolver = new FakeResolver(new() { ["Name"] = [A] });
        var builder = new CandidateSetBuilder([resolver]);

        var result = builder.Build([Set(new Expression("Name", "MatchRegex", "abc"))], Context());

        Assert.NotNull(result);
        Assert.Equal(new HashSet<Guid> { A }, result);
    }

    [Fact]
    public void Build_EmptyIntersection_ShortCircuitsRemainingRulesInSet()
    {
        // People={A}, Genres={B}: intersection empty after rule 2; rule 3 must not be consulted.
        var resolver = new FakeResolver(new()
        {
            ["People"] = [A],
            ["Genres"] = [B],
            ["Studios"] = [C],
        });
        var builder = new CandidateSetBuilder([resolver]);

        var result = builder.Build([Set(Rule("People"), Rule("Genres"), Rule("Studios"))], Context());

        Assert.NotNull(result);
        Assert.Empty(result);
        Assert.Equal(2, resolver.CallCount);
    }

    [Fact]
    public void Build_EmptySetBranch_ContributesNothingToUnion()
    {
        // Set 1 intersects to empty (hard "nothing matches"); set 2 is bounded - the
        // union is exactly set 2's candidates.
        var resolver = new FakeResolver(new()
        {
            ["People"] = [A],
            ["Genres"] = [B],
            ["Studios"] = [C, D],
        });
        var builder = new CandidateSetBuilder([resolver]);

        var result = builder.Build([Set(Rule("People"), Rule("Genres")), Set(Rule("Studios"))], Context());

        Assert.NotNull(result);
        Assert.Equal(new HashSet<Guid> { C, D }, result);
    }

    [Fact]
    public void Build_AllSetsEmpty_ReturnsEmptySetNotNull()
    {
        // Empty (non-null) is a hard claim distinct from null (= no shrink possible).
        var resolver = new FakeResolver(new()
        {
            ["People"] = [A],
            ["Genres"] = [B],
        });
        var builder = new CandidateSetBuilder([resolver]);

        var result = builder.Build([Set(Rule("People"), Rule("Genres"))], Context());

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void Build_FirstResolverWithResultWins()
    {
        var first = new FakeResolver(new() { ["People"] = [A] });
        var second = new FakeResolver(new() { ["People"] = [B] });
        var builder = new CandidateSetBuilder([first, second]);

        var result = builder.Build([Set(Rule("People"))], Context());

        Assert.NotNull(result);
        Assert.Equal(new HashSet<Guid> { A }, result);
        Assert.Equal(0, second.CallCount);
    }

    [Fact]
    public void Build_ResolverThatCannotBound_FallsThroughToNextResolver()
    {
        var first = new FakeResolver(new() { ["Genres"] = [B] }); // knows nothing about People
        var second = new FakeResolver(new() { ["People"] = [A] });
        var builder = new CandidateSetBuilder([first, second]);

        var result = builder.Build([Set(Rule("People"))], Context());

        Assert.NotNull(result);
        Assert.Equal(new HashSet<Guid> { A }, result);
    }

    [Fact]
    public void Build_ThrowingResolver_DegradesToNoShrinkForThatRule()
    {
        var throwing = new ThrowingResolver();
        var builder = new CandidateSetBuilder([throwing]);

        var result = builder.Build([Set(Rule("People"))], Context());

        Assert.Null(result); // Rule stays per-item -> set unbounded -> no shrink.
        Assert.Equal(1, throwing.CallCount);
    }

    [Fact]
    public void Build_NullExpressionEntries_AreSkipped()
    {
        var resolver = new FakeResolver(new() { ["People"] = [A] });
        var builder = new CandidateSetBuilder([resolver]);
        var set = new ExpressionSet { Expressions = [null!, Rule("People")] };

        var result = builder.Build([set], Context());

        Assert.NotNull(result);
        Assert.Equal(new HashSet<Guid> { A }, result);
    }

    [Fact]
    public void Build_SetWithNullExpressionsList_ForcesNullOverall()
    {
        // A malformed set is treated conservatively as "may match anything".
        var resolver = new FakeResolver(new() { ["People"] = [A] });
        var builder = new CandidateSetBuilder([resolver]);
        var malformed = new ExpressionSet { Expressions = null };

        var result = builder.Build([Set(Rule("People")), malformed], Context());

        Assert.Null(result);
    }

    private sealed class ThrowingResolver : IRulePrefilterResolver
    {
        public int CallCount { get; private set; }

        public HashSet<Guid>? Resolve(Expression expression, PrefilterContext context)
        {
            CallCount++;
            throw new InvalidOperationException("boom");
        }
    }
}
