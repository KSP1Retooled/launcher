namespace KSP.Retooled.Launcher.Releases;

/// <summary>
/// A file attached to a release; <see cref="Sha256"/> is GitHub's asset digest when it has one.
/// </summary>
public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl, string ApiUrl, string? Sha256);
