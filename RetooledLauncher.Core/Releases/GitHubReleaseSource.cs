using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KSP.Retooled.Launcher.Releases;

public interface IReleaseSource
{
    Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken cancellation = default);

    /// <summary>Requests for <paramref name="asset"/> of <paramref name="release"/>, which this source listed, to try in turn.</summary>
    IEnumerable<HttpRequestMessage> AssetRequests(Release release, ReleaseAsset asset);
}

/// <summary>
/// Lists a GitHub repository's published releases (drafts are dropped) through the REST API, following pagination.
/// A token (e.g. <c>GITHUB_TOKEN</c>) is only needed for a private repository or to lift the anonymous rate limit.
/// </summary>
/// <param name="channel">A repository, with <see cref="LauncherInfo.DebugSuffix"/> for its debug patches.</param>
public sealed class GitHubReleaseSource(
    HttpClient http,
    string channel,
    string? token = null,
    GitHubMetadataCache? cache = null
) : IReleaseSource
{
    const int MaxPages = 10;
    readonly GitHubMetadataCache metadata = cache ?? GitHubMetadataCache.For(http);

    public string Repository { get; } = LauncherInfo.RepositoryOf(channel);

    public async Task<IReadOnlyList<Release>> GetReleasesAsync(
        CancellationToken cancellation = default
    )
    {
        if (!IsValidRepository(Repository))
            throw new LauncherException($"'{Repository}' is not a GitHub repository (owner/name)");
        var scope = metadata.ForToken(token);
        await scope.Gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!scope.Repositories.TryGetValue(Repository, out var stored))
                scope.Repositories[Repository] = stored = metadata.Load(
                    Repository, string.IsNullOrEmpty(token)
                );
            var now = metadata.Clock.GetUtcNow();
            if (
                stored.Pages.Count > 0
                && now >= stored.CheckedAt
                && now - stored.CheckedAt < metadata.Freshness
            )
                return Releases(stored);
            if (now < scope.RetryAt)
                throw new LauncherException($"GitHub's rate limit was reached; try again after {scope.RetryAt:u}");

            var updated = new Metadata();
            string? url = $"https://api.github.com/repos/{Repository}/releases?per_page=100";
            for (int page = 0; url != null && page < MaxPages; page++)
            {
                cancellation.ThrowIfCancellationRequested();
                // Never forward credentials to another host through a pagination link.
                if (
                    !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme != "https"
                    || uri.Host != "api.github.com"
                )
                    throw new LauncherException("GitHub returned an invalid pagination URL");
                stored.Pages.TryGetValue(url, out var previous);
                using var request = Request(HttpMethod.Get, url, "application/vnd.github+json");
                if (previous?.ETag is string etag)
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                using var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellation
                ).ConfigureAwait(false);
                ObserveRateLimit(scope, response);
                MetadataPage current;
                if (response.StatusCode == System.Net.HttpStatusCode.NotModified && previous != null)
                    current = previous;
                else
                {
                    if (!response.IsSuccessStatusCode)
                        throw new LauncherException(Describe(response, Repository));
                    await using var body = await response.Content
                        .ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                    current = new MetadataPage
                    {
                        ETag = response.Headers.ETag?.ToString(),
                        Next = NextPage(response),
                        Items = await JsonSerializer.DeserializeAsync(
                            body, GitHubJson.Default.ListGitHubRelease, cancellation
                        ).ConfigureAwait(false) ?? [],
                    };
                }
                updated.Pages[url] = current;
                url = current.Next;
                if (url != null && metadata.Clock.GetUtcNow() < scope.RetryAt)
                    throw new LauncherException($"GitHub's rate limit was reached; try again after {scope.RetryAt:u}");
            }
            updated.CheckedAt = metadata.Clock.GetUtcNow();
            scope.Repositories[Repository] = updated;
            metadata.Save(Repository, string.IsNullOrEmpty(token), updated);
            return Releases(updated);
        }
        finally
        {
            scope.Gate.Release();
        }
    }

    IReadOnlyList<Release> Releases(Metadata stored) =>
        stored.Pages.Values
            .SelectMany(p => p.Items)
            .Where(r => !r.Draft)
            .Select(r => Convert(r) with { Channel = channel })
            .ToArray();

    void ObserveRateLimit(GitHubMetadataCache.Scope scope, HttpResponseMessage response)
    {
        var now = metadata.Clock.GetUtcNow();
        DateTimeOffset retry = default;
        if (response.Headers.RetryAfter is { } after)
            retry = after.Date ?? now + (after.Delta ?? TimeSpan.FromMinutes(1));
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.FirstOrDefault() == "0"
            && response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), out long seconds)
            && seconds >= 0 && seconds <= 253402300799)
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (at > retry) retry = at;
        }
        if ((int)response.StatusCode is 403 or 429 && retry <= now)
            retry = now + TimeSpan.FromMinutes(1);
        if (retry > scope.RetryAt)
            scope.RetryAt = retry;
    }

    /// <summary>A request for <paramref name="url"/> carrying the headers GitHub wants and the token if any.</summary>
    public HttpRequestMessage Request(HttpMethod method, string url, string accept)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.Add(
            new ProductInfoHeaderValue("KSPRetooledLauncher", LauncherInfo.Version)
        );
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Where to download an asset from: the API endpoint when a token is set (private repositories only serve assets
    /// there), otherwise the public download URL.
    /// </summary>
    public HttpRequestMessage AssetRequest(ReleaseAsset asset) =>
        string.IsNullOrEmpty(token)
            ? Request(HttpMethod.Get, asset.DownloadUrl, "application/octet-stream")
            : Request(HttpMethod.Get, asset.ApiUrl, "application/octet-stream");

    /// <summary>Where GitHub serves the file <paramref name="name"/> of the release tagged <paramref name="tag"/>.</summary>
    public string DownloadUrl(string tag, string name) =>
        $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";

    public IEnumerable<HttpRequestMessage> AssetRequests(Release release, ReleaseAsset asset)
    {
        yield return AssetRequest(asset);
    }

    public static bool IsValidRepository(string repository)
    {
        var parts = repository.Split('/');
        return parts.Length == 2
            && parts.All(p =>
                p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            );
    }

    static string Describe(HttpResponseMessage response, string repository) =>
        (int)response.StatusCode switch
        {
            404 =>
                $"GitHub has no repository {repository}, or it is private and needs a GITHUB_TOKEN",
            401 => "GitHub rejected the token in GITHUB_TOKEN",
            403 or 429 => "GitHub's rate limit was reached; try again later or set GITHUB_TOKEN",
            _ =>
                $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for {repository}'s releases",
        };

    static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var links))
            return null;
        foreach (string link in links.SelectMany(l => l.Split(',')))
        {
            var parts = link.Split(';', StringSplitOptions.TrimEntries);
            if (
                parts.Length >= 2
                && parts[1..].Any(p => p == "rel=\"next\"")
                && parts[0].StartsWith('<')
                && parts[0].EndsWith('>')
            )
                return parts[0][1..^1];
        }
        return null;
    }

    internal static Release Convert(GitHubRelease r) =>
        new Release(
            r.TagName,
            r.Name ?? "",
            r.Prerelease,
            r.PublishedAt ?? r.CreatedAt ?? DateTimeOffset.MinValue,
            r.Body ?? "",
            r.HtmlUrl ?? "",
            [
                .. (r.Assets ?? []).Select(a => new ReleaseAsset(
                    a.Name,
                    a.Size,
                    a.BrowserDownloadUrl,
                    a.Url,
                    Sha256(a.Digest)
                )),
            ]
        )
        {
            Created = r.CreatedAt ?? r.PublishedAt ?? DateTimeOffset.MinValue,
        };

    /// <summary>GitHub (and GitCode) report digests as <c>sha256:&lt;hex&gt;</c>.</summary>
    internal static string? Sha256(string? digest) =>
        digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest[7..].ToLowerInvariant()
            : null;
}

sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

[JsonSerializable(typeof(List<GitHubRelease>))]
[JsonSerializable(typeof(Metadata))]
sealed partial class GitHubJson : JsonSerializerContext;
