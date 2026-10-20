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
    public const string Extension = ".rtpatch";
    internal static readonly byte[] Magic = "RTPATCH2"u8.ToArray();
    internal const int HeaderSize = 24;

    /// <summary>Largest window a reader accepts (2 GB); the writer's window must not exceed it.</summary>
    internal const int MaxWindowLog = 31;

    public string FilePath { get; }
    public PatchManifest Manifest { get; }

    PatchFile(string path, PatchManifest manifest)
    {
        FilePath = path;
        Manifest = manifest;
    }

    public static PatchFile Open(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[HeaderSize];
        if (file.Length < HeaderSize || file.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) < HeaderSize
            || !header[..8].SequenceEqual(Magic))
            throw new InvalidDataException($"{path} is not a retooled patch file (or was made by an older patch tool)");
        long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        long length = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        if (offset < HeaderSize || length <= 0 || offset + length > file.Length)
            throw new InvalidDataException($"{path}: truncated patch file");

        byte[] compressed = new byte[length];
        file.Position = offset;
        file.ReadExactly(compressed);
        using var decompressor = new Decompressor();
        var manifest = PatchManifest.FromJson(decompressor.Unwrap(compressed));
        if (manifest.Version != PatchManifest.CurrentVersion)
            throw new InvalidDataException($"{path}: manifest version {manifest.Version}, this tool reads {PatchManifest.CurrentVersion}");
        foreach (var section in manifest.Sections)
            if (section.Offset < HeaderSize || section.Offset + section.Length > offset)
                throw new InvalidDataException($"{path}: section {section.Name} lies outside the file");
        return new PatchFile(Path.GetFullPath(path), manifest);
    }

    /// <summary>A forward-only reader of one section's decompressed blobs.</summary>
    public SectionReader OpenSection(int index) => new(FilePath, Manifest.Sections[index]);

    public static string Describe(PatchManifest m) =>
        $"{m.Platform} patch: {m.Outputs.Count} outputs, {m.Sources.Count} stock files, "
        + $"sources {string.Join(", ", m.Variants.Select(v => v.Id))}";
}

public sealed class SectionReader : IDisposable
{
    readonly FileStream file;
    readonly DecompressionStream zstd;
    readonly Section section;
    byte[]? skipBuffer;

    internal SectionReader(string path, Section section)
    {
        this.section = section;
        file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        file.Position = section.Offset;
        zstd = new DecompressionStream(new BoundedStream(file, section.Length), 1 << 20, checkEndOfStream: true, leaveOpen: false);
        zstd.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, PatchFile.MaxWindowLog);
    }

    /// <summary>Decompressed bytes consumed so far.</summary>
    public long Position { get; private set; }

    public void SkipTo(long offset)
    {
        if (offset < Position || offset > section.Size)
            throw new InvalidDataException($"section {section.Name}: blob at {offset} is behind the reader ({Position}) or past the end");
        skipBuffer ??= new byte[1 << 20];
        while (Position < offset) Read(skipBuffer.AsSpan(0, (int)Math.Min(skipBuffer.Length, offset - Position)));
    }

    public void Read(Span<byte> buffer)
    {
        zstd.ReadExactly(buffer);
        Position += buffer.Length;
    }

    public void Dispose()
    {
        zstd.Dispose();
        file.Dispose();
    }
}

/// <summary>Writes a patch file: sections one at a time, then the manifest that indexes them.</summary>
public sealed class PatchFileWriter : IDisposable
{
    readonly FileStream file;
    readonly int level, windowLog, workers;
    CountingStream? open;
    string openName = "";
    long openOffset;

    public PatchFileWriter(string path, int level, int windowLog, int workers)
    {
        if (windowLog > PatchFile.MaxWindowLog) throw new ArgumentOutOfRangeException(nameof(windowLog));
        (this.level, this.windowLog, this.workers) = (level, windowLog, workers);
        file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        file.Write(new byte[PatchFile.HeaderSize]);
    }

    /// <summary>Starts a section; write its blobs to the returned stream, then call <see cref="EndSection"/>.</summary>
    public Stream BeginSection(string name)
    {
        if (open != null) throw new InvalidOperationException("previous section not ended");
        var zstd = new CompressionStream(file, level, 1 << 20, leaveOpen: true);
        zstd.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
        zstd.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
        if (workers > 1) zstd.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, workers);
        (openName, openOffset) = (name, file.Position);
        return open = new CountingStream(zstd);
    }

    public Section EndSection()
    {
        var stream = open ?? throw new InvalidOperationException("no open section");
        stream.Dispose();
        open = null;
        return new Section { Name = openName, Offset = openOffset, Length = file.Position - openOffset, Size = stream.Length };
    }

    public void Finish(PatchManifest manifest)
    {
        if (open != null) throw new InvalidOperationException("section not ended");
        long offset = file.Position;
        using (var compressor = new Compressor(19))
            file.Write(compressor.Wrap(manifest.ToJson()));
        Span<byte> header = stackalloc byte[PatchFile.HeaderSize];
        PatchFile.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)offset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], (ulong)(file.Position - offset));
        file.Position = 0;
        file.Write(header);
        file.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        open?.Dispose();
        file.Dispose();
    }
}

/// <summary>Read-only view of the next <c>length</c> bytes of a stream.</summary>
sealed class BoundedStream(Stream inner, long length) : Stream
{
    long left = length;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (left <= 0) return 0;
        int n = inner.Read(buffer[..(int)Math.Min(buffer.Length, left)]);
        left -= n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Write-only stream that counts what passes through it.</summary>
sealed class CountingStream(Stream inner) : Stream
{
    long length;

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        length += buffer.Length;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => length;
    public override long Position { get => length; set => throw new NotSupportedException(); }
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
