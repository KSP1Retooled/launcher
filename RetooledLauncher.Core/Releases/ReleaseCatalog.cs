namespace KSP.Retooled.Launcher.Releases;

/// <summary>What moving an install to a release means, given what it has now.</summary>
public enum InstallAction
{
    /// <summary>The install is stock.</summary>
    Install,
    Upgrade,
    Downgrade,
    Reinstall,

    /// <summary>Retooled is installed but its version is unknown or not comparable.</summary>
    Replace,

    /// <summary>The installed release's patches were replaced by a new build since it was installed.</summary>
    Update,
}

/// <summary>
/// The releases of one repository, newest first by creation date, whatever their tags. Pre-releases are only offered
/// when the user opts in or the repository has no other releases, but the installed release is always listed so the
/// install never shows a version the launcher pretends not to know.
/// </summary>
/// <param name="versioned">
/// Whether each release replaces the ones before it. When false, as for <see cref="LauncherInfo.ExperimentalChannel"/>,
/// the releases are separate builds: none is an upgrade of another, and only a new build of the installed one is offered.
/// </param>
public sealed class ReleaseCatalog(IEnumerable<Release> releases, bool versioned = true)
{
    public IReadOnlyList<Release> All { get; } = [.. releases.OrderByDescending(r => r.Created)];

    public bool Versioned { get; } = versioned;

    /// <summary>True when every release is a pre-release, so they are offered without the opt-in.</summary>
    public bool OnlyPrereleases => All.Count > 0 && All.All(r => r.Prerelease);

    public static ReleaseCatalog Empty { get; } = new([]);

    public Release? Find(string? tag) => tag == null ? null : All.FirstOrDefault(r => r.Tag == tag);

    /// <summary>
    /// The releases to show: stable ones, pre-releases when opted in or there is nothing else, and the installed one
    /// regardless.
    /// </summary>
    public IReadOnlyList<Release> Visible(bool includePrereleases, string? installedTag = null) =>
        [.. All.Where(r => Offers(r, includePrereleases) || r.Tag == installedTag)];

    /// <summary>The newest offered release with a patch for <paramref name="platform"/>; null when the releases are not <see cref="Versioned"/>.</summary>
    public Release? Latest(string platform, bool includePrereleases) =>
        Versioned
            ? All.FirstOrDefault(r => Offers(r, includePrereleases) && r.PatchFor(platform) != null)
            : null;

    /// <summary>
    /// The newest offered release that is newer than the installed one, or else the installed release if it has a new
    /// build; null if the install is current.
    /// </summary>
    /// <param name="installedSha256">The installed patch's <see cref="Installs.InstalledRelease.Sha256"/>.</param>
    public Release? UpgradeFor(
        string? installedTag,
        string platform,
        bool includePrereleases,
        string? installedSha256 = null
    )
    {
        var latest = Latest(platform, includePrereleases);
        if (installedTag == null)
            return latest;
        if (latest != null && ActionFor(latest, installedTag) == InstallAction.Upgrade)
            return latest;
        var installed = Find(installedTag);
        return installed?.PatchFor(platform) != null && IsRebuilt(installed, installedSha256)
            ? installed
            : null;
    }

    /// <summary>
    /// The newest release newer than <paramref name="version"/>, which need not be listed, or null if there is none or
    /// it is not a version. Only someone already on a pre-release is offered pre-releases.
    /// </summary>
    public Release? NewerThan(string version)
    {
        if (!ReleaseVersion.TryParse(version, out var current))
            return null;
        bool prereleases = current.PrereleaseIds.Count > 0;
        return All.FirstOrDefault(r =>
            (prereleases || !r.Prerelease)
            && ReleaseVersion.TryParse(r.Tag, out var v)
            && v.CompareTo(current) > 0
        );
    }

    /// <param name="installedTag">The installed release's tag; null for a stock install, "" for Retooled of unknown version.</param>
    /// <param name="installedSha256">The installed patch's <see cref="Installs.InstalledRelease.Sha256"/>.</param>
    public InstallAction ActionFor(Release target, string? installedTag, string? installedSha256 = null)
    {
        if (installedTag == null)
            return InstallAction.Install;
        if (installedTag == target.Tag)
            return IsRebuilt(target, installedSha256) ? InstallAction.Update : InstallAction.Reinstall;
        if (!Versioned)
            return InstallAction.Replace;
        int c = Compare(target, installedTag);
        return c > 0 ? InstallAction.Upgrade
            : c < 0 ? InstallAction.Downgrade
            : InstallAction.Replace;
    }

    /// <summary>
    /// Whether the installed patch of <paramref name="release"/> is no longer among its patches, which happens when its
    /// patches are replaced in place. Unknown digests never count as a new build.
    /// </summary>
    static bool IsRebuilt(Release release, string? installedSha256) =>
        installedSha256 != null
        && release.Assets.Any(a => a.Sha256 != null)
        && !release.Assets.Any(a =>
            string.Equals(a.Sha256, installedSha256, StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>Positive when <paramref name="target"/> is newer than the installed tag, 0 when that cannot be told.</summary>
    int Compare(Release target, string installedTag)
    {
        int installed = IndexOf(installedTag),
            wanted = IndexOf(target.Tag);
        if (installed >= 0 && wanted >= 0)
            return installed.CompareTo(wanted); // All is newest first
        if (
            ReleaseVersion.TryParse(target.Tag, out var a)
            && ReleaseVersion.TryParse(installedTag, out var b)
        )
            return a.CompareTo(b);
        return 0;
    }

    bool Offers(Release release, bool includePrereleases) =>
        includePrereleases || OnlyPrereleases || !release.Prerelease;

    int IndexOf(string tag)
    {
        for (int i = 0; i < All.Count; i++)
            if (All[i].Tag == tag)
                return i;
        return -1;
    }
}
