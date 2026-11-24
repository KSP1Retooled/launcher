namespace KSP.Retooled.Launcher.Installs.Saves;

/// <summary>
/// A backup in <see cref="SavesBackup.BACKUP_FOLDER"/> and the record stored in it.
/// </summary>
public sealed record SavesBackupInfo(string Path, SavesBackupRecord Record);
