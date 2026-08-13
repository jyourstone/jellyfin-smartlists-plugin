using System.Diagnostics;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Core.Models;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;
using Jellyfin.Plugin.SmartLists.Utilities;
using MediaTypeConstants = Jellyfin.Plugin.SmartLists.Core.Constants.MediaTypes;

namespace Jellyfin.Plugin.SmartLists.Tests.Utilities;

/// <summary>
/// Behavioural tests for <see cref="InputValidator"/>, the plugin's request-validation
/// boundary. Expectations here were derived by reading the implementation, not by
/// assuming what validation "should" do - notably, field names and operators are
/// validated <em>syntactically</em> (character-class only), not against the
/// FieldRegistry/Operators catalogues, and string rule values deliberately allow SQL/XSS
/// payloads because they are search terms for media metadata, never SQL.
/// </summary>
public class InputValidatorTests
{
    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------

    private static void AssertValid(SmartListValidationResult result)
    {
        Assert.True(result.IsValid, "Expected valid, got: " + result.ErrorMessage);
        Assert.Null(result.ErrorMessage);
    }

    /// <summary>
    /// Asserts a rejection AND that the caller is told something actionable - a failure
    /// with a blank message would be useless to the API layer that surfaces it.
    /// </summary>
    private static void AssertInvalid(SmartListValidationResult result, string expectedMessageFragment)
    {
        Assert.False(result.IsValid, "Expected invalid, but validation passed");
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage), "Failure carried no error message");
        Assert.Contains(expectedMessageFragment, result.ErrorMessage, StringComparison.Ordinal);
    }

    private static Expression Rule(string field = "Genres", string op = "Contains", string value = "Action")
        => new(field, op, value);

    private static ExpressionSet Group(params Expression[] rules)
        => new() { Expressions = [.. rules] };

    private static SmartPlaylistDto ValidPlaylist() => new()
    {
        Name = "Valid List",
        MediaTypes = [MediaTypeConstants.Movie],
        ExpressionSets = [Group(Rule())],
    };

    // ---------------------------------------------------------------------------------
    // SmartListValidationResult contract
    // ---------------------------------------------------------------------------------

    [Fact]
    public void SmartListValidationResult_SuccessCarriesNoMessage_FailureCarriesTheGivenMessage()
    {
        var success = SmartListValidationResult.Success();
        var failure = SmartListValidationResult.Failure("boom");

        Assert.True(success.IsValid);
        Assert.Null(success.ErrorMessage);
        Assert.False(failure.IsValid);
        Assert.Equal("boom", failure.ErrorMessage);
    }

    // ---------------------------------------------------------------------------------
    // ValidateName
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\u00A0")] // NBSP is whitespace to string.IsNullOrWhiteSpace
    public void ValidateName_NullOrBlank_IsRejectedAsEmpty(string? name)
    {
        AssertInvalid(InputValidator.ValidateName(name!), "List name cannot be empty");
    }

    [Fact]
    public void ValidateName_AtMaxLength_IsAccepted()
    {
        AssertValid(InputValidator.ValidateName(new string('a', 500)));
    }

    [Fact]
    public void ValidateName_OneOverMaxLength_IsRejected()
    {
        AssertInvalid(InputValidator.ValidateName(new string('a', 501)), "cannot exceed 500 characters");
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("..\\windows\\system32")]
    [InlineData("%2e%2e/secrets")]
    [InlineData("%2E%2E\\secrets")] // pattern set is case-insensitive
    [InlineData("Season ../ Two")]
    public void ValidateName_PathTraversalSequences_AreRejected(string name)
    {
        // Path traversal is checked before the dangerous-character check, so the message
        // is the generic one - pinning it proves the ordering has not silently changed.
        var result = InputValidator.ValidateName(name);

        Assert.False(result.IsValid);
        Assert.Equal("List name contains invalid characters", result.ErrorMessage);
    }

    [Fact]
    public void ValidateName_DoubleDotWithoutSeparator_IsAccepted()
    {
        // The traversal patterns are separator-anchored; ".." on its own is a legitimate
        // name fragment and must not be blanket-banned.
        AssertValid(InputValidator.ValidateName("Seasons 1..3"));
    }

    [Theory]
    [InlineData("My\nList")]
    [InlineData("My\rList")]
    [InlineData("My\tList")]
    [InlineData("My\u0007List")]
    [InlineData("My\u0085List")] // NEL (C1 control)
    public void ValidateName_ControlCharacters_AreRejected(string name)
    {
        AssertInvalid(InputValidator.ValidateName(name), "control characters");
    }

    [Fact]
    public void ValidateName_NulByte_IsReportedAsControlCharacterNotAsDangerousChar()
    {
        // NUL appears in both the control-character check and DangerousFileNameChars.
        // The control check runs first; this pins which message a caller actually sees.
        var result = InputValidator.ValidateName("My\0List");

        Assert.False(result.IsValid);
        Assert.Equal("List name contains invalid control characters", result.ErrorMessage);
    }

    [Theory]
    [InlineData("My<List")]
    [InlineData("My>List")]
    [InlineData("My:List")]
    [InlineData("My\"List")]
    [InlineData("My/List")]
    [InlineData("My\\List")]
    [InlineData("My|List")]
    [InlineData("My?List")]
    [InlineData("My*List")]
    public void ValidateName_FileSystemHostileCharacters_AreRejected(string name)
    {
        // Names become file names on disk, so these must not get through.
        AssertInvalid(InputValidator.ValidateName(name), "List name contains invalid characters:");
    }

    [Theory]
    [InlineData("Bébé & Café")]
    [InlineData("日本語のリスト")]
    [InlineData("Movies 🎬 2024")]
    [InlineData("Ω Alpha (Director's Cut) [4K] - #1, 50% off!")]
    public void ValidateName_UnicodeAndOrdinaryPunctuation_AreAccepted(string name)
    {
        // Only the nine file-system-hostile characters are banned; everything else,
        // including non-ASCII and astral-plane emoji, is a legal list name.
        AssertValid(InputValidator.ValidateName(name));
    }

    // ---------------------------------------------------------------------------------
    // ValidateStringValue
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ValidateStringValue_Null_IsAccepted()
    {
        AssertValid(InputValidator.ValidateStringValue(null));
    }

    [Fact]
    public void ValidateStringValue_AtMaxLength_IsAccepted()
    {
        AssertValid(InputValidator.ValidateStringValue(new string('x', 2000)));
    }

    [Fact]
    public void ValidateStringValue_OneOverMaxLength_IsRejectedAndNamesTheField()
    {
        AssertInvalid(
            InputValidator.ValidateStringValue(new string('x', 2001), fieldName: "Rule value"),
            "Rule value cannot exceed 2000 characters");
    }

    [Theory]
    [InlineData("'; DROP TABLE Users;--")]
    [InlineData("1' OR '1'='1")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("javascript:void(0)")]
    [InlineData("<iframe src=x onerror=y>")]
    [InlineData("../../etc/passwd")]
    [InlineData("The SELECT Few")]
    public void ValidateStringValue_InjectionLookingPayloads_AreAccepted(string value)
    {
        // Deliberate design decision documented in InputValidator: rule values are media
        // search terms, never SQL or HTML, and real titles legitimately contain these
        // substrings. If someone adds SQL/XSS filtering here, this test must fail loudly.
        AssertValid(InputValidator.ValidateStringValue(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line1\nline2")]
    [InlineData("tab\there")]
    public void ValidateStringValue_BlankAndControlCharacters_AreAccepted(string value)
    {
        // Asymmetry with ValidateName, which rejects both: rule values are not file names.
        AssertValid(InputValidator.ValidateStringValue(value));
    }

    // ---------------------------------------------------------------------------------
    // ValidateRegexPattern
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateRegexPattern_NullOrBlank_IsRejected(string? pattern)
    {
        AssertInvalid(InputValidator.ValidateRegexPattern(pattern!), "Regex pattern cannot be empty");
    }

    [Theory]
    [InlineData("^The .*")]
    [InlineData("(?i)action")]
    [InlineData(@"\d{4}")]
    [InlineData("[A-Z][a-z]+")]
    [InlineData(@"(?<year>\d{4})-\d{2}")]
    public void ValidateRegexPattern_WellFormedPatterns_AreAccepted(string pattern)
    {
        AssertValid(InputValidator.ValidateRegexPattern(pattern));
    }

    [Theory]
    [InlineData("[")]
    [InlineData("(")]
    [InlineData("*abc")]
    [InlineData("a{2,1}")]
    [InlineData("[z-a]")]
    [InlineData("(?<")]
    [InlineData("\\")]
    public void ValidateRegexPattern_SyntacticallyInvalidPatterns_AreRejectedWithParserDetail(string pattern)
    {
        var result = InputValidator.ValidateRegexPattern(pattern);

        Assert.False(result.IsValid);
        // The parser's own diagnostic is forwarded so the user can fix their pattern.
        Assert.StartsWith("Invalid regex pattern: ", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(pattern, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRegexPattern_AtMaxLength_IsAccepted()
    {
        AssertValid(InputValidator.ValidateRegexPattern(new string('a', 1000)));
    }

    [Fact]
    public void ValidateRegexPattern_OneOverMaxLength_IsRejectedBeforeCompiling()
    {
        // 1001 characters of an *invalid* pattern: the length guard must fire first,
        // so an oversized pattern is never handed to the regex parser at all.
        AssertInvalid(
            InputValidator.ValidateRegexPattern("(" + new string('a', 1000)),
            "Regex pattern cannot exceed 1000 characters");
    }

    [Theory]
    [InlineData("(a+)+$")]
    [InlineData("(x+x+)+y")]
    [InlineData("^(t|te|tes|test)+$")]
    [InlineData("(([a-z])+.)+[A-Z]([a-z])+$")]
    public void ValidateRegexPattern_CatastrophicBacktrackingPattern_ReturnsPromptly(string pattern)
    {
        // Whatever the verdict, validation is bounded: it compiles with a 1000ms match
        // timeout, so this call can never hang the request thread. The generous budget
        // keeps the test stable on a loaded machine.
        var stopwatch = Stopwatch.StartNew();
        _ = InputValidator.ValidateRegexPattern(pattern);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            "ValidateRegexPattern took " + stopwatch.ElapsedMilliseconds + "ms");
    }

    [Theory]
    [InlineData("(a+)+$")]
    [InlineData("(x+x+)+y")]
    [InlineData("^(t|te|tes|test)+$")]
    public void ValidateRegexPattern_CatastrophicBacktrackingPattern_IsAcceptedAndBoundedAtMatchTime(string pattern)
    {
        // Deliberate: validation accepts these. Whether a pattern backtracks catastrophically
        // depends on the subject string, so probing it here against a fixed sample would give
        // both false negatives (the sample is too short to blow up) and false positives (a slow
        // sample for a pattern that is fine on real data).
        //
        // The mitigation lives at match time instead - Engine compiles every pattern with
        // Engine.RegexMatchTimeout, so a pathological pattern costs one bounded timeout per item
        // rather than pinning a CPU core for the whole refresh. See
        // EngineInternalsTests.AnyRegexMatch_CatastrophicBacktrackingPattern_ReturnsInsteadOfRunningUnbounded.
        //
        // Do not "fix" this by rejecting patterns here; it would break legitimate user regexes.
        Assert.True(InputValidator.ValidateRegexPattern(pattern).IsValid);
    }

    // ---------------------------------------------------------------------------------
    // ValidateFieldName
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateFieldName_NullOrBlank_IsRejected(string? fieldName)
    {
        AssertInvalid(InputValidator.ValidateFieldName(fieldName!), "Field name cannot be empty");
    }

    [Fact]
    public void ValidateFieldName_EveryRegisteredField_IsAccepted()
    {
        var fieldNames = FieldRegistry.GetAllFieldNames();

        Assert.NotEmpty(fieldNames);
        foreach (var fieldName in fieldNames)
        {
            var result = InputValidator.ValidateFieldName(fieldName);
            Assert.True(result.IsValid, "Registered field rejected: " + fieldName + " -> " + result.ErrorMessage);
        }
    }

    [Theory]
    [InlineData("1Genres")]        // must not start with a digit
    [InlineData("Gen-res")]
    [InlineData("Gen res")]
    [InlineData("Genres.Name")]
    [InlineData("Genres;DROP")]
    [InlineData("Genres()")]
    [InlineData("$Genres")]
    [InlineData("Gênres")]         // the character class is ASCII-only
    [InlineData("Genres\nevil")]
    public void ValidateFieldName_NonIdentifierCharacters_AreRejected(string fieldName)
    {
        AssertInvalid(InputValidator.ValidateFieldName(fieldName), "must contain only letters, numbers, and underscores");
    }

    [Fact]
    public void ValidateFieldName_UnregisteredButWellFormedIdentifier_IsAccepted()
    {
        // Pins that this is a syntax check, not an allow-list against FieldRegistry.
        // Unknown fields are rejected later, by rule compilation - not here.
        AssertValid(InputValidator.ValidateFieldName("NoSuchFieldExists_42"));
        AssertValid(InputValidator.ValidateFieldName("_leadingUnderscore"));
    }

    [Fact]
    public void ValidateFieldName_AtMaxLength_IsAccepted()
    {
        AssertValid(InputValidator.ValidateFieldName(new string('a', 100)));
    }

    [Fact]
    public void ValidateFieldName_OneOverMaxLength_IsRejected()
    {
        AssertInvalid(InputValidator.ValidateFieldName(new string('a', 101)), "cannot exceed 100 characters");
    }

    [Fact]
    public void ValidateFieldName_TrailingNewline_IsRejected()
    {
        AssertInvalid(InputValidator.ValidateFieldName("Genres\n"), "must contain only letters, numbers, and underscores");
    }

    // ---------------------------------------------------------------------------------
    // ValidateOperator
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateOperator_NullOrBlank_IsRejected(string? operatorValue)
    {
        AssertInvalid(InputValidator.ValidateOperator(operatorValue!), "Operator cannot be empty");
    }

    [Fact]
    public void ValidateOperator_EveryCatalogueOperator_IsAccepted()
    {
        Assert.NotEmpty(Operators.AllOperators);
        foreach (var op in Operators.AllOperators)
        {
            var result = InputValidator.ValidateOperator(op.Value);
            Assert.True(result.IsValid, "Catalogue operator rejected: " + op.Value + " -> " + result.ErrorMessage);
        }
    }

    [Theory]
    [InlineData(">=")]
    [InlineData("==")]
    [InlineData("Equal;")]
    [InlineData("Equal'")]
    [InlineData("Equal)")]
    [InlineData("Equal!")]
    public void ValidateOperator_SymbolicOrPunctuatedOperators_AreRejected(string operatorValue)
    {
        AssertInvalid(InputValidator.ValidateOperator(operatorValue), "Operator contains invalid characters");
    }

    [Fact]
    public void ValidateOperator_UnregisteredButWellFormedOperator_IsAccepted()
    {
        // Same as field names: character-class check only, not an allow-list.
        AssertValid(InputValidator.ValidateOperator("NoSuchOperator"));
        AssertValid(InputValidator.ValidateOperator("greater than or equal"));
    }

    [Fact]
    public void ValidateOperator_OneOverMaxLength_IsRejected()
    {
        AssertValid(InputValidator.ValidateOperator(new string('a', 50)));
        AssertInvalid(InputValidator.ValidateOperator(new string('a', 51)), "cannot exceed 50 characters");
    }

    // ---------------------------------------------------------------------------------
    // ValidateInteger
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ValidateInteger_Null_IsAcceptedEvenWhenBoundsExcludeEverything()
    {
        AssertValid(InputValidator.ValidateInteger(null, min: 10, max: 5));
    }

    [Theory]
    [InlineData(5, true)]    // at min - inclusive
    [InlineData(10, true)]   // at max - inclusive
    [InlineData(7, true)]
    [InlineData(4, false)]
    [InlineData(11, false)]
    public void ValidateInteger_BoundsAreInclusive(int value, bool expectedValid)
    {
        var result = InputValidator.ValidateInteger(value, min: 5, max: 10);

        Assert.Equal(expectedValid, result.IsValid);
    }

    [Fact]
    public void ValidateInteger_BelowMin_MessageNamesTheFieldAndTheBound()
    {
        var result = InputValidator.ValidateInteger(-1, min: 0, max: 100000, fieldName: "MaxItems");

        Assert.False(result.IsValid);
        Assert.Equal("MaxItems must be at least 0", result.ErrorMessage);
    }

    [Fact]
    public void ValidateInteger_AboveMax_MessageNamesTheFieldAndTheBound()
    {
        var result = InputValidator.ValidateInteger(100001, min: 0, max: 100000, fieldName: "MaxItems");

        Assert.False(result.IsValid);
        Assert.Equal("MaxItems cannot exceed 100000", result.ErrorMessage);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(0)]
    public void ValidateInteger_WithoutBounds_AcceptsAnything(int value)
    {
        AssertValid(InputValidator.ValidateInteger(value));
    }

    [Fact]
    public void ValidateInteger_OnlyOneBoundSupplied_TheOtherSideIsUnbounded()
    {
        AssertValid(InputValidator.ValidateInteger(int.MaxValue, min: 0));
        AssertValid(InputValidator.ValidateInteger(int.MinValue, max: 0));
    }

    // ---------------------------------------------------------------------------------
    // ValidateSmartList - composite path
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ValidateSmartList_Null_IsRejected()
    {
        AssertInvalid(InputValidator.ValidateSmartList(null), "List data is required");
    }

    [Fact]
    public void ValidateSmartList_FullyPopulatedPlaylist_IsAccepted()
    {
        var dto = ValidPlaylist();
        dto.MaxItems = 100;
        dto.MaxPlayTimeMinutes = 120;
        dto.Tags = ["curated", "weekly"];
        dto.Schedules = [new Schedule()];
        dto.VisibilitySchedules = [new Schedule()];
        dto.RandomGroupSelection = new RandomGroupSelectionDto { Enabled = true, GroupBy = "Genres", MinimumItems = 3 };
        dto.ExpressionSets =
        [
            Group(Rule("Genres", "Contains", "Action"), Rule("ProductionYear", "GreaterThan", "2000")),
            Group(Rule("Name", "MatchRegex", "^The .*")),
        ];
        dto.Bumpers = new BumperConfigDto
        {
            MediaTypes = [MediaTypeConstants.Video],
            ExpressionSets = [Group(Rule("Tags", "Contains", "bumper"))],
            BumperOrder = "Random",
            Interval = 5,
        };

        AssertValid(InputValidator.ValidateSmartList(dto));
    }

    [Fact]
    public void ValidateSmartList_ValidCollection_IsAccepted()
    {
        var dto = new SmartCollectionDto
        {
            Name = "My Collection",
            MediaTypes = [MediaTypeConstants.Series],
            ExpressionSets = [Group(Rule())],
        };

        AssertValid(InputValidator.ValidateSmartList(dto));
    }

    [Fact]
    public void ValidateSmartList_InvalidName_ReportsTheNameProblem()
    {
        var dto = ValidPlaylist();
        dto.Name = "Bad/Name";

        AssertInvalid(InputValidator.ValidateSmartList(dto), "List name contains invalid characters:");
    }

    [Fact]
    public void ValidateSmartList_NameViolationWins_OverLaterRuleViolations()
    {
        // Validation short-circuits on the first failure; this pins the order so a
        // reordering that hides the name error would be caught.
        var dto = ValidPlaylist();
        dto.Name = "";
        dto.ExpressionSets = [Group(Rule(field: "1Bad", op: "!!"))];

        AssertInvalid(InputValidator.ValidateSmartList(dto), "List name cannot be empty");
    }

    [Fact]
    public void ValidateSmartList_RuleWithMalformedFieldName_ReportsTheFieldProblem()
    {
        var dto = ValidPlaylist();
        dto.ExpressionSets = [Group(Rule(field: "Genres; DROP TABLE"))];

        AssertInvalid(InputValidator.ValidateSmartList(dto), "Field name must contain only letters");
    }

    [Fact]
    public void ValidateSmartList_RuleWithMalformedOperator_ReportsTheOperatorProblem()
    {
        var dto = ValidPlaylist();
        dto.ExpressionSets = [Group(Rule(op: ">="))];

        AssertInvalid(InputValidator.ValidateSmartList(dto), "Operator contains invalid characters");
    }

    [Theory]
    [InlineData("MatchRegex", false)]
    [InlineData("matchregex", false)] // operator sniffing is case-insensitive
    [InlineData("NotMatchRegexish", false)]
    [InlineData("Contains", true)]    // non-regex operators take the plain string path
    public void ValidateSmartList_RegexValidationAppliesOnlyToRegexOperators(string op, bool expectedValid)
    {
        var dto = ValidPlaylist();
        dto.ExpressionSets = [Group(Rule(op: op, value: "["))];

        var result = InputValidator.ValidateSmartList(dto);

        Assert.Equal(expectedValid, result.IsValid);
        if (!expectedValid)
        {
            Assert.StartsWith("Invalid regex pattern: ", result.ErrorMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ValidateSmartList_OverlongRuleValue_ReportsTheRuleValueProblem()
    {
        var dto = ValidPlaylist();
        dto.ExpressionSets = [Group(Rule(value: new string('x', 2001)))];

        AssertInvalid(InputValidator.ValidateSmartList(dto), "Rule value cannot exceed 2000 characters");
    }

    [Fact]
    public void ValidateSmartList_TooManyRuleGroups_IsRejected()
    {
        var dto = ValidPlaylist();
        dto.ExpressionSets = [.. Enumerable.Range(0, 101).Select(_ => Group(Rule()))];

        AssertInvalid(InputValidator.ValidateSmartList(dto), "Cannot have more than 100 rule groups");
    }

    [Fact]
    public void ValidateSmartList_ExactlyMaxRuleGroups_IsAccepted()
    {
        var dto = ValidPlaylist();
        dto.ExpressionSets = [.. Enumerable.Range(0, 100).Select(_ => Group(Rule()))];

        AssertValid(InputValidator.ValidateSmartList(dto));
    }

    [Fact]
    public void ValidateSmartList_TooManyRulesInOneGroup_IsRejected()
    {
        var dto = ValidPlaylist();
        dto.ExpressionSets = [Group([.. Enumerable.Range(0, 101).Select(_ => Rule())])];

        AssertInvalid(InputValidator.ValidateSmartList(dto), "Cannot have more than 100 rules in a single group");
    }

    [Fact]
    public void ValidateSmartList_GroupWithNullExpressions_IsSkippedRatherThanCrashing()
    {
        // Legacy persisted data can deserialize with a null Expressions list.
        var dto = ValidPlaylist();
        dto.ExpressionSets = [new ExpressionSet { Expressions = null }];

        AssertValid(InputValidator.ValidateSmartList(dto));
    }

    [Theory]
    [InlineData(-1, null, "MaxItems must be at least 0")]
    [InlineData(100001, null, "MaxItems cannot exceed 100000")]
    [InlineData(null, -1, "MaxPlayTimeMinutes must be at least 0")]
    [InlineData(null, 525601, "MaxPlayTimeMinutes cannot exceed 525600")]
    public void ValidateSmartList_NumericLimitsOutOfRange_AreRejected(int? maxItems, int? maxPlayTime, string expected)
    {
        var dto = ValidPlaylist();
        dto.MaxItems = maxItems;
        dto.MaxPlayTimeMinutes = maxPlayTime;

        AssertInvalid(InputValidator.ValidateSmartList(dto), expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateSmartList_TooManySchedules_IsRejected(bool visibility)
    {
        var dto = ValidPlaylist();
        var schedules = Enumerable.Range(0, 101).Select(_ => new Schedule()).ToList();
        if (visibility)
        {
            dto.VisibilitySchedules = schedules;
        }
        else
        {
            dto.Schedules = schedules;
        }

        AssertInvalid(
            InputValidator.ValidateSmartList(dto),
            visibility ? "more than 100 visibility schedules" : "more than 100 schedules");
    }

    // ---------------------------------------------------------------------------------
    // ValidateSmartList - media types
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ValidateSmartList_UnknownMediaTypes_AreSilentlyDroppedByTheDtoBeforeValidation()
    {
        // The SmartListDto.MediaTypes setter filters to known types, so the validator's
        // own "Invalid media type(s)" branch is unreachable through the DTO. Pinning the
        // drop here documents what an API caller actually gets: a quietly narrowed list,
        // not a 400.
        var dto = ValidPlaylist();
        dto.MediaTypes = [MediaTypeConstants.Movie, "Bogus", "Movie"];

        Assert.Equal([MediaTypeConstants.Movie], dto.MediaTypes);
        AssertValid(InputValidator.ValidateSmartList(dto));
    }

    [Fact]
    public void ValidateSmartList_EveryKnownMediaType_SurvivesTheDtoFilterAndValidates()
    {
        var dto = ValidPlaylist();
        dto.MediaTypes = [.. MediaTypeConstants.All];

        Assert.Equal(MediaTypeConstants.All.Length, dto.MediaTypes.Count);
        AssertValid(InputValidator.ValidateSmartList(dto));
    }

    // ---------------------------------------------------------------------------------
    // ValidateSmartList - tags
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "Tags cannot be empty or whitespace")]
    [InlineData("", "Tags cannot be empty or whitespace")]
    [InlineData("   ", "Tags cannot be empty or whitespace")]
    [InlineData("bad\ttag", "Tags cannot contain control characters")]
    [InlineData("bad\ntag", "Tags cannot contain control characters")]
    public void ValidateSmartList_MalformedTag_IsRejected(string? tag, string expected)
    {
        var dto = ValidPlaylist();
        dto.Tags = ["fine", tag!];

        AssertInvalid(InputValidator.ValidateSmartList(dto), expected);
    }

    [Fact]
    public void ValidateSmartList_TagAtMaxLength_IsAccepted_OneOver_IsRejected()
    {
        var dto = ValidPlaylist();

        dto.Tags = [new string('t', 100)];
        AssertValid(InputValidator.ValidateSmartList(dto));

        dto.Tags = [new string('t', 101)];
        AssertInvalid(InputValidator.ValidateSmartList(dto), "Tags cannot exceed 100 characters");
    }

    [Fact]
    public void ValidateSmartList_TooManyTags_IsRejected()
    {
        var dto = ValidPlaylist();
        dto.Tags = [.. Enumerable.Range(0, 101).Select(i => "tag" + i)];

        AssertInvalid(InputValidator.ValidateSmartList(dto), "Cannot have more than 100 tags");
    }

    [Fact]
    public void ValidateSmartList_EmptyTagList_IsAccepted()
    {
        // An empty list is meaningful (clears managed tags) and must not be rejected.
        var dto = ValidPlaylist();
        dto.Tags = [];

        AssertValid(InputValidator.ValidateSmartList(dto));
    }

    // ---------------------------------------------------------------------------------
    // ValidateSmartList - random group selection
    // ---------------------------------------------------------------------------------

    [Fact]
    public void ValidateSmartList_RandomGroupSelectionEnabledWithoutGroupBy_IsRejected()
    {
        var dto = ValidPlaylist();
        dto.RandomGroupSelection = new RandomGroupSelectionDto { Enabled = true, GroupBy = null };

        AssertInvalid(InputValidator.ValidateSmartList(dto), "RandomGroupSelection.GroupBy is required");
    }

    [Fact]
    public void ValidateSmartList_RandomGroupSelectionWithUnsupportedGroupBy_IsRejectedAndEchoesTheValue()
    {
        var dto = ValidPlaylist();
        dto.RandomGroupSelection = new RandomGroupSelectionDto { Enabled = true, GroupBy = "ProductionYear" };

        AssertInvalid(InputValidator.ValidateSmartList(dto), "Invalid RandomGroupSelection.GroupBy value: ProductionYear");
    }

    [Fact]
    public void ValidateSmartList_EverySupportedGroupByField_IsAccepted()
    {
        var supported = RandomGroupSelectionDto.SupportedGroupByFields;

        Assert.NotEmpty(supported);
        foreach (var field in supported)
        {
            var dto = ValidPlaylist();
            dto.RandomGroupSelection = new RandomGroupSelectionDto { Enabled = true, GroupBy = field };

            var result = InputValidator.ValidateSmartList(dto);
            Assert.True(result.IsValid, "Supported GroupBy rejected: " + field + " -> " + result.ErrorMessage);
        }
    }

    [Fact]
    public void ValidateSmartList_RandomGroupSelectionDisabled_SkipsItsValidationEntirely()
    {
        var dto = ValidPlaylist();
        dto.RandomGroupSelection = new RandomGroupSelectionDto { Enabled = false, GroupBy = "NotAField", MinimumItems = -1 };

        AssertValid(InputValidator.ValidateSmartList(dto));
    }

    [Fact]
    public void ValidateSmartList_RandomGroupSelectionNegativeMinimumItems_IsRejected()
    {
        var dto = ValidPlaylist();
        dto.RandomGroupSelection = new RandomGroupSelectionDto { Enabled = true, GroupBy = "Genres", MinimumItems = -1 };

        AssertInvalid(InputValidator.ValidateSmartList(dto), "RandomGroupSelection.MinimumItems must be at least 0");
    }

    // ---------------------------------------------------------------------------------
    // ValidateSmartList - bumpers (playlist-only branch)
    // ---------------------------------------------------------------------------------

    private static SmartPlaylistDto PlaylistWithBumpers(Action<BumperConfigDto> configure)
    {
        var dto = ValidPlaylist();
        var bumpers = new BumperConfigDto
        {
            MediaTypes = [MediaTypeConstants.Video],
            ExpressionSets = [Group(Rule("Tags", "Contains", "bumper"))],
        };
        configure(bumpers);
        dto.Bumpers = bumpers;
        return dto;
    }

    [Theory]
    [InlineData(0, "Bumper interval must be at least 1")]
    [InlineData(-5, "Bumper interval must be at least 1")]
    [InlineData(10001, "Bumper interval cannot exceed 10000")]
    public void ValidateSmartList_BumperIntervalOutOfRange_IsRejected(int interval, string expected)
    {
        var dto = PlaylistWithBumpers(b => b.Interval = interval);

        AssertInvalid(InputValidator.ValidateSmartList(dto), expected);
    }

    [Fact]
    public void ValidateSmartList_BumperRulesWithoutMediaTypes_IsRejected()
    {
        var dto = PlaylistWithBumpers(b => b.MediaTypes = []);

        AssertInvalid(InputValidator.ValidateSmartList(dto), "Bumpers require at least one media type");
    }

    [Fact]
    public void ValidateSmartList_BumperWithoutRulesOrMediaTypes_IsAccepted()
    {
        // The media-type requirement is conditional on there being bumper rules at all.
        var dto = PlaylistWithBumpers(b =>
        {
            b.MediaTypes = [];
            b.ExpressionSets = [];
        });

        AssertValid(InputValidator.ValidateSmartList(dto));
    }

    [Fact]
    public void ValidateSmartList_UnknownBumperMediaType_IsRejected()
    {
        // Unlike SmartListDto.MediaTypes, BumperConfigDto.MediaTypes is a plain list with
        // no filtering setter, so this is the one media-type branch a caller can reach.
        var dto = PlaylistWithBumpers(b => b.MediaTypes = [MediaTypeConstants.Video, "Bogus"]);

        AssertInvalid(InputValidator.ValidateSmartList(dto), "Invalid bumper media type(s): Bogus");
    }

    [Theory]
    [InlineData(MediaTypeConstants.Series)]
    [InlineData(MediaTypeConstants.Season)]
    [InlineData(MediaTypeConstants.MusicAlbum)]
    public void ValidateSmartList_ContainerBumperMediaTypes_AreRejectedBecausePlaylistsCannotHoldThem(string mediaType)
    {
        var dto = PlaylistWithBumpers(b => b.MediaTypes = [mediaType]);

        AssertInvalid(InputValidator.ValidateSmartList(dto), mediaType + " media type is not supported for bumpers");
    }

    [Theory]
    [InlineData("Random")]
    [InlineData("Name")]
    [InlineData("ReleaseDate")]
    [InlineData("")]
    public void ValidateSmartList_AllowedBumperOrders_AreAccepted(string order)
    {
        var dto = PlaylistWithBumpers(b => b.BumperOrder = order);

        AssertValid(InputValidator.ValidateSmartList(dto));
    }

    [Theory]
    [InlineData("Shuffle")]
    [InlineData("random")] // comparison is ordinal, so casing matters
    public void ValidateSmartList_UnknownBumperOrder_IsRejectedAndEchoesTheValue(string order)
    {
        var dto = PlaylistWithBumpers(b => b.BumperOrder = order);

        AssertInvalid(InputValidator.ValidateSmartList(dto), "Invalid bumper order '" + order + "'");
    }

    [Fact]
    public void ValidateSmartList_BumperRuleWithMalformedField_IsRejected()
    {
        var dto = PlaylistWithBumpers(b => b.ExpressionSets = [Group(Rule(field: "1Bad"))]);

        AssertInvalid(InputValidator.ValidateSmartList(dto), "Field name must contain only letters");
    }

    [Fact]
    public void ValidateSmartList_TooManyBumperRuleGroups_IsRejectedWithBumperLabelledMessage()
    {
        var dto = PlaylistWithBumpers(b => b.ExpressionSets = [.. Enumerable.Range(0, 101).Select(_ => Group(Rule()))]);

        AssertInvalid(InputValidator.ValidateSmartList(dto), "Bumpers cannot have more than 100 rule groups");
    }

    [Fact]
    public void ValidateSmartList_CollectionIgnoresBumperRules_BecauseBumpersArePlaylistOnly()
    {
        // Sanity-check the type test in the bumper branch: a collection carrying the same
        // rule shape never enters it.
        var dto = new SmartCollectionDto
        {
            Name = "My Collection",
            MediaTypes = [MediaTypeConstants.Series],
            ExpressionSets = [Group(Rule())],
        };

        AssertValid(InputValidator.ValidateSmartList(dto));
    }
}
