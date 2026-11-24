using System.Text.Json;
using KSP.Retooled.Launcher.Exceptions;
using KSP.Retooled.Launcher.Releases.GitCode;
using KSP.Retooled.Launcher.Releases.GitHub;
using KSP.Retooled.Launcher.Settings;
using KSP.Retooled.Launcher.Utils;

namespace KSP.Retooled.Launcher.Releases;

/// <summary>
/// A GitHub repository with a GitCode copy, for when one of them cannot be reached. Releases are listed from the
/// preferred host, and from the other if that fails; a patch is downloaded from the preferred host, falling back to
/// the other's copy of the same release. The asset's size and digest are checked either way.
/// </summary>
public sealed class MirroredReleaseSource(GitHubReleaseSource github,
                                          GitCodeReleaseSource gitcode,
                                          ReleaseMirror preferred = ReleaseMirror.GitHub,
                                          Action<string>? log = null) : IReleaseSource
{
    /// <summary>
    /// How long the preferred host gets to list releases before the other is asked instead.
    /// </summary>
    private TimeSpan PreferredTimeout { get; } = TimeSpan.FromSeconds(30);

    private bool GitCodeFirst => preferred is ReleaseMirror.GitCode;

    private IReleaseSource First => this.GitCodeFirst ? gitcode : github;

    private IReleaseSource Second => this.GitCodeFirst ? github : gitcode;

    private string FirstName => this.GitCodeFirst ? this.GitCodeName : this.GitHubName;

    private string SecondName => this.GitCodeFirst ? this.GitHubName : this.GitCodeName;

    private string GitHubName => $"github.com/{github.Repository}";

    private string GitCodeName => $"gitcode.com/{gitcode.Repository}";

    public async Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken cancellation = default)
    {
        Exception failure;
        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            timeout.CancelAfter(this.PreferredTimeout);
            try
            {
                return await this.First.GetReleasesAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (IsUnreachable(e, cancellation))
            {
                failure = e;
            }
        }

        string reason = failure is OperationCanceledException
                            ? $"{this.FirstName} timed out"
                            : failure.Message;

        log?.Invoke($"listing releases from {this.FirstName} failed ({reason}); trying {this.SecondName}");
        try
        {
            return await this.Second.GetReleasesAsync(cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (IsUnreachable(e, cancellation))
        {
            throw new LauncherException($"{reason}, and {this.SecondName} failed too: {e.Message}", failure);
        }
    }

    public IEnumerable<HttpRequestMessage> AssetRequests(Release release, ReleaseAsset asset)
    {
        bool fromGitCode = GitCodeReleaseSource.IsGitCodeAsset(asset);
        IEnumerable<HttpRequestMessage> onGitHub = fromGitCode
                                                       ? Copy(github, github.DownloadUrl(release.Tag, asset.Name))
                                                       : github.AssetRequests(release, asset);

        IEnumerable<HttpRequestMessage> onGitCode = fromGitCode
                                                        ? gitcode.AssetRequests(release, asset)
                                                        : Copy(gitcode, gitcode.DownloadUrl(release.Tag, asset.Name));

        return this.GitCodeFirst
                   ? onGitCode.Concat(Fallback(onGitHub))
                   : onGitHub.Concat(Fallback(onGitCode));

        // built lazily, so that the second host is only named once the first has failed
        IEnumerable<HttpRequestMessage> Copy(IReleaseSource host, string url) =>
            host.AssetRequests(release, asset with { DownloadUrl = url, ApiUrl = url });

        IEnumerable<HttpRequestMessage> Fallback(IEnumerable<HttpRequestMessage> requests)
        {
            log?.Invoke($"downloading {asset.Name} from {this.FirstName} failed; trying {this.SecondName}");
            foreach (HttpRequestMessage request in requests)
            {
                yield return request;
            }
        }
    }

    /// <summary>
    /// The source for <paramref name="channel"/>: a mirrored one when its repository has a GitCode copy, otherwise
    /// GitHub alone.
    /// </summary>
    public static IReleaseSource For(HttpClient http,
                                     string channel,
                                     string? token = null,
                                     ReleaseMirror preferred = ReleaseMirror.GitHub,
                                     Action<string>? log = null,
                                     GitHubMetadataCache? cache = null)
    {
        GitHubReleaseSource github = new(http, channel, token, cache);
        return LauncherInfo.MirrorOf(github.Repository) is { } repository
                   ? new MirroredReleaseSource(github,
                                               new GitCodeReleaseSource(http, channel, repository),
                                               preferred,
                                               log)
                   : github;
    }

    /// <summary>
    /// Failures that the other host might get around; the user cancelling is not one.
    /// </summary>
    internal static bool IsUnreachable(Exception e, CancellationToken cancellation) => e is LauncherException
                                                                                         or HttpRequestException
                                                                                         or JsonException
                                                                                         or IOException
                                                                                    || (e is OperationCanceledException && !cancellation.IsCancellationRequested);
}
