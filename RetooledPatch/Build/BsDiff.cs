using System.Buffers.Binary;

namespace KSP.Retooled.Patch;

/// <summary>
/// bsdiff (Colin Percival, BSD-2-Clause; see THIRD_PARTY_NOTICES.md) producing the raw stream layout
/// <see cref="BsPatch"/> reads. Memory is about 7× the reference length while sorting and 5× after it, so callers keep
/// references to a bounded window.
/// </summary>
public static class BsDiff
{
    /// <summary>How many entries <see cref="PrefixIndex(ReadOnlySpan{byte}, int[])"/> fills: one per three-byte prefix, and an end.</summary>
    internal const int PrefixIndexLength = (1 << 24) + 1;

    public static byte[] Diff(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new) =>
        Diff(old, SuffixSort(old), PrefixIndex(old), @new);

    /// <summary>Diffs against <paramref name="old"/> with its suffix array from <see cref="SuffixSort"/>, reusable across calls.</summary>
    public static byte[] Diff(ReadOnlySpan<byte> old, int[] suffixes, ReadOnlySpan<byte> @new) =>
        Diff(old, suffixes, PrefixIndex(old), @new);

    /// <summary>The same, with <paramref name="prefixes"/> from <see cref="PrefixIndex"/>, also reusable across calls.</summary>
    internal static byte[] Diff(ReadOnlySpan<byte> old, int[] suffixes, int[] prefixes, ReadOnlySpan<byte> @new)
    {
        var control = new List<(long Add, long Copy, long Seek)>();
        byte[] diff = new byte[@new.Length], extra = new byte[@new.Length];
        int diffLength = 0, extraLength = 0;
        int oldSize = old.Length, newSize = @new.Length;

        int scan = 0, len = 0, pos = 0, lastScan = 0, lastPos = 0, lastOffset = 0;
        while (scan < newSize)
        {
            int oldScore = 0;
            int scsc;
            for (scsc = scan += len; scan < newSize; scan++)
            {
                len = Search(suffixes, prefixes, old, @new[scan..], out pos);
                for (; scsc < scan + len; scsc++)
                    if (scsc + lastOffset >= 0 && scsc + lastOffset < oldSize && old[scsc + lastOffset] == @new[scsc]) oldScore++;
                if ((len == oldScore && len != 0) || len > oldScore + 8) break;
                if (scan + lastOffset >= 0 && scan + lastOffset < oldSize && old[scan + lastOffset] == @new[scan]) oldScore--;
            }

            if (len == oldScore && scan != newSize) continue;

            // extend the previous match forwards and the new match backwards, then split any overlap
            int s = 0, bestForward = 0, lenForward = 0;
            for (int i = 0; lastScan + i < scan && lastPos + i < oldSize;)
            {
                if (old[lastPos + i] == @new[lastScan + i]) s++;
                i++;
                if (s * 2 - i > bestForward * 2 - lenForward) { bestForward = s; lenForward = i; }
            }

            int lenBack = 0;
            if (scan < newSize)
            {
                s = 0;
                int bestBack = 0;
                for (int i = 1; scan >= lastScan + i && pos >= i; i++)
                {
                    if (old[pos - i] == @new[scan - i]) s++;
                    if (s * 2 - i > bestBack * 2 - lenBack) { bestBack = s; lenBack = i; }
                }
            }

            if (lastScan + lenForward > scan - lenBack)
            {
                int overlap = lastScan + lenForward - (scan - lenBack);
                s = 0;
                int bestSplit = 0, lenSplit = 0;
                for (int i = 0; i < overlap; i++)
                {
                    if (@new[lastScan + lenForward - overlap + i] == old[lastPos + lenForward - overlap + i]) s++;
                    if (@new[scan - lenBack + i] == old[pos - lenBack + i]) s--;
                    if (s > bestSplit) { bestSplit = s; lenSplit = i + 1; }
                }
                lenForward += lenSplit - overlap;
                lenBack -= lenSplit;
            }

            for (int i = 0; i < lenForward; i++)
                diff[diffLength + i] = (byte)(@new[lastScan + i] - old[lastPos + i]);
            int extraRun = scan - lenBack - (lastScan + lenForward);
            @new.Slice(lastScan + lenForward, extraRun).CopyTo(extra.AsSpan(extraLength));
            diffLength += lenForward;
            extraLength += extraRun;
            control.Add((lenForward, extraRun, pos - lenBack - (lastPos + lenForward)));

            lastScan = scan - lenBack;
            lastPos = pos - lenBack;
            lastOffset = pos - scan;
        }

        byte[] patch = new byte[BsPatch.HeaderSize + control.Count * 24 + diffLength + extraLength];
        var span = patch.AsSpan();
        BinaryPrimitives.WriteInt64LittleEndian(span, control.Count);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], diffLength);
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], extraLength);
        BinaryPrimitives.WriteInt64LittleEndian(span[24..], newSize);
        int at = BsPatch.HeaderSize;
        foreach (var (add, copy, seek) in control)
        {
            BinaryPrimitives.WriteInt64LittleEndian(span[at..], add);
            BinaryPrimitives.WriteInt64LittleEndian(span[(at + 8)..], copy);
            BinaryPrimitives.WriteInt64LittleEndian(span[(at + 16)..], seek);
            at += 24;
        }
        diff.AsSpan(0, diffLength).CopyTo(span[at..]);
        extra.AsSpan(0, extraLength).CopyTo(span[(at + diffLength)..]);
        return patch;
    }

    // binary search of the suffix array for the longest match of `target`; memcmp over the shorter length, as bsdiff does.
    // This runs for nearly every byte of the new data, and each step can miss the cache twice, in the suffix array and in
    // the reference. So the index skips the steps it can, and the rest are decided by a key compared here: most of them
    // differ within eight bytes, which don't justify a call into a vectorized compare.
    static int Search(int[] suffixes, int[] prefixes, ReadOnlySpan<byte> old, ReadOnlySpan<byte> target, out int pos)
    {
        int start = 0, end = old.Length;
        if (target.Length >= 3)
        {
            int prefix = target[0] << 16 | target[1] << 8 | target[2];
            start = Math.Max(prefixes[prefix] - 1, 0);
            end = Math.Min(prefixes[prefix + 1], end);
        }

        bool hasKey = target.Length >= sizeof(ulong);
        ulong targetKey = hasKey ? BinaryPrimitives.ReadUInt64BigEndian(target) : 0;
        while (end - start >= 2)
        {
            int middle = start + (end - start) / 2;
            int suffix = suffixes[middle];
            bool less;
            ulong key = hasKey && old.Length - suffix >= sizeof(ulong) ? BinaryPrimitives.ReadUInt64BigEndian(old[suffix..]) : targetKey;
            if (key != targetKey)
            {
                less = key < targetKey;
            }
            else
            {
                int length = Math.Min(old.Length - suffix, target.Length);
                less = old.Slice(suffix, length).SequenceCompareTo(target[..length]) < 0;
            }

            if (less) start = middle;
            else end = middle;
        }
        int x = old[suffixes[start]..].CommonPrefixLength(target);
        int y = old[suffixes[end]..].CommonPrefixLength(target);
        pos = x > y ? suffixes[start] : suffixes[end];
        return Math.Max(x, y);
    }

    /// <summary>The suffix array of <paramref name="old"/>, empty suffix first.</summary>
    public static int[] SuffixSort(ReadOnlySpan<byte> old)
    {
        var suffixes = new int[old.Length + 1];
        SuffixSorter.Sort(old, suffixes);
        return suffixes;
    }

    internal static int[] PrefixIndex(ReadOnlySpan<byte> old)
    {
        var index = new int[PrefixIndexLength];
        PrefixIndex(old, index);
        return index;
    }

    /// <summary>
    /// Fills <paramref name="index"/>, of <see cref="PrefixIndexLength"/> entries, with where each three-byte prefix
    /// starts in the suffix array of <paramref name="old"/>, for <see cref="Search"/>. A shorter suffix counts as padded
    /// with zeros, which keeps it next to the suffixes it sorts among.
    /// </summary>
    internal static void PrefixIndex(ReadOnlySpan<byte> old, int[] index)
    {
        Array.Clear(index);
        for (int i = 0; i + 2 < old.Length; i++)
            index[old[i] << 16 | old[i + 1] << 8 | old[i + 2]]++;
        if (old.Length >= 2)
            index[old[^2] << 16 | old[^1] << 8]++;
        if (old.Length >= 1)
            index[old[^1] << 16]++;

        int sum = 1; // the empty suffix comes first
        for (int i = 0; i < PrefixIndexLength; i++)
            (index[i], sum) = (sum, sum + index[i]);
    }
}
