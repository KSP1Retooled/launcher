using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace KSP.Retooled.Patch;

/// <summary>
/// Applies a raw bsdiff patch (Colin Percival's algorithm, BSD-2-Clause; see THIRD_PARTY_NOTICES.md). The layout is
/// <c>BSDIFF40</c> without its header and bzip2: four int64 LE (control triple count, diff length, extra length, new
/// size), the control triples (add length, extra length, old seek; int64 LE each), the diff bytes, the extra bytes.
/// Streams are kept separate so the archive's zstd stream sees the long zero runs of the diff stream together.
/// </summary>
public static class BsPatch
{
    public const int HeaderSize = 32;

    public static void Apply(ReadOnlySpan<byte> old, ReadOnlySpan<byte> patch, Stream output)
    {
        if (patch.Length < HeaderSize) throw new InvalidDataException("bsdiff patch shorter than its header");
        long controlCount = BinaryPrimitives.ReadInt64LittleEndian(patch);
        long diffLength = BinaryPrimitives.ReadInt64LittleEndian(patch[8..]);
        long extraLength = BinaryPrimitives.ReadInt64LittleEndian(patch[16..]);
        long newSize = BinaryPrimitives.ReadInt64LittleEndian(patch[24..]);
        if (controlCount < 0 || diffLength < 0 || extraLength < 0 || newSize < 0
            || HeaderSize + controlCount * 24 + diffLength + extraLength != patch.Length)
            throw new InvalidDataException("bsdiff patch header does not match its length");

        var control = patch.Slice(HeaderSize, (int)(controlCount * 24));
        var diff = patch.Slice(HeaderSize + control.Length, (int)diffLength);
        var extra = patch.Slice(HeaderSize + control.Length + (int)diffLength, (int)extraLength);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
        try
        {
            long oldPos = 0, newPos = 0;
            for (int c = 0; c < controlCount; c++)
            {
                long add = BinaryPrimitives.ReadInt64LittleEndian(control[(c * 24)..]);
                long copy = BinaryPrimitives.ReadInt64LittleEndian(control[(c * 24 + 8)..]);
                long seek = BinaryPrimitives.ReadInt64LittleEndian(control[(c * 24 + 16)..]);
                if (add < 0 || copy < 0 || newPos + add + copy > newSize || add > diff.Length || copy > extra.Length)
                    throw new InvalidDataException("corrupt bsdiff control stream");

                for (long done = 0; done < add;)
                {
                    int n = (int)Math.Min(buffer.Length, add - done);
                    AddOld(diff.Slice((int)done, n), old, oldPos + done, buffer.AsSpan(0, n));
                    output.Write(buffer, 0, n);
                    done += n;
                }
                diff = diff[(int)add..];
                oldPos += add;
                newPos += add;

                output.Write(extra[..(int)copy]);
                extra = extra[(int)copy..];
                newPos += copy;
                oldPos += seek;
            }
            if (newPos != newSize) throw new InvalidDataException("bsdiff patch produced the wrong length");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // destination[i] = diff[i] + old[oldPos + i], where old bytes outside the old file count as zero
    static void AddOld(ReadOnlySpan<byte> diff, ReadOnlySpan<byte> old, long oldPos, Span<byte> destination)
    {
        diff.CopyTo(destination);
        long start = Math.Max(oldPos, 0), end = Math.Min(oldPos + diff.Length, old.Length);
        if (start >= end) return;
        var target = destination[(int)(start - oldPos)..(int)(end - oldPos)];
        var source = old[(int)start..(int)end];
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i + Vector<byte>.Count <= target.Length; i += Vector<byte>.Count)
                (new Vector<byte>(target[i..]) + new Vector<byte>(source[i..])).CopyTo(target[i..]);
        }
        for (; i < target.Length; i++) target[i] += source[i];
    }
}
