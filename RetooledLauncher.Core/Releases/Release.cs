using KSP.Retooled.Patch;

namespace KSP.Retooled.Launcher.Releases;

/// <summary>
/// A file attached to a release; <see cref="Sha256"/> is GitHub's asset digest when it has one.
/// </summary>
public sealed record ReleaseAsset(string Name,
                                  long Size,
                                  string DownloadUrl,
                                  string ApiUrl,
                                  string? Sha256);

/// <summary>
/// A published release of KSP Retooled. <see cref="Prerelease"/> is GitHub's flag, not a guess from the tag.
/// </summary>
public sealed record Release(string Tag,
                             string Name,
                             bool Prerelease,
                             DateTimeOffset Published,
                             string Notes,
                             string PageUrl,
                             IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>
    /// When the release was created, which orders the catalog; a draft is created before it is published.
    /// </summary>
    public DateTimeOffset Created { get; init; } = Published;

    /// <summary>
    /// The repository it was listed from: tags are only unique within one.
    /// </summary>
    public string Channel { get; init; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(this.Name) || this.Name == this.Tag ? this.Tag : $"{this.Tag} — {this.Name}";

    /// <summary>
    /// The release's name for a platform's patch: <c>retooled-&lt;platform&gt;.rtpatch</c>, or
    /// <c>retooled-&lt;platform&gt;-debug.rtpatch</c> for the development player's.
    /// </summary>
    public static string PatchName(string platform, bool debug = false) => $"retooled-{platform}{(debug ? "-debug" : string.Empty)}{PatchFile.EXTENSION}";

    /// <summary>
    /// The name of the patch for <paramref name="platform"/> that this release's channel offers.
    /// </summary>
    public string PatchNameFor(string platform) => PatchName(platform, LauncherInfo.IsDebugChannel(this.Channel));

    /// <summary>
    /// Whether a link in <see cref="Notes"/> may be opened. Notes carry the titles of pull requests, so anyone who
    /// opens one can put a link here, and handing a path or a custom scheme to the OS would run it.
    /// </summary>
    public static bool IsWebLink(string? url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                                              && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public ReleaseAsset? PatchFor(string platform) => this.Assets.FirstOrDefault(a => a.Name.Equals(PatchNameFor(platform), StringComparison.OrdinalIgnoreCase));
}
