using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KSP.Retooled.Launcher.Releases;

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

    internal Metadata Load(string repository, bool anonymous)
    {
        if (directory is null || !anonymous) return new Metadata();

        try
        {
            Metadata? stored = JsonSerializer.Deserialize(File.ReadAllText(PathFor(repository)), GitHubJson.Default.Metadata);
            if (stored?.Pages is not null
             && stored.Pages.Values.All(p => p?.Items is not null && p.Items.All(r => r is not null)))
            {
                return stored;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }

        return new Metadata();
    }

    internal void Save(string repository, bool anonymous, Metadata metadata)
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
            File.WriteAllText(partial, JsonSerializer.Serialize(metadata, GitHubJson.Default.Metadata));
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
        public readonly Dictionary<string, Metadata> repositories = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset retryAt;
    }
}

internal sealed class Metadata
{
    public DateTimeOffset CheckedAt { get; set; }

    public Dictionary<string, MetadataPage?> Pages { get; set; } = [];
}

internal sealed class MetadataPage
{
    public string? ETag { get; set; }

    public string? Next { get; set; }

    public List<GitHubRelease?> Items { get; set; } = [];
}
