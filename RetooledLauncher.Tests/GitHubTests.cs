using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using KSP.Retooled.Launcher.Installs;
using KSP.Retooled.Launcher.Releases;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

/// <summary>Answers requests from a map of URL to response.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public readonly List<HttpRequestMessage> requests = [];
    public readonly Dictionary<string, Func<HttpResponseMessage>> routes = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        this.requests.Add(request);
        return Task.FromResult(
                               this.routes.TryGetValue(request.RequestUri!.ToString(), out Func<HttpResponseMessage>? route)
                                   ? route()
                                   : new HttpResponseMessage(HttpStatusCode.NotFound)
                              );
    }

    public void Json(string url, string json, string? next = null) =>
        this.routes[url] = () =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (next != null)
            {
                response.Headers.Add(
                                     "Link",
                                     $"<{next}>; rel=\"next\", <https://api.github.com/x?page=9>; rel=\"last\""
                                    );
            }

            return response;
        };

    public void Bytes(string url, byte[] bytes) =>
        this.routes[url] = () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
}

public sealed class GitHubReleaseSourceTests
{
    private const string PAGE1 = "https://api.github.com/repos/o/r/releases?per_page=100";
    private const string PAGE2 = "https://api.github.com/repos/o/r/releases?per_page=100&page=2";

    [Theory, InlineData("o/r", "retooled-windows.rtpatch"), InlineData("o/r#debug", "retooled-windows-debug.rtpatch")]
    public async Task AChannelPicksItsOwnPatchFromItsRepository(string channel, string expected)
    {
        FakeHttp http = new();
        http.Json(
                  PAGE1,
                  """
                  [ { "tag_name": "v1.0.0", "prerelease": false, "draft": false, "published_at": "2026-01-01T00:00:00Z",
                      "assets": [
                        { "name": "retooled-windows.rtpatch", "size": 1, "url": "a", "browser_download_url": "a" },
                        { "name": "retooled-windows-debug.rtpatch", "size": 2, "url": "b", "browser_download_url": "b" }
                      ] } ]
                  """
                 );

        Release release = Assert.Single(
                                        await new GitHubReleaseSource(new HttpClient(http), channel).GetReleasesAsync()
                                       );

        Assert.Equal(channel, release.Channel);
        Assert.Equal(expected, release.PatchFor("windows")?.Name);
    }

    [Fact]
    public void AReleaseWithoutDebugPatchesCannotBeInstalledFromTheDEBUG_CHANNEL()
    {
        ReleaseAsset asset = new(Release.PatchName("windows"), 1, "a", "a", null);
        Release release = new("v1.0.0", string.Empty, false, default, string.Empty, string.Empty, [asset]);

        Assert.Same(asset, (release with { Channel = "o/r" }).PatchFor("windows"));
        Assert.Null((release with { Channel = "o/r#debug" }).PatchFor("windows"));
    }

    [Theory, InlineData(LauncherInfo.DEFAULT_REPOSITORY, true), InlineData(LauncherInfo.DEBUG_CHANNEL, true), InlineData(LauncherInfo.EXPERIMENTAL_CHANNEL, true), InlineData("o/r#debug", false), InlineData("o/r", false)]
    public void OnlyTheBuiltInChannelsAreKeptOutOfTheSavedList(string channel, bool builtIn) =>
        Assert.Equal(builtIn, LauncherInfo.IsBuiltInChannel(channel));

