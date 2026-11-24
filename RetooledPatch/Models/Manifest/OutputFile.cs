namespace KSP.Retooled.Patch.Models.Manifest;

public sealed class OutputFile
{
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Index into <see cref="PatchManifest.Components"/>.
    /// </summary>
    public int Component { get; set; }

    /// <summary>
    /// An ELF or Mach-O binary or a script, given the executable mode on Linux and macOS.
    /// </summary>
    public bool Executable { get; set; }

    public List<Recipe> Recipes { get; set; } = [];
}
