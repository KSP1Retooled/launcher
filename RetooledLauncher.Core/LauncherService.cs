using KSP.Retooled.Launcher.Installs;
using KSP.Retooled.Launcher.Releases;
using KSP.Retooled.Patch;

namespace KSP.Retooled.Launcher;

/// <summary>What the launcher is doing: <see cref="Done"/> of <see cref="Total"/> (0 when unknown) in <see cref="Stage"/>.</summary>
public readonly record struct LauncherProgress(string Stage, long Done = 0, long Total = 0)
{
    public double? Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : null;
}

/// <summary>
/// Installs, changes and removes KSP Retooled on a KSP install. Patches are built against stock, so moving between
/// releases reverts to stock first and applies the other release's patch; if that apply fails, what was installed is
/// put back from its cached patch, so a failed upgrade does not leave the user without a working game. A release is one
/// of a channel's, or a local patch file, and moving between them works the same way.
/// </summary>
public sealed class LauncherService(IPatchProvider patches, string platform, TextWriter? log = null)
{
    readonly TextWriter log = log ?? TextWriter.Null;

    public string Platform { get; } = platform;

    /// <summary>Installs <paramref name="target"/>, replacing whatever is installed.</summary>
    /// <param name="catalog">Finds the installed release, to roll back to, if its cached patch is gone.</param>
    public async Task<InstallInfo> InstallAsync(
        string install,
        Release target,
        ReleaseCatalog catalog,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellation = default
    )
    {
        var info = KspInstall.Inspect(install);
        Require(info, InstallStatus.Stock, InstallStatus.Retooled);

        string patchPath = await patches
            .GetPatchAsync(target, Platform, progress, cancellation)
            .ConfigureAwait(false);
        return await ReplaceAsync(
                info,
                Open(patchPath, target.Tag),
                MarkerFor(target, patchPath),
                catalog,
                progress,
                cancellation
            )
            .ConfigureAwait(false);
    }

    /// <summary>Installs the patch file <paramref name="file"/>, replacing whatever is installed.</summary>
    /// <param name="catalog">Finds the installed release, to roll back to, if its cached patch is gone.</param>
    public async Task<InstallInfo> InstallFileAsync(
        string install,
        string file,
        ReleaseCatalog catalog,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellation = default
    )
    {
        var info = KspInstall.Inspect(install);
        Require(info, InstallStatus.Stock, InstallStatus.Retooled);

        string name = Path.GetFileName(file);
        Open(file, name); // not worth copying if it cannot be installed
        string patchPath = await patches
            .ImportAsync(file, progress, cancellation)
            .ConfigureAwait(false);
        return await ReplaceAsync(
                info,
                Open(patchPath, name),
                new InstalledRelease
                {
                    Tag = name,
                    Asset = name,
                    Channel = LauncherInfo.LocalChannel,
                    Patch = patchPath,
                },
                catalog,
                progress,
                cancellation
            )
            .ConfigureAwait(false);
    }

