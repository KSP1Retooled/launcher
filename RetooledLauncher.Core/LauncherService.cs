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
/// releases reverts to stock first and applies the other release's patch; if that apply fails, the release that was
/// installed is put back from its cached patch, so a failed upgrade does not leave the user without a working game.
/// </summary>
public sealed class LauncherService(IPatchProvider patches, string platform, TextWriter? log = null)
{
    readonly TextWriter log = log ?? TextWriter.Null;

    public string Platform { get; } = platform;

    /// <summary>Installs <paramref name="target"/>, replacing whatever release is installed.</summary>
    /// <param name="catalog">Finds the installed release, to roll back to.</param>
    public async Task<InstallInfo> InstallAsync(
        string install,
        Release target,
        ReleaseCatalog catalog,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellation = default
    )
    {
        var info = KspInstall.Inspect(install);
        install = info.Path;
        Require(info, InstallStatus.Stock, InstallStatus.Retooled);

        string patchPath = await patches
            .GetPatchAsync(target, Platform, progress, cancellation)
            .ConfigureAwait(false);
        var patch = Open(patchPath, target.Tag);

        string? rollback = null;
        Release? previous = null;
        if (info.Status == InstallStatus.Retooled)
        {
            previous = catalog.Find(info.Release?.Tag);
            if (previous?.PatchFor(Platform) != null)
                try
                {
                    rollback = await patches
                        .GetPatchAsync(previous, Platform, progress, cancellation)
                        .ConfigureAwait(false);
                }
                catch (Exception e)
                    when (e is LauncherException or HttpRequestException or IOException)
                {
                    log.WriteLine(
                        $"warning: {previous.Tag}'s patch is unavailable ({e.Message}); a failed install cannot roll back"
                    );
                }
            else
                log.WriteLine(
                    "warning: the installed version is not a known release; a failed install cannot roll back"
                );

            log.WriteLine($"removing {previous?.Tag ?? "the installed version"}");
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
            if (rollback != null && previous != null)
            {
                log.WriteLine($"putting {previous.Tag} back");
                // the user's cancel applies to the new install, not to restoring the old one
                await ApplyAsync(
                        install,
                        Open(rollback, previous.Tag),
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

        patches.Prune([target.Tag]);
        return KspInstall.Inspect(install);
    }

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
        Release release,
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
                KspInstall.WriteMarker(
                    install,
                    new InstalledRelease
                    {
                        Tag = release.Tag,
                        Asset = Path.GetFileName(patch.FilePath),
                        Prerelease = release.Prerelease,
                        Installed = DateTime.UtcNow,
                    }
                );
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
