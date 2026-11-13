using System.Text.Json;

namespace KSP.Retooled.Launcher.Releases;

/// <summary>
/// A GitHub repository with a GitCode copy, for when one of them cannot be reached. Releases are listed from the
/// preferred host, and from the other if that fails; a patch is downloaded from the preferred host, falling back to
/// the other's copy of the same release. The asset's size and digest are checked either way.
/// </summary>
public sealed class MirroredReleaseSource(
    GitHubReleaseSource github,
    GitCodeReleaseSource gitcode,
    ReleaseMirror preferred = ReleaseMirror.GitHub,
    Action<string>? log = null
) : IReleaseSource
{
    /// <summary>How long the preferred host gets to list releases before the other is asked instead.</summary>
    public TimeSpan PreferredTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The source for <paramref name="channel"/>: a mirrored one when its repository has a GitCode copy, otherwise
    /// GitHub alone.
    /// </summary>
    public static IReleaseSource For(
        HttpClient http,
        string channel,
        string? token = null,
        ReleaseMirror preferred = ReleaseMirror.GitHub,
        Action<string>? log = null
    )
    {
        var github = new GitHubReleaseSource(http, channel, token);
        return LauncherInfo.MirrorOf(github.Repository) is string repository
            ? new MirroredReleaseSource(
                github,
                new GitCodeReleaseSource(http, channel, repository),
                preferred,
                log
            )
            : github;
    }

    bool GitCodeFirst => preferred == ReleaseMirror.GitCode;
    IReleaseSource First => GitCodeFirst ? gitcode : github;
    IReleaseSource Second => GitCodeFirst ? github : gitcode;
    string FirstName => GitCodeFirst ? GitCodeName : GitHubName;
    string SecondName => GitCodeFirst ? GitHubName : GitCodeName;
    string GitHubName => $"github.com/{github.Repository}";
    string GitCodeName => $"gitcode.com/{gitcode.Repository}";

    public async Task<IReadOnlyList<Release>> GetReleasesAsync(
        CancellationToken cancellation = default
    )
    {
        Exception failure;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            timeout.CancelAfter(PreferredTimeout);
            try
            {
                return await First.GetReleasesAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (IsUnreachable(e, cancellation))
            {
                failure = e;
            }
        }
        string reason = failure is OperationCanceledException
            ? $"{FirstName} timed out"
            : failure.Message;
        log?.Invoke($"listing releases from {FirstName} failed ({reason}); trying {SecondName}");
        try
        {
            return await Second.GetReleasesAsync(cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (IsUnreachable(e, cancellation))
        {
            throw new LauncherException(
                $"{reason}, and {SecondName} failed too: {e.Message}",
                failure
            );
        }
    }

    public IEnumerable<HttpRequestMessage> AssetRequests(Release release, ReleaseAsset asset)
    {
        bool fromGitCode = GitCodeReleaseSource.IsGitCodeAsset(asset);
        var onGitHub = fromGitCode
            ? Copy(github, github.DownloadUrl(release.Tag, asset.Name))
            : github.AssetRequests(release, asset);
        var onGitCode = fromGitCode
            ? gitcode.AssetRequests(release, asset)
            : Copy(gitcode, gitcode.DownloadUrl(release.Tag, asset.Name));
        return GitCodeFirst
            ? onGitCode.Concat(Fallback(onGitHub))
            : onGitHub.Concat(Fallback(onGitCode));

        // built lazily, so that the second host is only named once the first has failed
        IEnumerable<HttpRequestMessage> Copy(IReleaseSource host, string url) =>
            host.AssetRequests(release, asset with { DownloadUrl = url, ApiUrl = url });

        IEnumerable<HttpRequestMessage> Fallback(IEnumerable<HttpRequestMessage> requests)
        {
            log?.Invoke($"downloading {asset.Name} from {FirstName} failed; trying {SecondName}");
            foreach (var request in requests)
                yield return request;
        }
    }

    /// <summary>Failures that the other host might get around; the user cancelling is not one.</summary>
    internal static bool IsUnreachable(Exception e, CancellationToken cancellation) =>
        e
            is LauncherException
                or HttpRequestException
                or JsonException
                or IOException
        || (e is OperationCanceledException && !cancellation.IsCancellationRequested);
}
