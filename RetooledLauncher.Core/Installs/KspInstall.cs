using System.Text.Json;
using System.Text.Json.Serialization;
using KSP.Retooled.Patch;

namespace KSP.Retooled.Launcher.Installs;

public enum InstallStatus
{
    /// <summary>The folder does not exist.</summary>
    Missing,

    /// <summary>The folder is not a KSP install.</summary>
    NotKsp,
    Stock,
    Retooled,

    /// <summary>An apply or revert stopped half way; uninstalling finishes restoring the stock install.</summary>
    Interrupted,
}

/// <summary>What is in a KSP folder right now.</summary>
public sealed record InstallInfo(string Path, InstallStatus Status)
{
    /// <summary>From <c>buildID64.txt</c> / <c>buildID.txt</c>, e.g. <c>03190</c> for 1.12.5.</summary>
    public string? BuildId { get; init; }

    /// <summary>The release the launcher installed; null when stock or installed by other means.</summary>
    public InstalledRelease? Release { get; init; }

    /// <summary>
    /// For an install that has a patch applied, the platform that the patch was built for. For a stock install, the
    /// platform of the game executable that the folder contains. Null for a folder that is not a KSP install.
    /// </summary>
    public string? Platform { get; init; }

    /// <summary>The installed tag as <see cref="Releases.ReleaseCatalog.ActionFor"/> wants it: null when stock, "" when unknown.</summary>
    public string? InstalledTag =>
        Status is InstallStatus.Retooled or InstallStatus.Interrupted ? Release?.Tag ?? "" : null;

    /// <summary>
    /// <see cref="InstalledTag"/> for comparing with <paramref name="channel"/>'s releases: "" when the installed release
    /// came from elsewhere, since an equal tag there may be a different build and a version number says nothing.
    /// </summary>
    public string? InstalledTagIn(string channel) =>
        string.IsNullOrEmpty(Release?.Channel) || Release.IsFrom(channel) ? InstalledTag
        : InstalledTag == null ? null
        : "";
}

/// <summary>The launcher's record, in <c>.retooled/launcher.json</c>, of which release it applied. A revert deletes it with the journal.</summary>
public sealed class InstalledRelease
{
    public string Tag { get; set; } = "";
    public string Asset { get; set; } = "";
    public bool Prerelease { get; set; }
    public DateTime Installed { get; set; }

    /// <summary>The repository it came from, or <see cref="LauncherInfo.LocalChannel"/>; null if installed by an older launcher.</summary>
    public string? Channel { get; set; }

    /// <summary>The cached patch it was applied from, to roll back to; null if installed by an older launcher.</summary>
    public string? Patch { get; set; }

