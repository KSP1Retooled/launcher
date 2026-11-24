namespace KSP.Retooled.Launcher.Releases;

public interface IReleaseSource
{
    Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken cancellation = default);

    /// <summary>
    /// Requests for <paramref name="asset"/> of <paramref name="release"/>, which this source listed, to try in turn.
    /// </summary>
    IEnumerable<HttpRequestMessage> AssetRequests(Release release, ReleaseAsset asset);
}
