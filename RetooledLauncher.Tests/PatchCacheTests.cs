using System.Security.Cryptography;
using System.Text;
using KSP.Retooled.Launcher.Exceptions;
using KSP.Retooled.Launcher.Patches;
using KSP.Retooled.Launcher.Releases;
using KSP.Retooled.Launcher.Releases.GitHub;
using KSP.Retooled.Launcher.Tests.Mocking;
using KSP.Retooled.Launcher.Utils;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

public sealed class PatchCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
                                                "retooled-launcher-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, true);
        }
    }

    private static Release WithPatch(string tag, byte[] bytes, bool digest) => new(tag,
                                                                                   string.Empty,
                                                                                   false,
                                                                                   DateTimeOffset.UnixEpoch,
                                                                                   string.Empty,
                                                                                   string.Empty,
                                                                                   [
                                                                                       new ReleaseAsset("retooled-linux.rtpatch",
                                                                                                        bytes.Length,
                                                                                                        $"https://dl/{tag}",
                                                                                                        $"https://api/{tag}",
                                                                                                        digest ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : null)
                                                                                   ]);

    [Fact]
    public async Task DownloadsOnceThenReusesTheVerifiedFile()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("patch bytes");
        FakeHttp http = new();
        http.Bytes("https://dl/v1", bytes);
        HttpClient client = new(http);
        PatchCache cache = new(client, new GitHubReleaseSource(client, "o/r"), this.root);
        Release release = WithPatch("v1", bytes, true);

        string path = await cache.GetPatchAsync(release, "linux", null, CancellationToken.None);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        await cache.GetPatchAsync(release, "linux", null, CancellationToken.None);
        Assert.Single(http.requests);
    }

    [Fact]
    public async Task ANewBuildOfAReleaseDoesNotOverwriteTheOldOne()
    {
        byte[] first = Encoding.UTF8.GetBytes("first build");
        byte[] second = Encoding.UTF8.GetBytes("second build");
        FakeHttp http = new();
        HttpClient client = new(http);
        PatchCache cache = new(client, new GitHubReleaseSource(client, "o/r"), this.root);

        http.Bytes("https://dl/pr-1", first);
        string old = await cache.GetPatchAsync(WithPatch("pr-1", first, true), "linux", null, CancellationToken.None);
        http.Bytes("https://dl/pr-1", second);
        string rebuilt = await cache.GetPatchAsync(WithPatch("pr-1", second, true), "linux", null, CancellationToken.None);

        Assert.NotEqual(old, rebuilt);
        Assert.Equal(first, await File.ReadAllBytesAsync(old));
        Assert.Equal(second, await File.ReadAllBytesAsync(rebuilt));
    }

    [Fact]
    public async Task UsesTheApiEndpointWhenATokenIsSet()
    {
        byte[] bytes = [1, 2, 3];
        FakeHttp http = new();
        http.Bytes("https://api/v1", bytes);
        HttpClient client = new(http);
        PatchCache cache = new(client,
                               new GitHubReleaseSource(client, "o/r", "secret"), this.root);

        await cache.GetPatchAsync(WithPatch("v1", bytes, false), "linux", null, CancellationToken.None);
        HttpRequestMessage request = Assert.Single(http.requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("application/octet-stream", request.Headers.Accept.Single().MediaType);
    }

    [Fact]
    public async Task DownloadsFromTheMirrorWhenGitHubFails()
    {
        byte[] bytes = [1, 2, 3];
        FakeHttp http = new();
        http.routes["https://github.com/KSP1Retooled/retooled/releases/download/v1/retooled-linux.rtpatch"] =
            () => throw new HttpRequestException("connection reset");

        http.Bytes("https://gitcode.com/ksp1retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                   bytes);

        HttpClient client = new(http);
        PatchCache cache = new(client,
                               MirroredReleaseSource.For(client, LauncherInfo.DEFAULT_REPOSITORY, "secret"), this.root);

        Release release = new("v1",
                              string.Empty,
                              false,
                              DateTimeOffset.UnixEpoch,
                              string.Empty,
                              string.Empty,
                              [
                                  new ReleaseAsset("retooled-linux.rtpatch",
                                                   bytes.Length,
                                                   "https://github.com/KSP1Retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                                                   "https://github.com/KSP1Retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                                                   Convert.ToHexStringLower(SHA256.HashData(bytes)))
                              ]) { Channel = LauncherInfo.DEFAULT_REPOSITORY };

        string path = await cache.GetPatchAsync(release, "linux", null, CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, http.requests.Count);
        // the GitHub token stays with GitHub
        Assert.Null(http.requests[1].Headers.Authorization);
    }

    [Fact]
    public async Task RejectsADownloadThatDoesNotMatchTheDigest()
    {
        FakeHttp http = new();
        http.Bytes("https://dl/v1", Encoding.UTF8.GetBytes("tampered!!!"));
        HttpClient client = new(http);
        PatchCache cache = new(client, new GitHubReleaseSource(client, "o/r"), this.root);

        await Assert.ThrowsAsync<LauncherException>(() =>
                                                        cache.GetPatchAsync(WithPatch("v1", Encoding.UTF8.GetBytes("patch bytes"), true),
                                                                            "linux",
                                                                            null,
                                                                            CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(this.root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PruneKeepsOnlyTheNamedReleases()
    {
        FakeHttp http = new();
        http.Bytes("https://dl/v1", [1]);
        http.Bytes("https://dl/release/v2", [2]);
        HttpClient client = new(http);
        PatchCache cache = new(client, new GitHubReleaseSource(client, "o/r"), this.root);
        string old = await cache.GetPatchAsync(WithPatch("v1", [1], false), "linux", null, CancellationToken.None);
        string kept = await cache.GetPatchAsync(WithPatch("release/v2", [2], false),
                                                "linux",
                                                null,
                                                CancellationToken.None);

        cache.Prune([kept]);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public async Task TheSameTagInTwoChannelsDoesNotShareACacheEntry()
    {
        FakeHttp http = new();
        http.Bytes("https://dl/v1", [1, 2, 3]);
        HttpClient client = new(http);
        PatchCache cache = new(client, new GitHubReleaseSource(client, "o/r"), this.root);
        Release release = WithPatch("v1", [1, 2, 3], false);

        string first = await cache.GetPatchAsync(release with { Channel = "a/one" },
                                                 "linux",
                                                 null,
                                                 CancellationToken.None);

        string second = await cache.GetPatchAsync(release with { Channel = "a/two" },
                                                  "linux",
                                                  null,
                                                  CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.Equal(2, http.requests.Count);
    }

    [Fact]
    public async Task ImportKeepsACopyPerContent()
    {
        Directory.CreateDirectory(this.root);
        string file = Path.Combine(this.root, "mine.rtpatch");
        HttpClient client = new(new FakeHttp());
        PatchCache cache = new(client,
                               new GitHubReleaseSource(client, "o/r"),
                               Path.Combine(this.root, "cache"));

        File.WriteAllBytes(file, [1, 2, 3]);
        string first = await cache.ImportAsync(file, null, CancellationToken.None);
        Assert.Equal(first, await cache.ImportAsync(file, null, CancellationToken.None));

        // a rebuild under the same name must leave the copy of the installed patch alone
        File.WriteAllBytes(file, [4, 5, 6]);
        string second = await cache.ImportAsync(file, null, CancellationToken.None);
        File.Delete(file);

        Assert.NotEqual(first, second);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(first));
        Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(second));

        cache.Prune([second]);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
    }
}