    async Task<InstallInfo> ReplaceAsync(
        InstallInfo info,
        PatchFile patch,
        InstalledRelease target,
        ReleaseCatalog catalog,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellation
    )
    {
        string install = info.Path;
        InstalledRelease? previous = null;
        if (info.Status == InstallStatus.Retooled)
        {
            previous = await RollbackTargetAsync(info.Release, catalog, progress, cancellation)
                .ConfigureAwait(false);
            log.WriteLine($"removing {info.Release?.Tag ?? "the installed version"}");
            await RevertAsync(install, progress).ConfigureAwait(false);
        }

        try
        {
            log.WriteLine($"installing {target.Tag}");
            await ApplyAsync(install, patch, target, progress, cancellation).ConfigureAwait(false);
        }
        catch (Exception failure)
            when (failure
                    is PatchException
                        or IOException
                        or UnauthorizedAccessException
                        or OperationCanceledException
                        or InvalidDataException
            )
        {
            log.WriteLine($"installing {target.Tag} failed: {failure.Message}");
            if (InstallJournal.Exists(install))
                await RevertAsync(install, progress).ConfigureAwait(false);
            else
                DeleteScratch(install);
            if (previous != null)
            {
                log.WriteLine($"putting {previous.Tag} back");
                // the user's cancel applies to the new install, not to restoring the old one
                await ApplyAsync(
                        install,
                        Open(previous.Patch!, previous.Tag),
                        previous,
                        progress,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
                throw new LauncherException(
                    $"installing {target.Tag} failed, so {previous.Tag} was put back: {failure.Message}",
                    failure
                );
            }
            if (failure is OperationCanceledException)
                throw;
            throw new LauncherException(
                $"installing {target.Tag} failed; the stock install was restored: {failure.Message}",
                failure
            );
        }

        patches.Prune([target.Patch!]);
        return KspInstall.Inspect(install);
    }

    /// <summary>
    /// What to restore if the install fails: the patch <paramref name="installed"/> was applied from, wherever it came
    /// from, or failing that its release if <paramref name="catalog"/> lists it. A release of another channel that
    /// happens to share the tag is a different build, so it does not count. Null when there is nothing to restore.
    /// </summary>
    async Task<InstalledRelease?> RollbackTargetAsync(
        InstalledRelease? installed,
        ReleaseCatalog catalog,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellation
    )
    {
        if (installed?.Patch != null && File.Exists(installed.Patch))
            return installed;

        var release = catalog.Find(installed?.Tag);
        if (
            release?.PatchFor(Platform) == null
            || !(string.IsNullOrEmpty(installed?.Channel) || installed.IsFrom(release.Channel))
        )
        {
            log.WriteLine(
                "warning: the installed version is not a known release; a failed install cannot roll back"
            );
            return null;
        }
        try
        {
            string path = await patches
                .GetPatchAsync(release, Platform, progress, cancellation)
                .ConfigureAwait(false);
            return MarkerFor(release, path);
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException or IOException)
        {
            log.WriteLine(
                $"warning: {release.Tag}'s patch is unavailable ({e.Message}); a failed install cannot roll back"
            );
            return null;
        }
    }

    static InstalledRelease MarkerFor(Release release, string patch) =>
        new()
        {
            Tag = release.Tag,
            Asset = Path.GetFileName(patch),
            Prerelease = release.Prerelease,
            Channel = release.Channel,
            Patch = patch,
        };

    /// <summary>Restores the stock install; also finishes an interrupted install or uninstall.</summary>
    public async Task<InstallInfo> UninstallAsync(
        string install,
        IProgress<LauncherProgress>? progress = null
    )
    {
        var info = KspInstall.Inspect(install);
        Require(info, InstallStatus.Retooled, InstallStatus.Interrupted);
        log.WriteLine($"removing {info.Release?.Tag ?? "KSP Retooled"}");
        await RevertAsync(info.Path, progress).ConfigureAwait(false);
        patches.Prune([]);
        return KspInstall.Inspect(info.Path);
    }

    Task ApplyAsync(
        string install,
        PatchFile patch,
        InstalledRelease release,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellation
    ) =>
        Task.Run(
            () =>
            {
                new PatchApplier(patch, install).Apply(
                    new ApplyOptions
                    {
                        Log = log,
                        Progress = Forward(progress, release.Tag),
                        Cancellation = cancellation,
                    }
                );
                release.Installed = DateTime.UtcNow;
                KspInstall.WriteMarker(install, release);
            },
            CancellationToken.None
        );

    Task RevertAsync(string install, IProgress<LauncherProgress>? progress) =>
        Task.Run(() => PatchReverter.Revert(install, log, Forward(progress, null)));

    PatchFile Open(string path, string tag)
    {
        PatchFile patch;
        try
        {
            patch = PatchFile.Open(path);
        }
        catch (InvalidDataException e)
        {
            throw new LauncherException($"{tag}'s patch cannot be read: {e.Message}", e);
        }
        if (patch.Manifest.Platform != Platform)
            throw new LauncherException(
                $"{tag}'s patch is for {patch.Manifest.Platform}, not {Platform}"
            );
        return patch;
    }

    /// <summary>An apply that failed before journalling leaves only its staging folder behind.</summary>
    static void DeleteScratch(string install)
    {
        string scratch = Path.Combine(install, InstallJournal.Directory);
        try
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
        catch (IOException) { }
    }

    static void Require(InstallInfo info, params InstallStatus[] allowed)
    {
        if (allowed.Contains(info.Status))
            return;
        throw new LauncherException(
            info.Status switch
            {
                InstallStatus.Missing => $"{info.Path} does not exist",
                InstallStatus.NotKsp =>
                    $"{info.Path} is not a KSP install (no GameData/Squad and game executable)",
                InstallStatus.Stock => $"{info.Path} does not have KSP Retooled installed",
                InstallStatus.Interrupted =>
                    "an earlier install or uninstall did not finish; uninstall first to restore the stock game",
                _ => $"{info.Path} already has KSP Retooled installed",
            }
        );
    }

    static IProgress<PatchProgress>? Forward(IProgress<LauncherProgress>? progress, string? tag) =>
        progress == null
            ? null
            : new SyncProgress<PatchProgress>(p =>
                progress.Report(
                    new LauncherProgress(
                        p.Stage switch
                        {
                            "verify" => "Checking the KSP files",
                            "write" => $"Installing {tag}",
                            "revert" => "Restoring the stock files",
                            _ => p.Stage,
                        },
                        p.Done,
                        p.Total
                    )
                )
            );

    /// <summary>Reports on the calling thread; <see cref="Progress{T}"/> would post every report to a sync context.</summary>
    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
