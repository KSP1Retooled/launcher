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

    [Theory]
    [InlineData("o/r", "retooled-windows.rtpatch")]
    [InlineData("o/r#debug", "retooled-windows-debug.rtpatch")]
    public async Task AChannelPicksItsOwnPatchFromItsRepository(string channel, string expected)
    {
        var http = new FakeHttp();
        http.Json(
            Page1,
            """
            [ { "tag_name": "v1.0.0", "prerelease": false, "draft": false, "published_at": "2026-01-01T00:00:00Z",
                "assets": [
                  { "name": "retooled-windows.rtpatch", "size": 1, "url": "a", "browser_download_url": "a" },
                  { "name": "retooled-windows-debug.rtpatch", "size": 2, "url": "b", "browser_download_url": "b" }
                ] } ]
            """
        );

        var release = Assert.Single(
            await new GitHubReleaseSource(new HttpClient(http), channel).GetReleasesAsync()
        );

        Assert.Equal(channel, release.Channel);
        Assert.Equal(expected, release.PatchFor("windows")?.Name);
    }

    [Fact]
    public void AReleaseWithoutDebugPatchesCannotBeInstalledFromTheDebugChannel()
    {
        var asset = new ReleaseAsset(Release.PatchName("windows"), 1, "a", "a", null);
        var release = new Release("v1.0.0", "", false, default, "", "", [asset]);

        Assert.Same(asset, (release with { Channel = "o/r" }).PatchFor("windows"));
        Assert.Null((release with { Channel = "o/r#debug" }).PatchFor("windows"));
    }

    [Theory]
    [InlineData(LauncherInfo.DefaultRepository, true)]
    [InlineData(LauncherInfo.DebugChannel, true)]
    [InlineData("o/r#debug", false)]
    [InlineData("o/r", false)]
    public void OnlyTheBuiltInChannelsAreKeptOutOfTheSavedList(string channel, bool builtIn) =>
        Assert.Equal(builtIn, LauncherInfo.IsBuiltInChannel(channel));

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
        Assert.All(releases, r => Assert.Equal("o/r", r.Channel));
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

public sealed class MirrorTests
{
    const string GitHubList = "https://api.github.com/repos/KSP1Retooled/retooled/releases?per_page=100";
    const string GitCodeList =
        "https://api.gitcode.com/api/v5/repos/ksp1retooled/retooled/releases?per_page=100&page=1";

    const string GitCodeJson = """
        [ { "tag_name": "1.13.0-rc6", "name": "RC 6", "prerelease": true, "body": "notes",
            "created_at": "2026-11-11T19:02:45+08:00", "release_status": "pre",
            "assets": [
              { "browser_download_url": "https://raw.gitcode.com/ksp1retooled/retooled/archive/refs/heads/1.13.0-rc6.zip",
                "name": "1.13.0-rc6.zip", "type": "source" },
              { "browser_download_url": "https://gitcode.com/ksp1retooled/retooled/releases/download/1.13.0-rc6/retooled-windows.rtpatch",
                "name": "retooled-windows.rtpatch", "type": "attach", "id": 1, "size": 3, "digest": "sha256:ABC" }
            ] } ]
        """;

    [Theory]
    [InlineData("KSP1Retooled/retooled", "ksp1retooled/retooled")]
    [InlineData("ksp1retooled/RETOOLED", "ksp1retooled/retooled")]
    [InlineData("o/r", null)]
    public void OnlyTheDefaultRepositoryHasAMirror(string repository, string? mirror) =>
        Assert.Equal(mirror, LauncherInfo.MirrorOf(repository));

    [Fact]
    public void AChannelWithoutAMirrorIsListedFromGitHubAlone()
    {
        var client = new HttpClient(new FakeHttp());
        Assert.IsType<GitHubReleaseSource>(MirroredReleaseSource.For(client, "o/r"));
        Assert.IsType<MirroredReleaseSource>(
            MirroredReleaseSource.For(client, LauncherInfo.DebugChannel)
        );
    }

    [Fact]
    public async Task ReadsGitCodeReleasesAsTheChannelTheyMirror()
    {
        var http = new FakeHttp();
        http.Json(GitCodeList, GitCodeJson);

        var release = Assert.Single(
            await new GitCodeReleaseSource(
                new HttpClient(http),
                LauncherInfo.DebugChannel,
                LauncherInfo.DefaultMirror
            ).GetReleasesAsync()
        );

        Assert.Equal(LauncherInfo.DebugChannel, release.Channel);
        Assert.True(release.Prerelease);
        Assert.Equal("https://gitcode.com/ksp1retooled/retooled/releases/1.13.0-rc6", release.PageUrl);
        var asset = Assert.Single(release.Assets);
        Assert.Equal("retooled-windows.rtpatch", asset.Name);
        Assert.Equal(3, asset.Size);
        Assert.Equal("abc", asset.Sha256);
    }

