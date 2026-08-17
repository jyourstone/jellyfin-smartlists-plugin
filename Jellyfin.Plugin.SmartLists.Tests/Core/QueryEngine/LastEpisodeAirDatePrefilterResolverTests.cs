using Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Covers the pure logic of <see cref="LastEpisodeAirDatePrefilterResolver"/>: the
/// operator-to-query-plan mapping (including the unknown-dates gate and the moving-cutoff
/// margin for OlderThan), the episode-to-series attribution, and the complement set math.
/// Its contract:
///
/// - After/NewerThan ride directly (candidates = series with an episode at/after the bound),
///   Before/OlderThan ride as an exact complement (pool series MINUS series with an episode
///   at/after the bound), Equal rides as a [day, day+1] superset window.
/// - NotEqual and Weekday need the actual max date, not existence - they never ride.
/// - IncludeUnknownDates = true disables the resolver entirely: the compiled rule then
///   matches every dateless operand, which no date range can bound.
/// - Value parsing is EXACTLY the per-item semantics (strict yyyy-MM-dd absolute dates,
///   number:unit relative values); anything the compiled rule would reject stays per-item.
/// - Relative bounds are floored to whole unix seconds, mirroring the ToUnixTimeSeconds
///   truncation on both sides of the per-item comparison.
///
/// The GetCount/GetItemList steps need a live Jellyfin and are exercised there, not here.
/// </summary>
public class LastEpisodeAirDatePrefilterResolverTests
{
    private static readonly DateTime FixedUtcNow = new(2026, 8, 16, 10, 30, 45, 500, DateTimeKind.Utc);

    // ---- Unknown-dates gate ----

    [Theory]
    [InlineData("After", "2024-06-15")]
    [InlineData("NewerThan", "3:days")]
    [InlineData("Before", "2024-06-15")]
    [InlineData("OlderThan", "2:years")]
    [InlineData("Equal", "2024-06-15")]
    public void IncludeUnknownDates_DisablesEveryOperator(string ruleOperator, string targetValue)
    {
        Assert.Null(LastEpisodeAirDatePrefilterResolver.TryBuildPlan(ruleOperator, targetValue, includeUnknownDates: true, FixedUtcNow));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void DefaultUnknownDates_Rides(bool? includeUnknownDates)
    {
        Assert.NotNull(LastEpisodeAirDatePrefilterResolver.TryBuildPlan("After", "2024-06-15", includeUnknownDates, FixedUtcNow));
    }

    // ---- Operator -> query-plan mapping ----

    [Fact]
    public void After_MapsToDirectLowerBound()
    {
        var plan = LastEpisodeAirDatePrefilterResolver.TryBuildPlan("After", "2024-06-15", null, FixedUtcNow);

        Assert.NotNull(plan);
        Assert.Equal(new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc), plan.MinPremiereDate);
        Assert.Null(plan.MaxPremiereDate);
        Assert.False(plan.IsComplement);
    }

