using System.Diagnostics;
using System.Text;

namespace KSP.Retooled.Launcher.Installs;

public static class GameStarter
{
    /// <summary>
    /// Starts the install's player for <paramref name="platform"/> with the install as its working directory, passing
    /// it the arguments in <paramref name="arguments"/> (see <see cref="SplitArguments"/>).
    /// </summary>
    public static void Start(string install, string platform, string? arguments = null)
    {
        var start = StartInfo(install, platform, arguments);
        string player = Path.Combine(install, KspInstall.Player(platform));
        if (platform == "macos" ? !Directory.Exists(player) : !File.Exists(player))
            throw new LauncherException($"{player} is missing; reinstall Retooled");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(player, File.GetUnixFileMode(player) | UnixFileMode.UserExecute);
        using var _ =
            Process.Start(start) ?? throw new LauncherException($"could not start {player}");
    }

    /// <summary>How <see cref="Start"/> runs the player.</summary>
    internal static ProcessStartInfo StartInfo(string install, string platform, string? arguments)
    {
        string player = Path.Combine(install, KspInstall.Player(platform));
        var args = SplitArguments(arguments);
        // open takes the app's own arguments after --args
        var start =
            platform == "macos"
                ? new ProcessStartInfo("open", ["-a", player, .. args is [] ? [] : args.Prepend("--args")])
                : new ProcessStartInfo(player, args);
        start.WorkingDirectory = install;
        start.UseShellExecute = false;
        return start;
    }

    /// <summary>
    /// Splits a command line the user typed into arguments: at whitespace, except inside double quotes, which are
    /// removed. Nothing else is special, so a backslash in a Windows path is kept as it is.
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string? commandLine)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        bool quoted = false,
            started = false;
        foreach (char c in commandLine ?? "")
        {
            if (c == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started)
                    arguments.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }
        if (started)
            arguments.Add(current.ToString());
        return arguments;
    }
}
