using System.Net;
using System.Net.Http.Headers;
using KSP.Retooled.Launcher.Releases;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

public sealed class GitHubMetadataCacheTests : IDisposable
{
    const string Page1 = "https://api.github.com/repos/o/r/releases?per_page=100";
    const string Page2 = Page1 + "&page=2";
    readonly string directory = Path.Combine(Path.GetTempPath(), "retooled-metadata-" + Guid.NewGuid());

    [Fact]
    public async Task FreshMetadataIsSharedAcrossRegularAndDebugChannels()
    {
        var http = new FakeHttp();
        http.Json(Page1, """[{"tag_name":"v1"}]""");
        var client = new HttpClient(http);
        var normal = await new GitHubReleaseSource(client, "o/r").GetReleasesAsync();
        var debug = await new GitHubReleaseSource(client, "o/r#debug").GetReleasesAsync();
        Assert.Single(http.Requests);
        Assert.Equal("o/r", Assert.Single(normal).Channel);
        Assert.Equal("o/r#debug", Assert.Single(debug).Channel);
    }

    [Fact]
    public async Task RevalidatesEveryPageAndKeepsPaginationWhenNotModified()
    {
        var http = new FakeHttp();
        http.Routes[Page1] = () => Page("v2", "first", Page2);
        http.Routes[Page2] = () => Page("v1", "second");
        var client = new HttpClient(http);
        var cache = new GitHubMetadataCache { Freshness = TimeSpan.Zero };
        var source = new GitHubReleaseSource(client, "o/r", cache: cache);
        await source.GetReleasesAsync();
        http.Routes[Page1] = http.Routes[Page2] = () => new(HttpStatusCode.NotModified);
        var releases = await source.GetReleasesAsync();
        Assert.Equal(new[] { "v2", "v1" }, releases.Select(r => r.Tag));
        Assert.Equal("\"first\"", http.Requests[2].Headers.IfNoneMatch.Single().ToString());
        Assert.Equal("\"second\"", http.Requests[3].Headers.IfNoneMatch.Single().ToString());
    }

    [Fact]
    public async Task UpdatedPaginationDropsOldPages()
    {
        var http = new FakeHttp();
        http.Routes[Page1] = () => Page("v2", "old", Page2);
        http.Routes[Page2] = () => Page("v1", "second");
        var client = new HttpClient(http);
        var source = new GitHubReleaseSource(client, "o/r", cache: new() { Freshness = TimeSpan.Zero });
        await source.GetReleasesAsync();
        http.Routes[Page1] = () => Page("v3", "new");
        Assert.Equal("v3", Assert.Single(await source.GetReleasesAsync()).Tag);
        Assert.Equal(3, http.Requests.Count);
    }

    [Fact]
    public async Task AnonymousMetadataSurvivesANewCacheAndIsRevalidated()
    {
        var http = new FakeHttp();
        http.Routes[Page1] = () => Page("v1", "persisted");
        var client = new HttpClient(http);
        await new GitHubReleaseSource(client, "o/r", cache: new(directory)).GetReleasesAsync();
        http.Routes[Page1] = () => new(HttpStatusCode.NotModified);
        var restarted = new GitHubMetadataCache(directory) { Freshness = TimeSpan.Zero };
        Assert.Equal("v1", Assert.Single(await new GitHubReleaseSource(client, "o/r", cache: restarted).GetReleasesAsync()).Tag);
        Assert.Equal("\"persisted\"", http.Requests[1].Headers.IfNoneMatch.Single().ToString());
    }

