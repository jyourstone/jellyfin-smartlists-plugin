using System.Reflection;
using Jellyfin.Plugin.SmartLists.Core.Constants;
using Jellyfin.Plugin.SmartLists.Core.QueryEngine;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Mechanically enforces the cross-file contract that CLAUDE.md flags as a known footgun:
/// "Adding a new field requires updates in FieldRegistry.cs (definition), Operand.cs (property),
/// and Factory.cs (extraction logic)". Factory.cs is unreachable from here (it takes Jellyfin
/// dependencies), but the FieldRegistry -> Operand -> Engine half of that contract is pure C#
/// and is pinned below, by reflection, so it cannot drift silently.
///
/// Why these are the right invariants (all read off the production code, not assumed):
///   * Engine.BuildExpr resolves non-user-specific fields with
///     System.Linq.Expressions.Expression.PropertyOrField(param, r.MemberName) - so a registry
///     field with no same-named Operand member throws ArgumentException at rule-compile time,
///     i.e. at refresh time, for the user, not at build time.
///   * Engine.BuildUserSpecificExpression instead resolves typeof(T).GetMethod(methodName, [string])
///     where methodName comes from Expression.GetUserSpecificFieldName() = "Get{Name}ByUser".
///     User-specific fields therefore have NO plain Operand property by design.
///   * Engine.BuildExpr then dispatches purely on the resolved CLR type (string / bool / double
///     + IsDateField / float? + IsFramerateField / IEnumerable&lt;T&gt;), so a FieldType that
///     disagrees with its Operand CLR type silently routes to the wrong expression builder.
/// </summary>
public class FieldRegistryInvariantTests
{
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    private static IReadOnlyList<FieldMetadata> AllFields() =>
        FieldRegistry.GetAllFieldNames()
            .Select(name => FieldRegistry.GetField(name)!)
            .ToList();

    /// <summary>Exact-case property lookup - the registry doc comment requires an exact match.</summary>
    private static PropertyInfo? OperandProperty(string name) =>
        typeof(Operand).GetProperty(name, PublicInstance);

    /// <summary>The Get{Name}ByUser(string) accessor Engine reflects on for user-specific fields.</summary>
    private static MethodInfo? UserDataAccessor(string name) =>
        typeof(Operand).GetMethod("Get" + name + "ByUser", [typeof(string)]);

    /// <summary>
    /// The CLR type Engine.BuildExpr will actually branch on for this field, or null when the
    /// field has no Operand-backed member at all.
    /// </summary>
    private static Type? ResolveEngineFacingType(FieldMetadata field) =>
        field.IsUserSpecific
            ? UserDataAccessor(field.Name)?.ReturnType
            : OperandProperty(field.Name)?.PropertyType;

    /// <summary>
    /// FieldType -> permitted CLR type, derived from the branch order in Engine.BuildExpr
    /// (resolution before string, framerate before string, date only when the type is double).
    /// </summary>
    private static bool ClrTypeSatisfies(FieldType type, Type clr) => type switch
    {
        FieldType.Text => clr == typeof(string),
        FieldType.Simple => clr == typeof(string),
        FieldType.UserData => clr == typeof(string),
        FieldType.Resolution => clr == typeof(string),
        FieldType.Framerate => clr == typeof(float?),
        FieldType.Boolean => clr == typeof(bool),
        FieldType.Date => clr == typeof(double),
        FieldType.Numeric => clr == typeof(int) || clr == typeof(float) || clr == typeof(double),
        FieldType.List => clr != typeof(string) && typeof(IEnumerable<string>).IsAssignableFrom(clr),
        FieldType.Similarity => true,
        _ => false,
    };

    private static string ExpectedClrDescription(FieldType type) => type switch
    {
        FieldType.Text or FieldType.Simple or FieldType.UserData or FieldType.Resolution => "string",
        FieldType.Framerate => "float?",
        FieldType.Boolean => "bool",
        FieldType.Date => "double (unix seconds)",
        FieldType.Numeric => "int, float or double",
        FieldType.List => "a List<string> (anything implementing IEnumerable<string>)",
        FieldType.Similarity => "<no Operand member - handled outside Engine>",
        _ => "<unknown FieldType>",
    };

