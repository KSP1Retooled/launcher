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
            "1.10.0"
        ];

        List<ReleaseVersion> parsed =
        [
            ..ascending
               .Select(t => ReleaseVersion.TryParse(t, out ReleaseVersion v) ? v : throw new Exception(t))
        ];

        for (int i = 1; i < parsed.Count; i++)
        {
            Assert.True(parsed[i - 1].CompareTo(parsed[i]) < 0,
                        $"{ascending[i - 1]} < {ascending[i]}");
        }
    }

    [Fact]
    public void BuildMetadataAndPrefixDoNotChangeTheVersion()
    {
        Assert.True(ReleaseVersion.TryParse("v1.2.3+abc", out ReleaseVersion a));
        Assert.True(ReleaseVersion.TryParse("1.2.3", out ReleaseVersion b));
        Assert.Equal(0, a.CompareTo(b));
    }

    [Theory, InlineData("nightly"), InlineData("1"), InlineData("1.2.3.4"), InlineData("1.x.0"), InlineData("1.0.0-"), InlineData("")]
    public void RejectsTagsThatAreNotVersions(string tag) => Assert.False(ReleaseVersion.TryParse(tag, out _));
}
