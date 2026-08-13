using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartLists.Core;
using Jellyfin.Plugin.SmartLists.Core.Orders;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.Orders;

/// <summary>
/// Pins the wiring layer every sort option in the plugin travels through.
///
/// The chain, read off the production code (SmartList.InitializeFromDto):
///   SortOption { SortBy = "ProductionYear", SortOrder = Descending }
///     -> OrderFactory.IsDirectionless("ProductionYear") == false
///     -> OrderFactory.CreateOrder("ProductionYear" + " " + "Descending")
///     -> OrderMap lookup -> new ProductionYearOrderDesc()
///     -> SmartList.IsDescendingOrder(order) decides the LINQ direction in ApplySortingCore.
///
/// Every link is a plain string lookup with a silent fallback: an unregistered name returns
/// NoOrder, and a single NoOrder is then rewritten to "Name Ascending" by ResolveDefaultOrder.
/// So a broken link never throws - the user just silently gets the wrong order. That is the
/// exact failure class these tests exist to catch, which is why they assert resulting ORDER and
/// exact names rather than "returned something non-null".
/// </summary>
public class OrderFactoryTests
{
    private const BindingFlags NonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>
    /// The sort names the plugin promises to serve, hardcoded on purpose.
    /// <see cref="OrderMap_ContainsExactlyTheDocumentedSortNames"/> holds this list and the real
    /// private OrderMap to each other, so a REMOVED registration fails here (the reflection sweep
    /// alone could not see a removal) and an ADDED one fails there until it is documented.
    /// </summary>
    private static readonly string[] RegisteredSortNames =
    [
        "Name Ascending",
        "Name Descending",
        "Name (Ignore Articles) Ascending",
        "Name (Ignore Articles) Descending",
        "ProductionYear Ascending",
        "ProductionYear Descending",
        "DateCreated Ascending",
        "DateCreated Descending",
        "Similarity Ascending",
        "Similarity Descending",
        "ReleaseDate Ascending",
        "ReleaseDate Descending",
        "CommunityRating Ascending",
        "CommunityRating Descending",
        "PlayCount (owner) Ascending",
        "PlayCount (owner) Descending",
        "LastPlayed (owner) Ascending",
        "LastPlayed (owner) Descending",
        "Runtime Ascending",
        "Runtime Descending",
        "Resolution Ascending",
        "Resolution Descending",
        "SeriesName Ascending",
        "SeriesName Descending",
        "SeriesName (Ignore Articles) Ascending",
        "SeriesName (Ignore Articles) Descending",
        "AlbumName Ascending",
        "AlbumName Descending",
        "Artist Ascending",
        "Artist Descending",
        "TrackNumber Ascending",
        "TrackNumber Descending",
        "SeasonNumber Ascending",
        "SeasonNumber Descending",
        "EpisodeNumber Ascending",
        "EpisodeNumber Descending",
        "Random",
        "Rule Block Order Ascending",
        "Rule Block Order Descending",
        "External List Order Ascending",
        "External List Order Descending",
        "LastEpisodeAirDate Ascending",
        "LastEpisodeAirDate Descending",
        "Round Robin Ascending",
        "Round Robin Descending",
        "Random Round Robin",
        "Shuffled Round Robin",
        "Least Recently Watched Round Robin",
        "NoOrder",
    ];

