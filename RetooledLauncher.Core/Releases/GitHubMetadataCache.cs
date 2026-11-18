using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KSP.Retooled.Launcher.Releases;

/// <summary>Shares release metadata across channels. Only anonymous metadata is persisted.</summary>
public sealed class GitHubMetadataCache(string? directory = null, TimeProvider? clock = null)
{
    static readonly ConditionalWeakTable<HttpClient, GitHubMetadataCache> Defaults = new();
    readonly ConcurrentDictionary<string, Scope> scopes = new();
    internal TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    /// <summary>How long a successful listing can be reused without contacting GitHub.</summary>
    public TimeSpan Freshness { get; init; } = TimeSpan.FromMinutes(1);

    internal static GitHubMetadataCache For(HttpClient http) => Defaults.GetValue(http, _ => new());

    internal Scope ForToken(string? token) => scopes.GetOrAdd(token ?? "", _ => new());

    internal Metadata Load(string repository, bool anonymous)
    {
        if (directory != null && anonymous)
            try
            {
                var stored = JsonSerializer.Deserialize(
                    File.ReadAllText(PathFor(repository)), GitHubJson.Default.Metadata
                );
                if (
                    stored?.Pages != null
                    && stored.Pages.Values.All(p => p?.Items != null && p.Items.All(r => r != null))
                )
                    return stored;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    internal void Save(string repository, bool anonymous, Metadata metadata)
    {
        if (directory == null || !anonymous)
            return;
        string path = PathFor(repository);
        string partial = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(partial, JsonSerializer.Serialize(metadata, GitHubJson.Default.Metadata));
            File.Move(partial, path, overwrite: true);
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

    string PathFor(string repository) =>
        Path.Combine(directory!, Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(repository.ToLowerInvariant()))
        ) + ".json");

    internal sealed class Scope
    {
        public readonly SemaphoreSlim Gate = new(1);
        public readonly Dictionary<string, Metadata> Repositories = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset RetryAt;
    }
}

internal sealed class Metadata
{
    public DateTimeOffset CheckedAt { get; set; }
    public Dictionary<string, MetadataPage> Pages { get; set; } = [];
}

internal sealed class MetadataPage
{
    public string? ETag { get; set; }
    public string? Next { get; set; }
    public List<GitHubRelease> Items { get; set; } = [];
}
