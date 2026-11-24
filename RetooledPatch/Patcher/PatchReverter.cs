using KSP.Retooled.Patch.Exceptions;
using KSP.Retooled.Patch.Install;
using KSP.Retooled.Patch.Models.Journal;

namespace KSP.Retooled.Patch.Patcher;

/// <summary>
/// Restores the stock install an in-place apply patched, from its <see cref="InstallJournal"/>: originals go back,
/// added files and directories go, and Retooled's runtime caches are deleted. Saves, settings, logs and anything else
/// the apply did not touch stay as they are.
/// </summary>
public static class PatchReverter
{
    /// <summary>
    /// Caches only Retooled writes (the texture cache), relative to the install.
    /// </summary>
    private static readonly string[] RuntimeArtifacts = ["PluginData/TextureCache"];

    // ReSharper disable once CognitiveComplexity
    public static void Revert(string install, TextWriter? log = null, IProgress<PatchProgress>? progress = null)
    {
        install =   Path.TrimEndingDirectorySeparator(Path.GetFullPath(install));
        log     ??= TextWriter.Null;
        InstallJournal journal = InstallJournal.Load(install) ?? throw new PatchException($"{install} has no {InstallJournal.DIRECTORY} journal; it is not patched");
        // a backup can be missing only where an earlier apply or revert stopped before moving it
        bool interrupted = journal.State != JournalState.APPLIED;
        journal.State = JournalState.REVERTING;
        journal.Save(install);

        string Full(string path) => Path.Combine(install, path);

        List<string> problems = [];
        int restored = 0, deleted = 0, done = 0;
        for (int i = journal.Files.Count - 1; i >= 0; i--)
        {
            JournalFile file = journal.Files[i];
            string path = Full(file.Path), backup = Full(InstallJournal.ORIGINAL_DIRECTORY + "/" + file.Path);
            if (file.Action == JournalAction.ADDED)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            else if (File.Exists(backup))
            {
                // the directory the apply made in this file's place: what it added has gone, and anything else there (Finder's .DS_Store) goes too
                if (journal.CreatedDirectories.Contains(file.Path) && Directory.Exists(path)) Directory.Delete(path, true);
                InstallCommit.MoveFile(backup, path);
                restored++;
            }
            else if (!interrupted) problems.Add($"missing backup {InstallJournal.ORIGINAL_DIRECTORY}/{file.Path}");

            progress?.Report(new PatchProgress("revert", ++done, journal.Files.Count));
        }

        foreach (string directory in journal.CreatedDirectories.AsEnumerable().Reverse())
        {
            if (Directory.Exists(Full(directory)) && !Directory.EnumerateFileSystemEntries(Full(directory)).Any())
            {
                Directory.Delete(Full(directory));
            }
        }

        foreach (string directory in journal.RemovedDirectories) Directory.CreateDirectory(Full(directory));

        foreach (string artifact in RuntimeArtifacts.Where(a => Directory.Exists(Full(a))))
        {
            Directory.Delete(Full(artifact), true);
            string parent = Path.GetDirectoryName(Full(artifact))!;
            if (parent.Length > install.Length && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }

        if (problems.Count > 0)
        {
            throw new PatchException($"restored {restored} files but {problems.Count} originals are gone; the journal is kept:\n  "
                                   + string.Join("\n  ", problems));
        }

        Directory.Delete(Full(InstallJournal.DIRECTORY), true);
        log.WriteLine($"reverted {install}: restored {restored} stock files, deleted {deleted} retooled files");
    }
}
