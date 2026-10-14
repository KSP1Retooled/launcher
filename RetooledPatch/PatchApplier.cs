using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace KSP.Retooled.Patch;

/// <summary>
/// Applies patch archives to a stock install. Every base file is hash-checked before anything is written. Side-by-side
/// writes the retooled install to a new directory (unchanged files hard-linked). In-place builds every output in a
/// staging directory inside the install first, then moves them over the stock files and deletes the stock files the
/// manifest lists in <see cref="PatchManifest.Remove"/>, so a failed apply never leaves a half-patched install.
/// </summary>
public sealed class PatchApplier(string install, TextWriter log)
{
    public const string StagingDirectory = ".retooled-staging";

    readonly string install = Path.GetFullPath(install);

    public void Apply(IReadOnlyList<string> archives, string? outputDirectory)
    {
        var components = new List<(PatchManifest Manifest, Stream Blobs)>();
        try
        {
            foreach (string archive in archives)
            {
                var (manifest, blobs) = PatchArchive.OpenRead(archive);
                if (manifest.Requires != null && !Directory.Exists(Path.Combine(install, manifest.Requires)))
                {
                    log.WriteLine($"skip {manifest.Component}: {manifest.Requires} is not installed");
                    blobs.Dispose();
                    continue;
                }
                log.WriteLine("apply " + PatchArchive.Describe(manifest));
                components.Add((manifest, blobs));
            }
            if (components.Count == 0) throw new PatchException("no applicable patch components");

            VerifyBase(components.Select(c => c.Manifest));

            bool inPlace = outputDirectory == null;
            string root = inPlace ? Path.Combine(install, StagingDirectory) : PrepareOutput(outputDirectory!);
            if (inPlace && Directory.Exists(root)) Directory.Delete(root, recursive: true);

            var staged = new List<string>();
            foreach (var (manifest, blobs) in components)
                WriteOutputs(manifest, blobs, root, inPlace, staged);

            if (inPlace) Commit(root, staged, components.SelectMany(c => c.Manifest.Remove));
            log.WriteLine(inPlace ? $"patched {install} in place" : $"wrote {root}");
        }
        finally
        {
            foreach (var (_, blobs) in components) blobs.Dispose();
        }
    }

    void VerifyBase(IEnumerable<PatchManifest> manifests)
    {
        var expected = new Dictionary<string, BaseFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifests.SelectMany(m => m.Base))
        {
            if (expected.TryGetValue(file.Path, out var other) && other.Sha256 != file.Sha256)
                throw new PatchException($"patch components disagree about base file {file.Path}");
            expected[file.Path] = file;
        }

        var problems = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(expected.Values, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
        {
            string path = Path.Combine(install, file.Path);
            if (!File.Exists(path)) problems.Add($"missing  {file.Path}");
            else if (new FileInfo(path).Length != file.Size || Sha256Hex(path) != file.Sha256) problems.Add($"modified {file.Path}");
        });
        if (!problems.IsEmpty)
            throw new PatchException(
                $"{install} does not match the stock KSP 1.12.5 files this patch needs ({problems.Count} files):\n  "
                + string.Join("\n  ", problems.Order(StringComparer.OrdinalIgnoreCase)));
        log.WriteLine($"verified {expected.Count} base files");
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

    void WriteOutputs(PatchManifest manifest, Stream blobs, string root, bool inPlace, List<string> staged)
    {
        var handles = new Dictionary<int, SafeFileHandle>();
        try
        {
            SafeFileHandle Handle(int index)
            {
                if (!handles.TryGetValue(index, out var handle))
                    handles[index] = handle = File.OpenHandle(Path.Combine(install, manifest.Base[index].Path));
                return handle;
            }

            int linked = 0, written = 0;
            foreach (var output in manifest.Outputs)
            {
                if (output.Same is int same)
                {
                    string source = manifest.Base[same].Path;
                    if (inPlace && string.Equals(source, output.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    string destination = Destination(root, output.Path);
                    if (inPlace) File.Copy(Path.Combine(install, source), destination);
                    else LinkOrCopy(Path.Combine(install, source), destination);
                    if (inPlace) staged.Add(output.Path);
                    linked++;
                    continue;
                }

                using (var file = new FileStream(Destination(root, output.Path), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                using (var hashing = new HashingStream(file))
                {
                    foreach (var segment in output.Segments ?? [])
                        WriteSegment(segment, manifest, blobs, Handle, hashing);
                    if (hashing.Length != output.Size || Convert.ToHexStringLower(hashing.Hash()) != output.Sha256)
                        throw new PatchException($"{output.Path}: patched content does not match the manifest hash");
                }
                if (inPlace) staged.Add(output.Path);
                written++;
            }
            log.WriteLine($"  {manifest.Component}: {written} files patched, {linked} unchanged");
        }
        finally
        {
            foreach (var handle in handles.Values) handle.Dispose();
        }
    }

    static void WriteSegment(Segment segment, PatchManifest manifest, Stream blobs, Func<int, SafeFileHandle> handle, Stream output)
    {
        switch (segment.Kind)
        {
            case SegmentKind.Copy:
                CopyRange(handle(segment.File!.Value), segment.Offset!.Value, segment.Length, output);
                break;
            case SegmentKind.Literal:
                CopyStream(blobs, segment.Length, output);
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
                blobs.ReadExactly(patch);
                long before = output.Position;
                BsPatch.Apply(reference, patch, output);
                if (output.Position - before != segment.Length)
                    throw new PatchException($"bsdiff segment produced {output.Position - before} bytes, expected {segment.Length}");
                break;
            default:
                throw new InvalidDataException($"unknown segment kind {segment.Kind} in {manifest.Component}");
        }
    }

    void Commit(string staging, List<string> staged, IEnumerable<string> remove)
    {
        foreach (string path in staged)
        {
            string destination = Path.Combine(install, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(Path.Combine(staging, path), destination, overwrite: true);
        }
        int removed = 0;
        foreach (string path in remove)
        {
            string full = Path.Combine(install, path);
            if (!File.Exists(full)) continue;
            File.Delete(full);
            removed++;
            for (string? dir = Path.GetDirectoryName(full); dir != null && dir.Length > install.Length
                 && !Directory.EnumerateFileSystemEntries(dir).Any(); dir = Path.GetDirectoryName(dir))
                Directory.Delete(dir);
        }
        Directory.Delete(staging, recursive: true);
        log.WriteLine($"moved {staged.Count} files into the install, removed {removed} stock files");
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
            if (n == 0) throw new EndOfStreamException("base file is shorter than its manifest entry");
            output.Write(buffer, 0, n);
            offset += n;
            length -= n;
        }
    }

    static void CopyStream(Stream source, long length, Stream output)
    {
        byte[] buffer = new byte[(int)Math.Min(length, 1 << 20)];
        while (length > 0)
        {
            int n = (int)Math.Min(buffer.Length, length);
            source.ReadExactly(buffer, 0, n);
            output.Write(buffer, 0, n);
            length -= n;
        }
    }

    static void LinkOrCopy(string source, string destination)
    {
        if (OperatingSystem.IsWindows() && CreateHardLink(destination, source, IntPtr.Zero)) return;
        File.Copy(source, destination);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

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
