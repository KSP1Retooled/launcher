using KSP.Retooled.Launcher.Releases;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

public sealed class ReleaseVersionTests
{
    [Fact]
    public void OrdersBySemverPrecedence()
    {
        string[] ascending =
        [
            "0.9.10",
            "v1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "v1.0.0",
            "1.0.1",
            "1.2",
            "1.10.0",
        ];
        var parsed = ascending
            .Select(t => ReleaseVersion.TryParse(t, out var v) ? v : throw new Exception(t))
            .ToList();
        for (int i = 1; i < parsed.Count; i++)
            Assert.True(
                parsed[i - 1].CompareTo(parsed[i]) < 0,
                $"{ascending[i - 1]} < {ascending[i]}"
            );
    }

    [Fact]
    public void BuildMetadataAndPrefixDoNotChangeTheVersion()
    {
        Assert.True(ReleaseVersion.TryParse("v1.2.3+abc", out var a));
        Assert.True(ReleaseVersion.TryParse("1.2.3", out var b));
        Assert.Equal(0, a.CompareTo(b));
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.0")]
    [InlineData("1.0.0-")]
    [InlineData("")]
    public void RejectsTagsThatAreNotVersions(string tag) =>
        Assert.False(ReleaseVersion.TryParse(tag, out _));
}

public sealed class ReleaseCatalogTests
{
    static readonly DateTimeOffset Day0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static Release Make(
        string tag,
        bool prerelease = false,
        int day = 0,
        params string[] platforms
    ) =>
        new(
            tag,
            "",
            prerelease,
            Day0.AddDays(day),
            "",
            "",
            [
                .. (platforms.Length == 0 ? ["windows", "linux", "macos"] : platforms).Select(
                    p => new ReleaseAsset(
                        Release.PatchName(p),
                        10,
                        $"https://example/{tag}/{p}",
                        $"https://api/{tag}/{p}",
                        null
                    )
                ),
            ]
        );

    // 1.1.0-beta.1 came out after 1.0.0 but is a pre-release; 0.9.0 is older than both
    static ReleaseCatalog Catalog() =>
        new([
            Make("v0.9.0", day: 0),
            Make("v1.1.0-beta.1", prerelease: true, day: 20),
            Make("v1.0.0", day: 10),
        ]);

    [Fact]
    public void ListsNewestFirst() =>
        Assert.Equal(["v1.1.0-beta.1", "v1.0.0", "v0.9.0"], Catalog().All.Select(r => r.Tag));

    [Fact]
    public void HidesPrereleasesUnlessOptedIn()
    {
        Assert.Equal(
            ["v1.0.0", "v0.9.0"],
            Catalog().Visible(includePrereleases: false).Select(r => r.Tag)
        );
        Assert.Equal(
            ["v1.1.0-beta.1", "v1.0.0", "v0.9.0"],
            Catalog().Visible(includePrereleases: true).Select(r => r.Tag)
        );
    }

    [Fact]
    public void AnInstalledPrereleaseStaysListed() =>
        Assert.Contains(
            "v1.1.0-beta.1",
            Catalog()
                .Visible(includePrereleases: false, installedTag: "v1.1.0-beta.1")
                .Select(r => r.Tag)
        );

    [Fact]
    public void LatestHonoursTheOptIn()
    {
        Assert.Equal("v1.0.0", Catalog().Latest("windows", includePrereleases: false)?.Tag);
        Assert.Equal("v1.1.0-beta.1", Catalog().Latest("windows", includePrereleases: true)?.Tag);
    }

    [Fact]
    public void LatestSkipsReleasesWithoutABuildForThePlatform()
    {
        var catalog = new ReleaseCatalog([
            Make("v1.0.0"),
            Make("v1.1.0", day: 1, platforms: "windows"),
        ]);
        Assert.Equal("v1.1.0", catalog.Latest("windows", false)?.Tag);
        Assert.Equal("v1.0.0", catalog.Latest("linux", false)?.Tag);
    }

    [Fact]
    public void StableUsersAreNotOfferedPrereleaseUpgrades()
    {
        Assert.Null(Catalog().UpgradeFor("v1.0.0", "windows", includePrereleases: false));
        Assert.Equal(
            "v1.1.0-beta.1",
            Catalog().UpgradeFor("v1.0.0", "windows", includePrereleases: true)?.Tag
        );
    }

    [Fact]
    public void APrereleaseUserIsOfferedTheStableReleaseThatSupersedesIt()
    {
        var catalog = new ReleaseCatalog([
            Make("v1.1.0-beta.1", prerelease: true),
            Make("v1.1.0", day: 5),
        ]);
        Assert.Equal(
            "v1.1.0",
            catalog.UpgradeFor("v1.1.0-beta.1", "windows", includePrereleases: false)?.Tag
        );
    }

    [Fact]
    public void ActionsCompareAgainstTheInstalledRelease()
    {
        var catalog = Catalog();
        var stable = catalog.Find("v1.0.0")!;
        Assert.Equal(InstallAction.Install, catalog.ActionFor(stable, installedTag: null));
        Assert.Equal(InstallAction.Upgrade, catalog.ActionFor(stable, "v0.9.0"));
        Assert.Equal(InstallAction.Downgrade, catalog.ActionFor(stable, "v1.1.0-beta.1"));
        Assert.Equal(InstallAction.Reinstall, catalog.ActionFor(stable, "v1.0.0"));
        Assert.Equal(InstallAction.Replace, catalog.ActionFor(stable, ""));
        // a release since deleted from GitHub is still compared by version
        Assert.Equal(InstallAction.Downgrade, catalog.ActionFor(stable, "v2.0.0"));
    }

    [Fact]
    public void AStrayTagDoesNotUnsortTheVersionedReleases()
    {
        // a maintenance release of an older line, published after the newer line's release
        var catalog = new ReleaseCatalog([
            Make("v2.0.0", day: 10),
            Make("v1.9.1", day: 12),
            Make("old-build", day: 1),
            Make("v1.0.0", day: 0),
        ]);
        Assert.Equal(["v2.0.0", "v1.9.1", "old-build", "v1.0.0"], catalog.All.Select(r => r.Tag));
    }

    [Fact]
    public void NonVersionTagsAreOrderedByPublishDate()
    {
        var catalog = new ReleaseCatalog([
            Make("nightly-b", day: 2),
            Make("v1.0.0", day: 1),
            Make("nightly-c", day: 3),
        ]);
        Assert.Equal(["nightly-c", "nightly-b", "v1.0.0"], catalog.All.Select(r => r.Tag));
        Assert.Equal(
            InstallAction.Upgrade,
            catalog.ActionFor(catalog.Find("nightly-c")!, "nightly-b")
        );
    }
}
