using System.Diagnostics;
using KSP.Retooled.Launcher.Common;
using KSP.Retooled.Launcher.Installs;
using KSP.Retooled.Launcher.Utils;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

public sealed class GameStarterTests
{
    [Theory, InlineData(null), InlineData(""), InlineData("  \t ")]
    public void NoArgumentsSplitToNone(string? commandLine) => Assert.Empty(GameStarter.SplitArguments(commandLine));

    [Fact]
    public void ArgumentsAreSplitAtWhitespace() => Assert.Equal(["-popupwindow", "-screen-width", "1920"],
                                                                GameStarter.SplitArguments("  -popupwindow -screen-width\t1920 "));

    [Fact]
    public void QuotesKeepWhitespaceAndAreRemoved() => Assert.Equal(["-logFile", @"C:\my logs\ksp.log", "--name=Jeb Kerman", string.Empty],
                                                                    GameStarter.SplitArguments("""-logFile "C:\my logs\ksp.log" --name="Jeb Kerman" "" """));

    [Theory, InlineData(Platform.WINDOWS), InlineData(Platform.LINUX)]
    public void ThePlayerIsGivenTheArguments(Platform platform)
    {
        ProcessStartInfo start = GameStarter.StartInfo("install", platform, "-a \"b c\"");
        Assert.Equal(Path.Combine("install", platform.PlayerName), start.FileName);
        Assert.Equal(["-a", "b c"], start.ArgumentList);
        Assert.Equal("install", start.WorkingDirectory);
        Assert.Empty(GameStarter.StartInfo("install", platform, null).ArgumentList);
    }

    [Fact]
    public void OnMacosTheArgumentsFollowArgs()
    {
        string app = Path.Combine("install", "KSP.app");
        Assert.Equal(["-a", app, "--args", "-a", "b c"],
                     GameStarter.StartInfo("install", Platform.MACOS, "-a \"b c\"").ArgumentList);

        Assert.Equal(["-a", app], GameStarter.StartInfo("install", Platform.MACOS, " ").ArgumentList);
    }
}
