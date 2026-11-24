using System.Globalization;
using KSP.Retooled.Launcher.Releases;

namespace KSP.Retooled.Launcher.App.ViewModels;

/// <summary>
/// One row of the release list.
/// </summary>
public sealed class ReleaseItem(Release release, bool installed, bool latest, bool hasBuild)
{
    public Release Release { get; } = release;

    public string Title => this.Release.DisplayName;

    public string Date => this.Release.Published != DateTimeOffset.MinValue
                              ? this.Release.Published.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                              : string.Empty;

    public bool IsPrerelease => this.Release.Prerelease;

    public bool IsInstalled { get; } = installed;

    public bool IsLatest { get; } = latest;

    public bool HasBuild { get; } = hasBuild;

    public bool NoBuild => !this.HasBuild;
}