    private static string SuggestedOperandDeclaration(FieldMetadata field)
    {
        if (field.IsUserSpecific)
        {
            return "public Dictionary<string, TValue> " + field.Name + "ByUser { get; set; } = []; " +
                   "plus a public TValue Get" + field.Name + "ByUser(string userId) accessor";
        }

        return field.Type switch
        {
            FieldType.List => "public List<string> " + field.Name + " { get; set; } = [];",
            FieldType.Date => "public double " + field.Name + " { get; set; } = 0;",
            FieldType.Numeric => "public int " + field.Name + " { get; set; } = 0;",
            FieldType.Boolean => "public bool " + field.Name + " { get; set; }",
            FieldType.Framerate => "public float? " + field.Name + " { get; set; } = null;",
            _ => "public string " + field.Name + " { get; set; } = string.Empty;",
        };
    }

    // ---------------------------------------------------------------------------------------
    // 1. Registry <-> Operand wiring
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// THE invariant this whole file exists for. Every registered field must be reachable on
    /// Operand under FieldMetadata.OperandPropertyName, or Engine throws when the rule is compiled.
    /// </summary>
    [Fact]
    public void EveryRegisteredField_HasAMatchingMemberOnOperand()
    {
        var fields = AllFields();

        // Anti-vacuity: every sweep in this file iterates the registry, so an empty (or
        // reflection-broken) registry would make all of them pass while checking nothing.
        Assert.True(fields.Count > 50, "FieldRegistry returned only " + fields.Count + " fields.");
        Assert.NotNull(typeof(Operand).GetProperty("Genres", PublicInstance));
        Assert.Null(typeof(Operand).GetProperty("DefinitelyNotAnOperandProperty", PublicInstance));
        Assert.NotNull(typeof(Operand).GetMethod("GetIsFavoriteByUser", [typeof(string)]));
        Assert.Null(typeof(Operand).GetMethod("GetDefinitelyNotAFieldByUser", [typeof(string)]));

        var problems = new List<string>();
        var verified = 0;

        foreach (var field in fields)
        {
            // FieldType.Similarity is the one deliberate carve-out: SimilarTo rules are never
            // compiled into an expression at all - SmartList.cs explicitly skips them
            // ("SimilarTo is not compiled, skip it") and scores them separately - so they have
            // no Operand member by design. Asserted below so the carve-out cannot widen.
            if (field.Type == FieldType.Similarity)
            {
                Assert.True(
                    FieldRegistry.IsSimilarityField(field.Name),
                    "Field '" + field.Name + "' was exempted from the Operand-member requirement " +
                    "but IsSimilarityField says it is not a similarity field.");
                continue;
            }

            if (ResolveEngineFacingType(field) != null)
            {
                verified++;
                continue;
            }

            problems.Add(
                field.Name + " (FieldType." + field.Type + ", IsUserSpecific=" + field.IsUserSpecific +
                ") -> add to Operand.cs: " + SuggestedOperandDeclaration(field));
        }

        Assert.True(
            problems.Count == 0,
            "FieldRegistry declares " + problems.Count + " field(s) with no matching member on Operand.\n" +
            "Engine.BuildExpr resolves rule fields with Expression.PropertyOrField(param, MemberName), so " +
            "each of these throws at rule-compile time the moment a user builds a rule on it.\n" +
            "Adding a rule field means editing FieldRegistry.cs AND Operand.cs AND Factory.cs " +
            "(see CLAUDE.md, 'Adding New Rule Fields'). Missing:\n  " +
            string.Join("\n  ", problems));

        Assert.True(verified > 50, "Only " + verified + " fields were actually resolved against Operand.");
    }

