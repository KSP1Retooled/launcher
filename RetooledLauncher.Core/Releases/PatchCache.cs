using System.Security.Cryptography;

namespace KSP.Retooled.Launcher.Releases;

/// <summary>Where the launcher gets a release's patch file from.</summary>
public interface IPatchProvider
{
    /// <summary>A local path to <paramref name="release"/>'s patch for <paramref name="platform"/>, downloading it if needed.</summary>
    Task<string> GetPatchAsync(
        Release release,
        string platform,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellation
    );

    /// <summary>Drops cached patches of every release but <paramref name="keep"/>.</summary>
    void Prune(IEnumerable<string> keep);
}

/// <summary>
/// Downloads patch files into <c>&lt;cache&gt;/&lt;tag&gt;/</c> and keeps them, so the installed release's patch is at
/// hand to roll back to if an upgrade fails. A file is reused only when its size (and GitHub's digest, if any) match.
/// </summary>
public sealed class PatchCache(HttpClient http, GitHubReleaseSource source, string directory)
    : IPatchProvider
{
    public string Directory { get; } = directory;

    public async Task<string> GetPatchAsync(
        Release release,
        string platform,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellation
    )
    {
        var asset =
            release.PatchFor(platform)
            ?? throw new LauncherException(
                $"{release.Tag} has no {platform} build ({Release.PatchName(platform)})"
            );
        string path = PathFor(release.Tag, asset.Name);
        if (
            File.Exists(path)
            && await IsIntactAsync(path, asset, cancellation).ConfigureAwait(false)
        )
            return path;

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string partial = path + ".part";
        using (var request = source.AssetRequest(asset))
        using (
            var response = await http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellation
                )
                .ConfigureAwait(false)
        )
        {
            if (!response.IsSuccessStatusCode)
                throw new LauncherException(
                    $"downloading {asset.Name} of {release.Tag} failed: {(int)response.StatusCode} {response.ReasonPhrase}"
                );
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var body = await response
                .Content.ReadAsStreamAsync(cancellation)
                .ConfigureAwait(false);
            await using var file = new FileStream(
                partial,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1 << 16,
                useAsync: true
            );
            var buffer = new byte[1 << 16];
            long done = 0;
            for (
                int n;
                (n = await body.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0;

            )
            {
                await file.WriteAsync(buffer.AsMemory(0, n), cancellation).ConfigureAwait(false);
                done += n;
                progress?.Report(new LauncherProgress($"Downloading {release.Tag}", done, total));
            }
        }

        if (!await IsIntactAsync(partial, asset, cancellation).ConfigureAwait(false))
        {
            File.Delete(partial);
            throw new LauncherException(
                $"the download of {asset.Name} for {release.Tag} is corrupt; try again"
            );
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }

    public void Prune(IEnumerable<string> keep)
    {
        if (!System.IO.Directory.Exists(Directory))
            return;
        var kept = keep.Select(SafeName).ToHashSet(StringComparer.Ordinal);
        foreach (string dir in System.IO.Directory.EnumerateDirectories(Directory))
            if (!kept.Contains(Path.GetFileName(dir)))
                try
                {
                    System.IO.Directory.Delete(dir, recursive: true);
                }
                catch (IOException) { } // in use or locked: try again next time
                catch (UnauthorizedAccessException) { }
    }

    string PathFor(string tag, string asset) =>
        Path.Combine(Directory, SafeName(tag), Path.GetFileName(asset));

    /// <summary>A tag as a single directory name (tags may hold '/').</summary>
    static string SafeName(string tag) =>
        string.Concat(
            tag.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c
            )
        );

    static async Task<bool> IsIntactAsync(
        string path,
        ReleaseAsset asset,
        CancellationToken cancellation
    )
    {
        if (new FileInfo(path).Length != asset.Size)
            return false;
        if (asset.Sha256 == null)
            return true;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1 << 20,
            useAsync: true
        );
        byte[] hash = await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash) == asset.Sha256;
    }
}
