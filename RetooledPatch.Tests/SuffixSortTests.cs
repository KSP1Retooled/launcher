using KSP.Retooled.Patch.Utils;
using Xunit;

namespace KSP.Retooled.Patch.Tests;

public sealed class SuffixSortTests
{
    public static TheoryData<byte[]> Texts =>
    [
        [],
        [0],
        [255],
        [.."\0\0\0\0\0\0"u8],
        [255, 255, 255, 255],
        [1, 2, 1, 2, 1, 2, 1, 2],
        [2, 1, 2, 1, 2, 1, 2, 1, 2],
        [255, 0, 255, 0, 1, 255],
        [.."mississippi"u8],
        [..Enumerable.Range(0, 256).Select(i => (byte)i)],
        [..Enumerable.Range(0, 256).Select(i => (byte)(255 - i))],
        [..Enumerable.Repeat<byte>(7, 5000)]
    ];

    private static int[] BruteForce(byte[] text)
    {
        int[] suffixes = [..Enumerable.Range(0, text.Length + 1)];
        Array.Sort(suffixes, (a, b) => text.AsSpan(a).SequenceCompareTo(text.AsSpan(b)));
        return suffixes;
    }

    private static byte[] Repeats(int seed, int length, int alphabet)
    {
        Random rng = new(seed);
        List<byte> bytes = new(length);
        while (bytes.Count < length)
        {
            if (bytes.Count > 8 && rng.Next(3) is 0)
            {
                int run = rng.Next(1, 40), from = rng.Next(bytes.Count);
                for (int i = 0; i < run; i++) bytes.Add(bytes[from + i]);
            }
            else
            {
                bytes.Add((byte)rng.Next(alphabet));
            }
        }

        return [..bytes.Take(length)];
    }

    [Theory, MemberData(nameof(Texts))]
    public void SortsSmallAndDegenerateTexts(byte[] text) => Assert.Equal(BruteForce(text), BsDiff.SuffixSort(text));

    [Theory, InlineData(1, 2), InlineData(2, 3), InlineData(3, 4), InlineData(4, 16), InlineData(5, 256)]
    public void SortsRepetitiveTexts(int seed, int alphabet)
    {
        for (int length = 1; length < 400; length++)
        {
            byte[] text = Repeats((seed * 1000) + length, length, alphabet);
            Assert.Equal(BruteForce(text), BsDiff.SuffixSort(text));
        }

        byte[] large = Repeats(seed, 200_000, alphabet);
        int[] suffixes = BsDiff.SuffixSort(large);
        Assert.Equal(large.Length, suffixes[0]);
        for (int i = 1; i < suffixes.Length; i++)
        {
            Assert.True(large.AsSpan(suffixes[i - 1]).SequenceCompareTo(large.AsSpan(suffixes[i])) < 0);
        }

        Assert.Equal(Enumerable.Range(0, suffixes.Length), suffixes.Order());
    }

    [Fact]
    public void WritesIntoALargerBuffer()
    {
        byte[] text = [.."banana"u8];
        int[] buffer = new int[32];
        SuffixSorter.Sort(text, buffer);
        Assert.Equal(BruteForce(text), buffer[..(text.Length + 1)]);
    }

    /// <summary>
    /// The first three bytes of a suffix, padded with zeros as the prefix index counts them.
    /// </summary>
    private static byte[] Padded(byte[] text, int suffix)
    {
        byte[] key = new byte[3];
        text.AsSpan(suffix, Math.Min(3, text.Length - suffix)).CopyTo(key);
        return key;
    }

    [Theory, InlineData(1, 2), InlineData(2, 4), InlineData(3, 256)]
    // ReSharper disable once CognitiveComplexity
    public void TheIndexPointsAtEachPrefixInTheSuffixArray(int seed, int alphabet)
    {
        byte[] old = Repeats(seed, 5_000, alphabet);
        int[] suffixes = BsDiff.SuffixSort(old), index = BsDiff.PrefixIndex(old);

        for (int a = 0; a < Math.Min(alphabet, 8); a++)
        {
            for (int b = 0; b < Math.Min(alphabet, 8); b++)
            {
                for (int c = 0; c < Math.Min(alphabet, 8); c++)
                {
                    int prefix = (a << 16) | (b << 8) | c;
                    byte[] target = [(byte)a, (byte)b, (byte)c];
                    int lower = Math.Max(index[prefix] - 1, 0), upper = Math.Min(index[prefix + 1], old.Length);

                    // Every suffix starting with the prefix lies strictly between the two bounds.
                    if (lower > 0) Assert.True(old.AsSpan(suffixes[lower]).SequenceCompareTo(target) < 0);
                    if (upper < old.Length) Assert.True(Padded(old, suffixes[upper]).AsSpan().SequenceCompareTo(target) > 0);
                }
            }
        }
    }

    [Theory, InlineData(1, 2), InlineData(2, 256)]
    public void DiffingAgainstShortReferencesRoundTrips(int seed, int alphabet)
    {
        for (int length = 0; length < 40; length++)
        {
            byte[] old = Repeats(seed + length, length, alphabet), @new = Repeats((seed * 7) + length, 300, alphabet);
            using MemoryStream output = new();
            BsPatch.Apply(old, BsDiff.Diff(old, @new), output);
            Assert.Equal(@new, output.ToArray());
        }
    }
}
