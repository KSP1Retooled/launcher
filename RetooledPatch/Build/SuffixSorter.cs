using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace KSP.Retooled.Patch;

/// <summary>
/// Suffix array construction by induced sorting (SA-IS: Nong, Zhang and Chan, "Two Efficient Algorithms for Linear
/// Time Suffix Array Construction", 2009). Linear in the text's length whatever it holds, which matters here: game
/// assets are full of repeats, the input the usual comparison sorts are slowest on.
/// <para>
/// Most of the time goes to cache misses, because induction reads the text at positions that jump all over it. So a
/// suffix's type lives in the top bit of its symbol, where reading the symbol also fetches the type, and the induction
/// loops prefetch the symbols they are about to read.
/// </para>
/// </summary>
internal static unsafe class SuffixSorter
{
    /// <summary>How far ahead of the scan to prefetch: enough for main memory to answer before the scan arrives.</summary>
    const int PrefetchDistance = 32;

    /// <summary>
    /// Writes the suffix array of <paramref name="text"/> to the first <c>text.Length + 1</c> entries of
    /// <paramref name="suffixes"/>: the empty suffix first, the others in unsigned byte order.
    /// </summary>
    public static void Sort(ReadOnlySpan<byte> text, Span<int> suffixes)
    {
        ushort[] symbols = ArrayPool<ushort>.Shared.Rent(text.Length + 1);
        try
        {
            Sort(text, suffixes, symbols);
        }
        finally
        {
            ArrayPool<ushort>.Shared.Return(symbols);
        }
    }

