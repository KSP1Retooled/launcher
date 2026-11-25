using KSP.Retooled.Launcher.Common;

namespace KSP.Retooled.Launcher.Installs;

public enum InstallStatus
{
    /// <summary>
    /// The folder does not exist.
    /// </summary>
    MISSING,

    /// <summary>
    /// The folder is not a KSP install.
    /// </summary>
    NOT_KSP,
    STOCK,
    RETOOLED,

    /// <summary>
    /// An apply or revert stopped half way; uninstalling finishes restoring the stock install.
    /// </summary>
    INTERRUPTED
}

/// <summary>
/// What is in a KSP folder right now.
/// </summary>
public sealed record InstallInfo(string Path, InstallStatus Status)
{
    /// <summary>
    /// From <c>buildID64.txt</c> / <c>buildID.txt</c>, e.g. <c>03190</c> for 1.12.5.
    /// </summary>
    public string? BuildId { get; init; }

    /// <summary>
    /// The release the launcher installed; null when stock or installed by other means.
    /// </summary>
    public InstalledRelease? Release { get; init; }

    /// <summary>
    /// For an install that has a patch applied, the platform that the patch was built for. For a stock install, the
    /// platform of the game executable that the folder contains. Null for a folder that is not a KSP install.
    /// </summary>
    public Platform? Platform { get; init; }

    /// <summary>
    /// The installed tag as <see cref="Releases.ReleaseCatalog.ActionFor"/> wants it: null when stock, "" when unknown.
    /// </summary>
    public string? InstalledTag => this.Status is InstallStatus.RETOOLED or InstallStatus.INTERRUPTED ? this.Release?.Tag ?? string.Empty : null;

    /// <summary>
    /// <see cref="InstalledTag"/> for comparing with <paramref name="channel"/>'s releases: "" when the installed
    /// release
    /// came from elsewhere, since an equal tag there may be a different build and a version number says nothing.
    /// </summary>
    public string? InstalledTagIn(string channel) =>
        string.IsNullOrEmpty(this.Release?.Channel) || this.Release.IsFrom(channel) ? this.InstalledTag
        : this.InstalledTag is null                                                 ? null
                                                                                      : string.Empty;
}
