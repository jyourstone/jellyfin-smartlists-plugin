using Jellyfin.Plugin.SmartLists.Core.QueryEngine;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// End-to-end coverage of <see cref="Engine.CompileRule{T}"/> across the operator x field-type
/// matrix. Every expectation here was read out of Engine.cs / FieldRegistry.cs, not assumed:
/// this file is the record of what the rule engine actually does, including the parts that are
/// surprising (regex is the one case-SENSITIVE operator, "is in" is a substring match, a null
/// framerate fails even "not equals").
/// </summary>
public class EngineOperatorTests
{
    private const string UserIdDashed = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

    private static string UserIdN => Guid.Parse(UserIdDashed).ToString("N");

    private static Func<Operand, bool> Compile(string field, string op, string target)
        => Engine.CompileRule<Operand>(new Expression(field, op, target), string.Empty);

    private static Func<Operand, bool> Compile(Expression rule, string defaultUserId = "")
        => Engine.CompileRule<Operand>(rule, defaultUserId);

    private static double Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds();

    private static Operand WithTags(params string[] tags) => new("item") { Tags = [.. tags] };

    // ---------------------------------------------------------------------------------------
    // String (Text) fields - Operand.Name, exercised through BuildStringExpression
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// String Equal is whole-value equality under OrdinalIgnoreCase - NOT a substring match.
    /// </summary>
    [Theory]
    [InlineData("The Matrix", "The Matrix", true)]
    [InlineData("The Matrix", "the matrix", true)]
    [InlineData("The Matrix", "THE MATRIX", true)]
    [InlineData("The Matrix", "Matrix", false)]
    [InlineData("The Matrix", "", false)]
    [InlineData("", "", true)]
    public void CompileRule_StringEqual_IsCaseInsensitiveWholeValueMatch(string name, string target, bool expected)
    {
        Assert.Equal(expected, Compile("Name", "Equal", target)(new Operand(name)));
    }

    /// <summary>
    /// NotEqual is the exact logical negation of Equal, including for the empty-string default.
    /// </summary>
    [Theory]
    [InlineData("The Matrix", "the matrix", false)]
    [InlineData("The Matrix", "Matrix", true)]
    [InlineData("", "Matrix", true)]
    [InlineData("", "", false)]
    public void CompileRule_StringNotEqual_IsNegationOfEqual(string name, string target, bool expected)
    {
        Assert.Equal(expected, Compile("Name", "NotEqual", target)(new Operand(name)));
    }

    /// <summary>
    /// String Contains is a case-insensitive substring test. An empty target is contained in
    /// every string (including the empty default), which makes Contains "" a match-all rule.
    /// </summary>
    [Theory]
    [InlineData("The Matrix", "matr", true)]
    [InlineData("The Matrix", "MATRIX", true)]
    [InlineData("The Matrix", "Reloaded", false)]
    [InlineData("The Matrix", "", true)]
    [InlineData("", "", true)]
    [InlineData("", "Matrix", false)]
    public void CompileRule_StringContains_IsCaseInsensitiveSubstring(string name, string target, bool expected)
    {
        Assert.Equal(expected, Compile("Name", "Contains", target)(new Operand(name)));
    }

    [Theory]
    [InlineData("The Matrix", "matr", false)]
    [InlineData("The Matrix", "Reloaded", true)]
    [InlineData("", "Matrix", true)]
    public void CompileRule_StringNotContains_IsNegationOfContains(string name, string target, bool expected)
    {
        Assert.Equal(expected, Compile("Name", "NotContains", target)(new Operand(name)));
    }

    /// <summary>
    /// "is in" on a string field is NOT equality against a list - Engine.StringIsInList asks
    /// whether the field value *contains* any semicolon-separated entry. Entries are trimmed and
    /// empty entries dropped; an empty target list matches nothing.
    /// </summary>
    [Theory]
    [InlineData("The Matrix", "Matrix;Inception", true)]
    [InlineData("The Matrix", "matr", true)]
    [InlineData("The Matrix", "  Inception ; the matrix  ", true)]
    [InlineData("The Matrix", "Inception;Avatar", false)]
    [InlineData("The Matrix", "", false)]
    [InlineData("The Matrix", ";;", false)]
    [InlineData("", "Matrix", false)]
    public void CompileRule_StringIsIn_IsSubstringMatchAgainstSemicolonList(string name, string target, bool expected)
    {
        Assert.Equal(expected, Compile("Name", "IsIn", target)(new Operand(name)));
    }

    /// <summary>
    /// IsNotIn is Not(StringIsInList). Because StringIsInList short-circuits to false on an
    /// empty field value or an empty target list, IsNotIn is TRUE in both of those cases.
    /// </summary>
    [Theory]
    [InlineData("The Matrix", "Matrix;Inception", false)]
    [InlineData("The Matrix", "Avatar", true)]
    [InlineData("", "Matrix", true)]
    [InlineData("The Matrix", "", true)]
    public void CompileRule_StringIsNotIn_IsTrueWhenEitherSideIsEmpty(string name, string target, bool expected)
    {
        Assert.Equal(expected, Compile("Name", "IsNotIn", target)(new Operand(name)));
    }

    /// <summary>
    /// MatchRegex is the ONLY case-sensitive operator in the engine - GetOrCreateRegex compiles
    /// with RegexOptions.None, so no IgnoreCase. Every other string operator is OrdinalIgnoreCase.
    /// </summary>
    [Theory]
    [InlineData("The Matrix", "^The", true)]
    [InlineData("The Matrix", "^the", false)]
    [InlineData("The Matrix", "Matr.x$", true)]
    [InlineData("The Matrix", "^Matrix", false)]
    public void CompileRule_StringMatchRegex_IsCaseSensitive(string name, string pattern, bool expected)
    {
        Assert.Equal(expected, Compile("Name", "MatchRegex", pattern)(new Operand(name)));
    }