    public bool IsFrom(string channel) =>
        string.Equals(Channel, channel, StringComparison.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
[JsonSerializable(typeof(InstalledRelease))]
sealed partial class InstallJson : JsonSerializerContext;

public static class KspInstall
{
    const string MarkerFile = InstallJournal.Directory + "/launcher.json";

    /// <summary>The player each platform's build ships, relative to the install (the Retooled build keeps stock's names).</summary>
    public static string Player(string platform) =>
        platform switch
        {
            "windows" => "KSP_x64.exe",
            "linux" => "KSP.x86_64",
            "macos" => "KSP.app",
            _ => throw new LauncherException($"unsupported platform {platform}"),
        };

    /// <summary>Every platform that KSP was released for.</summary>
    public static readonly IReadOnlyList<string> Platforms = ["windows", "linux", "macos"];

    /// <summary>
    /// <see cref="Platforms"/> with <paramref name="host"/> moved to the front. If a folder has game executables for
    /// more than one platform, the launcher uses the first platform in this order.
    /// </summary>
    public static IReadOnlyList<string> PlatformsOn(string host) =>
        [.. Platforms.OrderBy(p => p != host)];

    /// <summary>
    /// The platform of the patch that the launcher should apply to <paramref name="info"/>. This is the install's own
    /// platform, which can differ from the platform that the launcher runs on. For example, people run the Windows
    /// version of KSP on Linux with Proton. When the install's platform isn't known, this is <paramref name="host"/>.
    /// </summary>
    public static string PatchPlatform(InstallInfo? info, string host) => info?.Platform ?? host;

    /// <param name="host">The platform that the launcher runs on. The default is <see cref="LauncherInfo.Platform"/>.</param>
    public static InstallInfo Inspect(string path, string? host = null)
    {
        string full;
        try
        {
            full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        }
        catch (Exception e)
            when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new InstallInfo(path, InstallStatus.Missing);
        }
        if (!Directory.Exists(full))
            return new InstallInfo(full, InstallStatus.Missing);
        // Steam's registry keeps its path lowercased, which would show as c:\program files (x86)\steam\...
        full = OnDiskCasing(full);

        InstallJournal? journal = null;
        try
        {
            journal = InstallJournal.Load(full);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException) { }
        if (journal != null)
            return new InstallInfo(
                full,
                journal.State == JournalState.Applied
                    ? InstallStatus.Retooled
                    : InstallStatus.Interrupted
            )
            {
                BuildId = ReadBuildId(full),
                Release = ReadMarker(full),
                Platform = journal.Platform,
            };

        string? platform = PlatformsOn(host ?? LauncherInfo.Platform)
            .FirstOrDefault(p => PlayerExists(full, p));
        bool ksp =
            Directory.Exists(System.IO.Path.Combine(full, "GameData", "Squad")) && platform != null;
        return new InstallInfo(full, ksp ? InstallStatus.Stock : InstallStatus.NotKsp)
        {
            BuildId = ksp ? ReadBuildId(full) : null,
            Platform = ksp ? platform : null,
        };
    }

    static bool PlayerExists(string install, string platform)
    {
        string player = System.IO.Path.Combine(install, Player(platform));
        return File.Exists(player) || Directory.Exists(player);
    }

    /// <summary>An existing folder's path spelled as on disk, on Windows.</summary>
    internal static string OnDiskCasing(string full)
    {
        if (!OperatingSystem.IsWindows())
            return full;
        string root = System.IO.Path.GetPathRoot(full)!;
        string path = root is [_, ':', ..] ? root.ToUpperInvariant() : root;
        foreach (
            string part in full[root.Length..]
                .Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
        )
        {
            string? name = null;
            try
            {
                name = new DirectoryInfo(path).EnumerateDirectories(part).FirstOrDefault()?.Name;
            }
            catch (Exception e)
                when (e
                        is IOException
                            or UnauthorizedAccessException
                            or System.Security.SecurityException
                ) { }
            path = System.IO.Path.Combine(path, name ?? part);
        }
        return path;
    }

    static string? ReadBuildId(string install)
    {
        foreach (string name in new[] { "buildID64.txt", "buildID.txt" })
        {
            string file = System.IO.Path.Combine(install, name);
            if (!File.Exists(file))
                continue;
            foreach (string line in File.ReadLines(file))
            {
                int eq = line.IndexOf('=');
                if (
                    eq > 0
                    && line[..eq].Trim().Equals("build id", StringComparison.OrdinalIgnoreCase)
                )
                    return line[(eq + 1)..].Trim();
            }
        }
        return null;
    }

    public static InstalledRelease? ReadMarker(string install)
    {
        string file = System.IO.Path.Combine(install, MarkerFile);
        if (!File.Exists(file))
            return null;
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllBytes(file),
                InstallJson.Default.InstalledRelease
            );
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void WriteMarker(string install, InstalledRelease release)
    {
        string file = System.IO.Path.Combine(install, MarkerFile);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllBytes(
            file + ".tmp",
            JsonSerializer.SerializeToUtf8Bytes(release, InstallJson.Default.InstalledRelease)
        );
        File.Move(file + ".tmp", file, overwrite: true);
    }
}
