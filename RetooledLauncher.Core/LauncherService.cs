using KSP.Retooled.Launcher.Exceptions;
using KSP.Retooled.Launcher.Installs;
using KSP.Retooled.Launcher.Installs.Saves;
using KSP.Retooled.Launcher.Patches;
using KSP.Retooled.Launcher.Releases;
using KSP.Retooled.Launcher.Utils;
using KSP.Retooled.Patch.Exceptions;
using KSP.Retooled.Patch.Models.Journal;
using KSP.Retooled.Patch.Patcher;

namespace KSP.Retooled.Launcher;

/// <summary>
/// Installs, changes and removes KSP Retooled on a KSP install. Patches are built against stock, so moving between
/// releases reverts to stock first and applies the other release's patch; if that apply fails, what was installed is
/// put back from its cached patch, so a failed upgrade does not leave the user without a working game. A release is
/// one
/// of a channel's, or a local patch file, and moving between them works the same way.
/// </summary>
public sealed class LauncherService(IPatchProvider patches, string platform, TextWriter? log = null)
{
    private readonly TextWriter log = log ?? TextWriter.Null;

    private string Platform { get; } = platform;

    /// <summary>
    /// Installs <paramref name="target"/>, replacing whatever is installed.
    /// </summary>
    /// <param name="install">Install path</param>
    /// <param name="target">Release target to install</param>
    /// <param name="catalog">Finds the installed release, to roll back to, if its cached patch is gone.</param>
    /// <param name="progress">Installation progress</param>
    /// <param name="cancellation">Cancellation token</param>
    public async Task<InstallInfo> InstallAsync(string install,
                                                Release target,
                                                ReleaseCatalog catalog,
                                                IProgress<LauncherProgress>? progress = null,
                                                CancellationToken cancellation = default)
    {
        InstallInfo info = KspInstall.Inspect(install);
        Require(info, InstallStatus.STOCK, InstallStatus.RETOOLED);

        await this.log.WriteLineAsync($"installing KSP Retooled {target.Tag}");
        string patchPath = await patches.GetPatchAsync(target, this.Platform, progress, cancellation)
                                        .ConfigureAwait(false);

        return await ReplaceAsync(info,
                                  Open(patchPath, target.Tag),
                                  MarkerFor(target, patchPath),
                                  catalog,
                                  progress,
                                  cancellation)
                  .ConfigureAwait(false);
    }

    /// <summary>
    /// Installs the patch file <paramref name="file"/>, replacing whatever is installed.
    /// </summary>
    /// <param name="install">Installation path</param>
    /// <param name="file">File to patch</param>
    /// <param name="catalog">Finds the installed release, to roll back to, if its cached patch is gone.</param>
    /// <param name="progress">Installation progress</param>
    /// <param name="cancellation">Cancellation token</param>
    public async Task<InstallInfo> InstallFileAsync(string install,
                                                    string file,
                                                    ReleaseCatalog catalog,
                                                    IProgress<LauncherProgress>? progress = null,
                                                    CancellationToken cancellation = default)
    {
        InstallInfo info = KspInstall.Inspect(install);
        Require(info, InstallStatus.STOCK, InstallStatus.RETOOLED);

        string name = Path.GetFileName(file);
        await this.log.WriteLineAsync($"installing KSP Retooled from {name}"); // a patch file does not record its version
        Open(file, name);                                                      // not worth copying if it cannot be installed
        string patchPath = await patches
                                .ImportAsync(file, progress, cancellation)
                                .ConfigureAwait(false);

        return await ReplaceAsync(info,
                                  Open(patchPath, name),
                                  new InstalledRelease
                                  {
                                      Tag     = name,
                                      Asset   = name,
                                      Channel = LauncherInfo.LOCAL_CHANNEL,
                                      Patch   = patchPath
                                  },
                                  catalog,
                                  progress,
                                  cancellation)
                  .ConfigureAwait(false);
    }

