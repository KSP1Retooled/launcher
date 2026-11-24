using KSP.Retooled.Patch.Patcher;
using KSP.Retooled.Patch.Utils;
using Xunit;
using ZstdSharp;

namespace KSP.Retooled.Patch.Tests;

public sealed class BsDiffTests
{
    // returns the compressed patch size: raw patches are about as long as the new data
    private static int RoundTrip(byte[] old, byte[] @new)
    {
        byte[] patch = BsDiff.Diff(old, @new);
        using MemoryStream output = new();
        BsPatch.Apply(old, patch, output);
        Assert.Equal(@new, output.ToArray());
        using Compressor compressor = new();
        return compressor.Wrap(patch).Length;
    }

    private static byte[] Random(int seed, int length)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void EditedDataProducesSmallPatch()
    {
        byte[] old = Random(1, 200_000);
        List<byte> edited = [..old];
        Random rng = new(2);
        for (int i = 0; i < 50; i++) edited[rng.Next(edited.Count)] ^= 0x5A;
        edited.InsertRange(1000, Random(3, 300));
        edited.RemoveRange(150_000, 2000);
        int compressed = RoundTrip(old, [..edited]);
        Assert.True(compressed < 5_000, $"patch {compressed} bytes compressed");
    }

    [Theory, InlineData(0, 0), InlineData(0, 1000), InlineData(1000, 0), InlineData(5000, 5000), InlineData(1, 70_000)]
    public void UnrelatedAndEmptyInputs(int oldLength, int newLength) => RoundTrip(Random(oldLength, oldLength), Random(newLength + 7, newLength));

    [Fact]
    public void RepetitiveData()
    {
        byte[] old = new byte[100_000];
        byte[] @new = new byte[120_000];
        for (int i = 0; i < @new.Length; i++) @new[i] = (byte)(i % 7 is 0 ? 1 : 0);
        RoundTrip(old, @new);
    }
}