    /// <summary>
    /// User-specific fields are resolved by method call, not property access, so they need three
    /// things in lockstep: the registry flag, Expression.IsUserSpecificField, and the accessor pair
    /// on Operand. Flip only the registry flag and Engine takes the PropertyOrField path and throws;
    /// flip only Expression and Engine looks for a Get*ByUser method that does not exist.
    /// </summary>
    [Fact]
    public void EveryUserSpecificField_HasByUserStorageAndIsKnownToExpression()
    {
        var problems = new List<string>();

        foreach (var field in AllFields())
        {
            var expressionAgrees = Expression.IsUserSpecificField(field.Name);

            if (field.IsUserSpecific != expressionAgrees)
            {
                problems.Add(
                    field.Name + ": FieldRegistry says IsUserSpecific=" + field.IsUserSpecific +
                    " but Expression.IsUserSpecificField says " + expressionAgrees +
                    " -> update the switch in Expression.IsUserSpecificField/GetUserSpecificFieldName to match.");
            }

            if (!field.IsUserSpecific)
            {
                continue;
            }

            if (UserDataAccessor(field.Name) == null)
            {
                problems.Add(
                    field.Name + ": Operand has no Get" + field.Name + "ByUser(string) accessor -> " +
                    "add it (Engine.BuildUserSpecificExpression resolves it with GetMethod).");
            }

            var backingStore = OperandProperty(field.Name + "ByUser");
            if (backingStore == null || !typeof(System.Collections.IDictionary).IsAssignableFrom(backingStore.PropertyType))
            {
                problems.Add(
                    field.Name + ": Operand has no Dictionary '" + field.Name + "ByUser' backing store -> " +
                    "add it (Factory.cs populates it per referenced user).");
            }

            if (OperandProperty(field.Name) != null)
            {
                problems.Add(
                    field.Name + ": user-specific fields must NOT have a plain Operand property of the " +
                    "same name - Engine never reads it, so it would be dead state that silently disagrees " +
                    "with the per-user dictionary.");
            }
        }

        Assert.True(
            problems.Count == 0,
            "User-specific field wiring is inconsistent across FieldRegistry.cs, Expression.cs and Operand.cs:\n  " +
            string.Join("\n  ", problems));

        // Anti-vacuity: the loop above only bites on user-specific fields.
        Assert.Contains(AllFields(), f => f.IsUserSpecific);
    }

    // ---------------------------------------------------------------------------------------
    // 2. Declared FieldType vs. actual CLR type
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Engine.BuildExpr picks its expression builder from the resolved CLR type, so a FieldType
    /// that disagrees with the Operand type does not fail loudly - it silently routes the rule to
    /// the wrong builder (e.g. a Date field typed as string skips BuildDateExpression entirely and
    /// gets compared as text).
    /// </summary>
    [Fact]
    public void EveryFieldType_AgreesWithItsOperandClrType()
    {
        var problems = new List<string>();
        var checkedTypes = new HashSet<FieldType>();

        foreach (var field in AllFields())
        {
            if (field.Type == FieldType.Similarity)
            {
                continue;
            }

            var clr = ResolveEngineFacingType(field);
            if (clr == null)
            {
                // Reported by EveryRegisteredField_HasAMatchingMemberOnOperand; not double-reported here.
                continue;
            }

            checkedTypes.Add(field.Type);

            if (!ClrTypeSatisfies(field.Type, clr))
            {
                problems.Add(
                    field.Name + ": declared FieldType." + field.Type + " (expects " +
                    ExpectedClrDescription(field.Type) + ") but Operand exposes it as '" +
                    clr.Name + "' -> change one side so they agree.");
            }
        }

        Assert.True(
            problems.Count == 0,
            "FieldType and Operand CLR type disagree for " + problems.Count + " field(s). " +
            "Engine.BuildExpr dispatches on the CLR type, so these rules compile but evaluate " +
            "through the wrong comparison path:\n  " +
            string.Join("\n  ", problems));

        // Anti-vacuity: every FieldType except Similarity must have been exercised by a real field,
        // so ClrTypeSatisfies cannot rot into a mapping nothing reaches.
        var unexercised = Enum.GetValues<FieldType>()
            .Where(t => t != FieldType.Similarity && !checkedTypes.Contains(t))
            .ToArray();
        Assert.True(
            unexercised.Length == 0,
            "No registered field exercised FieldType(s): " + string.Join(", ", unexercised));
    }

