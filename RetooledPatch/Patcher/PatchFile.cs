using System.Buffers.Binary;
using KSP.Retooled.Patch.IO;
using KSP.Retooled.Patch.Models.Manifest;
using ZstdSharp;

namespace KSP.Retooled.Patch.Patcher;

/// <summary>
/// A patch file (<c>retooled-&lt;platform&gt;.rtpatch</c>): <c>RTPATCH2</c>, the manifest's offset and length (uint64
/// LE
/// each), then the sections, each an independent zstd frame of raw blobs, then the manifest JSON as one zstd frame.
/// An apply decompresses only the sections its recipes read, skipping forward within them past blobs other installs
/// need.
/// </summary>
public sealed class PatchFile
{
    public const string EXTENSION = ".rtpatch";
    internal const int HEADER_SIZE = 24;

    /// <summary>
    /// Largest window a reader accepts (2 GB); the writer's window must not exceed it.
    /// </summary>
    internal const int MAX_WINDOW_LOG = 31;
    internal static readonly byte[] Magic = [.."RTPATCH2"u8];

    private PatchFile(string path, PatchManifest manifest)
    {
        this.FilePath = path;
        this.Manifest = manifest;
    }

    public string FilePath { get; }

    public PatchManifest Manifest { get; }

    public static PatchFile Open(string path)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[HEADER_SIZE];
        if (file.Length < HEADER_SIZE || file.ReadAtLeast(header, HEADER_SIZE, false) < HEADER_SIZE
                                      || !header[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException($"{path} is not a retooled patch file (or was made by an older patch tool)");
        }

        long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        long length = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        if (offset < HEADER_SIZE || length <= 0 || offset + length > file.Length)
        {
            throw new InvalidDataException($"{path}: truncated patch file");
        }

        byte[] compressed = new byte[length];
        file.Position = offset;
        file.ReadExactly(compressed);
        using Decompressor decompressor = new();
        PatchManifest manifest = PatchManifest.FromJson(decompressor.Unwrap(compressed));
        if (manifest.Version is < PatchManifest.OLDEST_VERSION or > PatchManifest.CURRENT_VERSION)
        {
            throw new InvalidDataException($"{path}: manifest version {manifest.Version}, this tool reads "
                                         + $"{PatchManifest.OLDEST_VERSION} to {PatchManifest.CURRENT_VERSION}");
        }

        Section? section = manifest.Sections.FirstOrDefault(section => section.Offset < HEADER_SIZE || section.Offset + section.Length > offset);
        return section is null
                   ? new PatchFile(Path.GetFullPath(path), manifest)
                   : throw new InvalidDataException($"{path}: section {section.Name} lies outside the file");
    }

    /// <summary>
    /// A forward-only reader of one section's decompressed blobs.
    /// </summary>
    public SectionReader OpenSection(int index) => new(this.FilePath, this.Manifest.Sections[index]);

    public static string Describe(PatchManifest m) => $"{m.Platform} patch: {m.Outputs.Count} outputs, {m.Sources.Count} stock files, "
                                                    + $"sources {string.Join(", ", m.Variants.Select(v => v.Id))}";
}
