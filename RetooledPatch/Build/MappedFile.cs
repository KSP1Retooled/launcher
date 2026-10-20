using System.IO.MemoryMappedFiles;

namespace KSP.Retooled.Patch;

/// <summary>
/// A read-only view of a whole file (under 2 GB) backed by the OS page cache instead of the managed heap, so scanning
/// gigabytes of stock and build files costs address space rather than memory.
/// </summary>
sealed unsafe class MappedFile : IDisposable
{
    readonly MemoryMappedFile map;
    readonly MemoryMappedViewAccessor view;
    readonly byte* pointer;
    public readonly int Length;

    public MappedFile(string path, long length)
    {
        if (length is <= 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(length), $"{path}: cannot map {length} bytes");
        Length = (int)length;
        map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        view = map.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        pointer = p + view.PointerOffset;
    }

    public ReadOnlySpan<byte> Span => new(pointer, Length);

    public void Dispose()
    {
        view.SafeMemoryMappedViewHandle.ReleasePointer();
        view.Dispose();
        map.Dispose();
    }
}
