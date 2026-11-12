using System.Text.Json;
using System.Text.RegularExpressions;

namespace KSP.Retooled.Launcher.Installs;

/// <summary>Finds KSP installs in the usual Steam, GOG, Heroic and Minigalaxy locations of each platform.</summary>
public static partial class InstallLocator
{
    const string GameFolder = "Kerbal Space Program";

    /// <summary>KSP's GOG product id.</summary>
    const string GogId = "1429864849";

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
        var libraries = SteamRoots().SelectMany(steam => SteamLibraries(steam, null)).ToList();
        foreach (string library in libraries)
            yield return SteamGame(library);
        // GOG's Linux installers put the game in a "game" subfolder, next to start.sh. Heroic, Minigalaxy and Lutris
        // keep that layout, so look in both.
        foreach (string gog in GogInstalls().Concat(HeroicInstalls()).Concat(MinigalaxyInstalls()))
        {
            yield return gog;
            yield return Path.Combine(gog, "game");
        }
        // The Windows version installed with Windows Steam or GOG inside Wine, CrossOver, Bottles, Whisky or a Proton
        // prefix. Steam's own KSP running under Proton stays in the native library above.
        foreach (string prefix in WinePrefixes(libraries))
        {
            string driveC = Path.Combine(prefix, "drive_c");
            foreach (string steam in WindowsSteamRoots(driveC))
            foreach (string library in SteamLibraries(steam, prefix))
                yield return SteamGame(library);
            foreach (string gog in WindowsGogFolders(driveC))
                yield return gog;
        }
    }

    static string SteamGame(string library) =>
        Path.Combine(library, "steamapps", "common", GameFolder);

    /// <summary>Where Windows Steam installs by default, under the drive <paramref name="root"/>.</summary>
    static IEnumerable<string> WindowsSteamRoots(string root) =>
        [
            Path.Combine(root, "Program Files (x86)", "Steam"),
            Path.Combine(root, "Program Files", "Steam"),
        ];

    /// <summary>Where GOG Galaxy and GOG's offline installer put KSP by default, under the drive <paramref name="root"/>.</summary>
    static IEnumerable<string> WindowsGogFolders(string root) =>
        [
            Path.Combine(root, "GOG Games", GameFolder),
            Path.Combine(root, "Program Files (x86)", "GOG Galaxy", "Games", GameFolder),
        ];

    /// <summary>
    /// Wine prefixes (folders holding <c>drive_c</c>) in the default locations of Wine, CrossOver, Bottles, Whisky,
    /// Heroic and Lutris, and the Proton prefixes in each of the Steam <paramref name="libraries"/>.
    /// </summary>
    static IEnumerable<string> WinePrefixes(IEnumerable<string> libraries)
    {
        if (OperatingSystem.IsWindows())
            return [];
        var prefixes = new List<string> { Path.Combine(Home, ".wine") };
        // Folders whose subfolders are prefixes
        var parents = new List<string>();
        if (OperatingSystem.IsMacOS())
        {
            parents.Add(Path.Combine(Home, "Library", "Application Support", "CrossOver", "Bottles"));
            parents.Add(
                Path.Combine(Home, "Library", "Containers", "com.isaacmarovitz.Whisky", "Bottles")
            );
        }
        else
        {
            parents.Add(Path.Combine(Home, ".cxoffice"));
            parents.Add(Path.Combine(Home, ".local", "share", "bottles", "bottles"));
            parents.Add(
                Path.Combine(
                    Home,
                    ".var",
                    "app",
                    "com.usebottles.bottles",
                    "data",
                    "bottles",
                    "bottles"
                )
            );
            parents.Add(Path.Combine(Home, "Games", "Heroic", "Prefixes"));
            parents.Add(Path.Combine(Home, "Games", "Heroic", "Prefixes", "default"));
            // Lutris makes a prefix per game in ~/Games
            parents.Add(Path.Combine(Home, "Games"));
        }
        prefixes.AddRange(parents.SelectMany(Subfolders));
        // Proton keeps a prefix per app in compatdata/<app id>/pfx
        prefixes.AddRange(
            libraries
                .SelectMany(library => Subfolders(Path.Combine(library, "steamapps", "compatdata")))
                .Select(app => Path.Combine(app, "pfx"))
        );
        return prefixes.Where(prefix => Directory.Exists(Path.Combine(prefix, "drive_c")));
    }

    static IEnumerable<string> Subfolders(string folder)
    {
        try
        {
            return Directory.GetDirectories(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The host path of the Windows <paramref name="path"/> inside the Wine <paramref name="prefix"/>, or null when it
    /// isn't an absolute Windows path. Wine maps each drive letter with a link in <c>dosdevices</c>; C: and Z: are
    /// mapped directly to <c>drive_c</c> and <c>/</c> so the same install isn't found by two paths.
    /// </summary>
    internal static string? WindowsPathInPrefix(string prefix, string path)
    {
        var match = WindowsAbsolutePath().Match(path);
        if (!match.Success)
            return null;
        char drive = char.ToLowerInvariant(match.Groups[1].Value[0]);
        string[] parts = match
            .Groups[2]
            .Value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        string root = drive switch
        {
            'c' => Path.Combine(prefix, "drive_c"),
            'z' => "/",
            _ => Path.Combine(prefix, "dosdevices", drive + ":"),
        };
        return Path.Combine([root, .. parts]);
    }

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary><c>$XDG_CONFIG_HOME</c>, or <c>~/.config</c> when it is unset.</summary>
    static string XdgConfig =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Home, ".config");

    static IEnumerable<string> SteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (
                var (key, value) in new[]
                {
                    (@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
                    (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                    (@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath"),
                }
            )
                if (Microsoft.Win32.Registry.GetValue(key, value, null) is string path)
                    yield return path;
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam"
            );
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(Home, "Library", "Application Support", "Steam");
        }
        else
        {
            yield return Path.Combine(Home, ".steam", "steam");
            yield return Path.Combine(Home, ".local", "share", "Steam");
            yield return Path.Combine(
                Home,
                ".var",
                "app",
                "com.valvesoftware.Steam",
                ".local",
                "share",
                "Steam"
            );
            yield return Path.Combine(Home, "snap", "steam", "common", ".local", "share", "Steam");
        }
    }

    /// <summary>
    /// The Steam root and every library listed in its <c>libraryfolders.vdf</c>. For a Windows Steam inside the Wine
    /// <paramref name="prefix"/>, the listed Windows paths are mapped to the host.
    /// </summary>
    static IEnumerable<string> SteamLibraries(string steam, string? prefix)
    {
        yield return steam;
        if (ReadText(Path.Combine(steam, "steamapps", "libraryfolders.vdf")) is not { } text)
            yield break;
        foreach (string library in ParseLibraryFolders(text))
            if (prefix == null)
                yield return library;
            else if (WindowsPathInPrefix(prefix, library) is { } mapped)
                yield return mapped;
    }

    /// <summary>
    /// The library paths of a Steam <c>libraryfolders.vdf</c>, unescaped. Current files give each library a
    /// <c>"path"</c> key; files from before 2021 map a number straight to the path.
    /// </summary>
    internal static IEnumerable<string> ParseLibraryFolders(string vdf) =>
        VdfPath()
            .Matches(vdf)
            .Concat(VdfLegacyPath().Matches(vdf))
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));

    static IEnumerable<string> GogInstalls()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (
                string key in new[]
                {
                    $@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\Games\{GogId}",
                    $@"HKEY_LOCAL_MACHINE\SOFTWARE\GOG.com\Games\{GogId}",
                }
            )
                if (Microsoft.Win32.Registry.GetValue(key, "path", null) is string path)
                    yield return path;
            foreach (string folder in WindowsGogFolders(@"C:\"))
                yield return folder;
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine("/Applications", GameFolder);
            yield return Path.Combine(Home, "Applications", GameFolder);
        }
        else
        {
            yield return Path.Combine(Home, "GOG Games", GameFolder);
            yield return Path.Combine(Home, "Games", GameFolder);
            // Lutris's default for its GOG installer
            yield return Path.Combine(Home, "Games", "kerbal-space-program");
        }
    }

    /// <summary>Heroic's config folders: the native one, and on Linux the Flatpak one.</summary>
    static IEnumerable<string> HeroicConfigs()
    {
        if (OperatingSystem.IsWindows())
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "heroic"
            );
        else if (OperatingSystem.IsMacOS())
            yield return Path.Combine(Home, "Library", "Application Support", "heroic");
        else
        {
            yield return Path.Combine(XdgConfig, "heroic");
            yield return Path.Combine(
                Home,
                ".var",
                "app",
                "com.heroicgameslauncher.hgl",
                "config",
                "heroic"
            );
        }
    }

    /// <summary>KSP's installs in Heroic's GOG manifest, and its default install folder.</summary>
    static IEnumerable<string> HeroicInstalls()
    {
        foreach (string config in HeroicConfigs())
        {
            if (ReadText(Path.Combine(config, "gog_store", "installed.json")) is { } installed)
                foreach (string path in ParseHeroicInstalls(installed, GogId))
                    yield return path;
            if (
                ReadText(Path.Combine(config, "config.json")) is { } settings
                && ParseHeroicInstallFolder(settings) is { } folder
            )
                yield return Path.Combine(folder, GameFolder);
        }
        yield return OperatingSystem.IsMacOS()
            ? Path.Combine(Home, "Applications", "Heroic", GameFolder)
            : Path.Combine(Home, "Games", "Heroic", GameFolder);
    }

    /// <summary>
    /// The <c>install_path</c> of each entry for <paramref name="appName"/> in Heroic's
    /// <c>gog_store/installed.json</c>.
    /// </summary>
    internal static IEnumerable<string> ParseHeroicInstalls(string json, string appName)
    {
        var paths = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (
                doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("installed", out var installed)
                || installed.ValueKind != JsonValueKind.Array
            )
                return paths;
            foreach (var entry in installed.EnumerateArray())
                if (
                    entry.ValueKind == JsonValueKind.Object
                    && GetString(entry, "appName") == appName
                    && GetString(entry, "install_path") is { Length: > 0 } path
                )
                    paths.Add(path);
        }
        catch (JsonException) { }
        return paths;
    }

    /// <summary>The <c>defaultSettings.defaultInstallPath</c> of Heroic's <c>config.json</c>.</summary>
    internal static string? ParseHeroicInstallFolder(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (
                doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("defaultSettings", out var defaults)
                && defaults.ValueKind == JsonValueKind.Object
                && GetString(defaults, "defaultInstallPath") is { Length: > 0 } folder
            )
                return folder;
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>KSP in Minigalaxy's install folder, native and Flatpak (Linux only).</summary>
    static IEnumerable<string> MinigalaxyInstalls()
    {
        if (!OperatingSystem.IsLinux())
            yield break;
        foreach (
            string config in new[]
            {
                Path.Combine(XdgConfig, "minigalaxy", "config.json"),
                Path.Combine(
                    Home,
                    ".var",
                    "app",
                    "io.github.sharkwouter.Minigalaxy",
                    "config",
                    "minigalaxy",
                    "config.json"
                ),
            }
        )
            if (ReadText(config) is { } json && ParseMinigalaxyInstallFolder(json) is { } folder)
                yield return Path.Combine(folder, GameFolder);
    }

    /// <summary>The <c>install_dir</c> of Minigalaxy's <c>config.json</c>.</summary>
    internal static string? ParseMinigalaxyInstallFolder(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (
                doc.RootElement.ValueKind == JsonValueKind.Object
                && GetString(doc.RootElement, "install_dir") is { Length: > 0 } folder
            )
                return folder;
        }
        catch (JsonException) { }
        return null;
    }

    static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    /// <summary>
    /// <c>"1" "D:\\SteamLibrary"</c>. A value with no path separator, such as an app's <c>"228980" "1"</c> in the
    /// current format, is not a library.
    /// </summary>
    [GeneratedRegex("\"\\d+\"\\s+\"((?:[^\"\\\\]|\\\\.)*[\\\\/](?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex VdfLegacyPath();

    /// <summary><c>D:\Games</c> or <c>D:/Games</c>: the drive letter, then the rest.</summary>
    [GeneratedRegex(@"^([A-Za-z]):[\\/](.*)$")]
    private static partial Regex WindowsAbsolutePath();
}
