using System.Diagnostics;
using System.Text;
using KSP.Retooled.Launcher.Exceptions;
using KSP.Retooled.Launcher.Installs;
using RetooledLauncher.Common;

namespace KSP.Retooled.Launcher.Utils;

public static class GameStarter
{
    /// <summary>
    /// Starts the install's player for <paramref name="platform"/> with the install as its working directory, passing
    /// it the arguments in <paramref name="arguments"/> (see <see cref="SplitArguments"/>).
    /// </summary>
    public static void Start(string install, Platform platform, string? arguments = null)
    {
        ProcessStartInfo start = StartInfo(install, platform, arguments);
        string player = Path.Combine(install, KspInstall.Player(platform));
        if (platform is Platform.MACOS ? !Directory.Exists(player) : !File.Exists(player))
        {
            throw new LauncherException($"{player} is missing; reinstall Retooled");
        }

        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(player, File.GetUnixFileMode(player) | UnixFileMode.UserExecute);
        }

        using Process _ =
            Process.Start(start) ?? throw new LauncherException($"could not start {player}");
    }

    /// <summary>
    /// How <see cref="Start"/> runs the player.
    /// </summary>
    internal static ProcessStartInfo StartInfo(string install, Platform platform, string? arguments)
    {
        string player = Path.Combine(install, KspInstall.Player(platform));
        IReadOnlyList<string> args = SplitArguments(arguments);
        // open takes the app's own arguments after --args
        ProcessStartInfo start =
            platform is Platform.MACOS
                ? new ProcessStartInfo("open", ["-a", player, ..args is [] ? [] : args.Prepend("--args")])
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
        List<string> arguments = [];
        StringBuilder current = new();
        bool quoted = false;
        bool started = false;
        foreach (char c in commandLine ?? string.Empty)
        {
            if (c is '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started)
                {
                    arguments.Add(current.ToString());
                }

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
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }
}
