namespace KSP.Retooled.Launcher.Installs;

/// <summary>
/// Copies an install's <c>saves</c> folder to <c>saves-backup/&lt;date&gt;</c> before the launcher installs a release.
/// Saves made with one Retooled release may not load in another, so the copy is how a player gets their game back.
/// </summary>
public static class SavesBackup
{
    public const string SavesFolder = "saves";
    public const string BackupFolder = "saves-backup";

    /// <summary>
    /// Copies <paramref name="install"/>'s saves to a new folder under <see cref="BackupFolder"/>, named after
    /// <paramref name="date"/>, with " (2)", " (3)" and so on added when that name is taken. Returns the new folder, or
    /// null when there are no saves to copy. A copy that fails part way is deleted, so every backup folder is complete.
    /// </summary>
    public static string? Create(
        string install,
        DateTime date,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellation = default
    )
    {
        string saves = Path.Combine(install, SavesFolder);
        if (!Directory.Exists(saves))
            return null;

        var files = Directory
            .EnumerateFiles(saves, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .ToList();
        if (files.Count == 0)
            return null;

        string backups = Path.Combine(install, BackupFolder);
        Directory.CreateDirectory(backups);
        string target = FreeName(backups, date.ToString("yyyy-MM-dd"));

        // copied under a temporary name, so an interrupted copy never looks like a backup
        string partial = target + ".partial";
        if (Directory.Exists(partial))
            Directory.Delete(partial, recursive: true);

        long total = files.Sum(f => f.Length);
        long done = 0;
        try
        {
            foreach (
                var directory in Directory.EnumerateDirectories(
                    saves,
                    "*",
                    SearchOption.AllDirectories
                )
            )
                Directory.CreateDirectory(
                    Path.Combine(partial, Path.GetRelativePath(saves, directory))
                );

            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                string copy = Path.Combine(partial, Path.GetRelativePath(saves, file.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                file.CopyTo(copy);
                done += file.Length;
                progress?.Report(new LauncherProgress("Backing up saves", done, total));
            }

            Directory.Move(partial, target);
        }
        catch
        {
            try
            {
                if (Directory.Exists(partial))
                    Directory.Delete(partial, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }

        return target;
    }

    static string FreeName(string backups, string name)
    {
        string path = Path.Combine(backups, name);
        for (int n = 2; Directory.Exists(path) || File.Exists(path); n++)
            path = Path.Combine(backups, $"{name} ({n})");
        return path;
    }
}
