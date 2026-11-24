using KSP.Retooled.Launcher.Utils;

namespace KSP.Retooled.Launcher.Installs;

/// <summary>
/// The launcher's record, in <c>.retooled/launcher.json</c>, of which release it applied. A revert deletes it with the
/// journal.
/// </summary>
public sealed class InstalledRelease
{
    public string Tag { get; set; } = string.Empty;

    public string Asset { get; set; } = string.Empty;

    public bool Prerelease { get; set; }

    public DateTime Installed { get; set; }

    /// <summary>
    /// The repository it came from, or <see cref="LauncherInfo.LOCAL_CHANNEL"/>; null if installed by an older launcher.
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>
    /// The cached patch it was applied from, to roll back to; null if installed by an older launcher.
    /// </summary>
    public string? Patch { get; set; }

    /// <summary>
    /// The published SHA-256 of the patch, which tells this build from a later one attached to the same release; null
    /// if the release published none, the patch is a local file or an older launcher installed it.
    /// </summary>
    public string? Sha256 { get; set; }

    public bool IsFrom(string channel) => string.Equals(this.Channel, channel, StringComparison.OrdinalIgnoreCase);
}
