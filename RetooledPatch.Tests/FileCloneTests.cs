using KSP.Retooled.Patch.Utils;
using Xunit;

namespace KSP.Retooled.Patch.Tests;

public sealed class FileCloneTests : IDisposable
{
    // Create the test files on the same volume as the test assembly.
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "clone-" + Guid.NewGuid().ToString("N"));

    public FileCloneTests() => Directory.CreateDirectory(this.root);

    public void Dispose()
    {
        if (Directory.Exists(this.root)) Directory.Delete(this.root, true);
    }

    [Theory, InlineData(0), InlineData(1), InlineData(70_000), InlineData(3 << 20)]
    public void ACloneIsAFileOfItsOwnAndALinkSharesTheContent(int length)
    {
        byte[] bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        string source = Path.Combine(this.root, "source"), clone = Path.Combine(this.root, "clone");
        File.WriteAllBytes(source, bytes);
        File.SetLastWriteTimeUtc(source, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        FileClone.LinkOrCopy(source, clone);

        Assert.Equal(bytes, File.ReadAllBytes(clone));
        Assert.Equal(File.GetLastWriteTimeUtc(source), File.GetLastWriteTimeUtc(clone));
        using (FileStream stream = new(clone, FileMode.Open, FileAccess.Write))
        {
            stream.Write([1, 2, 3]);
        }

        byte[] after = File.ReadAllBytes(source);
        Assert.Equal(File.ReadAllBytes(clone), after);
    }

    [Fact]
    public void ACloneDoesNotReplaceAnExistingFile()
    {
        string source = Path.Combine(this.root, "source"), existing = Path.Combine(this.root, "existing");
        File.WriteAllBytes(source, [1]);
        File.WriteAllBytes(existing, [2]);
        Assert.ThrowsAny<IOException>(() => FileClone.LinkOrCopy(source, existing));
        Assert.Equal([2], File.ReadAllBytes(existing));
    }
}
