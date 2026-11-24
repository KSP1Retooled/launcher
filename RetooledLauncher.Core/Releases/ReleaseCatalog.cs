using KSP.Retooled.Launcher.Utils;

namespace KSP.Retooled.Launcher.Releases;

/// <summary>
/// What moving an install to a release means, given what it has now.
/// </summary>
public enum InstallAction
{
    /// <summary>
    /// The install is stock.
    /// </summary>
    INSTALL,
    UPGRADE,
    DOWNGRADE,
    REINSTALL,

    /// <summary>
    /// Retooled is installed but its version is unknown or not comparable.
    /// </summary>
    REPLACE,

    /// <summary>
    /// The installed release's patches were replaced by a new build since it was installed.
    /// </summary>
    UPDATE
}

/// <summary>
/// The releases of one repository, newest first by creation date, whatever their tags. Pre-releases are only offered
/// when the user opts in or the repository has no other releases, but the installed release is always listed so the
/// install never shows a version the launcher pretends not to know.
/// </summary>
/// <param name="versioned">
/// Whether each release replaces the ones before it. When false, as for
/// <see cref="LauncherInfo.EXPERIMENTAL_CHANNEL"/>,
/// the releases are separate builds: none is an upgrade of another, and only a new build of the installed one is
/// offered.
/// </param>
public sealed class ReleaseCatalog(IEnumerable<Release> releases, bool versioned = true)
{
    public IReadOnlyList<Release> All { get; } = [..releases.OrderByDescending(r => r.Created)];

    private bool Versioned { get; } = versioned;

    /// <summary>
    /// True when every release is a pre-release, so they are offered without the opt-in.
    /// </summary>
    public bool OnlyPrereleases => this.All.Count > 0 && this.All.All(r => r.Prerelease);

    public static ReleaseCatalog Empty { get; } = new([]);

    public Release? Find(string? tag) => tag is null ? null : this.All.FirstOrDefault(r => r.Tag == tag);

    /// <summary>
    /// The releases to show: stable ones, pre-releases when opted in or there is nothing else, and the installed one
    /// regardless.
    /// </summary>
    public IReadOnlyList<Release> Visible(bool includePrereleases, string? installedTag = null) => [..this.All.Where(r => Offers(r, includePrereleases) || r.Tag == installedTag)];

    /// <summary>
    /// The newest offered release with a patch for <paramref name="platform"/>; null when the releases are not
    /// <see cref="Versioned"/>.
    /// </summary>
    public Release? Latest(string platform, bool includePrereleases) => this.Versioned
                                                                            ? this.All.FirstOrDefault(r => Offers(r, includePrereleases) && r.PatchFor(platform) is not null)
                                                                            : null;

    /// <summary>
    /// The newest offered release that is newer than the installed one, or else the installed release if it has a new
    /// build; null if the install is current.
    /// </summary>
    /// <param name="installedTag">The currently installed tag</param>
    /// <param name="platform">The installed platform</param>
    /// <param name="includePrereleases">If prereleases should also be checked</param>
    /// <param name="installedSha256">The installed patch's <see cref="Installs.InstalledRelease.Sha256"/>.</param>
    public Release? UpgradeFor(string? installedTag,
                               string platform,
                               bool includePrereleases,
                               string? installedSha256 = null)
    {
        Release? latest = Latest(platform, includePrereleases);
        if (installedTag is null || (latest is not null && ActionFor(latest, installedTag) == InstallAction.UPGRADE))
        {
            return latest;
        }

        Release? installed = Find(installedTag);
        return installed?.PatchFor(platform) is not null && IsRebuilt(installed, installedSha256)
                   ? installed
                   : null;
    }

    /// <summary>
    /// The newest release newer than <paramref name="version"/>, which need not be listed, or null if there is none or
    /// it is not a version. Only someone already on a pre-release is offered pre-releases.
    /// </summary>
    public Release? NewerThan(string version)
    {
        if (!ReleaseVersion.TryParse(version, out ReleaseVersion current))
        {
            return null;
        }

        bool prereleases = current.PrereleaseIds.Count > 0;
        return this.All.FirstOrDefault(r =>
                                           (prereleases || !r.Prerelease)
                                        && ReleaseVersion.TryParse(r.Tag, out ReleaseVersion v)
                                        && v.CompareTo(current) > 0);
    }

    /// <param name="target">The release to install</param>
    /// <param name="installedTag">The installed release's tag; null for a stock install, "" for Retooled of unknown version.</param>
    /// <param name="installedSha256">The installed patch's <see cref="Installs.InstalledRelease.Sha256"/>.</param>
    public InstallAction ActionFor(Release target, string? installedTag, string? installedSha256 = null)
    {
        if (installedTag is null)
        {
            return InstallAction.INSTALL;
        }

        if (installedTag == target.Tag)
        {
            return IsRebuilt(target, installedSha256) ? InstallAction.UPDATE : InstallAction.REINSTALL;
        }

        if (!this.Versioned)
        {
            return InstallAction.REPLACE;
        }

        int c = Compare(target, installedTag);
        return c > 0   ? InstallAction.UPGRADE
               : c < 0 ? InstallAction.DOWNGRADE
                         : InstallAction.REPLACE;
    }

    /// <summary>
    /// Whether the installed patch of <paramref name="release"/> is no longer among its patches, which happens when its
    /// patches are replaced in place. Unknown digests never count as a new build.
    /// </summary>
    private static bool IsRebuilt(Release release, string? installedSha256)
    {
        return installedSha256 is not null
            && release.Assets.Any(a => a.Sha256 is not null)
            && !release.Assets.Any(a => string.Equals(a.Sha256, installedSha256, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Positive when <paramref name="target"/> is newer than the installed tag, 0 when that cannot be told.
    /// </summary>
    private int Compare(Release target, string installedTag)
    {
        int installed = IndexOf(installedTag);
        int wanted = IndexOf(target.Tag);
        if (installed >= 0 && wanted >= 0)
        {
            return installed.CompareTo(wanted); // All is newest first
        }

        if (ReleaseVersion.TryParse(target.Tag, out ReleaseVersion a)
         && ReleaseVersion.TryParse(installedTag, out ReleaseVersion b))
        {
            return a.CompareTo(b);
        }

        return 0;
    }

    private bool Offers(Release release, bool includePrereleases) => includePrereleases || this.OnlyPrereleases || !release.Prerelease;

    private int IndexOf(string tag)
    {
        for (int i = 0; i < this.All.Count; i++)
        {
            if (this.All[i].Tag == tag)
            {
                return i;
            }
        }

        return -1;
    }
}
