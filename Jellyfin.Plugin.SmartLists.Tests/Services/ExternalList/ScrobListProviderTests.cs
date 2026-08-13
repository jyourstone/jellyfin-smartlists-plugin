using Jellyfin.Plugin.SmartLists.Services.ExternalList;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.ExternalList;

/// <summary>
/// PRECONDITION: unlike the other six providers, ScrobListProvider talks to an admin-configured
/// host rather than a fixed public domain, so CanHandle is a security gate - it must reject any
/// URL whose origin differs from the configured server, or a non-admin user could point a rule
/// (and the admin's API key) at an arbitrary host.
///
/// Plugin.Instance is never constructed in a test process, so CanHandle itself can only be
/// observed in its unconfigured state. The comparison it delegates to takes the configured URL
/// as a parameter, which is what makes the gate itself testable; both it and the list-ID path
/// parser are internal, reached here through the InternalsVisibleTo wiring in
/// Jellyfin.Plugin.SmartLists.csproj.
/// </summary>
public class ScrobListProviderTests
{
    // ---------------------------------------------------------------------------------
    // MatchesConfiguredServer - the security gate: origin must match exactly
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://scrob.example.com/list/12", "https://scrob.example.com", true)]
    [InlineData("https://scrob.example.com/list/12", "https://scrob.example.com/", true)]      // trailing slash on config
    [InlineData("https://SCROB.EXAMPLE.COM/list/12", "https://scrob.example.com", true)]       // host compare is case-insensitive
    [InlineData("http://192.168.1.50:7330/list/12", "http://192.168.1.50:7330", true)]         // LAN address with explicit port
    [InlineData("https://scrob.example.com/api/proxy/lists/12", "https://scrob.example.com", true)]
    [InlineData("http://scrob.example.com/list/12", "https://scrob.example.com", false)]       // scheme differs
    [InlineData("https://scrob.example.com:8443/list/12", "https://scrob.example.com", false)] // port differs
    [InlineData("https://evil.example.com/list/12", "https://scrob.example.com", false)]       // different host
    [InlineData("https://sub.scrob.example.com/list/12", "https://scrob.example.com", false)]  // subdomain is not the host
    [InlineData("https://scrob.example.com.evil.test/list/12", "https://scrob.example.com", false)] // suffix attack
    [InlineData("ftp://scrob.example.com/list/12", "ftp://scrob.example.com", false)]          // non-http scheme
    [InlineData("not-a-url", "https://scrob.example.com", false)]
    [InlineData("", "https://scrob.example.com", false)]
    [InlineData("https://scrob.example.com/list/12", null, false)]                             // server not configured
    [InlineData("https://scrob.example.com/list/12", "", false)]
    [InlineData("https://scrob.example.com/list/12", "scrob.example.com", false)]              // configured value lacks a scheme
    public void MatchesConfiguredServer_AcceptsOnlyTheConfiguredOrigin(string url, string? configuredServerUrl, bool expected)
    {
        Assert.Equal(expected, ScrobListProvider.MatchesConfiguredServer(url, configuredServerUrl));
    }

    [Fact]
    public void CanHandle_RejectsEverythingWhenServerNotConfigured()
    {
        // Fail closed: with no Plugin.Instance there is no configured server to match against.
        Assert.Null(Plugin.Instance);

        var provider = new ScrobListProvider(null!, NullLogger<ScrobListProvider>.Instance); // CanHandle issues no HTTP

        Assert.False(provider.CanHandle("https://scrob.example.com/list/12"));
    }

    // ---------------------------------------------------------------------------------
    // ParseListId - extracts the list ID from the accepted path forms
    // ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://scrob.example/list/12", 12)]              // browser-copied form
    [InlineData("https://scrob.example/list/12/", 12)]              // trailing slash tolerated
    [InlineData("https://scrob.example/lists/12", 12)]              // tolerated alias
    [InlineData("https://scrob.example/api/proxy/lists/12", 12)]    // raw proxy form
    [InlineData("https://scrob.example/API/PROXY/LISTS/12", 12)]    // path match is case-insensitive
    [InlineData("https://scrob.example/scrob/list/12", 12)]         // reverse-proxy sub-path deployment
    [InlineData("https://scrob.example/list/0", null)]              // not a positive integer
    [InlineData("https://scrob.example/list/-1", null)]             // not a positive integer
    [InlineData("https://scrob.example/list/abc", null)]            // not numeric
    [InlineData("https://scrob.example/list/", null)]               // missing id
    [InlineData("https://scrob.example/movie/12", null)]            // wrong path segment
    [InlineData("https://scrob.example/mylist/12", null)]           // segment must be exactly list/lists
    [InlineData("https://scrob.example/list/12/extra", null)]       // trailing extra segment
    [InlineData("not-a-url", null)]
    public void ParseListId_ExtractsIdFromSupportedPathForms(string url, int? expected)
    {
        Assert.Equal(expected, ScrobListProvider.ParseListId(url));
    }
}
