using Jellyfin.Plugin.SmartLists.Core.QueryEngine.Prefilters;

namespace Jellyfin.Plugin.SmartLists.Tests.Core.QueryEngine;

/// <summary>
/// Covers the pure code-resolution step of <see cref="StreamLanguagePrefilterResolver"/> -
/// the piece that decides which RAW stored language codes a language rule matches before
/// the single item query runs. Its contract:
///
/// - Matching runs against each raw code's ISO 639-2 B-to-T NORMALIZED form (the form
///   per-item extraction sees, because the server's stream read path rewrites B codes to
///   T codes), while the RETURNED codes are the raw stored forms (the item query compares
///   the stored Language column byte-exact). The canonical case: a track stored as 'ger'
///   must ride a rule "Equal deu", and a rule "Equal ger" must match NOTHING (per-item
///   lists never contain 'ger').
/// - Operator semantics must be EXACTLY the plugin's per-item semantics (it delegates to
///   the same Engine helpers the compiled rules bind): Equal is whole-value
///   OrdinalIgnoreCase, Contains is substring OrdinalIgnoreCase, IsIn is a
///   semicolon-separated substring list, MatchRegex is case-sensitive.
/// - A pattern that matches the empty string never rides (an empty stream-language list
///   is evaluated against "", so such patterns match items with NO such streams -
///   unreachable from any code-derived set), nor does an invalid pattern or a
///   negative/unknown operator.
/// - Null/empty raw codes are skipped (the dump maps null/empty stored languages to
///   'und'; per-item extraction drops them).
/// - An empty (non-null) result is a hard "no stored code matches" claim.
///
/// The dump and GetItemIds steps need a live Jellyfin 12 and are exercised there, not here.
/// </summary>
public class StreamLanguagePrefilterResolverTests
{
    /// <summary>
    /// Raw stored codes as a real mixed library dumps them: Matroska B codes ('ger',
    /// 'fre'), T codes ('deu'), plain codes with no B/T split ('eng', 'und').
    /// </summary>
    private static readonly string[] DefaultCodes = ["ger", "eng", "fre", "und", "deu"];

    /// <summary>
    /// Fake of the server's ISO 639-2 B-to-T mapping, mirroring
    /// ILocalizationManager.TryGetISO6392TFromB for the fixture codes.
    /// </summary>
    private static bool FakeNormalize(string rawCode, out string? normalized)
    {
        switch (rawCode)
        {
            case "ger":
                normalized = "deu";
                return true;
            case "fre":
                normalized = "fra";
                return true;
            default:
                normalized = null;
                return false;
        }
    }

    // ---- Equal (the B-to-T normalization contract) ----

    [Fact]
    public void Equal_TCode_MatchesBothStoredSpellings()
    {
        // 'ger' normalizes to 'deu' and rides ALONGSIDE the natively stored 'deu' -
        // dropping either raw form would drop true matches.
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, "Equal", "deu", FakeNormalize);

        Assert.NotNull(matched);
        Assert.Equal(["ger", "deu"], matched);
    }

    [Fact]
    public void Equal_BCode_MatchesNothing()
    {
        // Per-item extraction never sees 'ger' (the read path rewrote it to 'deu'), so an
        // "Equal ger" rule matches no item - a hard empty claim, not a fallback.
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, "Equal", "ger", FakeNormalize);

        Assert.NotNull(matched);
        Assert.Empty(matched);
    }

    [Fact]
    public void Equal_IsCaseInsensitive()
    {
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, "Equal", "DEU", FakeNormalize);

        Assert.Equal(["ger", "deu"], matched);
    }

    [Fact]
    public void Equal_DoesNotSubstringMatch()
    {
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, "Equal", "de", FakeNormalize);

        Assert.NotNull(matched);
        Assert.Empty(matched);
    }

    [Fact]
    public void Equal_UnmappedCodePassesThroughRaw()
    {
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, "Equal", "eng", FakeNormalize);

        Assert.Equal(["eng"], matched);
    }

    // ---- Contains ----

    [Fact]
    public void Contains_SubstringCaseInsensitive_AgainstNormalizedForm()
    {
        // 'DE' is a substring of normalized 'deu' (from raw 'ger' and raw 'deu') only.
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, "Contains", "DE", FakeNormalize);

        Assert.Equal(["ger", "deu"], matched);
    }

    [Fact]
    public void Contains_DoesNotMatchRawFormOfMappedCode()
    {
        // 'fre' normalizes to 'fra'; nothing normalized contains 'fre'.
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, "Contains", "fre", FakeNormalize);

        Assert.NotNull(matched);
        Assert.Empty(matched);
    }

    // ---- IsIn ----

    [Fact]
    public void IsIn_SemicolonSeparatedSubstrings()
    {
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, "IsIn", "deu; eng", FakeNormalize);

        Assert.Equal(["ger", "eng", "deu"], matched);
    }

    // ---- MatchRegex ----

    [Fact]
    public void MatchRegex_CaseSensitive_AgainstNormalizedForm()
    {
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, "MatchRegex", "^de", FakeNormalize);

        Assert.Equal(["ger", "deu"], matched);
    }

    [Fact]
    public void MatchRegex_CaseSensitivityIsPreserved()
    {
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, "MatchRegex", "^DE", FakeNormalize);

        Assert.NotNull(matched);
        Assert.Empty(matched);
    }

    [Fact]
    public void MatchRegex_EmptyMatchingPattern_DoesNotRide()
    {
        // ".*" matches "" - it would match items with no streams of the kind at all.
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, "MatchRegex", ".*", FakeNormalize);

        Assert.Null(matched);
    }

    [Fact]
    public void MatchRegex_InvalidPattern_DoesNotRide()
    {
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, "MatchRegex", "[unclosed", FakeNormalize);

        Assert.Null(matched);
    }

    // ---- Operators that never ride ----

    [Theory]
    [InlineData("NotEqual")]
    [InlineData("NotContains")]
    [InlineData("IsNotIn")]
    [InlineData("GreaterThan")]
    [InlineData("SomethingUnknown")]
    public void UnsupportedOperators_DoNotRide(string ruleOperator)
    {
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, ruleOperator, "deu", FakeNormalize);

        Assert.Null(matched);
    }

    // ---- Dump-code hygiene ----

    [Fact]
    public void NullAndEmptyRawCodes_AreSkipped()
    {
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes([null!, string.Empty, "deu"], "Equal", "deu", FakeNormalize);

        Assert.Equal(["deu"], matched);
    }

    [Fact]
    public void NormalizerReturningTrueWithEmptyOutput_FallsBackToRawCode()
    {
        // Defensive: a normalizer that claims success but yields nothing must not erase
        // the code (that could drop true matches for the raw form).
        static bool BadNormalize(string rawCode, out string? normalized)
        {
            normalized = string.Empty;
            return true;
        }

        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(["deu"], "Equal", "deu", BadNormalize);

        Assert.Equal(["deu"], matched);
    }

    [Fact]
    public void NoMatch_IsAHardEmptyClaim()
    {
        var matched = StreamLanguagePrefilterResolver.ResolveMatchingRawCodes(DefaultCodes, "Equal", "jpn", FakeNormalize);

        Assert.NotNull(matched);
        Assert.Empty(matched);
    }
}
