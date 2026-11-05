using Xunit;

namespace KSP.Retooled.Patch.Tests;

public sealed class SuffixSortTests
{
    static int[] BruteForce(byte[] text)
    {
        var suffixes = Enumerable.Range(0, text.Length + 1).ToArray();
        Array.Sort(suffixes, (a, b) => text.AsSpan(a).SequenceCompareTo(text.AsSpan(b)));
        return suffixes;
    }

    static byte[] Repeats(int seed, int length, int alphabet)
    {
        var rng = new Random(seed);
        var bytes = new List<byte>(length);
        while (bytes.Count < length)
        {
            if (bytes.Count > 8 && rng.Next(3) == 0)
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

    public static TheoryData<byte[]> Texts => new()
    {
        Array.Empty<byte>(),
        new byte[] { 0 },
        new byte[] { 255 },
        new byte[] { 0, 0, 0, 0, 0, 0 },
        new byte[] { 255, 255, 255, 255 },
        new byte[] { 1, 2, 1, 2, 1, 2, 1, 2 },
        new byte[] { 2, 1, 2, 1, 2, 1, 2, 1, 2 },
        new byte[] { 255, 0, 255, 0, 1, 255 },
        "mississippi"u8.ToArray(),
        Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(),
        Enumerable.Range(0, 256).Select(i => (byte)(255 - i)).ToArray(),
        Enumerable.Repeat<byte>(7, 5000).ToArray(),
    };

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
            Assert.True(large.AsSpan(suffixes[i - 1]).SequenceCompareTo(large.AsSpan(suffixes[i])) < 0);
        Assert.Equal(Enumerable.Range(0, suffixes.Length), suffixes.Order());
    }

    [Fact]
    public void WritesIntoALargerBuffer()
    {
        byte[] text = "banana"u8.ToArray();
        var buffer = new int[32];
        SuffixSorter.Sort(text, buffer);
        Assert.Equal(BruteForce(text), buffer[..(text.Length + 1)]);
    }

    /// <summary>The first three bytes of a suffix, padded with zeros as the prefix index counts them.</summary>
    static byte[] Padded(byte[] text, int suffix)
    {
        byte[] key = new byte[3];
        text.AsSpan(suffix, Math.Min(3, text.Length - suffix)).CopyTo(key);
        return key;
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 256)]
    public void TheIndexPointsAtEachPrefixInTheSuffixArray(int seed, int alphabet)
    {
        byte[] old = Repeats(seed, 5_000, alphabet);
        int[] suffixes = BsDiff.SuffixSort(old), index = BsDiff.PrefixIndex(old);

        for (int a = 0; a < Math.Min(alphabet, 8); a++)
            for (int b = 0; b < Math.Min(alphabet, 8); b++)
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

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 256)]
    public void DiffingAgainstShortReferencesRoundTrips(int seed, int alphabet)
    {
        for (int length = 0; length < 40; length++)
        {
            byte[] old = Repeats(seed + length, length, alphabet), @new = Repeats(seed * 7 + length, 300, alphabet);
            using var output = new MemoryStream();
            BsPatch.Apply(old, BsDiff.Diff(old, @new), output);
            Assert.Equal(@new, output.ToArray());
        }
    }
}

public sealed class BsDiffTests
{
    // returns the compressed patch size: raw patches are about as long as the new data
    static int RoundTrip(byte[] old, byte[] @new)
    {
        byte[] patch = BsDiff.Diff(old, @new);
        using var output = new MemoryStream();
        BsPatch.Apply(old, patch, output);
        Assert.Equal(@new, output.ToArray());
        using var compressor = new ZstdSharp.Compressor(3);
        return compressor.Wrap(patch).Length;
    }

