using KSP.Retooled.Launcher.Releases;

namespace KSP.Retooled.Launcher.Patches;

/// <summary>
/// Where the launcher gets a release's patch file from.
/// </summary>
public interface IPatchProvider
{
    /// <summary>
    /// A local path to <paramref name="release"/>'s patch for <paramref name="platform"/>, downloading it if needed.
    /// </summary>
    Task<string> GetPatchAsync(Release release,
                               string platform,
                               IProgress<LauncherProgress>? progress,
                               CancellationToken cancellation);

    /// <summary>
    /// A path to a copy of the patch file <paramref name="file"/> that outlives the original, so it is still there to
    /// roll back to when the user has moved or rebuilt it.
    /// </summary>
    Task<string> ImportAsync(string file,
                             IProgress<LauncherProgress>? progress,
                             CancellationToken cancellation);

    /// <summary>
    /// Drops every cached patch but the files in <paramref name="keep"/>, paths this provider returned.
    /// </summary>
    void Prune(IEnumerable<string> keep);
}
