using System.Text.RegularExpressions;

namespace KSP.Retooled.Launcher.Installs;

/// <summary>Finds KSP installs in the usual Steam and GOG locations of each platform.</summary>
public static partial class InstallLocator
{
    const string SteamFolder = "Kerbal Space Program";

    public static IReadOnlyList<InstallInfo> Find()
    {
        var found = new List<InstallInfo>();
        var seen = new HashSet<string>(
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase
        );
        foreach (string candidate in Candidates())
        {
            var info = KspInstall.Inspect(candidate);
            if (info.Status is InstallStatus.Missing or InstallStatus.NotKsp)
                continue;
            if (seen.Add(info.Path))
                found.Add(info);
        }
        return found;
    }

    static IEnumerable<string> Candidates()
    {
        foreach (string steam in SteamRoots())
        foreach (string library in SteamLibraries(steam))
            yield return Path.Combine(library, "steamapps", "common", SteamFolder);
        foreach (string gog in GogInstalls())
            yield return gog;
    }

    static IEnumerable<string> SteamRoots()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            if (
                Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Valve\Steam",
                    "SteamPath",
                    null
                )
                is string path
            )
                yield return path;
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam"
            );
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "Steam");
        }
        else
        {
            yield return Path.Combine(home, ".steam", "steam");
            yield return Path.Combine(home, ".local", "share", "Steam");
            yield return Path.Combine(
                home,
                ".var",
                "app",
                "com.valvesoftware.Steam",
                ".local",
                "share",
                "Steam"
            );
            yield return Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        }
    }

    /// <summary>The Steam root and every library listed in its <c>libraryfolders.vdf</c>.</summary>
    static IEnumerable<string> SteamLibraries(string steam)
    {
        yield return steam;
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        string text;
        try
        {
            text = File.ReadAllText(vdf);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string library in ParseLibraryFolders(text))
            yield return library;
    }

    /// <summary>The <c>"path"</c> values of a Steam <c>libraryfolders.vdf</c>, unescaped.</summary>
    internal static IEnumerable<string> ParseLibraryFolders(string vdf) =>
        VdfPath().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));

    static IEnumerable<string> GogInstalls()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            // KSP's GOG product id
            foreach (
                string key in new[]
                {
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\Games\1429864849",
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\GOG.com\Games\1429864849",
                }
            )
                if (Microsoft.Win32.Registry.GetValue(key, "path", null) is string path)
                    yield return path;
            yield return @"C:\GOG Games\Kerbal Space Program";
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "GOG Galaxy",
                "Games",
                SteamFolder
            );
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine("/Applications", SteamFolder);
            yield return Path.Combine(home, "Applications", SteamFolder);
        }
        else
        {
            yield return Path.Combine(home, "GOG Games", SteamFolder, "game");
            yield return Path.Combine(home, "Games", SteamFolder);
        }
    }

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();
}
