using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;

namespace KSP.Retooled.Patch.Build;

/// <summary>
/// A stock install the patch must apply to; later <see cref="Roots"/> overlay earlier ones (e.g. the game and DLC
/// depots).
/// </summary>
public sealed class BuildSource
{
    /// <summary>
    /// Defaults to <c>&lt;distribution&gt;-&lt;build id&gt;</c> from the install's buildID file, else the directory name.
    /// </summary>
    public string? Name { get; init; }

    public required IReadOnlyList<string> Roots { get; init; }
}

public sealed class BuildOptions
{
    public required IReadOnlyList<BuildSource> Sources { get; init; }

    /// <summary>
    /// The retooled release build every source is patched into.
    /// </summary>
    public required string Target { get; init; }

    /// <summary>
    /// The patch file to write.
    /// </summary>
    public required string Output { get; init; }

    /// <summary>
    /// <c>windows</c>, <c>linux</c> or <c>macos</c>; detected from the target's player when null.
    /// </summary>
    public string? Platform { get; init; }

    public int Jobs { get; init; } = PatchBuilder.DefaultJobs;

    public int Level { get; init; } = 19;

    public int Window { get; init; } = 27;

    /// <summary>
    /// Install-relative patterns ('*' spans directories) left out of every tree, on top of the defaults.
    /// </summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>
    /// Patterns left out of the target only.
    /// </summary>
    public IReadOnlyList<string> TargetExcludes { get; init; } = [];

    /// <summary>
    /// Install-relative files and directories left out of every tree, which a side-by-side apply copies from the install.
    /// </summary>
    public IReadOnlyList<string> CopiedPaths { get; init; } = [];

    public TextWriter Log { get; init; } = TextWriter.Null;
}

public sealed record BuildResult(PatchManifest Manifest, string Report);

/// <summary>
/// Builds one patch file that turns any of several stock installs into the same retooled build (thread #241). The
/// first source is planned in full: an output is either identical to a stock file or a sequence of runs, copies of
/// identical stock bytes found anywhere in scope (<see cref="CopyMatcher"/>) and, for the gaps between them, bsdiff
/// against the stock file at the same path or a literal, whichever compresses smaller. Each further source reuses
/// every recipe whose stock files it has with the same content, and only the remaining outputs are planned again
/// against its files, into a section of their own.
/// </summary>
public static partial class PatchBuilder
{
    /// <summary>
    /// Target window diffed against one reference window (at most 1.5× this), sharing one suffix sort.
    /// </summary>
    private const int CHUNK_SIZE = 64 << 20;
    /// <summary>
    /// What a worker can hold at its largest: a reference window, its suffix array and the sort's symbols.
    /// </summary>
    private const long JOB_MEMORY = 1L << 30;

    /// <summary>
    /// Gaps shorter than this, or windows with fewer gap bytes than <see cref="MIN_DIFF_WINDOW"/>, stay literal.
    /// </summary>
    private const int MIN_DIFF_GAP = 4 << 10;
    private const int MIN_DIFF_WINDOW = 256 << 10;
    /// <summary>
    /// Never part of a stock base or a shipped build: user data, logs, Unity's backup folders, the part icon / craft
    /// caches KSP writes at runtime (absent from a fresh install, different in every played one), the folder metadata
    /// Finder and Explorer write, and a patched install's journal. Debug symbols are shipped when the build has them:
    /// the build decides which ones a patch carries.
    /// </summary>
    private static readonly string[] DefaultExcludes =
    [
        "saves/*",
        "saves-backup/*",
        "Logs/*",
        "Screenshots/*",
        "thumbs/*",
        "@thumbs/*",
        "*/@thumbs/*",
        "*.loadmeta",
        "CKAN/*",
        "Ships-Old/*",
        "Notes/*",
        "PluginData/*",
        "settings.cfg",
        "*.log",
        "dataLog.*",
        "rt_dump.txt",
        ".DS_Store",
        "*/.DS_Store",
        "Thumbs.db",
        "*/Thumbs.db",
        "desktop.ini",
        "*/desktop.ini",
        "*_BackUpThisFolder_ButDontShipItWithYourGame/*",
        $"{InstallJournal.DIRECTORY}/*"
    ];

    /// <summary>
    /// Stock content under an excluded folder: the scenario and training saves.
    /// </summary>
    private static readonly string[] StockSaves = ["saves/scenarios/*", "saves/training/*"];

