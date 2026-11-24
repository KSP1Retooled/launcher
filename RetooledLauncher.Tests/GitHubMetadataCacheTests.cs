using System.Net;
using System.Net.Http.Headers;
using KSP.Retooled.Launcher.Exceptions;
using KSP.Retooled.Launcher.Releases;
using KSP.Retooled.Launcher.Releases.GitHub;
using KSP.Retooled.Launcher.Tests.Mocking;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

public sealed class GitHubMetadataCacheTests : IDisposable
{
    private const string PAGE1 = "https://api.github.com/repos/o/r/releases?per_page=100";
    private const string PAGE2 = PAGE1 + "&page=2";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "retooled-metadata-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(this.directory)) Directory.Delete(this.directory, true);
    }

    [Fact]
    public async Task FreshMetadataIsSharedAcrossRegularAndDebugChannels()
    {
        FakeHttp http = new();
        http.Json(PAGE1, """[{"tag_name":"v1"}]""");
        HttpClient client = new(http);
        IReadOnlyList<Release> normal = await new GitHubReleaseSource(client, "o/r").GetReleasesAsync();
        IReadOnlyList<Release> debug = await new GitHubReleaseSource(client, "o/r#debug").GetReleasesAsync();
        Assert.Single(http.requests);
        Assert.Equal("o/r", Assert.Single(normal).Channel);
        Assert.Equal("o/r#debug", Assert.Single(debug).Channel);
    }

    [Fact]
    public async Task RevalidatesEveryPageAndKeepsPaginationWhenNotModified()
    {
        FakeHttp http = new();
        http.routes[PAGE1] = () => Page("v2", "first", PAGE2);
        http.routes[PAGE2] = () => Page("v1", "second");
        HttpClient client = new(http);
        GitHubMetadataCache cache = new() { Freshness = TimeSpan.Zero };
        GitHubReleaseSource source = new(client, "o/r", cache: cache);
        await source.GetReleasesAsync();
        http.routes[PAGE1] = http.routes[PAGE2] = () => new HttpResponseMessage(HttpStatusCode.NotModified);
        IReadOnlyList<Release> releases = await source.GetReleasesAsync();
        Assert.Equal(["v2", "v1"], releases.Select(r => r.Tag));
        Assert.Equal("\"first\"", http.requests[2].Headers.IfNoneMatch.Single().ToString());
        Assert.Equal("\"second\"", http.requests[3].Headers.IfNoneMatch.Single().ToString());
    }

    [Fact]
    public async Task UpdatedPaginationDropsOldPages()
    {
        FakeHttp http = new();
        http.routes[PAGE1] = () => Page("v2", "old", PAGE2);
        http.routes[PAGE2] = () => Page("v1", "second");
        HttpClient client = new(http);
        GitHubReleaseSource source = new(client, "o/r", cache: new GitHubMetadataCache { Freshness = TimeSpan.Zero });
        await source.GetReleasesAsync();
        http.routes[PAGE1] = () => Page("v3", "new");
        Assert.Equal("v3", Assert.Single(await source.GetReleasesAsync()).Tag);
        Assert.Equal(3, http.requests.Count);
    }

    [Fact]
    public async Task AnonymousMetadataSurvivesANewCacheAndIsRevalidated()
    {
        FakeHttp http = new();
        http.routes[PAGE1] = () => Page("v1", "persisted");
        HttpClient client = new(http);
        await new GitHubReleaseSource(client, "o/r", cache: new GitHubMetadataCache(this.directory)).GetReleasesAsync();
        http.routes[PAGE1] = () => new HttpResponseMessage(HttpStatusCode.NotModified);
        GitHubMetadataCache restarted = new(this.directory) { Freshness = TimeSpan.Zero };
        Assert.Equal("v1", Assert.Single(await new GitHubReleaseSource(client, "o/r", cache: restarted).GetReleasesAsync()).Tag);
        Assert.Equal("\"persisted\"", http.requests[1].Headers.IfNoneMatch.Single().ToString());
    }

    [Fact]
    public async Task TokensHaveSeparateCachesAndPrivateMetadataIsNotPersisted()
    {
        FakeHttp http = new();
        http.routes[PAGE1] = () => Page("private", "secret");
        HttpClient client = new(http);
        GitHubMetadataCache cache = new(this.directory);
        await new GitHubReleaseSource(client, "o/r", "one", cache).GetReleasesAsync();
        await new GitHubReleaseSource(client, "o/r", "two", cache).GetReleasesAsync();
        Assert.False(Directory.Exists(this.directory));
        http.routes[PAGE1] = () => Page("public", "public");
        IReadOnlyList<Release> releases = await new GitHubReleaseSource(client, "o/r", cache: cache).GetReleasesAsync();
        Assert.Equal("public", Assert.Single(releases).Tag);
        Assert.Equal(3, http.requests.Count);
        Assert.Empty(http.requests[2].Headers.IfNoneMatch);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task CooldownAppliesAcrossRepositoriesAndExpires(bool useReset)
    {
        ManualClock clock = new();
        FakeHttp http = new();
        http.routes[PAGE1] = () =>
        {
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
            if (useReset)
            {
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset", clock.GetUtcNow().AddMinutes(2).ToUnixTimeSeconds().ToString());
            }
            else
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            }

            return response;
        };

        HttpClient client = new(http);
        GitHubMetadataCache cache = new(clock: clock);
        await Assert.ThrowsAsync<LauncherException>(() => new GitHubReleaseSource(client, "o/r", cache: cache).GetReleasesAsync());
        GitHubReleaseSource other = new(client, "other/repo", cache: cache);
        await Assert.ThrowsAsync<LauncherException>(() => other.GetReleasesAsync());
        Assert.Single(http.requests);
        clock.now += TimeSpan.FromMinutes(2);
        http.Json("https://api.github.com/repos/other/repo/releases?per_page=100", "[]");
        Assert.Empty(await other.GetReleasesAsync());
        Assert.Equal(2, http.requests.Count);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneFetchAndCancellingAWaiterKeepsItRunning()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        HttpClient client = new(new AsyncHttp(async (_, cancellation) =>
        {
            Interlocked.Increment(ref requests);
            started.SetResult();
            await finish.Task.WaitAsync(cancellation);
            return Page("v1", "shared");
        }));

        Task<IReadOnlyList<Release>> first = new GitHubReleaseSource(client, "o/r").GetReleasesAsync();
        await started.Task;
        using CancellationTokenSource cancel = new();
        Task<IReadOnlyList<Release>> cancelled = new GitHubReleaseSource(client, "o/r#debug").GetReleasesAsync(cancel.Token);
        // ReSharper disable once MethodSupportsCancellation
        Task<IReadOnlyList<Release>> shared = new GitHubReleaseSource(client, "o/r#debug").GetReleasesAsync();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        finish.SetResult();
        await first;
        Assert.Equal("o/r#debug", Assert.Single(await shared).Channel);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CancellingTheFetchReleasesTheGateForAnotherCaller()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        HttpClient client = new(new AsyncHttp(async (_, cancellation) =>
        {
            if (Interlocked.Increment(ref requests) is 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }

            return Page("v1", "retry");
        }));

        using CancellationTokenSource cancel = new();
        Task<IReadOnlyList<Release>> first = new GitHubReleaseSource(client, "o/r").GetReleasesAsync(cancel.Token);
        await started.Task;
        // ReSharper disable once MethodSupportsCancellation
        Task<IReadOnlyList<Release>> second = new GitHubReleaseSource(client, "o/r").GetReleasesAsync();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Single(await second);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task FailedLaterPageDoesNotCommitAPartialRefresh()
    {
        FakeHttp http = new();
        http.routes[PAGE1] = () => Page("v2", "original", PAGE2);
        http.routes[PAGE2] = () => Page("v1", "second");
        HttpClient client = new(http);
        GitHubReleaseSource source = new(client, "o/r", cache: new GitHubMetadataCache { Freshness = TimeSpan.Zero });
        await source.GetReleasesAsync();
        http.routes[PAGE1] = () => Page("v3", "uncommitted", PAGE2);
        http.routes[PAGE2] = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<LauncherException>(() => source.GetReleasesAsync());
        http.routes[PAGE1] = http.routes[PAGE2] = () => new HttpResponseMessage(HttpStatusCode.NotModified);
        Assert.Equal(["v2", "v1"], (await source.GetReleasesAsync()).Select(r => r.Tag));
        Assert.Equal("\"original\"", http.requests[4].Headers.IfNoneMatch.Single().ToString());
    }

    [Fact]
    public async Task CorruptDiskCacheIsIgnored()
    {
        FakeHttp http = new();
        http.routes[PAGE1] = () => Page("v1", "valid");
        HttpClient client = new(http);
        await new GitHubReleaseSource(client, "o/r", cache: new GitHubMetadataCache(this.directory)).GetReleasesAsync();
        string path = Assert.Single(Directory.GetFiles(this.directory));
        foreach (string corrupt in new[] { "broken json", "{\"Pages\":null}", "{\"Pages\":{\"url\":null}}" })
        {
            await File.WriteAllTextAsync(path, corrupt);
            Assert.Single(await new GitHubReleaseSource(client, "o/r", cache: new GitHubMetadataCache(this.directory)).GetReleasesAsync());
        }

        Assert.Equal(4, http.requests.Count);
    }

    [Fact]
    public async Task MetadataWithoutAnEtagIsFetchedAgainAfterFreshnessExpires()
    {
        ManualClock clock = new();
        FakeHttp http = new();
        http.Json(PAGE1, "[]");
        HttpClient client = new(http);
        GitHubReleaseSource source = new(client, "o/r", cache: new GitHubMetadataCache(clock: clock));
        await source.GetReleasesAsync();
        await source.GetReleasesAsync();
        Assert.Single(http.requests);
        clock.now += TimeSpan.FromMinutes(1);
        await source.GetReleasesAsync();
        Assert.Equal(2, http.requests.Count);
        Assert.Empty(http.requests[1].Headers.IfNoneMatch);
    }

    [Fact]
    public async Task AForbiddenResponseWithoutRetryHeadersGetsABoundedCooldown()
    {
        ManualClock clock = new();
        FakeHttp http = new();
        http.routes[PAGE1] = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        HttpClient client = new(http);
        GitHubReleaseSource source = new(client, "o/r", cache: new GitHubMetadataCache(clock: clock));
        await Assert.ThrowsAsync<LauncherException>(() => source.GetReleasesAsync());
        await Assert.ThrowsAsync<LauncherException>(() => source.GetReleasesAsync());
        Assert.Single(http.requests);
        clock.now += TimeSpan.FromMinutes(1);
        http.Json(PAGE1, "[]");
        Assert.Empty(await source.GetReleasesAsync());
        Assert.Equal(2, http.requests.Count);
    }

    private static HttpResponseMessage Page(string tag, string etag, string? next = null)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"tag_name\":\"" + tag + "\"}]")
        };

        response.Headers.ETag = new EntityTagHeaderValue("\"" + etag + "\"");
        if (next is not null) response.Headers.Add("Link", "<" + next + ">; rel=\"next\"");
        return response;
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => this.now;
    }

    private sealed class AsyncHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
