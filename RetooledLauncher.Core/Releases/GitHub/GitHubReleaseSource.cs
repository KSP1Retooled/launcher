using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using KSP.Retooled.Launcher.Exceptions;
using KSP.Retooled.Launcher.Utils;

namespace KSP.Retooled.Launcher.Releases.GitHub;

/// <summary>
/// Lists a GitHub repository's published releases (drafts are dropped) through the REST API, following pagination.
/// A token (e.g. <c>GITHUB_TOKEN</c>) is only needed for a private repository or to lift the anonymous rate limit.
/// </summary>
/// <param name="channel">A repository, with <see cref="LauncherInfo.DEBUG_SUFFIX"/> for its debug patches.</param>
public sealed class GitHubReleaseSource(HttpClient http, string channel, string? token = null, GitHubMetadataCache? cache = null) : IReleaseSource
{
    private const int MAX_PAGES = 10;
    private readonly GitHubMetadataCache metadata = cache ?? GitHubMetadataCache.For(http);

    public string Repository { get; } = LauncherInfo.RepositoryOf(channel);

    // ReSharper disable once CognitiveComplexity
    public async Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken cancellation = default)
    {
        if (!IsValidRepository(this.Repository))
        {
            throw new LauncherException($"'{this.Repository}' is not a GitHub repository (owner/name)");
        }

        GitHubMetadataCache.Scope scope = this.metadata.ForToken(token);
        await scope.gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!scope.repositories.TryGetValue(this.Repository, out GitHubMetadata? stored))
            {
                scope.repositories[this.Repository] = stored = this.metadata.Load(this.Repository, string.IsNullOrEmpty(token));
            }

            DateTimeOffset now = this.metadata.Clock.GetUtcNow();
            if (stored.Pages?.Count > 0
             && now >= stored.CheckedAt
             && now - stored.CheckedAt < this.metadata.Freshness)
            {
                return Releases(stored);
            }

            if (now < scope.retryAt)
            {
                throw new LauncherException($"GitHub's rate limit was reached; try again after {scope.retryAt:u}");
            }

            GitHubMetadata updated = new();
            string? url = $"https://api.github.com/repos/{this.Repository}/releases?per_page=100";
            for (int page = 0; url is not null && page < MAX_PAGES; page++)
            {
                cancellation.ThrowIfCancellationRequested();
                // Never forward credentials to another host through a pagination link.
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                 || uri.Scheme is not "https"
                 || uri.Host is not "api.github.com")
                {
                    throw new LauncherException("GitHub returned an invalid pagination URL");
                }

                if (stored.Pages?.TryGetValue(url, out GitHubMetadataPage? previous) is null)
                {
                    previous = null;
                }

                using HttpRequestMessage request = Request(HttpMethod.Get, url, "application/vnd.github+json");
                if (previous?.ETag is { } etag)
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                }

                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                ObserveRateLimit(scope, response);
                GitHubMetadataPage current;
                if (response.StatusCode == HttpStatusCode.NotModified && previous is not null)
                {
                    current = previous;
                }
                else
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new LauncherException(Describe(response, this.Repository));
                    }

                    await using Stream body = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                    current = new GitHubMetadataPage
                    {
                        ETag = response.Headers.ETag?.ToString(),
                        Next = NextPage(response),
                        Items = await JsonSerializer.DeserializeAsync(body, GitHubJsonContext.Default.ListGitHubRelease, cancellation)
                                                    .ConfigureAwait(false) ?? []
                    };
                }

                updated.Pages?[url] = current;
                url = current.Next;
                if (url is not null && this.metadata.Clock.GetUtcNow() < scope.retryAt)
                {
                    throw new LauncherException($"GitHub's rate limit was reached; try again after {scope.retryAt:u}");
                }
            }

            updated.CheckedAt                   = this.metadata.Clock.GetUtcNow();
            scope.repositories[this.Repository] = updated;
            this.metadata.Save(this.Repository, string.IsNullOrEmpty(token), updated);
            return Releases(updated);
        }
        finally
        {
            scope.gate.Release();
        }
    }

    public IEnumerable<HttpRequestMessage> AssetRequests(Release release, ReleaseAsset asset)
    {
        yield return AssetRequest(asset);
    }

    private IReadOnlyList<Release> Releases(GitHubMetadata stored) => stored.Pages is null ? [] :
    [
        ..stored.Pages.Values
                .Where(p => p is not null)
                .SelectMany(p => p!.Items)
                .Where(r => r?.Draft is false)
                .Select(r => Convert(r!) with { Channel = channel })
    ];

    private void ObserveRateLimit(GitHubMetadataCache.Scope scope, HttpResponseMessage response)
    {
        DateTimeOffset now = this.metadata.Clock.GetUtcNow();
        DateTimeOffset retry = default;
        if (response.Headers.RetryAfter is { } after)
        {
            retry = after.Date ?? now + (after.Delta ?? TimeSpan.FromMinutes(1));
        }

        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? remaining)
         && remaining.FirstOrDefault() is "0"
         && response.Headers.TryGetValues("X-RateLimit-Reset", out IEnumerable<string>? reset)
         && long.TryParse(reset.FirstOrDefault(), out long seconds)
         && seconds is >= 0 and <= 253402300799)
        {
            DateTimeOffset at = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (at > retry) retry = at;
        }

        if ((int)response.StatusCode is 403 or 429 && retry <= now)
        {
            retry = now + TimeSpan.FromMinutes(1);
        }

        if (retry > scope.retryAt)
        {
            scope.retryAt = retry;
        }
    }

    /// <summary>
    /// A request for <paramref name="url"/> carrying the headers GitHub wants and the token if any.
    /// </summary>
    private HttpRequestMessage Request(HttpMethod method, string url, string accept)
    {
        HttpRequestMessage request = new(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("KSPRetooledLauncher", LauncherInfo.Version));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    /// <summary>
    /// Where to download an asset from: the API endpoint when a token is set (private repositories only serve assets
    /// there), otherwise the public download URL.
    /// </summary>
    private HttpRequestMessage AssetRequest(ReleaseAsset asset) => string.IsNullOrEmpty(token)
                                                                       ? Request(HttpMethod.Get, asset.DownloadUrl, "application/octet-stream")
                                                                       : Request(HttpMethod.Get, asset.ApiUrl, "application/octet-stream");

    /// <summary>
    /// Where GitHub serves the file <paramref name="name"/> of the release tagged <paramref name="tag"/>.
    /// </summary>
    public string DownloadUrl(string tag, string name) =>
        $"https://github.com/{this.Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";

    public static bool IsValidRepository(string repository)
    {
        string[] parts = repository.Split('/');
        return parts.Length == 2
            && parts.All(p =>
                             p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                        );
    }

    private static string Describe(HttpResponseMessage response, string repository) =>
        (int)response.StatusCode switch
        {
            404 =>
                $"GitHub has no repository {repository}, or it is private and needs a GITHUB_TOKEN",
            401        => "GitHub rejected the token in GITHUB_TOKEN",
            403 or 429 => "GitHub's rate limit was reached; try again later or set GITHUB_TOKEN",
            _ =>
                $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for {repository}'s releases"
        };

    private static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out IEnumerable<string>? links))
        {
            return null;
        }

        foreach (string link in links.SelectMany(l => l.Split(',')))
        {
            string[] parts = link.Split(';', StringSplitOptions.TrimEntries);
            if (
                parts.Length >= 2
             && parts[1..].Any(p => p is "rel=\"next\"")
             && parts[0].StartsWith('<')
             && parts[0].EndsWith('>')
            )
            {
                return parts[0][1..^1];
            }
        }

        return null;
    }

    private static Release Convert(GitHubRelease r) =>
        new(r.TagName,
            r.Name ?? string.Empty,
            r.Prerelease,
            r.PublishedAt ?? r.CreatedAt ?? DateTimeOffset.MinValue,
            r.Body ?? string.Empty,
            r.HtmlUrl ?? string.Empty,
            [
                ..(r.Assets ?? []).Select(a => new ReleaseAsset(
                                                                a.Name,
                                                                a.Size,
                                                                a.BrowserDownloadUrl,
                                                                a.Url,
                                                                Sha256(a.Digest)
                                                               ))
            ])
        {
            Created = r.CreatedAt ?? r.PublishedAt ?? DateTimeOffset.MinValue
        };

    /// <summary>
    /// GitHub (and GitCode) report digests as <c>sha256:&lt;hex&gt;</c>.
    /// </summary>
    internal static string? Sha256(string? digest) =>
        digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest[7..].ToLowerInvariant()
            : null;
}