    /// <param name="text">The text to sort.</param>
    /// <param name="suffixes">Receives the suffix array.</param>
    /// <param name="symbols">Scratch space for <c>text.Length + 1</c> entries.</param>
    public static void Sort(ReadOnlySpan<byte> text, Span<int> suffixes, Span<ushort> symbols)
    {
        int n = text.Length;
        ArgumentOutOfRangeException.ThrowIfLessThan(suffixes.Length, n + 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(symbols.Length, n + 1);
        if (n == 0)
        {
            suffixes[0] = 0;
            return;
        }

        // SA-IS needs a unique smallest symbol at the end, which a byte has no room for.
        for (int i = 0; i < n; i++)
            symbols[i] = (ushort)(text[i] + 1);
        symbols[n] = 0;

        fixed (ushort* s = symbols)
        fixed (int* sa = suffixes)
            Induce(s, sa, n + 1, byte.MaxValue + 1);
    }

    // T is ushort for the text and int when recursing. The top bit of a symbol flags an S-type suffix: one smaller than
    // the suffix after it. These run for every step of the sort, so they test sizeof(T), which the JIT folds in any
    // tier, rather than use generic math, which it doesn't.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Raw<T>(T* s, int i)
        where T : unmanaged => sizeof(T) == sizeof(ushort) ? ((ushort*)s)[i] : ((int*)s)[i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Symbol<T>(T* s, int i)
        where T : unmanaged => sizeof(T) == sizeof(ushort) ? ((ushort*)s)[i] & 0x7FFF : ((int*)s)[i] & int.MaxValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsS<T>(T* s, int i)
        where T : unmanaged => sizeof(T) == sizeof(ushort) ? (((ushort*)s)[i] & 0x8000) != 0 : ((int*)s)[i] < 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void MarkS<T>(T* s, int i)
        where T : unmanaged
    {
        if (sizeof(T) == sizeof(ushort))
            ((ushort*)s)[i] |= 0x8000;
        else
            ((int*)s)[i] |= int.MinValue;
    }

    /// <summary>A leftmost S-type suffix: an S-type suffix right after an L-type one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsLms<T>(T* s, int i)
        where T : unmanaged => i > 0 && IsS(s, i) && !IsS(s, i - 1);

    /// <summary>Asks for the symbol before the suffix <paramref name="sa"/> holds at <paramref name="index"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Prefetch<T>(T* s, int* sa, int index, int n)
        where T : unmanaged
    {
        // A prefetch never faults, so a slot that is still empty only wastes the hint.
        if (Sse.IsSupported && index >= 0 && index < n)
            Sse.Prefetch0(s + sa[index] - 1);
    }

    /// <param name="s">The text, ending in a unique smallest symbol of 0. Its symbols get their type flags here.</param>
    /// <param name="sa">Receives the suffix array; also the working space of the recursion.</param>
    /// <param name="n">The text's length, including that last symbol.</param>
    /// <param name="maxSymbol">The largest symbol in the text.</param>
    static void Induce<T>(T* s, int* sa, int n, int maxSymbol)
        where T : unmanaged
    {
        int[] countArray = ArrayPool<int>.Shared.Rent(maxSymbol + 1);
        int[] bucketArray = ArrayPool<int>.Shared.Rent(maxSymbol + 1);
        try
        {
            fixed (int* counts = countArray, buckets = bucketArray)
            {
                new Span<int>(counts, maxSymbol + 1).Clear();
                for (int i = 0; i < n; i++)
                    counts[Symbol(s, i)]++;

                ClassifySuffixes(s, n);
                new Span<int>(sa, n).Fill(-1);

                // Sort the LMS substrings by inducing from them in text order.
                BucketEnds(counts, buckets, maxSymbol);
                for (int i = 1; i < n; i++)
                    if (IsLms(s, i))
                        sa[--buckets[Symbol(s, i)]] = i;
                InduceFromLms(s, sa, counts, buckets, n, maxSymbol);

                int lmsCount = NameLmsSubstrings(s, sa, n, out int names);

                // The reduced text sits at the end of sa, and its suffix array goes at the start.
                int* reduced = sa + n - lmsCount;
                if (names < lmsCount)
                    Induce(reduced, sa, lmsCount, names - 1);
                else
                    for (int i = 0; i < lmsCount; i++)
                        sa[reduced[i]] = i;

                // Place the now sorted LMS suffixes and induce everything else from them.
                for (int i = 1, j = 0; i < n; i++)
                    if (IsLms(s, i))
                        reduced[j++] = i;
                for (int i = 0; i < lmsCount; i++)
                    sa[i] = reduced[sa[i]];
                new Span<int>(sa + lmsCount, n - lmsCount).Fill(-1);

                BucketEnds(counts, buckets, maxSymbol);
                for (int i = lmsCount - 1; i >= 0; i--)
                {
                    int suffix = sa[i];
                    sa[i] = -1;
                    sa[--buckets[Symbol(s, suffix)]] = suffix;
                }
                InduceFromLms(s, sa, counts, buckets, n, maxSymbol);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(bucketArray);
            ArrayPool<int>.Shared.Return(countArray);
        }
    }

    /// <summary>Flags every S-type suffix. The last suffix is the smallest, so it is S-type.</summary>
    static void ClassifySuffixes<T>(T* s, int n)
        where T : unmanaged
    {
        MarkS(s, n - 1);
        bool nextIsS = true;
        int next = 0;
        for (int i = n - 2; i >= 0; i--)
        {
            int current = Symbol(s, i);
            nextIsS = current < next || (current == next && nextIsS);
            if (nextIsS)
                MarkS(s, i);
            next = current;
        }
    }

    /// <summary>Sets each symbol's bucket pointer one past its bucket's last entry.</summary>
    static void BucketEnds(int* counts, int* buckets, int maxSymbol)
    {
        int sum = 0;
        for (int i = 0; i <= maxSymbol; i++)
        {
            sum += counts[i];
            buckets[i] = sum;
        }
    }

    /// <summary>Induces the L-type suffixes left to right, then the S-type ones right to left.</summary>
    static void InduceFromLms<T>(T* s, int* sa, int* counts, int* buckets, int n, int maxSymbol)
        where T : unmanaged
    {
        int sum = 0;
        for (int i = 0; i <= maxSymbol; i++)
        {
            buckets[i] = sum;
            sum += counts[i];
        }

        for (int i = 0; i < n; i++)
        {
            Prefetch(s, sa, i + PrefetchDistance, n);
            int previous = sa[i] - 1;
            if (previous >= 0 && !IsS(s, previous))
                sa[buckets[Symbol(s, previous)]++] = previous;
        }

        BucketEnds(counts, buckets, maxSymbol);
        for (int i = n - 1; i >= 0; i--)
        {
            Prefetch(s, sa, i - PrefetchDistance, n);
            int previous = sa[i] - 1;
            if (previous >= 0 && IsS(s, previous))
                sa[--buckets[Symbol(s, previous)]] = previous;
        }
    }

    /// <summary>
    /// With the LMS substrings sorted in <paramref name="sa"/>, gives equal substrings equal names and writes the names
    /// in text order to the end of <paramref name="sa"/>: the reduced text. Returns its length.
    /// </summary>
    static int NameLmsSubstrings<T>(T* s, int* sa, int n, out int names)
        where T : unmanaged
    {
        int lmsCount = 0;
        for (int i = 0; i < n; i++)
            if (IsLms(s, sa[i]))
                sa[lmsCount++] = sa[i];
        new Span<int>(sa + lmsCount, n - lmsCount).Fill(-1);

        names = 0;
        int previous = -1;
        for (int i = 0; i < lmsCount; i++)
        {
            int position = sa[i];
            if (previous < 0 || !SameLmsSubstring(s, position, previous))
            {
                names++;
                previous = position;
            }

            // LMS positions are at least two apart, so halves are distinct and fit behind the sorted list.
            sa[lmsCount + position / 2] = names - 1;
        }

        for (int i = n - 1, j = n - 1; i >= lmsCount; i--)
            if (sa[i] >= 0)
                sa[j--] = sa[i];
        return lmsCount;
    }

    /// <summary>
    /// Compares symbols with their type flags, as equal substrings need both to match. The text's last symbol is unique,
    /// so a comparison stops before it can run off the end.
    /// </summary>
    static bool SameLmsSubstring<T>(T* s, int a, int b)
        where T : unmanaged
    {
        for (int d = 0; ; d++)
        {
            if (Raw(s, a + d) != Raw(s, b + d))
                return false;
            if (d > 0 && (IsLms(s, a + d) || IsLms(s, b + d)))
                return true;
        }
    }
}