    [Fact]
    public async Task ReadsEveryPageAndDropsDrafts()
    {
        FakeHttp http = new();
        http.Json(
                  PAGE1,
                  """
                  [
                    { "tag_name": "v1.1.0-beta.1", "name": "Beta", "prerelease": true, "draft": false, "published_at": "2026-02-01T00:00:00Z",
                      "body": "notes", "html_url": "https://github.com/o/r/releases/v1.1.0-beta.1",
                      "assets": [ { "name": "retooled-windows.rtpatch", "size": 12, "url": "https://api.github.com/assets/1",
                                    "browser_download_url": "https://github.com/dl/1", "digest": "sha256:ABCDEF" } ] },
                    { "tag_name": "v2.0.0", "draft": true, "prerelease": false, "assets": [] }
                  ]
                  """,
                  PAGE2
                 );

        http.Json(
                  PAGE2,
                  """[ { "tag_name": "v1.0.0", "prerelease": false, "draft": false, "published_at": "2026-01-01T00:00:00Z", "assets": [] } ]"""
                 );

        IReadOnlyList<Release> releases = await new GitHubReleaseSource(
                                                                        new HttpClient(http),
                                                                        "o/r"
                                                                       ).GetReleasesAsync();

        Assert.Equal(["v1.1.0-beta.1", "v1.0.0"], releases.Select(r => r.Tag));
        Assert.All(releases, r => Assert.Equal("o/r", r.Channel));
        Release beta = releases[0];
        Assert.True(beta.Prerelease);
        Assert.False(releases[1].Prerelease);
        ReleaseAsset asset = Assert.Single(beta.Assets);
        Assert.Equal("abcdef", asset.Sha256);
        Assert.Same(asset, beta.PatchFor("windows"));
        Assert.Null(beta.PatchFor("linux"));
        Assert.All(
                   http.requests,
                   r => Assert.Contains("KSPRetooledLauncher", r.Headers.UserAgent.ToString())
                  );
    }

    [Fact]
    public async Task ExplainsAMissingRepository()
    {
        LauncherException error = await Assert.ThrowsAsync<LauncherException>(() =>
                                                                                  new GitHubReleaseSource(new HttpClient(new FakeHttp()), "o/r").GetReleasesAsync()
                                                                             );

        Assert.Contains("GITHUB_TOKEN", error.Message);
    }

    [Theory, InlineData("KSP1Retooled/retooled", true), InlineData("a/b.c-d_e", true), InlineData("noslash", false), InlineData("a/b/c", false), InlineData("a/../b", false)]
    public void ValidatesRepositoryNames(string repository, bool valid) =>
        Assert.Equal(valid, GitHubReleaseSource.IsValidRepository(repository));
}

public sealed class MirrorTests
{
    private const string GIT_HUB_LIST = "https://api.github.com/repos/KSP1Retooled/retooled/releases?per_page=100";
    private const string GIT_CODE_LIST =
        "https://api.gitcode.com/api/v5/repos/ksp1retooled/retooled/releases?per_page=100&page=1";

    private const string GIT_CODE_JSON = """
                                         [ { "tag_name": "1.13.0-rc6", "name": "RC 6", "prerelease": true, "body": "notes",
                                             "created_at": "2026-11-11T19:02:45+08:00", "release_status": "pre",
                                             "assets": [
                                               { "browser_download_url": "https://raw.gitcode.com/ksp1retooled/retooled/archive/refs/heads/1.13.0-rc6.zip",
                                                 "name": "1.13.0-rc6.zip", "type": "source" },
                                               { "browser_download_url": "https://gitcode.com/ksp1retooled/retooled/releases/download/1.13.0-rc6/retooled-windows.rtpatch",
                                                 "name": "retooled-windows.rtpatch", "type": "attach", "id": 1, "size": 3, "digest": "sha256:ABC" }
                                             ] } ]
                                         """;

    [Theory, InlineData("KSP1Retooled/retooled", "ksp1retooled/retooled"), InlineData("ksp1retooled/RETOOLED", "ksp1retooled/retooled"), InlineData("o/r", null)]
    public void OnlyTheDEFAULT_REPOSITORYHasAMirror(string repository, string? mirror) =>
        Assert.Equal(mirror, LauncherInfo.MirrorOf(repository));

    [Fact]
    public void AChannelWithoutAMirrorIsListedFromGitHubAlone()
    {
        HttpClient client = new(new FakeHttp());
        Assert.IsType<GitHubReleaseSource>(MirroredReleaseSource.For(client, "o/r"));
        Assert.IsType<MirroredReleaseSource>(
                                             MirroredReleaseSource.For(client, LauncherInfo.DEBUG_CHANNEL)
                                            );
    }