    [Fact]
    public async Task ListsFromGitHubWhenItAnswers()
    {
        var http = new FakeHttp();
        http.Json(GitHubList, """[ { "tag_name": "v1", "assets": [] } ]""");
        http.Json(GitCodeList, GitCodeJson);

        var releases = await MirroredReleaseSource
            .For(new HttpClient(http), LauncherInfo.DefaultRepository)
            .GetReleasesAsync();

        Assert.Equal(["v1"], releases.Select(r => r.Tag));
        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.Host.EndsWith("gitcode.com"));
    }

    [Fact]
    public async Task ListsFromTheMirrorWhenGitHubFails()
    {
        var http = new FakeHttp();
        http.Routes[GitHubList] = () => throw new HttpRequestException("no route to host");
        http.Json(GitCodeList, GitCodeJson);
        var logged = new List<string>();

        var releases = await MirroredReleaseSource
            .For(new HttpClient(http), LauncherInfo.DefaultRepository, log: logged.Add)
            .GetReleasesAsync();

        Assert.Equal(["1.13.0-rc6"], releases.Select(r => r.Tag));
        Assert.All(releases, r => Assert.Equal(LauncherInfo.DefaultRepository, r.Channel));
        Assert.Contains(logged, l => l.Contains("no route to host"));
    }

    [Fact]
    public async Task ReportsBothFailures()
    {
        var http = new FakeHttp();
        http.Routes[GitHubList] = () => throw new HttpRequestException("no route to host");

        var error = await Assert.ThrowsAsync<LauncherException>(() =>
            MirroredReleaseSource
                .For(new HttpClient(http), LauncherInfo.DefaultRepository)
                .GetReleasesAsync()
        );

        Assert.Contains("no route to host", error.Message);
        Assert.Contains("gitcode.com/ksp1retooled/retooled", error.Message);
    }

    [Fact]
    public async Task PreferringGitCodeListsFromItFirst()
    {
        var http = new FakeHttp();
        http.Json(GitHubList, """[ { "tag_name": "v1", "assets": [] } ]""");
        http.Json(GitCodeList, GitCodeJson);

        var releases = await MirroredReleaseSource
            .For(new HttpClient(http), LauncherInfo.DefaultRepository, preferred: ReleaseMirror.GitCode)
            .GetReleasesAsync();

        Assert.Equal(["1.13.0-rc6"], releases.Select(r => r.Tag));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task PreferringGitCodeFallsBackToGitHub()
    {
        var http = new FakeHttp();
        http.Json(GitHubList, """[ { "tag_name": "v1", "assets": [] } ]""");

        var releases = await MirroredReleaseSource
            .For(new HttpClient(http), LauncherInfo.DefaultRepository, preferred: ReleaseMirror.GitCode)
            .GetReleasesAsync();

        Assert.Equal(["v1"], releases.Select(r => r.Tag));
    }

    [Fact]
    public void AGitCodeAssetFallsBackToGitHubsCopy()
    {
        var source = MirroredReleaseSource.For(
            new HttpClient(new FakeHttp()),
            LauncherInfo.DefaultRepository,
            preferred: ReleaseMirror.GitCode
        );
        var asset = new ReleaseAsset(
            "retooled-linux.rtpatch",
            1,
            "https://gitcode.com/ksp1retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
            "https://gitcode.com/ksp1retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
            null
        );
        var release = new Release("v1", "", false, default, "", "", [asset]);

        Assert.Equal(
            [
                "https://gitcode.com/ksp1retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                "https://github.com/KSP1Retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
            ],
            source.AssetRequests(release, asset).Select(r => r.RequestUri!.ToString())
        );
    }

    [Fact]
    public async Task CancellingDoesNotTurnToTheMirror()
    {
        using var cancel = new CancellationTokenSource();
        var http = new FakeHttp();
        http.Routes[GitHubList] = () =>
        {
            cancel.Cancel();
            throw new TaskCanceledException();
        };
        http.Json(GitCodeList, GitCodeJson);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MirroredReleaseSource
                .For(new HttpClient(http), LauncherInfo.DefaultRepository)
                .GetReleasesAsync(cancel.Token)
        );
        Assert.Single(http.Requests);
    }
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
    public async Task DownloadsFromTheMirrorWhenGitHubFails()
    {
        byte[] bytes = [1, 2, 3];
        var http = new FakeHttp();
        http.Routes["https://github.com/KSP1Retooled/retooled/releases/download/v1/retooled-linux.rtpatch"] =
            () => throw new HttpRequestException("connection reset");
        http.Bytes(
            "https://gitcode.com/ksp1retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
            bytes
        );
        var client = new HttpClient(http);
        var cache = new PatchCache(
            client,
            MirroredReleaseSource.For(client, LauncherInfo.DefaultRepository, token: "secret"),
            root
        );
        var release = new Release(
            "v1",
            "",
            false,
            DateTimeOffset.UnixEpoch,
            "",
            "",
            [
                new ReleaseAsset(
                    "retooled-linux.rtpatch",
                    bytes.Length,
                    "https://github.com/KSP1Retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                    "https://github.com/KSP1Retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                    Convert.ToHexStringLower(SHA256.HashData(bytes))
                ),
            ]
        ) { Channel = LauncherInfo.DefaultRepository };

        string path = await cache.GetPatchAsync(release, "linux", null, default);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(2, http.Requests.Count);
        // the GitHub token stays with GitHub
        Assert.Null(http.Requests[1].Headers.Authorization);
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

        cache.Prune([kept]);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public async Task TheSameTagInTwoChannelsDoesNotShareACacheEntry()
    {
        var http = new FakeHttp();
        http.Bytes("https://dl/v1", [1, 2, 3]);
        var client = new HttpClient(http);
        var cache = new PatchCache(client, new GitHubReleaseSource(client, "o/r"), root);
        var release = WithPatch("v1", [1, 2, 3], digest: false);

        string first = await cache.GetPatchAsync(
            release with { Channel = "a/one" },
            "linux",
            null,
            default
        );
        string second = await cache.GetPatchAsync(
            release with { Channel = "a/two" },
            "linux",
            null,
            default
        );

        Assert.NotEqual(first, second);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task ImportKeepsACopyPerContent()
    {
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "mine.rtpatch");
        var client = new HttpClient(new FakeHttp());
        var cache = new PatchCache(
            client,
            new GitHubReleaseSource(client, "o/r"),
            Path.Combine(root, "cache")
        );

        File.WriteAllBytes(file, [1, 2, 3]);
        string first = await cache.ImportAsync(file, null, default);
        Assert.Equal(first, await cache.ImportAsync(file, null, default));

        // a rebuild under the same name must leave the copy of the installed patch alone
        File.WriteAllBytes(file, [4, 5, 6]);
        string second = await cache.ImportAsync(file, null, default);
        File.Delete(file);

        Assert.NotEqual(first, second);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(first));
        Assert.Equal([4, 5, 6], File.ReadAllBytes(second));

        cache.Prune([second]);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
    }
}