    // ---------------------------------------------------------------------------------------
    // 3. Operator catalogue
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// An operator that is allowed on a field but missing from Operators.AllOperators can never be
    /// picked in the UI (the dropdown is built from AllOperators) and has no display label.
    /// </summary>
    [Fact]
    public void EveryAllowedOperator_IsDeclaredInTheOperatorCatalogue()
    {
        var catalogued = Operators.AllOperators.Select(op => op.Value).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var field in AllFields())
        {
            Assert.NotNull(field.AllowedOperators);

            var unknown = field.AllowedOperators.Where(op => !catalogued.Contains(op)).ToArray();
            if (unknown.Length > 0)
            {
                problems.Add(
                    field.Name + ": unknown operator(s) [" + string.Join(", ", unknown) +
                    "] -> add them to Operators.AllOperators (with a UI label) or fix the typo.");
            }

            var duplicates = field.AllowedOperators
                .GroupBy(op => op, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();
            if (duplicates.Length > 0)
            {
                problems.Add(field.Name + ": duplicate operator(s) [" + string.Join(", ", duplicates) + "].");
            }

            if (field.AllowedOperators.Length == 0)
            {
                problems.Add(
                    field.Name + ": no allowed operators -> Operators.GetOperatorsForField would fall " +
                    "back to the ENTIRE catalogue for this field, letting nonsensical rules through.");
            }
        }

        Assert.True(
            problems.Count == 0,
            "FieldRegistry operator declarations are inconsistent with Core/Constants/Operators.cs:\n  " +
            string.Join("\n  ", problems));
    }

    /// <summary>
    /// GetOperatorsForField is what Engine validates rules against (Engine.BuildStringExpression
    /// rejects operators outside it), so it must surface exactly the field's declared list - and
    /// must return empty for an unknown field, which is what makes Operators.GetOperatorsForField
    /// fall back to the full catalogue.
    /// </summary>
    [Fact]
    public void GetOperatorsForField_MirrorsMetadata_AndFallsBackForUnknownFields()
    {
        foreach (var field in AllFields())
        {
            Assert.Equal(field.AllowedOperators, FieldRegistry.GetOperatorsForField(field.Name));
        }

        Assert.Empty(FieldRegistry.GetOperatorsForField("NoSuchField"));
        Assert.Equal(
            Operators.AllOperators.Select(op => op.Value),
            Operators.GetOperatorsForField("NoSuchField"));
    }

