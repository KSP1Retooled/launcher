using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace KSP.Retooled.Patch;

/// <summary>Where an apply or revert is: <see cref="Done"/> of <see cref="Total"/> (bytes or files) in <see cref="Stage"/>.</summary>
public readonly record struct PatchProgress(string Stage, long Done, long Total);

public sealed class ApplyOptions
{
    /// <summary>Write the retooled install here (unchanged files hard-linked) and leave the stock install alone; null patches in place.</summary>
    public string? OutputDirectory { get; init; }
    public TextWriter Log { get; init; } = TextWriter.Null;
    public IProgress<PatchProgress>? Progress { get; init; }
    public CancellationToken Cancellation { get; init; }
}

/// <summary>What <see cref="PatchApplier.Check"/> found: whether the install can be patched and which source it matches.</summary>
public sealed class InstallCheck
{
    /// <summary>The source whose files the chosen recipes read, or null for a mix of sources.</summary>
    public string? Variant { get; init; }
    public bool AlreadyPatched { get; init; }
    /// <summary>Stock files that are missing or modified, leaving some output with no usable recipe.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
    public IReadOnlyList<string> SkippedComponents { get; init; } = [];
    public bool CanApply => !AlreadyPatched && Problems.Count == 0;

    internal Recipe?[] Chosen = [];
    internal Dictionary<string, string?> Hashes = [];
}

