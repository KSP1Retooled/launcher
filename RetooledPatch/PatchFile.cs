using System.Buffers.Binary;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace KSP.Retooled.Patch;

/// <summary>
/// A patch file (<c>retooled-&lt;platform&gt;.rtpatch</c>): <c>RTPATCH2</c>, the manifest's offset and length (uint64 LE
/// each), then the sections, each an independent zstd frame of raw blobs, then the manifest JSON as one zstd frame.
/// An apply decompresses only the sections its recipes read, skipping forward within them past blobs other installs
/// need.
/// </summary>
public sealed class PatchFile
{
    public const string EXTENSION = ".rtpatch";
    internal static readonly byte[] Magic = [.."RTPATCH2"u8];
    internal const int HEADER_SIZE = 24;

    /// <summary>
    /// Largest window a reader accepts (2 GB); the writer's window must not exceed it.
    /// </summary>
    internal const int MAX_WINDOW_LOG = 31;

    public string FilePath { get; }

    public PatchManifest Manifest { get; }

    private PatchFile(string path, PatchManifest manifest)
    {
        this.FilePath = path;
        this.Manifest    = manifest;
    }

    public static PatchFile Open(string path)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[HEADER_SIZE];
        if (file.Length < HEADER_SIZE || file.ReadAtLeast(header, HEADER_SIZE, throwOnEndOfStream: false) < HEADER_SIZE
            || !header[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException($"{path} is not a retooled patch file (or was made by an older patch tool)");
        }

        long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        long length = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        if (offset < HEADER_SIZE || length <= 0 || offset + length > file.Length)
        {
            throw new InvalidDataException($"{path}: truncated patch file");
        }

        byte[] compressed = new byte[length];
        file.Position = offset;
        file.ReadExactly(compressed);
        using Decompressor decompressor = new();
        PatchManifest manifest = PatchManifest.FromJson(decompressor.Unwrap(compressed));
        if (manifest.Version is < PatchManifest.OLDEST_VERSION or > PatchManifest.CURRENT_VERSION)
        {
            throw new InvalidDataException($"{path}: manifest version {manifest.Version}, this tool reads "
                                         + $"{PatchManifest.OLDEST_VERSION} to {PatchManifest.CURRENT_VERSION}");
        }

        Section? section = manifest.Sections.FirstOrDefault(section => section.Offset < HEADER_SIZE || section.Offset + section.Length > offset);
        return section is null
                   ? new PatchFile(Path.GetFullPath(path), manifest)
                   : throw new InvalidDataException($"{path}: section {section.Name} lies outside the file");
    }

    /// <summary>
    /// A forward-only reader of one section's decompressed blobs.
    /// </summary>
    public SectionReader OpenSection(int index) => new(this.FilePath, this.Manifest.Sections[index]);

    public static string Describe(PatchManifest m) => $"{m.Platform} patch: {m.Outputs.Count} outputs, {m.Sources.Count} stock files, "
                                                    + $"sources {string.Join(", ", m.Variants.Select(v => v.Id))}";
}

public sealed class SectionReader : IDisposable
{
    private readonly FileStream file;
    private readonly DecompressionStream zstd;
    private readonly Section section;
    private byte[]? skipBuffer;

    internal SectionReader(string path, Section section)
    {
        this.section       = section;
        this.file          = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        this.file.Position = section.Offset;
        this.zstd             = new DecompressionStream(new BoundedStream(this.file, section.Length), 1 << 20, checkEndOfStream: true, leaveOpen: false);
        this.zstd.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, PatchFile.MAX_WINDOW_LOG);
    }

    /// <summary>
    /// Decompressed bytes consumed so far.
    /// </summary>
    private long Position { get; set; }

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

    public void Dispose()
    {
        this.zstd.Dispose();
        this.file.Dispose();
    }
}

/// <summary>
/// Writes a patch file: sections one at a time, then the manifest that indexes them.
/// </summary>
public sealed class PatchFileWriter : IDisposable
{
    private readonly FileStream file;
    private readonly int level, windowLog, workers;
    private CountingStream? open;
    private string openName = string.Empty;
    private long openOffset;

    public PatchFileWriter(string path, int level, int windowLog, int workers)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(windowLog, PatchFile.MAX_WINDOW_LOG);
        (this.level, this.windowLog, this.workers) = (level, windowLog, workers);
        this.file                                     = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        this.file.Write(new byte[PatchFile.HEADER_SIZE]);
    }

    /// <summary>
    /// Starts a section; write its blobs to the returned stream, then call <see cref="EndSection"/>.
    /// </summary>
    public Stream BeginSection(string name)
    {
        if (this.open is not null) throw new InvalidOperationException("previous section not ended");

        CompressionStream zstd = new(this.file, this.level, 1 << 20, leaveOpen: true);
        zstd.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
        zstd.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, this.windowLog);
        if (this.workers > 1) zstd.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, this.workers);
        (this.openName, this.openOffset) = (name, this.file.Position);
        return this.open = new CountingStream(zstd);
    }

    public Section EndSection()
    {
        CountingStream stream = this.open ?? throw new InvalidOperationException("no open section");
        stream.Dispose();
        this.open = null;
        return new Section { Name = this.openName, Offset = this.openOffset, Length = this.file.Position - this.openOffset, Size = stream.Length };
    }

    public void Finish(PatchManifest manifest)
    {
        if (this.open is not null) throw new InvalidOperationException("section not ended");
        long offset = this.file.Position;
        using (Compressor compressor = new(19))
        {
            this.file.Write(compressor.Wrap(manifest.ToJson()));
        }

        Span<byte> header = stackalloc byte[PatchFile.HEADER_SIZE];
        PatchFile.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)offset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], (ulong)(this.file.Position - offset));
        this.file.Position = 0;
        this.file.Write(header);
        this.file.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        this.open?.Dispose();
        this.file.Dispose();
    }
}

/// <summary>
/// Read-only view of the next <c>length</c> bytes of a stream.
/// </summary>
internal sealed class BoundedStream(Stream inner, long length) : Stream
{
    private long left = length;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (this.left <= 0) return 0;

        int n = inner.Read(buffer[..(int)Math.Min(buffer.Length, this.left)]);
        this.left -= n;
        return n;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Write-only stream that counts what passes through it.
/// </summary>
internal sealed class CountingStream(Stream inner) : Stream
{
    private long length;

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        this.length += buffer.Length;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => this.length;

    public override long Position { get => this.length; set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
