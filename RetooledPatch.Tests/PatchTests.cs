using System.Text;
using KSP.Retooled.Patch.Build;
using KSP.Retooled.Patch.Cli;
using Xunit;
using ZstdSharp;

namespace KSP.Retooled.Patch.Tests;

public sealed class SuffixSortTests
{
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
        return [.. bytes.Take(length)];
    }

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

    [Theory]
    [MemberData(nameof(Texts))]
    public void SortsSmallAndDegenerateTexts(byte[] text) => Assert.Equal(BruteForce(text), BsDiff.SuffixSort(text));

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(4, 16)]
    [InlineData(5, 256)]
    public void SortsRepetitiveTexts(int seed, int alphabet)
    {
        for (int length = 1; length < 400; length++)
        {
            byte[] text = Repeats(seed * 1000 + length, length, alphabet);
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

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 256)]
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
                    int prefix = a << 16 | b << 8 | c;
                    byte[] target = [(byte)a, (byte)b, (byte)c];
                    int lower = Math.Max(index[prefix] - 1, 0), upper = Math.Min(index[prefix + 1], old.Length);

                    // Every suffix starting with the prefix lies strictly between the two bounds.
                    if (lower > 0) Assert.True(old.AsSpan(suffixes[lower]).SequenceCompareTo(target) < 0);
                    if (upper < old.Length) Assert.True(Padded(old, suffixes[upper]).AsSpan().SequenceCompareTo(target) > 0);
                }
            }
        }
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 256)]
    public void DiffingAgainstShortReferencesRoundTrips(int seed, int alphabet)
    {
        for (int length = 0; length < 40; length++)
        {
            byte[] old = Repeats(seed + length, length, alphabet), @new = Repeats(seed * 7 + length, 300, alphabet);
            using MemoryStream output = new();
            BsPatch.Apply(old, BsDiff.Diff(old, @new), output);
            Assert.Equal(@new, output.ToArray());
        }
    }
}

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

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1000)]
    [InlineData(1000, 0)]
    [InlineData(5000, 5000)]
    [InlineData(1, 70_000)]
    public void UnrelatedAndEmptyInputs(int oldLength, int newLength) =>
        RoundTrip(Random(oldLength, oldLength), Random(newLength + 7, newLength));

    [Fact]
    public void RepetitiveData()
    {
        byte[] old = new byte[100_000];
        byte[] @new = new byte[120_000];
        for (int i = 0; i < @new.Length; i++) @new[i] = (byte)(i % 7 is 0 ? 1 : 0);
        RoundTrip(old, @new);
    }
}

