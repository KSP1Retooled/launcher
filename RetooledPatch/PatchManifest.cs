using System.Text.Json;
using System.Text.Json.Serialization;

namespace KSP.Retooled.Patch;

/// <summary>
/// Recipe for one patch component: the stock files it reads (verified by hash before anything is written) and how to
/// produce every file of the retooled install from them. Paths are install-relative with '/' separators.
/// </summary>
public sealed class PatchManifest
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Component { get; set; } = "";
    /// <summary>Install-relative directory that must exist for this component to apply (a DLC); null for core.</summary>
    public string? Requires { get; set; }
    public List<BaseFile> Base { get; set; } = [];
    public List<OutputFile> Outputs { get; set; } = [];
    /// <summary>Stock files an in-place apply deletes because the retooled install has no counterpart.</summary>
    public List<string> Remove { get; set; } = [];

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, ManifestJson.Default.PatchManifest);

    public static PatchManifest FromJson(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize(json, ManifestJson.Default.PatchManifest) ?? throw new InvalidDataException("empty manifest");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PatchManifest))]
sealed partial class ManifestJson : JsonSerializerContext;

public sealed class BaseFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class OutputFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>Index into <see cref="PatchManifest.Base"/> of a stock file with identical content; no segments then.</summary>
    public int? Same { get; set; }
    public List<Segment>? Segments { get; set; }
}

public enum SegmentKind { Copy, BsDiff, Literal }

/// <summary>
/// One run of output bytes. <c>Copy</c> reads <see cref="Length"/> bytes of base file <see cref="File"/> at
/// <see cref="Offset"/>. <c>BsDiff</c> reads a <see cref="Blob"/>-byte raw bsdiff patch from the archive and applies
/// it to the concatenated <see cref="Refs"/>, producing <see cref="Length"/> bytes. <c>Literal</c> reads
/// <see cref="Length"/> bytes from the archive.
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

    /// <summary>Bytes this segment consumes from the archive's blob region.</summary>
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
