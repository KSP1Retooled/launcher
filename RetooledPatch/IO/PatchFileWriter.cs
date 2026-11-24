using System.Buffers.Binary;
using KSP.Retooled.Patch.Models.Manifest;
using KSP.Retooled.Patch.Patcher;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace KSP.Retooled.Patch.IO;

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
        this.file                                  = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        this.file.Write(new byte[PatchFile.HEADER_SIZE]);
    }

    public void Dispose()
    {
        this.open?.Dispose();
        this.file.Dispose();
    }

    /// <summary>
    /// Starts a section; write its blobs to the returned stream, then call <see cref="EndSection"/>.
    /// </summary>
    public Stream BeginSection(string name)
    {
        if (this.open is not null) throw new InvalidOperationException("previous section not ended");

        CompressionStream zstd = new(this.file, this.level, 1 << 20);
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
        this.file.Flush(true);
    }
}