    [Fact]
    public void CompileRule_StringMatchRegex_InvalidPatternThrowsAtCompileTime()
    {
        Assert.Throws<ArgumentException>(() => Compile("Name", "MatchRegex", "[unclosed"));
    }

    /// <summary>
    /// String fields enforce the per-field operator whitelist from FieldRegistry: ItemType is a
    /// Simple field (Equal/NotEqual only), so Contains is rejected at compile time even though
    /// the underlying CLR type is string and Contains would otherwise be buildable.
    /// </summary>
    [Theory]
    [InlineData("ItemType", "Contains")]
    [InlineData("ItemType", "MatchRegex")]
    [InlineData("Name", "GreaterThan")]
    [InlineData("Name", "After")]
    public void CompileRule_StringField_RejectsOperatorOutsideFieldWhitelist(string field, string op)
    {
        Assert.Throws<ArgumentException>(() => Compile(field, op, "anything"));
    }

    // ---------------------------------------------------------------------------------------
    // List fields - Operand.Tags, exercised through BuildStringEnumerableExpression
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Equal on a list field means membership: does ANY element equal the target (case-insensitive,
    /// whole element). The empty-list default never matches.
    /// </summary>
    [Theory]
    [InlineData(new[] { "Action", "Sci-Fi" }, "action", true)]
    [InlineData(new[] { "Action", "Sci-Fi" }, "Sci-Fi", true)]
    [InlineData(new[] { "Action" }, "Act", false)]
    [InlineData(new string[0], "Action", false)]
    [InlineData(new string[0], "", false)]
    public void CompileRule_ListEqual_MatchesAnyElementWholeValue(string[] tags, string target, bool expected)
    {
        Assert.Equal(expected, Compile("Tags", "Equal", target)(WithTags(tags)));
    }

    /// <summary>
    /// NotEqual on a list is Not(any element equals), so an item with no tags at all PASSES a
    /// "Tags not equals X" rule. This is the behaviour that makes negative list rules include
    /// untagged items.
    /// </summary>
    [Theory]
    [InlineData(new[] { "Action" }, "action", false)]
    [InlineData(new[] { "Action", "Drama" }, "Drama", false)]
    [InlineData(new[] { "Action" }, "Drama", true)]
    [InlineData(new string[0], "Action", true)]
    public void CompileRule_ListNotEqual_IsTrueForEmptyList(string[] tags, string target, bool expected)
    {
        Assert.Equal(expected, Compile("Tags", "NotEqual", target)(WithTags(tags)));
    }

    /// <summary>
    /// Contains on a list is a case-insensitive SUBSTRING test against each element, not
    /// membership. An empty target matches any non-empty list but not an empty one.
    /// </summary>
    [Theory]
    [InlineData(new[] { "Action", "Sci-Fi" }, "sci", true)]
    [InlineData(new[] { "Action" }, "ion", true)]
    [InlineData(new[] { "Action" }, "drama", false)]
    [InlineData(new[] { "Action" }, "", true)]
    [InlineData(new string[0], "", false)]
    [InlineData(new string[0], "action", false)]
    public void CompileRule_ListContains_MatchesSubstringOfAnyElement(string[] tags, string target, bool expected)
    {
        Assert.Equal(expected, Compile("Tags", "Contains", target)(WithTags(tags)));
    }

    [Theory]
    [InlineData(new[] { "Action" }, "ion", false)]
    [InlineData(new[] { "Action" }, "drama", true)]
    [InlineData(new string[0], "action", true)]
    public void CompileRule_ListNotContains_IsTrueForEmptyList(string[] tags, string target, bool expected)
    {
        Assert.Equal(expected, Compile("Tags", "NotContains", target)(WithTags(tags)));
    }

    /// <summary>
    /// IsIn on a list is a substring match of any element against any semicolon-separated entry.
    /// </summary>
    [Theory]
    [InlineData(new[] { "Action", "Sci-Fi" }, "Drama;sci", true)]
    [InlineData(new[] { "Action" }, "act", true)]
    [InlineData(new[] { "Action" }, "Drama;Comedy", false)]
    [InlineData(new[] { "Action" }, "", false)]
    [InlineData(new string[0], "Action", false)]
    public void CompileRule_ListIsIn_MatchesAnyElementAgainstSemicolonList(string[] tags, string target, bool expected)
    {
        Assert.Equal(expected, Compile("Tags", "IsIn", target)(WithTags(tags)));
    }

    [Theory]
    [InlineData(new[] { "Action" }, "Drama;action", false)]
    [InlineData(new[] { "Action" }, "Drama", true)]
    [InlineData(new string[0], "Action", true)]
    public void CompileRule_ListIsNotIn_IsTrueForEmptyList(string[] tags, string target, bool expected)
    {
        Assert.Equal(expected, Compile("Tags", "IsNotIn", target)(WithTags(tags)));
    }

    /// <summary>
    /// List regex is case-sensitive like the string one, plus one documented special case:
    /// Engine.AnyRegexMatch tests an EMPTY list against the empty string, so "^$" is the
    /// idiomatic way to select items that have no tags/genres at all.
    /// </summary>
    [Theory]
    [InlineData(new[] { "Action" }, "^Act", true)]
    [InlineData(new[] { "Action" }, "^act", false)]
    [InlineData(new[] { "Action", "Sci-Fi" }, "Fi$", true)]
    [InlineData(new string[0], "^$", true)]
    [InlineData(new string[0], "^Act", false)]
    public void CompileRule_ListMatchRegex_IsCaseSensitiveAndEmptyListTestsEmptyString(string[] tags, string pattern, bool expected)
    {
        Assert.Equal(expected, Compile("Tags", "MatchRegex", pattern)(WithTags(tags)));
    }

    // ---------------------------------------------------------------------------------------
    // Any* vs Only* helpers (internal statics)
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { "Action", "Drama" }, "Action", true)]
    [InlineData(new[] { "Action", "Drama" }, "drama", true)]
    [InlineData(new[] { "Action" }, "Act", false)]
    [InlineData(new string[0], "Action", false)]
    public void AnyItemEquals_MatchesWhenAnyElementEquals(string[] list, string value, bool expected)
    {
        Assert.Equal(expected, Engine.AnyItemEquals(list, value));
    }

    /// <summary>
    /// Only* is stricter than "every element matches": OnlyEqualsWithOptionalStripping takes the
    /// first two non-empty entries and requires the count to be exactly 1. So a list of DUPLICATES
    /// (["Action","Action"]) is false even though every element matches the target. Null/empty
    /// entries are filtered out before the count, so ["", "Action"] is true.
    /// </summary>
    [Theory]
    [InlineData(new[] { "Action" }, "Action", true)]
    [InlineData(new[] { "action" }, "Action", true)]
    [InlineData(new[] { "", "Action" }, "Action", true)]
    [InlineData(new[] { "Action", "Drama" }, "Action", false)]
    [InlineData(new[] { "Action", "Action" }, "Action", false)]
    [InlineData(new[] { "Action" }, "Drama", false)]
    [InlineData(new string[0], "Action", false)]
    public void OnlyItemEquals_RequiresExactlyOneNonEmptyElement(string[] list, string value, bool expected)
    {
        Assert.Equal(expected, Engine.OnlyItemEquals(list, value));
    }

    [Fact]
    public void AnyItemHelpers_NullList_ReturnFalseRatherThanThrow()
    {
        Assert.False(Engine.AnyItemEquals(null!, "Action"));
        Assert.False(Engine.AnyItemContains(null!, "Action"));
        Assert.False(Engine.OnlyItemEquals(null!, "Action"));
        Assert.False(Engine.AnyRegexMatch(null!, "^Act"));
        Assert.False(Engine.AnyItemIsInList(null!, "Action"));
    }

    /// <summary>
    /// Collections/Playlists get prefix/suffix stripping that plain list fields do not. With no
    /// Plugin.Instance loaded, NameFormatter falls back to prefix "" and suffix "[Smart]", so a
    /// collection stored as "Marvel [Smart]" matches a rule that says just "Marvel" - while the
    /// generic AnyItemEquals on the same input does not.
    /// </summary>
    [Fact]
    public void AnyCollectionEquals_StripsConfiguredSuffixWhereAnyItemEqualsDoesNot()
    {
        string[] collections = ["Marvel [Smart]"];

        Assert.True(Engine.AnyCollectionEquals(collections, "Marvel"));
        Assert.True(Engine.AnyPlaylistEquals(collections, "Marvel"));
        Assert.False(Engine.AnyItemEquals(collections, "Marvel"));

        // The un-stripped name still matches, and an unrelated name still does not.
        Assert.True(Engine.AnyCollectionEquals(collections, "Marvel [Smart]"));
        Assert.False(Engine.AnyCollectionEquals(collections, "DC"));
    }

    [Fact]
    public void OnlyCollectionEquals_StripsSuffixButStillRequiresASingleEntry()
    {
        Assert.True(Engine.OnlyCollectionEquals(["Marvel [Smart]"], "Marvel"));
        Assert.True(Engine.OnlyPlaylistEquals(["Marvel [Smart]"], "Marvel"));
        Assert.False(Engine.OnlyCollectionEquals(["Marvel [Smart]", "DC [Smart]"], "Marvel"));
    }

    /// <summary>
    /// The stripping is wired into rule compilation for the Collections field specifically -
    /// Equal on Collections resolves to AnyCollectionEquals, not AnyItemEquals.
    /// </summary>
    [Fact]
    public void CompileRule_CollectionsEqual_MatchesBaseNameOfSuffixedCollection()
    {
        var operand = new Operand("item") { Collections = ["Marvel [Smart]"] };

        Assert.True(Compile("Collections", "Equal", "Marvel")(operand));
        Assert.False(Compile("Collections", "Equal", "DC")(operand));
        Assert.False(Compile("Collections", "NotEqual", "Marvel")(operand));
    }

    // ---------------------------------------------------------------------------------------
    // Numeric fields
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Numeric comparison boundaries on an int field, checked at equal / just-above / just-below.
    /// </summary>
    [Theory]
    [InlineData("Equal", 2000, true)]
    [InlineData("Equal", 2001, false)]
    [InlineData("NotEqual", 2000, false)]
    [InlineData("NotEqual", 1999, true)]
    [InlineData("GreaterThan", 1999, false)]
    [InlineData("GreaterThan", 2000, false)]
    [InlineData("GreaterThan", 2001, true)]
    [InlineData("GreaterThanOrEqual", 1999, false)]
    [InlineData("GreaterThanOrEqual", 2000, true)]
    [InlineData("LessThan", 2000, false)]
    [InlineData("LessThan", 1999, true)]
    [InlineData("LessThanOrEqual", 2000, true)]
    [InlineData("LessThanOrEqual", 2001, false)]
    public void CompileRule_NumericComparison_HonoursExactBoundary(string op, int productionYear, bool expected)
    {
        var rule = Compile("ProductionYear", op, "2000");

        Assert.Equal(expected, rule(new Operand("item") { ProductionYear = productionYear }));
    }

    /// <summary>
    /// Numeric fields have no "unknown" sentinel: an item with no production year defaults to 0
    /// and therefore satisfies "less than 2000". Contrast with ReleaseDate below, which does
    /// filter its 0 sentinel out.
    /// </summary>
    [Fact]
    public void CompileRule_NumericDefaultZero_SatisfiesLessThanComparisons()
    {
        var operand = new Operand("item");

        Assert.Equal(0, operand.ProductionYear);
        Assert.True(Compile("ProductionYear", "LessThan", "2000")(operand));
        Assert.False(Compile("ProductionYear", "GreaterThan", "0")(operand));
    }

    /// <summary>
    /// Float targets are parsed with InvariantCulture, so "8.5" is 8.5 regardless of the ambient
    /// locale (a comma-decimal culture must not turn it into 85).
    /// </summary>
    [Fact]
    public void CompileRule_FloatField_ParsesTargetWithInvariantCulture()
    {
        var operand = new Operand("item") { CommunityRating = 8.5f };

        Assert.True(Compile("CommunityRating", "Equal", "8.5")(operand));
        Assert.True(Compile("CommunityRating", "GreaterThan", "8.4")(operand));
        Assert.False(Compile("CommunityRating", "GreaterThan", "8.5")(operand));
    }

    [Theory]
    [InlineData("Contains")]
    [InlineData("IsIn")]
    [InlineData("MatchRegex")]
    public void CompileRule_NumericField_RejectsNonComparisonOperator(string op)
    {
        Assert.Throws<ArgumentException>(() => Compile("ProductionYear", op, "2000"));
    }

    /// <summary>
    /// RuntimeMinutes with RuntimeUnit "seconds" gets a +/- half-second tolerance window for
    /// Equal instead of exact double equality (which would essentially never hit).
    /// </summary>
    [Theory]
    [InlineData(90.0, true)]
    [InlineData(89.6, true)]
    [InlineData(90.4, true)]
    [InlineData(89.4, false)]
    [InlineData(90.6, false)]
    public void CompileRule_RuntimeMinutesInSeconds_EqualUsesHalfSecondWindow(double actualSeconds, bool expected)
    {
        var rule = Compile(new Expression("RuntimeMinutes", "Equal", "90") { RuntimeUnit = "seconds" });

        Assert.Equal(expected, rule(new Operand("item") { RuntimeMinutes = actualSeconds / 60.0 }));
    }

    [Fact]
    public void CompileRule_RuntimeMinutesInSeconds_ConvertsTargetToMinutesForOrderingOperators()
    {
        var rule = Compile(new Expression("RuntimeMinutes", "GreaterThan", "90") { RuntimeUnit = "seconds" });

        Assert.True(rule(new Operand("item") { RuntimeMinutes = 91.0 / 60.0 }));
        Assert.False(rule(new Operand("item") { RuntimeMinutes = 90.0 / 60.0 }));
    }

    // ---------------------------------------------------------------------------------------
    // Date fields
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Date Equal is a UTC day-range test [start-of-day, start-of-next-day), not a timestamp
    /// comparison - any time on the target day matches, midnight of the next day does not.
    /// </summary>
    [Fact]
    public void CompileRule_DateEqual_MatchesAnyTimeWithinTargetUtcDay()
    {
        var rule = Compile("DateCreated", "Equal", "2024-01-15");

        Assert.True(rule(new Operand("item") { DateCreated = Utc(2024, 1, 15) }));
        Assert.True(rule(new Operand("item") { DateCreated = Utc(2024, 1, 15, 23, 59, 59) }));
        Assert.False(rule(new Operand("item") { DateCreated = Utc(2024, 1, 16) }));
        Assert.False(rule(new Operand("item") { DateCreated = Utc(2024, 1, 14, 23, 59, 59) }));
    }

    [Fact]
    public void CompileRule_DateNotEqual_MatchesOnlyOutsideTargetUtcDay()
    {
        var rule = Compile("DateCreated", "NotEqual", "2024-01-15");

        Assert.False(rule(new Operand("item") { DateCreated = Utc(2024, 1, 15, 12) }));
        Assert.True(rule(new Operand("item") { DateCreated = Utc(2024, 1, 16) }));
        Assert.True(rule(new Operand("item") { DateCreated = Utc(2024, 1, 14, 23, 59, 59) }));
    }

    /// <summary>
    /// After/Before compare against midnight UTC of the target date and are STRICT, so an item
    /// stamped exactly at midnight satisfies neither.
    /// </summary>
    [Fact]
    public void CompileRule_DateAfterAndBefore_AreStrictAtMidnightBoundary()
    {
        var after = Compile("DateCreated", "After", "2024-01-15");
        var before = Compile("DateCreated", "Before", "2024-01-15");
        var midnight = new Operand("item") { DateCreated = Utc(2024, 1, 15) };

        Assert.False(after(midnight));
        Assert.False(before(midnight));
        Assert.True(after(new Operand("item") { DateCreated = Utc(2024, 1, 15, 0, 0, 1) }));
        Assert.True(before(new Operand("item") { DateCreated = Utc(2024, 1, 14, 23, 59, 59) }));
    }

    /// <summary>
    /// Weekday takes 0-6 with 0 = Sunday and is evaluated in UTC. 2024-01-15 is a Monday.
    /// </summary>
    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("2", false)]
    public void CompileRule_DateWeekday_MatchesUtcDayOfWeek(string target, bool expected)
    {
        var rule = Compile("DateCreated", "Weekday", target);

        Assert.Equal(expected, rule(new Operand("item") { DateCreated = Utc(2024, 1, 15, 12) }));
    }

    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("Monday")]
    public void CompileRule_DateWeekday_RejectsOutOfRangeOrNonNumericTarget(string target)
    {
        Assert.Throws<ArgumentException>(() => Compile("DateCreated", "Weekday", target));
    }

    /// <summary>
    /// NewerThan/OlderThan take "number:unit" and compute the cutoff at EVALUATION time (the
    /// expression tree calls DateTimeOffset.UtcNow), so a cached compiled rule cannot go stale.
    /// NewerThan is inclusive (>= cutoff), OlderThan is exclusive (&lt; cutoff).
    /// </summary>
    [Fact]
    public void CompileRule_DateNewerThanAndOlderThan_SplitAtTheRelativeCutoff()
    {
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var newerThanAWeek = Compile("DateCreated", "NewerThan", "7:days");
        var olderThanAWeek = Compile("DateCreated", "OlderThan", "7:days");

        var recent = new Operand("item") { DateCreated = now - (60 * 60 * 24) };
        var ancient = new Operand("item") { DateCreated = now - (60 * 60 * 24 * 30) };

        Assert.True(newerThanAWeek(recent));
        Assert.False(newerThanAWeek(ancient));
        Assert.False(olderThanAWeek(recent));
        Assert.True(olderThanAWeek(ancient));
    }

    [Theory]
    [InlineData("7 days")]
    [InlineData("7:fortnights")]
    [InlineData("-7:days")]
    [InlineData("days:7")]
    public void CompileRule_RelativeDate_RejectsMalformedTarget(string target)
    {
        Assert.Throws<ArgumentException>(() => Compile("DateCreated", "NewerThan", target));
    }

    [Theory]
    [InlineData("2024-1-15")]
    [InlineData("15/01/2024")]
    [InlineData("not-a-date")]
    public void CompileRule_DateField_RejectsNonIsoTarget(string target)
    {
        Assert.Throws<ArgumentException>(() => Compile("DateCreated", "After", target));
    }

    /// <summary>
    /// ReleaseDate stores "unknown" as 0, and the engine guards it: without IncludeUnknownDates
    /// an unknown release date fails even Before (which epoch 0 would otherwise satisfy). Setting
    /// IncludeUnknownDates flips it to an unconditional match. DateCreated has no such guard, so
    /// a 0 there really does count as 1970.
    /// </summary>
    [Fact]
    public void CompileRule_ReleaseDateUnknown_IsExcludedUnlessIncludeUnknownDatesIsSet()
    {
        var unknown = new Operand("item") { ReleaseDate = 0, DateCreated = 0 };

        Assert.False(Compile("ReleaseDate", "Before", "2024-01-15")(unknown));
        Assert.False(Compile("ReleaseDate", "After", "2024-01-15")(unknown));

        var including = Compile(new Expression("ReleaseDate", "Before", "2024-01-15") { IncludeUnknownDates = true });
        var includingAfter = Compile(new Expression("ReleaseDate", "After", "2024-01-15") { IncludeUnknownDates = true });
        Assert.True(including(unknown));
        Assert.True(includingAfter(unknown));

        // The guard is specific to ReleaseDate/LastEpisodeAirDate - DateCreated=0 is just 1970.
        Assert.True(Compile("DateCreated", "Before", "2024-01-15")(unknown));
    }

    [Fact]
    public void CompileRule_ReleaseDateKnown_IgnoresIncludeUnknownDates()
    {
        var known = new Operand("item") { ReleaseDate = Utc(2020, 6, 1) };
        var rule = Compile(new Expression("ReleaseDate", "Before", "2024-01-15") { IncludeUnknownDates = true });

        Assert.True(rule(known));
        Assert.False(Compile(new Expression("ReleaseDate", "After", "2024-01-15") { IncludeUnknownDates = true })(known));
    }

    // ---------------------------------------------------------------------------------------
    // Boolean + user-specific fields
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Boolean user data resolves through Operand.GetIsFavoriteByUser, which defaults a missing
    /// user entry to false - so "IsFavorite equals false" matches items the user never touched.
    /// </summary>
    [Theory]
    [InlineData(true, "Equal", "true", true)]
    [InlineData(true, "Equal", "false", false)]
    [InlineData(true, "NotEqual", "true", false)]
    [InlineData(false, "Equal", "false", true)]
    [InlineData(null, "Equal", "false", true)]
    [InlineData(null, "Equal", "true", false)]
    public void CompileRule_IsFavorite_ResolvesPerUserWithFalseDefault(bool? stored, string op, string target, bool expected)
    {
        var operand = new Operand("item");
        if (stored.HasValue)
        {
            operand.IsFavoriteByUser[UserIdN] = stored.Value;
        }

        var rule = Compile(new Expression("IsFavorite", op, target) { UserId = UserIdDashed });

        Assert.Equal(expected, rule(operand));
    }

    /// <summary>
    /// The rule may carry a dashed GUID while the operand dictionary is keyed by "N" format;
    /// Engine.NormalizeUserId bridges the two. Without normalization the lookup would miss and
    /// every favourite would silently read as false.
    /// </summary>
    [Fact]
    public void CompileRule_UserSpecificField_NormalizesDashedUserIdToNFormat()
    {
        var operand = new Operand("item");
        operand.IsFavoriteByUser[UserIdN] = true;

        Assert.True(Compile(new Expression("IsFavorite", "Equal", "true") { UserId = UserIdDashed })(operand));
        Assert.True(Compile(new Expression("IsFavorite", "Equal", "true") { UserId = UserIdN })(operand));
    }

    /// <summary>
    /// When the rule carries no UserId the playlist owner (defaultUserId) is used instead.
    /// </summary>
    [Fact]
    public void CompileRule_UserSpecificFieldWithoutUserId_FallsBackToDefaultUserId()
    {
        var operand = new Operand("item");
        operand.IsFavoriteByUser[UserIdN] = true;

        Assert.True(Compile(new Expression("IsFavorite", "Equal", "true"), UserIdDashed)(operand));
    }

    [Fact]
    public void CompileRule_UserSpecificFieldWithNoUserIdAnywhere_Throws()
    {
        Assert.Throws<ArgumentException>(() => Compile(new Expression("IsFavorite", "Equal", "true"), string.Empty));
    }

    /// <summary>
    /// Boolean targets are parsed leniently for whitespace and JSON quoting, but anything that is
    /// not true/false is rejected at compile time rather than silently treated as false.
    /// </summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("\"true\"", true)]
    [InlineData("'true'", true)]
    public void CompileRule_BooleanTarget_TolerantOfWhitespaceAndQuotes(string target, bool expected)
    {
        var operand = new Operand("item");
        operand.IsFavoriteByUser[UserIdN] = true;

        Assert.Equal(expected, Compile(new Expression("IsFavorite", "Equal", target) { UserId = UserIdDashed })(operand));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    public void CompileRule_BooleanField_RejectsNonBooleanTarget(string target)
    {
        Assert.Throws<ArgumentException>(
            () => Compile(new Expression("IsFavorite", "Equal", target) { UserId = UserIdDashed }));
    }

    [Theory]
    [InlineData("GreaterThan")]
    [InlineData("Contains")]
    public void CompileRule_BooleanField_RejectsNonEqualityOperator(string op)
    {
        Assert.Throws<ArgumentException>(
            () => Compile(new Expression("IsFavorite", op, "true") { UserId = UserIdDashed }));
    }

    /// <summary>
    /// PlaybackStatus is a per-user string that defaults to "Unplayed" for users with no data,
    /// and is compared case-insensitively.
    /// </summary>
    [Theory]
    [InlineData("Played", "Played", true)]
    [InlineData("Played", "played", true)]
    [InlineData("Played", "Unplayed", false)]
    [InlineData(null, "Unplayed", true)]
    [InlineData(null, "Played", false)]
    public void CompileRule_PlaybackStatus_DefaultsToUnplayedAndIgnoresCase(string? stored, string target, bool expected)
    {
        var operand = new Operand("item");
        if (stored != null)
        {
            operand.PlaybackStatusByUser[UserIdN] = stored;
        }

        var rule = Compile(new Expression("PlaybackStatus", "Equal", target) { UserId = UserIdDashed });

        Assert.Equal(expected, rule(operand));
    }

    /// <summary>
    /// The legacy IsPlayed field is routed to PlaybackStatus and its true/false target is
    /// translated to Played/Unplayed for backwards compatibility with old saved rules.
    /// </summary>
    [Theory]
    [InlineData("true", "Played", true)]
    [InlineData("true", "Unplayed", false)]
    [InlineData("false", "Unplayed", true)]
    [InlineData("\"false\"", "Unplayed", true)]
    public void CompileRule_LegacyIsPlayed_TranslatesBooleanTargetToPlaybackStatus(string target, string stored, bool expected)
    {
        var operand = new Operand("item");
        operand.PlaybackStatusByUser[UserIdN] = stored;

        var rule = Compile(new Expression("IsPlayed", "Equal", target) { UserId = UserIdDashed });

        Assert.Equal(expected, rule(operand));
    }

    /// <summary>
    /// PlayCount is a per-user integer defaulting to 0, compared with the ordinary .NET operators.
    /// </summary>
    [Theory]
    [InlineData(5, "GreaterThan", "4", true)]
    [InlineData(5, "GreaterThan", "5", false)]
    [InlineData(5, "GreaterThanOrEqual", "5", true)]
    [InlineData(5, "Equal", "5", true)]
    [InlineData(null, "Equal", "0", true)]
    [InlineData(null, "GreaterThan", "0", false)]
    public void CompileRule_PlayCount_ResolvesPerUserWithZeroDefault(int? stored, string op, string target, bool expected)
    {
        var operand = new Operand("item");
        if (stored.HasValue)
        {
            operand.PlayCountByUser[UserIdN] = stored.Value;
        }

        var rule = Compile(new Expression("PlayCount", op, target) { UserId = UserIdDashed });

        Assert.Equal(expected, rule(operand));
    }

    /// <summary>
    /// GetLastPlayedDateByUser returns -1 for "never played", and the engine ANDs a
    /// "!= -1" guard onto every LastPlayedDate rule - so a never-played item fails even Before,
    /// which -1 would otherwise satisfy numerically.
    /// </summary>
    [Fact]
    public void CompileRule_LastPlayedDateNeverPlayed_MatchesNothing()
    {
        var neverPlayed = new Operand("item");
        var played = new Operand("item");
        played.LastPlayedDateByUser[UserIdN] = Utc(2024, 1, 15, 12);

        var before = Compile(new Expression("LastPlayedDate", "Before", "2024-06-01") { UserId = UserIdDashed });
        var after = Compile(new Expression("LastPlayedDate", "After", "2024-06-01") { UserId = UserIdDashed });

        Assert.False(before(neverPlayed));
        Assert.False(after(neverPlayed));
        Assert.True(before(played));
        Assert.False(after(played));
    }

    /// <summary>
    /// Regression test. LastPlayedDate is a Date field, but because it is also user-specific it
    /// compiles through BuildDateExpressionForMethodCall rather than the member-access path.
    /// That method used to lack Equal/NotEqual branches, so Equal fell into the generic
    /// Enum.TryParse arm and compiled to an exact-second comparison against midnight UTC -
    /// "last played equals 2024-01-15" matched only an item played at exactly 00:00:00 UTC.
    ///
    /// Both paths now share BuildDateEqualityExpression, so Equal means the whole UTC day here
    /// exactly as it does for DateCreated, ReleaseDate and friends. Do not skip or delete this.
    /// </summary>
    [Fact]
    public void CompileRule_LastPlayedDateEqual_MatchesAnyTimeWithinTargetUtcDay()
    {
        var operand = new Operand("item");
        operand.LastPlayedDateByUser[UserIdN] = Utc(2024, 1, 15, 12);

        var rule = Compile(new Expression("LastPlayedDate", "Equal", "2024-01-15") { UserId = UserIdDashed });

        Assert.True(rule(operand));
    }

    /// <summary>
    /// The mirror of the above: NotEqual excludes the whole target UTC day rather than a single
    /// second, so an item played at midday on the target date does not match.
    /// </summary>
    [Theory]
    [InlineData(2024, 1, 15, 12, false)] // within the target day -> excluded
    [InlineData(2024, 1, 15, 0, false)]  // exactly midnight, still within the day
    [InlineData(2024, 1, 16, 0, true)]   // next day -> matches
    [InlineData(2024, 1, 14, 23, true)]  // previous day -> matches
    public void CompileRule_LastPlayedDateNotEqual_ExcludesTheWholeTargetUtcDay(
        int year, int month, int day, int hour, bool expected)
    {
        var operand = new Operand("item");
        operand.LastPlayedDateByUser[UserIdN] = Utc(year, month, day, hour);

        var rule = Compile(new Expression("LastPlayedDate", "NotEqual", "2024-01-15") { UserId = UserIdDashed });

        Assert.Equal(expected, rule(operand));
    }

    /// <summary>
    /// A never-played item (-1 sentinel) must not satisfy NotEqual. Without the engine's
    /// "!= -1" guard the day-range inequality would happily match -1, so every unplayed item
    /// in the library would leak into a "last played is not X" rule.
    /// </summary>
    [Fact]
    public void CompileRule_LastPlayedDateNotEqual_DoesNotMatchNeverPlayedItems()
    {
        var neverPlayed = new Operand("item");

        var rule = Compile(new Expression("LastPlayedDate", "NotEqual", "2024-01-15") { UserId = UserIdDashed });

        Assert.False(rule(neverPlayed));
    }

    // ---------------------------------------------------------------------------------------
    // Resolution + framerate (null/invalid sentinels)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Resolution comparisons run on pixel height, not on the label text - so "4K" is greater
    /// than "1440p" even though the strings sort the other way.
    /// </summary>
    [Theory]
    [InlineData("1080p", "GreaterThan", "720p", true)]
    [InlineData("720p", "GreaterThan", "1080p", false)]
    [InlineData("4K", "GreaterThan", "1440p", true)]
    [InlineData("1080p", "GreaterThan", "1080p", false)]
    [InlineData("1080p", "GreaterThanOrEqual", "1080p", true)]
    [InlineData("1080p", "Equal", "1080p", true)]
    [InlineData("1080p", "NotEqual", "720p", true)]
    [InlineData("720p", "LessThan", "1080p", true)]
    public void CompileRule_Resolution_ComparesByPixelHeight(string actual, string op, string target, bool expected)
    {
        var rule = Compile("Resolution", op, target);

        Assert.Equal(expected, rule(new Operand("item") { Resolution = actual }));
    }

    /// <summary>
    /// An unknown resolution (the empty default, or anything not in ResolutionTypes) yields
    /// height -1, and every resolution rule is ANDed with a "height > 0" validity check - so such
    /// items match NOTHING, not even NotEqual.
    /// </summary>
    [Theory]
    [InlineData("Equal")]
    [InlineData("NotEqual")]
    [InlineData("GreaterThan")]
    [InlineData("LessThan")]
    public void CompileRule_ResolutionUnknown_MatchesNothingIncludingNotEqual(string op)
    {
        var rule = Compile("Resolution", op, "1080p");

        Assert.False(rule(new Operand("item")));
        Assert.False(rule(new Operand("item") { Resolution = "SomethingElse" }));
    }

    [Fact]
    public void CompileRule_Resolution_RejectsTargetOutsideTheKnownSet()
    {
        Assert.Throws<ArgumentException>(() => Compile("Resolution", "Equal", "999p"));
    }

    /// <summary>
    /// Framerate is nullable and every rule is ANDed with HasValue, so items whose framerate
    /// could not be read are excluded from positive AND negative rules alike.
    /// </summary>
    [Theory]
    [InlineData("Equal")]
    [InlineData("NotEqual")]
    [InlineData("GreaterThan")]
    [InlineData("LessThan")]
    public void CompileRule_FramerateNull_MatchesNothingIncludingNotEqual(string op)
    {
        var rule = Compile("Framerate", op, "24");

        Assert.Null(new Operand("item").Framerate);
        Assert.False(rule(new Operand("item")));
    }

    [Theory]
    [InlineData(23.976f, "GreaterThan", "23", true)]
    [InlineData(23.976f, "LessThan", "23", false)]
    [InlineData(23.976f, "LessThan", "24", true)]
    [InlineData(24f, "Equal", "24", true)]
    [InlineData(24f, "NotEqual", "24", false)]
    public void CompileRule_Framerate_ComparesNumericallyWithInvariantParsing(float actual, string op, string target, bool expected)
    {
        var rule = Compile("Framerate", op, target);

        Assert.Equal(expected, rule(new Operand("item") { Framerate = actual }));
    }

    [Fact]
    public void CompileRule_Framerate_RejectsNonNumericTarget()
    {
        Assert.Throws<ArgumentException>(() => Compile("Framerate", "Equal", "twenty-four"));
    }

    // ---------------------------------------------------------------------------------------
    // Parent-aware list fields (Tags / Studios / Genres)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// With a parent source enabled, POSITIVE operators OR the item field with the parent field:
    /// an episode with no tags of its own inherits a match from its series.
    /// </summary>
    [Fact]
    public void CompileRule_TagsIncludingParentSeries_OrsItemAndParentForPositiveOperators()
    {
        var rule = Compile(new Expression("Tags", "Equal", "Anime") { IncludeParentSeriesTags = true });

        Assert.True(rule(new Operand("item") { ParentSeriesTags = ["Anime"] }));
        Assert.True(rule(new Operand("item") { Tags = ["Anime"] }));
        Assert.False(rule(new Operand("item") { Tags = ["Drama"], ParentSeriesTags = ["Comedy"] }));
    }

    /// <summary>
    /// NEGATIVE operators (NotEqual/NotContains/IsNotIn) AND the per-field results instead, so a
    /// match on EITHER the item or its parent excludes the item. Anything else would let a
    /// "not tagged Anime" rule leak in episodes of an Anime-tagged series.
    /// </summary>
    [Fact]
    public void CompileRule_TagsIncludingParentSeries_AndsItemAndParentForNegativeOperators()
    {
        var rule = Compile(new Expression("Tags", "NotEqual", "Anime") { IncludeParentSeriesTags = true });

        Assert.False(rule(new Operand("item") { Tags = ["Anime"] }));
        Assert.False(rule(new Operand("item") { ParentSeriesTags = ["Anime"] }));
        Assert.True(rule(new Operand("item") { Tags = ["Drama"], ParentSeriesTags = ["Comedy"] }));
        Assert.True(rule(new Operand("item")));
    }

    /// <summary>
    /// OnlyParent skips the item's own tags entirely.
    /// </summary>
    [Fact]
    public void CompileRule_TagsOnlyParentSeries_IgnoresTheItemsOwnTags()
    {
        var rule = Compile(new Expression("Tags", "Equal", "Anime")
        {
            OnlyParentTags = true,
            IncludeParentSeriesTags = true,
        });

        Assert.False(rule(new Operand("item") { Tags = ["Anime"] }));
        Assert.True(rule(new Operand("item") { ParentSeriesTags = ["Anime"] }));
    }

    /// <summary>
    /// OnlyParent with no parent source selected compiles to a constant false rather than falling
    /// back to the item's own field - an incoherent rule must match nothing, not everything.
    /// </summary>
    [Fact]
    public void CompileRule_TagsOnlyParentWithNoParentSource_MatchesNothing()
    {
        var rule = Compile(new Expression("Tags", "Equal", "Anime") { OnlyParentTags = true });

        Assert.False(rule(new Operand("item") { Tags = ["Anime"] }));
        Assert.False(rule(new Operand("item") { ParentSeriesTags = ["Anime"] }));
    }

    /// <summary>
    /// Genres behaves the same way as Tags, including for the album-parent source (music).
    /// </summary>
    [Fact]
    public void CompileRule_GenresIncludingParentAlbum_OrsItemAndParent()
    {
        var rule = Compile(new Expression("Genres", "Contains", "jazz") { IncludeParentAlbumGenres = true });

        Assert.True(rule(new Operand("item") { ParentAlbumGenres = ["Smooth Jazz"] }));
        Assert.False(rule(new Operand("item") { Genres = ["Rock"], ParentAlbumGenres = ["Blues"] }));
    }

    // ---------------------------------------------------------------------------------------
    // Field redirects
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// LibraryName is declared as a Text field but the engine redirects it to the LibraryNames
    /// LIST, so an item that lives in several libraries matches on any of them - and list
    /// semantics (Equal = membership) apply rather than string semantics.
    /// </summary>
    [Fact]
    public void CompileRule_LibraryName_EvaluatesAgainstTheLibraryNamesList()
    {
        var operand = new Operand("item") { LibraryNames = ["Movies", "4K Movies"] };

        Assert.True(Compile("LibraryName", "Equal", "movies")(operand));
        Assert.True(Compile("LibraryName", "Equal", "4K Movies")(operand));
        Assert.True(Compile("LibraryName", "Contains", "4k")(operand));
        Assert.False(Compile("LibraryName", "Equal", "Music")(operand));
        Assert.True(Compile("LibraryName", "NotEqual", "Music")(operand));
    }

    /// <summary>
    /// OnlyDefaultAudioLanguage swaps the evaluated property from AudioLanguages to
    /// DefaultAudioLanguages, so a film with an English track available but Japanese as its
    /// default no longer matches an "audio languages = eng" rule.
    /// </summary>
    [Fact]
    public void CompileRule_AudioLanguagesOnlyDefault_EvaluatesDefaultAudioLanguagesInstead()
    {
        var operand = new Operand("item")
        {
            AudioLanguages = ["eng", "jpn"],
            DefaultAudioLanguages = ["jpn"],
        };

        var defaultOnly = Compile(new Expression("AudioLanguages", "Equal", "eng") { OnlyDefaultAudioLanguage = true });
        var anyTrack = Compile("AudioLanguages", "Equal", "eng");

        Assert.False(defaultOnly(operand));
        Assert.True(anyTrack(operand));
        Assert.True(Compile(new Expression("AudioLanguages", "Equal", "jpn") { OnlyDefaultAudioLanguage = true })(operand));
    }

    // ---------------------------------------------------------------------------------------
    // Compile-time guards
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void CompileRule_NullRule_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Engine.CompileRule<Operand>(null!, string.Empty));
    }

    [Fact]
    public void CompileRule_UnknownFieldName_Throws()
    {
        Assert.Throws<ArgumentException>(() => Compile("NoSuchField", "Equal", "x"));
    }
}
