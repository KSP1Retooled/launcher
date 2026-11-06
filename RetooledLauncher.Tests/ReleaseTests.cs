using KSP.Retooled.Launcher.Releases;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

public sealed class ReleaseLinkTests
{
    [Theory]
    [InlineData("https://github.com/KSP1Retooled/launcher/pull/9", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("HTTPS://EXAMPLE.COM/", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData(@"C:\Windows\System32\calc.exe", false)]
    [InlineData(@"\\host\share\run.exe", false)]
    [InlineData("/usr/bin/xcalc", false)]
    [InlineData("ms-settings:", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("relative/page", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OpensOnlyWebLinks(string? url, bool expected) =>
        Assert.Equal(expected, Release.IsWebLink(url));
}

public sealed class ReleaseNotesTests
{
    const string Url = "https://github.com/KSP1Retooled/launcher/pull/9";
    const string Linked = $"[{Url}]({Url})";

    [Theory]
    [InlineData($"* Fix it by @someone in {Url}", $"* Fix it by @someone in {Linked}")]
    [InlineData($"{Url}\r\nnext", $"{Linked}\r\nnext")]
    [InlineData($"<{Url}>", Linked)]
    [InlineData($"See {Url}.", $"See {Linked}.")]
    [InlineData($"See {Url}, then", $"See {Linked}, then")]
    [InlineData($"(see {Url})", $"(see {Linked})")]
    [InlineData($"**{Url}**", $"**{Linked}**")]
    [InlineData(
        "**Full Changelog**: https://github.com/a/b/compare/v0.1.3...v0.1.4",
        "**Full Changelog**: [https://github.com/a/b/compare/v0.1.3...v0.1.4](https://github.com/a/b/compare/v0.1.3...v0.1.4)"
    )]
    [InlineData("https://a.example/x_(y)", @"[https://a.example/x\_(y)](https://a.example/x_(y))")]
    [InlineData("https://a.example/a*b~c", @"[https://a.example/a\*b\~c](https://a.example/a*b~c)")]
    public void LinksUrls(string notes, string expected) =>
        Assert.Equal(expected, ReleaseNotes.LinkUrls(notes));

    [Theory]
    [InlineData("")]
    [InlineData("no links here")]
    [InlineData(Linked)]
    [InlineData($"[the pull request]({Url})")]
    [InlineData($"![screenshot]({Url}.png)")]
    [InlineData($"[![badge]({Url}.svg)]({Url})")]
    [InlineData($"[ref]: {Url}")]
    [InlineData($"`{Url}`")]
    [InlineData($"``a ` {Url}``")]
    [InlineData($"```\n{Url}\n```")]
    [InlineData($"~~~sh\r\ncurl {Url}\r\n~~~\r\n")]
    [InlineData($"```\n{Url}")]
    [InlineData($"    {Url}")]
    [InlineData("ftp://example.com/file")]
    [InlineData("https://.")]
    public void LeavesOtherTextAlone(string notes) =>
        Assert.Equal(notes, ReleaseNotes.LinkUrls(notes));

    [Fact]
    public void LinksTextAroundCode()
    {
        var notes = $"```\n{Url}\n```\n\n{Url} and `{Url}` and {Url}";
        Assert.Equal(
            $"```\n{Url}\n```\n\n{Linked} and `{Url}` and {Linked}",
            ReleaseNotes.LinkUrls(notes)
        );
    }
}

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

    [Theory]
    [InlineData("0.9.0", "v1.0.0")] // the newest stable one, not the pre-release above it
    [InlineData("1.0.0", null)]
    [InlineData("1.0.1", null)] // a build newer than anything published
    [InlineData("1.1.0-beta.1", "v1.1.0-beta.2")] // already on a pre-release, so offered the next
    [InlineData("1.1.0-beta.2", null)]
    [InlineData("0.9.0-beta.1", "v1.1.0-beta.2")] // need not be a listed release
    [InlineData("not-a-version", null)]
    public void NewerThanLooksPastPrereleasesUnlessOnOne(string version, string? expected)
    {
        var catalog = new ReleaseCatalog([
            Make("v0.9.0"),
            Make("v1.0.0"),
            Make("v1.1.0-beta.1", prerelease: true),
            Make("v1.1.0-beta.2", prerelease: true),
            Make("nightly", day: 9),
        ]);
        Assert.Equal(expected, catalog.NewerThan(version)?.Tag);
    }

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
