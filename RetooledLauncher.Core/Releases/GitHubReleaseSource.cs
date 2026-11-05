using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KSP.Retooled.Launcher.Releases;

public interface IReleaseSource
{
    Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken cancellation = default);
}

/// <summary>
/// Lists a GitHub repository's published releases (drafts are dropped) through the REST API, following pagination.
/// A token (e.g. <c>GITHUB_TOKEN</c>) is only needed for a private repository or to lift the anonymous rate limit.
/// </summary>
/// <param name="channel">A repository, with <see cref="LauncherInfo.DebugSuffix"/> for its debug patches.</param>
public sealed class GitHubReleaseSource(HttpClient http, string channel, string? token = null)
    : IReleaseSource
{
    const int MaxPages = 10;

    public string Repository { get; } = LauncherInfo.RepositoryOf(channel);

    public async Task<IReadOnlyList<Release>> GetReleasesAsync(
        CancellationToken cancellation = default
    )
    {
        if (!IsValidRepository(Repository))
            throw new LauncherException($"'{Repository}' is not a GitHub repository (owner/name)");
        var releases = new List<Release>();
        string? url = $"https://api.github.com/repos/{Repository}/releases?per_page=100";
        for (int page = 0; url != null && page < MaxPages; page++)
        {
            using var request = Request(HttpMethod.Get, url, "application/vnd.github+json");
            using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new LauncherException(Describe(response, Repository));
            await using var body = await response
                .Content.ReadAsStreamAsync(cancellation)
                .ConfigureAwait(false);
            var items =
                await JsonSerializer
                    .DeserializeAsync(body, GitHubJson.Default.ListGitHubRelease, cancellation)
                    .ConfigureAwait(false)
                ?? [];
            releases.AddRange(
                items.Where(r => !r.Draft).Select(r => Convert(r) with { Channel = channel })
            );
            url = NextPage(response);
        }
        return releases;
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
        new(
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
        );

    /// <summary>GitHub reports digests as <c>sha256:&lt;hex&gt;</c>.</summary>
    static string? Sha256(string? digest) =>
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
sealed partial class GitHubJson : JsonSerializerContext;
