using KSP.Retooled.Launcher.Exceptions;
using KSP.Retooled.Launcher.Releases;
using KSP.Retooled.Launcher.Releases.GitHub;
using KSP.Retooled.Launcher.Tests.Mocking;
using KSP.Retooled.Launcher.Utils;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

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