    [Fact]
    public void Before_MapsToComplementLowerBound()
    {
        var plan = LastEpisodeAirDatePrefilterResolver.TryBuildPlan("Before", "2024-06-15", null, FixedUtcNow);

        Assert.NotNull(plan);
        Assert.Equal(new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc), plan.MinPremiereDate);
        Assert.Null(plan.MaxPremiereDate);
        Assert.True(plan.IsComplement);
    }

    [Fact]
    public void Equal_MapsToDayWindow()
    {
        var plan = LastEpisodeAirDatePrefilterResolver.TryBuildPlan("Equal", "2024-06-15", null, FixedUtcNow);

        Assert.NotNull(plan);
        Assert.Equal(new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc), plan.MinPremiereDate);
        Assert.Equal(new DateTime(2024, 6, 16, 0, 0, 0, DateTimeKind.Utc), plan.MaxPremiereDate);
        Assert.False(plan.IsComplement);
    }

    [Fact]
    public void NewerThan_UsesCutoffFlooredToWholeSeconds()
    {
        var plan = LastEpisodeAirDatePrefilterResolver.TryBuildPlan("NewerThan", "3:days", null, FixedUtcNow);

        Assert.NotNull(plan);
        // utcNow - 3 days, sub-second component truncated.
        Assert.Equal(new DateTime(2026, 8, 13, 10, 30, 45, DateTimeKind.Utc), plan.MinPremiereDate);
        Assert.Null(plan.MaxPremiereDate);
        Assert.False(plan.IsComplement);
    }

    [Theory]
    [InlineData("6:hours", 2026, 8, 16, 4, 30, 45)]
    [InlineData("2:weeks", 2026, 8, 2, 10, 30, 45)]
    [InlineData("1:months", 2026, 7, 16, 10, 30, 45)]
    [InlineData("1:years", 2025, 8, 16, 10, 30, 45)]
    [InlineData("3:DAYS", 2026, 8, 13, 10, 30, 45)] // unit is lower-cased, mirroring the engine
    public void NewerThan_UnitTable_MirrorsEngine(string targetValue, int year, int month, int day, int hour, int minute, int second)
    {
        var plan = LastEpisodeAirDatePrefilterResolver.TryBuildPlan("NewerThan", targetValue, null, FixedUtcNow);

        Assert.NotNull(plan);
        Assert.Equal(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc), plan.MinPremiereDate);
    }

    [Fact]
    public void NewerThan_MonthClamping_MirrorsEngine()
    {
        var endOfMonth = new DateTime(2026, 3, 31, 12, 0, 0, DateTimeKind.Utc);
        var plan = LastEpisodeAirDatePrefilterResolver.TryBuildPlan("NewerThan", "1:months", null, endOfMonth);

        Assert.NotNull(plan);
        Assert.Equal(new DateTime(2026, 2, 28, 12, 0, 0, DateTimeKind.Utc), plan.MinPremiereDate);
    }

    [Fact]
    public void OlderThan_MapsToComplementWithSafetyMargin()
    {
        var plan = LastEpisodeAirDatePrefilterResolver.TryBuildPlan("OlderThan", "2:years", null, FixedUtcNow);

        Assert.NotNull(plan);
        // The per-item cutoff is recomputed from UtcNow at every evaluation, so the exclusion
        // threshold carries the margin: cutoff-at-build-time + margin, floored to seconds.
        var expected = new DateTime(2024, 8, 16, 10, 30, 45, DateTimeKind.Utc)
            + LastEpisodeAirDatePrefilterResolver.OlderThanComplementMargin;
        Assert.Equal(expected, plan.MinPremiereDate);
        Assert.Null(plan.MaxPremiereDate);
        Assert.True(plan.IsComplement);
    }

    // ---- Non-riding operators ----

    [Theory]
    [InlineData("NotEqual", "2024-06-15")] // day-window negation needs the actual max
    [InlineData("Weekday", "3")] // day-of-week OF the max needs the actual max
    [InlineData("GreaterThan", "2024-06-15")]
    [InlineData("Contains", "2024")]
    [InlineData("after", "2024-06-15")] // operator comparison is Ordinal, mirroring the engine switch
    [InlineData("", "2024-06-15")]
    [InlineData(null, "2024-06-15")]
    public void NonRidingOperators_StayPerItem(string? ruleOperator, string targetValue)
    {
        Assert.Null(LastEpisodeAirDatePrefilterResolver.TryBuildPlan(ruleOperator, targetValue, null, FixedUtcNow));
    }

    // ---- Values the compiled rule would reject ----

    [Theory]
    [InlineData("After", "junk")]
    [InlineData("After", "2024-6-15")] // strict yyyy-MM-dd only
    [InlineData("After", "15-06-2024")]
    [InlineData("After", "")]
    [InlineData("After", null)]
    [InlineData("Equal", "2024-06-15T00:00:00")]
    [InlineData("NewerThan", "3days")]
    [InlineData("NewerThan", "-1:days")]
    [InlineData("NewerThan", "3:fortnights")]
    [InlineData("NewerThan", ":days")]
    [InlineData("NewerThan", "3:")]
    [InlineData("NewerThan", "3:days:extra")]
    [InlineData("NewerThan", null)]
    [InlineData("OlderThan", "2 years")]
    public void UnparsableValues_StayPerItem(string ruleOperator, string? targetValue)
    {
        Assert.Null(LastEpisodeAirDatePrefilterResolver.TryBuildPlan(ruleOperator, targetValue, null, FixedUtcNow));
    }

    // ---- Episode -> series attribution ----

    [Fact]
    public void MapToSeriesIds_CollapsesToDistinctSeries()
    {
        var seriesA = Guid.NewGuid();
        var seriesB = Guid.NewGuid();
        var episodes = new BaseItem[]
        {
            new Episode { Id = Guid.NewGuid(), SeriesId = seriesA },
            new Episode { Id = Guid.NewGuid(), SeriesId = seriesA },
            new Episode { Id = Guid.NewGuid(), SeriesId = seriesB },
        };

        var result = LastEpisodeAirDatePrefilterResolver.MapToSeriesIds(episodes);

        Assert.Equal(new HashSet<Guid> { seriesA, seriesB }, result);
    }

    [Fact]
    public void MapToSeriesIds_SkipsUnattributableAndNonEpisodes()
    {
        var seriesA = Guid.NewGuid();
        var episodes = new BaseItem[]
        {
            new Episode { Id = Guid.NewGuid(), SeriesId = seriesA },
            // No SeriesId and no parent chain: FindSeriesId() yields Guid.Empty - the
            // per-series extraction cannot see this episode either.
            new Episode { Id = Guid.NewGuid() },
            new Movie { Id = Guid.NewGuid() },
        };

        var result = LastEpisodeAirDatePrefilterResolver.MapToSeriesIds(episodes);

        Assert.Equal(new HashSet<Guid> { seriesA }, result);
    }

    // ---- Complement set math ----

    [Fact]
    public void Complement_IsPoolSeriesMinusExcluded()
    {
        var recentSeries = new Series { Id = Guid.NewGuid() };
        var oldSeries = new Series { Id = Guid.NewGuid() };
        var datelessSeries = new Series { Id = Guid.NewGuid() };
        var movie = new Movie { Id = Guid.NewGuid() };
        var pool = new BaseItem[] { recentSeries, oldSeries, datelessSeries, movie };

        var excluded = new HashSet<Guid> { recentSeries.Id };

        var result = LastEpisodeAirDatePrefilterResolver.ComplementOverPoolSeries(pool, excluded);

        // Series with no episode at/after the bound stay candidates - including dateless
        // series, whose 0 sentinel the final per-item evaluation still rejects. Non-Series
        // pool items can never match the rule under the unknown-dates gate.
        Assert.Equal(new HashSet<Guid> { oldSeries.Id, datelessSeries.Id }, result);
    }

    [Fact]
    public void Complement_AllPoolSeriesExcluded_IsEmptyHardClaim()
    {
        var series = new Series { Id = Guid.NewGuid() };
        var pool = new BaseItem[] { series, new Movie { Id = Guid.NewGuid() } };

        var result = LastEpisodeAirDatePrefilterResolver.ComplementOverPoolSeries(pool, [series.Id]);

        Assert.Empty(result);
    }
}