/// <summary>
/// Two store variants of one stock install (Steam and GOG: different player data, executable and store DLL) patched
/// into one build that ships Steam's DLL.
/// </summary>
public sealed class PatchRoundTripTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "retooled-patch-" + Guid.NewGuid().ToString("N"));
    private string Steam => Path.Combine(this.root, "steam");
    private string Gog => Path.Combine(this.root, "gog");
    private string Build => Path.Combine(this.root, "build");
    private string PatchPath => Path.Combine(this.root, "retooled-windows.rtpatch");

    private readonly Dictionary<string, string> gogTree;

    public PatchRoundTripTests()
    {
        Random rng = new(42);
        byte[] Bytes(int n)
        {
            byte[] b = new byte[n];
            rng.NextBytes(b);
            return b;
        }
        void Both(string relative, byte[] bytes)
        {
            Write(this.Steam, relative, bytes);
            Write(this.Gog, relative, bytes);
        }

        byte[] shared = Bytes(50_000), data = Bytes(300_000), dlc = Bytes(20_000), steamDll = Bytes(3000);
        Both("GameData/Squad/same.cfg", shared);
        Write(this.Steam, "KSP_x64_Data/level0", data);
        byte[] gogData = (byte[])data.Clone();
        gogData[100_000] ^= 0xFF;
        Write(this.Gog, "KSP_x64_Data/level0", gogData);
        Both("KSP_x64_Data/gone.assets", Bytes(1000));
        Write(this.Steam, "KSP_x64.exe", Bytes(4000));
        Write(this.Gog, "KSP_x64.exe", Bytes(4100));
        Write(this.Steam, "KSP_x64_Data/Plugins/x86_64/steam_api64.dll", steamDll);
        Write(this.Gog, "KSP_x64_Data/Plugins/x86_64/galaxy64.dll", Bytes(2000));
        Write(this.Steam, "buildID64.txt", [.."build id = 03190\ndistribution name = Steam\n"u8]);
        Write(this.Gog, "buildID64.txt", [.."build id = 03190\ndistribution name = GOG\nlanguage = ja\n"u8]);
        Both("GameData/SquadExpansion/Serenity/dlc.bin", dlc);
        Both("saves/default/persistent.sfs", Bytes(100));
        byte[] scenario = Bytes(300);
        Both("saves/scenarios/Mun Orbit.sfs", scenario);
        Write(this.Build, "saves/scenarios/Mun Orbit.sfs", scenario);
        Directory.CreateDirectory(Path.Combine(this.Steam, "Missions"));
        Directory.CreateDirectory(Path.Combine(this.Gog, "Missions"));
        Directory.CreateDirectory(Path.Combine(this.Build, "Missions"));

        Write(this.Build, "GameData/Squad/same.cfg", shared);
        byte[] edited = (byte[])data.Clone();
        edited[1234] ^= 0xFF;
        Write(this.Build, "KSP_x64_Data/level0", edited);
        Write(this.Build, "KSP_x64_Data/moved.cfg", shared);
        Write(this.Build, "KSP_x64.exe", Bytes(4000));
        Write(this.Build, "KSP_x64_Data/Plugins/x86_64/steam_api64.dll", steamDll);
        Write(this.Build, "buildID64.txt", [.."build id = retooled\n"u8]);
        Write(this.Build, "GameData/Retooled/new.dll", Bytes(10_000));
        byte[] dlcEdited = (byte[])dlc.Clone();
        dlcEdited[0] ^= 1;
        Write(this.Build, "GameData/SquadExpansion/Serenity/dlc.bin", dlcEdited);
        Write(this.Build, "KSP_BackUpThisFolder_ButDontShipItWithYourGame/x.pdb", Bytes(10));
        Write(this.Build, "KSP_x64_Data/Plugins/x86_64/lib_burst_generated.pdb", Bytes(2000));

        // payloads relocated between files, as Unity does with .resS data; GOG's second payload differs
        byte[] payloadA = Bytes(400_000), payloadB = Bytes(400_000);
        Both("KSP_x64_Data/sharedassets1.assets.resS", payloadA);
        Write(this.Steam, "KSP_x64_Data/sharedassets2.assets.resS", payloadB);
        Write(this.Gog, "KSP_x64_Data/sharedassets2.assets.resS", [.. payloadB.AsSpan(0, 200_000), .. Bytes(200_000)]);
        Write(this.Build, "KSP_x64_Data/sharedassets1.assets.resS", [.. payloadB.AsSpan(100_000, 250_000), .. Bytes(5000), .. payloadA.AsSpan(0, 300_000)]);

        // a chunk straddling the end of one copy run can match another stock file (C holds A's tail and the new data)
        byte[] tail = Bytes(150_000);
        Both("KSP_x64_Data/sharedassets3.assets.resS", [.. payloadA.AsSpan(200_000, 100_000), .. tail]);
        Write(this.Build, "KSP_x64_Data/sharedassets3.assets.resS", [.. payloadA.AsSpan(0, 300_000), .. tail]);

        // Finder and Explorer write these wherever a folder was browsed: neither stock files nor shipped
        Write(this.Steam, ".DS_Store", [.."Bud1 steam"u8]);
        Write(this.Gog, "GameData/.DS_Store", [.."Bud1 gog"u8]);
        Write(this.Build, ".DS_Store", [.."Bud1 build"u8]);
        Write(this.Build, "GameData/Squad/Thumbs.db", [.."thumbs"u8]);
        Write(this.Build, "GameData/desktop.ini", [.."[.ShellClassInfo]"u8]);

        this.gogTree = Tree(this.Gog);
        PatchBuilder.Build(Options(this.PatchPath));
    }

    private BuildOptions Options(string output) => new()
    {
        Sources = [new BuildSource { Roots = [this.Steam] }, new BuildSource { Roots = [this.Gog] }],
        Target  = this.Build,
        Output  = output,
        Level   = 3,
        Jobs    = 2,
    };

    private static void Write(string dir, string relative, byte[] bytes)
    {
        string path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static Dictionary<string, string> Tree(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), PatchApplier.Sha256Hex, StringComparer.OrdinalIgnoreCase);

    private static List<string> Directories(string dir) =>
    [
        ..Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                   .Select(d => Path.GetRelativePath(dir, d).Replace('\\', '/')).Order(StringComparer.Ordinal)
    ];

    private Dictionary<string, string> Shipped()
    {
        Dictionary<string, string> tree = Tree(this.Build);
        foreach (string excluded in new[] { "KSP_BackUpThisFolder_ButDontShipItWithYourGame/x.pdb", ".DS_Store", "GameData/Squad/Thumbs.db", "GameData/desktop.ini" })
        {
            tree.Remove(excluded);
        }

        return tree;
    }

    private PatchApplier Applier(string install) => new(PatchFile.Open(this.PatchPath), install);

    [Theory]
    [InlineData("steam")]
    [InlineData("gog")]
    public void EverySourceReproducesTheBuild(string source)
    {
        string output = Path.Combine(this.root, "out");
        InstallCheck check = Applier(Path.Combine(this.root, source)).Apply(new ApplyOptions { OutputDirectory = output });
        Assert.Equal(source + "-03190", check.Variant);
        Assert.Equal(Shipped().OrderBy(p => p.Key, StringComparer.Ordinal), Tree(output).OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(output, "Missions")));
    }

    [Fact]
    public void ABuildIdenticalToItsSourceHasNothingToDiff()
    {
        string patch = Path.Combine(this.root, "unchanged.rtpatch");

        PatchBuilder.Build(new BuildOptions
        {
            Sources = [new BuildSource { Roots = [this.Steam] }],
            Target  = this.Steam,
            Output  = patch,
            Level   = 3,
            Jobs    = 2,
        });

        Assert.All(PatchFile.Open(patch).Manifest.Outputs, o => Assert.NotNull(Assert.Single(o.Recipes).Same));
    }

    [Fact]
    public void SecondSourceOnlyAddsWhatItCannotReuse()
    {
        PatchManifest m = PatchFile.Open(this.PatchPath).Manifest;
        Assert.Equal(["steam-03190", "gog-03190"], m.Variants.Select(v => v.Id));
        Assert.Contains(m.Sections, s => s.Name is "core@gog-03190");
        // the executable and buildID are literals, which read no stock file, so GOG reuses them
        List<string> withAlternatives = [..m.Outputs.Where(o => o.Recipes.Count > 1).Select(o => o.Path).Order(StringComparer.Ordinal)];
        Assert.Equal(["KSP_x64_Data/Plugins/x86_64/steam_api64.dll", "KSP_x64_Data/level0", "KSP_x64_Data/sharedassets1.assets.resS"],
            withAlternatives);
        Assert.Equal(["KSP_x64_Data/gone.assets", "KSP_x64_Data/Plugins/x86_64/galaxy64.dll", "KSP_x64_Data/sharedassets2.assets.resS"],
            m.Components.Single(c => c.Name is "core").Remove);
    }

    [Fact]
    public void RelocatedStockBytesBecomeCopies()
    {
        PatchManifest m = PatchFile.Open(this.PatchPath).Manifest;
        OutputFile output = m.Outputs.Single(o => o.Path is "KSP_x64_Data/sharedassets1.assets.resS");
        List<Segment> copies = [..output.Recipes[0].Segments!.Where(s => s.Kind == SegmentKind.COPY)];
        Assert.Equal(550_000, copies.Sum(s => s.Length));
        Assert.Equal(["KSP_x64_Data/sharedassets1.assets.resS", "KSP_x64_Data/sharedassets2.assets.resS"],
            copies.Select(s => m.Sources[s.File!.Value].Path).Distinct().Order());
    }

    [Fact]
    public void MixedInstallStillApplies()
    {
        File.Copy(Path.Combine(this.Gog, "KSP_x64_Data/level0"), Path.Combine(this.Steam, "KSP_x64_Data/level0"), overwrite: true);
        string output = Path.Combine(this.root, "out");
        InstallCheck check = Applier(this.Steam).Apply(new ApplyOptions { OutputDirectory = output });
        Assert.Null(check.Variant);
        Assert.Equal(Shipped().OrderBy(p => p.Key, StringComparer.Ordinal), Tree(output).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void InPlaceThenRevertRestoresTheStockInstall()
    {
        List<string> directories = Directories(this.Gog);
        Applier(this.Gog).Apply(new ApplyOptions());
        Dictionary<string, string> tree = Tree(this.Gog);
        foreach ((string path, string hash) in Shipped()) Assert.Equal(hash, tree[path]);
        Assert.False(tree.ContainsKey("KSP_x64_Data/gone.assets"));
        Assert.False(tree.ContainsKey("KSP_x64_Data/Plugins/x86_64/galaxy64.dll"));
        Assert.True(tree.ContainsKey("saves/default/persistent.sfs"));
        Assert.True(tree.ContainsKey("GameData/.DS_Store"));
        Assert.True(Directory.Exists(Path.Combine(this.Gog, "Missions")));
        Assert.False(Directory.Exists(Path.Combine(this.Gog, InstallJournal.STAGING_DIRECTORY)));
        Assert.True(InstallJournal.Exists(this.Gog));

        // the game runs: a new save, a changed save and the texture cache
        Write(this.Gog, "saves/career/persistent.sfs", [1, 2, 3]);
        Write(this.Gog, "saves/default/persistent.sfs", [4, 5, 6]);
        Write(this.Gog, "PluginData/TextureCache/abc.bin", [7]);

        PatchReverter.Revert(this.Gog);
        Dictionary<string, string> expected = new(this.gogTree, StringComparer.OrdinalIgnoreCase)
        {
            ["saves/career/persistent.sfs"]  = Tree(this.Gog)["saves/career/persistent.sfs"],
            ["saves/default/persistent.sfs"] = Tree(this.Gog)["saves/default/persistent.sfs"],
        };
        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(directories.Append("saves/career").Order(StringComparer.Ordinal), Directories(this.Gog));
        Assert.False(InstallJournal.Exists(this.Gog));
    }

    [Fact]
    public void InPlaceSetsTheStockLanguageInSettings()
    {
        Write(this.Gog, "settings.cfg", [.."SETTINGS_FILE_VERSION = 1.3.0\r\nLANGUAGE = en-us\r\nTUTORIALS_EDITOR_ENABLE = False\r\n"u8]);
        Applier(this.Gog).Apply(new ApplyOptions());
        Assert.Equal("SETTINGS_FILE_VERSION = 1.3.0\r\nLANGUAGE = ja\r\nTUTORIALS_EDITOR_ENABLE = False\r\n",
            File.ReadAllText(Path.Combine(this.Gog, "settings.cfg")));
    }

    [Fact]
    public void InPlaceReplacesAHardLinkedSettingsFileInsteadOfWritingIntoIt()
    {
        const string STOCK = "LANGUAGE = en-us\n";
        Write(this.root, "shared-settings.cfg", Encoding.UTF8.GetBytes(STOCK));
        Assert.True(FileClone.TryHardLink(Path.Combine(this.root, "shared-settings.cfg"), Path.Combine(this.Gog, "settings.cfg")));

        Applier(this.Gog).Apply(new ApplyOptions());

        Assert.Equal("LANGUAGE = ja\n", File.ReadAllText(Path.Combine(this.Gog, "settings.cfg")));
        Assert.Equal(STOCK, File.ReadAllText(Path.Combine(this.root, "shared-settings.cfg")));
    }

    /// <summary>
    /// Moves the directory at <paramref name="relative"/> outside <paramref name="install"/> and puts a symlink
    /// at its original path. If symlink creation fails, restores the directory and returns false.
    /// </summary>
    private bool ShareDirectory(string install, string relative)
    {
        string inside = Path.Combine(install, relative), outside = Path.Combine(this.root, "shared", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        Directory.Move(inside, outside);
        try
        {
            Directory.CreateSymbolicLink(inside, outside);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Directory.Move(outside, inside);
            return false;
        }
    }

    [Fact]
    public void InPlaceCopiesASymlinkedDirectoryBeforeChangingIt()
    {
        if (!ShareDirectory(this.Gog, "KSP_x64_Data")) return;
        string shared = Path.Combine(this.root, "shared", "KSP_x64_Data");
        // Verify that the local copy preserves a nested symlink that the patch never traverses.
        Directory.CreateSymbolicLink(Path.Combine(shared, "Linked"), Path.Combine(this.Gog, "Missions"));
        Dictionary<string, string> before = Tree(shared);

        Applier(this.Gog).Apply(new ApplyOptions());

        Assert.Equal(before.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(shared).OrderBy(p => p.Key, StringComparer.Ordinal));
        DirectoryInfo data = new(Path.Combine(this.Gog, "KSP_x64_Data"));
        Assert.Null(data.LinkTarget);
        Assert.NotNull(new DirectoryInfo(Path.Combine(data.FullName, "Linked")).LinkTarget);
        Dictionary<string, string> tree = Tree(this.Gog);
        foreach ((string path, string hash) in Shipped()) Assert.Equal(hash, tree[path]);
        Assert.False(tree.ContainsKey("KSP_x64_Data/gone.assets"));

        PatchReverter.Revert(this.Gog);
        Assert.Equal(before.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(shared).OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(this.gogTree.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void InPlaceLeavesASymlinkedDirectoryItDoesNotTouch()
    {
        Write(this.Gog, "GameData/SomeMod/mod.cfg", [1, 2, 3]);
        if (!ShareDirectory(this.Gog, "GameData/SomeMod")) return;

        Applier(this.Gog).Apply(new ApplyOptions());

        Assert.NotNull(new DirectoryInfo(Path.Combine(this.Gog, "GameData/SomeMod")).LinkTarget);
    }

    [Fact]
    public void PatchedInstallIsRefusedUntilReverted()
    {
        Applier(this.Steam).Apply(new ApplyOptions());
        PatchException error = Assert.Throws<PatchException>(() => Applier(this.Steam).Apply(new ApplyOptions()));
        Assert.Contains("revert", error.Message);
        PatchReverter.Revert(this.Steam);
        Assert.True(Applier(this.Steam).Check().CanApply);
    }

    [Fact]
    public void AnApplyThatFailsAtTheLastStepLeavesTheStockInstall()
    {
        Write(this.Gog, "settings.cfg", [.."LANGUAGE = en-us\n"u8]);
        List<string> directories = Directories(this.Gog);
        Dictionary<string, string> stock = Tree(this.Gog);

        // every move, the settings and the removed directories are done by the time the journal can't be saved
        Assert.ThrowsAny<SystemException>(() => Applier(this.Gog).Apply(new ApplyOptions
        {
            BeforeMove = _ => Directory.CreateDirectory(Path.Combine(this.Gog, InstallJournal.DIRECTORY, "journal.json.tmp")),
        }));

        Assert.Equal(stock.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(directories, Directories(this.Gog));
        Assert.True(Applier(this.Gog).Check().CanApply);
    }

    [Fact]
    public void AnApplyBlockedPartWayByTheInstallLeavesTheStockInstall()
    {
        // a directory where the build has a new file, so the move in fails after the stock files have moved out
        Write(this.Gog, "GameData/Retooled/new.dll/mine.txt", [1]);
        Dictionary<string, string> stock = Tree(this.Gog);

        Assert.ThrowsAny<SystemException>(() => Applier(this.Gog).Apply(new ApplyOptions()));

        Assert.Equal(stock.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void AnApplyThatFailsWhileStagingLeavesNoScratch()
    {
        Dictionary<string, string> stock = Tree(this.Gog);
        using CancellationTokenSource cancel = new();
        SyncProgress progress = new(p =>
        {
            // ReSharper disable once AccessToDisposedClosure
            if (p.Stage is "write") cancel.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() =>
            Applier(this.Gog).Apply(new ApplyOptions { Progress = progress, Cancellation = cancel.Token }));

        Assert.Equal(stock.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void AnApplyLeavesNoTransactionFiles()
    {
        Applier(this.Gog).Apply(new ApplyOptions());
        Assert.False(Directory.Exists(Path.Combine(this.Gog, InstallJournal.TRANSACTION_DIRECTORY)));
        Assert.Equal(JournalState.APPLIED, InstallJournal.Load(this.Gog)!.State);
    }

    [Fact]
    public void ARevertFinishesARollbackThatFailed()
    {
        Dictionary<string, string> stock = Tree(this.Gog);
        string staging = Path.Combine(this.Gog, InstallJournal.STAGING_DIRECTORY);
        string? movedIn = null;

        PatchException error = Assert.Throws<PatchException>(() => Applier(this.Gog).Apply(new ApplyOptions
        {
            BeforeMove = path =>
            {
                if (movedIn is not null)
                {
                    // a file where the staged one came from stops the rollback there
                    Write(staging, movedIn, [1]);
                    throw new IOException("disk full");
                }
                string relative = Path.GetRelativePath(this.Gog, path);
                if (!relative.StartsWith(InstallJournal.DIRECTORY, StringComparison.Ordinal)) movedIn = relative;
            },
        }));

        Assert.Contains("disk full", error.Message);
        Assert.Contains("Undoing the install failed", error.Message);
        Assert.Equal(JournalState.COMMITTING, InstallJournal.Load(this.Gog)!.State);
        Assert.False(Applier(this.Gog).Check().CanApply);

        PatchReverter.Revert(this.Gog);
        Assert.Equal(stock.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    private sealed class SyncProgress(Action<PatchProgress> report) : IProgress<PatchProgress>
    {
        public void Report(PatchProgress value) => report(value);
    }

    [Fact]
    public void RevertUndoesAnInterruptedApply()
    {
        Applier(this.Gog).Apply(new ApplyOptions());
        // as if the apply stopped after backing up level0 but before moving the new file in
        InstallJournal journal = InstallJournal.Load(this.Gog)!;
        journal.State = JournalState.COMMITTING;
        journal.Save(this.Gog);
        File.Delete(Path.Combine(this.Gog, "KSP_x64_Data/level0"));
        File.Delete(Path.Combine(this.Gog, "GameData/Retooled/new.dll"));
        // and before backing up gone.assets
        File.Move(Path.Combine(this.Gog, InstallJournal.ORIGINAL_DIRECTORY, "KSP_x64_Data/gone.assets"), Path.Combine(this.Gog, "KSP_x64_Data/gone.assets"));

        PatchReverter.Revert(this.Gog);
        Assert.Equal(this.gogTree.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void DlcIsSkippedWhenNotInstalled()
    {
        Directory.Delete(Path.Combine(this.Gog, "GameData/SquadExpansion/Serenity"), recursive: true);
        string output = Path.Combine(this.root, "out");
        InstallCheck check = Applier(this.Gog).Apply(new ApplyOptions { OutputDirectory = output });
        Assert.Equal(["makinghistory", "serenity"], check.SkippedComponents);
        Dictionary<string, string> expected = Shipped();
        expected.Remove("GameData/SquadExpansion/Serenity/dlc.bin");
        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(output).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void ModifiedInstallIsRejectedBeforeWriting()
    {
        File.WriteAllBytes(Path.Combine(this.Gog, "KSP_x64_Data/level0"), [1, 2, 3]);
        string output = Path.Combine(this.root, "out");
        PatchException error = Assert.Throws<PatchException>(() => Applier(this.Gog).Apply(new ApplyOptions { OutputDirectory = output }));
        Assert.Contains("KSP_x64_Data/level0", error.Message);
        Assert.False(Directory.Exists(output));
    }

    private const string UNCHANGED_FILE = "saves/scenarios/Mun Orbit.sfs";

    [Fact]
    public void EditedUnchangedFileIsCarriedAcross()
    {
        File.WriteAllBytes(Path.Combine(this.Gog, UNCHANGED_FILE), [1, 2, 3]);
        string output = Path.Combine(this.root, "out");
        InstallCheck check = Applier(this.Gog).Apply(new ApplyOptions { OutputDirectory = output });
        Assert.Equal("gog-03190", check.Variant);
        Dictionary<string, string> expected = Shipped();
        expected[UNCHANGED_FILE] = PatchApplier.Sha256Hex(Path.Combine(this.Gog, UNCHANGED_FILE));
        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(output).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void MissingUnchangedFileStaysMissing()
    {
        File.Delete(Path.Combine(this.Gog, UNCHANGED_FILE));
        string output = Path.Combine(this.root, "out");
        Assert.Equal("gog-03190", Applier(this.Gog).Apply(new ApplyOptions { OutputDirectory = output }).Variant);
        Dictionary<string, string> expected = Shipped();
        expected.Remove(UNCHANGED_FILE);
        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(output).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InPlaceLeavesUnchangedFilesAlone(bool missing)
    {
        string path = Path.Combine(this.Gog, UNCHANGED_FILE);
        if (missing)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllBytes(path, [1, 2, 3]);
        }

        Dictionary<string, string> stock = Tree(this.Gog);

        Applier(this.Gog).Apply(new ApplyOptions());
        Assert.Equal(!missing, File.Exists(path));
        if (!missing) Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        Assert.DoesNotContain(InstallJournal.Load(this.Gog)!.Files, f => f.Path == UNCHANGED_FILE);

        PatchReverter.Revert(this.Gog);
        Assert.Equal(stock.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    // same.cfg is unchanged, but moved.cfg is a copy of it
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedFileAnotherOutputReadsIsStillRequired(bool missing)
    {
        string path = Path.Combine(this.Gog, "GameData/Squad/same.cfg");
        if (missing)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllBytes(path, [1, 2, 3]);
        }

        IReadOnlyList<string> problems = Applier(this.Gog).Check().Problems;
        Assert.Equal([(missing ? "missing  " : "modified ") + "GameData/Squad/same.cfg"], problems);
    }

    private string BuildWithCopiedPaths()
    {
        Write(this.Steam, "GameData/Squad/part.dds", [1]);
        Write(this.Gog, "GameData/Squad/part.dds", [2]);
        Write(this.Build, "GameData/Squad/part.dds", [1]);
        Write(this.Gog, "GameData/Squad/Mod/mod.cfg", [3]);
        Directory.CreateDirectory(Path.Combine(this.Gog, "GameData/Squad/Empty"));
        Write(this.Steam, "Plugins/store.dll", [4]);
        Write(this.Gog, "Plugins/store.dll", [4]);
        Write(this.Steam, "EULA.txt", [5]);
        Write(this.Gog, "EULA.txt", [6]);
        Write(this.Build, "EULA.txt", [7]);
        string patch = Path.Combine(this.root, "copied.rtpatch");
        PatchBuilder.Build(new BuildOptions
        {
            Sources     = [new BuildSource { Roots = [this.Steam] }, new BuildSource { Roots = [this.Gog] }],
            Target      = this.Build,
            Output      = patch,
            Level       = 3,
            Jobs        = 2,
            CopiedPaths = ["GameData/Squad/", "Plugins", "EULA.txt"],
        });
        return patch;
    }

    [Fact]
    public void ACopiedPathIsNotInThePatch()
    {
        PatchManifest m = PatchFile.Open(BuildWithCopiedPaths()).Manifest;

        Assert.Equal(PatchManifest.CURRENT_VERSION, m.Version);
        Assert.Equal(["GameData/Squad", "Plugins", "EULA.txt"], m.CopiedPaths);
        Assert.DoesNotContain(m.Sources.Select(s => s.Path).Concat(m.Outputs.Select(o => o.Path)).Concat(m.Components.SelectMany(c => c.Remove)),
            p => p.StartsWith("GameData/Squad/", StringComparison.Ordinal) || p.StartsWith("Plugins/", StringComparison.Ordinal) || p is "EULA.txt");
    }

    [Fact]
    public void ASideBySideApplyCopiesAFileAndADirectoryWhole()
    {
        string patch = BuildWithCopiedPaths();
        string output = Path.Combine(this.root, "out");

        InstallCheck check = new PatchApplier(PatchFile.Open(patch), this.Gog).Apply(new ApplyOptions { OutputDirectory = output });

        Assert.Equal("gog-03190", check.Variant);
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(output, "GameData/Squad/part.dds")));
        Assert.Equal([3], File.ReadAllBytes(Path.Combine(output, "GameData/Squad/Mod/mod.cfg")));
        Assert.True(Directory.Exists(Path.Combine(output, "GameData/Squad/Empty")));
        Assert.Equal([4], File.ReadAllBytes(Path.Combine(output, "Plugins/store.dll")));
        Assert.Equal([6], File.ReadAllBytes(Path.Combine(output, "EULA.txt")));
    }

    [Fact]
    public void AnInstallWithoutACopiedPathStillApplies()
    {
        string patch = BuildWithCopiedPaths();
        Directory.Delete(Path.Combine(this.Gog, "Plugins"), recursive: true);
        File.Delete(Path.Combine(this.Gog, "EULA.txt"));
        string output = Path.Combine(this.root, "out");

        new PatchApplier(PatchFile.Open(patch), this.Gog).Apply(new ApplyOptions { OutputDirectory = output });

        Assert.False(Directory.Exists(Path.Combine(output, "Plugins")));
        Assert.False(File.Exists(Path.Combine(output, "EULA.txt")));
    }

    [Fact]
    public void AnInPlaceApplyLeavesACopiedPathAlone()
    {
        string patch = BuildWithCopiedPaths();
        // no source has this content, as with a mod's texture
        Write(this.Gog, "GameData/Squad/part.dds", [9]);
        Dictionary<string, string> stock = Tree(this.Gog);

        new PatchApplier(PatchFile.Open(patch), this.Gog).Apply(new ApplyOptions());

        Assert.Equal([9], File.ReadAllBytes(Path.Combine(this.Gog, "GameData/Squad/part.dds")));
        Assert.Equal([6], File.ReadAllBytes(Path.Combine(this.Gog, "EULA.txt")));
        Assert.DoesNotContain(InstallJournal.Load(this.Gog)!.Files, f => f.Path.StartsWith("GameData/Squad/", StringComparison.Ordinal) || f.Path is "EULA.txt");

        PatchReverter.Revert(this.Gog);
        Assert.Equal(stock.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void ACopiedPathCannotBeAPattern()
    {
        PatchException error = Assert.Throws<PatchException>(() => PatchBuilder.Build(new BuildOptions
        {
            Sources     = [new BuildSource { Roots = [this.Steam] }],
            Target      = this.Build,
            Output      = Path.Combine(this.root, "pattern.rtpatch"),
            CopiedPaths = ["unins*"],
        }));

        Assert.Contains("unins*", error.Message);
    }

    [Theory]
    [InlineData(PatchManifest.OLDEST_VERSION, true)]
    [InlineData(PatchManifest.CURRENT_VERSION, true)]
    [InlineData(PatchManifest.OLDEST_VERSION - 1, false)]
    [InlineData(PatchManifest.CURRENT_VERSION + 1, false)]
    public void OnlyKnownManifestVersionsAreRead(int version, bool readable)
    {
        string patch = Path.Combine(this.root, "version.rtpatch");
        using (PatchFileWriter writer = new(patch, 3, 27, 1))
        {
            writer.Finish(new PatchManifest { Version = version, Platform = "windows" });
        }

        if (readable)
        {
            Assert.Equal(version, PatchFile.Open(patch).Manifest.Version);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => PatchFile.Open(patch));
        }
    }

    [Fact]
    public void BuildWithoutStockLayoutIsRejected()
    {
        Directory.Delete(Path.Combine(this.Build, "saves"), recursive: true);
        PatchException error = Assert.Throws<PatchException>(() => PatchBuilder.Build(Options(Path.Combine(this.root, "other.rtpatch"))));
        Assert.Contains("saves", error.Message);
    }

    // Only a CoreCLR release leaves out stock's Mono runtime, and an in-place apply then takes it out of the install.
    [Fact]
    public void CoreClrBuildRemovesTheStockMonoRuntime()
    {
        const string RUNTIME = "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll";
        Write(this.Steam, RUNTIME, [1, 2, 3]);
        Write(this.Gog, RUNTIME, [1, 2, 3]);
        Write(this.Gog, "MonoBleedingEdge/etc/mono/config", [4]);
        Dictionary<string, string> stockTree = Tree(this.Gog);
        string patch = Path.Combine(this.root, "coreclr.rtpatch");

        PatchException error = Assert.Throws<PatchException>(() => PatchBuilder.Build(Options(patch)));
        Assert.Contains("MonoBleedingEdge", error.Message);

        Write(this.Build, "CoreCLR/native/coreclr.dll", [5, 6]);
        PatchBuilder.Build(Options(patch));
        new PatchApplier(PatchFile.Open(patch), this.Gog).Apply(new ApplyOptions());
        Assert.False(Directory.Exists(Path.Combine(this.Gog, "MonoBleedingEdge")));
        Assert.True(File.Exists(Path.Combine(this.Gog, "CoreCLR/native/coreclr.dll")));

        PatchReverter.Revert(this.Gog);
        Assert.Equal(stockTree.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    // The Retooled macOS player ships MainMenu.nib as a bundle, where stock has a file of that name.
    [Fact]
    public void InPlaceTurnsAStockFileIntoADirectoryAndBack()
    {
        const string BUNDLE = "KSP_x64_Data/MainMenu.nib";
        Write(this.Steam, BUNDLE, [1, 2, 3]);
        Write(this.Gog, BUNDLE, [1, 2, 3]);
        Write(this.Build, BUNDLE + "/keyedobjects.nib", [4, 5]);
        Write(this.Build, BUNDLE + "/designable.nib", [6]);
        Dictionary<string, string> stockTree = Tree(this.Gog);
        string patch = Path.Combine(this.root, "bundle.rtpatch");
        PatchBuilder.Build(Options(patch));

        new PatchApplier(PatchFile.Open(patch), this.Gog).Apply(new ApplyOptions());
        Assert.Equal([4, 5], File.ReadAllBytes(Path.Combine(this.Gog, BUNDLE, "keyedobjects.nib")));
        Assert.Equal([6], File.ReadAllBytes(Path.Combine(this.Gog, BUNDLE, "designable.nib")));

        Write(this.Gog, BUNDLE + "/.DS_Store", [7]);
        PatchReverter.Revert(this.Gog);
        Assert.Equal(stockTree.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void AFailedApplyPutsBackFilesAndDirectoriesThatSwappedPlaces()
    {
        Write(this.Steam, "KSP_x64_Data/MainMenu.nib", [1, 2, 3]);
        Write(this.Gog, "KSP_x64_Data/MainMenu.nib", [1, 2, 3]);
        Write(this.Build, "KSP_x64_Data/MainMenu.nib/keyedobjects.nib", [4, 5]);
        Write(this.Steam, "KSP_x64_Data/Folder/file.bin", [1, 2, 3]);
        Write(this.Gog, "KSP_x64_Data/Folder/file.bin", [1, 2, 3]);
        Write(this.Build, "KSP_x64_Data/Folder", [4, 5]);
        List<string> directories = Directories(this.Gog);
        Dictionary<string, string> stock = Tree(this.Gog);
        string patch = Path.Combine(this.root, "swapped.rtpatch");
        PatchBuilder.Build(Options(patch));

        Assert.ThrowsAny<SystemException>(() => new PatchApplier(PatchFile.Open(patch), this.Gog).Apply(new ApplyOptions
        {
            BeforeMove = _ => Directory.CreateDirectory(Path.Combine(this.Gog, InstallJournal.DIRECTORY, "journal.json.tmp")),
        }));

        Assert.Equal(stock.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(directories, Directories(this.Gog));
    }

    [Fact]
    public void InPlaceTurnsAStockDirectoryIntoAFileAndBack()
    {
        const string FOLDER = "KSP_x64_Data/Folder";
        Write(this.Steam, FOLDER + "/file.bin", [1, 2, 3]);
        Write(this.Gog, FOLDER + "/file.bin", [1, 2, 3]);
        Write(this.Build, FOLDER, [4]);
        Dictionary<string, string> stockTree = Tree(this.Gog);
        string patch = Path.Combine(this.root, "folder.rtpatch");
        PatchBuilder.Build(Options(patch));

        new PatchApplier(PatchFile.Open(patch), this.Gog).Apply(new ApplyOptions());
        Assert.Equal([4], File.ReadAllBytes(Path.Combine(this.Gog, FOLDER)));

        PatchReverter.Revert(this.Gog);
        Assert.Equal(stockTree.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    // older applies listed the files they removed last
    [Fact]
    public void RevertReadsAJournalThatListsRemovedFilesLast()
    {
        Applier(this.Gog).Apply(new ApplyOptions());
        InstallJournal journal = InstallJournal.Load(this.Gog)!;
        journal.Files = [.. journal.Files.OrderBy(f => f.Action == JournalAction.REMOVED)];
        journal.Save(this.Gog);

        PatchReverter.Revert(this.Gog);
        Assert.Equal(this.gogTree.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void RevertLeavesADirectoryTheApplyDidNotMake()
    {
        Applier(this.Gog).Apply(new ApplyOptions());
        Write(this.Gog, "KSP_x64_Data/gone.assets/mine.txt", [1]);

        // an IOException on Unix, an UnauthorizedAccessException on Windows
        Assert.ThrowsAny<SystemException>(() => PatchReverter.Revert(this.Gog));
        Assert.True(File.Exists(Path.Combine(this.Gog, "KSP_x64_Data/gone.assets/mine.txt")));
        Assert.True(InstallJournal.Exists(this.Gog));

        File.Delete(Path.Combine(this.Gog, "KSP_x64_Data/gone.assets/mine.txt"));
        Directory.Delete(Path.Combine(this.Gog, "KSP_x64_Data/gone.assets"));
        PatchReverter.Revert(this.Gog);
        Assert.Equal(this.gogTree.OrderBy(p => p.Key, StringComparer.Ordinal), Tree(this.Gog).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void CommandLineBuildsChecksAndApplies()
    {
        string patch = Path.Combine(this.root, "cli.rtpatch"), output = Path.Combine(this.root, "out");
        Assert.Equal(0, Program.Main(["build", "--source", "steam=" + this.Steam, "--source", this.Gog, "--target", this.Build,
                                         "--out", patch, "--level", "3"]));
        Assert.True(File.Exists(Path.Combine(this.root, "cli.report.txt")));
        Assert.Equal(["steam", "gog-03190"], PatchFile.Open(patch).Manifest.Variants.Select(v => v.Id));
        Assert.Equal(0, Program.Main(["check", "--install", this.Gog, "--patch", patch]));
        Assert.Equal(0, Program.Main(["apply", "--install", this.Gog, "--patch", patch, "--out", output]));
        Assert.Equal(Shipped().OrderBy(p => p.Key, StringComparer.Ordinal), Tree(output).OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(2, Program.Main(["apply", "--install", this.Gog, "--patch", patch]));
    }

    public void Dispose()
    {
        if (Directory.Exists(this.root)) Directory.Delete(this.root, recursive: true);
    }
}

public sealed class FileCloneTests : IDisposable
{
    // Create the test files on the same volume as the test assembly.
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "clone-" + Guid.NewGuid().ToString("N"));

    public FileCloneTests() => Directory.CreateDirectory(this.root);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(70_000)]
    [InlineData(3 << 20)]
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

    public void Dispose()
    {
        if (Directory.Exists(this.root)) Directory.Delete(this.root, recursive: true);
    }
}
