using System.Diagnostics;

namespace KSP.Retooled.Launcher.Installs;

public static class GameStarter
{
    /// <summary>Starts the install's player for <paramref name="platform"/> with the install as its working directory.</summary>
    public static void Start(string install, string platform)
    {
        string player = Path.Combine(install, KspInstall.Player(platform));
        var start =
            platform == "macos"
                ? new ProcessStartInfo("open", ["-a", player])
                : new ProcessStartInfo(player);
        start.WorkingDirectory = install;
        start.UseShellExecute = false;
        if (platform == "macos" ? !Directory.Exists(player) : !File.Exists(player))
            throw new LauncherException($"{player} is missing; reinstall Retooled");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(player, File.GetUnixFileMode(player) | UnixFileMode.UserExecute);
        using var _ =
            Process.Start(start) ?? throw new LauncherException($"could not start {player}");
    }
}
