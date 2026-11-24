using System.Text.Json;
using RetooledLauncher.Common;

namespace KSP.Retooled.Patch.Models.Manifest;

/// <summary>
/// Everything a patch file knows: the stock files of every source install it was built from (<see cref="Sources"/>,
/// one entry per distinct path and content), which of them each known install has (<see cref="Variants"/>), and how
/// to produce every file of the retooled install. Each output lists one or more <see cref="Recipe"/>s; an apply uses
/// the first whose stock files are all present with the recorded content, so any install whose files match one source
/// (or a mix of them) reaches the same target, except that an output every source has unchanged is left as the install
/// has it. Paths are install-relative with '/' separators.
/// </summary>
public sealed class PatchManifest
{
    public const int CURRENT_VERSION = 3;
    /// <summary>
    /// The oldest manifest a reader still accepts; version 3 only added <see cref="CopiedPaths"/>.
    /// </summary>
    public const int OLDEST_VERSION = 2;

    public int Version { get; set; } = CURRENT_VERSION;

    /// <summary>
    /// <c>windows</c>, <c>linux</c> or <c>macos</c>: the player layout of the target build.
    /// </summary>
    public Platform Platform { get; set; } = Platform.UNSUPPORTED;

    /// <summary>
    /// Install-relative files and directories the patch knows nothing about: nothing in them is checked, read, replaced or
    /// removed, and a side-by-side apply copies them across whole, because the install's copy is the only one.
    /// </summary>
    public List<string> CopiedPaths { get; set; } = [];

    public List<SourceFile> Sources { get; set; } = [];

    public List<Variant> Variants { get; set; } = [];

    public List<Component> Components { get; set; } = [];

    public List<Section> Sections { get; set; } = [];

    /// <summary>
    /// Sorted by component, then path; recipes' blobs appear in each section in this order.
    /// </summary>
    public List<OutputFile> Outputs { get; set; } = [];

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, PatcherJsonContext.Default.PatchManifest);

    public static PatchManifest FromJson(ReadOnlySpan<byte> json) => JsonSerializer.Deserialize(json, PatcherJsonContext.Default.PatchManifest)
                                                                  ?? throw new InvalidDataException("empty manifest");
}
