using System.Runtime.InteropServices;
using KSP.Retooled.Launcher.Releases;
using Velopack;
using Velopack.Sources;

namespace KSP.Retooled.Launcher.App;

/// <summary>
/// Replaces the installed launcher with a newer release, from the update files that release carries. A downloaded
/// update goes in on <see cref="Restart"/>. On Windows it otherwise goes in the next time the launcher starts; elsewhere
/// it is kept, and offered again.
/// </summary>
public sealed class LauncherUpdater(string? token)
{
    private readonly UpdateManager manager = new(new GithubSource($"https://github.com/{LauncherInfo.REPOSITORY}", token, OnPrerelease));
    private VelopackAsset? downloaded;

    /// <summary>
    /// Only someone already on a pre-release is moved to a newer one.
    /// </summary>
    private static bool OnPrerelease => ReleaseVersion.TryParse(LauncherInfo.Version, out ReleaseVersion version)
                                     && version.PrereleaseIds.Count > 0;

    /// <summary>
    /// False for a copy that was not installed, such as <c>dotnet run</c>, and for one that can't be replaced where it is.
    /// </summary>
    public bool CanUpdate => this.manager.IsInstalled && !OnReadOnlyVolume;

    /// <summary>
    /// A Mac app started from its disk image, or a downloaded one that macOS runs from a read-only copy until it is
    /// moved (App Translocation). Velopack would fail to replace either, so it is told about a new release instead.
    /// </summary>
    public static bool OnReadOnlyVolume { get; } = OperatingSystem.IsMacOS() && IsReadOnly(AppContext.BaseDirectory);

    // access() reports a read-only file system as EROFS whatever the file's permissions are, and even for root.
    private static bool IsReadOnly(string path)
    {
        const int WRITABLE = 2;
        const int READ_ONLY_FILE_SYSTEM = 30;
        return Access(path, WRITABLE) is not 0 && Marshal.GetLastPInvokeError() == READ_ONLY_FILE_SYSTEM;
    }

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int Access(string path, int mode);

    /// <summary>
    /// Downloads the newest release: its version, or null if this one is the newest.
    /// </summary>
    public async Task<string?> DownloadAsync()
    {
        UpdateInfo? update = await this.manager.CheckForUpdatesAsync();
        if (update is null)
        {
            return null;
        }

        await this.manager.DownloadUpdatesAsync(update);
        this.downloaded = update.TargetFullRelease;
        return this.downloaded.Version.ToString();
    }

    /// <summary>
    /// Installs what <see cref="DownloadAsync"/> fetched and starts the new launcher; this process exits.
    /// </summary>
    public void Restart() => this.manager.ApplyUpdatesAndRestart(this.downloaded);
}
