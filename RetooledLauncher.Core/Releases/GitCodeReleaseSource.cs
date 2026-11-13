using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KSP.Retooled.Launcher.Releases;

/// <summary>
/// Lists the releases of a GitCode (gitcode.com) repository that mirrors a GitHub one. Its API answers anonymously in
/// much the same shape as GitHub's. The releases carry <paramref name="channel"/>, the GitHub channel they mirror, so
/// a patch installed from either is the same release to the launcher.
/// </summary>
/// <param name="repository">The GitCode repository (<c>owner/name</c>).</param>
public sealed class GitCodeReleaseSource(HttpClient http, string channel, string repository)
    : IReleaseSource
{
    const int MaxPages = 10;

    public string Repository { get; } = repository;

    public async Task<IReadOnlyList<Release>> GetReleasesAsync(
        CancellationToken cancellation = default
    )
    {
        if (!GitHubReleaseSource.IsValidRepository(Repository))
            throw new LauncherException($"'{Repository}' is not a GitCode repository (owner/name)");
        var releases = new List<Release>();
        int pages = 1;
        for (int page = 1; page <= pages && page <= MaxPages; page++)
        {
            string url =
                $"https://api.gitcode.com/api/v5/repos/{Repository}/releases?per_page=100&page={page}";
            using var request = Request(url, "application/json");
            using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new LauncherException(
                    $"GitCode answered {(int)response.StatusCode} {response.ReasonPhrase} for {Repository}'s releases"
                );
            await using var body = await response
                .Content.ReadAsStreamAsync(cancellation)
                .ConfigureAwait(false);
            var items =
                await JsonSerializer
                    .DeserializeAsync(body, GitCodeJson.Default.ListGitCodeRelease, cancellation)
                    .ConfigureAwait(false)
                ?? [];
            releases.AddRange(items.Select(r => Convert(r) with { Channel = channel }));
            if (
                response.Headers.TryGetValues("total_page", out var values)
                && int.TryParse(values.FirstOrDefault(), out int total)
            )
                pages = total;
        }
        return releases;
    }

    public IEnumerable<HttpRequestMessage> AssetRequests(Release release, ReleaseAsset asset)
    {
        yield return Request(asset.DownloadUrl, "application/octet-stream");
    }

    /// <summary>Where GitCode serves the file <paramref name="name"/> of the release tagged <paramref name="tag"/>.</summary>
    public string DownloadUrl(string tag, string name) =>
        $"https://gitcode.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";

    /// <summary>Whether <paramref name="asset"/> was listed by GitCode rather than GitHub.</summary>
    public static bool IsGitCodeAsset(ReleaseAsset asset) =>
        Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri)
        && (uri.Host == "gitcode.com" || uri.Host.EndsWith(".gitcode.com", StringComparison.Ordinal));

    static HttpRequestMessage Request(string url, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.Add(
            new ProductInfoHeaderValue("KSPRetooledLauncher", LauncherInfo.Version)
        );
        return request;
    }

    Release Convert(GitCodeRelease r) =>
        new(
            r.TagName,
            r.Name ?? "",
            r.Prerelease,
            r.CreatedAt ?? DateTimeOffset.MinValue,
            r.Body ?? "",
            $"https://gitcode.com/{Repository}/releases/{Uri.EscapeDataString(r.TagName)}",
            [
                .. (r.Assets ?? [])
                    // the rest are archives of the source that GitCode adds to every release
                    .Where(a => a.Type == "attach")
                    .Select(a => new ReleaseAsset(
                        a.Name,
                        a.Size ?? 0,
                        a.BrowserDownloadUrl,
                        a.BrowserDownloadUrl,
                        GitHubReleaseSource.Sha256(a.Digest)
                    )),
            ]
        );
}

sealed class GitCodeRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("assets")]
    public List<GitCodeAsset>? Assets { get; set; }
}

sealed class GitCodeAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

[JsonSerializable(typeof(List<GitCodeRelease>))]
sealed partial class GitCodeJson : JsonSerializerContext;
