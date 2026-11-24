using System.IO.Compression;
using System.Security.Cryptography;
using KSP.Retooled.Launcher.Exceptions;
using KSP.Retooled.Launcher.Installs;
using KSP.Retooled.Launcher.Installs.Saves;
using KSP.Retooled.Launcher.Releases;
using KSP.Retooled.Launcher.Tests.Mocking;
using KSP.Retooled.Launcher.Utils;
using KSP.Retooled.Patch;
using KSP.Retooled.Patch.Models.Journal;
using KSP.Retooled.Patch.Patcher;
using RetooledLauncher.Common;
using Xunit;

namespace KSP.Retooled.Launcher.Tests;

/// <summary>
/// A stock install and two Retooled builds of it, turned into real patch files, driven through install, upgrade,
/// rollback and uninstall.
/// </summary>
public sealed class LauncherServiceTests : IDisposable
{
    private const Platform PLATFORM = Platform.WINDOWS;

    private static readonly string[] SourceArray = ["v1.0.0", "v1.1.0", "v2.0.0"];
    private readonly ReleaseCatalog catalog;
    private readonly FakePatches patches = new();
    private readonly string root = Path.Combine(Path.GetTempPath(),
                                                $"retooled-launcher-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string> stockTree;

    public LauncherServiceTests()
    {
        Random rng = new(7);

        byte[] Bytes(int n)
        {
            byte[] b = new byte[n];
            rng.NextBytes(b);
            return b;
        }

        byte[] data = Bytes(200_000);
        byte[] part = Bytes(30_000);

        Write(this.Stock, "KSP_x64.exe", Bytes(3000));
        Write(this.Stock, "KSP_x64_Data/level0", data);
        Write(this.Stock, "KSP_x64_Data/old.assets", Bytes(5000));
        Write(this.Stock, "GameData/Squad/part.cfg", part);
        Write(this.Stock, "buildID64.txt", [.."build id = 03190\n"u8]);

        Build("v1.0.0", (dir, edit) =>
        {
            Write(dir, "KSP_x64.exe", Bytes(3000));
            Write(dir, "KSP_x64_Data/level0", edit(data, 10));
            Write(dir, "GameData/Squad/part.cfg", part);
            Write(dir, "GameData/Retooled/v1.dll", Bytes(4000));
            Write(dir, "buildID64.txt", [.."build id = 03190\n"u8]);
        });

        Build("v1.1.0", (dir, edit) =>
        {
            Write(dir, "KSP_x64.exe", Bytes(3000));
            Write(dir, "KSP_x64_Data/level0", edit(data, 20));
            Write(dir, "GameData/Squad/part.cfg", edit(part, 5));
            Write(dir, "GameData/Retooled/v2.dll", Bytes(4000));
            Write(dir, "buildID64.txt", [.."build id = 03190\n"u8]);
        });

        // a release that patches a file of a different stock install, so it cannot apply to this one
        string other = Path.Combine(this.root, "other-stock");
        CopyTree(this.Stock, other);
        byte[] otherData = Bytes(200_000);
        Write(other, "KSP_x64_Data/level0", otherData);
        BuildFrom(other, "v2.0.0", dir =>
        {
            byte[] edited = (byte[])otherData.Clone();
            edited[30] ^= 0xFF;
            Write(dir, "KSP_x64_Data/level0", edited);
            Write(dir, "GameData/Retooled/v3.dll", Bytes(100));
        });

        this.catalog = new ReleaseCatalog(SourceArray.Select(t => ReleaseCatalogTests.Make(t, platforms: PLATFORM)));

        CopyTree(this.Stock, this.Install);
        Write(this.Install, "saves/default/persistent.sfs", Bytes(100));
        Write(this.Install, "GameData/SomeMod/mod.dll", Bytes(100));
        this.stockTree = Tree(this.Install);
    }

    private string Install => Path.Combine(this.root, "ksp");
    private string Stock => Path.Combine(this.root, "stock");

    private string BackupFolder => Path.Combine(this.Install, SavesBackup.BACKUP_FOLDER);

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, true);
        }
    }

    private LauncherService Service() => new(this.patches, PLATFORM);

    private Release Get(string tag) => this.catalog.Find(tag)!;

    private void Build(string tag, Action<string, Func<byte[], int, byte[]>> write) =>
        BuildFrom(this.Stock, tag, dir =>
        write(dir, (bytes, at) =>
        {
            byte[] copy = (byte[])bytes.Clone();
            copy[at] ^= 0xFF;
            return copy;
        }));

    private void BuildFrom(string stock, string tag, Action<string> write)
    {
        string dir = Path.Combine(this.root, "build-" + tag);
        CopyTree(stock, dir);
        write(dir);
        string patch = Path.Combine(this.root, tag + PatchFile.EXTENSION);
        PatchBuilder.Build(new BuildOptions
        {
            Sources  = [new BuildSource { Name = "steam-03190", Roots = [stock] }],
            Target   = dir,
            Output   = patch,
            Platform = PLATFORM,
            Level    = 3,
            Jobs     = 1
        });

        this.patches.files[tag] = patch;
    }

    /// <summary>
    /// The install's files without the launcher's own bookkeeping and saves backups.
    /// </summary>
    private Dictionary<string, string> Installed() => Tree(this.Install).Where(p => !p.Key.StartsWith(InstallJournal.DIRECTORY + "/")
                                                                                 && !p.Key.StartsWith(SavesBackup.BACKUP_FOLDER + "/"))
                                                                        .ToDictionary();

    /// <summary>
    /// The names of every file in the backup folder, in order.
    /// </summary>
    private string[] Backups() => !Directory.Exists(this.BackupFolder) ? [] :
    [
        ..Directory
         .EnumerateFiles(this.BackupFolder)
         .Select(Path.GetFileName)
         .Order(StringComparer.Ordinal)!
    ];

    private static SavesBackupRecord Record(string version, DateTime created, bool stock = false) => new()
    {
        Version = version,
        Stock   = stock,
        Created = created
    };

    [Fact]
    public async Task InstallingZipsTheSaves()
    {
        await Service().InstallAsync(this.Install, Get("v1.0.0"), this.catalog);

        string backup = Assert.Single(Backups());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}\.zip$", backup);
        await using ZipArchive zip = await ZipFile.OpenReadAsync(Path.Combine(this.BackupFolder, backup));
        using MemoryStream saved = new();
        await using (Stream entry = await zip.GetEntry("saves/default/persistent.sfs")!.OpenAsync())
        {
            await entry.CopyToAsync(saved);
        }

        Assert.Equal(this.stockTree["saves/default/persistent.sfs"], Convert.ToHexStringLower(SHA256.HashData(saved.ToArray())));
    }

    [Fact]
    public async Task ABackupRecordsTheVersionInstalledBeforeTheUpgrade()
    {
        await Service().InstallAsync(this.Install, Get("v1.0.0"), this.catalog);
        await Service().InstallAsync(this.Install, Get("v1.1.0"), this.catalog);

        IReadOnlyList<SavesBackupInfo> backups = SavesBackup.List(this.Install);
        Assert.Equal(2, backups.Count);
        Assert.Equal("v1.1.0", KspInstall.Inspect(this.Install).InstalledTag);

        Assert.Equal("v1.0.0", backups[0].Record.Version);
        Assert.False(backups[0].Record.Stock);
        Assert.Equal(SavesBackup.STOCK_VERSION, backups[1].Record.Version);
        Assert.True(backups[1].Record.Stock);
    }

    [Fact]
    public async Task EachInstallOnTheSameDayGetsItsOwnBackup()
    {
        await Service().InstallAsync(this.Install, Get("v1.0.0"), this.catalog);
        await Service().InstallAsync(this.Install, Get("v1.1.0"), this.catalog);

        string[] backups = [..SavesBackup.List(this.Install).Select(b => Path.GetFileName(b.Path))];
        Assert.Equal(2, backups.Length);
        Assert.Equal(Path.GetFileNameWithoutExtension(backups[1]) + " (2).zip", backups[0]);
    }

    [Fact]
    public void ABackupIsNamedAfterTheDateAndNumberedWhenTheDateIsTaken()
    {
        DateTime date = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Local);

        string? first = SavesBackup.Create(this.Install, Record("v1.0.0", date));
        string? second = SavesBackup.Create(this.Install, Record("v1.0.0", date));
        string? third = SavesBackup.Create(this.Install, Record("v1.0.0", date));

        Assert.Equal("2026-11-13.zip", Path.GetFileName(first));
        Assert.Equal("2026-11-13 (2).zip", Path.GetFileName(second));
        Assert.Equal("2026-11-13 (3).zip", Path.GetFileName(third));
    }

    [Fact]
    public void PruneKeepsTheNewestBackupsAndTheNewestStockOne()
    {
        DateTime start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        SavesBackup.Create(this.Install, Record("1.12.5", start, true));
        string newestStock = SavesBackup.Create(this.Install, Record("1.12.5", start.AddDays(1), true))!;
        for (int i = 0; i < SavesBackup.KEEP; i++)
        {
            SavesBackup.Create(this.Install, Record($"v1.{i}.0", start.AddDays(2 + i)));
        }

        File.WriteAllText(Path.Combine(this.BackupFolder, "mine.zip"), "not a backup");

        IReadOnlyList<string> deleted = SavesBackup.Prune(this.Install);

        Assert.Single(deleted);
        IReadOnlyList<SavesBackupInfo> kept = SavesBackup.List(this.Install);
        Assert.Equal(SavesBackup.KEEP + 1, kept.Count);
        Assert.Equal(newestStock, kept[^1].Path);
        Assert.True(File.Exists(Path.Combine(this.BackupFolder, "mine.zip")));
    }

    [Fact]
    public void PruneKeepsOnlyTheNewestWhenTheyIncludeAStockBackup()
    {
        DateTime start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < SavesBackup.KEEP + 3; i++)
        {
            SavesBackup.Create(this.Install, Record($"v1.{i}.0", start.AddDays(i), i == SavesBackup.KEEP + 2));
        }

        SavesBackup.Prune(this.Install);

        IReadOnlyList<SavesBackupInfo> kept = SavesBackup.List(this.Install);
        Assert.Equal(SavesBackup.KEEP, kept.Count);
        Assert.True(kept[0].Record.Stock);
        Assert.Equal("v1.3.0", kept[^1].Record.Version);
    }

    [Fact]
    public void NoSavesMeansNoBackup()
    {
        Directory.Delete(Path.Combine(this.Install, SavesBackup.SAVES_FOLDER), true);

        Assert.Null(SavesBackup.Create(this.Install, Record("v1.0.0", DateTime.UtcNow)));
        Assert.Empty(Backups());
    }

    [Fact]
    public async Task ACancelledBackupLeavesNoBackupAndInstallsNothing()
    {
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().InstallAsync(this.Install, Get("v1.0.0"), this.catalog, cancellation: cancel.Token));

        Assert.Equal(InstallStatus.STOCK, KspInstall.Inspect(this.Install).Status);
        Assert.Empty(Backups());
    }

    private Dictionary<string, string> Expected(string tag)
    {
        Dictionary<string, string> tree = Tree(Path.Combine(this.root, "build-" + tag));
        // the user's own files outlive every install and uninstall
        tree["saves/default/persistent.sfs"] = this.stockTree["saves/default/persistent.sfs"];
        tree["GameData/SomeMod/mod.dll"]     = this.stockTree["GameData/SomeMod/mod.dll"];
        return tree;
    }

    [Fact]
    public async Task InstallsAReleaseOntoAStockInstall()
    {
        Assert.Equal(InstallStatus.STOCK, KspInstall.Inspect(this.Install).Status);

        InstallInfo info = await Service().InstallAsync(this.Install, Get("v1.0.0"), this.catalog);

        Assert.Equal(InstallStatus.RETOOLED, info.Status);
        Assert.Equal("v1.0.0", info.InstalledTag);
        Assert.Equal("03190", info.BuildId);
        AssertTree(Expected("v1.0.0"), Installed());
        Assert.Equal([this.patches.files["v1.0.0"]], this.patches.kept);
    }

    [Fact]
    public async Task RecordsTheDigestOfTheInstalledPatch()
    {
        Release release = Get("v1.0.0");
        release = release with { Assets = [..release.Assets.Select(a => a with { Sha256 = "ab12" })] };

        InstallInfo info = await Service().InstallAsync(this.Install, release, this.catalog);

        Assert.Equal("ab12", info.Release?.Sha256);
    }

    [Fact]
    public async Task LogsTheVersionBeingInstalled()
    {
        StringWriter log = new();

        await new LauncherService(this.patches, PLATFORM, log).InstallAsync(this.Install, Get("v1.0.0"), this.catalog);
        Assert.Contains("installing KSP Retooled v1.0.0", log.ToString());
    }

    [Fact]
    public async Task LogsTheVersionEvenIfItsPatchCannotBeFetched()
    {
        StringWriter log = new();
        this.patches.files.Remove("v1.0.0");

        await Assert.ThrowsAsync<LauncherException>(() => new LauncherService(this.patches, PLATFORM, log).InstallAsync(this.Install, Get("v1.0.0"), this.catalog));

        Assert.Contains("installing KSP Retooled v1.0.0", log.ToString());
    }

    [Fact]
    public async Task LogsThePatchFileBeingInstalled()
    {
        StringWriter log = new();

        await new LauncherService(this.patches, PLATFORM, log).InstallFileAsync(this.Install, this.patches.files["v1.0.0"], this.catalog);

        Assert.Contains("installing KSP Retooled from v1.0.0.rtpatch", log.ToString());
    }

    [Fact]
    public async Task UpgradesFromOneReleaseToTheNext()
    {
        await Service().InstallAsync(this.Install, Get("v1.0.0"), this.catalog);

        InstallInfo info = await Service().InstallAsync(this.Install, Get("v1.1.0"), this.catalog);

        Assert.Equal("v1.1.0", info.InstalledTag);
        AssertTree(Expected("v1.1.0"), Installed());
        Assert.False(File.Exists(Path.Combine(this.Install, "GameData/Retooled/v1.dll")));
    }

    [Fact]
    public async Task UninstallRestoresTheStockInstall()
    {
        await Service().InstallAsync(this.Install, Get("v1.1.0"), this.catalog);

        InstallInfo info = await Service().UninstallAsync(this.Install);

        Assert.Equal(InstallStatus.STOCK, info.Status);
        Assert.Null(info.InstalledTag);
        AssertTree(this.stockTree, Installed());
        Assert.Empty(this.patches.kept);
    }

    [Fact]
    public async Task AFailedUpgradePutsThePreviousReleaseBack()
    {
        await Service().InstallAsync(this.Install, Get("v1.0.0"), this.catalog);

        LauncherException error = await Assert.ThrowsAsync<LauncherException>(() => Service().InstallAsync(this.Install, Get("v2.0.0"), this.catalog));

        Assert.Contains("v1.0.0 was put back", error.Message);
        Assert.Equal("v1.0.0", KspInstall.Inspect(this.Install).InstalledTag);
        AssertTree(Expected("v1.0.0"), Installed());
    }

    [Fact]
    public async Task AFailedFirstInstallLeavesTheStockInstall()
    {
        LauncherException error = await Assert.ThrowsAsync<LauncherException>(() => Service().InstallAsync(this.Install, Get("v2.0.0"), this.catalog));

        Assert.Contains("stock install was restored", error.Message);
        Assert.Equal(InstallStatus.STOCK, KspInstall.Inspect(this.Install).Status);
        AssertTree(this.stockTree, Installed());
    }

    [Fact]
    public async Task APatchForAnotherPlatformIsRejectedBeforeAnythingChanges()
    {
        await Service().InstallAsync(this.Install, Get("v1.0.0"), this.catalog);
        Dictionary<string, string> before = Tree(this.Install);

        await Assert.ThrowsAsync<LauncherException>(() => new LauncherService(this.patches, Platform.LINUX).InstallAsync(this.Install, Get("v1.1.0"), this.catalog));

        AssertTree(before, Tree(this.Install));
    }

    [Fact]
    public async Task InstallsALocalPatchFile()
    {
        InstallInfo info = await Service().InstallFileAsync(this.Install, this.patches.files["v1.0.0"], this.catalog);

        Assert.Equal(InstallStatus.RETOOLED, info.Status);
        Assert.Equal("v1.0.0.rtpatch", info.InstalledTag);
        Assert.Equal(LauncherInfo.LOCAL_CHANNEL, info.Release?.Channel);
        AssertTree(Expected("v1.0.0"), Installed());

        Assert.Equal(InstallStatus.STOCK, (await Service().UninstallAsync(this.Install)).Status);
        AssertTree(this.stockTree, Installed());
    }

    [Fact]
    public async Task ALocalFileThatIsNotAPatchIsRejectedBeforeAnythingChanges()
    {
        await Service().InstallAsync(this.Install, Get("v1.0.0"), this.catalog);
        Dictionary<string, string> before = Tree(this.Install);
        string file = Path.Combine(this.root, "garbage.rtpatch");
        await File.WriteAllBytesAsync(file, new byte[100]);

        await Assert.ThrowsAsync<LauncherException>(() => Service().InstallFileAsync(this.Install, file, this.catalog));

        AssertTree(before, Tree(this.Install));
    }

    [Fact]
    public async Task AFailedInstallOfALocalFilePutsTheReleaseBack()
    {
        await Service().InstallAsync(this.Install, Get("v1.0.0"), this.catalog);

        LauncherException error = await Assert.ThrowsAsync<LauncherException>(() => Service().InstallFileAsync(this.Install, this.patches.files["v2.0.0"], this.catalog));

        Assert.Contains("v1.0.0 was put back", error.Message);
        Assert.Equal("v1.0.0", KspInstall.Inspect(this.Install).InstalledTag);
        AssertTree(Expected("v1.0.0"), Installed());
    }

    [Fact]
    public async Task AFailedUpgradePutsALocalFileBack()
    {
        await Service().InstallFileAsync(this.Install, this.patches.files["v1.0.0"], this.catalog);

        LauncherException error = await Assert.ThrowsAsync<LauncherException>(() => Service().InstallAsync(this.Install, Get("v2.0.0"), this.catalog));

        Assert.Contains("v1.0.0.rtpatch was put back", error.Message);
        Assert.Equal(LauncherInfo.LOCAL_CHANNEL, KspInstall.Inspect(this.Install).Release?.Channel);
        AssertTree(Expected("v1.0.0"), Installed());
    }

    [Fact]
    public async Task AFailedUpgradePutsBackAReleaseOfAnotherChannel()
    {
        await Service()
           .InstallAsync(this.Install, Get("v1.0.0") with { Channel = "other/repo" }, this.catalog);

        LauncherException error = await Assert.ThrowsAsync<LauncherException>(() => Service().InstallAsync(this.Install, Get("v2.0.0"), ReleaseCatalog.Empty));

        Assert.Contains("v1.0.0 was put back", error.Message);
        Assert.Equal("other/repo", KspInstall.Inspect(this.Install).Release?.Channel);
        AssertTree(Expected("v1.0.0"), Installed());
    }

    [Fact]
    public async Task RollbackIsNotFooledByTheSameTagInAnotherChannel()
    {
        await Service()
           .InstallAsync(this.Install, Get("v1.0.0") with { Channel = "other/repo" }, this.catalog);

        File.Delete(this.patches.files["v1.0.0"]); // the cached patch is gone, so only the catalog could help

        LauncherException error = await Assert.ThrowsAsync<LauncherException>(() => Service().InstallAsync(this.Install, Get("v2.0.0"), this.catalog));

        Assert.Contains("stock install was restored", error.Message);
        Assert.Equal(InstallStatus.STOCK, KspInstall.Inspect(this.Install).Status);
    }

    [Fact]
    public async Task RollbackFallsBackToTheCatalogForAMarkerWithoutAPatch()
    {
        await Service().InstallAsync(this.Install, Get("v1.0.0"), this.catalog);
        InstalledRelease marker = KspInstall.ReadMarker(this.Install)!;
        marker.Channel = null;
        marker.Patch   = null; // as an older launcher wrote it
        KspInstall.WriteMarker(this.Install, marker);

        LauncherException error = await Assert.ThrowsAsync<LauncherException>(() => Service().InstallAsync(this.Install, Get("v2.0.0"), this.catalog));

        Assert.Contains("v1.0.0 was put back", error.Message);
        AssertTree(Expected("v1.0.0"), Installed());
    }

    [Fact]
    public async Task AReleaseOfAnotherChannelHasNoTagComparableWithThisOne()
    {
        InstallInfo info = await Service()
                              .InstallAsync(this.Install, Get("v1.0.0") with { Channel = "a/b" }, this.catalog);

        Assert.Equal("v1.0.0", info.InstalledTagIn("A/B"));
        Assert.Equal(string.Empty, info.InstalledTagIn("c/d"));
        Assert.Null(KspInstall.Inspect(this.Stock).InstalledTagIn("c/d"));
    }

    [Fact]
    public async Task UninstallFinishesAnInterruptedInstall()
    {
        await Service().InstallAsync(this.Install, Get("v1.0.0"), this.catalog);
        InstallJournal journal = InstallJournal.Load(this.Install)!;
        journal.State = JournalState.COMMITTING;
        journal.Save(this.Install);
        Assert.Equal(InstallStatus.INTERRUPTED, KspInstall.Inspect(this.Install).Status);
        await Assert.ThrowsAsync<LauncherException>(() => Service().InstallAsync(this.Install, Get("v1.1.0"), this.catalog));

        InstallInfo info = await Service().UninstallAsync(this.Install);

        Assert.Equal(InstallStatus.STOCK, info.Status);
        AssertTree(this.stockTree, Installed());
    }

    [Fact]
    public void AFolderWithoutKspIsNotAnInstall()
    {
        Directory.CreateDirectory(Path.Combine(this.root, "empty"));
        Assert.Equal(InstallStatus.NOT_KSP, KspInstall.Inspect(Path.Combine(this.root, "empty")).Status);
        Assert.Equal(InstallStatus.MISSING, KspInstall.Inspect(Path.Combine(this.root, "nowhere")).Status);
    }

    [Fact]
    public void OnLinuxAWindowsInstallGetsTheWindowsPatch()
    {
        InstallInfo info = KspInstall.Inspect(this.Stock, Platform.LINUX);

        Assert.Equal(Platform.WINDOWS, info.Platform);
        Assert.Equal(Platform.WINDOWS, KspInstall.PatchPlatform(info, Platform.LINUX));
    }

    [Fact]
    public void WhenAFolderHasBothExecutablesTheHostPlatformIsUsed()
    {
        Write(this.Stock, "KSP.x86_64", [1]);

        Assert.Equal(Platform.LINUX, KspInstall.PatchPlatform(KspInstall.Inspect(this.Stock, Platform.LINUX), Platform.LINUX));
        Assert.Equal(Platform.WINDOWS, KspInstall.PatchPlatform(KspInstall.Inspect(this.Stock, Platform.WINDOWS), Platform.WINDOWS));
    }

    [Fact]
    public void OnMacAWindowsInstallGetsTheWindowsPatch()
    {
        InstallInfo info = KspInstall.Inspect(this.Stock, Platform.MACOS);

        Assert.Equal(Platform.WINDOWS, info.Platform);
        Assert.Equal(Platform.WINDOWS, KspInstall.PatchPlatform(info, Platform.MACOS));
    }

    [Fact]
    public void OnWindowsALinuxInstallGetsTheLinuxPatch()
    {
        File.Move(Path.Combine(this.Stock, "KSP_x64.exe"), Path.Combine(this.Stock, "KSP.x86_64"));
        InstallInfo info = KspInstall.Inspect(this.Stock, Platform.WINDOWS);

        Assert.Equal(InstallStatus.STOCK, info.Status);
        Assert.Equal(Platform.LINUX, KspInstall.PatchPlatform(info, Platform.WINDOWS));
    }

    [Fact]
    public void OnLinuxAMacInstallGetsTheMacPatch()
    {
        File.Delete(Path.Combine(this.Stock, "KSP_x64.exe"));
        Directory.CreateDirectory(Path.Combine(this.Stock, "KSP.app"));

        Assert.Equal(Platform.MACOS, KspInstall.PatchPlatform(KspInstall.Inspect(this.Stock, Platform.LINUX), Platform.LINUX));
    }

    [Fact]
    public async Task UpgradesUseThePlatformOfTheInstalledPatch()
    {
        await Service().InstallAsync(this.Install, Get("v1.0.0"), this.catalog);

        InstallInfo info = KspInstall.Inspect(this.Install, Platform.LINUX);
        Assert.Equal(PLATFORM, info.Platform);
        Assert.Equal(PLATFORM, KspInstall.PatchPlatform(info, Platform.LINUX));
    }

    [Fact]
    public void InspectSpellsThePathAsOnDisk()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(this.root, "Kerbal Space Program"));
        string path = Path.Combine(this.root, "Kerbal Space Program");

        string inspected = KspInstall.Inspect(path.ToLowerInvariant()).Path;
        Assert.Equal(KspInstall.Inspect(path).Path, inspected);
        Assert.StartsWith(Path.GetPathRoot(path)!.ToUpperInvariant(),
                          inspected,
                          StringComparison.Ordinal);

        Assert.EndsWith(@"\Kerbal Space Program", inspected, StringComparison.Ordinal);
    }

    private static void AssertTree(Dictionary<string, string> expected,
                                   Dictionary<string, string> actual) => Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal),
                                                                                      actual.OrderBy(p => p.Key, StringComparer.Ordinal));

    private static void Write(string dir, string relative, byte[] bytes)
    {
        string path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static Dictionary<string, string> Tree(string dir) => Directory
                                                                 .EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                                                                 .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'),
                                                                               PatchApplier.Sha256Hex,
                                                                               StringComparer.Ordinal);
}