    [Fact]
    public async Task ReadsGitCodeReleasesAsTheChannelTheyMirror()
    {
        FakeHttp http = new();
        http.Json(GIT_CODE_LIST, GIT_CODE_JSON);

        Release release = Assert.Single(
                                        await new GitCodeReleaseSource(
                                                                       new HttpClient(http),
                                                                       LauncherInfo.DEBUG_CHANNEL,
                                                                       LauncherInfo.DEFAULT_MIRROR
                                                                      ).GetReleasesAsync()
                                       );

        Assert.Equal(LauncherInfo.DEBUG_CHANNEL, release.Channel);
        Assert.True(release.Prerelease);
        Assert.Equal("https://gitcode.com/ksp1retooled/retooled/releases/1.13.0-rc6", release.PageUrl);
        ReleaseAsset asset = Assert.Single(release.Assets);
        Assert.Equal("retooled-windows.rtpatch", asset.Name);
        Assert.Equal(3, asset.Size);
        Assert.Equal("abc", asset.Sha256);
    }

    [Fact]
    public async Task ListsFromGitHubWhenItAnswers()
    {
        FakeHttp http = new();
        http.Json(GIT_HUB_LIST, """[ { "tag_name": "v1", "assets": [] } ]""");
        http.Json(GIT_CODE_LIST, GIT_CODE_JSON);

        IReadOnlyList<Release> releases = await MirroredReleaseSource
                                               .For(new HttpClient(http), LauncherInfo.DEFAULT_REPOSITORY)
                                               .GetReleasesAsync();

        Assert.Equal(["v1"], releases.Select(r => r.Tag));
        Assert.DoesNotContain(http.requests, r => r.RequestUri!.Host.EndsWith("gitcode.com"));
    }

    [Fact]
    public async Task ListsFromTheMirrorWhenGitHubFails()
    {
        FakeHttp http = new();
        http.routes[GIT_HUB_LIST] = () => throw new HttpRequestException("no route to host");
        http.Json(GIT_CODE_LIST, GIT_CODE_JSON);
        List<string> logged = [];

        IReadOnlyList<Release> releases = await MirroredReleaseSource
                                               .For(new HttpClient(http), LauncherInfo.DEFAULT_REPOSITORY, log: logged.Add)
                                               .GetReleasesAsync();

        Assert.Equal(["1.13.0-rc6"], releases.Select(r => r.Tag));
        Assert.All(releases, r => Assert.Equal(LauncherInfo.DEFAULT_REPOSITORY, r.Channel));
        Assert.Contains(logged, l => l.Contains("no route to host"));
    }

    [Fact]
    public async Task ReportsBothFailures()
    {
        FakeHttp http = new();
        http.routes[GIT_HUB_LIST] = () => throw new HttpRequestException("no route to host");

        LauncherException error = await Assert.ThrowsAsync<LauncherException>(() =>
                                                                                  MirroredReleaseSource
                                                                                     .For(new HttpClient(http), LauncherInfo.DEFAULT_REPOSITORY)
                                                                                     .GetReleasesAsync()
                                                                             );

        Assert.Contains("no route to host", error.Message);
        Assert.Contains("gitcode.com/ksp1retooled/retooled", error.Message);
    }

    [Fact]
    public async Task PreferringGitCodeListsFromItFirst()
    {
        FakeHttp http = new();
        http.Json(GIT_HUB_LIST, """[ { "tag_name": "v1", "assets": [] } ]""");
        http.Json(GIT_CODE_LIST, GIT_CODE_JSON);

        IReadOnlyList<Release> releases = await MirroredReleaseSource
                                               .For(new HttpClient(http), LauncherInfo.DEFAULT_REPOSITORY, preferred: ReleaseMirror.GitCode)
                                               .GetReleasesAsync();

        Assert.Equal(["1.13.0-rc6"], releases.Select(r => r.Tag));
        Assert.Single(http.requests);
    }

    [Fact]
    public async Task PreferringGitCodeFallsBackToGitHub()
    {
        FakeHttp http = new();
        http.Json(GIT_HUB_LIST, """[ { "tag_name": "v1", "assets": [] } ]""");

        IReadOnlyList<Release> releases = await MirroredReleaseSource
                                               .For(new HttpClient(http), LauncherInfo.DEFAULT_REPOSITORY, preferred: ReleaseMirror.GitCode)
                                               .GetReleasesAsync();

        Assert.Equal(["v1"], releases.Select(r => r.Tag));
    }

