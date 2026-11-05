using KSP.Retooled.Launcher.Installs;
using KSP.Retooled.Launcher.Releases;
using KSP.Retooled.Patch;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

/// <summary>Serves patch files built by the test, by tag.</summary>
sealed class FakePatches : IPatchProvider
{
    public readonly Dictionary<string, string> Files = [];
    public readonly List<string> Kept = [];

    public Task<string> GetPatchAsync(
        Release release,
        string platform,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellation
    ) =>
        Files.TryGetValue(release.Tag, out string? path)
            ? Task.FromResult(path)
            : throw new LauncherException($"no patch for {release.Tag}");

    public void Prune(IEnumerable<string> keep)
    {
        Kept.Clear();
        Kept.AddRange(keep);
    }
}

/// <summary>
/// A stock install and two Retooled builds of it, turned into real patch files, driven through install, upgrade,
/// rollback and uninstall.
/// </summary>
public sealed class LauncherServiceTests : IDisposable
{
    const string Platform = "windows";
    readonly string root = Path.Combine(
        Path.GetTempPath(),
        "retooled-launcher-" + Guid.NewGuid().ToString("N")
    );
    readonly FakePatches patches = new();
    readonly Dictionary<string, string> stockTree;
    readonly ReleaseCatalog catalog;

    string Install => Path.Combine(root, "ksp");
    string Stock => Path.Combine(root, "stock");

