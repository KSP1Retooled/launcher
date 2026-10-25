using KSP.Retooled.Patch;

namespace KSP.Retooled.Launcher.Releases;

/// <summary>A file attached to a release; <see cref="Sha256"/> is GitHub's asset digest when it has one.</summary>
public sealed record ReleaseAsset(
    string Name,
    long Size,
    string DownloadUrl,
    string ApiUrl,
    string? Sha256
);

/// <summary>A published release of KSP Retooled. <see cref="Prerelease"/> is GitHub's flag, not a guess from the tag.</summary>
public sealed record Release(
    string Tag,
    string Name,
    bool Prerelease,
    DateTimeOffset Published,
    string Notes,
    string PageUrl,
    IReadOnlyList<ReleaseAsset> Assets
)
{
    /// <summary>The release's name for a platform's patch: <c>retooled-&lt;platform&gt;.rtpatch</c>.</summary>
    public static string PatchName(string platform) => $"retooled-{platform}{PatchFile.Extension}";

    public ReleaseAsset? PatchFor(string platform) =>
        Assets.FirstOrDefault(a =>
            a.Name.Equals(PatchName(platform), StringComparison.OrdinalIgnoreCase)
        );

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) || Name == Tag ? Tag : $"{Tag} — {Name}";
}