    private async Task<InstallInfo> ReplaceAsync(InstallInfo info,
                                                 PatchFile patch,
                                                 InstalledRelease target,
                                                 ReleaseCatalog catalog,
                                                 IProgress<LauncherProgress>? progress,
                                                 CancellationToken cancellation)
    {
        string install = info.Path;
        // taken while the old release is still installed, so the backup records the version the saves were made with
        await BackUpSavesAsync(info, progress, cancellation).ConfigureAwait(false);

        InstalledRelease? previous = null;
        if (info.Status == InstallStatus.RETOOLED)
        {
            previous = await RollbackTargetAsync(info.Release, catalog, progress, cancellation)
                          .ConfigureAwait(false);

            await this.log.WriteLineAsync($"removing {info.Release?.Tag ?? "the installed version"}");
            await RevertAsync(install, progress).ConfigureAwait(false);
        }

        try
        {
            await ApplyAsync(install, patch, target, progress, cancellation).ConfigureAwait(false);
        }
        catch (Exception failure)
            when (failure is PatchException
                          or IOException
                          or UnauthorizedAccessException
                          or OperationCanceledException
                          or InvalidDataException)
        {
            await this.log.WriteLineAsync($"installing {target.Tag} failed: {failure.Message}");
            if (InstallJournal.Exists(install))
            {
                await RevertAsync(install, progress).ConfigureAwait(false);
            }

            if (previous is not null)
            {
                await this.log.WriteLineAsync($"putting {previous.Tag} back");
                // the user's cancel applies to the new install, not to restoring the old one
                await ApplyAsync(install,
                                 Open(previous.Patch!, previous.Tag),
                                 previous,
                                 progress,
                                 CancellationToken.None)
                   .ConfigureAwait(false);

                throw new LauncherException($"installing {target.Tag} failed, so {previous.Tag} was put back: {failure.Message}", failure);
            }

            if (failure is OperationCanceledException)
            {
                throw;
            }

            throw new LauncherException($"installing {target.Tag} failed; the stock install was restored: {failure.Message}", failure);
        }

        patches.Prune([target.Patch!]);
        return KspInstall.Inspect(install);
    }

    /// <summary>
    /// Zips the saves before anything changes, since saves from one release may not load in another, then deletes the
    /// backups <see cref="SavesBackup.Prune"/> no longer keeps. If the backup fails, the install stops there and the game
    /// is left as it was.
    /// </summary>
    private async Task BackUpSavesAsync(InstallInfo info,
                                        IProgress<LauncherProgress>? progress,
                                        CancellationToken cancellation)
    {
        string install = info.Path;
        SavesBackupRecord record = SavesBackupRecord.For(info);
        record.Created = DateTime.UtcNow;

        string? backup;
        try
        {
            backup = await Task.Run(() => SavesBackup.Create(install, record, progress, cancellation),
                                    CancellationToken.None)
                               .ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"backing up the saves failed, so nothing was installed: {e.Message}", e);
        }

        if (backup is null)
        {
            return;
        }

        await this.log.WriteLineAsync($"backed up the {(record.Version == string.Empty ? string.Empty : record.Version + " ")}saves to {Path.GetRelativePath(install, backup)}");

