namespace KSP.Retooled.Launcher.Installs.Saves;

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
