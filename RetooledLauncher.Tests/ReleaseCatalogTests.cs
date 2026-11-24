using KSP.Retooled.Launcher.Releases;
using KSP.Retooled.Launcher.Utils;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

public sealed class ReleaseCatalogTests
{
    private static readonly DateTimeOffset Day0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static Release Make(string tag,
                                 bool prerelease = false,
                                 int day = 0,
                                 params string[] platforms)
    {
        return new Release(tag,
                           string.Empty,
                           prerelease,
                           Day0.AddDays(day),
                           string.Empty,
                           string.Empty,
                           [
                               ..(platforms.Length is 0
                                      ? ["windows", "linux", "macos"]
                                      : platforms).Select(p => new ReleaseAsset(Release.PatchName(p),
                                                                                10,
                                                                                $"https://example/{tag}/{p}",
                                                                                $"https://api/{tag}/{p}",
                                                                                null))
                           ]);
    }

    [Theory, InlineData("0.9.0", "v1.0.0"), InlineData("1.0.0", null), InlineData("1.0.1", null), InlineData("1.1.0-beta.1", "v1.1.0-beta.2"), InlineData("1.1.0-beta.2", null), InlineData("0.9.0-beta.1", "v1.1.0-beta.2"), InlineData("not-a-version", null)]
    // the newest stable one, not the pre-release above it
    // a build newer than anything published
    // already on a pre-release, so offered the next
    // need not be a listed release
    public void NewerThanLooksPastPrereleasesUnlessOnOne(string version, string? expected)
    {
        ReleaseCatalog catalog = new([
            Make("v0.9.0", day: 0),
            Make("v1.0.0", day: 1),
            Make("v1.1.0-beta.1", true, 2),
            Make("v1.1.0-beta.2", true, 3),
            Make("nightly", day: 9)
        ]);

        Assert.Equal(expected, catalog.NewerThan(version)?.Tag);
    }

    // 1.1.0-beta.1 came out after 1.0.0 but is a pre-release; 0.9.0 is older than both
    private static ReleaseCatalog Catalog() => new([
        Make("v0.9.0", day: 0),
        Make("v1.1.0-beta.1", true, 20),
        Make("v1.0.0", day: 10)
    ]);

    [Fact]
    public void ListsNewestFirst() => Assert.Equal(["v1.1.0-beta.1", "v1.0.0", "v0.9.0"], Catalog().All.Select(r => r.Tag));

    [Fact]
    public void HidesPrereleasesUnlessOptedIn()
    {
        Assert.Equal(["v1.0.0", "v0.9.0"],
                     Catalog().Visible(false).Select(r => r.Tag));

        Assert.Equal(["v1.1.0-beta.1", "v1.0.0", "v0.9.0"],
                     Catalog().Visible(true).Select(r => r.Tag));
    }

    [Fact]
    public void AnInstalledPrereleaseStaysListed() => Assert.Contains("v1.1.0-beta.1",
                                                                      Catalog()
                                                                         .Visible(false, "v1.1.0-beta.1")
                                                                         .Select(r => r.Tag));

    [Fact]
    public void LatestHonoursTheOptIn()
    {
        Assert.Equal("v1.0.0", Catalog().Latest("windows", false)?.Tag);
        Assert.Equal("v1.1.0-beta.1", Catalog().Latest("windows", true)?.Tag);
    }

    [Fact]
    public void LatestSkipsReleasesWithoutABuildForThePlatform()
    {
        ReleaseCatalog catalog = new([
            Make("v1.0.0"),
            Make("v1.1.0", day: 1, platforms: "windows")
        ]);

        Assert.Equal("v1.1.0", catalog.Latest("windows", false)?.Tag);
        Assert.Equal("v1.0.0", catalog.Latest("linux", false)?.Tag);
    }

    [Fact]
    public void StableUsersAreNotOfferedPrereleaseUpgrades()
    {
        Assert.Null(Catalog().UpgradeFor("v1.0.0", "windows", false));
        Assert.Equal("v1.1.0-beta.1",
                     Catalog().UpgradeFor("v1.0.0", "windows", true)?.Tag);
    }

    [Fact]
    public void OffersPrereleasesWhenThereIsNothingElse()
    {
        ReleaseCatalog catalog = new([
            Make("v1.0.0-beta.1", true),
            Make("v1.0.0-beta.2", true, 1)
        ]);

        Assert.True(catalog.OnlyPrereleases);
        Assert.Equal(["v1.0.0-beta.2", "v1.0.0-beta.1"],
                     catalog.Visible(false).Select(r => r.Tag));

        Assert.Equal("v1.0.0-beta.2", catalog.Latest("windows", false)?.Tag);
        Assert.Equal("v1.0.0-beta.2",
                     catalog.UpgradeFor("v1.0.0-beta.1", "windows", false)?.Tag);

        Assert.False(Catalog().OnlyPrereleases);
        Assert.False(ReleaseCatalog.Empty.OnlyPrereleases);
    }

    [Fact]
    public void APrereleaseUserIsOfferedTheStableReleaseThatSupersedesIt()
    {
        ReleaseCatalog catalog = new([
            Make("v1.1.0-beta.1", true),
            Make("v1.1.0", day: 5)
        ]);

        Assert.Equal("v1.1.0",
                     catalog.UpgradeFor("v1.1.0-beta.1", "windows", false)?.Tag);
    }

