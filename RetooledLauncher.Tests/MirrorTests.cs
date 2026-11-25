using KSP.Retooled.Launcher.Common.Exceptions;
using KSP.Retooled.Launcher.Releases;
using KSP.Retooled.Launcher.Releases.GitCode;
using KSP.Retooled.Launcher.Releases.GitHub;
using KSP.Retooled.Launcher.Settings;
using KSP.Retooled.Launcher.Tests.Mocking;
using KSP.Retooled.Launcher.Utils;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

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
    public void OnlyTheDEFAULT_REPOSITORYHasAMirror(string repository, string? mirror) => Assert.Equal(mirror, LauncherInfo.MirrorOf(repository));

    [Fact]
    public void AChannelWithoutAMirrorIsListedFromGitHubAlone()
    {
        HttpClient client = new(new FakeHttp());
        Assert.IsType<GitHubReleaseSource>(MirroredReleaseSource.For(client, "o/r"));
        Assert.IsType<MirroredReleaseSource>(MirroredReleaseSource.For(client, LauncherInfo.DEBUG_CHANNEL));
    }

    [Fact]
    public async Task ReadsGitCodeReleasesAsTheChannelTheyMirror()
    {
        FakeHttp http = new();
        http.Json(GIT_CODE_LIST, GIT_CODE_JSON);

        Release release = Assert.Single(await new GitCodeReleaseSource(new HttpClient(http),
                                                                       LauncherInfo.DEBUG_CHANNEL,
                                                                       LauncherInfo.DEFAULT_MIRROR).GetReleasesAsync());

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
                                                                                     .GetReleasesAsync());

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
                                               .For(new HttpClient(http), LauncherInfo.DEFAULT_REPOSITORY, preferred: ReleaseMirror.GIT_CODE)
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
                                               .For(new HttpClient(http), LauncherInfo.DEFAULT_REPOSITORY, preferred: ReleaseMirror.GIT_CODE)
                                               .GetReleasesAsync();

        Assert.Equal(["v1"], releases.Select(r => r.Tag));
    }

    [Fact]
    public void AGitCodeAssetFallsBackToGitHubsCopy()
    {
        IReleaseSource source = MirroredReleaseSource.For(new HttpClient(new FakeHttp()),
                                                          LauncherInfo.DEFAULT_REPOSITORY,
                                                          preferred: ReleaseMirror.GIT_CODE);

        ReleaseAsset asset = new("retooled-linux.rtpatch",
                                 1,
                                 "https://gitcode.com/ksp1retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                                 "https://gitcode.com/ksp1retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                                 null);

        Release release = new("v1", string.Empty, false, default, string.Empty, string.Empty, [asset]);

        Assert.Equal([
                         "https://gitcode.com/ksp1retooled/retooled/releases/download/v1/retooled-linux.rtpatch",
                         "https://github.com/KSP1Retooled/retooled/releases/download/v1/retooled-linux.rtpatch"
                     ],
                     source.AssetRequests(release, asset).Select(r => r.RequestUri!.ToString()));
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
                                                                       .GetReleasesAsync(cancel.Token));

        Assert.Single(http.requests);
    }
}