    /// <summary>
    /// DLC components first so their prefixes win; core takes everything else.
    /// </summary>
    private static readonly (string Name, string? Requires)[] Components =
    [
        ("makinghistory", "GameData/SquadExpansion/MakingHistory"),
        ("serenity", "GameData/SquadExpansion/Serenity"),
        ("core", null)
    ];

    /// <summary>
    /// One job per logical core, unless that would need more memory than this machine or container has.
    /// </summary>
    internal static int DefaultJobs => (int)Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / JOB_MEMORY,
                                                       1, Environment.ProcessorCount);

    public static BuildResult Build(BuildOptions o)
    {
        if (o.Sources.Count is 0) throw new PatchException("no sources");

        TextWriter log = o.Log;
        string targetPath = Path.GetFullPath(o.Target), outPath = Path.GetFullPath(o.Output);
        List<string> copied = [..o.CopiedPaths.Select(p => p.Replace('\\', '/').TrimEnd('/'))];
        if (copied.FirstOrDefault(p => p.IndexOfAny(['*', '?']) >= 0) is { } pattern)
        {
            throw new PatchException($"{pattern} is a pattern: an apply copies exact paths");
        }

        string[] excludes = [..DefaultExcludes, ..o.Excludes, ..copied, ..copied.Select(p => p + "/*")];
        string[] targetExcludes = [..excludes, ..o.TargetExcludes];

        Stopwatch clock = Stopwatch.StartNew();
        List<string> emptyDirectories = [];
        List<FileEntry> targetFiles = Walk(targetPath, targetExcludes, emptyDirectories);
        string platform = o.Platform ?? DetectPlatform(targetPath);
        List<SourceInstall> sources = LoadSources(o.Sources, excludes, log);
        foreach (SourceInstall source in sources) CheckLayout(source, targetFiles, targetPath);
        log.WriteLine($"target {targetFiles.Count} files ({platform}); sources {string.Join(", ", sources.Select(s => $"{s.Id} ({s.Files.Count} files)"))}");

        Parallel.ForEach(sources.SelectMany(s => s.Files).Concat(targetFiles),
                         new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                         f => _ = f.Hash);

        log.WriteLine($"hashed in {clock.Elapsed.TotalSeconds:F0}s");

        PatchManifest manifest = new() { Platform = platform, CopiedPaths = copied };
        Dictionary<(string, string), int> sourceIndex = new();

        int SourceIndex(FileEntry stock)
        {
            if (sourceIndex.TryGetValue((stock.relative, stock.Hash), out int index)) return index;

            sourceIndex[(stock.relative, stock.Hash)] = index = manifest.Sources.Count;
            manifest.Sources.Add(new SourceFile { Path = stock.relative, Size = stock.size, Sha256 = stock.Hash });
            return index;
        }

        foreach (SourceInstall source in sources)
        {
            manifest.Variants.Add(new Variant { Id = source.Id, Files = [..source.Files.Select(SourceIndex).Order()] });
        }

        List<FileEntry> ordered = [..targetFiles.OrderBy(t => t.component).ThenBy(t => t.relative, StringComparer.OrdinalIgnoreCase)];
        Dictionary<FileEntry, List<PlannedRecipe>> recipes = ordered.ToDictionary(t => t, _ => new List<PlannedRecipe>());

        string spool = outPath + ".spool";
        if (Directory.Exists(spool))
        {
            Directory.Delete(spool, true);
        }

        Directory.CreateDirectory(spool);
        StringBuilder report = new();
        for (int v = 0; v < sources.Count; v++)
        {
            SourceInstall source = sources[v];
            HashSet<int> has = [..manifest.Variants[v].Files];
            List<FileEntry> pending = [..ordered.Where(t => !recipes[t].Any(r => r.Recipe.Inputs().All(has.Contains)))];
            log.WriteLine($"{source.Id}: {ordered.Count - pending.Count} outputs reuse earlier recipes, {pending.Count} to plan");
            if (pending.Count is 0) continue;

            List<PlannedOutput> plans = PlanSource(source.Files, pending, spool, o.Jobs, log, clock);
            foreach (PlannedOutput plan in plans)
            {
                string section = Components[plan.target.component].Name + (v is 0 ? string.Empty : "@" + source.Id);
                recipes[plan.target].Add(new PlannedRecipe(ToRecipe(plan, SourceIndex), plan, section));
            }

            Report(report, source.Id, plans);
        }

        foreach (FileEntry target in ordered)
        {
            manifest.Outputs.Add(new OutputFile
            {
                Path       = target.relative, Size         = target.size, Sha256 = target.Hash, Component = target.component,
                Executable = IsExecutable(target), Recipes = [..recipes[target].Select(r => r.Recipe)]
            });
        }

        manifest.Components =
        [
            ..Components.Select((c, i) => new Component
            {
                Name        = c.Name,
                Requires    = c.Requires,
                Remove      = RemovedStockFiles(sources, targetFiles, i),
                Directories = [..emptyDirectories.Where(d => ComponentOf(d + "/") == i)]
            })
        ];

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        WriteFile(outPath, manifest, [..ordered.SelectMany(t => recipes[t])], o, report);
        Directory.Delete(spool, true);
        log.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length / 1048576.0:F1} MB) at {clock.Elapsed.TotalSeconds:F0}s");
        return new BuildResult(manifest, report.ToString());
    }

    private static string DetectPlatform(string target)
    {
        if (File.Exists(Path.Combine(target, "KSP_x64.exe"))) return "windows";
        if (File.Exists(Path.Combine(target, "KSP.x86_64"))) return "linux";
        if (Directory.Exists(Path.Combine(target, "KSP.app"))) return "macos";

        throw new PatchException($"{target} has no KSP player (KSP_x64.exe, KSP.x86_64 or KSP.app); pass the platform explicitly");
    }

    private static List<SourceInstall> LoadSources(IReadOnlyList<BuildSource> sources, string[] excludes, TextWriter log)
    {
        List<SourceInstall> loaded = [];
        foreach (BuildSource source in sources)
        {
            if (source.Roots.Count is 0) throw new PatchException("a source has no directories");

            Dictionary<string, FileEntry> files = new(StringComparer.OrdinalIgnoreCase);
            foreach (string root in source.Roots)
            {
                foreach (FileEntry file in Walk(Path.GetFullPath(root), excludes))
                {
                    files[file.relative] = file;
                }
            }

            string name = source.Name ?? BuildId(source.Roots) ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Roots[0])));
            string id = name;
            for (int n = 2; loaded.Any(l => l.Id == id); n++)
            {
                id = $"{name}-{n}";
            }

            loaded.Add(new SourceInstall(id, [..files.Values.OrderBy(f => f.relative, StringComparer.OrdinalIgnoreCase)]));
        }

        return loaded;
    }

    /// <summary>
    /// <c>steam-03190</c> from buildID64.txt / buildID.txt (<c>build id = 03190</c>, <c>distribution name = Steam</c>).
    /// </summary>
    private static string? BuildId(IEnumerable<string> roots)
    {
        foreach (string root in roots)
        {
            foreach (string name in BuildIdFile.Names)
            {
                string path = Path.Combine(root, name);
                if (!File.Exists(path)) continue;

                string? build = BuildIdFile.Value(path, "build id");
                if (build is null) continue;

                string distribution = (BuildIdFile.Value(path, "distribution name") ?? "ksp").ToLowerInvariant().Replace(' ', '-');
                return $"{distribution}-{build}";
            }
        }

        return null;
    }

    private static bool Excluded(string relative, string[] excludes) => excludes.Any(e => FileSystemName.MatchesSimpleExpression(e, relative))
                                                                     && !StockSaves.Any(e => FileSystemName.MatchesSimpleExpression(e, relative));

    /// <summary>
    /// The files under <paramref name="root"/>; <paramref name="emptyDirectories"/> collects the directories with no
    /// entries.
    /// </summary>
    private static List<FileEntry> Walk(string root, string[] excludes, List<string>? emptyDirectories = null)
    {
        if (!Directory.Exists(root)) throw new PatchException($"{root} does not exist");

        List<FileEntry> files = [];
        Stack<string> pending = new([root]);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            if (emptyDirectories is not null && directory != root && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                string relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
                if (!Excluded(relative + "/", excludes))
                {
                    emptyDirectories.Add(relative);
                }
            }

            foreach (string sub in Directory.EnumerateDirectories(directory))
            {
                pending.Push(sub);
            }

            foreach (string full in Directory.EnumerateFiles(directory))
            {
                string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                if (Excluded(relative, excludes)) continue;

                // dev GameData is largely symlinks into a stock install; a link's own FileInfo has the link's length
                FileInfo info = new(full);
                long size = info.LinkTarget is null ? info.Length : ((FileInfo)info.ResolveLinkTarget(true)!).Length;
                files.Add(new FileEntry(relative, full, size, ComponentOf(relative)));
            }
        }

        return files;
    }

    private static int ComponentOf(string relative)
    {
        for (int c = 0; c < Components.Length; c++)
        {
            if (Components[c].Requires is not { } prefix || relative.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return c;
        }

        throw new UnreachableException();
    }

    private static bool IsCore(FileEntry file) => Components[file.component].Requires is null;

    private static bool InScope(FileEntry stock, FileEntry target) => stock.component == target.component || IsCore(stock);

    private static string TopLevel(FileEntry file) => file.relative.Split('/')[0];

    /// <summary>
    /// An in-place apply removes every stock file the build does not ship, so the build must be a release with stock's
    /// layout: a build missing a whole stock top-level entry (GameData, Ships, the player) would delete it. A CoreCLR
    /// release alone has no MonoBleedingEdge, and there the apply should delete stock's: a CoreCLR player does not use it,
    /// and Special K, finding it, crashes the game (Builder.StockMonoRuntime).
    /// </summary>
    private static void CheckLayout(SourceInstall source, List<FileEntry> targetFiles, string targetPath)
    {
        HashSet<string> shipped = targetFiles.Select(TopLevel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool coreClr = shipped.Contains("CoreCLR");
        List<string> missing =
        [
            ..source.Files.Where(IsCore).Select(TopLevel).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(n => !shipped.Contains(n) && !(coreClr && n.Equals("MonoBleedingEdge", StringComparison.OrdinalIgnoreCase)))
                    .Order(StringComparer.OrdinalIgnoreCase)
        ];

        if (missing.Count > 0)
        {
            throw new PatchException($"{targetPath} has none of {source.Id}'s {string.Join(", ", missing)}: patch a release build (Builder.Build -retooledRelease), which has stock's layout");
        }
    }

    /// <summary>
    /// ELF and Mach-O binaries and scripts, which need the executable mode on Linux and macOS.
    /// </summary>
    private static bool IsExecutable(FileEntry file)
    {
        if (file.size < 4) return false;

        Span<byte> magic = stackalloc byte[4];
        using (FileStream stream = File.OpenRead(file.full)) stream.ReadExactly(magic);
        uint word = BinaryPrimitives.ReadUInt32BigEndian(magic);
        return word is 0x7F454C46 or 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE
            || (magic[0] == '#' && magic[1] == '!');
    }

    /// <summary>
    /// Plans <paramref name="targets"/> against one source's files: whole-file matches, copy runs, then diffs.
    /// </summary>
    private static List<PlannedOutput> PlanSource(List<FileEntry> stock, List<FileEntry> targets, string spool, int jobs, TextWriter log, Stopwatch clock)
    {
        List<PlannedOutput> plans = Plan(stock, targets);
        log.WriteLine($"  planned in {clock.Elapsed.TotalSeconds:F0}s: {plans.Count(p => p.same is not null)} unchanged, {plans.Count(p => p.reference is not null)} with a same-path reference, {plans.Count(p => p.same is null && p.reference is null)} new");
        List<DiffJob> work = Layout(plans, stock, jobs, log);
        log.WriteLine($"  matched copies in {clock.Elapsed.TotalSeconds:F0}s");
        Diff(work, spool, jobs, log);

        // Writing the patch starts zstd's workers, whose native memory the GC can't see, so nothing else will prompt it
        // to give back the gigabytes the diff workers held.
        GC.Collect();
        log.WriteLine($"  diffed in {clock.Elapsed.TotalSeconds:F0}s");
        return plans;
    }

    private static List<PlannedOutput> Plan(List<FileEntry> baseFiles, List<FileEntry> targetFiles)
    {
        Dictionary<string, FileEntry> byPath = baseFiles.ToDictionary(f => f.relative, StringComparer.OrdinalIgnoreCase);
        ILookup<long, FileEntry> bySize = baseFiles.ToLookup(f => f.size);
        List<PlannedOutput> plans = [..targetFiles.Select(t => new PlannedOutput(t))];
        Parallel.ForEach(plans, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, plan =>
        {
            FileEntry target = plan.target;
            byPath.TryGetValue(target.relative, out FileEntry? samePath);
            if (samePath is not null && !InScope(samePath, target)) samePath = null;

            // identical content anywhere in scope; the same path first so in-place applies skip it entirely
            IOrderedEnumerable<FileEntry> candidates = bySize[target.size].Where(b => InScope(b, target)).OrderBy(b => b == samePath ? 0 : 1);
            plan.same = candidates.FirstOrDefault(b => b.Hash == target.Hash);
            if (plan.same is null && samePath is { size: > 0 }) plan.reference = samePath;
        });

        return plans;
    }

    private static bool Indexable(FileEntry file) => file.size is >= MIN_DIFF_GAP and < int.MaxValue;

    /// <summary>
    /// Splits every changed output into copy runs and gaps; returns one diff job per target window with gaps.
    /// </summary>
    private static List<DiffJob> Layout(List<PlannedOutput> plans, List<FileEntry> baseFiles, int jobs, TextWriter log)
    {
        List<PlannedOutput> changed = [..plans.Where(p => p.same is null)];
        if (changed.Count is 0) return [];

        List<FileEntry> stock =
        [
            ..baseFiles.Where(Indexable)
                       .OrderBy(f => IsCore(f) ? 0 : 1).ThenBy(f => f.relative, StringComparer.OrdinalIgnoreCase)
        ];

        using CopyMatcher matcher = new(stock);
        log.WriteLine($"  indexed {matcher.Count} chunks of {stock.Count} stock files");

        ConcurrentBag<DiffJob> work = [];
        Parallel.ForEach(changed, new ParallelOptions { MaxDegreeOfParallelism = jobs }, plan =>
        {
            FileEntry target = plan.target;
            // TODO csavard: handle this disposed in outside scope variable
            List<CopyRun> runs = Indexable(target) ? matcher.Match(target, s => InScope(s, target)) : [];

            List<PlannedSegment?> slots = [];
            Dictionary<long, List<(int, long, int)>> windows = new();

            void Gap(long start, long end)
            {
                // split at window boundaries so each piece belongs to exactly one target window
                while (start < end)
                {
                    long windowIndex = start / CHUNK_SIZE;
                    long pieceEnd = Math.Min(end, (windowIndex + 1) * CHUNK_SIZE);
                    slots.Add(null);
                    if (!windows.TryGetValue(windowIndex, out List<(int, long, int)>? gaps)) windows[windowIndex] = gaps = [];
                    gaps.Add((slots.Count - 1, start, (int)(pieceEnd - start)));
                    start = pieceEnd;
                }
            }

            long at = 0;
            foreach (CopyRun run in runs)
            {
                Gap(at, run.Start);
                slots.Add(new PlannedSegment(Segment.Copy(-1, run.SourceOffset, run.Length), run.Start, run.Source));
                at = run.End;
            }

            Gap(at, target.size);
            if (at > target.size || runs.Zip(runs.Skip(1)).Any(p => p.First.End > p.Second.Start))
            {
                throw new InvalidOperationException($"overlapping copy runs in {target.relative}");
            }

            // TODO csavard: Handle this possible null assignment
            plan.segments = [..slots]!;

            foreach ((long windowIndex, List<(int, long, int)> gaps) in windows)
            {
                work.Add(ReferenceWindow(plan, windowIndex, gaps));
            }
        });

        return [..work];
    }

    /// <summary>
    /// The stock window a target window is diffed against: same relative position, padded a quarter chunk each side.
    /// </summary>
    private static DiffJob ReferenceWindow(PlannedOutput plan, long windowIndex, List<(int, long, int)> gaps)
    {
        if (plan.reference is null) return new DiffJob(plan, gaps, 0, 0);

        long newSize = plan.target.size, refSize = plan.reference.size;
        long newOffset = windowIndex * CHUNK_SIZE, newEnd = Math.Min(newSize, newOffset + CHUNK_SIZE);
        double scale = (double)refSize / newSize;
        long refStart = Math.Max(0, (long)(newOffset * scale) - (CHUNK_SIZE / 4));
        long refEnd = Math.Min(refSize, (long)(newEnd * scale) + (CHUNK_SIZE / 4));
        if (refSize <= (CHUNK_SIZE * 3L) / 2) (refStart, refEnd) = (0, refSize);
        refEnd = Math.Min(refEnd, refStart + ((CHUNK_SIZE * 3L) / 2));
        return new DiffJob(plan, gaps, refStart, (int)(refEnd - refStart));
    }

    private static void Diff(List<DiffJob> work, string spool, int jobs, TextWriter log)
    {
        List<DiffJob> ordered = [..work.OrderByDescending(j => j.RefLength + j.GapBytes)];
        int done = 0;
        int largest = ordered.Count is 0 ? 0 : ordered.Max(j => j.RefLength);

        // A window's arrays reach a gigabyte, and allocating them per job leaves the GC to stop every worker to collect
        // and compact them. So they are reused, from a bag rather than per thread, because a pool can use more threads
        // than it runs at once, and at most one workspace per window can then be in use.
        ConcurrentBag<DiffWorkspace> workspaces = [];

        // Sorting is limited by memory bandwidth, so a job for every hardware thread sorts no faster than one per core
        // and holds twice the memory. Half the jobs is the closest to that the runtime can tell.
        ParallelOptions windowOptions = new() { MaxDegreeOfParallelism = Math.Max(1, jobs / 2) };
        ParallelOptions gapOptions = new() { MaxDegreeOfParallelism    = jobs };

        // Unbuffered, so that workers take the largest windows first rather than ranges of the list.
        Parallel.ForEach(Partitioner.Create(ordered, EnumerablePartitionerOptions.NoBuffering), windowOptions, job =>
        {
            Stopwatch clock = Stopwatch.StartNew();
            PlannedOutput output = job.Output;
            bool diffable = output.reference is not null && job.GapBytes >= MIN_DIFF_WINDOW;
            if (!workspaces.TryTake(out DiffWorkspace? workspace))
            {
                workspace = new DiffWorkspace(largest);
            }

            if (diffable)
            {
                Span<byte> reference = workspace.reference.AsSpan(0, job.RefLength);
                ReadRange(output.reference!.full, job.RefOffset, reference);
                SuffixSorter.Sort(reference, workspace.suffixes, workspace.symbols);
                BsDiff.PrefixIndex(reference, workspace.prefixes);
            }

            // A window's gaps share its read-only suffix array, and the windows with the most gap bytes take so much
            // longer than the rest that they would otherwise keep the whole build waiting on one core.
            long estimate = 0;
            Parallel.ForEach(job.Gaps, gapOptions, gap =>
            {
                (int slot, long offset, int length) = gap;
                byte[] newBytes = new byte[length];
                ReadRange(output.target.full, offset, newBytes);
                PlannedSegment segment = new(Segment.Literal(length), offset, Estimate: Compressed(newBytes));
                if (diffable && length >= MIN_DIFF_GAP)
                {
                    // raw patches are about as long as the new data (the diff stream is mostly zeros), so compare compressed sizes
                    Span<byte> reference = workspace.reference.AsSpan(0, job.RefLength);
                    byte[] patch = BsDiff.Diff(reference, workspace.suffixes, workspace.prefixes, newBytes);
                    long patchEstimate = Compressed(patch);
                    if (patchEstimate < segment.Estimate)
                    {
                        segment = SpoolDiff(output, offset, spool, reference, job.RefOffset, newBytes, patch, patchEstimate);
                    }
                }

                output.segments[slot] = segment;
                Interlocked.Add(ref estimate, segment.Estimate);
            });

            workspaces.Add(workspace);

            int n = Interlocked.Increment(ref done);
            if (job.GapBytes >= 1 << 20 || n == ordered.Count)
            {
                log.WriteLine($"  [{n}/{ordered.Count}] {output.target.relative}: {job.Gaps.Count} gaps, {job.GapBytes / 1048576.0:F1} MB -> "
                            + $"~{estimate / 1048576.0:F2} MB ({clock.Elapsed.TotalSeconds:F1}s)");
            }
        });
    }

    private static PlannedSegment SpoolDiff(PlannedOutput output, long offset, string spool, ReadOnlySpan<byte> reference, long refOffset,
                                            byte[] newBytes, byte[] patch, long estimate)
    {
        using (MemoryStream check = new(newBytes.Length))
        {
            BsPatch.Apply(reference, patch, check);
            if (!check.GetBuffer().AsSpan(0, (int)check.Length).SequenceEqual(newBytes))
            {
                throw new InvalidOperationException($"bsdiff round trip failed for {output.target.relative} at {offset}");
            }
        }

        string spoolFile = Path.Combine(spool, $"{Guid.NewGuid():N}.bsdiff");
        File.WriteAllBytes(spoolFile, patch);
        List<Extent> refs = [new(-1, refOffset, reference.Length)];
        return new PlannedSegment(Segment.Diff(refs, newBytes.Length, patch.Length), offset, output.reference, spoolFile, estimate);
    }

    /// <summary>
    /// Stand-alone zstd size at a fast level: decides diff vs literal and ranks the report.
    /// </summary>
    private static long Compressed(ReadOnlySpan<byte> bytes)
    {
        using Compressor compressor = new();
        return compressor.Wrap(bytes).Length;
    }

    private static void ReadRange(string path, long offset, Span<byte> bytes)
    {
        using SafeFileHandle handle = File.OpenHandle(path);
        for (int at = 0; at < bytes.Length;)
        {
            int n = RandomAccess.Read(handle, bytes[at..], offset + at);
            if (n is 0) throw new EndOfStreamException(path);

            at += n;
        }
    }

    /// <summary>
    /// A component's files in any source that the build does not ship, so in-place matches side-by-side.
    /// </summary>
    private static List<string> RemovedStockFiles(List<SourceInstall> sources, List<FileEntry> targetFiles, int component)
    {
        HashSet<string> shipped = targetFiles.Select(t => t.relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            ..sources.SelectMany(s => s.Files)
                     .Where(b => b.component == component && !shipped.Contains(b.relative))
                     .Select(b => b.relative)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase)
        ];
    }

    private static Recipe ToRecipe(PlannedOutput plan, Func<FileEntry, int> sourceIndex)
    {
        if (plan.same is not null) return new Recipe { Same = sourceIndex(plan.same) };

        Recipe recipe = new() { Segments = [] };
        foreach ((Segment segment, _, FileEntry? fileEntry, _, _) in plan.segments)
        {
            if (segment.Kind is SegmentKind.Copy) segment.File = sourceIndex(fileEntry!);
            foreach (Extent extent in segment.Refs ?? [])
            {
                extent.File = sourceIndex(fileEntry!);
            }

            recipe.Segments.Add(segment);
        }

        return recipe;
    }

    /// <summary>
    /// Writes the sections (each source's recipes per component, blobs in output order), then the manifest. Sections
    /// of the first source come first; a recipe without blobs has no section.
    /// </summary>
    private static void WriteFile(string path, PatchManifest manifest, List<PlannedRecipe> recipes, BuildOptions o, StringBuilder report)
    {
        List<string> sectionNames =
        [
            ..recipes.Where(r => r.Recipe.ArchiveBytes > 0).Select(r => r.SectionName).Distinct()
                     .OrderBy(n => n.Contains('@') ? 1 : 0).ThenBy(n => recipes.FindIndex(r => r.SectionName == n))
        ];

        using PatchFileWriter writer = new(path, o.Level, o.Window, o.Jobs);
        byte[] buffer = new byte[1 << 20];
        foreach (string name in sectionNames)
        {
            Stream stream = writer.BeginSection(name);
            int index = manifest.Sections.Count;
            foreach (PlannedRecipe planned in recipes.Where(r => r.SectionName == name && r.Recipe.ArchiveBytes > 0))
            {
                planned.Recipe.Section = index;
                planned.Recipe.Offset  = stream.Position;
                WriteBlobs(planned.PlannedOutput, stream, buffer);
            }

            Section section = writer.EndSection();
            manifest.Sections.Add(section);
            report.AppendLine(CultureInfo.InvariantCulture, $"section {name,-32} {section.Size / 1048576.0,10:F1} MB -> {section.Length / 1048576.0,8:F1} MB");
        }

        writer.Finish(manifest);
    }

    private static void WriteBlobs(PlannedOutput plan, Stream archive, byte[] buffer)
    {
        using FileStream? target = plan.segments.Any(s => s.Segment.Kind == SegmentKind.Literal) ? File.OpenRead(plan.target.full) : null;
        foreach (PlannedSegment planned in plan.segments)
        {
            if (planned.Spool is not null)
            {
                using FileStream spool = File.OpenRead(planned.Spool);
                spool.CopyTo(archive);
            }
            else if (planned.Segment.Kind is SegmentKind.Literal)
            {
                target!.Position = planned.TargetOffset;
                for (long left = planned.Segment.Length; left > 0;)
                {
                    int n = target.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n is 0) throw new EndOfStreamException(plan.target.full);

                    archive.Write(buffer, 0, n);
                    left -= n;
                }
            }
        }
    }

    private static void Report(StringBuilder report, string source, List<PlannedOutput> outputs)
    {
        static string Mb(long bytes) => $"{bytes / 1048576.0,10:F1} MB";

        List<PlannedOutput> same = [..outputs.Where(p => p.same is not null)];
        List<PlannedSegment> segments = [..outputs.SelectMany(p => p.segments)];
        List<PlannedSegment> copies = [..segments.Where(s => s.Segment.Kind == SegmentKind.Copy)];
        List<PlannedSegment> diffs = [..segments.Where(s => s.Segment.Kind == SegmentKind.BsDiff)];
        List<PlannedSegment> literals = [..segments.Where(s => s.Segment.Kind == SegmentKind.Literal)];

        report.AppendLine($"== {source}: {outputs.Count} outputs planned");
        report.AppendLine($"  unchanged  {same.Count,7} files {Mb(same.Sum(p => p.target.size))}");
        report.AppendLine($"  copy       {copies.Count,7} runs  {Mb(copies.Sum(s => s.Segment.Length))}");
        report.AppendLine($"  bsdiff     {diffs.Count,7} runs  {Mb(diffs.Sum(s => s.Segment.Length))} -> ~{Mb(diffs.Sum(s => s.Estimate))}");
        report.AppendLine($"  literal    {literals.Count,7} runs  {Mb(literals.Sum(s => s.Segment.Length))} -> ~{Mb(literals.Sum(s => s.Estimate))}");
        report.AppendLine("  largest contributors (~compressed alone at level 3 / copied from stock / output size):");

        foreach (PlannedOutput plan in outputs.Where(p => p.same is null).OrderByDescending(p => p.segments.Sum(s => s.Estimate)).Take(40))
        {
            long bytes = plan.segments.Sum(s => s.Estimate);
            long copied = plan.segments.Where(s => s.Segment.Kind == SegmentKind.Copy).Sum(s => s.Segment.Length);
            string kind = plan.reference is null ? "new" : "diff";
            report.AppendLine($"    {Mb(bytes)} / {Mb(copied)} / {Mb(plan.target.size)}  {kind,-4} {plan.target.relative}");
        }
    }

    private sealed class FileEntry(string relative, string full, long size, int component)
    {
        public readonly int component = component;
        private readonly Lazy<string> hash = new(() => PatchApplier.Sha256Hex(full));
        public readonly string relative = relative, full = full;
        public readonly long size = size;
        public string Hash => this.hash.Value;
    }

    private sealed record SourceInstall(string Id, List<FileEntry> Files);

    /// <summary>
    /// A segment plus where its bytes come from: <see cref="Source"/> is the stock file a copy or bsdiff reads; literal
    /// bytes are read from the target file at the segment's position and bsdiff blobs from a spool file.
    /// </summary>
    private sealed record PlannedSegment(Segment Segment, long TargetOffset, FileEntry? Source = null, string? Spool = null, long Estimate = 0);

    private sealed class PlannedOutput(FileEntry target)
    {
        public readonly FileEntry target = target;
        public FileEntry? reference;
        public FileEntry? same;
        public PlannedSegment[] segments = [];
    }

    /// <summary>
    /// The gaps of one target window, each filling slot <c>Slot</c> of the output's segments.
    /// </summary>
    private sealed record DiffJob(PlannedOutput Output, List<(int Slot, long Offset, int Length)> Gaps, long RefOffset, int RefLength)
    {
        public long GapBytes => this.Gaps.Sum(g => (long)g.Length);
    }

    /// <summary>
    /// A recipe and the plan whose blobs it writes into section <see cref="SectionName"/>.
    /// </summary>
    private sealed record PlannedRecipe(Recipe Recipe, PlannedOutput PlannedOutput, string SectionName);

    /// <summary>
    /// One worker's buffers for a reference window of up to the given length.
    /// </summary>
    private sealed class DiffWorkspace(int capacity)
    {
        public readonly int[] prefixes = GC.AllocateUninitializedArray<int>(BsDiff.PREFIX_INDEX_LENGTH);
        public readonly byte[] reference = GC.AllocateUninitializedArray<byte>(capacity);
        public readonly int[] suffixes = GC.AllocateUninitializedArray<int>(capacity + 1);
        public readonly ushort[] symbols = GC.AllocateUninitializedArray<ushort>(capacity + 1);
    }
}
