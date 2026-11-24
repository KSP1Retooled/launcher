using System.Text.Json.Serialization;

namespace KSP.Retooled.Launcher.Releases.GitHub;

[JsonSerializable(typeof(List<GitHubRelease>)), JsonSerializable(typeof(GitHubMetadata))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
