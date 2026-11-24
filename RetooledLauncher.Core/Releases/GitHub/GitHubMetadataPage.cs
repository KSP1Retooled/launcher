namespace KSP.Retooled.Launcher.Releases.GitHub;

internal sealed class GitHubMetadataPage
{
    public string? ETag { get; set; }

    public string? Next { get; set; }

    public List<GitHubRelease?> Items { get; set; } = [];
}
