using KSP.Retooled.Launcher.Releases;
using Velopack;
using Velopack.Sources;

namespace KSP.Retooled.Launcher.App;

/// <summary>
/// Replaces the installed launcher with a newer release, from the update files that release carries. A downloaded
/// update goes in on <see cref="Restart"/>, or else the next time the launcher starts (see <see cref="VelopackApp"/>).
/// </summary>
public sealed class LauncherUpdater(string? token)
{
    readonly UpdateManager manager = new(
        new GithubSource($"https://github.com/{LauncherInfo.Repository}", token, OnPrerelease)
    );
    VelopackAsset? downloaded;

    /// <summary>Only someone already on a pre-release is moved to a newer one.</summary>
    static bool OnPrerelease =>
        ReleaseVersion.TryParse(LauncherInfo.Version, out var version)
        && version.PrereleaseIds.Count > 0;

    /// <summary>False where it is up to the user, and for a copy that was not installed, such as <c>dotnet run</c>.</summary>
    public bool CanUpdate => LauncherInfo.UpdatesItself && manager.IsInstalled;

    /// <summary>Downloads the newest release: its version, or null if this one is the newest.</summary>
    public async Task<string?> DownloadAsync()
    {
        var update = await manager.CheckForUpdatesAsync();
        if (update == null)
            return null;
        await manager.DownloadUpdatesAsync(update);
        downloaded = update.TargetFullRelease;
        return downloaded.Version.ToString();
    }

    /// <summary>Installs what <see cref="DownloadAsync"/> fetched and starts the new launcher; this process exits.</summary>
    public void Restart() => manager.ApplyUpdatesAndRestart(downloaded);
}