    static byte[] Random(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void EditedDataProducesSmallPatch()
    {
        var old = Random(1, 200_000);
        var edited = new List<byte>(old);
        var rng = new Random(2);
        for (int i = 0; i < 50; i++) edited[rng.Next(edited.Count)] ^= 0x5A;
        edited.InsertRange(1000, Random(3, 300));
        edited.RemoveRange(150_000, 2000);
        int compressed = RoundTrip(old, edited.ToArray());
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
        var old = new byte[100_000];
        var @new = new byte[120_000];
        for (int i = 0; i < @new.Length; i++) @new[i] = (byte)(i % 7 == 0 ? 1 : 0);
        RoundTrip(old, @new);
    }
}

/// <summary>
/// Two store variants of one stock install (Steam and GOG: different player data, executable and store DLL) patched
/// into one build that ships Steam's DLL.
/// </summary>
public sealed class PatchRoundTripTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "retooled-patch-" + Guid.NewGuid().ToString("N"));
    string Steam => Path.Combine(root, "steam");
    string Gog => Path.Combine(root, "gog");
    string Build => Path.Combine(root, "build");
    string PatchPath => Path.Combine(root, "retooled-windows.rtpatch");

    readonly Dictionary<string, string> gogTree;

    public PatchRoundTripTests()
    {
        var rng = new Random(42);
        byte[] Bytes(int n)
        {
            var b = new byte[n];
            rng.NextBytes(b);
            return b;
        }
        void Both(string relative, byte[] bytes)
        {
            Write(Steam, relative, bytes);
            Write(Gog, relative, bytes);
        }

        byte[] shared = Bytes(50_000), data = Bytes(300_000), dlc = Bytes(20_000), steamDll = Bytes(3000);
        Both("GameData/Squad/same.cfg", shared);
        Write(Steam, "KSP_x64_Data/level0", data);
        var gogData = (byte[])data.Clone();
        gogData[100_000] ^= 0xFF;
        Write(Gog, "KSP_x64_Data/level0", gogData);
        Both("KSP_x64_Data/gone.assets", Bytes(1000));
        Write(Steam, "KSP_x64.exe", Bytes(4000));
        Write(Gog, "KSP_x64.exe", Bytes(4100));
        Write(Steam, "KSP_x64_Data/Plugins/x86_64/steam_api64.dll", steamDll);
        Write(Gog, "KSP_x64_Data/Plugins/x86_64/galaxy64.dll", Bytes(2000));
        Write(Steam, "buildID64.txt", "build id = 03190\ndistribution name = Steam\n"u8.ToArray());
        Write(Gog, "buildID64.txt", "build id = 03190\ndistribution name = GOG\nlanguage = ja\n"u8.ToArray());
        Both("GameData/SquadExpansion/Serenity/dlc.bin", dlc);
        Both("saves/default/persistent.sfs", Bytes(100));
        byte[] scenario = Bytes(300);
        Both("saves/scenarios/Mun Orbit.sfs", scenario);
        Write(Build, "saves/scenarios/Mun Orbit.sfs", scenario);
        Directory.CreateDirectory(Path.Combine(Steam, "Missions"));
        Directory.CreateDirectory(Path.Combine(Gog, "Missions"));
        Directory.CreateDirectory(Path.Combine(Build, "Missions"));

        Write(Build, "GameData/Squad/same.cfg", shared);
        var edited = (byte[])data.Clone();
        edited[1234] ^= 0xFF;
        Write(Build, "KSP_x64_Data/level0", edited);
        Write(Build, "KSP_x64_Data/moved.cfg", shared);
        Write(Build, "KSP_x64.exe", Bytes(4000));
        Write(Build, "KSP_x64_Data/Plugins/x86_64/steam_api64.dll", steamDll);
        Write(Build, "buildID64.txt", "build id = retooled\n"u8.ToArray());
        Write(Build, "GameData/Retooled/new.dll", Bytes(10_000));
        var dlcEdited = (byte[])dlc.Clone();
        dlcEdited[0] ^= 1;
        Write(Build, "GameData/SquadExpansion/Serenity/dlc.bin", dlcEdited);
        Write(Build, "KSP_BackUpThisFolder_ButDontShipItWithYourGame/x.pdb", Bytes(10));

        // payloads relocated between files, as Unity does with .resS data; GOG's second payload differs
        byte[] payloadA = Bytes(400_000), payloadB = Bytes(400_000);
        Both("KSP_x64_Data/sharedassets1.assets.resS", payloadA);
        Write(Steam, "KSP_x64_Data/sharedassets2.assets.resS", payloadB);
        Write(Gog, "KSP_x64_Data/sharedassets2.assets.resS", [.. payloadB.AsSpan(0, 200_000), .. Bytes(200_000)]);
        Write(Build, "KSP_x64_Data/sharedassets1.assets.resS", [.. payloadB.AsSpan(100_000, 250_000), .. Bytes(5000), .. payloadA.AsSpan(0, 300_000)]);

        // a chunk straddling the end of one copy run can match another stock file (C holds A's tail and the new data)
        byte[] tail = Bytes(150_000);
        Both("KSP_x64_Data/sharedassets3.assets.resS", [.. payloadA.AsSpan(200_000, 100_000), .. tail]);
        Write(Build, "KSP_x64_Data/sharedassets3.assets.resS", [.. payloadA.AsSpan(0, 300_000), .. tail]);

        // Finder and Explorer write these wherever a folder was browsed: neither stock files nor shipped
        Write(Steam, ".DS_Store", "Bud1 steam"u8.ToArray());
        Write(Gog, "GameData/.DS_Store", "Bud1 gog"u8.ToArray());
        Write(Build, ".DS_Store", "Bud1 build"u8.ToArray());
        Write(Build, "GameData/Squad/Thumbs.db", "thumbs"u8.ToArray());
        Write(Build, "GameData/desktop.ini", "[.ShellClassInfo]"u8.ToArray());

        gogTree = Tree(Gog);
        PatchBuilder.Build(Options(PatchPath));
    }

    BuildOptions Options(string output) => new()
    {
        Sources = [new BuildSource { Roots = [Steam] }, new BuildSource { Roots = [Gog] }],
        Target = Build,
        Output = output,
        Level = 3,
        Jobs = 2,
    };

    static void Write(string dir, string relative, byte[] bytes)
    {
        string path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    static Dictionary<string, string> Tree(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), PatchApplier.Sha256Hex, StringComparer.OrdinalIgnoreCase);

    static List<string> Directories(string dir) =>
        Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(dir, d).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    Dictionary<string, string> Shipped()
    {
        var tree = Tree(Build);
        foreach (string excluded in new[] { "KSP_BackUpThisFolder_ButDontShipItWithYourGame/x.pdb", ".DS_Store", "GameData/Squad/Thumbs.db", "GameData/desktop.ini" })
            tree.Remove(excluded);
        return tree;
    }

    PatchApplier Applier(string install) => new(PatchFile.Open(PatchPath), install);

    [Theory]
    [InlineData("steam")]
    [InlineData("gog")]
    public void EverySourceReproducesTheBuild(string source)
    {
        string output = Path.Combine(root, "out");
        var check = Applier(Path.Combine(root, source)).Apply(new ApplyOptions { OutputDirectory = output });
        Assert.Equal(source + "-03190", check.Variant);
        Assert.Equal(Shipped().OrderBy(p => p.Key), Tree(output).OrderBy(p => p.Key));
        Assert.True(Directory.Exists(Path.Combine(output, "Missions")));
    }

    [Fact]
    public void ABuildIdenticalToItsSourceHasNothingToDiff()
    {
        string patch = Path.Combine(root, "unchanged.rtpatch");

        PatchBuilder.Build(new BuildOptions
        {
            Sources = [new BuildSource { Roots = [Steam] }],
            Target = Steam,
            Output = patch,
            Level = 3,
            Jobs = 2,
        });

        Assert.All(PatchFile.Open(patch).Manifest.Outputs, o => Assert.NotNull(Assert.Single(o.Recipes).Same));
    }

    [Fact]
    public void SecondSourceOnlyAddsWhatItCannotReuse()
    {
        var m = PatchFile.Open(PatchPath).Manifest;
        Assert.Equal(["steam-03190", "gog-03190"], m.Variants.Select(v => v.Id));
        Assert.Contains(m.Sections, s => s.Name == "core@gog-03190");
        // the executable and buildID are literals, which read no stock file, so GOG reuses them
        var withAlternatives = m.Outputs.Where(o => o.Recipes.Count > 1).Select(o => o.Path).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["KSP_x64_Data/Plugins/x86_64/steam_api64.dll", "KSP_x64_Data/level0", "KSP_x64_Data/sharedassets1.assets.resS"],
            withAlternatives);
        Assert.Equal(["KSP_x64_Data/gone.assets", "KSP_x64_Data/Plugins/x86_64/galaxy64.dll", "KSP_x64_Data/sharedassets2.assets.resS"],
            m.Components.Single(c => c.Name == "core").Remove);
    }

    [Fact]
    public void RelocatedStockBytesBecomeCopies()
    {
        var m = PatchFile.Open(PatchPath).Manifest;
        var output = m.Outputs.Single(o => o.Path == "KSP_x64_Data/sharedassets1.assets.resS");
        var copies = output.Recipes[0].Segments!.Where(s => s.Kind == SegmentKind.Copy).ToList();
        Assert.Equal(550_000, copies.Sum(s => s.Length));
        Assert.Equal(["KSP_x64_Data/sharedassets1.assets.resS", "KSP_x64_Data/sharedassets2.assets.resS"],
            copies.Select(s => m.Sources[s.File!.Value].Path).Distinct().Order());
    }

    [Fact]
    public void MixedInstallStillApplies()
    {
        File.Copy(Path.Combine(Gog, "KSP_x64_Data/level0"), Path.Combine(Steam, "KSP_x64_Data/level0"), overwrite: true);
        string output = Path.Combine(root, "out");
        var check = Applier(Steam).Apply(new ApplyOptions { OutputDirectory = output });
        Assert.Null(check.Variant);
        Assert.Equal(Shipped().OrderBy(p => p.Key), Tree(output).OrderBy(p => p.Key));
    }

    [Fact]
    public void InPlaceThenRevertRestoresTheStockInstall()
    {
        var directories = Directories(Gog);
        Applier(Gog).Apply(new ApplyOptions());
        var tree = Tree(Gog);
        foreach (var (path, hash) in Shipped()) Assert.Equal(hash, tree[path]);
        Assert.False(tree.ContainsKey("KSP_x64_Data/gone.assets"));
        Assert.False(tree.ContainsKey("KSP_x64_Data/Plugins/x86_64/galaxy64.dll"));
        Assert.True(tree.ContainsKey("saves/default/persistent.sfs"));
        Assert.True(tree.ContainsKey("GameData/.DS_Store"));
        Assert.True(Directory.Exists(Path.Combine(Gog, "Missions")));
        Assert.False(Directory.Exists(Path.Combine(Gog, InstallJournal.StagingDirectory)));
        Assert.True(InstallJournal.Exists(Gog));

        // the game runs: a new save, a changed save and the texture cache
        Write(Gog, "saves/career/persistent.sfs", [1, 2, 3]);
        Write(Gog, "saves/default/persistent.sfs", [4, 5, 6]);
        Write(Gog, "PluginData/TextureCache/abc.bin", [7]);

        PatchReverter.Revert(Gog);
        var expected = new Dictionary<string, string>(gogTree, StringComparer.OrdinalIgnoreCase)
        {
            ["saves/career/persistent.sfs"] = Tree(Gog)["saves/career/persistent.sfs"],
            ["saves/default/persistent.sfs"] = Tree(Gog)["saves/default/persistent.sfs"],
        };
        Assert.Equal(expected.OrderBy(p => p.Key), Tree(Gog).OrderBy(p => p.Key));
        Assert.Equal(directories.Append("saves/career").Order(StringComparer.Ordinal), Directories(Gog));
        Assert.False(InstallJournal.Exists(Gog));
    }

    [Fact]
    public void InPlaceSetsTheStockLanguageInSettings()
    {
        Write(Gog, "settings.cfg", "SETTINGS_FILE_VERSION = 1.3.0\r\nLANGUAGE = en-us\r\nTUTORIALS_EDITOR_ENABLE = False\r\n"u8.ToArray());
        Applier(Gog).Apply(new ApplyOptions());
        Assert.Equal("SETTINGS_FILE_VERSION = 1.3.0\r\nLANGUAGE = ja\r\nTUTORIALS_EDITOR_ENABLE = False\r\n",
            File.ReadAllText(Path.Combine(Gog, "settings.cfg")));
    }

    [Fact]
    public void PatchedInstallIsRefusedUntilReverted()
    {
        Applier(Steam).Apply(new ApplyOptions());
        var error = Assert.Throws<PatchException>(() => Applier(Steam).Apply(new ApplyOptions()));
        Assert.Contains("revert", error.Message);
        PatchReverter.Revert(Steam);
        Assert.True(Applier(Steam).Check().CanApply);
    }

    [Fact]
    public void RevertUndoesAnInterruptedApply()
    {
        Applier(Gog).Apply(new ApplyOptions());
        // as if the apply stopped after backing up level0 but before moving the new file in
        var journal = InstallJournal.Load(Gog)!;
        journal.State = JournalState.Committing;
        journal.Save(Gog);
        File.Delete(Path.Combine(Gog, "KSP_x64_Data/level0"));
        File.Delete(Path.Combine(Gog, "GameData/Retooled/new.dll"));
        // and before backing up gone.assets
        File.Move(Path.Combine(Gog, InstallJournal.OriginalDirectory, "KSP_x64_Data/gone.assets"), Path.Combine(Gog, "KSP_x64_Data/gone.assets"));

        PatchReverter.Revert(Gog);
        Assert.Equal(gogTree.OrderBy(p => p.Key), Tree(Gog).OrderBy(p => p.Key));
    }

    [Fact]
    public void DlcIsSkippedWhenNotInstalled()
    {
        Directory.Delete(Path.Combine(Gog, "GameData/SquadExpansion/Serenity"), recursive: true);
        string output = Path.Combine(root, "out");
        var check = Applier(Gog).Apply(new ApplyOptions { OutputDirectory = output });
        Assert.Equal(["makinghistory", "serenity"], check.SkippedComponents);
        var expected = Shipped();
        expected.Remove("GameData/SquadExpansion/Serenity/dlc.bin");
        Assert.Equal(expected.OrderBy(p => p.Key), Tree(output).OrderBy(p => p.Key));
    }

    [Fact]
    public void ModifiedInstallIsRejectedBeforeWriting()
    {
        File.WriteAllBytes(Path.Combine(Gog, "KSP_x64_Data/level0"), [1, 2, 3]);
        string output = Path.Combine(root, "out");
        var error = Assert.Throws<PatchException>(() => Applier(Gog).Apply(new ApplyOptions { OutputDirectory = output }));
        Assert.Contains("KSP_x64_Data/level0", error.Message);
        Assert.False(Directory.Exists(output));
    }

    const string UnchangedFile = "saves/scenarios/Mun Orbit.sfs";

    [Fact]
    public void EditedUnchangedFileIsCarriedAcross()
    {
        File.WriteAllBytes(Path.Combine(Gog, UnchangedFile), [1, 2, 3]);
        string output = Path.Combine(root, "out");
        var check = Applier(Gog).Apply(new ApplyOptions { OutputDirectory = output });
        Assert.Equal("gog-03190", check.Variant);
        var expected = Shipped();
        expected[UnchangedFile] = PatchApplier.Sha256Hex(Path.Combine(Gog, UnchangedFile));
        Assert.Equal(expected.OrderBy(p => p.Key), Tree(output).OrderBy(p => p.Key));
    }

    [Fact]
    public void MissingUnchangedFileStaysMissing()
    {
        File.Delete(Path.Combine(Gog, UnchangedFile));
        string output = Path.Combine(root, "out");
        Assert.Equal("gog-03190", Applier(Gog).Apply(new ApplyOptions { OutputDirectory = output }).Variant);
        var expected = Shipped();
        expected.Remove(UnchangedFile);
        Assert.Equal(expected.OrderBy(p => p.Key), Tree(output).OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InPlaceLeavesUnchangedFilesAlone(bool missing)
    {
        string path = Path.Combine(Gog, UnchangedFile);
        if (missing) File.Delete(path);
        else File.WriteAllBytes(path, [1, 2, 3]);
        var stock = Tree(Gog);

        Applier(Gog).Apply(new ApplyOptions());
        Assert.Equal(!missing, File.Exists(path));
        if (!missing) Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        Assert.DoesNotContain(InstallJournal.Load(Gog)!.Files, f => f.Path == UnchangedFile);

        PatchReverter.Revert(Gog);
        Assert.Equal(stock.OrderBy(p => p.Key), Tree(Gog).OrderBy(p => p.Key));
    }

    // same.cfg is unchanged, but moved.cfg is a copy of it
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedFileAnotherOutputReadsIsStillRequired(bool missing)
    {
        string path = Path.Combine(Gog, "GameData/Squad/same.cfg");
        if (missing) File.Delete(path);
        else File.WriteAllBytes(path, [1, 2, 3]);
        var problems = Applier(Gog).Check().Problems;
        Assert.Equal([(missing ? "missing  " : "modified ") + "GameData/Squad/same.cfg"], problems);
    }

    [Fact]
    public void BuildWithoutStockLayoutIsRejected()
    {
        Directory.Delete(Path.Combine(Build, "saves"), recursive: true);
        var error = Assert.Throws<PatchException>(() => PatchBuilder.Build(Options(Path.Combine(root, "other.rtpatch"))));
        Assert.Contains("saves", error.Message);
    }

    // Only a CoreCLR release leaves out stock's Mono runtime, and an in-place apply then takes it out of the install.
    [Fact]
    public void CoreClrBuildRemovesTheStockMonoRuntime()
    {
        const string runtime = "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll";
        Write(Steam, runtime, [1, 2, 3]);
        Write(Gog, runtime, [1, 2, 3]);
        Write(Gog, "MonoBleedingEdge/etc/mono/config", [4]);
        var stockTree = Tree(Gog);
        string patch = Path.Combine(root, "coreclr.rtpatch");

        var error = Assert.Throws<PatchException>(() => PatchBuilder.Build(Options(patch)));
        Assert.Contains("MonoBleedingEdge", error.Message);

        Write(Build, "CoreCLR/native/coreclr.dll", [5, 6]);
        PatchBuilder.Build(Options(patch));
        new PatchApplier(PatchFile.Open(patch), Gog).Apply(new ApplyOptions());
        Assert.False(Directory.Exists(Path.Combine(Gog, "MonoBleedingEdge")));
        Assert.True(File.Exists(Path.Combine(Gog, "CoreCLR/native/coreclr.dll")));

        PatchReverter.Revert(Gog);
        Assert.Equal(stockTree.OrderBy(p => p.Key), Tree(Gog).OrderBy(p => p.Key));
    }

    [Fact]
    public void CommandLineBuildsChecksAndApplies()
    {
        string patch = Path.Combine(root, "cli.rtpatch"), output = Path.Combine(root, "out");
        Assert.Equal(0, Cli.Program.Main(["build", "--source", "steam=" + Steam, "--source", Gog, "--target", Build,
            "--out", patch, "--level", "3"]));
        Assert.True(File.Exists(Path.Combine(root, "cli.report.txt")));
        Assert.Equal(["steam", "gog-03190"], PatchFile.Open(patch).Manifest.Variants.Select(v => v.Id));
        Assert.Equal(0, Cli.Program.Main(["check", "--install", Gog, "--patch", patch]));
        Assert.Equal(0, Cli.Program.Main(["apply", "--install", Gog, "--patch", patch, "--out", output]));
        Assert.Equal(Shipped().OrderBy(p => p.Key), Tree(output).OrderBy(p => p.Key));
        Assert.Equal(2, Cli.Program.Main(["apply", "--install", Gog, "--patch", patch]));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