    public LauncherServiceTests()
    {
        var rng = new Random(7);
        byte[] Bytes(int n)
        {
            var b = new byte[n];
            rng.NextBytes(b);
            return b;
        }

        byte[] data = Bytes(200_000),
            part = Bytes(30_000);
        Write(Stock, "KSP_x64.exe", Bytes(3000));
        Write(Stock, "KSP_x64_Data/level0", data);
        Write(Stock, "KSP_x64_Data/old.assets", Bytes(5000));
        Write(Stock, "GameData/Squad/part.cfg", part);
        Write(Stock, "buildID64.txt", "build id = 03190\n"u8.ToArray());

        Build(
            "v1.0.0",
            (dir, edit) =>
            {
                Write(dir, "KSP_x64.exe", Bytes(3000));
                Write(dir, "KSP_x64_Data/level0", edit(data, 10));
                Write(dir, "GameData/Squad/part.cfg", part);
                Write(dir, "GameData/Retooled/v1.dll", Bytes(4000));
                Write(dir, "buildID64.txt", "build id = 03190\n"u8.ToArray());
            }
        );
        Build(
            "v1.1.0",
            (dir, edit) =>
            {
                Write(dir, "KSP_x64.exe", Bytes(3000));
                Write(dir, "KSP_x64_Data/level0", edit(data, 20));
                Write(dir, "GameData/Squad/part.cfg", edit(part, 5));
                Write(dir, "GameData/Retooled/v2.dll", Bytes(4000));
                Write(dir, "buildID64.txt", "build id = 03190\n"u8.ToArray());
            }
        );

        // a release that patches a file of a different stock install, so it cannot apply to this one
        string other = Path.Combine(root, "other-stock");
        CopyTree(Stock, other);
        byte[] otherData = Bytes(200_000);
        Write(other, "KSP_x64_Data/level0", otherData);
        BuildFrom(
            other,
            "v2.0.0",
            dir =>
            {
                var edited = (byte[])otherData.Clone();
                edited[30] ^= 0xFF;
                Write(dir, "KSP_x64_Data/level0", edited);
                Write(dir, "GameData/Retooled/v3.dll", Bytes(100));
            }
        );

        catalog = new ReleaseCatalog(
            new[] { "v1.0.0", "v1.1.0", "v2.0.0" }.Select(t =>
                ReleaseCatalogTests.Make(t, platforms: Platform)
            )
        );
        CopyTree(Stock, Install);
        Write(Install, "saves/default/persistent.sfs", Bytes(100));
        Write(Install, "GameData/SomeMod/mod.dll", Bytes(100));
        stockTree = Tree(Install);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    LauncherService Service() => new(patches, Platform);

    Release Get(string tag) => catalog.Find(tag)!;

    void Build(string tag, Action<string, Func<byte[], int, byte[]>> write) =>
        BuildFrom(
            Stock,
            tag,
            dir =>
                write(
                    dir,
                    (bytes, at) =>
                    {
                        var copy = (byte[])bytes.Clone();
                        copy[at] ^= 0xFF;
                        return copy;
                    }
                )
        );

    void BuildFrom(string stock, string tag, Action<string> write)
    {
        string dir = Path.Combine(root, "build-" + tag);
        CopyTree(stock, dir);
        write(dir);
        string patch = Path.Combine(root, tag + PatchFile.Extension);
        PatchBuilder.Build(
            new BuildOptions
            {
                Sources = [new BuildSource { Name = "steam-03190", Roots = [stock] }],
                Target = dir,
                Output = patch,
                Platform = Platform,
                Level = 3,
                Jobs = 1,
            }
        );
        patches.Files[tag] = patch;
    }

    /// <summary>The install's files without the launcher's own bookkeeping.</summary>
    Dictionary<string, string> Installed() =>
        Tree(Install).Where(p => !p.Key.StartsWith(InstallJournal.Directory + "/")).ToDictionary();

    Dictionary<string, string> Expected(string tag)
    {
        var tree = Tree(Path.Combine(root, "build-" + tag));
        // the user's own files outlive every install and uninstall
        tree["saves/default/persistent.sfs"] = stockTree["saves/default/persistent.sfs"];
        tree["GameData/SomeMod/mod.dll"] = stockTree["GameData/SomeMod/mod.dll"];
        return tree;
    }

    [Fact]
    public async Task InstallsAReleaseOntoAStockInstall()
    {
        Assert.Equal(InstallStatus.Stock, KspInstall.Inspect(Install).Status);

        var info = await Service().InstallAsync(Install, Get("v1.0.0"), catalog);

        Assert.Equal(InstallStatus.Retooled, info.Status);
        Assert.Equal("v1.0.0", info.InstalledTag);
        Assert.Equal("03190", info.BuildId);
        AssertTree(Expected("v1.0.0"), Installed());
        Assert.Equal(["v1.0.0"], patches.Kept);
    }

    [Fact]
    public async Task UpgradesFromOneReleaseToTheNext()
    {
        await Service().InstallAsync(Install, Get("v1.0.0"), catalog);

        var info = await Service().InstallAsync(Install, Get("v1.1.0"), catalog);

        Assert.Equal("v1.1.0", info.InstalledTag);
        AssertTree(Expected("v1.1.0"), Installed());
        Assert.False(File.Exists(Path.Combine(Install, "GameData/Retooled/v1.dll")));
    }

    [Fact]
    public async Task UninstallRestoresTheStockInstall()
    {
        await Service().InstallAsync(Install, Get("v1.1.0"), catalog);

        var info = await Service().UninstallAsync(Install);

        Assert.Equal(InstallStatus.Stock, info.Status);
        Assert.Null(info.InstalledTag);
        AssertTree(stockTree, Tree(Install));
        Assert.Empty(patches.Kept);
    }

    [Fact]
    public async Task AFailedUpgradePutsThePreviousReleaseBack()
    {
        await Service().InstallAsync(Install, Get("v1.0.0"), catalog);

        var error = await Assert.ThrowsAsync<LauncherException>(() =>
            Service().InstallAsync(Install, Get("v2.0.0"), catalog)
        );

        Assert.Contains("v1.0.0 was put back", error.Message);
        Assert.Equal("v1.0.0", KspInstall.Inspect(Install).InstalledTag);
        AssertTree(Expected("v1.0.0"), Installed());
    }

    [Fact]
    public async Task AFailedFirstInstallLeavesTheStockInstall()
    {
        var error = await Assert.ThrowsAsync<LauncherException>(() =>
            Service().InstallAsync(Install, Get("v2.0.0"), catalog)
        );

        Assert.Contains("stock install was restored", error.Message);
        Assert.Equal(InstallStatus.Stock, KspInstall.Inspect(Install).Status);
        AssertTree(stockTree, Tree(Install));
    }

    [Fact]
    public async Task APatchForAnotherPlatformIsRejectedBeforeAnythingChanges()
    {
        await Service().InstallAsync(Install, Get("v1.0.0"), catalog);
        var before = Tree(Install);

        await Assert.ThrowsAsync<LauncherException>(() =>
            new LauncherService(patches, "linux").InstallAsync(Install, Get("v1.1.0"), catalog)
        );

        AssertTree(before, Tree(Install));
    }

    [Fact]
    public async Task UninstallFinishesAnInterruptedInstall()
    {
        await Service().InstallAsync(Install, Get("v1.0.0"), catalog);
        var journal = InstallJournal.Load(Install)!;
        journal.State = JournalState.Committing;
        journal.Save(Install);
        Assert.Equal(InstallStatus.Interrupted, KspInstall.Inspect(Install).Status);
        await Assert.ThrowsAsync<LauncherException>(() =>
            Service().InstallAsync(Install, Get("v1.1.0"), catalog)
        );

        var info = await Service().UninstallAsync(Install);

        Assert.Equal(InstallStatus.Stock, info.Status);
        AssertTree(stockTree, Tree(Install));
    }

    [Fact]
    public void AFolderWithoutKspIsNotAnInstall()
    {
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        Assert.Equal(InstallStatus.NotKsp, KspInstall.Inspect(Path.Combine(root, "empty")).Status);
        Assert.Equal(
            InstallStatus.Missing,
            KspInstall.Inspect(Path.Combine(root, "nowhere")).Status
        );
    }

    [Fact]
    public void InspectSpellsThePathAsOnDisk()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(Path.Combine(root, "Kerbal Space Program"));
        string path = Path.Combine(root, "Kerbal Space Program");

        string inspected = KspInstall.Inspect(path.ToLowerInvariant()).Path;
        Assert.Equal(KspInstall.Inspect(path).Path, inspected);
        Assert.StartsWith(
            Path.GetPathRoot(path)!.ToUpperInvariant(),
            inspected,
            StringComparison.Ordinal
        );
        Assert.EndsWith(@"\Kerbal Space Program", inspected, StringComparison.Ordinal);
    }

    static void AssertTree(
        Dictionary<string, string> expected,
        Dictionary<string, string> actual
    ) =>
        Assert.Equal(
            expected.OrderBy(p => p.Key, StringComparer.Ordinal),
            actual.OrderBy(p => p.Key, StringComparer.Ordinal)
        );

    static void Write(string dir, string relative, byte[] bytes)
    {
        string path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    static void CopyTree(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    static Dictionary<string, string> Tree(string dir) =>
        Directory
            .EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(dir, f).Replace('\\', '/'),
                PatchApplier.Sha256Hex,
                StringComparer.Ordinal
            );
}
