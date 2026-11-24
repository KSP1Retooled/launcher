using System.Text.Json.Serialization;

namespace KSP.Retooled.Patch.Models.Manifest;

public enum SegmentKind
{
    [JsonStringEnumMemberName("Copy")]
    COPY,
    [JsonStringEnumMemberName("BsDiff")]
    BS_DIFF,
    [JsonStringEnumMemberName("Literal")]
    LITERAL
}

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

    /// <summary>
    /// Bytes this segment consumes from its section.
    /// </summary>
    [JsonIgnore]
    public long ArchiveBytes => this.Kind switch
    {
        SegmentKind.BS_DIFF => this.Blob ?? 0,
        SegmentKind.LITERAL => this.Length,
        _                   => 0
    };

    public static Segment Copy(int file, long offset, long length) => new()
    {
        Kind   = SegmentKind.COPY,
        File   = file,
        Offset = offset,
        Length = length
    };

    public static Segment Diff(List<Extent> refs, long length, long blob) => new()
    {
        Kind   = SegmentKind.BS_DIFF,
        Refs   = refs,
        Length = length,
        Blob   = blob
    };

    public static Segment Literal(long length) => new()
    {
        Kind   = SegmentKind.LITERAL,
        Length = length
    };
}