public sealed class LauncherSettingsTests : IDisposable
{
    readonly string file = Path.Combine(
        Path.GetTempPath(),
        "retooled-settings-" + Guid.NewGuid().ToString("N") + ".json"
    );

    public void Dispose() => File.Delete(file);

    [Fact]
    public void OffersTheDefaultChannelFirstAndOnlyOnce() =>
        Assert.Equal(
            [LauncherInfo.DefaultRepository, LauncherInfo.DebugChannel, "a/b"],
            new LauncherSettings
            {
                Channels = ["a/b", "A/B", LauncherInfo.DefaultRepository],
            }.AllChannels
        );

    [Fact]
    public void ThePreferredMirrorIsSavedByName()
    {
        new LauncherSettings { PreferredMirror = ReleaseMirror.GitCode }.Save(file);
        Assert.Contains("\"GitCode\"", File.ReadAllText(file));
        Assert.Equal(ReleaseMirror.GitCode, LauncherSettings.Load(file).PreferredMirror);
        File.WriteAllText(file, "{}");
        Assert.Equal(ReleaseMirror.GitHub, LauncherSettings.Load(file).PreferredMirror);
    }

    [Fact]
    public void ARepositoryFromBeforeChannelsIsStillOffered()
    {
        File.WriteAllText(file, """{ "repository": "o/r" }""");
        Assert.Equal(
            [LauncherInfo.DefaultRepository, LauncherInfo.DebugChannel, "o/r"],
            LauncherSettings.Load(file).AllChannels
        );
    }

    [Fact]
    public void ChannelsSurviveASave()
    {
        new LauncherSettings { Channels = ["a/b"], Repository = "a/b" }.Save(file);
        var loaded = LauncherSettings.Load(file);
        Assert.Equal(["a/b"], loaded.Channels);
        Assert.Equal("a/b", loaded.Repository);
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

    [Fact]
    public void ReadsLegacySteamLibraryPaths()
    {
        const string vdf = """
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
            InstallLocator.ParseLibraryFolders(vdf)
        );
    }

    [Fact]
    public void ReadsHeroicGogInstalls()
    {
        const string json = """
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
            InstallLocator.ParseHeroicInstalls(json, "1429864849")
        );
        Assert.Empty(InstallLocator.ParseHeroicInstalls("not json", "1429864849"));
        Assert.Empty(InstallLocator.ParseHeroicInstalls("[]", "1429864849"));
    }

    [Fact]
    public void MapsWindowsPathsIntoWinePrefixes()
    {
        const string prefix = "/home/kerbal/.wine";
        Assert.Equal(
            Path.Combine(prefix, "drive_c", "Program Files (x86)", "Steam"),
            InstallLocator.WindowsPathInPrefix(prefix, @"C:\Program Files (x86)\Steam")
        );
        Assert.Equal(
            Path.Combine(prefix, "dosdevices", "d:", "SteamLibrary"),
            InstallLocator.WindowsPathInPrefix(prefix, @"D:\SteamLibrary")
        );
        Assert.Equal(
            Path.Combine("/", "mnt", "games"),
            InstallLocator.WindowsPathInPrefix(prefix, "z:/mnt/games/")
        );
        Assert.Null(InstallLocator.WindowsPathInPrefix(prefix, "/mnt/games"));
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
