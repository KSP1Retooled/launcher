using System.Buffers.Binary;

namespace KSP.Retooled.Patch;

/// <summary>
/// bsdiff (Colin Percival, BSD-2-Clause; see THIRD_PARTY_NOTICES.md) producing the raw stream layout
/// <see cref="BsPatch"/> reads. Memory is about 9× the reference length (two int arrays for the suffix sort), so callers
/// keep references to a bounded window. The suffix sort recurses, so run it on a thread with a large stack.
/// </summary>
public static class BsDiff
{
    public static byte[] Diff(ReadOnlySpan<byte> old, ReadOnlySpan<byte> @new) => Diff(old, SuffixSort(old), @new);

    /// <summary>Diffs against <paramref name="old"/> with its suffix array from <see cref="SuffixSort"/>, reusable across calls.</summary>
    public static byte[] Diff(ReadOnlySpan<byte> old, int[] suffixes, ReadOnlySpan<byte> @new)
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
                len = Search(suffixes, old, @new[scan..], out pos);
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

    // binary search of the suffix array for the longest match of `target`; memcmp over the shorter length, as bsdiff does
    static int Search(int[] suffixes, ReadOnlySpan<byte> old, ReadOnlySpan<byte> target, out int pos)
    {
        int start = 0, end = old.Length;
        while (end - start >= 2)
        {
            int middle = start + (end - start) / 2;
            int length = Math.Min(old.Length - suffixes[middle], target.Length);
            if (old.Slice(suffixes[middle], length).SequenceCompareTo(target[..length]) < 0) start = middle;
            else end = middle;
        }
        int x = old[suffixes[start]..].CommonPrefixLength(target);
        int y = old[suffixes[end]..].CommonPrefixLength(target);
        pos = x > y ? suffixes[start] : suffixes[end];
        return Math.Max(x, y);
    }

    /// <summary>Larsson–Sadakane qsufsort as used by bsdiff: the suffix array of <paramref name="old"/>, empty suffix first.</summary>
    public static int[] SuffixSort(ReadOnlySpan<byte> old)
    {
        int n = old.Length;
        var I = new int[n + 1];
        var V = new int[n + 1];
        Span<int> buckets = stackalloc int[256];
        buckets.Clear();
        foreach (byte b in old) buckets[b]++;
        for (int i = 1; i < 256; i++) buckets[i] += buckets[i - 1];
        for (int i = 255; i > 0; i--) buckets[i] = buckets[i - 1];
        buckets[0] = 0;
        for (int i = 0; i < n; i++) I[++buckets[old[i]]] = i;
        I[0] = n;
        for (int i = 0; i < n; i++) V[i] = buckets[old[i]];
        V[n] = 0;
        for (int i = 1; i < 256; i++)
            if (buckets[i] == buckets[i - 1] + 1) I[buckets[i]] = -1;
        I[0] = -1;

        for (int h = 1; I[0] != -(n + 1); h += h)
        {
            int len = 0, i = 0;
            while (i < n + 1)
            {
                if (I[i] < 0)
                {
                    len -= I[i];
                    i -= I[i];
                }
                else
                {
                    if (len != 0) I[i - len] = -len;
                    len = V[I[i]] + 1 - i;
                    Split(I, V, i, len, h);
                    i += len;
                    len = 0;
                }
            }
            if (len != 0) I[i - len] = -len;
        }

        for (int i = 0; i < n + 1; i++) I[V[i]] = i;
        return I;
    }

    static void Split(int[] I, int[] V, int start, int len, int h)
    {
        while (true)
        {
            if (len < 16)
            {
                for (int k = start, j; k < start + len; k += j)
                {
                    j = 1;
                    int x = V[I[k] + h];
                    for (int i = 1; k + i < start + len; i++)
                    {
                        if (V[I[k + i] + h] < x)
                        {
                            x = V[I[k + i] + h];
                            j = 0;
                        }
                        if (V[I[k + i] + h] == x)
                        {
                            (I[k + j], I[k + i]) = (I[k + i], I[k + j]);
                            j++;
                        }
                    }
                    for (int i = 0; i < j; i++) V[I[k + i]] = k + j - 1;
                    if (j == 1) I[k] = -1;
                }
                return;
            }

            int pivot = V[I[start + len / 2] + h];
            int jj = 0, kk = 0;
            for (int i = start; i < start + len; i++)
            {
                if (V[I[i] + h] < pivot) jj++;
                if (V[I[i] + h] == pivot) kk++;
            }
            jj += start;
            kk += jj;

            int a = start, e = 0, g = 0;
            while (a < jj)
            {
                int v = V[I[a] + h];
                if (v < pivot) a++;
                else if (v == pivot)
                {
                    (I[a], I[jj + e]) = (I[jj + e], I[a]);
                    e++;
                }
                else
                {
                    (I[a], I[kk + g]) = (I[kk + g], I[a]);
                    g++;
                }
            }
            while (jj + e < kk)
            {
                if (V[I[jj + e] + h] == pivot) e++;
                else
                {
                    (I[jj + e], I[kk + g]) = (I[kk + g], I[jj + e]);
                    g++;
                }
            }

            if (jj > start) Split(I, V, start, jj - start, h);
            for (int i = 0; i < kk - jj; i++) V[I[jj + i]] = kk - 1;
            if (jj == kk - 1) I[jj] = -1;

            if (start + len <= kk) return;
            len = start + len - kk;
            start = kk;
        }
    }
}
