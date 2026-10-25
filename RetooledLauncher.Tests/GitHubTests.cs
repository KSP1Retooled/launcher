using System.Net;
using System.Security.Cryptography;
using System.Text;
using KSP.Retooled.Launcher.Installs;
using KSP.Retooled.Launcher.Releases;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

/// <summary>Answers requests from a map of URL to response.</summary>
sealed class FakeHttp : HttpMessageHandler
{
    public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = [];
    public readonly List<HttpRequestMessage> Requests = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        Requests.Add(request);
        return Task.FromResult(
            Routes.TryGetValue(request.RequestUri!.ToString(), out var route)
                ? route()
                : new HttpResponseMessage(HttpStatusCode.NotFound)
        );
    }

    public void Json(string url, string json, string? next = null) =>
        Routes[url] = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (next != null)
                response.Headers.Add(
                    "Link",
                    $"<{next}>; rel=\"next\", <https://api.github.com/x?page=9>; rel=\"last\""
                );
            return response;
        };

    public void Bytes(string url, byte[] bytes) =>
        Routes[url] = () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
}

public sealed class GitHubReleaseSourceTests
{
    const string Page1 = "https://api.github.com/repos/o/r/releases?per_page=100";
    const string Page2 = "https://api.github.com/repos/o/r/releases?per_page=100&page=2";

    [Fact]
    public async Task ReadsEveryPageAndDropsDrafts()
    {
        var http = new FakeHttp();
        http.Json(
            Page1,
            """
            [
              { "tag_name": "v1.1.0-beta.1", "name": "Beta", "prerelease": true, "draft": false, "published_at": "2026-02-01T00:00:00Z",
                "body": "notes", "html_url": "https://github.com/o/r/releases/v1.1.0-beta.1",
                "assets": [ { "name": "retooled-windows.rtpatch", "size": 12, "url": "https://api.github.com/assets/1",
                              "browser_download_url": "https://github.com/dl/1", "digest": "sha256:ABCDEF" } ] },
              { "tag_name": "v2.0.0", "draft": true, "prerelease": false, "assets": [] }
            ]
            """,
            next: Page2
        );
        http.Json(
            Page2,
            """[ { "tag_name": "v1.0.0", "prerelease": false, "draft": false, "published_at": "2026-01-01T00:00:00Z", "assets": [] } ]"""
        );

        var releases = await new GitHubReleaseSource(
            new HttpClient(http),
            "o/r"
        ).GetReleasesAsync();

        Assert.Equal(["v1.1.0-beta.1", "v1.0.0"], releases.Select(r => r.Tag));
        var beta = releases[0];
        Assert.True(beta.Prerelease);
        Assert.False(releases[1].Prerelease);
        var asset = Assert.Single(beta.Assets);
        Assert.Equal("abcdef", asset.Sha256);
        Assert.Same(asset, beta.PatchFor("windows"));
        Assert.Null(beta.PatchFor("linux"));
        Assert.All(
            http.Requests,
            r => Assert.Contains("KSPRetooledLauncher", r.Headers.UserAgent.ToString())
        );
    }

    [Fact]
    public async Task ExplainsAMissingRepository()
    {
        var error = await Assert.ThrowsAsync<LauncherException>(() =>
            new GitHubReleaseSource(new HttpClient(new FakeHttp()), "o/r").GetReleasesAsync()
        );
        Assert.Contains("GITHUB_TOKEN", error.Message);
    }

    [Theory]
    [InlineData("KSP1Retooled/retooled", true)]
    [InlineData("a/b.c-d_e", true)]
    [InlineData("noslash", false)]
    [InlineData("a/b/c", false)]
    [InlineData("a/../b", false)]
    public void ValidatesRepositoryNames(string repository, bool valid) =>
        Assert.Equal(valid, GitHubReleaseSource.IsValidRepository(repository));
}

public sealed class PatchCacheTests : IDisposable
{
    readonly string root = Path.Combine(
        Path.GetTempPath(),
        "retooled-launcher-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    static Release WithPatch(string tag, byte[] bytes, bool digest) =>
        new(
            tag,
            "",
            false,
            DateTimeOffset.UnixEpoch,
            "",
            "",
            [
                new ReleaseAsset(
                    "retooled-linux.rtpatch",
                    bytes.Length,
                    $"https://dl/{tag}",
                    $"https://api/{tag}",
                    digest ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : null
                ),
            ]
        );

    [Fact]
    public async Task DownloadsOnceThenReusesTheVerifiedFile()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("patch bytes");
        var http = new FakeHttp();
        http.Bytes("https://dl/v1", bytes);
        var client = new HttpClient(http);
        var cache = new PatchCache(client, new GitHubReleaseSource(client, "o/r"), root);
        var release = WithPatch("v1", bytes, digest: true);

        string path = await cache.GetPatchAsync(release, "linux", null, default);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        await cache.GetPatchAsync(release, "linux", null, default);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task UsesTheApiEndpointWhenATokenIsSet()
    {
        byte[] bytes = [1, 2, 3];
        var http = new FakeHttp();
        http.Bytes("https://api/v1", bytes);
        var client = new HttpClient(http);
        var cache = new PatchCache(
            client,
            new GitHubReleaseSource(client, "o/r", token: "secret"),
            root
        );

        await cache.GetPatchAsync(WithPatch("v1", bytes, digest: false), "linux", null, default);
        var request = Assert.Single(http.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("application/octet-stream", request.Headers.Accept.Single().MediaType);
    }

    [Fact]
    public async Task RejectsADownloadThatDoesNotMatchTheDigest()
    {
        var http = new FakeHttp();
        http.Bytes("https://dl/v1", Encoding.UTF8.GetBytes("tampered!!!"));
        var client = new HttpClient(http);
        var cache = new PatchCache(client, new GitHubReleaseSource(client, "o/r"), root);

        await Assert.ThrowsAsync<LauncherException>(() =>
            cache.GetPatchAsync(
                WithPatch("v1", Encoding.UTF8.GetBytes("patch bytes"), digest: true),
                "linux",
                null,
                default
            )
        );
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PruneKeepsOnlyTheNamedReleases()
    {
        var http = new FakeHttp();
        http.Bytes("https://dl/v1", [1]);
        http.Bytes("https://dl/release/v2", [2]);
        var client = new HttpClient(http);
        var cache = new PatchCache(client, new GitHubReleaseSource(client, "o/r"), root);
        string old = await cache.GetPatchAsync(WithPatch("v1", [1], false), "linux", null, default);
        string kept = await cache.GetPatchAsync(
            WithPatch("release/v2", [2], false),
            "linux",
            null,
            default
        );

        cache.Prune(["release/v2"]);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(kept));
    }
}

public sealed class InstallLocatorTests
{
    [Fact]
    public void ReadsSteamLibraryPaths()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "apps" { "228980" "1" }
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                }
            }
            """;
        Assert.Equal(
            [@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"],
            InstallLocator.ParseLibraryFolders(vdf)
        );
    }
}
