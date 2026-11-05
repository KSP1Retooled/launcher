using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using ZstdSharp;

namespace KSP.Retooled.Patch;

/// <summary>A stock install the patch must apply to; later <see cref="Roots"/> overlay earlier ones (e.g. the game and DLC depots).</summary>
public sealed class BuildSource
{
    /// <summary>Defaults to <c>&lt;distribution&gt;-&lt;build id&gt;</c> from the install's buildID file, else the directory name.</summary>
    public string? Name { get; init; }
    public required IReadOnlyList<string> Roots { get; init; }
}

public sealed class BuildOptions
{
    public required IReadOnlyList<BuildSource> Sources { get; init; }
    /// <summary>The retooled release build every source is patched into.</summary>
    public required string Target { get; init; }
    /// <summary>The patch file to write.</summary>
    public required string Output { get; init; }
    /// <summary><c>windows</c>, <c>linux</c> or <c>macos</c>; detected from the target's player when null.</summary>
    public string? Platform { get; init; }
    public int Jobs { get; init; } = PatchBuilder.DefaultJobs;
    public int Level { get; init; } = 19;
    public int Window { get; init; } = 27;
    /// <summary>Install-relative patterns ('*' spans directories) left out of every tree, on top of the defaults.</summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];
    /// <summary>Patterns left out of the target only.</summary>
    public IReadOnlyList<string> TargetExcludes { get; init; } = [];
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
    /// Never part of a stock base or a shipped build: user data, logs, debug symbols, Unity's backup folders, the
    /// part icon / craft caches KSP writes at runtime (absent from a fresh install, different in every played one),
    /// the folder metadata Finder and Explorer write, and a patched install's journal.
    /// </summary>
    static readonly string[] DefaultExcludes =
    [
        "saves/*", "Logs/*", "Screenshots/*", "thumbs/*", "@thumbs/*", "*/@thumbs/*", "*.loadmeta", "CKAN/*", "Ships-Old/*", "Notes/*",
        "PluginData/*", "settings.cfg", "*.log", "dataLog.*", "rt_dump.txt", "*.pdb",
        ".DS_Store", "*/.DS_Store", "Thumbs.db", "*/Thumbs.db", "desktop.ini", "*/desktop.ini",
        "*_BackUpThisFolder_ButDontShipItWithYourGame/*", InstallJournal.Directory + "/*",
    ];

    /// <summary>Stock content under an excluded folder: the scenario and training saves.</summary>
    static readonly string[] StockSaves = ["saves/scenarios/*", "saves/training/*"];

    /// <summary>
    /// The game's own parts and DLC, which mods overwrite and which differ between stores, so a patch must not depend on
    /// what they hold: no recipe reads them, and a file here that some source has as the build ships it stays the install's.
    /// </summary>
    static readonly string[] UnreadFolders = ["GameData/Squad/", "GameData/SquadExpansion/"];

    /// <summary>DLC components first so their prefixes win; core takes everything else.</summary>
    static readonly (string Name, string? Requires)[] Components =
    [
        ("makinghistory", "GameData/SquadExpansion/MakingHistory"),
        ("serenity", "GameData/SquadExpansion/Serenity"),
        ("core", null),
    ];

    /// <summary>Target window diffed against one reference window (at most 1.5× this), sharing one suffix sort.</summary>
    const int ChunkSize = 64 << 20;
    /// <summary>What a worker can hold at its largest: a reference window, its suffix array and the sort's symbols.</summary>
    const long JobMemory = 1L << 30;
    /// <summary>One job per logical core, unless that would need more memory than this machine or container has.</summary>
    internal static int DefaultJobs => (int)Math.Clamp(
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / JobMemory, 1, Environment.ProcessorCount);
    /// <summary>Gaps shorter than this, or windows with fewer gap bytes than <see cref="MinDiffWindow"/>, stay literal.</summary>
    const int MinDiffGap = 4 << 10;
    const int MinDiffWindow = 256 << 10;

