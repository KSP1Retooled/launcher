namespace KSP.Retooled.Patch.Models.Manifest;

/// <summary>
/// A stock file as one or more source installs have it.
/// </summary>
public sealed class SourceFile
{
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;
}