/// <summary>
/// Applies a patch file to a stock install. Each output uses the first recipe whose stock files are all present with
/// the recorded content, and every such file is hash-checked before anything is written. An output the patch leaves
/// as stock reads nothing, so the install's copy stays as it is, whatever it holds, and may be missing. Side-by-side
/// writes the retooled install to a new directory. In-place builds every output in <c>.retooled/staging</c> first, then records
/// the plan in the <see cref="InstallJournal"/>, moves each stock file it replaces or removes into
/// <c>.retooled/original</c> and the new file into place, so <see cref="PatchReverter"/> can restore the stock install.
/// </summary>
public sealed class PatchApplier(PatchFile patch, string install)
{
    readonly string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(install));
    readonly PatchManifest manifest = patch.Manifest;

    public InstallCheck Check(ApplyOptions? options = null)
    {
        options ??= new ApplyOptions();
        var skipped = new List<string>();
        var applicable = new bool[manifest.Components.Count];
        for (int c = 0; c < applicable.Length; c++)
        {
            var component = manifest.Components[c];
            applicable[c] = component.Requires == null || Directory.Exists(Path.Combine(install, component.Requires));
            if (!applicable[c]) skipped.Add(component.Name);
        }
        if (InstallJournal.Exists(install))
            return new InstallCheck { AlreadyPatched = true, SkippedComponents = skipped, Problems = ["already patched; revert it first"] };

        var outputs = Enumerable.Range(0, manifest.Outputs.Count).Where(i => applicable[manifest.Outputs[i].Component]).ToList();
        var changed = outputs.Where(i => !IsUnchanged(manifest.Outputs[i]));
        var hashes = HashInputs(changed.SelectMany(i => manifest.Outputs[i].Recipes.SelectMany(r => r.Inputs())), options);
        bool Matches(int source) => hashes[manifest.Sources[source].Path] == manifest.Sources[source].Sha256;

        var chosen = new Recipe?[manifest.Outputs.Count];
        var problems = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (int i in outputs)
        {
            var recipes = manifest.Outputs[i].Recipes;
            if (IsUnchanged(manifest.Outputs[i]))
            {
                if (File.Exists(Path.Combine(install, manifest.Outputs[i].Path))) chosen[i] = recipes[0];
                continue;
            }
            chosen[i] = recipes.FirstOrDefault(r => r.Inputs().All(Matches));
            if (chosen[i] != null) continue;
            var closest = recipes.MinBy(r => r.Inputs().Distinct().Count(s => !Matches(s)));
            foreach (int s in closest?.Inputs().Where(s => !Matches(s)) ?? [])
            {
                string path = manifest.Sources[s].Path;
                problems.Add((File.Exists(Path.Combine(install, path)) ? "modified " : "missing  ") + path);
            }
        }

        var used = outputs.SelectMany(i => chosen[i]?.Inputs() ?? []).ToHashSet();
        string? variant = manifest.Variants.FirstOrDefault(v => used.IsSubsetOf(v.Files))?.Id;
        return new InstallCheck { Variant = variant, Problems = [.. problems], SkippedComponents = skipped, Chosen = chosen, Hashes = hashes };
    }

    /// <summary>
    /// Whether every source install has this output, as stock at its own path. Such an output has that one recipe (a
    /// source without the file would have added another), so its source is in every variant and never tells them apart.
    /// </summary>
    bool IsUnchanged(OutputFile output) =>
        output.Recipes is [{ Same: int same }] && manifest.Sources[same].Path == output.Path;

    /// <summary>Hashes the install's copy of every listed source path, skipping files whose size matches no source there.</summary>
    Dictionary<string, string?> HashInputs(IEnumerable<int> sources, ApplyOptions options)
    {
        var sizes = sources.Distinct().Select(s => manifest.Sources[s])
            .GroupBy(s => s.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Size).ToHashSet(), StringComparer.Ordinal);
        var hashes = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        var work = new List<(string Path, long Size)>();
        foreach (var (path, candidates) in sizes)
        {
            var info = new FileInfo(Path.Combine(install, path));
            if (info.Exists && candidates.Contains(info.Length)) work.Add((path, info.Length));
            else hashes[path] = null;
        }

        long total = work.Sum(w => w.Size), done = 0;
        Parallel.ForEach(work.OrderByDescending(w => w.Size),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = options.Cancellation }, w =>
            {
                hashes[w.Path] = Sha256Hex(Path.Combine(install, w.Path));
                options.Progress?.Report(new PatchProgress("verify", Interlocked.Add(ref done, w.Size), total));
            });
        options.Log.WriteLine($"hashed {work.Count} stock files ({total / 1048576.0:F0} MB)");
        return new Dictionary<string, string?>(hashes, StringComparer.Ordinal);
    }

    public InstallCheck Apply(ApplyOptions options)
    {
        var check = Check(options);
        if (check.AlreadyPatched) throw new PatchException($"{install} is already patched; revert it first");
        if (check.Problems.Count > 0)
            throw new PatchException(
                $"{install} does not match any stock KSP install this patch was built from ({PatchFile.Describe(manifest)}); "
                + $"{check.Problems.Count} files differ:\n  " + string.Join("\n  ", check.Problems));
        foreach (string name in check.SkippedComponents) options.Log.WriteLine($"skip {name}: not installed");
        options.Log.WriteLine($"install matches {check.Variant ?? "a mix of the known sources"}");

        bool inPlace = options.OutputDirectory == null;
        // read before the build's buildID file replaces the stock one
        string? language = inPlace ? BuildIdFile.Find(install, "language") : null;
        string root = inPlace ? Path.Combine(install, InstallJournal.StagingDirectory) : PrepareOutput(options.OutputDirectory!);
        if (inPlace && Directory.Exists(root)) Directory.Delete(root, recursive: true);

        var staged = WriteOutputs(check, root, inPlace, options);
        var components = manifest.Components.Where(c => !check.SkippedComponents.Contains(c.Name)).ToList();
        if (inPlace)
        {
            var journal = new InstallJournal
            {
                Platform = manifest.Platform,
                Variant = check.Variant,
                Patch = Path.GetFileName(patch.FilePath),
                Applied = DateTime.UtcNow,
            };
            new InstallCommit(install, journal, check.Hashes, options.Log).Run(root, staged,
                components.SelectMany(c => c.Remove), components.SelectMany(c => c.Directories));
            if (language != null && GameSettingsFile.SetLanguage(install, language))
                options.Log.WriteLine($"set LANGUAGE = {language} in {GameSettingsFile.FileName}");
        }
        else
        {
            foreach (string directory in components.SelectMany(c => c.Directories)) Directory.CreateDirectory(Path.Combine(root, directory));
        }
        options.Log.WriteLine(inPlace ? $"patched {install} in place" : $"wrote {root}");
        return check;
    }

    string PrepareOutput(string directory)
    {
        string full = Path.GetFullPath(directory);
        string relative = Path.GetRelativePath(install, full);
        if (relative == "." || !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw new PatchException($"output {full} must be outside the install {install}");
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
            throw new PatchException($"output {full} is not empty");
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Writes every chosen output under <paramref name="root"/>; returns the staged outputs (in-place skips unchanged same-path files).</summary>
    List<OutputFile> WriteOutputs(InstallCheck check, string root, bool inPlace, ApplyOptions options)
    {
        var handles = new Dictionary<int, SafeFileHandle>();
        var sections = new Dictionary<int, SectionReader>();
        try
        {
            SafeFileHandle Handle(int source)
            {
                if (!handles.TryGetValue(source, out var handle))
                    handles[source] = handle = File.OpenHandle(Path.Combine(install, manifest.Sources[source].Path));
                return handle;
            }

            var staged = new List<OutputFile>();
            int linked = 0, written = 0;
            long total = manifest.Outputs.Where((o, i) => check.Chosen[i] != null).Sum(o => o.Size), done = 0;
            for (int i = 0; i < manifest.Outputs.Count; i++)
            {
                if (check.Chosen[i] is not Recipe recipe) continue;
                options.Cancellation.ThrowIfCancellationRequested();
                var output = manifest.Outputs[i];
                if (recipe.Same is int same)
                {
                    string source = manifest.Sources[same].Path;
                    if (!(inPlace && source == output.Path))
                    {
                        string destination = Destination(root, output.Path);
                        if (inPlace) File.Copy(Path.Combine(install, source), destination);
                        else LinkOrCopy(Path.Combine(install, source), destination);
                        if (inPlace) staged.Add(output);
                    }
                    linked++;
                }
                else
                {
                    SectionReader? section = null;
                    if (recipe.Section is int index && !sections.TryGetValue(index, out section))
                        sections[index] = section = patch.OpenSection(index);
                    section?.SkipTo(recipe.Offset ?? 0);
                    string destination = Destination(root, output.Path);
                    using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                    using (var hashing = new HashingStream(file))
                    {
                        foreach (var segment in recipe.Segments ?? [])
                            WriteSegment(segment, section, Handle, hashing);
                        if (hashing.Length != output.Size || Convert.ToHexStringLower(hashing.Hash()) != output.Sha256)
                            throw new PatchException($"{output.Path}: patched content does not match the manifest hash");
                    }
                    if (output.Executable) MarkExecutable(destination);
                    if (inPlace) staged.Add(output);
                    written++;
                }
                options.Progress?.Report(new PatchProgress("write", done += output.Size, total));
            }
            options.Log.WriteLine($"{written} files patched, {linked} unchanged");
            return staged;
        }
        finally
        {
            foreach (var handle in handles.Values) handle.Dispose();
            foreach (var section in sections.Values) section.Dispose();
        }
    }

    static void WriteSegment(Segment segment, SectionReader? section, Func<int, SafeFileHandle> handle, Stream output)
    {
        if (segment.ArchiveBytes > 0 && section == null) throw new InvalidDataException("a recipe with blobs has no section");
        switch (segment.Kind)
        {
            case SegmentKind.Copy:
                CopyRange(handle(segment.File!.Value), segment.Offset!.Value, segment.Length, output);
                break;
            case SegmentKind.Literal:
                byte[] buffer = new byte[(int)Math.Min(segment.Length, 1 << 20)];
                for (long left = segment.Length; left > 0;)
                {
                    int n = (int)Math.Min(buffer.Length, left);
                    section!.Read(buffer.AsSpan(0, n));
                    output.Write(buffer, 0, n);
                    left -= n;
                }
                break;
            case SegmentKind.BsDiff:
                var reference = new byte[segment.Refs!.Sum(r => r.Length)];
                int at = 0;
                foreach (var extent in segment.Refs!)
                {
                    RandomAccess.Read(handle(extent.File), reference.AsSpan(at, (int)extent.Length), extent.Offset);
                    at += (int)extent.Length;
                }
                var patch = new byte[segment.Blob!.Value];
                section!.Read(patch);
                long before = output.Position;
                BsPatch.Apply(reference, patch, output);
                if (output.Position - before != segment.Length)
                    throw new PatchException($"bsdiff segment produced {output.Position - before} bytes, expected {segment.Length}");
                break;
            default:
                throw new InvalidDataException($"unknown segment kind {segment.Kind}");
        }
    }

    static string Destination(string root, string path)
    {
        string full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    static void CopyRange(SafeFileHandle source, long offset, long length, Stream output)
    {
        byte[] buffer = new byte[(int)Math.Min(length, 1 << 20)];
        while (length > 0)
        {
            int n = RandomAccess.Read(source, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length)), offset);
            if (n == 0) throw new EndOfStreamException("stock file is shorter than its manifest entry");
            output.Write(buffer, 0, n);
            offset += n;
            length -= n;
        }
    }

    static void LinkOrCopy(string source, string destination)
    {
        try
        {
            if (OperatingSystem.IsWindows() ? CreateHardLink(destination, source, IntPtr.Zero) : link(source, destination) == 0) return;
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
        File.Copy(source, destination);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true)]
    static extern int link(string existing, string link);

    static void MarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path)
            | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    public static string Sha256Hex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

public sealed class PatchException(string message) : Exception(message);

/// <summary>Write-only stream that hashes everything written through it.</summary>
sealed class HashingStream(Stream inner) : Stream
{
    readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    long length;

    public byte[] Hash() => hash.GetHashAndReset();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        hash.AppendData(buffer);
        inner.Write(buffer);
        length += buffer.Length;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => length;
    public override long Position { get => length; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) hash.Dispose();
        base.Dispose(disposing);
    }
}