    sealed class FileEntry(string relative, string full, long size, int component)
    {
        public readonly string Relative = relative, Full = full;
        public readonly long Size = size;
        public readonly int Component = component;
        readonly Lazy<string> hash = new(() => PatchApplier.Sha256Hex(full));
        public string Hash => hash.Value;
    }

    sealed record SourceInstall(string Id, List<FileEntry> Files);

    /// <summary>
    /// A segment plus where its bytes come from: <see cref="Source"/> is the stock file a copy or bsdiff reads; literal
    /// bytes are read from the target file at the segment's position and bsdiff blobs from a spool file.
    /// </summary>
    sealed record PlannedSegment(Segment Segment, long TargetOffset, FileEntry? Source = null, string? Spool = null, long Estimate = 0);

    sealed class PlannedOutput(FileEntry target)
    {
        public readonly FileEntry Target = target;
        public FileEntry? Same;
        public FileEntry? Reference;
        public PlannedSegment[] Segments = [];
    }

    /// <summary>The gaps of one target window, each filling slot <c>Slot</c> of the output's segments.</summary>
    sealed record DiffJob(PlannedOutput Output, List<(int Slot, long Offset, int Length)> Gaps, long RefOffset, int RefLength)
    {
        public long GapBytes => Gaps.Sum(g => (long)g.Length);
    }

    /// <summary>A recipe and the plan whose blobs it writes into section <see cref="SectionName"/>.</summary>
    sealed record PlannedRecipe(Recipe Recipe, PlannedOutput Plan, string SectionName);

