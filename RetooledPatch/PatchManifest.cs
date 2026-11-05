using System.Text.Json;
using System.Text.Json.Serialization;

namespace KSP.Retooled.Patch;

/// <summary>
/// Everything a patch file knows: the stock files of every source install it was built from (<see cref="Sources"/>,
/// one entry per distinct path and content), which of them each known install has (<see cref="Variants"/>), and how
/// to produce every file of the retooled install. Each output lists one or more <see cref="Recipe"/>s; an apply uses
/// the first whose stock files are all present with the recorded content, so any install whose files match one source
/// (or a mix of them) reaches the same target, except that an output with one recipe, a stock file at its own path, is
/// left as the install has it. Paths are install-relative with '/' separators.
/// </summary>
public sealed class PatchManifest
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    /// <summary><c>windows</c>, <c>linux</c> or <c>macos</c>: the player layout of the target build.</summary>
    public string Platform { get; set; } = "";
    public List<SourceFile> Sources { get; set; } = [];
    public List<Variant> Variants { get; set; } = [];
    public List<Component> Components { get; set; } = [];
    public List<Section> Sections { get; set; } = [];
    /// <summary>Sorted by component, then path; recipes' blobs appear in each section in this order.</summary>
    public List<OutputFile> Outputs { get; set; } = [];

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, ManifestJson.Default.PatchManifest);

    public static PatchManifest FromJson(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, ManifestJson.Default.PatchManifest) ?? throw new InvalidDataException("empty manifest");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PatchManifest))]
[JsonSerializable(typeof(InstallJournal))]
sealed partial class ManifestJson : JsonSerializerContext;

/// <summary>A stock file as one or more source installs have it.</summary>
public sealed class SourceFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>One source install the patch was built from, e.g. <c>steam-03190</c>, and the <see cref="PatchManifest.Sources"/> it has.</summary>
public sealed class Variant
{
    public string Id { get; set; } = "";
    public List<int> Files { get; set; } = [];
}

/// <summary>
/// Core or a DLC. A DLC's outputs are produced only when its <see cref="Requires"/> directory is installed.
/// </summary>
public sealed class Component
{
    public string Name { get; set; } = "";
    /// <summary>Install-relative directory that must exist for this component to apply (a DLC); null for core.</summary>
    public string? Requires { get; set; }
    /// <summary>Stock files of any source that the retooled install has no counterpart for; an in-place apply removes them.</summary>
    public List<string> Remove { get; set; } = [];
    /// <summary>Empty directories of the retooled install (stock's Missions, sounds, ...), created by every apply.</summary>
    public List<string> Directories { get; set; } = [];
}

/// <summary>
/// One independent zstd frame of the patch file holding raw blobs: <see cref="Offset"/> and <see cref="Length"/> locate
/// it in the file, <see cref="Size"/> is its decompressed length.
/// </summary>
public sealed class Section
{
    public string Name { get; set; } = "";
    public long Offset { get; set; }
    public long Length { get; set; }
    public long Size { get; set; }
}

public sealed class OutputFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>Index into <see cref="PatchManifest.Components"/>.</summary>
    public int Component { get; set; }
    /// <summary>An ELF or Mach-O binary or a script, given the executable mode on Linux and macOS.</summary>
    public bool Executable { get; set; }
    public List<Recipe> Recipes { get; set; } = [];
}

/// <summary>
/// One way to produce an output: identical to stock file <see cref="Same"/>, or <see cref="Segments"/> whose blobs
/// start at decompressed offset <see cref="Offset"/> of section <see cref="Section"/>.
/// </summary>
public sealed class Recipe
{
    public int? Same { get; set; }
    public int? Section { get; set; }
    public long? Offset { get; set; }
    public List<Segment>? Segments { get; set; }

    /// <summary>The <see cref="PatchManifest.Sources"/> this recipe reads.</summary>
    public IEnumerable<int> Inputs() =>
        Same is int same ? [same] : (Segments ?? []).SelectMany(s => s.Refs?.Select(r => r.File) ?? (s.File is int f ? [f] : []));

    [JsonIgnore]
    public long ArchiveBytes => Segments?.Sum(s => s.ArchiveBytes) ?? 0;
}

public enum SegmentKind { Copy, BsDiff, Literal }

/// <summary>
/// One run of output bytes. <c>Copy</c> reads <see cref="Length"/> bytes of source <see cref="File"/> at
/// <see cref="Offset"/>. <c>BsDiff</c> reads a <see cref="Blob"/>-byte raw bsdiff patch from the section and applies
/// it to the concatenated <see cref="Refs"/>, producing <see cref="Length"/> bytes. <c>Literal</c> reads
/// <see cref="Length"/> bytes from the section.
/// </summary>
public sealed class Segment
{
    public SegmentKind Kind { get; set; }
    public int? File { get; set; }
    public long? Offset { get; set; }
    public long Length { get; set; }
    public List<Extent>? Refs { get; set; }
    public long? Blob { get; set; }

    public static Segment Copy(int file, long offset, long length) =>
        new() { Kind = SegmentKind.Copy, File = file, Offset = offset, Length = length };

    public static Segment Diff(List<Extent> refs, long length, long blob) =>
        new() { Kind = SegmentKind.BsDiff, Refs = refs, Length = length, Blob = blob };

    public static Segment Literal(long length) => new() { Kind = SegmentKind.Literal, Length = length };

    /// <summary>Bytes this segment consumes from its section.</summary>
    [JsonIgnore]
    public long ArchiveBytes => Kind switch
    {
        SegmentKind.BsDiff => Blob ?? 0,
        SegmentKind.Literal => Length,
        _ => 0,
    };
}

public sealed class Extent
{
    public int File { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }

    public Extent() { }

    public Extent(int file, long offset, long length)
    {
        File = file;
        Offset = offset;
        Length = length;
    }
}
