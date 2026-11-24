using System.Text.Json;
using KSP.Retooled.Patch.Install;
using RetooledLauncher.Common;

namespace KSP.Retooled.Patch.Models.Journal;

public enum JournalState
{
    COMMITTING,
    APPLIED,
    REVERTING
}

/// <summary>
/// <c>.retooled/journal.json</c> in a patched install: every file an in-place apply replaced, added or removed, with
/// the stock originals kept under <c>.retooled/original</c>. Written before the first file moves, so a revert also
/// undoes an apply whose process died half way, which the transaction in <see cref="InstallCommit"/> cannot survive.
/// </summary>
public sealed class InstallJournal
{
    public const string DIRECTORY = ".retooled";
    public const string STAGING_DIRECTORY = DIRECTORY + "/staging";
    public const string ORIGINAL_DIRECTORY = DIRECTORY + "/original";
    public const string TRANSACTION_DIRECTORY = DIRECTORY + "/transaction";
    internal const string FILE_NAME = DIRECTORY + "/journal.json";

    public int Version { get; set; } = 1;

    public JournalState State { get; set; }

    public Platform Platform { get; set; } = Platform.UNSUPPORTED;

    /// <summary>
    /// The source install the apply matched, or null for a mix.
    /// </summary>
    public string? Variant { get; set; }

    public string Patch { get; set; } = string.Empty;

    public DateTime Applied { get; set; }

    /// <summary>
    /// In the order the apply moves them; a revert goes through them backwards.
    /// </summary>
    public List<JournalFile> Files { get; set; } = [];

    /// <summary>
    /// Directories the apply created, parents first.
    /// </summary>
    public List<string> CreatedDirectories { get; set; } = [];

    /// <summary>
    /// Stock directories the apply deleted because removing stock files left them empty.
    /// </summary>
    public List<string> RemovedDirectories { get; set; } = [];

    public static bool Exists(string install) => File.Exists(Path.Combine(install, FILE_NAME));

    public static InstallJournal? Load(string install)
    {
        string path = Path.Combine(install, FILE_NAME);
        if (!File.Exists(path)) return null;

        return JsonSerializer.Deserialize(File.ReadAllBytes(path), PatcherJsonContext.Default.InstallJournal)
            ?? throw new InvalidDataException($"{path} is empty");
    }

    public void Save(string install)
    {
        string path = Path.Combine(install, FILE_NAME);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(this, PatcherJsonContext.Default.InstallJournal));
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>
    /// Clears what a failed apply left in <see cref="DIRECTORY"/>. Best effort: it must not mask the failure.
    /// </summary>
    internal static void DeleteScratch(string install)
    {
        string scratch = Path.Combine(install, DIRECTORY);
        try
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