    [Fact]
    public void AGitCodeAssetFallsBackToGitHubsCopy()
    {
        IReleaseSource source = MirroredReleaseSource.For(new HttpClient(new FakeHttp()),
                                                          LauncherInfo.DEFAULT_REPOSITORY,
                                                          preferred: ReleaseMirror.GitCode);

        ReleaseAsset asset = new("retooled-linux.rtpatch",
                                 1,
                                 "https://gitcode.com/ksp1retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                                 "https://gitcode.com/ksp1retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                                 null);

        Release release = new("v1", string.Empty, false, default, string.Empty, string.Empty, [asset]);

        Assert.Equal(
                     [
                         "https://gitcode.com/ksp1retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                         "https://github.com/KSP1Retooled/retooled/releases/download/v1/retooled-linux.rtpatch"
                     ],
                     source.AssetRequests(release, asset).Select(r => r.RequestUri!.ToString())
                    );
    }

    [Fact]
    public async Task CancellingDoesNotTurnToTheMirror()
    {
        using CancellationTokenSource cancel = new();
        FakeHttp http = new();
        http.routes[GIT_HUB_LIST] = () =>
        {
            // ReSharper disable once AccessToDisposedClosure
            cancel.Cancel();
            throw new TaskCanceledException();
        };

        http.Json(GIT_CODE_LIST, GIT_CODE_JSON);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                                                                    MirroredReleaseSource
                                                                       .For(new HttpClient(http), LauncherInfo.DEFAULT_REPOSITORY)
                                                                       .GetReleasesAsync(cancel.Token)
                                                               );

        Assert.Single(http.requests);
    }
}

