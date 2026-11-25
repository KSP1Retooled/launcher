using KSP.Retooled.Launcher.Releases;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

public sealed class ReleaseNotesTests
{
    private const string URL = "https://github.com/KSP1Retooled/launcher/pull/9";
    private const string LINKED = $"[{URL}]({URL})";

    [Theory, InlineData($"* Fix it by @someone in {URL}", $"* Fix it by @someone in {LINKED}"), InlineData($"{URL}\r\nnext", $"{LINKED}\r\nnext"), InlineData($"<{URL}>", LINKED), InlineData($"See {URL}.", $"See {LINKED}."), InlineData($"See {URL}, then", $"See {LINKED}, then"), InlineData($"(see {URL})", $"(see {LINKED})"), InlineData($"**{URL}**", $"**{LINKED}**"), InlineData("**Full Changelog**: https://github.com/a/b/compare/v0.1.3...v0.1.4",
                                                                                                                                                                                                                                                                                                                                                                                            "**Full Changelog**: [https://github.com/a/b/compare/v0.1.3...v0.1.4](https://github.com/a/b/compare/v0.1.3...v0.1.4)"), InlineData("https://a.example/x_(y)", @"[https://a.example/x\_(y)](https://a.example/x_(y))"), InlineData("https://a.example/a*b~c", @"[https://a.example/a\*b\~c](https://a.example/a*b~c)")]
    public void LinksUrls(string notes, string expected) => Assert.Equal(expected, ReleaseNotes.LinkUrls(notes));

    [Theory, InlineData(""), InlineData("no links here"), InlineData(LINKED), InlineData($"[the pull request]({URL})"), InlineData($"![screenshot]({URL}.png)"), InlineData($"[![badge]({URL}.svg)]({URL})"), InlineData($"[ref]: {URL}"), InlineData($"`{URL}`"), InlineData($"``a ` {URL}``"), InlineData($"```\n{URL}\n```"), InlineData($"~~~sh\r\ncurl {URL}\r\n~~~\r\n"), InlineData($"```\n{URL}"), InlineData($"    {URL}"), InlineData("ftp://example.com/file"), InlineData("https://.")]
    public void LeavesOtherTextAlone(string notes) => Assert.Equal(notes, ReleaseNotes.LinkUrls(notes));

    [Fact]
    public void LinksTextAroundCode()
    {
        const string NOTES = $"```\n{URL}\n```\n\n{URL} and `{URL}` and {URL}";
        Assert.Equal($"```\n{URL}\n```\n\n{LINKED} and `{URL}` and {LINKED}",
                     ReleaseNotes.LinkUrls(NOTES));
    }
}