    [Fact]
    public void ActionsCompareAgainstTheInstalledRelease()
    {
        ReleaseCatalog catalog = Catalog();
        Release stable = catalog.Find("v1.0.0")!;
        Assert.Equal(InstallAction.INSTALL, catalog.ActionFor(stable, null));
        Assert.Equal(InstallAction.UPGRADE, catalog.ActionFor(stable, "v0.9.0"));
        Assert.Equal(InstallAction.DOWNGRADE, catalog.ActionFor(stable, "v1.1.0-beta.1"));
        Assert.Equal(InstallAction.REINSTALL, catalog.ActionFor(stable, "v1.0.0"));
        Assert.Equal(InstallAction.REPLACE, catalog.ActionFor(stable, string.Empty));
        // a release since deleted from GitHub is still compared by version
        Assert.Equal(InstallAction.DOWNGRADE, catalog.ActionFor(stable, "v2.0.0"));
    }

    [Fact]
    public void OrdersByCreationDateRatherThanVersion()
    {
        // a maintenance release of an older line, created after the newer line's release
        ReleaseCatalog catalog = new([
            Make("v2.0.0", day: 10),
            Make("v1.9.1", day: 12),
            Make("old-build", day: 1),
            Make("v1.0.0", day: 0)
        ]);

        Assert.Equal(["v1.9.1", "v2.0.0", "old-build", "v1.0.0"], catalog.All.Select(r => r.Tag));
    }

    [Fact]
    public void OrdersByCreationDateRatherThanPublishDate()
    {
        // v1.1.0 was drafted first and published after v1.0.1
        ReleaseCatalog catalog = new([
            Make("v1.1.0", day: 9) with { Created = Day0.AddDays(1) },
            Make("v1.0.1", day: 5)
        ]);

        Assert.Equal(["v1.0.1", "v1.1.0"], catalog.All.Select(r => r.Tag));
    }

    private static Release Built(string tag, string sha256, int day = 0)
    {
        Release release = Make(tag, true, day);
        return release with { Assets = [..release.Assets.Select(a => a with { Sha256 = sha256 })] };
    }

    [Fact]
    public void SeparateBuildsAreNotVersionsOfEachOther()
    {
        ReleaseCatalog catalog = new([Built("pr-1", "aa"), Built("pr-2", "bb", 1)], false);
        Assert.Equal(InstallAction.REPLACE, catalog.ActionFor(catalog.Find("pr-2")!, "pr-1", "aa"));
        Assert.Equal(InstallAction.REPLACE, catalog.ActionFor(catalog.Find("pr-1")!, "pr-2", "bb"));
        Assert.Null(catalog.Latest("windows", false));
        Assert.Null(catalog.UpgradeFor("pr-1", "windows", false, "aa"));
        Assert.Null(catalog.UpgradeFor(null, "windows", false));
    }

    [Fact]
    public void ANewBuildOfTheInstalledReleaseIsAnUpdate()
    {
        ReleaseCatalog catalog = new([Built("pr-1", "bb"), Built("pr-2", "cc", 1)], false);
        Release installed = catalog.Find("pr-1")!;
        Assert.Equal(InstallAction.UPDATE, catalog.ActionFor(installed, "pr-1", "aa"));
        Assert.Same(installed, catalog.UpgradeFor("pr-1", "windows", false, "aa"));
        Assert.Equal(InstallAction.REINSTALL, catalog.ActionFor(installed, "pr-1", "BB"));
        Assert.Null(catalog.UpgradeFor("pr-1", "windows", false, "bb"));
        // installed by an older launcher, or from a release that published no digests
        Assert.Equal(InstallAction.REINSTALL, catalog.ActionFor(installed, "pr-1"));
        Assert.Equal(InstallAction.REINSTALL, catalog.ActionFor(Make("pr-1"), "pr-1", "aa"));
    }

    [Fact]
    public void ANewerVersionComesBeforeANewBuildOfTheInstalledOne()
    {
        ReleaseCatalog catalog = new([Built("v1.0.0", "bb"), Built("v1.1.0", "cc", 1)]);
        Assert.Equal("v1.1.0", catalog.UpgradeFor("v1.0.0", "windows", true, "aa")?.Tag);
        Assert.Equal("v1.1.0", catalog.UpgradeFor("v1.1.0", "windows", true, "aa")?.Tag);
        Assert.Null(catalog.UpgradeFor("v1.1.0", "windows", true, "cc"));
    }

    [Fact]
    public void OnlyTheExperimentalChannelHoldsSeparateBuilds()
    {
        Assert.False(LauncherInfo.IsVersionedChannel(LauncherInfo.EXPERIMENTAL_CHANNEL));
        Assert.False(LauncherInfo.IsVersionedChannel(LauncherInfo.EXPERIMENTAL_CHANNEL + LauncherInfo.DEBUG_SUFFIX));
        Assert.True(LauncherInfo.IsVersionedChannel(LauncherInfo.DEFAULT_REPOSITORY));
        Assert.True(LauncherInfo.IsVersionedChannel("o/r"));
    }

    [Fact]
    public void NonVersionTagsAreOrderedByCreationDate()
    {
        ReleaseCatalog catalog = new([
            Make("nightly-b", day: 2),
            Make("v1.0.0", day: 1),
            Make("nightly-c", day: 3)
        ]);

        Assert.Equal(["nightly-c", "nightly-b", "v1.0.0"], catalog.All.Select(r => r.Tag));
        Assert.Equal(InstallAction.UPGRADE,
                     catalog.ActionFor(catalog.Find("nightly-c")!, "nightly-b"));
    }
}