    public static TheoryData<string> AllRegisteredSortNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in RegisteredSortNames)
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>The real private registry, so tests enumerate production data instead of a copy.</summary>
    private static IReadOnlyDictionary<string, Func<Order>> OrderMap()
    {
        var field = typeof(OrderFactory).GetField("OrderMap", NonPublicStatic);
        Assert.NotNull(field);
        var map = field!.GetValue(null) as Dictionary<string, Func<Order>>;
        Assert.NotNull(map);
        return map!;
    }

    /// <summary>SmartList.IsDescendingOrder - private static, so reflection rather than InternalsVisibleTo.</summary>
    private static bool IsDescendingOrder(Order order)
    {
        var method = typeof(SmartList).GetMethod("IsDescendingOrder", NonPublicStatic);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, [order])!;
    }

    private static Movie MovieNamed(string name) => new() { Id = Guid.NewGuid(), Name = name };

    // ---------------------------------------------------------------------------------------
    // Registry shape
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void OrderMap_ContainsExactlyTheDocumentedSortNames()
    {
        var actual = OrderMap().Keys.ToHashSet(StringComparer.Ordinal);
        var expected = RegisteredSortNames.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(RegisteredSortNames.Length, RegisteredSortNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(actual.Except(expected, StringComparer.Ordinal));
        Assert.Empty(expected.Except(actual, StringComparer.Ordinal));
    }

    /// <summary>
    /// Name round-trip. This is not cosmetic: FieldRequirements.Analyze does string matching on
    /// order.Name to decide which expensive extraction groups to switch on, so an order whose Name
    /// disagrees with its registration key silently loses the data it needs to sort correctly.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllRegisteredSortNames))]
    public void CreateOrder_RegisteredSortName_ReturnsOrderReportingThatExactName(string sortName)
    {
        var order = OrderFactory.CreateOrder(sortName);

        Assert.NotNull(order);
        Assert.Equal(sortName, order.Name);

        // Only the literal "NoOrder" registration may produce NoOrder; anything else falling back
        // to NoOrder means the registration is broken and the sort would silently do nothing.
        if (!string.Equals(sortName, "NoOrder", StringComparison.Ordinal))
        {
            Assert.IsNotType<NoOrder>(order);
        }
    }

    /// <summary>
    /// The map stores factory delegates, not instances. That matters because several orders carry
    /// per-list mutable state that SmartList injects (SimilarityOrder.Scores,
    /// RuleBlockOrder.GroupMappings, RoundRobinBase.GroupByField) - a shared instance would leak
    /// one smart list's scores into another's sort.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllRegisteredSortNames))]
    public void CreateOrder_CalledTwice_ReturnsIndependentInstances(string sortName)
    {
        var first = OrderFactory.CreateOrder(sortName);
        var second = OrderFactory.CreateOrder(sortName);

        Assert.NotSame(first, second);
        Assert.Equal(first.GetType(), second.GetType());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Totally Bogus Sort")]
    [InlineData("ProductionYear")]              // bare directional name - SmartList must append a direction
    [InlineData("productionyear ascending")]    // OrderMap uses the default ordinal comparer
    [InlineData("ProductionYear  Ascending")]   // double space
    [InlineData("ProductionYear Asc")]
    public void CreateOrder_UnrecognisedName_FallsBackToNoOrder(string? sortName)
    {
        var order = OrderFactory.CreateOrder(sortName!);

        Assert.IsType<NoOrder>(order);
        Assert.Equal("NoOrder", order.Name);
    }

    /// <summary>
    /// Every directional sort must expose both directions, and the two must be different classes.
    /// A half-registered pair means the UI offers a direction toggle that silently does nothing.
    /// </summary>
    [Fact]
    public void OrderMap_EveryDirectionalSort_RegistersBothDirectionsAsDistinctTypes()
    {
        var map = OrderMap();
        var ascending = map.Keys.Where(k => k.EndsWith(" Ascending", StringComparison.Ordinal)).ToList();
        var descending = map.Keys.Where(k => k.EndsWith(" Descending", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(ascending);
        Assert.Equal(ascending.Count, descending.Count);

        foreach (var ascName in ascending)
        {
            var baseName = ascName[..^" Ascending".Length];
            var descName = baseName + " Descending";
            Assert.True(map.ContainsKey(descName), $"'{ascName}' is registered but '{descName}' is not.");

            var asc = OrderFactory.CreateOrder(ascName);
            var desc = OrderFactory.CreateOrder(descName);
            Assert.NotEqual(asc.GetType(), desc.GetType());
        }

        foreach (var descName in descending)
        {
            var baseName = descName[..^" Descending".Length];
            Assert.True(
                map.ContainsKey(baseName + " Ascending"),
                $"'{descName}' is registered but '{baseName} Ascending' is not.");
        }
    }

    /// <summary>
    /// SmartList.IsDescendingOrder is a hand-maintained 21-way `order is XDesc ||` chain. Adding a
    /// new *Desc class and forgetting to extend that chain compiles fine and silently sorts the new
    /// descending option ascending in every multi-sort. This sweeps the whole real registry.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllRegisteredSortNames))]
    public void IsDescendingOrder_MatchesTheDescendingSuffixForEveryRegisteredName(string sortName)
    {
        var order = OrderFactory.CreateOrder(sortName);
        var expected = sortName.EndsWith(" Descending", StringComparison.Ordinal);

        Assert.Equal(expected, IsDescendingOrder(order));
    }

    /// <summary>
    /// IsDirectionless decides whether SmartList looks the sort up bare or as "X {SortOrder}".
    /// Getting it wrong in either direction produces a lookup miss -> NoOrder -> silent Name sort,
    /// so the flag must agree exactly with how the name is registered.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllRegisteredSortNames))]
    public void IsDirectionless_IsTrueExactlyForNamesRegisteredWithoutADirectionSuffix(string sortName)
    {
        var map = OrderMap();
        var hasDirectionSuffix =
            sortName.EndsWith(" Ascending", StringComparison.Ordinal) ||
            sortName.EndsWith(" Descending", StringComparison.Ordinal);

        Assert.Equal(!hasDirectionSuffix, OrderFactory.IsDirectionless(sortName));

        if (!hasDirectionSuffix)
        {
            // A bare registration must not also have directional siblings, otherwise SmartList
            // would look up the bare name and never reach them.
            Assert.False(map.ContainsKey(sortName + " Ascending"));
            Assert.False(map.ContainsKey(sortName + " Descending"));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Random Round Robin ")]
    [InlineData("random")]
    public void IsDirectionless_UnrecognisedName_IsFalse(string? sortName)
    {
        Assert.False(OrderFactory.IsDirectionless(sortName!));
    }

    /// <summary>
    /// Catches the "class written, registration forgotten" case: a concrete Order that nothing can
    /// ever construct is dead code and, worse, usually means the UI option that was added alongside
    /// it silently resolves to NoOrder.
    /// </summary>
    [Fact]
    public void OrderMap_ProducesEveryConcreteOrderSubclass_ExceptThePipelineOnlyWrapper()
    {
        // ChildAggregatingOrder is deliberately unregistered: it takes constructor arguments and is
        // built by SmartList.WrapOrdersWithChildAggregation, never looked up by name.
        var deliberatelyUnregistered = new[] { typeof(ChildAggregatingOrder) };

        Type[] allTypes;
        try
        {
            allTypes = typeof(Order).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            allTypes = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
        }

        var concreteOrders = allTypes
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition && typeof(Order).IsAssignableFrom(t))
            .Except(deliberatelyUnregistered)
            .ToHashSet();

        var produced = OrderMap().Values.Select(factory => factory().GetType()).ToHashSet();

        var unreachable = concreteOrders.Except(produced).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Empty(unreachable);

        // And nothing the map produces should be missing from the assembly scan (sanity on the scan).
        Assert.Empty(produced.Except(concreteOrders).Except(deliberatelyUnregistered));
    }

    // ---------------------------------------------------------------------------------------
    // The factory actually wires up working, correctly-directed sorts
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void CreateOrder_ProductionYearAscendingAndDescending_ProduceOppositeOrderings()
    {
        var items = new List<BaseItem>
        {
            new Movie { Id = Guid.NewGuid(), Name = "Mid", ProductionYear = 1999 },
            new Movie { Id = Guid.NewGuid(), Name = "New", ProductionYear = 2010 },
            new Movie { Id = Guid.NewGuid(), Name = "Old", ProductionYear = 1980 },
        };

        var ascending = OrderFactory.CreateOrder("ProductionYear Ascending").OrderBy(items).Select(i => i.Name);
        var descending = OrderFactory.CreateOrder("ProductionYear Descending").OrderBy(items).Select(i => i.Name);

        Assert.Equal(["Old", "Mid", "New"], ascending);
        Assert.Equal(["New", "Mid", "Old"], descending);
    }

    /// <summary>
    /// The Similarity pair inverts the usual Order/OrderDesc naming: the plain class
    /// <see cref="SimilarityOrder"/> IS the descending one and <see cref="SimilarityOrderAsc"/> the
    /// ascending one. A "tidy-up" rename would swap the two registrations without any compile
    /// error, so this pins the direction behaviourally, through the factory, not by class name.
    /// </summary>
    [Fact]
    public void CreateOrder_SimilarityDescending_PutsTheHighestScoreFirstDespiteTheInvertedClassNames()
    {
        var low = MovieNamed("Low");
        var high = MovieNamed("High");
        var mid = MovieNamed("Mid");
        var items = new List<BaseItem> { low, high, mid };

        var descending = OrderFactory.CreateOrder("Similarity Descending");
        var ascending = OrderFactory.CreateOrder("Similarity Ascending");
        Assert.IsType<SimilarityOrder>(descending);
        Assert.IsType<SimilarityOrderAsc>(ascending);

        foreach (var order in new[] { descending, ascending })
        {
            var scores = ((SimilarityOrderBase)order).Scores;
            scores[low.Id] = 0.1f;
            scores[high.Id] = 0.9f;
            scores[mid.Id] = 0.5f;
        }

        Assert.Equal(["High", "Mid", "Low"], descending.OrderBy(items).Select(i => i.Name));
        Assert.Equal(["Low", "Mid", "High"], ascending.OrderBy(items).Select(i => i.Name));

        // ApplySortingCore does not read the class name - it asks IsDescendingOrder.
        Assert.True(IsDescendingOrder(descending));
        Assert.False(IsDescendingOrder(ascending));
    }

    // ---------------------------------------------------------------------------------------
    // Cross-file contract: what the config UI offers must exist in the registry
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// SORT_OPTIONS values from Configuration/config-core.js, read out of the embedded resource in
    /// the built plugin assembly so the test sees exactly what ships.
    /// </summary>
    private static IReadOnlyList<string> UiSortOptionValues()
    {
        var js = ReadEmbeddedConfigCoreJs();
        var block = Regex.Match(js, @"SmartLists\.SORT_OPTIONS\s*=\s*\[(.*?)\];", RegexOptions.Singleline);
        Assert.True(block.Success, "Could not locate SmartLists.SORT_OPTIONS in config-core.js.");

        var values = Regex.Matches(block.Groups[1].Value, @"value:\s*'([^']+)'")
            .Select(m => m.Groups[1].Value)
            .ToList();

        // Guard against a silently-broken parse producing a vacuously green test.
        Assert.True(values.Count >= 20, $"Parsed only {values.Count} SORT_OPTIONS entries - the parse is broken.");
        Assert.Contains("Name", values);
        Assert.Contains("Random", values);
        return values;
    }

    private static IReadOnlyList<string> UiOrderlessSortValues()
    {
        var js = ReadEmbeddedConfigCoreJs();
        var block = Regex.Match(js, @"SmartLists\.ORDERLESS_SORTS\s*=\s*\[(.*?)\];", RegexOptions.Singleline);
        Assert.True(block.Success, "Could not locate SmartLists.ORDERLESS_SORTS in config-core.js.");

        var values = Regex.Matches(block.Groups[1].Value, @"'([^']+)'")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(values);
        return values;
    }

    private static string ReadEmbeddedConfigCoreJs()
    {
        const string resourceName = "Jellyfin.Plugin.SmartLists.Configuration.config-core.js";
        using var stream = typeof(OrderFactory).Assembly.GetManifestResourceStream(resourceName);
        Assert.True(stream is not null, $"Embedded resource '{resourceName}' not found in the plugin assembly.");
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Reproduces SmartList.InitializeFromDto's name composition for a UI sort value.
    /// </summary>
    private static string ComposeOrderName(string sortBy, SortOrder sortOrder) =>
        OrderFactory.IsDirectionless(sortBy) ? sortBy : $"{sortBy} {sortOrder}";

    /// <summary>
    /// Every sort the admin/user UI offers must land on a real registered order. There are no
    /// carve-outs: "Resolution" used to be the one known unbacked option and is now registered,
    /// so any NEW unbacked option fails here.
    /// </summary>
    [Fact]
    public void EverySortOptionOfferedByTheConfigUi_ResolvesToARegisteredOrder()
    {
        var map = OrderMap();
        var unbacked = new List<string>();

        foreach (var sortBy in UiSortOptionValues())
        {
            foreach (var direction in new[] { SortOrder.Ascending, SortOrder.Descending })
            {
                var composed = ComposeOrderName(sortBy, direction);
                if (!map.ContainsKey(composed))
                {
                    unbacked.Add($"{sortBy} ({direction}) -> '{composed}' is not in OrderMap");
                }
            }
        }

        Assert.Empty(unbacked);
    }

    /// <summary>
    /// OrderFactory.DirectionlessOrders carries an in-code comment saying it mirrors ORDERLESS_SORTS
    /// in config-core.js. If the two drift, the UI hides the direction toggle for a sort the backend
    /// then looks up with a direction appended (or vice versa) - a guaranteed NoOrder fallback.
    /// </summary>
    [Fact]
    public void ConfigUiOrderlessSorts_MatchOrderFactoryDirectionlessOrders()
    {
        var uiOrderless = UiOrderlessSortValues().ToHashSet(StringComparer.Ordinal);

        var field = typeof(OrderFactory).GetField("DirectionlessOrders", NonPublicStatic);
        Assert.NotNull(field);
        var backendOrderless = (HashSet<string>)field!.GetValue(null)!;

        Assert.Empty(uiOrderless.Except(backendOrderless, StringComparer.Ordinal));
        Assert.Empty(backendOrderless.Except(uiOrderless, StringComparer.Ordinal));
    }

    /// <summary>
    /// REGRESSION TEST for a fixed bug.
    ///
    /// config-core.js SORT_OPTIONS offers { value: 'Resolution', label: 'Resolution' } and
    /// docs/content/user-guide/sorting-and-limits.md documents it ("Sort by video resolution
    /// (e.g., 480p, 720p, 1080p, 4K)"), but there used to be no ResolutionOrder class and no
    /// "Resolution Ascending"/"Resolution Descending" entry in OrderFactory.OrderMap.
    ///
    /// The old consequence, traced through SmartList.InitializeFromDto:
    ///   CreateOrder("Resolution Ascending") -> NoOrder
    ///   -> Orders == [NoOrder] -> ResolveDefaultOrder() rewrites it to NameOrder
    ///   -> the list silently sorted by Name Ascending with no warning anywhere.
    /// Nothing validates SortBy on the API side either, so the misconfiguration was unreachable
    /// by any error path - hence this explicit pin.
    ///
    /// (A ChannelResolutionOrder exists on the unmerged new/live_tv branch, but that is a
    /// different field - it does not back this option.)
    /// </summary>
    [Fact]
    public void CreateOrder_ResolutionSortOfferedByTheUi_ShouldResolveToAResolutionOrder()
    {
        Assert.Contains("Resolution", UiSortOptionValues());

        var ascending = OrderFactory.CreateOrder(ComposeOrderName("Resolution", SortOrder.Ascending));
        var descending = OrderFactory.CreateOrder(ComposeOrderName("Resolution", SortOrder.Descending));

        Assert.IsNotType<NoOrder>(ascending);
        Assert.IsNotType<NoOrder>(descending);
        Assert.Equal("Resolution Ascending", ascending.Name);
        Assert.Equal("Resolution Descending", descending.Name);
        Assert.True(IsDescendingOrder(descending));
    }

    // ---------------------------------------------------------------------------------------
    // Order base class defaults (what every unoverridden order inherits)
    // ---------------------------------------------------------------------------------------

    /// <summary>Overrides nothing but Name, so it exercises the base class defaults verbatim.</summary>
    private sealed class BareOrder : Order
    {
        public override string Name => "Bare";
    }

    /// <summary>Overrides only the single-argument OrderBy, to prove the 5-arg overload delegates to it.</summary>
    private sealed class DelegationProbeOrder : Order
    {
        public override string Name => "Probe";

        public int SimpleOverloadCalls { get; private set; }

        public override IEnumerable<BaseItem> OrderBy(IEnumerable<BaseItem> items)
        {
            SimpleOverloadCalls++;
            return items.Reverse();
        }
    }

    [Fact]
    public void Order_OrderBy_DefaultImplementationIsAPassthrough()
    {
        var a = MovieNamed("A");
        var b = MovieNamed("B");
        var c = MovieNamed("C");
        var input = new List<BaseItem> { c, a, b };

        var result = new BareOrder().OrderBy(input).ToList();

        Assert.Equal([c, a, b], result);
    }

    [Fact]
    public void Order_OrderBy_NullItems_ReturnsEmptyRatherThanThrowing()
    {
        Assert.Empty(new BareOrder().OrderBy(null!));
    }

    [Fact]
    public void Order_OrderByWithUserContext_DelegatesToTheSingleArgumentOverload()
    {
        var probe = new DelegationProbeOrder();
        var a = MovieNamed("A");
        var b = MovieNamed("B");
        var user = new User("tester", "authProviderId", "pwResetProviderId");

        var result = probe.OrderBy([a, b], user, null, null, null).ToList();

        Assert.Equal(1, probe.SimpleOverloadCalls);
        Assert.Equal([b, a], result);
    }

    [Fact]
    public void Order_GetSortKey_DefaultImplementationFallsBackToItemName()
    {
        var order = new BareOrder();
        var user = new User("tester", "authProviderId", "pwResetProviderId");

        Assert.Equal("The Matrix", order.GetSortKey(MovieNamed("The Matrix"), user, null, null));

        // An item with no Name must collapse to "" rather than returning null, because
        // ApplySortingCore feeds the key straight into OrderBy/ThenBy as an IComparable.
        var unnamed = new Movie { Id = Guid.NewGuid() };
        Assert.Equal("", order.GetSortKey(unnamed, user, null, null));
    }

    /// <summary>
    /// NoOrder inherits every default, so "Default" must leave the upstream sequence exactly as it
    /// found it - it is the identity element the whole ordering pipeline leans on.
    /// </summary>
    [Fact]
    public void NoOrder_PreservesInputOrderExactly()
    {
        var zebra = MovieNamed("Zebra");
        var apple = MovieNamed("Apple");
        var mango = MovieNamed("Mango");
        var input = new List<BaseItem> { zebra, apple, mango };
        var user = new User("tester", "authProviderId", "pwResetProviderId");

        var order = OrderFactory.CreateOrder("NoOrder");

        Assert.Equal([zebra, apple, mango], order.OrderBy(input).ToList());
        Assert.Equal([zebra, apple, mango], order.OrderBy(input, user, null, null, null).ToList());
        Assert.False(IsDescendingOrder(order));
    }
}
