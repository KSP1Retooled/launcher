namespace KSP.Retooled.Patch.IO;

/// <summary>
/// Read-only view of the next <c>length</c> bytes of a stream.
/// </summary>
internal sealed class BoundedStream(Stream inner, long length) : Stream
{
    private long left = length;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (this.left <= 0) return 0;

        int n = inner.Read(buffer[..(int)Math.Min(buffer.Length, this.left)]);
        this.left -= n;
        return n;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
