using System.IO.MemoryMappedFiles;

namespace KSP.Retooled.Patch;

/// <summary>
/// A read-only view of a whole file (under 2 GB) backed by the OS page cache instead of the managed heap, so scanning
/// gigabytes of stock and build files costs address space rather than memory.
/// </summary>
internal sealed unsafe class MappedFile : IDisposable
{
    private readonly int length;
    private readonly MemoryMappedFile map;
    private readonly byte* pointer;
    private readonly MemoryMappedViewAccessor view;

    public MappedFile(string path, long length)
    {
        if (length is <= 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(length), $"{path}: cannot map {length} bytes");

        this.length = (int)length;
        this.map    = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        this.view   = this.map.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        byte* p = null;
        this.view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        this.pointer = p + this.view.PointerOffset;
    }

    public ReadOnlySpan<byte> Span => new(this.pointer, this.length);

    public void Dispose()
    {
        this.view.SafeMemoryMappedViewHandle.ReleasePointer();
        this.view.Dispose();
        this.map.Dispose();
    }
}
