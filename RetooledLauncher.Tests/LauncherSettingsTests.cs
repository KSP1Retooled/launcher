using KSP.Retooled.Launcher.Settings;
using KSP.Retooled.Launcher.Utils;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

public sealed class LauncherSettingsTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(),
                                                "retooled-settings-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose() => File.Delete(this.file);

    [Fact]
    public void OffersTheDefaultChannelFirstAndOnlyOnce() => Assert.Equal([LauncherInfo.DEFAULT_REPOSITORY, LauncherInfo.DEBUG_CHANNEL, LauncherInfo.EXPERIMENTAL_CHANNEL, "a/b"],
                                                                          new LauncherSettings
                                                                          {
                                                                              Channels = ["a/b", "A/B", LauncherInfo.DEFAULT_REPOSITORY]
                                                                          }.AllChannels);

    [Fact]
    public void ThePreferredMirrorIsSavedByName()
    {
        new LauncherSettings { PreferredMirror = ReleaseMirror.GIT_CODE }.Save(this.file);
        Assert.Contains("\"GitCode\"", File.ReadAllText(this.file));
        Assert.Equal(ReleaseMirror.GIT_CODE, LauncherSettings.Load(this.file).PreferredMirror);
        File.WriteAllText(this.file, "{}");
        Assert.Equal(ReleaseMirror.GIT_HUB, LauncherSettings.Load(this.file).PreferredMirror);
    }

    [Fact]
    public void TheLaunchArgumentsAreSaved()
    {
        new LauncherSettings { LaunchArguments = "-popupwindow -logFile \"my log.txt\"" }.Save(this.file);
        Assert.Equal("-popupwindow -logFile \"my log.txt\"",
                     LauncherSettings.Load(this.file).LaunchArguments);

        File.WriteAllText(this.file, "{}");
        Assert.Null(LauncherSettings.Load(this.file).LaunchArguments);
    }

    [Fact]
    public void ARepositoryFromBeforeChannelsIsStillOffered()
    {
        File.WriteAllText(this.file, """{ "repository": "o/r" }""");
        Assert.Equal([LauncherInfo.DEFAULT_REPOSITORY, LauncherInfo.DEBUG_CHANNEL, LauncherInfo.EXPERIMENTAL_CHANNEL, "o/r"],
                     LauncherSettings.Load(this.file).AllChannels);
    }

    [Fact]
    public void ChannelsSurviveASave()
    {
        new LauncherSettings { Channels = ["a/b"], Repository = "a/b" }.Save(this.file);
        LauncherSettings loaded = LauncherSettings.Load(this.file);
        Assert.Equal(["a/b"], loaded.Channels);
        Assert.Equal("a/b", loaded.Repository);
    }
}