    [Fact]
    public async Task TokensHaveSeparateCachesAndPrivateMetadataIsNotPersisted()
    {
        var http = new FakeHttp();
        http.Routes[Page1] = () => Page("private", "secret");
        var client = new HttpClient(http);
        var cache = new GitHubMetadataCache(directory);
        await new GitHubReleaseSource(client, "o/r", "one", cache).GetReleasesAsync();
        await new GitHubReleaseSource(client, "o/r", "two", cache).GetReleasesAsync();
        Assert.False(Directory.Exists(directory));
        http.Routes[Page1] = () => Page("public", "public");
        var releases = await new GitHubReleaseSource(client, "o/r", cache: cache).GetReleasesAsync();
        Assert.Equal("public", Assert.Single(releases).Tag);
        Assert.Equal(3, http.Requests.Count);
        Assert.Empty(http.Requests[2].Headers.IfNoneMatch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CooldownAppliesAcrossRepositoriesAndExpires(bool useReset)
    {
        var clock = new ManualClock();
        var http = new FakeHttp();
        http.Routes[Page1] = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (useReset)
            {
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset", clock.GetUtcNow().AddMinutes(2).ToUnixTimeSeconds().ToString());
            }
            else
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return response;
        };
        var client = new HttpClient(http);
        var cache = new GitHubMetadataCache(clock: clock);
        await Assert.ThrowsAsync<LauncherException>(() => new GitHubReleaseSource(client, "o/r", cache: cache).GetReleasesAsync());
        var other = new GitHubReleaseSource(client, "other/repo", cache: cache);
        await Assert.ThrowsAsync<LauncherException>(() => other.GetReleasesAsync());
        Assert.Single(http.Requests);
        clock.Now += TimeSpan.FromMinutes(2);
        http.Json("https://api.github.com/repos/other/repo/releases?per_page=100", "[]");
        Assert.Empty(await other.GetReleasesAsync());
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneFetchAndCancellingAWaiterKeepsItRunning()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        var client = new HttpClient(new AsyncHttp(async (_, cancellation) =>
        {
            Interlocked.Increment(ref requests);
            started.SetResult();
            await finish.Task.WaitAsync(cancellation);
            return Page("v1", "shared");
        }));
        var first = new GitHubReleaseSource(client, "o/r").GetReleasesAsync();
        await started.Task;
        using var cancel = new CancellationTokenSource();
        var cancelled = new GitHubReleaseSource(client, "o/r#debug").GetReleasesAsync(cancel.Token);
        var shared = new GitHubReleaseSource(client, "o/r#debug").GetReleasesAsync();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        finish.SetResult();
        await first;
        Assert.Equal("o/r#debug", Assert.Single(await shared).Channel);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CancellingTheFetchReleasesTheGateForAnotherCaller()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        var client = new HttpClient(new AsyncHttp(async (_, cancellation) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            return Page("v1", "retry");
        }));
        using var cancel = new CancellationTokenSource();
        var first = new GitHubReleaseSource(client, "o/r").GetReleasesAsync(cancel.Token);
        await started.Task;
        var second = new GitHubReleaseSource(client, "o/r").GetReleasesAsync();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Single(await second);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task FailedLaterPageDoesNotCommitAPartialRefresh()
    {
        var http = new FakeHttp();
        http.Routes[Page1] = () => Page("v2", "original", Page2);
        http.Routes[Page2] = () => Page("v1", "second");
        var client = new HttpClient(http);
        var source = new GitHubReleaseSource(client, "o/r", cache: new() { Freshness = TimeSpan.Zero });
        await source.GetReleasesAsync();
        http.Routes[Page1] = () => Page("v3", "uncommitted", Page2);
        http.Routes[Page2] = () => new(HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<LauncherException>(() => source.GetReleasesAsync());
        http.Routes[Page1] = http.Routes[Page2] = () => new(HttpStatusCode.NotModified);
        Assert.Equal(new[] { "v2", "v1" }, (await source.GetReleasesAsync()).Select(r => r.Tag));
        Assert.Equal("\"original\"", http.Requests[4].Headers.IfNoneMatch.Single().ToString());
    }

    [Fact]
    public async Task CorruptDiskCacheIsIgnored()
    {
        var http = new FakeHttp();
        http.Routes[Page1] = () => Page("v1", "valid");
        var client = new HttpClient(http);
        await new GitHubReleaseSource(client, "o/r", cache: new(directory)).GetReleasesAsync();
        var path = Assert.Single(Directory.GetFiles(directory));
        foreach (string corrupt in new[] { "broken json", "{\"Pages\":null}", "{\"Pages\":{\"url\":null}}" })
        {
            File.WriteAllText(path, corrupt);
            Assert.Single(await new GitHubReleaseSource(client, "o/r", cache: new(directory)).GetReleasesAsync());
        }
        Assert.Equal(4, http.Requests.Count);
    }

    [Fact]
    public async Task MetadataWithoutAnEtagIsFetchedAgainAfterFreshnessExpires()
    {
        var clock = new ManualClock();
        var http = new FakeHttp();
        http.Json(Page1, "[]");
        var client = new HttpClient(http);
        var source = new GitHubReleaseSource(client, "o/r", cache: new(clock: clock));
        await source.GetReleasesAsync();
        await source.GetReleasesAsync();
        Assert.Single(http.Requests);
        clock.Now += TimeSpan.FromMinutes(1);
        await source.GetReleasesAsync();
        Assert.Equal(2, http.Requests.Count);
        Assert.Empty(http.Requests[1].Headers.IfNoneMatch);
    }

    [Fact]
    public async Task AForbiddenResponseWithoutRetryHeadersGetsABoundedCooldown()
    {
        var clock = new ManualClock();
        var http = new FakeHttp();
        http.Routes[Page1] = () => new(HttpStatusCode.Forbidden);
        var client = new HttpClient(http);
        var source = new GitHubReleaseSource(client, "o/r", cache: new(clock: clock));
        await Assert.ThrowsAsync<LauncherException>(() => source.GetReleasesAsync());
        await Assert.ThrowsAsync<LauncherException>(() => source.GetReleasesAsync());
        Assert.Single(http.Requests);
        clock.Now += TimeSpan.FromMinutes(1);
        http.Json(Page1, "[]");
        Assert.Empty(await source.GetReleasesAsync());
        Assert.Equal(2, http.Requests.Count);
    }

    static HttpResponseMessage Page(string tag, string etag, string? next = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"tag_name\":\"" + tag + "\"}]"),
        };
        response.Headers.ETag = new EntityTagHeaderValue("\"" + etag + "\"");
        if (next != null) response.Headers.Add("Link", "<" + next + ">; rel=\"next\"");
        return response;
    }

    sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class AsyncHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
