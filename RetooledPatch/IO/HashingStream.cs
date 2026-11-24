using System.Security.Cryptography;

namespace KSP.Retooled.Patch.IO;

/// <summary>
/// Write-only stream that hashes everything written through it.
/// </summary>
internal sealed class HashingStream(Stream inner) : Stream
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long length;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    // ReSharper disable once ConvertToAutoPropertyWhenPossible
    public override long Length => this.length;

    public override long Position
    {
        get => this.length;
        set => throw new NotSupportedException();
    }

    public byte[] Hash() => this.hash.GetHashAndReset();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        this.hash.AppendData(buffer);
        inner.Write(buffer);
        this.length += buffer.Length;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) this.hash.Dispose();
        base.Dispose(disposing);
    }
}