        try
        {
            foreach (string deleted in SavesBackup.Prune(install))
            {
                await this.log.WriteLineAsync($"deleted the old backup {Path.GetRelativePath(install, deleted)}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await this.log.WriteLineAsync($"warning: old backups could not be deleted: {e.Message}");
        }
    }

    /// <summary>
    /// What to restore if the install fails:the patch <paramref name="installed"/> was applied from, wherever it came
    /// from, or failing that its release if <paramref name="catalog"/> lists it. A release of another channel that
    /// happens to share the tag is a different build, so it does not count. Null when there is nothing to restore.
    /// </summary>
    private async Task<InstalledRelease?> RollbackTargetAsync(InstalledRelease? installed,
                                                              ReleaseCatalog catalog,
                                                              IProgress<LauncherProgress>? progress,
                                                              CancellationToken cancellation)
    {
        if (installed?.Patch is not null && File.Exists(installed.Patch))
        {
            return installed;
        }

        Release? release = catalog.Find(installed?.Tag);
        if (release?.PatchFor(this.Platform) is null
         || !(string.IsNullOrEmpty(installed?.Channel) || installed.IsFrom(release.Channel)))
        {
            await this.log.WriteLineAsync("warning: the installed version is not a known release; a failed install cannot roll back");
            return null;
        }

        try
        {
            string path = await patches.GetPatchAsync(release, this.Platform, progress, cancellation)
                                       .ConfigureAwait(false);

            return MarkerFor(release, path);
        }
        catch (Exception e) when (e is LauncherException or HttpRequestException or IOException)
        {
            await this.log.WriteLineAsync($"warning: {release.Tag}'s patch is unavailable ({e.Message}); a failed install cannot roll back");
            return null;
        }
    }

    private InstalledRelease MarkerFor(Release release, string patch) => new()
    {
        Tag        = release.Tag,
        Asset      = Path.GetFileName(patch),
        Prerelease = release.Prerelease,
        Channel    = release.Channel,
        Patch      = patch,
        Sha256     = release.PatchFor(this.Platform)?.Sha256
    };

    /// <summary>
    /// Restores the stock install; also finishes an interrupted install or uninstall.
    /// </summary>
    public async Task<InstallInfo> UninstallAsync(string install,
                                                  IProgress<LauncherProgress>? progress = null)
    {
        InstallInfo info = KspInstall.Inspect(install);
        Require(info, InstallStatus.RETOOLED, InstallStatus.INTERRUPTED);
        await this.log.WriteLineAsync($"removing {info.Release?.Tag ?? "KSP Retooled"}");
        await RevertAsync(info.Path, progress).ConfigureAwait(false);
        patches.Prune([]);
        return KspInstall.Inspect(info.Path);
    }

    private Task ApplyAsync(string install,
                            PatchFile patch,
                            InstalledRelease release,
                            IProgress<LauncherProgress>? progress,
                            CancellationToken cancellation)
    {
        return Task.Run(() =>
                        {
                            new PatchApplier(patch, install).Apply(new ApplyOptions
                            {
                                Log          = this.log,
                                Progress     = Forward(progress, release.Tag),
                                Cancellation = cancellation
                            });

                            release.Installed = DateTime.UtcNow;
                            KspInstall.WriteMarker(install, release);
                        },
                        CancellationToken.None);
    }

    private Task RevertAsync(string install, IProgress<LauncherProgress>? progress)
    {
        return Task.Run(() => PatchReverter.Revert(install, this.log, Forward(progress, null)));
    }

    private PatchFile Open(string path, string tag)
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

        return patch.Manifest.Platform == this.Platform
                   ? patch
                   : throw new LauncherException($"{tag}'s patch is for {patch.Manifest.Platform}, not {this.Platform}");
    }

    private static void Require(InstallInfo info, params InstallStatus[] allowed)
    {
        if (allowed.Contains(info.Status))
        {
            return;
        }

        throw new LauncherException(info.Status switch
        {
            InstallStatus.MISSING => $"{info.Path} does not exist",
            InstallStatus.NOT_KSP =>
                $"{info.Path} is not a KSP install (no GameData/Squad and game executable)",
            InstallStatus.STOCK => $"{info.Path} does not have KSP Retooled installed",
            InstallStatus.INTERRUPTED =>
                "an earlier install or uninstall did not finish; uninstall first to restore the stock game",
            _ => $"{info.Path} already has KSP Retooled installed"
        });
    }

    private static IProgress<PatchProgress>? Forward(IProgress<LauncherProgress>? progress, string? tag)
    {
        return progress is not null
                   ? new SyncProgress<PatchProgress>(p => progress.Report(new LauncherProgress(p.Stage switch
                                                                                               {
                                                                                                   "verify" => "Checking the KSP files",
                                                                                                   "write"  => $"Installing {tag}",
                                                                                                   "revert" => "Restoring the stock files",
                                                                                                   _        => p.Stage
                                                                                               },
                                                                                               p.Done,
                                                                                               p.Total)))
                   : null;
    }

    /// <summary>
    /// Reports on the calling thread; <see cref="Progress{T}"/> would post every report to a sync context.
    /// </summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
