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

    /// <summary>
    /// A path to a copy of the patch file <paramref name="file"/> that outlives the original, so it is still there to
    /// roll back to when the user has moved or rebuilt it.
    /// </summary>
    Task<string> ImportAsync(
        string file,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellation
    );

    /// <summary>Drops every cached patch but the files in <paramref name="keep"/>, paths this provider returned.</summary>
    void Prune(IEnumerable<string> keep);
}

/// <summary>
/// Downloads patch files into <c>&lt;cache&gt;/&lt;channel&gt;@&lt;tag&gt;/</c> and keeps them, so the installed release's
/// patch is at hand to roll back to if an upgrade fails. A file is reused only when its size (and GitHub's digest, if
/// any) match. Local patch files are copied into <c>&lt;cache&gt;/local@&lt;sha256&gt;/</c>: by content, because a rebuilt
/// file of the same name must not overwrite the copy of the installed one.
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
                $"{release.Tag} has no {platform} build ({release.PatchNameFor(platform)})"
            );
        string path = Path.Combine(
            EntryFor(release.Channel, release.Tag),
            Path.GetFileName(asset.Name)
        );
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

    public async Task<string> ImportAsync(
        string file,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellation
    )
    {
        string name = Path.GetFileName(file);
        progress?.Report(new LauncherProgress($"Reading {name}"));
        string hash = await Sha256Async(file, cancellation).ConfigureAwait(false);
        string path = Path.Combine(EntryFor(LauncherInfo.LocalChannel, hash), name);
        if (File.Exists(path))
            return path;

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string partial = path + ".part";
        await using (var input = OpenRead(file))
        await using (
            var output = new FileStream(
                partial,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1 << 20,
                useAsync: true
            )
        )
            await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
        File.Move(partial, path, overwrite: true);
        return path;
    }

    public void Prune(IEnumerable<string> keep)
    {
        if (!System.IO.Directory.Exists(Directory))
            return;
        var kept = keep.Select(p => Path.GetFileName(Path.GetDirectoryName(p)))
            .ToHashSet(StringComparer.Ordinal);
        foreach (string dir in System.IO.Directory.EnumerateDirectories(Directory))
            if (!kept.Contains(Path.GetFileName(dir)))
                try
                {
                    System.IO.Directory.Delete(dir, recursive: true);
                }
                catch (IOException) { } // in use or locked: try again next time
                catch (UnauthorizedAccessException) { }
    }

    string EntryFor(string channel, string tag) =>
        Path.Combine(Directory, SafeName($"{channel}@{tag}"));

    /// <summary>A channel and tag as a single directory name (both may hold '/').</summary>
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
        return asset.Sha256 == null
            || await Sha256Async(path, cancellation).ConfigureAwait(false) == asset.Sha256;
    }

    static async Task<string> Sha256Async(string path, CancellationToken cancellation)
    {
        await using var stream = OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
}
