using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace KSP.Retooled.Launcher.Installs;

/// <summary>
/// The version a saves backup was taken from, stored in the backup as <see cref="SavesBackup.RECORD_ENTRY"/>.
/// </summary>
public sealed class SavesBackupRecord
{
    /// <summary>
    /// The installed Retooled release's tag, <see cref="SavesBackup.STOCK_VERSION"/>, or "" when unknown.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// True when the saves are from stock KSP rather than a Retooled release.
    /// </summary>
    public bool Stock { get; set; }

    /// <summary>
    /// The installed release's channel; null for stock or an install the launcher has no record of.
    /// </summary>
    public string? Channel { get; set; }

    public DateTime Created { get; set; }

    /// <summary>
    /// The version of <paramref name="info"/>'s saves: what is installed before anything changes.
    /// </summary>
    public static SavesBackupRecord For(InstallInfo info) =>
        info.Status == InstallStatus.STOCK
            ? new SavesBackupRecord { Version = SavesBackup.STOCK_VERSION, Stock = true }
            : new SavesBackupRecord
            {
                Version = info.Release?.Tag ?? string.Empty,
                Channel = info.Release?.Channel
            };
}

/// <summary>
/// A backup in <see cref="SavesBackup.BACKUP_FOLDER"/> and the record stored in it.
/// </summary>
public sealed record SavesBackupInfo(string Path, SavesBackupRecord Record);

/// <summary>
/// Zips an install's <c>saves</c> folder to <c>saves-backup/&lt;date&gt;.zip</c> before the launcher installs a
/// release.
/// Saves made with one Retooled release may not load in another, so the backup is how a player gets their game back.
/// The zip holds the folder as <c>saves/</c>, so extracting it into the install restores it, and a
/// <see cref="RECORD_ENTRY"/> naming the version the saves were made with.
/// </summary>
public static class SavesBackup
{
    public const string SAVES_FOLDER = "saves";
    public const string BACKUP_FOLDER = "saves-backup";
    public const string RECORD_ENTRY = "backup.json";
    public const string STOCK_VERSION = "1.12.5";

    /// <summary>
    /// How many of the newest backups <see cref="Prune"/> keeps, besides the newest stock one.
    /// </summary>
    public const int KEEP = 10;

    private const string EXTENSION = ".zip";

    /// <summary>
    /// Zips <paramref name="install"/>'s saves to a new file under <see cref="BACKUP_FOLDER"/>, named after the local
    /// date of <see cref="SavesBackupRecord.Created"/>, with " (2)", " (3)" and so on added when that name is taken.
    /// Returns the new file, or null when there are no saves. A zip that fails part way is deleted, so every backup is
    /// complete.
    /// </summary>
    public static string? Create(string install,
                                 SavesBackupRecord record,
                                 IProgress<LauncherProgress>? progress = null,
                                 CancellationToken cancellation = default)
    {
        string saves = Path.Combine(install, SAVES_FOLDER);
        if (!Directory.Exists(saves))
        {
            return null;
        }

        List<FileInfo> files =
        [
            ..Directory
             .EnumerateFiles(saves, "*", SearchOption.AllDirectories)
             .Select(f => new FileInfo(f))
        ];

        if (files.Count is 0)
        {
            return null;
        }

        string backups = Path.Combine(install, BACKUP_FOLDER);
        Directory.CreateDirectory(backups);
        string target = FreeName(backups, record.Created.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        // written under a temporary name, so an interrupted zip never looks like a backup
        string partial = target + ".partial";
        long total = files.Sum(f => f.Length);
        long done = 0;
        try
        {
            using (ZipArchive zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = zip.CreateEntry(RECORD_ENTRY);
                using (Stream stream = entry.Open())
                {
                    JsonSerializer.Serialize(stream, record, InstallJson.Default.SavesBackupRecord);
                }

                foreach (string directory in EmptyDirectories(saves))
                {
                    zip.CreateEntry(EntryName(saves, directory) + "/");
                }

                foreach (FileInfo file in files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(file.FullName,
                                            EntryName(saves, file.FullName),
                                            CompressionLevel.Optimal);

                    done += file.Length;
                    progress?.Report(new LauncherProgress("Backing up saves", done, total));
                }
            }

            File.Move(partial, target);
        }
        catch
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            throw;
        }

        return target;
    }

    /// <summary>
    /// The backups in <paramref name="install"/>'s <see cref="BACKUP_FOLDER"/>, newest first. Zips without a readable
    /// record are left out, so files the user put there are never pruned.
    /// </summary>
    public static IReadOnlyList<SavesBackupInfo> List(string install)
    {
        string backups = Path.Combine(install, BACKUP_FOLDER);
        if (!Directory.Exists(backups))
        {
            return [];
        }

        List<SavesBackupInfo> list = [];
        foreach (string file in Directory.EnumerateFiles(backups, "*" + EXTENSION))
        {
            SavesBackupRecord? record = ReadRecord(file);
            if (record is not null)
            {
                list.Add(new SavesBackupInfo(file, record));
            }
        }

        return [..list.OrderByDescending(b => b.Record.Created)];
    }

    /// <summary>
    /// Deletes all but the <see cref="KEEP"/> newest backups, and the newest backup of stock saves, so the player can
    /// always go back to stock KSP however many releases they install. Returns the deleted files.
    /// </summary>
    public static IReadOnlyList<string> Prune(string install)
    {
        IReadOnlyList<SavesBackupInfo> backups = List(install);
        SavesBackupInfo? stock = backups.FirstOrDefault(b => b.Record.Stock);
        List<string> deleted = [];
        foreach (SavesBackupInfo backup in backups.Skip(KEEP))
        {
            if (backup == stock)
            {
                continue;
            }

            File.Delete(backup.Path);
            deleted.Add(backup.Path);
        }

        return deleted;
    }

    private static SavesBackupRecord? ReadRecord(string file)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(file);
            using Stream? stream = zip.GetEntry(RECORD_ENTRY)?.Open();
            return stream is not null
                       ? JsonSerializer.Deserialize(stream, InstallJson.Default.SavesBackupRecord)
                       : null;
        }
        catch (Exception e) when (e is IOException
                                    or InvalidDataException
                                    or JsonException
                                    or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EmptyDirectories(string saves)
    {
        return Directory
              .EnumerateDirectories(saves, "*", SearchOption.AllDirectories)
              .Where(d => !Directory.EnumerateFileSystemEntries(d).Any());
    }

    private static string EntryName(string saves, string path) => SAVES_FOLDER + "/" + Path.GetRelativePath(saves, path).Replace('\\', '/');

    private static string FreeName(string backups, string name)
    {
        string path = Path.Combine(backups, name + EXTENSION);
        for (int n = 2; File.Exists(path) || Directory.Exists(path); n++)
        {
            path = Path.Combine(backups, $"{name} ({n}){EXTENSION}");
        }

        return path;
    }
}