public sealed class PatchCacheTests : IDisposable
{
    private readonly string root = Path.Combine(
                                                Path.GetTempPath(),
                                                "retooled-launcher-" + Guid.NewGuid().ToString("N")
                                               );

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, true);
        }
    }

    private static Release WithPatch(string tag, byte[] bytes, bool digest) =>
        new(
            tag,
            string.Empty,
            false,
            DateTimeOffset.UnixEpoch,
            string.Empty,
            string.Empty,
            [
                new ReleaseAsset(
                                 "retooled-linux.rtpatch",
                                 bytes.Length,
                                 $"https://dl/{tag}",
                                 $"https://api/{tag}",
                                 digest ? Convert.ToHexStringLower(SHA256.HashData(bytes)) : null
                                )
            ]
           );

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
        PatchCache cache = new(
                               client,
                               new GitHubReleaseSource(client, "o/r", "secret"), this.root
                              );

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

        http.Bytes(
                   "https://gitcode.com/ksp1retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                   bytes
                  );

        HttpClient client = new(http);
        PatchCache cache = new(
                               client,
                               MirroredReleaseSource.For(client, LauncherInfo.DEFAULT_REPOSITORY, "secret"), this.root
                              );

        Release release = new(
                              "v1",
                              string.Empty,
                              false,
                              DateTimeOffset.UnixEpoch,
                              string.Empty,
                              string.Empty,
                              [
                                  new ReleaseAsset(
                                                   "retooled-linux.rtpatch",
                                                   bytes.Length,
                                                   "https://github.com/KSP1Retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                                                   "https://github.com/KSP1Retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                                                   Convert.ToHexStringLower(SHA256.HashData(bytes))
                                                  )
                              ]
                             ) { Channel = LauncherInfo.DEFAULT_REPOSITORY };

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
                                                        cache.GetPatchAsync(
                                                                            WithPatch("v1", Encoding.UTF8.GetBytes("patch bytes"), true),
                                                                            "linux",
                                                                            null,
                                                                            CancellationToken.None
                                                                           )
                                                   );

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
        string kept = await cache.GetPatchAsync(
                                                WithPatch("release/v2", [2], false),
                                                "linux",
                                                null,
                                                CancellationToken.None
                                               );

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

        string first = await cache.GetPatchAsync(
                                                 release with { Channel = "a/one" },
                                                 "linux",
                                                 null,
                                                 CancellationToken.None
                                                );

        string second = await cache.GetPatchAsync(
                                                  release with { Channel = "a/two" },
                                                  "linux",
                                                  null,
                                                  CancellationToken.None
                                                 );

        Assert.NotEqual(first, second);
        Assert.Equal(2, http.requests.Count);
    }

    [Fact]
    public async Task ImportKeepsACopyPerContent()
    {
        Directory.CreateDirectory(this.root);
        string file = Path.Combine(this.root, "mine.rtpatch");
        HttpClient client = new(new FakeHttp());
        PatchCache cache = new(
                               client,
                               new GitHubReleaseSource(client, "o/r"),
                               Path.Combine(this.root, "cache")
                              );

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

public sealed class LauncherSettingsTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(),
                                                "retooled-settings-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose() => File.Delete(this.file);

    [Fact]
    public void OffersTheDefaultChannelFirstAndOnlyOnce() =>
        Assert.Equal(
                     [LauncherInfo.DEFAULT_REPOSITORY, LauncherInfo.DEBUG_CHANNEL, LauncherInfo.EXPERIMENTAL_CHANNEL, "a/b"],
                     new LauncherSettings
                     {
                         Channels = ["a/b", "A/B", LauncherInfo.DEFAULT_REPOSITORY]
                     }.AllChannels
                    );

    [Fact]
    public void ThePreferredMirrorIsSavedByName()
    {
        new LauncherSettings { PreferredMirror = ReleaseMirror.GitCode }.Save(this.file);
        Assert.Contains("\"GitCode\"", File.ReadAllText(this.file));
        Assert.Equal(ReleaseMirror.GitCode, LauncherSettings.Load(this.file).PreferredMirror);
        File.WriteAllText(this.file, "{}");
        Assert.Equal(ReleaseMirror.GitHub, LauncherSettings.Load(this.file).PreferredMirror);
    }

    [Fact]
    public void TheLaunchArgumentsAreSaved()
    {
        new LauncherSettings { LaunchArguments = "-popupwindow -logFile \"my log.txt\"" }.Save(this.file);
        Assert.Equal(
                     "-popupwindow -logFile \"my log.txt\"",
                     LauncherSettings.Load(this.file).LaunchArguments
                    );

        File.WriteAllText(this.file, "{}");
        Assert.Null(LauncherSettings.Load(this.file).LaunchArguments);
    }

    [Fact]
    public void ARepositoryFromBeforeChannelsIsStillOffered()
    {
        File.WriteAllText(this.file, """{ "repository": "o/r" }""");
        Assert.Equal(
                     [LauncherInfo.DEFAULT_REPOSITORY, LauncherInfo.DEBUG_CHANNEL, LauncherInfo.EXPERIMENTAL_CHANNEL, "o/r"],
                     LauncherSettings.Load(this.file).AllChannels
                    );
    }

    [Fact]
    public void ChannelsSurviveASave()
    {
        new LauncherSettings { Channels = ["a/b"], Repository = "a/b" }.Save(this.file);
        LauncherSettings loaded = LauncherSettings.Load(this.file);
        Assert.Equal(["a/b"], loaded.Channels);
        Assert.Equal("a/b", loaded.Repository);
    }
}