    // ---------------------------------------------------------------------------------------
    // 4. Predicate helpers vs. metadata
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The Is*Field predicates are cached HashSets built once in the static constructor; every
    /// caller in SmartList/Engine/InputValidator uses them instead of reading FieldType directly,
    /// so a set that drifts from the metadata is invisible until runtime.
    /// </summary>
    [Fact]
    public void TypePredicates_ClassifyExactlyTheFieldsWithThatFieldType()
    {
        var problems = new List<string>();

        var predicates = new (string Name, FieldType Type, Func<string, bool> Predicate)[]
        {
            ("IsDateField", FieldType.Date, FieldRegistry.IsDateField),
            ("IsListField", FieldType.List, FieldRegistry.IsListField),
            ("IsNumericField", FieldType.Numeric, FieldRegistry.IsNumericField),
            ("IsBooleanField", FieldType.Boolean, FieldRegistry.IsBooleanField),
            ("IsSimpleField", FieldType.Simple, FieldRegistry.IsSimpleField),
            ("IsResolutionField", FieldType.Resolution, FieldRegistry.IsResolutionField),
            ("IsFramerateField", FieldType.Framerate, FieldRegistry.IsFramerateField),
            ("IsSimilarityField", FieldType.Similarity, FieldRegistry.IsSimilarityField),
        };

        foreach (var field in AllFields())
        {
            foreach (var (name, type, predicate) in predicates)
            {
                var expected = field.Type == type;
                if (predicate(field.Name) != expected)
                {
                    problems.Add(
                        name + "('" + field.Name + "') returned " + !expected + " but the field is declared " +
                        "FieldType." + field.Type + " -> the derived HashSet in FieldRegistry's static " +
                        "constructor no longer matches the metadata.");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    /// <summary>
    /// IsPeopleField and IsUserDataField are derived from flags rather than FieldType, so they get
    /// their own sweep. IsPeopleField additionally implies the People extraction group - that is
    /// what makes a people rule two-phase filtered and cache-backed.
    /// </summary>
    [Fact]
    public void FlagPredicates_MatchThePeopleAndUserSpecificMetadataFlags()
    {
        var problems = new List<string>();

        foreach (var field in AllFields())
        {
            if (FieldRegistry.IsPeopleField(field.Name) != field.IsPeopleField)
            {
                problems.Add(
                    "IsPeopleField('" + field.Name + "') disagrees with FieldMetadata.IsPeopleField=" +
                    field.IsPeopleField + ".");
            }

            if (FieldRegistry.IsUserDataField(field.Name) != field.IsUserSpecific)
            {
                problems.Add(
                    "IsUserDataField('" + field.Name + "') disagrees with FieldMetadata.IsUserSpecific=" +
                    field.IsUserSpecific + ".");
            }

            if (field.IsPeopleField && !field.ExtractionGroup.HasFlag(ExtractionGroup.People))
            {
                problems.Add(
                    field.Name + " is a people field but its ExtractionGroup is " + field.ExtractionGroup +
                    " -> it must include ExtractionGroup.People or Factory will never populate it and " +
                    "two-phase filtering will not defer it.");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    /// <summary>
    /// IsExpensiveField drives two-phase filtering in SmartList.cs: a field wrongly classified cheap
    /// gets extracted for every candidate item instead of only for Phase 1 survivors.
    /// </summary>
    [Fact]
    public void IsExpensiveField_EqualsExtractionGroupOutsideTheCheapGroups()
    {
        var problems = new List<string>();

        foreach (var field in AllFields())
        {
            var expected = (field.ExtractionGroup & ~FieldRegistry.CheapExtractionGroups) != ExtractionGroup.None;

            if (field.IsExpensive != expected || FieldRegistry.IsExpensiveField(field.Name) != expected)
            {
                problems.Add(
                    field.Name + " (ExtractionGroup=" + field.ExtractionGroup + "): expected IsExpensive=" +
                    expected + " but FieldMetadata.IsExpensive=" + field.IsExpensive +
                    " and IsExpensiveField=" + FieldRegistry.IsExpensiveField(field.Name) + ".");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    /// <summary>
    /// Anchors the cheap/expensive tier boundary to concrete fields so the previous test cannot be
    /// satisfied by moving a group between CheapExtractionGroups and the expensive tier. Expensive
    /// groups MUST have a cache in RefreshQueueService.RefreshCache (FieldRegistry.cs header).
    /// </summary>
    [Theory]
    [InlineData("Name", false)]              // ExtractionGroup.None - direct BaseItem property
    [InlineData("Overview", false)]          // TextContent - cheap
    [InlineData("Genres", false)]            // ItemLists - cheap
    [InlineData("Tags", false)]              // ItemLists - cheap
    [InlineData("DateCreated", false)]       // Dates - cheap
    [InlineData("LibraryName", false)]       // LibraryInfo - cheap
    [InlineData("IsFavorite", false)]        // UserData - cheap
    [InlineData("Album", false)]             // AudioMetadata - cheap
    [InlineData("FolderPath", false)]        // FileInfo - cheap
    [InlineData("Actors", true)]             // People
    [InlineData("Collections", true)]        // Collections
    [InlineData("Playlists", true)]          // Playlists
    [InlineData("Resolution", true)]         // VideoQuality
    [InlineData("AudioLanguages", true)]     // AudioLanguages
    [InlineData("AudioCodec", true)]         // AudioQuality
    [InlineData("SeriesName", true)]         // SeriesName
    [InlineData("NextUnwatched", true)]      // NextUnwatched
    [InlineData("LastEpisodeAirDate", true)] // LastEpisodeAirDate
    [InlineData("ExternalList", true)]       // ExternalLists
    [InlineData("SimilarTo", true)]          // SimilarTo
    public void IsExpensiveField_PinsTheKnownTierOfEachExtractionGroup(string fieldName, bool expected)
    {
        Assert.NotNull(FieldRegistry.GetField(fieldName));
        Assert.Equal(expected, FieldRegistry.IsExpensiveField(fieldName));
    }

    /// <summary>
    /// Every predicate is a HashSet.Contains, so an unregistered name must simply be false rather
    /// than throw - InputValidator and the API layer call these on user-supplied field names.
    /// </summary>
    [Theory]
    [InlineData("NoSuchField")]
    [InlineData("")]
    [InlineData("Name; DROP TABLE")]
    public void AllPredicates_ReturnFalseForAnUnregisteredFieldName(string fieldName)
    {
        Assert.False(FieldRegistry.IsDateField(fieldName));
        Assert.False(FieldRegistry.IsListField(fieldName));
        Assert.False(FieldRegistry.IsPeopleField(fieldName));
        Assert.False(FieldRegistry.IsNumericField(fieldName));
        Assert.False(FieldRegistry.IsBooleanField(fieldName));
        Assert.False(FieldRegistry.IsExpensiveField(fieldName));
        Assert.False(FieldRegistry.IsUserDataField(fieldName));
        Assert.False(FieldRegistry.IsSimpleField(fieldName));
        Assert.False(FieldRegistry.IsResolutionField(fieldName));
        Assert.False(FieldRegistry.IsFramerateField(fieldName));
        Assert.False(FieldRegistry.IsSimilarityField(fieldName));
    }

    // ---------------------------------------------------------------------------------------
    // 5. Lookup behaviour
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The registry is a Dictionary keyed OrdinalIgnoreCase, so a second AddField call for the same
    /// name silently overwrites the first. Names must therefore be unique even case-insensitively,
    /// and the key must round-trip to the metadata that carries it (OperandPropertyName => Name is
    /// what Engine reflects on).
    /// </summary>
    [Fact]
    public void FieldNames_AreCaseInsensitivelyUnique_AndRoundTripThroughGetField()
    {
        var names = FieldRegistry.GetAllFieldNames();

        var collisions = names
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" / ", g))
            .ToArray();

        Assert.True(
            collisions.Length == 0,
            "Field names collide case-insensitively, so one registration silently overwrites the " +
            "other in FieldRegistry's OrdinalIgnoreCase dictionary: " + string.Join("; ", collisions));

        foreach (var name in names)
        {
            var field = FieldRegistry.GetField(name);
            Assert.NotNull(field);
            Assert.Equal(name, field!.Name);
            Assert.Equal(name, field.OperandPropertyName);
            Assert.False(string.IsNullOrWhiteSpace(field.DisplayLabel), "Field '" + name + "' has no DisplayLabel.");
        }
    }

    /// <summary>
    /// GetField must be tolerant of user-supplied casing (the registry comparer is OrdinalIgnoreCase)
    /// and must answer null - not throw - for names it does not know, because it is reached from the
    /// API with unvalidated rule payloads.
    /// </summary>
    [Fact]
    public void GetField_IsCaseInsensitive_AndReturnsNullForUnknownNames()
    {
        Assert.NotNull(FieldRegistry.GetField("productionyear"));
        Assert.NotNull(FieldRegistry.GetField("PRODUCTIONYEAR"));
        Assert.Equal("ProductionYear", FieldRegistry.GetField("productionyear")!.Name);

        Assert.Null(FieldRegistry.GetField("NoSuchField"));
        Assert.Null(FieldRegistry.GetField(string.Empty));
        Assert.Equal(ExtractionGroup.None, FieldRegistry.GetExtractionGroup("NoSuchField"));
    }

    // ---------------------------------------------------------------------------------------
    // 6. Category and extraction-group membership
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// GetFieldsByCategory is what builds the field dropdown in the UI (via
    /// GetAvailableFieldsForApi), so a field missing from its own category bucket is a field the
    /// user can never select, and a field in two buckets shows up twice.
    /// </summary>
    [Fact]
    public void EveryField_AppearsInExactlyOneCategoryBucket()
    {
        var seen = new Dictionary<string, List<FieldCategory>>(StringComparer.Ordinal);

        foreach (var category in Enum.GetValues<FieldCategory>())
        {
            foreach (var field in FieldRegistry.GetFieldsByCategory(category))
            {
                if (!seen.TryGetValue(field.Name, out var categories))
                {
                    categories = [];
                    seen[field.Name] = categories;
                }

                categories.Add(category);

                Assert.Equal(category, field.Category);
            }
        }

        var problems = new List<string>();

        foreach (var field in AllFields())
        {
            if (!seen.TryGetValue(field.Name, out var categories))
            {
                problems.Add(
                    field.Name + " is registered under FieldCategory." + field.Category +
                    " but GetFieldsByCategory does not return it - it will be missing from the UI dropdown.");
            }
            else if (categories.Count != 1)
            {
                problems.Add(
                    field.Name + " appears in " + categories.Count + " category buckets (" +
                    string.Join(", ", categories) + ").");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
        Assert.Equal(FieldRegistry.GetAllFieldNames().Length, seen.Count);
    }

    /// <summary>
    /// GetFieldsInExtractionGroup is how SmartList decides which extraction work a rule set needs.
    /// Membership is HasFlag-based, so a field carrying two flags must appear under both.
    /// </summary>
    [Fact]
    public void ExtractionGroupMembership_MatchesEachFieldsDeclaredFlags()
    {
        var problems = new List<string>();

        foreach (var group in Enum.GetValues<ExtractionGroup>())
        {
            if (group == ExtractionGroup.None)
            {
                Assert.Empty(FieldRegistry.GetFieldsInExtractionGroup(group));
                continue;
            }

            var actual = FieldRegistry.GetFieldsInExtractionGroup(group).ToHashSet(StringComparer.Ordinal);
            var expected = AllFields()
                .Where(f => f.ExtractionGroup.HasFlag(group))
                .Select(f => f.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var missing in expected.Except(actual))
            {
                problems.Add("ExtractionGroup." + group + " should contain '" + missing + "' but does not.");
            }

            foreach (var extra in actual.Except(expected))
            {
                problems.Add("ExtractionGroup." + group + " contains '" + extra + "' which does not declare that flag.");
            }
        }

        foreach (var field in AllFields())
        {
            if (FieldRegistry.GetExtractionGroup(field.Name) != field.ExtractionGroup)
            {
                problems.Add(
                    "GetExtractionGroup('" + field.Name + "') returned " +
                    FieldRegistry.GetExtractionGroup(field.Name) + " but the metadata declares " +
                    field.ExtractionGroup + ".");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    /// <summary>
    /// Both accessors document that they hand back a defensive copy. If either starts returning the
    /// cached List directly, any caller (the API layer serialises these) could mutate the registry
    /// for the whole process.
    /// </summary>
    [Fact]
    public void CategoryAndGroupAccessors_ReturnAFreshCopyPerCall()
    {
        var firstCategory = FieldRegistry.GetFieldsByCategory(FieldCategory.Video);
        var secondCategory = FieldRegistry.GetFieldsByCategory(FieldCategory.Video);
        Assert.NotSame(firstCategory, secondCategory);
        Assert.Equal(firstCategory.Select(f => f.Name), secondCategory.Select(f => f.Name));

        var firstGroup = FieldRegistry.GetFieldsInExtractionGroup(ExtractionGroup.People);
        var secondGroup = FieldRegistry.GetFieldsInExtractionGroup(ExtractionGroup.People);
        Assert.NotSame(firstGroup, secondGroup);
        Assert.Equal(firstGroup, secondGroup);
    }
}
