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
}

/// <summary>
/// The releases of one repository, newest first: releases tagged with a semantic version by version (so a maintenance
/// release of an older line stays below the newer line), any other tag by publish date among them. Pre-releases are only offered when the user opts in, but the installed release is always
/// listed so the install never shows a version the launcher pretends not to know.
/// </summary>
public sealed class ReleaseCatalog
{
    public IReadOnlyList<Release> All { get; }

    public ReleaseCatalog(IEnumerable<Release> releases)
    {
        var list = releases.ToList();
        var versions = list.ToDictionary(
            r => r,
            r => ReleaseVersion.TryParse(r.Tag, out var v) ? v : null
        );
        var ordered = list.Where(r => versions[r] != null)
            .OrderByDescending(r => versions[r])
            .ThenByDescending(r => r.Published)
            .ToList();
        // a tag that is not a version goes before the first release published before it
        foreach (
            var other in list.Where(r => versions[r] == null).OrderByDescending(r => r.Published)
        )
        {
            int at = ordered.FindIndex(r => r.Published < other.Published);
            ordered.Insert(at < 0 ? ordered.Count : at, other);
        }
        All = ordered;
    }

    public static ReleaseCatalog Empty { get; } = new([]);

    public Release? Find(string? tag) => tag == null ? null : All.FirstOrDefault(r => r.Tag == tag);

    /// <summary>The releases to show: stable ones, pre-releases when opted in, and the installed one regardless.</summary>
    public IReadOnlyList<Release> Visible(bool includePrereleases, string? installedTag = null) =>
        [.. All.Where(r => includePrereleases || !r.Prerelease || r.Tag == installedTag)];

    /// <summary>The newest release with a patch for <paramref name="platform"/>, honouring the pre-release opt-in.</summary>
    public Release? Latest(string platform, bool includePrereleases) =>
        All.FirstOrDefault(r =>
            (includePrereleases || !r.Prerelease) && r.PatchFor(platform) != null
        );

    /// <summary>The newest offered release that is newer than the installed one, or null if it is current.</summary>
    public Release? UpgradeFor(string? installedTag, string platform, bool includePrereleases)
    {
        var latest = Latest(platform, includePrereleases);
        if (latest == null || installedTag == null)
            return latest;
        return ActionFor(latest, installedTag) == InstallAction.Upgrade ? latest : null;
    }

    /// <param name="installedTag">The installed release's tag; null for a stock install, "" for Retooled of unknown version.</param>
    public InstallAction ActionFor(Release target, string? installedTag)
    {
        if (installedTag == null)
            return InstallAction.Install;
        if (installedTag == target.Tag)
            return InstallAction.Reinstall;
        int c = Compare(target, installedTag);
        return c > 0 ? InstallAction.Upgrade
            : c < 0 ? InstallAction.Downgrade
            : InstallAction.Replace;
    }

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

    int IndexOf(string tag)
    {
        for (int i = 0; i < All.Count; i++)
            if (All[i].Tag == tag)
                return i;
        return -1;
    }
}
