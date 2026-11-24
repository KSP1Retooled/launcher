namespace KSP.Retooled.Launcher.Releases.GitHub;

internal sealed class GitHubMetadata
{
    public DateTimeOffset CheckedAt { get; set; }

    public Dictionary<string, GitHubMetadataPage?> Pages { get; set; } = [];
}
