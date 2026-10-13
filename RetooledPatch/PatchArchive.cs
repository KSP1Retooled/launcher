using System.Buffers.Binary;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace KSP.Retooled.Patch;

/// <summary>
/// A component's patch archive: one zstd stream holding <c>RTPATCH1</c>, the manifest length (int32 LE), the manifest
/// JSON, then every blob in manifest order (outputs in order, segments in order). Blobs are raw; the single stream is
/// the only compression, so redundancy across blobs is shared.
/// </summary>
public static class PatchArchive
{
    public const string Extension = ".rtpatch";
    static readonly byte[] Magic = "RTPATCH1"u8.ToArray();

    /// <summary>Largest window a reader accepts (2 GB); the writer's window must not exceed it.</summary>
    const int MaxWindowLog = 31;

    public static Stream OpenWrite(string path, int level, int windowLog, int workers, PatchManifest manifest)
    {
        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var zstd = new CompressionStream(file, level, 1 << 20, leaveOpen: false);
        zstd.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
        zstd.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, windowLog);
        if (workers > 1) zstd.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, workers);

        byte[] json = manifest.ToJson();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, json.Length);
        zstd.Write(Magic);
        zstd.Write(length);
        zstd.Write(json);
        return zstd;
    }

    /// <summary>Opens an archive and reads its manifest; the returned stream is positioned at the first blob.</summary>
    public static (PatchManifest Manifest, Stream Blobs) OpenRead(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var zstd = new DecompressionStream(file, 1 << 20, checkEndOfStream: true, leaveOpen: false);
        zstd.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, MaxWindowLog);
        try
        {
            Span<byte> header = stackalloc byte[12];
            zstd.ReadExactly(header);
            if (!header[..8].SequenceEqual(Magic))
                throw new InvalidDataException($"{path} is not a retooled patch archive");
            int length = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            byte[] json = new byte[length];
            zstd.ReadExactly(json);
            var manifest = PatchManifest.FromJson(json);
            if (manifest.Version != PatchManifest.CurrentVersion)
                throw new InvalidDataException($"{path}: manifest version {manifest.Version}, this tool reads {PatchManifest.CurrentVersion}");
            return (manifest, zstd);
        }
        catch
        {
            zstd.Dispose();
            throw;
        }
    }

    public static string Describe(PatchManifest m) =>
        $"{m.Component}{(m.Requires == null ? "" : $" (requires {m.Requires})")}: {m.Outputs.Count} outputs, {m.Base.Count} base files";
}
