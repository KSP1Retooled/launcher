using System.Text.Json;
using System.Transactions;
using ChinhDo.Transactions;

namespace KSP.Retooled.Patch;

public enum JournalState { Committing, Applied, Reverting }

public enum JournalAction { Replaced, Added, Removed }

/// <summary>
/// <c>.retooled/journal.json</c> in a patched install: every file an in-place apply replaced, added or removed, with
/// the stock originals kept under <c>.retooled/original</c>. Written before the first file moves, so a revert also
/// undoes an apply whose process died half way, which the transaction in <see cref="InstallCommit"/> cannot survive.
/// </summary>
public sealed class InstallJournal
{
    public const string Directory = ".retooled";
    public const string StagingDirectory = Directory + "/staging";
    public const string OriginalDirectory = Directory + "/original";
    public const string TransactionDirectory = Directory + "/transaction";
    internal const string FileName = Directory + "/journal.json";

    public int Version { get; set; } = 1;
    public JournalState State { get; set; }
    public string Platform { get; set; } = "";
    /// <summary>The source install the apply matched, or null for a mix.</summary>
    public string? Variant { get; set; }
    public string Patch { get; set; } = "";
    public DateTime Applied { get; set; }
    /// <summary>In the order the apply moves them; a revert goes through them backwards.</summary>
    public List<JournalFile> Files { get; set; } = [];
    /// <summary>Directories the apply created, parents first.</summary>
    public List<string> CreatedDirectories { get; set; } = [];
    /// <summary>Stock directories the apply deleted because removing stock files left them empty.</summary>
    public List<string> RemovedDirectories { get; set; } = [];

    public static bool Exists(string install) => File.Exists(Path.Combine(install, FileName));

    public static InstallJournal? Load(string install)
    {
        string path = Path.Combine(install, FileName);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize(File.ReadAllBytes(path), ManifestJson.Default.InstallJournal)
            ?? throw new InvalidDataException($"{path} is empty");
    }

