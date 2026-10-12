using Jellyfin.Plugin.SmartLists.Services.ExternalList;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.ExternalList;

/// <summary>
/// Issue #544: ExternalListService keeps the last successful fetch of each URL in memory so the
/// Missing items view can show what the list held without fetching it again.
/// </summary>
public class ExternalListServiceSnapshotTests
{
    private const string Url = "https://example.test/list/1";

    private sealed class FakeProvider(Func<string, ExternalListResult> fetch) : IExternalListProvider
    {
        public bool CanHandle(string url) => url.StartsWith("https://example.test/", StringComparison.OrdinalIgnoreCase);

        public Task<ExternalListResult> FetchListAsync(string url, CancellationToken cancellationToken, int maxItems = 0)
            => Task.FromResult(fetch(url));
    }

    private static ExternalListService Service(Func<string, ExternalListResult> fetch)
        => new([new FakeProvider(url => fetch(url))], NullLogger<ExternalListService>.Instance);

    private static Task Drain(ExternalListService service, string url)
        => service.PreFetchListsAsync([url], new RefreshQueueService.RefreshCache(), CancellationToken.None);

    [Fact]
    public async Task SuccessfulFetch_StoresASnapshot()
    {
        var fetched = new ExternalListResult();
        var service = Service(_ => fetched);

        var before = DateTime.UtcNow;
        await Drain(service, Url);
        var after = DateTime.UtcNow;

        // The lookup ignores case
        var snapshot = service.GetSnapshot(Url.ToUpperInvariant());
        Assert.NotNull(snapshot);
        Assert.Same(fetched, snapshot.Result);
        Assert.Equal(DateTimeKind.Utc, snapshot.FetchedAtUtc.Kind);
        Assert.InRange(snapshot.FetchedAtUtc, before, after);
    }

    [Fact]
    public async Task FailedFetch_KeepsThePreviousSnapshot()
    {
        var first = new ExternalListResult();
        Func<string, ExternalListResult> fetch = _ => first;
        var service = Service(url => fetch(url));
        await Drain(service, Url);
        var original = service.GetSnapshot(Url);

        // A later drain (fresh refresh cache) where the provider throws.
        fetch = _ => throw new InvalidOperationException("boom");
        await Drain(service, Url);

        Assert.Same(original, service.GetSnapshot(Url));
    }

    [Fact]
    public async Task UnsupportedOrNeverFetchedUrl_HasNoSnapshot()
    {
        var service = Service(_ => new ExternalListResult());

        await Drain(service, "https://unsupported.test/list/1");
        await Drain(service, Url);

        Assert.Null(service.GetSnapshot("https://unsupported.test/list/1"));
        Assert.Null(service.GetSnapshot("https://example.test/list/2"));
    }
}
