using KSP.Retooled.Launcher.Releases;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

public sealed class ReleaseLinkTests
{
    [Theory, InlineData("https://github.com/KSP1Retooled/launcher/pull/9", true), InlineData("http://example.com/", true), InlineData("HTTPS://EXAMPLE.COM/", true), InlineData("file:///C:/Windows/System32/calc.exe", false), InlineData(@"C:\Windows\System32\calc.exe", false), InlineData(@"\\host\share\run.exe", false), InlineData("/usr/bin/xcalc", false), InlineData("ms-settings:", false), InlineData("javascript:alert(1)", false), InlineData("relative/page", false), InlineData("", false), InlineData(null, false)]
    public void OpensOnlyWebLinks(string? url, bool expected) => Assert.Equal(expected, Release.IsWebLink(url));
}