public sealed class InstallLocatorTests
{
    [Fact]
    public void ReadsSteamLibraryPaths()
    {
        const string VDF = """
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
                     InstallLocator.ParseLibraryFolders(VDF)
                    );
    }

    [Fact]
    public void ReadsLegacySteamLibraryPaths()
    {
        const string VDF = """
                           "LibraryFolders"
                           {
                               "TimeNextStatsReport"		"1600000000"
                               "ContentStatsID"		"-1234"
                               "1"		"D:\\SteamLibrary"
                               "2"		"/mnt/games/steam"
                           }
                           """;

        Assert.Equal(
                     [@"D:\SteamLibrary", "/mnt/games/steam"],
                     InstallLocator.ParseLibraryFolders(VDF)
                    );
    }

    [Fact]
    public void ReadsHeroicGogInstalls()
    {
        const string JSON = """
                            {
                              "installed": [
                                {
                                  "platform": "linux",
                                  "executable": "",
                                  "install_path": "/home/kerbal/Games/Heroic/Kerbal Space Program",
                                  "appName": "1429864849"
                                },
                                {
                                  "platform": "windows",
                                  "install_path": "/home/kerbal/Games/Heroic/Other Game",
                                  "appName": "1111111111"
                                }
                              ]
                            }
                            """;

        Assert.Equal(
                     ["/home/kerbal/Games/Heroic/Kerbal Space Program"],
                     InstallLocator.ParseHeroicInstalls(JSON, "1429864849")
                    );

        Assert.Empty(InstallLocator.ParseHeroicInstalls("not json", "1429864849"));
        Assert.Empty(InstallLocator.ParseHeroicInstalls("[]", "1429864849"));
    }

    [Fact]
    public void MapsWindowsPathsIntoWinePrefixes()
    {
        const string PREFIX = "/home/kerbal/.wine";
        Assert.Equal(
                     Path.Combine(PREFIX, "drive_c", "Program Files (x86)", "Steam"),
                     InstallLocator.WindowsPathInPrefix(PREFIX, @"C:\Program Files (x86)\Steam")
                    );

        Assert.Equal(
                     Path.Combine(PREFIX, "dosdevices", "d:", "SteamLibrary"),
                     InstallLocator.WindowsPathInPrefix(PREFIX, @"D:\SteamLibrary")
                    );

        Assert.Equal(
                     Path.Combine("/", "mnt", "games"),
                     InstallLocator.WindowsPathInPrefix(PREFIX, "z:/mnt/games/")
                    );

        Assert.Null(InstallLocator.WindowsPathInPrefix(PREFIX, "/mnt/games"));
    }

    [Fact]
    public void ReadsHeroicAndMinigalaxyInstallFolders()
    {
        Assert.Equal(
                     "/data/games",
                     InstallLocator.ParseHeroicInstallFolder(
                                                             """{ "defaultSettings": { "defaultInstallPath": "/data/games" } }"""
                                                            )
                    );

        Assert.Null(InstallLocator.ParseHeroicInstallFolder("{}"));
        Assert.Equal(
                     "/home/kerbal/GOG Games",
                     InstallLocator.ParseMinigalaxyInstallFolder(
                                                                 """{ "install_dir": "/home/kerbal/GOG Games" }"""
                                                                )
                    );

        Assert.Null(InstallLocator.ParseMinigalaxyInstallFolder("garbage"));
    }
}

public sealed class GameStarterTests
{
    [Theory, InlineData(null), InlineData(""), InlineData("  \t ")]
    public void NoArgumentsSplitToNone(string? commandLine) =>
        Assert.Empty(GameStarter.SplitArguments(commandLine));

    [Fact]
    public void ArgumentsAreSplitAtWhitespace() =>
        Assert.Equal(
                     ["-popupwindow", "-screen-width", "1920"],
                     GameStarter.SplitArguments("  -popupwindow -screen-width\t1920 ")
                    );

    [Fact]
    public void QuotesKeepWhitespaceAndAreRemoved() =>
        Assert.Equal(
                     ["-logFile", @"C:\my logs\ksp.log", "--name=Jeb Kerman", string.Empty],
                     GameStarter.SplitArguments("""-logFile "C:\my logs\ksp.log" --name="Jeb Kerman" "" """)
                    );

    [Theory, InlineData("windows"), InlineData("linux")]
    public void ThePlayerIsGivenTheArguments(string platform)
    {
        ProcessStartInfo start = GameStarter.StartInfo("install", platform, "-a \"b c\"");
        Assert.Equal(Path.Combine("install", KspInstall.Player(platform)), start.FileName);
        Assert.Equal(["-a", "b c"], start.ArgumentList);
        Assert.Equal("install", start.WorkingDirectory);
        Assert.Empty(GameStarter.StartInfo("install", platform, null).ArgumentList);
    }

    [Fact]
    public void OnMacosTheArgumentsFollowArgs()
    {
        string app = Path.Combine("install", "KSP.app");
        Assert.Equal(
                     ["-a", app, "--args", "-a", "b c"],
                     GameStarter.StartInfo("install", "macos", "-a \"b c\"").ArgumentList
                    );

        Assert.Equal(["-a", app], GameStarter.StartInfo("install", "macos", " ").ArgumentList);
    }
}
