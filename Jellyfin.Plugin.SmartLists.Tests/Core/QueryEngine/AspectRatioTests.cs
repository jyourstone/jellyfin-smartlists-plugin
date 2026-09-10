using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

public class AspectRatioTests
{
    private static Func<Operand, bool> Compile(string op, string target)
        => Engine.CompileRule<Operand>(new Expression("AspectRatio", op, target), string.Empty);

    private static Operand Item(string aspectRatio) => new("item") { AspectRatio = aspectRatio };

    [Theory]
    [InlineData("16:9", true)]
    [InlineData("2.35:1", true)]
    [InlineData("80:29", true)]
    [InlineData(" 9 : 16 ", true)]
    [InlineData("", false)]
    [InlineData("16", false)]
    [InlineData("16:0", false)]
    [InlineData("-16:9", false)]
    public void IsValid_RequiresTwoPositiveComponents(string value, bool expected)
    {
        Assert.Equal(expected, AspectRatioTypes.IsValid(value));
    }

    [Theory]
    [InlineData("100000:1", true)]
    [InlineData("1:100000", true)]
    [InlineData("0.0001:1", true)]
    [InlineData("100001:1", false)]
    [InlineData("1:100001", false)]
    [InlineData("0.00009:1", false)]
    [InlineData("99999999999999999999:1", false)]
    public void IsValid_RejectsComponentsOutsideSupportedRange(string value, bool expected)
    {
        Assert.Equal(expected, AspectRatioTypes.IsValid(value));
    }

    [Fact]
    public void CompileRule_ExtremeTargetIsRejectedInsteadOfOverflowing()
    {
        Assert.Throws<ArgumentException>(() => Compile("GreaterThan", "99999999999999999999:1"));
    }

    [Fact]
    public void CompileRule_ExtremeItemRatioNeverMatches()
    {
        var isWide = Compile("GreaterThan", "1:1");
        Assert.False(isWide(Item("99999999999999999999:0.0000000001")));
    }

    [Fact]
    public void Evaluate_NearOverflowComponentsStayOrdered()
    {
        // Both cross-products land at 1e10, the arithmetic worst case the bounds allow.
        Assert.True(AspectRatioTypes.Evaluate("100000:0.0001", "99999:0.0001", "GreaterThan"));
        Assert.True(AspectRatioTypes.Evaluate("0.0001:100000", "0.0001:99999", "LessThan"));
    }

    [Theory]
    [InlineData("Equal", "160:58", "80:29", true)]
    [InlineData("NotEqual", "16:9", "4:3", true)]
    [InlineData("LessThan", "9:16", "1:1", true)]
    [InlineData("GreaterThan", "80:29", "2.40:1", true)]
    [InlineData("GreaterThanOrEqual", "16:9", "32:18", true)]
    [InlineData("LessThanOrEqual", "1:1", "1:1", true)]
    public void CompileRule_RatioComparisonsUseNumericProportions(string op, string actual, string target, bool expected)
    {
        Assert.Equal(expected, Compile(op, target)(Item(actual)));
    }

    [Theory]
    [InlineData("IsIn", "16:9", "4:3;32:18", true)]
    [InlineData("IsIn", "2.35:1", "2.3:1;16:9", false)]
    [InlineData("IsNotIn", "80:29", "16:9;4:3", true)]
    [InlineData("IsNotIn", "80:29", "160:58;4:3", false)]
    public void CompileRule_MembershipUsesExactNumericRatios(string op, string actual, string targets, bool expected)
    {
        Assert.Equal(expected, Compile(op, targets)(Item(actual)));
    }

    [Theory]
    [InlineData("NotEqual", "16:9")]
    [InlineData("IsNotIn", "16:9;4:3")]
    [InlineData("LessThan", "1:1")]
    [InlineData("MatchRegex", "^$")]
    public void CompileRule_MissingRatioNeverMatches(string op, string target)
    {
        Assert.False(Compile(op, target)(Item(string.Empty)));
    }

    [Fact]
    public void CompileRule_RegexTargetsJellyfinRawValue()
    {
        Assert.True(Compile("MatchRegex", "^[0-9]+:29$")(Item("80:29")));
        Assert.False(Compile("MatchRegex", "^2\\.76:1$")(Item("80:29")));
    }

    [Theory]
    [InlineData("Contains")]
    [InlineData("NotContains")]
    public void CompileRule_SubstringOperatorsAreRejected(string op)
    {
        Assert.Throws<ArgumentException>(() => Compile(op, "2.3"));
    }

    [Theory]
    [InlineData("Equal", "not-a-ratio")]
    [InlineData("IsIn", "16:9;invalid")]
    [InlineData("IsNotIn", "")]
    public void CompileRule_InvalidTargetsAreRejected(string op, string target)
    {
        Assert.Throws<ArgumentException>(() => Compile(op, target));
    }
}
