using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KSP.Retooled.Launcher.Releases.GitHub;

/// <summary>
/// Shares release metadata across channels. Only anonymous metadata is persisted.
/// </summary>
public sealed class GitHubMetadataCache(string? directory = null, TimeProvider? clock = null)
{
    private static readonly ConditionalWeakTable<HttpClient, GitHubMetadataCache> Defaults = new();
    private readonly ConcurrentDictionary<string, Scope> scopes = new();

    internal TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    /// <summary>
    /// How long a successful listing can be reused without contacting GitHub.
    /// </summary>
    public TimeSpan Freshness { get; init; } = TimeSpan.FromMinutes(1);

    internal static GitHubMetadataCache For(HttpClient http) => Defaults.GetValue(http, _ => new GitHubMetadataCache());

    internal Scope ForToken(string? token) => this.scopes.GetOrAdd(token ?? string.Empty, _ => new Scope());

    internal GitHubMetadata Load(string repository, bool anonymous)
    {
        if (directory is null || !anonymous) return new GitHubMetadata();

        try
        {
            GitHubMetadata? stored = JsonSerializer.Deserialize(File.ReadAllText(PathFor(repository)), GitHubJsonContext.Default.GitHubMetadata);
            if (stored?.Pages is not null
             && stored.Pages.Values.All(p => p?.Items is not null && p.Items.All(r => r is not null)))
            {
                return stored;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }

        return new GitHubMetadata();
    }

    internal void Save(string repository, bool anonymous, GitHubMetadata gitHubMetadata)
    {
        if (directory is null || !anonymous)
        {
            return;
        }

        string path = PathFor(repository);
        string partial = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(partial, JsonSerializer.Serialize(gitHubMetadata, GitHubJsonContext.Default.GitHubMetadata));
            File.Move(partial, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private string PathFor(string repository) => Path.Combine(directory!,
                                                              Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(repository.ToLowerInvariant()))) + ".json");

    internal sealed class Scope
    {
        public readonly SemaphoreSlim gate = new(1);
        public readonly Dictionary<string, GitHubMetadata> repositories = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset retryAt;
    }
}
