using System.Collections.Concurrent;
using System.Security.Cryptography;
using KSP.Retooled.Patch.Exceptions;
using KSP.Retooled.Patch.Install;
using KSP.Retooled.Patch.IO;
using KSP.Retooled.Patch.Models.Journal;
using KSP.Retooled.Patch.Models.Manifest;
using KSP.Retooled.Patch.Utils;
using Microsoft.Win32.SafeHandles;

namespace KSP.Retooled.Patch.Patcher;

/// <summary>
/// Applies a patch file to a stock install. Each output uses the first recipe whose stock files are all present with
/// the recorded content, and every such file is hash-checked before anything is written. An output the patch leaves
/// as stock reads nothing, so the install's copy stays as it is, whatever it holds, and may be missing. Side-by-side
/// writes the retooled install to a new directory. In-place builds every output in <c>.retooled/staging</c> first,
/// then records
/// the plan in the <see cref="InstallJournal"/>, moves each stock file it replaces or removes into
/// <c>.retooled/original</c> and the new file into place, so <see cref="PatchReverter"/> can restore the stock
/// install.
/// An in-place apply that throws leaves the install as it found it, unless undoing it failed too: the journal then
/// stays, for a revert to finish.
/// </summary>
public sealed class PatchApplier(PatchFile patch, string install)
{
    private readonly string install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(install));
    private readonly PatchManifest manifest = patch.Manifest;

    // ReSharper disable once CognitiveComplexity
    public InstallCheck Check(ApplyOptions? options = null)
    {
        options ??= new ApplyOptions();
        List<string> skipped = [];
        bool[] applicable = new bool[this.manifest.Components.Count];
        for (int c = 0; c < applicable.Length; c++)
        {
            Component component = this.manifest.Components[c];
            applicable[c] = component.Requires is null || Directory.Exists(Path.Combine(this.install, component.Requires));
            if (!applicable[c]) skipped.Add(component.Name);
        }

        if (InstallJournal.Exists(this.install))
        {
            return new InstallCheck { AlreadyPatched = true, SkippedComponents = skipped, Problems = ["already patched; revert it first"] };
        }

        List<int> outputs = [..Enumerable.Range(0, this.manifest.Outputs.Count).Where(i => applicable[this.manifest.Outputs[i].Component])];
        IEnumerable<int> changed = outputs.Where(i => !IsUnchanged(this.manifest.Outputs[i]));
        Dictionary<string, string?> hashes = HashInputs(changed.SelectMany(i => this.manifest.Outputs[i].Recipes.SelectMany(r => r.Inputs())), options);

        bool Matches(int source) => hashes[this.manifest.Sources[source].Path] == this.manifest.Sources[source].Sha256;

        Recipe?[] chosen = new Recipe?[this.manifest.Outputs.Count];
        SortedSet<string> problems = new(StringComparer.OrdinalIgnoreCase);
        foreach (int i in outputs)
        {
            List<Recipe> recipes = this.manifest.Outputs[i].Recipes;
            if (IsUnchanged(this.manifest.Outputs[i]))
            {
                if (File.Exists(Path.Combine(this.install, this.manifest.Outputs[i].Path))) chosen[i] = recipes[0];
                continue;
            }

            chosen[i] = recipes.FirstOrDefault(r => r.Inputs().All(Matches));
            if (chosen[i] is not null) continue;

            Recipe? closest = recipes.MinBy(r => r.Inputs().Distinct().Count(s => !Matches(s)));
            foreach (int s in closest?.Inputs().Where(s => !Matches(s)) ?? [])
            {
                string path = this.manifest.Sources[s].Path;
                problems.Add((File.Exists(Path.Combine(this.install, path)) ? "modified " : "missing  ") + path);
            }
        }

        HashSet<int> used = [..outputs.SelectMany(i => chosen[i]?.Inputs() ?? [])];
        string? variant = this.manifest.Variants.FirstOrDefault(v => used.IsSubsetOf(v.Files))?.Id;
        return new InstallCheck { Variant = variant, Problems = [..problems], SkippedComponents = skipped, chosen = chosen, hashes = hashes };
    }

    /// <summary>
    /// Whether every source install has this output, as stock at its own path. Such an output has that one recipe (a
    /// source without the file would have added another), so its source is in every variant and never tells them apart.
    /// </summary>
    private bool IsUnchanged(OutputFile output) => output.Recipes is [{ Same: { } same }]
                                                && this.manifest.Sources[same].Path == output.Path;

    /// <summary>
    /// Hashes the install's copy of every listed source path, skipping files whose size matches no source there.
    /// </summary>
    private Dictionary<string, string?> HashInputs(IEnumerable<int> sources, ApplyOptions options)
    {
        Dictionary<string, HashSet<long>> sizes = sources.Distinct()
                                                         .Select(s => this.manifest.Sources[s])
                                                         .GroupBy(s => s.Path, StringComparer.Ordinal)
                                                         .ToDictionary(g => g.Key, g => g.Select(s => s.Size).ToHashSet(), StringComparer.Ordinal);

        ConcurrentDictionary<string, string?> hashes = new(StringComparer.Ordinal);
        List<(string Path, long Size)> work = [];
        foreach ((string path, HashSet<long> candidates) in sizes)
        {
            FileInfo info = new(Path.Combine(this.install, path));
            if (info.Exists && candidates.Contains(info.Length))
            {
                work.Add((path, info.Length));
            }
            else
            {
                hashes[path] = null;
            }
        }

        long total = work.Sum(w => w.Size), done = 0;
        Parallel.ForEach(work.OrderByDescending(w => w.Size),
                         new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = options.Cancellation }, w =>
                         {
                             hashes[w.Path] = Sha256Hex(Path.Combine(this.install, w.Path));
                             options.Progress?.Report(new PatchProgress("verify", Interlocked.Add(ref done, w.Size), total));
                         });

        options.Log.WriteLine($"hashed {work.Count} stock files ({total / 1048576.0:F0} MB)");
        return new Dictionary<string, string?>(hashes, StringComparer.Ordinal);
    }

    // ReSharper disable once CognitiveComplexity
    public InstallCheck Apply(ApplyOptions options)
    {
        InstallCheck check = Check(options);
        if (check.AlreadyPatched) throw new PatchException($"{this.install} is already patched; revert it first");

        if (check.Problems.Count > 0)
        {
            throw new PatchException($"{this.install} does not match any stock KSP install this patch was built from ({PatchFile.Describe(this.manifest)}); "
                                   + $"{check.Problems.Count} files differ:\n  " + string.Join("\n  ", check.Problems));
        }

        foreach (string name in check.SkippedComponents) options.Log.WriteLine($"skip {name}: not installed");
        options.Log.WriteLine($"install matches {check.Variant ?? "a mix of the known sources"}");

        bool inPlace = options.OutputDirectory is null;
        // read before the build's buildID file replaces the stock one
        string? language = inPlace ? BuildIdFile.Find(this.install, "language") : null;
        string root = inPlace ? Path.Combine(this.install, InstallJournal.STAGING_DIRECTORY) : PrepareOutput(options.OutputDirectory!);
        // there's no journal, or Check would have refused, so whatever an earlier apply left here is scratch
        string scratch = Path.Combine(this.install, InstallJournal.DIRECTORY);
        if (inPlace && Directory.Exists(scratch)) Directory.Delete(scratch, true);

        List<OutputFile> staged;
        try
        {
            staged = WriteOutputs(check, root, inPlace, options);
        }
        catch when (inPlace)
        {
            InstallJournal.DeleteScratch(this.install);
            throw;
        }

        List<Component> components = [..this.manifest.Components.Where(c => !check.SkippedComponents.Contains(c.Name))];
        if (inPlace)
        {
            InstallJournal journal = new()
            {
                Platform = this.manifest.Platform,
                Variant  = check.Variant,
                Patch    = Path.GetFileName(patch.FilePath),
                Applied  = DateTime.UtcNow
            };

            new InstallCommit(this.install, journal, check.hashes, options.Log, options.BeforeMove).Run(root, staged,
                                                                                                        components.SelectMany(c => c.Remove), components.SelectMany(c => c.Directories), language);
        }
        else
        {
            foreach (string directory in components.SelectMany(c => c.Directories))
            {
                Directory.CreateDirectory(Path.Combine(root, directory));
            }

            foreach (string path in this.manifest.CopiedPaths)
            {
                CopyFromInstall(path, root);
            }
        }

        options.Log.WriteLine(inPlace ? $"patched {this.install} in place" : $"wrote {root}");
        return check;
    }

    private string PrepareOutput(string directory)
    {
        string full = Path.GetFullPath(directory);
        string relative = Path.GetRelativePath(this.install, full);
        if (relative is "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative)))
        {
            throw new PatchException($"output {full} must be outside the install {this.install}");
        }

        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            throw new PatchException($"output {full} is not empty");
        }

        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>
    /// Links the install's file or directory, whatever it holds, into <paramref name="root"/>, if it has one.
    /// </summary>
    private void CopyFromInstall(string path, string root)
    {
        string from = Path.Combine(this.install, path);
        if (File.Exists(from)) FileClone.LinkOrCopy(from, Destination(root, path));
        if (!Directory.Exists(from)) return;

        Directory.CreateDirectory(Path.Combine(root, path));
        foreach (string sub in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(root, Path.GetRelativePath(this.install, sub)));
        }

        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            FileClone.LinkOrCopy(file, Path.Combine(root, Path.GetRelativePath(this.install, file)));
        }
    }

    /// <summary>
    /// Writes every chosen output under <paramref name="root"/>; returns the staged outputs (in-place skips unchanged
    /// same-path files).
    /// </summary>
    // ReSharper disable once CognitiveComplexity
    private List<OutputFile> WriteOutputs(InstallCheck check, string root, bool inPlace, ApplyOptions options)
    {
        Dictionary<int, SafeFileHandle> handles = new();
        Dictionary<int, SectionReader> sections = new();
        try
        {
            SafeFileHandle Handle(int source)
            {
                if (!handles.TryGetValue(source, out SafeFileHandle? handle))
                {
                    handles[source] = handle = File.OpenHandle(Path.Combine(this.install, this.manifest.Sources[source].Path));
                }

                return handle;
            }

            List<OutputFile> staged = [];
            int linked = 0, written = 0;
            long total = this.manifest.Outputs.Where((_, i) => check.chosen[i] is not null).Sum(o => o.Size), done = 0;
            for (int i = 0; i < this.manifest.Outputs.Count; i++)
            {
                if (check.chosen[i] is not { } recipe) continue;

                options.Cancellation.ThrowIfCancellationRequested();
                OutputFile output = this.manifest.Outputs[i];
                if (recipe.Same is { } same)
                {
                    string source = this.manifest.Sources[same].Path;
                    if (!(inPlace && source == output.Path))
                    {
                        string destination = Destination(root, output.Path);
                        if (inPlace)
                        {
                            File.Copy(Path.Combine(this.install, source), destination);
                        }
                        else
                        {
                            FileClone.LinkOrCopy(Path.Combine(this.install, source), destination);
                        }

                        if (inPlace) staged.Add(output);
                    }

                    linked++;
                }
                else
                {
                    SectionReader? section = null;
                    if (recipe.Section is { } index && !sections.TryGetValue(index, out section))
                    {
                        sections[index] = section = patch.OpenSection(index);
                    }

                    section?.SkipTo(recipe.Offset ?? 0);
                    string destination = Destination(root, output.Path);
                    using (FileStream file = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                    using (HashingStream hashing = new(file))
                    {
                        foreach (Segment segment in recipe.Segments ?? [])
                        {
                            WriteSegment(segment, section, Handle, hashing);
                        }

                        if (hashing.Length != output.Size || Convert.ToHexStringLower(hashing.Hash()) != output.Sha256)
                        {
                            throw new PatchException($"{output.Path}: patched content does not match the manifest hash");
                        }
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
            foreach (SafeFileHandle handle in handles.Values) handle.Dispose();
            foreach (SectionReader section in sections.Values) section.Dispose();
        }
    }

    private static void WriteSegment(Segment segment, SectionReader? section, Func<int, SafeFileHandle> handle, Stream output)
    {
        if (segment.ArchiveBytes > 0 && section is null) throw new InvalidDataException("a recipe with blobs has no section");

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
                byte[] reference = new byte[segment.Refs!.Sum(r => r.Length)];
                int at = 0;
                foreach (Extent extent in segment.Refs!)
                {
                    RandomAccess.Read(handle(extent.File), reference.AsSpan(at, (int)extent.Length), extent.Offset);
                    at += (int)extent.Length;
                }

                byte[] patch = new byte[segment.Blob!.Value];
                section!.Read(patch);
                long before = output.Position;
                BsPatch.Apply(reference, patch, output);
                if (output.Position - before != segment.Length)
                {
                    throw new PatchException($"bsdiff segment produced {output.Position - before} bytes, expected {segment.Length}");
                }

                break;

            default:
                throw new InvalidDataException($"unknown segment kind {segment.Kind}");
        }
    }

    private static string Destination(string root, string path)
    {
        string full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    private static void CopyRange(SafeFileHandle source, long offset, long length, Stream output)
    {
        byte[] buffer = new byte[(int)Math.Min(length, 1 << 20)];
        while (length > 0)
        {
            int n = RandomAccess.Read(source, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length)), offset);
            if (n is 0) throw new EndOfStreamException("stock file is shorter than its manifest entry");

            output.Write(buffer, 0, n);
            offset += n;
            length -= n;
        }
    }

    private static void MarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                                 | UnixFileMode.UserExecute
                                 | UnixFileMode.GroupExecute
                                 | UnixFileMode.OtherExecute);
    }

    public static string Sha256Hex(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