    public static BuildResult Build(BuildOptions o)
    {
        if (o.Sources.Count == 0) throw new PatchException("no sources");
        var log = o.Log;
        string targetPath = Path.GetFullPath(o.Target), outPath = Path.GetFullPath(o.Output);
        var excludes = DefaultExcludes.Concat(o.Excludes).ToArray();
        var targetExcludes = excludes.Concat(o.TargetExcludes).ToArray();

        var clock = Stopwatch.StartNew();
        var emptyDirectories = new List<string>();
        var targetFiles = Walk(targetPath, targetExcludes, emptyDirectories);
        string platform = o.Platform ?? DetectPlatform(targetPath);
        var sources = LoadSources(o.Sources, excludes, log);
        foreach (var source in sources) CheckLayout(source, targetFiles, targetPath);
        log.WriteLine($"target {targetFiles.Count} files ({platform}); sources "
            + string.Join(", ", sources.Select(s => $"{s.Id} ({s.Files.Count} files)")));

        Parallel.ForEach(sources.SelectMany(s => s.Files).Concat(targetFiles),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f => _ = f.Hash);
        log.WriteLine($"hashed in {clock.Elapsed.TotalSeconds:F0}s");

        var manifest = new PatchManifest { Platform = platform };
        var sourceIndex = new Dictionary<(string, string), int>();
        int SourceIndex(FileEntry stock)
        {
            if (!sourceIndex.TryGetValue((stock.Relative, stock.Hash), out int index))
            {
                sourceIndex[(stock.Relative, stock.Hash)] = index = manifest.Sources.Count;
                manifest.Sources.Add(new SourceFile { Path = stock.Relative, Size = stock.Size, Sha256 = stock.Hash });
            }
            return index;
        }
        foreach (var source in sources)
            manifest.Variants.Add(new Variant { Id = source.Id, Files = source.Files.Select(SourceIndex).Order().ToList() });

        var ordered = targetFiles.OrderBy(t => t.Component).ThenBy(t => t.Relative, StringComparer.OrdinalIgnoreCase).ToList();
        var recipes = ordered.ToDictionary(t => t, _ => new List<PlannedRecipe>());

        var stockByPath = sources.SelectMany(s => s.Files).ToLookup(f => f.Relative, StringComparer.OrdinalIgnoreCase);
        var leftAlone = new HashSet<FileEntry>();
        foreach (var target in ordered.Where(t => Unread(t.Relative)))
        {
            if (stockByPath[target.Relative].FirstOrDefault(f => f.Hash == target.Hash) is not FileEntry stock) continue;
            recipes[target].Add(new PlannedRecipe(new Recipe { Same = SourceIndex(stock) }, new PlannedOutput(target), ""));
            leftAlone.Add(target);
        }

        string spool = outPath + ".spool";
        if (Directory.Exists(spool)) Directory.Delete(spool, recursive: true);
        Directory.CreateDirectory(spool);
        var report = new StringBuilder();
        for (int v = 0; v < sources.Count; v++)
        {
            var source = sources[v];
            var has = manifest.Variants[v].Files.ToHashSet();
            var pending = ordered.Where(t => !leftAlone.Contains(t) && !recipes[t].Any(r => r.Recipe.Inputs().All(has.Contains))).ToList();
            log.WriteLine($"{source.Id}: {ordered.Count - pending.Count} outputs reuse earlier recipes, {pending.Count} to plan");
            if (pending.Count == 0) continue;

            var plans = PlanSource(source.Files, pending, spool, o.Jobs, log, clock);
            foreach (var plan in plans)
            {
                string section = Components[plan.Target.Component].Name + (v == 0 ? "" : "@" + source.Id);
                recipes[plan.Target].Add(new PlannedRecipe(ToRecipe(plan, SourceIndex), plan, section));
            }
            Report(report, source.Id, plans);
        }

        foreach (var target in ordered)
            manifest.Outputs.Add(new OutputFile
            {
                Path = target.Relative, Size = target.Size, Sha256 = target.Hash, Component = target.Component,
                Executable = IsExecutable(target), Recipes = recipes[target].Select(r => r.Recipe).ToList(),
            });
        manifest.Components = Components.Select((c, i) => new Component
        {
            Name = c.Name,
            Requires = c.Requires,
            Remove = RemovedStockFiles(sources, targetFiles, i),
            Directories = emptyDirectories.Where(d => ComponentOf(d + "/") == i).ToList(),
        }).ToList();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        WriteFile(outPath, manifest, ordered.SelectMany(t => recipes[t]).ToList(), o, report);
        Directory.Delete(spool, recursive: true);
        log.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length / 1048576.0:F1} MB) at {clock.Elapsed.TotalSeconds:F0}s");
        return new BuildResult(manifest, report.ToString());
    }

    static string DetectPlatform(string target)
    {
        if (File.Exists(Path.Combine(target, "KSP_x64.exe"))) return "windows";
        if (File.Exists(Path.Combine(target, "KSP.x86_64"))) return "linux";
        if (Directory.Exists(Path.Combine(target, "KSP.app"))) return "macos";
        throw new PatchException($"{target} has no KSP player (KSP_x64.exe, KSP.x86_64 or KSP.app); pass the platform explicitly");
    }

    static List<SourceInstall> LoadSources(IReadOnlyList<BuildSource> sources, string[] excludes, TextWriter log)
    {
        var loaded = new List<SourceInstall>();
        foreach (var source in sources)
        {
            if (source.Roots.Count == 0) throw new PatchException("a source has no directories");
            var files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in source.Roots)
                foreach (var file in Walk(Path.GetFullPath(root), excludes))
                    files[file.Relative] = file;
            string name = source.Name ?? BuildId(source.Roots) ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Roots[0])));
            string id = name;
            for (int n = 2; loaded.Any(l => l.Id == id); n++) id = $"{name}-{n}";
            loaded.Add(new SourceInstall(id, files.Values.OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase).ToList()));
        }
        return loaded;
    }

    /// <summary><c>steam-03190</c> from buildID64.txt / buildID.txt (<c>build id = 03190</c>, <c>distribution name = Steam</c>).</summary>
    static string? BuildId(IEnumerable<string> roots)
    {
        foreach (string root in roots)
            foreach (string name in BuildIdFile.Names)
            {
                string path = Path.Combine(root, name);
                if (!File.Exists(path)) continue;
                string? build = BuildIdFile.Value(path, "build id");
                if (build == null) continue;
                string distribution = (BuildIdFile.Value(path, "distribution name") ?? "ksp").ToLowerInvariant().Replace(' ', '-');
                return $"{distribution}-{build}";
            }
        return null;
    }

    static bool Excluded(string relative, string[] excludes) =>
        excludes.Any(e => FileSystemName.MatchesSimpleExpression(e, relative, ignoreCase: true))
        && !StockSaves.Any(e => FileSystemName.MatchesSimpleExpression(e, relative, ignoreCase: true));

    /// <summary>The files under <paramref name="root"/>; <paramref name="emptyDirectories"/> collects the directories with no entries.</summary>
    static List<FileEntry> Walk(string root, string[] excludes, List<string>? emptyDirectories = null)
    {
        if (!Directory.Exists(root)) throw new PatchException($"{root} does not exist");
        var files = new List<FileEntry>();
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            if (emptyDirectories != null && directory != root && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                string relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
                if (!Excluded(relative + "/", excludes)) emptyDirectories.Add(relative);
            }
            foreach (string sub in Directory.EnumerateDirectories(directory)) pending.Push(sub);
            foreach (string full in Directory.EnumerateFiles(directory))
            {
                string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                if (Excluded(relative, excludes)) continue;
                // dev GameData is largely symlinks into a stock install; a link's own FileInfo has the link's length
                var info = new FileInfo(full);
                long size = info.LinkTarget == null ? info.Length : ((FileInfo)info.ResolveLinkTarget(returnFinalTarget: true)!).Length;
                files.Add(new FileEntry(relative, full, size, ComponentOf(relative)));
            }
        }
        return files;
    }

    static int ComponentOf(string relative)
    {
        for (int c = 0; c < Components.Length; c++)
            if (Components[c].Requires is not string prefix || relative.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return c;
        throw new UnreachableException();
    }

    static bool Unread(string relative) =>
        UnreadFolders.Any(f => relative.StartsWith(f, StringComparison.OrdinalIgnoreCase));

    static bool IsCore(FileEntry file) => Components[file.Component].Requires == null;

    static bool InScope(FileEntry stock, FileEntry target) => stock.Component == target.Component || IsCore(stock);

    static string TopLevel(FileEntry file) => file.Relative.Split('/')[0];

    /// <summary>
    /// An in-place apply removes every stock file the build does not ship, so the build must be a release with stock's
    /// layout: a build missing a whole stock top-level entry (GameData, Ships, the player) would delete it. A CoreCLR
    /// release alone has no MonoBleedingEdge, and there the apply should delete stock's: a CoreCLR player does not use it,
    /// and Special K, finding it, crashes the game (Builder.StockMonoRuntime).
    /// </summary>
    static void CheckLayout(SourceInstall source, List<FileEntry> targetFiles, string targetPath)
    {
        var shipped = targetFiles.Select(TopLevel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool coreClr = shipped.Contains("CoreCLR");
        var missing = source.Files.Where(IsCore).Select(TopLevel).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !shipped.Contains(n) && !(coreClr && n.Equals("MonoBleedingEdge", StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
            throw new PatchException($"{targetPath} has none of {source.Id}'s {string.Join(", ", missing)}: patch a release build "
                + "(Builder.Build -retooledRelease), which has stock's layout");
    }

    /// <summary>ELF and Mach-O binaries and scripts, which need the executable mode on Linux and macOS.</summary>
    static bool IsExecutable(FileEntry file)
    {
        if (file.Size < 4) return false;
        Span<byte> magic = stackalloc byte[4];
        using (var stream = File.OpenRead(file.Full)) stream.ReadExactly(magic);
        uint word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(magic);
        return word is 0x7F454C46 or 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE
            || magic[0] == '#' && magic[1] == '!';
    }

    /// <summary>Plans <paramref name="targets"/> against one source's files: whole-file matches, copy runs, then diffs.</summary>
    static List<PlannedOutput> PlanSource(List<FileEntry> stock, List<FileEntry> targets, string spool, int jobs, TextWriter log, Stopwatch clock)
    {
        var plans = Plan(stock, targets);
        log.WriteLine($"  planned in {clock.Elapsed.TotalSeconds:F0}s: {plans.Count(p => p.Same != null)} unchanged, "
            + $"{plans.Count(p => p.Reference != null)} with a same-path reference, {plans.Count(p => p.Same == null && p.Reference == null)} new");
        var work = Layout(plans, stock, jobs, log);
        log.WriteLine($"  matched copies in {clock.Elapsed.TotalSeconds:F0}s");
        Diff(work, spool, jobs, log);

        // Writing the patch starts zstd's workers, whose native memory the GC can't see, so nothing else will prompt it
        // to give back the gigabytes the diff workers held.
        GC.Collect();
        log.WriteLine($"  diffed in {clock.Elapsed.TotalSeconds:F0}s");
        return plans;
    }

    static List<PlannedOutput> Plan(List<FileEntry> baseFiles, List<FileEntry> targetFiles)
    {
        var byPath = baseFiles.ToDictionary(f => f.Relative, StringComparer.OrdinalIgnoreCase);
        var bySize = baseFiles.ToLookup(f => f.Size);
        var plans = targetFiles.Select(t => new PlannedOutput(t)).ToList();
        Parallel.ForEach(plans, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, plan =>
        {
            var target = plan.Target;
            byPath.TryGetValue(target.Relative, out var samePath);
            if (samePath != null && (!InScope(samePath, target) || Unread(samePath.Relative))) samePath = null;

            // identical content anywhere in scope; the same path first so in-place applies skip it entirely
            var candidates = bySize[target.Size].Where(b => InScope(b, target) && !Unread(b.Relative)).OrderBy(b => b == samePath ? 0 : 1);
            plan.Same = candidates.FirstOrDefault(b => b.Hash == target.Hash);
            if (plan.Same == null && samePath is { Size: > 0 }) plan.Reference = samePath;
        });
        return plans;
    }

    static bool Indexable(FileEntry file) => file.Size >= MinDiffGap && file.Size < int.MaxValue;

    /// <summary>Splits every changed output into copy runs and gaps; returns one diff job per target window with gaps.</summary>
    static List<DiffJob> Layout(List<PlannedOutput> plans, List<FileEntry> baseFiles, int jobs, TextWriter log)
    {
        var changed = plans.Where(p => p.Same == null).ToList();
        if (changed.Count == 0) return [];
        var stock = baseFiles.Where(f => Indexable(f) && !Unread(f.Relative))
            .OrderBy(f => IsCore(f) ? 0 : 1).ThenBy(f => f.Relative, StringComparer.OrdinalIgnoreCase).ToList();
        using var matcher = new CopyMatcher(stock);
        log.WriteLine($"  indexed {matcher.Count} chunks of {stock.Count} stock files");

        var work = new ConcurrentBag<DiffJob>();
        Parallel.ForEach(changed, new ParallelOptions { MaxDegreeOfParallelism = jobs }, plan =>
        {
            var target = plan.Target;
            var runs = Indexable(target) ? matcher.Match(target, s => InScope(s, target)) : [];

            var slots = new List<PlannedSegment?>();
            var windows = new Dictionary<long, List<(int, long, int)>>();
            void Gap(long start, long end)
            {
                // split at window boundaries so each piece belongs to exactly one target window
                while (start < end)
                {
                    long windowIndex = start / ChunkSize;
                    long pieceEnd = Math.Min(end, (windowIndex + 1) * ChunkSize);
                    slots.Add(null);
                    if (!windows.TryGetValue(windowIndex, out var gaps)) windows[windowIndex] = gaps = [];
                    gaps.Add((slots.Count - 1, start, (int)(pieceEnd - start)));
                    start = pieceEnd;
                }
            }

            long at = 0;
            foreach (var run in runs)
            {
                Gap(at, run.Start);
                slots.Add(new PlannedSegment(Segment.Copy(-1, run.SourceOffset, run.Length), run.Start, run.Source));
                at = run.End;
            }
            Gap(at, target.Size);
            if (at > target.Size || runs.Zip(runs.Skip(1)).Any(p => p.First.End > p.Second.Start))
                throw new InvalidOperationException($"overlapping copy runs in {target.Relative}");
            plan.Segments = slots.ToArray()!;

            foreach (var (windowIndex, gaps) in windows)
                work.Add(ReferenceWindow(plan, windowIndex, gaps));
        });
        return work.ToList();
    }

    /// <summary>The stock window a target window is diffed against: same relative position, padded a quarter chunk each side.</summary>
    static DiffJob ReferenceWindow(PlannedOutput plan, long windowIndex, List<(int, long, int)> gaps)
    {
        if (plan.Reference == null) return new DiffJob(plan, gaps, 0, 0);
        long newSize = plan.Target.Size, refSize = plan.Reference.Size;
        long newOffset = windowIndex * ChunkSize, newEnd = Math.Min(newSize, newOffset + ChunkSize);
        double scale = (double)refSize / newSize;
        long refStart = Math.Max(0, (long)(newOffset * scale) - ChunkSize / 4);
        long refEnd = Math.Min(refSize, (long)(newEnd * scale) + ChunkSize / 4);
        if (refSize <= ChunkSize * 3L / 2) (refStart, refEnd) = (0, refSize);
        refEnd = Math.Min(refEnd, refStart + ChunkSize * 3L / 2);
        return new DiffJob(plan, gaps, refStart, (int)(refEnd - refStart));
    }

    static void Diff(List<DiffJob> work, string spool, int jobs, TextWriter log)
    {
        var ordered = work.OrderByDescending(j => j.RefLength + j.GapBytes).ToList();
        int done = 0;
        int largest = ordered.Count == 0 ? 0 : ordered.Max(j => j.RefLength);

        // A window's arrays reach a gigabyte, and allocating them per job leaves the GC to stop every worker to collect
        // and compact them. So they are reused, from a bag rather than per thread, because a pool can use more threads
        // than it runs at once, and at most one workspace per window can then be in use.
        var workspaces = new ConcurrentBag<DiffWorkspace>();

        // Sorting is limited by memory bandwidth, so a job for every hardware thread sorts no faster than one per core
        // and holds twice the memory. Half the jobs is the closest to that the runtime can tell.
        var windowOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs / 2) };
        var gapOptions = new ParallelOptions { MaxDegreeOfParallelism = jobs };

        // Unbuffered, so that workers take the largest windows first rather than ranges of the list.
        Parallel.ForEach(Partitioner.Create(ordered, EnumerablePartitionerOptions.NoBuffering), windowOptions, job =>
        {
            var clock = Stopwatch.StartNew();
            var output = job.Output;
            bool diffable = output.Reference != null && job.GapBytes >= MinDiffWindow;
            if (!workspaces.TryTake(out var workspace))
                workspace = new DiffWorkspace(largest);
            if (diffable)
            {
                var reference = workspace.Reference.AsSpan(0, job.RefLength);
                ReadRange(output.Reference!.Full, job.RefOffset, reference);
                SuffixSorter.Sort(reference, workspace.Suffixes, workspace.Symbols);
                BsDiff.PrefixIndex(reference, workspace.Prefixes);
            }

            // A window's gaps share its read-only suffix array, and the windows with the most gap bytes take so much
            // longer than the rest that they would otherwise keep the whole build waiting on one core.
            long estimate = 0;
            Parallel.ForEach(job.Gaps, gapOptions, gap =>
            {
                var (slot, offset, length) = gap;
                byte[] newBytes = new byte[length];
                ReadRange(output.Target.Full, offset, newBytes);
                var segment = new PlannedSegment(Segment.Literal(length), offset, Estimate: Compressed(newBytes));
                if (diffable && length >= MinDiffGap)
                {
                    // raw patches are about as long as the new data (the diff stream is mostly zeros), so compare compressed sizes
                    var reference = workspace.Reference.AsSpan(0, job.RefLength);
                    byte[] patch = BsDiff.Diff(reference, workspace.Suffixes, workspace.Prefixes, newBytes);
                    long patchEstimate = Compressed(patch);
                    if (patchEstimate < segment.Estimate)
                        segment = SpoolDiff(output, offset, spool, reference, job.RefOffset, newBytes, patch, patchEstimate);
                }
                output.Segments[slot] = segment;
                Interlocked.Add(ref estimate, segment.Estimate);
            });
            workspaces.Add(workspace);

            int n = Interlocked.Increment(ref done);
            if (job.GapBytes >= 1 << 20 || n == ordered.Count)
                log.WriteLine($"  [{n}/{ordered.Count}] {output.Target.Relative}: {job.Gaps.Count} gaps, {job.GapBytes / 1048576.0:F1} MB -> "
                    + $"~{estimate / 1048576.0:F2} MB ({clock.Elapsed.TotalSeconds:F1}s)");
        });
    }

    /// <summary>One worker's buffers for a reference window of up to the given length.</summary>
    sealed class DiffWorkspace(int capacity)
    {
        public readonly byte[] Reference = GC.AllocateUninitializedArray<byte>(capacity);
        public readonly int[] Suffixes = GC.AllocateUninitializedArray<int>(capacity + 1);
        public readonly ushort[] Symbols = GC.AllocateUninitializedArray<ushort>(capacity + 1);
        public readonly int[] Prefixes = GC.AllocateUninitializedArray<int>(BsDiff.PrefixIndexLength);
    }

    static PlannedSegment SpoolDiff(PlannedOutput output, long offset, string spool, ReadOnlySpan<byte> reference, long refOffset,
        byte[] newBytes, byte[] patch, long estimate)
    {
        using (var check = new MemoryStream(newBytes.Length))
        {
            BsPatch.Apply(reference, patch, check);
            if (!check.GetBuffer().AsSpan(0, (int)check.Length).SequenceEqual(newBytes))
                throw new InvalidOperationException($"bsdiff round trip failed for {output.Target.Relative} at {offset}");
        }
        string spoolFile = Path.Combine(spool, $"{Guid.NewGuid():N}.bsdiff");
        File.WriteAllBytes(spoolFile, patch);
        var refs = new List<Extent> { new(-1, refOffset, reference.Length) };
        return new PlannedSegment(Segment.Diff(refs, newBytes.Length, patch.Length), offset, output.Reference, spoolFile, estimate);
    }

    /// <summary>Stand-alone zstd size at a fast level: decides diff vs literal and ranks the report.</summary>
    static long Compressed(ReadOnlySpan<byte> bytes)
    {
        using var compressor = new Compressor(3);
        return compressor.Wrap(bytes).Length;
    }

    static void ReadRange(string path, long offset, Span<byte> bytes)
    {
        using var handle = File.OpenHandle(path);
        for (int at = 0; at < bytes.Length;)
        {
            int n = RandomAccess.Read(handle, bytes[at..], offset + at);
            if (n == 0) throw new EndOfStreamException(path);
            at += n;
        }
    }

    /// <summary>A component's files in any source that the build does not ship, so in-place matches side-by-side.</summary>
    static List<string> RemovedStockFiles(List<SourceInstall> sources, List<FileEntry> targetFiles, int component)
    {
        var shipped = targetFiles.Select(t => t.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sources.SelectMany(s => s.Files)
            .Where(b => b.Component == component && !shipped.Contains(b.Relative))
            .Select(b => b.Relative)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static Recipe ToRecipe(PlannedOutput plan, Func<FileEntry, int> sourceIndex)
    {
        if (plan.Same != null) return new Recipe { Same = sourceIndex(plan.Same) };
        var recipe = new Recipe { Segments = [] };
        foreach (var planned in plan.Segments)
        {
            var segment = planned.Segment;
            if (segment.Kind == SegmentKind.Copy) segment.File = sourceIndex(planned.Source!);
            foreach (var extent in segment.Refs ?? []) extent.File = sourceIndex(planned.Source!);
            recipe.Segments.Add(segment);
        }
        return recipe;
    }

    /// <summary>
    /// Writes the sections (each source's recipes per component, blobs in output order), then the manifest. Sections
    /// of the first source come first; a recipe without blobs has no section.
    /// </summary>
    static void WriteFile(string path, PatchManifest manifest, List<PlannedRecipe> recipes, BuildOptions o, StringBuilder report)
    {
        var sectionNames = recipes.Where(r => r.Recipe.ArchiveBytes > 0).Select(r => r.SectionName).Distinct()
            .OrderBy(n => n.Contains('@') ? 1 : 0).ThenBy(n => recipes.FindIndex(r => r.SectionName == n)).ToList();

        using var writer = new PatchFileWriter(path, o.Level, o.Window, o.Jobs);
        byte[] buffer = new byte[1 << 20];
        foreach (string name in sectionNames)
        {
            var stream = writer.BeginSection(name);
            int index = manifest.Sections.Count;
            foreach (var planned in recipes.Where(r => r.SectionName == name && r.Recipe.ArchiveBytes > 0))
            {
                planned.Recipe.Section = index;
                planned.Recipe.Offset = stream.Position;
                WriteBlobs(planned.Plan, stream, buffer);
            }
            var section = writer.EndSection();
            manifest.Sections.Add(section);
            report.AppendLine($"section {name,-32} {section.Size / 1048576.0,10:F1} MB -> {section.Length / 1048576.0,8:F1} MB");
        }
        writer.Finish(manifest);
    }

    static void WriteBlobs(PlannedOutput plan, Stream archive, byte[] buffer)
    {
        using var target = plan.Segments.Any(s => s.Segment.Kind == SegmentKind.Literal) ? File.OpenRead(plan.Target.Full) : null;
        foreach (var planned in plan.Segments)
        {
            if (planned.Spool != null)
            {
                using var spool = File.OpenRead(planned.Spool);
                spool.CopyTo(archive);
            }
            else if (planned.Segment.Kind == SegmentKind.Literal)
            {
                target!.Position = planned.TargetOffset;
                for (long left = planned.Segment.Length; left > 0;)
                {
                    int n = target.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n == 0) throw new EndOfStreamException(plan.Target.Full);
                    archive.Write(buffer, 0, n);
                    left -= n;
                }
            }
        }
    }

    static void Report(StringBuilder report, string source, List<PlannedOutput> outputs)
    {
        static string Mb(long bytes) => $"{bytes / 1048576.0,10:F1} MB";
        var same = outputs.Where(p => p.Same != null).ToList();
        var segments = outputs.SelectMany(p => p.Segments).ToList();
        var copies = segments.Where(s => s.Segment.Kind == SegmentKind.Copy).ToList();
        var diffs = segments.Where(s => s.Segment.Kind == SegmentKind.BsDiff).ToList();
        var literals = segments.Where(s => s.Segment.Kind == SegmentKind.Literal).ToList();

        report.AppendLine($"== {source}: {outputs.Count} outputs planned");
        report.AppendLine($"  unchanged  {same.Count,7} files {Mb(same.Sum(p => p.Target.Size))}");
        report.AppendLine($"  copy       {copies.Count,7} runs  {Mb(copies.Sum(s => s.Segment.Length))}");
        report.AppendLine($"  bsdiff     {diffs.Count,7} runs  {Mb(diffs.Sum(s => s.Segment.Length))} -> ~{Mb(diffs.Sum(s => s.Estimate))}");
        report.AppendLine($"  literal    {literals.Count,7} runs  {Mb(literals.Sum(s => s.Segment.Length))} -> ~{Mb(literals.Sum(s => s.Estimate))}");
        report.AppendLine("  largest contributors (~compressed alone at level 3 / copied from stock / output size):");
        foreach (var plan in outputs.Where(p => p.Same == null).OrderByDescending(p => p.Segments.Sum(s => s.Estimate)).Take(40))
        {
            long bytes = plan.Segments.Sum(s => s.Estimate);
            long copied = plan.Segments.Where(s => s.Segment.Kind == SegmentKind.Copy).Sum(s => s.Segment.Length);
            string kind = plan.Reference == null ? "new" : "diff";
            report.AppendLine($"    {Mb(bytes)} / {Mb(copied)} / {Mb(plan.Target.Size)}  {kind,-4} {plan.Target.Relative}");
        }
    }
}
