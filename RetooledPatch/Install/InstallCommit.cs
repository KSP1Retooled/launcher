using System.Transactions;
using ChinhDo.Transactions;
using KSP.Retooled.Patch.Exceptions;
using KSP.Retooled.Patch.Models.Journal;
using KSP.Retooled.Patch.Models.Manifest;
using KSP.Retooled.Patch.Patcher;
using KSP.Retooled.Patch.Utils;

namespace KSP.Retooled.Patch.Install;

/// <summary>
/// The in-place half of <see cref="PatchApplier"/>: journals, backs up and moves staged outputs into the install, in
/// a
/// transaction, so an exception puts every file back and leaves no journal. If that rollback fails too, or the process
/// dies, the journal stays and <see cref="PatchReverter"/> finishes the job.
/// </summary>
internal sealed class InstallCommit(string install, InstallJournal journal, Dictionary<string, string?> hashes, TextWriter log, Action<string>? beforeMove)
{
    /// <summary>
    /// The longest the runtime allows. A timeout rolls back on another thread while the moves carry on, so there can't be
    /// one, and an install on a slow disk can outlast the default limit of ten minutes.
    /// </summary>
    private static readonly TimeSpan NoTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    static InstallCommit() => TransactionManager.MaximumTimeout = NoTimeout;

    private string Full(string path) => Path.Combine(install, path);

    public void Run(string staging, List<OutputFile> staged, IEnumerable<string> remove, IEnumerable<string> directories, string? language)
    {
        List<string> requested = [..directories];
        bool completed = false;
        Exception? failure = null;
        try
        {
            string[] removeArray = remove as string[] ?? [..remove];
            SharedLinks.Unshare(install,
                                staged.Select(o => o.Path)
                                      .Concat(removeArray)
                                      .Concat(requested)
                                      .Append(GameSettingsFile.FILE_NAME),
                                log);

            Plan(staged, removeArray, requested);
            journal.State = JournalState.COMMITTING;
            journal.Save(install);

            // in the install, not the system temp directory: an empty directory is deleted by moving it here
            TxFileManager files = new(Full(InstallJournal.TRANSACTION_DIRECTORY));
            using TransactionScope scope = new(TransactionScopeOption.Required, new TransactionOptions { Timeout = NoTimeout });
            try
            {
                Commit(files, staging, requested, language);
                scope.Complete();
                completed = true;
            }
            catch (Exception e)
            {
                failure = e;
                throw;
            }
        }
        // once completed the files are in place and the originals are only in the scratch directory, so it has to stay
        catch (Exception e) when (!completed)
        {
            // a failed rollback replaces the exception that caused it, and stops at the first file it can't put back
            if (e is TransactionException)
            {
                throw new PatchException($"{failure?.Message} Undoing the install failed too: {e.InnerException?.Message}", e);
            }

            InstallJournal.DeleteScratch(install);
            throw;
        }

        Directory.Delete(staging, true);
        Directory.Delete(Full(InstallJournal.TRANSACTION_DIRECTORY), true);
        int removed = journal.Files.Count(f => f.Action == JournalAction.REMOVED);
        log.WriteLine($"moved {journal.Files.Count - removed} files into the install, removed {removed} stock files "
                    + $"(originals kept in {InstallJournal.ORIGINAL_DIRECTORY})");
    }

    private void Plan(List<OutputFile> staged, IEnumerable<string> remove, List<string> directories)
    {
        string StockHash(string path) => hashes.GetValueOrDefault(path) ?? PatchApplier.Sha256Hex(Full(path));

        // removed files go first: a path can be a file in stock and a directory in the build (MainMenu.nib on macOS) or
        // the reverse, and the build's can only be made once stock's has left
        foreach (string path in remove.Distinct(StringComparer.Ordinal).Where(p => File.Exists(Full(p))))
        {
            journal.Files.Add(new JournalFile { Path = path, Action = JournalAction.REMOVED, Original = StockHash(path) });
        }

        foreach (OutputFile output in staged)
        {
            bool exists = File.Exists(Full(output.Path));
            journal.Files.Add(new JournalFile
            {
                Path     = output.Path,
                Action   = exists ? JournalAction.REPLACED : JournalAction.ADDED,
                Original = exists ? StockHash(output.Path) : null,
                Retooled = output.Sha256
            });
        }

        SortedSet<string> created = new(StringComparer.Ordinal);
        IEnumerable<string> newPaths = journal.Files.Where(f => f.Action == JournalAction.ADDED).Select(f => Path.GetDirectoryName(f.Path)!);
        foreach (string directory in newPaths.Concat(directories))
        {
            for (string? d = directory.Replace('\\', '/'); !string.IsNullOrEmpty(d) && !Directory.Exists(Full(d)); d = Path.GetDirectoryName(d)?.Replace('\\', '/'))
            {
                created.Add(d);
            }
        }

        journal.CreatedDirectories = [..created.OrderBy(d => d.Count(c => c == '/')).ThenBy(d => d, StringComparer.Ordinal)];
    }

    private void Commit(IFileManager files, string staging, List<string> directories, string? language)
    {
        foreach (JournalFile file in journal.Files)
        {
            string destination = Full(file.Path);
            if (file.Action != JournalAction.ADDED) Move(files, destination, Full(InstallJournal.ORIGINAL_DIRECTORY + "/" + file.Path));
            if (file.Action != JournalAction.REMOVED)
            {
                Move(files, Path.Combine(staging, file.Path), destination);
            }
            else
            {
                PruneEmptyParents(files, destination);
            }
        }

        foreach (string directory in directories) files.CreateDirectory(Full(directory));

        if (language is not null && GameSettingsFile.WithLanguage(install, language) is { } settings)
        {
            // Stage a replacement to preserve the settings of any install that shares the original file.
            string staged = Path.Combine(staging, GameSettingsFile.FILE_NAME);
            File.WriteAllText(staged, settings);
            files.Delete(Full(GameSettingsFile.FILE_NAME));
            files.Move(staged, Full(GameSettingsFile.FILE_NAME));
            log.WriteLine($"set LANGUAGE = {language} in {GameSettingsFile.FILE_NAME}");
        }

        // last, so a rollback that stops short leaves the journal saying the apply didn't finish
        files.Snapshot(Full(InstallJournal.FILE_NAME));
        journal.State = JournalState.APPLIED;
        journal.Save(install);
    }

    private void Move(IFileManager files, string from, string to)
    {
        beforeMove?.Invoke(to);
        files.CreateDirectory(Path.GetDirectoryName(to)!);
        files.Move(from, to);
    }

    private void PruneEmptyParents(IFileManager files, string file)
    {
        for (string? dir = Path.GetDirectoryName(file);
             dir is not null && dir.Length > install.Length
                             && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any();
             dir = Path.GetDirectoryName(dir))
        {
            files.DeleteDirectory(dir);
            journal.RemovedDirectories.Add(Path.GetRelativePath(install, dir).Replace('\\', '/'));
        }
    }

    internal static void MoveFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to, true);
    }
}
