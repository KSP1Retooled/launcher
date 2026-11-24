namespace KSP.Retooled.Patch.Models.Journal;

public enum JournalAction
{
    REPLACED,
    ADDED,
    REMOVED
}

public sealed class JournalFile
{
    public string Path { get; set; } = string.Empty;

    public JournalAction Action { get; set; }

    /// <summary>
    /// SHA-256 of the stock file (replaced and removed files).
    /// </summary>
    public string? Original { get; set; }

    /// <summary>
    /// SHA-256 of the retooled file (replaced and added files).
    /// </summary>
    public string? Retooled { get; set; }
}