    public void Save(string install)
    {
        string path = Path.Combine(install, FileName);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(this, ManifestJson.Default.InstallJournal));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Clears what a failed apply left in <see cref="Directory"/>. Best effort: it must not mask the failure.</summary>
    internal static void DeleteScratch(string install)
    {
        string scratch = Path.Combine(install, Directory);
        try
        {
            if (System.IO.Directory.Exists(scratch)) System.IO.Directory.Delete(scratch, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class JournalFile
{
    public string Path { get; set; } = "";
    public JournalAction Action { get; set; }
    /// <summary>SHA-256 of the stock file (replaced and removed files).</summary>
    public string? Original { get; set; }
    /// <summary>SHA-256 of the retooled file (replaced and added files).</summary>
    public string? Retooled { get; set; }
}

/// <summary>
/// The in-place half of <see cref="PatchApplier"/>: journals, backs up and moves staged outputs into the install, in a
/// transaction, so an exception puts every file back and leaves no journal. If that rollback fails too, or the process
/// dies, the journal stays and <see cref="PatchReverter"/> finishes the job.
/// </summary>
sealed class InstallCommit(string install, InstallJournal journal, Dictionary<string, string?> hashes, TextWriter log, Action<string>? beforeMove)
{
    /// <summary>
    /// The longest the runtime allows. A timeout rolls back on another thread while the moves carry on, so there can't be
    /// one, and an install on a slow disk can outlast the default limit of ten minutes.
    /// </summary>
    static readonly TimeSpan NoTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    static InstallCommit() => TransactionManager.MaximumTimeout = NoTimeout;

    string Full(string path) => Path.Combine(install, path);

    public void Run(string staging, List<OutputFile> staged, IEnumerable<string> remove, IEnumerable<string> directories, string? language)
    {
        var requested = directories.ToList();
        bool completed = false;
        Exception? failure = null;
        try
        {
            Plan(staged, remove, requested);
            journal.State = JournalState.Committing;
            journal.Save(install);

            // in the install, not the system temp directory: an empty directory is deleted by moving it here
            var files = new TxFileManager(Full(InstallJournal.TransactionDirectory));
            using var scope = new TransactionScope(TransactionScopeOption.Required, new TransactionOptions { Timeout = NoTimeout });
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
                throw new PatchException($"{failure?.Message} Undoing the install failed too: {e.InnerException?.Message}", e);
            InstallJournal.DeleteScratch(install);
            throw;
        }

        Directory.Delete(staging, recursive: true);
        Directory.Delete(Full(InstallJournal.TransactionDirectory), recursive: true);
        int removed = journal.Files.Count(f => f.Action == JournalAction.Removed);
        log.WriteLine($"moved {journal.Files.Count - removed} files into the install, removed {removed} stock files "
            + $"(originals kept in {InstallJournal.OriginalDirectory})");
    }

    void Plan(List<OutputFile> staged, IEnumerable<string> remove, List<string> directories)
    {
        string StockHash(string path) => hashes.GetValueOrDefault(path) ?? PatchApplier.Sha256Hex(Full(path));

        // removed files go first: a path can be a file in stock and a directory in the build (MainMenu.nib on macOS) or
        // the reverse, and the build's can only be made once stock's has left
        foreach (string path in remove.Distinct(StringComparer.Ordinal).Where(p => File.Exists(Full(p))))
            journal.Files.Add(new JournalFile { Path = path, Action = JournalAction.Removed, Original = StockHash(path) });
        foreach (var output in staged)
        {
            bool exists = File.Exists(Full(output.Path));
            journal.Files.Add(new JournalFile
            {
                Path = output.Path,
                Action = exists ? JournalAction.Replaced : JournalAction.Added,
                Original = exists ? StockHash(output.Path) : null,
                Retooled = output.Sha256,
            });
        }

        var created = new SortedSet<string>(StringComparer.Ordinal);
        var newPaths = journal.Files.Where(f => f.Action == JournalAction.Added).Select(f => Path.GetDirectoryName(f.Path)!);
        foreach (string directory in newPaths.Concat(directories))
            for (string? d = directory.Replace('\\', '/'); !string.IsNullOrEmpty(d) && !Directory.Exists(Full(d)); d = Path.GetDirectoryName(d)?.Replace('\\', '/'))
                created.Add(d);
        journal.CreatedDirectories = [.. created.OrderBy(d => d.Count(c => c == '/')).ThenBy(d => d, StringComparer.Ordinal)];
    }

    void Commit(IFileManager files, string staging, List<string> directories, string? language)
    {
        foreach (var file in journal.Files)
        {
            string destination = Full(file.Path);
            if (file.Action != JournalAction.Added) Move(files, destination, Full(InstallJournal.OriginalDirectory + "/" + file.Path));
            if (file.Action != JournalAction.Removed) Move(files, Path.Combine(staging, file.Path), destination);
            else PruneEmptyParents(files, destination);
        }
        foreach (string directory in directories) files.CreateDirectory(Full(directory));

        if (language != null)
        {
            files.Snapshot(Full(GameSettingsFile.FileName));
            if (GameSettingsFile.SetLanguage(install, language))
                log.WriteLine($"set LANGUAGE = {language} in {GameSettingsFile.FileName}");
        }

        // last, so a rollback that stops short leaves the journal saying the apply didn't finish
        files.Snapshot(Full(InstallJournal.FileName));
        journal.State = JournalState.Applied;
        journal.Save(install);
    }

    void Move(IFileManager files, string from, string to)
    {
        beforeMove?.Invoke(to);
        files.CreateDirectory(Path.GetDirectoryName(to)!);
        files.Move(from, to);
    }

    void PruneEmptyParents(IFileManager files, string file)
    {
        for (string? dir = Path.GetDirectoryName(file); dir != null && dir.Length > install.Length
             && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any(); dir = Path.GetDirectoryName(dir))
        {
            files.DeleteDirectory(dir);
            journal.RemovedDirectories.Add(Path.GetRelativePath(install, dir).Replace('\\', '/'));
        }
    }

    internal static void MoveFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to, overwrite: true);
    }
}

/// <summary>
/// Restores the stock install an in-place apply patched, from its <see cref="InstallJournal"/>: originals go back,
/// added files and directories go, and Retooled's runtime caches are deleted. Saves, settings, logs and anything else
/// the apply did not touch stay as they are.
/// </summary>
public static class PatchReverter
{
    /// <summary>Caches only Retooled writes (the texture cache), relative to the install.</summary>
    static readonly string[] RuntimeArtifacts = ["PluginData/TextureCache"];

    public static void Revert(string install, TextWriter? log = null, IProgress<PatchProgress>? progress = null)
    {
        install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(install));
        log ??= TextWriter.Null;
        var journal = InstallJournal.Load(install) ?? throw new PatchException($"{install} has no {InstallJournal.Directory} journal; it is not patched");
        // a backup can be missing only where an earlier apply or revert stopped before moving it
        bool interrupted = journal.State != JournalState.Applied;
        journal.State = JournalState.Reverting;
        journal.Save(install);

        string Full(string path) => Path.Combine(install, path);
        var problems = new List<string>();
        int restored = 0, deleted = 0, done = 0;
        for (int i = journal.Files.Count - 1; i >= 0; i--)
        {
            var file = journal.Files[i];
            string path = Full(file.Path), backup = Full(InstallJournal.OriginalDirectory + "/" + file.Path);
            if (file.Action == JournalAction.Added)
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
                if (journal.CreatedDirectories.Contains(file.Path) && Directory.Exists(path)) Directory.Delete(path, recursive: true);
                InstallCommit.MoveFile(backup, path);
                restored++;
            }
            else if (!interrupted) problems.Add($"missing backup {InstallJournal.OriginalDirectory}/{file.Path}");
            progress?.Report(new PatchProgress("revert", ++done, journal.Files.Count));
        }

        foreach (string directory in journal.CreatedDirectories.AsEnumerable().Reverse())
            if (Directory.Exists(Full(directory)) && !Directory.EnumerateFileSystemEntries(Full(directory)).Any())
                Directory.Delete(Full(directory));
        foreach (string directory in journal.RemovedDirectories) Directory.CreateDirectory(Full(directory));

        foreach (string artifact in RuntimeArtifacts.Where(a => Directory.Exists(Full(a))))
        {
            Directory.Delete(Full(artifact), recursive: true);
            string parent = Path.GetDirectoryName(Full(artifact))!;
            if (parent.Length > install.Length && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }

        if (problems.Count > 0)
            throw new PatchException($"restored {restored} files but {problems.Count} originals are gone; the journal is kept:\n  "
                + string.Join("\n  ", problems));
        Directory.Delete(Full(InstallJournal.Directory), recursive: true);
        log.WriteLine($"reverted {install}: restored {restored} stock files, deleted {deleted} retooled files");
    }
}
