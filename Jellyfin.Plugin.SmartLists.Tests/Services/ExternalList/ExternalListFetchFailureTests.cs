using Jellyfin.Plugin.SmartLists.Services.ExternalList;
using Jellyfin.Plugin.SmartLists.Services.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.SmartLists.Tests.Services.ExternalList;

/// <summary>
/// Issue #555: a list that cannot be fetched used to count as empty, so every playlist using it was written
/// with nothing from it. PreFetchListsAsync now reports failed URLs - for the whole refresh drain, since the
/// failure is cached - and the playlist and collection services leave those lists unchanged.
/// </summary>
public class ExternalListFetchFailureTests
{
    private const string Failing = "https://example.test/list/down";
    private const string Working = "https://example.test/list/up";

    private sealed class FakeProvider : IExternalListProvider
    {
        public int Calls { get; private set; }

        public bool CanHandle(string url) => url.StartsWith("https://example.test/", StringComparison.OrdinalIgnoreCase);

        public Task<ExternalListResult> FetchListAsync(string url, CancellationToken cancellationToken, int maxItems = 0)
        {
            Calls++;
            return url == Failing
                ? throw new HttpRequestException("Name or service not known")
                : Task.FromResult(new ExternalListResult());
        }
    }

    [Fact]
    public async Task FailedFetch_IsReportedForEveryListInTheDrain()
    {
        var provider = new FakeProvider();
        var service = new ExternalListService([provider], NullLogger<ExternalListService>.Instance);
        var cache = new RefreshQueueService.RefreshCache();

        Assert.Equal([Failing], await service.PreFetchListsAsync([Failing, Working], cache, CancellationToken.None));

        // A second list using the same URL (any case) in the same drain: still failed, without fetching again
        var sameUrl = Failing.ToUpperInvariant();
        Assert.Equal([sameUrl], await service.PreFetchListsAsync([sameUrl], cache, CancellationToken.None));
        Assert.Equal(2, provider.Calls);

        Assert.Empty(await service.PreFetchListsAsync([Working], cache, CancellationToken.None));
    }

    [Fact]
    public async Task UnsupportedUrl_IsNotReportedAsFailed()
    {
        var service = new ExternalListService([new FakeProvider()], NullLogger<ExternalListService>.Instance);

        Assert.Empty(await service.PreFetchListsAsync(["https://unsupported.test/list/1"], new RefreshQueueService.RefreshCache(), CancellationToken.None));
    }
}
