using KSP.Retooled.Patch.Models.Manifest;
using KSP.Retooled.Patch.Patcher;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace KSP.Retooled.Patch.IO;

public sealed class SectionReader : IDisposable
{
    private readonly FileStream file;
    private readonly Section section;
    private readonly DecompressionStream zstd;
    private byte[]? skipBuffer;

    internal SectionReader(string path, Section section)
    {
        this.section = section;
        this.file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        this.file.Position = section.Offset;
        this.zstd = new DecompressionStream(new BoundedStream(this.file, section.Length), 1 << 20, true, false);
        this.zstd.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, PatchFile.MAX_WINDOW_LOG);
    }

    /// <summary>
    /// Decompressed bytes consumed so far.
    /// </summary>
    private long Position { get; set; }

    public void Dispose()
    {
        this.zstd.Dispose();
        this.file.Dispose();
    }

    public void SkipTo(long offset)
    {
        if (offset < this.Position || offset > this.section.Size)
        {
            throw new InvalidDataException($"section {this.section.Name}: blob at {offset} is behind the reader ({this.Position}) or past the end");
        }

        this.skipBuffer ??= new byte[1 << 20];
        while (this.Position < offset) Read(this.skipBuffer.AsSpan(0, (int)Math.Min(this.skipBuffer.Length, offset - this.Position)));
    }

    public void Read(Span<byte> buffer)
    {
        this.zstd.ReadExactly(buffer);
        this.Position += buffer.Length;
    }
}
