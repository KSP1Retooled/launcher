using System.Security;
using System.Text.Json;
using FastEnumUtility;
using KSP.Retooled.Launcher.Exceptions;
using KSP.Retooled.Patch.Models.Journal;
using RetooledLauncher.Common;

namespace KSP.Retooled.Launcher.Installs;

public static class KspInstall
{
    private const string MARKER_FILE = InstallJournal.DIRECTORY + "/launcher.json";

    /// <summary>
    /// The player each platform's build ships, relative to the install (the Retooled build keeps stock's names).
    /// </summary>
    public static string Player(Platform platform) => platform switch
    {
        Platform.WINDOWS => "KSP_x64.exe",
        Platform.LINUX   => "KSP.x86_64",
        Platform.MACOS   => "KSP.app",
        Platform.UNKNOWN => throw new LauncherException($"Unsupported platform {platform.FastToString()}"),
        _                => throw new LauncherException($"Unsupported platform {platform.FastToString()}")
    };

    /// <summary>
    /// Platforms with <paramref name="host"/> moved to the front. If a folder has game executables for
    /// more than one platform, the launcher uses the first platform in this order.
    /// </summary>
    public static IReadOnlyList<Platform> PlatformsOn(Platform host) => [..Platform.All.OrderBy(p => p != host)];

    /// <summary>
    /// The platform of the patch that the launcher should apply to <paramref name="info"/>. This is the install's own
    /// platform, which can differ from the platform that the launcher runs on. For example, people run the Windows
    /// version of KSP on Linux with Proton. When the install's platform isn't known, this is <paramref name="host"/>.
    /// </summary>
    public static Platform PatchPlatform(InstallInfo? info, Platform host) => info?.Platform ?? host;

    /// <param name="path">Install path</param>
    /// <param name="host">The platform that the launcher runs on. The default is the current platform.</param>
    public static InstallInfo Inspect(string path, Platform? host = null)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new InstallInfo(path, InstallStatus.MISSING);
        }

        if (!Directory.Exists(full))
        {
            return new InstallInfo(full, InstallStatus.MISSING);
        }

        // Steam's registry keeps its path lowercased, which would show as c:\program files (x86)\steam\...
        full = OnDiskCasing(full);

        InstallJournal? journal = null;
        try
        {
            journal = InstallJournal.Load(full);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException) { }

        if (journal is not null)
        {
            return new InstallInfo(full, journal.State == JournalState.APPLIED
                                             ? InstallStatus.RETOOLED
                                             : InstallStatus.INTERRUPTED)
            {
                BuildId = ReadBuildId(full),
                Release = ReadMarker(full),
                Platform = journal.Platform
            };
        }

        Platform? platform = PlatformsOn(host ?? Platform.Current).FirstOrDefault(p => PlayerExists(full, p));
        bool ksp = Directory.Exists(Path.Combine(full, "GameData", "Squad")) && platform is not null;
        return new InstallInfo(full, ksp ? InstallStatus.STOCK : InstallStatus.NOT_KSP)
        {
            BuildId = ksp ? ReadBuildId(full) : null,
            Platform = ksp ? platform : null
        };
    }

    private static bool PlayerExists(string install, Platform platform)
    {
        string player = Path.Combine(install, Player(platform));
        return File.Exists(player) || Directory.Exists(player);
    }

    /// <summary>
    /// An existing folder's path spelled as on disk, on Windows.
    /// </summary>
    private static string OnDiskCasing(string full)
    {
        if (!OperatingSystem.IsWindows())
        {
            return full;
        }

        string root = Path.GetPathRoot(full)!;
        string path = root is [_, ':', ..] ? root.ToUpperInvariant() : root;
        foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string? name = null;
            try
            {
                name = new DirectoryInfo(path).EnumerateDirectories(part).FirstOrDefault()?.Name;
            }
            catch (Exception e)when (e is IOException
                                       or UnauthorizedAccessException
                                       or SecurityException) { }

            path = Path.Combine(path, name ?? part);
        }

        return path;
    }

    private static string? ReadBuildId(string install)
    {
        foreach (string name in new[] { "buildID64.txt", "buildID.txt" })
        {
            string file = Path.Combine(install, name);
            if (!File.Exists(file))
            {
                continue;
            }

            foreach (string line in File.ReadLines(file))
            {
                int eq = line.IndexOf('=');
                if (
                    eq > 0
                 && line[..eq].Trim().Equals("build id", StringComparison.OrdinalIgnoreCase)
                )
                {
                    return line[(eq + 1)..].Trim();
                }
            }
        }

        return null;
    }

    public static InstalledRelease? ReadMarker(string install)
    {
        string file = Path.Combine(install, MARKER_FILE);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(file),
                                              InstallJsonContext.Default.InstalledRelease);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void WriteMarker(string install, InstalledRelease release)
    {
        string file = Path.Combine(install, MARKER_FILE);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes($"{file}.tmp", JsonSerializer.SerializeToUtf8Bytes(release, InstallJsonContext.Default.InstalledRelease));
        File.Move(file + ".tmp", file, true);
    }
}
